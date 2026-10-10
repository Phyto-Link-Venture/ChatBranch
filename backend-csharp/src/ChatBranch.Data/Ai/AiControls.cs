using System.Globalization;
using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>The Setting keys behind the AI on/off controls (all entity-scoped).</summary>
public static class AiControlKeys
{
    /// <summary>The master switch: on | off | schedule.</summary>
    public const string Master = "ai_enabled";
    /// <summary>Per-weekday windows: {"mon":[["18:00","23:59"]], …} (org timezone).</summary>
    public const string Schedule = "ai_schedule";
    /// <summary>inside = the AI answers only inside the windows · outside = only outside them.</summary>
    public const string ScheduleMode = "ai_schedule_mode";
    /// <summary>Comma list of contact tags whose carriers the AI never answers.</summary>
    public const string NeverTags = "ai_never_tags";

    public static readonly string[] All = { Master, Schedule, ScheduleMode, NeverTags };
}

/// <summary>The master switch's values.</summary>
public static class AiMasterModes
{
    public const string On = "on";
    public const string Off = "off";
    public const string Schedule = "schedule";
    public static readonly string[] All = { On, Off, Schedule };

    public static string Normalize(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "off" or "false" or "0" or "disabled" => Off,
        "schedule" or "scheduled" => Schedule,
        _ => On,
    };
}

public static class AiScheduleModes
{
    public const string Inside = "inside";
    public const string Outside = "outside";

    public static string Normalize(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() == Outside ? Outside : Inside;
}

/// <summary><see cref="Contact.AiMode"/>'s values.</summary>
public static class ContactAiModes
{
    public const string Follow = "";
    public const string Never = "never";
    public const string Always = "always";
    public static readonly string[] All = { Follow, Never, Always };

    /// <summary>The stored value, or null when <paramref name="raw"/> is not one.</summary>
    public static string? Parse(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "follow" or "default" or "inherit" => Follow,
        "never" or "off" => Never,
        "always" or "on" => Always,
        _ => null,
    };

    public static string Normalize(string? raw) => Parse(raw) ?? Follow;
}

/// <summary>Which rule decided (wire values of <c>ai_rule</c>).</summary>
public static class AiRules
{
    public const string OptedOut = "opted_out";
    public const string MasterOff = "master_off";
    public const string Schedule = "schedule";
    public const string ContactNever = "contact_never";
    public const string NeverTag = "never_tag";
    /// <summary>The conversation's entity (or an ancestor) used up its monthly AI quota.</summary>
    public const string Quota = "quota";
    public const string ChatTick = "chat_tick";
    public const string ContactAlways = "contact_always";
    public const string InboxDefault = "inbox_default";
    public const string RoleDefault = "role_default";
    public const string Thread = "thread";
    public const string Error = "error";
}

/// <summary>
/// Is the AI on for one conversation, which rule said so, and the sentence the
/// dashboard shows (<c>ai_reason</c>). <see cref="Forced"/> = a rule ABOVE the
/// per-chat tick decided, so the chat's AI switch is shown disabled.
/// </summary>
public sealed record AiVerdict(bool Enabled, string Rule, string Reason, bool Forced);

/// <summary>One weekday window, wall-clock in the org timezone. Start &gt; End
/// runs past midnight; Start == End is the whole day.</summary>
public readonly record struct AiWindow(TimeOnly Start, TimeOnly End);

/// <summary>
/// The AI schedule — the same per-weekday shape as the booking hours
/// (<c>{"mon":[["18:00","23:59"]], …}</c>). A single pair (<c>"mon":["09:00","18:00"]</c>)
/// and whole hours (<c>[[9,18]]</c>) are accepted on the way in; anything
/// unreadable is dropped, never thrown.
/// </summary>
public sealed class AiSchedule
{
    public static readonly string[] Days = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };

    public Dictionary<string, List<AiWindow>> Windows { get; } = new();

