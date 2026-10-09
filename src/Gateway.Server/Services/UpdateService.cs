using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using Gateway.Core.Update;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

public enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Ready, Applying, Error }

/// <summary>
/// 서버 자체 업데이트 (사내망 대응). WPF 프로그램의 자동 업데이트(AmrMapEditor Updater)와 같은 규칙:
///  - 확인: {BaseUrl}/releases/latest/download/AgvFotaGateway.json → releases/latest 리다이렉트 → api.github.com 순서
///    (사내망은 api.github.com 이 막히고 github.com 만 열림)
///  - 받기: 프로그램이 직접 받고(.partial) SHA-256 이 맞을 때만 채택
///  - 교체: 실행 중인 exe 는 덮어쓸 수 없으므로 서비스를 멈춘 뒤 현재 exe → .old, 새 파일 복사, 다시 시작 (apply-update.bat)
///  - 되돌리기: .old 가 있으면 한 세대 되돌림
///  - 채널: release(정식) · dev(main 테스트, 정식만 받음) · local(로컬 빌드, 교체 안 함)
/// appsettings.json 과 data/ 는 덮어쓰지 않는다.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    readonly UpdateOptions _opt;
    readonly JobService _jobs;
    readonly IHostApplicationLifetime _life;
    readonly ILogger<UpdateService> _log;
    readonly string _dir;
    readonly HttpClient _http;
    readonly List<string> _logLines = new();

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
    public string Message { get; private set; } = "";
    public DateTime? LastCheck { get; private set; }
    public IReadOnlyList<string> Log { get { lock (_logLines) return _logLines.ToArray(); } }
    public bool Enabled => _opt.Enabled;
    public string BaseUrl => _opt.BaseUrl.TrimEnd('/');
    public string ManifestUrl => string.IsNullOrEmpty(_opt.ManifestUrl) ? $"{BaseUrl}/releases/latest/download/{UpdateFeed.ManifestName}" : _opt.ManifestUrl;
    /// <summary>브라우저로 직접 받는 고정 주소 (항상 최신)</summary>
    public string DownloadUrl => $"{BaseUrl}/releases/latest/download/{UpdateFeed.AssetName}";
    public string ReleasesPage => $"{BaseUrl}/releases";
    public event Action? Changed;

    static string ExePath => Path.Combine(AppContext.BaseDirectory, "AgvFotaGateway.exe");
    static string OldPath => ExePath + ".old";

    public UpdateService(IOptions<UpdateOptions> opt, IOptions<GatewayOptions> gw, JobService jobs, IHostApplicationLifetime life, ILogger<UpdateService> log)
    {
        _opt = opt.Value; _jobs = jobs; _life = life; _log = log;
        _dir = Path.Combine(gw.Value.DataDir, "update");
        var asm = Assembly.GetEntryAssembly()!;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var parts = info.Split('+', 2);
        Current = UpdateFeed.ParseVersion(parts[0]) ?? UpdateFeed.ParseVersion(asm.GetName().Version?.ToString()) ?? new Version(0, 0, 0);
        CurrentText = Current.ToString(3);
        Commit = parts.Length > 1 && parts[1].Length >= 7 ? parts[1][..7] : null;
        Channel = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildChannel")?.Value ?? "local";
        // 시스템 프록시 + 사내 프록시가 Windows 로그인 인증을 요구해도 통과
        _http = new HttpClient(new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials, AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgvFotaGateway/" + CurrentText);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        CleanupLeftovers();
        if (!_opt.Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _opt.FirstCheckDelaySec)), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = await CheckAsync(ct);
                if (info is not null && _opt.AutoApply && CanSelfUpdate)
                {
                    if (Stage == UpdateStage.Available) await DownloadAsync(ct);
                    if (Stage == UpdateStage.Ready) await ApplyAsync();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { Add("자동 확인 실패: " + e.Message); }
            if (_opt.CheckIntervalMin <= 0) return;
            try { await Task.Delay(TimeSpan.FromMinutes(_opt.CheckIntervalMin), ct); } catch (OperationCanceledException) { return; }
        }
    }

    // ───── 확인: 매니페스트 → 리다이렉트 → API ─────
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        Set(UpdateStage.Checking, "확인 중…");
        UpdateInfo? info = null;
        Exception? first = null;
        foreach (var source in new Func<CancellationToken, Task<UpdateInfo?>>[] { FromManifestAsync, FromRedirectAsync, FromApiAsync })
        {
            try { info = await source(ct); if (info is not null) break; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { first ??= e; }
        }
        LastCheck = DateTime.UtcNow;
        if (info is null)
        {
            Set(UpdateStage.Error, first is null
                ? $"정식 릴리스가 없거나 접근할 수 없음 ({ManifestUrl}). 비공개 저장소면 공개로 바꾸거나 사내 BaseUrl 또는 GitHubToken 을 설정"
                : "확인 실패: " + first.Message);
            return null;
        }
        Latest = info;
        if (!UpdateFeed.IsNewer(info.Version, Current)) { Set(UpdateStage.UpToDate, $"최신 버전입니다 ({CurrentText}, {ChannelName})"); return null; }
        if (StagedDir is not null && Directory.Exists(StagedDir) && StagedDir.Contains(info.Tag)) { Set(UpdateStage.Ready, $"{info.Tag} 받아 둠 — 적용 대기"); return info; }
        Set(UpdateStage.Available, $"새 버전 {info.Tag} ({info.AssetName}, {info.Size / 1024 / 1024}MB){(info.Sha256 is null ? " · SHA-256 정보 없음" : "")}");
        return info;
    }

    async Task<UpdateInfo?> FromManifestAsync(CancellationToken ct)
    {
        var text = await ReadTextAsync(ManifestUrl, ct);
        return text is null ? null : UpdateFeed.ParseManifest(text, BaseUrl);
    }

    async Task<UpdateInfo?> FromRedirectAsync(CancellationToken ct)
    {
        if (!BaseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
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
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(_opt.GitHubToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.GitHubToken);
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new InvalidOperationException("GitHub API 횟수 제한 — 1시간 뒤 다시");
        res.EnsureSuccessStatusCode();
        return UpdateFeed.ParseGitHubRelease(await res.Content.ReadAsStringAsync(ct));
    }

    // ───── 받기: .partial + SHA-256 ─────
    public async Task DownloadAsync(CancellationToken ct = default)
    {
        var info = Latest;
        if (info is null || Stage is not (UpdateStage.Available or UpdateStage.Error)) return;
        Set(UpdateStage.Downloading, $"{info.AssetName} 받는 중…");
        try
        {
            Directory.CreateDirectory(_dir);
            var expected = info.Sha256 ?? (await ReadTextAsync(info.Sha256Url, ct) is { } s ? UpdateFeed.ParseShaFile(s) : null)
                           ?? throw new InvalidOperationException("릴리스에 검증용 SHA-256 정보가 없음 (매니페스트 또는 .sha256 파일)");
            var zip = Path.Combine(_dir, $"AgvFotaGateway_{info.Version.ToString(3)}.zip");
            var part = zip + ".partial";
            try
            {
                if (info.AssetUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(new Uri(info.AssetUrl).LocalPath, part, true);
                }
                else
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, info.AssetUrl);
                    req.Headers.Accept.ParseAdd("application/octet-stream");
                    if (!string.IsNullOrEmpty(_opt.GitHubToken) && info.AssetUrl.Contains("api.github.com")) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.GitHubToken);
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
            TryDelete(zip);
            Set(UpdateStage.Ready, $"{info.Tag} 받음 (SHA-256 확인) — 적용하면 서비스가 재시작됩니다");
        }
        catch (Exception e) when (e is not OperationCanceledException) { Set(UpdateStage.Error, "받기 실패: " + e.Message); }
    }

    // ───── 교체 · 되돌리기 (Windows, 배치) ─────
    public Task ApplyAsync()
    {
        if (Stage != UpdateStage.Ready || StagedDir is null) return Task.CompletedTask;
        if (!CanSelfUpdate) { Set(UpdateStage.Ready, Channel == "local" ? "로컬 빌드는 자동 교체하지 않음 (bin 폴더)" : "자동 교체는 Windows 에서만"); return Task.CompletedTask; }
        if (_jobs.List().Any(j => j.State == JobState.Running)) { Set(UpdateStage.Ready, "배포 작업이 실행 중 — 끝난 뒤 적용하세요"); return Task.CompletedTask; }
        return RunBatchAsync("apply", StagedDir, Latest?.Tag ?? "", $"{Latest?.Tag} 적용 중 — 서비스가 재시작됩니다 (약 10초)");
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

    public Task RollbackAsync()
    {
        if (OldVersionText is null) return Task.CompletedTask;
        if (!CanSelfUpdate) { Add("되돌리기는 Windows 정식/테스트 빌드에서만"); return Task.CompletedTask; }
        if (_jobs.List().Any(j => j.State == JobState.Running)) { Add("배포 작업이 실행 중 — 끝난 뒤 되돌리세요"); return Task.CompletedTask; }
        return RunBatchAsync("rollback", "", OldVersionText, $"{OldVersionText} 로 되돌리는 중 — 서비스가 재시작됩니다");
    }

    Task RunBatchAsync(string mode, string stageDir, string tag, string msg)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var bat = Path.Combine(_dir, "apply-update.bat");
            File.WriteAllText(bat, ApplyScript, System.Text.Encoding.ASCII);   // cmd 는 UTF-8 BOM 없는 한글을 깨뜨리므로 스크립트는 ASCII 만
            File.WriteAllText(Path.Combine(_dir, "exclude.txt"), "appsettings.json\r\n");
            File.WriteAllText(Path.Combine(_dir, "pending.txt"), $"{mode} {tag}");
            var app = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var psi = new ProcessStartInfo("cmd.exe", $"/c start \"\" /min \"{bat}\" {mode} {Environment.ProcessId} \"{app}\" \"{stageDir}\" {_opt.ServiceName}")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _dir };
            Process.Start(psi);
            Set(UpdateStage.Applying, msg);
            _log.LogWarning("update {Mode}: {Tag}", mode, tag);
            _ = Task.Delay(1500).ContinueWith(_ => _life.StopApplication());
        }
        catch (Exception e) { Set(UpdateStage.Error, "적용 실패: " + e.Message); }
        return Task.CompletedTask;
    }

    void CleanupLeftovers()
    {
        try
        {
            TryDelete(ExePath + ".rollback");
            if (Directory.Exists(_dir)) foreach (var f in Directory.GetFiles(_dir, "*.partial")) TryDelete(f);
            var pending = Path.Combine(_dir, "pending.txt");
            if (!File.Exists(pending)) return;
            var p = File.ReadAllText(pending).Trim().Split(' ', 2);
            File.Delete(pending);
            var tag = p.Length > 1 ? p[1] : "";
            var ok = UpdateFeed.ParseVersion(tag) is { } v && v.ToString(3) == CurrentText;
            Add(p[0] == "rollback"
                ? (ok ? $"{tag} 로 되돌림" : $"되돌리기 후에도 버전이 {CurrentText} — apply.log 확인")
                : (ok ? $"업데이트 {tag} 적용됨 (현재 {CurrentText}, 이전 {OldVersionText ?? "-"} 는 되돌리기 가능)" : $"업데이트 {tag} 적용 후에도 버전이 {CurrentText} — apply.log 확인"));
            var stage = Path.Combine(_dir, tag);
            if (ok && Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        catch (Exception e) { _log.LogDebug("cleanup: {Msg}", e.Message); }
    }

    async Task<string?> ReadTextAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = new Uri(url).LocalPath;
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        }
        using var res = await _http.GetAsync(url, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    void Set(UpdateStage s, string msg) { Stage = s; Message = msg; Add(msg); }
    void Add(string line)
    {
        lock (_logLines) { _logLines.Add($"{DateTime.Now:MM-dd HH:mm:ss} {line}"); if (_logLines.Count > 100) _logLines.RemoveAt(0); }
        _log.LogInformation("update: {Line}", line);
        try { Changed?.Invoke(); } catch { }
    }
    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    // %1 = apply|rollback, %2 = 현재 PID, %3 = 앱 폴더, %4 = 새 파일 폴더(apply), %5 = 서비스 이름
    const string ApplyScript = """
        @echo off
        setlocal
        set MODE=%1
        set PID=%2
        set APP=%~3
        set STAGE=%~4
        set SVC=%5
        set LOG=%~dp0apply.log
        set EXE=%APP%\AgvFotaGateway.exe
        echo [%date% %time%] %MODE% %STAGE% -^> %APP% >> "%LOG%"
        set WASSVC=0
        sc query %SVC% 2>nul | findstr /C:"RUNNING" >nul && (set WASSVC=1 & sc stop %SVC% >> "%LOG%" 2>&1)
        set /a N=0
        :wait
        tasklist /FI "PID eq %PID%" /NH 2>nul | findstr /B /I /C:"AgvFotaGateway" >nul || goto swap
        set /a N+=1
        if %N% GEQ 60 (echo [%date% %time%] process %PID% still running, killing >> "%LOG%" & taskkill /PID %PID% /F >> "%LOG%" 2>&1 & goto swap)
        timeout /t 1 /nobreak >nul
        goto wait
        :swap
        timeout /t 1 /nobreak >nul
        if "%MODE%"=="rollback" (
            if exist "%EXE%.rollback" del /f /q "%EXE%.rollback"
            ren "%EXE%" AgvFotaGateway.exe.rollback >> "%LOG%" 2>&1
            ren "%EXE%.old" AgvFotaGateway.exe >> "%LOG%" 2>&1 || (ren "%EXE%.rollback" AgvFotaGateway.exe & echo [%date% %time%] rollback failed >> "%LOG%")
        ) else (
            if exist "%EXE%.old" del /f /q "%EXE%.old"
            ren "%EXE%" AgvFotaGateway.exe.old >> "%LOG%" 2>&1
            xcopy "%STAGE%\*" "%APP%\" /E /Y /I /EXCLUDE:%~dp0exclude.txt >> "%LOG%" 2>&1
            if not exist "%EXE%" (echo [%date% %time%] copy failed, restoring .old >> "%LOG%" & ren "%EXE%.old" AgvFotaGateway.exe)
        )
        if "%WASSVC%"=="1" (
            sc start %SVC% >> "%LOG%" 2>&1
        ) else (
            start "" /D "%APP%" "%EXE%"
        )
        echo [%date% %time%] done >> "%LOG%"
        endlocal
        """;
}
