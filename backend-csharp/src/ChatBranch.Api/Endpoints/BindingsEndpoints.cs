using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Bulk relationship management — one screen for every many-to-many binding in
/// the system, instead of ticking boxes one row at a time in five different
/// places.
///
/// The shape is deliberately uniform: pick a TARGET (an entity, a team, a
/// provider), get every CANDIDATE with a `member` flag, then send a diff. That
/// uniformity is what lets one page filter, select-all-matching and apply in
/// batches for relationships whose storage could not be more different —
/// a join table (ContactEntity), a skip-navigation collection (Profile.Entities),
/// a row with its own fields (ResourceLink) and a plain FK (Profile.ContactId).
///
/// Every kind is symmetric: bind "contacts to an entity" or "entities to a
/// contact" — whichever list the operator has in front of them.
/// </summary>
public static class BindingsEndpoints
{
    /// <summary>
    /// The binding kinds the UI may ask for. `Target` is what you pick first;
    /// `Candidate` is what you tick. Reversed variants exist so neither side is
    /// second-class.
    /// </summary>
    public static readonly (string Id, string Label, string Target, string Candidate, string Hint)[] Kinds =
    {
        ("entity-contacts", "Contacts on an entity", "entity", "contact",
            "Which customers belong to this branch. A contact on a child branch is visible to its parent."),
        ("contact-entities", "Entities for a contact", "contact", "entity",
            "Which branches this customer belongs to. Zero is allowed."),
        ("team-users", "Members of a team", "team", "user",
            "Who is in this team. New conversations round-robin across them."),
        ("user-teams", "Teams for a user", "user", "team",
            "Which teams this login belongs to."),
        ("entity-users", "Users with access to an entity", "entity", "user",
            "Agents scoped to this branch see only its conversations (and those beneath it)."),
        ("user-entities", "Entity access for a user", "user", "entity",
            "Which branches this login may see. Empty = the whole organization."),
        ("provider-contacts", "Customers bound to a service provider", "provider", "contact",
            "The customers this teacher/practitioner serves."),
        ("contact-providers", "Service providers for a contact", "contact", "provider",
            "Who serves this customer."),
    };

