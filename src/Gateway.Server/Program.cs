using Gateway.Core.Acs;
using Gateway.Core.Agv;
using Gateway.Core.Bridge;
using Gateway.Core.Groups;
using Gateway.Core.Positions;
using Gateway.Server.Components;
using Gateway.Server.Data;
using Gateway.Server.Options;
using Gateway.Server.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Diagnostics;


// Windows 서비스로 돌 때 작업 디렉터리가 system32 가 되므로 실행 파일 위치를 기준으로 잡는다
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "AgvFotaGateway");
builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.Section));
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.Section));
builder.Services.Configure<AcsApiOptions>(builder.Configuration.GetSection(AcsApiOptions.Section));
builder.Services.Configure<AcsInventoryOptions>(builder.Configuration.GetSection(AcsInventoryOptions.Section));
builder.Services.Configure<AcsMapInventoryOptions>(builder.Configuration.GetSection(AcsMapInventoryOptions.Section));
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 64L * 1024 * 1024);
builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = 64L * 1024 * 1024);
// REST 요청 본문·인자 해석 실패를 예외로 받아 /api 미들웨어가 400 + 사유로 돌려준다 (기본은 빈 400 → HTML 오류 페이지)
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

var opt = builder.Configuration.GetSection(GatewayOptions.Section).Get<GatewayOptions>() ?? new GatewayOptions();
var dataDir = Path.IsPathRooted(opt.DataDir) ? opt.DataDir : Path.Combine(AppContext.BaseDirectory, opt.DataDir);
Directory.CreateDirectory(dataDir);
builder.Services.Configure<GatewayOptions>(o => o.DataDir = dataDir);
// Windows 서비스로 돌면 콘솔 로그가 보이지 않으므로 파일 로그 추가 ({DataDir}/logs/gateway-yyyyMMdd.log, 14일 보관)
var fileLog = new FileLoggerProvider(Path.Combine(dataDir, "logs"));
builder.Logging.AddProvider(fileLog);
builder.Services.AddSingleton(fileLog);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// 데이터베이스: SQLite 파일 하나 (펌웨어 목록·작업·호기 소속·최근 위치·마지막 접속). 예전 JSON 파일은 첫 실행 때 옮긴다
builder.Services.AddSingleton(sp => new GatewayDb(dataDir));
builder.Services.AddSingleton(sp => new AgvRegistry(sp.GetRequiredService<GatewayDb>()));
builder.Services.AddSingleton<BridgeUdpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BridgeUdpService>());
builder.Services.AddSingleton<AcsRelayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AcsRelayService>());
builder.Services.AddSingleton(sp => new FirmwareStore(dataDir, sp.GetRequiredService<GatewayDb>(), sp.GetRequiredService<ILogger<FirmwareStore>>()));
builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobService>());
builder.Services.AddSingleton<TraceLog>();
builder.Services.AddSingleton<AgvPositionStore>();
builder.Services.AddHttpClient<AcsRestPublisher>();
builder.Services.AddSingleton<AcsPositionPushService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AcsPositionPushService>());
builder.Services.AddSingleton(sp => new AgvGroupStore(dataDir, sp.GetRequiredService<GatewayDb>()));
builder.Services.AddSingleton<AcsInventoryPullService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AcsInventoryPullService>());
builder.Services.AddSingleton<AcsMapInventoryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AcsMapInventoryService>());
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

// DNS 리바인딩 방어: Host 가 이 서버의 이름(IP·localhost·PC 이름·FQDN·Gateway.AllowedHostNames)이 아니면 화면·정적 파일·API 모두 403.
// 설정 화면도 같은 인스턴스로 허용 목록을 보인다
var hostGuard = new HostGuard(opt.AllowedHostNames);
builder.Services.AddSingleton(hostGuard);

var app = builder.Build();

app.Use(hostGuard.Middleware);
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
// /api: 다른 출처(사외·사내 다른 웹페이지)에서 브라우저로 보낸 변경 요청 거부, 오류는 HTML 대신 짧은 사유로
app.UseWhen(c => c.Request.Path.StartsWithSegments("/api"), b => b.Use(ApiGuard.Middleware));
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// ───── 브리지 OTA 파일 서비스 (ESP 가 HTTP GET 으로 받아 감) ─────
app.MapGet("/fw/{id}/{file}", (string id, string file, FirmwareStore fw) =>
{
    var e = fw.Get(id);
    var f = e?.Files.FirstOrDefault(x => x.FileName == file);
    return e is null || f is null ? Results.NotFound() : Results.File(fw.PathOf(e, f), "application/octet-stream", file);
});

