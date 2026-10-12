using System.Globalization;
using System.Text.Json;
using ChatBranch.Data.Bookings;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Flows;

/// <summary>
/// The "Before a booking" trigger: N minutes / hours / days before a ChatBranch
/// booking starts. It is PER BOOKING, not per contact (a contact with two
/// bookings gets two reminders), so it has its own pass instead of the per-contact
/// loop + <c>AlreadyRan</c>:
/// <list type="bullet">
/// <item>candidates = booked bookings whose reminder window (start − lead) has
/// opened and that have not started yet, on the chosen calendar skill (or any), in
/// the journey's entity scope (the booking's own entity, else its contact's);</item>
/// <item>a booking made inside its window never gets one ("in 1 hour" for a booking
/// half an hour away), nor does a window that opened before the journey went live;</item>
/// <item>one run per booking AND start time (<c>booking_id</c> + <c>booking_start</c>
/// in the run's context), so a moved booking gets a reminder for its new time;</item>
/// <item>the run's conversation is the booking's (<c>trigger_thread_id</c>): its
/// messages go to the chat the booking was made in, never another company's;</item>
/// <item>a booking cancelled or moved while the run is still going ends it
/// (<see cref="FlowRunStatus.Cancelled"/>) before its next step.</item>
/// </list>
/// The <c>{{booking_…}}</c> variables (<see cref="MergeBookingVariables"/>) render
/// the booking in its own timezone.
/// </summary>
public partial class FlowEngine
{
    public const string WhenBookingStarts = "Before a booking";

    /// <summary>The run-context keys a "Before a booking" run carries.</summary>
    internal const string BookingIdKey = "booking_id";
    internal const string BookingStartKey = "booking_start";

    /// <summary>Every <c>{{booking_…}}</c> variable — always defined (empty without a
    /// booking), so a placeholder never reaches a customer as typed.</summary>
    public static readonly string[] BookingVariables =
    {
        "booking_time", "booking_date", "booking_start_time", "booking_end_time",
        "booking_length", "booking_calendar", "booking_link",
    };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>How long before the start the reminder goes: amount (≥ 1) × unit
    /// (minutes | hours | days, default hours), at most a year.</summary>
    public static TimeSpan BookingLead(JsonElement cfg)
    {
        var amount = Math.Max(1, CfgInt(cfg, "amount", 1));
        var minutes = CfgStr(cfg, "unit").Trim().ToLowerInvariant() switch
        {
            "minutes" or "minute" => (double)amount,
            "days" or "day" => amount * 1440d,
            _ => amount * 60d,
        };
        return TimeSpan.FromMinutes(Math.Min(minutes, 366 * 1440d));
    }

    /// <summary>"1 hours before a booking" (the run log's words).</summary>
    private static string BookingTriggerText(JsonElement cfg)
    {
        var unit = CfgStr(cfg, "unit").Trim().ToLowerInvariant();
        if (unit is not ("minutes" or "days")) unit = "hours";
        return $"{Math.Max(1, CfgInt(cfg, "amount", 1))} {unit} before a booking";
    }

    /// <summary>A booking's start as the run context stores it (ISO 8601 UTC, to the second).</summary>
    public static string BookingStartStamp(DateTime startUtc) =>
        DateTime.SpecifyKind(startUtc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);

    /// <summary>
    /// The per-booking pass of one "Before a booking" trigger node: end the live runs
    /// whose booking was cancelled or moved, then start one run for every booking whose
    /// reminder is due. Returns how many runs it started.
    /// </summary>
    private int StartBookingTriggers(Workflow wf, JsonElement node, JsonElement cfg)
    {
        var nodeId = Str(node, "id");
        EndRunsForGoneBookings(wf, nodeId);

        var now = DateTime.UtcNow;
        var lead = BookingLead(cfg);
        var horizon = now + lead;
        var calendar = CfgStr(cfg, "calendar").Trim();
        var q = _db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Booked && b.StartUtc > now && b.StartUtc <= horizon);
        if (calendar != "") q = q.Where(b => b.SkillId == calendar);
        var due = q.OrderBy(b => b.StartUtc).ThenBy(b => b.Id).ToList()
            .Where(b =>
            {
                var opens = b.StartUtc - lead;
                // made inside its window ("in 1 hour" for a booking 30 minutes away), or a
                // window that opened before the journey went live: no reminder
                return b.CreatedAt <= opens && !(wf.LiveSince is DateTime live && live > opens);
            })
            .ToList();
        if (due.Count == 0) return 0;

        // already reminded: a run of this trigger for the same booking AND the same start
        var contactIds = due.Select(b => b.ContactId).Distinct().ToList();
        var done = _db.FlowRuns.AsNoTracking()
            .Where(r => r.WorkflowId == wf.Id && r.TriggerKey == nodeId
                        && r.ContactId != null && contactIds.Contains(r.ContactId.Value))
            .Select(r => r.Context).ToList()
            .Select(c => $"{CtxValue(c, BookingIdKey)}@{CtxValue(c, BookingStartKey)}")
            .ToHashSet();

        // the journey's entity scope: the booking's own entity decides; a booking
        // with none (an unassigned conversation) falls back to its contact's links
        var runsFor = JourneyEntities(wf);
        HashSet<int>? subtree = runsFor.Count > 0 ? _scoping.EntitySubtreeIds(runsFor).ToHashSet() : null;
        HashSet<int>? entityContacts = null;
        bool InScope(Booking b) =>
            subtree == null
            || (b.EntityId is int be ? subtree.Contains(be) : (entityContacts ??= EntityContacts(wf) ?? new()).Contains(b.ContactId));

