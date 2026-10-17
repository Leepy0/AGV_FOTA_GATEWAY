using System.Net;
using System.Security.Cryptography;
using Gateway.Core.Bridge;
using Gateway.Core.Fota;
using Gateway.Core.Update;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

/// <summary>
/// 브리지 펌웨어의 새 버전을 알아낸다. 브리지 릴리스(0.7.0+)가 첨부하는 bridge.json 을
/// {BridgeBaseUrl}/releases/latest/download/bridge.json 에서 읽는다 (서버 자체 업데이트와 같이 api.github.com 없이 github.com 만, 같은 프록시 설정).
/// 새 버전은 화면의 "펌웨어로 가져오기"로 sha256 을 확인해 펌웨어 목록(ESP)에 올린다 — 배포 작업은 운용자가 만든다.
/// 출처는 https 의 github.com 만 (서명이 없으므로 TLS 로 출처를 보증).
/// </summary>
public sealed class BridgeReleaseService : BackgroundService
{
    readonly UpdateOptions _opt;
    readonly FirmwareStore _fw;
    readonly ILogger<BridgeReleaseService> _log;
    readonly HttpClient _http;
    readonly SemaphoreSlim _busy = new(1, 1);

    public BridgeRelease? Latest { get; private set; }
    public DateTime LastCheck { get; private set; }
    /// <summary>마지막 확인이 실패한 사유 (성공하면 null)</summary>
    public string? Error { get; private set; }
    public event Action? Changed;

    public string RepoUrl => _opt.BridgeBaseUrl.TrimEnd('/');
    public string ManifestUrl => $"{RepoUrl}/releases/latest/download/{BridgeRelease.ManifestName}";

    public BridgeReleaseService(IOptions<UpdateOptions> opt, FirmwareStore fw, ILogger<BridgeReleaseService> log)
        : this(opt, fw, log, null) { }

    /// <summary>시험용: HttpClient 를 직접 준다</summary>
    internal BridgeReleaseService(IOptions<UpdateOptions> opt, FirmwareStore fw, ILogger<BridgeReleaseService> log, HttpClient? http)
    {
        _opt = opt.Value; _fw = fw; _log = log;
        if (http is not null) { _http = http; return; }
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        try { UpdateProxy.Apply(handler, _opt.Proxy, _opt.ProxyUser, _opt.ProxyPassword); }
        catch (ArgumentException) { UpdateProxy.Apply(handler, "", "", ""); }     // 잘못된 프록시 설정은 서버 업데이트 화면이 알려 준다
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _opt.FirstCheckDelaySec) + 5), ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckAsync(ct);
                if (_opt.BridgeCheckIntervalMin <= 0) return;
                await Task.Delay(TimeSpan.FromMinutes(_opt.BridgeCheckIntervalMin), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>매니페스트를 읽어 Latest 를 갱신한다. 실패하면 Error 에 사유 (이전 Latest 는 유지)</summary>
    public async Task<BridgeRelease?> CheckAsync(CancellationToken ct = default)
    {
        await _busy.WaitAsync(ct);
        try
        {
            LastCheck = DateTime.UtcNow;
            if (!UpdateFeed.IsAllowedSource(ManifestUrl)) { Error = $"허용되지 않은 주소: {ManifestUrl} (https://github.com 저장소만)"; Notify(); return Latest; }
            try
            {
                var json = await _http.GetStringAsync(ManifestUrl, ct);
                var rel = BridgeRelease.Parse(json);
                if (rel is null) { Error = "bridge.json 을 읽을 수 없습니다 (형식·해시 확인)"; Notify(); return Latest; }
                Latest = rel; Error = null;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // 404 = 아직 bridge.json 을 올린 릴리스가 없음(브리지 0.7.0 미만만 있음)
                Error = e is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                    ? "최신 브리지 릴리스에 bridge.json 이 없습니다 (브리지 0.7.0 이상부터 첨부됨)"
                    : "확인 실패: " + e.Message;
                _log.LogDebug("브리지 릴리스 확인 실패: {Msg}", e.Message);
            }
            Notify();
            return Latest;
        }
        finally { _busy.Release(); }
    }

    /// <summary>이미 펌웨어 목록에 같은 버전의 ESP 이미지가 있으면 그 항목</summary>
    public FirmwareEntry? AlreadyImported(BridgeRelease rel)
        => _fw.List().FirstOrDefault(e => e.Kind == FirmwareKind.Esp && e.Version == rel.Version.ToString(3));

    /// <summary>
    /// 최신 앱 이미지를 받아 sha256·크기·ESP-IDF 앱 헤더(프로젝트·버전)를 확인한 뒤 펌웨어 목록에 올린다.
    /// 이미 올려 둔 버전이면 그 항목을 돌려준다. 어느 단계든 어긋나면 InvalidOperationException (사유 포함).
    /// </summary>
    public async Task<FirmwareEntry> ImportLatestAsync(CancellationToken ct = default)
    {
        var rel = Latest ?? await CheckAsync(ct) ?? throw new InvalidOperationException(Error ?? "최신 버전을 알 수 없습니다");
        if (AlreadyImported(rel) is { } have) return have;
        var url = rel.AppUrl(RepoUrl);
        if (!UpdateFeed.IsAllowedSource(url)) throw new InvalidOperationException($"허용되지 않은 받기 주소: {url}");
        byte[] bytes;
        try { bytes = await _http.GetByteArrayAsync(url, ct); }
        catch (HttpRequestException e) { throw new InvalidOperationException("받기 실패: " + e.Message); }
        if (rel.AppSize > 0 && bytes.Length != rel.AppSize) throw new InvalidOperationException($"크기가 다름: 받은 {bytes.Length:N0}B / 매니페스트 {rel.AppSize:N0}B");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (sha != rel.AppSha256) throw new InvalidOperationException("sha256 이 매니페스트와 다릅니다 — 받은 파일을 쓰지 않습니다");
        var info = EspAppImage.Parse(bytes) ?? throw new InvalidOperationException("ESP-IDF 앱 이미지가 아닙니다");
        if (info.Project != EspAppImage.BridgeProject) throw new InvalidOperationException($"브리지 이미지가 아닙니다 (project {info.Project})");
        // 릴리스 빌드의 앱 버전은 태그와 같다 ("0.7.0"). 다르면 잘못 올린 릴리스
        if (UpdateFeed.ParseVersion(info.Version)?.ToString(3) != rel.Version.ToString(3))
            throw new InvalidOperationException($"앱 이미지 버전({info.Version})이 릴리스 버전({rel.Version.ToString(3)})과 다릅니다");
        using var ms = new MemoryStream(bytes);
        var entry = await _fw.AddAsync(FirmwareKind.Esp, info.Version, info.Project, $"GitHub 릴리스 {rel.Tag} 에서 가져옴 (sha256 확인){(rel.Notes.Length > 0 ? " — " + rel.Notes : "")}",
                                       [("esp", rel.AppFile, ms)], ct);
        _log.LogInformation("브리지 펌웨어 {Ver} 를 GitHub 에서 가져옴 → {Id}", info.Version, entry.Id);
        Notify();
        return entry;
    }

    void Notify() { try { Changed?.Invoke(); } catch { } }
}
