using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai;

/// <summary>
/// One named plan as the month's terms snapshot keeps it (the serialized form of
/// <see cref="AiPriceBook"/>). A month bills from this copy, never from the table,
/// so editing a plan today cannot rewrite a month that was already billed.
/// </summary>
public sealed class PricePlan
{
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary><see cref="AiPriceModes"/>: markup | flat | unit.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = AiPriceModes.Markup;
    [JsonPropertyName("markup_pct")] public double MarkupPct { get; set; }
    [JsonPropertyName("flat_amount")] public double FlatAmount { get; set; }
    [JsonPropertyName("unit_per_1k_tokens")] public double UnitPer1kTokens { get; set; }
    [JsonPropertyName("unit_per_reply")] public double UnitPerReply { get; set; }
    [JsonPropertyName("monthly_fee")] public double MonthlyFee { get; set; }
    /// <summary>The monthly AI allowance this plan grants EACH login it covers
    /// (billing currency; 0 = none). An explicit <c>ai_quotas</c> cap still wins.</summary>
    [JsonPropertyName("user_allowance")] public double UserAllowance { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; } = true;

    public PricePlan Copy() => new()
    {
        Slug = Slug, Label = Label, Mode = Mode, MarkupPct = MarkupPct, FlatAmount = FlatAmount,
        UnitPer1kTokens = UnitPer1kTokens, UnitPerReply = UnitPerReply, MonthlyFee = MonthlyFee,
        UserAllowance = UserAllowance, Active = Active,
    };

    public static PricePlan From(AiPriceBook row) => new()
    {
        Slug = row.Slug, Label = row.Label, Mode = row.Mode, MarkupPct = row.MarkupPct,
        FlatAmount = row.FlatAmount, UnitPer1kTokens = row.UnitPer1kTokens, UnitPerReply = row.UnitPerReply,
        MonthlyFee = row.MonthlyFee, UserAllowance = row.UserAllowance, Active = row.IsActive,
    };
}

/// <summary>
/// The price that governs ONE entity for ONE month, and where it came from —
/// the single answer every caller (the bill, the quotas, the settings screen)
/// asks <see cref="AiPriceBooks.Resolve"/> for.
/// </summary>
public sealed record EffectivePrice(
    string Source,
    string? PlanSlug,
    string? PlanLabel,
    string Mode,
    // AssignedAt: the entity the winning plan was assigned to (null = org default / built-in)
    int? AssignedAt,
    double MarkupPct,
    double FlatAmount,
    double UnitPer1kTokens,
    double UnitPerReply,
    double MonthlyFee,
    double UserAllowance,
    bool OverriddenMarkup,
    bool OverriddenFee)
{
    /// <summary>A per-company explicit override set at least one of this price's numbers.</summary>
    public const string FromOverride = "company_override";
    /// <summary>A plan assigned to this entity or to one of its ancestors.</summary>
    public const string FromPlan = "plan";
    /// <summary>The organization default plan.</summary>
    public const string FromOrgDefault = "org_default_plan";
    /// <summary>No plan anywhere: the terms' own default markup and fee (today's behaviour).</summary>
    public const string FromBuiltin = "builtin";

    /// <summary>The plan came from an ANCESTOR of the entity asked about, not from itself.</summary>
    public bool InheritedBy(int? entityId) => AssignedAt != null && AssignedAt != entityId;

    /// <summary>
    /// What this month's USAGE is billed, in the billing currency:
    /// <list type="bullet">
    /// <item><c>markup</c> — provider cost × (1 + markup %) × the exchange rate.</item>
    /// <item><c>flat</c> — nothing: the plan's flat amount is charged once a month
    /// (<see cref="FixedMonthly"/>) whatever the usage.</item>
    /// <item><c>unit</c> — tokens ÷ 1,000 × the token price + replies × the reply price,
    /// quoted directly in the billing currency (never converted: the client was
    /// quoted these numbers in their own money).</item>
    /// </list>
    /// </summary>
    public double UsageAmount(double costUsd, long tokens, long replies, double usdRate) => Mode switch
    {
        AiPriceModes.Flat => 0,
        AiPriceModes.Unit => tokens / 1000d * UnitPer1kTokens + replies * UnitPerReply,
        _ => costUsd * (1 + MarkupPct / 100d) * usdRate,
    };

    /// <summary>
    /// What an AI usage QUOTA counts for the same usage. Identical to
    /// <see cref="UsageAmount"/> for every markup plan (markup ≥ 0 can never bill
    /// below cost), with one floor: a quota counts at least what the usage COST,
    /// converted at the exchange rate. Without it a flat-priced company (usage
    /// amount 0) could never reach a cap, and a cap is the brake on runaway spend.
    /// </summary>
    public double QuotaAmount(double costUsd, long tokens, long replies, double usdRate) =>
        Math.Max(UsageAmount(costUsd, tokens, replies, usdRate), costUsd * usdRate);

    /// <summary>The fixed part of the month: the monthly fee plus a flat plan's amount.</summary>
    public double FixedMonthly() => MonthlyFee + (Mode == AiPriceModes.Flat ? FlatAmount : 0);
}

