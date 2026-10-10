using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// "Request access" to a contact of ANOTHER branch of the caller's company
/// (crm_contactaccessrequest). Contacts are de-duplicated per company, so a user
/// bound to Lumina KL who adds a number Lumina Melaka already has gets a 409 that
/// names no one — with an opaque <c>request_token</c>
/// (<see cref="ContactAccessToken"/>). They turn it into a request here; an
/// admin who can see the contact AND manages the target branch approves (the
/// contact gets a <see cref="ContactEntity"/> link to that branch, keeping its
/// others) or denies it. Until then NOTHING about the contact reaches the
/// requester: their rows carry the branch, the note and the status only.
///
/// Who decides (<see cref="CanDecideAsync"/>): superadmin; an organization-wide
/// admin of the contact's tenant; a company admin whose scope holds the target
/// branch AND one of the contact's current entities — each with
/// <c>contacts.manage</c>.
/// </summary>
public static class ContactAccessEndpoints
{
    private const string Kind = "contact_access";
    private const int NoteMax = 500;

    public static void MapContactAccessEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- ask -------------------------------------------------------------
        app.MapPost("/api/contacts/access-requests", async (JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var username = me.Identity?.Name ?? "";
            var token = ContactAccessToken.Read(sc, Str(body, "request_token"));
            if (token == null || !string.Equals(token.Username, username, StringComparison.OrdinalIgnoreCase))
                return Json(400, "This request link is invalid or has expired — add the contact again to get a new one.");
            var bound = ShellScope.BoundEntityIds(db, sc, me);
            if (bound != null && !bound.Contains(token.EntityId))
                return Json(403, "That entity is outside your scope.");
            var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == token.ContactId);
            var entity = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == token.EntityId);
            if (contact == null || entity == null)
                return Json(400, "This request link is invalid or has expired — add the contact again to get a new one.");
            if (await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                    .AnyAsync(c => c.Id == contact.Id))
                return Results.Json(new
                {
                    detail = "You can already see this customer.",
                    existing_id = contact.Id,
                    existing_name = contact.Name,
                }, statusCode: 409);

            var note = (Str(body, "note") ?? "").Trim();
            if (note.Length > NoteMax) note = note[..NoteMax];
            var existing = await Pending(db, contact.Id, entity.Id);
            if (existing != null)
                return Results.Ok(new { created = false, request = await RowAsync(db, sc, me, existing) });

