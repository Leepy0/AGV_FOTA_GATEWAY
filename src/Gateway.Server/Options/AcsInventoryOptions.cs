using Gateway.Core.Acs;

namespace Gateway.Server.Options;

/// <summary>
/// appsettings.json 의 "AcsInventory" 섹션 — 기존 ACS 서버에서 호기 소속(제품군·층·라인·공정·라우트)을 REST 로 받아오는 설정.
/// 응답 필드 이름은 Fields 로 맞춘다 (월요일 스키마 반영 지점: 대개 이 섹션만 고치면 됨).
/// </summary>
public sealed class AcsInventoryOptions
{
    public const string Section = "AcsInventory";

    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";
    /// <summary>호기 목록 GET 경로</summary>
    public string Path { get; set; } = "/api/agv/list";
    /// <summary>None | Bearer | ApiKey | Basic</summary>
    public string AuthType { get; set; } = "None";
    /// <summary>환경변수 AcsInventory__AuthValue 로도 줄 수 있음</summary>
    public string? AuthValue { get; set; }
    public string ApiKeyHeader { get; set; } = "X-API-Key";
    public int TimeoutMs { get; set; } = 10000;
    /// <summary>받아오는 주기 (초). 0 = 시작 때 한 번만 (화면·API 로 수동)</summary>
    public int IntervalSec { get; set; } = 300;
    /// <summary>true = 화면·CSV 로 직접 넣은 값(manual/import)도 ACS 값으로 덮어씀. false = ACS 값은 acs 출처 항목만 갱신</summary>
    public bool OverrideManual { get; set; } = false;
    /// <summary>true = ACS 목록에 없는 acs 출처 호기는 소속 삭제</summary>
    public bool RemoveMissing { get; set; } = true;
    public AcsInventoryFieldMap Fields { get; set; } = new();
}
