using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

public record SendOutcome(bool Ok, string ExternalId, string Detail);

/// <summary>
/// WhatsApp Cloud API sender (port of whatsapp/client.py send_text): POST to
/// graph.facebook.com/{version}/{phone_number_id}/messages with a Bearer token,
/// the standard text payload, returning the wamid. An account with no
/// access_token is "not configured" → the caller sandbox-stores instead.
/// </summary>
public interface IWhatsAppCloudClient
{
    bool IsConfigured(ChannelAccount account);
    Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default);
}

public class WhatsAppCloudClient : IWhatsAppCloudClient
{
    private readonly IHttpClientFactory _http;
    public WhatsAppCloudClient(IHttpClientFactory http) => _http = http;

    public bool IsConfigured(ChannelAccount account) =>
        account.Provider == Providers.Cloud
        && !string.IsNullOrEmpty(account.AccessToken)
        && !string.IsNullOrEmpty(account.ExternalId);

    public async Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default)
    {
        if (!IsConfigured(account)) return new SendOutcome(false, "", "account not configured");
        try
        {
            var version = string.IsNullOrEmpty(account.ApiVersion) ? "v23.0" : account.ApiVersion;
            var url = $"https://graph.facebook.com/{version}/{account.ExternalId}/messages";
            var client = _http.CreateClient("whatsapp");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {account.AccessToken}");
            req.Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = toWaId,
                type = "text",
                text = new { preview_url = false, body = text },
            });
            using var resp = await client.SendAsync(req, ct);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = json.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                    ? m.GetString() : resp.StatusCode.ToString();
                return new SendOutcome(false, "", err ?? "send failed");
            }
            var wamid = json.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0
                ? msgs[0].GetProperty("id").GetString() ?? "" : "";
            return new SendOutcome(true, wamid, "");
        }
        catch (Exception ex)
        {
            return new SendOutcome(false, "", ex.Message);
        }
    }
}
