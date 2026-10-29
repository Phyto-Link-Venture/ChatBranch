using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
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
/// A fake seller service. Records what ChatBranch asked it to send, and answers
/// whatever the test wants — so the bridge is exercised without a sidecar.
/// </summary>
public sealed class FakeCommerceGateway : ICommerceGateway
{
    public bool Configured { get; set; } = true;
    public string Reason { get; set; } = "";
    public List<(string Account, string Key, string Text)> Sent { get; } = new();
    public CommerceResult NextSend { get; set; } = CommerceResult.Success("m-1");
    public List<CommerceShop> Shops { get; } = new();
    public string ShopsError { get; set; } = "";
    public int Polls { get; private set; }

    public bool IsConfigured => Configured;
    public string BaseUrl => "http://seller.test";
    public string NotConfiguredReason => Configured ? "" : (Reason.Length > 0 ? Reason : "not attached");

    public Task<CommerceResult> SendTextAsync(string account, string key, string text, CancellationToken ct = default)
    {
        Sent.Add((account, key, text));
        return Task.FromResult(NextSend);
    }

    public Task<(IReadOnlyList<CommerceShop> Shops, string Error)> ShopsAsync(CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<CommerceShop>, string)>((Shops, ShopsError));

    public Task<CommerceResult> PollChatsAsync(CancellationToken ct = default)
    {
        Polls += 1;
        return Task.FromResult(CommerceResult.Success());
    }
}

/// <summary>The API host with a fake seller service in place of the sidecar.</summary>
public class CommerceFactory : ApiFactory
{
    public FakeCommerceGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<ICommerceGateway>(Gateway));
    }
}

/// <summary>
/// The marketplace bridge: Shopee, Lazada, TikTok Shop and Shopify as inbox
/// channels, carried by the seller-service sidecar.
///
/// The shape of these tests follows where the bugs would be: the shared secret
/// (closed, wrong, right), the push answering HONESTLY so the sidecar knows
/// whether to retry, a shop becoming an inbox on first contact, our own sends
/// not coming back as buyer messages, and Shopify refusing chat instead of
/// pretending.
/// </summary>
public class CommerceBridgeTests : IClassFixture<CommerceFactory>
{
    private readonly CommerceFactory _f;
    private const string Secret = "commerce-test-secret";

    public CommerceBridgeTests(CommerceFactory f)
    {
        _f = f;
        _ = _f.CreateClient();
        _f.Gateway.Sent.Clear();
        _f.Gateway.Shops.Clear();
        _f.Gateway.Configured = true;
        _f.Gateway.ShopsError = "";
        _f.Gateway.NextSend = CommerceResult.Success("m-1");
    }

    // ---- helpers --------------------------------------------------------------

    private T InDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private void SetSecret(string value) =>
        InDb(db => { new ScopingService(db).SetSetting("commerce_secret", value); return 0; });

    private async Task<HttpClient> Authed(string user = "admin", string pass = "admin123")
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>A push as the sidecar sends it.</summary>
    private async Task<HttpResponseMessage> Push(object body, string? secret = Secret)
    {
        var c = _f.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/commerce/inbound")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (secret != null) req.Headers.TryAddWithoutValidation("X-Commerce-Secret", secret);
        return await c.SendAsync(req);
    }

    private static object BuyerMessage(string channel, string account, string key, string text,
        string messageId, bool fromMe = false, string buyerName = "Ali") => new
    {
        channel,
        account,
        key,
        message_id = messageId,
        from_me = fromMe,
        text,
        kind = "text",
        media_url = "",
        buyer_id = "88",
        buyer_name = buyerName,
        timestamp = "2026-10-29T09:15:00.000Z",
        shop_name = "Kopi Kaw HQ",
    };

    private Thread? ThreadFor(string channel, string key) =>
        InDb(db => db.Threads.AsNoTracking().FirstOrDefault(t => t.Channel == channel && t.Key == key));

    // ---- the vocabulary -------------------------------------------------------

    [Fact]
    public void A_shop_handle_splits_into_its_marketplace_and_shop_id()
    {
        Assert.Equal(("shopee", "123456"), CommerceChannels.ParseAccountKey("shopee:123456"));
        // Shopify's shop id is a domain: dots are part of it, the FIRST colon splits
        Assert.Equal(("shopify", "my-store.myshopify.com"),
            CommerceChannels.ParseAccountKey("shopify:my-store.myshopify.com"));
        Assert.Equal(("", "nonsense"), CommerceChannels.ParseAccountKey("nonsense"));
        Assert.Equal(("", ""), CommerceChannels.ParseAccountKey(null));
    }

