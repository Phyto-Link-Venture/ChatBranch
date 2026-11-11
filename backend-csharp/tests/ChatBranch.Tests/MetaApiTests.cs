using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>The API host with graph.facebook.com (and Meta's CDN) replaced by <see cref="FakeGraph"/>.</summary>
public class MetaApiFactory : ApiFactory
{
    internal FakeGraph Graph { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var http = new FakeHttpFactory(Graph);
            services.AddSingleton<IWhatsAppCloudClient>(new WhatsAppCloudClient(http));
            services.AddSingleton<IMetaMessagingClient>(new MetaMessagingClient(http));
        });
    }
}

/// <summary>
/// The Meta transports end to end over HTTP: the Messenger/Instagram webhook
/// (verify, REQUIRED signature, routing, dedup, attachments, receipts), WhatsApp
/// Cloud inbound media + delivery statuses, the auth-gated + entity-scoped media
/// route, live templates (list / create / delete / send), live media sends, the
/// Baileys two-phase media push, call recordings and the Meta account tests.
/// Every Cloud webhook post is signed: this class creates live accounts.
/// </summary>
public class MetaApiTests : IClassFixture<MetaApiFactory>
{
    private const string Secret = "meta-api-test-secret";
    private readonly MetaApiFactory _factory;
    private FakeGraph Graph => _factory.Graph;

    public MetaApiTests(MetaApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static StringContent Signed(object payload, string secret = Secret)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        content.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
        return content;
    }

    private T InScope<T>(Func<AppDbContext, T> read)
    {
        using var scope = _factory.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task MediaIdle() =>
        _factory.Services.GetRequiredService<MediaFetcher>().WhenIdleAsync(TimeSpan.FromSeconds(10));

    /// <summary>A live Cloud number carrying the class's app secret.</summary>
    private async Task<string> CloudAccount(HttpClient admin, string pnid, string waba = "")
    {
        var res = await admin.PostAsJsonAsync("/api/whatsapp/accounts", new
        {
            name = $"Cloud {pnid}", phone_number_id = pnid, access_token = $"tok-{pnid}", app_secret = Secret,
            waba_id = waba,
        });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
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

    // ---- Messenger / Instagram webhook ----------------------------------------------------

    [Fact]
    public async Task Messenger_webhook_verifies_requires_a_signature_and_routes_to_the_page()
    {
        var su = await Authed("superadmin", "super123");
        await su.PutAsJsonAsync("/api/settings", new { key = "whatsapp_verify_token", value = "meta-verify" });
        var anon = _factory.CreateClient();
        var ok = await anon.GetAsync("/api/messaging/webhook?hub.mode=subscribe&hub.verify_token=meta-verify&hub.challenge=777");
        Assert.Equal("777", await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await anon.GetAsync("/api/messaging/webhook?hub.mode=subscribe&hub.verify_token=nope&hub.challenge=1")).StatusCode);

        var admin = await Authed("admin", "admin123");
        var created = await admin.PostAsJsonAsync("/api/messaging/accounts", new
        {
            channel = "facebook", name = "Shop Page", external_id = "PAGE-55", access_token = "page-tok",
            app_secret = Secret,
        });
        created.EnsureSuccessStatusCode();
        var account = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("••", account.GetProperty("app_secret_masked").GetString()); // never in clear
        Graph.Json("GET", @"/v23\.0/PSID-55\?fields", new { first_name = "Nora", last_name = "Lee" });
        Graph.Bytes(@"cdn\.fbsbx\.test/pic\.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0x01 }, "image/jpeg");
        // Meta stamps the message itself; recent, so the messaging window is open for the reply below
        var sentAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();

        var payload = new
        {
            @object = "page",
            entry = new[] { new { id = "PAGE-55", time = 1, messaging = new object[]
            {
                new { sender = new { id = "PSID-55" }, recipient = new { id = "PAGE-55" }, timestamp = sentAt,
                      message = new { mid = "mid.55", text = "Do you ship to Penang?", attachments = new[]
                      { new { type = "image", payload = new { url = "https://cdn.fbsbx.test/pic.jpg" } } } } },
            } } },
        };

        // unsigned and wrongly signed posts never create a message
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/messaging/webhook",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync("/api/messaging/webhook", Signed(payload, "not-the-secret"))).StatusCode);
        Assert.False(InScope(db => db.Threads.Any(t => t.Channel == "facebook" && t.Key == "PSID-55")));

        (await anon.PostAsync("/api/messaging/webhook", Signed(payload))).EnsureSuccessStatusCode();
        (await anon.PostAsync("/api/messaging/webhook", Signed(payload))).EnsureSuccessStatusCode(); // redelivery
        await MediaIdle();

        var (thread, msgs) = InScope(db =>
        {
            var t = db.Threads.Include(x => x.Account).Single(x => x.Channel == "facebook" && x.Key == "PSID-55");
            return (t, db.Messages.Where(m => m.ThreadId == t.Id).ToList());
        });
        Assert.Equal("PAGE-55", thread.Account!.ExternalId);   // routed by the entry id
        Assert.Equal("Nora Lee", thread.Name);                 // profile learnt off the ack
        var inbound = Assert.Single(msgs);                     // mid dedup: one row
        Assert.Equal("Do you ship to Penang?", inbound.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(sentAt).UtcDateTime, inbound.CreatedAt);
        Assert.Equal("image", inbound.MediaType);

        // the attachment is served auth-gated from /api/messaging/media/{pk}
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/messaging/media/{inbound.Id}")).StatusCode);
        var file = await admin.GetAsync($"/api/messaging/media/{inbound.Id}");
        file.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0x01 }, await file.Content.ReadAsByteArrayAsync());

        // a reply from the inbox goes out through the Send API with the page token
        Graph.Json("POST", @"/me/messages$", new { recipient_id = "PSID-55", message_id = "mid.out.1" });
        var sent = await admin.PostAsJsonAsync("/api/messaging/threads/facebook/PSID-55/send", new { text = "Yes we do!" });
        sent.EnsureSuccessStatusCode();
        var out1 = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("sent", out1.GetProperty("status").GetString());
        Assert.Contains(Graph.Requests, r => r.Url.EndsWith("/me/messages") && r.Auth == "Bearer page-tok"
                                             && r.Body.Contains("Yes we do!"));

        // … and Messenger's delivery receipt ticks it
        (await anon.PostAsync("/api/messaging/webhook", Signed(new
        {
            @object = "page",
            entry = new[] { new { id = "PAGE-55", messaging = new object[]
            {
                new { sender = new { id = "PSID-55" }, recipient = new { id = "PAGE-55" }, timestamp = 1700000001000,
                      delivery = new { mids = new[] { "mid.out.1" }, watermark = 1700000001000 } },
            } } },
        }))).EnsureSuccessStatusCode();
        Assert.Equal("delivered", InScope(db => db.Messages.Single(m => m.ExternalId == "mid.out.1").Status));
    }

