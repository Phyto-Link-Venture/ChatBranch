using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// AI PRICE BOOKS: named plans, assigned in bulk, inherited down the entity tree.
///
/// What is proved here: the resolution order lives in one place and runs in the
/// documented order (per-company override → the plan assigned nearest up the tree
/// → the organization default plan → the built-in defaults); all three price modes
/// bill; a reassignment prices the current month and later and never rewrites a
/// month already billed; today's settings keep producing exactly today's bills;
/// one bindings call prices a hundred entities; and the per-login allowance comes
/// from the plan instead of a thousand typed rows.
/// </summary>
public class AiPriceBookTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AiPriceBookTests(ApiFactory f) => _f = f;

    private T With<T>(Func<AppDbContext, T> act) => SettingsTestKit.WithDb(_f, act);

    private static int _n;
    private static string Sfx() => $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..6];

    // ---- helpers -------------------------------------------------------------------------

    private async Task<int> Entity(HttpClient su, string slug, string? parent = null)
    {
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = slug.Replace('-', ' '), slug, parent })).StatusCode);
        return With(db => db.Entities.Single(e => e.Slug == slug).Id);
    }

    /// <summary>Create (or change) a plan; answers its id.</summary>
    private static async Task<int> Plan(HttpClient su, object body)
    {
        var res = await su.PostAsJsonAsync("/api/billing/ai/price-books", body);
        res.EnsureSuccessStatusCode();
        var payload = await res.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("plan").GetProperty("id").GetInt32();
    }

    /// <summary>The bulk assignment: ONE call, many entities (the bindings pattern).</summary>
    private static async Task<JsonElement> Assign(HttpClient su, int planId, int[]? add = null, int[]? remove = null)
    {
        var res = await su.PostAsJsonAsync("/api/bindings/pricebook-entities",
            new { target = planId, add = add ?? Array.Empty<int>(), remove = remove ?? Array.Empty<int>() });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private void Spend(int? entityId, double costUsd, string feature = "chat_reply", int tokens = 0,
        DateTime? at = null)
    {
        With(db =>
        {
            db.LlmUsage.Add(new LlmUsage
            {
                Feature = feature, Model = "price-book-test-model", EntityId = entityId,
                PromptTokens = tokens, CostUsd = costUsd, CreatedAt = at ?? DateTime.UtcNow,
            });
            db.SaveChanges();
            return 0;
        });
        AiQuota.Invalidate();
    }

    private static async Task<JsonElement> Bill(HttpClient c, string company, string? month = null) =>
        await c.GetFromJsonAsync<JsonElement>(
            $"/api/billing/ai?company={company}" + (month == null ? "" : $"&month={month}"));

    private static JsonElement Summary(JsonElement bill) => bill.GetProperty("company").GetProperty("summary");

    /// <summary>Back to the shipped defaults (USD at 1, no markup, no fee, no overrides).</summary>
    private static async Task ResetTerms(HttpClient su) =>
        (await su.PutAsJsonAsync("/api/billing/ai/settings", new
        {
            currency = "USD", usd_rate = 1, default_markup_pct = 0, default_monthly_fee = 0,
            companies = new Dictionary<string, object>(),
        })).EnsureSuccessStatusCode();

    // ---- the resolution order, with no database in the way --------------------------------

    private static BillingTerms Terms(params PricePlan[] plans) => new()
    {
        Currency = "MYR", UsdRate = 4, DefaultMarkupPct = 10, DefaultMonthlyFee = 7,
        Plans = plans.ToList(),
    };

    private static PricePlan MarkupPlan(string slug, double pct, double fee = 0, double allowance = 0) => new()
    {
        Slug = slug, Label = slug.ToUpperInvariant(), Mode = AiPriceModes.Markup, MarkupPct = pct,
        MonthlyFee = fee, UserAllowance = allowance,
    };

    [Fact]
    public void Resolution_runs_override_then_plan_then_org_default_then_builtin()
    {
        // one company (7) with one branch (8) beneath it
        var company = new List<int> { 7 };
        var branch = new List<int> { 8, 7 };

        // 4. nothing anywhere: the terms' own defaults — exactly what billed before price books
        var bare = AiPriceBooks.Resolve(Terms(), company);
        Assert.Equal(EffectivePrice.FromBuiltin, bare.Source);
        Assert.Equal(10, bare.MarkupPct);
        Assert.Equal(7, bare.FixedMonthly());
        Assert.Null(bare.PlanSlug);

        // 3. the organization default plan
        var terms = Terms(MarkupPlan("house", 20, fee: 5), MarkupPlan("vip", 1), MarkupPlan("gone", 99));
        terms.Plans.Single(p => p.Slug == "gone").Active = false;
        terms.DefaultPlan = "house";
        var byDefault = AiPriceBooks.Resolve(terms, company);
        Assert.Equal(EffectivePrice.FromOrgDefault, byDefault.Source);
        Assert.Equal(20, byDefault.MarkupPct);
        Assert.Equal(5, byDefault.FixedMonthly());

        // 2. a plan assigned to the company beats the organization default
        terms.EntityPlans["7"] = "vip";
        var assigned = AiPriceBooks.Resolve(terms, company);
        Assert.Equal(EffectivePrice.FromPlan, assigned.Source);
        Assert.Equal("vip", assigned.PlanSlug);
        Assert.Equal(7, assigned.AssignedAt);
        Assert.False(assigned.InheritedBy(7));

        // an INACTIVE plan is as good as no assignment: back to the organization default
        terms.EntityPlans["7"] = "gone";
        Assert.Equal(EffectivePrice.FromOrgDefault, AiPriceBooks.Resolve(terms, company).Source);
        terms.EntityPlans["7"] = "vip";

        // 1. the per-company explicit override wins, field by field
        terms.Companies["7"] = new CompanyTerms { MarkupPct = 42 };
        var overridden = AiPriceBooks.Resolve(terms, company);
        Assert.Equal(EffectivePrice.FromOverride, overridden.Source);
        Assert.Equal(42, overridden.MarkupPct);
        Assert.Equal(AiPriceModes.Markup, overridden.Mode);
        // what the override does NOT set still comes from the plan ("vip" charges no fee)
        Assert.Equal(0, overridden.FixedMonthly());
        terms.Companies["7"] = new CompanyTerms { MonthlyFee = 300 };
        var feeOnly = AiPriceBooks.Resolve(terms, company);
        Assert.Equal(1, feeOnly.MarkupPct);        // still the plan's
        Assert.Equal(300, feeOnly.FixedMonthly()); // the override's

        // the branch: a plan on the parent flows DOWN to it (nearest level wins)
        terms.Companies.Clear();
        var inherited = AiPriceBooks.Resolve(terms, branch);
        Assert.Equal("vip", inherited.PlanSlug);
        Assert.Equal(7, inherited.AssignedAt);
        Assert.True(inherited.InheritedBy(8));
        // …until the branch has one of its own
        terms.EntityPlans["8"] = "house";
        var own = AiPriceBooks.Resolve(terms, branch);
        Assert.Equal("house", own.PlanSlug);
        Assert.False(own.InheritedBy(8));
    }

    [Fact]
    public void The_three_price_modes_each_compute_an_amount()
    {
        var terms = Terms();
        terms.Plans.Add(MarkupPlan("mk", 25, fee: 9));
        terms.Plans.Add(new PricePlan { Slug = "fl", Label = "Flat", Mode = AiPriceModes.Flat, FlatAmount = 300 });
        terms.Plans.Add(new PricePlan
        {
            Slug = "un", Label = "Unit", Mode = AiPriceModes.Unit,
            UnitPer1kTokens = 0.002, UnitPerReply = 0.05, MonthlyFee = 1,
        });
        var company = new List<int> { 7 };

        terms.EntityPlans["7"] = "mk";
        var markup = AiPriceBooks.Resolve(terms, company);
        Assert.Equal(4 * 1.25 * 4, markup.UsageAmount(4, 1_000_000, 3, terms.UsdRate), 6);  // cost × markup × rate
        Assert.Equal(9, markup.FixedMonthly());

        terms.EntityPlans["7"] = "fl";
        var flat = AiPriceBooks.Resolve(terms, company);
        Assert.Equal(0, flat.UsageAmount(4, 1_000_000, 3, terms.UsdRate));                  // usage is not charged
        Assert.Equal(300, flat.FixedMonthly());
        // …but a quota still counts what the usage cost, or a flat client could never hit one
        Assert.Equal(16, flat.QuotaAmount(4, 1_000_000, 3, terms.UsdRate), 6);

        terms.EntityPlans["7"] = "un";
        var unit = AiPriceBooks.Resolve(terms, company);
        // 15,000 tokens ÷ 1,000 × 0.002 + 2 replies × 0.05 — quoted in the billing currency
        Assert.Equal(0.13, unit.UsageAmount(4, 15_000, 2, terms.UsdRate), 6);
        Assert.Equal(1, unit.FixedMonthly());
        // only customer-facing replies are a billable unit
        Assert.True(AiPriceBooks.IsReply("chat_reply"));
        Assert.True(AiPriceBooks.IsReply("follow_up"));
        Assert.False(AiPriceBooks.IsReply("embedding"));
        Assert.False(AiPriceBooks.IsReply("assistant"));
    }

    [Fact]
    public void Todays_terms_resolve_to_exactly_todays_numbers()
    {
        // the pre-price-book shape: defaults plus the per-company override map
        var terms = new BillingTerms
        {
            Currency = "MYR", UsdRate = 4.5, DefaultMarkupPct = 10, DefaultMonthlyFee = 7,
            Companies =
            {
                ["7"] = new CompanyTerms { MarkupPct = 50, MonthlyFee = 100 },
                ["8"] = new CompanyTerms { MonthlyFee = 0 },
            },
        };
        foreach (var id in new[] { 7, 8, 9 })
        {
            var price = AiPriceBooks.Resolve(terms, new List<int> { id });
            Assert.Equal(terms.MarkupFor(id), price.MarkupPct);
            Assert.Equal(terms.FeeFor(id), price.FixedMonthly());
            Assert.Equal(terms.BilledRaw(id, 3.25), price.UsageAmount(3.25, 1234, 2, terms.UsdRate), 9);
        }
    }

    // ---- the bill itself ------------------------------------------------------------------

    [Fact]
    public async Task All_three_modes_bill_and_a_branch_plan_beats_its_companys()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var sfx = Sfx();
        await ResetTerms(su);
        var mk = await Entity(su, $"pb-mk-{sfx}");
        var branch = await Entity(su, $"pb-mkb-{sfx}", $"pb-mk-{sfx}");
        var fl = await Entity(su, $"pb-fl-{sfx}");
        var un = await Entity(su, $"pb-un-{sfx}");

        var markup = await Plan(su, new { label = $"Markup {sfx}", slug = $"mk-{sfx}", mode = "markup", markup_pct = 12.5 });
        var steep = await Plan(su, new { label = $"Steep {sfx}", slug = $"st-{sfx}", mode = "markup", markup_pct = 100 });
        var flat = await Plan(su, new
        {
            label = $"Flat {sfx}", slug = $"fl-{sfx}", mode = "flat", flat_amount = 300, monthly_fee = 20,
        });
        var unit = await Plan(su, new
        {
            label = $"Unit {sfx}", slug = $"un-{sfx}", mode = "unit",
            unit_per_1k_tokens = 0.002, unit_per_reply = 0.05,
        });

        // one call each — and the branch gets a plan of its own, which must win for itself
        await Assign(su, markup, new[] { mk });
        await Assign(su, steep, new[] { branch });
        await Assign(su, flat, new[] { fl });
        await Assign(su, unit, new[] { un });

        Spend(mk, 4);                                              // 4 × 1.125 = 4.50
        Spend(branch, 1);                                          // 1 × 2     = 2.00
        Spend(fl, 10);                                             // flat: nothing for usage
        Spend(un, 9, tokens: 10_000);                              // a reply, 10k tokens
        Spend(un, 9, feature: "embedding", tokens: 5_000);         // overhead: tokens only

        var markupBill = Summary(await Bill(su, $"pb-mk-{sfx}"));
        Assert.Equal(6.5, markupBill.GetProperty("usage_amount").GetDouble(), 4);   // 4.50 + 2.00
        Assert.Equal(12.5, markupBill.GetProperty("markup_pct").GetDouble(), 4);    // the COMPANY's plan
        Assert.Equal($"mk-{sfx}", markupBill.GetProperty("plan_slug").GetString());
        Assert.Equal("markup", markupBill.GetProperty("mode").GetString());
        Assert.Equal(0, markupBill.GetProperty("monthly_fee").GetDouble(), 4);
        // decimals are allowed now: 12.5% is a real markup, not 12 or 13
        Assert.Equal(5.0, markupBill.GetProperty("cost_usd").GetDouble(), 4);

        var flatBill = Summary(await Bill(su, $"pb-fl-{sfx}"));
        Assert.Equal(0, flatBill.GetProperty("usage_amount").GetDouble(), 4);
        Assert.Equal(320, flatBill.GetProperty("monthly_fee").GetDouble(), 4);      // 300 flat + 20 fee
        Assert.Equal(320, flatBill.GetProperty("total").GetDouble(), 4);
        Assert.Equal("flat", flatBill.GetProperty("mode").GetString());
        Assert.Equal(310, flatBill.GetProperty("margin").GetDouble(), 4);           // 320 − 10 cost

        var unitBill = Summary(await Bill(su, $"pb-un-{sfx}"));
        // 15,000 tokens ÷ 1,000 × 0.002 + ONE reply × 0.05 (the embedding row is not a reply)
        Assert.Equal(0.08, unitBill.GetProperty("usage_amount").GetDouble(), 4);
        Assert.Equal("unit", unitBill.GetProperty("mode").GetString());
        Assert.Equal(-17.92, unitBill.GetProperty("margin").GetDouble(), 4);        // unit pricing can lose money

        // the quotas price the same month the same way (one resolution, two readers)
        var quotas = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/quotas");
        double Spent(int id) => quotas.GetProperty("entities").EnumerateArray()
            .Single(e => e.GetProperty("id").GetInt32() == id).GetProperty("spent").GetDouble();
        Assert.Equal(6.5, Spent(mk), 4);
        Assert.Equal(10, Spent(fl), 4);      // flat: the quota counts what it cost
    }

    [Fact]
    public async Task Reassigning_a_plan_prices_this_month_and_leaves_a_billed_month_alone()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var sfx = Sfx();
        await ResetTerms(su);
        var slug = $"pb-ver-{sfx}";
        var company = await Entity(su, slug);
        var now = DateTime.UtcNow;
        var lastMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-5);
        var lastKey = AiBilling.MonthKey(lastMonth);
        var thisKey = AiBilling.MonthKey(now);
        Spend(company, 2, at: lastMonth);
        Spend(company, 2);

        var plan = await Plan(su, new { label = $"Early {sfx}", slug = $"ver-{sfx}", mode = "markup", markup_pct = 10 });
        await Assign(su, plan, new[] { company });
        // make those terms the ones that billed LAST month too (they would otherwise start today)
        With(db =>
        {
            AiPriceBooks.SyncTerms(db, new ScopingService(db), lastMonth);
            return 0;
        });
        Assert.Equal(2.2, Summary(await Bill(su, slug, lastKey)).GetProperty("usage_amount").GetDouble(), 4);

        // now change the plan — and bill again
        await Plan(su, new { slug = $"ver-{sfx}", label = $"Early {sfx}", markup_pct = 200 });
        Assert.Equal(6.0, Summary(await Bill(su, slug, thisKey)).GetProperty("usage_amount").GetDouble(), 4);
        // the month that was already billed keeps the plan that billed it
        Assert.Equal(2.2, Summary(await Bill(su, slug, lastKey)).GetProperty("usage_amount").GetDouble(), 4);

        // the same for an ASSIGNMENT change: moving the company onto another plan now
        var other = await Plan(su, new { label = $"Other {sfx}", slug = $"oth-{sfx}", mode = "markup", markup_pct = 400 });
        await Assign(su, other, new[] { company });
        Assert.Equal(10.0, Summary(await Bill(su, slug, thisKey)).GetProperty("usage_amount").GetDouble(), 4);
        Assert.Equal(2.2, Summary(await Bill(su, slug, lastKey)).GetProperty("usage_amount").GetDouble(), 4);

        // and unassigning it entirely falls back to the defaults, still only from this month
        await Assign(su, other, remove: new[] { company });
        Assert.Equal(2.0, Summary(await Bill(su, slug, thisKey)).GetProperty("usage_amount").GetDouble(), 4);
        Assert.Equal(2.2, Summary(await Bill(su, slug, lastKey)).GetProperty("usage_amount").GetDouble(), 4);

        // the history says so out loud
        var settings = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/settings");
        Assert.Contains(settings.GetProperty("history").EnumerateArray(),
            h => h.GetProperty("effective_from").GetString() == lastKey);
    }

    [Fact]
    public async Task Todays_settings_keep_producing_todays_bills()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var sfx = Sfx();
        var x = await Entity(su, $"pb-x-{sfx}");
        var y = await Entity(su, $"pb-y-{sfx}");
        Spend(x, 4);
        Spend(y, 2);
        try
        {
            // the pre-price-book way of pricing two clients, byte for byte
            var legacy = new
            {
                currency = "MYR", usd_rate = 4.5, default_markup_pct = 10, default_monthly_fee = 7,
                companies = new Dictionary<string, object>
                {
                    [x.ToString(CultureInfo.InvariantCulture)] = new { markup_pct = 50, monthly_fee = 100 },
                },
            };
            (await su.PutAsJsonAsync("/api/billing/ai/settings", legacy)).EnsureSuccessStatusCode();

            async Task AssertBaseline(string why)
            {
                var bx = Summary(await Bill(su, $"pb-x-{sfx}"));
                Assert.Equal(27.0, bx.GetProperty("usage_amount").GetDouble(), 4);   // 4 × 1.5 × 4.5
                Assert.Equal(100.0, bx.GetProperty("monthly_fee").GetDouble(), 4);
                Assert.Equal(127.0, bx.GetProperty("total").GetDouble(), 4);
                var by = Summary(await Bill(su, $"pb-y-{sfx}"));
                Assert.Equal(9.9, by.GetProperty("usage_amount").GetDouble(), 4);    // 2 × 1.1 × 4.5
                Assert.Equal(7.0, by.GetProperty("monthly_fee").GetDouble(), 4);
                Assert.True(by.GetProperty("total").GetDouble() > 0, why);
            }

            await AssertBaseline("baseline");

            // a plan that exists but prices nobody changes NOTHING
            var cheap = await Plan(su, new { label = $"Cheap {sfx}", slug = $"ch-{sfx}", mode = "markup", markup_pct = 0 });
            await AssertBaseline("a plan nobody is on");

            // saving the terms again keeps the plans (the snapshot is carried through)
            (await su.PutAsJsonAsync("/api/billing/ai/settings", legacy)).EnsureSuccessStatusCode();
            var settings = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/settings");
            Assert.Contains(settings.GetProperty("plans").EnumerateArray(),
                p => p.GetProperty("slug").GetString() == $"ch-{sfx}");
            await AssertBaseline("after re-saving the terms");

            // assigning it prices Y by the plan — and leaves X alone, because X's own
            // override sets both of its numbers explicitly
            await Assign(su, cheap, new[] { x, y });
            var bx = Summary(await Bill(su, $"pb-x-{sfx}"));
            Assert.Equal(27.0, bx.GetProperty("usage_amount").GetDouble(), 4);
            Assert.Equal(100.0, bx.GetProperty("monthly_fee").GetDouble(), 4);
            var by2 = Summary(await Bill(su, $"pb-y-{sfx}"));
            Assert.Equal(9.0, by2.GetProperty("usage_amount").GetDouble(), 4);       // 2 × 1.0 × 4.5
            Assert.Equal(0.0, by2.GetProperty("monthly_fee").GetDouble(), 4);
        }
        finally
        {
            await ResetTerms(su);
        }
    }

    // ---- bulk assignment, the list, and the gates ------------------------------------------

    [Fact]
    public async Task One_call_prices_many_entities_and_their_branches()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var sfx = Sfx();
        await ResetTerms(su);
        var many = new List<int>();
        for (var i = 0; i < 4; i++) many.Add(await Entity(su, $"pb-bulk{i}-{sfx}"));
        var branch = await Entity(su, $"pb-bulkb-{sfx}", $"pb-bulk0-{sfx}");
        var deep = await Entity(su, $"pb-bulkd-{sfx}", $"pb-bulkb-{sfx}");

        var plan = await Plan(su, new { label = $"Bulk {sfx}", slug = $"bk-{sfx}", mode = "markup", markup_pct = 30 });
        var applied = await Assign(su, plan, many.ToArray());
        Assert.Equal(4, applied.GetProperty("added").GetInt32());
        Assert.Equal(4, applied.GetProperty("member_ids").EnumerateArray().Count());

        // reading the binding back answers the same ids
        var read = await su.GetFromJsonAsync<JsonElement>($"/api/bindings/pricebook-entities?target={plan}");
        Assert.Equal(4, read.GetProperty("member_ids").EnumerateArray().Count());

        // a parent was priced, so every branch beneath it inherits — no second call
        var rows = await su.GetFromJsonAsync<JsonElement>(
            $"/api/billing/ai/price-books?scope=entities&q=pb-bulk&page_size=50");
        JsonElement Row(int id) => rows.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("id").GetInt32() == id);
        Assert.Equal($"bk-{sfx}", Row(branch).GetProperty("plan").GetString());
        Assert.True(Row(branch).GetProperty("inherited").GetBoolean());
        Assert.Equal(many[0], Row(branch).GetProperty("assigned_at").GetInt32());
        Assert.Equal($"bk-{sfx}", Row(deep).GetProperty("plan").GetString());         // two levels down
        Assert.True(Row(deep).GetProperty("inherited").GetBoolean());
        Assert.False(Row(many[0]).GetProperty("inherited").GetBoolean());
        Assert.True(Row(many[0]).GetProperty("assigned_here").GetBoolean());
        Assert.False(Row(branch).GetProperty("assigned_here").GetBoolean());

        // an entity carries ONE plan: assigning another MOVES it
        var second = await Plan(su, new { label = $"Bulk2 {sfx}", slug = $"bk2-{sfx}", mode = "markup", markup_pct = 60 });
        await Assign(su, second, new[] { many[0] });
        Assert.Equal(3, (await su.GetFromJsonAsync<JsonElement>($"/api/bindings/pricebook-entities?target={plan}"))
            .GetProperty("member_ids").EnumerateArray().Count());

        // every pricing change leaves its own activity-log row
        Assert.True(With(db => db.AuditLogs.Any(a => a.Action == "billing.pricebook.assign"
                                                     && a.Target == $"bk-{sfx}"
                                                     && a.Detail.Contains("assigned to 4"))));
        Assert.True(With(db => db.AuditLogs.Any(a => a.Action == "billing.pricebook" && a.Target == $"bk-{sfx}")));

        // pricing is the billing manager's: an admin may neither read nor change it
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.GetAsync($"/api/bindings/pricebook-entities?target={plan}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/bindings/pricebook-entities",
            new { target = plan, add = new[] { many[1] } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/billing/ai/price-books")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/billing/ai/price-books",
            new { label = "Nope", mode = "markup", markup_pct = 1 })).StatusCode);

        // an unknown plan or entity is refused, not silently ignored
        Assert.Equal(HttpStatusCode.NotFound, (await su.PostAsJsonAsync("/api/bindings/pricebook-entities",
            new { target = 999_999, add = new[] { many[1] } })).StatusCode);
    }

    [Fact]
    public async Task The_list_searches_paginates_and_shows_what_the_numbers_would_bill()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var sfx = Sfx();
        await ResetTerms(su);
        var ids = new List<int>();
        for (var i = 0; i < 5; i++) ids.Add(await Entity(su, $"pb-list{i}-{sfx}"));
        Spend(ids[0], 3, tokens: 1_000);

        var first = await su.GetFromJsonAsync<JsonElement>(
            $"/api/billing/ai/price-books?q=pb-list&page_size=2&page=1");
        Assert.Equal(5, first.GetProperty("total").GetInt32());
        Assert.Equal(2, first.GetProperty("rows").EnumerateArray().Count());
        Assert.Equal(2, first.GetProperty("page_size").GetInt32());
        var third = await su.GetFromJsonAsync<JsonElement>(
            $"/api/billing/ai/price-books?q=pb-list&page_size=2&page=3");
        Assert.Single(third.GetProperty("rows").EnumerateArray());

        // the operator no longer types blind: the row carries the month's real figures
        var row = (await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/price-books?q=pb-list0-{sfx}"))
            .GetProperty("rows").EnumerateArray().Single();
        var usage = row.GetProperty("usage");
        Assert.Equal(1, usage.GetProperty("requests").GetInt32());
        Assert.Equal(1000, usage.GetProperty("tokens").GetInt64());
        Assert.Equal(3, usage.GetProperty("cost_usd").GetDouble(), 4);
        Assert.Equal(3, usage.GetProperty("total").GetDouble(), 4);          // no markup, no fee yet
        Assert.Equal("builtin", row.GetProperty("plan_source").GetString());

        // "show only overrides" hides the hundred rows that inherit
        var plan = await Plan(su, new { label = $"List {sfx}", slug = $"ls-{sfx}", mode = "markup", markup_pct = 5 });
        await Assign(su, plan, new[] { ids[1] });
        var only = await su.GetFromJsonAsync<JsonElement>(
            $"/api/billing/ai/price-books?q=pb-list&overrides_only=true&page_size=50");
        Assert.Single(only.GetProperty("rows").EnumerateArray());
        Assert.Equal(ids[1], only.GetProperty("rows")[0].GetProperty("id").GetInt32());

        // the plan list says how many entities each plan prices
        Assert.Equal(1, only.GetProperty("plans").EnumerateArray()
            .Single(p => p.GetProperty("slug").GetString() == $"ls-{sfx}").GetProperty("entities").GetInt32());

        // a plan still pricing someone cannot be deleted by accident
        Assert.Equal(HttpStatusCode.BadRequest, (await su.DeleteAsync($"/api/billing/ai/price-books/ls-{sfx}")).StatusCode);
        await Assign(su, plan, remove: new[] { ids[1] });
        (await su.DeleteAsync($"/api/billing/ai/price-books/ls-{sfx}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await su.DeleteAsync($"/api/billing/ai/price-books/ls-{sfx}")).StatusCode);
    }

    [Fact]
    public async Task A_plan_is_validated_and_the_organization_default_is_the_last_fallback()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var sfx = Sfx();
        await ResetTerms(su);
        var company = await Entity(su, $"pb-def-{sfx}");
        Spend(company, 2);

        async Task<HttpStatusCode> Post(object body) =>
            (await su.PostAsJsonAsync("/api/billing/ai/price-books", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { label = "", mode = "markup" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { label = "Bad mode", mode = "guess" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { label = "No unit price", mode = "unit" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { label = "No flat", mode = "flat" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { label = "Too much", mode = "markup", markup_pct = 20000 }));

        try
        {
            var house = await Plan(su, new
            {
                label = $"House {sfx}", slug = $"ho-{sfx}", mode = "markup", markup_pct = 15, is_default = true,
            });
            // nothing is assigned: the organization default prices every company
            Assert.Equal(2.3, Summary(await Bill(su, $"pb-def-{sfx}")).GetProperty("usage_amount").GetDouble(), 4);
            var payload = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/price-books?q=pb-def-{sfx}");
            Assert.Equal($"ho-{sfx}", payload.GetProperty("default_plan").GetString());
            var row = payload.GetProperty("rows").EnumerateArray().Single();
            Assert.Equal("org_default_plan", row.GetProperty("plan_source").GetString());
            Assert.False(row.GetProperty("inherited").GetBoolean());        // nobody was assigned it

            // deactivating it drops back to the built-in defaults
            await Plan(su, new { slug = $"ho-{sfx}", label = $"House {sfx}", active = false });
            Assert.Equal(2.0, Summary(await Bill(su, $"pb-def-{sfx}")).GetProperty("usage_amount").GetDouble(), 4);
            // …and an inactive plan can no longer be assigned
            Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/bindings/pricebook-entities",
                new { target = house, add = new[] { company } })).StatusCode);
        }
        finally
        {
            await ResetTerms(su);
        }
    }

    [Fact]
    public async Task A_plan_grants_every_login_it_covers_an_allowance()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var sfx = Sfx();
        await ResetTerms(su);
        var company = await Entity(su, $"pb-all-{sfx}");
        var who = $"pb-user-{sfx}";
        (await su.PostAsJsonAsync("/api/v1/users", new
        {
            username = who, password = "allowance123", role = "agent", entity_ids = new[] { company },
        })).EnsureSuccessStatusCode();

        var plan = await Plan(su, new
        {
            label = $"Allowance {sfx}", slug = $"al-{sfx}", mode = "markup", markup_pct = 0, user_allowance = 5,
        });
        await Assign(su, plan, new[] { company });
        AiQuota.Invalidate();

        async Task<JsonElement> Row()
        {
            var quotas = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/quotas");
            return quotas.GetProperty("users").EnumerateArray()
                .Single(u => u.GetProperty("username").GetString() == who);
        }

        var fromPlan = await Row();
        Assert.Equal(5, fromPlan.GetProperty("cap").GetDouble(), 4);
        Assert.True(fromPlan.GetProperty("from_plan").GetBoolean());

        // a cap someone typed still wins over the plan's allowance
        (await su.PutAsJsonAsync("/api/billing/ai/quotas",
            new { users = new Dictionary<string, object> { [who] = 2 } })).EnsureSuccessStatusCode();
        AiQuota.Invalidate();
        var typed = await Row();
        Assert.Equal(2, typed.GetProperty("cap").GetDouble(), 4);
        Assert.False(typed.GetProperty("from_plan").GetBoolean());

        // and the allowance stops the person's own AI features once it is used up
        Spend(company, 6, feature: "playground");
        With(db =>
        {
            db.LlmUsage.OrderByDescending(u => u.Id).First().UserName = who;
            db.SaveChanges();
            return 0;
        });
        (await su.PutAsJsonAsync("/api/billing/ai/quotas",
            new { users = new Dictionary<string, object?> { [who] = null } })).EnsureSuccessStatusCode();
        AiQuota.Invalidate();
        var used = await Row();
        Assert.Equal(5, used.GetProperty("cap").GetDouble(), 4);
        Assert.Equal("paused", used.GetProperty("state").GetString());
    }

    // ---- the schema upgrade ----------------------------------------------------------------

    private static List<string> Objects(AppDbContext db, string type, string table)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type = '{type}' AND tbl_name = '{table}'";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    [Fact]
    public void Upgrading_an_existing_sqlite_database_adds_the_price_book_tables()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cb-pricebook-upgrade-{Guid.NewGuid():N}.sqlite3");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={file}").Options;
        try
        {
            using (var db = new AppDbContext(options))
            {
                db.Database.EnsureCreated();
                // the OLD schema: no price books at all
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_aipricebookassignment\"");
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_aipricebook\"");
                Assert.Empty(Objects(db, "table", "crm_aipricebook"));
            }

            using (var db = new AppDbContext(options)) Seed.Run(db);   // the boot after the deploy

            using (var db = new AppDbContext(options))
            {
                Assert.NotEmpty(Objects(db, "table", "crm_aipricebook"));
                Assert.NotEmpty(Objects(db, "table", "crm_aipricebookassignment"));
                Assert.Contains("uniq_aipricebook_slug", Objects(db, "index", "crm_aipricebook"));
                Assert.Contains("uniq_aipricebookassignment_entity", Objects(db, "index", "crm_aipricebookassignment"));
                Assert.Contains("ix_aipricebookassignment_book", Objects(db, "index", "crm_aipricebookassignment"));
                Seed.Run(db);   // a second boot changes nothing
            }

            using (var db = new AppDbContext(options))
            {
                // the upgraded tables really work, and the month's terms pick them up
                var tenant = db.Tenants.First();
                var entity = new Entity { TenantId = tenant.Id, Name = "Upgraded Co", Slug = "upgraded-pb-co" };
                db.Entities.Add(entity);
                db.AiPriceBooks.Add(new AiPriceBook
                {
                    Slug = "upgraded", Label = "Upgraded", Mode = AiPriceModes.Markup, MarkupPct = 33,
                    IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
                db.AiPriceBookAssignments.Add(new AiPriceBookAssignment
                {
                    PriceBookId = db.AiPriceBooks.Single(p => p.Slug == "upgraded").Id, EntityId = entity.Id,
                    Actor = "test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();

                var sc = new ScopingService(db);
                var terms = AiPriceBooks.SyncTerms(db, sc).TermsFor(AiBilling.MonthKey(DateTime.UtcNow));
                var price = AiPriceBooks.ResolveFor(sc, terms, entity.Id);
                Assert.Equal("upgraded", price.PlanSlug);
                Assert.Equal(33, price.MarkupPct);
            }
        }
        finally
        {
            foreach (var path in new[] { file, file + "-wal", file + "-shm" })
                try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
