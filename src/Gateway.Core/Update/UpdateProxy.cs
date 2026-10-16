using System.Net;

namespace Gateway.Core.Update;

/// <summary>
/// 업데이트 HttpClient 의 프록시 (appsettings Update.Proxy · ProxyUser · ProxyPassword).
/// 콘솔(start.bat)은 로그인 사용자의 인터넷 옵션(프록시·PAC)과 Windows 로그인 자격을 쓰지만, 서비스(LocalSystem)는 SYSTEM 계정의
/// (보통 비어 있는) 설정과 컴퓨터 계정 자격을 쓴다. 그래서 콘솔로는 되는데 서비스로는 확인이 실패하는 사내망에서는 여기에 프록시를 적는다.
/// </summary>
public static class UpdateProxy
{
    /// <summary>Update.Proxy 가 이 값이면 시스템 설정을 무시하고 프록시 없이 직접 연결</summary>
    public const string None = "none";

    /// <summary>핸들러에 적용할 값과 화면·로그용 설명(비밀번호는 넣지 않음)</summary>
    public sealed record Setting(bool UseProxy, IWebProxy? Proxy, ICredentials? DefaultProxyCredentials, string Text);

    /// <summary>
    /// 빈 값 → 시스템 설정(서비스면 SYSTEM 계정 설정)과 현재 계정 자격.
    /// "none" → 프록시 없음. 그 밖에는 http(s)://호스트[:포트] 절대 주소 — 계정을 주면 그 계정(DOMAIN\user 가능), 없으면 현재 계정 자격.
    /// 주소가 잘못되면 <see cref="ArgumentException"/>
    /// </summary>
    public static Setting Resolve(string? address, string? user, string? password)
    {
        address = address?.Trim() ?? "";
        if (address.Length == 0) return new(true, null, CredentialCache.DefaultCredentials, "시스템 설정 (서비스는 SYSTEM 계정의 설정)");
        if (address.Equals(None, StringComparison.OrdinalIgnoreCase)) return new(false, null, null, "없음 (직접 연결)");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException($"Update.Proxy 가 올바르지 않음: '{address}' — http://호스트:포트 형식 (계정은 ProxyUser · ProxyPassword 에)");

        user = user?.Trim() ?? "";
        ICredentials credentials;
        string who;
        if (user.Length == 0) { credentials = CredentialCache.DefaultCredentials; who = "현재 계정 자격"; }
        else
        {
            int sep = user.IndexOf('\\');
            credentials = sep > 0 ? new NetworkCredential(user[(sep + 1)..], password ?? "", user[..sep]) : new NetworkCredential(user, password ?? "");
            who = $"계정 {user}";
        }
        var proxy = new WebProxy(uri) { BypassProxyOnLocal = true, Credentials = credentials };
        return new(true, proxy, null, $"{uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped)} ({who})");
    }

    /// <summary><see cref="Resolve"/> 결과를 핸들러에 적용하고 돌려준다</summary>
    public static Setting Apply(HttpClientHandler handler, string? address, string? user, string? password)
    {
        var s = Resolve(address, user, password);
        handler.UseProxy = s.UseProxy;
        handler.Proxy = s.Proxy;
        handler.DefaultProxyCredentials = s.DefaultProxyCredentials;
        return s;
    }
}