    /// <summary>Someone who is not a contact messages the page / the Instagram account:
    /// an unlinked conversation, listed under "Not in contacts".</summary>
    [Theory]
    [InlineData("facebook", "page", "PAGE-NEW", "PSID-NEWCOMER")]
    [InlineData("instagram", "instagram", "IG-NEW", "IGSID-NEWCOMER")]
    public async Task Messenger_and_Instagram_message_from_a_stranger_lands_as_an_unlinked_conversation(
        string channel, string objectType, string pageId, string senderId)
    {
        var admin = await Authed("admin", "admin123");
        (await admin.PostAsJsonAsync("/api/messaging/accounts", new
        {
            channel, name = $"{channel} newcomers", external_id = pageId, access_token = $"tok-{pageId}", app_secret = Secret,
        })).EnsureSuccessStatusCode();
        Graph.Json("GET", $@"/v23\.0/{senderId}\?fields", new { first_name = "New", last_name = "Person", name = "New Person" });
        var payload = new
        {
            @object = objectType,
            entry = new[] { new { id = pageId, time = 1, messaging = new object[]
            {
                new { sender = new { id = senderId }, recipient = new { id = pageId },
                      timestamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
                      message = new { mid = $"mid.{senderId}", text = "Hi, saw your post" } },
            } } },
        };
        (await _factory.CreateClient().PostAsync("/api/messaging/webhook", Signed(payload))).EnsureSuccessStatusCode();
        await MediaIdle();
        await UnknownSenderAsserts.AssertListedAsNotInContactsAsync(_factory, admin, channel, senderId, "Hi, saw your post");
    }

