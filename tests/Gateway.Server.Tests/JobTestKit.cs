using System.Net;
using System.Text.Json;
using Gateway.Core.Bridge;
using Gateway.Core.Fota;
using Gateway.Server.Data;
using Gateway.Server.Options;
using Gateway.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Server.Tests;

/// <summary>
/// 가짜 브리지 여러 대(0.9.0 흉내): START 에 download→staged, APPLY 에 start→…→done 을 보고하고 AgvRegistry 의 heartbeat 상태를 갱신한다.
/// 앱 버전 칸이 8자라 done·heartbeat 의 dsp 는 앞 8자만 (실제 브리지와 같음).
/// 패키지: START 에 pkg_* 가 없으면 no_pkg, 있으면 보관 → heartbeat staged.pkg, 적용 뒤 dsp_pkg (pkg_st). Legacy08 이면 0.8.0 처럼 pkg_* 를 무시하고 새 필드가 없다.
/// </summary>
sealed class FakeBridges(AgvRegistry reg) : IAgvLink
{
    readonly Dictionary<int, List<AgvSubscription>> _subs = new();
    readonly Dictionary<int, (string dsp, string st, long job, string ver)> _state = new();
    public readonly List<(int agv, string kind, string json)> Sent = new();
    public bool SilentStage;     // START 에 아무 보고도 안 함 (실행 중 취소 시험)
    public bool HangApply;       // APPLY 에 start 만 보고하고 멈춤 (굽는 중 취소 시험)
    public string? StageFailErr; // START 에 이 오류로 fail 보고 (예: url_host)
    public string? ApplyFailErr; // APPLY 에 start 뒤 이 오류로 fail 보고 (예: app_no_reply)
    public bool DeafApply;       // APPLY 를 받기만 하고 아무것도 안 함 (명령 유실 — 스테이징 유지)
    readonly Dictionary<int, (string fw, string part, long up)> _esp = new();
    public bool Legacy08;                       // 브리지 0.8.0 흉내: pkg_* 무시, pkg_st·dsp_pkg·staged.pkg 없음
    public readonly HashSet<int> NoPkgPart = []; // 이 호기들은 pkg_st=nopart (START 는 pkg_nopart 로 거부)
    readonly Dictionary<int, string> _pkgStaged = new();                // 스테이징과 짝인 패키지 ID
    readonly Dictionary<int, (string id, string ver)> _pkgApplied = new(); // 메인보드에 적용된 패키지 (dsp_pkg)

    /// <summary>호기를 온라인으로 등록 (dsp: 앱 버전, "?" = 앱 무응답)</summary>
    public AgvState Add(int agv, string dsp = "1.0.0", string dspState = "idle")
    {
        _state[agv] = (dsp, dspState, 0, "");
        return Beat(agv);
    }

    public AgvState Beat(int agv)
    {
        var (dsp, st, job, ver) = _state[agv];
        var (fw, part, up) = _esp.TryGetValue(agv, out var e) ? e : ("0.5.2", "ota_0", 10L);
        var pkgStaged = !Legacy08 && _pkgStaged.TryGetValue(agv, out var sp) ? $",\"pkg\":\"{sp}\"" : "";
        var staged = job == 0 ? "null" : $"{{\"job\":{job},\"ver\":\"{ver}\"{pkgStaged}}}";
        var pkgFields = Legacy08 ? "" : $",\"pkg_st\":\"{(NoPkgPart.Contains(agv) ? "nopart" : "ok")}\",\"dsp_pkg\":"
            + (_pkgApplied.TryGetValue(agv, out var ap) ? $"{{\"id\":\"{ap.id}\",\"ver\":\"{ap.ver}\"}}" : "null");
        var a = reg.GetOrAdd(agv);
        a.Status = BridgeStatus.Parse(System.Text.Encoding.UTF8.GetBytes(
            $"{{\"fw\":\"{fw}\",\"part\":\"{part}\",\"ota\":\"valid\",\"up\":{up},\"dsp\":\"{dsp}\",\"dspb\":\"1.0.0\",\"dsp_st\":\"{st}\",\"staged\":{staged}{pkgFields}}}"));
        a.LastSeen = DateTime.UtcNow;
        return a;
    }

