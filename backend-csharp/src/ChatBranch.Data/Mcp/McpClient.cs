using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatBranch.Data.Mcp;

/// <summary>An MCP server refused or failed a request (reason is operator-readable).</summary>
public sealed class McpException : Exception
{
    public McpException(string message) : base(message) { }
}

/// <summary>
/// A minimal Model Context Protocol client over the Streamable HTTP transport:
/// JSON-RPC 2.0 POSTs carrying <c>initialize</c> → <c>notifications/initialized</c>
/// → <c>tools/list</c> / <c>tools/call</c>. A server may answer a POST with plain
/// JSON or with an SSE stream (<c>text/event-stream</c>); both are handled — the
/// stream is read until the response matching our request id arrives.
///
/// The registry's auth header/token and extra headers ride on every request,
/// and the <c>Mcp-Session-Id</c> the server issues is echoed back.
/// </summary>
public sealed class McpClient
{
    public const string ProtocolVersion = "2025-06-18";

    private readonly HttpClient _http;
    private readonly McpServerRow _server;
    private int _nextId;
    private string? _sessionId;
    private string _protocol = ProtocolVersion;

    public JsonElement ServerInfo { get; private set; }
    public string Instructions { get; private set; } = "";

    public McpClient(HttpClient http, McpServerRow server)
    {
        _http = http;
        _server = server;
    }

    /// <summary>Open a session. Throws <see cref="McpException"/> with a readable reason.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var result = await RpcAsync("initialize", new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "ChatBranch", title = "ChatBranch", version = Version() },
        }, ct);
        if (result.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String)
            _protocol = pv.GetString() ?? ProtocolVersion;
        ServerInfo = result.TryGetProperty("serverInfo", out var si) ? si.Clone() : default;
        Instructions = result.TryGetProperty("instructions", out var ins) && ins.ValueKind == JsonValueKind.String
            ? ins.GetString() ?? "" : "";
        await NotifyAsync("notifications/initialized", ct);
    }

    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await RpcAsync("tools/list", cursor == null ? new { } : new { cursor }, ct);
            if (result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var t in arr.EnumerateArray())
                {
                    var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Length == 0) continue;
                    var readOnly = t.TryGetProperty("annotations", out var ann)
                                   && ann.ValueKind == JsonValueKind.Object
                                   && ann.TryGetProperty("readOnlyHint", out var ro)
                                   && ro.ValueKind == JsonValueKind.True;
                    tools.Add(new McpToolInfo
                    {
                        Name = name,
                        Title = t.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String
                            ? ti.GetString() ?? "" : "",
                        Description = t.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                            ? d.GetString() ?? "" : "",
                        InputSchema = t.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object
                            ? s.Clone() : null,
                        ReadOnly = readOnly,
                    });
                }
            cursor = result.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String
                ? nc.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return tools;
    }

    /// <summary>tools/call → the raw result object ({content:[…], structuredContent?, isError?}).</summary>
    public Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken ct = default) =>
        RpcAsync("tools/call", new { name, arguments }, ct);

    /// <summary>A tools/call result flattened to text for a model: the text parts, else the JSON.</summary>
    public static (string Text, bool IsError) Flatten(JsonElement result)
    {
        var isError = result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        if (result.TryGetProperty("structuredContent", out var sc) && sc.ValueKind == JsonValueKind.Object)
            return (sc.GetRawText(), isError);
        var parts = new List<string>();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var c in content.EnumerateArray())
                if (c.TryGetProperty("type", out var ty) && ty.GetString() == "text"
                    && c.TryGetProperty("text", out var tx))
                    parts.Add(tx.GetString() ?? "");
                else parts.Add(c.GetRawText());
        return (parts.Count > 0 ? string.Join("\n", parts) : result.GetRawText(), isError);
    }

    // ---- transport --------------------------------------------------------------

    private async Task<JsonElement> RpcAsync(string method, object @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        using var req = Request(new { jsonrpc = "2.0", id, method, @params });
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException("The server did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"Could not reach the server — {ex.Message}");
        }

        using (resp)
        {
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _sessionId = sid.FirstOrDefault();
            if (!resp.IsSuccessStatusCode)
            {
                var detail = resp.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "the server rejected the credentials (401)",
                    System.Net.HttpStatusCode.Forbidden => "the server refused access (403)",
                    System.Net.HttpStatusCode.NotFound => "no MCP endpoint at that URL (404)",
                    System.Net.HttpStatusCode.MethodNotAllowed =>
                        "the server does not accept Streamable HTTP POSTs (405) — legacy SSE-only servers are not supported",
                    _ => $"HTTP {(int)resp.StatusCode}",
                };
                throw new McpException($"{method} failed: {detail}.");
            }

            var media = resp.Content.Headers.ContentType?.MediaType ?? "";
            JsonElement message = media.Contains("event-stream", StringComparison.OrdinalIgnoreCase)
                ? await ReadSseAsync(resp, id, ct)
                : await ReadJsonAsync(resp, id, ct);

            if (message.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : err.GetRawText();
                throw new McpException($"{method} failed: {msg}");
            }
            if (!message.TryGetProperty("result", out var result))
                throw new McpException($"{method} returned no result.");
            return result.Clone();
        }
    }

    private async Task NotifyAsync(string method, CancellationToken ct)
    {
        using var req = Request(new { jsonrpc = "2.0", method });
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            // 202 Accepted is the spec answer; anything else is tolerated — the
            // notification is fire-and-forget.
        }
        catch (HttpRequestException) { }
    }

    private HttpRequestMessage Request(object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _server.Url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocol);
        if (_sessionId != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        foreach (var (k, v) in _server.Headers)
            if (!string.IsNullOrWhiteSpace(k)) req.Headers.TryAddWithoutValidation(k, v);
        if (!string.IsNullOrEmpty(_server.AuthToken))
        {
            var header = string.IsNullOrWhiteSpace(_server.AuthHeader) ? "Authorization" : _server.AuthHeader.Trim();
            var value = _server.AuthToken.Trim();
            if (header.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !value.Contains(' '))
                value = "Bearer " + value;
            req.Headers.Remove(header);
            req.Headers.TryAddWithoutValidation(header, value);
        }
        return req;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, int id, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement; }
        catch { throw new McpException("The server answered with something that is not JSON."); }
        // A batch answer: pick ours.
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var m in root.EnumerateArray())
                if (IsResponseTo(m, id)) return m.Clone();
        return root.Clone();
    }

    private static async Task<JsonElement> ReadSseAsync(HttpResponseMessage resp, int id, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (line.Length == 0)
            {
                if (data.Length > 0 && TryParse(data.ToString(), id, out var hit)) return hit;
                data.Clear();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]);
            }
        }
        if (data.Length > 0 && TryParse(data.ToString(), id, out var last)) return last;
        throw new McpException("The server's event stream ended without an answer.");
    }

    private static bool TryParse(string json, int id, out JsonElement message)
    {
        message = default;
        try
        {
            var el = JsonDocument.Parse(json).RootElement;
            if (!IsResponseTo(el, id)) return false;
            message = el.Clone();
            return true;
        }
        catch { return false; }
    }

    private static bool IsResponseTo(JsonElement m, int id) =>
        m.ValueKind == JsonValueKind.Object
        && m.TryGetProperty("id", out var mid)
        && ((mid.ValueKind == JsonValueKind.Number && mid.TryGetInt32(out var n) && n == id)
            || (mid.ValueKind == JsonValueKind.String && mid.GetString() == id.ToString()))
        && (m.TryGetProperty("result", out _) || m.TryGetProperty("error", out _));

    private static string Version() =>
        typeof(McpClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
