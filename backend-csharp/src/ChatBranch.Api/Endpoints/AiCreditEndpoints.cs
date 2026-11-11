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
/// Prepaid AI credit — the balance a COMPANY (a top-level entity, its branches
/// rolling up into it) spends its AI from, and the immutable ledger behind it
/// (<see cref="AiCredit"/>).
///
/// <list type="bullet">
/// <item><c>GET /api/billing/ai/credit</c> — balances + the ledger
/// (<c>billing.view</c>, or <c>billing.manage</c>). A reader who is bound to
/// entities sees only their own companies, exactly like the AI bill.</item>
/// <item><c>POST /api/billing/ai/credit/grant</c> — hand credit to a company (or
/// to a login), <c>billing.manage</c> (superadmin by default).</item>
/// <item><c>POST /api/billing/ai/credit/adjust</c> — a SIGNED correction. The
/// ledger is append-only: a correction is a new line, never an edit.</item>
/// </list>
///
/// <para>Credit is denominated in the billing currency of the moment the account
/// was opened (<c>ai_billing</c>, see <see cref="AiBilling"/>). A later currency
/// change does not convert it, so a grant in a different currency is refused.</para>
///
/// <para><b>Phase 1.</b> Nothing debits these balances yet and no balance can stop
/// an AI reply — consumption metering and enforcement come later. The optional
/// per-login balance can only ever cover what a person runs themselves (the
/// operator assistant, the AI playground, skill drafting, on-demand summaries):
/// automated AI work carries no username.</para>
/// </summary>
public static class AiCreditEndpoints
{
    public const string ViewCap = AiBillingEndpoints.ViewCap;
    public const string ManageCap = AiBillingEndpoints.ManageCap;

    private const string Note =
        "Credit is prepaid money held per company (a top-level entity; a branch draws on its company's "
        + "balance). The ledger is append-only — a correction is a new signed line, never an edit. Nothing "
        + "debits these balances yet: AI usage is still billed by the monthly AI bill, and no balance can "
        + "stop an AI reply. A per-login balance covers only what that person runs themselves (Ask "
        + "ChatBranch, the AI playground, skill drafting, on-demand summaries) — automated AI work "
        + "(customer replies, journeys, voice, indexing) carries no username and is never counted against it.";

