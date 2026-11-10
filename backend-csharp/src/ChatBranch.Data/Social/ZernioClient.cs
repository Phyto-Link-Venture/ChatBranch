using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Data.Social;

/// <summary>
/// One round-trip to Zernio. <see cref="Error"/> is operator-facing (Zernio's own
/// <c>code: error</c>, or a transport failure) and never carries the API key.
/// </summary>
public sealed record ZernioReply(int Status, JsonElement Json, string Error)
{
    public bool Ok => Status is >= 200 and < 300 && Error.Length == 0;

    /// <summary>A string at a JSON path ("" when absent).</summary>
    public string Str(params string[] path) => ZernioJson.Str(At(path));

    public JsonElement At(params string[] path) => ZernioJson.At(Json, path);

    public static ZernioReply Fail(string error, int status = 0) => new(status, default, error);

    /// <summary>A successful reply carrying <paramref name="json"/> (fakes and tests).</summary>
    public static ZernioReply Of(object json, int status = 200) =>
        new(status, JsonSerializer.SerializeToElement(json), "");
}

/// <summary>Defensive readers for Zernio's JSON (fields come and go per platform).</summary>
public static class ZernioJson
{
    public static JsonElement At(JsonElement e, params string[] path)
    {
        var cur = e;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out var next)) return default;
            cur = next;
        }
        return cur;
    }

    public static string Str(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    public static string Str(JsonElement e, params string[] path) => Str(At(e, path));

    public static bool Bool(JsonElement e, params string[] path) =>
        At(e, path) is { ValueKind: JsonValueKind.True } || Str(At(e, path)) == "true";

    public static int Int(JsonElement e, params string[] path)
    {
        var v = At(e, path);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return int.TryParse(Str(v), out var m) ? m : 0;
    }

    public static IEnumerable<JsonElement> Arr(JsonElement e, params string[] path)
    {
        var v = At(e, path);
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();
    }

    /// <summary>An id that may come as a string or as a populated object (<c>{_id, …}</c>).</summary>
    public static string Id(JsonElement e) => e.ValueKind == JsonValueKind.Object ? Str(e, "_id") : Str(e);
}

/// <summary>
/// The Zernio REST API (<c>https://zernio.com/api/v1</c>, <c>Authorization: Bearer</c>).
/// ONE key serves the whole deployment. Every method answers a <see cref="ZernioReply"/>
/// and never throws — a social send or a webhook path must not fail on transport.
/// Paths and fields follow Zernio's OpenAPI spec (openapi.yaml, API 1.548).
/// </summary>
public interface IZernioClient
{
    bool IsConfigured { get; }
    /// <summary>The base URL in use, for "unreachable at …" messages.</summary>
    string BaseUrl { get; }
    /// <summary>Why it cannot be used ("" when it can).</summary>
    string NotConfiguredReason { get; }

    // ---- profiles + accounts -------------------------------------------------
    /// <summary>GET /v1/profiles[?externalId=] → {profiles:[{_id, name, isDefault, externalId}]}.</summary>
    Task<ZernioReply> ListProfilesAsync(string? externalId = null, CancellationToken ct = default);
    /// <summary>POST /v1/profiles {name, externalId} → 201 {profile:{_id…}}; 409 carries details.existingProfileId.</summary>
    Task<ZernioReply> CreateProfileAsync(string name, string externalId, CancellationToken ct = default);
    /// <summary>GET /v1/accounts[?profileId=] → {accounts:[…]}.</summary>
    Task<ZernioReply> ListAccountsAsync(string? profileId = null, CancellationToken ct = default);
    /// <summary>GET /v1/connect/{platform}?profileId=&amp;redirect_url= → {authUrl}.</summary>
    Task<ZernioReply> ConnectUrlAsync(string platform, string profileId, string redirectUrl, CancellationToken ct = default);
    /// <summary>DELETE /v1/accounts/{accountId}.</summary>
    Task<ZernioReply> DeleteAccountAsync(string accountId, CancellationToken ct = default);
    /// <summary>PATCH /v1/accounts/{accountId} {profileId} → {message, profileId} (moveAccountToProfile).</summary>
    Task<ZernioReply> MoveAccountAsync(string accountId, string profileId, CancellationToken ct = default);
    /// <summary>GET /v1/accounts/{accountId}/facebook-page[?refresh=true] → {pages:[{id, name, username, category}], selectedPageId} (getFacebookPages).</summary>
    Task<ZernioReply> FacebookPagesAsync(string accountId, bool refresh = false, CancellationToken ct = default);
    /// <summary>PUT /v1/accounts/{accountId}/facebook-page {selectedPageId} → {selectedPage} (updateFacebookPage).</summary>
    Task<ZernioReply> SelectFacebookPageAsync(string accountId, string pageId, CancellationToken ct = default);

