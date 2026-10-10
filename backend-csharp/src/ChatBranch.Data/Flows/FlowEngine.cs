using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Flows;

/// <summary>
/// The flow-execution engine (port of flows/engine.py) — the ONLY interpreter of
/// the builder's React Flow JSON. Two entry points: <see cref="Tick"/> (time
/// axis — resume due runs, enroll trigger matches, drain the queue) and
/// <see cref="OnInboundMessage"/> (event axis — resume runs parked on a reply).
/// Execution state lives in FlowRun. Sends are ENQUEUED onto the outbound queue,
/// never inline, so pacing + account resolution happen at send time.
///
/// Step types: trigger · action (send message / AI message / call / app
/// action) · wait · decision ("Check conditions" / "Let AI decide") · followup ("Follow up if no
/// reply") · handover · update ("Update contact") · end — plus the legacy
/// "stage" section, executed as a pass-through. Every arrival, completion,
/// park and failure is appended to <see cref="FlowStepEvent"/> (per flow, per
/// org-local day) so the builder can show how many people reached each step.
/// </summary>
public partial class FlowEngine
{
    private const int MaxSteps = 60;
    private const int LogCap = 200;
    private const int StalledRunningMinutes = 10;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly QueueService _queue;
    private readonly Integrations.IntegrationActions? _actions;
    private readonly Dictionary<int, string> _flowKeys = new();
    private TimeZoneInfo? _zone;
    /// <summary>What the step being processed reports in its "completed" event
    /// (a branch verdict, an outcome) — set by the step handler.</summary>
    private string _completeDetail = "";
    private readonly Ai.AssistantService? _assistant;
    /// <summary>The model behind the "Let AI decide" steps (null or unconfigured = the sandbox guess).</summary>
    private readonly Ai.ILlmClient? _llm;

    public FlowEngine(AppDbContext db, ScopingService scoping, QueueService queue,
        Integrations.IntegrationActions? actions = null, Ai.AssistantService? assistant = null,
        Ai.ILlmClient? llm = null)
    {
        _db = db;
        _scoping = scoping;
        _queue = queue;
        _actions = actions;
        _assistant = assistant;
        _llm = llm;
    }

    internal static readonly string[] ClosedStatuses =
        { "closed", "done", "resolved", "cancelled", "completed" };

    // ---- graph helpers ------------------------------------------------------

    private static List<JsonElement> Elems(List<object?> raw) =>
        raw.Select(o => JsonSerializer.SerializeToElement(o)).ToList();

