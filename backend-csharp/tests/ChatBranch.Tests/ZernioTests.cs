using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Zernio;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ChannelIds = ChatBranch.Domain.Channels;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Zernio that never leaves the process: records every call (name + arguments) and
/// answers with what the test set. The defaults answer like Zernio's own examples.
/// </summary>
public sealed class FakeZernioClient : IZernioClient
{
    public bool Configured { get; set; } = true;
    public ConcurrentQueue<(string Call, Dictionary<string, object?> Args)> Calls { get; } = new();
    public List<object> Profiles { get; } = new();
    public List<object> Accounts { get; } = new();
    public Func<string, ZernioReply>? Override { get; set; }
    private int _n;

    public bool IsConfigured => Configured;
    public string BaseUrl => "https://zernio.test/api/v1";
    public string NotConfiguredReason => Configured ? "" : "Zernio is not connected: save the Zernio API key first.";

    public void Reset()
    {
        Configured = true;
        Calls.Clear();
        Profiles.Clear();
        Accounts.Clear();
        Override = null;
        StaleMoves = false;
        FacebookPages.Clear();
        Templates.Clear();
        Files.Clear();
        PhoneNumbers.Clear();
        Countries.Clear();
        Released.Clear();
        TelegramStatus = null;
    }

    public List<Dictionary<string, object?>> Of(string call) =>
        Calls.Where(c => c.Call == call).Select(c => c.Args).ToList();

    private Task<ZernioReply> Do(string call, Dictionary<string, object?> args, Func<ZernioReply> dflt)
    {
        Calls.Enqueue((call, args));
        if (!Configured) return Task.FromResult(ZernioReply.Fail(NotConfiguredReason));
        return Task.FromResult(Override?.Invoke(call) ?? dflt());
    }

    private string Next(string prefix) => $"{prefix}-{Interlocked.Increment(ref _n)}";

    public Task<ZernioReply> ListProfilesAsync(string? externalId = null, CancellationToken ct = default) =>
        Do("ListProfiles", new() { ["externalId"] = externalId }, () =>
        {
            var all = Profiles.Select(p => JsonSerializer.SerializeToElement(p)).ToList();
            if (externalId != null) all = all.Where(p => ZernioJson.Str(p, "externalId") == externalId).ToList();
            return ZernioReply.Of(new { profiles = all });
        });

    public Task<ZernioReply> CreateProfileAsync(string name, string externalId, CancellationToken ct = default) =>
        Do("CreateProfile", new() { ["name"] = name, ["externalId"] = externalId }, () =>
        {
            var p = new { _id = Next("prof"), name, externalId, isDefault = false };
            Profiles.Add(p);
            return ZernioReply.Of(new { message = "Profile created successfully", profile = p }, 201);
        });

    public Task<ZernioReply> ListAccountsAsync(string? profileId = null, CancellationToken ct = default) =>
        Do("ListAccounts", new() { ["profileId"] = profileId }, () => ZernioReply.Of(new { accounts = Accounts, hasAnalyticsAccess = false }));

    public Task<ZernioReply> ConnectUrlAsync(string platform, string profileId, string redirectUrl,
        string? onboarding = null, string? signup = null, CancellationToken ct = default) =>
        Do("ConnectUrl", new()
            {
                ["platform"] = platform, ["profileId"] = profileId, ["redirect_url"] = redirectUrl,
                // null = the parameter was NOT sent (Zernio's own default would apply)
                ["onboarding"] = onboarding, ["signup"] = signup,
            },
            // real Zernio answers a state with the URL; its callback params do not carry one back
            () => ZernioReply.Of(new
            {
                authUrl = $"https://auth.example/{platform}?profile={profileId}"
                          + (string.IsNullOrEmpty(onboarding) ? "" : $"&onboarding={onboarding}")
                          + (string.IsNullOrEmpty(signup) ? "" : $"&signup={signup}"),
                state = "zstate-" + Next("s"),
            }));

    public Task<ZernioReply> DeleteAccountAsync(string accountId, CancellationToken ct = default) =>
        Do("DeleteAccount", new() { ["accountId"] = accountId }, () => ZernioReply.Of(new { message = "Account disconnected successfully" }));

    /// <summary>Moves the listed account to the profile too (a later sync sees where it went), unless <see cref="StaleMoves"/>.</summary>
    public Task<ZernioReply> MoveAccountAsync(string accountId, string profileId, CancellationToken ct = default) =>
        Do("MoveAccount", new() { ["accountId"] = accountId, ["profileId"] = profileId }, () =>
        {
            if (!StaleMoves)
                for (var i = 0; i < Accounts.Count; i++)
                {
                    var row = JsonSerializer.SerializeToElement(Accounts[i]);
                    if (ZernioJson.Str(row, "_id") != accountId) continue;
                    var copy = JsonSerializer.Deserialize<Dictionary<string, object?>>(row.GetRawText())!;
                    copy["profileId"] = profileId;
                    Accounts[i] = copy;
                }
            return ZernioReply.Of(new { message = "Account updated successfully", profileId });
        });

    /// <summary>Facebook Pages per account id: the list and the selected Page.</summary>
    public ConcurrentDictionary<string, (List<object> Pages, string Selected)> FacebookPages { get; } = new();