    // ---- webhooks --------------------------------------------------------------
    /// <summary>GET /v1/webhooks/settings → {webhooks:[{_id, url, events…}]}.</summary>
    Task<ZernioReply> ListWebhooksAsync(CancellationToken ct = default);
    /// <summary>POST /v1/webhooks/settings {name, url, secret, events, isActive}.</summary>
    Task<ZernioReply> CreateWebhookAsync(string name, string url, string secret, IReadOnlyList<string> events,
        CancellationToken ct = default);
    /// <summary>PUT /v1/webhooks/settings {webhookId, url, secret, events, isActive}.</summary>
    Task<ZernioReply> UpdateWebhookAsync(string webhookId, string url, string secret, IReadOnlyList<string> events,
        CancellationToken ct = default);

    // ---- the inbox: comments, reviews, DMs -------------------------------------
    /// <summary>POST /v1/inbox/comments/{postId} {accountId, message, commentId} → {data:{commentId}}.</summary>
    Task<ZernioReply> ReplyToCommentAsync(string accountId, string postId, string commentId, string message,
        CancellationToken ct = default);
    /// <summary>POST (hide) / DELETE (unhide) /v1/inbox/comments/{postId}/{commentId}/hide.</summary>
    Task<ZernioReply> HideCommentAsync(string accountId, string postId, string commentId, bool hidden,
        CancellationToken ct = default);
    /// <summary>POST /v1/inbox/comments/{postId}/{commentId}/private-reply {accountId, message} (Instagram, Facebook).</summary>
    Task<ZernioReply> PrivateReplyAsync(string accountId, string postId, string commentId, string message,
        CancellationToken ct = default);
    /// <summary>POST /v1/inbox/reviews/{reviewId}/reply {accountId, message} → {reply:{id…}} (reviewId URL-encoded).</summary>
    Task<ZernioReply> ReplyToReviewAsync(string accountId, string reviewId, string message, CancellationToken ct = default);
    /// <summary>POST /v1/inbox/conversations/{conversationId}/messages {accountId, message} → {data:{messageId}}.</summary>
    Task<ZernioReply> SendMessageAsync(string accountId, string conversationId, string message,
        CancellationToken ct = default);

