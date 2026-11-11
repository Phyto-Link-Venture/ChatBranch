using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// AI PRICE BOOKS — pricing at scale: a named plan, assigned to as many entities
/// as you like, inherited by their branches.
///
/// <list type="bullet">
/// <item><c>GET /api/billing/ai/price-books</c> — the plans, plus a SEARCHABLE,
/// PAGINATED list of companies (or of every entity) with the plan that actually
/// governs each one, whether it is inherited or set on the row itself, and — for
/// a company — this month's real requests, tokens, provider cost and resulting
/// bill, so nobody prices blind again.</item>
/// <item><c>POST /api/billing/ai/price-books</c> — create or update a plan
/// (upsert by slug), and optionally make it the organization default.</item>
/// <item><c>DELETE /api/billing/ai/price-books/{slug}</c> — delete one that
/// nothing is assigned to (the months it billed keep their own snapshot of it).</item>
/// <item>Assigning a plan to MANY entities at once is the bindings pattern:
/// <c>POST /api/bindings/pricebook-entities</c> with <c>target</c> = the plan id
/// and <c>add</c> / <c>remove</c> = entity ids (<see cref="BindingsEndpoints"/>).
/// Assign a parent and every branch beneath it inherits.</item>
/// </list>
///
/// <para>Writes need <c>billing.manage</c> and leave an <c>AuditTrail</c> row, and
/// each one re-snapshots the plans into the month-versioned <c>ai_billing</c>
/// terms (<see cref="AiPriceBooks.SyncTerms"/>) — so a change prices the CURRENT
/// month and later, and a month that was already billed keeps the plan that
/// billed it.</para>
///
/// <para>The resolution order lives in ONE place,
/// <see cref="AiPriceBooks.Resolve"/>; this file only reports and edits.</para>
/// </summary>
public static class AiPriceBookEndpoints
{
    public const string ManageCap = AiBillingEndpoints.ManageCap;
    public const string ViewCap = AiBillingEndpoints.ViewCap;

    /// <summary>Which rows the list answers with.</summary>
    private const string ScopeCompanies = "companies";
    private const string ScopeEntities = "entities";

    private const string Note =
        "A plan is priced one of three ways: markup (percent over what the provider charged), flat (a fixed "
        + "amount per company per month, usage or no usage) or unit (a price per 1,000 tokens and/or per AI "
        + "reply, quoted in the billing currency). Each plan may also carry a monthly fee and the monthly AI "
        + "allowance it grants EACH login it covers. What governs one entity: its company's explicit override "
        + "first (field by field), then the plan assigned to it or to its nearest parent, then the organization "
        + "default plan, then the plain default markup and fee. Changes apply to this month and later; months "
        + "already billed keep the plan that billed them.";

