using System.Globalization;
using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Bookings;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Calendar bookings made by the AI's "Connected app" skills (and cancelled by
/// operators):
///
/// * <c>GET /api/bookings</c> (<c>chats.view</c>) — scoped exactly like
///   conversations (<see cref="ShellScope.ThreadEntityIds"/>: admins see all,
///   agents/custom roles their assigned outlets' subtrees, plus unassigned).
///   Filters: <c>contact_id</c>, <c>thread=&lt;channel&gt;:&lt;key&gt;</c>,
///   <c>status</c> (comma list; none = every status), <c>from</c>/<c>to</c>
///   (a date in the org timezone, inclusive, or an ISO instant),
///   <c>entity</c>, <c>upcoming=1</c> (not yet ended, soonest first) or
///   <c>upcoming=0</c> (ended, newest first); default newest first.
/// * <c>POST /api/bookings/{id}/cancel</c> (<c>chats.send</c>, in scope) — an
///   operator cancels now (Google event deleted when it has one); note + audit +
///   notification through <see cref="BookingService"/>.
/// </summary>
public static class BookingsEndpoints
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public static void MapBookingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bookings", async (int? contact_id, string? thread, string? status, string? from,
            string? to, string? entity, string? upcoming, int? limit, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, TimeProvider clock) =>
        {
            var picked = await InboxEndpoints.EntityId(db, entity);
            var ids = ShellScope.ThreadEntityIds(db, sc, me, picked);
            var q = Scoped(db.Bookings.AsNoTracking(), ids);

            if (contact_id is int cid) q = q.Where(b => b.ContactId == cid);
            if (!string.IsNullOrWhiteSpace(thread))
            {
                var at = thread.IndexOf(':');
                if (at <= 0) return Results.Json(new { detail = "thread must be <channel>:<key>." }, statusCode: 400);
                var ch = thread[..at];
                var key = thread[(at + 1)..];
                var tid = await db.Threads.AsNoTracking().Where(t => t.Channel == ch && t.Key == key)
                    .Select(t => (int?)t.Id).FirstOrDefaultAsync();
                if (tid == null) return Results.Ok(new { bookings = Array.Empty<object>() });
                q = q.Where(b => b.ThreadId == tid);
            }

            var statuses = (status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant()).Where(s => s != "all").ToList();
            if (statuses.Any(s => !BookingStatus.All.Contains(s)))
                return Results.Json(new { detail = $"status must be one of {string.Join(", ", BookingStatus.All)}." },
                    statusCode: 400);
            // no status filter = every status (the dashboard filters status client-side)
            if (statuses.Count > 0) q = q.Where(b => statuses.Contains(b.Status));

            var tz = BuiltinSkills.OrgTimeZone(sc);
            if (ParseBound(from, tz, endOfDay: false) is DateTime fromUtc) q = q.Where(b => b.EndUtc > fromUtc);
            if (ParseBound(to, tz, endOfDay: true) is DateTime toUtc) q = q.Where(b => b.StartUtc < toUtc);

            var now = clock.GetUtcNow().UtcDateTime;
            var mode = (upcoming ?? "").Trim().ToLowerInvariant();
            IQueryable<Booking> ordered = mode is "1" or "true" or "yes"
                ? q.Where(b => b.EndUtc > now).OrderBy(b => b.StartUtc).ThenBy(b => b.Id)
                : mode is "0" or "false" or "no"
                    ? q.Where(b => b.EndUtc <= now).OrderByDescending(b => b.StartUtc).ThenByDescending(b => b.Id)
                    : q.OrderByDescending(b => b.StartUtc).ThenByDescending(b => b.Id);
            var rows = await ordered.Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit)).ToListAsync();
            return Results.Ok(new { bookings = await PayloadsAsync(db, rows) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.view");

        app.MapPost("/api/bookings/{id:int}/cancel", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, BookingService bookings, CancellationToken ct) =>
        {
            var ids = ShellScope.ThreadEntityIds(db, sc, me, null);
            var row = await Scoped(db.Bookings, ids).FirstOrDefaultAsync(b => b.Id == id, ct);
            if (row == null) return Results.NotFound(new { detail = "Booking not found." });
            var failure = await bookings.OperatorCancelAsync(row, me.Identity?.Name ?? "operator", ct);
            if (failure != null)
                return Results.Json(new { ok = false, detail = failure },
                    statusCode: failure.StartsWith("This booking is", StringComparison.Ordinal) ? 409 : 502);
            var fresh = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            return Results.Ok(new { ok = true, detail = "Cancelled.", booking = (await PayloadsAsync(db, new() { fresh }))[0] });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send");
    }

    // ---- shapes ---------------------------------------------------------------------

    /// <summary>The wire shape of a booking (GET /api/bookings, the cancel response).</summary>
    internal static async Task<List<object>> PayloadsAsync(AppDbContext db, List<Booking> rows)
    {
        var contactIds = rows.Select(b => b.ContactId).Distinct().ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name);
        var threadIds = rows.Where(b => b.ThreadId != null).Select(b => b.ThreadId!.Value).Distinct().ToList();
        var threads = await db.Threads.AsNoTracking().Where(t => threadIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Channel, t.Key }).ToDictionaryAsync(t => t.Id, t => $"{t.Channel}:{t.Key}");
        return rows.Select(b =>
        {
            var tz = BookingService.TimeZoneFor(b.Timezone, TimeZoneInfo.Utc);
            return (object)new
            {
                id = b.Id,
                contact_id = b.ContactId,
                contact_name = contacts.TryGetValue(b.ContactId, out var n) ? n : "",
                thread = b.ThreadId is int tid && threads.TryGetValue(tid, out var tk) ? tk : null,
                start = BookingAvailability.OffsetIso(b.StartUtc, tz),
                end = BookingAvailability.OffsetIso(b.EndUtc, tz),
                timezone = b.Timezone,
                label = BookingService.LabelOf(b),
                title = b.Title,
                status = b.Status,
                source = b.Source,
                calendar_label = b.CalendarLabel,
                event_link = b.EventLink.Length > 0 ? b.EventLink : null,
                created_by = b.CreatedBy,
                skill_id = b.SkillId,
                draft_id = b.DraftId,
                detail = b.Detail,
                created_at = BookingAvailability.Utc(b.CreatedAt),
            };
        }).ToList();
    }

    /// <summary>
    /// <c>bookings</c> on an AI-monitor draft payload: what approving the draft
    /// would book / cancel — [{type, booking_id, label, status, calendar_label,
    /// timezone, start}] from the draft context's <c>pending_actions</c>.
    /// </summary>
    internal static List<object> MonitorItems(IEnumerable<ChatBranch.Data.Ai.AiPendingAction> actions,
        IReadOnlyDictionary<int, Booking> rows) =>
        actions.Select(a =>
        {
            rows.TryGetValue(a.BookingId, out var b);
            var tz = BookingService.TimeZoneFor(b?.Timezone, TimeZoneInfo.Utc);
            return (object)new
            {
                type = a.Type,
                booking_id = a.BookingId,
                label = b == null ? "" : BookingService.LabelOf(b),
                status = b?.Status ?? "missing",
                calendar_label = b?.CalendarLabel ?? "",
                timezone = b?.Timezone ?? "",
                start = b == null ? null : BookingAvailability.OffsetIso(b.StartUtc, tz),
            };
        }).ToList();

    internal static IQueryable<Booking> Scoped(IQueryable<Booking> q, List<int>? entityIds) =>
        entityIds is null
            ? q
            : q.Where(b => b.EntityId == null || (b.EntityId != null && entityIds.Contains(b.EntityId.Value)));

    /// <summary>"2026-10-14" → that local day's start (or the next day's start for
    /// an inclusive end) in UTC; a full ISO date-time is taken as given.</summary>
    private static DateTime? ParseBound(string? raw, TimeZoneInfo tz, bool endOfDay)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.Length == 10 && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return BookingAvailability.ToUtc((endOfDay ? d.AddDays(1) : d).ToDateTime(TimeOnly.MinValue), tz);
        return SkillService.ParseInstant(s, tz)?.UtcDateTime;
    }
}
