using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// QA batch 2 — entity-bound agents see only their own subtree on the
/// dashboard, the channel lists, the AI controls, journeys, teams and bindings;
/// quota writes are tiered and gated by a WRITE capability; the AI features
/// that used to skip quotas refuse at the cap; bad input answers 400 (never
/// 500); bindings follow the /api/v1/users tiers; assignees must be able to see
/// the conversation; and send-time routing never auto-assigns an unattributed
/// contact to an outlet's number.
/// </summary>
public class QaScopeFixesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public QaScopeFixesTests(ApiFactory f) => _f = f;

    private T With<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // ---- fixtures -------------------------------------------------------------------------

    private record World(int Mine, int MineKl, int Other, string MineKey, string OtherKey, int OtherAccount, int OtherContact);

    /// <summary>"qa-mine" (+ branch) for the agent, "qa-other" another company with its
    /// own number, conversation, flow, team and health history.</summary>
    private World Seed() => With(db =>
    {
        var tenant = db.Tenants.First();
        var now = DateTime.UtcNow;
        Entity Get(string slug, int? parent)
        {
            var e = db.Entities.FirstOrDefault(x => x.Slug == slug);
            if (e != null) return e;
            e = new Entity { TenantId = tenant.Id, Name = slug.ToUpperInvariant(), Slug = slug, ParentId = parent,
                CreatedAt = now, UpdatedAt = now };
            db.Entities.Add(e);
            db.SaveChanges();
            return e;
        }
        var mine = Get("qa-mine", null);
        var kl = Get("qa-mine-kl", mine.Id);
        var other = Get("qa-other", null);

        var account = db.ChannelAccounts.FirstOrDefault(a => a.Name == "QA-OTHER line");
        if (account == null)
        {
            account = new ChannelAccount
            {
                Channel = "whatsapp", Provider = Providers.Baileys, Name = "QA-OTHER line", ExternalId = "qa-other-1",
                EntityId = other.Id, CreatedAt = now,
            };
            db.ChannelAccounts.Add(account);
            db.SaveChanges();
        }

        (string Key, int Contact) Conversation(Entity ent, string key, string label, int? accountId)
        {
            var contact = db.Contacts.FirstOrDefault(c => c.Phone == key);
            if (contact == null)
            {
                contact = new Contact { Name = $"QA {ent.Slug}", Phone = key, TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
                db.Contacts.Add(contact);
                db.SaveChanges();
                db.ContactEntities.Add(new ContactEntity { ContactId = contact.Id, EntityId = ent.Id, CreatedAt = now });
                var thread = new Thread
                {
                    Channel = "whatsapp", Key = key, Address = key, Name = contact.Name, ContactId = contact.Id,
                    EntityId = ent.Id, AccountId = accountId, Labels = new List<string> { label },
                    CreatedAt = now, UpdatedAt = now,
                };
                db.Threads.Add(thread);
                db.SaveChanges();
                db.Messages.Add(new Message { ThreadId = thread.Id, Direction = "in", Source = "customer", Text = "hi",
                    Status = "received", AccountId = accountId, CreatedAt = now.AddMinutes(-5) });
                db.Messages.Add(new Message { ThreadId = thread.Id, Direction = "out", Source = "agent", Text = "hello",
                    Status = "sent", AccountId = accountId, CreatedAt = now.AddMinutes(-4) });
                db.SaveChanges();
            }
            return (key, contact.Id);
        }
        var m = Conversation(mine, "60188800001", "qa-mine-label", null);
        var o = Conversation(other, "60188800002", "qa-other-secret", account.Id);

        if (!db.Workflows.Any(w => w.FlowId == "wf-qa-other"))
        {
            db.Workflows.Add(new Workflow { FlowId = "wf-qa-other", Name = "QA-OTHER journey", EntityId = other.Id,
                Nodes = new List<object?>(), Edges = new List<object?>(), CreatedAt = now, UpdatedAt = now });
            db.Teams.Add(new Team { Name = "QA-OTHER team", EntityId = other.Id, CreatedAt = now, UpdatedAt = now });
            db.AuditLogs.Add(new AuditLog { Actor = "system", Action = "account.health", Target = "QA-OTHER line",
                Detail = "unknown → disconnected — test", CreatedAt = now });
            db.SaveChanges();
        }
        return new World(mine.Id, kl.Id, other.Id, m.Key, o.Key, account.Id, o.Contact);
    });

    private void BindAgent(int entityId) => With(db =>
    {
        var profile = db.Profiles.Include(p => p.Entities).Include(p => p.User).First(p => p.User!.Username == "agent");
        if (!profile.Entities.Any(e => e.Id == entityId)) profile.Entities.Add(db.Entities.First(e => e.Id == entityId));
        return db.SaveChanges();
    });

    private void UnbindAgent() => With(db =>
    {
        var profile = db.Profiles.Include(p => p.Entities).Include(p => p.User).First(p => p.User!.Username == "agent");
        profile.Entities.Clear();
        return db.SaveChanges();
    });

    private async Task AsBoundAgent(int entityId, Func<HttpClient, Task> work)
    {
        BindAgent(entityId);
        try { await work(await Platform.As(_f, "agent", "agent123")); }
        finally { UnbindAgent(); }
    }

    private static List<string> Names(JsonElement j, string array) =>
        j.GetProperty(array).EnumerateArray()
            .Select(a => a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").ToList();

    // ---- 1. dashboard ---------------------------------------------------------------------

    [Fact]
    public async Task Dashboard_reports_and_flow_stats_are_bounded_to_an_agents_entities()
    {
        var w = Seed();
        var admin = await Platform.As(_f, "admin", "admin123");
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/analytics/summary?days=7");
        Assert.Contains("QA-OTHER line", Names(all, "accounts"));

        await AsBoundAgent(w.Mine, async agent =>
        {
            foreach (var url in new[] { "/api/analytics/summary?days=7", "/api/analytics/summary?days=7&entity=qa-other" })
            {
                var s = await agent.GetFromJsonAsync<JsonElement>(url);
                Assert.NotEqual("qa-other", s.GetProperty("entity").ValueKind == JsonValueKind.String
                    ? s.GetProperty("entity").GetString() : null);
                Assert.DoesNotContain("QA-OTHER line", Names(s, "accounts"));
                Assert.DoesNotContain(s.GetProperty("health_events").EnumerateArray(),
                    h => h.GetProperty("target").GetString() == "QA-OTHER line");
            }
            // the reports: another company's labels never show
            var rep = await agent.GetFromJsonAsync<JsonElement>("/api/analytics/reports?entity=qa-other");
            var labels = rep.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToList();
            Assert.DoesNotContain("qa-other-secret", labels);
            Assert.Contains("qa-mine-label", labels);
            // the journeys card: another company's flow never shows
            var flows = await agent.GetFromJsonAsync<JsonElement>("/api/analytics/flows?entity=qa-other");
            Assert.DoesNotContain(flows.GetProperty("flows").EnumerateArray(),
                f => f.GetProperty("flowId").GetString() == "wf-qa-other");
        });
        var adminRep = await admin.GetFromJsonAsync<JsonElement>("/api/analytics/reports");
        Assert.Contains(adminRep.GetProperty("labels").EnumerateArray(), l => l.GetProperty("name").GetString() == "qa-other-secret");
    }

    // ---- 2. channel lists ----------------------------------------------------------------

    [Fact]
    public async Task Channel_lists_show_a_branch_agent_only_the_numbers_their_outlets_may_use()
    {
        var w = Seed();
        var admin = await Platform.As(_f, "admin", "admin123");
        Assert.Contains("QA-OTHER line", Names(await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/accounts?channel=all"), "accounts"));
        Assert.Contains("QA-OTHER line", Names(await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/channels/health"), "accounts"));
        // ?entity= (the /channels switcher) narrows to what that outlet inherits
        Assert.DoesNotContain("QA-OTHER line",
            Names(await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/accounts?channel=all&entity=qa-mine"), "accounts"));

        await AsBoundAgent(w.MineKl, async agent =>
        {
            Assert.DoesNotContain("QA-OTHER line",
                Names(await agent.GetFromJsonAsync<JsonElement>("/api/whatsapp/accounts?channel=all"), "accounts"));
            Assert.DoesNotContain("QA-OTHER line",
                Names(await agent.GetFromJsonAsync<JsonElement>("/api/whatsapp/channels/health?entity=qa-other"), "accounts"));
        });
    }

    // ---- 3. AI controls ------------------------------------------------------------------

    [Fact]
    public async Task Ai_controls_of_another_entity_are_not_readable_by_a_bound_agent()
    {
        var w = Seed();
        await AsBoundAgent(w.Mine, async agent =>
        {
            Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/ai/controls?entity=qa-other")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/ai/controls?entity=qa-mine-kl")).StatusCode);
        });
    }

    // ---- 4. quota tiers + write capabilities ------------------------------------------------

    [Fact]
    public async Task Quota_writes_are_tiered_and_need_a_write_capability()
    {
        var w = Seed();
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var kl = w.MineKl.ToString();
        var put = (HttpClient c, object body) => c.PutAsJsonAsync("/api/billing/ai/quotas", body);
        try
        {
            // nobody but a superadmin sets their own cap
            Assert.Equal(HttpStatusCode.Forbidden, (await put(admin, new { users = new Dictionary<string, object> { ["admin"] = 50 } })).StatusCode);
            // an admin sets an unset branch cap and changes it again (admin-set)
            Assert.Equal(HttpStatusCode.OK, (await put(admin, new { entities = new Dictionary<string, object> { [kl] = 5 } })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await put(admin, new { entities = new Dictionary<string, object> { [kl] = 6 } })).StatusCode);
            // once a billing manager sets it, the admin can neither raise nor clear it
            Assert.Equal(HttpStatusCode.OK, (await put(su, new { entities = new Dictionary<string, object> { [kl] = 2 } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await put(admin, new { entities = new Dictionary<string, object> { [kl] = 100 } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await put(admin, new { entities = new Dictionary<string, object?> { [kl] = null } })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await put(su, new { users = new Dictionary<string, object> { ["agent"] = 3 } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await put(admin, new { users = new Dictionary<string, object> { ["agent"] = 0 } })).StatusCode);
            var cfg = With(db => AiQuota.LoadConfig(new ScopingService(db)));
            Assert.Equal(2, cfg.Entities[kl]);
            Assert.Equal(3, cfg.Users["agent"]);
            // the GET says which rows the admin may change
            var q = await admin.GetFromJsonAsync<JsonElement>("/api/billing/ai/quotas");
            var row = q.GetProperty("entities").EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == w.MineKl);
            Assert.False(row.GetProperty("editable").GetBoolean());
            Assert.True(row.GetProperty("set_by_manager").GetBoolean());
            Assert.False(q.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("username").GetString() == "admin")
                .GetProperty("editable").GetBoolean());
            // a cap below one cent is refused, not silently stored as 0
            Assert.Equal(HttpStatusCode.BadRequest, (await put(su, new { entities = new Dictionary<string, object> { [kl] = 0.001 } })).StatusCode);

            // an API key scoped to billing.view reads but never writes
            var key = Platform.Client(_f, Platform.MintKey(_f, "admin", "billing.view"));
            Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/billing/ai/quotas")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await put(key, new { users = new Dictionary<string, object> { ["agent"] = 9 } })).StatusCode);
        }
        finally
        {
            await put(su, new
            {
                entities = new Dictionary<string, object?> { [kl] = null },
                users = new Dictionary<string, object?> { ["agent"] = null },
            });
        }
    }

    [Fact]
    public async Task Voice_default_and_automation_reorder_and_test_need_real_capabilities()
    {
        // an admin's key without the capability: the role passes, the capability does not
        var key = Platform.Client(_f, Platform.MintKey(_f, "admin", "analytics.view"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await key.PutAsJsonAsync("/api/voice/default-voice", new { voice = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await key.PostAsJsonAsync("/api/automations/reorder", new { ids = new[] { 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await key.PostAsJsonAsync("/api/automations/1/test", new { channel = "whatsapp", key = "1" })).StatusCode);
        // with them it goes through (400 = validation, not a gate)
        var ok = Platform.Client(_f, Platform.MintKey(_f, "admin", "settings.ai.manage", "flows.manage"));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ok.PutAsJsonAsync("/api/voice/default-voice", new { voice = "no-such-voice" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await ok.PostAsJsonAsync("/api/automations/reorder", new { ids = Array.Empty<int>() })).StatusCode);
    }

    // ---- 5. quotas are enforced everywhere ------------------------------------------------------

    private void Spend(double usd, int? entityId = null, string user = "")
    {
        With(db =>
        {
            db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "gpt-4o", EntityId = entityId, UserName = user,
                PromptTokens = 10, CostUsd = usd, CreatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        AiQuota.Invalidate();
    }

    [Fact]
    public async Task Suggest_reply_review_and_calls_refuse_at_the_quota()
    {
        var w = Seed();
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var put = (object body) => su.PutAsJsonAsync("/api/billing/ai/quotas", body);

        // with room left, a suggestion is charged to the operator who asked for it
        var before = With(db => db.LlmUsage.Count(u => u.UserName == "admin" && u.Feature == "chat_reply"));
        var res = await admin.PostAsJsonAsync("/api/aicore/reply", new { channel = "whatsapp", key = w.MineKey });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var stamped = With(db => db.LlmUsage.Count(u => u.UserName == "admin" && u.Feature == "chat_reply"));
        Assert.True(stamped > before, "an operator suggestion is stamped with the operator");

        // the conversation's company at its cap → 429 for the suggestion
        (await put(new { entities = new Dictionary<string, object> { [w.Mine.ToString()] = 1 } })).EnsureSuccessStatusCode();
        Spend(5, w.MineKl);
        try
        {
            var blocked = await admin.PostAsJsonAsync("/api/aicore/reply", new { channel = "whatsapp", key = w.MineKey });
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.True((await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quota").GetBoolean());
            // the reviewer over that outlet
            Assert.Equal(HttpStatusCode.TooManyRequests,
                (await admin.PostAsJsonAsync("/api/ai/review", new { entity = "qa-mine", wait_seconds = 0 })).StatusCode);
        }
        finally
        {
            await put(new { entities = new Dictionary<string, object?> { [w.Mine.ToString()] = null } });
        }

        // the operator's own cap → 429 for suggestions, reviews and AI calls
        (await put(new { users = new Dictionary<string, object> { ["admin"] = 1 } })).EnsureSuccessStatusCode();
        Spend(5, user: "admin");
        try
        {
            Assert.Equal(HttpStatusCode.TooManyRequests,
                (await admin.PostAsJsonAsync("/api/aicore/reply", new { channel = "whatsapp", key = w.MineKey })).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests,
                (await admin.PostAsJsonAsync("/api/ai/review", new { wait_seconds = 0 })).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests,
                (await admin.PostAsJsonAsync("/api/calls/dial", new { phone = "+60123456789" })).StatusCode);
        }
        finally
        {
            await put(new { users = new Dictionary<string, object?> { ["admin"] = null } });
        }
    }

    // ---- 7. bad input is a 400, never a 500 --------------------------------------------------

    [Fact]
    public async Task Malformed_billing_and_review_input_answers_400()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var company = Seed().Mine.ToString();
        var nullTerms = await su.PutAsJsonAsync("/api/billing/ai/settings", new
        {
            currency = "USD", usd_rate = 1, default_markup_pct = 0, default_monthly_fee = 0,
            companies = new Dictionary<string, object?> { [company] = null },
        });
        Assert.Equal(HttpStatusCode.BadRequest, nullTerms.StatusCode);
        Assert.Contains("markup_pct", (await nullTerms.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PutAsJsonAsync("/api/billing/ai/settings", new
        {
            currency = "ÄBC", usd_rate = 1, default_markup_pct = 0, default_monthly_fee = 0,
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await su.GetAsync("/api/billing/ai?month=9999-12")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await su.PostAsJsonAsync("/api/ai/review", new { to = "9999-12-31" })).StatusCode);
    }

    // ---- 8. bindings ----------------------------------------------------------------------

    [Fact]
    public async Task Bindings_follow_the_user_tiers_and_the_contact_scope()
    {
        var w = Seed();
        var admin = await Platform.As(_f, "admin", "admin123");
        var ids = With(db => db.Users.ToDictionary(u => u.Username, u => u.Id));
        var team = With(db => db.Teams.First(t => t.Name == "QA-OTHER team").Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/bindings/user-entities",
            new { target = ids["superadmin"], add = new[] { w.Mine } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/bindings/user-entities",
            new { target = ids["admin"], add = new[] { w.Mine } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/bindings/user-teams",
            new { target = ids["superadmin"], add = new[] { team } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/bindings/team-users",
            new { target = team, add = new[] { ids["admin"] } })).StatusCode);
        // an agent-level user is fine
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/bindings/user-teams",
            new { target = ids["agent"], add = new[] { team } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/bindings/user-teams",
            new { target = ids["agent"], remove = new[] { team } })).StatusCode);

        await AsBoundAgent(w.Mine, async agent =>
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await agent.GetAsync($"/api/bindings/contact-entities?target={w.OtherContact}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await agent.GetAsync($"/api/bindings/entity-contacts?target={w.Other}")).StatusCode);
            var teams = await agent.GetFromJsonAsync<JsonElement>("/api/teams");
            Assert.DoesNotContain(teams.GetProperty("teams").EnumerateArray(), t => t.GetProperty("name").GetString() == "QA-OTHER team");
        });
    }

    // ---- 9. journeys library ------------------------------------------------------------------

    [Fact]
    public async Task Saving_the_journeys_library_from_All_never_duplicates_an_entity_flow()
    {
        Seed();
        var admin = await Platform.As(_f, "admin", "admin123");
        var lib = await admin.GetFromJsonAsync<JsonElement>("/api/flows/library");
        var flows = lib.GetProperty("workflows").EnumerateArray().ToList();
        Assert.Contains(flows, f => f.GetProperty("flowId").GetString() == "wf-qa-other");
        var payload = flows.Select((f, i) => new
        {
            flowId = f.GetProperty("flowId").GetString(),
            name = f.GetProperty("name").GetString() == "QA-OTHER journey" ? "QA-OTHER journey (edited)" : f.GetProperty("name").GetString(),
            hint = "",
            active = f.GetProperty("active").GetBoolean(),
            position = i,
            nodes = f.GetProperty("nodes"),
            edges = f.GetProperty("edges"),
        }).ToList();
        (await admin.PutAsJsonAsync("/api/flows/library", new { workflows = payload })).EnsureSuccessStatusCode();
        var rows = With(db => db.Workflows.AsNoTracking().Where(x => x.FlowId == "wf-qa-other").ToList());
        var row = Assert.Single(rows);
        Assert.Equal(With(db => db.Entities.Single(e => e.Slug == "qa-other").Id), row.EntityId);
        Assert.Equal("QA-OTHER journey (edited)", row.Name);   // updated in place

        // a bound agent reads only their subtree + ancestors + the global flows
        var w = Seed();
        await AsBoundAgent(w.Mine, async agent =>
        {
            var mine = await agent.GetFromJsonAsync<JsonElement>("/api/flows/library");
            Assert.DoesNotContain(mine.GetProperty("workflows").EnumerateArray(), f => f.GetProperty("flowId").GetString() == "wf-qa-other");
        });
    }

    // ---- 10. assignees ---------------------------------------------------------------------

    [Fact]
    public async Task An_assignee_must_be_an_active_user_who_can_see_the_conversation()
    {
        var w = Seed();
        var admin = await Platform.As(_f, "admin", "admin123");
        Task<HttpResponseMessage> Assign(string key, string who) =>
            admin.PostAsJsonAsync($"/api/messaging/threads/whatsapp/{key}/assign", new { assignee = who });

        Assert.Equal(HttpStatusCode.BadRequest, (await Assign(w.MineKey, "nobody-at-all")).StatusCode);
        BindAgent(w.Mine);
        try
        {
            // the agent works for qa-mine: another company's chat is not theirs to own
            Assert.Equal(HttpStatusCode.BadRequest, (await Assign(w.OtherKey, "agent")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Assign(w.MineKey, "agent")).StatusCode);
            Assert.Equal("agent", With(db => db.Threads.Single(t => t.Key == w.MineKey).Assignee));
            var picker = await admin.GetFromJsonAsync<JsonElement>($"/api/messaging/agents?channel=whatsapp&key={w.OtherKey}");
            var names = picker.GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("username").GetString()).ToList();
            Assert.DoesNotContain("agent", names);
            Assert.Contains("admin", names);
        }
        finally { UnbindAgent(); }
        Assert.Equal(HttpStatusCode.OK, (await Assign(w.MineKey, "")).StatusCode);   // "" unassigns
    }

    // ---- 11. routing ------------------------------------------------------------------------

    [Fact]
    public void An_unattributed_contact_is_never_auto_assigned_to_an_outlets_number()
    {
        var w = Seed();
        var (contactId, threadId) = With(db =>
        {
            var c = new Contact { Name = "QA nobody", Phone = "60188800099", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            var t = new Thread { Channel = "whatsapp", Key = "60188800099", Address = "60188800099", ContactId = c.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Threads.Add(t);
            db.SaveChanges();
            return (c.Id, t.Id);
        });
        With(db =>
        {
            var sc = new ScopingService(db);
            var thread = db.Threads.Single(t => t.Id == threadId);
            var r = new Routing(db, sc).ForThread(thread, RouteNeed.Conversation, apply: true);
            Assert.True(r.Account == null || r.Account.EntityId == null,
                $"routed onto {r.Account?.Name} (entity {r.Account?.EntityId})");
            Assert.NotEqual(w.OtherAccount, db.Threads.AsNoTracking().Single(t => t.Id == threadId).AccountId);
            Assert.False(db.AccountAssignments.Any(a => a.ContactId == contactId && a.AccountId == w.OtherAccount));
            Assert.Null(db.Threads.AsNoTracking().Single(t => t.Id == threadId).EntityId);
            return 0;
        });
    }
}
