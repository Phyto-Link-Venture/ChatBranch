using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Tasks;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Contacts;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ChannelIds = ChatBranch.Domain.Channels;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Group conversations on a QR (Baileys) WhatsApp number, end to end through the API
/// host: a group is ONE thread keyed by its whole jid, named after its subject, with no
/// contact (even when the jid's digits tail-match a customer's phone); each message keeps
/// its author; nothing customer-shaped runs (STOP, identity ask, automations, AI tasks);
/// the AI answers only when switched on for the group AND mentioned; the payloads carry
/// is_group / sender / last_sender.
/// </summary>
public class GroupChatTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public GroupChatTests(ApiFactory f) => _f = f;

    private T Db<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _f.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private HttpClient Gateway()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Baileys-Secret", "baileys-local-dev");
        return c;
    }

    private async Task<HttpClient> Admin()
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>A paired QR number whose inbox answers with AI by default.</summary>
    private int Paired(string key, Dictionary<string, object?>? config = null) => Db(db =>
    {
        var a = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Baileys, Name = key, ExternalId = key,
            HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
            Config = config ?? new Dictionary<string, object?> { [InboxSettings.AiDefaultKey] = "on" },
        };
        db.ChannelAccounts.Add(a);
        db.SaveChanges();
        return a.Id;
    });

    private Task<HttpResponseMessage> GroupSays(string account, string jid, string subject, string senderJid,
        string senderName, string text, string msgId, bool mentioned = false, string phone = "") =>
        Gateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound", new
        {
            account_id = account, wa_id = jid, name = senderName, text, msg_id = msgId,
            group = new { jid, subject }, sender = new { id = senderJid, name = senderName, phone }, mentioned,
        });

    private Thread GroupThread(string jid) => Db(db => db.Threads.AsNoTracking().Single(t => t.Channel == "whatsapp" && t.Key == jid));

    private List<Message> Messages(int threadId) =>
        Db(db => db.Messages.AsNoTracking().Where(m => m.ThreadId == threadId).OrderBy(m => m.Id).ToList());

    private bool AiQueued(Thread t) => Db(db => db.OutboundJobs.Any(j => j.Kind == JobKind.AiReply
        && j.Channel == "whatsapp" && j.ToAddress == t.Key)
        || db.Messages.Any(m => m.ThreadId == t.Id && m.Source == MessageSource.Ai));

    [Fact]
    public async Task A_group_is_one_thread_keyed_by_its_jid_named_after_it_and_never_linked_to_a_contact()
    {
        const string jid = "120363041234567890@g.us";
        Paired("gw-group-1");
        // a customer whose phone shares the jid's last 8 digits — the trap a digits-only key falls into
        var trap = Db(db =>
        {
            var c = new Contact { Name = "Tail Match", Phone = "+60 11-3456 7890", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            return c.Id;
        });

        (await GroupSays("gw-group-1", jid, "Bakery regulars", "60123456789@s.whatsapp.net", "Ana", "Morning all!", "G1-1"))
            .EnsureSuccessStatusCode();

        var t = GroupThread(jid);
        Assert.True(GroupChats.IsGroup(t));
        Assert.Equal("Bakery regulars", t.Name);
        Assert.Null(t.ContactId);
        Assert.False(Db(db => db.Threads.Any(x => x.Channel == "whatsapp" && x.Key == "120363041234567890")));
        Assert.False(Db(db => db.Threads.Any(x => x.ContactId == trap)));
        var m = Assert.Single(Messages(t.Id));
        Assert.Equal(("60123456789@s.whatsapp.net", "Ana", "60123456789"), (m.SenderKey, m.SenderName, m.SenderPhone));

        // a @lid participant with the phone the gateway learned, and a renamed group
        (await GroupSays("gw-group-1", jid, "Bakery regulars 🍞", "98765432101234@lid", "Ben", "Hi!", "G1-2",
            phone: "60199999999")).EnsureSuccessStatusCode();
        t = GroupThread(jid);
        Assert.Equal("Bakery regulars 🍞", t.Name);
        Assert.Null(t.ContactId);
        var ben = Messages(t.Id).Last();
        Assert.Equal(("98765432101234@lid", "Ben", "60199999999"), (ben.SenderKey, ben.SenderName, ben.SenderPhone));
        // a blank subject leaves the name alone
        (await GroupSays("gw-group-1", jid, "", "60123456789@s.whatsapp.net", "Ana", "?", "G1-3")).EnsureSuccessStatusCode();
        Assert.Equal("Bakery regulars 🍞", GroupThread(jid).Name);
        // a redelivery is still one row
        (await GroupSays("gw-group-1", jid, "Bakery regulars 🍞", "60123456789@s.whatsapp.net", "Ana", "?", "G1-3"))
            .EnsureSuccessStatusCode();
        Assert.Equal(3, Messages(t.Id).Count);
    }

    [Fact]
    public async Task Nothing_customer_shaped_runs_in_a_group()
    {
        const string jid = "120363000000000111@g.us";
        Paired("gw-group-2", new Dictionary<string, object?>
        {
            [InboxSettings.AiDefaultKey] = "on", [IdentifySettings.ModeKey] = "ask",
        });
        var ruleId = Db(db =>
        {
            var rule = new AutomationRule
            {
                Name = "group probe", Event = "message_created", Active = true,
                Conditions = new List<object?>
                {
                    new Dictionary<string, object?> { ["field"] = "message", ["op"] = "contains", ["value"] = "zz-grp" },
                },
                Actions = new List<object?> { new Dictionary<string, object?> { ["type"] = "add_label", ["value"] = "probe" } },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.AutomationRules.Add(rule);
            db.SaveChanges();
            return rule.Id;
        });

        (await GroupSays("gw-group-2", jid, "Team", "60111111111@s.whatsapp.net", "Cai", "hello zz-grp", "G2-1"))
            .EnsureSuccessStatusCode();
        (await GroupSays("gw-group-2", jid, "Team", "60111111112@s.whatsapp.net", "Dee", "STOP", "G2-2"))
            .EnsureSuccessStatusCode();

        var t = GroupThread(jid);
        Assert.False(t.OptedOut);                                    // one member's STOP opts nobody out
        Assert.Equal(0, Db(db => db.AutomationRules.Single(r => r.Id == ruleId).TimesFired));
        Assert.DoesNotContain("probe", t.Labels);
        Assert.Empty(Messages(t.Id).Where(m => m.Direction == MessageDirection.Out)); // no "who are you?" ask
        Assert.False(AiQueued(t));                                   // inbox AI default on — still off in a group

        // the defence-in-depth guards on the shared seams
        Db(db =>
        {
            var sc = new ScopingService(db);
            var tracked = db.Threads.Single(x => x.Id == t.Id);
            var msg = db.Messages.First(m => m.ThreadId == t.Id);
            Assert.Null(OptOut.Apply(db, sc, new ThreadStore(db, sc), tracked, "STOP"));
            Assert.Equal(0, AiTaskService.Enqueue(db, sc, tracked, msg));
            return 0;
        });
    }

    [Fact]
    public async Task The_AI_answers_a_group_only_when_switched_on_for_it_and_mentioned()
    {
        const string jid = "120363000000000222@g.us";
        Paired("gw-group-3");
        (await GroupSays("gw-group-3", jid, "Club", "60122222221@s.whatsapp.net", "Eve", "anyone open today?", "G3-1"))
            .EnsureSuccessStatusCode();
        var t = GroupThread(jid);
        Assert.False(AiQueued(t));
        var verdict = Db(db => AiEligibility.Decide(db, new ScopingService(db), db.Threads.Single(x => x.Id == t.Id)));
        Assert.False(verdict.Enabled);
        Assert.Equal(AiRules.Group, verdict.Rule);
        Assert.False(verdict.Forced);   // the operator may switch it on

        // mentioned, but nobody switched the AI on for this group
        (await GroupSays("gw-group-3", jid, "Club", "60122222221@s.whatsapp.net", "Eve", "@shop are you open?", "G3-2",
            mentioned: true)).EnsureSuccessStatusCode();
        Assert.False(AiQueued(t));

        // switched on: chatter is still not answered…
        Db(db =>
        {
            var tracked = db.Threads.Single(x => x.Id == t.Id);
            AiEligibility.Set(tracked, true);
            return db.SaveChanges();
        });
        (await GroupSays("gw-group-3", jid, "Club", "60122222223@s.whatsapp.net", "Fin", "lol", "G3-3"))
            .EnsureSuccessStatusCode();
        Assert.False(AiQueued(t));
        // …a mention is
        (await GroupSays("gw-group-3", jid, "Club", "60122222221@s.whatsapp.net", "Eve", "@shop open today?", "G3-4",
            mentioned: true)).EnsureSuccessStatusCode();
        Assert.True(AiQueued(t));
        Assert.True(Db(db => AiEligibility.Decide(db, new ScopingService(db), db.Threads.Single(x => x.Id == t.Id)).Enabled));
    }

    [Fact]
    public async Task A_group_file_attaches_to_the_group_with_its_author()
    {
        const string jid = "120363000000000333@g.us";
        Paired("gw-group-4");
        var form = new MultipartFormDataContent
        {
            { new StringContent("gw-group-4"), "account_id" },
            { new StringContent(jid), "wa_id" },
            { new StringContent("G4-IMG"), "msg_id" },
            { new StringContent("image"), "media_type" },
            { new StringContent("60133333333@s.whatsapp.net"), "sender_id" },
            { new StringContent("Gia"), "sender_name" },
        };
        var file = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "media.jpg");
        var res = await Gateway().PostAsync("/api/whatsapp/baileys/media", form);
        res.EnsureSuccessStatusCode();

        var t = GroupThread(jid);
        Assert.Null(t.ContactId);
        var m = Assert.Single(Messages(t.Id));
        Assert.Equal(("image", "60133333333@s.whatsapp.net", "Gia"), (m.MediaType, m.SenderKey, m.SenderName));
        Assert.NotEqual("", m.MediaPath);
        Assert.False(Db(db => db.Threads.Any(x => x.Key == "120363000000000333")));
    }

    [Fact]
    public async Task Payloads_carry_is_group_sender_and_last_sender()
    {
        const string jid = "120363000000000444@g.us";
        Paired("gw-group-5");
        (await GroupSays("gw-group-5", jid, "Payload group", "60144444444@s.whatsapp.net", "Hana", "first", "G5-1"))
            .EnsureSuccessStatusCode();
        (await GroupSays("gw-group-5", jid, "Payload group", "4444@lid", "Ivo", "second", "G5-2"))
            .EnsureSuccessStatusCode();
        var t = GroupThread(jid);
        var admin = await Admin();

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/inbox/threads?q={Uri.EscapeDataString("Payload group")}");
        var row = list.GetProperty("threads").EnumerateArray().Single(r => r.GetProperty("thread_id").GetInt32() == t.Id);
        Assert.True(row.GetProperty("is_group").GetBoolean());
        Assert.Equal("Ivo", row.GetProperty("last_sender").GetString());
        Assert.Equal("Payload group", row.GetProperty("name").GetString());
        Assert.Equal(AiRules.Group, row.GetProperty("ai_rule").GetString());

        var thread = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/whatsapp/threads/{Uri.EscapeDataString(jid)}?thread_id={t.Id}");
        Assert.True(thread.GetProperty("is_group").GetBoolean());
        var msgs = thread.GetProperty("messages").EnumerateArray().ToList();
        var hana = msgs.Single(m => m.GetProperty("text").GetString() == "first").GetProperty("sender");
        Assert.Equal(("60144444444@s.whatsapp.net", "Hana", "60144444444"),
            (hana.GetProperty("id").GetString(), hana.GetProperty("name").GetString(), hana.GetProperty("phone").GetString()));
        var ivo = msgs.Single(m => m.GetProperty("text").GetString() == "second").GetProperty("sender");
        Assert.Equal(JsonValueKind.Null, ivo.GetProperty("phone").ValueKind);

        var unified = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/messaging/threads/whatsapp/{Uri.EscapeDataString(jid)}?thread_id={t.Id}");
        Assert.True(unified.GetProperty("is_group").GetBoolean());
        Assert.Equal("Hana", unified.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("text").GetString() == "first").GetProperty("sender").GetProperty("name").GetString());

        // a 1:1 chat's rows say so too, and its bubbles carry no sender
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound",
            new { account_id = "gw-group-5", wa_id = "60155555555", name = "Solo", text = "hi", msg_id = "G5-SOLO" }))
            .EnsureSuccessStatusCode();
        var solo = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60155555555");
        Assert.False(solo.GetProperty("is_group").GetBoolean());
        Assert.False(solo.GetProperty("messages")[0].TryGetProperty("sender", out _));

        // no calls in a group
        var call = await admin.PostAsJsonAsync($"/api/whatsapp/threads/{Uri.EscapeDataString(jid)}/call/start?thread_id={t.Id}",
            new { });
        Assert.Equal(HttpStatusCode.BadRequest, call.StatusCode);
    }
}

