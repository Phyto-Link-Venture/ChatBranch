using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Social;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ChannelIds = ChatBranch.Domain.Channels;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Zernio as a CONNECTION METHOD on native channels ("managed connection"): WhatsApp /
/// Messenger / Instagram / Telegram / SMS accounts become inboxes of those channels with
/// provider "zernio"; inbound runs the native pipeline under the native key, replies address
/// Zernio's conversation id, and the connect flows (hosted, Telegram code, WhatsApp
/// credentials, SMS number) end in an inbox. Every Zernio call goes to <see cref="FakeZernioClient"/>.
/// </summary>
public class ZernioNativeTests : IClassFixture<ZernioFactory>
{
    private readonly ZernioFactory _f;
    private const string Secret = "zernio-native-secret";

    public ZernioNativeTests(ZernioFactory f)
    {
        _f = f;
        _ = _f.CreateClient();
        _f.Zernio.Reset();
        InDb(db => { new ScopingService(db).SetSetting("zernio_webhook_secret", Secret); return 0; });
    }

    private static string U() => Guid.NewGuid().ToString("N")[..10];
    private static string Digits() => "6012" + Random.Shared.Next(1000000, 9999999);

    private T InDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<HttpClient> Admin() => SettingsTestKit.Client(_f, "admin", "admin123");

    /// <summary>A native inbox carried by Zernio (what the mirror makes for a native-capable platform).</summary>
    private ChannelAccount Native(string channel, int? entityId = null, string address = "")
    {
        return InDb(db =>
        {
            var zid = "zn-" + U();
            var a = new ChannelAccount
            {
                Channel = channel, Provider = Providers.Zernio, ExternalId = zid, Name = $"native {channel} {U()}",
                EntityId = entityId, CreatedAt = DateTime.UtcNow, HealthStatus = Health.Healthy,
                Config = new Dictionary<string, object?>
                {
                    [ZernioNative.AccountIdKey] = zid, ["platform"] = ZernioNative.PlatformFor(channel),
                    ["username"] = "kedai", ["zernio_status"] = "connected", [ZernioNative.AddressKey] = address,
                },
            };
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a;
        });
    }

