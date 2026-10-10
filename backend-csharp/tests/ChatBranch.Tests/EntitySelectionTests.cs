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
/// The multi-select entity filter: <c>?entity=a,b</c> shows every ticked entity
/// (each with its branches) and nothing else, and a ticked entity outside the
/// caller's scope never widens what they see.
/// </summary>
public class EntitySelectionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public EntitySelectionTests(ApiFactory f) => _f = f;

    private T WithDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private record Seeded(int A, int AKl, int B, int C, Dictionary<string, int> Drafts, Dictionary<string, int> Contacts);

    private Seeded Seed() => WithDb(db =>
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
        var a = Get("sel-a", null);
        var akl = Get("sel-a-kl", a.Id);
        var b = Get("sel-b", null);
        var c = Get("sel-c", null);
        var drafts = new Dictionary<string, int>();
        var contacts = new Dictionary<string, int>();
        var n = 0;
        foreach (var (name, ent) in new[] { ("a", a), ("akl", akl), ("b", b), ("c", c) })
        {
            var key = $"6014400{++n:0000}";
            var contact = db.Contacts.FirstOrDefault(x => x.Phone == key);
            if (contact == null)
            {
                contact = new Contact { Name = $"Sel {name}", Phone = key, TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
                db.Contacts.Add(contact);
                db.SaveChanges();
                db.ContactEntities.Add(new ContactEntity { ContactId = contact.Id, EntityId = ent.Id, CreatedAt = now });
                var thread = new Thread { Channel = "whatsapp", Key = key, Address = key, Name = contact.Name,
                    ContactId = contact.Id, EntityId = ent.Id, CreatedAt = now, UpdatedAt = now };
                db.Threads.Add(thread);
                db.SaveChanges();
                db.AiDrafts.Add(new AiDraft { ThreadId = thread.Id, ThreadKey = key, Channel = "whatsapp",
                    ContactId = contact.Id, EntityId = ent.Id, Kind = AiDraftKind.Reply, Content = "x",
                    Status = AiDraftStatus.Pending, CreatedAt = now, UpdatedAt = now });
                db.SaveChanges();
            }
            contacts[name] = contact.Id;
            drafts[name] = db.AiDrafts.First(d => d.ContactId == contact.Id).Id;
        }
        return new Seeded(a.Id, akl.Id, b.Id, c.Id, drafts, contacts);
    });

    private static List<int> DraftIds(JsonElement j) =>
        j.GetProperty("drafts").EnumerateArray().Select(d => d.GetProperty("id").GetInt32()).ToList();

    private static List<int> ContactIds(JsonElement j) =>
        j.GetProperty("contacts").EnumerateArray().Select(d => d.GetProperty("id").GetInt32()).ToList();

    [Fact]
    public async Task Ticking_several_entities_shows_each_with_its_branches_and_nothing_else()
    {
        var s = Seed();
        var admin = await Platform.As(_f, "admin", "admin123");

        var two = DraftIds(await admin.GetFromJsonAsync<JsonElement>("/api/ai/monitor?entity=sel-a,sel-b&page_size=200"));
        Assert.Contains(s.Drafts["a"], two);
        Assert.Contains(s.Drafts["akl"], two);   // a ticked entity brings its branches
        Assert.Contains(s.Drafts["b"], two);
        Assert.DoesNotContain(s.Drafts["c"], two);

        var one = DraftIds(await admin.GetFromJsonAsync<JsonElement>("/api/ai/monitor?entity=sel-a&page_size=200"));
        Assert.Contains(s.Drafts["akl"], one);
        Assert.DoesNotContain(s.Drafts["b"], one);

        var people = ContactIds(await admin.GetFromJsonAsync<JsonElement>("/api/contacts?entity=sel-b,sel-c&limit=500"));
        Assert.Contains(s.Contacts["b"], people);
        Assert.Contains(s.Contacts["c"], people);
        Assert.DoesNotContain(s.Contacts["a"], people);

        // the repeated form works too
        var repeated = DraftIds(await admin.GetFromJsonAsync<JsonElement>("/api/ai/monitor?entity=sel-b&entity=sel-c&page_size=200"));
        Assert.Contains(s.Drafts["c"], repeated);
        Assert.DoesNotContain(s.Drafts["a"], repeated);
    }

    [Fact]
    public async Task A_ticked_entity_outside_an_agents_scope_never_widens_it()
    {
        var s = Seed();
        WithDb(db =>
        {
            var profile = db.Profiles.Include(p => p.Entities).Include(p => p.User).First(p => p.User!.Username == "agent");
            if (!profile.Entities.Any(e => e.Id == s.A)) profile.Entities.Add(db.Entities.First(e => e.Id == s.A));
            return db.SaveChanges();
        });
        try
        {
            var agent = await Platform.As(_f, "agent", "agent123");
            var mine = DraftIds(await agent.GetFromJsonAsync<JsonElement>("/api/ai/monitor?entity=sel-a,sel-c&page_size=200"));
            Assert.Contains(s.Drafts["a"], mine);
            Assert.DoesNotContain(s.Drafts["c"], mine);
            var sneaky = DraftIds(await agent.GetFromJsonAsync<JsonElement>("/api/ai/monitor?entity=sel-c,sel-b&page_size=200"));
            Assert.DoesNotContain(s.Drafts["c"], sneaky);
            Assert.DoesNotContain(s.Drafts["b"], sneaky);
        }
        finally
        {
            WithDb(db =>
            {
                var profile = db.Profiles.Include(p => p.Entities).Include(p => p.User).First(p => p.User!.Username == "agent");
                foreach (var e in profile.Entities.Where(e => e.Id == s.A).ToList()) profile.Entities.Remove(e);
                return db.SaveChanges();
            });
        }
    }
}
