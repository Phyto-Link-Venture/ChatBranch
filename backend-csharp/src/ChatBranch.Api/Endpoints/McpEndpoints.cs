using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Mcp;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// External MCP server registry (under /api/integrations/mcp): servers whose
/// tools the operator assistant may use. Persisted as the JSON Setting
/// <c>mcp_servers</c> (<see cref="McpServerRegistry"/>). Test is a REAL probe —
/// initialize + tools/list over Streamable HTTP (<see cref="McpClient"/>) — and
/// caches the advertised tools plus a health status; only enabled, healthy
/// servers offer tools (<see cref="ExternalMcpTools"/>). Every change is audited;
/// auth tokens never leave the server unmasked.
/// </summary>
public static class McpEndpoints
{
    private static object Payload(McpServerRow r) => new
    {
        id = r.Id,
        name = r.Name,
        url = r.Url,
        transport = r.Transport,
        enabled = r.Enabled,
        tool_allowlist = r.ToolAllowlist,
        headers = r.Headers.ToDictionary(h => h.Key, h => McpServerRegistry.Mask(h.Value)),
        auth_header = r.AuthHeader,
        auth_token_masked = McpServerRegistry.Mask(r.AuthToken),
        has_auth_token = !string.IsNullOrEmpty(r.AuthToken),
        status = string.IsNullOrEmpty(r.Status) ? "unknown" : r.Status,
        status_detail = r.StatusDetail,
        last_ok_at = r.LastOkAt,
        server_name = r.ServerName,
        tool_namespace = McpServerRegistry.Slug(r.Name),
        usable = r.Enabled && r.Status == "healthy" && r.OfferedTools().Any(),
        tools = r.Tools.Select(t => new
        {
            name = t.Name,
            title = t.Title,
            description = t.Description,
            read_only = t.ReadOnly,
            offered = r.ToolAllowlist.Count == 0 || r.ToolAllowlist.Contains(t.Name),
            qualified_name = ExternalMcpTools.Qualify(r, t.Name),
        }),
    };

    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/integrations/mcp", (ScopingService sc) =>
            Results.Ok(new { servers = McpServerRegistry.Load(sc).Select(Payload) }))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(DevelopersEndpoints.Capability);

        app.MapPost("/api/integrations/mcp", async (HttpContext ctx, ScopingService sc, AppDbContext db) =>
        {
            var body = await ReadBody(ctx);
            var rows = McpServerRegistry.Load(sc);
            var row = new McpServerRow { Id = rows.Count == 0 ? 1 : rows.Max(r => r.Id) + 1 };
            Apply(row, body);
            if (Validate(row) is string bad) return Results.Json(new { detail = bad }, statusCode: 400);
            rows.Add(row);
            McpServerRegistry.Save(sc, rows);
            AuditTrail.Write(db, "mcp_server.create", row.Name, row.Url);
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(DevelopersEndpoints.Capability)
          .OrgWideAction();   // the org-wide registry of external MCP servers

        app.MapPatch("/api/integrations/mcp/{id:int}", async (int id, HttpContext ctx, ScopingService sc,
            AppDbContext db) =>
        {
            var rows = McpServerRegistry.Load(sc);
            var row = rows.FirstOrDefault(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "not found" });
            var urlBefore = row.Url;
            Apply(row, await ReadBody(ctx));
            if (Validate(row) is string bad) return Results.Json(new { detail = bad }, statusCode: 400);
            // A new address (or new credentials) means the cached tools are stale.
            if (row.Url != urlBefore) { row.Status = "unknown"; row.Tools.Clear(); row.StatusDetail = ""; }
            McpServerRegistry.Save(sc, rows);
            AuditTrail.Write(db, "mcp_server.update", row.Name,
                $"enabled={row.Enabled}; allowlist={row.ToolAllowlist.Count}");
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(DevelopersEndpoints.Capability)
          .OrgWideAction();   // the org-wide registry of external MCP servers

        app.MapDelete("/api/integrations/mcp/{id:int}", (int id, ScopingService sc, AppDbContext db) =>
        {
            var rows = McpServerRegistry.Load(sc);
            var row = rows.FirstOrDefault(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "not found" });
            rows.Remove(row);
            McpServerRegistry.Save(sc, rows);
            AuditTrail.Write(db, "mcp_server.delete", row.Name, row.Url);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(DevelopersEndpoints.Capability)
          .OrgWideAction();   // the org-wide registry of external MCP servers

        // Live probe: initialize + tools/list. Answers 200 either way with ok/detail
        // — a failing server is a status to show, not an API error.
        app.MapPost("/api/integrations/mcp/{id:int}/test", async (int id, ScopingService sc, AppDbContext db,
            IHttpClientFactory http, CancellationToken ct) =>
        {
            var rows = McpServerRegistry.Load(sc);
            var row = rows.FirstOrDefault(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "not found" });

            bool ok;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var client = new McpClient(http.CreateClient("mcp"), row);
                await client.InitializeAsync(timeout.Token);
                row.Tools = await client.ListToolsAsync(timeout.Token);
                var info = client.ServerInfo;
                row.ServerName = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("name", out var n)
                    ? n.GetString() ?? "" : "";
                var version = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("version", out var v)
                    ? v.GetString() ?? "" : "";
                row.Status = "healthy";
                row.LastOkAt = DateTime.UtcNow;
                row.StatusDetail = $"{(row.ServerName.Length > 0 ? row.ServerName : "Server")}"
                                   + (version.Length > 0 ? $" {version}" : "")
                                   + $" · {row.Tools.Count} tool{(row.Tools.Count == 1 ? "" : "s")}";
                ok = true;
            }
            catch (Exception ex) when (ex is McpException or OperationCanceledException or InvalidOperationException
                                           or UriFormatException)
            {
                row.Status = "disconnected";
                row.StatusDetail = ex is OperationCanceledException
                    ? "The server did not answer within 20 seconds."
                    : ex.Message;
                ok = false;
            }
            McpServerRegistry.Save(sc, rows);
            AuditTrail.Write(db, "mcp_server.test", row.Name, ok ? row.StatusDetail : $"failed: {row.StatusDetail}");
            return Results.Ok(new
            {
                ok,
                detail = row.StatusDetail,
                tools = row.Tools.Select(t => t.Name),
                server = Payload(row),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(DevelopersEndpoints.Capability)
          .OrgWideAction();   // the org-wide registry of external MCP servers
    }

    private static string? Validate(McpServerRow row)
    {
        if (row.Name.Length == 0) return "A name is required.";
        if (!Uri.TryCreate(row.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "The server URL must be an http(s) address.";
        return null;
    }

    private static async Task<JsonElement> ReadBody(HttpContext ctx)
    {
        try { return await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    private static void Apply(McpServerRow row, JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object) return;
        if (b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) row.Name = n.GetString()!.Trim();
        if (b.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) row.Url = u.GetString()!.Trim();
        if (b.TryGetProperty("transport", out var t) && t.ValueKind == JsonValueKind.String) row.Transport = t.GetString()!.Trim();
        if (b.TryGetProperty("enabled", out var e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False)) row.Enabled = e.GetBoolean();
        if (b.TryGetProperty("auth_header", out var ah) && ah.ValueKind == JsonValueKind.String) row.AuthHeader = ah.GetString()!.Trim();
        if (b.TryGetProperty("auth_token", out var at) && at.ValueKind == JsonValueKind.String) row.AuthToken = at.GetString()!;
        if (b.TryGetProperty("tool_allowlist", out var ta) && ta.ValueKind == JsonValueKind.Array)
            row.ToolAllowlist = ta.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        if (b.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            // A masked value coming back unchanged keeps the stored secret.
            var next = new Dictionary<string, string>();
            foreach (var p in h.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
            {
                var v = p.Value.GetString()!;
                next[p.Name] = v.Contains('•') && row.Headers.TryGetValue(p.Name, out var old) ? old : v;
            }
            row.Headers = next;
        }
    }
}
