namespace ChatBranch.Domain;

// Buying prepaid AI credit (phase 3: Stripe Checkout top-ups) and promotions
// (phase 4: offers). Every credit these write goes through the ONE ledger writer,
// AiCredit.ApplyAsync — these tables only record what was bought and why.

/// <summary>The closed vocabulary of <see cref="AiCreditPayment.Status"/>.</summary>
public static class AiCreditPaymentStatuses
{
    /// <summary>A Checkout Session was (or is being) opened; nothing paid yet.</summary>
    public const string Created = "created";
    /// <summary>Stripe confirmed the payment and the credit was written.</summary>
    public const string Paid = "paid";
    /// <summary>Money was received but NOT credited — an amount, currency or mode
    /// mismatch (or a billing-currency change) needs a person to decide.</summary>
    public const string Review = "review";
    /// <summary>Stripe refused to open the session, or a delayed payment failed.</summary>
    public const string Failed = "failed";
    /// <summary>The Checkout Session expired unpaid.</summary>
    public const string Expired = "expired";
    /// <summary>Refunded in full (a partial refund keeps the row <see cref="Paid"/>).</summary>
    public const string Refunded = "refunded";
    /// <summary>The customer's bank opened a dispute (chargeback) for the whole remaining amount.</summary>
    public const string Disputed = "disputed";

    public static readonly string[] All = { Created, Paid, Review, Failed, Expired, Refunded, Disputed };
}

/// <summary>The closed vocabulary of <see cref="AiCreditOffer.Kind"/>.</summary>
public static class AiCreditOfferKinds
{
    /// <summary>A percentage on top of what is bought (<see cref="AiCreditOffer.BonusPct"/>).</summary>
    public const string BonusPct = "bonus_pct";
    /// <summary>A fixed bonus on top of what is bought (<see cref="AiCreditOffer.BonusFixed"/>).</summary>
    public const string BonusFixed = "bonus_fixed";
    /// <summary>Pay <see cref="AiCreditOffer.PayAmount"/>, receive <see cref="AiCreditOffer.CreditAmount"/>.</summary>
    public const string Bundle = "bundle";

    public static readonly string[] All = { BonusPct, BonusFixed, Bundle };
}

/// <summary>
/// One Stripe Checkout purchase of AI credit (<c>crm_aicreditpayment</c>).
///
/// <para><see cref="CreditAmount"/> and <see cref="BonusAmount"/> are decided
/// when the row is created and are NEVER re-derived from a webhook: the webhook
/// only proves that <see cref="AmountMinor"/> in <see cref="Currency"/> was paid
/// for <see cref="SessionId"/>, and then the row's own numbers are credited.</para>
/// </summary>
public class AiCreditPayment
{
    public int Id { get; set; }
    /// <summary>A server-generated id: the Checkout Session's client_reference_id and
    /// the Stripe Idempotency-Key, so a retried creation can never open two sessions.</summary>
    public string Uuid { get; set; } = "";
    /// <summary>The company (top-level entity) the credit lands on.</summary>
    public int EntityId { get; set; }
    /// <summary>The promotion applied (its redemption was claimed at creation). No FK.</summary>
    public int? OfferId { get; set; }
    public string Provider { get; set; } = "stripe";
    /// <summary>test | live — the configured Stripe mode when the session was opened.</summary>
    public string Mode { get; set; } = "test";
    /// <summary>The Checkout Session id (cs_…), unique where non-blank.</summary>
    public string SessionId { get; set; } = "";
    /// <summary>Learnt at completion: refunds and disputes are matched on it.</summary>
    public string PaymentIntentId { get; set; } = "";
    /// <summary>The caller's own retry key (scoped to the caller), unique where non-blank:
    /// a double-clicked Pay button returns the same session.</summary>
    public string IdempotencyKey { get; set; } = "";
    /// <summary>Where the buyer pays (Stripe-hosted). Kept so a retry returns it.</summary>
    public string CheckoutUrl { get; set; } = "";
    /// <summary>What Stripe charges, in the currency's minor unit (sen, cents).</summary>
    public int AmountMinor { get; set; }
    /// <summary>The billing currency at creation, upper-case.</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>The PAID part to credit (a <c>topup</c> ledger line).</summary>
    public double CreditAmount { get; set; }
    /// <summary>The promotional part to credit (a separate <c>promo_bonus</c> line).</summary>
    public double BonusAmount { get; set; }
    /// <summary><see cref="AiCreditPaymentStatuses"/>.</summary>
    public string Status { get; set; } = AiCreditPaymentStatuses.Created;
    /// <summary>Why the row is in its status (a Stripe error, a mismatch) — for people.</summary>
    public string StatusDetail { get; set; } = "";
    /// <summary>Minor units refunded so far (Stripe's cumulative amount_refunded, capped).</summary>
    public int RefundedMinor { get; set; }
    /// <summary>Minor units clawed back by disputes so far.</summary>
    public int DisputedMinor { get; set; }
    public int? CreatedByUserId { get; set; }
    public string CreatedBy { get; set; } = "";
    /// <summary>The last Stripe event that changed this row.</summary>
    public string LastEventId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// A promotion on AI credit (<c>crm_aicreditoffer</c>). The bonus it gives is
/// always written as its own <c>promo_bonus</c> ledger line, so what was paid
/// for and what was given away stay separately auditable.
/// </summary>
public class AiCreditOffer
{
    public int Id { get; set; }
    /// <summary>Stable key, unique where non-blank.</summary>
    public string Slug { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary><see cref="AiCreditOfferKinds"/>.</summary>
    public string Kind { get; set; } = AiCreditOfferKinds.BonusPct;
    /// <summary>bundle: what the buyer pays. bonus_pct / bonus_fixed: the minimum purchase (0 = none).</summary>
    public double PayAmount { get; set; }
    /// <summary>bundle: what the buyer receives in total (the part above PayAmount is the bonus).</summary>
    public double CreditAmount { get; set; }
    public double BonusPct { get; set; }
    public double BonusFixed { get; set; }
    /// <summary>The billing currency the amounts are in; the offer is unavailable in any other.</summary>
    public string Currency { get; set; } = "USD";
    public bool Active { get; set; } = true;
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    /// <summary>0 = unlimited. Redemptions are claimed with one conditional UPDATE, so this holds under concurrency.</summary>
    public int MaxRedemptions { get; set; }
    public int Redemptions { get; set; }
    /// <summary>Only this company (a top-level entity); null = every company.</summary>
    public int? EntityId { get; set; }
    public string Note { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Every Stripe webhook event seen once (<c>crm_stripeevent</c>, the
/// <c>crm_zernioevent</c> precedent). A crediting event is first CLAIMED (in the
/// same transaction that moves its payment row) and marked processed once its
/// ledger lines are written — so a crash in between is finished by Stripe's retry.
/// </summary>
public class StripeEvent
{
    public int Id { get; set; }
    /// <summary>evt_…, unique where non-blank.</summary>
    public string EventId { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Livemode { get; set; }
    /// <summary>claimed | processed | ignored.</summary>
    public string Status { get; set; } = "processed";
    public int? PaymentId { get; set; }
    /// <summary>For a refund / dispute: the minor units this event claws back (decided once, at claim).</summary>
    public int AmountMinor { get; set; }
    public string Detail { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
