using System.Globalization;
using System.Text.Json;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Buying prepaid AI credit with Stripe Checkout (phase 3) and the promotions that
/// add a bonus to a purchase (phase 4). Every balance move goes through
/// <see cref="AiCredit.ApplyAsync"/> — this class decides WHAT to credit and WHEN,
/// never touches a balance itself.
///
/// <para><b>Creating a top-up.</b> The server decides the amount charged and the
/// credit (and any bonus) up front and stores them on a
/// <see cref="AiCreditPayment"/> row BEFORE opening the Checkout Session; the
/// webhook only proves the payment and credits the row's own numbers.</para>
///
/// <para><b>The webhook — three independent replay guards</b>, all required:
/// (1) every ledger line carries <c>idempotency_key = "stripe:&lt;event id&gt;"</c>
/// (<c>uniq_aicreditentry_idem</c>); (2) the payment row moves
/// <c>created → paid</c> with one conditional UPDATE whose WHERE carries the
/// status, so only one delivery ever wins; (3) <c>session_id</c> is unique, so one
/// Checkout Session never backs two rows. On top, each event is recorded once in
/// <c>crm_stripeevent</c>: a crediting event is CLAIMED in the same transaction
/// that moves its payment row and marked processed once its ledger lines exist —
/// a crash in between is finished by Stripe's retry, never lost and never doubled.</para>
///
/// <para>Only <c>checkout.session.completed</c> (with <c>payment_status = paid</c>)
/// and <c>checkout.session.async_payment_succeeded</c> credit. Stripe also sends
/// <c>payment_intent.succeeded</c> for the same money; it is ignored, or every
/// top-up would be credited twice.</para>
/// </summary>
public static class AiCreditTopup
{
    /// <summary>The smallest and largest purchase, in the billing currency.</summary>
    public const double MinAmount = 5;
    public const double MaxAmount = 50_000;
    /// <summary>The amounts the top-up card offers as one-tap choices.</summary>
    public static readonly double[] Presets = { 50, 100, 200, 500 };
    /// <summary>The ledger/audit actor of everything the webhook writes.</summary>
    public const string WebhookActor = "stripe";
    /// <summary>How long a Checkout Session stays payable (Stripe allows 30 min – 24 h).
    /// Short, so an abandoned checkout releases a limited offer's redemption quickly.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);

    public const string EventClaimed = "claimed";
    public const string EventProcessed = "processed";
    public const string EventIgnored = "ignored";

    // ======================= offers ==========================================

    /// <summary>Why this offer cannot be used by this company now, or null when it can.</summary>
    public static string? Unavailable(AiCreditOffer o, int companyId, string currency, DateTime nowUtc)
    {
        if (!o.Active) return "This offer is not active.";
        if (o.StartsAt is DateTime s && nowUtc < s) return "This offer has not started yet.";
        if (o.EndsAt is DateTime e && nowUtc >= e) return "This offer has ended.";
        if (!string.Equals(o.Currency, currency, StringComparison.OrdinalIgnoreCase))
            return $"This offer is priced in {o.Currency}, but AI credit is sold in {currency}.";
        if (o.EntityId is int only && only != companyId) return "This offer is not available to this company.";
        if (o.MaxRedemptions > 0 && o.Redemptions >= o.MaxRedemptions) return "This offer has been fully redeemed.";
        return null;
    }

    /// <summary>Every redemption is taken (a 409, not a 400: the request was fine, the stock ran out).</summary>
    public static bool FullyRedeemed(AiCreditOffer o) => o.MaxRedemptions > 0 && o.Redemptions >= o.MaxRedemptions;

    /// <summary>What a purchase pays, credits as paid credit, and adds as a bonus.</summary>
    public sealed record Quote(double Pay, double Credit, double Bonus, string? Error = null)
    {
        public double Total => Credit + Bonus;
    }

    private static double Money2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The purchase of <paramref name="amount"/> with <paramref name="offer"/> applied.
    /// A bundle ignores the amount (its price is fixed); a bonus offer may need a minimum.
    /// </summary>
    public static Quote QuoteFor(AiCreditOffer? offer, double amount)
    {
        amount = Money2(amount);
        if (offer == null) return new Quote(amount, amount, 0);
        switch (offer.Kind)
        {
            case AiCreditOfferKinds.Bundle:
                return new Quote(Money2(offer.PayAmount), Money2(offer.PayAmount),
                    Money2(Math.Max(0, offer.CreditAmount - offer.PayAmount)));
            case AiCreditOfferKinds.BonusPct:
            case AiCreditOfferKinds.BonusFixed:
                if (offer.PayAmount > 0 && amount < offer.PayAmount)
                    return new Quote(amount, amount, 0,
                        $"This offer needs a purchase of at least {AiCredit.Money(offer.Currency, offer.PayAmount)}.");
                var bonus = offer.Kind == AiCreditOfferKinds.BonusPct
                    ? Money2(amount * offer.BonusPct / 100.0)
                    : Money2(offer.BonusFixed);
                return new Quote(amount, amount, bonus);
            default:
                return new Quote(amount, amount, 0, $"Unknown offer kind '{offer.Kind}'.");
        }
    }

