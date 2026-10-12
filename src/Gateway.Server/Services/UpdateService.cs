using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using Gateway.Core.Update;
using Gateway.Server.Options;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace Gateway.Server.Services;

public enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Ready, Applying, Error }

/// <summary>
/// 서버 자체 업데이트 (사내망 대응). WPF 프로그램의 자동 업데이트(AmrMapEditor Updater)와 같은 규칙:
///  - 확인: {BaseUrl}/releases/latest/download/AgvFotaGateway.json → releases/latest 리다이렉트 → api.github.com 순서
///    (사내망은 api.github.com 이 막히고 github.com 만 열림)
///  - 받기: 프로그램이 직접 받고(.partial) SHA-256 이 맞을 때만 채택
///  - 출처: https 의 github.com · api.github.com 만 (서명 검증 없음 — 출처를 TLS 로 보증)
///  - 교체: 실행 중인 exe 는 덮어쓸 수 없으므로 서비스를 멈춘 뒤 현재 exe → .prev, 새 파일 복사, 성공하면 .prev → .old, 다시 시작 (apply-update.bat).
///    배치가 시작됐다는 표시(run-started.txt)를 5초 안에 확인한 뒤에만 서버를 멈춘다 (못 보면 멈추지 않고 오류로 돌아감)
///    다시 띄운 뒤 60초 안에 /api/health 가 새 버전을 돌려주지 않으면 배치가 이전 exe 로 자동 되돌림
///  - 되돌리기: .old 가 있으면 한 세대 되돌림
///  - 채널: release(정식) · dev(main 테스트, 정식만 받음) · local(로컬 빌드, 교체 안 함)
/// appsettings.json 과 data/ 는 덮어쓰지 않는다.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    readonly UpdateOptions _opt;
    readonly JobService _jobs;
    readonly AcsRelayService _acs;
    readonly IServer _server;
    readonly IHostApplicationLifetime _life;
    readonly ILogger<UpdateService> _log;
    readonly string _dir;
    readonly HttpClient _http;
    readonly List<string> _logLines = new();
    readonly object _gate = new();

    public Version Current { get; }
    public string CurrentText { get; }
    public string? Commit { get; }
    /// <summary>release · dev · local</summary>
    public string Channel { get; }
    public string ChannelName => Channel switch { "release" => "정식 배포", "dev" => "테스트 빌드", _ => "로컬 빌드" };
    public bool CanSelfUpdate => Channel is "release" or "dev" && OperatingSystem.IsWindows();
    public UpdateStage Stage { get; private set; }
    public UpdateInfo? Latest { get; private set; }
    public string? StagedDir { get; private set; }
    /// <summary>받아 둔(적용 대기) 버전 태그 vX.Y.Z</summary>
    public string? StagedTag { get; private set; }
    public string Message { get; private set; } = "";
    public DateTime? LastCheck { get; private set; }
    public IReadOnlyList<string> Log { get { lock (_logLines) return _logLines.ToArray(); } }
    public bool Enabled => _opt.Enabled;
    public string BaseUrl => _opt.BaseUrl.TrimEnd('/');
    public string ManifestUrl => string.IsNullOrEmpty(_opt.ManifestUrl) ? $"{BaseUrl}/releases/latest/download/{UpdateFeed.ManifestName}" : _opt.ManifestUrl;
    /// <summary>브라우저로 직접 받는 고정 주소 (항상 최신)</summary>
    public string DownloadUrl => $"{BaseUrl}/releases/latest/download/{UpdateFeed.AssetName}";
    public string ReleasesPage => $"{BaseUrl}/releases";
    /// <summary>받기 단추를 켤지: 확인된 새 버전이 있고 받는 중·적용 중이 아님 (받기 실패 뒤에도 다시 받을 수 있게)</summary>
    public bool CanDownload => Latest is { } l && UpdateFeed.IsNewer(l.Version, Current) && Stage is UpdateStage.Available or UpdateStage.Error or UpdateStage.Ready && StagedTag != l.Tag;
    /// <summary>
    /// 서비스로 등록된 이 exe 가 서비스 관리자(SCM) 밖에서 돌고 있으면 안내 문구 (v0.2.x 의 자체 업데이트가 남긴 상태 등), 아니면 null.
    /// LocalSystem 으로 돌고 있으면 다음 업데이트·되돌리기가 svc 모드로 sc start 해서 서비스로 되돌아간다
    /// </summary>
    public string? OutsideScmWarning { get; }
    /// <summary>배치를 svc 모드(sc stop/start)로 돌릴지</summary>
    readonly bool _runAsService;
    public event Action? Changed;

    static string ExePath => Path.Combine(AppContext.BaseDirectory, "AgvFotaGateway.exe");
    static string OldPath => ExePath + ".old";
    /// <summary>교체 실패·자동 되돌림·수동 되돌리기한 버전 (자동 적용에서 제외, 화면에서는 적용 가능)</summary>
    string SkipAutoPath => Path.Combine(_dir, "skip-auto.txt");

    public UpdateService(IOptions<UpdateOptions> opt, IOptions<GatewayOptions> gw, JobService jobs, AcsRelayService acs, IServer server,
                         IHostApplicationLifetime life, ILogger<UpdateService> log)
    {
        _opt = opt.Value; _jobs = jobs; _acs = acs; _server = server; _life = life; _log = log;
        _dir = Path.Combine(gw.Value.DataDir, "update");
        var asm = Assembly.GetEntryAssembly()!;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var parts = info.Split('+', 2);
        Current = UpdateFeed.ParseVersion(parts[0]) ?? UpdateFeed.ParseVersion(asm.GetName().Version?.ToString()) ?? new Version(0, 0, 0);
        CurrentText = Current.ToString(3);
        Commit = parts.Length > 1 && parts[1].Length >= 7 ? parts[1][..7] : null;
        Channel = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildChannel")?.Value ?? "local";
        // 시스템 프록시 사용. 콘솔 실행이면 로그인 사용자의 프록시·자격, 서비스(LocalSystem)면 SYSTEM 계정의 프록시 설정과
        // 컴퓨터 계정(DOMAIN\PC$) 자격이 쓰인다 (사용자 프록시·사용자 인증은 서비스에 적용되지 않음)
        _http = new HttpClient(new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials, AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgvFotaGateway/" + CurrentText);

        _runAsService = WindowsServiceHelpers.IsWindowsService();
        if (OperatingSystem.IsWindows() && !_runAsService && ServiceUsesThisExe())
        {
            bool system = IsLocalSystem();
            // 0.2.x 배치는 서비스를 일반 SYSTEM 프로세스로 다시 띄웠다 — 그대로 console 모드로 두면 업데이트마다 SCM 밖에 머문다
            _runAsService = system;
            OutsideScmWarning = $"Windows 서비스 '{_opt.ServiceName}' 로 등록된 exe 가 서비스 관리자(SCM) 밖에서 실행 중입니다 — 멈춰도 자동 재시작되지 않습니다. "
                              + $"관리자 명령 창에서 taskkill /f /im AgvFotaGateway.exe 후 sc start {_opt.ServiceName} 로 서비스로 되돌리세요"
                              + (system ? " (다음 서버 업데이트·되돌리기도 서비스로 다시 시작합니다)." : ".");
            _log.LogWarning("SCM 밖에서 실행 중: {Msg}", OutsideScmWarning);
        }
    }

    /// <summary>레지스트리의 서비스 등록(ImagePath)이 지금 실행 중인 exe 를 가리키는지</summary>
    [SupportedOSPlatform("windows")]
    bool ServiceUsesThisExe()
    {
        try
        {
            var image = Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{_opt.ServiceName}", "ImagePath", null) as string;
            return SelfUpdateScript.SameExe(image, Environment.ProcessPath ?? ExePath);
        }
        catch (Exception e) { _log.LogDebug("service ImagePath: {Msg}", e.Message); return false; }
    }

    [SupportedOSPlatform("windows")]
    static bool IsLocalSystem() { try { using var id = WindowsIdentity.GetCurrent(); return id.IsSystem; } catch { return false; } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        CleanupLeftovers();
        RestoreStaged();
        if (!_opt.Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _opt.FirstCheckDelaySec)), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = await CheckAsync(ct);
                if (info is not null && _opt.AutoApply && CanSelfUpdate)
                {
                    if (ReadSkipAuto() == info.Tag) Add($"{info.Tag} 은(는) 이전에 실패했거나 되돌린 버전(교체 실패·기동 확인 실패·수동 되돌리기) — 자동 적용하지 않음 (화면에서 적용 가능)");
                    else
                    {
                        if (Stage == UpdateStage.Available) await DownloadAsync(ct);
                        if (Stage == UpdateStage.Ready)
                        {
                            // 적용 중에는 ACS 중계가 약 10초 끊기므로 연결돼 있으면 자동 적용하지 않고 화면에서 적용하게 한다
                            if (_acs.Connected) Set(UpdateStage.Ready, $"{StagedTag} 받아 둠 — ACS 중계 연결 중이라 자동 적용을 미룸 (화면에서 적용)");
                            else await ApplyAsync();
                        }
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { Add("자동 확인 실패: " + e.Message); }
            if (_opt.CheckIntervalMin <= 0) return;
            try { await Task.Delay(TimeSpan.FromMinutes(_opt.CheckIntervalMin), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>from 단계 중 하나일 때만 to 로 바꾼다 (동시에 누른 확인·받기가 겹치지 않게)</summary>
    bool Enter(UpdateStage to, string msg, Func<UpdateStage, bool> from)
    {
        lock (_gate)
        {
            if (!from(Stage)) return false;
            Stage = to; Message = msg;
        }
        Add(msg);
        return true;
    }

    bool HasStaged => StagedDir is not null && StagedTag is not null && Directory.Exists(StagedDir);

    // ───── 확인: 매니페스트 → 리다이렉트 → API ─────
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // 받는 중·적용 중에는 확인하지 않는다 (받기 상태가 Available 로 돌아가 두 번째 받기가 겹치는 것 방지)
        if (!Enter(UpdateStage.Checking, "확인 중…", s => s is not (UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Applying))) return null;
        UpdateInfo? info = null;
        Exception? first = null;
        if (!UpdateFeed.IsAllowedSource(BaseUrl) || !UpdateFeed.IsAllowedSource(ManifestUrl))
            first = new InvalidOperationException($"허용되지 않은 업데이트 주소 ({ManifestUrl}) — https://github.com 저장소 주소만 됩니다 (Update.BaseUrl · ManifestUrl)");
        else
            foreach (var source in new Func<CancellationToken, Task<UpdateInfo?>>[] { FromManifestAsync, FromRedirectAsync, FromApiAsync })
            {
                try { info = await source(ct); if (info is not null) break; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { Set(HasStaged ? UpdateStage.Ready : UpdateStage.Idle, "확인 취소"); throw; }
                catch (Exception e) { first ??= e; }
            }
        LastCheck = DateTime.UtcNow;
        if (info is null)
        {
            var why = first is null
                ? $"정식 릴리스가 없거나 접근할 수 없음 ({ManifestUrl}). 비공개 저장소면 공개로 바꾸거나 GitHubToken 을 설정"
                : "확인 실패: " + first.Message;
            // 받아 둔 업데이트는 확인이 실패해도 그대로 적용할 수 있다
            if (HasStaged) Set(UpdateStage.Ready, $"{why} — 받아 둔 {StagedTag} 은(는) 적용 가능");
            else Set(UpdateStage.Error, why);
            return null;
        }
        Latest = info;
        if (!UpdateFeed.IsNewer(info.Version, Current)) { Set(UpdateStage.UpToDate, $"최신 버전입니다 ({CurrentText}, {ChannelName})"); return null; }
        if (HasStaged && StagedTag == info.Tag) { Set(UpdateStage.Ready, $"{info.Tag} 받아 둠 — 적용 대기"); return info; }
        Set(UpdateStage.Available, $"새 버전 {info.Tag} ({info.AssetName}, {info.Size / 1024 / 1024}MB){(info.Sha256 is null ? " · SHA-256 정보 없음" : "")}"
                                   + (HasStaged ? $" · 받아 둔 {StagedTag} 대신 새 버전을 받으세요" : ""));
        return info;
    }

    async Task<UpdateInfo?> FromManifestAsync(CancellationToken ct)
    {
        var text = await ReadTextAsync(ManifestUrl, ct);
        if (text is null) return null;
        var info = UpdateFeed.ParseManifest(text, BaseUrl);
        if (info is not null && UpdateFeed.SourceError(info) is { } err) throw new InvalidOperationException("매니페스트: " + err);
        return info;
    }

    async Task<UpdateInfo?> FromRedirectAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync($"{ReleasesPage}/latest", HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        var final = res.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        int i = final.LastIndexOf("/tag/", StringComparison.Ordinal);
        if (i < 0 || UpdateFeed.ParseVersion(Uri.UnescapeDataString(final[(i + 5)..])) is not { } ver) return null;
        string tag = "v" + ver.ToString(3);
        return new UpdateInfo(ver, tag, UpdateFeed.AssetName, $"{ReleasesPage}/download/{tag}/{UpdateFeed.AssetName}", "", $"{ReleasesPage}/tag/{tag}", 0,
                              null, $"{ReleasesPage}/download/{tag}/{UpdateFeed.AssetName}.sha256", DateTimeOffset.MinValue);
    }

    async Task<UpdateInfo?> FromApiAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) || !u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var repo = u.AbsolutePath.Trim('/');
        using var req = NewRequest($"https://api.github.com/repos/{repo}/releases/latest", "application/vnd.github+json");
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new InvalidOperationException("GitHub API 횟수 제한 — 1시간 뒤 다시");
        res.EnsureSuccessStatusCode();
        var info = UpdateFeed.ParseGitHubRelease(await res.Content.ReadAsStringAsync(ct));
        if (info is not null && UpdateFeed.SourceError(info) is { } err) throw new InvalidOperationException("GitHub API: " + err);
        return info;
    }

    /// <summary>GitHub 토큰은 호스트가 api.github.com 일 때만 붙인다 (리다이렉트 때 HttpClient 가 Authorization 을 떼어 냄)</summary>
    HttpRequestMessage NewRequest(string url, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd(accept);
        if (!string.IsNullOrEmpty(_opt.GitHubToken) && UpdateFeed.IsGitHubApi(url)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.GitHubToken);
        return req;
    }

    // ───── 받기: .partial + SHA-256 ─────
    public async Task DownloadAsync(CancellationToken ct = default)
    {
        var info = Latest;
        if (info is null || !CanDownload) return;
        if (!Enter(UpdateStage.Downloading, $"{info.AssetName} 받는 중…", s => s is UpdateStage.Available or UpdateStage.Error or UpdateStage.Ready)) return;
        try
        {
            if (UpdateFeed.SourceError(info) is { } err) throw new InvalidOperationException(err);
            Directory.CreateDirectory(_dir);
            var expected = info.Sha256 ?? (await ReadTextAsync(info.Sha256Url, ct) is { } s ? UpdateFeed.ParseShaFile(s) : null)
                           ?? throw new InvalidOperationException("릴리스에 검증용 SHA-256 정보가 없음 (매니페스트 또는 .sha256 파일)");
            var zip = Path.Combine(_dir, $"AgvFotaGateway_{info.Version.ToString(3)}.zip");
            var part = zip + ".partial";
            try
            {
                using var req = NewRequest(info.AssetUrl, "application/octet-stream");
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? info.Size, done = 0; int lastPct = -1;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buf = new byte[81920]; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct / 10 != lastPct / 10) { lastPct = pct; Message = $"{info.AssetName} 받는 중… {pct}%"; Changed?.Invoke(); }
                }
            }
            catch { TryDelete(part); throw; }
            string actual;
            await using (var fs = File.OpenRead(part)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();   // 닫은 뒤 이동 (Windows 공유 위반 방지)
            if (actual != expected) { TryDelete(part); throw new InvalidOperationException("받은 파일이 손상됨 (SHA-256 불일치) — 다시 받으세요"); }
            File.Move(part, zip, true);

            var stage = Path.Combine(_dir, info.Tag);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            ZipFile.ExtractToDirectory(zip, stage);
            var exe = Directory.GetFiles(stage, "AgvFotaGateway.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException("zip 에 AgvFotaGateway.exe 가 없음");
            StagedDir = Path.GetDirectoryName(exe)!;
            StagedTag = info.Tag;
            TryDelete(zip);
            Set(UpdateStage.Ready, $"{info.Tag} 받음 (SHA-256 확인) — 적용하면 서비스가 재시작됩니다");
        }
        catch (OperationCanceledException) { Set(HasStaged ? UpdateStage.Ready : UpdateStage.Available, "받기 취소"); throw; }
        catch (Exception e)
        {
            // 받아 둔 이전 업데이트가 있으면 그대로 적용할 수 있게 둔다
            if (HasStaged) Set(UpdateStage.Ready, $"받기 실패: {e.Message} — 받아 둔 {StagedTag} 은(는) 적용 가능");
            else Set(UpdateStage.Error, "받기 실패: " + e.Message);
        }
    }

    // ───── 교체 · 되돌리기 (Windows, 배치) ─────
    public async Task ApplyAsync()
    {
        string tag, dir;
        lock (_gate)
        {
            if (Stage != UpdateStage.Ready || !HasStaged) return;
            if (!CanSelfUpdate) { Set(UpdateStage.Ready, Channel == "local" ? "로컬 빌드는 자동 교체하지 않음 (bin 폴더)" : "자동 교체는 Windows 에서만"); return; }
            if (!HoldJobs()) { Set(UpdateStage.Ready, "배포 작업이 실행 중 — 끝난 뒤 적용하세요"); return; }
            tag = StagedTag!; dir = StagedDir!;
            Stage = UpdateStage.Applying;   // 배치 시작 확인 동안 확인·받기·되돌리기가 끼어들지 않게
        }
        if (!await RunBatchAsync("apply", dir, tag, UpdateFeed.ParseVersion(tag)?.ToString(3), $"{tag} 적용 중 — 서비스가 재시작됩니다 (약 10초)", UpdateStage.Ready))
            _jobs.HoldNewJobs = false;
    }

    /// <summary>.old 버전 문자열 (되돌릴 수 있으면), 없으면 null</summary>
    public string? OldVersionText
    {
        get
        {
            if (!File.Exists(OldPath)) return null;
            try { return UpdateFeed.ParseVersion(FileVersionInfo.GetVersionInfo(OldPath).ProductVersion?.Split('+')[0])?.ToString(3) ?? "이전 버전"; }
            catch { return "이전 버전"; }
        }
    }

    public async Task RollbackAsync()
    {
        string old; UpdateStage prev;
        lock (_gate)
        {
            if (Stage is UpdateStage.Applying or UpdateStage.Checking or UpdateStage.Downloading || OldVersionText is not { } o) return;
            if (!CanSelfUpdate) { Add("되돌리기는 Windows 정식/테스트 빌드에서만"); return; }
            if (!HoldJobs()) { Add("배포 작업이 실행 중 — 끝난 뒤 되돌리세요"); return; }
            old = o; prev = Stage;
            Stage = UpdateStage.Applying;
        }
        if (!await RunBatchAsync("rollback", "", old, UpdateFeed.ParseVersion(old)?.ToString(3), $"{old} 로 되돌리는 중 — 서비스가 재시작됩니다", prev))
            _jobs.HoldNewJobs = false;
        else
            try { File.WriteAllText(SkipAutoPath, "v" + CurrentText); } catch { }   // 되돌린 버전을 AutoApply 가 곧바로 다시 적용하지 않게
    }

    /// <summary>
    /// 새 작업 시작을 먼저 막고 그 상태에서 실행 중 작업을 다시 본다 (적용 직전에 예약·즉시 작업이 시작돼 재시작에 끊기는 것 방지).
    /// 실행 중이면 막은 것을 풀고 false
    /// </summary>
    bool HoldJobs()
    {
        _jobs.HoldNewJobs = true;
        if (!_jobs.IsBusy) return true;
        _jobs.HoldNewJobs = false;
        return false;
    }

    /// <summary>배치가 시작됐다는 표시를 이 시간 안에 못 보면 서버를 멈추지 않는다</summary>
    static readonly TimeSpan BatchStartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 배치를 띄우고, 배치가 시작했다는 표시(run-started.txt)를 확인한 뒤에만 서버를 멈춘다.
    /// 못 띄웠거나 표시가 없으면 멈추지 않고 이전 단계(prev)로 돌아가 false (호출한 쪽이 HoldNewJobs 해제)
    /// </summary>
    async Task<bool> RunBatchAsync(string mode, string stageDir, string tag, string? expectedVersion, string msg, UpdateStage prev)
    {
        var pending = Path.Combine(_dir, "pending.txt");
        try
        {
            Directory.CreateDirectory(_dir);
            var bat = Path.Combine(_dir, SelfUpdateScript.FileName);
            File.WriteAllText(bat, SelfUpdateScript.Text.ReplaceLineEndings("\r\n"), System.Text.Encoding.ASCII);   // cmd 는 UTF-8 BOM 없는 한글을 깨뜨리므로 스크립트는 ASCII 만
            File.WriteAllText(Path.Combine(_dir, "exclude.txt"), "appsettings.json\r\n");
            File.WriteAllText(pending, $"{mode} {tag}");
            var health = SelfUpdateScript.HealthUrl(_server.Features.Get<IServerAddressesFeature>()?.Addresses ?? []);
            var args = SelfUpdateScript.Arguments(mode, Environment.ProcessId, AppContext.BaseDirectory, stageDir, _opt.ServiceName,
                                                  _runAsService, health, expectedVersion);
            SelfUpdateScript.PrepareStart(_dir);
            var psi = new ProcessStartInfo("cmd.exe", SelfUpdateScript.LaunchArguments(bat, args))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _dir };
            using (Process.Start(psi)) { }
            Set(UpdateStage.Applying, "교체 배치 시작 확인 중…");
            if (!await SelfUpdateScript.WaitStartedAsync(_dir, BatchStartTimeout))
                throw new InvalidOperationException($"교체 배치가 {BatchStartTimeout.TotalSeconds:0}초 안에 시작되지 않음 — 서버는 그대로 실행합니다 ({SelfUpdateScript.FileName}, apply.log 확인)");
            Set(UpdateStage.Applying, msg);
            _log.LogWarning("update {Mode}: {Tag} ({Args})", mode, tag, args);
            _ = Task.Delay(1500).ContinueWith(_ => _life.StopApplication());
            return true;
        }
        catch (Exception e)
        {
            TryDelete(pending);   // 실행되지 않은 결과를 다음 기동 때 잘못 보고하지 않게
            Set(HasStaged ? UpdateStage.Ready : prev is UpdateStage.Applying ? UpdateStage.Idle : prev, (mode == "apply" ? "적용 실패: " : "되돌리기 실패: ") + e.Message);
            return false;
        }
    }

    void CleanupLeftovers()
    {
        try
        {
            if (Directory.Exists(_dir)) foreach (var f in Directory.GetFiles(_dir, "*.partial")) TryDelete(f);
            var pending = Path.Combine(_dir, "pending.txt");
            // 되돌리기 직후 기동에서는 .rollback 을 남긴다 — 이 프로세스가 기동 확인에 실패하면 배치가 그것으로 복구한다
            if (!File.Exists(pending)) { TryDelete(ExePath + ".rollback"); return; }
            var p = File.ReadAllText(pending).Trim().Split(' ', 2);
            File.Delete(pending);
            var tag = p.Length > 1 ? p[1].Trim() : "";
            var ver = UpdateFeed.ParseVersion(tag);
            var ok = ver is not null && ver.ToString(3) == CurrentText;
            Add(p[0] switch
            {
                "rollback" => ok ? $"{tag} 로 되돌림" : $"되돌리기 후에도 버전이 {CurrentText} — apply.log 확인",
                "apply" => ok ? $"업데이트 {tag} 적용됨 (현재 {CurrentText}, 이전 {OldVersionText ?? "-"} 는 되돌리기 가능)" : $"업데이트 {tag} 적용 후에도 버전이 {CurrentText} — apply.log 확인",
                "apply-reverted" => $"업데이트 {tag} 이(가) {SelfUpdateScript.HealthTimeoutSec}초 안에 응답하지 않아 이전 버전 {CurrentText} 로 자동 되돌림 (실패한 exe 는 AgvFotaGateway.exe.bad) — apply.log 확인",
                "rollback-reverted" => $"되돌리기 대상 {tag} 이(가) {SelfUpdateScript.HealthTimeoutSec}초 안에 응답하지 않아 원래 버전 {CurrentText} 로 복구 — apply.log 확인",
                "apply-unhealthy" or "rollback-unhealthy" => $"{tag} 이(가) 응답하지 않았지만 되돌릴 이전 exe 가 없음 (현재 {CurrentText}) — apply.log 확인",
                "apply-failed" => $"업데이트 {tag} 파일 교체 실패 — {CurrentText} 유지, apply.log 확인",
                "rollback-failed" => $"되돌리기 실패 — {CurrentText} 유지, apply.log 확인",
                _ => $"알 수 없는 업데이트 결과 '{p[0]}' — apply.log 확인",
            });
            if (ver is null) return;
            var stage = Path.Combine(_dir, "v" + ver.ToString(3));
            if (SelfUpdateScript.SkipsAutoApply(p[0]))
            {
                // 교체·기동에 실패한 버전은 자동 적용에서 뺀다 (AutoApply 가 CheckIntervalMin 마다 같은 실패·재시작을 되풀이하지 않게).
                // 교체 실패(apply-failed)는 받아 둔 파일을 남겨 원인을 고친 뒤 화면에서 다시 적용할 수 있게 한다
                File.WriteAllText(SkipAutoPath, "v" + ver.ToString(3));
                if (p[0] != "apply-failed" && Directory.Exists(stage)) Directory.Delete(stage, true);
            }
            else if (p[0] == "apply" && ok)
            {
                TryDelete(SkipAutoPath);
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
            }
        }
        catch (Exception e) { _log.LogDebug("cleanup: {Msg}", e.Message); }
    }

    /// <summary>재시작 전에 받아 둔 새 버전(data/update/vX.Y.Z)이 있으면 '적용 대기'로 복원하고, 현재보다 오래된 폴더는 지운다</summary>
    void RestoreStaged()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var dirs = Directory.GetDirectories(_dir, "v*").Where(d => File.Exists(Path.Combine(d, "AgvFotaGateway", "AgvFotaGateway.exe"))
                                                                     || File.Exists(Path.Combine(d, "AgvFotaGateway.exe"))).ToList();
            var best = UpdateFeed.NewestStagedTag(dirs.Select(d => Path.GetFileName(d)), Current);
            foreach (var d in Directory.GetDirectories(_dir, "v*"))
                if (Path.GetFileName(d) != best) try { Directory.Delete(d, true); } catch { }
            if (best is null) return;
            var exe = Directory.GetFiles(Path.Combine(_dir, best), "AgvFotaGateway.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe is null) return;
            StagedDir = Path.GetDirectoryName(exe)!;
            StagedTag = best;
            Set(UpdateStage.Ready, $"{best} 받아 둠 (재시작 전에 받은 파일) — 적용 대기");
        }
        catch (Exception e) { _log.LogDebug("restore staged: {Msg}", e.Message); }
    }

    string? ReadSkipAuto() { try { return File.Exists(SkipAutoPath) ? File.ReadAllText(SkipAutoPath).Trim() : null; } catch { return null; } }

    async Task<string?> ReadTextAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (!UpdateFeed.IsAllowedSource(url)) throw new InvalidOperationException($"허용되지 않은 주소: {url} (https://github.com · api.github.com 만)");
        // api.github.com 자산은 octet-stream 을 요청해야 내용(메타데이터 JSON 이 아니라)을 준다
        using var req = NewRequest(url, UpdateFeed.IsGitHubApi(url) ? "application/octet-stream" : "*/*");
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    void Set(UpdateStage s, string msg) { lock (_gate) { Stage = s; Message = msg; } Add(msg); }
    void Add(string line)
    {
        lock (_logLines) { _logLines.Add($"{DateTime.Now:MM-dd HH:mm:ss} {line}"); if (_logLines.Count > 100) _logLines.RemoveAt(0); }
        _log.LogInformation("update: {Line}", line);
        try { Changed?.Invoke(); } catch { }
    }
    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
