using Gateway.Core.Fota;

namespace Gateway.Core.Tests;

public class DspfwImageTests
{
    [Fact]
    public void Crc32_vector() => Assert.Equal(0xCBF43926u, DspfwImage.Crc32("123456789"u8));

    [Fact]
    public void Build_then_parse_roundtrip_and_tamper()
    {
        var body = Enumerable.Range(0, 3000).Select(i => (byte)(i * 13)).ToArray();
        var f = DspfwImage.Build(body, "1.2.3");
        Assert.Equal("AGVDSP1\0"u8.ToArray(), f[..8]);
        var info = DspfwImage.Parse(f)!;
        Assert.Equal(("1.2.3", f.Length), (info.Version, info.Size));
        Assert.Equal(DspfwImage.Crc32(body), info.Crc32);
        f[100] ^= 1;
        Assert.Null(DspfwImage.Parse(f));
        Assert.Null(DspfwImage.Parse("not a dspfw"u8));
    }
}
