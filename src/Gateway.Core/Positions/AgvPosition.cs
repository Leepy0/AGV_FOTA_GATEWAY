namespace Gateway.Core.Positions;

/// <summary>위치 추정 상태 (docs/RTLS.md 4절)</summary>
public static class PositionState
{
    public const string Tracked = "TRACKED";    // 태그 2개 이상 연속 인식, 오차 범위 안
    public const string Degraded = "DEGRADED";  // 마지막 태그 이후 거리 초과 / 방향 미확정
    public const string Lost = "LOST";          // heartbeat 끊김, 재부팅, 라인 이탈·JOG
    public const string Unknown = "UNKNOWN";    // 아직 태그를 한 번도 못 읽음
}

/// <summary>
/// 호기 한 대의 최신 추정 위치. RTLS 추정기가 채우고(아직 구현 전), ACS 발행과 /api/positions 가 읽는다.
/// 좌표는 맵 기준 m, 방향은 도(0~360, x축 기준 반시계), 속도는 m/s. 모르는 값은 null.
/// </summary>
public sealed record AgvPosition(
    int Agv,
    DateTimeOffset At,              // 이 추정이 만들어진 시각 (서버 시계)
    double? X, double? Y,
    double? Heading,
    string State,
    double? Speed = null,
    bool Rotating = false,          // 제자리 회전 중 (Q 이벤트 시작~끝)
    string? LastTag = null,         // 마지막으로 읽은 태그 코드 4자
    DateTimeOffset? LastTagAt = null,
    string? Map = null,             // 맵(라인) 이름
    bool Online = true,             // 브리지 heartbeat 기준
    string? Note = null);           // 예: "injected" (테스트 주입), "rtls"
