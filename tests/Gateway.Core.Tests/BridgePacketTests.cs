using Gateway.Core.Bridge;

namespace Gateway.Core.Tests;

public class BridgePacketTests
{
    [Fact]
    public void Build_matches_bridge_proto_layout()
    {
        var p = BridgePacket.Build(BridgePacket.MainDown, 7, 0x1234, [1, 2, 3]);
        Assert.Equal(new byte[] { 0xA5, 0x5A, 1, 0x02, 7, 0, 0x34, 0x12, 3, 0, 1, 2, 3 }, p);
        Assert.True(BridgePacket.TryParse(p, out var h, out var pl));
        Assert.Equal((BridgePacket.MainDown, (byte)7, (ushort)0x1234, (ushort)3), (h.Type, h.Agv, h.Seq, h.Length));
        Assert.Equal(new byte[] { 1, 2, 3 }, pl.ToArray());
    }

    [Fact]
    public void Payload_limit_is_1024_and_enforced_both_ways()
    {
        Assert.Equal(1024, BridgePacket.PayloadMax);
        var max = new byte[BridgePacket.PayloadMax];
        var p = BridgePacket.Build(BridgePacket.CfgCmd, 7, 1, max);
        Assert.True(BridgePacket.TryParse(p, out _, out var pl));
        Assert.Equal(1024, pl.Length);
        Assert.Throws<ArgumentException>(() => BridgePacket.Build(BridgePacket.CfgCmd, 7, 1, new byte[1025]));
        p[8] = 0x01; p[9] = 0x04; p = [.. p, 0];      // len 1025 — 헤더 길이가 맞아도 한도 초과로 거부
        Assert.False(BridgePacket.TryParse(p, out _, out _));
    }

    [Theory]
    [InlineData("rebooting\n>\n", "rebooting")]                  // 끝 표시 줄 제거
    [InlineData("ok hb_ms 300 - 'save' then 'reboot' to apply\n>\n", "ok hb_ms 300 - 'save' then 'reboot' to apply")]
    [InlineData("cfg\nagv=7\n>\n", "cfg\nagv=7")]                // 여러 줄 본문 유지
    [InlineData(">\n", "")]                                    // 본문 없이 끝 표시만
    [InlineData("{\"fw\":\"0.5.5\"}\n>\n", "{\"fw\":\"0.5.5\"}")]
    [InlineData("{\"fw\":\"0.8.0\",\"cfg\":{}}>\n", "{\"fw\":\"0.8.0\",\"cfg\":{}}")]   // 개행 없이 붙은 끝 표시
    [InlineData("old firmware text\n", "old firmware text")]   // 끝 표시가 없는 구버전 응답은 그대로
    public void Cfg_result_text_drops_end_marker(string raw, string expected)
        => Assert.Equal(expected, BridgePacket.CfgResultText(System.Text.Encoding.UTF8.GetBytes(raw)));

    [Fact]
    public void Parse_rejects_length_mismatch_and_bad_version()
    {
        var p = BridgePacket.Build(BridgePacket.Status, 1, 1, "{}"u8);
        p[8] = 3; Assert.False(BridgePacket.TryParse(p, out _, out _));
        p[8] = 2; p[2] = 2; Assert.False(BridgePacket.TryParse(p, out _, out _));
    }

    [Fact]
    public void Status_json_parses_bridge_heartbeat()
    {
        var js = """{"fw":"0.3.0","part":"ota_0","ota":"valid","up":123,"rssi":-55,"ch":6,"bssid":"aa:bb","disc":1,"reason":0,"m_rx":10,"m_tx":5,"r_rx":0,"u_err":0,"n_off":0,"n_err":0,"heap":1000,"cfg":"saved","ap":0}"""u8;
        var s = BridgeStatus.Parse(js)!;
        Assert.Equal(("0.3.0", "ota_0", "valid", -55, "saved", false), (s.Fw, s.Part, s.Ota, s.Rssi, s.Cfg, s.SetupAp));
        Assert.Null(BridgeStatus.Parse("not json"u8));
    }