    public Task<ZernioReply> FacebookPagesAsync(string accountId, bool refresh = false, CancellationToken ct = default) =>
        Do("FacebookPages", new() { ["accountId"] = accountId, ["refresh"] = refresh }, () =>
            FacebookPages.TryGetValue(accountId, out var fp)
                ? ZernioReply.Of(new { pages = fp.Pages, selectedPageId = fp.Selected, cached = !refresh })
                : ZernioReply.Fail("Account not found, or not a facebook or metaads account", 404));

    public Task<ZernioReply> SelectFacebookPageAsync(string accountId, string pageId, CancellationToken ct = default) =>
        Do("SelectFacebookPage", new() { ["accountId"] = accountId, ["pageId"] = pageId }, () =>
        {
            if (!FacebookPages.TryGetValue(accountId, out var fp)) return ZernioReply.Fail("not found", 404);
            FacebookPages[accountId] = (fp.Pages, pageId);
            return ZernioReply.Of(new { message = "Facebook page updated successfully", selectedPage = new { id = pageId } });
        });

    /// <summary>Zernio answers the move but its listing still shows the old profile (eventual consistency).</summary>
    public bool StaleMoves { get; set; }

    public Task<ZernioReply> ListWebhooksAsync(CancellationToken ct = default) =>
        Do("ListWebhooks", new(), () => ZernioReply.Of(new { webhooks = Array.Empty<object>() }));

    public Task<ZernioReply> CreateWebhookAsync(string name, string url, string secret, IReadOnlyList<string> events, CancellationToken ct = default) =>
        Do("CreateWebhook", new() { ["url"] = url, ["secret"] = secret, ["events"] = events },
            () => ZernioReply.Of(new { success = true, webhook = new { _id = "wh-1", url, events } }));

    public Task<ZernioReply> UpdateWebhookAsync(string webhookId, string url, string secret, IReadOnlyList<string> events, CancellationToken ct = default) =>
        Do("UpdateWebhook", new() { ["webhookId"] = webhookId, ["url"] = url, ["secret"] = secret },
            () => ZernioReply.Of(new { success = true, webhook = new { _id = webhookId, url, events } }));

    public Task<ZernioReply> SendInboxMessageAsync(string accountId, string conversationId, Dictionary<string, object?> body,
        CancellationToken ct = default) =>
        Do("SendInboxMessage", new(body) { ["accountId"] = accountId, ["conversationId"] = conversationId },
            () => ZernioReply.Of(new { success = true, data = new { messageId = Next("wamid"), conversationId } }));

    public Task<ZernioReply> CreateConversationAsync(Dictionary<string, object?> body, CancellationToken ct = default) =>
        Do("CreateConversation", body, () => ZernioReply.Of(new
        {
            success = true, data = new { messageId = Next("wamid"), conversationId = Next("conv") },
        }, 201));

    public Task<ZernioReply> SendSmsAsync(string from, string to, string text, IReadOnlyList<string>? mediaUrls = null,
        CancellationToken ct = default) =>
        Do("SendSms", new() { ["from"] = from, ["to"] = to, ["text"] = text, ["mediaUrls"] = mediaUrls },
            () => ZernioReply.Of(new { id = Next("sms"), conversationId = Next("conv"), status = "sent" }));

    public Task<ZernioReply> ReactAsync(string accountId, string conversationId, string messageId, string emoji,
        CancellationToken ct = default) =>
        Do("React", new() { ["accountId"] = accountId, ["conversationId"] = conversationId, ["messageId"] = messageId, ["emoji"] = emoji },
            () => ZernioReply.Of(new { success = true, platformMessageId = messageId }));

    /// <summary>The WhatsApp templates GET /v1/whatsapp/templates answers (Meta-shaped).</summary>
    public List<object> Templates { get; } = new();

    public Task<ZernioReply> ListWhatsAppTemplatesAsync(string accountId, CancellationToken ct = default) =>
        Do("ListWhatsAppTemplates", new() { ["accountId"] = accountId }, () => ZernioReply.Of(new { success = true, templates = Templates }));

    /// <summary>Files a download answers, by URL.</summary>
    public ConcurrentDictionary<string, (byte[] Bytes, string Mime)> Files { get; } = new();

    public Task<(byte[]? Bytes, string Mime, string Error)> DownloadAsync(string url, long maxBytes, CancellationToken ct = default)
    {
        Calls.Enqueue(("Download", new() { ["url"] = url }));
        return Task.FromResult<(byte[]?, string, string)>(Files.TryGetValue(url, out var f) ? (f.Bytes, f.Mime, "") : (null, "", "not found"));
    }

    /// <summary>What PATCH /v1/connect/telegram answers next (null = pending).</summary>
    public object? TelegramStatus { get; set; }

    public Task<ZernioReply> TelegramCodeAsync(string profileId, CancellationToken ct = default) =>
        Do("TelegramCode", new() { ["profileId"] = profileId }, () => ZernioReply.Of(new
        {
            code = "ZRN-ABC123", expiresAt = "2026-11-10T12:15:00.000Z", expiresIn = 900, botUsername = "ZernioBot",
            instructions = new[] { "1. Add @ZernioBot as an administrator", "2. Send ZRN-ABC123 to @ZernioBot" },
        }));

