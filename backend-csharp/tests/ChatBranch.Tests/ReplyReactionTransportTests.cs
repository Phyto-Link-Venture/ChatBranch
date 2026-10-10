using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>Quotes + reactions over Telegram, through the API host with the Bot API faked.</summary>
public class ReplyReactionTelegramTests : IClassFixture<TransportFactory>
{
    private readonly TransportFactory _f;

    public ReplyReactionTelegramTests(TransportFactory f)
    {
        _f = f;
        _f.TelegramApi.Reset();
    }

    private async Task<HttpClient> Authed()
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private T InDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static object Update(long chatId, long messageId, string text, object? replyTo = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["message_id"] = messageId,
            ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["chat"] = new { id = chatId, type = "private" },
            ["from"] = new { id = chatId, is_bot = false, first_name = "Tele" },
            ["text"] = text,
        };
        if (replyTo != null) message["reply_to_message"] = replyTo;
        return new { update_id = messageId, message };
    }

    [Fact]
    public async Task Telegram_quotes_and_reactions_both_ways()
    {
        var bot = InDb(db =>
        {
            var a = new ChannelAccount { Channel = "telegram", Name = "TG quotes", AccessToken = "5101:quote-bot-token-zzzz",
                HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow };
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a;
        });
        var hook = $"/api/messaging/webhook/telegram/{bot.AccessToken}";
        var anon = _f.CreateClient();
        (await anon.PostAsync(hook, Json(Update(8101, 50, "Price of the red one?")))).EnsureSuccessStatusCode();
        (await anon.PostAsync(hook, Json(Update(8101, 51, "this one", new { message_id = 50, text = "Price of the red one?" }))))
            .EnsureSuccessStatusCode();
        var (firstPk, replyToId) = InDb(db =>
        {
            var t = db.Threads.Single(x => x.Channel == "telegram" && x.Key == "8101");
            var first = db.Messages.Single(m => m.ThreadId == t.Id && m.ExternalId == "50");
            return (first.Id, db.Messages.Single(m => m.ThreadId == t.Id && m.ExternalId == "51").ReplyToId);
        });
        Assert.Equal(firstPk, replyToId);

        // the customer reacts — stored, no bubble; clearing it removes it
        var count = InDb(db => db.Messages.Count(m => m.Thread!.Key == "8101"));
        (await anon.PostAsync(hook, Json(new
        {
            update_id = 900, message_reaction = new
            {
                chat = new { id = 8101, type = "private" }, message_id = 50, user = new { id = 8101, is_bot = false },
                date = 1, old_reaction = Array.Empty<object>(), new_reaction = new[] { new { type = "emoji", emoji = "🔥" } },
            },
        }))).EnsureSuccessStatusCode();
        Assert.Equal(count, InDb(db => db.Messages.Count(m => m.Thread!.Key == "8101")));
        Assert.Equal(("contact", "🔥"), InDb(db =>
        {
            var r = db.MessageReactions.Single(x => x.MessageId == firstPk);
            return (r.Actor, r.Emoji);
        }));
        (await anon.PostAsync(hook, Json(new
        {
            update_id = 901, message_reaction = new
            {
                chat = new { id = 8101 }, message_id = 50, user = new { id = 8101 }, date = 2,
                old_reaction = new[] { new { type = "emoji", emoji = "🔥" } }, new_reaction = Array.Empty<object>(),
            },
        }))).EnsureSuccessStatusCode();
        Assert.Equal(0, InDb(db => db.MessageReactions.Count(x => x.MessageId == firstPk)));

        // our reply quotes their first message: reply_parameters.message_id
        var admin = await Authed();
        (await admin.PostAsJsonAsync("/api/messaging/threads/telegram/8101/send", new { text = "RM 45", reply_to = firstPk }))
            .EnsureSuccessStatusCode();
        var send = _f.TelegramApi.Calls.Last(c => c.Method == "sendMessage");
        using (var body = JsonDocument.Parse(send.Body))
            Assert.Equal(50, body.RootElement.GetProperty("reply_parameters").GetProperty("message_id").GetInt64());
        Assert.Equal(firstPk, InDb(db => db.Messages.Where(m => m.Thread!.Key == "8101" && m.Text == "RM 45")
            .Select(m => m.ReplyToId).Single()));

        // our reaction: setMessageReaction
        (await admin.PostAsJsonAsync($"/api/messaging/threads/telegram/8101/messages/{firstPk}/react", new { emoji = "👍" }))
            .EnsureSuccessStatusCode();
        var react = _f.TelegramApi.Calls.Last(c => c.Method == "setMessageReaction");
        using (var body = JsonDocument.Parse(react.Body))
        {
            Assert.Equal(50, body.RootElement.GetProperty("message_id").GetInt64());
            Assert.Equal("👍", body.RootElement.GetProperty("reaction")[0].GetProperty("emoji").GetString());
        }
        Assert.Equal("user:admin", InDb(db => db.MessageReactions.Single(x => x.MessageId == firstPk).Actor));

        // email has no reactions → 409
        var emailPk = InDb(db =>
        {
            var t = new Thread { Channel = "email", Key = "x@example.test", Name = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Threads.Add(t);
            db.SaveChanges();
            var m = new Message { ThreadId = t.Id, Direction = "in", Source = "customer", Text = "hi", CreatedAt = DateTime.UtcNow };
            db.Messages.Add(m);
            db.SaveChanges();
            return m.Id;
        });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(
            $"/api/messaging/threads/email/x@example.test/messages/{emailPk}/react", new { emoji = "👍" })).StatusCode);
    }
}