    public bool IsEmpty => Windows.Values.All(w => w.Count == 0);

    public static AiSchedule Parse(string? raw)
    {
        var s = new AiSchedule();
        if (string.IsNullOrWhiteSpace(raw)) return s;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return From(doc.RootElement);
        }
        catch { return s; }
    }

    public static AiSchedule From(JsonElement root)
    {
        var s = new AiSchedule();
        if (root.ValueKind != JsonValueKind.Object) return s;
        foreach (var p in root.EnumerateObject())
        {
            var day = DayKey(p.Name);
            if (day == null || p.Value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<AiWindow>();
            var items = p.Value.EnumerateArray().ToList();
            // "mon": ["09:00","18:00"] — one pair, not a list of pairs
            if (items.Count == 2 && items.All(i => i.ValueKind is JsonValueKind.String or JsonValueKind.Number))
                items = new List<JsonElement> { p.Value };
            foreach (var pair in items)
            {
                if (pair.ValueKind != JsonValueKind.Array) continue;
                var ends = pair.EnumerateArray().ToList();
                if (ends.Count < 2 || !TryTime(ends[0], out var a) || !TryTime(ends[1], out var b)) continue;
                list.Add(new AiWindow(a, b));
            }
            if (list.Count > 0) s.Windows[day] = list.Take(6).ToList();
        }
        return s;
    }

    /// <summary>The normalized JSON stored in the Setting row.</summary>
    public string ToJson()
    {
        var o = new Dictionary<string, List<List<string>>>();
        foreach (var d in Days)
            if (Windows.TryGetValue(d, out var w) && w.Count > 0)
                o[d] = w.Select(x => new List<string> { Hm(x.Start), Hm(x.End) }).ToList();
        return JsonSerializer.Serialize(o);
    }

    public Dictionary<string, List<List<string>>> ToWire()
    {
        var o = new Dictionary<string, List<List<string>>>();
        foreach (var d in Days)
            o[d] = Windows.TryGetValue(d, out var w)
                ? w.Select(x => new List<string> { Hm(x.Start), Hm(x.End) }).ToList()
                : new List<List<string>>();
        return o;
    }

    /// <summary>Is this org-local moment inside one of the windows?</summary>
    public bool Contains(DateTime local)
    {
        var t = TimeOnly.FromDateTime(local);
        if (Windows.TryGetValue(Key(local.DayOfWeek), out var today))
            foreach (var w in today)
            {
                if (w.Start == w.End) return true;                         // all day
                if (w.Start < w.End)
                {
                    // "…–23:59" means to the end of the day
                    if (t >= w.Start && (t < w.End || w.End == LastMinute)) return true;
                }
                else if (t >= w.Start) return true;                         // overnight, first half
            }
        if (Windows.TryGetValue(Key(local.AddDays(-1).DayOfWeek), out var yesterday))
            foreach (var w in yesterday)
                if (w.Start > w.End && t < w.End) return true;              // overnight, second half
        return false;
    }

    private static readonly TimeOnly LastMinute = new(23, 59);

    public static string Key(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "mon", DayOfWeek.Tuesday => "tue", DayOfWeek.Wednesday => "wed",
        DayOfWeek.Thursday => "thu", DayOfWeek.Friday => "fri", DayOfWeek.Saturday => "sat", _ => "sun",
    };

    private static string? DayKey(string raw)
    {
        var k = raw.Trim().ToLowerInvariant();
        if (k.Length >= 3) k = k[..3];
        return Days.Contains(k) ? k : null;
    }

    private static bool TryTime(JsonElement el, out TimeOnly t)
    {
        t = default;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var h) && h >= 0 && h <= 24)
        {
            t = h >= 24 ? LastMinute : new TimeOnly((int)h, (int)Math.Round((h - Math.Floor(h)) * 60) % 60);
            return true;
        }
        if (el.ValueKind != JsonValueKind.String) return false;
        var s = (el.GetString() ?? "").Trim();
        if (s == "24:00") { t = LastMinute; return true; }
        return TimeOnly.TryParseExact(s, new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out t);
    }

    private static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// The master switch as it applies to one entity: resolved up the entity TREE
