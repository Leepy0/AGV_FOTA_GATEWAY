using System.Text;
using Gateway.Core.Bridge;

namespace Gateway.Core.Tests;

/// <summary>시각 동기·Wi-Fi 사건·설정 사본·브리지 릴리스 매니페스트 — 서버 운영 기능의 순수 규칙</summary>
public class BridgeClockTests
{
    [Fact]
    public void Time_packets_have_distinct_types_and_names()
    {
        Assert.Equal((byte)0x22, BridgePacket.TimeReq);
        Assert.Equal((byte)0x23, BridgePacket.Time);
        Assert.Equal("TIME_REQ", BridgePacket.TypeName(BridgePacket.TimeReq));
        Assert.Equal("TIME", BridgePacket.TypeName(BridgePacket.Time));
    }

    [Theory]
    [InlineData("{\"t0\":123456}", 123456L)]
    [InlineData("{\"t0\":0}", 0L)]
    [InlineData("{\"x\":1}", null)]
    [InlineData("{\"t0\":-5}", null)]
    [InlineData("not json", null)]
    [InlineData("[1]", null)]
    public void Request_parse(string json, long? expected)
        => Assert.Equal(expected, BridgeClock.ParseRequest(Encoding.ASCII.GetBytes(json)));

    [Fact]
    public void Reply_echoes_t0_and_carries_server_times()
    {
        Assert.Equal("{\"t0\":777,\"t1\":1760700000100,\"t2\":1760700000102}", Encoding.ASCII.GetString(BridgeClock.BuildReply(777, 1760700000100, 1760700000102)));
        Assert.Equal("{\"t2\":1760700000102}", Encoding.ASCII.GetString(BridgeClock.BuildReply(null, 0, 1760700000102)));
    }

    [Theory]
    [InlineData(1000, 1000, 0, false)]
    [InlineData(1005, 1000, 5, false)]
    [InlineData(1000, 1600, -600, false)]     // 임계 1000 ms
    [InlineData(1000, 3000, -2000, true)]
    [InlineData(5000, 1000, 4000, true)]
    public void Skew_and_threshold(long bridge, long server, long skew, bool over)
    {
        Assert.Equal(skew, BridgeClock.SkewMs(bridge, server));
        Assert.Equal(over, BridgeClock.OverThreshold(skew, 1000));
    }

    [Theory]
    [InlineData(3, "+3 ms")]
    [InlineData(-999, "-999 ms")]
    [InlineData(1500, "+1.5 s")]
    [InlineData(-90_000, "-1.5 분")]
    public void Skew_text(long ms, string text) => Assert.Equal(text, BridgeClock.FormatSkew(ms));

    [Fact]
    public void Status_parses_clock_fields()
    {
        var synced = BridgeStatus.Parse("""{"fw":"0.8.0","up":50,"ts":1760700000000,"tsync":12,"cfg":"saved"}"""u8)!;
        Assert.True(synced.HasClock && synced.ClockSynced);
        Assert.Equal((1760700000000L, 12L), (synced.TsMs, synced.TsyncAgeSec));

        var unsynced = BridgeStatus.Parse("""{"fw":"0.8.0","up":5,"tsync":-1}"""u8)!;
        Assert.True(unsynced.HasClock);
        Assert.False(unsynced.ClockSynced);

        var old = BridgeStatus.Parse("""{"fw":"0.7.0","up":5}"""u8)!;
        Assert.False(old.HasClock);
        Assert.False(old.ClockSynced);
    }
}

public class WifiMonitorTests
{
    static readonly DateTime T = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    const string A = "aa:aa:aa:aa:aa:01", B = "bb:bb:bb:bb:bb:02";

    [Fact]
    public void Roam_when_bssid_changes_without_disconnect()
    {
        var m = new WifiMonitor();
        Assert.Null(m.Observe(7, T, 100, -50, 6, A, 0, "tag1"));
        Assert.Null(m.Observe(7, T.AddSeconds(1), 101, -55, 6, A, 0, "tag1"));
        var e = m.Observe(7, T.AddSeconds(2), 102, -60, 11, B, 0, "tag2")!;
        Assert.Equal((WifiEventKind.Roam, A, B, "tag2", 11), (e.Kind, e.FromBssid, e.ToBssid, e.Tag, e.Channel));
    }

    [Fact]
    public void Disconnect_when_counter_increases_even_if_bssid_changed()
    {
        var m = new WifiMonitor();
        m.Observe(7, T, 100, -50, 6, A, 2, "");
        var e = m.Observe(7, T.AddSeconds(1), 101, -70, 11, B, 3, "t")!;
        Assert.Equal(WifiEventKind.Disconnect, e.Kind);
        Assert.Equal((A, B), (e.FromBssid, e.ToBssid));
    }