/// <summary>A Baileys gateway double that records quotes and reactions.</summary>
internal sealed class QuoteBaileys : IBaileysGateway
{
    public List<(string Text, BaileysQuote? Quote)> Sends { get; } = new();
    public List<(BaileysQuote Target, string Emoji)> Reactions { get; } = new();
    public bool IsConfigured => true;
    public string BaseUrl => "http://gateway.test";

    public Task<BaileysResult> SendTextAsync(string a, string to, string text, CancellationToken ct = default) =>
        SendTextAsync(a, to, text, null, ct);

    public Task<BaileysResult> SendTextAsync(string a, string to, string text, BaileysQuote? quote,
        CancellationToken ct = default)
    {
        Sends.Add((text, quote));
        return Task.FromResult(new BaileysResult(true, "sent", $"3EB0Q{Sends.Count}"));
    }

    public Task<BaileysResult> ReactAsync(string a, string to, BaileysQuote target, string emoji,
        CancellationToken ct = default)
    {
        Reactions.Add((target, emoji));
        return Task.FromResult(new BaileysResult(true, "sent", "3EB0R"));
    }

    public Task<BaileysResult> StartCallAsync(string a, string to, Dictionary<string, object?> d,
        CancellationToken ct = default) => Task.FromResult(new BaileysResult(false, "no calls"));
    public Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "ended"));
    private static readonly BaileysStatus Connected = new("connected", null, "me", null);
    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
}

