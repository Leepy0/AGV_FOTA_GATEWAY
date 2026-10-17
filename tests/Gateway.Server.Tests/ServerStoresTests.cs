using System.Net;
using System.Security.Cryptography;
using System.Text;
using Gateway.Core.Bridge;
using Gateway.Core.Fota;
using Gateway.Server.Data;
using Gateway.Server.Options;
using Gateway.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Tests;

/// <summary>가짜 브리지 무리: 설정(cfg json·set·save·reboot)에 답하고 재부팅을 흉내 낸다 — 일괄 변경·수집 시험용</summary>
sealed class FakeFleet : ICfgChannel
{
    readonly AgvRegistry _reg;
    readonly object _lock = new();
    public readonly Dictionary<int, Dictionary<string, string>> Cfg = new();
    readonly Dictionary<int, Dictionary<string, string>> _pending = new();
    readonly Dictionary<int, Dictionary<string, string>> _committed = new();
    public readonly List<string> Log = new();
    public readonly HashSet<int> RejectSet = new();        // set 을 거부
    public readonly HashSet<int> RevertOnReboot = new();   // 재부팅하면 이전 설정으로 돌아옴 (서버에 못 닿은 경우)
    public readonly HashSet<int> NeverReturns = new();     // 재부팅 뒤 돌아오지 않음
    public string CfgJsonOverride = "";                    // 비우지 않으면 cfg json 에 이 글을 그대로 답함 (구 펌웨어 흉내)

    public FakeFleet(AgvRegistry reg) => _reg = reg;

    public static Dictionary<string, string> DefaultCfg(int agv) => new()
    {
        ["agv"] = agv.ToString(), ["ssid"] = "AGV_WIFI", ["pass_len"] = "10", ["ip"] = $"192.168.10.{100 + agv}", ["mask"] = "255.255.255.0", ["gw"] = "192.168.10.1",
        ["dns"] = "auto", ["dns2"] = "auto", ["server"] = "192.168.10.10", ["server_port"] = "50000", ["local_port"] = "50000", ["rfid_dst"] = "off",
        ["rfid_port"] = "50001", ["main_baud"] = "115200", ["rfid_baud"] = "9600", ["hb_ms"] = "1000",
    };

    public void Add(int agv, long up = 1000)
    {
        Cfg[agv] = DefaultCfg(agv);
        Beat(agv, up, "saved");
    }

    public void Beat(int agv, long up, string cfg, string dspState = "idle")
    {
        var a = _reg.GetOrAdd(agv);
        a.EndPoint = new IPEndPoint(IPAddress.Loopback, 40000 + agv);
        a.LastSeen = DateTime.UtcNow;
        a.Status = BridgeStatus.Parse(Encoding.UTF8.GetBytes($"{{\"fw\":\"0.8.0\",\"up\":{up},\"cfg\":\"{cfg}\",\"dsp_st\":\"{dspState}\",\"rssi\":-50,\"bssid\":\"aa\"}}"));
    }

    string CfgJson(int agv)
    {
        var c = Cfg[agv];
        string V(string k) => c[k];
        string Q(string k) => $"\"{k}\":\"{V(k)}\"";
        string N(string k) => $"\"{k}\":{V(k)}";
        return $"{{\"fw\":\"0.8.0\",\"idf\":\"v6.1\",\"mac\":\"a1:b2:c3:d4:e5:{agv:x2}\",\"name\":\"AGVBR-{agv:D2}\",\"part\":\"ota_0\",\"ota\":\"valid\",\"state\":\"saved\",\"differs\":false," +
               $"\"cfg\":{{{N("agv")},{Q("ssid")},{N("pass_len")},{Q("ip")},{Q("mask")},{Q("gw")},{Q("dns")},{Q("dns2")},{Q("server")},{N("server_port")},{N("local_port")},{Q("rfid_dst")},{N("rfid_port")},{N("main_baud")},{N("rfid_baud")},{N("hb_ms")}}}," +
               "\"keys\":[\"agv\",\"ssid\",\"pass\",\"ip\"]}";
    }

