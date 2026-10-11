using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Tags;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Bulk relationship management — one screen for every many-to-many binding in
/// the system, instead of ticking boxes one row at a time in five different
/// places.
///
/// The shape is deliberately uniform: pick a TARGET (a user, a team, a tag, an
/// entity…), get every CANDIDATE with a `member` flag, then send a diff. That
/// uniformity is what lets one page (the BindingGrid card pattern) bind
/// relationships whose storage could not be more different — a join table
/// (ContactEntity), a skip-navigation collection (Profile.Entities), a row with
/// its own values (TagAssignment) and a plain FK (ChannelAccount.EntityId).
///
/// The first six kinds are the ones the Bindings page offers; the reversed
/// variants stay for API callers so neither side is second-class.
/// </summary>
public static class BindingsEndpoints
{
    /// <summary>
    /// The binding kinds. `Target` is what you pick first; `Candidate` is what
    /// you tick; `Capability` gates the write (checked server-side).
    /// </summary>
    public static readonly (string Id, string Label, string Target, string Candidate, string Capability, bool Primary, string Hint)[] Kinds =
    {
        ("user-entities", "User ↔ entities", "user", "entity", "users.manage", true,
            "Which entities this login may see. A main entity includes everything beneath it. None = the whole organization."),
        ("team-users", "Team ↔ members", "team", "user", "users.manage", true,
            "Who is in this team. New conversations round-robin across its members."),
        ("contact-entities", "Contact ↔ entities", "contact", "entity", "contacts.manage", true,
            "Which entities this customer belongs to. A contact on a sub-entity is visible to its main entity."),
        ("tag-contacts", "Tag ↔ contacts", "tag", "contact", "tags.manage", true,
            "Which contacts carry this tag. Applying a tag that has a group replaces the contact's other tag of that group."),
        ("tag-entities", "Tag ↔ entities", "tag", "entity", "tags.manage", true,
            "Which entities carry this tag (e.g. inner company / outside company)."),
        ("entity-accounts", "Channel account ↔ entity", "entity", "account", "channels.manage", true,
            "The numbers and inboxes that belong to this entity. Messages they receive are filed under it. An account belongs to one entity at a time — binding it here moves it."),
        ("entity-contacts", "Contacts on an entity", "entity", "contact", "contacts.manage", false,
            "Which customers belong to this entity."),
        ("user-teams", "Teams for a user", "user", "team", "users.manage", false,
            "Which teams this login belongs to."),
        ("entity-users", "Users with access to an entity", "entity", "user", "users.manage", false,
            "Agents scoped to this entity see only its conversations (and those beneath it)."),
    };

    /// <summary>Who may READ a kind's members besides users.view (the Bindings page).</summary>
    private static string ReadCapability(string kind) => kind switch
    {
        "contact-entities" or "entity-contacts" => "contacts.view",
        "tag-contacts" or "tag-entities" => "tags.view",
        _ => "users.view",
    };

