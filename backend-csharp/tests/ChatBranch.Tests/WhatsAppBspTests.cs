using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Tests;

/// <summary>The API host with YCloud and Twilio (api.ycloud.com, api.twilio.com, content.twilio.com) faked at the HTTP level.</summary>
public sealed class WhatsAppBspFactory : ApiFactory
{
    public FakeProviderHttp Http { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton(new YCloudWhatsAppClient(Http));
            s.AddSingleton(new TwilioWhatsAppClient(Http));
        });
    }
}

/// <summary>
/// WhatsApp through the two Business Solution Providers end to end: the REAL YCloud and
/// Twilio clients (URLs, headers, payloads, error mapping) against a fake HTTP layer, the
/// signed per-account webhooks (ok / bad / missing secret / unknown / stale), inbound text,
/// media off the ack, quotes, reactions, delivery statuses and dedup, sends (text, quoted
/// reply, media, templates, reactions), the template registries, routing (official =
/// template-capable, never Baileys for a template), health probes and the accounts API.
/// </summary>
public class WhatsAppBspTests : IClassFixture<WhatsAppBspFactory>
{
    private const string YSecret = "whsec_ycloud_test";
    private const string TwilioToken = "twilio-auth-token-0001";
    private const string TwilioSid = "AC0123456789abcdef0123456789abcdef";
    private readonly WhatsAppBspFactory _f;

    public WhatsAppBspTests(WhatsAppBspFactory f)
    {
        _f = f;
        _f.Http.Reset();
    }

    // ---- helpers --------------------------------------------------------------------

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

    private static string Digits() => "601" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    private ChannelAccount YCloudAccount(string? secret = YSecret, string health = Health.Unknown, string waba = "WABA-YC") =>
        Add(Providers.YCloud, a =>
        {
            a.AccessToken = "yc-api-key-0001";
            a.ExternalId = Digits();
            a.WabaId = waba;
            a.AppSecret = secret ?? "";
            a.HealthStatus = health;
        });

    private ChannelAccount TwilioAccount(string token = TwilioToken, string health = Health.Unknown) =>
        Add(Providers.Twilio, a =>
        {
            a.AccessToken = token;
            var from = Digits();
            a.ExternalId = from;
            a.HealthStatus = health;
            a.Config = new Dictionary<string, object?>
            {
                ["account_sid"] = TwilioSid,
                ["from_number"] = $"+{from}",
            };
        });

    private ChannelAccount Add(string provider, Action<ChannelAccount> setup)
    {
        var a = new ChannelAccount
        {
            Channel = ChannelIds.WhatsApp, Provider = provider, Name = $"{provider} {Guid.NewGuid():N}"[..18],
            CreatedAt = DateTime.UtcNow,
        };
        setup(a);
        return InDb(db =>
        {
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a;
        });
    }

    private ChannelAccount Account(int id) => InDb(db => db.ChannelAccounts.AsNoTracking().First(a => a.Id == id));

    private Domain.Thread? Thread(string key) =>
        InDb(db => db.Threads.AsNoTracking().FirstOrDefault(t => t.Channel == ChannelIds.WhatsApp && t.Key == key));

