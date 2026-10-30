using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// An edit WhatsApp has already applied, reported by the gateway: the customer
/// re-wording their own message, or a message re-worded on the number's OWN phone
/// (or WhatsApp Web). It is the same message, so it updates that row instead of
/// adding a bubble — the chat has to say what the two of them now see, while the
/// activity log keeps what it used to say.
///
/// The counterpart of a recall: both arrive as a `protocolMessage` the gateway
/// turns into one push, and both can strand an AI reply that is still waiting for
/// review. The gateway's half (which upserts are edits) is `messages.js editOf`,
/// self-tested.
/// </summary>
public class PhoneEditTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PhoneEditTests(ApiFactory f) => _f = f;

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

    private void Paired(string key) => Db(db =>
    {
        db.ChannelAccounts.Add(new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Baileys, Name = key, ExternalId = key,
            HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
        });
        return db.SaveChanges();
    });

    private async Task Inbound(string key, string waId, string text, string msgId) =>
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound",
            new { account_id = key, wa_id = waId, name = "Customer", text, msg_id = msgId })).EnsureSuccessStatusCode();

    private async Task<int> Edited(string key, string waId, string msgId, string text, bool fromMe = false)
    {
        var res = await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/edited",
            new { account_id = key, wa_id = waId, msg_id = msgId, text, from_me = fromMe });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("edited").GetInt32();
    }

    private Message Row(string msgId) => Db(db => db.Messages.Single(m => m.ExternalId == msgId));

    [Fact]
    public async Task A_customers_edit_rewrites_that_message_and_the_log_keeps_the_old_wording()
    {
        Paired("gw-edit-1");
        await Inbound("gw-edit-1", "60172000001", "I want 2 boxes", "ED-1");
        await Inbound("gw-edit-1", "60172000001", "and a receipt please", "ED-2");

        Assert.Equal(1, await Edited("gw-edit-1", "60172000001", "ED-1", "I want 5 boxes"));

        var edited = Row("ED-1");
        Assert.Equal("I want 5 boxes", edited.Text);
        Assert.NotNull(edited.EditedAt);
        Assert.Equal("and a receipt please", Row("ED-2").Text);           // only that one
        // the new wording exists ONCE: an edit replaces, it never adds a bubble
        Assert.Equal(1, Db(db => db.Messages.Count(m => m.ThreadId == edited.ThreadId
            && m.Text == "I want 5 boxes")));
        Assert.True(Db(db => db.AuditLogs.Any(a => a.Action == MessageEdits.AuditAction
            && a.Target == "whatsapp:60172000001" && a.Detail.Contains("I want 2 boxes"))));
    }

    [Fact]
    public async Task An_edit_made_on_our_own_phone_is_recorded_as_ours()
    {
        Paired("gw-edit-2");
        await Inbound("gw-edit-2", "60172000002", "hi", "ED-10");
        // what the person typed on the phone, synced here as their own reply
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/from-phone",
            new { account_id = "gw-edit-2", wa_id = "60172000002", text = "Ready at 4pm", msg_id = "ED-11" }))
            .EnsureSuccessStatusCode();

        Assert.Equal(1, await Edited("gw-edit-2", "60172000002", "ED-11", "Ready at 5pm", fromMe: true));

        Assert.Equal("Ready at 5pm", Row("ED-11").Text);
        Assert.True(Db(db => db.AuditLogs.Any(a => a.Action == MessageEdits.AuditAction
            && a.Actor == "agent:phone" && a.Detail.Contains("Ready at 4pm"))));
    }

    [Fact]
    public async Task The_chat_shows_the_new_wording_marked_as_edited()
    {
        Paired("gw-edit-3");
        await Inbound("gw-edit-3", "60172000003", "Tuesday works", "ED-20");
        await Edited("gw-edit-3", "60172000003", "ED-20", "Wednesday works");

        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        var thread = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60172000003");
        var bubble = thread.GetProperty("messages").EnumerateArray()
            .First(m => m.GetProperty("pk").GetInt32() == Row("ED-20").Id);
        Assert.Equal("Wednesday works", bubble.GetProperty("text").GetString());
        // the key is only on the payload once a message HAS been edited
        Assert.True(bubble.TryGetProperty("edited_at", out var when) && !string.IsNullOrEmpty(when.GetString()));
    }

    [Fact]
    public async Task An_edit_nothing_here_sent_or_a_repeat_changes_nothing_and_a_bad_secret_is_refused()
    {
        Paired("gw-edit-4");
        await Inbound("gw-edit-4", "60172000004", "original", "ED-30");

        // a message this inbox never saw
        Assert.Equal(0, await Edited("gw-edit-4", "60172000004", "ED-UNKNOWN", "whatever"));
        // the same edit delivered twice is not two edits
        Assert.Equal(1, await Edited("gw-edit-4", "60172000004", "ED-30", "corrected"));
        Assert.Equal(0, await Edited("gw-edit-4", "60172000004", "ED-30", "corrected"));
        // an empty new wording would blank the bubble — refused, not applied
        Assert.Equal(0, await Edited("gw-edit-4", "60172000004", "ED-30", "   "));
        Assert.Equal("corrected", Row("ED-30").Text);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Gateway("wrong").PostAsJsonAsync(
            "/api/whatsapp/baileys/edited",
            new { account_id = "gw-edit-4", wa_id = "60172000004", msg_id = "ED-30", text = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_recalled_message_is_not_brought_back_by_an_edit()
    {
        Paired("gw-edit-5");
        await Inbound("gw-edit-5", "60172000005", "my IC is 900101-14-5678", "ED-40");
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/recall",
            new { account_id = "gw-edit-5", wa_id = "60172000005", msg_id = "ED-40" })).EnsureSuccessStatusCode();

        // an edit arriving after the delete must not put the words back
        Assert.Equal(0, await Edited("gw-edit-5", "60172000005", "ED-40", "my IC is 900101-14-5678"));
        var gone = Row("ED-40");
        Assert.NotNull(gone.RecalledAt);
        Assert.Equal("", gone.Text);
    }

    [Fact]
    public void A_reply_held_for_review_expires_when_the_customer_rewords_what_it_answered()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Set("ai_wait_seconds", "30");
        r.Set("ai_review_mode", "review_all");
        r.Contact("Edited", "60123000041");
        r.Say("60123000041", "Do you deliver to Penang?", acct.Id, "wamid.ed.1");
        r.Clock.Advance(TimeSpan.FromSeconds(40));
        new QueueService(r.Db, r.Sc, r.Threads, r.Assistant, r.Sender, r.Responder, r.Gate, r.Clock).Drain();
        var held = r.Drafts("whatsapp", "60123000041").Single();
        Assert.Equal(AiDraftStatus.Pending, held.Status);

        // they changed the question before anyone approved the answer to the old one
        Assert.Single(MessageEdits.RecordExternal(r.Db, r.Threads, acct, "60123000041", "wamid.ed.1",
            "Do you deliver to Sabah?", MessageEdits.ByCustomer, r.Gate));
        r.Db.ChangeTracker.Clear();

        Assert.Equal(AiDraftStatus.Expired, r.Db.AiDrafts.Single(d => d.Id == held.Id).Status);
        Assert.Equal("Do you deliver to Sabah?", r.Db.Messages.Single(m => m.ExternalId == "wamid.ed.1").Text);

        // and the next reply is written against what the message says NOW
        r.Say("60123000041", "Hello?", acct.Id, "wamid.ed.2");
        r.Clock.Advance(TimeSpan.FromSeconds(40));
        new QueueService(r.Db, r.Sc, r.Threads, r.Assistant, r.Sender, r.Responder, r.Gate, r.Clock).Drain();
        var seen = string.Join("\n", r.Llm.Seen.Last().Messages.Select(m => m.Content));
        Assert.Contains("Sabah", seen);
        Assert.DoesNotContain("Penang", seen);
    }

    [Fact]
    public void Our_own_edit_leaves_a_held_reply_alone()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Set("ai_wait_seconds", "30");
        r.Set("ai_review_mode", "review_all");
        r.Contact("Ours", "60123000042");
        r.Say("60123000042", "What time do you close?", acct.Id, "wamid.ed.10");
        r.Clock.Advance(TimeSpan.FromSeconds(40));
        new QueueService(r.Db, r.Sc, r.Threads, r.Assistant, r.Sender, r.Responder, r.Gate, r.Clock).Drain();
        var held = r.Drafts("whatsapp", "60123000042").Single();

        // a teammate fixes a typo in one of OUR messages on their phone: the
        // customer's question is untouched, so the draft still answers it
        // (nothing was sent — the reply is still held — so the row is put there)
        r.Db.Messages.Add(new Message
        {
            ThreadId = held.ThreadId!.Value, Direction = MessageDirection.Out, Source = MessageSource.Agent,
            Text = "Sory — we close at 7pm.", Status = "sent", ExternalId = "wamid.ed.11",
            AccountId = acct.Id, CreatedAt = r.Clock.Utc,
        });
        r.Db.SaveChanges();
        MessageEdits.RecordExternal(r.Db, r.Threads, acct, "60123000042", "wamid.ed.11",
            "Sorry — we close at 7pm.", MessageEdits.ByBusiness, r.Gate);
        r.Db.ChangeTracker.Clear();

        Assert.Equal(AiDraftStatus.Pending, r.Db.AiDrafts.Single(d => d.Id == held.Id).Status);
        Assert.Equal("Sorry — we close at 7pm.", r.Db.Messages.Single(m => m.ExternalId == "wamid.ed.11").Text);
    }
}