    // ---- native channels via Zernio (WhatsApp, Messenger, Instagram, Telegram, SMS) ----
    /// <summary>
    /// POST /v1/inbox/conversations/{conversationId}/messages with a full body (sendInboxMessage):
    /// <c>message</c>, <c>attachmentUrl</c> + <c>attachmentType</c> (image | video | audio | file),
    /// <c>attachmentName</c>, <c>replyTo</c>, <c>template</c> ({elements:[{name, language, components}]}
    /// on WhatsApp), <c>messagingType</c> + <c>messageTag</c> (Messenger / Instagram HUMAN_AGENT).
    /// <paramref name="accountId"/> is added. → {success, data:{messageId, conversationId}}.
    /// </summary>
    Task<ZernioReply> SendInboxMessageAsync(string accountId, string conversationId, Dictionary<string, object?> body,
        CancellationToken ct = default);
    /// <summary>POST /v1/inbox/conversations (createInboxConversation) {accountId, participantId, message?,
    /// templateName?, templateLanguage?, templateParams?} → 201 {data:{messageId, conversationId}}.</summary>
    Task<ZernioReply> CreateConversationAsync(Dictionary<string, object?> body, CancellationToken ct = default);
    /// <summary>POST /v1/sms/messages {from, to, text, mediaUrls?} → {id, conversationId, status}.</summary>
    Task<ZernioReply> SendSmsAsync(string from, string to, string text, IReadOnlyList<string>? mediaUrls = null,
        CancellationToken ct = default);
    /// <summary>POST (emoji) / DELETE ("" = remove ours) /v1/inbox/conversations/{c}/messages/{m}/reactions.</summary>
    Task<ZernioReply> ReactAsync(string accountId, string conversationId, string messageId, string emoji,
        CancellationToken ct = default);
    /// <summary>GET /v1/whatsapp/templates?accountId= → {templates:[{id, name, status, category, language, components}]}.</summary>
    Task<ZernioReply> ListWhatsAppTemplatesAsync(string accountId, CancellationToken ct = default);
    /// <summary>
    /// GET an inbound attachment: the bearer key rides ONLY to Zernio's own host (WhatsApp media
    /// points at <c>GET /v1/whatsapp/media/{id}</c>); a platform CDN link is fetched bare.
    /// </summary>
    Task<(byte[]? Bytes, string Mime, string Error)> DownloadAsync(string url, long maxBytes, CancellationToken ct = default);
    /// <summary>GET /v1/connect/telegram?profileId= → {code, expiresAt, expiresIn, botUsername, instructions[]}.</summary>
    Task<ZernioReply> TelegramCodeAsync(string profileId, CancellationToken ct = default);
    /// <summary>PATCH /v1/connect/telegram?code= → {status: pending | connected | expired, chatId, chatTitle, account}.</summary>
    Task<ZernioReply> TelegramStatusAsync(string code, CancellationToken ct = default);
    /// <summary>POST /v1/connect/telegram {chatId, profileId} → {message, account:{_id…}} (the bot is already an admin).</summary>
    Task<ZernioReply> TelegramConnectAsync(string profileId, string chatId, CancellationToken ct = default);
    /// <summary>POST /v1/connect/whatsapp/credentials {profileId, accessToken, wabaId, phoneNumberId, pin?} → {account:{accountId…}}.</summary>
    Task<ZernioReply> WhatsAppCredentialsAsync(string profileId, string accessToken, string wabaId, string phoneNumberId,
        string? pin, CancellationToken ct = default);
    /// <summary>GET /v1/phone-numbers[?profileId=] → {numbers:[OwnedPhoneNumber]}.</summary>
    Task<ZernioReply> ListPhoneNumbersAsync(string? profileId = null, CancellationToken ct = default);
    /// <summary>POST /v1/phone-numbers/{id}/sms → {enabled, id (the SMS account), phoneNumber, isActive, needsRegistration…}.</summary>
    Task<ZernioReply> EnableSmsAsync(string phoneNumberId, CancellationToken ct = default);

