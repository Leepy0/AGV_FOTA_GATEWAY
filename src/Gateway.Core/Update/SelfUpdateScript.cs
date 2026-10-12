using System.Net;
using System.Text.RegularExpressions;

namespace Gateway.Core.Update;

/// <summary>
/// 서버 자체 업데이트의 교체 배치(apply-update.bat)와 그 인자.
/// 서비스/콘솔 구분은 실행 중인 프로그램이 알려 준다(sc query 시점에 의존하지 않음).
/// 다시 띄운 뒤 60초 안에 /api/health 가 기대 버전을 돌려주지 않으면 이전 exe 로 자동 되돌린다.
/// 결과는 pending.txt 에 남기고(실패 시 "{mode}-reverted 버전" 또는 "{mode}-failed 버전") 다음 기동 때 화면 기록에 보인다.
/// 실행: <see cref="LaunchArguments"/> 로 cmd.exe 를 거쳐 띄우고, 배치가 첫 줄에서 표(<see cref="TicketFile"/>)를 집어
/// <see cref="StartedFile"/> 로 바꾼 것을 <see cref="WaitStartedAsync"/> 로 확인한 뒤에만 서버를 멈춘다.
/// </summary>
public static partial class SelfUpdateScript
{
    public const string FileName = "apply-update.bat";
    /// <summary>새 버전이 이 시간 안에 응답해야 함 (초)</summary>
    public const int HealthTimeoutSec = 60;

    /// <summary>실행 전에 서버가 쓰는 표. 배치가 첫 줄에서 <see cref="StartedFile"/> 로 이름을 바꿔 '시작함'을 알린다</summary>
    public const string TicketFile = "run-ticket.txt";
    public const string StartedFile = "run-started.txt";

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionRx();

    /// <summary>
    /// 배치 인자: %1 apply|rollback, %2 현재 PID, %3 앱 폴더, %4 새 파일 폴더(apply), %5 서비스 이름,
    /// %6 svc|console, %7 확인 주소(없으면 -), %8 기대 버전 x.y.z(모르면 -)
    /// </summary>
    public static string Arguments(string mode, int pid, string appDir, string stageDir, string serviceName, bool asService, string? healthUrl, string? expectedVersion)
    {
        if (mode is not ("apply" or "rollback")) throw new ArgumentException("mode", nameof(mode));
        string ver = expectedVersion is { } v && VersionRx().IsMatch(v) ? v : "-";
        string health = string.IsNullOrEmpty(healthUrl) ? "-" : healthUrl;
        return $"{mode} {pid} \"{appDir.TrimEnd('\\', '/')}\" \"{stageDir.TrimEnd('\\', '/')}\" \"{serviceName}\" {(asService ? "svc" : "console")} \"{health}\" {ver}";
    }

    /// <summary>
    /// cmd.exe /c 에 넘길 실행 줄. 배치 파일을 START 에 바로 주면 START 가 cmd /K 로 띄우고, /K 뒤 문자열이 따옴표로 시작하며
    /// 따옴표가 2개를 넘으면 cmd 가 처음·마지막 따옴표를 지워 경로가 깨질 수 있다. 그래서 START 로는 cmd.exe(실행 파일)를 띄우고
    /// 그 cmd 가 call 로 배치를 부른다 — 바깥(/c start …)·안쪽(/c call …) 모두 따옴표로 시작하지 않아 따옴표 제거 규칙에 걸리지 않고,
    /// /K 가 아니므로 배치가 끝나면 창(프로세스)도 닫힌다. /d = AutoRun 무시
    /// </summary>
    public static string LaunchArguments(string batPath, string args) => $"/c start \"\" /min cmd.exe /d /c call \"{batPath}\" {args}";

    /// <summary>실행 직전: 지난 표시를 지우고 새 표를 쓴다</summary>
    public static void PrepareStart(string dir)
    {
        File.Delete(Path.Combine(dir, StartedFile));
        File.WriteAllText(Path.Combine(dir, TicketFile), "");
    }

