using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// A person bound to assigned entities (a company admin, an agent or a custom
/// role with entities — ShellScope.ScopeRoots non-empty) reads and acts only
/// inside their subtree: every organization-wide AI write (behaviour, controls,
/// persona, safety rules, re-index, the worker tick, deleting an org lesson) is
/// refused even WITH the capabilities, a sibling entity's levels read as 404,
/// and their feedback / monitor decisions on an unassigned conversation never
/// create, merge into or demote an organization-wide lesson.
/// </summary>
public class EntityBoundAiScopeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public EntityBoundAiScopeTests(ApiFactory f) => _f = f;

    private static int _n;

    private T WithDb<T>(Func<AppDbContext, ScopingService, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<ScopingService>());
    }

    private sealed record World(int A, string ASlug, int B, string BSlug, string Sfx);

    /// <summary>Two top-level entities A and B.</summary>
    private World Seed()
    {
        var sfx = $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];
        return WithDb((db, _) =>
        {
            var tenant = db.Tenants.First();
            var now = DateTime.UtcNow;
            Entity Ent(string slug)
            {
                var e = new Entity { TenantId = tenant.Id, Name = $"Co {slug}", Slug = slug, CreatedAt = now, UpdatedAt = now };
                db.Entities.Add(e);
                db.SaveChanges();
                return e;
            }
            var a = Ent($"eb-a-{sfx}");
            var b = Ent($"eb-b-{sfx}");
            return new World(a.Id, a.Slug, b.Id, b.Slug, sfx);
        });
    }

    private async Task<HttpClient> CompanyAdmin(World w)
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var username = $"ebadmin{w.Sfx}";
        var res = await su.PostAsJsonAsync("/api/v1/users",
            new { username, password = "password123", role = "admin", entity_ids = new[] { w.A } });
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await Platform.As(_f, username, "password123");
    }

    /// <summary>A custom role holding every AI capability, bound to entity A.</summary>
    private async Task<(HttpClient Client, string Username)> CustomRole(World w)
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var role = await su.PostAsJsonAsync("/api/auth/roles", new { label = $"AI lead {w.Sfx}" });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        var slug = (await role.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        WithDb((db, _) =>
        {
            foreach (var cap in new[] { "ai.monitor", "ai.behaviour.manage", "settings.ai.manage",
                         "settings.ai_hard_rules.manage", "chats.view" })
            {
                // a new role starts with every capability row present and denied
                var row = db.RolePermissions.FirstOrDefault(r => r.Role == slug && r.Capability == cap);
                if (row == null) db.RolePermissions.Add(new RolePermission { Role = slug, Capability = cap, Allowed = true, UpdatedAt = DateTime.UtcNow });
                else row.Allowed = true;
            }
            return db.SaveChanges();
        });
        var username = $"eblead{w.Sfx}";
        var res = await su.PostAsJsonAsync("/api/v1/users",
            new { username, password = "password123", role = slug, entity_ids = new[] { w.A } });
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await Platform.As(_f, username, "password123"), username);
    }

    private int OrgLesson(string question, string answer, string status = AiLessonStatus.Active) => WithDb((db, _) =>
    {
        var l = new AiLesson { EntityId = null, Kind = AiLessonKind.Example, Status = status, Question = question,
            Answer = answer, SourceCount = 1, CreatedBy = "superadmin", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.AiLessons.Add(l);
        db.SaveChanges();
        return l.Id;
    });

    /// <summary>An UNASSIGNED conversation (no entity) the AI answered: (AI message, draft).</summary>
    private (int Ai, int Draft) UnassignedConversation(string key, string question, string answer) => WithDb((db, _) =>
    {
        var now = DateTime.UtcNow;
        var t = new Thread { Channel = "whatsapp", Key = key, Address = key, Name = key, CreatedAt = now, UpdatedAt = now };
        db.Threads.Add(t);
        db.SaveChanges();
        var q = new Message { ThreadId = t.Id, Direction = "in", Source = "customer", Text = question, Status = "received",
            CreatedAt = now.AddSeconds(-30) };
        var a = new Message { ThreadId = t.Id, Direction = "out", Source = "ai", Text = answer, Status = "sent", CreatedAt = now };
        db.Messages.AddRange(q, a);
        db.SaveChanges();
        var d = new AiDraft { ThreadId = t.Id, ThreadKey = key, Channel = "whatsapp", Kind = AiDraftKind.Reply,
            Content = answer, Status = AiDraftStatus.AutoSent, SentMessageId = a.Id, SentAt = now,
            CreatedAt = now, UpdatedAt = now };
        db.AiDrafts.Add(d);
        db.SaveChanges();
        return (a.Id, d.Id);
    });

    private void SetOrg(string key, string value) => WithDb((db, sc) => { sc.SetSetting(key, value, null); return 0; });

    [Fact]
    public async Task Whoami_says_whether_the_caller_is_bound()
    {
        var w = Seed();
        var co = await CompanyAdmin(w);
        var (lead, _) = await CustomRole(w);
        var admin = await Platform.As(_f, "admin", "admin123");
        Assert.True((await co.GetFromJsonAsync<JsonElement>("/api/v1/whoami")).GetProperty("bound").GetBoolean());
        Assert.True((await lead.GetFromJsonAsync<JsonElement>("/api/v1/whoami")).GetProperty("bound").GetBoolean());
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/v1/whoami")).GetProperty("bound").GetBoolean());
    }

    [Fact]
    public async Task Bound_callers_are_refused_every_organization_wide_ai_write()
    {
        var w = Seed();
        var co = await CompanyAdmin(w);
        var (lead, _) = await CustomRole(w);
        var orgLesson = OrgLesson($"org question {w.Sfx}?", "org answer");
        foreach (var c in new[] { co, lead })
        {
            Assert.Equal(HttpStatusCode.Forbidden,
                (await c.PutAsJsonAsync("/api/ai/behaviour", new { values = new { ai_review_mode = "review_all" } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await c.PutAsJsonAsync("/api/ai/controls", new { mode = "off" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await c.PutAsJsonAsync("/api/aicore/persona", new { persona = "Org persona" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await c.PutAsJsonAsync("/api/aicore/persona", new { hard_rules = "Be nice." })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/ai/learning/reindex", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/ai/monitor/tick", null)).StatusCode);

            // their own entity's level still works
            var ownPut = await c.PutAsJsonAsync($"/api/ai/behaviour?entity={w.ASlug}", new { values = new { ai_review_mode = "review_all" } });
            Assert.True(ownPut.StatusCode == HttpStatusCode.OK, await ownPut.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK,
                (await c.PutAsJsonAsync($"/api/aicore/persona", new { persona = "Outlet persona", entity = w.ASlug })).StatusCode);
        }
        // the company admin (admin+) may not delete an org-wide lesson
        Assert.Equal(HttpStatusCode.Forbidden, (await co.DeleteAsync($"/api/ai/lessons/{orgLesson}")).StatusCode);
        Assert.True(WithDb((db, _) => db.AiLessons.Any(l => l.Id == orgLesson)));
        // the org-wide admin may
        var admin = await Platform.As(_f, "admin", "admin123");
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/ai/lessons/{orgLesson}")).StatusCode);

        // the safety rules stay where they were
        Assert.True(WithDb((db, _) => !db.Settings.Any(s => s.Key == "ai_hard_rules" && s.EntityId == null && s.Value == "Be nice.")));
    }

    [Fact]
    public async Task Bound_callers_read_their_levels_and_never_a_siblings()
    {
        var w = Seed();
        var co = await CompanyAdmin(w);
        var (lead, _) = await CustomRole(w);
        foreach (var c in new[] { co, lead })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/ai/behaviour?entity={w.BSlug}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/aicore/persona?entity={w.BSlug}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/ai/learning/status?entity={w.BSlug}")).StatusCode);

            // the organization level stays readable — but not editable
            var org = await c.GetFromJsonAsync<JsonElement>("/api/ai/behaviour");
            Assert.False(org.GetProperty("can_manage").GetBoolean());
            var persona = await c.GetFromJsonAsync<JsonElement>("/api/aicore/persona");
            Assert.False(persona.GetProperty("can_manage").GetBoolean());
            Assert.False(persona.GetProperty("can_edit_hard_rules").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/ai/learning/status")).StatusCode);

            var own = await c.GetFromJsonAsync<JsonElement>($"/api/ai/behaviour?entity={w.ASlug}");
            Assert.True(own.GetProperty("can_manage").GetBoolean());
        }
        var admin = await Platform.As(_f, "admin", "admin123");
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/ai/behaviour")).GetProperty("can_manage").GetBoolean());
    }

    [Fact]
    public async Task Bound_feedback_on_an_unassigned_conversation_never_touches_org_lessons()
    {
        var w = Seed();
        var co = await CompanyAdmin(w);
        SetOrg("ai_learning_mode", "auto");
        try
        {
            // a 👍 on an unassigned conversation: the rating is kept, no org-wide lesson
            var q1 = $"Do you deliver to Penang {w.Sfx}?";
            var c1 = UnassignedConversation($"6019{w.Sfx.GetHashCode() & 0xFFFFF:0000000}", q1, "Yes, we do.");
            var up = await co.PostAsJsonAsync("/api/ai/feedback", new { message_id = c1.Ai, rating = 1 });
            Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
            var fb = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("feedback");
            Assert.Equal(JsonValueKind.Null, fb.GetProperty("lesson_id").ValueKind);
            Assert.False(WithDb((db, _) => db.AiLessons.Any(l => l.EntityId == null && l.Question.Contains(w.Sfx) && l.Question.Contains("Penang"))));

            // a 👎 with a correction must not demote the live org lesson for the same question
            var q2 = $"What time do you open {w.Sfx}?";
            var live = OrgLesson(q2, "We open at 9am.");
            var c2 = UnassignedConversation($"6018{w.Sfx.GetHashCode() & 0xFFFFF:0000000}", q2, "We open at 10am.");
            var down = await co.PostAsJsonAsync("/api/ai/feedback",
                new { message_id = c2.Ai, rating = -1, reasons = new[] { "wrong_info" }, correction = "We open at 8am." });
            Assert.True(down.IsSuccessStatusCode, await down.Content.ReadAsStringAsync());
            var lesson = WithDb((db, _) => db.AiLessons.AsNoTracking().First(l => l.Id == live));
            Assert.Equal(AiLessonStatus.Active, lesson.Status);
            Assert.Equal("We open at 9am.", lesson.Answer);

            // an edited monitor decision by the bound admin: same rule
            var c3 = UnassignedConversation($"6017{w.Sfx.GetHashCode() & 0xFFFFF:0000000}", q2, "We open at 11am.");
            WithDb((db, sc) =>
            {
                var draft = db.AiDrafts.First(d => d.Id == c3.Draft);
                draft.OriginalContent = draft.Content;
                draft.Content = "We open at 7am.";
                var fbRow = new LearningService(db, sc).RecordDecision(draft, AiFeedbackKind.Edited, $"ebadmin{w.Sfx}", null);
                Assert.NotNull(fbRow);                 // the feedback row is still recorded
                Assert.Null(fbRow!.LessonId);
                return 0;
            });
            lesson = WithDb((db, _) => db.AiLessons.AsNoTracking().First(l => l.Id == live));
            Assert.Equal(AiLessonStatus.Active, lesson.Status);
            Assert.Equal("We open at 9am.", lesson.Answer);

            // the org-wide admin's correction in auto mode still updates it live
            WithDb((db, sc) =>
            {
                var draft = db.AiDrafts.First(d => d.Id == c3.Draft);
                draft.OriginalContent = "We open at 11am.";
                draft.Content = "We open at 8:30am.";
                new LearningService(db, sc).RecordDecision(draft, AiFeedbackKind.Edited, "admin", null);
                return 0;
            });
            lesson = WithDb((db, _) => db.AiLessons.AsNoTracking().First(l => l.Id == live));
            Assert.Equal(AiLessonStatus.Active, lesson.Status);
            Assert.Equal("We open at 8:30am.", lesson.Answer);
        }
        finally { SetOrg("ai_learning_mode", ""); }
    }

    [Fact]
    public void A_company_admin_only_auto_approves_inside_their_companies()
    {
        var w = Seed();
        Task.Run(() => CompanyAdmin(w)).GetAwaiter().GetResult();
        WithDb((db, sc) =>
        {
            var actor = new LearningService(db, sc).ActorFor($"ebadmin{w.Sfx}");
            Assert.False(actor.OrgWide);
            Assert.True(actor.AutoApproves(w.A));
            Assert.False(actor.AutoApproves(w.B));
            Assert.False(actor.AutoApproves(null));
            var orgAdmin = new LearningService(db, sc).ActorFor("admin");
            Assert.True(orgAdmin.OrgWide);
            Assert.True(orgAdmin.AutoApproves(null));
            return 0;
        });
    }
}
