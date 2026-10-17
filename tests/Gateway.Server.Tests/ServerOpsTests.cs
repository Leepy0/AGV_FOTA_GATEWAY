using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Gateway.Core.Bridge;
using Gateway.Server.Data;
using Gateway.Server.Options;
using Gateway.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gateway.Server.Tests;

static class OpsKit
{
    public static string TempDir() => Path.Combine(Path.GetTempPath(), "gw-ops-" + Guid.NewGuid().ToString("N")[..8]);

    public static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    public static IOptions<GatewayOptions> Opts(int port = 0, bool autoSync = true) =>
        Microsoft.Extensions.Options.Options.Create(new GatewayOptions { UdpPort = port, RfidPort = 0, OfflineAfterSec = 5, ClockAutoSync = autoSync, ClockSkewWarnMs = 1000 });

    /// <summary>진짜 UDP 소켓으로 서비스를 띄우고 가짜 브리지 소켓을 돌려준다</summary>
    public static async Task<(BridgeUdpService svc, UdpClient bridge, IPEndPoint server)> StartAsync(AgvRegistry reg, bool autoSync = true)
    {
        var port = FreeUdpPort();
        var svc = new BridgeUdpService(Opts(port, autoSync), reg, NullLogger<BridgeUdpService>.Instance);
        await svc.StartAsync(CancellationToken.None);
        var bridge = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await Task.Delay(100);      // 서비스가 소켓을 열 시간
        return (svc, bridge, new IPEndPoint(IPAddress.Loopback, port));
    }

    public static async Task<(BridgeHeader h, byte[] payload)?> RecvAsync(UdpClient bridge, int ms = 3000, byte? type = null)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            using var cts = new CancellationTokenSource(Math.Max(1, (int)(until - DateTime.UtcNow).TotalMilliseconds));
            try
            {
                var r = await bridge.ReceiveAsync(cts.Token);
                if (BridgePacket.TryParse(r.Buffer, out var h, out var pl) && (type is null || h.Type == type)) return (h, pl.ToArray());
            }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    public static async Task SendAsync(UdpClient bridge, IPEndPoint server, byte type, int agv, ushort seq, string json)
    {
        var p = BridgePacket.Build(type, agv, seq, Encoding.UTF8.GetBytes(json));
        await bridge.SendAsync(p, p.Length, server);
    }


    /// <summary>패킷이 서비스에서 처리돼 호기가 온라인으로 보일 때까지</summary>
    public static async Task WaitOnlineAsync(AgvRegistry reg, int agv)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && !(reg.Get(agv)?.Online(5) ?? false)) await Task.Delay(10);
        Assert.True(reg.Get(agv)?.Online(5) ?? false, $"{agv} 호기가 온라인이 되지 않음");
    }
    public static string Heartbeat(long ts, long tsync, long up = 100, string cfg = "saved", string fw = "0.8.0")
        => $"{{\"fw\":\"{fw}\",\"part\":\"ota_0\",\"ota\":\"valid\",\"up\":{up},\"rssi\":-50,\"ch\":6,\"bssid\":\"aa:aa:aa:aa:aa:01\",\"disc\":0,\"cfg\":\"{cfg}\",\"ts\":{ts},\"tsync\":{tsync}}}";
}

