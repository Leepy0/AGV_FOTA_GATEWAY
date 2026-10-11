using Gateway.Core.Bridge;
using Gateway.Core.Fota;

namespace Gateway.Core.Tests;

/// <summary>
/// 가짜 브리지 자체 OTA: OTA_START 에 start→progress→done 을 보고하고 재부팅(up 감소, 슬롯 전환, ota=pending)한다.
/// PING 을 받으면 valid 로 확정. UDP 유실·지연·롤백·OTA 중 재부팅을 흉내 낼 수 있다.
/// </summary>
sealed class FakeOtaBridge : IAgvLink
{
    readonly List<AgvSubscription> _subs = new();
    public volatile string Fw = "0.5.1", Part = "ota_0", Ota = "valid";
    public long Up = 100;
    public string NewFw = "0.5.2";
    public int DropStarts, DropPings;      // 앞의 OTA_START·PING N개 유실
    public bool Silent;                    // 아무 보고도 재부팅도 안 함
    public bool Busy;                      // 이미 OTA 진행 중 (busy 보고)
    public int StartDelayMs;               // OTA 작업이 보고를 시작하기까지 지연 (그동안 수신 흔적 없음)
    public Func<string, bool>? DropReport; // true 면 그 보고(단계 이름)를 유실
    public string? FailErr;                // 다운로드 실패 보고
    public int Repeat = 1;                 // 최종 결과 반복 (브리지 0.5.2 는 3)
    public bool AutoConfirm;               // 재부팅 직후 ACS 중계 하향으로 이미 확정 → 첫 heartbeat 부터 valid
    public bool RollBack;                  // 새 펌웨어 기동 실패 → 부트로더가 이전 슬롯으로 (part·fw 그대로)
    public bool CrashMidway;               // 다운로드 중 브리지 재부팅 (done 없음, 이전 펌웨어)
    public bool HidePart;                  // heartbeat 에 part 없음 (구 펌웨어)
    public List<string> Sent = new();
    public int Pings;

    void Report(string js, string stage)
    {
        if (DropReport?.Invoke(stage) == true) return;
        int n = stage is "done" or "fail" ? Repeat : 1;
        AgvSubscription[] subs;
        lock (_subs) subs = _subs.ToArray();
        for (int i = 0; i < n; i++) foreach (var s in subs) s.OtaResults.Writer.TryWrite(js);
    }

    public bool SendOtaStart(int agv, string url)
    {
        Sent.Add(url);
        if (DropStarts > 0) { DropStarts--; return true; }
        if (Busy) { Report("""{"stage":"busy"}""", "busy"); return true; }
        if (Silent) return true;
        Busy = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(StartDelayMs);
            Ota = "busy";
            Report($$"""{"stage":"start","url":"{{url}}"}""", "start");
            if (FailErr is not null) { Ota = "valid"; Busy = false; Report($$"""{"stage":"fail","err":"{{FailErr}}"}""", "fail"); return; }
            Report("""{"stage":"progress","pct":50,"bytes":500}""", "progress");
            if (CrashMidway) { await Task.Delay(30); Up = 1; Ota = "valid"; Busy = false; return; }
            Report("""{"stage":"progress","pct":100,"bytes":1000}""", "progress");
            Report($$"""{"stage":"done","ver":"{{NewFw}}","bytes":1000}""", "done");
            await Task.Delay(30);
            Up = 1; Busy = false;
            if (RollBack) { Ota = "valid"; return; }
            Part = Part == "ota_0" ? "ota_1" : "ota_0";
            Fw = NewFw;
            Ota = AutoConfirm ? "valid" : "pending";
        });
        return true;
    }

    public bool SendPing(int agv)
    {
        Pings++;
        if (DropPings > 0) { DropPings--; return true; }
        if (Ota == "pending") Ota = "valid";
        return true;
    }

    public BridgeStatus Status() => BridgeStatus.Parse(System.Text.Encoding.UTF8.GetBytes(
        $"{{\"fw\":\"{Fw}\",{(HidePart ? "" : $"\"part\":\"{Part}\",")}\"ota\":\"{Ota}\",\"up\":{Up}}}"))!;

    public bool SendMainDown(int agv, ReadOnlySpan<byte> frame) => true;
    public bool SendDspOtaStart(int agv, string json) => true;
    public bool SendDspOtaApply(int agv, string json) => true;
    public bool SendInfoRequest(int agv) => true;
    public AgvSubscription Subscribe(int agv) { AgvSubscription? s = null; s = new(() => { lock (_subs) _subs.Remove(s!); }); lock (_subs) _subs.Add(s); return s; }
    public string? LocalAddressToward(int agv) => "127.0.0.1";
}

