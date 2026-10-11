using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Authorization;

namespace ChatBranch.Api.Tools;

/// <summary>
/// One thing an AI may do in ChatBranch — shared by the MCP server (external AI
/// apps) and the operator assistant (the in-app chat). A tool declares the
/// capability it needs and whether it changes anything; the registry enforces
/// both, and every tool additionally enforces the caller's entity scope.
/// </summary>
public interface IChatBranchTool
{
    /// <summary>snake_case, stable: it is the MCP tool name.</summary>
    string Name { get; }
    string Title { get; }
    string Description { get; }
    /// <summary>JSON Schema (type object) for the arguments.</summary>
    JsonElement InputSchema { get; }
    /// <summary>Capability the caller must hold (null = any signed-in user).</summary>
    string? Capability { get; }
    /// <summary>Changes something — the assistant asks before running it.</summary>
    bool IsWrite { get; }
    /// <summary>One line a person can confirm ("Send 'On my way' to Jane on WhatsApp").</summary>
    string Summarize(JsonElement args);
    Task<object?> ExecuteAsync(ToolContext ctx, JsonElement args, CancellationToken ct);

    /// <summary>The capabilities that open this tool — ANY one of them (empty = any
    /// signed-in user). <see cref="Capability"/> is the first, for display.</summary>
    IReadOnlyList<string> AnyCapabilities { get; }
    /// <summary>Deletes, or changes many things at once — MCP callers must pass
    /// <c>confirm: true</c>; the assistant's confirm button is that confirmation.</summary>
    bool Destructive { get; }
    /// <summary>Calling it twice with the same arguments changes nothing more.</summary>
    bool Idempotent { get; }
    /// <summary>Reaches outside ChatBranch: messages or calls a customer, or calls a provider.</summary>
    bool OpenWorld { get; }
    /// <summary>Catalogue group (contacts, conversations, ai, …) — docs and the assistant's router.</summary>
    string Domain { get; }
    /// <summary>The dashboard endpoints this tool reaches (method + route TEMPLATE as
    /// mapped) — what the MCP coverage test checks against the route table.</summary>
    IReadOnlyList<RouteRef> Covers { get; }
}

/// <summary>One endpoint: HTTP method + the route template exactly as mapped.</summary>
public sealed record RouteRef(string Method, string Template)
{
    public override string ToString() => $"{Method} {Template}";

    /// <summary>"POST /api/x/{id:int}" → a RouteRef.</summary>
    public static RouteRef Parse(string s)
    {
        var i = s.IndexOf(' ');
        return new RouteRef(s[..i].Trim().ToUpperInvariant(), s[(i + 1)..].Trim());
    }
}

/// <summary>A tool refusing with a reason the model (and the person) can read.</summary>
public sealed class ToolException : Exception
{
    public int Status { get; }
    public ToolException(string message, int status = 400) : base(message) => Status = status;
}

public sealed record ToolLink(string Label, string Href);

/// <summary>Who is calling, how (mcp | assistant), and what they may do.</summary>
public sealed class ToolContext
{
    public required HttpContext Http { get; init; }
    public required ClaimsPrincipal User { get; init; }
    /// <summary>mcp | assistant — the audit source of everything this call does.</summary>
    public required string Source { get; init; }
    public required HashSet<string> Capabilities { get; init; }
    /// <summary>The entity the caller is looking at (assistant page context), if any —
    /// told to the model; tools narrow only on an explicit `entity` argument.</summary>
    public string? EntitySlug { get; init; }
    /// <summary>Navigation suggestions the tools produced (assistant chips).</summary>
    public List<ToolLink> Links { get; } = new();

    public IServiceProvider Services => Http.RequestServices;
    public AppDbContext Db => Http.RequestServices.GetRequiredService<AppDbContext>();
    public string Username => Caller.Username(User);
    public string Role => Caller.Role(User);

