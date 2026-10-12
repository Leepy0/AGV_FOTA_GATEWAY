namespace Gateway.Server.Options;

/// <summary>appsettings.json 의 "Gateway" 섹션</summary>
public sealed class GatewayOptions
{
    public const string Section = "Gateway";

    /// <summary>브리지가 보내는 서버 UDP 포트 (브리지 server_port). MAIN_UP/STATUS/PONG/OTA_RESULT/CFG_RESULT 수신·하향 송신</summary>
    public int UdpPort { get; set; } = 50000;
    /// <summary>RFID 브로드캐스트 수신 포트 (브리지 rfid_port). 0 = 받지 않음</summary>
    public int RfidPort { get; set; } = 50001;
    /// <summary>데이터 디렉터리 (펌웨어 파일, 작업 목록). 상대 경로면 실행 파일 기준</summary>
    public string DataDir { get; set; } = "data";
    /// <summary>heartbeat 가 이 시간 넘게 없으면 오프라인 표시</summary>
    public int OfflineAfterSec { get; set; } = 5;
    /// <summary>
    /// 브리지 OTA 파일 URL 에 쓸 이 서버 주소. 비우면 호기 쪽 NIC 의 IP 와 Kestrel 포트를 자동으로 씀.
    /// 브리지 0.5.2 는 URL 호스트가 자기에게 설정된 server IP 와 정확히 같아야 받으므로 http://IPv4:포트 로 적는다 (호스트명 불가)
    /// </summary>
    public string? PublicBaseUrl { get; set; }
    /// <summary>
    /// 화면·API 에 접속할 때 쓸 수 있는 이름(Host) 추가 목록. IP 주소·localhost·이 PC 이름·FQDN 은 늘 허용.
    /// 사내 DNS 별칭 등으로 접속하면 여기에 적는다 ("*" = 검사 안 함). DNS 리바인딩 방어용
    /// </summary>
    public string[] AllowedHostNames { get; set; } = [];
    public AcsOptions Acs { get; set; } = new();

    /// <summary>
    /// PublicBaseUrl 이 설정돼 있는데 브리지 URL 규칙(bridge_util.h bu_url_host_is)에 맞지 않으면 경고 문구, 문제없거나 비었으면 null.
    /// 규칙: 소문자 http:// 또는 https://, 호스트는 앞자리 0 없는 점 네 칸 IPv4(각 0~255), 포트는 없거나 1~65535, 경로는 없거나 '/' 만.
    /// 사용자 정보(a@b)·호스트명·IPv6·16진·정수 꼴은 .NET 은 받아도 브리지가 url_host 로 거부한다. 원문 그대로 URL 에 쓰이므로 원문을 본다.
    /// 자동 결정(호기 쪽 NIC 의 IP)은 늘 이 꼴이라 해당 없음.
    /// </summary>
    public string? PublicBaseUrlProblem()
    {
        var url = PublicBaseUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return null;
        return IsBridgeBaseUrl(url) ? null
            : $"PublicBaseUrl '{url}' 이 브리지 URL 규칙에 맞지 않음 — 브리지는 http://IPv4[:포트] 꼴(소문자 scheme, 앞자리 0 없는 IPv4, 경로 없음)이고 호스트가 브리지에 설정된 server IP 와 같아야 받으므로 bad_url·url_host 로 거부할 수 있음 (예: http://192.168.10.5:8080 · 비우면 자동)";
    }

    /// <summary>브리지가 받는 OTA 기준 주소 꼴인지 (bu_url_host_is + ota.c·dsp_ota.c 의 소문자 "http" 접두어 검사)</summary>
    internal static bool IsBridgeBaseUrl(string url)
    {
        string rest;
        if (url.StartsWith("http://", StringComparison.Ordinal)) rest = url[7..];
        else if (url.StartsWith("https://", StringComparison.Ordinal)) rest = url[8..];
        else return false;
        if (rest.EndsWith('/')) rest = rest[..^1];   // 경로는 '/' 하나까지
        var colon = rest.IndexOf(':');
        var host = colon < 0 ? rest : rest[..colon];
        if (colon >= 0)
        {
            var port = rest[(colon + 1)..];
            if (port.Length is < 1 or > 5 || !port.All(char.IsAsciiDigit) || int.Parse(port) is < 1 or > 65535) return false;
        }
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(o =>
            o.Length is >= 1 and <= 3 && o.All(char.IsAsciiDigit) && !(o.Length > 1 && o[0] == '0') && int.Parse(o) <= 255);
    }
}

/// <summary>ACS(교통제어) TCP 소켓 중계. ※ 기존 ZigBee 게이트웨이의 소켓 규격 확인 후 맞춘다</summary>
public sealed class AcsOptions
{
    public bool Enabled { get; set; } = false;
    /// <summary>Client = 이 서버가 ACS 에 접속, Server = ACS 가 이 서버로 접속</summary>
    public string Mode { get; set; } = "Client";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6000;
    public int ReconnectSec { get; set; } = 3;
    /// <summary>Server 모드에서 접속을 받을 ACS IP 목록. 비우면 모두 허용. 목록에 없으면 접속 즉시 닫는다 (Client 모드는 해당 없음)</summary>
    public string[] AllowedRemotes { get; set; } = [];
}
