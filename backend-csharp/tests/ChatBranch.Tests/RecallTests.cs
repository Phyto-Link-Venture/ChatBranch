using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// Recalled messages — WhatsApp's "delete for everyone" (QR numbers, pushed by the
/// gateway to /api/whatsapp/baileys/recall), LINE's unsend, Instagram / Messenger's
/// is_deleted. By default the inbox erases the message like the customer's phone does
/// (words, file, reactions, the notification preview); with "Keep messages the customer
/// deletes" (keep_recalled) it stays readable, marked. Either way the AI never answers
/// or reads it. The gateway's half (which upserts are recalls) is messages.js recallOf,
/// self-tested.
/// </summary>
public class RecallTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public RecallTests(ApiFactory f) => _f = f;

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

    private async Task<int> Paired(string key, bool keep = false)
    {
        var id = Db(db =>
        {
            var a = new ChannelAccount { Channel = "whatsapp", Provider = Providers.Baileys, Name = key, ExternalId = key,
                HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow };
            if (keep) a.Config = new Dictionary<string, object?> { [InboxSettings.KeepRecalledKey] = true };
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a.Id;
        });
        return await Task.FromResult(id);
    }

    private async Task Inbound(string key, string waId, string text, string msgId) =>
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound",
            new { account_id = key, wa_id = waId, name = "Customer", text, msg_id = msgId })).EnsureSuccessStatusCode();

    private async Task<int> Recall(string key, string waId, string msgId, bool fromMe = false)
    {
        var res = await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/recall",
            new { account_id = key, wa_id = waId, msg_id = msgId, from_me = fromMe });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recalled").GetInt32();
    }

    private Message Row(string msgId) => Db(db => db.Messages.Single(m => m.ExternalId == msgId));

    [Fact]
    public async Task By_default_a_recalled_message_is_erased_like_on_the_customers_phone()
    {
        await Paired("gw-recall-1");
        await Inbound("gw-recall-1", "60171000001", "My IC is 900101-14-5678", "RC-1");
        await Inbound("gw-recall-1", "60171000001", "Is the shop open today?", "RC-2");

        Assert.Equal(1, await Recall("gw-recall-1", "60171000001", "RC-1"));
        var gone = Row("RC-1");
        Assert.NotNull(gone.RecalledAt);
        Assert.Equal((MessageRecalls.ByCustomer, ""), (gone.RecalledBy, gone.Text));
        Assert.Equal("Is the shop open today?", Row("RC-2").Text);   // only that one
        Assert.True(Db(db => db.AuditLogs.Any(a => a.Action == MessageRecalls.AuditAction
            && a.Target == "whatsapp:60171000001" && a.Detail.Contains("erased"))));
        // a redelivery changes nothing
        Assert.Equal(0, await Recall("gw-recall-1", "60171000001", "RC-1"));

        // the chat says so; nothing of it is left to read
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        var thread = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60171000001");
        var bubble = thread.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("pk").GetInt32() == gone.Id);
        var recalled = bubble.GetProperty("recalled");
        Assert.Equal(("customer", false), (recalled.GetProperty("by").GetString(), recalled.GetProperty("kept").GetBoolean()));
        Assert.Equal("", bubble.GetProperty("text").GetString());
    }

    [Fact]
    public async Task With_keep_recalled_on_the_message_stays_readable_and_marked()
    {
        await Paired("gw-recall-2", keep: true);
        await Inbound("gw-recall-2", "60171000002", "Actually cancel my order", "RK-1");
        Assert.Equal(1, await Recall("gw-recall-2", "60171000002", "RK-1"));

        var kept = Row("RK-1");
        Assert.NotNull(kept.RecalledAt);
        Assert.Equal("Actually cancel my order", kept.Text);
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        var thread = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60171000002");
        var bubble = thread.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("pk").GetInt32() == kept.Id);
        Assert.True(bubble.GetProperty("recalled").GetProperty("kept").GetBoolean());
        Assert.Equal("Actually cancel my order", bubble.GetProperty("text").GetString());
    }

    [Fact]
    public async Task An_erased_recall_removes_the_file_and_the_list_preview_says_deleted()
    {
        await Paired("gw-recall-3");
        var form = new MultipartFormDataContent
        {
            { new StringContent("60171000003"), "wa_id" },
            { new StringContent("gw-recall-3"), "account_id" },
            { new StringContent("RF-1"), "msg_id" },
            { new StringContent("image"), "media_type" },
            { new StringContent("photo.jpg"), "file_name" },
        };
        var file = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 });
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "photo.jpg");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/whatsapp/baileys/media") { Content = form };
        req.Headers.Add("X-Baileys-Secret", "baileys-local-dev");
        (await _f.CreateClient().SendAsync(req)).EnsureSuccessStatusCode();
        var before = Row("RF-1");
        var path = new MediaStore(ApiFactory.DataDir).Resolve("whatsapp", before.MediaPath);
        Assert.NotNull(path);

        Assert.Equal(1, await Recall("gw-recall-3", "60171000003", "RF-1"));
        Assert.False(File.Exists(path));
        Assert.Equal("", Row("RF-1").MediaPath);

        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/inbox/threads?channel=whatsapp&q=60171000003");
        var row = list.GetProperty("threads").EnumerateArray().First(t => t.GetProperty("key").GetString() == "60171000003");
        Assert.Equal("[deleted]", row.GetProperty("last_message").GetString());
    }

    [Fact]
    public async Task A_recall_made_on_our_own_phone_is_ours_and_bad_pushes_are_refused()
    {
        await Paired("gw-recall-4");
        await Inbound("gw-recall-4", "60171000004", "hello", "RO-IN");
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/from-phone",
            new { account_id = "gw-recall-4", wa_id = "60171000004", text = "Wrong chat, sorry", msg_id = "RO-1" }))
            .EnsureSuccessStatusCode();
        Assert.Equal(1, await Recall("gw-recall-4", "60171000004", "RO-1", fromMe: true));
        Assert.Equal(MessageRecalls.ByBusiness, Row("RO-1").RecalledBy);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Gateway("wrong").PostAsJsonAsync("/api/whatsapp/baileys/recall",
            new { account_id = "gw-recall-4", wa_id = "60171000004", msg_id = "RO-IN" })).StatusCode);
        Assert.Equal(0, await Recall("no-such-number", "60171000004", "RO-IN"));
        Assert.Equal(0, await Recall("gw-recall-4", "60171000004", "NOT-A-MESSAGE"));
        Assert.Null(Row("RO-IN").RecalledAt);
    }

    [Fact]
    public async Task The_switch_rides_on_the_inbox_and_shows_where_it_applies()
    {
        var id = await Paired("gw-recall-5");
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        var res = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}", new { keep_recalled = "true" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var account = body.TryGetProperty("account", out var inner) ? inner : body;
        Assert.True(account.GetProperty("keep_recalled").GetBoolean());
        Assert.True(account.GetProperty("recalls_supported").GetBoolean());
        Assert.True(Db(db => InboxSettings.KeepRecalled(db.ChannelAccounts.Single(a => a.Id == id))));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}", new { keep_recalled = "maybe" })).StatusCode);
        (await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}", new { keep_recalled = false })).EnsureSuccessStatusCode();
        Assert.False(Db(db => InboxSettings.KeepRecalled(db.ChannelAccounts.Single(a => a.Id == id))));

        Assert.False(MessageRecalls.Supports(new ChannelAccount { Channel = "whatsapp", Provider = Providers.Cloud }));
        Assert.True(MessageRecalls.Supports(new ChannelAccount { Channel = "line" }));
        Assert.True(MessageRecalls.Supports(new ChannelAccount { Channel = "instagram" }));
        Assert.False(MessageRecalls.Supports(new ChannelAccount { Channel = "telegram" }));
    }

    // ---- the AI never answers or reads a recalled message ------------------------------------

    [Fact]
    public void The_AI_does_not_answer_a_message_the_customer_took_back()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Set("ai_wait_seconds", "30");
        r.Contact("Recall", "60123000031");
        r.Say("60123000031", "Send me the price list for 900101-14-5678", acct.Id, "wamid.rc.1");

        // deleted for everyone before the AI's turn came
        Assert.Single(MessageRecalls.Record(r.Db, r.Threads, acct, "60123000031", "wamid.rc.1",
            MessageRecalls.ByCustomer, r.Gate));
        r.Clock.Advance(TimeSpan.FromSeconds(40));
        new QueueService(r.Db, r.Sc, r.Threads, r.Assistant, r.Sender, r.Responder, r.Gate, r.Clock).Drain();

        Assert.Empty(r.Llm.Seen);
        Assert.Empty(r.Cloud.Events);
    }

    [Fact]
    public void A_kept_recall_never_reaches_the_AI_and_a_reply_held_for_it_expires()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        acct.Config = new Dictionary<string, object?>(acct.Config) { [InboxSettings.KeepRecalledKey] = true };
        r.Db.SaveChanges();
        r.Set("ai_wait_seconds", "30");
        r.Set("ai_review_mode", "review_all");
        r.Contact("Held", "60123000032");
        r.Say("60123000032", "Do you deliver to Penang?", acct.Id, "wamid.rc.10");
        r.Clock.Advance(TimeSpan.FromSeconds(40));
        new QueueService(r.Db, r.Sc, r.Threads, r.Assistant, r.Sender, r.Responder, r.Gate, r.Clock).Drain();
        var held = r.Drafts("whatsapp", "60123000032").Single();
        Assert.Equal(AiDraftStatus.Pending, held.Status);

        MessageRecalls.Record(r.Db, r.Threads, acct, "60123000032", "wamid.rc.10", MessageRecalls.ByCustomer, r.Gate);
        r.Db.ChangeTracker.Clear();
        Assert.Equal(AiDraftStatus.Expired, r.Db.AiDrafts.Single(d => d.Id == held.Id).Status);
        Assert.Equal("Do you deliver to Penang?", r.Db.Messages.Single(m => m.ExternalId == "wamid.rc.10").Text); // kept

        // the next reply is written without it
        r.Say("60123000032", "Hello?", acct.Id, "wamid.rc.11");
        r.Clock.Advance(TimeSpan.FromSeconds(40));
        new QueueService(r.Db, r.Sc, r.Threads, r.Assistant, r.Sender, r.Responder, r.Gate, r.Clock).Drain();
        var seen = string.Join("\n", r.Llm.Seen.Last().Messages.Select(m => m.Content));
        Assert.DoesNotContain("Penang", seen);
        Assert.Contains("Hello?", seen);
    }

    // ---- LINE unsend · Instagram is_deleted (parsers) -----------------------------------------

    [Fact]
    public void Line_unsend_and_instagram_is_deleted_are_read_as_recalls()
    {
        using var line = JsonDocument.Parse("""
            {"destination":"Ubot","events":[{"type":"unsend","source":{"type":"user","userId":"U1"},
              "timestamp":1760000000000,"unsend":{"messageId":"325708"}}]}
            """);
        var e = Assert.Single(LineInbound.Parse(line.RootElement).Events);
        Assert.Equal(("unsend", "U1", "325708"), (e.Type, e.UserId, e.MessageId));

        using var ig = JsonDocument.Parse("""
            {"object":"instagram","entry":[{"id":"IG1","messaging":[{"sender":{"id":"C1"},"recipient":{"id":"IG1"},
              "timestamp":1760000000000,"message":{"mid":"aWdfZAG","is_deleted":true}}]}]}
            """);
        var ev = Assert.Single(MetaWebhook.MessagingEvents(ig.RootElement));
        Assert.Equal(("deleted", "aWdfZAG", "C1"), (ev.Kind, ev.Mid, ev.SenderId));
    }
}
