using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatBranch.Api.Assistant;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Tools;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Mcp;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The operator assistant ("Ask ChatBranch"):
///
///   POST /api/assistant/chat     {message, conversation_id?, context:{path, entity}}
///                                → {reply, steps, pending_actions, navigation, mode,
///                                   conversation_id, title, message_id}
///   POST /api/assistant/confirm  {id} → runs a pending write, as the same user
///   POST /api/assistant/cancel   {id}
///
///   GET    /api/assistant/conversations?limit=&amp;before=  → the caller's chats, newest first
///   GET    /api/assistant/conversations/{id}             → its turns, in order
///   PATCH  /api/assistant/conversations/{id} {title}
///   DELETE /api/assistant/conversations/{id}
///   DELETE /api/assistant/conversations                  → clear all of mine
///
/// History is per user and server-side (<see cref="AssistantHistory"/>): every
/// route reads the CALLER's conversations only — another login's id is a 404,
/// superadmin included. The model's context is rebuilt from the stored turns of
/// the conversation; a legacy client's <c>messages</c> list is only honoured when
/// it names no conversation. Proposed writes are stored for 15 minutes, so the UI
/// can only ever execute what the model proposed, and only the person it was
/// proposed to; the stored card follows confirm / cancel / expiry.
/// </summary>
public static class AssistantEndpoints
{
    public static void MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/assistant/chat", async (JsonElement body, HttpContext http, ClaimsPrincipal me,
            AppDbContext db, ILlmClient llm, IHttpClientFactory httpFactory, ScopingService sc, CancellationToken ct) =>
        {
            // Legacy clients send the whole history as `messages`; the dock sends
            // just the new `message` and the conversation it belongs to.
            var sent = new List<OperatorAssistant.ChatTurn>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("messages", out var msgs)
                && msgs.ValueKind == JsonValueKind.Array)
                foreach (var m in msgs.EnumerateArray())
                {
                    var role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                    var content = m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? "" : "";
                    sent.Add(new OperatorAssistant.ChatTurn(role, content));
                }
            var question = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out var q)
                           && q.ValueKind == JsonValueKind.String
                ? q.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(question))
            {
                // no `message`: the legacy list's last user turn is the question
                var lastUser = sent.FindLastIndex(t => t.Role == "user" && !string.IsNullOrWhiteSpace(t.Content));
                if (lastUser < 0)
                    return Results.Json(new { detail = "Send at least one user message." }, statusCode: 400);
                question = sent[lastUser].Content;
                sent = sent.Take(lastUser).ToList();
            }
            question = question.Trim();

            string? path = null, entity = null;
            if (body.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                path = context.TryGetProperty("path", out var p) ? p.GetString() : null;
                entity = context.TryGetProperty("entity", out var e) ? e.GetString() : null;
            }

            // The context is the STORED conversation. A missing id, or one that is
            // not the caller's, starts a new conversation instead. A client-sent
            // history only counts for a legacy request that names no conversation.
            var requested = ConversationId(body);
            var turns = requested == null ? sent : new List<OperatorAssistant.ChatTurn>();
            AssistantConversation? convo = null;
            if (await Caller.UserIdAsync(db, me) is int userId)
            {
                if (requested is int cid)
                    convo = await AssistantHistory.OwnAsync(db, userId, cid, ct);
                if (convo != null)
                    turns = await AssistantHistory.ContextAsync(db, convo.Id, ct);
                else
                    convo = await AssistantHistory.StartAsync(db, userId, question,
                        await InboxEndpoints.EntityId(db, entity), ct);
                await AssistantHistory.AppendAsync(db, convo, "user", question,
                    new JsonObject { ["context"] = new JsonObject { ["path"] = path, ["entity"] = entity } }, ct);
            }
            turns.Add(new OperatorAssistant.ChatTurn("user", question));

            // a person whose AI quota is used up gets that answer, not an error
            OperatorAssistant.Reply reply;
            if (QuotaGuard.UserBlocked(db, sc, me) is string refusal)
                reply = new OperatorAssistant.Reply(refusal, new(), new(), new(), "quota");
            else
            {
                var tools = await ToolContext.CreateAsync(http, me, AuditSources.Assistant, entity);
                var assistant = new OperatorAssistant(tools, llm, httpFactory, path);
                reply = await assistant.ChatAsync(turns, ct);
            }
            var payload = AssistantHistory.ReplyPayload(reply);
            AssistantMessage? stored = null;
            if (convo != null)
                stored = await AssistantHistory.AppendAsync(db, convo, "assistant", reply.Text, payload, ct);
            return Results.Ok(new
            {
                reply = reply.Text,
                steps = payload["steps"],
                pending_actions = payload["pending_actions"],
                navigation = payload["navigation"],
                mode = reply.Mode,
                conversation_id = convo?.Id,
                title = convo?.Title,
                message_id = stored?.Id,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/assistant/confirm", async (JsonElement body, HttpContext http, ClaimsPrincipal me,
            AppDbContext db, ILlmClient llm, IHttpClientFactory httpFactory, ScopingService sc,
            CancellationToken ct) =>
        {
            var (action, error) = await ClaimAsync(db, me, body);
            if (action == null) return error!;

            var tools = await ToolContext.CreateAsync(http, me, AuditSources.Assistant);
            var args = OperatorAssistant.ParseArgs(action.Args);
            bool ok;
            object? result;
            string? message;
            if (ToolRegistry.Find(action.Tool) is { } tool)
            {
                var outcome = await ToolRegistry.ExecuteAsync(tools, tool.Name, args, ct);
                ok = outcome.Ok;
                result = outcome.Data;
                message = ok ? $"Done — {action.Summary}." : outcome.Error;
            }
            else if (ExternalMcpTools.Available(sc).FirstOrDefault(e => e.QualifiedName == action.Tool) is { } ext
                     && tools.Capabilities.Contains("integrations.view"))
            {
                var assistant = new OperatorAssistant(tools, llm, httpFactory, null);
                var (text, extOk) = await assistant.RunExternalAsync(ext, args, ct);
                ok = extOk;
                result = new { output = text };
                message = ok ? $"Done — {action.Summary}." : text;
            }
            else
            {
                ok = false;
                result = null;
                message = "That tool is no longer available.";
            }

            action.Status = ok ? "done" : "failed";
            action.Result = Cap(JsonSerializer.Serialize(ok ? result : new { error = message }), 4000);
            action.DecidedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await AssistantHistory.MarkActionAsync(db, action.UserId, action.Id, action.Status, message,
                tools.Links, ct);
            return Results.Ok(new
            {
                ok,
                id = action.Id,
                status = action.Status,
                message,
                result,
                navigation = tools.Links.Select(l => new { label = l.Label, href = l.Href }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/assistant/cancel", async (JsonElement body, AppDbContext db, ClaimsPrincipal me) =>
        {
            var (action, error) = await ClaimAsync(db, me, body, claimAs: "cancelled");
            if (action == null) return error!;
            action.DecidedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await AssistantHistory.MarkActionAsync(db, action.UserId, action.Id, action.Status);
            return Results.Ok(new { ok = true, id = action.Id, status = action.Status });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        MapHistory(app);
    }

    /// <summary>The caller's own assistant conversations (see the class summary).</summary>
    private static void MapHistory(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/assistant/conversations", async (int? limit, string? before, AppDbContext db,
            ClaimsPrincipal me, CancellationToken ct) =>
        {
            if (await Caller.UserIdAsync(db, me) is not int userId)
                return Results.Ok(new
                {
                    conversations = Array.Empty<object>(), total = 0, has_more = false, next_before = (string?)null,
                });
            var take = Math.Clamp(limit ?? 30, 1, 100);
            var mine = db.AssistantConversations.AsNoTracking().Where(c => c.UserId == userId);
            var total = await mine.CountAsync(ct);
            var page = mine;
            if (!string.IsNullOrWhiteSpace(before))
            {
                if (!DateTime.TryParse(before, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var cursor))
                    return Results.Json(new { detail = "before must be a timestamp (next_before)." },
                        statusCode: 400);
                page = page.Where(c => c.UpdatedAt < cursor);
            }
            var rows = await page.OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id)
                .Take(take + 1)
                .Select(c => new
                {
                    c.Id, c.Title, c.EntityId, c.CreatedAt, c.UpdatedAt,
                    Count = c.Messages.Count(),
                    Last = c.Messages.OrderByDescending(m => m.Id).Select(m => m.Text).FirstOrDefault(),
                })
                .ToListAsync(ct);
            var more = rows.Count > take;
            if (more) rows.RemoveAt(rows.Count - 1);
            return Results.Ok(new
            {
                conversations = rows.Select(c => new
                {
                    id = c.Id,
                    title = c.Title,
                    entity_id = c.EntityId,
                    created_at = AssistantHistory.Iso(c.CreatedAt),
                    updated_at = AssistantHistory.Iso(c.UpdatedAt),
                    message_count = c.Count,
                    snippet = AssistantHistory.Snippet(c.Last),
                }),
                total,
                has_more = more,
                next_before = more ? AssistantHistory.Iso(rows[^1].UpdatedAt) : null,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapGet("/api/assistant/conversations/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me,
            CancellationToken ct) =>
        {
            var convo = await MineAsync(db, me, id, ct);
            if (convo == null) return NoSuchConversation();
            var messages = await AssistantHistory.RenderAsync(db, convo.UserId, convo.Id, ct);
            return Results.Ok(new
            {
                id = convo.Id,
                title = convo.Title,
                entity_id = convo.EntityId,
                created_at = AssistantHistory.Iso(convo.CreatedAt),
                updated_at = AssistantHistory.Iso(convo.UpdatedAt),
                messages,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapMethods("/api/assistant/conversations/{id:int}", new[] { "PATCH" }, async (int id,
            JsonElement body, AppDbContext db, ClaimsPrincipal me, CancellationToken ct) =>
        {
            var convo = await MineAsync(db, me, id, ct);
            if (convo == null) return NoSuchConversation();
            var raw = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("title", out var t)
                      && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var title = Regex.Replace(raw, @"\s+", " ").Trim();
            if (title.Length == 0) return Results.Json(new { detail = "title is required." }, statusCode: 400);
            if (title.Length > AssistantHistory.MaxTitle)
                return Results.Json(new { detail = $"title must be at most {AssistantHistory.MaxTitle} characters." },
                    statusCode: 400);
            convo.Title = title;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                id = convo.Id,
                title = convo.Title,
                entity_id = convo.EntityId,
                created_at = AssistantHistory.Iso(convo.CreatedAt),
                updated_at = AssistantHistory.Iso(convo.UpdatedAt),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/assistant/conversations/{id:int}", async (int id, AppDbContext db,
            ClaimsPrincipal me, CancellationToken ct) =>
        {
            var convo = await MineAsync(db, me, id, ct);
            if (convo == null) return NoSuchConversation();
            await AssistantHistory.DeleteAsync(db, convo.UserId, new[] { convo.Id }, ct);
            AuditTrail.Write(db, "assistant.conversation.delete", $"assistant_conversation:{id}", convo.Title);
            return Results.Ok(new { ok = true, deleted = 1 });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/assistant/conversations", async (AppDbContext db, ClaimsPrincipal me,
            CancellationToken ct) =>
        {
            if (await Caller.UserIdAsync(db, me) is not int userId)
                return Results.Ok(new { ok = true, deleted = 0 });
            var deleted = await AssistantHistory.DeleteAsync(db, userId, null, ct);
            AuditTrail.Write(db, "assistant.conversation.clear", "assistant history", $"{deleted} conversation(s)");
            return Results.Ok(new { ok = true, deleted });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    /// <summary>The caller's own conversation, or null — another login's id reads as missing.</summary>
    private static async Task<AssistantConversation?> MineAsync(AppDbContext db, ClaimsPrincipal me, int id,
        CancellationToken ct) =>
        await Caller.UserIdAsync(db, me) is int userId ? await AssistantHistory.OwnAsync(db, userId, id, ct) : null;

    private static IResult NoSuchConversation() =>
        Results.Json(new { detail = "Conversation not found." }, statusCode: 404);

    /// <summary>`conversation_id` as a number or a numeric string; anything else = none.</summary>
    private static int? ConversationId(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("conversation_id", out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s : null;
    }

    /// <summary>
    /// Atomically move one of the CALLER's pending actions out of "pending"
    /// (another user's id reads as missing; a double-click runs it once). A
    /// refusal also brings the stored card in line with the real status.
    /// </summary>
    private static async Task<(AssistantAction? Action, IResult? Error)> ClaimAsync(AppDbContext db,
        ClaimsPrincipal me, JsonElement body, string claimAs = "running")
    {
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var i)
            ? i.GetString() ?? "" : "";
        var uid = await Caller.UserIdAsync(db, me);
        var action = await db.AssistantActions.FirstOrDefaultAsync(a => a.Id == id);
        if (action == null || action.UserId != uid)
            return (null, Results.Json(new { detail = "No such pending action." }, statusCode: 404));
        if (action.Status != "pending")
        {
            if (action.Status != "running")
                await AssistantHistory.MarkActionAsync(db, action.UserId, action.Id, action.Status);
            return (null, Results.Json(new { detail = $"This action was already {action.Status}.", status = action.Status },
                statusCode: 409));
        }
        if (action.ExpiresAt <= DateTime.UtcNow)
        {
            action.Status = "expired";
            await db.SaveChangesAsync();
            await AssistantHistory.MarkActionAsync(db, action.UserId, action.Id, "expired",
                "This action expired — ask again.");
            return (null, Results.Json(new { detail = "This action expired — ask again.", status = "expired" },
                statusCode: 410));
        }
        var claimed = await db.AssistantActions
            .Where(a => a.Id == id && a.Status == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, claimAs));
        if (claimed != 1)
            return (null, Results.Json(new { detail = "This action is already being handled." }, statusCode: 409));
        action.Status = claimAs;
        return (action, null);
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}
