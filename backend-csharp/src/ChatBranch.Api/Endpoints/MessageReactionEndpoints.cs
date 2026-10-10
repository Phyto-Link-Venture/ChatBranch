using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// <c>POST /api/messaging/threads/{channel}/{key}/messages/{pk}/react {emoji}</c> —
/// the inbox's reaction bar, for every channel including WhatsApp (like the note
/// route, the messaging path serves it). <c>emoji: ""</c> removes ours; reacting
/// again replaces it. WhatsApp Cloud sends <c>type: reaction</c> (refused outside
/// the 24h window → 409), Baileys <c>react</c>, Telegram <c>setMessageReaction</c>;
/// a conversation with no live account stores it as sandbox. Other channels: 409.
/// An opted-out conversation takes one only with <c>force</c>. Notes and system
/// rows never carry one. Scope = <c>chats.send</c> on the conversation (out of
/// scope = 404).
/// </summary>
public static class MessageReactionEndpoints
{
    public record ReactRequest(string? Emoji, bool Force = false);

    public static void MapMessageReactionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/messaging/threads/{channel}/{key}/messages/{pk:int}/react",
            async (string channel, string key, int pk, ReactRequest body, AppDbContext db, OutboundSender sender,
                ClaimsPrincipal me) =>
        {
            var raw = (key ?? "").Trim();
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            var thread = await db.Threads.Include(t => t.Account)
                .FirstOrDefaultAsync(t => t.Channel == channel
                                          && (t.Key == raw || (channel == Channels.WhatsApp && digits.Length > 0 && t.Key == digits)));
            if (thread == null) return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == pk && m.ThreadId == thread.Id);
            if (msg == null) return Results.Json(new { detail = "Message not found." }, statusCode: 404);
            if (msg.Private || msg.Source == MessageSource.System)
                return Results.Json(new { detail = "Notes and system messages can't carry a reaction." }, statusCode: 409);
            var emoji = MessageReactions.Normalize(body?.Emoji);
            if (emoji == null) return Results.Json(new { detail = "emoji must be a single emoji (or \"\" to remove)." },
                statusCode: 400);
            if (!MessageReactions.Supports(thread.Channel))
                return Results.Json(new { detail = $"Reactions aren't supported on {thread.Channel}." }, statusCode: 409);
            if (thread.OptedOut && body?.Force != true)
                return Results.Json(new
                {
                    detail = "Contact opted out (STOP). Resend with force=true to override as a manual exception.",
                }, statusCode: 409);

            var username = me.Identity?.Name ?? "";
            if (username.Length == 0) return Results.Unauthorized();
            var sent = sender.React(thread, msg, emoji);
            if (!sent.Ok)
                return Results.Json(new { detail = sent.Detail }, statusCode: sent.Conflict ? 409 : 502);
            MessageReactions.Set(db, thread, msg, MessageReactions.UserActor(username), emoji);
            AuditTrail.Write(db, emoji.Length == 0 ? "message.unreact" : "message.react",
                $"{thread.Channel}:{thread.Key}", $"message {msg.Id} {emoji}".Trim(), entityId: thread.EntityId);
            var reactions = MessageReactions.For(db, new[] { msg.Id }).GetValueOrDefault(msg.Id) ?? new();
            return Results.Ok(new
            {
                ok = true,
                sandbox = !sent.Live,
                pk = msg.Id,
                reactions = reactions.Select(r => new
                {
                    emoji = r.Emoji,
                    actor = r.Actor,
                    at = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc).ToString("o"),
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");
    }
}