    // ---- posts + media ---------------------------------------------------------
    /// <summary>POST /v1/posts → {post:{_id, status, platforms[]}}.</summary>
    Task<ZernioReply> CreatePostAsync(Dictionary<string, object?> body, CancellationToken ct = default);
    /// <summary>GET /v1/posts/{postId} → {post}.</summary>
    Task<ZernioReply> GetPostAsync(string postId, CancellationToken ct = default);
    /// <summary>PUT /v1/posts/{postId} → {post}.</summary>
    Task<ZernioReply> UpdatePostAsync(string postId, Dictionary<string, object?> body, CancellationToken ct = default);
    /// <summary>DELETE /v1/posts/{postId} (any status but published).</summary>
    Task<ZernioReply> DeletePostAsync(string postId, CancellationToken ct = default);
    /// <summary>POST /v1/posts/{postId}/retry → {post}.</summary>
    Task<ZernioReply> RetryPostAsync(string postId, CancellationToken ct = default);
    /// <summary>POST /v1/posts/{postId}/unpublish {platform, accountId} (a published post).</summary>
    Task<ZernioReply> UnpublishPostAsync(string postId, string platform, string accountId, CancellationToken ct = default);
    /// <summary>POST /v1/media/presign {filename, contentType, size} → {uploadUrl, publicUrl}.</summary>
    Task<ZernioReply> PresignMediaAsync(string filename, string contentType, long size, CancellationToken ct = default);
    /// <summary>PUT the bytes to a presigned <paramref name="uploadUrl"/> (no Zernio auth header — the URL is signed).</summary>
    Task<ZernioReply> UploadMediaAsync(string uploadUrl, byte[] bytes, string contentType, CancellationToken ct = default);
}

/// <summary>Nothing attached: every call answers the reason. Registered by tests that must never reach Zernio.</summary>
public sealed class NullZernioClient : IZernioClient
{
    public bool IsConfigured => false;
    public string BaseUrl => "";
    public string NotConfiguredReason => "Zernio is not connected: save the Zernio API key (zernio_api_key) first.";
    private Task<ZernioReply> No() => Task.FromResult(ZernioReply.Fail(NotConfiguredReason));

    public Task<ZernioReply> ListProfilesAsync(string? e = null, CancellationToken ct = default) => No();
    public Task<ZernioReply> CreateProfileAsync(string n, string e, CancellationToken ct = default) => No();
    public Task<ZernioReply> ListAccountsAsync(string? p = null, CancellationToken ct = default) => No();
    public Task<ZernioReply> ConnectUrlAsync(string pl, string p, string r, CancellationToken ct = default) => No();
    public Task<ZernioReply> DeleteAccountAsync(string a, CancellationToken ct = default) => No();
    public Task<ZernioReply> MoveAccountAsync(string a, string p, CancellationToken ct = default) => No();
    public Task<ZernioReply> FacebookPagesAsync(string a, bool r = false, CancellationToken ct = default) => No();
    public Task<ZernioReply> SelectFacebookPageAsync(string a, string p, CancellationToken ct = default) => No();
    public Task<ZernioReply> ListWebhooksAsync(CancellationToken ct = default) => No();
    public Task<ZernioReply> CreateWebhookAsync(string n, string u, string s, IReadOnlyList<string> e, CancellationToken ct = default) => No();
    public Task<ZernioReply> UpdateWebhookAsync(string i, string u, string s, IReadOnlyList<string> e, CancellationToken ct = default) => No();
    public Task<ZernioReply> ReplyToCommentAsync(string a, string p, string c, string m, CancellationToken ct = default) => No();
    public Task<ZernioReply> HideCommentAsync(string a, string p, string c, bool h, CancellationToken ct = default) => No();
    public Task<ZernioReply> PrivateReplyAsync(string a, string p, string c, string m, CancellationToken ct = default) => No();
    public Task<ZernioReply> ReplyToReviewAsync(string a, string r, string m, CancellationToken ct = default) => No();
    public Task<ZernioReply> SendMessageAsync(string a, string c, string m, CancellationToken ct = default) => No();
    public Task<ZernioReply> SendInboxMessageAsync(string a, string c, Dictionary<string, object?> b, CancellationToken ct = default) => No();
    public Task<ZernioReply> CreateConversationAsync(Dictionary<string, object?> b, CancellationToken ct = default) => No();
    public Task<ZernioReply> SendSmsAsync(string f, string t, string x, IReadOnlyList<string>? m = null, CancellationToken ct = default) => No();
    public Task<ZernioReply> ReactAsync(string a, string c, string m, string e, CancellationToken ct = default) => No();
    public Task<ZernioReply> ListWhatsAppTemplatesAsync(string a, CancellationToken ct = default) => No();
    public Task<(byte[]? Bytes, string Mime, string Error)> DownloadAsync(string u, long m, CancellationToken ct = default) =>
        Task.FromResult<(byte[]?, string, string)>((null, "", NotConfiguredReason));
    public Task<ZernioReply> TelegramCodeAsync(string p, CancellationToken ct = default) => No();
    public Task<ZernioReply> TelegramStatusAsync(string c, CancellationToken ct = default) => No();
    public Task<ZernioReply> TelegramConnectAsync(string p, string c, CancellationToken ct = default) => No();
    public Task<ZernioReply> WhatsAppCredentialsAsync(string p, string t, string w, string n, string? pin, CancellationToken ct = default) => No();
    public Task<ZernioReply> ListPhoneNumbersAsync(string? p = null, CancellationToken ct = default) => No();
    public Task<ZernioReply> EnableSmsAsync(string n, CancellationToken ct = default) => No();
    public Task<ZernioReply> CreatePostAsync(Dictionary<string, object?> b, CancellationToken ct = default) => No();
    public Task<ZernioReply> GetPostAsync(string p, CancellationToken ct = default) => No();
    public Task<ZernioReply> UpdatePostAsync(string p, Dictionary<string, object?> b, CancellationToken ct = default) => No();
    public Task<ZernioReply> DeletePostAsync(string p, CancellationToken ct = default) => No();
    public Task<ZernioReply> RetryPostAsync(string p, CancellationToken ct = default) => No();
    public Task<ZernioReply> UnpublishPostAsync(string p, string pl, string a, CancellationToken ct = default) => No();
    public Task<ZernioReply> PresignMediaAsync(string f, string c, long s, CancellationToken ct = default) => No();
    public Task<ZernioReply> UploadMediaAsync(string u, byte[] b, string c, CancellationToken ct = default) => No();
}

