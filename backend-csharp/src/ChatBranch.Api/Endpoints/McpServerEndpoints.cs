using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Tools;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using ChatBranch.Data;

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
        "Messenger, Instagram, Telegram, LINE, Viber, SMS, WeChat, Zalo and website chat, with " +
        "contacts, flows, automations, an AI that answers customers, and voice calls. These tools " +
        "cover what an operator can do in the dashboard and act as the API key's owner, with that " +
        "person's permissions and entity scope. Names are domain_verb_object (contacts_*, " +
        "conversations_*, ai_*, knowledge_*, skills_*, bookings_*, templates_*, tags_*, flows_*, " +
        "automations_*, teams_*, users_*, entities_*, bindings_*, channels_*, integrations_*, calls_*, " +
        "voice_*, notifications_*, settings_*, views_*, analytics_*, activity_*, developers_*); the " +
        "original core is search_contacts, get_contact, list_conversations, get_conversation, " +
        "send_message, add_note, set_conversation_status, assign_conversation. Conversations are " +
        "addressed by channel + key (as the conversation tools return them; results carry a /chats " +
        "deep link) — a customer has one conversation per company, so when several companies you can " +
        "see talk to the same customer, pass thread_id (or company) to say which. Sends follow the normal guards — opt-outs, WhatsApp's 24-hour window, the " +
        "Messenger/Instagram human-agent window (closed to API keys), the cold-message guard — and " +
        "a refusal says why. Tools marked destructive need \"confirm\": true — ask the person first. " +
        "Everything you do is recorded in the ChatBranch activity log under source mcp.";


    /// <summary>Where the MCP endpoint answers — the first is the one to hand out.</summary>
    public static readonly string[] Paths = { "/api/mcp", "/mcp" };

    /// <summary>True for a request to the MCP endpoint (either path).</summary>
    public static bool IsMcpPath(string? path) =>
        Paths.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase)
                       || (path ?? "").StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));

    public static void MapMcpServerEndpoints(this IEndpointRouteBuilder app)
    {
        // (two parameters, so this binds as a minimal-API handler whose IResult is
        // executed — a bare (HttpContext) => Task<IResult> would bind as a
        // RequestDelegate and silently drop the result)
        // Served at /mcp AND /api/mcp: a single-origin deployment usually proxies
        // only /api/ to the API (deploy/nginx-chatbranch.conf), where a bare /mcp
        // reaches the dashboard and 404s. /api/mcp is the URL the dashboard shows.
        foreach (var path in Paths)
        {
            app.MapPost(path, HandleAsync).AllowAnonymous();
            app.MapMethods(path, new[] { "GET", "DELETE" }, (HttpContext ctx) =>
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
    }

    private static async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        // An AI app, never a dashboard session: either a personal API key, or a
        // connection the person approved by signing in (McpOAuth — the token acts as
        // them and is marked auth_source=api, which is what IsApiKey reads).
        if (ctx.User.Identity?.IsAuthenticated != true || !ApiKeyAuth.IsApiKey(ctx.User))
        {
            // RFC 9728: the resource_metadata link is what lets a client offer "Connect"
            // instead of asking the person to find and paste a key.
            var metadata = $"{MessagingAccountTransport.PublicBaseUrl(null, ctx.RequestServices.GetRequiredService<ScopingService>(), ctx)}"
                           + "/.well-known/oauth-protected-resource";
            ctx.Response.Headers.WWWAuthenticate =
                $"Bearer realm=\"ChatBranch\", resource_metadata=\"{metadata}\"";
            return Results.Json(Error(null, -32001,
                "Unauthorized. Connect this app by signing in (your client will offer it), or send a "
                + "ChatBranch API key as 'Authorization: Bearer cbk_…' from Developers → API keys."),
                statusCode: StatusCodes.Status401Unauthorized);
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
            {
                var cursor = p.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null;
                List<IChatBranchTool> page;
                string? next;
                try { (page, next) = Page(ToolRegistry.AllowedFor(tools).ToList(), cursor, PageSize); }
                catch (ArgumentException) { return Error(id, -32602, "Invalid params: unknown cursor."); }
                return next == null
                    ? Result(id, new { tools = page.Select(Describe) })
                    : Result(id, new { tools = page.Select(Describe), nextCursor = next });
            }
            case "tools/call":
            {
                var name = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? "" : "";
                if (ToolRegistry.Find(name) == null)
                    return Error(id, -32602, $"Unknown tool: '{name}'. Call tools/list for the catalogue.");
                var args = p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
                    ? a : JsonDocument.Parse("{}").RootElement;
                tools.Images.Clear();   // one context serves a whole batch
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
                // the JSON first, then each picture as its own image block behind a one-line label
                var content = new List<object>
                {
                    new { type = "text", text = JsonSerializer.Serialize(structured, ToolResults.Wire) },
                };
                foreach (var img in tools.Images)
                {
                    content.Add(new { type = "text", text = img.Label });
                    content.Add(new { type = "image", data = Convert.ToBase64String(img.Data), mimeType = img.Mime });
                }
                return Result(id, new { content, structuredContent = structured, isError = false });
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
            destructiveHint = t.IsWrite && t.Destructive,
            idempotentHint = t.Idempotent,
            openWorldHint = t.OpenWorld,
        },
    };

    /// <summary>Tools per tools/list page. The whole catalogue fits one page by
    /// default (some clients never follow nextCursor); set MCP_TOOLS_PAGE_SIZE to page it.</summary>
    public static int PageSize =>
        int.TryParse(Environment.GetEnvironmentVariable("MCP_TOOLS_PAGE_SIZE"), out var n) && n > 0 ? n : 500;

    /// <summary>One page of the list from an opaque cursor (the next offset, base64).</summary>
    public static (List<T> Items, string? NextCursor) Page<T>(IReadOnlyList<T> all, string? cursor, int size)
    {
        var offset = 0;
        if (!string.IsNullOrEmpty(cursor))
        {
            try { offset = int.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor))); }
            catch { throw new ArgumentException("Invalid cursor."); }
        }
        offset = Math.Clamp(offset, 0, all.Count);
        var items = all.Skip(offset).Take(size).ToList();
        var next = offset + items.Count < all.Count
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((offset + items.Count).ToString()))
            : null;
        return (items, next);
    }

    private static object Result(object? id, object result) => new { jsonrpc = "2.0", id, result };

    private static object Error(object? id, int code, string message) =>
        new { jsonrpc = "2.0", id, error = new { code, message } };

    private static object? IdOf(JsonElement msg) =>
        msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("id", out var i)
            ? i.ValueKind == JsonValueKind.Number ? i.GetDecimal() : i.ValueKind == JsonValueKind.String ? i.GetString() : null
            : null;
}
