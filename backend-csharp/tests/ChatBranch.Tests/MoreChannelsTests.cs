using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Tests;

/// <summary>
/// LINE, Viber, SMS (Twilio), WeChat, TikTok, Douyin and Zalo end to end through
/// the API host with every provider faked at the HTTP level: signed webhooks
/// (good / bad / missing / no secret), inbound → thread + message (+ contact by
/// phone for SMS only), invite codes, opt-out signals, media off the ack, sends
/// through OutboundSender, reply windows, health verdicts and the accounts API.
/// </summary>
public class MoreChannelsTests : IClassFixture<MoreChannelsFactory>
{
    private readonly MoreChannelsFactory _f;

    public MoreChannelsTests(MoreChannelsFactory f)
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

    private ChannelAccount Add(string channel, Action<ChannelAccount> setup)
    {
        var a = new ChannelAccount { Channel = channel, Name = $"{channel} {Guid.NewGuid():N}"[..20], CreatedAt = DateTime.UtcNow };
        setup(a);
        return InDb(db =>
        {
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a;
        });
    }

    private ChannelAccount Account(int id) => InDb(db => db.ChannelAccounts.AsNoTracking().First(a => a.Id == id));

    private Domain.Thread? Thread(string channel, string key) =>
        InDb(db => db.Threads.AsNoTracking().FirstOrDefault(t => t.Channel == channel && t.Key == key));