    public Task<CfgReply> RequestCfgAsync(int agv, string cmd, TimeSpan timeout, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var shown = cmd.StartsWith("set pass ", StringComparison.Ordinal) ? "set pass ***" : cmd;
            Log.Add($"{agv}:{shown}");
            if (!Cfg.ContainsKey(agv)) return Task.FromResult(new CfgReply(null, "오프라인"));
            var a = _reg.Get(agv);
            if (a is null || !a.Online(5)) return Task.FromResult(new CfgReply(null, "오프라인"));
            if (cmd == "cfg json") return Task.FromResult(new CfgReply(CfgJsonOverride.Length > 0 ? CfgJsonOverride : CfgJson(agv), null));
            if (cmd.StartsWith("set ", StringComparison.Ordinal))
            {
                if (RejectSet.Contains(agv)) return Task.FromResult(new CfgReply("invalid key or value: x\n", null));
                var p = cmd.Split(' ', 3);
                if (!_pending.TryGetValue(agv, out var d)) _pending[agv] = d = new();
                d[p[1]] = p[1] == "pass" ? (p[2] == "-" ? "0" : p[2].Length.ToString()) : p[2];       // pass 는 길이로만 보관
                if (p[1] == "pass") d["pass_len"] = d["pass"];
                return Task.FromResult(new CfgReply($"ok {p[1]} - 'save' then 'reboot' to apply\n", null));
            }
            if (cmd == "save")
            {
                _committed[agv] = _pending.TryGetValue(agv, out var d) ? new(d) : new();
                return Task.FromResult(new CfgReply("saved (trial) - 'reboot' to apply.\n", null));
            }
            if (cmd == "reboot")
            {
                var revert = RevertOnReboot.Contains(agv);
                var gone = NeverReturns.Contains(agv);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(30);
                    a.LastSeen = DateTime.UtcNow.AddSeconds(-60);                  // 오프라인
                    await Task.Delay(100);
                    if (gone) return;
                    lock (_lock)
                    {
                        if (!revert && _committed.TryGetValue(agv, out var c)) foreach (var (k, v) in c) if (k != "pass") Cfg[agv][k] = v;
                        _pending.Remove(agv); _committed.Remove(agv);
                    }
                    Beat(agv, 2, "saved");
                });
                return Task.FromResult(new CfgReply("rebooting\n", null));
            }
            return Task.FromResult(new CfgReply($"unknown command '{cmd}' - type help\n", null));
        }
    }
}

public class CfgStoreTests
{
    static (CfgStore store, FakeFleet fleet, AgvRegistry reg, GatewayDb db, string dir) Make()
    {
        var dir = OpsKit.TempDir();
        var db = new GatewayDb(dir);
        var reg = new AgvRegistry();
        var fleet = new FakeFleet(reg);
        var store = new CfgStore(db, reg, null, fleet, OpsKit.Opts(), NullLogger<CfgStore>.Instance);
        return (store, fleet, reg, db, dir);
    }

    [Fact]
    public async Task Collect_stores_snapshot_and_survives_a_restart()
    {
        var (store, fleet, reg, db, _) = Make();
        fleet.Add(7);
        var r = await store.CollectAsync(7);
        Assert.True(r.Ok);
        Assert.Equal("192.168.10.10", store.Get(7)!.Snapshot.Get("server"));
        // 다시 열어도(재시작) 사본이 남아 있다
        var again = new CfgStore(db, reg, null, fleet, OpsKit.Opts(), NullLogger<CfgStore>.Instance);
        Assert.Equal("AGV_WIFI", again.Get(7)!.Snapshot.Get("ssid"));
    }

    [Fact]
    public async Task Old_firmware_and_errors_are_reported_not_stored()
    {
        var (store, fleet, _, _, _) = Make();
        fleet.Add(7);
        fleet.CfgJsonOverride = "unknown command 'cfg json' - type help\n";
        var r = await store.CollectAsync(7);
        Assert.False(r.Ok);
        Assert.Contains("0.6.0", r.Message);
        Assert.Null(store.Get(7));
        fleet.CfgJsonOverride = "{\"err\":\"overflow\"}";
        Assert.Contains("오류", (await store.CollectAsync(7)).Message);
        Assert.False((await store.CollectAsync(9)).Ok);       // 모르는 호기
    }

