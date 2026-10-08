using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The tag registry (replacing Service Providers): CRUD + entity scoping, group
/// exclusivity, the Contact.Tags write-through cache, attribute values and
/// their template variables, the new binding kinds — and proof the provider
/// surface is gone.
/// </summary>
public class TagsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public TagsTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) =>
        await res.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<int> NewContact(HttpClient c, string name, string phone, string[]? tags = null)
    {
        var res = await c.PostAsJsonAsync("/api/contacts", new { name, phone, tags = tags ?? Array.Empty<string>() });
        res.EnsureSuccessStatusCode();
        return (await Json(res)).GetProperty("id").GetInt32();
    }

    private static async Task<int> NewTag(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/tags", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Json(res)).GetProperty("id").GetInt32();
    }

    private static List<string> Names(JsonElement contact) =>
        contact.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToList();

    [Fact]
    public async Task Conversation_labels_are_seeded_as_registry_tags()
    {
        var agent = await Authed("agent", "agent123");
        var list = await agent.GetFromJsonAsync<JsonElement>("/api/tags?applies_to=conversation");
        var slugs = list.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("slug").GetString()).ToList();
        foreach (var slug in new[] { "follow-up", "resolved", "urgent", "escalate", "wrong-number" })
            Assert.Contains(slug, slugs);
        Assert.Contains("tangerine", list.GetProperty("palette").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Tag_crud_capabilities_and_entity_scoping()
    {
        var admin = await Authed("admin", "admin123");
        var agent = await Authed("agent", "agent123");

        // agents view (tags.view) but cannot manage (tags.manage)
        var denied = await agent.PostAsJsonAsync("/api/tags", new { name = "Nope", applies_to = "contact" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        (await agent.GetAsync("/api/tags")).EnsureSuccessStatusCode();

        // a branch under main, and a tag owned by each scope
        var branch = await admin.PostAsJsonAsync("/api/v1/entities",
            new { name = "Scope Branch", slug = "scope-branch", parent = "main" });
        Assert.Equal(HttpStatusCode.Created, branch.StatusCode);

        var global = await NewTag(admin, new { name = "Org VIP", applies_to = "contact", color = "plum" });
        var mainOnly = await NewTag(admin, new { name = "Main only", applies_to = "contact", entity = "main" });
        var branchOnly = await NewTag(admin, new { name = "Branch only", applies_to = "contact", entity = "scope-branch" });

        var forBranch = await admin.GetFromJsonAsync<JsonElement>("/api/tags?applies_to=contact&entity=scope-branch");
        var ids = forBranch.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(global, ids);       // inherited
        Assert.Contains(branchOnly, ids);   // its own
        Assert.DoesNotContain(mainOnly, ids); // a sibling scope's library is not inherited

        // bad colour / duplicate name / bad attribute
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/tags", new { name = "Hex", color = "#ff0000" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/tags", new { name = "org vip", applies_to = "contact" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/tags", new
        {
            name = "Broken", attributes = new[] { new { label = "Kind", type = "select" } },
        })).StatusCode);

        // update
        var patch = await admin.PatchAsJsonAsync($"/api/tags/{global}",
            new { name = "Org VIP+", description = "Top customers", group = "Tier" });
        patch.EnsureSuccessStatusCode();
        var patched = await Json(patch);
        Assert.Equal("Org VIP+", patched.GetProperty("name").GetString());
        Assert.Equal("Tier", patched.GetProperty("group").GetString());
        Assert.Equal("org-vip", patched.GetProperty("slug").GetString()); // slug is immutable

        // delete (unused → no force needed)
        (await admin.DeleteAsync($"/api/tags/{mainOnly}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/tags/{mainOnly}")).StatusCode);

        // every write is audited
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "tag.create"));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "tag.update"));
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "tag.delete"));
    }

    [Fact]
    public async Task Group_exclusivity_replaces_the_other_tag_of_the_group()
    {
        var admin = await Authed("admin", "admin123");
        var boss = await NewTag(admin, new { name = "Gx Boss", applies_to = "contact", group = "Gx Role" });
        var staff = await NewTag(admin, new { name = "Gx Staff", applies_to = "contact", group = "Gx Role" });
        var id = await NewContact(admin, "Grace Group", "60120000201");

        // both of one group in one PUT is refused rather than silently dropping one
        var both = await admin.PutAsJsonAsync($"/api/contacts/{id}/tags",
            new { tags = new[] { new { tag_id = boss }, new { tag_id = staff } } });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        // applying the second through the binding view displaces the first
        (await admin.PostAsJsonAsync($"/api/tags/{boss}/targets", new { add = new[] { id } })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/tags/{staff}/targets", new { add = new[] { id } })).EnsureSuccessStatusCode();

        var tags = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}/tags");
        var held = tags.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("tag_id").GetInt32()).ToList();
        Assert.Equal(new[] { staff }, held);
        var contact = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}");
        Assert.Equal(new[] { "Gx Staff" }, Names(contact));
    }

    [Fact]
    public async Task Contact_tags_cache_is_written_through_both_ways()
    {
        var admin = await Authed("admin", "admin123");
        var id = await NewContact(admin, "Wanda Write", "60120000301", new[] { "Wt Legacy" });

        // a free-text name written the OLD way becomes a registry tag
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/tags?applies_to=contact");
        var legacy = list.GetProperty("tags").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "Wt Legacy");
        Assert.Equal(1, legacy.GetProperty("usage").GetInt32());

        // PATCH with names reconciles the assignments
        (await admin.PatchAsJsonAsync($"/api/contacts/{id}", new { tags = new[] { "Wt Other" } })).EnsureSuccessStatusCode();
        var assigned = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}/tags");
        Assert.Equal(new[] { "Wt Other" },
            assigned.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray());

        // a registry write rewrites the name cache the flows/automations read
        var registry = await NewTag(admin, new { name = "Wt Registry", applies_to = "contact" });
        (await admin.PutAsJsonAsync($"/api/contacts/{id}/tags",
            new { tags = new[] { new { tag_id = registry } } })).EnsureSuccessStatusCode();
        var contact = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}");
        Assert.Equal(new[] { "Wt Registry" }, Names(contact));

        // renaming the tag renames it on the contact
        (await admin.PatchAsJsonAsync($"/api/tags/{registry}", new { name = "Wt Renamed" })).EnsureSuccessStatusCode();
        contact = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}");
        Assert.Equal(new[] { "Wt Renamed" }, Names(contact));

        // ?tag=<slug> filters the contact list
        var slug = (await admin.GetFromJsonAsync<JsonElement>($"/api/tags?applies_to=contact")).GetProperty("tags")
            .EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == registry).GetProperty("slug").GetString();
        var filtered = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts?tag={slug}");
        Assert.Equal(new[] { id }, filtered.GetProperty("contacts").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToArray());

        // deleting an in-use tag needs force, and clears the cache
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/tags/{registry}")).StatusCode);
        (await admin.DeleteAsync($"/api/tags/{registry}?force=1")).EnsureSuccessStatusCode();
        contact = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}");
        Assert.Empty(Names(contact));
    }

    [Fact]
    public async Task Tag_attribute_values_are_validated_and_reach_templates()
    {
        var admin = await Authed("admin", "admin123");
        var driver = await NewTag(admin, new
        {
            name = "Av Driver", applies_to = "contact",
            attributes = new object[]
            {
                new { label = "Licence no.", type = "text", required = true },
                new { label = "Years", type = "number" },
                new { label = "Shift", type = "select", options = new[] { "Day", "Night" } },
            },
        });
        var id = await NewContact(admin, "Dan Driver", "60120000401");

        // required attribute missing → 400; bad number → 400; bad option → 400
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/contacts/{id}/tags",
            new { tags = new[] { new { tag_id = driver, values = new { years = "3" } } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/contacts/{id}/tags",
            new { tags = new[] { new { tag_id = driver, values = new { licence_no = "D1", years = "many" } } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/contacts/{id}/tags",
            new { tags = new[] { new { tag_id = driver, values = new { licence_no = "D1", shift = "Noon" } } } })).StatusCode);

        var ok = await admin.PutAsJsonAsync($"/api/contacts/{id}/tags", new
        {
            tags = new[] { new { tag_id = driver, values = new Dictionary<string, object> { ["licence_no"] = "D-123", ["years"] = "7", ["shift"] = "Night", ["junk"] = "x" } } },
        });
        ok.EnsureSuccessStatusCode();
        var row = (await Json(ok)).GetProperty("tags")[0];
        var values = row.GetProperty("values");
        Assert.Equal("D-123", values.GetProperty("licence_no").GetString());
        Assert.Equal(7, values.GetProperty("years").GetDouble());
        Assert.False(values.TryGetProperty("junk", out _));     // unknown keys are dropped
        Assert.Empty(row.GetProperty("missing_required").EnumerateArray());

        // {{tag_<slug>_<key>}} — dashes in the slug become underscores
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<FlowEngine>();
        var contact = await db.Contacts.FirstAsync(c => c.Id == id);
        var vars = engine.VariablesFor(contact);
        Assert.Equal("D-123", vars["tag_av_driver_licence_no"]);
        Assert.Equal("7", vars["tag_av_driver_years"]);
        Assert.Equal("Night", FlowEngine.Render("{{tag_av_driver_shift}}", vars));

        // a contact without the tag still resolves the variable — to empty
        var otherId = await NewContact(admin, "No Tag", "60120000402");
        var other = await db.Contacts.FirstAsync(c => c.Id == otherId);
        Assert.Equal("", FlowEngine.Render("{{tag_av_driver_licence_no}}", engine.VariablesFor(other)));
    }

    [Fact]
    public async Task Entity_tags_and_bindings_for_the_new_kinds()
    {
        var admin = await Authed("admin", "admin123");
        var entities = await admin.GetFromJsonAsync<JsonElement>("/api/v1/entities");
        var mainId = entities.GetProperty("entities").EnumerateArray()
            .First(e => e.GetProperty("slug").GetString() == "main").GetProperty("id").GetInt32();

        var inner = await NewTag(admin, new { name = "Bk Inner", applies_to = "entity", group = "Bk Company type", color = "teal" });
        var outside = await NewTag(admin, new
        {
            name = "Bk Outside", applies_to = "entity", group = "Bk Company type",
            attributes = new[] { new { label = "Contract end", type = "date" } },
        });

        // entity tag editor
        var put = await admin.PutAsJsonAsync("/api/entities/main/tags", new
        {
            tags = new[] { new { tag_id = outside, values = (object)new { contract_end = "2027-03-31" } } },
        });
        put.EnsureSuccessStatusCode();
        Assert.Equal("2027-03-31", (await Json(put)).GetProperty("tags")[0].GetProperty("values")
            .GetProperty("contract_end").GetString());

        // tag-entities binding: binding the rival tag displaces it (group rule)
        var bind = await admin.PostAsJsonAsync("/api/bindings/tag-entities", new { target = inner, add = new[] { mainId } });
        bind.EnsureSuccessStatusCode();
        Assert.Equal(new[] { mainId }, (await Json(bind)).GetProperty("member_ids").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        var onMain = await admin.GetFromJsonAsync<JsonElement>("/api/entities/main/tags");
        Assert.Equal(new[] { inner }, onMain.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("tag_id").GetInt32()).ToArray());

        // tag-contacts binding: add then remove, the cache follows
        var contactTag = await NewTag(admin, new { name = "Bk Contact", applies_to = "contact" });
        var cid = await NewContact(admin, "Bea Binding", "60120000501");
        var add = await admin.PostAsJsonAsync("/api/bindings/tag-contacts", new { target = contactTag, add = new[] { cid } });
        add.EnsureSuccessStatusCode();
        Assert.Equal(1, (await Json(add)).GetProperty("added").GetInt32());
        Assert.Equal(new[] { "Bk Contact" }, Names(await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{cid}")));
        var rem = await admin.PostAsJsonAsync("/api/bindings/tag-contacts", new { target = contactTag, remove = new[] { cid } });
        Assert.Equal(1, (await Json(rem)).GetProperty("removed").GetInt32());
        Assert.Empty(Names(await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/{cid}")));

        // the wrong kind of tag for the binding is refused
        var wrong = await admin.PostAsJsonAsync("/api/bindings/tag-contacts", new { target = inner, add = new[] { cid } });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // entity-accounts: binding moves the account to the entity; unbinding returns it org-wide
        int accountId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var acc = new ChannelAccount { Channel = "telegram", Name = "Bk Bot", CreatedAt = DateTime.UtcNow };
            db.ChannelAccounts.Add(acc);
            await db.SaveChangesAsync();
            accountId = acc.Id;
        }
        (await admin.PostAsJsonAsync("/api/bindings/entity-accounts", new { target = mainId, add = new[] { accountId } }))
            .EnsureSuccessStatusCode();
        var members = await admin.GetFromJsonAsync<JsonElement>($"/api/bindings/entity-accounts?target={mainId}");
        Assert.Contains(accountId, members.GetProperty("member_ids").EnumerateArray().Select(x => x.GetInt32()));
        (await admin.PostAsJsonAsync("/api/bindings/entity-accounts", new { target = mainId, remove = new[] { accountId } }))
            .EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null((await db.ChannelAccounts.FirstAsync(a => a.Id == accountId)).EntityId);
        }

        // catalog: new pools, no provider pool, primary kinds flagged
        var catalog = await admin.GetFromJsonAsync<JsonElement>("/api/bindings/catalog");
        var pools = catalog.GetProperty("pools");
        Assert.True(pools.TryGetProperty("tags", out _));
        Assert.True(pools.TryGetProperty("accounts", out _));
        Assert.False(pools.TryGetProperty("providers", out _));
        var primary = catalog.GetProperty("kinds").EnumerateArray()
            .Where(k => k.GetProperty("primary").GetBoolean()).Select(k => k.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "user-entities", "team-users", "contact-entities", "tag-contacts", "tag-entities", "entity-accounts" }, primary);

        // agents cannot write bindings
        var agent = await Authed("agent", "agent123");
        var agentBind = await agent.PostAsJsonAsync("/api/bindings/tag-contacts", new { target = contactTag, add = new[] { cid } });
        Assert.Equal(HttpStatusCode.Forbidden, agentBind.StatusCode);
    }

    [Fact]
    public void First_boot_migrates_free_text_contact_tags_idempotently()
    {
        using var t = new TestDb();
        var db = t.Db;
        db.Contacts.AddRange(
            new Contact { Name = "A", Phone = "1", Tags = new() { "VIP", "Driver" }, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new Contact { Name = "B", Phone = "2", Tags = new() { "vip" }, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();

        ChatBranch.Data.Tags.TagService.SeedRegistry(db, seedDemo: false);
        ChatBranch.Data.Tags.TagService.SeedRegistry(db, seedDemo: false); // marker-guarded no-op

        var contactTags = db.Tags.Where(x => x.AppliesTo == "contact").ToList();
        Assert.Equal(2, contactTags.Count);                       // "VIP" and "vip" are one tag
        var vip = contactTags.Single(x => x.Slug == "vip");
        Assert.Equal(2, db.TagAssignments.Count(a => a.TagId == vip.Id));
        Assert.Equal(10, db.Tags.Count(x => x.AppliesTo == "conversation"));
        Assert.Equal(3, db.TagAssignments.Count());
        // the cache now holds the registry's canonical names
        Assert.Equal(new[] { "VIP" }, t.Fresh().Contacts.Single(c => c.Name == "B").Tags);
    }

    [Fact]
    public async Task Service_provider_surface_is_gone()
    {
        var admin = await Authed("superadmin", "super123");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/resources")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/resources/1/links")).StatusCode);
        var cid = await NewContact(admin, "Gone Provider", "60120000601");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/contacts/{cid}/resources")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.GetAsync("/api/bindings/provider-contacts?target=1")).StatusCode);

        var who = await admin.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        Assert.False(who.TryGetProperty("resource_id", out _));
        var v1 = await admin.GetFromJsonAsync<JsonElement>("/api/v1/whoami");
        Assert.False(v1.TryGetProperty("resource_id", out _));
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users");
        foreach (var u in users.GetProperty("users").EnumerateArray())
        {
            Assert.False(u.TryGetProperty("resource_id", out _));
            Assert.False(u.TryGetProperty("resource_name", out _));
        }

        // no seeded provider role, no provider custom-field target
        var roles = await admin.GetFromJsonAsync<JsonElement>("/api/auth/roles");
        Assert.DoesNotContain("provider", roles.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("slug").GetString()));
        var fields = await admin.GetFromJsonAsync<JsonElement>("/api/settings/custom-fields");
        Assert.False(fields.TryGetProperty("provider", out _));

        // the capability catalog carries the tag capabilities
        var perms = await admin.GetFromJsonAsync<JsonElement>("/api/auth/permissions");
        var caps = perms.GetProperty("catalog").EnumerateArray().Select(c => c.GetProperty("capability").GetString()).ToList();
        Assert.Contains("tags.view", caps);
        Assert.Contains("tags.manage", caps);
    }
}
