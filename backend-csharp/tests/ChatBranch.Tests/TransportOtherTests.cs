using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Email (SMTP out / IMAP in), Telegram (Bot API out, webhook in) and the
/// channel-health checker, end to end through the API host with every provider
/// faked (no network): routing, threading headers, every sender kept (unlinked
/// when not a contact; auto-replies and our own address skipped),
/// dedup, media both ways, webhook auth, health verdicts and the reconnect reset.
/// </summary>
public class TransportOtherTests : IClassFixture<TransportFactory>
{
    private readonly TransportFactory _f;

    public TransportOtherTests(TransportFactory f)
    {
        _f = f;
        _f.Email.Reset();
        _f.TelegramApi.Reset();
        _f.Graph.Answer = _ => new GraphPing(true, 200, null, "");
        _f.Mailbox.Fail = _ => null;
    }

    // ---- helpers --------------------------------------------------------------

    private async Task<HttpClient> Authed(string user = "admin", string pass = "admin123")
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
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

    private ChannelAccount AddAccount(ChannelAccount a) => InDb(db =>
    {
        a.CreatedAt = DateTime.UtcNow;
        db.ChannelAccounts.Add(a);
        db.SaveChanges();
        return a;
    });

    private ChannelAccount Account(int id) => InDb(db => db.ChannelAccounts.AsNoTracking().First(a => a.Id == id));

