using System.Globalization;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai;

/// <summary>One balance with the names a screen needs.</summary>
public sealed record AiCreditStatus(
    string Scope,
    string Key,
    int? EntityId,
    string UserName,
    string Name,
    double Balance,
    string Currency,
    bool Enabled,
    double OverdraftLimit,
    double LowBalanceThreshold,
    DateTime? UpdatedAt)
{
    /// <summary>No account row yet: shown as zero, enforced by nothing.</summary>
    public bool Opened => UpdatedAt != null;
    /// <summary>The money is gone (phase 2 stops the AI here; phase 1 only says so).</summary>
    public bool Exhausted => Opened && Enabled && Balance <= -OverdraftLimit;
}

/// <summary>
/// Prepaid AI credit: a balance a company (or a login) spends its AI from.
///
/// <para><b>The ledger is the truth.</b> <c>crm_aicreditentry</c> is append-only —
/// never updated, never deleted; a correction is a new signed line.
/// <c>crm_aicreditaccount.balance</c> is a CACHE of that ledger's sum, kept in
/// step by one atomic <c>ExecuteUpdate(balance = balance + x)</c> issued inside
/// the entry's own transaction. Nothing reads the balance, adds in C# and writes
/// it back — that would lose a concurrent grant or debit
/// (<see cref="ApplyAsync"/>).</para>
///
/// <para><b>Where credit attaches.</b> To the COMPANY — a top-level
/// <see cref="Entity"/> — exactly like the AI bill and the AI quotas. A branch
/// has no balance of its own: it draws on its top-level parent's, found by
/// walking the chain (<see cref="Snapshot.CompanyOf"/>), the same rule
/// <c>AiQuota.Snapshot.Blocking</c> uses.</para>
///
/// <para><b>The optional per-login balance</b> (<see cref="AiCreditScopes.User"/>)
/// mirrors <c>AiQuotaConfig.Users</c>. It can only ever cover what a person runs
/// THEMSELVES — the operator assistant, the AI playground, skill drafting and
/// on-demand summaries — because <see cref="LlmUsage.UserName"/> is "" for every
/// automated AI call (customer replies, flows, voice, embeddings). It is not a
/// seat licence and it cannot meter the product's main cost driver.</para>
///
/// <para><b>Balances are SIGNED and never clamped</b> — see
/// <see cref="AiCreditAccount.Balance"/>.</para>
///
/// <para><b>Consumption and enforcement</b> (phase 2) live beside this file:
/// <see cref="AiCreditSweep"/> debits AI usage into the ledger through
/// <see cref="ApplyAsync"/>, and <see cref="AiCreditGuard"/> stops the AI of an
/// ENABLED account whose balance is at or below <c>-overdraft_limit</c>. A scope
/// with no account row is never debited or blocked.</para>
/// </summary>
public static class AiCredit
{
    /// <summary>Written amounts are rounded here — 6 dp, never 2: a single AI reply
    /// costs fractions of a cent and 2 dp would round the majority of them to zero.</summary>
    public const int Decimals = 6;
    /// <summary>What a reconciliation (ledger sum vs cached balance) may differ by.</summary>
    public const double Tolerance = 1e-6;

    public static double Round(double v) => Math.Round(v, Decimals, MidpointRounding.AwayFromZero);

    /// <summary>The account key of a login (lower-case, like <c>AiQuotaConfig.UserKey</c>).</summary>
    public static string NormalizeUser(string? username) => (username ?? "").Trim().ToLowerInvariant();

    // ---- reading -----------------------------------------------------------------------

