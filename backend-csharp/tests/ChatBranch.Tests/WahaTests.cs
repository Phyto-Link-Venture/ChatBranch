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

/// <summary>The API host with the operator's WAHA server (waha.test:3000) faked at the HTTP level.</summary>
public class WahaFactory : ApiFactory
{
    public FakeProviderHttp Http { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton(new WahaWhatsAppClient(Http)));
    }
}

/// <summary>
/// WhatsApp through WAHA (the operator's own self-hosted WhatsApp HTTP API), tested against a
/// fake of its DOCUMENTED API — never a live WAHA:
/// <list type="bullet">
///   <item>the provider vocabulary: normalisation, and that <c>waha</c> is NOT official (no
///         templates, no 24h window, the conversation role, no template registry);</item>
///   <item>an outbound text, a quoted reply and a reaction against the fake
///         (<c>POST /api/sendText</c>, <c>PUT /api/reaction</c>, <c>X-Api-Key</c>);</item>
///   <item>the signed webhook: unsigned / tampered / replayed / no key / unknown account;</item>
///   <item>one inbound <c>message</c> → exactly one thread + one message through
///         <c>ThreadStore</c>, idempotent on a redelivery;</item>
///   <item>session status → health, with the pairing states NOT recorded as failures;</item>
///   <item>the pairing flow (QR, pairing code) and the probe;</item>
///   <item>sandbox: no WAHA configured ⇒ <c>status="sandbox"</c>, never a throw.</item>
/// </list>
/// </summary>
public class WahaTests : IClassFixture<WahaFactory>
{
    private const string Host = "waha.test";
    private const string BaseUrl = "http://waha.test:3000";
    private const string ApiKey = "waha-api-key-0001";
    private const string Secret = "whsec_waha_0123456789abcdef";

    private readonly WahaFactory _f;

    public WahaTests(WahaFactory f)
    {
        _f = f;
        _f.Http.Reset();
    }

    // ---- helpers ----------------------------------------------------------------------

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

    private static ChannelAccount Row(string session, string baseUrl = BaseUrl, string secret = Secret,
        string key = ApiKey, string health = Health.Unknown, string me = "") => new()
        {
            Channel = ChannelIds.WhatsApp,
            Provider = Providers.Waha,
            Name = $"WAHA {session}",
            ExternalId = me,
            AccessToken = key,
            AppSecret = secret,
            HealthStatus = health,
            CreatedAt = DateTime.UtcNow,
            Config = new Dictionary<string, object?>
            {
                [WahaConfig.BaseUrlKey] = baseUrl,
                [WahaConfig.SessionKey] = session,
                [WahaConfig.EngineKey] = "NOWEB",
            },
        };

    private ChannelAccount Add(ChannelAccount a) => InDb(db =>
    {
        db.ChannelAccounts.Add(a);
        db.SaveChanges();
        return a;
    });

    private ChannelAccount Account(int id) => InDb(db => db.ChannelAccounts.AsNoTracking().First(a => a.Id == id));

    private Domain.Thread? Thread(string key) =>
        InDb(db => db.Threads.AsNoTracking().FirstOrDefault(t => t.Channel == ChannelIds.WhatsApp && t.Key == key));

    private List<Message> Messages(string key) => InDb(db => db.Messages.AsNoTracking()
        .Where(m => m.Thread!.Channel == ChannelIds.WhatsApp && m.Thread.Key == key).OrderBy(m => m.Id).ToList());

    /// <summary>The <c>message</c> envelope exactly as WAHA sends it (WAHAWebhook&lt;WAMessage&gt;).</summary>
    private static object Inbound(ChannelAccount a, string from, string id, string body, object? media = null,
        DateTime? at = null, string? replyTo = null) => new
        {
            id = "evt_" + Guid.NewGuid().ToString("N")[..20].ToLowerInvariant(),
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            @event = WahaWebhook.MessageEvent,
            session = WahaConfig.Session(a),
            metadata = new Dictionary<string, string> { ["chatbranch.account"] = a.Id.ToString() },
            me = new { id = $"{a.ExternalId}@c.us", pushName = "Shop" },
            engine = "NOWEB",
            environment = new { tier = "CORE", version = "2026.6.1" },
            payload = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["timestamp"] = new DateTimeOffset(at ?? DateTime.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds(),
                ["from"] = $"{from}@c.us",
                ["fromMe"] = false,
                ["source"] = "app",
                ["to"] = $"{a.ExternalId}@c.us",
                ["body"] = body,
                ["hasMedia"] = media != null,
                ["media"] = media,
                ["ack"] = 1,
                ["ackName"] = "SERVER",
                ["replyTo"] = replyTo == null ? null : new { id = replyTo },
                ["_data"] = new { notifyName = "Aisyah" },
            },
        };

    /// <summary>A <c>session.status</c> envelope (WASessionStatusBody).</summary>
    private static object StatusEvent(ChannelAccount a, string status, string me = "") => new
    {
        id = "evt_" + Guid.NewGuid().ToString("N")[..20].ToLowerInvariant(),
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        @event = WahaWebhook.SessionStatusEvent,
        session = WahaConfig.Session(a),
        me = me.Length > 0 ? new { id = $"{me}@c.us", pushName = "Shop" } : null,
        engine = "NOWEB",
        payload = new { name = WahaConfig.Session(a), status, statuses = Array.Empty<object>(), data = (object?)null },
    };

    private async Task<HttpResponseMessage> Post(ChannelAccount a, object evt, string? secret = Secret,
        bool sign = true, int? accountId = null, string? tamper = null)
    {
        var body = JsonSerializer.Serialize(evt);
        var signed = Encoding.UTF8.GetBytes(body);
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/messaging/webhook/whatsapp-waha/{accountId ?? a.Id}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (sign)
            req.Headers.TryAddWithoutValidation(WahaWhatsAppClient.SignatureHeader,
                tamper ?? WahaWhatsAppClient.Sign(secret ?? "", signed));
        return await _f.CreateClient().SendAsync(req);
    }

