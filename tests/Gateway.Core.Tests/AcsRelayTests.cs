using System.Net;
using Gateway.Core.Acs;
using Gateway.Core.Agv;
using Gateway.Core.Positions;

namespace Gateway.Core.Tests;

public class AcsRelayPolicyTests
{
    [Theory]
    [InlineData('F', true)]
    [InlineData('R', true)]
    [InlineData('W', false)]
    [InlineData('N', false)]
    [InlineData('E', false)]
    public void Down_frames_F_and_R_are_blocked(char type, bool blocked)
    {
        var f = AgvFrame.Build(type, '1', '1', 7, "U"u8);
        Assert.Equal(blocked, AcsRelayPolicy.IsBlockedDown(f));
    }

    [Fact]
    public void Blocked_check_handles_short_input()
    {
        Assert.False(AcsRelayPolicy.IsBlockedDown([]));
        Assert.False(AcsRelayPolicy.IsBlockedDown([AgvFrame.Stx]));
    }

    [Fact]
    public void Empty_allow_list_allows_everyone()
    {
        Assert.True(AcsRelayPolicy.IsAllowedRemote(IPAddress.Parse("10.0.0.5"), []));
        Assert.True(AcsRelayPolicy.IsAllowedRemote(IPAddress.Parse("10.0.0.5"), null));
        Assert.True(AcsRelayPolicy.IsAllowedRemote(IPAddress.Parse("10.0.0.5"), ["", "  "]));
    }

    [Fact]
    public void Allow_list_matches_exact_ip_including_ipv4_mapped()
    {
        string[] allow = [" 192.168.10.20 ", "192.168.10.21"];
        Assert.True(AcsRelayPolicy.IsAllowedRemote(IPAddress.Parse("192.168.10.20"), allow));
        Assert.True(AcsRelayPolicy.IsAllowedRemote(IPAddress.Parse("192.168.10.21").MapToIPv6(), allow));
        Assert.False(AcsRelayPolicy.IsAllowedRemote(IPAddress.Parse("192.168.10.22"), allow));
        Assert.False(AcsRelayPolicy.IsAllowedRemote(null, allow));
    }

    [Fact]
    public void Invalid_entries_are_ignored_and_reported()
    {
        string[] allow = ["acs-pc", "192.168.10.20"];
        Assert.Equal(["acs-pc"], AcsRelayPolicy.InvalidEntries(allow));
        Assert.True(AcsRelayPolicy.IsAllowedRemote(IPAddress.Parse("192.168.10.20"), allow));
        // 해석되는 항목이 하나도 없으면 아무도 허용하지 않음 (오타로 전부 열리지 않게)
        Assert.False(AcsRelayPolicy.IsAllowedRemote(IPAddress.Parse("192.168.10.20"), ["acs-pc"]));
    }
}

public class VersionedPositionsTests
{
    static AgvPosition P(int agv, double x, string state = PositionState.Tracked, bool online = true) =>
        new(agv, DateTimeOffset.UtcNow, x, 0, 0, state, Online: online);

    [Fact]
    public void Restored_entries_are_not_reported_as_changes()
    {
        var m = new VersionedPositions();
        m.Restore(P(5, 12.3, PositionState.Lost, online: false));
        var (changed, ver) = m.ChangedSince(0);
        Assert.Empty(changed);
        Assert.Equal(0, ver);
        Assert.Equal(PositionState.Lost, m.Get(5)!.State);
    }

    [Fact]
    public void ChangedSince_returns_changes_and_watermark()
    {
        var m = new VersionedPositions();
        m.Set(P(1, 1)); m.Set(P(2, 2));
        var (c1, v1) = m.ChangedSince(0);
        Assert.Equal([1, 2], c1.Select(p => p.Agv));
        m.Set(P(2, 3));
        var (c2, v2) = m.ChangedSince(v1);
        Assert.Equal(3.0, Assert.Single(c2).X);
        Assert.Empty(m.ChangedSince(v2).changed);
        var (all, va) = m.All();
        Assert.Equal(2, all.Count); Assert.Equal(v2, va);
    }

    [Fact]
    public void Update_changes_only_when_function_returns_value()
    {
        var m = new VersionedPositions();
        Assert.Null(m.Update(9, p => p));                 // 없는 호기
        m.Set(P(9, 1));
        var v = m.Version;
        Assert.Null(m.Update(9, _ => null));
        Assert.Equal(v, m.Version);
        var n = m.Update(9, p => p with { Online = false, State = PositionState.Lost });
        Assert.Equal(PositionState.Lost, n!.State);
        Assert.Equal(v + 1, m.Version);
    }

    // I-11: 쓰기와 발행 주기가 겹쳐도, 워터마크를 따라가며 읽은 쪽이 결국 모든 호기의 마지막 값을 본다
    [Fact]
    public async Task Concurrent_writers_never_lose_a_change_behind_the_watermark()
    {
        var m = new VersionedPositions();
        const int agvs = 8, perAgv = 20000;
        var seen = new Dictionary<int, double>();
        long mark = 0;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var (changed, v) = m.ChangedSince(mark);
                foreach (var p in changed) seen[p.Agv] = p.X!.Value;
                mark = v;
            }
        });
        await Task.WhenAll(Enumerable.Range(1, agvs).Select(a => Task.Run(() =>
        {
            for (int i = 1; i <= perAgv; i++) m.Set(P(a, i));
        })));
        stop.Cancel();
        await reader;
        // 쓰기가 끝난 뒤 한 번 더 읽어도, 리더가 이미 본 워터마크 뒤에 남은 변경은 없어야 한다 → 마지막 주기에서 다 잡힘
        var (rest, _) = m.ChangedSince(mark);
        foreach (var p in rest) seen[p.Agv] = p.X!.Value;
        for (int a = 1; a <= agvs; a++) Assert.Equal(perAgv, seen[a]);
        Assert.Equal(agvs * perAgv, m.Version);
    }
}
