using System.Net;

namespace Gateway.Core.Bridge;

public enum OriginDecision
{
    /// <summary>기존 endpoint 에서 온 패킷</summary>
    Same,
    /// <summary>새 주소를 endpoint 로 채택 (처음 보는 호기, 기존 endpoint 가 오프라인, 같은 MAC 의 주소 변경)</summary>
    Move,
    /// <summary>기존 endpoint 가 살아 있는데 다른 장치가 같은 호기 번호로 보냄 — endpoint 를 바꾸지 않고 패킷을 버린다</summary>
    Conflict,
}

/// <summary>
/// 패킷 송신원으로 호기 endpoint 를 정하는 규칙 (호기 번호 충돌 감지). 이미 붙어 있던(살아 있는) endpoint 를 지키고,
/// 다른 주소에서 같은 번호가 오면 충돌로 본다. 기존 endpoint 가 오프라인이 되면 새 주소로 넘어간다(정상 IP 변경).
/// MAC 은 INFO(및 브리지 0.5.2+ heartbeat)에만 있다 — 둘 다 알면 MAC 이 같을 때만 같은 장치로 본다.
/// </summary>
public static class BridgeOrigin
{
    public const int MinAgv = 1, MaxAgv = 99;

    /// <summary>호기 번호 범위 (ACS 프레임 AGV 칸이 두 자리)</summary>
    public static bool IsValidAgv(int agv) => agv is >= MinAgv and <= MaxAgv;

    /// <param name="current">지금 endpoint (없으면 null)</param>
    /// <param name="currentOnline">지금 endpoint 에서 최근 OfflineAfterSec 안에 패킷이 왔는지</param>
    /// <param name="knownMac">알고 있는 그 호기의 MAC ("" = 모름)</param>
    /// <param name="from">이번 패킷 송신원</param>
    /// <param name="packetMac">이번 패킷(heartbeat/INFO)의 MAC ("" = 없음)</param>
    public static OriginDecision Decide(IPEndPoint? current, bool currentOnline, string knownMac, IPEndPoint from, string packetMac)
    {
        if (current is null) return OriginDecision.Move;
        if (current.Equals(from)) return OriginDecision.Same;
        if (!currentOnline) return OriginDecision.Move;
        if (knownMac.Length > 0 && packetMac.Length > 0 && string.Equals(knownMac, packetMac, StringComparison.OrdinalIgnoreCase))
            return OriginDecision.Move;
        return OriginDecision.Conflict;
    }

    /// <summary>둘 다 MAC 을 알고 서로 다르면 확실한 충돌 (표시용)</summary>
    public static bool MacDiffers(string knownMac, string packetMac) =>
        knownMac.Length > 0 && packetMac.Length > 0 && !string.Equals(knownMac, packetMac, StringComparison.OrdinalIgnoreCase);
}
