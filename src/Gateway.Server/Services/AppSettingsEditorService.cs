using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting.WindowsServices;\nusing Microsoft.Extensions.Options;

namespace Gateway.Server.Services;

/// <summary>웹에서 appsettings.json을 안전하게 편집한다. 비밀값은 마스킹해 반환하고, 마스크가 그대로 저장되면 원본 값을 보존한다.</summary>
public sealed class AppSettingsEditorService(IOptions<Gateway.Server.Options.UpdateOptions> update, IOptions<Gateway.Server.Options.GatewayOptions> gateway, IHostApplicationLifetime lifetime, ILogger<AppSettingsEditorService> log)
{
    const string Mask = "********";
    readonly string _path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    readonly string _restartDir = Path.Combine(gateway.Value.DataDir, "update");

    public string ReadMasked()
    {
        var root = JsonNode.Parse(File.ReadAllText(_path)) ?? throw new InvalidDataException("appsettings.json이 비어 있습니다.");
        MaskSecrets(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void Save(string json)
    {
        JsonNode incoming;
        try { incoming = JsonNode.Parse(json) ?? throw new JsonException("JSON 루트가 없습니다."); }
        catch (JsonException e) { throw new InvalidOperationException($"JSON 문법 오류: {e.Message}", e); }
        if (incoming is not JsonObject) throw new InvalidOperationException("최상위 값은 JSON 객체여야 합니다.");
        var original = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? throw new InvalidDataException("현재 설정 파일이 JSON 객체가 아닙니다.");
        RestoreSecrets(incoming, original);
        var serialized = incoming.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        _ = JsonDocument.Parse(serialized); // 마지막 문법 검증
        var backup = _path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
        File.Copy(_path, backup, false);
        var temp = _path + ".new";
        try
        {
            File.WriteAllText(temp, serialized + Environment.NewLine, new System.Text.UTF8Encoding(false));
            File.Move(temp, _path, true);
        }
        catch { try { File.Delete(temp); } catch { } throw; }
        log.LogWarning("appsettings.json was updated through the web UI; backup: {Backup}", backup);
    }

    public void RequestRestart()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("서비스 재시작은 Windows에서만 지원합니다.");
        Directory.CreateDirectory(_restartDir);
        var bat = Path.Combine(_restartDir, "restart-gateway.bat");
        var exe = Path.Combine(AppContext.BaseDirectory, "AgvFotaGateway.exe");
        var service = string.IsNullOrWhiteSpace(update.Value.ServiceName) ? "AgvFotaGateway" : update.Value.ServiceName.Replace("\"", "");
        string script;
        if (WindowsServiceHelpers.IsWindowsService())
        {
            script = $"""@echo off
timeout /t 2 /nobreak >nul
sc stop "{service}" >nul 2>&1
set /a N=0
:waitstop
sc query "{service}" | findstr /I "STOPPED" >nul && goto startsvc
set /a N+=1
if %N% GEQ 45 goto startsvc
timeout /t 1 /nobreak >nul
goto waitstop
:startsvc
sc start "{service}"
""";
        }
        else
        {
            script = $"""@echo off
timeout /t 2 /nobreak >nul
start "" "{exe}"
""";
        }
        File.WriteAllText(bat, script, System.Text.Encoding.ASCII);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c start \"\" /min \"{bat}\"")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _restartDir });
        _ = Task.Delay(1000).ContinueWith(_ => lifetime.StopApplication());
    }

    static bool IsSecret(string key) => key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Equals("AuthValue", StringComparison.OrdinalIgnoreCase)
        || key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase);

    static void MaskSecrets(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(x => x.Key).ToArray())
                if (IsSecret(key) && obj[key] is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    obj[key] = Mask;
                else MaskSecrets(obj[key]);
        else if (node is JsonArray arr)
            foreach (var child in arr) MaskSecrets(child);
    }

    static void RestoreSecrets(JsonNode incoming, JsonObject original)
    {
        Restore(incoming, original);
        static void Restore(JsonNode? node, JsonNode? old)
        {
            if (node is JsonObject obj && old is JsonObject oldObj)
            {
                foreach (var key in obj.Select(x => x.Key).ToArray())
                {
                    if (IsSecret(key) && obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s == Mask)
                    {
                        if (oldObj[key] is not null) obj[key] = oldObj[key]!.DeepClone();
                        else obj[key] = "";
                    }
                    else Restore(obj[key], oldObj[key]);
                }
            }
            else if (node is JsonArray arr && old is JsonArray oldArr)
                for (var i = 0; i < Math.Min(arr.Count, oldArr.Count); i++) Restore(arr[i], oldArr[i]);
        }
    }
}