    [Fact]
    public async Task Baseline_drift_and_majority()
    {
        var (store, fleet, _, db, _) = Make();
        foreach (var a in new[] { 1, 2, 3 }) fleet.Add(a);
        fleet.Cfg[3]["hb_ms"] = "500";
        foreach (var a in new[] { 1, 2, 3 }) await store.CollectAsync(a);

        Assert.Empty(store.Baseline());
        Assert.Equal(CfgSnapshot.FleetKeys.Length, store.SetBaselineFromFleet());     // 다수결
        Assert.Equal("1000", store.Baseline()["hb_ms"]);
        Assert.Empty(store.DriftOf(1));
        Assert.Equal([new CfgDiff("hb_ms", "1000", "500")], store.DriftOf(3));

        // 기준값은 DB 에 남는다
        var again = new CfgStore(db, new AgvRegistry(), null, fleet, OpsKit.Opts(), NullLogger<CfgStore>.Instance);
        Assert.Equal("1000", again.Baseline()["hb_ms"]);
        // 호기마다 다른 항목은 기준값에 넣지 않는다
        store.SetBaseline(new Dictionary<string, string> { ["ip"] = "1.2.3.4", ["agv"] = "1", ["server"] = "10.0.0.1" });
        Assert.Equal(["server"], store.Baseline().Keys);
    }

