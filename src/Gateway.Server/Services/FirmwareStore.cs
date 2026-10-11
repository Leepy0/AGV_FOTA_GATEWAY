using System.Security.Cryptography;
using System.Text.Json;
using Gateway.Core.Fota;
using Gateway.Server.Data;
using Microsoft.Extensions.Logging.Abstractions;

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
    readonly ILogger _log;
    readonly object _lock = new();
    readonly List<FirmwareEntry> _entries = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    /// <summary>저장된 파일 이름 최대 길이 — 메인보드 스테이징 URL(http://IP:포트/fw/{id}/{파일명}) 이 브리지 한도 199자를 넘지 않게</summary>
    public const int MaxFileName = 60;

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

    static void Insert(Microsoft.Data.Sqlite.SqliteConnection c, FirmwareEntry e)
    {
        var f = e.Files[0];
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
                    // 앱의 버전 응답이 8자 고정이라 그보다 길면 굽고 나서도 '버전 다름' 실패가 된다
                    if (!DspVersion.IsValid(info.Version))
                        throw new InvalidOperationException($"메인보드 버전은 {DspVersion.MaxLength}자 이하 영문·숫자·._- 만 됩니다 (파일 헤더 버전 '{info.Version}') — dsp_pack.py --ver 를 고쳐 다시 묶으세요");
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
            // DB 에 먼저 쓰고, 성공했을 때만 메모리에 반영 (실패하면 아래에서 폴더 정리)
            lock (_lock)
            {
                using (var c = _db.Open()) Insert(c, e);
                _entries.Add(e);
            }
        }
        catch
        {
            TryDeleteDir(dir);
            throw;
        }
        _log.LogInformation("펌웨어 등록: {Kind} {Ver} ({Id})", e.Kind, e.Version, e.Id);
        RaiseChanged();
        return e;
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

    /// <summary>삭제 결과: 없음 / 사용 중(작업 이름) / 삭제됨</summary>
    public enum DeleteResult { NotFound, InUse, Deleted }

    public bool Delete(string id) => Delete(id, null, out _) == DeleteResult.Deleted;

    /// <summary>펌웨어를 아직 쓰는 작업인지 — 끝나지 않은 작업 전부 (예약·실행·적용 대기 등)</summary>
    public static bool InUseBy(Job j) => j.State is not (JobState.Done or JobState.Failed or JobState.Cancelled);

    /// <summary>Delete 의 usedBy 로 넘길 함수 — 이 펌웨어를 쓰는 끝나지 않은 작업 이름</summary>
    public static Func<string, IReadOnlyList<string>> ActiveJobsOf(JobService jobs) =>
        id => jobs.List().Where(j => j.FirmwareId == id && InUseBy(j)).Select(j => j.Name).ToList();

    /// <summary>
    /// 사용 여부 확인과 삭제를 저장소 lock 안에서 한 번에 한다 (확인 뒤 다른 사람이 그 펌웨어로 작업을 만들어도 지우지 않게).
    /// usedBy(id) 는 이 펌웨어를 쓰는 예약·실행 중 작업 이름 목록 — lock 순서는 펌웨어 → 작업 (JobService 는 작업 lock 안에서 펌웨어 저장소를 부르지 않는다).
    /// DB 삭제가 실패하면 예외를 그대로 던지고 메모리 목록은 그대로 둔다.
    /// </summary>
    public DeleteResult Delete(string id, Func<string, IReadOnlyList<string>>? usedBy, out IReadOnlyList<string> users)
    {
        users = [];
        FirmwareEntry? e;
        lock (_lock)
        {
            e = _entries.FirstOrDefault(x => x.Id == id);
            if (e is null) return DeleteResult.NotFound;
            if (usedBy is not null && (users = usedBy(id)).Count > 0) return DeleteResult.InUse;
            using (var c = _db.Open()) GatewayDb.Exec(c, "DELETE FROM firmware WHERE id=$id", ("$id", id));
            _entries.Remove(e);
            TryDeleteDir(Path.Combine(_root, id));
        }
        _log.LogInformation("펌웨어 삭제: {Kind} {Ver} ({Id})", e.Kind, e.Version, id);
        RaiseChanged();
        return DeleteResult.Deleted;
    }

    /// <summary>등록된 펌웨어로 작업을 만들 때 쓴다 — 펌웨어가 있으면 저장소 lock 안에서 f 를 실행 (그동안 삭제되지 않음)</summary>
    public T? UseIfExists<T>(string id, Func<FirmwareEntry, T> f) where T : class
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            return e is null ? null : f(e);
        }
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