    [Theory]
    [InlineData(10, 11, 0)]          // 연속
    [InlineData(10, 15, 4)]          // 4개 누락
    [InlineData(10, 10, 0)]          // 중복
    [InlineData(65535, 0, 0)]        // 넘김
    [InlineData(65530, 3, 8)]        // 넘김 + 누락 (65531..65535, 0..2)
    [InlineData(36000, 0, 0)]        // 10시간 가동 뒤 재부팅 — 29535 를 손실로 잡지 않는다
    [InlineData(100, 0, 0)]          // 짧게 가동 뒤 재부팅
    [InlineData(1000, 40000, 38999)] // 오래 오프라인 (브리지는 오프라인 중에도 번호를 올린다)
    public void Seq_loss_handles_wrap_reboot_and_gap(int last, int seq, int loss)
        => Assert.Equal(loss, BridgeSeq.Loss((ushort)last, (ushort)seq));

    [Fact]
    public void Seq_check_skips_echoed_types()
    {
        Assert.False(BridgeSeq.Tracked(BridgePacket.Pong));         // 서버 PING 의 seq 반송
        Assert.False(BridgeSeq.Tracked(BridgePacket.CfgResult));    // 서버 CFG_CMD 의 seq 반송
        Assert.True(BridgeSeq.Tracked(BridgePacket.Status));
        Assert.True(BridgeSeq.Tracked(BridgePacket.MainUp));
    }

    const string Heartbeat = """{"fw":"0.5.1","part":"ota_0","ota":"valid","up":100,"rssi":-55,"ch":6,"bssid":"aa:bb","disc":2,"m_rx":10,"heap":1000,"cfg":"saved","setup":0,"dsp":"1.0.0","staged":null}""";
    const string Info = """{"agv":7,"mac":"24:0a:c4:00:00:01","ip":"10.0.0.7","fw":"0.5.1","part":"ota_0","ota":"valid","cfg":"saved","setup":0,"up":101,"dsp":"1.0.0","staged":null}""";

    static BridgeStatus P(string js) => BridgeStatus.Parse(System.Text.Encoding.UTF8.GetBytes(js))!;

    [Fact]
    public void Info_merge_keeps_wifi_and_counters()
    {
        var m = BridgeStatus.Merge(P(Info), P(Heartbeat), info: true);
        Assert.Equal(("24:0a:c4:00:00:01", "10.0.0.7", 101L), (m.Mac, m.Ip, m.UpSeconds));
        Assert.Equal((-55, 6, "aa:bb", 2L, 10L), (m.Rssi, m.Channel, m.Bssid, m.Disconnects, m.MainRx));
        Assert.Contains("\"rssi\":-55", m.Raw);   // DB(status_json)에 남는 원문도 합친 값
    }

    [Fact]
    public void Heartbeat_merge_keeps_info_mac_and_ip_only()
    {
        var withInfo = BridgeStatus.Merge(P(Info), P(Heartbeat), info: true);
        var hb2 = Heartbeat.Replace("\"up\":100", "\"up\":102").Replace("\"rssi\":-55", "\"rssi\":-60").Replace("\"ota\":\"valid\"", "\"ota\":\"pending\"");
        var m = BridgeStatus.Merge(P(hb2), withInfo, info: false);
        Assert.Equal(("24:0a:c4:00:00:01", "10.0.0.7"), (m.Mac, m.Ip));
        Assert.Equal((-60, 102L, "pending"), (m.Rssi, m.UpSeconds, m.Ota));
        Assert.DoesNotContain("\"agv\"", m.Raw);  // INFO 전용 다른 키는 heartbeat 에 끌어오지 않는다

        // heartbeat 에 mac 이 있으면(브리지 0.5.2+) 그 값이 우선
        var hbMac = Heartbeat.Replace("{", "{\"mac\":\"24:0a:c4:00:00:02\",");
        Assert.Equal("24:0a:c4:00:00:02", BridgeStatus.Merge(P(hbMac), withInfo, info: false).Mac);
        // 직전 상태가 없으면 그대로
        Assert.Equal("", BridgeStatus.Merge(P(Heartbeat), null, info: false).Mac);
    }

    [Theory]
    [InlineData("saved", "valid", false)]
    [InlineData("trial", "valid", true)]
    [InlineData("saved", "pending", true)]
    public void Confirm_ping_needed_for_trial_or_pending(string cfg, string ota, bool need)
        => Assert.Equal(need, P($$"""{"cfg":"{{cfg}}","ota":"{{ota}}"}""").NeedsConfirmPing);
}
