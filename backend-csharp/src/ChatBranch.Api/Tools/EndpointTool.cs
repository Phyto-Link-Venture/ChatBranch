using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Tools;

/// <summary>Where an argument goes on the endpoint call.</summary>
public enum ArgIn
{
    /// <summary>Path when the route has a {param} of that name; else query for GET/DELETE, body otherwise.</summary>
    Auto,
    Path,
    Query,
    Body,
    /// <summary>An object argument whose properties become the top-level body.</summary>
    Spread,
    /// <summary>Read by the tool itself (custom Run, confirm, routing hints) — never forwarded.</summary>
    None,
}

/// <summary>One argument: its JSON schema fragment and where it is forwarded.</summary>
public sealed record ToolArg(string Name, JsonObject Schema, bool Required, ArgIn In = ArgIn.Auto, string? As = null);

/// <summary>Argument builders — the vocabulary every catalogue entry is written in.</summary>
public static class A
{
    private static JsonObject S(string type, string desc) => new() { ["type"] = type, ["description"] = desc };

    public static ToolArg Str(string name, string desc, bool req = false, string[]? oneOf = null,
        ArgIn @in = ArgIn.Auto, string? @as = null)
    {
        var s = S("string", desc);
        if (oneOf != null) s["enum"] = new JsonArray(oneOf.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        return new ToolArg(name, s, req, @in, @as);
    }

    public static ToolArg Int(string name, string desc, bool req = false, int? min = null, int? max = null,
        ArgIn @in = ArgIn.Auto, string? @as = null)
    {
        var s = S("integer", desc);
        if (min != null) s["minimum"] = min;
        if (max != null) s["maximum"] = max;
        return new ToolArg(name, s, req, @in, @as);
    }

    public static ToolArg Num(string name, string desc, bool req = false, ArgIn @in = ArgIn.Auto, string? @as = null) =>
        new(name, S("number", desc), req, @in, @as);

    public static ToolArg Bool(string name, string desc, bool req = false, ArgIn @in = ArgIn.Auto, string? @as = null) =>
        new(name, S("boolean", desc), req, @in, @as);

    public static ToolArg StrList(string name, string desc, bool req = false, string[]? oneOf = null,
        ArgIn @in = ArgIn.Auto, string? @as = null)
    {
        var items = new JsonObject { ["type"] = "string" };
        if (oneOf != null) items["enum"] = new JsonArray(oneOf.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        var s = S("array", desc);
        s["items"] = items;
        return new ToolArg(name, s, req, @in, @as);
    }

    public static ToolArg IntList(string name, string desc, bool req = false, ArgIn @in = ArgIn.Auto, string? @as = null)
    {
        var s = S("array", desc);
        s["items"] = new JsonObject { ["type"] = "integer" };
        return new ToolArg(name, s, req, @in, @as);
    }

    public static ToolArg Obj(string name, string desc, bool req = false, ArgIn @in = ArgIn.Auto, string? @as = null)
    {
        var s = S("object", desc);
        s["additionalProperties"] = true;
        return new ToolArg(name, s, req, @in, @as);
    }

    public static ToolArg Arr(string name, string desc, bool req = false, ArgIn @in = ArgIn.Auto, string? @as = null,
        string itemType = "object")
    {
        var s = S("array", desc);
        s["items"] = new JsonObject { ["type"] = itemType };
        return new ToolArg(name, s, req, @in, @as);
    }

    /// <summary>Any JSON value (an id that may be a number or a string, a nullable link…).</summary>
    public static ToolArg Any(string name, string desc, bool req = false, ArgIn @in = ArgIn.Auto, string? @as = null) =>
        new(name, new JsonObject { ["description"] = desc }, req, @in, @as);

    // ---- the common ones ------------------------------------------------------

    public static ToolArg Id(string what, string name = "id") => Int(name, $"The {what} id.", req: true);

    public static ToolArg Entity(string? desc = null, ArgIn @in = ArgIn.Auto) =>
        Str("entity", desc ?? "Entity (outlet) slug — includes its branches. Omit for every entity you can see.",
            @in: @in);

    public static ToolArg Days(string? desc = null) =>
        Int("days", desc ?? "Window in days (default 30).", min: 1, max: 365);

    public static ToolArg Limit(int fallback, int max) =>
        Int("limit", $"How many to return (default {fallback}, max {max}).", min: 1, max: max);

    public static ToolArg Page => Int("page", "Page number (1-based).", min: 1);

    public static ToolArg PageSize(int max = 100) => Int("page_size", $"Rows per page (max {max}).", min: 1, max: max);
}

/// <summary>What a result shaper gets: the endpoint's JSON, the tool's arguments, the conversation (thread tools).</summary>
public sealed record ShapeInput(JsonElement Body, JsonElement Args, ToolContext Ctx, Thread? Thread);

/// <summary>
/// A tool that IS one dashboard endpoint: the declarative form most of the
/// catalogue is written in. Arguments map onto the route's path parameters,
/// the query string or the JSON body; the call goes through
/// <see cref="InternalApi"/> as the caller, so the endpoint's own guards (role
/// policy, capability filter, conversation scope, opt-out, 24-hour and social
/// windows, cold-text guard, AI gate) decide — the tool adds only the entity
/// check (an agent can't widen their view by naming another outlet) and, for
/// conversation tools, the inbox's scope check up front.
///
/// <see cref="Run"/> replaces the single call for the few composite tools
/// (read-modify-write of a library the dashboard saves as a whole); those
/// declare what they reach in <see cref="Reaches"/>.
/// </summary>
public sealed class EndpointTool : ChatBranchTool
{
    private string? _schemaText;

    public required string ToolName { get; init; }
    public required string ToolTitle { get; init; }
    public required string Text { get; init; }
    public string Group { get; init; } = "general";
    /// <summary>Any of these opens it (empty = any signed-in user).</summary>
    public string[] Caps { get; init; } = Array.Empty<string>();
    public bool Write { get; init; }
    public bool IsDestructive { get; init; }
    public bool? IsIdempotent { get; init; }
    public bool Outside { get; init; }

    public string Method { get; init; } = "GET";
    public string Route { get; init; } = "";
    /// <summary>The WhatsApp twin of <see cref="Route"/> (the dashboard's threadPath:
    /// WhatsApp threads use /api/whatsapp/threads/{key}/…, every other channel the
    /// channel-agnostic route).</summary>
    public string? WaRoute { get; init; }
    /// <summary>Takes channel + key, checks the conversation is in the caller's scope first.</summary>
    public bool OnThread { get; init; }
    /// <summary>Only these channels (e.g. WhatsApp-only calls and templates).</summary>
    public string[]? Channels { get; init; }
    public ToolArg[] Args { get; init; } = Array.Empty<ToolArg>();
    public Dictionary<string, string>? FixedQuery { get; init; }
    public Dictionary<string, object?>? FixedBody { get; init; }
    public Func<ShapeInput, object?>? Shape { get; init; }
    public ToolResults.Options? ShapeOptions { get; init; }
    public Func<ToolContext, JsonElement, CancellationToken, Task<object?>>? Run { get; init; }
    /// <summary>Endpoints a custom <see cref="Run"/> reaches ("METHOD /route").</summary>
    public string[]? Reaches { get; init; }
    public Func<JsonElement, string>? Summary { get; init; }
    /// <summary>A navigation chip for the assistant (args, result) → link.</summary>
    public Func<JsonElement, JsonElement, ToolLink?>? Link { get; init; }

    public override string Name => ToolName;
    public override string Title => ToolTitle;
    public override string Description => Text;
    public override string? Capability => Caps.Length > 0 ? Caps[0] : null;
    public override IReadOnlyList<string> AnyCapabilities => Caps;
    public override bool IsWrite => Write;
    public override bool Destructive => IsDestructive;
    public override bool Idempotent => IsIdempotent ?? (!Write || Method is "PUT" or "DELETE");
    public override bool OpenWorld => Outside;
    public override string Domain => Group;

    private IReadOnlyList<RouteRef>? _covers;

    public override IReadOnlyList<RouteRef> Covers => _covers ??= Reaches != null
        ? Reaches.Select(RouteRef.Parse).ToList()
        : new[] { new RouteRef(Method, Route) }
            .Concat(WaRoute != null ? new[] { new RouteRef(Method, WaRoute) } : Array.Empty<RouteRef>())
            .ToList();

    protected override string Schema => _schemaText ??= BuildSchema();

    private IEnumerable<ToolArg> AllArgs()
    {
        if (OnThread)
        {
            yield return A.Str("channel", "Conversation channel (as returned by the conversation tools).", req: true,
                oneOf: Channels ?? ChannelNames, @in: ArgIn.None);
            yield return A.Str("key", "The conversation key (WhatsApp number, email address, chat id…).", req: true,
                @in: ArgIn.None);
            yield return A.Int("thread_id", ToolScope.ThreadIdHelp, @in: ArgIn.None);
            yield return A.Str("company", ToolScope.CompanyHelp, @in: ArgIn.None);
        }
        foreach (var a in Args) yield return a;
        if (IsDestructive)
            yield return A.Bool("confirm",
                "Must be true over MCP: this is destructive — confirm with the person before calling.",
                @in: ArgIn.None);
    }

    internal static readonly string[] ChannelNames =
    {
        "whatsapp", "email", "facebook", "instagram", "telegram", "website", "line", "viber", "sms",
        "wechat", "tiktok", "douyin", "zalo", "social",
    };

    private string BuildSchema()
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var a in AllArgs())
        {
            props[a.Name] = a.Schema.DeepClone();
            if (a.Required) required.Add(a.Name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;
        return schema.ToJsonString();
    }

    public override string Summarize(JsonElement a)
    {
        if (Summary != null) return Summary(a);
        var bits = new List<string>();
        if (OnThread && Str(a, "channel") is { } ch) bits.Add($"{ch}:{Str(a, "key")}");
        foreach (var name in new[] { "id", "name", "flow_id", "room", "query", "q", "text", "action", "status", "kind" })
            if (Str(a, name) is { Length: > 0 } v && bits.Count < 3) bits.Add($"{name} {Clip(v, 60)}");
        return bits.Count > 0 ? $"{Title} ({string.Join(", ", bits)})" : Title;
    }

    public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        // An explicit entity narrows; one outside the caller's view is refused.
        if (Args.Any(x => x.Name == "entity") && Str(a, "entity") is { Length: > 0 } slug)
            await ToolScope.EntityAsync(ctx, slug);

        if (Run != null) return await Run(ctx, a, ct);

        Thread? t = null;
        var route = Route;
        var rv = new Dictionary<string, string>();
        var fixedBody = FixedBody;
        var fixedQuery = FixedQuery;
        if (OnThread)
        {
            t = await ToolScope.FindThreadAsync(ctx, Req(a, "channel"), Req(a, "key"), Int(a, "thread_id"),
                Str(a, "company"));
            // the endpoint resolves exactly this conversation (one per customer per company)
            fixedQuery = new Dictionary<string, string>(FixedQuery ?? new()) { ["thread_id"] = t.Id.ToString() };
            if (Channels != null && !Channels.Contains(t.Channel))
                throw new ToolException($"This works on {string.Join(" / ", Channels)} conversations only.");
            if (t.Channel == ChatBranch.Domain.Channels.WhatsApp && WaRoute != null) route = WaRoute;
            rv["channel"] = t.Channel;
            rv["key"] = t.Key;
            // a route that doesn't carry the conversation in its path takes it in the body
            if (!PathParams(route).Contains("key") && Method != "GET")
                fixedBody = new Dictionary<string, object?>(FixedBody ?? new())
                {
                    ["channel"] = t.Channel, ["key"] = t.Key, ["thread_id"] = t.Id,
                };
        }
        var r = ToolContext.Require(await CallEndpointAsync(ctx, Method, route, a, Args, rv, fixedQuery, fixedBody, ct));
        if (t != null)
            ctx.Links.Add(new ToolLink($"Open {(t.Name.Length > 0 ? t.Name : t.Key)}",
                ToolResults.ChatLink(t.Channel, t.Key, t.Id)));
        if (Link?.Invoke(a, r.Body) is { } link) ctx.Links.Add(link);
        return Shape != null
            ? Shape(new ShapeInput(r.Body, a, ctx, t))
            : ToolResults.Compact(r.Body, ShapeOptions);
    }

    /// <summary>Path parameter names of a route template ("/a/{id:int}/{key}" → id, key).</summary>
    public static IEnumerable<string> PathParams(string template) =>
        template.Split('/').Where(p => p.StartsWith('{') && p.EndsWith('}'))
            .Select(p => p.Trim('{', '}').Split(':', '=', '?')[0]);

    /// <summary>
    /// Map the arguments onto one endpoint call and make it. Path values already
    /// resolved (a thread's channel/key) win over arguments of the same name.
    /// </summary>
    public static Task<InternalApi.Response> CallEndpointAsync(ToolContext ctx, string method, string route,
        JsonElement a, IEnumerable<ToolArg> args, Dictionary<string, string>? pathValues = null,
        Dictionary<string, string>? fixedQuery = null, Dictionary<string, object?>? fixedBody = null,
        CancellationToken ct = default)
    {
        var rv = pathValues ?? new Dictionary<string, string>();
        var pathNames = PathParams(route).ToHashSet();
        var query = new Dictionary<string, string?>();
        var body = new Dictionary<string, object?>();
        foreach (var (k, v) in fixedQuery ?? new()) query[k] = v;
        foreach (var (k, v) in fixedBody ?? new()) body[k] = v;
        var hasBody = method is "POST" or "PUT" or "PATCH" || (fixedBody?.Count ?? 0) > 0;

        foreach (var arg in args)
        {
            var target = arg.As ?? arg.Name;
            var where = arg.In;
            if (where == ArgIn.None) continue;
            if (where == ArgIn.Auto)
                where = pathNames.Contains(target) ? ArgIn.Path
                    : method is "GET" or "DELETE" ? ArgIn.Query : ArgIn.Body;
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(arg.Name, out var v)
                || v.ValueKind == JsonValueKind.Undefined)
            {
                if (where == ArgIn.Path && !rv.ContainsKey(target))
                    throw new ToolException($"'{arg.Name}' is required.");
                continue;
            }
            switch (where)
            {
                case ArgIn.Path:
                    if (!rv.ContainsKey(target))
                    {
                        var s = Scalar(v);
                        if (string.IsNullOrWhiteSpace(s)) throw new ToolException($"'{arg.Name}' is required.");
                        rv[target] = s.Trim();
                    }
                    break;
                case ArgIn.Query:
                    if (v.ValueKind != JsonValueKind.Null) query[target] = Scalar(v);
                    break;
                case ArgIn.Spread:
                    if (v.ValueKind == JsonValueKind.Object)
                        foreach (var p in v.EnumerateObject()) body[p.Name] = p.Value.Clone();
                    hasBody = true;
                    break;
                default:
                    body[target] = Normalize(arg, v);
                    hasBody = true;
                    break;
            }
        }
        foreach (var name in pathNames)
            if (!rv.ContainsKey(name)) throw new ToolException($"'{name}' is required.");
        return ctx.CallAsync(method, route, rv, hasBody ? body : null, query, ct);
    }

    /// <summary>A comma list typed as one string becomes the list the schema promised.</summary>
    private static object? Normalize(ToolArg arg, JsonElement v)
    {
        if (arg.Schema["type"]?.GetValue<string>() == "array" && v.ValueKind == JsonValueKind.String)
        {
            var parts = v.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var itemType = arg.Schema["items"]?["type"]?.GetValue<string>();
            return itemType == "integer"
                ? parts.Select(p => int.TryParse(p, out var n) ? (object?)n : p).ToList()
                : parts.Cast<object?>().ToList();
        }
        return v.Clone();
    }

    /// <summary>A JSON value as one query/path string (arrays → comma list).</summary>
    internal static string Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(",", v.EnumerateArray().Select(Scalar)),
        JsonValueKind.Null => "",
        _ => v.GetRawText(),
    };
}
