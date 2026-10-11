using System.Text.Json;
using Gateway.Core.Bridge;

namespace Gateway.Core.Fota;

public sealed class EspOtaException(string message) : Exception(message);

/// <summary>
/// 브리지(ESP32) 자체 OTA — OTA_START 에 URL 을 보내고 OTA_RESULT(start/progress/done/fail/busy) 를 기다린 뒤,
/// 재부팅한 새 펌웨어의 heartbeat 를 보고 pending 이면 PING 을 반복해 확정(valid)시킨다.
/// UDP 유실 대비: OTA_START 뒤 수신 흔적이 없으면 다시 보내고, done 보고가 유실돼도 heartbeat 의 재부팅 흔적
/// (part 변경·ota=pending·fw 변경·up 감소)으로 진행한다. 브리지 0.5.2 는 최종 결과를 3회 반복하므로 중복에 안전해야 한다.
/// </summary>
public sealed class EspOtaClient
{
    readonly IAgvLink _link;
    readonly Func<int, BridgeStatus?> _status;     // 최신 heartbeat 조회 (서버의 AgvRegistry)
    readonly OtaTimeouts _t;

    public EspOtaClient(IAgvLink link, Func<int, BridgeStatus?> status, OtaTimeouts? timeouts = null)
    {
        _link = link; _status = status; _t = timeouts ?? OtaTimeouts.Default;
    }

    public async Task<string> RunAsync(int agv, string url, string expectedVersion, Action<string>? progress, CancellationToken ct)
    {
        using var sub = _link.Subscribe(agv);
        var w = new Watch(_status(agv));
        var before = w.Before?.Fw ?? "?";
        if (!_link.SendOtaStart(agv, url)) throw new EspOtaException("브리지 endpoint 없음");
        progress?.Invoke($"OTA_START {url} (현재 fw {before})");

        // 1. 다운로드 결과 — OTA_RESULT done, 또는 done 이 유실됐으면 heartbeat 의 재부팅 흔적
        var t0 = DateTime.UtcNow;
        var lastSend = t0;
        int resends = 0;
        bool received = false;
        BridgeStatus? st = null;
        var reader = sub.OtaResults.Reader;
        while (st is null)
        {
            if (DateTime.UtcNow - t0 >= _t.EspResult)
                throw new EspOtaException($"OTA_RESULT 없음 ({Sec(_t.EspResult)}{(resends > 0 ? $", 재전송 {resends}회" : "")})");
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(_t.Poll);
                try { if (!await reader.WaitToReadAsync(cts.Token)) throw new EspOtaException("구독 종료"); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            bool done = false;
            while (!done && reader.TryRead(out var js))
            {
                string stage = "", err = "";
                try
                {
                    using var d = JsonDocument.Parse(js);
                    stage = d.RootElement.TryGetProperty("stage", out var s) ? s.GetString() ?? "" : "";
                    err = d.RootElement.TryGetProperty("err", out var e) ? e.GetString() ?? "" : "";
                }
                catch (JsonException) { }
                received = true;
                if (stage == "busy" && resends > 0) { progress?.Invoke("OTA busy — 재전송 전 명령이 이미 진행 중"); continue; }
                progress?.Invoke($"OTA {js}");
                if (stage == "done") done = true;
                else if (stage == "fail") throw new EspOtaException($"브리지 OTA 실패: {err}");
                else if (stage == "busy") throw new EspOtaException("브리지가 이미 OTA 진행 중");
            }
            if (done) break;

            var s0 = w.Next(_status(agv));
            if (s0 is not null)
            {
                if (s0.Ota == "busy") received = true;
                if (w.NewFirmware(s0) || w.Rebooted)
                {
                    if (s0.Ota != "pending" && w.SamePart(s0))
                        throw new EspOtaException($"OTA 중 브리지가 재부팅됨 (done 보고 없음) — 이전 펌웨어 그대로 (fw {s0.Fw}, part {s0.Part})");
                    st = s0;
                    progress?.Invoke("재부팅 확인 — OTA_RESULT done 미수신, heartbeat 로 판정");
                    break;
                }
            }
            var now = DateTime.UtcNow;
            if (!received && resends < _t.MaxResends && now - lastSend >= _t.Resend)
            {
                resends++;
                lastSend = now;
                _link.SendOtaStart(agv, url);
                progress?.Invoke($"OTA_START 재전송 ({resends}/{_t.MaxResends})");
            }
        }

        // 2. 재부팅 후 새 펌웨어 heartbeat — part 변경·ota=pending·fw 변경·up 감소 중 하나 (같은 버전 재설치는 fw 가 같고,
        //    ACS 중계 하향을 먼저 받으면 첫 heartbeat 부터 valid 일 수 있다)
        if (st is null)
        {
            progress?.Invoke("재부팅 대기");
            var t1 = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(_t.Poll, ct);
                var s = w.Next(_status(agv));
                if (s is not null && (w.Rebooted || w.NewFirmware(s))) { st = s; break; }
                if (DateTime.UtcNow - t1 >= _t.EspReboot)
                    throw new EspOtaException($"재부팅 후 heartbeat 확인 안 됨 ({Sec(_t.EspReboot)}) — 새 펌웨어가 미확정(pending)이면 부팅 180초 뒤 이전 펌웨어로 롤백됨");
            }
        }
        if (st.Ota != "pending" && w.SamePart(st))
            throw new EspOtaException($"재부팅했지만 이전 슬롯({st.Part}) 그대로 — 새 펌웨어 기동 실패로 롤백됨 (fw {st.Fw})");

        // 3. pending 이면 valid 가 될 때까지 PING 반복 (한 번만 보내면 UDP 유실 한 번에 실패 → 180초 뒤 롤백)
        if (st.Ota == "pending")
        {
            var t2 = DateTime.UtcNow;
            int pings = 0;
            while (true)
            {
                _link.SendPing(agv);
                pings++;
                await Task.Delay(_t.PingInterval, ct);
                var s = _status(agv);
                if (s is not null && s.Ota == "valid") { st = s; break; }
                if (DateTime.UtcNow - t2 >= _t.EspConfirm)
                    throw new EspOtaException($"확정(valid) 안 됨 ({Sec(_t.EspConfirm)}, PING {pings}회) — 부팅 180초 안에 호기 현황에서 PING 하면 확정됨, 재부팅하면 롤백됨");
            }
            progress?.Invoke($"확정 (PING {pings}회)");
        }
        if (!string.IsNullOrEmpty(expectedVersion) && st.Fw != expectedVersion)
            throw new EspOtaException($"버전 불일치: 기대 {expectedVersion}, 실제 {st.Fw}");
        progress?.Invoke($"완료 fw {st.Fw} part {st.Part} ota {st.Ota}");
        return st.Fw;
    }

