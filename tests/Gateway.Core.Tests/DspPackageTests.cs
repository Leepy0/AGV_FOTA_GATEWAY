using System.Text;
using System.Text.Json.Nodes;
using Gateway.Core.Fota;

namespace Gateway.Core.Tests;

/// <summary>메인보드 패키지 .dsppkg (규격 v1): 정상·연결 확인 규칙·거부 사유. 패키지는 브리지 기준 .dspfw + 합성 .out/.map 으로 만든다</summary>
public class DspPackageTests
{
    static byte[] Golden() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "app_golden.dspfw"));
    const uint T = DspPackageBuilder.DefaultLinkTime;

    /// <summary>항목을 하나씩 바꿔 가며 만드는 패키지 (manifest 는 바꾼 바이트로 계산한 뒤 edit 을 적용)</summary>
    sealed class Parts
    {
        public byte[] Dspfw = Golden();
        public byte[] Out = DspPackageBuilder.CoffOut(T);
        public byte[] Map = Encoding.Latin1.GetBytes(DspPackageBuilder.Map("C:/ws/AGV_Main/Debug/AGV_Main.out", T + 9 * 3600));
        public string DspfwName = "AGV_Main_1.2.3.dspfw", OutName = "AGV_Main.out", MapName = "AGV_Main.map", LinkCheck = "time";
        public Action<JsonObject>? Edit;
        public Func<List<(string name, byte[] data)>, List<(string name, byte[] data)>>? Entries;

        public byte[] Zip()
        {
            var m = DspPackageBuilder.Manifest(Dspfw, DspfwName, Out, OutName, Map, MapName, LinkCheck);
            Edit?.Invoke(m);
            var list = new List<(string, byte[])> { ("manifest.json", DspPackageBuilder.ManifestBytes(m)), (DspfwName, Dspfw), (OutName, Out), (MapName, Map) };
            return DspPackageBuilder.Zip(Entries?.Invoke(list) ?? list);
        }
    }

    static DspPackage Ok(byte[] pkg, DspPackageLimits? lim = null)
    {
        var r = DspPackage.Read(pkg, lim);
        Assert.True(r.Ok, r.Error);
        return r.Package!;
    }

    static string Err(byte[] pkg, DspPackageLimits? lim = null)
    {
        var r = DspPackage.Read(pkg, lim);
        Assert.False(r.Ok, "거부되어야 함");
        Assert.Null(r.Package);
        return r.Error!;
    }

    // ── 정상 ──

    [Fact]
    public void Bridge_packer_sample_is_accepted_with_its_package_id()
    {
        // AGV_Wifi_Bridge dsp_pack.py bundle 출력 (ver 1.2.3, .dspfw = app_golden.dspfw 와 같은 바이트)
        var p = Ok(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "example.dsppkg")));
        Assert.Equal("2e05a29ed4626247", p.PkgId);
        Assert.Equal(("1.2.3", "time", "coff2"), (p.Version, p.LinkCheck, p.OutFormat));
        Assert.Equal(Golden(), p.Dspfw.Data);
        Assert.Equal(("AGV_Main_1.2.3.dspfw", "AGV_Main.out", "AGV_Main.map"), (p.Dspfw.Name, p.Out.Name, p.Map.Name));
        Assert.Empty(p.Warnings);
        Assert.Equal(400u, p.Image.AppLen);
        Assert.Equal(0xF55BEFB2u, p.Image.AppCrc32);
        Assert.Equal(1700000000u, p.Image.BuildTime);
    }

    [Theory]
    [InlineData(9 * 3600)]              // 한국 PC 에서 링크 (map 은 현지 시각)
    [InlineData(0)]
    [InlineData(-5 * 3600)]
    [InlineData(5 * 3600 + 1800)]       // 30분 단위 시간대
    [InlineData(5 * 3600 + 2700)]       // 45분 단위 시간대 (네팔)
    [InlineData(9 * 3600 + 5)]          // ±5초 허용
    [InlineData(-14 * 3600 - 4)]
    public void Time_rule_accepts_timezone_offsets(int tz)
    {
        var p = Ok(DspPackageBuilder.Build(Golden(), tzOffsetSec: tz));
        Assert.Equal("time", p.LinkCheck);
        Assert.Empty(p.Warnings);
        Assert.Equal(16, p.PkgId.Length);
        Assert.Equal(p.Dspfw.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(p.Dspfw.Data)).ToLowerInvariant());
    }

    [Fact]
    public void Time_rule_with_different_map_name_is_accepted_with_warning()
    {
        var p = Ok(new Parts { Map = Encoding.Latin1.GetBytes(DspPackageBuilder.Map("x/Other.out", T + 9 * 3600)) }.Zip());
        Assert.Equal("time", p.LinkCheck);
        Assert.Contains(p.Warnings, w => w.Contains("Other.out"));
    }

    [Fact]
    public void Name_rule_for_elf_and_for_map_without_linked_line()
    {
        var elf = Ok(DspPackageBuilder.Build(Golden(), elf: true));
        Assert.Equal(("name", "elf"), (elf.LinkCheck, elf.OutFormat));
        var noLinked = Ok(new Parts { Map = Encoding.Latin1.GetBytes(DspPackageBuilder.Map(@"C:\ws\Debug\agv_main.OUT", null)), LinkCheck = "name" }.Zip());
        Assert.Equal("name", noLinked.LinkCheck);   // 대소문자 무시, 경로는 basename
    }

    [Fact]
    public void Name_rule_uses_orig_name_when_present()
    {
        var p = Ok(new Parts
        {
            OutName = "AGV_Main_v2_.out", Map = Encoding.Latin1.GetBytes(DspPackageBuilder.Map("AGV Main v2!.out", null)), LinkCheck = "name",
            Edit = m => m["files"]!["out"]!["orig_name"] = "AGV Main v2!.out",
        }.Zip());
        Assert.Equal("AGV Main v2!.out", p.Out.OrigName);
        Assert.Equal("AGV_Main_v2_.out", p.Out.Name);
    }

    [Fact]
    public void Skipped_link_check_is_accepted_with_warning_even_if_times_differ()
    {
        var p = Ok(new Parts { Map = Encoding.Latin1.GetBytes(DspPackageBuilder.Map("AGV_Main.out", T + 12345)), LinkCheck = "skipped" }.Zip());
        Assert.Equal("skipped", p.LinkCheck);
        Assert.Contains(p.Warnings, w => w.Contains("건너뛰"));
    }

    [Fact]
    public void Coff1_and_unknown_coff_target_is_warning_only()
    {
        Assert.Equal("coff1", Ok(new Parts { Out = DspPackageBuilder.CoffOut(T, magic: 0x00C1) }.Zip()).OutFormat);
        var p = Ok(new Parts { Out = DspPackageBuilder.CoffOut(T, target: 0x0099) }.Zip());
        Assert.Contains(p.Warnings, w => w.Contains("0x0099"));
    }

    [Fact]
    public void Package_id_is_hash_of_raw_manifest_bytes()
    {
        var a = new Parts().Zip();
        var b = new Parts { Edit = m => m["created_utc"] = "2026-11-01T00:00:00Z" }.Zip();
        Assert.NotEqual(Ok(a).PkgId, Ok(b).PkgId);
        Assert.Equal(Ok(a).PkgId, Ok(new Parts().Zip()).PkgId);
    }

    [Theory]
    [InlineData("Sat Oct 31 14:00:10 2026", 1793422810 + 9 * 3600)]
    [InlineData("Mon Nov  2 01:02:03 2026", 1793581323)]
    [InlineData("mon   nov 2   01:02:03   2026", 1793581323)]
    public void Ctime_parses_like_strptime(string s, long want) => Assert.Equal(want, DspPackage.ParseCtime(s));

    [Theory]
    [InlineData("2026-10-31 14:00:10")]
    [InlineData("Sat Foo 31 14:00:10 2026")]
    [InlineData("Sat Feb 30 14:00:10 2026")]
    [InlineData("")]
    public void Unreadable_linked_time_is_none(string s) => Assert.Null(DspPackage.ParseCtime(s));

    [Fact]
    public void Unreadable_linked_line_falls_back_to_name_rule()
    {
        var map = DspPackageBuilder.Map("AGV_Main.out", T).Replace(DspPackageBuilder.Ctime(T), "yesterday");
        Assert.Equal("name", Ok(new Parts { Map = Encoding.Latin1.GetBytes(map), LinkCheck = "name" }.Zip()).LinkCheck);
    }

    // ── 거부 ──

    [Theory]
    [InlineData("manifest.json", "manifest.json")]
    [InlineData("AGV_Main_1.2.3.dspfw", ".dspfw")]
    [InlineData("AGV_Main.out", ".out")]
    [InlineData("AGV_Main.map", ".map")]
    public void Missing_entry(string drop, string what)
    {
        var e = Err(new Parts { Entries = l => l.Where(x => x.name != drop).ToList() }.Zip());
        Assert.Contains($"{what} 이 없습니다", e);
        Assert.Contains("dsp_pack.py bundle", e);
    }

    [Fact]
    public void Extra_or_duplicate_kind_entry()
    {
        Assert.Contains("알 수 없는 항목 'README.txt'", Err(new Parts { Entries = l => [.. l, ("README.txt", "x"u8.ToArray())] }.Zip()));
        Assert.Contains(".map 이 둘 이상", Err(new Parts { Entries = l => [.. l, ("Other.map", "x"u8.ToArray())] }.Zip()));
        Assert.Contains("같은 이름", Err(new Parts { Entries = l => [.. l, ("agv_main.MAP", "x"u8.ToArray())] }.Zip()));
    }

    [Theory]
    [InlineData("../AGV_Main.map")]
    [InlineData("sub/AGV_Main.map")]
    [InlineData(@"..\AGV_Main.map")]
    [InlineData("AGV..Main.map")]
    [InlineData(".AGV_Main.map")]
    [InlineData("AGV Main.map")]
    public void Bad_entry_name(string name)
    {
        var e = Err(new Parts { Entries = l => l.Select(x => x.name == "AGV_Main.map" ? (name, x.data) : x).ToList() }.Zip());
        Assert.Contains("규칙에 맞지 않습니다", e);
    }

    [Fact]
    public void Directory_entry_is_rejected() =>
        Assert.Contains("규칙에 맞지 않습니다", Err(new Parts { Entries = l => [.. l, ("dir/", Array.Empty<byte>())] }.Zip()));

    [Fact]
    public void Sha_or_size_mismatch()
    {
        Assert.Contains("SHA-256 이 manifest 와 다릅니다", Err(new Parts { Edit = m => m["files"]!["out"]!["sha256"] = new string('0', 64) }.Zip()));
        Assert.Contains("크기", Err(new Parts { Edit = m => m["files"]!["map"]!["size"] = 1 }.Zip()));
        // manifest 를 만든 뒤 .out 내용이 바뀜 (같은 크기)
        var p = new Parts();
        var changed = (byte[])p.Out.Clone();
        changed[100] ^= 0xFF;
        p.Entries = l => l.Select(x => x.name == "AGV_Main.out" ? (x.name, changed) : x).ToList();
        Assert.Contains("SHA-256", Err(p.Zip()));
    }

    [Fact]
    public void Manifest_version_differs_from_dspfw()
    {
        var e = Err(new Parts { Edit = m => m["ver"] = "1.2.4" }.Zip());
        Assert.Contains("1.2.4", e);
        Assert.Contains("1.2.3", e);
    }

    [Fact]
    public void Dspfw_version_breaking_8_char_rule()
    {
        var img = DspfwImage.BuildApp(new ushort[] { 1, 2, 3 }, "1.2.3-rc1");
        Assert.Contains("8자 이하", Err(new Parts { Dspfw = img, DspfwName = "AGV_Main_1.2.3-rc1.dspfw" }.Zip()));
    }

    [Fact]
    public void Broken_dspfw()
    {
        var f = Golden();
        f[100] ^= 1;
        Assert.Contains(".dspfw 형식·CRC·블록 구조", Err(new Parts { Dspfw = f }.Zip()));
    }

    [Fact]
    public void App_header_values_must_match_manifest()
    {
        Assert.Contains("app_crc32", Err(new Parts { Edit = m => m["dspfw"]!["app_crc32"] = "00000000" }.Zip()));
        Assert.Contains("app_len", Err(new Parts { Edit = m => m["dspfw"]!["app_len"] = 401 }.Zip()));
        Assert.Contains("build_time", Err(new Parts { Edit = m => m["dspfw"]!["build_time"] = 1 }.Zip()));
        Ok(new Parts { Edit = m => m["dspfw"]!["app_crc32"] = "F55BEFB2" }.Zip());   // 읽을 때 대소문자 무시
    }

    [Fact]
    public void Wrong_format_or_link_check_value()
    {
        Assert.Contains("format_ver 2", Err(new Parts { Edit = m => m["format_ver"] = 2 }.Zip()));
        Assert.Contains("패키지 형식", Err(new Parts { Edit = m => m["format"] = "other" }.Zip()));
        Assert.Contains("time·name·skipped", Err(new Parts { Edit = m => m["link_check"] = "maybe" }.Zip()));
        Assert.Contains("JSON", Err(new Parts { Entries = l => l.Select(x => x.name == "manifest.json" ? (x.name, "{nope"u8.ToArray()) : x).ToList() }.Zip()));
    }

    [Fact]
    public void Unknown_manifest_keys_are_ignored() =>
        Ok(new Parts { Edit = m => { m["future"] = new JsonObject { ["x"] = 1 }; m["files"]!["out"]!["dwarf"] = true; } }.Zip());

    [Fact]
    public void Elf_for_other_machine_is_rejected() =>
        Assert.Contains("C2000 .out 이 아닙니다", Err(new Parts { Out = DspPackageBuilder.ElfOut(machine: 40), LinkCheck = "name" }.Zip()));

    [Fact]
    public void Elf64_is_rejected()
    {
        var b = DspPackageBuilder.ElfOut();
        b[4] = 2;
        Assert.Contains("C2000 .out 이 아닙니다", Err(new Parts { Out = b, LinkCheck = "name" }.Zip()));
    }

    [Fact]
    public void Non_ti_out_is_rejected()
    {
        Assert.Contains("TI .out(COFF/ELF) 이 아닙니다", Err(new Parts { Out = Enumerable.Repeat((byte)0x55, 64).ToArray() }.Zip()));
        Assert.Contains("22바이트", Err(new Parts { Out = DspPackageBuilder.CoffOut(T)[..21] }.Zip()));
    }

    [Fact]
    public void Map_without_output_file_name_or_with_nul_is_rejected()
    {
        Assert.Contains("CCS 링커 맵이 아닙니다", Err(new Parts { Map = "just some text\n"u8.ToArray() }.Zip()));
        var nul = new Parts().Map.Concat(new byte[] { 0 }).ToArray();
        Assert.Contains("NUL", Err(new Parts { Map = nul }.Zip()));
    }

    [Theory]
    [InlineData(9 * 3600 + 6)]
    [InlineData(100)]
    [InlineData(15 * 3600)]      // ±14시간 밖
    public void Link_time_mismatch_is_rejected(int tz) =>
        Assert.Contains("링크 시각이 다릅니다", Err(DspPackageBuilder.Build(Golden(), tzOffsetSec: tz)));

    [Fact]
    public void Name_mismatch_is_rejected_when_time_is_unavailable()
    {
        var e = Err(new Parts { Out = DspPackageBuilder.ElfOut(), Map = Encoding.Latin1.GetBytes(DspPackageBuilder.Map("Other.out", T)), LinkCheck = "name" }.Zip());
        Assert.Contains("Other.out", e);
        Assert.Contains("AGV_Main.out", e);
    }

    [Fact]
    public void Manifest_link_check_must_agree_with_server()
    {
        Assert.Contains("link_check(name)와 서버 판정(time)", Err(new Parts { LinkCheck = "name" }.Zip()));
        Assert.Contains("link_check(time)와 서버 판정(name)", Err(DspPackageBuilder.Build(Golden(), elf: true, linkCheck: "time")));
    }

    [Fact]
    public void Oversize_entries_are_stopped_while_reading()
    {
        var pkg = new Parts().Zip();
        Assert.Contains(".out 이 한도 100 B", Err(pkg, new DspPackageLimits { Out = 100 }));
        Assert.Contains(".map 이 한도", Err(pkg, new DspPackageLimits { Map = 64 }));
        Assert.Contains("manifest.json 이 한도", Err(pkg, new DspPackageLimits { Manifest = 128 }));
        Assert.Contains(".dspfw 이 한도", Err(pkg, new DspPackageLimits { Dspfw = 721 }));
        Ok(pkg, new DspPackageLimits { Dspfw = 722 });
        // 기본 한도: .dspfw 는 브리지 파티션 0x60000 − 64 B
        Assert.Equal(393_152, DspPackageLimits.Default.Dspfw);
    }

    [Fact]
    public void Zip_bomb_like_out_is_cut_at_the_limit()
    {
        // 압축이 잘 되는 64 MiB+1 .out — 다 풀지 않고 한도에서 멈춘다
        var big = new byte[64 * 1024 * 1024 + 1];
        big[0] = 0xC2;
        var e = Err(new Parts { Out = big }.Zip());
        Assert.Contains(".out 이 한도 64 MiB", e);
    }

    [Fact]
    public void Corrupted_compressed_data_is_rejected_not_thrown()
    {
        // .out 의 압축 데이터 가운데를 깨뜨림 (로컬 헤더·중앙 디렉터리는 그대로)
        var pkg = new Parts { Out = Enumerable.Range(0, 4000).Select(i => (byte)(i * 7 % 251)).Prepend((byte)0).Prepend((byte)0xC2).ToArray() }.Zip();
        var at = IndexOf(pkg, "AGV_Main.out"u8.ToArray()) + "AGV_Main.out".Length + 40;
        pkg[at] ^= 0xFF; pkg[at + 1] ^= 0xFF; pkg[at + 2] ^= 0xFF;
        var r = DspPackage.Read(pkg);
        Assert.False(r.Ok);
        Assert.Contains("dsp_pack.py bundle", r.Error);
    }

    static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [Fact]
    public void Not_a_zip()
    {
        Assert.Contains("ZIP 이 아니", Err(Golden()));
        Assert.Contains("ZIP 이 아니", Err([]));
    }
}
