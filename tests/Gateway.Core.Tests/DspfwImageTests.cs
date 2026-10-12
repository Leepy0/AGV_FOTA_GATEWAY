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

    // AGV_Wifi_Bridge tests/host/golden/app_golden.dspfw 그대로 (dsp_pack.py pack app_golden.hex --ver 1.2.3 --build 1700000000).
    // 브리지 test_bootproto.c test_golden 과 dsp_pack.py info 의 기대값: app_len 400워드, app_crc32 F55BEFB2, 블록 3개, 722바이트
    static byte[] BridgeGolden() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "app_golden.dspfw"));

    [Fact]
    public void Bridge_golden_dspfw_parses_with_bridge_expected_values()
    {
        var f = BridgeGolden();
        Assert.Equal(722, f.Length);
        var info = DspfwImage.Parse(f)!;
        Assert.NotNull(info);
        Assert.Equal(("1.2.3", 722, true), (info.Version, info.Size, info.VersionValid));
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(12)), info.Crc32);          // 파일 헤더 CRC = DSP_OTA_START crc32
        Assert.Equal(DspfwImage.Crc32(f.AsSpan(DspfwImage.HeaderLength)), info.Crc32);

        // Parse 가 노출하지 않는 앱 헤더·블록 값은 같은 규칙으로 직접 읽어 브리지 기대값과 대조
        ushort W(int byteOff) => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(byteOff));
        const int H = DspfwImage.HeaderLength;
        Assert.Equal(400u, W(H + 8) | ((uint)W(H + 10) << 16));                    // app_len
        Assert.Equal(0xF55BEFB2u, W(H + 12) | ((uint)W(H + 14) << 16));            // app_crc32
        Assert.Equal(1700000000u, W(H + 16) | ((uint)W(H + 18) << 16));            // build_time
        int pos = H + DspfwImage.AppHeaderWords * 2, blocks = 0, words = 0;
        var flat = Enumerable.Repeat((ushort)0xFFFF, 400).ToArray();
        while (pos < f.Length)
        {
            uint addr = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(pos));
            int nw = W(pos + 4);
            for (int i = 0; i < nw; i++) if (addr + i - DspfwImage.AppCodeAddr < 400) flat[addr + i - DspfwImage.AppCodeAddr] = W(pos + 6 + i * 2);
            pos += 6 + nw * 2; blocks++; words += nw;
        }
        Assert.Equal((f.Length, 3, 272), (pos, blocks, words));                    // "blocks: 3 (272 words transferred)"
        Assert.Equal(0xF55BEFB2u, DspfwImage.Crc32Words(flat));                    // 서버 CRC32 워드 계산이 브리지와 같음
    }

    [Fact]
    public void Bridge_golden_dspfw_rejects_any_single_byte_change()
    {
        var f = BridgeGolden();
        foreach (var at in new[] { 0, 9, 13, 20, DspfwImage.HeaderLength + 1, DspfwImage.HeaderLength + 130, 300, f.Length - 1 })
        {
            var t = (byte[])f.Clone();
            t[at] ^= 0x01;
            Assert.Null(DspfwImage.Parse(t));
        }
        // 파일 CRC 를 다시 맞춰도 본문(앱 헤더 app_crc·블록 데이터) 검사로 거부
        Assert.Null(DspfwImage.Parse(Rewrap(f, b => b[DspfwImage.AppHeaderWords * 2 + 6 + 10] ^= 1)));
        Assert.NotNull(DspfwImage.Parse(Rewrap(f, _ => { })));
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
    // ── R3: 브리지 dap_image_check_blocks 와 같은 블록 순서 규칙 (주소 오름차순·겹침 없음) ──

    // 본문을 앱 헤더와 블록(머리 6B + 데이터) 조각들로 나눈다
    static (byte[] Hdr, List<byte[]> Blocks) Split(byte[] file)
    {
        var body = file.AsSpan(DspfwImage.HeaderLength).ToArray();
        var blocks = new List<byte[]>();
        for (int pos = Blk0; pos < body.Length;)
        {
            int len = 6 + BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos + 4)) * 2;
            blocks.Add(body.AsSpan(pos, len).ToArray());
            pos += len;
        }
        return (body[..Blk0], blocks);
    }

    static byte[] Join(byte[] hdr, IEnumerable<byte[]> blocks) => DspfwImage.Build(hdr.Concat(blocks.SelectMany(b => b)).ToArray(), "1.2.3");

    [Fact]
    public void Blocks_out_of_address_order_are_rejected_even_with_matching_crcs()
    {
        var (hdr, blocks) = Split(DspfwImage.BuildApp(GoldenCode(), "1.2.3"));
        Assert.True(blocks.Count >= 3);
        Assert.NotNull(DspfwImage.Parse(Join(hdr, blocks)));                     // 나눠 다시 붙여도 그대로 통과
        var swapped = new List<byte[]>(blocks);
        (swapped[0], swapped[1]) = (swapped[1], swapped[0]);                     // 파일 CRC 는 Build 가, app_crc 는 복원 결과가 같아 맞음
        Assert.Null(DspfwImage.Parse(Join(hdr, swapped)));
    }

    [Fact]
    public void Overlapping_blocks_are_rejected_even_with_matching_crcs()
    {
        var code = Enumerable.Range(0, 256).Select(i => (ushort)(i * 7)).ToArray();
        var (hdr, blocks) = Split(DspfwImage.BuildApp(code, "1.2.3"));
        Assert.Equal(2, blocks.Count);
        // 첫 블록 끝 워드를 같은 값으로 다시 쓰는 1워드 블록을 뒤에 덧붙임 (복원 결과·app_crc 는 같음)
        var dup = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(dup, DspfwImage.AppCodeAddr + 127);
        BinaryPrimitives.WriteUInt16LittleEndian(dup.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dup.AsSpan(6), code[127]);
        Assert.Null(DspfwImage.Parse(Join(hdr, new[] { blocks[0], dup, blocks[1] })));    // 직전 블록과 겹침
        Assert.Null(DspfwImage.Parse(Join(hdr, new[] { blocks[0], blocks[1], dup })));    // 뒤로 되돌아감
        // 겹치지 않고 바로 이어지는 블록은 통과 (블록 경계 = 직전 끝)
        var tail = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, DspfwImage.AppCodeAddr + 256);
        BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(6), 0xFFFF);             // app_len 밖이라 CRC 에 영향 없음
        Assert.NotNull(DspfwImage.Parse(Join(hdr, new[] { blocks[0], blocks[1], tail })));
    }
}
