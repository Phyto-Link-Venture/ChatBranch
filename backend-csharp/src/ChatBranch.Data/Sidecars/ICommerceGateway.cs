using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Data.Sidecars;

/// <summary>One round-trip to the seller service. <see cref="Detail"/> is operator-facing, never a secret.</summary>
public sealed record CommerceResult(bool Ok, string Detail = "", string ExternalId = "")
{
    public static CommerceResult Success(string externalId = "") => new(true, "", externalId);
    public static CommerceResult Fail(string detail) => new(false, detail);
}

/// <summary>A shop the seller service holds a grant for.</summary>
/// <param name="Channel">The ChatBranch channel (shopee, lazada, tiktok_shop, shopify).</param>
/// <param name="Account">The shop handle — what <see cref="Domain.ChannelAccount.ExternalId"/> stores.</param>
public sealed record CommerceShop(string Channel, string Account, string Marketplace, string ShopId,
    string ShopName, string Region, string Status, string StatusDetail);

/// <summary>
/// The `e-comerce-seller-mcp` sidecar, which holds the marketplace grants
/// (Shopee, Lazada, TikTok Shop, Shopify) and talks to the marketplace APIs.
///
/// Same contract shape as <see cref="IBaileysGateway"/>, for the same reason: a
/// provider connection nobody wants to re-implement in C# lives in a Node
/// sidecar, this backend owns the conversation, and ONE shared secret
/// (<c>X-Commerce-Secret</c>) authenticates both directions — the sidecar pushes
/// buyer messages to <c>/api/commerce/inbound</c>, and we ask it to deliver
/// replies.
///
/// Config resolves Setting → env → default, read PER CALL so a dashboard change
/// applies without a restart: <c>commerce_url</c> (<c>COMMERCE_URL</c>, default
/// <c>http://127.0.0.1:3040</c>) and <c>commerce_secret</c>
/// (<c>COMMERCE_SECRET</c>, no default). **No secret means not configured**: the
/// sidecar refuses unauthenticated calls, so guessing one would only produce a
/// failed send where a clean sandbox message is the honest answer.
/// </summary>
public interface ICommerceGateway
{
    bool IsConfigured { get; }
    /// <summary>Base URL in use, for operator-facing "unreachable at …" messages.</summary>
    string BaseUrl { get; }
    /// <summary>Why it is not usable, or "".</summary>
    string NotConfiguredReason { get; }

    /// <summary>
    /// Deliver a reply to a buyer. <paramref name="accountExternalId"/> is the shop
    /// handle (<c>shopee:123456</c>), <paramref name="conversationKey"/> the
    /// marketplace's conversation id — which is what a <see cref="Domain.Thread.Key"/>
    /// holds on these channels, precisely because it is what a reply needs.
    /// </summary>
    Task<CommerceResult> SendTextAsync(string accountExternalId, string conversationKey, string text,
        CancellationToken ct = default);

    /// <summary>Every shop the sidecar holds a grant for (for the inbox sync and the health probe).</summary>
    Task<(IReadOnlyList<CommerceShop> Shops, string Error)> ShopsAsync(CancellationToken ct = default);

    /// <summary>Ask the sidecar to poll the marketplaces that have no chat webhook (Lazada).</summary>
    Task<CommerceResult> PollChatsAsync(CancellationToken ct = default);
}

/// <summary>
/// The no-op gateway: nothing is attached, so every call answers with the reason
/// instead of throwing. Registered when the service is absent, and used by tests
/// that must never reach a developer's live sidecar.
/// </summary>
public sealed class NullCommerceGateway : ICommerceGateway
{
    public bool IsConfigured => false;
    public string BaseUrl => "";
    public string NotConfiguredReason =>
        "The seller service is not attached: set commerce_url and commerce_secret (or COMMERCE_URL / COMMERCE_SECRET).";

    public Task<CommerceResult> SendTextAsync(string a, string k, string t, CancellationToken ct = default) =>
        Task.FromResult(CommerceResult.Fail(NotConfiguredReason));

    public Task<(IReadOnlyList<CommerceShop> Shops, string Error)> ShopsAsync(CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<CommerceShop>, string)>((Array.Empty<CommerceShop>(), NotConfiguredReason));

    public Task<CommerceResult> PollChatsAsync(CancellationToken ct = default) =>
        Task.FromResult(CommerceResult.Fail(NotConfiguredReason));
}

/// <summary>
/// The real client. Reachability is discovered per call and reported as a clean
/// error — never an exception into a send or a webhook path.
/// </summary>
public sealed class HttpCommerceGateway : ICommerceGateway
{
    public const string ClientName = "commerce";
    public const string SecretHeader = "X-Commerce-Secret";

    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;