/// <summary>The seams below the webhook: keys, the send contract, routing, the AI rule, parsers.</summary>
public class GroupChatUnitTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void Group_keys_are_recognised_per_channel()
    {
        Assert.True(GroupChats.IsGroup("whatsapp", "120363041234567890@g.us"));
        Assert.False(GroupChats.IsGroup("whatsapp", "60123456789"));
        Assert.True(GroupChats.IsGroup("telegram", "-1001234567890"));
        Assert.False(GroupChats.IsGroup("telegram", "8101"));
        Assert.True(GroupChats.IsGroup("line", "C4af4980629"));
        Assert.True(GroupChats.IsGroup("line", "Rb1c2d3e4"));
        Assert.False(GroupChats.IsGroup("line", "U1234"));
        Assert.False(GroupChats.IsGroup("email", "-x@y"));

        Assert.Equal("120363041234567890@g.us", GroupChats.WhatsAppKey(" 120363041234567890@G.US "));
        Assert.Equal("60123456789", GroupChats.WhatsAppKey("+60 12-345 6789"));
        Assert.Equal("60123456789", GroupChats.SenderPhone("60123456789:12@s.whatsapp.net"));
        Assert.Equal("", GroupChats.SenderPhone("1234@lid"));
    }

    [Fact]
    public void A_reply_and_a_reaction_in_a_group_name_the_member_who_wrote_it()
    {
        var gw = new QuoteBaileys();
        var sc = new ScopingService(_t.Db);
        var threads = new ThreadStore(_t.Db, sc);
        var sender = new OutboundSender(_t.Db, threads, new WhatsAppCloudClient(new FakeHttpFactory(new FakeGraph())), gw);
        var account = new ChannelAccount { Channel = "whatsapp", Provider = Providers.Baileys, Name = "QR", ExternalId = "gw-g",
            CreatedAt = DateTime.UtcNow };
        _t.Db.ChannelAccounts.Add(account);
        _t.Db.SaveChanges();
        var thread = threads.GetOrCreateThread("whatsapp", "120363000000009999@g.us", "Group", account.Id);
        var inbound = threads.AppendMessage(thread, "in", "customer", "Who has the keys?", status: "received",
            externalId: "BAEGRP1", accountId: account.Id,
            sender: new InboundSender("60177777777@s.whatsapp.net", "Jo"));

        var reply = sender.Send(thread, "Jo does", MessageSource.Agent, account.Id, replyTo: inbound);
        Assert.Equal("sent", reply.Status);
        var (_, quote) = Assert.Single(gw.Sends);
        Assert.Equal(new BaileysQuote("BAEGRP1", false, "Who has the keys?", "60177777777@s.whatsapp.net"), quote);
        sender.React(thread, inbound, "👍");
        Assert.Equal("60177777777@s.whatsapp.net", Assert.Single(gw.Reactions).Target.Participant);
        // our own message carries no participant
        sender.React(thread, reply, "");
        Assert.Equal("", gw.Reactions[1].Target.Participant);
    }

    [Fact]
    public async Task The_http_gateway_sends_the_group_jid_and_the_participant()
    {
        var sc = new ScopingService(_t.Db);
        sc.SetSetting("baileys_url", "http://gw.test/");
        sc.SetSetting("baileys_secret", "s3cret");
        var services = new ServiceCollection();
        services.AddScoped(_ => new ScopingService(_t.Db));
        await using var sp = services.BuildServiceProvider();
        var stub = new GatewayStub();
        var gw = new HttpBaileysGateway(new FakeHttpFactory(stub), sp.GetRequiredService<IServiceScopeFactory>());

        await gw.SendTextAsync("gw1", "1203@g.us", "yes", new BaileysQuote("ABC", false, "q?", "601@s.whatsapp.net"));
        var body = JsonSerializer.Deserialize<JsonElement>(stub.Requests[0].Body);
        Assert.Equal(("1203@g.us", "601@s.whatsapp.net"),
            (body.GetProperty("to").GetString(), body.GetProperty("quoted_participant").GetString()));
        await gw.ReactAsync("gw1", "1203@g.us", new BaileysQuote("XYZ", false, Participant: "602@lid"), "😂");
        var react = JsonSerializer.Deserialize<JsonElement>(stub.Requests[1].Body);
        Assert.Equal("602@lid", react.GetProperty("participant").GetString());
        // a 1:1 quote carries no participant key
        await gw.SendTextAsync("gw1", "60123", "ok", new BaileysQuote("DEF", true, "x"));
        Assert.False(JsonSerializer.Deserialize<JsonElement>(stub.Requests[2].Body).TryGetProperty("quoted_participant", out _));
    }

    [Fact]
    public void A_group_stays_on_the_number_that_is_in_it_even_while_that_number_is_down()
    {
        var sc = new ScopingService(_t.Db);
        var threads = new ThreadStore(_t.Db, sc);
        ChannelAccount Qr(string key, string health)
        {
            var a = new ChannelAccount { Channel = "whatsapp", Provider = Providers.Baileys, Name = key, ExternalId = key,
                HealthStatus = health, CreatedAt = DateTime.UtcNow };
            _t.Db.ChannelAccounts.Add(a);
            _t.Db.SaveChanges();
            return a;
        }
        var down = Qr("gw-down", Health.Disconnected);
        Qr("gw-up", Health.Healthy);
        var group = threads.GetOrCreateThread("whatsapp", "120363000000008888@g.us", "G", down.Id);
        var route = new Routing(_t.Db, sc).ForThread(group, RouteNeed.Conversation);
        Assert.Equal(down.Id, route.Account?.Id);
        // the control: a 1:1 chat on the dead number fails over
        var solo = threads.GetOrCreateThread("whatsapp", "60188888888", "S", down.Id);
        Assert.NotEqual(down.Id, new Routing(_t.Db, sc).ForThread(solo, RouteNeed.Conversation).Account?.Id);
    }

    [Fact]
    public void Telegram_group_messages_are_the_groups_with_their_author_and_mention()
    {
        static JsonElement Update(object message) =>
            JsonSerializer.SerializeToElement(new { update_id = 1, message });
        var chat = new { id = -1001234567890L, type = "supergroup", title = "Shop fans" };
        var from = new { id = 4242L, is_bot = false, first_name = "Kim", last_name = "Lee" };

        var plain = TelegramInbound.Parse(Update(new { message_id = 1, chat, from, text = "hello all" }), "shop_bot", "999")!;
        Assert.True(plain.IsGroup);
        Assert.Equal(("-1001234567890", "Shop fans"), (plain.ChatId, plain.Name));
        Assert.Equal(new InboundSender("4242", "Kim Lee"), plain.Sender);
        Assert.False(plain.Mentioned);

        var mention = TelegramInbound.Parse(Update(new
        {
            message_id = 2, chat, from, text = "@shop_bot price?",
            entities = new[] { new { type = "mention", offset = 0, length = 9 } },
        }), "shop_bot", "999")!;
        Assert.True(mention.Mentioned);
        var other = TelegramInbound.Parse(Update(new
        {
            message_id = 3, chat, from, text = "@other_bot price?",
            entities = new[] { new { type = "mention", offset = 0, length = 10 } },
        }), "shop_bot", "999")!;
        Assert.False(other.Mentioned);
        var command = TelegramInbound.Parse(Update(new
        {
            message_id = 4, chat, from, text = "/menu@shop_bot",
            entities = new[] { new { type = "bot_command", offset = 0, length = 14 } },
        }), "shop_bot", "999")!;
        Assert.True(command.Mentioned);
        var reply = TelegramInbound.Parse(Update(new
        {
            message_id = 5, chat, from, text = "and delivery?",
            reply_to_message = new { message_id = 4, from = new { id = 999L, is_bot = true } },
        }), "shop_bot", "999")!;
        Assert.True(reply.Mentioned);

        // a private chat is unchanged: named after the person, no sender
        var dm = TelegramInbound.Parse(Update(new
        {
            message_id = 6, chat = new { id = 4242L, type = "private" }, from, text = "hi",
        }))!;
        Assert.False(dm.IsGroup);
        Assert.Null(dm.Sender);
        Assert.Equal("Kim Lee", dm.Name);
    }

    [Fact]
    public void Line_group_and_room_events_are_keyed_by_the_chat()
    {
        var root = JsonSerializer.SerializeToElement(new
        {
            destination = "Ubot",
            events = new object[]
            {
                new
                {
                    type = "message", replyToken = "rt-g", timestamp = 1700000000000,
                    source = new { type = "group", groupId = "Cgroup1", userId = "Uann" },
                    message = new
                    {
                        type = "text", id = "m1", text = "@Shop hi",
                        mention = new { mentionees = new[] { new { index = 0, length = 5, isSelf = true } } },
                    },
                },
                new
                {
                    type = "message", replyToken = "rt-r", timestamp = 1700000000000,
                    source = new { type = "room", roomId = "Rroom1", userId = "Ubob" },
                    message = new { type = "text", id = "m2", text = "chatter" },
                },
                // following is a 1:1 matter — never from a group
                new { type = "follow", replyToken = "x", timestamp = 1, source = new { type = "group", groupId = "Cgroup1" } },
                new { type = "unsend", timestamp = 1, source = new { type = "group", groupId = "Cgroup1", userId = "Uann" },
                      unsend = new { messageId = "m1" } },
            },
        });
        var (_, events) = LineInbound.Parse(root);
        Assert.Equal(3, events.Count);
        Assert.Equal(("Cgroup1", "Uann", true, true), (events[0].ChatKey, events[0].UserId, events[0].IsGroup, events[0].Mentioned));
        Assert.Equal(("Rroom1", "Ubob", true, false), (events[1].ChatKey, events[1].UserId, events[1].IsGroup, events[1].Mentioned));
        Assert.Equal(("unsend", "Cgroup1"), (events[2].Type, events[2].ChatKey));
    }

    [Fact]
    public void Zalo_group_messages_are_ignored_rather_than_filed_as_a_members_1to1()
    {
        var ev = ZaloInbound.Parse(JsonSerializer.SerializeToElement(new
        {
            app_id = "1", event_name = "user_send_group_text", timestamp = "1700000000000",
            sender = new { id = "zu-1" }, recipient = new { id = "grp-1" }, message = new { text = "hi", msg_id = "z1" },
        }));
        Assert.Equal("ignore", ev!.Kind);
    }
}

