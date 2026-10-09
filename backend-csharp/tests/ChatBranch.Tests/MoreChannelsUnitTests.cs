using System.Text;
using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Xunit;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Tests;

/// <summary>
/// The pure halves of LINE, Viber, SMS, WeChat, TikTok, Douyin and Zalo:
/// webhook signatures (good / bad / missing, with independently computed
/// vectors where the provider documents one), the AES safe mode, payload
/// parsing, the reply-window registry and the invite-code prefix.
/// </summary>
public class MoreChannelsUnitTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static JsonElement J(string s) => JsonSerializer.Deserialize<JsonElement>(s);

    // ---- signatures ---------------------------------------------------------------

    [Fact]
    public void Line_signature_is_base64_hmac_sha256_of_the_body()
    {
        var body = B("{\"events\":[]}");
        // independently computed (python hmac) for secret "secret"
        Assert.True(LineTransport.VerifySignature("secret", body, "pkK1lVPJPiJ+wPLziRD79xIxohl8AImYM8AEeM7IbzQ="));
        Assert.False(LineTransport.VerifySignature("secret", B("{\"events\":[1]}"), "pkK1lVPJPiJ+wPLziRD79xIxohl8AImYM8AEeM7IbzQ="));
        Assert.False(LineTransport.VerifySignature("other", body, LineTransport.Sign("secret", body)));
        Assert.False(LineTransport.VerifySignature("secret", body, null));
        Assert.False(LineTransport.VerifySignature("secret", body, "not base64 !!"));
        Assert.False(LineTransport.VerifySignature("", body, LineTransport.Sign("secret", body)));
    }

    [Fact]
    public void Viber_signature_is_hex_hmac_sha256_with_the_auth_token()
    {
        var body = B("{\"event\":\"message\"}");
        var sig = ViberTransport.Sign("tok-1", body);
        Assert.Matches("^[0-9a-f]{64}$", sig);
        Assert.True(ViberTransport.VerifySignature("tok-1", body, sig));
        Assert.True(ViberTransport.VerifySignature("tok-1", body, sig.ToUpperInvariant()));
        Assert.False(ViberTransport.VerifySignature("tok-2", body, sig));
        Assert.False(ViberTransport.VerifySignature("tok-1", B("{}"), sig));
        Assert.False(ViberTransport.VerifySignature("tok-1", body, ""));
    }

    [Fact]
    public void Twilio_signature_matches_the_documented_example()
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("CallSid", "CA1234567890ABCDE"), new("Caller", "+12349013030"), new("Digits", "1234"),
            new("From", "+12349013030"), new("To", "+18005551212"),
        };
        const string url = "https://mycompany.com/myapp.php?foo=1&bar=2";
        Assert.Equal("0/KCTR6DLpKmkAf8muzZqo1nDgQ=", TwilioSmsTransport.Sign("12345", url, form));
        Assert.True(TwilioSmsTransport.VerifySignature("12345", new[] { "https://wrong.example", url }, form,
            "0/KCTR6DLpKmkAf8muzZqo1nDgQ="));
        Assert.False(TwilioSmsTransport.VerifySignature("12345", new[] { "https://wrong.example" }, form,
            "0/KCTR6DLpKmkAf8muzZqo1nDgQ="));
        Assert.False(TwilioSmsTransport.VerifySignature("54321", new[] { url }, form, "0/KCTR6DLpKmkAf8muzZqo1nDgQ="));
        Assert.False(TwilioSmsTransport.VerifySignature("12345", new[] { url }, form, null));
    }

    [Fact]
    public void WeChat_url_signature_and_aes_safe_mode_round_trip()
    {
        // sha1 of the sorted parts — independently computed
        Assert.Equal("1a3904d4dd2e9bed5c07e76e561eb75f212d057d", WeChatCrypto.Signature("tok123", "1700000000", "nonce42"));
        Assert.True(WeChatCrypto.Verify("1a3904d4dd2e9bed5c07e76e561eb75f212d057d", "nonce42", "tok123", "1700000000"));
        Assert.False(WeChatCrypto.Verify("1a3904d4dd2e9bed5c07e76e561eb75f212d057d", "tok123", "1700000001", "nonce42"));
        Assert.False(WeChatCrypto.Verify("", "tok123", "1700000000", "nonce42"));

        const string key = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG"; // 43 chars
        var xml = "<xml><Content><![CDATA[你好 hello]]></Content></xml>";
        var enc = WeChatCrypto.Encrypt(key, xml, "wx1234567890abcdef");
        Assert.Equal(xml, WeChatCrypto.Decrypt(key, enc, "wx1234567890abcdef"));
        Assert.Null(WeChatCrypto.Decrypt(key, enc, "wxSOMEONEELSE00000"));          // another app's message
        Assert.Null(WeChatCrypto.Decrypt("bbcdefghijklmnopqrstuvwxyz0123456789ABCDEFG", enc, "wx1234567890abcdef"));
        Assert.Null(WeChatCrypto.Decrypt(key, "bm90IGEgY2lwaGVydGV4dA==", "wx1234567890abcdef"));
        Assert.Null(WeChatCrypto.Key("too-short"));
    }

    [Fact]
    public void TikTok_signature_checks_hmac_and_the_replay_window()
    {
        var body = B("{\"event\":\"message\"}");
        var now = DateTime.UtcNow;
        var t = new DateTimeOffset(now).ToUnixTimeSeconds().ToString();
        var good = $"t={t},s={TikTokTransport.Sign("app-secret", t, body)}";
        Assert.True(TikTokTransport.VerifySignature("app-secret", body, good, now));
        Assert.False(TikTokTransport.VerifySignature("app-secret", B("{}"), good, now));
        Assert.False(TikTokTransport.VerifySignature("app-secret", body, good, now.AddMinutes(10)));   // replayed later
        Assert.False(TikTokTransport.VerifySignature("app-secret", body, $"t={t}", now));
        Assert.False(TikTokTransport.VerifySignature("", body, good, now));
    }

    [Fact]
    public void Douyin_and_Zalo_signatures()
    {
        var body = B("{\"event\":\"im_receive_msg\"}");
        var d = DouyinTransport.Sign("client-secret", body);
        Assert.Matches("^[0-9a-f]{40}$", d);
        Assert.True(DouyinTransport.VerifySignature("client-secret", body, d));
        Assert.False(DouyinTransport.VerifySignature("client-secret", B("{}"), d));
        Assert.False(DouyinTransport.VerifySignature("client-secret", body, null));

        const string raw = "{\"event_name\":\"user_send_text\",\"timestamp\":\"1700000000000\"}";
        var mac = ZaloTransport.Mac("app-1", raw, "1700000000000", "oa-secret");
        Assert.True(ZaloTransport.VerifySignature("app-1", "oa-secret", raw, "1700000000000", $"mac={mac}"));
        Assert.True(ZaloTransport.VerifySignature("app-1", "oa-secret", raw, "1700000000000", mac));
        Assert.False(ZaloTransport.VerifySignature("app-1", "oa-secret", raw, "1700000000001", $"mac={mac}"));
        Assert.False(ZaloTransport.VerifySignature("app-1", "other", raw, "1700000000000", $"mac={mac}"));
        Assert.False(ZaloTransport.VerifySignature("app-1", "oa-secret", raw, "1700000000000", null));
    }

    // ---- parsing ------------------------------------------------------------------

    [Fact]
    public void Line_parse_keeps_user_chats_and_media()
    {
        var (dest, events) = LineInbound.Parse(J("""
        {"destination":"Ubot","events":[
          {"type":"message","replyToken":"r1","timestamp":1700000000000,"source":{"type":"user","userId":"U1"},
           "message":{"type":"text","id":"m1","text":"hello"}},
          {"type":"message","replyToken":"r2","timestamp":1700000000000,"source":{"type":"group","groupId":"G","userId":"U2"},
           "message":{"type":"text","id":"m2","text":"group chat"}},
          {"type":"message","replyToken":"r3","timestamp":1700000000000,"source":{"type":"user","userId":"U1"},
           "message":{"type":"image","id":"m3","contentProvider":{"type":"line"}}},
          {"type":"unfollow","timestamp":1700000000000,"source":{"type":"user","userId":"U1"}}]}
        """));
        Assert.Equal("Ubot", dest);
        Assert.Equal(3, events.Count);
        Assert.Equal("hello", events[0].Text);
        Assert.Equal("r1", events[0].ReplyToken);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), events[0].Timestamp);
        Assert.Equal("[image]", events[1].Text);
        Assert.Equal("m3", events[1].Media!.Ref);
        Assert.Equal("unfollow", events[2].Type);
    }

    [Fact]
    public void Viber_keys_are_url_safe_and_reversible()
    {
        const string id = "01234567890A/b+c=";
        var key = ViberTransport.KeyFor(id);
        Assert.DoesNotContain('/', key);
        Assert.Equal(id, ViberTransport.IdFor(key));
        var e = ViberInbound.Parse(J("""
        {"event":"conversation_started","timestamp":1700000000000,"message_token":1,"type":"open",
         "context":"INV-42","user":{"id":"ab/c+d=","name":"Mai"},"subscribed":false}
        """))!;
        Assert.Equal("conversation_started", e.Event);
        Assert.Equal("ab_c-d=", e.UserKey);
        Assert.Equal("INV-42", e.Context);
    }

    [Fact]
    public void WeChat_parse_reads_messages_and_scene_events()
    {
        var text = WeChatInbound.Parse(WeChatInbound.Load(
            "<xml><ToUserName><![CDATA[gh_1]]></ToUserName><FromUserName><![CDATA[oOPEN1]]></FromUserName>" +
            "<CreateTime>1700000000</CreateTime><MsgType><![CDATA[text]]></MsgType><Content><![CDATA[你好]]></Content>" +
            "<MsgId>9001</MsgId></xml>")!);
        Assert.Equal(("message", "oOPEN1", "你好", "9001"), (text.Kind, text.OpenId, text.Text, text.ExternalId));
        var sub = WeChatInbound.Parse(WeChatInbound.Load(
            "<xml><ToUserName>gh_1</ToUserName><FromUserName>oOPEN1</FromUserName><CreateTime>1700000000</CreateTime>" +
            "<MsgType>event</MsgType><Event>subscribe</Event><EventKey>qrscene_INV7</EventKey></xml>")!);
        Assert.Equal(("subscribe", "INV7"), (sub.Kind, sub.Ref));
        // an XML bomb / external entity is refused outright
        Assert.Null(WeChatInbound.Load("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><xml>&e;</xml>"));
    }

    [Fact]
    public void Douyin_TikTok_and_Zalo_parse()
    {
        var d = DouyinInbound.Parse(J("""
        {"event":"im_receive_msg","from_user_id":"user-1","to_user_id":"ent-1","client_key":"ck",
         "content":"{\"conversation_short_id\":\"c9\",\"server_message_id\":\"s9\",\"message_type\":\"text\",\"text\":\"在吗\",\"create_time\":1700000000000,\"user_infos\":[{\"open_id\":\"user-1\",\"nick_name\":\"小王\"}]}"}
        """))!;
        Assert.Equal(("message", "user-1", "c9|s9", "在吗", "小王"), (d.Kind, d.FromUser, d.ExternalId, d.Text, d.Name));
        Assert.Equal("verify", DouyinInbound.Parse(J("""{"event":"verify_webhook","content":{"challenge":123}}"""))!.Kind);

        var t = TikTokInbound.Parse(J("""
        {"event":"message.receive","create_time":1700000000,
         "content":"{\"conversation_id\":\"conv-1\",\"message_id\":\"tm-1\",\"from_user\":{\"id\":\"user-9\",\"display_name\":\"Ana\"},\"text\":{\"body\":\"hi\"}}"}
        """), "biz-1")!;
        Assert.Equal(("conv-1", "tm-1", "hi", "Ana"), (t.ConversationId, t.MessageId, t.Text, t.Name));
        Assert.Null(TikTokInbound.Parse(J("""{"event":"message.receive","content":{"from_user":{"id":"biz-1"},"text":{"body":"echo"}}}"""), "biz-1"));

        var z = ZaloInbound.Parse(J("""
        {"app_id":"1","event_name":"user_send_image","timestamp":"1700000000000","sender":{"id":"zu-1"},"recipient":{"id":"OA-1"},
         "message":{"msg_id":"zm-1","attachments":[{"type":"image","payload":{"url":"https://cdn.example.test/a.jpg"}}]}}
        """))!;
        Assert.Equal(("message", "zu-1", "[image]", "zm-1", "OA-1"), (z.Kind, z.UserId, z.Text, z.MessageId, z.OaId));
        Assert.Equal("image", z.Media!.MediaType);
        Assert.Equal("ignore", ZaloInbound.Parse(J("""{"event_name":"oa_send_text","timestamp":"1"}"""))!.Kind);
    }

    // ---- registry -----------------------------------------------------------------

    [Fact]
    public void Window_registry_hours_per_platform()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(SocialWindowState.Open, ChannelWindow.StateAt(ChannelIds.WeChat, now.AddHours(-47), now));
        Assert.Equal(SocialWindowState.Closed, ChannelWindow.StateAt(ChannelIds.WeChat, now.AddHours(-49), now));
        Assert.Equal(SocialWindowState.Open, ChannelWindow.StateAt(ChannelIds.Zalo, now.AddDays(-6), now));
        Assert.Equal(SocialWindowState.Closed, ChannelWindow.StateAt(ChannelIds.Zalo, now.AddDays(-8), now));
        Assert.Equal(SocialWindowState.Closed, ChannelWindow.StateAt(ChannelIds.TikTok, null, now));
        Assert.False(ChannelWindow.Applies(ChannelIds.Line));
        Assert.False(ChannelWindow.Applies(ChannelIds.Sms));
        Assert.Contains("48-hour", ChannelWindow.Refusal(ChannelIds.WeChat, SocialWindowState.Closed, true)!);
        Assert.Contains("7-day", ChannelWindow.Refusal(ChannelIds.Zalo, SocialWindowState.Closed, true)!);
        Assert.Contains("after the customer has written",
            ChannelWindow.Refusal(ChannelIds.Douyin, SocialWindowState.Closed, false, everWrote: false)!);
        Assert.Equal(true, ChannelWindow.Fields(ChannelIds.WeChat, live: false, null, now)["window_open"]);
        Assert.Equal(false, ChannelWindow.Fields(ChannelIds.WeChat, live: true, now.AddDays(-3), now)["window_open"]);
    }

    [Fact]
    public void Invite_codes_ride_on_the_first_message_only_when_present()
    {
        Assert.Equal("[ref:INV-7] hello", MoreChannels.WithRef("hello", "INV-7"));
        Assert.Equal("hello", MoreChannels.WithRef("hello", ""));
        Assert.Equal("hello", MoreChannels.WithRef("hello", "   "));
        Assert.Equal("[ref:ab12] x", MoreChannels.WithRef("x", "a b]1[2"));   // brackets / spaces can't break the marker
        Assert.Equal(64, MoreChannels.CleanRef(new string('a', 100))!.Length);
    }

    [Fact]
    public void Configured_rules_per_channel()
    {
        Assert.False(MoreChannels.Configured(new ChannelAccount { Channel = ChannelIds.Line, AccessToken = "t" }));
        Assert.True(MoreChannels.Configured(new ChannelAccount { Channel = ChannelIds.Line, AccessToken = "t", AppSecret = "s" }));
        Assert.False(MoreChannels.Configured(new ChannelAccount { Channel = ChannelIds.Sms, AccessToken = "t",
            Config = new() { ["account_sid"] = "AC1" } }));
        Assert.True(MoreChannels.Configured(new ChannelAccount { Channel = ChannelIds.Sms, AccessToken = "t",
            Config = new() { ["account_sid"] = "AC1", ["from_number"] = "+60123" } }));
        Assert.True(MoreChannels.Configured(new ChannelAccount { Channel = ChannelIds.WeChat, AppSecret = "s",
            Config = new() { ["app_id"] = "wx1" } }));
        Assert.False(MoreChannels.Configured(new ChannelAccount { Channel = ChannelIds.TikTok, AccessToken = "t" }));
        Assert.True(Routing.AccountBound(ChannelIds.Zalo));
        Assert.False(Routing.AccountBound(ChannelIds.Sms));
        Assert.True(Routing.AddressPortable(ChannelIds.Sms));
    }
}