/// (the nearest level with a value wins — an outlet's own row, then its
/// parent's, …, then the organization's), so switching a head office off
/// silences its branches unless a branch says otherwise.
/// </summary>
public sealed record AiMaster(
    string Mode,
    AiSchedule Schedule,
    string ScheduleMode,
    int? ScopeEntityId,
    string ScopeName,
    string ModeFrom)
{
    public static AiMaster Default { get; } =
        new(AiMasterModes.On, new AiSchedule(), AiScheduleModes.Inside, null, "the organization", "default");

    /// <summary>May the AI act at this moment (master switch + schedule)?</summary>
    public bool AllowsAt(DateTime nowUtc, TimeZoneInfo zone)
    {
        if (Mode == AiMasterModes.Off) return false;
        if (Mode != AiMasterModes.Schedule) return true;
        var inside = Schedule.Contains(Flows.OrgClock.ToOrg(nowUtc, zone));
        return ScheduleMode == AiScheduleModes.Outside ? !inside : inside;
    }

    /// <summary>When the answer of <see cref="AllowsAt"/> next flips (5-minute
    /// steps over the coming 8 days), or null when it never does.</summary>
    public DateTime? NextChangeUtc(DateTime nowUtc, TimeZoneInfo zone)
    {
        if (Mode != AiMasterModes.Schedule) return null;
        var now = AllowsAt(nowUtc, zone);
        var start = nowUtc.AddTicks(-(nowUtc.Ticks % TimeSpan.FromMinutes(5).Ticks));
        for (var i = 1; i <= 8 * 24 * 12; i++)
        {
            var probe = start.AddMinutes(5 * i);
            if (AllowsAt(probe, zone) != now) return probe;
        }
        return null;
    }
}

/// <summary>Everything above the per-chat tick that decides whether the AI may answer.</summary>
public sealed record AiPolicyInputs(
    AiMaster Master,
    DateTime NowUtc,
    TimeZoneInfo Zone,
    string ContactMode,
    IReadOnlyCollection<string> ContactTags,
    IReadOnlyCollection<string> NeverTags,
    /// <summary>Set = an AI usage quota is reached: the <c>ai_reason</c> (<see cref="AiQuota.Reason"/>).</summary>
    string? QuotaBlock = null);

/// <summary>
/// Resolves the AI controls (master switch, schedule, never-tags) from Setting
/// rows and decides conversations in bulk. ONE instance per request or pass —
/// it caches per entity, so a 200-row inbox page costs a handful of queries.
/// The rule itself lives in <see cref="AiEligibility.Decide(Thread, ChannelAccount?, bool, AiPolicyInputs)"/>.
/// </summary>
public sealed class AiControls
{
    private readonly AppDbContext _db;
    private readonly ScopingService _sc;
    /// <summary>Entities looked up so far (id → parent, name) — a tree walk costs one query per level, once.</summary>
    private readonly Dictionary<int, (int? ParentId, string Name)?> _entities = new();
    private readonly Dictionary<int, AiMaster> _masters = new();
    private readonly Dictionary<int, List<string>> _never = new();
    private readonly Dictionary<int, bool> _reminder = new();
    private TimeZoneInfo? _zone;

    public AiControls(AppDbContext db, ScopingService? scoping = null, DateTime? nowUtc = null)
    {
        _db = db;
        _sc = scoping ?? new ScopingService(db);
        NowUtc = nowUtc ?? DateTime.UtcNow;
    }

    public DateTime NowUtc { get; }

    /// <summary>Resolve the schedule even when the switch is not on "schedule" (the settings screen).</summary>
    public bool AlwaysLoadSchedule { get; init; }

    /// <summary>Leave the AI usage quota out (what would the verdict be without it?).</summary>
    public bool IgnoreQuota { get; init; }

