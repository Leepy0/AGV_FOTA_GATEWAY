using System.Net;

namespace Gateway.Core.Acs;

/// <summary>ACS TCP 중계의 허용 규칙: 하향 프레임 종류, 접속 원격 주소</summary>
public static class AcsRelayPolicy
{
    /// <summary>
    /// ACS → 메인보드로 넘기면 안 되는 하향 프레임인지. F(DSP 업데이트 진입·전송)·R(브리지용 응답)은
    /// 브리지·서버 전용이라 ACS 경로로 오면 버린다.
    /// </summary>
    public static bool IsBlockedDown(ReadOnlySpan<byte> f) => f.Length > 1 && (f[1] == (byte)'F' || f[1] == (byte)'R');

    /// <summary>
    /// 접속한 원격 IP 가 허용 목록에 있는지. 목록이 비었으면(공백 항목만 있어도) 모두 허용.
    /// 항목은 IP 주소 그대로 비교한다(IPv4-mapped IPv6 는 IPv4 로 보고 비교). 해석 안 되는 항목은 무시.
    /// </summary>
    public static bool IsAllowedRemote(IPAddress? remote, IReadOnlyCollection<string>? allowed)
    {
        if (allowed is null || allowed.All(string.IsNullOrWhiteSpace)) return true;
        if (remote is null) return false;
        remote = Norm(remote);
        foreach (var s in allowed)
            if (IPAddress.TryParse(s?.Trim(), out var a) && Norm(a).Equals(remote)) return true;
        return false;
    }

    /// <summary>목록에서 IP 로 해석되지 않는 항목 (기동 시 경고용)</summary>
    public static IReadOnlyList<string> InvalidEntries(IReadOnlyCollection<string>? allowed) =>
        allowed is null ? [] : allowed.Where(s => !string.IsNullOrWhiteSpace(s) && !IPAddress.TryParse(s.Trim(), out _)).ToList();

    static IPAddress Norm(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}