    public static void MapAiPriceBookEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- the plans + who they price -------------------------------------------------
        app.MapGet("/api/billing/ai/price-books", async (string? q, string? scope, string? month, int? page,
            int? page_size, bool? overrides_only, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            IHttpClientFactory http) =>
        {
            var rows = await db.AiPriceBooks.AsNoTracking().OrderBy(p => p.Label).ToListAsync();
            var assignments = await db.AiPriceBookAssignments.AsNoTracking()
                .Select(a => new { a.PriceBookId, a.EntityId }).ToListAsync();
            var wantEntities = string.Equals(scope, ScopeEntities, StringComparison.OrdinalIgnoreCase);

            // the month's terms — what actually bills right now
            var stored = AiBilling.Load(sc);
            var monthKey = AiBilling.MonthKey(DateTime.UtcNow);
            if (!string.IsNullOrWhiteSpace(month))
            {
                if (!DateTime.TryParseExact(month.Trim(), "yyyy-MM", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                    || parsed.Year is < 2000 or > 2200)
                    return Bad("month must look like 2026-10 (years 2000–2200).");
                monthKey = parsed.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            }
            BillingTerms terms = stored.TermsFor(monthKey);

            // this month's real figures per company, from the ONE bill computation
            var (bill, error) = await AiBillingEndpoints.BuildBillAsync(monthKey, null, db, sc, me, http);
            if (bill == null) return error!;
            var figures = bill.Companies.ToDictionary(c => c.Id);

            var entities = await db.Entities.AsNoTracking()
                .Select(e => new { e.Id, e.ParentId, e.Name, e.Slug }).ToListAsync();
            var byId = entities.ToDictionary(e => e.Id);
            Dictionary<int, List<int>> chains = sc.ChainMap();
            List<int> ChainOf(int id) => chains.TryGetValue(id, out var chain) ? chain : new List<int> { id };
            var assignedTo = assignments.ToDictionary(a => a.EntityId, a => a.PriceBookId);

            var visible = wantEntities
                ? entities.Where(e => figures.ContainsKey(chains.TryGetValue(e.Id, out var c) && c.Count > 0 ? c[^1] : e.Id)).ToList()
                : entities.Where(e => e.ParentId == null && figures.ContainsKey(e.Id)).ToList();

            var needle = (q ?? "").Trim();
            if (needle.Length > 0)
                visible = visible.Where(e => e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                             || e.Slug.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();

            object? Figures(int id) => figures.TryGetValue(id, out var c)
                ? new
                {
                    requests = c.Requests, tokens = c.Tokens, cost_usd = c.CostUsd,
                    usage_amount = c.UsageAmount, monthly_fee = c.MonthlyFee, total = c.Total,
                }
                : null;

            var all = visible.Select(e =>
            {
                var price = AiPriceBooks.Resolve(terms, ChainOf(e.Id));
                var company = chains.TryGetValue(e.Id, out var chain) && chain.Count > 0 ? chain[^1] : e.Id;
                var own = terms.Companies.TryGetValue(company.ToString(CultureInfo.InvariantCulture), out var ov) && e.ParentId == null
                    ? ov : null;
                return new
                {
                    id = e.Id, name = e.Name, slug = e.Slug, parent_id = e.ParentId,
                    company = e.ParentId == null, depth = Math.Max(0, ChainOf(e.Id).Count - 1),
                    company_id = company,
                    plan = price.PlanSlug, plan_label = price.PlanLabel, mode = price.Mode,
                    plan_source = price.Source,
                    inherited = price.InheritedBy(e.Id),
                    assigned_at = price.AssignedAt,
                    assigned_at_name = price.AssignedAt is int at && byId.TryGetValue(at, out var atRow) ? atRow.Name : null,
                    // a plan is pinned ON this row (not inherited): what "show only overrides" means
                    assigned_here = assignedTo.ContainsKey(e.Id),
                    markup_pct = price.MarkupPct,
                    monthly_fee = price.FixedMonthly(),
                    user_allowance = price.UserAllowance,
                    unit_per_1k_tokens = price.UnitPer1kTokens,
                    unit_per_reply = price.UnitPerReply,
                    @override = own == null ? null : new { markup_pct = own.MarkupPct, monthly_fee = own.MonthlyFee },
                    usage = e.ParentId == null ? Figures(e.Id) : null,
                };
            }).OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase).ToList();

            if (overrides_only == true)
                all = all.Where(r => r.@override != null || r.assigned_here).ToList();

            var size = Math.Clamp(page_size ?? 25, 1, 200);
            var pageNo = Math.Max(1, page ?? 1);
            var slice = all.Skip((pageNo - 1) * size).Take(size).ToList();

            return Results.Ok(new
            {
                month = monthKey,
                current_month = AiBilling.MonthKey(DateTime.UtcNow),
                currency = terms.Currency,
                usd_rate = terms.UsdRate,
                defaults = new { markup_pct = terms.DefaultMarkupPct, monthly_fee = terms.DefaultMonthlyFee },
                default_plan = terms.DefaultPlan,
                plans = rows.Select(p => Plan(p, assignments.Count(a => a.PriceBookId == p.Id), terms)).ToList(),
                scope = wantEntities ? ScopeEntities : ScopeCompanies,
                q = needle,
                overrides_only = overrides_only == true,
                page = pageNo, page_size = size, total = all.Count,
                rows = slice,
                reply_features = AiPriceBooks.ReplyFeatures,
                note = Note,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        // ---- create or update one plan --------------------------------------------------
        app.MapPost("/api/billing/ai/price-books", async (JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return Bad("Send a plan: {label, mode: markup|flat|unit, markup_pct | flat_amount | unit_per_1k_tokens / unit_per_reply, monthly_fee, user_allowance}.");
            var label = Str(body, "label").Trim();
            var slug = AiPriceBooks.NormalizeSlug(Str(body, "slug").Length > 0 ? Str(body, "slug") : label);
            if (slug.Length < 2) return Bad("A plan needs a key or a name of at least 2 usable characters.");

            var existing = await db.AiPriceBooks.FirstOrDefaultAsync(p => p.Slug == slug);
            var now = DateTime.UtcNow;
            if (existing == null && await db.AiPriceBooks.CountAsync() >= AiPriceBooks.MaxPlans)
                return Bad($"At most {AiPriceBooks.MaxPlans} plans. Deactivate the ones you no longer sell.");
            var plan = existing ?? new AiPriceBook
            {
                Slug = slug, CreatedAt = now, CreatedBy = me.Identity?.Name ?? "system",
            };
            // an update keeps every field the caller left out (a PATCH, as elsewhere)
            plan.Label = label.Length > 0 ? label : plan.Label.Length > 0 ? plan.Label : slug;
            plan.Mode = Str(body, "mode") is { Length: > 0 } mode ? mode.Trim().ToLowerInvariant() : plan.Mode;
            plan.MarkupPct = Num(body, "markup_pct") ?? plan.MarkupPct;
            plan.FlatAmount = Num(body, "flat_amount") ?? plan.FlatAmount;
            plan.UnitPer1kTokens = Num(body, "unit_per_1k_tokens") ?? plan.UnitPer1kTokens;
            plan.UnitPerReply = Num(body, "unit_per_reply") ?? plan.UnitPerReply;
            plan.MonthlyFee = Num(body, "monthly_fee") ?? plan.MonthlyFee;
            plan.UserAllowance = Num(body, "user_allowance") ?? plan.UserAllowance;
            plan.IsActive = Bool(body, "active") ?? plan.IsActive;
            plan.Note = Str(body, "note") is { Length: > 0 } note ? note.Trim() : plan.Note;
            plan.UpdatedAt = now;
            if (AiPriceBooks.Validate(plan) is string bad)
            {
                db.ChangeTracker.Clear();
                return Bad(bad);
            }
            var makeDefault = Bool(body, "is_default");
            if (existing == null) db.AiPriceBooks.Add(plan);
            await db.SaveChangesAsync();
            if (makeDefault == true && plan.IsActive)
            {
                foreach (var other in await db.AiPriceBooks.Where(p => p.Id != plan.Id && p.IsDefault).ToListAsync())
                    other.IsDefault = false;
                plan.IsDefault = true;
                await db.SaveChangesAsync();
            }
            else if (makeDefault == false || !plan.IsActive)
            {
                // a deactivated plan can never be the organization default
                if (plan.IsDefault)
                {
                    plan.IsDefault = false;
                    await db.SaveChangesAsync();
                }
            }

            // the month's terms snapshot: this plan bills from THIS month on
            var next = AiPriceBooks.SyncTerms(db, sc);
            ChatBranch.Data.Audit.AuditTrail.Write(db, "billing.pricebook", plan.Slug,
                $"{(existing == null ? "Added" : "Changed")} plan '{plan.Label}': {AiPriceBooks.Describe(plan)}"
                + (plan.IsDefault ? " (organization default)" : "")
                + (plan.IsActive ? "" : " (inactive)")
                + $", from {next.EffectiveFrom ?? "the start"}");
            return Results.Ok(new
            {
                ok = true,
                created = existing == null,
                plan = Plan(plan, await db.AiPriceBookAssignments.CountAsync(a => a.PriceBookId == plan.Id),
                    next.TermsFor(AiBilling.MonthKey(DateTime.UtcNow))),
                effective_from = next.EffectiveFrom,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        // ---- delete one ------------------------------------------------------------------
        app.MapDelete("/api/billing/ai/price-books/{slug}", async (string slug, AppDbContext db, ScopingService sc) =>
        {
            var plan = await db.AiPriceBooks.FirstOrDefaultAsync(p => p.Slug == slug);
            if (plan == null) return Results.NotFound(new { detail = $"No plan '{slug}'." });
            var assigned = await db.AiPriceBookAssignments.CountAsync(a => a.PriceBookId == plan.Id);
            if (assigned > 0)
                return Bad($"'{plan.Label}' still prices {assigned} entity/entities — move them to another plan first "
                           + "(POST /api/bindings/pricebook-entities with remove), or just deactivate this one.");
            db.AiPriceBooks.Remove(plan);
            await db.SaveChangesAsync();
            AiPriceBooks.SyncTerms(db, sc);
            ChatBranch.Data.Audit.AuditTrail.Write(db, "billing.pricebook.delete", plan.Slug,
                $"Deleted plan '{plan.Label}' ({AiPriceBooks.Describe(plan)}). Months already billed keep it.");
            return Results.Ok(new { ok = true, deleted = plan.Slug });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);
    }

    /// <summary>One plan row: what it is, how many entities it prices, and whether the
    /// month's terms already carry it (they do unless a write failed half-way).</summary>
    private static object Plan(AiPriceBook p, int entities, BillingTerms terms)
    {
        var inTerms = terms.Plan(p.Slug);
        return new
        {
            id = p.Id, slug = p.Slug, label = p.Label, mode = p.Mode,
            markup_pct = p.MarkupPct, flat_amount = p.FlatAmount,
            unit_per_1k_tokens = p.UnitPer1kTokens, unit_per_reply = p.UnitPerReply,
            monthly_fee = p.MonthlyFee, user_allowance = p.UserAllowance,
            active = p.IsActive, is_default = p.IsDefault, note = p.Note,
            entities,
            summary = AiPriceBooks.Describe(p),
            // the plan as THIS month bills it (a change applies to the current month on)
            in_terms = inTerms != null
                       && JsonSerializer.Serialize(inTerms) == JsonSerializer.Serialize(PricePlan.From(p)),
            created_at = p.CreatedAt, updated_at = p.UpdatedAt,
        };
    }

    private static string Str(JsonElement body, string name) =>
        body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var s) && double.IsFinite(s)) return s;
        return null;
    }

    private static bool? Bool(JsonElement body, string name) =>
        body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static IResult Bad(string detail) => Results.BadRequest(new { detail });
}