    private List<Message> Messages(string channel, string key) => InDb(db => db.Messages.AsNoTracking()
        .Where(m => m.Thread!.Channel == channel && m.Thread.Key == key).OrderBy(m => m.Id).ToList());

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met in time");
            await Task.Delay(50);
        }
    }

    private static string U() => Guid.NewGuid().ToString("N")[..10];

    private async Task<HttpResponseMessage> PostSigned(string path, string body, string header, string signature)
    {
        var c = _f.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (header.Length > 0) req.Headers.TryAddWithoutValidation(header, signature);
        return await c.SendAsync(req);
    }

    private Task<HttpResponseMessage> PostLine(ChannelAccount a, string body, string? secret = null) =>
        PostSigned($"/api/messaging/webhook/line/{a.Id}", body, LineTransport.SignatureHeader,
            LineTransport.Sign(secret ?? a.AppSecret, Encoding.UTF8.GetBytes(body)));

    private static string LineText(string userId, string msgId, string text, string replyToken = "rt-1") =>
        JsonSerializer.Serialize(new
        {
            destination = "Ubot0001",
            events = new object[]
            {
                new
                {
                    type = "message", replyToken, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    source = new { type = "user", userId },
                    message = new { type = "text", id = msgId, text },
                },
            },
        });

    // ---- LINE ------------------------------------------------------------------------

    [Fact]
    public async Task Line_webhook_refuses_unsigned_traffic_and_unconfigured_accounts()
    {
        var noSecret = Add(ChannelIds.Line, a => a.AccessToken = "line-token");
        var res = await PostSigned($"/api/messaging/webhook/line/{noSecret.Id}", "{}", "", "");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);

        var a = Add(ChannelIds.Line, x => { x.AccessToken = "line-token"; x.AppSecret = "line-secret"; });
        var body = LineText("U" + U(), "m" + U(), "hi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSigned($"/api/messaging/webhook/line/{a.Id}", body, "", "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostLine(a, body, secret: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostSigned("/api/messaging/webhook/line/999999", body,
            LineTransport.SignatureHeader, "x")).StatusCode);
        Assert.Empty(InDb(db => db.Threads.Where(t => t.Channel == ChannelIds.Line && t.AccountId == a.Id).ToList()));
    }

    [Fact]
    public async Task Line_inbound_creates_the_thread_names_it_and_replies_with_the_fresh_token()
    {
        var a = Add(ChannelIds.Line, x => { x.AccessToken = "line-token"; x.AppSecret = "line-secret"; });
        var user = "U" + U();
        var res = await PostLine(a, LineText(user, "m" + U(), "Hello shop", replyToken: "reply-abc"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var thread = Thread(ChannelIds.Line, user)!;
        Assert.Equal(a.Id, thread.AccountId);
        Assert.Null(thread.ContactId); // LINE ids are opaque — never auto-linked
        Assert.Equal("Hello shop", Messages(ChannelIds.Line, user).Single().Text);
        Assert.Equal("Ubot0001", Account(a.Id).ExternalId);  // learned from `destination`
        Assert.Equal(Health.Healthy, Account(a.Id).HealthStatus);
        await WaitFor(() => Thread(ChannelIds.Line, user)!.Name == "Aiko");

        var c = await Authed();
        var send = await c.PostAsJsonAsync($"/api/messaging/threads/line/{user}/send", new { text = "Hi Aiko!" });
        send.EnsureSuccessStatusCode();
        var msg = (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message");
        Assert.Equal("sent", msg.GetProperty("status").GetString());
        var reply = Assert.Single(_f.Http.To("api.line.me", "/message/reply"));
        Assert.Equal("reply-abc", reply.Json.GetProperty("replyToken").GetString());
        Assert.Equal("Bearer line-token", reply.Header("Authorization"));
        Assert.Empty(_f.Http.To("api.line.me", "/message/push"));

        // the token is single use: the next message is a push
        var again = await c.PostAsJsonAsync($"/api/messaging/threads/line/{user}/send", new { text = "Anything else?" });
        again.EnsureSuccessStatusCode();
        var push = Assert.Single(_f.Http.To("api.line.me", "/message/push"));
        Assert.Equal(user, push.Json.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Line_media_downloads_off_the_ack_and_unfollow_opts_out()
    {
        var a = Add(ChannelIds.Line, x => { x.AccessToken = "line-token"; x.AppSecret = "line-secret"; });
        var user = "U" + U();
        var body = JsonSerializer.Serialize(new
        {
            events = new object[]
            {
                new { type = "message", replyToken = "r", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                      source = new { type = "user", userId = user },
                      message = new { type = "image", id = "img-" + U(), contentProvider = new { type = "line" } } },
            },
        });
        (await PostLine(a, body)).EnsureSuccessStatusCode();
        await WaitFor(() => Messages(ChannelIds.Line, user).Single().MediaPath is { Length: > 0 });
        Assert.Equal("image", Messages(ChannelIds.Line, user).Single().MediaType);

        var unfollow = JsonSerializer.Serialize(new
        {
            events = new object[] { new { type = "unfollow", timestamp = 1, source = new { type = "user", userId = user } } },
        });
        (await PostLine(a, unfollow)).EnsureSuccessStatusCode();
        Assert.True(Thread(ChannelIds.Line, user)!.OptedOut);
    }

    // ---- Viber -----------------------------------------------------------------------

    private Task<HttpResponseMessage> PostViber(ChannelAccount a, object payload)
    {
        var body = JsonSerializer.Serialize(payload);
        return PostSigned($"/api/messaging/webhook/viber/{a.Id}", body, ViberTransport.SignatureHeader,
            ViberTransport.Sign(a.AccessToken, Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task Viber_invite_context_rides_on_the_first_message_and_receipts_tick()
    {
        var a = Add(ChannelIds.Viber, x => x.AccessToken = "viber-token");
        var viberId = "vb" + U() + "/x+=";
        var key = ViberTransport.KeyFor(viberId);

        var bad = await PostSigned($"/api/messaging/webhook/viber/{a.Id}", "{\"event\":\"webhook\"}",
            ViberTransport.SignatureHeader, "deadbeef");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        (await PostViber(a, new { @event = "webhook", timestamp = 1 })).EnsureSuccessStatusCode();

        (await PostViber(a, new
        {
            @event = "conversation_started", timestamp = 1700000000000, message_token = 1, type = "open",
            context = "INV-77", user = new { id = viberId, name = "Mai" }, subscribed = false,
        })).EnsureSuccessStatusCode();
        (await PostViber(a, new
        {
            @event = "message", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), message_token = 111,
            sender = new { id = viberId, name = "Mai" }, message = new { type = "text", text = "hello" },
        })).EnsureSuccessStatusCode();
        (await PostViber(a, new
        {
            @event = "message", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), message_token = 112,
            sender = new { id = viberId, name = "Mai" }, message = new { type = "text", text = "second" },
        })).EnsureSuccessStatusCode();

        var inbound = Messages(ChannelIds.Viber, key).Where(m => m.Direction == MessageDirection.In).ToList();
        Assert.Equal(new[] { "[ref:INV-77] hello", "second" }, inbound.Select(m => m.Text));
        Assert.Equal("Mai", Thread(ChannelIds.Viber, key)!.Name);

        var c = await Authed();
        var send = await c.PostAsJsonAsync($"/api/messaging/threads/viber/{key}/send", new { text = "Welcome!" });
        send.EnsureSuccessStatusCode();
        var call = Assert.Single(_f.Http.To("chatapi.viber.com", "/send_message"));
        Assert.Equal(viberId, call.Json.GetProperty("receiver").GetString()); // the real id, not the URL-safe key
        Assert.Equal("viber-token", call.Header("X-Viber-Auth-Token"));
        var token = Messages(ChannelIds.Viber, key).Last().ExternalId;
        (await PostViber(a, new { @event = "seen", timestamp = 1, message_token = long.Parse(token), user_id = viberId }))
            .EnsureSuccessStatusCode();
        Assert.Equal("read", Messages(ChannelIds.Viber, key).Last().Status);
        // forward-only: a late "delivered" never downgrades
        (await PostViber(a, new { @event = "delivered", timestamp = 1, message_token = long.Parse(token), user_id = viberId }))
            .EnsureSuccessStatusCode();
        Assert.Equal("read", Messages(ChannelIds.Viber, key).Last().Status);

        (await PostViber(a, new { @event = "unsubscribed", timestamp = 1, user_id = viberId })).EnsureSuccessStatusCode();
        Assert.True(Thread(ChannelIds.Viber, key)!.OptedOut);
    }

    // ---- SMS (Twilio) ----------------------------------------------------------------

    private ChannelAccount SmsAccount() => Add(ChannelIds.Sms, x =>
    {
        x.AccessToken = "twilio-auth";
        x.ExternalId = "+15550001111";
        x.Config = new() { ["account_sid"] = "AC" + new string('a', 32), ["from_number"] = "+15550001111" };
    });

    private async Task<HttpResponseMessage> PostSms(ChannelAccount a, Dictionary<string, string> form, string? token = null)
    {
        var c = _f.CreateClient();
        var url = $"http://localhost/api/messaging/webhook/sms/{a.Id}";
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/messaging/webhook/sms/{a.Id}")
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.TryAddWithoutValidation(TwilioSmsTransport.SignatureHeader,
            TwilioSmsTransport.Sign(token ?? a.AccessToken, url, form.ToList()));
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task Sms_inbound_links_the_contact_by_phone_and_mirrors_twilio_opt_out()
    {
        var a = SmsAccount();
        var digits = "1555" + Random.Shared.Next(1000000, 9999999);
        var contactId = InDb(db =>
        {
            var c = new Contact { Name = "Sam", Phone = "+" + digits, Status = "active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            return c.Id;
        });
        var form = new Dictionary<string, string>
        {
            ["MessageSid"] = "SM" + U(), ["AccountSid"] = "AC" + new string('a', 32), ["From"] = "+" + digits,
            ["To"] = "+15550001111", ["Body"] = "Is my order ready?", ["NumMedia"] = "0",
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSms(a, form, token: "wrong")).StatusCode);
        var ok = await PostSms(a, form);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("<Response>", await ok.Content.ReadAsStringAsync());
        var thread = Thread(ChannelIds.Sms, digits)!;
        Assert.Equal(contactId, thread.ContactId);              // SMS IS phone-keyed
        Assert.Equal("Is my order ready?", Messages(ChannelIds.Sms, digits).Single().Text);
        // redelivery is a no-op
        (await PostSms(a, form)).EnsureSuccessStatusCode();
        Assert.Single(Messages(ChannelIds.Sms, digits));

        var stop = new Dictionary<string, string>(form) { ["MessageSid"] = "SM" + U(), ["Body"] = "CANCEL", ["OptOutType"] = "STOP" };
        (await PostSms(a, stop)).EnsureSuccessStatusCode();
        Assert.True(Thread(ChannelIds.Sms, digits)!.OptedOut);
    }

    [Fact]
    public async Task Sms_outbound_goes_through_twilio_and_failures_are_classified()
    {
        var a = SmsAccount();
        var digits = "1666" + Random.Shared.Next(1000000, 9999999);
        await PostSms(a, new Dictionary<string, string>
        {
            ["MessageSid"] = "SM" + U(), ["From"] = "+" + digits, ["To"] = "+15550001111", ["Body"] = "hi",
        });
        var c = await Authed();
        (await c.PostAsJsonAsync($"/api/messaging/threads/sms/{digits}/send", new { text = "Ready at 5pm." }))
            .EnsureSuccessStatusCode();
        var call = Assert.Single(_f.Http.To("api.twilio.com", "/Messages.json"));
        var form = call.Body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Assert.Equal("+" + digits, form["To"]);
        Assert.Equal("+15550001111", form["From"]);
        Assert.Equal("Ready at 5pm.", form["Body"]);
        Assert.StartsWith("Basic ", call.Header("Authorization"));
        Assert.Equal("sent", Messages(ChannelIds.Sms, digits).Last().Status);

        // 21610 = the recipient unsubscribed at the carrier: failed, opted out, account health untouched
        _f.Http.Respond = r => r.Path.EndsWith("/Messages.json")
            ? FakeProviderHttp.Json(new { code = 21610, message = "Attempt to send to unsubscribed recipient", status = 400 }, HttpStatusCode.BadRequest)
            : null;
        (await c.PostAsJsonAsync($"/api/messaging/threads/sms/{digits}/send", new { text = "Hello?" })).EnsureSuccessStatusCode();
        var failed = Messages(ChannelIds.Sms, digits).Where(m => m.Status == "failed").Last();
        Assert.StartsWith("21610", failed.Error);
        Assert.True(Thread(ChannelIds.Sms, digits)!.OptedOut);
        Assert.Equal(Health.Healthy, Account(a.Id).HealthStatus);

        // 20003 = Twilio refused the credentials: the account is disconnected
        _f.Http.Respond = r => r.Path.EndsWith("/Messages.json")
            ? FakeProviderHttp.Json(new { code = 20003, message = "Authenticate", status = 401 }, HttpStatusCode.Unauthorized)
            : null;
        (await c.PostAsJsonAsync($"/api/messaging/threads/sms/{digits}/send", new { text = "x", force = true }))
            .EnsureSuccessStatusCode();
        Assert.Equal(Health.Disconnected, Account(a.Id).HealthStatus);
    }

    // ---- WeChat ----------------------------------------------------------------------

    private const string AesKey = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

    private ChannelAccount WeChatAccount(bool safe = false) => Add(ChannelIds.WeChat, x =>
    {
        x.AppSecret = "wx-secret";
        x.Config = new() { ["app_id"] = "wx1234567890abcdef", ["token"] = "wxtoken" };
        if (safe) x.Config["encoding_aes_key"] = AesKey;
    });

    private async Task<HttpResponseMessage> PostWeChat(ChannelAccount a, string xml, long? ts = null, string? nonce = null,
        bool aes = false)
    {
        var timestamp = (ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        nonce ??= U();
        var query = $"signature={WeChatCrypto.Signature("wxtoken", timestamp, nonce)}&timestamp={timestamp}&nonce={nonce}&openid=x";
        var body = xml;
        if (aes)
        {
            var enc = WeChatCrypto.Encrypt(AesKey, xml, "wx1234567890abcdef");
            body = $"<xml><ToUserName><![CDATA[gh_1]]></ToUserName><Encrypt><![CDATA[{enc}]]></Encrypt></xml>";
            query += $"&encrypt_type=aes&msg_signature={WeChatCrypto.Signature("wxtoken", timestamp, nonce, enc)}";
        }
        return await _f.CreateClient().PostAsync($"/api/messaging/webhook/wechat/{a.Id}?{query}",
            new StringContent(body, Encoding.UTF8, "text/xml"));
    }

    private static string WxText(string openId, string text, string msgId) =>
        $"<xml><ToUserName><![CDATA[gh_shop]]></ToUserName><FromUserName><![CDATA[{openId}]]></FromUserName>" +
        $"<CreateTime>{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}</CreateTime><MsgType><![CDATA[text]]></MsgType>" +
        $"<Content><![CDATA[{text}]]></Content><MsgId>{msgId}</MsgId></xml>";

    [Fact]
    public async Task WeChat_url_verification_and_signed_pushes()
    {
        var a = WeChatAccount();
        var c = _f.CreateClient();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var ok = await c.GetAsync($"/api/messaging/webhook/wechat/{a.Id}?signature={WeChatCrypto.Signature("wxtoken", ts, "n1")}&timestamp={ts}&nonce=n1&echostr=ECHO123");
        Assert.Equal("ECHO123", await ok.Content.ReadAsStringAsync());
        var bad = await c.GetAsync($"/api/messaging/webhook/wechat/{a.Id}?signature=abc&timestamp={ts}&nonce=n1&echostr=ECHO123");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var openId = "o" + U();
        Assert.Equal("success", await (await PostWeChat(a, WxText(openId, "你好", "1" + Random.Shared.Next()))).Content.ReadAsStringAsync());
        Assert.Equal("你好", Messages(ChannelIds.WeChat, openId).Single().Text);
        Assert.Equal("gh_shop", Account(a.Id).ExternalId);

        // a stale timestamp (replayed URL) is refused; a reused nonce is acked but never processed
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PostWeChat(a, WxText(openId, "replayed", "2"), ts: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds())).StatusCode);
        var nonce = U();
        await PostWeChat(a, WxText(openId, "first", "3" + Random.Shared.Next()), nonce: nonce);
        await PostWeChat(a, WxText(openId, "forged body", "4" + Random.Shared.Next()), nonce: nonce);
        Assert.DoesNotContain(Messages(ChannelIds.WeChat, openId), m => m.Text == "forged body");
    }

    [Fact]
    public async Task WeChat_scene_qr_safe_mode_window_and_unsubscribe()
    {
        var a = WeChatAccount(safe: true);
        var openId = "o" + U();
        var subscribe = $"<xml><ToUserName>gh_shop</ToUserName><FromUserName>{openId}</FromUserName>" +
                        $"<CreateTime>{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}</CreateTime><MsgType>event</MsgType>" +
                        "<Event>subscribe</Event><EventKey>qrscene_INV-9</EventKey></xml>";
        // safe mode: a plaintext post is refused once the account has an EncodingAESKey… only if it claims aes
        (await PostWeChat(a, subscribe, aes: true)).EnsureSuccessStatusCode();
        Assert.Equal("[ref:INV-9] [followed the account]", Messages(ChannelIds.WeChat, openId).Single().Text);

        var c = await Authed();
        (await c.PostAsJsonAsync($"/api/messaging/threads/wechat/{openId}/send", new { text = "欢迎!" })).EnsureSuccessStatusCode();
        var send = Assert.Single(_f.Http.To("api.weixin.qq.com", "/message/custom/send"));
        Assert.Contains("access_token=WXTOKEN-1", send.Url.Query);
        Assert.Contains("欢迎!", send.Body); // raw UTF-8, not \u escapes
        Assert.Equal(openId, send.Json.GetProperty("touser").GetString());

        // outside WeChat's 48 hours: refused before WeChat is ever called
        InDb(db =>
        {
            foreach (var m in db.Messages.Where(m => m.Thread!.Channel == ChannelIds.WeChat && m.Thread.Key == openId))
                m.CreatedAt = DateTime.UtcNow.AddHours(-50);
            return db.SaveChanges();
        });
        _f.Http.Reset();
        var late = await c.PostAsJsonAsync($"/api/messaging/threads/wechat/{openId}/send", new { text = "still there?" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("48-hour", (await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Empty(_f.Http.To("api.weixin.qq.com", "/message/custom/send"));
        var detail = await c.GetFromJsonAsync<JsonElement>($"/api/messaging/threads/wechat/{openId}");
        Assert.False(detail.GetProperty("window_open").GetBoolean());

        var unsub = $"<xml><ToUserName>gh_shop</ToUserName><FromUserName>{openId}</FromUserName>" +
                    $"<CreateTime>{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}</CreateTime><MsgType>event</MsgType><Event>unsubscribe</Event></xml>";
        (await PostWeChat(a, unsub, aes: true)).EnsureSuccessStatusCode();
        Assert.True(Thread(ChannelIds.WeChat, openId)!.OptedOut);

        // a safe-mode body whose msg_signature doesn't match is refused
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var n = U();
        var forged = await _f.CreateClient().PostAsync(
            $"/api/messaging/webhook/wechat/{a.Id}?signature={WeChatCrypto.Signature("wxtoken", ts, n)}&timestamp={ts}&nonce={n}&encrypt_type=aes&msg_signature=00",
            new StringContent("<xml><Encrypt>abc</Encrypt></xml>", Encoding.UTF8, "text/xml"));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    // ---- Zalo, TikTok, Douyin -----------------------------------------------------------

    private ChannelAccount ZaloAccount() => Add(ChannelIds.Zalo, x =>
    {
        x.AccessToken = "zalo-old";
        x.AppSecret = "zalo-app-secret";
        x.Config = new() { ["app_id"] = "12345", ["oa_secret_key"] = "oa-secret", ["refresh_token"] = "REFRESH-OLD" };
    });

    private Task<HttpResponseMessage> PostZalo(ChannelAccount a, object payload, string? oaSecret = null)
    {
        var body = JsonSerializer.Serialize(payload);
        var ts = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("timestamp").GetString()!;
        return PostSigned($"/api/messaging/webhook/zalo/{a.Id}", body, ZaloTransport.SignatureHeader,
            "mac=" + ZaloTransport.Mac("12345", body, ts, oaSecret ?? "oa-secret"));
    }

    [Fact]
    public async Task Zalo_inbound_refreshes_an_expired_token_and_enforces_the_7_day_window()
    {
        var a = ZaloAccount();
        var user = "zu" + U();
        var payload = new
        {
            app_id = "12345", event_name = "user_send_text", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            sender = new { id = user }, recipient = new { id = "OA-777" }, message = new { text = "Xin chào", msg_id = "zm" + U() },
        };
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostZalo(a, payload, oaSecret: "wrong")).StatusCode);
        (await PostZalo(a, payload)).EnsureSuccessStatusCode();
        Assert.Equal("Xin chào", Messages(ChannelIds.Zalo, user).Single().Text);
        await WaitFor(() => Thread(ChannelIds.Zalo, user)!.Name == "Linh");

        // the first send meets an expired token: refreshed once, the new pair saved, the send retried
        var first = true;
        _f.Http.Respond = r =>
        {
            if (r.Path == "/v3.0/oa/message/cs" && first)
            {
                first = false;
                return FakeProviderHttp.Json(new { error = -216, message = "Access token is invalid" });
            }
            return null;
        };
        var c = await Authed();
        (await c.PostAsJsonAsync($"/api/messaging/threads/zalo/{user}/send", new { text = "Cảm ơn!" })).EnsureSuccessStatusCode();
        Assert.Equal("sent", Messages(ChannelIds.Zalo, user).Last().Status);
        var refreshed = Account(a.Id);
        Assert.Equal("ZALO-NEW", refreshed.AccessToken);
        Assert.Equal("REFRESH-NEW", MoreChannels.Cfg(refreshed, "refresh_token"));
        Assert.Equal("ZALO-NEW", _f.Http.To("openapi.zalo.me", "/message/cs").Last().Header("access_token"));

        // Zalo's own -230 (no interaction in 7 days) is a window refusal: failed row, health untouched
        _f.Http.Respond = r => r.Path == "/v3.0/oa/message/cs"
            ? FakeProviderHttp.Json(new { error = -230, message = "User has not interacted" }) : null;
        (await c.PostAsJsonAsync($"/api/messaging/threads/zalo/{user}/send", new { text = "Ping" })).EnsureSuccessStatusCode();
        Assert.Contains("7-day", Messages(ChannelIds.Zalo, user).Last().Text);
        Assert.Equal(Health.Healthy, Account(a.Id).HealthStatus);
    }

    [Fact]
    public async Task TikTok_and_Douyin_verify_parse_and_reply_in_window()
    {
        var tk = Add(ChannelIds.TikTok, x => { x.AccessToken = "tt-token"; x.AppSecret = "tt-secret"; x.ExternalId = "biz-1"; });
        var conv = "conv-" + U();
        var body = JsonSerializer.Serialize(new
        {
            @event = "message.receive", create_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            content = JsonSerializer.Serialize(new { conversation_id = conv, message_id = "tm-" + U(),
                from_user = new { id = "user-9", display_name = "Ana" }, text = new { body = "price?" } }),
        });
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig = $"t={t},s={TikTokTransport.Sign("tt-secret", t, Encoding.UTF8.GetBytes(body))}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSigned($"/api/messaging/webhook/tiktok/{tk.Id}", body,
            TikTokTransport.SignatureHeader, $"t={t},s=00")).StatusCode);
        (await PostSigned($"/api/messaging/webhook/tiktok/{tk.Id}", body, TikTokTransport.SignatureHeader, sig)).EnsureSuccessStatusCode();
        Assert.Equal("price?", Messages(ChannelIds.TikTok, conv).Single().Text);
        var c = await Authed();
        (await c.PostAsJsonAsync($"/api/messaging/threads/tiktok/{conv}/send", new { text = "RM20" })).EnsureSuccessStatusCode();
        var tcall = Assert.Single(_f.Http.To("business-api.tiktok.com", "/message/send/"));
        Assert.Equal("biz-1", tcall.Json.GetProperty("business_id").GetString());
        Assert.Equal("tt-token", tcall.Header("Access-Token"));

        var dy = Add(ChannelIds.Douyin, x =>
        {
            x.AccessToken = "dy-token"; x.AppSecret = "dy-secret"; x.ExternalId = "ent-1";
            x.Config = new() { ["client_key"] = "ck-1" };
        });
        var verify = "{\"event\":\"verify_webhook\",\"client_key\":\"ck-1\",\"content\":{\"challenge\":12345}}";
        var vres = await PostSigned($"/api/messaging/webhook/douyin/{dy.Id}", verify, DouyinTransport.SignatureHeader,
            DouyinTransport.Sign("dy-secret", Encoding.UTF8.GetBytes(verify)));
        Assert.Equal(12345, (await vres.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSigned($"/api/messaging/webhook/douyin/{dy.Id}", verify,
            DouyinTransport.SignatureHeader, "00")).StatusCode);

        var user = "dyu-" + U();
        var msg = JsonSerializer.Serialize(new
        {
            @event = "im_receive_msg", from_user_id = user, to_user_id = "ent-1", client_key = "ck-1",
            content = JsonSerializer.Serialize(new { conversation_short_id = "c1", server_message_id = "s1",
                message_type = "text", text = "在吗", create_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }),
        });
        (await PostSigned($"/api/messaging/webhook/douyin/{dy.Id}", msg, DouyinTransport.SignatureHeader,
            DouyinTransport.Sign("dy-secret", Encoding.UTF8.GetBytes(msg)))).EnsureSuccessStatusCode();
        Assert.Equal("c1|s1", Messages(ChannelIds.Douyin, user).Single().ExternalId);
        (await c.PostAsJsonAsync($"/api/messaging/threads/douyin/{user}/send", new { text = "在的" })).EnsureSuccessStatusCode();
        var dcall = Assert.Single(_f.Http.To("open.douyin.com", "/im/send/msg/"));
        Assert.Equal(("s1", "c1", user), (dcall.Json.GetProperty("msg_id").GetString(),
            dcall.Json.GetProperty("conversation_id").GetString(), dcall.Json.GetProperty("to_user_id").GetString()));
        Assert.Contains("open_id=ent-1", dcall.Url.Query);
    }

    // ---- sandbox, accounts, health, flows ---------------------------------------------------

    [Fact]
    public void Unconfigured_channels_stay_sandbox_and_never_call_a_provider()
    {
        using var t = new TestDb();
        var scoping = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, scoping);
        var http = new FakeProviderHttp();
        var transports = new ChannelTransports(new IChannelTransport[]
        {
            new LineTransport(http), new ViberTransport(http), new TwilioSmsTransport(http), new WeChatTransport(http),
            new TikTokTransport(http), new DouyinTransport(http), new ZaloTransport(http),
        });
        var sender = new OutboundSender(t.Db, threads, new StubWhatsApp(),
            new ChatBranch.Data.Sidecars.SandboxBaileysGateway(),
            more: new MoreChannelSender(t.Db, scoping, threads, transports));
        foreach (var ch in MoreChannels.All)
        {
            var thread = threads.GetOrCreateThread(ch, "k" + U());
            Assert.False(sender.WouldSendLive(thread));
            Assert.Null(sender.SocialWindowBlock(thread, null, humanAgent: false)); // sandbox never dead-ends
            Assert.Equal("sandbox", sender.Send(thread, "hello", MessageSource.Agent).Status);
        }
        // an account without its credentials is still sandbox
        var half = new ChannelAccount { Channel = ChannelIds.Line, Name = "half", AccessToken = "only-token", CreatedAt = DateTime.UtcNow };
        t.Db.ChannelAccounts.Add(half);
        t.Db.SaveChanges();
        var bound = threads.GetOrCreateThread(ChannelIds.Line, "U-half", accountId: half.Id);
        Assert.Equal("sandbox", sender.Send(bound, "hello", MessageSource.Agent).Status);
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task Sms_threads_are_digits_keyed_and_status_lists_every_channel()
    {
        var c = await Authed();
        var sms = await c.PostAsJsonAsync("/api/messaging/threads", new { channel = "sms", to = "+60 12-777 8888" });
        Assert.Equal("60127778888", (await sms.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString());
        var status = await c.GetFromJsonAsync<JsonElement>("/api/messaging/status");
        Assert.True(status.TryGetProperty("zalo_configured", out _));
    }

    [Fact]
    public async Task Accounts_api_masks_secrets_tests_and_probes()
    {
        var c = await Authed();
        var missing = await c.PostAsJsonAsync("/api/messaging/accounts", new { channel = "line", name = "LINE shop", access_token = "tok" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("channel_secret", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        var badSid = await c.PostAsJsonAsync("/api/messaging/accounts", new { channel = "sms", account_sid = "nope", access_token = "t", from_number = "+15550001111" });
        Assert.Equal(HttpStatusCode.BadRequest, badSid.StatusCode);

        var res = await c.PostAsJsonAsync("/api/messaging/accounts", new
        {
            channel = "line", name = "LINE shop", access_token = "line-access-token-1234", channel_secret = "line-channel-secret-9876",
        });
        res.EnsureSuccessStatusCode();
        var row = await res.Content.ReadFromJsonAsync<JsonElement>();
        var raw = row.GetRawText();
        Assert.DoesNotContain("line-access-token-1234", raw);
        Assert.DoesNotContain("line-channel-secret-9876", raw);
        Assert.Equal("••••1234", row.GetProperty("access_token_masked").GetString());
        Assert.Equal("••••9876", row.GetProperty("channel_secret_masked").GetString());
        Assert.True(row.GetProperty("configured").GetBoolean());
        var id = row.GetProperty("id").GetString();
        Assert.Equal($"/api/messaging/webhook/line/{id}", row.GetProperty("webhook_path").GetString());

        // a masked round-trip keeps the stored secret
        (await c.PatchAsJsonAsync($"/api/messaging/accounts/{id}", new { channel_secret = "••••9876", name = "LINE main" }))
            .EnsureSuccessStatusCode();
        Assert.Equal("line-channel-secret-9876", Account(int.Parse(id!)).AppSecret);

        var test = await c.PostAsJsonAsync($"/api/messaging/accounts/{id}/test", new { });
        test.EnsureSuccessStatusCode();
        Assert.Contains("Shop Bot", (await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal(Health.Healthy, Account(int.Parse(id!)).HealthStatus);

        // the prober: a revoked token disconnects the account
        _f.Http.Respond = r => r.Host == "api.line.me" && r.Path == "/v2/bot/info"
            ? FakeProviderHttp.Json(new { message = "Authentication failed" }, HttpStatusCode.Unauthorized) : null;
        (await c.PostAsync("/api/whatsapp/channels/health", null)).EnsureSuccessStatusCode();
        Assert.Equal(Health.Disconnected, Account(int.Parse(id!)).HealthStatus);

        // register webhook: LINE's endpoint API with the account's own path
        _f.Http.Reset();
        var hook = await c.PostAsJsonAsync($"/api/messaging/accounts/{id}/set-webhook", new { base_url = "https://hooks.example.test" });
        hook.EnsureSuccessStatusCode();
        var put = Assert.Single(_f.Http.To("api.line.me", "/channel/webhook/endpoint"));
        Assert.Equal($"https://hooks.example.test/api/messaging/webhook/line/{id}", put.Json.GetProperty("endpoint").GetString());

        // agents can't create accounts
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/messaging/accounts",
            new { channel = "viber", access_token = "x" })).StatusCode);
        var list = await c.GetFromJsonAsync<JsonElement>("/api/messaging/accounts");
        Assert.Contains(list.GetProperty("accounts").EnumerateArray(), x => x.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Flow_sms_step_queues_a_real_sms_job()
    {
        var a = SmsAccount();
        var digits = "1777" + Random.Shared.Next(1000000, 9999999);
        using var scope = _f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var contact = new Contact { Name = "Flo", Phone = "+" + digits, Status = "active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Contacts.Add(contact);
        var wf = new Workflow
        {
            FlowId = "sms-" + U(), Name = "sms", Active = true, LiveSince = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Nodes = new List<object?>
            {
                new { id = "trg", type = "trigger", data = new { label = "t", config = new { when = "Manual / API" } } },
                new { id = "act", type = "action", data = new { label = "a", config = new { channels = new[] { "SMS" }, script = "Hi {{first_name}}" } } },
            },
            Edges = new List<object?> { new { id = "e1", source = "trg", target = "act" } },
        };
        db.Workflows.Add(wf);
        db.SaveChanges();
        var engine = sp.GetRequiredService<FlowEngine>();
        engine.StartManual(wf, new[] { contact.Id });
        engine.Tick();
        var job = db.OutboundJobs.AsNoTracking().Single(j => j.ContactId == contact.Id);
        Assert.Equal(("sms", digits), (job.Channel, job.ToAddress));

        // only this job in the queue (other tests' debounced AI replies wait elsewhere)
        foreach (var other in db.OutboundJobs.Where(j => j.Status == JobStatus.Queued && j.Id != job.Id).ToList())
            other.Status = JobStatus.Cancelled;
        db.SaveChanges();
        sp.GetRequiredService<QueueService>().Drain(10);
        var call = _f.Http.To("api.twilio.com", "/Messages.json").Single(x => x.Body.Contains(digits));
        Assert.Contains("Hi+Flo", call.Body);
        var thread = Thread(ChannelIds.Sms, digits)!;
        Assert.NotNull(thread.AccountId);
        Assert.Equal(contact.Id, thread.ContactId);
        Assert.Equal("sent", Messages(ChannelIds.Sms, digits).Last().Status);
    }
}
