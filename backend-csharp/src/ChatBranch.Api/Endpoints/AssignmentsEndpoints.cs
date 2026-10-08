using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The Account assignment module's write that was missing from the port:
/// <c>POST /api/whatsapp/assignments/rebalance {channel}</c> — spread every
/// UNPINNED contact→account assignment of that channel evenly across the
/// channel's eligible accounts (configured, not disconnected/banned). Pinned
/// rows never move but DO count toward their account's load, so the result is
/// as even as the pins allow. Moves are minimal: a contact already on an
/// eligible account under its share stays put.
///
/// WhatsApp mirrors the conversational routing rule: when any conversation-role
/// number is eligible, only conversation numbers receive contacts (the business
/// reminder number is a template cannon, not a conversation home).
///
/// Gate = the <c>account_assignment.manage</c> capability (superadmin always).
/// </summary>
public static class AssignmentsEndpoints
{
    internal const string Capability = "account_assignment.manage";

    private static readonly string[] Ineligible = { Health.Disconnected, Health.Banned };

    public static void MapAssignmentsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/whatsapp/assignments/rebalance", async (HttpContext ctx, AppDbContext db,
            ClaimsPrincipal me) =>
        {
            if (await SettingsEndpoints.RequireCapability(db, me, Capability) is { } denied) return denied;

            var channel = Channels.WhatsApp;
            try
            {
                var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("channel", out var c)
                    && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                    channel = c.GetString()!.Trim().ToLowerInvariant();
            }
            catch (JsonException) { /* empty/garbage body → the WhatsApp default */ }

            var result = await RebalanceAsync(db, channel);
            if (result.Error != null)
                return Results.Json(new { detail = result.Error }, statusCode: 409);

            await AuditAsync(db, ctx, me, "assignment.rebalance", channel,
                $"moved {result.Moved} of {result.Movable} unpinned across {result.Eligible} account(s)");
            return Results.Ok(new
            {
                ok = true,
                channel,
                moved = result.Moved,
                total = result.Total,
                pinned = result.Pinned,
                eligible_accounts = result.Eligible,
                accounts = result.Counts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    internal sealed record RebalanceResult(int Moved, int Movable, int Total, int Pinned, int Eligible,
        Dictionary<int, int> Counts, string? Error = null);

    /// <summary>The rebalance itself — public to the assembly so it is unit-testable.</summary>
    internal static async Task<RebalanceResult> RebalanceAsync(AppDbContext db, string channel)
    {
        var accounts = await db.ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == channel).OrderBy(a => a.Id).ToListAsync();
        var eligible = accounts
            .Where(a => WhatsAppSettingsEndpoints.IsConfigured(a) && !Ineligible.Contains(a.HealthStatus))
            .ToList();
        if (channel == Channels.WhatsApp)
        {
            var conversation = eligible
                .Where(a => WhatsAppSettingsEndpoints.AccountRole(a) == "conversation").ToList();
            if (conversation.Count > 0) eligible = conversation;
        }

        var rows = await db.AccountAssignments.Where(a => a.Channel == channel)
            .OrderBy(a => a.ContactId).ToListAsync();
        var pinned = rows.Where(r => r.Pinned).ToList();
        var movable = rows.Where(r => !r.Pinned).ToList();

        Dictionary<int, int> Counts() => accounts.ToDictionary(a => a.Id, a => rows.Count(r => r.AccountId == a.Id));

        if (eligible.Count == 0)
            return new RebalanceResult(0, movable.Count, rows.Count, pinned.Count, 0, Counts(),
                $"No healthy {channel} account to distribute contacts across — link or reconnect one first.");

        // Water-fill: hand each movable slot to the currently lightest eligible
        // account (pins included in the load), ties by account id.
        var load = eligible.ToDictionary(a => a.Id, a => pinned.Count(p => p.AccountId == a.Id));
        var quota = eligible.ToDictionary(a => a.Id, _ => 0);
        for (var i = 0; i < movable.Count; i++)
        {
            var next = load.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            load[next]++;
            quota[next]++;
        }

        // Keep whoever is already on an eligible account with room left…
        var leftovers = new List<AccountAssignment>();
        foreach (var row in movable)
        {
            if (quota.TryGetValue(row.AccountId, out var room) && room > 0) quota[row.AccountId] = room - 1;
            else leftovers.Add(row);
        }
        // …and move the rest to wherever room remains.
        var now = DateTime.UtcNow;
        foreach (var row in leftovers)
        {
            var target = quota.Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            quota[target]--;
            row.AccountId = target;
            row.UpdatedAt = now;
        }
        if (leftovers.Count > 0) await db.SaveChangesAsync();

        return new RebalanceResult(leftovers.Count, movable.Count, rows.Count, pinned.Count,
            eligible.Count, Counts());
    }

    private static async Task AuditAsync(AppDbContext db, HttpContext ctx, ClaimsPrincipal me,
        string action, string target, string detail)
    {
        try
        {
            var username = me.Identity?.Name ?? "anonymous";
            var userId = await db.Users.AsNoTracking().Where(u => u.Username == username)
                .Select(u => (int?)u.Id).FirstOrDefaultAsync();
            db.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                Actor = username.Length <= 150 ? username : username[..150],
                Action = action,
                Target = target,
                Detail = detail,
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch { }
    }
}