    private readonly Dictionary<int, string?> _quota = new();

    /// <summary>The quota reason for an entity's conversations (its own cap or any ancestor's), or null.</summary>
    public string? QuotaBlock(int? entityId)
    {
        if (IgnoreQuota || entityId is not int id) return null;
        if (_quota.TryGetValue(id, out var cached)) return cached;
        var s = AiQuota.EntityBlock(_db, _sc, id, NowUtc);
        return _quota[id] = s == null ? null : AiQuota.Reason(s);
    }

    public TimeZoneInfo Zone => _zone ??= Skills.BuiltinSkills.OrgTimeZone(_sc);

    // ---- the entity tree -------------------------------------------------------

    private (int? ParentId, string Name)? EntityRow(int id)
    {
        if (_entities.TryGetValue(id, out var cached)) return cached;
        var row = _db.Entities.AsNoTracking().Where(e => e.Id == id)
            .Select(e => new { e.ParentId, e.Name }).FirstOrDefault();
        (int? ParentId, string Name)? v = row == null ? null : (row.ParentId, row.Name);
        _entities[id] = v;
        return v;
    }

    /// <summary>The entity and its ancestors, nearest first (cycle-safe).</summary>
    public List<int> Chain(int? entityId)
    {
        var chain = new List<int>();
        var cur = entityId;
        while (cur is int id && EntityRow(id) is { } e && !chain.Contains(id) && chain.Count < 16)
        {
            chain.Add(id);
            cur = e.ParentId;
        }
        return chain;
    }

    public string EntityName(int? entityId) =>
        entityId is int id && EntityRow(id) is { } e ? e.Name : "the organization";

    /// <summary>One key up the tree: (value, the entity that set it — null = the organization row).</summary>
    public (string Value, int? At, bool Found) Resolve(string key, int? entityId)
    {
        var chain = Chain(entityId);
        var rows = _db.Settings.AsNoTracking()
            .Where(s => s.Key == key && (s.EntityId == null || chain.Contains(s.EntityId.Value)))
            .Select(s => new { s.EntityId, s.Value }).ToList();
        foreach (var level in chain)
        {
            var row = rows.FirstOrDefault(r => r.EntityId == level);
            if (row != null && !string.IsNullOrWhiteSpace(row.Value)) return (row.Value.Trim(), level, true);
        }
        var global = rows.FirstOrDefault(r => r.EntityId == null);
        if (global != null && !string.IsNullOrWhiteSpace(global.Value)) return (global.Value.Trim(), null, true);
        return ("", null, false);
    }

    // ---- master switch -----------------------------------------------------------

    public AiMaster Master(int? entityId)
    {
        var k = entityId ?? 0;
        if (_masters.TryGetValue(k, out var cached)) return cached;
        AiMaster m;
        try
        {
            var (raw, at, found) = Resolve(AiControlKeys.Master, entityId);
            var mode = found ? AiMasterModes.Normalize(raw) : AiMasterModes.On;
            // the hours only matter on a schedule (the settings screen asks for them always)
            var needHours = mode == AiMasterModes.Schedule || AlwaysLoadSchedule;
            var schedule = needHours ? AiSchedule.Parse(Resolve(AiControlKeys.Schedule, entityId).Value) : new AiSchedule();
            var smode = needHours
                ? AiScheduleModes.Normalize(Resolve(AiControlKeys.ScheduleMode, entityId).Value)
                : AiScheduleModes.Inside;
            var from = !found ? "default" : at == null ? "organization" : at == entityId ? "entity" : "inherited";
            m = new AiMaster(mode, schedule, smode, at, EntityName(at), from);
        }
        catch
        {
            m = AiMaster.Default; // unreadable settings never switch the whole AI off by accident
        }
        _masters[k] = m;
        return m;
    }

    // ---- never-AI tags -------------------------------------------------------------