    [Fact]
    public void The_channel_and_the_marketplace_id_differ_only_for_tiktok_shop()
    {
        Assert.Equal(ChannelIds.TikTokShop, CommerceChannels.ChannelFor("tiktokshop"));
        Assert.Equal(ChannelIds.TikTokShop, CommerceChannels.ChannelFor("tiktok_shop"));
        Assert.Equal("tiktokshop", CommerceChannels.Marketplace(ChannelIds.TikTokShop));
        Assert.Equal("tiktok_shop:2001", CommerceChannels.AccountKey(ChannelIds.TikTokShop, "2001"));
        // and it is NOT the existing TikTok Business Messaging channel
        Assert.NotEqual(ChannelIds.TikTok, ChannelIds.TikTokShop);
        Assert.Null(CommerceChannels.ChannelFor("whatsapp"));
        Assert.Null(CommerceChannels.ChannelFor(""));
    }

    [Fact]
    public void Shopify_is_a_marketplace_inbox_that_cannot_carry_a_conversation()
    {
        Assert.True(CommerceChannels.Handles(ChannelIds.Shopify));
        Assert.False(CommerceChannels.CarriesChat(ChannelIds.Shopify));
        foreach (var chat in new[] { ChannelIds.Shopee, ChannelIds.Lazada, ChannelIds.TikTokShop })
            Assert.True(CommerceChannels.CarriesChat(chat));
    }

    [Fact]
    public void A_marketplace_shop_is_configured_by_naming_a_shop_not_by_a_token()
    {
        var shop = new ChannelAccount { Channel = ChannelIds.Shopee, ExternalId = "shopee:555" };
        Assert.True(CommerceChannels.Configured(shop));
        Assert.True(MoreChannels.Configured(shop));
        Assert.True(MoreChannels.Handles(ChannelIds.Shopee), "it must ride the shared send path");
        Assert.True(MoreChannels.IsCommerce(ChannelIds.Shopee));
        // no shop handle = nothing the sidecar can act for
        Assert.False(CommerceChannels.Configured(new ChannelAccount { Channel = ChannelIds.Shopee }));
        // and there is no webhook URL for an operator to paste anywhere
        Assert.Equal("", MoreChannels.WebhookPath(shop));
    }

    // ---- the shared secret ----------------------------------------------------