    public Task<ZernioReply> TelegramStatusAsync(string code, CancellationToken ct = default) =>
        Do("TelegramStatus", new() { ["code"] = code }, () => ZernioReply.Of(TelegramStatus ?? new { status = "pending", expiresIn = 600 }));

    public Task<ZernioReply> TelegramConnectAsync(string profileId, string chatId, CancellationToken ct = default) =>
        Do("TelegramConnect", new() { ["profileId"] = profileId, ["chatId"] = chatId }, () => ZernioReply.Of(new
        {
            message = "Telegram channel connected successfully",
            account = new { _id = Next("ztg"), platform = "telegram", username = "kedaibot", displayName = "Kedai", isActive = true, chatType = "group" },
        }));

    public Task<ZernioReply> WhatsAppCredentialsAsync(string profileId, string accessToken, string wabaId, string phoneNumberId,
        string? pin, CancellationToken ct = default) =>
        Do("WhatsAppCredentials", new() { ["profileId"] = profileId, ["accessToken"] = accessToken, ["wabaId"] = wabaId, ["phoneNumberId"] = phoneNumberId, ["pin"] = pin },
            () => ZernioReply.Of(new
            {
                message = "WhatsApp connected successfully",
                account = new
                {
                    accountId = Next("zwa"), platform = "whatsapp", username = "+60 12-345 6789", displayName = "Kedai Kopi",
                    isActive = true, phoneNumber = "+60 12-345 6789", verifiedName = "Kedai Kopi", qualityRating = "GREEN",
                },
            }));

    public List<object> PhoneNumbers { get; } = new();

    public Task<ZernioReply> ListPhoneNumbersAsync(string? profileId = null, CancellationToken ct = default) =>
        Do("ListPhoneNumbers", new() { ["profileId"] = profileId }, () => ZernioReply.Of(new { numbers = PhoneNumbers }));

    public Task<ZernioReply> EnableSmsAsync(string phoneNumberId, CancellationToken ct = default) =>
        Do("EnableSms", new() { ["phoneNumberId"] = phoneNumberId }, () => ZernioReply.Of(new
        {
            enabled = true, id = "zsms-" + phoneNumberId, phoneNumber = "+60123000111", isActive = true, country = "MY",
            smsCapable = true, needsRegistration = false,
        }));

    /// <summary>What GET /v1/phone-numbers/countries quotes (the price before a purchase).</summary>
    public List<object> Countries { get; } = new();

    /// <summary>Numbers a release took away, by id (so a test can assert what was released).</summary>
    public List<string> Released { get; } = new();

    public Task<ZernioReply> GetPhoneNumberAsync(string phoneNumberId, CancellationToken ct = default) =>
        Do("GetPhoneNumber", new() { ["phoneNumberId"] = phoneNumberId }, () =>
        {
            var hit = PhoneNumbers.Select(n => JsonSerializer.SerializeToElement(n))
                .FirstOrDefault(n => ZernioJson.Str(n, "_id") == phoneNumberId || ZernioJson.Str(n, "id") == phoneNumberId);
            return hit.ValueKind == JsonValueKind.Object
                ? new ZernioReply(200, hit, "")
                : ZernioReply.Fail("Phone number not found", 404);
        });

    public Task<ZernioReply> PhoneNumberCountriesAsync(CancellationToken ct = default) =>
        Do("PhoneNumberCountries", new(), () => ZernioReply.Of(new { countries = Countries }));

    public Task<ZernioReply> PurchasePhoneNumberAsync(string profileId, string country, string? areaCode,
        string? phoneNumber, bool wantsSms, bool connectWhatsapp, string? purchaseIntentId,
        CancellationToken ct = default) =>
        Do("PurchasePhoneNumber", new()
        {
            ["profileId"] = profileId, ["country"] = country, ["areaCode"] = areaCode,
            ["phoneNumber"] = phoneNumber, ["wantsSms"] = wantsSms, ["connectWhatsapp"] = connectWhatsapp,
            ["purchaseIntentId"] = purchaseIntentId,
        }, () =>
        {
            var monthly = Countries.Select(c => JsonSerializer.SerializeToElement(c))
                .Where(c => ZernioJson.Str(c, "code") == country).Select(c => ZernioJson.Int(c, "monthlyCents"))
                .FirstOrDefault();
            var bought = new Dictionary<string, object?>
            {
                ["id"] = Next("znum"), ["_id"] = Next("znum"),
                ["phoneNumber"] = phoneNumber ?? "+1415555" + Interlocked.Increment(ref _n).ToString("0000"),
                ["country"] = country, ["status"] = "active", ["monthlyCents"] = monthly,
                ["hostedByZernio"] = true, ["smsCapable"] = wantsSms, ["profileId"] = profileId,
            };
            PhoneNumbers.Add(bought);
            return ZernioReply.Of(new { message = "Phone number provisioned", phoneNumber = bought });
        });

