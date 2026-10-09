using System.Security.Cryptography;
using System.Text.Json;
using Gateway.Core.Fota;
using Gateway.Server.Data;

namespace Gateway.Server.Services;

public enum FirmwareKind { Dsp, Esp }

public sealed class FirmwareFile
{
    /// <summary>DSP: "dspfw", ESP: "esp"</summary>
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
    public List<FirmwareFile> Files { get; set; } = new();
    public string FilesText => string.Join(", ", Files.Select(f => $"{f.Slot}:{f.FileName} ({f.Size:N0}B)"));
}

/// <summary>펌웨어 저장소 — 파일은 {DataDir}/firmware/{id}/ 에, 목록은 SQLite firmware 표에 (예전 index.json 은 첫 실행 때 옮긴다)</summary>
public sealed class FirmwareStore
{
    readonly string _root;
    readonly GatewayDb _db;
    readonly object _lock = new();
    readonly List<FirmwareEntry> _entries = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public FirmwareStore(string dataDir, GatewayDb db)
    {
        _root = Path.Combine(dataDir, "firmware");
        _db = db;
        Directory.CreateDirectory(_root);
        Load();
        MigrateJson(Path.Combine(_root, "index.json"));
    }

    void Load()
    {
        using var c = _db.Open();
        using var cmd = GatewayDb.Cmd(c, "SELECT id, kind, version, project, notes, uploaded_at, slot, file_name, size, sha256 FROM firmware");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            _entries.Add(new FirmwareEntry
            {
                Id = r.GetString(0), Kind = Enum.Parse<FirmwareKind>(r.GetString(1)), Version = r.GetString(2), Project = r.GetString(3), Notes = r.GetString(4),
                UploadedAt = GatewayDb.Dt(r.GetString(5)),
                Files = { new FirmwareFile { Slot = r.GetString(6), FileName = r.GetString(7), Size = r.GetInt64(8), Sha256 = r.GetString(9) } },
            });
    }

    /// <summary>SQLite 도입 전 index.json 이 있으면 한 번 가져오고 .migrated 로 이름을 바꾼다</summary>
    void MigrateJson(string indexPath)
    {
        if (!File.Exists(indexPath)) return;
        try
        {
            var old = JsonSerializer.Deserialize<List<FirmwareEntry>>(File.ReadAllText(indexPath), Json) ?? new();
            int n = 0;
            foreach (var e in old.Where(e => e.Files.Count == 1 && !_entries.Any(x => x.Id == e.Id)))
            {
                Insert(e); _entries.Add(e); n++;
            }
            File.Move(indexPath, indexPath + ".migrated", true);
            if (n > 0) Console.WriteLine($"firmware index.json → SQLite: {n}개 이전");
        }
        catch (Exception e) { Console.Error.WriteLine("firmware index.json 이전 실패: " + e.Message); }
    }

    void Insert(FirmwareEntry e)
    {
        var f = e.Files[0];
        using var c = _db.Open();
        GatewayDb.Exec(c, """
            INSERT OR REPLACE INTO firmware(id, kind, version, project, notes, uploaded_at, slot, file_name, size, sha256)
            VALUES($id, $kind, $ver, $proj, $notes, $up, $slot, $file, $size, $sha)
            """, ("$id", e.Id), ("$kind", e.Kind.ToString()), ("$ver", e.Version), ("$proj", e.Project), ("$notes", e.Notes),
            ("$up", GatewayDb.Ts(e.UploadedAt)), ("$slot", f.Slot), ("$file", f.FileName), ("$size", f.Size), ("$sha", f.Sha256));
    }

    public IReadOnlyList<FirmwareEntry> List() { lock (_lock) return _entries.OrderByDescending(e => e.UploadedAt).ToList(); }
    public FirmwareEntry? Get(string id) { lock (_lock) return _entries.FirstOrDefault(e => e.Id == id); }
    public string PathOf(FirmwareEntry e, FirmwareFile f) => Path.Combine(_root, e.Id, f.FileName);

