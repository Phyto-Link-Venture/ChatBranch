using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The ROLE's channel policy (Role.ChannelAccess): an admin gives a role some channels
/// and some connection methods of each — e.g. "SMS" plus "WhatsApp, official numbers
/// only". People holding the role then CONNECT only those (create / edit / pair refused
/// otherwise) and USE only those (inbox, conversation routes, account lists). Unlike the
/// per-person allow-list it is hard: an unattributed conversation on a channel the role
/// isn't given is never theirs. No policy = every channel; superadmin is never limited.
/// </summary>
public class RoleChannelAccessTests : IClassFixture<ApiFactory>
{
    private const string Desk = "rca-desk";        // the limited role under test
    private const string Lead = "rca-lead";        // a limited role that holds rbac.manage
    private const string Wide = "rca-wide";        // a role whose policy is set then cleared
    private static readonly string[] DeskCaps =
        { "chats.view", "chats.send", "channels.view", "channels.manage", "users.view" };

    private readonly ApiFactory _factory;
    private static readonly object Gate = new();
    private sealed record World(int Cloud, int Baileys, int Sms, int Telegram, int Company);
    private static World? _w;

    public RoleChannelAccessTests(ApiFactory factory)
    {
        _factory = factory;
        _ = _factory.CreateClient(); // boot + seed
        Arrange();
    }

    private World W => _w!;

