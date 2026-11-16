using System.Security.Claims;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
// `Thread` is declared on the Found record below, so the domain one must win
// over System.Threading.Thread.
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// What an operator can do in a chat beyond text and files, the way WhatsApp
/// itself lets a person: <c>POST …/messages/{pk}/recall</c> ("delete for
/// everyone"), <c>POST …/messages/{pk}/edit {text}</c>,
/// <c>POST …/messages/{pk}/forward {to_channel, to_key}</c> and
/// <c>POST …/send-card {kind: location|contact|poll}</c>. They sit beside
/// <see cref="MessageReactionEndpoints"/> and share its shape — the messaging
/// path serves every channel, the conversation is resolved and scoped by
/// <c>RequireThreadAccess("chats.send")</c>, and out of scope is a 404.
///
/// **These are QR (Baileys) numbers only, and the refusal says so.** Meta's
/// Cloud API publishes no delete and no edit, so an official number answers 409
/// naming the reason rather than failing obscurely — the same honesty the
/// reaction route uses for providers that cannot react.
///
/// WhatsApp's own time limits are checked BEFORE the provider call
/// (<see cref="MessageEdits.Window"/>, <see cref="MessageEdits.RecallWindow"/>),
/// so an operator past the window gets a sentence instead of a provider error.
/// A message that never left this inbox (no provider id, or no live number) is
/// handled locally: there is nothing on the customer's phone to change.
/// </summary>
public static class MessageActionEndpoints
{
    public record EditRequest(string? Text);

    public record ForwardRequest(
        [property: JsonPropertyName("to_channel")] string? ToChannel,
        [property: JsonPropertyName("to_key")] string? ToKey,
        [property: JsonPropertyName("to_thread_id")] int? ToThreadId = null,
        bool Force = false);

    public record CardRequest(
        string? Kind,
        double? Lat, double? Lng, string? Name, string? Address,
        List<string>? Phones, string? Organization,
        string? Question, List<string>? Options,
        [property: JsonPropertyName("selectable_count")] int? SelectableCount = null,
        bool Force = false);

