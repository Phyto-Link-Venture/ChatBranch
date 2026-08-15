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
/// </summary>
public class FlowEngine
{
    private const int MaxSteps = 60;
    private const int LogCap = 200;
    private const int StalledRunningMinutes = 10;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly QueueService _queue;

    public FlowEngine(AppDbContext db, ScopingService scoping, QueueService queue)
    {
        _db = db;
        _scoping = scoping;
        _queue = queue;
    }

    private static readonly string[] ClosedStatuses =
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
        return vars;
    }

    // ---- trigger matching ---------------------------------------------------

    private bool TriggerMatches(JsonElement cfg, Contact contact)
    {
        var when = CfgStr(cfg, "when"); if (when == "") when = "Days after due date";
        if (when == "Record status is")
        {
            var record = PrimaryRecord(contact);
            var wanted = CfgStr(cfg, "status").Trim().ToLowerInvariant();
            return record != null && wanted != "" && record.Status.Trim().ToLowerInvariant() == wanted;
        }
        if (when == "Contact has tag")
        {
            var wanted = CfgStr(cfg, "tag").Trim().ToLowerInvariant();
            var tags = contact.Tags.Select(t => t.Trim().ToLowerInvariant()).ToHashSet();
            return wanted != "" && tags.Contains(wanted);
        }
        var rec = PrimaryRecord(contact);
        if (rec?.DueDate is not DateOnly dueDate) return false;
        var days = CfgInt(cfg, "days");
        var od = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - dueDate.DayNumber;
        if (when == "On the due date") return od == 0;
        if (when == "Days before due date") return od == -Math.Abs(days == 0 ? 1 : days);
        if (when == "Days after due date (exactly)") return od == (days == 0 ? 1 : days);
        return od >= (days == 0 ? 1 : days);
    }

    private bool AlreadyRan(Workflow wf, string nodeId, Contact contact, string repeat)
    {
        var runs = _db.FlowRuns.Where(r => r.WorkflowId == wf.Id && r.TriggerKey == nodeId
                                           && r.ContactId == contact.Id);
        if (runs.Any(r => r.Status == FlowRunStatus.Running || r.Status == FlowRunStatus.Waiting))
            return true;
        if (repeat == "Every 2 days")
            return runs.Any(r => r.StartedAt >= DateTime.UtcNow.AddDays(-2));
        // "Daily until answered" and "Once" both collapse to: don't re-run today / ever
        if (repeat == "Daily until answered")
            return runs.Any(r => r.StartedAt.Date == DateTime.UtcNow.Date);
        return runs.Any();
    }

    public int EvaluateTriggers()
    {
        var started = 0;
        var contacts = _db.Contacts.Where(c => c.Status != "resolved").ToList();
        foreach (var wf in _db.Workflows.Where(w => w.Active).ToList())
        {
            foreach (var node in Nodes(wf).Where(n => Str(n, "type") == "trigger"))
            {
                var cfg = Cfg(node);
                var repeat = CfgStr(cfg, "repeat"); if (repeat == "") repeat = "Once";
                var nodeId = Str(node, "id");
                foreach (var contact in contacts)
                {
                    if (!TriggerMatches(cfg, contact)) continue;
                    if (AlreadyRan(wf, nodeId, contact, repeat)) continue;
                    var run = new FlowRun
                    {
                        WorkflowId = wf.Id, ContactId = contact.Id, TriggerKey = nodeId,
                        CurrentNode = nodeId, Status = FlowRunStatus.Running,
                        StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                    };
                    _db.FlowRuns.Add(run);
                    _db.SaveChanges();
                    Log(run, nodeId, $"trigger fired: {CfgStr(cfg, "when")} ({repeat})");
                    Advance(run, wf);
                    started++;
                }
            }
        }
        return started;
    }

    // ---- decisions ----------------------------------------------------------

    private static readonly Regex YesRe = new(
        @"\b(yes|ya|yeah|yup|ok|okay|sure|can|will|paid|done|sudah|boleh|setuju|bayar|alright|noted)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoRe = new(
        @"\b(no|not|nope|cannot|can't|cant|tak|tidak|later|busy|belum|tunggu)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ClassifyReply(string text)
    {
        var yes = YesRe.Match(text ?? "");
        var no = NoRe.Match(text ?? "");
        if (yes.Success && no.Success) return yes.Index < no.Index ? "yes" : "no";
        return yes.Success ? "yes" : "no";
    }

    private string? Decide(FlowRun run, JsonElement node)
    {
        var cfg = Cfg(node);
        var basis = CfgStr(cfg, "basedOn"); if (basis == "") basis = "Contact's answer";
        var contact = run.ContactId is int cid ? _db.Contacts.Find(cid) : null;
        var nodeId = Str(node, "id");

        if (basis == "Record status")
        {
            var record = PrimaryRecord(contact);
            var wanted = CfgStr(cfg, "status").Trim().ToLowerInvariant();
            string answer = wanted != ""
                ? (record != null && record.Status.Trim().ToLowerInvariant() == wanted ? "yes" : "no")
                : (record != null && ClosedStatuses.Contains(record.Status.Trim().ToLowerInvariant()) ? "yes" : "no");
            Log(run, nodeId, $"decision (record status): {answer}");
            return answer;
        }
        if (basis == "Contact tag")
        {
            var wanted = CfgStr(cfg, "tag").Trim().ToLowerInvariant();
            var tags = (contact?.Tags ?? new()).Select(t => t.Trim().ToLowerInvariant()).ToHashSet();
            var answer = wanted != "" && tags.Contains(wanted) ? "yes" : "no";
            Log(run, nodeId, $"decision (contact tag {wanted}): {answer}");
            return answer;
        }
        var ctx = run.Context;
        if (ctx.TryGetValue("last_reply", out var reply) && reply != null
            && !(ctx.TryGetValue("reply_consumed", out var rc) && rc is JsonElement je && je.ValueKind == JsonValueKind.True))
        {
            ctx["reply_consumed"] = true;
            run.Context = ctx;
            var answer = ClassifyReply(reply.ToString() ?? "");
            Log(run, nodeId, $"decision (reply): {answer}");
            return answer;
        }
        Log(run, nodeId, "parked: waiting for contact reply");
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

        foreach (var ch in channels)
        {
            try
            {
                switch (ch)
                {
                    case "WhatsApp": QueueWhatsApp(run, nodeId, contact, cfg, script); break;
                    case "Email": QueueSimple(run, nodeId, contact, "email", contact?.Email, script); break;
                    case "Telegram": QueueTelegram(run, nodeId, contact, script); break;
                    case "AI voice call": EnqueueCall(run, nodeId, contact, cfg, ""); break;
                    case "Script call": EnqueueScriptCall(run, nodeId, contact, cfg); break;
                    case "In-app push":
                        Notify(new[] { "superadmin", "admin", "agent" },
                            $"Flow push · {contact?.Name ?? "contact"}", script);
                        Log(run, nodeId, "in-app push sent");
                        break;
                    default:
                        Log(run, nodeId, $"{ch}: logged (no provider) — {Trim(script, 80)}");
                        break;
                }
            }
            catch (Exception e) { Log(run, nodeId, $"{ch} failed: {e.Message}"); }
        }
        _db.SaveChanges();
        return true;
    }

    private void QueueWhatsApp(FlowRun run, string nodeId, Contact? contact, JsonElement cfg, string script)
    {
        if (contact == null || string.IsNullOrEmpty(contact.Phone))
        { Log(run, nodeId, "whatsapp skipped: no contact phone"); return; }
        var mode = CfgStr(cfg, "waMode").Trim().ToLowerInvariant();
        var templateName = CfgStr(cfg, "templateName").Trim();
        string kind; Dictionary<string, object?> payload;
        if (mode == "template" && templateName != "")
        {
            payload = new() { ["name"] = templateName,
                ["language"] = CfgStr(cfg, "templateLanguage") is { Length: > 0 } l ? l : "en",
                ["variables"] = "" };
            kind = JobKind.Template;
        }
        else
        {
            if (string.IsNullOrEmpty(script)) { Log(run, nodeId, "whatsapp skipped: empty script"); return; }
            kind = JobKind.Text; payload = new() { ["text"] = script };
        }
        var job = _queue.Enqueue("whatsapp", kind, contact.Phone, payload, contact.Id,
            flowRunId: run.Id, flowNode: nodeId);
        Log(run, nodeId, $"whatsapp queued ({kind}, job #{job.Id})");
    }

    private void QueueSimple(FlowRun run, string nodeId, Contact? contact, string channel,
        string? address, string script)
    {
        if (contact == null || string.IsNullOrEmpty(address) || !address.Contains('@'))
        { Log(run, nodeId, $"{channel} skipped: no address"); return; }
        if (string.IsNullOrEmpty(script)) { Log(run, nodeId, $"{channel} skipped: empty script"); return; }
        var job = _queue.Enqueue(channel, JobKind.Text, address, new() { ["text"] = script },
            contact.Id, flowRunId: run.Id, flowNode: nodeId);
        Log(run, nodeId, $"{channel} queued (job #{job.Id})");
    }

    private void QueueTelegram(FlowRun run, string nodeId, Contact? contact, string script)
    {
        if (contact == null) { Log(run, nodeId, "telegram skipped: no contact"); return; }
        var keys = _db.Threads.Where(t => t.Channel == "telegram" && t.ContactId == contact.Id)
            .Select(t => t.Key).ToList();
        if (keys.Count == 0) { Log(run, nodeId, "telegram skipped: no linked chat"); return; }
        foreach (var key in keys)
            _queue.Enqueue("telegram", JobKind.Text, key, new() { ["text"] = script },
                contact.Id, flowRunId: run.Id, flowNode: nodeId);
        Log(run, nodeId, $"telegram queued ×{keys.Count}");
    }

    private void EnqueueCall(FlowRun run, string nodeId, Contact? contact, JsonElement cfg, string script)
    {
        if (contact == null || string.IsNullOrEmpty(contact.Phone))
        { Log(run, nodeId, "call skipped: no contact phone"); return; }
        if (contact.DoNotCall) { Log(run, nodeId, "call skipped: do-not-call flag"); return; }
        var whatsapp = CfgStr(cfg, "callVia").ToLowerInvariant().StartsWith("whatsapp");
        var payload = new Dictionary<string, object?>
        {
            ["transport"] = whatsapp ? "baileys" : "sip",
            ["script"] = script,
            ["voice"] = CfgStr(cfg, "voice"),
        };
        var job = _queue.Enqueue(whatsapp ? "whatsapp" : "voice", JobKind.Call, contact.Phone,
            payload, contact.Id, flowRunId: run.Id, flowNode: nodeId);
        Log(run, nodeId, $"call queued via {(whatsapp ? "WhatsApp" : "SIP")} (job #{job.Id})");
    }

    private void EnqueueScriptCall(FlowRun run, string nodeId, Contact? contact, JsonElement cfg)
    {
        if (contact == null) { Log(run, nodeId, "script call skipped: no contact"); return; }
        var templateId = CfgInt(cfg, "scriptTemplateId");
        var template = templateId > 0 ? _db.ScriptTemplates.Find(templateId) : null;
        if (template == null) { Log(run, nodeId, "script call skipped: no template"); return; }
        var spoken = Render(template.Content, VariablesFor(contact));
        EnqueueCall(run, nodeId, contact, cfg, spoken);
    }

    private void DoHandover(FlowRun run, JsonElement node)
    {
        var cfg = Cfg(node);
        var contact = run.ContactId is int cid ? _db.Contacts.Find(cid) : null;
        var team = CfgStr(cfg, "team"); if (team == "") team = "Senior agent";
        var reason = Render(CfgStr(cfg, "reason"), VariablesFor(contact));
        Notify(new[] { "superadmin", "admin" },
            $"Flow handover → {team}: {contact?.Name ?? "unknown"}", reason);
        Log(run, Str(node, "id"), $"handover → {team}");
    }

    // ---- the walk -----------------------------------------------------------

    private JsonElement? PickEdge(Workflow wf, JsonElement node, string? branch)
    {
        var nodeId = Str(node, "id");
        var edges = Edges(wf).Where(e => Str(e, "source") == nodeId).ToList();
        if (edges.Count == 0) return null;
        if (branch != null)
        {
            foreach (var e in edges)
            {
                var handle = Str(e, "sourceHandle").ToLowerInvariant();
                var label = Str(e, "label").Trim().ToLowerInvariant();
                if (handle == branch || label.StartsWith(branch)) return e;
            }
            return edges[0];
        }
        var normal = edges.Where(e => EdgeTiming(e) != "No answer / timeout").ToList();
        return (normal.Count > 0 ? normal : edges)[0];
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
        if (edge.TryGetProperty("data", out var d) && d.TryGetProperty("config", out var c)
            && c.TryGetProperty("waitDays", out var w))
            return w.ValueKind == JsonValueKind.Number ? w.GetDouble()
                : double.TryParse(w.ToString(), out var n) ? n : 0;
        return 0;
    }

    private bool ApplyEdge(FlowRun run, JsonElement edge)
    {
        var timing = EdgeTiming(edge); if (timing == "") timing = "Immediately";
        run.CurrentNode = Str(edge, "target");
        if (timing == "Wait N days")
        {
            run.Status = FlowRunStatus.Waiting;
            run.WaitUntil = DateTime.UtcNow.AddDays(EdgeWaitDays(edge));
            Log(run, run.CurrentNode, "parked: waiting");
            _db.SaveChanges();
            return false;
        }
        if (timing == "On contact reply")
        {
            run.Status = FlowRunStatus.Waiting; run.WaitFor = "reply";
            Log(run, run.CurrentNode, "parked: waiting for reply");
            _db.SaveChanges();
            return false;
        }
        if (timing == "On record update")
        {
            run.Status = FlowRunStatus.Waiting; run.WaitFor = "record";
            _db.SaveChanges();
            return false;
        }
        _db.SaveChanges();
        return true;
    }

    private void Advance(FlowRun run, Workflow wf)
    {
        var byId = Nodes(wf).ToDictionary(n => Str(n, "id"), n => n);
        for (var i = 0; i < MaxSteps; i++)
        {
            if (!byId.TryGetValue(run.CurrentNode, out var node))
            { Finish(run, FlowRunStatus.Failed, $"node {run.CurrentNode} not in workflow"); return; }
            var kind = Str(node, "type");
            string? branch = null;
            if (kind == "action")
            {
                if (!DoAction(run, node)) { _db.SaveChanges(); return; }
            }
            else if (kind == "decision")
            {
                branch = Decide(run, node);
                if (branch == null)
                { run.Status = FlowRunStatus.Waiting; run.WaitFor = "reply"; _db.SaveChanges(); return; }
            }
            else if (kind == "handover") DoHandover(run, node);
            else if (kind == "end") { Finish(run, FlowRunStatus.Done, EndCode(node)); return; }

            var edge = PickEdge(wf, node, branch);
            if (edge == null) { Finish(run, FlowRunStatus.Done, "end of path"); return; }
            if (!ApplyEdge(run, edge.Value)) return;
        }
        Finish(run, FlowRunStatus.Failed, "aborted (cycle?)");
    }

    private static string EndCode(JsonElement node)
    {
        var code = CfgStr(Cfg(node), "logCode").Trim();
        return code == "" ? "END" : code;
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
            run.Status = FlowRunStatus.Running; run.WaitUntil = null;
            Log(run, run.CurrentNode, "wait elapsed — resuming");
            Advance(run, wf);
            resumed++;
        }
        return resumed;
    }

    public int OnInboundMessage(string channel, string key, string text)
    {
        var contact = ContactFor(channel, key);
        if (contact == null) return 0;
        var resumed = 0;
        foreach (var run in _db.FlowRuns.Where(r => r.Status == FlowRunStatus.Waiting
                     && r.WaitFor == "reply" && r.ContactId == contact.Id).ToList())
        {
            var wf = _db.Workflows.Find(run.WorkflowId);
            if (wf == null) continue;
            var ctx = run.Context;
            ctx["last_reply"] = Trim(text, 500);
            ctx["reply_consumed"] = false;
            ctx["answered"] = true;
            run.Context = ctx;
            run.Status = FlowRunStatus.Running; run.WaitFor = "";
            Log(run, run.CurrentNode, $"contact replied on {channel}");
            Advance(run, wf);
            resumed++;
        }
        return resumed;
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

    private void Notify(string[] roles, string title, string body)
    {
        var users = _db.Profiles.Where(p => roles.Contains(p.Role)).Select(p => p.UserId).ToList();
        foreach (var uid in users)
            _db.Notifications.Add(new Notification { UserId = uid, Kind = "system",
                Title = Trim(title, 160), Body = Trim(body, 400), CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
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

    private void Log(FlowRun run, string nodeId, string evt)
    {
        var log = run.Log;
        if (log.Count >= LogCap) log = log.Skip(log.Count - LogCap + 1).ToList();
        log.Add(new { t = DateTime.UtcNow.ToString("o"), node = nodeId, evt = Trim(evt, 300) });
        run.Log = log;
        run.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
    }

    private void Finish(FlowRun run, string status, string detail = "")
    {
        run.Status = status; run.WaitFor = ""; run.WaitUntil = null;
        run.FinishedAt = DateTime.UtcNow; run.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
    }

    private static string Trim(string s, int n) => (s ?? "").Length <= n ? s ?? "" : s[..n];
}
