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
/// Prepaid AI credit, phase 2: the consumption sweep debits AI usage into the
/// ledger (priced like the bill, attributed like the bill, crash-safe, never
/// rounding replies to free), and a company ON CREDIT whose balance is used up
/// has its AI paused through the one eligibility funnel — with a handover, a
/// visible reason and fail-open-loudly on a read failure. A company with no
/// account, or with credit switched off, is never touched: that is the deploy-
/// safety property and the first test.
/// </summary>
public class AiCreditConsumptionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AiCreditConsumptionTests(ApiFactory f) => _f = f;

    private T With<T>(Func<AppDbContext, T> act) => Platform.WithDb(_f, act);

    private static int _n;
    private static string Sfx() => $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];

    private async Task<(int Company, string Slug, int Branch)> Company(HttpClient su, string tag = "cons")
    {
        var sfx = Sfx();
        var company = $"{tag}-co-{sfx}";
        var branch = $"{tag}-br-{sfx}";
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = $"{tag} Co {sfx}", slug = company })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = $"{tag} Branch {sfx}", slug = branch, parent = company })).StatusCode);
        return With(db => (db.Entities.Single(e => e.Slug == company).Id, company,
            db.Entities.Single(e => e.Slug == branch).Id));
    }

    private static async Task Grant(HttpClient su, object body) =>
        (await su.PostAsJsonAsync("/api/billing/ai/credit/grant", body)).EnsureSuccessStatusCode();

    /// <summary>A usage row with a provider-reported cost (USD), created now.</summary>
    private int Usage(double usd, int? entityId = null, string user = "", int? contactId = null, string feature = "chat_reply") =>
        With(db =>
        {
            var u = new LlmUsage
            {
                Feature = feature, Model = "gpt-4o", EntityId = entityId, ContactId = contactId, UserName = user,
                PromptTokens = 10, CompletionTokens = 5, CostUsd = usd, CreatedAt = DateTime.UtcNow,
            };
            db.LlmUsage.Add(u);
            db.SaveChanges();
            return u.Id;
        });

    /// <summary>One pass, with the clock a minute ahead so just-written rows are settled.</summary>
    private AiCreditSweep.Outcome Sweep() => With(db => AiCreditSweep.Run(db, DateTime.UtcNow.AddMinutes(1)));

    /// <summary>Make sure the cursor exists and has passed every row written so far.</summary>
    private void Prime()
    {
        var o = Sweep();
        if (o.Status == AiCreditSweep.Outcome.Started) Sweep();
    }

    private double Balance(int company) =>
        With(db => db.AiCreditAccounts.AsNoTracking().Single(a => a.Scope == AiCreditScopes.Entity && a.EntityId == company).Balance);

    private List<AiCreditEntry> Consumption(int? company = null, string? user = null) =>
        With(db => db.AiCreditEntries.AsNoTracking().Where(e => e.Kind == AiCreditKinds.Consumption
                && (company == null || e.EntityId == company) && (user == null || e.UserName == user))
            .OrderBy(e => e.Id).ToList());

    private void AssertReconciles() => Assert.Empty(With(db => AiCredit.ReconcileAsync(db).GetAwaiter().GetResult()));

    private Domain.Thread EmailThread(int entityId, string email) => With(db =>
    {
        var c = new Contact { Name = email, Email = email, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Contacts.Add(c);
        db.SaveChanges();
        db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = entityId });
        var t = new Domain.Thread
        {
            Channel = "email", Key = email, Name = email, ContactId = c.Id, EntityId = entityId, AiEnabled = true,
            AiOverrides = new Dictionary<string, object?> { ["thread"] = true },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Threads.Add(t);
        db.SaveChanges();
        return t;
    });

    private AiVerdict Verdict(int threadId) => With(db =>
    {
        var t = db.Threads.AsNoTracking().Single(x => x.Id == threadId);
        return AiEligibility.Decide(db, new ScopingService(db), t);
    });

    /// <summary>The superadmin's notifications under this RefKey prefix (one row per recipient).</summary>
    private int CreditNotifications(string refKeyPrefix) =>
        With(db =>
        {
            var su = db.Users.Single(u => u.Username == "superadmin").Id;
            return db.Notifications.AsNoTracking().Count(n => n.UserId == su && n.RefKey.StartsWith(refKeyPrefix));
        });

    // ---- THE deploy-safety property --------------------------------------------------------------

    [Fact]
    public async Task A_company_with_no_account_or_credit_switched_off_is_never_debited_blocked_or_warned()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (none, _, noneBranch) = await Company(su, "nocredit");
        var (off, offSlug, offBranch) = await Company(su, "offcredit");
        // "off": it HAS an account, already below zero, but credit is switched off for it
        await Grant(su, new { entity = offSlug, amount = 1 });
        (await su.PostAsJsonAsync("/api/billing/ai/credit/adjust", new { entity = offSlug, amount = -6 })).EnsureSuccessStatusCode();
        var put = await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = offSlug, enabled = false });
        put.EnsureSuccessStatusCode();
        Assert.False((await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credit").GetProperty("enabled").GetBoolean());
        var offBefore = Balance(off);
        int Notes() => CreditNotifications($"ai-credit:entity:{none}:") + CreditNotifications($"ai-credit:entity:{off}:");
        var notesBefore = Notes();

        Prime();
        Usage(500, none);
        Usage(500, noneBranch);
        Usage(500, off);
        Usage(500, offBranch);
        Sweep();
        Sweep();

        // never debited: no account appears out of nowhere, the disabled balance does not move
        Assert.False(With(db => db.AiCreditAccounts.Any(a => a.EntityId == none)));
        Assert.Empty(Consumption(none));
        Assert.Empty(Consumption(off));
        Assert.Equal(offBefore, Balance(off), 6);

        // never blocked: not by the funnel, not by the guard, not by the inbound path
        var tNone = EmailThread(noneBranch, $"none-{Sfx()}@credit.test");
        var tOff = EmailThread(offBranch, $"off-{Sfx()}@credit.test");
        AiCreditGuard.Invalidate();
        Assert.NotEqual(AiRules.Credit, Verdict(tNone.Id).Rule);
        Assert.NotEqual(AiRules.Credit, Verdict(tOff.Id).Rule);
        Assert.True(Verdict(tNone.Id).Enabled);
        Assert.True(Verdict(tOff.Id).Enabled);
        Assert.Null(With(db => AiCreditGuard.EntityBlock(db, new ScopingService(db), noneBranch)));
        Assert.Null(With(db => AiCreditGuard.EntityBlock(db, new ScopingService(db), offBranch)));
        var admin = await Platform.As(_f, "admin", "admin123");
        (await admin.PostAsJsonAsync("/api/whatsapp/simulate?ai=1",
            new { channel = "email", key = tOff.Key, text = "hello?" })).EnsureSuccessStatusCode();
        Assert.NotEqual(HandoverKinds.Credit, With(db => db.Threads.AsNoTracking().Single(x => x.Id == tOff.Id).HandoverKind));

        // never warned
        Assert.Equal(notesBefore, Notes());
        Assert.Equal(0, notesBefore);

        // switched back on, the (negative) balance governs again — the switch is real
        (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = offSlug, enabled = true })).EnsureSuccessStatusCode();
        Assert.Equal(AiRules.Credit, Verdict(tOff.Id).Rule);
        (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = offSlug, enabled = false })).EnsureSuccessStatusCode();
        Assert.True(Verdict(tOff.Id).Enabled);

        var audit = await Platform.Audit(su, "action=billing.credit.account");
        Assert.Contains(audit, a => (a.GetProperty("target").GetString() ?? "") == "entity:" + off.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_first_pass_starts_at_the_newest_usage_and_never_backfills()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _) = await Company(su, "firstrun");
        await Grant(su, new { entity = slug, amount = 100 });
        // usage written after the grant but before metering ever ran
        With(db => db.Settings.Where(s => s.Key == AiCreditSweep.CursorKey).ExecuteDelete());
        Usage(3, company);
        var first = Sweep();
        Assert.Equal(AiCreditSweep.Outcome.Started, first.Status);
        Sweep();
        Assert.Empty(Consumption(company));
        Assert.Equal(100, Balance(company), 6);

        Usage(2, company);
        Sweep();
        Assert.Equal(98, Balance(company), 6);
        AssertReconciles();
    }

    // ---- debiting --------------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_debits_the_branchs_company_at_the_price_book_price_and_matches_the_bill()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, branch) = await Company(su, "priced");
        var contact = With(db =>
        {
            var c = new Contact { Name = "Priced", Phone = "+60100007781", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = branch });
            db.SaveChanges();
            return c.Id;
        });
        // this company is priced at a 50 % markup (a per-company override)
        (await su.PutAsJsonAsync("/api/billing/ai/settings", new
        {
            currency = "USD", usd_rate = 1, default_markup_pct = 0,
            companies = new Dictionary<string, object> { [company.ToString(CultureInfo.InvariantCulture)] = new { markup_pct = 50 } },
        })).EnsureSuccessStatusCode();
        try
        {
            await Grant(su, new { entity = slug, amount = 100 });
            Prime();
            Usage(2.0, branch);                  // a branch's usage → its company
            Usage(1.0, company);
            Usage(0.5, contactId: contact);      // no entity: the contact's company (the bill's fallback)
            Usage(4.0);                          // unattributed: never debited to anyone
            var o = Sweep();
            Assert.Equal(AiCreditSweep.Outcome.Done, o.Status);

            var lines = Consumption(company);
            Assert.Single(lines);                // one line per company per batch
            Assert.Equal(-(3.5 * 1.5), lines[0].Amount, 6);
            Assert.Equal(100 - 5.25, Balance(company), 6);
            Assert.StartsWith("llmusage:", lines[0].Ref);
            Assert.Equal("system", lines[0].Actor);
            AssertReconciles();

            // the debit IS the bill's usage amount for this company
            var bill = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai?company={company}");
            var usage = bill.GetProperty("company").GetProperty("summary").GetProperty("usage_amount").GetDouble();
            Assert.Equal(usage, -Consumption(company).Sum(l => l.Amount), 4);

            // the credit read shows it
            var credit = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit/consumption?company={slug}");
            var row = credit.GetProperty("companies").EnumerateArray().Single();
            Assert.Equal(5.25, row.GetProperty("used_24h").GetDouble(), 6);
            Assert.False(row.GetProperty("paused").GetBoolean());
            Assert.Single(row.GetProperty("recent").EnumerateArray());
            Assert.True(credit.GetProperty("metering").GetProperty("last_usage_id").GetInt32() > 0);
        }
        finally
        {
            (await su.PutAsJsonAsync("/api/billing/ai/settings",
                new { currency = "USD", usd_rate = 1, default_markup_pct = 0, companies = new Dictionary<string, object>() }))
                .EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Many_tiny_replies_sum_to_a_real_debit_instead_of_rounding_to_free()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _) = await Company(su, "tiny");
        await Grant(su, new { entity = slug, amount = 1 });
        Prime();
        // 1,000 replies of USD 0.0000004 each: every one is 0 at 2 dp AND at 6 dp
        With(db =>
        {
            for (var i = 0; i < 1000; i++)
                db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "gpt-4o", EntityId = company,
                    CostUsd = 0.0000004, CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
            return 0;
        });
        Sweep();
        var debited = -Consumption(company).Sum(l => l.Amount);
        Assert.Equal(0.0004, debited, 9);
        Assert.Equal(1 - 0.0004, Balance(company), 9);
        AssertReconciles();
    }

    [Fact]
    public async Task A_crash_mid_sweep_then_a_rerun_never_double_debits_and_lab_deletions_do_not_confuse_the_cursor()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (a, aSlug, _) = await Company(su, "crasha");
        var (b, bSlug, _) = await Company(su, "crashb");
        await Grant(su, new { entity = aSlug, amount = 100 });
        await Grant(su, new { entity = bSlug, amount = 100 });
        Prime();
        Usage(1, a);
        Usage(2, b);
        Usage(4, a);

        // die right after the first ledger line of the batch
        With(db => { AiCreditSweep.SetCrashAfterForTests(db, 1); return 0; });
        try { Assert.Equal(AiCreditSweep.Outcome.Failed, Sweep().Status); }
        finally { With(db => { AiCreditSweep.SetCrashAfterForTests(db, null); return 0; }); }
        Assert.Single(With(db => db.AiCreditEntries.AsNoTracking()
            .Where(e => e.Kind == AiCreditKinds.Consumption && (e.EntityId == a || e.EntityId == b)).ToList()));
        var pinned = With(db => AiCreditSweep.ReadCursor(db));
        Assert.NotNull(pinned!.PendingTo);   // the range stayed pinned
        Assert.Null(pinned.Lease);           // and the lease was let go

        // more usage lands before the rerun — it must not change the replayed batch's keys
        Usage(8, a);
        Usage(16, b);
        Sweep();
        Sweep();

        Assert.Equal(100 - (1 + 4 + 8), Balance(a), 6);
        Assert.Equal(100 - (2 + 16), Balance(b), 6);
        Assert.Equal(2, Consumption(a).Count);   // batch 1 (replayed, not duplicated) + batch 2
        Assert.Equal(2, Consumption(b).Count);
        AssertReconciles();
        Assert.Null(With(db => AiCreditSweep.ReadCursor(db))!.PendingTo);

        // the test lab deletes usage rows behind the cursor (LabReset): nothing re-debits,
        // nothing stalls, the next row is metered
        With(db => db.LlmUsage.Where(u => u.EntityId == a || u.EntityId == b).ExecuteDelete());
        Sweep();
        Usage(0.5, a);
        Sweep();
        Assert.Equal(100 - (1 + 4 + 8 + 0.5), Balance(a), 6);
        Assert.Equal(100 - (2 + 16), Balance(b), 6);
        AssertReconciles();
    }

    [Fact]
    public async Task A_per_login_allowance_is_debited_only_for_usage_carrying_that_username()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _) = await Company(su, "peruser");
        await Grant(su, new { user = "agent", amount = 1 });
        await Grant(su, new { entity = slug, amount = 10 });
        var agentBefore = With(db => db.AiCreditAccounts.AsNoTracking().Single(x => x.Scope == AiCreditScopes.User && x.UserName == "agent").Balance);
        Prime();
        Usage(0.3, user: "agent", feature: "playground");      // the agent's own run, no company
        Usage(0.5, user: "admin", feature: "playground");      // someone without a per-login account
        Usage(0.7, company);                                    // automated work: UserName ""
        Usage(0.2, company, user: "Agent", feature: "summary"); // the agent, inside a company on credit
        Sweep();

        var agentLines = Consumption(user: "agent");
        Assert.Single(agentLines);
        Assert.Equal(-(0.3 + 0.2), agentLines[0].Amount, 6);
        Assert.Equal(agentBefore - 0.5, With(db => db.AiCreditAccounts.AsNoTracking()
            .Single(x => x.Scope == AiCreditScopes.User && x.UserName == "agent").Balance), 6);
        Assert.False(With(db => db.AiCreditAccounts.Any(x => x.Scope == AiCreditScopes.User && x.UserName == "admin")));
        // the company pays for everything attributed to it, including the agent's summary
        Assert.Equal(10 - (0.7 + 0.2), Balance(company), 6);
        AssertReconciles();

        // used up: the agent's interactive AI is refused with a credit reason, an admin's is not
        Usage(5, user: "agent", feature: "playground");
        Sweep();
        var agent = await Platform.As(_f, "agent", "agent123");
        var res = await agent.PostAsJsonAsync("/api/ai/playground/reply", new
        {
            channel = "whatsapp", profile = new { name = "Test" },
            messages = new[] { new { role = "customer", text = "hi" } },
        });
        Assert.Equal((HttpStatusCode)429, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("credit").GetBoolean());
        Assert.StartsWith("Your AI credit is used up", body.GetProperty("detail").GetString());

        // a top-up for the person lifts it
        await Grant(su, new { user = "agent", amount = 50 });
        var again = await agent.PostAsJsonAsync("/api/ai/playground/reply", new
        {
            channel = "whatsapp", profile = new { name = "Test" },
            messages = new[] { new { role = "customer", text = "hi" } },
        });
        Assert.NotEqual((HttpStatusCode)429, again.StatusCode);
        // leave the shared "agent" login without an enforced balance for the other tests
        (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { user = "agent", enabled = false })).EnsureSuccessStatusCode();
    }

    // ---- enforcement -----------------------------------------------------------------------------

    [Fact]
    public async Task Out_of_credit_pauses_the_ai_with_a_credit_reason_and_a_handover_and_a_grant_resumes_it()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var (company, slug, branch) = await Company(su, "paused");
        var thread = EmailThread(branch, $"paused-{Sfx()}@credit.test");
        await Grant(su, new { entity = slug, amount = 1 });
        Prime();
        Usage(1.25, branch);
        Sweep();
        Assert.Equal(-0.25, Balance(company), 6);   // signed: the overshoot is kept, never clamped

        var v = Verdict(thread.Id);
        Assert.False(v.Enabled);
        Assert.Equal(AiRules.Credit, v.Rule);
        Assert.True(v.Forced);
        Assert.StartsWith("Off: AI credit used up for paused Co", v.Reason);

        // a customer writes: the chat is handed to a person (once), the AI stays silent,
        // and the chatbot tick is kept so the AI resumes by itself after a top-up
        (await admin.PostAsJsonAsync("/api/whatsapp/simulate?ai=1",
            new { channel = "email", key = thread.Key, text = "hello?" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/whatsapp/simulate?ai=1",
            new { channel = "email", key = thread.Key, text = "anyone?" })).EnsureSuccessStatusCode();
        var t = With(db => db.Threads.AsNoTracking().Single(x => x.Id == thread.Id));
        Assert.Equal(HandoverKinds.Credit, t.HandoverKind);
        Assert.True(Handover.IsOpen(t));
        Assert.Equal(1, t.HandoverCount);
        Assert.True(t.AiEnabled);
        Assert.Contains("AI credit used up", t.HandoverReason);
        Assert.Equal(0, With(db => db.Messages.Count(m => m.ThreadId == thread.Id && m.Source == MessageSource.Ai)));

        // the out-of-credit warning went to the admins, once
        Sweep();
        Assert.Equal(1, CreditNotifications($"ai-credit:entity:{company}:out"));

        // an overdraft lets it run a little below zero
        (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, overdraft_limit = 1 })).EnsureSuccessStatusCode();
        Assert.True(Verdict(thread.Id).Enabled);
        (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, overdraft_limit = 0 })).EnsureSuccessStatusCode();
        Assert.Equal(AiRules.Credit, Verdict(thread.Id).Rule);

        // the payloads say it honestly
        var consumption = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit/consumption?company={slug}");
        var row = consumption.GetProperty("companies").EnumerateArray().Single();
        Assert.True(row.GetProperty("paused").GetBoolean());
        Assert.Contains("AI credit used up", row.GetProperty("paused_reason").GetString());

        // a grant unblocks on the next message — no cache wait
        await Grant(su, new { entity = slug, amount = 5 });
        var after = Verdict(thread.Id);
        Assert.True(after.Enabled);
        Assert.NotEqual(AiRules.Credit, after.Rule);
        AssertReconciles();
    }

    [Fact]
    public async Task A_credit_read_failure_lets_the_ai_answer_and_raises_an_admin_notification()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, branch) = await Company(su, "failopen");
        var thread = EmailThread(branch, $"failopen-{Sfx()}@credit.test");
        await Grant(su, new { entity = slug, amount = 1 });
        (await su.PostAsJsonAsync("/api/billing/ai/credit/adjust", new { entity = slug, amount = -3 })).EnsureSuccessStatusCode();
        Assert.Equal(AiRules.Credit, Verdict(thread.Id).Rule);    // genuinely out of credit

        var before = With(db => AiCreditGuard.HealthOf(db).FailOpen);
        With(db => { AiCreditGuard.SetFaultForTests(db, new InvalidOperationException("credit table unreadable")); return 0; });
        try
        {
            var v = Verdict(thread.Id);
            Assert.True(v.Enabled);                               // fail OPEN: the AI keeps answering
            Assert.NotEqual(AiRules.Credit, v.Rule);
            Assert.NotEqual(AiRules.Error, v.Rule);
            Assert.True(With(db => AiCreditGuard.HealthOf(db).FailOpen) > before);
            // ... loudly: the platform admins are told, in one collapsing notification
            var note = With(db => db.Notifications.AsNoTracking()
                .Where(n => n.RefKey == "ai-credit:fail-open").OrderByDescending(n => n.Id).First());
            Assert.Contains("could not be read", note.Body);
            Assert.Contains("credit table unreadable", note.Body);
            var superadminId = With(db => db.Users.Single(u => u.Username == "superadmin").Id);
            Assert.True(With(db => db.Notifications.Any(n => n.RefKey == "ai-credit:fail-open" && n.UserId == superadminId)));
            // a second failure refreshes it rather than stacking a new row
            var rows = With(db => db.Notifications.Count(n => n.RefKey == "ai-credit:fail-open" && n.UserId == superadminId && !n.Read));
            Verdict(thread.Id);
            Assert.Equal(rows, With(db => db.Notifications.Count(n => n.RefKey == "ai-credit:fail-open" && n.UserId == superadminId && !n.Read)));
        }
        finally
        {
            With(db => { AiCreditGuard.SetFaultForTests(db, null); return 0; });
        }
        Assert.Equal(AiRules.Credit, Verdict(thread.Id).Rule);
        (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, enabled = false })).EnsureSuccessStatusCode();
        _ = company;
    }

    // ---- warnings + metering health ----------------------------------------------------------------

    [Fact]
    public async Task Low_and_out_warnings_are_sent_once_and_rearm_after_a_top_up()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _) = await Company(su, "lowbal");
        await Grant(su, new { entity = slug, amount = 10 });   // derived warning level: 10 % of the last credit = 1
        Prime();
        Usage(9.5, company);
        Sweep();
        Sweep();
        Assert.Equal(1, CreditNotifications($"ai-credit:entity:{company}:low"));
        Assert.Equal(0, CreditNotifications($"ai-credit:entity:{company}:out"));

        Usage(1, company);
        Sweep();
        Sweep();
        Assert.Equal(1, CreditNotifications($"ai-credit:entity:{company}:out"));

        // a top-up recovers it; running low again warns again (the low row is refreshed)
        await Grant(su, new { entity = slug, amount = 100 });
        Sweep();
        With(db => db.Notifications.Where(n => n.RefKey.StartsWith($"ai-credit:entity:{company}:")).ExecuteUpdate(
            s => s.SetProperty(n => n.Read, true)));
        Usage(95, company);
        Sweep();
        var suId = With(db => db.Users.Single(u => u.Username == "superadmin").Id);
        Assert.Equal(1, With(db => db.Notifications.Count(n => n.UserId == suId
                                                              && n.RefKey == $"ai-credit:entity:{company}:low" && !n.Read)));

        // an explicit warning level wins over the derived one
        (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, low_balance_threshold = 2.5 })).EnsureSuccessStatusCode();
        var payload = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai/credit/consumption?company={slug}");
        Assert.Equal(2.5, payload.GetProperty("companies")[0].GetProperty("warn_at").GetDouble(), 6);
        (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, enabled = false })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_swallowed_usage_write_is_counted_and_surfaced_to_the_admins()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var before = With(db => AiCreditGuard.HealthOf(db).MeteringFailures);
        With(db => { AiCreditGuard.MeteringFailed(db, "chat_reply", new InvalidOperationException("disk full")); return 0; });
        Assert.Equal(before + 1, With(db => AiCreditGuard.HealthOf(db).MeteringFailures));
        Sweep();
        var note = With(db => db.Notifications.AsNoTracking().Where(n => n.RefKey == "ai-credit:metering-failed")
            .OrderByDescending(n => n.Id).First());
        Assert.Contains("disk full", note.Body);
        var payload = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/credit/consumption");
        Assert.True(payload.GetProperty("metering").GetProperty("usage_write_failures").GetInt64() >= 1);
    }

    [Fact]
    public async Task Setting_only_a_warning_level_on_a_never_granted_company_never_pauses_it()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, branch) = await Company(su, "warnonly");
        var thread = EmailThread(branch, $"warnonly-{Sfx()}@credit.test");
        var res = await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, low_balance_threshold = 5 });
        res.EnsureSuccessStatusCode();
        var credit = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credit");
        Assert.False(credit.GetProperty("enabled").GetBoolean());   // opened OFF: balance 0 would be "used up"
        Assert.False(credit.GetProperty("exhausted").GetBoolean());
        Assert.True(Verdict(thread.Id).Enabled);
        Sweep();
        Assert.Equal(0, CreditNotifications($"ai-credit:entity:{company}:"));
    }

    [Fact]
    public async Task Interactive_ai_is_refused_when_every_company_of_the_login_is_out_of_credit_never_for_an_unbound_superadmin()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _) = await Company(su, "guardco");
        var username = $"guardadmin{Sfx()}";
        var created = await su.PostAsJsonAsync("/api/v1/users",
            new { username, password = "password123", role = "admin", entity_ids = new[] { company } });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var bound = await Platform.As(_f, username, "password123");
        await Grant(su, new { entity = slug, amount = 1 });
        (await su.PostAsJsonAsync("/api/billing/ai/credit/adjust", new { entity = slug, amount = -2 })).EnsureSuccessStatusCode();

        object Playground() => new
        {
            channel = "whatsapp", profile = new { name = "Test" },
            messages = new[] { new { role = "customer", text = "hi" } },
        };
        var refused = await bound.PostAsJsonAsync("/api/ai/playground/reply", Playground());
        Assert.Equal((HttpStatusCode)429, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("credit").GetBoolean());
        Assert.Contains("is out of AI credit", body.GetProperty("detail").GetString());

        // the unbound superadmin is never locked out by a company's credit
        Assert.NotEqual((HttpStatusCode)429, (await su.PostAsJsonAsync("/api/ai/playground/reply", Playground())).StatusCode);

        // credit switched off for that company: the bound login is not refused either
        (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, enabled = false })).EnsureSuccessStatusCode();
        Assert.NotEqual((HttpStatusCode)429, (await bound.PostAsJsonAsync("/api/ai/playground/reply", Playground())).StatusCode);
    }

    [Fact]
    public async Task A_row_with_an_unknown_model_and_no_provider_cost_is_counted_not_silently_free()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, slug, _) = await Company(su, "unpriced");
        await Grant(su, new { entity = slug, amount = 10 });
        Prime();
        var before = With(db => AiCreditGuard.HealthOf(db).UnpricedRows);
        With(db =>
        {
            db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "no-such-model-xyz", EntityId = company,
                PromptTokens = 1000, CompletionTokens = 500, CostUsd = null, CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
            return 0;
        });
        var o = Sweep();
        Assert.Equal(before + 1, With(db => AiCreditGuard.HealthOf(db).UnpricedRows));
        Assert.Contains(o.Errors, e => e.Contains("had no price"));
        Assert.Equal(10, Balance(company), 6);   // debited as 0 — and now visible as such
        var payload = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/credit/consumption");
        Assert.True(payload.GetProperty("metering").GetProperty("unpriced_rows").GetInt64() >= 1);
    }

    [Fact]
    public async Task The_account_switch_is_for_billing_managers_and_validates()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var (_, slug, branch) = await Company(su, "acctsw");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, overdraft_limit = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = branch.ToString(CultureInfo.InvariantCulture), enabled = true })).StatusCode);
        // switching credit OFF before any grant opens a zero, unenforced balance
        var res = await su.PutAsJsonAsync("/api/billing/ai/credit/account", new { entity = slug, enabled = false });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("created").GetBoolean());
        Assert.False(body.GetProperty("credit").GetProperty("exhausted").GetBoolean());
        AssertReconciles();
    }
}
