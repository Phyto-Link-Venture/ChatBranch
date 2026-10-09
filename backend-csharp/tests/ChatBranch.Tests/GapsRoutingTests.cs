using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The last routing/email gaps: HTML mail out (multipart/alternative, escaped),
/// HTML-only mail in (flattened to readable text), email mailboxes chosen by the
/// same send-time routing as WhatsApp numbers (least-loaded spread, failover off a
/// dead mailbox, the outlet's own first), Telegram's bot-bound conversations, and
/// instant AI replies routed like queued ones.
/// </summary>
public class GapsRoutingTests : IClassFixture<TransportFactory>
{
    private readonly TransportFactory _f;

    public GapsRoutingTests(TransportFactory f)
    {
        _f = f;
        _f.Email.Reset();
        _f.Mailbox.Fail = _ => null;
    }

    // ---- helpers --------------------------------------------------------------

    private static string Norm(string s) => s.Replace("\r\n", "\n");

    private static string TempDir() => Path.Combine(Path.GetTempPath(), $"cb-gaps-{Guid.NewGuid():N}");

    private static ChannelAccount Mailbox(AppDbContext db, string name, string address, int? entityId = null,
        string health = Health.Healthy)
    {
        var a = new ChannelAccount
        {
            Channel = "email", Name = name, ExternalId = address, EntityId = entityId, HealthStatus = health,
            CreatedAt = DateTime.UtcNow,
            Config = new() { ["host"] = $"smtp.{name.Replace(' ', '-').ToLowerInvariant()}.test",
                ["username"] = address, ["password"] = "pw", ["from_email"] = address },
        };
        db.ChannelAccounts.Add(a);
        db.SaveChanges();
        return a;
    }

