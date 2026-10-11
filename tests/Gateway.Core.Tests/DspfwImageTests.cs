using System.Buffers.Binary;
using Gateway.Core.Fota;

namespace Gateway.Core.Tests;

public class DspfwImageTests
{
    [Fact]
    public void Crc32_vector() => Assert.Equal(0xCBF43926u, DspfwImage.Crc32("123456789"u8));

    [Fact]
    public void Crc16_vector() =>       // CRC-16/CCITT-FALSE("12345678") = A12B, 워드마다 하위 바이트 먼저 ("12" → 0x3231)
        Assert.Equal(0xA12B, DspfwImage.Crc16Words(new ushort[] { 0x3231, 0x3433, 0x3635, 0x3837 }));

    // AGV_Wifi_Bridge tools/dsp_pack.py pack (--ver 1.2.3 --build 1760000000) 의 실제 출력: 5블록, 빈 블록 생략, app_len 963
    static readonly byte[] Golden = Convert.FromBase64String(
        "QUdWRFNQMQBEBQAAOymowTEuMi4zAAAAAAAAAAAAAABapVBGAQAAAMMDAABHRGzhAHjnaDEALgAyAC4AMwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/////////////////////////////////////////////////////sHJPjUCAPgCAAEAAAID/////////////" +
        "////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////" +
        "////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////" +
        "///////////////////////////////////////////////////////////////////////////////////AgD4AgAD/////////////////////////////" +
        "////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////" +
        "/////////////////////2gkvzcWS21exHEbhXKYyasgv3fSzuUl+XwM0x8qM4FG2FkvbYaA3ZM0p4u64s054ZD05wc+G5Uu7EFDVZpo8XtIj5+i9rVNyaTc" +
        "++9SA6kWACpXPa5QBWRcd7OKCp5hsbjED9hm673+FBJrJcI4GUxwX8dyHoZ1mcysI8B609HmQIE+AIAAKPp/DdYgLTSER9taMm6JgeCUN6iOu+XOPOKT9eoI" +
        "QRyYL+9CRladafR8S5Cio/m2UMqn3f7wVQSsFwMrWj6xUQhlX3i2iw2fZLK7xRLZaezA/xcTbibFORxNc2DKcyGHeJrPrSbBfdTU5yv7gg7ZITA1h0jeWzVv" +
        "jILjlTqpkbzozz/jlvbtCUQdmzDyQ0lXoGr3fU6RpaT8t1PLqt4B8lgFrxgGLF0/tFILZmJ5uYwQoGezvsYV2mztwwAaFHEnyDofTnZhzXQkiHub0q4pwoDV" +
        "1+gu/IUP3CIzNopJ4Vw4cI+D5pY9qpS969BC5Jn38ApHHp4x9URMWKNr+n5RksCBPgCAAKil/7hWzK3fBPNbBrIZCS1gQLdTDmdleryNE6FqtMHHGNtv7sYB" +
        "HRV0KMs7Ik95YtB1J4l+nNWvLMOD1trpMf2IEN8jNjeNSuRdO3GShOmXQKuXvu7RReWc+PMLSh+hMvhFT1mmbP1/VJOrpgK6Wc2w4Af0Xge1GgwuY0G6VBFo" +
        "aHu/jhaibbXEyBvccu/JAiAWdynOPCVQfGPTdiqKgZ3YsC/Ehtfd6jT+ixHiJDk4kEvnXj5ylYXsmEOsmr/x0kjmn/n2DE0gpDP7RlJaqW0AgVeUrqcFu///" +
        "///////////////////////////////////////////////////Agz4AQwD/////////////////////////////////////////////////////////////" +
        "/////////////////////////////////////////////////////////////////////////////////////////////////////////////zQS///Nqw==");

    static ushort[] GoldenCode()
    {
        var code = Enumerable.Repeat((ushort)0xFFFF, 0x3E8403 - 0x3E8040).ToArray();
        code[0] = 0x0040; code[1] = 0x8000;
        for (int i = 0; i < 300; i++) code[0x100 - 0x40 + i] = (ushort)((i * 0x1357 + 0x2468) & 0xFFFF);
        code[0x400 - 0x40] = 0x1234; code[0x402 - 0x40] = 0xABCD;
        return code;
    }

    [Fact]
    public void Parses_packer_output_and_BuildApp_matches_it_byte_for_byte()
    {
        var info = DspfwImage.Parse(Golden)!;
        Assert.Equal(("1.2.3", Golden.Length, true), (info.Version, info.Size, info.VersionValid));
        Assert.Equal(DspfwImage.Crc32(Golden.AsSpan(DspfwImage.HeaderLength)), info.Crc32);
        Assert.Equal(Golden, DspfwImage.BuildApp(GoldenCode(), "1.2.3", 1760000000));
    }

