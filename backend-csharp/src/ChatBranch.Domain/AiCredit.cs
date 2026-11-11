namespace ChatBranch.Domain;

// Prepaid AI credit (Django app: crm). Two tables: an append-only LEDGER
// (AiCreditEntry) that is the source of truth, and an ACCOUNT (AiCreditAccount)
// whose `balance` is a cache of that ledger's sum — kept in step by one atomic
// increment per entry, inside the entry's own transaction.
//
// Credit attaches to a COMPANY (a top-level Entity); a branch draws on its
// top-level parent's balance, exactly as the AI bill and the AI quotas already
// attribute usage. A second, OPTIONAL scope is keyed on a login
// (AiCreditScopes.User), mirroring AiQuotaConfig.Users.

/// <summary>The closed vocabulary of <see cref="AiCreditAccount.Scope"/>.</summary>
public static class AiCreditScopes
{
    /// <summary>A company's balance: <see cref="AiCreditAccount.EntityId"/> is a top-level entity.</summary>
    public const string Entity = "entity";
    /// <summary>One login's balance: <see cref="AiCreditAccount.UserName"/>.</summary>
    public const string User = "user";

    public static readonly string[] All = { Entity, User };
}

/// <summary>The closed vocabulary of <see cref="AiCreditEntry.Kind"/>.</summary>
public static class AiCreditKinds
{
    /// <summary>Credit handed out by a billing manager (no money changed hands).</summary>
    public const string Grant = "grant";
    /// <summary>Credit bought (Stripe) — written by phase 3.</summary>
    public const string Topup = "topup";
    /// <summary>The bonus part of a promotional purchase — written by phase 4.</summary>
    public const string PromoBonus = "promo_bonus";
    /// <summary>AI usage debited from the balance — written by phase 2.</summary>
    public const string Consumption = "consumption";
    /// <summary>A signed correction by a billing manager.</summary>
    public const string Adjustment = "adjustment";
    public const string Refund = "refund";
    public const string Chargeback = "chargeback";
    public const string Expiry = "expiry";
    /// <summary>Half of a currency change (the other half credits the new currency).</summary>
    public const string Conversion = "conversion";

    public static readonly string[] All =
        { Grant, Topup, PromoBonus, Consumption, Adjustment, Refund, Chargeback, Expiry, Conversion };
}

/// <summary>
/// One prepaid AI credit balance. <see cref="Balance"/> is a CACHE of
/// <c>sum(crm_aicreditentry.amount)</c> for the same scope — never authoritative,
/// and never clamped: it is SIGNED, because consumption is only knowable after a
/// model call and the last reply before exhaustion always overshoots. Clamping
/// it to zero would forgive money and stop the ledger reconciling.
/// </summary>
public class AiCreditAccount
{
    public int Id { get; set; }
    /// <summary><see cref="AiCreditScopes"/>: a company (entity) or one login (user).</summary>
    public string Scope { get; set; } = AiCreditScopes.Entity;
    /// <summary>The company — a TOP-LEVEL entity. Null on a user-scoped account.</summary>
    public int? EntityId { get; set; }
    /// <summary>The login of a user-scoped account; "" on a company account.</summary>
    public string UserName { get; set; } = "";
    /// <summary>The ledger's running sum, in <see cref="Currency"/>. May be negative.</summary>
    public double Balance { get; set; }
    /// <summary>The billing currency this balance is denominated in, copied from the
    /// billing terms when the account was created (a later currency change does NOT
    /// convert it — see docs/DATABASE_SCHEMA.md).</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>Whether credit governs this scope at all. False = the balance is shown
    /// but never enforced (phase 2); an account row only exists once credit was granted
    /// or adjusted, so a company with no row is simply not on credit.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How far below zero the balance may go before AI stops (phase 2). 0 = stop at zero.</summary>
    public double OverdraftLimit { get; set; }
    /// <summary>The balance at which admins are warned (phase 2). 0 = derive one.</summary>
    public double LowBalanceThreshold { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One immutable line of the credit ledger — the source of truth. Append-only:
/// never updated, never deleted. A correction is a NEW signed entry.
/// </summary>
public class AiCreditEntry
{
    public int Id { get; set; }
    /// <summary><see cref="AiCreditScopes"/> — matches the account this line belongs to.</summary>
    public string Scope { get; set; } = AiCreditScopes.Entity;
    /// <summary>The company (top-level entity) credited or debited; null on a user line.</summary>
    public int? EntityId { get; set; }
    /// <summary>The login of a user-scoped line; "" on a company line.</summary>
    public string UserName { get; set; } = "";
    /// <summary><see cref="AiCreditKinds"/>.</summary>
    public string Kind { get; set; } = AiCreditKinds.Grant;
    /// <summary>SIGNED: credits positive, consumption and refunds negative.</summary>
    public double Amount { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>The account's balance once this line was applied (the atomic increment's result).</summary>
    public double BalanceAfter { get; set; }
    /// <summary>What this line came from: <c>llmusage:12..84</c>, <c>stripe:evt_…</c>, <c>payment:12</c>.</summary>
    public string Ref { get; set; } = "";
    /// <summary>The replay guard, UNIQUE where non-blank (<c>uniq_aicreditentry_idem</c>):
    /// a repeated write with the same key credits once. "" = not applicable.</summary>
    public string IdempotencyKey { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>Who wrote the line (a username, or "system" for a background writer).</summary>
    public string Actor { get; set; } = "";
    /// <summary><c>AuditSources</c> vocabulary.</summary>
    public string Source { get; set; } = "";
    /// <summary>The promotion this line came from (phase 4). No FK — the ledger outlives an offer.</summary>
    public int? OfferId { get; set; }
    /// <summary>The purchase this line came from (phase 3). No FK — the ledger outlives a payment row.</summary>
    public int? PaymentId { get; set; }
    public DateTime CreatedAt { get; set; }
}