    public static async Task<ToolContext> CreateAsync(HttpContext http, ClaimsPrincipal user, string source,
        string? entitySlug = null)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        return new ToolContext
        {
            Http = http,
            User = user,
            Source = source,
            Capabilities = await ApiKeyAuth.EffectiveCapabilitiesAsync(db, user),
            EntitySlug = string.IsNullOrWhiteSpace(entitySlug) || entitySlug is "all" or "__all__"
                ? null : entitySlug,
        };
    }

    /// <summary>Call one of the app's own endpoints as this user (see <see cref="InternalApi"/>).</summary>
    public Task<InternalApi.Response> CallAsync(string method, string route,
        IDictionary<string, string>? routeValues = null, object? body = null,
        IDictionary<string, string?>? query = null, CancellationToken ct = default,
        InternalApi.FormBody? form = null) =>
        InternalApi.InvokeAsync(Http, User, method, route, routeValues, body, query, ct, form);

    /// <summary>Endpoint answered with an error → surface the handler's own reason.</summary>
    public static InternalApi.Response Require(InternalApi.Response r)
    {
        if (!r.Ok) throw new ToolException(r.Detail(), r.Status);
        return r;
    }
}

/// <summary>Convenience base: schema from a JSON literal, typed argument readers.</summary>
public abstract class ChatBranchTool : IChatBranchTool
{
    private JsonElement? _schema;

    public abstract string Name { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    protected abstract string Schema { get; }
    public virtual string? Capability => null;
    public virtual bool IsWrite => false;
    public virtual IReadOnlyList<string> AnyCapabilities =>
        Capability == null ? Array.Empty<string>() : new[] { Capability };
    public virtual bool Destructive => false;
    public virtual bool Idempotent => !IsWrite;
    public virtual bool OpenWorld => false;
    public virtual string Domain => "general";
    public virtual IReadOnlyList<RouteRef> Covers => Array.Empty<RouteRef>();

    public JsonElement InputSchema => _schema ??= JsonDocument.Parse(Schema).RootElement.Clone();

    public virtual string Summarize(JsonElement args) => Title;

    public abstract Task<object?> ExecuteAsync(ToolContext ctx, JsonElement args, CancellationToken ct);

    /// <summary>"GET /api/x" strings → route refs (for <see cref="Covers"/>).</summary>
    protected static RouteRef[] R(params string[] refs) => refs.Select(RouteRef.Parse).ToArray();

    // ---- argument readers (models send loose JSON — be forgiving) ----------

    protected internal static string? Str(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        };
    }

    protected internal static string Req(JsonElement a, string key)
    {
        var s = Str(a, key)?.Trim();
        if (string.IsNullOrEmpty(s)) throw new ToolException($"'{key}' is required.");
        return s;
    }

    protected internal static int? Int(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m)) return m;
        return null;
    }

    protected internal static bool? Bool(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on" => true,
                "false" or "0" or "no" or "off" => false,
                _ => null,
            },
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n != 0 : null,
            _ => null,
        };
    }

    protected internal static bool Has(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v)
        && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined;

    protected internal static List<string>? StrList(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList();
        if (v.ValueKind == JsonValueKind.String)
            return v.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        return null;
    }

    protected static int Limit(JsonElement a, int fallback, int max) =>
        Math.Clamp(Int(a, "limit") ?? fallback, 1, max);

    protected internal static string Clip(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n] + "…";
    }
}

/// <summary>Outcome of one registry execution, already audited.</summary>
public sealed record ToolOutcome(bool Ok, object? Data, string? Error, int Status)
{
    public string Json() => JsonSerializer.Serialize(Ok ? Data : new { error = Error }, ToolResults.Wire);
}