    public HttpCommerceGateway(IHttpClientFactory http, IServiceScopeFactory scopes)
    {
        _http = http;
        _scopes = scopes;
    }

    // Read through a fresh scope: this is a singleton, ScopingService is scoped.
    private (string Url, string Secret) Config()
    {
        using var scope = _scopes.CreateScope();
        var sc = scope.ServiceProvider.GetRequiredService<ScopingService>();
        string S(string key, string env, string dflt)
        {
            var v = sc.SettingValue(key);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            var e = Environment.GetEnvironmentVariable(env);
            return string.IsNullOrWhiteSpace(e) ? dflt : e.Trim();
        }
        return (S("commerce_url", "COMMERCE_URL", "http://127.0.0.1:3040").TrimEnd('/'),
                S("commerce_secret", "COMMERCE_SECRET", ""));
    }

    public bool IsConfigured
    {
        get
        {
            var (url, secret) = Config();
            return url.Length > 0 && secret.Length > 0;
        }
    }

    public string BaseUrl => Config().Url;

    public string NotConfiguredReason
    {
        get
        {
            var (url, secret) = Config();
            if (url.Length == 0) return "The seller service has no URL: set commerce_url (or COMMERCE_URL).";
            if (secret.Length == 0)
                return "The seller service has no shared secret: set commerce_secret (or COMMERCE_SECRET) to the " +
                       "same value as CHATBRANCH_SECRET there.";
            return "";
        }
    }

    private async Task<(bool Ok, JsonElement Body, string Error)> CallAsync(HttpMethod method, string path,
        object? payload, CancellationToken ct, int timeoutSeconds = 30)
    {
        var (url, secret) = Config();
        if (secret.Length == 0) return (false, default, NotConfiguredReason);
        try
        {
            var client = _http.CreateClient(ClientName);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            using var req = new HttpRequestMessage(method, url + path);
            req.Headers.TryAddWithoutValidation(SecretHeader, secret);
            if (payload != null) req.Content = JsonContent.Create(payload);
            using var resp = await client.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            JsonElement body;
            try { body = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone(); }
            catch { body = default; }
            if (!resp.IsSuccessStatusCode)
            {
                var detail = body.ValueKind == JsonValueKind.Object
                             && body.TryGetProperty("detail", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "" : text;
                var capped = detail.Length > 200 ? detail[..200] : detail;
                return (false, body, $"seller service {(int)resp.StatusCode}: {capped}".Trim());
            }
            return (true, body, "");
        }
        catch (HttpRequestException)
        {
            return (false, default,
                $"The seller service is unreachable at {url} — start it with `npm start` in e-comerce-seller-mcp.");
        }
        catch (TaskCanceledException)
        {
            return (false, default, $"The seller service timed out at {url}.");
        }
        catch (Exception ex)
        {
            return (false, default, ex.GetType().Name);
        }
    }

    public async Task<CommerceResult> SendTextAsync(string accountExternalId, string conversationKey, string text,
        CancellationToken ct = default)
    {
        var (ok, body, error) = await CallAsync(HttpMethod.Post, "/api/bridge/send",
            new { account = accountExternalId, key = conversationKey, text }, ct);
        if (!ok) return CommerceResult.Fail(error);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("messageId", out var m)
                 && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "" : "";
        return CommerceResult.Success(id);
    }

    public async Task<(IReadOnlyList<CommerceShop> Shops, string Error)> ShopsAsync(CancellationToken ct = default)
    {
        var (ok, body, error) = await CallAsync(HttpMethod.Get, "/api/bridge/accounts", null, ct, timeoutSeconds: 15);
        if (!ok) return (Array.Empty<CommerceShop>(), error);
        var shops = new List<CommerceShop>();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("accounts", out var rows)
            && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                string S(string key) => row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
                // trust the marketplace, not the channel name: one place maps them
                var channel = CommerceChannels.ChannelFor(S("marketplace")) ?? CommerceChannels.ChannelFor(S("channel"));
                if (channel == null) continue;
                var shopId = S("shop_id");
                if (shopId.Length == 0) continue;
                shops.Add(new CommerceShop(channel, CommerceChannels.AccountKey(channel, shopId),
                    CommerceChannels.Marketplace(channel), shopId, S("shop_name"), S("region"),
                    S("status"), S("status_detail")));
            }
        }
        return (shops, "");
    }

    public async Task<CommerceResult> PollChatsAsync(CancellationToken ct = default)
    {
        var (ok, _, error) = await CallAsync(HttpMethod.Post, "/api/bridge/poll", new { }, ct, timeoutSeconds: 60);
        return ok ? CommerceResult.Success() : CommerceResult.Fail(error);
    }
}
