using System.Text;

namespace Gateway.Core.Agv;

/// <summary>
/// 메인보드(DSP) UART 프레임: STX + 종류 PAN MSG AGV(2) + 본문 + ' ' + BCC + ETX. ACS 규격과 DSP OTA(F) 가 공통으로 쓴다.
/// BCC = STX·ETX 를 제외한 바이트 합의 하위 4비트를 ASCII-HEX 한 글자로.
/// </summary>
public static class AgvFrame
{
    public const byte Stx = 0x02, Etx = 0x03;
    public const int BridgeFrameMax = 256;      // AGV_Wifi_Bridge uart_bridge.h FRAME_MAX — 넘으면 브리지가 폐기

    public static byte Bcc(ReadOnlySpan<byte> payload)
    {
        int sum = 0;
        foreach (var b in payload) sum += b;
        int n = sum & 0x0F;
        return (byte)(n < 10 ? '0' + n : 'A' + n - 10);
    }

    /// <summary>CRC-16/CCITT-FALSE (DSP OTA 블록 검사용)</summary>
    public static ushort Crc16(ReadOnlySpan<byte> d)
    {
        ushort crc = 0xFFFF;
        foreach (var b in d)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    public static byte[] Build(char type, char pan, char msg, int agv, ReadOnlySpan<byte> body)
    {
        var p = new byte[5 + body.Length + 1];
        p[0] = (byte)type; p[1] = (byte)pan; p[2] = (byte)msg; p[3] = (byte)('0' + agv / 10); p[4] = (byte)('0' + agv % 10);
        body.CopyTo(p.AsSpan(5));
        p[^1] = (byte)' ';
        var f = new byte[p.Length + 3];
        f[0] = Stx;
        p.CopyTo(f, 1);
        f[^2] = Bcc(p);
        f[^1] = Etx;
        return f;
    }

    /// <summary>프레임 검증·분해. 끝 공백은 본문에서 제외. BCC 는 대소문자 무관.</summary>
    public static bool TryParse(ReadOnlySpan<byte> f, out ParsedFrame r)
    {
        r = default;
        if (f.Length < 8 || f[0] != Stx || f[^1] != Etx) return false;
        if (char.ToUpperInvariant((char)f[^2]) != (char)Bcc(f.Slice(1, f.Length - 3))) return false;
        var b = f.Slice(1, f.Length - 3);
        if (b[^1] == ' ') b = b[..^1];
        if (b.Length < 5 || !char.IsAsciiDigit((char)b[3]) || !char.IsAsciiDigit((char)b[4])) return false;
        r = new ParsedFrame((char)b[0], (char)b[1], (char)b[2], (b[3] - '0') * 10 + (b[4] - '0'), b[5..].ToArray());
        return true;
    }

    /// <summary>프레임에서 AGV ID 만 빠르게 (검증 없이). 실패 시 -1</summary>
    public static int PeekAgv(ReadOnlySpan<byte> f)
    {
        if (f.Length < 6 || f[0] != Stx || !char.IsAsciiDigit((char)f[4]) || !char.IsAsciiDigit((char)f[5])) return -1;
        return (f[4] - '0') * 10 + (f[5] - '0');
    }

    /// <summary>DSP OTA 응답(A…F…) 인지 — ACS 로 넘기지 않는다</summary>
    public static bool IsOtaReply(ReadOnlySpan<byte> f) => f.Length >= 8 && f[0] == Stx && f[1] == 'A' && f[6] == 'F';

    /// <summary>버퍼를 ETX 기준으로 프레임들로 나눈다. 남은 조각은 rest 로 돌려준다.</summary>
    public static List<byte[]> Split(ReadOnlySpan<byte> data, ref byte[] rest)
    {
        var buf = rest.Length == 0 ? data.ToArray() : [.. rest, .. data];
        var frames = new List<byte[]>();
        int i;
        while ((i = Array.IndexOf(buf, Etx)) >= 0)
        {
            var fr = buf[..(i + 1)];
            buf = buf[(i + 1)..];
            int s = Array.LastIndexOf(fr, Stx);
            if (s >= 0) frames.Add(fr[s..]);
        }
        rest = buf.Length < 512 ? buf : [];
        return frames;
    }

    /// <summary>표시용: 제어문자는 &lt;STX&gt; / \xHH</summary>
    public static string Fmt(ReadOnlySpan<byte> d)
    {
        var sb = new StringBuilder();
        foreach (var c in d)
            sb.Append(c switch
            {
                Stx => "<STX>", Etx => "<ETX>", 0x0D => "<CR>", 0x0A => "<LF>",
                >= 0x20 and < 0x7F => ((char)c).ToString(), _ => $"\\x{c:X2}"
            });
        return sb.ToString();
    }

    /// <summary>&lt;STX&gt; &lt;ETX&gt; &lt;CR&gt; &lt;LF&gt; \xHH 표기 → 바이트</summary>
    public static byte[] ParseData(string s)
    {
        var o = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '<')
            {
                int j = s.IndexOf('>', i);
                byte? v = j < 0 ? null : s[(i + 1)..j].ToUpperInvariant() switch { "STX" => Stx, "ETX" => Etx, "CR" => 0x0D, "LF" => 0x0A, _ => null };
                if (v is byte t) { o.Add(t); i = j; continue; }
            }
            if (s[i] == '\\' && i + 3 < s.Length && s[i + 1] == 'x' &&
                byte.TryParse(s.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var hx))
            { o.Add(hx); i += 3; continue; }
            o.Add((byte)s[i]);
        }
        return [.. o];
    }
}

public readonly record struct ParsedFrame(char Type, char Pan, char Msg, int Agv, byte[] Body);
