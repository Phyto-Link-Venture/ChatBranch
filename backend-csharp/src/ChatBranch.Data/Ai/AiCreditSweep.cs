using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Prepaid AI credit CONSUMPTION (phase 2): a single-writer sweep that turns
/// <c>crm_llmusage</c> rows into <c>consumption</c> ledger lines.
///
/// <para><b>Why a sweep and not inline writes.</b> The 16 usage write sites sit
/// inside "never break the reply" <c>catch {}</c> blocks — a debit inlined there
/// would be swallowed (free AI, undetectably), and every new AI feature would be
/// one more site to forget. The sweep reads the ledger every feature already
/// writes, so it covers features that do not exist yet.</para>
///
/// <para><b>Priced exactly like the bill.</b> Each row: its cost
/// (<see cref="LlmPricing.RowCost"/>), its company (its own entity's top-level
/// ancestor, else its contact's company when ALL of the contact's links share
/// one — the rule <c>AiBillingEndpoints.BuildBillAsync</c> and
/// <see cref="AiQuota.Build"/> both use), and its price
/// (<see cref="AiPriceBooks.Resolve"/> on the row's own entity chain, else its
/// company's, with the terms of the row's OWN month —
/// <see cref="EffectivePrice.UsageAmount"/>, the bill's usage amount). So a
/// company's debits for a month equal its bill's usage line. The monthly fee and
/// a flat plan's fixed amount are not usage and are never debited; a flat plan
/// therefore consumes nothing. Unattributed usage (no company) is never debited
/// to anyone.</para>
///
/// <para><b>Rounding.</b> Per-row amounts are summed UNROUNDED and rounded once,
/// to <see cref="AiCredit.Decimals"/> dp, per ledger line — rounding each reply to
/// cents would make most replies free.</para>
///
/// <para><b>Who is debited.</b> Only a scope with an ENABLED account row, and only
/// for usage created after that account was opened (no backfill). A company with no
/// account, or with credit off, is untouched. A row carrying a
/// <see cref="LlmUsage.UserName"/> (only interactive features stamp one) is ALSO
/// debited from that login's per-login allowance when that login has an enabled
/// account — in addition to its company, exactly as user quotas sit beside company
/// quotas, and even when the row has no company.</para>
///
/// <para><b>Single writer, crash-safe.</b> The cursor is the Setting
/// <see cref="CursorKey"/> (<c>{last, pending_from, pending_to, lease, lease_until}</c>),
/// claimed and advanced only by compare-and-swap on its stored value, under a
/// lease, so two workers cannot both debit. The cursor keys on usage ID (never on
/// counts: the test lab deletes usage rows behind it). Each batch first PINS its
/// id range in the cursor, then writes its lines with idempotency keys built from
/// that range (<c>consumption:&lt;scope&gt;:&lt;key&gt;:&lt;currency&gt;:&lt;from&gt;-&lt;to&gt;</c>),
/// then advances. A crash anywhere replays the SAME range with the SAME keys, so a
/// line already written is never written twice and the rest are completed.</para>
///
/// <para><b>First run.</b> With no cursor yet the sweep starts at the current
/// highest usage id and debits nothing — past usage was billed by the monthly bill
/// and is never retro-debited.</para>
///
/// <para>Rows newer than <see cref="Settle"/> wait for the next pass: on Postgres a
/// lower id can commit a moment after a higher one, and the cursor never goes
/// back.</para>
/// </summary>
public static class AiCreditSweep
{
    public const string CursorKey = "ai_credit_sweep";
    public const string WarnedKey = "ai_credit_warned";
    public const int BatchSize = 2000;
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LeaseFor = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> CrashAfter = new();

    /// <summary>TEST SEAM: on this database, die right after the <paramref name="entries"/>-th ledger line
    /// a batch writes (null clears) — the crash-and-rerun test.</summary>
    public static void SetCrashAfterForTests(AppDbContext db, int? entries)
    {
        if (entries is int n) CrashAfter[AiCreditGuard.Key(db)] = n;
        else CrashAfter.TryRemove(AiCreditGuard.Key(db), out _);
    }

