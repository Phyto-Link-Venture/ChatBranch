using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The AI bill: usage rolls up to its company (top-level entity), cost is
/// marked up and converted per the superadmin's terms, companies see only
/// billed amounts, and the raw cost ledger is billing.manage only.
/// </summary>
public class AiBillingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AiBillingTests(ApiFactory f) => _f = f;

    private T With<T>(Func<AppDbContext, T> act) => SettingsTestKit.WithDb(_f, act);

    [Fact]
    public async Task Bills_roll_up_to_the_company_with_markup_fee_and_currency()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var agent = await Platform.As(_f, "agent", "agent123");

        // a company with a branch; usage on the branch, usage via a contact, platform usage
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = "Bill Co", slug = "bill-co" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = "Bill Co Branch", slug = "bill-co-branch", parent = "bill-co" })).StatusCode);
        var (company, branch) = With(db => (db.Entities.Single(e => e.Slug == "bill-co").Id,
            db.Entities.Single(e => e.Slug == "bill-co-branch").Id));
        var month = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        With(db =>
        {
            var contact = new Contact { Name = "Bill Customer", Phone = "+60100000991", CreatedAt = month, UpdatedAt = month };
            db.Contacts.Add(contact);
            db.SaveChanges();
            db.ContactEntities.Add(new ContactEntity { ContactId = contact.Id, EntityId = branch });
            // gpt-4o: $2.50 in / $10 out per 1M → 1M in + 0.1M out = $3.50
            db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "gpt-4o", EntityId = branch,
                PromptTokens = 1_000_000, CompletionTokens = 100_000, CreatedAt = month });
            // via the contact's entity: $0.50
            db.LlmUsage.Add(new LlmUsage { Feature = "summary", Model = "gpt-4o", ContactId = contact.Id,
                PromptTokens = 200_000, CreatedAt = month });
            // platform work, no company
            db.LlmUsage.Add(new LlmUsage { Feature = "skill_draft", Model = "gpt-4o", PromptTokens = 400_000, CreatedAt = month });
            // another month — not on this bill
            db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "gpt-4o", EntityId = branch,
                PromptTokens = 1_000_000, CreatedAt = month.AddMonths(1) });
            db.SaveChanges();
            return 0;
        });

        // only billing.manage sets the terms
        var terms = new
        {
            currency = "MYR", usd_rate = 4.5, default_markup_pct = 10, default_monthly_fee = 0,
            companies = new Dictionary<string, object> { [company.ToString()] = new { markup_pct = 50, monthly_fee = 100 } },
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/billing/ai/settings", terms)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PutAsJsonAsync("/api/billing/ai/settings",
            new { currency = "MYR", usd_rate = 4.5, companies = new Dictionary<string, object> { [branch.ToString()] = new { markup_pct = 5 } } })).StatusCode);
        (await su.PutAsJsonAsync("/api/billing/ai/settings", terms)).EnsureSuccessStatusCode();
        try
        {
            // the manager's view: cost, markup and margin
            var bill = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai?month=2026-03&company=bill-co");
            Assert.Equal("MYR", bill.GetProperty("currency").GetString());
            var s = bill.GetProperty("company").GetProperty("summary");
            Assert.Equal(2, s.GetProperty("requests").GetInt32());
            Assert.Equal(4.0, s.GetProperty("cost_usd").GetDouble(), 3);
            Assert.Equal(27.0, s.GetProperty("usage_amount").GetDouble(), 2);   // 4.00 × 1.5 × 4.5
            Assert.Equal(100.0, s.GetProperty("monthly_fee").GetDouble(), 2);
            Assert.Equal(127.0, s.GetProperty("total").GetDouble(), 2);
            Assert.Equal(109.0, s.GetProperty("margin").GetDouble(), 2);        // 127 − 4.00 × 4.5
            Assert.Equal(1.0, bill.GetProperty("unattributed").GetProperty("cost_usd").GetDouble(), 3);
            var lines = bill.GetProperty("company").GetProperty("lines").EnumerateArray().ToList();
            Assert.Equal("chat_reply", lines[0].GetProperty("feature").GetString());
            Assert.InRange(lines[0].GetProperty("amount").GetDouble(), 23.62, 23.63); // 3.50 × 1.5 × 4.5 = 23.625

            // the company's view: billed amounts only
            var theirs = await admin.GetFromJsonAsync<JsonElement>("/api/billing/ai?month=2026-03&company=bill-co");
            var ts = theirs.GetProperty("company").GetProperty("summary");
            Assert.Equal(127.0, ts.GetProperty("total").GetDouble(), 2);
            Assert.Equal(JsonValueKind.Null, ts.GetProperty("cost_usd").ValueKind);
            Assert.Equal(JsonValueKind.Null, ts.GetProperty("markup_pct").ValueKind);
            Assert.Equal(JsonValueKind.Null, ts.GetProperty("margin").ValueKind);
            Assert.Equal(JsonValueKind.Null, theirs.GetProperty("unattributed").ValueKind);
            Assert.Equal(JsonValueKind.Null, theirs.GetProperty("company").GetProperty("lines")[0].GetProperty("cost_usd").ValueKind);

            // agents don't see bills; the raw cost ledger is the manager's
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/billing/ai")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/analytics/usage?days=7")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await su.GetAsync("/api/billing/ai?month=March")).StatusCode);
        }
        finally
        {
            await su.PutAsJsonAsync("/api/billing/ai/settings", new { currency = "USD", usd_rate = 1 });
        }
    }
}
