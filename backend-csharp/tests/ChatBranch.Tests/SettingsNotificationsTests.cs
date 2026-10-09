using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>Shared helpers for the settings / notifications suites.</summary>
internal static class SettingsTestKit
{
    public static async Task<HttpClient> Client(ApiFactory f, string user, string pass)
    {
        var c = f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>A login with the given role, straight into the DB. Returns its user id.</summary>
    public static int CreateUser(ApiFactory f, string username, string password, string role)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = db.Tenants.First(t => t.Slug == "demo");
        var u = new AppUser
        {
            Username = username, Password = DjangoPasswordHasher.Hash(password), IsActive = true,
        };
        db.Users.Add(u);
        db.SaveChanges();
        db.Profiles.Add(new Profile { UserId = u.Id, Role = role, TenantId = tenant.Id });
        db.SaveChanges();
        return u.Id;
    }

    public static int UserId(ApiFactory f, string username)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.Users.First(u => u.Username == username).Id;
    }

    public static void WithDb(ApiFactory f, Action<AppDbContext> act)
    {
        using var scope = f.Services.CreateScope();
        act(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static T WithDb<T>(ApiFactory f, Func<AppDbContext, T> act)
    {
        using var scope = f.Services.CreateScope();
        return act(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>A custom role holding exactly the given capabilities, plus one user in it.</summary>
    public static async Task<HttpClient> CustomRoleClient(ApiFactory f, string label, params string[] caps)
    {
        var super = await Client(f, "superadmin", "super123");
        var created = await super.PostAsJsonAsync("/api/auth/roles", new { label });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var slug = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        var grant = caps.ToDictionary(c => c, _ => true);
        var put = await super.PutAsJsonAsync("/api/auth/permissions",
            new { matrix = new Dictionary<string, Dictionary<string, bool>> { [slug] = grant } });
        put.EnsureSuccessStatusCode();
        var username = slug + "-user";
        CreateUser(f, username, "pass12345", slug);
        return await Client(f, username, "pass12345");
    }
}

/// <summary>The /notifications inbox API: own rows only, paging, filters, state, deletes.</summary>
public class NotificationInboxTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public NotificationInboxTests(ApiFactory f) => _f = f;

    private void Seed(int userId, int count, Func<int, Notification> make) =>
        SettingsTestKit.WithDb(_f, db =>
        {
            for (var i = 0; i < count; i++)
            {
                var n = make(i);
                n.UserId = userId;
                db.Notifications.Add(n);
            }
            db.SaveChanges();
        });

    [Fact]
    public async Task Inbox_lists_only_own_rows_with_paging_and_filters()
    {
        var me = SettingsTestKit.CreateUser(_f, "inbox-a", "pass12345", Roles.Admin);
        var other = SettingsTestKit.CreateUser(_f, "inbox-b", "pass12345", Roles.Agent);
        var now = DateTime.UtcNow;
        // 30 rows: categories cycle, every 3rd read, 2 rows a week old.
        Seed(me, 30, i => new Notification
        {
            Kind = i % 5 == 0 ? "message" : "system",
            Category = new[] { "messages", "account_health", "flows", "admin", "system" }[i % 5],
            Title = i == 7 ? "Invoice reminder bounced" : $"Event {i}",
            Body = "body",
            Read = i % 3 == 0,
            CreatedAt = i >= 28 ? now.AddDays(-7) : now.AddMinutes(-i),
        });
        // A legacy row (no category) — counts as "messages" via its kind.
        Seed(me, 1, _ => new Notification { Kind = "message", Title = "Legacy", CreatedAt = now });
        Seed(other, 2, i => new Notification { Kind = "system", Category = "admin", Title = $"Not yours {i}", CreatedAt = now });

        var c = await SettingsTestKit.Client(_f, "inbox-a", "pass12345");

        var page1 = await c.GetFromJsonAsync<JsonElement>("/api/notifications?page=1&page_size=10");
        Assert.Equal(31, page1.GetProperty("total").GetInt32());
        Assert.Equal(10, page1.GetProperty("items").GetArrayLength());
        Assert.True(page1.GetProperty("has_more").GetBoolean());
        Assert.DoesNotContain(page1.GetProperty("items").EnumerateArray(),
            n => n.GetProperty("title").GetString()!.StartsWith("Not yours"));

        var page4 = await c.GetFromJsonAsync<JsonElement>("/api/notifications?page=4&page_size=10");
        Assert.Equal(1, page4.GetProperty("items").GetArrayLength());
        Assert.False(page4.GetProperty("has_more").GetBoolean());

        // category: 6 "messages" rows + the legacy one
        var msgs = await c.GetFromJsonAsync<JsonElement>("/api/notifications?category=messages&page_size=100");
        Assert.Equal(7, msgs.GetProperty("total").GetInt32());
        Assert.All(msgs.GetProperty("items").EnumerateArray(),
            n => Assert.Equal("messages", n.GetProperty("category").GetString()));

        var two = await c.GetFromJsonAsync<JsonElement>("/api/notifications?category=flows,admin");
        Assert.Equal(12, two.GetProperty("total").GetInt32());

        // read state: 10 of the 30 are read (i % 3 == 0)
        var unread = await c.GetFromJsonAsync<JsonElement>("/api/notifications?unread=true");
        Assert.Equal(21, unread.GetProperty("total").GetInt32());
        Assert.Equal(21, unread.GetProperty("unread").GetInt32());
        var read = await c.GetFromJsonAsync<JsonElement>("/api/notifications?unread=false");
        Assert.Equal(10, read.GetProperty("total").GetInt32());

        // text search is case-insensitive
        var search = await c.GetFromJsonAsync<JsonElement>("/api/notifications?q=invoice");
        Assert.Equal(1, search.GetProperty("total").GetInt32());

        // date range: only the two week-old rows
        var from = now.AddDays(-8).ToString("yyyy-MM-dd");
        var to = now.AddDays(-6).ToString("yyyy-MM-dd");
        var range = await c.GetFromJsonAsync<JsonElement>($"/api/notifications?from={from}&to={to}");
        Assert.Equal(2, range.GetProperty("total").GetInt32());

        // unread counts per category ride along for the filter chips
        var byCat = page1.GetProperty("unread_by_category");
        Assert.Equal(21, byCat.EnumerateObject().Sum(p => p.Value.GetInt32()));
    }

    [Fact]
    public async Task Read_unread_read_all_mark_and_delete_work_on_own_rows_only()
    {
        var me = SettingsTestKit.CreateUser(_f, "inbox-c", "pass12345", Roles.Agent);
        var other = SettingsTestKit.CreateUser(_f, "inbox-d", "pass12345", Roles.Agent);
        Seed(me, 6, i => new Notification
        {
            Kind = "system", Category = i < 3 ? "flows" : "admin", Title = $"n{i}", CreatedAt = DateTime.UtcNow,
        });
        Seed(other, 1, _ => new Notification { Kind = "system", Category = "admin", Title = "theirs", CreatedAt = DateTime.UtcNow });
        var ids = SettingsTestKit.WithDb(_f, db =>
            db.Notifications.Where(n => n.UserId == me).OrderBy(n => n.Id).Select(n => n.Id).ToList());
        var theirs = SettingsTestKit.WithDb(_f, db => db.Notifications.First(n => n.UserId == other).Id);

        var c = await SettingsTestKit.Client(_f, "inbox-c", "pass12345");

        var r = await (await c.PostAsync($"/api/notifications/{ids[0]}/read", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, r.GetProperty("unread").GetInt32());
        var u = await (await c.PostAsync($"/api/notifications/{ids[0]}/unread", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(6, u.GetProperty("unread").GetInt32());

        // someone else's row is a 404, never touched
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/api/notifications/{theirs}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/notifications/{theirs}")).StatusCode);
        Assert.False(SettingsTestKit.WithDb(_f, db => db.Notifications.First(n => n.Id == theirs).Read));

        // read-all narrowed to one category
        var all = await (await c.PostAsJsonAsync("/api/notifications/read-all", new { category = "flows" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, all.GetProperty("marked").GetInt32());
        Assert.Equal(3, all.GetProperty("unread").GetInt32());

        // bulk mark unread
        var mark = await (await c.PostAsJsonAsync("/api/notifications/mark", new { ids = new[] { ids[0], ids[1] }, read = false }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, mark.GetProperty("unread").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/notifications/mark", new { ids = new[] { ids[0] } })).StatusCode);

        // delete one, delete selected, delete every read row
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/notifications/{ids[5]}")).StatusCode);
        var sel = await (await c.PostAsJsonAsync("/api/notifications/delete", new { ids = new[] { ids[3], ids[4], theirs } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, sel.GetProperty("deleted").GetInt32());   // theirs is silently out of reach
        var clr = await (await c.PostAsJsonAsync("/api/notifications/delete", new { read = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, clr.GetProperty("deleted").GetInt32());   // ids[2] (read via read-all)
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/notifications/delete", new { })).StatusCode);

        var left = SettingsTestKit.WithDb(_f, db => db.Notifications.Count(n => n.UserId == me));
        Assert.Equal(2, left);
        Assert.True(SettingsTestKit.WithDb(_f, db =>
            db.AuditLogs.Any(a => a.Actor == "inbox-c" && a.Action == "notifications.delete")));
    }

    [Fact]
    public async Task Bell_endpoint_keeps_working_and_carries_the_category()
    {
        var me = SettingsTestKit.CreateUser(_f, "inbox-e", "pass12345", Roles.Agent);
        Seed(me, 25, i => new Notification { Kind = "message", Category = "messages", Title = $"m{i}", CreatedAt = DateTime.UtcNow.AddSeconds(-i) });
        var c = await SettingsTestKit.Client(_f, "inbox-e", "pass12345");

        var bell = await c.GetFromJsonAsync<JsonElement>("/api/auth/notifications");
        Assert.Equal(25, bell.GetProperty("unread").GetInt32());
        Assert.Equal(20, bell.GetProperty("notifications").GetArrayLength());
        Assert.Equal("messages", bell.GetProperty("notifications")[0].GetProperty("category").GetString());

        (await c.PostAsJsonAsync("/api/auth/notifications", new { id = "all" })).EnsureSuccessStatusCode();
        bell = await c.GetFromJsonAsync<JsonElement>("/api/auth/notifications");
        Assert.Equal(0, bell.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task Personal_preferences_round_trip_and_validate()
    {
        SettingsTestKit.CreateUser(_f, "inbox-f", "pass12345", Roles.Agent);
        var c = await SettingsTestKit.Client(_f, "inbox-f", "pass12345");

        var prefs = await c.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");
        Assert.Equal(5, prefs.GetProperty("categories").GetArrayLength());
        Assert.True(prefs.GetProperty("user").GetProperty("messages").GetBoolean());
        Assert.False(prefs.GetProperty("only_assigned").GetBoolean());
        Assert.False(prefs.GetProperty("can_manage_org").GetBoolean());

        var put = await c.PutAsJsonAsync("/api/notifications/preferences",
            new { user = new { flows = false }, only_assigned = true });
        put.EnsureSuccessStatusCode();
        prefs = await c.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");
        Assert.False(prefs.GetProperty("user").GetProperty("flows").GetBoolean());
        Assert.True(prefs.GetProperty("user").GetProperty("messages").GetBoolean());
        Assert.True(prefs.GetProperty("only_assigned").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync("/api/notifications/preferences", new { user = new { bogus = true } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync("/api/notifications/preferences", new { only_assigned = "yes" })).StatusCode);
    }
}

/// <summary>
/// Inbound customer messages write notifications through the ONE notifier:
/// assignee → team → admins, one unread per conversation per user, org mutes
/// and personal switches honoured.
/// </summary>
public class NotificationDeliveryTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public NotificationDeliveryTests(ApiFactory f) => _f = f;

    private List<Notification> RowsFor(string username, string key) =>
        SettingsTestKit.WithDb(_f, db =>
        {
            var uid = db.Users.First(u => u.Username == username).Id;
            return db.Notifications.AsNoTracking()
                .Where(n => n.UserId == uid && n.Link.Contains("key=" + key)).ToList();
        });

    private static Task<HttpResponseMessage> Simulate(HttpClient c, string key, string text) =>
        c.PostAsJsonAsync("/api/whatsapp/simulate", new { channel = "whatsapp", key, text, name = "Pat Doe" });

    [Fact]
    public async Task Inbound_message_pages_admins_once_per_conversation_until_read()
    {
        var c = await SettingsTestKit.Client(_f, "admin", "admin123");
        const string key = "60120000001";
        (await Simulate(c, key, "first")).EnsureSuccessStatusCode();
        (await Simulate(c, key, "second")).EnsureSuccessStatusCode();

        foreach (var who in new[] { "admin", "superadmin" })
        {
            var rows = RowsFor(who, key);
            var row = Assert.Single(rows);
            Assert.False(row.Read);
            Assert.Equal("messages", row.Category);
            Assert.Equal("message", row.Kind);
            Assert.Equal("second", row.Body);                      // refreshed, not stacked
            Assert.Equal("New messages from Pat Doe", row.Title);
            Assert.StartsWith("/chats?channel=whatsapp&key=", row.Link);
        }
        Assert.Empty(RowsFor("agent", key));                       // unassigned: agents are not paged

        // once read, the next message opens a fresh unread row
        var id = RowsFor("admin", key).Single().Id;
        (await c.PostAsync($"/api/notifications/{id}/read", null)).EnsureSuccessStatusCode();
        (await Simulate(c, key, "third")).EnsureSuccessStatusCode();
        var after = RowsFor("admin", key);
        Assert.Equal(2, after.Count);
        Assert.Single(after, n => !n.Read);
    }

    [Fact]
    public async Task Assignee_then_team_take_precedence_over_admins()
    {
        var c = await SettingsTestKit.Client(_f, "admin", "admin123");
        const string key = "60120000002";
        (await Simulate(c, key, "hello")).EnsureSuccessStatusCode();
        var adminBefore = RowsFor("admin", key).Single();

        SettingsTestKit.WithDb(_f, db =>
        {
            var t = db.Threads.First(x => x.Channel == "whatsapp" && x.Key == key);
            t.Assignee = "agent";
            db.SaveChanges();
        });
        (await Simulate(c, key, "for the agent")).EnsureSuccessStatusCode();
        Assert.Equal("for the agent", RowsFor("agent", key).Single().Body);
        Assert.Equal(adminBefore.Body, RowsFor("admin", key).Single().Body);   // admins not re-paged

        // unassigned but owned by a team → its members
        var memberId = SettingsTestKit.CreateUser(_f, "team-member-1", "pass12345", Roles.Agent);
        SettingsTestKit.WithDb(_f, db =>
        {
            var team = new Team { Name = "Desk", AutoAssign = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Teams.Add(team);
            db.SaveChanges();
            db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = memberId, CreatedAt = DateTime.UtcNow });
            var t = db.Threads.First(x => x.Channel == "whatsapp" && x.Key == key);
            t.Assignee = "";
            t.TeamId = team.Id;
            db.SaveChanges();
        });
        (await Simulate(c, key, "for the team")).EnsureSuccessStatusCode();
        Assert.Equal("for the team", RowsFor("team-member-1", key).Single().Body);
    }

    [Fact]
    public async Task Organization_mute_silences_the_category()
    {
        var super = await SettingsTestKit.Client(_f, "superadmin", "super123");
        try
        {
            (await super.PutAsJsonAsync("/api/auth/notification-prefs", new { messages = false })).EnsureSuccessStatusCode();
            (await Simulate(super, "60120000003", "muted")).EnsureSuccessStatusCode();
            Assert.Empty(RowsFor("admin", "60120000003"));
            Assert.Empty(RowsFor("superadmin", "60120000003"));
        }
        finally
        {
            (await super.PutAsJsonAsync("/api/auth/notification-prefs", new { messages = true })).EnsureSuccessStatusCode();
        }
        (await Simulate(super, "60120000003", "audible")).EnsureSuccessStatusCode();
        Assert.Single(RowsFor("admin", "60120000003"));
    }

    [Fact]
    public async Task Personal_switches_and_only_assigned_filter_apply_per_user()
    {
        SettingsTestKit.CreateUser(_f, "admin-quiet", "pass12345", Roles.Admin);
        var quiet = await SettingsTestKit.Client(_f, "admin-quiet", "pass12345");
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");

        (await quiet.PutAsJsonAsync("/api/notifications/preferences", new { user = new { messages = false } }))
            .EnsureSuccessStatusCode();
        (await Simulate(admin, "60120000004", "a")).EnsureSuccessStatusCode();
        Assert.Empty(RowsFor("admin-quiet", "60120000004"));
        Assert.Single(RowsFor("admin", "60120000004"));

        (await quiet.PutAsJsonAsync("/api/notifications/preferences",
            new { user = new { messages = true }, only_assigned = true })).EnsureSuccessStatusCode();
        (await Simulate(admin, "60120000005", "b")).EnsureSuccessStatusCode();
        Assert.Empty(RowsFor("admin-quiet", "60120000005"));       // not theirs
        SettingsTestKit.WithDb(_f, db =>
        {
            db.Threads.First(x => x.Key == "60120000005").Assignee = "admin-quiet";
            db.SaveChanges();
        });
        (await Simulate(admin, "60120000005", "c")).EnsureSuccessStatusCode();
        Assert.Single(RowsFor("admin-quiet", "60120000005"));      // now it is
    }

    [Fact]
    public void Notifier_fails_open_on_garbage_and_collapses_by_key()
    {
        using var t = new TestDb();
        var db = t.Db;
        var tenant = new Tenant { Name = "T", Slug = "t", Status = "active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        var u = new AppUser { Username = "a", Password = "x", IsActive = true };
        db.Users.Add(u);
        db.SaveChanges();
        db.Profiles.Add(new Profile { UserId = u.Id, Role = Roles.Admin, TenantId = tenant.Id, Preferences = "{not json" });
        db.Settings.Add(new Setting { Key = "notif_flows", Value = " FALSE ", UpdatedAt = DateTime.UtcNow });
        db.Settings.Add(new Setting { Key = "notif_admin", Value = "garbage", UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();

        // muted category writes nothing; an unreadable mute value fails OPEN
        Assert.Equal(0, NotificationService.Notify(db, "flows", "system", "x", "", "", NotificationAudience.Admins()));
        Assert.Equal(1, NotificationService.Notify(db, "admin", "system", "x", "", "", NotificationAudience.Admins()));

        // collapse key: the second write refreshes the unread row
        var opts = new NotifyOptions { RefKey = "thread:9", CollapsedTitle = "again" };
        NotificationService.Notify(db, "system", "system", "first", "1", "", NotificationAudience.Admins(), opts);
        NotificationService.Notify(db, "system", "system", "first", "2", "", NotificationAudience.Admins(), opts);
        var row = Assert.Single(db.Notifications.Where(n => n.RefKey == "thread:9"));
        Assert.Equal("again", row.Title);
        Assert.Equal("2", row.Body);

        // Save = false leaves the commit to the caller
        NotificationService.Notify(db, "system", "system", "later", "", "", NotificationAudience.Admins(),
            new NotifyOptions { Save = false });
        using var other = t.Fresh();
        Assert.False(other.Notifications.Any(n => n.Title == "later"));
        db.SaveChanges();
        Assert.True(other.Notifications.Any(n => n.Title == "later"));

        // unknown category falls back by kind
        Assert.Equal("messages", NotificationCategories.Normalize("", "message"));
        Assert.Equal("admin", NotificationCategories.Normalize("nope", "system"));
    }
}

/// <summary>Settings sub-pages are capability-gated; appearance preferences.</summary>
public class SettingsCapabilityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public SettingsCapabilityTests(ApiFactory f) => _f = f;

    private static readonly object Look = new
    {
        appearance = new
        {
            theme = "graphite", mode = "dark", accent = "#2f7d4f", textSize = "lg", radius = "round", headingFont = "sans",
        },
    };

    [Fact]
    public async Task Custom_role_with_one_settings_capability_can_use_only_that_page()
    {
        var looks = await SettingsTestKit.CustomRoleClient(_f, "Look Keeper", "settings.appearance.org");
        Assert.Equal(HttpStatusCode.OK, (await looks.PutAsJsonAsync("/api/settings/appearance", Look)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await looks.PutAsJsonAsync("/api/auth/notification-prefs", new { flows = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await looks.PutAsJsonAsync("/api/settings/ai-providers", new { chat_provider_pref = "" })).StatusCode);
        var who = await looks.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        Assert.Contains(who.GetProperty("capabilities").EnumerateArray(),
            c => c.GetString() == "settings.appearance.org");
        var prefs = await looks.GetFromJsonAsync<JsonElement>("/api/auth/preferences");
        Assert.True(prefs.GetProperty("can_set_org_default").GetBoolean());

        var notif = await SettingsTestKit.CustomRoleClient(_f, "Bell Ringer", "notifications.manage");
        Assert.Equal(HttpStatusCode.OK,
            (await notif.PutAsJsonAsync("/api/auth/notification-prefs", new { flows = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await notif.PutAsJsonAsync("/api/settings/appearance", Look)).StatusCode);
        var np = await notif.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");
        Assert.True(np.GetProperty("can_manage_org").GetBoolean());

        var ai = await SettingsTestKit.CustomRoleClient(_f, "Model Wrangler", "settings.ai_keys.manage");
        Assert.Equal(HttpStatusCode.OK,
            (await ai.PutAsJsonAsync("/api/settings/ai-providers", new { chat_provider_pref = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ai.PostAsJsonAsync("/api/whatsapp/settings", new { quiet_start = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ai.PutAsJsonAsync("/api/whatsapp/routing-rules", new { cold_text_guard = "block" })).StatusCode);

        // the built-ins keep their old reach: admin may not touch AI keys, superadmin may
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PutAsJsonAsync("/api/settings/ai-providers", new { chat_provider_pref = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync("/api/whatsapp/routing-rules", new { cold_text_guard = "block" })).StatusCode);
        var super = await SettingsTestKit.Client(_f, "superadmin", "super123");
        Assert.Equal(HttpStatusCode.OK,
            (await super.PutAsJsonAsync("/api/settings/ai-providers", new { chat_provider_pref = "" })).StatusCode);

        // the Permissions page lists the new capabilities under one Settings area
        var perms = await super.GetFromJsonAsync<JsonElement>("/api/auth/permissions");
        var settingsCaps = perms.GetProperty("catalog").EnumerateArray()
            .Where(c => c.GetProperty("area").GetString() == "Settings")
            .Select(c => c.GetProperty("capability").GetString()).ToList();
        Assert.Contains("settings.appearance.org", settingsCaps);
        Assert.Contains("settings.custom_fields.manage", settingsCaps);
        Assert.Contains("settings.ai_keys.manage", settingsCaps);
        Assert.Contains("notifications.manage", settingsCaps);
    }

    [Fact]
    public async Task Appearance_preferences_round_trip_validate_and_fall_back_to_the_org_default()
    {
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        var start = await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences");
        Assert.Equal(JsonValueKind.Null, start.GetProperty("appearance").ValueKind);
        Assert.False(start.GetProperty("can_set_org_default").GetBoolean());

        var saved = await agent.PutAsJsonAsync("/api/auth/preferences", Look);
        saved.EnsureSuccessStatusCode();
        var mine = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("appearance");
        Assert.Equal("graphite", mine.GetProperty("theme").GetString());
        Assert.Equal("#2F7D4F", mine.GetProperty("accent").GetString());

        // a partial object fills the defaults; bad values and unknown keys are 400s
        var partial = await agent.PutAsJsonAsync("/api/auth/preferences", new { appearance = new { mode = "system" } });
        var p = (await partial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("appearance");
        Assert.Equal("paper", p.GetProperty("theme").GetString());
        Assert.Equal("system", p.GetProperty("mode").GetString());
        foreach (var bad in new object[]
                 {
                     new { appearance = new { theme = "neon" } },
                     new { appearance = new { accent = "orange" } },
                     new { appearance = new { fontFamily = "comic" } },
                     new { look = new { } },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await agent.PutAsJsonAsync("/api/auth/preferences", bad)).StatusCode);

        // org default: agent may not set it, admin may; a user without a choice inherits it
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PutAsJsonAsync("/api/settings/appearance", Look)).StatusCode);
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        (await admin.PutAsJsonAsync("/api/settings/appearance",
            new { appearance = new { theme = "soft", mode = "light" } })).EnsureSuccessStatusCode();
        (await agent.PutAsJsonAsync("/api/auth/preferences", new { appearance = (object?)null })).EnsureSuccessStatusCode();
        var inherited = await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences");
        Assert.Equal(JsonValueKind.Null, inherited.GetProperty("appearance").ValueKind);
        Assert.Equal("soft", inherited.GetProperty("org_default").GetProperty("theme").GetString());
        var org = await agent.GetFromJsonAsync<JsonElement>("/api/settings/appearance");
        Assert.Equal("soft", org.GetProperty("appearance").GetProperty("theme").GetString());

        (await admin.PutAsJsonAsync("/api/settings/appearance", new { appearance = (object?)null })).EnsureSuccessStatusCode();
        org = await agent.GetFromJsonAsync<JsonElement>("/api/settings/appearance");
        Assert.Equal(JsonValueKind.Null, org.GetProperty("appearance").ValueKind);
        Assert.True(SettingsTestKit.WithDb(_f, db => db.AuditLogs.Any(a => a.Actor == "admin" && a.Target == "appearance")));
    }

    [Fact]
    public async Task Language_preference_round_trips_validates_and_falls_back_to_the_org_default()
    {
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        var start = await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences");
        Assert.Equal(JsonValueKind.Null, start.GetProperty("locale").ValueKind);

        // the user's own language, without touching their appearance
        var saved = await agent.PutAsJsonAsync("/api/auth/preferences", new { locale = "ms" });
        saved.EnsureSuccessStatusCode();
        var mine = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ms", mine.GetProperty("locale").GetString());
        (await agent.PutAsJsonAsync("/api/auth/preferences", new { locale = "ZH" })).EnsureSuccessStatusCode();
        Assert.Equal("zh", (await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences")).GetProperty("locale").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await agent.PutAsJsonAsync("/api/auth/preferences", new { locale = "fr" })).StatusCode);

        // the organization default: admin only; a user without a choice inherits it
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync("/api/settings/appearance", new { locale = "ms" })).StatusCode);
        (await admin.PutAsJsonAsync("/api/settings/appearance", new { locale = "ms" })).EnsureSuccessStatusCode();
        (await agent.PutAsJsonAsync("/api/auth/preferences", new { locale = (string?)null })).EnsureSuccessStatusCode();
        var inherited = await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences");
        Assert.Equal(JsonValueKind.Null, inherited.GetProperty("locale").ValueKind);
        Assert.Equal("ms", inherited.GetProperty("org_locale").GetString());
        Assert.Equal("ms", (await agent.GetFromJsonAsync<JsonElement>("/api/settings/appearance")).GetProperty("locale").GetString());

        (await admin.PutAsJsonAsync("/api/settings/appearance", new { locale = (string?)null })).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null,
            (await agent.GetFromJsonAsync<JsonElement>("/api/auth/preferences")).GetProperty("org_locale").ValueKind);
    }
}

/// <summary>POST /api/whatsapp/assignments/rebalance — even spread, pins stay.</summary>
public class AssignmentsRebalanceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AssignmentsRebalanceTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Rebalance_spreads_unpinned_contacts_evenly_and_never_moves_pins()
    {
        var (a, b, down, contacts) = SettingsTestKit.WithDb(_f, db =>
        {
            ChannelAccount Acc(string name, string health) => new()
            {
                Channel = "whatsapp", Provider = "baileys", Name = name, HealthStatus = health,
                CreatedAt = DateTime.UtcNow,
            };
            var aa = Acc("Desk A", "healthy");
            var bb = Acc("Desk B", "unknown");
            var cc = Acc("Desk C", "banned");
            db.ChannelAccounts.AddRange(aa, bb, cc);
            var people = Enumerable.Range(1, 7).Select(i => new Contact
            {
                Name = $"Rebalance {i}", Phone = $"+6019000000{i}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            }).ToList();
            db.Contacts.AddRange(people);
            db.SaveChanges();
            void Pin(Contact c, ChannelAccount acc, bool pinned) => db.AccountAssignments.Add(new AccountAssignment
            {
                ContactId = c.Id, Channel = "whatsapp", AccountId = acc.Id, Pinned = pinned,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            Pin(people[0], aa, true);
            for (var i = 1; i < 5; i++) Pin(people[i], aa, false);
            Pin(people[5], cc, false);
            Pin(people[6], cc, true);
            db.SaveChanges();
            return (aa.Id, bb.Id, cc.Id, people.Select(p => p.Id).ToList());
        });

        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/whatsapp/assignments/rebalance", new { channel = "whatsapp" })).StatusCode);

        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        var res = await admin.PostAsJsonAsync("/api/whatsapp/assignments/rebalance", new { channel = "whatsapp" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("moved").GetInt32());
        Assert.Equal(2, body.GetProperty("pinned").GetInt32());
        var counts = body.GetProperty("accounts");
        Assert.Equal(3, counts.GetProperty(a.ToString()).GetInt32());
        Assert.Equal(3, counts.GetProperty(b.ToString()).GetInt32());
        Assert.Equal(1, counts.GetProperty(down.ToString()).GetInt32());   // the pinned one stays put

        var rows = SettingsTestKit.WithDb(_f, db => db.AccountAssignments.AsNoTracking()
            .Where(x => contacts.Contains(x.ContactId)).ToList());
        Assert.Equal(a, rows.Single(x => x.ContactId == contacts[0]).AccountId);
        Assert.Equal(down, rows.Single(x => x.ContactId == contacts[6]).AccountId);
        Assert.NotEqual(down, rows.Single(x => x.ContactId == contacts[5]).AccountId);

        // already balanced → nothing moves; audit trail written
        var again = await (await admin.PostAsJsonAsync("/api/whatsapp/assignments/rebalance", new { channel = "whatsapp" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, again.GetProperty("moved").GetInt32());
        Assert.True(SettingsTestKit.WithDb(_f, db => db.AuditLogs.Any(x => x.Action == "assignment.rebalance")));

        // a channel with no healthy account is a clear 409
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/whatsapp/assignments/rebalance", new { channel = "telegram" })).StatusCode);
    }

    [Fact]
    public async Task Custom_role_with_account_assignment_capability_may_rebalance()
    {
        var c = await SettingsTestKit.CustomRoleClient(_f, "Router", "account_assignment.manage");
        // no telegram accounts → 409 proves the gate let it through
        Assert.Equal(HttpStatusCode.Conflict,
            (await c.PostAsJsonAsync("/api/whatsapp/assignments/rebalance", new { channel = "telegram" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await c.PutAsJsonAsync("/api/settings/appearance", new { appearance = new { theme = "paper" } })).StatusCode);
    }
}