public class EspOtaClientTests
{
    const string Url = "http://10.0.0.1:8080/fw/esp/agv_wifi_bridge.bin";

    static readonly OtaTimeouts Fast = new()
    {
        EspResult = TimeSpan.FromMilliseconds(2000), EspReboot = TimeSpan.FromMilliseconds(2000), EspConfirm = TimeSpan.FromMilliseconds(1000),   // CI 러너가 느려도 여유
        PingInterval = TimeSpan.FromMilliseconds(20), Resend = TimeSpan.FromMilliseconds(100), Poll = TimeSpan.FromMilliseconds(10),
    };

    static async Task<(string Ver, List<string> Log)> Run(FakeOtaBridge b, string expected = "0.5.2")
    {
        var log = new List<string>();
        var c = new EspOtaClient(b, _ => b.Status(), Fast);
        var ver = await c.RunAsync(7, Url, expected, line => { lock (log) log.Add(line); }, CancellationToken.None);
        return (ver, log);
    }

    [Fact]
    public async Task Normal_flow_reboots_and_confirms_with_ping()
    {
        var b = new FakeOtaBridge { Repeat = 3 };
        var (ver, log) = await Run(b);
        Assert.Equal("0.5.2", ver);
        Assert.Single(b.Sent);
        Assert.True(b.Pings >= 1);
        Assert.Equal(("ota_1", "valid"), (b.Part, b.Ota));
        Assert.Contains(log, l => l.StartsWith("완료 fw 0.5.2"));
    }

    [Fact]
    public async Task Lost_ota_start_is_resent()
    {
        var b = new FakeOtaBridge { DropStarts = 1 };
        var (ver, log) = await Run(b);
        Assert.Equal("0.5.2", ver);
        Assert.True(b.Sent.Count >= 2);
        Assert.Contains(log, l => l.StartsWith("OTA_START 재전송 (1/"));
    }

    [Fact]
    public async Task Busy_after_resend_means_first_command_is_running()
    {
        var b = new FakeOtaBridge { StartDelayMs = 300 };
        var (ver, log) = await Run(b);
        Assert.Equal("0.5.2", ver);
        Assert.True(b.Sent.Count >= 2);
        Assert.Contains(log, l => l.Contains("이미 진행 중"));
    }

    [Fact]
    public async Task Busy_before_any_resend_fails()
    {
        var b = new FakeOtaBridge { Busy = true };
        var ex = await Assert.ThrowsAsync<EspOtaException>(() => Run(b));
        Assert.Contains("이미 OTA 진행 중", ex.Message);
    }

    [Theory]
    [InlineData("mainboard_staging", "스테이징")]
    [InlineData("mainboard_programming", "메인보드를 굽는 중")]
    [InlineData("url_host", "PublicBaseUrl")]
    [InlineData("no_mem", "메모리")]
    [InlineData("bad_url", "URL 형식")]
    public async Task Fail_codes_are_explained_in_korean(string err, string hint)
    {
        var b = new FakeOtaBridge { FailErr = err };
        var ex = await Assert.ThrowsAsync<EspOtaException>(() => Run(b));
        Assert.Contains(err, ex.Message);
        Assert.Contains(hint, ex.Message);
    }