            var row = new ContactAccessRequest
            {
                ContactId = contact.Id, EntityId = entity.Id, RequestedBy = username, Note = note,
                Status = ContactAccessStatus.Pending, CreatedAt = DateTime.UtcNow,
            };
            db.ContactAccessRequests.Add(row);
            AuditTrail.Add(db, "contact.access_requested", $"access-request",
                $"{username} asked to add a customer to {entity.Name}", entityId: entity.Id);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // a concurrent identical request won the unique (pending) index
                db.ChangeTracker.Clear();
                existing = await Pending(db, contact.Id, entity.Id);
                if (existing == null) throw;
                return Results.Ok(new { created = false, request = await RowAsync(db, sc, me, existing) });
            }

            var approvers = await ApproverIdsAsync(db, sc, row, contact, username);
            await NotificationService.NotifyAsync(db, NotificationCategories.Admin, Kind,
                $"{username} asked to add a customer to {entity.Name}",
                $"{contact.Name}{(note.Length > 0 ? $" — \"{note}\"" : "")}",
                $"/contacts?access={row.Id}", NotificationAudience.ForUsers(approvers),
                new NotifyOptions { RefKey = RefKey(row.Id) });
            return Results.Json(new { created = true, request = await RowAsync(db, sc, me, row) }, statusCode: 201);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("contacts.manage", "chats.send");

        // ---- list ------------------------------------------------------------
        // Requesters see their own requests (no contact details until approved);
        // approvers see the requests they can decide.
        app.MapGet("/api/contacts/access-requests", async (string? status, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var wanted = (status ?? ContactAccessStatus.Pending).Trim().ToLowerInvariant();
            if (wanted != "all" && !ContactAccessStatus.All.Contains(wanted))
                return Json(400, $"status must be one of {string.Join(", ", ContactAccessStatus.All)} or all.");
            var username = me.Identity?.Name ?? "";
            var approver = await IsApproverAsync(db, me);
            var q = db.ContactAccessRequests.AsNoTracking().Include(r => r.Contact).AsQueryable();
            if (!approver) q = q.Where(r => r.RequestedBy == username);
            else if (ShellScope.RoleOf(me) != Roles.SuperAdmin && TenantId(me) is int tid)
                q = q.Where(r => r.RequestedBy == username || r.Contact!.TenantId == tid || r.Contact!.TenantId == null);
            var all = await q.OrderByDescending(r => r.Id).Take(1000).ToListAsync();

            var rows = new List<object>();
            int toDecide = 0, minePending = 0;
            var decider = approver ? await DeciderFor(db, sc, me) : null;
            foreach (var r in all)
            {
                var mine = string.Equals(r.RequestedBy, username, StringComparison.OrdinalIgnoreCase);
                var canDecide = decider != null && r.Contact != null && await decider.Can(r, r.Contact);
                if (!mine && !canDecide) continue;
                if (r.Status == ContactAccessStatus.Pending)
                {
                    if (canDecide) toDecide++;
                    if (mine) minePending++;
                }
                if (wanted != "all" && r.Status != wanted) continue;
                if (rows.Count < 200) rows.Add(await RowAsync(db, sc, me, r, canDecide));
            }
            return Results.Ok(new
            {
                requests = rows,
                can_approve = approver,
                counts = new { to_decide = toDecide, mine_pending = minePending },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("contacts.manage", "chats.send");

        // ---- decide ------------------------------------------------------------
        app.MapPost("/api/contacts/access-requests/{id:int}/approve", async (int id, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var (req, error) = await Decidable(db, sc, me, id);
            if (req == null) return error!;
            var actor = me.Identity?.Name ?? "";
            ContactCompanies.LinkToEntity(db, req.ContactId, req.EntityId);
            req.Status = ContactAccessStatus.Approved;
            req.DecidedBy = actor;
            req.DecidedAt = DateTime.UtcNow;
            var entityName = await EntityName(db, req.EntityId);
            AuditTrail.Add(db, "contact.access_approved", $"contact:{req.ContactId}",
                $"{req.Contact!.Name} added to {entityName} for {req.RequestedBy}", entityId: req.EntityId);
            await db.SaveChangesAsync();
            await CloseApproverAlertsAsync(db, req);
            await NotifyRequesterAsync(db, req, "Your access request was approved",
                $"{req.Contact.Name} is now shared with {entityName}.", $"/contacts/{req.ContactId}");
            return Results.Ok(new { request = await RowAsync(db, sc, me, req, false) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        app.MapPost("/api/contacts/access-requests/{id:int}/deny", async (int id, JsonElement body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var (req, error) = await Decidable(db, sc, me, id);
            if (req == null) return error!;
            var reason = (Str(body, "reason") ?? "").Trim();
            if (reason.Length > NoteMax) reason = reason[..NoteMax];
            req.Status = ContactAccessStatus.Denied;
            req.DecidedBy = me.Identity?.Name ?? "";
            req.DecidedAt = DateTime.UtcNow;
            req.Reason = reason;
            var entityName = await EntityName(db, req.EntityId);
            AuditTrail.Add(db, "contact.access_denied", $"contact:{req.ContactId}",
                $"{req.RequestedBy} → {entityName}{(reason.Length > 0 ? $": {reason}" : "")}", entityId: req.EntityId);
            await db.SaveChangesAsync();
            await CloseApproverAlertsAsync(db, req);
            // the requester still sees nothing about the contact
            await NotifyRequesterAsync(db, req, "Your access request was denied",
                reason.Length > 0 ? $"For {entityName}. Reason: {reason}" : $"For {entityName}.",
                $"/contacts?access={req.Id}");
            return Results.Ok(new { request = await RowAsync(db, sc, me, req, false) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        app.MapPost("/api/contacts/access-requests/{id:int}/cancel", async (int id, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var username = me.Identity?.Name ?? "";
            var req = await db.ContactAccessRequests.FirstOrDefaultAsync(r => r.Id == id);
            if (req == null || !string.Equals(req.RequestedBy, username, StringComparison.OrdinalIgnoreCase))
                return Json(404, "Request not found.");
            if (req.Status != ContactAccessStatus.Pending)
                return Json(409, $"This request is already {req.Status}.");
            req.Status = ContactAccessStatus.Cancelled;
            req.DecidedBy = username;
            req.DecidedAt = DateTime.UtcNow;
            AuditTrail.Add(db, "contact.access_cancelled", "access-request", $"#{req.Id}", entityId: req.EntityId);
            await db.SaveChangesAsync();
            await CloseApproverAlertsAsync(db, req);
            return Results.Ok(new { request = await RowAsync(db, sc, me, req, false) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("contacts.manage", "chats.send");
    }

    // ---- who decides ----------------------------------------------------------

    /// <summary>Superadmin, or an admin holding contacts.manage (the matrix + the
    /// caller's key scopes) — the people who may decide SOME request.</summary>
    private static async Task<bool> IsApproverAsync(AppDbContext db, ClaimsPrincipal me) =>
        ShellScope.RoleOf(me) is Roles.SuperAdmin or Roles.Admin
        && await AdminEndpoints.HasCapabilityAsync(db, ShellScope.RoleOf(me), "contacts.manage");

    /// <summary>One person's decision rule, with their scope resolved once.</summary>
    private sealed class Decider
    {
        public required AppDbContext Db { get; init; }
        public required string Role { get; init; }
        public int? TenantId { get; init; }
        /// <summary>Null = not bound (superadmin / organization-wide admin).</summary>
        public List<int>? Bound { get; init; }

        public async Task<bool> Can(ContactAccessRequest r, Contact contact)
        {
            if (Role == Roles.SuperAdmin) return true;
            if (Role != Roles.Admin) return false;
            if (TenantId is int t && contact.TenantId is int ct && ct != t) return false;
            if (Bound == null) return true;   // an organization-wide admin
            if (!Bound.Contains(r.EntityId)) return false;
            var bound = Bound;
            // the contact's CURRENT entities: its links, or a conversation there
            return await Db.ContactEntities.AsNoTracking()
                       .AnyAsync(l => l.ContactId == contact.Id && bound.Contains(l.EntityId))
                   || await Db.Threads.AsNoTracking()
                       .AnyAsync(x => x.ContactId == contact.Id && x.EntityId != null && bound.Contains(x.EntityId.Value));
        }
    }

    private static async Task<Decider?> DeciderFor(AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        if (!await IsApproverAsync(db, me)) return null;
        return new Decider
        {
            Db = db, Role = ShellScope.RoleOf(me), TenantId = TenantId(me),
            Bound = ShellScope.BoundEntityIds(db, sc, me),
        };
    }

    /// <summary>Every active user who may decide this request (for the alert).</summary>
    private static async Task<List<int>> ApproverIdsAsync(AppDbContext db, ScopingService sc,
        ContactAccessRequest req, Contact contact, string requester)
    {
        var ids = new List<int>();
        try
        {
            var people = await db.Profiles.AsNoTracking()
                .Where(p => (p.Role == Roles.SuperAdmin || p.Role == Roles.Admin) && p.User != null && p.User.IsActive)
                .Select(p => new { p.UserId, p.User!.Username, p.Role, p.TenantId,
                    Entities = p.Entities.Select(e => e.Id).ToList() })
                .ToListAsync();
            foreach (var p in people)
            {
                if (string.Equals(p.Username, requester, StringComparison.OrdinalIgnoreCase)) continue;
                if (!await AdminEndpoints.RoleHasCapabilityAsync(db, p.Role, "contacts.manage")) continue;
                var decider = new Decider
                {
                    Db = db, Role = p.Role, TenantId = p.TenantId,
                    Bound = p.Role == Roles.SuperAdmin || p.Entities.Count == 0 ? null : sc.EntitySubtreeIds(p.Entities),
                };
                if (await decider.Can(req, contact)) ids.Add(p.UserId);
            }
        }
        catch { /* an alert is a side effect — never fail the request */ }
        return ids;
    }

    /// <summary>The pending request the caller may decide, else the error to return.</summary>
    private static async Task<(ContactAccessRequest?, IResult?)> Decidable(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, int id)
    {
        var req = await db.ContactAccessRequests.Include(r => r.Contact).FirstOrDefaultAsync(r => r.Id == id);
        if (req?.Contact == null) return (null, Json(404, "Request not found."));
        var decider = await DeciderFor(db, sc, me);
        if (decider == null || !await decider.Can(req, req.Contact))
            return (null, Json(403, "You can't decide this request — it needs an admin who manages both branches."));
        if (req.Status != ContactAccessStatus.Pending)
            return (null, Json(409, $"This request is already {req.Status}."));
        return (req, null);
    }

    // ---- payloads + side effects ---------------------------------------------

    private static async Task<object> RowAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        ContactAccessRequest r, bool? canDecide = null)
    {
        var username = me.Identity?.Name ?? "";
        var contact = r.Contact ?? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.ContactId);
        var decide = canDecide ?? (contact != null && await DeciderFor(db, sc, me) is { } d && await d.Can(r, contact));
        // the contact's details only to someone who can already see it — an
        // approver, or the requester once approved
        var sees = contact != null && (decide || await ContactsEndpoints.ScopeContacts(
            db.Contacts.AsNoTracking(), me, db, sc).AnyAsync(c => c.Id == contact.Id));
        var entity = await db.Entities.AsNoTracking().Where(e => e.Id == r.EntityId)
            .Select(e => new { e.Id, e.Name, e.Slug }).FirstOrDefaultAsync();
        var company = entity == null ? null : await db.Entities.AsNoTracking()
            .Where(e => e.Id == ContactCompanies.CompanyOf(sc, entity.Id)).Select(e => e.Name).FirstOrDefaultAsync();
        object? contactInfo = null;
        if (sees && contact != null)
        {
            var entities = await db.ContactEntities.AsNoTracking().Where(l => l.ContactId == contact.Id)
                .Select(l => new { id = l.EntityId, name = l.Entity!.Name, slug = l.Entity.Slug }).ToListAsync();
            contactInfo = new
            {
                id = contact.Id,
                name = contact.Name,
                phone = MaskPhone(contact.Phone),
                email = MaskEmail(contact.Email),
                entities,
            };
        }
        return new
        {
            id = r.Id,
            status = r.Status,
            note = r.Note,
            reason = r.Reason,
            requested_by = r.RequestedBy,
            decided_by = r.DecidedBy,
            created_at = Iso(r.CreatedAt),
            decided_at = r.DecidedAt is DateTime at ? Iso(at) : null,
            entity = entity == null ? null : new { id = entity.Id, name = entity.Name, slug = entity.Slug },
            company_name = company ?? "",
            mine = string.Equals(r.RequestedBy, username, StringComparison.OrdinalIgnoreCase),
            can_decide = decide && r.Status == ContactAccessStatus.Pending,
            contact = contactInfo,
        };
    }

    private static Task<ContactAccessRequest?> Pending(AppDbContext db, int contactId, int entityId) =>
        db.ContactAccessRequests.AsNoTracking().FirstOrDefaultAsync(r =>
            r.ContactId == contactId && r.EntityId == entityId && r.Status == ContactAccessStatus.Pending);

    private static async Task NotifyRequesterAsync(AppDbContext db, ContactAccessRequest req, string title,
        string body, string link)
    {
        var uid = await db.Users.AsNoTracking().Where(u => u.Username == req.RequestedBy)
            .Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (uid is not int id) return;
        await NotificationService.NotifyAsync(db, NotificationCategories.Admin, Kind, title, body, link,
            NotificationAudience.ForUsers(new[] { id }), new NotifyOptions { RefKey = RefKey(req.Id) });
    }

    /// <summary>A decided / cancelled request: the approvers' unread alerts about it are done.</summary>
    private static async Task CloseApproverAlertsAsync(AppDbContext db, ContactAccessRequest req)
    {
        try
        {
            var key = RefKey(req.Id);
            var rows = await db.Notifications.Where(n => n.RefKey == key && !n.Read && n.User!.Username != req.RequestedBy)
                .ToListAsync();
            foreach (var n in rows) n.Read = true;
            if (rows.Count > 0) await db.SaveChangesAsync();
        }
        catch { /* side effect */ }
    }

    internal static string RefKey(int id) => $"contact-access:{id}";

    /// <summary>"•••• 4567" — only the last 4 digits.</summary>
    internal static string MaskPhone(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? "" : "•••• " + (digits.Length <= 4 ? digits : digits[^4..]);
    }

    internal static string MaskEmail(string? email)
    {
        var e = (email ?? "").Trim();
        var at = e.IndexOf('@');
        if (at <= 0) return e.Length == 0 ? "" : "••••";
        return e[0] + "•••" + e[at..];
    }

    private static async Task<string> EntityName(AppDbContext db, int id) =>
        await db.Entities.AsNoTracking().Where(e => e.Id == id).Select(e => e.Name).FirstOrDefaultAsync() ?? "";

    private static string Iso(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("o");

    private static int? TenantId(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var el)
        && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static IResult Json(int status, string detail) => Results.Json(new { detail }, statusCode: status);
}