    /// <summary>
    /// 배치가 표를 집었는지(시작했는지) 최대 timeout 동안 본다. 시간 안에 못 집었으면 표를 거둬들여 늦게 뜬 배치가
    /// 첫 줄에서 그냥 끝나게 하고 false (서버는 멈추지 않고 그대로 둔다). 거둬들이려는 순간 배치가 집었으면 true.
    /// </summary>
    public static async Task<bool> WaitStartedAsync(string dir, TimeSpan timeout, CancellationToken ct = default)
    {
        string ticket = Path.Combine(dir, TicketFile), started = Path.Combine(dir, StartedFile);
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(started)) return true;
            await Task.Delay(100, ct);
        }
        for (int i = 0; i < 10; i++)
        {
            try { File.Move(ticket, ticket + ".void", true); return false; }   // 이름 바꾸기는 원자적 — 배치와 둘 중 하나만 성공
            catch (FileNotFoundException) { return File.Exists(started); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(100, ct); }   // 배치가 바꾸는 중 (잠김) — 다시
        }
        return File.Exists(started);
    }

    /// <summary>이 결과로 끝난 버전은 자동 적용에서 뺀다 (교체 실패·기동 확인 실패를 CheckIntervalMin 마다 되풀이하지 않게). 화면에서는 적용 가능</summary>
    public static bool SkipsAutoApply(string result) => result is "apply-reverted" or "apply-failed" or "apply-unhealthy";

    /// <summary>
    /// 서비스 등록의 ImagePath 가 가리키는 exe 가 exePath 와 같은지 (대소문자 무시). ImagePath 는 "C:\a b\x.exe" -인자 또는 C:\x.exe 형식
    /// </summary>
    public static bool SameExe(string? imagePath, string exePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(exePath)) return false;
        var s = imagePath.Trim();
        if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];
        string exe;
        if (s.StartsWith('"')) { int e = s.IndexOf('"', 1); exe = e < 0 ? s[1..] : s[1..e]; }
        else { int e = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); exe = e < 0 ? s : s[..(e + 4)]; }
        return string.Equals(exe.Replace('/', '\\').Trim(), exePath.Replace('/', '\\').Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kestrel 이 연 주소 중 http 하나로 이 PC 안에서 부를 확인 주소. 모든 주소(0.0.0.0, [::], *, +)·localhost 면 127.0.0.1.
    /// http 주소가 없으면 null (확인 생략)
    /// </summary>
    public static string? HealthUrl(IEnumerable<string> serverAddresses)
    {
        foreach (var a in serverAddresses)
        {
            var s = a.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0");
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp) continue;
            string host = u.Host;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))))
                host = "127.0.0.1";
            return $"http://{host}:{u.Port}/api/health";
        }
        return null;
    }

    /// <summary>
    /// cmd 는 UTF-8 BOM 없는 한글을 깨뜨리므로 ASCII 만 쓴다.
    /// set 은 모두 set "X=값" 형식 — set X=1 &amp; ... 은 &amp; 앞 공백까지 값에 들어간다.
    /// </summary>
    public const string Text = """
        @echo off
        rem AgvFotaGateway self-update (written by UpdateService before each run - do not edit)
        rem args: mode(apply/rollback) pid appdir stagedir service runas(svc/console) healthurl version (- = none)
        rem claim the ticket first: the server stops only after it sees run-started.txt (no ticket = withdrawn or manual run, so quit)
        ren "%~dp0run-ticket.txt" run-started.txt >nul 2>&1 || exit /b 1
        setlocal
        set "MODE=%~1"
        set "PID=%~2"
        set "APP=%~3"
        set "STAGE=%~4"
        set "SVC=%~5"
        set "RUNAS=%~6"
        set "HEALTH=%~7"
        set "WANT=%~8"
        set "LOG=%~dp0apply.log"
        set "PENDING=%~dp0pending.txt"
        set "BODY=%~dp0health.txt"
        set "EXE=%APP%\AgvFotaGateway.exe"
        cd /d "%~dp0"
        >>"%LOG%" echo [%date% %time%] %MODE% %WANT% as %RUNAS%: "%STAGE%" -^> "%APP%"
        if /i "%RUNAS%"=="svc" sc stop "%SVC%" >>"%LOG%" 2>&1
        set /a N=0
        :wait_old
        tasklist /FI "PID eq %PID%" /NH 2>nul | findstr /B /I /C:"AgvFotaGateway" >nul || goto swap_files
        set /a N+=1
        if %N% GEQ 60 (>>"%LOG%" echo [%date% %time%] process %PID% still running, killing& taskkill /PID %PID% /F >>"%LOG%" 2>&1& goto swap_files)
        timeout /t 1 /nobreak >nul
        goto wait_old

        :swap_files
        timeout /t 1 /nobreak >nul
        if /i "%MODE%"=="rollback" goto swap_rollback
        rem the current exe waits as .prev; the existing .old (rollback point) is replaced only after the new exe is in place
        if exist "%EXE%.prev" del /f /q "%EXE%.prev"
        ren "%EXE%" AgvFotaGateway.exe.prev >>"%LOG%" 2>&1
        if exist "%EXE%" (>>"%LOG%" echo [%date% %time%] cannot rename exe, update aborted& >"%PENDING%" echo apply-failed %WANT%& goto start_only)
        rem xcopy /EXCLUDE: cannot take a quoted path (fails with spaces), so use a path relative to this folder
        xcopy "%STAGE%\*" "%APP%\" /E /Y /I /EXCLUDE:exclude.txt >>"%LOG%" 2>&1
        if exist "%EXE%" goto keep_old
        >>"%LOG%" echo [%date% %time%] copy failed, restoring previous exe
        ren "%EXE%.prev" AgvFotaGateway.exe >>"%LOG%" 2>&1
        >"%PENDING%" echo apply-failed %WANT%
        goto start_only

        :keep_old
        if exist "%EXE%.old" del /f /q "%EXE%.old"
        ren "%EXE%.prev" AgvFotaGateway.exe.old >>"%LOG%" 2>&1 || >>"%LOG%" echo [%date% %time%] cannot rename .prev to .old
        goto start_check

        :swap_rollback
        if exist "%EXE%.rollback" del /f /q "%EXE%.rollback"
        ren "%EXE%" AgvFotaGateway.exe.rollback >>"%LOG%" 2>&1
        ren "%EXE%.old" AgvFotaGateway.exe >>"%LOG%" 2>&1 && goto start_check
        if not exist "%EXE%" ren "%EXE%.rollback" AgvFotaGateway.exe >>"%LOG%" 2>&1
        >>"%LOG%" echo [%date% %time%] rollback failed
        >"%PENDING%" echo rollback-failed %WANT%
        goto start_only

        :start_check
        call :startapp
        if "%HEALTH%"=="-" (>>"%LOG%" echo [%date% %time%] no http address, health check skipped& goto done)
        call :waithealth
        if not errorlevel 1 (>>"%LOG%" echo [%date% %time%] healthy: %HEALTH%& goto done)
        >>"%LOG%" echo [%date% %time%] no healthy answer from %HEALTH% within 60 s, auto rollback
        call :stopapp
        call :revert
        if errorlevel 1 (>"%PENDING%" echo %MODE%-unhealthy %WANT%) else (>"%PENDING%" echo %MODE%-reverted %WANT%)
        call :startapp
        goto done

        :start_only
        call :startapp

        :done
        >>"%LOG%" echo [%date% %time%] done
        endlocal
        exit /b 0

        rem ---- start: service via SCM, console as a plain process ----
        :startapp
        if /i "%RUNAS%"=="svc" (
            sc start "%SVC%" >>"%LOG%" 2>&1
        ) else (
            start "" /D "%APP%" "%EXE%"
        )
        exit /b 0

        rem ---- stop the new process (any AgvFotaGateway.exe), kill after 30 s ----
        :stopapp
        if /i "%RUNAS%"=="svc" sc stop "%SVC%" >>"%LOG%" 2>&1
        set /a N=0
        :stop_wait
        tasklist /FI "IMAGENAME eq AgvFotaGateway.exe" /NH 2>nul | findstr /B /I /C:"AgvFotaGateway" >nul || exit /b 0
        set /a N+=1
        if %N% GEQ 30 (taskkill /IM AgvFotaGateway.exe /F >>"%LOG%" 2>&1& timeout /t 2 /nobreak >nul& exit /b 0)
        timeout /t 1 /nobreak >nul
        goto stop_wait

        rem ---- put the previous exe back: apply -> .old, rollback -> .rollback; the failed one is kept as .bad ----
        :revert
        set "PREV=%EXE%.old"
        if /i "%MODE%"=="rollback" set "PREV=%EXE%.rollback"
        rem .prev is left only when :keep_old could not turn it into .old - then it is the exe that ran before this apply
        if /i not "%MODE%"=="rollback" if exist "%EXE%.prev" set "PREV=%EXE%.prev"
        if not exist "%PREV%" (>>"%LOG%" echo [%date% %time%] no previous exe to restore& exit /b 1)
        if exist "%EXE%.bad" del /f /q "%EXE%.bad"
        ren "%EXE%" AgvFotaGateway.exe.bad >>"%LOG%" 2>&1
        ren "%PREV%" AgvFotaGateway.exe >>"%LOG%" 2>&1 && (>>"%LOG%" echo [%date% %time%] restored previous exe& exit /b 0)
        >>"%LOG%" echo [%date% %time%] restore failed, keeping the new exe
        if not exist "%EXE%" ren "%EXE%.bad" AgvFotaGateway.exe >>"%LOG%" 2>&1
        exit /b 1

        rem ---- poll health up to 60 s: errorlevel 0 = answered ok (and the expected version) ----
        :waithealth
        set /a T=0
        :health_loop
        timeout /t 2 /nobreak >nul
        set /a T+=2
        call :probe_get && exit /b 0
        if %T% GEQ 60 exit /b 1
        goto health_loop

        :probe_get
        if exist "%BODY%" del /f /q "%BODY%"
        where curl.exe >nul 2>&1 || goto probe_ps
        curl.exe -fsS -m 3 --noproxy "*" -o "%BODY%" "%HEALTH%" >nul 2>&1
        goto probe_check
        :probe_ps
        powershell -NoProfile -NonInteractive -Command "try { [System.Net.WebRequest]::DefaultWebProxy = $null; (Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 -Uri '%HEALTH%').Content | Set-Content -Encoding ASCII -LiteralPath '%BODY%' } catch { exit 1 }" >nul 2>&1
        :probe_check
        if not exist "%BODY%" exit /b 1
        if "%WANT%"=="-" (findstr /L /C:"\"ok\":true" "%BODY%" >nul) else (findstr /L /C:"\"version\":\"%WANT%\"" "%BODY%" >nul)
        exit /b %errorlevel%
        """;
}
