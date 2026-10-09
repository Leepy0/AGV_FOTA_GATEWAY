using System.Security.Cryptography;
using System.Text.Json;
using Gateway.Core.Fota;

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

/// <summary>펌웨어 저장소 — {DataDir}/firmware/{id}/ 에 파일, index.json 에 목록</summary>
public sealed class FirmwareStore
{
    readonly string _root;
    readonly string _index;
    readonly object _lock = new();
    List<FirmwareEntry> _entries = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public FirmwareStore(string dataDir)
    {
        _root = Path.Combine(dataDir, "firmware");
        _index = Path.Combine(_root, "index.json");
        Directory.CreateDirectory(_root);
        if (File.Exists(_index))
            _entries = JsonSerializer.Deserialize<List<FirmwareEntry>>(File.ReadAllText(_index), Json) ?? new();
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
        lock (_lock) { _entries.Add(e); Save(); }
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
        var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"[^A-Za-z0-9._-]", "_").Trim('.', '_');
        return string.IsNullOrEmpty(cleaned) ? "firmware.bin" : cleaned;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            _entries.Remove(e);
            Save();
            try { Directory.Delete(Path.Combine(_root, id), true); } catch (IOException) { }
            return true;
        }
    }

    void Save() => File.WriteAllText(_index, JsonSerializer.Serialize(_entries, Json));
}
