using System.Globalization;
using System.Text.Json;
using ChatBranch.Domain;

namespace ChatBranch.Data.Integrations;

/// <summary>
/// The Google Calendar calls ChatBranch makes — free/busy, create an event,
/// delete an event, and READ the events + calendar list for the Bookings page's
/// Calendar view — shared by the flow actions ("Find free slot", "Create
/// calendar event" in <see cref="IntegrationActions"/>) and the AI's calendar
/// booking tools (<see cref="Bookings.BookingService"/>).
///
/// The bearer token comes from <see cref="GoogleOAuth.AccessTokenAsync"/>
/// (lazy refresh; "" = not connected, which every result reports as
/// <c>NotConnected</c> rather than an error). Each provider call records the
/// grant's health on the integration row, so a dead grant surfaces in the UI.
/// Nothing here throws, logs a token or returns a whole provider body.
/// </summary>
public class GoogleCalendarClient
{
    public const string Base = "https://www.googleapis.com/calendar/v3";

    private readonly AppDbContext _db;
    private readonly GoogleOAuth _oauth;
    private readonly IHttpClientFactory _http;

    public GoogleCalendarClient(AppDbContext db, GoogleOAuth oauth, IHttpClientFactory http)
    {
        _db = db;
        _oauth = oauth;
        _http = http;
    }

    public sealed record FreeBusyResult(bool Ok, bool NotConnected, string Error,
        IReadOnlyList<(DateTime Start, DateTime End)> Busy);

    public sealed record EventResult(bool Ok, bool NotConnected, string Error, string Id, string HtmlLink, int Status);

    /// <summary>An event to create. Times are UTC; <see cref="TimeZone"/> is the
    /// IANA zone Google shows it in (optional).</summary>
    public sealed record EventSpec(string Summary, string Description, DateTime StartUtc, DateTime EndUtc,
        string? TimeZone = null, string? AttendeeEmail = null, string? SendUpdates = null);

    /// <summary>The calendar id to use: the caller's choice, else the connection's
    /// configured <c>calendar_id</c>, else "primary".</summary>
    public static string CalendarIdFor(Integration row, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred.Trim();
        var configured = row.Config.TryGetValue("calendar_id", out var v) ? v?.ToString() ?? "" : "";
        return configured.Trim().Length > 0 ? configured.Trim() : "primary";
    }

    public async Task<string> TokenAsync(Integration row, CancellationToken ct = default) =>
        await _oauth.AccessTokenAsync(row, ct);

    /// <summary>Busy spans (UTC) of one calendar between two instants.</summary>
    public async Task<FreeBusyResult> FreeBusyAsync(Integration row, string calendarId, DateTime fromUtc,
        DateTime toUtc, CancellationToken ct = default)
    {
        var token = await TokenAsync(row, ct);
        if (token.Length == 0)
            return new FreeBusyResult(false, true, "Google Calendar is not connected.", Array.Empty<(DateTime, DateTime)>());
        var r = await ProviderHttp.SendAsync(_http, HttpMethod.Post, $"{Base}/freeBusy", token,
            jsonBody: new Dictionary<string, object?>
            {
                ["timeMin"] = Iso(fromUtc),
                ["timeMax"] = Iso(toUtc),
                ["items"] = new[] { new Dictionary<string, object?> { ["id"] = calendarId } },
            }, ct: ct);
        RecordResult(row, r.Ok, r.Ok ? "" : r.Body);
        if (!r.Ok) return new FreeBusyResult(false, false, $"Google Calendar: {r.Body}", Array.Empty<(DateTime, DateTime)>());
        var (spans, calendarError) = BusySpans(r.Body, calendarId);
        return calendarError.Length > 0
            ? new FreeBusyResult(false, false, $"Google Calendar: {calendarError}", spans)
            : new FreeBusyResult(true, false, "", spans);
    }

    /// <summary>Create an event. <c>sendUpdates</c> goes on the query string only when given.</summary>
    public async Task<EventResult> InsertEventAsync(Integration row, string calendarId, EventSpec spec,
        CancellationToken ct = default)
    {
        var token = await TokenAsync(row, ct);
        if (token.Length == 0) return new EventResult(false, true, "Google Calendar is not connected.", "", "", 0);
        var start = new Dictionary<string, object?> { ["dateTime"] = Iso(spec.StartUtc) };
        var end = new Dictionary<string, object?> { ["dateTime"] = Iso(spec.EndUtc) };
        if (!string.IsNullOrWhiteSpace(spec.TimeZone))
        {
            start["timeZone"] = spec.TimeZone;
            end["timeZone"] = spec.TimeZone;
        }
        var body = new Dictionary<string, object?>
        {
            ["summary"] = spec.Summary,
            ["description"] = spec.Description,
            ["start"] = start,
            ["end"] = end,
        };
        if (!string.IsNullOrWhiteSpace(spec.AttendeeEmail))
            body["attendees"] = new[] { new Dictionary<string, object?> { ["email"] = spec.AttendeeEmail } };
        var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events"
                  + (string.IsNullOrWhiteSpace(spec.SendUpdates) ? "" : $"?sendUpdates={Uri.EscapeDataString(spec.SendUpdates)}");
        var r = await ProviderHttp.SendAsync(_http, HttpMethod.Post, url, token, jsonBody: body, ct: ct);
        RecordResult(row, r.Ok, r.Ok ? "" : r.Body);
        if (!r.Ok) return new EventResult(false, false, $"Google Calendar: {r.Body}", "", "", r.Status);
        return new EventResult(true, false, "", ProviderHttp.JsonField(r.Body, "id"),
            ProviderHttp.JsonField(r.Body, "htmlLink"), r.Status);
    }