/// <summary>
/// AI PRICE BOOKS — named plans, assigned in bulk, inherited down the entity tree.
///
/// <para><b>Why.</b> Pricing used to mean typing a markup % over a provider cost
/// nobody can see into one box per company, with one global Save. At a hundred
/// companies and a thousand logins that is not a workflow. A plan is named once
/// and assigned to as many entities as you like (one call, the bindings pattern:
/// <c>POST /api/bindings/pricebook-entities</c>), and the per-login allowance
/// comes with it instead of being typed a thousand times.</para>
///
/// <para><b>The resolution order — ONE place, <see cref="Resolve"/>:</b></para>
/// <list type="number">
/// <item>the per-company explicit override (<c>ai_billing.companies</c>), FIELD BY
/// FIELD: a markup it sets wins (and makes the price a markup price), a monthly
/// fee it sets wins;</item>
/// <item>the plan assigned to the entity — else to its nearest ancestor that has
/// one (the chain comes from <see cref="ScopingService.EntityChain"/> /
/// <see cref="ScopingService.ChainMap"/>, so a plan set on a parent flows to its
/// branches through the same nearest-level-wins rule as every other setting);</item>
/// <item>the organization default plan (<c>default_plan</c>);</item>
/// <item>the built-in default: the terms' own <c>default_markup_pct</c> and
/// <c>default_monthly_fee</c> — which is exactly what billed before price books
/// existed, so a database with no plans bills identically.</item>
/// </list>
///
/// <para><b>Months stay frozen.</b> <see cref="AiPriceBook"/> and
/// <see cref="AiPriceBookAssignment"/> are the editing surface;
/// <see cref="SyncTerms"/> writes a SNAPSHOT of them into the month-versioned
/// <c>ai_billing</c> Setting on every change (<see cref="AiBilling.Apply"/> —
/// current month and later). Billing and quotas read that snapshot via
/// <c>TermsFor(month)</c> and never the tables, so a plan edited or reassigned
/// today cannot change the invoice of a month that was already billed.</para>
/// </summary>
public static class AiPriceBooks
{
    /// <summary>
    /// The usage features that count as one "AI reply" for <c>unit</c> pricing:
    /// the messages a customer actually receives from the AI. Everything else the
    /// AI spends money on (indexing, summaries, reviews, the operator's own
    /// assistant, voice) is the platform's own overhead and is NOT a billable
    /// unit — a unit-priced client was quoted a price per reply, not per
    /// embedding batch.
    /// </summary>
    public static readonly string[] ReplyFeatures = { "chat_reply", "follow_up", "flow_message" };

    public static bool IsReply(string? feature) =>
        feature != null && ReplyFeatures.Contains(feature, StringComparer.OrdinalIgnoreCase);

    // ---- resolution ----------------------------------------------------------------------

    /// <summary>
    /// THE price of one entity for the month these <paramref name="terms"/> bill.
    /// <paramref name="chain"/> is the entity and its ancestors, NEAREST FIRST
    /// (<see cref="ScopingService.EntityChain"/>); its LAST element is the company,
    /// which is what a per-company override and the monthly fee hang off. An empty
    /// or null chain = no entity (unattributed work): the defaults.
    /// </summary>
    public static EffectivePrice Resolve(BillingTerms terms, IReadOnlyList<int>? chain)
    {
        // 2–4: the plan assigned nearest up the tree, else the org default, else nothing
        var source = EffectivePrice.FromBuiltin;
        PricePlan? plan = null;
        int? assignedAt = null;
        if (chain != null)
            foreach (var id in chain)
            {
                if (!terms.EntityPlans.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var slug)) continue;
                if (terms.Plan(slug) is not { Active: true } hit) continue;
                plan = hit;
                assignedAt = id;
                source = EffectivePrice.FromPlan;
                break;
            }
        if (plan == null && terms.Plan(terms.DefaultPlan) is { Active: true } orgDefault)
        {
            plan = orgDefault;
            source = EffectivePrice.FromOrgDefault;
        }

