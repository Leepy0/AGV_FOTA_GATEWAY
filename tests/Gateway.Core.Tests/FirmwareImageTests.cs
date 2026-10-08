using Gateway.Core.Fota;

namespace Gateway.Core.Tests;

public class FirmwareImageTests
{
    [Fact]
    public void Crc32_vector() => Assert.Equal(0xCBF43926u, FirmwareImage.Crc32("123456789"u8));

    [Fact]
    public void Build_then_parse_roundtrip()
    {
        var app = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
        var img = FirmwareImage.Build(app, "1.2.3", 42, 1, "agv-dsp");
        Assert.Equal(64 + app.Length, img.Length);
        var info = FirmwareImage.Parse(img)!;
        Assert.Equal(("1.2.3", "agv-dsp", 42u, "B", (uint)app.Length), (info.Version, info.Project, info.Seq, info.SlotName, info.AppSize));
        img[100] ^= 1;
        Assert.Null(FirmwareImage.Parse(img));
    }
}
