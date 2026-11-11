using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Contacts;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// "Link existing conversations to contacts" —
/// <c>POST /api/contacts/link-conversations</c>, the on-demand backfill behind
/// <see cref="ThreadContactBackfill"/>.
///
/// A contact import writes contacts, not conversations: a WhatsApp history
/// import that opened the chats BEFORE the contacts existed leaves them with
/// <c>contact_id = null</c> and no name, and nothing revisits them until the
/// customer writes again. This runs the INBOUND path's own resolution
/// (<see cref="ContactCompanies.MatchPhone"/> / <see cref="ContactCompanies.MatchEmail"/>
/// against the receiving entity, then <see cref="ContactCompanies.MayLink"/>) over
/// the conversations already on file — so a contact of one company can never be
/// attached to another company's conversation.
///
/// Body (all optional): <c>{preview, entity, channel, limit}</c>; <c>?preview=1</c>
/// works too. A preview writes nothing and reports exactly what a commit would
/// link. Capability <c>contacts.manage</c> (the import's own), and the run is
/// bounded by the caller's entity scope: only superadmin reaches a conversation
/// that belongs to no entity.
///
/// Audit: a commit writes <c>contacts.link_conversations</c> with the counts; a
/// preview changes nothing and is covered by <c>RequestAuditMiddleware</c>'s
/// generic row, the same way the AI playground's dry runs are.
/// </summary>
public static class ContactThreadLinkEndpoints
{
    public static void MapContactThreadLinkEndpoints(this IEndpointRouteBuilder app)
    {
        // the body is optional: a bare POST (and an MCP call with no arguments)
        // commits the default run over everything the caller may see
        app.MapPost("/api/contacts/link-conversations", async (JsonElement? raw, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var body = raw ?? default;
            var preview = Truthy(ctx.Request.Query["preview"]) || Flag(body, "preview");
            var channel = Str(body, "channel");
            var limit = Num(body, "limit");

            int? entityId = null;
            var allowed = await ContactIdentityEndpoints.AllowedEntityIdsAsync(db, sc, me);
            var slug = Str(body, "entity");
            if (!string.IsNullOrWhiteSpace(slug))
            {
                entityId = await InboxEndpoints.EntityId(db, slug);
                if (entityId == null) return Json(400, $"Unknown entity \"{slug}\".");
                if (allowed != null && !allowed.Contains(entityId.Value))
                    return Json(403, "That entity is outside your scope.");
            }

            var backfill = new ThreadContactBackfill(db, sc);
            var report = backfill.Run(preview, entityId, channel, limit, allowed);

            if (!preview && report.Linked > 0)
                AuditTrail.Write(db, "contacts.link_conversations",
                    $"{(slug is { Length: > 0 } s ? s : "all entities")}{(channel is { Length: > 0 } c ? $":{c}" : "")}",
                    $"linked {report.Linked}, already linked {report.AlreadyLinked}, no match {report.NoMatch}, "
                    + $"other company {report.Refused}, skipped {report.Skipped} (of {report.Scanned} scanned)",
                    entityId: entityId);

            return Results.Ok(new
            {
                preview = report.Preview,
                scanned = report.Scanned,
                linked = report.Linked,
                already_linked = report.AlreadyLinked,
                no_match = report.NoMatch,
                refused = report.Refused,
                skipped = report.Skipped,
                more = report.More,
                entity = slug,
                channel,
                sample = report.Sample.Select(l => new
                {
                    thread_id = l.ThreadId,
                    channel = l.Channel,
                    key = l.Key,
                    contact_id = l.ContactId,
                    contact_name = l.ContactName,
                }).ToList(),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");
    }

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var el)
        && el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s ? s.Trim() : null;

    private static bool Flag(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var el)
        && (el.ValueKind == JsonValueKind.True
            || (el.ValueKind == JsonValueKind.String && Truthy(el.GetString())));

    private static int? Num(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var el)
        && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) ? n : null;

    private static bool Truthy(string? v) =>
        v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                   || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static IResult Json(int status, string detail) => Results.Json(new { detail }, statusCode: status);
}
