using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Buying prepaid AI credit (Stripe Checkout) and the promotions on it
/// (<see cref="AiCreditTopup"/>). The webhook that credits a paid top-up is the
/// existing <c>POST /api/integrations/webhooks/stripe</c> (IntegrationsEndpoints).
///
/// <list type="bullet">
/// <item><c>GET /api/billing/ai/credit/topup</c> — what the caller may buy: the
/// companies, the offers open to them, recent purchases, one purchase's status
/// (<c>billing.topup</c> or <c>billing.manage</c>).</item>
/// <item><c>POST /api/billing/ai/credit/topup</c> — open a Checkout Session for
/// one company and answer its URL (same gate). Spends money: no MCP tool.</item>
/// <item><c>GET|POST /api/billing/ai/credit/offers</c>,
/// <c>DELETE …/offers/{slug}</c>, <c>POST …/offers/{slug}/grant</c> — offer CRUD
/// and a manual grant of an offer's bonus (<c>billing.manage</c>).</item>
/// </list>
///
/// <para><b>Why <c>billing.topup</c>.</b> Buying credit for your own company is a
/// company admin's job, but <c>billing.manage</c> also shows provider cost, markup
/// and margin and is superadmin-only (and stripped from anyone bound to entities).
/// So buying has its own capability: admins hold it by default, agents do not, and
/// it is NOT organization-level — a company admin buys for their own companies only.</para>
/// </summary>
public static class AiCreditTopupEndpoints
{
    public const string TopupCap = "billing.topup";
    public const string ManageCap = AiBillingEndpoints.ManageCap;

    private const string Note =
        "Top-ups are paid on Stripe's own checkout page (cards, FPX and the other methods enabled on the Stripe "
        + "account) and credited when Stripe confirms the payment — usually within seconds. The amount credited "
        + "is decided when the checkout opens; a promotion's bonus is written as its own ledger line.";