    public void SetDsp(int agv, string dsp) { _state[agv] = (dsp, "idle", 0, ""); Beat(agv); }
    /// <summary>메인보드 상태만 바꿈 (스테이징 유지 — 굽다 실패해 staged 로 돌아온 경우 등)</summary>
    public void SetDspState(int agv, string dsp, string dspState) { var s = _state[agv]; _state[agv] = (dsp, dspState, s.job, s.ver); Beat(agv); }
    /// <summary>브리지 자체 펌웨어·실행 슬롯·가동 시간 (heartbeat fw/part/up)</summary>
    public void SetEsp(int agv, string fw, string part, long up) { _esp[agv] = (fw, part, up); Beat(agv); }
    /// <summary>브리지가 보내는 메인보드 OTA 보고를 흉내 (취소 뒤 확인 시험)</summary>
    public void SendReport(int agv, long job, string stage, string? err = null) => Report(agv, job, stage, -1, null, err);
    /// <summary>브리지가 재부팅 등으로 스테이징을 잃음</summary>
    public void DropStaged(int agv) { var s = _state[agv]; _state[agv] = (s.dsp, "idle", 0, ""); _pkgStaged.Remove(agv); Beat(agv); }
    /// <summary>브리지가 보관한 패키지(dsp_pkg)를 정함 — 호기 현황 시험용. null 이면 없음</summary>
    public void SetPkgApplied(int agv, string? id, string ver = "") { if (id is null) _pkgApplied.Remove(agv); else _pkgApplied[agv] = (id, ver); Beat(agv); }
    /// <summary>브리지가 지금 보고하는 보관 패키지 / 스테이징 패키지 (시험 확인용)</summary>
    public (string id, string ver)? PkgApplied(int agv) => _pkgApplied.TryGetValue(agv, out var p) ? p : null;
    public string? PkgStaged(int agv) => _pkgStaged.TryGetValue(agv, out var p) ? p : null;

    void Report(int agv, long job, string stage, int pct = -1, string? ver = null, string? err = null)
    {
        var js = $"{{\"job\":{job},\"stage\":\"{stage}\"" + (pct >= 0 ? $",\"pct\":{pct}" : "") + (ver is null ? "" : $",\"ver\":\"{ver}\"") + (err is null ? "" : $",\"err\":\"{err}\"") + "}";
        if (_subs.TryGetValue(agv, out var l)) foreach (var s in l.ToArray()) s.DspOtaResults.Writer.TryWrite(js);
    }

    public bool SendDspOtaStart(int agv, string json)
    {
        lock (Sent) Sent.Add((agv, "START", json));
        if (SilentStage) return true;
        using var d = JsonDocument.Parse(json);
        long job = d.RootElement.GetProperty("job").GetInt64();
        if (StageFailErr is not null) { Report(agv, job, "fail", err: StageFailErr); return true; }
        var ver = d.RootElement.GetProperty("ver").GetString()!;
        if (!Legacy08)
        {
            // 브리지 0.9.0: pkg_* 넷이 모두 있어야 받는다
            var r = d.RootElement;
            string? pid = r.TryGetProperty("pkg_id", out var pi) ? pi.GetString() : null;
            bool all = pid is not null && r.TryGetProperty("pkg_url", out _) && r.TryGetProperty("pkg_size", out _) && r.TryGetProperty("pkg_sha256", out _);
            if (!all) { Report(agv, job, "fail", err: "no_pkg"); return true; }
            if (NoPkgPart.Contains(agv)) { Report(agv, job, "fail", err: "pkg_nopart"); return true; }
            _pkgStaged[agv] = pid!;
        }
        Report(agv, job, "download", 50);
        var s = _state[agv];
        _state[agv] = (s.dsp, "staged", job, ver);
        Beat(agv);
        Report(agv, job, "staged", 100, ver);
        return true;
    }

    public bool SendDspOtaApply(int agv, string json)
    {
        lock (Sent) Sent.Add((agv, "APPLY", json));
        using var d = JsonDocument.Parse(json);
        long job = d.RootElement.GetProperty("job").GetInt64();
        var s = _state[agv];
        if (job != s.job) { Report(agv, job, "fail", err: s.job == 0 ? "not_staged" : "job_mismatch"); return true; }
        if (DeafApply) return true;
        Report(agv, job, "start", 0, s.ver);
        if (ApplyFailErr is not null) { Report(agv, job, "fail", ver: s.ver, err: ApplyFailErr); return true; }
        if (HangApply) { _state[agv] = (s.dsp, "programming", s.job, s.ver); Beat(agv); return true; }
        foreach (var st in new[] { "enter", "erase" }) Report(agv, job, st);
        Report(agv, job, "progress", 100);
        Report(agv, job, "verify");
        var v8 = s.ver.Length > 8 ? s.ver[..8] : s.ver;
        _state[agv] = (v8, "idle", 0, "");
        if (!Legacy08 && _pkgStaged.Remove(agv, out var pid)) _pkgApplied[agv] = (pid, v8);   // 적용 성공 → 보관 패키지 = 이 버전
        Beat(agv);
        Report(agv, job, "done", 100, v8);
        return true;
    }

    public bool SendMainDown(int agv, ReadOnlySpan<byte> frame) => true;
    public bool SendOtaStart(int agv, string url) { lock (Sent) Sent.Add((agv, "OTA", url)); return true; }
    public bool SendInfoRequest(int agv) => true;
    public bool SendPing(int agv) => true;
    public AgvSubscription Subscribe(int agv)
    {
        AgvSubscription? s = null;
        s = new(() => { lock (_subs) _subs[agv].Remove(s!); });
        lock (_subs) { if (!_subs.TryGetValue(agv, out var l)) _subs[agv] = l = new(); l.Add(s); }
        return s;
    }
    public string? LocalAddressToward(int agv) => "10.0.0.1";
}

