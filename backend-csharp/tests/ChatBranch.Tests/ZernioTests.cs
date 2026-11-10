using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Social;
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

    public Task<ZernioReply> ConnectUrlAsync(string platform, string profileId, string redirectUrl, CancellationToken ct = default) =>
        Do("ConnectUrl", new() { ["platform"] = platform, ["profileId"] = profileId, ["redirect_url"] = redirectUrl },
            () => ZernioReply.Of(new { authUrl = $"https://auth.example/{platform}?profile={profileId}" }));

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

    public Task<ZernioReply> ReplyToCommentAsync(string accountId, string postId, string commentId, string message, CancellationToken ct = default) =>
        Do("ReplyToComment", new() { ["accountId"] = accountId, ["postId"] = postId, ["commentId"] = commentId, ["message"] = message },
            () => ZernioReply.Of(new { success = true, data = new { commentId = Next("reply"), isReply = true } }));

    public Task<ZernioReply> HideCommentAsync(string accountId, string postId, string commentId, bool hidden, CancellationToken ct = default) =>
        Do("HideComment", new() { ["accountId"] = accountId, ["postId"] = postId, ["commentId"] = commentId, ["hidden"] = hidden },
            () => ZernioReply.Of(new { status = "success", commentId, hidden }));

    public Task<ZernioReply> PrivateReplyAsync(string accountId, string postId, string commentId, string message, CancellationToken ct = default) =>
        Do("PrivateReply", new() { ["accountId"] = accountId, ["postId"] = postId, ["commentId"] = commentId, ["message"] = message },
            () => ZernioReply.Of(new { success = true }));

    public Task<ZernioReply> ReplyToReviewAsync(string accountId, string reviewId, string message, CancellationToken ct = default) =>
        Do("ReplyToReview", new() { ["accountId"] = accountId, ["reviewId"] = reviewId, ["message"] = message },
            () => ZernioReply.Of(new { status = "success", reply = new { id = Next("rr"), text = message }, platform = "googlebusiness" }));

    public Task<ZernioReply> SendMessageAsync(string accountId, string conversationId, string message, CancellationToken ct = default) =>
        Do("SendMessage", new() { ["accountId"] = accountId, ["conversationId"] = conversationId, ["message"] = message },
            () => ZernioReply.Of(new { success = true, data = new { messageId = Next("pm"), conversationId } }));

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

    public Task<ZernioReply> CreatePostAsync(Dictionary<string, object?> body, CancellationToken ct = default) =>
        Do("CreatePost", body, () => ZernioReply.Of(new
        {
            post = new
            {
                _id = Next("zp"),
                status = body.ContainsKey("publishNow") ? "published" : body.ContainsKey("isDraft") ? "draft" : "scheduled",
                platforms = Platforms(body, body.ContainsKey("publishNow") ? "published" : "pending"),
            },
        }, 201));

    public Task<ZernioReply> GetPostAsync(string postId, CancellationToken ct = default) =>
        Do("GetPost", new() { ["postId"] = postId }, () => ZernioReply.Of(new { post = new { _id = postId, status = "scheduled" } }));

    public Task<ZernioReply> UpdatePostAsync(string postId, Dictionary<string, object?> body, CancellationToken ct = default) =>
        Do("UpdatePost", new(body) { ["postId"] = postId }, () => ZernioReply.Of(new { post = new { _id = postId, status = "scheduled" } }));

    public Task<ZernioReply> DeletePostAsync(string postId, CancellationToken ct = default) =>
        Do("DeletePost", new() { ["postId"] = postId }, () => ZernioReply.Of(new { message = "Post deleted" }));

    public Task<ZernioReply> RetryPostAsync(string postId, CancellationToken ct = default) =>
        Do("RetryPost", new() { ["postId"] = postId }, () => ZernioReply.Of(new
        {
            post = new { _id = postId, status = "published", platforms = new[] { new { platform = "instagram", accountId = "x", status = "published", platformPostUrl = "https://instagram.example/p/1" } } },
        }));

    public Task<ZernioReply> UnpublishPostAsync(string postId, string platform, string accountId, CancellationToken ct = default) =>
        Do("UnpublishPost", new() { ["postId"] = postId, ["platform"] = platform, ["accountId"] = accountId }, () => ZernioReply.Of(new { success = true }));

    public Task<ZernioReply> PresignMediaAsync(string filename, string contentType, long size, CancellationToken ct = default) =>
        Do("Presign", new() { ["filename"] = filename, ["contentType"] = contentType, ["size"] = size },
            () => ZernioReply.Of(new { uploadUrl = "https://upload.example/put", publicUrl = $"https://media.zernio.com/temp/{filename}", expiresIn = 3600 }));

    public Task<ZernioReply> UploadMediaAsync(string uploadUrl, byte[] bytes, string contentType, CancellationToken ct = default) =>
        Do("Upload", new() { ["uploadUrl"] = uploadUrl, ["bytes"] = bytes.Length, ["contentType"] = contentType }, () => new ZernioReply(200, default, ""));

    private static object[] Platforms(Dictionary<string, object?> body, string status) =>
        body.TryGetValue("platforms", out var p) && p is IEnumerable<Dictionary<string, object?>> list
            ? list.Select(x => (object)new
            {
                platform = x["platform"]?.ToString(), accountId = x["accountId"]?.ToString(), status,
                platformSpecificData = x.GetValueOrDefault("platformSpecificData"),
                platformPostUrl = status == "published" ? $"https://{x["platform"]}.example/p/1" : null,
            }).ToArray()
            : Array.Empty<object>();
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
/// ChatBranch × Zernio: the signed webhook, comments / reviews / DMs as inbox threads
/// whose replies reach Zernio, account mirroring, entity profiles and social posts.
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
        InDb(db => { new ScopingService(db).SetSetting(SocialEndpointsSecret, Secret); return 0; });
    }

    private const string SocialEndpointsSecret = "zernio_webhook_secret";
    private static string U() => Guid.NewGuid().ToString("N")[..10];

    private T InDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<HttpClient> Admin() => SettingsTestKit.Client(_f, "admin", "admin123");

    private ChannelAccount Account(string platform, int? entityId = null)
    {
        return InDb(db =>
        {
            var a = new ChannelAccount
            {
                Channel = ChannelIds.Social, Provider = Providers.Zernio, ExternalId = "acc-" + U(),
                Name = $"@shop ({platform})", EntityId = entityId, CreatedAt = DateTime.UtcNow, HealthStatus = Health.Healthy,
                Config = new Dictionary<string, object?>
                {
                    ["platform"] = platform, ["username"] = "shop", ["zernio_status"] = "connected", ["ai_default"] = "off",
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

    private static object Comment(ChannelAccount a, string commentId, string text, string? parent = null,
        bool own = false) => new
    {
        id = Guid.NewGuid().ToString(),
        @event = "comment.received",
        comment = new
        {
            id = commentId, postId = "zp-77", platformPostId = "ig-post-1", platform = "instagram", text,
            author = new { id = "u-9", username = "aisyah", name = "Aisyah", picture = "https://cdn.example/a.jpg", isOwnAccount = own },
            createdAt = "2026-11-10T08:00:00Z", isReply = parent != null, parentCommentId = parent,
        },
        post = new { id = "zp-77", platformPostId = "ig-post-1", content = "Our new menu!", imageUrl = "https://cdn.example/p.jpg", permalink = "https://instagram.com/p/1" },
        account = new { id = a.ExternalId, accountId = a.ExternalId, platform = "instagram", username = "shop" },
        timestamp = "2026-11-10T08:00:01Z",
    };

    private Thread? ThreadByKey(string key) =>
        InDb(db => db.Threads.AsNoTracking().FirstOrDefault(t => t.Channel == ChannelIds.Social && t.Key == key));

    // ---- vocabulary -------------------------------------------------------------

    [Fact]
    public void Keys_are_url_safe_and_split_back_into_kind_account_and_id()
    {
        Assert.Equal("comment:acc1:179", SocialChannels.ThreadKey("comment", "acc1", "179"));
        Assert.Equal(("review", "acc1", "789"), SocialChannels.ParseKey(
            SocialChannels.ThreadKey("review", "acc1", "accounts/1/locations/2/reviews/789")));
        var bsky = SocialChannels.KeyPart("at://did:plc:abc/app.bsky.feed.post/3k");
        Assert.DoesNotContain("/", bsky);
        Assert.Equal(("", "", "nope"), SocialChannels.ParseKey("nope"));
        Assert.Equal(1, SocialChannels.EntityFromExternalId("cb-entity-1"));
        Assert.Null(SocialChannels.EntityFromExternalId("tenant_42"));
        Assert.True(Data.Channels.MoreChannels.Handles(ChannelIds.Social));
    }

    // ---- the webhook: signature, dedupe -------------------------------------------

    [Fact]
    public async Task The_webhook_needs_a_valid_signature()
    {
        var a = Account("instagram");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook("comment.received", Comment(a, "c-" + U(), "hi"), secret: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Hook("comment.received", Comment(a, "c-" + U(), "hi"), secret: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Hook("comment.received", Comment(a, "c-" + U(), "hi"))).StatusCode);

        // no secret registered = closed, never "accept unsigned"
        InDb(db => { new ScopingService(db).SetSetting(SocialEndpointsSecret, ""); return 0; });
        var closed = await Hook("comment.received", Comment(a, "c-" + U(), "hi"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.StatusCode);
        Assert.Contains("not_configured", await closed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_redelivered_event_is_stored_once()
    {
        var a = Account("instagram");
        var cid = "c-" + U();
        var eventId = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await Hook("comment.received", Comment(a, cid, "Ada halal?"), eventId: eventId)).StatusCode);
        var again = await Hook("comment.received", Comment(a, cid, "Ada halal?"), eventId: eventId);
        Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicate").GetBoolean());
        var thread = ThreadByKey($"comment:{a.ExternalId}:{cid}")!;
        Assert.Equal(1, InDb(db => db.Messages.Count(m => m.ThreadId == thread.Id)));
    }

    // ---- comments ----------------------------------------------------------------

    [Fact]
    public async Task A_comment_opens_a_thread_and_a_reply_goes_back_as_a_comment_reply()
    {
        var a = Account("instagram");
        var cid = "c-" + U();
        await Hook("comment.received", Comment(a, cid, "Ada halal?"));
        var key = $"comment:{a.ExternalId}:{cid}";
        var thread = ThreadByKey(key);
        Assert.NotNull(thread);
        Assert.Equal(a.Id, thread!.AccountId);
        Assert.Null(thread.ContactId);              // public: never a contact
        Assert.False(thread.AiEnabled);             // the inbox's AI default is off

        var msg = InDb(db => db.Messages.AsNoTracking().Single(m => m.ThreadId == thread.Id));
        Assert.Equal("Ada halal?", msg.Text);
        Assert.Equal("Aisyah", msg.SenderName);

        // a reply to it joins the same thread
        await Hook("comment.received", Comment(a, "c2-" + U(), "Me too", parent: cid));
        Assert.Equal(2, InDb(db => db.Messages.Count(m => m.ThreadId == thread.Id)));

        var c = await Admin();
        var detail = await c.GetFromJsonAsync<JsonElement>($"/api/messaging/threads/social/{Uri.EscapeDataString(key)}");
        var social = detail.GetProperty("social");
        Assert.Equal("comment", social.GetProperty("kind").GetString());
        Assert.Equal("instagram", social.GetProperty("platform").GetString());
        Assert.Equal("https://instagram.com/p/1", social.GetProperty("post").GetProperty("permalink").GetString());
        Assert.Equal("Aisyah", social.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("Aisyah", detail.GetProperty("messages")[0].GetProperty("sender").GetProperty("name").GetString());

        var inbox = await c.GetFromJsonAsync<JsonElement>("/api/inbox/threads?channel=social");
        Assert.Contains(inbox.GetProperty("threads").EnumerateArray(),
            t => t.GetProperty("key").GetString() == key && t.GetProperty("social").GetProperty("kind").GetString() == "comment");

        var send = await c.PostAsJsonAsync($"/api/messaging/threads/social/{Uri.EscapeDataString(key)}/send", new { text = "Yes, halal!" });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal("sent", (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetProperty("status").GetString());
        var call = Assert.Single(_f.Zernio.Of("ReplyToComment"));
        Assert.Equal("ig-post-1", call["postId"]);
        Assert.Equal(cid, call["commentId"]);
        Assert.Equal(a.ExternalId, call["accountId"]);

        // the platform echoing our own reply back is not a new customer message
        var ourId = InDb(db => db.Messages.AsNoTracking().Where(m => m.ThreadId == thread.Id && m.Direction == "out")
            .Select(m => m.ExternalId).Single());
        await Hook("comment.received", Comment(a, ourId, "Yes, halal!", parent: cid, own: true));
        Assert.Equal(3, InDb(db => db.Messages.Count(m => m.ThreadId == thread.Id)));
    }

    [Fact]
    public async Task Hide_and_private_reply_reach_zernio()
    {
        var a = Account("facebook");
        var cid = "c-" + U();
        await Hook("comment.received", Comment(a, cid, "Spam spam"));
        var key = Uri.EscapeDataString($"comment:{a.ExternalId}:{cid}");
        var c = await Admin();

        var hide = await c.PostAsJsonAsync($"/api/social/threads/{key}/hide", new { hidden = true });
        Assert.Equal(HttpStatusCode.OK, hide.StatusCode);
        var call = Assert.Single(_f.Zernio.Of("HideComment"));
        Assert.Equal(true, call["hidden"]);
        Assert.Equal(cid, call["commentId"]);
        var detail = await c.GetFromJsonAsync<JsonElement>($"/api/messaging/threads/social/{key}");
        Assert.True(detail.GetProperty("social").GetProperty("hidden").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/social/threads/{key}/private-reply", new { text = "DM you" })).StatusCode);
        Assert.Single(_f.Zernio.Of("PrivateReply"));
        // platforms allow one private reply per comment
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/social/threads/{key}/private-reply", new { text = "again" })).StatusCode);
        // an agent without chats.send on that scope… has it; a stranger thread is 404
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/social/threads/comment%3Anope%3Anope/hide", new { hidden = true })).StatusCode);
    }

    // ---- reviews -------------------------------------------------------------------

    [Fact]
    public async Task A_google_review_becomes_a_thread_with_its_rating_and_a_reply_goes_to_the_review()
    {
        var a = Account("googlebusiness");
        var rid = $"accounts/1/locations/2/reviews/r{U()}";
        object Review(string text, int rating, object? reply = null) => new
        {
            id = Guid.NewGuid().ToString(), @event = "review.new",
            review = new
            {
                id = rid, platform = "googlebusiness", rating, text,
                reviewer = new { id = (string?)null, name = "Tan", profileImage = (string?)null },
                createdAt = "2026-11-09T10:00:00Z", hasReply = reply != null, reply,
            },
            account = new { id = a.ExternalId, accountId = a.ExternalId, platform = "googlebusiness", username = "Kopi HQ" },
            timestamp = "2026-11-09T10:00:01Z",
        };
        await Hook("review.new", Review("Slow service", 2));
        var key = SocialChannels.ThreadKey("review", a.ExternalId, rid);
        Assert.DoesNotContain("/", key);
        var c = await Admin();
        var detail = await c.GetFromJsonAsync<JsonElement>($"/api/messaging/threads/social/{Uri.EscapeDataString(key)}");
        Assert.Equal("review", detail.GetProperty("social").GetProperty("kind").GetString());
        Assert.Equal(2, detail.GetProperty("social").GetProperty("rating").GetInt32());

        await c.PostAsJsonAsync($"/api/messaging/threads/social/{Uri.EscapeDataString(key)}/send", new { text = "Sorry, Tan!" });
        var call = Assert.Single(_f.Zernio.Of("ReplyToReview"));
        Assert.Equal(rid, call["reviewId"]);     // the FULL review name, not the key's short form

        // the edit: rating up, text changed → a second inbound; the reply echo is not duplicated
        await Hook("review.updated", Review("Better now", 4, new { text = "Sorry, Tan!", createdAt = "2026-11-10T00:00:00Z" }));
        var thread = ThreadByKey(key)!;
        Assert.Equal(2, InDb(db => db.Messages.Count(m => m.ThreadId == thread.Id && m.Direction == "in")));
        Assert.Equal(1, InDb(db => db.Messages.Count(m => m.ThreadId == thread.Id && m.Direction == "out")));
        Assert.Equal(4, InDb(db => db.SocialThreads.Single(s => s.ThreadId == thread.Id).Rating));
    }

    // ---- DMs -----------------------------------------------------------------------

    private static object Dm(ChannelAccount a, string platform, string conv, string text) => new
    {
        id = Guid.NewGuid().ToString(), @event = "message.received",
        message = new
        {
            id = "zm-" + Guid.NewGuid().ToString("N")[..6], conversationId = conv, platform,
            platformMessageId = "pm-in-" + Guid.NewGuid().ToString("N")[..6], direction = "incoming", text,
            attachments = Array.Empty<object>(), sender = new { id = "s-1", name = "Bob", username = "bob" },
            sentAt = "2026-11-10T09:00:00Z", isRead = false,
        },
        conversation = new { id = conv, platformConversationId = "p-" + conv, status = "active" },
        account = new { id = a.ExternalId, accountId = a.ExternalId, platform, username = "shop" },
        timestamp = "2026-11-10T09:00:01Z",
    };

    [Fact]
    public async Task A_dm_is_skipped_when_the_entity_has_a_native_inbox_for_that_platform()
    {
        var entity = InDb(db => db.Entities.First(e => e.Slug == "main").Id);
        var ig = Account("instagram", entity);
        InDb(db =>
        {
            db.ChannelAccounts.Add(new ChannelAccount
            {
                Channel = ChannelIds.Instagram, Name = "native ig " + U(), EntityId = entity, CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
            return 0;
        });
        var conv = "conv-" + U();
        var res = await Hook("message.received", Dm(ig, "instagram", conv, "hello"));
        Assert.Equal("skipped", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
        Assert.Null(ThreadByKey($"dm:{ig.ExternalId}:{conv}"));

        // X has no native ChatBranch channel: its DMs land here, and a reply is a Zernio message
        var x = Account("twitter", entity);
        var conv2 = "conv-" + U();
        await Hook("message.received", Dm(x, "twitter", conv2, "price?"));
        var key = $"dm:{x.ExternalId}:{conv2}";
        Assert.NotNull(ThreadByKey(key));
        var c = await Admin();
        await c.PostAsJsonAsync($"/api/messaging/threads/social/{Uri.EscapeDataString(key)}/send", new { text = "RM10" });
        var call = Assert.Single(_f.Zernio.Of("SendMessage"));
        Assert.Equal(conv2, call["conversationId"]);

        // message.failed for our send marks it failed
        var ext = InDb(db => db.Messages.Where(m => m.Thread!.Key == key && m.Direction == "out").Select(m => m.ExternalId).Single());
        await Hook("message.failed", new
        {
            id = Guid.NewGuid().ToString(), @event = "message.failed",
            message = new { platformMessageId = ext }, error = new { code = "131026", message = "unreachable" },
            account = new { id = x.ExternalId },
        });
        Assert.Equal("failed", InDb(db => db.Messages.Single(m => m.ExternalId == ext).Status));
    }

    // ---- accounts + profiles -------------------------------------------------------

    [Fact]
    public async Task Accounts_are_mirrored_as_channel_accounts_on_their_entitys_profile()
    {
        var entity = InDb(db => db.Entities.First(e => e.Slug == "main").Id);
        var pid = "prof-main-" + U();
        _f.Zernio.Profiles.Add(new { _id = "prof-default", name = "Default", isDefault = true, externalId = (string?)null });
        _f.Zernio.Profiles.Add(new { _id = pid, name = "Main", isDefault = false, externalId = $"cb-entity-{entity}" });
        var zid = "zacc-" + U();
        _f.Zernio.Accounts.Add(new
        {
            _id = zid, platform = "tiktok", username = "kopi.tt", displayName = "Kopi", profilePicture = "https://cdn.example/t.jpg",
            profileUrl = "https://tiktok.com/@kopi.tt", isActive = true, profileId = new { _id = pid, name = "Main" },
        });
        var c = await Admin();
        var res = await c.GetFromJsonAsync<JsonElement>("/api/social/accounts?entity=main&refresh=1");
        var row = Assert.Single(res.GetProperty("accounts").EnumerateArray(), r => r.GetProperty("id").GetString() == zid);
        Assert.Equal("tiktok", row.GetProperty("platform").GetString());
        Assert.True(row.GetProperty("can_comments").GetBoolean());
        Assert.False(row.GetProperty("can_reviews").GetBoolean());

        var mirrored = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Channel == ChannelIds.Social && a.ExternalId == zid));
        Assert.Equal(ChannelIds.Social, mirrored.Channel);
        Assert.Equal(Providers.Zernio, mirrored.Provider);
        Assert.Equal(entity, mirrored.EntityId);
        Assert.Equal("@kopi.tt (TikTok)", mirrored.Name);

        // account.disconnected marks the inbox down (its conversations stay)
        await Hook("account.disconnected", new
        {
            id = Guid.NewGuid().ToString(), @event = "account.disconnected",
            account = new { accountId = zid, profileId = pid, platform = "tiktok", username = "kopi.tt", disconnectionType = "unintentional", reason = "token expired" },
        });
        Assert.Equal(Health.Disconnected, InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Channel == ChannelIds.Social && a.ExternalId == zid).HealthStatus));

        // disconnect from ChatBranch: Zernio is told, the inbox goes
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/social/accounts/{zid}?confirm=true")).StatusCode);
        Assert.Single(_f.Zernio.Of("DeleteAccount"));
        Assert.False(InDb(db => db.ChannelAccounts.Any(a => a.ExternalId == zid)));
    }

    [Fact]
    public async Task Connecting_for_an_entity_creates_its_profile_with_our_external_id()
    {
        var name = "Zernio Branch " + U();
        var entityId = InDb(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var e = new Entity { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), TenantId = tenant.Id };
            db.Entities.Add(e);
            db.SaveChanges();
            return e.Id;
        });
        var slug = InDb(db => db.Entities.Single(e => e.Id == entityId).Slug);
        var c = await Admin();
        var res = await c.PostAsJsonAsync("/api/social/connect", new { platform = "x", entity = slug });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("https://auth.example/twitter", body.GetProperty("url").GetString());

        var made = Assert.Single(_f.Zernio.Of("CreateProfile"));
        Assert.Equal($"cb-entity-{entityId}", made["externalId"]);
        var connect = Assert.Single(_f.Zernio.Of("ConnectUrl"));
        Assert.Equal("twitter", connect["platform"]);
        Assert.EndsWith("/channels?zernio=connected", connect["redirect_url"]?.ToString());
        Assert.Equal(1, InDb(db => db.SocialProfiles.Count(p => p.EntityId == entityId)));

        // the second connect reuses it
        await c.PostAsJsonAsync("/api/social/connect", new { platform = "instagram", entity = slug });
        Assert.Single(_f.Zernio.Of("CreateProfile"));
        // an unknown platform is refused before Zernio
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/social/connect", new { platform = "myspace" })).StatusCode);
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
        var row = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Channel == ChannelIds.Social && a.ExternalId == zid));
        Assert.Null(row.EntityId);
        return row;
    }

    private async Task<List<string>> Listed(HttpClient c, string entitySlug, bool refresh = false)
    {
        var res = await c.GetFromJsonAsync<JsonElement>(
            $"/api/social/accounts?entity={entitySlug}{(refresh ? "&refresh=1" : "")}");
        return res.GetProperty("accounts").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();
    }

    private int SeedThread(string key, int accountId, int? entityId = null) => InDb(db =>
    {
        var t = new Thread
        {
            Channel = ChannelIds.Social, Key = key, Name = "someone", AccountId = accountId, EntityId = entityId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Threads.Add(t);
        db.SaveChanges();
        return t.Id;
    });

    private int SeedPost(string status, int? entityId, params string[] accountIds) => InDb(db =>
    {
        var p = new SocialPost
        {
            EntityId = entityId, Content = "post " + U(), AccountIds = accountIds.ToList(),
            Platforms = accountIds.Select(_ => "instagram").ToList(), Status = status,
            ScheduledAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.SocialPosts.Add(p);
        db.SaveChanges();
        return p.Id;
    });

    private static Task<HttpResponseMessage> Assign(HttpClient c, string zid, object? entity) =>
        c.PatchAsJsonAsync($"/api/social/accounts/{zid}", new Dictionary<string, object?> { ["entity"] = entity });

    [Fact]
    public async Task Assigning_an_account_to_an_outlet_moves_it_to_that_profile_with_its_threads_and_pending_posts()
    {
        var c = await Admin();
        var (e1, slug1) = NewEntity();
        var (_, slug2) = NewEntity();
        var acct = await OrgAccount(c);
        var zid = acct.ExternalId;
        // organization-level: every outlet inherits it
        Assert.Contains(zid, await Listed(c, slug1));
        Assert.Contains(zid, await Listed(c, slug2));

        var comment = SeedThread($"comment:{zid}:c-{U()}", acct.Id);
        var dm = SeedThread($"dm:{zid}:conv-{U()}", acct.Id);
        var unrelated = SeedThread($"comment:zacc-other-{U()}:c-{U()}", acct.Id);
        var other = Account("facebook"); // another organization-level account
        var scheduled = SeedPost(SocialPosts.Scheduled, null, zid);
        var draft = SeedPost(SocialPosts.Draft, null, zid);
        var published = SeedPost(SocialPosts.Published, null, zid);
        var mixed = SeedPost(SocialPosts.Scheduled, null, zid, other.ExternalId);
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

        var mirrored = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Channel == ChannelIds.Social && a.ExternalId == zid));
        Assert.Equal(e1, mirrored.EntityId);
        Assert.Equal(profileId, SocialChannels.Cfg(mirrored, SocialChannels.ProfileIdKey));

        // its conversations and not-yet-published posts follow; the rest stay
        Assert.Equal(e1, InDb(db => db.Threads.Single(t => t.Id == comment).EntityId));
        Assert.Equal(e1, InDb(db => db.Threads.Single(t => t.Id == dm).EntityId));
        Assert.Null(InDb(db => db.Threads.Single(t => t.Id == unrelated).EntityId));
        Assert.Equal(e1, InDb(db => db.SocialPosts.Single(p => p.Id == scheduled).EntityId));
        Assert.Equal(e1, InDb(db => db.SocialPosts.Single(p => p.Id == draft).EntityId));
        Assert.Null(InDb(db => db.SocialPosts.Single(p => p.Id == published).EntityId));
        Assert.Null(InDb(db => db.SocialPosts.Single(p => p.Id == mixed).EntityId));
        Assert.True(InDb(db => db.AuditLogs.Any(l => l.Action == "account.entity" && l.Target.Contains(mirrored.Name))));

        // the outlet sees it, its sibling no longer does — and a sync keeps the binding
        Assert.Contains(zid, await Listed(c, slug1, refresh: true));
        Assert.DoesNotContain(zid, await Listed(c, slug2));
        Assert.Equal(e1, InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Channel == ChannelIds.Social && a.ExternalId == zid).EntityId));

        // the same outlet again: no new profile, no Zernio move
        Assert.Equal(HttpStatusCode.OK, (await Assign(c, zid, e1.ToString())).StatusCode);
        Assert.Single(_f.Zernio.Of("CreateProfile"));
        Assert.Single(_f.Zernio.Of("MoveAccount"));

        // back to the organization level: the default profile, inherited by every outlet again
        var back = await Assign(c, zid, null);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await back.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entity_id").ValueKind);
        Assert.Equal("prof-default", _f.Zernio.Of("MoveAccount").Last()["profileId"]);
        Assert.Null(InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Channel == ChannelIds.Social && a.ExternalId == zid).EntityId));
        Assert.Null(InDb(db => db.Threads.Single(t => t.Id == comment).EntityId));
        Assert.Null(InDb(db => db.SocialPosts.Single(p => p.Id == scheduled).EntityId));
        Assert.Contains(zid, await Listed(c, slug1, refresh: true));
        Assert.Contains(zid, await Listed(c, slug2));
        Assert.Null(InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Channel == ChannelIds.Social && a.ExternalId == zid).EntityId));
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
        var row = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Channel == ChannelIds.Social && a.ExternalId == acct.ExternalId));
        Assert.Equal(e1, row.EntityId);
        Assert.NotEqual("prof-default", SocialChannels.Cfg(row, SocialChannels.ProfileIdKey));
    }

    [Fact]
    public async Task Assigning_needs_social_manage_and_both_levels_in_scope()
    {
        var admin = await Admin();
        var (e1, slug1) = NewEntity();
        var (e2, slug2) = NewEntity();
        var acct = await OrgAccount(admin);
        var mine = Account("facebook", e1);
        var theirs = Account("tiktok", e2);

        // no social.manage
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

        // the generic inbox PATCH cannot rebind a social account behind Zernio's back
        var generic = await admin.PatchAsJsonAsync($"/api/messaging/accounts/{mine.Id}", new { entity = slug2 });
        Assert.Equal(HttpStatusCode.Conflict, generic.StatusCode);
    }

    // ---- Facebook Pages: one login, several Pages, each bound to its outlet --------------

    private static JsonElement Row(JsonElement list, string zid) =>
        list.GetProperty("accounts").EnumerateArray().Single(r => r.GetProperty("id").GetString() == zid);

    private static List<string> PageIds(JsonElement row) =>
        row.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task Facebook_pages_are_mirrored_bound_to_outlets_selected_and_targeted_per_page()
    {
        var c = await Admin();
        var (e1, slug1) = NewEntity();
        var (_, slug2) = NewEntity();
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
            new { id = "P3", name = "Kopi Group", username = "kopigroup", category = "Brand", fan_count = 30 },
        }, "P1");

        // the sync mirrors the Pages (refresh=1 re-reads Meta through Zernio)
        var all = await c.GetFromJsonAsync<JsonElement>("/api/social/accounts?refresh=1");
        var row = Row(all, fb);
        Assert.Equal(new[] { "P1", "P2", "P3" }.ToHashSet(), PageIds(row).ToHashSet());
        var p1 = row.GetProperty("pages").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "P1");
        Assert.True(p1.GetProperty("selected").GetBoolean());
        Assert.Equal($"{fb}:P1", p1.GetProperty("target").GetString());
        Assert.Equal(true, _f.Zernio.Of("FacebookPages").Last()["refresh"]);

        // bind P1 → outlet 1, P2 → outlet 2; P3 stays with the account (organization)
        var bind = await c.PatchAsJsonAsync($"/api/social/accounts/{fb}/pages/P1", new { entity = slug1 });
        Assert.Equal(HttpStatusCode.OK, bind.StatusCode);
        var bound = (await bind.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pages").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "P1");
        Assert.Equal(e1, bound.GetProperty("entity_id").GetInt32());
        Assert.Equal(slug1, bound.GetProperty("entity_slug").GetString());
        Assert.Equal(HttpStatusCode.OK,
            (await c.PatchAsJsonAsync($"/api/social/accounts/{fb}/pages/P2", new { entity = slug2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PatchAsJsonAsync($"/api/social/accounts/{fb}/pages/P9", new { entity = slug2 })).StatusCode);

        // an outlet sees its own Pages and the organization's, never a sibling's
        Assert.Equal(new[] { "P1", "P3" }.ToHashSet(),
            PageIds(Row(await c.GetFromJsonAsync<JsonElement>($"/api/social/accounts?entity={slug1}"), fb)).ToHashSet());
        Assert.Equal(new[] { "P2", "P3" }.ToHashSet(),
            PageIds(Row(await c.GetFromJsonAsync<JsonElement>($"/api/social/accounts?entity={slug2}"), fb)).ToHashSet());

        // posts: an outlet posts to ITS Page — the account repeated with platformSpecificData.pageId
        var post = await c.PostAsJsonAsync($"/api/social/posts?entity={slug1}",
            new { content = "KL opening!", account_ids = new[] { $"{fb}:P1" }, publish_now = true });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var created = _f.Zernio.Of("CreatePost").Last();
        var platforms = JsonSerializer.SerializeToElement(created["platforms"]);
        var only = Assert.Single(platforms.EnumerateArray());
        Assert.Equal(fb, only.GetProperty("accountId").GetString());
        Assert.Equal("P1", only.GetProperty("platformSpecificData").GetProperty("pageId").GetString());
        var result = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("post").GetProperty("results")[0];
        Assert.Equal("P1", result.GetProperty("page_id").GetString());

        // a sibling's Page, an unknown Page, or a Page of a non-facebook account: refused
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/social/posts?entity={slug1}",
            new { content = "x", account_ids = new[] { $"{fb}:P2" }, publish_now = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/social/posts",
            new { content = "x", account_ids = new[] { $"{fb}:P404" }, publish_now = true })).StatusCode);
        var ig = Account("instagram");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/social/posts",
            new { content = "x", account_ids = new[] { $"{ig.ExternalId}:P1" }, publish_now = true })).StatusCode);

        // the organization posts to the account's selected Page AND its own Page P3 in one post
        var org = await c.PostAsJsonAsync("/api/social/posts",
            new { content = "Group news", account_ids = new[] { fb, $"{fb}:P3" }, publish_now = true });
        Assert.Equal(HttpStatusCode.OK, org.StatusCode);
        var orgPlatforms = JsonSerializer.SerializeToElement(_f.Zernio.Of("CreatePost").Last()["platforms"]).EnumerateArray().ToList();
        Assert.Equal(2, orgPlatforms.Count);
        Assert.All(orgPlatforms, p => Assert.Equal(fb, p.GetProperty("accountId").GetString()));
        Assert.False(orgPlatforms[0].TryGetProperty("platformSpecificData", out _));

        // which Page receives comments / messages
        var sel = await c.PostAsync($"/api/social/accounts/{fb}/pages/P2/select", null);
        Assert.Equal(HttpStatusCode.OK, sel.StatusCode);
        Assert.Equal("P2", _f.Zernio.Of("SelectFacebookPage").Single()["pageId"]);
        var selPages = (await sel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pages").EnumerateArray().ToList();
        Assert.True(selPages.Single(p => p.GetProperty("id").GetString() == "P2").GetProperty("selected").GetBoolean());
        Assert.False(selPages.Single(p => p.GetProperty("id").GetString() == "P1").GetProperty("selected").GetBoolean());

        // unbinding a Page brings its pending posts back with it
        var pending = SeedPost(SocialPosts.Scheduled, e1, $"{fb}:P1");
        Assert.Equal(HttpStatusCode.OK,
            (await c.PatchAsJsonAsync($"/api/social/accounts/{fb}/pages/P1", new { entity = (string?)null })).StatusCode);
        Assert.Null(InDb(db => db.SocialPosts.Single(p => p.Id == pending).EntityId));

        // permissions: no social.manage; a company admin of outlet 1 cannot touch outlet 2's Page
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PatchAsJsonAsync($"/api/social/accounts/{fb}/pages/P2", new { entity = slug1 })).StatusCode);
        var user = "co-fb-" + U();
        var uid = SettingsTestKit.CreateUser(_f, user, "pass12345", Roles.Admin);
        InDb(db =>
        {
            db.Profiles.Include(p => p.Entities).Single(p => p.UserId == uid).Entities.Add(db.Entities.Single(e => e.Id == e1));
            db.SaveChanges();
            return 0;
        });
        var co = await SettingsTestKit.Client(_f, user, "pass12345");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await co.PatchAsJsonAsync($"/api/social/accounts/{fb}/pages/P2", new { entity = slug1 })).StatusCode);

        // the account moves to outlet 1: outlet 2's Page binding no longer fits under it and is cleared,
        // and a Page cannot be bound outside the account's outlet
        Assert.Equal(HttpStatusCode.OK, (await Assign(c, fb, slug1)).StatusCode);
        Assert.Null(InDb(db => db.SocialPages.Single(p => p.ZernioAccountId == fb && p.PageId == "P2").EntityId));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PatchAsJsonAsync($"/api/social/accounts/{fb}/pages/P2", new { entity = slug2 })).StatusCode);
    }

    // ---- posts -----------------------------------------------------------------------

    [Fact]
    public async Task Posts_publish_schedule_list_retry_and_delete_and_follow_status_webhooks()
    {
        var ig = Account("instagram");
        var fb = Account("facebook");
        var c = await Admin();

        // publish now
        var now = await c.PostAsJsonAsync("/api/social/posts", new { content = "Opening day!", account_ids = new[] { ig.ExternalId, fb.ExternalId }, publish_now = true });
        Assert.Equal(HttpStatusCode.OK, now.StatusCode);
        var nowPost = (await now.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("post");
        Assert.Equal("published", nowPost.GetProperty("status").GetString());
        Assert.Equal(2, nowPost.GetProperty("results").GetArrayLength());
        var created = _f.Zernio.Of("CreatePost").Last();
        Assert.Equal(true, created["publishNow"]);

        // schedule (wall-clock time in a zone) → Zernio gets the UTC instant
        var at = DateTime.UtcNow.AddDays(2);
        var local = TimeZoneInfo.ConvertTimeFromUtc(at, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur"));
        var sched = await c.PostAsJsonAsync("/api/social/posts", new
        {
            content = "Weekend promo", account_ids = new[] { ig.ExternalId },
            media = new[] { new { url = "https://media.zernio.com/temp/a.jpg", type = "image" } },
            scheduled_at = local.ToString("yyyy-MM-ddTHH:mm:ss"), timezone = "Asia/Kuala_Lumpur",
        });
        var schedPost = (await sched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("post");
        Assert.Equal("scheduled", schedPost.GetProperty("status").GetString());
        var schedBody = _f.Zernio.Of("CreatePost").Last();
        Assert.Equal(at.ToString("yyyy-MM-ddTHH:mm:ss") + "Z", schedBody["scheduledFor"]);
        var zid = schedPost.GetProperty("zernio_post_id").GetString()!;

        // a draft stays local
        var draft = await c.PostAsJsonAsync("/api/social/posts", new { content = "idea", draft = true });
        Assert.Equal("draft", (await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("post").GetProperty("status").GetString());
        Assert.Equal(2, _f.Zernio.Of("CreatePost").Count);

        // the status webhook: partial with a failure
        await Hook("post.partial", new
        {
            id = Guid.NewGuid().ToString(), @event = "post.partial",
            post = new
            {
                id = zid, status = "partial", scheduledFor = at,
                platforms = new[] { new { platform = "instagram", accountId = ig.ExternalId, status = "failed", error = "Media too large", publishedUrl = (string?)null } },
            },
        });
        var listed = await c.GetFromJsonAsync<JsonElement>("/api/social/posts");
        var row = listed.GetProperty("posts").EnumerateArray().First(p => p.GetProperty("id").GetInt32() == schedPost.GetProperty("id").GetInt32());
        Assert.Equal("partial", row.GetProperty("status").GetString());
        Assert.Contains("Media too large", row.GetProperty("error").GetString());

        // retry
        var retry = await c.PostAsync($"/api/social/posts/{row.GetProperty("id").GetInt32()}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("published", (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("post").GetProperty("status").GetString());

        // delete a scheduled one → Zernio DELETE; a published one → unpublish per platform
        var another = await c.PostAsJsonAsync("/api/social/posts", new { content = "later", account_ids = new[] { fb.ExternalId }, scheduled_at = DateTime.UtcNow.AddDays(3).ToString("o") });
        var anotherId = (await another.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("post").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/social/posts/{anotherId}?confirm=true")).StatusCode);
        Assert.Single(_f.Zernio.Of("DeletePost"));
        var nowId = nowPost.GetProperty("id").GetInt32();
        await c.DeleteAsync($"/api/social/posts/{nowId}");
        Assert.Equal(2, _f.Zernio.Of("UnpublishPost").Count);
        Assert.Equal("deleted", InDb(db => db.SocialPosts.Single(p => p.Id == nowId).Status));

        // accounts of another entity cannot be posted to from the organization level
        var entity = InDb(db => db.Entities.First(e => e.Slug == "main").Id);
        var other = Account("instagram", entity);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/social/posts", new { content = "x", account_ids = new[] { other.ExternalId }, publish_now = true })).StatusCode);
    }

    [Fact]
    public async Task Media_upload_goes_through_a_presigned_url()
    {
        var c = await Admin();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0x00 });
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "photo.jpg");
        var res = await c.PostAsync("/api/social/media", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("image", body.GetProperty("type").GetString());
        Assert.Equal("https://media.zernio.com/temp/photo.jpg", body.GetProperty("url").GetString());
        Assert.Equal("image/jpeg", _f.Zernio.Of("Presign").Single()["contentType"]);
        Assert.Single(_f.Zernio.Of("Upload"));
    }

    // ---- settings + permissions --------------------------------------------------------

    [Fact]
    public async Task Social_capabilities_belong_to_admins_and_unconfigured_zernio_says_so()
    {
        var admin = await Admin();
        var who = await admin.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        var caps = who.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("social.view", caps);
        Assert.Contains("social.publish", caps);
        Assert.Contains("social.manage", caps);

        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/social/posts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/social/connect", new { platform = "instagram" })).StatusCode);
        // the key's settings are superadmin's
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/zernio/status")).StatusCode);

        _f.Zernio.Configured = false;
        var res = await admin.GetAsync("/api/social/accounts");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(ChatBranch.Api.Endpoints.SocialEndpoints.NotConfiguredCode,
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