/// <summary>A Telegram group through the bot's webhook: keyed by the chat, named after it, authors kept.</summary>
public class GroupChatTelegramTests : IClassFixture<TransportFactory>
{
    private readonly TransportFactory _f;
    public GroupChatTelegramTests(TransportFactory f) => _f = f;

    private T InDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task A_telegram_group_is_one_thread_named_after_the_chat()
    {
        var bot = InDb(db =>
        {
            var a = new ChannelAccount { Channel = "telegram", Name = "Group bot", AccessToken = "5202:group-bot-token-zzzz",
                HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow, ExternalId = "5202",
                Config = new Dictionary<string, object?> { [TelegramBot.UsernameKey] = "group_shop_bot" } };
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a;
        });
        var hook = $"/api/messaging/webhook/telegram/{bot.AccessToken}";
        object Update(long id, string title, long fromId, string first, string text) => new
        {
            update_id = id, message = new
            {
                message_id = id, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                chat = new { id = -1009990001L, type = "supergroup", title },
                from = new { id = fromId, is_bot = false, first_name = first }, text,
            },
        };
        var anon = _f.CreateClient();
        (await anon.PostAsJsonAsync(hook, Update(70, "Fans", 31, "Lia", "hi all"))).EnsureSuccessStatusCode();
        (await anon.PostAsJsonAsync(hook, Update(71, "Fans club", 32, "Max", "STOP"))).EnsureSuccessStatusCode();

        var (thread, msgs) = InDb(db =>
        {
            var t = db.Threads.AsNoTracking().Single(x => x.Channel == "telegram" && x.Key == "-1009990001");
            return (t, db.Messages.AsNoTracking().Where(m => m.ThreadId == t.Id).OrderBy(m => m.Id).ToList());
        });
        Assert.Equal("Fans club", thread.Name);
        Assert.False(thread.OptedOut);
        Assert.Null(thread.ContactId);
        Assert.Equal(new[] { ("31", "Lia"), ("32", "Max") }, msgs.Select(m => (m.SenderKey, m.SenderName)).ToArray());
        // no thread was opened for either member
        Assert.False(InDb(db => db.Threads.Any(x => x.Channel == "telegram" && (x.Key == "31" || x.Key == "32"))));
    }
}