/// <summary>임시 폴더에 DB·펌웨어 저장소·가짜 브리지를 갖춘 작업 실행기</summary>
sealed class JobTestKit : IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "gw-jobtest-" + Guid.NewGuid().ToString("N")[..8]);
    public readonly GatewayDb Db;
    public FirmwareStore Store;
    public readonly AgvRegistry Reg = new();
    public readonly FakeBridges Link;
    public readonly GatewayOptions Opt;
    public JobService Svc;

    public JobTestKit(string? publicBaseUrl = "http://10.0.0.1:8080")
    {
        Db = new GatewayDb(Dir);
        Store = new FirmwareStore(Dir, Db);
        Link = new FakeBridges(Reg);
        Opt = new GatewayOptions { DataDir = Dir, OfflineAfterSec = 5, PublicBaseUrl = publicBaseUrl };
        Svc = NewService();
    }

    public JobService NewService() => new(Microsoft.Extensions.Options.Options.Create(Opt), Store, Reg, Link, Db, NullLogger<JobService>.Instance)
    {
        NotBefore = DateTime.MinValue, OnlineWait = TimeSpan.FromSeconds(1), SaveRetryGap = TimeSpan.Zero,
    };

    /// <summary>서버 재시작 흉내: 같은 DB 로 실행기를 새로 만든다</summary>
    public JobService Restart() => Svc = NewService();

    /// <summary>블록·app_crc 검사를 통과하는 작은 앱 이미지 (dsp_pack.py pack() 과 같은 형식)</summary>
    public static byte[] DspImage(string ver) => DspfwImage.BuildApp(Enumerable.Range(0, 200).Select(i => (ushort)(i * 7)).ToArray(), ver);

    /// <summary>DspImage 를 담은 패키지 (.dsppkg, 합성 .out·.map — 규격 v1)</summary>
    public static byte[] DspPackage(string ver, string stem = "app") => DspPackageBuilder.Build(DspImage(ver), stem);

    public FirmwareEntry AddDsp(string ver = "2.0.0", string fileName = "app.dsppkg")
    {
        if (!DspVersion.IsValid(ver)) return AddLegacyDsp(ver, fileName);
        return Store.AddAsync(FirmwareKind.Dsp, "", "", "", [("pkg", fileName, new MemoryStream(DspPackage(ver)))]).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 버전 규칙(8자)이 생기기 전에 올라온 펌웨어 흉내: 지금은 업로드가 거부하므로 정상 버전으로 올린 뒤
    /// 파일과 DB 의 버전을 긴 값으로 바꾸고 저장소·실행기를 다시 만든다
    /// </summary>
    FirmwareEntry AddLegacyDsp(string ver, string fileName)
    {
        var e = AddDsp("legacy", fileName);
        File.WriteAllBytes(Store.PathOf(e, e.Primary!), DspImage(ver));
        using (var c = Db.Open()) GatewayDb.Exec(c, "UPDATE firmware SET version=$v WHERE id=$id", ("$v", ver), ("$id", e.Id));
        Store = new FirmwareStore(Dir, Db);
        Svc = NewService();
        return Store.Get(e.Id)!;
    }

    /// <summary>
    /// 패키지 도입(0.7.0) 전에 .dspfw 만 올린 이전 등록 (pkg_id 없음, 패키지·.out·.map 없음): 지금은 업로드가 거부하므로 DB 에 직접 넣고
    /// 저장소·실행기를 다시 만든다 (v4 DB 의 행과 같은 모양)
    /// </summary>
    public FirmwareEntry AddNoPackageDsp(string ver = "1.0.3")
    {
        var id = "legacy-" + Guid.NewGuid().ToString("N")[..6];
        var img = DspImage(ver);
        var name = $"app_{ver}.dspfw";
        using (var c = Db.Open())
            GatewayDb.Exec(c, """
                INSERT INTO firmware(id, kind, version, project, notes, uploaded_at, slot, file_name, size, sha256, pkg_id, link_check)
                VALUES($id, 'Dsp', $v, 'agv-dsp', '0.6 업로드', $up, 'dspfw', $f, $size, 'AB', '', '')
                """, ("$id", id), ("$v", ver), ("$up", GatewayDb.Ts(DateTime.UtcNow)), ("$f", name), ("$size", img.Length));
        Directory.CreateDirectory(Path.Combine(Dir, "firmware", id));
        File.WriteAllBytes(Path.Combine(Dir, "firmware", id, name), img);
        Store = new FirmwareStore(Dir, Db);
        Svc = NewService();
        return Store.Get(id)!;
    }

    /// <summary>스케줄러 한 번 돌리고, 시작한 작업이 끝날(또는 적용 대기에 멈출) 때까지</summary>
    public async Task TickAndWaitAsync()
    {
        Svc.Tick();
        await Svc.LastRun.WaitAsync(TimeSpan.FromSeconds(20));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
