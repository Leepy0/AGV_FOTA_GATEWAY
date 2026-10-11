using Gateway.Core.Agv;

namespace Gateway.Core.Tests;

public class AgvFrameTests
{
    [Theory]
    [InlineData("H", '8')]
    [InlineData("12345678", '4')]
    [InlineData("E3107I012245 ", '7')]
    public void Bcc_matches_protocol_vectors(string payload, char expected)
        => Assert.Equal((byte)expected, AgvFrame.Bcc(System.Text.Encoding.ASCII.GetBytes(payload)));

    [Fact]
    public void Crc16_ccitt_false_vector() => Assert.Equal(0x29B1, AgvFrame.Crc16("123456789"u8));

    [Fact]
    public void Build_then_parse_roundtrip()
    {
        var f = AgvFrame.Build('F', '1', 'A', 7, "S"u8);
        Assert.Equal(0x02, f[0]); Assert.Equal(0x03, f[^1]);
        Assert.True(AgvFrame.TryParse(f, out var p));
        Assert.Equal(('F', '1', 'A', 7), (p.Type, p.Pan, p.Msg, p.Agv));
        Assert.Equal("S", System.Text.Encoding.ASCII.GetString(p.Body));
        Assert.Equal(7, AgvFrame.PeekAgv(f));
    }

    [Fact]
    public void Parse_rejects_bad_bcc_and_accepts_lowercase()
    {
        var f = AgvFrame.Build('W', '1', '1', 7, "C001000 00000"u8);
        var bad = (byte[])f.Clone(); bad[^2] ^= 1;
        Assert.False(AgvFrame.TryParse(bad, out _));
        var lower = (byte[])f.Clone(); lower[^2] = (byte)char.ToLower((char)lower[^2]);
        Assert.Equal(AgvFrame.TryParse(f, out _), AgvFrame.TryParse(lower, out _));
    }

    [Fact]
    public void IsForAcs_whitelists_only_E_and_ack()
    {
        Assert.True(AgvFrame.IsForAcs(AgvFrame.Build('E', '3', '1', 7, "I012245"u8)));
        Assert.True(AgvFrame.IsForAcs(AgvFrame.Build('A', '1', '1', 7, "WCPA"u8)));
        Assert.False(AgvFrame.IsForAcs(AgvFrame.Build('P', '1', '1', 7, "L=1,R=2"u8)));    // 위치(제안)
        Assert.False(AgvFrame.IsForAcs(AgvFrame.Build('R', '1', '1', 7, "V1.2.3"u8)));     // 브리지용 응답
        Assert.False(AgvFrame.IsForAcs("\x02E31"u8));
    }

    [Fact]
    public void IsForAcs_rejects_truncated_or_corrupt_frames()
    {
        var e = AgvFrame.Build('E', '3', '1', 7, "I012245"u8);
        Assert.False(AgvFrame.IsForAcs(e[..^1]));            // ETX 없이 시간 초과로 끊긴 조각 (uart_bridge no_etx)
        Assert.False(AgvFrame.IsForAcs(e[..10]));            // 중간에서 잘린 조각 (overlong)
        var bad = (byte[])e.Clone(); bad[7] ^= 0x01;         // 노이즈로 본문 1비트 깨짐 → BCC 불일치
        Assert.False(AgvFrame.IsForAcs(bad));
        Assert.True(AgvFrame.IsAcsKind(e[..^1]));            // 깨진 ACS 프레임으로 집계할 대상
        Assert.False(AgvFrame.IsAcsKind(AgvFrame.Build('R', '1', '1', 7, "V1"u8)));
    }

    [Fact]
    public void Split_handles_partial_and_concatenated_frames()
    {
        var a = AgvFrame.Build('E', '1', '1', 7, "I012245"u8);
        var b = AgvFrame.Build('A', '1', '2', 7, "FSPA00"u8);
        byte[] rest = [];
        var first = AgvFrame.Split([.. a, .. b[..5]], ref rest);
        Assert.Single(first); Assert.Equal(a, first[0]); Assert.Equal(5, rest.Length);
        var second = AgvFrame.Split(b[5..], ref rest);
        Assert.Single(second); Assert.Equal(b, second[0]); Assert.Empty(rest);
    }

    [Fact]
    public void ParseData_and_Fmt_roundtrip()
    {
        var d = AgvFrame.ParseData("<STX>W1107C001000 00000 5<ETX>\\x7F");
        Assert.Equal(0x02, d[0]); Assert.Equal(0x7F, d[^1]);
        Assert.Equal("<STX>W1107C001000 00000 5<ETX>\\x7F", AgvFrame.Fmt(d));
    }
}
