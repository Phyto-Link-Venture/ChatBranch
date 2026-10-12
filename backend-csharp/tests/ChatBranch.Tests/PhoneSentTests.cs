using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// A reply the person typed on the PHONE of a QR (Baileys) number: the gateway pushes it
/// to /api/whatsapp/baileys/from-phone and it is stored as their outbound reply
/// (external_client) — so the chat shows it, an open handover is answered and the AI
/// doesn't answer the customer a second time. ChatBranch's own sends never come back
/// as a second row. The gateway's half (only other devices' live messages, never its own
/// sends) is messages.js phoneSent, self-tested.
/// </summary>
public class PhoneSentTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PhoneSentTests(ApiFactory f) => _f = f;

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

    /// <summary>A paired number (its gateway key) and a customer who wrote to it.</summary>
    private async Task<(string Key, int AccountId)> Paired(string key, string waId, string? customerSays = null)
    {
        var id = Db(db =>
        {
            var a = db.ChannelAccounts.FirstOrDefault(x => x.ExternalId == key);
            if (a == null)
            {
                a = new ChannelAccount { Channel = "whatsapp", Provider = Providers.Baileys, Name = key, ExternalId = key,
                    HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow };
                db.ChannelAccounts.Add(a);
                db.SaveChanges();
            }
            return a.Id;
        });
        if (customerSays != null)
            (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound",
                new { account_id = key, wa_id = waId, name = "Customer", text = customerSays, msg_id = $"IN-{waId}" }))
                .EnsureSuccessStatusCode();
        return (key, id);
    }

    private List<Message> Messages(string waId) => Db(db =>
    {
        var t = db.Threads.Single(x => x.Channel == "whatsapp" && x.Key == waId);
        return db.Messages.Where(m => m.ThreadId == t.Id).OrderBy(m => m.Id).ToList();
    });

    [Fact]
    public async Task A_reply_typed_on_the_phone_lands_in_the_chat_as_the_persons_reply()
    {
        var (key, _) = await Paired("gw-phone-1", "60170000001", "Is the blue one in stock?");
        // a person had been asked to look at it
        Db(db =>
        {
            var t = db.Threads.Single(x => x.Key == "60170000001");
            return Handover.Mark(db, t, HandoverKinds.Manual, "Check stock", "agent:admin");
        });

        var res = await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/from-phone", new
        {
            account_id = key, wa_id = "60170000001", text = "Yes, 3 left — I'll keep one for you",
            msg_id = "PHONE-1", reply_to = "IN-60170000001", ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        res.EnsureSuccessStatusCode();
        Assert.True((await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("stored").GetBoolean());

        var msgs = Messages("60170000001");
        var reply = msgs.Last();
        Assert.Equal((MessageDirection.Out, MessageSource.ExternalClient, "sent", "PHONE-1"),
            (reply.Direction, reply.Source, reply.Status, reply.ExternalId));
        Assert.Equal(msgs.First(m => m.ExternalId == "IN-60170000001").Id, reply.ReplyToId);
        var thread = Db(db => db.Threads.Single(x => x.Key == "60170000001"));
        Assert.NotNull(thread.HandoverResolvedAt);
        Assert.True(Db(db => db.AuditLogs.Any(a => a.Action == Handover.ClearedAuditAction
            && a.Target == "whatsapp:60170000001" && a.Actor == "agent:phone")));

        // the gateway retried: still one row
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/from-phone",
            new { account_id = key, wa_id = "60170000001", text = "Yes, 3 left — I'll keep one for you", msg_id = "PHONE-1" }))
            .EnsureSuccessStatusCode();
        Assert.Single(Messages("60170000001"), m => m.ExternalId == "PHONE-1");
    }

    [Fact]
    public async Task ChatBranchs_own_send_is_never_stored_twice()
    {
        var (key, accountId) = await Paired("gw-phone-2", "60170000002", "hello");
        Db(db =>
        {
            var t = db.Threads.Single(x => x.Key == "60170000002");
            db.Messages.Add(new Message { ThreadId = t.Id, AccountId = accountId, Direction = MessageDirection.Out,
                Source = MessageSource.Agent, Text = "Hi! How can I help?", Status = "sent", ExternalId = "OURS-1",
                CreatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/from-phone",
            new { account_id = key, wa_id = "60170000002", text = "Hi! How can I help?", msg_id = "OURS-1" }))
            .EnsureSuccessStatusCode();
        var ours = Assert.Single(Messages("60170000002"), m => m.ExternalId == "OURS-1");
        Assert.Equal(MessageSource.Agent, ours.Source);
    }

    [Fact]
    public async Task A_chat_started_from_the_phone_links_the_contact_and_keeps_the_chatbot_off()
    {
        var (key, _) = await Paired("gw-phone-3", "60170000003");
        Db(db =>
        {
            db.Contacts.Add(new Contact { Name = "Started From Phone", Phone = "+60170000003", CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/from-phone",
            new { account_id = key, wa_id = "60170000003", text = "Hi, this is Aisha from the shop", msg_id = "PHONE-3" }))
            .EnsureSuccessStatusCode();

        var thread = Db(db => db.Threads.Single(x => x.Key == "60170000003"));
        Assert.Equal("Started From Phone", Db(db => db.Contacts.Single(c => c.Id == thread.ContactId).Name));
        Assert.False(Db(db => AiEligibility.Effective(db, new ScopingService(db), thread)));
        Assert.Equal(MessageSource.ExternalClient, Assert.Single(Messages("60170000003")).Source);
    }

    [Fact]
    public async Task A_file_sent_from_the_phone_is_the_persons_message_with_its_file()
    {
        var (key, _) = await Paired("gw-phone-4", "60170000004", "Can you send the catalogue?");
        var form = new MultipartFormDataContent
        {
            { new StringContent("60170000004"), "wa_id" },
            { new StringContent(key), "account_id" },
            { new StringContent("PHONE-DOC"), "msg_id" },
            { new StringContent("document"), "media_type" },
            { new StringContent("Catalogue.pdf"), "file_name" },
            { new StringContent("1"), "from_me" },
        };
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7 c"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "Catalogue.pdf");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/whatsapp/baileys/media") { Content = form };
        req.Headers.Add("X-Baileys-Secret", "baileys-local-dev");
        (await _f.CreateClient().SendAsync(req)).EnsureSuccessStatusCode();

        var doc = Messages("60170000004").Single(m => m.ExternalId == "PHONE-DOC");
        Assert.Equal((MessageDirection.Out, MessageSource.ExternalClient, "document", "Catalogue.pdf"),
            (doc.Direction, doc.Source, doc.MediaType, doc.MediaName));
    }

    [Fact]
    public async Task Bad_secret_and_unknown_numbers()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Gateway("wrong").PostAsJsonAsync("/api/whatsapp/baileys/from-phone",
            new { account_id = "gw-phone-1", wa_id = "60170000001", text = "x", msg_id = "X" })).StatusCode);
        var unknown = await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/from-phone",
            new { account_id = "no-such-number", wa_id = "60170000009", text = "x", msg_id = "X9" });
        unknown.EnsureSuccessStatusCode();   // acked — a retry could never file it
        Assert.False((await unknown.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("stored").GetBoolean());
        Assert.False(Db(db => db.Threads.Any(t => t.Key == "60170000009")));
    }

    [Fact]
    public void A_reply_from_the_phone_stops_the_AI_answering_the_same_message()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Set("ai_wait_seconds", "30");   // the AI waits for the customer to finish typing
        r.Contact("Phone", "60123000020");
        r.Say("60123000020", "Do you open on Sunday?", acct.Id, "wamid.in.90");

        // the owner answers on the phone before the AI's turn comes
        var msg = PhoneSentMessages.Record(r.Db, r.Sc, r.Threads, r.Inbound, acct, "60123000020",
            "Yes, 10am to 4pm!", "wamid.phone.90");
        Assert.NotNull(msg);
        r.Clock.Advance(TimeSpan.FromSeconds(40));
        new QueueService(r.Db, r.Sc, r.Threads, r.Assistant, r.Sender, r.Responder, r.Gate, r.Clock).Drain();

        Assert.Empty(r.Llm.Seen);                 // nothing left to answer
        Assert.Empty(r.Ai("whatsapp", "60123000020"));
        Assert.Empty(r.Cloud.Events);
    }
}
