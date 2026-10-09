using System.Buffers.Binary;
using System.Text;

namespace Gateway.Core.Fota;

/// <summary>
/// ESP-IDF 앱 이미지(agv_wifi_bridge.bin)의 버전·프로젝트를 읽는다.
/// esp_image_header_t(24B, magic 0xE9) + 세그먼트 헤더(8B) 뒤에 esp_app_desc_t:
///   magic 0xABCD5432 @32, version[32] @48, project_name[32] @80, time[16] @112, date[16] @128, idf_ver[32] @144
/// </summary>
public static class EspAppImage
{
    public const string BridgeProject = "agv_wifi_bridge";

    public static EspAppInfo? Parse(ReadOnlySpan<byte> f)
    {
        if (f.Length < 256 || f[0] != 0xE9) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(f[32..]) != 0xABCD5432) return null;
        return new EspAppInfo(S(f, 48, 32), S(f, 80, 32), $"{S(f, 128, 16)} {S(f, 112, 16)}".Trim(), S(f, 144, 32));
    }

    static string S(ReadOnlySpan<byte> f, int off, int len)
        => Encoding.ASCII.GetString(f.Slice(off, len)).Split('\0')[0].Trim();

    /// <summary>테스트용 최소 이미지 생성</summary>
    public static byte[] Build(string version, string project, string idf = "v6.1")
    {
        var f = new byte[512];
        f[0] = 0xE9;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(32), 0xABCD5432);
        Encoding.ASCII.GetBytes(version.Length > 31 ? version[..31] : version).CopyTo(f, 48);
        Encoding.ASCII.GetBytes(project.Length > 31 ? project[..31] : project).CopyTo(f, 80);
        Encoding.ASCII.GetBytes("12:00:00").CopyTo(f, 112);
        Encoding.ASCII.GetBytes("Oct  9 2026").CopyTo(f, 128);
        Encoding.ASCII.GetBytes(idf).CopyTo(f, 144);
        return f;
    }
}

public sealed record EspAppInfo(string Version, string Project, string BuildDate, string IdfVersion);
