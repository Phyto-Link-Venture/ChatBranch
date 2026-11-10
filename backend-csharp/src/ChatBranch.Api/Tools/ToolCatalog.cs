using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatBranch.Api.Tools;

/// <summary>
/// Everything an operator can do in the dashboard, as tools — the MCP server's
/// and Ask ChatBranch's catalogue beyond the original sixteen
/// (<see cref="BuiltinTools"/>). Almost every entry is a declarative
/// <see cref="EndpointTool"/>: a name, a precise description, its arguments,
/// the capability that opens it and the dashboard endpoint it IS — the call
/// runs that endpoint in-process as the caller (<see cref="InternalApi"/>), so
/// the dashboard's guards decide. Split by domain over the partial files.
///
/// Naming: <c>domain_verb_object</c>. What is deliberately NOT a tool lives in
/// <see cref="McpExclusions"/>, with the reason; <c>McpCoverageTests</c> keeps
/// the two honest against the route table.
/// </summary>
public static partial class ToolCatalog
{
    public static IReadOnlyList<IChatBranchTool> Create() =>
        Contacts().Concat(Conversations()).Concat(Ai()).Concat(Library()).Concat(Automation())
            .Concat(Organization()).Concat(ChannelTools()).Concat(Social()).Concat(Platform()).ToList();

    private static string[] C(params string[] caps) => caps;

    /// <summary>
    /// One endpoint-backed tool. <paramref name="write"/> defaults to "not a GET";
    /// pass false for a POST that only computes (preview, dry run, test).
    /// </summary>
    private static EndpointTool T(string group, string name, string title, string text, string method,
        string route, string[]? caps = null, ToolArg[]? args = null, bool? write = null, bool destructive = false,
        bool outside = false, bool thread = false, string? wa = null, string[]? channels = null,
        Dictionary<string, string>? query = null, Dictionary<string, object?>? body = null,
        Func<ShapeInput, object?>? shape = null, bool? idempotent = null, ToolResults.Options? options = null,
        Func<JsonElement, string>? summary = null, Func<JsonElement, JsonElement, ToolLink?>? link = null) => new()
    {
        Group = group,
        ToolName = name,
        ToolTitle = title,
        Text = text,
        Method = method,
        Route = route,
        Caps = caps ?? Array.Empty<string>(),
        Args = args ?? Array.Empty<ToolArg>(),
        Write = write ?? method != "GET",
        IsDestructive = destructive,
        Outside = outside,
        OnThread = thread,
        WaRoute = wa,
        Channels = channels,
        FixedQuery = query,
        FixedBody = body,
        Shape = shape,
        IsIdempotent = idempotent,
        ShapeOptions = options,
        Summary = summary,
        Link = link,
    };

    /// <summary>A composite tool: its own <paramref name="run"/>, reaching <paramref name="reaches"/>.</summary>
    private static EndpointTool Custom(string group, string name, string title, string text, string[] reaches,
        Func<ToolContext, JsonElement, CancellationToken, Task<object?>> run, string[]? caps = null,
        ToolArg[]? args = null, bool write = true, bool destructive = false, bool outside = false,
        bool? idempotent = null, Func<JsonElement, string>? summary = null) => new()
    {
        Group = group,
        ToolName = name,
        ToolTitle = title,
        Text = text,
        Method = RouteRef.Parse(reaches[0]).Method,
        Route = RouteRef.Parse(reaches[0]).Template,
        Reaches = reaches,
        Run = run,
        Caps = caps ?? Array.Empty<string>(),
        Args = args ?? Array.Empty<ToolArg>(),
        Write = write,
        IsDestructive = destructive,
        Outside = outside,
        IsIdempotent = idempotent,
        Summary = summary,
    };

    // ---- shared argument readers for the custom tools -------------------------

    private static string? S(JsonElement a, string k) => ChatBranchTool.Str(a, k);
    private static string Need(JsonElement a, string k) => ChatBranchTool.Req(a, k);
    private static int? I(JsonElement a, string k) => ChatBranchTool.Int(a, k);
    private static bool? B(JsonElement a, string k) => ChatBranchTool.Bool(a, k);

    private static JsonElement? El(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null
            ? v : null;

    /// <summary>A base64 file argument → bytes (data: URLs accepted), bounded.</summary>
    private static byte[] Base64(JsonElement a, string key, long maxBytes)
    {
        var raw = Need(a, key);
        var comma = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? raw.IndexOf(',') : -1;
        if (comma >= 0) raw = raw[(comma + 1)..];
        byte[] bytes;
        try { bytes = Convert.FromBase64String(raw.Trim()); }
        catch { throw new ToolException($"'{key}' is not valid base64."); }
        if (bytes.Length == 0) throw new ToolException($"'{key}' is empty.");
        if (bytes.Length > maxBytes)
            throw new ToolException($"The file is too large ({bytes.Length / 1048576.0:0.#} MB; max {maxBytes / 1048576} MB).");
        return bytes;
    }

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".pdf"] = "application/pdf", [".mp4"] = "video/mp4", [".3gp"] = "video/3gpp",
        [".mp3"] = "audio/mpeg", [".ogg"] = "audio/ogg", [".opus"] = "audio/ogg", [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac", [".amr"] = "audio/amr", [".wav"] = "audio/wav", [".txt"] = "text/plain",
        [".md"] = "text/markdown", [".csv"] = "text/csv", [".tsv"] = "text/tab-separated-values",
        [".vcf"] = "text/vcard", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword", [".xls"] = "application/vnd.ms-excel",
    };

    private static string MimeOf(string fileName) =>
        Mime.TryGetValue(Path.GetExtension(fileName), out var m) ? m : "application/octet-stream";

    private static InternalApi.FormBody Form(Dictionary<string, string?> fields, string field, string fileName,
        byte[] bytes) =>
        new(fields, new[] { new InternalApi.FormFile(field, fileName, MimeOf(fileName), bytes) });

    /// <summary>The compact shape of a JSON value (see <see cref="ToolResults"/>).</summary>
    private static JsonNode? Compact(JsonElement el) => ToolResults.Compact(el);

    /// <summary>Keep only these fields of every row in <paramref name="listKey"/> (plus the
    /// envelope's other scalars), long text clipped.</summary>
    private static object? Rows(JsonElement body, string listKey, string[] fields, int clip = 240)
    {
        if (body.ValueKind != JsonValueKind.Object) return ToolResults.Compact(body);
        var outObj = new JsonObject();
        foreach (var p in body.EnumerateObject())
        {
            if (p.Name == listKey && p.Value.ValueKind == JsonValueKind.Array)
            {
                var arr = new JsonArray();
                foreach (var row in p.Value.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) { arr.Add(JsonNode.Parse(row.GetRawText())); continue; }
                    var o = new JsonObject();
                    foreach (var f in fields)
                        if (row.TryGetProperty(f, out var v))
                            o[f] = v.ValueKind == JsonValueKind.String && v.GetString()!.Length > clip
                                ? JsonValue.Create(v.GetString()![..clip] + "…")
                                : JsonNode.Parse(v.GetRawText());
                    arr.Add(o);
                }
                outObj[p.Name] = arr;
            }
            else
                outObj[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        }
        return ToolResults.Compact(JsonSerializer.SerializeToElement(outObj));
    }

    private static ToolLink? Page(string label, string href) => new(label, href);
}
