using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Contacts: a name and a phone OR an email; the same phone / email once PER
/// COMPANY (ContactCompanies), never across the whole database; inbound picks
/// the receiving company's contact; chat-made contacts are linked to their
/// conversation's entity (and the one-time backfill links old ones).
/// </summary>
public class ContactsPerCompanyTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ContactsPerCompanyTests(ApiFactory f) => _f = f;

    private T WithDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static int _n;
    private static string Sfx() => $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];
    private static string Phone() => "+6019" + Random.Shared.Next(1000000, 9999999);

    private (int Lumina, int LuminaKl, int Swift, string LSlug, string LKlSlug, string SSlug) Companies()
    {
        var sfx = Sfx();
        return WithDb(db =>
        {
            var tenant = db.Tenants.First();
            var now = DateTime.UtcNow;
            Entity Ent(string slug, int? parent)
            {
                var e = new Entity { TenantId = tenant.Id, Name = slug, Slug = slug, ParentId = parent, CreatedAt = now, UpdatedAt = now };
                db.Entities.Add(e);
                db.SaveChanges();
                return e;
            }
            var l = Ent($"lumina-{sfx}", null);
            var lkl = Ent($"lumina-kl-{sfx}", l.Id);
            var s = Ent($"swift-{sfx}", null);
            return (l.Id, lkl.Id, s.Id, l.Slug, lkl.Slug, s.Slug);
        });
    }

    [Fact]
    public async Task A_contact_needs_a_name_and_a_phone_or_an_email()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var email = $"only-{Sfx()}@example.com";
        var v1 = await admin.PostAsJsonAsync("/api/v1/contacts", new { name = "Email Only", email });
        Assert.Equal(HttpStatusCode.Created, v1.StatusCode);
        var id = (await v1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        // only the email identity is registered
        var channels = WithDb(db => db.ContactIdentities.Where(i => i.ContactId == id).Select(i => i.Channel).ToList());
        Assert.Equal(new[] { "email" }, channels);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/v1/contacts", new { name = "Nobody" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/v1/contacts", new { email = "x@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/v1/contacts", new { name = "Bad", email = "not-an-email" })).StatusCode);

        var plain = await admin.PostAsJsonAsync("/api/contacts", new { name = "Email Only 2", email = $"two-{Sfx()}@example.com" });
        Assert.True(plain.IsSuccessStatusCode, await plain.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/contacts", new { name = "Nobody" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/contacts", new { name = "Bad", email = "nope@" })).StatusCode);
    }

    [Fact]
    public async Task The_same_phone_is_one_contact_per_company()
    {
        var c = Companies();
        var admin = await Platform.As(_f, "admin", "admin123");
        var phone = Phone();
        var lumina = await admin.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Ana Lumina", phone, entity_slugs = new[] { c.LSlug } });
        Assert.Equal(HttpStatusCode.Created, lumina.StatusCode);
        var swift = await admin.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Ana Swift", phone, entity_slugs = new[] { c.SSlug } });
        Assert.Equal(HttpStatusCode.Created, swift.StatusCode);

        // twice in Lumina (its branch counts as Lumina): 409 naming the existing one
        var twice = await admin.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Ana Again", phone, entity_slugs = new[] { c.LKlSlug } });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        var body = await twice.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Ana Lumina", body.GetProperty("detail").GetString());
        var luminaId = (await lumina.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(luminaId, body.GetProperty("existing_id").GetInt32());

        // the same email, per company too
        var email = $"ana-{Sfx()}@example.com";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Mail Lumina", email, entity_slugs = new[] { c.LSlug } })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Mail Swift", email, entity_slugs = new[] { c.SSlug } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Mail Again", email = email.ToUpperInvariant(), entity_slugs = new[] { c.LSlug } })).StatusCode);

        // an edit follows the same rule
        var swiftId = (await swift.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var other = Phone();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Bo Swift", phone = other, entity_slugs = new[] { c.SSlug } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PatchAsJsonAsync($"/api/v1/contacts/{swiftId}", new { phone = other })).StatusCode);
        // "add to the existing contact" links instead of duplicating
        Assert.True((await admin.PostAsJsonAsync($"/api/v1/contacts/{luminaId}/entities", new { entity = c.LKlSlug }))
            .IsSuccessStatusCode);
        Assert.Equal(2, WithDb(db => db.ContactEntities.Count(l => l.ContactId == luminaId)));
    }

    [Fact]
    public async Task A_clash_the_caller_cannot_see_never_names_the_contact()
    {
        var c = Companies();
        var admin = await Platform.As(_f, "admin", "admin123");
        var phone = Phone();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Hidden Head Office", phone, entity_slugs = new[] { c.LSlug } })).StatusCode);

        // a company admin bound to Lumina's BRANCH: same company, but head office's contact is not theirs
        var su = await Platform.As(_f, "superadmin", "super123");
        var user = $"klboss{Sfx()}";
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/users",
            new { username = user, password = "password123", role = "admin", entity_ids = new[] { c.LuminaKl } })).StatusCode);
        var kl = await Platform.As(_f, user, "password123");
        var res = await kl.PostAsJsonAsync("/api/v1/contacts", new { name = "Mine", phone, entity_slugs = new[] { c.LKlSlug } });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Hidden Head Office", text);

        // a contact of ANOTHER company is no clash at all
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Other company", phone, entity_slugs = new[] { c.SSlug } })).StatusCode);
    }

    [Fact]
    public void Import_creates_the_second_companys_contact()
    {
        var c = Companies();
        var phone = Phone();
        WithDb(db =>
        {
            var now = DateTime.UtcNow;
            var existing = new Contact { Name = "Imp Lumina", Phone = phone, TenantId = db.Tenants.First().Id, CreatedAt = now, UpdatedAt = now };
            db.Contacts.Add(existing);
            db.SaveChanges();
            db.ContactEntities.Add(new ContactEntity { ContactId = existing.Id, EntityId = c.Lumina, CreatedAt = now });
            return db.SaveChanges();
        });
        var report = WithDb(db =>
        {
            var table = new ImportTable();
            table.Add(new Dictionary<string, string> { ["Name"] = "Imp Swift", ["Phone"] = phone }, 2);
            var importer = new ContactImporter(db, new ScopingService(db));
            return importer.Run(table, new Dictionary<string, string> { ["Name"] = "name", ["Phone"] = "phone" },
                new ImportOptions { Preview = false, EntityId = c.Swift, TenantId = db.Tenants.First().Id, Discover = false });
        });
        Assert.Equal(1, report.Created);
        Assert.Equal(0, report.Duplicates);
        Assert.True(WithDb(db => db.Contacts.Any(x => x.Name == "Imp Swift"
                                                     && x.EntityLinks.Any(l => l.EntityId == c.Swift))));
    }

    [Fact]
    public void An_inbound_on_a_companys_number_links_that_companys_contact()
    {
        var c = Companies();
        var phone = Phone();
        var digits = phone.TrimStart('+');
        var (swiftContact, swiftAccount) = WithDb(db =>
        {
            var now = DateTime.UtcNow;
            var tid = db.Tenants.First().Id;
            Contact Make(string name, int entity)
            {
                var x = new Contact { Name = name, Phone = phone, TenantId = tid, CreatedAt = now, UpdatedAt = now };
                db.Contacts.Add(x);
                db.SaveChanges();
                db.ContactEntities.Add(new ContactEntity { ContactId = x.Id, EntityId = entity, CreatedAt = now });
                db.SaveChanges();
                return x;
            }
            Make("In Lumina", c.Lumina);           // older — the old rule would pick this one
            var s = Make("In Swift", c.Swift);
            var acct = new ChannelAccount { Channel = "whatsapp", Provider = "baileys", Name = "Swift line",
                EntityId = c.Swift, CreatedAt = now };
            db.ChannelAccounts.Add(acct);
            db.SaveChanges();
            return (s.Id, acct.Id);
        });
        using (var scope = _f.Services.CreateScope())
        {
            var inbound = scope.ServiceProvider.GetRequiredService<InboundProcessor>();
            inbound.ProcessInbound("whatsapp", digits, "hi", accountId: swiftAccount);
        }
        Assert.Equal(swiftContact, WithDb(db => db.Threads.First(t => t.Channel == "whatsapp" && t.Key == digits).ContactId));

        // the matcher never crosses companies: a number known only to Lumina
        // does not link a chat on Swift's number
        var lonely = Phone();
        WithDb(db =>
        {
            var now = DateTime.UtcNow;
            var x = new Contact { Name = "Only Lumina", Phone = lonely, TenantId = db.Tenants.First().Id, CreatedAt = now, UpdatedAt = now };
            db.Contacts.Add(x);
            db.SaveChanges();
            db.ContactEntities.Add(new ContactEntity { ContactId = x.Id, EntityId = c.Lumina, CreatedAt = now });
            return db.SaveChanges();
        });
        using (var scope = _f.Services.CreateScope())
        {
            var inbound = scope.ServiceProvider.GetRequiredService<InboundProcessor>();
            inbound.ProcessInbound("whatsapp", lonely.TrimStart('+'), "hi", accountId: swiftAccount);
        }
        Assert.Null(WithDb(db => db.Threads.First(t => t.Channel == "whatsapp" && t.Key == lonely.TrimStart('+')).ContactId));
    }

    [Fact]
    public async Task Chat_made_contacts_belong_to_their_conversations_entity()
    {
        var c = Companies();
        var admin = await Platform.As(_f, "admin", "admin123");
        // a contact with no link, only a conversation under Swift
        var (contactId, key) = WithDb(db =>
        {
            var now = DateTime.UtcNow;
            var x = new Contact { Name = $"Chatty {Sfx()}", Phone = Phone(), TenantId = db.Tenants.First().Id, CreatedAt = now, UpdatedAt = now };
            db.Contacts.Add(x);
            db.SaveChanges();
            var k = x.Phone.TrimStart('+');
            db.Threads.Add(new Thread { Channel = "whatsapp", Key = k, Address = k, ContactId = x.Id, EntityId = c.Swift,
                CreatedAt = now, UpdatedAt = now });
            db.SaveChanges();
            return (x.Id, k);
        });
        // the contacts list under Swift shows it already (it has a conversation there)
        var under = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts?entity={c.SSlug}&limit=500");
        Assert.Contains(contactId, under.GetProperty("contacts").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        // …and "Unassigned (no entity)" finds it until it is linked
        var none = await admin.GetFromJsonAsync<JsonElement>("/api/contacts?entity=__none__&limit=5000");
        Assert.Contains(contactId, none.GetProperty("contacts").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));

        // the one-time backfill links it to the conversation's entity
        WithDb(db =>
        {
            db.Settings.RemoveRange(db.Settings.Where(s => s.Key == Seed.ContactEntityBackfillKey));
            db.SaveChanges();
            Seed.BackfillContactEntities(db);
            return 0;
        });
        Assert.True(WithDb(db => db.ContactEntities.Any(l => l.ContactId == contactId && l.EntityId == c.Swift)));
        // …only once: a link removed afterwards is not put back on the next boot
        WithDb(db =>
        {
            db.ContactEntities.RemoveRange(db.ContactEntities.Where(l => l.ContactId == contactId));
            db.SaveChanges();
            Seed.BackfillContactEntities(db);
            return 0;
        });
        Assert.False(WithDb(db => db.ContactEntities.Any(l => l.ContactId == contactId)));
        Assert.NotNull(key);
    }

    [Fact]
    public void A_contact_created_from_a_chat_is_linked_to_the_receiving_entity()
    {
        var c = Companies();
        WithDb(db =>
        {
            var now = DateTime.UtcNow;
            var acct = new ChannelAccount { Channel = "whatsapp", Provider = "baileys", Name = "Swift desk", EntityId = c.Swift, CreatedAt = now };
            db.ChannelAccounts.Add(acct);
            db.SaveChanges();
            var k = Phone().TrimStart('+');
            var t = new Thread { Channel = "whatsapp", Key = k, Address = k, AccountId = acct.Id, EntityId = c.Swift, CreatedAt = now, UpdatedAt = now };
            db.Threads.Add(t);
            db.SaveChanges();
            var sc = new ScopingService(db);
            var svc = new IdentityRequestService(db, sc, new ChatBranch.Data.Stores.ThreadStore(db, sc), null!);
            var contact = svc.CreateContact(null, t, acct, db.Tenants.First().Id,
                new IdentityDetails("Walk In", "", "", false), "test", tagSelfIdentified: false);
            Assert.True(db.ContactEntities.Any(l => l.ContactId == contact.Id && l.EntityId == c.Swift));
            return 0;
        });
    }
}
