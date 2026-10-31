using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gateway.Core.Fota;
using Gateway.Server.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Server.Services;

public enum FirmwareKind { Dsp, Esp }

public sealed class FirmwareFile
{
    /// <summary>주 파일(브리지가 받는 것): DSP "dspfw", ESP "esp". 메인보드 패키지의 보관 파일: "out", "map", "pkg"(올린 .dsppkg 그대로)</summary>
    public string Slot { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class FirmwareEntry
{
    public string Id { get; set; } = "";
    public FirmwareKind Kind { get; set; }
    public string Version { get; set; } = "";
    public string Project { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime UploadedAt { get; set; }
    /// <summary>주 파일이 맨 앞, 이어서 out·map·pkg (메인보드 패키지로 올린 것만)</summary>
    public List<FirmwareFile> Files { get; set; } = new();
    /// <summary>메인보드 패키지 ID (manifest.json sha256 앞 16자). 패키지 도입(0.7.0) 전에 .dspfw 만 올린 것·브리지는 ""</summary>
    public string PkgId { get; set; } = "";
    /// <summary>패키지의 .map↔.out 연결 확인: "time" | "name" | "skipped"(패커가 건너뜀 — 경고 표시). 이전 등록·브리지는 ""</summary>
    public string LinkCheck { get; set; } = "";
    /// <summary>이 메인보드 버전의 .out·.map 을 게이트웨이가 보관하고 있는지 (원본 분실 대비)</summary>
    public bool HasOriginals => PkgId.Length > 0 && FileOf(FirmwareStore.SlotOut) is not null && FileOf(FirmwareStore.SlotMap) is not null;
    /// <summary>브리지가 받아 가는 파일 (DSP .dspfw, ESP .bin) — 작업 실행기는 이것만 쓴다</summary>
    [JsonIgnore] public FirmwareFile? Primary => FileOf(Kind == FirmwareKind.Dsp ? FirmwareStore.SlotDspfw : FirmwareStore.SlotEsp) ?? Files.FirstOrDefault();
    public FirmwareFile? FileOf(string slot) => Files.FirstOrDefault(f => f.Slot == slot);
    public string FilesText => string.Join(", ", Files.Select(f => $"{f.Slot}:{f.FileName} ({f.Size:N0}B)"));
}

/// <summary>
/// 펌웨어 저장소 — 파일은 {DataDir}/firmware/{id}/ 에, 목록은 SQLite firmware 표(주 파일) + firmware_files 표(메인보드 .out·.map·패키지)에
/// (예전 index.json 은 첫 실행 때 옮긴다)
/// </summary>
public sealed class FirmwareStore
{
    readonly string _root;
    readonly GatewayDb _db;
    readonly ILogger _log;
    readonly object _lock = new();
    readonly List<FirmwareEntry> _entries = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    /// <summary>저장된 파일 이름 최대 길이 — 메인보드 스테이징 URL(http://IP:포트/fw/{id}/{파일명}) 이 브리지 한도 199자를 넘지 않게</summary>
    public const int MaxFileName = 60;
    public const string SlotDspfw = "dspfw", SlotEsp = "esp", SlotOut = "out", SlotMap = "map", SlotPkg = "pkg";
    /// <summary>메인보드 업로드는 패키지만 — .dspfw 만 올리면 이 안내로 거부</summary>
    public const string PackageOnly = "메인보드 펌웨어는 .out·.map 이 든 패키지(.dsppkg)로 올립니다 — dsp_pack.py bundle (AGV_Wifi_Bridge tools)";

    /// <summary>목록이 바뀌면 (등록·삭제) — 화면 갱신용</summary>
    public event Action? Changed;

    public FirmwareStore(string dataDir, GatewayDb db, ILogger<FirmwareStore>? log = null)
    {
        _root = Path.Combine(dataDir, "firmware");
        _db = db;
        _log = (ILogger?)log ?? NullLogger.Instance;
        Directory.CreateDirectory(_root);
        Load();
        MigrateJson(Path.Combine(_root, "index.json"));
    }

    void Load()
    {
        using var c = _db.Open();
        using (var cmd = GatewayDb.Cmd(c, "SELECT id, kind, version, project, notes, uploaded_at, slot, file_name, size, sha256, pkg_id, link_check FROM firmware"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                _entries.Add(new FirmwareEntry
                {
                    Id = r.GetString(0), Kind = Enum.Parse<FirmwareKind>(r.GetString(1)), Version = r.GetString(2), Project = r.GetString(3), Notes = r.GetString(4),
                    UploadedAt = GatewayDb.Dt(r.GetString(5)),
                    Files = { new FirmwareFile { Slot = r.GetString(6), FileName = r.GetString(7), Size = r.GetInt64(8), Sha256 = r.GetString(9) } },
                    PkgId = r.GetString(10), LinkCheck = r.GetString(11),
                });
        // 주 파일 외의 보관 파일 (v5). 이전 등록은 없음
        var byId = _entries.ToDictionary(e => e.Id);
        using (var cmd = GatewayDb.Cmd(c, "SELECT firmware_id, slot, file_name, size, sha256 FROM firmware_files"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (byId.TryGetValue(r.GetString(0), out var e))
                    e.Files.Add(new FirmwareFile { Slot = r.GetString(1), FileName = r.GetString(2), Size = r.GetInt64(3), Sha256 = r.GetString(4) });
        foreach (var e in _entries) SortFiles(e);
    }

    static readonly string[] SlotOrder = [SlotDspfw, SlotEsp, SlotOut, SlotMap, SlotPkg];
    static void SortFiles(FirmwareEntry e)
    {
        var sorted = e.Files.OrderBy(f => Array.IndexOf(SlotOrder, f.Slot) is var i and >= 0 ? i : 99).ToList();
        e.Files.Clear();
        e.Files.AddRange(sorted);
    }

    /// <summary>
    /// SQLite 도입 전 index.json 이 있으면 한 번 가져오고 .migrated 로 이름을 바꾼다.
    /// 한 트랜잭션으로 넣고 커밋에 성공했을 때만 이름을 바꾼다. 실패하면 로그만 남기고 원본을 둬 다음 기동 때 다시 시도한다 (기동은 계속).
    /// </summary>
    void MigrateJson(string indexPath)
    {
        if (!File.Exists(indexPath)) return;
        try
        {
            var old = JsonSerializer.Deserialize<List<FirmwareEntry>>(File.ReadAllText(indexPath), Json) ?? new();
            var add = old.Where(e => e.Files.Count == 1 && !_entries.Any(x => x.Id == e.Id)).GroupBy(e => e.Id).Select(g => g.First()).ToList();
            if (add.Count > 0)
            {
                using var c = _db.Open();
                using var tx = c.BeginTransaction();
                foreach (var e in add) Insert(c, e);
                tx.Commit();
                _entries.AddRange(add);
            }
            File.Move(indexPath, indexPath + ".migrated", true);
            if (add.Count > 0) _log.LogInformation("firmware index.json → SQLite: {N}개 이전", add.Count);
        }
        catch (Exception e) { _log.LogError("firmware index.json 이전 실패 (원본 유지, 다음 기동 때 다시 시도): {Msg}", e.Message); }
    }

    /// <summary>
    /// firmware 행(주 파일)과 firmware_files 행(나머지)을 함께 쓴다 — 부르는 쪽이 트랜잭션으로 감싼다.
    /// 주 파일을 firmware 의 기존 열에 두는 것은 이전 서버(자체 업데이트 되돌리기)도 이 행을 그대로 배포할 수 있게 하기 위해서
    /// </summary>
    static void Insert(Microsoft.Data.Sqlite.SqliteConnection c, FirmwareEntry e)
    {
        var f = e.Primary ?? throw new InvalidOperationException("파일이 없습니다");
        GatewayDb.Exec(c, """
            INSERT OR REPLACE INTO firmware(id, kind, version, project, notes, uploaded_at, slot, file_name, size, sha256, pkg_id, link_check)
            VALUES($id, $kind, $ver, $proj, $notes, $up, $slot, $file, $size, $sha, $pkg, $lc)
            """, ("$id", e.Id), ("$kind", e.Kind.ToString()), ("$ver", e.Version), ("$proj", e.Project), ("$notes", e.Notes),
            ("$up", GatewayDb.Ts(e.UploadedAt)), ("$slot", f.Slot), ("$file", f.FileName), ("$size", f.Size), ("$sha", f.Sha256),
            ("$pkg", e.PkgId), ("$lc", e.LinkCheck));
        foreach (var x in e.Files.Where(x => x != f))
            GatewayDb.Exec(c, "INSERT OR REPLACE INTO firmware_files(firmware_id, slot, file_name, size, sha256) VALUES($id, $slot, $file, $size, $sha)",
                ("$id", e.Id), ("$slot", x.Slot), ("$file", x.FileName), ("$size", x.Size), ("$sha", x.Sha256));
    }

    public IReadOnlyList<FirmwareEntry> List() { lock (_lock) return _entries.OrderByDescending(e => e.UploadedAt).ToList(); }
    public FirmwareEntry? Get(string id) { lock (_lock) return _entries.FirstOrDefault(e => e.Id == id); }
    public string PathOf(FirmwareEntry e, FirmwareFile f) => Path.Combine(_root, e.Id, f.FileName);

    /// <summary>
    /// DSP: dsp_pack.py bundle 이 만든 패키지 .dsppkg 한 파일 (manifest·.dspfw·.out·.map 검사 — DspPackage). .dspfw 단독은 거부.
    ///      {id}/ 에 패키지 그대로 + 풀어낸 .dspfw(주 파일, 브리지가 받음)·.out·.map 을 둔다.
    /// ESP: agv_wifi_bridge.bin (ESP-IDF 앱 디스크립터에서 버전·프로젝트를 읽어 검증).
    /// 버전 인자는 비어 있을 때만 보조 (적으면 파일 버전과 같은지 확인).
    /// </summary>
    public async Task<FirmwareEntry> AddAsync(FirmwareKind kind, string version, string project, string notes,
                                              IEnumerable<(string slot, string fileName, Stream data)> files, CancellationToken ct = default)
        => (await AddCheckedAsync(kind, version, project, notes, files, ct)).Entry;

    /// <summary>AddAsync + 등록 경고(패키지 검사 경고·같은 패키지 중복). 경고는 저장하지 않는다 — 화면·업로드 응답에만</summary>
    public async Task<(FirmwareEntry Entry, IReadOnlyList<string> Warnings)> AddCheckedAsync(FirmwareKind kind, string version, string project, string notes,
                                              IEnumerable<(string slot, string fileName, Stream data)> files, CancellationToken ct = default)
    {
        var list = files.ToList();
        if (list.Count == 0) throw new InvalidOperationException("파일이 없습니다");
        if (list.Count > 1) throw new InvalidOperationException("파일은 하나만 올립니다" + (kind == FirmwareKind.Dsp ? " — 메인보드는 .dsppkg 패키지 하나" : ""));
        var (_, name, data) = list[0];
        if (kind == FirmwareKind.Dsp && !name.EndsWith(".dsppkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException((name.EndsWith(".dspfw", StringComparison.OrdinalIgnoreCase) ? $"{Path.GetFileName(name)}: .dspfw 만으로는 올릴 수 없습니다. " : $"{Path.GetFileName(name)}: ")
                                                + PackageOnly);
        var bytes = await ReadAllAsync(data, kind == FirmwareKind.Dsp ? DspPackage.MaxPackageBytes : 64L * 1024 * 1024, ct);
        var e = new FirmwareEntry
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Kind = kind, Version = version.Trim(), Project = project.Trim(), Notes = notes.Trim(), UploadedAt = DateTime.UtcNow,
        };
        var toWrite = new List<(string fileName, byte[] data)>();
        var warnings = new List<string>();
        if (kind == FirmwareKind.Dsp)
        {
            var r = DspPackage.Read(bytes, Limits);
            var pkg = r.Package ?? throw new InvalidOperationException($"{Path.GetFileName(name)}: {r.Error}");
            if (string.IsNullOrEmpty(e.Version)) e.Version = pkg.Version;
            else if (e.Version != pkg.Version) throw new InvalidOperationException($"입력한 버전 {e.Version} 과 패키지 버전 {pkg.Version} 이 다릅니다");
            e.PkgId = pkg.PkgId;
            e.LinkCheck = pkg.LinkCheck;
            warnings.AddRange(pkg.Warnings);
            // 항목 이름은 패키지 규칙(영문·숫자·._- 60자 이하)을 이미 통과 — SafeName 은 한 번 더 막는 것
            var dspfwName = SafeName(pkg.Dspfw.Name);
            var pkgName = SafeName(Path.GetFileName(name));
            if (!pkgName.EndsWith(".dsppkg", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(pkgName).Trim('_').Length < 3)
                pkgName = SafeName(Path.GetFileNameWithoutExtension(dspfwName) + ".dsppkg");
            foreach (var (slot, fn, d, sha) in new[]
                     {
                         (SlotDspfw, dspfwName, pkg.Dspfw.Data, pkg.Dspfw.Sha256), (SlotOut, SafeName(pkg.Out.Name), pkg.Out.Data, pkg.Out.Sha256),
                         (SlotMap, SafeName(pkg.Map.Name), pkg.Map.Data, pkg.Map.Sha256), (SlotPkg, pkgName, bytes, Convert.ToHexString(SHA256.HashData(bytes))),
                     })
            {
                // 확장자(.dspfw·.out·.map·.dsppkg)가 서로 달라 이름이 겹치지 않는다
                e.Files.Add(new FirmwareFile { Slot = slot, FileName = fn, Size = d.Length, Sha256 = sha.ToUpperInvariant() });
                toWrite.Add((fn, d));
            }
            if (FindPackage(pkg.PkgId) is { } same)
                warnings.Add($"같은 패키지(ID {pkg.PkgId})가 이미 등록되어 있습니다 — v{same.Version}, {same.UploadedAt.ToLocalTime():yyyy-MM-dd HH:mm} 업로드. 내용이 같으니 하나만 남기세요");
        }
        else
        {
            var info = EspAppImage.Parse(bytes) ?? throw new InvalidOperationException($"{name}: ESP-IDF 앱 이미지가 아닙니다 (build/agv_wifi_bridge.bin 을 올리세요. bootloader·partition-table·merged 이미지는 불가)");
            if (!string.Equals(info.Project, EspAppImage.BridgeProject, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{name}: 프로젝트가 '{info.Project}' 입니다 — '{EspAppImage.BridgeProject}' 빌드만 올릴 수 있습니다");
            if (string.IsNullOrEmpty(e.Version)) e.Version = info.Version;
            else if (e.Version != info.Version) throw new InvalidOperationException($"입력한 버전 {e.Version} 과 이미지 버전 {info.Version} 이 다릅니다");
            if (string.IsNullOrEmpty(e.Notes)) e.Notes = $"빌드 {info.BuildDate}, IDF {info.IdfVersion}";
            var outName = SafeName(Path.GetFileName(name));
            // 한글 등으로 이름이 거의 사라지면 종류·버전으로 이름을 만든다
            if (Path.GetFileNameWithoutExtension(outName).Trim('_').Length < 3) outName = $"agv_wifi_bridge_{SafeName(e.Version)}.bin";
            e.Files.Add(new FirmwareFile { Slot = SlotEsp, FileName = outName, Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
            toWrite.Add((outName, bytes));
        }
        if (string.IsNullOrEmpty(e.Version)) throw new InvalidOperationException("버전을 알 수 없습니다");
        if (string.IsNullOrEmpty(e.Project)) e.Project = kind == FirmwareKind.Dsp ? "agv-dsp" : EspAppImage.BridgeProject;

        var dir = Path.Combine(_root, e.Id);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (fn, d) in toWrite) await File.WriteAllBytesAsync(Path.Combine(dir, fn), d, ct);
            // DB 에 먼저 쓰고(firmware·firmware_files 한 트랜잭션), 성공했을 때만 메모리에 반영 (실패하면 아래에서 폴더 정리).
            // DB 는 저장소 lock 밖에서 — 새 id 는 아직 목록에 없어 다른 곳이 건드릴 수 없고, lock 안에서 DB 를 기다리면
            // 작업 lock 을 쥔 JobService.Create(→ Get)까지 막힌다
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                Insert(c, e);
                tx.Commit();
            }
            lock (_lock) _entries.Add(e);
        }
        catch
        {
            TryDeleteDir(dir);
            throw;
        }
        _log.LogInformation("펌웨어 등록: {Kind} {Ver} ({Id}){Pkg}", e.Kind, e.Version, e.Id, e.PkgId.Length > 0 ? $" 패키지 {e.PkgId} link {e.LinkCheck}" : "");
        foreach (var w in warnings) _log.LogWarning("펌웨어 {Id}: {Warn}", e.Id, w);
        RaiseChanged();
        return (e, warnings);
    }

    /// <summary>패키지 검사 한도 (시험에서 작게)</summary>
    internal DspPackageLimits Limits { get; set; } = DspPackageLimits.Default;

    static async Task<byte[]> ReadAllAsync(Stream s, long max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidOperationException($"파일이 {DspPackage.Size(max)} 를 넘습니다");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>같은 패키지 ID 가 이미 등록되어 있으면 반환 (중복 업로드 경고용)</summary>
    public FirmwareEntry? FindPackage(string pkgId)
    {
        if (string.IsNullOrEmpty(pkgId)) return null;
        lock (_lock) return _entries.FirstOrDefault(e => e.PkgId == pkgId);
    }

    /// <summary>
    /// .out·.map 을 보관하고 있는 메인보드 버전들 (앞 8자, DspVersion.Head) — 호기 현황이 한 번 만들어 행마다 찾는다
    /// </summary>
    public HashSet<string> VersionsWithOriginals()
    {
        lock (_lock) return _entries.Where(e => e.Kind == FirmwareKind.Dsp && e.HasOriginals).Select(e => DspVersion.Head(e.Version)).ToHashSet();
    }

    /// <summary>같은 종류·버전이 이미 등록되어 있으면 반환 (중복 업로드 경고용)</summary>
    public FirmwareEntry? FindDuplicate(FirmwareKind kind, string version)
    {
        lock (_lock) return _entries.FirstOrDefault(e => e.Kind == kind && e.Version == version.Trim());
    }

    /// <summary>파일명을 ASCII 안전 문자로 정리하고 MaxFileName 자 이하로 줄인다 (URL·배치 호환, 경로 조작 방지, 확장자 유지)</summary>
    public static string SafeName(string name)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9._-]+", "_").Trim('.', '_');
        if (string.IsNullOrEmpty(cleaned)) return "firmware.bin";
        if (cleaned.Length <= MaxFileName) return cleaned;
        var ext = Path.GetExtension(cleaned);
        if (ext.Length > 10) ext = "";
        var stem = cleaned[..^ext.Length].TrimEnd('.', '_');
        return stem[..Math.Min(stem.Length, MaxFileName - ext.Length)].TrimEnd('.', '_') + ext;
    }

    /// <summary>
    /// 펌웨어를 지운다 (DB 를 먼저 지우고 성공했을 때만 메모리에서 뺀다 — DB 가 실패하면 예외를 던지고 목록은 그대로).
    /// 화면·REST 는 이것을 직접 부르지 않고 JobService.DeleteFirmwareIfUnused 를 쓴다('사용 중' 확인과 작업 생성 차단이 거기에 있음 —
    /// 같은 id 를 동시에 둘이 지우지 않는다).
    /// 저장소 lock 은 메모리 목록만 지킨다: DB 는 lock 밖에서 (lock 을 쥔 채 DB 잠금을 기다리면 작업 lock 안의 Get 까지 막힌다).
    /// </summary>
    internal bool Delete(string id)
    {
        FirmwareEntry? e;
        lock (_lock) e = _entries.FirstOrDefault(x => x.Id == id);
        if (e is null) return false;
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            // 외래 키(ON DELETE CASCADE)에 기대지 않고 두 표를 함께 지운다 — 한 트랜잭션
            GatewayDb.Exec(c, "DELETE FROM firmware_files WHERE firmware_id=$id", ("$id", id));
            GatewayDb.Exec(c, "DELETE FROM firmware WHERE id=$id", ("$id", id));
            tx.Commit();
        }
        lock (_lock) _entries.Remove(e);
        TryDeleteDir(Path.Combine(_root, id));
        _log.LogInformation("펌웨어 삭제: {Kind} {Ver} ({Id})", e.Kind, e.Version, id);
        RaiseChanged();
        return true;
    }

    void TryDeleteDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            try { Directory.Delete(dir, true); }
            catch (UnauthorizedAccessException)
            {
                // 읽기 전용 파일이 있으면 속성을 풀고 한 번 더
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, true);
            }
        }
        catch (Exception x) { _log.LogWarning("펌웨어 폴더 삭제 실패 {Dir}: {Msg}", dir, x.Message); }
    }

    void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch (Exception x) { _log.LogDebug("firmware changed: {Msg}", x.Message); }
    }
}
