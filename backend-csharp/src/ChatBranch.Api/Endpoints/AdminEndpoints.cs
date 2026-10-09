using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The admin/RBAC surface: the data-driven capability matrix
/// (<c>/api/auth/permissions</c>), the role list (<c>/api/auth/roles</c>), and
/// the v1 management reads the Users &amp; Roles / Entities &amp; Tenants pages
/// consume (<c>/api/v1/users</c>, <c>/api/v1/entities</c>).
///
/// Ported from Django <c>crm/permissions_store.py</c> + <c>crm/user_service.py</c>
/// + <c>api/views.py</c>. Three rules carry over verbatim:
///
/// * <b>superadmin is always allowed, in code</b> — never derivable from a DB
///   row, so the matrix can never lock the platform out.
/// * <b>Missing row → the code default matrix</b>, so an unseeded DB behaves
///   exactly like a seeded one.
/// * <b>Capabilities gate the ACTION; tenancy/ownership gates the ROWS.</b> The
///   admin→agent tiering in the user list sits on top of the capability check,
///   never instead of it.
/// </summary>
public static class AdminEndpoints
{
    /// Role create/rename body (`{label}`); the slug is always derived, never
    /// operator-supplied — it's the identity Profile.role/RolePermission point at.
    public record RoleWrite(string? Label);

    // ---- capability catalog (crm/permissions_store.py CAPABILITIES) --------
    // The frontend renders the permission matrix from exactly this list; keep
    // it in step with `CAPABILITY_CATALOG` in frontend/src/lib/auth.ts.
    private static readonly (string Capability, string Area, string Label)[] Catalog =
    {
        ("contacts.view", "Contacts", "View contacts"),
        ("contacts.manage", "Contacts", "Create & edit contacts"),
        ("chats.view", "Chats", "View chats"),
        ("chats.send", "Chats", "Send messages"),
        ("chats.manage", "Chats", "Manage chats"),
        ("flows.view", "Flows", "View flows"),
        ("flows.manage", "Flows", "Build & edit flows"),
        ("channels.view", "Channels", "View channels"),
        ("channels.manage", "Channels", "Manage channel accounts"),
        ("integrations.view", "Integrations", "View integrations"),
        ("integrations.manage", "Integrations", "Manage integrations"),
        ("users.view", "Users & Roles", "View users"),
        ("users.manage", "Users & Roles", "Create & manage users"),
        ("entities.view", "Entities & Tenants", "View entities & tenants"),
        ("entities.manage", "Entities & Tenants", "Manage entities & tenants"),
        ("templates.manage", "Library", "Manage message templates"),
        ("script_templates.manage", "Library", "Manage script templates"),
        ("notifications.manage", "Settings", "Manage organization notification defaults"),
        ("analytics.view", "Review", "View analytics"),
        ("call_logs.view", "Review", "View call logs"),
        ("activity.view", "Review", "View activity log"),
        ("settings.ai.manage", "Settings", "Manage AI configuration"),
        ("account_assignment.manage", "Settings", "Manage account assignment"),
        ("app_keys.manage", "Settings", "Manage app & provider keys"),
        ("pronunciation.manage", "Settings", "Manage pronunciation"),
        // Settings sub-pages — one capability each (Personal pages need none).
        ("settings.appearance.org", "Settings", "Set the organization default appearance"),
        ("settings.custom_fields.manage", "Settings", "Manage custom fields"),
        ("settings.ai_keys.manage", "Settings", "Manage AI keys & models"),
        ("rbac.manage", "Admin", "Edit the permission matrix"),
        ("tags.view", "Library", "View tags"),
        ("tags.manage", "Library", "Create & edit tags"),
        ("api_keys.manage", "Admin", "Manage API keys & MCP"),
        ("ai.monitor", "AI", "See & approve AI drafts"),
        ("ai.behaviour.manage", "AI", "Edit AI behaviour settings"),
        ("billing.view", "Review", "View the AI usage bill"),
        ("billing.manage", "Settings", "Set AI billing markup & see costs"),
    };

