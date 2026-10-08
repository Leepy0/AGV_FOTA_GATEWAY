using System.Collections.Concurrent;
using System.Net;
using Gateway.Core.Bridge;
using Gateway.Core.Fota;

namespace Gateway.Server.Services;

/// <summary>호기 하나의 최신 상태 (heartbeat + 최근 OTA 상태). UI 와 작업 실행기가 읽는다</summary>
public sealed class AgvState
{
    public int Id { get; init; }
    public IPEndPoint? EndPoint { get; set; }
    public DateTime LastSeen { get; set; }
    public BridgeStatus? Status { get; set; }
    /// <summary>heartbeat 원문 (JSON 파싱에 실패해도 보관)</summary>
    public string StatusRaw { get; set; } = "";
    public DspStatus? Dsp { get; set; }
    public DateTime? DspSeen { get; set; }
    public long MainUp, MainDown, SeqLoss;
    public string LastFrame { get; set; } = "";
    public Dictionary<byte, ushort> LastSeq { get; } = new();
    public bool Online(int offlineAfterSec) => (DateTime.UtcNow - LastSeen).TotalSeconds < offlineAfterSec;
}

/// <summary>호기 목록. 변경 알림은 최대 초당 몇 번으로 묶어서 보낸다</summary>
public sealed class AgvRegistry
{
    readonly ConcurrentDictionary<int, AgvState> _agvs = new();
    public event Action? Changed;
    int _pending;

    public AgvState GetOrAdd(int id) => _agvs.GetOrAdd(id, i => new AgvState { Id = i });
    public AgvState? Get(int id) => _agvs.TryGetValue(id, out var a) ? a : null;
    public IReadOnlyList<AgvState> Snapshot() => _agvs.Values.OrderBy(a => a.Id).ToList();
    public BridgeStatus? StatusOf(int id) => Get(id)?.Status;

    /// <summary>타입별 seq 연속성 검사 → 누락 수</summary>
    public static int CheckSeq(AgvState a, byte type, ushort seq)
    {
        lock (a.LastSeq)
        {
            int loss = 0;
            if (a.LastSeq.TryGetValue(type, out var last))
            {
                var expect = (ushort)(last + 1);
                if (seq != expect && seq != 0) loss = (ushort)(seq - expect);
            }
            a.LastSeq[type] = seq;
            return loss;
        }
    }

    public void NotifyChanged()
    {
        // 여러 호기의 heartbeat 가 몰려도 UI 갱신은 200ms 에 한 번
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _ = Task.Delay(200).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _pending, 0);
            try { Changed?.Invoke(); } catch { }
        });
    }
}
