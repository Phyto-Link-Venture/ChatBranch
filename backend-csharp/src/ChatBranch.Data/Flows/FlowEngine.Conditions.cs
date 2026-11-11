using System.Globalization;
using System.Text.Json;
using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Flows;

/// <summary>
/// "Check conditions" — the deterministic decision. A step holds a list of
/// conditions joined by <c>match</c> = all (AND, the default) | any (OR); each
/// condition is <c>{subject, op, field?, value?, values?, amount?}</c>. The
/// vocabulary below is a CONTRACT with the editor
/// (frontend/src/lib/flow/conditions.ts) — change both sides together.
///
/// <code>
/// subject       op (value / amount)
/// field         equals · not_equals · contains · is_empty · not_empty ·
///               gt · lt (numbers) · before · after (a yyyy-MM-dd date) ·
///               within_last_days · within_next_days (amount = N) ·
///               is_true · is_false · any_of (values)        — field = the key
/// tag           has · not_has (value = the tag name)
/// contact       language_is (value) · has_phone · has_email ·
///               in_entity (value = slug or id; any entity in its subtree) ·
///               opted_out (value = any | this)
/// conversation  status_is (value) · assigned · unassigned · team_is (value =
///               team id) · open_handover · ai_on · channel_is (value) ·
///               last_message_within_hours (amount) · messages_at_least (amount)
/// reply         replied · contains_any · contains_all (value = words) ·
///               said_yes · said_no
/// booking       upcoming · booked_within_days (amount)
/// </code>
///
/// An unknown subject or operator matches NOTHING (like an automation rule);
/// an empty list answers NO. "The conversation" is the one that started the
/// run, else the one the latest reply came in on, else the contact's most
/// recent one. Values compare case-insensitively; numbers, dates and booleans
/// are parsed from whatever the field holds, and a value that does not parse
/// never matches.
/// </summary>
public partial class FlowEngine
{
    public const string DecideConditions = "Conditions";
    public const string DecideAiYesNo = "AI yes/no";
    public const string DecideAiPick = "AI picks a path";

    /// <summary>A decision outcome that finishes the run ("when unsure: end").</summary>
    internal const string EndBranch = "\u0000end";
    /// <summary>The decision parked the run itself (it will be asked again).</summary>
    internal const string ParkedBranch = "\u0000parked";

    internal sealed record FlowCondition(string Subject, string Op, string Field, string Value,
        List<string> Values, double? Amount, bool Legacy = false);

    /// <summary>Lazily loaded facts shared by the conditions of one group.</summary>
    private sealed class ConditionFacts
    {
        public bool ThreadLoaded;
        public Thread? Thread;
        public Dictionary<string, string>? Kinds;
    }

