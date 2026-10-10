using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The write side of multi-entity library availability ("Available to:
/// Everyone / Selected entities") shared by quick replies, call scripts,
/// knowledge, AI skills and automation rules:
///
/// * <see cref="ParseAsync"/> reads <c>entities: [slug|id, …]</c> (an empty
///   list = global) or, for older clients, <c>entity: slug</c> ("" / null =
///   global). An unknown entity is a 400 — never a silent "global".
/// * <see cref="Check"/> is the permission rule: every entity ADDED or
///   REMOVED must be one the caller may edit (<see cref="AiControlsEndpoints.MayEdit"/>:
///   admins any entity of their tenant, an assigned user only inside their
///   assigned subtree, an unassigned user the tenant-wide view), and making an
///   item global — or narrowing a global one — is an org-wide decision
///   (<see cref="MayGoGlobal"/>: admin+, or a capability holder with no assigned entities).
///
/// The read rule lives in <see cref="ScopingService.LibraryVisible{T}"/>; the
/// storage invariant in <see cref="LibraryEntities"/>.
/// </summary>
internal static class LibraryScope
{
    internal record Pick(bool Given, List<int> Ids, IResult? Error);

    /// <summary>`entities` wins over `entity`; neither key = not given (keep).</summary>
    internal static Task<Pick> ReadAsync(AppDbContext db, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return Task.FromResult(new Pick(false, new(), null));
        JsonElement? list = body.TryGetProperty("entities", out var l) ? l : null;
        var hasEntity = body.TryGetProperty("entity", out var e);
        string? entity = hasEntity && e.ValueKind == JsonValueKind.String ? e.GetString()
            : hasEntity && e.ValueKind == JsonValueKind.Number ? e.ToString() : null;
        return ParseAsync(db, list, entity, hasEntity);
    }

    internal static async Task<Pick> ParseAsync(AppDbContext db, JsonElement? entities, string? entity, bool entityGiven)
    {
        var raw = new List<string>();
        if (entities is JsonElement list && list.ValueKind != JsonValueKind.Null && list.ValueKind != JsonValueKind.Undefined)
        {
            if (list.ValueKind == JsonValueKind.String)
                raw.AddRange((list.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else if (list.ValueKind == JsonValueKind.Array)
                foreach (var x in list.EnumerateArray())
                {
                    var v = x.ValueKind switch
                    {
                        JsonValueKind.String => x.GetString() ?? "",
                        JsonValueKind.Number => x.ToString(),
                        JsonValueKind.Object when x.TryGetProperty("slug", out var s) => s.GetString() ?? "",
                        JsonValueKind.Object when x.TryGetProperty("id", out var i) => i.ToString(),
                        _ => "",
                    };
                    if (v.Trim().Length > 0) raw.Add(v.Trim());
                }
            else return new Pick(true, new(), Results.BadRequest(new { detail = "entities must be a list of entity slugs or ids." }));
        }
        else if (entityGiven)
        {
            if (!string.IsNullOrWhiteSpace(entity) && entity is not ("all" or "__all__")) raw.Add(entity.Trim());
        }
        else return new Pick(false, new(), null);

        return await Resolve(db, raw);
    }

    /// <summary>Slugs / numeric ids → entity ids (order kept, deduped).</summary>
    internal static async Task<Pick> Resolve(AppDbContext db, IEnumerable<string> raw)
    {
        var ids = new List<int>();
        foreach (var token in raw)
        {
            if (token is "all" or "__all__" or "global") continue;
            var row = int.TryParse(token, out var n)
                ? await db.Entities.AsNoTracking().Where(x => x.Id == n).Select(x => (int?)x.Id).FirstOrDefaultAsync()
                : await db.Entities.AsNoTracking().Where(x => x.Slug == token).Select(x => (int?)x.Id).FirstOrDefaultAsync();
            if (row is not int id)
                return new Pick(true, new(), Results.BadRequest(new { detail = $"Unknown entity '{token}'." }));
            if (!ids.Contains(id)) ids.Add(id);
        }
        return new Pick(true, ids, null);
    }

    /// <summary>
    /// May the caller change an item's set from <paramref name="before"/> (null =
    /// a new item) to <paramref name="after"/>? null = yes, else the 403.
    /// </summary>
    internal static async Task<IResult?> Check(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        IReadOnlyCollection<int>? before, IReadOnlyCollection<int> after)
    {
        var prev = before ?? Array.Empty<int>();
        var wasGlobal = before != null && prev.Count == 0;
        var nowGlobal = after.Count == 0;
        if ((nowGlobal && (before == null || !wasGlobal)) || (wasGlobal && !nowGlobal))
            if (!MayGoGlobal(db, me))
                return Forbidden("Only an admin, or someone not limited to particular entities, can make this available " +
                                 "to everyone (organization-wide) or narrow an organization-wide item.");

        var changed = after.Except(prev).Concat(prev.Except(after)).Distinct().ToList();
        if (changed.Count == 0) return null;
        List<int>? tenantIds = null;
        if (ShellScope.RoleOf(me) == Roles.Admin)   // an admin: their tenant (a company admin: their companies)
            tenantIds = (await AdminEndpoints.AccessibleEntitiesAsync(db, me)).Select(e => e.Id).ToList();
        foreach (var id in changed)
        {
            if (!AiControlsEndpoints.MayEdit(db, sc, me, id) || (tenantIds != null && !tenantIds.Contains(id)))
            {
                var name = await db.Entities.AsNoTracking().Where(e => e.Id == id).Select(e => e.Name).FirstOrDefaultAsync();
                return Forbidden($"'{name ?? id.ToString()}' is outside your scope — you can only pick entities you manage.");
            }
        }
        return null;
    }

    /// <summary>
    /// Organization-wide library rows: superadmins, org-wide admins, and a holder
    /// of the library's capability with NO assigned entities (they already hold
    /// the tenant-wide view — the library writes have always followed the
    /// capability, not the role). Someone assigned to entities — a company admin
    /// included — stays inside them.
    /// </summary>
    internal static bool MayGoGlobal(AppDbContext db, ClaimsPrincipal me) =>
        // superadmin, an org-wide admin, or any unassigned holder — a COMPANY
        // admin (assigned entities) stays inside their companies
        ShellScope.ScopeRoots(db, me).Count == 0;

    /// <summary>
    /// May the caller edit or delete an EXISTING item whose availability is
    /// <paramref name="current"/> (empty = organization-wide)? An unbound caller
    /// (superadmin, an org-wide admin, an unassigned holder) yes; a bound one — a
    /// company admin, an assigned agent — only when every entity the item serves
    /// lies inside their own subtrees: an org-wide item, or one shared with
    /// another company, is read-only to them. null = yes, else the 403.
    /// </summary>
    internal static IResult? CheckExisting(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        IReadOnlyCollection<int> current)
    {
        if (ShellScope.BoundEntityIds(db, sc, me) is not List<int> bound) return null;
        if (current.Count == 0)
            return Forbidden("This item is available to everyone (organization-wide) — only an organization-wide admin can change it.");
        if (current.Any(id => !bound.Contains(id)))
            return Forbidden("This item is also available outside your entities — only an organization-wide admin can change it.");
        return null;
    }

    private static IResult Forbidden(string detail) => Results.Json(new { detail }, statusCode: 403);
}
