using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Tools;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// ChatBranch as an MCP server — <c>POST /mcp</c>, the Streamable HTTP transport.
/// Any MCP-capable AI app (Claude Desktop, Claude Code, Cursor…) connects with a
/// personal API key and gets ChatBranch's tools, acting as that key's user:
/// same capabilities, same entity scope, every call in the activity log under
/// source "mcp".
///
/// JSON-RPC 2.0 over POST, answered as <c>application/json</c> (no server push, so
/// no SSE stream is needed): <c>initialize</c>, <c>notifications/initialized</c>,
/// <c>ping</c>, <c>tools/list</c>, <c>tools/call</c>. Batches are accepted. GET and
/// DELETE answer 405 (no standalone stream; sessions are stateless — an
/// <c>Mcp-Session-Id</c> is issued on initialize and accepted, never required).
/// </summary>
public static class McpServerEndpoints
{
    public static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    public static string ServerVersion { get; } =
        typeof(McpServerEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? typeof(McpServerEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public const string Instructions =
        "ChatBranch is a multi-channel conversation platform: one inbox for WhatsApp, email, " +
        "Messenger, Instagram, Telegram and website chat, with contacts, automations and an AI " +
        "assistant. These tools act as the API key's owner, with that person's permissions and " +
        "entity scope. Typical flow: search_contacts or list_conversations to find something, " +
        "get_contact / get_conversation to read it, then send_message, add_note, " +
        "set_conversation_status or assign_conversation to act. Conversations are addressed by " +
        "channel + key (as returned by list_conversations). Sends follow the normal guards — " +
        "opt-outs, WhatsApp's 24-hour window — and a refusal says why. Everything you do is " +
        "recorded in the ChatBranch activity log.";


    public static void MapMcpServerEndpoints(this IEndpointRouteBuilder app)
    {
        // (two parameters, so this binds as a minimal-API handler whose IResult is
        // executed — a bare (HttpContext) => Task<IResult> would bind as a
        // RequestDelegate and silently drop the result)
        app.MapPost("/mcp", HandleAsync).AllowAnonymous();
        app.MapMethods("/mcp", new[] { "GET", "DELETE" }, (HttpContext ctx) =>
        {
            ctx.Response.Headers.Allow = "POST";
            return Results.Json(new
            {
                jsonrpc = "2.0",
                id = (object?)null,
                error = new { code = -32000, message = "Method not allowed: ChatBranch's MCP endpoint accepts POST only." },
            }, statusCode: StatusCodes.Status405MethodNotAllowed);
        }).AllowAnonymous();
    }

    private static async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        // Only API keys: an AI app holds a key, never a dashboard session.
        if (ctx.User.Identity?.IsAuthenticated != true || !ApiKeyAuth.IsApiKey(ctx.User))
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"ChatBranch\"";
            return Results.Json(Error(null, -32001,
                "Unauthorized: send a ChatBranch API key as 'Authorization: Bearer cbk_…'. " +
                "Create one under Developers → API keys."), statusCode: StatusCodes.Status401Unauthorized);
        }

        JsonElement body;
        try
        {
            body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch
        {
            return Results.Json(Error(null, -32700, "Parse error: the body is not valid JSON."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tools = await ToolContext.CreateAsync(ctx, ctx.User, AuditSources.Mcp);
        if (body.ValueKind == JsonValueKind.Array)
        {
            var replies = new List<object>();
            foreach (var msg in body.EnumerateArray())
                if (await DispatchAsync(ctx, tools, msg) is { } reply) replies.Add(reply);
            return replies.Count == 0 ? Results.Accepted() : Results.Json(replies);
        }

        var single = await DispatchAsync(ctx, tools, body);
        return single == null ? Results.Accepted() : Results.Json(single);
    }

    /// <summary>One JSON-RPC message → its response, or null for a notification.</summary>
    private static async Task<object?> DispatchAsync(HttpContext http, ToolContext tools, JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object
            || !msg.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String)
            return Error(IdOf(msg), -32600, "Invalid request: expected a JSON-RPC 2.0 object with a method.");

        var method = m.GetString()!;
        var hasId = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        object? id = hasId ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetDecimal() : idEl.GetString()) : null;
        var p = msg.TryGetProperty("params", out var pe) && pe.ValueKind == JsonValueKind.Object
            ? pe : JsonDocument.Parse("{}").RootElement;

        // Notifications (no id) get no response body — 202 at the transport level.
        if (!hasId) return null;

        switch (method)
        {
            case "initialize":
            {
                var asked = p.TryGetProperty("protocolVersion", out var pv) ? pv.GetString() : null;
                var version = asked != null && SupportedVersions.Contains(asked) ? asked : SupportedVersions[0];
                http.Response.Headers["Mcp-Session-Id"] =
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                return Result(id, new
                {
                    protocolVersion = version,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "ChatBranch", title = "ChatBranch", version = ServerVersion },
                    instructions = Instructions,
                });
            }
            case "ping":
                return Result(id, new { });
            case "tools/list":
                return Result(id, new { tools = ToolRegistry.AllowedFor(tools).Select(Describe) });
            case "tools/call":
            {
                var name = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? "" : "";
                if (ToolRegistry.Find(name) == null)
                    return Error(id, -32602, $"Unknown tool: '{name}'. Call tools/list for the catalogue.");
                var args = p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a : JsonDocument.Parse("{}").RootElement;
                var outcome = await ToolRegistry.ExecuteAsync(tools, name, args, http.RequestAborted);
                if (!outcome.Ok)
                    return Result(id, new
                    {
                        content = new[] { new { type = "text", text = outcome.Error ?? "The tool failed." } },
                        isError = true,
                    });
                var structured = JsonSerializer.SerializeToElement(outcome.Data);
                if (structured.ValueKind != JsonValueKind.Object)
                    structured = JsonSerializer.SerializeToElement(new { result = structured });
                return Result(id, new
                {
                    content = new[] { new { type = "text", text = structured.GetRawText() } },
                    structuredContent = structured,
                    isError = false,
                });
            }
            default:
                return Error(id, -32601, $"Method not found: {method}.");
        }
    }

    /// <summary>MCP tool descriptor, with behaviour hints from the registry.</summary>
    internal static object Describe(IChatBranchTool t) => new
    {
        name = t.Name,
        title = t.Title,
        description = t.Description,
        inputSchema = t.InputSchema,
        annotations = new
        {
            title = t.Title,
            readOnlyHint = !t.IsWrite,
            destructiveHint = false,
            idempotentHint = !t.IsWrite || t.Name is "set_conversation_status" or "assign_conversation"
                or "update_contact",
            openWorldHint = t.Name == "send_message",
        },
    };

    private static object Result(object? id, object result) => new { jsonrpc = "2.0", id, result };

    private static object Error(object? id, int code, string message) =>
        new { jsonrpc = "2.0", id, error = new { code, message } };

    private static object? IdOf(JsonElement msg) =>
        msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("id", out var i)
            ? i.ValueKind == JsonValueKind.Number ? i.GetDecimal() : i.ValueKind == JsonValueKind.String ? i.GetString() : null
            : null;
}
