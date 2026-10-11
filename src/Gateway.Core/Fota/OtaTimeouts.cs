namespace Gateway.Core.Fota;

/// <summary>
/// OTA 클라이언트의 대기·재전송 시간. 기본값이 운영값이고, 시험에서는 짧게 줄여 실제 시간 초과 경로를 지나게 한다.
/// </summary>
public sealed record OtaTimeouts
{
    public static readonly OtaTimeouts Default = new();

    /// <summary>메인보드 스테이징: 보고·heartbeat 진행 표시가 이만큼 끊기면 실패 (다운로드 전체)</summary>
    public TimeSpan DspStageIdle { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>메인보드 적용: 단계 보고가 이만큼 끊기면 실패 (erase 1~2초×섹터, 쓰기 수십 초, 복구 시 wait_power 10초 간격)</summary>
    public TimeSpan DspApplyIdle { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>브리지 OTA: OTA_RESULT done 까지 전체</summary>
    public TimeSpan EspResult { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>브리지 OTA: done 뒤 재부팅한 새 펌웨어의 heartbeat 까지</summary>
    public TimeSpan EspReboot { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>브리지 OTA: pending → valid 확정까지 (PING 반복). 브리지는 부팅 180초 안에 확정 못 하면 롤백</summary>
    public TimeSpan EspConfirm { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>확정 PING 간격</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>명령(START/APPLY/OTA_START) 뒤 이만큼 수신 흔적(보고·heartbeat)이 없으면 다시 보낸다</summary>
    public TimeSpan Resend { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>재전송 최대 횟수 (처음 송신 제외)</summary>
    public int MaxResends { get; init; } = 3;
    /// <summary>heartbeat 확인 주기</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(500);
}
