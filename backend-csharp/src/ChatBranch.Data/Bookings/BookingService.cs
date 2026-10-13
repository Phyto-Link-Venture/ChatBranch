using System.Globalization;
using System.Text.Json;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Bookings;

/// <summary>
/// Calendar booking for the AI ("Connected app" skills, <c>app: "calendar"</c>)
/// — the ONE place its rules live:
/// <list type="bullet">
/// <item><b>Reserve during generation, make at delivery.</b> The book / cancel
/// tools only write a <see cref="BookingStatus.Pending"/> row (or flip a booked
/// one to <see cref="BookingStatus.Cancelling"/>); their ids travel on the
/// reply (<see cref="AiReplyResult.PendingActions"/> → the AiDraft context).
/// <see cref="ConfirmAsync"/> makes them when <see cref="AiGate"/> delivers
/// the reply — re-checking the slot — and <see cref="Release"/> gives them back
/// when the reply is rejected, superseded, expired or shadowed.</item>
/// <item><b>Availability</b> = working hours − Google free/busy (google source)
/// − ChatBranch's own holding bookings for the same calendar
/// (<see cref="BookingAvailability"/>).</item>
/// <item><b>A google skill never pretends.</b> No connection ⇒ the tools say
/// the calendar is unavailable; nothing silently falls back to ChatBranch.</item>
/// <item><b>Limits</b>: one book + one cancel per reply, a per-contact cap of
/// future bookings per skill, bookings only for the conversation's contact.</item>
/// </list>
/// Every made / cancelled / failed booking writes an audit row, a private note
/// in the conversation and a notification. Nothing here throws into a reply,
/// webhook or send path.
/// </summary>
public class BookingService
{
    public const string GoogleProvider = "google_calendar";
    /// <summary>A reservation no reply picked up within this long is released.</summary>
    public static readonly TimeSpan OrphanAfter = TimeSpan.FromMinutes(30);
    private const int ResultCap = 4000;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly IntegrationResolver _resolver;
    private readonly GoogleCalendarClient _calendar;
    private readonly TimeProvider _clock;
    private readonly ZoomClient _zoom;

    public BookingService(AppDbContext db, ScopingService scoping, IntegrationResolver? resolver = null,
        GoogleCalendarClient? calendar = null, TimeProvider? clock = null, ZoomClient? zoom = null)
    {
        _db = db;
        _scoping = scoping;
        _resolver = resolver ?? new IntegrationResolver(db, scoping);
        _calendar = calendar ?? new GoogleCalendarClient(db,
            new GoogleOAuth(db, scoping, PlainHttpClientFactory.Instance), PlainHttpClientFactory.Instance);
        _clock = clock ?? TimeProvider.System;
        _zoom = zoom ?? new ZoomClient(db, PlainHttpClientFactory.Instance);
    }

    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    // =========================================================================
    // calendar target
    // =========================================================================

    /// <summary>Which calendar a skill books into for a conversation.</summary>
    public sealed record CalendarTarget(string Source, Integration? Row, string CalendarId, string Key,
        string Label, string? Error)
    {
        public bool Ok => Error == null;
    }

    /// <summary>
    /// chatbranch → <c>chatbranch:&lt;skill entity id | global&gt;</c>. google →
    /// the assignee's own grant → the entity's shared grant → the org's
    /// (<c>assignee</c>), entity → org (<c>shared</c>) or one specific
    /// google_calendar row; key <c>google:&lt;row id&gt;:&lt;calendar id&gt;</c>.
    /// </summary>
    public CalendarTarget ResolveTarget(AgentSkill skill, CalendarAppConfig cfg, int? entityId, int? assigneeUserId)
    {
        if (cfg.Source != BookingSources.Google)
            return new CalendarTarget(BookingSources.ChatBranch, null, "",
                $"chatbranch:{(skill.EntityId is int se ? se.ToString(CultureInfo.InvariantCulture) : "global")}",
                "ChatBranch bookings", null);
        try
        {
            var scopeEntity = entityId ?? skill.EntityId;
            Integration? row = cfg.Calendar switch
            {
                "assignee" => _resolver.Active(GoogleProvider, scopeEntity, assigneeUserId),
                "shared" => _resolver.Active(GoogleProvider, scopeEntity, null),
                _ => cfg.IntegrationId is int id
                    ? _db.Integrations.FirstOrDefault(i => i.Id == id && i.Provider == GoogleProvider)
                    : null,
            };
            if (row == null || GoogleOAuth.Cred(row, "access_token").Length == 0)
                return new CalendarTarget(BookingSources.Google, row, "", "", "Google Calendar",
                    "Google Calendar is not connected for this calendar.");
            var calendarId = GoogleCalendarClient.CalendarIdFor(row, cfg.CalendarId);
            return new CalendarTarget(BookingSources.Google, row, calendarId,
                $"google:{row.Id}:{calendarId}", CalendarLabel(row), null);
        }
        catch (Exception ex)
        {
            return new CalendarTarget(BookingSources.Google, null, "", "", "Google Calendar",
                $"The calendar could not be resolved ({ex.GetType().Name}).");
        }
    }

