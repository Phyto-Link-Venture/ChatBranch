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

    public BookingService(AppDbContext db, ScopingService scoping, IntegrationResolver? resolver = null,
        GoogleCalendarClient? calendar = null, TimeProvider? clock = null)
    {
        _db = db;
        _scoping = scoping;
        _resolver = resolver ?? new IntegrationResolver(db, scoping);
        _calendar = calendar ?? new GoogleCalendarClient(db,
            new GoogleOAuth(db, scoping, PlainHttpClientFactory.Instance), PlainHttpClientFactory.Instance);
        _clock = clock ?? TimeProvider.System;
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

    /// <summary>Every free slot on [from, to] for this calendar (working hours − busy − holds).</summary>
    public async Task<AvailabilityResult> FreeSlotsAsync(CalendarAppConfig cfg, CalendarTarget target,
        TimeZoneInfo tz, DateOnly from, DateOnly to, int? ignoreBookingId = null, CancellationToken ct = default)
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
            busy.AddRange(fb.Busy);
        }
        var free = BookingAvailability.FreeSlots(cfg, tz, now, from, to, busy);
        return new AvailabilityResult(true, "", free, from, to);
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
        var dur = $"{cfg.DurationMinutes}-minute";
        if (cfg.Allows("availability"))
            yield return (b + "_availability", "availability", new LlmTool(b + "_availability",
                $"Check open appointment times ({dur}) on {calendar}. Returns up to 8 free start times spread across "
                + $"the dates, in {tzId} local time, each with a ready-made label. Call this BEFORE suggesting any "
                + "time and offer the customer two or three of the returned options, each with weekday, date, time "
                + "and timezone — never invent, guess or round a time. Dates are YYYY-MM-DD in the business's local "
                + "time: work out 'today', 'tomorrow' or 'next Tuesday' from the current date in the system prompt. "
                + $"Bookings are possible up to {cfg.MaxDaysAhead} days ahead.",
                Schema("{\"type\":\"object\",\"properties\":{"
                       + "\"from_date\":{\"type\":\"string\",\"description\":\"First day to search, YYYY-MM-DD (default: today)\"},"
                       + "\"to_date\":{\"type\":\"string\",\"description\":\"Last day to search, YYYY-MM-DD (default: 7 days after from_date). Use the same date as from_date to see one day.\"}"
                       + "}}")));
        if (cfg.Allows("book"))
            yield return (b + "_book", "book", new LlmTool(b + "_book",
                $"Book ONE {dur} appointment for this customer (the person in this conversation) at a start time "
                + $"returned by {b}_availability. Call it ONLY after the customer clearly chose that specific time — "
                + "never on your own pick, never for 'any time' and never while they are still deciding. "
                + $"`start` is the local start time as YYYY-MM-DDTHH:mm ({tzId}). The time is held for them and "
                + "confirmed when your reply is sent: in that reply, confirm the booking and restate the weekday, "
                + "date, time and timezone exactly as this tool returns them. If it fails, apologise and offer the "
                + "alternatives it returns.",
                Schema("{\"type\":\"object\",\"properties\":{"
                       + "\"start\":{\"type\":\"string\",\"description\":\"Chosen start, YYYY-MM-DDTHH:mm in the calendar's local time, exactly as returned by the availability tool\"},"
                       + "\"note\":{\"type\":\"string\",\"description\":\"Optional short note for the booking (what the appointment is about)\"}"
                       + "},\"required\":[\"start\"]}")));
        if (cfg.Allows("cancel"))
        {
            yield return (b + "_my_bookings", "my_bookings", new LlmTool(b + "_my_bookings",
                "List this customer's upcoming bookings (booking_id, date, time, status). Use it to find the "
                + "booking_id before cancelling or rescheduling, or when they ask when their appointment is.",
                Schema("{\"type\":\"object\",\"properties\":{}}")));
            yield return (b + "_cancel", "cancel", new LlmTool(b + "_cancel",
                $"Cancel one of this customer's upcoming bookings (booking_id from {b}_my_bookings). Only after "
                + "the customer asked to cancel or reschedule and confirmed which appointment. To reschedule, call "
                + $"{b}_cancel for the old booking AND {b}_book for the new time in the same reply. The cancellation "
                + "takes effect when your reply is sent — confirm it with the date and time.",
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
        try
        {
            var args = ParseArgs(argsJson);
            var outcome = binding.Action switch
            {
                "availability" => await AvailabilityToolAsync(binding, args, ctx, ct),
                "book" => await BookToolAsync(binding, args, ctx, ct),
                "cancel" => await CancelToolAsync(binding, args, ctx, ct),
                "my_bookings" => MyBookingsTool(binding, ctx),
                _ => Error($"unknown calendar action '{binding.Action}'", "", ok: false),
            };
            return outcome.Json.Length <= ResultCap ? outcome : outcome with { Json = outcome.Json[..ResultCap] };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Error($"The calendar tool failed ({ex.GetType().Name}).",
                "Don't promise a time. Tell the customer a teammate will confirm the appointment.", ok: false);
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
        var from = ParseDate(Arg(args, "from_date"), today) ?? today;
        var to = ParseDate(Arg(args, "to_date"), today) ?? from.AddDays(7);
        if (to < from) (from, to) = (to, from);
        if (from > last)
            return Refused($"Bookings can only be made up to {last:yyyy-MM-dd} ({cfg.MaxDaysAhead} days ahead).",
                $"Offer times on or before {last:dddd d MMMM}.", new() { ["last_bookable_date"] = last.ToString("yyyy-MM-dd") });

        var r = await FreeSlotsAsync(cfg, target, tz, from, to, ct: ct);
        if (!r.Ok) return Unavailable(r.Error);
        var offered = BookingAvailability.Spread(r.Free);
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["timezone"] = tz.Id,
            ["calendar"] = target.Label,
            ["duration_minutes"] = cfg.DurationMinutes,
            ["from_date"] = r.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to_date"] = r.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["slots"] = offered.Select(SlotJson).ToList(),
            ["free_slots_in_range"] = r.Free.Count,
            ["last_bookable_date"] = last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        payload["note"] = offered.Count == 0
            ? $"No free times between {r.From:yyyy-MM-dd} and {r.To:yyyy-MM-dd}. Try other dates (up to {last:yyyy-MM-dd}) — don't offer a time that wasn't returned."
            : r.Free.Count > offered.Count
                ? "A spread of the free times; ask for a specific day (from_date = to_date) to see more on that day. Offer 2–3 of these, with weekday, date, time and timezone."
                : "These are all the free times in the range. Offer 2–3 of them, with weekday, date, time and timezone.";
        var upcoming = ctx.ContactId is int cid ? Upcoming(cid, b.Skill, target.Key, ctx.EntityId) : new();
        if (upcoming.Count > 0)
            payload["customer_upcoming_bookings"] = upcoming.Select(x => BookingJson(x)).ToList();
        return new ToolOutcome(JsonSerializer.Serialize(payload), true);
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
        if (session.BookedId != null)
            return startRaw == session.BookedStart
                ? new ToolOutcome(session.BookedResult, true)
                : Refused("You already reserved a time in this reply — only one booking per reply.",
                    "Confirm the time you already booked; if the customer wants a different one, they can ask in their next message.");

        var tz = TimeZoneFor(cfg);
        var target = ResolveTarget(b.Skill, cfg, ctx.EntityId, UserIdOf(ctx.Assignee));
        if (!target.Ok) return Unavailable(target.Error!);
        var when = SkillService.ParseInstant(startRaw, tz);
        if (when == null)
            return Refused($"'{startRaw}' is not a start time.",
                $"Pass `start` as YYYY-MM-DDTHH:mm, exactly as returned by {b.Base}_availability.");
        var localStart = BookingAvailability.ToLocal(when.Value.UtcDateTime, tz);
        var day = DateOnly.FromDateTime(localStart);
        var now = UtcNow;

        // the per-contact cap (cancelling rows are on their way out — a reschedule)
        var active = (await _db.Bookings.AsNoTracking()
            .Where(x => x.ContactId == contact.Id && x.SkillId == b.Skill.SkillId
                        && (x.Status == BookingStatus.Pending || x.Status == BookingStatus.Booked) && x.EndUtc > now)
            .OrderBy(x => x.StartUtc).ToListAsync(ct)).Where(x => SameCompany(x, ctx.EntityId)).ToList();
        if (active.Count >= cfg.MaxActivePerContact)
        {
            var first = active[0];
            return Refused(
                $"This customer already has {(active.Count == 1 ? "an upcoming booking" : $"{active.Count} upcoming bookings")} "
                + $"({LabelOf(first)}, booking_id {first.Id}) — the limit is {cfg.MaxActivePerContact}.",
                cfg.Allows("cancel")
                    ? $"Offer to reschedule instead: with their agreement call {b.Base}_cancel for booking {first.Id} and then {b.Base}_book for the new time, in the same reply."
                    : "Tell them about their existing booking; a teammate can help change it.",
                new() { ["existing_booking"] = BookingJson(first) });
        }

        var r = await FreeSlotsAsync(cfg, target, tz, day, day, ct: ct);
        if (!r.Ok) return Unavailable(r.Error);
        var slot = r.Free.FirstOrDefault(s => s.LocalStart == localStart);
        if (slot == null)
        {
            var alternatives = BookingAvailability.Spread(r.Free, 3);
            if (alternatives.Count == 0)
            {
                var more = await FreeSlotsAsync(cfg, target, tz, day.AddDays(1), day.AddDays(7), ct: ct);
                alternatives = more.Ok ? BookingAvailability.Spread(more.Free, 3) : new();
            }
            return Refused($"{BookingAvailability.StartLabel(localStart)} ({tz.Id}) is not an available time.",
                alternatives.Count > 0
                    ? "Apologise, and offer the alternatives below (or check other dates with the availability tool). Book only after they choose."
                    : "Apologise and check other dates with the availability tool.",
                new() { ["alternatives"] = alternatives.Select(SlotJson).ToList() });
        }

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
                ["label"] = BookingAvailability.StartLabel(slot.LocalStart),
                ["timezone"] = tz.Id,
                ["calendar"] = target.Label,
                ["instructions"] = "The time is held for this customer and is confirmed when your reply is sent. "
                                   + "In your reply, confirm the booking and restate the weekday, date, time and timezone.",
            };
            session.Previews.Add(new BookingPreview(AiPendingAction.Book, BookingAvailability.StartLabel(slot.LocalStart),
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

        // two conversations racing for one slot: the first reservation wins
        var clash = await _db.Bookings.AsNoTracking().AnyAsync(x => x.Id < row.Id && x.CalendarKey == row.CalendarKey
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
            ["label"] = BookingAvailability.StartLabel(slot.LocalStart),
            ["timezone"] = tz.Id,
            ["calendar"] = target.Label,
            ["instructions"] = "The time is held for this customer and is confirmed when your reply is sent. "
                               + "In your reply, confirm the booking and restate the weekday, date, time and timezone.",
        };
        session.BookedId = row.Id;
        session.BookedStart = startRaw;
        session.BookedResult = JsonSerializer.Serialize(result);
        return new ToolOutcome(session.BookedResult, true);
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
            ["label"] = BookingAvailability.StartLabel(local),
            ["timezone"] = row.Timezone,
            ["instructions"] = "The cancellation takes effect when your reply is sent. Confirm to the customer that "
                               + "this appointment is cancelled, restating its weekday, date and time.",
        };
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
            foreach (var a in actions.Where(x => x.Type == AiPendingAction.Book))
            {
                var row = await _db.Bookings.FirstOrDefaultAsync(x => x.Id == a.BookingId, ct);
                string? failure = row == null ? "The reserved booking no longer exists."
                    : row.Status == BookingStatus.Booked ? null
                    : row.Status is BookingStatus.Cancelled ? "The booking was cancelled before the reply was sent."
                    : row.Status is BookingStatus.Failed ? (row.Detail.Length > 0 ? row.Detail : "The booking failed.")
                    : await MakeAsync(row, draft.Id, actor, draft.ThreadId, ct);
                if (failure != null)
                {
                    if (row != null && row.Status is BookingStatus.Pending or BookingStatus.Released)
                    {
                        row.Status = BookingStatus.Failed;
                        row.Detail = Cap(failure, 300);
                        row.UpdatedAt = UtcNow;
                        await _db.SaveChangesAsync(ct);
                    }
                    if (row != null) Record(row, "booking.failed", actor,
                        $"📅 Booking failed — {LabelWithZone(row)} on {row.CalendarLabel}: {failure} The AI's reply was not sent.",
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
    private async Task<string?> MakeAsync(Booking row, int? draftId, string actor, int? noteThreadId,
        CancellationToken ct)
    {
        var now = UtcNow;
        if (BookingAvailability.Utc(row.StartUtc) <= now) return "the time has already passed.";
        var taken = await _db.Bookings.AsNoTracking().AnyAsync(x => x.Id != row.Id && x.CalendarKey == row.CalendarKey
            && BookingStatus.Holding.Contains(x.Status) && x.StartUtc < row.EndUtc && x.EndUtc > row.StartUtc, ct);
        if (taken) return "that time has been taken in the meantime.";

        var contact = await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == row.ContactId, ct);
        var skill = await _db.AgentSkills.AsNoTracking().Where(s => s.SkillId == row.SkillId)
            .OrderByDescending(s => s.EntityId == row.EntityId).FirstOrDefaultAsync(ct);
        var cfg = CalendarAppConfig.From(skill?.Config);

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
            if (fb.Busy.Any(x => x.Start < BookingAvailability.Utc(row.EndUtc) + buffer
                                 && x.End > BookingAvailability.Utc(row.StartUtc) - buffer))
                return "that time is no longer free on the calendar.";
            var email = contact?.Email ?? "";
            var ev = await _calendar.InsertEventAsync(integration, calendarId, new GoogleCalendarClient.EventSpec(
                row.Title, Description(cfg, row, contact), BookingAvailability.Utc(row.StartUtc),
                BookingAvailability.Utc(row.EndUtc), row.Timezone,
                AttendeeEmail: email.Length > 0 ? email : null, SendUpdates: email.Length > 0 ? "all" : null), ct);
            if (!ev.Ok) return ev.NotConnected ? "Google Calendar is not connected." : ev.Error;
            row.ExternalEventId = ev.Id;
            row.EventLink = ev.HtmlLink;
        }

        row.Status = BookingStatus.Booked;
        row.DraftId ??= draftId;
        row.Detail = "";
        row.UpdatedAt = UtcNow;
        await _db.SaveChangesAsync(ct);
        Record(row, "booking.created", actor, $"📅 Booked {LabelWithZone(row)} on {row.CalendarLabel}.",
            "Booking confirmed", null, noteThreadId);
        return null;
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
        await _db.SaveChangesAsync(ct);
        Record(row, "booking.cancelled", actor, $"📅 Cancelled {LabelWithZone(row)} on {row.CalendarLabel}.",
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

    private static Dictionary<string, object?> SlotJson(BookingAvailability.Slot s) => new()
    {
        ["start"] = BookingAvailability.LocalIso(s.LocalStart),
        ["end"] = BookingAvailability.LocalIso(s.LocalEnd),
        ["label"] = BookingAvailability.StartLabel(s.LocalStart),
    };

    private static Dictionary<string, object?> BookingJson(Booking b)
    {
        var tz = TimeZoneFor(b.Timezone, TimeZoneInfo.Utc);
        var local = BookingAvailability.ToLocal(b.StartUtc, tz);
        return new()
        {
            ["booking_id"] = b.Id,
            ["start"] = BookingAvailability.LocalIso(local),
            ["label"] = BookingAvailability.StartLabel(local),
            ["timezone"] = b.Timezone,
            ["status"] = b.Status,
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
