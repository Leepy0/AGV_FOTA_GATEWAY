using System.Net;
using Gateway.Core.Bridge;

namespace Gateway.Core.Tests;

public class BridgeOriginTests
{
    static readonly IPEndPoint A = IPEndPoint.Parse("10.0.0.7:50000"), B = IPEndPoint.Parse("10.0.0.99:50000");

    [Fact]
    public void First_packet_and_same_endpoint_are_accepted()
    {
        Assert.Equal(OriginDecision.Move, BridgeOrigin.Decide(null, false, "", A, ""));
        Assert.Equal(OriginDecision.Same, BridgeOrigin.Decide(A, true, "", IPEndPoint.Parse("10.0.0.7:50000"), ""));
    }

    [Fact]
    public void Other_address_while_current_online_is_conflict()
    {
        // 같은 번호의 예비 브리지·가짜 브리지(fake_esp) — endpoint 를 뺏기지 않는다
        Assert.Equal(OriginDecision.Conflict, BridgeOrigin.Decide(A, true, "", B, ""));
        Assert.Equal(OriginDecision.Conflict, BridgeOrigin.Decide(A, true, "24:0a:c4:00:00:01", B, "24:0a:c4:00:00:02"));
        Assert.True(BridgeOrigin.MacDiffers("24:0a:c4:00:00:01", "24:0a:c4:00:00:02"));
        Assert.False(BridgeOrigin.MacDiffers("", "24:0a:c4:00:00:02"));
        // 포트만 달라도 다른 장치로 본다 (브리지는 고정 local_port 로 보낸다)
        Assert.Equal(OriginDecision.Conflict, BridgeOrigin.Decide(A, true, "", IPEndPoint.Parse("10.0.0.7:50011"), ""));
    }

    [Fact]
    public void Offline_current_or_same_mac_moves_to_new_address()
    {
        Assert.Equal(OriginDecision.Move, BridgeOrigin.Decide(A, false, "", B, ""));                                     // 정상 IP 변경
        Assert.Equal(OriginDecision.Move, BridgeOrigin.Decide(A, true, "24:0A:C4:00:00:01", B, "24:0a:c4:00:00:01"));   // 같은 장치(MAC)
    }

    [Fact]
    public void Same_mac_under_other_number_is_rename()
    {
        var known = new[] { (5, "24:0A:C4:00:00:01"), (6, ""), (7, "24:0a:c4:00:00:02") };
        Assert.Equal(5, BridgeOrigin.RenamedFrom(known, 9, "24:0a:c4:00:00:01"));   // 5 → 9
        Assert.Null(BridgeOrigin.RenamedFrom(known, 5, "24:0a:c4:00:00:01"));       // 자기 번호
        Assert.Null(BridgeOrigin.RenamedFrom(known, 9, "24:0a:c4:00:00:03"));       // 처음 보는 장치
        Assert.Null(BridgeOrigin.RenamedFrom(known, 9, ""));                        // MAC 모름 (구 브리지 heartbeat)
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    [InlineData(255, false)]
    public void Agv_number_range(int agv, bool ok) => Assert.Equal(ok, BridgeOrigin.IsValidAgv(agv));
}
