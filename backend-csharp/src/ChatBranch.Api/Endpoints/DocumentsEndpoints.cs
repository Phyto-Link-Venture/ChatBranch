using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Documents;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Documents made by the Documents / QR app skills (and by operators):
///
/// * <c>GET /api/documents</c> (<c>chats.view</c>) — scoped like conversations
///   (<see cref="ShellScope.ThreadEntityIds"/>); filters <c>thread=&lt;channel&gt;:&lt;key&gt;</c>,
///   <c>contact_id</c>, <c>type</c>, <c>status</c>, <c>entity</c>; newest first.
/// * <c>GET /api/documents/{id}/file</c> (<c>chats.view</c>, in scope) — the PDF / PNG
///   (the AI monitor's "Open", the chat's document card).
/// * <c>GET /api/documents/public/{token}</c> — ANONYMOUS: the customer's download
///   link on channels that can't carry a file. The 128-bit token is the
///   credential; a void or pending document, or one older than 90 days, is a 404.
/// * <c>GET /api/documents/preview/{id}</c> — a throw-away preview (the AI
///   playground, the skill form's "Preview PDF"); nothing about a customer.
/// * <c>POST /api/documents</c> (<c>chats.send</c>, the conversation in scope) — an
///   operator makes one by hand with the conversation's Documents skill
///   (letterhead, numbering) and, with <c>send: true</c>, sends it now.
/// </summary>
public static class DocumentsEndpoints
{
    public record CreateBody(string? Channel, string? Key, string? Type, bool? Send, JsonElement? Fields);

