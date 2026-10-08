using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;

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
    /// <summary>The entity the caller is looking at (assistant page context), if any.</summary>
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
        IDictionary<string, string?>? query = null, CancellationToken ct = default) =>
        InternalApi.InvokeAsync(Http, User, method, route, routeValues, body, query, ct);

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

    public JsonElement InputSchema => _schema ??= JsonDocument.Parse(Schema).RootElement.Clone();

    public virtual string Summarize(JsonElement args) => Title;

    public abstract Task<object?> ExecuteAsync(ToolContext ctx, JsonElement args, CancellationToken ct);

    // ---- argument readers (models send loose JSON — be forgiving) ----------

    protected static string? Str(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        };
    }

    protected static string Req(JsonElement a, string key)
    {
        var s = Str(a, key)?.Trim();
        if (string.IsNullOrEmpty(s)) throw new ToolException($"'{key}' is required.");
        return s;
    }

    protected static int? Int(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m)) return m;
        return null;
    }

    protected static bool Has(JsonElement a, string key) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v)
        && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined;

    protected static List<string>? StrList(JsonElement a, string key)
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

    protected static string Clip(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n] + "…";
    }
}

/// <summary>Outcome of one registry execution, already audited.</summary>
public sealed record ToolOutcome(bool Ok, object? Data, string? Error, int Status)
{
    public string Json() => JsonSerializer.Serialize(Ok ? Data : new { error = Error },
        new JsonSerializerOptions { WriteIndented = false });
}

/// <summary>
/// The tool catalogue plus the one execution path both surfaces use: unknown
/// tool → 404, missing capability → 403, a tool's own refusal → its status;
/// every call is written to the activity log under the caller's source (one
/// row: a write tool whose endpoint already logged a domain row adds none).
/// </summary>
public static class ToolRegistry
{
    public static readonly IReadOnlyList<IChatBranchTool> All = BuiltinTools.Create();

    public static IChatBranchTool? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    public static bool Allowed(ToolContext ctx, IChatBranchTool tool) =>
        tool.Capability == null || ctx.Capabilities.Contains(tool.Capability);

    public static IEnumerable<IChatBranchTool> AllowedFor(ToolContext ctx) => All.Where(t => Allowed(ctx, t));

    public static async Task<ToolOutcome> ExecuteAsync(ToolContext ctx, string name, JsonElement args,
        CancellationToken ct = default)
    {
        var tool = Find(name);
        if (tool == null) return new ToolOutcome(false, null, $"Unknown tool '{name}'.", 404);
        if (args.ValueKind != JsonValueKind.Object) args = JsonDocument.Parse("{}").RootElement;

        string summary;
        try { summary = tool.Summarize(args); } catch { summary = tool.Title; }

        if (!Allowed(ctx, tool))
        {
            var msg = $"You don't have permission to {tool.Title.ToLowerInvariant()} " +
                      $"(needs the {tool.Capability} capability).";
            Record(ctx, tool, summary, $"denied: missing {tool.Capability}", 403);
            return new ToolOutcome(false, null, msg, 403);
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
