namespace Gateway.Core.Bridge;

public enum WifiEventKind { Roam, Disconnect }

/// <summary>Wi-Fi 끊김·AP 이동(로밍) 한 건. Tag = 그때 호기가 마지막으로 읽은 바닥 RFID 태그(위치 대용, 없으면 "")</summary>
public sealed record WifiEvent(DateTime At, int Agv, WifiEventKind Kind, string FromBssid, string ToBssid, int Channel, int Rssi, string Tag);

/// <summary>
/// 호기별 heartbeat 를 이어서 보며 Wi-Fi 사건을 가려낸다 (브리지 변경 없이 서버에서).
///  - 끊김: 브리지의 disc 카운터가 늘었다.
///  - 로밍: BSSID 가 바뀌었다 (끊김 없이). 끊김과 BSSID 변경이 한 번에 보이면 끊김으로 센다.
///  - 브리지 재부팅(가동 시간이 줄었다)이면 기준을 새로 잡고 사건으로 세지 않는다. 연결 안 된 heartbeat(BSSID 없음·RSSI 0)는 건너뛴다.
/// </summary>
public sealed class WifiMonitor
{
    sealed class Last { public string Bssid = ""; public long Disc; public long Up = -1; public bool Have; }
    readonly Dictionary<int, Last> _last = new();

    public WifiEvent? Observe(int agv, DateTime atUtc, long upSeconds, int rssi, int channel, string bssid, long disc, string tag)
    {
        lock (_last)
        {
            if (!_last.TryGetValue(agv, out var l)) _last[agv] = l = new Last();
            if (upSeconds < l.Up)
            {
                l.Have = false;                 // 재부팅 — disc 카운터도 0 부터 다시
            }
            l.Up = upSeconds;
            if (string.IsNullOrEmpty(bssid) || rssi == 0) return null;
            WifiEvent? ev = null;
            if (l.Have)
            {
                if (disc > l.Disc) ev = new WifiEvent(atUtc, agv, WifiEventKind.Disconnect, l.Bssid, bssid, channel, rssi, tag);
                else if (!string.Equals(bssid, l.Bssid, StringComparison.OrdinalIgnoreCase))
                    ev = new WifiEvent(atUtc, agv, WifiEventKind.Roam, l.Bssid, bssid, channel, rssi, tag);
            }
            l.Bssid = bssid; l.Disc = disc; l.Have = true;
            return ev;
        }
    }
}

/// <summary>시간(UTC 정시)·위치(태그)·AP(BSSID) 별 RSSI 표본 합계</summary>
public sealed record WifiSpotKey(DateTime Hour, string Tag, string Bssid);
public sealed record WifiSpot(long Samples, long RssiSum, int RssiMin, long Weak, long Drops, long Roams);

/// <summary>
/// RSSI 표본을 메모리에 모았다가 한꺼번에 DB 로 내보낸다 (호기 37대 × 초당 1회를 그대로 쓰지 않으려고).
/// 약한 신호 기준 WeakRssi 이하. 끊김·로밍 건수도 같은 칸에 센다.
/// </summary>
public sealed class WifiAggregator
{
    public const int WeakRssi = -75;
    readonly Dictionary<WifiSpotKey, WifiSpot> _m = new();

    public static DateTime HourOf(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    public void AddSample(DateTime atUtc, string tag, string bssid, int rssi)
    {
        if (string.IsNullOrEmpty(bssid) || rssi == 0) return;
        Update(new WifiSpotKey(HourOf(atUtc), tag, bssid.ToLowerInvariant()),
               s => new WifiSpot(s.Samples + 1, s.RssiSum + rssi, s.Samples == 0 ? rssi : Math.Min(s.RssiMin, rssi), s.Weak + (rssi <= WeakRssi ? 1 : 0), s.Drops, s.Roams));
    }

    public void AddEvent(WifiEvent e)
        => Update(new WifiSpotKey(HourOf(e.At), e.Tag, e.ToBssid.ToLowerInvariant()),
                  s => e.Kind == WifiEventKind.Disconnect ? s with { Drops = s.Drops + 1 } : s with { Roams = s.Roams + 1 });

    void Update(WifiSpotKey k, Func<WifiSpot, WifiSpot> f)
    {
        lock (_m) _m[k] = f(_m.TryGetValue(k, out var s) ? s : new WifiSpot(0, 0, 0, 0, 0, 0));
    }

    /// <summary>모은 값을 꺼내고 비운다</summary>
    public List<(WifiSpotKey Key, WifiSpot Spot)> Drain()
    {
        lock (_m)
        {
            var r = _m.Select(kv => (kv.Key, kv.Value)).ToList();
            _m.Clear();
            return r;
        }
    }
}
