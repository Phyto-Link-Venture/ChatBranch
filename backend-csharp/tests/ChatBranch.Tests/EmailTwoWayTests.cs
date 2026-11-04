using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace ChatBranch.Tests;

/// <summary>
/// Two-way email: the IMAP sync no longer depends on <c>\Seen</c> (a UID cursor per
/// mailbox and folder, folders read-only, nothing ever marked read), reads the Sent
/// folder as outbound mail (ChatBranch's own sends recognised by Message-ID, nothing
/// automated reacting), and files a copy of each mail ChatBranch sends into Sent —
/// end to end through the API host with the IMAP mailbox and SMTP faked.
/// </summary>
public class EmailTwoWayTests : IClassFixture<TransportFactory>
{
    private readonly TransportFactory _f;

    public EmailTwoWayTests(TransportFactory f)
    {
        _f = f;
        _f.Email.Reset();
        _f.Mailbox.Fail = _ => null;
        _f.Mailbox.AppendFail = _ => null;
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

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met in time");
            await Task.Delay(50);
        }
    }

    private Task SentCopiesDone() =>
        _f.Services.GetRequiredService<SentFolderCopier>().WhenIdleAsync(TimeSpan.FromSeconds(15));

    private static MimeMessage Mail(string from, string to, string subject, string body, string messageId,
        DateTimeOffset? date = null)
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(to));
        m.Subject = subject;
        m.MessageId = messageId;
        m.Date = date ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        m.Body = new BodyBuilder { TextBody = body }.ToMessageBody();
        return m;
    }

    private async Task<int> CreateMailbox(HttpClient admin, string username, string from)
    {
        var res = await admin.PostAsJsonAsync("/api/messaging/accounts", new
        {
            channel = "email", name = "Desk", host = "smtp.test", port = "587", username,
            password = "app-password-2", from_email = from, imap_host = "imap.test",
        });
        res.EnsureSuccessStatusCode();
        return int.Parse((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> Sync(HttpClient admin)
    {
        var res = await admin.PostAsJsonAsync("/api/messaging/email/sync", new { force = true });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private List<Message> MessagesOf(string key) => InDb(db =>
    {
        var t = db.Threads.AsNoTracking().Single(x => x.Channel == "email" && x.Key == key);
        return db.Messages.AsNoTracking().Where(m => m.ThreadId == t.Id).OrderBy(m => m.Id).ToList();
    });

    private MailboxCursor Cursor(string user, string folder) => InDb(db =>
        db.MailboxCursors.AsNoTracking().Single(c => c.Mailbox == $"imap.test|{user}" && c.Folder == folder));

    // ---- 4a: \Seen plays no part ----------------------------------------------------

    [Fact]
    public async Task An_inbox_mail_already_read_in_outlook_still_imports_once_and_nothing_is_marked_read()
    {
        var admin = await Authed();
        await CreateMailbox(admin, "penny@shop.test", "penny@shop.test");
        var read = _f.Mailbox.Deliver("penny@shop.test",
            Mail("Reader <reader@cust.test>", "penny@shop.test", "Invoice 7", "Got it, thanks.", "r1@cust.test"),
            seen: true);
        var unread = _f.Mailbox.Deliver("penny@shop.test",
            Mail("other@cust.test", "penny@shop.test", "Question", "Open on Sunday?", "r2@cust.test"));

        var first = await Sync(admin);
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.Equal(2, first.GetProperty("fetched").GetInt32()); // the one Penny already read, too
        Assert.Equal(new[] { "Invoice 7\n\nGot it, thanks." }, MessagesOf("reader@cust.test").Select(m => m.Text));

        // ChatBranch read both and marked neither: the read one stays read, the unread one unread
        var flags = _f.Mailbox.Items("penny@shop.test", "INBOX").ToDictionary(i => i.Uid, i => i.Seen);
        Assert.True(flags[read]);
        Assert.False(flags[unread]);

        // the next pass starts after the cursor: nothing is read again, nothing stored twice
        var second = await Sync(admin);
        Assert.Equal(0, second.GetProperty("fetched").GetInt32());
        Assert.Equal(0, second.GetProperty("duplicates").GetInt32());
        Assert.Empty(_f.Mailbox.Reads.Last(r => r.User == "penny@shop.test" && r.Folder == "INBOX").Uids);
        Assert.Single(MessagesOf("reader@cust.test"));
        var cursor = Cursor("penny@shop.test", "INBOX");
        Assert.Equal(unread, (uint)cursor.LastUid);
        Assert.Equal(1, cursor.UidValidity);
        Assert.False(_f.Mailbox.Items("penny@shop.test", "INBOX").Single(i => i.Uid == unread).Seen);
    }

    [Fact]
    public async Task A_new_uidvalidity_reads_the_window_again_without_importing_twice()
    {
        var admin = await Authed();
        await CreateMailbox(admin, "rebuilt@shop.test", "rebuilt@shop.test");
        _f.Mailbox.Deliver("rebuilt@shop.test", Mail("a@cust.test", "rebuilt@shop.test", "One", "First.", "v1@cust.test"));
        _f.Mailbox.Deliver("rebuilt@shop.test", Mail("b@cust.test", "rebuilt@shop.test", "Two", "Second.", "v2@cust.test"));
        Assert.Equal(2, (await Sync(admin)).GetProperty("fetched").GetInt32());
        Assert.Equal(2, Cursor("rebuilt@shop.test", "INBOX").LastUid);

        // the server rebuilt the folder: other UIDVALIDITY, mail renumbered — the old cursor means nothing
        _f.Mailbox.Rebuild("rebuilt@shop.test", "INBOX");
        _f.Mailbox.Deliver("rebuilt@shop.test", Mail("c@cust.test", "rebuilt@shop.test", "Three", "Third.", "v3@cust.test"));
        var report = await Sync(admin);
        Assert.Equal(1, report.GetProperty("fetched").GetInt32());    // only the new mail is new
        Assert.Equal(2, report.GetProperty("duplicates").GetInt32()); // the window again, caught by Message-ID
        var pass = _f.Mailbox.Reads.Last(r => r.User == "rebuilt@shop.test" && r.Folder == "INBOX");
        Assert.True(pass.FirstRun);
        Assert.Equal(new uint[] { 1, 2, 3 }, pass.Uids);
        var cursor = Cursor("rebuilt@shop.test", "INBOX");
        Assert.Equal(101, cursor.UidValidity);
        Assert.Equal(3, cursor.LastUid);
        Assert.Single(MessagesOf("a@cust.test"));
    }

    [Fact]
    public async Task A_backlog_drains_oldest_first_over_passes_and_a_broken_mail_never_stalls_it()
    {
        var admin = await Authed();
        await CreateMailbox(admin, "busy@shop.test", "busy@shop.test");
        // older than the window: a first run passes over it for good
        _f.Mailbox.Deliver("busy@shop.test", Mail("ancient@cust.test", "busy@shop.test", "Old", "Long ago.",
            "old1@cust.test", DateTimeOffset.UtcNow.AddDays(-30)));
        // two full passes and a part one; UIDs: ancient = 1, c{i} = i + 1
        const int max = EmailSyncService.MaxFetchPerMailbox;
        const int total = 2 * max + 5;
        for (var i = 1; i <= total; i++)
            _f.Mailbox.Deliver("busy@shop.test", Mail($"c{i}@backlog.test", "busy@shop.test", $"Mail {i:00}",
                $"Body {i}", $"b{i}@backlog.test"));
        _f.Mailbox.Break("busy@shop.test", "INBOX", 6); // "Mail 05" cannot be downloaded

        var p1 = await Sync(admin);
        Assert.Equal(max - 1, p1.GetProperty("fetched").GetInt32());
        Assert.Contains(p1.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("could not be downloaded (UID 6)"));
        Assert.Equal(Enumerable.Range(2, max).Select(u => (uint)u), _f.Mailbox.Reads.Last(r => r.User == "busy@shop.test" && r.Folder == "INBOX").Uids);
        var threads = InDb(db => db.Threads.AsNoTracking().Where(t => t.Key.EndsWith("@backlog.test")).Select(t => t.Key).ToList());
        Assert.Contains($"c{max}@backlog.test", threads);
        Assert.DoesNotContain("c5@backlog.test", threads);
        Assert.DoesNotContain($"c{max + 1}@backlog.test", threads);

        var p2 = await Sync(admin);
        Assert.Equal(max, p2.GetProperty("fetched").GetInt32());
        Assert.True(p2.GetProperty("ok").GetBoolean()); // the broken mail is not tried again
        var p3 = await Sync(admin);
        Assert.Equal(5, p3.GetProperty("fetched").GetInt32());
        var p4 = await Sync(admin);
        Assert.Equal(0, p4.GetProperty("fetched").GetInt32());
        Assert.Empty(_f.Mailbox.Reads.Last(r => r.User == "busy@shop.test" && r.Folder == "INBOX").Uids);

        Assert.Equal(total - 1, InDb(db => db.Threads.Count(t => t.Key.EndsWith("@backlog.test"))));
        Assert.False(InDb(db => db.Threads.Any(t => t.Key == "ancient@cust.test")));
        Assert.Equal(total + 1, Cursor("busy@shop.test", "INBOX").LastUid);
    }

    // ---- 4b: the Sent folder ----------------------------------------------------------

    [Fact]
    public async Task A_mail_sent_from_outlook_shows_as_outbound_on_the_customers_conversation()
    {
        var admin = await Authed();
        var boxId = await CreateMailbox(admin, "desk@shop.test", "desk@shop.test");
        _f.Mailbox.Deliver("desk@shop.test", Mail("Cara Customer <cara@cust.test>", "desk@shop.test", "Order 9",
            "Is it shipped?", "c1@cust.test"));
        await Sync(admin);

        // Penny answers in Outlook (copying her boss), writes to a brand-new address, and notes something to herself
        var sentAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var answer = Mail("Desk <desk@shop.test>", "Cara Customer <cara@cust.test>", "Re: Order 9",
            "Yes, shipped this morning.\n\n________________________________\nFrom: Cara Customer\nSent: Monday\n\nIs it shipped?",
            "o1@shop.test", sentAt);
        answer.Cc.Add(MailboxAddress.Parse("boss@shop.test"));
        answer.InReplyTo = "c1@cust.test";
        _f.Mailbox.DeliverSent("desk@shop.test", answer);
        _f.Mailbox.DeliverSent("desk@shop.test", Mail("desk@shop.test", "New Lead <lead@new.test>", "Hello",
            "Following up on your call.", "o2@shop.test"));
        _f.Mailbox.DeliverSent("desk@shop.test", Mail("desk@shop.test", "desk@shop.test", "Reminder", "Call Cara.",
            "o3@shop.test"));

        var report = await Sync(admin);
        Assert.True(report.GetProperty("ok").GetBoolean());
        Assert.Equal(0, report.GetProperty("fetched").GetInt32());
        Assert.Equal(2, report.GetProperty("sent_imported").GetInt32());
        Assert.Equal(1, report.GetProperty("sent_skipped").GetInt32()); // the note to self

        var cara = MessagesOf("cara@cust.test");
        Assert.Equal(2, cara.Count);
        var reply = cara[1];
        Assert.Equal(MessageDirection.Out, reply.Direction);
        Assert.Equal(MessageSource.ExternalClient, reply.Source);
        Assert.Equal("sent", reply.Status);
        Assert.Equal("Yes, shipped this morning.", reply.Text); // Outlook's quoted history stripped
        Assert.Equal("<o1@shop.test>", reply.ExternalId);
        Assert.Equal(boxId, reply.AccountId);
        Assert.True(Math.Abs((reply.CreatedAt - sentAt.UtcDateTime).TotalSeconds) < 2); // when it was sent
        var header = InDb(db => db.EmailHeaders.AsNoTracking().Single(h => h.MessageId == reply.Id));
        Assert.Equal("<c1@cust.test>", header.InReplyTo);
        Assert.Equal("Re: Order 9", header.Subject);
        // Cc is not a conversation, nor is our own address
        Assert.False(InDb(db => db.Threads.Any(t => t.Key == "boss@shop.test" || t.Key == "desk@shop.test")));

        // a brand-new address: an unlinked conversation, chatbot off, filed under the mailbox
        var lead = InDb(db => db.Threads.AsNoTracking().Single(t => t.Channel == "email" && t.Key == "lead@new.test"));
        Assert.Null(lead.ContactId);
        Assert.False(lead.AiEnabled);
        Assert.Equal(boxId, lead.AccountId);
        Assert.Equal("New Lead", lead.Name);
        Assert.Equal(new[] { (MessageDirection.Out, "Hello\n\nFollowing up on your call.") },
            MessagesOf("lead@new.test").Select(m => (m.Direction, m.Text)));

        // nothing is read twice
        var again = await Sync(admin);
        Assert.Equal(0, again.GetProperty("sent_imported").GetInt32());
        Assert.Equal(0, again.GetProperty("sent_duplicates").GetInt32());
        Assert.Equal(3, Cursor("desk@shop.test", "Sent").LastUid);

        // ChatBranch's next reply threads under the mail sent from Outlook
        (await admin.PostAsJsonAsync("/api/messaging/threads/email/cara@cust.test/send", new { text = "Tracking: 77" }))
            .EnsureSuccessStatusCode();
        var next = _f.Email.Sent.Last().Message;
        Assert.Contains("o1@shop.test", next.References);
        Assert.Equal("Re: Order 9", next.Subject);
    }

    [Fact]
    public async Task A_mail_imported_from_sent_triggers_no_ai_automation_or_notification()
    {
        var admin = await Authed();
        await CreateMailbox(admin, "quiet@shop.test", "quiet@shop.test");
        var ruleId = InDb(db =>
        {
            db.Contacts.Add(new Contact { Name = "Quinn", Email = "quinn@cust.test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            var rule = new AutomationRule
            {
                Name = "probe", Event = "message_created", Active = true,
                Conditions = new List<object?>
                {
                    new Dictionary<string, object?> { ["field"] = "message", ["op"] = "contains", ["value"] = "zz-probe" },
                },
                Actions = new List<object?> { new Dictionary<string, object?> { ["type"] = "add_label", ["value"] = "probe" } },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.AutomationRules.Add(rule);
            db.SaveChanges();
            return rule.Id;
        });

        // the control: Quinn's own mail runs the whole inbound pipeline
        _f.Mailbox.Deliver("quiet@shop.test", Mail("quinn@cust.test", "quiet@shop.test", "Hi", "Hello zz-probe", "q1@cust.test"));
        await Sync(admin);
        var threadId = InDb(db => db.Threads.Single(x => x.Key == "quinn@cust.test").Id);
        bool Paged() => InDb(db => db.Notifications.Any(n => n.RefKey == $"thread:{threadId}" && !n.Read));
        Assert.Equal(1, InDb(db => db.AutomationRules.Single(r => r.Id == ruleId).TimesFired));
        Assert.True(Paged());
        InDb(db =>
        {
            var t = db.Threads.Single(x => x.Id == threadId);
            AiEligibility.Set(t, true); // the chatbot answers this conversation
            return db.SaveChanges();
        });
        _f.Mailbox.Deliver("quiet@shop.test", Mail("quinn@cust.test", "quiet@shop.test", "Re: Hi", "Still there? zz-probe", "q2@cust.test"));
        await Sync(admin);
        await SentCopiesDone();
        int AiWork() => InDb(db => db.AiDrafts.Count(d => d.ThreadId == threadId)
                                   + db.OutboundJobs.Count(j => j.Channel == "email" && j.ToAddress == "quinn@cust.test"));
        Assert.True(AiWork() > 0); // with the chatbot on, a customer's mail gets an AI answer

        InDb(db => db.Notifications.ExecuteUpdate(s => s.SetProperty(n => n.Read, true)));
        var before = (Fired: InDb(db => db.AutomationRules.Single(r => r.Id == ruleId).TimesFired),
            Ai: AiWork(), Status: InDb(db => db.Threads.Single(t => t.Id == threadId).Status));

        // Penny answers from Outlook — the same words, the same chatbot setting
        _f.Mailbox.DeliverSent("quiet@shop.test", Mail("quiet@shop.test", "quinn@cust.test", "Re: Hi",
            "Yes, here. zz-probe", "qo1@shop.test"));
        var report = await Sync(admin);
        Assert.Equal(1, report.GetProperty("sent_imported").GetInt32());
        Assert.Equal(MessageSource.ExternalClient, MessagesOf("quinn@cust.test").Last().Source);

        Assert.Equal(before.Fired, InDb(db => db.AutomationRules.Single(r => r.Id == ruleId).TimesFired));
        Assert.False(Paged());
        Assert.Equal(before.Ai, AiWork());
        Assert.Equal(before.Status, InDb(db => db.Threads.Single(t => t.Id == threadId).Status));
    }

    [Fact]
    public async Task A_mailbox_without_a_sent_folder_is_noted_not_failed()
    {
        var admin = await Authed();
        await CreateMailbox(admin, "nosent@shop.test", "nosent@shop.test");
        _f.Mailbox.SentFolder("nosent@shop.test", null);
        _f.Mailbox.Deliver("nosent@shop.test", Mail("n@cust.test", "nosent@shop.test", "Hi", "Hello.", "ns1@cust.test"));
        var report = await Sync(admin);
        Assert.True(report.GetProperty("ok").GetBoolean());
        Assert.Equal(1, report.GetProperty("fetched").GetInt32());
        Assert.Contains(report.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("no Sent folder"));
    }

    // ---- 4b + 4c: ChatBranch's own replies --------------------------------------------

    [Fact]
    public async Task A_reply_sent_from_chatbranch_is_not_imported_again_from_sent()
    {
        var admin = await Authed();
        await CreateMailbox(admin, "own@shop.test", "own@shop.test");
        _f.Mailbox.Deliver("own@shop.test", Mail("buyer@cust.test", "own@shop.test", "Price?", "How much?", "p1@cust.test"));
        await Sync(admin);

        (await admin.PostAsJsonAsync("/api/messaging/threads/email/buyer@cust.test/send", new { text = "RM 40." }))
            .EnsureSuccessStatusCode();
        await SentCopiesDone();
        Assert.Single(_f.Mailbox.Items("own@shop.test", "Sent")); // the copy is there for Outlook

        var report = await Sync(admin);
        Assert.Equal(0, report.GetProperty("sent_imported").GetInt32());
        Assert.Equal(1, report.GetProperty("sent_duplicates").GetInt32()); // recognised by its Message-ID
        var outbound = MessagesOf("buyer@cust.test").Where(m => m.Direction == MessageDirection.Out).ToList();
        Assert.Equal(new[] { (MessageSource.Agent, "RM 40.") }, outbound.Select(m => (m.Source, m.Text)));
    }

    [Fact]
    public async Task After_a_chatbranch_send_a_copy_is_appended_to_sent_and_a_failed_copy_never_touches_the_send()
    {
        var admin = await Authed();
        var boxId = await CreateMailbox(admin, "copy@shop.test", "copy@shop.test");
        _f.Mailbox.Deliver("copy@shop.test", Mail("dina@cust.test", "copy@shop.test", "Booking", "Friday ok?", "d1@cust.test"));
        await Sync(admin);

        var res = await admin.PostAsJsonAsync("/api/messaging/threads/email/dina@cust.test/send", new { text = "Friday works." });
        res.EnsureSuccessStatusCode();
        var sentId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetProperty("id").GetString();
        await WaitFor(() => _f.Mailbox.Appended.Any(a => a.Server.ImapUsername == "copy@shop.test"));
        var (server, copy) = _f.Mailbox.Appended.Single(a => a.Server.ImapUsername == "copy@shop.test");
        Assert.Same(_f.Email.Sent.Last().Message, copy);          // exactly the mail that went out
        Assert.Equal(sentId, $"<{copy.MessageId}>");
        Assert.Equal(boxId, server.AccountId);
        var filed = _f.Mailbox.Items("copy@shop.test", "Sent").Single();
        Assert.True(filed.Seen);                                   // filed as read, like any sent mail

        // the IMAP copy fails: the mail is still sent once, stored as sent, and never re-sent
        _f.Mailbox.AppendFail = _ => new MailboxException("IMAP copy to Sent failed: timed out", authFailure: false);
        var smtpBefore = _f.Email.Sent.Count;
        var second = await admin.PostAsJsonAsync("/api/messaging/threads/email/dina@cust.test/send", new { text = "See you." });
        second.EnsureSuccessStatusCode();
        Assert.Equal("sent", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message")
            .GetProperty("status").GetString());
        await SentCopiesDone();
        Assert.Equal(smtpBefore + 1, _f.Email.Sent.Count);
        Assert.Single(_f.Mailbox.Items("copy@shop.test", "Sent"));
        Assert.Equal(Health.Healthy, InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == boxId).HealthStatus));

        // a mailbox without a Sent folder: nothing to file the copy in, the send is untouched
        _f.Mailbox.AppendFail = _ => null;
        _f.Mailbox.SentFolder("copy@shop.test", null);
        var third = await admin.PostAsJsonAsync("/api/messaging/threads/email/dina@cust.test/send", new { text = "Bye." });
        Assert.Equal("sent", (await third.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message")
            .GetProperty("status").GetString());
        await SentCopiesDone();
        Assert.Single(_f.Mailbox.Appended, a => a.Server.ImapUsername == "copy@shop.test");
    }

    // ---- pure rules --------------------------------------------------------------------

    [Fact]
    public void The_cursor_plan_windows_first_then_reads_after_the_cursor()
    {
        // first run: the window, oldest first, up to the folder's top
        var p = MailboxCursorPlan.Plan(null, 7, new uint[] { 9, 4, 6 }, uidNext: 12, max: 10);
        Assert.True(p.FirstRun);
        Assert.Equal(new uint[] { 4, 6, 9 }, p.Fetch);
        Assert.Equal(11u, p.AdvanceTo); // mail 10 and 11 were outside the window: considered
        // a first run that found nothing and cannot see the folder's top keeps windowing
        Assert.Null(MailboxCursorPlan.Plan(null, 7, Array.Empty<uint>(), null, 10).AdvanceTo);
        Assert.Equal(0u, MailboxCursorPlan.Plan(null, 7, Array.Empty<uint>(), 1, 10).AdvanceTo); // empty folder

        // later: after the cursor; IMAP's "n:*" answer with the highest UID ≤ cursor is dropped
        var later = MailboxCursorPlan.Plan(new FolderCursor(7, 11), 7, new uint[] { 11 }, 12, 10);
        Assert.False(later.FirstRun);
        Assert.Empty(later.Fetch);
        Assert.Equal(11u, later.AdvanceTo);

        // a backlog: the oldest max, the cursor on the last one taken
        var backlog = MailboxCursorPlan.Plan(new FolderCursor(7, 11), 7, Enumerable.Range(12, 25).Select(i => (uint)i), 37, 10);
        Assert.Equal(Enumerable.Range(12, 10).Select(i => (uint)i), backlog.Fetch);
        Assert.Equal(21u, backlog.AdvanceTo);

        // another UIDVALIDITY = the folder was rebuilt: a first run again
        Assert.True(MailboxCursorPlan.Plan(new FolderCursor(7, 11), 8, new uint[] { 1, 2 }, 3, 10).FirstRun);
    }

    [Fact]
    public void The_sent_folder_is_the_flagged_one_else_a_usual_name()
    {
        static SentFolders.Listed L(string name, char sep = '/', bool flagged = false) => new(name, sep, flagged);

        Assert.Equal("Gesendet", SentFolders.Pick(new[] { L("INBOX"), L("Sent"), L("Gesendet", flagged: true) }));
        Assert.Equal("SENT ITEMS", SentFolders.Pick(new[] { L("INBOX"), L("Drafts"), L("SENT ITEMS") }));
        Assert.Equal("SENT", SentFolders.Pick(new[] { L("INBOX"), L("Sent Items"), L("SENT") })); // "Sent" comes first
        Assert.Equal("Sent Messages", SentFolders.Pick(new[] { L("INBOX"), L("Sent Messages") }));
        Assert.Equal("INBOX/Sent", SentFolders.Pick(new[] { L("INBOX"), L("INBOX/Drafts"), L("INBOX/Sent") }));
        Assert.Equal("INBOX.Sent", SentFolders.Pick(new[] { L("INBOX", '.'), L("INBOX.Sent", '.') }, "INBOX.", '.'));
        // a personal namespace prefix ("INBOX.") in front of the usual names
        Assert.Equal("INBOX.Sent Items", SentFolders.Pick(new[] { L("INBOX", '.'), L("INBOX.Sent Items", '.') }, "INBOX", '.'));
        Assert.Null(SentFolders.Pick(new[] { L("INBOX"), L("Archive"), L("Sentimental") }));
    }

    [Fact]
    public void An_existing_sqlite_database_gains_the_cursor_table()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var db = new AppDbContext(options))
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("DROP TABLE \"crm_mailboxcursor\""); // a database from before the table
            Seed.UpgradeSqlite(db);
            db.MailboxCursors.Add(new MailboxCursor { Mailbox = "imap.test|a@b.test", Folder = "INBOX",
                UidValidity = uint.MaxValue, LastUid = 42, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        using (var db = new AppDbContext(options))
        {
            var row = db.MailboxCursors.Single();
            Assert.Equal(uint.MaxValue, row.UidValidity);
            Assert.Equal(42, row.LastUid);
            db.MailboxCursors.Add(new MailboxCursor { Mailbox = "imap.test|a@b.test", Folder = "INBOX", UpdatedAt = DateTime.UtcNow });
            Assert.Throws<DbUpdateException>(() => db.SaveChanges()); // one cursor per (mailbox, folder)
        }
    }
}