    /// <summary>
    /// DSP: dsp_pack.py 가 만든 .dspfw 한 파일 (헤더의 버전·CRC 를 읽어 기록, 버전 인자는 비어 있을 때만 보조).
    /// ESP: agv_wifi_bridge.bin (ESP-IDF 앱 디스크립터에서 버전·프로젝트를 읽어 검증).
    /// </summary>
    public async Task<FirmwareEntry> AddAsync(FirmwareKind kind, string version, string project, string notes,
                                              IEnumerable<(string slot, string fileName, Stream data)> files, CancellationToken ct = default)
    {
        var e = new FirmwareEntry
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Kind = kind, Version = version.Trim(), Project = project.Trim(), Notes = notes.Trim(), UploadedAt = DateTime.UtcNow,
        };
        var dir = Path.Combine(_root, e.Id);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (slot, name, data) in files)
            {
                if (e.Files.Count == 1) throw new InvalidOperationException("파일은 하나만 올립니다");
                using var ms = new MemoryStream();
                await data.CopyToAsync(ms, ct);
                var bytes = ms.ToArray();
                string outName = SafeName(Path.GetFileName(name));
                // 한글 등으로 이름이 거의 사라지면 종류·버전으로 이름을 만든다 (버전은 파일을 읽은 뒤 확정되므로 자리만 잡고 아래서 바꿈)
                bool renameByVersion = Path.GetFileNameWithoutExtension(outName).Trim('_').Length < 3;
                if (kind == FirmwareKind.Dsp)
                {
                    var info = DspfwImage.Parse(bytes) ?? throw new InvalidOperationException($"{name}: .dspfw 형식이 아니거나 CRC 가 틀립니다 (dsp_pack.py 출력 파일을 올리세요)");
                    if (string.IsNullOrEmpty(e.Version)) e.Version = info.Version;
                    else if (e.Version != info.Version) throw new InvalidOperationException($"입력한 버전 {e.Version} 과 파일 헤더 버전 {info.Version} 이 다릅니다");
                    e.Files.Add(new FirmwareFile { Slot = "dspfw", FileName = outName, Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
                }
                else
                {
                    var info = EspAppImage.Parse(bytes) ?? throw new InvalidOperationException($"{name}: ESP-IDF 앱 이미지가 아닙니다 (build/agv_wifi_bridge.bin 을 올리세요. bootloader·partition-table·merged 이미지는 불가)");
                    if (!string.Equals(info.Project, EspAppImage.BridgeProject, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"{name}: 프로젝트가 '{info.Project}' 입니다 — '{EspAppImage.BridgeProject}' 빌드만 올릴 수 있습니다");
                    if (string.IsNullOrEmpty(e.Version)) e.Version = info.Version;
                    else if (e.Version != info.Version) throw new InvalidOperationException($"입력한 버전 {e.Version} 과 이미지 버전 {info.Version} 이 다릅니다");
                    if (string.IsNullOrEmpty(e.Notes)) e.Notes = $"빌드 {info.BuildDate}, IDF {info.IdfVersion}";
                    e.Files.Add(new FirmwareFile { Slot = "esp", FileName = outName, Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
                }
                if (renameByVersion)
                {
                    outName = $"{(kind == FirmwareKind.Dsp ? "agv_dsp" : "agv_wifi_bridge")}_{SafeName(e.Version)}{(kind == FirmwareKind.Dsp ? ".dspfw" : ".bin")}";
                    e.Files[^1].FileName = outName;
                }
                await File.WriteAllBytesAsync(Path.Combine(dir, outName), bytes, ct);
            }
            if (e.Files.Count == 0) throw new InvalidOperationException("파일이 없습니다");
            if (string.IsNullOrEmpty(e.Version)) throw new InvalidOperationException("버전을 알 수 없습니다");
            if (string.IsNullOrEmpty(e.Project)) e.Project = kind == FirmwareKind.Dsp ? "agv-dsp" : EspAppImage.BridgeProject;
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        lock (_lock) { Insert(e); _entries.Add(e); }
        return e;
    }

    /// <summary>같은 종류·버전이 이미 등록되어 있으면 반환 (중복 업로드 경고용)</summary>
    public FirmwareEntry? FindDuplicate(FirmwareKind kind, string version)
    {
        lock (_lock) return _entries.FirstOrDefault(e => e.Kind == kind && e.Version == version.Trim());
    }

    /// <summary>파일명을 ASCII 안전 문자로 정리 (URL·배치 호환, 경로 조작 방지)</summary>
    public static string SafeName(string name)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9._-]+", "_").Trim('.', '_');
        return string.IsNullOrEmpty(cleaned) ? "firmware.bin" : cleaned;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            _entries.Remove(e);
            using (var c = _db.Open()) GatewayDb.Exec(c, "DELETE FROM firmware WHERE id=$id", ("$id", id));
            try { Directory.Delete(Path.Combine(_root, id), true); } catch (IOException) { }
            return true;
        }
    }
}