    /// <summary>
    /// Every account with the entity tree it hangs off, read once. Deliberately
    /// NOT cached: a grant must be visible on the next read, and nothing on the
    /// inbound hot path uses this yet (enforcement is phase 2, where a short
    /// cache like <c>AiQuota.Current</c>'s belongs).
    /// </summary>
    public sealed class Snapshot
    {
        public DateTime BuiltAt { get; init; }
        /// <summary>The current billing currency (new accounts are opened in it).</summary>
        public string Currency { get; init; } = "USD";
        public Dictionary<int, (int? ParentId, string Name, string Slug)> Entities { get; init; } = new();
        /// <summary>By top-level entity id.</summary>
        public Dictionary<int, AiCreditAccount> ByEntity { get; init; } = new();
        /// <summary>By normalized username.</summary>
        public Dictionary<string, AiCreditAccount> ByUser { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The entity and its ancestors, nearest first.</summary>
        public List<int> Chain(int? entityId)
        {
            var chain = new List<int>();
            var cur = entityId;
            while (cur is int id && Entities.TryGetValue(id, out var e) && !chain.Contains(id) && chain.Count < 32)
            {
                chain.Add(id);
                cur = e.ParentId;
            }
            return chain;
        }

        /// <summary>The top-level entity this one belongs to (itself, when top-level), or null.</summary>
        public int? CompanyOf(int? entityId)
        {
            foreach (var id in Chain(entityId))
                if (Entities.TryGetValue(id, out var e) && e.ParentId == null) return id;
            return null;
        }

        public string NameOf(int entityId) =>
            Entities.TryGetValue(entityId, out var e) ? e.Name : $"#{entityId.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>
        /// The balance a conversation of this entity draws on: its COMPANY's.
        /// A branch never has one of its own. Null when the entity is unknown or
        /// orphaned (no top-level ancestor) — unattributed AI work has no company
        /// and is never debited to one.
        /// </summary>
        public AiCreditStatus? ForEntity(int? entityId)
        {
            if (CompanyOf(entityId) is not int company) return null;
            return Company(company);
        }

        /// <summary>A company's balance — a zero one when no account has been opened.</summary>
        public AiCreditStatus Company(int companyId)
        {
            var name = NameOf(companyId);
            if (!ByEntity.TryGetValue(companyId, out var a))
                return new AiCreditStatus(AiCreditScopes.Entity,
                    companyId.ToString(CultureInfo.InvariantCulture), companyId, "", name,
                    0, Currency, false, 0, 0, null);
            return Of(a, name);
        }

        /// <summary>A login's optional balance — a zero one when no account has been opened.</summary>
        public AiCreditStatus User(string username)
        {
            var key = NormalizeUser(username);
            if (!ByUser.TryGetValue(key, out var a))
                return new AiCreditStatus(AiCreditScopes.User, key, null, key, username.Trim(),
                    0, Currency, false, 0, 0, null);
            return Of(a, a.UserName);
        }

        private static AiCreditStatus Of(AiCreditAccount a, string name) => new(
            a.Scope,
            a.Scope == AiCreditScopes.User
                ? a.UserName
                : (a.EntityId?.ToString(CultureInfo.InvariantCulture) ?? ""),
            a.EntityId, a.UserName, name, a.Balance, a.Currency, a.Enabled,
            a.OverdraftLimit, a.LowBalanceThreshold, a.UpdatedAt);

        /// <summary>Every account that exists, companies first.</summary>
        public List<AiCreditStatus> All() =>
            ByEntity.Values.OrderBy(a => NameOf(a.EntityId ?? 0), StringComparer.OrdinalIgnoreCase)
                .Select(a => Of(a, NameOf(a.EntityId ?? 0)))
                .Concat(ByUser.Values.OrderBy(a => a.UserName, StringComparer.OrdinalIgnoreCase)
                    .Select(a => Of(a, a.UserName)))
                .ToList();
    }

    public static async Task<Snapshot> LoadAsync(AppDbContext db, ScopingService sc, CancellationToken ct = default)
    {
        var entities = await db.Entities.AsNoTracking()
            .Select(e => new { e.Id, e.ParentId, e.Name, e.Slug }).ToListAsync(ct);
        var accounts = await db.AiCreditAccounts.AsNoTracking().ToListAsync(ct);
        return Build(entities.Select(e => (e.Id, e.ParentId, e.Name, e.Slug)), accounts, AiBilling.Load(sc).Currency);
    }

    public static Snapshot Load(AppDbContext db, ScopingService sc)
    {
        var entities = db.Entities.AsNoTracking().Select(e => new { e.Id, e.ParentId, e.Name, e.Slug }).ToList();
        var accounts = db.AiCreditAccounts.AsNoTracking().ToList();
        return Build(entities.Select(e => (e.Id, e.ParentId, e.Name, e.Slug)), accounts, AiBilling.Load(sc).Currency);
    }