    /// <summary>The fake WAHA: a session in <paramref name="status"/>, plus the send endpoints.</summary>
    private void FakeWaha(string status = WahaStatus.Working, string me = "", string qr = "UFFSLVZBTFVF",
        string? pairingCode = "WXYZ-1234")
    {
        var next = 0;
        _f.Http.Respond = c =>
        {
            if (c.Host != Host) return null;
            if (c.Path.StartsWith("/api/sessions") && c.Method == "GET")
                return FakeProviderHttp.Json(new
                {
                    name = "default",
                    status,
                    me = me.Length > 0 ? new { id = $"{me}@c.us", pushName = "Mei Ling" } : null,
                });
            if (c.Path == "/api/startTyping" || c.Path == "/api/sendSeen")
                return FakeProviderHttp.Json(new { });
            if (c.Path.EndsWith("/auth/qr"))
                return FakeProviderHttp.Json(new { mimetype = "image/png", data = qr });
            if (c.Path.EndsWith("/auth/request-code"))
                return pairingCode == null
                    ? FakeProviderHttp.Json(new { message = "pairing code not available" },
                        HttpStatusCode.UnprocessableEntity)
                    : FakeProviderHttp.Json(new { code = pairingCode });
            if (c.Path.StartsWith("/api/sessions")) return FakeProviderHttp.Json(new { name = "default", status });
            if (c.Path.StartsWith("/api/send") || c.Path == "/api/reaction")
                return FakeProviderHttp.Json(new
                {
                    id = $"false_60000@c.us_WAHA{++next:0000}",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    fromMe = true,
                });
            return FakeProviderHttp.Json(new { message = "not faked" }, HttpStatusCode.NotFound);
        };
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

    // =====================================================================================
    // 1. the provider vocabulary — WAHA is UNOFFICIAL
    // =====================================================================================

    [Fact]
    public void Waha_is_a_known_provider_and_is_never_official()
    {
        Assert.Contains(Providers.Waha, WhatsAppProviders.All);
        Assert.DoesNotContain(Providers.Waha, WhatsAppProviders.Official);
        Assert.Contains(Providers.Waha, WhatsAppProviders.Unofficial);

        Assert.True(WhatsAppProviders.Known("waha"));
        Assert.True(WhatsAppProviders.Known(" WAHA "));
        Assert.Equal(Providers.Waha, WhatsAppProviders.Normalize("WaHa"));
        // an unknown provider still reads as cloud, the historical default
        Assert.Equal(Providers.Cloud, WhatsAppProviders.Normalize("wahaa"));
        Assert.Equal(Providers.Cloud, WhatsAppProviders.Normalize(null));

        var a = Row("default");
        Assert.False(WhatsAppProviders.IsOfficial(a));
        Assert.True(WhatsAppProviders.IsUnofficial(a));
        Assert.True(WhatsAppProviders.IsWaha(a));
        Assert.False(WhatsAppProviders.IsBsp(a));           // not a Business Solution Provider
        Assert.True(WhatsAppProviders.HasOwnProbe(a));      // but it does probe its own session
        Assert.False(WhatsAppProviders.IsMeta(a));

        // no templates, no Meta window, and it is a CONVERSATION line, never the business one
        Assert.False(WhatsAppProviders.HasTemplateRegistry(a));
        Assert.False(WhatsAppProviders.WindowApplies(a));
        Assert.Equal(AccountRoles.Conversation, AccountRoles.Of(a));
        // a template asks for AnyOfficial — WAHA can never carry one
        Assert.False(WhatsAppProviders.Matches(a, WhatsAppProviders.AnyOfficial));
        Assert.True(WhatsAppProviders.Matches(a, Providers.Waha));

        Assert.Equal("WhatsApp (WAHA, unofficial)", WhatsAppProviders.Label(Providers.Waha));
        Assert.Equal("/api/messaging/webhook/whatsapp-waha/0", WhatsAppProviders.WebhookPath(a));

        // configured = we know WHERE WAHA is and WHICH session; the API key is optional
        Assert.True(WhatsAppProviders.Configured(a));
        Assert.True(WhatsAppProviders.Configured(Row("default", key: "")));
        Assert.False(WhatsAppProviders.Configured(Row("default", baseUrl: "")));
    }

    [Fact]
    public void Waha_connection_settings_are_validated()
    {
        Assert.True(WahaConfig.ValidBaseUrl("http://waha:3000/", out var url));
        Assert.Equal("http://waha:3000", url);
        Assert.True(WahaConfig.ValidBaseUrl("https://waha.example.com", out _));
        Assert.False(WahaConfig.ValidBaseUrl("waha.example.com", out _));
        Assert.False(WahaConfig.ValidBaseUrl("", out _));
        Assert.False(WahaConfig.ValidBaseUrl("ftp://waha", out _));

        Assert.True(WahaConfig.ValidSession("", out var s));
        Assert.Equal(WahaConfig.DefaultSession, s);
        Assert.True(WahaConfig.ValidSession("shop-1", out _));
        Assert.False(WahaConfig.ValidSession("shop/1", out _));

        Assert.True(WahaConfig.ValidEngine("", out _));
        Assert.True(WahaConfig.ValidEngine("gows", out var e));
        Assert.Equal("GOWS", e);
        Assert.False(WahaConfig.ValidEngine("VENOM", out _));   // not one WAHA lists today

        Assert.Equal("60123456789@c.us", WahaWhatsAppClient.ChatId("+60123456789"));
        Assert.Equal("60123@c.us", WahaWhatsAppClient.ChatId("60123@c.us"));
        Assert.Equal("60123456789", WahaWhatsAppClient.Digits("60123456789:12@s.whatsapp.net"));
    }

    // =====================================================================================
    // 2. sending
    // =====================================================================================

    /// <summary>A live WAHA number with one inbound message, so a conversation exists.</summary>
    private async Task<(ChannelAccount Account, string Key, HttpClient Client)> Conversation(string session)
    {
        var a = Add(Row(session, me: Digits()));
        var from = Digits();
        FakeWaha(me: a.ExternalId);
        (await Post(a, Inbound(a, from, $"false_{from}@c.us_IN1", "hi"))).EnsureSuccessStatusCode();
        _f.Http.Reset();
        FakeWaha(me: a.ExternalId);
        return (a, from, await Authed());
    }

    [Fact]
    public async Task Waha_sends_text_a_quoted_reply_and_a_reaction_against_the_documented_api()
    {
        var (_, key, c) = await Conversation("send");
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "We open at 9." }))
            .EnsureSuccessStatusCode();
        var call = Assert.Single(_f.Http.To(Host, "/api/sendText"));
        Assert.Equal("POST", call.Method);
        Assert.Equal(ApiKey, call.Header(WahaWhatsAppClient.KeyHeader));
        Assert.Equal("send", call.Json.GetProperty("session").GetString());
        Assert.Equal($"{key}@c.us", call.Json.GetProperty("chatId").GetString());
        Assert.Equal("We open at 9.", call.Json.GetProperty("text").GetString());
        var sent = Messages(key).Last();
        Assert.Equal("sent", sent.Status);
        Assert.StartsWith("false_", sent.ExternalId);

        // a quoted reply rides on reply_to (WAHA's own field name)
        var inbound = Messages(key).First(m => m.Direction == MessageDirection.In);
        _f.Http.Reset();
        FakeWaha();
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "Yes!", reply_to = inbound.Id }))
            .EnsureSuccessStatusCode();
        var reply = Assert.Single(_f.Http.To(Host, "/api/sendText"));
        Assert.Equal(inbound.ExternalId, reply.Json.GetProperty("reply_to").GetString());
        Assert.Equal(inbound.Id, Messages(key).Last().ReplyToId);

        // a reaction is PUT /api/reaction {session, messageId, reaction}
        _f.Http.Reset();
        FakeWaha();
        (await c.PostAsJsonAsync($"/api/messaging/threads/whatsapp/{key}/messages/{inbound.Id}/react",
            new { emoji = "👍" })).EnsureSuccessStatusCode();
        var react = Assert.Single(_f.Http.To(Host, "/api/reaction"));
        Assert.Equal("PUT", react.Method);
        Assert.Equal(inbound.ExternalId, react.Json.GetProperty("messageId").GetString());
        Assert.Equal("👍", react.Json.GetProperty("reaction").GetString());
    }

    [Fact]
    public async Task Waha_has_no_template_registry_and_never_receives_a_template_message()
    {
        var (a, key, c) = await Conversation("tpl");
        Assert.False(WhatsAppProviders.HasTemplateRegistry(Account(a.Id)));
        // no live registry behind an unofficial number: the picker is the sandbox set
        var list = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/templates?account_id={a.Id}");
        Assert.True(list.GetProperty("sandbox").GetBoolean());
        // a name only Meta could have judged is refused here, not silently "sent"
        var unknown = await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send-template",
            new { name = "not_a_real_template", language = "en", variables = new[] { "Aisyah" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        // and a known one never goes out as a TEMPLATE through WAHA: RouteNeed.Template asks
        // for an official number, so nothing template-shaped ever reaches this server
        var known = list.GetProperty("templates").EnumerateArray().First().GetProperty("name").GetString();
        await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send-template",
            new { name = known, language = "en", variables = new[] { "Aisyah", "#1042" } });
        Assert.Empty(_f.Http.Calls.Where(x => x.Host == Host && x.Path.Contains("template")));
    }

    [Fact]
    public async Task Waha_sends_a_file_inline_as_base64_because_waha_has_no_upload_step()
    {
        var (_, key, c) = await Conversation("media");
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(FakeProviderHttp.Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "photo.jpg");
        form.Add(new StringContent("Here it is"), "caption");
        (await c.PostAsync($"/api/whatsapp/threads/{key}/send-media", form)).EnsureSuccessStatusCode();
        var send = Assert.Single(_f.Http.To(Host, "/api/sendImage"));
        var sentFile = send.Json.GetProperty("file");
        Assert.Equal("image/jpeg", sentFile.GetProperty("mimetype").GetString());
        Assert.Equal(Convert.ToBase64String(FakeProviderHttp.Jpeg), sentFile.GetProperty("data").GetString());
        Assert.Equal("Here it is", send.Json.GetProperty("caption").GetString());
        Assert.Equal(("sent", "image"), (Messages(key).Last().Status, Messages(key).Last().MediaType));
    }

    // =====================================================================================
    // 3. the webhook's authentication
    // =====================================================================================

    [Fact]
    public async Task Waha_webhook_refuses_unsigned_tampered_replayed_keyless_and_unknown()
    {
        var noKey = Add(Row("nokey", secret: ""));
        var from = Digits();
        var evt = Inbound(noKey, from, $"false_{from}@c.us_A", "hi");
        // never accepted unsigned: no HMAC key configured ⇒ 503
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(noKey, evt)).StatusCode);

        var a = Add(Row("auth"));
        evt = Inbound(a, from, $"false_{from}@c.us_B", "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(a, evt, sign: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(a, evt, secret: "wrong-key")).StatusCode);
        // a body that changed after signing
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post(a, evt, tamper: WahaWhatsAppClient.Sign(Secret, Encoding.UTF8.GetBytes("{}")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(a, evt, accountId: 999_999)).StatusCode);
        Assert.Null(Thread(from));

        // a REPLAY of a correctly signed envelope from outside the freshness window
        var stale = new
        {
            id = "evt_stale",
            timestamp = DateTimeOffset.UtcNow.Add(-WahaWhatsAppClient.Freshness).AddMinutes(-5)
                .ToUnixTimeMilliseconds(),
            @event = WahaWebhook.MessageEvent,
            session = "auth",
            payload = new
            {
                id = $"false_{from}@c.us_STALE", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                from = $"{from}@c.us", fromMe = false, body = "replayed", hasMedia = false,
            },
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(a, stale)).StatusCode);
        Assert.Null(Thread(from));
    }

    [Fact]
    public void Waha_signature_is_hex_hmac_sha512_of_the_raw_body()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"message\"}");
        var sig = WahaWhatsAppClient.Sign(Secret, body);
        Assert.Equal(128, sig.Length);                                   // sha512 → 64 bytes of hex
        Assert.Equal(sig, sig.ToLowerInvariant());
        Assert.Equal(WahaWhatsAppClient.Algorithm, "sha512");
        Assert.True(WahaWhatsAppClient.VerifySignature(Secret, body, sig));
        Assert.True(WahaWhatsAppClient.VerifySignature(Secret, body, $"  {sig.ToUpperInvariant()} "));
        Assert.False(WahaWhatsAppClient.VerifySignature(Secret, body, sig[..126] + "00"));
        Assert.False(WahaWhatsAppClient.VerifySignature(Secret, Encoding.UTF8.GetBytes("{}"), sig));
        Assert.False(WahaWhatsAppClient.VerifySignature("", body, sig));
        Assert.False(WahaWhatsAppClient.VerifySignature(Secret, body, null));
    }

    // =====================================================================================
    // 4. inbound
    // =====================================================================================

    [Fact]
    public async Task Waha_inbound_stores_one_thread_and_message_heals_and_is_idempotent()
    {
        var a = Add(Row("in", health: Health.Degraded, me: Digits()));
        FakeWaha(me: a.ExternalId);
        var from = Digits();
        var at = DateTime.UtcNow.AddMinutes(-20);
        var id = $"false_{from}@c.us_IDEMPOTENT";
        var evt = Inbound(a, from, id, "Is the durian ready?", at: at);

        (await Post(a, evt)).EnsureSuccessStatusCode();
        var thread = Thread(from);
        Assert.NotNull(thread);
        Assert.Equal(a.Id, thread!.AccountId);
        var msgs = Messages(from);
        var one = Assert.Single(msgs);
        Assert.Equal(MessageDirection.In, one.Direction);
        Assert.Equal("Is the durian ready?", one.Text);
        Assert.Equal(id, one.ExternalId);
        // WhatsApp's own time, not arrival time
        Assert.True(Math.Abs((one.CreatedAt - at).TotalSeconds) < 5);
        Assert.Equal("Aisyah", InDb(db => db.Threads.AsNoTracking().First(t => t.Id == thread.Id).Name));
        // inbound proves the session works
        Assert.Equal(Health.Healthy, Account(a.Id).HealthStatus);

        // WAHA retries a webhook up to 15 times: a redelivery must change nothing
        var before = thread.UpdatedAt;
        (await Post(a, evt)).EnsureSuccessStatusCode();
        Assert.Single(Messages(from));
        Assert.Equal(before, Thread(from)!.UpdatedAt);
        // and a DIFFERENT id on the same chat appends to the SAME thread
        (await Post(a, Inbound(a, from, id + "-2", "Hello?"))).EnsureSuccessStatusCode();
        Assert.Equal(2, Messages(from).Count);
        Assert.Equal(thread.Id, Thread(from)!.Id);
    }

    [Fact]
    public async Task Waha_inbound_media_is_a_placeholder_then_the_file_off_the_ack()
    {
        var a = Add(Row("inmedia", me: Digits()));
        var from = Digits();
        var url = $"{BaseUrl}/api/files/false_{from}@c.us_IMG.jpg";
        _f.Http.Respond = c => c.Host == Host && c.Path.StartsWith("/api/files/")
            ? FakeProviderHttp.File(FakeProviderHttp.Jpeg)
            : c.Host == Host ? FakeProviderHttp.Json(new { name = "inmedia", status = WahaStatus.Working }) : null;

        (await Post(a, Inbound(a, from, $"false_{from}@c.us_IMG", "",
            media: new { url, mimetype = "image/jpeg", filename = "durian.jpg" }))).EnsureSuccessStatusCode();
        var msg = Assert.Single(Messages(from));
        Assert.Equal("[image]", msg.Text);
        await WaitFor(() => Messages(from)[0].MediaPath.Length > 0);
        Assert.Equal("image", Messages(from)[0].MediaType);
        // the API key only ever rides to WAHA's own host
        Assert.Equal(ApiKey, Assert.Single(_f.Http.To(Host, "/api/files/")).Header(WahaWhatsAppClient.KeyHeader));
    }

    [Fact]
    public async Task Waha_webhook_acks_the_events_the_inbox_does_not_use()
    {
        var a = Add(Row("ignore", me: Digits()));
        FakeWaha(me: a.ExternalId);
        var from = Digits();
        // a group, our own echo, and an event we never subscribed to: all acked, none stored
        foreach (var evt in new object[]
                 {
                     new { @event = "message", session = "ignore", payload = new { id = "g1", from = $"{from}@g.us", fromMe = false, body = "hi" } },
                     new { @event = "message", session = "ignore", payload = new { id = "m1", from = $"{from}@c.us", fromMe = true, body = "ours" } },
                     new { @event = "presence.update", session = "ignore", payload = new { id = "p1", from = $"{from}@c.us", fromMe = false, body = "x" } },
                 })
            (await Post(a, evt)).EnsureSuccessStatusCode();
        Assert.Null(Thread(from));
        // signed garbage is acked, never 500
        var junk = new HttpRequestMessage(HttpMethod.Post, $"/api/messaging/webhook/whatsapp-waha/{a.Id}")
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
        };
        junk.Headers.TryAddWithoutValidation(WahaWhatsAppClient.SignatureHeader,
            WahaWhatsAppClient.Sign(Secret, Encoding.UTF8.GetBytes("not json")));
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().SendAsync(junk)).StatusCode);
    }

    // =====================================================================================
    // 5. session status → health
    // =====================================================================================

    [Fact]
    public void Waha_session_status_maps_to_health_and_the_pairing_states_are_progress()
    {
        var a = Row("verdict");

        // WORKING is the only healthy one
        var ok = WahaWhatsAppClient.Verdict(a, new WahaSession(WahaStatus.Working, MeName: "Mei Ling", HttpStatus: 200));
        Assert.Equal(Health.Healthy, ok.Status);

        // the pairing flow working as intended: NEVER recorded as a failure
        foreach (var status in new[]
                 {
                     WahaStatus.Starting, WahaStatus.PasskeyRequired, WahaStatus.PasskeyConfirmationRequired,
                 })
        {
            Assert.True(WahaStatus.Transient(status));
            var v = WahaWhatsAppClient.Verdict(a, new WahaSession(status, HttpStatus: 200));
            Assert.Null(v.Status);
            Assert.True(v.Transient);
        }

        // and the ones that really mean "this inbox cannot receive"
        Assert.Equal(Health.Disconnected,
            WahaWhatsAppClient.Verdict(a, new WahaSession(WahaStatus.ScanQrCode, HttpStatus: 200)).Status);
        Assert.Equal(Health.Disconnected,
            WahaWhatsAppClient.Verdict(a, new WahaSession(WahaStatus.Stopped, HttpStatus: 200)).Status);
        Assert.Equal(Health.Disconnected,
            WahaWhatsAppClient.Verdict(a, new WahaSession(WahaStatus.Failed, HttpStatus: 200)).Status);
        // a refused API key is account-level
        Assert.Equal(Health.Disconnected,
            WahaWhatsAppClient.Verdict(a, new WahaSession(WahaStatus.Unknown, HttpStatus: 401)).Status);
        // WAHA itself being down is TRANSIENT — the number is fine
        var down = WahaWhatsAppClient.Verdict(a, new WahaSession(WahaStatus.Unknown, Detail: "WAHA unreachable"));
        Assert.Null(down.Status);
        Assert.True(down.Transient);
        // a banned number stays banned until WAHA says WORKING again
        var banned = Row("verdict");
        banned.HealthStatus = Health.Banned;
        Assert.Null(WahaWhatsAppClient.Verdict(banned, new WahaSession(WahaStatus.Failed, HttpStatus: 200)).Status);
    }

    [Fact]
    public async Task Waha_session_status_pushes_move_health_through_the_single_writer_only()
    {
        var a = Add(Row("lifecycle", health: Health.Healthy));
        FakeWaha();
        // STARTING is expected right after a start / a post-pair restart: health must NOT move
        (await Post(a, StatusEvent(a, WahaStatus.Starting))).EnsureSuccessStatusCode();
        Assert.Equal(Health.Healthy, Account(a.Id).HealthStatus);

        (await Post(a, StatusEvent(a, WahaStatus.ScanQrCode))).EnsureSuccessStatusCode();
        Assert.Equal(Health.Disconnected, Account(a.Id).HealthStatus);

        // WORKING both heals it and teaches us the number
        var me = Digits();
        (await Post(a, StatusEvent(a, WahaStatus.Working, me))).EnsureSuccessStatusCode();
        Assert.Equal(Health.Healthy, Account(a.Id).HealthStatus);
        Assert.Equal(me, Account(a.Id).ExternalId);
        // every transition is one account.health audit row, written by AccountHealth.SetStatus
        Assert.NotEmpty(InDb(db => db.AuditLogs.AsNoTracking()
            .Where(l => l.Action == "account.health" && l.Target == a.Name).ToList()));

        // a push for ANOTHER session on the same WAHA is not ours
        var other = StatusEvent(Row("somebody-else"), WahaStatus.Failed);
        (await Post(a, other)).EnsureSuccessStatusCode();
        Assert.Equal(Health.Healthy, Account(a.Id).HealthStatus);
    }

    [Fact]
    public async Task Waha_probe_reads_its_own_session_and_reports_the_number()
    {
        var http = new FakeProviderHttp();
        var a = Row("probe");
        http.Respond = c => c.Host == Host && c.Path == "/api/sessions/probe"
            ? FakeProviderHttp.Json(new
            {
                name = "probe", status = WahaStatus.Working, me = new { id = "60123456789@c.us", pushName = "Mei Ling" },
            })
            : null;
        var client = new WahaWhatsAppClient(http);
        var probe = await client.ProbeBspAsync(a);
        Assert.NotNull(probe);
        Assert.Equal(Health.Healthy, probe!.Verdict.Status);
        Assert.Equal("60123456789", probe.Number);
        Assert.Equal("Mei Ling", probe.Name);
        Assert.Equal(WahaStatus.Working, probe.Quality);
        Assert.Equal(ApiKey, Assert.Single(http.To(Host, "/api/sessions/probe")).Header(WahaWhatsAppClient.KeyHeader));

        // a session WAHA has never heard of reads as stopped, not as a crash
        http.Reset();
        http.Respond = c => c.Host == Host
            ? FakeProviderHttp.Json(new { statusCode = 404, message = "Session not found" }, HttpStatusCode.NotFound)
            : null;
        Assert.Equal(Health.Disconnected, (await client.ProbeBspAsync(a))!.Verdict.Status);
    }

    // =====================================================================================
    // 6. pairing
    // =====================================================================================

    [Fact]
    public async Task Waha_pairing_creates_the_session_with_our_signed_webhook_and_shows_the_qr()
    {
        var a = Add(Row("pair", secret: ""));
        var c = await Authed();
        // the session does not exist yet → POST /api/sessions {name, start, config}
        var created = false;
        _f.Http.Respond = call =>
        {
            if (call.Host != Host) return null;
            if (call.Path == "/api/sessions/pair" && call.Method == "GET" && !created)
                return FakeProviderHttp.Json(new { statusCode = 404, message = "Session not found" },
                    HttpStatusCode.NotFound);
            if (call.Path == "/api/sessions" && call.Method == "POST")
            {
                created = true;
                return FakeProviderHttp.Json(new { name = "pair", status = WahaStatus.Starting });
            }
            if (call.Path == "/api/sessions/pair" && call.Method == "GET")
                return FakeProviderHttp.Json(new { name = "pair", status = WahaStatus.ScanQrCode });
            if (call.Path == "/api/pair/auth/qr")
                return FakeProviderHttp.Json(new { mimetype = "image/png", data = "UUJBU0U2NA==" });
            return FakeProviderHttp.Json(new { name = "pair", status = WahaStatus.Starting });
        };

        var state = await (await c.PostAsJsonAsync($"/api/whatsapp/accounts/{a.Id}/waha/start", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(WahaStatus.ScanQrCode, state.GetProperty("status").GetString());
        Assert.StartsWith("data:image/png;base64,", state.GetProperty("qr").GetString());
        Assert.True(state.GetProperty("webhook_configured").GetBoolean());
        Assert.Contains("/api/messaging/webhook/whatsapp-waha/", state.GetProperty("webhook_url").GetString());

        // the session was created with OUR webhook, our two events and a minted HMAC key
        var create = Assert.Single(_f.Http.Calls.Where(x => x.Host == Host && x.Path == "/api/sessions" && x.Method == "POST"));
        var hook = create.Json.GetProperty("config").GetProperty("webhooks")[0];
        Assert.Contains($"/api/messaging/webhook/whatsapp-waha/{a.Id}", hook.GetProperty("url").GetString());
        Assert.Equal(new[] { "message", "session.status" },
            hook.GetProperty("events").EnumerateArray().Select(e => e.GetString()).ToArray());
        var key = hook.GetProperty("hmac").GetProperty("key").GetString();
        Assert.Equal(64, key!.Length);
        Assert.Equal(key, Account(a.Id).AppSecret);          // what we will verify with
        // and that minted key really does verify a delivery
        var from = Digits();
        (await Post(a, Inbound(a, from, $"false_{from}@c.us_PAIRED", "hello"), secret: key))
            .EnsureSuccessStatusCode();
        Assert.Single(Messages(from));
    }

    [Fact]
    public async Task Waha_pairing_by_phone_number_asks_waha_for_a_code_and_refuses_an_impossible_number()
    {
        var a = Add(Row("bycode"));
        var c = await Authed();
        FakeWaha(status: WahaStatus.ScanQrCode);

        var bad = await c.PostAsJsonAsync($"/api/whatsapp/accounts/{a.Id}/waha/start", new { phone = "0123" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty(_f.Http.To(Host, "/auth/request-code"));   // a pairing attempt is never spent on it

        var state = await (await c.PostAsJsonAsync($"/api/whatsapp/accounts/{a.Id}/waha/start",
            new { phone = "+60123456789" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("WXYZ-1234", state.GetProperty("pairing_code").GetString());
        Assert.Equal("60123456789", state.GetProperty("pairing_phone").GetString());
        Assert.Equal("60123456789",
            Assert.Single(_f.Http.To(Host, "/auth/request-code")).Json.GetProperty("phoneNumber").GetString());

        // WhatsApp refusing the number keeps the QR as the fallback, not an error
        _f.Http.Reset();
        FakeWaha(status: WahaStatus.ScanQrCode, pairingCode: null);
        var fallback = await (await c.PostAsJsonAsync($"/api/whatsapp/accounts/{a.Id}/waha/start",
            new { phone = "+60123456789" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(WahaStatus.ScanQrCode, fallback.GetProperty("status").GetString());
        Assert.StartsWith("data:image/png;base64,", fallback.GetProperty("qr").GetString());
    }

    [Fact]
    public async Task Waha_pairing_never_rewrites_a_working_session()
    {
        // WAHA's own SessionService.updateSession STOPS the session, upserts the config and
        // starts it again — so pressing Pair on a paired number must write NOTHING.
        var a = Add(Row("live", me: Digits()));
        var c = await Authed();
        var ourHook = "";
        _f.Http.Respond = call =>
        {
            if (call.Host != Host) return null;
            if (call.Path == "/api/sessions/live" && call.Method == "GET")
                return FakeProviderHttp.Json(new
                {
                    name = "live",
                    status = WahaStatus.Working,
                    me = new { id = $"{a.ExternalId}@c.us", pushName = "Mei Ling" },
                    config = new { webhooks = new[] { new { url = ourHook, events = WahaWhatsAppClient.Events } } },
                });
            return FakeProviderHttp.Json(new { name = "live", status = WahaStatus.Working });
        };
        // learn our own webhook URL from a read, then claim it is the one already configured
        var first = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/accounts/{a.Id}/waha/status");
        ourHook = first.GetProperty("webhook_url").GetString()!;
        _f.Http.Reset();
        _f.Http.Respond = call =>
        {
            if (call.Host != Host) return null;
            if (call.Path == "/api/sessions/live" && call.Method == "GET")
                return FakeProviderHttp.Json(new
                {
                    name = "live",
                    status = WahaStatus.Working,
                    me = new { id = $"{a.ExternalId}@c.us", pushName = "Mei Ling" },
                    config = new { webhooks = new[] { new { url = ourHook, events = WahaWhatsAppClient.Events } } },
                });
            return FakeProviderHttp.Json(new { name = "live", status = WahaStatus.Working });
        };
        var state = await (await c.PostAsJsonAsync($"/api/whatsapp/accounts/{a.Id}/waha/start", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(state.GetProperty("working").GetBoolean());
        // only reads — no PUT, no POST /start, nothing that could drop the connection
        Assert.All(_f.Http.Calls.Where(x => x.Host == Host), call => Assert.Equal("GET", call.Method));
    }

    [Fact]
    public async Task Waha_waits_for_the_socket_before_spending_a_pairing_code()
    {
        var a = Add(Row("settle"));
        var c = await Authed();
        var reads = 0;
        _f.Http.Respond = call =>
        {
            if (call.Host != Host) return null;
            if (call.Path == "/api/sessions/settle" && call.Method == "GET")
                // a session WAHA has just created is STARTING: no socket, nothing to issue yet
                return FakeProviderHttp.Json(new
                {
                    name = "settle",
                    status = ++reads <= 2 ? WahaStatus.Starting : WahaStatus.ScanQrCode,
                });
            if (call.Path.EndsWith("/auth/qr")) return FakeProviderHttp.Json(new { mimetype = "image/png", data = "UQ==" });
            if (call.Path.EndsWith("/auth/request-code")) return FakeProviderHttp.Json(new { code = "ABCD-1234" });
            return FakeProviderHttp.Json(new { name = "settle", status = WahaStatus.Starting });
        };
        var state = await (await c.PostAsJsonAsync($"/api/whatsapp/accounts/{a.Id}/waha/start",
            new { phone = "+60123456789" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ABCD-1234", state.GetProperty("pairing_code").GetString());
        // WhatsApp rate-limits codes PER NUMBER: exactly one attempt is ever spent
        Assert.Single(_f.Http.To(Host, "/auth/request-code"));
        Assert.True(reads >= 3, $"the status was only read {reads} time(s) — it never waited for the socket");
    }

    [Fact]
    public async Task Waha_passkey_confirmation_is_a_number_to_confirm_not_a_code_to_type()
    {
        var a = Add(Row("passkey"));
        var c = await Authed();
        _f.Http.Respond = call => call.Host == Host && call.Method == "GET"
            ? FakeProviderHttp.Json(new
            {
                name = "passkey",
                status = WahaStatus.PasskeyConfirmationRequired,
                data = new { code = "42" },
            })
            : call.Host == Host ? FakeProviderHttp.Json(new { name = "passkey", status = WahaStatus.PasskeyRequired }) : null;
        var state = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/accounts/{a.Id}/waha/status");
        Assert.Equal("42", state.GetProperty("confirm_code").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("pairing_code").ValueKind);
        // and it is PROGRESS: health must not move for it
        Assert.True(state.GetProperty("transient").GetBoolean());
        Assert.Equal(Health.Unknown, Account(a.Id).HealthStatus);
    }

    [Fact]
    public async Task Waha_rebases_its_own_localhost_media_url_onto_the_configured_base()
    {
        // a WAHA started without WAHA_BASE_URL advertises media on ITS OWN address
        var home = Uri.UnescapeDataString(BaseUrl);
        Assert.Equal($"{home}/api/files/x.jpg",
            WahaWhatsAppClient.Rebase(new Uri("http://localhost:3000/api/files/x.jpg"), home).ToString());
        // a link that is already right is untouched, and so is anything that is not WAHA's
        Assert.Equal($"{home}/api/files/y.jpg",
            WahaWhatsAppClient.Rebase(new Uri($"{home}/api/files/y.jpg"), home).ToString());
        Assert.Equal("https://cdn.example.test/z.jpg",
            WahaWhatsAppClient.Rebase(new Uri("https://cdn.example.test/z.jpg"), home).ToString());
    }

    [Fact]
    public async Task Two_inboxes_cannot_share_one_waha_session()
    {
        var a = Add(Row("shared-session"));
        var c = await Authed();
        var res = await c.PostAsJsonAsync("/api/whatsapp/accounts/waha", new
        {
            name = "Second inbox, same session",
            base_url = BaseUrl,
            session = "shared-session",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var detail = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.Contains(a.Name, detail);
        // a different session name on the same server is fine
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/whatsapp/accounts/waha", new
        {
            name = "Second inbox, own session",
            base_url = BaseUrl,
            session = "shared-session-2",
        })).StatusCode);
    }

    [Fact]
    public async Task Waha_ignores_a_delivery_for_another_session_on_the_same_server()
    {
        var a = Add(Row("mine", me: Digits()));
        FakeWaha(me: a.ExternalId);
        var from = Digits();
        // correctly signed with OUR key, but WAHA ran it for a different session
        var evt = Inbound(Row("not-mine"), from, $"false_{from}@c.us_OTHER", "hello");
        (await Post(a, evt)).EnsureSuccessStatusCode();
        Assert.Null(Thread(from));
    }

    [Fact]
    public async Task Waha_status_reports_a_transient_state_as_progress_not_as_an_error()
    {
        var a = Add(Row("transient"));
        var c = await Authed();
        FakeWaha(status: WahaStatus.Starting);
        var state = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/accounts/{a.Id}/waha/status");
        Assert.Equal(WahaStatus.Starting, state.GetProperty("status").GetString());
        Assert.True(state.GetProperty("transient").GetBoolean());
        Assert.False(state.GetProperty("working").GetBoolean());
        Assert.True(state.GetProperty("reachable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("detail").ValueKind);   // nothing is wrong
        // health is untouched by a transient state
        Assert.Equal(Health.Unknown, Account(a.Id).HealthStatus);

        // and WAHA itself being unreachable says exactly that, and is not "working"
        _f.Http.Respond = _ => throw new HttpRequestException("connection refused");
        var down = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/accounts/{a.Id}/waha/status");
        Assert.False(down.GetProperty("reachable").GetBoolean());
        Assert.Contains("WAHA", down.GetProperty("detail").GetString()!);
    }

    // =====================================================================================
    // 7. the client itself refuses cleanly when nothing is configured
    // =====================================================================================

    [Fact]
    public async Task Waha_client_calls_refuse_cleanly_when_nothing_is_configured()
    {
        var http = new FakeProviderHttp();
        http.Respond = _ => throw new HttpRequestException("there is no WAHA here");
        var client = new WahaWhatsAppClient(http);
        var blank = Row("blank", baseUrl: "", secret: "", key: "");
        Assert.False(client.IsConfigured(blank));

        var text = await client.SendTextAsync(blank, "60123456789", "hi");
        Assert.False(text.Ok);
        Assert.Contains("not configured", text.Detail);
        Assert.False((await client.UploadMediaAsync(blank, FakeProviderHttp.Jpeg, "image/jpeg", "a.jpg")).Ok);
        Assert.Equal(WahaStatus.Unknown, (await client.StatusAsync(blank)).Status);
        Assert.Null(await client.ProbeBspAsync(blank));
        Assert.False((await client.GetPhoneNumberAsync(blank)).Ok);
        Assert.Equal("", await client.QrAsync(blank));
        // nothing was dialled, and nothing threw
        Assert.Empty(http.Calls);
    }
}

/// <summary>Its own database: the sandbox rule only means anything when NOTHING is configured.</summary>
public sealed class WahaSandboxFactory : WahaFactory { }

/// <summary>
/// The sandbox rule for WAHA (AGENTS.md: "Sandbox must never raise. With no credentials,
/// sends store with status=\"sandbox\""), in a database with no other WhatsApp number to
/// fail over to.
/// </summary>
public class WahaSandboxTests : IClassFixture<WahaSandboxFactory>
{
    private readonly WahaSandboxFactory _f;

    public WahaSandboxTests(WahaSandboxFactory f)
    {
        _f = f;
        _f.Http.Reset();
    }

    private async Task<HttpClient> Authed()
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
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

    [Fact]
    public async Task Waha_with_no_credentials_stores_sends_as_sandbox_and_never_raises()
    {
        // an inbox someone started but never pointed at a WAHA server
        var a = InDb(db =>
        {
            var row = new ChannelAccount
            {
                Channel = ChannelIds.WhatsApp, Provider = Providers.Waha, Name = "WAHA blank",
                CreatedAt = DateTime.UtcNow,
                Config = new Dictionary<string, object?> { [WahaConfig.SessionKey] = "blank" },
            };
            db.ChannelAccounts.Add(row);
            db.SaveChanges();
            return row;
        });
        Assert.False(WhatsAppProviders.Configured(a));
        var key = "60188000222";
        InDb(db =>
        {
            var thread = new Domain.Thread
            {
                Channel = ChannelIds.WhatsApp, Key = key, AccountId = a.Id, Status = ThreadStatus.Open,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Threads.Add(thread);
            db.SaveChanges();
            // the customer wrote first, so the cold-outbound guard is not what we are testing
            db.Messages.Add(new Message
            {
                ThreadId = thread.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                Text = "hi", Status = "received", CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });
        _f.Http.Respond = _ => throw new HttpRequestException("there is no WAHA here");

        var c = await Authed();
        var res = await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "Demo reply" });
        res.EnsureSuccessStatusCode();
        Assert.Equal("sandbox", InDb(db => db.Messages.AsNoTracking()
            .Where(m => m.Thread!.Key == key).OrderBy(m => m.Id).Last()).Status);
        Assert.Empty(_f.Http.Calls);

        // the pairing panel is honest about it instead of throwing
        var state = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/accounts/{a.Id}/waha/status");
        Assert.Equal(WahaStatus.Unknown, state.GetProperty("status").GetString());
        Assert.Contains("base URL", state.GetProperty("detail").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync($"/api/whatsapp/accounts/{a.Id}/waha/start", new { })).StatusCode);
    }
}

/// <summary>Its own database again: here a WAHA number is the ONLY WhatsApp inbox.</summary>
public sealed class WahaSoloFactory : WahaFactory { }

/// <summary>
/// An organization whose only WhatsApp number is a WAHA one. Templates cannot route to an
/// official number because there is none, so the send falls back to free text (the Baileys
/// rule) instead of leaving a failed row.
/// </summary>
public class WahaSoloTests : IClassFixture<WahaSoloFactory>
{
    private readonly WahaSoloFactory _f;

    public WahaSoloTests(WahaSoloFactory f)
    {
        _f = f;
        _f.Http.Reset();
    }

    private async Task<HttpClient> Authed()
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
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

    /// <summary>A WAHA number as the ONLY WhatsApp inbox: a template has nowhere official to
    /// go, so it is rendered as free text — exactly as on Baileys.</summary>
    [Fact]
    public async Task A_template_on_the_only_number_being_waha_is_rendered_as_text()
    {
        var a = InDb(db =>
        {
            var row = new ChannelAccount
            {
                Channel = ChannelIds.WhatsApp, Provider = Providers.Waha, Name = "WAHA solo",
                AccessToken = "k", AppSecret = new string('a', 64), CreatedAt = DateTime.UtcNow,
                HealthStatus = Health.Healthy,
                Config = new Dictionary<string, object?>
                {
                    [WahaConfig.BaseUrlKey] = "http://waha.test:3000",
                    [WahaConfig.SessionKey] = "solo",
                },
            };
            db.ChannelAccounts.Add(row);
            db.SaveChanges();
            return row;
        });
        var key = "60199000111";
        InDb(db =>
        {
            var thread = new Domain.Thread
            {
                Channel = ChannelIds.WhatsApp, Key = key, AccountId = a.Id, Status = ThreadStatus.Open,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Threads.Add(thread);
            db.SaveChanges();
            db.Messages.Add(new Message
            {
                ThreadId = thread.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                Text = "hi", Status = "received", CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });
        _f.Http.Respond = call => call.Host == "waha.test"
            ? FakeProviderHttp.Json(new { id = "false_x@c.us_TPL", timestamp = 1, fromMe = true })
            : null;

        var c = await Authed();
        var list = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/templates?account_id={a.Id}");
        Assert.True(list.GetProperty("sandbox").GetBoolean());
        var known = list.GetProperty("templates").EnumerateArray().First().GetProperty("name").GetString();
        (await c.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send-template",
            new { name = known, language = "en", variables = new[] { "Aisyah", "#1042" } }))
            .EnsureSuccessStatusCode();
        // free text through sendText — never a template call, and never a failed row
        Assert.Single(_f.Http.To("waha.test", "/api/sendText"));
        Assert.Empty(_f.Http.Calls.Where(x => x.Path.Contains("template")));
        var sent = InDb(db => db.Messages.AsNoTracking()
            .Where(m => m.Thread!.Key == key).OrderBy(m => m.Id).Last());
        Assert.Equal("sent", sent.Status);
    }
}