    public Task<ZernioReply> ReleasePhoneNumberAsync(string phoneNumberId, CancellationToken ct = default) =>
        Do("ReleasePhoneNumber", new() { ["phoneNumberId"] = phoneNumberId }, () =>
        {
            var gone = PhoneNumbers.Where(n => ZernioJson.Str(JsonSerializer.SerializeToElement(n), "_id") == phoneNumberId
                                               || ZernioJson.Str(JsonSerializer.SerializeToElement(n), "id") == phoneNumberId)
                .ToList();
            if (gone.Count == 0) return ZernioReply.Fail("Phone number not found", 404);
            foreach (var g in gone) PhoneNumbers.Remove(g);
            Released.Add(phoneNumberId);
            return ZernioReply.Of(new { message = "Phone number released" });
        });

    public Task<ZernioReply> PresignMediaAsync(string filename, string contentType, long size, CancellationToken ct = default) =>
        Do("Presign", new() { ["filename"] = filename, ["contentType"] = contentType, ["size"] = size },
            () => ZernioReply.Of(new { uploadUrl = "https://upload.example/put", publicUrl = $"https://media.zernio.com/temp/{filename}", expiresIn = 3600 }));

    public Task<ZernioReply> UploadMediaAsync(string uploadUrl, byte[] bytes, string contentType, CancellationToken ct = default) =>
        Do("Upload", new() { ["uploadUrl"] = uploadUrl, ["bytes"] = bytes.Length, ["contentType"] = contentType }, () => new ZernioReply(200, default, ""));
}

/// <summary>The API host with the fake Zernio in place of the real client.</summary>
public class ZernioFactory : ApiFactory
{
    public FakeZernioClient Zernio { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<IZernioClient>(Zernio));
    }
}

/// <summary>
/// ChatBranch × Zernio as a managed connection: the signed webhook, account mirroring onto native
/// messaging inboxes, entity profiles, outlet moves and a Messenger account's Facebook Pages.
/// Social-media features (posts, comments, reviews) are out of scope and must stay out.
/// Every Zernio call goes to <see cref="FakeZernioClient"/>.
/// </summary>
public class ZernioTests : IClassFixture<ZernioFactory>
{
    private readonly ZernioFactory _f;
    private const string Secret = "zernio-test-secret";

    public ZernioTests(ZernioFactory f)
    {
        _f = f;
        _ = _f.CreateClient();
        _f.Zernio.Reset();
        InDb(db => { new ScopingService(db).SetSetting(WebhookSecretSetting, Secret); return 0; });
    }

    private const string WebhookSecretSetting = "zernio_webhook_secret";
    private static string U() => Guid.NewGuid().ToString("N")[..10];

    private T InDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<HttpClient> Admin() => SettingsTestKit.Client(_f, "admin", "admin123");

    /// <summary>A managed inbox (native channel, provider zernio) seeded straight into the mirror.</summary>
    private ChannelAccount Account(string platform, int? entityId = null)
    {
        return InDb(db =>
        {
            var a = new ChannelAccount
            {
                Channel = ZernioNative.ChannelFor(platform)!, Provider = Providers.Zernio, ExternalId = "acc-" + U(),
                Name = $"@shop ({platform})", EntityId = entityId, CreatedAt = DateTime.UtcNow, HealthStatus = Health.Healthy,
                Config = new Dictionary<string, object?>
                {
                    ["platform"] = platform, ["username"] = "shop", ["zernio_status"] = "connected",
                },
            };
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a;
        });
    }

