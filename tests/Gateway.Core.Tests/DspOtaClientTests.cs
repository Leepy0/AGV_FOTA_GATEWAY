using System.Text;
using Gateway.Core.Agv;
using Gateway.Core.Fota;

namespace Gateway.Core.Tests;

/// <summary>가짜 링크: 요청을 받아 규격대로 응답하는 최소 DSP. 첫 요청 n 번은 응답을 떨어뜨려 재전송을 검사한다</summary>
sealed class FakeDspLink : IAgvLink
{
    readonly List<AgvSubscription> _subs = new();
    public int DropFirst;
    public HashSet<int> DropAt = new();      // n번째 요청의 응답을 떨어뜨림 (재전송 검사)
    public int Received;
    public int Blocks = -1;

    public bool SendMainDown(int agv, ReadOnlySpan<byte> frame)
    {
        Received++;
        if (DropFirst-- > 0 || DropAt.Contains(Received)) return true;
        Assert.True(AgvFrame.TryParse(frame, out var p));
        Assert.True(frame.Length <= AgvFrame.BridgeFrameMax);
        var cmd = (char)p.Body[0];      // 요청 본문 = cmd + 인자 (F 는 종류 문자)
        string extra = cmd switch
        {
            'S' => "AVN" + "00000064" + "0000A400" + "1.0.0".PadRight(16),
            'B' => "B" + ((Blocks = (Convert.ToInt32(Encoding.ASCII.GetString(p.Body, 1, 8), 16) + 63) / 64)).ToString("X4"),
            'D' => Encoding.ASCII.GetString(p.Body, 1, 4),
            'E' => "2.0.0".PadRight(16),
            'C' => "V",
            _ => "",
        };
        var reply = AgvFrame.Build('A', p.Pan, p.Msg, agv, Encoding.ASCII.GetBytes("F" + cmd + "PA00" + extra));
        foreach (var s in _subs.ToArray()) s.Frames.Writer.TryWrite(reply);
        return true;
    }
    public bool SendOtaStart(int agv, string url) => true;
    public bool SendPing(int agv) => true;
    public AgvSubscription Subscribe(int agv) { AgvSubscription? s = null; s = new(() => _subs.Remove(s!)); _subs.Add(s); return s; }
    public string? LocalAddressToward(int agv) => "127.0.0.1";
}

public class DspOtaClientTests
{
    [Fact]
    public async Task Status_parses_reply()
    {
        var link = new FakeDspLink();
        using var c = new DspOtaClient(link, 7);
        var st = await c.StatusAsync();
        Assert.Equal(('A', 'V', 'N', 100u, 0xA400u, "1.0.0"), (st.Slot, st.State, st.Other, st.Seq, st.Size, st.Version));
    }

    [Fact]
    public async Task Update_sends_all_blocks_and_retries_lost_replies()
    {
        var link = new FakeDspLink { DropAt = { 3, 5 } };      // D#1, D#2 의 첫 응답 유실 → 0.5초 뒤 재전송
        using var c = new DspOtaClient(link, 7);
        var img = FirmwareImage.Build(new byte[1000], "2.0.0", 1, FirmwareImage.SlotAny);
        int last = 0;
        var slot = await c.UpdateAsync(img, 64, (d, t) => last = d);
        Assert.Equal('B', slot);
        Assert.Equal(link.Blocks, last);
        Assert.Equal(2, c.Retries);
        Assert.Equal(1 + link.Blocks + 1 + 2, link.Received);   // B + D×n + E + 재전송 2
    }

    [Fact]
    public async Task Request_times_out_after_retries()
    {
        var link = new FakeDspLink { DropFirst = 100 };
        using var c = new DspOtaClient(link, 7);
        var ex = await Assert.ThrowsAsync<DspOtaException>(() => c.RequestAsync('S', null, 20, 2, CancellationToken.None));
        Assert.Contains("응답 없음", ex.Message);
    }
}
