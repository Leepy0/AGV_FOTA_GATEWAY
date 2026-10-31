using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gateway.Core.Fota;

/// <summary>
/// 메인보드 펌웨어 패키지 (.dsppkg, 규격 v1) — AGV_Wifi_Bridge/tools/dsp_pack.py bundle 이 만든다.
/// ZIP 하나에 manifest.json + .dspfw + .out + .map 정확히 4개. 원본 소스를 잃어도 .out(DWARF)·.map 이 남게 하는 재발 방지책.
/// Read 는 규격 1~4절을 모두 검사한다: 항목 이름·개수, 압축 해제 크기 한도(읽으면서), sha256·크기, manifest 형식,
/// .dspfw(DspfwImage.Parse·버전 규칙·app_len/app_crc32/build_time), .out 형식(COFF1/COFF2/ELF C2000), .map 형식, .map↔.out 연결 확인.
/// 브리지는 지금처럼 .dspfw 만 받는다 (이 형식은 게이트웨이·USB 스테이징 보관용).
/// </summary>
public sealed partial class DspPackage
{
    public const string Format = "agv-dsppkg";
    public const int FormatVersion = 1;
    /// <summary>업로드 한도 (압축된 패키지 파일). 압축 해제 한도 합(약 80.4 MiB)보다 넉넉하게</summary>
    public const long MaxPackageBytes = 96L * 1024 * 1024;
    public const ushort CoffTargetC2000 = 0x009D;
    public const ushort ElfMachineC2000 = 141;
    /// <summary>패키지를 다시 만들라는 안내 (구조 오류 메시지 끝)</summary>
    public const string Remake = " — dsp_pack.py bundle 로 다시 만드세요";

