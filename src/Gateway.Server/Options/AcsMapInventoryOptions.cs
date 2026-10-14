namespace Gateway.Server.Options;

public sealed class AcsMapInventoryOptions
{
    public const string Section = "AcsMapInventory";
    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";
    public string SubareasPath { get; set; } = "/v1/map/subareas";
    public string AgvInfoPath { get; set; } = "/v1/map/agv-info";
    public string AuthType { get; set; } = "None";
    public string? AuthValue { get; set; }
    public string ApiKeyHeader { get; set; } = "X-API-Key";
    public int TimeoutMs { get; set; } = 10000;
    public int IntervalSec { get; set; } = 60;
}