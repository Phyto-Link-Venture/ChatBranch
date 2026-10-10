using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai;

/// <summary>The Setting <c>ai_quotas</c>: monthly caps in the billing currency (0 / absent = no cap).</summary>
public sealed class AiQuotaConfig
{
    /// <summary>Entity id (as a string) → cap. A top-level entity = a company cap; any child = a branch cap.</summary>
    [JsonPropertyName("entities")] public Dictionary<string, double> Entities { get; set; } = new();
    /// <summary>Username → cap for what that person runs themselves (assistant, playground, skill drafting, summaries).</summary>
    [JsonPropertyName("users")] public Dictionary<string, double> Users { get; set; } = new();

    /// <summary>Caps a billing manager set ("entity:&lt;id&gt;" / "user:&lt;username&gt;", lower-case):
    /// an admin may not change or clear them, only a billing manager.</summary>
    [JsonPropertyName("set_by_manager")] public List<string> SetByManager { get; set; } = new();

    public static string EntityKey(string id) => "entity:" + id;
    public static string UserKey(string username) => "user:" + username.Trim().ToLowerInvariant();
    public bool ManagerSet(string key) => SetByManager.Contains(key, StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public bool IsEmpty => Entities.Values.All(v => v <= 0) && Users.Values.All(v => v <= 0);
}

/// <summary>One quota with its month-to-date spend.</summary>
public sealed record AiQuotaStatus(string Scope, string Key, int? EntityId, int? ParentId, bool Company,
    string Name, double Cap, double Spent, string Currency, int? TenantId)
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Paused = "paused";

    public double Pct => Cap > 0 ? Math.Round(Spent / Cap * 100, 1) : 0;
    public bool Reached => Cap > 0 && Spent >= Cap;
    public string State => Reached ? Paused : Cap > 0 && Spent >= Cap * 0.8 ? Warn : Ok;
    /// <summary>The "already warned" record key.</summary>
    public string WarnKey => $"{Scope}:{Key}";
}

/// <summary>
/// AI usage quotas: monthly spending caps in the BILLING currency, priced
/// exactly like the AI bill (<see cref="LlmPricing"/> × the month's
/// <see cref="BillingTerms"/>; the monthly fee is not usage and never counts).
///
/// <para><b>What counts.</b> A COMPANY cap (a top-level entity) counts every row
/// the bill attributes to that company — the row's own entity anywhere in its
/// tree, else its contact's company when all of the contact's entities share one.
/// A BRANCH cap counts rows whose <c>entity_id</c> is in the branch's subtree
/// (no contact fallback — a contact belongs to a company, not to one branch).
/// A USER cap counts rows stamped with that username
/// (<see cref="LlmUsage.UserName"/>): the operator assistant, the AI playground,
/// skill drafting and on-demand summaries — what the person runs themselves.</para>
///
/// <para><b>What it does.</b> A conversation whose entity — or ANY ancestor —
/// has reached its cap has the AI OFF (<see cref="AiEligibility"/>, rule
/// <c>quota</c>, forced), so every gate and payload follows; a user at their cap
/// is refused by the interactive AI endpoints. At 80 % and 100 % the tenant's
/// admins (and the user, for a user cap) are notified once per scope per month.
/// Months are UTC, like the bill.</para>
///
/// <para>Spend is computed once per ≤60 s per database and cached in-process, so
/// the inbound path stays cheap; with no caps configured nothing is queried.
/// A failure reading quotas fails OPEN (the AI keeps answering).</para>
/// </summary>
public static class AiQuota
{
    public const string SettingKey = "ai_quotas";
    public const string WarnedKey = "ai_quota_warned";
    public const string ScopeEntity = "entity";
    public const string ScopeUser = "user";
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, Snapshot> Cache = new();

    /// <summary>Forget every cached spend (quotas or terms changed; tests).</summary>
    public static void Invalidate() => Cache.Clear();

