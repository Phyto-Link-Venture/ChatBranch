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
/// AI usage quotas: month-to-date billed spend per company / branch / user, the
/// AI switched off (rule "quota") for a conversation whose branch or company is
/// at its cap, a tracked handover of kind quota on inbound, the user-side
/// refusal of the playground and the assistant, the PUT's permission tiers and
/// the 80 % / 100 % warnings (once each per month). Default billing terms here
/// (USD, rate 1, no markup) — so billed = cost.
/// </summary>
public class AiQuotaTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AiQuotaTests(ApiFactory f) => _f = f;

    private T With<T>(Func<AppDbContext, T> act) => SettingsTestKit.WithDb(_f, act);

    private async Task<(int Company, int Branch)> CompanyWithBranch(HttpClient su, string slug)
    {
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = $"{slug} Co", slug })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = $"{slug} Branch", slug = $"{slug}-branch", parent = slug })).StatusCode);
        return With(db => (db.Entities.Single(e => e.Slug == slug).Id, db.Entities.Single(e => e.Slug == $"{slug}-branch").Id));
    }

    /// <summary>A usage row of exactly <paramref name="usd"/> (a provider-reported cost) this month.</summary>
    private void Spend(double usd, int? entityId = null, string user = "", int? contactId = null)
    {
        With(db =>
        {
            db.LlmUsage.Add(new LlmUsage
            {
                Feature = user.Length > 0 ? "playground" : "chat_reply", Model = "gpt-4o", EntityId = entityId,
                ContactId = contactId, UserName = user, PromptTokens = 10, CostUsd = usd, CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
            return 0;
        });
        AiQuota.Invalidate();
    }

    private static Task<HttpResponseMessage> PutQuotas(HttpClient c, object body) =>
        c.PutAsJsonAsync("/api/billing/ai/quotas", body);

    [Fact]
    public async Task Spend_is_billed_usage_per_company_branch_and_user()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, branch) = await CompanyWithBranch(su, "qspend");
        var contact = With(db =>
        {
            var c = new Contact { Name = "Q Spend", Phone = "+60100000881", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = branch });
            db.SaveChanges();
            return c.Id;
        });
        Spend(2.0, branch);
        Spend(1.0, company);
        Spend(0.5, contactId: contact);                // the bill's contact → company fallback
        Spend(0.25, user: "admin");                    // a person's own playground run
        With(db =>
        {
            // last month never counts
            db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "gpt-4o", EntityId = branch, CostUsd = 9,
                CreatedAt = DateTime.UtcNow.AddMonths(-1) });
            db.SaveChanges();
            return 0;
        });

        (await PutQuotas(su, new
        {
            entities = new Dictionary<string, object> { [company.ToString()] = 100, [branch.ToString()] = 10 },
            users = new Dictionary<string, object> { ["admin"] = 1 },
        })).EnsureSuccessStatusCode();
        try
        {
            var q = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/quotas");
            var rows = q.GetProperty("entities").EnumerateArray().ToList();
            var co = rows.Single(r => r.GetProperty("id").GetInt32() == company);
            var br = rows.Single(r => r.GetProperty("id").GetInt32() == branch);
            Assert.Equal(3.5, co.GetProperty("spent").GetDouble(), 3);    // 2 + 1 + 0.5 (contact fallback)
            Assert.Equal(2.0, br.GetProperty("spent").GetDouble(), 3);    // its own rows only
            Assert.Equal(20.0, br.GetProperty("pct").GetDouble(), 1);
            Assert.Equal("ok", br.GetProperty("state").GetString());
            Assert.Equal(1, br.GetProperty("depth").GetInt32());
            var admin = q.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("username").GetString() == "admin");
            Assert.Equal(0.25, admin.GetProperty("spent").GetDouble(), 3);
            Assert.Equal(25.0, admin.GetProperty("pct").GetDouble(), 1);

            // the company cap counts exactly what the bill bills
            var bill = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai?company={company}");
            Assert.Equal(3.5, bill.GetProperty("company").GetProperty("summary").GetProperty("usage_amount").GetDouble(), 3);
        }
        finally
        {
            await PutQuotas(su, new
            {
                entities = new Dictionary<string, object?> { [company.ToString()] = null, [branch.ToString()] = 0 },
                users = new Dictionary<string, object?> { ["admin"] = null },
            });
        }
    }

    [Fact]
    public async Task A_branch_or_its_company_at_cap_switches_the_ai_off_with_a_quota_reason()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, branch) = await CompanyWithBranch(su, "qelig");
        var threadId = With(db =>
        {
            var t = new Domain.Thread
            {
                Channel = "email", Key = "qelig@quota.test", Name = "Q", EntityId = branch, AiEnabled = true,
                AiOverrides = new Dictionary<string, object?> { ["thread"] = true },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Threads.Add(t);
            db.SaveChanges();
            return t.Id;
        });
        AiVerdict Verdict() => With(db => AiEligibility.Decide(db, new ScopingService(db),
            db.Threads.AsNoTracking().First(t => t.Id == threadId)));

        Assert.True(Verdict().Enabled);
        Spend(5.0, branch);
        (await PutQuotas(su, new { entities = new Dictionary<string, object> { [branch.ToString()] = 5 } })).EnsureSuccessStatusCode();
        try
        {
            var v = Verdict();
            Assert.False(v.Enabled);
            Assert.Equal(AiRules.Quota, v.Rule);
            Assert.True(v.Forced);
            Assert.Equal("Off: AI quota reached for qelig Branch ($5.00 of $5.00 this month)", v.Reason);

            // raising the branch cap turns it back on…
            (await PutQuotas(su, new { entities = new Dictionary<string, object> { [branch.ToString()] = 50 } })).EnsureSuccessStatusCode();
            Assert.True(Verdict().Enabled);

            // …until the COMPANY's cap is reached (an ancestor's cap applies too)
            (await PutQuotas(su, new { entities = new Dictionary<string, object> { [company.ToString()] = 4 } })).EnsureSuccessStatusCode();
            v = Verdict();
            Assert.False(v.Enabled);
            Assert.Equal(AiRules.Quota, v.Rule);
            Assert.Contains("qelig Co", v.Reason);

            // the opt-out still wins (it is the one rule above the quota)
            With(db => { db.Threads.First(t => t.Id == threadId).OptedOut = true; db.SaveChanges(); return 0; });
            Assert.Equal(AiRules.OptedOut, Verdict().Rule);
        }
        finally
        {
            await PutQuotas(su, new { entities = new Dictionary<string, object?> { [company.ToString()] = null, [branch.ToString()] = null } });
        }
    }

    [Fact]
    public async Task An_inbound_the_quota_silences_is_handed_to_a_person_once()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var (_, branch) = await CompanyWithBranch(su, "qhand");
        const string email = "qhand@quota.test";
        With(db =>
        {
            var c = new Contact { Name = "Q Hand", Email = email, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = branch });
            db.Threads.Add(new Domain.Thread
            {
                Channel = "email", Key = email, Name = "Q Hand", ContactId = c.Id, EntityId = branch, AiEnabled = true,
                AiOverrides = new Dictionary<string, object?> { ["thread"] = true },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
            return 0;
        });
        Spend(3.0, branch);
        (await PutQuotas(su, new { entities = new Dictionary<string, object> { [branch.ToString()] = 2 } })).EnsureSuccessStatusCode();
        try
        {
            (await admin.PostAsJsonAsync("/api/whatsapp/simulate?ai=1",
                new { channel = "email", key = email, text = "hello, anyone?" })).EnsureSuccessStatusCode();
            (await admin.PostAsJsonAsync("/api/whatsapp/simulate?ai=1",
                new { channel = "email", key = email, text = "hello again" })).EnsureSuccessStatusCode();

            var t = With(db => db.Threads.AsNoTracking().First(x => x.Channel == "email" && x.Key == email));
            Assert.Equal(HandoverKinds.Quota, t.HandoverKind);
            Assert.True(Handover.IsOpen(t));
            Assert.Equal(1, t.HandoverCount);                 // once per open handover
            Assert.True(t.AiEnabled);                          // the tick stays — the AI resumes by itself
            Assert.Contains("AI quota reached for qhand Branch", t.HandoverReason);
            var aiReplies = With(db => db.Messages.Count(m => m.ThreadId == t.Id && m.Source == MessageSource.Ai));
            Assert.Equal(0, aiReplies);
            var notes = With(db => db.Messages.Count(m => m.ThreadId == t.Id && m.Private && m.Text.Contains("handed this conversation")));
            Assert.Equal(1, notes);
        }
        finally
        {
            await PutQuotas(su, new { entities = new Dictionary<string, object?> { [branch.ToString()] = null } });
        }
    }

    [Fact]
    public async Task A_user_at_cap_is_refused_by_the_playground_and_answered_by_the_assistant()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var agent = await Platform.As(_f, "agent", "agent123");
        Spend(2.0, user: "agent");
        (await PutQuotas(su, new { users = new Dictionary<string, object> { ["agent"] = 1.5 } })).EnsureSuccessStatusCode();
        try
        {
            var res = await agent.PostAsJsonAsync("/api/ai/playground/reply", new
            {
                channel = "whatsapp", profile = new { name = "Test" },
                messages = new[] { new { role = "customer", text = "hi" } },
            });
            Assert.Equal((HttpStatusCode)429, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Your AI quota for this month is used up ($2.00 of $1.50). Ask an admin to raise it.",
                body.GetProperty("detail").GetString());

            var chat = await agent.PostAsJsonAsync("/api/assistant/chat", new { message = "how many contacts?" });
            chat.EnsureSuccessStatusCode();
            var reply = await chat.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("quota", reply.GetProperty("mode").GetString());
            Assert.Contains("AI quota for this month is used up", reply.GetProperty("reply").GetString());

            // another person is not affected
            var admin = await Platform.As(_f, "admin", "admin123");
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/assistant/chat",
                new { message = "how many contacts?" })).StatusCode);
        }
        finally
        {
            await PutQuotas(su, new { users = new Dictionary<string, object?> { ["agent"] = null } });
        }
    }

    [Fact]
    public async Task Put_tiers_company_caps_are_for_billing_managers_admins_cap_branches_and_users()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var agent = await Platform.As(_f, "agent", "agent123");
        var (company, branch) = await CompanyWithBranch(su, "qtier");
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await PutQuotas(admin,
                new { entities = new Dictionary<string, object> { [company.ToString()] = 10 } })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PutQuotas(admin,
                new { entities = new Dictionary<string, object> { [branch.ToString()] = 10 } })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PutQuotas(admin,
                new { users = new Dictionary<string, object> { ["agent"] = 7 } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PutQuotas(admin,
                new { users = new Dictionary<string, object> { ["superadmin"] = 7 } })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PutQuotas(su,
                new { entities = new Dictionary<string, object> { [company.ToString()] = 25 } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await PutQuotas(agent,
                new { entities = new Dictionary<string, object> { [branch.ToString()] = 1 } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/billing/ai/quotas")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PutQuotas(su,
                new { entities = new Dictionary<string, object> { [branch.ToString()] = -1 } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PutQuotas(su,
                new { entities = new Dictionary<string, object> { ["999999"] = 1 } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PutQuotas(su,
                new { users = new Dictionary<string, object> { ["nobody-here"] = 1 } })).StatusCode);

            // the admin's view: the company cap read-only, the branch editable
            var q = await admin.GetFromJsonAsync<JsonElement>("/api/billing/ai/quotas");
            Assert.False(q.GetProperty("is_manager").GetBoolean());
            var rows = q.GetProperty("entities").EnumerateArray().ToList();
            var co = rows.Single(r => r.GetProperty("id").GetInt32() == company);
            Assert.False(co.GetProperty("editable").GetBoolean());
            Assert.Equal(25, co.GetProperty("cap").GetDouble());
            Assert.True(rows.Single(r => r.GetProperty("id").GetInt32() == branch).GetProperty("editable").GetBoolean());
            Assert.DoesNotContain(q.GetProperty("users").EnumerateArray(), u => u.GetProperty("username").GetString() == "superadmin");

            // audited
            Assert.True(With(db => db.AuditLogs.Any(a => a.Action == "billing.quotas" && a.Detail.Contains("qtier Branch"))));
        }
        finally
        {
            await PutQuotas(su, new
            {
                entities = new Dictionary<string, object?> { [company.ToString()] = null, [branch.ToString()] = null },
                users = new Dictionary<string, object?> { ["agent"] = null },
            });
        }
    }

    [Fact]
    public async Task Admins_are_warned_at_80_and_100_percent_once_each()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var (company, _) = await CompanyWithBranch(su, "qwarn");
        (await PutQuotas(su, new { entities = new Dictionary<string, object> { [company.ToString()] = 10 } })).EnsureSuccessStatusCode();
        int Count(int level) => With(db =>
        {
            var adminId = db.Users.Single(u => u.Username == "admin").Id;
            return db.Notifications.Count(n => n.UserId == adminId && n.RefKey == $"ai-quota:entity:{company}:{level}");
        });
        try
        {
            Spend(5.0, company);
            await su.GetAsync("/api/billing/ai/quotas");
            Assert.Equal(0, Count(80));

            Spend(3.5, company);                       // 85 %
            await su.GetAsync("/api/billing/ai/quotas");
            await su.GetAsync("/api/billing/ai/quotas");
            Assert.Equal(1, Count(80));
            Assert.Equal(0, Count(100));

            Spend(2.0, company);                       // 105 %
            await su.GetAsync("/api/billing/ai/quotas");
            AiQuota.Invalidate();
            await su.GetAsync("/api/billing/ai/quotas");
            Assert.Equal(1, Count(80));
            Assert.Equal(1, Count(100));
            var paused = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/quotas");
            Assert.Equal("paused", paused.GetProperty("entities").EnumerateArray()
                .Single(r => r.GetProperty("id").GetInt32() == company).GetProperty("state").GetString());
        }
        finally
        {
            await PutQuotas(su, new { entities = new Dictionary<string, object?> { [company.ToString()] = null } });
        }
    }
}

/// <summary>
/// Billing terms are locked per month: a change applies to this month's bill and
/// later; earlier months keep the terms that applied to them.
/// </summary>
public class AiBillingTermsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AiBillingTermsTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task A_change_bills_this_month_onward_and_past_months_keep_their_terms()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities",
            new { name = "Terms Co", slug = "terms-co" })).StatusCode);
        var now = DateTime.UtcNow;
        var past = now.AddMonths(-2);
        var pastKey = past.ToString("yyyy-MM");
        var thisKey = now.ToString("yyyy-MM");
        SettingsTestKit.WithDb(_f, db =>
        {
            var company = db.Entities.Single(e => e.Slug == "terms-co").Id;
            db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "gpt-4o", EntityId = company, CostUsd = 10,
                CreatedAt = new DateTime(past.Year, past.Month, 5, 0, 0, 0, DateTimeKind.Utc) });
            db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "gpt-4o", EntityId = company, CostUsd = 10,
                CreatedAt = now });
            db.SaveChanges();
            // terms that have applied since three months ago: 10 % markup
            new ScopingService(db).SetSetting(AiBilling.SettingKey,
                "{\"currency\":\"USD\",\"usd_rate\":1,\"default_markup_pct\":10,\"default_monthly_fee\":0," +
                $"\"companies\":{{}},\"effective_from\":\"{now.AddMonths(-3):yyyy-MM}\"}}");
            return 0;
        });

        (await su.PutAsJsonAsync("/api/billing/ai/settings",
            new { currency = "USD", usd_rate = 1, default_markup_pct = 50 })).EnsureSuccessStatusCode();
        // a second change in the same month replaces this month's terms, not history
        var put = await su.PutAsJsonAsync("/api/billing/ai/settings",
            new { currency = "USD", usd_rate = 1, default_markup_pct = 30 });
        put.EnsureSuccessStatusCode();

        double Usage(JsonElement bill) =>
            bill.GetProperty("company").GetProperty("summary").GetProperty("usage_amount").GetDouble();
        var then = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai?month={pastKey}&company=terms-co");
        Assert.Equal(11.0, Usage(then), 3);                  // 10 × 1.10 — the old terms
        var current = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai?month={thisKey}&company=terms-co");
        Assert.Equal(13.0, Usage(current), 3);               // 10 × 1.30 — the new terms

        var settings = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/settings");
        Assert.Equal(thisKey, settings.GetProperty("settings").GetProperty("effective_from").GetString());
        Assert.Equal(30, settings.GetProperty("settings").GetProperty("default_markup_pct").GetDouble());
        var history = settings.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);                      // current + the one it replaced
        Assert.Equal(10, history[1].GetProperty("default_markup_pct").GetDouble());
        Assert.Equal(now.AddMonths(-1).ToString("yyyy-MM"), history[1].GetProperty("until").GetString());

        // saving the same terms again changes nothing
        (await su.PutAsJsonAsync("/api/billing/ai/settings",
            new { currency = "USD", usd_rate = 1, default_markup_pct = 30 })).EnsureSuccessStatusCode();
        settings = await su.GetFromJsonAsync<JsonElement>("/api/billing/ai/settings");
        Assert.Equal(2, settings.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public void Terms_for_a_month_pick_the_latest_that_started_by_then()
    {
        var s = new BillingSettings
        {
            DefaultMarkupPct = 30, EffectiveFrom = "2026-10",
            History = new()
            {
                new BillingTerms { DefaultMarkupPct = 5 },                          // legacy: every earlier month
                new BillingTerms { DefaultMarkupPct = 20, EffectiveFrom = "2026-06" },
            },
        };
        Assert.Equal(5, s.TermsFor("2025-01").DefaultMarkupPct);
        Assert.Equal(20, s.TermsFor("2026-06").DefaultMarkupPct);
        Assert.Equal(20, s.TermsFor("2026-09").DefaultMarkupPct);
        Assert.Equal(30, s.TermsFor("2026-10").DefaultMarkupPct);
        Assert.Equal(30, s.TermsFor("2027-03").DefaultMarkupPct);

        // a first save has nothing to keep; a save over an older set moves it into history
        var first = AiBilling.Apply(new BillingSettings(), false, new BillingTerms { DefaultMarkupPct = 10 },
            new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        Assert.Empty(first.History);
        Assert.Equal("2026-10", first.EffectiveFrom);
        var later = AiBilling.Apply(first, true, new BillingTerms { DefaultMarkupPct = 40 },
            new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Single(later.History);
        Assert.Equal(10, later.TermsFor("2026-11").DefaultMarkupPct);
        Assert.Equal(40, later.TermsFor("2026-12").DefaultMarkupPct);

        // changed, then changed back in the same month: ONE period, not two identical rows
        var back = AiBilling.Apply(later, true, new BillingTerms { DefaultMarkupPct = 10 },
            new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc));
        Assert.Empty(back.History);
        Assert.Equal("2026-10", back.EffectiveFrom);
        Assert.Single(back.Timeline());
        // a stored history that already holds identical neighbours lists them once
        var dup = new BillingSettings { DefaultMarkupPct = 10, EffectiveFrom = "2026-12",
            History = new() { new BillingTerms { DefaultMarkupPct = 10, EffectiveFrom = "2026-10" } } };
        Assert.Single(dup.Timeline());
        Assert.Equal("2026-10", dup.Timeline()[0].Terms.EffectiveFrom);
    }
}