    private void Arrange()
    {
        lock (Gate)
        {
            if (_w != null) return;
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            var co = new Entity { TenantId = tenant.Id, Name = "RCA Co", Slug = "rca-co", CreatedAt = now, UpdatedAt = now };
            db.Entities.Add(co);
            db.SaveChanges();

            ChannelAccount Acct(string channel, string provider, string name)
            {
                var a = new ChannelAccount
                {
                    Channel = channel, Provider = provider, Name = name, EntityId = co.Id, CreatedAt = now,
                    ExternalId = name,
                };
                db.ChannelAccounts.Add(a);
                db.SaveChanges();
                return a;
            }
            var cloud = Acct("whatsapp", Providers.Cloud, "rca-cloud");
            var baileys = Acct("whatsapp", Providers.Baileys, "rca-qr");
            var sms = Acct("sms", "cloud", "rca-sms");          // a non-WhatsApp account keeps the "cloud" default
            var tg = Acct("telegram", "cloud", "rca-bot");

            foreach (var (slug, policy, caps) in new[]
                     {
                         (Desk, """{"whatsapp":["cloud"],"sms":["direct"]}""", DeskCaps),
                         (Lead, """{"whatsapp":["cloud","ycloud"],"sms":["direct"]}""", DeskCaps.Append("rbac.manage").ToArray()),
                         (Wide, """{"sms":["direct"]}""", DeskCaps),
                     })
            {
                db.Roles.Add(new Role { Slug = slug, Label = slug, CreatedAt = now, ChannelAccess = policy });
                foreach (var cap in caps)
                    db.RolePermissions.Add(new RolePermission { Role = slug, Capability = cap, Allowed = true, UpdatedAt = now });
            }
            db.SaveChanges();

            foreach (var (user, role) in new[] { ("rca-desk-user", Desk), ("rca-lead-user", Lead), ("rca-wide-user", Wide) })
            {
                var u = new AppUser { Username = user, Password = DjangoPasswordHasher.Hash("password123"), IsActive = true };
                db.Users.Add(u);
                db.SaveChanges();
                var p = new Profile { UserId = u.Id, Role = role, TenantId = tenant.Id };
                // the lead edits roles, which only someone NOT bound to entities may do
                if (role != Lead) p.Entities.Add(co);
                db.Profiles.Add(p);
            }
            db.SaveChanges();

            Thread T(string channel, string key, int? entity, int? account) => new()
            {
                Channel = channel, Key = key, Name = key, EntityId = entity, AccountId = account,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Threads.AddRange(
                T("whatsapp", "60110000001", co.Id, cloud.Id),       // official number → theirs
                T("whatsapp", "60110000002", co.Id, baileys.Id),     // QR number → not theirs
                T("whatsapp", "60110000003", null, baileys.Id),      // unattributed, QR → still not theirs
                T("whatsapp", "60110000004", co.Id, null),           // WhatsApp, no number yet → theirs
                T("sms", "60110000005", co.Id, sms.Id),              // SMS → theirs
                T("telegram", "rca-tg-1", co.Id, tg.Id),             // Telegram → not theirs
                T("email", "rca@example.com", null, null));          // email, unattributed → not theirs
            db.SaveChanges();
            ChatBranch.Data.Stores.ThreadCompanies.Backfill(db);
            foreach (var t in db.Threads.Where(t => t.Key.StartsWith("6011000000") || t.Key.StartsWith("rca")).ToList())
                db.Messages.Add(new Message
                {
                    ThreadId = t.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                    Text = $"hello from {t.Key}", Status = "received", CreatedAt = now,
                });
            db.SaveChanges();
            _w = new World(cloud.Id, baileys.Id, sms.Id, tg.Id, co.Id);
        }
    }

    private async Task<HttpClient> As(string user, string pass = "password123")
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<List<string>> InboxKeys(HttpClient c)
    {
        var body = await c.GetFromJsonAsync<JsonElement>("/api/inbox/threads?limit=500");
        return body.GetProperty("threads").EnumerateArray()
            .Select(t => t.GetProperty("key").GetString()!)
            .Where(k => k.StartsWith("6011000000") || k.StartsWith("rca")).ToList();
    }

    private static async Task<string> Code(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

    [Fact]
    public async Task Inbox_shows_only_the_roles_channels_and_methods()
    {
        var keys = await InboxKeys(await As("rca-desk-user"));
        Assert.Contains("60110000001", keys);          // WhatsApp, official
        Assert.Contains("60110000004", keys);          // WhatsApp, no number yet
        Assert.Contains("60110000005", keys);          // SMS
        Assert.DoesNotContain("60110000002", keys);    // QR number
        Assert.DoesNotContain("60110000003", keys);    // unattributed QR — the role limit is hard
        Assert.DoesNotContain("rca-tg-1", keys);       // Telegram
        Assert.DoesNotContain("rca@example.com", keys); // email, unattributed, no account

        // superadmin is never limited
        var all = await InboxKeys(await As("superadmin", "super123"));
        Assert.Contains("60110000002", all);
        Assert.Contains("rca-tg-1", all);
    }

    [Fact]
    public async Task A_conversation_on_another_method_or_channel_is_not_found()
    {
        var desk = await As("rca-desk-user");
        Assert.Equal(HttpStatusCode.NotFound,
            (await desk.GetAsync("/api/messaging/threads/telegram/rca-tg-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await desk.GetAsync("/api/whatsapp/threads/whatsapp/60110000002/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await desk.GetAsync("/api/whatsapp/threads/whatsapp/60110000001/messages")).StatusCode);
    }

    [Fact]
    public async Task Account_lists_and_whoami_follow_the_policy()
    {
        var desk = await As("rca-desk-user");
        var ids = (await desk.GetFromJsonAsync<JsonElement>("/api/whatsapp/accounts?channel=all"))
            .GetProperty("accounts").EnumerateArray()
            .Select(a => int.TryParse(a.GetProperty("id").ToString(), out var n) ? n : 0).ToList();
        Assert.Contains(W.Cloud, ids);
        Assert.Contains(W.Sms, ids);
        Assert.DoesNotContain(W.Baileys, ids);
        Assert.DoesNotContain(W.Telegram, ids);

        var who = await desk.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        var access = who.GetProperty("channel_access");
        Assert.Equal(new[] { "cloud" }, access.GetProperty("whatsapp").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "direct" }, access.GetProperty("sms").EnumerateArray().Select(x => x.GetString()));
        Assert.False(access.TryGetProperty("telegram", out _));

        var su = await (await As("superadmin", "super123")).GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        Assert.Equal(JsonValueKind.Null, su.GetProperty("channel_access").ValueKind);
    }

    [Fact]
    public async Task Connecting_a_method_or_channel_the_role_lacks_is_refused()
    {
        var desk = await As("rca-desk-user");

        var qr = await desk.PostAsJsonAsync("/api/whatsapp/accounts", new { provider = "baileys", name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, qr.StatusCode);
        Assert.Equal("channel_not_allowed", await Code(qr));

        var waha = await desk.PostAsJsonAsync("/api/whatsapp/accounts/waha", new { name = "x", base_url = "http://w" });
        Assert.Equal(HttpStatusCode.Forbidden, waha.StatusCode);

        var bot = await desk.PostAsJsonAsync("/api/messaging/accounts", new { channel = "telegram", access_token = "1:abc" });
        Assert.Equal(HttpStatusCode.Forbidden, bot.StatusCode);

        var widget = await desk.PostAsJsonAsync("/api/widgets", new { name = "site" });
        Assert.Equal(HttpStatusCode.Forbidden, widget.StatusCode);

        // an allowed method gets past the policy (to the handler's own validation / success)
        var official = await desk.PostAsJsonAsync("/api/whatsapp/accounts",
            new { provider = "cloud", name = "rca-new", phone_number_id = "rca-pnid-1", access_token = "rca-token",
                  entity = "rca-co" });
        Assert.NotEqual(HttpStatusCode.Forbidden, official.StatusCode);
        var smsTry = await desk.PostAsJsonAsync("/api/messaging/accounts", new { channel = "sms", entity = "rca-co" });
        Assert.NotEqual("channel_not_allowed", await Code(smsTry));
    }

    [Fact]
    public async Task Editing_pairing_or_moving_to_a_method_the_role_lacks_is_refused()
    {
        var desk = await As("rca-desk-user");
        // an existing QR number: no edit, no pairing, no delete
        Assert.Equal(HttpStatusCode.Forbidden,
            (await desk.PostAsJsonAsync($"/api/whatsapp/accounts/{W.Baileys}", new { name = "renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await desk.PostAsJsonAsync($"/api/whatsapp/accounts/{W.Baileys}/baileys/start", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await desk.DeleteAsync($"/api/whatsapp/accounts/{W.Baileys}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await desk.DeleteAsync($"/api/messaging/accounts/{W.Telegram}")).StatusCode);
        // an official number can't be switched to QR
        var flip = await desk.PostAsJsonAsync($"/api/whatsapp/accounts/{W.Cloud}", new { provider = "baileys" });
        Assert.Equal(HttpStatusCode.Forbidden, flip.StatusCode);
        // …but it can still be edited
        Assert.Equal(HttpStatusCode.OK,
            (await desk.PostAsJsonAsync($"/api/whatsapp/accounts/{W.Cloud}", new { name = "rca-cloud" })).StatusCode);
    }

    [Fact]
    public async Task Admin_api_validates_and_reports_the_policy()
    {
        var su = await As("superadmin", "super123");
        var snap = await su.GetFromJsonAsync<JsonElement>("/api/auth/channel-access");
        var wa = snap.GetProperty("catalog").EnumerateArray().First(c => c.GetProperty("channel").GetString() == "whatsapp");
        var methods = wa.GetProperty("methods").EnumerateArray().ToList();
        Assert.Contains(methods, m => m.GetProperty("id").GetString() == "baileys" && !m.GetProperty("official").GetBoolean());
        Assert.Contains(methods, m => m.GetProperty("id").GetString() == "cloud" && m.GetProperty("official").GetBoolean());
        Assert.Equal(JsonValueKind.Null, snap.GetProperty("roles").GetProperty(Roles.SuperAdmin).ValueKind);
        Assert.Equal("cloud", snap.GetProperty("roles").GetProperty(Desk).GetProperty("whatsapp")[0].GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await su.PutAsJsonAsync($"/api/auth/roles/{Desk}/channels",
            new { channels = new Dictionary<string, string[]> { ["fax"] = new[] { "direct" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PutAsJsonAsync($"/api/auth/roles/{Desk}/channels",
            new { channels = new Dictionary<string, string[]> { ["sms"] = new[] { "baileys" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PutAsJsonAsync($"/api/auth/roles/{Roles.SuperAdmin}/channels",
            new { channels = new Dictionary<string, string[]> { ["sms"] = new[] { "direct" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await su.PutAsJsonAsync("/api/auth/roles/no-such-role/channels",
            new { channels = (object?)null })).StatusCode);
        // the desk role itself can't change policies (no rbac.manage)
        Assert.Equal(HttpStatusCode.Forbidden, (await (await As("rca-desk-user")).PutAsJsonAsync(
            $"/api/auth/roles/{Desk}/channels", new { channels = (object?)null })).StatusCode);
    }

    [Fact]
    public async Task A_limited_role_editor_hands_out_only_what_it_has()
    {
        var lead = await As("rca-lead-user");
        // never "every channel", never a channel or method beyond its own
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PutAsJsonAsync($"/api/auth/roles/{Desk}/channels",
            new { channels = (object?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PutAsJsonAsync($"/api/auth/roles/{Desk}/channels",
            new { channels = new Dictionary<string, string[]> { ["telegram"] = new[] { "direct" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PutAsJsonAsync($"/api/auth/roles/{Desk}/channels",
            new { channels = new Dictionary<string, string[]> { ["whatsapp"] = new[] { "baileys" } } })).StatusCode);
        // inside its own reach it may (re)write the desk's policy
        var ok = await lead.PutAsJsonAsync($"/api/auth/roles/{Desk}/channels",
            new { channels = new Dictionary<string, string[]> { ["whatsapp"] = new[] { "cloud" }, ["sms"] = new[] { "direct" } } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Clearing_the_policy_gives_every_channel_back()
    {
        var wide = await As("rca-wide-user");
        Assert.DoesNotContain("rca-tg-1", await InboxKeys(wide));

        var su = await As("superadmin", "super123");
        var cleared = await su.PutAsJsonAsync($"/api/auth/roles/{Wide}/channels", new { channels = (object?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var keys = await InboxKeys(wide);
        Assert.Contains("rca-tg-1", keys);
        Assert.Contains("60110000002", keys);
    }
}