    public static AiQuotaConfig LoadConfig(ScopingService sc)
    {
        try
        {
            var raw = sc.SettingValue(SettingKey);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var c = JsonSerializer.Deserialize<AiQuotaConfig>(raw, Json) ?? new AiQuotaConfig();
                c.Entities ??= new();
                c.Users = new Dictionary<string, double>(c.Users ?? new(), StringComparer.OrdinalIgnoreCase);
                c.SetByManager ??= new();
                return c;
            }
        }
        catch { /* unreadable = no caps */ }
        return new AiQuotaConfig();
    }

    public static void SaveConfig(ScopingService sc, AiQuotaConfig config)
    {
        var clean = new AiQuotaConfig
        {
            Entities = config.Entities.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            Users = config.Users.Where(kv => kv.Value > 0)
                .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2), StringComparer.OrdinalIgnoreCase),
        };
        // keep a "set by a manager" mark only while its cap exists
        clean.SetByManager = (config.SetByManager ?? new())
            .Select(k => k.ToLowerInvariant()).Distinct()
            .Where(k => clean.Entities.Keys.Any(e => AiQuotaConfig.EntityKey(e) == k)
                        || clean.Users.Keys.Any(u => AiQuotaConfig.UserKey(u) == k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        sc.SetSetting(SettingKey, JsonSerializer.Serialize(clean));
        Invalidate();
    }

    // ---- the month's spend --------------------------------------------------------------

    public sealed class Snapshot
    {
        public DateTime BuiltAt { get; init; }
        public string Month { get; init; } = "";
        public string Currency { get; init; } = "USD";
        public AiQuotaConfig Config { get; init; } = new();
        public Dictionary<int, (int? ParentId, string Name, int TenantId)> Entities { get; init; } = new();
        /// <summary>Billed spend by the row's own entity.</summary>
        public Dictionary<int, double> ByEntity { get; init; } = new();
        /// <summary>Billed spend by company, as the bill attributes it.</summary>
        public Dictionary<int, double> ByCompany { get; init; } = new();
        public Dictionary<string, double> ByUser { get; init; } = new(StringComparer.OrdinalIgnoreCase);

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

        public double EntitySpend(int entityId)
        {
            if (!Entities.TryGetValue(entityId, out var e)) return 0;
            if (e.ParentId == null) return ByCompany.GetValueOrDefault(entityId);
            return ByEntity.Where(kv => Chain(kv.Key).Contains(entityId)).Sum(kv => kv.Value);
        }

        public double Cap(int entityId) =>
            Config.Entities.TryGetValue(entityId.ToString(CultureInfo.InvariantCulture), out var c) ? c : 0;

        public AiQuotaStatus EntityStatus(int entityId)
        {
            var e = Entities.TryGetValue(entityId, out var row) ? row : (null, $"#{entityId}", 0);
            return new AiQuotaStatus(ScopeEntity, entityId.ToString(CultureInfo.InvariantCulture), entityId,
                e.ParentId, e.ParentId == null, e.Name, Cap(entityId), Round(EntitySpend(entityId)), Currency, e.TenantId);
        }

        public AiQuotaStatus UserStatus(string username, int? tenantId = null) =>
            new(ScopeUser, username, null, null, false, username,
                Config.Users.TryGetValue(username, out var c) ? c : 0,
                Round(ByUser.GetValueOrDefault(username)), Currency, tenantId);

        /// <summary>The first cap reached on the way up from this entity (nearest first), or null.</summary>
        public AiQuotaStatus? Blocking(int? entityId)
        {
            if (Config.Entities.Count == 0) return null;
            foreach (var id in Chain(entityId))
            {
                if (Cap(id) <= 0) continue;
                var s = EntityStatus(id);
                if (s.Reached) return s;
            }
            return null;
        }

        /// <summary>Every configured quota with its spend.</summary>
        public List<AiQuotaStatus> All()
        {
            var list = new List<AiQuotaStatus>();
            foreach (var (key, cap) in Config.Entities)
                if (cap > 0 && int.TryParse(key, out var id) && Entities.ContainsKey(id)) list.Add(EntityStatus(id));
            foreach (var (user, cap) in Config.Users)
                if (cap > 0) list.Add(UserStatus(user));
            return list;
        }
    }

    private static double Round(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    /// <summary>The cached spend snapshot for this database (rebuilt after <see cref="CacheFor"/> or a month change).</summary>
    public static Snapshot Current(AppDbContext db, ScopingService sc, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var key = CacheKey(db);
        if (Cache.TryGetValue(key, out var hit) && now - hit.BuiltAt < CacheFor && now >= hit.BuiltAt
            && hit.Month == AiBilling.MonthKey(now))
            return hit;
        var snap = Build(db, sc, now);
        Cache[key] = snap;
        return snap;
    }

    private static string CacheKey(AppDbContext db)
    {
        try { return db.Database.GetConnectionString() ?? "default"; }
        catch { return "default"; }
    }

    /// <param name="always">price the month even with no caps set (the quotas page shows spend
    /// before anyone sets a cap); enforcement leaves it off so an uncapped org queries nothing</param>
    public static Snapshot Build(AppDbContext db, ScopingService sc, DateTime nowUtc, bool always = false)
    {
        var month = AiBilling.MonthKey(nowUtc);
        var config = LoadConfig(sc);
        var terms = AiBilling.Load(sc).TermsFor(month);
        if (config.IsEmpty && !always)
            return new Snapshot { BuiltAt = nowUtc, Month = month, Currency = terms.Currency, Config = config };

        var entities = db.Entities.AsNoTracking()
            .Select(e => new { e.Id, e.ParentId, e.Name, e.TenantId }).ToList()
            .ToDictionary(e => e.Id, e => (e.ParentId, e.Name, e.TenantId));
        int? CompanyOf(int? entityId)
        {
            var cur = entityId;
            for (var i = 0; i < 32 && cur is int id && entities.TryGetValue(id, out var e); i++)
            {
                if (e.ParentId == null) return id;
                cur = e.ParentId;
            }
            return null;
        }
        var contactCompany = db.ContactEntities.AsNoTracking()
            .Select(l => new { l.ContactId, l.EntityId }).ToList()
            .GroupBy(l => l.ContactId)
            .Select(g => (g.Key, Companies: g.Select(l => CompanyOf(l.EntityId)).Distinct().ToList()))
            .Where(x => x.Companies.Count == 1 && x.Companies[0] != null)
            .ToDictionary(x => x.Key, x => x.Companies[0]!.Value);

        var from = AiBilling.MonthStart(nowUtc);
        var to = from.AddMonths(1);
        var prices = LlmPricing.PriceTable(sc);
        var rows = db.LlmUsage.AsNoTracking().Where(u => u.CreatedAt >= from && u.CreatedAt < to)
            .Select(u => new
            {
                u.Model, u.EntityId, u.ContactId, u.UserName,
                u.PromptTokens, u.CompletionTokens, u.AudioPromptTokens, u.AudioCompletionTokens, u.CostUsd,
            }).ToList();
        var byEntity = new Dictionary<int, double>();
        var byCompany = new Dictionary<int, double>();
        var byUser = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in rows)
        {
            var cost = LlmPricing.RowCost(prices, u.Model, u.PromptTokens, u.CompletionTokens,
                u.AudioPromptTokens, u.AudioCompletionTokens, u.CostUsd);
            var company = CompanyOf(u.EntityId)
                          ?? (u.ContactId is int cid && contactCompany.TryGetValue(cid, out var cc) ? cc : (int?)null);
            var billed = terms.BilledRaw(company, cost);
            if (u.EntityId is int eid) byEntity[eid] = byEntity.GetValueOrDefault(eid) + billed;
            if (company is int co) byCompany[co] = byCompany.GetValueOrDefault(co) + billed;
            if (!string.IsNullOrWhiteSpace(u.UserName))
                byUser[u.UserName] = byUser.GetValueOrDefault(u.UserName) + billed;
        }
        return new Snapshot
        {
            BuiltAt = nowUtc, Month = month, Currency = terms.Currency, Config = config,
            Entities = entities, ByEntity = byEntity, ByCompany = byCompany, ByUser = byUser,
        };
    }

    // ---- enforcement ----------------------------------------------------------------------

    /// <summary>The cap that switches the AI off for a conversation of this entity, or null (fails open).</summary>
    public static AiQuotaStatus? EntityBlock(AppDbContext db, ScopingService sc, int? entityId, DateTime? nowUtc = null)
    {
        if (entityId == null) return null;
        try { return Current(db, sc, nowUtc).Blocking(entityId); }
        catch { return null; }
    }

    /// <summary>The <c>ai_reason</c> of a conversation the quota switched off.</summary>
    public static string Reason(AiQuotaStatus s) =>
        $"Off: AI quota reached for {s.Name} ({Money(s.Currency, s.Spent)} of {Money(s.Currency, s.Cap)} this month)";

    /// <summary>The user's reached cap, or null (no cap / under it / unreadable).</summary>
    public static AiQuotaStatus? UserBlock(AppDbContext db, ScopingService sc, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        try
        {
            var snap = Current(db, sc);
            if (!snap.Config.Users.TryGetValue(username, out var cap) || cap <= 0) return null;
            var s = snap.UserStatus(username);
            return s.Reached ? s : null;
        }
        catch { return null; }
    }

    /// <summary>The refusal an interactive AI feature answers with.</summary>
    public static string UserRefusal(AiQuotaStatus s) =>
        $"Your AI quota for this month is used up ({Money(s.Currency, s.Spent)} of {Money(s.Currency, s.Cap)}). "
        + "Ask an admin to raise it.";

    /// <summary>
    /// The person a usage row should be charged to: the signed-in user of the
    /// current request (the ambient audit context), else "". Only the
    /// interactive features stamp it.
    /// </summary>
    public static string CurrentUser()
    {
        var ctx = Audit.AuditContext.Current;
        if (ctx == null || ctx.UserId == null || string.IsNullOrWhiteSpace(ctx.Actor)) return "";
        return ctx.Actor.Length > 150 ? ctx.Actor[..150] : ctx.Actor;
    }

    /// <summary>"RM 12.50" — the billing currency with a familiar symbol where there is one.</summary>
    public static string Money(string currency, double amount)
    {
        var code = (currency ?? "USD").ToUpperInvariant();
        var n = amount.ToString(amount != 0 && Math.Abs(amount) < 1 ? "0.00##" : "N2", CultureInfo.InvariantCulture);
        return code switch
        {
            "MYR" => $"RM {n}",
            "USD" => $"${n}",
            "SGD" => $"S${n}",
            "EUR" => $"€{n}",
            "GBP" => $"£{n}",
            _ => $"{code} {n}",
        };
    }

    // ---- warnings ---------------------------------------------------------------------------

    private sealed class Warned
    {
        [JsonPropertyName("month")] public string Month { get; set; } = "";
        [JsonPropertyName("sent")] public Dictionary<string, int> Sent { get; set; } = new();
    }

    /// <summary>
    /// Notify at 80 % and 100 % of each quota, once per scope per month: the
    /// tenant's admins, plus the person for a user cap. Call it where nothing
    /// else is pending on <paramref name="db"/> (the AI-ops worker, the quotas
    /// read, a quota handover). Returns how many warnings went out; never throws.
    /// </summary>
    public static int Sweep(AppDbContext db, ScopingService sc, DateTime? nowUtc = null)
    {
        try
        {
            var snap = Current(db, sc, nowUtc);
            if (snap.Config.IsEmpty) return 0;
            Warned warned;
            try { warned = JsonSerializer.Deserialize<Warned>(sc.SettingValue(WarnedKey), Json) ?? new Warned(); }
            catch { warned = new Warned(); }
            if (warned.Month != snap.Month) warned = new Warned { Month = snap.Month };
            warned.Sent ??= new();

            var sent = 0;
            foreach (var s in snap.All())
            {
                var level = s.Reached ? 100 : s.State == AiQuotaStatus.Warn ? 80 : 0;
                if (level == 0 || warned.Sent.GetValueOrDefault(s.WarnKey) >= level) continue;
                warned.Sent[s.WarnKey] = level;
                Notify(db, s, level);
                sent++;
            }
            if (sent > 0) sc.SetSetting(WarnedKey, JsonSerializer.Serialize(warned));
            return sent;
        }
        catch { return 0; }
    }

    private static void Notify(AppDbContext db, AiQuotaStatus s, int level)
    {
        var used = $"{Money(s.Currency, s.Spent)} of {Money(s.Currency, s.Cap)} used this month.";
        string title, body;
        int? tenant = s.TenantId;
        int? userId = null;
        if (s.Scope == ScopeUser)
        {
            var u = db.Users.AsNoTracking().Where(x => x.Username == s.Key)
                .Select(x => new { x.Id, Tenant = x.Profile != null ? x.Profile.TenantId : null }).FirstOrDefault();
            userId = u?.Id;
            tenant = u?.Tenant;
            title = level >= 100 ? $"AI quota used up — {s.Name}" : $"AI quota at 80% — {s.Name}";
            body = used + (level >= 100
                ? " The assistant, playground, skill drafting and summaries are paused for this person until next month or until the quota is raised."
                : " At 100% their AI features pause until next month.");
        }
        else
        {
            title = level >= 100 ? $"AI paused — quota reached for {s.Name}" : $"AI quota at 80% — {s.Name}";
            body = used + (level >= 100
                ? " The AI stops answering its chats and hands them to a person until next month or until the quota is raised."
                : " At 100% the AI stops answering and chats go to a person.");
        }
        var opts = new NotifyOptions { RefKey = $"ai-quota:{s.WarnKey}:{level}" };
        NotificationService.Notify(db, NotificationCategories.Admin, "system", title, body, "/billing",
            NotificationAudience.Admins(tenant), opts);
        if (userId is int uid)
            NotificationService.Notify(db, NotificationCategories.Admin, "system", title, body, "/billing",
                NotificationAudience.ForUsers(new[] { uid }), opts);
    }
}
