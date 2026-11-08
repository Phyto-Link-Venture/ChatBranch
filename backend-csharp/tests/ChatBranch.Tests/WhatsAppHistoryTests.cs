using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Importing the chats a QR (Baileys) number ALREADY had, when it links: the gateway
/// replays them and pushes them to /api/whatsapp/baileys/history, and
/// Channels/WhatsAppHistoryImport files them as a BACKFILL — the words arrive and
/// nothing else runs. The gateway's half (which messages are importable, and the
/// window) is backend/baileys_service/history.js, self-tested.
/// </summary>
public class WhatsAppHistoryTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public WhatsAppHistoryTests(ApiFactory f) => _f = f;

    private T Db<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _f.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private HttpClient Gateway(string secret = "baileys-local-dev")
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Baileys-Secret", secret);
        return c;
    }

    private static long Ago(TimeSpan span) => DateTimeOffset.UtcNow.Subtract(span).ToUnixTimeSeconds();

    /// <summary>A paired QR number, optionally with its own history window.</summary>
    private string Paired(string key, int? importDays = null) => Db(db =>
    {
        var a = db.ChannelAccounts.FirstOrDefault(x => x.ExternalId == key);
        if (a == null)
        {
            a = new ChannelAccount
            {
                Channel = "whatsapp", Provider = Providers.Baileys, Name = key, ExternalId = key,
                HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
            };
            db.ChannelAccounts.Add(a);
        }
        if (importDays is int days)
            a.Config = new Dictionary<string, object?>(a.Config)
            {
                [BaileysSettings.ImportHistoryDaysKey] = days.ToString(),
            };
        db.SaveChanges();
        return key;
    });

    private Task<HttpResponseMessage> Push(string key, params object[] messages) =>
        Gateway().PostAsJsonAsync("/api/whatsapp/baileys/history", new { account_id = key, messages });

    private static object Msg(string waId, string id, string text, long ts, bool fromMe = false,
        string name = "Customer", string replyTo = "") =>
        new { wa_id = waId, name, msg_id = id, from_me = fromMe, ts, text, reply_to = replyTo };

    private List<Message> Messages(string waId) => Db(db =>
    {
        var t = db.Threads.Single(x => x.Channel == "whatsapp" && x.Key == waId);
        return db.Messages.Where(m => m.ThreadId == t.Id).OrderBy(m => m.Id).ToList();
    });

    private Thread ThreadOf(string waId) => Db(db => db.Threads.Single(x => x.Key == waId));

    [Fact]
    public async Task A_numbers_existing_chat_arrives_with_both_sides_and_its_own_times()
    {
        var key = Paired("gw-hist-1");
        var asked = Ago(TimeSpan.FromDays(3));
        var answered = Ago(TimeSpan.FromDays(3).Subtract(TimeSpan.FromMinutes(4)));

        var res = await Push(key,
            Msg("60180000001", "H-1", "Do you deliver to Penang?", asked, name: "Mei"),
            Msg("60180000001", "H-2", "Yes, 2–3 working days", answered, fromMe: true, replyTo: "H-1"));
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("stored").GetInt32());
        Assert.Equal(1, body.GetProperty("threads").GetInt32());

        var msgs = Messages("60180000001");
        Assert.Equal(2, msgs.Count);
        // the customer's side is a normal inbound
        Assert.Equal((MessageDirection.In, MessageSource.Customer, "received"),
            (msgs[0].Direction, msgs[0].Source, msgs[0].Status));
        // the number's own side is that person's reply, exactly like one typed on the phone
        Assert.Equal((MessageDirection.Out, MessageSource.ExternalClient, "sent"),
            (msgs[1].Direction, msgs[1].Source, msgs[1].Status));
        // WhatsApp's own timestamps, not the moment of the import
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(asked).UtcDateTime, msgs[0].CreatedAt,
            TimeSpan.FromSeconds(2));
        Assert.True(msgs[1].CreatedAt > msgs[0].CreatedAt);
        // a quote inside the history is linked up
        Assert.Equal(msgs[0].Id, msgs[1].ReplyToId);

        // the conversation is ordered by what it actually holds, not by the import
        var thread = ThreadOf("60180000001");
        Assert.True(thread.UpdatedAt < DateTime.UtcNow.AddDays(-2),
            $"a 3-day-old chat should not jump to the top of the inbox (updated_at {thread.UpdatedAt:o})");
    }

    [Fact]
    public async Task Importing_is_silent___no_ai_no_automation_no_notifications_no_reopening()
    {
        var key = Paired("gw-hist-2");
        // the inbox has already finished with this customer
        await Push(key, Msg("60180000002", "S-1", "Thanks!", Ago(TimeSpan.FromDays(5))));
        Db(db =>
        {
            var t = db.Threads.Single(x => x.Key == "60180000002");
            t.Status = ThreadStatus.Resolved;
            return db.SaveChanges();
        });
        var notificationsBefore = Db(db => db.Notifications.Count());

        // more of the same conversation turns up in a later batch
        (await Push(key,
            Msg("60180000002", "S-2", "One more thing — do you have it in blue?", Ago(TimeSpan.FromDays(4)))))
            .EnsureSuccessStatusCode();

        var thread = ThreadOf("60180000002");
        // a message from last week is not new work: the conversation stays closed
        Assert.Equal(ThreadStatus.Resolved, thread.Status);
        // nothing answered it and nobody was told
        Assert.False(Db(db => db.AiDrafts.Any(d => d.ThreadId == thread.Id)));
        Assert.False(Db(db => db.OutboundJobs.Any(j => j.ToAddress == "60180000002")));
        Assert.Equal(notificationsBefore, Db(db => db.Notifications.Count()));
        Assert.Equal("", thread.HandoverKind);
    }

    [Fact]
    public async Task An_import_never_answers_an_open_handover()
    {
        var key = Paired("gw-hist-3");
        await Push(key, Msg("60180000003", "A-1", "Hello?", Ago(TimeSpan.FromDays(2))));
        Db(db =>
        {
            var t = db.Threads.Single(x => x.Key == "60180000003");
            return Handover.Mark(db, t, HandoverKinds.Manual, "Customer wants a human", "agent:admin");
        });

        // history that includes a reply the number once sent must not look like
        // someone answering the handover that is open NOW
        (await Push(key, Msg("60180000003", "A-2", "Hi, how can I help?", Ago(TimeSpan.FromDays(2)),
            fromMe: true))).EnsureSuccessStatusCode();

        var thread = ThreadOf("60180000003");
        Assert.Null(thread.HandoverResolvedAt);
        Assert.Equal(HandoverKinds.Manual, thread.HandoverKind);
    }

    [Fact]
    public async Task A_replay_of_the_same_messages_changes_nothing()
    {
        var key = Paired("gw-hist-4");
        var ts = Ago(TimeSpan.FromDays(1));
        (await Push(key, Msg("60180000004", "D-1", "Still there?", ts))).EnsureSuccessStatusCode();

        var again = await Push(key, Msg("60180000004", "D-1", "Still there?", ts));
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("stored").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());
        Assert.Single(Messages("60180000004"));
    }

    [Fact]
    public async Task The_inboxs_window_decides_how_far_back_it_reaches()
    {
        // 30 days (the default): a two-month-old message is not wanted
        var monthly = Paired("gw-hist-5", importDays: 30);
        (await Push(monthly,
            Msg("60180000005", "W-1", "recent", Ago(TimeSpan.FromDays(10))),
            Msg("60180000005", "W-2", "ancient", Ago(TimeSpan.FromDays(60))))).EnsureSuccessStatusCode();
        Assert.Single(Messages("60180000005"), m => m.ExternalId == "W-1");
        Assert.DoesNotContain(Messages("60180000005"), m => m.ExternalId == "W-2");

        // "everything WhatsApp sends" takes the old one too
        var all = Paired("gw-hist-6", importDays: BaileysSettings.HistoryAll);
        (await Push(all, Msg("60180000006", "W-3", "ancient", Ago(TimeSpan.FromDays(400)))))
            .EnsureSuccessStatusCode();
        Assert.Single(Messages("60180000006"), m => m.ExternalId == "W-3");

        // off imports nothing at all, even when the gateway pushes
        var off = Paired("gw-hist-7", importDays: 0);
        var res = await Push(off, Msg("60180000007", "W-4", "today", Ago(TimeSpan.FromHours(1))));
        res.EnsureSuccessStatusCode();
        Assert.Equal(0, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetInt32());
        Assert.False(Db(db => db.Threads.Any(t => t.Key == "60180000007")));
    }

    [Fact]
    public async Task The_customers_contact_is_linked_when_this_company_may_see_it()
    {
        var key = Paired("gw-hist-8");
        var contactId = Db(db =>
        {
            var c = new Contact { Name = "Siti Rahman", Phone = "+60180000008", CreatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            return c.Id;
        });

        (await Push(key, Msg("60180000008", "L-1", "Booking for Friday?", Ago(TimeSpan.FromDays(2)))))
            .EnsureSuccessStatusCode();

        Assert.Equal(contactId, ThreadOf("60180000008").ContactId);
    }

    [Fact]
    public async Task A_push_that_stored_nothing_says_so_rather_than_a_silent_ok()
    {
        var key = Paired("gw-hist-9");

        // a wrong secret is never the gateway
        var wrong = await Gateway("nope").PostAsJsonAsync("/api/whatsapp/baileys/history",
            new { account_id = key, messages = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // a number ChatBranch does not know has nothing to file under
        var unknown = await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/history",
            new { account_id = "gw-hist-nobody", messages = new[] { Msg("60180000009", "X", "hi", Ago(TimeSpan.FromHours(1))) } });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // a body with no messages is a bug in the caller, not something to retry
        var empty = await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/history", new { account_id = key });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public void A_message_with_no_id_or_no_time_is_not_importable()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var ok = new HistoryMessage("60123456789", "", "ID-1", false, Ago(TimeSpan.FromDays(1)), "hi");
        Assert.True(WhatsAppHistoryImport.Wanted(ok, cutoff));
        Assert.False(WhatsAppHistoryImport.Wanted(ok with { MsgId = "" }, cutoff));
        Assert.False(WhatsAppHistoryImport.Wanted(ok with { Seconds = 0 }, cutoff));
        // a timestamp that cannot be true is no timestamp at all
        Assert.Null(WhatsAppHistoryImport.At(DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds()));
        Assert.Null(WhatsAppHistoryImport.At(100));
        // an open window takes anything that has a time
        Assert.True(WhatsAppHistoryImport.Wanted(ok with { Seconds = Ago(TimeSpan.FromDays(900)) }, null));
    }

    [Theory]
    [InlineData(null, 30)]   // nothing set: the last month
    [InlineData("0", 0)]
    [InlineData("90", 90)]
    [InlineData("-1", BaileysSettings.HistoryAll)]
    [InlineData("7", 30)]    // not a window we offer: the default, never "everything"
    [InlineData("nonsense", 30)]
    public void The_window_reads_back_off_the_account(string? stored, int expected)
    {
        var a = new ChannelAccount { Provider = Providers.Baileys };
        if (stored != null)
            a.Config = new Dictionary<string, object?> { [BaileysSettings.ImportHistoryDaysKey] = stored };
        Assert.Equal(expected, BaileysSettings.ImportHistoryDays(a));
    }
}
