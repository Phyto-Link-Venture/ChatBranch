using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// The per-person channel allow-list (Profile.ChannelAccountIds): inside one company,
/// someone limited to account A sees and acts on A's conversations only — never B's —
/// while unattributed conversations stay everybody's and a conversation on no number
/// yet stays visible inside the company. An empty list keeps today's behaviour.
/// </summary>
public class ChannelAccessTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly object Gate = new();
    private sealed record World(int Company, int Other, int AcctA, int AcctB, int AcctOther,
        int LimitedUserId, int OpenUserId);
    private static World? _w;

    public ChannelAccessTests(ApiFactory factory)
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
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (db.Entities.Any(e => e.Slug == "ca-co"))
            {
                if (_w != null) return;
            }
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            var co = new Entity { TenantId = tenant.Id, Name = "CA Co", Slug = "ca-co", CreatedAt = now, UpdatedAt = now };
            var other = new Entity { TenantId = tenant.Id, Name = "CA Other", Slug = "ca-other", CreatedAt = now, UpdatedAt = now };
            db.Entities.AddRange(co, other);
            db.SaveChanges();
            ChannelAccount Acct(string name, int entity)
            {
                var a = new ChannelAccount { Channel = "telegram", Name = name, EntityId = entity, CreatedAt = now };
                db.ChannelAccounts.Add(a);
                db.SaveChanges();
                return a;
            }
            var a = Acct("CA bot A", co.Id);
            var b = Acct("CA bot B", co.Id);
            var o = Acct("CA bot other", other.Id);

            int User(string username, List<int> channels)
            {
                var u = new AppUser { Username = username, Password = DjangoPasswordHasher.Hash("password123"), IsActive = true };
                db.Users.Add(u);
                db.SaveChanges();
                var p = new Profile { UserId = u.Id, Role = Roles.Agent, TenantId = tenant.Id, ChannelAccountIds = channels };
                p.Entities.Add(co);
                db.Profiles.Add(p);
                db.SaveChanges();
                return u.Id;
            }
            var limited = User("ca-limited", new List<int> { a.Id });
            var open = User("ca-open", new List<int>());

            Thread T(string key, int? entity, int? account) => new()
            {
                Channel = "telegram", Key = key, Name = key, EntityId = entity, AccountId = account,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Threads.AddRange(
                T("ca-on-a", co.Id, a.Id),
                T("ca-on-b", co.Id, b.Id),
                T("ca-unattributed", null, null),
                T("ca-no-number", co.Id, null));
            db.SaveChanges();
            ChatBranch.Data.Stores.ThreadCompanies.Backfill(db);
            foreach (var t in db.Threads.Where(t => t.Key.StartsWith("ca-")).ToList())
                db.Messages.Add(new Message
                {
                    ThreadId = t.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                    Text = $"hello from {t.Key}", Status = "received", CreatedAt = now,
                });
            db.SaveChanges();
            _w = new World(co.Id, other.Id, a.Id, b.Id, o.Id, limited, open);
        }
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

    private static async Task<List<string>> InboxKeys(HttpClient c)
    {
        var body = await c.GetFromJsonAsync<JsonElement>("/api/inbox/threads?limit=200&channel=telegram");
        return body.GetProperty("threads").EnumerateArray()
            .Select(t => t.GetProperty("key").GetString()!).Where(k => k.StartsWith("ca-")).ToList();
    }

    [Fact]
    public async Task Limited_person_lists_only_their_channels_plus_unattributed_and_numberless()
    {
        var limited = await Authed("ca-limited", "password123");
        var keys = await InboxKeys(limited);
        Assert.Contains("ca-on-a", keys);
        Assert.Contains("ca-unattributed", keys);   // no company = everybody's
        Assert.Contains("ca-no-number", keys);      // no number yet = the company's
        Assert.DoesNotContain("ca-on-b", keys);

        var open = await Authed("ca-open", "password123");
        var all = await InboxKeys(open);
        Assert.Contains("ca-on-a", all);
        Assert.Contains("ca-on-b", all);
    }

    [Fact]
    public async Task Another_channels_conversation_is_not_found()
    {
        var limited = await Authed("ca-limited", "password123");
        Assert.Equal(HttpStatusCode.NotFound,
            (await limited.GetAsync("/api/messaging/threads/telegram/ca-on-b")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await limited.GetAsync("/api/whatsapp/threads/telegram/ca-on-b/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await limited.GetAsync("/api/messaging/threads/telegram/ca-on-a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await limited.GetAsync("/api/messaging/threads/telegram/ca-unattributed")).StatusCode);

        // never a second conversation for the same customer by sending into the hidden one
        var send = await limited.PostAsJsonAsync("/api/whatsapp/threads/telegram/ca-on-b/send", new { text = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, send.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, db.Threads.Count(t => t.Channel == "telegram" && t.Key == "ca-on-b"));
    }

    [Fact]
    public async Task Pickers_offer_only_their_channels_and_sending_on_another_is_refused()
    {
        var limited = await Authed("ca-limited", "password123");
        var body = await limited.GetFromJsonAsync<JsonElement>("/api/whatsapp/accounts?channel=telegram");
        var names = body.GetProperty("accounts").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Contains("CA bot A", names);
        Assert.DoesNotContain("CA bot B", names);

        var send = await limited.PostAsJsonAsync("/api/whatsapp/threads/telegram/ca-on-a/send",
            new { text = "hi", accountId = W.AcctB });
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
    }

    [Fact]
    public async Task Search_and_live_counts_respect_the_allow_list()
    {
        var limited = await Authed("ca-limited", "password123");
        var res = await limited.GetFromJsonAsync<JsonElement>("/api/search?q=ca-on&types=conversations");
        var text = res.ToString();
        Assert.Contains("ca-on-a", text);
        Assert.DoesNotContain("ca-on-b", text);
    }

    [Fact]
    public async Task Managers_set_the_list_within_the_users_entities()
    {
        var admin = await Authed("admin", "admin123");
        var ok = await admin.PostAsJsonAsync($"/api/v1/users/{W.OpenUserId}",
            new { channel_account_ids = new[] { W.AcctA, W.AcctB } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var payload = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { W.AcctA, W.AcctB },
            payload.GetProperty("channel_account_ids").EnumerateArray().Select(x => x.GetInt32()).OrderBy(x => x));

        // a channel of a company the user isn't in
        var outside = await admin.PostAsJsonAsync($"/api/v1/users/{W.OpenUserId}",
            new { channel_account_ids = new[] { W.AcctOther } });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);

        var missing = await admin.PostAsJsonAsync($"/api/v1/users/{W.OpenUserId}",
            new { channel_account_ids = new[] { 987654 } });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // [] clears it again
        var clear = await admin.PostAsJsonAsync($"/api/v1/users/{W.OpenUserId}",
            new { channel_account_ids = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Equal(0, (await clear.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("channel_account_ids").GetArrayLength());

        // moving the user to another company drops channels the new scope can't use
        await admin.PostAsJsonAsync($"/api/v1/users/{W.OpenUserId}", new { channel_account_ids = new[] { W.AcctA } });
        var moved = await admin.PostAsJsonAsync($"/api/v1/users/{W.OpenUserId}", new { entity_ids = new[] { W.Other } });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(0, (await moved.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("channel_account_ids").GetArrayLength());
        await admin.PostAsJsonAsync($"/api/v1/users/{W.OpenUserId}", new { entity_ids = new[] { W.Company } });
    }

    [Fact]
    public async Task A_message_on_another_channel_never_notifies_them()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var onB = db.Threads.Single(t => t.Key == "ca-on-b");
        var onA = db.Threads.Single(t => t.Key == "ca-on-a");
        onB.Assignee = "ca-limited";
        onA.Assignee = "ca-limited";
        db.SaveChanges();

        await NotificationService.NotifyInboundMessageAsync(db, onB, "on b");
        await NotificationService.NotifyInboundMessageAsync(db, onA, "on a");
        var mine = db.Notifications.AsNoTracking().Where(n => n.UserId == W.LimitedUserId).ToList();
        Assert.Contains(mine, n => n.RefKey == $"thread:{onA.Id}");
        Assert.DoesNotContain(mine, n => n.RefKey == $"thread:{onB.Id}");
    }
}
