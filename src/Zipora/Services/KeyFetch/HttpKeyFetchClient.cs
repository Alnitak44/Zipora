using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Zipora.Services.KeyFetch;

/// <summary>Client for KeyFetch's public API. The fingerprint identifies the
/// local quota bucket; no activation token is sent or stored.</summary>
public sealed class HttpKeyFetchClient : IKeyFetchClient
{
    private readonly HttpClient _http;

    public bool IsConfigured => true;

    public HttpKeyFetchClient(string baseUrl, string deviceFp)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException("KeyFetch server URL must be an absolute HTTP(S) URL.", nameof(baseUrl));
        }

        _http = new HttpClient
        {
            BaseAddress = new Uri(AppendSlash(baseUrl)),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Device-Fingerprint", deviceFp);
    }

    public async Task<FetchResult> FetchPasswordAsync(string md5, long size, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"api/fetch?md5={Uri.EscapeDataString(md5)}&size={size}", ct);
        var body = await ReadBodyAsync(response, ct);
        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.OK when TryGetCode(body, out var code) && code == 0 && TryGetPassword(body, out var password) && password != null
                => new FetchResult(FetchStatus.Ok, password, null),
            System.Net.HttpStatusCode.NotFound => new FetchResult(FetchStatus.NotFound, null, GetMsg(body, "未收录")),
            System.Net.HttpStatusCode.TooManyRequests => new FetchResult(FetchStatus.QuotaExceeded, null, GetMsg(body, "今日额度已用完")),
            _ => new FetchResult(FetchStatus.Error, null, GetMsg(body, $"服务器错误 ({response.StatusCode})")),
        };
    }

    public async Task<ContributeResult> ContributeAsync(string md5, long size, string password, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { md5, size, password });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("api/contribute", content, ct);
        var body = await ReadBodyAsync(response, ct);
        return response.StatusCode switch
        {
            System.Net.HttpStatusCode.OK => new ContributeResult(ContributeStatus.Ok, GetMsg(body, "上传成功，感谢贡献！")),
            System.Net.HttpStatusCode.TooManyRequests => new ContributeResult(ContributeStatus.QuotaExceeded, GetMsg(body, "今日额度已用完")),
            _ => new ContributeResult(ContributeStatus.Error, GetMsg(body, $"服务器错误 ({response.StatusCode})")),
        };
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(ct)); }
        catch { return ""; }
    }

    private static bool TryGetCode(string body, out int code)
    {
        code = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number)
            {
                code = value.GetInt32();
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TryGetPassword(string body, out string? password)
    {
        password = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("password", out var value) && value.ValueKind == JsonValueKind.String)
            {
                password = value.GetString();
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string GetMsg(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("msg", out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? fallback;
        }
        catch { }
        return fallback;
    }

    private static string AppendSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
