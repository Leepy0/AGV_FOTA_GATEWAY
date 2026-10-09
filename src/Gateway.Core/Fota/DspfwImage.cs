using System.Buffers.Binary;
using System.Text;

namespace Gateway.Core.Fota;

/// <summary>
/// 메인보드 펌웨어 이미지 파일 (.dspfw) — AGV_Wifi_Bridge/tools/dsp_pack.py 가 만들고 브리지가 스테이징한다.
/// 파일 헤더 32B: magic "AGVDSP1\0"(8) + total_len u32(파일 전체) + crc32 u32(헤더 뒤 본문) + ver 16B ASCII(NUL 패딩).
/// 본문 = 앱 헤더 64워드 + 블록들 (서버는 본문 내용을 해석하지 않는다).
/// </summary>
public static class DspfwImage
{
    public const int HeaderLength = 32;
    static readonly byte[] Magic = "AGVDSP1\0"u8.ToArray();

    static readonly uint[] Tbl = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32 IEEE (zlib·dsp_pack.py 와 동일)</summary>
    public static uint Crc32(ReadOnlySpan<byte> d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d) c = Tbl[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    /// <summary>헤더·길이·본문 CRC 가 맞으면 정보, 아니면 null</summary>
    public static DspfwInfo? Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderLength || !file[..8].SequenceEqual(Magic)) return null;
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        if (total != (uint)file.Length) return null;
        if (Crc32(file[HeaderLength..]) != crc) return null;
        var ver = Encoding.ASCII.GetString(file.Slice(16, 16)).TrimEnd('\0');
        return new DspfwInfo(ver, crc, file.Length);
    }

    /// <summary>테스트·도구용: 본문을 감싼 .dspfw 파일 생성</summary>
    public static byte[] Build(ReadOnlySpan<byte> body, string version)
    {
        var f = new byte[HeaderLength + body.Length];
        Magic.CopyTo(f, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), (uint)f.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(12), Crc32(body));
        Encoding.ASCII.GetBytes(version.Length > 15 ? version[..15] : version).CopyTo(f, 16);
        body.CopyTo(f.AsSpan(HeaderLength));
        return f;
    }
}

/// <summary>Crc32 = 본문 CRC (DSP_OTA_START 의 crc32), Size = 파일 전체 길이 (size)</summary>
public sealed record DspfwInfo(string Version, uint Crc32, int Size);