    [Fact]
    public void Reboot_resets_baseline_and_offline_beats_are_skipped()
    {
        var m = new WifiMonitor();
        m.Observe(7, T, 500, -50, 6, A, 4, "");
        Assert.Null(m.Observe(7, T.AddSeconds(1), 3, -50, 6, B, 0, ""));      // 가동 시간이 줄었다 = 재부팅 → 사건 아님
        Assert.Null(m.Observe(7, T.AddSeconds(2), 4, 0, 0, "", 0, ""));       // 연결 안 됨
        Assert.Null(m.Observe(7, T.AddSeconds(3), 5, -50, 6, B, 0, ""));      // 같은 AP 로 돌아옴
    }

    [Fact]
    public void Each_agv_has_its_own_baseline()
    {
        var m = new WifiMonitor();
        m.Observe(1, T, 10, -50, 6, A, 0, "");
        Assert.Null(m.Observe(2, T, 10, -50, 6, B, 0, ""));                   // 2번의 첫 관측
        Assert.NotNull(m.Observe(1, T.AddSeconds(1), 11, -50, 6, B, 0, ""));
    }
}

public class WifiAggregatorTests
{
    static readonly DateTime T = new(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Samples_group_by_hour_tag_and_bssid()
    {
        var a = new WifiAggregator();
        a.AddSample(T, "tag1", "AA:BB", -50);
        a.AddSample(T.AddMinutes(10), "tag1", "aa:bb", -80);      // 같은 시간·태그·AP (대소문자 무시)
        a.AddSample(T.AddMinutes(40), "tag1", "aa:bb", -60);      // 다음 시간
        a.AddSample(T, "tag2", "aa:bb", -90);
        a.AddSample(T, "tag1", "", -40);                          // BSSID 없음 → 무시
        var r = a.Drain();
        Assert.Equal(3, r.Count);
        var s = r.Single(x => x.Key.Tag == "tag1" && x.Key.Hour.Hour == 9).Spot;
        Assert.Equal((2L, -130L, -80, 1L), (s.Samples, s.RssiSum, s.RssiMin, s.Weak));   // -80 은 약한 신호(≤-75)
        Assert.Empty(a.Drain());                                  // 꺼내면 비워진다
    }

    [Fact]
    public void Events_count_drops_and_roams_in_the_target_ap_cell()
    {
        var a = new WifiAggregator();
        a.AddEvent(new WifiEvent(T, 7, WifiEventKind.Disconnect, "a", "B", 6, -70, "t"));
        a.AddEvent(new WifiEvent(T, 7, WifiEventKind.Roam, "a", "B", 6, -70, "t"));
        var s = a.Drain().Single().Spot;
        Assert.Equal((0L, 1L, 1L), (s.Samples, s.Drops, s.Roams));
    }
}

public class CfgSnapshotTests
{
    const string Json = """{"fw":"0.7.0","idf":"v6.1","mac":"a1:b2:c3:d4:e5:f6","name":"AGVBR-07-E5F6","part":"ota_0","ota":"valid","state":"saved","differs":false,"cfg":{"agv":7,"ssid":"AGV WIFI","pass_len":10,"ip":"192.168.10.107","mask":"255.255.255.0","gw":"192.168.10.1","dns":"auto","dns2":"auto","server":"192.168.10.10","server_port":50000,"local_port":50000,"rfid_dst":"off","rfid_port":50001,"main_baud":115200,"rfid_baud":9600,"hb_ms":1000},"keys":["agv","ssid","pass","ip"]}""";

    [Fact]
    public void Parses_identity_values_and_keys()
    {
        var s = CfgSnapshot.Parse(Json)!;
        Assert.Equal(("0.7.0", "AGVBR-07-E5F6", "saved", false), (s.Fw, s.Name, s.State, s.Differs));
        Assert.Equal("192.168.10.10", s.Get("server"));
        Assert.Equal("50000", s.Get("server_port"));     // 숫자도 글자로
        Assert.Equal("AGV WIFI", s.Get("ssid"));
        Assert.Equal(["agv", "ssid", "pass", "ip"], s.Keys);
    }

    [Theory]
    [InlineData("unknown command 'cfg json' - type help")]
    [InlineData("{\"err\":\"overflow\"}")]
    [InlineData("")]
    [InlineData("{ not json")]
    public void Non_snapshot_responses_are_null(string text) => Assert.Null(CfgSnapshot.Parse(text));

    [Fact]
    public void Drift_lists_only_fleet_keys_that_differ_from_baseline()
    {
        var s = CfgSnapshot.Parse(Json)!;
        var baseline = new Dictionary<string, string> { ["server"] = "192.168.10.10", ["hb_ms"] = "500", ["dns"] = "auto", ["ip"] = "1.2.3.4", ["agv"] = "1" };
        var d = CfgDrift.Compare(baseline, s);
        Assert.Equal([new CfgDiff("hb_ms", "500", "1000")], d);      // ip·agv 는 호기별이라 비교하지 않음
    }