    private async Task<JsonElement> Hook(string eventName, object body)
    {
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/zernio/webhook")
        {
            Content = new ByteArrayContent(raw) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        req.Headers.TryAddWithoutValidation("X-Zernio-Event", eventName);
        req.Headers.TryAddWithoutValidation("X-Zernio-Event-Id", Guid.NewGuid().ToString());
        req.Headers.TryAddWithoutValidation("X-Zernio-Signature", ZernioWebhook.Sign(Secret, raw));
        var res = await _f.CreateClient().SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object Received(ChannelAccount a, string platform, string conversationId, string text,
        object sender, string platformConversationId, object[]? attachments = null, string? pmid = null) => new
    {
        id = Guid.NewGuid().ToString(),
        @event = "message.received",
        message = new
        {
            id = "m-" + Guid.NewGuid().ToString("N")[..8], conversationId, platform,
            platformMessageId = pmid ?? "pm-" + Guid.NewGuid().ToString("N")[..10], direction = "incoming", text,
            attachments = attachments ?? Array.Empty<object>(), sender, sentAt = DateTime.UtcNow.ToString("o"), isRead = false,
        },
        conversation = new { id = conversationId, platformConversationId, status = "active" },
        account = new { id = a.ExternalId, accountId = a.ExternalId, platform, username = "kedai" },
        timestamp = DateTime.UtcNow.ToString("o"),
    };

    private Thread? ThreadOf(string channel, string key) =>
        InDb(db => db.Threads.AsNoTracking().FirstOrDefault(t => t.Channel == channel && t.Key == key));

    // ---- WhatsApp ---------------------------------------------------------------------

    [Fact]
    public async Task WhatsApp_via_zernio_links_the_contact_by_phone_opens_the_native_thread_and_replies_in_its_conversation()
    {
        var wa = Native(ChannelIds.WhatsApp);
        var phone = Digits();
        var contactId = InDb(db =>
        {
            var c = new Contact { Name = "Aminah " + U(), Phone = "+" + phone, CreatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            return c.Id;
        });
        var conv = "conv-" + U();
        _f.Zernio.Files["https://zernio.test/api/v1/whatsapp/media/77"] = (new byte[] { 1, 2, 3 }, "image/jpeg");
        var hook = await Hook("message.received", Received(wa, "whatsapp", conv, "Ada stok?",
            new { id = phone, name = "Aminah", phoneNumber = "+" + phone }, phone,
            new object[] { new { type = "image", url = "https://zernio.test/api/v1/whatsapp/media/77" } }));
        Assert.Equal("ok", hook.GetProperty("result").GetString());

        var thread = ThreadOf(ChannelIds.WhatsApp, phone)!;
        Assert.NotNull(thread);
        Assert.Equal(wa.Id, thread.AccountId);
        Assert.Equal(contactId, thread.ContactId);              // the native pipeline linked the contact
        Assert.Equal(conv, InDb(db => ZernioNative.Conversation(db, wa.Id, phone))!.ConversationId);
        await _f.Services.GetRequiredService<MediaFetcher>().WhenIdleAsync(TimeSpan.FromSeconds(10));
        var inbound = InDb(db => db.Messages.AsNoTracking().Single(m => m.ThreadId == thread.Id));
        Assert.Equal("image", inbound.MediaType);
        Assert.Contains(_f.Zernio.Of("Download"), c => (string?)c["url"] == "https://zernio.test/api/v1/whatsapp/media/77");

        var c = await Admin();
        var send = await c.PostAsJsonAsync($"/api/messaging/threads/whatsapp/{phone}/send", new { text = "Ada, RM12" });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var sent = (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("sent", sent.GetProperty("status").GetString());
        var call = Assert.Single(_f.Zernio.Of("SendInboxMessage"));
        Assert.Equal(conv, call["conversationId"]);
        Assert.Equal(wa.ExternalId, call["accountId"]);
        Assert.Equal("Ada, RM12", call["message"]);

        // delivery receipts move it forward
        var ext = InDb(db => db.Messages.AsNoTracking().Single(m => m.ThreadId == thread.Id && m.Direction == "out").ExternalId);
        await Hook("message.delivered", new
        {
            id = Guid.NewGuid().ToString(), @event = "message.delivered", statusAt = DateTime.UtcNow.ToString("o"),
            message = new { platformMessageId = ext, platform = "whatsapp" }, account = new { id = wa.ExternalId },
        });
        Assert.Equal("delivered", InDb(db => db.Messages.AsNoTracking().Single(m => m.ExternalId == ext).Status));
        await Hook("message.read", new
        {
            id = Guid.NewGuid().ToString(), @event = "message.read",
            message = new { platformMessageId = ext, platform = "whatsapp" }, account = new { id = wa.ExternalId },
        });
        Assert.Equal("read", InDb(db => db.Messages.AsNoTracking().Single(m => m.ExternalId == ext).Status));
    }

    [Fact]
    public void WhatsApp_via_zernio_is_an_official_number_with_templates_in_and_out_of_a_conversation()
    {
        var wa = Native(ChannelIds.WhatsApp);
        Assert.True(WhatsAppProviders.IsOfficial(wa));
        Assert.True(Routing.IsConfigured(wa));
        _f.Zernio.Templates.Add(new
        {
            id = "t1", name = "order_update", status = "APPROVED", category = "UTILITY", language = "en",
            components = new object[] { new { type = "BODY", text = "Hi {{1}}, your order {{2}} is ready." } },
        });
        var known = Digits();
        var fresh = Digits();
        InDb(db =>
        {
            // a conversation from 3 days ago: the 24h window is closed
            var t = new Thread { Channel = ChannelIds.WhatsApp, Key = known, Name = "old", AccountId = wa.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Threads.Add(t);
            db.SaveChanges();
            db.Messages.Add(new Message { ThreadId = t.Id, Direction = "in", Text = "hi", Status = "received", CreatedAt = DateTime.UtcNow.AddDays(-3) });
            db.SaveChanges();
            ZernioNative.Remember(db, db.ChannelAccounts.Single(a => a.Id == wa.Id), known, "conv-old");
            db.Threads.Add(new Thread { Channel = ChannelIds.WhatsApp, Key = fresh, Name = "new", AccountId = wa.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
            return 0;
        });

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<OutboundSender>();
        var oldThread = db.Threads.Single(t => t.Channel == ChannelIds.WhatsApp && t.Key == known);
        var args = new TemplateArgs(new List<string> { "Aminah", "#42" });
        var a = sender.SendTemplate(oldThread, "order_update", "en", args, MessageSource.Agent, wa.Id);
        Assert.Equal("sent", a.Status);
        Assert.Contains("Aminah", a.Text);  // rendered from Zernio's template registry
        var inConv = Assert.Single(_f.Zernio.Of("SendInboxMessage"));
        Assert.Equal("conv-old", inConv["conversationId"]);
        var tpl = JsonSerializer.SerializeToElement(inConv["template"]).GetProperty("elements")[0];
        Assert.Equal("order_update", tpl.GetProperty("name").GetString());
        Assert.Equal("en", tpl.GetProperty("language").GetString());

        // nobody to write to yet: the template OPENS the conversation (create-conversation), remembered for replies
        var newThread = db.Threads.Single(t => t.Channel == ChannelIds.WhatsApp && t.Key == fresh);
        var b = sender.SendTemplate(newThread, "order_update", "en", args, MessageSource.Agent, wa.Id);
        Assert.Equal("sent", b.Status);
        var made = Assert.Single(_f.Zernio.Of("CreateConversation"));
        Assert.Equal(fresh, made["participantId"]);
        Assert.Equal("order_update", made["templateName"]);
        Assert.Equal(new[] { "Aminah", "#42" }, ((IEnumerable<string>)made["templateParams"]!).ToArray());
        Assert.NotNull(ZernioNative.Conversation(db, wa.Id, fresh));

        // a free-text first message on WhatsApp is refused honestly (no conversation, no template)
        var third = new Thread { Channel = ChannelIds.WhatsApp, Key = Digits(), Name = "x", AccountId = wa.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Threads.Add(third);
        db.SaveChanges();
        var refused = sender.Send(third, "hello", MessageSource.Agent, wa.Id);
        Assert.Equal("failed", refused.Status);
        Assert.Contains("template", refused.Text);
    }

    // ---- Telegram + Messenger ---------------------------------------------------------

    [Fact]
    public async Task Telegram_and_messenger_via_zernio_use_the_native_keys_and_reply_through_zernio()
    {
        var tg = Native(ChannelIds.Telegram);
        var chat = Random.Shared.Next(100000000, 999999999).ToString();
        var tconv = "tconv-" + U();
        await Hook("message.received", Received(tg, "telegram", tconv, "/start", new { id = chat, name = "Ali" }, chat, pmid: "55"));
        var tthread = ThreadOf(ChannelIds.Telegram, chat)!;
        Assert.Equal(tg.Id, tthread.AccountId);
        var c = await Admin();
        var reply = await c.PostAsJsonAsync($"/api/messaging/threads/telegram/{chat}/send", new { text = "Hello Ali" });
        Assert.Equal("sent", (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetProperty("status").GetString());
        Assert.Equal(tconv, _f.Zernio.Of("SendInboxMessage").Last()["conversationId"]);

        // a Telegram group: the group-chat model (the author on the message, no contact)
        var group = "-100" + Random.Shared.Next(100000000, 999999999);
        await Hook("message.received", Received(tg, "telegram", "g-" + U(), "hi all", new { id = "777", name = "Siti" }, group));
        var gthread = ThreadOf(ChannelIds.Telegram, group)!;
        Assert.Null(gthread.ContactId);
        Assert.Equal("Siti", InDb(db => db.Messages.AsNoTracking().Single(m => m.ThreadId == gthread.Id).SenderName));

        // Messenger: the PSID keys the thread; 24h–7d a PERSON's reply goes out tagged HUMAN_AGENT
        var fb = Native(ChannelIds.Facebook);
        var psid = "psid" + U();
        var fconv = "fconv-" + U();
        await Hook("message.received", Received(fb, "facebook", fconv, "Buka?", new { id = psid, name = "Mei" }, psid));
        var fthread = ThreadOf(ChannelIds.Facebook, psid)!;
        Assert.Equal(fb.Id, fthread.AccountId);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/messaging/threads/facebook/{psid}/send", new { text = "Buka 9am" })).StatusCode);
        var plain = _f.Zernio.Of("SendInboxMessage").Last();
        Assert.Equal(fconv, plain["conversationId"]);
        Assert.False(plain.ContainsKey("messageTag"));
        InDb(db => { db.Messages.Where(m => m.ThreadId == fthread.Id).ExecuteUpdate(s => s.SetProperty(m => m.CreatedAt, DateTime.UtcNow.AddDays(-2))); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/messaging/threads/facebook/{psid}/send", new { text = "Sorry for the wait" })).StatusCode);
        var tagged = _f.Zernio.Of("SendInboxMessage").Last();
        Assert.Equal("HUMAN_AGENT", tagged["messageTag"]);
        Assert.Equal("MESSAGE_TAG", tagged["messagingType"]);
    }

    [Fact]
    public async Task A_native_non_zernio_inbox_keeps_its_dms_unless_the_zernio_account_itself_is_the_inbox()
    {
        var entity = InDb(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var e = new Entity { Name = "IG Outlet " + U(), Slug = "ig-outlet-" + U(), TenantId = tenant.Id };
            db.Entities.Add(e);
            db.ChannelAccounts.Add(new ChannelAccount { Channel = ChannelIds.Instagram, Name = "meta ig " + U(), EntityId = null, CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
            db.ChannelAccounts.Add(new ChannelAccount { Channel = ChannelIds.Instagram, Name = "meta ig " + U(), EntityId = e.Id, AccessToken = "tok", CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
            return e.Id;
        });
        // a social-only Zernio account (comments) in that outlet: its DMs stay with the Meta inbox
        var social = InDb(db =>
        {
            var a = new ChannelAccount
            {
                Channel = ChannelIds.Social, Provider = Providers.Zernio, ExternalId = "zs-" + U(), Name = "@kedai (Instagram)",
                EntityId = entity, CreatedAt = DateTime.UtcNow,
                Config = new Dictionary<string, object?> { ["platform"] = "instagram", ["zernio_status"] = "connected" },
            };
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a;
        });
        var igsid = "ig" + U();
        var skipped = await Hook("message.received", Received(social, "instagram", "c-" + U(), "hello", new { id = igsid }, igsid));
        Assert.Equal("skipped", skipped.GetProperty("result").GetString());
        Assert.Null(ThreadOf(ChannelIds.Instagram, igsid));
        Assert.False(InDb(db => db.ChannelAccounts.Any(a => a.ExternalId == social.ExternalId && a.Channel == ChannelIds.Instagram)));

        // the SAME outlet, but this Zernio account was connected as the inbox: its DMs are native
        var native = Native(ChannelIds.Instagram, entity);
        var igsid2 = "ig" + U();
        var ok = await Hook("message.received", Received(native, "instagram", "c-" + U(), "hello", new { id = igsid2 }, igsid2));
        Assert.Equal("ok", ok.GetProperty("result").GetString());
        Assert.Equal(native.Id, ThreadOf(ChannelIds.Instagram, igsid2)!.AccountId);
    }

    // ---- connect flows --------------------------------------------------------------

    [Fact]
    public async Task Connect_flows_end_in_native_inboxes_for_whatsapp_telegram_and_sms()
    {
        _f.Zernio.Profiles.Add(new { _id = "prof-default", name = "Default", isDefault = true, externalId = (string?)null });
        var c = await Admin();

        // WhatsApp: the hosted signup, plus the own-credentials alternative
        var hosted = await (await c.PostAsJsonAsync("/api/social/connect", new { platform = "whatsapp", purpose = "inbox" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hosted", hosted.GetProperty("flow").GetString());
        Assert.StartsWith("https://auth.example/whatsapp", hosted.GetProperty("url").GetString());
        Assert.Contains("/channels?zernio=connected", _f.Zernio.Of("ConnectUrl").Last()["redirect_url"]!.ToString());
        var alt = hosted.GetProperty("alternatives")[0];
        Assert.Equal("/api/social/connect/whatsapp", alt.GetProperty("submit_url").GetString());
        Assert.Contains(alt.GetProperty("fields").EnumerateArray(), f => f.GetProperty("key").GetString() == "access_token"
                                                                       && f.GetProperty("secret").GetBoolean());
        var creds = await c.PostAsJsonAsync("/api/social/connect/whatsapp", new
        {
            platform = "whatsapp", purpose = "inbox",
            credentials = new { access_token = "EAAB-secret", waba_id = "123", phone_number_id = "987" },
        });
        Assert.Equal(HttpStatusCode.OK, creds.StatusCode);
        var done = await creds.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(done.GetProperty("connected").GetBoolean());
        var waRow = done.GetProperty("account");
        Assert.Equal("whatsapp", waRow.GetProperty("channel").GetString());
        Assert.Equal("zernio", waRow.GetProperty("provider").GetString());
        Assert.Equal("managed", waRow.GetProperty("method").GetString());
        Assert.Equal("+60 12-345 6789", waRow.GetProperty("address").GetString());
        var waId = int.Parse(waRow.GetProperty("id").GetString()!);
        var wa = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == waId));
        Assert.Equal(Providers.Zernio, wa.Provider);
        Assert.False(InDb(db => db.ChannelAccounts.Any(a => a.ExternalId == wa.ExternalId && a.Channel == ChannelIds.Social)));
        Assert.Equal("EAAB-secret", _f.Zernio.Of("WhatsAppCredentials").Single()["accessToken"]);

        // Telegram: an access code + steps, then the poll connects it
        var code = await (await c.PostAsJsonAsync("/api/social/connect", new { platform = "telegram", purpose = "inbox" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", code.GetProperty("status").GetString());
        Assert.Equal("ZRN-ABC123", code.GetProperty("code").GetString());
        Assert.True(code.GetProperty("steps").GetArrayLength() >= 2);
        var statusUrl = code.GetProperty("status_url").GetString()!;
        var pending = await c.GetFromJsonAsync<JsonElement>(statusUrl);
        Assert.False(pending.GetProperty("connected").GetBoolean());
        var tgId = "ztg-" + U();
        _f.Zernio.TelegramStatus = new
        {
            status = "connected", chatId = "-1001234", chatTitle = "Kedai group", chatType = "group",
            account = new { _id = tgId, platform = "telegram", username = "kedaigroup", displayName = "Kedai group" },
        };
        var connected = await c.GetFromJsonAsync<JsonElement>(statusUrl);
        Assert.True(connected.GetProperty("connected").GetBoolean());
        Assert.Equal("telegram", connected.GetProperty("account").GetProperty("channel").GetString());
        Assert.True(InDb(db => db.ChannelAccounts.Any(a => a.ExternalId == tgId && a.Channel == ChannelIds.Telegram && a.Provider == Providers.Zernio)));
        // …and its social twin (channel posts) shares the Zernio id
        Assert.True(InDb(db => db.ChannelAccounts.Any(a => a.ExternalId == tgId && a.Channel == ChannelIds.Social)));

        // SMS: pick a number, SMS goes on, the SMS account is the inbox
        _f.Zernio.PhoneNumbers.Add(new { _id = "num1", phoneNumber = "+60123000111", country = "MY", status = "active", smsCapable = true });
        var pick = await (await c.PostAsJsonAsync("/api/social/connect", new { platform = "sms", purpose = "inbox" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/api/social/connect/sms", pick.GetProperty("submit_url").GetString());
        var field = pick.GetProperty("fields")[0];
        Assert.Equal("phone_number_id", field.GetProperty("key").GetString());
        Assert.Equal("num1", field.GetProperty("options")[0].GetProperty("value").GetString());
        var sms = await (await c.PostAsJsonAsync("/api/social/connect/sms", new { platform = "sms", credentials = new { phone_number_id = "num1" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(sms.GetProperty("connected").GetBoolean());
        var smsRow = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.ExternalId == "zsms-num1"));
        Assert.Equal(ChannelIds.Sms, smsRow.Channel);
        Assert.Equal("+60123000111", ZernioNative.Address(smsRow));

        // SMS outbound-first: Zernio's SMS send from the inbox number, its conversation remembered
        var to = Digits();
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var t = new Thread { Channel = ChannelIds.Sms, Key = to, Name = "x", AccountId = smsRow.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Threads.Add(t);
            db.SaveChanges();
            var msg = scope.ServiceProvider.GetRequiredService<OutboundSender>().Send(t, "Your table is ready", MessageSource.Agent);
            Assert.Equal("sent", msg.Status);
        }
        var smsCall = Assert.Single(_f.Zernio.Of("SendSms"));
        Assert.Equal("+60123000111", smsCall["from"]);
        Assert.Equal("+" + to, smsCall["to"]);
        Assert.NotNull(InDb(db => ZernioNative.Conversation(db, smsRow.Id, to)));
    }

    [Fact]
    public async Task Managed_inboxes_are_listed_with_their_method_and_refuse_credential_edits_and_local_deletes()
    {
        var wa = Native(ChannelIds.WhatsApp, address: "+60 11-111 1111");
        var tg = Native(ChannelIds.Telegram);
        var c = await Admin();

        var was = await c.GetFromJsonAsync<JsonElement>("/api/whatsapp/accounts");
        var waRow = was.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("id").GetString() == wa.Id.ToString());
        Assert.Equal("zernio", waRow.GetProperty("provider").GetString());
        Assert.Equal("managed", waRow.GetProperty("method").GetString());
        Assert.Equal("Managed connection", waRow.GetProperty("method_label").GetString());
        Assert.Equal(wa.ExternalId, waRow.GetProperty("managed").GetProperty("zernio_account_id").GetString());
        Assert.True(waRow.GetProperty("official").GetBoolean());

        var msgs = await c.GetFromJsonAsync<JsonElement>("/api/messaging/accounts");
        var tgRow = msgs.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("id").GetString() == tg.Id.ToString());
        Assert.Equal("zernio", tgRow.GetProperty("provider").GetString());
        Assert.Equal("managed", tgRow.GetProperty("method").GetString());
        Assert.Equal(tg.ExternalId, tgRow.GetProperty("zernio_account_id").GetString());
        Assert.Equal(Health.Healthy, tgRow.GetProperty("health_status").GetString());
        Assert.DoesNotContain("Zernio", tgRow.GetProperty("provider_label").GetString());

        // name + who answers it: yes; credentials / a local rebind / a local delete: no
        Assert.Equal(HttpStatusCode.OK, (await c.PatchAsJsonAsync($"/api/messaging/accounts/{tg.Id}", new { name = "Kedai bot", ai_default = "on" })).StatusCode);
        Assert.Equal("Kedai bot", InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == tg.Id).Name));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PatchAsJsonAsync($"/api/messaging/accounts/{tg.Id}", new { access_token = "123:abc" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/messaging/accounts/{tg.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/whatsapp/accounts/{wa.Id}", new { access_token = "EAAB" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/whatsapp/accounts/{wa.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/whatsapp/accounts", new { provider = "zernio", name = "x" })).StatusCode);

        // disconnect goes through the social path: Zernio is told, the inbox goes
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/social/accounts/{tg.ExternalId}?confirm=true")).StatusCode);
        Assert.Equal(tg.ExternalId, _f.Zernio.Of("DeleteAccount").Single()["accountId"]);
        Assert.False(InDb(db => db.ChannelAccounts.Any(a => a.Id == tg.Id)));

        // not enabled: the code stays, the words never say Zernio
        _f.Zernio.Configured = false;
        var off = await c.PostAsJsonAsync("/api/social/connect", new { platform = "whatsapp", purpose = "inbox" });
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        var body = await off.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("zernio_not_configured", body.GetProperty("code").GetString());
        Assert.DoesNotContain("Zernio", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_synced_facebook_account_gets_a_messenger_inbox_at_its_selected_pages_outlet()
    {
        var c = await Admin();
        var (e1, slug1) = InDb(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var e = new Entity { Name = "FB Outlet " + U(), Slug = "fb-outlet-" + U(), TenantId = tenant.Id };
            db.Entities.Add(e);
            db.SaveChanges();
            return (e.Id, e.Slug);
        });
        _f.Zernio.Profiles.Add(new { _id = "prof-default", name = "Default", isDefault = true, externalId = (string?)null });
        var fb = "zfb-" + U();
        _f.Zernio.Accounts.Add(new
        {
            _id = fb, platform = "facebook", username = "kopi.group", displayName = "Kopi Group", isActive = true,
            profileId = new { _id = "prof-default", name = "Default" },
        });
        _f.Zernio.FacebookPages[fb] = (new List<object>
        {
            new { id = "P1", name = "Kopi KL", username = "kopikl", category = "Cafe" },
            new { id = "P2", name = "Kopi PG", username = "kopipg", category = "Cafe" },
        }, "P1");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/social/accounts?refresh=1")).StatusCode);
        var native = InDb(db => db.ChannelAccounts.AsNoTracking().SingleOrDefault(a => a.ExternalId == fb && a.Channel == ChannelIds.Facebook));
        if (native == null)
            throw new Xunit.Sdk.XunitException("no native messenger row");
        Assert.Equal(Providers.Zernio, native.Provider);
        Assert.Null(native.EntityId);
        Assert.Equal("Kopi KL", ZernioNative.Address(native));

        // bind the selected Page to outlet 1: the Messenger inbox and the account's comments follow it
        Assert.Equal(HttpStatusCode.OK, (await c.PatchAsJsonAsync($"/api/social/accounts/{fb}/pages/P1", new { entity = slug1 })).StatusCode);
        Assert.Equal(e1, InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == native.Id).EntityId));
        var social = InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.ExternalId == fb && a.Channel == ChannelIds.Social));
        var cid = "c-" + U();
        await Hook("comment.received", new
        {
            id = Guid.NewGuid().ToString(), @event = "comment.received",
            comment = new { id = cid, platformPostId = "fbp1", platform = "facebook", text = "Open?", author = new { id = "u1", name = "Lim" }, createdAt = DateTime.UtcNow.ToString("o") },
            post = new { id = "zp", platformPostId = "fbp1" },
            account = new { id = fb, accountId = fb, platform = "facebook" },
        });
        Assert.Equal(e1, ThreadOf(ChannelIds.Social, $"comment:{fb}:{cid}")!.EntityId);
        Assert.Null(social.EntityId); // the account itself (and its posts' level) stays where it was

        // selecting the other (unbound) Page moves the inbox back to the account's level
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/social/accounts/{fb}/pages/P2/select", null)).StatusCode);
        Assert.Null(InDb(db => db.ChannelAccounts.AsNoTracking().Single(a => a.Id == native.Id).EntityId));
        Assert.Null(ThreadOf(ChannelIds.Social, $"comment:{fb}:{cid}")!.EntityId);
    }

    [Fact]
    public async Task The_ai_task_catalog_offers_the_social_media_app_with_its_opt_ins()
    {
        var c = await Admin();
        var cat = (await c.GetFromJsonAsync<JsonElement>("/api/ai/tasks")).GetProperty("catalog").GetProperty("builtins");
        var schedule = cat.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "social_schedule_post");
        Assert.Equal("Social media", schedule.GetProperty("app").GetString());
        Assert.Equal("apps", schedule.GetProperty("group").GetString());
        Assert.True(schedule.GetProperty("connected").GetBoolean());
        Assert.True(schedule.GetProperty("write").GetBoolean());
        Assert.Equal("allow_publish", schedule.GetProperty("settings")[0].GetProperty("key").GetString());
        Assert.False(cat.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "social_list_accounts").GetProperty("write").GetBoolean());
        Assert.True(cat.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "social_hide_comment").GetProperty("comment_only").GetBoolean());
        foreach (var t in cat.EnumerateArray().Where(t => t.GetProperty("id").GetString()!.StartsWith("social_")))
        {
            Assert.DoesNotContain("Zernio", t.GetProperty("label").GetString());
            Assert.DoesNotContain("Zernio", t.GetProperty("description").GetString());
        }

        // a task saves the opt-in as "true"; anything else turns it off
        var made = await c.PostAsJsonAsync("/api/ai/tasks", new
        {
            name = "Auto post " + U(), instructions = "Post promos.", tools = new[] { "social_schedule_post" },
            config = new Dictionary<string, object> { ["allow_publish"] = true },
        });
        Assert.True(made.IsSuccessStatusCode);
        Assert.Equal("true", (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("config").GetProperty("allow_publish").GetString());

        _f.Zernio.Configured = false;
        cat = (await c.GetFromJsonAsync<JsonElement>("/api/ai/tasks")).GetProperty("catalog").GetProperty("builtins");
        Assert.False(cat.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "social_schedule_post").GetProperty("connected").GetBoolean());
    }

    [Fact]
    public void Native_keys_follow_each_channels_own_rules()
    {
        JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
        Assert.Equal("60123456789", ZernioNative.Key(ChannelIds.WhatsApp,
            J(new { sender = new { id = "60123456789", phoneNumber = "+60123456789" } }), J(new { platformConversationId = "x" })));
        // a WhatsApp username user without a phone: the BSUID keys the chat
        Assert.Equal("MY.123abc", ZernioNative.Key(ChannelIds.WhatsApp,
            J(new { sender = new { id = "MY.123abc", businessScopedUserId = "MY.123abc" } }), J(new { })));
        Assert.Equal("-100777", ZernioNative.Key(ChannelIds.Telegram, J(new { sender = new { id = "5" } }), J(new { platformConversationId = "-100777" })));
        Assert.Equal("psid1", ZernioNative.Key(ChannelIds.Facebook, J(new { sender = new { id = "psid1" } }), J(new { platformConversationId = "psid1" })));
        Assert.Equal("60123000111", ZernioNative.Key(ChannelIds.Sms, J(new { sender = new { id = "+60123000111" } }), J(new { })));
        Assert.Equal(new[] { "a", "b", "c" }, ZernioNativeTransport.FlatParams(J(new object[]
        {
            new { type = "body", parameters = new[] { new { type = "text", text = "b" } } },
            new { type = "header", parameters = new[] { new { type = "text", text = "a" } } },
            new { type = "button", sub_type = "url", index = "0", parameters = new[] { new { type = "text", text = "c" } } },
        })).ToArray());
    }
}