    private static string Str(JsonElement e, string prop)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        return "";
    }

    private static JsonElement Cfg(JsonElement node)
    {
        if (node.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object)
            return c;
        return default;
    }

    private static string CfgStr(JsonElement cfg, string key) =>
        cfg.ValueKind == JsonValueKind.Object ? Str(cfg, key) : "";

    private static int CfgInt(JsonElement cfg, string key, int fallback = 0) =>
        int.TryParse(CfgStr(cfg, key), out var n) ? n : fallback;

    private static List<JsonElement> Nodes(Workflow wf) => Elems(wf.Nodes);
    private static List<JsonElement> Edges(Workflow wf) => Elems(wf.Edges);

    // ---- {{variable}} rendering ---------------------------------------------

    private static readonly Regex VarRe = new(@"\{\{\s*([a-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    public static string Render(string text, Dictionary<string, string> vars) =>
        VarRe.Replace(text ?? "", m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    private Record? PrimaryRecord(Contact? contact)
    {
        if (contact == null) return null;
        var records = _db.Records.Where(r => r.ContactId == contact.Id).ToList();
        return records.FirstOrDefault(r => !ClosedStatuses.Contains(r.Status.ToLowerInvariant()))
               ?? records.FirstOrDefault();
    }

    public Dictionary<string, string> VariablesFor(Contact? contact, Record? record = null)
    {
        var vars = new Dictionary<string, string>();
        // business vars from Setting rows
        foreach (var (k, dflt) in new[] { ("business_name", "our team"), ("agent_name", ""),
                     ("support_number", ""), ("support_email", ""), ("website", "") })
        {
            var v = _scoping.SettingValue(k);
            vars[k] = string.IsNullOrEmpty(v) ? dflt : v;
        }
        if (contact == null) return vars;
        record ??= PrimaryRecord(contact);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var due = record?.DueDate;
        int? delta = due is DateOnly d ? d.DayNumber - today.DayNumber : null;
        var currency = record?.Currency ?? "MYR";

        vars["name"] = contact.Name;
        vars["first_name"] = (contact.Name ?? "").Split(' ').FirstOrDefault() ?? "";
        vars["phone"] = contact.Phone;
        vars["email"] = contact.Email;
        vars["language"] = contact.Language;
        vars["company"] = string.IsNullOrEmpty(contact.CompanyName) ? "our company" : contact.CompanyName;
        vars["record_id"] = record?.ExternalId ?? "";
        vars["record_type"] = record?.Type ?? "";
        vars["record_status"] = record?.Status ?? "";
        vars["record_title"] = record?.Title ?? "";
        vars["amount"] = record?.Amount is decimal amt ? $"{currency} {amt:N2}" : "";
        vars["currency"] = currency;
        vars["due_date"] = due is DateOnly dd ? dd.ToString("dd MMM yyyy") : "";
        vars["days_until_due"] = delta is int du && du >= 0 ? du.ToString() : "";
        vars["days_overdue"] = delta is int od && od < 0 ? (-od).ToString() : "0";
        vars["situation"] = contact.Notes;
        // record links + AI-memory vars (parity with flows/engine.py — the
        // template pickers offer {{link}} and {{best_contact_time}}, so they
        // must resolve rather than render literally)
        var data = record?.Data ?? new Dictionary<string, object?>();
        vars["tracking_url"] = DataString(data, "tracking_url");
        var link = DataString(data, "link");
        vars["link"] = link.Length > 0 ? link : DataString(data, "url");
        vars["account_number"] = record?.ExternalId ?? "";
        vars["best_contact_time"] = contact.PreferredContactTime;
        vars["preferred_channel"] = FormatAttribute(contact.Attributes, "preferred_channel", "text");
        var lastContact = _db.Messages.AsNoTracking()
            .Where(m => m.Thread != null && m.Thread.ContactId == contact.Id)
            .Max(m => (DateTime?)m.CreatedAt);
        vars["last_contact_date"] = lastContact is DateTime lc ? lc.ToString("dd MMM yyyy") : "";
        MergeCustomFields(vars, contact);
        return vars;
    }

    /// <summary>
    /// Operator-defined Custom Fields (Settings → Custom Fields) as template
    /// variables: customer fields become <c>{{cf_&lt;key&gt;}}</c>, and the
    /// attributes of the contact's registry tags become
    /// <c>{{tag_&lt;tagslug&gt;_&lt;key&gt;}}</c> (TagService.MergeVariables). The
    /// prefixes are deliberate: a field labelled "Company" or "Email" would
    /// otherwise silently shadow a built-in. A field with no value renders as
    /// an empty string (never the literal placeholder), so a template never
    /// leaks "{{cf_grade}}" to a customer.
    /// </summary>
    private void MergeCustomFields(Dictionary<string, string> vars, Contact contact)
    {
        try
        {
            foreach (var (key, kind) in CustomFieldSchema())
                vars[$"cf_{key}"] = FormatAttribute(contact.Attributes, key, kind);
        }
        catch
        {
            // custom fields are a nicety — a malformed schema must never break a send
        }
        Tags.TagService.MergeVariables(_db, vars, contact);
    }

    /// <summary>A string out of a Record.Data JSON bag ("" when absent).</summary>
    private static string DataString(Dictionary<string, object?> data, string key) =>
        FormatAttribute(data, key, "text");

    /// <summary>The customer custom-field schema as (key, kind) pairs, read
    /// straight from the `custom_fields` Setting row (the API layer owns the
    /// write side; this only needs the shape).</summary>
    private List<(string key, string kind)> CustomFieldSchema() =>
        CustomFieldDefs().Select(f => (f.key, f.kind)).ToList();

    /// <summary>The customer custom fields with their labels (key, label, kind).</summary>
    private List<(string key, string label, string kind)> CustomFieldDefs()
    {
        var customer = new List<(string, string, string)>();
        var raw = _scoping.SettingValue("custom_fields");
        if (string.IsNullOrWhiteSpace(raw)) return customer;
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("customer", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return customer;
        foreach (var f in arr.EnumerateArray())
        {
            var key = f.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            if (key.Length == 0) continue;
            var label = f.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
            var kind = f.TryGetProperty("kind", out var kd) ? kd.GetString() ?? "text" : "text";
            customer.Add((key, label.Length > 0 ? label : key, kind));
        }
        return customer;
    }

    /// <summary>Render one stored attribute the way the dashboard shows it
    /// (frontend lib/customFields.ts formatFieldValue): booleans as Yes/No,
    /// multi-select as a comma list.</summary>
    private static string FormatAttribute(Dictionary<string, object?> attributes, string key, string kind)
    {
        if (!attributes.TryGetValue(key, out var raw) || raw == null) return "";
        if (raw is JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined: return "";
                case JsonValueKind.True: return "Yes";
                case JsonValueKind.False: return "No";
                case JsonValueKind.Array:
                    return string.Join(", ", el.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                        .Where(x => x.Length > 0));
                case JsonValueKind.String: return el.GetString() ?? "";
                default: return el.ToString();
            }
        }
        if (raw is bool b) return b ? "Yes" : "No";
        if (raw is System.Collections.IEnumerable seq and not string)
            return string.Join(", ", seq.Cast<object?>().Select(x => x?.ToString() ?? "").Where(x => x.Length > 0));
        if (kind == "boolean")
            return string.Equals(raw.ToString(), "true", StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
        return raw.ToString() ?? "";
    }

    // ---- trigger matching ---------------------------------------------------
    //
    // The builder offers six plain-language triggers. The six debt-collection
    // era `when` values (due dates, record status, "Contact has tag") are no
    // longer offered but still run, so flows saved before the rename keep
    // working unchanged.

    public const string WhenContactCreated = "Contact created";
    public const string WhenTagAdded = "Tag added";
    public const string WhenMessageReceived = "Message received";
    public const string WhenDateField = "Date field";
    public const string WhenScheduled = "Scheduled";
    public const string WhenManual = "Manual / API";

    private static string When(JsonElement cfg)
    {
        var when = CfgStr(cfg, "when");
        return when == "" ? "Days after due date" : when;
    }

    private bool TriggerMatches(JsonElement cfg, Contact contact, Workflow wf)
    {
        var when = When(cfg);
        switch (when)
        {
            case WhenContactCreated:
                return wf.LiveSince is DateTime live && contact.CreatedAt >= live;
            case WhenTagAdded:
                return HasTag(contact, CfgStr(cfg, "tag"));
            case WhenDateField:
                return DateFieldMatches(cfg, contact);
            case WhenScheduled:
                return true; // the time gate is per trigger, see ScheduleDue
            case WhenMessageReceived:
            case WhenManual:
                return false; // event-driven — never polled
        }
        if (when == "Record status is")
        {
            var record = PrimaryRecord(contact);
            var wanted = CfgStr(cfg, "status").Trim().ToLowerInvariant();
            return record != null && wanted != "" && record.Status.Trim().ToLowerInvariant() == wanted;
        }
        if (when == "Contact has tag") return HasTag(contact, CfgStr(cfg, "tag"));
        var rec = PrimaryRecord(contact);
        if (rec?.DueDate is not DateOnly dueDate) return false;
        var days = CfgInt(cfg, "days");
        var od = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - dueDate.DayNumber;
        if (when == "On the due date") return od == 0;
        if (when == "Days before due date") return od == -Math.Abs(days == 0 ? 1 : days);
        if (when == "Days after due date (exactly)") return od == (days == 0 ? 1 : days);
        return od >= (days == 0 ? 1 : days);
    }

    private static bool HasTag(Contact? contact, string tag)
    {
        var wanted = (tag ?? "").Trim().ToLowerInvariant();
        if (wanted == "" || contact == null) return false;
        return contact.Tags.Any(t => (t ?? "").Trim().ToLowerInvariant() == wanted);
    }

    /// <summary>"N days before / on / after" a contact date custom field. With
    /// <c>yearly</c> only month + day count (birthdays, renewals).</summary>
    private bool DateFieldMatches(JsonElement cfg, Contact contact)
    {
        var field = CfgStr(cfg, "field").Trim();
        if (field == "") return false;
        var raw = FormatAttribute(contact.Attributes, field, "text").Trim();
        if (raw == "" || !TryParseDate(raw, out var date)) return false;
        var days = Math.Abs(CfgInt(cfg, "days"));
        var target = CfgStr(cfg, "dateWhen") switch
        {
            "before" => date.AddDays(-days),
            "after" => date.AddDays(days),
            _ => date,
        };
        var today = DateOnly.FromDateTime(OrgNow());
        if (CfgBool(cfg, "yearly"))
            return target.Month == today.Month && target.Day == today.Day;
        return target == today;
    }

    private static bool TryParseDate(string raw, out DateOnly date)
    {
        if (DateOnly.TryParseExact(raw, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date)) return true;
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }
        date = default;
        return false;
    }

    private static readonly string[] WeekdayKeys = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

    /// <summary>
    /// "Every day at HH:MM" (optionally only some weekdays): due once today's
    /// slot has passed — but only a slot that came AFTER the flow went live, so
    /// arming a 09:00 flow at 15:00 waits for tomorrow instead of firing now.
    /// </summary>
    private bool ScheduleDue(JsonElement cfg, Workflow wf, out DateTime slotUtc)
    {
        slotUtc = DateTime.MaxValue;
        var zone = Zone();
        var orgNow = OrgClock.ToOrg(DateTime.UtcNow, zone);
        if (!TimeSpan.TryParse(CfgStr(cfg, "time") is { Length: > 0 } t ? t : "09:00", out var at)) return false;
        var weekdays = CfgList(cfg, "weekdays").Select(d => d.ToLowerInvariant()).ToList();
        if (weekdays.Count > 0 && !weekdays.Contains(WeekdayKeys[(int)orgNow.DayOfWeek])) return false;
        var slot = orgNow.Date + at;
        if (orgNow < slot) return false;
        slotUtc = OrgClock.ToUtc(slot, zone);
        return !(wf.LiveSince is DateTime live && live > slotUtc);
    }

    /// <summary>
    /// Legacy "call window" (9:00–12:00…) on old triggers: enrolment waits for
    /// the window. New triggers no longer offer it — quiet hours already gate
    /// every send — but a saved value is still honoured.
    /// </summary>
    private bool InTriggerWindow(JsonElement cfg)
    {
        var window = CfgStr(cfg, "window").Trim();
        if (window == "" || window.StartsWith("Any", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = window.Split(new[] { '–', '-', '—' }, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TimeSpan.TryParse(parts[0], out var from)
                              || !TimeSpan.TryParse(parts[1], out var to)) return true;
        var now = OrgNow().TimeOfDay;
        return from <= to ? now >= from && now < to : now >= from || now < to;
    }

    /// <summary>
    /// The audience filters every trigger shares: specific contacts (the
    /// "Apply to" picker — empty = everyone), an optional "only contacts
    /// tagged" filter, and the workflow's own entity (an outlet's flow only
    /// enrols that outlet's contacts).
    /// </summary>
    private bool TriggerAllows(JsonElement cfg, Contact contact, HashSet<int>? entityContacts)
    {
        var scoped = CfgList(cfg, "contacts");
        if (scoped.Count > 0 && !scoped.Contains(contact.Id.ToString())) return false;
        var onlyTag = CfgStr(cfg, "onlyTag");
        if (onlyTag.Trim() != "" && !HasTag(contact, onlyTag)) return false;
        if (entityContacts != null && !entityContacts.Contains(contact.Id)) return false;
        return true;
    }

    private HashSet<int>? EntityContacts(Workflow wf)
    {
        if (wf.EntityId is not int eid) return null;
        var ids = _scoping.EntitySubtreeIds(eid);
        return _db.ContactEntities.AsNoTracking().Where(l => ids.Contains(l.EntityId))
            .Select(l => l.ContactId).ToHashSet();
    }

    /// <summary>
    /// Re-entry policy. A contact never has two live runs of one trigger.
    /// "Once" = once ever (legacy default). "Every time" = every event for
    /// message triggers, and at most once per org day for polled triggers
    /// (their condition stays true all day). "Once a day" / legacy "Daily until
    /// answered" = once per org day; legacy "Every 2 days" kept as is.
    /// </summary>
    private bool AlreadyRan(Workflow wf, string nodeId, int contactId, string repeat,
        bool polled, DateTime? notBefore = null)
    {
        var runs = _db.FlowRuns.Where(r => r.WorkflowId == wf.Id && r.TriggerKey == nodeId
                                           && r.ContactId == contactId);
        if (runs.Any(r => r.Status == FlowRunStatus.Running || r.Status == FlowRunStatus.Waiting))
            return true;
        if (notBefore is DateTime nb && runs.Any(r => r.StartedAt >= nb)) return true;
        var dayStart = OrgClock.TodayStartUtc(Zone());
        switch (repeat)
        {
            case "Every 2 days":
                var twoDays = DateTime.UtcNow.AddDays(-2);
                return runs.Any(r => r.StartedAt >= twoDays);
            case "Daily until answered":
            case "Once a day":
                return runs.Any(r => r.StartedAt >= dayStart);
            case "Every time":
                return polled && runs.Any(r => r.StartedAt >= dayStart);
            default:
                return runs.Any();
        }
    }

    private FlowRun Start(Workflow wf, string triggerNodeId, Contact contact, string reason,
        Dictionary<string, object?>? context = null)
    {
        var run = new FlowRun
        {
            WorkflowId = wf.Id, ContactId = contact.Id, TriggerKey = triggerNodeId,
            CurrentNode = triggerNodeId, Status = FlowRunStatus.Running,
            Context = context ?? new(),
            StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.FlowRuns.Add(run);
        _db.SaveChanges();
        _flowKeys[wf.Id] = wf.FlowId;
        Log(run, triggerNodeId, $"trigger fired: {reason}");
        Advance(run, wf);
        return run;
    }

    public int EvaluateTriggers()
    {
        var started = 0;
        var contacts = _db.Contacts.Where(c => c.Status != "resolved").ToList();
        foreach (var wf in _db.Workflows.Where(w => w.Active).ToList())
        {
            if (wf.LiveSince == null)
            {
                // armed before LiveSince existed (or by a direct DB write): the
                // clock starts now, so "Contact created" never back-fires.
                wf.LiveSince = DateTime.UtcNow;
                _db.SaveChanges();
            }
            HashSet<int>? entityContacts = null;
            var entityLoaded = false;
            foreach (var node in Nodes(wf).Where(n => Str(n, "type") == "trigger"))
            {
                var cfg = Cfg(node);
                var when = When(cfg);
                if (when is WhenMessageReceived or WhenManual) continue;
                if (!InTriggerWindow(cfg)) continue;
                DateTime? notBefore = null;
                if (when == WhenScheduled)
                {
                    if (!ScheduleDue(cfg, wf, out var slotUtc)) continue;
                    notBefore = slotUtc;
                }
                if (!entityLoaded) { entityContacts = EntityContacts(wf); entityLoaded = true; }
                var repeat = CfgStr(cfg, "repeat"); if (repeat == "") repeat = "Once";
                var nodeId = Str(node, "id");
                foreach (var contact in contacts)
                {
                    if (!TriggerAllows(cfg, contact, entityContacts)) continue;
                    if (!TriggerMatches(cfg, contact, wf)) continue;
                    if (AlreadyRan(wf, nodeId, contact.Id, repeat, polled: true, notBefore)) continue;
                    Start(wf, nodeId, contact, $"{TriggerText(cfg)} ({repeat})");
                    started++;
                }
            }
        }
        return started;
    }

    /// <summary>Plain-words description of a trigger for the run log.</summary>
    private static string TriggerText(JsonElement cfg)
    {
        var when = When(cfg);
        return when switch
        {
            WhenTagAdded => $"tagged '{CfgStr(cfg, "tag")}'",
            WhenDateField => $"date field {CfgStr(cfg, "field")}",
            WhenScheduled => $"scheduled {CfgStr(cfg, "time")}",
            _ => when,
        };
    }

    /// <summary>
    /// Event axis of "Message received" triggers: a customer message (any
    /// channel, or one channel; optionally only when it contains a keyword)
    /// starts a run. The message itself seeds the run as its first reply, so an
    /// "Ask &amp; branch: reply contains…" step right after the trigger reads it.
    /// </summary>
    private List<int> StartMessageTriggers(Contact contact, string channel, string text, string key = "")
    {
        var startedIds = new List<int>();
        foreach (var wf in _db.Workflows.Where(w => w.Active).ToList())
        {
            HashSet<int>? entityContacts = null;
            var entityLoaded = false;
            foreach (var node in Nodes(wf).Where(n => Str(n, "type") == "trigger"))
            {
                var cfg = Cfg(node);
                if (When(cfg) != WhenMessageReceived) continue;
                if (!ChannelMatches(CfgStr(cfg, "channel"), channel)) continue;
                var keywords = Keywords(CfgStr(cfg, "keyword"));
                if (keywords.Count > 0 && !keywords.Any(k => (text ?? "").Contains(k, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!entityLoaded) { entityContacts = EntityContacts(wf); entityLoaded = true; }
                if (!TriggerAllows(cfg, contact, entityContacts)) continue;
                var repeat = CfgStr(cfg, "repeat"); if (repeat == "") repeat = "Every time";
                var nodeId = Str(node, "id");
                if (AlreadyRan(wf, nodeId, contact.Id, repeat, polled: false)) continue;
                var run = Start(wf, nodeId, contact, $"message received on {channel}", new()
                {
                    ["last_reply"] = Trim(text ?? "", 500),
                    ["reply_consumed"] = false,
                    ["answered"] = true,
                    ["trigger_message"] = Trim(text ?? "", 500),
                    // the conversation that started the run: AI decisions read its history
                    ["trigger_channel"] = channel,
                    ["trigger_key"] = key,
                });
                startedIds.Add(run.Id);
            }
        }
        return startedIds;
    }

    private static bool ChannelMatches(string wanted, string channel)
    {
        var w = (wanted ?? "").Trim().ToLowerInvariant();
        if (w is "" or "any" or "any channel") return true;
        var c = (channel ?? "").Trim().ToLowerInvariant();
        if (w is "messenger") w = Domain.Channels.Facebook;
        if (w is "website chat" or "widget") w = Domain.Channels.Website;
        return w == c;
    }

    private static List<string> Keywords(string raw) =>
        (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length > 0).ToList();

    /// <summary>
    /// "Manual / API" trigger: start the flow for the given contacts now
    /// (POST /api/flows/{flowId}/start). A contact that already has a live run
    /// of this flow is skipped — never two runs at once.
    /// </summary>
    public (int started, int skipped) StartManual(Workflow wf, IEnumerable<int> contactIds)
    {
        var triggers = Nodes(wf).Where(n => Str(n, "type") == "trigger").ToList();
        if (triggers.Count == 0) return (0, contactIds.Count());
        var trigger = triggers.FirstOrDefault(n => When(Cfg(n)) == WhenManual);
        if (trigger.ValueKind == JsonValueKind.Undefined) trigger = triggers[0];
        var nodeId = Str(trigger, "id");
        int started = 0, skipped = 0;
        foreach (var id in contactIds.Distinct())
        {
            var contact = _db.Contacts.Find(id);
            var live = _db.FlowRuns.Any(r => r.WorkflowId == wf.Id && r.ContactId == id
                && (r.Status == FlowRunStatus.Running || r.Status == FlowRunStatus.Waiting));
            if (contact == null || live) { skipped++; continue; }
            Start(wf, nodeId, contact, "started manually");
            started++;
        }
        return (started, skipped);
    }

    // ---- decisions ----------------------------------------------------------

    internal static readonly Regex YesRe = new(
        @"\b(yes|ya|yeah|yup|ok|okay|sure|can|will|paid|done|sudah|boleh|setuju|bayar|alright|noted)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static readonly Regex NoRe = new(
        @"\b(no|not|nope|cannot|can't|cant|tak|tidak|later|busy|belum|tunggu)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ClassifyReply(string text)
    {
        var yes = YesRe.Match(text ?? "");
        var no = NoRe.Match(text ?? "");
        if (yes.Success && no.Success) return yes.Index < no.Index ? "yes" : "no";
        return yes.Success ? "yes" : "no";
    }

    /// <summary>
    /// The decision step ("Check conditions" / "Let AI decide") — three kinds (<c>basedOn</c>):
    /// <list type="bullet">
    /// <item><c>Conditions</c> — a group of checks joined by all/any, evaluated
    /// deterministically (FlowEngine.Conditions.cs); never parks.</item>
    /// <item><c>AI yes/no</c> and <c>AI picks a path</c> — the model reads the
    /// conversation history and answers (FlowEngine.AiDecisions.cs); when it is
    /// unsure the step's "when unsure" choice decides, which may park the run
    /// until the next reply.</item>
    /// </list>
    /// Flows saved before the condition builder keep their single-check
    /// <c>basedOn</c> (They replied · Reply contains · They said yes · Has tag ·
    /// Custom field equals): each is read as the equivalent one-condition group
    /// and behaves exactly as before. The legacy "Record status" check and
    /// "Contact's answer" (which parks until a reply) run as they always did.
    /// Returns the branch (yes / no / an option handle), <see cref="EndBranch"/>,
    /// <see cref="ParkedBranch"/> when the step parked the run itself, or null =
    /// park on the contact's answer (legacy).
    /// </summary>
    private string? Decide(FlowRun run, JsonElement node)
    {
        var cfg = Cfg(node);
        var basis = CfgStr(cfg, "basedOn"); if (basis == "") basis = "Contact's answer";
        var contact = run.ContactId is int cid
            ? _db.Contacts.Include(c => c.EntityLinks).FirstOrDefault(c => c.Id == cid)
            : null;
        var nodeId = Str(node, "id");
        if (basis is DecideAiYesNo or DecideAiPick)
            return DecideWithAi(run, nodeId, cfg, contact, basis == DecideAiPick);

        var reply = Ctx(run, "last_reply");
        string Yn(bool b) => b ? "yes" : "no";
        string? answer = null;
        if (basis == DecideConditions)
        {
            var (ok, summary) = EvaluateGroup(run, contact, ParseConditions(cfg), CfgStr(cfg, "match"));
            answer = Yn(ok);
            Log(run, nodeId, $"decision ({summary}): {answer}");
            _completeDetail = answer;
            return answer;
        }
        if (LegacyCondition(basis, cfg) is FlowCondition legacy)
        {
            answer = Yn(Evaluate(run, contact, legacy, new ConditionFacts()));
        }
        else if (basis == "Record status")
        {
            var record = PrimaryRecord(contact);
            var wanted = CfgStr(cfg, "status").Trim().ToLowerInvariant();
            answer = wanted != ""
                ? Yn(record != null && record.Status.Trim().ToLowerInvariant() == wanted)
                : Yn(record != null && ClosedStatuses.Contains(record.Status.Trim().ToLowerInvariant()));
        }
        if (answer != null)
        {
            Log(run, nodeId, $"decision ({basis.ToLowerInvariant()}): {answer}");
            _completeDetail = answer;
            return answer;
        }
        // legacy "Contact's answer": consume an unread reply, else park
        if (reply != "" && !CtxBool(run, "reply_consumed"))
        {
            SetCtx(run, "reply_consumed", true);
            var verdict = ClassifyReply(reply);
            Log(run, nodeId, $"decision (reply): {verdict}");
            _completeDetail = verdict;
            return verdict;
        }
        return null;
    }

    // ---- action execution ---------------------------------------------------

    private bool DoAction(FlowRun run, JsonElement node)
    {
        var contact = run.ContactId is int cid ? _db.Contacts.Find(cid) : null;
        var cfg = Cfg(node);
        var nodeId = Str(node, "id");
        var vars = VariablesFor(contact);
        var script = Render(CfgStr(cfg, "script"), vars);
        var channels = ChannelList(cfg);

        // quiet-hours postpone for messaging channels
        if (channels.Any(c => c is "WhatsApp" or "SMS" or "Email" or "Telegram") && InQuietHours())
        {
            run.Status = FlowRunStatus.Waiting;
            run.WaitUntil = DateTime.UtcNow.AddMinutes(30);
            Log(run, nodeId, "quiet hours — action postponed 30min");
            return false;
        }

        // AI message composition (the ai area's seam): a step naming a skill or
        // selecting "AI message" has its text written by the assistant.
        var ai = ComposeMessage(run, nodeId, contact, cfg, ref script, ref channels);
        var aiSince = DateTime.UtcNow;
        if (ai is { Composed: true, ShouldSend: false }) Track(run, nodeId, FlowStepKind.Skipped, $"AI message held: {ai.Reason}");

        foreach (var ch in channels)
        {
            try
            {
                switch (ch)
                {
                    case "WhatsApp": QueueWhatsApp(run, nodeId, contact, cfg, script); break;
                    case "Email": QueueEmail(run, nodeId, contact, cfg, script); break;
                    case "Telegram": QueueTelegram(run, nodeId, contact, cfg, script); break;
                    case "SMS": QueueSms(run, nodeId, contact, cfg, script); break;
                    case "AI message": break; // composition marker — the text above is AI-written; sent on the step's other channels
                    case "AI voice call": EnqueueCall(run, nodeId, contact, cfg, ""); break;
                    case "Script call": EnqueueScriptCall(run, nodeId, contact, cfg); break;
                    case "In-app push":
                        Notify(new[] { "superadmin", "admin", "agent" },
                            $"Flow push · {contact?.Name ?? "contact"}", script);
                        Log(run, nodeId, "in-app push sent");
                        break;
                    default:
                        // Integration actions (Create calendar event, Append row
                        // to Sheet, …) run for real against the resolved
                        // connection; anything else is still just logged.
                        if (Integrations.IntegrationActions.Providers.ContainsKey(ch))
                        {
                            DoIntegration(run, nodeId, contact, ch, cfg, script);
                            break;
                        }
                        Log(run, nodeId, $"{ch}: logged (no provider) — {Trim(script, 80)}");
                        break;
                }
            }
            catch (Exception e)
            {
                Log(run, nodeId, $"{ch} failed: {e.Message}");
                Track(run, nodeId, FlowStepKind.Skipped, $"{ch} failed: {e.Message}");
            }
        }
        if (ai is { Composed: true, ShouldSend: true }) StampAiJobs(run, nodeId, aiSince, ai);
        _db.SaveChanges();
        return true;
    }

    /// <summary>
    /// Queue-payload hints every message a flow enqueues carries: <c>source</c>
    /// ("ai" when the step's text is AI-written — the "AI message" channel — so
    /// the AI monitor can gate it; "flow" for an operator-written message) and
    /// <c>humanize</c> ("Send like a person": typing delay + short bursts — the
    /// queue reads it, the flow only sets it).
    /// </summary>
    private static Dictionary<string, object?> PayloadHints(JsonElement cfg)
    {
        var hints = new Dictionary<string, object?>
        {
            ["source"] = ChannelList(cfg).Contains("AI message") ? "ai" : "flow",
        };
        if (CfgBool(cfg, "humanize")) hints["humanize"] = true;
        return hints;
    }

    private static Dictionary<string, object?> WithHints(Dictionary<string, object?> payload, JsonElement cfg)
    {
        foreach (var (k, v) in PayloadHints(cfg)) payload[k] = v;
        return payload;
    }

    /// <summary>"Send to: all linked" (legacy key waAllNumbers) — every
    /// identity on file instead of just the CRM primary.</summary>
    private static bool AllLinked(JsonElement cfg) => CfgBool(cfg, "allLinked") || CfgBool(cfg, "waAllNumbers");

    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>The WhatsApp numbers this step sends to: the CRM phone, plus —
    /// with "all linked" — every WhatsApp chat linked to the contact. Deduped on
    /// the last nine digits so "+60 12…" and "6012…" are one number.</summary>
    private List<string> WhatsAppTargets(Contact contact, JsonElement cfg)
    {
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(contact.Phone)) raw.Add(contact.Phone);
        if (AllLinked(cfg))
            raw.AddRange(_db.Threads.AsNoTracking()
                .Where(t => t.Channel == Domain.Channels.WhatsApp && t.ContactId == contact.Id)
                .Select(t => t.Key).ToList());
        var seen = new HashSet<string>();
        var outv = new List<string>();
        foreach (var r in raw)
        {
            var d = Digits(r);
            if (d.Length < 6) continue;
            var key = d.Length > 9 ? d[^9..] : d;
            if (seen.Add(key)) outv.Add(r);
        }
        return outv;
    }

    /// <summary>Meta's 24h customer-service window: open when the contact wrote
    /// to this number in the last 24 hours.</summary>
    private bool WhatsAppWindowOpen(Contact contact, string address)
    {
        var digits = Digits(address);
        var since = DateTime.UtcNow.AddHours(-24);
        return _db.Messages.AsNoTracking().Any(m => m.Direction == MessageDirection.In && m.CreatedAt >= since
            && m.Thread != null && m.Thread.Channel == Domain.Channels.WhatsApp
            && (m.Thread.Key == digits || m.Thread.Key == address || m.Thread.ContactId == contact.Id));
    }

    /// <summary>templateVariables {"1": "{{name}}", …} rendered from the CRM, in
    /// slot order.</summary>
    private List<string> TemplateParameters(JsonElement cfg, Contact contact)
    {
        if (cfg.ValueKind != JsonValueKind.Object || !cfg.TryGetProperty("templateVariables", out var tv)
            || tv.ValueKind != JsonValueKind.Object) return new();
        var vars = VariablesFor(contact);
        return tv.EnumerateObject()
            .OrderBy(p => int.TryParse(p.Name, out var n) ? n : int.MaxValue)
            .Select(p => Render(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString(), vars))
            .ToList();
    }

    // ---- AI message composition (owned by the ai area) ----------------------

    private static readonly string[] MessageChannels = { "WhatsApp", "Email", "Telegram" };

    /// <summary>
    /// Let the assistant write this step's message when the step names a skill
    /// (config <c>skill</c>: SkillId, or a legacy skill name) or selects
    /// "AI message"; otherwise the rendered script stands, exactly as before.
    /// "AI message" on its own sends on the contact's latest conversation
    /// channel. A draft that fails its reply checks is NOT sent: the message
    /// channels are dropped for this step and the reason is logged.
    /// </summary>
    private Ai.AiReplyResult? ComposeMessage(FlowRun run, string nodeId, Contact? contact, JsonElement cfg,
        ref string script, ref List<string> channels)
    {
        var aiMessage = channels.Contains("AI message");
        if (aiMessage)
        {
            channels = channels.Where(c => c != "AI message").ToList();
            if (!channels.Any(c => MessageChannels.Contains(c)) && contact != null)
            {
                var latest = _db.Threads.AsNoTracking()
                    .Where(t => t.ContactId == contact.Id
                                && (t.Channel == "whatsapp" || t.Channel == "email" || t.Channel == "telegram"))
                    .OrderByDescending(t => t.UpdatedAt).Select(t => t.Channel).FirstOrDefault();
                var target = latest switch
                {
                    "email" => "Email",
                    "telegram" => "Telegram",
                    "whatsapp" => "WhatsApp",
                    _ => !string.IsNullOrEmpty(contact.Phone) ? "WhatsApp"
                        : !string.IsNullOrEmpty(contact.Email) ? "Email" : null,
                };
                if (target != null) channels = channels.Append(target).ToList();
            }
        }
        if (AiStepBlocked(run, nodeId, contact, cfg, aiMessage, ref channels)) return null; // AI controls
        if (_assistant == null || (!aiMessage && CfgStr(cfg, "skill").Trim() == "")) return null;
        if (!channels.Any(c => MessageChannels.Contains(c))) return null;

        var result = _assistant.ComposeActionTextAsync(run, contact, cfg, script).GetAwaiter().GetResult();
        if (!result.Composed)
        {
            if (result.Reason.Length > 0) Log(run, nodeId, $"ai message: {result.Reason}");
            return result;
        }
        if (result.ShouldSend)
        {
            script = result.Text;
            var tools = result.Tools.Count > 0 ? $", tools: {string.Join(", ", result.Tools.Select(t => t.Name))}" : "";
            Log(run, nodeId, $"ai message composed ({result.Checks.Count(c => c.Ok)}/{result.Checks.Count} checks passed{tools})");
            return result;
        }
        channels = channels.Where(c => !MessageChannels.Contains(c)).ToList();
        Log(run, nodeId, $"ai message held ({result.Action}): {result.Reason}");
        return result;
    }

    /// <summary>Mark this step's queued text jobs as AI-written, with the audit
    /// metadata a review gate shows (skill, tools called, checks run).</summary>
    private void StampAiJobs(FlowRun run, string nodeId, DateTime since, Ai.AiReplyResult ai)
    {
        foreach (var job in _db.OutboundJobs.Where(j => j.FlowRunId == run.Id && j.FlowNode == nodeId
                     && j.Kind == JobKind.Text && j.CreatedAt >= since).ToList())
            job.Payload = new Dictionary<string, object?>(job.Payload) { ["source"] = "ai", ["ai_meta"] = ai.ToMeta() };
        _db.SaveChanges();
    }

    private void QueueWhatsApp(FlowRun run, string nodeId, Contact? contact, JsonElement cfg, string script)
    {
        if (contact == null) { Skip(run, nodeId, "whatsapp skipped: no contact"); return; }
        var targets = WhatsAppTargets(contact, cfg);
        if (targets.Count == 0) { Skip(run, nodeId, "whatsapp skipped: no contact phone"); return; }
        // waMode: text (default) | template | auto (text inside the 24h
        // window, the template once it has closed)
        var mode = CfgStr(cfg, "waMode").Trim().ToLowerInvariant();
        var templateName = CfgStr(cfg, "templateName").Trim();
        List<string>? parameters = null;
        var jobs = new List<int>();
        foreach (var to in targets)
        {
            var useTemplate = templateName != ""
                && (mode == "template" || (mode == "auto" && !WhatsAppWindowOpen(contact, to)));
            string kind; Dictionary<string, object?> payload;
            if (useTemplate)
            {
                parameters ??= TemplateParameters(cfg, contact);
                payload = new() { ["name"] = templateName,
                    ["language"] = CfgStr(cfg, "templateLanguage") is { Length: > 0 } l ? l : "en",
                    ["variables"] = string.Join(", ", parameters),
                    ["parameters"] = parameters };
                kind = JobKind.Template;
            }
            else
            {
                if (string.IsNullOrEmpty(script)) { Skip(run, nodeId, "whatsapp skipped: empty message"); continue; }
                kind = JobKind.Text; payload = new() { ["text"] = script };
            }
            var job = _queue.Enqueue("whatsapp", kind, to, WithHints(payload, cfg), contact.Id,
                flowRunId: run.Id, flowNode: nodeId);
            jobs.Add(job.Id);
            Log(run, nodeId, $"whatsapp queued ({kind}, job #{job.Id})");
        }
    }

    /// <summary>Email: the CRM address plus — with "all linked" — every email
    /// thread linked to the contact (deduped case-insensitively).</summary>
    private void QueueEmail(FlowRun run, string nodeId, Contact? contact, JsonElement cfg, string script)
    {
        if (contact == null) { Skip(run, nodeId, "email skipped: no contact"); return; }
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(contact.Email)) raw.Add(contact.Email.Trim());
        if (AllLinked(cfg))
            raw.AddRange(_db.Threads.AsNoTracking()
                .Where(t => t.Channel == Domain.Channels.Email && t.ContactId == contact.Id)
                .Select(t => t.Key).ToList());
        var targets = raw.Where(a => a.Contains('@'))
            .GroupBy(a => a.Trim().ToLowerInvariant()).Select(g => g.First().Trim()).ToList();
        if (targets.Count == 0) { Skip(run, nodeId, "email skipped: no address"); return; }
        if (string.IsNullOrEmpty(script)) { Skip(run, nodeId, "email skipped: empty message"); return; }
        foreach (var to in targets)
        {
            var payload = new Dictionary<string, object?> { ["text"] = script };
            if (CfgStr(cfg, "subject") is { Length: > 0 } subject) payload["subject"] = subject;
            var job = _queue.Enqueue("email", JobKind.Text, to, WithHints(payload, cfg),
                contact.Id, flowRunId: run.Id, flowNode: nodeId);
            Log(run, nodeId, $"email queued (job #{job.Id})");
        }
    }

    private void QueueTelegram(FlowRun run, string nodeId, Contact? contact, JsonElement cfg, string script)
    {
        if (contact == null) { Skip(run, nodeId, "telegram skipped: no contact"); return; }
        var keys = _db.Threads.Where(t => t.Channel == "telegram" && t.ContactId == contact.Id)
            .Select(t => t.Key).ToList();
        if (keys.Count == 0) { Skip(run, nodeId, "telegram skipped: no linked chat"); return; }
        if (string.IsNullOrEmpty(script)) { Skip(run, nodeId, "telegram skipped: empty message"); return; }
        foreach (var key in keys)
            _queue.Enqueue("telegram", JobKind.Text, key, WithHints(new() { ["text"] = script }, cfg),
                contact.Id, flowRunId: run.Id, flowNode: nodeId);
        Log(run, nodeId, $"telegram queued ×{keys.Count}");
    }

    /// <summary>
    /// SMS: the CRM phone (plus, with "all linked", every SMS conversation linked to
    /// the contact), digits-keyed like the SMS thread. The queue sends it through
    /// OutboundSender — a real Twilio SMS when an SMS inbox is configured, a sandbox
    /// row otherwise (it used to be log-only).
    /// </summary>
    private void QueueSms(FlowRun run, string nodeId, Contact? contact, JsonElement cfg, string script)
    {
        if (contact == null) { Skip(run, nodeId, "sms skipped: no contact"); return; }
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(contact.Phone)) raw.Add(contact.Phone);
        if (AllLinked(cfg))
            raw.AddRange(_db.Threads.AsNoTracking()
                .Where(t => t.Channel == Domain.Channels.Sms && t.ContactId == contact.Id)
                .Select(t => t.Key).ToList());
        var seen = new HashSet<string>();
        var targets = new List<string>();
        foreach (var d in raw.Select(Digits).Where(d => d.Length >= 6))
            if (seen.Add(d.Length > 9 ? d[^9..] : d)) targets.Add(d);
        if (targets.Count == 0) { Skip(run, nodeId, "sms skipped: no contact phone"); return; }
        if (string.IsNullOrEmpty(script)) { Skip(run, nodeId, "sms skipped: empty message"); return; }
        foreach (var to in targets)
        {
            var job = _queue.Enqueue(Domain.Channels.Sms, JobKind.Text, to, WithHints(new() { ["text"] = script }, cfg),
                contact.Id, flowRunId: run.Id, flowNode: nodeId);
            Log(run, nodeId, $"sms queued (job #{job.Id})");
        }
    }

    private void EnqueueCall(FlowRun run, string nodeId, Contact? contact, JsonElement cfg, string script)
    {
        if (contact == null || string.IsNullOrEmpty(contact.Phone))
        { Skip(run, nodeId, "call skipped: no contact phone"); return; }
        if (contact.DoNotCall) { Skip(run, nodeId, "call skipped: do-not-call flag"); return; }
        var whatsapp = CfgStr(cfg, "callVia").ToLowerInvariant().StartsWith("whatsapp");
        var payload = new Dictionary<string, object?>
        {
            ["transport"] = whatsapp ? "baileys" : "sip",
            ["script"] = script,
            ["voice"] = CfgStr(cfg, "voice"),
        };
        if (CfgStr(cfg, "skill") is { Length: > 0 } skill) payload["skill"] = skill;
        var job = _queue.Enqueue(whatsapp ? "whatsapp" : "voice", JobKind.Call, contact.Phone,
            payload, contact.Id, flowRunId: run.Id, flowNode: nodeId);
        Log(run, nodeId, $"call queued via {(whatsapp ? "WhatsApp" : "SIP")} (job #{job.Id})");
    }

    private void EnqueueScriptCall(FlowRun run, string nodeId, Contact? contact, JsonElement cfg)
    {
        if (contact == null) { Skip(run, nodeId, "script call skipped: no contact"); return; }
        var templateId = CfgInt(cfg, "scriptTemplateId");
        var template = templateId > 0 ? _db.ScriptTemplates.Find(templateId) : null;
        if (template == null) { Skip(run, nodeId, "script call skipped: no script chosen"); return; }
        var spoken = Render(template.Content, VariablesFor(contact));
        EnqueueCall(run, nodeId, contact, cfg, spoken);
    }

    private void Skip(FlowRun run, string nodeId, string why)
    {
        Log(run, nodeId, why);
        Track(run, nodeId, FlowStepKind.Skipped, why);
    }

    // ---- hand over / update contact / end ------------------------------------

    /// <summary>
    /// Hand the conversation to people: the contact's latest conversation moves
    /// to the chosen team (unclaimed, so the team's queue picks it up), the bot
    /// stands down on it, and a private note records why. Priority decides who
    /// hears about it: the team's members always; admins too when it is High or
    /// Urgent (or when the team has nobody in it).
    /// </summary>
    private void DoHandover(FlowRun run, JsonElement node, Workflow wf)
    {
        var cfg = Cfg(node);
        var nodeId = Str(node, "id");
        var contact = run.ContactId is int cid ? _db.Contacts.Find(cid) : null;
        var teamRef = CfgStr(cfg, "team").Trim();
        var priority = CfgStr(cfg, "priority").Trim(); if (priority == "") priority = "Normal";
        var reason = Render(CfgStr(cfg, "reason"), VariablesFor(contact));
        Team? team = null;
        if (teamRef != "")
            team = int.TryParse(teamRef, out var tid)
                ? _db.Teams.FirstOrDefault(t => t.Id == tid)
                : _db.Teams.AsEnumerable().FirstOrDefault(t =>
                    string.Equals(t.Name, teamRef, StringComparison.OrdinalIgnoreCase));
        var teamLabel = team?.Name ?? (teamRef != "" && !int.TryParse(teamRef, out _) ? teamRef : "the team");

        var thread = contact == null ? null : _db.Threads
            .Where(t => t.ContactId == contact.Id).OrderByDescending(t => t.UpdatedAt).FirstOrDefault();
        if (thread != null)
        {
            if (team != null) { thread.TeamId = team.Id; thread.Assignee = ""; }
            thread.Status = ThreadStatus.Open;
            Ai.AiEligibility.Set(thread, false); // the current number's tick + the legacy column
            var note = $"Handed over by the flow “{wf.Name}” to {teamLabel} · {priority} priority"
                       + (reason.Trim() != "" ? $" — {reason.Trim()}" : "");
            Ai.Handover.Mark(_db, thread, Ai.HandoverKinds.Rule, reason.Trim() != "" ? reason.Trim()
                : $"The flow “{wf.Name}” handed it to {teamLabel}", "flow", note: false, notify: false, save: false);
            thread.Status = ThreadStatus.Open; // the team's queue picks it up
            _db.Messages.Add(new Message
            {
                ThreadId = thread.Id, AccountId = thread.AccountId, Direction = MessageDirection.Out,
                Source = MessageSource.System, Private = true, Text = Trim(note, 1000),
                Status = "sent", CreatedAt = DateTime.UtcNow,
            });
            thread.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }

        var recipients = team == null ? new List<int>()
            : _db.TeamMembers.Where(m => m.TeamId == team.Id).Select(m => m.UserId).ToList();
        var escalate = priority is "High" or "Urgent";
        if (escalate || recipients.Count == 0)
            recipients.AddRange(_db.Profiles.Where(p => p.Role == Roles.Admin || p.Role == Roles.SuperAdmin)
                .Select(p => p.UserId).ToList());
        var prefix = priority switch { "Urgent" => "URGENT · ", "High" => "High priority · ", _ => "" };
        NotifyUsers(recipients.Distinct(), $"{prefix}Handover → {teamLabel}: {contact?.Name ?? "unknown"}",
            reason, contact != null ? $"/contacts/{contact.Id}" : "");
        Log(run, nodeId, $"handover → {teamLabel} ({priority}){(thread == null ? " — no conversation to move" : "")}");
        _completeDetail = $"{teamLabel} · {priority}";
    }

    /// <summary>"Update contact": add or remove a tag, or set a custom field
    /// (value may hold {{variables}}).</summary>
    private void DoUpdate(FlowRun run, JsonElement node)
    {
        var cfg = Cfg(node);
        var nodeId = Str(node, "id");
        var contact = run.ContactId is int cid ? _db.Contacts.Find(cid) : null;
        if (contact == null) { Skip(run, nodeId, "update skipped: no contact"); return; }
        var op = CfgStr(cfg, "op"); if (op == "") op = "add_tag";
        if (op == "add_tag" && CfgStr(cfg, "updateType") == "chatbot") op = "chatbot"; // the spec's alias
        var vars = VariablesFor(contact);
        switch (op)
        {
            case "chatbot":
                SetChatbot(run, nodeId, contact, cfg);
                return;
            case "add_tag":
            case "remove_tag":
                var tag = Render(CfgStr(cfg, "tag"), vars).Trim();
                if (tag == "") { Skip(run, nodeId, "update skipped: no tag"); return; }
                var tags = contact.Tags.Where(t => !string.Equals(t.Trim(), tag, StringComparison.OrdinalIgnoreCase)).ToList();
                if (op == "add_tag") tags.Add(tag);
                contact.Tags = tags;
                _completeDetail = op == "add_tag" ? $"tagged '{tag}'" : $"untagged '{tag}'";
                break;
            case "set_field":
                var field = CfgStr(cfg, "field").Trim();
                if (field == "") { Skip(run, nodeId, "update skipped: no field"); return; }
                var value = Render(CfgStr(cfg, "value"), vars);
                var attrs = new Dictionary<string, object?>(contact.Attributes) { [field] = value };
                contact.Attributes = attrs;
                _completeDetail = $"{field} = {Trim(value, 60)}";
                break;
            default:
                Skip(run, nodeId, $"update skipped: unknown change '{op}'");
                return;
        }
        contact.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
        Log(run, nodeId, $"contact updated: {_completeDetail}");
    }

    // ---- AI controls: "Set chatbot on/off" + the master switch on AI steps -------

    /// <summary>
    /// "Set chatbot on/off" (an Update step with <c>op: "chatbot"</c>):
    /// <c>value</c> on|off; <c>scope</c> all (every conversation of the contact,
    /// the default) | channels (only those on <c>channels</c> — thread channel
    /// ids or their labels). Writes the per-chat tick through AiEligibility.Set,
    /// so it is the same switch the chat header shows.
    /// </summary>
    private void SetChatbot(FlowRun run, string nodeId, Contact contact, JsonElement cfg)
    {
        var raw = (CfgStr(cfg, "value") is { Length: > 0 } v ? v : CfgStr(cfg, "chatbot")).Trim().ToLowerInvariant();
        bool? on = raw is "on" or "true" or "yes" or "enable" ? true
            : raw is "off" or "false" or "no" or "disable" ? false : null;
        if (on is not bool enabled) { Skip(run, nodeId, "chatbot skipped: choose on or off"); return; }
        var channels = ChatbotChannels(cfg);
        var scopeAll = CfgStr(cfg, "scope").Trim().ToLowerInvariant() != "channels" || channels.Count == 0;
        var q = _db.Threads.Where(t => t.ContactId == contact.Id);
        if (!scopeAll) q = q.Where(t => channels.Contains(t.Channel));
        var threads = q.ToList();
        if (threads.Count == 0)
        {
            Skip(run, nodeId, scopeAll ? "chatbot skipped: no conversation yet"
                : $"chatbot skipped: no conversation on {string.Join(", ", channels)}");
            return;
        }
        foreach (var t in threads) Ai.AiEligibility.Set(t, enabled);
        _db.SaveChanges();
        _completeDetail = $"chatbot {(enabled ? "on" : "off")} · {threads.Count} conversation{(threads.Count == 1 ? "" : "s")}";
        Log(run, nodeId, $"chatbot turned {(enabled ? "on" : "off")} on {threads.Count} conversation(s)"
                         + (scopeAll ? "" : $" ({string.Join(", ", channels)})"));
    }

    private static List<string> ChatbotChannels(JsonElement cfg)
    {
        var raw = new List<string>();
        if (cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("channels", out var el))
        {
            if (el.ValueKind == JsonValueKind.Array)
                raw.AddRange(el.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()));
            else if (el.ValueKind == JsonValueKind.String)
                raw.AddRange((el.GetString() ?? "").Split(','));
        }
        return raw.Select(c => c.Trim().ToLowerInvariant() switch
            {
                "messenger" => Domain.Channels.Facebook,
                "website chat" or "widget" => Domain.Channels.Website,
                var x => x,
            })
            .Where(c => c is Domain.Channels.WhatsApp or Domain.Channels.Email or Domain.Channels.Telegram
                or Domain.Channels.Facebook or Domain.Channels.Instagram or Domain.Channels.Website)
            .Distinct().ToList();
    }

    /// <summary>
    /// An AI-written step (an "AI message", or a send step with a skill) while
    /// the AI controls say no for this contact — the master switch / schedule of
    /// the conversation's outlet, the contact's "never" mode or a never-AI tag.
    /// An AI message is skipped with a step note; a send step with its own
    /// script sends that script as written. False = carry on composing.
    /// </summary>
    private bool AiStepBlocked(FlowRun run, string nodeId, Contact? contact, JsonElement cfg, bool aiMessage,
        ref List<string> channels)
    {
        if (!aiMessage && CfgStr(cfg, "skill").Trim() == "") return false;
        if (!channels.Any(c => MessageChannels.Contains(c))) return false;
        int? entityId = null;
        if (contact != null)
            entityId = _db.Threads.AsNoTracking().Where(t => t.ContactId == contact.Id && t.EntityId != null)
                .OrderByDescending(t => t.UpdatedAt).Select(t => t.EntityId).FirstOrDefault();
        entityId ??= _db.Workflows.AsNoTracking().Where(w => w.Id == run.WorkflowId)
            .Select(w => w.EntityId).FirstOrDefault();
        var why = new Ai.AiControls(_db, _scoping).FlowBlock(contact, entityId);
        if (why == null) return false;
        var reason = why.StartsWith("Off: ") ? why[5..] : why;
        if (aiMessage)
        {
            channels = channels.Where(c => !MessageChannels.Contains(c)).ToList();
            Skip(run, nodeId, $"AI message skipped — {reason}");
        }
        else Log(run, nodeId, $"AI is off ({reason}) — the step's script was used as written");
        return true;
    }

    /// <summary>End: finish with the outcome code, and (when asked) remember the
    /// outcome in the contact's AI memory so chat and calls know how it went.</summary>
    private void DoEnd(FlowRun run, JsonElement node, Workflow wf)
    {
        var nodeId = Str(node, "id");
        var cfg = Cfg(node);
        var code = EndCode(node);
        var remember = CfgStr(cfg, "updateMemory").Trim().ToLowerInvariant() is "yes" or "true";
        if (remember && run.ContactId is int cid)
        {
            try
            {
                var label = node.TryGetProperty("data", out var d) ? Str(d, "label") : "";
                _db.ConversationSummaries.Add(new ConversationSummary
                {
                    ContactId = cid, Source = "flow", Channel = "", Ref = $"flow:{wf.FlowId}:{run.Id}",
                    Summary = Trim($"Finished the “{wf.Name}” flow — outcome {code}"
                                   + (label.Trim() != "" ? $" ({label.Trim()})." : "."), 600),
                    CreatedAt = DateTime.UtcNow,
                });
                _db.SaveChanges();
            }
            catch (Exception e) { Log(run, nodeId, $"memory note skipped: {e.Message}"); }
        }
        Track(run, nodeId, FlowStepKind.Completed, code);
        Finish(run, FlowRunStatus.Done, code);
    }

    private static string EndCode(JsonElement node)
    {
        var code = CfgStr(Cfg(node), "logCode").Trim();
        return code == "" ? "END" : code;
    }

    // ---- wait + follow-up ---------------------------------------------------

    private static TimeSpan Duration(JsonElement cfg, string amountKey, string unitKey, TimeSpan fallback)
    {
        var raw = CfgStr(cfg, amountKey);
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0) return fallback;
        return CfgStr(cfg, unitKey).ToLowerInvariant() switch
        {
            "minutes" => TimeSpan.FromMinutes(n),
            "hours" => TimeSpan.FromHours(n),
            "weeks" => TimeSpan.FromDays(n * 7),
            _ => TimeSpan.FromDays(n),
        };
    }

    private static string Human(TimeSpan t)
    {
        if (t.TotalDays >= 1 && Math.Abs(t.TotalDays - Math.Round(t.TotalDays)) < 0.01)
            return $"{Math.Round(t.TotalDays)} day{(Math.Round(t.TotalDays) == 1 ? "" : "s")}";
        if (t.TotalHours >= 1 && Math.Abs(t.TotalHours - Math.Round(t.TotalHours)) < 0.01)
            return $"{Math.Round(t.TotalHours)} hour{(Math.Round(t.TotalHours) == 1 ? "" : "s")}";
        return $"{Math.Max(1, Math.Round(t.TotalMinutes))} min";
    }

    /// <summary>
    /// "Wait": a set amount of time, or until the contact replies (optionally
    /// giving up after a timeout). Either way the run moves on afterwards; the
    /// <c>answered</c> flag records whether a reply arrived, which the next
    /// "Ask &amp; branch: They replied" step reads.
    /// </summary>
    private bool DoWait(FlowRun run, JsonElement node)
    {
        var cfg = Cfg(node);
        var nodeId = Str(node, "id");
        var forReply = CfgStr(cfg, "mode") == "reply";
        if (Ctx(run, "_park_node") == nodeId)
        {
            var wake = Ctx(run, "_wake");
            if (forReply && wake == "reply") { _completeDetail = "replied"; return true; }
            if (forReply)
            {
                SetCtx(run, "answered", false);
                _completeDetail = "no reply in time";
                Log(run, nodeId, "no reply before the timeout — moving on");
                return true;
            }
            _completeDetail = "wait over";
            return true;
        }
        SetCtx(run, "answered", false);
        SetCtx(run, "reply_consumed", true);
        if (forReply)
        {
            var timeout = Duration(cfg, "timeoutAmount", "timeoutUnit", TimeSpan.Zero);
            var detail = "waiting for a reply" + (timeout > TimeSpan.Zero ? $" (up to {Human(timeout)})" : "");
            Park(run, nodeId, "reply", timeout > TimeSpan.Zero ? DateTime.UtcNow + timeout : null, detail);
        }
        else
        {
            var span = Duration(cfg, "amount", "unit", TimeSpan.FromDays(1));
            Park(run, nodeId, "", DateTime.UtcNow + span, $"waiting {Human(span)}");
        }
        return false;
    }

    /// <summary>
    /// "Follow up if no reply": wait, and if the contact has not answered send a
    /// follow-up (the step's own message, template or AI-written text — through
    /// the normal action path, so the queue paces it and quiet hours hold it),
    /// up to N times. Any reply ends it on the "replied" branch; so does a stop
    /// condition (a tag appears, the conversation is resolved). Running out of
    /// follow-ups takes the "no reply" branch.
    /// </summary>
    private string? DoFollowUp(FlowRun run, JsonElement node)
    {
        var cfg = Cfg(node);
        var nodeId = Str(node, "id");
        var max = Math.Clamp(CfgInt(cfg, "maxFollowUps", 2), 1, 5);
        var gap = Duration(cfg, "waitAmount", "waitUnit", TimeSpan.FromDays(1));
        if (Ctx(run, "_park_node") != nodeId)
        {
            SetCtx(run, "_fu_count", 0);
            SetCtx(run, "answered", false);
            SetCtx(run, "reply_consumed", true);
            Park(run, nodeId, "reply", DateTime.UtcNow + gap, $"waiting {Human(gap)} for a reply");
            return null;
        }
        var sent = CtxInt(run, "_fu_count");
        if (Ctx(run, "_wake") == "reply")
        {
            _completeDetail = sent == 0 ? "replied before any follow-up" : $"replied after follow-up {sent}";
            return "yes";
        }
        var stop = StopReason(run, cfg);
        if (stop != null) { _completeDetail = $"stopped: {stop}"; return "yes"; }
        if (sent >= max)
        {
            SetCtx(run, "answered", false);
            _completeDetail = $"no reply after {sent} follow-up{(sent == 1 ? "" : "s")}";
            return "no";
        }
        if (InQuietHours() || !DoAction(run, node))
        {
            Park(run, nodeId, "reply", DateTime.UtcNow.AddMinutes(30), "", track: false);
            Log(run, nodeId, "quiet hours — follow-up postponed 30 min");
            return null;
        }
        SetCtx(run, "_fu_count", sent + 1);
        Track(run, nodeId, FlowStepKind.FollowUp, $"follow-up {sent + 1} sent");
        Log(run, nodeId, $"follow-up {sent + 1} of {max} sent");
        Park(run, nodeId, "reply", DateTime.UtcNow + gap, "", track: false);
        return null;
    }

    private string? StopReason(FlowRun run, JsonElement cfg)
    {
        if (run.ContactId is not int cid) return null;
        var contact = _db.Contacts.AsNoTracking().FirstOrDefault(c => c.Id == cid);
        var stopTag = CfgStr(cfg, "stopTag");
        if (stopTag.Trim() != "" && HasTag(contact, stopTag)) return $"tag '{stopTag.Trim()}' added";
        if (CfgBool(cfg, "stopOnResolved"))
        {
            if (string.Equals(contact?.Status, "resolved", StringComparison.OrdinalIgnoreCase))
                return "contact resolved";
            var latest = _db.Threads.AsNoTracking().Where(t => t.ContactId == cid)
                .OrderByDescending(t => t.UpdatedAt).Select(t => t.Status).FirstOrDefault();
            if (latest == ThreadStatus.Resolved) return "conversation resolved";
        }
        return null;
    }

    /// <summary>Park the run AT a node (wait/follow-up/decision): it resumes on
    /// <paramref name="until"/> (time or timeout) or, for "reply", on a reply.</summary>
    private void Park(FlowRun run, string nodeId, string waitFor, DateTime? until, string detail, bool track = true)
    {
        run.Status = FlowRunStatus.Waiting;
        run.WaitFor = waitFor;
        run.WaitUntil = until;
        SetCtx(run, "_park_node", nodeId);
        SetCtx(run, "_wake", null);
        if (track && detail != "") Track(run, nodeId, FlowStepKind.Waiting, detail);
        if (detail != "") Log(run, nodeId, $"parked: {detail}");
        _db.SaveChanges();
    }

    // ---- the walk -----------------------------------------------------------

    private const string TimeoutTiming = "No answer / timeout";

    /// <summary>
    /// The edge out of a node. A branch (decision yes/no, follow-up
    /// replied/no-reply) takes the edge from its handle (or, for old flows, a
    /// label starting with it); with no such edge the path simply ends — it
    /// never falls through to the other branch. Timeout edges are only taken by
    /// the timeout machinery.
    /// </summary>
    private JsonElement? PickEdge(Workflow wf, JsonElement node, string? branch)
    {
        var nodeId = Str(node, "id");
        var edges = Edges(wf).Where(e => Str(e, "source") == nodeId && EdgeTiming(e) != TimeoutTiming).ToList();
        if (edges.Count == 0) return null;
        if (branch == null) return edges[0];
        foreach (var e in edges)
            if (Str(e, "sourceHandle").ToLowerInvariant() == branch) return e;
        foreach (var e in edges)
            if (Str(e, "sourceHandle") == "" && Str(e, "label").Trim().ToLowerInvariant().StartsWith(branch)) return e;
        foreach (var e in edges)
        {
            var label = Str(e, "label").Trim().ToLowerInvariant();
            if (Str(e, "sourceHandle") == "" && !label.StartsWith("yes") && !label.StartsWith("no")) return e;
        }
        return null;
    }

    private static string EdgeTiming(JsonElement edge)
    {
        if (edge.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object)
            return Str(c, "timing");
        return "";
    }

    private static double EdgeWaitDays(JsonElement edge)
    {
        if (edge.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object
            && c.TryGetProperty("waitDays", out var w))
            return w.ValueKind == JsonValueKind.Number ? w.GetDouble()
                : double.TryParse(w.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        return 0;
    }

    /// <summary>The soonest "No answer / timeout" edge out of a node (a timeout
    /// edge with no days set gives up after one day).</summary>
    private JsonElement? TimeoutEdgeFrom(Workflow wf, string nodeId) =>
        Edges(wf).Where(e => Str(e, "source") == nodeId && EdgeTiming(e) == TimeoutTiming)
            .OrderBy(e => EdgeWaitDays(e) > 0 ? EdgeWaitDays(e) : 1)
            .Cast<JsonElement?>().FirstOrDefault();

    private static DateTime TimeoutAt(JsonElement edge) =>
        DateTime.UtcNow.AddDays(EdgeWaitDays(edge) > 0 ? EdgeWaitDays(edge) : 1);

    private JsonElement? EdgeById(Workflow wf, string id) =>
        Edges(wf).Where(e => Str(e, "id") == id).Cast<JsonElement?>().FirstOrDefault();

    /// <summary>
    /// Old-style timing on a line (edge): wait N days, wait for a reply (with
    /// an optional "No answer / timeout" line out of the same step as the
    /// fallback), or wait for a record update. The run parks BEFORE the
    /// target step, which is entered when it resumes.
    /// </summary>
    private bool ApplyEdge(FlowRun run, Workflow wf, JsonElement edge)
    {
        var timing = EdgeTiming(edge); if (timing == "") timing = "Immediately";
        var source = Str(edge, "source");
        run.CurrentNode = Str(edge, "target");
        SetCtx(run, "_park_node", null);
        SetCtx(run, "_wake", null);
        SetCtx(run, "_timeout_edge", null);
        if (timing == "Wait N days")
        {
            var days = EdgeWaitDays(edge);
            run.Status = FlowRunStatus.Waiting; run.WaitFor = "";
            run.WaitUntil = DateTime.UtcNow.AddDays(days);
            var detail = $"waiting {days:0.##} day{(days == 1 ? "" : "s")}";
            Track(run, run.CurrentNode, FlowStepKind.Waiting, detail);
            Log(run, run.CurrentNode, $"parked: {detail}");
            _db.SaveChanges();
            return false;
        }
        if (timing is "On contact reply" or "On record update")
        {
            var reply = timing == "On contact reply";
            run.Status = FlowRunStatus.Waiting;
            run.WaitFor = reply ? "reply" : "record";
            run.WaitUntil = null;
            SetCtx(run, "_wait_from", source);
            if (reply) { SetCtx(run, "answered", false); SetCtx(run, "reply_consumed", true); }
            else SetCtx(run, "_park_at", DateTime.UtcNow.ToString("o"));
            if (TimeoutEdgeFrom(wf, source) is JsonElement te)
            {
                SetCtx(run, "_timeout_edge", Str(te, "id"));
                run.WaitUntil = TimeoutAt(te);
            }
            var detail = reply ? "waiting for a reply" : "waiting for a record update";
            Track(run, run.CurrentNode, FlowStepKind.Waiting, detail);
            Log(run, run.CurrentNode, $"parked: {detail}");
            _db.SaveChanges();
            return false;
        }
        _db.SaveChanges();
        return true;
    }

    private void Advance(FlowRun run, Workflow wf)
    {
        _flowKeys[wf.Id] = wf.FlowId;
        var byId = new Dictionary<string, JsonElement>();
        foreach (var n in Nodes(wf)) byId.TryAdd(Str(n, "id"), n);
        for (var i = 0; i < MaxSteps; i++)
        {
            if (!byId.TryGetValue(run.CurrentNode, out var node))
            { Finish(run, FlowRunStatus.Failed, $"step {run.CurrentNode} is no longer in the flow"); return; }
            var nodeId = Str(node, "id");
            var kind = Str(node, "type");
            EnterNode(run, nodeId);
            _completeDetail = "";
            string? branch = null;
            switch (kind)
            {
                case "trigger":
                case "stage": // legacy day-range section — a pass-through
                    break;
                case "action":
                    if (!DoAction(run, node))
                    {
                        Track(run, nodeId, FlowStepKind.Waiting, "quiet hours — postponed 30 min");
                        _db.SaveChanges();
                        return;
                    }
                    // a message just went out: a reply from here on is an answer to it
                    SetCtx(run, "answered", false);
                    SetCtx(run, "reply_consumed", true);
                    break;
                case "decision":
                    branch = Decide(run, node);
                    if (branch == ParkedBranch) return; // "wait for the next reply and ask again"
                    if (branch == EndBranch)
                    {
                        Track(run, nodeId, FlowStepKind.Completed, _completeDetail);
                        Finish(run, FlowRunStatus.Done, "AI unsure — the journey ends here");
                        return;
                    }
                    if (branch == null)
                    {
                        var te = TimeoutEdgeFrom(wf, nodeId);
                        Park(run, nodeId, "reply", te is JsonElement e ? TimeoutAt(e) : null,
                            "waiting for the contact's answer");
                        if (te is JsonElement t) { SetCtx(run, "_timeout_edge", Str(t, "id")); _db.SaveChanges(); }
                        return;
                    }
                    break;
                case "wait":
                    if (!DoWait(run, node)) return;
                    break;
                case "followup":
                    branch = DoFollowUp(run, node);
                    if (branch == null) return;
                    break;
                case "handover":
                    DoHandover(run, node, wf);
                    break;
                case "update":
                    DoUpdate(run, node);
                    break;
                case "end":
                    DoEnd(run, node, wf);
                    return;
                default:
                    Log(run, nodeId, $"unknown step type '{kind}' — passed through");
                    break;
            }
            Track(run, nodeId, FlowStepKind.Completed, _completeDetail != "" ? _completeDetail : branch ?? "");
            SetCtx(run, "_park_node", null);
            SetCtx(run, "_wake", null);
            var edge = PickEdge(wf, node, branch);
            if (edge == null) { Finish(run, FlowRunStatus.Done, "end of path"); return; }
            if (!ApplyEdge(run, wf, edge.Value)) return;
        }
        Finish(run, FlowRunStatus.Failed, $"stopped after {MaxSteps} steps in one go (a loop in the flow?)");
    }

    /// <summary>Record "entered" once per arrival — a run resuming at the step
    /// it parked on is not a second arrival.</summary>
    private void EnterNode(FlowRun run, string nodeId)
    {
        if (Ctx(run, "_node") == nodeId) return;
        SetCtx(run, "_node", nodeId);
        Track(run, nodeId, FlowStepKind.Entered);
    }

    // ---- resume: time + events ----------------------------------------------

    public int ResumeDueRuns()
    {
        var resumed = 0;
        var now = DateTime.UtcNow;
        // crash recovery: a "running" run untouched too long = a dead process
        foreach (var run in _db.FlowRuns.Where(r => r.Status == FlowRunStatus.Running
                     && r.UpdatedAt < now.AddMinutes(-StalledRunningMinutes)).ToList())
            Finish(run, FlowRunStatus.Failed, "stalled mid-step");

        foreach (var run in _db.FlowRuns.Where(r => r.Status == FlowRunStatus.Waiting
                     && r.WaitUntil != null && r.WaitUntil <= now).ToList())
        {
            var wf = _db.Workflows.Find(run.WorkflowId);
            if (wf == null) { Finish(run, FlowRunStatus.Failed, "workflow gone"); continue; }
            _flowKeys[wf.Id] = wf.FlowId;
            var waitedFor = run.WaitFor;
            run.Status = FlowRunStatus.Running; run.WaitUntil = null; run.WaitFor = "";
            if (waitedFor is "reply" or "record")
            {
                // a reply/record wait ran out of time
                var timeoutEdge = Ctx(run, "_timeout_edge");
                if (timeoutEdge != "" && EdgeById(wf, timeoutEdge) is JsonElement te)
                {
                    var at = run.CurrentNode;
                    if (Ctx(run, "_node") == at)
                        Track(run, at, FlowStepKind.Completed, "no reply — took the timeout path");
                    Log(run, at, $"no {(waitedFor == "reply" ? "reply" : "update")} in time — taking the timeout path");
                    SetCtx(run, "answered", false);
                    SetCtx(run, "_timeout_edge", null);
                    SetCtx(run, "_park_node", null);
                    run.CurrentNode = Str(te, "target");
                    _db.SaveChanges();
                    Advance(run, wf);
                    resumed++;
                    continue;
                }
                SetCtx(run, "_wake", "timeout");
            }
            else SetCtx(run, "_wake", "time");
            Log(run, run.CurrentNode, "wait elapsed — resuming");
            Advance(run, wf);
            resumed++;
        }

        // "On record update" waits: resume once any of the contact's records
        // (or their events) changed after the run parked.
        foreach (var run in _db.FlowRuns.Where(r => r.Status == FlowRunStatus.Waiting
                     && r.WaitFor == "record").ToList())
        {
            if (run.ContactId is not int cid) continue;
            var since = DateTime.TryParse(Ctx(run, "_park_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var p) ? p.ToUniversalTime() : run.UpdatedAt;
            var changed = _db.Records.Any(r => r.ContactId == cid && r.UpdatedAt > since)
                          || _db.RecordEvents.Any(e => e.Record != null && e.Record.ContactId == cid && e.CreatedAt > since);
            if (!changed) continue;
            var wf = _db.Workflows.Find(run.WorkflowId);
            if (wf == null) { Finish(run, FlowRunStatus.Failed, "workflow gone"); continue; }
            run.Status = FlowRunStatus.Running; run.WaitUntil = null; run.WaitFor = "";
            SetCtx(run, "_timeout_edge", null);
            SetCtx(run, "_wake", "record");
            Log(run, run.CurrentNode, "record updated — resuming");
            Advance(run, wf);
            resumed++;
        }
        return resumed;
    }

    /// <summary>
    /// Event axis. A customer message (1) starts runs of "Message received"
    /// flows, (2) is remembered on every live run of that contact (so a "They
    /// replied" check after a timed wait sees it), and (3) resumes runs parked
    /// on a reply. Returns how many parked runs it resumed.
    /// </summary>
    public int OnInboundMessage(string channel, string key, string text)
    {
        var contact = ContactFor(channel, key);
        if (contact == null) return 0;
        var started = new List<int>();
        try { started = StartMessageTriggers(contact, channel, text, key); }
        catch { /* a broken trigger must never block resuming parked runs */ }
        var resumed = 0;
        foreach (var run in _db.FlowRuns.Where(r => r.ContactId == contact.Id
                     && (r.Status == FlowRunStatus.Waiting || r.Status == FlowRunStatus.Running)).ToList())
        {
            if (started.Contains(run.Id)) continue;
            // A message the run queued has not gone out yet (pacing, quiet hours):
            // this reply cannot be an answer to it, so it neither wakes a "wait
            // for a reply" nor counts as "they replied".
            if (MessageNotSentYet(run))
            {
                Log(run, run.CurrentNode, $"a message on {channel} arrived before the journey's own message was sent — not counted as a reply");
                continue;
            }
            SetCtx(run, "last_reply", Trim(text, 500));
            SetCtx(run, "last_reply_channel", channel);
            SetCtx(run, "last_reply_key", key);
            SetCtx(run, "reply_consumed", false);
            SetCtx(run, "answered", true);
            if (run.Status != FlowRunStatus.Waiting || run.WaitFor != "reply") { _db.SaveChanges(); continue; }
            var wf = _db.Workflows.Find(run.WorkflowId);
            if (wf == null) continue;
            _flowKeys[wf.Id] = wf.FlowId;
            run.Status = FlowRunStatus.Running; run.WaitFor = ""; run.WaitUntil = null;
            SetCtx(run, "_timeout_edge", null);
            SetCtx(run, "_wake", "reply");
            Log(run, run.CurrentNode, $"contact replied on {channel}");
            Advance(run, wf);
            resumed++;
        }
        return resumed;
    }

    /// <summary>True while one of the run's own message steps (an action
    /// step's text / template / AI message — not a call, not a follow-up nudge,
    /// which answers the earlier message) is still queued or sending.</summary>
    private bool MessageNotSentYet(FlowRun run)
    {
        var pending = _db.OutboundJobs.AsNoTracking()
            .Where(j => j.FlowRunId == run.Id && j.Kind != JobKind.Call
                        && (j.Status == JobStatus.Queued || j.Status == "sending"))
            .Select(j => j.FlowNode).Distinct().ToList();
        if (pending.Count == 0) return false;
        var wf = _db.Workflows.AsNoTracking().FirstOrDefault(w => w.Id == run.WorkflowId);
        if (wf == null) return false;
        var actions = Nodes(wf).Where(n => Str(n, "type") == "action").Select(n => Str(n, "id")).ToHashSet();
        return pending.Any(actions.Contains);
    }

    private Contact? ContactFor(string channel, string key)
    {
        var thread = _db.Threads.Include(t => t.Contact)
            .FirstOrDefault(t => t.Channel == channel && t.Key == key);
        if (thread?.Contact != null) return thread.Contact;
        if (channel == "email")
            return _db.Contacts.FirstOrDefault(c => c.Email.ToLower() == key.ToLower());
        var digits = new string(key.Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return null;
        var tail = digits[^9..];
        foreach (var c in _db.Contacts.AsEnumerable())
        {
            var p = new string((c.Phone ?? "").Where(char.IsDigit).ToArray());
            if (p.Length >= 9 && p[^9..] == tail) return c;
        }
        return null;
    }

    public object Tick()
    {
        var resumed = ResumeDueRuns();
        var started = EvaluateTriggers();
        var drained = _queue.Drain(50);
        return new { resumed, started, drained };
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// Organization-wide quiet hours only — the flow has no sending account yet
    /// (it is resolved at send time), so per-account quiet hours are enforced
    /// by the queue drain instead (AccountDelivery.InQuietHours).
    /// </summary>
    private bool InQuietHours() => Channels.AccountDelivery.InQuietHours(null, _scoping);

    /// <summary>
    /// Run an integration action step. The connection is resolved per entity
    /// (the flow has no signed-in user, so a shared/entity grant answers), and a
    /// missing one SKIPS so the run continues.
    /// </summary>
    private void DoIntegration(FlowRun run, string nodeId, Contact? contact, string action,
        JsonElement cfg, string script)
    {
        if (_actions == null)
        {
            Skip(run, nodeId, $"{action}: skipped (integrations unavailable)");
            return;
        }
        var args = new Dictionary<string, string>();
        if (cfg.ValueKind == JsonValueKind.Object)
            foreach (var p in cfg.EnumerateObject())
                args[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? "" : p.Value.ToString();
        var entityId = contact?.EntityLinks.FirstOrDefault()?.EntityId;
        var result = _actions.RunAsync(action, contact, args, script, entityId, null)
            .GetAwaiter().GetResult();
        Log(run, nodeId, $"{action}: {result.Detail}");
        if (!result.Ok) Track(run, nodeId, FlowStepKind.Skipped, $"{action}: {result.Detail}");
    }

    private void Notify(string[] roles, string title, string body)
    {
        NotificationService.Notify(_db, NotificationCategories.Flows, "system", title, body, "", NotificationAudience.ForRoles(roles));
    }

    private void NotifyUsers(IEnumerable<int> userIds, string title, string body, string link)
    {
        NotificationService.Notify(_db, NotificationCategories.Flows, "system", title, body, link,
            NotificationAudience.ForUsers(userIds));
    }

    private static List<string> ChannelList(JsonElement cfg)
    {
        if (cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("channels", out var c))
        {
            if (c.ValueKind == JsonValueKind.Array)
                return c.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList();
            if (c.ValueKind == JsonValueKind.String) return new() { c.GetString() ?? "" };
        }
        return new();
    }

    /// <summary>A config value as a string list (arrays, or one string; numbers
    /// stringified) — the "contacts" scope picker stores ids either way.</summary>
    private static List<string> CfgList(JsonElement cfg, string key)
    {
        if (cfg.ValueKind != JsonValueKind.Object || !cfg.TryGetProperty(key, out var v)) return new();
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                .Where(s => s.Trim() != "").Select(s => s.Trim()).ToList();
        if (v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            return v.ToString().Trim() is { Length: > 0 } one ? new() { one } : new();
        return new();
    }

    private static bool CfgBool(JsonElement cfg, string key) =>
        CfgStr(cfg, key).Trim().ToLowerInvariant() is "true" or "yes" or "1" or "on";

    // ---- run context (JSON bag; values come back as JsonElement after a reload)

    private static string Ctx(FlowRun run, string key)
    {
        if (!run.Context.TryGetValue(key, out var v) || v == null) return "";
        if (v is JsonElement je)
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => je.ToString(),
            };
        if (v is bool b) return b ? "true" : "false";
        return v.ToString() ?? "";
    }

    private static bool CtxBool(FlowRun run, string key) => Ctx(run, key) == "true";

    private static int CtxInt(FlowRun run, string key) => int.TryParse(Ctx(run, key), out var n) ? n : 0;

    private static void SetCtx(FlowRun run, string key, object? value)
    {
        var ctx = run.Context;
        if (value == null) ctx.Remove(key); else ctx[key] = value;
        run.Context = ctx;
    }

    // ---- step events ---------------------------------------------------------

    private DateTime OrgNow() => OrgClock.ToOrg(DateTime.UtcNow, Zone());

    private TimeZoneInfo Zone() => _zone ??= OrgClock.Zone(_scoping);

    private string FlowKey(FlowRun run)
    {
        if (_flowKeys.TryGetValue(run.WorkflowId, out var key)) return key;
        key = _db.Workflows.AsNoTracking().Where(w => w.Id == run.WorkflowId)
            .Select(w => w.FlowId).FirstOrDefault() ?? "";
        _flowKeys[run.WorkflowId] = key;
        return key;
    }

    /// <summary>
    /// Append one step event. Cheap, and NEVER throws into the engine: a failed
    /// insert is detached so it cannot poison the run's own next save.
    /// </summary>
    private void Track(FlowRun run, string nodeId, string kind, string detail = "")
    {
        FlowStepEvent? ev = null;
        try
        {
            var now = DateTime.UtcNow;
            ev = new FlowStepEvent
            {
                WorkflowId = FlowKey(run), RunId = run.Id, ContactId = run.ContactId,
                NodeId = nodeId ?? "", Kind = kind, Detail = Trim(detail ?? "", 300),
                At = now, Day = OrgClock.Day(now, Zone()),
            };
            _db.FlowStepEvents.Add(ev);
            _db.SaveChanges();
        }
        catch
        {
            try { if (ev != null) _db.Entry(ev).State = EntityState.Detached; } catch { }
        }
    }

    private void Log(FlowRun run, string nodeId, string evt)
    {
        var log = run.Log;
        if (log.Count >= LogCap) log = log.Skip(log.Count - LogCap + 1).ToList();
        log.Add(new { t = DateTime.UtcNow.ToString("o"), node = nodeId, evt = Trim(evt, 300) });
        run.Log = log;
        run.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
    }

    /// <summary>End a run, keeping WHY: the outcome code, "end of path", or the
    /// failure reason (also recorded as a "failed" step event).</summary>
    private void Finish(FlowRun run, string status, string detail = "")
    {
        run.Status = status; run.WaitFor = ""; run.WaitUntil = null;
        run.FinishedAt = DateTime.UtcNow; run.UpdatedAt = DateTime.UtcNow;
        run.Detail = Trim(detail, 300);
        if (status == FlowRunStatus.Failed)
            Track(run, run.CurrentNode, FlowStepKind.Failed, detail);
        Log(run, run.CurrentNode, status == FlowRunStatus.Done ? $"finished: {detail}" : $"{status}: {detail}");
    }

    private static string Trim(string s, int n) => (s ?? "").Length <= n ? s ?? "" : s[..n];
}
