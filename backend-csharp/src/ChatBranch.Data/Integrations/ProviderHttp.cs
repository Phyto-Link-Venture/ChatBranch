using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatBranch.Data.Integrations;

/// <summary>
/// One provider HTTP call, shared by the flow integration actions and the
/// Google Calendar client. Never throws, never returns a token or a whole
/// provider body: a failure comes back as <c>"HTTP 4xx &lt;provider message,
/// capped&gt;"</c> or the exception's type name.
/// </summary>
public static class ProviderHttp
{
    public sealed record Response(bool Ok, string Body, int Status);

    public static async Task<Response> SendAsync(IHttpClientFactory http, HttpMethod method, string url,
        string token, Dictionary<string, object?>? jsonBody = null, Dictionary<string, string>? formBody = null,
        string? rawBody = null, string? rawContentType = null,
        Dictionary<string, string>? headers = null, CancellationToken ct = default, byte[]? bytes = null)
    {
        try
        {
            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var req = new HttpRequestMessage(method, url);
            if (token.Length > 0)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            foreach (var kv in headers ?? new()) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            if (jsonBody != null)
                req.Content = new StringContent(JsonSerializer.Serialize(jsonBody), Encoding.UTF8, "application/json");
            else if (formBody != null)
                req.Content = new FormUrlEncodedContent(formBody);
            else if (rawBody != null)
                req.Content = new StringContent(rawBody, Encoding.UTF8, rawContentType ?? "text/plain");
            else if (bytes != null)
            {
                req.Content = new ByteArrayContent(bytes);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(rawContentType ?? "application/octet-stream");
            }

            using var resp = await client.SendAsync(req, ct);
            var text = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return new Response(true, text, (int)resp.StatusCode);
            // surface the provider's own message, capped — never the whole body
            return new Response(false, $"HTTP {(int)resp.StatusCode} {Trim(ProviderMessage(text), 200)}".TrimEnd(),
                (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            return new Response(false, e.GetType().Name, 0);
        }
    }

    /// <summary>The human-readable bit of a provider error body.</summary>
    public static string ProviderMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "";
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                    return m.GetString() ?? "";
            }
        }
        catch { /* not JSON */ }
        return body;
    }

    public static string JsonField(string body, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v)
                ? v.ToString() : "";
        }
        catch { return ""; }
    }

    private static string Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}

/// <summary>A bare <see cref="IHttpClientFactory"/> for services built by hand
/// (outside DI) — one plain client per call.</summary>
public sealed class PlainHttpClientFactory : IHttpClientFactory
{
    public static readonly PlainHttpClientFactory Instance = new();
    public HttpClient CreateClient(string name) => new();
}