    /// <summary>패키지 ID = sha256(manifest.json 의 ZIP 안 바이트) 소문자 hex 앞 16자</summary>
    public string PkgId { get; private init; } = "";
    public string Version { get; private init; } = "";
    /// <summary>"time" | "name" | "skipped" (manifest 값, skipped 가 아니면 서버가 다시 판정해 같음을 확인함)</summary>
    public string LinkCheck { get; private init; } = "";
    public IReadOnlyList<string> Warnings { get; private init; } = [];
    public DspfwInfo Image { get; private init; } = null!;
    /// <summary>"coff1" | "coff2" | "elf"</summary>
    public string OutFormat { get; private init; } = "";
    public DspPackageFile Dspfw { get; private init; } = null!;
    public DspPackageFile Out { get; private init; } = null!;
    public DspPackageFile Map { get; private init; } = null!;
    public string CreatedUtc { get; private init; } = "";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,59}$")]
    private static partial Regex EntryName();
    [GeneratedRegex(@"OUTPUT FILE NAME:\s*<([^>]+)>")]
    private static partial Regex MapOutput();
    [GeneratedRegex(@"^>> Linked (.+)$", RegexOptions.Multiline)]
    private static partial Regex MapLinked();

    /// <summary>항목 이름 규칙 (경로 조작 방지): 영문·숫자로 시작, 영문·숫자·._- 60자 이하, '..' 없음</summary>
    public static bool IsValidEntryName(string name) => EntryName().IsMatch(name) && !name.Contains("..");

    public static DspPackageResult Read(byte[] package, DspPackageLimits? limits = null)
    {
        using var ms = new MemoryStream(package, writable: false);
        return Read(ms, limits);
    }

    /// <summary>패키지를 읽어 검사한다. 실패는 Error(한국어 사유 + 조치), 성공은 Package (경고는 Package.Warnings)</summary>
    public static DspPackageResult Read(Stream zip, DspPackageLimits? limits = null)
    {
        limits ??= DspPackageLimits.Default;
        ZipArchive za;
        try { za = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true); }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or ArgumentException) { return Fail("ZIP 이 아니거나 손상된 파일입니다" + Remake); }
        using (za)
        {
            try { return Check(za, limits); }
            // 지원하지 않는 압축 방식·CRC 오류·잘린 항목 (stored·deflate 만 씀)
            catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException) { return Fail($"패키지를 풀지 못했습니다 ({e.Message}) — 손상됐거나 지원하지 않는 압축입니다. dsp_pack.py bundle 로 다시 만드세요"); }
        }
    }

    static DspPackageResult Fail(string why) => new(null, why);

    static DspPackageResult Check(ZipArchive za, DspPackageLimits lim)
    {
        // ── 1절: 항목 이름·개수 (내용을 읽기 전에) ──
        var found = new Dictionary<string, ZipArchiveEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in za.Entries)
        {
            var n = e.FullName;
            var shown = n.Length > 64 ? n[..64] + "…" : n;
            if (!IsValidEntryName(n)) return Fail($"패키지 항목 이름 '{shown}' 이 규칙에 맞지 않습니다 (하위 폴더·경로·'..' 불가, 영문·숫자·._- 60자 이하)" + Remake);
            if (!seen.Add(n)) return Fail($"패키지에 같은 이름의 항목이 둘 있습니다 ('{shown}')" + Remake);
            if (e.IsEncrypted) return Fail($"암호가 걸린 항목 '{shown}' 은 받지 않습니다" + Remake);
            var what = n.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ? "manifest.json"
                     : n.EndsWith(".dspfw", StringComparison.OrdinalIgnoreCase) ? ".dspfw"
                     : n.EndsWith(".out", StringComparison.OrdinalIgnoreCase) ? ".out"
                     : n.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ? ".map" : null;
            if (what is null) return Fail($"패키지에 알 수 없는 항목 '{shown}' 이 있습니다 — manifest.json·.dspfw·.out·.map 4개만 들어갑니다. dsp_pack.py bundle 로 다시 만드세요");
            if (!found.TryAdd(what, e)) return Fail($"패키지에 {what} 이 둘 이상입니다" + Remake);
        }
        foreach (var what in new[] { "manifest.json", ".dspfw", ".out", ".map" })
            if (!found.ContainsKey(what)) return Fail($"패키지에 {what} 이 없습니다" + Remake);
        ZipArchiveEntry manifestE = found["manifest.json"], dspfwE = found[".dspfw"], outE = found[".out"], mapE = found[".map"];

        // ── 2절: manifest ──
        var manBytes = ReadLimited(manifestE, lim.Manifest, "manifest.json", out var err);
        if (manBytes is null) return Fail(err!);
        JsonElement man;
        try
        {
            var span = manBytes.AsSpan();
            if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) span = span[3..];
            using var doc = JsonDocument.Parse(span.ToArray());
            man = doc.RootElement.Clone();
        }
        catch (JsonException) { return Fail("manifest.json 이 JSON 이 아닙니다" + Remake); }
        if (man.ValueKind != JsonValueKind.Object) return Fail("manifest.json 이 JSON 객체가 아닙니다" + Remake);
        var fmt = Str(man, "format");
        var fver = Long(man, "format_ver");
        if (fmt != Format || fver != FormatVersion)
            return Fail($"패키지 형식이 다릅니다 (format '{fmt ?? "?"}', format_ver {fver?.ToString() ?? "?"}) — 이 서버는 {Format} {FormatVersion} 만 받습니다");
        var ver = Str(man, "ver");
        if (string.IsNullOrEmpty(ver)) return Fail("manifest 에 ver 가 없습니다" + Remake);
        var linkCheck = Str(man, "link_check");
        if (linkCheck is not ("time" or "name" or "skipped")) return Fail($"manifest 의 link_check '{linkCheck ?? "?"}' 가 time·name·skipped 가 아닙니다" + Remake);
        if (!Obj(man, "files", out var files)) return Fail("manifest 에 files 가 없습니다" + Remake);
        if (!Obj(files, "dspfw", out var mDspfw) || !Obj(files, "out", out var mOut) || !Obj(files, "map", out var mMap))
            return Fail("manifest 의 files 에 dspfw·out·map 이 모두 있어야 합니다" + Remake);
        if (!Obj(man, "dspfw", out var mImg)) return Fail("manifest 에 dspfw(app_len·app_crc32·build_time) 가 없습니다" + Remake);

        // ── 내용 (한도는 읽으면서) · sha256·크기 ──
        var dspfw = ReadLimited(dspfwE, lim.Dspfw, ".dspfw", out err); if (dspfw is null) return Fail(err!);
        var outB = ReadLimited(outE, lim.Out, ".out", out err); if (outB is null) return Fail(err!);
        var mapB = ReadLimited(mapE, lim.Map, ".map", out err); if (mapB is null) return Fail(err!);
        var fDspfw = FileOf(dspfwE, dspfw, mDspfw, "dspfw", out err); if (fDspfw is null) return Fail(err!);
        var fOut = FileOf(outE, outB, mOut, "out", out err); if (fOut is null) return Fail(err!);
        var fMap = FileOf(mapE, mapB, mMap, "map", out err); if (fMap is null) return Fail(err!);

        // ── .dspfw: 형식·버전 규칙·manifest 와 일치 ──
        var img = DspfwImage.Parse(dspfw);
        if (img is null) return Fail($"패키지의 {dspfwE.FullName}: .dspfw 형식·CRC·블록 구조가 틀립니다 (파일 헤더 버전 = 앱 헤더 버전 포함)" + Remake);
        if (!DspVersion.IsValid(img.Version))
            return Fail($"메인보드 버전은 {DspVersion.MaxLength}자 이하 영문·숫자·._- 만 됩니다 (파일 헤더 버전 '{img.Version}') — dsp_pack.py bundle --ver 를 고쳐 다시 묶으세요");
        if (ver != img.Version) return Fail($"manifest 의 ver({ver})와 .dspfw 헤더 버전({img.Version})이 다릅니다" + Remake);
        if (Long(mImg, "app_len") != img.AppLen) return Fail($"manifest 의 dspfw.app_len({Long(mImg, "app_len")?.ToString() ?? "?"})이 .dspfw 앱 헤더({img.AppLen})와 다릅니다" + Remake);
        var crcText = Str(mImg, "app_crc32");
        if (crcText is null || !crcText.Equals(img.AppCrc32.ToString("x8"), StringComparison.OrdinalIgnoreCase))
            return Fail($"manifest 의 dspfw.app_crc32({crcText ?? "?"})가 .dspfw 앱 헤더({img.AppCrc32:x8})와 다릅니다" + Remake);
        if (Long(mImg, "build_time") != img.BuildTime) return Fail($"manifest 의 dspfw.build_time 이 .dspfw 앱 헤더({img.BuildTime})와 다릅니다" + Remake);

        var warn = new List<string>();
        // ── 3절: .out ──
        var o = CheckOut(outB, out err);
        if (o is null) return Fail(err!);
        if (Str(mOut, "format") is { } mf && mf != o.Value.format) return Fail($"manifest 의 files.out.format({mf})과 실제 .out 형식({o.Value.format})이 다릅니다" + Remake);
        if (o.Value.format != "elf" && o.Value.target != CoffTargetC2000)
            warn.Add($".out 의 COFF 대상 ID 가 0x{o.Value.target:X4}({o.Value.target}) 입니다 — C2000 은 0x009D(157) 로 알고 있음. 맞는 빌드인지 확인하세요");
        // ── 3절: .map ──
        if (Array.IndexOf(mapB, (byte)0) >= 0) return Fail(".map 에 NUL 바이트가 있습니다 — CCS 링커 맵(텍스트)이 아닙니다");
        var mapText = Encoding.Latin1.GetString(mapB);
        var mo = MapOutput().Match(mapText);
        if (!mo.Success) return Fail(".map 이 CCS 링커 맵이 아닙니다 (OUTPUT FILE NAME 없음)");
        var mapOut = BaseName(mo.Groups[1].Value.Trim());
        long? linked = null;
        if (MapLinked().Match(mapText) is { Success: true } ml) linked = ParseCtime(ml.Groups[1].Value);

        // ── 4절: .map ↔ .out 연결 확인 ──
        if (linkCheck == "skipped")
            warn.Add("패커가 .map↔.out 연결 확인을 건너뛰었습니다 (--no-link-check) — 같은 빌드의 .map 인지 확인하세요");
        else
        {
            var outName = fOut.OrigName;
            string got;
            long ts = o.Value.timestamp;
            if (ts > 0 && linked is { } lt)
            {
                if (!LinkTimeMatches(ts - lt)) return Fail(".map 과 .out 의 링크 시각이 다릅니다 — 같은 빌드의 .map 이 아닙니다");
                got = "time";
                if (!mapOut.Equals(outName, StringComparison.OrdinalIgnoreCase))
                    warn.Add($".map 의 출력 파일 이름({mapOut})과 .out 이름({outName})이 다르지만 링크 시각이 같아 받았습니다");
            }
            else
            {
                if (!mapOut.Equals(outName, StringComparison.OrdinalIgnoreCase)) return Fail($".map 의 출력 파일 이름({mapOut})과 .out 이름({outName})이 다릅니다");
                got = "name";
            }
            if (got != linkCheck) return Fail($"manifest 의 link_check({linkCheck})와 서버 판정({got})이 다릅니다" + Remake);
        }

        return new(new DspPackage
        {
            PkgId = Convert.ToHexString(SHA256.HashData(manBytes)).ToLowerInvariant()[..16],
            Version = ver, LinkCheck = linkCheck, Warnings = warn, Image = img, OutFormat = o.Value.format,
            Dspfw = fDspfw, Out = fOut, Map = fMap, CreatedUtc = Str(man, "created_utc") ?? "",
        }, null);
    }

    /// <summary>링크 PC 의 시간대 오프셋만큼 차이 남: 어떤 정수 k (|k| ≤ 56, ±14시간)에 대해 |d − 900·k| ≤ 5</summary>
    public static bool LinkTimeMatches(long d)
    {
        for (int k = -56; k <= 56; k++)
            if (Math.Abs(d - 900L * k) <= 5) return true;
        return false;
    }

    static readonly string[] Wdays = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>C ctime 형식 "Www Mmm d HH:MM:SS YYYY"(공백 정리)을 시간대 없이 UTC 로 본 unix 초. 못 읽으면 null (패커 strptime 과 같이 요일은 이름만 확인)</summary>
    public static long? ParseCtime(string s)
    {
        var p = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 5 || Array.IndexOf(Wdays, p[0].ToLowerInvariant()) < 0) return null;
        int mon = Array.IndexOf(Months, p[1].ToLowerInvariant()) + 1;
        var t = p[3].Split(':');
        if (mon == 0 || t.Length != 3
            || !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(p[4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(t[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
            || !int.TryParse(t[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mm)
            || !int.TryParse(t[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ss)) return null;
        if (year is < 1970 or > 9999 || day < 1 || day > DateTime.DaysInMonth(year, mon) || hh > 23 || mm > 59 || ss > 61) return null;
        return new DateTimeOffset(year, mon, day, hh, mm, 0, TimeSpan.Zero).ToUnixTimeSeconds() + ss;   // 윤초 60·61 은 다음 분으로 (timegm 과 같음)
    }

    /// <summary>.out 형식 검사 (3절): TI COFF1/COFF2 (머리 22B, 대상 ID·링크 시각) 또는 ELF32 LE C2000(e_machine 141)</summary>
    static (string format, ushort target, uint timestamp)? CheckOut(byte[] b, out string? err)
    {
        err = null;
        if (b.Length >= 4 && b[0] == 0x7F && b[1] == (byte)'E' && b[2] == (byte)'L' && b[3] == (byte)'F')
        {
            if (b.Length < 20 || b[4] != 1 || b[5] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(18)) != ElfMachineC2000)
            {
                err = ".out 이 C2000 .out 이 아닙니다 (ELF32 리틀엔디언, e_machine 141 이어야 함)";
                return null;
            }
            return ("elf", ElfMachineC2000, 0);
        }
        if (b.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(b) is 0x00C1 or 0x00C2)
        {
            if (b.Length < 22) { err = ".out 의 TI COFF 머리가 22바이트보다 짧습니다 — 손상된 .out"; return null; }
            var fmt = BinaryPrimitives.ReadUInt16LittleEndian(b) == 0x00C1 ? "coff1" : "coff2";
            return (fmt, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(20)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        }
        err = ".out 이 TI .out(COFF/ELF) 이 아닙니다 — CCS 링커 출력(.out)을 넣으세요";
        return null;
    }

    static string BaseName(string path) { var i = path.LastIndexOfAny(['/', '\\']); return i >= 0 ? path[(i + 1)..] : path; }

    /// <summary>압축 해제하며 한도를 넘으면 중단 (ZIP 의 선언 크기를 믿지 않음 — zip bomb 방지)</summary>
    static byte[]? ReadLimited(ZipArchiveEntry e, long max, string what, out string? err)
    {
        err = null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > max) { err = $"패키지의 {what} 이 한도 {Size(max)} 를 넘습니다"; return null; }
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    static DspPackageFile? FileOf(ZipArchiveEntry e, byte[] data, JsonElement m, string key, out string? err)
    {
        err = null;
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (Str(m, "name") is { } name && name != e.FullName) { err = $"manifest 의 files.{key}.name({name})이 패키지 항목({e.FullName})과 다릅니다" + Remake; return null; }
        if (Long(m, "size") is not { } size || size != data.Length) { err = $"패키지의 {e.FullName} 크기(실제 {data.Length:N0} B)가 manifest(files.{key}.size)와 다릅니다" + Remake; return null; }
        if (Str(m, "sha256") is not { } want || !want.Equals(sha, StringComparison.OrdinalIgnoreCase)) { err = $"패키지의 {e.FullName} SHA-256 이 manifest 와 다릅니다 (손상되었거나 수정됨)" + Remake; return null; }
        var orig = Str(m, "orig_name");
        return new DspPackageFile(e.FullName, string.IsNullOrEmpty(orig) ? e.FullName : orig, data, sha);
    }

    static string? Str(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static long? Long(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
    static bool Obj(JsonElement o, string k, out JsonElement v)
    {
        v = default;
        return o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.Object;
    }

    public static string Size(long b) => b >= 1024 * 1024 && b % (1024 * 1024) == 0 ? $"{b / (1024 * 1024)} MiB" : b >= 1024 && b % 1024 == 0 ? $"{b / 1024} KiB" : $"{b:N0} B";
}

/// <summary>패키지 안의 파일 하나. Name = ZIP 항목 이름(규칙에 맞음), OrigName = manifest orig_name(없으면 Name), Sha256 소문자 hex</summary>
public sealed record DspPackageFile(string Name, string OrigName, byte[] Data, string Sha256);

/// <summary>Package 또는 Error 중 하나</summary>
public sealed record DspPackageResult(DspPackage? Package, string? Error)
{
    public bool Ok => Package is not null;
}

/// <summary>압축 해제 기준 한도 (1절). 시험에서는 작게 바꿔 쓴다</summary>
public sealed record DspPackageLimits
{
    /// <summary>브리지 dspfw 파티션 0x60000 − 스테이징 머리 64 B</summary>
    public long Dspfw { get; init; } = 0x60000 - 64;
    public long Out { get; init; } = 64L * 1024 * 1024;
    public long Map { get; init; } = 16L * 1024 * 1024;
    public long Manifest { get; init; } = 64 * 1024;
    public static DspPackageLimits Default { get; } = new();
}

/// <summary>
/// 테스트·도구용: 규격 v1 패키지를 만든다. .out·.map 은 합성(머리만 맞춘 가짜)이라 실제 빌드 산출물이 아니다 —
/// 실제 패키지는 dsp_pack.py bundle 로 만든다.
/// </summary>
public static class DspPackageBuilder
{
    /// <summary>시험 기본 링크 시각 (2026-10-31 05:00:10 UTC)</summary>
    public const uint DefaultLinkTime = 1793422810;

    /// <summary>TI COFF 머리 22B + 채움 (magic 0xC1 = coff1, 0xC2 = coff2)</summary>
    public static byte[] CoffOut(uint timestamp, ushort target = DspPackage.CoffTargetC2000, ushort magic = 0x00C2, int size = 278)
    {
        var b = new byte[Math.Max(size, 22)];
        BinaryPrimitives.WriteUInt16LittleEndian(b, magic);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), target);
        for (int i = 22; i < b.Length; i++) b[i] = (byte)(i - 22);
        return b;
    }

    /// <summary>ELF32 LE 머리 + 채움 (e_machine 기본 141 = C2000)</summary>
    public static byte[] ElfOut(ushort machine = DspPackage.ElfMachineC2000, int size = 128)
    {
        var b = new byte[Math.Max(size, 52)];
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F'; b[4] = 1; b[5] = 1; b[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), machine);
        return b;
    }

    /// <summary>unix 초 → C ctime 형식 (일자는 앞 공백 2자리)</summary>
    public static string Ctime(long unix)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return d.ToString("ddd MMM ", CultureInfo.InvariantCulture) + d.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2) + d.ToString(" HH:mm:ss yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>CCS 링커 맵 머리 흉내 (linkedUnix 가 null 이면 '>> Linked' 줄 없음)</summary>
    public static string Map(string outputPath, long? linkedUnix) =>
        "******************************************************************************\n" +
        "            TMS320C2000 Linker PC v18.12.4\n" +
        "******************************************************************************\n" +
        (linkedUnix is { } l ? $">> Linked {Ctime(l)}\n" : "") +
        $"\nOUTPUT FILE NAME:   <{outputPath}>\nENTRY POINT SYMBOL: \"_c_int00\"  address: 003e8040\n";

    static string Sha(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();

    /// <summary>규격 2절 manifest (파일 정보는 주어진 바이트에서 계산)</summary>
    public static JsonObject Manifest(byte[] dspfw, string dspfwName, byte[] outB, string outName, byte[] mapB, string mapName, string linkCheck)
    {
        var img = DspfwImage.Parse(dspfw);
        var ver = dspfw.Length >= 32 ? Encoding.ASCII.GetString(dspfw.AsSpan(16, 16)).TrimEnd('\0') : "";
        var mapText = Encoding.Latin1.GetString(mapB);
        // 시험에서 일부러 깨뜨린 .out 도 manifest 는 만들 수 있게 (짧으면 0)
        var outFmt = outB.Length >= 4 && outB[0] == 0x7F ? "elf" : outB.Length >= 2 && outB[0] == 0xC1 && outB[1] == 0 ? "coff1" : "coff2";
        ushort U16(int off) => outB.Length >= off + 2 ? BinaryPrimitives.ReadUInt16LittleEndian(outB.AsSpan(off)) : (ushort)0;
        uint U32(int off) => outB.Length >= off + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(outB.AsSpan(off)) : 0;
        var mo = Regex.Match(mapText, @"OUTPUT FILE NAME:\s*<([^>]+)>");
        var ml = Regex.Match(mapText, @"^>> Linked (.+)$", RegexOptions.Multiline);
        var outInfo = new JsonObject
        {
            ["name"] = outName, ["orig_name"] = outName, ["size"] = outB.Length, ["sha256"] = Sha(outB), ["format"] = outFmt,
            ["target"] = outFmt == "elf" ? U16(18) : U16(20),
            ["timestamp"] = outFmt == "elf" ? 0 : U32(4),
        };
        return new JsonObject
        {
            ["format"] = DspPackage.Format, ["format_ver"] = DspPackage.FormatVersion, ["ver"] = img?.Version ?? ver,
            ["created_utc"] = "2026-10-31T05:00:00Z", ["tool"] = "DspPackageBuilder (test)", ["hex2000"] = "", ["link_check"] = linkCheck,
            ["files"] = new JsonObject
            {
                ["dspfw"] = new JsonObject { ["name"] = dspfwName, ["size"] = dspfw.Length, ["sha256"] = Sha(dspfw) },
                ["out"] = outInfo,
                ["map"] = new JsonObject
                {
                    ["name"] = mapName, ["orig_name"] = mapName, ["size"] = mapB.Length, ["sha256"] = Sha(mapB),
                    ["output"] = mo.Success ? mo.Groups[1].Value.Split('/', '\\')[^1] : "",
                    ["linked"] = ml.Success ? string.Join(' ', ml.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : "",
                },
            },
            ["dspfw"] = img is null ? new JsonObject() : new JsonObject { ["app_len"] = img.AppLen, ["app_crc32"] = img.AppCrc32.ToString("x8"), ["build_time"] = img.BuildTime },
        };
    }

    public static byte[] ManifestBytes(JsonObject m) => Encoding.UTF8.GetBytes(m.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

    /// <summary>항목들을 그대로 ZIP 으로 (deflate)</summary>
    public static byte[] Zip(IEnumerable<(string name, byte[] data)> entries)
    {
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var s = za.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    /// <summary>
    /// .dspfw 로 올바른 패키지: 합성 COFF2 .out(링크 시각 linkTime) + 링크 PC 시간대 tzOffsetSec 만큼 앞선 Linked 시각의 .map.
    /// elf=true 면 ELF .out(시각 없음 → 이름으로 연결 확인)
    /// </summary>
    public static byte[] Build(byte[] dspfw, string stem = "AGV_Main", uint linkTime = DefaultLinkTime, int tzOffsetSec = 9 * 3600, bool elf = false, string? linkCheck = null)
    {
        var ver = DspfwImage.Parse(dspfw)?.Version ?? throw new ArgumentException(".dspfw 가 아님", nameof(dspfw));
        var outB = elf ? ElfOut() : CoffOut(linkTime);
        var mapB = Encoding.Latin1.GetBytes(Map($"./Debug/{stem}.out", elf ? null : linkTime + tzOffsetSec));
        var (dn, on, mn) = ($"{stem}_{ver}.dspfw", $"{stem}.out", $"{stem}.map");
        var man = ManifestBytes(Manifest(dspfw, dn, outB, on, mapB, mn, linkCheck ?? (elf ? "name" : "time")));
        return Zip([("manifest.json", man), (dn, dspfw), (on, outB), (mn, mapB)]);
    }
}