/// <summary>Baileys quotes + reactions at the send seam and on the gateway's HTTP contract.</summary>
public class ReplyReactionBaileysTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly QuoteBaileys _gw = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Sender_passes_the_quote_and_the_reaction_target_to_the_gateway()
    {
        var sc = new ScopingService(_t.Db);
        var threads = new ThreadStore(_t.Db, sc);
        var sender = new OutboundSender(_t.Db, threads, new WhatsAppCloudClient(new FakeHttpFactory(new FakeGraph())), _gw);
        var account = new ChannelAccount { Channel = "whatsapp", Provider = Providers.Baileys, Name = "QR", ExternalId = "gw-q",
            CreatedAt = DateTime.UtcNow };
        _t.Db.ChannelAccounts.Add(account);
        _t.Db.SaveChanges();
        var thread = threads.GetOrCreateThread("whatsapp", "60111000001", "Q", account.Id);
        var inbound = threads.AppendMessage(thread, "in", "customer", "What time do you close?", status: "received",
            externalId: "BAE5IN1", accountId: account.Id);

        var reply = sender.Send(thread, "At 9pm", MessageSource.Agent, account.Id, replyTo: inbound);
        Assert.Equal("sent", reply.Status);
        Assert.Equal(inbound.Id, reply.ReplyToId);
        var (_, quote) = Assert.Single(_gw.Sends);
        Assert.Equal(new BaileysQuote("BAE5IN1", false, "What time do you close?"), quote);

        var ok = sender.React(thread, inbound, "🙏");
        Assert.True(ok.Ok && ok.Live);
        var (target, emoji) = Assert.Single(_gw.Reactions);
        Assert.Equal(("BAE5IN1", false, "🙏"), (target.Id, target.FromMe, emoji));
        // our own sent message: from_me = true
        sender.React(thread, reply, "");
        Assert.True(_gw.Reactions[1].Target.FromMe);
        Assert.Equal("", _gw.Reactions[1].Emoji);
    }

    [Fact]
    public async Task Http_gateway_posts_quoted_id_and_react_bodies()
    {
        var sc = new ScopingService(_t.Db);
        sc.SetSetting("baileys_url", "http://gw.test/");
        sc.SetSetting("baileys_secret", "s3cret");
        var services = new ServiceCollection();
        services.AddScoped(_ => new ScopingService(_t.Db));
        await using var sp = services.BuildServiceProvider();
        var stub = new GatewayStub();
        var gw = new HttpBaileysGateway(new FakeHttpFactory(stub), sp.GetRequiredService<IServiceScopeFactory>());

        var sent = await gw.SendTextAsync("gw1", "60123", "yes", new BaileysQuote("ABC", true, "Shall I book it?"));
        Assert.True(sent.Ok);
        var body = JsonSerializer.Deserialize<JsonElement>(stub.Requests[0].Body);
        Assert.Equal("ABC", body.GetProperty("quoted_id").GetString());
        Assert.True(body.GetProperty("quoted_from_me").GetBoolean());
        Assert.Equal("Shall I book it?", body.GetProperty("quoted_text").GetString());
        // a plain send carries no quote keys
        await gw.SendTextAsync("gw1", "60123", "plain");
        Assert.False(JsonSerializer.Deserialize<JsonElement>(stub.Requests[1].Body).TryGetProperty("quoted_id", out _));

        var reacted = await gw.ReactAsync("gw1", "60123", new BaileysQuote("XYZ", false), "😂");
        Assert.True(reacted.Ok);
        Assert.EndsWith("/accounts/gw1/react", stub.Requests[2].Url);
        var react = JsonSerializer.Deserialize<JsonElement>(stub.Requests[2].Body);
        Assert.Equal(("XYZ", false, "😂"), (react.GetProperty("msg_id").GetString(), react.GetProperty("from_me").GetBoolean(),
            react.GetProperty("emoji").GetString()));
    }

    [Fact]
    public void Emoji_normalization_accepts_emoji_and_refuses_text()
    {
        Assert.Equal("👍", MessageReactions.Normalize(" 👍 "));
        Assert.Equal("❤️", MessageReactions.Normalize("❤️"));
        Assert.Equal("👨‍👩‍👧", MessageReactions.Normalize("👨‍👩‍👧"));
        Assert.Equal("1️⃣", MessageReactions.Normalize("1️⃣"));
        Assert.Equal("", MessageReactions.Normalize(""));
        Assert.Null(MessageReactions.Normalize("ok"));
        Assert.Null(MessageReactions.Normalize("1"));
        Assert.Null(MessageReactions.Normalize("<b>"));
        Assert.Null(MessageReactions.Normalize(string.Concat(Enumerable.Repeat("😀", 20))));
        Assert.Equal("[contact] Ana · +6012", MessageCards.Contact("Ana", "+6012"));
        Assert.Equal(("Ana Lim", "+60 12"), MessageCards.FromVCard("BEGIN:VCARD\nFN:Ana Lim\nitem1.TEL;waid=6012:+60 12\nEND:VCARD"));
    }
}