    /// <summary>
    /// The bonus a billing manager hands out by hand for an offer (no purchase):
    /// a percentage offer needs the amount it is a percentage OF.
    /// </summary>
    public static Quote ManualBonus(AiCreditOffer offer, double baseAmount) => offer.Kind switch
    {
        AiCreditOfferKinds.BonusPct when baseAmount <= 0 => new Quote(0, 0, 0,
            "A percentage offer needs the amount it is a percentage of (amount)."),
        AiCreditOfferKinds.BonusPct => new Quote(0, 0, Money2(baseAmount * offer.BonusPct / 100.0)),
        AiCreditOfferKinds.BonusFixed => new Quote(0, 0, Money2(offer.BonusFixed)),
        AiCreditOfferKinds.Bundle => new Quote(0, 0, Money2(Math.Max(0, offer.CreditAmount - offer.PayAmount))),
        _ => new Quote(0, 0, 0, $"Unknown offer kind '{offer.Kind}'."),
    };

    /// <summary>
    /// Claim one redemption with ONE conditional UPDATE — the database decides, so
    /// <see cref="AiCreditOffer.MaxRedemptions"/> holds however many buyers race.
    /// </summary>
    public static async Task<bool> ReserveAsync(AppDbContext db, int offerId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await db.AiCreditOffers
            .Where(o => o.Id == offerId && o.Active && (o.MaxRedemptions == 0 || o.Redemptions < o.MaxRedemptions))
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Redemptions, o => o.Redemptions + 1)
                .SetProperty(o => o.UpdatedAt, now), ct) == 1;
    }

    /// <summary>Give a claimed redemption back (a checkout that expired, failed or was refused).</summary>
    public static Task<int> ReleaseAsync(AppDbContext db, int offerId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return db.AiCreditOffers.Where(o => o.Id == offerId && o.Redemptions > 0)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Redemptions, o => o.Redemptions - 1)
                .SetProperty(o => o.UpdatedAt, now), ct);
    }

    // ======================= creating a top-up ===============================

    public sealed record CreateRequest(
        int CompanyId,
        string CompanyName,
        double Amount,
        string? OfferSlug,
        string ClientKey,
        string SuccessUrl,
        string CancelUrl,
        int? UserId,
        string UserName);

    /// <summary>The row (with its checkout URL), or an error with the HTTP status to answer.</summary>
    public sealed record CreateResult(AiCreditPayment? Payment, string? Error, int Status, bool Replayed = false)
    {
        public bool Ok => Error == null;
    }

    private static CreateResult Fail(int status, string error) => new(null, error, status);

    /// <summary>Why online top-ups cannot be offered right now, or null when they can.</summary>
    public static string? NotReady(ScopingService sc)
    {
        var secret = StripeConfig.SecretKey(sc);
        var mode = StripeConfig.Mode(sc);
        if (secret.Length == 0)
            return "Online top-ups are not set up: the platform's Stripe secret key (STRIPE_SECRET_KEY) is not configured.";
        if (!StripeConfig.KeyMatchesMode(secret, mode))
            return $"The configured Stripe key is not a {mode}-mode key (STRIPE_MODE={mode}) — top-ups are refused until they agree.";
        if (StripeConfig.WebhookSecret(sc).Length == 0)
            return "Online top-ups are not set up: the Stripe webhook signing secret (STRIPE_WEBHOOK_SECRET) is not "
                   + "configured, so a payment could never be credited.";
        if (StripeConfig.MinorFactor(AiBilling.Load(sc).Currency) == null)
            return $"Stripe top-ups do not support the billing currency {AiBilling.Load(sc).Currency}.";
        return null;
    }

    /// <summary>
    /// Open a Stripe Checkout Session for a company's top-up. In order: refuse when
    /// Stripe is not configured or the key's mode disagrees; answer a retried request
    /// (same caller + same key) with its first session; check the currency is the
    /// billing currency and the balance's; validate the amount and the offer; claim
    /// the offer's redemption; INSERT the row with the credit decided now; then call
    /// Stripe with the row's uuid as its Idempotency-Key.
    /// </summary>
    public static async Task<CreateResult> CreateAsync(AppDbContext db, ScopingService sc, IStripeGateway stripe,
        CreateRequest r, CancellationToken ct = default)
    {
        if (NotReady(sc) is string notReady) return Fail(503, notReady);
        var secret = StripeConfig.SecretKey(sc);
        var mode = StripeConfig.Mode(sc);
        var now = DateTime.UtcNow;

        var clientKey = (r.ClientKey ?? "").Trim();
        if (clientKey.Length > 200) return Fail(400, "The Idempotency-Key is too long (200 characters at most).");
        // scoped to the caller: two people can never collide on a key
        var storedKey = clientKey.Length == 0
            ? ""
            : $"u{(r.UserId?.ToString(CultureInfo.InvariantCulture) ?? AiCredit.NormalizeUser(r.UserName))}:{clientKey}";
        if (storedKey.Length > 0
            && await db.AiCreditPayments.AsNoTracking().FirstOrDefaultAsync(p => p.IdempotencyKey == storedKey, ct)
                is AiCreditPayment earlier)
            return Replay(earlier, r.CompanyId);

        var currency = AiBilling.Load(sc).Currency.Trim().ToUpperInvariant();
        var factor = StripeConfig.MinorFactor(currency)!.Value;
        var account = await db.AiCreditAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Scope == AiCreditScopes.Entity && a.EntityId == r.CompanyId, ct);
        if (account != null && !string.Equals(account.Currency, currency, StringComparison.OrdinalIgnoreCase))
            return Fail(409, $"{r.CompanyName}'s credit is held in {account.Currency}, but the billing currency is now "
                             + $"{currency}. A currency change does not convert credit, so top-ups are refused until "
                             + "a billing manager converts the balance or sets the currency back.");

        AiCreditOffer? offer = null;
        var slug = (r.OfferSlug ?? "").Trim();
        if (slug.Length > 0)
        {
            offer = await db.AiCreditOffers.AsNoTracking().FirstOrDefaultAsync(o => o.Slug == slug, ct);
            if (offer == null) return Fail(400, $"Unknown offer '{slug}'.");
            if (Unavailable(offer, r.CompanyId, currency, now) is string why) return Fail(FullyRedeemed(offer) ? 409 : 400, why);
        }
        var amount = factor == 1 ? Math.Round(r.Amount, 0, MidpointRounding.AwayFromZero) : r.Amount;
        var quote = QuoteFor(offer, amount);
        if (quote.Error != null) return Fail(400, quote.Error);
        if (!double.IsFinite(quote.Pay) || quote.Pay < MinAmount || quote.Pay > MaxAmount)
            return Fail(400, $"A top-up is between {AiCredit.Money(currency, MinAmount)} and {AiCredit.Money(currency, MaxAmount)}.");
        var minor = (int)Math.Round(quote.Pay * factor, MidpointRounding.AwayFromZero);

        if (offer != null && !await ReserveAsync(db, offer.Id, ct))
            return Fail(409, "This offer has just been fully redeemed.");

        var row = new AiCreditPayment
        {
            Uuid = Guid.NewGuid().ToString("N"),
            EntityId = r.CompanyId,
            OfferId = offer?.Id,
            Provider = "stripe",
            Mode = mode,
            IdempotencyKey = storedKey,
            AmountMinor = minor,
            Currency = currency,
            // credit exactly what is charged (a bundle: its price), decided NOW
            CreditAmount = AiCredit.Round(minor / (double)factor),
            BonusAmount = AiCredit.Round(quote.Bonus),
            Status = AiCreditPaymentStatuses.Created,
            CreatedByUserId = r.UserId,
            CreatedBy = r.UserName ?? "",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AiCreditPayments.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (storedKey.Length > 0)
        {
            // the same key raced in twice: the first row wins
            db.ChangeTracker.Clear();
            if (offer != null) await ReleaseAsync(db, offer.Id, ct);
            var won = await db.AiCreditPayments.AsNoTracking().FirstOrDefaultAsync(p => p.IdempotencyKey == storedKey, ct);
            if (won != null) return Replay(won, r.CompanyId);
            throw;
        }
        db.Entry(row).State = EntityState.Detached;
        return await OpenSessionAsync(db, stripe, row, offer, r, secret, ct);
    }

    private static CreateResult Replay(AiCreditPayment p, int companyId)
    {
        if (p.EntityId != companyId)
            return Fail(409, "This Idempotency-Key was already used for a top-up of another company.");
        if (p.Status == AiCreditPaymentStatuses.Created && p.CheckoutUrl.Length > 0)
            return new CreateResult(p, null, 200, Replayed: true);
        if (p.Status == AiCreditPaymentStatuses.Created)
            return Fail(409, "This top-up is still being opened, or Stripe did not answer — start a new top-up.");
        return Fail(409, $"This top-up is already {p.Status}.");
    }

    private static string WithQuery(string url, string key, string value)
    {
        var sep = url.Contains('?') ? "&" : "?";
        return $"{url}{sep}{key}={Uri.EscapeDataString(value)}";
    }

    private static async Task<CreateResult> OpenSessionAsync(AppDbContext db, IStripeGateway stripe,
        AiCreditPayment row, AiCreditOffer? offer, CreateRequest r, string secret, CancellationToken ct)
    {
        var id = row.Id.ToString(CultureInfo.InvariantCulture);
        var name = $"AI credit — {r.CompanyName}";
        var form = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            // the server's uuid, not the row id: demo and prod may share one Stripe account
            ["client_reference_id"] = row.Uuid,
            ["success_url"] = WithQuery(r.SuccessUrl, "payment", id),
            ["cancel_url"] = WithQuery(r.CancelUrl, "payment", id),
            ["expires_at"] = new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc) + SessionLifetime)
                .ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = row.Currency.ToLowerInvariant(),
            ["line_items[0][price_data][unit_amount]"] = row.AmountMinor.ToString(CultureInfo.InvariantCulture),
            ["line_items[0][price_data][product_data][name]"] = name.Length <= 200 ? name : name[..200],
            ["metadata[payment_uuid]"] = row.Uuid,
            ["metadata[payment_id]"] = id,
            ["metadata[entity_id]"] = row.EntityId.ToString(CultureInfo.InvariantCulture),
            ["payment_intent_data[metadata][payment_uuid]"] = row.Uuid,
        };
        if (offer != null) form["metadata[offer_id]"] = offer.Id.ToString(CultureInfo.InvariantCulture);
        if (row.BonusAmount > 0)
            form["line_items[0][price_data][product_data][description]"] =
                $"Includes a {AiCredit.Money(row.Currency, row.BonusAmount)} bonus" + (offer != null ? $" ({offer.Label})" : "");

        var res = await stripe.CreateCheckoutSessionAsync(secret, form, "chatbranch-topup-" + row.Uuid, ct);
        var sessionId = res.Ok ? ProviderHttp.JsonField(res.Body, "id") : "";
        var url = res.Ok ? ProviderHttp.JsonField(res.Body, "url") : "";
        var now = DateTime.UtcNow;
        if (!res.Ok || sessionId.Length == 0 || url.Length == 0)
        {
            var detail = res.Ok ? "Stripe answered without a session id or URL." : res.Body;
            if (res.Refused)
            {
                // Stripe refused the request: nothing was created, so the row is over
                await db.AiCreditPayments.Where(p => p.Id == row.Id && p.Status == AiCreditPaymentStatuses.Created)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.Status, AiCreditPaymentStatuses.Failed)
                        .SetProperty(p => p.StatusDetail, AuditTrail.Cap(detail, 500))
                        .SetProperty(p => p.UpdatedAt, now), ct);
                if (row.OfferId is int oid) await ReleaseAsync(db, oid, ct);
            }
            else
            {
                // no answer: the session MAY exist — keep the row open; if it is paid, the
                // webhook still finds it by client_reference_id (the uuid)
                await db.AiCreditPayments.Where(p => p.Id == row.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.StatusDetail, AuditTrail.Cap("Stripe did not answer: " + detail, 500))
                        .SetProperty(p => p.UpdatedAt, now), ct);
            }
            AuditTrail.Write(db, "billing.credit.topup.failed", $"payment:{id}",
                $"Stripe could not open a checkout for {AiCredit.Money(row.Currency, row.CreditAmount)} of AI credit for "
                + $"{r.CompanyName}: {detail}", entityId: row.EntityId);
            return Fail(502, $"Stripe could not open the checkout: {detail}");
        }

        await db.AiCreditPayments.Where(p => p.Id == row.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.SessionId, sessionId)
                .SetProperty(p => p.CheckoutUrl, url)
                .SetProperty(p => p.StatusDetail, "")
                .SetProperty(p => p.UpdatedAt, now), ct);
        AuditTrail.Write(db, "billing.credit.topup.created", $"payment:{id}",
            $"Opened a {row.Mode}-mode Stripe checkout: pay {AiCredit.Money(row.Currency, row.AmountMinor / (double)(StripeConfig.MinorFactor(row.Currency) ?? 100))} "
            + $"for {AiCredit.Money(row.Currency, row.CreditAmount)} of AI credit"
            + (row.BonusAmount > 0 ? $" + a {AiCredit.Money(row.Currency, row.BonusAmount)} bonus ({offer?.Label})" : "")
            + $" for {r.CompanyName} ({sessionId})", entityId: row.EntityId);
        var fresh = await db.AiCreditPayments.AsNoTracking().FirstAsync(p => p.Id == row.Id, ct);
        return new CreateResult(fresh, null, 200);
    }

    // ======================= the webhook =====================================

    /// <summary>What the webhook endpoint answers Stripe.</summary>
    public sealed record WebhookResult(int Status, object Body);

    private static WebhookResult Ok(object body) => new(200, body);

    private static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.ToString(),
                // an expanded object ({id: …}) — e.g. payment_intent when expanded
                JsonValueKind.Object => v.TryGetProperty("id", out var inner) && inner.ValueKind == JsonValueKind.String
                    ? inner.GetString() ?? "" : "",
                _ => "",
            }
            : "";

    private static long Long(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) ? n : -1;

    /// <summary>
    /// Handle one VERIFIED Stripe event (the caller has checked the signature).
    /// Returns 200 for everything handled or deliberately ignored (so Stripe stops
    /// retrying), 400 for a malformed event or a livemode mismatch, and lets an
    /// exception escape (→ 500, Stripe retries) when the database fails.
    /// </summary>
    public static async Task<WebhookResult> HandleWebhookAsync(AppDbContext db, ScopingService sc, byte[] raw,
        CancellationToken ct = default)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { return new WebhookResult(400, new { detail = "malformed event" }); }
        using (doc)
        {
            var root = doc.RootElement;
            var evt = Str(root, "id");
            var type = Str(root, "type");
            if (evt.Length == 0 || type.Length == 0)
                return new WebhookResult(400, new { detail = "an event needs an id and a type" });
            var livemode = root.TryGetProperty("livemode", out var lm) && lm.ValueKind == JsonValueKind.True;
            var mode = StripeConfig.Mode(sc);

            // a live event can never credit a test install, nor a test event a live one
            if (livemode != (mode == StripeConfig.Live))
            {
                AuditTrail.Write(db, "billing.credit.stripe.refused", $"stripe:{evt}",
                    $"Refused a {(livemode ? "live" : "test")}-mode {type} event: this install is configured for "
                    + $"{mode} mode (STRIPE_MODE). Nothing was credited.",
                    actor: WebhookActor, source: AuditSources.Webhook);
                return new WebhookResult(400, new { detail = $"livemode mismatch: this endpoint accepts {mode}-mode events only" });
            }

            var seen = await db.StripeEvents.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == evt, ct);
            if (seen != null && seen.Status != EventClaimed) return Ok(new { ok = true, duplicate = true });
            if (seen != null) return await ResumeAsync(db, sc, seen, ct);

            var obj = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                      && data.TryGetProperty("object", out var o) ? o : default;
            return type switch
            {
                "checkout.session.completed" or "checkout.session.async_payment_succeeded" =>
                    await CompleteAsync(db, sc, evt, type, livemode, obj, ct),
                "checkout.session.expired" => await CloseAsync(db, evt, type, livemode, obj, AiCreditPaymentStatuses.Expired, ct),
                "checkout.session.async_payment_failed" => await CloseAsync(db, evt, type, livemode, obj, AiCreditPaymentStatuses.Failed, ct),
                "charge.refunded" => await ReverseAsync(db, sc, evt, type, livemode, obj, dispute: false, ct),
                "charge.dispute.created" => await ReverseAsync(db, sc, evt, type, livemode, obj, dispute: true, ct),
                // payment_intent.succeeded and everything else: seen, deliberately not acted on
                _ => await IgnoreAsync(db, evt, type, livemode, "not an event AI credit acts on", ct),
            };
        }
    }

    private static async Task<WebhookResult> IgnoreAsync(AppDbContext db, string evt, string type, bool livemode,
        string why, CancellationToken ct)
    {
        db.StripeEvents.Add(new StripeEvent
        {
            EventId = evt, Type = type, Livemode = livemode, Status = EventIgnored, Detail = why,
            ReceivedAt = DateTime.UtcNow, ProcessedAt = DateTime.UtcNow,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); /* recorded by a concurrent delivery */ }
        return Ok(new { ok = true, ignored = why });
    }

    private static async Task MarkProcessedAsync(AppDbContext db, string evt, string detail, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var d = AuditTrail.Cap(detail, 500);
        await db.StripeEvents.Where(e => e.EventId == evt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, EventProcessed)
                .SetProperty(e => e.Detail, d)
                .SetProperty(e => e.ProcessedAt, now), ct);
    }

    /// <summary>The top-up behind a Checkout Session: by session id, else (a session whose
    /// creation answer was lost) by its client_reference_id = the row's uuid.</summary>
    private static async Task<AiCreditPayment?> FindSessionAsync(AppDbContext db, string sessionId, string reference,
        CancellationToken ct)
    {
        if (sessionId.Length > 0
            && await db.AiCreditPayments.AsNoTracking().FirstOrDefaultAsync(p => p.SessionId == sessionId, ct)
                is AiCreditPayment bySession)
            return bySession;
        if (reference.Length == 0) return null;
        return await db.AiCreditPayments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Uuid == reference && p.SessionId == "", ct);
    }

    /// <summary>Another delivery of the same event claimed it first: finish it, or say it is done.</summary>
    private static async Task<WebhookResult> AfterRaceAsync(AppDbContext db, ScopingService sc, string evt,
        CancellationToken ct)
    {
        var seen = await db.StripeEvents.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == evt, ct);
        if (seen == null) return new WebhookResult(500, new { detail = "event record conflict — retry" });
        if (seen.Status == EventClaimed) return await ResumeAsync(db, sc, seen, ct);
        return Ok(new { ok = true, duplicate = true });
    }

    /// <summary>A claimed event whose ledger lines may not all exist yet: write them (idempotently).</summary>
    private static async Task<WebhookResult> ResumeAsync(AppDbContext db, ScopingService sc, StripeEvent seen,
        CancellationToken ct)
    {
        if (seen.PaymentId is not int pid)
        {
            await MarkProcessedAsync(db, seen.EventId, "nothing to finish", ct);
            return Ok(new { ok = true, duplicate = true });
        }
        switch (seen.Type)
        {
            case "charge.refunded":
                return await DebitAsync(db, pid, seen.EventId, dispute: false, seen.AmountMinor, ct);
            case "charge.dispute.created":
                return await DebitAsync(db, pid, seen.EventId, dispute: true, seen.AmountMinor, ct);
            default:
                var row = await db.AiCreditPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct);
                if (row != null && row.Status == AiCreditPaymentStatuses.Paid && row.LastEventId == seen.EventId)
                    return await CreditAsync(db, pid, seen.EventId, ct);
                await MarkProcessedAsync(db, seen.EventId, $"payment {row?.Status ?? "missing"} — nothing to credit", ct);
                return Ok(new { ok = true, duplicate = true });
        }
    }

    private static string CompanyName(AppDbContext db, int entityId) =>
        db.Entities.AsNoTracking().Where(e => e.Id == entityId).Select(e => e.Name).FirstOrDefault()
        ?? $"#{entityId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A paid Checkout Session: verify it against the row, flip the row, credit it.</summary>
    private static async Task<WebhookResult> CompleteAsync(AppDbContext db, ScopingService sc, string evt, string type,
        bool livemode, JsonElement obj, CancellationToken ct)
    {
        var sessionId = Str(obj, "id");
        var row = await FindSessionAsync(db, sessionId, Str(obj, "client_reference_id"), ct);
        // the AI payment-link skill opens Checkout Sessions on the same account — not ours
        if (row == null) return await IgnoreAsync(db, evt, type, livemode, "not an AI-credit top-up session", ct);
        var paymentStatus = Str(obj, "payment_status");
        if (paymentStatus != "paid")
            return await IgnoreAsync(db, evt, type, livemode,
                $"payment not settled yet (payment_status={paymentStatus}) — credited on async_payment_succeeded", ct);

        // verify: the row's mode, the amount + currency Stripe charged, and that the
        // credit can still land (billing currency + the balance's currency unchanged)
        var amountTotal = Long(obj, "amount_total");
        var currency = Str(obj, "currency").ToUpperInvariant();
        string? problem = null;
        if (row.Mode != (livemode ? StripeConfig.Live : StripeConfig.Test))
            problem = $"a {(livemode ? "live" : "test")}-mode payment for a top-up opened in {row.Mode} mode";
        else if (amountTotal != row.AmountMinor || currency != row.Currency)
            problem = $"Stripe charged {currency} {amountTotal} (minor units) but the top-up was opened for "
                      + $"{row.Currency} {row.AmountMinor}";
        else
        {
            var billing = AiBilling.Load(sc).Currency.Trim().ToUpperInvariant();
            var account = await db.AiCreditAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Scope == AiCreditScopes.Entity && a.EntityId == row.EntityId, ct);
            if (billing != row.Currency)
                problem = $"the billing currency changed from {row.Currency} to {billing} after the top-up was opened "
                          + "(a currency change does not convert credit)";
            else if (account != null && !string.Equals(account.Currency, row.Currency, StringComparison.OrdinalIgnoreCase))
                problem = $"the company's credit is held in {account.Currency}, the payment is in {row.Currency}";
        }

        var now = DateTime.UtcNow;
        var target = problem == null ? AiCreditPaymentStatuses.Paid : AiCreditPaymentStatuses.Review;
        var detail = problem == null ? "" : "Paid but NOT credited: " + problem + ".";
        var pi = Str(obj, "payment_intent");
        var adopt = row.SessionId.Length > 0 ? row.SessionId : sessionId;

        // CLAIM: the event row and the created → paid flip commit together
        int flipped;
        StripeEvent ev;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            ev = new StripeEvent
            {
                EventId = evt, Type = type, Livemode = livemode, Status = EventClaimed, PaymentId = row.Id,
                Detail = AuditTrail.Cap(detail, 500), ReceivedAt = now,
            };
            db.StripeEvents.Add(ev);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return await AfterRaceAsync(db, sc, evt, ct);
            }
            flipped = await db.AiCreditPayments
                .Where(p => p.Id == row.Id && p.Status == AiCreditPaymentStatuses.Created)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, target)
                    .SetProperty(p => p.StatusDetail, AuditTrail.Cap(detail, 500))
                    .SetProperty(p => p.SessionId, adopt)
                    .SetProperty(p => p.PaymentIntentId, pi)
                    .SetProperty(p => p.LastEventId, evt)
                    .SetProperty(p => p.CompletedAt, now)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            if (flipped == 0 || problem != null)
            {
                var current = flipped == 0
                    ? await db.AiCreditPayments.AsNoTracking().Where(p => p.Id == row.Id).Select(p => p.Status).FirstAsync(ct)
                    : target;
                ev.Status = EventProcessed;
                ev.ProcessedAt = now;
                ev.Detail = AuditTrail.Cap(flipped == 0 ? $"payment already {current} — not credited again" : detail, 500);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        if (flipped == 0) return Ok(new { ok = true, duplicate = true });
        if (problem != null)
        {
            var who = CompanyName(db, row.EntityId);
            AuditTrail.Write(db, "billing.credit.topup.review", $"payment:{row.Id}",
                $"Stripe payment {adopt} for {who} was received but NOT credited: {problem}. Refund it in Stripe or "
                + "grant the credit by hand.", actor: WebhookActor, source: AuditSources.Webhook, entityId: row.EntityId);
            NotifyReview(db, row, who, problem);
            return Ok(new { ok = true, review = problem });
        }
        return await CreditAsync(db, row.Id, evt, ct);
    }

    /// <summary>Write a paid top-up's ledger lines: the paid part, then the bonus as its own line.</summary>
    private static async Task<WebhookResult> CreditAsync(AppDbContext db, int paymentId, string evt, CancellationToken ct)
    {
        var row = await db.AiCreditPayments.AsNoTracking().FirstAsync(p => p.Id == paymentId, ct);
        var offer = row.OfferId is int oid
            ? await db.AiCreditOffers.AsNoTracking().FirstOrDefaultAsync(o => o.Id == oid, ct)
            : null;
        var refId = $"payment:{row.Id.ToString(CultureInfo.InvariantCulture)}";
        var paid = await AiCredit.ApplyAsync(db, new AiCredit.Request(AiCreditScopes.Entity, row.EntityId, "",
            AiCreditKinds.Topup, row.CreditAmount, row.Currency,
            Note: $"Stripe top-up ({row.Mode})", Ref: refId, IdempotencyKey: $"stripe:{evt}",
            Actor: WebhookActor, Source: AuditSources.Webhook, OfferId: row.OfferId, PaymentId: row.Id), ct);
        AiCredit.Result? bonus = null;
        if (paid.Ok && row.BonusAmount > 0)
            bonus = await AiCredit.ApplyAsync(db, new AiCredit.Request(AiCreditScopes.Entity, row.EntityId, "",
                AiCreditKinds.PromoBonus, row.BonusAmount, row.Currency,
                Note: "Promotion bonus" + (offer != null ? $": {offer.Label}" : ""), Ref: refId,
                IdempotencyKey: $"stripe:{evt}:bonus", Actor: WebhookActor, Source: AuditSources.Webhook,
                OfferId: row.OfferId, PaymentId: row.Id), ct);

        var who = CompanyName(db, row.EntityId);
        var error = !paid.Ok ? paid.Error : bonus is { Ok: false } ? bonus.Error : null;
        if (error != null)
        {
            var now = DateTime.UtcNow;
            var detail = AuditTrail.Cap("Paid but NOT (fully) credited: " + error, 500);
            await db.AiCreditPayments.Where(p => p.Id == row.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, AiCreditPaymentStatuses.Review)
                    .SetProperty(p => p.StatusDetail, detail)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            AuditTrail.Write(db, "billing.credit.topup.review", $"payment:{row.Id}",
                $"Stripe payment {row.SessionId} for {who} was received but could not be credited: {error}",
                actor: WebhookActor, source: AuditSources.Webhook, entityId: row.EntityId);
            NotifyReview(db, row, who, error);
            await MarkProcessedAsync(db, evt, detail, ct);
            return Ok(new { ok = true, review = error });
        }

        if (!paid.Replayed || bonus is { Replayed: false })
            AuditTrail.Write(db, "billing.credit.topup.paid", $"payment:{row.Id}",
                $"Credited {AiCredit.Money(row.Currency, row.CreditAmount)} of AI credit"
                + (row.BonusAmount > 0
                    ? $" + a {AiCredit.Money(row.Currency, row.BonusAmount)} bonus ({offer?.Label ?? "offer"})" : "")
                + $" to {who} — Stripe {row.Mode} payment {row.SessionId}, balance "
                + AiCredit.Money(row.Currency, (bonus?.Entry ?? paid.Entry)!.BalanceAfter),
                actor: WebhookActor, source: AuditSources.Webhook, entityId: row.EntityId);
        await MarkProcessedAsync(db, evt, "credited", ct);
        return Ok(new { ok = true, credited = row.CreditAmount, bonus = row.BonusAmount, payment = row.Id });
    }

    /// <summary>An unpaid Checkout Session ended (expired, or a delayed payment failed): close the row
    /// and give back the offer's redemption.</summary>
    private static async Task<WebhookResult> CloseAsync(AppDbContext db, string evt, string type, bool livemode,
        JsonElement obj, string status, CancellationToken ct)
    {
        var sessionId = Str(obj, "id");
        var row = await FindSessionAsync(db, sessionId, Str(obj, "client_reference_id"), ct);
        if (row == null) return await IgnoreAsync(db, evt, type, livemode, "not an AI-credit top-up session", ct);
        var now = DateTime.UtcNow;
        var adopt = row.SessionId.Length > 0 ? row.SessionId : sessionId;
        var detail = status == AiCreditPaymentStatuses.Expired
            ? "The checkout expired unpaid." : "The delayed payment failed.";
        int flipped;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            db.StripeEvents.Add(new StripeEvent
            {
                EventId = evt, Type = type, Livemode = livemode, Status = EventProcessed, PaymentId = row.Id,
                Detail = detail, ReceivedAt = now, ProcessedAt = now,
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return Ok(new { ok = true, duplicate = true });
            }
            flipped = await db.AiCreditPayments
                .Where(p => p.Id == row.Id && p.Status == AiCreditPaymentStatuses.Created)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, status)
                    .SetProperty(p => p.StatusDetail, detail)
                    .SetProperty(p => p.SessionId, adopt)
                    .SetProperty(p => p.LastEventId, evt)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            if (flipped == 1 && row.OfferId is int oid) await ReleaseAsync(db, oid, ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        if (flipped == 1)
            AuditTrail.Write(db, $"billing.credit.topup.{status}", $"payment:{row.Id}",
                $"{detail} {AiCredit.Money(row.Currency, row.CreditAmount)} of AI credit for {CompanyName(db, row.EntityId)} "
                + $"was not bought ({adopt}).", actor: WebhookActor, source: AuditSources.Webhook, entityId: row.EntityId);
        return Ok(new { ok = true, closed = flipped == 1 ? status : null });
    }

    /// <summary>
    /// A refund (<c>charge.refunded</c>, cumulative <c>amount_refunded</c>) or a dispute
    /// (<c>charge.dispute.created</c>): claim how many minor units this event takes back —
    /// capped so refunds + disputes never exceed what was paid — then debit that share of
    /// the credit AND of the bonus as NEW negative ledger lines. The original top-up line
    /// is never edited or deleted.
    /// </summary>
    private static async Task<WebhookResult> ReverseAsync(AppDbContext db, ScopingService sc, string evt, string type,
        bool livemode, JsonElement obj, bool dispute, CancellationToken ct)
    {
        var pi = Str(obj, "payment_intent");
        var row = pi.Length == 0
            ? null
            : await db.AiCreditPayments.AsNoTracking().FirstOrDefaultAsync(p => p.PaymentIntentId == pi, ct);
        if (row == null) return await IgnoreAsync(db, evt, type, livemode, "not an AI-credit top-up payment", ct);
        var reported = Long(obj, dispute ? "amount" : "amount_refunded");
        if (reported < 0) return await IgnoreAsync(db, evt, type, livemode, "no amount on the event", ct);

        var now = DateTime.UtcNow;
        int delta;
        bool debit;
        string status;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var ev = new StripeEvent
            {
                EventId = evt, Type = type, Livemode = livemode, Status = EventClaimed, PaymentId = row.Id,
                ReceivedAt = now,
            };
            db.StripeEvents.Add(ev);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return await AfterRaceAsync(db, sc, evt, ct);
            }

            var fresh = await db.AiCreditPayments.AsNoTracking().FirstAsync(p => p.Id == row.Id, ct);
            int refunded = fresh.RefundedMinor, disputed = fresh.DisputedMinor;
            if (dispute)
            {
                delta = (int)Math.Clamp(reported, 0, Math.Max(0, fresh.AmountMinor - refunded - disputed));
                disputed += delta;
            }
            else
            {
                var target = (int)Math.Clamp(reported, 0, Math.Max(0, fresh.AmountMinor - disputed));
                delta = Math.Max(0, target - refunded);
                refunded += delta;
            }
            // only what was credited can be debited: a payment held for review never was
            var wasCredited = fresh.Status is AiCreditPaymentStatuses.Paid or AiCreditPaymentStatuses.Refunded
                or AiCreditPaymentStatuses.Disputed;
            status = fresh.Status;
            if (delta > 0 && refunded + disputed >= fresh.AmountMinor)
                status = dispute ? AiCreditPaymentStatuses.Disputed : AiCreditPaymentStatuses.Refunded;
            if (delta > 0)
            {
                var n = await db.AiCreditPayments
                    .Where(p => p.Id == fresh.Id && p.RefundedMinor == fresh.RefundedMinor
                                && p.DisputedMinor == fresh.DisputedMinor)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.RefundedMinor, refunded)
                        .SetProperty(p => p.DisputedMinor, disputed)
                        .SetProperty(p => p.Status, status)
                        .SetProperty(p => p.LastEventId, evt)
                        .SetProperty(p => p.UpdatedAt, now), ct);
                if (n == 0)
                {
                    await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    return new WebhookResult(500, new { detail = "the payment changed concurrently — retry" });
                }
            }
            debit = delta > 0 && wasCredited;
            ev.AmountMinor = delta;
            if (!debit)
            {
                ev.Status = EventProcessed;
                ev.ProcessedAt = now;
                ev.Detail = delta == 0 ? "nothing new to take back" : "the payment was never credited — nothing debited";
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        if (!debit)
        {
            if (delta > 0)
                AuditTrail.Write(db, dispute ? "billing.credit.chargeback" : "billing.credit.refund", $"payment:{row.Id}",
                    $"Stripe {(dispute ? "dispute" : "refund")} on payment {row.SessionId} for "
                    + $"{CompanyName(db, row.EntityId)}: it was never credited (status {row.Status}), so nothing was debited.",
                    actor: WebhookActor, source: AuditSources.Webhook, entityId: row.EntityId);
            return Ok(new { ok = true, debited = 0 });
        }
        return await DebitAsync(db, row.Id, evt, dispute, delta, ct);
    }

    private static async Task<WebhookResult> DebitAsync(AppDbContext db, int paymentId, string evt, bool dispute,
        int delta, CancellationToken ct)
    {
        var row = await db.AiCreditPayments.AsNoTracking().FirstAsync(p => p.Id == paymentId, ct);
        if (delta <= 0 || row.AmountMinor <= 0)
        {
            await MarkProcessedAsync(db, evt, "nothing to take back", ct);
            return Ok(new { ok = true, debited = 0 });
        }
        var share = delta / (double)row.AmountMinor;
        var paidPart = AiCredit.Round(row.CreditAmount * share);
        var bonusPart = AiCredit.Round(row.BonusAmount * share);
        var kind = dispute ? AiCreditKinds.Chargeback : AiCreditKinds.Refund;
        var what = dispute ? "Stripe dispute opened" : "Stripe refund";
        var refId = $"payment:{row.Id.ToString(CultureInfo.InvariantCulture)}";
        var r1 = await AiCredit.ApplyAsync(db, new AiCredit.Request(AiCreditScopes.Entity, row.EntityId, "",
            kind, -paidPart, row.Currency, Note: what, Ref: refId, IdempotencyKey: $"stripe:{evt}",
            Actor: WebhookActor, Source: AuditSources.Webhook, OfferId: row.OfferId, PaymentId: row.Id), ct);
        AiCredit.Result? r2 = null;
        if (r1.Ok && bonusPart > 0)
            r2 = await AiCredit.ApplyAsync(db, new AiCredit.Request(AiCreditScopes.Entity, row.EntityId, "",
                kind, -bonusPart, row.Currency, Note: what + " — the promotion bonus on the returned part",
                Ref: refId, IdempotencyKey: $"stripe:{evt}:bonus", Actor: WebhookActor, Source: AuditSources.Webhook,
                OfferId: row.OfferId, PaymentId: row.Id), ct);

        var who = CompanyName(db, row.EntityId);
        var error = !r1.Ok ? r1.Error : r2 is { Ok: false } ? r2.Error : null;
        var action = dispute ? "billing.credit.chargeback" : "billing.credit.refund";
        if (error != null)
        {
            AuditTrail.Write(db, action + ".failed", $"payment:{row.Id}",
                $"{what} on payment {row.SessionId} for {who} could not be debited from the credit: {error}",
                actor: WebhookActor, source: AuditSources.Webhook, entityId: row.EntityId);
            NotifyReview(db, row, who, $"{what} could not be debited: {error}");
            await MarkProcessedAsync(db, evt, "not debited: " + error, ct);
            return Ok(new { ok = true, review = error });
        }
        if (!r1.Replayed || r2 is { Replayed: false })
            AuditTrail.Write(db, action, $"payment:{row.Id}",
                $"{what}: took back {AiCredit.Money(row.Currency, paidPart)} of AI credit"
                + (bonusPart > 0 ? $" and {AiCredit.Money(row.Currency, bonusPart)} of its bonus" : "")
                + $" from {who} (payment {row.SessionId}) — balance "
                + AiCredit.Money(row.Currency, (r2?.Entry ?? r1.Entry)!.BalanceAfter),
                actor: WebhookActor, source: AuditSources.Webhook, entityId: row.EntityId);
        await MarkProcessedAsync(db, evt, $"debited {paidPart} + bonus {bonusPart}", ct);
        return Ok(new { ok = true, debited = paidPart + bonusPart });
    }

    /// <summary>Money moved at Stripe that the ledger could not follow: page the superadmins.</summary>
    private static void NotifyReview(AppDbContext db, AiCreditPayment row, string who, string problem)
    {
        try
        {
            NotificationService.Notify(db, NotificationCategories.Admin, "system",
                $"AI credit payment needs review — {who}",
                $"Stripe payment {row.SessionId} ({AiCredit.Money(row.Currency, row.CreditAmount)}): {problem}. "
                + "Refund it in Stripe or correct the credit by hand.",
                "/billing", NotificationAudience.ForRoles(Roles.SuperAdmin),
                new NotifyOptions { RefKey = $"ai-credit-review:{row.Id}" });
        }
        catch { /* an alert never breaks the webhook */ }
    }
}
