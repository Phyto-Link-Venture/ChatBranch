using ChatBranch.Api.Auth;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Tools;

/// <summary>
/// Entity scope for tools, the same rule the inbox applies: superadmin sees
/// everything; an admin (and an agent with no entity assignment) sees their
/// tenant; an agent with assignments sees their entities' subtrees plus the
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
    /// bounded below their tenant (superadmin, admin, unassigned agent).</summary>
    public static List<int>? BoundedEntityIds(ToolContext ctx)
    {
        if (Caller.IsAdminPlus(ctx.User)) return null;
        var assigned = AssignedEntityIds(ctx.Db, ctx.Username);
        if (assigned.Count == 0) return null;
        var sc = ctx.Services.GetRequiredService<ScopingService>();
        return sc.EntitySubtreeIds(assigned);
    }

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

    /// <summary>The conversation, if it exists AND the caller may see it.</summary>
    public static async Task<Thread> FindThreadAsync(ToolContext ctx, string channel, string key)
    {
        channel = channel.Trim().ToLowerInvariant();
        key = key.Trim();
        var t = await ctx.Db.Threads.AsNoTracking().Include(x => x.Contact).Include(x => x.Account)
            .FirstOrDefaultAsync(x => x.Channel == channel && x.Key == key);
        if (t == null || !await CanSeeThreadAsync(ctx, t))
            throw new ToolException($"No conversation {channel}:{key} in your scope.", 404);
        return t;
    }

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
