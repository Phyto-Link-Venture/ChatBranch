using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// MCP server registry (under /api/integrations/mcp), ported from
/// integrations/mcp_views.py. The C# schema has no McpServer table yet, so the
/// registry is persisted as a JSON blob in the global Setting `mcp_servers` —
/// enough for the dashboard's list / add / edit / remove flow. The live
/// tools/list probe is not available in this build (`test` returns a clean
/// "not available" so the page degrades instead of erroring). Auth tokens are
/// stored server-side and only ever returned masked.
/// </summary>
public static class McpEndpoints
{
    private const string Key = "mcp_servers";

    private class Row
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("transport")] public string Transport { get; set; } = "streamable-http";
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("tool_allowlist")] public List<string> ToolAllowlist { get; set; } = new();
        [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; } = new();
        [JsonPropertyName("auth_header")] public string AuthHeader { get; set; } = "";
        [JsonPropertyName("auth_token")] public string AuthToken { get; set; } = "";
    }

    private static string Mask(string token) =>
        string.IsNullOrEmpty(token) ? "" : new string('•', Math.Max(0, token.Length - 4)) + token[^Math.Min(4, token.Length)..];

    private static object Payload(Row r) => new
    {
        id = r.Id,
        name = r.Name,
        url = r.Url,
        transport = r.Transport,
        enabled = r.Enabled,
        tool_allowlist = r.ToolAllowlist,
        headers = r.Headers,
        auth_header = r.AuthHeader,
        auth_token_masked = Mask(r.AuthToken),
        has_auth_token = !string.IsNullOrEmpty(r.AuthToken),
    };

    private static List<Row> Load(ScopingService sc)
    {
        var raw = sc.SettingValue(Key);
        if (string.IsNullOrWhiteSpace(raw)) return new List<Row>();
        try { return JsonSerializer.Deserialize<List<Row>>(raw) ?? new List<Row>(); }
        catch { return new List<Row>(); }
    }

    private static void Save(ScopingService sc, List<Row> rows) =>
        sc.SetSetting(Key, JsonSerializer.Serialize(rows));

    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        // GET list
        app.MapGet("/api/integrations/mcp", (ScopingService sc) =>
            Results.Ok(new { servers = Load(sc).Select(Payload) }))
            .RequireAuthorization(AuthPolicies.Authenticated);

        // POST create (admin+)
        app.MapPost("/api/integrations/mcp", async (HttpContext ctx, ScopingService sc) =>
        {
            var body = await ReadBody(ctx);
            var rows = Load(sc);
            var row = new Row { Id = rows.Count == 0 ? 1 : rows.Max(r => r.Id) + 1 };
            Apply(row, body);
            rows.Add(row);
            Save(sc, rows);
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // PATCH update (admin+)
        app.MapPatch("/api/integrations/mcp/{id:int}", async (int id, HttpContext ctx, ScopingService sc) =>
        {
            var rows = Load(sc);
            var row = rows.FirstOrDefault(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "not found" });
            Apply(row, await ReadBody(ctx));
            Save(sc, rows);
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // DELETE (admin+)
        app.MapDelete("/api/integrations/mcp/{id:int}", (int id, ScopingService sc) =>
        {
            var rows = Load(sc);
            var removed = rows.RemoveAll(r => r.Id == id);
            if (removed == 0) return Results.NotFound(new { detail = "not found" });
            Save(sc, rows);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // POST test — live probe unavailable in this build (sandbox-safe).
        app.MapPost("/api/integrations/mcp/{id:int}/test", (int id, ScopingService sc) =>
        {
            var row = Load(sc).FirstOrDefault(r => r.Id == id);
            if (row == null) return Results.NotFound(new { detail = "not found" });
            return Results.Ok(new
            {
                ok = false,
                tools = Array.Empty<string>(),
                detail = "Live MCP tools/list probe is not available in this build.",
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    private static async Task<JsonElement> ReadBody(HttpContext ctx)
    {
        try { return await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    private static void Apply(Row row, JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object) return;
        if (b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) row.Name = n.GetString()!.Trim();
        if (b.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) row.Url = u.GetString()!.Trim();
        if (b.TryGetProperty("transport", out var t) && t.ValueKind == JsonValueKind.String) row.Transport = t.GetString()!.Trim();
        if (b.TryGetProperty("enabled", out var e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False)) row.Enabled = e.GetBoolean();
        if (b.TryGetProperty("auth_header", out var ah) && ah.ValueKind == JsonValueKind.String) row.AuthHeader = ah.GetString()!.Trim();
        if (b.TryGetProperty("auth_token", out var at) && at.ValueKind == JsonValueKind.String) row.AuthToken = at.GetString()!;
        if (b.TryGetProperty("tool_allowlist", out var ta) && ta.ValueKind == JsonValueKind.Array)
            row.ToolAllowlist = ta.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
        if (b.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            row.Headers = h.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }
}
