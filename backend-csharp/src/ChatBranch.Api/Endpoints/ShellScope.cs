using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The scope rules the shell-wide surfaces (top-bar search, the Live chip) apply,
/// kept in ONE place so both read exactly what the corresponding list endpoints
/// would show the caller:
///
/// * conversations — the inbox rule (<c>GET /api/whatsapp/threads</c>):
///   superadmins and org-wide admins see every conversation (narrowed by
///   <c>?entity=</c> when given); company admins, agents and custom roles only
///   their ASSIGNED entities' subtrees —
///   an out-of-scope <c>?entity=</c> narrows to their own scope, never widens —
///   plus the unassigned bucket the inbox always shows. An agent with no
///   assignment keeps the tenant-wide view (unchanged behaviour, not a lockout).
/// * capabilities — gate every result TYPE, the same matrix the pages use.
/// </summary>
internal static class ShellScope
{
    internal static string RoleOf(ClaimsPrincipal me) => me.FindFirstValue(ClaimTypes.Role) ?? "";

    internal static Task<bool> HasCapAsync(AppDbContext db, ClaimsPrincipal me, string capability) =>
        AdminEndpoints.HasCapabilityAsync(db, RoleOf(me), capability);

    // ---- THE entity-scope rule ----------------------------------------------
    // Superadmin is never bound. Everyone else — admins included — is bound by
    // their ASSIGNED entities when they have any: an admin with assigned
    // entities is a COMPANY ADMIN (admin capabilities, but only inside those
    // entities' subtrees); an admin with none is an ORGANIZATION-WIDE admin (the
    // tenant view, unchanged). An agent with none keeps the tenant view too.
    // Every scoping helper below (and every endpoint's ad-hoc check) asks these.