/// <summary>
/// The tool catalogue plus the one execution path both surfaces use: unknown
/// tool → 404, missing capability / role → 403, a write on a read-only key →
/// 403, a destructive MCP call without <c>confirm: true</c> → 400, a tool's own
/// refusal → its status; every call is written to the activity log under the
/// caller's source (one row: a write tool whose endpoint already logged a domain
/// row adds none).
/// </summary>
public static class ToolRegistry
{
    public static readonly IReadOnlyList<IChatBranchTool> All =
        BuiltinTools.Create().Concat(ToolCatalog.Create()).ToList();

    private static readonly Dictionary<string, IChatBranchTool> ByName =
        All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static IChatBranchTool? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>
    /// May this caller use the tool? Capability (any of the tool's), the gate of
    /// the endpoint(s) it reaches — their capability filters (and any role policy
    /// left on them) read from the route table, the LEAST strict endpoint wins —
    /// and a scoped API key without any .manage/.send capability is read-only
    /// here exactly as it is on the REST API.
    /// </summary>
    public static bool Allowed(ToolContext ctx, IChatBranchTool tool) => Denial(ctx, tool) == null;

    public static IEnumerable<IChatBranchTool> AllowedFor(ToolContext ctx) => All.Where(t => Allowed(ctx, t));

    /// <summary>Why the caller may not use this tool, or null when they may.</summary>
    public static string? Denial(ToolContext ctx, IChatBranchTool tool)
    {
        var caps = tool.AnyCapabilities;
        if (caps.Count > 0 && !caps.Any(ctx.Capabilities.Contains))
            return $"You don't have permission to {tool.Title.ToLowerInvariant()} " +
                   (caps.Count == 1 ? $"(needs the {caps[0]} capability)." : $"(needs one of: {string.Join(", ", caps)}).");
        if (tool.IsWrite && ApiKeyAuth.IsReadOnlyKey(ctx.User))
            return "This API key is read-only — its scopes grant no write capability.";
        if (EndpointDenial(ctx, tool) is { } endpoint)
            return $"You don't have permission to {tool.Title.ToLowerInvariant()} ({endpoint}).";
        return null;
    }

    /// <summary>What one endpoint demands: the role policies still on it (none on
    /// a capability-gated route) and each <c>RequireAnyCapability</c> group (ALL of
    /// the groups, ANY capability within a group).</summary>
    public sealed record EndpointGate(IReadOnlyList<string> Policies, IReadOnlyList<IReadOnlyList<string>> CapabilityGroups);

    private static readonly ConcurrentDictionary<string, IReadOnlyList<EndpointGate>?> Gates = new();

    /// <summary>The gates of the endpoints a tool reaches (null = no route-table
    /// gate to follow: no covered routes, or one not found). Read from the route
    /// table once per tool, so tools follow the endpoints' own gates.</summary>
    public static IReadOnlyList<EndpointGate>? EndpointGates(ToolContext ctx, IChatBranchTool tool)
    {
        if (tool.Covers.Count == 0) return null;
        return Gates.GetOrAdd(tool.Name, _ =>
        {
            var source = ctx.Services.GetService<EndpointDataSource>();
            if (source == null) return null;
            var gates = new List<EndpointGate>();
            foreach (var r in tool.Covers)
            {
                var ep = InternalApi.Find(source, r.Method, r.Template);
                if (ep == null) return null;
                var policies = ep.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Select(a => a.Policy).OfType<string>()
                    .Where(p => p is AuthPolicies.AdminPlus or AuthPolicies.SuperAdmin).ToList();
                var groups = ep.Metadata.GetOrderedMetadata<RequiredCapabilities>()
                    .Select(c => (IReadOnlyList<string>)c.Any.ToList()).ToList();
                gates.Add(new EndpointGate(policies, groups));
            }
            return gates;
        });
    }

    /// <summary>Why no endpoint of the tool would let the caller through, or null:
    /// the tool is offered when ANY endpoint it reaches would (the least strict).</summary>
    private static string? EndpointDenial(ToolContext ctx, IChatBranchTool tool)
    {
        if (EndpointGates(ctx, tool) is not { Count: > 0 } gates) return null;
        string? first = null;
        foreach (var g in gates)
        {
            var why = GateDenial(ctx, g);
            if (why == null) return null;
            first ??= why;
        }
        return first;
    }

