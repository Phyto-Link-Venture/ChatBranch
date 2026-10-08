using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>Live webhook intake: Meta verify + signed inbound, Telegram inbound.</summary>
public class WebhooksTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public WebhooksTests(ApiFactory factory) => _factory = factory;

    /// <summary>The Meta app secret of the configured account below. Every post is
    /// signed with it: once a live account exists the webhook refuses unsigned posts,
    /// and xUnit does not promise test order inside a class.</summary>
    private const string AppSecret = "wh-test-app-secret";

    private static StringContent Signed(string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(AppSecret));
        content.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
        return content;
    }

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Meta_verify_echoes_challenge_with_the_right_token()
    {
        var su = await Authed("superadmin", "super123");
        await su.PutAsJsonAsync("/api/settings", new { key = "whatsapp_verify_token", value = "verify-me" });

        var c = _factory.CreateClient();
        var ok = await c.GetAsync("/api/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=verify-me&hub.challenge=42");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("42", await ok.Content.ReadAsStringAsync());

        var bad = await c.GetAsync("/api/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=42");
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
    }

    [Fact]
    public async Task Meta_inbound_stores_message_links_contact_and_ai_replies()
    {
        var admin = await Authed("admin", "admin123");
        // a contact whose phone matches the inbound sender → coverage gate passes
        await admin.PostAsJsonAsync("/api/contacts", new { name = "Nadia", phone = "60129998887" });

        var payload = new
        {
            entry = new[]
            {
                new
                {
                    changes = new[]
                    {
                        new
                        {
                            value = new
                            {
                                metadata = new { phone_number_id = "PN-1" },
                                contacts = new[] { new { profile = new { name = "Nadia" }, wa_id = "60129998887" } },
                                messages = new[]
                                {
                                    new { from = "60129998887", id = "wamid.ABC", timestamp = "1700000000",
                                          type = "text", text = new { body = "hello?" } },
                                },
                            },
                        },
                    },
                },
            },
        };

        var c = _factory.CreateClient();
        var res = await c.PostAsync("/api/whatsapp/webhook", Signed(JsonSerializer.Serialize(payload)));
        res.EnsureSuccessStatusCode();

        // thread now has the inbound + a sandbox AI reply
        var msgs = await admin.GetFromJsonAsync<JsonElement>(
            "/api/whatsapp/threads/whatsapp/60129998887/messages");
        var list = msgs.GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains(list, m => m.GetProperty("direction").GetString() == "in"
                                   && m.GetProperty("text").GetString() == "hello?");
        Assert.Contains(list, m => m.GetProperty("source").GetString() == "ai"); // AI auto-reply fired
    }

    /// <summary>
    /// With a CONFIGURED account and ai_reply_mode=queued (the default), the
    /// reply must be enqueued rather than sent inline — that queue job is what
    /// the account's Send Pacing gap and quiet hours act on. Before this the
    /// rule was stored and never read, so pacing paced nothing.
    /// </summary>
    [Fact]
    public async Task Configured_account_queues_the_ai_reply_instead_of_sending_inline()
    {
        var admin = await Authed("admin", "admin123");
        await admin.PostAsJsonAsync("/api/contacts", new { name = "Paced", phone = "60129990111" });
        // a real (configured) Cloud account carries this number
        await admin.PostAsJsonAsync("/api/whatsapp/accounts", new
        {
            name = "Paced Number", phone_number_id = "PN-PACED", access_token = "tok-live",
            app_secret = AppSecret,
        });

        var payload = JsonSerializer.Serialize(new
        {
            entry = new[] { new { changes = new[] { new { value = new
            {
                metadata = new { phone_number_id = "PN-PACED" },
                messages = new[] { new { from = "60129990111", id = "wamid.PACED", timestamp = "1700000000",
                    type = "text", text = new { body = "are you open?" } } },
            } } } } },
        });
        var c = _factory.CreateClient();
        // a live account now exists: an unsigned (or wrongly signed) post is refused
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/whatsapp/webhook",
            new StringContent(payload, Encoding.UTF8, "application/json"))).StatusCode);
        (await c.PostAsync("/api/whatsapp/webhook", Signed(payload))).EnsureSuccessStatusCode();

        // nothing sent inline …
        var msgs = await admin.GetFromJsonAsync<JsonElement>(
            "/api/whatsapp/threads/whatsapp/60129990111/messages");
        Assert.DoesNotContain(msgs.GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("source").GetString() == "ai");

        // … an ai_reply job is waiting for the pacing gap instead
        var queue = await admin.GetFromJsonAsync<JsonElement>("/api/flows/queue");
        Assert.Contains(queue.GetProperty("jobs").EnumerateArray(),
            j => j.GetProperty("kind").GetString() == "ai_reply"
                 && j.GetProperty("toAddress").GetString() == "60129990111");
    }

    [Fact]
    public async Task Meta_inbound_dedupes_on_wamid()
    {
        var admin = await Authed("admin", "admin123");
        var c = _factory.CreateClient();
        string Body() => JsonSerializer.Serialize(new
        {
            entry = new[] { new { changes = new[] { new { value = new
            {
                metadata = new { phone_number_id = "PN-2" },
                messages = new[] { new { from = "60111000222", id = "wamid.DUP", timestamp = "1700000000",
                    type = "text", text = new { body = "dup" } } },
            } } } } },
        });
        await c.PostAsync("/api/whatsapp/webhook", Signed(Body()));
        await c.PostAsync("/api/whatsapp/webhook", Signed(Body()));

        var msgs = await admin.GetFromJsonAsync<JsonElement>(
            "/api/whatsapp/threads/whatsapp/60111000222/messages");
        var dupCount = msgs.GetProperty("messages").EnumerateArray()
            .Count(m => m.GetProperty("text").GetString() == "dup");
        Assert.Equal(1, dupCount); // wamid dedup collapsed the redelivery
    }

    [Fact]
    public async Task Telegram_inbound_stores_message()
    {
        var admin = await Authed("admin", "admin123");
        // the token in the path must belong to a linked bot (an unknown one is a 404)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatBranch.Data.AppDbContext>();
            if (!db.ChannelAccounts.Any(a => a.Channel == "telegram" && a.AccessToken == "sometoken"))
            {
                db.ChannelAccounts.Add(new ChatBranch.Domain.ChannelAccount
                {
                    Channel = "telegram", Name = "Webhook bot", AccessToken = "sometoken", CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
            }
        }
        var c = _factory.CreateClient();
        var update = JsonSerializer.Serialize(new
        {
            message = new { chat = new { id = 55501 }, from = new { first_name = "Tele" }, text = "hi from tg" },
        });
        var res = await c.PostAsync("/api/messaging/webhook/telegram/sometoken",
            new StringContent(update, Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();

        var msgs = await admin.GetFromJsonAsync<JsonElement>(
            "/api/whatsapp/threads/telegram/55501/messages");
        Assert.Contains(msgs.GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("text").GetString() == "hi from tg");
    }
}
