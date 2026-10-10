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
/// * conversations — the inbox rule (<c>GET /api/whatsapp/threads</c>): admins
///   and superadmins see every conversation (narrowed by <c>?entity=</c> when
///   given), agents and custom roles only their ASSIGNED entities' subtrees —
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

    /// <summary>
    /// Entity ids whose conversations the caller may see, or null for "no
    /// narrowing" (every conversation). Unassigned conversations are always
    /// included on top, like the inbox.
    /// </summary>
    internal static List<int>? ThreadEntityIds(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        int? pickedEntityId)
    {
        var role = RoleOf(me);
        if (role is not (Roles.SuperAdmin or Roles.Admin))
        {
            var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
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
    /// The entity subtree an entity-BOUND caller (an agent or custom role with
    /// assigned entities) is confined to, or null for "not bound" (admins,
    /// superadmins, unassigned agents — the tenant view).
    /// </summary>
    internal static List<int>? BoundEntityIds(AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        if (RoleOf(me) is Roles.SuperAdmin or Roles.Admin) return null;
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        return assigned.Count > 0 ? sc.EntitySubtreeIds(assigned) : null;
    }

    /// <summary>A bound caller's assigned entities' subtrees PLUS their ancestors —
    /// every level whose inherited rows (channel accounts, libraries) they may use.
    /// Null when not bound.</summary>
    internal static List<int>? BoundInheritedIds(AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        if (RoleOf(me) is Roles.SuperAdmin or Roles.Admin) return null;
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
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
    /// and the outlet's own. Admins and superadmins read any outlet's library (narrowed by
    /// <c>?entity=</c> when given — none = every outlet's rows, the management
    /// view). Agents and custom roles with ASSIGNED entities read the org-wide
    /// rows plus their assigned entities' subtrees only — an out-of-scope
    /// <c>?entity=</c> narrows to their own scope instead of widening, the same
    /// rule as conversations. An agent with no assignment keeps the tenant-wide
    /// view (not a lockout).
    /// </summary>
    internal static IQueryable<T> ScopeLibrary<T>(IQueryable<T> qs, AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, int? pickedEntityId) where T : class, IEntityScoped
    {
        if (RoleOf(me) is not (Roles.SuperAdmin or Roles.Admin))
        {
            var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
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