    public static void MapAiCreditEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- read: balances + the ledger -------------------------------------------------
        app.MapGet("/api/billing/ai/credit", async (string? company, string? entity, string? user,
            int? page, int? page_size, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var manager = await IsManagerAsync(db, me);
            var snap = await AiCredit.LoadAsync(db, sc);
            var visible = VisibleCompanies(db, me, manager, snap);

            var companies = snap.Entities.Where(kv => kv.Value.ParentId == null)
                .Where(kv => visible == null || visible.Contains(kv.Key))
                .OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key).ToList();

            // the ledger: one company (or one login) when asked for, else everything visible
            int? ledgerCompany = null;
            if (!string.IsNullOrWhiteSpace(company))
            {
                ledgerCompany = ResolveCompany(snap, company);
                if (ledgerCompany == null) return Bad($"Unknown company '{company}' — use a top-level entity slug or id.");
                if (visible != null && !visible.Contains(ledgerCompany.Value))
                    return Results.Json(new { detail = "That company is outside your scope." }, statusCode: 403);
            }
            var ledgerUser = AiCredit.NormalizeUser(user);
            if (ledgerUser.Length > 0 && !manager && !string.Equals(ledgerUser, me.Identity?.Name, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { detail = "Only a billing manager reads another person's credit." }, statusCode: 403);

            var size = Math.Clamp(page_size ?? 50, 1, 200);
            var skip = Math.Max(0, (Math.Max(1, page ?? 1) - 1) * size);
            var lines = await AiCredit.LedgerAsync(db, ledgerCompany, ledgerUser.Length > 0 ? ledgerUser : null,
                size, skip);
            if (ledgerCompany == null && ledgerUser.Length == 0 && visible != null)
                lines = lines.Where(l => l.EntityId is int id && visible.Contains(id)).ToList();

            // "what does this entity draw on?" — the rule a branch follows
            object? resolved = null;
            if (!string.IsNullOrWhiteSpace(entity))
            {
                var id = ResolveEntity(snap, entity);
                if (id == null) return Bad($"Unknown entity '{entity}'.");
                var status = snap.ForEntity(id);
                if (status == null) return Bad($"'{entity}' belongs to no company — it has no credit balance.");
                if (visible != null && status.EntityId is int cid && !visible.Contains(cid))
                    return Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403);
                resolved = new
                {
                    entity_id = id,
                    entity_name = snap.NameOf(id.Value),
                    draws_on = Status(status),
                };
            }

            var userAccounts = snap.ByUser.Values
                .Where(a => manager || string.Equals(a.UserName, me.Identity?.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.UserName, StringComparer.OrdinalIgnoreCase)
                .Select(a => snap.User(a.UserName)).ToList();

            var rows = companies.Select(id => snap.Company(id)).ToList();
            return Results.Ok(new
            {
                currency = snap.Currency,
                is_manager = manager,
                can_grant = manager,
                companies = companies.Select(id => new
                {
                    id,
                    name = snap.NameOf(id),
                    slug = snap.Entities.TryGetValue(id, out var e) ? e.Slug : "",
                    branches = snap.Entities.Where(kv => snap.CompanyOf(kv.Key) == id && kv.Key != id)
                        .OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => new { id = kv.Key, name = kv.Value.Name, slug = kv.Value.Slug }).ToList(),
                    credit = Status(snap.Company(id)),
                }).ToList(),
                users = userAccounts.Select(Status).ToList(),
                resolved,
                totals = new
                {
                    balance = AiCredit.Round(rows.Sum(r => r.Balance)),
                    accounts = rows.Count(r => r.Opened),
                    exhausted = rows.Count(r => r.Exhausted),
                },
                ledger = lines.Select(l => new
                {
                    id = l.Id,
                    scope = l.Scope,
                    entity_id = l.EntityId,
                    company = l.EntityId is int cid ? snap.NameOf(cid) : null,
                    user_name = l.UserName,
                    kind = l.Kind,
                    amount = l.Amount,
                    currency = l.Currency,
                    balance_after = l.BalanceAfter,
                    @ref = l.Ref,
                    note = l.Note,
                    actor = l.Actor,
                    source = l.Source,
                    created_at = l.CreatedAt,
                }).ToList(),
                page = Math.Max(1, page ?? 1),
                page_size = size,
                note = Note,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability(ViewCap, ManageCap);

        // ---- write: a grant (positive) and a signed adjustment ---------------------------
        app.MapPost("/api/billing/ai/credit/grant", (JsonElement body, AppDbContext db, ScopingService sc,
                ClaimsPrincipal me) => WriteAsync(body, db, sc, me, AiCreditKinds.Grant))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapPost("/api/billing/ai/credit/adjust", (JsonElement body, AppDbContext db, ScopingService sc,
                ClaimsPrincipal me) => WriteAsync(body, db, sc, me, AiCreditKinds.Adjustment))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);
    }

    private static async Task<IResult> WriteAsync(JsonElement body, AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, string kind)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return Bad("Send {entity: \"<company slug or id>\", amount: 100, note: \"why\"} — or user instead of entity.");
        var grant = kind == AiCreditKinds.Grant;
        if (!TryAmount(body, out var amount, out var amountError)) return Bad(amountError);
        if (grant && amount <= 0) return Bad("A grant must be a positive amount — use /adjust to take credit back.");
        if (!grant && amount == 0) return Bad("An adjustment must be a non-zero amount (negative takes credit away).");

        var snap = await AiCredit.LoadAsync(db, sc);
        var note = Str(body, "note");
        var key = Str(body, "idempotency_key");
        var entityRaw = Str(body, "entity");
        var userRaw = Str(body, "user");
        if (entityRaw.Length > 0 && userRaw.Length > 0)
            return Bad("Credit one thing at a time: a company (entity) or a login (user).");

        AiCredit.Request req;
        string target, who;
        if (userRaw.Length > 0)
        {
            var username = await db.Users.AsNoTracking()
                .Where(u => u.Username.ToLower() == userRaw.ToLower()).Select(u => u.Username).FirstOrDefaultAsync();
            if (username == null) return Bad($"Unknown user '{userRaw}'.");
            req = new AiCredit.Request(AiCreditScopes.User, null, username, kind, amount, snap.Currency,
                note, Actor: me.Identity?.Name ?? "", Source: AuditSources.User, IdempotencyKey: key);
            target = "user:" + AiCredit.NormalizeUser(username);
            who = username;
        }
        else
        {
            if (entityRaw.Length == 0) return Bad("Which company? Send entity (a top-level entity's slug or id) or user.");
            var id = ResolveEntity(snap, entityRaw);
            if (id == null) return Bad($"Unknown entity '{entityRaw}'.");
            if (snap.Entities.TryGetValue(id.Value, out var e) && e.ParentId != null)
            {
                var company = snap.CompanyOf(id);
                return Bad($"{e.Name} is a branch — credit is held by its company"
                           + (company is int c ? $", {snap.NameOf(c)} (id {c.ToString(CultureInfo.InvariantCulture)})." : "."));
            }
            req = new AiCredit.Request(AiCreditScopes.Entity, id, "", kind, amount, snap.Currency,
                note, Actor: me.Identity?.Name ?? "", Source: AuditSources.User, IdempotencyKey: key);
            target = "entity:" + id.Value.ToString(CultureInfo.InvariantCulture);
            who = snap.NameOf(id.Value);
        }

        var result = await AiCredit.ApplyAsync(db, req);
        if (!result.Ok) return Bad(result.Error!);
        var entry = result.Entry!;
        var account = result.Account;
        if (!result.Replayed)
        {
            var money = AiCredit.Money(entry.Currency, Math.Abs(entry.Amount));
            var verb = grant ? "Granted" : entry.Amount >= 0 ? "Added" : "Took back";
            ChatBranch.Data.Audit.AuditTrail.Write(db, "billing.credit." + (grant ? "grant" : "adjust"), target,
                $"{verb} {money} of AI credit to {who} — balance {AiCredit.Money(entry.Currency, entry.BalanceAfter)}"
                + (note.Length > 0 ? $" ({note})" : ""),
                entityId: entry.EntityId);
        }
        return Results.Ok(new
        {
            ok = true,
            replayed = result.Replayed,
            currency = entry.Currency,
            entry = new
            {
                id = entry.Id, scope = entry.Scope, entity_id = entry.EntityId, user_name = entry.UserName,
                kind = entry.Kind, amount = entry.Amount, balance_after = entry.BalanceAfter,
                note = entry.Note, actor = entry.Actor, created_at = entry.CreatedAt,
            },
            balance = account?.Balance ?? entry.BalanceAfter,
            name = who,
        });
    }

