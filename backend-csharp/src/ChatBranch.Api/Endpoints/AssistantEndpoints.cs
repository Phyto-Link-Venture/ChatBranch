using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Assistant;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Tools;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Mcp;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The operator assistant ("Ask ChatBranch"):
///
///   POST /api/assistant/chat     {messages:[{role,content}], context:{path, entity}}
///                                → {reply, steps, pending_actions, navigation, mode}
///   POST /api/assistant/confirm  {id} → runs a pending write, as the same user
///   POST /api/assistant/cancel   {id}
///
/// The conversation lives in the browser (sessionStorage); only proposed writes
/// are stored, server-side, for 15 minutes — so the UI can only ever execute
/// what the model proposed, and only the person it was proposed to.
/// </summary>
public static class AssistantEndpoints
{
    public static void MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/assistant/chat", async (JsonElement body, HttpContext http, ClaimsPrincipal me,
            ILlmClient llm, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var turns = new List<OperatorAssistant.ChatTurn>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("messages", out var msgs)
                && msgs.ValueKind == JsonValueKind.Array)
                foreach (var m in msgs.EnumerateArray())
                {
                    var role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                    var content = m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? "" : "";
                    turns.Add(new OperatorAssistant.ChatTurn(role, content));
                }
            if (!turns.Any(t => t.Role == "user"))
                return Results.Json(new { detail = "Send at least one user message." }, statusCode: 400);

            string? path = null, entity = null;
            if (body.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                path = context.TryGetProperty("path", out var p) ? p.GetString() : null;
                entity = context.TryGetProperty("entity", out var e) ? e.GetString() : null;
            }

            var tools = await ToolContext.CreateAsync(http, me, AuditSources.Assistant, entity);
            var assistant = new OperatorAssistant(tools, llm, httpFactory, path);
            var reply = await assistant.ChatAsync(turns, ct);
            return Results.Ok(new
            {
                reply = reply.Text,
                steps = reply.Steps.Select(s => new { tool = s.Tool, title = s.Title, ok = s.Ok, summary = s.Summary }),
                pending_actions = reply.PendingActions.Select(p => new
                {
                    id = p.Id, tool = p.Tool, title = p.Title, args = p.Args, summary = p.Summary,
                    expires_at = p.ExpiresAt,
                }),
                navigation = reply.Navigation.Select(l => new { label = l.Label, href = l.Href }),
                mode = reply.Mode,
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
            return Results.Ok(new { ok = true, id = action.Id, status = action.Status });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    /// <summary>
    /// Atomically move one of the CALLER's pending actions out of "pending"
    /// (another user's id reads as missing; a double-click runs it once).
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
            return (null, Results.Json(new { detail = $"This action was already {action.Status}." }, statusCode: 409));
        if (action.ExpiresAt <= DateTime.UtcNow)
        {
            action.Status = "expired";
            await db.SaveChangesAsync();
            return (null, Results.Json(new { detail = "This action expired — ask again." }, statusCode: 410));
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