    [Fact]
    public void Tampered_or_foreign_files_are_rejected()
    {
        var f = (byte[])Golden.Clone();
        f[100] ^= 1;
        Assert.Null(DspfwImage.Parse(f));                                   // 파일 CRC
        Assert.Null(DspfwImage.Parse("not a dspfw"u8));
        Assert.Null(DspfwImage.Parse(DspfwImage.Build(new byte[500], "1.2.3")));   // 앱 헤더 없음
    }

    // 본문(앱 헤더 + 블록)을 바꾼 뒤 파일 헤더 CRC 를 다시 맞춘다 — 본문 검사만으로 거부되는지 본다
    static byte[] Rewrap(byte[] file, Action<byte[]> edit, int extra = 0)
    {
        var body = new byte[file.Length - DspfwImage.HeaderLength + extra];
        file.AsSpan(DspfwImage.HeaderLength, Math.Min(body.Length, file.Length - DspfwImage.HeaderLength)).CopyTo(body);
        edit(body);
        return DspfwImage.Build(body, "1.2.3");
    }

    static void FixHdrCrc(byte[] body)
    {
        var hdr = new ushort[64];
        for (int i = 0; i < 64; i++) hdr[i] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i * 2));
        ushort c = DspfwImage.Crc16Words(hdr.AsSpan(0, 62));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(124), c);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(126), (ushort)~c);
    }

    const int Blk0 = 128;       // 본문에서 첫 블록 위치 (앱 헤더 64워드 뒤)

    [Fact]
    public void Rewrap_without_change_still_parses() => Assert.NotNull(DspfwImage.Parse(Rewrap(Golden, _ => { })));

    [Fact]
    public void Block_address_outside_app_area_is_rejected()
    {
        // 첫 블록(codestart 0x3E8040)을 앱 헤더 자리(0x3E8000)로
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(Blk0), 0x3E8000))));
        // 부트로더 섹터 A 쪽으로 넘침
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(Blk0), 0x3F4000 - 64))));
    }

    [Fact]
    public void Block_word_count_out_of_range_or_not_matching_file_end_is_rejected()
    {
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(Blk0 + 4), 0))));
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(Blk0 + 4), 129))));
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, _ => { }, extra: 3)));          // 끝에 블록 머리도 안 되는 찌꺼기
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, _ => { }, extra: -2)));         // 마지막 블록이 잘림
    }

    [Fact]
    public void Block_data_not_matching_app_crc_is_rejected()
    {
        // 블록 데이터 한 워드를 바꿈 (파일 CRC 는 다시 맞춤) → 블록으로 복원한 CRC 가 app_crc 와 다름
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => b[Blk0 + 6 + 10] ^= 0x55)));
        // app_len 을 한 워드 줄임 (헤더 CRC 도 다시 맞춤) → 복원 범위가 달라져 CRC 불일치
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => { b[8] -= 1; FixHdrCrc(b); })));
    }

    [Fact]
    public void App_header_errors_are_rejected()
    {
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => b[0] ^= 1)));                     // magic
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => b[124] ^= 1)));                   // hdr_crc16
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => { b[8] = 0; b[9] = 0; b[10] = 0; b[11] = 0; FixHdrCrc(b); })));     // app_len 0
        Assert.Null(DspfwImage.Parse(Rewrap(Golden, b => { b[10] = 1; FixHdrCrc(b); })));  // app_len > 앱 영역
    }

    [Fact]
    public void File_and_app_header_versions_must_agree()
    {
        var f = DspfwImage.BuildApp(GoldenCode(), "1.2.3");
        f[16 + 4] = (byte)'4';                                   // 파일 헤더 ver 만 1.2.4 로
        var body = f.AsSpan(DspfwImage.HeaderLength).ToArray();
        var g = DspfwImage.Build(body, "1.2.4");
        Assert.Null(DspfwImage.Parse(g));
    }

    [Fact]
    public void Blank_blocks_are_skipped_and_counted_as_ffff()
    {
        var code = Enumerable.Repeat((ushort)0xFFFF, 1000).ToArray();
        code[0] = 1; code[999] = 2;                              // 가운데 블록들은 전부 0xFFFF → 생략
        var f = DspfwImage.BuildApp(code, "2.0.0");
        Assert.Equal(DspfwImage.HeaderLength + 128 + 2 * (6 + 256) - (128 - 1000 % 128) * 2, f.Length);
        Assert.NotNull(DspfwImage.Parse(f));
    }

    [Fact]
    public void Invalid_version_is_parsed_but_flagged()
    {
        var info = DspfwImage.Parse(DspfwImage.BuildApp(GoldenCode(), "2026.10.11"))!;
        Assert.Equal("2026.10.11", info.Version);
        Assert.False(info.VersionValid);
        Assert.True(DspfwImage.Parse(DspfwImage.BuildApp(GoldenCode(), "1.2.3-rc"))!.VersionValid);
    }
}