    public static void MapBindingsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- what can be bound, and what you can pick ------------------------
        app.MapGet("/api/bindings/catalog", async (AppDbContext db, ClaimsPrincipal me, ScopingService sc) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            var tenantId = int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : (int?)null;

            // Ordinal sorting puts "MS1" before "Main", which reads as a bug to
            // anyone scanning the list — order these the way a person would.
            var entityRows = await AdminEndpoints.AccessibleEntitiesAsync(db, me);
            var entities = entityRows.OrderBy(e => e.Name.ToLower())
                .Select(e => new
                {
                    id = e.Id, label = e.Name, slug = e.Slug, parent_id = e.ParentId,
                    sub = e.Parent != null ? e.Parent.Name : "Main entity",
                }).ToList();
            var visibleEntityIds = entityRows.Select(e => e.Id).ToHashSet();

            var teams = (await db.Teams.AsNoTracking().Include(x => x.Entity).ToListAsync())
                .Where(x => x.EntityId == null || visibleEntityIds.Contains(x.EntityId.Value))
                .OrderBy(x => x.Name.ToLower())
                .Select(x => new { id = x.Id, label = x.Name, sub = x.Entity?.Name ?? "Organization-wide" })
                .ToList();

            var userQ = db.Profiles.AsNoTracking().Include(p => p.User)
                .Where(p => p.User != null && p.User.IsActive);
            if (role != Roles.SuperAdmin)
                userQ = userQ.Where(p => p.TenantId == tenantId);
            var users = await userQ.OrderBy(p => p.User!.Username)
                .Select(p => new { id = p.UserId, label = p.User!.Username, sub = p.Role })
                .ToListAsync();

            var contacts = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                .OrderBy(c => c.Name.ToLower()).Take(2000)
                .Select(c => new
                {
                    id = c.Id, label = c.Name, sub = c.Phone,
                    entity_ids = c.EntityLinks.Select(l => l.EntityId).ToList(),
                })
                .ToListAsync();

            var tags = (await db.Tags.AsNoTracking().ToListAsync())
                .Where(x => x.EntityId == null || visibleEntityIds.Contains(x.EntityId.Value))
                .OrderBy(x => x.AppliesTo).ThenBy(x => x.Position).ThenBy(x => x.Name.ToLower())
                .Select(x => new
                {
                    id = x.Id, label = x.Name, sub = x.GroupName, slug = x.Slug, color = x.Color,
                    applies_to = x.AppliesTo, group = x.GroupName,
                }).ToList();

            var accounts = (await db.ChannelAccounts.AsNoTracking().Include(a => a.Entity).ToListAsync())
                .Where(a => a.EntityId == null || visibleEntityIds.Contains(a.EntityId.Value))
                .OrderBy(a => a.Channel).ThenBy(a => a.Name.ToLower())
                .Select(a => new
                {
                    id = a.Id,
                    label = a.Name.Length > 0 ? a.Name : a.ExternalId,
                    sub = a.Entity?.Name ?? "Organization-wide",
                    channel = a.Channel, provider = a.Provider, entity_id = a.EntityId,
                }).ToList();

            // The Bindings page (users.view) gets every pool; the Tags page's binding
            // view (tags.view) only what a tag binds to — contacts with contacts.view.
            if (!await ShellScope.HasCapAsync(db, me, "users.view"))
            {
                teams.Clear();
                users.Clear();
                accounts.Clear();
                if (!await ShellScope.HasCapAsync(db, me, "contacts.view")) contacts.Clear();
            }

            return Results.Ok(new
            {
                kinds = Kinds.Select(k => new
                {
                    id = k.Id, label = k.Label, target = k.Target,
                    candidate = k.Candidate, hint = k.Hint, primary = k.Primary,
                    capability = k.Capability,
                }),
                // one pool per side so the UI never has to guess what to list
                pools = new { entities, teams, users, contacts, tags, accounts },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("users.view", "tags.view");

        // ---- current membership for one target -------------------------------
        app.MapGet("/api/bindings/{kind}", async (string kind, int target, AppDbContext db, ClaimsPrincipal me,
            ScopingService sc) =>
        {
            if (!Kinds.Any(k => k.Id == kind))
                return Results.Json(new { detail = "Unknown binding kind." }, statusCode: 400);
            // the Bindings page reads every kind; the contact page its entities, the Tags page its targets
            var need = ReadCapability(kind);
            if (!await ShellScope.HasCapAsync(db, me, "users.view") && !await ShellScope.HasCapAsync(db, me, need))
                return Results.Json(new { detail = $"The {need} capability is required." }, statusCode: 403);
            var members = await MembersAsync(db, kind, target);
            // contact bindings follow the contact scope (tenant + an agent's entities)
            if (kind == "contact-entities")
            {
                if (!await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc).AnyAsync(c => c.Id == target))
                    return Results.Json(new { detail = "Contact not found." }, statusCode: 404);
                if (ShellScope.RoleOf(me) is not Roles.SuperAdmin)
                {
                    var tenant = TenantOf(me);
                    var visible = (await db.Entities.AsNoTracking().Where(e => e.TenantId == tenant)
                        .Select(e => e.Id).ToListAsync()).ToHashSet();
                    if (ShellScope.BoundEntityIds(db, sc, me) is List<int> bound) visible.IntersectWith(bound);
                    members = members.Where(visible.Contains).ToList();
                }
            }
            else if (kind == "entity-contacts")
            {
                if (!await EntityVisibleAsync(db, sc, me, target))
                    return Results.Json(new { detail = "Entity not found." }, statusCode: 404);
                var ids = members;
                members = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                    .Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync();
            }
            return Results.Ok(new { kind, target, member_ids = members });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("users.view", "contacts.view", "tags.view");

        // ---- apply a diff -----------------------------------------------------
        // A DIFF, not a replacement: the operator may be filtering to a subset,
        // and sending "these are all the members" from a filtered list would
        // silently unbind everyone they had filtered out.
        app.MapPost("/api/bindings/{kind}", async (string kind, JsonElement body, AppDbContext db,
            ClaimsPrincipal me, ScopingService sc) =>
        {
            var def = Kinds.FirstOrDefault(k => k.Id == kind);
            if (def.Id == null)
                return Results.Json(new { detail = "Unknown binding kind." }, statusCode: 400);
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await AdminEndpoints.HasCapabilityAsync(db, role, def.Capability))
                return Results.Json(new { detail = $"The {def.Capability} capability is required." },
                                    statusCode: 403);
            var target = Int(body, "target") ?? 0;
            if (target <= 0)
                return Results.Json(new { detail = "target is required." }, statusCode: 400);

            var add = IntList(body, "add");
            var remove = IntList(body, "remove");
            var overlap = add.Intersect(remove).ToList();
            if (overlap.Count > 0)
                return Results.Json(new { detail = "The same id cannot be added and removed." },
                                    statusCode: 400);

            // ---- user tiers, as /api/v1/users: an admin manages agent-level users of
            // their own tenant only (never an admin, a superadmin or themselves), and
            // entity access binds agent-level users only
            if (kind is "user-entities" or "user-teams" or "team-users" or "entity-users")
            {
                var userIds = kind is "user-entities" or "user-teams" ? new List<int> { target } : add.Concat(remove).ToList();
                var profiles = await db.Profiles.AsNoTracking().Where(p => userIds.Contains(p.UserId)).ToListAsync();
                var addedUsers = kind is "user-entities" or "user-teams" ? (add.Count > 0 ? userIds : new List<int>()) : add;
                var bound = ShellScope.BoundEntityIds(db, sc, me);
                var selfId = await Caller.UserIdAsync(db, me);
                foreach (var profile in profiles)
                {
                    if (role != Roles.SuperAdmin)
                    {
                        if (AdminEndpoints.IsPrivilegedRole(profile.Role))
                            return Results.Json(new { detail = "Admins can only manage agent-level users." }, statusCode: 403);
                        if (profile.TenantId != TenantOf(me))
                            return Results.Json(new { detail = "That user is outside your tenant." }, statusCode: 403);
                    }
                    if (kind is "user-entities" or "entity-users" && addedUsers.Contains(profile.UserId)
                        && AdminEndpoints.IsPrivilegedRole(profile.Role))
                        return Results.Json(new { detail = "Entity access applies to agent-level users (agent and custom roles) only." },
                            statusCode: 400);
                    if (kind is not ("user-entities" or "entity-users")) continue;
                    // nobody but a superadmin changes their OWN entity access
                    if (role != Roles.SuperAdmin && profile.UserId == selfId)
                        return Results.Json(new { detail = "You can't change your own entity access." }, statusCode: 403);
                    // a manager bound to entities (a company admin, a bound custom role) changes
                    // the access of users bound inside their entities only — never a tenant-wide
                    // user (as /api/v1/users)
                    if (bound != null)
                    {
                        var theirs = await db.Profiles.AsNoTracking().Where(p => p.Id == profile.Id)
                            .SelectMany(p => p.Entities.Select(e => e.Id)).ToListAsync();
                        if (theirs.Count == 0 || theirs.Any(e => !bound.Contains(e)))
                            return Results.Json(new { detail = "That user is outside your companies." }, statusCode: 403);
                    }
                }
                // a team's members: the team must be one the caller may edit (as PATCH /api/teams)
                var teamIds = kind == "team-users" ? new List<int> { target }
                    : kind == "user-teams" ? add.Concat(remove).ToList() : new List<int>();
                foreach (var teamEntity in await db.Teams.AsNoTracking().Where(t => teamIds.Contains(t.Id))
                             .Select(t => t.EntityId).ToListAsync())
                    if (ShellScope.CompanyLevelError(db, sc, me, teamEntity) is { } outsideTeam) return outsideTeam;
                if (kind == "user-entities" && profiles.Count == 0)
                    return Results.Json(new { detail = "User not found." }, statusCode: 404);
                // the entities granted must be the caller's own
                var entityIds = kind == "user-entities" ? add : kind == "entity-users" ? new List<int> { target } : new List<int>();
                foreach (var eid in entityIds)
                    if (!await EntityVisibleAsync(db, sc, me, eid))
                        return Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403);
            }
            // ---- contact bindings: only contacts (and entities) in the caller's scope
            if (kind is "contact-entities" or "entity-contacts")
            {
                var contactIds = kind == "contact-entities" ? new List<int> { target } : add.Concat(remove).ToList();
                var inScope = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                    .Where(c => contactIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
                if (contactIds.Except(inScope).Any())
                    return Results.Json(new { detail = "Contact not found." }, statusCode: 404);
                var entityIds = kind == "contact-entities" ? add.Concat(remove).ToList() : new List<int> { target };
                foreach (var eid in entityIds.Distinct())
                    if (!await EntityVisibleAsync(db, sc, me, eid))
                        return Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403);
            }
            // ---- tags: only contacts / entities in the caller's scope
            if (kind == "tag-contacts")
            {
                var contactIds = add.Concat(remove).Distinct().ToList();
                var inScope = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                    .Where(c => contactIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
                if (contactIds.Except(inScope).Any())
                    return Results.Json(new { detail = "Contact not found." }, statusCode: 404);
            }
            if (kind == "tag-entities")
                foreach (var eid in add.Concat(remove).Distinct())
                    if (!await EntityVisibleAsync(db, sc, me, eid))
                        return Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403);
            // ---- channel accounts: the target entity and every account moved must be the
            // caller's (someone bound to entities never moves another company's number, and
            // only a company admin's old reach touches an organization-wide one)
            if (kind == "entity-accounts")
            {
                if (!await EntityVisibleAsync(db, sc, me, target))
                    return Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403);
                var moved = add.Concat(remove).Distinct().ToList();
                foreach (var accountEntity in await db.ChannelAccounts.AsNoTracking().Where(a => moved.Contains(a.Id))
                             .Select(a => a.EntityId).ToListAsync())
                    if (ShellScope.ObjectScopeError(db, sc, me, accountEntity) is { } outsideAccount) return outsideAccount;
                // unbinding returns a number to organization-wide
                if (remove.Count > 0 && ShellScope.OrgWideActionError(db, me) is { } orgWide) return orgWide;
            }

            if (def.Target == "tag")
            {
                var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == target);
                var wanted = kind == "tag-contacts" ? TagService.ContactTarget : TagService.EntityTarget;
                if (tag == null)
                    return Results.Json(new { detail = "Tag not found." }, statusCode: 404);
                if (tag.AppliesTo != wanted)
                    return Results.Json(new { detail = $"'{tag.Name}' is a {tag.AppliesTo} tag, not a {wanted} tag." },
                                        statusCode: 400);
            }

            var (added, removed) = await ApplyAsync(db, sc, kind, target, add, remove,
                                                    me.Identity?.Name ?? "system");
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
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // a first cut for the route table; the kind's own capability is checked above
          .RequireAnyCapability("users.manage", "contacts.manage", "tags.manage", "channels.manage");
    }

    private static int? TenantOf(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;

    /// <summary>An entity the caller may bind to: superadmin any; an admin their
    /// tenant's; an entity-bound user their assigned subtrees.</summary>
    private static async Task<bool> EntityVisibleAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int entityId)
    {
        var ent = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entityId);
        if (ent == null) return false;
        var role = ShellScope.RoleOf(me);
        if (role == Roles.SuperAdmin) return true;
        if (ent.TenantId != TenantOf(me)) return false;
        return ShellScope.BoundEntityIds(db, sc, me) is not List<int> bound || bound.Contains(entityId);
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

        "tag-contacts" => await db.TagAssignments.AsNoTracking()
            .Where(a => a.TagId == target && a.TargetType == TagService.ContactTarget)
            .Select(a => a.TargetId).ToListAsync(),
        "tag-entities" => await db.TagAssignments.AsNoTracking()
            .Where(a => a.TagId == target && a.TargetType == TagService.EntityTarget)
            .Select(a => a.TargetId).ToListAsync(),

        "entity-accounts" => await db.ChannelAccounts.AsNoTracking()
            .Where(a => a.EntityId == target).Select(a => a.Id).ToListAsync(),

        _ => new List<int>(),
    };

