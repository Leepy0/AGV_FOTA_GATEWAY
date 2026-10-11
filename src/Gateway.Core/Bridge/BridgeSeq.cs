namespace Gateway.Core.Bridge;

/// <summary>
/// 브리지 상향 패킷의 타입별 seq 연속성 판정. 브리지는 타입마다 따로 세고(net_bridge.c s_seq_*), 오프라인이어도 번호는 올린다.
/// PONG·CFG_RESULT 는 서버 요청의 seq 를 그대로 반송하므로 검사하지 않는다.
/// </summary>
public static class BridgeSeq
{
    /// <summary>뒤로 간 번호 중 이 간격 이하만 65535→0 넘김으로 본다. 그보다 크면 재부팅(0 부터 다시)으로 보고 재동기</summary>
    public const int MaxWrapGap = 1000;

    public static bool Tracked(byte type) => type is not (BridgePacket.Pong or BridgePacket.CfgResult);

    /// <summary>직전 번호 last 다음에 seq 가 왔을 때의 누락 수</summary>
    public static int Loss(ushort last, ushort seq)
    {
        var diff = (ushort)(seq - (ushort)(last + 1));
        if (seq > last) return diff;                    // 앞으로 감: 사이 번호가 빠짐 (오프라인 구간 포함)
        if (seq == last) return 0;                      // 중복
        return diff <= MaxWrapGap ? diff : 0;           // 뒤로 감: 넘김이면 누락, 아니면 재부팅·재동기
    }
}
