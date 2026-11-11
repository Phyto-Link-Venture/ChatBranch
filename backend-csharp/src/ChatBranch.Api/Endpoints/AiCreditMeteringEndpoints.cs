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
/// Prepaid AI credit, phase 2 — consumption and enforcement
/// (<see cref="AiCreditSweep"/>, <see cref="AiCreditGuard"/>).
///
/// <list type="bullet">
/// <item><c>GET /api/billing/ai/credit/consumption</c> — what AI usage has drawn
/// from each visible company's balance (recent <c>consumption</c> ledger lines and
/// 24 h / 7 d / 30 d totals), whether its AI is paused for lack of credit, and —
/// for billing managers — the metering health (sweep cursor, fail-open count,
/// usage rows that failed to save). Sweeps first, so the answer is fresh.
/// <c>billing.view</c> or <c>billing.manage</c>; a reader bound to entities sees
/// only their own companies.</item>
/// <item><c>PUT /api/billing/ai/credit/account</c> — a billing manager switches
/// enforcement on or off for one company (or login) and sets its overdraft limit
/// and low-balance warning level. <c>enabled = false</c> is the off switch: that
/// scope is then never debited, blocked or warned.</item>
/// </list>
/// </summary>
public static class AiCreditMeteringEndpoints
{
    public static void MapAiCreditMeteringEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/billing/ai/credit/consumption", async (string? company, int? limit,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            await AiCreditSweep.RunAsync(db);
            var manager = await IsManagerAsync(db, me);
            var snap = await AiCredit.LoadAsync(db, sc);
            var visible = VisibleCompanies(db, me, manager, snap);
            var companies = snap.Entities.Where(kv => kv.Value.ParentId == null)
                .Where(kv => visible == null || visible.Contains(kv.Key))
                .OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key).ToList();
            if (!string.IsNullOrWhiteSpace(company))
            {
                var id = ResolveCompany(snap, company);
                if (id == null) return Bad($"Unknown company '{company}' — use a top-level entity slug or id.");
                if (!companies.Contains(id.Value))
                    return Results.Json(new { detail = "That company is outside your scope." }, statusCode: 403);
                companies = new List<int> { id.Value };
            }

            var now = DateTime.UtcNow;
            var since = now.AddDays(-30);
            var take = Math.Clamp(limit ?? 10, 1, 100);
            var opened = companies.Where(id => snap.ByEntity.ContainsKey(id)).ToList();
            var lines = opened.Count == 0
                ? new List<AiCreditEntry>()
                : await db.AiCreditEntries.AsNoTracking()
                    .Where(e => e.Kind == AiCreditKinds.Consumption && e.Scope == AiCreditScopes.Entity
                                && e.EntityId != null && opened.Contains(e.EntityId.Value) && e.CreatedAt >= since)
                    .ToListAsync();

            var rows = new List<object>();
            var pausedCount = 0;
            foreach (var id in companies)
            {
                var status = snap.Company(id);
                var mine = lines.Where(l => l.EntityId == id).ToList();
                double Used(TimeSpan span) => AiCredit.Round(-mine.Where(l => l.CreatedAt >= now - span).Sum(l => l.Amount));
                double? threshold = null;
                if (snap.ByEntity.TryGetValue(id, out var account))
                    threshold = await AiCreditSweep.ThresholdAsync(db, account);
                if (status.Exhausted) pausedCount++;
                rows.Add(new
                {
                    id,
                    name = snap.NameOf(id),
                    slug = snap.Entities.TryGetValue(id, out var e) ? e.Slug : "",
                    on_credit = status.Opened && status.Enabled,
                    enabled = status.Enabled,
                    opened = status.Opened,
                    balance = status.Balance,
                    currency = status.Currency,
                    overdraft_limit = status.OverdraftLimit,
                    low_balance_threshold = status.LowBalanceThreshold,
                    warn_at = threshold,
                    low = status.Opened && status.Enabled && !status.Exhausted && threshold is double th && status.Balance <= th,
                    // the honest state: this company's AI is not answering for lack of credit
                    paused = status.Exhausted,
                    paused_reason = status.Exhausted ? AiCreditGuard.Reason(status)[5..] : null,
                    used_24h = Used(TimeSpan.FromDays(1)),
                    used_7d = Used(TimeSpan.FromDays(7)),
                    used_30d = Used(TimeSpan.FromDays(30)),
                    recent = mine.OrderByDescending(l => l.Id).Take(take).Select(l => new
                    {
                        id = l.Id, amount = l.Amount, balance_after = l.BalanceAfter, currency = l.Currency,
                        @ref = l.Ref, note = l.Note, created_at = l.CreatedAt,
                    }).ToList(),
                });
            }

            object? metering = null;
            if (manager)
            {
                var cursor = AiCreditSweep.ReadCursor(db);
                var health = AiCreditGuard.HealthOf(db);
                metering = new
                {
                    started_at = cursor?.StartedAt,
                    last_usage_id = cursor?.Last,
                    last_sweep_at = cursor?.At,
                    pending = cursor?.PendingTo != null,
                    fail_open = health.FailOpen,
                    last_fail_open_at = health.LastFailOpenAt,
                    last_fail_open_error = health.LastFailOpenError,
                    usage_write_failures = health.MeteringFailures,
                    last_usage_write_failure_at = health.LastMeteringFailureAt,
                    sweep_failures = health.SweepFailures,
                    last_sweep_failure_at = health.LastSweepFailureAt,
                    last_sweep_failure_error = health.LastSweepError,
                    unpriced_rows = health.UnpricedRows,
                };
            }

            return Results.Ok(new
            {
                currency = snap.Currency,
                is_manager = manager,
                companies = rows,
                paused = pausedCount,
                metering,
                note = "Each consumption line is AI usage the credit sweep debited — priced exactly like the AI bill "
                       + "(same price book, markup and exchange rate), summed unrounded and rounded once to 6 decimals. "
                       + "Only a company on credit (an enabled account) is debited, and its AI pauses — chats are handed "
                       + "to a person — once the balance reaches minus its overdraft limit. Balances are never clamped: "
                       + "the last replies before it runs out overshoot, and that carries into the next top-up.",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated)
            .RequireAnyCapability(AiCreditEndpoints.ViewCap, AiCreditEndpoints.ManageCap);

        app.MapPut("/api/billing/ai/credit/account", async (JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return Bad("Send {entity: \"<company slug or id>\", enabled: true, overdraft_limit: 0, "
                           + "low_balance_threshold: 10} — or user instead of entity.");
            var snap = await AiCredit.LoadAsync(db, sc);
            var entityRaw = Str(body, "entity");
            var userRaw = Str(body, "user");
            if ((entityRaw.Length > 0) == (userRaw.Length > 0))
                return Bad("Send exactly one of entity (a company) or user (a login).");

            if (!TryBool(body, "enabled", out var enabled, out var err)) return Bad(err);
            if (!TryAmount(body, "overdraft_limit", out var overdraft, out err)) return Bad(err);
            if (!TryAmount(body, "low_balance_threshold", out var threshold, out err)) return Bad(err);
            if (enabled == null && overdraft == null && threshold == null)
                return Bad("Nothing to change: send enabled, overdraft_limit and/or low_balance_threshold.");

            string scope, target, who;
            int? entityId = null;
            var user = "";
            if (userRaw.Length > 0)
            {
                var username = await db.Users.AsNoTracking()
                    .Where(u => u.Username.ToLower() == userRaw.ToLower()).Select(u => u.Username).FirstOrDefaultAsync();
                if (username == null) return Bad($"Unknown user '{userRaw}'.");
                scope = AiCreditScopes.User;
                user = AiCredit.NormalizeUser(username);
                target = "user:" + user;
                who = username;
            }
            else
            {
                var id = ResolveEntity(snap, entityRaw);
                if (id == null) return Bad($"Unknown entity '{entityRaw}'.");
                if (snap.Entities.TryGetValue(id.Value, out var e) && e.ParentId != null)
                    return Bad($"{e.Name} is a branch — credit is held (and switched on or off) by its company.");
                scope = AiCreditScopes.Entity;
                entityId = id;
                target = "entity:" + id.Value.ToString(CultureInfo.InvariantCulture);
                who = snap.NameOf(id.Value);
            }

            var now = DateTime.UtcNow;
            var account = scope == AiCreditScopes.User
                ? await db.AiCreditAccounts.FirstOrDefaultAsync(a => a.Scope == AiCreditScopes.User && a.UserName == user)
                : await db.AiCreditAccounts.FirstOrDefaultAsync(a => a.Scope == AiCreditScopes.Entity && a.EntityId == entityId);
            var created = false;
            if (account == null)
            {
                // an account with no ledger lines (balance 0) — so credit can be set up, or
                // switched OFF, before the first grant lands. Opened DISABLED unless enabled=true
                // was sent explicitly: a zero balance is already "used up", and setting only a
                // warning level must never pause a company's AI by accident.
                account = new AiCreditAccount
                {
                    Scope = scope, EntityId = entityId, UserName = user, Balance = 0,
                    Currency = snap.Currency, Enabled = enabled ?? false, CreatedAt = now, UpdatedAt = now,
                };
                db.AiCreditAccounts.Add(account);
                created = true;
            }
            var before = $"enabled {account.Enabled}, overdraft {account.OverdraftLimit}, warn at {account.LowBalanceThreshold}";
            if (enabled is bool en) account.Enabled = en;
            if (overdraft is double od) account.OverdraftLimit = od;
            if (threshold is double th) account.LowBalanceThreshold = th;
            account.UpdatedAt = now;
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                return Results.Json(new { detail = "That balance was opened at the same moment — try again." }, statusCode: 409);
            }
            AiCreditGuard.Invalidate();

            var after = $"enabled {account.Enabled}, overdraft {account.OverdraftLimit}, warn at {account.LowBalanceThreshold}";
            ChatBranch.Data.Audit.AuditTrail.Write(db, "billing.credit.account", target,
                $"AI credit settings for {who}: " + (created ? "opened with " : $"{before} → ") + after
                + (account.Enabled ? "" : " — credit is NOT enforced: never debited, never blocks the AI"),
                entityId: entityId);

            var fresh = (await AiCredit.LoadAsync(db, sc));
            var status = scope == AiCreditScopes.User ? fresh.User(user) : fresh.Company(entityId!.Value);
            return Results.Ok(new
            {
                ok = true,
                created,
                name = who,
                credit = new
                {
                    scope = status.Scope, key = status.Key, entity_id = status.EntityId, user_name = status.UserName,
                    balance = status.Balance, currency = status.Currency, enabled = status.Enabled,
                    opened = status.Opened, exhausted = status.Exhausted,
                    overdraft_limit = status.OverdraftLimit, low_balance_threshold = status.LowBalanceThreshold,
                    updated_at = status.UpdatedAt,
                },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(AiCreditEndpoints.ManageCap);
    }

    // ---- helpers (the same visibility rule as GET /api/billing/ai/credit) -----------------------

    private static Task<bool> IsManagerAsync(AppDbContext db, ClaimsPrincipal me) =>
        ShellScope.RoleOf(me) == Roles.SuperAdmin
            ? Task.FromResult(true)
            : ShellScope.HasCapAsync(db, me, AiCreditEndpoints.ManageCap);

    private static HashSet<int>? VisibleCompanies(AppDbContext db, ClaimsPrincipal me, bool manager,
        AiCredit.Snapshot snap)
    {
        if (manager) return null;
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        if (assigned.Count == 0) return null;
        return assigned.Select(a => snap.CompanyOf(a)).OfType<int>().ToHashSet();
    }

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

    private static bool TryBool(JsonElement body, string name, out bool? value, out string error)
    {
        value = null;
        error = "";
        if (!body.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return true;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = v.GetBoolean();
            return true;
        }
        error = $"{name} must be true or false.";
        return false;
    }

    /// <summary>An optional non-negative amount (absent / null = unchanged).</summary>
    private static bool TryAmount(JsonElement body, string name, out double? value, out string error)
    {
        value = null;
        error = "";
        if (!body.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return true;
        double n;
        if (v.ValueKind == JsonValueKind.String)
        {
            if (!double.TryParse((v.GetString() ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n))
            {
                error = $"{name} must be a number.";
                return false;
            }
        }
        else if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out n))
        {
            error = $"{name} must be a number.";
            return false;
        }
        if (!double.IsFinite(n) || n < 0 || n > 1_000_000_000)
        {
            error = $"{name} must be between 0 and 1,000,000,000.";
            return false;
        }
        value = Math.Round(n, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    private static IResult Bad(string detail) => Results.BadRequest(new { detail });
}