    [Fact]
    public async Task Fail_report_throws_with_err()
    {
        var b = new FakeOtaBridge { FailErr = "perform ESP_FAIL", Repeat = 3 };
        var ex = await Assert.ThrowsAsync<EspOtaException>(() => Run(b));
        Assert.Contains("perform ESP_FAIL", ex.Message);
    }

    [Fact]
    public async Task Silent_bridge_times_out_after_resends()
    {
        var b = new FakeOtaBridge { Silent = true };
        var ex = await Assert.ThrowsAsync<EspOtaException>(() => Run(b));
        Assert.Contains("OTA_RESULT 없음", ex.Message);
        Assert.Equal(1 + Fast.MaxResends, b.Sent.Count);
    }

    [Fact]
    public async Task Lost_done_report_is_taken_from_heartbeat()
    {
        var b = new FakeOtaBridge { DropReport = s => s == "done" };
        var (ver, log) = await Run(b);
        Assert.Equal("0.5.2", ver);
        Assert.Contains(log, l => l.StartsWith("재부팅 확인 — OTA_RESULT done 미수신"));
        Assert.Equal("valid", b.Ota);
    }

    [Fact]
    public async Task Lost_pings_are_retried_until_valid()
    {
        var b = new FakeOtaBridge { DropPings = 2 };
        var (ver, _) = await Run(b);
        Assert.Equal("0.5.2", ver);
        Assert.True(b.Pings >= 3, $"PING {b.Pings}회");
        Assert.Equal("valid", b.Ota);
    }

    [Fact]
    public async Task Never_confirmed_fails_with_ping_count()
    {
        var b = new FakeOtaBridge { DropPings = 10_000 };
        var ex = await Assert.ThrowsAsync<EspOtaException>(() => Run(b));
        Assert.Contains("확정(valid) 안 됨", ex.Message);
        Assert.Contains("PING", ex.Message);
        Assert.True(b.Pings >= 5);
    }

    [Fact]
    public async Task Reinstall_same_version_already_confirmed_by_acs_traffic()
    {
        // 같은 버전 재설치 + 재부팅 직후 ACS 하향으로 확정 → fw 같고 첫 heartbeat 부터 valid. 슬롯 변경으로 재부팅 확인
        var b = new FakeOtaBridge { NewFw = "0.5.1", AutoConfirm = true };
        var (ver, _) = await Run(b, "0.5.1");
        Assert.Equal("0.5.1", ver);
        Assert.Equal(0, b.Pings);
    }

    [Fact]
    public async Task Reinstall_without_part_field_uses_uptime_drop()
    {
        var b = new FakeOtaBridge { NewFw = "0.5.1", AutoConfirm = true, HidePart = true };
        var (ver, _) = await Run(b, "0.5.1");
        Assert.Equal("0.5.1", ver);
    }

    [Fact]
    public async Task Reboot_into_old_slot_is_reported_as_rollback()
    {
        var b = new FakeOtaBridge { NewFw = "0.5.1", RollBack = true };
        var ex = await Assert.ThrowsAsync<EspOtaException>(() => Run(b, "0.5.1"));
        Assert.Contains("이전 슬롯(ota_0)", ex.Message);
    }

    [Fact]
    public async Task Bridge_reboot_during_download_fails_without_waiting()
    {
        var b = new FakeOtaBridge { CrashMidway = true };
        var ex = await Assert.ThrowsAsync<EspOtaException>(() => Run(b));
        Assert.Contains("OTA 중 브리지가 재부팅됨", ex.Message);
    }

    [Fact]
    public async Task Version_mismatch_throws()
    {
        var b = new FakeOtaBridge();
        var ex = await Assert.ThrowsAsync<EspOtaException>(() => Run(b, "0.6.0"));
        Assert.Contains("버전 불일치", ex.Message);
    }

    [Fact]
    public async Task Cancel_is_cancellation()
    {
        var b = new FakeOtaBridge { Silent = true };
        var c = new EspOtaClient(b, _ => b.Status());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.RunAsync(7, Url, "", null, cts.Token));
    }
}