    /// <summary>Delete an event. One that is already gone (404/410) counts as deleted.</summary>
    public async Task<EventResult> DeleteEventAsync(Integration row, string calendarId, string eventId,
        string? sendUpdates = null, CancellationToken ct = default)
    {
        var token = await TokenAsync(row, ct);
        if (token.Length == 0) return new EventResult(false, true, "Google Calendar is not connected.", eventId, "", 0);
        var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}"
                  + (string.IsNullOrWhiteSpace(sendUpdates) ? "" : $"?sendUpdates={Uri.EscapeDataString(sendUpdates)}");
        var r = await ProviderHttp.SendAsync(_http, HttpMethod.Delete, url, token, ct: ct);
        var gone = r.Status is 404 or 410;
        RecordResult(row, r.Ok || gone, r.Ok || gone ? "" : r.Body);
        if (!r.Ok && !gone) return new EventResult(false, false, $"Google Calendar: {r.Body}", eventId, "", r.Status);
        return new EventResult(true, false, "", eventId, "", r.Status);
    }

    // ---- reading the calendar (the Bookings page's Calendar view) ---------------------

    /// <summary>One event as the Bookings page shows it — never its description or attendees.
    /// Timed events carry UTC instants; an all-day event its local dates (end exclusive).</summary>
    public sealed record CalendarEvent(string Id, string Title, DateTime? StartUtc, DateTime? EndUtc,
        DateOnly? StartDate, DateOnly? EndDate, string Location, string HtmlLink, bool Free)
    {
        public bool AllDay => StartDate != null;
    }

    public sealed record EventsResult(bool Ok, bool NotConnected, string Error, IReadOnlyList<CalendarEvent> Events,
        string TimeZone, bool Truncated);

    /// <summary>One calendar of the connected account (calendarList).</summary>
    public sealed record CalendarInfo(string Id, string Summary, bool Primary, string TimeZone);

    public sealed record CalendarsResult(bool Ok, bool NotConnected, string Error, IReadOnlyList<CalendarInfo> Calendars);

    /// <summary>The most events one read returns (two pages of Google's 250).</summary>
    public const int MaxEvents = 500;

    /// <summary>
    /// The events of one calendar overlapping [fromUtc, toUtc), recurring ones expanded, soonest first
    /// (<c>events.list</c>, the <c>calendar.readonly</c> scope). Cancelled events are left out; only
    /// the fields the page shows are asked for. More than <see cref="MaxEvents"/> = <c>Truncated</c>.
    /// </summary>
    public async Task<EventsResult> ListEventsAsync(Integration row, string calendarId, DateTime fromUtc,
        DateTime toUtc, CancellationToken ct = default)
    {
        var token = await TokenAsync(row, ct);
        if (token.Length == 0)
            return new EventsResult(false, true, "Google Calendar is not connected.", Array.Empty<CalendarEvent>(), "", false);
        var events = new List<CalendarEvent>();
        var zone = "";
        var pageToken = "";
        for (var page = 0; page < MaxEvents / 250; page++)
        {
            var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events"
                      + $"?timeMin={Uri.EscapeDataString(Iso(fromUtc))}&timeMax={Uri.EscapeDataString(Iso(toUtc))}"
                      + "&singleEvents=true&orderBy=startTime&maxResults=250"
                      + "&fields=" + Uri.EscapeDataString(
                          "timeZone,nextPageToken,items(id,status,summary,start,end,htmlLink,location,transparency)")
                      + (pageToken.Length > 0 ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");
            var r = await ProviderHttp.SendAsync(_http, HttpMethod.Get, url, token, ct: ct);
            RecordResult(row, r.Ok, r.Ok ? "" : r.Body);
            if (!r.Ok)
                return new EventsResult(false, false, $"Google Calendar: {r.Body}", Array.Empty<CalendarEvent>(), "", false);
            var (items, tz, next) = ParseEvents(r.Body);
            events.AddRange(items);
            if (zone.Length == 0) zone = tz;
            pageToken = next;
            if (pageToken.Length == 0) return new EventsResult(true, false, "", events, zone, false);
        }
        return new EventsResult(true, false, "", events, zone, true);
    }

    /// <summary>The calendars of the connected account the page may list events of (owner, writer
    /// or reader access — a free/busy-only calendar has no events to show), primary first.</summary>
    public async Task<CalendarsResult> ListCalendarsAsync(Integration row, CancellationToken ct = default)
    {
        var token = await TokenAsync(row, ct);
        if (token.Length == 0)
            return new CalendarsResult(false, true, "Google Calendar is not connected.", Array.Empty<CalendarInfo>());
        var url = $"{Base}/users/me/calendarList?maxResults=100&fields="
                  + Uri.EscapeDataString("items(id,summary,summaryOverride,primary,accessRole,timeZone)");
        var r = await ProviderHttp.SendAsync(_http, HttpMethod.Get, url, token, ct: ct);
        RecordResult(row, r.Ok, r.Ok ? "" : r.Body);
        return r.Ok
            ? new CalendarsResult(true, false, "", ParseCalendars(r.Body))
            : new CalendarsResult(false, false, $"Google Calendar: {r.Body}", Array.Empty<CalendarInfo>());
    }

    /// <summary>The events, the calendar's timezone and the next page token out of an events.list body.</summary>
    public static (List<CalendarEvent> Events, string TimeZone, string NextPage) ParseEvents(string payload)
    {
        var list = new List<CalendarEvent>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            string Str(JsonElement e, string key) =>
                e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray())
                {
                    if (Str(it, "status") == "cancelled" || Str(it, "id").Length == 0) continue;
                    if (!it.TryGetProperty("start", out var start) || !it.TryGetProperty("end", out var end)) continue;
                    var (sUtc, sDate) = When(start);
                    var (eUtc, eDate) = When(end);
                    if (sUtc == null && sDate == null) continue;
                    list.Add(new CalendarEvent(Str(it, "id"), Str(it, "summary"), sUtc, eUtc, sDate, eDate,
                        Str(it, "location"), Str(it, "htmlLink"), Str(it, "transparency") == "transparent"));
                }
            return (list, Str(root, "timeZone"), Str(root, "nextPageToken"));
        }
        catch { return (list, "", ""); }

        static (DateTime? Utc, DateOnly? Date) When(JsonElement e)
        {
            if (e.TryGetProperty("dateTime", out var dt) && dt.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(dt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                    out var at))
                return (DateTime.SpecifyKind(at.UtcDateTime, DateTimeKind.Utc), null);
            if (e.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day))
                return (null, day);
            return (null, null);
        }
    }

    /// <summary>The readable calendars out of a calendarList body, primary first.</summary>
    public static List<CalendarInfo> ParseCalendars(string payload)
    {
        var list = new List<CalendarInfo>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var it in items.EnumerateArray())
            {
                string Str(string key) =>
                    it.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (Str("accessRole") is not ("owner" or "writer" or "reader") || Str("id").Length == 0) continue;
                var primary = it.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
                var name = Str("summaryOverride").Length > 0 ? Str("summaryOverride") : Str("summary");
                list.Add(new CalendarInfo(Str("id"), name.Length > 0 ? name : Str("id"), primary, Str("timeZone")));
            }
        }
        catch { /* a malformed body lists nothing */ }
        return list.OrderByDescending(c => c.Primary).ThenBy(c => c.Summary, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Busy spans for the calendar out of a freeBusy response, plus the
    /// calendar's own error (an unknown or unshared calendar id) when Google gave one.</summary>
    public static (List<(DateTime Start, DateTime End)> Spans, string Error) BusySpans(string payload, string calendarId)
    {
        var spans = new List<(DateTime, DateTime)>();
        var error = "";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("calendars", out var cals)) return (spans, error);
            if (!cals.TryGetProperty(calendarId, out var cal)) return (spans, error);
            if (cal.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array
                && errs.GetArrayLength() > 0)
            {
                var reason = errs[0].TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "";
                error = $"calendar '{calendarId}' could not be read ({(reason.Length > 0 ? reason : "error")})";
            }
            if (!cal.TryGetProperty("busy", out var busy) || busy.ValueKind != JsonValueKind.Array)
                return (spans, error);
            foreach (var b in busy.EnumerateArray())
            {
                if (DateTime.TryParse(b.GetProperty("start").GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var s)
                    && DateTime.TryParse(b.GetProperty("end").GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var e))
                    spans.Add((DateTime.SpecifyKind(s, DateTimeKind.Utc), DateTime.SpecifyKind(e, DateTimeKind.Utc)));
            }
        }
        catch { /* a malformed body just means "no known busy time" */ }
        return (spans.OrderBy(x => x.Item1).ToList(), error);
    }

    /// <summary>RFC 3339 in UTC ("2026-10-14T07:00:00.0000000Z").</summary>
    public static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc)
            .ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Health is recorded per call so a dead grant surfaces in the UI.</summary>
    private void RecordResult(Integration row, bool ok, string detail)
    {
        try
        {
            row.Status = ok ? "healthy" : "degraded";
            row.StatusDetail = ok ? "" : (detail.Length <= 200 ? detail : detail[..200]);
            row.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
        catch { /* the ledger never breaks the call */ }
    }
}