    public static void MapDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/documents", async (string? thread, int? contact_id, string? type, string? status,
            string? entity, int? limit, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var picked = await InboxEndpoints.EntityId(db, entity);
            var ids = ShellScope.ThreadEntityIds(db, sc, me, picked);
            var q = Scoped(db.Documents.AsNoTracking(), ids);
            if (contact_id is int cid) q = q.Where(d => d.ContactId == cid);
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(d => d.Type == type.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(status))
            {
                var wanted = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => s.ToLowerInvariant()).ToList();
                if (wanted.Any(s => !DocumentStatus.All.Contains(s)))
                    return Results.Json(new { detail = $"status must be one of {string.Join(", ", DocumentStatus.All)}." }, statusCode: 400);
                q = q.Where(d => wanted.Contains(d.Status));
            }
            if (!string.IsNullOrWhiteSpace(thread))
            {
                var at = thread.IndexOf(':');
                if (at <= 0) return Results.Json(new { detail = "thread must be <channel>:<key>." }, statusCode: 400);
                var ch = thread[..at];
                var key = thread[(at + 1)..];
                var tid = await db.Threads.AsNoTracking().Where(t => t.Channel == ch && t.Key == key)
                    .Select(t => (int?)t.Id).FirstOrDefaultAsync();
                if (tid == null) return Results.Ok(new { documents = Array.Empty<object>() });
                q = q.Where(d => d.ThreadId == tid);
            }
            var rows = await q.OrderByDescending(d => d.Id).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync();
            return Results.Ok(new { documents = rows.Select(DocumentService.Payload).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.view");

        app.MapGet("/api/documents/{id:int}/file", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, DocumentService documents, HttpContext ctx) =>
        {
            var ids = ShellScope.ThreadEntityIds(db, sc, me, null);
            var doc = await Scoped(db.Documents.AsNoTracking(), ids).FirstOrDefaultAsync(d => d.Id == id);
            var full = doc == null ? null : documents.FileOf(doc);
            return full == null ? NotFound() : Serve(ctx, full, doc!.Mime, doc.FileName);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.view");

        // the customer's download link — the unguessable token is the credential
        app.MapGet("/api/documents/public/{token}", (string token, DocumentService documents, HttpContext ctx) =>
        {
            var doc = documents.ByPublicToken(token);
            var full = doc == null ? null : documents.FileOf(doc);
            return full == null ? NotFound() : Serve(ctx, full, doc!.Mime, doc.FileName);
        }).AllowAnonymous();

        app.MapGet("/api/documents/preview/{id}", (string id, DocumentService documents, HttpContext ctx) =>
        {
            var full = documents.PreviewFile(id);
            return full == null ? NotFound()
                : Serve(ctx, full, id.EndsWith(".png") ? "image/png" : "application/pdf", "preview" + Path.GetExtension(id));
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // the AI playground (monitor / behaviour) and the Skills page's "Preview PDF"
          .RequireAnyCapability("ai.monitor", "ai.behaviour.manage", "settings.ai.manage");

        app.MapPost("/api/documents", async (CreateBody body, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            SkillService skills, OutboundSender sender) =>
        {
            var channel = (body.Channel ?? "").Trim().ToLowerInvariant();
            var key = (body.Key ?? "").Trim();
            var thread = await db.Threads.Include(t => t.Contact)
                .FirstOrDefaultAsync(t => t.Channel == channel && t.Key == key);
            if (thread == null || !ThreadAccessFilter.InScope(db, sc, me, thread.EntityId))
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            var type = (body.Type ?? "").Trim().ToLowerInvariant();
            if (!DocumentAppConfig.AllActions.Contains(type))
                return Results.Json(new { detail = "type must be invoice, quotation, receipt or letter." }, statusCode: 400);

            // the conversation's own Documents skill: its letterhead and numbering
            var skill = skills.InScope(thread.EntityId, thread.Channel)
                .Where(s => s.Kind == SkillKinds.App && AppSkills.AppOf(s.Config) == AppSkills.Documents)
                .FirstOrDefault(s => DocumentAppConfig.From(s.Config).Allows(type));
            if (skill == null)
                return Results.Json(new
                {
                    detail = $"No Documents skill that makes a {type} is switched on for this conversation — add the "
                             + "Documents app on the Skills page first.",
                }, statusCode: 409);
            var cfg = DocumentAppConfig.From(skill.Config);
            var fields = body.Fields is JsonElement f && f.ValueKind == JsonValueKind.Object
                ? f : JsonDocument.Parse("{}").RootElement;
            var build = DocumentService.Build(type, fields, cfg, thread.Contact, skills.UtcNow, skills.OrgTimeZone(), "");
            if (build.Error != null) return Results.Json(new { detail = build.Error }, statusCode: 400);

            var user = me.Identity?.Name ?? "operator";
            if (body.Send == true)
            {
                if (thread.OptedOut)
                    return Results.Json(new { detail = "The contact opted out (STOP) — nothing was sent." }, statusCode: 409);
                if (sender.SocialWindowBlock(thread, thread.AccountId, MessagingTransportEndpoints.IsHumanSender(me)) is { } closed)
                    return Results.Json(new { detail = closed, window_open = false }, statusCode: 409);
            }
            var doc = skills.Documents.Issue(skill, type, cfg, build.Content!, thread.ContactId, thread, thread.EntityId,
                $"agent:{user}");
            AuditTrail.Write(db, "document.created", $"document:{doc.Id}",
                $"{DocumentService.Label(doc)} for {thread.Channel}:{thread.Key}", actor: user, entityId: doc.EntityId);
            if (body.Send == true)
            {
                skills.Documents.Deliver(new[] { doc.Id }, thread, thread.AccountId, sender, MessageSource.Agent, user);
                doc = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
            }
            return Results.Ok(new { document = DocumentService.Payload(doc) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send");
    }

    internal static IQueryable<Document> Scoped(IQueryable<Document> q, List<int>? entityIds) =>
        entityIds is null
            ? q
            : q.Where(d => d.EntityId == null || (d.EntityId != null && entityIds.Contains(d.EntityId.Value)));

    private static IResult NotFound() => Results.NotFound(new { detail = "Not found." });

    /// <summary>
    /// A PDF / PNG inline with nosniff. These files are rendered BY the server (never
    /// a customer's upload), so no sandbox CSP — it would blank the browser's PDF viewer.
    /// </summary>
    private static IResult Serve(HttpContext ctx, string full, string mime, string fileName)
    {
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        ctx.Response.Headers["Content-Disposition"] = $"inline; filename=\"{DocumentService.SafeFileName(Path.GetFileNameWithoutExtension(fileName))}{Path.GetExtension(fileName)}\"";
        return Results.File(full, mime is "application/pdf" or "image/png" ? mime : "application/octet-stream",
            enableRangeProcessing: true);
    }
}