    /// <summary>"Google Calendar · aisha" / "Google Calendar · Bangsar (shared)" / "Google Calendar".</summary>
    public string CalendarLabel(Integration row)
    {
        try
        {
            if (row.OwnerUserId is int uid)
            {
                var name = _db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.Username).FirstOrDefault();
                return string.IsNullOrEmpty(name) ? "Google Calendar" : $"Google Calendar · {name}";
            }
            if (row.EntityId is int eid)
            {
                var name = _db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => e.Name).FirstOrDefault();
                return string.IsNullOrEmpty(name) ? "Google Calendar (shared)" : $"Google Calendar · {name} (shared)";
            }
        }
        catch { /* label only */ }
        return "Google Calendar";
    }

    public TimeZoneInfo TimeZoneFor(CalendarAppConfig cfg) =>
        BuiltinSkills.ResolveTimeZone(cfg.Timezone) ?? BuiltinSkills.OrgTimeZone(_scoping);

    public int? UserIdOf(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        try
        {
            return _db.Users.AsNoTracking().Where(u => u.Username == username)
                .Select(u => (int?)u.Id).FirstOrDefault();
        }
        catch { return null; }
    }

    // =========================================================================
    // availability
    // =========================================================================

    public sealed record AvailabilityResult(bool Ok, string Error, IReadOnlyList<BookingAvailability.Slot> Free,
        DateOnly From, DateOnly To);

    /// <summary>Every free slot on [from, to] for this calendar (working hours − busy − holds), each
    /// <paramref name="durationMinutes"/> long (default: the skill's default length).
    /// <paramref name="ignoreBookingId"/> = a booking being RESCHEDULED: its hold and its own Google
    /// event (the free/busy span with exactly its start and end) don't count as busy.</summary>
    public async Task<AvailabilityResult> FreeSlotsAsync(CalendarAppConfig cfg, CalendarTarget target,
        TimeZoneInfo tz, DateOnly from, DateOnly to, int? ignoreBookingId = null, CancellationToken ct = default,
        int? durationMinutes = null)
    {
        var now = UtcNow;
        var today = BookingAvailability.Today(now, tz);
        var last = BookingAvailability.LastDay(cfg, now, tz);
        if (from < today) from = today;
        if (to > last) to = last;
        if (to < from) return new AvailabilityResult(true, "", Array.Empty<BookingAvailability.Slot>(), from, to);
        if (!target.Ok) return new AvailabilityResult(false, target.Error!, Array.Empty<BookingAvailability.Slot>(), from, to);

        var (spanFrom, spanTo) = BookingAvailability.UtcSpan(from, to, tz);
        var busy = new List<(DateTime, DateTime)>();
        var holds = await _db.Bookings.AsNoTracking()
            .Where(b => b.CalendarKey == target.Key && BookingStatus.Holding.Contains(b.Status)
                        && b.EndUtc > spanFrom && b.StartUtc < spanTo
                        && (ignoreBookingId == null || b.Id != ignoreBookingId))
            .Select(b => new { b.StartUtc, b.EndUtc }).ToListAsync(ct);
        busy.AddRange(holds.Select(h => (h.StartUtc, h.EndUtc)));

        if (target.Source == BookingSources.Google)
        {
            var fb = await _calendar.FreeBusyAsync(target.Row!, target.CalendarId, spanFrom, spanTo, ct);
            if (!fb.Ok)
                return new AvailabilityResult(false, fb.NotConnected ? "Google Calendar is not connected." : fb.Error,
                    Array.Empty<BookingAvailability.Slot>(), from, to);
            var replaced = ignoreBookingId is int ib
                ? await _db.Bookings.AsNoTracking().Where(x => x.Id == ib).ToListAsync(ct)
                : new List<Booking>();
            busy.AddRange(WithoutOwnEvents(fb.Busy, replaced, target.Key));
        }
        var free = BookingAvailability.FreeSlots(cfg, tz, now, from, to, busy, durationMinutes);
        return new AvailabilityResult(true, "", free, from, to);
    }

    /// <summary>
    /// Google free/busy minus the events of bookings being rescheduled. Free/busy has no event ids, so
    /// a span is the booking's own event when it has EXACTLY its start and end on the same calendar;
    /// a span merged with a neighbouring event stays busy — the reschedule is then refused and the old
    /// booking kept, never double-booked.
    /// </summary>
    private static IEnumerable<(DateTime Start, DateTime End)> WithoutOwnEvents(
        IEnumerable<(DateTime Start, DateTime End)> busy, IReadOnlyCollection<Booking> replaced, string calendarKey)
    {
        var own = replaced.Where(x => x.Source == BookingSources.Google && x.ExternalEventId.Length > 0
                                      && x.CalendarKey == calendarKey)
            .Select(x => (Start: BookingAvailability.Utc(x.StartUtc), End: BookingAvailability.Utc(x.EndUtc))).ToList();
        return own.Count == 0 ? busy
            : busy.Where(b => !own.Contains((BookingAvailability.Utc(b.Start), BookingAvailability.Utc(b.End))));
    }

    // =========================================================================
    // tools (what the model is offered and what a call does)
    // =========================================================================

    /// <summary>The per-generation state of the app tools: what this reply reserved.</summary>
    public sealed class Session
    {
        public List<AiPendingAction> Actions { get; } = new();
        /// <summary>
        /// The AI playground: the tools check everything exactly as for a real
        /// reply (free/busy, the per-contact cap, the slot still free, whose
        /// booking it is) but RESERVE NOTHING — no row is written, no booking is
        /// flipped to cancelling. What the reply would have booked or cancelled is
        /// recorded in <see cref="Previews"/> instead, and <see cref="Actions"/>
        /// stays empty, so there is never anything to make or release.
        /// </summary>
        public bool Preview { get; set; }
        /// <summary>What a <see cref="Preview"/> generation would have booked / cancelled.</summary>
        public List<BookingPreview> Previews { get; } = new();
        internal int? BookedId;
        internal string BookedStart = "";
        internal string BookedResult = "";
        internal int? CancelledId;
        internal string CancelledResult = "";
        /// <summary>A book call of this reply failed for its TIME — a reschedule in this reply then
        /// keeps the old booking (all-or-nothing).</summary>
        internal bool BookFailed;
        /// <summary>The last book refusal asked for a cancel first (the per-customer limit, or their own
        /// booking in the way) — following that advice is not a failed reschedule.</summary>
        internal bool CancelFirst;
        /// <summary>This reply called the book tool at all — a reschedule, so its cancel needs the new booking.</summary>
        internal bool BookAttempted;
    }

    /// <summary>
    /// What a playground reply would have done to the calendar (nothing was
    /// done): <c>book</c> a slot or <c>cancel</c> one of the contact's bookings.
    /// </summary>
    public sealed record BookingPreview(string Type, string Label, string Start, string Timezone, string Calendar,
        int? BookingId);

    /// <summary>One app tool the model may call.</summary>
    public sealed record ToolBinding(AgentSkill Skill, CalendarAppConfig Config, string Action, string Base);

    public sealed record ToolOutcome(string Json, bool Ok);

    /// <summary>The tool definitions for one calendar skill (only its enabled actions).</summary>
    public static IEnumerable<(string Name, string Action, LlmTool Tool)> ToolDefinitions(string b,
        CalendarAppConfig cfg, string tzId)
    {
        var calendar = cfg.Source == BookingSources.Google ? "the business's Google Calendar" : "the business's booking calendar";
        var dur = cfg.VariableLength ? LengthsText(cfg) + "-long" : $"{cfg.DurationMinutes}-minute";
        // more than one length: ask how long, pass duration_minutes, restate the time range
        var lengthHowTo = cfg.VariableLength
            ? $" Appointments can be {LengthsText(cfg)} long (default {cfg.DurationMinutes}). If the customer hasn't "
              + "said how long they want, ask them first; then pass that length as duration_minutes to this tool and "
              + $"to {b}_book, so the free times returned fit the whole appointment."
            : "";
        // rescheduling: the customer's own booking may be checked as if it were free
        var reschedule = cfg.Allows("book") && cfg.Allows("cancel");
        var lengthSchema = cfg.VariableLength
            ? ",\"duration_minutes\":{\"type\":\"integer\",\"enum\":[" + string.Join(",", cfg.Lengths)
              + $"],\"description\":\"Length of the appointment in minutes (default {cfg.DurationMinutes})\"}}"
            : "";
        if (cfg.Allows("availability"))
            yield return (b + "_availability", "availability", new LlmTool(b + "_availability",
                $"Check open appointment times ({dur}) on {calendar}, in {tzId} local time, each with a ready-made "
                + $"label. A search over several days returns a SAMPLE of up to {BookingAvailability.MaxOffered} free "
                + "start times spread across the dates; a one-day search (from_date = to_date) lists EVERY free time "
                + "that day. When the customer asks for a specific time (\"can I have 2pm on Monday?\"), call it with "
                + "that day as from_date and `time` (HH:mm): it answers whether that exact time is free and, if not, "
                + "why and the nearest free times. A time missing from a sample may still be free — never tell the "
                + "customer a time is unavailable unless you checked that exact time or the book tool refused it. "
                + "Every result is the live calendar, so never call earlier results made up or unchecked. Call this "
                + "BEFORE suggesting any time and offer the customer two or three of the returned options, each with "
                + "weekday, date, time and timezone — never invent, guess or round a time. Dates are YYYY-MM-DD in "
                + "the business's local time: work out 'today', 'tomorrow' or 'next Tuesday' from the current date "
                + $"in the system prompt. Bookings are possible up to {cfg.MaxDaysAhead} days ahead." + lengthHowTo
                + (reschedule
                    ? " To reschedule, pass the customer's current booking as ignore_booking_id: its own time then counts "
                      + "as free (it is only freed when you cancel it and book the new time in the same reply)."
                    : ""),
                Schema("{\"type\":\"object\",\"properties\":{"
                       + "\"from_date\":{\"type\":\"string\",\"description\":\"First day to search, YYYY-MM-DD (default: today)\"},"
                       + "\"to_date\":{\"type\":\"string\",\"description\":\"Last day to search, YYYY-MM-DD (default: 7 days after from_date). Use the same date as from_date to list every free time that day.\"},"
                       + "\"time\":{\"type\":\"string\",\"description\":\"Exact start to check, HH:mm local time (24-hour, e.g. 14:00 for 2 PM), with a single from_date\"}"
                       + lengthSchema
                       + (reschedule
                           ? ",\"ignore_booking_id\":{\"type\":\"integer\",\"description\":\"Rescheduling: the customer's current booking_id, whose own time then counts as free\"}"
                           : "")
                       + "}}")));
        if (cfg.Allows("book"))
            yield return (b + "_book", "book", new LlmTool(b + "_book",
                $"Book ONE {dur} appointment for this customer (the person in this conversation) at a start time "
                + $"returned by {b}_availability. Call it ONLY after the customer clearly chose that specific time — "
                + "never on your own pick, never for 'any time' and never while they are still deciding. "
                + $"`start` is the local start time as YYYY-MM-DDTHH:mm ({tzId}). The time is held for them and "
                + "confirmed when your reply is sent: in that reply, confirm the booking and restate the weekday, "
                + "date, time and timezone exactly as this tool returns them. If it fails, apologise and offer the "
                + "alternatives it returns."
                + (cfg.VariableLength
                    ? " Pass the length the customer chose as duration_minutes (the same one you checked availability "
                      + "with) and restate it as a time range when you confirm, e.g. \"Mon 12 Oct, 2:00–3:00 PM\"."
                    : ""),
                Schema("{\"type\":\"object\",\"properties\":{"
                       + "\"start\":{\"type\":\"string\",\"description\":\"Chosen start, YYYY-MM-DDTHH:mm in the calendar's local time, exactly as returned by the availability tool\"},"
                       + "\"note\":{\"type\":\"string\",\"description\":\"Optional short note for the booking (what the appointment is about)\"}"
                       + lengthSchema
                       + "},\"required\":[\"start\"]}")));
        // also offered when bookings carry a meeting: the customer may ask for the link again
        if (cfg.Allows("cancel") || cfg.Meeting.Length > 0)
            yield return (b + "_my_bookings", "my_bookings", new LlmTool(b + "_my_bookings",
                "List this customer's upcoming bookings (booking_id, date, time, status"
                + (cfg.Meeting.Length > 0 ? ", meeting_link" : "") + "). Use it to find the "
                + "booking_id before cancelling or rescheduling, or when they ask when their appointment is"
                + (cfg.Meeting.Length > 0 ? " or for its meeting link" : "") + ".",
                Schema("{\"type\":\"object\",\"properties\":{}}")));
        if (cfg.Allows("cancel"))
        {
            yield return (b + "_cancel", "cancel", new LlmTool(b + "_cancel",
                $"Cancel one of this customer's upcoming bookings (booking_id from {b}_my_bookings). Only after "
                + "the customer asked to cancel or reschedule and confirmed which appointment. To reschedule, call "
                + $"{b}_cancel for the old booking FIRST and then {b}_book for the new time, in the same reply: the two "
                + "go together — if the new time can't be booked, the old booking is kept and nothing is cancelled. The "
                + "cancellation takes effect when your reply is sent — confirm it with the date and time.",
                Schema("{\"type\":\"object\",\"properties\":{"
                       + "\"booking_id\":{\"type\":\"integer\",\"description\":\"The booking to cancel\"}"
                       + "},\"required\":[\"booking_id\"]}")));
        }
    }

    /// <summary>Run one app tool call. Never throws: problems come back as
    /// <c>{ok:false, error, advice}</c> for the model to act on.</summary>
    public async Task<ToolOutcome> ExecuteToolAsync(ToolBinding binding, string? argsJson, SkillContext ctx,
        CancellationToken ct = default)
    {
        var session = ctx.Apps;
        var bookedBefore = session.BookedId != null;
        ToolOutcome outcome;
        try
        {
            var args = ParseArgs(argsJson);
            outcome = binding.Action switch
            {
                "availability" => await AvailabilityToolAsync(binding, args, ctx, ct),
                "book" => await BookToolAsync(binding, args, ctx, ct),
                "cancel" => await CancelToolAsync(binding, args, ctx, ct),
                "my_bookings" => MyBookingsTool(binding, ctx),
                _ => Error($"unknown calendar action '{binding.Action}'", "", ok: false),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            outcome = Error($"The calendar tool failed ({ex.GetType().Name}).",
                "Don't promise a time. Tell the customer a teammate will confirm the appointment.", ok: false);
        }
        // a reschedule is all-or-nothing: a book that reserved nothing keeps the booking it was replacing
        if (binding.Action == "book") session.BookAttempted = true;
        if (binding.Action == "book" && !bookedBefore && session.BookedId == null && !session.CancelFirst)
            outcome = await KeepReplacedBookingAsync(outcome, session, ct);
        return outcome.Json.Length <= ResultCap ? outcome : outcome with { Json = outcome.Json[..ResultCap] };
    }

    /// <summary>
    /// A book call reserved nothing (the time was taken, bad input, a calendar error). Remember that
    /// for this reply, and when the reply already cancels a booking — a reschedule — give that booking
    /// back (cancelling → booked, out of the reply's actions) and say so in the result, so the reply
    /// never cancels the old appointment without making the new one. Never throws.
    /// </summary>
    private async Task<ToolOutcome> KeepReplacedBookingAsync(ToolOutcome outcome, Session session, CancellationToken ct)
    {
        session.BookFailed = true;
        if (session.CancelledId is not int id) return outcome;
        try
        {
            var row = await RestoreCancelAsync(session, id, ct);
            var node = System.Text.Json.Nodes.JsonNode.Parse(outcome.Json) as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject { ["ok"] = false };
            var kept = $"The old booking ({id}) was kept because the new time could not be booked.";
            node["error"] = $"{(string?)node["error"] ?? ""} {kept}".Trim();
            node["advice"] = (row == null ? "" : $"Their appointment {LabelWithZone(row)} still stands — don't say it "
                                                   + "was cancelled. ")
                             + ((string?)node["advice"] ?? "");
            node["old_booking_kept"] = id;
            return new ToolOutcome(node.ToJsonString(), outcome.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return outcome;
        }
    }

    private async Task<ToolOutcome> AvailabilityToolAsync(ToolBinding b, JsonElement args, SkillContext ctx,
        CancellationToken ct)
    {
        var cfg = b.Config;
        var tz = TimeZoneFor(cfg);
        var target = ResolveTarget(b.Skill, cfg, ctx.EntityId, UserIdOf(ctx.Assignee));
        if (!target.Ok) return Unavailable(target.Error!);
        var now = UtcNow;
        var today = BookingAvailability.Today(now, tz);
        var last = BookingAvailability.LastDay(cfg, now, tz);
        if (LengthOf(args, cfg, out var length) is { } badLength) return badLength;
        if (IgnoredBooking(args, b, target, ctx, out var ignore) is { } badIgnore) return badIgnore;
        var timeRaw = (Arg(args, "time") ?? "").Trim();
        if (timeRaw.Length > 0)
            return await CheckTimeToolAsync(b, args, timeRaw, ctx, target, tz, today, last, length, ignore, ct);
        var from = ParseDate(Arg(args, "from_date"), today) ?? today;
        var to = ParseDate(Arg(args, "to_date"), today) ?? from.AddDays(7);
        if (to < from) (from, to) = (to, from);
        if (from > last)
            return Refused($"Bookings can only be made up to {last:yyyy-MM-dd} ({cfg.MaxDaysAhead} days ahead).",
                $"Offer times on or before {last:dddd d MMMM}.", new() { ["last_bookable_date"] = last.ToString("yyyy-MM-dd") });

        var r = await FreeSlotsAsync(cfg, target, tz, from, to, ignore, ct, length);
        if (!r.Ok) return Unavailable(r.Error);
        // one day = every free time (the "is 2 PM open?" answer is in the list); several days =
        // a spread, which is a SAMPLE — a time missing from it may still be free
        var singleDay = r.From == r.To;
        var offered = singleDay
            ? r.Free.Take(BookingAvailability.MaxSingleDay).ToList()
            : BookingAvailability.Spread(r.Free);
        var payload = AvailabilityPayload(cfg, target, tz, last, r.From, r.To, length, ignore);
        payload["free_slots_in_range"] = r.Free.Count;
        AddUpcoming(payload, b, target, ctx);

        // a full day must fit the result cap whole: shorter slots first, then fewer (never cut mid-JSON)
        var shortSlots = false;
        string json;
        while (true)
        {
            var complete = offered.Count == r.Free.Count;
            payload["slots"] = offered.Select(s => shortSlots ? ShortSlotJson(s, cfg.VariableLength) : SlotJson(s, cfg.VariableLength)).ToList();
            payload["complete"] = complete;
            payload["note"] = (offered.Count == 0
                ? $"No free times between {r.From:yyyy-MM-dd} and {r.To:yyyy-MM-dd}. Try other dates (up to {last:yyyy-MM-dd}) — don't offer a time that wasn't returned."
                : complete
                    ? "These are all the free times in the range. Offer 2–3 of them, with weekday, date, time and timezone."
                    : "A sample of the free times, not all of them. A time missing from this list may still be free. Never tell "
                      + "the customer a time is unavailable unless you checked that exact time (call this tool with `time`) or the "
                      + "book tool refused it. To list every free time on one day, call again with from_date = to_date. Offer 2–3 "
                      + "of these, with weekday, date, time and timezone.")
                + IgnoreNote(ignore) + SampleNote;
            json = JsonSerializer.Serialize(payload);
            if (json.Length <= ResultCap || offered.Count == 0) break;
            if (singleDay && !shortSlots) { shortSlots = true; continue; }
            offered = offered.Take(offered.Count - 1).ToList();
        }
        return new ToolOutcome(json, true);
    }

    /// <summary>Take this reply's cancel of booking <paramref name="id"/> back: cancelling → booked, and
    /// out of the reply's actions / previews. Returns the booking (null when it is gone).</summary>
    private async Task<Booking?> RestoreCancelAsync(Session session, int id, CancellationToken ct)
    {
        var row = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (!session.Preview && row is { Status: BookingStatus.Cancelling })
        {
            row.Status = BookingStatus.Booked;
            row.CancelDraftId = null;
            row.UpdatedAt = UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        session.Actions.RemoveAll(a => a.Type == AiPendingAction.Cancel && a.BookingId == id);
        session.Previews.RemoveAll(p => p.Type == AiPendingAction.Cancel && p.BookingId == id);
        session.CancelledId = null;
        session.CancelledResult = "";
        return row;
    }

    /// <summary>
    /// After the model's tool calls: a reply that tried to book but reserved nothing must not cancel
    /// either — a reschedule is all-or-nothing (the model may have cancelled after a "cancel first"
    /// refusal and then never booked). Gives the cancel back and returns its booking id, else null.
    /// The reply loop then treats the reply as not cancelling (its booking-claim check catches a
    /// "has been cancelled"). Never throws.
    /// </summary>
    public async Task<int?> SettleRescheduleAsync(Session session, CancellationToken ct = default)
    {
        if (!session.BookAttempted || session.BookedId != null || session.CancelledId is not int id) return null;
        try
        {
            await RestoreCancelAsync(session, id, ct);
            session.BookFailed = true;
            return id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// The booking an availability check treats as free: <c>ignore_booking_id</c> (one of THIS
    /// customer's upcoming bookings on this calendar, else a refusal), else the booking this reply
    /// is already cancelling — a reschedule.
    /// </summary>
    private ToolOutcome? IgnoredBooking(JsonElement args, ToolBinding b, CalendarTarget target, SkillContext ctx,
        out int? ignore)
    {
        ignore = ctx.Apps.CancelledId;
        var raw = (Arg(args, "ignore_booking_id") ?? "").Trim();
        if (raw.Length == 0) return null;
        var contact = ContactOf(ctx);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && contact != null
            && Upcoming(contact.Id, b.Skill, target.Key, ctx.EntityId).Any(x => x.Id == id))
        {
            ignore = id;
            return null;
        }
        return Refused($"Booking {raw} isn't one of this customer's upcoming bookings.",
            $"Pass the booking_id of this customer's own booking (from {b.Base}_my_bookings), or leave ignore_booking_id out.");
    }

    private static string IgnoreNote(int? ignore) => ignore is int id
        ? $" Booking {id}'s own time counts as free here: it is only freed when you cancel booking {id} and book the new time in the same reply."
        : "";

    /// <summary>Said on every availability answer: a fresh call is the same live calendar.</summary>
    private const string SampleNote =
        " Each call can return a different sample of the same live calendar. Earlier results were real, so never tell "
        + "the customer your earlier times were made up or unchecked.";

    /// <summary>
    /// The availability tool with <c>time</c>: is that exact start free on the one day given? Free →
    /// <c>requested.available = true</c>; else the reason (outside_hours | too_soon | busy |
    /// off_grid | beyond_last_day) and up to 3 alternatives — the nearest that day, else the next
    /// 7 days. Bad input comes back as <c>{ok:false, error, advice}</c>.
    /// </summary>
    private async Task<ToolOutcome> CheckTimeToolAsync(ToolBinding b, JsonElement args, string timeRaw, SkillContext ctx,
        CalendarTarget target, TimeZoneInfo tz, DateOnly today, DateOnly last, int length, int? ignore, CancellationToken ct)
    {
        var cfg = b.Config;
        if (!TimeOnly.TryParseExact(timeRaw, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return Refused($"'{timeRaw}' is not a time.",
                "Pass `time` as HH:mm in 24-hour local time (for example 14:00 for 2 PM), with the day as from_date.");
        var fromRaw = Arg(args, "from_date");
        if (ParseDate(fromRaw, today) is not DateOnly day)
            return Refused(string.IsNullOrWhiteSpace(fromRaw) ? "`time` needs the day to check." : $"'{fromRaw}' is not a date.",
                "Pass the day as from_date (YYYY-MM-DD) together with `time`.");
        var toRaw = Arg(args, "to_date");
        if (!string.IsNullOrWhiteSpace(toRaw) && ParseDate(toRaw, today) != day)
            return Refused("`time` checks one day only.",
                "Pass the day as from_date and leave to_date empty (or the same date). To see several days, call without `time`.");

        var r = await FreeSlotsAsync(cfg, target, tz, day, day, ignore, ct, length);
        if (!r.Ok) return Unavailable(r.Error);
        var check = BookingAvailability.CheckTime(cfg, tz, UtcNow, day, time, r.Free, length);
        var label = cfg.VariableLength
            ? BookingAvailability.RangeLabel(check.LocalStart, check.LocalStart.AddMinutes(length))
            : BookingAvailability.StartLabel(check.LocalStart);
        var requested = new Dictionary<string, object?>
        {
            ["start"] = BookingAvailability.LocalIso(check.LocalStart),
            ["label"] = label,
            ["available"] = check.Available,
        };
        var payload = AvailabilityPayload(cfg, target, tz, last, day, day, length, ignore);
        payload["requested"] = requested;
        if (check.Available)
        {
            requested["end"] = BookingAvailability.LocalIso(check.Slot!.LocalEnd);
            payload["note"] = $"{label} ({tz.Id}) is free. Offer it; book it only after the customer confirms that time."
                              + IgnoreNote(ignore) + SampleNote;
        }
        else
        {
            requested["reason"] = check.Reason;
            var alternatives = await AlternativesAsync(cfg, target, tz, day, r.Free, check.LocalStart, length, ignore, ct);
            payload["alternatives"] = alternatives.Select(s => SlotJson(s, cfg.VariableLength)).ToList();
            payload["note"] = $"{label} ({tz.Id}) is not available — {ReasonText(check.Reason, cfg, last)}. "
                              + (alternatives.Count > 0
                                  ? "Say so and offer these free alternatives instead."
                                  : $"Check other dates with this tool (up to {last:yyyy-MM-dd}).")
                              + IgnoreNote(ignore) + SampleNote;
        }
        AddUpcoming(payload, b, target, ctx);
        return new ToolOutcome(JsonSerializer.Serialize(payload), true);
    }

    private static string ReasonText(string reason, CalendarAppConfig cfg, DateOnly last) => reason switch
    {
        BookingAvailability.Reasons.OutsideHours => "it is outside the business's opening hours",
        BookingAvailability.Reasons.TooSoon => cfg.MinNoticeMinutes > 0
            ? $"it is too soon: bookings need at least {cfg.MinNoticeMinutes} minutes' notice"
            : "that time has already passed",
        BookingAvailability.Reasons.OffGrid =>
            $"appointments start on a {Math.Max(5, cfg.SlotStepMinutes)}-minute grid (e.g. on the hour or half hour)",
        BookingAvailability.Reasons.BeyondLastDay => $"bookings can only be made up to {last:yyyy-MM-dd}",
        _ => "that time is already taken",
    };

    /// <summary>The fields every availability answer starts with.</summary>
    private static Dictionary<string, object?> AvailabilityPayload(CalendarAppConfig cfg, CalendarTarget target,
        TimeZoneInfo tz, DateOnly last, DateOnly from, DateOnly to, int length, int? ignore = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["timezone"] = tz.Id,
            ["calendar"] = target.Label,
            ["duration_minutes"] = length,
            ["from_date"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to_date"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["last_bookable_date"] = last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        if (cfg.VariableLength) payload["allowed_lengths"] = cfg.Lengths.ToList();
        if (ignore is int id) payload["ignored_booking_id"] = id;
        return payload;
    }

    /// <summary>"30, 60 or 90 minutes" — the lengths a variable-length calendar books.</summary>
    private static string LengthsText(CalendarAppConfig cfg)
    {
        var all = cfg.Lengths.Select(m => m.ToString(CultureInfo.InvariantCulture)).ToList();
        return (all.Count == 1 ? all[0] : string.Join(", ", all.Take(all.Count - 1)) + " or " + all[^1]) + " minutes";
    }

    /// <summary>
    /// The appointment length a tool call asks for: <c>duration_minutes</c> when given — one of the
    /// skill's lengths (unit × 1 … max_units) — else the default length. A length the calendar
    /// doesn't book is an <c>{ok:false, error, advice}</c> refusal naming the allowed ones.
    /// </summary>
    private static ToolOutcome? LengthOf(JsonElement args, CalendarAppConfig cfg, out int minutes)
    {
        minutes = cfg.DurationMinutes;
        var raw = (Arg(args, "duration_minutes") ?? "").Trim();
        if (raw.Length == 0) return null;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && cfg.AllowsLength(m))
        {
            minutes = m;
            return null;
        }
        return Refused($"{raw} minutes is not a length this calendar books.",
            cfg.VariableLength
                ? $"Use duration_minutes = {string.Join(", ", cfg.Lengths)} (minutes) — ask the customer which length they want."
                : $"Appointments here are always {cfg.DurationMinutes} minutes: leave duration_minutes out.",
            new() { ["allowed_lengths"] = cfg.Lengths.ToList() });
    }

    private void AddUpcoming(Dictionary<string, object?> payload, ToolBinding b, CalendarTarget target, SkillContext ctx)
    {
        var upcoming = ctx.ContactId is int cid ? Upcoming(cid, b.Skill, target.Key, ctx.EntityId) : new();
        if (upcoming.Count > 0)
            payload["customer_upcoming_bookings"] = upcoming.Select(x => BookingJson(x)).ToList();
    }

    /// <summary>
    /// Up to 3 other times to offer instead of a refused one on <paramref name="day"/>: the nearest
    /// to <paramref name="near"/> that day (a spread of the day when no time is given), else a
    /// spread of the next 7 days. ONE rule for the book tool's refusal and the exact-time check.
    /// </summary>
    private async Task<List<BookingAvailability.Slot>> AlternativesAsync(CalendarAppConfig cfg, CalendarTarget target,
        TimeZoneInfo tz, DateOnly day, IReadOnlyList<BookingAvailability.Slot> dayFree, DateTime? near, int length,
        int? ignore, CancellationToken ct)
    {
        var picks = near is DateTime at
            ? dayFree.OrderBy(s => Math.Abs((s.LocalStart - at).Ticks)).ThenBy(s => s.StartUtc).Take(3)
                .OrderBy(s => s.StartUtc).ToList()
            : BookingAvailability.Spread(dayFree, 3);
        if (picks.Count > 0) return picks;
        var more = await FreeSlotsAsync(cfg, target, tz, day.AddDays(1), day.AddDays(7), ignore, ct, length);
        return more.Ok ? BookingAvailability.Spread(more.Free, 3) : new();
    }

    private async Task<ToolOutcome> BookToolAsync(ToolBinding b, JsonElement args, SkillContext ctx, CancellationToken ct)
    {
        var cfg = b.Config;
        var contact = ContactOf(ctx);
        if (contact == null)
            return Refused("This conversation isn't linked to a customer record, so nothing can be booked.",
                "Tell the customer a teammate will confirm the appointment.");
        var startRaw = (Arg(args, "start") ?? "").Trim();
        var session = ctx.Apps;
        session.CancelFirst = false;
        if (session.BookedId != null)
            return startRaw == session.BookedStart
                ? new ToolOutcome(session.BookedResult, true)
                : Refused("You already reserved a time in this reply — only one booking per reply.",
                    "Confirm the time you already booked; if the customer wants a different one, they can ask in their next message.");

        var tz = TimeZoneFor(cfg);
        var target = ResolveTarget(b.Skill, cfg, ctx.EntityId, UserIdOf(ctx.Assignee));
        if (!target.Ok) return Unavailable(target.Error!);
        if (LengthOf(args, cfg, out var length) is { } badLength) return badLength;
        var when = SkillService.ParseInstant(startRaw, tz);
        if (when == null)
            return Refused($"'{startRaw}' is not a start time.",
                $"Pass `start` as YYYY-MM-DDTHH:mm, exactly as returned by {b.Base}_availability.");
        var localStart = BookingAvailability.ToLocal(when.Value.UtcDateTime, tz);
        var day = DateOnly.FromDateTime(localStart);
        var now = UtcNow;

        // the booking this reply cancels — a reschedule swaps one for the other
        var replacing = session.CancelledId;

        // the per-contact cap counts bookings, not units; cancelling rows (and the one this reply
        // cancels, also in a playground preview) are on their way out
        var active = (await _db.Bookings.AsNoTracking()
            .Where(x => x.ContactId == contact.Id && x.SkillId == b.Skill.SkillId
                        && (x.Status == BookingStatus.Pending || x.Status == BookingStatus.Booked) && x.EndUtc > now)
            .OrderBy(x => x.StartUtc).ToListAsync(ct))
            .Where(x => SameCompany(x, ctx.EntityId) && x.Id != replacing).ToList();
        if (active.Count >= cfg.MaxActivePerContact)
        {
            var first = active[0];
            session.CancelFirst = true;
            return Refused(
                $"This customer already has {(active.Count == 1 ? "an upcoming booking" : $"{active.Count} upcoming bookings")} "
                + $"({LabelOf(first)}, booking_id {first.Id}) — the limit is {cfg.MaxActivePerContact}.",
                cfg.Allows("cancel")
                    ? $"Offer to reschedule instead: check the new time with {b.Base}_availability and ignore_booking_id = "
                      + $"{first.Id} (their own booking's time then counts as free); then, with their agreement, call "
                      + $"{b.Base}_cancel for booking {first.Id} and then {b.Base}_book for the new time, in the same reply."
                    : "Tell them about their existing booking; a teammate can help change it.",
                new() { ["existing_booking"] = BookingJson(first) });
        }

        // ONE booking covering the whole length: free only when every minute of it is — the booking
        // this reply cancels (its hold and its own Google event) doesn't count
        var r = await FreeSlotsAsync(cfg, target, tz, day, day, replacing, ct, length);
        if (!r.Ok) return Unavailable(r.Error);
        var slot = r.Free.FirstOrDefault(s => s.LocalStart == localStart);
        if (slot == null)
        {
            var asked = cfg.VariableLength
                ? BookingAvailability.RangeLabel(localStart, localStart.AddMinutes(length))
                : BookingAvailability.StartLabel(localStart);
            // the customer's OWN booking in the way: moving it means cancelling it first
            if (replacing == null && cfg.Allows("cancel")
                && await OwnBookingInTheWayAsync(cfg, b, target, tz, ctx, contact, day, localStart, length, ct) is { } own)
            {
                session.CancelFirst = true;
                return Refused($"{asked} ({tz.Id}) overlaps this customer's own booking {own.Id} ({LabelOf(own)}).",
                    $"If they want to MOVE that booking to this time, call {b.Base}_cancel for booking {own.Id} first and "
                    + $"then {b.Base}_book again for this time, in the same reply. Otherwise offer another time.",
                    new() { ["own_booking"] = BookingJson(own) });
            }
            var alternatives = await AlternativesAsync(cfg, target, tz, day, r.Free, near: null, length, replacing, ct);
            return Refused($"{asked} ({tz.Id}) is not an available time.",
                alternatives.Count > 0
                    ? "Apologise, and offer the alternatives below (or check other dates with the availability tool). Book only after they choose."
                    : "Apologise and check other dates with the availability tool.",
                new() { ["alternatives"] = alternatives.Select(s => SlotJson(s, cfg.VariableLength)).ToList() });
        }
        var slotLabel = SlotLabel(slot, cfg.VariableLength);
        var restate = (cfg.VariableLength
            ? "In your reply, confirm the booking and restate the weekday, date, time range (start–end) and timezone."
            : "In your reply, confirm the booking and restate the weekday, date, time and timezone.")
            + MeetingInstruction(cfg);

        if (session.Preview)
        {
            // the playground: everything above was checked for real; nothing is held
            var preview = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["status"] = "reserved",
                ["booking_id"] = 0,
                ["start"] = BookingAvailability.LocalIso(slot.LocalStart),
                ["end"] = BookingAvailability.LocalIso(slot.LocalEnd),
                ["duration_minutes"] = length,
                ["label"] = slotLabel,
                ["timezone"] = tz.Id,
                ["calendar"] = target.Label,
                ["meeting"] = cfg.Meeting.Length > 0 ? MeetingKinds.Label(cfg.Meeting) : null,
                ["instructions"] = "The time is held for this customer and is confirmed when your reply is sent. " + restate,
            };
            session.Previews.Add(new BookingPreview(AiPendingAction.Book, slotLabel,
                BookingAvailability.LocalIso(slot.LocalStart), tz.Id, target.Label, null));
            session.BookedId = 0;
            session.BookedStart = startRaw;
            session.BookedResult = JsonSerializer.Serialize(preview);
            return new ToolOutcome(session.BookedResult, true);
        }

        var vars = Vars(contact);
        var row = new Booking
        {
            EntityId = ctx.EntityId, ContactId = contact.Id, ThreadId = ctx.ThreadId, SkillId = b.Skill.SkillId,
            Source = target.Source, IntegrationId = target.Row?.Id, CalendarKey = target.Key,
            CalendarLabel = target.Label, Title = Cap(Render(cfg.EventTitle, vars), 200),
            Note = Cap(Arg(args, "note") ?? "", 500), StartUtc = slot.StartUtc, EndUtc = slot.EndUtc,
            Timezone = tz.Id, Status = BookingStatus.Pending, CreatedBy = "ai", CreatedAt = now, UpdatedAt = now,
        };
        if (row.Title.Trim().Length == 0) row.Title = "Appointment";
        _db.Bookings.Add(row);
        await _db.SaveChangesAsync(ct);

        // two conversations racing for one slot: the first reservation wins (the booking this reply
        // replaces is not a rival)
        var clash = await _db.Bookings.AsNoTracking().AnyAsync(x => x.Id < row.Id && x.CalendarKey == row.CalendarKey
            && x.Id != replacing
            && BookingStatus.Holding.Contains(x.Status) && x.StartUtc < row.EndUtc && x.EndUtc > row.StartUtc, ct);
        if (clash)
        {
            row.Status = BookingStatus.Released;
            row.Detail = "Taken by another conversation while reserving.";
            row.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return Refused("That time was just taken.", $"Apologise and check {b.Base}_availability again for other times.");
        }

        session.Actions.Add(new AiPendingAction(AiPendingAction.Book, row.Id));
        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["status"] = "reserved",
            ["booking_id"] = row.Id,
            ["start"] = BookingAvailability.LocalIso(slot.LocalStart),
            ["end"] = BookingAvailability.LocalIso(slot.LocalEnd),
            ["duration_minutes"] = length,
            ["label"] = slotLabel,
            ["timezone"] = tz.Id,
            ["calendar"] = target.Label,
            ["meeting"] = cfg.Meeting.Length > 0 ? MeetingKinds.Label(cfg.Meeting) : null,
            ["instructions"] = "The time is held for this customer and is confirmed when your reply is sent. " + restate,
        };
        session.BookedId = row.Id;
        session.BookedStart = startRaw;
        session.BookedResult = JsonSerializer.Serialize(result);
        return new ToolOutcome(session.BookedResult, true);
    }

    /// <summary>
    /// The customer's own confirmed booking that alone keeps <paramref name="localStart"/> from being
    /// free (with it ignored the time is free) — a reschedule the model should start with a cancel.
    /// </summary>
    private async Task<Booking?> OwnBookingInTheWayAsync(CalendarAppConfig cfg, ToolBinding b, CalendarTarget target,
        TimeZoneInfo tz, SkillContext ctx, Contact contact, DateOnly day, DateTime localStart, int length,
        CancellationToken ct)
    {
        var startUtc = BookingAvailability.ToUtc(localStart, tz);
        var endUtc = startUtc.AddMinutes(length);
        var buffer = TimeSpan.FromMinutes(cfg.BufferMinutes);
        var mine = Upcoming(contact.Id, b.Skill, target.Key, ctx.EntityId)
            .Where(x => x.Status == BookingStatus.Booked && x.CalendarKey == target.Key
                        && BookingAvailability.Utc(x.StartUtc) - buffer < endUtc
                        && BookingAvailability.Utc(x.EndUtc) + buffer > startUtc)
            .ToList();
        foreach (var own in mine)
        {
            var r = await FreeSlotsAsync(cfg, target, tz, day, day, own.Id, ct, length);
            if (r.Ok && r.Free.Any(s => s.LocalStart == localStart)) return own;
        }
        return null;
    }

    private async Task<ToolOutcome> CancelToolAsync(ToolBinding b, JsonElement args, SkillContext ctx, CancellationToken ct)
    {
        var contact = ContactOf(ctx);
        if (contact == null)
            return Refused("This conversation isn't linked to a customer record.", "A teammate can help with their booking.");
        var idRaw = Arg(args, "booking_id") ?? "";
        if (!int.TryParse(idRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return Refused("booking_id must be a number.", $"Call {b.Base}_my_bookings to find it.");
        var session = ctx.Apps;
        if (session.CancelledId != null)
            return session.CancelledId == id
                ? new ToolOutcome(session.CancelledResult, true)
                : Refused("You already cancelled a booking in this reply — only one cancellation per reply.",
                    "Confirm that cancellation; anything else can be handled in their next message.");
        // all-or-nothing: the new time failed in this reply, so the old booking stays
        if (session.BookFailed && session.BookedId == null)
            return Refused($"Booking {id} was kept: the new time could not be booked in this reply, and a reschedule "
                           + "only cancels the old booking once the new time is booked.",
                "Tell the customer their appointment still stands and offer other free times. If they want to cancel "
                + "it without a new time, confirm that with them and cancel it in your next reply.",
                new() { ["old_booking_kept"] = id });

        var tz = TimeZoneFor(b.Config);
        var target = ResolveTarget(b.Skill, b.Config, ctx.EntityId, UserIdOf(ctx.Assignee));
        var row = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == id && x.ContactId == contact.Id, ct);
        if (row != null && !SameCompany(row, ctx.EntityId)) row = null;   // another company's appointment
        if (row == null || (row.SkillId != b.Skill.SkillId && (target.Key.Length == 0 || row.CalendarKey != target.Key)))
            return Refused($"Booking {id} isn't one of this customer's bookings.", $"Call {b.Base}_my_bookings to see them.");
        if (row.Status == BookingStatus.Cancelling)
            return Refused($"Booking {id} is already being cancelled.", "Just confirm the cancellation to the customer.");
        if (row.Status != BookingStatus.Booked)
            return Refused($"Booking {id} is {row.Status}, not a confirmed booking — there is nothing to cancel.",
                $"Call {b.Base}_my_bookings to see their confirmed bookings.");
        if (BookingAvailability.Utc(row.StartUtc) <= UtcNow)
            return Refused($"Booking {id} has already started or is in the past.", "A teammate can help with it.");
        if (row.Source == BookingSources.Google)
        {
            var integration = row.IntegrationId is int iid ? await _db.Integrations.FirstOrDefaultAsync(i => i.Id == iid, ct) : null;
            if (integration == null || GoogleOAuth.Cred(integration, "access_token").Length == 0)
                return Unavailable("Google Calendar is not connected, so the booking can't be cancelled automatically.");
        }

        if (!session.Preview)
        {
            row.Status = BookingStatus.Cancelling;
            row.CancelDraftId = null;
            row.UpdatedAt = UtcNow;
            await _db.SaveChangesAsync(ct);
            session.Actions.Add(new AiPendingAction(AiPendingAction.Cancel, row.Id));
        }
        var local = BookingAvailability.ToLocal(row.StartUtc, TimeZoneFor(row.Timezone, tz));
        if (session.Preview) // the playground: checked for real, the booking itself untouched
            session.Previews.Add(new BookingPreview(AiPendingAction.Cancel, BookingAvailability.StartLabel(local),
                BookingAvailability.LocalIso(local), row.Timezone, row.CalendarLabel, row.Id));
        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["status"] = "cancel_pending",
            ["booking_id"] = row.Id,
            ["start"] = BookingAvailability.LocalIso(local),
            ["end"] = BookingAvailability.LocalIso(BookingAvailability.ToLocal(row.EndUtc, TimeZoneFor(row.Timezone, tz))),
            ["label"] = BookingAvailability.StartLabel(local),
            ["timezone"] = row.Timezone,
            ["instructions"] = "The cancellation takes effect when your reply is sent. Confirm to the customer that "
                               + "this appointment is cancelled, restating its weekday, date and time.",
        };
        if (session.BookAttempted && session.BookedId == null)
        {
            // a reschedule: this cancel only goes through together with the new booking
            result["requires_new_booking"] = true;
            result["instructions"] = $"This is a reschedule: now call {b.Base}_book for the new time in this reply. The "
                                     + "cancellation only goes through together with the new booking — if no new time is "
                                     + "booked in this reply, booking " + row.Id + " stays and you must not say it was cancelled.";
        }
        session.CancelledId = row.Id;
        session.CancelledResult = JsonSerializer.Serialize(result);
        return new ToolOutcome(session.CancelledResult, true);
    }

    private ToolOutcome MyBookingsTool(ToolBinding b, SkillContext ctx)
    {
        var contact = ContactOf(ctx);
        if (contact == null) return Refused("This conversation isn't linked to a customer record.", "");
        var target = ResolveTarget(b.Skill, b.Config, ctx.EntityId, UserIdOf(ctx.Assignee));
        var rows = Upcoming(contact.Id, b.Skill, target.Key, ctx.EntityId);
        return new ToolOutcome(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["bookings"] = rows.Select(x => BookingJson(x)).ToList(),
            ["note"] = rows.Count == 0 ? "This customer has no upcoming bookings." : null,
        }), true);
    }

    /// <summary>The contact's upcoming bookings made for the ANSWERING conversation's company
    /// (<paramref name="entityId"/>'s) — a customer shared by several companies never hears
    /// about another company's appointments.</summary>
    private List<Booking> Upcoming(int contactId, AgentSkill skill, string calendarKey, int? entityId)
    {
        var now = UtcNow;
        return _db.Bookings.AsNoTracking()
            .Where(x => x.ContactId == contactId && (x.SkillId == skill.SkillId || (calendarKey != "" && x.CalendarKey == calendarKey))
                        && (x.Status == BookingStatus.Pending || x.Status == BookingStatus.Booked
                            || x.Status == BookingStatus.Cancelling) && x.EndUtc > now)
            .OrderBy(x => x.StartUtc).ToList()
            .Where(x => SameCompany(x, entityId)).Take(10).ToList();
    }

    /// <summary>
    /// What the booking-claim check (<see cref="Ai.BookingClaims"/>) measures a reply
    /// against: the contact's upcoming ACTIVE bookings and the ones cancelled in the
    /// last 30 days, made for the answering conversation's company — this
    /// conversation's own first — in each booking's local time. Never throws.
    /// </summary>
    public List<Ai.BookingClaims.KnownBooking> KnownForClaims(int contactId, int? threadId, int? entityId)
    {
        try
        {
            var now = UtcNow;
            var since = now.AddDays(-30);
            return _db.Bookings.AsNoTracking()
                .Where(x => x.ContactId == contactId
                            && ((x.Status == BookingStatus.Booked && x.EndUtc > now)
                                || (x.Status == BookingStatus.Cancelled && x.UpdatedAt > since)))
                .OrderByDescending(x => x.UpdatedAt).Take(40).ToList()
                .Where(x => x.ThreadId == threadId || SameCompany(x, entityId))
                .OrderByDescending(x => x.ThreadId == threadId).ThenByDescending(x => x.UpdatedAt).Take(10)
                .Select(x =>
                {
                    var tz = TimeZoneFor(x.Timezone, TimeZoneInfo.Utc);
                    return new Ai.BookingClaims.KnownBooking(BookingAvailability.ToLocal(x.StartUtc, tz),
                        BookingAvailability.ToLocal(x.EndUtc, tz), x.Status, x.UpdatedAt);
                }).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Was this booking made for the same company as <paramref name="entityId"/>?</summary>
    private bool SameCompany(Booking x, int? entityId) => _scoping.CompanyOf(x.EntityId) == _scoping.CompanyOf(entityId);

    // =========================================================================
    // lifecycle: bind → confirm | release
    // =========================================================================

    /// <summary>The reply carrying these reservations was saved as an AI draft.</summary>
    public void Bind(IEnumerable<AiPendingAction> actions, int draftId)
    {
        try
        {
            var changed = 0;
            foreach (var a in actions)
            {
                var row = _db.Bookings.FirstOrDefault(x => x.Id == a.BookingId);
                if (row == null) continue;
                if (a.Type == AiPendingAction.Book && row.DraftId == null) row.DraftId = draftId;
                else if (a.Type == AiPendingAction.Cancel && row.Status == BookingStatus.Cancelling)
                    row.CancelDraftId = draftId;
                else continue;
                row.UpdatedAt = UtcNow;
                changed++;
            }
            if (changed > 0) _db.SaveChanges();
        }
        catch { /* binding only helps housekeeping */ }
    }

    /// <summary>
    /// Nothing will be delivered as written: give the reservations back —
    /// pending → released, cancelling → booked. Booked/cancelled rows are not
    /// touched (a reply that was delivered stays delivered). Returns how many
    /// rows changed. Never throws.
    /// </summary>
    public int Release(IEnumerable<AiPendingAction> actions, string reason)
    {
        try
        {
            var n = 0;
            foreach (var a in actions.ToList())
            {
                var row = _db.Bookings.FirstOrDefault(x => x.Id == a.BookingId);
                if (row == null) continue;
                if (a.Type == AiPendingAction.Book && row.Status == BookingStatus.Pending)
                {
                    row.Status = BookingStatus.Released;
                    row.Detail = Cap(reason, 300);
                }
                else if (a.Type == AiPendingAction.Cancel && row.Status == BookingStatus.Cancelling)
                {
                    row.Status = BookingStatus.Booked;
                    row.CancelDraftId = null;
                }
                else continue;
                row.UpdatedAt = UtcNow;
                n++;
            }
            if (n > 0) _db.SaveChanges();
            return n;
        }
        catch { return 0; }
    }

    public int Release(Dictionary<string, object?>? draftContext, string reason) =>
        Release(AiPendingAction.From(draftContext), reason);

    public sealed record ConfirmResult(bool Ok, string Reason)
    {
        public static readonly ConfirmResult Nothing = new(true, "");
    }

    /// <summary>
    /// The reply is being delivered: make what it reserved, books first (a
    /// reschedule never loses the old booking when the new time is gone), then
    /// cancellations. Each booking is re-checked: still free, still in the
    /// future, calendar still connected. Any failure stops the delivery — the
    /// caller blocks the reply; the failure is audited, noted and notified
    /// here, and whatever else the reply reserved is released.
    /// </summary>
    public async Task<ConfirmResult> ConfirmAsync(AiDraft draft, string actor, CancellationToken ct = default)
    {
        var actions = AiPendingAction.From(draft.Context);
        if (actions.Count == 0) return ConfirmResult.Nothing;
        try
        {
            // a reschedule: the bookings this reply cancels are replaced by its new one, so they don't
            // make the new time "taken" — and they are only cancelled once the new one is made
            var cancelIds = actions.Where(x => x.Type == AiPendingAction.Cancel).Select(x => x.BookingId).ToList();
            var replacing = cancelIds.Count == 0 ? new List<Booking>()
                : await _db.Bookings.AsNoTracking().Where(x => cancelIds.Contains(x.Id)).ToListAsync(ct);
            foreach (var a in actions.Where(x => x.Type == AiPendingAction.Book))
            {
                var row = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == a.BookingId, ct);
                string? failure = row == null ? "The reserved booking no longer exists."
                    : row.Status == BookingStatus.Booked ? null
                    : row.Status is BookingStatus.Cancelled ? "The booking was cancelled before the reply was sent."
                    : row.Status is BookingStatus.Failed ? (row.Detail.Length > 0 ? row.Detail : "The booking failed.")
                    : await MakeAsync(row, draft.Id, actor, draft.ThreadId, ct, replacing);
                if (failure != null)
                {
                    if (row != null && row.Status is BookingStatus.Pending or BookingStatus.Released)
                    {
                        row.Status = BookingStatus.Failed;
                        row.Detail = Cap(failure, 300);
                        row.UpdatedAt = UtcNow;
                        await _db.SaveChangesAsync(ct);
                    }
                    var kept = replacing.Count == 0 ? ""
                        : $" The booking it was replacing ({string.Join(", ", replacing.Select(LabelWithZone))}) stays.";
                    if (row != null) Record(row, "booking.failed", actor,
                        $"📅 Booking failed — {LabelWithZone(row)} on {row.CalendarLabel}: {failure} The AI's reply was not sent.{kept}",
                        "Booking failed", failure, draft.ThreadId);
                    Release(actions.Where(x => x.BookingId != a.BookingId), "The reply was not sent: " + failure);
                    return new ConfirmResult(false, $"The booking could not be made: {failure}");
                }
            }
            foreach (var a in actions.Where(x => x.Type == AiPendingAction.Cancel))
            {
                var row = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == a.BookingId, ct);
                if (row == null || row.Status == BookingStatus.Cancelled) continue;
                if (row.Status is not (BookingStatus.Cancelling or BookingStatus.Booked)) continue;
                var failure = await CancelNowAsync(row, actor, "by the AI's reply", draft.ThreadId, ct);
                if (failure != null)
                {
                    row.Status = BookingStatus.Booked;
                    row.CancelDraftId = null;
                    row.UpdatedAt = UtcNow;
                    await _db.SaveChangesAsync(ct);
                    Record(row, "booking.failed", actor,
                        $"📅 Cancelling failed — {LabelWithZone(row)} on {row.CalendarLabel}: {failure} The booking stands; the AI's reply was not sent.",
                        "Booking cancellation failed", failure, draft.ThreadId);
                    return new ConfirmResult(false, $"The booking could not be cancelled: {failure}");
                }
            }
            return ConfirmResult.Nothing;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Release(actions, "The reply was not sent: the calendar step failed.");
            return new ConfirmResult(false, $"The calendar step failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>Make one reserved booking. Returns null on success, else why not.</summary>
    /// <param name="replacing">Bookings the same reply cancels (a reschedule): neither their holds nor their
    /// own Google events count against the new time.</param>
    private async Task<string?> MakeAsync(Booking row, int? draftId, string actor, int? noteThreadId,
        CancellationToken ct, IReadOnlyCollection<Booking>? replacing = null)
    {
        var now = UtcNow;
        replacing ??= Array.Empty<Booking>();
        var replacedIds = replacing.Select(x => x.Id).ToList();
        if (BookingAvailability.Utc(row.StartUtc) <= now) return "the time has already passed.";
        var taken = await _db.Bookings.AsNoTracking().AnyAsync(x => x.Id != row.Id && x.CalendarKey == row.CalendarKey
            && !replacedIds.Contains(x.Id)
            && BookingStatus.Holding.Contains(x.Status) && x.StartUtc < row.EndUtc && x.EndUtc > row.StartUtc, ct);
        if (taken) return "that time has been taken in the meantime.";

        var contact = await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == row.ContactId, ct);
        var skill = await _db.AgentSkills.AsNoTracking().Where(s => s.SkillId == row.SkillId)
            .OrderByDescending(s => s.EntityId == row.EntityId).FirstOrDefaultAsync(ct);
        var cfg = CalendarAppConfig.From(skill?.Config);

        // the video meeting is made WITH the booking (a Zoom meeting of its own, or the skill's
        // own link) — before the Google event, so the invite carries the link. A meeting that
        // can't be made never stops the booking: the note says so and a person sends a link.
        var meetingProblem = await MakeMeetingAsync(row, cfg, contact, ct);

        if (row.Source == BookingSources.Google)
        {
            var integration = row.IntegrationId is int iid
                ? await _db.Integrations.FirstOrDefaultAsync(i => i.Id == iid, ct) : null;
            if (integration == null || GoogleOAuth.Cred(integration, "access_token").Length == 0)
                return "Google Calendar is not connected.";
            var calendarId = row.CalendarKey.Split(':', 3) is { Length: 3 } parts ? parts[2] : "primary";
            var buffer = TimeSpan.FromMinutes(cfg.BufferMinutes);
            var fb = await _calendar.FreeBusyAsync(integration, calendarId, BookingAvailability.Utc(row.StartUtc) - buffer,
                BookingAvailability.Utc(row.EndUtc) + buffer, ct);
            if (!fb.Ok) return fb.NotConnected ? "Google Calendar is not connected." : fb.Error;
            if (WithoutOwnEvents(fb.Busy, replacing, row.CalendarKey)
                .Any(x => x.Start < BookingAvailability.Utc(row.EndUtc) + buffer
                          && x.End > BookingAvailability.Utc(row.StartUtc) - buffer))
                return "that time is no longer free on the calendar.";
            var email = contact?.Email ?? "";
            var meet = cfg.Meeting == MeetingKinds.GoogleMeet;
            var ev = await _calendar.InsertEventAsync(integration, calendarId, new GoogleCalendarClient.EventSpec(
                row.Title, Description(cfg, row, contact), BookingAvailability.Utc(row.StartUtc),
                BookingAvailability.Utc(row.EndUtc), row.Timezone,
                AttendeeEmail: email.Length > 0 ? email : null, SendUpdates: email.Length > 0 ? "all" : null,
                Location: row.MeetingUrl.Length > 0 ? row.MeetingUrl : null, CreateMeet: meet), ct);
            if (!ev.Ok)
            {
                // no booking ⇒ no meeting: the Zoom meeting made a moment ago goes too
                await DropMeetingAsync(row, ct);
                return ev.NotConnected ? "Google Calendar is not connected." : ev.Error;
            }
            row.ExternalEventId = ev.Id;
            row.EventLink = ev.HtmlLink;
            if (meet)
            {
                if (ev.MeetLink.Length > 0)
                {
                    row.MeetingProvider = MeetingKinds.GoogleMeet;
                    row.MeetingUrl = ev.MeetLink;
                }
                else meetingProblem = "Google didn't return a Google Meet link for the event.";
            }
        }
        else if (cfg.Meeting == MeetingKinds.GoogleMeet)
            meetingProblem = "Google Meet links need a Google Calendar — this calendar keeps its bookings in ChatBranch.";

        row.Status = BookingStatus.Booked;
        row.DraftId ??= draftId;
        row.Detail = "";
        row.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        var meetingNote = row.MeetingUrl.Length > 0
            ? $" {MeetingKinds.Label(row.MeetingProvider)} link: {row.MeetingUrl} (sent to the customer after the reply)."
            : meetingProblem != null ? $" ⚠ No meeting link: {meetingProblem} Send the customer a link yourself." : "";
        Record(row, "booking.created", actor, $"📅 Booked {LabelWithZone(row)} on {row.CalendarLabel}.{meetingNote}",
            meetingProblem != null ? "Booking confirmed — no meeting link" : "Booking confirmed", meetingProblem,
            noteThreadId);
        return null;
    }

    /// <summary>The booking's own meeting before it is made: a Zoom meeting (through the Zoom
    /// connection of the booking's entity → its parents → the organization) or the skill's own
    /// link. Google Meet is made with the Google event. Returns why there is none, or null.</summary>
    private async Task<string?> MakeMeetingAsync(Booking row, CalendarAppConfig cfg, Contact? contact, CancellationToken ct)
    {
        try
        {
            if (row.MeetingUrl.Length > 0) return null; // made already (a retried delivery)
            switch (cfg.Meeting)
            {
                case MeetingKinds.Link when MeetingKinds.IsLink(cfg.MeetingLink):
                    row.MeetingProvider = MeetingKinds.Link;
                    row.MeetingUrl = cfg.MeetingLink;
                    return null;
                case MeetingKinds.Link:
                    return "the calendar skill has no meeting link.";
                case MeetingKinds.Zoom:
                    var zoomRow = _resolver.Active(ZoomClient.Provider, row.EntityId);
                    if (zoomRow == null) return "Zoom is not connected (Integrations → Zoom).";
                    var minutes = (int)Math.Round((BookingAvailability.Utc(row.EndUtc) - BookingAvailability.Utc(row.StartUtc)).TotalMinutes);
                    var z = await _zoom.CreateMeetingAsync(zoomRow, row.Title, BookingAvailability.Utc(row.StartUtc), minutes,
                        row.Timezone, Description(cfg, row, contact), ct);
                    if (!z.Ok) return $"the Zoom meeting could not be made ({z.Error.TrimEnd('.')}).";
                    row.MeetingProvider = MeetingKinds.Zoom;
                    row.MeetingUrl = z.JoinUrl;
                    row.MeetingId = z.Id;
                    row.MeetingIntegrationId = zoomRow.Id;
                    return null;
                default:
                    return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return $"the meeting could not be made ({ex.GetType().Name}).";
        }
    }

    /// <summary>Delete the booking's Zoom meeting (cancelled / never made). Null = gone, else why not.
    /// Google Meet goes with its event; a fixed link is the skill's, never deleted.</summary>
    private async Task<string?> DropMeetingAsync(Booking row, CancellationToken ct)
    {
        try
        {
            if (row.MeetingProvider != MeetingKinds.Zoom || row.MeetingId.Length == 0) return null;
            var zoomRow = row.MeetingIntegrationId is int zid
                ? await _db.Integrations.FirstOrDefaultAsync(i => i.Id == zid, ct)
                : _resolver.Active(ZoomClient.Provider, row.EntityId);
            if (zoomRow == null) return "the Zoom connection is gone, so its meeting could not be deleted.";
            var r = await _zoom.DeleteMeetingAsync(zoomRow, row.MeetingId, ct);
            if (!r.Ok) return $"the Zoom meeting could not be deleted ({r.Error.TrimEnd('.')}).";
            if (row.Status != BookingStatus.Cancelled)
            {
                row.MeetingProvider = "";
                row.MeetingUrl = "";
                row.MeetingId = "";
                row.MeetingIntegrationId = null;
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return $"the Zoom meeting could not be deleted ({ex.GetType().Name}).";
        }
    }

    /// <summary>Cancel one booking now (Google delete when it has an event). Null = done.</summary>
    private async Task<string?> CancelNowAsync(Booking row, string actor, string how, int? noteThreadId,
        CancellationToken ct)
    {
        if (row.Source == BookingSources.Google && row.ExternalEventId.Length > 0)
        {
            var integration = row.IntegrationId is int iid
                ? await _db.Integrations.FirstOrDefaultAsync(i => i.Id == iid, ct) : null;
            if (integration == null || GoogleOAuth.Cred(integration, "access_token").Length == 0)
                return "Google Calendar is not connected.";
            var calendarId = row.CalendarKey.Split(':', 3) is { Length: 3 } parts ? parts[2] : "primary";
            var hasAttendee = await _db.Contacts.AsNoTracking()
                .AnyAsync(c => c.Id == row.ContactId && c.Email != "", ct);
            var r = await _calendar.DeleteEventAsync(integration, calendarId, row.ExternalEventId,
                hasAttendee ? "all" : null, ct);
            if (!r.Ok) return r.NotConnected ? "Google Calendar is not connected." : r.Error;
        }
        row.Status = BookingStatus.Cancelled;
        row.Detail = Cap($"Cancelled {how} ({actor}).", 300);
        row.UpdatedAt = UtcNow;
        // its Zoom meeting goes too — never a reason to keep the booking
        var meeting = await DropMeetingAsync(row, ct);
        await _db.SaveChangesAsync(ct);
        Record(row, "booking.cancelled", actor, $"📅 Cancelled {LabelWithZone(row)} on {row.CalendarLabel}."
            + (meeting != null ? $" ⚠ {char.ToUpperInvariant(meeting[0])}{meeting[1..]} Delete it in Zoom." : ""),
            "Booking cancelled", null, noteThreadId);
        return null;
    }

    /// <summary>
    /// The reply that made / cancelled these bookings was scheduled (typing
    /// pause, quiet hours) and then refused at send time: the calendar change
    /// stands, but the customer never heard. Say so in the conversation and
    /// notify, so a person tells them. Never throws.
    /// </summary>
    public void ConfirmationUndelivered(AiDraft draft, string error)
    {
        try
        {
            foreach (var a in AiPendingAction.From(draft.Context))
            {
                var row = _db.Bookings.FirstOrDefault(x => x.Id == a.BookingId);
                if (row == null) continue;
                var what = a.Type == AiPendingAction.Book && row.Status == BookingStatus.Booked ? "booked"
                    : a.Type == AiPendingAction.Cancel && row.Status == BookingStatus.Cancelled ? "cancelled"
                    : null;
                if (what == null) continue;
                Record(row, "booking.unconfirmed", "ai",
                    $"📅 {LabelWithZone(row)} is {what} on {row.CalendarLabel}, but the AI's message telling the "
                    + $"customer was not delivered ({error}). Let them know.",
                    "Booking not confirmed to the customer", error, draft.ThreadId);
            }
        }
        catch { /* a note is a side effect */ }
    }

    /// <summary>
    /// An operator cancels a booking from the dashboard: now, no reply involved.
    /// A pending reservation is cancelled too (the held reply that made it will
    /// then refuse to send as written). Returns null on success, else why not.
    /// </summary>
    public async Task<string?> OperatorCancelAsync(Booking row, string username, CancellationToken ct = default)
    {
        try
        {
            var actor = string.IsNullOrWhiteSpace(username) ? "operator" : username;
            switch (row.Status)
            {
                case BookingStatus.Booked:
                case BookingStatus.Cancelling:
                    return await CancelNowAsync(row, actor, $"by {actor}", null, ct);
                case BookingStatus.Pending:
                    row.Status = BookingStatus.Cancelled;
                    row.Detail = Cap($"Cancelled by {actor} before the reply was sent.", 300);
                    row.UpdatedAt = UtcNow;
                    await _db.SaveChangesAsync(ct);
                    Record(row, "booking.cancelled", actor,
                        $"📅 Reservation cancelled by {actor} — {LabelWithZone(row)} on {row.CalendarLabel}.",
                        "Booking cancelled", null);
                    return null;
                default:
                    return $"This booking is {row.Status} — there is nothing to cancel.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return $"The calendar step failed ({ex.GetType().Name}).";
        }
    }

    /// <summary>
    /// Worker pass: release reservations no reply will deliver — pending rows
    /// whose draft was rejected / expired / failed, or that no draft picked up
    /// within <see cref="OrphanAfter"/> — and restore cancellations the same way.
    /// Returns how many rows changed. Never throws.
    /// </summary>
    public int Housekeep()
    {
        try
        {
            var now = UtcNow;
            var cutoff = now - OrphanAfter;
            var rows = _db.Bookings.Where(b => b.Status == BookingStatus.Pending || b.Status == BookingStatus.Cancelling)
                .OrderBy(b => b.Id).Take(500).ToList();
            if (rows.Count == 0) return 0;
            var draftIds = rows.Select(b => b.Status == BookingStatus.Pending ? b.DraftId : b.CancelDraftId)
                .Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
            var drafts = _db.AiDrafts.AsNoTracking().Where(d => draftIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Status }).ToList().ToDictionary(d => d.Id, d => d.Status);
            var n = 0;
            foreach (var b in rows)
            {
                var draftId = b.Status == BookingStatus.Pending ? b.DraftId : b.CancelDraftId;
                var status = draftId is int did && drafts.TryGetValue(did, out var s) ? s : null;
                var undelivered = status is AiDraftStatus.Rejected or AiDraftStatus.Expired or AiDraftStatus.Failed
                                  or AiDraftStatus.Shadow;
                var stale = BookingAvailability.Utc(b.UpdatedAt) < cutoff
                            && status is not (AiDraftStatus.Pending);
                if (!undelivered && !stale) continue;
                if (b.Status == BookingStatus.Pending)
                {
                    b.Status = BookingStatus.Released;
                    b.Detail = status == null ? "No reply carried this reservation." : $"The reply was {AiGate.Label(status)} — nothing was booked.";
                }
                else
                {
                    b.Status = BookingStatus.Booked;
                    b.CancelDraftId = null;
                }
                b.UpdatedAt = now;
                n++;
            }
            if (n > 0) _db.SaveChanges();
            return n;
        }
        catch { return 0; }
    }

    // =========================================================================
    // labels, notes, audit, notifications
    // =========================================================================

    public static TimeZoneInfo TimeZoneFor(string? tzId, TimeZoneInfo fallback) =>
        BuiltinSkills.ResolveTimeZone(tzId) ?? fallback;

    /// <summary>"Tue 14 Oct, 3:00–3:30 PM" in the booking's own timezone.</summary>
    public static string LabelOf(Booking b)
    {
        var tz = TimeZoneFor(b.Timezone, TimeZoneInfo.Utc);
        return BookingAvailability.RangeLabel(BookingAvailability.ToLocal(b.StartUtc, tz),
            BookingAvailability.ToLocal(b.EndUtc, tz));
    }

    public static string LabelWithZone(Booking b) => $"{LabelOf(b)} ({(b.Timezone.Length > 0 ? b.Timezone : "UTC")})";

    /// <summary>A slot for the model; <paramref name="range"/> (a variable-length calendar) labels the
    /// whole span — "Mon 12 Oct, 2:00–3:00 PM" — so the length is restated with the time.</summary>
    private static Dictionary<string, object?> SlotJson(BookingAvailability.Slot s, bool range) => new()
    {
        ["start"] = BookingAvailability.LocalIso(s.LocalStart),
        ["end"] = BookingAvailability.LocalIso(s.LocalEnd),
        ["label"] = SlotLabel(s, range),
    };

    private static string SlotLabel(BookingAvailability.Slot s, bool range) => range
        ? BookingAvailability.RangeLabel(s.LocalStart, s.LocalEnd)
        : BookingAvailability.StartLabel(s.LocalStart);

    /// <summary>A slot without its end — what a full day's list shrinks to when it would not fit the result cap.</summary>
    private static Dictionary<string, object?> ShortSlotJson(BookingAvailability.Slot s, bool range) => new()
    {
        ["start"] = BookingAvailability.LocalIso(s.LocalStart),
        ["label"] = SlotLabel(s, range),
    };

    private static Dictionary<string, object?> BookingJson(Booking b)
    {
        var tz = TimeZoneFor(b.Timezone, TimeZoneInfo.Utc);
        var local = BookingAvailability.ToLocal(b.StartUtc, tz);
        var end = BookingAvailability.ToLocal(b.EndUtc, tz);
        return new()
        {
            ["booking_id"] = b.Id,
            ["start"] = BookingAvailability.LocalIso(local),
            // the STORED span — a booking may be longer than the calendar's default length
            ["end"] = BookingAvailability.LocalIso(end),
            ["duration_minutes"] = (int)Math.Round((end - local).TotalMinutes),
            ["label"] = BookingAvailability.StartLabel(local),
            ["range"] = BookingAvailability.RangeLabel(local, end),
            ["timezone"] = b.Timezone,
            ["status"] = b.Status,
            // the real link — the AI may repeat it when the customer asks for it again
            ["meeting_link"] = b.Status == BookingStatus.Booked && b.MeetingUrl.Length > 0 ? b.MeetingUrl : null,
        };
    }

    /// <summary>What the book tool tells the model about the booking's meeting link: ChatBranch sends
    /// it (the real one, made with the booking) — the model never writes a link of its own.</summary>
    private static string MeetingInstruction(CalendarAppConfig cfg) => cfg.Meeting.Length == 0 ? ""
        : $" The {MeetingKinds.Label(cfg.Meeting)} link is sent to the customer automatically, in its own message "
          + "right after your reply — you may say it is on its way, but never write a meeting link yourself.";

    // =========================================================================
    // the meeting link, sent to the customer after the reply that booked
    // =========================================================================

    public sealed record MeetingLinkMessage(Booking Row, string Text);

    /// <summary>
    /// The meeting links a DELIVERED reply's bookings carry and the customer hasn't been sent yet,
    /// each with the message it goes in (the skill's <c>meeting_message</c>, else the default in
    /// the contact's language). <see cref="AiGate.DeliverExtras"/> sends them after the reply's text.
    /// </summary>
    public List<MeetingLinkMessage> MeetingLinksToSend(Dictionary<string, object?>? draftContext)
    {
        try
        {
            var ids = AiPendingAction.From(draftContext).Where(a => a.Type == AiPendingAction.Book)
                .Select(a => a.BookingId).Distinct().ToList();
            if (ids.Count == 0) return new();
            var rows = _db.Bookings.Where(b => ids.Contains(b.Id) && b.Status == BookingStatus.Booked
                && b.MeetingUrl != "" && b.MeetingSentAt == null).OrderBy(b => b.Id).ToList();
            var list = new List<MeetingLinkMessage>();
            foreach (var row in rows)
            {
                var contact = _db.Contacts.AsNoTracking().FirstOrDefault(c => c.Id == row.ContactId);
                var skill = _db.AgentSkills.AsNoTracking().Where(x => x.SkillId == row.SkillId)
                    .OrderByDescending(x => x.EntityId == row.EntityId).FirstOrDefault();
                list.Add(new MeetingLinkMessage(row, MeetingMessage(row, CalendarAppConfig.From(skill?.Config), contact)));
            }
            return list;
        }
        catch { return new(); }
    }

    /// <summary>The link went out (or could not): stamp it so it is never sent twice; a failed
    /// send is noted for a person to follow up.</summary>
    public void MeetingLinkSent(Booking row, bool ok, string? error, int? threadId)
    {
        try
        {
            var tracked = _db.Bookings.FirstOrDefault(b => b.Id == row.Id);
            if (tracked == null) return;
            tracked.MeetingSentAt = UtcNow;
            tracked.UpdatedAt = UtcNow;
            _db.SaveChanges();
            if (!ok)
                Record(tracked, "booking.unconfirmed", "ai",
                    $"📅 The {MeetingKinds.Label(tracked.MeetingProvider)} link for {LabelWithZone(tracked)} could not be sent "
                    + $"({error ?? "send failed"}): {tracked.MeetingUrl} — send it to the customer yourself.",
                    "Meeting link not sent", error ?? "send failed", threadId);
        }
        catch { /* a stamp is a side effect */ }
    }

    /// <summary>"Here's the link to join your appointment on Tue 14 Oct, 3:00–3:30 PM: https://…".</summary>
    public static string MeetingMessage(Booking b, CalendarAppConfig cfg, Contact? c)
    {
        var time = LabelOf(b);
        if (cfg.MeetingMessage.Trim().Length > 0)
            return Render(cfg.MeetingMessage, new Dictionary<string, string>
            {
                ["link"] = b.MeetingUrl, ["time"] = time, ["name"] = c?.Name ?? "",
            });
        return (c?.Language ?? "").Trim().ToLowerInvariant() switch
        {
            "ms" => $"Ini pautan untuk menyertai temu janji anda pada {time}: {b.MeetingUrl}",
            "zh" => $"这是您 {time} 预约的会议链接：{b.MeetingUrl}",
            _ => $"Here's the link to join your appointment on {time}: {b.MeetingUrl}",
        };
    }

    /// <summary>Audit row + private note in the conversation + notification for one booking event.</summary>
    /// <param name="noteThreadId">The conversation to note it in — the reply's own conversation
    /// when a reply made it (a cancelled booking may have been made elsewhere); default the booking's.</param>
    private void Record(Booking b, string action, string actor, string note, string title, string? failure,
        int? noteThreadId = null)
    {
        Thread? thread = null;
        try
        {
            thread = (noteThreadId ?? b.ThreadId) is int tid ? _db.Threads.FirstOrDefault(t => t.Id == tid) : null;
            var contactName = _db.Contacts.AsNoTracking().Where(c => c.Id == b.ContactId).Select(c => c.Name).FirstOrDefault() ?? "";
            Audit.AuditTrail.Add(_db, action, $"booking:{b.Id}",
                $"{LabelWithZone(b)} on {b.CalendarLabel} for {(contactName.Length > 0 ? contactName : $"contact #{b.ContactId}")}"
                + (failure != null ? $" — {failure}" : ""),
                actor: actor, entityId: b.EntityId);
            if (thread != null)
            {
                _db.Messages.Add(new Message
                {
                    ThreadId = thread.Id, Direction = MessageDirection.Out, Source = MessageSource.System,
                    Private = true, Status = "note", AccountId = thread.AccountId, CreatedAt = DateTime.UtcNow,
                    Text = Cap(note, 1500),
                });
                thread.UpdatedAt = DateTime.UtcNow; // the SSE stream refreshes the chat
            }
            _db.SaveChanges();

            var who = thread?.Name is { Length: > 0 } tn ? tn : contactName.Length > 0 ? contactName : "a customer";
            var assignee = thread != null ? UserIdOf(thread.Assignee) : null;
            int? tenantId = b.EntityId is int eid
                ? _db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => (int?)e.TenantId).FirstOrDefault()
                : null;
            NotificationService.Notify(_db, NotificationCategories.Admin, "booking",
                $"{title} · {who}",
                Cap($"{LabelWithZone(b)} on {b.CalendarLabel}" + (failure != null ? $" — {failure}" : ""), 400),
                thread != null
                    ? Stores.ThreadCompanies.LegacyChatHref(thread)
                    : "/bookings",
                assignee is int aid ? NotificationAudience.ForUsers(new[] { aid }) : NotificationAudience.Admins(tenantId),
                new NotifyOptions { Thread = thread, RefKey = $"booking:{b.Id}" });
        }
        catch { /* the record is a side effect — never break the delivery */ }
    }

    private static string Description(CalendarAppConfig cfg, Booking b, Contact? c)
    {
        var parts = new List<string>();
        var custom = Render(cfg.EventDescription, Vars(c)).Trim();
        if (custom.Length > 0) parts.Add(custom);
        if (b.Note.Trim().Length > 0) parts.Add($"Note: {b.Note.Trim()}");
        if (c != null)
        {
            var reach = string.Join(" · ", new[] { c.Phone, c.Email }.Where(x => !string.IsNullOrWhiteSpace(x)));
            parts.Add($"Customer: {c.Name}{(reach.Length > 0 ? $" ({reach})" : "")}");
        }
        parts.Add("Booked through ChatBranch.");
        return Cap(string.Join("\n\n", parts), 2000);
    }

    private static Dictionary<string, string> Vars(Contact? c) => new()
    {
        ["name"] = c?.Name ?? "",
        ["phone"] = c?.Phone ?? "",
        ["email"] = c?.Email ?? "",
        ["company"] = c?.CompanyName ?? "",
    };

    public static string Render(string template, Dictionary<string, string> vars)
    {
        var s = template ?? "";
        foreach (var (k, v) in vars) s = s.Replace("{{" + k + "}}", v).Replace("{{ " + k + " }}", v);
        return s.Trim();
    }

    // ---- small helpers ------------------------------------------------------------

    private Contact? ContactOf(SkillContext ctx)
    {
        if (ctx.ContactRecord != null) return ctx.ContactRecord;
        if (ctx.ContactId is not int cid) return null;
        try { return _db.Contacts.AsNoTracking().FirstOrDefault(c => c.Id == cid); }
        catch { return null; }
    }

    private static JsonElement ParseArgs(string? argsJson)
    {
        try
        {
            var el = string.IsNullOrWhiteSpace(argsJson) ? default : JsonDocument.Parse(argsJson).RootElement.Clone();
            if (el.ValueKind == JsonValueKind.Object) return el;
        }
        catch { /* treat as no arguments */ }
        return JsonDocument.Parse("{}").RootElement.Clone();
    }

    private static string? Arg(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>"2026-10-14", "today", "tomorrow" (also a full date-time) → a local date.</summary>
    public static DateOnly? ParseDate(string? raw, DateOnly today)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        if (s == "today") return today;
        if (s == "tomorrow") return today.AddDays(1);
        if (s.Length >= 10 && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    private static ToolOutcome Error(string error, string advice, bool ok) => new(JsonSerializer.Serialize(
        new Dictionary<string, object?> { ["ok"] = false, ["error"] = error, ["advice"] = advice }), ok);

    /// <summary>The calendar can't be used (not connected, Google error) — a real tool failure.</summary>
    private static ToolOutcome Unavailable(string why) => Error(
        $"The calendar is not available right now ({why.TrimEnd('.')}).",
        "Don't offer or promise any time. Tell the customer a teammate will confirm an appointment time with them, and offer to hand over to a person.",
        ok: false);

    /// <summary>A booking rule said no (taken, cap, bad input) — the tool worked; the model adapts.</summary>
    private static ToolOutcome Refused(string error, string advice, Dictionary<string, object?>? extra = null)
    {
        var payload = new Dictionary<string, object?> { ["ok"] = false, ["error"] = error, ["advice"] = advice };
        foreach (var (k, v) in extra ?? new()) payload[k] = v;
        return new ToolOutcome(JsonSerializer.Serialize(payload), true);
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Cap(string? s, int n) => (s ?? "").Length <= n ? s ?? "" : s![..n];
}
