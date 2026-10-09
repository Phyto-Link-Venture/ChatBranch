using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Part B — invite links: codes (shape, expiry, per-channel links + QR), and
/// the binder in the ONE inbound path for every text format ("[ref:CODE] ",
/// "(ref:CODE)", "ref CODE", Telegram "/start CODE") plus Messenger referrals
/// over the signed webhook; expired / spent / wrong-channel codes are ignored.
/// </summary>
public class ContactInviteTests : IdentityApiBase, IClassFixture<IdentityFactory>
{
    private const string MetaSecret = "invite-meta-secret";
    public ContactInviteTests(IdentityFactory factory) : base(factory) { }

    private async Task<JsonElement> Invite(HttpClient c, int contactId, object body)
    {
        var res = await c.PostAsJsonAsync($"/api/contacts/{contactId}/invites", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Message Inbound(string channel, string key, string text, int? accountId = null) =>
        InScope((sp, _) => sp.GetRequiredService<InboundProcessor>()
            .ProcessInbound(channel, key, text, name: "Someone", accountId: accountId));

    private ChatBranch.Domain.Thread ThreadOf(string channel, string key) =>
        InScope((_, db) => db.Threads.AsNoTracking().Single(t => t.Channel == channel && t.Key == key));

    private List<Message> Messages(int threadId) =>
        InScope((_, db) => db.Messages.AsNoTracking().Where(m => m.ThreadId == threadId).OrderBy(m => m.Id).ToList());

    [Fact]
    public async Task An_invite_has_a_short_unambiguous_code_a_30_day_expiry_and_per_app_links()
    {
        var admin = await Authed();
        var waNumber = NewNumber();
        AddAccount("whatsapp", "Chat number", provider: "baileys", externalId: "qr-invite",
            config: new() { ["phone"] = "+" + waNumber });
        AddAccount("telegram", "Shop bot", config: new() { ["bot_username"] = "shop_test_bot" }, token: "");
        AddAccount("facebook", "Shop page", externalId: "PAGE-INV", config: new() { ["page_username"] = "shoppage" });
        AddAccount("instagram", "Shop IG", externalId: "IG-INV", config: new() { ["username"] = "shop.ig" });
        AddAccount("email", "Shop mail", externalId: "hello@shop.test",
            config: new() { ["from_email"] = "hello@shop.test", ["host"] = "smtp.test", ["username"] = "u", ["password"] = "p" });
        var id = await CreateContact(admin, "Invite Ivy", "+" + NewNumber());

        var one = await Invite(admin, id, new { channel = "whatsapp" });
        var code = one.GetProperty("code").GetString()!;
        Assert.Equal(8, code.Length);
        Assert.True(code.All(ch => ContactInviteService.Alphabet.Contains(ch)));
        Assert.DoesNotContain('O', code);
        var expires = one.GetProperty("expires_at").GetDateTime().ToUniversalTime();
        Assert.InRange((expires - DateTime.UtcNow).TotalDays, 29.9, 30.1);
        var wa = one.GetProperty("links").EnumerateArray().Single();
        Assert.Equal($"https://wa.me/{waNumber}?text=Hi%20(ref%3A{code})", wa.GetProperty("url").GetString());
        Assert.Contains("<svg", wa.GetProperty("qr_svg").GetString());
        Assert.Equal("whatsapp", one.GetProperty("channel").GetString());

        var all = await Invite(admin, id, new { });
        var c2 = all.GetProperty("code").GetString()!;
        Assert.Equal("", all.GetProperty("channel").GetString());
        var urls = all.GetProperty("links").EnumerateArray()
            .ToDictionary(l => l.GetProperty("channel").GetString()!, l => l.GetProperty("url").GetString());
        Assert.Equal($"https://t.me/shop_test_bot?start={c2}", urls["telegram"]);
        Assert.Equal($"https://m.me/shoppage?ref={c2}", urls["facebook"]);
        Assert.Equal("https://ig.me/m/shop.ig", urls["instagram"]);
        Assert.Equal($"mailto:hello@shop.test?subject=Hello%20%28ref%20{c2}%29", urls["email"]);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}/invites");
        Assert.Equal(2, list.GetProperty("invites").GetArrayLength());

        // agents may not mint invites (contacts.manage)
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync($"/api/contacts/{id}/invites", new { channel = "whatsapp" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/contacts/{id}/invites", new { channel = "no such app!" })).StatusCode);
    }

    [Theory]
    [InlineData("line", "U-line-{0}", "[ref:{1}] hello there", "hello there")]
    [InlineData("whatsapp", "{0}", "Hi (ref:{1})", "Hi")]
    [InlineData("instagram", "IGSID-{0}", "here is my code ref {1} thanks", "here is my code thanks")]
    [InlineData("telegram", "TG-{0}", "/start {1}", "/start")]
    [InlineData("email", "inv{0}@mail.test", "Hello (ref {1})", "Hello")]
    public async Task Every_text_format_binds_the_conversation_and_strips_the_marker(string channel, string keyFmt,
        string textFmt, string stored)
    {
        var admin = await Authed();
        var id = await CreateContact(admin, $"Bound {channel}", "+" + NewNumber());
        var code = (await Invite(admin, id, new { })).GetProperty("code").GetString()!;
        var key = string.Format(keyFmt, NewNumber());
        var text = string.Format(textFmt, NewNumber(), code.ToLowerInvariant());   // codes are case-insensitive

        Inbound(channel, key, text);

        var thread = ThreadOf(channel, key);
        Assert.Equal(id, thread.ContactId);
        var msgs = Messages(thread.Id);
        Assert.Equal(stored, msgs.Single(m => m.Direction == "in").Text);
        var note = msgs.Single(m => m.Private);
        Assert.Contains("via invite link", note.Text);
        if (channel != "telegram") Assert.Contains(text, note.Text);   // the original kept in the note
        InScope((_, db) =>
        {
            var inv = db.ContactInvites.AsNoTracking().Single(i => i.Code == code);
            Assert.NotNull(inv.UsedAt);
            Assert.Equal(thread.Id, inv.UsedThreadId);
            Assert.Contains(channel, inv.UsedChannels);
            Assert.True(db.AuditLogs.Any(a => a.Action == "contact.invite_used" && a.Detail.Contains(code)));
        });
    }

    [Fact]
    public async Task Expired_spent_and_wrong_channel_codes_are_ignored_silently()
    {
        var admin = await Authed();
        var id = await CreateContact(admin, "Strict Sam", "+" + NewNumber());

        // expired
        var expired = (await Invite(admin, id, new { channel = "telegram" })).GetProperty("code").GetString()!;
        InScope((_, db) =>
        {
            var inv = db.ContactInvites.Single(i => i.Code == expired);
            inv.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            db.SaveChanges();
        });
        var k1 = "TG-exp-" + NewNumber();
        Inbound("telegram", k1, $"/start {expired}");
        Assert.Null(ThreadOf("telegram", k1).ContactId);
        Assert.Equal($"/start {expired}", Messages(ThreadOf("telegram", k1).Id).Single().Text);

        // made for Telegram, arriving on WhatsApp
        var tgOnly = (await Invite(admin, id, new { channel = "telegram" })).GetProperty("code").GetString()!;
        var waKey = NewNumber();
        Inbound("whatsapp", waKey, $"Hi (ref:{tgOnly})");
        Assert.Null(ThreadOf("whatsapp", waKey).ContactId);

        // single-channel code: binds once
        var k2 = "TG-a-" + NewNumber();
        var k3 = "TG-b-" + NewNumber();
        Inbound("telegram", k2, $"/start {tgOnly}");
        Inbound("telegram", k3, $"/start {tgOnly}");
        Assert.Equal(id, ThreadOf("telegram", k2).ContactId);
        Assert.Null(ThreadOf("telegram", k3).ContactId);

        // an any-channel code binds once PER channel
        var any = (await Invite(admin, id, new { })).GetProperty("code").GetString()!;
        var w1 = NewNumber();
        var w2 = NewNumber();
        Inbound("whatsapp", w1, $"Hi (ref:{any})");
        Inbound("whatsapp", w2, $"Hi (ref:{any})");
        var k4 = "TG-c-" + NewNumber();
        Inbound("telegram", k4, $"/start {any}");
        Assert.Equal(id, ThreadOf("whatsapp", w1).ContactId);
        Assert.Null(ThreadOf("whatsapp", w2).ContactId);
        Assert.Equal(id, ThreadOf("telegram", k4).ContactId);

        // a made-up code: nothing happens, the text stays as written
        var k5 = "TG-d-" + NewNumber();
        Inbound("telegram", k5, "/start ABCDEFGH");
        Assert.Null(ThreadOf("telegram", k5).ContactId);
    }

    [Fact]
    public async Task A_chat_already_linked_to_someone_else_is_not_relinked()
    {
        var admin = await Authed();
        var owner = NewNumber();
        var ownerId = await CreateContact(admin, "Phone Owner", "+" + owner);
        var invitedId = await CreateContact(admin, "Invited Iris", "+" + NewNumber());
        var code = (await Invite(admin, invitedId, new { channel = "whatsapp" })).GetProperty("code").GetString()!;
        Inbound("whatsapp", owner, "first message");           // phone match links the owner
        Assert.Equal(ownerId, ThreadOf("whatsapp", owner).ContactId);

        Inbound("whatsapp", owner, $"Hi (ref:{code})");
        var thread = ThreadOf("whatsapp", owner);
        Assert.Equal(ownerId, thread.ContactId);
        Assert.Contains(Messages(thread.Id), m => m.Private && m.Text.Contains("already linked to Phone Owner"));
        Assert.Null(InScope((_, db) => db.ContactInvites.AsNoTracking().Single(i => i.Code == code).UsedAt));
    }

    private static StringContent Signed(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(MetaSecret));
        content.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
        return content;
    }

    [Fact]
    public async Task Messenger_referrals_bind_through_the_signed_webhook()
    {
        var admin = await Authed();
        // a page with only its app secret: verified webhooks, no Graph calls
        AddAccount("facebook", "Referral page", externalId: "PAGE-REF", appSecret: MetaSecret);
        var id = await CreateContact(admin, "Referral Rae", "+" + NewNumber());
        var c1 = (await Invite(admin, id, new { channel = "facebook" })).GetProperty("code").GetString()!;
        var anon = Factory.CreateClient();

        // an existing conversation: a standalone messaging_referrals event
        var psid1 = "PSID-" + NewNumber();
        var res = await anon.PostAsync("/api/messaging/webhook", Signed(new
        {
            @object = "page",
            entry = new[] { new { id = "PAGE-REF", messaging = new object[] { new
            {
                sender = new { id = psid1 }, recipient = new { id = "PAGE-REF" }, timestamp = 1700000000000,
                referral = new { @ref = c1, source = "SHORTLINK", type = "OPEN_THREAD" },
            } } } },
        }));
        res.EnsureSuccessStatusCode();
        Assert.Equal(id, ThreadOf("facebook", psid1).ContactId);

        // a new conversation: "Get started" postback carrying the referral
        var c2 = (await Invite(admin, id, new { channel = "facebook" })).GetProperty("code").GetString()!;
        var psid2 = "PSID-" + NewNumber();
        (await anon.PostAsync("/api/messaging/webhook", Signed(new
        {
            @object = "page",
            entry = new[] { new { id = "PAGE-REF", messaging = new object[] { new
            {
                sender = new { id = psid2 }, recipient = new { id = "PAGE-REF" }, timestamp = 1700000001000,
                postback = new { title = "Get Started", payload = "GET_STARTED", mid = "pb-" + psid2,
                    referral = new { @ref = c2, source = "SHORTLINK", type = "OPEN_THREAD" } },
            } } } },
        }))).EnsureSuccessStatusCode();
        var t2 = ThreadOf("facebook", psid2);
        Assert.Equal(id, t2.ContactId);
        Assert.Contains(Messages(t2.Id), m => m.Direction == "in" && m.Text == "Get Started");

        // unsigned → refused, nothing bound
        var bad = new StringContent(JsonSerializer.Serialize(new { @object = "page" }), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/messaging/webhook", bad)).StatusCode);
    }

    [Fact]
    public void The_text_binder_finds_codes_only_in_the_known_shapes()
    {
        var c = ContactInviteService.Candidates("[ref:AB23CD45] hi");
        Assert.Contains(c, x => x.Code == "AB23CD45" && x.Stripped == "hi");
        Assert.Contains(ContactInviteService.Candidates("/start@shop_bot XY23ZW45"), x => x.Code == "XY23ZW45" && x.Stripped == "/start");
        Assert.Empty(ContactInviteService.Candidates("please reference my order"));
        Assert.False(ContactInviteService.LooksLikeCode("ABCDEF10"));   // 1 and 0 are not in the alphabet
        Assert.True(ContactInviteService.LooksLikeCode("abcdef23"));
    }
}
