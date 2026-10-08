using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Mcp;

/// <summary>One tool an external MCP server advertised (cached by the last Test).</summary>
public class McpToolInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("input_schema")] public JsonElement? InputSchema { get; set; }
    /// <summary>The server's own readOnlyHint — reads run directly, everything
    /// else waits for the person's confirmation.</summary>
    [JsonPropertyName("read_only")] public bool ReadOnly { get; set; }
}

/// <summary>A registered external MCP server (Setting <c>mcp_servers</c>, JSON).</summary>
public class McpServerRow
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
    // ---- health, written by Test -------------------------------------------
    [JsonPropertyName("status")] public string Status { get; set; } = "unknown";
    [JsonPropertyName("status_detail")] public string StatusDetail { get; set; } = "";
    [JsonPropertyName("last_ok_at")] public DateTime? LastOkAt { get; set; }
    [JsonPropertyName("server_name")] public string ServerName { get; set; } = "";
    [JsonPropertyName("tools")] public List<McpToolInfo> Tools { get; set; } = new();

    /// <summary>Tools the assistant may offer: the advertised set, narrowed by the allowlist.</summary>
    public IEnumerable<McpToolInfo> OfferedTools() =>
        ToolAllowlist.Count == 0 ? Tools : Tools.Where(t => ToolAllowlist.Contains(t.Name));
}

/// <summary>
/// The external MCP server registry, persisted as one JSON Setting row (the
/// schema has no table for it). Auth tokens live server-side and only ever leave
/// masked.
/// </summary>
public static class McpServerRegistry
{
    public const string SettingKey = "mcp_servers";

    public static List<McpServerRow> Load(ScopingService sc)
    {
        var raw = sc.SettingValue(SettingKey);
        if (string.IsNullOrWhiteSpace(raw)) return new List<McpServerRow>();
        try { return JsonSerializer.Deserialize<List<McpServerRow>>(raw) ?? new List<McpServerRow>(); }
        catch { return new List<McpServerRow>(); }
    }

    public static void Save(ScopingService sc, List<McpServerRow> rows) =>
        sc.SetSetting(SettingKey, JsonSerializer.Serialize(rows));

    /// <summary>"Google Workspace" → "google_workspace": the namespace in front of
    /// each external tool name (<c>google_workspace__read_sheet</c>).</summary>
    public static string Slug(string name)
    {
        var s = Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (s.Length == 0) s = "server";
        return s.Length > 24 ? s[..24].TrimEnd('_') : s;
    }

    public static string Mask(string token) =>
        string.IsNullOrEmpty(token)
            ? ""
            : new string('•', Math.Max(0, token.Length - 4)) + token[^Math.Min(4, token.Length)..];
}
