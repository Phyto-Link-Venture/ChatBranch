using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The AI controls + handover HTTP surface: the master switch / schedule
/// (GET/PUT /api/ai/controls), the contact AI mode, the inbox bulk endpoint
/// (caps, scope, the 1000 cap), the handover filter + counts on
/// GET /api/inbox/threads, take / let-the-AI-continue / flag, clearing on a
/// person's reply and on resolve, and the handover stats. The class shares one
/// database, so every test uses its own keys and its own entity.
/// </summary>
public class AiControlsApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AiControlsApiTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user = "admin", string pass = "admin123")
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private T WithDb<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _factory.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task Simulate(HttpClient c, string channel, string key, string text)
    {
        var res = await c.PostAsJsonAsync("/api/whatsapp/simulate", new { channel, key, text });
        res.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> Row(HttpClient c, string channel, string key)
    {
        var body = await c.GetFromJsonAsync<JsonElement>($"/api/inbox/threads?thread={channel}:{Uri.EscapeDataString(key)}");
        return body.GetProperty("threads").EnumerateArray().Single();
    }

    private static async Task<int> Count(HttpClient c, string query)
    {
        var body = await c.GetFromJsonAsync<JsonElement>($"/api/inbox/threads?count_only=1&{query}");
        return body.GetProperty("count").GetInt32();
    }

    private static async Task<List<string>> Keys(HttpClient c, string query)
    {
        var body = await c.GetFromJsonAsync<JsonElement>($"/api/inbox/threads?limit=200&{query}");
        return body.GetProperty("threads").EnumerateArray().Select(r => r.GetProperty("key").GetString()!).ToList();
    }

    /// <summary>An entity of its own (child of the seeded main one) with one email conversation in it.</summary>
    private (int EntityId, string Slug) Outlet(string slug, string name, string? threadKey = null)
    {
        return WithDb(db =>
        {
            var existing = db.Entities.FirstOrDefault(e => e.Slug == slug);
            var tenant = db.Tenants.First();
            var e = existing ?? new Entity { TenantId = tenant.Id, Name = name, Slug = slug, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            if (existing == null) { db.Entities.Add(e); db.SaveChanges(); }
            if (threadKey != null)
            {
                var t = db.Threads.FirstOrDefault(x => x.Channel == "email" && x.Key == threadKey);
                if (t != null) { t.EntityId = e.Id; db.SaveChanges(); }
            }
            return (e.Id, e.Slug);
        });
    }

    // =========================================================================
    // master switch + schedule
    // =========================================================================

    [Fact]
    public async Task Master_switch_per_outlet_shows_on_the_thread_with_its_reason()
    {
        var admin = await Authed();
        await Simulate(admin, "email", "master-off@example.com", "hello");
        var (_, slug) = Outlet("aic-north", "North Branch", "master-off@example.com");

        var before = await Row(admin, "email", "master-off@example.com");
        Assert.True(before.TryGetProperty("ai_reason", out _));

        var put = await admin.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { mode = "off" });
        put.EnsureSuccessStatusCode();
        var payload = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("off", payload.GetProperty("mode").GetString());
        Assert.Equal("entity", payload.GetProperty("mode_from").GetString());
        Assert.Equal("AI is OFF for North Branch", payload.GetProperty("label").GetString());
        Assert.False(payload.GetProperty("now_on").GetBoolean());

        var row = await Row(admin, "email", "master-off@example.com");
        Assert.False(row.GetProperty("ai_enabled").GetBoolean());
        Assert.Equal("Off: AI is switched off for North Branch", row.GetProperty("ai_reason").GetString());
        Assert.True(row.GetProperty("ai_forced").GetBoolean());
        var detail = await admin.GetFromJsonAsync<JsonElement>("/api/messaging/threads/email/master-off@example.com");
        Assert.Equal("Off: AI is switched off for North Branch", detail.GetProperty("ai_reason").GetString());
        // the list's ai filter follows the same rule
        Assert.Contains("master-off@example.com", await Keys(admin, $"ai=off&entity={slug}"));

        // org-wide is untouched; inherit drops the outlet's own row
        var org = await admin.GetFromJsonAsync<JsonElement>("/api/ai/controls");
        Assert.Equal("on", org.GetProperty("mode").GetString());
        (await admin.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { mode = "inherit" })).EnsureSuccessStatusCode();
        var back = await admin.GetFromJsonAsync<JsonElement>($"/api/ai/controls?entity={slug}");
        Assert.Equal("on", back.GetProperty("mode").GetString());
        Assert.Equal("", back.GetProperty("own").GetProperty("mode").GetString());
        Assert.Contains(WithDb(db => db.AuditLogs.Where(a => a.Action == "ai.controls.update" && a.Target == $"entity:{slug}")
            .Select(a => a.Detail).ToList()), d => d.Contains("ai_enabled=off"));
    }

    [Fact]
    public async Task Schedule_needs_a_window_and_reports_now_and_next_change()
    {
        var admin = await Authed();
        var (_, slug) = Outlet("aic-sched", "Night Desk");
        var noWindows = await admin.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { mode = "schedule" });
        Assert.Equal(HttpStatusCode.BadRequest, noWindows.StatusCode);
        var badMode = await admin.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { mode = "sometimes" });
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);

        // every day, all day → inside = always on, outside = always off
        var allDay = new Dictionary<string, object> { ["mon"] = new[] { new[] { "00:00", "00:00" } } };
        foreach (var d in new[] { "tue", "wed", "thu", "fri", "sat", "sun" }) allDay[d] = allDay["mon"];
        var put = await admin.PutAsJsonAsync($"/api/ai/controls?entity={slug}",
            new { mode = "schedule", schedule = allDay, schedule_mode = "inside" });
        put.EnsureSuccessStatusCode();
        var p = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(p.GetProperty("now_on").GetBoolean());
        Assert.Equal("00:00", p.GetProperty("schedule").GetProperty("sun")[0][0].GetString());
        Assert.Contains("on a schedule", p.GetProperty("label").GetString());

        var outside = await admin.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { schedule_mode = "outside" });
        Assert.False((await outside.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("now_on").GetBoolean());

        // switching on and back to the schedule keeps the stored hours
        (await admin.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { mode = "on" })).EnsureSuccessStatusCode();
        var again = await admin.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { mode = "schedule" });
        again.EnsureSuccessStatusCode();
        Assert.Equal("schedule", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mode").GetString());
        (await admin.PutAsJsonAsync($"/api/ai/controls?entity={slug}", new { mode = "inherit", schedule = (object?)null, schedule_mode = "" }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Controls_are_readable_by_agents_but_only_managers_change_them()
    {
        var agent = await Authed("agent", "agent123");
        var get = await agent.GetAsync("/api/ai/controls");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.False((await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("can_manage").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PutAsJsonAsync("/api/ai/controls", new { mode = "off" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/ai/controls")).StatusCode);

        var admin = await Authed();
        Outlet("aic-tags", "Tag Outlet");
        var never = await admin.PutAsJsonAsync("/api/ai/controls?entity=aic-tags",
            new { never_tags = new[] { "VIP", " legal ", "vip" } });
        never.EnsureSuccessStatusCode();
        var tags = (await never.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("own").GetProperty("never_tags")
            .EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(new[] { "VIP", "legal" }, tags);
        (await admin.PutAsJsonAsync("/api/ai/controls?entity=aic-tags", new { never_tags = Array.Empty<string>() })).EnsureSuccessStatusCode();
    }

    // =========================================================================
    // contact AI mode
    // =========================================================================

    [Fact]
    public async Task Contact_ai_mode_reads_writes_and_reaches_the_thread()
    {
        var admin = await Authed();
        var created = await admin.PostAsJsonAsync("/api/contacts", new { name = "Mode Person", phone = "+60129990001", email = "mode-person@example.com" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        await Simulate(admin, "email", "mode-person@example.com", "hi there");

        var get = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}/ai-mode");
        Assert.Equal("", get.GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync($"/api/contacts/{id}/ai-mode", new { mode = "sometimes" })).StatusCode);
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync($"/api/contacts/{id}/ai-mode", new { mode = "never" })).StatusCode);

        var put = await admin.PutAsJsonAsync($"/api/contacts/{id}/ai-mode", new { mode = "never" });
        put.EnsureSuccessStatusCode();
        Assert.Equal("never", (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mode").GetString());
        var row = await Row(admin, "email", "mode-person@example.com");
        Assert.False(row.GetProperty("ai_enabled").GetBoolean());
        Assert.Equal("Off: this contact is set to never use AI", row.GetProperty("ai_reason").GetString());
        Assert.Equal(ContactAiModes.Never, WithDb(db => db.Contacts.Single(c => c.Id == id).AiMode));

        (await admin.PutAsJsonAsync($"/api/contacts/{id}/ai-mode", new { mode = "always" })).EnsureSuccessStatusCode();
        row = await Row(admin, "email", "mode-person@example.com");
        Assert.True(row.GetProperty("ai_enabled").GetBoolean());
        Assert.Equal("On: this contact is set to always use AI", row.GetProperty("ai_reason").GetString());
        Assert.Contains(WithDb(db => db.AuditLogs.Where(a => a.Action == "contact.ai_mode").Select(a => a.Detail).ToList()),
            d => d == "never → always");
    }

    // =========================================================================
    // bulk
    // =========================================================================

    [Fact]
    public async Task Bulk_needs_chats_manage_and_acts_on_picked_threads_or_a_filter()
    {
        var admin = await Authed();
        await Simulate(admin, "email", "bulk-a@example.com", "bulk unique zebra");
        await Simulate(admin, "email", "bulk-b@example.com", "bulk unique zebra");
        await Simulate(admin, "email", "bulk-c@example.com", "something else");

        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/inbox/threads/bulk",
            new { threads = new[] { "email:bulk-a@example.com" }, action = "ai_off" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/inbox/threads/bulk",
            new { threads = new[] { "email:bulk-a@example.com" }, action = "explode" })).StatusCode);

        var off = await admin.PostAsJsonAsync("/api/inbox/threads/bulk",
            new { threads = new[] { "email:bulk-a@example.com", "email:bulk-b@example.com", "email:nope@example.com" }, action = "ai_off" });
        off.EnsureSuccessStatusCode();
        var o = await off.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, o.GetProperty("matched").GetInt32());
        Assert.Equal("email:nope@example.com", o.GetProperty("skipped")[0].GetString());
        Assert.False((await Row(admin, "email", "bulk-a@example.com")).GetProperty("ai_enabled").GetBoolean());
        Assert.Equal("Off: switched off for this chat",
            (await Row(admin, "email", "bulk-b@example.com")).GetProperty("ai_reason").GetString());

        // everything a list filter matches — the same parameters as GET
        var resolve = await admin.PostAsJsonAsync("/api/inbox/threads/bulk",
            new { filter = new { q = "bulk unique zebra", channel = new[] { "email" } }, action = "resolve" });
        resolve.EnsureSuccessStatusCode();
        Assert.Equal(2, (await resolve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matched").GetInt32());
        Assert.Equal("resolved", (await Row(admin, "email", "bulk-a@example.com")).GetProperty("status").GetString());
        Assert.Equal("open", (await Row(admin, "email", "bulk-c@example.com")).GetProperty("status").GetString());

        var assign = await admin.PostAsJsonAsync("/api/inbox/threads/bulk",
            new { threads = new[] { "email:bulk-c@example.com" }, action = "assign", value = "agent" });
        assign.EnsureSuccessStatusCode();
        Assert.Equal("agent", (await Row(admin, "email", "bulk-c@example.com")).GetProperty("assignee").GetString());
        Assert.Contains(WithDb(db => db.AuditLogs.Where(a => a.Action == "inbox.bulk").Select(a => a.Target).ToList()),
            t => t == "resolve ×2");
    }

    [Fact]
    public async Task Bulk_refuses_more_than_a_thousand()
    {
        var admin = await Authed();
        WithDb(db =>
        {
            var now = DateTime.UtcNow;
            var rows = Enumerable.Range(0, 1001).Select(i => new Domain.Thread
            {
                Channel = "website", Key = $"bulkcap-{i:D4}", Name = "bulkcap visitor",
                Status = "open", CreatedAt = now, UpdatedAt = now,
            }).ToList();
            db.Threads.AddRange(rows);
            db.SaveChanges();
            // the list only holds conversations something was said in
            db.Messages.AddRange(rows.Select(t => new Domain.Message
            {
                ThreadId = t.Id, Direction = Domain.MessageDirection.In, Text = "hi", CreatedAt = now,
            }));
            return db.SaveChanges();
        });
        var res = await admin.PostAsJsonAsync("/api/inbox/threads/bulk",
            new { filter = new { q = "bulkcap", channel = "website" }, action = "ai_off" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(1001, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matched").GetInt32());
        var tooMany = await admin.PostAsJsonAsync("/api/inbox/threads/bulk",
            new { threads = Enumerable.Range(0, 1001).Select(i => $"website:bulkcap-{i:D4}").ToArray(), action = "ai_off" });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        // nothing was touched
        Assert.Equal(0, WithDb(db => db.Threads.Count(t => t.Key.StartsWith("bulkcap-") && !t.AiEnabled)));
    }

    [Fact]
    public async Task Bulk_is_scoped_like_the_list()
    {
        var admin = await Authed();
        await Simulate(admin, "email", "scope-east@example.com", "scoped bulk kiwi");
        await Simulate(admin, "email", "scope-west@example.com", "scoped bulk kiwi");
        var (eastId, _) = Outlet("aic-east", "East Outlet", "scope-east@example.com");
        Outlet("aic-west", "West Outlet", "scope-west@example.com");
        WithDb(db =>
        {
            db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent").Entities
                .Add(db.Entities.First(e => e.Id == eastId));
            db.RolePermissions.Add(new RolePermission { Role = Roles.Agent, Capability = "chats.manage", Allowed = true, UpdatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        try
        {
            var agent = await Authed("agent", "agent123");
            var picked = await agent.PostAsJsonAsync("/api/inbox/threads/bulk",
                new { threads = new[] { "email:scope-east@example.com", "email:scope-west@example.com" }, action = "ai_off" });
            picked.EnsureSuccessStatusCode();
            var p = await picked.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, p.GetProperty("matched").GetInt32());
            Assert.Equal("email:scope-west@example.com", p.GetProperty("skipped")[0].GetString());

            var filtered = await agent.PostAsJsonAsync("/api/inbox/threads/bulk",
                new { filter = new { q = "scoped bulk kiwi" }, action = "assign", value = "agent" });
            filtered.EnsureSuccessStatusCode();
            Assert.Equal(1, (await filtered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matched").GetInt32());
            Assert.True(WithDb(db => db.Threads.Single(t => t.Key == "scope-west@example.com").AiEnabled));
            Assert.Equal("", WithDb(db => db.Threads.Single(t => t.Key == "scope-west@example.com").Assignee));
        }
        finally
        {
            WithDb(db =>
            {
                db.RolePermissions.RemoveRange(db.RolePermissions.Where(x => x.Role == Roles.Agent && x.Capability == "chats.manage"));
                var prof = db.Profiles.Include(x => x.Entities).First(x => x.User!.Username == "agent");
                prof.Entities.Clear();
                return db.SaveChanges();
            });
        }
    }

    // =========================================================================
    // handover: filter, counts, take / continue / flag, clearing, stats
    // =========================================================================

    [Fact]
    public async Task Handover_filter_counts_take_continue_and_clearing()
    {
        var admin = await Authed();
        await Simulate(admin, "email", "ho-one@example.com", "handover candidate");
        await Simulate(admin, "email", "ho-two@example.com", "handover candidate");

        var openBefore = await Count(admin, "handover=open");
        var flag = await admin.PostAsJsonAsync("/api/inbox/threads/email/ho-one@example.com/handover",
            new { action = "flag", reason = "Wants the manager" });
        flag.EnsureSuccessStatusCode();
        var f = await flag.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(f.GetProperty("handover").GetProperty("open").GetBoolean());
        Assert.Equal("manual", f.GetProperty("handover").GetProperty("kind").GetString());
        Assert.Equal("agent:admin", f.GetProperty("handover").GetProperty("by").GetString());
        Assert.False(f.GetProperty("ai_enabled").GetBoolean());
        Assert.Equal("Off: handed to a person — waiting for a human", f.GetProperty("ai_reason").GetString());

        Assert.Equal(openBefore + 1, await Count(admin, "handover=open"));
        Assert.Contains("ho-one@example.com", await Keys(admin, "handover=open"));
        Assert.Contains("ho-one@example.com", await Keys(admin, "handover_kind=manual"));
        Assert.DoesNotContain("ho-one@example.com", await Keys(admin, "handover=none"));
        Assert.Contains("ho-two@example.com", await Keys(admin, "handover=none"));
        var row = await Row(admin, "email", "ho-one@example.com");
        Assert.Equal("Wants the manager", row.GetProperty("handover").GetProperty("reason").GetString());
        Assert.Equal("pending", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, (await Row(admin, "email", "ho-two@example.com")).GetProperty("handover").ValueKind);
        var live = await admin.GetFromJsonAsync<JsonElement>("/api/live");
        Assert.True(live.GetProperty("needs_human_count").GetInt32() >= 1);

        // Take it: assigned to me, still waiting for the reply
        var take = await admin.PostAsJsonAsync("/api/inbox/threads/email/ho-one@example.com/handover", new { action = "take" });
        take.EnsureSuccessStatusCode();
        Assert.Equal("admin", (await take.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("assignee").GetString());
        Assert.Contains("ho-one@example.com", await Keys(admin, "handover=open"));

        // a person's reply answers it (fields kept for history)
        (await admin.PostAsJsonAsync("/api/messaging/threads/email/ho-one@example.com/send", new { text = "I'm on it." }))
            .EnsureSuccessStatusCode();
        Assert.DoesNotContain("ho-one@example.com", await Keys(admin, "handover=open"));
        Assert.Contains("ho-one@example.com", await Keys(admin, "handover=any"));
        var answered = (await Row(admin, "email", "ho-one@example.com")).GetProperty("handover");
        Assert.False(answered.GetProperty("open").GetBoolean());
        Assert.Equal(JsonValueKind.String, answered.GetProperty("resolved_at").ValueKind);

        // handed over again → counted; resolving clears it
        (await admin.PostAsJsonAsync("/api/inbox/threads/email/ho-one@example.com/handover", new { action = "flag" })).EnsureSuccessStatusCode();
        Assert.Equal(2, (await Row(admin, "email", "ho-one@example.com")).GetProperty("handover").GetProperty("count").GetInt32());
        (await admin.PostAsJsonAsync("/api/messaging/threads/email/ho-one@example.com/status", new { status = "resolved" })).EnsureSuccessStatusCode();
        Assert.False((await Row(admin, "email", "ho-one@example.com")).GetProperty("handover").GetProperty("open").GetBoolean());

        // Let AI continue: clears + the chatbot back on
        (await admin.PostAsJsonAsync("/api/inbox/threads/email/ho-two@example.com/handover", new { action = "flag", reason = "check" }))
            .EnsureSuccessStatusCode();
        var cont = await admin.PostAsJsonAsync("/api/inbox/threads/email/ho-two@example.com/handover", new { action = "continue" });
        cont.EnsureSuccessStatusCode();
        var c = await cont.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(c.GetProperty("handover").GetProperty("open").GetBoolean());
        Assert.True(c.GetProperty("ai_enabled").GetBoolean());
        Assert.Equal("open", c.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/inbox/threads/email/ho-two@example.com/handover", new { action = "dance" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync("/api/inbox/threads/email/missing@example.com/handover", new { action = "take" })).StatusCode);
    }

    [Fact]
    public async Task Handover_stats_by_kind_over_time()
    {
        var admin = await Authed();
        await Simulate(admin, "email", "stats-a@example.com", "stats");
        await Simulate(admin, "email", "stats-b@example.com", "stats");
        (await admin.PostAsJsonAsync("/api/inbox/threads/email/stats-a@example.com/handover", new { action = "flag", reason = "x" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/inbox/threads/email/stats-b@example.com/handover", new { action = "flag", reason = "y" })).EnsureSuccessStatusCode();

        var s = await admin.GetFromJsonAsync<JsonElement>("/api/ai/handovers/stats?days=7");
        Assert.Equal(7, s.GetProperty("days").GetInt32());
        Assert.True(s.GetProperty("total").GetInt32() >= 2);
        Assert.True(s.GetProperty("open").GetInt32() >= 2);
        Assert.True(s.GetProperty("handed_over_conversations").GetInt32() >= 2);
        Assert.InRange(s.GetProperty("rate").GetDouble(), 0.0, 1.0);          // never above 100%
        Assert.Equal(7, s.GetProperty("series").GetArrayLength());
        Assert.True(s.GetProperty("series")[6].GetProperty("handovers").GetInt32() >= 2);
        Assert.Contains(s.GetProperty("by_kind").EnumerateArray(),
            k => k.GetProperty("kind").GetString() == "manual" && k.GetProperty("count").GetInt32() >= 2);
        Assert.Contains(s.GetProperty("by_source").EnumerateArray(), k => k.GetProperty("by").GetString() == "agent");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/ai/handovers/stats")).StatusCode);
    }
}