    /// <summary>
    /// Apply the diff. Adding something already bound (or removing something
    /// that is not) is a NO-OP rather than an error — two operators working the
    /// same list must not collide.
    /// </summary>
    private static async Task<(int added, int removed)> ApplyAsync(AppDbContext db, ScopingService sc,
        string kind, int target, List<int> add, List<int> remove, string actor)
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
                var addEntityIds = toAdd.Select(entityOf).Distinct().ToList();
                var addContactIds = toAdd.Select(contactOf).Distinct().ToList();
                var validEntities = await db.Entities.Where(e => addEntityIds.Contains(e.Id))
                    .Select(e => e.Id).ToListAsync();
                var validContacts = await db.Contacts.Where(c => addContactIds.Contains(c.Id))
                    .Select(c => c.Id).ToListAsync();
                toAdd = toAdd.Where(id => validEntities.Contains(entityOf(id)) && validContacts.Contains(contactOf(id)))
                    .ToList();
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
                await db.SaveChangesAsync();
                // A contact's entity links decide where its conversations are
                // filed when the receiving number has no entity of its own.
                var touched = toAdd.Concat(toRemove).Select(contactOf).Distinct().ToList();
                foreach (var thread in await db.Threads.Include(x => x.Contact).Include(x => x.Account)
                             .Where(x => x.ContactId != null && touched.Contains(x.ContactId.Value)).ToListAsync())
                    sc.SyncThreadEntity(thread, save: false);
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

