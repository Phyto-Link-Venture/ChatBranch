using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// <c>GET /api/live?entity=</c> — what is happening RIGHT NOW, for the top bar's
/// Live chip and its popover (the dashboard polls this every 15s and refetches
/// on every inbox <c>change</c> event).
///
/// <code>
/// { active_call_count, active_calls: [...],      // calls connecting / ringing / in progress
///   waiting_count,     waiting: [...],           // conversations whose newest message is the
///                                                //   customer's, oldest wait first
///   recent: [...] }                               // the last 10 events the caller may see
/// </code>
///
/// Conversations follow the inbox scope (ShellScope — assigned entities for
/// agents, ?entity narrows) and need chats.view. The feed mixes messages from
/// those conversations with audit rows under the Activity rule
/// (<c>/api/auth/audit</c>): superadmin sees every actor, an admin their own,
/// anyone else none — AuditLog carries no entity, so it cannot be scoped finer.
/// </summary>
public static class LiveEndpoints
{
    public const int ListLimit = 8;
    public const int RecentLimit = 10;

    private static readonly string[] LiveCallStates = { "connecting", "ringing", "in_call" };

    public static void MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/live", async (string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var now = DateTime.UtcNow;
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var canChats = await ShellScope.HasCapAsync(db, me, "chats.view");

            var activeCalls = new List<object>();
            var waiting = new List<object>();
            var waitingCount = 0;
            var activeCallCount = 0;
            var recent = new List<(DateTime At, object Row)>();

            if (canChats)
            {
                var scoped = ShellScope.ScopeThreads(db.Threads.AsNoTracking(),
                    ShellScope.ThreadEntityIds(db, sc, me, entityId));

                // ---- active calls
                var calls = await scoped.Where(t => LiveCallStates.Contains(t.CallStatus))
                    .OrderByDescending(t => t.UpdatedAt).Take(ListLimit)
                    .Select(t => new
                    {
                        t.Channel, t.Key, t.Name, t.Address, t.CallStatus, t.CallRoom, t.UpdatedAt,
                        ContactName = t.Contact != null ? t.Contact.Name : null,
                    }).ToListAsync();
                activeCalls = calls.Select(c => (object)new
                {
                    channel = c.Channel,
                    key = c.Key,
                    name = Who(c.ContactName, c.Name, c.Address, c.Key),
                    status = c.CallStatus,
                    room = c.CallRoom,
                    since = Iso(c.UpdatedAt),
                    url = ShellScope.ChatUrl(c.Channel, c.Key),
                }).ToList();
                activeCallCount = await scoped.CountAsync(t => LiveCallStates.Contains(t.CallStatus));

                // ---- waiting for a reply: open conversations whose newest
                // (non-private) message is inbound. "Waiting since" is the first
                // inbound after our last outbound — how long the customer has
                // actually been left hanging, not just since their latest nudge.
                var waitingQ = scoped
                    .Where(t => t.Status != ThreadStatus.Resolved && t.Status != ThreadStatus.Snoozed)
                    .Where(t => t.Messages.Where(m => !m.Private)
                        .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                        .Select(m => m.Direction).FirstOrDefault() == MessageDirection.In);
                waitingCount = await waitingQ.CountAsync();
                var waitRows = await waitingQ
                    .Select(t => new
                    {
                        t.Id, t.Channel, t.Key, t.Name, t.Address, t.Assignee,
                        ContactName = t.Contact != null ? t.Contact.Name : null,
                        LastOut = t.Messages.Where(m => !m.Private && m.Direction == MessageDirection.Out)
                            .Max(m => (DateTime?)m.CreatedAt),
                        LastIn = t.Messages.Where(m => !m.Private && m.Direction == MessageDirection.In)
                            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                            .Select(m => new { m.Text, m.CreatedAt }).FirstOrDefault(),
                    })
                    .ToListAsync();
                // FirstIn-after-LastOut needs LastOut, so it is a second, bounded pass.
                var ids = waitRows.Select(w => w.Id).ToList();
                var inbound = await db.Messages.AsNoTracking()
                    .Where(m => ids.Contains(m.ThreadId) && !m.Private && m.Direction == MessageDirection.In)
                    .Select(m => new { m.ThreadId, m.CreatedAt }).ToListAsync();
                var inboundBy = inbound.ToLookup(m => m.ThreadId, m => m.CreatedAt);
                waiting = waitRows
                    .Select(w =>
                    {
                        var since = inboundBy[w.Id].Where(at => w.LastOut is not DateTime lo || at > lo)
                            .DefaultIfEmpty(w.LastIn?.CreatedAt ?? now).Min();
                        return new { w, since };
                    })
                    .OrderBy(x => x.since).Take(ListLimit)
                    .Select(x => (object)new
                    {
                        channel = x.w.Channel,
                        key = x.w.Key,
                        name = Who(x.w.ContactName, x.w.Name, x.w.Address, x.w.Key),
                        preview = ShellScope.Snip(x.w.LastIn?.Text, 80),
                        assignee = x.w.Assignee,
                        waiting_since = Iso(x.since),
                        waiting_seconds = (long)Math.Max(0, (now - Utc(x.since)).TotalSeconds),
                        url = ShellScope.ChatUrl(x.w.Channel, x.w.Key),
                    }).ToList();

                // ---- feed: recent messages in scope
                var msgs = await db.Messages.AsNoTracking()
                    .Where(m => !m.Private && scoped.Any(t => t.Id == m.ThreadId))
                    .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(RecentLimit)
                    .Select(m => new
                    {
                        m.Id, m.Direction, m.Source, m.Text, m.CreatedAt,
                        Channel = m.Thread!.Channel, Key = m.Thread.Key, Name = m.Thread.Name,
                        Address = m.Thread.Address,
                        ContactName = m.Thread.Contact != null ? m.Thread.Contact.Name : null,
                    }).ToListAsync();
                foreach (var m in msgs)
                {
                    var who = Who(m.ContactName, m.Name, m.Address, m.Key);
                    recent.Add((m.CreatedAt, new
                    {
                        kind = "message",
                        id = $"m{m.Id}",
                        title = m.Direction == MessageDirection.In ? who
                            : $"{SourceLabel(m.Source)} → {who}",
                        text = ShellScope.Snip(m.Text, 90),
                        direction = m.Direction,
                        channel = m.Channel,
                        at = Iso(m.CreatedAt),
                        url = ShellScope.ChatUrl(m.Channel, m.Key),
                    }));
                }

            }