    [Fact]
    public async Task With_no_secret_configured_the_bridge_is_closed()
    {
        SetSecret("");
        var res = await Push(BuyerMessage("shopee", "shopee:901", "conv-closed", "hi", "m-closed"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("commerce_secret", body.GetProperty("detail").GetString());
        Assert.Null(ThreadFor(ChannelIds.Shopee, "conv-closed"));
    }

    [Fact]
    public async Task A_wrong_or_missing_secret_is_refused()
    {
        SetSecret(Secret);
        foreach (var sent in new[] { "guess", null })
        {
            var res = await Push(BuyerMessage("shopee", "shopee:902", "conv-401", "hi", "m-401"), sent);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        Assert.Null(ThreadFor(ChannelIds.Shopee, "conv-401"));
    }

    // ---- inbound --------------------------------------------------------------

    [Fact]
    public async Task A_buyer_message_opens_a_conversation_and_an_inbox_for_the_shop()
    {
        SetSecret(Secret);
        var res = await Push(BuyerMessage("shopee", "shopee:555", "conv-1", "Masih ada stock?", "msg-1"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("stored").GetBoolean());
        Assert.Equal("in", body.GetProperty("direction").GetString());

        // the shop became an inbox by itself — its grant cannot be typed in here
        var account = InDb(db => db.ChannelAccounts.AsNoTracking()
            .FirstOrDefault(a => a.Channel == ChannelIds.Shopee && a.ExternalId == "shopee:555"));
        Assert.NotNull(account);
        Assert.Contains("Kopi Kaw HQ", account!.Name);
        Assert.Equal(Health.Healthy, account.HealthStatus);

        // the conversation is keyed by the marketplace's conversation id
        var thread = ThreadFor(ChannelIds.Shopee, "conv-1");
        Assert.NotNull(thread);
        Assert.Equal(account.Id, thread!.AccountId);

        var messages = InDb(db => db.Messages.AsNoTracking().Where(m => m.ThreadId == thread.Id).ToList());
        var inbound = Assert.Single(messages);
        Assert.Equal("Masih ada stock?", inbound.Text);
        Assert.Equal(MessageDirection.In, inbound.Direction);
        Assert.Equal(MessageSource.Customer, inbound.Source);
        Assert.Equal("msg-1", inbound.ExternalId);
    }

    [Fact]
    public async Task The_same_push_twice_stores_one_message()
    {
        SetSecret(Secret);
        var push = BuyerMessage("lazada", "lazada:9001", "sess-dedup", "Bila sampai?", "lm-7");
        Assert.Equal(HttpStatusCode.OK, (await Push(push)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Push(push)).StatusCode);

        var thread = ThreadFor(ChannelIds.Lazada, "sess-dedup");
        Assert.NotNull(thread);
        var count = InDb(db => db.Messages.Count(m => m.ThreadId == thread!.Id));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_message_the_shop_sent_elsewhere_is_our_outbound_reply_not_something_to_answer()
    {
        SetSecret(Secret);
        await Push(BuyerMessage("shopee", "shopee:556", "conv-2", "Ada stock?", "msg-a"));
        var res = await Push(BuyerMessage("shopee", "shopee:556", "conv-2", "Ada, ready stock!", "msg-b", fromMe: true));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("out", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("direction").GetString());

        var thread = ThreadFor(ChannelIds.Shopee, "conv-2")!;
        var ours = InDb(db => db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.Out).ToList());
        var reply = Assert.Single(ours);
        // external_client is how ChatBranch records a person replying outside it
        Assert.Equal(MessageSource.ExternalClient, reply.Source);
        Assert.Equal("Ada, ready stock!", reply.Text);
    }

    [Fact]
    public async Task A_push_that_can_never_work_says_so_rather_than_acking_silently()
    {
        SetSecret(Secret);
        // the sidecar checks the answer: a 4xx means "do not retry", a 2xx "stored"
        var unknown = await Push(BuyerMessage("etsy", "etsy:1", "c", "hi", "m"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("unknown marketplace channel",
            (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var noKey = await Push(BuyerMessage("shopee", "shopee:557", "", "hi", "m"));
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        var noShop = await Push(BuyerMessage("shopee", "", "conv-3", "hi", "m"));
        Assert.Equal(HttpStatusCode.BadRequest, noShop.StatusCode);

        var garbage = await _f.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/commerce/inbound")
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
            Headers = { { "X-Commerce-Secret", Secret } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task An_inbound_buyer_message_heals_the_shop_and_keeps_its_name_current()
    {
        SetSecret(Secret);
        await Push(BuyerMessage("tiktok_shop", "tiktok_shop:2001", "conv-tt", "Still available?", "tt-1"));
        var account = InDb(db => db.ChannelAccounts.First(a => a.ExternalId == "tiktok_shop:2001"));
        InDb(db =>
        {
            var row = db.ChannelAccounts.First(a => a.Id == account.Id);
            row.HealthStatus = Health.Disconnected;
            db.SaveChanges();
            return 0;
        });

        await Push(new
        {
            channel = "tiktok_shop",
            account = "tiktok_shop:2001",
            key = "conv-tt",
            message_id = "tt-2",
            from_me = false,
            text = "Hello?",
            shop_name = "Kopi TT Renamed",
            timestamp = "2026-10-29T10:00:00.000Z",
        });

        var healed = InDb(db => db.ChannelAccounts.AsNoTracking().First(a => a.Id == account.Id));
        Assert.Equal(Health.Healthy, healed.HealthStatus);
        Assert.Contains("Kopi TT Renamed", healed.Name);
    }

    [Fact]
    public async Task Marketplace_media_is_linked_so_the_operator_can_open_it()
    {
        SetSecret(Secret);
        await Push(new
        {
            channel = "shopee",
            account = "shopee:558",
            key = "conv-media",
            message_id = "msg-media",
            from_me = false,
            text = "[image]",
            media_url = "https://cf.shopee.com/photo.jpg",
            timestamp = "2026-10-29T10:00:00.000Z",
        });
        var thread = ThreadFor(ChannelIds.Shopee, "conv-media")!;
        var msg = InDb(db => db.Messages.AsNoTracking().First(m => m.ThreadId == thread.Id));
        Assert.Contains("https://cf.shopee.com/photo.jpg", msg.Text);
        Assert.Contains("[image]", msg.Text);
    }

    // ---- outbound -------------------------------------------------------------

    [Fact]
    public async Task A_reply_goes_out_through_the_seller_service()
    {
        SetSecret(Secret);
        await Push(BuyerMessage("shopee", "shopee:560", "conv-send", "Ada stock?", "msg-send"));
        var client = await Authed();

        var res = await client.PostAsJsonAsync("/api/messaging/threads/shopee/conv-send/send",
            new { text = "Ada, ready stock!" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var sent = Assert.Single(_f.Gateway.Sent);
        Assert.Equal("shopee:560", sent.Account);
        Assert.Equal("conv-send", sent.Key);
        Assert.Equal("Ada, ready stock!", sent.Text);

        var thread = ThreadFor(ChannelIds.Shopee, "conv-send")!;
        var ours = InDb(db => db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.Out).ToList());
        Assert.Single(ours);
        Assert.Equal("sent", ours[0].Status);
        Assert.Equal("m-1", ours[0].ExternalId);
    }

    [Fact]
    public async Task A_reply_the_seller_service_refuses_is_a_failed_row_with_the_reason()
    {
        SetSecret(Secret);
        await Push(BuyerMessage("shopee", "shopee:561", "conv-fail", "Ada stock?", "msg-fail"));
        _f.Gateway.NextSend = CommerceResult.Fail("seller service 409: not_connected — reconnect the shop");
        var client = await Authed();

        var res = await client.PostAsJsonAsync("/api/messaging/threads/shopee/conv-fail/send", new { text = "Hello" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // the send is recorded, honestly

        var thread = ThreadFor(ChannelIds.Shopee, "conv-fail")!;
        var ours = InDb(db => db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.Out).ToList());
        Assert.Contains(ours, m => m.Status == "failed");
        Assert.Contains(ours, m => (m.Error ?? "").Contains("reconnect", StringComparison.OrdinalIgnoreCase)
                                   || (m.Error ?? "").Contains("not_connected"));
    }

    [Fact]
    public async Task Shopify_refuses_a_reply_instead_of_pretending_to_send_one()
    {
        SetSecret(Secret);
        // a Shopify shop can still exist as an inbox (orders, products)
        InDb(db =>
        {
            db.ChannelAccounts.Add(new ChannelAccount
            {
                Channel = ChannelIds.Shopify,
                ExternalId = "shopify:my-store.myshopify.com",
                Name = "Shopify · my-store",
                CreatedAt = DateTime.UtcNow,
                HealthStatus = Health.Healthy,
            });
            return db.SaveChanges();
        });
        var account = InDb(db => db.ChannelAccounts.First(a => a.Channel == ChannelIds.Shopify));
        var thread = InDb(db =>
        {
            var t = new Thread
            {
                Channel = ChannelIds.Shopify,
                Key = "shopify-conv",
                AccountId = account.Id,
                Status = ThreadStatus.Open,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Threads.Add(t);
            db.SaveChanges();
            return t;
        });

        var transport = _f.Services.GetServices<IChannelTransport>()
            .First(t => t.Channel == ChannelIds.Shopify);
        var result = await transport.SendTextAsync(account, new SendTarget(thread.Key, null, null, thread.Id), "hi");

        Assert.False(result.Ok);
        Assert.Equal(TransportFailure.Unsupported, result.Failure);
        Assert.Contains("no buyer-chat API", result.Detail);
        Assert.Empty(_f.Gateway.Sent);
    }

    [Fact]
    public async Task A_seller_service_that_is_not_attached_never_blames_the_shop()
    {
        _f.Gateway.Configured = false;
        _f.Gateway.Reason = "COMMERCE_SECRET is not set";
        var account = new ChannelAccount { Channel = ChannelIds.Shopee, ExternalId = "shopee:700" };
        var transport = _f.Services.GetServices<IChannelTransport>().First(t => t.Channel == ChannelIds.Shopee);

        var result = await transport.SendTextAsync(account, new SendTarget("conv-x", null, null), "hi");
        Assert.False(result.Ok);
        // transient: the SHOP is fine, the sidecar is down — health must not move
        Assert.Equal(TransportFailure.Transient, result.Failure);
        Assert.Contains("COMMERCE_SECRET", result.Detail);

        var probe = await transport.ProbeAsync(account);
        Assert.True(probe.Verdict.Transient);
        Assert.Null(probe.Verdict.Status);
    }

    [Fact]
    public async Task A_grant_the_seller_service_no_longer_holds_marks_the_shop_disconnected()
    {
        var account = new ChannelAccount { Channel = ChannelIds.Shopee, ExternalId = "shopee:gone" };
        var transport = _f.Services.GetServices<IChannelTransport>().First(t => t.Channel == ChannelIds.Shopee);

        var probe = await transport.ProbeAsync(account);
        Assert.Equal(Health.Disconnected, probe.Verdict.Status);
        Assert.Contains("reconnect", probe.Verdict.Detail);

        _f.Gateway.Shops.Add(new CommerceShop(ChannelIds.Shopee, "shopee:gone", "shopee", "gone",
            "Kopi Kaw", "global", "connected", ""));
        var healthy = await transport.ProbeAsync(account);
        Assert.Equal(Health.Healthy, healthy.Verdict.Status);
        Assert.Equal("Kopi Kaw", healthy.Name);
    }

    // ---- the operator's side --------------------------------------------------

    [Fact]
    public async Task The_status_route_says_whether_the_seller_service_is_attached()
    {
        _f.Gateway.Shops.Add(new CommerceShop(ChannelIds.Lazada, "lazada:9100", "lazada", "9100",
            "Kopi MY", "my", "connected", ""));
        var client = await Authed();
        var res = await client.GetAsync("/api/commerce/status");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("attached").GetBoolean());
        Assert.Equal(4, body.GetProperty("marketplaces").GetArrayLength());
        var shops = body.GetProperty("shops").EnumerateArray().ToList();
        Assert.Contains(shops, s => s.GetProperty("shop").GetString() == "lazada:9100");
        // a shop the sidecar holds but that has no inbox yet is visible as such
        Assert.Contains(shops, s => s.GetProperty("shop").GetString() == "lazada:9100"
                                    && !s.GetProperty("in_inbox").GetBoolean());
    }

    [Fact]
    public async Task Syncing_gives_every_connected_shop_an_inbox_and_is_idempotent()
    {
        _f.Gateway.Shops.Add(new CommerceShop(ChannelIds.Shopee, "shopee:9200", "shopee", "9200",
            "Sync Shop", "global", "connected", ""));
        var client = await Authed();

        var first = await client.PostAsJsonAsync("/api/commerce/shops/sync", new { });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("shopee:9200", body.GetProperty("added").EnumerateArray().Select(e => e.GetString()));

        var account = InDb(db => db.ChannelAccounts.AsNoTracking().First(a => a.ExternalId == "shopee:9200"));
        Assert.Contains("Sync Shop", account.Name);

        // running it again adds nothing
        var second = await client.PostAsJsonAsync("/api/commerce/shops/sync", new { });
        var again = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(again.GetProperty("added").EnumerateArray());
        Assert.Equal(1, InDb(db => db.ChannelAccounts.Count(a => a.ExternalId == "shopee:9200")));
    }

    [Fact]
    public async Task A_marketplace_shop_cannot_be_created_or_credential_edited_by_hand()
    {
        var client = await Authed();
        var created = await client.PostAsJsonAsync("/api/messaging/accounts",
            new { channel = "shopee", name = "My shop", access_token = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        var detail = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();
        Assert.Contains("seller service", detail);
    }

    [Fact]
    public async Task A_marketplace_shop_is_still_an_inbox_whose_settings_can_be_changed()
    {
        SetSecret(Secret);
        await Push(BuyerMessage("shopee", "shopee:9300", "conv-inbox", "hi", "msg-inbox"));
        var account = InDb(db => db.ChannelAccounts.AsNoTracking().First(a => a.ExternalId == "shopee:9300"));
        var client = await Authed();

        // "who answers it" is editable like any other inbox
        var patched = await client.PatchAsJsonAsync($"/api/messaging/accounts/{account.Id}", new { ai_default = "off" });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var body = await patched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("shopee", body.GetProperty("marketplace").GetString());
        Assert.Equal("shopee:9300", body.GetProperty("shop").GetString());
        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.True(body.GetProperty("carries_chat").GetBoolean());
        // and no secret is invented for it
        Assert.Equal("seller_service", body.GetProperty("credentials_held_by").GetString());
    }

    [Fact]
    public async Task Fetching_marketplace_chats_now_asks_the_seller_service_to_poll()
    {
        var client = await Authed();
        var before = _f.Gateway.Polls;
        var res = await client.PostAsJsonAsync("/api/commerce/poll", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(before + 1, _f.Gateway.Polls);
    }

    [Fact]
    public async Task The_operator_routes_refuse_cleanly_when_nothing_is_attached()
    {
        _f.Gateway.Configured = false;
        _f.Gateway.Reason = "COMMERCE_URL is not set";
        var client = await Authed();

        var sync = await client.PostAsJsonAsync("/api/commerce/shops/sync", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, sync.StatusCode);
        var poll = await client.PostAsJsonAsync("/api/commerce/poll", new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, poll.StatusCode);

        // status still answers — it is how an operator finds out
        var status = await client.GetAsync("/api/commerce/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var body = await status.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("attached").GetBoolean());
        Assert.Contains("COMMERCE_URL", body.GetProperty("reason").GetString());
    }
}
