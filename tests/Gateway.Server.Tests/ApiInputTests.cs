using Gateway.Core.Agv;
using Gateway.Server.Options;
using Gateway.Server.Services;

namespace Gateway.Server.Tests;

/// <summary>REST 입력 검사: POST /api/jobs (INT-A2), 수동 전송 F/R 차단 (INT-A8), PublicBaseUrl 브리지 규칙 (INT-A5, R2), 설정 명령 길이 (R18)</summary>
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
        Assert.False(ApiGuard.IsBlockedManualFrame("WF107C001000"u8));          // STX 없이 적은 W — PAN 글자가 F 여도 정상
        Assert.False(ApiGuard.IsBlockedManualFrame("WR107C001000"u8));
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
    [InlineData("http://192.168.10.5", false)]
    [InlineData("https://192.168.10.5:8443", false)]
    [InlineData("http://0.0.0.0:1", false)]
    [InlineData("http://255.255.255.255:65535/", false)]
    [InlineData("HTTP://192.168.10.5:8080", true)]    // 브리지 접두어 검사는 소문자만
    [InlineData("ftp://192.168.10.5:8080", true)]
    [InlineData("http://192.168.010.5:8080", true)]   // 앞자리 0 — .NET 은 192.168.8.5 로 받지만 브리지는 url_host
    [InlineData("http://0x7f.0.0.1:8080", true)]      // 16진
    [InlineData("http://3232235525:8080", true)]      // 정수
    [InlineData("http://user@192.168.10.5:8080", true)]
    [InlineData("http://192.168.10.256:8080", true)]
    [InlineData("http://1234.1.1.1:8080", true)]
    [InlineData("http://192.168.10:8080", true)]
    [InlineData("http://192.168.10.5:0", true)]       // 포트 1~65535
    [InlineData("http://192.168.10.5:65536", true)]
    [InlineData("http://192.168.10.5:", true)]
    [InlineData("http://192.168.10.5:123456", true)]
    [InlineData("http://192.168.10.5:8080/gw", true)] // 경로 없음 또는 '/' 만
    [InlineData("http://192.168.10.5:8080//", true)]
    [InlineData("http://192.168.10.5:8080?x=1", true)]
    public void PublicBaseUrl_must_be_an_ipv4_literal(string? url, bool problem) =>
        Assert.Equal(problem, new GatewayOptions { PublicBaseUrl = url }.PublicBaseUrlProblem() is not null);

    [Fact]
    public void Cfg_command_over_payload_max_is_refused()
    {
        Assert.Null(ApiGuard.CfgCommandProblem("set ip 192.168.10.107"));
        Assert.Null(ApiGuard.CfgCommandProblem(new string('a', Gateway.Core.Bridge.BridgePacket.PayloadMax)));
        Assert.Contains("1024", ApiGuard.CfgCommandProblem(new string('a', Gateway.Core.Bridge.BridgePacket.PayloadMax + 1)));
        Assert.Null(ApiGuard.CfgCommandProblem(new string('가', 341)));      // UTF-8 3바이트 × 341 = 1023
        Assert.NotNull(ApiGuard.CfgCommandProblem(new string('가', 342)));   // 3 × 342 = 1026
    }
}