    // ---- helpers -----------------------------------------------------------------------

    private static object Status(AiCreditStatus s) => new
    {
        scope = s.Scope,
        key = s.Key,
        entity_id = s.EntityId,
        user_name = s.UserName,
        name = s.Name,
        balance = s.Balance,
        currency = s.Currency,
        enabled = s.Enabled,
        opened = s.Opened,
        exhausted = s.Exhausted,
        overdraft_limit = s.OverdraftLimit,
        low_balance_threshold = s.LowBalanceThreshold,
        updated_at = s.UpdatedAt,
    };

    private static Task<bool> IsManagerAsync(AppDbContext db, ClaimsPrincipal me) =>
        ShellScope.RoleOf(me) == Roles.SuperAdmin ? Task.FromResult(true) : ShellScope.HasCapAsync(db, me, ManageCap);

    /// <summary>Company ids the caller may see; null = every company (the AI bill's rule).</summary>
    private static HashSet<int>? VisibleCompanies(AppDbContext db, ClaimsPrincipal me, bool manager,
        AiCredit.Snapshot snap)
    {
        if (manager) return null;
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        if (assigned.Count == 0) return null;
        return assigned.Select(a => snap.CompanyOf(a)).OfType<int>().ToHashSet();
    }

    /// <summary>An entity by id or slug.</summary>
    private static int? ResolveEntity(AiCredit.Snapshot snap, string raw)
    {
        var s = raw.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return snap.Entities.ContainsKey(id) ? id : null;
        foreach (var (key, e) in snap.Entities)
            if (string.Equals(e.Slug, s, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }

    /// <summary>A TOP-LEVEL entity by id or slug.</summary>
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

    private static bool TryAmount(JsonElement body, out double amount, out string error)
    {
        amount = 0;
        error = "";
        if (!body.TryGetProperty("amount", out var v))
        {
            error = "An amount is required.";
            return false;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            if (!double.TryParse((v.GetString() ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
            {
                error = "The amount must be a number.";
                return false;
            }
        }
        else if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out amount))
        {
            error = "The amount must be a number.";
            return false;
        }
        if (!double.IsFinite(amount) || Math.Abs(amount) > 1_000_000_000)
        {
            error = "The amount must be a number below 1,000,000,000.";
            return false;
        }
        if (amount != 0 && Math.Abs(amount) < 0.01)
        {
            error = "The smallest amount is 0.01.";
            return false;
        }
        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    private static IResult Bad(string detail) => Results.BadRequest(new { detail });
}