    /// <summary>The stored cursor.</summary>
    public sealed class Cursor
    {
        /// <summary>Every usage id at or below this is done.</summary>
        [JsonPropertyName("last")] public int Last { get; set; }
        /// <summary>A batch in flight: its pinned range (null = none).</summary>
        [JsonPropertyName("pending_from")] public int? PendingFrom { get; set; }
        [JsonPropertyName("pending_to")] public int? PendingTo { get; set; }
        [JsonPropertyName("lease")] public string? Lease { get; set; }
        [JsonPropertyName("lease_until")] public DateTime? LeaseUntil { get; set; }
        /// <summary>When the cursor was first set (metering starts here).</summary>
        [JsonPropertyName("started_at")] public DateTime StartedAt { get; set; }
        /// <summary>The last time a pass finished.</summary>
        [JsonPropertyName("at")] public DateTime? At { get; set; }
    }

    /// <summary>What one pass did.</summary>
    public sealed record Outcome(string Status, int Batches, int Rows, int Entries, double Debited, int Cursor,
        IReadOnlyList<string> Errors)
    {
        public const string Done = "done";
        public const string Started = "started";
        public const string Busy = "busy";
        public const string Failed = "failed";
    }

    /// <summary>The cursor as stored, or null before the first pass.</summary>
    public static Cursor? ReadCursor(AppDbContext db)
    {
        var raw = db.Settings.AsNoTracking().Where(s => s.Key == CursorKey && s.EntityId == null)
            .Select(s => s.Value).FirstOrDefault();
        return raw == null ? null : Parse(raw);
    }

