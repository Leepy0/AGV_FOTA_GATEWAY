using System.Text.RegularExpressions;

namespace Gateway.Core.Fota;

/// <summary>
/// 메인보드 앱 버전 규칙. 앱의 버전 응답(R…) 칸이 8자 고정이라 브리지는 앞 8자만 알 수 있다.
/// 패커·업로드는 IsValid 로 막고, 서버의 모든 비교는 Matches 로 한다(기존에 올라온 긴 버전도 앞 8자로 맞춰 봄).
/// </summary>
public static partial class DspVersion
{
    public const int MaxLength = 8;

    [GeneratedRegex(@"^[0-9A-Za-z._-]{1,8}$")]
    private static partial Regex Rule();

    /// <summary>1~8자, 영문·숫자·. _ - 만 (JSON·프레임에 그대로 들어가도 안전)</summary>
    public static bool IsValid(string? v) => v is not null && Rule().IsMatch(v);

    /// <summary>앱이 보고한 버전(최대 8자)과 이미지 버전이 같은지 — 둘 다 앞 8자로 비교</summary>
    public static bool Matches(string? reported, string? expected)
    {
        if (string.IsNullOrEmpty(reported) || string.IsNullOrEmpty(expected)) return false;
        return Head(reported) == Head(expected);
    }

    static string Head(string v) { v = v.Trim(); return v.Length > MaxLength ? v[..MaxLength] : v; }
}
