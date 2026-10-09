using System.Security.Claims;
using System.Text;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Breakdown reports — who handled what, on which team, under which label, over
/// which channel. The existing analytics page answers "how is the business
/// doing"; this answers "how is the TEAM doing", which is the question a
/// supervisor actually asks.
///
/// Every report is downloadable as CSV from the same data, because the first
/// thing anyone does with a breakdown is put it in a spreadsheet.
/// </summary>
public static class ReportsEndpoints
{
    public static void MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/reports", async (string? days, string? entity,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var (from, entityId) = await WindowAsync(db, days, entity);
            var report = await BuildAsync(db, sc, from, entityId);
            return Results.Ok(report);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("analytics.view");

        // ---- CSV export -------------------------------------------------------
        // One report per call (?report=agents|teams|labels|channels) so the file
        // a supervisor downloads is the table they were looking at.
        app.MapGet("/api/analytics/reports.csv", async (string? report, string? days, string? entity,
            AppDbContext db, ScopingService sc) =>
        {
            var (from, entityId) = await WindowAsync(db, days, entity);
            var data = await BuildAsync(db, sc, from, entityId);
            var which = (report ?? "agents").Trim().ToLowerInvariant();

            var (header, rows) = which switch
            {
                "teams" => ("Team,Conversations,Open,Resolved,Members",
                    data.Teams.Select(t => $"{Csv(t.Name)},{t.Conversations},{t.Open},{t.Resolved},{t.Members}")),
                "labels" => ("Label,Conversations",
                    data.Labels.Select(l => $"{Csv(l.Name)},{l.Conversations}")),
                "channels" => ("Channel,Conversations,Received,Sent",
                    data.Channels.Select(c => $"{Csv(c.Name)},{c.Conversations},{c.In},{c.Out}")),
                _ => ("Agent,Conversations,Resolved,Messages sent",
                    data.Agents.Select(a => $"{Csv(a.Name)},{a.Conversations},{a.Resolved},{a.MessagesSent}")),
            };

            var csv = new StringBuilder().AppendLine(header);
            foreach (var line in rows) csv.AppendLine(line);
            var name = $"chatbranch-{which}-{DateTime.UtcNow:yyyy-MM-dd}.csv";
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", name);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("analytics.view");
    }

    // ---- the report ---------------------------------------------------------

    public record AgentRow(string Name, int Conversations, int Resolved, int MessagesSent);
    public record TeamRow(string Name, int Conversations, int Open, int Resolved, int Members);
    public record LabelRow(string Name, int Conversations);
    public record ChannelRow(string Name, int Conversations, int In, int Out);
    public record Report(int Days, List<AgentRow> Agents, List<TeamRow> Teams,
        List<LabelRow> Labels, List<ChannelRow> Channels);

    private static async Task<Report> BuildAsync(AppDbContext db, ScopingService sc,
        DateTime from, int? entityId)
    {
        // Conversations TOUCHED in the window, not created in it — a thread that
        // ran for weeks still belongs in this month's workload.
        var threads = await sc.ScopeOwned(db.Threads.AsNoTracking(), entityId, includeUnassigned: true)
            .Where(t => t.UpdatedAt >= from)
            .Select(t => new { t.Id, t.Assignee, t.TeamId, t.Labels, t.Channel, t.Status })
            .ToListAsync();
        var ids = threads.Select(t => t.Id).ToHashSet();

        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.CreatedAt >= from && !m.Private)
            .Select(m => new { m.ThreadId, m.Direction, m.Source })
            .ToListAsync();
        var mine = messages.Where(m => ids.Contains(m.ThreadId)).ToList();

        // agents — an unassigned conversation is reported as such rather than
        // dropped, because "nobody owns 40% of them" is the finding
        var agents = threads
            .GroupBy(t => t.Assignee.Length > 0 ? t.Assignee : "(unassigned)")
            .Select(g => new AgentRow(
                g.Key,
                g.Count(),
                g.Count(t => t.Status == "resolved"),
                mine.Count(m => g.Select(x => x.Id).Contains(m.ThreadId)
                                && m.Direction == MessageDirection.Out
                                && m.Source == MessageSource.Agent)))
            .OrderByDescending(a => a.Conversations).ToList();

        var teamNames = await db.Teams.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name);
        var teamSizes = await db.TeamMembers.AsNoTracking()
            .GroupBy(m => m.TeamId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        var teams = threads
            .GroupBy(t => t.TeamId)
            .Select(g => new TeamRow(
                g.Key is int id ? teamNames.GetValueOrDefault(id, $"#{id}") : "(no team)",
                g.Count(),
                g.Count(t => t.Status == "open"),
                g.Count(t => t.Status == "resolved"),
                g.Key is int tid ? teamSizes.GetValueOrDefault(tid) : 0))
            .OrderByDescending(t => t.Conversations).ToList();

        var labels = threads
            .SelectMany(t => t.Labels.Count > 0 ? t.Labels : new List<string> { "(none)" })
            .GroupBy(l => l)
            .Select(g => new LabelRow(g.Key, g.Count()))
            .OrderByDescending(l => l.Conversations).ToList();

        var channels = threads
            .GroupBy(t => t.Channel)
            .Select(g =>
            {
                var gids = g.Select(x => x.Id).ToHashSet();
                var msgs = mine.Where(m => gids.Contains(m.ThreadId)).ToList();
                return new ChannelRow(
                    g.Key,
                    g.Count(),
                    msgs.Count(m => m.Direction == MessageDirection.In),
                    msgs.Count(m => m.Direction == MessageDirection.Out));
            })
            .OrderByDescending(c => c.Conversations).ToList();

        return new Report((DateTime.UtcNow - from).Days, agents, teams, labels, channels);
    }

    private static async Task<(DateTime from, int? entityId)> WindowAsync(AppDbContext db,
        string? days, string? entity)
    {
        var n = int.TryParse(days, out var d) && d > 0 ? Math.Min(d, 365) : 30;
        return (DateTime.UtcNow.AddDays(-n), await InboxEndpoints.EntityId(db, entity));
    }

    /// <summary>
    /// CSV-quote a field. A label like `urgent, escalate` would otherwise split
    /// into two columns and silently corrupt the export.
    /// </summary>
    private static string Csv(string raw)
    {
        var v = raw ?? "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
}
