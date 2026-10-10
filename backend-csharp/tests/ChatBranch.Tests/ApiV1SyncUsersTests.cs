using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// /api/v1: the bulk record sync (camelCase or snake_case bodies, the ONE
/// last-8-digit phone rule, skipped rows counted apart from created/updated)
/// and entity access for custom-role users, bounded by the creator's scope.
/// </summary>
public class ApiV1SyncUsersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ApiV1SyncUsersTests(ApiFactory f) => _f = f;

    private Task<HttpClient> Admin() => SettingsTestKit.Client(_f, "admin", "admin123");
    private Task<HttpClient> Super() => SettingsTestKit.Client(_f, "superadmin", "super123");

    private static async Task<JsonElement> Json(HttpResponseMessage res) =>
        await res.Content.ReadFromJsonAsync<JsonElement>();

    // ---- record sync -----------------------------------------------------------

    [Fact]
    public async Task Record_sync_takes_snake_case_matches_phones_by_last_8_digits_and_counts_skips()
    {
        var admin = await Admin();
        (await admin.PostAsJsonAsync("/api/contacts", new { name = "Existing Siti", phone = "60177000501" }))
            .EnsureSuccessStatusCode();

        var res = await admin.PostAsJsonAsync("/api/v1/sync/records", new
        {
            records = new object[]
            {
                new { external_id = "INV-77", type = "invoice", title = "March", amount = "120.50",
                      due_date = "2026-11-01", contact = new { phone = "+60 17-700 0501", name = "Siti R." } },
                new { externalId = "INV-78", dueDate = "2026-11-02T08:00:00Z",
                      contact = new { phone = "0177000502", name = "New Person" } },
                new { external_id = "INV-79", contact = new { name = "No Phone" } },
                new { title = "no id", contact = new { phone = "60177000503" } },
            },
        });
        res.EnsureSuccessStatusCode();
        var body = await Json(res);
        Assert.Equal(2, body.GetProperty("created").GetInt32());
        Assert.Equal(0, body.GetProperty("updated").GetInt32());
        Assert.Equal(2, body.GetProperty("skipped").GetInt32());
        Assert.Equal(4, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("contacts_created").GetInt32());
        var reasons = body.GetProperty("skipped_rows").EnumerateArray()
            .Select(r => r.GetProperty("reason").GetString()).ToList();
        Assert.Contains("no contact phone", reasons);
        Assert.Contains("missing externalId", reasons);

        SettingsTestKit.WithDb(_f, db =>
        {
            var siti = db.Contacts.Where(c => c.Name == "Existing Siti").ToList();
            Assert.Single(siti);                                              // no twin
            var rec = db.Records.First(r => r.ExternalId == "INV-77");
            Assert.Equal(siti[0].Id, rec.ContactId);
            Assert.Equal(120.50m, rec.Amount);
            Assert.Equal(new DateOnly(2026, 11, 1), rec.DueDate);
            Assert.Equal(new DateOnly(2026, 11, 2), db.Records.First(r => r.ExternalId == "INV-78").DueDate);
        });

        var again = await Json(await admin.PostAsJsonAsync("/api/v1/sync/records",
            new { records = new[] { new { external_id = "INV-77", type = "invoice", status = "paid",
                contact = new { phone = "0177000501" } } } }));
        Assert.Equal(1, again.GetProperty("updated").GetInt32());
    }

    // ---- users: entity access for custom roles ---------------------------------

    [Fact]
    public async Task Custom_role_users_take_entity_ids_bounded_by_the_creators_scope()
    {
        var super = await Super();
        var role = await super.PostAsJsonAsync("/api/auth/roles", new { label = "Branch lead" });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        var slug = (await Json(role)).GetProperty("slug").GetString()!;
        var (mainId, otherId) = SettingsTestKit.WithDb(_f, db =>
        {
            var main = db.Entities.First(e => e.Slug == "main");
            var other = new Tenant { Name = "Other Co", Slug = "other-co-users", CreatedAt = DateTime.UtcNow };
            db.Tenants.Add(other);
            db.SaveChanges();
            var foreign = new Entity { TenantId = other.Id, Name = "Foreign", Slug = "foreign-users", CreatedAt = DateTime.UtcNow };
            db.Entities.Add(foreign);
            db.SaveChanges();
            return (main.Id, foreign.Id);
        });

        var created = await super.PostAsJsonAsync("/api/v1/users",
            new { username = "lead-one", password = "pass12345", role = slug, entity_ids = new[] { mainId } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = await Json(created);
        Assert.Equal(slug, user.GetProperty("role").GetString());
        Assert.Equal(mainId, user.GetProperty("entities").EnumerateArray().Single().GetProperty("id").GetInt32());

        // an admin can't grant entities outside their own tenant
        var admin = await Admin();
        var outside = await admin.PostAsJsonAsync("/api/v1/users",
            new { username = "lead-two", password = "pass12345", role = slug, entity_ids = new[] { otherId } });
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);

        // switching agent ↔ custom role keeps the binding; an admin WITH entities is
        // a company admin; a superadmin never takes one
        var id = user.GetProperty("id").GetInt32();
        var toAgent = await Json(await super.PostAsJsonAsync($"/api/v1/users/{id}", new { role = "agent" }));
        Assert.Single(toAgent.GetProperty("entities").EnumerateArray());
        var companyAdmin = await super.PostAsJsonAsync("/api/v1/users",
            new { username = "admin-ent", password = "pass12345", role = "admin", entity_ids = new[] { mainId } });
        Assert.Equal(HttpStatusCode.Created, companyAdmin.StatusCode);
        Assert.True((await Json(companyAdmin)).GetProperty("company_admin").GetBoolean());
        var bad = await super.PostAsJsonAsync("/api/v1/users",
            new { username = "super-ent", password = "pass12345", role = "superadmin", entity_ids = new[] { mainId } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