    public static void MapMessageActionEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- delete for everyone --------------------------------------------
        app.MapPost("/api/messaging/threads/{channel}/{key}/messages/{pk:int}/recall",
            async (string channel, string key, int pk, AppDbContext db, OutboundSender sender,
                ChatBranch.Data.Ai.AiGate gate, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var found = await LoadAsync(db, ctx, pk);
            if (found.Error != null) return found.Error;
            var (thread, msg) = (found.Thread!, found.Message!);
            var username = me.Identity?.Name ?? "";
            if (username.Length == 0) return Results.Unauthorized();

            if (msg.RecalledAt != null)
                return Results.Json(new { detail = "That message was already deleted." }, statusCode: 409);
            if (!MessageEdits.WithinRecallWindow(msg))
                return Results.Json(new
                {
                    detail = "WhatsApp only allows deleting for everyone within about two days of sending. "
                             + "You can still delete it on your own phone.",
                }, statusCode: 409);

            var sent = sender.Revoke(thread, msg);
            if (!sent.Ok) return Results.Json(new { detail = sent.Detail }, statusCode: sent.Conflict ? 409 : 502);

            var account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == (msg.AccountId ?? thread.AccountId));
            // with no account row the inbox's keep/erase switch has no owner —
            // treat it as the thread's own inbox, which is what the UI showed
            MessageRecalls.RecordOurs(db, account ?? new ChannelAccount { Channel = thread.Channel }, msg, username,
                gate: gate);
            return Results.Ok(new { ok = true, sandbox = !sent.Live, pk = msg.Id, recalled = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- edit a sent message --------------------------------------------
        app.MapPost("/api/messaging/threads/{channel}/{key}/messages/{pk:int}/edit",
            async (string channel, string key, int pk, EditRequest body, AppDbContext db, OutboundSender sender,
                ClaimsPrincipal me, HttpContext ctx) =>
        {
            var found = await LoadAsync(db, ctx, pk);
            if (found.Error != null) return found.Error;
            var (thread, msg) = (found.Thread!, found.Message!);
            var username = me.Identity?.Name ?? "";
            if (username.Length == 0) return Results.Unauthorized();

            var text = (body?.Text ?? "").Trim();
            if (text.Length == 0)
                return Results.Json(new { detail = "text is required (to remove a message, delete it instead)." },
                    statusCode: 400);
            if (text.Length > 4096)
                return Results.Json(new { detail = "That edit is too long for one WhatsApp message." },
                    statusCode: 400);
            if (text == msg.Text.Trim())
                return Results.Json(new { detail = "That is the same text — nothing to edit." }, statusCode: 400);
            if (!MessageEdits.WithinWindow(msg))
                return Results.Json(new
                {
                    detail = "WhatsApp only allows editing within about 15 minutes of sending. "
                             + "Send a correction, or delete the message for everyone.",
                }, statusCode: 409);

            var sent = sender.Edit(thread, msg, text);
            if (!sent.Ok) return Results.Json(new { detail = sent.Detail }, statusCode: sent.Conflict ? 409 : 502);

            MessageEdits.Record(db, thread, msg, text, username);
            return Results.Ok(new
            {
                ok = true,
                sandbox = !sent.Live,
                pk = msg.Id,
                text = msg.Text,
                edited_at = DateTime.SpecifyKind(msg.EditedAt!.Value, DateTimeKind.Utc).ToString("o"),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- forward a message into another conversation ---------------------
        app.MapPost("/api/messaging/threads/{channel}/{key}/messages/{pk:int}/forward",
            async (string channel, string key, int pk, ForwardRequest body, AppDbContext db, ScopingService sc,
                OutboundSender sender, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var found = await LoadAsync(db, ctx, pk);
            if (found.Error != null) return found.Error;
            var msg = found.Message!;

            var toChannel = (body?.ToChannel ?? "").Trim().ToLowerInvariant();
            var toKey = (body?.ToKey ?? "").Trim();
            if (toChannel.Length == 0 || toKey.Length == 0)
                return Results.Json(new { detail = "to_channel and to_key are required." }, statusCode: 400);
            if (toChannel != found.Thread!.Channel)
                return Results.Json(new
                {
                    detail = "A message can only be forwarded within the same channel — "
                             + $"this one is on {found.Thread.Channel}.",
                }, statusCode: 409);

            // the TARGET conversation is named in the body, so it is scoped here
            // (the filter only scoped the one in the path)
            var target = body?.ToThreadId is int id
                ? await db.Threads.Include(x => x.Account).FirstOrDefaultAsync(x => x.Id == id)
                : await db.Threads.Include(x => x.Account)
                    .FirstOrDefaultAsync(x => x.Channel == toChannel && x.Key == toKey);
            if (target == null || !ThreadAccessFilter.InScope(db, sc, me, target))
                return Results.Json(new { detail = "That conversation was not found." }, statusCode: 404);
            if (target.Id == found.Thread.Id)
                return Results.Json(new { detail = "That is the same conversation." }, statusCode: 400);
            if (target.OptedOut && body?.Force != true)
                return Results.Json(new
                {
                    detail = "That contact opted out (STOP). Resend with force=true to override as a manual exception.",
                }, statusCode: 409);

            var text = msg.Text.Length > 0 ? msg.Text : $"[{(msg.MediaType.Length > 0 ? msg.MediaType : "message")}]";
            var (sent, error, conflict) = sender.Forward(target, msg.ExternalId, text, MessageSource.Agent);
            if (sent == null) return Results.Json(new { detail = error }, statusCode: conflict ? 409 : 502);
            AiControlsEndpoints.HumanReplied(db, target, sent, me);
            AuditTrail.Write(db, "message.forwarded", $"{target.Channel}:{target.Key}",
                $"forwarded message #{msg.Id} from {found.Thread.Channel}:{found.Thread.Key}",
                entityId: target.EntityId);
            return Results.Ok(new { ok = true, pk = sent.Id, to = new { channel = target.Channel, key = target.Key } });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- a location, a contact card or a poll ----------------------------
        app.MapPost("/api/messaging/threads/{channel}/{key}/send-card",
            async (string channel, string key, CardRequest body, AppDbContext db, OutboundSender sender,
                ClaimsPrincipal me, HttpContext ctx) =>
        {
            var thread = await ThreadResolver.LoadAsync(ctx);
            if (thread == null) return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            if (thread.OptedOut && body?.Force != true)
                return Results.Json(new
                {
                    detail = "Contact opted out (STOP). Resend with force=true to override as a manual exception.",
                }, statusCode: 409);

            var kind = (body?.Kind ?? "").Trim().ToLowerInvariant();
            (Message? Sent, string Error, bool Conflict) outcome;
            switch (kind)
            {
                case "location":
                    if (body?.Lat is not double lat || body.Lng is not double lng)
                        return Results.Json(new { detail = "lat and lng are required for a location." },
                            statusCode: 400);
                    if (lat is < -90 or > 90 || lng is < -180 or > 180)
                        return Results.Json(new { detail = "lat must be -90..90 and lng -180..180." },
                            statusCode: 400);
                    outcome = sender.SendLocation(thread, lat, lng, (body.Name ?? "").Trim(),
                        (body.Address ?? "").Trim(), MessageSource.Agent);
                    break;

                case "contact":
                    var contactName = (body?.Name ?? "").Trim();
                    var phones = (body?.Phones ?? new List<string>())
                        .Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).Take(10).ToList();
                    if (contactName.Length == 0 || phones.Count == 0)
                        return Results.Json(new { detail = "name and at least one phone are required for a contact card." },
                            statusCode: 400);
                    outcome = sender.SendContact(thread, contactName, phones, (body!.Organization ?? "").Trim(),
                        MessageSource.Agent);
                    break;

                case "poll":
                    var question = (body?.Question ?? "").Trim();
                    // WhatsApp shows duplicates as one option, and then the votes
                    // cannot be told apart — so they are dropped, not sent
                    var options = (body?.Options ?? new List<string>())
                        .Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).Distinct().Take(12).ToList();
                    if (question.Length == 0 || options.Count < 2)
                        return Results.Json(new { detail = "A poll needs a question and at least 2 different options." },
                            statusCode: 400);
                    var pick = Math.Clamp(body?.SelectableCount ?? 1, 1, options.Count);
                    outcome = sender.SendPoll(thread, question, options, pick, MessageSource.Agent);
                    break;

                default:
                    return Results.Json(new { detail = "kind must be location, contact or poll." }, statusCode: 400);
            }

            if (outcome.Sent == null)
                return Results.Json(new { detail = outcome.Error }, statusCode: outcome.Conflict ? 409 : 502);
            AiControlsEndpoints.HumanReplied(db, thread, outcome.Sent, me);
            AuditTrail.Write(db, "message.send", $"{thread.Channel}:{thread.Key}", $"card:{kind}",
                entityId: thread.EntityId);
            return Results.Ok(new
            {
                ok = true,
                pk = outcome.Sent.Id,
                text = outcome.Sent.Text,
                sandbox = outcome.Sent.Status == "sandbox",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");
    }

    private sealed record Found(Thread? Thread, Message? Message, IResult? Error);

    /// <summary>
    /// The conversation (already scoped by the filter) and one of ITS messages.
    /// A message id from another conversation is a 404, not someone else's row.
    /// </summary>
    private static async Task<Found> LoadAsync(AppDbContext db, HttpContext ctx, int pk)
    {
        var thread = await ThreadResolver.LoadAsync(ctx);
        if (thread == null)
            return new Found(null, null, Results.Json(new { detail = "Thread not found." }, statusCode: 404));
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == pk && m.ThreadId == thread.Id);
        if (msg == null)
            return new Found(null, null, Results.Json(new { detail = "Message not found." }, statusCode: 404));
        return new Found(thread, msg, null);
    }
}
