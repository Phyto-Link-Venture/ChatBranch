using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Prepaid AI credit, phase 1: the append-only ledger is the truth, the account
/// row caches its signed sum, credit is held per COMPANY (a top-level entity —
/// a branch draws on its company's balance), a replayed grant credits once, and
/// only a billing manager hands credit out. Nothing debits a balance yet.
/// </summary>
public class AiCreditTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AiCreditTests(ApiFactory f) => _f = f;

    private T With<T>(Func<AppDbContext, T> act) => Platform.WithDb(_f, act);

    private static int _n;
    private static string Sfx() => $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];

    /// <summary>A company with one branch under it.</summary>
    private async Task<(int Company, string CompanySlug, int Branch, string BranchSlug)> Company(HttpClient su)
    {
        var sfx = Sfx();
        var company = $"credit-co-{sfx}";
        var branch = $"credit-br-{sfx}";
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = $"Credit Co {sfx}", slug = company })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = $"Credit Branch {sfx}", slug = branch, parent = company })).StatusCode);
        return With(db => (db.Entities.Single(e => e.Slug == company).Id, company,
            db.Entities.Single(e => e.Slug == branch).Id, branch));
    }

    private static JsonElement CompanyRow(JsonElement payload, int id) =>
        payload.GetProperty("companies").EnumerateArray()
            .Single(c => c.GetProperty("id").GetInt32() == id);

    // ---- a grant moves the balance and leaves a ledger line -------------------------------

    [Fact]
    public async Task A_grant_credits_the_company_and_writes_one_ledger_line()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _, _) = await Company(su);

        var res = await su.PostAsJsonAsync("/api/billing/ai/credit/grant",
            new { entity = slug, amount = 250.5, note = "opening balance" });
        res.EnsureSuccessStatusCode();
        var granted = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(granted.GetProperty("replayed").GetBoolean());
        Assert.Equal(250.5, granted.GetProperty("entry").GetProperty("balance_after").GetDouble(), 6);

        var payload = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit?company={slug}");
        var credit = CompanyRow(payload, company).GetProperty("credit");
        Assert.Equal(250.5, credit.GetProperty("balance").GetDouble(), 6);
        Assert.True(credit.GetProperty("opened").GetBoolean());
        Assert.True(credit.GetProperty("enabled").GetBoolean());
        Assert.False(credit.GetProperty("exhausted").GetBoolean());

        var lines = payload.GetProperty("ledger").EnumerateArray().ToList();
        Assert.Single(lines);
        Assert.Equal("grant", lines[0].GetProperty("kind").GetString());
        Assert.Equal(250.5, lines[0].GetProperty("amount").GetDouble(), 6);
        Assert.Equal("opening balance", lines[0].GetProperty("note").GetString());
        Assert.Equal("superadmin", lines[0].GetProperty("actor").GetString());

        // the activity log carries it
        var audit = await Platform.Audit(su, "action=billing.credit.grant");
        Assert.Contains(audit, a => (a.GetProperty("target").GetString() ?? "")
                                    == "entity:" + company.ToString(CultureInfo.InvariantCulture));

        // a signed correction is a NEW line, never an edit
        (await su.PostAsJsonAsync("/api/billing/ai/credit/adjust",
            new { entity = slug, amount = -50, note = "wrong company" })).EnsureSuccessStatusCode();
        payload = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit?company={slug}");
        Assert.Equal(200.5, CompanyRow(payload, company).GetProperty("credit").GetProperty("balance").GetDouble(), 6);
        Assert.Equal(2, payload.GetProperty("ledger").GetArrayLength());

        // balances are signed and never clamped: an over-correction goes negative
        (await su.PostAsJsonAsync("/api/billing/ai/credit/adjust",
            new { entity = slug, amount = -300.5, note = "clawback" })).EnsureSuccessStatusCode();
        payload = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit?company={slug}");
        var after = CompanyRow(payload, company).GetProperty("credit");
        Assert.Equal(-100.0, after.GetProperty("balance").GetDouble(), 6);
        Assert.True(after.GetProperty("exhausted").GetBoolean());
    }

    [Fact]
    public async Task Only_a_billing_manager_grants_credit()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var agent = await Platform.As(_f, "agent", "agent123");
        var (_, slug, _, _) = await Company(su);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/billing/ai/credit/grant",
            new { entity = slug, amount = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/billing/ai/credit/adjust",
            new { entity = slug, amount = -10 })).StatusCode);
        // billing.view reads, an agent does not
        (await admin.GetAsync("/api/billing/ai/credit")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/billing/ai/credit")).StatusCode);

        // the input guards
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/billing/ai/credit/grant",
            new { entity = slug, amount = -10 })).StatusCode);          // a grant is positive
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/billing/ai/credit/adjust",
            new { entity = slug, amount = 0 })).StatusCode);            // nothing to correct
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/billing/ai/credit/grant",
            new { entity = "no-such-company", amount = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/billing/ai/credit/grant",
            new { amount = 10 })).StatusCode);                          // which company?
    }

    // ---- idempotency ---------------------------------------------------------------------

    [Fact]
    public async Task A_replayed_grant_key_credits_once()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _, _) = await Company(su);
        var key = "test-grant-" + Guid.NewGuid().ToString("N");
        var body = new { entity = slug, amount = 40, note = "retried", idempotency_key = key };

        var first = await (await su.PostAsJsonAsync("/api/billing/ai/credit/grant", body))
            .Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await su.PostAsJsonAsync("/api/billing/ai/credit/grant", body))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(first.GetProperty("replayed").GetBoolean());
        Assert.True(second.GetProperty("replayed").GetBoolean());
        Assert.Equal(first.GetProperty("entry").GetProperty("id").GetInt32(),
            second.GetProperty("entry").GetProperty("id").GetInt32());

        var payload = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit?company={slug}");
        Assert.Equal(40.0, CompanyRow(payload, company).GetProperty("credit").GetProperty("balance").GetDouble(), 6);
        Assert.Equal(1, payload.GetProperty("ledger").GetArrayLength());
        Assert.Equal(1, With(db => db.AiCreditEntries.Count(e => e.IdempotencyKey == key)));

        // the database itself refuses a second line with the same key
        Assert.Throws<DbUpdateException>(() => With(db =>
        {
            db.AiCreditEntries.Add(new AiCreditEntry
            {
                Scope = AiCreditScopes.Entity, EntityId = company, Kind = AiCreditKinds.Grant,
                Amount = 1, Currency = "USD", IdempotencyKey = key, CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
            return 0;
        }));
    }

    // ---- reconciliation: the cached balance IS the ledger's sum ---------------------------

    [Fact]
    public async Task Every_balance_reconciles_with_its_ledger()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _, _) = await Company(su);
        // amounts that do not add up cleanly in binary floating point
        foreach (var amount in new[] { 0.1, 0.2, 10.07, 99.99 })
            (await su.PostAsJsonAsync("/api/billing/ai/credit/grant", new { entity = slug, amount }))
                .EnsureSuccessStatusCode();
        foreach (var amount in new[] { -0.3, -5.55 })
            (await su.PostAsJsonAsync("/api/billing/ai/credit/adjust", new { entity = slug, amount, note = "fix" }))
                .EnsureSuccessStatusCode();

        var (balance, sum, drift) = With(db =>
        {
            var account = db.AiCreditAccounts.AsNoTracking()
                .Single(a => a.Scope == AiCreditScopes.Entity && a.EntityId == company);
            var ledger = db.AiCreditEntries.AsNoTracking()
                .Where(e => e.EntityId == company).Sum(e => e.Amount);
            return (account.Balance, ledger, AiCredit.ReconcileAsync(db).GetAwaiter().GetResult());
        });
        Assert.Equal(sum, balance, AiCredit.Tolerance);
        Assert.Equal(104.51, balance, 6);   // 0.1 + 0.2 + 10.07 + 99.99 − 0.3 − 5.55
        // and NO account anywhere in the database drifts from its ledger
        Assert.Empty(drift);

        // every line records the balance it produced
        var last = With(db => db.AiCreditEntries.AsNoTracking()
            .Where(e => e.EntityId == company).OrderByDescending(e => e.Id).First());
        Assert.Equal(balance, last.BalanceAfter, AiCredit.Tolerance);
    }

    // ---- a branch draws on its company ---------------------------------------------------

    [Fact]
    public async Task A_branch_draws_on_its_companys_balance()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, branch, branchSlug) = await Company(su);
        (await su.PostAsJsonAsync("/api/billing/ai/credit/grant", new { entity = slug, amount = 75 }))
            .EnsureSuccessStatusCode();

        // asked about the BRANCH, the API answers with its company's balance
        var payload = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit?entity={branchSlug}");
        var resolved = payload.GetProperty("resolved");
        Assert.Equal(branch, resolved.GetProperty("entity_id").GetInt32());
        var draws = resolved.GetProperty("draws_on");
        Assert.Equal(company, draws.GetProperty("entity_id").GetInt32());
        Assert.Equal(75.0, draws.GetProperty("balance").GetDouble(), 6);

        // the branch is listed under its company, and has no account of its own
        Assert.Contains(CompanyRow(payload, company).GetProperty("branches").EnumerateArray(),
            b => b.GetProperty("id").GetInt32() == branch);
        Assert.Equal(0, With(db => db.AiCreditAccounts.Count(a => a.EntityId == branch)));

        // granting to a branch is refused, naming the company that holds the credit
        var bad = await su.PostAsJsonAsync("/api/billing/ai/credit/grant", new { entity = branchSlug, amount = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("branch", (await bad.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);

        // and the service agrees, without going through HTTP
        var status = With(db => AiCredit.Load(db, new ScopingService(db)).ForEntity(branch));
        Assert.NotNull(status);
        Assert.Equal(company, status!.EntityId);
        Assert.Equal(75.0, status.Balance, 6);
    }

    // ---- the optional per-login balance --------------------------------------------------

    [Fact]
    public async Task A_login_can_hold_its_own_balance()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var res = await su.PostAsJsonAsync("/api/billing/ai/credit/grant",
            new { user = "agent", amount = 12.25, note = "playground budget" });
        res.EnsureSuccessStatusCode();
        var granted = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user", granted.GetProperty("entry").GetProperty("scope").GetString());

        var payload = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/credit?user=agent");
        var row = payload.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("user_name").GetString() == "agent");
        Assert.True(row.GetProperty("balance").GetDouble() >= 12.25);
        Assert.Contains(payload.GetProperty("ledger").EnumerateArray(),
            l => l.GetProperty("user_name").GetString() == "agent" && l.GetProperty("scope").GetString() == "user");

        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/billing/ai/credit/grant",
            new { user = "nobody-at-all", amount = 1 })).StatusCode);
        // one thing at a time
        Assert.Equal(HttpStatusCode.BadRequest, (await su.PostAsJsonAsync("/api/billing/ai/credit/grant",
            new { user = "agent", entity = "1", amount = 1 })).StatusCode);
    }

    // ---- the same thing through the MCP tools --------------------------------------------

    [Fact]
    public async Task The_credit_tools_grant_and_read_over_mcp()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _, _) = await Company(su);
        var key = Platform.Client(_f, Platform.MintKey(_f, "superadmin"));

        var granted = await Platform.CallTool(key, "billing_grant_ai_credit",
            new { entity = slug, amount = 60, note = "over mcp" });
        Assert.False(granted.GetProperty("isError").GetBoolean(),
            granted.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(60.0, granted.GetProperty("structuredContent").GetProperty("balance").GetDouble(), 6);

        var read = await Platform.CallTool(key, "billing_get_ai_credit", new { company = slug });
        Assert.False(read.GetProperty("isError").GetBoolean());
        Assert.Equal(60.0, CompanyRow(read.GetProperty("structuredContent"), company)
            .GetProperty("credit").GetProperty("balance").GetDouble(), 6);

        var corrected = await Platform.CallTool(key, "billing_adjust_ai_credit",
            new { entity = slug, amount = -10, note = "fix" });
        Assert.False(corrected.GetProperty("isError").GetBoolean());
        Assert.Equal(50.0, corrected.GetProperty("structuredContent").GetProperty("balance").GetDouble(), 6);
    }

    // ---- the schema upgrade on an existing SQLite database --------------------------------

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
    public void Upgrading_an_existing_sqlite_database_adds_the_credit_tables()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cb-credit-upgrade-{Guid.NewGuid():N}.sqlite3");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={file}").Options;
        try
        {
            using (var db = new AppDbContext(options))
            {
                db.Database.EnsureCreated();
                // make it the OLD schema: no credit tables at all
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_aicreditentry\"");
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_aicreditaccount\"");
                Assert.Empty(Objects(db, "table", "crm_aicreditaccount"));
            }

            using (var db = new AppDbContext(options)) Seed.Run(db);   // the boot after the deploy

            using (var db = new AppDbContext(options))
            {
                Assert.NotEmpty(Objects(db, "table", "crm_aicreditaccount"));
                Assert.NotEmpty(Objects(db, "table", "crm_aicreditentry"));
                Assert.Contains("uniq_aicreditaccount_entity", Objects(db, "index", "crm_aicreditaccount"));
                Assert.Contains("uniq_aicreditaccount_user", Objects(db, "index", "crm_aicreditaccount"));
                Assert.Contains("uniq_aicreditentry_idem", Objects(db, "index", "crm_aicreditentry"));
                Assert.Contains("ix_aicreditentry_entity_created", Objects(db, "index", "crm_aicreditentry"));
                // a second boot is a no-op
                Seed.Run(db);
            }

            using (var db = new AppDbContext(options))
            {
                // the upgraded tables really work: EF's model matches the DDL
                var tenant = db.Tenants.First();
                var entity = new Entity { TenantId = tenant.Id, Name = "Upgraded Co", Slug = "upgraded-co" };
                db.Entities.Add(entity);
                db.SaveChanges();
                var sc = new ScopingService(db);
                var done = AiCredit.ApplyAsync(db, new AiCredit.Request(AiCreditScopes.Entity, entity.Id, "",
                    AiCreditKinds.Grant, 20, "USD", "after the upgrade", IdempotencyKey: "upgrade-once")).GetAwaiter().GetResult();
                Assert.True(done.Ok);
                Assert.Equal(20.0, done.Entry!.BalanceAfter, 6);
                // replayed: credited once
                var again = AiCredit.ApplyAsync(db, new AiCredit.Request(AiCreditScopes.Entity, entity.Id, "",
                    AiCreditKinds.Grant, 20, "USD", "replay", IdempotencyKey: "upgrade-once")).GetAwaiter().GetResult();
                Assert.True(again.Replayed);
                Assert.Equal(20.0, AiCredit.Load(db, sc).Company(entity.Id).Balance, 6);
                Assert.Empty(AiCredit.ReconcileAsync(db).GetAwaiter().GetResult());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }
}
