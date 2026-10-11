using System.Buffers.Binary;
using System.Text;

namespace Gateway.Core.Fota;

/// <summary>
/// 메인보드 펌웨어 이미지 파일 (.dspfw) — AGV_Wifi_Bridge/tools/dsp_pack.py 가 만들고 브리지가 스테이징한다.
/// 파일 헤더 32B: magic "AGVDSP1\0"(8) + total_len u32(파일 전체) + crc32 u32(헤더 뒤 본문) + ver 16B ASCII(NUL 패딩).
/// 본문 = 앱 헤더 64워드 + 블록들 [addr u32(워드 주소) nw u16 data nw워드(LE)].
/// Parse 는 dsp_pack.py unpack()·브리지 dsp_image.* 와 같은 규칙으로 본문까지 검사한다 — 메인보드를 지운 뒤에야 드러날 이미지를 업로드에서 막는다.
/// </summary>
public static class DspfwImage
{
    public const int HeaderLength = 32;
    static readonly byte[] Magic = "AGVDSP1\0"u8.ToArray();

    // 메인보드 플래시 배치·앱 헤더 (AGV_Wifi_Bridge dsp/common/dsp_image.h, bootproto.h 와 같은 값)
    public const uint AppCodeAddr = 0x3E8040;        // 앱 codestart (블록 주소 하한)
    public const uint AppEnd = 0x3F4000;             // 앱 영역 끝 (exclusive)
    public const uint AppMaxWords = AppEnd - AppCodeAddr;
    public const int BlockWords = 128;               // BP_BLOCK_WORDS
    public const int AppHeaderWords = 64;
    const ushort AppMagic0 = 0xA55A, AppMagic1 = 0x4650, AppHdrVer = 1;
    const int VerWords = 32;

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

    /// <summary>CRC-16/CCITT-FALSE (dsp_image.c dspimg_crc16_words 와 동일: 워드마다 하위 → 상위 바이트)</summary>
    public static ushort Crc16Words(ReadOnlySpan<ushort> w)
    {
        ushort c = 0xFFFF;
        foreach (var x in w) { c = Crc16Byte(c, x & 0xFF); c = Crc16Byte(c, x >> 8); }
        return c;
    }

    static ushort Crc16Byte(ushort c, int b)
    {
        c ^= (ushort)(b << 8);
        for (int k = 0; k < 8; k++) c = (c & 0x8000) != 0 ? (ushort)((c << 1) ^ 0x1021) : (ushort)(c << 1);
        return c;
    }

