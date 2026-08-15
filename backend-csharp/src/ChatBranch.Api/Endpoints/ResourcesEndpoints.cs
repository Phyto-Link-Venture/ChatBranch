using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Resource (service-provider) + binding REST API — port of
/// <c>crm/resource_views.py</c>, rendered by Settings → Service Providers
/// (<c>components/settings/ProvidersSettings.tsx</c>).
///
/// A Resource is the delivery side of the business: the teacher / coach /
/// practitioner a customer books. A ResourceLink is the binding that says which
/// customers belong to each — the AI reads a bound provider's availability
/// reference (a Sheet id / calendar id off <c>config</c>) when that customer
/// asks to book or reschedule.
///
/// WIRE FORMAT IS snake_case (the Django payloads), built inline here rather
/// than through the camelCase <see cref="Dtos"/> mappers.
///
/// Auth mirrors Django: any authenticated role reads (an agent needs to see
/// which provider a customer has); admin+ mutates. Rows are tenant-scoped —
/// superadmin sees all, everyone else sees their own tenant's providers plus
/// tenant-less legacy rows.
/// </summary>
public static class ResourcesEndpoints
{
    public static void MapResourcesEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- providers ---------------------------------------------------------
        app.MapGet("/api/resources", async (string? active, AppDbContext db, ClaimsPrincipal me) =>
        {
            var q = Scope(db.Resources.AsNoTracking(), me);
            if (active == "true") q = q.Where(r => r.Active);
            var rows = await q.OrderBy(r => r.Name).ToListAsync();
            var counts = await LinkCountsAsync(db, rows.Select(r => r.Id).ToList());
            return Results.Ok(new { resources = rows.Select(r => Payload(r, Count(counts, r.Id))) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/resources", async (JsonElement body, AppDbContext db,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            var name = (AsString(body, "name") ?? "").Trim();
            if (name.Length == 0) return Bad("name is required.");

            var row = new Resource
            {
                Name = Clip(name, 120),
                // A provider is created inside the creator's own tenant; a
                // superadmin without one creates a tenant-less (shared) row.
                TenantId = TenantId(me),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            Apply(row, body);
            db.Resources.Add(row);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "resource.create", row.Name, "");
            return Results.Ok(Payload(row, 0));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapGet("/api/resources/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await Scope(db.Resources.AsNoTracking(), me).FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return NotFound("Provider not found.");
            var links = await LinksAsync(db, id);
            return Results.Ok(Payload(row, links.Count, links));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapMethods("/api/resources/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, AppDbContext db, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var row = await Scope(db.Resources, me).FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return NotFound("Provider not found.");
            Apply(row, body);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "resource.update", row.Name, "");
            var links = await LinksAsync(db, id);
            return Results.Ok(Payload(row, links.Count, links));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapDelete("/api/resources/{id:int}", async (int id, AppDbContext db,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            var row = await Scope(db.Resources, me).FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return NotFound("Provider not found.");
            var name = row.Name;
            // The bindings go with the provider (Django's cascade); EF only
            // cascades what it has loaded, so clear them explicitly.
            db.ResourceLinks.RemoveRange(db.ResourceLinks.Where(l => l.ResourceId == id));
            db.Resources.Remove(row);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "resource.delete", name, "");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- bindings ----------------------------------------------------------
        app.MapGet("/api/resources/{id:int}/links", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await Scope(db.Resources.AsNoTracking(), me).FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return NotFound("Provider not found.");
            return Results.Ok(new { links = await LinksAsync(db, id) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/resources/{id:int}/links", async (int id, JsonElement body,
            AppDbContext db, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var row = await Scope(db.Resources, me).FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return NotFound("Provider not found.");

            var contactId = AsInt(body, "contact_id");
            var contact = contactId is int cid
                ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid)
                : null;
            if (contact == null) return Bad("contact_id must reference an existing contact.");

            // resources.link — idempotent on the (resource, contact) pair.
            var link = await db.ResourceLinks
                .FirstOrDefaultAsync(l => l.ResourceId == id && l.ContactId == contact.Id);
            if (link == null)
            {
                link = new ResourceLink
                {
                    ResourceId = id, ContactId = contact.Id, CreatedAt = DateTime.UtcNow,
                };
                db.ResourceLinks.Add(link);
            }
            var relationship = (AsString(body, "relationship") ?? "").Trim();
            if (relationship.Length > 0) link.Relationship = Clip(relationship, 32);
            var since = AsString(body, "since");
            if (!string.IsNullOrWhiteSpace(since) &&
                DateOnly.TryParse(since, out var parsed)) link.Since = parsed;
            // Django: active=body.get("active", True) is not False — anything but
            // an explicit false means active.
            link.Active = AsBool(body, "active") != false;
            link.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            Audit(db, me, ctx, "resource.link", $"{row.Name} - {contact.Name}", "");
            return Results.Ok(LinkPayload(link, contact.Name));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapDelete("/api/resources/{id:int}/links/{linkId:int}", async (int id, int linkId,
            AppDbContext db, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var row = await Scope(db.Resources.AsNoTracking(), me).FirstOrDefaultAsync(r => r.Id == id);
            if (row == null) return NotFound("Provider not found.");
            var link = await db.ResourceLinks.Include(l => l.Contact)
                .FirstOrDefaultAsync(l => l.Id == linkId && l.ResourceId == id);
            if (link == null) return NotFound("Binding not found.");
            var contactName = link.Contact?.Name ?? "?";
            db.ResourceLinks.Remove(link);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "resource.unlink", $"{row.Name} - {contactName}", "");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    // ======================= payloads (snake_case) ===========================

    private static object Payload(Resource row, int linksCount, List<object>? links = null)
    {
        var outv = new Dictionary<string, object?>
        {
            ["id"] = row.Id,
            ["name"] = row.Name,
            ["external_id"] = row.ExternalId,
            ["email"] = row.Email,
            ["phone"] = row.Phone,
            ["active"] = row.Active,
            ["config"] = row.Config,
            ["attributes"] = row.Attributes,
            ["tenant_id"] = row.TenantId,
            ["links_count"] = linksCount,
            ["created_at"] = Iso(row.CreatedAt),
            ["updated_at"] = Iso(row.UpdatedAt),
        };
        if (links != null) outv["links"] = links;
        return outv;
    }

    private static object LinkPayload(ResourceLink link, string contactName) => new
    {
        id = link.Id,
        contact_id = link.ContactId,
        contact_name = contactName,
        relationship = link.Relationship,
        active = link.Active,
        since = link.Since?.ToString("yyyy-MM-dd"),
        created_at = Iso(link.CreatedAt),
    };

    private static async Task<List<object>> LinksAsync(AppDbContext db, int resourceId) =>
        (await db.ResourceLinks.AsNoTracking().Include(l => l.Contact)
            .Where(l => l.ResourceId == resourceId).OrderBy(l => l.Id).ToListAsync())
        .Select(l => LinkPayload(l, l.Contact?.Name ?? "")).ToList();

    private static async Task<Dictionary<int, int>> LinkCountsAsync(AppDbContext db, List<int> ids)
    {
        if (ids.Count == 0) return new Dictionary<int, int>();
        return await db.ResourceLinks.AsNoTracking().Where(l => ids.Contains(l.ResourceId))
            .GroupBy(l => l.ResourceId)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
    }

    private static int Count(Dictionary<int, int> counts, int id) =>
        counts.TryGetValue(id, out var n) ? n : 0;

    // ======================= write path ======================================

    /// <summary>
    /// resource_views._apply — a PATCH touches only the keys actually present,
    /// and only when they carry the right JSON type (a null/number "name" is
    /// ignored, exactly as Django's isinstance() checks do).
    /// </summary>
    private static void Apply(Resource row, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return;

        if (TryString(body, "name", out var name) && name.Trim().Length > 0)
            row.Name = Clip(name.Trim(), 120);
        if (TryString(body, "external_id", out var ext)) row.ExternalId = Clip(ext.Trim(), 64);
        if (TryString(body, "email", out var email)) row.Email = email.Trim();
        if (TryString(body, "phone", out var phone)) row.Phone = Clip(phone.Trim(), 32);
        if (body.TryGetProperty("active", out var act) &&
            act.ValueKind is JsonValueKind.True or JsonValueKind.False)
            row.Active = act.GetBoolean();
        if (body.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
            row.Config = ToDict(cfg);
        if (body.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            row.Attributes = ToDict(attrs);
    }

    // ======================= scoping + helpers ===============================

    /// <summary>
    /// Tenant-scope a Resource queryset: superadmin sees all; everyone else sees
    /// their tenant's rows plus tenant-less legacy rows (resource_views._scope).
    /// </summary>
    private static IQueryable<Resource> Scope(IQueryable<Resource> qs, ClaimsPrincipal me)
    {
        if ((me.FindFirstValue(ClaimTypes.Role) ?? "") == Roles.SuperAdmin) return qs;
        if (TenantId(me) is not int tid) return qs;
        return qs.Where(r => r.TenantId == tid || r.TenantId == null);
    }

    private static int? TenantId(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;

    private static Dictionary<string, object?> ToDict(JsonElement obj) =>
        obj.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static string? Iso(DateTime dt) =>
        dt == default ? null : dt.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);
    private static IResult NotFound(string detail) => Results.Json(new { detail }, statusCode: 404);

    private static bool TryString(JsonElement body, string key, out string value)
    {
        value = "";
        if (body.ValueKind != JsonValueKind.Object) return false;
        if (!body.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return true;
    }

    private static string? AsString(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => el.ToString(),
        };
    }

    private static int? AsInt(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString()?.Trim(), out var s)) return s;
        return null;
    }

    private static bool? AsBool(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static void Audit(AppDbContext db, ClaimsPrincipal me, HttpContext ctx,
        string action, string target, string detail)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system",
                Action = action,
                Target = target,
                Detail = detail,
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* an audit failure must never fail the operation itself */ }
    }
}