    /// <summary>The entity ids that bound the caller (empty = not bound by
    /// assignment: superadmin, org-wide admins, unassigned agents).</summary>
    internal static List<int> ScopeRoots(AppDbContext db, ClaimsPrincipal me) =>
        RoleOf(me) == Roles.SuperAdmin
            ? new List<int>()
            : ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);

    /// <summary>An admin bound to assigned entities — admin capabilities inside
    /// their companies (and branches) only.</summary>
    internal static bool IsCompanyAdmin(AppDbContext db, ClaimsPrincipal me) =>
        RoleOf(me) == Roles.Admin && ScopeRoots(db, me).Count > 0;

    /// <summary>Superadmin, or an admin with no assigned entities: who may touch
    /// organization-wide (global) rows and settings.</summary>
    internal static bool IsOrgWideAdmin(AppDbContext db, ClaimsPrincipal me) =>
        RoleOf(me) switch
        {
            Roles.SuperAdmin => true,
            Roles.Admin => ScopeRoots(db, me).Count == 0,
            _ => false,
        };

    /// <summary>Bound by assigned entities (a company admin, an agent or a custom
    /// role with entities): acts only inside their subtrees, whatever their
    /// capabilities. Superadmin is never bound.</summary>
    internal static bool IsBound(AppDbContext db, ClaimsPrincipal me) => ScopeRoots(db, me).Count > 0;

    /// <summary>May the caller act at this level — the scope half of a write whose
    /// capability the route already checked: someone unbound anywhere (they hold
    /// the tenant-wide view); a bound caller only on an entity inside their
    /// subtrees — never the organization level (<paramref name="entityId"/> null).
    /// A capability never widens anyone beyond their entities.</summary>
    internal static bool MayActAt(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? entityId)
    {
        var bound = BoundEntityIds(db, sc, me);
        if (bound == null) return true;
        return entityId is int e && bound.Contains(e);
    }

    /// <summary>
    /// The write guard for an entity-LEVEL change (a settings override, a channel
    /// account, a team, a widget…) by anyone BOUND to entities — a company admin,
    /// or an agent / custom role granted the capability: allowed only on an entity
    /// inside their subtrees, never the organization level (null). Unbound callers
    /// are left to the endpoint's own rule (null = allowed, else the 403).
    /// </summary>
    internal static IResult? CompanyLevelError(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? entityId)
    {
        var bound = BoundEntityIds(db, sc, me);
        if (bound == null) return null;
        if (entityId is int e && bound.Contains(e)) return null;
        return Results.Json(new
        {
            detail = entityId == null
                ? "You manage your own companies only — the organization-wide setting is for someone not limited to particular entities."
                : "That entity is outside your companies.",
        }, statusCode: 403);
    }

    /// <summary>
    /// For actions with no entity to confine them (a tenant-wide sync, an engine
    /// tick, the org-wide voice library, the raw settings store…): someone unbound
    /// holding the route's capability may act; a company admin keeps the reach
    /// the admin role always had here; anyone else bound to entities is refused —
    /// granting them the capability must not widen them beyond their subtrees.
    /// null = allowed, else the 403.
    /// </summary>
    internal static IResult? OrgWideActionError(AppDbContext db, ClaimsPrincipal me) =>
        IsBound(db, me) && !IsCompanyAdmin(db, me)
            ? Results.Json(new
            {
                detail = "This acts on the whole organization — your account is limited to its assigned entities.",
            }, statusCode: 403)
            : null;

    /// <summary>Route filter form of <see cref="OrgWideActionError"/> — put it after the
    /// capability gate so a missing capability still answers first.</summary>
    internal static TBuilder OrgWideAction<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            return OrgWideActionError(db, http.User) ?? await next(ctx);
        });

    /// <summary>The write guard for one existing object (an account, a template's
    /// account…): an entity outside a bound caller's subtrees is refused; an
    /// organization-wide object (null) follows <see cref="OrgWideActionError"/>.</summary>
    internal static IResult? ObjectScopeError(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? entityId) =>
        entityId is int ? CompanyLevelError(db, sc, me, entityId) : OrgWideActionError(db, me);

    /// <summary>Route filter for organization-level writes (org-wide voice, the
    /// default mail settings…): anyone bound to entities (a company admin, a bound
    /// agent or custom role) gets a 403, everyone else passes through to the
    /// route's own gate.</summary>
    internal static TBuilder OrgWideOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            if (IsBound(db, http.User))
                return Results.Json(new
                {
                    detail = "This is an organization-wide setting — your account manages its own companies only.",
                }, statusCode: 403);
            return await next(ctx);
        });

    /// <summary>
    /// Entity ids whose conversations the caller may see, or null for "no
    /// narrowing" (every conversation). Unassigned conversations are always
    /// included on top, like the inbox.
    /// </summary>
    internal static List<int>? ThreadEntityIds(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        int? pickedEntityId)
    {
        {
            var assigned = ScopeRoots(db, me);
            if (assigned.Count > 0)
            {
                var allowed = sc.EntitySubtreeIds(assigned);
                return pickedEntityId is int picked && allowed.Contains(picked)
                    ? sc.PickSubtree(picked).Where(allowed.Contains).ToList()
                    : allowed;
            }
        }
        return pickedEntityId is int eid ? sc.PickSubtree(eid) : null;
    }

    /// <summary>
    /// The entity subtree an entity-BOUND caller (a company admin, an agent or a
    /// custom role with assigned entities) is confined to, or null for "not
    /// bound" (superadmins, org-wide admins, unassigned agents — the tenant view).
    /// </summary>
    internal static List<int>? BoundEntityIds(AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        var assigned = ScopeRoots(db, me);
        return assigned.Count > 0 ? sc.EntitySubtreeIds(assigned) : null;
    }

    /// <summary>A bound caller's assigned entities' subtrees PLUS their ancestors —
    /// every level whose inherited rows (channel accounts, libraries) they may use.
    /// Null when not bound.</summary>
    internal static List<int>? BoundInheritedIds(AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        var assigned = ScopeRoots(db, me);
        if (assigned.Count == 0) return null;
        return sc.EntitySubtreeIds(assigned).Concat(assigned.SelectMany(a => sc.EntityChain(a))).Distinct().ToList();
    }

    /// <summary>A picked <c>?entity=</c> outside a bound caller's scope is dropped
    /// (narrows to their own scope instead of widening).</summary>
    internal static int? ClampPick(List<int>? bound, int? picked) =>
        bound != null && picked is int p && !bound.Contains(p) ? null : picked;

    /// <summary>
    /// Libraries an outlet INHERITS (skills, knowledge, message + script
    /// templates, integrations…): the organization-wide rows, its ancestors'
    /// and the outlet's own. Superadmins and org-wide admins read any outlet's library (narrowed by
    /// <c>?entity=</c> when given — none = every outlet's rows, the management
    /// view). Company admins, agents and custom roles with ASSIGNED entities read the org-wide
    /// rows plus their assigned entities' subtrees only — an out-of-scope
    /// <c>?entity=</c> narrows to their own scope instead of widening, the same
    /// rule as conversations. An agent with no assignment keeps the tenant-wide
    /// view (not a lockout).
    /// </summary>
    internal static IQueryable<T> ScopeLibrary<T>(IQueryable<T> qs, AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, int? pickedEntityId) where T : class, IEntityScoped
    {
        {
            var assigned = ScopeRoots(db, me);
            if (assigned.Count > 0)
            {
                var allowed = sc.EntitySubtreeIds(assigned);
                // a multi-selection is already cut to the caller's entities (UseEntitySelection)
                if (pickedEntityId is int picked && allowed.Contains(picked)) return sc.ScopeInherited(qs, picked);
                // their outlets' own rows + what those outlets inherit from above
                var visible = allowed.Concat(assigned.SelectMany(a => sc.EntityChain(a))).Distinct().ToList();
                return sc.LibraryVisible(qs, visible);
            }
        }
        return sc.ScopeInherited(qs, pickedEntityId);
    }

    /// <summary>
    /// Does the caller see conversations of more than one COMPANY (top-level entity)? A
    /// person bound to entities of one company does not; the organization-wide view does
    /// once the tenant has two companies. The inbox then shows whose each chat is.
    /// </summary>
    internal static bool SeesSeveralCompanies(AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        var roots = ScopeRoots(db, me);
        if (roots.Count > 0) return roots.Select(r => sc.CompanyOf(r)).Distinct().Count() > 1;
        return db.Entities.AsNoTracking().Count(e => e.ParentId == null) > 1;
    }

    internal static IQueryable<Thread> ScopeThreads(IQueryable<Thread> qs, List<int>? entityIds) =>
        entityIds is null
            ? qs
            : qs.Where(t => t.EntityId == null || (t.EntityId != null && entityIds.Contains(t.EntityId.Value)));

    // ---- channel access ---------------------------------------------------------
    //
    // Two limits narrow the conversation rule, on top of the entity scope:
    //
    // * the ROLE's channel policy (Role.ChannelAccess — channels and connection
    //   methods, e.g. "SMS" or "WhatsApp, official numbers only"). HARD: a
    //   conversation on an account whose channel + method the role is not given is
    //   never theirs — unattributed or not — and an account-less conversation is
    //   theirs only on a channel the role has;
    // * the PERSON's allow-list (Profile.ChannelAccountIds). SOFT, as it always was:
    //   an unattributed conversation (no entity) stays everybody's; inside their
    //   entity scope it is theirs only on one of their accounts, or on no account yet.
    //
    // Superadmin is never limited by either. No policy + no list = no narrowing.

    /// <summary>The caller's channel limits (see the block above).</summary>
    internal sealed class ChannelAccess
    {
        public static readonly ChannelAccess None = new(null, null, null);

        /// <summary>The role's policy, or null = every channel.</summary>
        public RoleChannelPolicy? Policy { get; }
        /// <summary>Accounts the role's policy allows (null = no policy).</summary>
        public List<int>? RoleAccounts { get; }
        /// <summary>The person's own allow-list (null = none set).</summary>
        public List<int>? UserAccounts { get; }

        public ChannelAccess(RoleChannelPolicy? policy, List<int>? roleAccounts, List<int>? userAccounts)
        {
            Policy = policy;
            RoleAccounts = roleAccounts;
            UserAccounts = userAccounts;
        }

        public bool Limited => RoleAccounts != null || UserAccounts != null;

        /// <summary>The accounts the caller may pick / send on / list: both limits
        /// applied (null = any; an empty list = none).</summary>
        public List<int>? Accounts =>
            RoleAccounts == null ? UserAccounts
            : UserAccounts == null ? RoleAccounts
            : RoleAccounts.Intersect(UserAccounts).ToList();

        public bool AccountOk(int? accountId) =>
            accountId is not int id || Accounts is not List<int> ok || ok.Contains(id);

        /// <summary>The channel half of the conversation rule (the entity half is apart).</summary>
        public bool ThreadOk(int? entityId, string? channel, int? accountId)
        {
            if (Policy != null)
            {
                if (accountId is int a ? !RoleAccounts!.Contains(a) : !Policy.AllowsChannel(channel))
                    return false;
            }
            return UserAccounts == null || entityId == null || accountId == null
                   || UserAccounts.Contains(accountId.Value);
        }
    }

    /// <summary>The role's channel policy (null = every channel; superadmin always null).</summary>
    internal static RoleChannelPolicy? RolePolicy(AppDbContext db, string role)
    {
        if (role is "" or Roles.SuperAdmin) return null;
        try
        {
            return RoleChannelPolicy.Parse(db.Roles.AsNoTracking()
                .Where(r => r.Slug == role).Select(r => r.ChannelAccess).FirstOrDefault());
        }
        catch (Exception)
        {
            return null;   // an un-upgraded role store must not lock anyone out
        }
    }

    /// <summary>The caller's channel limits — the role's policy and their own allow-list.</summary>
    internal static ChannelAccess Access(AppDbContext db, ClaimsPrincipal me)
    {
        if (me.Identity?.IsAuthenticated != true) return ChannelAccess.None;
        var role = RoleOf(me);
        if (role == Roles.SuperAdmin) return ChannelAccess.None;
        List<int>? user = null;
        var name = me.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var ids = db.Profiles.AsNoTracking()
                .Where(p => p.User != null && p.User.Username == name)
                .Select(p => p.ChannelAccountIds)
                .FirstOrDefault();
            if (ids is { Count: > 0 }) user = ids;
        }
        var policy = RolePolicy(db, role);
        List<int>? roleAccounts = null;
        if (policy != null)
            roleAccounts = db.ChannelAccounts.AsNoTracking()
                .Select(a => new { a.Id, a.Channel, a.Provider }).ToList()
                .Where(a => policy.Allows(a.Channel, ChannelMethods.Of(a.Channel, a.Provider)))
                .Select(a => a.Id).ToList();
        return new ChannelAccess(policy, roleAccounts, user);
    }

    /// <summary>The accounts the caller may use, or null for "every channel in scope"
    /// (an empty list = none: their role is given no account that exists).</summary>
    internal static List<int>? ChannelAccountIds(AppDbContext db, ClaimsPrincipal me) => Access(db, me).Accounts;

    /// <summary>The conversation rule in memory: entity scope + channel access
    /// (null entity list = no entity narrowing).</summary>
    internal static bool ThreadVisible(int? entityId, string? channel, int? accountId, List<int>? entityIds,
        ChannelAccess access) =>
        (entityId == null || entityIds == null || entityIds.Contains(entityId.Value))
        && access.ThreadOk(entityId, channel, accountId);

    /// <summary>The conversation rule as a query (see <see cref="ThreadVisible"/>).</summary>
    internal static IQueryable<Thread> ScopeThreads(IQueryable<Thread> qs, List<int>? entityIds,
        ChannelAccess access)
    {
        qs = ScopeThreads(qs, entityIds);
        if (access.Policy != null)
        {
            var roleAccounts = access.RoleAccounts!;
            var roleChannels = access.Policy.Channels;
            qs = qs.Where(t => t.AccountId != null
                ? roleAccounts.Contains(t.AccountId.Value)
                : roleChannels.Contains(t.Channel));
        }
        if (access.UserAccounts is List<int> mine)
            qs = qs.Where(t => t.EntityId == null || t.AccountId == null || mine.Contains(t.AccountId.Value));
        return qs;
    }

    /// <summary>The conversations the caller may see (entity scope, optionally narrowed to
    /// <paramref name="pickedEntityId"/>, plus their channel access).</summary>
    internal static IQueryable<Thread> VisibleThreads(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        IQueryable<Thread> qs, int? pickedEntityId = null) =>
        ScopeThreads(qs, ThreadEntityIds(db, sc, me, pickedEntityId), Access(db, me));

    /// <summary>May the caller use this channel account (send on it, pick it, move a chat to it)?
    /// True when neither their role nor their allow-list rules it out. Entity scope is checked apart.</summary>
    internal static bool AccountAllowed(AppDbContext db, ClaimsPrincipal me, int? accountId) =>
        accountId is not int || Access(db, me).AccountOk(accountId);

    /// <summary>The 403 for a named account outside the caller's channel access (null = fine).</summary>
    internal static IResult? AccountNotAllowed(AppDbContext db, ClaimsPrincipal me, int? accountId) =>
        AccountAllowed(db, me, accountId)
            ? null
            : Results.Json(new { detail = "That channel isn't one of yours — ask a manager for access." },
                statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// The CONNECT half of the role's channel policy: may the caller add (or edit) an
    /// account of this channel through this connection method? null = fine, else a 403
    /// naming what their role is given. Superadmin and roles with no policy always pass.
    /// </summary>
    internal static IResult? ConnectError(AppDbContext db, ClaimsPrincipal me, string? channel, string method)
    {
        var policy = RolePolicy(db, RoleOf(me));
        var ch = (channel ?? "").Trim().ToLowerInvariant();
        if (policy == null || policy.Allows(ch, method)) return null;
        var detail = policy.AllowsChannel(ch)
            ? $"Your role can't connect {ChannelMethods.Label(ch, method)} — ask an admin to allow that connection method."
            : $"Your role can't use {ChannelMethods.Find(ch)?.Label ?? ch} — ask an admin for access to this channel.";
        return Results.Json(new { detail, code = "channel_not_allowed" }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary><see cref="ConnectError"/> for an existing account (edit, re-pair, delete).</summary>
    internal static IResult? ConnectError(AppDbContext db, ClaimsPrincipal me, ChannelAccount account) =>
        ConnectError(db, me, account.Channel, ChannelMethods.Of(account));

    /// <summary>
    /// Route filter for a route naming an existing channel account by <c>{id}</c> (edit,
    /// test, pair, re-register, delete): an account whose channel / connection method the
    /// caller's role is not given is refused with <see cref="ConnectError"/>. A missing
    /// account falls through to the handler's own 404. <paramref name="find"/> overrides
    /// the default lookup (a numeric primary key).
    /// </summary>
    internal static TBuilder RequireAccountChannel<TBuilder>(this TBuilder builder,
        Func<AppDbContext, string, Task<ChannelAccount?>>? find = null) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            if (RolePolicy(db, RoleOf(http.User)) == null) return await next(ctx);
            var raw = http.Request.RouteValues["id"]?.ToString() ?? "";
            var account = find != null
                ? await find(db, raw)
                : int.TryParse(raw, out var pk)
                    ? await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == pk)
                    : null;
            if (account != null && ConnectError(db, http.User, account) is { } refused) return refused;
            return await next(ctx);
        });

    /// <summary>The chat deep link the inbox understands (/chats?channel=…&amp;key=…&amp;thread_id=…):
    /// a customer has one conversation per company, so the id picks which.</summary>
    internal static string ChatUrl(string channel, string key, int? threadId = null) =>
        ChatBranch.Data.Stores.ThreadCompanies.LegacyChatHref(channel, key, threadId);

    /// <summary>LIKE pattern for a free-text term, with the wildcards escaped.</summary>
    internal static string Like(string term) =>
        "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    internal static string Snip(string? text, int max = 90)
    {
        var s = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>A short snippet centred on the first match, so a hit deep in a
    /// long message still shows the matched words.</summary>
    internal static string SnipAround(string? text, string term, int max = 90)
    {
        var s = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        var at = s.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || s.Length <= max) return Snip(s, max);
        var start = Math.Max(0, at - max / 3);
        var piece = s.Substring(start, Math.Min(max, s.Length - start));
        return (start > 0 ? "…" : "") + piece.Trim() + (start + piece.Length < s.Length ? "…" : "");
    }
}
