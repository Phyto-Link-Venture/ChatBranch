using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Contacts (DATABASE-FIRST, port of calls/contacts.py), the per-contact unified
/// channel hub (GET/POST /api/contacts/{id}/channels), and script templates.
/// </summary>
public static class ContactsEndpoints
{
    public record ContactWrite(string Name, string Phone, string? Email, string? Language,
        string? CompanyName, List<string>? Tags);
    public record IdentityWrite(string Channel, string Address);
    public record ScriptWrite(string Name, string Content, string? Entity);

    public static void MapContactsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/contacts", async (string? q, int? limit, AppDbContext db) =>
        {
            var query = db.Contacts.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(c => c.Name.Contains(q) || c.Phone.Contains(q) || c.Email.Contains(q));
            var rows = await query.OrderByDescending(c => c.UpdatedAt).Take(limit ?? 200).ToListAsync();
            return Results.Ok(new { contacts = rows.Select(Dtos.ContactDto) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/contacts", async (ContactWrite b, AppDbContext db) =>
        {
            var row = new Contact
            {
                Name = b.Name, Phone = b.Phone, Email = b.Email ?? "", Language = b.Language ?? "en",
                CompanyName = b.CompanyName ?? "", Tags = b.Tags ?? new(),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Contacts.Add(row);
            await db.SaveChangesAsync();
            return Results.Ok(Dtos.ContactDto(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapGet("/api/contacts/{id:int}", async (int id, AppDbContext db) =>
        {
            var c = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return c == null ? Results.NotFound() : Results.Ok(Dtos.ContactDto(c));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapMethods("/api/contacts/{id:int}", new[] { "PATCH" },
            async (int id, ContactWrite b, AppDbContext db) =>
        {
            var c = await db.Contacts.FindAsync(id);
            if (c == null) return Results.NotFound();
            c.Name = b.Name; c.Phone = b.Phone;
            if (b.Email != null) c.Email = b.Email;
            if (b.Language != null) c.Language = b.Language;
            if (b.CompanyName != null) c.CompanyName = b.CompanyName;
            if (b.Tags != null) c.Tags = b.Tags;
            c.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(Dtos.ContactDto(c));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- unified channel hub ----------------------------------------------
        app.MapGet("/api/contacts/{id:int}/channels", async (int id, AppDbContext db) =>
        {
            var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return Results.NotFound();

            var threads = await db.Threads.AsNoTracking().Include(t => t.Account)
                .Where(t => t.ContactId == id).ToListAsync();
            var channels = threads.Select(t => new
            {
                channel = t.Channel,
                address = t.Key,
                accountId = t.AccountId,
                accountName = t.Account?.Name,
                accountHealth = t.Account?.HealthStatus,
                inbound = db.Messages.Count(m => m.ThreadId == t.Id && m.Direction == MessageDirection.In),
                outbound = db.Messages.Count(m => m.ThreadId == t.Id && m.Direction == MessageDirection.Out),
                lastActive = t.UpdatedAt,
                optedOut = t.OptedOut,
            }).ToList();
            return Results.Ok(new { contact = Dtos.ContactDto(contact), channels });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/contacts/{id:int}/channels", async (int id, IdentityWrite b,
            AppDbContext db, ThreadStore store) =>
        {
            var contact = await db.Contacts.FindAsync(id);
            if (contact == null) return Results.NotFound();
            var thread = store.GetThread(b.Channel, b.Address);
            if (thread != null && thread.ContactId != null && thread.ContactId != id)
                return Results.Json(new { detail = "identity linked to another contact" }, statusCode: 409);
            // store and this endpoint share the same scoped DbContext, so the
            // returned thread is already tracked — just link and save.
            thread ??= store.GetOrCreateThread(b.Channel, b.Address, contact.Name);
            thread.ContactId = id;
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, channel = b.Channel, address = b.Address });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- script templates --------------------------------------------------
        app.MapGet("/api/calls/script-templates", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = sc.ScopeInherited(db.ScriptTemplates.AsNoTracking(), entityId)
                .OrderBy(s => s.Name).ToList();
            // Frontend (ScriptTemplatesSettings) reads `templates` — a `scripts`
            // key left it undefined and crashed on `sorted.length`.
            return Results.Ok(new { templates = rows.Select(Dtos.ScriptTemplateDto) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/calls/script-templates", async (ScriptWrite b, AppDbContext db) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, b.Entity);
            var row = new ScriptTemplate
            {
                Name = b.Name, Content = b.Content, EntityId = entityId,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.ScriptTemplates.Add(row);
            await db.SaveChangesAsync();
            return Results.Ok(Dtos.ScriptTemplateDto(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }
}
