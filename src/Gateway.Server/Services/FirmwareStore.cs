using System.Security.Cryptography;
using System.Text.Json;
using Gateway.Core.Fota;

namespace Gateway.Server.Services;

public enum FirmwareKind { Dsp, Esp }

public sealed class FirmwareFile
{
    /// <summary>DSP: "A" / "B" / "any", ESP: "esp"</summary>
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
    /// DSP: raw .bin 이면 헤더를 붙여 .img 로 저장(slot/version/project 는 인자), 이미 헤더가 있으면 헤더 값을 우선.
    /// ESP: 파일 그대로.
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
        uint seq = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (slot, name, data) in files)
        {
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            string outName = Path.GetFileName(name);
            if (kind == FirmwareKind.Dsp)
            {
                var info = FirmwareImage.Parse(bytes);
                if (info is null)
                {
                    bytes = FirmwareImage.Build(bytes, e.Version, seq, FirmwareImage.SlotFromName(slot), string.IsNullOrEmpty(e.Project) ? FirmwareImage.DefaultProject : e.Project);
                    info = FirmwareImage.Parse(bytes)!;
                    outName = Path.ChangeExtension(outName, ".img");
                }
                else
                {
                    if (string.IsNullOrEmpty(e.Version)) e.Version = info.Version;
                    if (string.IsNullOrEmpty(e.Project)) e.Project = info.Project;
                }
                var f = new FirmwareFile { Slot = info.SlotName, FileName = outName, Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
                e.Files.Add(f);
            }
            else
            {
                e.Files.Add(new FirmwareFile { Slot = "esp", FileName = outName, Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
            }
            await File.WriteAllBytesAsync(Path.Combine(dir, outName), bytes, ct);
        }
        if (e.Files.Count == 0) throw new InvalidOperationException("파일이 없습니다");
        if (kind == FirmwareKind.Dsp && e.Files.Select(f => f.Slot).Distinct().Count() != e.Files.Count)
            throw new InvalidOperationException("같은 슬롯 파일이 둘입니다");
        if (string.IsNullOrEmpty(e.Project)) e.Project = kind == FirmwareKind.Dsp ? FirmwareImage.DefaultProject : "agv_wifi_bridge";
        lock (_lock) { _entries.Add(e); Save(); }
        return e;
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

    /// <summary>DSP: 실행 중 슬롯의 반대쪽에 맞는 파일. "any" 파일이 있으면 그것</summary>
    public FirmwareFile? PickDspFile(FirmwareEntry e, char runningSlot)
    {
        var target = runningSlot == 'A' ? "B" : runningSlot == 'B' ? "A" : null;
        return e.Files.FirstOrDefault(f => f.Slot == "any")
            ?? (target is null ? e.Files.FirstOrDefault(f => f.Slot == "A") : e.Files.FirstOrDefault(f => f.Slot == target));
    }

    void Save() => File.WriteAllText(_index, JsonSerializer.Serialize(_entries, Json));
}