    private static Snapshot Build(IEnumerable<(int Id, int? ParentId, string Name, string Slug)> entities,
        List<AiCreditAccount> accounts, string currency) => new()
    {
        BuiltAt = DateTime.UtcNow,
        Currency = currency,
        Entities = entities.ToDictionary(e => e.Id, e => (e.ParentId, e.Name, e.Slug)),
        ByEntity = accounts.Where(a => a.Scope == AiCreditScopes.Entity && a.EntityId != null)
            .GroupBy(a => a.EntityId!.Value).ToDictionary(g => g.Key, g => g.First()),
        ByUser = accounts.Where(a => a.Scope == AiCreditScopes.User && a.UserName.Length > 0)
            .GroupBy(a => a.UserName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
    };

    // ---- writing -----------------------------------------------------------------------

    /// <summary>What <see cref="ApplyAsync"/> writes: one ledger line.</summary>
    public sealed record Request(
        string Scope,
        int? EntityId,
        string UserName,
        string Kind,
        double Amount,
        string Currency,
        string Note = "",
        string Ref = "",
        string IdempotencyKey = "",
        string Actor = "",
        string Source = "",
        int? OfferId = null,
        int? PaymentId = null);

    /// <summary>Why a write was refused (a 400 for the caller), or null when it went through.</summary>
    public sealed record Result(AiCreditEntry? Entry, AiCreditAccount? Account, string? Error, bool Replayed = false)
    {
        public bool Ok => Error == null;
    }

    /// <summary>
    /// Append one ledger line and move the balance by the same amount, atomically.
    ///
    /// <para>In ONE transaction: open the account if it does not exist, then
    /// <c>UPDATE … SET balance = balance + @amount</c> (never read-modify-write,
    /// so a concurrent grant or debit cannot be lost), read the row back for
    /// <c>balance_after</c>, and insert the entry. A duplicate
    /// <c>idempotency_key</c> violates <c>uniq_aicreditentry_idem</c>, the whole
    /// transaction rolls back and the first line is returned — so a replayed
    /// grant credits exactly once.</para>
    /// </summary>
    public static async Task<Result> ApplyAsync(AppDbContext db, Request req, CancellationToken ct = default)
    {
        var scope = req.Scope == AiCreditScopes.User ? AiCreditScopes.User : AiCreditScopes.Entity;
        var user = scope == AiCreditScopes.User ? NormalizeUser(req.UserName) : "";
        var entityId = scope == AiCreditScopes.Entity ? req.EntityId : null;
        if (scope == AiCreditScopes.Entity && entityId == null) return new Result(null, null, "A company is required.");
        if (scope == AiCreditScopes.User && user.Length == 0) return new Result(null, null, "A username is required.");
        if (!AiCreditKinds.All.Contains(req.Kind)) return new Result(null, null, $"Unknown credit kind '{req.Kind}'.");
        var amount = Round(req.Amount);
        if (!double.IsFinite(amount)) return new Result(null, null, "The amount must be a number.");
        var currency = (req.Currency ?? "").Trim().ToUpperInvariant();
        if (currency.Length != 3) return new Result(null, null, "The currency must be a 3-letter code.");
        var key = (req.IdempotencyKey ?? "").Trim();

        // a replay we have already recorded: answer with the original line
        if (key.Length > 0)
        {
            var seen = await Existing(db, key, ct);
            if (seen != null) return new Result(seen, await AccountOf(db, scope, entityId, user, ct), null, true);
        }

        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var account = await AccountOf(db, scope, entityId, user, ct);
            if (account == null)
            {
                account = new AiCreditAccount
                {
                    Scope = scope, EntityId = entityId, UserName = user,
                    Balance = 0, Currency = currency, Enabled = true,
                    CreatedAt = now, UpdatedAt = now,
                };
                db.AiCreditAccounts.Add(account);
                await db.SaveChangesAsync(ct);
                db.Entry(account).State = EntityState.Detached;
            }
            else if (!string.Equals(account.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(ct);
                return new Result(null, account, $"This balance is held in {account.Currency}, not {currency}. "
                                                 + "Set the billing currency back, or convert the balance first — a "
                                                 + "currency change does not convert existing credit.");
            }

            // the atomic move: balance = balance + amount, in the database
            var id = account.Id;
            await db.AiCreditAccounts.Where(a => a.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Balance, a => a.Balance + amount)
                    .SetProperty(a => a.UpdatedAt, now), ct);
            var after = await db.AiCreditAccounts.AsNoTracking().Where(a => a.Id == id)
                .Select(a => a.Balance).FirstAsync(ct);

            var entry = new AiCreditEntry
            {
                Scope = scope, EntityId = entityId, UserName = user,
                Kind = req.Kind, Amount = amount, Currency = currency, BalanceAfter = Round(after),
                Ref = req.Ref ?? "", IdempotencyKey = key, Note = req.Note ?? "",
                Actor = string.IsNullOrWhiteSpace(req.Actor) ? "system" : req.Actor.Trim(),
                Source = req.Source ?? "", OfferId = req.OfferId, PaymentId = req.PaymentId,
                CreatedAt = now,
            };
            db.AiCreditEntries.Add(entry);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            AiCreditGuard.Invalidate();   // a grant / top-up unblocks on the next message (phase 2)
            var fresh = await AccountOf(db, scope, entityId, user, ct);
            return new Result(entry, fresh, null);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            // the only unique constraint a line can break is the replay guard
            if (key.Length > 0 && await Existing(db, key, ct) is AiCreditEntry won)
                return new Result(won, await AccountOf(db, scope, entityId, user, ct), null, true);
            throw;
        }
    }