    static string Sec(TimeSpan t) => t.TotalSeconds >= 1 ? $"{t.TotalSeconds:F0}초" : $"{t.TotalMilliseconds:F0}ms";

    /// <summary>OTA 전 heartbeat 와 비교해 재부팅·새 펌웨어 흔적을 판정 (새로 온 heartbeat 만 본다)</summary>
    sealed class Watch(BridgeStatus? before)
    {
        public BridgeStatus? Before { get; } = before;
        BridgeStatus? _last = before;

        /// <summary>heartbeat 의 up 이 직전 heartbeat 보다 줄어든 적이 있음 (재부팅)</summary>
        public bool Rebooted { get; private set; }

        /// <summary>새 heartbeat 면 돌려주고 up 감소를 기록, 같은 것(아직 새 heartbeat 없음)이면 null</summary>
        public BridgeStatus? Next(BridgeStatus? s)
        {
            if (s is null || ReferenceEquals(s, _last)) return null;
            if (_last is not null && s.UpSeconds < _last.UpSeconds) Rebooted = true;
            _last = s;
            return s;
        }

        /// <summary>새 펌웨어로 부팅한 흔적: ota=pending, 실행 슬롯 변경, fw 변경(+valid)</summary>
        public bool NewFirmware(BridgeStatus s) =>
            (s.Ota == "pending" && Before?.Ota != "pending") ||
            (Before is not null && Before.Part.Length > 0 && s.Part.Length > 0 && s.Part != Before.Part) ||
            (Before is not null && s.Fw != Before.Fw && s.Ota == "valid");

        /// <summary>OTA 전과 같은 슬롯에서 돌고 있음 (OTA 는 항상 다른 슬롯에 쓰므로 롤백·기동 실패)</summary>
        public bool SamePart(BridgeStatus s) => Before is not null && Before.Part.Length > 0 && s.Part == Before.Part;
    }
}
