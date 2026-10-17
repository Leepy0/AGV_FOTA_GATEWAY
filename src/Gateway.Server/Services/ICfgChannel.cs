namespace Gateway.Server.Services;

/// <summary>설정 명령 응답. Text = 브리지가 돌려준 글(끝 표시 ">" 줄은 뗌), Error = 보내지 못했거나 시간 안에 응답이 없을 때의 사유</summary>
public readonly record struct CfgReply(string? Text, string? Error)
{
    public bool Ok => Error is null && Text is not null;
}

/// <summary>
/// 호기에 설정 명령(CFG_CMD)을 보내고 응답(CFG_RESULT)을 기다리는 길. 원격 진단·설정 수집·일괄 변경이 쓴다.
/// BridgeUdpService 가 구현하고 시험에서는 가짜로 바꾼다. 응답은 요청 seq 로 짝짓는다.
/// </summary>
public interface ICfgChannel
{
    Task<CfgReply> RequestCfgAsync(int agv, string cmd, TimeSpan timeout, CancellationToken ct = default);
}