    private async Task<HttpResponseMessage> Hook(string eventName, object body, string? secret = Secret,
        string? eventId = null)
    {
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/zernio/webhook")
        {
            Content = new ByteArrayContent(raw) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        req.Headers.TryAddWithoutValidation("X-Zernio-Event", eventName);
        req.Headers.TryAddWithoutValidation("X-Zernio-Event-Id", eventId ?? Guid.NewGuid().ToString());
        if (secret != null) req.Headers.TryAddWithoutValidation("X-Zernio-Signature", ZernioWebhook.Sign(secret, raw));
        return await _f.CreateClient().SendAsync(req);
    }

    /// <summary>An Instagram DM to a managed Instagram inbox (the IGSID keys the thread).</summary>
    private static object Dm(ChannelAccount a, string igsid, string text, string? pmid = null) => new
    {
        id = Guid.NewGuid().ToString(),
        @event = "message.received",
        message = new
        {
            id = "m-" + U(), conversationId = "conv-" + igsid, platform = "instagram",
            platformMessageId = pmid ?? "pm-" + U(), direction = "incoming", text,
            attachments = Array.Empty<object>(), sender = new { id = igsid, name = "Aisyah" },
            sentAt = DateTime.UtcNow.ToString("o"),
        },
        conversation = new { id = "conv-" + igsid, platformConversationId = igsid, status = "active" },
        account = new { id = a.ExternalId, accountId = a.ExternalId, platform = "instagram", username = "shop" },
        timestamp = DateTime.UtcNow.ToString("o"),
    };

    // ---- the webhook: signature, dedupe, out-of-scope events ----------------------------

    [Fact]
    public async Task The_webhook_needs_a_valid_signature()
    {
        var a = Account("instagram");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook("message.received", Dm(a, "ig-" + U(), "hi"), secret: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook("message.received", Dm(a, "ig-" + U(), "hi"), secret: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Hook("message.received", Dm(a, "ig-" + U(), "hi"))).StatusCode);

        // no secret registered = closed, never "accept unsigned"
        InDb(db => { new ScopingService(db).SetSetting(WebhookSecretSetting, ""); return 0; });
        var closed = await Hook("message.received", Dm(a, "ig-" + U(), "hi"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.StatusCode);
        Assert.Contains("not_configured", await closed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_redelivered_event_is_stored_once()
    {
        var a = Account("instagram");
        var igsid = "ig-" + U();
        var eventId = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await Hook("message.received", Dm(a, igsid, "Ada halal?", "pm-1"), eventId: eventId)).StatusCode);
        var again = await Hook("message.received", Dm(a, igsid, "Ada halal?", "pm-1"), eventId: eventId);
        Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicate").GetBoolean());
        var thread = InDb(db => db.Threads.AsNoTracking().Single(t => t.Channel == ChannelIds.Instagram && t.Key == igsid));
        Assert.Equal(1, InDb(db => db.Messages.Count(m => m.ThreadId == thread.Id)));
    }

    [Fact]
    public async Task Social_media_events_are_acknowledged_and_ignored()
    {
        var a = Account("instagram");
        var before = InDb(db => db.Threads.Count());
        foreach (var ev in new[] { "comment.received", "review.new", "review.updated", "post.published", "post.failed" })
        {
            var res = await Hook(ev, new
            {
                id = Guid.NewGuid().ToString(), @event = ev,
                comment = new { id = "c-" + U(), text = "nice", platform = "instagram" },
                account = new { id = a.ExternalId, accountId = a.ExternalId, platform = "instagram" },
            });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        Assert.Equal(before, InDb(db => db.Threads.Count()));
        // what the registered webhook subscribes to is messaging only
        Assert.DoesNotContain(ZernioWebhook.Events, e => e.StartsWith("comment.") || e.StartsWith("review.") || e.StartsWith("post."));
    }

    // ---- accounts + profiles -------------------------------------------------------

    [Fact]
    public async Task Accounts_are_mirrored_as_native_inboxes_on_their_entitys_profile()
    {
        var entity = InDb(db => db.Entities.First(e => e.Slug == "main").Id);
        var pid = "prof-main-" + U();
        _f.Zernio.Profiles.Add(new { _id = "prof-default", name = "Default", isDefault = true, externalId = (string?)null });
        _f.Zernio.Profiles.Add(new { _id = pid, name = "Main", isDefault = false, externalId = $"cb-entity-{entity}" });
        var zid = "zacc-" + U();
        var tiktok = "zacc-tt-" + U();
        _f.Zernio.Accounts.Add(new
        {
            _id = zid, platform = "telegram", username = "kopi_bot", displayName = "Kopi", profilePicture = "https://cdn.example/t.jpg",
            isActive = true, profileId = new { _id = pid, name = "Main" },
        });
        // a social network account in the same profile is not ChatBranch's business
        _f.Zernio.Accounts.Add(new
        {
            _id = tiktok, platform = "tiktok", username = "kopi.tt", isActive = true, profileId = new { _id = pid, name = "Main" },
        });
        var c = await Admin();
        var res = await c.GetFromJsonAsync<JsonElement>("/api/social/accounts?entity=main&refresh=1");
        var row = Assert.Single(res.GetProperty("accounts").EnumerateArray(), r => r.GetProperty("id").GetString() == zid);
        Assert.Equal("telegram", row.GetProperty("channel").GetString());
        Assert.False(row.TryGetProperty("can_publish", out _));
        Assert.DoesNotContain(res.GetProperty("accounts").EnumerateArray(), r => r.GetProperty("id").GetString() == tiktok);
        Assert.False(InDb(db => db.ChannelAccounts.Any(a => a.ExternalId == tiktok)));

        var mirrored = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.ExternalId == zid));
        Assert.Equal(ChannelIds.Telegram, mirrored.Channel);
        Assert.Equal(Providers.Zernio, mirrored.Provider);
        Assert.Equal(entity, mirrored.EntityId);
        Assert.Equal("@kopi_bot (Telegram)", mirrored.Name);

        // account.disconnected marks the inbox down (its conversations stay)
        await Hook("account.disconnected", new
        {
            id = Guid.NewGuid().ToString(), @event = "account.disconnected",
            account = new { accountId = zid, profileId = pid, platform = "telegram", username = "kopi_bot", disconnectionType = "unintentional", reason = "token expired" },
        });
        Assert.Equal(Health.Disconnected, InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.ExternalId == zid).HealthStatus));

