using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

/// <summary>
/// Quoted replies + emoji reactions over WhatsApp Cloud, end to end through the
/// API host with graph.facebook.com faked: inbound <c>context.id</c> → ReplyToId,
/// inbound <c>type: reaction</c> (stored, removable, never a bubble / the AI / a
/// reopen), outbound <c>context.message_id</c>, the outbound reaction payload, the
/// 24h window and the react route's permission + entity scope.
/// </summary>
public class ReplyReactionCloudTests : IClassFixture<MetaApiFactory>
{
    private const string Secret = "reply-reaction-secret";
    private readonly MetaApiFactory _factory;
    private FakeGraph Graph => _factory.Graph;

    public ReplyReactionCloudTests(MetaApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static StringContent Signed(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        content.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
        return content;
    }

    private T InScope<T>(Func<AppDbContext, T> read)
    {
        using var scope = _factory.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static object CloudMessage(string pnid, object message) => new
    {
        entry = new[] { new { changes = new[] { new { value = new
        {
            metadata = new { phone_number_id = pnid },
            contacts = new[] { new { profile = new { name = "Cust" } } },
            messages = new[] { message },
        } } } } },
    };

    private async Task CloudAccount(HttpClient admin, string pnid)
    {
        var res = await admin.PostAsJsonAsync("/api/whatsapp/accounts", new
        {
            name = $"Cloud {pnid}", phone_number_id = pnid, access_token = $"tok-{pnid}", app_secret = Secret,
        });
        res.EnsureSuccessStatusCode();
    }

    private static string Now(int minutesAgo = 0) =>
        DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToUnixTimeSeconds().ToString();

    private async Task Inbound(string pnid, object message) =>
        (await _factory.CreateClient().PostAsync("/api/whatsapp/webhook", Signed(CloudMessage(pnid, message))))
        .EnsureSuccessStatusCode();

    [Fact]
    public async Task Inbound_reaction_is_stored_never_a_bubble_never_the_ai_and_can_be_removed()
    {
        var admin = await Authed("admin", "admin123");
        await CloudAccount(admin, "PN-RX1");
        (await admin.PostAsJsonAsync("/api/contacts", new { name = "Rina", phone = "60177100001" })).EnsureSuccessStatusCode();
        await Inbound("PN-RX1", new { from = "60177100001", id = "wamid.RX.IN1", timestamp = Now(5), type = "text",
            text = new { body = "Is the shop open today?" } });
        Graph.Json("POST", @"/PN-RX1/messages$", new { messages = new[] { new { id = "wamid.RX.OUT1" } } });
        (await admin.PostAsJsonAsync("/api/whatsapp/threads/60177100001/send", new { text = "Yes, until 9pm" }))
            .EnsureSuccessStatusCode();
        // the AI is on for this chat and the chat is resolved — a reaction must not change either
        (await admin.PostAsJsonAsync("/api/whatsapp/threads/60177100001/ai", new { enabled = true })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/whatsapp/threads/60177100001/status", new { status = "resolved" }))
            .EnsureSuccessStatusCode();
        var (threadId, messagesBefore, draftsBefore, updatedBefore) = InScope(db =>
        {
            var t = db.Threads.Single(x => x.Channel == "whatsapp" && x.Key == "60177100001");
            return (t.Id, db.Messages.Count(m => m.ThreadId == t.Id), db.AiDrafts.Count(d => d.ThreadId == t.Id), t.UpdatedAt);
        });

        await Inbound("PN-RX1", new { from = "60177100001", id = "wamid.RX.REACT1", timestamp = Now(), type = "reaction",
            reaction = new { message_id = "wamid.RX.OUT1", emoji = "👍" } });

        InScope(db =>
        {
            var t = db.Threads.Single(x => x.Id == threadId);
            Assert.Equal(messagesBefore, db.Messages.Count(m => m.ThreadId == threadId)); // no bubble
            Assert.Equal(draftsBefore, db.AiDrafts.Count(d => d.ThreadId == threadId));   // no AI
            Assert.Equal("resolved", t.Status);                                         // no reopen
            Assert.True(t.UpdatedAt > updatedBefore);                                   // the stream refreshes
            var r = db.MessageReactions.Single(x => x.ThreadId == threadId);
            Assert.Equal(("contact", "👍"), (r.Actor, r.Emoji));
            Assert.Equal(db.Messages.Single(m => m.ExternalId == "wamid.RX.OUT1").Id, r.MessageId);
            return 0;
        });
        var payload = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177100001");
        var bubble = payload.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("id").GetString() == "wamid.RX.OUT1");
        Assert.Equal("👍", bubble.GetProperty("reactions")[0].GetProperty("emoji").GetString());

        // reacting again replaces it; an empty emoji removes it
        await Inbound("PN-RX1", new { from = "60177100001", id = "wamid.RX.REACT2", timestamp = Now(), type = "reaction",
            reaction = new { message_id = "wamid.RX.OUT1", emoji = "❤️" } });
        Assert.Equal("❤️", InScope(db => db.MessageReactions.Single(x => x.ThreadId == threadId).Emoji));
        await Inbound("PN-RX1", new { from = "60177100001", id = "wamid.RX.REACT3", timestamp = Now(), type = "reaction",
            reaction = new { message_id = "wamid.RX.OUT1", emoji = "" } });
        Assert.Equal(0, InScope(db => db.MessageReactions.Count(x => x.ThreadId == threadId)));
        Assert.Equal(messagesBefore, InScope(db => db.Messages.Count(m => m.ThreadId == threadId)));
    }

    [Fact]
    public async Task Inbound_context_id_resolves_the_quoted_message_and_a_reply_sends_context_message_id()
    {
        var admin = await Authed("admin", "admin123");
        await CloudAccount(admin, "PN-RQ1");
        await Inbound("PN-RQ1", new { from = "60177100002", id = "wamid.RQ.IN1", timestamp = Now(10), type = "text",
            text = new { body = "Do you have size M?" } });
        Graph.Json("POST", @"/PN-RQ1/messages$", new { messages = new[] { new { id = "wamid.RQ.OUT1" } } });
        (await admin.PostAsJsonAsync("/api/whatsapp/threads/60177100002/send", new { text = "Which colour?" }))
            .EnsureSuccessStatusCode();

        // the customer quotes our question
        await Inbound("PN-RQ1", new { from = "60177100002", id = "wamid.RQ.IN2", timestamp = Now(), type = "text",
            context = new { from = "60100000000", id = "wamid.RQ.OUT1" }, text = new { body = "Blue" } });
        var (quotedPk, replyPk) = InScope(db =>
        {
            var reply = db.Messages.Single(m => m.ExternalId == "wamid.RQ.IN2");
            var quoted = db.Messages.Single(m => m.ExternalId == "wamid.RQ.OUT1");
            Assert.Equal(quoted.Id, reply.ReplyToId);
            Assert.Equal("wamid.RQ.OUT1", reply.ReplyToExternalId);
            return (quoted.Id, reply.Id);
        });
        var payload = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177100002");
        var bubble = payload.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("pk").GetInt32() == replyPk);
        Assert.Equal(quotedPk, bubble.GetProperty("reply_to").GetProperty("pk").GetInt32());
        Assert.Equal("Which colour?", bubble.GetProperty("reply_to").GetProperty("text").GetString());

        // a quote of a message we never stored keeps the provider id only
        await Inbound("PN-RQ1", new { from = "60177100002", id = "wamid.RQ.IN3", timestamp = Now(), type = "text",
            context = new { id = "wamid.UNKNOWN" }, text = new { body = "and this" } });
        Assert.Null(InScope(db => db.Messages.Single(m => m.ExternalId == "wamid.RQ.IN3").ReplyToId));

        // we quote the customer's first message: context.message_id goes to Meta
        Graph.Json("POST", @"/PN-RQ1/messages$", new { messages = new[] { new { id = "wamid.RQ.OUT2" } } });
        var inPk = InScope(db => db.Messages.Single(m => m.ExternalId == "wamid.RQ.IN1").Id);
        var sent = await admin.PostAsJsonAsync("/api/whatsapp/threads/60177100002/send", new { text = "Yes, M in blue", reply_to = inPk });
        sent.EnsureSuccessStatusCode();
        var req = JsonSerializer.Deserialize<JsonElement>(Graph.Requests.Last(r => r.Url.EndsWith("/PN-RQ1/messages")).Body);
        Assert.Equal("wamid.RQ.IN1", req.GetProperty("context").GetProperty("message_id").GetString());
        Assert.Equal("Yes, M in blue", req.GetProperty("text").GetProperty("body").GetString());
        var message = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal(inPk, message.GetProperty("reply_to").GetProperty("pk").GetInt32());

        // a reply_to from another conversation is ignored — the text still goes, unquoted
        var foreign = InScope(db =>
        {
            var t = new Thread { Channel = "whatsapp", Key = "60177100099", Name = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Threads.Add(t);
            db.SaveChanges();
            var m = new Message { ThreadId = t.Id, Direction = "in", Source = "customer", Text = "other", ExternalId = "wamid.OTHER", CreatedAt = DateTime.UtcNow };
            db.Messages.Add(m);
            db.SaveChanges();
            return m.Id;
        });
        Graph.Json("POST", @"/PN-RQ1/messages$", new { messages = new[] { new { id = "wamid.RQ.OUT3" } } });
        (await admin.PostAsJsonAsync("/api/whatsapp/threads/60177100002/send", new { text = "plain", reply_to = foreign }))
            .EnsureSuccessStatusCode();
        var plain = JsonSerializer.Deserialize<JsonElement>(Graph.Requests.Last(r => r.Url.EndsWith("/PN-RQ1/messages")).Body);
        Assert.False(plain.TryGetProperty("context", out _));
    }

    [Fact]
    public async Task Outbound_reaction_sends_type_reaction_replaces_removes_and_respects_the_window()
    {
        var admin = await Authed("admin", "admin123");
        await CloudAccount(admin, "PN-RO1");
        await Inbound("PN-RO1", new { from = "60177100003", id = "wamid.RO.IN1", timestamp = Now(3), type = "text",
            text = new { body = "Thanks for the delivery!" } });
        var pk = InScope(db => db.Messages.Single(m => m.ExternalId == "wamid.RO.IN1").Id);
        Graph.Json("POST", @"/PN-RO1/messages$", new { messages = new[] { new { id = "wamid.RO.REACT" } } });
        var url = $"/api/messaging/threads/whatsapp/60177100003/messages/{pk}/react";

        var res = await admin.PostAsJsonAsync(url, new { emoji = "❤️" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("sandbox").GetBoolean());
        var sent = JsonSerializer.Deserialize<JsonElement>(Graph.Requests.Last(r => r.Url.EndsWith("/PN-RO1/messages")).Body);
        Assert.Equal("reaction", sent.GetProperty("type").GetString());
        Assert.Equal("60177100003", sent.GetProperty("to").GetString());
        Assert.Equal("wamid.RO.IN1", sent.GetProperty("reaction").GetProperty("message_id").GetString());
        Assert.Equal("❤️", sent.GetProperty("reaction").GetProperty("emoji").GetString());
        Assert.Equal(("user:admin", "❤️"), InScope(db =>
        {
            var r = db.MessageReactions.Single(x => x.MessageId == pk);
            return (r.Actor, r.Emoji);
        }));
        // no message was added for it
        Assert.Equal(1, InScope(db => db.Messages.Count(m => m.Thread!.Key == "60177100003")));

        // reacting again replaces; "" removes (and tells Meta with an empty emoji)
        (await admin.PostAsJsonAsync(url, new { emoji = "🙏" })).EnsureSuccessStatusCode();
        Assert.Equal("🙏", InScope(db => db.MessageReactions.Single(x => x.MessageId == pk).Emoji));
        (await admin.PostAsJsonAsync(url, new { emoji = "" })).EnsureSuccessStatusCode();
        var removed = JsonSerializer.Deserialize<JsonElement>(Graph.Requests.Last(r => r.Url.EndsWith("/PN-RO1/messages")).Body);
        Assert.Equal("", removed.GetProperty("reaction").GetProperty("emoji").GetString());
        Assert.Equal(0, InScope(db => db.MessageReactions.Count(x => x.MessageId == pk)));

        // not an emoji → 400; a message of another conversation → 404
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(url, new { emoji = "ok" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(
            "/api/messaging/threads/whatsapp/60177100003/messages/999999/react", new { emoji = "👍" })).StatusCode);

        // a closed 24h window: Meta would refuse it, so we do first — a clear 409
        await Inbound("PN-RO1", new { from = "60177100004", id = "wamid.RO.OLD", timestamp = Now(60 * 30), type = "text",
            text = new { body = "old message" } });
        var oldPk = InScope(db => db.Messages.Single(m => m.ExternalId == "wamid.RO.OLD").Id);
        var closed = await admin.PostAsJsonAsync($"/api/messaging/threads/whatsapp/60177100004/messages/{oldPk}/react",
            new { emoji = "👍" });
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.Contains("24h", (await closed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal(0, InScope(db => db.MessageReactions.Count(x => x.MessageId == oldPk)));
    }

    [Fact]
    public async Task React_route_needs_chats_send_and_the_conversation_in_scope()
    {
        var (insidePk, outsidePk) = InScope(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            Entity Ent(string slug)
            {
                var e = db.Entities.FirstOrDefault(x => x.Slug == slug)
                        ?? new Entity { TenantId = tenant.Id, Name = slug, Slug = slug, CreatedAt = now, UpdatedAt = now };
                if (e.Id == 0) { db.Entities.Add(e); db.SaveChanges(); }
                return e;
            }
            var north = Ent("react-north");
            var south = Ent("react-south");
            var agent = db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent");
            if (!agent.Entities.Any(e => e.Id == north.Id)) agent.Entities.Add(north);
            int Make(string key, int entityId)
            {
                var t = new Thread { Channel = "whatsapp", Key = key, Name = key, EntityId = entityId, CreatedAt = now, UpdatedAt = now };
                db.Threads.Add(t);
                db.SaveChanges();
                var m = new Message { ThreadId = t.Id, Direction = "in", Source = "customer", Text = "hi", Status = "received",
                    ExternalId = $"wamid.{key}", CreatedAt = now };
                db.Messages.Add(m);
                db.SaveChanges();
                return m.Id;
            }
            return (Make("60188100001", north.Id), Make("60188100002", south.Id));
        });
        var agentClient = await Authed("agent", "agent123");
        // in scope: a sandbox conversation (no account) stores it
        var ok = await agentClient.PostAsJsonAsync($"/api/messaging/threads/whatsapp/60188100001/messages/{insidePk}/react",
            new { emoji = "👍" });
        ok.EnsureSuccessStatusCode();
        Assert.True((await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sandbox").GetBoolean());
        Assert.Equal("user:agent", InScope(db => db.MessageReactions.Single(r => r.MessageId == insidePk).Actor));
        // outside the agent's outlet: the conversation doesn't exist for them
        Assert.Equal(HttpStatusCode.NotFound, (await agentClient.PostAsJsonAsync(
            $"/api/messaging/threads/whatsapp/60188100002/messages/{outsidePk}/react", new { emoji = "👍" })).StatusCode);
        Assert.Equal(0, InScope(db => db.MessageReactions.Count(r => r.MessageId == outsidePk)));
        // signed out: 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync(
            $"/api/messaging/threads/whatsapp/60188100001/messages/{insidePk}/react", new { emoji = "👍" })).StatusCode);
    }

    [Fact]
    public async Task Baileys_push_carries_quotes_and_reactions_without_making_bubbles()
    {
        var anon = _factory.CreateClient();
        // the push must name a PAIRED number: an unknown account_id is a recorded drop
        // (Channels/BaileysInboxRouting — it used to be filed account-less)
        InScope(db =>
        {
            if (!db.ChannelAccounts.Any(a => a.Provider == "baileys" && a.ExternalId == "gw-x"))
            {
                db.ChannelAccounts.Add(new ChatBranch.Domain.ChannelAccount
                {
                    Channel = "whatsapp", Provider = "baileys", Name = "gw-x", ExternalId = "gw-x",
                    HealthStatus = "healthy", CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
            }
            return 0;
        });
        async Task Push(object body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/whatsapp/baileys/inbound")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Baileys-Secret", "baileys-local-dev");
            (await anon.SendAsync(req)).EnsureSuccessStatusCode();
        }
        await Push(new { account_id = "gw-x", wa_id = "60199100001", name = "Bee", text = "first", msg_id = "BAE5Q1" });
        await Push(new { account_id = "gw-x", wa_id = "60199100001", name = "Bee", text = "replying", msg_id = "BAE5Q2", reply_to = "BAE5Q1" });
        var count = InScope(db =>
        {
            var q2 = db.Messages.Single(m => m.ExternalId == "BAE5Q2");
            Assert.Equal(db.Messages.Single(m => m.ExternalId == "BAE5Q1").Id, q2.ReplyToId);
            return db.Messages.Count(m => m.Thread!.Key == "60199100001");
        });
        await Push(new { account_id = "gw-x", wa_id = "60199100001", reaction = new { msg_id = "BAE5Q1", emoji = "😂" } });
        Assert.Equal(count, InScope(db => db.Messages.Count(m => m.Thread!.Key == "60199100001")));
        Assert.Equal("😂", InScope(db =>
        {
            var target = db.Messages.Single(m => m.ExternalId == "BAE5Q1").Id;
            return db.MessageReactions.Single(r => r.MessageId == target).Emoji;
        }));
    }
}
