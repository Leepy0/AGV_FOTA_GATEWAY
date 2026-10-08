namespace Gateway.Server.Options;

/// <summary>appsettings.json 의 "Update" 섹션 — 서버 자체 업데이트</summary>
public sealed class UpdateOptions
{
    public const string Section = "Update";

    public bool Enabled { get; set; } = true;
    /// <summary>GitHub = GitHub Releases API, Url = 사내 HTTP 피드(latest.json)</summary>
    public string Source { get; set; } = "GitHub";
    public string GitHubRepo { get; set; } = "Leepy0/AGV_FOTA_GATEWAY";
    /// <summary>비공개 저장소면 필요 (fine-grained PAT, Contents: Read). 환경변수 Update__GitHubToken 으로도 줄 수 있다</summary>
    public string? GitHubToken { get; set; }
    /// <summary>Source=Url 일 때 latest.json 주소 (예: http://fileserver/fota/latest.json)</summary>
    public string? FeedUrl { get; set; }
    public string AssetSuffix { get; set; } = "_win-x64.zip";
    /// <summary>자동 확인 주기 (0 = 수동만)</summary>
    public int CheckIntervalMin { get; set; } = 360;
    /// <summary>새 버전을 받으면 작업이 없을 때 자동으로 적용(재시작). false 면 화면에서 "적용" 을 눌러야 한다</summary>
    public bool AutoApply { get; set; } = false;
    public string ServiceName { get; set; } = "AgvFotaGateway";
}