/// <summary>
/// The real client. The key and base URL resolve Setting → env → default PER CALL,
/// so saving the key in the dashboard applies without a restart:
/// <c>zernio_api_key</c> (<c>ZERNIO_API_KEY</c>) and <c>zernio_base_url</c>
/// (<c>ZERNIO_BASE_URL</c>, default <see cref="DefaultBaseUrl"/>).
/// </summary>
public sealed class HttpZernioClient : IZernioClient
{
    public const string ClientName = "zernio";
    public const string DefaultBaseUrl = "https://zernio.com/api/v1";
    public const string KeySetting = "zernio_api_key";
    public const string BaseUrlSetting = "zernio_base_url";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;

    public HttpZernioClient(IHttpClientFactory http, IServiceScopeFactory scopes)
    {
        _http = http;
        _scopes = scopes;
    }

    /// <summary>The key + base URL in effect (read through a fresh scope: this is a singleton).</summary>
    public static (string Key, string BaseUrl) Resolve(ScopingService sc)
    {
        string S(string key, string env, string dflt)
        {
            var v = "";
            try { v = sc.SettingValue(key); } catch { }
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            var e = Environment.GetEnvironmentVariable(env);
            return string.IsNullOrWhiteSpace(e) ? dflt : e.Trim();
        }
        return (S(KeySetting, "ZERNIO_API_KEY", ""), NormalizeBase(S(BaseUrlSetting, "ZERNIO_BASE_URL", DefaultBaseUrl)));
    }

    /// <summary>Zernio's spec names the server <c>…/api</c> with <c>/v1</c> in every path: accept both spellings.</summary>
    public static string NormalizeBase(string raw)
    {
        var b = (raw ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) b = DefaultBaseUrl;
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b : b + "/v1";
    }

    private (string Key, string BaseUrl) Config()
    {
        using var scope = _scopes.CreateScope();
        return Resolve(scope.ServiceProvider.GetRequiredService<ScopingService>());
    }

    public bool IsConfigured => Config().Key.Length > 0;
    public string BaseUrl => Config().BaseUrl;
    public string NotConfiguredReason =>
        IsConfigured ? "" : "Zernio is not connected: save the Zernio API key (zernio_api_key or ZERNIO_API_KEY) first.";