    [Fact]
    public async Task Messenger_account_test_probes_the_token_and_moves_health()
    {
        var admin = await Authed("admin", "admin123");
        var created = await admin.PostAsJsonAsync("/api/messaging/accounts", new
        {
            channel = "instagram", name = "IG shop", external_id = "IG-1", access_token = "ig-good",
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Graph.On("GET", @"/v23\.0/me\?fields=id,name", req => req.Headers.Authorization?.Parameter == "ig-good"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = "IG-1", name = "IG Shop" }) }
            : new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { error = new { message = "Invalid OAuth access token", code = 190 } }),
            });

        var ok = await admin.PostAsync($"/api/messaging/accounts/{id}/test", null);
        ok.EnsureSuccessStatusCode();
        Assert.Equal("healthy", (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("health_status").GetString());

        (await admin.PatchAsJsonAsync($"/api/messaging/accounts/{id}", new { access_token = "ig-revoked" }))
            .EnsureSuccessStatusCode();
        var bad = await admin.PostAsync($"/api/messaging/accounts/{id}/test", null);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("disconnected", InScope(db => db.ChannelAccounts.Single(a => a.ExternalId == "IG-1").HealthStatus));
    }

    // ---- WhatsApp Cloud: inbound media, statuses, the media route ----------------------------

    [Fact]
    public async Task Cloud_inbound_media_is_downloaded_off_the_ack_and_served_in_scope()
    {
        var admin = await Authed("admin", "admin123");
        await CloudAccount(admin, "PN-MEDIA");
        Graph.Json("GET", @"/v23\.0/WAMEDIA-1$", new
        {
            url = "https://lookaside.fbsbx.com/whatsapp_business/attachments/?mid=WAMEDIA-1",
            mime_type = "application/pdf", file_size = 9, id = "WAMEDIA-1",
        });
        Graph.Bytes(@"mid=WAMEDIA-1", Encoding.ASCII.GetBytes("%PDF-1.4!"), "application/pdf");

        var anon = _factory.CreateClient();
        var payload = CloudMessage("PN-MEDIA", new
        {
            from = "60177000001", id = "wamid.MEDIA1", timestamp = "1700000000", type = "document",
            document = new { id = "WAMEDIA-1", mime_type = "application/pdf", filename = "statement.pdf" },
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/whatsapp/webhook",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"))).StatusCode);
        (await anon.PostAsync("/api/whatsapp/webhook", Signed(payload))).EnsureSuccessStatusCode();
        await MediaIdle();

        var msg = InScope(db => db.Messages.Single(m => m.ExternalId == "wamid.MEDIA1"));
        Assert.Equal(("[document]", "document"), (msg.Text, msg.MediaType));
        Assert.StartsWith("60177000001/", msg.MediaPath);
        var thread = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177000001");
        var bubble = thread.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("pk").GetInt32() == msg.Id);
        var url = bubble.GetProperty("media").GetProperty("url").GetString();
        Assert.Equal($"/api/whatsapp/media/{msg.Id}", url);

        var file = await admin.GetAsync(url);
        file.EnsureSuccessStatusCode();
        Assert.Equal("%PDF-1.4!", await file.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        // ranges work (audio seeking uses the same route)
        var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.Range = new RangeHeaderValue(0, 3);
        var part = await admin.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("%PDF", await part.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/whatsapp/media/987654")).StatusCode);
    }

    [Fact]
    public async Task Media_is_hidden_outside_the_callers_entity_scope()
    {
        var (south, northThreadMsg, southThreadMsg) = InScope(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            var north = db.Entities.FirstOrDefault(e => e.Slug == "media-north")
                        ?? new Entity { TenantId = tenant.Id, Name = "Media North", Slug = "media-north", CreatedAt = now, UpdatedAt = now };
            var s = db.Entities.FirstOrDefault(e => e.Slug == "media-south")
                    ?? new Entity { TenantId = tenant.Id, Name = "Media South", Slug = "media-south", CreatedAt = now, UpdatedAt = now };
            if (north.Id == 0) db.Entities.Add(north);
            if (s.Id == 0) db.Entities.Add(s);
            db.SaveChanges();
            var agent = db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent");
            if (!agent.Entities.Any(e => e.Id == north.Id)) agent.Entities.Add(north);
            var store = _factory.Services.GetRequiredService<MediaStore>();
            Message Make(string key, int entityId)
            {
                var t = new Thread { Channel = "whatsapp", Key = key, Name = key, EntityId = entityId, CreatedAt = now, UpdatedAt = now };
                db.Threads.Add(t);
                db.SaveChanges();
                var m = new Message
                {
                    ThreadId = t.Id, Direction = "in", Source = "customer", Text = "[image]", Status = "received",
                    MediaType = "image", CreatedAt = now,
                    MediaPath = store.SaveAsync("whatsapp", key, new byte[] { 7 }, ".png").GetAwaiter().GetResult(),
                };
                db.Messages.Add(m);
                db.SaveChanges();
                return m;
            }
            return (s, Make("60188000001", north.Id), Make("60188000002", s.Id));
        });
        var agentClient = await Authed("agent", "agent123");
        (await agentClient.GetAsync($"/api/whatsapp/media/{northThreadMsg.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await agentClient.GetAsync($"/api/whatsapp/media/{southThreadMsg.Id}")).StatusCode);
        var admin = await Authed("admin", "admin123");
        (await admin.GetAsync($"/api/whatsapp/media/{southThreadMsg.Id}")).EnsureSuccessStatusCode();
        Assert.NotEqual(0, south.Id);
    }

    [Fact]
    public async Task Delivery_statuses_tick_messages_and_131047_flips_the_composer_to_templates()
    {
        var admin = await Authed("admin", "admin123");
        await CloudAccount(admin, "PN-STAT");
        var anon = _factory.CreateClient();
        // the contact wrote in an hour ago (Meta's timestamp), so the window is open
        var oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString();
        (await anon.PostAsync("/api/whatsapp/webhook", Signed(CloudMessage("PN-STAT", new
        {
            from = "60177000002", id = "wamid.IN.ST", timestamp = oneHourAgo, type = "text", text = new { body = "hi" },
        })))).EnsureSuccessStatusCode();
        var before = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177000002");
        Assert.True(before.GetProperty("window_open").GetBoolean());

        Graph.Json("POST", @"/PN-STAT/messages$", new { messages = new[] { new { id = "wamid.OUT.ST" } } });
        (await admin.PostAsJsonAsync("/api/whatsapp/threads/60177000002/send", new { text = "hello back" }))
            .EnsureSuccessStatusCode();

        object Status(string status, int? code = null) => new
        {
            entry = new[] { new { changes = new[] { new { value = new
            {
                metadata = new { phone_number_id = "PN-STAT" },
                statuses = new[]
                {
                    code is int c
                        ? (object)new { id = "wamid.OUT.ST", status, timestamp = "1700000100", recipient_id = "60177000002",
                            errors = new[] { new { code = c, title = "Re-engagement message" } } }
                        : new { id = "wamid.OUT.ST", status, timestamp = "1700000100", recipient_id = "60177000002" },
                },
            } } } } },
        };
        (await anon.PostAsync("/api/whatsapp/webhook", Signed(Status("delivered")))).EnsureSuccessStatusCode();
        Assert.Equal("delivered", InScope(db => db.Messages.Single(m => m.ExternalId == "wamid.OUT.ST").Status));

        (await anon.PostAsync("/api/whatsapp/webhook", Signed(Status("failed", 131047)))).EnsureSuccessStatusCode();
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177000002");
        Assert.False(after.GetProperty("window_open").GetBoolean());
        var failed = after.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "wamid.OUT.ST");
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.StartsWith("131047", failed.GetProperty("error").GetString());
        // per-recipient: the number stays healthy (inbound healed it)
        Assert.Equal("healthy", InScope(db => db.ChannelAccounts.Single(a => a.ExternalId == "PN-STAT").HealthStatus));
        // free text is refused now — use a template
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/whatsapp/threads/60177000002/send",
            new { text = "again" })).StatusCode);
    }

    // ---- templates ---------------------------------------------------------------------------

    [Fact]
    public async Task Live_templates_list_from_meta_create_delete_and_send_as_templates()
    {
        var admin = await Authed("admin", "admin123");
        var accountId = await CloudAccount(admin, "PN-TPL", "WABA-TPL");
        Graph.Json("GET", @"/WABA-TPL/message_templates\?fields", new
        {
            data = new[] { new { name = "welcome_back", language = "en", status = "APPROVED", category = "MARKETING",
                id = "TPL-1", components = new object[] { new { type = "BODY", text = "Welcome back {{1}}!" } } } },
            paging = new { next = "https://graph.facebook.com/v23.0/WABA-TPL/message_templates?after=P2" },
        });
        Graph.Json("GET", @"WABA-TPL/message_templates\?after=P2", new
        {
            data = new[] { new { name = "invoice_due", language = "en", status = "APPROVED", category = "UTILITY",
                id = "TPL-2", components = new object[] { new { type = "BODY", text = "Invoice {{1}} is due {{2}}." } } } },
        });

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/whatsapp/templates?account_id={accountId}");
        Assert.False(list.GetProperty("sandbox").GetBoolean());
        var names = list.GetProperty("templates").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("welcome_back", names);
        Assert.Contains("invoice_due", names);          // page two
        Assert.DoesNotContain("order_confirmation", names); // demo set is sandbox-only
        var live = list.GetProperty("templates").EnumerateArray().First(t => t.GetProperty("name").GetString() == "invoice_due");
        Assert.True(live.GetProperty("custom").GetBoolean()); // editable through Meta
        Assert.Equal(2, live.GetProperty("variables").GetInt32());

        // create → POST /{waba}/message_templates (Meta review)
        Graph.Json("POST", @"/WABA-TPL/message_templates$", new { id = "TPL-3", status = "PENDING", category = "UTILITY" });
        var created = await admin.PostAsJsonAsync($"/api/whatsapp/templates?account_id={accountId}", new
        {
            name = "slot_offer", language = "en", category = "UTILITY", body = "Hi {{1}}, a slot opened on {{2}}.",
            example = new[] { "Ana", "Monday" }, buttons = new[] { new { type = "QUICK_REPLY", text = "Book it" } },
        });
        created.EnsureSuccessStatusCode();
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(createdBody.GetProperty("sandbox").GetBoolean());
        Assert.Equal("PENDING", createdBody.GetProperty("template").GetProperty("status").GetString());
        var createReq = JsonSerializer.Deserialize<JsonElement>(
            Graph.Requests.Last(r => r.Method == "POST" && r.Url.EndsWith("/WABA-TPL/message_templates")).Body);
        Assert.Equal("slot_offer", createReq.GetProperty("name").GetString());
        var bodyComp = createReq.GetProperty("components")[0];
        Assert.Equal("BODY", bodyComp.GetProperty("type").GetString());
        Assert.Equal("Monday", bodyComp.GetProperty("example").GetProperty("body_text")[0][1].GetString());

        // delete → DELETE /{waba}/message_templates?name=…
        Graph.Json("DELETE", @"/WABA-TPL/message_templates\?name=welcome_back", new { success = true });
        (await admin.DeleteAsync($"/api/whatsapp/templates/welcome_back?account_id={accountId}")).EnsureSuccessStatusCode();
        Assert.Contains(Graph.Requests, r => r.Method == "DELETE" && r.Url.Contains("name=welcome_back"));
        // Meta's refusal reaches the operator
        Graph.Error("DELETE", @"name=missing_one", 100, "Template not found");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.DeleteAsync($"/api/whatsapp/templates/missing_one?account_id={accountId}")).StatusCode);

        // send → a REAL template message; the inbox keeps the rendered body
        (await admin.PostAsJsonAsync("/api/whatsapp/threads", new { phone = "60177000003", account_id = accountId }))
            .EnsureSuccessStatusCode();
        Graph.Json("POST", @"/PN-TPL/messages$", new { messages = new[] { new { id = "wamid.TPL.1" } } });
        var sent = await admin.PostAsJsonAsync("/api/whatsapp/threads/60177000003/send-template", new
        {
            name = "invoice_due", language = "en", variables = new[] { "INV-9", "Friday" },
        });
        sent.EnsureSuccessStatusCode();
        var message = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("Invoice INV-9 is due Friday.", message.GetProperty("text").GetString());
        Assert.Equal("sent", message.GetProperty("status").GetString());
        var tplReq = JsonSerializer.Deserialize<JsonElement>(
            Graph.Requests.Last(r => r.Url.EndsWith("/PN-TPL/messages")).Body);
        Assert.Equal("template", tplReq.GetProperty("type").GetString());
        Assert.Equal("invoice_due", tplReq.GetProperty("template").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Live_media_send_uploads_to_meta()
    {
        var admin = await Authed("admin", "admin123");
        var accountId = await CloudAccount(admin, "PN-UP");
        var anon = _factory.CreateClient();
        (await anon.PostAsync("/api/whatsapp/webhook", Signed(CloudMessage("PN-UP", new
        {
            from = "60177000004", id = "wamid.IN.UP", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            type = "text", text = new { body = "send me the menu" },
        })))).EnsureSuccessStatusCode();
        Graph.Json("POST", @"/PN-UP/media$", new { id = "UPLOADED-1" });
        Graph.Json("POST", @"/PN-UP/messages$", new { messages = new[] { new { id = "wamid.UP.1" } } });

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "menu.png");
        form.Add(new StringContent("Today's menu"), "caption");
        var res = await admin.PostAsync("/api/whatsapp/threads/60177000004/send-media", form);
        res.EnsureSuccessStatusCode();
        var msg = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("sent", msg.GetProperty("status").GetString());
        Assert.Equal("image", msg.GetProperty("media").GetProperty("type").GetString());
        var send = JsonSerializer.Deserialize<JsonElement>(Graph.Requests.Last(r => r.Url.EndsWith("/PN-UP/messages")).Body);
        Assert.Equal("UPLOADED-1", send.GetProperty("image").GetProperty("id").GetString());
        Assert.Equal("Today's menu", send.GetProperty("image").GetProperty("caption").GetString());
        // the stored copy is what the inbox renders
        var served = await admin.GetAsync(msg.GetProperty("media").GetProperty("url").GetString());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, await served.Content.ReadAsByteArrayAsync());
        _ = accountId;
    }

    // ---- Baileys two-phase media ----------------------------------------------------------------

    [Fact]
    public async Task Baileys_media_push_attaches_the_file_to_the_placeholder_message()
    {
        var anon = _factory.CreateClient();
        // the push must name a PAIRED number: an unknown account_id is a recorded drop
        // (Channels/BaileysInboxRouting — it used to be filed account-less)
        InScope(db =>
        {
            if (!db.ChannelAccounts.Any(a => a.Provider == "baileys" && a.ExternalId == "gw-none"))
            {
                db.ChannelAccounts.Add(new ChatBranch.Domain.ChannelAccount
                {
                    Channel = "whatsapp", Provider = "baileys", Name = "gw-none", ExternalId = "gw-none",
                    HealthStatus = "healthy", CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
            }
            return 0;
        });
        var inbound = new StringContent(JsonSerializer.Serialize(new
        {
            account_id = "gw-none", wa_id = "60199000001", name = "Gw", text = "[image]", msg_id = "BAE5MSG1",
        }), Encoding.UTF8, "application/json");
        inbound.Headers.Add("X-Baileys-Secret", "baileys-local-dev");
        (await anon.PostAsync("/api/whatsapp/baileys/inbound", inbound)).EnsureSuccessStatusCode();

        MultipartFormDataContent Form()
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent("gw-none"), "account_id" },
                { new StringContent("60199000001"), "wa_id" },
                { new StringContent("BAE5MSG1"), "msg_id" },
                { new StringContent("image"), "media_type" },
            };
            var file = new ByteArrayContent(new byte[] { 1, 2, 3 });
            file.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
            form.Add(file, "file", "BAE5MSG1.webp");
            return form;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/whatsapp/baileys/media", Form())).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/whatsapp/baileys/media") { Content = Form() };
        req.Headers.Add("X-Baileys-Secret", "baileys-local-dev");
        var res = await anon.SendAsync(req);
        res.EnsureSuccessStatusCode();
        Assert.True((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetBoolean());

        var msg = InScope(db => db.Messages.Single(m => m.ExternalId == "BAE5MSG1"));
        Assert.Equal("image", msg.MediaType);
        Assert.EndsWith(".webp", msg.MediaPath);
        var admin = await Authed("admin", "admin123");
        Assert.Equal(new byte[] { 1, 2, 3 }, await (await admin.GetAsync($"/api/whatsapp/media/{msg.Id}"))
            .Content.ReadAsByteArrayAsync());
    }

    // ---- call recordings ------------------------------------------------------------------------

    [Fact]
    public async Task Call_recordings_list_stream_with_ranges_and_refuse_traversal()
    {
        var admin = await Authed("admin", "admin123");
        (await admin.PostAsJsonAsync("/api/whatsapp/threads", new { phone = "60166000001" })).EnsureSuccessStatusCode();
        var recs = _factory.Services.GetRequiredService<CallRecordings>();
        Directory.CreateDirectory(recs.GatewayDir);
        var wav = new byte[64];
        for (var i = 0; i < wav.Length; i++) wav[i] = (byte)i;
        File.WriteAllBytes(Path.Combine(recs.GatewayDir, "60166000001-1700000000123.wav"), wav);
        File.WriteAllText(Path.Combine(recs.GatewayDir, "60166000001-1700000000123.json"), JsonSerializer.Serialize(new
        {
            room = "rm-x", final = true,
            turns = new[] { new { role = "user", text = "Hello?", ts = 1700000001.5 } },
            meta = new { initiated_by = "admin", ended_by = "contact", account = "Gw" },
        }));

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60166000001/call/recordings");
        var entry = list.GetProperty("recordings").EnumerateArray().Single();
        Assert.Equal("/api/whatsapp/calls/recording/60166000001-1700000000123.wav", entry.GetProperty("url").GetString());
        Assert.Equal("Hello?", entry.GetProperty("transcript")[0].GetProperty("text").GetString());
        Assert.Equal("contact", entry.GetProperty("meta").GetProperty("ended_by").GetString());

        var url = entry.GetProperty("url").GetString()!;
        var full = await admin.GetAsync(url);
        Assert.Equal("audio/wav", full.Content.Headers.ContentType!.MediaType);
        Assert.Equal(64, (await full.Content.ReadAsByteArrayAsync()).Length);
        var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.Range = new RangeHeaderValue(10, 19);
        var part = await admin.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(wav[10..20], await part.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/whatsapp/calls/recording/..%2F..%2Fcalls.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/whatsapp/calls/recording/nope.wav")).StatusCode);
    }

    [Fact]
    public async Task Ended_call_event_writes_the_paired_transcript()
    {
        var admin = await Authed("admin", "admin123");
        (await admin.PostAsJsonAsync("/api/whatsapp/threads", new { phone = "60166000002" })).EnsureSuccessStatusCode();
        var voice = _factory.Services.GetRequiredService<ChatBranch.Data.Sidecars.VoiceAgentBridge>();
        InScope(db =>
        {
            var t = db.Threads.Single(x => x.Channel == "whatsapp" && x.Key == "60166000002");
            t.CallRoom = "room-ended-1";
            t.CallStatus = "in_call";
            t.CallId = "call-1";
            return db.SaveChanges();
        });
        Directory.CreateDirectory(Path.Combine(voice.DataDir, "live"));
        File.WriteAllText(Path.Combine(voice.DataDir, "live", "room-ended-1.json"),
            """{"turns":[{"role":"assistant","text":"Hi, this is the clinic.","ts":1700000002}],"done":false}""");
        var recs = _factory.Services.GetRequiredService<CallRecordings>();
        Directory.CreateDirectory(recs.GatewayDir);
        File.WriteAllBytes(Path.Combine(recs.GatewayDir, "60166000002-1700000000999.wav"), new byte[] { 1 });

        var ev = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["account_id"] = "gw", ["event"] = "ended", ["room"] = "room-ended-1", ["wa_id"] = "60166000002",
            ["reason"] = "hangup", ["recording"] = "60166000002-1700000000999.wav",
            ["rec_started_at"] = 1700000000999,
        }), Encoding.UTF8, "application/json");
        ev.Headers.Add("X-Baileys-Secret", "baileys-local-dev");
        (await _factory.CreateClient().PostAsync("/api/whatsapp/baileys/call-event", ev)).EnsureSuccessStatusCode();

        var json = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(Path.Combine(recs.GatewayDir, "60166000002-1700000000999.json")));
        Assert.Equal("room-ended-1", json.GetProperty("room").GetString());
        Assert.Equal("Hi, this is the clinic.", json.GetProperty("turns")[0].GetProperty("text").GetString());
        Assert.Equal(1700000000999, json.GetProperty("meta").GetProperty("rec_started_at").GetInt64());

        // the agent finishes later (ended_by after its summary): the list backfills it
        File.WriteAllText(Path.Combine(voice.DataDir, "live", "room-ended-1.json"),
            """{"turns":[{"role":"assistant","text":"Hi, this is the clinic.","ts":1700000002},{"role":"user","text":"Bye","ts":1700000009}],"done":true,"ended_by":"agent"}""");
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60166000002/call/recordings");
        var entry = list.GetProperty("recordings").EnumerateArray().Single();
        Assert.Equal(2, entry.GetProperty("transcript").GetArrayLength());
        Assert.Equal("agent", entry.GetProperty("meta").GetProperty("ended_by").GetString());
    }
}