        var started = 0;
        foreach (var b in due)
        {
            var stamp = BookingStartStamp(b.StartUtc);
            if (done.Contains($"{b.Id}@{stamp}")) continue;
            if (!InScope(b)) continue;
            var contact = _db.Contacts.Find(b.ContactId);
            if (contact == null || !TriggerAllows(cfg, contact, null)) continue;
            var context = new Dictionary<string, object?>
            {
                [BookingIdKey] = b.Id,
                [BookingStartKey] = stamp,
            };
            // the booking's chat: the run's messages go there (ThreadStore pins the jobs)
            if (b.ThreadId is int tid) context["trigger_thread_id"] = tid;
            Start(wf, nodeId, contact, $"{BookingTriggerText(cfg)} — booking #{b.Id}, {BookingService.LabelWithZone(b)}", context);
            done.Add($"{b.Id}@{stamp}");
            started++;
        }
        return started;
    }

    /// <summary>Live runs of this trigger whose booking was cancelled or moved end now
    /// (a run parked on "wait for a reply" would otherwise wait forever).</summary>
    private void EndRunsForGoneBookings(Workflow wf, string nodeId)
    {
        var live = _db.FlowRuns.Where(r => r.WorkflowId == wf.Id && r.TriggerKey == nodeId
                                           && r.Status == FlowRunStatus.Waiting).ToList();
        foreach (var run in live)
            if (BookingGone(run) is string why)
                Finish(run, FlowRunStatus.Cancelled, why);
    }

    /// <summary>
    /// Why a "Before a booking" run must stop: its booking was cancelled (or is being
    /// cancelled), or moved to another time — null while it stands, and for every
    /// run that is not about a booking.
    /// </summary>
    private string? BookingGone(FlowRun run)
    {
        if (!int.TryParse(Ctx(run, BookingIdKey), out var id)) return null;
        var b = _db.Bookings.AsNoTracking().FirstOrDefault(x => x.Id == id);
        if (b == null || b.Status != BookingStatus.Booked) return "booking cancelled";
        var start = Ctx(run, BookingStartKey);
        if (start != "" && start != BookingStartStamp(b.StartUtc)) return "booking moved to another time";
        return null;
    }

    /// <summary>The conversation a "Before a booking" run's booking was made in (null for other runs).</summary>
    private int? BookingThreadId(FlowRun run) =>
        Ctx(run, BookingIdKey) != "" && int.TryParse(Ctx(run, "trigger_thread_id"), out var tid) ? tid : null;

    /// <summary>The booking's conversation when it is on <paramref name="channel"/> (a thread
    /// channel id) — where the run's messages on that channel go.</summary>
    private Thread? BookingThread(FlowRun run, string? channel = null)
    {
        if (BookingThreadId(run) is not int tid) return null;
        var t = _db.Threads.AsNoTracking().FirstOrDefault(x => x.Id == tid);
        return t != null && (channel == null || t.Channel == channel) ? t : null;
    }

    /// <summary>
    /// <c>{{booking_time}}</c> "Mon 12 Oct, 2:15 PM" · <c>{{booking_date}}</c> "Mon 12 Oct 2026" ·
    /// <c>{{booking_start_time}}</c> "2:15 PM" · <c>{{booking_end_time}}</c> ·
    /// <c>{{booking_length}}</c> "45 min" · <c>{{booking_calendar}}</c> · <c>{{booking_link}}</c>
    /// — in the booking's own timezone (else the organization's). Every key is set,
    /// empty when the run is not about a booking.
    /// </summary>
    private void MergeBookingVariables(Dictionary<string, string> vars, FlowRun? run)
    {
        foreach (var k in BookingVariables) vars[k] = "";
        if (run == null || !int.TryParse(Ctx(run, BookingIdKey), out var id)) return;
        var b = _db.Bookings.AsNoTracking().FirstOrDefault(x => x.Id == id);
        if (b == null) return;
        var tz = BookingService.TimeZoneFor(b.Timezone, Zone());
        var start = BookingAvailability.ToLocal(b.StartUtc, tz);
        var end = BookingAvailability.ToLocal(b.EndUtc, tz);
        vars["booking_time"] = BookingAvailability.StartLabel(start);
        vars["booking_date"] = start.ToString("ddd d MMM yyyy", Inv);
        vars["booking_start_time"] = start.ToString("h:mm tt", Inv);
        vars["booking_end_time"] = b.EndUtc > b.StartUtc ? end.ToString("h:mm tt", Inv) : "";
        vars["booking_length"] = LengthText((int)Math.Round((b.EndUtc - b.StartUtc).TotalMinutes));
        vars["booking_calendar"] = b.CalendarLabel ?? "";
        vars["booking_link"] = b.EventLink ?? "";
    }

    /// <summary>"45 min", "1 hour", "2 hours", "1 h 30 min".</summary>
    public static string LengthText(int minutes)
    {
        if (minutes <= 0) return "";
        if (minutes < 60) return $"{minutes} min";
        if (minutes % 60 == 0) return minutes == 60 ? "1 hour" : $"{minutes / 60} hours";
        return $"{minutes / 60} h {minutes % 60} min";
    }

    /// <summary>The variables a run's steps render with: the contact's + the run's booking.</summary>
    private Dictionary<string, string> RunVariables(FlowRun run, Contact? contact)
    {
        var vars = VariablesFor(contact);
        MergeBookingVariables(vars, run);
        return vars;
    }
}