    private List<Message> Messages(string key) => InDb(db => db.Messages.AsNoTracking()
        .Where(m => m.Thread!.Channel == ChannelIds.WhatsApp && m.Thread.Key == key).OrderBy(m => m.Id).ToList());

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met in time");
            await Task.Delay(50);
        }
    }

    // ---- YCloud webhook helpers --------------------------------------------------------

    private async Task<HttpResponseMessage> PostYCloud(ChannelAccount a, object evt, string? secret = YSecret,
        bool sign = true, long? at = null, int? accountId = null)
    {
        var body = JsonSerializer.Serialize(evt);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/messaging/webhook/whatsapp-ycloud/{accountId ?? a.Id}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (sign)
            req.Headers.TryAddWithoutValidation(YCloudWhatsAppClient.SignatureHeader,
                YCloudWhatsAppClient.Sign(secret ?? "", at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Encoding.UTF8.GetBytes(body)));
        return await _f.CreateClient().SendAsync(req);
    }

    private static object YInbound(ChannelAccount a, string from, string wamid, object message, DateTime? sent = null,
        string name = "Aisyah") =>
        new
        {
            id = "evt_" + Guid.NewGuid().ToString("N")[..8],
            type = YCloudWebhook.InboundType,
            apiVersion = "v2",
            createTime = DateTime.UtcNow.ToString("o"),
            whatsappInboundMessage = Merge(new Dictionary<string, object?>
            {
                ["id"] = "yc_" + wamid,
                ["wamid"] = wamid,
                ["wabaId"] = a.WabaId,
                ["from"] = $"+{from}",
                ["to"] = $"+{a.ExternalId}",
                ["customerProfile"] = new { name },
                ["sendTime"] = (sent ?? DateTime.UtcNow).ToString("o"),
            }, message),
        };

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> d, object extra)
    {
        foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject())
            d[p.Name] = p.Value;
        return d;
    }

    private static object YStatus(string wamid, string status, int? metaCode = null, string id = "") => new
    {
        id = "evt_s",
        type = YCloudWebhook.StatusType,
        whatsappMessage = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["wamid"] = wamid,
            ["status"] = status,
            ["updateTime"] = DateTime.UtcNow.ToString("o"),
            ["errorCode"] = metaCode?.ToString(),
            ["errorMessage"] = metaCode != null ? "Re-engagement message" : null,
            ["whatsappApiError"] = metaCode != null ? new { code = metaCode, message = "Re-engagement message" } : null,
        },
    };

    // ---- Twilio webhook helpers --------------------------------------------------------

    private async Task<HttpResponseMessage> PostTwilio(ChannelAccount a, Dictionary<string, string> form,
        string? token = null, bool sign = true)
    {
        var url = $"http://localhost/api/messaging/webhook/whatsapp-twilio/{a.Id}";
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/messaging/webhook/whatsapp-twilio/{a.Id}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (sign)
            req.Headers.TryAddWithoutValidation(TwilioSmsTransport.SignatureHeader,
                TwilioSmsTransport.Sign(token ?? a.AccessToken, url, form.ToList()));
        return await _f.CreateClient().SendAsync(req);
    }

    private static Dictionary<string, string> TInbound(ChannelAccount a, string from, string sid, string body,
        Dictionary<string, string>? extra = null)
    {
        var d = new Dictionary<string, string>
        {
            ["MessageSid"] = sid,
            ["SmsMessageSid"] = sid,
            ["AccountSid"] = TwilioSid,
            ["From"] = $"whatsapp:+{from}",
            ["To"] = $"whatsapp:+{a.ExternalId}",
            ["WaId"] = from,
            ["ProfileName"] = "Mei Ling",
            ["Body"] = body,
            ["NumMedia"] = "0",
            ["SmsStatus"] = "received",
        };
        foreach (var (k, v) in extra ?? new()) d[k] = v;
        return d;
    }

    // =====================================================================================
    // YCloud
    // =====================================================================================

    [Fact]
    public async Task YCloud_webhook_refuses_unsigned_stale_unknown_and_unconfigured()
    {
        var noSecret = YCloudAccount(secret: "");
        var from = Digits();
        var evt = YInbound(noSecret, from, "wamid.A" + from, new { type = "text", text = new { body = "hi" } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostYCloud(noSecret, evt)).StatusCode);

        var a = YCloudAccount();
        evt = YInbound(a, from, "wamid.B" + from, new { type = "text", text = new { body = "hi" } });
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostYCloud(a, evt, sign: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostYCloud(a, evt, secret: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostYCloud(a, evt,
            at: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds())).StatusCode); // replayed
        Assert.Equal(HttpStatusCode.NotFound, (await PostYCloud(a, evt, accountId: 999_999)).StatusCode);
        // a Twilio account id is not a YCloud webhook
        var tw = TwilioAccount();
        Assert.Equal(HttpStatusCode.NotFound, (await PostYCloud(a, evt, accountId: tw.Id)).StatusCode);
        Assert.Null(Thread(from));
    }

    [Fact]
    public async Task YCloud_inbound_text_is_stored_with_ycloud_time_deduped_and_heals_the_number()
    {
        var a = YCloudAccount(health: Health.Degraded);
        var from = Digits();
        var sent = DateTime.UtcNow.AddMinutes(-30);
        var evt = YInbound(a, from, "wamid.T" + from, new
        {
            type = "text",
            text = new { body = "Is the shop open today?" },
            context = new { from = $"+{a.ExternalId}", id = "wamid.quoted-earlier" },
        }, sent);
        Assert.Equal(HttpStatusCode.OK, (await PostYCloud(a, evt)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostYCloud(a, evt)).StatusCode); // YCloud retries

        var thread = Thread(from)!;
        Assert.Equal(a.Id, thread.AccountId);
        var msg = Assert.Single(Messages(from));
        Assert.Equal("Is the shop open today?", msg.Text);
        Assert.Equal("wamid.T" + from, msg.ExternalId);
        Assert.Equal("wamid.quoted-earlier", msg.ReplyToExternalId);
        // YCloud's own send time, not arrival time (the 24h window is computed from it)
        Assert.True(Math.Abs((msg.CreatedAt - sent).TotalSeconds) < 2, $"{msg.CreatedAt:o} vs {sent:o}");
        var acct = Account(a.Id);
        Assert.Equal(Health.Healthy, acct.HealthStatus);
        Assert.NotNull(acct.LastInboundAt);
    }

    [Fact]
    public async Task YCloud_message_from_an_unknown_number_lands_as_an_unlinked_conversation()
    {
        var a = YCloudAccount();
        var from = Digits();
        var evt = YInbound(a, from, "wamid.NEW" + from, new { type = "text", text = new { body = "found you online" } });
        Assert.Equal(HttpStatusCode.OK, (await PostYCloud(a, evt)).StatusCode);
        await UnknownSenderAsserts.AssertListedAsNotInContactsAsync(_f, await Authed(), ChannelIds.WhatsApp, from,
            "found you online");
        Assert.Equal(a.Id, Thread(from)!.AccountId);
    }

    [Fact]
    public async Task YCloud_inbound_media_downloads_the_link_off_the_ack_with_the_api_key()
    {
        var a = YCloudAccount();
        var from = Digits();
        var link = "https://api.ycloud.com/v2/whatsapp/media/download/abc123?token=x";
        var evt = YInbound(a, from, "wamid.M" + from, new
        {
            type = "image",
            image = new { id = "meta-media-1", link, mime_type = "image/jpeg", caption = "my receipt" },
        });
        (await PostYCloud(a, evt)).EnsureSuccessStatusCode();
        var msg = Assert.Single(Messages(from));
        Assert.Equal("my receipt", msg.Text);
        await _f.Services.GetRequiredService<MediaFetcher>().WhenIdleAsync(TimeSpan.FromSeconds(10));
        await WaitFor(() => Messages(from).Single().MediaPath.Length > 0);
        Assert.Equal("image", Messages(from).Single().MediaType);
        var download = Assert.Single(_f.Http.To("api.ycloud.com", "/v2/whatsapp/media/download"));
        Assert.Equal("yc-api-key-0001", download.Header(YCloudWhatsAppClient.KeyHeader));
    }

    [Fact]
    public async Task YCloud_inbound_reaction_lands_on_the_message_and_is_never_a_bubble()
    {
        var a = YCloudAccount();
        var from = Digits();
        (await PostYCloud(a, YInbound(a, from, "wamid.R1" + from, new { type = "text", text = new { body = "hello" } })))
            .EnsureSuccessStatusCode();
        var target = Messages(from).Single();
        (await PostYCloud(a, YInbound(a, from, "wamid.R2" + from, new
        {
            type = "reaction",
            reaction = new { message_id = target.ExternalId, emoji = "👍" },
        }))).EnsureSuccessStatusCode();

        Assert.Single(Messages(from)); // no new message
        var reaction = InDb(db => db.MessageReactions.AsNoTracking().Single(r => r.MessageId == target.Id));
        Assert.Equal(("contact", "👍"), (reaction.Actor, reaction.Emoji));
    }

    private async Task<(ChannelAccount Account, string Key, HttpClient Client)> YCloudConversation()
    {
        var a = YCloudAccount();
        var from = Digits();
        (await PostYCloud(a, YInbound(a, from, "wamid.IN" + from, new { type = "text", text = new { body = "hi" } })))
            .EnsureSuccessStatusCode();
        _f.Http.Reset();
        return (a, from, await Authed());
    }

    [Fact]
    public async Task YCloud_sends_text_and_a_quoted_reply_through_send_directly()
    {
        var (a, key, c) = await YCloudConversation();
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "We open at 9." })).EnsureSuccessStatusCode();
        var call = Assert.Single(_f.Http.To("api.ycloud.com", "/v2/whatsapp/messages/sendDirectly"));
        Assert.Equal("yc-api-key-0001", call.Header(YCloudWhatsAppClient.KeyHeader));
        Assert.Equal($"+{a.ExternalId}", call.Json.GetProperty("from").GetString());
        Assert.Equal($"+{key}", call.Json.GetProperty("to").GetString());
        Assert.Equal("text", call.Json.GetProperty("type").GetString());
        Assert.Equal("We open at 9.", call.Json.GetProperty("text").GetProperty("body").GetString());
        var sent = Messages(key).Last();
        Assert.Equal("sent", sent.Status);
        Assert.StartsWith("wamid.YC", sent.ExternalId);

        // a quoted reply carries context.message_id (the customer's wamid) and records the quote
        var inbound = Messages(key).First(m => m.Direction == MessageDirection.In);
        _f.Http.Reset();
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "Yes!", reply_to = inbound.Id }))
            .EnsureSuccessStatusCode();
        var reply = Assert.Single(_f.Http.To("api.ycloud.com", "/v2/whatsapp/messages/sendDirectly"));
        Assert.Equal(inbound.ExternalId, reply.Json.GetProperty("context").GetProperty("message_id").GetString());
        Assert.Equal(inbound.Id, Messages(key).Last().ReplyToId);
    }

    [Fact]
    public async Task YCloud_sends_media_by_upload_and_a_live_template()
    {
        var (_, key, c) = await YCloudConversation();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(FakeProviderHttp.Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "photo.jpg");
        form.Add(new StringContent("Here it is"), "caption");
        (await c.PostAsync($"/api/whatsapp/threads/{key}/send-media", form)).EnsureSuccessStatusCode();
        Assert.Single(_f.Http.Calls.Where(x => x.Host == "api.ycloud.com" && x.Path.EndsWith("/upload")));
        var send = Assert.Single(_f.Http.To("api.ycloud.com", "/v2/whatsapp/messages/sendDirectly"));
        Assert.Equal("image", send.Json.GetProperty("type").GetString());
        Assert.StartsWith("ycmedia", send.Json.GetProperty("image").GetProperty("id").GetString());
        Assert.Equal("Here it is", send.Json.GetProperty("image").GetProperty("caption").GetString());
        Assert.Equal(("sent", "image"), (Messages(key).Last().Status, Messages(key).Last().MediaType));

        // the template picker lists YCloud's registry for this WABA
        var list = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/templates?account_id={Thread(key)!.AccountId}");
        Assert.Contains(list.GetProperty("templates").EnumerateArray(), t => t.GetProperty("name").GetString() == "order_update");
        Assert.False(list.GetProperty("sandbox").GetBoolean());

        _f.Http.Reset();
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send-template",
            new { name = "order_update", language = "en", variables = new[] { "Aisyah", "#1042" } })).EnsureSuccessStatusCode();
        var tpl = Assert.Single(_f.Http.To("api.ycloud.com", "/v2/whatsapp/messages/sendDirectly"));
        Assert.Equal("template", tpl.Json.GetProperty("type").GetString());
        var t = tpl.Json.GetProperty("template");
        Assert.Equal("order_update", t.GetProperty("name").GetString());
        var parameters = t.GetProperty("components")[0].GetProperty("parameters");
        Assert.Equal("#1042", parameters[1].GetProperty("text").GetString());
        Assert.Equal("Hi Aisyah, order #1042 is ready.", Messages(key).Last().Text);
    }

    [Fact]
    public async Task YCloud_statuses_move_forward_only_and_a_closed_window_is_not_an_account_fault()
    {
        var (a, key, c) = await YCloudConversation();
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "Order shipped" })).EnsureSuccessStatusCode();
        var sent = Messages(key).Last();

        (await PostYCloud(a, YStatus(sent.ExternalId, "read"))).EnsureSuccessStatusCode();
        Assert.Equal("read", Messages(key).Last().Status);
        (await PostYCloud(a, YStatus(sent.ExternalId, "delivered"))).EnsureSuccessStatusCode();
        Assert.Equal("read", Messages(key).Last().Status); // late "delivered" never moves it back

        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "Second" })).EnsureSuccessStatusCode();
        var second = Messages(key).Last();
        // YCloud's own id matches too (a send that returned no wamid)
        (await PostYCloud(a, YStatus("wamid.unknown", "failed", metaCode: 131047, id: second.ExternalId))).EnsureSuccessStatusCode();
        var failed = Messages(key).Last();
        Assert.Equal("failed", failed.Status);
        Assert.StartsWith("131047", failed.Error);
        Assert.NotEqual(Health.Disconnected, Account(a.Id).HealthStatus); // per-recipient: health untouched
    }

    [Fact]
    public async Task YCloud_reaction_send_and_window_guard()
    {
        var (_, key, c) = await YCloudConversation();
        var inbound = Messages(key).Single();
        var react = await c.PostAsJsonAsync($"/api/messaging/threads/whatsapp/{key}/messages/{inbound.Id}/react", new { emoji = "❤️" });
        Assert.Equal(HttpStatusCode.OK, react.StatusCode);
        var call = Assert.Single(_f.Http.To("api.ycloud.com", "/v2/whatsapp/messages/sendDirectly"));
        Assert.Equal("reaction", call.Json.GetProperty("type").GetString());
        Assert.Equal(inbound.ExternalId, call.Json.GetProperty("reaction").GetProperty("message_id").GetString());

        // the 24h window binds YCloud like Cloud: free text after it closes → 409
        InDb(db =>
        {
            var m = db.Messages.First(x => x.Id == inbound.Id);
            m.CreatedAt = DateTime.UtcNow.AddHours(-30);
            return db.SaveChanges();
        });
        var late = await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "Still there?" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task YCloud_account_api_masks_secrets_and_tests_the_number()
    {
        var c = await Authed();
        var bad = await c.PostAsJsonAsync("/api/whatsapp/accounts", new { provider = "ycloud", name = "YC", access_token = "k" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var res = await c.PostAsJsonAsync("/api/whatsapp/accounts", new
        {
            provider = "ycloud", name = "YCloud line", phone_number = "+60 12-345 0000", access_token = "yc-secret-key-9999",
            waba_id = "WABA-YC", app_secret = "whsec_signing_1234",
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ycloud", body.GetProperty("provider").GetString());
        Assert.Equal("reminder", body.GetProperty("role").GetString());
        Assert.True(body.GetProperty("official").GetBoolean());
        Assert.True(body.GetProperty("configured").GetBoolean());
        var id = body.GetProperty("id").GetString();
        Assert.Equal($"/api/messaging/webhook/whatsapp-ycloud/{id}", body.GetProperty("webhook_path").GetString());
        Assert.Equal("+60123450000", body.GetProperty("bsp").GetProperty("phone_number").GetString());
        Assert.DoesNotContain("yc-secret-key-9999", body.GetRawText());
        Assert.DoesNotContain("whsec_signing_1234", body.GetRawText());

        var test = await c.PostAsync($"/api/whatsapp/accounts/{id}/test", null);
        test.EnsureSuccessStatusCode();
        var t = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("YC Shop", t.GetProperty("verified_name").GetString());
        Assert.Equal(Health.Healthy, Account(int.Parse(id!)).HealthStatus);
    }

    // =====================================================================================
    // Twilio
    // =====================================================================================

    [Fact]
    public async Task Twilio_webhook_refuses_bad_signatures_missing_token_and_unknown_accounts()
    {
        var noToken = Add(Providers.Twilio, x =>
        {
            x.ExternalId = Digits();
            x.Config = new() { ["account_sid"] = TwilioSid, ["api_key_sid"] = "SK0123456789abcdef0123456789abcdef", ["api_key_secret"] = "s" };
        });
        var from = Digits();
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await PostTwilio(noToken, TInbound(noToken, from, "SMx1", "hi"), token: "x")).StatusCode);

        var a = TwilioAccount();
        var form = TInbound(a, from, "SMx2", "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostTwilio(a, form, sign: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostTwilio(a, form, token: "wrong")).StatusCode);
        var unknown = new HttpRequestMessage(HttpMethod.Post, "/api/messaging/webhook/whatsapp-twilio/999999")
        {
            Content = new FormUrlEncodedContent(form),
        };
        Assert.Equal(HttpStatusCode.NotFound, (await _f.CreateClient().SendAsync(unknown)).StatusCode);
        Assert.Null(Thread(from));
    }

    [Fact]
    public async Task Twilio_inbound_text_media_quote_and_dedup()
    {
        var a = TwilioAccount();
        var from = Digits();
        var form = TInbound(a, from, "SMin" + from, "Can I change my order?",
            new() { ["OriginalRepliedMessageSid"] = "SMearlier" });
        var ok = await PostTwilio(a, form);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("<Response>", await ok.Content.ReadAsStringAsync());
        (await PostTwilio(a, form)).EnsureSuccessStatusCode(); // a retry is not a second message

        var thread = Thread(from)!;
        Assert.Equal((a.Id, "Mei Ling"), (thread.AccountId ?? 0, thread.Name));
        var msg = Assert.Single(Messages(from));
        Assert.Equal(("Can I change my order?", "SMin" + from, "SMearlier"), (msg.Text, msg.ExternalId, msg.ReplyToExternalId));

        var media = TInbound(a, from, "SMmedia" + from, "", new()
        {
            ["NumMedia"] = "1",
            ["MediaUrl0"] = $"https://api.twilio.com/2010-04-01/Accounts/{TwilioSid}/Messages/MM1/Media/ME1",
            ["MediaContentType0"] = "image/jpeg",
        });
        (await PostTwilio(a, media)).EnsureSuccessStatusCode();
        Assert.Equal("[image]", Messages(from).Last().Text);
        await _f.Services.GetRequiredService<MediaFetcher>().WhenIdleAsync(TimeSpan.FromSeconds(10));
        await WaitFor(() => Messages(from).Last().MediaPath.Length > 0);
        var fetch = Assert.Single(_f.Http.To("api.twilio.com", "/Media/"));
        Assert.StartsWith("Basic ", fetch.Header("Authorization"));

        var location = TInbound(a, from, "SMloc" + from, "", new() { ["Latitude"] = "3.139", ["Longitude"] = "101.6869", ["Label"] = "KLCC" });
        (await PostTwilio(a, location)).EnsureSuccessStatusCode();
        Assert.Equal("[location] 3.139,101.6869 KLCC", Messages(from).Last().Text);
    }

    [Fact]
    public async Task Twilio_message_from_an_unknown_number_lands_as_an_unlinked_conversation()
    {
        var a = TwilioAccount();
        var from = Digits();
        (await PostTwilio(a, TInbound(a, from, "SMnew" + from, "first message, who answers?"))).EnsureSuccessStatusCode();
        await UnknownSenderAsserts.AssertListedAsNotInContactsAsync(_f, await Authed(), ChannelIds.WhatsApp, from,
            "first message, who answers?");
        Assert.Equal(a.Id, Thread(from)!.AccountId);
    }

    [Fact]
    public async Task Twilio_reaction_form_is_never_a_message()
    {
        var a = TwilioAccount();
        var from = Digits();
        (await PostTwilio(a, TInbound(a, from, "SMr1" + from, "hello"))).EnsureSuccessStatusCode();
        var target = Messages(from).Single();
        (await PostTwilio(a, TInbound(a, from, "SMr2" + from, "😂",
            new() { ["MessageType"] = "reaction", ["OriginalRepliedMessageSid"] = target.ExternalId }))).EnsureSuccessStatusCode();
        Assert.Single(Messages(from));
        Assert.Equal("😂", InDb(db => db.MessageReactions.AsNoTracking().Single(r => r.MessageId == target.Id)).Emoji);
    }

    private async Task<(ChannelAccount Account, string Key, HttpClient Client)> TwilioConversation()
    {
        var a = TwilioAccount();
        var from = Digits();
        (await PostTwilio(a, TInbound(a, from, "SMstart" + from, "hello"))).EnsureSuccessStatusCode();
        _f.Http.Reset();
        return (a, from, await Authed());
    }

    [Fact]
    public async Task Twilio_sends_text_unquoted_replies_and_refuses_reactions()
    {
        var (a, key, c) = await TwilioConversation();
        var inbound = Messages(key).Single();
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "Sure!", reply_to = inbound.Id }))
            .EnsureSuccessStatusCode();
        var call = Assert.Single(_f.Http.To("api.twilio.com", "/Messages.json"));
        var form = System.Web.HttpUtility.ParseQueryString(call.Body);
        Assert.Equal($"whatsapp:+{key}", form["To"]);
        Assert.Equal($"whatsapp:+{a.ExternalId}", form["From"]);
        Assert.Equal("Sure!", form["Body"]);
        Assert.StartsWith("Basic ", call.Header("Authorization"));
        var sent = Messages(key).Last();
        Assert.Equal("sent", sent.Status);
        Assert.StartsWith("SM", sent.ExternalId);
        Assert.Null(sent.ReplyToId); // Twilio has no quotes: sent unquoted, never failed

        var react = await c.PostAsJsonAsync($"/api/messaging/threads/whatsapp/{key}/messages/{inbound.Id}/react", new { emoji = "👍" });
        Assert.Equal(HttpStatusCode.Conflict, react.StatusCode);
        Assert.Contains("Twilio", (await react.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Twilio_sends_media_by_a_signed_public_link_and_templates_by_content_sid()
    {
        InDb(db =>
        {
            new ScopingService(db).SetSetting("public_base_url", "https://chat.example.test");
            return 0;
        });
        try
        {
            var (_, key, c) = await TwilioConversation();
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(FakeProviderHttp.Jpeg);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "file", "invoice.jpg");
            form.Add(new StringContent("Your invoice"), "caption");
            (await c.PostAsync($"/api/whatsapp/threads/{key}/send-media", form)).EnsureSuccessStatusCode();
            var call = Assert.Single(_f.Http.To("api.twilio.com", "/Messages.json"));
            var sentForm = System.Web.HttpUtility.ParseQueryString(call.Body);
            var mediaUrl = sentForm["MediaUrl"]!;
            Assert.StartsWith("https://chat.example.test/api/media/public/", mediaUrl);
            Assert.Equal("Your invoice", sentForm["Body"]);

            // the link serves exactly that file, anonymously; a tampered token is a 404
            var anon = _f.CreateClient();
            var path = new Uri(mediaUrl).AbsolutePath;
            var got = await anon.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, got.StatusCode);
            Assert.Equal(FakeProviderHttp.Jpeg, await got.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(path[..^2] + "xx")).StatusCode);

            // templates: the Content API listing, sent by ContentSid + ContentVariables
            var list = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/templates?account_id={Thread(key)!.AccountId}");
            var tpl = list.GetProperty("templates").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "order_ready");
            Assert.Equal("APPROVED", tpl.GetProperty("status").GetString());

            _f.Http.Reset();
            (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send-template",
                new { name = "order_ready", language = "en", variables = new[] { "Mei", "#77" } })).EnsureSuccessStatusCode();
            var send = Assert.Single(_f.Http.To("api.twilio.com", "/Messages.json"));
            var tform = System.Web.HttpUtility.ParseQueryString(send.Body);
            Assert.Equal(FakeProviderHttp.TwilioContentSid, tform["ContentSid"]);
            var vars = JsonSerializer.Deserialize<Dictionary<string, string>>(tform["ContentVariables"]!)!;
            Assert.Equal(("Mei", "#77"), (vars["1"], vars["2"]));
            Assert.Equal("Hi Mei, your order #77 is ready.", Messages(key).Last().Text);

            // Twilio Content templates are read-only here
            var create = await c.PostAsJsonAsync($"/api/whatsapp/templates?account_id={Thread(key)!.AccountId}",
                new { name = "new_one", body = "Hello {{1}}" });
            Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        }
        finally
        {
            InDb(db =>
            {
                new ScopingService(db).SetSetting("public_base_url", "");
                return 0;
            });
        }
    }

    [Fact]
    public async Task Twilio_media_without_a_public_address_fails_honestly()
    {
        var (a, key, c) = await TwilioConversation();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(FakeProviderHttp.Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "x.jpg");
        (await c.PostAsync($"/api/whatsapp/threads/{key}/send-media", form)).EnsureSuccessStatusCode();
        Assert.Empty(_f.Http.To("api.twilio.com", "/Messages.json"));
        var last = Messages(key).Last();
        Assert.Equal("failed", last.Status);
        Assert.Contains("public_base_url", last.Text);
        Assert.NotEqual(Health.Disconnected, Account(a.Id).HealthStatus);
    }

    [Fact]
    public async Task Twilio_status_callbacks_move_forward_and_map_error_codes()
    {
        var (a, key, c) = await TwilioConversation();
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "Your table is ready" })).EnsureSuccessStatusCode();
        var sid = Messages(key).Last().ExternalId;
        Dictionary<string, string> Status(string s, string? code = null)
        {
            var d = new Dictionary<string, string>
            {
                ["MessageSid"] = sid, ["MessageStatus"] = s, ["SmsStatus"] = s, ["AccountSid"] = TwilioSid,
                ["From"] = $"whatsapp:+{a.ExternalId}", ["To"] = $"whatsapp:+{key}",
            };
            if (code != null) d["ErrorCode"] = code;
            return d;
        }
        (await PostTwilio(a, Status("delivered"))).EnsureSuccessStatusCode();
        Assert.Equal("delivered", Messages(key).Last().Status);
        (await PostTwilio(a, Status("sent"))).EnsureSuccessStatusCode();
        Assert.Equal("delivered", Messages(key).Last().Status); // never backwards
        (await PostTwilio(a, Status("read"))).EnsureSuccessStatusCode();
        Assert.Equal("read", Messages(key).Last().Status);
        Assert.Single(Messages(key).Where(m => m.Direction == MessageDirection.In)); // a status is never a message

        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "Hello again" })).EnsureSuccessStatusCode();
        sid = Messages(key).Last().ExternalId;
        (await PostTwilio(a, Status("undelivered", "63016"))).EnsureSuccessStatusCode();
        var failed = Messages(key).Last();
        Assert.Equal("failed", failed.Status);
        Assert.Contains("Twilio 63016", failed.Error);
        Assert.NotEqual(Health.Disconnected, Account(a.Id).HealthStatus); // a closed window is the recipient's
        // …and 63016 closed the 24h window: free text is refused until the customer writes again
        Assert.Equal(HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "?" })).StatusCode);
        (await PostTwilio(a, TInbound(a, key, "SMagain" + key, "I'm back"))).EnsureSuccessStatusCode();

        // an account-level failure does move health
        _f.Http.Respond = call => call.Host == "api.twilio.com" && call.Path.EndsWith("/Messages.json")
            ? FakeProviderHttp.Json(new { code = 20003, message = "Authenticate" }, HttpStatusCode.Unauthorized)
            : null;
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "?" })).EnsureSuccessStatusCode();
        Assert.Equal("failed", Messages(key).Last().Status);
        Assert.Equal(Health.Disconnected, Account(a.Id).HealthStatus);
    }

    [Fact]
    public async Task Twilio_account_api_validates_and_masks()
    {
        var c = await Authed();
        var badSid = await c.PostAsJsonAsync("/api/whatsapp/accounts", new
        {
            provider = "twilio", name = "T", account_sid = "nope", access_token = "t", from_number = "+14155238886",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badSid.StatusCode);
        var noSender = await c.PostAsJsonAsync("/api/whatsapp/accounts", new
        {
            provider = "twilio", name = "T", account_sid = TwilioSid, access_token = "t",
        });
        Assert.Equal(HttpStatusCode.BadRequest, noSender.StatusCode);

        var res = await c.PostAsJsonAsync("/api/whatsapp/accounts", new
        {
            provider = "twilio", name = "Twilio WA", account_sid = TwilioSid, access_token = "twilio-secret-token-77",
            from_number = "whatsapp:+14155238886", api_key_sid = "SK0123456789abcdef0123456789abcdef",
            api_key_secret = "api-key-secret-88", role = "conversation",
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("twilio", "conversation"), (body.GetProperty("provider").GetString(), body.GetProperty("role").GetString()));
        Assert.Equal("+14155238886", body.GetProperty("bsp").GetProperty("from_number").GetString());
        Assert.Equal("14155238886", body.GetProperty("phone_number_id").GetString());
        Assert.False(body.GetProperty("supports_reactions").GetBoolean());
        Assert.DoesNotContain("twilio-secret-token-77", body.GetRawText());
        Assert.DoesNotContain("api-key-secret-88", body.GetRawText());

        // a masked secret sent back is "untouched"
        var id = body.GetProperty("id").GetString();
        (await c.PostAsJsonAsync($"/api/whatsapp/accounts/{id}", new { api_key_secret = "••••ret-88", name = "Renamed" }))
            .EnsureSuccessStatusCode();
        var row = Account(int.Parse(id!));
        Assert.Equal("api-key-secret-88", WhatsAppProviders.Cfg(row, "api_key_secret"));

        // the account test uses the API key, and reads the Twilio account
        _f.Http.Reset();
        var test = await c.PostAsync($"/api/whatsapp/accounts/{id}/test", null);
        test.EnsureSuccessStatusCode();
        var probe = Assert.Single(_f.Http.To("api.twilio.com", $"/Accounts/{TwilioSid}.json"));
        var basic = Encoding.UTF8.GetString(Convert.FromBase64String(probe.Header("Authorization")["Basic ".Length..]));
        Assert.Equal("SK0123456789abcdef0123456789abcdef:api-key-secret-88", basic);
    }

    // =====================================================================================
    // routing + health
    // =====================================================================================

    [Fact]
    public void Routing_treats_both_as_template_capable_and_never_sends_a_template_on_baileys()
    {
        InDb(db =>
        {
            var entity = new Entity { Name = "BSP outlet " + Guid.NewGuid().ToString("N")[..6], Slug = "bsp-" + Guid.NewGuid().ToString("N")[..8],
                TenantId = db.Entities.First().TenantId, CreatedAt = DateTime.UtcNow };
            db.Entities.Add(entity);
            db.SaveChanges();
            var baileys = new ChannelAccount
            {
                Channel = ChannelIds.WhatsApp, Provider = Providers.Baileys, Name = "QR", ExternalId = "gw-" + entity.Id,
                EntityId = entity.Id, HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
            };
            var yc = new ChannelAccount
            {
                Channel = ChannelIds.WhatsApp, Provider = Providers.YCloud, Name = "YC", ExternalId = Digits(), AccessToken = "k",
                EntityId = entity.Id, HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
            };
            db.ChannelAccounts.AddRange(baileys, yc);
            db.SaveChanges();
            Assert.Equal(AccountRoles.Reminder, AccountRoles.Of(yc));

            var routing = new Routing(db, new ScopingService(db));
            var templateOn = routing.Eligible(ChannelIds.WhatsApp, RouteNeed.Template, entity.Id);
            Assert.Equal(new[] { yc.Id }, templateOn.Select(a => a.Id));

            var thread = new Domain.Thread
            {
                Channel = ChannelIds.WhatsApp, Key = Digits(), AccountId = baileys.Id, EntityId = entity.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Threads.Add(thread);
            db.SaveChanges();
            // the thread lives on the QR number, but a template goes out on the official one
            Assert.Equal(yc.Id, routing.ForThread(thread, RouteNeed.Template).Account?.Id);

            // the official number is banned → the template is refused, never handed to Baileys
            yc.HealthStatus = Health.Banned;
            db.SaveChanges();
            var refused = routing.ForThread(thread, RouteNeed.Template);
            Assert.False(refused.Ok);
            Assert.Contains("never fall back to a Baileys", refused.Error);

            // Twilio counts as official too
            var tw = new ChannelAccount
            {
                Channel = ChannelIds.WhatsApp, Provider = Providers.Twilio, Name = "TW", ExternalId = Digits(), AccessToken = "t",
                EntityId = entity.Id, HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
                Config = new() { ["account_sid"] = TwilioSid, ["from_number"] = "+14155238886" },
            };
            db.ChannelAccounts.Add(tw);
            db.SaveChanges();
            Assert.Equal(tw.Id, routing.ForThread(thread, RouteNeed.Template).Account?.Id);
            return 0;
        });
    }

    [Fact]
    public async Task Health_probes_read_each_providers_own_answer()
    {
        var yc = YCloudAccount();
        var tw = TwilioAccount();
        using (var scope = _f.Services.CreateScope())
        {
            var prober = scope.ServiceProvider.GetRequiredService<ChannelHealthProber>();
            Assert.True(await prober.CheckAccountAsync(yc.Id));
            Assert.True(await prober.CheckAccountAsync(tw.Id));
        }
        Assert.Equal(Health.Healthy, Account(yc.Id).HealthStatus);
        Assert.Equal(Health.Healthy, Account(tw.Id).HealthStatus);
        Assert.Single(_f.Http.To("api.ycloud.com", "/v2/whatsapp/phoneNumbers/WABA-YC/"));
        Assert.Empty(_f.Http.Calls.Where(c => c.Host == "graph.facebook.com")); // never a Graph ping

        _f.Http.Respond = call => call.Host switch
        {
            "api.ycloud.com" => FakeProviderHttp.Json(new { error = new { status = 401, code = "UNAUTHENTICATED", message = "Invalid API key" } },
                HttpStatusCode.Unauthorized),
            "api.twilio.com" => FakeProviderHttp.Json(new { status = "suspended", friendly_name = "Twilio Shop" }),
            _ => null,
        };
        using (var scope = _f.Services.CreateScope())
        {
            var prober = scope.ServiceProvider.GetRequiredService<ChannelHealthProber>();
            await prober.CheckAccountAsync(yc.Id);
            await prober.CheckAccountAsync(tw.Id);
        }
        Assert.Equal(Health.Disconnected, Account(yc.Id).HealthStatus);
        Assert.Equal(Health.Banned, Account(tw.Id).HealthStatus);
    }

    [Fact]
    public void Signatures_and_media_links_are_tamper_proof()
    {
        var body = Encoding.UTF8.GetBytes("{\"type\":\"x\"}");
        var now = DateTimeOffset.UtcNow;
        var header = YCloudWhatsAppClient.Sign("sec", now.ToUnixTimeSeconds(), body);
        Assert.True(YCloudWhatsAppClient.VerifySignature("sec", body, header, now));
        Assert.False(YCloudWhatsAppClient.VerifySignature("sec", Encoding.UTF8.GetBytes("{}"), header, now));
        Assert.False(YCloudWhatsAppClient.VerifySignature("sec", body, header, now.AddMinutes(6)));
        Assert.False(YCloudWhatsAppClient.VerifySignature("", body, header, now));

        InDb(db =>
        {
            var links = new MediaLinks(new ScopingService(db));
            var token = links.Mint("whatsapp", "6012/abc.jpg");
            Assert.Equal(("whatsapp", "6012/abc.jpg"), links.Verify(token));
            Assert.Null(links.Verify(token + "x"));
            Assert.Null(links.Verify(token, DateTime.UtcNow.Add(MediaLinks.Lifetime).AddMinutes(1)));
            Assert.False(MediaLinks.Reachable("http://localhost:8090"));
            Assert.True(MediaLinks.Reachable("https://chat.example.com"));
            return 0;
        });

        Assert.Equal(MetaErrors.WindowClosed, TwilioWhatsAppClient.MetaEquivalent(63016));
        Assert.Equal(190, TwilioWhatsAppClient.MetaEquivalent(20003));
        Assert.Null(TwilioWhatsAppClient.MetaEquivalent(99999));
    }
}