    private static readonly string[] CapabilityIds =
        Catalog.Select(c => c.Capability).ToArray();

    private static readonly HashSet<string> CapabilitySet = new(CapabilityIds);

    /// The 3 built-in roles, in canonical display order. Custom roles are
    /// additive and always sort after these.
    private static readonly string[] BuiltinRoles =
        { Roles.SuperAdmin, Roles.Admin, Roles.Agent };

    private static readonly Dictionary<string, string> SystemRoleLabels = new()
    {
        [Roles.SuperAdmin] = "Super admin",
        [Roles.Admin] = "Admin",
        [Roles.Agent] = "Agent",
    };

    /// admin gets everything EXCEPT these (kept superadmin-only).
    private static readonly HashSet<string> AdminExclude = new()
        { "rbac.manage", "app_keys.manage", "settings.ai_keys.manage", "billing.manage" };

    /// agent's read-mostly work surface.
    private static readonly HashSet<string> AgentAllow = new()
    {
        "contacts.view", "chats.view", "chats.send", "flows.view",
        "analytics.view", "call_logs.view", "activity.view",
        "tags.view", "ai.monitor",
    };

    /// rbac.manage for superadmin is never toggleable off (anti-lockout).
    private const string RbacManage = "rbac.manage";

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- GET /api/auth/roles ------------------------------------------
        // {roles: [{slug, label, is_system, user_count}]}, system built-ins
        // first. Feeds the Users page role picker and the Permissions page's
        // role cards.
        app.MapGet("/api/auth/roles", async (AppDbContext db) =>
        {
            var rows = await LoadRolesAsync(db);
            var counts = await RoleUserCountsAsync(db);
            var payload = OrderedRoleSlugs(rows).Select(slug =>
            {
                var row = rows.FirstOrDefault(r => r.Slug == slug);
                return new
                {
                    slug,
                    label = row?.Label ?? SystemRoleLabels.GetValueOrDefault(slug, slug),
                    is_system = row?.IsSystem ?? BuiltinRoles.Contains(slug),
                    user_count = counts.GetValueOrDefault(slug, 0),
                };
            });
            return Results.Ok(new { roles = payload });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- POST /api/auth/roles -----------------------------------------
        // {label} → a new CUSTOM role. The label is slugified into the role's
        // permanent identity, and a full set of all-false RolePermission rows is
        // seeded so a fresh role grants NOTHING until an operator saves the
        // matrix (crm/permissions_store.py create_role: "no auto-pass").
        //
        // Gate is `rbac.manage`, not a role literal — same as PUT
        // /api/auth/permissions. Under the default matrix that's superadmin
        // only, but a custom role granted rbac.manage passes too.
        app.MapPost("/api/auth/roles", async (RoleWrite? body,
            ClaimsPrincipal me, AppDbContext db) =>
        {
            var actor = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await HasCapabilityAsync(db, actor, RbacManage))
                return RbacDenied();

            var label = (body?.Label ?? "").Trim();
            if (label.Length == 0) return Bad("A role name is required.");
            var slug = Slugify(label);
            if (slug.Length == 0) return Bad("Could not derive a role slug from that name.");
            if (BuiltinRoles.Contains(slug))
                return Conflict($"'{slug}' collides with a built-in role.");
            var known = await LoadRolesAsync(db);
            if (known.Any(r => r.Slug == slug))
                return Conflict($"A role '{slug}' already exists.");

            var row = new Role
            {
                Slug = slug, Label = label, IsSystem = false, CreatedAt = DateTime.UtcNow,
            };
            try
            {
                db.Roles.Add(row);
                foreach (var cap in CapabilityIds)
                    db.RolePermissions.Add(new RolePermission
                    {
                        Role = slug, Capability = cap, Allowed = false,
                        UpdatedAt = DateTime.UtcNow,
                    });
                db.AuditLogs.Add(new AuditLog
                {
                    Actor = me.Identity?.Name ?? "system", Action = "role.create",
                    Target = slug, Detail = $"label: {label}", CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception)
            {
                // Unseeded/unmigrated sandbox DB — report the refusal rather
                // than 500-ing the Permissions page.
                return Bad("Could not create the role — the role store is unavailable.");
            }
            // Django answers {role:{…}}; the slug is echoed at the top level too
            // so the Permissions page can jump straight into the new role's card
            // grid (app/permissions/page.tsx reads body.slug).
            var payload = new
            {
                slug, label, is_system = false, user_count = 0,
            };
            return Results.Json(new { role = payload, payload.slug }, statusCode: 201);
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- PATCH /api/auth/roles/{slug} ---------------------------------
        // {label} renames a CUSTOM role's display label. The slug is IMMUTABLE
        // (Profile.role and RolePermission.role reference it), so only the
        // human-facing label moves. Built-ins are protected.
        app.MapMethods("/api/auth/roles/{slug}", new[] { "PATCH" }, async (string slug,
            RoleWrite? body, ClaimsPrincipal me, AppDbContext db) =>
        {
            var actor = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await HasCapabilityAsync(db, actor, RbacManage))
                return RbacDenied();

            var known = await LoadRolesAsync(db);
            var role = known.FirstOrDefault(r => r.Slug == slug);
            if (role == null) return NotFound("No such role.");
            if (role.IsSystem || BuiltinRoles.Contains(role.Slug))
                return Bad("Built-in roles cannot be renamed.");

            var label = (body?.Label ?? "").Trim();
            if (label.Length == 0) return Bad("A role name is required.");
            if (known.Any(r => r.Id != role.Id &&
                    string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase)))
                return Conflict($"A role named '{label}' already exists.");
            var newSlug = Slugify(label);
            if (newSlug.Length > 0 && newSlug != role.Slug &&
                known.Any(r => r.Id != role.Id && r.Slug == newSlug))
                return Conflict($"A role '{newSlug}' already exists.");

            try
            {
                var tracked = await db.Roles.FirstAsync(r => r.Id == role.Id);
                tracked.Label = label;
                db.AuditLogs.Add(new AuditLog
                {
                    Actor = me.Identity?.Name ?? "system", Action = "role.update",
                    Target = role.Slug, Detail = $"label: {label}", CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception)
            {
                return Bad("Could not rename the role — the role store is unavailable.");
            }

            var counts = await RoleUserCountsAsync(db);
            return Results.Ok(new
            {
                role = new
                {
                    slug = role.Slug, label, is_system = false,
                    user_count = counts.GetValueOrDefault(role.Slug, 0),
                },
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- DELETE /api/auth/roles/{slug} --------------------------------
        // Deletes a CUSTOM role and its matrix rows. Built-ins are protected,
        // and a role any user still holds is a 409 (+ user_count, which the
        // Permissions page turns into "reassign them first") — never a silent
        // orphaning of Profile.role.
        app.MapDelete("/api/auth/roles/{slug}", async (string slug,
            ClaimsPrincipal me, AppDbContext db) =>
        {
            var actor = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await HasCapabilityAsync(db, actor, RbacManage))
                return RbacDenied();

            var known = await LoadRolesAsync(db);
            var role = known.FirstOrDefault(r => r.Slug == slug);
            if (role == null) return NotFound("No such role.");
            if (role.IsSystem || BuiltinRoles.Contains(role.Slug))
                return Bad("Built-in roles cannot be deleted.");

            var counts = await RoleUserCountsAsync(db);
            var inUse = counts.GetValueOrDefault(role.Slug, 0);
            if (inUse > 0)
                return Results.Json(new
                {
                    detail = $"{inUse} user(s) still hold this role — reassign them first.",
                    user_count = inUse,
                }, statusCode: 409);

            try
            {
                await db.RolePermissions.Where(r => r.Role == slug).ExecuteDeleteAsync();
                await db.Roles.Where(r => r.Id == role.Id).ExecuteDeleteAsync();
                db.AuditLogs.Add(new AuditLog
                {
                    Actor = me.Identity?.Name ?? "system", Action = "role.delete",
                    Target = slug, CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception)
            {
                return Bad("Could not delete the role — the role store is unavailable.");
            }
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- GET /api/auth/permissions ------------------------------------
        // {catalog, roles, matrix} — the catalog, the dynamic role list, and
        // the effective matrix (stored rows overlaid on the code defaults).
        app.MapGet("/api/auth/permissions", async (AppDbContext db) =>
            Results.Ok(await SnapshotAsync(db)))
            .RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- PUT /api/auth/permissions ------------------------------------
        // Body {matrix: {role: {capability: bool}}} (a bare matrix object is
        // accepted too, matching Django). Requires the `rbac.manage`
        // capability — superadmin only under the default matrix. Returns the
        // fresh snapshot so the page can re-render from server truth.
        app.MapPut("/api/auth/permissions", async (JsonElement body,
            ClaimsPrincipal me, AppDbContext db) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await HasCapabilityAsync(db, role, RbacManage))
                return Results.Json(new
                {
                    detail = "The rbac.manage capability is required to edit permissions.",
                }, statusCode: 403);

            var matrix = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("matrix", out var m)
                && m.ValueKind == JsonValueKind.Object ? m : body;
            if (matrix.ValueKind != JsonValueKind.Object)
                return Results.Json(new
                {
                    detail = "matrix must be an object of {role: {capability: bool}}.",
                }, statusCode: 400);

            // Any existing role (built-in or custom) is a valid target — the
            // matrix is dynamic. Built-ins are valid even without a Role row.
            var known = await LoadRolesAsync(db);
            var validRoles = new HashSet<string>(known.Select(r => r.Slug));
            foreach (var b in BuiltinRoles) validRoles.Add(b);

            // Validate the WHOLE body before writing anything — a rejected PUT
            // must leave the stored matrix untouched.
            var changes = new List<(string Role, string Capability, bool Allowed)>();
            foreach (var roleProp in matrix.EnumerateObject())
            {
                if (!validRoles.Contains(roleProp.Name))
                    return Bad($"Unknown role '{roleProp.Name}'.");
                if (roleProp.Value.ValueKind != JsonValueKind.Object)
                    return Bad($"matrix['{roleProp.Name}'] must be an object.");
                foreach (var capProp in roleProp.Value.EnumerateObject())
                {
                    if (!CapabilitySet.Contains(capProp.Name))
                        return Bad($"Unknown capability '{capProp.Name}'.");
                    if (capProp.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Bad($"{roleProp.Name}.{capProp.Name} must be true or false.");
                    var allowed = capProp.Value.ValueKind == JsonValueKind.True;
                    if (roleProp.Name == Roles.SuperAdmin && capProp.Name == RbacManage && !allowed)
                        return Bad("Superadmin's rbac.manage cannot be turned off.");
                    changes.Add((roleProp.Name, capProp.Name, allowed));
                }
            }

            if (changes.Count > 0)
            {
                var stored = await db.RolePermissions.ToListAsync();
                foreach (var (r, cap, allowed) in changes)
                {
                    var row = stored.FirstOrDefault(x => x.Role == r && x.Capability == cap);
                    if (row == null)
                    {
                        row = new RolePermission { Role = r, Capability = cap };
                        db.RolePermissions.Add(row);
                        stored.Add(row);
                    }
                    row.Allowed = allowed;
                    row.UpdatedAt = DateTime.UtcNow;
                }
                db.AuditLogs.Add(new AuditLog
                {
                    Actor = me.Identity?.Name ?? "system",
                    Action = "rbac.update",
                    Target = "permissions",
                    Detail = $"{changes.Count} capability toggle(s)",
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            return Results.Ok(await SnapshotAsync(db));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- GET /api/v1/entities -----------------------------------------
        // {entities: [{id, name, slug, tenant, is_active, created_at}]} — the
        // Entities & Tenants page + the Users page's entity-access picker.
        // Scope (api/rbac.py accessible_entities): superadmin all tenants,
        // admin their own tenant, agent only their assigned entities.
        app.MapGet("/api/v1/entities", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var rows = await AccessibleEntitiesAsync(db, me);
            return Results.Ok(new
            {
                entities = rows.Select(e => new
                {
                    id = e.Id,
                    name = e.Name,
                    slug = e.Slug,
                    tenant = e.Tenant?.Slug ?? "",
                    // Hierarchy: null parent = a top-level entity. Children
                    // inherit their parent's tenant, so the UI only asks for a
                    // parent (the tenant is never typed in by hand).
                    parent_id = e.ParentId,
                    parent = e.Parent?.Slug,
                    parent_name = e.Parent?.Name ?? "",
                    // ingest_entity has no is_active column in this port yet —
                    // every entity reads as live (the page renders a "paused"
                    // pill off this flag).
                    is_active = true,
                    created_at = e.CreatedAt,
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- GET /api/v1/users --------------------------------------------
        // {users: [managed_user_payload]} — the Users & Roles table. Gated on
        // the `users.manage` capability (data-driven, like Django's
        // HasCapability.for_("users.manage")); the tiering below then bounds
        // the ROWS: superadmin everyone, admin their own tenant, anyone else
        // just themselves.
        app.MapGet("/api/v1/users", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await HasCapabilityAsync(db, role, "users.manage"))
                return Results.Json(new
                {
                    error = new { code = "forbidden", message = "Admin role required to manage users." },
                }, statusCode: 403);

            var username = me.Identity?.Name ?? "";
            var tenantId = TenantId(me);
            var q = db.Users.AsNoTracking()
                .Include(u => u.Profile!).ThenInclude(p => p.Tenant)
                .Include(u => u.Profile!).ThenInclude(p => p.Entities)
                .Include(u => u.Profile!).ThenInclude(p => p.Contact)
                .AsQueryable();
            if (role == Roles.Admin)
                q = q.Where(u => u.Profile != null && u.Profile.TenantId == tenantId);
            else if (role != Roles.SuperAdmin)
                q = q.Where(u => u.Username == username);
            var rows = await q.OrderBy(u => u.Username).ToListAsync();
            return Results.Ok(new { users = rows.Select(ManagedUserPayload) });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // ---- payloads ---------------------------------------------------------

    /// crm/user_service.py managed_user_payload. `name` falls back to the
    /// username (auth_user's first_name isn't mapped in this port) and
    /// `created` is null for the same reason — the Users table shows neither.
    internal static object ManagedUserPayload(AppUser u) => new
    {
        id = u.Id,
        username = u.Username,
        name = u.Username,
        role = RoleOf(u),
        active = u.IsActive,
        created = (DateTime?)null,
        last_login = u.LastLogin,
        tenant = u.Profile?.Tenant?.Slug,
        entities = (u.Profile?.Entities ?? new List<Entity>())
            .Select(e => new { id = e.Id, name = e.Name, slug = e.Slug }),
        // Customer login: the Contact this user signs in as (integrations are
        // connected per registered user, against their own contact record).
        contact_id = u.Profile?.ContactId,
        contact_name = u.Profile?.Contact?.Name,
    };

    /// A Django superuser with no Profile row still counts as superadmin.
    internal static string RoleOf(AppUser u) =>
        u.Profile?.Role is { Length: > 0 } r ? r
            : u.IsSuperuser ? Roles.SuperAdmin : Roles.Admin;

    private static async Task<object> SnapshotAsync(AppDbContext db)
    {
        var roles = await LoadRolesAsync(db);
        var slugs = OrderedRoleSlugs(roles);
        return new
        {
            catalog = Catalog.Select(c => new
            {
                capability = c.Capability, area = c.Area, label = c.Label,
            }),
            roles = slugs,
            matrix = await EffectiveMatrixAsync(db, slugs),
        };
    }

    // ---- the matrix -------------------------------------------------------

    /// Stored row if present, else the code default (custom roles have no code
    /// default, so every capability is false until granted). superadmin's
    /// rbac.manage is always reported on (locked).
    private static async Task<Dictionary<string, Dictionary<string, bool>>>
        EffectiveMatrixAsync(AppDbContext db, IReadOnlyList<string> slugs)
    {
        var stored = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var row in await StoredPermissionsAsync(db))
        {
            if (!stored.TryGetValue(row.Role, out var caps))
                stored[row.Role] = caps = new Dictionary<string, bool>();
            caps[row.Capability] = row.Allowed;
        }

        var matrix = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var role in slugs)
        {
            var caps = new Dictionary<string, bool>();
            stored.TryGetValue(role, out var rows);
            foreach (var cap in CapabilityIds)
                caps[cap] = rows != null && rows.TryGetValue(cap, out var v)
                    ? v : DefaultAllows(role, cap);
            matrix[role] = caps;
        }
        if (matrix.TryGetValue(Roles.SuperAdmin, out var sa)) sa[RbacManage] = true;
        return matrix;
    }

    /// crm/permissions_store.py capabilities_for — every capability this role
    /// holds, in catalog order. whoami ships it so the frontend gates the UI on
    /// capabilities rather than on the role literal (lib/auth.ts hasCap);
    /// superadmin holds all of them, in code.
    internal static async Task<List<string>> CapabilitiesForAsync(AppDbContext db, string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return new List<string>();
        if (role == Roles.SuperAdmin) return CapabilityIds.ToList();
        var stored = new Dictionary<string, bool>();
        foreach (var row in await StoredPermissionsAsync(db))
            if (row.Role == role) stored[row.Capability] = row.Allowed;
        return CapabilityIds
            .Where(cap => stored.TryGetValue(cap, out var v) ? v : DefaultAllows(role, cap))
            .ToList();
    }

    /// Every assignable role slug: the Role table PLUS the built-ins (valid even
    /// before the Role rows are seeded). crm/user_service.py _valid_roles.
    internal static async Task<HashSet<string>> ValidRoleSlugsAsync(AppDbContext db)
    {
        var slugs = new HashSet<string>(BuiltinRoles);
        foreach (var row in await LoadRolesAsync(db)) slugs.Add(row.Slug);
        return slugs;
    }

    /// admin/superadmin only. agent AND every custom role are agent-level for
    /// the admin→agent tiering (crm/user_service.py PRIVILEGED_ROLES).
    internal static bool IsPrivilegedRole(string? role) =>
        role is Roles.Admin or Roles.SuperAdmin;

    /// The one gate the server-side checks consult. A request on a SCOPED API
    /// key holds only its scopes (superadmin included); then superadmin → always
    /// true; unknown capability → false; a stored row wins, else the code default.
    internal static async Task<bool> HasCapabilityAsync(AppDbContext db, string role, string capability)
    {
        if (string.IsNullOrWhiteSpace(role)) return false;
        if (ApiKeyAuth.CurrentScopes is { } scopes && !scopes.Contains(capability)) return false;
        if (role == Roles.SuperAdmin) return true;
        if (!CapabilitySet.Contains(capability)) return false;
        RolePermission? row = null;
        try
        {
            row = await db.RolePermissions.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Role == role && r.Capability == capability);
        }
        catch (Exception)
        {
            // Unseeded/unmigrated sandbox DB — the code default IS the contract
            // (permissions_store: "missing row → the code default matrix"), so
            // degrade to it rather than 500-ing an admin page.
        }
        return row?.Allowed ?? DefaultAllows(role, capability);
    }

    private static async Task<List<RolePermission>> StoredPermissionsAsync(AppDbContext db)
    {
        try { return await db.RolePermissions.AsNoTracking().ToListAsync(); }
        catch (Exception) { return new List<RolePermission>(); }
    }

    private static bool DefaultAllows(string role, string capability) => role switch
    {
        Roles.SuperAdmin => true,
        Roles.Admin => !AdminExclude.Contains(capability),
        Roles.Agent => AgentAllow.Contains(capability),
        _ => false, // custom roles start fully denied — no auto-pass
    };

    // ---- roles ------------------------------------------------------------

    /// Every Role row. An empty result is fine — OrderedRoleSlugs always yields
    /// the 3 built-ins, so a bare DB still renders the matrix.
    private static async Task<List<Role>> LoadRolesAsync(AppDbContext db)
    {
        try { return await db.Roles.AsNoTracking().ToListAsync(); }
        catch (Exception) { return new List<Role>(); }
    }

    /// Built-ins first (canonical order, always present even if a Role row is
    /// missing), then custom roles by creation order.
    private static List<string> OrderedRoleSlugs(IReadOnlyCollection<Role> rows)
    {
        var ordered = new List<string>(BuiltinRoles);
        ordered.AddRange(rows
            .Where(r => !r.IsSystem && !BuiltinRoles.Contains(r.Slug))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => r.Slug));
        return ordered;
    }

    private static async Task<Dictionary<string, int>> RoleUserCountsAsync(AppDbContext db)
    {
        try
        {
            var rows = await db.Profiles.AsNoTracking()
                .GroupBy(p => p.Role)
                .Select(g => new { Role = g.Key, N = g.Count() })
                .ToListAsync();
            return rows.ToDictionary(x => x.Role, x => x.N);
        }
        catch (Exception) { return new Dictionary<string, int>(); }
    }

    // ---- entity scope (api/rbac.py) ---------------------------------------

    internal static async Task<List<Entity>> AccessibleEntitiesAsync(AppDbContext db, ClaimsPrincipal me)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        var tenantId = TenantId(me);
        var q = db.Entities.AsNoTracking().Include(e => e.Tenant).Include(e => e.Parent).AsQueryable();

        if (role == Roles.SuperAdmin)
        {
            // every tenant
        }
        else if (role == Roles.Admin)
        {
            if (tenantId is not int tid) return new List<Entity>();
            q = q.Where(e => e.TenantId == tid);
        }
        else
        {
            // agent (and every custom role): only the explicitly assigned
            // entities, constrained to their tenant as defense in depth.
            var ids = await AssignedEntityIdsAsync(db, me.Identity?.Name);
            if (ids.Count == 0 || tenantId is not int atid) return new List<Entity>();
            q = q.Where(e => ids.Contains(e.Id) && e.TenantId == atid);
        }
        return await q.OrderBy(e => e.Tenant!.Slug).ThenBy(e => e.Slug).ToListAsync();
    }

    private static async Task<List<int>> AssignedEntityIdsAsync(AppDbContext db, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return new List<int>();
        var userId = await db.Users.AsNoTracking()
            .Where(u => u.Username == username)
            .Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (userId is not int uid) return new List<int>();
        var profile = await db.Profiles.AsNoTracking()
            .Include(p => p.Entities)
            .FirstOrDefaultAsync(p => p.UserId == uid);
        return profile?.Entities.Select(e => e.Id).ToList() ?? new List<int>();
    }

    // ---- helpers ----------------------------------------------------------

    private static IResult Bad(string detail) =>
        Results.Json(new { detail }, statusCode: 400);

    private static IResult Conflict(string detail) =>
        Results.Json(new { detail }, statusCode: 409);

    private static IResult NotFound(string detail) =>
        Results.Json(new { detail }, statusCode: 404);

    private static IResult RbacDenied() => Results.Json(new
    {
        detail = "The rbac.manage capability is required to manage roles.",
    }, statusCode: 403);

    /// Django's slugify, near enough for role identities: lowercase, keep
    /// alphanumerics + underscore, collapse whitespace/dashes into one dash,
    /// trim dashes, cap at 32 chars (crm/permissions_store.py create_role).
    private static string Slugify(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
            else if ((char.IsWhiteSpace(ch) || ch == '-') && sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var slug = sb.ToString();
        if (slug.Length > 32) slug = slug[..32];
        return slug.Trim('-');
    }

    private static int? TenantId(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;
}
