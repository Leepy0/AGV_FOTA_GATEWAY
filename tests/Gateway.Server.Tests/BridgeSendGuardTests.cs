using System.Net;
using System.Net.Sockets;
using Gateway.Core.Bridge;
using Gateway.Server.Options;
using Gateway.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Server.Tests;

/// <summary>BridgeUdpService.Send: 패킷 한도를 넘는 payload 는 예외 대신 false (R18 방어)</summary>
public class BridgeSendGuardTests
{
    [Fact]
    public async Task Oversized_payload_returns_false_instead_of_throwing()
    {
        using var bridge = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var reg = new AgvRegistry();
        var a = reg.GetOrAdd(7);
        a.EndPoint = (IPEndPoint)bridge.Client.LocalEndPoint!;
        a.LastSeen = DateTime.UtcNow;
        var svc = new BridgeUdpService(Microsoft.Extensions.Options.Options.Create(new GatewayOptions { UdpPort = 0, RfidPort = 0 }), reg, NullLogger<BridgeUdpService>.Instance);
        string? trace = null;
        svc.Trace += t => trace = t;
        await svc.StartAsync(CancellationToken.None);
        try
        {
            // 소켓이 열릴 때까지 (ExecuteAsync 안에서 연다)
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!svc.SendInfoRequest(7) && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.True(svc.SendInfoRequest(7));

            Assert.False(svc.SendCfg(7, new string('a', BridgePacket.PayloadMax + 1)));
            Assert.Contains("넘어 송신 안 함", trace);
            Assert.True(svc.SendCfg(7, new string('a', BridgePacket.PayloadMax)));
        }
        finally { await svc.StopAsync(CancellationToken.None); }
    }
}
