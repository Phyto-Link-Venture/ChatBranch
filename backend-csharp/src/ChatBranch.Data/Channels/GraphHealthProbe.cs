using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>One Graph liveness answer: HTTP status (0 = no response), Meta's error code and message.</summary>
public sealed record GraphPing(bool Ok, int HttpStatus, int? ErrorCode, string Message);

/// <summary>
/// The cheap "is this token still good?" call the health checker makes for
/// Meta accounts (health.py <c>_graph_ping</c>): WhatsApp Cloud =
/// <c>GET /{version}/{phone_number_id}?fields=id</c>; Messenger / Instagram =
/// <c>GET /{version}/me?fields=id</c> with the page token. Its own small seam
/// (not part of the send clients) so the checker stays fakeable and the Meta
/// transport can evolve independently.
/// </summary>
public interface IGraphHealthProbe
{
    Task<GraphPing> PingAsync(ChannelAccount account, CancellationToken ct = default);
}

public sealed class GraphHealthProbe : IGraphHealthProbe
{
    private const string GraphBase = "https://graph.facebook.com";
    private readonly IHttpClientFactory _http;
    public GraphHealthProbe(IHttpClientFactory http) => _http = http;

    public async Task<GraphPing> PingAsync(ChannelAccount account, CancellationToken ct = default)
    {
        var version = string.IsNullOrWhiteSpace(account.ApiVersion) ? "v23.0" : account.ApiVersion.Trim();
        var node = account.Channel == ChannelIds.WhatsApp
            ? Uri.EscapeDataString(account.ExternalId)
            : "me";
        try
        {
            var client = _http.CreateClient("graph-health");
            client.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/{version}/{node}?fields=id");
            // the token rides in the header, never in a URL that could reach a log
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {account.AccessToken}");
            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return new GraphPing(true, (int)resp.StatusCode, null, "");
            int? code = null;
            var message = resp.ReasonPhrase ?? "";
            try
            {
                var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (body.TryGetProperty("error", out var err))
                {
                    if (err.TryGetProperty("code", out var c) && c.TryGetInt32(out var n)) code = n;
                    if (err.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
                }
            }
            catch { /* non-JSON error page */ }
            return new GraphPing(false, (int)resp.StatusCode, code, Scrub(message, account.AccessToken));
        }
        catch (Exception ex)
        {
            return new GraphPing(false, 0, null, Scrub(ex.Message, account.AccessToken));
        }
    }

    private static string Scrub(string text, string token)
    {
        var s = (text ?? "").Replace('\n', ' ').Trim();
        if (!string.IsNullOrEmpty(token)) s = s.Replace(token, "<token>");
        return s.Length > 160 ? s[..160] : s;
    }
}