/// <summary>A LINE group through the signed webhook: keyed by groupId, named by its summary, replies by its token.</summary>
public class GroupChatLineTests : IClassFixture<MoreChannelsFactory>
{
    private readonly MoreChannelsFactory _f;
    public GroupChatLineTests(MoreChannelsFactory f)
    {
        _f = f;
        _f.Http.Reset();
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

    [Fact]
    public async Task A_line_group_is_keyed_by_its_group_id()
    {
        var a = InDb(db =>
        {
            var x = new ChannelAccount { Channel = ChannelIds.Line, Name = "LINE grp", AccessToken = "line-token",
                AppSecret = "line-secret", CreatedAt = DateTime.UtcNow };
            db.ChannelAccounts.Add(x);
            db.SaveChanges();
            return x;
        });
        var groupId = "Cgrp" + Guid.NewGuid().ToString("N")[..8];
        _f.Http.Respond = c => c.Host == "api.line.me" && c.Path.EndsWith("/summary")
            ? FakeProviderHttp.Json(new { groupId, groupName = "Weekend riders" })
            : c.Host == "api.line.me" && c.Path.Contains("/member/")
                ? FakeProviderHttp.Json(new { displayName = "Nao", userId = "Unao" })
                : null;
        var body = JsonSerializer.Serialize(new
        {
            destination = "Ubot0001",
            events = new object[]
            {
                new
                {
                    type = "message", replyToken = "grp-token", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    source = new { type = "group", groupId, userId = "Unao" },
                    message = new { type = "text", id = "lm-" + groupId, text = "who's riding?" },
                },
            },
        });
        var c = _f.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/messaging/webhook/line/{a.Id}")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation(LineTransport.SignatureHeader,
            LineTransport.Sign("line-secret", Encoding.UTF8.GetBytes(body)));
        (await c.SendAsync(req)).EnsureSuccessStatusCode();

        Thread T() => InDb(db => db.Threads.AsNoTracking().Single(t => t.Channel == ChannelIds.Line && t.Key == groupId));
        Message M() { var tid = T().Id; return InDb(db => db.Messages.AsNoTracking().Single(m => m.ThreadId == tid)); }
        Assert.Null(T().ContactId);
        Assert.Equal("Unao", M().SenderKey);
        Assert.False(InDb(db => db.Threads.Any(t => t.Channel == ChannelIds.Line && t.Key == "Unao")));
        await WaitFor(() => T().Name == "Weekend riders");
        await WaitFor(() => M().SenderName == "Nao");

        // our answer uses the group's reply token, addressed to the group
        var admin = _f.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await admin.PostAsJsonAsync($"/api/messaging/threads/line/{groupId}/send", new { text = "Me!" }))
            .EnsureSuccessStatusCode();
        var reply = Assert.Single(_f.Http.To("api.line.me", "/message/reply"));
        Assert.Equal("grp-token", reply.Json.GetProperty("replyToken").GetString());
    }
}
