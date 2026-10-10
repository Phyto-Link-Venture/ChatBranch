using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Who may own a conversation — the ONE rule behind the assignee picker
/// (<c>GET /api/messaging/agents?channel=&amp;key=</c>) and the assign route
/// (<c>POST /api/messaging/threads/{channel}/{key}/assign</c>): an ACTIVE login
/// of the conversation's tenant who can see it — superadmins and admins always,
/// an agent or custom role when their role holds <c>chats.view</c> and the
/// conversation's entity sits in their assigned subtrees (no assignment = the
/// tenant view; an unassigned conversation is everyone's). A team's members come
/// first when the conversation has a team.
/// </summary>
internal static class Assignees
{
    internal sealed record Candidate(int UserId, string Username, string Role, bool InTeam);

    internal static async Task<List<Candidate>> EligibleAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, Thread? thread)
    {
        var callerTenant = int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : (int?)null;
        int? tenant = callerTenant;
        if (thread?.EntityId is int teid)
            tenant = await db.Entities.AsNoTracking().Where(e => e.Id == teid).Select(e => (int?)e.TenantId)
                .FirstOrDefaultAsync() ?? callerTenant;
        if (tenant == null && ShellScope.RoleOf(me) != Roles.SuperAdmin) return new List<Candidate>();

        var profiles = await db.Profiles.AsNoTracking().Include(p => p.User).Include(p => p.Entities)
            .Where(p => p.User != null && p.User.IsActive).ToListAsync();
        var team = thread?.TeamId is int tid
            ? (await db.TeamMembers.AsNoTracking().Where(m => m.TeamId == tid).Select(m => m.UserId).ToListAsync()).ToHashSet()
            : new HashSet<int>();
        var canView = new Dictionary<string, bool>();
        var result = new List<Candidate>();
        foreach (var p in profiles)
        {
            var role = p.User!.IsSuperuser ? Roles.SuperAdmin : p.Role;
            if (role != Roles.SuperAdmin)
            {
                if (tenant != null && p.TenantId != tenant) continue;
                if (role != Roles.Admin)
                {
                    if (!canView.TryGetValue(role, out var ok))
                        canView[role] = ok = await AdminEndpoints.RoleHasCapabilityAsync(db, role, "chats.view");
                    if (!ok) continue;
                    if (thread?.EntityId is int eid && p.Entities.Count > 0
                        && !sc.EntitySubtreeIds(p.Entities.Select(e => e.Id)).Contains(eid))
                        continue;
                }
            }
            result.Add(new Candidate(p.UserId, p.User.Username, role, team.Contains(p.UserId)));
        }
        return result.OrderByDescending(c => c.InTeam).ThenBy(c => c.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
