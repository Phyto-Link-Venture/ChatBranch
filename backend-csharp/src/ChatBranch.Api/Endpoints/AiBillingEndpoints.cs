using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The AI bill: what each COMPANY (a top-level entity — its branches roll up
/// into it) owes for its LLM usage in a calendar month.
///
/// Cost = the LlmUsage ledger priced by <c>llm_prices</c> (USD, the same table
/// as Analytics → AI usage). The bill = cost × (1 + markup %) converted at the
/// billing exchange rate, plus a flat monthly fee. Markup, fee, currency and
/// rate live in ONE organization-wide Setting <c>ai_billing</c>, set by
/// <c>billing.manage</c> (superadmin by default) — never by the company.
///
/// A usage row's company: its own <c>entity_id</c> (stamped by the AI brain),
/// else its contact's entity when every link shares one company, else
/// "unattributed" (platform work: skill drafting, embeddings, the operator
/// assistant) — shown to billing managers, billed to nobody.
///
/// Readers with <c>billing.view</c> see the bill of the companies in their scope
/// (their assigned entities' companies; unassigned = every company) as BILLED
/// amounts only. Cost, markup and margin are for <c>billing.manage</c> only.
/// </summary>
public static class AiBillingEndpoints
{
    public const string ViewCap = "billing.view";
    public const string ManageCap = "billing.manage";
    public const string SettingKey = "ai_billing";

    public sealed class CompanyTerms
    {
        [JsonPropertyName("markup_pct")] public double? MarkupPct { get; set; }
        [JsonPropertyName("monthly_fee")] public double? MonthlyFee { get; set; }
    }

    public sealed class BillingSettings
    {
        [JsonPropertyName("currency")] public string Currency { get; set; } = "USD";
        /// <summary>1 USD = this many units of <see cref="Currency"/>.</summary>
        [JsonPropertyName("usd_rate")] public double UsdRate { get; set; } = 1;
        [JsonPropertyName("default_markup_pct")] public double DefaultMarkupPct { get; set; }
        [JsonPropertyName("default_monthly_fee")] public double DefaultMonthlyFee { get; set; }
        /// <summary>Per company (top-level entity id as a string): overrides of the defaults.</summary>
        [JsonPropertyName("companies")] public Dictionary<string, CompanyTerms> Companies { get; set; } = new();

        public double MarkupFor(int companyId) =>
            Companies.TryGetValue(companyId.ToString(CultureInfo.InvariantCulture), out var c) && c.MarkupPct is double m
                ? m : DefaultMarkupPct;

        public double FeeFor(int companyId) =>
            Companies.TryGetValue(companyId.ToString(CultureInfo.InvariantCulture), out var c) && c.MonthlyFee is double f
                ? f : DefaultMonthlyFee;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    internal static BillingSettings Load(ScopingService sc)
    {
        try
        {
            var raw = sc.SettingValue(SettingKey);
            if (!string.IsNullOrWhiteSpace(raw))
                return JsonSerializer.Deserialize<BillingSettings>(raw, Json) ?? new BillingSettings();
        }
        catch { /* a malformed row bills at the defaults rather than failing */ }
        return new BillingSettings();
    }

    public static void MapAiBillingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/billing/ai/settings", async (AppDbContext db, ScopingService sc) =>
        {
            var s = Load(sc);
            var companies = await db.Entities.AsNoTracking().Where(e => e.ParentId == null)
                .OrderBy(e => e.Name).Select(e => new { e.Id, e.Name, e.Slug }).ToListAsync();
            return Results.Ok(new
            {
                settings = s,
                companies = companies.Select(c => new
                {
                    id = c.Id, name = c.Name, slug = c.Slug,
                    markup_pct = s.Companies.TryGetValue(c.Id.ToString(CultureInfo.InvariantCulture), out var t) ? t.MarkupPct : null,
                    monthly_fee = t?.MonthlyFee,
                    effective_markup_pct = s.MarkupFor(c.Id),
                    effective_monthly_fee = s.FeeFor(c.Id),
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapPut("/api/billing/ai/settings", async (BillingSettings body, AppDbContext db, ScopingService sc) =>
        {
            var currency = (body.Currency ?? "").Trim().ToUpperInvariant();
            if (currency.Length is < 3 or > 3 || !currency.All(char.IsLetter))
                return Bad("currency must be a 3-letter code, e.g. MYR or USD.");
            if (body.UsdRate is <= 0 or > 100000) return Bad("usd_rate must be above 0 (1 USD = how many units of your currency).");
            if (!Ok(body.DefaultMarkupPct) || !OkFee(body.DefaultMonthlyFee))
                return Bad("Markup must be between 0 and 10000 %, fees between 0 and 1,000,000.");
            var topLevel = (await db.Entities.AsNoTracking().Where(e => e.ParentId == null)
                .Select(e => e.Id).ToListAsync()).ToHashSet();
            var companies = new Dictionary<string, CompanyTerms>();
            foreach (var (key, terms) in body.Companies ?? new())
            {
                if (!int.TryParse(key, out var id) || !topLevel.Contains(id))
                    return Bad($"Unknown company '{key}' — use a top-level entity id.");
                if (terms.MarkupPct is double m && !Ok(m) || terms.MonthlyFee is double f && !OkFee(f))
                    return Bad("Markup must be between 0 and 10000 %, fees between 0 and 1,000,000.");
                if (terms.MarkupPct != null || terms.MonthlyFee != null)
                    companies[id.ToString(CultureInfo.InvariantCulture)] = terms;
            }
            var clean = new BillingSettings
            {
                Currency = currency, UsdRate = body.UsdRate,
                DefaultMarkupPct = body.DefaultMarkupPct, DefaultMonthlyFee = body.DefaultMonthlyFee,
                Companies = companies,
            };
            sc.SetSetting(SettingKey, JsonSerializer.Serialize(clean));
            ChatBranch.Data.Audit.AuditTrail.Write(db, "billing.settings", "ai_billing",
                $"Markup {clean.DefaultMarkupPct}% default, {companies.Count} company override(s), {currency} @ {clean.UsdRate}");
            return Results.Ok(new { settings = clean });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapGet("/api/billing/ai", async (string? month, string? company, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IHttpClientFactory http) =>
        {
            await ModelPriceCatalog.EnsureFreshAsync(http, sc);
            if (!TryMonth(month, out var from)) return Bad("month must look like 2026-10.");
            var to = from.AddMonths(1);
            var manager = await ShellScope.HasCapAsync(db, me, ManageCap);
            var settings = Load(sc);
            var prices = AnalyticsEndpoints.PriceTable(sc);

            // ---- the tree: entity -> its company (top-level ancestor)
            var entities = await db.Entities.AsNoTracking()
                .Select(e => new { e.Id, e.ParentId, e.Name, e.Slug }).ToListAsync();
            var byId = entities.ToDictionary(e => e.Id);
            int? CompanyOf(int? entityId)
            {
                var cur = entityId;
                for (var i = 0; i < 32 && cur is int id && byId.TryGetValue(id, out var e); i++)
                {
                    if (e.ParentId == null) return e.Id;
                    cur = e.ParentId;
                }
                return null;
            }
            var contactCompany = (await db.ContactEntities.AsNoTracking()
                    .Select(l => new { l.ContactId, l.EntityId }).ToListAsync())
                .GroupBy(l => l.ContactId)
                .Select(g => (g.Key, Companies: g.Select(l => CompanyOf(l.EntityId)).Distinct().ToList()))
                .Where(x => x.Companies.Count == 1 && x.Companies[0] != null)
                .ToDictionary(x => x.Key, x => x.Companies[0]!.Value);

            // ---- who may see which company
            var scope = await VisibleCompanies(db, sc, me, manager, CompanyOf);
            var companies = entities.Where(e => e.ParentId == null && (scope == null || scope.Contains(e.Id)))
                .OrderBy(e => e.Name).ToList();
            var picked = string.IsNullOrWhiteSpace(company) ? null
                : companies.FirstOrDefault(c => c.Slug == company || c.Id.ToString(CultureInfo.InvariantCulture) == company);
            if (!string.IsNullOrWhiteSpace(company) && picked == null)
                return Results.NotFound(new { detail = $"Unknown company '{company}'." });
            if (picked == null && !manager && companies.Count == 1) picked = companies[0];

            // ---- the month's ledger, priced per row
            var rows = (await db.LlmUsage.AsNoTracking().Where(u => u.CreatedAt >= from && u.CreatedAt < to)
                    .Select(u => new
                    {
                        u.Feature, u.Model, u.EntityId, u.ContactId, u.CreatedAt,
                        u.PromptTokens, u.CompletionTokens, u.AudioPromptTokens, u.AudioCompletionTokens, u.CostUsd,
                    }).ToListAsync())
                .Select(u =>
                {
                    var cost = AnalyticsEndpoints.RowCost(prices, u.Model, u.PromptTokens, u.CompletionTokens,
                        u.AudioPromptTokens, u.AudioCompletionTokens, u.CostUsd);
                    var co = CompanyOf(u.EntityId)
                             ?? (u.ContactId is int cid && contactCompany.TryGetValue(cid, out var cc) ? cc : (int?)null);
                    return new BillRow(co, u.Feature, u.CreatedAt,
                        (long)u.PromptTokens + u.CompletionTokens + u.AudioPromptTokens + u.AudioCompletionTokens, cost);
                }).ToList();

            double Billed(int companyId, double costUsd) =>
                // 4 decimals: a light month is fractions of a cent, which must not read as 0
                Math.Round(costUsd * (1 + settings.MarkupFor(companyId) / 100d) * settings.UsdRate, 4, MidpointRounding.AwayFromZero);

            object Summary(int companyId, string name, string slug)
            {
                var mine = rows.Where(r => r.Company == companyId).ToList();
                var cost = mine.Sum(r => r.CostUsd);
                var usage = Billed(companyId, cost);
                var fee = Math.Round(settings.FeeFor(companyId), 2);
                return new
                {
                    id = companyId, name, slug,
                    requests = mine.Count,
                    tokens = mine.Sum(r => r.Tokens),
                    usage_amount = usage,
                    monthly_fee = fee,
                    total = Math.Round(usage + fee, 4),
                    // billing managers only
                    cost_usd = manager ? Math.Round(cost, 4) : (double?)null,
                    markup_pct = manager ? settings.MarkupFor(companyId) : (double?)null,
                    margin = manager ? Math.Round(usage + fee - cost * settings.UsdRate, 4) : (double?)null,
                };
            }

            object? Detail()
            {
                if (picked == null) return null;
                var mine = rows.Where(r => r.Company == picked.Id).ToList();
                var lines = mine.GroupBy(r => r.Feature).Select(g => new
                {
                    feature = g.Key,
                    requests = g.Count(),
                    tokens = g.Sum(r => r.Tokens),
                    amount = Billed(picked.Id, g.Sum(r => r.CostUsd)),
                    cost_usd = manager ? Math.Round(g.Sum(r => r.CostUsd), 4) : (double?)null,
                }).OrderByDescending(l => l.amount).ThenByDescending(l => l.requests).ToList();
                var daily = Enumerable.Range(0, (to - from).Days).Select(i => from.AddDays(i)).Select(d => new
                {
                    date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    amount = Billed(picked.Id, mine.Where(r => r.CreatedAt.Date == d).Sum(r => r.CostUsd)),
                }).ToList();
                return new { summary = Summary(picked.Id, picked.Name, picked.Slug), lines, daily };
            }

            var unattributed = rows.Where(r => r.Company == null).ToList();
            return Results.Ok(new
            {
                month = from.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to = to.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                currency = settings.Currency,
                is_manager = manager,
                companies = companies.Select(c => Summary(c.Id, c.Name, c.Slug)).ToList(),
                company = Detail(),
                unattributed = manager
                    ? new { requests = unattributed.Count, tokens = unattributed.Sum(r => r.Tokens),
                            cost_usd = Math.Round(unattributed.Sum(r => r.CostUsd), 4) }
                    : null,
                usd_rate = manager ? settings.UsdRate : (double?)null,
                pricing_note = manager
                    ? "Cost = what the provider charged per call (OpenRouter reports it); calls without a reported "
                      + "price are estimated from llm_prices over OpenRouter's public price list (USD per 1M tokens). "
                      + "Bill = cost × (1 + markup) × the exchange rate + the monthly fee."
                    : null,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ViewCap);
    }

    private sealed record BillRow(int? Company, string Feature, DateTime CreatedAt, long Tokens, double CostUsd);

    /// <summary>Company ids the caller may see; null = every company.</summary>
    private static Task<HashSet<int>?> VisibleCompanies(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        bool manager, Func<int?, int?> companyOf)
    {
        if (manager || ShellScope.RoleOf(me) == Roles.SuperAdmin) return Task.FromResult<HashSet<int>?>(null);
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        if (assigned.Count == 0) return Task.FromResult<HashSet<int>?>(null);
        return Task.FromResult<HashSet<int>?>(assigned.Select(a => companyOf(a)).OfType<int>().ToHashSet());
    }

    private static bool TryMonth(string? month, out DateTime from)
    {
        var now = DateTime.UtcNow;
        from = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (string.IsNullOrWhiteSpace(month)) return true;
        if (!DateTime.TryParseExact(month.Trim(), "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        from = new DateTime(parsed.Year, parsed.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }

    private static bool Ok(double markup) => markup is >= 0 and <= 10000;
    private static bool OkFee(double fee) => fee is >= 0 and <= 1_000_000;
    private static IResult Bad(string detail) => Results.BadRequest(new { detail });
}