/// <summary>서버 ↔ 브리지 시각 동기와 응답 대기 설정 명령 (진짜 UDP 소켓, 루프백)</summary>
public class BridgeUdpOpsTests
{
    [Fact]
    public async Task Time_request_is_answered_with_echo_and_server_times()
    {
        var reg = new AgvRegistry();
        var (svc, bridge, server) = await OpsKit.StartAsync(reg);
        try
        {
            var before = BridgeClock.NowUnixMs();
            await OpsKit.SendAsync(bridge, server, BridgePacket.TimeReq, 7, 1, "{\"t0\":424242}");
            var got = await OpsKit.RecvAsync(bridge, type: BridgePacket.Time);
            Assert.NotNull(got);
            using var d = JsonDocument.Parse(got!.Value.payload);
            Assert.Equal(424242, d.RootElement.GetProperty("t0").GetInt64());
            long t1 = d.RootElement.GetProperty("t1").GetInt64(), t2 = d.RootElement.GetProperty("t2").GetInt64();
            Assert.InRange(t1, before, BridgeClock.NowUnixMs());
            Assert.InRange(t2, t1, BridgeClock.NowUnixMs());
            Assert.Equal(1, reg.Get(7)!.TimeReplies);
        }
        finally { bridge.Dispose(); await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Malformed_time_request_gets_no_reply()
    {
        var reg = new AgvRegistry();
        var (svc, bridge, server) = await OpsKit.StartAsync(reg);
        try
        {
            string? trace = null;
            svc.Trace += t => trace = t;
            await OpsKit.SendAsync(bridge, server, BridgePacket.TimeReq, 7, 1, "{\"nope\":1}");
            Assert.Null(await OpsKit.RecvAsync(bridge, 400, BridgePacket.Time));
            Assert.Contains("TIME_REQ 형식 오류", trace);
        }
        finally { bridge.Dispose(); await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Heartbeat_ts_gives_skew_and_large_skew_triggers_a_push()
    {
        var reg = new AgvRegistry();
        var (svc, bridge, server) = await OpsKit.StartAsync(reg);
        try
        {
            // 브리지 시계가 5초 앞섬 → 편차 ≈ +5000 ms, 서버가 먼저 시각을 보냄
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 1, OpsKit.Heartbeat(BridgeClock.NowUnixMs() + 5000, 3));
            var push = await OpsKit.RecvAsync(bridge, type: BridgePacket.Time);
            Assert.NotNull(push);
            using var d = JsonDocument.Parse(push!.Value.payload);
            Assert.False(d.RootElement.TryGetProperty("t0", out _));            // 먼저 보내는 형태는 t2 만
            Assert.True(d.RootElement.TryGetProperty("t2", out _));
            var a = reg.Get(7)!;
            Assert.InRange(a.ClockSkewMs!.Value, 4500, 5500);

            // 1분 안에는 다시 보내지 않는다
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 2, OpsKit.Heartbeat(BridgeClock.NowUnixMs() + 5000, 4));
            Assert.Null(await OpsKit.RecvAsync(bridge, 400, BridgePacket.Time));
        }
        finally { bridge.Dispose(); await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Small_skew_is_recorded_without_a_push_and_old_firmware_has_no_clock()
    {
        var reg = new AgvRegistry();
        var (svc, bridge, server) = await OpsKit.StartAsync(reg);
        try
        {
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 1, OpsKit.Heartbeat(BridgeClock.NowUnixMs() + 20, 3));
            Assert.Null(await OpsKit.RecvAsync(bridge, 400, BridgePacket.Time));
            Assert.InRange(reg.Get(7)!.ClockSkewMs!.Value, -200, 400);

            // 시각 동기를 모르는 구 펌웨어(ts·tsync 없음): 편차 없음, 서버도 보내지 않음, SendTime 거부
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 8, 1, """{"fw":"0.7.0","up":500,"rssi":-50,"bssid":"aa","ch":6,"cfg":"saved"}""");
            Assert.Null(await OpsKit.RecvAsync(bridge, 400, BridgePacket.Time));
            Assert.Null(reg.Get(8)!.ClockSkewMs);
            Assert.False(svc.SendTime(8));
        }
        finally { bridge.Dispose(); await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Unsynced_bridge_gets_the_time_after_it_has_been_up_a_while_but_not_right_after_boot()
    {
        var reg = new AgvRegistry();
        var (svc, bridge, server) = await OpsKit.StartAsync(reg);
        try
        {
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 1, OpsKit.Heartbeat(0, -1, up: 5));   // 막 부팅 — 스스로 요청할 시간을 준다
            Assert.Null(await OpsKit.RecvAsync(bridge, 400, BridgePacket.Time));
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 2, OpsKit.Heartbeat(0, -1, up: 60));  // 한참 지나도 미동기 → 서버가 보냄
            Assert.NotNull(await OpsKit.RecvAsync(bridge, type: BridgePacket.Time));
        }
        finally { bridge.Dispose(); await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Auto_sync_off_never_pushes()
    {
        var reg = new AgvRegistry();
        var (svc, bridge, server) = await OpsKit.StartAsync(reg, autoSync: false);
        try
        {
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 1, OpsKit.Heartbeat(BridgeClock.NowUnixMs() + 9000, 3));
            Assert.Null(await OpsKit.RecvAsync(bridge, 400, BridgePacket.Time));
            Assert.InRange(reg.Get(7)!.ClockSkewMs!.Value, 8500, 9500);     // 편차는 기록
            Assert.True(svc.SendTime(7));                                   // 수동은 가능
            Assert.NotNull(await OpsKit.RecvAsync(bridge, type: BridgePacket.Time));
        }
        finally { bridge.Dispose(); await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cfg_request_is_matched_by_seq_and_strips_the_end_marker()
    {
        var reg = new AgvRegistry();
        var (svc, bridge, server) = await OpsKit.StartAsync(reg);
        try
        {
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 1, OpsKit.Heartbeat(BridgeClock.NowUnixMs(), 3));
            await OpsKit.WaitOnlineAsync(reg, 7);   // 호기 등록(온라인)
            var req = svc.RequestCfgAsync(7, "cfg json", TimeSpan.FromSeconds(3));
            var cmd = await OpsKit.RecvAsync(bridge, type: BridgePacket.CfgCmd);
            Assert.NotNull(cmd);
            Assert.Equal("cfg json", Encoding.UTF8.GetString(cmd!.Value.payload));
            // 다른 seq 의 응답은 무시하고, 요청 seq 의 응답으로 끝난다
            await OpsKit.SendAsync(bridge, server, BridgePacket.CfgResult, 7, (ushort)(cmd.Value.h.Seq + 100), "wrong\n>\n");
            Assert.False(req.IsCompleted);
            await OpsKit.SendAsync(bridge, server, BridgePacket.CfgResult, 7, cmd.Value.h.Seq, "{\"fw\":\"0.8.0\"}\n>\n");
            var reply = await req;
            Assert.True(reply.Ok);
            Assert.Equal("{\"fw\":\"0.8.0\"}", reply.Text);
        }
        finally { bridge.Dispose(); await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cfg_request_times_out_and_refuses_unknown_or_offline_agv()
    {
        var reg = new AgvRegistry();
        var (svc, bridge, server) = await OpsKit.StartAsync(reg);
        try
        {
            Assert.Contains("모릅니다", (await svc.RequestCfgAsync(9, "stat", TimeSpan.FromMilliseconds(200))).Error);
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 1, OpsKit.Heartbeat(BridgeClock.NowUnixMs(), 3));
            await OpsKit.WaitOnlineAsync(reg, 7);
            var silent = await svc.RequestCfgAsync(7, "stat", TimeSpan.FromMilliseconds(300));       // 가짜 브리지가 답하지 않음
            Assert.False(silent.Ok);
            Assert.Contains("응답 없음", silent.Error);
            reg.Get(7)!.LastSeen = DateTime.UtcNow.AddSeconds(-60);
            Assert.Equal("오프라인", (await svc.RequestCfgAsync(7, "stat", TimeSpan.FromMilliseconds(200))).Error);
        }
        finally { bridge.Dispose(); await svc.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Status_update_event_gives_previous_and_new_state()
    {
        var reg = new AgvRegistry();
        var (svc, bridge, server) = await OpsKit.StartAsync(reg);
        try
        {
            var seen = new List<(string? prev, string cur)>();
            svc.StatusUpdated += (a, prev, cur) => seen.Add((prev?.Cfg, cur.Cfg));
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 1, OpsKit.Heartbeat(BridgeClock.NowUnixMs(), 3, cfg: "trial"));
            await OpsKit.SendAsync(bridge, server, BridgePacket.Status, 7, 2, OpsKit.Heartbeat(BridgeClock.NowUnixMs(), 3, cfg: "saved"));
            var until = DateTime.UtcNow.AddSeconds(3);
            while (seen.Count < 2 && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.Equal([((string?)null, "trial"), ("trial", "saved")], seen);
        }
        finally { bridge.Dispose(); await svc.StopAsync(CancellationToken.None); }
    }
}
