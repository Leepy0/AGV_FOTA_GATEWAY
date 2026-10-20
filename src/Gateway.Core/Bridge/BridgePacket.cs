using System.Buffers.Binary;
using System.Text;

namespace Gateway.Core.Bridge;

/// <summary>
/// ESP32 Wi-Fi 브리지 ↔ 서버 UDP 패킷 규격 (AGV_Wifi_Bridge/main/proto.h 와 동일하게 유지).
/// [magic A5 5A][ver 1][type][agv][flags][seq u16][len u16][payload] — 리틀 엔디언, 헤더 10바이트.
/// </summary>
public static class BridgePacket
{
    public const int HeaderLength = 10;
    public const int PayloadMax = 1024;    // 브리지 0.6.0+ (이전 512). 0.5.x 브리지는 512 를 넘는 패킷을 버린다
    public const byte Magic0 = 0xA5, Magic1 = 0x5A, Version = 1;

    public const byte MainUp = 0x01;     // ESP → 서버: 메인보드 UART 원본
    public const byte MainDown = 0x02;   // 서버 → ESP: 메인보드로 보낼 UART 원본
    public const byte Rfid = 0x03;       // ESP → rfid_dst
    public const byte Status = 0x10;     // heartbeat JSON
    public const byte InfoReq = 0x11;    // 서버 → ESP: 장치 정보 요청
    public const byte Info = 0x12;       // ESP → 서버: JSON (agv, mac, ip, fw, part, ota, cfg, dsp, dspb, staged …)
    public const byte Ping = 0x20;
    public const byte Pong = 0x21;
    public const byte TimeReq = 0x22;    // ESP → 서버: 시각 요청 {"t0": 브리지 가동 ms} (브리지 0.8.0+)
    public const byte Time = 0x23;       // 서버 → ESP: 서버 UTC 시각 {"t0","t1","t2"} (요청 응답) 또는 {"t2"} (서버가 먼저 보냄)
    public const byte OtaStart = 0x30;   // 서버 → ESP: 펌웨어 URL
    public const byte OtaResult = 0x31;  // ESP → 서버: JSON stage
    public const byte DspOtaStart = 0x32;  // 서버 → ESP: JSON {url, ver, crc32, size, job, apply} — 메인보드 이미지 스테이징
    public const byte DspOtaResult = 0x33; // ESP → 서버: JSON {job, stage, pct, ver, err}
    public const byte DspOtaApply = 0x34;  // 서버 → ESP: JSON {job} — 스테이징된 이미지를 메인보드에 적용
    public const byte CfgCmd = 0x40;
    public const byte CfgResult = 0x41;

    public static byte[] Build(byte type, int agv, ushort seq, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > PayloadMax) throw new ArgumentException($"payload {payload.Length} > {PayloadMax}");
        var b = new byte[HeaderLength + payload.Length];
        b[0] = Magic0; b[1] = Magic1; b[2] = Version; b[3] = type; b[4] = (byte)agv; b[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), seq);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), (ushort)payload.Length);
        payload.CopyTo(b.AsSpan(HeaderLength));
        return b;
    }

    public static bool TryParse(ReadOnlySpan<byte> b, out BridgeHeader header, out ReadOnlySpan<byte> payload)
    {
        header = default; payload = default;
        if (b.Length < HeaderLength || b[0] != Magic0 || b[1] != Magic1 || b[2] != Version) return false;
        int len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(8));
        if (len > PayloadMax || len != b.Length - HeaderLength) return false;
        header = new BridgeHeader(b[3], b[4], b[5], BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(6)), (ushort)len);
        payload = b.Slice(HeaderLength);
        return true;
    }

    /// <summary>CFG_RESULT 본문을 표시용 텍스트로. 브리지는 응답 끝에 ">" 한 줄(끝 표시)을 붙이므로 떼어 낸다.</summary>
    public static string CfgResultText(ReadOnlySpan<byte> payload)
    {
        var s = Encoding.UTF8.GetString(payload).TrimEnd('\n');
        if (s == ">") return "";
        if (s.EndsWith("\n>", StringComparison.Ordinal)) return s[..^2];
        // 브리지 0.8.0 의 한 줄 JSON(cfg json·stat json)은 개행 없이 "}>" 로 끝난다
        return s.EndsWith("}>", StringComparison.Ordinal) || s.EndsWith("]>", StringComparison.Ordinal) ? s[..^1] : s;
    }

    public static string TypeName(byte type) => type switch
    {
        MainUp => "MAIN_UP", MainDown => "MAIN_DOWN", Rfid => "RFID", Status => "STATUS", Ping => "PING", Pong => "PONG", TimeReq => "TIME_REQ", Time => "TIME",
        InfoReq => "INFO_REQ", Info => "INFO",
        OtaStart => "OTA_START", OtaResult => "OTA_RESULT", CfgCmd => "CFG_CMD", CfgResult => "CFG_RESULT",
        DspOtaStart => "DSP_OTA_START", DspOtaResult => "DSP_OTA_RESULT", DspOtaApply => "DSP_OTA_APPLY",
        _ => $"0x{type:X2}"
    };
}

public readonly record struct BridgeHeader(byte Type, byte Agv, byte Flags, ushort Seq, ushort Length);