    public static void MapBindingsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- what can be bound, and what you can pick ------------------------
        app.MapGet("/api/bindings/catalog", async (AppDbContext db, ClaimsPrincipal me) =>
        {
            // Ordinal sorting puts "MS1" before "Main", which reads as a bug to
            // anyone scanning the list — order these the way a person would.
            var entities = await db.Entities.AsNoTracking().Include(e => e.Parent)
                .OrderBy(e => e.Name.ToLower())
                .Select(e => new { id = e.Id, label = e.Name, sub = e.Parent != null ? e.Parent.Name : "Top level" })
                .ToListAsync();
            var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name.ToLower())
                .Select(t => new { id = t.Id, label = t.Name, sub = t.Description })
                .ToListAsync();
            var users = await db.Profiles.AsNoTracking().Include(p => p.User)
                .Where(p => p.User != null && p.User.IsActive)
                .OrderBy(p => p.User!.Username)
                .Select(p => new { id = p.UserId, label = p.User!.Username, sub = p.Role })
                .ToListAsync();
            var providers = await db.Resources.AsNoTracking().OrderBy(r => r.Name.ToLower())
                .Select(r => new { id = r.Id, label = r.Name, sub = r.Active ? "" : "inactive" })
                .ToListAsync();
            var contacts = await db.Contacts.AsNoTracking().OrderBy(c => c.Name.ToLower()).Take(2000)
                .Select(c => new { id = c.Id, label = c.Name, sub = c.Phone })
                .ToListAsync();

            return Results.Ok(new
            {
                kinds = Kinds.Select(k => new
                {
                    id = k.Id, label = k.Label, target = k.Target,
                    candidate = k.Candidate, hint = k.Hint,
                }),
                // one pool per side so the UI never has to guess what to list
                pools = new { entities, teams, users, providers, contacts },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- current membership for one target -------------------------------
        app.MapGet("/api/bindings/{kind}", async (string kind, int target, AppDbContext db) =>
        {
            if (!Kinds.Any(k => k.Id == kind))
                return Results.Json(new { detail = "Unknown binding kind." }, statusCode: 400);
            var members = await MembersAsync(db, kind, target);
            return Results.Ok(new { kind, target, member_ids = members });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- apply a diff -----------------------------------------------------
        // A DIFF, not a replacement: the operator may be filtering to a subset,
        // and sending "these are all the members" from a filtered list would
        // silently unbind everyone they had filtered out.
        app.MapPost("/api/bindings/{kind}", async (string kind, JsonElement body, AppDbContext db,
            ClaimsPrincipal me) =>
        {
            if (!Kinds.Any(k => k.Id == kind))
                return Results.Json(new { detail = "Unknown binding kind." }, statusCode: 400);
            var target = Int(body, "target") ?? 0;
            if (target <= 0)
                return Results.Json(new { detail = "target is required." }, statusCode: 400);

            var add = IntList(body, "add");
            var remove = IntList(body, "remove");
            var overlap = add.Intersect(remove).ToList();
            if (overlap.Count > 0)
                return Results.Json(new { detail = "The same id cannot be added and removed." },
                                    statusCode: 400);

            var (added, removed) = await ApplyAsync(db, kind, target, add, remove);
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "binding.update",
                Target = $"{kind}#{target}", Detail = $"+{added} / -{removed}",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                ok = true, added, removed,
                member_ids = await MembersAsync(db, kind, target),
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    // ---- per-kind storage ---------------------------------------------------

    /// <summary>Candidate ids currently bound to this target.</summary>
    private static async Task<List<int>> MembersAsync(AppDbContext db, string kind, int target) => kind switch
    {
        "entity-contacts" => await db.ContactEntities.AsNoTracking()
            .Where(l => l.EntityId == target).Select(l => l.ContactId).ToListAsync(),
        "contact-entities" => await db.ContactEntities.AsNoTracking()
            .Where(l => l.ContactId == target).Select(l => l.EntityId).ToListAsync(),

        "team-users" => await db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == target).Select(m => m.UserId).ToListAsync(),
        "user-teams" => await db.TeamMembers.AsNoTracking()
            .Where(m => m.UserId == target).Select(m => m.TeamId).ToListAsync(),

        "entity-users" => await db.Profiles.AsNoTracking()
            .Where(p => p.Entities.Any(e => e.Id == target)).Select(p => p.UserId).ToListAsync(),
        "user-entities" => await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == target).SelectMany(p => p.Entities.Select(e => e.Id)).ToListAsync(),

        "provider-contacts" => await db.ResourceLinks.AsNoTracking()
            .Where(l => l.ResourceId == target && l.Active).Select(l => l.ContactId).ToListAsync(),
        "contact-providers" => await db.ResourceLinks.AsNoTracking()
            .Where(l => l.ContactId == target && l.Active).Select(l => l.ResourceId).ToListAsync(),

        _ => new List<int>(),
    };

    /// <summary>
    /// Apply the diff. Adding something already bound (or removing something
    /// that is not) is a NO-OP rather than an error — two operators working the
    /// same list must not collide.
    /// </summary>
    private static async Task<(int added, int removed)> ApplyAsync(AppDbContext db, string kind,
        int target, List<int> add, List<int> remove)
    {
        var current = await MembersAsync(db, kind, target);
        var toAdd = add.Distinct().Where(id => !current.Contains(id)).ToList();
        var toRemove = remove.Distinct().Where(current.Contains).ToList();
        if (toAdd.Count == 0 && toRemove.Count == 0) return (0, 0);

        switch (kind)
        {
            case "entity-contacts":
            case "contact-entities":
            {
                var byEntity = kind == "entity-contacts";
                Func<int, int> entityOf = byEntity ? _ => target : id => id;
                Func<int, int> contactOf = byEntity ? id => id : _ => target;
                foreach (var id in toAdd)
                    db.ContactEntities.Add(new ContactEntity
                    {
                        EntityId = entityOf(id), ContactId = contactOf(id), CreatedAt = DateTime.UtcNow,
                    });
                var goneIds = toRemove.Select(id => (e: entityOf(id), c: contactOf(id))).ToList();
                var links = await db.ContactEntities
                    .Where(l => kind == "entity-contacts" ? l.EntityId == target : l.ContactId == target)
                    .ToListAsync();
                db.ContactEntities.RemoveRange(
                    links.Where(l => goneIds.Any(g => g.e == l.EntityId && g.c == l.ContactId)));
                break;
            }

            case "team-users":
            case "user-teams":
            {
                var byTeam = kind == "team-users";
                Func<int, int> teamOf = byTeam ? _ => target : id => id;
                Func<int, int> userOf = byTeam ? id => id : _ => target;
                foreach (var id in toAdd)
                    db.TeamMembers.Add(new TeamMember
                    {
                        TeamId = teamOf(id), UserId = userOf(id), CreatedAt = DateTime.UtcNow,
                    });
                var rows = await db.TeamMembers
                    .Where(m => kind == "team-users" ? m.TeamId == target : m.UserId == target)
                    .ToListAsync();
                db.TeamMembers.RemoveRange(rows.Where(m =>
                    toRemove.Contains(kind == "team-users" ? m.UserId : m.TeamId)));
                break;
            }

            case "entity-users":
            case "user-entities":
            {
                // Profile.Entities is a skip navigation, so both directions load
                // the profiles and mutate the collection.
                var userIds = kind == "entity-users" ? toAdd.Concat(toRemove).ToList() : new List<int> { target };
                var profiles = await db.Profiles.Include(p => p.Entities)
                    .Where(p => userIds.Contains(p.UserId)).ToListAsync();
                var entityIds = kind == "entity-users"
                    ? new List<int> { target }
                    : toAdd.Concat(toRemove).ToList();
                var entities = await db.Entities.Where(e => entityIds.Contains(e.Id)).ToListAsync();

                foreach (var profile in profiles)
                foreach (var entity in entities)
                {
                    var candidateId = kind == "entity-users" ? profile.UserId : entity.Id;
                    var has = profile.Entities.Any(e => e.Id == entity.Id);
                    if (toAdd.Contains(candidateId) && !has) profile.Entities.Add(entity);
                    else if (toRemove.Contains(candidateId) && has) profile.Entities.Remove(entity);
                }
                break;
            }

            case "provider-contacts":
            case "contact-providers":
            {
                var byProvider = kind == "provider-contacts";
                Func<int, int> resourceOf = byProvider ? _ => target : id => id;
                Func<int, int> contactOf2 = byProvider ? id => id : _ => target;
                foreach (var id in toAdd)
                {
                    // An inactive link is reactivated rather than duplicated —
                    // rebinding a customer must not create a second row.
                    var existing = await db.ResourceLinks.FirstOrDefaultAsync(
                        l => l.ResourceId == resourceOf(id) && l.ContactId == contactOf2(id));
                    if (existing != null)
                    {
                        existing.Active = true;
                        existing.UpdatedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        db.ResourceLinks.Add(new ResourceLink
                        {
                            ResourceId = resourceOf(id), ContactId = contactOf2(id), Active = true,
                            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                        });
                    }
                }
                var rows = await db.ResourceLinks
                    .Where(l => kind == "provider-contacts" ? l.ResourceId == target : l.ContactId == target)
                    .ToListAsync();
                // Deactivate rather than delete: the binding's history (since
                // when, what relationship) is worth keeping.
                foreach (var row in rows.Where(l =>
                             toRemove.Contains(kind == "provider-contacts" ? l.ContactId : l.ResourceId)))
                {
                    row.Active = false;
                    row.UpdatedAt = DateTime.UtcNow;
                }
                break;
            }
        }

        await db.SaveChangesAsync();
        return (toAdd.Count, toRemove.Count);
    }

    // ---- helpers ------------------------------------------------------------

    private static int? Int(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt32(out var n) ? n : null,
                JsonValueKind.String => int.TryParse(v.GetString(), out var n) ? n : null,
                _ => null,
            }
            : null;

    private static List<int> IntList(JsonElement b, string key)
    {
        var outv = new List<int>();
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(key, out var v)) return outv;
        if (v.ValueKind != JsonValueKind.Array) return outv;
        foreach (var el in v.EnumerateArray())
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) outv.Add(n);
            else if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var m)) outv.Add(m);
        return outv;
    }
}