    /// <summary>워드 배열의 CRC-32 (워드마다 하위 → 상위 바이트, 앱 헤더의 app_crc32)</summary>
    public static uint Crc32Words(ReadOnlySpan<ushort> w)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in w)
        {
            c = Tbl[(c ^ x) & 0xFF] ^ (c >> 8);
            c = Tbl[(c ^ (uint)(x >> 8)) & 0xFF] ^ (c >> 8);
        }
        return ~c;
    }

    /// <summary>
    /// 파일 헤더·길이·본문 CRC, 앱 헤더(magic·형식 버전·hdr_crc16·app_len 범위), 블록(nw 1~128, 주소가 앱 영역 안,
    /// 마지막 블록이 파일 끝과 정확히 맞음), 블록으로 복원한 코드(빈 곳 0xFFFF)의 CRC32 == app_crc 가 모두 맞으면 정보, 아니면 null.
    /// 버전 규칙(DspVersion)은 검사하지 않고 VersionValid 로 알려 준다 — 거부는 업로드 쪽에서.
    /// </summary>
    public static DspfwInfo? Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderLength + AppHeaderWords * 2 || !file[..8].SequenceEqual(Magic)) return null;
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        if (total != (uint)file.Length) return null;
        if (Crc32(file[HeaderLength..]) != crc) return null;
        var ver = Encoding.ASCII.GetString(file.Slice(16, 16)).TrimEnd('\0');

        // 앱 헤더 (dspimg_hdr_parse)
        var hdr = new ushort[AppHeaderWords];
        for (int i = 0; i < hdr.Length; i++) hdr[i] = BinaryPrimitives.ReadUInt16LittleEndian(file[(HeaderLength + i * 2)..]);
        if (hdr[0] != AppMagic0 || hdr[1] != AppMagic1 || hdr[2] != AppHdrVer) return null;
        ushort hc = Crc16Words(hdr.AsSpan(0, 62));
        if (hdr[62] != hc || hdr[63] != (ushort)~hc) return null;
        uint appLen = hdr[4] | ((uint)hdr[5] << 16);
        uint appCrc = hdr[6] | ((uint)hdr[7] << 16);
        if (appLen == 0 || appLen > AppMaxWords) return null;
        // 서버는 파일 헤더의 ver(15자), 브리지는 앱 헤더의 ver 로 비교하므로 둘이 같아야 한다 (dsp_pack.py 는 같은 값을 넣음)
        var appVer = new StringBuilder();
        for (int i = 0; i < VerWords - 1 && (hdr[10 + i] & 0xFF) != 0; i++) appVer.Append((char)(hdr[10 + i] & 0xFF));
        if ((appVer.Length > 15 ? appVer.ToString(0, 15) : appVer.ToString()) != ver) return null;

        // 블록 (dsp_pack.py unpack)
        var flat = new ushort[appLen];
        Array.Fill(flat, (ushort)0xFFFF);
        int pos = HeaderLength + AppHeaderWords * 2;
        while (pos < file.Length)
        {
            if (pos + 6 > file.Length) return null;
            uint addr = BinaryPrimitives.ReadUInt32LittleEndian(file[pos..]);
            int nw = BinaryPrimitives.ReadUInt16LittleEndian(file[(pos + 4)..]);
            pos += 6;
            if (nw == 0 || nw > BlockWords || pos + nw * 2 > file.Length) return null;
            if (addr < AppCodeAddr || (ulong)addr + (uint)nw > AppEnd) return null;
            for (int i = 0; i < nw; i++)
            {
                ulong off = addr + (ulong)i - AppCodeAddr;
                if (off < appLen) flat[off] = BinaryPrimitives.ReadUInt16LittleEndian(file[(pos + i * 2)..]);
            }
            pos += nw * 2;
        }
        if (Crc32Words(flat) != appCrc) return null;
        return new DspfwInfo(ver, crc, file.Length);
    }

    /// <summary>
    /// 테스트·도구용: 앱 코드(0x3E8040 부터의 워드, 빈 곳 0xFFFF)로 dsp_pack.py pack() 과 같은 .dspfw 를 만든다
    /// (앱 헤더 + 128워드 블록, 전부 0xFFFF 인 블록은 생략).
    /// </summary>
    public static byte[] BuildApp(ReadOnlySpan<ushort> code, string version, uint buildTime = 0)
    {
        if (code.Length == 0 || code.Length > AppMaxWords) throw new ArgumentOutOfRangeException(nameof(code));
        var hdr = new ushort[AppHeaderWords];
        Array.Fill(hdr, (ushort)0xFFFF);
        uint len = (uint)code.Length, acrc = Crc32Words(code);
        hdr[0] = AppMagic0; hdr[1] = AppMagic1; hdr[2] = AppHdrVer; hdr[3] = 0;
        hdr[4] = (ushort)len; hdr[5] = (ushort)(len >> 16);
        hdr[6] = (ushort)acrc; hdr[7] = (ushort)(acrc >> 16);
        hdr[8] = (ushort)buildTime; hdr[9] = (ushort)(buildTime >> 16);
        var vb = Encoding.ASCII.GetBytes(version);
        for (int i = 0; i < VerWords; i++) hdr[10 + i] = i < vb.Length && i < VerWords - 1 ? vb[i] : (ushort)0;
        ushort hc = Crc16Words(hdr.AsSpan(0, 62));
        hdr[62] = hc; hdr[63] = (ushort)~hc;

        var body = new List<byte>();
        foreach (var w in hdr) { body.Add((byte)w); body.Add((byte)(w >> 8)); }
        for (int off = 0; off < code.Length; off += BlockWords)
        {
            var chunk = code.Slice(off, Math.Min(BlockWords, code.Length - off));
            bool blank = true;
            foreach (var w in chunk) if (w != 0xFFFF) { blank = false; break; }
            if (blank) continue;                    // 지워진 플래시와 같으므로 생략
            var bh = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(bh, AppCodeAddr + (uint)off);
            BinaryPrimitives.WriteUInt16LittleEndian(bh.AsSpan(4), (ushort)chunk.Length);
            body.AddRange(bh);
            foreach (var w in chunk) { body.Add((byte)w); body.Add((byte)(w >> 8)); }
        }
        return Build(body.ToArray(), version);
    }

    /// <summary>테스트·도구용: 본문을 그대로 감싼 .dspfw 파일 생성 (본문 검사를 통과하려면 BuildApp 을 쓴다)</summary>
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
public sealed record DspfwInfo(string Version, uint Crc32, int Size)
{
    /// <summary>버전이 메인보드 규칙(1~8자 [0-9A-Za-z._-])에 맞는지 — 업로드는 이것이 false 면 거부해야 한다</summary>
    public bool VersionValid => DspVersion.IsValid(Version);
}