    [Theory]
    [InlineData("0.6.0", true)]
    [InlineData("0.8.0", true)]
    [InlineData("0.7.0-abc1234", true)]
    [InlineData("0.5.5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Cfg_json_support_by_fw(string? fw, bool ok) => Assert.Equal(ok, CfgStore.SupportsCfgJson(fw));

    [Fact]
    public async Task Collect_many_counts_ok_and_failed()
    {
        var (store, fleet, _, _, _) = Make();
        fleet.Add(1); fleet.Add(2);
        var (ok, fail) = await store.CollectManyAsync([1, 2, 3]);
        Assert.Equal((2, 1), (ok, fail));
    }
}

public class CfgBulkServiceTests
{
    static (CfgBulkService bulk, FakeFleet fleet, AgvRegistry reg, CfgStore store) Make(params int[] agvs)
    {
        var db = new GatewayDb(OpsKit.TempDir());
        var reg = new AgvRegistry();
        var fleet = new FakeFleet(reg);
        foreach (var a in agvs) fleet.Add(a, up: 1000);
        var store = new CfgStore(db, reg, null, fleet, OpsKit.Opts(), NullLogger<CfgStore>.Instance);
        var bulk = new CfgBulkService(reg, fleet, store, OpsKit.Opts(), NullLogger<CfgBulkService>.Instance)
        {
            ReplyTimeout = TimeSpan.FromSeconds(2), RebootWait = TimeSpan.FromSeconds(4), Poll = TimeSpan.FromMilliseconds(20),
        };
        return (bulk, fleet, reg, store);
    }

    static async Task<BulkRun> Finished(CfgBulkService bulk, BulkRun run)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (run.State == BulkRunState.Running && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.NotEqual(BulkRunState.Running, run.State);
        return run;
    }

    [Fact]
    public async Task Applies_batch_by_batch_and_verifies_each_agv()
    {
        var (bulk, fleet, _, _) = Make(1, 2, 3, 4);
        var run = await Finished(bulk, bulk.Start([1, 2, 3, 4], [new BulkChange("hb_ms", "500"), new BulkChange("dns", "192.168.10.1")], 2));
        Assert.Equal(BulkRunState.Done, run.State);
        Assert.All(run.Targets, t => Assert.Equal(BulkTargetState.Done, t.State));
        foreach (var a in new[] { 1, 2, 3, 4 }) { Assert.Equal("500", fleet.Cfg[a]["hb_ms"]); Assert.Equal("192.168.10.1", fleet.Cfg[a]["dns"]); }
        // 두 번째 묶음(3·4)은 첫 묶음(1·2)이 확인(cfg json)까지 끝난 뒤에 시작
        int Idx(string s) => fleet.Log.IndexOf(s);
        var lastOfFirst = Math.Max(fleet.Log.LastIndexOf("1:cfg json"), fleet.Log.LastIndexOf("2:cfg json"));
        Assert.True(Idx("3:set hb_ms 500") > lastOfFirst && Idx("4:set hb_ms 500") > lastOfFirst);
        // save → reboot 순서
        Assert.True(Idx("1:save") > Idx("1:set dns 192.168.10.1") && Idx("1:reboot") > Idx("1:save"));
    }

    [Fact]
    public async Task A_failed_batch_stops_the_run_and_skips_the_rest()
    {
        var (bulk, fleet, _, _) = Make(1, 2, 3, 4);
        fleet.RejectSet.Add(2);
        var run = await Finished(bulk, bulk.Start([1, 2, 3, 4], [new BulkChange("hb_ms", "500")], 2));
        Assert.Equal(BulkRunState.Failed, run.State);
        Assert.Equal(BulkTargetState.Failed, run.Targets.Single(t => t.Agv == 2).State);
        Assert.Contains("거부", run.Targets.Single(t => t.Agv == 2).Message);
        Assert.Equal(BulkTargetState.Skipped, run.Targets.Single(t => t.Agv == 3).State);
        Assert.DoesNotContain(fleet.Log, l => l.StartsWith("3:") || l.StartsWith("4:"));
        Assert.Equal("1000", fleet.Cfg[3]["hb_ms"]);
    }

    [Fact]
    public async Task Reverted_config_is_detected_by_reading_it_back()
    {
        var (bulk, fleet, _, _) = Make(1);
        fleet.RevertOnReboot.Add(1);      // 새 설정으로 서버에 못 닿아 이전 설정으로 돌아옴 → heartbeat 는 saved 로 보이지만 값은 그대로
        var run = await Finished(bulk, bulk.Start([1], [new BulkChange("server", "10.9.9.9")], 5));
        Assert.Equal(BulkRunState.Failed, run.State);
        Assert.Contains("반영 안 됨", run.Targets[0].Message);
        Assert.Contains("server", run.Targets[0].Message);
    }

    [Fact]
    public async Task An_agv_that_never_returns_fails_after_the_wait()
    {
        var (bulk, fleet, _, _) = Make(1);
        fleet.NeverReturns.Add(1);
        bulk.RebootWait = TimeSpan.FromMilliseconds(800);
        var run = await Finished(bulk, bulk.Start([1], [new BulkChange("hb_ms", "500")], 5));
        Assert.Equal(BulkRunState.Failed, run.State);
        Assert.Contains("돌아오지 않음", run.Targets[0].Message);
    }

    [Fact]
    public async Task Offline_conflicting_and_busy_agvs_are_skipped_up_front()
    {
        var (bulk, fleet, reg, _) = Make(1, 2, 3, 4);
        reg.Get(2)!.LastSeen = DateTime.UtcNow.AddSeconds(-60);                 // 오프라인
        fleet.Beat(3, 1000, "saved", dspState: "programming");                 // 메인보드 굽는 중
        reg.Get(4)!.ConflictAt = DateTime.UtcNow; reg.Get(4)!.ConflictEndPoint = new IPEndPoint(IPAddress.Loopback, 1);   // 번호 충돌
        var run = await Finished(bulk, bulk.Start([1, 2, 3, 4, 5], [new BulkChange("hb_ms", "500")], 5));
        Assert.Equal(BulkRunState.Done, run.State);
        Assert.Equal(BulkTargetState.Done, run.Targets.Single(t => t.Agv == 1).State);
        Assert.Equal("오프라인", run.Targets.Single(t => t.Agv == 2).Message);
        Assert.Equal("메인보드 작업 중", run.Targets.Single(t => t.Agv == 3).Message);
        Assert.Equal("호기 번호 충돌 중", run.Targets.Single(t => t.Agv == 4).Message);
        Assert.Contains("모름", run.Targets.Single(t => t.Agv == 5).Message);
        Assert.DoesNotContain(fleet.Log, l => l.StartsWith("2:set") || l.StartsWith("3:set") || l.StartsWith("4:set"));
    }

    [Fact]
    public async Task All_skipped_is_a_failed_run_and_only_one_run_at_a_time()
    {
        var (bulk, fleet, _, _) = Make(1);
        fleet.NeverReturns.Add(1);
        var first = bulk.Start([1], [new BulkChange("hb_ms", "500")], 5);
        Assert.Throws<InvalidOperationException>(() => bulk.Start([1], [new BulkChange("hb_ms", "500")], 5));
        bulk.Cancel();
        await Finished(bulk, first);
        Assert.Equal(BulkRunState.Cancelled, first.State);

        var run = await Finished(bulk, bulk.Start([9], [new BulkChange("hb_ms", "500")], 5));      // 없는 호기뿐
        Assert.Equal(BulkRunState.Failed, run.State);
    }

    [Fact]
    public async Task Password_change_is_verified_by_length_and_never_shown()
    {
        var (bulk, fleet, _, _) = Make(1);
        var run = await Finished(bulk, bulk.Start([1], [new BulkChange("pass", "newpassword-12")], 5));
        Assert.Equal(BulkRunState.Done, run.State);
        Assert.Equal("14", fleet.Cfg[1]["pass_len"]);
        Assert.DoesNotContain("newpassword-12", string.Join("|", run.Changes.Select(c => c.Shown)));
        Assert.DoesNotContain(fleet.Log, l => l.Contains("newpassword"));       // 가짜가 로그에 가린 형태만 남기므로 사실상 형식 확인
    }

    [Theory]
    [InlineData("hb_ms", "500", 3, 5, null)]
    [InlineData("ip", "1.2.3.4", 3, 5, "일괄로 바꿀 수 없는")]
    [InlineData("agv", "9", 3, 5, "일괄로 바꿀 수 없는")]
    [InlineData("hb_ms", "", 3, 5, "비었거나")]
    [InlineData("hb_ms", "5\n00", 3, 5, "비었거나")]
    [InlineData("hb_ms", "500", 0, 5, "대상 호기가 없습니다")]
    [InlineData("hb_ms", "500", 3, 0, "1~50")]
    [InlineData("hb_ms", "500", 3, 51, "1~50")]
    public void Input_check(string key, string value, int targets, int batch, string? problem)
    {
        var r = CfgBulkService.Check([new BulkChange(key, value)], Enumerable.Range(1, targets).ToList(), batch);
        if (problem is null) Assert.Null(r); else Assert.Contains(problem, r);
    }

    [Fact]
    public void Check_rejects_duplicate_keys_empty_changes_and_bad_agv_numbers()
    {
        Assert.Contains("두 번", CfgBulkService.Check([new BulkChange("hb_ms", "1"), new BulkChange("hb_ms", "2")], [1], 5));
        Assert.Contains("바꿀 항목이 없습니다", CfgBulkService.Check([], [1], 5));
        Assert.Contains("1~99", CfgBulkService.Check([new BulkChange("hb_ms", "1")], [100], 5));
    }

    [Fact]
    public void Match_rule_compares_password_by_length_only()
    {
        var s = CfgSnapshot.Parse("""{"cfg":{"pass_len":8,"hb_ms":500}}""")!;
        Assert.True(CfgBulkService.Matches(s, new BulkChange("pass", "12345678")));
        Assert.False(CfgBulkService.Matches(s, new BulkChange("pass", "1234567")));
        Assert.False(CfgBulkService.Matches(s, new BulkChange("pass", "-")));          // 오픈 AP = 길이 0
        Assert.True(CfgBulkService.Matches(s, new BulkChange("hb_ms", "500")));
        Assert.False(CfgBulkService.Matches(s, new BulkChange("hb_ms", "1000")));
    }
}

public class WifiStoreTests
{
    static BridgeStatus St(string bssid, int rssi, long disc, long up = 100)
        => BridgeStatus.Parse(Encoding.UTF8.GetBytes($"{{\"fw\":\"0.8.0\",\"up\":{up},\"rssi\":{rssi},\"ch\":6,\"bssid\":\"{bssid}\",\"disc\":{disc}}}"))!;

    [Fact]
    public void Samples_events_and_queries_round_trip_through_the_database()
    {
        var db = new GatewayDb(OpsKit.TempDir());
        var reg = new AgvRegistry();
        var link = new BridgeUdpService(OpsKit.Opts(), reg, NullLogger<BridgeUdpService>.Instance);
        var wifi = new WifiStore(db, link, OpsKit.Opts(), NullLogger<WifiStore>.Instance);
        var t = DateTime.UtcNow;
        const string A = "aa:aa:aa:aa:aa:01", B = "bb:bb:bb:bb:bb:02";

        wifi.Observe(7, t, "tagA", St(A, -50, 0, 100));
        wifi.Observe(7, t.AddSeconds(1), "tagA", St(A, -80, 0, 101));        // 약한 신호
        wifi.Observe(7, t.AddSeconds(2), "tagB", St(B, -60, 0, 102));        // 로밍 A → B (태그 B)
        wifi.Observe(7, t.AddSeconds(3), "tagB", St(B, -70, 1, 103));        // 끊김 (disc 증가)
        wifi.Flush();

        var aps = wifi.Aps(t.AddHours(-1));
        var a = aps.Single(x => x.Bssid == A);
        Assert.Equal((2L, -65.0, -80, 50.0), (a.Samples, a.AvgRssi, a.MinRssi, a.WeakPercent));
        var b = aps.Single(x => x.Bssid == B);
        Assert.Equal((2L, 1L, 1L), (b.Samples, b.Roams, b.Drops));

        var spots = wifi.Spots(t.AddHours(-1));
        Assert.Equal("tagB", spots[0].Tag);                                     // 끊김이 있는 위치가 앞
        Assert.Equal((1L, 1L), (spots[0].Drops, spots[0].Roams));

        var ev = wifi.Events(t.AddHours(-1), null, 50);
        Assert.Equal([WifiEventKind.Disconnect, WifiEventKind.Roam], ev.Select(e => e.Kind));        // 최근 것이 먼저
        Assert.Single(wifi.Events(t.AddHours(-1), 7, 1));
        Assert.Empty(wifi.Events(t.AddHours(-1), 8, 50));

        // 두 번째 Flush 는 같은 칸에 합산 (덮어쓰지 않음)
        wifi.Observe(7, t.AddSeconds(4), "tagA", St(B, -90, 1, 104));
        wifi.Flush();
        Assert.Equal(3L, wifi.Aps(t.AddHours(-1)).Single(x => x.Bssid == B).Samples);
        Assert.Equal(-90, wifi.Aps(t.AddHours(-1)).Single(x => x.Bssid == B).MinRssi);
    }

    [Fact]
    public void Offline_beats_and_missing_bssid_make_no_samples()
    {
        var db = new GatewayDb(OpsKit.TempDir());
        var wifi = new WifiStore(db, new BridgeUdpService(OpsKit.Opts(), new AgvRegistry(), NullLogger<BridgeUdpService>.Instance), OpsKit.Opts(), NullLogger<WifiStore>.Instance);
        wifi.Observe(7, DateTime.UtcNow, "", St("", 0, 0));
        wifi.Flush();
        Assert.Empty(wifi.Aps(DateTime.UtcNow.AddHours(-1)));
    }
}

public class BridgeReleaseServiceTests
{
    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Urls.Add(req.RequestUri!.ToString());
            return Task.FromResult(f(req));
        }
    }

