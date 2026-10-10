using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Domain;
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

    /// <summary>Admin-level (admin or superadmin) AND allowed to act at this
    /// level: org-wide admins anywhere; a company admin only on an entity inside
    /// their subtree — never the organization level (<paramref name="entityId"/> null).</summary>
    internal static bool AdminMayAct(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? entityId)
    {
        if (RoleOf(me) is not (Roles.SuperAdmin or Roles.Admin)) return false;
        var bound = BoundEntityIds(db, sc, me);
        if (bound == null) return true;
        return entityId is int e && bound.Contains(e);
    }

    /// <summary>
    /// The write guard for an entity-LEVEL change (a settings override, a channel
    /// account, a shared connection…) by a company admin: allowed only on an
    /// entity inside their companies, never the organization level (null). Other
    /// callers are left to the endpoint's own rule (null = allowed, else the 403).
    /// </summary>
    internal static IResult? CompanyLevelError(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? entityId)
    {
        if (!IsCompanyAdmin(db, me)) return null;
        var bound = BoundEntityIds(db, sc, me);
        if (bound != null && entityId is int e && bound.Contains(e)) return null;
        return Results.Json(new
        {
            detail = entityId == null
                ? "Company admins manage their own companies only — the organization-wide setting is for an organization-wide admin."
                : "That entity is outside your companies.",
        }, statusCode: 403);
    }

    /// <summary>Route filter for organization-level writes (org-wide voice, the
    /// default mail settings…): a company admin gets a 403, everyone else passes
    /// through to the route's own policy.</summary>
    internal static TBuilder OrgWideOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            if (IsCompanyAdmin(db, http.User))
                return Results.Json(new
                {
                    detail = "This is an organization-wide setting — company admins manage their own companies only.",
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

    internal static IQueryable<Thread> ScopeThreads(IQueryable<Thread> qs, List<int>? entityIds) =>
        entityIds is null
            ? qs
            : qs.Where(t => t.EntityId == null || (t.EntityId != null && entityIds.Contains(t.EntityId.Value)));

    /// <summary>The chat deep link the inbox understands (/chats?channel=…&amp;key=…).</summary>
    internal static string ChatUrl(string channel, string key) =>
        $"/chats?channel={Uri.EscapeDataString(channel)}&key={Uri.EscapeDataString(key)}";

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