            case "tag-contacts":
            case "tag-entities":
            {
                // Through TagService so group exclusivity and the Contact.Tags
                // cache hold no matter which screen did the binding.
                var targetType = kind == "tag-contacts" ? TagService.ContactTarget : TagService.EntityTarget;
                var tag = await db.Tags.FirstAsync(x => x.Id == target);
                var valid = targetType == TagService.ContactTarget
                    ? await db.Contacts.Where(c => toAdd.Contains(c.Id)).Select(c => c.Id).ToListAsync()
                    : await db.Entities.Where(e => toAdd.Contains(e.Id)).Select(e => e.Id).ToListAsync();
                toAdd = toAdd.Where(valid.Contains).ToList();
                foreach (var id in toAdd)
                    TagService.Assign(db, tag, targetType, id, new Dictionary<string, object?>(), actor);
                TagService.Unassign(db, tag.Id, targetType, toRemove);
                break;
            }

            case "entity-accounts":
            {
                // An account carries ONE entity: binding moves it here, unbinding
                // returns it to organization-wide.
                var ids = toAdd.Concat(toRemove).ToList();
                var accounts = await db.ChannelAccounts.Where(a => ids.Contains(a.Id)).ToListAsync();
                toAdd = toAdd.Where(id => accounts.Any(a => a.Id == id)).ToList();
                foreach (var a in accounts)
                    a.EntityId = toAdd.Contains(a.Id) ? target : null;
                await db.SaveChangesAsync();
                // Re-file the numbers' existing conversations under their new scope.
                var moved = accounts.Select(a => a.Id).ToList();
                foreach (var thread in await db.Threads.Include(x => x.Contact).Include(x => x.Account)
                             .Where(x => x.AccountId != null && moved.Contains(x.AccountId.Value)).ToListAsync())
                    sc.SyncThreadEntity(thread, save: false);
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
