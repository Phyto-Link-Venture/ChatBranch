namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    /// <summary>
    /// Buying AI credit and its promotions (AiCreditTopupEndpoints). Opening a Stripe
    /// checkout is deliberately NOT a tool (McpExclusions): it spends money at a
    /// browser a person pays in. Reading what may be bought, the offers, and granting
    /// an offer's bonus by hand (no money moves) are.
    /// </summary>
    private static IEnumerable<IChatBranchTool> CreditTopup()
    {
        const string an = "analytics";
        yield return T(an, "billing_get_ai_credit_topup", "AI credit top-up options",
            "What the caller may buy as prepaid AI credit: whether online top-ups are set up (and why not), the "
            + "Stripe mode (test | live), the billing currency, the minimum and maximum purchase, the companies the "
            + "caller may top up, the promotions open to them, and recent purchases with their status (created, "
            + "paid, review, failed, expired, refunded, disputed). Pass payment to read one purchase's status. "
            + "Paying happens in a browser on Stripe's checkout page, never through a tool.",
            "GET", "/api/billing/ai/credit/topup", C("billing.topup", "billing.manage"), new[]
            {
                A.Str("company", "Company (top-level entity) slug or id — only its offers and purchases."),
                A.Int("payment", "One purchase's id — its status."),
            }, link: (_, _) => Page("Open AI billing", "/billing"));
        yield return T(an, "billing_get_ai_credit_offers", "AI credit offers",
            "Every promotion on AI credit with its kind (bonus_pct = a percentage on top, bonus_fixed = a fixed "
            + "bonus, bundle = pay X get Y), amounts, window, redemptions used and left, the company it is "
            + "limited to, and why a buyer cannot pick it right now (unavailable).",
            "GET", "/api/billing/ai/credit/offers", C("billing.manage"),
            link: (_, _) => Page("Open AI billing settings", "/settings/billing"));
        yield return T(an, "billing_save_ai_credit_offer", "Create or change an AI credit offer",
            "Create a promotion on AI credit, or change one by slug (fields you leave out keep their value). "
            + "kind bonus_pct needs bonus_pct; bonus_fixed needs bonus_fixed; for both, pay_amount is an optional "
            + "minimum purchase. kind bundle needs pay_amount (the price) and credit_amount (what the buyer gets, "
            + "more than the price). Amounts are in the billing currency. max_redemptions 0 = unlimited. The bonus "
            + "is always written as its own promo_bonus ledger line.",
            "POST", "/api/billing/ai/credit/offers", C("billing.manage"), new[]
            {
                A.Str("label", "What buyers see, e.g. \"Launch week +10%\"."),
                A.Str("slug", "Stable key (default: from the label) — pass it to change an existing offer."),
                A.Str("kind", "bonus_pct | bonus_fixed | bundle.", oneOf: ChatBranch.Domain.AiCreditOfferKinds.All),
                A.Num("bonus_pct", "bonus_pct: the bonus as a percentage of the purchase."),
                A.Num("bonus_fixed", "bonus_fixed: the bonus amount."),
                A.Num("pay_amount", "bundle: the price. bonus offers: the minimum purchase (0 = none)."),
                A.Num("credit_amount", "bundle: the credit the buyer receives in total."),
                A.Bool("active", "False keeps the offer (and its history) but stops anyone using it."),
                A.Str("starts_at", "ISO date/time it opens (empty = now)."),
                A.Str("ends_at", "ISO date/time it closes (empty = never)."),
                A.Int("max_redemptions", "How many times it may be used in total (0 = unlimited)."),
                A.Str("entity", "Limit it to one company (top-level entity slug or id); empty = every company."),
                A.Str("note", "Why the offer exists / who agreed it."),
            }, idempotent: true);
        yield return T(an, "billing_delete_ai_credit_offer", "Delete an AI credit offer",
            "Delete a promotion nobody has used yet. One that has been redeemed is refused — deactivate it "
            + "instead (active=false), so the ledger and payments that name it keep their history.",
            "DELETE", "/api/billing/ai/credit/offers/{slug}", C("billing.manage"),
            new[] { A.Str("slug", "The offer's key.", req: true) }, destructive: true);
        yield return T(an, "billing_grant_ai_credit_offer", "Grant an offer's bonus by hand",
            "Hand a company an offer's bonus without a purchase: one promo_bonus ledger line, counted against the "
            + "offer's max_redemptions. A percentage offer needs amount (what the percentage applies to). The offer "
            + "must be open to that company now. Pass idempotency_key to make a retry safe.",
            "POST", "/api/billing/ai/credit/offers/{slug}/grant", C("billing.manage"), new[]
            {
                A.Str("slug", "The offer's key.", req: true),
                A.Str("entity", "Company (top-level entity) slug or id.", req: true),
                A.Num("amount", "bonus_pct offers: the amount the percentage applies to."),
                A.Str("note", "Why — kept on the ledger line and in the activity log."),
                A.Str("idempotency_key", "Your own key: a repeated call grants once."),
            }, idempotent: false);
    }
}
