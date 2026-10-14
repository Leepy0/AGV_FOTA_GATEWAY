namespace Gateway.Server.Options;

public sealed class AcsMapInventoryOptions
{
    public const string Section = "AcsMapInventory";
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";
    public string SubareasPath { get; set; } = "/v1/map/subareas";
    public string AgvInfoPath { get; set; } = "/v1/map/agv-info";
    // Tape API shown in ACS browser network capture; override site/area in deployment config as needed.
    public string TapePath { get; set; } = "/api/edge/acs/tape?site=KR03&area=A_FL1";
    public string TapeAgvIdField { get; set; } = "agvid";
    public string TapeStatusField { get; set; } = "status";
    public string TapeBatteryField { get; set; } = "battery";
    public Dictionary<string, string> StatusLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-1"] = "offline",
        ["0"] = "stop",
        ["1"] = "run",
        ["2"] = "err"
    };
    public string AuthType { get; set; } = "None";
    public string? AuthValue { get; set; }
    public string ApiKeyHeader { get; set; } = "X-API-Key";
    public int TimeoutMs { get; set; } = 10000;
    public int IntervalSec { get; set; } = 60;
}
