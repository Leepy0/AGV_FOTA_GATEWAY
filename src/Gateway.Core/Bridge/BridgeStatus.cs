using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Core.Fota;

namespace Gateway.Core.Bridge;

/// <summary>
/// 브리지 heartbeat(STATUS 0x10) 또는 INFO(0x12) JSON. 모르는 필드는 무시하고 Raw 에 원문을 둔다.
/// 메인보드 필드(브리지 0.5.0+): dsp(앱 버전), dspb(부트로더 버전), dsp_build, dsp_st(idle/downloading/staged/programming),
/// staged {job, ver[, pkg]} 또는 null.
/// 패키지 보관(브리지 0.9.0+): pkg_st("ok"/"nopart", 구 브리지는 ""), dsp_pkg {id, ver}(메인보드에 적용된 패키지) 또는 null, staged.pkg(스테이징과 짝인 패키지 ID).
/// </summary>
public sealed record BridgeStatus(
    string Fw, string Part, string Ota, long UpSeconds, int Rssi, int Channel, string Bssid,
    long Disconnects, long MainRx, long MainTx, long RfidRx, long UartErrors, long NetOffline, long NetErrors,
    long Heap, string Cfg, bool SetupAp,
    string Dsp, string DspBoot, string DspBuild, string DspState, long? StagedJob, string? StagedVer,
    string Mac, string Ip, string Raw,
    long TsMs = 0, long TsyncAgeSec = 0, bool HasClock = false, int Reason = 0,
    string PkgState = "", string? DspPkgId = null, string? DspPkgVer = null, string? StagedPkg = null)
{
    /// <summary>패키지 보관을 아는 브리지(0.9.0+)인지 — pkg_st 가 있다</summary>
    public bool HasPkgArchive => PkgState.Length > 0;
    /// <summary>브리지에 패키지 파티션(dsppkg)이 없음 — USB 로 파티션 표를 다시 구워야 함</summary>
    public bool PkgNoPart => PkgState == "nopart";
    /// <summary>브리지가 보관한 패키지(메인보드에 적용된 것)가 있고 그 버전이 실행 중인 메인보드 앱 버전과 같음 — .out·.map 이 브리지에 있다</summary>
    public bool HasArchivedOriginals => DspPkgId is { Length: > 0 } && HasDsp && !string.IsNullOrEmpty(DspPkgVer) && DspVersion.Matches(Dsp, DspPkgVer);

    /// <summary>시각 동기를 아는 브리지(0.8.0+)이고 시각을 서버에서 받아 맞춘 상태 — heartbeat 의 ts(UTC unix ms)가 믿을 만함</summary>
    public bool ClockSynced => HasClock && TsMs >= BridgeClock.MinPlausibleUnixMs;

    public static BridgeStatus? Parse(ReadOnlySpan<byte> json)
    {
        var raw = System.Text.Encoding.UTF8.GetString(json);
        try
        {
            using var d = JsonDocument.Parse(raw);
            var r = d.RootElement;
            long? stagedJob = null; string? stagedVer = null, stagedPkg = null;
            if (r.TryGetProperty("staged", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                stagedJob = Num(st, "job");
                stagedVer = Str(st, "ver");
                if (Str(st, "pkg") is { Length: > 0 } sp) stagedPkg = sp;
            }
            string? pkgId = null, pkgVer = null;
            if (r.TryGetProperty("dsp_pkg", out var dp) && dp.ValueKind == JsonValueKind.Object)
            {
                pkgId = Str(dp, "id");
                pkgVer = Str(dp, "ver");
                if (pkgId.Length == 0) pkgId = null;
            }
            return new BridgeStatus(
                Str(r, "fw"), Str(r, "part"), Str(r, "ota"), Num(r, "up"), (int)Num(r, "rssi"), (int)Num(r, "ch"), Str(r, "bssid"),
                Num(r, "disc"), Num(r, "m_rx"), Num(r, "m_tx"), Num(r, "r_rx"), Num(r, "u_err"), Num(r, "n_off"), Num(r, "n_err"),
                Num(r, "heap"), Str(r, "cfg"), Num(r, "ap") != 0 || Num(r, "setup") != 0,
                Str(r, "dsp"), Str(r, "dspb"), Str(r, "dsp_build"), Str(r, "dsp_st"), stagedJob, stagedVer,
                Str(r, "mac"), Str(r, "ip"), raw,
                Num(r, "ts"), Num(r, "tsync"), r.TryGetProperty("tsync", out _), (int)Num(r, "reason"),
                Str(r, "pkg_st"), pkgId, pkgId is null ? null : pkgVer, stagedPkg);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 새로 받은 heartbeat/INFO 를 직전 상태와 합친다 — 서로 없는 필드를 지우지 않게.
    /// INFO 는 무선·카운터 필드가 없으므로 직전 상태에 INFO 의 키만 덮어쓴다.
    /// heartbeat 는 전체를 새 값으로 하되, heartbeat 에 없는 mac·ip(INFO 값)는 직전 값을 유지한다.
    /// 직전 상태가 없거나(다른 장치로 바뀐 경우 포함) JSON 객체가 아니면 새 값 그대로.
    /// </summary>
    public static BridgeStatus Merge(BridgeStatus fresh, BridgeStatus? old, bool info)
    {
        if (old is null) return fresh;
        try
        {
            if (JsonNode.Parse(fresh.Raw) is not JsonObject n || JsonNode.Parse(old.Raw) is not JsonObject o) return fresh;
            JsonObject merged;
            if (info)
            {
                merged = o;
                foreach (var (k, v) in n) merged[k] = v?.DeepClone();
            }
            else
            {
                merged = n;
                foreach (var k in InfoOnlyKeys)
                    if (!n.ContainsKey(k) && o.TryGetPropertyValue(k, out var v)) merged[k] = v?.DeepClone();
            }
            return Parse(System.Text.Encoding.UTF8.GetBytes(merged.ToJsonString())) ?? fresh;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return fresh;   // 중복 키 등 — 합치지 않는다
        }
    }

    static readonly string[] InfoOnlyKeys = ["mac", "ip"];

    /// <summary>서버 하향 패킷으로 확정해야 하는 상태 — 원격 설정 시험 적용(cfg=trial) 또는 브리지 OTA 미확정(ota=pending). 서버가 PING 을 자동으로 보낸다</summary>
    public bool NeedsConfirmPing => Cfg == "trial" || Ota == "pending";

    /// <summary>메인보드 앱 버전을 아는 브리지인지 (0.5.0 미만·응답 없는 구 앱은 "" 또는 "?")</summary>
    public bool HasDsp => !string.IsNullOrEmpty(Dsp) && Dsp != "?";

    static string Str(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static long Num(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