    static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    static string Manifest(byte[] app, string version = "0.7.0")
        => $"{{\"version\":\"{version}\",\"published\":\"2026-10-17T12:40:39Z\",\"page\":\"https://github.com/Leepy0/AGV_Wifi_Bridge/releases/tag/v{version}\"," +
           $"\"app\":{{\"file\":\"agv_wifi_bridge.bin\",\"sha256\":\"{Convert.ToHexString(SHA256.HashData(app)).ToLowerInvariant()}\",\"size\":{app.Length},\"offset\":\"0x20000\"}},\"notes\":\"Add scan\"}}";

    static (BridgeReleaseService svc, FirmwareStore fw, Stub stub) Make(Func<HttpRequestMessage, HttpResponseMessage> f, string baseUrl = "https://github.com/Leepy0/AGV_Wifi_Bridge")
    {
        var dir = OpsKit.TempDir();
        var fw = new FirmwareStore(dir, new GatewayDb(dir));
        var stub = new Stub(f);
        var svc = new BridgeReleaseService(Microsoft.Extensions.Options.Options.Create(new UpdateOptions { BridgeBaseUrl = baseUrl }), fw, NullLogger<BridgeReleaseService>.Instance, new HttpClient(stub));
        return (svc, fw, stub);
    }

    [Fact]
    public async Task Check_reads_the_manifest_and_import_verifies_hash_then_registers_once()
    {
        var app = EspAppImage.Build("0.7.0", "agv_wifi_bridge");
        var (svc, fw, stub) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("bridge.json") ? Ok(Manifest(app)) : Bytes(app));
        var rel = await svc.CheckAsync();
        Assert.Equal("v0.7.0", rel!.Tag);
        Assert.Null(svc.Error);
        Assert.True(rel.IsOutdated("0.6.0"));
        Assert.Equal("https://github.com/Leepy0/AGV_Wifi_Bridge/releases/latest/download/bridge.json", stub.Urls[0]);

