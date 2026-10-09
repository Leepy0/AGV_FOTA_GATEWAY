using Gateway.Core.Fota;

namespace Gateway.Core.Tests;

public class EspAppImageTests
{
    [Fact]
    public void Parses_version_and_project_from_app_desc()
    {
        var f = EspAppImage.Build("0.5.0-abc1234", "agv_wifi_bridge");
        var i = EspAppImage.Parse(f)!;
        Assert.Equal(("0.5.0-abc1234", "agv_wifi_bridge", "v6.1"), (i.Version, i.Project, i.IdfVersion));
        Assert.Contains("Oct", i.BuildDate);
        Assert.Null(EspAppImage.Parse(new byte[300]));
        f[0] = 0x00; Assert.Null(EspAppImage.Parse(f));
    }
}