        // disconnect from ChatBranch: Zernio is told, the inbox goes
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/social/accounts/{zid}?confirm=true")).StatusCode);
        Assert.Single(_f.Zernio.Of("DeleteAccount"));
        Assert.False(InDb(db => db.ChannelAccounts.Any(a => a.ExternalId == zid)));
    }

    [Fact]
    public async Task Connecting_for_an_entity_creates_its_profile_with_our_external_id()
    {
        var (entityId, slug) = NewEntity("Zernio Branch");
        var c = await Admin();
        var res = await c.PostAsJsonAsync("/api/social/connect", new { platform = "ig", entity = slug });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("https://auth.example/instagram", body.GetProperty("url").GetString());
        Assert.Equal("instagram", body.GetProperty("channel").GetString());

        var made = Assert.Single(_f.Zernio.Of("CreateProfile"));
        Assert.Equal($"cb-entity-{entityId}", made["externalId"]);
        var connect = Assert.Single(_f.Zernio.Of("ConnectUrl"));
        Assert.Equal("instagram", connect["platform"]);
        Assert.EndsWith("/channels?zernio=connected", connect["redirect_url"]?.ToString());
        Assert.Equal(1, InDb(db => db.SocialProfiles.Count(p => p.EntityId == entityId)));

        // the second connect reuses it
        await c.PostAsJsonAsync("/api/social/connect", new { platform = "messenger", entity = slug });
        Assert.Single(_f.Zernio.Of("CreateProfile"));
        // social networks and unknown platforms are refused before Zernio
        foreach (var refused in new[] { "myspace", "googlebusiness", "tiktok", "twitter", "linkedin" })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/social/connect", new { platform = refused })).StatusCode);
        Assert.Equal(2, _f.Zernio.Of("ConnectUrl").Count);
    }

    // ---- binding an account to an outlet (PATCH /api/social/accounts/{id}) ---------------

    private (int Id, string Slug) NewEntity(string prefix = "Outlet")
    {
        var name = $"{prefix} {U()}";
        return InDb(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var e = new Entity { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), TenantId = tenant.Id };
            db.Entities.Add(e);
            db.SaveChanges();
            return (e.Id, e.Slug);
        });
    }

    /// <summary>A Zernio account in the key's default profile, mirrored (organization level) by a sync.</summary>
    private async Task<ChannelAccount> OrgAccount(HttpClient c, string platform = "instagram")
    {
        _f.Zernio.Profiles.Add(new { _id = "prof-default", name = "Default", isDefault = true, externalId = (string?)null });
        var zid = "zacc-" + U();
        _f.Zernio.Accounts.Add(new
        {
            _id = zid, platform, username = "kedai." + U(), displayName = "Kedai", isActive = true,
            profileId = new { _id = "prof-default", name = "Default" },
        });
        var res = await c.GetAsync("/api/social/accounts?refresh=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var row = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Provider == Providers.Zernio && a.ExternalId == zid));
        Assert.Equal(ZernioNative.ChannelFor(platform), row.Channel);
        Assert.Null(row.EntityId);
        return row;
    }

    private async Task<List<string>> Listed(HttpClient c, string entitySlug, bool refresh = false)
    {
        var res = await c.GetFromJsonAsync<JsonElement>(
            $"/api/social/accounts?entity={entitySlug}{(refresh ? "&refresh=1" : "")}");
        return res.GetProperty("accounts").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();
    }

    private int SeedThread(string channel, string key, int accountId, int? entityId = null) => InDb(db =>
    {
        var t = new Thread
        {
            Channel = channel, Key = key, Name = "someone", AccountId = accountId, EntityId = entityId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Threads.Add(t);
        db.SaveChanges();
        return t.Id;
    });

    private static Task<HttpResponseMessage> Assign(HttpClient c, string zid, object? entity) =>
        c.PatchAsJsonAsync($"/api/social/accounts/{zid}", new Dictionary<string, object?> { ["entity"] = entity });

    [Fact]
    public async Task Assigning_an_account_to_an_outlet_moves_it_to_that_profile_with_its_conversations()
    {
        var c = await Admin();
        var (e1, slug1) = NewEntity();
        var (_, slug2) = NewEntity();
        var acct = await OrgAccount(c);
        var zid = acct.ExternalId;
        // organization-level: every outlet inherits it
        Assert.Contains(zid, await Listed(c, slug1));
        Assert.Contains(zid, await Listed(c, slug2));

        var dm = SeedThread(ChannelIds.Instagram, "igsid-" + U(), acct.Id);
        var other = Account("facebook"); // another organization-level inbox
        var unrelated = SeedThread(ChannelIds.Facebook, "psid-" + U(), other.Id);
        _f.Zernio.Calls.Clear();

        var res = await Assign(c, zid, slug1);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var row = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(zid, row.GetProperty("id").GetString());
        Assert.Equal(e1, row.GetProperty("entity_id").GetInt32());
        Assert.Equal(slug1, row.GetProperty("entity_slug").GetString());
        Assert.Equal("instagram", row.GetProperty("platform").GetString());

        // the outlet's profile is created with our externalId, and the account moved into it
        var made = Assert.Single(_f.Zernio.Of("CreateProfile"));
        Assert.Equal($"cb-entity-{e1}", made["externalId"]);
        var profileId = InDb(db => db.SocialProfiles.Single(p => p.EntityId == e1).ProfileId);
        var move = Assert.Single(_f.Zernio.Of("MoveAccount"));
        Assert.Equal(zid, move["accountId"]);
        Assert.Equal(profileId, move["profileId"]);

        var mirrored = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == acct.Id));
        Assert.Equal(e1, mirrored.EntityId);
        Assert.Equal(profileId, ZernioKeys.Cfg(mirrored, ZernioKeys.ProfileIdKey));

        // its conversations follow; another inbox's stay
        Assert.Equal(e1, InDb(db => db.Threads.Single(t => t.Id == dm).EntityId));
        Assert.Null(InDb(db => db.Threads.Single(t => t.Id == unrelated).EntityId));
        Assert.True(InDb(db => db.AuditLogs.Any(l => l.Action == "account.entity" && l.Target.Contains(mirrored.Name))));

        // the outlet sees it, its sibling no longer does — and a sync keeps the binding
        Assert.Contains(zid, await Listed(c, slug1, refresh: true));
        Assert.DoesNotContain(zid, await Listed(c, slug2));
        Assert.Equal(e1, InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == acct.Id).EntityId));

        // the same outlet again: no new profile, no Zernio move
        Assert.Equal(HttpStatusCode.OK, (await Assign(c, zid, e1.ToString())).StatusCode);
        Assert.Single(_f.Zernio.Of("CreateProfile"));
        Assert.Single(_f.Zernio.Of("MoveAccount"));

        // back to the organization level: the default profile, inherited by every outlet again
        var back = await Assign(c, zid, null);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await back.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entity_id").ValueKind);
        Assert.Equal("prof-default", _f.Zernio.Of("MoveAccount").Last()["profileId"]);
        Assert.Null(InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == acct.Id).EntityId));
        Assert.Contains(zid, await Listed(c, slug1, refresh: true));
        Assert.Contains(zid, await Listed(c, slug2));
    }

    [Fact]
    public async Task A_sync_before_Zernio_reflects_the_move_keeps_the_new_binding()
    {
        var c = await Admin();
        var (e1, slug1) = NewEntity();
        var acct = await OrgAccount(c);
        _f.Zernio.StaleMoves = true; // Zernio still lists the account under the default profile
        Assert.Equal(HttpStatusCode.OK, (await Assign(c, acct.ExternalId, slug1)).StatusCode);
        Assert.Contains(acct.ExternalId, await Listed(c, slug1, refresh: true));
        var row = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == acct.Id));
        Assert.Equal(e1, row.EntityId);
        Assert.NotEqual("prof-default", ZernioKeys.Cfg(row, ZernioKeys.ProfileIdKey));
    }

    [Fact]
    public async Task Assigning_needs_channels_manage_and_both_levels_in_scope()
    {
        var admin = await Admin();
        var (e1, slug1) = NewEntity();
        var (e2, slug2) = NewEntity();
        var acct = await OrgAccount(admin);
        var mine = Account("facebook", e1);
        var theirs = Account("telegram", e2);

        // no channels.manage
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await Assign(agent, acct.ExternalId, slug1)).StatusCode);

        // bad input
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PatchAsJsonAsync($"/api/social/accounts/{acct.ExternalId}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assign(admin, acct.ExternalId, "no-such-outlet-" + U())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Assign(admin, "zacc-missing-" + U(), slug1)).StatusCode);

        // a company admin bound to e1
        var user = "co-admin-" + U();
        var uid = SettingsTestKit.CreateUser(_f, user, "pass12345", Roles.Admin);
        InDb(db =>
        {
            var profile = db.Profiles.Include(p => p.Entities).Single(p => p.UserId == uid);
            profile.Entities.Add(db.Entities.Single(e => e.Id == e1));
            db.SaveChanges();
            return 0;
        });
        var co = await SettingsTestKit.Client(_f, user, "pass12345");
        _f.Zernio.Calls.Clear();
        // another company's account: not even found
        Assert.Equal(HttpStatusCode.NotFound, (await Assign(co, theirs.ExternalId, slug1)).StatusCode);
        // its own account, to an outlet outside its companies or to the organization level: refused
        Assert.Equal(HttpStatusCode.Forbidden, (await Assign(co, mine.ExternalId, slug2)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Assign(co, mine.ExternalId, "")).StatusCode);
        // an organization-level account is not theirs to take
        Assert.Equal(HttpStatusCode.Forbidden, (await Assign(co, acct.ExternalId, slug1)).StatusCode);
        Assert.Empty(_f.Zernio.Of("MoveAccount"));
        Assert.Equal(e1, InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == mine.Id).EntityId));

        // the generic inbox PATCH cannot rebind a managed connection behind Zernio's back
        var generic = await admin.PatchAsJsonAsync($"/api/messaging/accounts/{mine.Id}", new { entity = slug2 });
        Assert.Equal(HttpStatusCode.Conflict, generic.StatusCode);
    }

    // ---- a managed Messenger account's Facebook Pages ---------------------------------

    [Fact]
    public async Task Facebook_pages_are_mirrored_and_one_is_selected_for_the_inbox()
    {
        var c = await Admin();
        _f.Zernio.Profiles.Add(new { _id = "prof-default", name = "Default", isDefault = true, externalId = (string?)null });
        var fb = "zfb-" + U();
        _f.Zernio.Accounts.Add(new
        {
            _id = fb, platform = "facebook", username = "kopi.group", displayName = "Kopi Group", isActive = true,
            profileId = new { _id = "prof-default", name = "Default" },
        });
        _f.Zernio.FacebookPages[fb] = (new List<object>
        {
            new { id = "P1", name = "Kopi KL", username = "kopikl", category = "Cafe", fan_count = 10 },
            new { id = "P2", name = "Kopi Penang", username = "kopipg", category = "Cafe", fan_count = 20 },
        }, "P1");

        // the sync mirrors the Messenger inbox and its Pages (refresh=1 re-reads Meta through Zernio)
        var all = await c.GetFromJsonAsync<JsonElement>("/api/social/accounts?refresh=1");
        var row = all.GetProperty("accounts").EnumerateArray().Single(r => r.GetProperty("id").GetString() == fb);
        Assert.Equal("facebook", row.GetProperty("channel").GetString());
        var pages = row.GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal(new[] { "P1", "P2" }.ToHashSet(), pages.Select(p => p.GetProperty("id").GetString()).ToHashSet());
        Assert.True(pages.Single(p => p.GetProperty("id").GetString() == "P1").GetProperty("selected").GetBoolean());
        Assert.Equal(true, _f.Zernio.Of("FacebookPages").Last()["refresh"]);
        var inbox = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.ExternalId == fb));
        Assert.Equal(ChannelIds.Facebook, inbox.Channel);
        Assert.Equal("Kopi KL", ZernioNative.Address(inbox));

        // switch the Page whose Messenger conversations reach the inbox
        var sel = await c.PostAsync($"/api/social/accounts/{fb}/pages/P2/select", null);
        Assert.Equal(HttpStatusCode.OK, sel.StatusCode);
        Assert.Equal("P2", _f.Zernio.Of("SelectFacebookPage").Single()["pageId"]);
        var selPages = (await sel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pages").EnumerateArray().ToList();
        Assert.True(selPages.Single(p => p.GetProperty("id").GetString() == "P2").GetProperty("selected").GetBoolean());
        Assert.False(selPages.Single(p => p.GetProperty("id").GetString() == "P1").GetProperty("selected").GetBoolean());
        Assert.Equal("Kopi Penang", ZernioNative.Address(InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.ExternalId == fb))));
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/api/social/accounts/{fb}/pages/P9/select", null)).StatusCode);

        // binding a Page to an outlet was a posting feature: gone
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PatchAsJsonAsync($"/api/social/accounts/{fb}/pages/P1", new { entity = "main" })).StatusCode);

        // permissions: no channels.manage
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/api/social/accounts/{fb}/pages/P1/select", null)).StatusCode);
    }

    // ---- settings + permissions --------------------------------------------------------

    [Fact]
    public async Task Social_media_is_gone_and_unconfigured_zernio_says_so()
    {
        var admin = await Admin();
        var who = await admin.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        var caps = who.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.DoesNotContain(caps, x => x!.StartsWith("social."));
        Assert.Contains("channels.manage", caps);

        // the posting and comment routes no longer exist
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/social/posts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/social/threads/comment:x:y/hide", new { })).StatusCode);

        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/social/connect", new { platform = "instagram" })).StatusCode);
        // the key's settings are superadmin's
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/zernio/status")).StatusCode);

        _f.Zernio.Configured = false;
        var res = await admin.GetAsync("/api/social/accounts");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(ChatBranch.Api.Endpoints.ZernioEndpoints.NotConfiguredCode,
            (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_key_is_saved_with_the_app_keys_masked_and_the_webhook_registers_with_a_secret()
    {
        var super = await SettingsTestKit.Client(_f, "superadmin", "super123");
        var save = await super.PostAsJsonAsync("/api/whatsapp/settings", new { zernio_api_key = "sk_test_0123456789abcdef" });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var masked = (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("zernio_api_key_masked").GetString();
        Assert.Equal("••••cdef", masked);
        Assert.Equal("sk_test_0123456789abcdef", InDb(db => new ScopingService(db).SettingValue("zernio_api_key")));

        var status = await super.GetFromJsonAsync<JsonElement>("/api/zernio/status");
        Assert.Equal("••••cdef", status.GetProperty("key_masked").GetString());
        Assert.DoesNotContain("1234567890", status.GetRawText());

        InDb(db => { var sc = new ScopingService(db); sc.SetSetting("zernio_webhook_secret", ""); sc.SetSetting("zernio_webhook_id", ""); return 0; });
        var reg = await super.PostAsJsonAsync("/api/zernio/webhook/register", new { base_url = "https://cb.example" });
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);
        var call = Assert.Single(_f.Zernio.Of("CreateWebhook"));
        Assert.Equal("https://cb.example/api/zernio/webhook", call["url"]);
        var secret = call["secret"]!.ToString()!;
        Assert.True(secret.Length >= 32);
        Assert.Equal(secret, InDb(db => new ScopingService(db).SettingValue("zernio_webhook_secret")));
        Assert.Equal("wh-1", InDb(db => new ScopingService(db).SettingValue("zernio_webhook_id")));
        // registering again updates the same webhook
        await super.PostAsJsonAsync("/api/zernio/webhook/register", new { base_url = "https://cb.example" });
        Assert.Single(_f.Zernio.Of("UpdateWebhook"));
        InDb(db => { var sc = new ScopingService(db); sc.SetSetting("zernio_api_key", ""); return 0; });
    }
}
