namespace Gateway.Server.Options;

/// <summary>appsettings.json 의 "AcsApi" 섹션 — 기존 ACS 서버로 호기 위치를 REST 로 올리는 설정 (TCP 중계 "Acs" 와 별개)</summary>
public sealed class AcsApiOptions
{
    public const string Section = "AcsApi";

    public bool Enabled { get; set; } = false;
    /// <summary>true 면 보내지 않고 통신 로그에 JSON 만 남김 (스키마 확인·시험용)</summary>
    public bool LogOnly { get; set; } = true;
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";
    /// <summary>묶음 전송 경로. 호기별 전송(PerAgv)이면 {agv} 가 호기 번호로 치환됨</summary>
    public string Path { get; set; } = "/api/agv/positions";
    public string Method { get; set; } = "POST";
    /// <summary>None | Bearer | ApiKey | Basic</summary>
    public string AuthType { get; set; } = "None";
    /// <summary>환경변수 AcsApi__AuthValue 로도 줄 수 있음</summary>
    public string? AuthValue { get; set; }
    public string ApiKeyHeader { get; set; } = "X-API-Key";
    public int TimeoutMs { get; set; } = 5000;
    /// <summary>발행 주기 (ms)</summary>
    public int IntervalMs { get; set; } = 1000;
    /// <summary>true = 지난 발행 이후 바뀐 호기만, false = 매번 전체</summary>
    public bool OnlyChanged { get; set; } = true;
    /// <summary>true = 호기마다 한 요청, false = 한 요청에 묶음</summary>
    public bool PerAgv { get; set; } = false;
    /// <summary>연속 실패 시 최대 대기 (초). 실패마다 2배로 늘어남</summary>
    public int MaxBackoffSec { get; set; } = 60;
}