        var mode = plan?.Mode is { Length: > 0 } m && AiPriceModes.IsValid(m) ? m : AiPriceModes.Markup;
        var markup = plan != null ? plan.MarkupPct : terms.DefaultMarkupPct;
        var fee = plan != null ? plan.MonthlyFee : terms.DefaultMonthlyFee;
        var flat = plan?.FlatAmount ?? 0;

        // 1: the per-company explicit override, field by field — what it does not set
        // still comes from the plan, and with no plans at all this IS today's behaviour
        // (BillingTerms.MarkupFor / FeeFor).
        var overriddenMarkup = false;
        var overriddenFee = false;
        var company = chain is { Count: > 0 } ? chain[^1] : (int?)null;
        if (company is int co
            && terms.Companies.TryGetValue(co.ToString(CultureInfo.InvariantCulture), out var ov) && ov != null)
        {
            if (ov.MarkupPct is double pct)
            {
                markup = pct;
                mode = AiPriceModes.Markup;   // an explicit markup IS a markup price
                flat = 0;
                overriddenMarkup = true;
            }
            if (ov.MonthlyFee is double f)
            {
                fee = f;
                overriddenFee = true;
            }
            if (overriddenMarkup || overriddenFee) source = EffectivePrice.FromOverride;
        }

        return new EffectivePrice(source, plan?.Slug, plan?.Label, mode, assignedAt, markup, flat,
            plan?.UnitPer1kTokens ?? 0, plan?.UnitPerReply ?? 0, fee, plan?.UserAllowance ?? 0,
            overriddenMarkup, overriddenFee);
    }

    /// <summary>
    /// <see cref="Resolve"/> for one entity, walking the tree with
    /// <see cref="ScopingService.EntityChain"/> (one entity = one query; for a
    /// payload or a whole month's ledger pass <see cref="ScopingService.ChainMap"/>
    /// chains instead).
    /// </summary>
    public static EffectivePrice ResolveFor(ScopingService sc, BillingTerms terms, int? entityId) =>
        Resolve(terms, entityId is int id ? sc.EntityChain(id) : null);

    /// <summary>
    /// The allowance a login gets from the plans: the SMALLEST positive allowance
    /// among the entities the person is bound to (never more than any of their
    /// companies' plans grants), or the organization default plan's when they are
    /// bound to none. 0 = no allowance. An explicit <c>ai_quotas</c> cap always wins
    /// over this — see <see cref="AiQuota"/>.
    /// </summary>
    public static double UserAllowance(BillingTerms terms, IEnumerable<IReadOnlyList<int>> chains)
    {
        var best = 0d;
        var any = false;
        foreach (var chain in chains)
        {
            any = true;
            var allowance = Resolve(terms, chain).UserAllowance;
            if (allowance <= 0) return 0;          // one unlimited scope = unlimited
            if (best == 0 || allowance < best) best = allowance;
        }
        return any ? best : Resolve(terms, null).UserAllowance;
    }

    // ---- the editing surface → the month's snapshot ---------------------------------------

    /// <summary>The plans and assignments as the TABLES hold them right now.</summary>
    public static (List<PricePlan> Plans, Dictionary<string, string> EntityPlans, string? DefaultPlan)
        LiveSnapshot(AppDbContext db)
    {
        var rows = db.AiPriceBooks.AsNoTracking().OrderBy(p => p.Slug).ToList();
        var plans = rows.Select(PricePlan.From).ToList();
        var bySlug = rows.ToDictionary(r => r.Id, r => r.Slug);
        var assignments = db.AiPriceBookAssignments.AsNoTracking()
            .Select(a => new { a.EntityId, a.PriceBookId }).ToList()
            .Where(a => bySlug.ContainsKey(a.PriceBookId))
            .GroupBy(a => a.EntityId)
            .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => bySlug[g.First().PriceBookId]);
        var fallback = rows.FirstOrDefault(r => r.IsDefault && r.IsActive)?.Slug;
        return (plans, assignments, fallback);
    }

    /// <summary>
    /// Snapshot the tables into the month-versioned terms: the plans and
    /// assignments now in force start billing the CURRENT month, and every month
    /// already recorded keeps the snapshot that billed it. Call it after EVERY plan
    /// or assignment change — it is what makes month-versioning hold.
    /// </summary>
    public static BillingSettings SyncTerms(AppDbContext db, ScopingService sc, DateTime? nowUtc = null)
    {
        var stored = AiBilling.Load(sc, out var hadRow);
        var (plans, entityPlans, fallback) = LiveSnapshot(db);
        var incoming = stored.CopyTerms(null);
        incoming.Plans = plans;
        incoming.EntityPlans = entityPlans;
        incoming.DefaultPlan = fallback;
        var next = AiBilling.Apply(stored, hadRow, incoming, nowUtc ?? DateTime.UtcNow);
        sc.SetSetting(AiBilling.SettingKey, JsonSerializer.Serialize(next));
        AiQuota.Invalidate();   // quota spend and allowances are priced with these terms
        return next;
    }

    // ---- validation (shared by the endpoint and the MCP tool) ------------------------------

    public const int MaxPlans = 200;

    /// <summary>A plan slug: lower-case letters, digits and '-', 2–40 characters.</summary>
    public static string NormalizeSlug(string? slug)
    {
        var s = (slug ?? "").Trim().ToLowerInvariant();
        var clean = new string(s.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        while (clean.Contains("--")) clean = clean.Replace("--", "-");
        return clean.Length > 40 ? clean[..40] : clean;
    }

    /// <summary>What is wrong with this plan, or null when it is fine.</summary>
    public static string? Validate(AiPriceBook plan)
    {
        if (plan.Slug.Length is < 2 or > 40) return "A plan needs a key of 2–40 characters (letters, digits and '-').";
        if (string.IsNullOrWhiteSpace(plan.Label)) return "A plan needs a name.";
        if (plan.Label.Length > 120) return "A plan name can be at most 120 characters.";
        if (!AiPriceModes.IsValid(plan.Mode))
            return "mode must be markup, flat or unit.";
        if (plan.MarkupPct is < 0 or > 10000) return "Markup must be between 0 and 10000 %.";
        foreach (var amount in new[] { plan.FlatAmount, plan.MonthlyFee, plan.UnitPer1kTokens, plan.UnitPerReply })
            if (amount is < 0 or > 1_000_000) return "Amounts must be between 0 and 1,000,000.";
        if (plan.UserAllowance is < 0 or > 1_000_000_000) return "The per-login allowance must be between 0 and 1,000,000,000.";
        if (plan.Mode == AiPriceModes.Flat && plan.FlatAmount <= 0)
            return "A flat plan needs a flat amount above 0.";
        if (plan.Mode == AiPriceModes.Unit && plan.UnitPer1kTokens <= 0 && plan.UnitPerReply <= 0)
            return "A unit plan needs a price per 1,000 tokens, a price per AI reply, or both.";
        if (plan.Note.Length > 500) return "The note can be at most 500 characters.";
        return null;
    }

    /// <summary>One line describing a plan's price, for an audit row or a tool answer.</summary>
    public static string Describe(AiPriceBook p) => p.Mode switch
    {
        AiPriceModes.Flat => $"flat {Fmt(p.FlatAmount)}/month",
        AiPriceModes.Unit => "unit "
            + string.Join(" + ", new[]
            {
                p.UnitPer1kTokens > 0 ? $"{Fmt(p.UnitPer1kTokens)}/1k tokens" : null,
                p.UnitPerReply > 0 ? $"{Fmt(p.UnitPerReply)}/reply" : null,
            }.Where(x => x != null)),
        _ => $"markup {Fmt(p.MarkupPct)}%",
    } + (p.MonthlyFee > 0 ? $" + fee {Fmt(p.MonthlyFee)}" : "")
      + (p.UserAllowance > 0 ? $", allowance {Fmt(p.UserAllowance)}/login" : "");

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
