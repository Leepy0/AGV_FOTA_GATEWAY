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
    public AcsOptions Acs { get; set; } = new();

    /// <summary>
    /// PublicBaseUrl 이 설정돼 있는데 호스트가 IPv4 주소가 아니면(호스트명·IPv6·형식 오류) 경고 문구, 문제없거나 비었으면 null.
    /// 자동 결정(호기 쪽 NIC 의 IP)은 늘 IPv4 라 해당 없음.
    /// </summary>
    public string? PublicBaseUrlProblem()
    {
        var url = PublicBaseUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.HostNameType != UriHostNameType.IPv4)
            return $"PublicBaseUrl '{url}' 의 호스트가 IPv4 주소가 아님 — 브리지 0.5.2 는 URL 호스트가 브리지에 설정된 server IP 와 같아야 받으므로 url_host 로 거부할 수 있음 (PublicBaseUrl 은 브리지 server IP 와 같은 IPv4, 예: http://192.168.10.5:8080 · 비우면 자동)";
        return null;
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
