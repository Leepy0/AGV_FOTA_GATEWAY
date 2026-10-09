using System.Text.Json;
using System.Text.Json.Serialization;
using Gateway.Core.Positions;

namespace Gateway.Core.Acs;

// ══════════════════════════════════════════════════════════════════════════════════════
//  ACS 위치 보고 — 스키마 반영 지점
//  ACS 가 받는 REST 형식(필드 이름·단위·좌표계·시각 형식·묶음/개별)이 정해지면 이 파일만 고친다.
//  아래는 임시 형식(제안)이며, AcsPositionMapper.Build 가 유일한 변환 지점이다.
// ══════════════════════════════════════════════════════════════════════════════════════

/// <summary>묶음 보고 본문 (임시). ACS 스키마 확정 후 교체</summary>
public sealed class AcsPositionPayload
{
    public string Source { get; set; } = "AgvFotaGateway";
    public DateTimeOffset SentAt { get; set; }
    public List<AcsPositionItem> Positions { get; set; } = new();
}

/// <summary>호기 한 대 (임시)</summary>
public sealed class AcsPositionItem
{
    public int AgvId { get; set; }
    public string? Map { get; set; }
    public double? X { get; set; }              // m
    public double? Y { get; set; }              // m
    public double? Heading { get; set; }        // 도
    public double? Speed { get; set; }          // m/s
    public string State { get; set; } = PositionState.Unknown;
    public bool Rotating { get; set; }
    public string? LastTag { get; set; }
    public DateTimeOffset? LastTagAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Online { get; set; }
}

public static class AcsPositionMapper
{
    /// <summary>직렬화 옵션 — ACS 가 snake_case 나 PascalCase 를 쓰면 여기서 바꾼다</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 한글 맵 이름을 \uXXXX 로 바꾸지 않음 (UTF-8 본문)
        WriteIndented = false,
    };

    public static AcsPositionPayload Build(IEnumerable<AgvPosition> positions, DateTimeOffset now) => new()
    {
        SentAt = now,
        Positions = positions.OrderBy(p => p.Agv).Select(Map).ToList(),
    };

    public static AcsPositionItem Map(AgvPosition p) => new()
    {
        AgvId = p.Agv, Map = p.Map, X = p.X, Y = p.Y, Heading = p.Heading, Speed = p.Speed,
        State = p.State, Rotating = p.Rotating, LastTag = p.LastTag, LastTagAt = p.LastTagAt,
        UpdatedAt = p.At, Online = p.Online,
    };

    public static string Serialize(AcsPositionPayload payload) => JsonSerializer.Serialize(payload, Json);
    /// <summary>호기별 개별 전송 모드용</summary>
    public static string Serialize(AcsPositionItem item) => JsonSerializer.Serialize(item, Json);
}