        var e = await svc.ImportLatestAsync();
        Assert.Equal((FirmwareKind.Esp, "0.7.0", "agv_wifi_bridge"), (e.Kind, e.Version, e.Project));
        Assert.Equal("https://github.com/Leepy0/AGV_Wifi_Bridge/releases/download/v0.7.0/agv_wifi_bridge.bin", stub.Urls[^1]);
        Assert.Equal(e.Id, svc.AlreadyImported(rel)!.Id);

        var downloads = stub.Urls.Count;
        Assert.Equal(e.Id, (await svc.ImportLatestAsync()).Id);          // 이미 가져온 버전: 다시 받지 않음
        Assert.Equal(downloads, stub.Urls.Count);
        Assert.Single(fw.List());
    }

    [Fact]
    public async Task Import_refuses_a_wrong_hash_size_project_or_version_and_adds_nothing()
    {
        var good = EspAppImage.Build("0.7.0", "agv_wifi_bridge");
        var tampered = (byte[])good.Clone(); tampered[300] ^= 0xFF;                 // 같은 크기, 다른 내용
        var (svc, fw, _) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("bridge.json") ? Ok(Manifest(good)) : Bytes(tampered));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ImportLatestAsync());
        Assert.Contains("sha256", ex.Message);

        var shortBody = good[..100];
        (svc, fw, _) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("bridge.json") ? Ok(Manifest(good)) : Bytes(shortBody));
        Assert.Contains("크기", (await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ImportLatestAsync())).Message);

        var other = EspAppImage.Build("0.7.0", "other_project");
        (svc, fw, _) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("bridge.json") ? Ok(Manifest(other)) : Bytes(other));
        Assert.Contains("브리지 이미지가 아닙니다", (await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ImportLatestAsync())).Message);

        var wrongVer = EspAppImage.Build("0.6.9", "agv_wifi_bridge");
        (svc, fw, _) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("bridge.json") ? Ok(Manifest(wrongVer, "0.7.0")) : Bytes(wrongVer));
        Assert.Contains("릴리스 버전", (await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ImportLatestAsync())).Message);
        Assert.Empty(fw.List());
    }

    [Fact]
    public async Task Missing_manifest_and_forbidden_hosts_are_reported()
    {
        var (svc, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await svc.CheckAsync());
        Assert.Contains("bridge.json", svc.Error);

        var (bad, _, stub) = Make(_ => Ok("{}"), baseUrl: "http://evil.example/x");
        Assert.Null(await bad.CheckAsync());
        Assert.Contains("허용되지 않은", bad.Error);
        Assert.Empty(stub.Urls);                                                       // 허용되지 않은 주소로는 요청하지 않는다

        var (junk, _, _) = Make(_ => Ok("not json"));
        Assert.Null(await junk.CheckAsync());
        Assert.Contains("읽을 수 없습니다", junk.Error);
    }

    [Fact]
    public async Task A_failed_recheck_keeps_the_last_known_release()
    {
        var app = EspAppImage.Build("0.7.0", "agv_wifi_bridge");
        var fail = false;
        var (svc, _, _) = Make(r => fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok(Manifest(app)));
        await svc.CheckAsync();
        fail = true;
        await svc.CheckAsync();
        Assert.Equal("v0.7.0", svc.Latest!.Tag);
        Assert.NotNull(svc.Error);
    }
}
