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

/// <summary>ACS REST 와 주고받기. 인증·메서드·타임아웃만 다루고 본문 형식은 AcsPositionMapper(보내기)·AcsInventoryMapper(받기)가 정한다.</summary>
public sealed class AcsRestPublisher
{
    readonly HttpClient _http;
    public AcsRestPublisher(HttpClient http) => _http = http;

    /// <summary>JSON 본문 보내기 (위치 보고). 응답 본문은 300자까지만 보관</summary>
    public async Task<PublishResult> PublishAsync(AcsRestSettings s, string json, int? agv = null, CancellationToken ct = default)
    {
        var path = s.Path.Replace("{agv}", agv?.ToString() ?? "");
        var url = s.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        using var req = new HttpRequestMessage(s.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Put : HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return await SendAsync(s, req, 300, ct);
    }

    /// <summary>GET 으로 받기 (호기 소속 목록). 응답 본문 전체를 돌려준다</summary>
    public async Task<PublishResult> GetAsync(AcsRestSettings s, CancellationToken ct = default)
    {
        var url = s.BaseUrl.TrimEnd('/') + "/" + s.Path.TrimStart('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await SendAsync(s, req, int.MaxValue, ct);
    }

    async Task<PublishResult> SendAsync(AcsRestSettings s, HttpRequestMessage req, int bodyLimit, CancellationToken ct)
    {
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
            if (body.Length > bodyLimit) body = body[..bodyLimit] + "…";
            return new PublishResult(res.IsSuccessStatusCode, (int)res.StatusCode, body, sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new PublishResult(false, 0, "", sw.Elapsed, $"시간 초과 ({s.TimeoutMs}ms)"); }
        catch (HttpRequestException e) { return new PublishResult(false, (int?)e.StatusCode ?? 0, "", sw.Elapsed, e.Message); }
    }
}
