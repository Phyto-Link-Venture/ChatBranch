namespace ChatBranch.Domain;

// AI PRICE BOOKS (Django app: crm). Two tables: a named plan
// (AiPriceBook, crm_aipricebook) and its assignment to an entity
// (AiPriceBookAssignment, crm_aipricebookassignment).
//
// A plan is what the operator sells: one price MODE (a markup over provider
// cost, a flat monthly amount, or a per-unit price the client understands),
// an optional monthly fee and the per-login AI allowance it grants. Assigning
// one plan to many entities at once is what replaces typing a markup into one
// box per company (Data/Ai/AiPriceBooks.cs).
//
// These tables are the EDITING surface. What actually bills a month is the
// snapshot of them inside the month-versioned `ai_billing` Setting
// (BillingTerms.Plans / EntityPlans / DefaultPlan), written on every change by
// AiPriceBooks.SyncTerms — so changing a plan today can never rewrite the
// invoice of a month that has already been billed.

/// <summary>The closed vocabulary of <see cref="AiPriceBook.Mode"/>.</summary>
public static class AiPriceModes
{
    /// <summary>Percent on top of what the provider charged (today's only behaviour).</summary>
    public const string Markup = "markup";
    /// <summary>A fixed amount per company per month, whatever the usage.</summary>
    public const string Flat = "flat";
    /// <summary>A price per unit the client understands: per 1,000 tokens and/or per AI reply.</summary>
    public const string Unit = "unit";

    public static readonly string[] All = { Markup, Flat, Unit };

    public static bool IsValid(string? mode) => mode != null && All.Contains(mode);
}

/// <summary>
/// One named price book ("plan"): the terms a client is sold, so a hundred
/// companies can be priced by assigning a plan instead of by typing a markup
/// into a hundred boxes.
/// </summary>
public class AiPriceBook
{
    public int Id { get; set; }
    /// <summary>The stable key the month's terms snapshot refers to (lower-case, unique).</summary>
    public string Slug { get; set; } = "";
    /// <summary>What the operator calls this plan ("Reseller 20%", "Enterprise flat").</summary>
    public string Label { get; set; } = "";
    /// <summary><see cref="AiPriceModes"/> — exactly ONE price mode per plan.</summary>
    public string Mode { get; set; } = AiPriceModes.Markup;
    /// <summary><c>markup</c> mode: percent over the provider's cost.</summary>
    public double MarkupPct { get; set; }
    /// <summary><c>flat</c> mode: the amount billed per company per month, usage or no usage.</summary>
    public double FlatAmount { get; set; }
    /// <summary><c>unit</c> mode: price per 1,000 tokens, in the billing currency.</summary>
    public double UnitPer1kTokens { get; set; }
    /// <summary><c>unit</c> mode: price per AI reply, in the billing currency.</summary>
    public double UnitPerReply { get; set; }
    /// <summary>A monthly platform fee on top of the usage price (every mode).</summary>
    public double MonthlyFee { get; set; }
    /// <summary>The monthly AI allowance this plan grants EACH login it covers, in the
    /// billing currency (0 = no allowance). It is what replaces a hand-typed cap per
    /// user; an explicit cap in <c>ai_quotas</c> still wins.</summary>
    public double UserAllowance { get; set; }
    /// <summary>False = kept for history and for the months it billed, but not assignable.</summary>
    public bool IsActive { get; set; } = true;
    /// <summary>The organization default: what an entity with no plan of its own is priced by.</summary>
    public bool IsDefault { get; set; }
    /// <summary>Free text for the operator (what this plan is for, who agreed it).</summary>
    public string Note { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One entity priced by one plan. ANY level of the tree may carry an
/// assignment: a branch with none inherits its nearest assigned ancestor's
/// (<c>ScopingService</c>'s nearest-level-wins rule, applied in
/// <c>AiPriceBooks.Resolve</c>).
/// </summary>
public class AiPriceBookAssignment
{
    public int Id { get; set; }
    public int PriceBookId { get; set; }
    /// <summary>The entity the plan was assigned to — unique, one plan per entity.</summary>
    public int EntityId { get; set; }
    /// <summary>Who assigned it (a username, or "system").</summary>
    public string Actor { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