    [Fact]
    public void Most_common_picks_the_majority_per_key()
    {
        var a = CfgSnapshot.Parse(Json)!;
        var b = CfgSnapshot.Parse(Json.Replace("\"hb_ms\":1000", "\"hb_ms\":500"))!;
        var c = CfgSnapshot.Parse(Json)!;
        var m = CfgDrift.MostCommon([a, b, c]);
        Assert.Equal("1000", m["hb_ms"]);
        Assert.Equal("192.168.10.10", m["server"]);
        Assert.False(m.ContainsKey("ip"));
    }

    [Fact]
    public void Restore_commands_skip_password_and_keep_agv_and_ip()
    {
        var cmds = CfgDrift.RestoreCommands(CfgSnapshot.Parse(Json)!);
        Assert.Contains("set agv 7", cmds);
        Assert.Contains("set ssid AGV WIFI", cmds);
        Assert.Contains("set ip 192.168.10.107", cmds);
        Assert.DoesNotContain(cmds, c => c.StartsWith("set pass"));
    }

    [Fact]
    public void Bulk_keys_are_fleet_keys_plus_pass_and_never_per_agv()
    {
        Assert.Contains("pass", CfgSnapshot.BulkKeys);
        Assert.DoesNotContain("ip", CfgSnapshot.BulkKeys);
        Assert.DoesNotContain("agv", CfgSnapshot.BulkKeys);
    }

    [Fact]
    public void Batches_split_sort_and_dedupe()
    {
        var b = CfgDrift.Batches([9, 3, 3, 1, 7, 5], 2);
        Assert.Equal([[1, 3], [5, 7], [9]], b);
        Assert.Equal(2, CfgDrift.Batches([1, 2], 0).Count);     // 0 이하는 한 대씩
    }
}

public class BridgeReleaseTests
{
    const string Manifest = """{"version":"0.7.0","published":"2026-10-17T12:40:39Z","page":"https://github.com/Leepy0/AGV_Wifi_Bridge/releases/tag/v0.7.0","zip":{"file":"agv_wifi_bridge_0.7.0.zip","sha256":"5b24b81561d1a8c97bb6cd284d1319fa7a53acb30877f6015393f8c4a9d9c3e0","size":5257705},"app":{"file":"agv_wifi_bridge.bin","sha256":"6F527B8150FB57455628A3DD99989643856F061431ACDC84F48D329536E7AEC3","size":1234224,"offset":"0x20000"},"minPartitionVersion":"0.5.0","notes":"Add scan"}""";

    [Fact]
    public void Parses_app_asset_and_builds_download_url()
    {
        var r = BridgeRelease.Parse(Manifest)!;
        Assert.Equal(("v0.7.0", "agv_wifi_bridge.bin", 1234224L, "0.5.0"), (r.Tag, r.AppFile, r.AppSize, r.MinPartitionVersion));
        Assert.Equal("6f527b8150fb57455628a3dd99989643856f061431acdc84f48d329536e7aec3", r.AppSha256);     // 소문자로
        Assert.Equal("https://github.com/Leepy0/AGV_Wifi_Bridge/releases/download/v0.7.0/agv_wifi_bridge.bin", r.AppUrl("https://github.com/Leepy0/AGV_Wifi_Bridge/"));
    }

    [Theory]
    [InlineData("""{"version":"x","app":{"sha256":"00"}}""")]
    [InlineData("""{"version":"0.7.0"}""")]                                            // app 없음
    [InlineData("""{"version":"0.7.0","app":{"file":"a.bin","sha256":"abc"}}""")]      // 해시 길이 틀림
    [InlineData("""{"version":"0.7.0","app":{"file":"../x.bin","sha256":"6f527b8150fb57455628a3dd99989643856f061431acdc84f48d329536e7aec3"}}""")]  // 경로가 낀 이름
    [InlineData("nope")]
    public void Unusable_manifests_are_null(string json) => Assert.Null(BridgeRelease.Parse(json));

    [Theory]
    [InlineData("0.6.0", true)]
    [InlineData("0.6.0-df648c4", true)]       // 개발 빌드 꼴
    [InlineData("0.7.0", false)]
    [InlineData("0.7.0-abc1234", false)]
    [InlineData("0.8.0", false)]
    [InlineData("", false)]                    // 모르면 구버전이라 하지 않음
    [InlineData("garbage", false)]
    public void Outdated_rule(string fw, bool outdated) => Assert.Equal(outdated, BridgeRelease.Parse(Manifest)!.IsOutdated(fw));
}
