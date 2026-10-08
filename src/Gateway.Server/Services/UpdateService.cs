using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using Gateway.Core.Update;
using Gateway.Server.Options;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

public enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Ready, Applying, Error }

/// <summary>
/// 서버 자체 업데이트 — WPF 프로그램의 자동 업데이트와 같은 흐름.
///  1. 확인: GitHub Releases 최신 태그(또는 사내 latest.json) 의 버전이 현재보다 높은가
///  2. 다운로드: zip 을 data/update/ 에 받고 풀어서 exe 가 있는지 확인
///  3. 적용: 배포 작업이 실행 중이 아니면 apply-update.bat 을 띄우고 종료 → 배치가 파일을 바꾸고 서비스를 다시 시작
/// appsettings.json 과 data/ 는 덮어쓰지 않는다.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    readonly UpdateOptions _opt;
    readonly JobService _jobs;
    readonly IHostApplicationLifetime _life;
    readonly ILogger<UpdateService> _log;
    readonly string _dir;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    readonly List<string> _logLines = new();

    public Version Current { get; }
    public string CurrentText { get; }
    public UpdateStage Stage { get; private set; }
    public UpdateInfo? Latest { get; private set; }
    public string? StagedDir { get; private set; }
    public string Message { get; private set; } = "";
    public DateTime? LastCheck { get; private set; }
    public IReadOnlyList<string> Log { get { lock (_logLines) return _logLines.ToArray(); } }
    public bool Enabled => _opt.Enabled;
    public string SourceText => _opt.Source.Equals("Url", StringComparison.OrdinalIgnoreCase) ? _opt.FeedUrl ?? "" : $"GitHub {_opt.GitHubRepo}";
    public event Action? Changed;

    public UpdateService(IOptions<UpdateOptions> opt, IOptions<GatewayOptions> gw, JobService jobs, IHostApplicationLifetime life, ILogger<UpdateService> log)
    {
        _opt = opt.Value; _jobs = jobs; _life = life; _log = log;
        _dir = Path.Combine(gw.Value.DataDir, "update");
        var asm = Assembly.GetEntryAssembly()!;
        CurrentText = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? asm.GetName().Version?.ToString() ?? "0.0.0";
        Current = UpdateFeed.ParseVersion(CurrentText) ?? new Version(0, 0, 0);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgvFotaGateway/" + CurrentText);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        CleanupAfterApply();
        if (!_opt.Enabled || _opt.CheckIntervalMin <= 0) return;
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = await CheckAsync(ct);
                if (info is not null && _opt.AutoApply)
                {
                    await DownloadAsync(ct);
                    if (Stage == UpdateStage.Ready) await ApplyAsync();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { Add("자동 확인 실패: " + e.Message); }
            await Task.Delay(TimeSpan.FromMinutes(_opt.CheckIntervalMin), ct);
        }
    }

    /// <summary>최신 버전 확인. 새 버전이면 UpdateInfo, 아니면 null</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        Set(UpdateStage.Checking, "확인 중…");
        try
        {
            UpdateInfo? info;
            if (_opt.Source.Equals("Url", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(_opt.FeedUrl)) throw new InvalidOperationException("Update.FeedUrl 이 비어 있음");
                info = UpdateFeed.ParseLatestJson(await _http.GetStringAsync(_opt.FeedUrl, ct));
            }
            else
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_opt.GitHubRepo}/releases/latest");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                if (!string.IsNullOrEmpty(_opt.GitHubToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.GitHubToken);
                using var res = await _http.SendAsync(req, ct);
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("릴리스가 없거나 비공개 저장소 (Update.GitHubToken 필요)");
                res.EnsureSuccessStatusCode();
                info = UpdateFeed.ParseGitHubRelease(await res.Content.ReadAsStringAsync(ct), _opt.AssetSuffix);
            }
            LastCheck = DateTime.UtcNow;
            if (info is null) { Set(UpdateStage.Error, $"릴리스에 {_opt.AssetSuffix} 자산이 없음"); return null; }
            Latest = info;
            if (!UpdateFeed.IsNewer(info.Version, Current)) { Set(UpdateStage.UpToDate, $"최신 버전입니다 ({CurrentText})"); return null; }
            if (StagedDir is not null && Directory.Exists(StagedDir) && Path.GetFileName(StagedDir) == info.Tag) { Set(UpdateStage.Ready, $"{info.Tag} 다운로드됨 — 적용 대기"); return info; }
            Set(UpdateStage.Available, $"새 버전 {info.Tag} ({info.AssetName}, {info.Size / 1024 / 1024}MB)");
            return info;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastCheck = DateTime.UtcNow;
            Set(UpdateStage.Error, "확인 실패: " + e.Message);
            return null;
        }
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        var info = Latest;
        if (info is null || Stage is not (UpdateStage.Available or UpdateStage.Error)) return;
        Set(UpdateStage.Downloading, $"{info.AssetName} 다운로드 중…");
        try
        {
            Directory.CreateDirectory(_dir);
            var zip = Path.Combine(_dir, info.AssetName);
            using (var req = new HttpRequestMessage(HttpMethod.Get, info.AssetUrl))
            {
                req.Headers.Accept.ParseAdd("application/octet-stream");
                if (!string.IsNullOrEmpty(_opt.GitHubToken) && info.AssetUrl.Contains("api.github.com")) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.GitHubToken);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                await using var fs = File.Create(zip);
                await res.Content.CopyToAsync(fs, ct);
            }
            var stage = Path.Combine(_dir, info.Tag);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            ZipFile.ExtractToDirectory(zip, stage);
            // zip 안에 폴더가 한 겹 있을 수 있음 → exe 가 있는 폴더를 찾는다
            var exe = Directory.GetFiles(stage, "AgvFotaGateway.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException("zip 에 AgvFotaGateway.exe 가 없음");
            StagedDir = Path.GetDirectoryName(exe)!;
            File.Delete(zip);
            Set(UpdateStage.Ready, $"{info.Tag} 다운로드 완료 — 적용하면 서비스가 재시작됩니다");
        }
        catch (Exception e) when (e is not OperationCanceledException) { Set(UpdateStage.Error, "다운로드 실패: " + e.Message); }
    }

    /// <summary>실행 중인 배포 작업이 없으면 교체 배치를 띄우고 이 프로세스를 종료한다</summary>
    public Task ApplyAsync()
    {
        if (Stage != UpdateStage.Ready || StagedDir is null) return Task.CompletedTask;
        if (_jobs.List().Any(j => j.State == JobState.Running)) { Set(UpdateStage.Ready, "배포 작업이 실행 중 — 끝난 뒤 적용하세요"); return Task.CompletedTask; }
        if (!OperatingSystem.IsWindows()) { Set(UpdateStage.Ready, "자동 적용은 Windows 에서만 지원 (수동: 파일 교체 후 재시작)"); return Task.CompletedTask; }
        try
        {
            var app = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var bat = Path.Combine(_dir, "apply-update.bat");
            File.WriteAllText(bat, ApplyScript, System.Text.Encoding.Default);
            File.WriteAllText(Path.Combine(_dir, "exclude.txt"), "appsettings.json\r\n");
            File.WriteAllText(Path.Combine(_dir, "pending.txt"), Latest?.Tag ?? "");
            var psi = new ProcessStartInfo("cmd.exe", $"/c start \"\" /min \"{bat}\" {Environment.ProcessId} \"{app}\" \"{StagedDir}\" {_opt.ServiceName}")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _dir,
            };
            Process.Start(psi);
            Set(UpdateStage.Applying, $"{Latest?.Tag} 적용 중 — 서비스가 재시작됩니다 (약 10초)");
            _log.LogWarning("update apply: {Tag} from {Dir}", Latest?.Tag, StagedDir);
            _ = Task.Delay(1500).ContinueWith(_ => _life.StopApplication());
        }
        catch (Exception e) { Set(UpdateStage.Error, "적용 실패: " + e.Message); }
        return Task.CompletedTask;
    }

    void CleanupAfterApply()
    {
        try
        {
            var pending = Path.Combine(_dir, "pending.txt");
            if (!File.Exists(pending)) return;
            var tag = File.ReadAllText(pending).Trim();
            File.Delete(pending);
            var ok = UpdateFeed.ParseVersion(tag) is { } v && !UpdateFeed.IsNewer(v, Current);
            Add(ok ? $"업데이트 {tag} 적용됨 (현재 {CurrentText})" : $"업데이트 {tag} 적용 후에도 버전이 {CurrentText} — apply.log 확인");
            var stage = Path.Combine(_dir, tag);
            if (ok && Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        catch (Exception e) { _log.LogDebug("cleanup: {Msg}", e.Message); }
    }

    void Set(UpdateStage s, string msg) { Stage = s; Message = msg; Add(msg); }
    void Add(string line)
    {
        lock (_logLines) { _logLines.Add($"{DateTime.Now:MM-dd HH:mm:ss} {line}"); if (_logLines.Count > 100) _logLines.RemoveAt(0); }
        _log.LogInformation("update: {Line}", line);
        try { Changed?.Invoke(); } catch { }
    }

    // %1 = 현재 PID, %2 = 앱 폴더, %3 = 새 파일 폴더, %4 = 서비스 이름
    const string ApplyScript = """
        @echo off
        setlocal
        set PID=%1
        set APP=%~2
        set STAGE=%~3
        set SVC=%4
        set LOG=%~dp0apply.log
        echo [%date% %time%] apply %STAGE% -^> %APP% >> "%LOG%"
        set WASSVC=0
        sc query %SVC% 2>nul | findstr /C:"RUNNING" >nul && (set WASSVC=1 & sc stop %SVC% >> "%LOG%" 2>&1)
        set /a N=0
        :wait
        tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul || goto copy
        set /a N+=1
        if %N% GEQ 60 (echo [%date% %time%] process %PID% still running, killing >> "%LOG%" & taskkill /PID %PID% /F >> "%LOG%" 2>&1 & goto copy)
        timeout /t 1 /nobreak >nul
        goto wait
        :copy
        timeout /t 1 /nobreak >nul
        xcopy "%STAGE%\*" "%APP%\" /E /Y /I /EXCLUDE:%~dp0exclude.txt >> "%LOG%" 2>&1
        if errorlevel 1 echo [%date% %time%] xcopy error %errorlevel% >> "%LOG%"
        if "%WASSVC%"=="1" (
            sc start %SVC% >> "%LOG%" 2>&1
        ) else (
            start "" /D "%APP%" "%APP%\AgvFotaGateway.exe"
        )
        echo [%date% %time%] done >> "%LOG%"
        endlocal
        """;
}
