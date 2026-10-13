using System.Globalization;
using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Bookings;
using ChatBranch.Data.Integrations;
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
/// * <c>GET /api/bookings/calendars</c> + <c>GET /api/bookings/calendar-events</c>
///   (<c>chats.view</c>) — the page's Calendar tab: the connected Google Calendar's
///   own events, READ-ONLY, ChatBranch's bookings among them linked to their
///   conversation. Which calendars: the caller's own personal grant, plus the shared
///   ones (an outlet's or the organization's, <see cref="ShellScope.ScopeLibrary"/>)
///   for <c>integrations.view</c> / <c>integrations.manage</c> holders; another
///   person's personal calendar never (<see cref="VisibleCalendarsAsync"/>).
/// </summary>
public static class BookingsEndpoints
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public static void MapBookingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bookings", async (int? contact_id, string? thread, int? thread_id, string? company,
            string? status, string? from, string? to, string? entity, string? upcoming, int? limit, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, TimeProvider clock) =>
        {
            var picked = await InboxEndpoints.EntityId(db, entity);
            var ids = ShellScope.ThreadEntityIds(db, sc, me, picked);
            var q = Scoped(db.Bookings.AsNoTracking(), ids);

            if (contact_id is int cid) q = q.Where(b => b.ContactId == cid);
            if (!string.IsNullOrWhiteSpace(thread) || thread_id != null)
            {
                var at = (thread ?? "").IndexOf(':');
                if (thread_id == null && at <= 0)
                    return Results.Json(new { detail = "thread must be <channel>:<key>." }, statusCode: 400);
                var ch = at > 0 ? thread![..at] : null;
                var key = at > 0 ? thread![(at + 1)..] : null;
                // the caller's ONE conversation (one per company: thread_id / company say which)
                var pick = await ThreadResolver.ResolveAsync(db, sc, me,
                    ch ?? await db.Threads.AsNoTracking().Where(t => t.Id == thread_id).Select(t => t.Channel)
                        .FirstOrDefaultAsync() ?? "",
                    key == null ? Array.Empty<string>() : ThreadResolver.KeyVariants(ch!, key), thread_id, company);
                if (ThreadResolver.Refusal(pick) is { } refused) return refused;
                if (pick.ThreadId is not int tid) return Results.Ok(new { bookings = Array.Empty<object>() });
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

        MapCalendarView(app);
    }

    // ---- the connected Google Calendar, read-only ----------------------------------------

    /// <summary>The longest window one calendar read covers.</summary>
    public const int MaxCalendarDays = 62;

    private static void MapCalendarView(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bookings/calendars", async (string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, BookingService bookings) =>
        {
            var rows = await VisibleCalendarsAsync(db, sc, me, await InboxEndpoints.EntityId(db, entity));
            return Results.Ok(new { calendars = rows.Select(r => ConnectionJson(r, bookings)).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.view");

        app.MapGet("/api/bookings/calendar-events", async (int? connection, string? calendar, string? from, string? to,
            string? entity, AppDbContext db, ScopingService sc, ClaimsPrincipal me, BookingService bookings,
            GoogleCalendarClient google, TimeProvider clock, CancellationToken ct) =>
        {
            var rows = await VisibleCalendarsAsync(db, sc, me, await InboxEndpoints.EntityId(db, entity));
            var row = connection is int cid ? rows.FirstOrDefault(r => r.Id == cid) : rows.FirstOrDefault();
            if (row == null)
                return connection == null
                    ? Results.Ok(new { ok = false, error = "no_calendar", detail = "No Google Calendar you can see is connected.",
                        events = Array.Empty<object>() })
                    : Results.NotFound(new { detail = "Calendar connection not found." });

            var orgTz = BuiltinSkills.OrgTimeZone(sc);
            var today = BookingAvailability.Today(clock.GetUtcNow().UtcDateTime, orgTz);
            DateOnly? Day(string? raw) => DateOnly.TryParseExact((raw ?? "").Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            if ((!string.IsNullOrWhiteSpace(from) && Day(from) == null) || (!string.IsNullOrWhiteSpace(to) && Day(to) == null))
                return Results.BadRequest(new { detail = "from and to are dates, YYYY-MM-DD." });
            var first = Day(from) ?? today;
            var last = Day(to) ?? first.AddDays(6);
            if (last < first) return Results.BadRequest(new { detail = "to must not be before from." });
            if (last.DayNumber - first.DayNumber >= MaxCalendarDays)
                return Results.BadRequest(new { detail = $"At most {MaxCalendarDays} days at a time." });

            // the account's calendars (for the picker); the one asked for must be among them
            var list = await google.ListCalendarsAsync(row, ct);
            var calendarId = string.IsNullOrWhiteSpace(calendar) ? GoogleCalendarClient.CalendarIdFor(row, null) : calendar.Trim();
            var info = list.Calendars.FirstOrDefault(c => c.Id == calendarId || (calendarId == "primary" && c.Primary));
            if (list.Ok && info == null && calendarId != "primary")
                return Results.NotFound(new { detail = "That calendar isn't one this connection can read." });
            var calendars = list.Calendars.Select(c => new { id = c.Id, name = c.Summary, primary = c.Primary }).ToList();

            var tz = BuiltinSkills.ResolveTimeZone(info?.TimeZone) ?? orgTz;
            var fromUtc = BookingAvailability.ToUtc(first.ToDateTime(TimeOnly.MinValue), tz);
            var toUtc = BookingAvailability.ToUtc(last.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
            var r = await google.ListEventsAsync(row, calendarId, fromUtc, toUtc, ct);
            var head = new Dictionary<string, object?>
            {
                ["connection"] = ConnectionJson(row, bookings),
                ["calendar_id"] = info?.Id ?? calendarId,
                ["calendars"] = calendars,
                ["from"] = first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["to"] = last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            if (!r.Ok)
            {
                head["ok"] = false;
                head["error"] = r.NotConnected ? "not_connected" : "google";
                head["detail"] = r.Error;
                head["timezone"] = tz.Id;
                head["events"] = Array.Empty<object>();
                return Results.Ok(head);
            }
            tz = BuiltinSkills.ResolveTimeZone(r.TimeZone) ?? tz;

            // ChatBranch's own bookings among the events: linked when the caller may see them
            var eventIds = r.Events.Select(e => e.Id).Distinct().ToList();
            var made = await db.Bookings.AsNoTracking()
                .Where(b => b.IntegrationId == row.Id && eventIds.Contains(b.ExternalEventId))
                .OrderByDescending(b => b.Id).ToListAsync(ct);
            var madeIds = made.Select(b => b.Id).ToList();
            var inScope = (await Scoped(db.Bookings.AsNoTracking(), ShellScope.ThreadEntityIds(db, sc, me, null))
                .Where(b => madeIds.Contains(b.Id)).Select(b => b.Id).ToListAsync(ct)).ToHashSet();
            var shown = made.Where(b => inScope.Contains(b.Id)).ToList();
            var payloads = (await PayloadsAsync(db, shown)).Zip(shown, (p, b) => (b.ExternalEventId, p))
                .GroupBy(x => x.ExternalEventId).ToDictionary(g => g.Key, g => g.First().p);
            var ours = made.Select(b => b.ExternalEventId).ToHashSet();

            head["ok"] = true;
            head["timezone"] = tz.Id;
            head["truncated"] = r.Truncated;
            head["events"] = r.Events.Select(e =>
            {
                var day = e.StartDate ?? DateOnly.FromDateTime(BookingAvailability.ToLocal(e.StartUtc!.Value, tz));
                return (object)new
                {
                    id = e.Id,
                    title = e.Title,
                    all_day = e.AllDay,
                    start = e.AllDay ? e.StartDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : BookingAvailability.OffsetIso(e.StartUtc!.Value, tz),
                    end = e.AllDay ? (e.EndDate ?? e.StartDate!.Value.AddDays(1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : e.EndUtc is DateTime end ? BookingAvailability.OffsetIso(end, tz) : null,
                    // the local day it is listed under (one that began earlier shows on the first day)
                    day = (day < first ? first : day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    location = e.Location,
                    link = e.HtmlLink.Length > 0 ? e.HtmlLink : null,
                    free = e.Free,
                    chatbranch = ours.Contains(e.Id),
                    booking = payloads.TryGetValue(e.Id, out var p) ? p : null,
                };
            }).ToList();
            return Results.Ok(head);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.view");
    }

    /// <summary>
    /// The Google Calendar connections whose events the caller may read: their OWN personal grant,
    /// plus — for <c>integrations.view</c> / <c>integrations.manage</c> holders — the shared ones
    /// (an outlet's or the organization's) the Integrations page shows them
    /// (<see cref="ShellScope.ScopeLibrary"/>: org-wide rows + their outlets' and those outlets'
    /// ancestors'). Someone else's personal calendar is never listed, admins included. Shared
    /// first (an outlet's, then the organization's), then the caller's own.
    /// </summary>
    internal static async Task<List<Integration>> VisibleCalendarsAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, int? entityId)
    {
        var uid = await db.Users.AsNoTracking().Where(u => u.Username == (me.Identity!.Name ?? ""))
            .Select(u => (int?)u.Id).FirstOrDefaultAsync();
        var rows = uid is int u
            ? await db.Integrations.AsNoTracking().Include(i => i.Entity)
                .Where(i => i.Provider == BookingService.GoogleProvider && i.OwnerUserId == u).ToListAsync()
            : new List<Integration>();
        if (await ShellScope.HasCapAsync(db, me, "integrations.view") || await ShellScope.HasCapAsync(db, me, "integrations.manage"))
            rows.AddRange(await ShellScope.ScopeLibrary(db.Integrations.AsNoTracking(), db, sc, me, entityId)
                .Include(i => i.Entity)
                .Where(i => i.Provider == BookingService.GoogleProvider && i.OwnerUserId == null).ToListAsync());
        return rows.Where(i => GoogleOAuth.Cred(i, "access_token").Length > 0)
            .GroupBy(i => i.Id).Select(g => g.First())
            .OrderBy(i => i.OwnerUserId != null ? 2 : i.EntityId == null ? 1 : 0).ThenBy(i => i.Id).ToList();
    }

    private static object ConnectionJson(Integration row, BookingService bookings) => new
    {
        id = row.Id,
        label = bookings.CalendarLabel(row),
        scope = row.OwnerUserId != null ? "personal" : row.EntityId != null ? "entity" : "organization",
        entity_name = row.Entity?.Name,
        status = row.Status,
    };

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
        // a customer has one conversation per company: the id says which one
        return rows.Select(b =>
        {
            var tz = BookingService.TimeZoneFor(b.Timezone, TimeZoneInfo.Utc);
            return (object)new
            {
                id = b.Id,
                contact_id = b.ContactId,
                contact_name = contacts.TryGetValue(b.ContactId, out var n) ? n : "",
                thread = b.ThreadId is int tid && threads.TryGetValue(tid, out var tk) ? tk : null,
                thread_id = b.ThreadId,
                start = BookingAvailability.OffsetIso(b.StartUtc, tz),
                end = BookingAvailability.OffsetIso(b.EndUtc, tz),
                timezone = b.Timezone,
                label = BookingService.LabelOf(b),
                title = b.Title,
                status = b.Status,
                source = b.Source,
                calendar_label = b.CalendarLabel,
                event_link = b.EventLink.Length > 0 ? b.EventLink : null,
                // the video meeting made with it (zoom | google_meet | link) — while it stands
                meeting_provider = b.MeetingUrl.Length > 0 && b.Status != BookingStatus.Cancelled ? b.MeetingProvider : null,
                meeting_url = b.MeetingUrl.Length > 0 && b.Status != BookingStatus.Cancelled ? b.MeetingUrl : null,
                meeting_sent_at = b.MeetingSentAt is DateTime sentAt ? BookingAvailability.Utc(sentAt) : (DateTime?)null,
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
