using ChatBranch.Api.Auth;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Tools;

/// <summary>
/// Entity scope for tools, the same rule the inbox applies: superadmin sees
/// everything; an org-wide admin (and an agent with no entity assignment) sees their
/// tenant; a company admin or an agent with assignments sees their entities' subtrees plus the
/// shared "unassigned" bucket. Out-of-scope reads as "not found" — a tool never
/// confirms that something it may not show exists.
/// </summary>
public static class ToolScope
{
    /// <summary>The caller's assigned entity ids (empty = not bounded by assignment).</summary>
    public static List<int> AssignedEntityIds(AppDbContext db, string username) =>
        username.Length == 0
            ? new List<int>()
            : db.Profiles.AsNoTracking()
                .Where(p => p.User != null && p.User.Username == username)
                .SelectMany(p => p.Entities.Select(e => e.Id))
                .ToList();

    /// <summary>Entity ids the caller is bounded to, or null when they are not
    /// bounded below their tenant (superadmin, org-wide admin, unassigned agent)
    /// — <see cref="ShellScope.BoundEntityIds"/>, so a company admin is bounded.</summary>
    public static List<int>? BoundedEntityIds(ToolContext ctx) =>
        ShellScope.BoundEntityIds(ctx.Db, ctx.Services.GetRequiredService<ScopingService>(), ctx.User);

    public static async Task<bool> CanSeeThreadAsync(ToolContext ctx, Thread t, List<int>? bounded = null)
    {
        if (ctx.Role == Roles.SuperAdmin) return true;
        bounded ??= BoundedEntityIds(ctx);
        if (bounded != null)
            return t.EntityId is not int e || bounded.Contains(e);
        if (t.EntityId is int eid && Caller.TenantId(ctx.User) is int tid)
            return await ctx.Db.Entities.AsNoTracking().AnyAsync(x => x.Id == eid && x.TenantId == tid);
        return true;
    }

    /// <summary>
    /// The ONE conversation the caller means, if it exists AND they may see it — the same
    /// rule as the dashboard (<see cref="ThreadResolver"/>): a customer has one conversation
    /// per company, so when the caller sees several, <paramref name="threadId"/> or
    /// <paramref name="company"/> must say which (else a 409 listing the candidates).
    /// </summary>
    public static async Task<Thread> FindThreadAsync(ToolContext ctx, string channel, string key,
        int? threadId = null, string? company = null)
    {
        channel = channel.Trim().ToLowerInvariant();
        key = key.Trim();
        var sc = ctx.Services.GetRequiredService<ScopingService>();
        var pick = await ThreadResolver.ResolveAsync(ctx.Db, sc, ctx.User, channel,
            ThreadResolver.KeyVariants(channel, key), threadId, company);
        if (pick.Outcome == ThreadResolver.Outcome.Ambiguous)
            throw new ToolException(
                $"{channel}:{key} has a conversation with more than one company — pass thread_id (or company): "
                + string.Join("; ", pick.Candidates.Select(c =>
                    $"thread_id {c.ThreadId} = {(c.CompanyName.Length > 0 ? c.CompanyName : "no company")}"
                    + (c.AccountName.Length > 0 ? $" via {c.AccountName}" : ""))), 409);
        var t = pick.ThreadId is int id
            ? await ctx.Db.Threads.AsNoTracking().Include(x => x.Contact).Include(x => x.Account)
                .FirstOrDefaultAsync(x => x.Id == id)
            : null;
        if (t == null || !await CanSeeThreadAsync(ctx, t))
            throw new ToolException($"No conversation {channel}:{key} in your scope.", 404);
        return t;
    }

    /// <summary>The query that pins an internal endpoint call to this conversation.</summary>
    public static Dictionary<string, string?> ThreadQuery(Thread t, IDictionary<string, string?>? more = null)
    {
        var q = new Dictionary<string, string?>(more ?? new Dictionary<string, string?>())
        {
            ["thread_id"] = t.Id.ToString(),
        };
        return q;
    }

    /// <summary>The optional disambiguators every conversation tool takes.</summary>
    public const string ThreadIdHelp =
        "Which conversation, when this customer has one with more than one company (thread_id as the conversation tools return it).";
    public const string CompanyHelp =
        "Or the company (its id or slug) whose conversation this is — a customer has one conversation per company.";

    /// <summary>
    /// Resolve an entity slug the caller asked for, refusing one outside what they
    /// may see (an agent cannot widen their view by naming another outlet).
    /// Only an explicit argument narrows: the page the person happens to be on
    /// is context for the model, never a silent filter on what tools find.
    /// </summary>
    public static async Task<Entity?> EntityAsync(ToolContext ctx, string? slug)
    {
        slug = slug?.Trim();
        if (string.IsNullOrEmpty(slug) || slug is "all" or "__all__") return null;
        var visible = await AdminEndpoints.AccessibleEntitiesAsync(ctx.Db, ctx.User);
        var ent = visible.FirstOrDefault(e => e.Slug == slug);
        if (ent == null)
        {
            // An unassigned agent is tenant-wide: any entity of their tenant is theirs.
            if (BoundedEntityIds(ctx) == null && Caller.TenantId(ctx.User) is int tid)
                ent = await ctx.Db.Entities.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Slug == slug && e.TenantId == tid);
        }
        return ent ?? throw new ToolException($"No entity '{slug}' in your scope.", 404);
    }
}
