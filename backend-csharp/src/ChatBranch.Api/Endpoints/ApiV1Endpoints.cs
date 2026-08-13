using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The /api/v1 surface (Django DRF twin) + the bulk record-sync pipeline. Uses
/// the same bearer token. "Brands" map to ingest.Entity rows (the outlet model
/// this port standardized on). Tenant scoping comes from the caller's claim.
/// </summary>
public static class ApiV1Endpoints
{
    public record BrandWrite(string Name, string Slug);
    public record SyncContact(string? Phone, string? Name, string? Email);
    public record SyncRecord(string ExternalId, string? Type, string? Title, string? Status,
        decimal? Amount, DateOnly? DueDate, SyncContact? Contact);
    public record SyncRequest(List<SyncRecord> Records);

    public static void MapApiV1Endpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/whoami", (ClaimsPrincipal me) => Results.Ok(new
        {
            username = me.Identity?.Name,
            role = me.FindFirstValue(ClaimTypes.Role),
            tenantId = me.FindFirstValue("tenant_id"),
        })).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- brands (= entities) ----------------------------------------------
        app.MapGet("/api/v1/brands", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var tenantId = TenantId(me);
            var q = db.Entities.AsNoTracking().AsQueryable();
            if (tenantId is int tid) q = q.Where(e => e.TenantId == tid);
            var rows = await q.OrderBy(e => e.Name).ToListAsync();
            return Results.Ok(new { brands = rows.Select(e => new
            {
                id = e.Id, name = e.Name, slug = e.Slug, tenantId = e.TenantId,
            }) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/v1/brands", async (BrandWrite b, ClaimsPrincipal me, AppDbContext db) =>
        {
            var tenantId = TenantId(me);
            if (tenantId is not int tid)
                return Results.Json(new { detail = "no tenant in scope" }, statusCode: 400);
            if (db.Entities.Any(e => e.TenantId == tid && e.Slug == b.Slug))
                return Results.Json(new { detail = "duplicate slug" }, statusCode: 409);
            var row = new Entity { TenantId = tid, Name = b.Name, Slug = b.Slug,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Entities.Add(row);
            await db.SaveChangesAsync();
            return Results.Ok(new { id = row.Id, name = row.Name, slug = row.Slug });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- contacts (brand-scoped) ------------------------------------------
        app.MapGet("/api/v1/contacts", async (string? q, AppDbContext db) =>
        {
            var query = db.Contacts.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(c => c.Name.Contains(q) || c.Phone.Contains(q));
            var rows = await query.OrderByDescending(c => c.UpdatedAt).Take(200).ToListAsync();
            return Results.Ok(new { contacts = rows.Select(Dtos.ContactDto) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- bulk record sync (ingest) ----------------------------------------
        app.MapPost("/api/v1/sync/records", (SyncRequest body, ClaimsPrincipal me, AppDbContext db) =>
        {
            var tenantId = TenantId(me);
            int created = 0, updated = 0;
            foreach (var r in body.Records)
            {
                // phone-first contact match/create
                Contact? contact = null;
                if (!string.IsNullOrWhiteSpace(r.Contact?.Phone))
                {
                    contact = db.Contacts.FirstOrDefault(c => c.Phone == r.Contact!.Phone
                        && (tenantId == null || c.TenantId == tenantId));
                    if (contact == null)
                    {
                        contact = new Contact
                        {
                            Name = r.Contact!.Name ?? r.Contact.Phone!, Phone = r.Contact.Phone!,
                            Email = r.Contact.Email ?? "", TenantId = tenantId,
                            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                        };
                        db.Contacts.Add(contact);
                        db.SaveChanges();
                    }
                }
                if (contact == null) continue; // records need an identity

                var type = r.Type ?? "custom";
                var rec = db.Records.FirstOrDefault(x => x.ExternalId == r.ExternalId
                    && x.Type == type && (tenantId == null || x.TenantId == tenantId));
                if (rec == null)
                {
                    rec = new Record { ExternalId = r.ExternalId, Type = type, ContactId = contact.Id,
                        TenantId = tenantId, CreatedAt = DateTime.UtcNow };
                    db.Records.Add(rec);
                    created++;
                }
                else updated++;
                rec.Title = r.Title ?? rec.Title;
                rec.Status = r.Status ?? rec.Status;
                rec.Amount = r.Amount ?? rec.Amount;
                rec.DueDate = r.DueDate ?? rec.DueDate;
                rec.UpdatedAt = DateTime.UtcNow;
                db.SaveChanges();
            }
            return Results.Ok(new { created, updated, total = body.Records.Count });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    private static int? TenantId(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;
}