    internal static List<FlowCondition> ParseConditions(JsonElement cfg)
    {
        var outv = new List<FlowCondition>();
        if (cfg.ValueKind != JsonValueKind.Object || !cfg.TryGetProperty("conditions", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return outv;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            double? amount = null;
            if (el.TryGetProperty("amount", out var a))
            {
                if (a.ValueKind == JsonValueKind.Number) amount = a.GetDouble();
                else if (double.TryParse(a.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    amount = n;
            }
            outv.Add(new FlowCondition(
                Str(el, "subject").Trim().ToLowerInvariant(),
                Str(el, "op").Trim().ToLowerInvariant(),
                Str(el, "field").Trim(),
                Str(el, "value").Trim(),
                CfgList(el, "values"),
                amount));
        }
        return outv;
    }

    /// <summary>
    /// A pre-condition-builder <c>basedOn</c> read as the one condition it
    /// always meant — same behaviour, so stored flows keep working unchanged
    /// (the stored JSON is never rewritten here). Null = not one of them.
    /// </summary>
    internal static FlowCondition? LegacyCondition(string basis, JsonElement cfg)
    {
        FlowCondition C(string subject, string op, string field = "", string value = "") =>
            new(subject, op, field, value, new List<string>(), null, Legacy: true);
        switch (basis)
        {
            case "They replied": return C("reply", "replied");
            case "Reply contains": return C("reply", "contains_any", value: CfgStr(cfg, "keyword"));
            case "They said yes": return C("reply", "said_yes");
            case "Has tag":
            case "Contact tag": return C("tag", "has", value: CfgStr(cfg, "tag"));
            case "Custom field equals":
                var wanted = CfgStr(cfg, "value").Trim();
                return C("field", wanted == "" ? "not_empty" : "equals", CfgStr(cfg, "field").Trim(), wanted);
            default: return null;
        }
    }

    /// <summary>All (default) or any of the conditions; an empty group is NO.
    /// Returns the verdict and a short log line ("all of 2: tag has VIP ✓ …").</summary>
    private (bool ok, string summary) EvaluateGroup(FlowRun run, Contact? contact, List<FlowCondition> conds, string match)
    {
        var any = (match ?? "").Trim().ToLowerInvariant() == "any";
        if (conds.Count == 0) return (false, "no conditions set");
        var facts = new ConditionFacts();
        var results = new List<(FlowCondition c, bool ok)>();
        foreach (var c in conds)
        {
            bool ok;
            try { ok = Evaluate(run, contact, c, facts); }
            catch { ok = false; } // a broken lookup never throws out of a decision
            results.Add((c, ok));
        }
        var verdict = any ? results.Any(r => r.ok) : results.All(r => r.ok);
        var parts = results.Select(r =>
            $"{r.c.Subject} {(r.c.Field != "" ? r.c.Field + " " : "")}{r.c.Op}{(r.c.Value != "" ? " " + Trim(r.c.Value, 30) : "")} {(r.ok ? "✓" : "✗")}");
        return (verdict, $"{(any ? "any" : "all")} of {conds.Count}: {string.Join("; ", parts)}");
    }

    private bool Evaluate(FlowRun run, Contact? contact, FlowCondition c, ConditionFacts facts) => c.Subject switch
    {
        "field" => FieldMatches(contact, c, facts),
        "tag" => c.Op switch
        {
            "has" => HasTag(contact, c.Value),
            "not_has" => c.Value.Trim() != "" && !HasTag(contact, c.Value),
            _ => false,
        },
        "contact" => ContactMatches(run, contact, c, facts),
        "conversation" => ConversationMatches(run, contact, c, facts),
        "reply" => ReplyMatches(run, c),
        "booking" => BookingMatches(contact, c),
        _ => false,
    };

    // ---- custom fields ---------------------------------------------------------

    private bool FieldMatches(Contact? contact, FlowCondition c, ConditionFacts facts)
    {
        if (c.Field == "") return false;
        var attrs = contact?.Attributes ?? new Dictionary<string, object?>();
        facts.Kinds ??= SafeKinds();
        var kind = c.Legacy ? "text" : facts.Kinds.GetValueOrDefault(c.Field, "text");
        var text = FormatAttribute(attrs, c.Field, kind).Trim();
        var items = AttributeItems(attrs, c.Field);
        var today = DateOnly.FromDateTime(OrgNow());
        var days = (int)Math.Round(Math.Abs(c.Amount ?? 0));
        switch (c.Op)
        {
            case "equals": return FieldEquals(text, items, c.Value, kind, c.Legacy);
            case "not_equals": return !FieldEquals(text, items, c.Value, kind, false);
            case "contains":
                if (c.Value == "") return false;
                return items.Any(i => string.Equals(i, c.Value, StringComparison.OrdinalIgnoreCase))
                       || text.Contains(c.Value, StringComparison.OrdinalIgnoreCase);
            case "is_empty": return text == "";
            case "not_empty": return text != "";
            case "gt": return TryNumber(text, out var a) && TryNumber(c.Value, out var b) && a > b;
            case "lt": return TryNumber(text, out var a2) && TryNumber(c.Value, out var b2) && a2 < b2;
            case "before": return TryParseDate(text, out var d1) && TryParseDate(c.Value, out var w1) && d1 < w1;
            case "after": return TryParseDate(text, out var d2) && TryParseDate(c.Value, out var w2) && d2 > w2;
            case "within_last_days":
                return TryParseDate(text, out var d3) && d3 <= today && d3 >= today.AddDays(-days);
            case "within_next_days":
                return TryParseDate(text, out var d4) && d4 >= today && d4 <= today.AddDays(days);
            case "is_true": return BoolOf(text) == true;
            case "is_false": return BoolOf(text) == false;
            case "any_of":
                var wanted = c.Values.Count > 0 ? c.Values : Keywords(c.Value);
                if (wanted.Count == 0) return false;
                var have = items.Count > 0 ? items : (text == "" ? new List<string>() : new List<string> { text });
                return have.Any(h => wanted.Any(w => string.Equals(h.Trim(), w.Trim(), StringComparison.OrdinalIgnoreCase)));
            default: return false;
        }
    }

    private Dictionary<string, string> SafeKinds()
    {
        try { return CustomFieldSchema().GroupBy(f => f.key).ToDictionary(g => g.Key, g => g.First().kind); }
        catch { return new Dictionary<string, string>(); }
    }

    /// <summary>Equal as text (case-insensitive) — and, unless the check is a
    /// legacy one, as a number or a yes/no when both sides read as one.</summary>
    private static bool FieldEquals(string text, List<string> items, string wanted, string kind, bool legacy)
    {
        if (string.Equals(text, wanted.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (legacy) return false;
        if (kind == "multiselect" && items.Count == 1
            && string.Equals(items[0], wanted.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (BoolOf(text) is bool x && BoolOf(wanted) is bool y && (kind == "boolean" || IsBoolWord(wanted))) return x == y;
        return TryNumber(text, out var a) && TryNumber(wanted, out var b) && a == b;
    }

    /// <summary>The stored value as a list of non-empty strings (a multi-select's
    /// options; one item for a scalar).</summary>
    private static List<string> AttributeItems(Dictionary<string, object?> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var raw) || raw == null) return new();
        if (raw is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array)
                return el.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var one = FormatAttribute(attrs, key, "text").Trim();
            return one == "" ? new() : new() { one };
        }
        if (raw is System.Collections.IEnumerable seq and not string)
            return seq.Cast<object?>().Select(x => (x?.ToString() ?? "").Trim()).Where(x => x.Length > 0).ToList();
        var s = raw.ToString()?.Trim() ?? "";
        return s == "" ? new() : new() { s };
    }

    private static readonly string[] TrueWords = { "yes", "true", "1", "y", "ya", "on" };
    private static readonly string[] FalseWords = { "no", "false", "0", "n", "tidak", "off" };

    private static bool IsBoolWord(string s)
    {
        var w = (s ?? "").Trim().ToLowerInvariant();
        return TrueWords.Contains(w) || FalseWords.Contains(w);
    }

    private static bool? BoolOf(string s)
    {
        var w = (s ?? "").Trim().ToLowerInvariant();
        if (TrueWords.Contains(w)) return true;
        if (FalseWords.Contains(w)) return false;
        return null;
    }

    private static bool TryNumber(string s, out decimal n)
    {
        var t = (s ?? "").Trim().Replace(",", "").Replace(" ", "");
        return decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out n);
    }

    // ---- the contact -------------------------------------------------------------

    private bool ContactMatches(FlowRun run, Contact? contact, FlowCondition c, ConditionFacts facts)
    {
        if (contact == null) return false;
        switch (c.Op)
        {
            case "language_is":
                var want = c.Values.Count > 0 ? c.Values : new List<string> { c.Value };
                return want.Any(w => w.Trim() != ""
                    && string.Equals((contact.Language ?? "").Trim(), w.Trim(), StringComparison.OrdinalIgnoreCase));
            case "has_phone": return Digits(contact.Phone).Length >= 6;
            case "has_email": return (contact.Email ?? "").Contains('@');
            case "in_entity":
                var target = ResolveEntity(c.Value);
                if (target == null) return false;
                var subtree = _scoping.EntitySubtreeIds(target.Value).ToHashSet();
                var linked = contact.EntityLinks.Count > 0
                    ? contact.EntityLinks.Select(l => l.EntityId).ToList()
                    : _db.ContactEntities.AsNoTracking().Where(l => l.ContactId == contact.Id).Select(l => l.EntityId).ToList();
                return linked.Any(subtree.Contains);
            case "opted_out":
                if (c.Value.Trim().ToLowerInvariant() == "this") return RunThread(run, contact, facts)?.OptedOut == true;
                return _db.Threads.AsNoTracking().Any(t => t.ContactId == contact.Id && t.OptedOut);
            default: return false;
        }
    }

    private int? ResolveEntity(string value)
    {
        var v = (value ?? "").Trim();
        if (v == "") return null;
        if (int.TryParse(v, out var id) && _db.Entities.AsNoTracking().Any(e => e.Id == id)) return id;
        return _db.Entities.AsNoTracking().Where(e => e.Slug == v).Select(e => (int?)e.Id).FirstOrDefault();
    }

    // ---- the conversation --------------------------------------------------------------

    /// <summary>The run's conversation: the one that started it, else the one
    /// the latest reply came in on, else the contact's most recent one — always one
    /// of the journey's own companies (an organization-wide journey: any).</summary>
    private Thread? RunThread(FlowRun run, Contact? contact, ConditionFacts? facts = null)
    {
        if (facts is { ThreadLoaded: true }) return facts.Thread;
        var t = RunConversation(run, contact);
        if (t == null && contact != null)
        {
            var (scoped, companies) = WorkflowCompanies(run);
            bool Fits(Thread x) => !scoped || _scoping.ThreadCompany(x) is int c && companies.Contains(c);
            t = _db.Threads.AsNoTracking().Where(x => x.ContactId == contact.Id)
                .OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id).ToList().FirstOrDefault(Fits);
        }
        if (facts != null) { facts.Thread = t; facts.ThreadLoaded = true; }
        return t;
    }

    /// <summary>
    /// The conversation the run is FOR — the one that started it, else the one the latest
    /// reply came in on — and null for a run with none (started by hand / API / a schedule):
    /// never <see cref="RunThread"/>'s last-resort guess at the contact's most recent chat.
    /// Always one of the journey's own companies.
    /// </summary>
    private Thread? RunConversation(FlowRun run, Contact? contact)
    {
        var (scoped, companies) = WorkflowCompanies(run);
        bool Fits(Thread x) => !scoped || _scoping.ThreadCompany(x) is int c && companies.Contains(c);
        Thread? t = null;
        foreach (var idKey in new[] { "trigger_thread_id", "last_reply_thread_id" })
            if (int.TryParse(Ctx(run, idKey), out var tid)
                && _db.Threads.AsNoTracking().FirstOrDefault(x => x.Id == tid) is { } byId && Fits(byId))
            {
                t = byId;
                break;
            }
        // older runs carry only channel + key: the contact's own conversation there, else any that fits
        if (t == null)
            foreach (var (ch, key) in new[] { ("trigger_channel", "trigger_key"), ("last_reply_channel", "last_reply_key") })
            {
                var c = Ctx(run, ch); var k = Ctx(run, key);
                if (c == "" || k == "") continue;
                var hits = _db.Threads.AsNoTracking().Where(x => x.Channel == c && x.Key == k)
                    .OrderByDescending(x => x.UpdatedAt).ToList().Where(Fits).ToList();
                t = hits.FirstOrDefault(x => contact != null && x.ContactId == contact.Id) ?? hits.FirstOrDefault();
                if (t != null) break;
            }
        return t;
    }

    /// <summary>
    /// The inbox (channel account) of the conversation the run is FOR, else null. Its own AI
    /// master switch narrows the journey's AI steps (<see cref="Ai.AiControls.Master(int?, ChannelAccount?)"/>
    /// — an inbox can only switch the AI OFF); a run with no conversation has no inbox to ask.
    /// </summary>
    private ChannelAccount? RunInbox(FlowRun run, Contact? contact) =>
        RunConversation(run, contact)?.AccountId is int aid
            ? _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == aid)
            : null;

    /// <summary>The companies a run's journey serves ("Runs for"; Scoped = it has entities).</summary>
    private (bool Scoped, List<int> Companies) WorkflowCompanies(FlowRun run)
    {
        var wf = _db.Workflows.AsNoTracking().FirstOrDefault(w => w.Id == run.WorkflowId);
        if (wf == null) return (false, new());
        var companies = JourneyCompanies(wf);
        return (companies.Count > 0, companies);
    }

    /// <summary>
    /// The company a run's messages and history belong to: its journey's (one of
    /// them when it runs for several — its conversation's, else the contact's), else
    /// its conversation's, else the contact's one company — null = the unattributed
    /// conversations only. A journey never reads another company's conversations.
    /// </summary>
    private int? RunCompany(FlowRun run, Contact? contact, Thread? thread)
    {
        var (scoped, companies) = WorkflowCompanies(run);
        if (scoped)
        {
            if (companies.Count == 1) return companies[0];
            if (thread != null && _scoping.ThreadCompany(thread) is int tc && companies.Contains(tc)) return tc;
            if (contact != null)
            {
                var mine = ContactCompanies.LinkCompanies(_db, _scoping, contact.Id);
                foreach (var c in companies)
                    if (mine.Contains(c)) return c;
            }
            return companies[0];
        }
        if (thread != null) return _scoping.ThreadCompany(thread);
        if (contact == null) return null;
        var links = ContactCompanies.LinkCompanies(_db, _scoping, contact.Id);
        return links.Count == 1 ? links.First() : null;
    }

    private bool ConversationMatches(FlowRun run, Contact? contact, FlowCondition c, ConditionFacts facts)
    {
        var t = RunThread(run, contact, facts);
        if (t == null) return false;
        var now = DateTime.UtcNow;
        switch (c.Op)
        {
            case "status_is":
                return c.Value.Trim() != "" && string.Equals(t.Status, c.Value.Trim(), StringComparison.OrdinalIgnoreCase);
            case "assigned": return (t.Assignee ?? "").Trim() != "";
            case "unassigned": return (t.Assignee ?? "").Trim() == "";
            case "team_is":
                if (t.TeamId is not int teamId || c.Value.Trim() == "") return false;
                if (c.Value.Trim() == teamId.ToString()) return true;
                return _db.Teams.AsNoTracking().Any(tm => tm.Id == teamId && tm.Name.ToLower() == c.Value.Trim().ToLower());
            case "open_handover": return Ai.Handover.IsOpen(t);
            case "ai_on": return Ai.AiEligibility.Decide(_db, _scoping, t).Enabled;
            case "channel_is": return c.Value.Trim() != "" && ChannelMatches(c.Value, t.Channel);
            case "last_message_within_hours":
                var since = now.AddHours(-Math.Abs(c.Amount ?? 0));
                return _db.Messages.AsNoTracking().Any(m => m.ThreadId == t.Id && m.Direction == MessageDirection.In
                                                            && !m.Private && m.CreatedAt >= since);
            case "messages_at_least":
                var n = (int)Math.Ceiling(Math.Abs(c.Amount ?? 0));
                return _db.Messages.AsNoTracking().Count(m => m.ThreadId == t.Id && m.Direction == MessageDirection.In
                                                              && !m.Private) >= n;
            default: return false;
        }
    }

    // ---- the reply that arrived during the last wait ------------------------------------

    private static bool ReplyMatches(FlowRun run, FlowCondition c)
    {
        var answered = CtxBool(run, "answered");
        var reply = Ctx(run, "last_reply");
        switch (c.Op)
        {
            case "replied": return answered;
            case "contains_any":
                var any = c.Values.Count > 0 ? c.Values : Keywords(c.Value);
                return answered && any.Any(k => reply.Contains(k, StringComparison.OrdinalIgnoreCase));
            case "contains_all":
                var all = c.Values.Count > 0 ? c.Values : Keywords(c.Value);
                return answered && all.Count > 0 && all.All(k => reply.Contains(k, StringComparison.OrdinalIgnoreCase));
            case "said_yes": return answered && ClassifyReply(reply) == "yes";
            case "said_no": return answered && NoRe.IsMatch(reply) && ClassifyReply(reply) == "no";
            default: return false;
        }
    }

    // ---- bookings ------------------------------------------------------------------------

    private bool BookingMatches(Contact? contact, FlowCondition c)
    {
        if (contact == null) return false;
        var now = DateTime.UtcNow;
        switch (c.Op)
        {
            case "upcoming":
                return _db.Bookings.AsNoTracking().Any(b => b.ContactId == contact.Id
                    && b.Status == BookingStatus.Booked && b.StartUtc > now);
            case "booked_within_days":
                var since = now.AddDays(-Math.Abs(c.Amount ?? 0));
                return _db.Bookings.AsNoTracking().Any(b => b.ContactId == contact.Id
                    && b.Status == BookingStatus.Booked && b.CreatedAt >= since);
            default: return false;
        }
    }
}
