using Gateway.Core.Agv;
using Gateway.Server.Options;
using Gateway.Server.Services;

namespace Gateway.Server.Tests;

/// <summary>REST 입력 검사: POST /api/jobs (INT-A2), 수동 전송 F/R 차단 (INT-A8), PublicBaseUrl IPv4 (INT-A5)</summary>
public class ApiInputTests
{
    static JobCreateRequest Req(string? fw = "fw1", List<int>? targets = null, string? mode = null, DateTime? at = null, DateTime? notAfter = null) =>
        new("", fw, targets ?? [7], at, null, mode, notAfter);

    [Fact]
    public void Valid_request_passes_with_default_approve_mode()
    {
        Assert.Null(JobApi.Check(Req(), out var mode));
        Assert.Equal(ApplyMode.Approve, mode);
        Assert.Null(JobApi.Check(Req(mode: "immediate"), out mode));
        Assert.Equal(ApplyMode.Immediate, mode);
        Assert.Null(JobApi.Check(Req(at: DateTime.Now.AddHours(1), notAfter: DateTime.Now.AddHours(2)), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Missing_firmware_id_is_400(string? fw) => Assert.Contains("firmwareId", JobApi.Check(Req(fw: fw), out _));

    [Fact]
    public void Missing_or_out_of_range_targets_are_400()
    {
        Assert.Contains("1~99", JobApi.Check(new JobCreateRequest("", "fw1", null, null, null, null, null), out _));
        Assert.Contains("1~99", JobApi.Check(Req(targets: []), out _));
        Assert.Contains("1~99", JobApi.Check(Req(targets: [0, 7]), out _));
        Assert.Contains("1~99", JobApi.Check(Req(targets: [100]), out _));
    }

    [Theory]
    [InlineData("later")]
    [InlineData("1")]        // 숫자는 받지 않음
    [InlineData("5")]
    public void Bad_apply_mode_is_400(string mode) => Assert.Contains("applyMode", JobApi.Check(Req(mode: mode), out _));

    [Fact]
    public void Bad_not_after_is_400()
    {
        Assert.Contains("지났", JobApi.Check(Req(notAfter: DateTime.Now.AddMinutes(-1)), out _));
        Assert.Contains("scheduledAt", JobApi.Check(Req(at: DateTime.Now.AddHours(2), notAfter: DateTime.Now.AddHours(1)), out _));
    }

    [Theory]
    [InlineData("F1107")]                    // STX 없이 적은 F
    [InlineData("<STX>F1107C001000 <ETX>")]
    [InlineData("<STX>R1107C001000 <ETX>")]
    [InlineData("xx<STX>F1107<ETX>")]        // 앞에 잡음이 있어도
    public void Update_entry_and_reply_frames_are_blocked_for_manual_send(string data) =>
        Assert.True(ApiGuard.IsBlockedManualFrame(AgvFrame.ParseData(data)));

    [Fact]
    public void Normal_frames_are_allowed_for_manual_send()
    {
        Assert.False(ApiGuard.IsBlockedManualFrame(AgvFrame.Build('W', '1', '1', 7, "C001000"u8.ToArray())));
        Assert.False(ApiGuard.IsBlockedManualFrame(AgvFrame.ParseData("<STX>E1107<ETX>")));
        Assert.True(ApiGuard.IsBlockedManualFrame(AgvFrame.Build('F', '1', '1', 7, "U"u8.ToArray())));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("http://192.168.10.5:8080", false)]
    [InlineData("http://192.168.10.5:8080/", false)]
    [InlineData("http://gateway.local:8080", true)]   // 호스트명 → 브리지 0.5.2 가 url_host 로 거부
    [InlineData("http://[::1]:8080", true)]
    [InlineData("192.168.10.5:8080", true)]           // 형식 오류
    public void PublicBaseUrl_must_be_an_ipv4_literal(string? url, bool problem) =>
        Assert.Equal(problem, new GatewayOptions { PublicBaseUrl = url }.PublicBaseUrlProblem() is not null);
}