    /// <summary>
    /// Run one pass (up to <paramref name="maxBatches"/> batches) on a FRESH context
    /// of the same database, so nothing the caller has pending is flushed. Then
    /// send the low-balance warnings and any pending credit alerts. Never throws.
    /// <paramref name="nowUtc"/> is injectable for tests (rows newer than
    /// now − <see cref="Settle"/> wait).
    /// </summary>
    public static async Task<Outcome> RunAsync(AppDbContext callerDb, DateTime? nowUtc = null, int maxBatches = 10,
        CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        AppDbContext? db = null;
        try
        {
            db = AiCreditGuard.Fresh(callerDb);
            var sc = new ScopingService(db);
            var outcome = await SweepAsync(db, sc, now, maxBatches, ct);
            if (outcome.Entries > 0) AiCreditGuard.Invalidate();
            try { await WarnAsync(db, sc, ct); } catch { /* warnings are best-effort */ }
            FlushAlerts(db);
            return outcome;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ai-credit] sweep failed: {ex.GetType().Name}: {ex.Message}");
            // a batch that fails every pass stays pinned and nobody is debited — never silently
            try
            {
                var health = AiCreditGuard.HealthOf(callerDb);
                var n = health.RecordSweepFailure(ex);
                using var alert = AiCreditGuard.Fresh(callerDb);
                NotificationService.Notify(alert, NotificationCategories.Admin, "system",
                    "AI credit metering is failing", $"The AI credit sweep failed {n} time(s) since the server started "
                    + $"(last: {health.LastSweepError}). Until it recovers, AI usage is not debited from credit "
                    + "balances and out-of-credit companies are not paused.", "/billing",
                    NotificationAudience.Admins(), new NotifyOptions { RefKey = "ai-credit:sweep-failed" });
            }
            catch { }
            return new Outcome(Outcome.Failed, 0, 0, 0, 0, 0, new[] { ex.Message });
        }
        finally
        {
            if (db != null) await db.DisposeAsync();
        }
    }

    /// <summary>Synchronous <see cref="RunAsync"/> for the timer worker.</summary>
    public static Outcome Run(AppDbContext callerDb, DateTime? nowUtc = null) =>
        RunAsync(callerDb, nowUtc).GetAwaiter().GetResult();

    // ---- the cursor ------------------------------------------------------------------------

    private static Cursor Parse(string raw)
    {
        try { return JsonSerializer.Deserialize<Cursor>(raw, Json) ?? new Cursor(); }
        catch { return new Cursor(); }
    }

    private static string Write(Cursor c) => JsonSerializer.Serialize(c);

    /// <summary>Swap the stored cursor from <paramref name="expected"/> to <paramref name="next"/>, atomically.</summary>
    private static async Task<string?> SwapAsync(AppDbContext db, string expected, Cursor next, DateTime now,
        CancellationToken ct)
    {
        var value = Write(next);
        var n = await db.Settings
            .Where(s => s.Key == CursorKey && s.EntityId == null && s.Value == expected)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value).SetProperty(x => x.UpdatedAt, now), ct);
        return n == 1 ? value : null;
    }

    private static async Task<Outcome> SweepAsync(AppDbContext db, ScopingService sc, DateTime now, int maxBatches,
        CancellationToken ct)
    {
        var raw = await db.Settings.AsNoTracking().Where(s => s.Key == CursorKey && s.EntityId == null)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        // first pass: start at the newest usage row and debit nothing (no backfill)
        if (raw == null)
        {
            var top = await db.LlmUsage.AsNoTracking().Select(u => (int?)u.Id).MaxAsync(ct) ?? 0;
            var start = new Cursor { Last = top, StartedAt = now, At = now };
            try
            {
                db.Settings.Add(new Setting { Key = CursorKey, Value = Write(start), UpdatedAt = now });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();   // another worker created it first — fine
            }
            return new Outcome(Outcome.Started, 0, 0, 0, 0, top, Array.Empty<string>());
        }

        // claim it: a lease another live worker holds means "not now"
        var cur = Parse(raw);
        if (cur.Lease != null && cur.LeaseUntil is DateTime until && until > now)
            return new Outcome(Outcome.Busy, 0, 0, 0, 0, cur.Last, Array.Empty<string>());
        var lease = Guid.NewGuid().ToString("N");
        cur.Lease = lease;
        cur.LeaseUntil = now + LeaseFor;
        var held = await SwapAsync(db, raw, cur, now, ct);
        if (held == null) return new Outcome(Outcome.Busy, 0, 0, 0, 0, cur.Last, Array.Empty<string>());

        var errors = new List<string>();
        int batches = 0, rows = 0, entries = 0;
        double debited = 0;
        var finished = false;
        try
        {
            var prices = LlmPricing.PriceTable(sc);
            var billing = AiBilling.Load(sc);
            var cutoff = now - Settle;
            while (batches < maxBatches)
            {
                ct.ThrowIfCancellationRequested();
                // 1. the range: the one pinned by a pass that did not finish, else the next ids
                int from, to;
                if (cur.PendingFrom is int pf && cur.PendingTo is int pt && pt >= pf)
                {
                    from = pf;
                    to = pt;
                }
                else
                {
                    var last = cur.Last;
                    var next = await db.LlmUsage.AsNoTracking().Where(u => u.Id > last).OrderBy(u => u.Id)
                        .Select(u => new { u.Id, u.CreatedAt }).Take(BatchSize).ToListAsync(ct);
                    // stop at the first row too new to be sure every lower id has committed
                    var settled = next.TakeWhile(u => u.CreatedAt <= cutoff).ToList();
                    if (settled.Count == 0) break;
                    from = last + 1;
                    to = settled[^1].Id;
                    cur.PendingFrom = from;
                    cur.PendingTo = to;
                    held = await SwapAsync(db, held, cur, now, ct);
                    if (held == null) { errors.Add("lost the sweep lease"); return Result(); }
                }

                // 2. debit it (idempotent per scope + range — a replay writes nothing twice)
                var (n, lines, amount, errs) = await DebitRangeAsync(db, sc, billing, prices, from, to, ct);
                rows += n;
                entries += lines;
                debited += amount;
                errors.AddRange(errs);
                batches++;

                // 3. advance
                cur.Last = Math.Max(cur.Last, to);
                cur.PendingFrom = null;
                cur.PendingTo = null;
                held = await SwapAsync(db, held, cur, now, ct);
                if (held == null) { errors.Add("lost the sweep lease"); return Result(); }
            }
            finished = true;
        }
        finally
        {
            // let go of the lease (best effort: it expires by itself after LeaseFor)
            try
            {
                if (held != null)
                {
                    cur.Lease = null;
                    cur.LeaseUntil = null;
                    if (finished) cur.At = now;   // a failed pass never looks like a healthy one
                    await SwapAsync(db, held, cur, now, CancellationToken.None);
                }
            }
            catch { }
        }
        return Result();

        Outcome Result() => new(Outcome.Done, batches, rows, entries, AiCredit.Round(debited), cur.Last, errors);
    }

    // ---- one batch -----------------------------------------------------------------------------

    private static async Task<(int Rows, int Entries, double Debited, List<string> Errors)> DebitRangeAsync(
        AppDbContext db, ScopingService sc, BillingSettings billing, Dictionary<string, (double In, double Out)> prices,
        int from, int to, CancellationToken ct)
    {
        var errors = new List<string>();
        var accounts = await db.AiCreditAccounts.AsNoTracking().Where(a => a.Enabled).ToListAsync(ct);
        // nobody is on credit: nothing to price, nothing to write
        if (accounts.Count == 0) return (0, 0, 0, errors);

        var byCompany = accounts.Where(a => a.Scope == AiCreditScopes.Entity && a.EntityId != null)
            .GroupBy(a => a.EntityId!.Value).ToDictionary(g => g.Key, g => g.First());
        var byUser = accounts.Where(a => a.Scope == AiCreditScopes.User && a.UserName.Length > 0)
            .GroupBy(a => AiCredit.NormalizeUser(a.UserName))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var usage = await db.LlmUsage.AsNoTracking().Where(u => u.Id >= from && u.Id <= to)
            .Select(u => new
            {
                u.Id, u.Feature, u.Model, u.EntityId, u.ContactId, u.UserName, u.CreatedAt,
                u.PromptTokens, u.CompletionTokens, u.AudioPromptTokens, u.AudioCompletionTokens, u.CostUsd,
            }).ToListAsync(ct);
        if (usage.Count == 0) return (0, 0, 0, errors);

        // ---- the bill's attribution rule
        var parents = await db.Entities.AsNoTracking().Select(e => new { e.Id, e.ParentId }).ToListAsync(ct);
        var parentOf = parents.ToDictionary(e => e.Id, e => e.ParentId);
        int? CompanyOf(int? entityId)
        {
            var cur = entityId;
            for (var i = 0; i < 32 && cur is int id && parentOf.TryGetValue(id, out var parent); i++)
            {
                if (parent == null) return id;
                cur = parent;
            }
            return null;
        }
        var contactIds = usage.Where(u => u.EntityId == null || CompanyOf(u.EntityId) == null)
            .Select(u => u.ContactId).OfType<int>().Distinct().ToList();
        var contactCompany = contactIds.Count == 0
            ? new Dictionary<int, int>()
            : (await db.ContactEntities.AsNoTracking().Where(l => contactIds.Contains(l.ContactId))
                .Select(l => new { l.ContactId, l.EntityId }).ToListAsync(ct))
            .GroupBy(l => l.ContactId)
            .Select(g => (g.Key, Companies: g.Select(l => CompanyOf(l.EntityId)).Distinct().ToList()))
            .Where(x => x.Companies.Count == 1 && x.Companies[0] != null)
            .ToDictionary(x => x.Key, x => x.Companies[0]!.Value);

        // ---- the bill's price: the row's own entity's plan, else its company's, that month's terms
        var chains = sc.ChainMap();
        var termsByMonth = new Dictionary<string, BillingTerms>();
        var priceCache = new Dictionary<(string Month, int? Entity), EffectivePrice>();
        BillingTerms TermsOf(DateTime at)
        {
            var month = AiBilling.MonthKey(at);
            if (!termsByMonth.TryGetValue(month, out var t)) termsByMonth[month] = t = billing.TermsFor(month);
            return t;
        }
        EffectivePrice PriceOf(BillingTerms terms, string month, int? entityId)
        {
            if (priceCache.TryGetValue((month, entityId), out var hit)) return hit;
            var price = entityId is int id
                ? AiPriceBooks.Resolve(terms, chains.TryGetValue(id, out var chain) ? chain : new List<int> { id })
                : AiPriceBooks.Resolve(terms, null);
            priceCache[(month, entityId)] = price;
            return price;
        }

        // ---- sum per scope, UNROUNDED
        var totals = new Dictionary<(string Scope, string Key, string Currency), (double Amount, int Rows, AiCreditAccount Account)>();
        void Add(string scope, string key, string currency, double amount, AiCreditAccount account)
        {
            var k = (scope, key, currency);
            var (a, n, _) = totals.TryGetValue(k, out var v) ? v : (0d, 0, account);
            totals[k] = (a + amount, n + 1, account);
        }
        var unpriced = 0;
        foreach (var u in usage)
        {
            var company = CompanyOf(u.EntityId)
                          ?? (u.ContactId is int cid && contactCompany.TryGetValue(cid, out var cc) ? cc : (int?)null);
            var user = AiCredit.NormalizeUser(u.UserName);
            AiCreditAccount? companyAccount = company is int co && byCompany.TryGetValue(co, out var ca)
                                                                && u.CreatedAt >= ca.CreatedAt ? ca : null;
            AiCreditAccount? userAccount = user.Length > 0 && byUser.TryGetValue(user, out var ua)
                                                           && u.CreatedAt >= ua.CreatedAt ? ua : null;
            if (companyAccount == null && userAccount == null) continue;

            var terms = TermsOf(u.CreatedAt);
            var month = AiBilling.MonthKey(u.CreatedAt);
            var cost = LlmPricing.RowCost(prices, u.Model, u.PromptTokens, u.CompletionTokens,
                u.AudioPromptTokens, u.AudioCompletionTokens, u.CostUsd);
            var tokens = (long)u.PromptTokens + u.CompletionTokens + u.AudioPromptTokens + u.AudioCompletionTokens;
            var amount = PriceOf(terms, month, u.EntityId ?? company)
                .UsageAmount(cost, tokens, AiPriceBooks.IsReply(u.Feature) ? 1 : 0, terms.UsdRate);
            // no provider price and a model the stored table does not know: it costs 0 here
            // (the bill refreshes the model catalog and may price it later) — count it
            if (u.CostUsd == null && cost == 0 && tokens > 0) unpriced++;
            if (!double.IsFinite(amount) || amount <= 0) continue;

            if (companyAccount != null)
                Add(AiCreditScopes.Entity, companyAccount.EntityId!.Value.ToString(CultureInfo.InvariantCulture),
                    terms.Currency, amount, companyAccount);
            if (userAccount != null)
                Add(AiCreditScopes.User, user, terms.Currency, amount, userAccount);
        }

        if (unpriced > 0)
        {
            AiCreditGuard.HealthOf(db).RecordUnpriced(unpriced);
            errors.Add($"{unpriced} usage row(s) in ids {from}-{to} had no price (unknown model, no provider cost) and were debited as 0");
        }

        // ---- one line per scope, rounded ONCE
        var written = 0;
        double debited = 0;
        var range = $"{from.ToString(CultureInfo.InvariantCulture)}-{to.ToString(CultureInfo.InvariantCulture)}";
        foreach (var ((scope, key, currency), (sum, n, account)) in totals)
        {
            var amount = AiCredit.Round(sum);
            if (amount <= 0) continue;
            var req = new AiCredit.Request(scope,
                scope == AiCreditScopes.Entity ? account.EntityId : null,
                scope == AiCreditScopes.User ? key : "",
                AiCreditKinds.Consumption, -amount, currency,
                Note: n == 1 ? "AI usage: 1 request" : $"AI usage: {n.ToString(CultureInfo.InvariantCulture)} requests",
                Ref: $"llmusage:{from.ToString(CultureInfo.InvariantCulture)}..{to.ToString(CultureInfo.InvariantCulture)}",
                IdempotencyKey: $"consumption:{scope}:{key}:{currency}:{range}",
                Actor: "system", Source: AuditSources.System);
            var result = await AiCredit.ApplyAsync(db, req, ct);
            if (!result.Ok)
            {
                // e.g. the billing currency changed under a balance: this usage cannot be
                // debited — say so loudly instead of stalling every other company
                var what = scope == AiCreditScopes.Entity ? $"company #{key}" : $"login {key}";
                var msg = $"{AiCredit.Money(currency, amount)} of AI usage (ids {range}) could not be debited from {what}: {result.Error}";
                errors.Add(msg);
                Console.Error.WriteLine("[ai-credit] " + msg);
                NotifyDebitFailed(db, account, msg);
                continue;
            }
            if (!result.Replayed)
            {
                written++;
                debited += amount;
                if (CrashAfter.TryGetValue(AiCreditGuard.Key(db), out var crashAt) && written >= crashAt)
                    throw new InvalidOperationException("simulated crash mid-sweep (test seam)");
            }
        }
        return (usage.Count, written, debited, errors);
    }

    private static void NotifyDebitFailed(AppDbContext db, AiCreditAccount account, string message)
    {
        try
        {
            NotificationService.Notify(db, NotificationCategories.Admin, "system",
                "AI usage could not be debited from credit", message, "/billing",
                NotificationAudience.Admins(), new NotifyOptions
                {
                    RefKey = $"ai-credit:debit-failed:{account.Scope}:{account.EntityId?.ToString(CultureInfo.InvariantCulture) ?? account.UserName}",
                });
        }
        catch { }
    }

    // ---- low-balance warnings --------------------------------------------------------------------

    private sealed class Warned
    {
        /// <summary>"entity:&lt;id&gt;" / "user:&lt;name&gt;" → "low" | "out".</summary>
        [JsonPropertyName("sent")] public Dictionary<string, string> Sent { get; set; } = new();
    }

    public const string LevelLow = "low";
    public const string LevelOut = "out";

    /// <summary>
    /// The balance at which this account's admins are warned:
    /// <see cref="AiCreditAccount.LowBalanceThreshold"/> when set, else 10 % of the
    /// most recent credit added (grant, top-up, positive correction), else 0.
    /// </summary>
    public static async Task<double> ThresholdAsync(AppDbContext db, AiCreditAccount a, CancellationToken ct = default)
    {
        if (a.LowBalanceThreshold > 0) return a.LowBalanceThreshold;
        var q = db.AiCreditEntries.AsNoTracking().Where(e => e.Amount > 0 && e.Scope == a.Scope);
        q = a.Scope == AiCreditScopes.User ? q.Where(e => e.UserName == a.UserName) : q.Where(e => e.EntityId == a.EntityId);
        var lastCredit = await q.OrderByDescending(e => e.Id).Select(e => (double?)e.Amount).FirstOrDefaultAsync(ct) ?? 0;
        return AiCredit.Round(lastCredit * 0.1);
    }

    /// <summary>
    /// Warn once when an ENABLED balance falls to its low-balance threshold and once
    /// when it runs out (the AI pauses). Re-armed when the balance recovers — credit
    /// is not monthly, so the next depletion warns again. Disabled or missing
    /// accounts are never warned. Returns how many warnings went out.
    /// </summary>
    public static async Task<int> WarnAsync(AppDbContext db, ScopingService sc, CancellationToken ct = default)
    {
        Warned warned;
        try { warned = JsonSerializer.Deserialize<Warned>(sc.SettingValue(WarnedKey), Json) ?? new Warned(); }
        catch { warned = new Warned(); }
        warned.Sent ??= new();
        var before = JsonSerializer.Serialize(warned);

        var accounts = await db.AiCreditAccounts.AsNoTracking().ToListAsync(ct);
        var live = new HashSet<string>();
        var sent = 0;
        foreach (var a in accounts)
        {
            var key = a.Scope == AiCreditScopes.User
                ? "user:" + a.UserName
                : "entity:" + a.EntityId?.ToString(CultureInfo.InvariantCulture);
            if (!a.Enabled) continue;
            live.Add(key);
            var threshold = await ThresholdAsync(db, a, ct);
            var level = a.Balance <= -a.OverdraftLimit ? LevelOut : a.Balance <= threshold ? LevelLow : null;
            if (level == null)
            {
                warned.Sent.Remove(key);   // recovered: re-arm
                continue;
            }
            // only a WORSE level than the one already sent notifies; a partial top-up
            // (out → low) is remembered silently, so running out again warns again
            var had = warned.Sent.GetValueOrDefault(key);
            warned.Sent[key] = level;
            if (Rank(level) <= Rank(had)) continue;
            Notify(db, a, level, threshold);
            sent++;
        }
        // forget accounts that are gone or switched off
        foreach (var k in warned.Sent.Keys.Where(k => !live.Contains(k)).ToList()) warned.Sent.Remove(k);
        var after = JsonSerializer.Serialize(warned);
        if (after != before) sc.SetSetting(WarnedKey, after);
        return sent;
    }

    private static int Rank(string? level) => level switch { LevelOut => 2, LevelLow => 1, _ => 0 };

    private static void Notify(AppDbContext db, AiCreditAccount a, string level, double threshold)
    {
        var balance = AiCredit.Money(a.Currency, a.Balance);
        string title, body;
        int? tenant = null;
        int? userId = null;
        if (a.Scope == AiCreditScopes.User)
        {
            var u = db.Users.AsNoTracking().Where(x => x.Username.ToLower() == a.UserName)
                .Select(x => new { x.Id, x.Username, Tenant = x.Profile != null ? x.Profile.TenantId : null })
                .FirstOrDefault();
            userId = u?.Id;
            tenant = u?.Tenant;
            var name = u?.Username ?? a.UserName;
            title = level == LevelOut ? $"AI credit used up — {name}" : $"AI credit running low — {name}";
            body = $"Balance {balance}. " + (level == LevelOut
                ? "Ask ChatBranch, the playground, skill drafting and summaries are paused for this person until credit is added."
                : $"At or below the warning level of {AiCredit.Money(a.Currency, threshold)}; at zero their AI features pause.");
        }
        else
        {
            var co = db.Entities.AsNoTracking().Where(e => e.Id == a.EntityId)
                .Select(e => new { e.Name, e.TenantId }).FirstOrDefault();
            tenant = co?.TenantId;
            var name = co?.Name ?? $"#{a.EntityId}";
            title = level == LevelOut ? $"AI paused: out of credit — {name}" : $"AI credit running low — {name}";
            body = $"Balance {balance}. " + (level == LevelOut
                ? "The AI stops answering this company's chats and hands them to a person until credit is added."
                : $"At or below the warning level of {AiCredit.Money(a.Currency, threshold)}; at zero the AI stops answering and chats go to a person.");
        }
        var opts = new NotifyOptions
        {
            RefKey = $"ai-credit:{a.Scope}:{a.EntityId?.ToString(CultureInfo.InvariantCulture) ?? a.UserName}:{level}",
        };
        NotificationService.Notify(db, NotificationCategories.Admin, "system", title, body, "/billing",
            NotificationAudience.Admins(tenant, a.Scope == AiCreditScopes.Entity ? a.EntityId : null), opts);
        if (userId is int uid)
            NotificationService.Notify(db, NotificationCategories.Admin, "system", title, body, "/billing",
                NotificationAudience.ForUsers(new[] { uid }), opts);
    }

    // ---- reading ---------------------------------------------------------------------------------

    /// <summary><see cref="AiCredit.LedgerAsync"/> limited to some kinds, newest first.</summary>
    public static async Task<List<AiCreditEntry>> LedgerOfKindsAsync(AppDbContext db, int? entityId, string? username,
        IReadOnlyCollection<string> kinds, int take = 50, int skip = 0, CancellationToken ct = default)
    {
        var q = db.AiCreditEntries.AsNoTracking().Where(e => kinds.Contains(e.Kind));
        if (entityId is int id) q = q.Where(e => e.EntityId == id);
        else if (!string.IsNullOrWhiteSpace(username))
        {
            var user = AiCredit.NormalizeUser(username);
            q = q.Where(e => e.UserName == user);
        }
        return await q.OrderByDescending(e => e.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
    }

    // ---- pending alerts --------------------------------------------------------------------------

    /// <summary>Deliver the fail-open and swallowed-metering alerts that could not be written when they happened.</summary>
    private static void FlushAlerts(AppDbContext db)
    {
        try
        {
            var health = AiCreditGuard.HealthOf(db);
            if (health.FailOpenUnnotified)
            {
                var n = health.FailOpen;
                if (AiCreditGuard.NotifyFailOpen(db, health)) health.FailOpenNotified(n);
            }
            if (health.MeteringUnnotified)
            {
                var n = health.MeteringFailures;
                if (AiCreditGuard.NotifyMetering(db, health)) health.MeteringNotified(n);
            }
        }
        catch { }
    }
}
