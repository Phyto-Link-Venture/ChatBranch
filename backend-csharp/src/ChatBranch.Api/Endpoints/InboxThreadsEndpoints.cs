using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// <c>GET /api/inbox/threads</c> — the ONE conversation list behind /chats,
/// filtered on the server across every channel.
///
/// It replaces the page's old pair of calls (<c>/api/whatsapp/threads</c> +
/// <c>/api/messaging/threads</c>) whose rows were merged and filtered in the
/// browser: the whatsapp list carried every channel, so non-WhatsApp chats
/// listed twice, and WhatsApp threads lost their status and team. Here every row
/// carries its own channel and the full management state.
///
/// Query (all optional; list params take repeated keys or comma lists):
///   q                text over contact name / phone / email, thread name /
///                    address, AND message text
///   status           open,pending,snoozed,resolved
///   channel          whatsapp,email,telegram,facebook,instagram,website
///   account          sending account ids; "none" = no account bound
///   assignee         me | unassigned | &lt;username&gt; (any of)
///   team             team ids; "none" = no owning team
///   entity           entity slugs (each with its subtree); default = the
///                    caller's whole scope
///   labels + labels_mode=any|all   conversation labels
///   tags             contact tags (any of)
///   ai=on|off · window=open|closed · opted_out=1|0 · waiting=1 ·
///   call=any|live · linked=1|0
///   handover=open|any|none (open = handed to a person and not yet answered;
///   any = ever handed over; none = never) · handover_kind (any of)
///   active_from / active_to   last-activity range (ISO; to is exclusive)
///   created_from / created_to conversation-created range
///   sort             newest (default, last activity) | customer_newest (the
///                    customer's last inbound message on the conversation —
///                    chats they never wrote in last) | oldest_waiting | unread
///   limit (50, max 200) · cursor (opaque offset from next_cursor — pages every
///   sort) · before (ISO; sort=newest only, ignored otherwise: strictly older
///   activity) · count_only=1 · thread=channel:key (every conversation with that
///   customer the caller sees — one per company) · thread_id (exactly one)
///
/// Entity scoping is the inbox rule: ScopeOwned semantics INCLUDING the
/// unassigned bucket, and agents/custom roles bounded by their assigned
/// entities — an out-of-scope slug narrows to their own scope, never widens.
/// </summary>
public static class InboxThreadsEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private static readonly string[] LiveCallStates = { "connecting", "ringing", "in_call" };

    public static void MapInboxThreadsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/inbox/threads", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            var f = InboxQuery.Parse(ctx.Request.Query, me.Identity?.Name ?? "");
            var scope = await ScopeEntityIdsAsync(db, sc, me, role, f.Entities);

            var candidates = await Candidates(db, f, scope);
            var waCtx = await WaContext.LoadAsync(db, sc, scope.Picked);
            var filtered = ApplyMemoryFilters(candidates, f, waCtx);
            var sorted = Sort(filtered, f.Sort);

            if (f.CountOnly) return Results.Ok(new { count = sorted.Count });

            // `before` is a keyset cursor over last activity, so it only means something
            // in that order; every sort pages with the offset `cursor`
            if (f.Before is DateTime before && f.Sort == "newest")
                sorted = sorted.Where(c => c.LastActivity < before).ToList();
            var offset = Math.Max(0, f.Offset);
            var page = sorted.Skip(offset).Take(f.Limit).ToList();
            var next = offset + page.Count < sorted.Count ? (offset + page.Count).ToString() : null;

            return Results.Ok(new
            {
                threads = await Hydrate(db, sc, page, waCtx),
                count = sorted.Count,
                next_cursor = next,
                // the caller sees more than one company: the list shows whose each chat is
                multi_company = ShellScope.SeesSeveralCompanies(db, sc, me),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.view");
    }

    /// <summary>
    /// The ids of every conversation the caller would see in the list for this
    /// query (same parsing, scope and filters as <c>GET /api/inbox/threads</c>) —
    /// the bulk endpoint's "everything in the current filter".
    /// </summary>
    internal static async Task<List<int>> MatchingThreadIdsAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, IQueryCollection qs)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        var f = InboxQuery.Parse(qs, me.Identity?.Name ?? "");
        var scope = await ScopeEntityIdsAsync(db, sc, me, role, f.Entities);
        var candidates = await Candidates(db, f, scope);
        var waCtx = await WaContext.LoadAsync(db, sc, scope.Picked);
        return Sort(ApplyMemoryFilters(candidates, f, waCtx), f.Sort).Select(c => c.Id).ToList();
    }

    /// <summary>The caller's visible-entity set for the inbox (null = no narrowing).</summary>
    internal static async Task<List<int>?> VisibleEntityIdsAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me) =>
        (await ScopeEntityIdsAsync(db, sc, me, me.FindFirstValue(ClaimTypes.Role) ?? "", new List<string>())).Ids;

    // =========================================================================
    // query parsing
    // =========================================================================

    internal sealed class InboxQuery
    {
        public string Q = "";
        public List<string> Statuses = new();
        public List<string> Channels = new();
        public List<int> AccountIds = new();
        public bool AccountNone;
        public List<string> Assignees = new();
        public bool AssigneeNone;
        public List<int> TeamIds = new();
        public bool TeamNone;
        public List<string> Entities = new();
        public List<string> Labels = new();
        public bool LabelsAll;
        public List<string> Tags = new();
        public bool? Ai;
        public bool? WindowOpen;
        public bool? OptedOut;
        public bool Waiting;
        public string Call = "";
        public bool? Linked;
        public string Handover = "";
        public List<string> HandoverKinds = new();
        public DateTime? ActiveFrom, ActiveTo, CreatedFrom, CreatedTo;
        public string Sort = "newest";
        public int Limit = DefaultLimit;
        public int Offset;
        public DateTime? Before;
        public bool CountOnly;
        public (string Channel, string Key)? Thread;
        public int? ThreadId;

        public static InboxQuery Parse(IQueryCollection qs, string me)
        {
            var f = new InboxQuery { Q = One(qs, "q").Trim() };
            f.Statuses = Multi(qs, "status").Select(s => s.ToLowerInvariant()).ToList();
            f.Channels = Multi(qs, "channel").Select(s => s.ToLowerInvariant()).ToList();
            foreach (var a in Multi(qs, "account"))
            {
                if (a is "none" or "unassigned") f.AccountNone = true;
                else if (int.TryParse(a, out var id)) f.AccountIds.Add(id);
            }
            foreach (var a in Multi(qs, "assignee"))
            {
                if (a is "unassigned" or "none") f.AssigneeNone = true;
                else if (a == "me") { if (me.Length > 0) f.Assignees.Add(me); else f.AssigneeNone = true; }
                else if (a is not ("any" or "all")) f.Assignees.Add(a);
            }
            foreach (var t in Multi(qs, "team"))
            {
                if (t is "none" or "unassigned") f.TeamNone = true;
                else if (int.TryParse(t, out var id)) f.TeamIds.Add(id);
            }
            f.Entities = Multi(qs, "entity").Where(s => s is not ("all" or "__all__")).ToList();
            f.Labels = Multi(qs, "labels").Concat(Multi(qs, "label"))
                .Select(s => s.ToLowerInvariant()).Distinct().ToList();
            f.LabelsAll = One(qs, "labels_mode").Equals("all", StringComparison.OrdinalIgnoreCase);
            f.Tags = Multi(qs, "tags").Concat(Multi(qs, "tag")).Distinct().ToList();
            f.Ai = OnOff(One(qs, "ai"));
            f.WindowOpen = One(qs, "window").ToLowerInvariant() switch
            {
                "open" => true, "closed" => false, _ => null,
            };
            f.OptedOut = Flag(One(qs, "opted_out"));
            f.Waiting = Flag(One(qs, "waiting")) == true;
            f.Call = One(qs, "call").ToLowerInvariant() is "any" or "live" or "none"
                ? One(qs, "call").ToLowerInvariant() : "";
            f.Linked = One(qs, "linked").ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "linked" => true,
                "0" or "false" or "no" or "unlinked" => false,
                _ => null,
            };
            f.Handover = One(qs, "handover").Trim().ToLowerInvariant() is "open" or "any" or "none"
                ? One(qs, "handover").Trim().ToLowerInvariant() : "";
            f.HandoverKinds = Multi(qs, "handover_kind").Select(s => s.ToLowerInvariant()).ToList();
            f.ActiveFrom = Date(One(qs, "active_from"));
            f.ActiveTo = Date(One(qs, "active_to"));
            f.CreatedFrom = Date(One(qs, "created_from"));
            f.CreatedTo = Date(One(qs, "created_to"));
            var sort = One(qs, "sort").Trim().ToLowerInvariant();
            f.Sort = sort is "customer_newest" or "oldest_waiting" or "unread" ? sort : "newest";
            f.Limit = int.TryParse(One(qs, "limit"), out var lim) && lim > 0 ? Math.Min(lim, MaxLimit) : DefaultLimit;
            f.Offset = int.TryParse(One(qs, "cursor"), out var off) && off > 0 ? off : 0;
            f.Before = Date(One(qs, "before"));
            f.CountOnly = Flag(One(qs, "count_only")) == true;
            var thread = One(qs, "thread");
            var colon = thread.IndexOf(':');
            if (colon > 0 && colon < thread.Length - 1)
                f.Thread = (thread[..colon].Trim().ToLowerInvariant(), thread[(colon + 1)..].Trim());
            if (int.TryParse(One(qs, "thread_id"), out var tid) && tid > 0) f.ThreadId = tid;
            return f;
        }

        private static string One(IQueryCollection qs, string key) =>
            qs.TryGetValue(key, out var v) ? (v.ToString() ?? "") : "";

        /// <summary>Repeated keys and comma lists both work: ?a=x&amp;a=y,z.</summary>
        private static List<string> Multi(IQueryCollection qs, string key)
        {
            if (!qs.TryGetValue(key, out var values)) return new List<string>();
            return values
                .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        }

        private static bool? Flag(string raw) => raw.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null,
        };

        private static bool? OnOff(string raw) => Flag(raw);

        /// <summary>ISO date or date-time; naive values are taken as UTC.</summary>
        private static DateTime? Date(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
                ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
        }
    }

    // =========================================================================
    // entity scope
    // =========================================================================

    /// <summary>
    /// The entity ids a request may see (each picked entity's subtree), or null
    /// for "no narrowing". <c>Picked</c> is the single entity the caller asked
    /// for (entity-scoped settings such as the reminder chatbot default resolve
    /// against it), null when several or none were picked.
    /// </summary>
    internal sealed record EntityScope(List<int>? Ids, int? Picked);

    internal static async Task<EntityScope> ScopeEntityIdsAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, string role, List<string> slugs)
    {
        var picked = slugs.Count == 0
            ? new List<int>()
            : await db.Entities.AsNoTracking().Where(e => slugs.Contains(e.Slug))
                .Select(e => e.Id).ToListAsync();

        // Agents (and custom roles) see only the entities assigned to them and
        // everything beneath those. Asking for an outlet outside that scope
        // narrows to their own rather than widening the inbox; an agent with NO
        // assignment keeps the tenant-wide view (unchanged behaviour).
        // superadmin + org-wide admins: unbound; company admins, agents: their entities
        {
            var assigned = ShellScope.ScopeRoots(db, me);
            if (assigned.Count > 0)
            {
                var allowed = sc.EntitySubtreeIds(assigned).ToHashSet();
                picked = picked.Where(allowed.Contains).ToList();
                if (picked.Count == 0) picked = assigned;
            }
        }
        if (picked.Count == 0) return new EntityScope(null, null);
        return new EntityScope(sc.EntitySubtreeIds(picked), picked.Count == 1 ? picked[0] : null);
    }

    // =========================================================================
    // candidate rows: everything SQL can decide, projected lean
    // =========================================================================

    internal sealed class Candidate
    {
        public int Id;
        public string Channel = "";
        public string Key = "";
        public int? AccountId;
        public int? EntityId;
        public bool AiEnabled;
        public Dictionary<string, object?>? AiOverrides;
        public List<string>? Labels;
        public List<string>? ContactTags;
        public DateTime UpdatedAt;
        public DateTime CreatedAt;
        public DateTime? LastTs;
        public DateTime? LastInTs;
        public string? LastPublicDir;
        public bool OptedOut;
        public string? ContactAiMode;
        public DateTime? HandoverAt;
        public DateTime? HandoverResolvedAt;

        public DateTime LastActivity => DateTime.SpecifyKind(LastTs ?? UpdatedAt, DateTimeKind.Utc);
        /// <summary>The newest customer-facing message came FROM the customer.</summary>
        public bool Waiting => LastPublicDir == MessageDirection.In;
    }

    private static async Task<List<Candidate>> Candidates(AppDbContext db, InboxQuery f, EntityScope scope)
    {
        IQueryable<Thread> q = db.Threads.AsNoTracking();

        if (f.ThreadId is int onlyId)
            q = q.Where(t => t.Id == onlyId);
        else if (f.Thread is (string tch, string tkey))
            q = q.Where(t => t.Channel == tch && t.Key == tkey);
        else
            // A conversation nothing was ever said in (a chat opened from a contact
            // and left, a number registered by hand) is not a conversation yet: keep
            // it out of the list. Asked for by name (the open chat's scope check)
            // it still counts, so a freshly started chat isn't closed under you.
            q = q.Where(t => db.Messages.Any(m => m.ThreadId == t.Id));

        if (scope.Ids is List<int> ids)
            q = q.Where(t => t.EntityId == null || ids.Contains(t.EntityId.Value));

        if (f.Channels.Count > 0) q = q.Where(t => f.Channels.Contains(t.Channel));
        if (f.Statuses.Count > 0) q = q.Where(t => f.Statuses.Contains(t.Status));

        if (f.AccountIds.Count > 0 || f.AccountNone)
        {
            var accIds = f.AccountIds;
            var none = f.AccountNone;
            q = q.Where(t => (t.AccountId != null && accIds.Contains(t.AccountId.Value))
                             || (none && t.AccountId == null));
        }
        if (f.Assignees.Count > 0 || f.AssigneeNone)
        {
            var names = f.Assignees;
            var none = f.AssigneeNone;
            q = q.Where(t => names.Contains(t.Assignee) || (none && (t.Assignee == null || t.Assignee == "")));
        }
        if (f.TeamIds.Count > 0 || f.TeamNone)
        {
            var teamIds = f.TeamIds;
            var none = f.TeamNone;
            q = q.Where(t => (t.TeamId != null && teamIds.Contains(t.TeamId.Value))
                             || (none && t.TeamId == null));
        }
        if (f.OptedOut is bool oo) q = q.Where(t => t.OptedOut == oo);
        if (f.Linked is bool linked)
            q = linked ? q.Where(t => t.ContactId != null) : q.Where(t => t.ContactId == null);
        // human handover (Ai/Handover): open = handed to a person, not yet answered
        if (f.Handover == "open") q = q.Where(t => t.HandoverAt != null && t.HandoverResolvedAt == null);
        else if (f.Handover == "any") q = q.Where(t => t.HandoverAt != null);
        else if (f.Handover == "none") q = q.Where(t => t.HandoverAt == null);
        if (f.HandoverKinds.Count > 0)
        {
            var kinds = f.HandoverKinds;
            q = q.Where(t => t.HandoverAt != null && kinds.Contains(t.HandoverKind));
        }
        if (f.CreatedFrom is DateTime cf) q = q.Where(t => t.CreatedAt >= cf);
        if (f.CreatedTo is DateTime ct) q = q.Where(t => t.CreatedAt < ct);

        if (f.Call == "live")
            q = q.Where(t => LiveCallStates.Contains(t.CallStatus));
        else if (f.Call is "any" or "none")
        {
            // A WhatsApp-transport call leaves its mark on the thread; a SIP
            // call is a CallRecord on the linked contact.
            var want = f.Call == "any";
            q = q.Where(t => ((t.CallStatus != "idle" && t.CallStatus != "") || t.CallId != null
                              || (t.ContactId != null && db.CallRecords.Any(c => c.ContactId == t.ContactId)))
                             == want);
        }

        if (f.Waiting)
            q = q.Where(t => t.Messages
                .Where(m => !m.Private && m.Source != MessageSource.System)
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Select(m => m.Direction).FirstOrDefault() == MessageDirection.In);

        if (f.Q.Length > 0)
        {
            var like = $"%{EscapeLike(f.Q)}%";
            var digits = Regex.Replace(f.Q, @"\D", "");
            var dlike = digits.Length >= 3 ? $"%{digits}%" : "";
            var useDigits = dlike.Length > 0;
            q = q.Where(t =>
                EF.Functions.Like(t.Name, like, "\\")
                || EF.Functions.Like(t.Address, like, "\\")
                || EF.Functions.Like(t.Key, like, "\\")
                || (useDigits && (EF.Functions.Like(t.Key, dlike) || EF.Functions.Like(t.Address, dlike)))
                || (t.Contact != null && (EF.Functions.Like(t.Contact.Name, like, "\\")
                                          || EF.Functions.Like(t.Contact.Phone, like, "\\")
                                          || EF.Functions.Like(t.Contact.Email, like, "\\")))
                || t.Messages.Any(m => EF.Functions.Like(m.Text, like, "\\")));
        }

        var rows = await q.Select(t => new
        {
            t.Id,
            t.Channel,
            t.Key,
            t.AccountId,
            t.EntityId,
            t.AiEnabled,
            t.AiOverrides,
            t.Labels,
            ContactTags = t.Contact != null ? t.Contact.Tags : null,
            ContactAiMode = t.Contact != null ? t.Contact.AiMode : null,
            t.OptedOut,
            t.HandoverAt,
            t.HandoverResolvedAt,
            t.UpdatedAt,
            t.CreatedAt,
            LastTs = t.Messages.Max(m => (DateTime?)m.CreatedAt),
            LastInTs = t.Messages.Where(m => m.Direction == MessageDirection.In)
                .Max(m => (DateTime?)m.CreatedAt),
            LastPublicDir = t.Messages
                .Where(m => !m.Private && m.Source != MessageSource.System)
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Select(m => m.Direction).FirstOrDefault(),
        }).ToListAsync();

        return rows.Select(r => new Candidate
        {
            Id = r.Id,
            Channel = r.Channel,
            Key = r.Key,
            AccountId = r.AccountId,
            EntityId = r.EntityId,
            AiEnabled = r.AiEnabled,
            AiOverrides = r.AiOverrides,
            Labels = r.Labels,
            ContactTags = r.ContactTags,
            UpdatedAt = r.UpdatedAt,
            CreatedAt = r.CreatedAt,
            LastTs = r.LastTs,
            LastInTs = r.LastInTs,
            LastPublicDir = r.LastPublicDir,
            OptedOut = r.OptedOut,
            ContactAiMode = r.ContactAiMode,
            HandoverAt = r.HandoverAt,
            HandoverResolvedAt = r.HandoverResolvedAt,
        }).ToList();
    }

    /// <summary>LIKE-escape with backslash so a literal % or _ in the search stays literal.</summary>
    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // =========================================================================
    // filters SQL can't express (JSON columns + derived state)
    // =========================================================================

    private static List<Candidate> ApplyMemoryFilters(List<Candidate> rows, InboxQuery f, WaContext wa)
    {
        IEnumerable<Candidate> outv = rows;
        if (f.Labels.Count > 0)
        {
            outv = outv.Where(c =>
            {
                var have = (c.Labels ?? new List<string>()).Select(l => l.ToLowerInvariant()).ToHashSet();
                return f.LabelsAll ? f.Labels.All(have.Contains) : f.Labels.Any(have.Contains);
            });
        }
        if (f.Tags.Count > 0)
        {
            var wanted = f.Tags.Select(t => t.ToLowerInvariant()).ToHashSet();
            outv = outv.Where(c => (c.ContactTags ?? new List<string>())
                .Any(t => wanted.Contains(t.ToLowerInvariant())));
        }
        if (f.Ai is bool ai) outv = outv.Where(c => wa.Ai(c) == ai);
        if (f.WindowOpen is bool open) outv = outv.Where(c => wa.Window(c).Open == open);
        if (f.ActiveFrom is DateTime af) outv = outv.Where(c => c.LastActivity >= af);
        if (f.ActiveTo is DateTime at) outv = outv.Where(c => c.LastActivity < at);
        return outv.ToList();
    }

    /// <summary>
    /// newest = most recent activity first. customer_newest = the customer's
    /// newest inbound message first (on any channel — each row is one
    /// conversation; the page folds one contact's channels into one entry);
    /// conversations nobody ever wrote in go last; ties by activity, then id.
    /// oldest_waiting = conversations waiting on our reply first, the longest
    /// wait at the top. unread = the same waiting set first but newest first —
    /// the inbox keeps no per-operator read receipts, so an unanswered customer
    /// message IS the unread signal. Runs in memory over the candidate rows (no
    /// provider-specific NULL ordering), and the order is total (id last), so
    /// the offset cursor pages every sort without gaps or repeats.
    /// </summary>
    private static List<Candidate> Sort(List<Candidate> rows, string sort) => sort switch
    {
        "customer_newest" => rows
            .OrderBy(c => c.LastInTs == null ? 1 : 0)
            .ThenByDescending(c => c.LastInTs)
            .ThenByDescending(c => c.LastActivity).ThenByDescending(c => c.Id).ToList(),
        "oldest_waiting" => rows
            .OrderBy(c => c.Waiting ? 0 : 1)
            .ThenBy(c => c.Waiting ? (c.LastInTs ?? c.LastActivity) : DateTime.MaxValue)
            .ThenByDescending(c => c.LastActivity).ThenByDescending(c => c.Id).ToList(),
        "unread" => rows
            .OrderBy(c => c.Waiting ? 0 : 1)
            .ThenByDescending(c => c.LastActivity).ThenByDescending(c => c.Id).ToList(),
        _ => rows.OrderByDescending(c => c.LastActivity).ThenByDescending(c => c.Id).ToList(),
    };

    // =========================================================================
    // WhatsApp window + chatbot tick (same rules as the thread detail)
    // =========================================================================

    internal sealed class WaContext
    {
        private Dictionary<int, ChannelAccount> _byId = new();
        /// <summary>Every inbox by id, every channel — the AI verdict's own-inbox lookup.</summary>
        private Dictionary<int, ChannelAccount> _everyById = new();
        /// <summary>Messenger / Instagram pages, for their messaging window.</summary>
        private Dictionary<int, ChannelAccount> _social = new();
        /// <summary>Live WeChat / Zalo / TikTok / Douyin accounts, for their reply windows.</summary>
        private Dictionary<int, ChannelAccount> _windowed = new();
        private ChannelAccount? _fallback;
        private bool _envIsSoleFallback;
        private ScopingService? _sc;
        private readonly Dictionary<int, bool> _reminderDefaults = new();
        /// <summary>The AI controls (master switch, schedule, never-tags), cached per entity.</summary>
        private ChatBranch.Data.Ai.AiControls? _ai;
        private readonly Dictionary<int, ChatBranch.Data.Ai.AiVerdict> _verdicts = new();

        /// <summary>reminder_ai_default for the THREAD's own entity — exactly what the AI gate reads.</summary>
        private bool ReminderDefaultOn(int? entityId)
        {
            var k = entityId ?? 0;
            if (!_reminderDefaults.TryGetValue(k, out var on))
                _reminderDefaults[k] = on = _sc != null
                    && ChatBranch.Data.Ai.AiEligibility.ReminderDefaultOn(_sc, entityId);
            return on;
        }

        public static async Task<WaContext> LoadAsync(AppDbContext db, ScopingService sc, int? entityId)
        {
            // every inbox in ONE query: the WhatsApp numbers and the social pages
            // as before, plus the rest — the AI verdict needs the conversation's
            // OWN inbox (its master switch and never-AI tags) whatever the channel
            var every = await db.ChannelAccounts.AsNoTracking().ToListAsync();
            var wa = every.Where(a => a.Channel == ChatBranch.Domain.Channels.WhatsApp).ToList();
            var configured = wa.Where(WhatsAppSettingsEndpoints.IsConfigured).ToList();
            var env = WhatsAppSettingsEndpoints.EnvAccountConfigured();
            var social = every.Where(a => a.Channel == ChatBranch.Domain.Channels.Facebook
                                          || a.Channel == ChatBranch.Domain.Channels.Instagram).ToList();
            return new WaContext
            {
                _byId = wa.ToDictionary(a => a.Id),
                _everyById = every.ToDictionary(a => a.Id),
                _social = social.ToDictionary(a => a.Id),
                _windowed = ChatBranch.Data.Channels.ChannelWindow.LiveAccounts(db),
                // an unknown/missing account falls back to the SOLE configured
                // one — never a guess between several (wa_config.get_account)
                _fallback = configured.Count == 1 && !env ? configured[0] : null,
                _envIsSoleFallback = configured.Count == 0 && env,
                _sc = sc,
                _ai = new ChatBranch.Data.Ai.AiControls(db, sc),
            };
        }

        public ChannelAccount? Account(int? accountId) =>
            accountId is int id && _byId.TryGetValue(id, out var own) ? own : _fallback;

        /// <summary>
        /// The conversation's OWN inbox on any channel (a widget, a bot, a
        /// mailbox…), for the AI verdict: its master switch and never-AI tags
        /// live there (<see cref="ChatBranch.Data.Ai.AiControls.InboxMaster"/>),
        /// and <see cref="Account"/> only knows WhatsApp numbers. Falls back to
        /// <see cref="Account"/> so the WhatsApp rules are unchanged.
        /// </summary>
        public ChannelAccount? AiAccount(int? accountId) =>
            accountId is int id && _everyById.TryGetValue(id, out var own) ? own : Account(accountId);

        /// <summary>
        /// Meta's 24h customer-service window. Only a LIVE Cloud number or a live
        /// Messenger / Instagram page has one (<see cref="ChatBranch.Data.Channels.SocialWindow"/>):
        /// Baileys rides WhatsApp Web, a sandbox thread never dead-ends, and
        /// other channels have no window at all (always "open").
        /// </summary>
        public (bool Open, string? ExpiresAt) Window(Candidate c)
        {
            if (ChatBranch.Data.Channels.SocialWindow.Applies(c.Channel))
                return ChatBranch.Data.Channels.SocialWindow.ListWindow(
                    ChatBranch.Data.Channels.SocialWindow.Live(
                        c.AccountId is int sid && _social.TryGetValue(sid, out var page) ? page : null, c.Channel),
                    c.LastInTs, DateTime.UtcNow);
            if (ChatBranch.Data.Channels.ChannelWindow.Applies(c.Channel))
                return ChatBranch.Data.Channels.ChannelWindow.ListWindow(c.Channel,
                    c.AccountId is int wid && _windowed.TryGetValue(wid, out var wacct) && wacct.Channel == c.Channel,
                    c.LastInTs, DateTime.UtcNow);
            if (c.Channel != ChatBranch.Domain.Channels.WhatsApp) return (true, null);
            var account = Account(c.AccountId);
            var live = account != null ? WhatsAppSettingsEndpoints.IsConfigured(account) : _envIsSoleFallback;
            // Baileys / WAHA: WhatsApp Web, so Meta's 24h window never applies
            if (!live || (account != null && ChatBranch.Data.Channels.WhatsAppProviders.IsUnofficial(account)))
                return (true, null);
            if (c.LastInTs is not DateTime lastIn) return (false, null);
            var expires = DateTime.SpecifyKind(lastIn, DateTimeKind.Utc).AddHours(24);
            return (DateTime.UtcNow < expires, expires.ToString("o"));
        }

        /// <summary>
        /// The effective chatbot tick: per sending account on WhatsApp (its
        /// role decides when there is no explicit tick), the thread flag elsewhere.
        /// </summary>
        public bool Ai(Candidate c) => Verdict(c).Enabled;

        /// <summary>
        /// The full AI rule with its reason (Ai/AiEligibility.Decide): opted out →
        /// master switch / schedule → contact never / never-tag → the tick →
        /// contact always → inbox default → role — what the gates apply.
        /// </summary>
        public ChatBranch.Data.Ai.AiVerdict Verdict(Candidate c)
        {
            if (_verdicts.TryGetValue(c.Id, out var cached)) return cached;
            var probe = new Thread
            {
                Id = c.Id,
                Channel = c.Channel,
                Key = c.Key,
                AccountId = c.AccountId,
                EntityId = c.EntityId,
                AiEnabled = c.AiEnabled,
                AiOverrides = c.AiOverrides ?? new Dictionary<string, object?>(),
                OptedOut = c.OptedOut,
                HandoverAt = c.HandoverAt,
                HandoverResolvedAt = c.HandoverResolvedAt,
            };
            ChatBranch.Data.Ai.AiVerdict v;
            if (_ai == null)
                v = new ChatBranch.Data.Ai.AiVerdict(
                    WhatsAppSettingsEndpoints.EffectiveAi(probe, Account(c.AccountId), ReminderDefaultOn(c.EntityId)),
                    ChatBranch.Data.Ai.AiRules.ChatTick, "", false);
            else
                v = _ai.Decide(probe, AiAccount(c.AccountId), c.ContactAiMode, c.ContactTags);
            _verdicts[c.Id] = v;
            return v;
        }

        public bool IsBaileys(int? accountId) => Account(accountId)?.Provider == Providers.Baileys;

        /// <summary>The WhatsApp provider of the conversation's number (cloud / baileys / ycloud / twilio).</summary>
        public string Provider(int? accountId) =>
            Account(accountId) is { } a ? ChatBranch.Data.Channels.WhatsAppProviders.Of(a) : Providers.Cloud;
    }

    // =========================================================================
    // the row the list renders
    // =========================================================================

    private static async Task<List<Dictionary<string, object?>>> Hydrate(AppDbContext db, ScopingService sc,
        List<Candidate> page, WaContext wa)
    {
        var ids = page.Select(c => c.Id).ToList();
        if (ids.Count == 0) return new List<Dictionary<string, object?>>();

        var threads = await db.Threads.AsNoTracking()
            .Include(t => t.Contact).Include(t => t.Account).Include(t => t.Team).Include(t => t.Entity)
            .Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var stats = await db.Threads.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                Count = t.Messages.Count(),
                Last = t.Messages.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                    .Select(m => new { m.Text, m.Direction, m.Status, m.Source, m.Private, m.MediaType, m.MediaPath, m.RecalledAt })
                    .FirstOrDefault(),
                // who wrote the newest inbound message — named only in a group (sender_name)
                LastSender = t.Messages.Where(m => m.Direction == MessageDirection.In)
                    .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                    .Select(m => m.SenderName).FirstOrDefault(),
            }).ToDictionaryAsync(x => x.Id);
        var contactIds = threads.Values.Where(t => t.ContactId != null).Select(t => t.ContactId!.Value)
            .Distinct().ToList();
        var sipContacts = contactIds.Count == 0
            ? new HashSet<int>()
            : (await db.CallRecords.AsNoTracking()
                .Where(c => c.ContactId != null && contactIds.Contains(c.ContactId.Value))
                .Select(c => c.ContactId!.Value).Distinct().ToListAsync()).ToHashSet();

        var companyMap = sc.CompanyMap().ToDictionary(kv => kv.Key, kv => (int?)kv.Value);
        var companyIds = threads.Values.Select(t => t.CompanyEntityId
                ?? (t.EntityId is int e && companyMap.TryGetValue(e, out var c0) ? c0 : null))
            .OfType<int>().Distinct().ToList();
        var companyNames = await db.Entities.AsNoTracking().Where(e => companyIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name);
        // social comments / reviews / DMs (Data/Social): their `social` object
        var socialMap = SocialEndpoints.SocialMap(db,
            threads.Values.Where(x => x.Channel == ChatBranch.Domain.Channels.Social).Select(x => x.Id));
        var outv = new List<Dictionary<string, object?>>();
        foreach (var c in page)
        {
            if (!threads.TryGetValue(c.Id, out var t)) continue;
            var stat = stats.GetValueOrDefault(t.Id);
            var (open, expires) = wa.Window(c);
            var account = t.Account ?? (t.Channel == ChatBranch.Domain.Channels.WhatsApp ? wa.Account(t.AccountId) : null);
            var last = stat?.Last;
            var preview = last == null ? ""
                : last.Private ? $"[note] {last.Text}"
                // recalled ("delete for everyone") and erased by the inbox: nothing left to preview
                : last.RecalledAt != null && last.Text.Length == 0 && last.MediaPath.Length == 0 ? "[deleted]"
                : last.Text.Length == 0 && last.MediaType.Length > 0 ? $"[{last.MediaType}]"
                : last.Text;
            var isWa = t.Channel == ChatBranch.Domain.Channels.WhatsApp;
            var address = t.Address.Length > 0 ? t.Address : t.Key;
            // a WhatsApp id is bare digits — show it as the phone number it is
            if (isWa && address.Length > 0 && address.All(char.IsDigit)) address = $"+{address}";
            var callStatus = string.IsNullOrEmpty(t.CallStatus) ? "idle" : t.CallStatus;

            var company = t.CompanyEntityId ?? (t.EntityId is int te && companyMap.TryGetValue(te, out var tc) ? tc : null);
            outv.Add(new Dictionary<string, object?>
            {
                ["channel"] = t.Channel,
                ["key"] = t.Key,
                // the conversation's identity — a customer has one per company, so channel + key
                // is not unique: thread_id is, and company says whose it is
                ["thread_id"] = t.Id,
                ["company"] = company is int cid0
                    ? new Dictionary<string, object?> { ["id"] = cid0, ["name"] = companyNames.GetValueOrDefault(cid0, "") }
                    : null,
                ["name"] = t.Name.Length > 0 ? t.Name : address,
                ["address"] = address,
                // a group conversation (Channels/GroupChats.cs) + who spoke last in it
                ["is_group"] = ChatBranch.Data.Channels.GroupChats.IsGroup(t),
                ["last_sender"] = stat?.LastSender is { Length: > 0 } lastSender ? lastSender : null,
                ["status"] = string.IsNullOrEmpty(t.Status) ? ThreadStatus.Open : t.Status,
                ["ai_enabled"] = wa.Ai(c),
                ["ai_reason"] = wa.Verdict(c).Reason,
                ["ai_rule"] = wa.Verdict(c).Rule,
                ["ai_forced"] = wa.Verdict(c).Forced,
                ["handover"] = AiControlsEndpoints.HandoverPayload(t),
                ["assignee"] = t.Assignee ?? "",
                ["team_id"] = t.TeamId,
                ["team_name"] = t.Team?.Name ?? "",
                ["labels"] = t.Labels ?? new List<string>(),
                ["contact_id"] = t.ContactId,
                ["contact_name"] = t.Contact?.Name ?? "",
                ["contact_tags"] = t.Contact?.Tags ?? new List<string>(),
                ["entity"] = t.Entity?.Slug,
                ["account_id"] = t.AccountId?.ToString(),
                ["account_name"] = account?.Name ?? "",
                ["provider"] = isWa ? wa.Provider(t.AccountId) : null,
                ["window_open"] = open,
                ["window_expires_at"] = expires,
                ["opted_out"] = t.OptedOut,
                ["waiting"] = c.Waiting,
                ["call"] = new Dictionary<string, object?>
                {
                    ["permission"] = t.CallPermission,
                    ["call_id"] = t.CallId,
                    ["status"] = callStatus,
                    ["room"] = t.CallRoom,
                },
                ["has_call"] = callStatus != "idle" || t.CallId != null
                               || (t.ContactId is int cid && sipContacts.Contains(cid)),
                ["last_message"] = preview,
                ["last_dir"] = last?.Direction ?? "",
                ["last_status"] = last?.Status ?? "",
                ["last_source"] = last?.Source ?? "",
                ["last_private"] = last?.Private ?? false,
                ["last_ts"] = c.LastActivity.ToString("o"),
                ["last_inbound_ts"] = c.LastInTs is DateTime li
                    ? DateTime.SpecifyKind(li, DateTimeKind.Utc).ToString("o") : null,
                ["message_count"] = stat?.Count ?? 0,
                ["created_at"] = DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc).ToString("o"),
            });
            if (socialMap.TryGetValue(t.Id, out var social)) outv[^1]["social"] = social;
        }
        return outv;
    }
}