    private async Task<ZernioReply> CallAsync(HttpMethod method, string path, object? body, CancellationToken ct,
        int timeoutSeconds = 30)
    {
        var (key, baseUrl) = Config();
        if (key.Length == 0) return ZernioReply.Fail(NotConfiguredReason);
        try
        {
            using var req = new HttpRequestMessage(method, baseUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var res = await _http.CreateClient(ClientName).SendAsync(req, cts.Token);
            var raw = await res.Content.ReadAsStringAsync(cts.Token);
            JsonElement json = default;
            if (raw.Length > 0)
                try { json = JsonDocument.Parse(raw).RootElement.Clone(); } catch { /* not JSON */ }
            var status = (int)res.StatusCode;
            if (status is >= 200 and < 300) return new ZernioReply(status, json, "");
            var code = ZernioJson.Str(json, "code");
            var msg = ZernioJson.Str(json, "error");
            if (msg.Length == 0) msg = ZernioJson.Str(json, "message");
            var detail = (code.Length > 0 ? code + ": " : "") + (msg.Length > 0 ? msg : $"HTTP {status}");
            return new ZernioReply(status, json, Scrub(detail, key));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ZernioReply.Fail($"the managed connection timed out ({timeoutSeconds}s)");
        }
        catch (Exception ex)
        {
            return ZernioReply.Fail($"the managed connection is unreachable ({ex.GetType().Name})");
        }
    }

    private static string Scrub(string text, string key) =>
        key.Length > 0 ? text.Replace(key, "••••", StringComparison.Ordinal) : text;

    private static string E(string v) => Uri.EscapeDataString(v ?? "");

    public Task<ZernioReply> ListProfilesAsync(string? externalId = null, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, "/profiles" + (string.IsNullOrEmpty(externalId) ? "" : "?externalId=" + E(externalId)),
            null, ct);

    public Task<ZernioReply> CreateProfileAsync(string name, string externalId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, "/profiles", new Dictionary<string, object?>
        {
            ["name"] = name,
            ["externalId"] = externalId,
            ["description"] = "Created by ChatBranch",
        }, ct);

    public Task<ZernioReply> ListAccountsAsync(string? profileId = null, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, "/accounts" + (string.IsNullOrEmpty(profileId) ? "" : "?profileId=" + E(profileId)),
            null, ct);

    public Task<ZernioReply> ConnectUrlAsync(string platform, string profileId, string redirectUrl,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get,
            $"/connect/{E(platform)}?profileId={E(profileId)}" +
            (redirectUrl.Length > 0 ? "&redirect_url=" + E(redirectUrl) : ""), null, ct);

    public Task<ZernioReply> DeleteAccountAsync(string accountId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Delete, $"/accounts/{E(accountId)}", null, ct);

    public Task<ZernioReply> MoveAccountAsync(string accountId, string profileId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Patch, $"/accounts/{E(accountId)}",
            new Dictionary<string, object?> { ["profileId"] = profileId }, ct);

    public Task<ZernioReply> FacebookPagesAsync(string accountId, bool refresh = false, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, $"/accounts/{E(accountId)}/facebook-page" + (refresh ? "?refresh=true" : ""), null, ct);

    public Task<ZernioReply> SelectFacebookPageAsync(string accountId, string pageId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Put, $"/accounts/{E(accountId)}/facebook-page",
            new Dictionary<string, object?> { ["selectedPageId"] = pageId }, ct);

    public Task<ZernioReply> ListWebhooksAsync(CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, "/webhooks/settings", null, ct);

    public Task<ZernioReply> CreateWebhookAsync(string name, string url, string secret, IReadOnlyList<string> events,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, "/webhooks/settings", new Dictionary<string, object?>
        {
            ["name"] = name, ["url"] = url, ["secret"] = secret, ["events"] = events, ["isActive"] = true,
        }, ct);

    public Task<ZernioReply> UpdateWebhookAsync(string webhookId, string url, string secret,
        IReadOnlyList<string> events, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Put, "/webhooks/settings", new Dictionary<string, object?>
        {
            ["webhookId"] = webhookId, ["url"] = url, ["secret"] = secret, ["events"] = events, ["isActive"] = true,
        }, ct);

    public Task<ZernioReply> ReplyToCommentAsync(string accountId, string postId, string commentId, string message,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/inbox/comments/{E(postId)}", new Dictionary<string, object?>
        {
            ["accountId"] = accountId, ["message"] = message,
            ["commentId"] = string.IsNullOrEmpty(commentId) ? null : commentId,
        }, ct);

    public Task<ZernioReply> HideCommentAsync(string accountId, string postId, string commentId, bool hidden,
        CancellationToken ct = default) =>
        hidden
            ? CallAsync(HttpMethod.Post, $"/inbox/comments/{E(postId)}/{E(commentId)}/hide",
                new Dictionary<string, object?> { ["accountId"] = accountId }, ct)
            : CallAsync(HttpMethod.Delete, $"/inbox/comments/{E(postId)}/{E(commentId)}/hide?accountId={E(accountId)}",
                null, ct);

    public Task<ZernioReply> PrivateReplyAsync(string accountId, string postId, string commentId, string message,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/inbox/comments/{E(postId)}/{E(commentId)}/private-reply",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["message"] = message }, ct);

    public Task<ZernioReply> ReplyToReviewAsync(string accountId, string reviewId, string message,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/inbox/reviews/{E(reviewId)}/reply",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["message"] = message }, ct);

    public Task<ZernioReply> SendMessageAsync(string accountId, string conversationId, string message,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/inbox/conversations/{E(conversationId)}/messages",
            new Dictionary<string, object?> { ["accountId"] = accountId, ["message"] = message }, ct);

    public Task<ZernioReply> SendInboxMessageAsync(string accountId, string conversationId,
        Dictionary<string, object?> body, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/inbox/conversations/{E(conversationId)}/messages",
            new Dictionary<string, object?>(body) { ["accountId"] = accountId }, ct, timeoutSeconds: 60);

    public Task<ZernioReply> CreateConversationAsync(Dictionary<string, object?> body, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, "/inbox/conversations", body, ct, timeoutSeconds: 60);

    public Task<ZernioReply> SendSmsAsync(string from, string to, string text, IReadOnlyList<string>? mediaUrls = null,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, "/sms/messages", new Dictionary<string, object?>
        {
            ["from"] = from, ["to"] = to,
            ["text"] = string.IsNullOrEmpty(text) ? null : text,
            ["mediaUrls"] = mediaUrls is { Count: > 0 } ? mediaUrls : null,
        }, ct);

    public Task<ZernioReply> ReactAsync(string accountId, string conversationId, string messageId, string emoji,
        CancellationToken ct = default) =>
        string.IsNullOrEmpty(emoji)
            ? CallAsync(HttpMethod.Delete,
                $"/inbox/conversations/{E(conversationId)}/messages/{E(messageId)}/reactions?accountId={E(accountId)}", null, ct)
            : CallAsync(HttpMethod.Post, $"/inbox/conversations/{E(conversationId)}/messages/{E(messageId)}/reactions",
                new Dictionary<string, object?> { ["accountId"] = accountId, ["emoji"] = emoji }, ct);

    public Task<ZernioReply> ListWhatsAppTemplatesAsync(string accountId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, $"/whatsapp/templates?accountId={E(accountId)}", null, ct);

    public async Task<(byte[]? Bytes, string Mime, string Error)> DownloadAsync(string url, long maxBytes,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (null, "", "not an https link");
        var (key, baseUrl) = Config();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            // the key goes to Zernio's own host only — never to a platform CDN
            if (key.Length > 0 && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)
                && string.Equals(b.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            using var res = await _http.CreateClient(ClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode) return (null, "", $"download failed (HTTP {(int)res.StatusCode})");
            if (res.Content.Headers.ContentLength is long len && len > maxBytes) return (null, "", "file too large");
            var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
            if (bytes.LongLength > maxBytes) return (null, "", "file too large");
            var mime = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (mime.Contains("json", StringComparison.OrdinalIgnoreCase))
                return (null, "", "the managed connection answered with an error instead of the file");
            return (bytes, mime, "");
        }
        catch (Exception ex)
        {
            return (null, "", $"download failed ({ex.GetType().Name})");
        }
    }

    public Task<ZernioReply> TelegramCodeAsync(string profileId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, $"/connect/telegram?profileId={E(profileId)}", null, ct);

    public Task<ZernioReply> TelegramStatusAsync(string code, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Patch, $"/connect/telegram?code={E(code)}", null, ct);

    public Task<ZernioReply> TelegramConnectAsync(string profileId, string chatId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, "/connect/telegram",
            new Dictionary<string, object?> { ["chatId"] = chatId, ["profileId"] = profileId }, ct);

    public Task<ZernioReply> WhatsAppCredentialsAsync(string profileId, string accessToken, string wabaId,
        string phoneNumberId, string? pin, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, "/connect/whatsapp/credentials", new Dictionary<string, object?>
        {
            ["profileId"] = profileId, ["accessToken"] = accessToken, ["wabaId"] = wabaId,
            ["phoneNumberId"] = phoneNumberId, ["pin"] = string.IsNullOrEmpty(pin) ? null : pin,
        }, ct, timeoutSeconds: 60);

    public Task<ZernioReply> ListPhoneNumbersAsync(string? profileId = null, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, "/phone-numbers" + (string.IsNullOrEmpty(profileId) ? "" : "?profileId=" + E(profileId)),
            null, ct);

    public Task<ZernioReply> EnableSmsAsync(string phoneNumberId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/phone-numbers/{E(phoneNumberId)}/sms", null, ct);

    // publishing now runs every platform inside the request — give it room
    public Task<ZernioReply> CreatePostAsync(Dictionary<string, object?> body, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, "/posts", body, ct, timeoutSeconds: 120);

    public Task<ZernioReply> GetPostAsync(string postId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Get, $"/posts/{E(postId)}", null, ct);

    public Task<ZernioReply> UpdatePostAsync(string postId, Dictionary<string, object?> body,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Put, $"/posts/{E(postId)}", body, ct, timeoutSeconds: 120);

    public Task<ZernioReply> DeletePostAsync(string postId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Delete, $"/posts/{E(postId)}", null, ct);

    public Task<ZernioReply> RetryPostAsync(string postId, CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/posts/{E(postId)}/retry", null, ct, timeoutSeconds: 120);

    public Task<ZernioReply> UnpublishPostAsync(string postId, string platform, string accountId,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, $"/posts/{E(postId)}/unpublish",
            new Dictionary<string, object?> { ["platform"] = platform, ["accountId"] = accountId }, ct);

    public Task<ZernioReply> PresignMediaAsync(string filename, string contentType, long size,
        CancellationToken ct = default) =>
        CallAsync(HttpMethod.Post, "/media/presign", new Dictionary<string, object?>
        {
            ["filename"] = filename, ["contentType"] = contentType, ["size"] = size,
        }, ct);

    public async Task<ZernioReply> UploadMediaAsync(string uploadUrl, byte[] bytes, string contentType,
        CancellationToken ct = default)
    {
        if (!uploadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return ZernioReply.Fail("the upload URL is not https");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(bytes) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            using var res = await _http.CreateClient(ClientName).SendAsync(req, cts.Token);
            var status = (int)res.StatusCode;
            return status is >= 200 and < 300
                ? new ZernioReply(status, default, "")
                : ZernioReply.Fail($"the upload was refused (HTTP {status})", status);
        }
        catch (Exception ex)
        {
            return ZernioReply.Fail($"the upload failed ({ex.GetType().Name})");
        }
    }
}
