namespace Gateway.Server.Options;

/// <summary>appsettings.json 의 "Update" 섹션 — 서버 자체 업데이트 (사내망 대응)</summary>
public sealed class UpdateOptions
{
    public const string Section = "Update";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 릴리스 루트 = GitHub 저장소 페이지 — 사내망에서도 github.com 은 열려 있어
    /// {BaseUrl}/releases/latest/download/AgvFotaGateway.json(매니페스트) 로 확인한다 (api.github.com 은 마지막 폴백).
    /// 서명 검증이 없으므로 출처는 https 의 github.com · api.github.com 만 허용한다 (http, file://, 다른 호스트는 확인 단계에서 거부).
    /// </summary>
    public string BaseUrl { get; set; } = "https://github.com/Leepy0/AGV_FOTA_GATEWAY";
    /// <summary>매니페스트 주소를 직접 지정할 때 (비우면 BaseUrl 에서 조합). 같은 출처 제한</summary>
    public string? ManifestUrl { get; set; }
    /// <summary>비공개 저장소에서 API 폴백을 쓸 때만 (fine-grained PAT, Contents: Read). 환경변수 Update__GitHubToken</summary>
    public string? GitHubToken { get; set; }
    /// <summary>시작 후 첫 확인까지 (초)</summary>
    public int FirstCheckDelaySec { get; set; } = 3;
    /// <summary>자동 확인 주기 (분, 0 = 시작 때 한 번만)</summary>
    public int CheckIntervalMin { get; set; } = 360;
    /// <summary>새 버전을 받으면 배포 작업이 없을 때 자동으로 적용(재시작). ACS 중계가 연결돼 있으면 미루고 화면에서 적용. false 면 화면에서 "적용"</summary>
    public bool AutoApply { get; set; } = false;
    public string ServiceName { get; set; } = "AgvFotaGateway";
}
