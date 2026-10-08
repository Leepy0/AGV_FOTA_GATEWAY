using System.Buffers.Binary;
using System.Text;

namespace Gateway.Core.Fota;

/// <summary>DSP 펌웨어 이미지 헤더 (DSP_OTA/core/ota_image.h 와 동일, 64바이트) + 앱 바이너리.</summary>
public static class FirmwareImage
{
    public const int HeaderLength = 64;
    public const byte SlotAny = 0xFF;
    public const string DefaultProject = "agv-dsp";

    static readonly uint[] Tbl = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32 IEEE (zlib 과 동일)</summary>
    public static uint Crc32(ReadOnlySpan<byte> d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d) c = Tbl[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    public static byte[] Build(ReadOnlySpan<byte> app, string version, uint seq, byte slot, string project = DefaultProject)
    {
        var h = new byte[HeaderLength];
        "DOTA"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), (uint)app.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), Crc32(app));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), slot);
        Encoding.ASCII.GetBytes(version.Length > 16 ? version[..16] : version).CopyTo(h, 24);
        Encoding.ASCII.GetBytes(project.Length > 16 ? project[..16] : project).CopyTo(h, 40);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(60), Crc32(h.AsSpan(0, 60)));
        return [.. h, .. app];
    }

    public static FirmwareImageInfo? Parse(ReadOnlySpan<byte> img)
    {
        if (img.Length < HeaderLength || !img[..4].SequenceEqual("DOTA"u8)) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(img[60..]) != Crc32(img[..60])) return null;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(img[8..]), crc = BinaryPrimitives.ReadUInt32LittleEndian(img[12..]);
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(img[16..]);
        byte slot = (byte)(BinaryPrimitives.ReadUInt32LittleEndian(img[20..]) & 0xFF);
        if (img.Length != HeaderLength + size || Crc32(img[HeaderLength..]) != crc) return null;
        return new FirmwareImageInfo(
            Encoding.ASCII.GetString(img.Slice(24, 16)).TrimEnd('\0'),
            Encoding.ASCII.GetString(img.Slice(40, 16)).TrimEnd('\0'),
            seq, slot, size, crc);
    }

    public static string SlotName(byte slot) => slot switch { 0 => "A", 1 => "B", SlotAny => "any", _ => $"?{slot}" };
    public static byte SlotFromName(string s) => s.Trim().ToUpperInvariant() switch { "A" or "0" => 0, "B" or "1" => 1, _ => SlotAny };
}

public sealed record FirmwareImageInfo(string Version, string Project, uint Seq, byte Slot, uint AppSize, uint AppCrc)
{
    public string SlotName => FirmwareImage.SlotName(Slot);
}