    // Background work (media download, the post-save health re-probe) runs off the
    // request; a cold first run (JIT, parallel test classes) can take seconds, so
    // the ceiling is generous — the loop exits as soon as the condition holds.
    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met in time");
            await Task.Delay(50);
        }
    }

    private static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static object TgUpdate(long chatId, long messageId, string text, object? extra = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["message_id"] = messageId,
            ["date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["chat"] = new { id = chatId, type = "private" },
            ["from"] = new { id = chatId, is_bot = false, first_name = "Tele", last_name = "Gram" },
        };
        if (text.Length > 0) message["text"] = text;
        if (extra != null)
            foreach (var p in extra.GetType().GetProperties()) message[p.Name] = p.GetValue(extra);
        return new { update_id = messageId, message };
    }

    private static MimeMessage Mail(string from, string to, string subject, string body, string messageId,
        params (string Name, byte[] Bytes)[] attachments)
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(to));
        m.Subject = subject;
        m.MessageId = messageId;
        m.Date = DateTimeOffset.UtcNow.AddMinutes(-5);
        var b = new BodyBuilder { TextBody = body };
        foreach (var (name, bytes) in attachments) b.Attachments.Add(name, bytes);
        m.Body = b.ToMessageBody();
        return m;
    }

    private async Task<JsonElement> CreateMailbox(HttpClient admin, string username, string from)
    {
        var res = await admin.PostAsJsonAsync("/api/messaging/accounts", new
        {
            channel = "email", name = "Support", host = "smtp.test", port = "587", username,
            password = "app-password-1", from_email = from, imap_host = "imap.test",
        });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ---- Telegram: inbound ------------------------------------------------------

    [Fact]
    public async Task Telegram_webhook_routes_by_token_checks_the_secret_and_heals_health()
    {
        var bot = AddAccount(new ChannelAccount
        {
            Channel = "telegram", Name = "TG secret bot", AccessToken = "5001:secret-bot-token-aaaa",
            HealthStatus = Health.Degraded,
            Config = new() { ["webhook_secret"] = "hook-secret-1" },
        });
        var c = _f.CreateClient();

        var unknown = await c.PostAsync("/api/messaging/webhook/telegram/9999:not-a-bot", Json(TgUpdate(1, 1, "x")));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var url = $"/api/messaging/webhook/telegram/{bot.AccessToken}";
        var noSecret = await c.PostAsync(url, Json(TgUpdate(7001, 10, "hello bot")));
        Assert.Equal(HttpStatusCode.Unauthorized, noSecret.StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = Json(TgUpdate(7001, 10, "hello bot")) };
        req.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "hook-secret-1");
        (await c.SendAsync(req)).EnsureSuccessStatusCode();

        // Telegram retries a slow ack — the redelivery must not store (or page) twice
        var again = new HttpRequestMessage(HttpMethod.Post, url) { Content = Json(TgUpdate(7001, 10, "hello bot")) };
        again.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "hook-secret-1");
        (await c.SendAsync(again)).EnsureSuccessStatusCode();

        // a bot's own echo is ignored
        var echo = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = Json(new { update_id = 99, message = new { message_id = 11, chat = new { id = 7001 },
                from = new { id = 5001, is_bot = true, first_name = "me" }, text = "echo" } }),
        };
        echo.Headers.Add("X-Telegram-Bot-Api-Secret-Token", "hook-secret-1");
        (await c.SendAsync(echo)).EnsureSuccessStatusCode();

        var (texts, threadAccount, name) = InDb(db =>
        {
            var t = db.Threads.First(x => x.Channel == "telegram" && x.Key == "7001");
            return (db.Messages.Where(m => m.ThreadId == t.Id).Select(m => m.Text).ToList(), t.AccountId, t.Name);
        });
        Assert.Equal(new[] { "hello bot" }, texts);
        Assert.Equal(bot.Id, threadAccount);
        Assert.Equal("Tele Gram", name);

        var healed = Account(bot.Id);
        Assert.Equal(Health.Healthy, healed.HealthStatus);
        Assert.NotNull(healed.LastInboundAt);
    }

    [Fact]
    public async Task Telegram_message_from_a_new_chat_lands_as_an_unlinked_conversation()
    {
        var bot = AddAccount(new ChannelAccount { Channel = "telegram", Name = "TG strangers", AccessToken = "5009:new-chat-bot-token-ffff" });
        var res = await _f.CreateClient().PostAsync($"/api/messaging/webhook/telegram/{bot.AccessToken}",
            Json(TgUpdate(7109, 90, "hi, is anyone there?")));
        res.EnsureSuccessStatusCode();
        await UnknownSenderAsserts.AssertListedAsNotInContactsAsync(_f, await Authed(), "telegram", "7109",
            "hi, is anyone there?");
    }

    [Fact]
    public async Task Telegram_photo_is_downloaded_off_the_ack_and_served_auth_gated()
    {
        var bot = AddAccount(new ChannelAccount { Channel = "telegram", Name = "TG media", AccessToken = "5002:media-bot-token-bbbb" });
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        _f.TelegramApi.Files["photos/file_1.jpg"] = jpeg;

        var update = TgUpdate(7002, 20, "", new
        {
            caption = "my receipt",
            photo = new[] { new { file_id = "small", width = 90 }, new { file_id = "large-id", width = 800 } },
        });
        var res = await _f.CreateClient().PostAsync($"/api/messaging/webhook/telegram/{bot.AccessToken}", Json(update));
        res.EnsureSuccessStatusCode();

        int msgId = 0;
        await WaitFor(() => InDb(db =>
        {
            var m = db.Messages.AsNoTracking().FirstOrDefault(x => x.Thread!.Key == "7002" && x.Thread.Channel == "telegram");
            if (m == null || m.MediaPath.Length == 0) return false;
            msgId = m.Id;
            return true;
        }));
        var stored = InDb(db => db.Messages.AsNoTracking().First(m => m.Id == msgId));
        Assert.Equal("my receipt", stored.Text);
        Assert.Equal("image", stored.MediaType);
        Assert.Contains(_f.TelegramApi.Calls, call => call.Method == "getFile" && call.Body.Contains("large-id"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync($"/api/messaging/media/{msgId}")).StatusCode);
        var admin = await Authed();
        var media = await admin.GetAsync($"/api/messaging/media/{msgId}");
        media.EnsureSuccessStatusCode();
        Assert.Equal(jpeg, await media.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/messaging/media/999999")).StatusCode);
    }

    // ---- Telegram: outbound + account actions -----------------------------------

    [Fact]
    public async Task Telegram_send_uses_the_bot_api_and_only_token_errors_hurt_health()
    {
        var bot = AddAccount(new ChannelAccount { Channel = "telegram", Name = "TG send", AccessToken = "5003:send-bot-token-cccc",
            HealthStatus = Health.Healthy });
        InDb(db =>
        {
            db.Threads.Add(new Thread { Channel = "telegram", Key = "7003", Address = "7003", Name = "Chat",
                AccountId = bot.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        var admin = await Authed();

        var ok = await admin.PostAsJsonAsync("/api/messaging/threads/telegram/7003/send", new { text = "Hi from the inbox" });
        ok.EnsureSuccessStatusCode();
        var sent = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("sent", sent.GetProperty("status").GetString());
        var call = _f.TelegramApi.Calls.Last(x => x.Method == "sendMessage");
        Assert.Equal(bot.AccessToken, call.Token);
        using (var body = JsonDocument.Parse(call.Body))
        {
            Assert.Equal("7003", body.RootElement.GetProperty("chat_id").GetString());
            Assert.Equal("Hi from the inbox", body.RootElement.GetProperty("text").GetString());
        }

        // blocked by this user = the recipient's problem: failed message, health untouched
        _f.TelegramApi.Respond = (_, m, _) => m == "sendMessage"
            ? FakeTelegramApi.Error(HttpStatusCode.Forbidden, "Forbidden: bot was blocked by the user") : null;
        var blocked = await admin.PostAsJsonAsync("/api/messaging/threads/telegram/7003/send", new { text = "again" });
        var failed = (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("blocked", failed.GetProperty("text").GetString());
        Assert.Equal(Health.Healthy, Account(bot.Id).HealthStatus);

        // a revoked token is the account's problem — and the token never leaks into the thread
        _f.TelegramApi.Respond = (token, m, _) => m == "sendMessage"
            ? FakeTelegramApi.Error(HttpStatusCode.Unauthorized, $"Unauthorized {token}") : null;
        var revoked = await admin.PostAsJsonAsync("/api/messaging/threads/telegram/7003/send", new { text = "third" });
        var revokedMsg = (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.DoesNotContain(bot.AccessToken, revokedMsg.GetProperty("text").GetString());
        Assert.Equal(Health.Disconnected, Account(bot.Id).HealthStatus);
    }

    [Fact]
    public async Task Telegram_set_webhook_registers_a_secret_and_never_echoes_the_token()
    {
        var bot = AddAccount(new ChannelAccount { Channel = "telegram", Name = "TG hook", AccessToken = "5004:hook-bot-token-dddd" });
        var admin = await Authed();
        var url = $"/api/messaging/accounts/{bot.Id}/set-webhook";

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync(url, new { base_url = "http://not-https.example" })).StatusCode);

        var res = await admin.PostAsJsonAsync(url, new { base_url = "https://public.example/" });
        res.EnsureSuccessStatusCode();
        var raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(bot.AccessToken, raw);
        Assert.Equal("https://public.example/api/messaging/webhook/telegram/<bot-token>",
            JsonDocument.Parse(raw).RootElement.GetProperty("url").GetString());

        var call = _f.TelegramApi.Calls.Last(x => x.Method == "setWebhook");
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal($"https://public.example/api/messaging/webhook/telegram/{bot.AccessToken}",
            body.RootElement.GetProperty("url").GetString());
        var secret = body.RootElement.GetProperty("secret_token").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{20,256}$", secret);
        Assert.Equal(secret, EmailConfig.Cfg(Account(bot.Id).Config, "webhook_secret"));

        // the registered secret is now required on the webhook
        var c = _f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c.PostAsync($"/api/messaging/webhook/telegram/{bot.AccessToken}", Json(TgUpdate(7004, 1, "x")))).StatusCode);

        _f.TelegramApi.Respond = (_, m, _) => m == "setWebhook"
            ? FakeTelegramApi.Error(HttpStatusCode.BadRequest, "Bad Request: bad webhook: HTTPS url must be provided") : null;
        Assert.Equal(HttpStatusCode.BadGateway,
            (await admin.PostAsJsonAsync(url, new { base_url = "https://public.example" })).StatusCode);
    }

    [Fact]
    public async Task Account_test_is_live_for_telegram_and_email()
    {
        var bot = AddAccount(new ChannelAccount { Channel = "telegram", Name = "TG test", AccessToken = "5005:test-bot-token-eeee" });
        var admin = await Authed();

        var ok = await admin.PostAsJsonAsync($"/api/messaging/accounts/{bot.Id}/test", new { });
        ok.EnsureSuccessStatusCode();
        var detail = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();
        Assert.Contains("@test_bot", detail);
        Assert.Contains("webhook registered", detail);
        Assert.Equal("777", Account(bot.Id).ExternalId); // learned from getMe

        _f.TelegramApi.Respond = (_, m, _) => m == "getMe" ? FakeTelegramApi.Error(HttpStatusCode.Unauthorized, "Unauthorized") : null;
        var bad = await admin.PostAsJsonAsync($"/api/messaging/accounts/{bot.Id}/test", new { });
        Assert.Equal(HttpStatusCode.BadGateway, bad.StatusCode);
        Assert.Contains("invalid or revoked", await bad.Content.ReadAsStringAsync());

        var mailbox = await CreateMailbox(admin, "tester@shop.test", "tester@shop.test");
        var id = mailbox.GetProperty("id").GetString();
        var probe = await admin.PostAsJsonAsync($"/api/messaging/accounts/{id}/test", new { });
        probe.EnsureSuccessStatusCode();
        var withMail = await admin.PostAsJsonAsync($"/api/messaging/accounts/{id}/test", new { to = "ops@shop.test" });
        withMail.EnsureSuccessStatusCode();
        Assert.Contains(_f.Email.Sent, s => s.Message.To.Mailboxes.Any(m => m.Address == "ops@shop.test")
                                            && s.Server.From == "tester@shop.test");

        _f.Email.ProbeResult = _ => new EmailResult(false, "SMTP login refused: 535", EmailFailure.Auth);
        var refused = await admin.PostAsJsonAsync($"/api/messaging/accounts/{id}/test", new { });
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Contains("Connection failed", await refused.Content.ReadAsStringAsync());
        // the payload never carries the password, and now shows the IMAP settings
        Assert.DoesNotContain("app-password-1", mailbox.ToString());
        Assert.Equal("imap.test", mailbox.GetProperty("config").GetProperty("imap_host").GetString());
    }

    // ---- Email: IMAP in, SMTP out -----------------------------------------------

    [Fact]
    public async Task Imap_sync_keeps_every_sender_dedups_and_the_reply_threads_under_it()
    {
        var admin = await Authed();
        await CreateMailbox(admin, "inbox@shop.test", "support@shop.test");
        var contactId = InDb(db =>
        {
            var c = new Contact { Name = "Known Customer", Email = "Known@Cust.test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            return c.Id;
        });

        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 fake");
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var mail = Mail("Known Customer <known@cust.test>", "support@shop.test", "Order 42",
            "Where is my order?\n\nOn Mon, 6 Oct 2026 at 10:00, Support <support@shop.test> wrote:\n> We shipped it.",
            "m1@cust.test", ("invoice.pdf", pdf), ("photo.png", png));
        _f.Mailbox.Deliver("inbox@shop.test", mail);
        _f.Mailbox.Deliver("inbox@shop.test", Mail("news@letters.test", "support@shop.test", "Weekly digest",
            "Big sale!", "n1@letters.test"));
        // an out-of-office from a known address is not the customer talking (RFC 3834)
        var ooo = Mail("known@cust.test", "support@shop.test", "Out of office", "I am away.", "ooo1@cust.test");
        ooo.Headers.Add(HeaderId.AutoSubmitted, "auto-replied");
        _f.Mailbox.Deliver("inbox@shop.test", ooo);
        // … and neither is a copy of our own mail landing back in the mailbox
        _f.Mailbox.Deliver("inbox@shop.test", Mail("Support <support@shop.test>", "support@shop.test", "Re: Order 42",
            "Sent from the shop.", "own1@shop.test"));

        var sync = await admin.PostAsJsonAsync("/api/messaging/email/sync", new { force = true });
        sync.EnsureSuccessStatusCode();
        var report = await sync.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(report.GetProperty("configured").GetBoolean());
        Assert.Equal(2, report.GetProperty("fetched").GetInt32()); // the customer AND the newsletter
        Assert.Equal(2, report.GetProperty("skipped").GetInt32()); // the auto-reply + our own address

        var (thread, msgs) = InDb(db =>
        {
            var t = db.Threads.AsNoTracking().First(x => x.Channel == "email" && x.Key == "known@cust.test");
            return (t, db.Messages.AsNoTracking().Where(m => m.ThreadId == t.Id).OrderBy(m => m.Id).ToList());
        });
        Assert.Equal(contactId, thread.ContactId);
        Assert.False(thread.AiEnabled);                      // email ships with the chatbot off
        Assert.Equal("Order 42\n\nWhere is my order?", msgs[0].Text); // quoted history stripped
        Assert.Equal("<m1@cust.test>", msgs[0].ExternalId);
        Assert.Equal("document", msgs[0].MediaType);
        Assert.Equal("[attachment] photo.png", msgs[1].Text);
        Assert.Equal("image", msgs[1].MediaType);
        Assert.Equal(2, msgs.Count); // the auto-reply was not stored
        // a sender who is not a contact is still a conversation — unlinked, chatbot off
        var news = InDb(db => db.Threads.AsNoTracking().Single(t => t.Channel == "email" && t.Key == "news@letters.test"));
        Assert.Null(news.ContactId);
        Assert.False(news.AiEnabled);
        Assert.False(InDb(db => db.Threads.Any(t => t.Channel == "email" && t.Key == "support@shop.test")));

        var pdfBack = await admin.GetAsync($"/api/messaging/media/{msgs[0].Id}");
        pdfBack.EnsureSuccessStatusCode();
        Assert.Equal(pdf, await pdfBack.Content.ReadAsByteArrayAsync());

        // the same mail again (another client unmarked it) is a duplicate, not a second message
        _f.Mailbox.Deliver("inbox@shop.test", Mail("known@cust.test", "support@shop.test", "Order 42", "Where?", "m1@cust.test"));
        var again = await (await admin.PostAsJsonAsync("/api/messaging/email/sync", new { force = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, again.GetProperty("duplicates").GetInt32());
        Assert.Equal(msgs.Count, InDb(db => db.Messages.Count(m => m.ThreadId == thread.Id)));

        // the reply leaves through the same mailbox, threaded under the customer's mail
        var reply = await admin.PostAsJsonAsync("/api/messaging/threads/email/known@cust.test/send", new { text = "It's on its way." });
        reply.EnsureSuccessStatusCode();
        var replyMsg = (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("sent", replyMsg.GetProperty("status").GetString());
        var (server, mime) = _f.Email.Sent.Last();
        Assert.Equal("support@shop.test", server.From);
        Assert.Equal("Re: Order 42", mime.Subject);
        Assert.Equal("m1@cust.test", mime.InReplyTo);
        Assert.Contains("m1@cust.test", mime.References);
        Assert.Equal("known@cust.test", mime.To.Mailboxes.Single().Address);
        Assert.Equal($"<{mime.MessageId}>", replyMsg.GetProperty("id").GetString());

        // a second reply stays "Re: Order 42" (no "Re: Re:") and references both mails
        await admin.PostAsJsonAsync("/api/messaging/threads/email/known@cust.test/send", new { text = "Tracking: 123" });
        var second = _f.Email.Sent.Last().Message;
        Assert.Equal("Re: Order 42", second.Subject);
        Assert.Equal(2, second.References.Count);
    }

    [Fact]
    public async Task Imap_sync_throttles_admits_correspondents_and_reports_a_refused_login()
    {
        var admin = await Authed();
        var box = await CreateMailbox(admin, "second@shop.test", "second@shop.test");
        var boxId = int.Parse(box.GetProperty("id").GetString()!);

        // a reply from an address we already wrote to (no CRM contact) continues that conversation
        InDb(db =>
        {
            var t = new Thread { Channel = "email", Key = "prospect@lead.test", Address = "prospect@lead.test",
                Name = "Prospect", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Threads.Add(t);
            db.SaveChanges();
            db.Messages.Add(new Message { ThreadId = t.Id, Direction = "out", Source = "agent", Text = "Hello!",
                Status = "sent", CreatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        _f.Mailbox.Deliver("second@shop.test", Mail("prospect@lead.test", "second@shop.test", "Re: Hello", "Interested!", "p1@lead.test"));

        EmailSyncService.ResetThrottle();
        var first = await (await admin.PostAsJsonAsync("/api/messaging/email/sync", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(first.GetProperty("throttled").GetBoolean());
        Assert.True(first.GetProperty("fetched").GetInt32() >= 1);
        Assert.True(InDb(db => db.Messages.Any(m => m.Thread!.Key == "prospect@lead.test" && m.Text == "Interested!")));

        var second = await (await admin.PostAsJsonAsync("/api/messaging/email/sync", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(second.GetProperty("throttled").GetBoolean());

        _f.Mailbox.Fail = mb => mb.ImapUsername == "second@shop.test"
            ? new MailboxException("IMAP login refused: AUTHENTICATIONFAILED", authFailure: true) : null;
        var refused = await (await admin.PostAsJsonAsync("/api/messaging/email/sync", new { force = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains(refused.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("login refused"));
        Assert.DoesNotContain("app-password-1", refused.ToString());
        Assert.Equal(Health.Disconnected, Account(boxId).HealthStatus);
    }

    /// <summary>
    /// The known-sender gate is gone: mail from someone who is not a contact (and
    /// whom we never wrote to) opens an unlinked conversation the inbox lists under
    /// "Not in contacts" — and nothing remembers a sender, so their next mail lands
    /// in the same conversation (the old gate's "one look, ever" was per mail).
    /// </summary>
    [Fact]
    public async Task Imap_sync_brings_mail_from_a_new_sender_in_as_an_unlinked_chat()
    {
        var admin = await Authed();
        await CreateMailbox(admin, "fresh@shop.test", "fresh@shop.test");
        _f.Mailbox.Deliver("fresh@shop.test", Mail("Stranger Lim <Stranger@New.test>", "fresh@shop.test", "Quote please",
            "Can I get a quote for 40 chairs?", "q1@new.test"));

        var report = await (await admin.PostAsJsonAsync("/api/messaging/email/sync", new { force = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, report.GetProperty("fetched").GetInt32());
        Assert.Equal(0, report.GetProperty("skipped").GetInt32());

        var thread = InDb(db => db.Threads.AsNoTracking().Single(t => t.Channel == "email" && t.Key == "stranger@new.test"));
        Assert.Null(thread.ContactId);
        Assert.False(thread.AiEnabled);               // email ships with the chatbot off
        Assert.Equal("Stranger Lim", thread.Name);
        Assert.NotNull(thread.AccountId);              // filed under the mailbox that received it
        await UnknownSenderAsserts.AssertListedAsNotInContactsAsync(_f, admin, "email", "stranger@new.test",
            "Can I get a quote for 40 chairs?");

        _f.Mailbox.Deliver("fresh@shop.test", Mail("stranger@new.test", "fresh@shop.test", "Re: Quote please",
            "Any news?", "q2@new.test"));
        await admin.PostAsJsonAsync("/api/messaging/email/sync", new { force = true });
        Assert.Equal(new[] { "Quote please\n\nCan I get a quote for 40 chairs?", "Any news?" },
            InDb(db => db.Messages.AsNoTracking().Where(m => m.ThreadId == thread.Id && m.Direction == "in")
                .OrderBy(m => m.Id).Select(m => m.Text).ToList()));
    }

    [Fact]
    public async Task Email_send_failures_classify_health_and_attachments_ride_along()
    {
        var admin = await Authed();
        var box = await CreateMailbox(admin, "third@shop.test", "third@shop.test");
        var boxId = int.Parse(box.GetProperty("id").GetString()!);
        await WaitFor(() => Account(boxId).HealthStatus == Health.Healthy); // the post-create probe
        InDb(db =>
        {
            db.Threads.Add(new Thread { Channel = "email", Key = "buyer@cust.test", Address = "buyer@cust.test",
                Name = "Buyer", AccountId = boxId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        var send = "/api/messaging/threads/email/buyer@cust.test/send";

        // a refused recipient is never the mailbox's fault
        _f.Email.SendResult = _ => new EmailResult(false, "recipient refused: 550", EmailFailure.Recipient);
        var r1 = (await (await admin.PostAsJsonAsync(send, new { text = "one" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("failed", r1.GetProperty("status").GetString());
        Assert.Equal(Health.Healthy, Account(boxId).HealthStatus);

        // an unreachable server degrades only once it keeps happening
        _f.Email.SendResult = _ => new EmailResult(false, "SMTP server unreachable", EmailFailure.Connect);
        for (var i = 0; i < HealthRecorder.DegradeAfter - 1; i++) await admin.PostAsJsonAsync(send, new { text = $"c{i}" });
        Assert.Equal(Health.Healthy, Account(boxId).HealthStatus);
        await admin.PostAsJsonAsync(send, new { text = "last straw" });
        Assert.Equal(Health.Degraded, Account(boxId).HealthStatus);

        // a refused login disconnects it (and pages the admins)
        _f.Email.SendResult = _ => new EmailResult(false, "SMTP login refused: 535", EmailFailure.Auth);
        await admin.PostAsJsonAsync(send, new { text = "auth" });
        Assert.Equal(Health.Disconnected, Account(boxId).HealthStatus);
        Assert.True(InDb(db => db.Notifications.Any(n => n.Category == "account_health"
                                                         && n.Title.Contains("disconnected"))));

        // (a disconnected mailbox is skipped by routing; pretend the operator fixed it
        // far enough to be routable again, so this conversation keeps its own mailbox)
        InDb(db =>
        {
            db.ChannelAccounts.First(a => a.Id == boxId).HealthStatus = Health.Degraded;
            return db.SaveChanges();
        });

        // an attachment goes out as a MIME part and stays on the message
        _f.Email.SendResult = _ => EmailResult.Success;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF quote")), "file", "quote.pdf");
        form.Add(new StringContent("Here is the quote"), "caption");
        var media = await admin.PostAsync("/api/messaging/threads/email/buyer@cust.test/send-media", form);
        media.EnsureSuccessStatusCode();
        var mediaMsg = (await media.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("sent", mediaMsg.GetProperty("status").GetString());
        Assert.Equal("document", mediaMsg.GetProperty("media").GetProperty("type").GetString());
        var mime = _f.Email.Sent.Last().Message;
        Assert.Equal("quote.pdf", mime.Attachments.OfType<MimePart>().Single().FileName);
        Assert.Equal("Here is the quote", mime.TextBody.Trim());
        Assert.Equal(Health.Healthy, Account(boxId).HealthStatus); // a good send heals
    }

    // ---- health checker ----------------------------------------------------------

    [Fact]
    public async Task Check_now_probes_the_callers_accounts_and_saving_credentials_rechecks()
    {
        var cloud = AddAccount(new ChannelAccount { Channel = "whatsapp", Provider = "cloud", Name = "Cloud line",
            ExternalId = "pn-health-1", AccessToken = "EAAG-token-1", HealthStatus = Health.Healthy });
        _f.Graph.Answer = a => a.Id == cloud.Id
            ? new GraphPing(false, 401, 190, "Error validating access token: Session has expired")
            : new GraphPing(true, 200, null, "");
        var admin = await Authed();

        var res = await admin.PostAsync("/api/whatsapp/channels/health", null);
        res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<JsonElement>();
        var row = payload.GetProperty("accounts").EnumerateArray().First(a => a.GetProperty("id").GetString() == cloud.Id.ToString());
        Assert.Equal(Health.Disconnected, row.GetProperty("health").GetString());
        Assert.Contains("190", row.GetProperty("detail").GetString());
        Assert.True(InDb(db => db.AuditLogs.Any(a => a.Action == "account.health" && a.Target == "Cloud line")));

        // new credentials: silently back to unknown, then the background re-check decides
        _f.Graph.Answer = _ => new GraphPing(true, 200, null, "");
        var save = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{cloud.Id}", new { access_token = "EAAG-token-2" });
        save.EnsureSuccessStatusCode();
        await WaitFor(() => Account(cloud.Id).HealthStatus == Health.Healthy);

        // a re-tokened Telegram bot is re-probed too, and its old webhook secret is dropped
        var botRes = await admin.PostAsJsonAsync("/api/messaging/accounts", new { channel = "telegram", name = "Recheck bot",
            access_token = "5006:recheck-bot-token-ffff" });
        botRes.EnsureSuccessStatusCode();
        var botId = int.Parse((await botRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        await WaitFor(() => Account(botId).HealthStatus == Health.Healthy);
        InDb(db =>
        {
            var b = db.ChannelAccounts.First(a => a.Id == botId);
            b.Config = new Dictionary<string, object?>(b.Config) { ["webhook_secret"] = "old" };
            return db.SaveChanges();
        });
        _f.TelegramApi.Respond = (token, m, _) => token == "5006:new-token-gggggggg" && m == "getMe"
            ? FakeTelegramApi.Error(HttpStatusCode.Unauthorized, "Unauthorized") : null;
        var patch = await admin.PatchAsJsonAsync($"/api/messaging/accounts/{botId}", new { access_token = "5006:new-token-gggggggg" });
        patch.EnsureSuccessStatusCode();
        await WaitFor(() => Account(botId).HealthStatus == Health.Disconnected);
        Assert.Equal("", EmailConfig.Cfg(Account(botId).Config, "webhook_secret"));
    }

    [Fact]
    public async Task Prober_classifies_each_channel_and_skips_what_it_cannot_probe()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var tgApi = new FakeTelegramApi
        {
            Respond = (token, method, _) => (token, method) switch
            {
                ("1:revoked-token-xxxxxxxx", "getMe") => FakeTelegramApi.Error(HttpStatusCode.Unauthorized, "Unauthorized"),
                ("2:nohook-token-xxxxxxxxx", "getWebhookInfo") => FakeTelegramApi.Ok(new { url = "", pending_update_count = 0 }),
                ("3:backlog-token-xxxxxxxx", "getWebhookInfo") => FakeTelegramApi.Ok(new { url = "https://x.test/h", pending_update_count = 57 }),
                _ => null,
            },
        };
        var email = new FakeEmailTransport
        {
            ProbeResult = s => s.Host == "bad-login.test" ? new EmailResult(false, "SMTP login refused", EmailFailure.Auth)
                : s.Host == "down.test" ? new EmailResult(false, "SMTP server unreachable", EmailFailure.Connect)
                : EmailResult.Success,
        };
        var graph = new FakeGraphProbe
        {
            Answer = a => a.Name == "banned wa" ? new GraphPing(false, 400, 368, "Temporarily blocked")
                : new GraphPing(true, 200, null, ""),
        };
        ChannelAccount Add(ChannelAccount a) { a.CreatedAt = DateTime.UtcNow; t.Db.ChannelAccounts.Add(a); return a; }
        Dictionary<string, object?> Smtp(string host) => new()
            { ["host"] = host, ["username"] = "u", ["password"] = "p", ["from_email"] = "a@b.test" };

        var tgOk = Add(new() { Channel = "telegram", Name = "tg ok", AccessToken = "0:fine-token-xxxxxxxxxx" });
        var tgRevoked = Add(new() { Channel = "telegram", Name = "tg revoked", AccessToken = "1:revoked-token-xxxxxxxx", HealthStatus = Health.Healthy });
        var tgNoHook = Add(new() { Channel = "telegram", Name = "tg nohook", AccessToken = "2:nohook-token-xxxxxxxxx" });
        var tgBacklog = Add(new() { Channel = "telegram", Name = "tg backlog", AccessToken = "3:backlog-token-xxxxxxxx" });
        var tgSandbox = Add(new() { Channel = "telegram", Name = "tg sandbox" });
        var mailOk = Add(new() { Channel = "email", Name = "mail ok", Config = Smtp("smtp.ok.test") });
        var mailAuth = Add(new() { Channel = "email", Name = "mail auth", Config = Smtp("bad-login.test") });
        var mailDown = Add(new() { Channel = "email", Name = "mail down", Config = Smtp("down.test") });
        var waBanned = Add(new() { Channel = "whatsapp", Provider = "cloud", Name = "banned wa", ExternalId = "1", AccessToken = "t" });
        var baileys = Add(new() { Channel = "whatsapp", Provider = "baileys", Name = "qr", ExternalId = "gw", HealthStatus = Health.Degraded });
        var fb = Add(new() { Channel = "facebook", Name = "page", AccessToken = "page-token" });
        t.Db.SaveChanges();
        sc.SetSetting("smtp_host", "smtp.global.test");
        sc.SetSetting("smtp_from", "noreply@org.test");

        var prober = new ChannelHealthProber(t.Db, sc, new TelegramClient(tgApi, "https://tg.test"), email, graph);
        var summary = await prober.RunAsync();

        Assert.Equal(Health.Healthy, tgOk.HealthStatus);
        Assert.Equal(Health.Disconnected, tgRevoked.HealthStatus);
        Assert.Equal(Health.Degraded, tgNoHook.HealthStatus);
        Assert.Contains("no webhook", tgNoHook.HealthDetail);
        Assert.Equal(Health.Degraded, tgBacklog.HealthStatus);
        Assert.Equal(Health.Unknown, tgSandbox.HealthStatus); // unconfigured: never probed
        Assert.Equal(Health.Healthy, mailOk.HealthStatus);
        Assert.Equal(Health.Disconnected, mailAuth.HealthStatus);
        Assert.Equal(Health.Unknown, mailDown.HealthStatus);  // transient: counted, not yet degraded
        Assert.Equal(1, mailDown.FailStreak);
        Assert.Equal(Health.Banned, waBanned.HealthStatus);
        Assert.Equal(Health.Degraded, baileys.HealthStatus);  // no gateway attached: untouched
        Assert.Equal(Health.Healthy, fb.HealthStatus);
        Assert.Equal(9, summary.Checked);
        Assert.Equal(Health.Healthy, summary.GlobalEmail);
        Assert.Contains("\"healthy\"", sc.SettingValue(ChannelHealthProber.EmailHealthKey));
        Assert.NotEqual("", sc.SettingValue(ChannelHealthProber.CheckedAtKey));

        // the third consecutive transient failure degrades
        await prober.RunAsync();
        await prober.RunAsync();
        Assert.Equal(Health.Degraded, mailDown.HealthStatus);

        // one audit row per real transition, none for the repeats
        Assert.Equal(1, t.Db.AuditLogs.Count(a => a.Action == "account.health" && a.Target == "tg revoked"));
    }

    // ---- unit: routing, MIME, parsing, the real clients ---------------------------

    [Fact]
    public void Email_routing_prefers_the_mailbox_then_global_smtp_then_sandbox()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, sc);
        var transport = new FakeEmailTransport();
        var dataDir = Path.Combine(Path.GetTempPath(), $"cb-email-{Guid.NewGuid():N}");
        var email = new EmailChannel(t.Db, sc, threads, transport, dataDir);

        var thread = threads.GetOrCreateThread("email", "someone@cust.test", "Someone");
        Assert.Equal("sandbox", email.Send(thread, "nobody configured", MessageSource.Agent, null).Status);
        Assert.Empty(transport.Sent);

        sc.SetSetting("smtp_host", "smtp.org.test");
        sc.SetSetting("smtp_from", "hello@org.test");
        sc.SetSetting("email_subject", "A note from Org");
        var viaGlobal = email.Send(thread, "global fallback", MessageSource.Agent, null);
        Assert.Equal("sent", viaGlobal.Status);
        var (server, mime) = transport.Sent.Last();
        Assert.Null(server.AccountId);
        Assert.Equal("A note from Org", mime.Subject);

        var box = new ChannelAccount { Channel = "email", Name = "Desk", ExternalId = "desk@org.test", CreatedAt = DateTime.UtcNow,
            Config = new() { ["host"] = "smtp.desk.test", ["username"] = "desk", ["password"] = "pw", ["from_email"] = "desk@org.test" } };
        t.Db.ChannelAccounts.Add(box);
        t.Db.SaveChanges();
        email.Send(thread, "via the mailbox", MessageSource.Agent, null, subject: "Your booking");
        var (boxServer, boxMime) = transport.Sent.Last();
        Assert.Equal(box.Id, boxServer.AccountId);
        Assert.Equal("Your booking", boxMime.Subject);
        Assert.Equal("Desk", boxMime.From.Mailboxes.Single().Name);
        Assert.Equal(box.Id, thread.AccountId); // replies keep leaving through it
        Assert.True(email.WouldSendLive(thread, null));
    }

    [Fact]
    public void Email_parser_prefers_plain_text_strips_quotes_conservatively_and_flattens_html()
    {
        Assert.Equal("Thanks, received.", EmailParser.StripQuotes(
            "Thanks, received.\r\n\r\nOn Tue, Oct 7, 2026 at 9:00 AM Shop <hi@shop.test>\r\nwrote:\r\n> earlier"));
        Assert.Equal("Yes please", EmailParser.StripQuotes(
            "Yes please\n\n________________________________\nFrom: Shop\nSent: Monday\nTo: me\n\nold text"));
        Assert.Equal("ok\n-- me", EmailParser.StripQuotes("ok\n-- me\n\n> quoted\n> lines\n"));
        // an inline reply interleaved with quotes is kept whole
        const string inline = "> Can you come Monday?\nYes, at 10.\n> And bring the form?\nWill do.";
        Assert.Equal(inline, EmailParser.StripQuotes(inline));
        // a mail that is ALL quote keeps its text rather than becoming empty
        Assert.Equal("> only a quote", EmailParser.StripQuotes("> only a quote"));

        var html = new MimeMessage();
        html.From.Add(MailboxAddress.Parse("Html <HTML@Sender.test>"));
        html.Subject = "Hi";
        html.MessageId = "h1@sender.test";
        html.Body = new TextPart("html") { Text = "<html><style>p{}</style><p>Hello&nbsp;there</p><p>Line&amp;two</p></html>" };
        var parsed = EmailParser.Parse(html);
        Assert.Equal("html@sender.test", parsed.FromAddress);
        Assert.Equal("Html", parsed.FromName);
        Assert.Equal("<h1@sender.test>", parsed.MessageId);
        Assert.Contains("Hello", parsed.Body);
        Assert.Contains("Line&two", parsed.Body);
        Assert.DoesNotContain("<p>", parsed.Body);

        var raw = "From: Raw <raw@x.test>\r\nTo: us@x.test\r\nSubject: Re: Order\r\nMessage-ID: <r1@x.test>\r\n" +
                  "In-Reply-To: <ours@x.test>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" +
                  "Got it.\r\n\r\n> earlier text\r\n";
        var fromRaw = EmailParser.Parse(MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw))));
        Assert.Equal("Got it.", fromRaw.Body);
        Assert.Equal("<ours@x.test>", fromRaw.InReplyTo);
        Assert.Empty(fromRaw.Attachments);
    }

    [Fact]
    public void Email_composer_threads_and_never_stacks_re()
    {
        Assert.Equal("Re: Order 7", EmailComposer.ReplySubject("Order 7"));
        Assert.Equal("RE: Order 7", EmailComposer.ReplySubject("RE: Order 7"));
        Assert.Null(EmailComposer.ReplySubject(" "));

        var server = new EmailServerSettings { From = "desk@org.test", FromName = "Org Desk" };
        var mime = EmailComposer.Build(server, "c@cust.test", "Customer", "Re: Order 7", "body",
            new[] { "<a@x.test>", "<b@x.test>", "not-an-id" }, "<b@x.test>",
            new[] { new EmailAttachment("f.txt", Encoding.ASCII.GetBytes("hi"), "text/plain") }, out var id);
        Assert.Equal($"<{mime.MessageId}>", id);
        Assert.EndsWith("@org.test", mime.MessageId);
        Assert.Equal("b@x.test", mime.InReplyTo);
        Assert.Contains("a@x.test", mime.References);
        Assert.Equal("Org Desk", mime.From.Mailboxes.Single().Name);
        Assert.Single(mime.Attachments);
    }

    [Fact]
    public async Task MailKit_transport_logs_in_sends_and_classifies_refusals_over_real_smtp()
    {
        var settings = new EmailServerSettings
        {
            Host = "127.0.0.1", Username = "desk@org.test", Password = "s3cret-pass", UseTls = false,
            From = "desk@org.test",
        };
        var transport = new MailKitEmailTransport(timeoutMs: 5000);
        var mime = EmailComposer.Build(settings, "c@cust.test", null, "Hello", "Body text",
            new[] { "<prev@cust.test>" }, "<prev@cust.test>", null, out _);

        using (var smtp = new FakeSmtpServer())
        {
            var s = settings with { Port = smtp.Port };
            var r = await transport.SendAsync(s, mime);
            Assert.True(r.Ok, r.Detail);
            Assert.NotEqual("", smtp.AuthPayload);
            Assert.Contains("Subject: Hello", smtp.Data);
            Assert.Contains("In-Reply-To: <prev@cust.test>", smtp.Data);
            Assert.True((await transport.ProbeAsync(s)).Ok);
        }
        using (var smtp = new FakeSmtpServer(rejectAuth: true))
        {
            var r = await transport.SendAsync(settings with { Port = smtp.Port }, mime);
            Assert.Equal(EmailFailure.Auth, r.Failure);
            Assert.DoesNotContain("s3cret-pass", r.Detail);
        }
        using (var smtp = new FakeSmtpServer(rejectRecipient: true))
        {
            var r = await transport.SendAsync(settings with { Port = smtp.Port }, mime);
            Assert.Equal(EmailFailure.Recipient, r.Failure);
        }
        // nothing listening: unreachable, not a credential problem
        var closedPort = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        closedPort.Start();
        var port = ((IPEndPoint)closedPort.LocalEndpoint).Port;
        closedPort.Stop();
        var down = await transport.ProbeAsync(settings with { Port = port });
        Assert.Equal(EmailFailure.Connect, down.Failure);
    }

    [Fact]
    public async Task Telegram_client_speaks_the_bot_api_and_scrubs_the_token()
    {
        var api = new FakeTelegramApi();
        var client = new TelegramClient(api, "https://tg.test");
        const string token = "42:very-secret-token-zzzz";

        var sent = await client.CallAsync(token, "sendMessage", new { chat_id = "9", text = "hi" });
        Assert.True(sent.Ok);
        Assert.True(sent.Long("message_id") > 0);
        Assert.Equal("sendMessage", api.Calls.Last().Method);

        var file = await client.SendFileAsync(token, "sendDocument", "document", "9", new byte[] { 1, 2 }, "a.pdf", "cap");
        Assert.True(file.Ok);
        Assert.Equal("multipart/form-data", api.Calls.Last().ContentType);

        api.Respond = (t, _, _) => FakeTelegramApi.Error(HttpStatusCode.Unauthorized, $"Unauthorized for bot{t}");
        var bad = await client.CallAsync(token, "getMe");
        Assert.False(bad.Ok);
        Assert.True(bad.TokenRejected);
        Assert.DoesNotContain(token, bad.Description);

        api.Reset();
        api.Files["photos/file_1.jpg"] = new byte[] { 9, 9, 9 };
        var (content, name, _) = await client.DownloadFileAsync(token, "fid", 1024);
        Assert.Equal(new byte[] { 9, 9, 9 }, content);
        Assert.Equal("file_1.jpg", name);
        var (tooBig, _, why) = await client.DownloadFileAsync(token, "fid", 2);
        Assert.Null(tooBig);
        Assert.Equal("file too large", why);

        Assert.True(TelegramBot.SecretEquals("abc", "abc"));
        Assert.False(TelegramBot.SecretEquals("abc", "abd"));
        Assert.False(TelegramBot.SecretEquals("", ""));
    }
}
