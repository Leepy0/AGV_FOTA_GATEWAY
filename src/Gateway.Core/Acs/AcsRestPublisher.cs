using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Gateway.Core.Acs;

/// <summary>ACS REST 접속 설정 (서버 appsettings "AcsApi" 섹션과 1:1)</summary>
public sealed record AcsRestSettings(
    string BaseUrl,                 // 예: http://acs.local:8000
    string Path,                    // 예: /api/agv/positions   ({agv} 를 넣으면 호기별 전송 때 치환)
    string Method = "POST",         // POST | PUT
    string AuthType = "None",       // None | Bearer | ApiKey | Basic
    string? AuthValue = null,       // Bearer 토큰, ApiKey 값, Basic 은 "user:pass"
    string ApiKeyHeader = "X-API-Key",
    int TimeoutMs = 5000);

public sealed record PublishResult(bool Ok, int StatusCode, string Body, TimeSpan Elapsed, string? Error = null)
{
    public override string ToString() => Ok ? $"HTTP {StatusCode} ({Elapsed.TotalMilliseconds:F0}ms)" : $"실패 {(StatusCode > 0 ? $"HTTP {StatusCode} " : "")}{Error ?? Body}";
}

/// <summary>JSON 본문을 ACS REST 로 보낸다. 인증·메서드·타임아웃만 다루고 본문 형식은 AcsPositionMapper 가 정한다.</summary>
public sealed class AcsRestPublisher
{
    readonly HttpClient _http;
    public AcsRestPublisher(HttpClient http) => _http = http;

    public async Task<PublishResult> PublishAsync(AcsRestSettings s, string json, int? agv = null, CancellationToken ct = default)
    {
        var path = s.Path.Replace("{agv}", agv?.ToString() ?? "");
        var url = s.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        using var req = new HttpRequestMessage(s.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Put : HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        switch (s.AuthType.ToLowerInvariant())
        {
            case "bearer": req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AuthValue); break;
            case "apikey": req.Headers.TryAddWithoutValidation(s.ApiKeyHeader, s.AuthValue); break;
            case "basic":  req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(s.AuthValue ?? ""))); break;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(s.TimeoutMs);
            using var res = await _http.SendAsync(req, cts.Token);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Length > 300) body = body[..300] + "…";
            return new PublishResult(res.IsSuccessStatusCode, (int)res.StatusCode, body, sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new PublishResult(false, 0, "", sw.Elapsed, $"시간 초과 ({s.TimeoutMs}ms)"); }
        catch (HttpRequestException e) { return new PublishResult(false, (int?)e.StatusCode ?? 0, "", sw.Elapsed, e.Message); }
    }
}
