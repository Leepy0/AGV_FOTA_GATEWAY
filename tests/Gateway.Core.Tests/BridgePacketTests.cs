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
}
