using System.Text;
using Gateway.Core.Agv;

namespace Gateway.Core.Fota;

public sealed class DspOtaException(string message) : Exception(message);

/// <summary>DSP OTA 상태 (S 응답). 상태 문자: N 없음, P pending, T trial(미확정), V valid, X invalid, D 다운로드 중</summary>
public sealed record DspStatus(char Slot, char State, char Other, uint Seq, uint Size, string Version)
{
    public static string StateName(char c) => c switch
    {
        'N' => "none", 'P' => "pending", 'T' => "trial", 'V' => "valid", 'X' => "invalid", 'D' => "downloading", _ => "?"
    };
    public override string ToString() => $"slot={Slot} state={State}({StateName(State)}) other={Other} seq={Seq} size={Size} ver={Version}";
}

/// <summary>
/// DSP OTA 클라이언트 — DSP_OTA/docs/PROTOCOL.md 의 F 프레임을 보내고 A 응답을 기다린다.
/// 응답이 없으면 같은 MSG ID 로 재전송(ACS 와 같은 0.5초 × 9회).
/// </summary>
public sealed class DspOtaClient : IDisposable
{
    public const int DefaultBlock = 64, BlockMax = 112;
    const int Retry = 9, TimeoutMs = 500;

    readonly IAgvLink _link;
    readonly AgvSubscription _sub;
    readonly int _agv;
    int _msg;

    public int Retries { get; private set; }
    public Action<string>? Log { get; set; }

    public DspOtaClient(IAgvLink link, int agv)
    {
        _link = link; _agv = agv;
        _sub = link.Subscribe(agv);
    }

    public void Dispose() => _sub.Dispose();

    char NextMsg() { _msg = _msg % 15 + 1; return "123456789ABCDEF"[_msg - 1]; }

    public async Task<(bool ok, int code, byte[] extra)> RequestAsync(char cmd, byte[]? body, int timeoutMs, int retry, CancellationToken ct)
    {
        var msg = NextMsg();
        var fr = AgvFrame.Build('F', '1', msg, _agv, [(byte)cmd, .. body ?? []]);
        for (int attempt = 0; attempt <= retry; attempt++)
        {
            if (attempt > 0) Retries++;
            if (!_link.SendMainDown(_agv, fr)) throw new DspOtaException($"AGV {_agv:D2}: 브리지 endpoint 없음 (heartbeat 미수신)");
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                var rest = deadline - DateTime.UtcNow;
                if (rest <= TimeSpan.Zero) break;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(rest);
                byte[] r;
                try { r = await _sub.Frames.Reader.ReadAsync(cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                if (!AgvFrame.TryParse(r, out var p)) continue;
                var b = p.Body;
                if (p.Type != 'A' || p.Agv != _agv || b.Length < 6 || b[0] != 'F') continue;
                if ((char)b[1] != cmd || p.Msg != msg) continue;        // 지난 요청의 늦은 응답
                bool ok = b[2] == 'P' && b[3] == 'A';
                return (ok, Convert.ToInt32(Encoding.ASCII.GetString(b, 4, 2), 16), b[6..]);
            }
        }
        throw new DspOtaException($"{cmd}: 응답 없음 ({retry + 1}회)");
    }

    Task<(bool ok, int code, byte[] extra)> RequestAsync(char cmd, CancellationToken ct, byte[]? body = null) =>
        RequestAsync(cmd, body, TimeoutMs, Retry, ct);

    static string Err(char cmd, int code, byte[] x) => $"{cmd} NG {code:X2} {Encoding.ASCII.GetString(x)}";

    public async Task<DspStatus> StatusAsync(CancellationToken ct = default)
    {
        var (ok, code, x) = await RequestAsync('S', ct);
        if (!ok || x.Length < 35) throw new DspOtaException(Err('S', code, x));
        return new DspStatus((char)x[0], (char)x[1], (char)x[2], Convert.ToUInt32(Encoding.ASCII.GetString(x, 3, 8), 16),
                             Convert.ToUInt32(Encoding.ASCII.GetString(x, 11, 8), 16), Encoding.ASCII.GetString(x, 19, 16).Trim());
    }

    /// <summary>B → D… → E. 반환: 기록된 슬롯 문자</summary>
    public async Task<char> UpdateAsync(byte[] image, int block, Action<int, int>? progress, CancellationToken ct = default)
    {
        if (block % 16 != 0 || block < 16 || block > BlockMax) throw new DspOtaException("block 은 16~112 의 16 배수");
        var (ok, code, x) = await RequestAsync('B', Encoding.ASCII.GetBytes($"{image.Length:X8}{FirmwareImage.Crc32(image):X8}{block:X4}"), 15000, 1, ct);
        if (!ok) throw new DspOtaException(Err('B', code, x));
        char target = (char)x[0];
        int blocks = Convert.ToInt32(Encoding.ASCII.GetString(x, 1, 4), 16);
        for (int i = 0; i < blocks; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = image.AsSpan(i * block, Math.Min(block, image.Length - i * block)).ToArray();
            (ok, code, x) = await RequestAsync('D', ct, Encoding.ASCII.GetBytes($"{i:X4}{AgvFrame.Crc16(chunk):X4}{Convert.ToHexString(chunk)}"));
            if (!ok) throw new DspOtaException($"D#{i}: " + Err('D', code, x));
            progress?.Invoke(i + 1, blocks);
        }
        (ok, code, x) = await RequestAsync('E', null, 15000, 1, ct);
        if (!ok) throw new DspOtaException(Err('E', code, x));
        return target;
    }

    public async Task<bool> RebootAsync(CancellationToken ct = default) => (await RequestAsync('R', null, 2000, 2, ct)).ok;
    public async Task<char?> ConfirmAsync(CancellationToken ct = default) { var (ok, _, x) = await RequestAsync('C', ct); return ok && x.Length > 0 ? (char)x[0] : null; }
    public async Task<bool> AbortAsync(CancellationToken ct = default) => (await RequestAsync('X', ct)).ok;

    /// <summary>재부팅 뒤 S 에 응답할 때까지 기다린다</summary>
    public async Task<DspStatus> WaitBackAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var t0 = DateTime.UtcNow;
        await Task.Delay(1000, ct);
        while (DateTime.UtcNow - t0 < timeout)
        {
            try { return await StatusAsync(ct); }
            catch (DspOtaException) { await Task.Delay(500, ct); }
        }
        throw new DspOtaException("재부팅 후 응답 없음");
    }
}