    private static Task<AiCreditEntry?> Existing(AppDbContext db, string key, CancellationToken ct) =>
        db.AiCreditEntries.AsNoTracking().Where(e => e.IdempotencyKey == key)
            .OrderBy(e => e.Id).FirstOrDefaultAsync(ct);

    private static Task<AiCreditAccount?> AccountOf(AppDbContext db, string scope, int? entityId, string user,
        CancellationToken ct) =>
        scope == AiCreditScopes.User
            ? db.AiCreditAccounts.AsNoTracking().FirstOrDefaultAsync(
                a => a.Scope == AiCreditScopes.User && a.UserName == user, ct)
            : db.AiCreditAccounts.AsNoTracking().FirstOrDefaultAsync(
                a => a.Scope == AiCreditScopes.Entity && a.EntityId == entityId, ct);

    /// <summary>A company's or a login's ledger, newest first.</summary>
    public static async Task<List<AiCreditEntry>> LedgerAsync(AppDbContext db, int? entityId, string? username,
        int take = 50, int skip = 0, CancellationToken ct = default)
    {
        var q = db.AiCreditEntries.AsNoTracking().AsQueryable();
        if (entityId is int id) q = q.Where(e => e.EntityId == id);
        else if (!string.IsNullOrWhiteSpace(username))
        {
            var user = NormalizeUser(username);
            q = q.Where(e => e.UserName == user);
        }
        return await q.OrderByDescending(e => e.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
    }

    // ---- reconciliation ----------------------------------------------------------------

    /// <summary>One account whose cached balance disagrees with its ledger.</summary>
    public sealed record Drift(int AccountId, string Scope, int? EntityId, string UserName,
        double Balance, double LedgerSum)
    {
        public double Difference => Balance - LedgerSum;
    }

    /// <summary>
    /// Every account whose <c>balance</c> is not <c>sum(ledger.amount)</c> within
    /// <see cref="Tolerance"/>. Empty is the invariant; anything else is a bug in
    /// a writer that did not go through <see cref="ApplyAsync"/>.
    /// </summary>
    public static async Task<List<Drift>> ReconcileAsync(AppDbContext db, CancellationToken ct = default)
    {
        var accounts = await db.AiCreditAccounts.AsNoTracking().ToListAsync(ct);
        var entries = await db.AiCreditEntries.AsNoTracking()
            .Select(e => new { e.Scope, e.EntityId, e.UserName, e.Amount }).ToListAsync(ct);
        var drift = new List<Drift>();
        foreach (var a in accounts)
        {
            var sum = a.Scope == AiCreditScopes.User
                ? entries.Where(e => e.Scope == AiCreditScopes.User
                                     && string.Equals(e.UserName, a.UserName, StringComparison.OrdinalIgnoreCase))
                    .Sum(e => e.Amount)
                : entries.Where(e => e.Scope == AiCreditScopes.Entity && e.EntityId == a.EntityId).Sum(e => e.Amount);
            if (Math.Abs(a.Balance - sum) > Tolerance)
                drift.Add(new Drift(a.Id, a.Scope, a.EntityId, a.UserName, a.Balance, sum));
        }
        return drift;
    }

    /// <summary>The money, formatted like the quotas page does it (RM for MYR).</summary>
    public static string Money(string currency, double amount) => AiQuota.Money(currency, amount);
}
