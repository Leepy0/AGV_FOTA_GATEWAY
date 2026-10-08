using System.Threading.Channels;

namespace Gateway.Core.Fota;

/// <summary>
/// 브리지를 통해 호기와 통신하는 경로. 서버의 UDP 서비스가 구현한다.
/// OTA 클라이언트는 이 인터페이스만 보므로 테스트에서는 가짜 링크(시뮬레이터)로 바꿀 수 있다.
/// </summary>
public interface IAgvLink
{
    /// <summary>MAIN_DOWN 으로 UART 프레임 송신. 호기를 모르면(heartbeat 없음) false</summary>
    bool SendMainDown(int agv, ReadOnlySpan<byte> frame);
    bool SendOtaStart(int agv, string url);
    bool SendPing(int agv);

    /// <summary>호기의 MAIN_UP 프레임·OTA_RESULT 를 받는 구독. Dispose 로 해제</summary>
    AgvSubscription Subscribe(int agv);

    /// <summary>호기 쪽으로 나가는 이 서버의 IP (OTA URL 용). 모르면 null</summary>
    string? LocalAddressToward(int agv);
}

public sealed class AgvSubscription : IDisposable
{
    readonly Action _onDispose;
    public Channel<byte[]> Frames { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    public Channel<string> OtaResults { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public AgvSubscription(Action onDispose) => _onDispose = onDispose;

    public void Dispose()
    {
        Frames.Writer.TryComplete();
        OtaResults.Writer.TryComplete();
        _onDispose();
    }
}