            await AddAuditAsync();
            return Results.Ok(new
            {
                generated_at = Iso(now),
                active_call_count = activeCallCount,
                active_calls = activeCalls,
                waiting_count = waitingCount,
                waiting,
                recent = recent.OrderByDescending(r => r.At).Take(RecentLimit).Select(r => r.Row),
            });

            // ---- feed: audit rows under the Activity rule
            async Task AddAuditAsync()
            {
                var role = ShellScope.RoleOf(me);
                if (role is not (Roles.SuperAdmin or Roles.Admin)) return;
                var q = db.AuditLogs.AsNoTracking().AsQueryable();
                if (role != Roles.SuperAdmin)
                {
                    var username = me.Identity?.Name ?? "";
                    var uid = await db.Users.AsNoTracking().Where(u => u.Username == username)
                        .Select(u => (int?)u.Id).FirstOrDefaultAsync();
                    if (uid is not int id) return;
                    q = q.Where(a => a.UserId == id);
                }
                var rows = await q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                    .Take(RecentLimit).ToListAsync();
                foreach (var a in rows)
                    recent.Add((a.CreatedAt, new
                    {
                        kind = "audit",
                        id = $"a{a.Id}",
                        title = AuditTitle(a.Action),
                        text = ShellScope.Snip(string.Join(" · ",
                            new[] { a.Target, a.Detail }.Where(s => !string.IsNullOrWhiteSpace(s))), 90),
                        actor = a.Actor,
                        action = a.Action,
                        at = Iso(a.CreatedAt),
                        url = "/activity",
                    }));
            }
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    private static string Who(string? contactName, string name, string address, string key) =>
        !string.IsNullOrWhiteSpace(contactName) ? contactName!
            : name.Length > 0 ? name : (address.Length > 0 ? address : key);

    private static string SourceLabel(string source) => source switch
    {
        MessageSource.Ai => "AI",
        MessageSource.System => "System",
        MessageSource.Agent => "Agent",
        _ => "Sent",
    };

    /// <summary>"account.health" → "Account health", "login" → "Login".</summary>
    internal static string AuditTitle(string action)
    {
        var s = (action ?? "").Replace('.', ' ').Replace('_', ' ').Trim();
        return s.Length == 0 ? "Event" : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static DateTime Utc(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    private static string Iso(DateTime dt) => Utc(dt).ToString("o");
}
