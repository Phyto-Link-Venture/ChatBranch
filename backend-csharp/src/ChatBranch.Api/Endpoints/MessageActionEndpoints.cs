using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
// `Thread` is declared on the Found record below, so the domain one must win
// over System.Threading.Thread.
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// What an operator can do to a message ALREADY SENT, the way WhatsApp itself
/// lets a person: <c>POST …/messages/{pk}/recall</c> ("delete for everyone") and
/// <c>POST …/messages/{pk}/edit {text}</c>. Both sit beside
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