    private static string? GateDenial(ToolContext ctx, EndpointGate g)
    {
        foreach (var p in g.Policies)
            if (!RoleOk(ctx.User, p))
                return $"needs the {(p == AuthPolicies.SuperAdmin ? "superadmin" : "admin")} role";
        foreach (var group in g.CapabilityGroups)
            if (group.Count > 0 && !group.Any(ctx.Capabilities.Contains))
                return group.Count == 1 ? $"needs the {group[0]} capability" : $"needs one of: {string.Join(", ", group)}";
        return null;
    }

    private static bool RoleOk(ClaimsPrincipal user, string gate) => gate switch
    {
        AuthPolicies.SuperAdmin => Caller.Role(user) == Roles.SuperAdmin,
        AuthPolicies.AdminPlus => Caller.IsAdminPlus(user),
        _ => true,
    };

    public static async Task<ToolOutcome> ExecuteAsync(ToolContext ctx, string name, JsonElement args,
        CancellationToken ct = default)
    {
        var tool = Find(name);
        if (tool == null) return new ToolOutcome(false, null, $"Unknown tool '{name}'.", 404);
        if (args.ValueKind != JsonValueKind.Object) args = JsonDocument.Parse("{}").RootElement;

        string summary;
        try { summary = tool.Summarize(args); } catch { summary = tool.Title; }

        if (Denial(ctx, tool) is { } denied)
        {
            var why = tool.AnyCapabilities.Count > 0 && !tool.AnyCapabilities.Any(ctx.Capabilities.Contains)
                ? $"denied: missing {string.Join("|", tool.AnyCapabilities)}" : "denied: " + denied;
            Record(ctx, tool, summary, why, 403);
            return new ToolOutcome(false, null, denied, 403);
        }

        // An AI app over MCP has no confirm button: a destructive call must say so.
        if (tool.Destructive && ctx.Source == AuditSources.Mcp && ChatBranchTool.Bool(args, "confirm") != true)
        {
            const string msg = "This action is destructive (it deletes or changes things in bulk). " +
                               "Check with the person first, then call again with \"confirm\": true.";
            Record(ctx, tool, summary, "refused: confirm required", 400);
            return new ToolOutcome(false, null, msg, 400);
        }

        var before = AuditContext.Current?.DomainRows ?? 0;
        ToolOutcome outcome;
        try
        {
            var data = await tool.ExecuteAsync(ctx, args, ct);
            outcome = new ToolOutcome(true, data, null, 200);
        }
        catch (ToolException ex)
        {
            outcome = new ToolOutcome(false, null, ex.Message, ex.Status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Services.GetService<ILoggerFactory>()?.CreateLogger("ChatBranch.Tools")
                .LogError(ex, "tool {Tool} failed", tool.Name);
            outcome = new ToolOutcome(false, null, "Something went wrong running this tool.", 500);
        }

        // A write whose endpoint already logged its own domain row ("message.send")
        // needs no second row; reads and refusals get one so nothing is invisible.
        var after = AuditContext.Current?.DomainRows ?? 0;
        if (!(outcome.Ok && tool.IsWrite && after > before))
            Record(ctx, tool, summary, outcome.Ok ? "ok" : $"error: {outcome.Error}", outcome.Status);
        return outcome;
    }

    private static void Record(ToolContext ctx, IChatBranchTool tool, string summary, string detail, int status)
    {
        try
        {
            var db = ctx.Db;
            var row = AuditTrail.Add(db, $"tool.{tool.Name}", summary, detail,
                actor: ctx.Username.Length > 0 ? ctx.Username : null, source: ctx.Source);
            row.Status = status;
            db.SaveChanges();
        }
        catch { /* the trail never breaks a tool call */ }
    }
}
