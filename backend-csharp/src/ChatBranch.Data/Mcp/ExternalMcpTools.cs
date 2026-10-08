using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Mcp;

/// <summary>
/// The tools of ENABLED external MCP servers that the operator assistant may
/// offer the model — each namespaced <c>&lt;server&gt;__&lt;tool&gt;</c> so two servers'
/// "search" never collide, narrowed by the server's allowlist, and taken from
/// the list the last successful Test cached (a server never tested offers
/// nothing; the assistant does not probe servers mid-conversation).
/// </summary>
public static class ExternalMcpTools
{
    public sealed record ExternalTool(string QualifiedName, McpServerRow Server, McpToolInfo Tool)
    {
        public string Description =>
            $"[{Server.Name}] " + (Tool.Description.Length > 0 ? Tool.Description : Tool.Title);

        public JsonElement Parameters => Tool.InputSchema is { ValueKind: JsonValueKind.Object } s
            ? s
            : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;
    }

    public static List<ExternalTool> Available(ScopingService sc)
    {
        var outv = new List<ExternalTool>();
        foreach (var server in McpServerRegistry.Load(sc).Where(s => s.Enabled && s.Status == "healthy"))
            foreach (var tool in server.OfferedTools())
                outv.Add(new ExternalTool(Qualify(server, tool.Name), server, tool));
        return outv;
    }

    /// <summary>Model-safe name: [a-zA-Z0-9_-], at most 64 characters.</summary>
    public static string Qualify(McpServerRow server, string tool)
    {
        var t = Regex.Replace(tool, "[^A-Za-z0-9_-]+", "_");
        var name = $"{McpServerRegistry.Slug(server.Name)}__{t}";
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>Run one external tool (fresh session per call). Never throws.</summary>
    public static async Task<(string Text, bool Ok)> CallAsync(HttpClient http, ExternalTool tool,
        JsonElement args, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var client = new McpClient(http, tool.Server);
            await client.InitializeAsync(timeout.Token);
            var result = await client.CallToolAsync(tool.Tool.Name, args, timeout.Token);
            var (text, isError) = McpClient.Flatten(result);
            return (text.Length > 8000 ? text[..8000] + "… (truncated)" : text, !isError);
        }
        catch (McpException ex)
        {
            return ($"error: {ex.Message}", false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("error: the server did not answer in time.", false);
        }
        catch (Exception ex)
        {
            return ($"error: {ex.Message}", false);
        }
    }
}