    /// <summary>
    /// The never-AI tags in force for an entity: the organization's PLUS every
    /// level up the tree (a "never" list only ever adds — a branch cannot
    /// re-enable the AI for the head office's VIPs).
    /// </summary>
    public List<string> NeverTags(int? entityId)
    {
        var k = entityId ?? 0;
        if (_never.TryGetValue(k, out var cached)) return cached;
        var outv = new List<string>();
        try
        {
            var chain = Chain(entityId);
            var rows = _db.Settings.AsNoTracking()
                .Where(s => s.Key == AiControlKeys.NeverTags
                            && (s.EntityId == null || chain.Contains(s.EntityId.Value)))
                .Select(s => s.Value).ToList();
            foreach (var row in rows) outv.AddRange(SplitTags(row));
        }
        catch { /* no list */ }
        outv = outv.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _never[k] = outv;
        return outv;
    }

    public static List<string> SplitTags(string? raw) => (raw ?? "")
        .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(t => t.Length > 0).Select(t => t.Length > 64 ? t[..64] : t)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The first contact tag that is on the never-list (names compare
    /// case-insensitively, also as slugs: "VIP Client" matches "vip-client").</summary>
    public static string? NeverTagHit(IEnumerable<string>? contactTags, IReadOnlyCollection<string> never)
    {
        if (contactTags == null || never.Count == 0) return null;
        var wanted = never.SelectMany(n => new[] { n.Trim().ToLowerInvariant(), Slug(n) }).ToHashSet();
        return contactTags.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)
            && (wanted.Contains(t.Trim().ToLowerInvariant()) || wanted.Contains(Slug(t))));
    }

    private static string Slug(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    // ---- deciding --------------------------------------------------------------------

    public bool ReminderDefaultOn(int? entityId)
    {
        var k = entityId ?? 0;
        if (!_reminder.TryGetValue(k, out var on))
            _reminder[k] = on = AiEligibility.ReminderDefaultOn(_sc, entityId);
        return on;
    }

    public AiPolicyInputs Inputs(int? entityId, string? contactMode, IReadOnlyCollection<string>? contactTags) =>
        new(Master(entityId), NowUtc, Zone, ContactAiModes.Normalize(contactMode),
            contactTags ?? Array.Empty<string>(), NeverTags(entityId), QuotaBlock(entityId));

    /// <summary>The full rule for a conversation whose account and contact the caller already holds.</summary>
    public AiVerdict Decide(Thread t, ChannelAccount? account, string? contactMode,
        IReadOnlyCollection<string>? contactTags)
    {
        try
        {
            return AiEligibility.Decide(t, account, ReminderDefaultOn(t.EntityId),
                Inputs(t.EntityId, contactMode, contactTags));
        }
        catch
        {
            // a broken lookup must never switch the AI ON
            return new AiVerdict(false, AiRules.Error, "Off: the AI settings could not be read", true);
        }
    }

    /// <summary>The full rule, loading the contact's mode + tags when the thread does not carry them.</summary>
    public AiVerdict Decide(Thread t, ChannelAccount? account)
    {
        try
        {
            var (mode, tags) = AiEligibility.ContactInputs(_db, t);
            return Decide(t, account, mode, tags);
        }
        catch
        {
            return new AiVerdict(false, AiRules.Error, "Off: the AI settings could not be read", true);
        }
    }

    /// <summary>
    /// May a flow's AI step write to this contact right now? null = yes; else
    /// the step note explaining why not (master switch, schedule, contact never /
    /// never-tag). The per-chat tick is the AI gate's business at send time.
    /// </summary>
    public string? FlowBlock(Contact? contact, int? entityId)
    {
        try
        {
            var probe = new Thread { EntityId = entityId, AiEnabled = true };
            var v = AiEligibility.Decide(probe, null, true,
                Inputs(entityId, contact?.AiMode, contact?.Tags));
            return v.Enabled || !v.Forced ? null : v.Reason;
        }
        catch { return null; }
    }
}
