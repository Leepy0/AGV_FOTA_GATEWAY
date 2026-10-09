using Gateway.Core.Acs;
using Gateway.Core.Agv;
using Gateway.Core.Positions;
using Gateway.Server.Components;
using Gateway.Server.Options;
using Gateway.Server.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;

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
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 64L * 1024 * 1024);
builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = 64L * 1024 * 1024);

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
builder.Services.AddSingleton<AgvRegistry>();
builder.Services.AddSingleton<BridgeUdpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BridgeUdpService>());
builder.Services.AddSingleton<AcsRelayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AcsRelayService>());
builder.Services.AddSingleton(sp => new FirmwareStore(dataDir));
builder.Services.AddSingleton<JobService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobService>());
builder.Services.AddSingleton<TraceLog>();
builder.Services.AddSingleton<AgvPositionStore>();
builder.Services.AddHttpClient<AcsRestPublisher>();
builder.Services.AddSingleton<AcsPositionPushService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AcsPositionPushService>());
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
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
    }));
api.MapGet("/firmware", (FirmwareStore fw) => fw.List());
api.MapPost("/firmware", async (HttpRequest req, FirmwareStore fw) =>
{
    // multipart: kind=dsp|esp, version, project, notes, 파일 하나 (dsp: .dspfw, esp: agv_wifi_bridge.bin)
    var form = await req.ReadFormAsync();
    var kind = form["kind"].ToString().Equals("esp", StringComparison.OrdinalIgnoreCase) ? FirmwareKind.Esp : FirmwareKind.Dsp;
    var files = form.Files.Select(f => (f.Name, f.FileName, f.OpenReadStream())).ToList();
    try
    {
        var e = await fw.AddAsync(kind, form["version"].ToString(), form["project"].ToString(), form["notes"].ToString(), files);
        return Results.Ok(e);
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
    finally { foreach (var (_, _, s) in files) s.Dispose(); }
}).DisableAntiforgery();
api.MapDelete("/firmware/{id}", (string id, FirmwareStore fw, JobService jobs) =>
{
    // 예약·실행 중인 작업이 참조하는 펌웨어는 지우지 않는다 (작업을 먼저 취소)
    var inUse = jobs.List().Where(j => j.FirmwareId == id && j.State is JobState.Scheduled or JobState.Running).ToList();
    if (inUse.Count > 0) return Results.Conflict($"작업 {string.Join(", ", inUse.Select(j => j.Name))} 이(가) 이 펌웨어를 사용 중입니다 — 작업을 먼저 취소하세요");
    return fw.Delete(id) ? Results.Ok() : Results.NotFound();
});
api.MapGet("/jobs", (JobService jobs) => jobs.List());
api.MapGet("/jobs/{id}", (string id, JobService jobs) => jobs.Get(id) is { } j ? Results.Ok(j) : Results.NotFound());
api.MapPost("/jobs", (JobCreateRequest r, JobService jobs) =>
{
    try { return Results.Ok(jobs.Create(r.Name ?? "", r.FirmwareId, r.Targets, r.ScheduledAt?.ToUniversalTime(), r.StopOnFailure ?? true)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
});
api.MapPost("/jobs/{id}/cancel", (string id, JobService jobs) => jobs.Cancel(id) ? Results.Ok() : Results.NotFound());
api.MapDelete("/jobs/{id}", (string id, JobService jobs) => jobs.Delete(id) ? Results.Ok() : Results.Conflict());
api.MapPost("/agvs/{agv:int}/send", (int agv, SendRequest r, BridgeUdpService link) =>
    link.SendMainDown(agv, AgvFrame.ParseData(r.Data)) ? Results.Ok() : Results.NotFound("호기 endpoint 없음"));
api.MapPost("/agvs/{agv:int}/info", (int agv, BridgeUdpService link) => link.SendInfoRequest(agv) ? Results.Ok() : Results.NotFound("호기 endpoint 없음"));
api.MapPost("/agvs/{agv:int}/cfg", (int agv, SendRequest r, BridgeUdpService link) =>
    link.SendCfg(agv, r.Data) ? Results.Ok() : Results.NotFound("호기 endpoint 없음"));
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
});

app.Run();

record JobCreateRequest(string? Name, string FirmwareId, List<int> Targets, DateTime? ScheduledAt, bool? StopOnFailure);
record SendRequest(string Data);
record PositionInject(double? X, double? Y, double? Heading, string? State, double? Speed, bool? Rotating, string? LastTag, string? Map);