    public static void MapAiCreditTopupEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- read: what the caller may buy, and what they bought ------------------------
        app.MapGet("/api/billing/ai/credit/topup", async (string? company, int? payment, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var who = await CallerAsync(db, me);
            var snap = await AiCredit.LoadAsync(db, sc);
            var buyable = Buyable(db, me, who, snap);
            var currency = snap.Currency.Trim().ToUpperInvariant();
            var now = DateTime.UtcNow;

            int? only = null;
            if (!string.IsNullOrWhiteSpace(company))
            {
                only = ResolveCompany(snap, company);
                if (only == null) return Bad($"Unknown company '{company}' — use a top-level entity slug or id.");
                if (!buyable.Contains(only.Value))
                    return Results.Json(new { detail = "That company is outside your scope." }, statusCode: 403);
            }
            var scope = only is int one ? new HashSet<int> { one } : buyable;

            var offers = (await db.AiCreditOffers.AsNoTracking().ToListAsync())
                .Where(o => AiCreditTopup.Unavailable(o, o.EntityId ?? 0, currency, now) == null)
                .Where(o => o.EntityId == null ? scope.Count > 0 : scope.Contains(o.EntityId.Value))
                .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
                .Select(o => OfferJson(o, snap, manager: false)).ToList();

            var scoped = scope.ToList();
            var payments = await db.AiCreditPayments.AsNoTracking().Where(p => scoped.Contains(p.EntityId))
                .OrderByDescending(p => p.Id).Take(20).ToListAsync();
            object? asked = null;
            if (payment is int pid)
            {
                var p = await db.AiCreditPayments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid);
                if (p == null || !buyable.Contains(p.EntityId))
                    return Results.Json(new { detail = "No such top-up in your scope." }, statusCode: 404);
                asked = PaymentJson(p, snap);
            }

            return Results.Ok(new
            {
                ready = AiCreditTopup.NotReady(sc) == null,
                reason = AiCreditTopup.NotReady(sc),
                mode = StripeConfig.Mode(sc),
                currency,
                min = AiCreditTopup.MinAmount,
                max = AiCreditTopup.MaxAmount,
                presets = AiCreditTopup.Presets,
                companies = buyable.Select(id => snap.Company(id))
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new
                    {
                        id = c.EntityId,
                        name = c.Name,
                        slug = c.EntityId is int cid && snap.Entities.TryGetValue(cid, out var e) ? e.Slug : "",
                        balance = c.Balance,
                        opened = c.Opened,
                    }).ToList(),
                offers,
                payments = payments.Select(p => PaymentJson(p, snap)).ToList(),
                payment = asked,
                note = Note,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability(TopupCap, ManageCap);

        // ---- write: open a Stripe Checkout Session ---------------------------------------
        app.MapPost("/api/billing/ai/credit/topup", async (JsonElement body, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return Bad("Send {company: \"<company slug or id>\", amount: 100, offer: \"<offer slug>\"}.");
            var who = await CallerAsync(db, me);
            if (who.IsCustomer)
                return Results.Json(new { detail = "A customer login cannot buy AI credit." }, statusCode: 403);
            var snap = await AiCredit.LoadAsync(db, sc);
            var buyable = Buyable(db, me, who, snap);

            var companyRaw = Str(body, "company");
            if (companyRaw.Length == 0) companyRaw = Str(body, "entity");
            int companyId;
            if (companyRaw.Length > 0)
            {
                var id = ResolveEntity(snap, companyRaw);
                if (id == null) return Bad($"Unknown company '{companyRaw}'.");
                if (snap.Entities.TryGetValue(id.Value, out var e) && e.ParentId != null)
                    return Bad($"{e.Name} is a branch — credit is bought for its company"
                               + (snap.CompanyOf(id) is int c ? $", {snap.NameOf(c)}." : "."));
                if (!buyable.Contains(id.Value))
                    return Results.Json(new { detail = "That company is outside your scope." }, statusCode: 403);
                companyId = id.Value;
            }
            else if (buyable.Count == 1) companyId = buyable.First();
            else return Bad("Which company? Send company (a top-level entity's slug or id).");

            var offer = Str(body, "offer");
            var amount = 0.0;
            if (body.TryGetProperty("amount", out var a))
            {
                if (!(a.ValueKind == JsonValueKind.Number && a.TryGetDouble(out amount))
                    && !(a.ValueKind == JsonValueKind.String
                         && double.TryParse(a.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount)))
                    return Bad("The amount must be a number.");
            }
            else if (offer.Length == 0) return Bad("An amount is required.");

            var key = ctx.Request.Headers["Idempotency-Key"].ToString().Trim();
            if (key.Length == 0) key = Str(body, "idempotency_key");
            var stripe = ctx.RequestServices.GetService<IStripeGateway>()
                         ?? new StripeHttpGateway(ctx.RequestServices.GetRequiredService<IHttpClientFactory>());

            var result = await AiCreditTopup.CreateAsync(db, sc, stripe, new AiCreditTopup.CreateRequest(
                companyId, snap.NameOf(companyId), amount, offer, key,
                SafeReturn(ctx, Str(body, "success_url"), "/billing/topup/success"),
                SafeReturn(ctx, Str(body, "cancel_url"), "/billing/topup/cancel"),
                who.UserId, me.Identity?.Name ?? ""), CancellationToken.None);
            if (!result.Ok) return Results.Json(new { detail = result.Error }, statusCode: result.Status);
            var p = result.Payment!;
            return Results.Ok(new
            {
                ok = true,
                replayed = result.Replayed,
                url = p.CheckoutUrl,
                payment = PaymentJson(p, snap),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability(TopupCap, ManageCap);

        // ---- offers: CRUD (billing.manage) --------------------------------------------
        app.MapGet("/api/billing/ai/credit/offers", async (AppDbContext db, ScopingService sc) =>
        {
            var snap = await AiCredit.LoadAsync(db, sc);
            var offers = await db.AiCreditOffers.AsNoTracking().OrderByDescending(o => o.Active)
                .ThenBy(o => o.Label).ToListAsync();
            var now = DateTime.UtcNow;
            return Results.Ok(new
            {
                currency = snap.Currency,
                kinds = AiCreditOfferKinds.All,
                offers = offers.Select(o => OfferJson(o, snap, manager: true, now)).ToList(),
                companies = snap.Entities.Where(kv => kv.Value.ParentId == null)
                    .OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new { id = kv.Key, name = kv.Value.Name, slug = kv.Value.Slug }).ToList(),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapPost("/api/billing/ai/credit/offers", async (JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) => await SaveOfferAsync(body, db, sc, me))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapDelete("/api/billing/ai/credit/offers/{slug}", async (string slug, AppDbContext db) =>
        {
            var o = await db.AiCreditOffers.FirstOrDefaultAsync(x => x.Slug == slug);
            if (o == null) return Results.Json(new { detail = $"No offer '{slug}'." }, statusCode: 404);
            var used = o.Redemptions > 0
                       || await db.AiCreditPayments.AnyAsync(p => p.OfferId == o.Id)
                       || await db.AiCreditEntries.AnyAsync(e => e.OfferId == o.Id);
            if (used)
                return Results.Json(new
                {
                    detail = $"'{o.Label}' has been used — deactivate it instead, so the ledger and the payments "
                             + "that name it keep their history.",
                }, statusCode: 409);
            db.AiCreditOffers.Remove(o);
            await db.SaveChangesAsync();
            AuditTrail.Write(db, "billing.credit.offer.delete", $"offer:{o.Slug}", $"Deleted the AI credit offer '{o.Label}'.");
            return Results.Ok(new { ok = true, deleted = o.Slug });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        // ---- a billing manager grants an offer's bonus by hand ------------------------
        app.MapPost("/api/billing/ai/credit/offers/{slug}/grant", async (string slug, JsonElement body,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return Bad("Send {entity: \"<company slug or id>\", amount: 100 (what a percentage applies to), note: \"why\"}.");
            var offer = await db.AiCreditOffers.AsNoTracking().FirstOrDefaultAsync(o => o.Slug == slug);
            if (offer == null) return Results.Json(new { detail = $"No offer '{slug}'." }, statusCode: 404);
            var snap = await AiCredit.LoadAsync(db, sc);
            var entityRaw = Str(body, "entity");
            if (entityRaw.Length == 0) entityRaw = Str(body, "company");
            if (entityRaw.Length == 0) return Bad("Which company? Send entity (a top-level entity's slug or id).");
            var id = ResolveEntity(snap, entityRaw);
            if (id == null) return Bad($"Unknown entity '{entityRaw}'.");
            if (snap.Entities.TryGetValue(id.Value, out var e) && e.ParentId != null)
                return Bad($"{e.Name} is a branch — credit is held by its company.");
            var currency = snap.Currency.Trim().ToUpperInvariant();
            if (AiCreditTopup.Unavailable(offer, id.Value, currency, DateTime.UtcNow) is string why)
                return AiCreditTopup.FullyRedeemed(offer) ? Results.Json(new { detail = why }, statusCode: 409) : Bad(why);
            var baseAmount = Num(body, "amount") ?? 0;
            var quote = AiCreditTopup.ManualBonus(offer, baseAmount);
            if (quote.Error != null) return Bad(quote.Error);
            if (quote.Bonus <= 0) return Bad("This offer gives no bonus to grant.");

            var key = Str(body, "idempotency_key");
            if (key.Length > 0 && await db.AiCreditEntries.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == key)
                    is AiCreditEntry earlier)
                return Results.Ok(new { ok = true, replayed = true, entry = EntryJson(earlier), balance = earlier.BalanceAfter });

            if (!await AiCreditTopup.ReserveAsync(db, offer.Id))
                return Results.Json(new { detail = "This offer has been fully redeemed." }, statusCode: 409);
            var note = Str(body, "note");
            var result = await AiCredit.ApplyAsync(db, new AiCredit.Request(AiCreditScopes.Entity, id, "",
                AiCreditKinds.PromoBonus, quote.Bonus, currency,
                Note: $"Promotion bonus: {offer.Label}" + (note.Length > 0 ? $" — {note}" : ""),
                Ref: $"offer:{offer.Slug}", IdempotencyKey: key, Actor: me.Identity?.Name ?? "",
                Source: AuditSources.User, OfferId: offer.Id));
            if (!result.Ok || result.Replayed) await AiCreditTopup.ReleaseAsync(db, offer.Id);
            if (!result.Ok) return Bad(result.Error!);
            var entry = result.Entry!;
            if (!result.Replayed)
                AuditTrail.Write(db, "billing.credit.offer.grant", $"entity:{id.Value.ToString(CultureInfo.InvariantCulture)}",
                    $"Granted the '{offer.Label}' bonus of {AiCredit.Money(entry.Currency, entry.Amount)} to "
                    + $"{snap.NameOf(id.Value)} — balance {AiCredit.Money(entry.Currency, entry.BalanceAfter)}"
                    + (note.Length > 0 ? $" ({note})" : ""), entityId: id);
            return Results.Ok(new
            {
                ok = true,
                replayed = result.Replayed,
                entry = EntryJson(entry),
                balance = result.Account?.Balance ?? entry.BalanceAfter,
                name = snap.NameOf(id.Value),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);
    }

    // ---- offers: create or change by slug -------------------------------------------

    private static async Task<IResult> SaveOfferAsync(JsonElement body, AppDbContext db, ScopingService sc,
        ClaimsPrincipal me)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return Bad("Send {label, kind: bonus_pct|bonus_fixed|bundle, …} — pass slug to change an existing offer.");
        var snap = await AiCredit.LoadAsync(db, sc);
        var slug = Slugify(Str(body, "slug"));
        var explicitSlug = slug.Length > 0;
        var label = Str(body, "label");
        if (!explicitSlug) slug = Slugify(label);
        if (slug.Length == 0) return Bad("An offer needs a label (or a slug).");

        var o = await db.AiCreditOffers.FirstOrDefaultAsync(x => x.Slug == slug);
        var creating = o == null;
        if (!creating && !explicitSlug)
            return Results.Json(new { detail = $"An offer with the key '{slug}' already exists — pass slug to change it." },
                statusCode: 409);
        var now = DateTime.UtcNow;
        o ??= new AiCreditOffer
        {
            Slug = slug, Currency = snap.Currency.Trim().ToUpperInvariant(), CreatedBy = me.Identity?.Name ?? "",
            CreatedAt = now,
        };

        if (label.Length > 0) o.Label = label.Length <= 120 ? label : label[..120];
        if (Str(body, "kind") is { Length: > 0 } kind) o.Kind = kind;
        if (Num(body, "pay_amount") is double pay) o.PayAmount = Math.Round(pay, 2);
        if (Num(body, "credit_amount") is double credit) o.CreditAmount = Math.Round(credit, 2);
        if (Num(body, "bonus_pct") is double pct) o.BonusPct = Math.Round(pct, 4);
        if (Num(body, "bonus_fixed") is double fixedBonus) o.BonusFixed = Math.Round(fixedBonus, 2);
        if (Str(body, "currency") is { Length: > 0 } cur) o.Currency = cur.ToUpperInvariant();
        if (body.TryGetProperty("active", out var act) && act.ValueKind is JsonValueKind.True or JsonValueKind.False)
            o.Active = act.GetBoolean();
        if (Num(body, "max_redemptions") is double max) o.MaxRedemptions = (int)Math.Max(0, Math.Round(max));
        if (body.TryGetProperty("note", out _)) o.Note = Str(body, "note");
        foreach (var (field, set) in new (string, Action<DateTime?>)[]
                 { ("starts_at", v => o.StartsAt = v), ("ends_at", v => o.EndsAt = v) })
        {
            if (!body.TryGetProperty(field, out var raw)) continue;
            if (raw.ValueKind == JsonValueKind.Null || (raw.ValueKind == JsonValueKind.String && raw.GetString() == ""))
                set(null);
            else if (raw.ValueKind == JsonValueKind.String
                     && DateTime.TryParse(raw.GetString(), CultureInfo.InvariantCulture,
                         DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                set(DateTime.SpecifyKind(when, DateTimeKind.Utc));
            else return Bad($"{field} must be an ISO date/time, or empty for none.");
        }
        if (body.TryGetProperty("entity", out var ent))
        {
            var raw = ent.ValueKind == JsonValueKind.Null ? "" : Str(body, "entity");
            if (raw.Length == 0) o.EntityId = null;
            else
            {
                var cid = ResolveCompany(snap, raw);
                if (cid == null) return Bad($"'{raw}' is not a company (a top-level entity).");
                o.EntityId = cid;
            }
        }

        // validate the merged offer
        if (o.Label.Length == 0) return Bad("An offer needs a label.");
        if (!AiCreditOfferKinds.All.Contains(o.Kind))
            return Bad($"kind must be one of {string.Join(", ", AiCreditOfferKinds.All)}.");
        if (o.Currency.Length != 3 || !o.Currency.All(char.IsLetter)) return Bad("The currency must be a 3-letter code.");
        if (o.PayAmount < 0 || o.CreditAmount < 0 || o.BonusFixed < 0 || o.BonusPct < 0)
            return Bad("Amounts cannot be negative.");
        switch (o.Kind)
        {
            case AiCreditOfferKinds.BonusPct when o.BonusPct <= 0 || o.BonusPct > 500:
                return Bad("A percentage offer needs bonus_pct between 0 and 500.");
            case AiCreditOfferKinds.BonusFixed when o.BonusFixed <= 0:
                return Bad("A fixed-bonus offer needs bonus_fixed above 0.");
            case AiCreditOfferKinds.Bundle when o.PayAmount < AiCreditTopup.MinAmount || o.PayAmount > AiCreditTopup.MaxAmount:
                return Bad($"A bundle's price (pay_amount) is between {AiCreditTopup.MinAmount} and {AiCreditTopup.MaxAmount}.");
            case AiCreditOfferKinds.Bundle when o.CreditAmount <= o.PayAmount:
                return Bad("A bundle must give more credit (credit_amount) than it costs (pay_amount).");
        }
        if (o.StartsAt is DateTime s && o.EndsAt is DateTime en && en <= s) return Bad("ends_at must be after starts_at.");

        o.UpdatedAt = now;
        if (creating) db.AiCreditOffers.Add(o);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            return Results.Json(new { detail = $"An offer with the key '{slug}' already exists." }, statusCode: 409);
        }
        AuditTrail.Write(db, "billing.credit.offer.save", $"offer:{o.Slug}",
            $"{(creating ? "Created" : "Changed")} the AI credit offer '{o.Label}': {Describe(o)}"
            + (o.Active ? "" : " (inactive)"));
        return Results.Ok(new { ok = true, created = creating, offer = OfferJson(o, snap, manager: true, now) });
    }

    private static string Describe(AiCreditOffer o) => o.Kind switch
    {
        AiCreditOfferKinds.BonusPct => $"+{o.BonusPct.ToString(CultureInfo.InvariantCulture)}% bonus"
                                       + (o.PayAmount > 0 ? $" on {AiCredit.Money(o.Currency, o.PayAmount)} or more" : ""),
        AiCreditOfferKinds.BonusFixed => $"+{AiCredit.Money(o.Currency, o.BonusFixed)} bonus"
                                         + (o.PayAmount > 0 ? $" on {AiCredit.Money(o.Currency, o.PayAmount)} or more" : ""),
        _ => $"pay {AiCredit.Money(o.Currency, o.PayAmount)}, get {AiCredit.Money(o.Currency, o.CreditAmount)}",
    } + (o.MaxRedemptions > 0 ? $", {o.MaxRedemptions} redemption(s) max" : "");

    // ---- shapes -------------------------------------------------------------------

    private static object OfferJson(AiCreditOffer o, AiCredit.Snapshot snap, bool manager, DateTime? now = null) => new
    {
        slug = o.Slug,
        label = o.Label,
        kind = o.Kind,
        pay_amount = o.PayAmount,
        credit_amount = o.CreditAmount,
        bonus_pct = o.BonusPct,
        bonus_fixed = o.BonusFixed,
        currency = o.Currency,
        active = o.Active,
        starts_at = o.StartsAt,
        ends_at = o.EndsAt,
        max_redemptions = o.MaxRedemptions,
        redemptions = manager ? o.Redemptions : (int?)null,
        remaining = o.MaxRedemptions > 0 ? Math.Max(0, o.MaxRedemptions - o.Redemptions) : (int?)null,
        entity_id = o.EntityId,
        company = o.EntityId is int id ? snap.NameOf(id) : null,
        summary = Describe(o),
        note = manager ? o.Note : null,
        // why a buyer cannot pick it right now (inactive, out of its window, fully redeemed,
        // another currency) — null when it is open
        unavailable = AiCreditTopup.Unavailable(o, o.EntityId ?? 0, snap.Currency.Trim().ToUpperInvariant(),
            now ?? DateTime.UtcNow),
        updated_at = o.UpdatedAt,
    };

    private static object PaymentJson(AiCreditPayment p, AiCredit.Snapshot snap) => new
    {
        id = p.Id,
        entity_id = p.EntityId,
        company = snap.NameOf(p.EntityId),
        status = p.Status,
        status_detail = p.StatusDetail,
        mode = p.Mode,
        currency = p.Currency,
        amount = p.AmountMinor / (double)(StripeConfig.MinorFactor(p.Currency) ?? 100),
        credit_amount = p.CreditAmount,
        bonus_amount = p.BonusAmount,
        offer_id = p.OfferId,
        refunded = p.RefundedMinor / (double)(StripeConfig.MinorFactor(p.Currency) ?? 100),
        created_by = p.CreatedBy,
        created_at = p.CreatedAt,
        completed_at = p.CompletedAt,
    };

    private static object EntryJson(AiCreditEntry e) => new
    {
        id = e.Id, scope = e.Scope, entity_id = e.EntityId, kind = e.Kind, amount = e.Amount,
        currency = e.Currency, balance_after = e.BalanceAfter, note = e.Note, actor = e.Actor,
        offer_id = e.OfferId, created_at = e.CreatedAt,
    };

    // ---- who may buy for which company --------------------------------------------

    private sealed record Caller(bool Manager, bool IsCustomer, int? UserId);

    private static async Task<Caller> CallerAsync(AppDbContext db, ClaimsPrincipal me)
    {
        var name = me.Identity?.Name ?? "";
        var u = await db.Users.AsNoTracking().Where(x => x.Username == name)
            .Select(x => new { x.Id, ContactId = x.Profile != null ? x.Profile.ContactId : null })
            .FirstOrDefaultAsync();
        var manager = ShellScope.RoleOf(me) == Roles.SuperAdmin || await ShellScope.HasCapAsync(db, me, ManageCap);
        return new Caller(manager, u?.ContactId != null, u?.Id);
    }

    /// <summary>
    /// The companies the caller may buy credit for: every company for a billing
    /// manager or an unbound (organization-level) admin; only their own companies for
    /// someone bound to entities; none for a customer login.
    /// </summary>
    private static HashSet<int> Buyable(AppDbContext db, ClaimsPrincipal me, Caller who, AiCredit.Snapshot snap)
    {
        if (who.IsCustomer) return new HashSet<int>();
        var all = snap.Entities.Where(kv => kv.Value.ParentId == null).Select(kv => kv.Key).ToHashSet();
        if (who.Manager) return all;
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        if (assigned.Count == 0) return all;
        return assigned.Select(a => snap.CompanyOf(a)).OfType<int>().ToHashSet();
    }

    /// <summary>
    /// A return URL handed to Stripe: absolute http(s) on the dashboard's own origin
    /// (the browser's Origin header, or this host) — anything else falls back to this
    /// host's own page, so the endpoint can never be used to bounce a buyer elsewhere.
    /// </summary>
    private static string SafeReturn(HttpContext ctx, string raw, string fallbackPath)
    {
        var req = ctx.Request;
        var self = $"{(string.IsNullOrEmpty(req.Scheme) ? "http" : req.Scheme)}://{(req.Host.HasValue ? req.Host.Value : "localhost")}";
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { self };
        var origin = req.Headers.Origin.ToString().Trim().TrimEnd('/');
        if (origin.Length > 0) allowed.Add(origin);
        if (raw.Length is > 0 and <= 1000 && Uri.TryCreate(raw, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            && allowed.Contains(u.GetLeftPart(UriPartial.Authority)))
            return raw;
        return (origin.Length > 0 ? origin : self) + fallbackPath;
    }

    // ---- small helpers (the AiCreditEndpoints conventions) ---------------------------

    private static int? ResolveEntity(AiCredit.Snapshot snap, string raw)
    {
        var s = raw.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return snap.Entities.ContainsKey(id) ? id : null;
        foreach (var (key, e) in snap.Entities)
            if (string.Equals(e.Slug, s, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }

    private static int? ResolveCompany(AiCredit.Snapshot snap, string raw)
    {
        var id = ResolveEntity(snap, raw);
        return id != null && snap.Entities.TryGetValue(id.Value, out var e) && e.ParentId == null ? id : null;
    }

    private static string Str(JsonElement body, string name) =>
        body.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => (v.GetString() ?? "").Trim(),
                JsonValueKind.Number => v.ToString(),
                _ => "",
            }
            : "";

    private static double? Num(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return d;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            && double.IsFinite(s)) return s;
        return null;
    }

    private static string Slugify(string s)
    {
        var slug = Regex.Replace((s ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length <= 60 ? slug : slug[..60].Trim('-');
    }

    private static IResult Bad(string detail) => Results.BadRequest(new { detail });
}