// ───── REST API (스크립트·종단 테스트용. 화면도 같은 서비스를 직접 쓴다) ─────
var api = app.MapGroup("/api");
api.MapGet("/agvs", (AgvRegistry reg, IOptions<GatewayOptions> o) =>
    reg.Snapshot().Select(a => new
    {
        a.Id, Online = a.Online(o.Value.OfflineAfterSec), EndPoint = a.EndPoint?.ToString(), a.LastSeen,
        Fw = a.Status?.Fw, Part = a.Status?.Part, Ota = a.Status?.Ota, Rssi = a.Status?.Rssi, Cfg = a.Status?.Cfg,
        Dsp = a.Status?.Dsp, DspBoot = a.Status?.DspBoot, DspState = a.Status?.DspState, a.Status?.StagedJob, a.Status?.StagedVer,
        a.MainUp, a.MainDown, a.SeqLoss, a.LastFrame, a.StatusRaw,
        Conflict = a.InConflict(o.Value.OfflineAfterSec), ConflictEndPoint = a.ConflictEndPoint?.ToString(), a.ConflictAt, a.ConflictCount,
    }));
api.MapGet("/firmware", (FirmwareStore fw) => fw.List());
api.MapPost("/firmware", async (HttpRequest req, FirmwareStore fw) =>
{
    // multipart: kind=dsp|esp, version, project, notes, 파일 하나 (dsp: .dspfw, esp: agv_wifi_bridge.bin)
    if (!req.HasFormContentType) return Results.BadRequest("multipart/form-data 로 보내세요 (kind, version, notes, 파일)");
    IFormCollection form;
    try { form = await req.ReadFormAsync(); }
    catch (Exception ex) when (ex is InvalidDataException or IOException) { return Results.BadRequest("폼을 읽지 못했습니다: " + ex.Message); }
    var kindText = form["kind"].ToString().Trim();
    if (kindText.Length > 0 && !kindText.Equals("dsp", StringComparison.OrdinalIgnoreCase) && !kindText.Equals("esp", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("kind 는 dsp 또는 esp");
    if (form.Files.Count == 0) return Results.BadRequest("파일이 없습니다");
    var kind = kindText.Equals("esp", StringComparison.OrdinalIgnoreCase) ? FirmwareKind.Esp : FirmwareKind.Dsp;
    var files = form.Files.Select(f => (f.Name, f.FileName, f.OpenReadStream())).ToList();
    try
    {
        var e = await fw.AddAsync(kind, form["version"].ToString(), form["project"].ToString(), form["notes"].ToString(), files);
        return Results.Ok(e);
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
    finally { foreach (var (_, _, s) in files) s.Dispose(); }
}).DisableAntiforgery();
api.MapDelete("/firmware/{id}", (string id, JobService jobs) =>
{
    // 끝나지 않은 작업(대기·실행 중·적용 대기)이 쓰는 펌웨어는 지우지 않는다 — 화면과 같은 원자적 경로 (확인·삭제 사이에 작업이 생기지 않음)
    if (jobs.DeleteFirmwareIfUnused(id, out var inUse)) return Results.Ok();
    return inUse.Count > 0
        ? Results.Conflict($"작업 {string.Join(", ", inUse.Select(j => $"'{j.Name}'"))} 이(가) 이 펌웨어를 사용 중입니다 — 작업을 먼저 취소하세요")
        : Results.NotFound("펌웨어 없음");
});
api.MapGet("/jobs", (JobService jobs) => jobs.List());
api.MapGet("/jobs/{id}", (string id, JobService jobs) => jobs.Get(id) is { } j ? Results.Ok(j) : Results.NotFound());
api.MapPost("/jobs", (JobCreateRequest? r, JobService jobs) =>
{
    // 오프셋 없는 시각은 서버 지역 시각. applyMode: Approve(기본, 메인보드 스테이징 후 승인하면 적용) | Immediate(바로 적용).
    // recover: 메인보드 복구 적용 (앱이 응답하지 않는 호기용 — 부트로더가 바로 안 잡히면 최대 60초 전원 재투입 대기, AGV 정지 필수)
    if (r is null) return Results.BadRequest("본문(JSON)이 없습니다");
    if (JobApi.Check(r, out var mode) is { } bad) return Results.BadRequest(bad);
    try { return Results.Ok(jobs.Create(r.Name ?? "", r.FirmwareId!, r.Targets, r.ScheduledAt?.ToUniversalTime(), r.StopOnFailure ?? true, mode, r.NotAfter?.ToUniversalTime(), r.Recover ?? false)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
});
// queued=true: 이 작업이 끝나면 곧바로 이어서 시작될 대기 작업도 함께 취소
api.MapPost("/jobs/{id}/cancel", (string id, bool? queued, JobService jobs) => jobs.Cancel(id, queued ?? false) ? Results.Ok() : Results.NotFound());
// 적용 대기(AwaitingApproval) 작업의 적용 승인 — 호출 전에 대상 AGV 정지를 확인할 것
api.MapPost("/jobs/{id}/approve", (string id, JobService jobs) =>
{
    try { return jobs.Approve(id) ? Results.Ok() : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
});
api.MapDelete("/jobs/{id}", (string id, JobService jobs) =>
    jobs.Delete(id) ? Results.Ok() : jobs.Get(id) is null ? Results.NotFound("없는 작업") : Results.Conflict("끝나지 않은 작업은 지울 수 없음 — 먼저 취소"));
api.MapPost("/agvs/{agv:int}/send", (int agv, SendRequest r, BridgeUdpService link, AgvRegistry reg) =>
{
    if (ApiGuard.ManualSendRefusal(agv, reg.Get(agv), opt.OfflineAfterSec) is { } no) return no;
    if (string.IsNullOrEmpty(r.Data)) return Results.BadRequest("data 가 비어 있습니다");
    var frame = AgvFrame.ParseData(r.Data);
    if (frame.Length > AgvFrame.BridgeFrameMax) return Results.BadRequest($"프레임이 {AgvFrame.BridgeFrameMax}바이트를 넘습니다 ({frame.Length})");
    if (ApiGuard.IsBlockedManualFrame(frame)) return Results.BadRequest(ApiGuard.BlockedManualFrameMessage);
    return link.SendMainDown(agv, frame) ? Results.Ok() : Results.NotFound("호기 endpoint 없음");
});
api.MapPost("/agvs/{agv:int}/info", (int agv, BridgeUdpService link) =>
    agv is < 1 or > 99 ? Results.BadRequest("호기 번호는 1~99")
    : link.SendInfoRequest(agv) ? Results.Ok() : Results.NotFound("호기 endpoint 없음"));
api.MapPost("/agvs/{agv:int}/cfg", (int agv, SendRequest r, BridgeUdpService link, AgvRegistry reg) =>
{
    if (ApiGuard.ManualSendRefusal(agv, reg.Get(agv), opt.OfflineAfterSec) is { } no) return no;
    if (string.IsNullOrWhiteSpace(r.Data)) return Results.BadRequest("data 가 비어 있습니다");
    var cmd = r.Data.Trim();
    if (ApiGuard.CfgCommandProblem(cmd) is { } tooLong) return Results.BadRequest(tooLong);
    return link.SendCfg(agv, cmd) ? Results.Ok() : Results.NotFound("호기 endpoint 없음");
});
api.MapGet("/update", (UpdateService u) => new { current = u.CurrentText, u.Channel, u.Commit, stage = u.Stage.ToString(), u.Message, latest = u.Latest?.Tag, sha256 = u.Latest?.Sha256, u.LastCheck, manifest = u.ManifestUrl, old = u.OldVersionText });
api.MapPost("/update/check", async (UpdateService u) => { var i = await u.CheckAsync(); return Results.Ok(new { stage = u.Stage.ToString(), u.Message, latest = i?.Tag }); });
api.MapPost("/update/download", async (UpdateService u) => { await u.DownloadAsync(); return Results.Ok(new { stage = u.Stage.ToString(), u.Message }); });
api.MapPost("/update/apply", async (UpdateService u) => { await u.ApplyAsync(); return Results.Ok(new { stage = u.Stage.ToString(), u.Message }); });
api.MapPost("/update/rollback", async (UpdateService u) => { await u.RollbackAsync(); return Results.Ok(new { stage = u.Stage.ToString(), u.Message }); });
// ───── 호기 위치 (RTLS 결과. 지금은 테스트 주입만) — ACS 가 끌어갈 때도 이 GET 을 쓴다 ─────
api.MapGet("/positions", (AgvPositionStore st) => st.Snapshot());
api.MapGet("/positions/{agv:int}", (int agv, AgvPositionStore st) => st.Get(agv) is { } p ? Results.Ok(p) : Results.NotFound());
api.MapPost("/positions/{agv:int}", (int agv, PositionInject r, AgvPositionStore st, AgvRegistry reg) =>
{
    // 시험용 주입: RTLS 가 들어오기 전 ACS 발행·화면을 시험한다
    if (agv is < 1 or > 99) return Results.BadRequest("호기 번호는 1~99");
    var a = reg.Get(agv);
    st.Set(new AgvPosition(agv, DateTimeOffset.UtcNow, r.X, r.Y, r.Heading, r.State ?? PositionState.Tracked, r.Speed, r.Rotating ?? false,
                           r.LastTag, r.LastTag is null ? null : DateTimeOffset.UtcNow, r.Map, a?.Online(opt.OfflineAfterSec) ?? true, "injected"));
    return Results.Ok(st.Get(agv));
});
// ───── ACS REST 발행 상태·수동 발행 ─────
api.MapGet("/acs/status", (AcsPositionPushService p, IOptions<AcsApiOptions> o) => new
{
    p.Enabled, p.LogOnly, p.Target, o.Value.IntervalMs, o.Value.OnlyChanged, o.Value.PerAgv, p.LastTry, p.LastOk, p.LastResult, p.SentCount, p.FailStreak,
});
api.MapPost("/acs/push", async (AcsPositionPushService p) => Results.Ok(new { result = await p.PushAsync(force: true) }));
api.MapGet("/acs/preview", (AgvPositionStore st) => Results.Text(AcsPositionMapper.Serialize(AcsPositionMapper.Build(st.Snapshot(), DateTimeOffset.UtcNow)), "application/json"));
// ───── 호기 소속 (제품군·층·라인·공정·라우트) — ACS 에서 받거나 직접 입력 ─────
api.MapGet("/groups", (AgvGroupStore g) => g.Tree);
api.MapGet("/groups/list", (AgvGroupStore g) => g.Snapshot());
api.MapGet("/groups/agvs", (string path, AgvGroupStore g) => g.AgvsUnder(path));
api.MapGet("/groups/export.csv", (AgvGroupStore g) => Results.Text(GroupCsv.Format(g.Snapshot()), "text/csv; charset=utf-8"));
api.MapPost("/groups/import", async (HttpRequest req, AgvGroupStore g) =>
{
    // 본문 = CSV 텍스트 (agv,제품군,층,라인,공정,라우트[,이름])
    using var sr = new StreamReader(req.Body);
    var items = GroupCsv.Parse(await sr.ReadToEndAsync(), DateTimeOffset.UtcNow, out var errors);
    var n = g.Import(items);
    return Results.Ok(new { imported = n, errors });
});
api.MapGet("/agvs/{agv:int}/group", (int agv, AgvGroupStore g) => g.Get(agv) is { } a ? Results.Ok(a) : Results.NotFound());
api.MapPut("/agvs/{agv:int}/group", (int agv, GroupSetRequest r, AgvGroupStore g) =>
    agv is < 1 or > 99 ? Results.BadRequest("호기 번호는 1~99")
    : Results.Ok(g.Set(agv, AgvGroup.Trimmed(r.ProductFamily, r.Floor, r.Line, r.Process, r.Route), r.Name, r.Note ?? "")));
api.MapDelete("/agvs/{agv:int}/group", (int agv, AgvGroupStore g) => g.Remove(agv) ? Results.Ok() : Results.NotFound());
api.MapGet("/acs/inventory/status", (AcsInventoryPullService p, IOptions<AcsInventoryOptions> o) => new
{
    p.Enabled, p.Target, o.Value.IntervalSec, o.Value.OverrideManual, o.Value.RemoveMissing, o.Value.Fields, p.LastTry, p.LastOk, p.LastResult, p.LastCount, p.LastWarnings, p.FailStreak,
});
api.MapPost("/acs/inventory/pull", async (AcsInventoryPullService p) => Results.Ok(new { result = await p.PullAsync() }));
api.MapPost("/acs/inventory/preview", async (AcsInventoryPullService p) =>
{
    var (http, parsed, sample) = await p.PreviewAsync();
    return Results.Ok(new { ok = http.Ok, http = http.ToString(), parsed?.TotalRecords, items = parsed?.Items, parsed?.Warnings, sample });
});
// ───── 데이터베이스 ─────
api.MapGet("/db", (GatewayDb db) => new
{
    db.Path, Size = db.FileSize, Schema = GatewayDb.SchemaVersion,
    Tables = new { firmware = db.Count("firmware"), jobs = db.Count("jobs"), job_targets = db.Count("job_targets"), agv_groups = db.Count("agv_groups"), agv_positions = db.Count("agv_positions"), agv_last_seen = db.Count("agv_last_seen") },
});
api.MapGet("/db/backup", (GatewayDb db) => Results.File(db.Backup(), "application/octet-stream", $"gateway-{DateTime.Now:yyyyMMdd-HHmm}.db"));
api.MapGet("/health", (UpdateService u) => Results.Ok(new { ok = true, version = u.CurrentText, channel = u.Channel, time = DateTime.UtcNow }));

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Services.GetRequiredService<TraceLog>();   // 처음부터 통신 로그를 모으도록 미리 생성
    // 실제로 열린 HTTP 포트를 작업 실행기에 알림 (브리지 OTA URL 용)
    var addrs = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];
    foreach (var a in addrs)
        if (Uri.TryCreate(a.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var u) && u.Scheme == "http")
            app.Services.GetRequiredService<JobService>().HttpPort = u.Port;
    app.Logger.LogInformation("AGV FOTA Gateway 시작: {Urls} / 데이터 {Dir}", string.Join(", ", addrs), dataDir);
    app.Logger.LogInformation("접속 주소(Host) 허용: {Hosts}", hostGuard.Describe());
    // 브리지 0.5.2 는 URL 호스트가 자기 server IP 와 같아야 받는다 — 호스트명으로 적었으면 미리 알림 (설정 화면에도 표시)
    if (opt.PublicBaseUrlProblem() is { } urlProblem) app.Logger.LogWarning("{Problem}", urlProblem);
});

app.Run();

record JobCreateRequest(string? Name, string? FirmwareId, List<int>? Targets, DateTime? ScheduledAt, bool? StopOnFailure, string? ApplyMode, DateTime? NotAfter,
                        bool? Recover = null);
record GroupSetRequest(string? ProductFamily, string? Floor, string? Line, string? Process, string? Route, string? Name, string? Note);
record SendRequest(string? Data);
record PositionInject(double? X, double? Y, double? Heading, string? State, double? Speed, bool? Rotating, string? LastTag, string? Map);

/// <summary>POST /api/jobs 입력 검사</summary>
static class JobApi
{
    /// <summary>문제가 있으면 400 사유, 없으면 null (mode = 적용 방식, 비었으면 Approve)</summary>
    internal static string? Check(JobCreateRequest r, out ApplyMode mode)
    {
        mode = ApplyMode.Approve;
        if (string.IsNullOrWhiteSpace(r.FirmwareId)) return "firmwareId 가 비어 있습니다 (GET /api/firmware 의 id)";
        if (r.Targets is null || r.Targets.Count == 0 || r.Targets.Any(t => t is < 1 or > 99)) return "대상 호기(1~99)를 하나 이상 targets 에 넣으세요";
        if (!string.IsNullOrWhiteSpace(r.ApplyMode))
        {
            var t = r.ApplyMode.Trim();
            // 숫자("1")·정의되지 않은 값은 받지 않는다
            if (int.TryParse(t, out _) || !Enum.TryParse(t, true, out mode) || !Enum.IsDefined(mode)) { mode = ApplyMode.Approve; return "applyMode 는 Approve 또는 Immediate"; }
        }
        if (r.NotAfter is { } na)
        {
            var u = na.ToUniversalTime();
            if (u <= DateTime.UtcNow) return "notAfter(마감 시각)가 이미 지났습니다";
            if (r.ScheduledAt is { } sa && u <= sa.ToUniversalTime()) return "notAfter(마감 시각)는 scheduledAt(예약 시각)보다 뒤여야 합니다";
        }
        return null;
    }
}

/// <summary>/api 공통 검사 — 다른 출처의 변경 요청 거부(CSRF), 오류를 짧은 사유로, 수동 전송 거부 조건</summary>
static class ApiGuard
{
    internal const string BlockedManualFrameMessage = "업데이트 진입(F)·응답(R) 프레임은 수동 전송할 수 없습니다 — 배포 작업을 쓰세요";

    /// <summary>
    /// 수동 전송(REST·통신 로그 화면)으로 보내면 안 되는 프레임인지 — 종류 F(메인보드 업데이트 진입·전송)·R(응답).
    /// ACS 중계와 같은 규칙(AcsRelayPolicy.IsBlockedDown)에, STX 없이 적은 프레임·중간에 STX 가 끼인 프레임도 본다.
    /// </summary>
    internal static bool IsBlockedManualFrame(ReadOnlySpan<byte> f)
    {
        // STX 로 시작하면 다음 글자가 종류, STX 없이 적었으면 첫 글자가 종류 (그 뒤 PAN 글자가 F·R 이어도 정상 프레임)
        if (f.Length > 0 && f[0] == AgvFrame.Stx ? AcsRelayPolicy.IsBlockedDown(f) : f.Length > 0 && f[0] is (byte)'F' or (byte)'R') return true;
        for (int i = 0; i + 1 < f.Length; i++)
            if (f[i] == AgvFrame.Stx && f[i + 1] is (byte)'F' or (byte)'R') return true;
        return false;
    }

    /// <summary>
    /// 브라우저가 다른 출처 페이지에서 보낸 변경 요청(POST·PUT·DELETE·PATCH)이면 true.
    /// Sec-Fetch-Site=cross-site 이거나, Origin 헤더가 있는데 scheme·호스트·포트가 이 요청과 다르면 거부한다.
    /// 둘 다 없는 요청(curl·스크립트)은 허용한다. 화면(Blazor)은 SignalR 이라 해당 없음.
    /// </summary>
    internal static bool IsCrossSite(HttpRequest r, out string why)
    {
        why = "";
        if (!(HttpMethods.IsPost(r.Method) || HttpMethods.IsPut(r.Method) || HttpMethods.IsDelete(r.Method) || HttpMethods.IsPatch(r.Method))) return false;
        if (string.Equals(r.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase))
        {
            why = "다른 사이트의 페이지에서 보낸 요청은 받지 않습니다";
            return true;
        }
        var origin = r.Headers.Origin.ToString();
        if (origin.Length == 0) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)
            || !string.Equals(o.Scheme, r.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(o.Host, r.Host.Host, StringComparison.OrdinalIgnoreCase)
            || o.Port != (r.Host.Port ?? (string.Equals(r.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)))
        {
            why = $"다른 출처({origin})에서 보낸 요청은 받지 않습니다";
            return true;
        }
        return false;
    }

    /// <summary>설정 명령(CFG_CMD) 길이 검사 — REST·통신 로그 화면 공용. UTF-8 기준 패킷 payload 한도를 넘으면 사유, 아니면 null</summary>
    internal static string? CfgCommandProblem(string cmd)
    {
        var n = System.Text.Encoding.UTF8.GetByteCount(cmd);
        return n > BridgePacket.PayloadMax ? $"명령이 {BridgePacket.PayloadMax}바이트를 넘습니다 ({n}바이트)" : null;
    }

    internal static async Task Middleware(HttpContext ctx, RequestDelegate next)
    {
        // 화면용 상태 코드 페이지(HTML 재실행)를 끈다 — API 는 상태 코드와 짧은 사유만
        if (ctx.Features.Get<IStatusCodePagesFeature>() is { } sc) sc.Enabled = false;
        if (IsCrossSite(ctx.Request, out var why))
        {
            await Write(ctx, StatusCodes.Status403Forbidden, why);
            return;
        }
        try { await next(ctx); }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e) when (!ctx.Response.HasStarted)
        {
            await Write(ctx, e.StatusCode, "잘못된 요청: " + e.Message);
        }
        catch (Exception e) when (!ctx.Response.HasStarted && e is not OperationCanceledException)
        {
            ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Api").LogError(e, "{Method} {Path} 처리 오류", ctx.Request.Method, ctx.Request.Path);
            await Write(ctx, StatusCodes.Status500InternalServerError, "서버 오류: " + e.Message);
        }
    }

    internal static Task Write(HttpContext ctx, int status, string message)
    {
        ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(message);
    }

    /// <summary>수동 전송(MAIN_DOWN·CFG_CMD)을 거부할 사유 — 호기 번호 범위, 같은 번호 장치 충돌. 없으면 null</summary>
    internal static IResult? ManualSendRefusal(int agv, AgvState? a, int offlineAfterSec)
    {
        if (agv is < 1 or > 99) return Results.BadRequest("호기 번호는 1~99");
        if (a is not null && a.InConflict(offlineAfterSec))
            return Results.Conflict($"{agv:D2} 호기 번호를 쓰는 장치가 둘입니다 ({a.EndPoint} / {a.ConflictEndPoint}) — 충돌이 풀릴 때까지 수동 전송을 받지 않습니다");
        return null;
    }
}

/// <summary>
/// DNS 리바인딩 방어 (X-03). 공격자 이름을 게이트웨이 IP 로 다시 묶으면 Origin·Host 가 둘 다 그 이름이라 출처 검사(ApiGuard.IsCrossSite)를 통과하므로,
/// Host 헤더 자체가 이 서버의 이름인지 본다 — IP 리터럴(IPv4·IPv6), localhost, PC 이름·FQDN, Gateway.AllowedHostNames ("*" = 검사 안 함).
/// 화면(SignalR)·정적 파일·/fw·/api 모든 경로·모든 메서드에 적용한다. Host 가 없는 요청(HTTP/1.0 스크립트)은 브라우저가 아니므로 허용.
/// </summary>
sealed class HostGuard
{
    readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase) { "localhost" };
    readonly bool _any;
    long _warnedAt = -60_000;

    public HostGuard(IEnumerable<string>? extra, IEnumerable<string>? machineNames = null)
    {
        foreach (var n in (machineNames ?? MachineNames()).Concat(extra ?? []))
        {
            var t = Normalize(n);
            if (t == "*") _any = true;
            else if (t.Length > 0) _names.Add(t);
        }
    }

    /// <summary>이 PC 의 이름들 — NetBIOS 이름, 호스트 이름, 도메인이 있으면 FQDN</summary>
    static IEnumerable<string> MachineNames()
    {
        var list = new List<string> { Environment.MachineName };
        try { list.Add(System.Net.Dns.GetHostName()); } catch { }
        try
        {
            var p = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            list.Add(p.HostName);
            if (!string.IsNullOrWhiteSpace(p.DomainName)) list.Add($"{p.HostName}.{p.DomainName}");
        }
        catch { }
        return list;
    }

    /// <summary>소문자, 끝 점 제거 ("gw-pc." == "gw-pc")</summary>
    static string Normalize(string? h) => (h ?? "").Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>host = Host 헤더의 이름 부분 (포트 제외, IPv6 는 대괄호 포함 가능)</summary>
    public bool IsAllowed(string? host)
    {
        var h = Normalize(host);
        if (_any || h.Length == 0) return true;
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        // 브라우저는 IP 꼴 주소를 DNS 로 풀지 않으므로 리바인딩 대상이 아니다
        if (System.Net.IPAddress.TryParse(h, out _)) return true;
        return _names.Contains(h);
    }

    public string Describe() => _any ? "전체 (Gateway.AllowedHostNames 에 \"*\")" : "IP 주소, " + string.Join(", ", _names);

    public async Task Middleware(HttpContext ctx, RequestDelegate next)
    {
        var host = ctx.Request.Host.Host;
        if (IsAllowed(host)) { await next(ctx); return; }
        var why = $"접속 주소(Host) {host} 가 허용 목록에 없음 — Gateway.AllowedHostNames 에 추가";
        // 사내 이름으로 접속하는 PC 가 있으면 운용자가 원인을 찾을 수 있게 로그 (1분에 한 번)
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _warnedAt);
        if (now - last >= 60_000 && Interlocked.CompareExchange(ref _warnedAt, now, last) == last)
            ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("HostGuard").LogWarning("{Why} ({Remote} {Path})", why, ctx.Connection.RemoteIpAddress, ctx.Request.Path);
        if (ctx.Request.Path.StartsWithSegments("/api")) { await ApiGuard.Write(ctx, StatusCodes.Status403Forbidden, why); return; }
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync(why);
    }
}
