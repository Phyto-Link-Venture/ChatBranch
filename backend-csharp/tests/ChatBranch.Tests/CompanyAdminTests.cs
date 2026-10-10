using System.Net;
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
/// Company admins: an admin with assigned entities sees and manages only those
/// companies (and their branches) — ShellScope.ScopeRoots / IsCompanyAdmin /
/// IsOrgWideAdmin. An admin with none stays organization-wide; superadmin is
/// never bound.
/// </summary>
public class CompanyAdminTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public CompanyAdminTests(ApiFactory f) => _f = f;

    private T WithDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private sealed record World(int A, int AKl, int B, int ContactA, int ContactB, string KeyA, string KeyB,
        int DraftA, int DraftB, int AccountB, string Suffix);

    private static int _n;

    /// <summary>Two companies (A with a branch, B), each with a contact, a
    /// conversation, an AI draft, knowledge; B also has a number and an agent.</summary>
    private World Seed()
    {
        var sfx = $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];
        return WithDb(db =>
        {
            var tenant = db.Tenants.First();
            var now = DateTime.UtcNow;
            Entity Ent(string slug, int? parent)
            {
                var e = new Entity { TenantId = tenant.Id, Name = $"Co {slug}", Slug = slug, ParentId = parent,
                    CreatedAt = now, UpdatedAt = now };
                db.Entities.Add(e);
                db.SaveChanges();
                return e;
            }
            var a = Ent($"ca-a-{sfx}", null);
            var akl = Ent($"ca-a-kl-{sfx}", a.Id);
            var b = Ent($"ca-b-{sfx}", null);
            (int Contact, string Key, int Draft) Person(string who, Entity ent, int n)
            {
                var key = $"6017{sfx.GetHashCode() & 0xFFFF:00000}{n:00}".PadRight(11, '1')[..11];
                var c = new Contact { Name = $"Person {who} {sfx}", Phone = "+" + key, TenantId = tenant.Id,
                    CreatedAt = now, UpdatedAt = now };
                db.Contacts.Add(c);
                db.SaveChanges();
                db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = ent.Id, CreatedAt = now });
                var t = new Thread { Channel = "whatsapp", Key = key, Address = key, Name = c.Name, ContactId = c.Id,
                    EntityId = ent.Id, CreatedAt = now, UpdatedAt = now };
                db.Threads.Add(t);
                db.SaveChanges();
                db.Messages.Add(new Message { ThreadId = t.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                    Text = "hello", CreatedAt = now });
                var d = new AiDraft { ThreadId = t.Id, ThreadKey = key, Channel = "whatsapp", ContactId = c.Id,
                    EntityId = ent.Id, Kind = AiDraftKind.Reply, Content = "x", Status = AiDraftStatus.Pending,
                    CreatedAt = now, UpdatedAt = now };
                db.AiDrafts.Add(d);
                db.SaveChanges();
                return (c.Id, key, d.Id);
            }
            var pa = Person("A", a, 1);
            var pb = Person("B", b, 2);
            db.KnowledgeItems.Add(new KnowledgeItem { Title = $"KB secret {sfx}", Content = "B only", Kind = "fact",
                Active = true, EntityId = b.Id, CreatedAt = now, UpdatedAt = now });
            var acct = new ChannelAccount { Channel = "whatsapp", Provider = "baileys", Name = $"B number {sfx}",
                EntityId = b.Id, CreatedAt = now };
            db.ChannelAccounts.Add(acct);
            db.SaveChanges();
            return new World(a.Id, akl.Id, b.Id, pa.Contact, pb.Contact, pa.Key, pb.Key, pa.Draft, pb.Draft, acct.Id, sfx);
        });
    }

    private async Task<(HttpClient Client, string Username)> CompanyAdmin(World w)
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var username = $"coadmin{w.Suffix}";
        var res = await su.PostAsJsonAsync("/api/v1/users",
            new { username, password = "password123", role = "admin", entity_ids = new[] { w.A } });
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await Platform.As(_f, username, "password123"), username);
    }

    private static async Task<string> Text(HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{url}: {(int)res.StatusCode} {body}");
        return body;
    }

    [Fact]
    public async Task A_company_admin_never_sees_another_companys_data()
    {
        var w = Seed();
        var (co, _) = await CompanyAdmin(w);

        var whoami = await co.GetFromJsonAsync<JsonElement>("/api/v1/whoami");
        Assert.True(whoami.GetProperty("company_admin").GetBoolean());
        var slugs = whoami.GetProperty("entities").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains($"ca-a-{w.Suffix}", slugs);
        Assert.Contains($"ca-a-kl-{w.Suffix}", slugs);          // their branch too
        Assert.DoesNotContain($"ca-b-{w.Suffix}", slugs);
        // the org-level capabilities are not theirs
        var caps = whoami.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("notifications.manage", caps);
        Assert.Contains("users.manage", caps);

        var contacts = await Text(co, "/api/contacts?limit=2000");
        Assert.Contains($"Person A {w.Suffix}", contacts);
        Assert.DoesNotContain($"Person B {w.Suffix}", contacts);
        Assert.Equal(HttpStatusCode.NotFound, (await co.GetAsync($"/api/contacts/{w.ContactB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await co.GetAsync($"/api/v1/contacts/{w.ContactB}")).StatusCode);

        var threads = await Text(co, "/api/inbox/threads?limit=200");
        Assert.Contains(w.KeyA, threads);
        Assert.DoesNotContain(w.KeyB, threads);
        Assert.Equal(HttpStatusCode.NotFound, (await co.GetAsync($"/api/whatsapp/threads/{w.KeyB}")).StatusCode);

        var drafts = await co.GetFromJsonAsync<JsonElement>("/api/ai/monitor?page_size=500");
        var draftIds = drafts.GetProperty("drafts").EnumerateArray().Select(d => d.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(w.DraftA, draftIds);
        Assert.DoesNotContain(w.DraftB, draftIds);

        Assert.DoesNotContain($"KB secret {w.Suffix}", await Text(co, "/api/aicore/knowledge"));
        Assert.DoesNotContain($"B number {w.Suffix}", await Text(co, "/api/whatsapp/accounts"));
        Assert.DoesNotContain($"ca-b-{w.Suffix}", await Text(co, "/api/v1/entities"));

        // the users list: themselves, never org-wide admins or the superadmin
        var users = await Text(co, "/api/v1/users");
        Assert.Contains($"coadmin{w.Suffix}", users);
        Assert.DoesNotContain("\"superadmin\"", users.Replace("\"role\":\"superadmin\"", ""));
        Assert.DoesNotContain("\"username\":\"admin\"", users);
    }

    [Fact]
    public async Task A_company_admin_manages_agents_in_their_company_only()
    {
        var w = Seed();
        var (co, _) = await CompanyAdmin(w);

        // an agent bound to their branch: yes
        var ok = await co.PostAsJsonAsync("/api/v1/users",
            new { username = $"coagent{w.Suffix}", password = "password123", role = "agent", entity_ids = new[] { w.AKl } });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Contains($"coagent{w.Suffix}", await Text(co, "/api/v1/users"));

        // an agent of another company: no
        var other = await co.PostAsJsonAsync("/api/v1/users",
            new { username = $"coagentb{w.Suffix}", password = "password123", role = "agent", entity_ids = new[] { w.B } });
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        // an unbound (tenant-wide) agent: no
        var unbound = await co.PostAsJsonAsync("/api/v1/users",
            new { username = $"coagentx{w.Suffix}", password = "password123", role = "agent" });
        Assert.Equal(HttpStatusCode.BadRequest, unbound.StatusCode);
        // an admin of any kind: no
        var admin = await co.PostAsJsonAsync("/api/v1/users",
            new { username = $"coadmin2{w.Suffix}", password = "password123", role = "admin", entity_ids = new[] { w.AKl } });
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        // the seeded org-wide admin is not theirs to edit
        var adminId = WithDb(db => db.Users.First(u => u.Username == "admin").Id);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await co.PostAsJsonAsync($"/api/v1/users/{adminId}", new { active = false })).StatusCode);
    }

    [Fact]
    public async Task A_company_admin_adds_branches_and_scoped_library_items_only()
    {
        var w = Seed();
        var (co, _) = await CompanyAdmin(w);

        // a new top-level company: no; a branch under theirs: yes; under B: no
        Assert.Equal(HttpStatusCode.Forbidden,
            (await co.PostAsJsonAsync("/api/v1/entities", new { name = "New Co", slug = $"newco-{w.Suffix}" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await co.PostAsJsonAsync("/api/v1/entities",
                new { name = "A Penang", slug = $"ca-a-pg-{w.Suffix}", parent_id = w.A })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await co.PostAsJsonAsync("/api/v1/entities",
                new { name = "B Penang", slug = $"ca-b-pg-{w.Suffix}", parent_id = w.B })).StatusCode);

        // a global (Everyone) library item: no; one for their company: yes
        Assert.Equal(HttpStatusCode.Forbidden,
            (await co.PostAsJsonAsync("/api/aicore/knowledge", new { title = "Global FAQ", content = "x", kind = "faq" })).StatusCode);
        var mine = await co.PostAsJsonAsync("/api/aicore/knowledge",
            new { title = "A FAQ", content = "x", kind = "faq", entities = new[] { $"ca-a-{w.Suffix}" } });
        Assert.True(mine.IsSuccessStatusCode, await mine.Content.ReadAsStringAsync());
        // an org-wide item stays read-only to them
        var globalId = WithDb(db =>
        {
            var k = new KnowledgeItem { Title = $"Org FAQ {w.Suffix}", Content = "y", Kind = "faq", Active = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.KnowledgeItems.Add(k);
            db.SaveChanges();
            return k.Id;
        });
        Assert.Equal(HttpStatusCode.Forbidden,
            (await co.PatchAsJsonAsync($"/api/aicore/knowledge/{globalId}", new { title = "hijacked" })).StatusCode);
        // another company's number: not theirs to change
        Assert.Equal(HttpStatusCode.Forbidden,
            (await co.PostAsJsonAsync($"/api/whatsapp/accounts/{w.AccountB}", new { name = "mine now" })).StatusCode);
        // organization-level settings: no
        Assert.Equal(HttpStatusCode.Forbidden,
            (await co.PutAsJsonAsync("/api/auth/notification-prefs", new { })).StatusCode);
    }

    [Fact]
    public async Task An_org_wide_admin_still_sees_everything_and_may_make_company_admins()
    {
        var w = Seed();
        var admin = await Platform.As(_f, "admin", "admin123");
        var whoami = await admin.GetFromJsonAsync<JsonElement>("/api/v1/whoami");
        Assert.False(whoami.GetProperty("company_admin").GetBoolean());

        var contacts = await Text(admin, "/api/contacts?limit=5000");
        Assert.Contains($"Person A {w.Suffix}", contacts);
        Assert.Contains($"Person B {w.Suffix}", contacts);
        Assert.Contains($"B number {w.Suffix}", await Text(admin, "/api/whatsapp/accounts"));

        // a company admin: yes; an organization-wide admin: superadmin only
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users",
            new { username = $"coadminb{w.Suffix}", password = "password123", role = "admin", entity_ids = new[] { w.B } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/v1/users",
            new { username = $"orgadmin{w.Suffix}", password = "password123", role = "admin" })).StatusCode);
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users");
        var row = list.GetProperty("users").EnumerateArray().First(u => u.GetProperty("username").GetString() == $"coadminb{w.Suffix}");
        Assert.True(row.GetProperty("company_admin").GetBoolean());

        // superadmin unchanged: everything, and may make an org-wide admin
        var su = await Platform.As(_f, "superadmin", "super123");
        Assert.Contains($"Person B {w.Suffix}", await Text(su, "/api/contacts?limit=5000"));
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/users",
            new { username = $"orgadmin2{w.Suffix}", password = "password123", role = "admin" })).StatusCode);
    }
}