    private static Contact Contact(AppDbContext db, string name, string email = "", string phone = "")
    {
        var c = new Contact { Name = name, Email = email, Phone = phone, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Contacts.Add(c);
        db.SaveChanges();
        return c;
    }

    private static void Admin(AppDbContext db)
    {
        var u = new AppUser { Username = $"boss-{Guid.NewGuid():N}"[..12], IsActive = true };
        db.Users.Add(u);
        db.SaveChanges();
        db.Profiles.Add(new Profile { UserId = u.Id, Role = Roles.SuperAdmin });
        db.SaveChanges();
    }

    private static void SetHealth(AppDbContext db, int accountId, string health)
    {
        db.ChannelAccounts.First(a => a.Id == accountId).HealthStatus = health;
        db.SaveChanges();
    }

    private static ChannelAccount WaNumber(AiRig r, string name, string? role, string health = Health.Healthy)
    {
        var a = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Cloud, Name = name, ExternalId = $"PN-{name}",
            AccessToken = "tok", HealthStatus = health, CreatedAt = DateTime.UtcNow,
            Config = role != null ? new() { ["role"] = role } : new(),
        };
        r.Db.ChannelAccounts.Add(a);
        r.Db.SaveChanges();
        return a;
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

    // =========================================================================
    // 1. HTML mail out
    // =========================================================================

    [Fact]
    public void Outgoing_mail_is_multipart_alternative_with_an_escaped_html_rendering_of_the_text()
    {
        var server = new EmailServerSettings { From = "desk@org.test", FromName = "Desk" };
        const string text = "Hi <b>Sam</b> & co,\n\n"
                            + "Track it: https://shop.test/o?id=7&k=\"x\" or see https://shop.test/faq.\n"
                            + "Questions? help@shop.test (or https://shop.test/a_(b)).\n"
                            + "<script>alert('x')</script>\n\n"
                            + "  indented javascript:alert(1)";
        var mime = EmailComposer.Build(server, "c@cust.test", "C", "Re: Order", text,
            new[] { "<a@x.test>" }, "<a@x.test>", null, out _);

        var alt = Assert.IsType<MultipartAlternative>(mime.Body);
        Assert.Equal(2, alt.Count);
        var plain = Assert.IsType<TextPart>(alt[0]);
        Assert.True(plain.IsPlain);
        Assert.Equal(text, Norm(plain.Text));                       // the text part is exactly what was written
        var htmlPart = Assert.IsType<TextPart>(alt[1]);
        Assert.True(htmlPart.IsHtml);
        var html = Norm(htmlPart.Text);

        // nothing typed is ever markup
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", html);
        Assert.Contains("Hi &lt;b&gt;Sam&lt;/b&gt; &amp; co,", html);
        // links: escaped hrefs, a quote can't break out of the attribute, punctuation stays outside
        Assert.Contains("<a href=\"https://shop.test/o?id=7&amp;k=\" style=", html);
        Assert.Contains("&quot;x&quot;", html);
        Assert.Contains(">https://shop.test/faq</a>.", html);
        Assert.Contains(">https://shop.test/a_(b)</a>).", html);
        Assert.Contains("<a href=\"mailto:help@shop.test\"", html);
        Assert.DoesNotContain("href=\"javascript", html);
        Assert.Contains("javascript:alert(1)", html);                 // stays plain text
        // paragraphs on blank lines, <br> on single newlines, indentation kept
        Assert.Equal(3, Regex.Matches(html, "<p ").Count);
        Assert.Contains("<br>", html);
        Assert.Contains("&nbsp;&nbsp;indented", html);
        Assert.Contains("font-family:", html);                       // the inline-styled wrapper
        // threading headers are untouched by the body change
        Assert.Equal("a@x.test", mime.InReplyTo);
        Assert.Contains("a@x.test", mime.References);

        // attachments wrap the alternative in multipart/mixed
        var withFile = EmailComposer.Build(server, "c@cust.test", null, "Quote", "See attached.",
            Array.Empty<string>(), null,
            new[] { new EmailAttachment("quote.pdf", Encoding.ASCII.GetBytes("%PDF"), "application/pdf") }, out _);
        var mixed = Assert.IsType<Multipart>(withFile.Body);
        Assert.Equal("mixed", mixed.ContentType.MediaSubtype);
        Assert.IsType<MultipartAlternative>(mixed[0]);
        Assert.Equal("quote.pdf", Assert.IsAssignableFrom<MimePart>(mixed[1]).FileName);
        Assert.Equal("See attached.", withFile.TextBody.Trim());
        Assert.Contains("See attached.", withFile.HtmlBody);
    }

    [Fact]
    public void An_ai_email_reply_carries_html_threading_and_auto_submitted()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, sc);
        var transport = new FakeEmailTransport();
        var email = new EmailChannel(t.Db, sc, threads, transport, TempDir());
        var box = Mailbox(t.Db, "Desk", "desk@org.test");
        var thread = threads.GetOrCreateThread("email", "ann@cust.test", "Ann", box.Id);
        threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer, "Where is it?",
            status: "received", externalId: "<q1@cust.test>", accountId: box.Id);

        var msg = email.Send(thread, "It ships <today> — https://shop.test/track", MessageSource.Ai, null);
        Assert.Equal("sent", msg.Status);
        var (_, mime) = transport.Sent.Last();
        Assert.Equal("auto-replied", mime.Headers[HeaderId.AutoSubmitted]);
        Assert.Equal("q1@cust.test", mime.InReplyTo);
        Assert.IsType<MultipartAlternative>(mime.Body);
        Assert.Contains("It ships &lt;today&gt;", mime.HtmlBody);
        Assert.Contains("<a href=\"https://shop.test/track\"", mime.HtmlBody);
    }

    // =========================================================================
    // 2. HTML-only mail in
    // =========================================================================

    [Fact]
    public void Html_only_mail_flattens_to_readable_text()
    {
        const string html = "<html><head><title>Ignored</title><style>p{color:red}</style>"
                            + "<script>steal()</script></head><body><!-- tracking --><!--[if mso]><p>mso</p><![endif]-->"
                            + "<p>Hi&nbsp;there,</p>\r\n<p>Is order <b>#42</b>\r\n shipped?<br>Thanks &amp; regards,<br/>Hana</p>"
                            + "<ul><li>one</li><li>two</li></ul>"
                            + "<p>Track: <a href=\"https://track.test/42?a=1&amp;b=2\">here</a> or "
                            + "<a href=\"https://x.test\">https://x.test</a></p>"
                            + "<div>&lt;not a tag&gt;</div><div><br></div><div>Bye</div></body></html>";
        var text = EmailParser.HtmlToText(html);
        Assert.Equal("Hi there,\n\nIs order #42 shipped?\nThanks & regards,\nHana\n\n• one\n• two\n\n"
                     + "Track: here (https://track.test/42?a=1&b=2) or https://x.test\n\n<not a tag>\n\nBye", text);

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse("Hana <hana@cust.test>"));
        mail.Subject = "Order";
        mail.MessageId = "h2@cust.test";
        mail.Body = new TextPart("html") { Text = html };
        var parsed = EmailParser.Parse(mail);
        Assert.StartsWith("Hi there,\n\nIs order #42 shipped?", parsed.Body);
        Assert.DoesNotContain("steal", parsed.Body);
        Assert.DoesNotContain("Ignored", parsed.Body);
        Assert.DoesNotContain("mso", parsed.Body);
    }

    [Fact]
    public async Task Imap_sync_stores_an_html_only_mail_as_readable_text()
    {
        var admin = await Authed();
        var res = await admin.PostAsJsonAsync("/api/messaging/accounts", new
        {
            channel = "email", name = "Html Desk", host = "smtp.test", port = "587", username = "htmldesk@shop.test",
            password = "app-password-1", from_email = "htmldesk@shop.test", imap_host = "imap.test",
        });
        res.EnsureSuccessStatusCode();
        InDb(db => Contact(db, "Hana", email: "hana.html@cust.test"));

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse("Hana <hana.html@cust.test>"));
        mail.To.Add(MailboxAddress.Parse("htmldesk@shop.test"));
        mail.Subject = "Order question";
        mail.MessageId = "html-only-1@cust.test";
        mail.Date = DateTimeOffset.UtcNow.AddMinutes(-2);
        mail.Body = new TextPart("html")
        {
            Text = "<html><head><style>.x{}</style></head><body><script>alert(1)</script>"
                   + "<p>Hello,</p><p>Is <b>#42</b> shipped?<br>Thanks &amp; regards</p>"
                   + "<div class=\"gmail_quote\">On Mon, 5 Oct 2026, Shop &lt;htmldesk@shop.test&gt; wrote:<br>"
                   + "<blockquote>We shipped it.</blockquote></div></body></html>",
        };
        _f.Mailbox.Deliver("htmldesk@shop.test", mail);

        var sync = await admin.PostAsJsonAsync("/api/messaging/email/sync", new { force = true });
        sync.EnsureSuccessStatusCode();
        var stored = InDb(db =>
        {
            var t = db.Threads.AsNoTracking().First(x => x.Channel == "email" && x.Key == "hana.html@cust.test");
            return db.Messages.AsNoTracking().Where(m => m.ThreadId == t.Id).OrderBy(m => m.Id).First();
        });
        Assert.Equal("Order question\n\nHello,\n\nIs #42 shipped?\nThanks & regards", stored.Text);
        Assert.Equal("<html-only-1@cust.test>", stored.ExternalId);
    }

    // =========================================================================
    // 3. Email mailboxes routed like WhatsApp numbers
    // =========================================================================

    [Fact]
    public void Email_spreads_contacts_over_the_least_loaded_mailbox_and_fails_over_off_a_dead_one()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, sc);
        var transport = new FakeEmailTransport();
        var email = new EmailChannel(t.Db, sc, threads, transport, TempDir());
        Admin(t.Db);
        var a = Mailbox(t.Db, "Desk A", "a@org.test");
        var b = Mailbox(t.Db, "Desk B", "b@org.test");

        var convs = Enumerable.Range(0, 4).Select(i =>
        {
            var c = Contact(t.Db, $"C{i}", email: $"c{i}@cust.test");
            return threads.GetOrCreateThread("email", c.Email, c.Name, contactId: c.Id);
        }).ToList();
        foreach (var conv in convs) Assert.Equal("sent", email.Send(conv, "Hello", MessageSource.Agent, null).Status);

        // an even spread, each contact's mailbox remembered (unpinned) and stamped on its thread
        var rows = t.Db.AccountAssignments.AsNoTracking().Where(x => x.Channel == "email").ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(x => x.AccountId == a.Id));
        Assert.Equal(2, rows.Count(x => x.AccountId == b.Id));
        Assert.All(rows, x => Assert.False(x.Pinned));
        Assert.All(convs, c => Assert.Equal(rows.Single(x => x.ContactId == c.ContactId).AccountId, c.AccountId));
        Assert.Equal(new[] { "a@org.test", "b@org.test", "a@org.test", "b@org.test" },
            transport.Sent.Select(s => s.Message.From.Mailboxes.Single().Address).ToArray());

        // a sticky row is honoured on the next send (no re-spread)
        var onA = convs.First(c => c.AccountId == a.Id);
        email.Send(onA, "Second", MessageSource.Agent, null);
        Assert.Equal(a.Id, transport.Sent.Last().Server.AccountId);

        // Desk A dies (operator had pinned the contact to it): failover to Desk B
        var row = t.Db.AccountAssignments.First(x => x.ContactId == onA.ContactId);
        row.Pinned = true;
        t.Db.SaveChanges();
        SetHealth(t.Db, a.Id, Health.Disconnected);
        Assert.Equal("sent", email.Send(onA, "Still there?", MessageSource.Agent, null).Status);
        Assert.Equal(b.Id, transport.Sent.Last().Server.AccountId);

        var fresh = t.Fresh();
        Assert.Equal(b.Id, fresh.Threads.First(x => x.Id == onA.Id).AccountId);   // the thread moved
        var moved = fresh.AccountAssignments.First(x => x.ContactId == onA.ContactId);
        Assert.Equal(b.Id, moved.AccountId);
        Assert.False(moved.Pinned);                                                  // failover clears the pin
        Assert.Contains(fresh.Messages, m => m.ThreadId == onA.Id && m.Source == MessageSource.System
                                             && m.Text.Contains("Desk A is disconnected")
                                             && m.Text.Contains("Desk B"));
        Assert.Contains(fresh.AuditLogs, x => x.Action == "account.failover");
        Assert.Contains(fresh.Notifications, n => n.Title.Contains("moved off Desk A"));

        // every mailbox down: refused honestly — failed, nothing sent, the thread untouched
        SetHealth(t.Db, b.Id, Health.Banned);
        var before = transport.Sent.Count;
        var refused = email.Send(onA, "Anyone?", MessageSource.Agent, null);
        Assert.Equal("failed", refused.Status);
        Assert.Contains("No healthy email mailbox", refused.Text);
        Assert.Equal(before, transport.Sent.Count);
        Assert.False(email.WouldSendLive(onA, null));
        Assert.Equal(b.Id, t.Fresh().Threads.First(x => x.Id == onA.Id).AccountId);
    }

    [Fact]
    public void Email_routing_uses_the_outlets_own_mailbox_before_the_shared_ones()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, sc);
        var transport = new FakeEmailTransport();
        var email = new EmailChannel(t.Db, sc, threads, transport, TempDir());
        var tenant = new Tenant { Name = "Org", Slug = "org", Status = "active" };
        t.Db.Tenants.Add(tenant);
        t.Db.SaveChanges();
        var outlet = new Entity { TenantId = tenant.Id, Name = "Branch", Slug = "branch" };
        t.Db.Entities.Add(outlet);
        t.Db.SaveChanges();

        var shared = Mailbox(t.Db, "Shared", "hello@org.test");
        var own = Mailbox(t.Db, "Branch Desk", "branch@org.test", entityId: outlet.Id);
        // the outlet's mailbox is busier — it still wins for the outlet's contact
        foreach (var i in Enumerable.Range(0, 3))
        {
            var other = Contact(t.Db, $"Busy{i}");
            t.Db.AccountAssignments.Add(new AccountAssignment
            {
                ContactId = other.Id, Channel = "email", AccountId = own.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
        var c = Contact(t.Db, "Local", email: "local@cust.test");
        t.Db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = outlet.Id, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var thread = threads.GetOrCreateThread("email", c.Email, c.Name, contactId: c.Id);
        Assert.Equal(outlet.Id, thread.EntityId);
        email.Send(thread, "Hi from your branch", MessageSource.Agent, null);
        Assert.Equal(own.Id, transport.Sent.Last().Server.AccountId);

        // an unattributed conversation with no contact takes the least-loaded shared/any mailbox
        var stranger = threads.GetOrCreateThread("email", "walkin@cust.test", "Walk-in");
        email.Send(stranger, "Hello", MessageSource.Agent, null);
        Assert.Equal(shared.Id, transport.Sent.Last().Server.AccountId);
        Assert.Equal(shared.Id, stranger.AccountId);
        Assert.False(t.Db.AccountAssignments.Any(x => x.Channel == "email" && x.AccountId == shared.Id));
    }

    // =========================================================================
    // 4. Telegram: a chat belongs to its bot
    // =========================================================================

    [Fact]
    public void Telegram_keeps_a_chat_on_its_own_bot_and_routes_a_botless_one()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, sc);
        var api = new FakeTelegramApi();
        var tg = new TelegramChannel(t.Db, sc, threads, new TelegramClient(api, "https://tg.test"));
        ChannelAccount Bot(string name, string health)
        {
            var a = new ChannelAccount { Channel = "telegram", Name = name, AccessToken = $"{name}:token",
                HealthStatus = health, CreatedAt = DateTime.UtcNow };
            t.Db.ChannelAccounts.Add(a);
            t.Db.SaveChanges();
            return a;
        }
        var dead = Bot("deadbot", Health.Disconnected);
        var live = Bot("livebot", Health.Healthy);

        // the customer started deadbot: another bot can't write to them, so no
        // failover — the send fails honestly on its own bot
        api.Respond = (token, method, _) => token.StartsWith("deadbot") && method == "sendMessage"
            ? FakeTelegramApi.Error(System.Net.HttpStatusCode.Unauthorized, "Unauthorized") : null;
        var chat = threads.GetOrCreateThread("telegram", "4401", "Chat", dead.Id);
        Assert.Equal(dead.Id, tg.Route(chat, null)!.Id);
        Assert.Equal("failed", tg.Send(chat, "hello", MessageSource.Agent, null).Status);
        Assert.Equal("deadbot:token", api.Calls.Last().Token);
        Assert.Equal(dead.Id, t.Fresh().Threads.First(x => x.Id == chat.Id).AccountId);

        // a bot-less conversation of a contact: routed (the eligible bot), remembered
        var c = Contact(t.Db, "Tele");
        var botless = threads.GetOrCreateThread("telegram", "4402", "Tele", contactId: c.Id);
        Assert.Equal("sent", tg.Send(botless, "hi", MessageSource.Agent, null).Status);
        Assert.Equal("livebot:token", api.Calls.Last().Token);
        Assert.Equal(live.Id, botless.AccountId);
        Assert.Equal(live.Id, t.Db.AccountAssignments.Single(x => x.ContactId == c.Id && x.Channel == "telegram").AccountId);
    }

    // =========================================================================
    // 5. Instant AI replies are routed like queued ones
    // =========================================================================

    [Fact]
    public void An_instant_ai_reply_fails_over_off_a_banned_conversation_number()
    {
        using var r = new AiRig();                                       // ai_reply_mode = instant
        Admin(r.Db);
        var dead = WaNumber(r, "DeadPhone", AccountRoles.Conversation, Health.Banned);
        WaNumber(r, "Business", null);                                   // healthy, reminder, idle, lower id
        var alive = WaNumber(r, "AlivePhone", AccountRoles.Conversation);
        var c = r.Contact("Rina", "60120000901");
        r.Threads.GetOrCreateThread("whatsapp", "60120000901", accountId: dead.Id, contactId: c.Id);
        r.Db.AccountAssignments.Add(new AccountAssignment
        {
            ContactId = c.Id, Channel = "whatsapp", AccountId = dead.Id, Pinned = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        r.Db.SaveChanges();
        r.Llm.Replies.Enqueue("It ships today.");

        r.Say("60120000901", "When does my order ship?", accountId: dead.Id);

        var ai = Assert.Single(r.AiMessages("60120000901"));
        Assert.Equal("It ships today.", ai.Text);
        Assert.Equal("sent", ai.Status);
        Assert.Equal(alive.Id, ai.AccountId);                            // never the business number
        Assert.Single(r.Wa.Sent);
        var fresh = r.T.Fresh();
        var thread = fresh.Threads.First(x => x.Key == "60120000901");
        Assert.Equal(alive.Id, thread.AccountId);
        var asg = fresh.AccountAssignments.Single(x => x.ContactId == c.Id);
        Assert.Equal(alive.Id, asg.AccountId);
        Assert.False(asg.Pinned);
        Assert.Single(fresh.Messages.Where(m => m.ThreadId == thread.Id && m.Source == MessageSource.System
                                                && m.Text.Contains("DeadPhone is banned")));
        Assert.Contains(fresh.AuditLogs, x => x.Action == "account.failover");
        Assert.Contains(fresh.Notifications, n => n.Title.Contains("moved off DeadPhone"));
        var draft = Assert.Single(r.Drafts("60120000901"));
        Assert.Equal(AiDraftStatus.AutoSent, draft.Status);
        Assert.Equal(alive.Id, draft.AccountId);
    }

    [Fact]
    public void A_queued_ai_reply_is_routed_once_and_an_approved_draft_is_routed_at_approval()
    {
        // queued: the drain routes (one failover note), the gate finds it already on the new number
        using (var r = new AiRig())
        {
            r.Set("ai_wait_seconds", "30");
            var dead = WaNumber(r, "Gone", AccountRoles.Conversation, Health.Disconnected);
            var alive = WaNumber(r, "Here", AccountRoles.Conversation);
            var c = r.Contact("Q", "60120000902");
            r.Threads.GetOrCreateThread("whatsapp", "60120000902", accountId: dead.Id, contactId: c.Id);
            r.Say("60120000902", "hello?", accountId: dead.Id);
            Assert.Empty(r.AiMessages("60120000902"));
            r.Clock.Advance(TimeSpan.FromMinutes(2));
            r.Queue.Drain();

            var ai = Assert.Single(r.AiMessages("60120000902"));
            Assert.Equal(alive.Id, ai.AccountId);
            var t = r.ThreadOf("60120000902");
            Assert.Single(r.T.Fresh().Messages.Where(m => m.ThreadId == t.Id && m.Source == MessageSource.System
                                                          && m.Text.Contains("Gone is disconnected")));
        }

        // review: held on a healthy number, the number dies, approval sends from the routed one
        using (var r = new AiRig())
        {
            r.Set("ai_review_mode", "review_all");
            var first = WaNumber(r, "First", AccountRoles.Conversation);
            var second = WaNumber(r, "Second", AccountRoles.Conversation);
            var c = r.Contact("R", "60120000903");
            r.Threads.GetOrCreateThread("whatsapp", "60120000903", accountId: first.Id, contactId: c.Id);
            r.Say("60120000903", "price?", accountId: first.Id);
            var held = Assert.Single(r.Drafts("60120000903"));
            Assert.Equal(AiDraftStatus.Pending, held.Status);

            SetHealth(r.Db, first.Id, Health.Banned);
            var outcome = r.Gate.Approve(held.Id, "boss");
            Assert.Equal(AiGateStatus.Sent, outcome.Status);
            var ai = Assert.Single(r.AiMessages("60120000903"));
            Assert.Equal(second.Id, ai.AccountId);
            Assert.Equal(second.Id, r.T.Fresh().Threads.First(x => x.Key == "60120000903").AccountId);
        }
    }

    [Fact]
    public void An_instant_ai_reply_with_no_configured_account_stays_sandbox_and_routes_nothing()
    {
        using var r = new AiRig();
        r.Contact("Sandy", "60120000904");
        r.Say("60120000904", "hi there");
        var ai = Assert.Single(r.AiMessages("60120000904"));
        Assert.Equal("sandbox", ai.Status);
        Assert.Null(ai.AccountId);
        Assert.Empty(r.T.Fresh().AccountAssignments);
        Assert.Empty(r.Wa.Sent);
    }
}
