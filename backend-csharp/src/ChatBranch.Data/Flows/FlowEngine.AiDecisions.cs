using System.Globalization;
using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Flows;

/// <summary>
/// "Let AI decide" — the engine side of the two AI decision kinds:
/// <c>AI yes/no</c> (branches yes / no) and <c>AI picks a path</c> (one
/// branch per option, handles opt-1…opt-6, plus <c>none</c>). Config:
/// <code>
/// question          the operator's question (plain words)
/// options           [{id, label, description}] — AI picks a path, 2–6
/// historyMessages   N (default 20, 5–100)
/// historyScope      all | channel | conversation
/// sinceEntered      only messages since the contact entered the journey
/// threshold         0..1 (default 0.6) — a less confident answer is "unsure"
/// unsure            yes | no | wait | end   (yes/no; default no)
///                   none | wait | end       (pick; default none)
/// maxAsks           re-asks before giving up (default 2, 1–5)
/// waitHours         how long one re-ask waits for a reply (default 24, 0 = no limit)
/// </code>
/// Decisions are internal (nothing reaches the customer), but the AI master
/// switch / schedule of the conversation's outlet still applies: switched off
/// = the "when unsure" path with a note, and so does an outlet at its AI usage
/// quota (<c>AiQuota.EntityBlock</c>). Every verdict is logged on the run
/// and in the step's completed event ("AI decided YES (0.82): …"), which the
/// Runs panel shows per contact.
/// </summary>
public partial class FlowEngine
{
    private FlowAiDecision? _decider;
    private FlowAiDecision Decider => _decider ??= new FlowAiDecision(_db, _llm);

    private string? DecideWithAi(FlowRun run, string nodeId, JsonElement cfg, Contact? contact, bool pick)
    {
        var options = pick ? AiOptions(cfg) : new List<FlowAiOption>();
        var threshold = Math.Clamp(CfgDouble(cfg, "threshold", 0.6), 0, 1);
        var unsure = CfgStr(cfg, "unsure").Trim().ToLowerInvariant();
        if (pick ? unsure is not ("none" or "wait" or "end") : unsure is not ("yes" or "no" or "wait" or "end"))
            unsure = pick ? "none" : "no";
        var fallback = pick ? "none" : "no";
        var asksKey = $"_ai_asks:{nodeId}";

        // a "wait for the next reply" ran out of time: give up on the fallback
        if (Ctx(run, "_park_node") == nodeId && Ctx(run, "_wake") == "timeout")
        {
            SetCtx(run, asksKey, null);
            return Settle(run, nodeId, fallback, "AI unsure → " + BranchWord(fallback) + ": no new reply came in time");
        }
        if (pick && options.Count < 2)
        {
            Log(run, nodeId, "AI picks a path: fewer than two options — took \"none of these\"");
            return Settle(run, nodeId, "none", "AI picked none of these: the step has fewer than two options");
        }

        try
        {
            var thread = RunThread(run, contact);
            // the company this run reads: its journey's / conversation's — never another company's chats
            var runCompany = RunCompany(run, contact, thread);
            // the AI master switch / schedule of the conversation's outlet
            int? entityId = thread?.EntityId;
            if (entityId == null && contact != null)
                entityId = _db.Threads.AsNoTracking().Where(t => t.ContactId == contact.Id && t.EntityId != null)
                    .OrderByDescending(t => t.UpdatedAt).ToList()
                    .Where(t => _scoping.ThreadCompany(t) == runCompany).Select(t => t.EntityId).FirstOrDefault();
            entityId ??= _db.Workflows.AsNoTracking().Where(w => w.Id == run.WorkflowId)
                .Select(w => w.EntityId).FirstOrDefault();
            var controls = new Ai.AiControls(_db, _scoping);
            if (!controls.Master(entityId).AllowsAt(DateTime.UtcNow, controls.Zone))
            {
                Log(run, nodeId, "AI is switched off for this outlet — took the \"when unsure\" path");
                return Unsure(run, nodeId, cfg, pick, unsure, asksKey, "AI is switched off", "");
            }
            // the AI usage quota of that outlet (or any level above it): at its cap
            // the model is not called — the "when unsure" path with a note
            if (Ai.AiQuota.EntityBlock(_db, _scoping, entityId) is { } quota)
            {
                Log(run, nodeId, Ai.AiQuota.Reason(quota) + " — took the \"when unsure\" path");
                return Unsure(run, nodeId, cfg, pick, unsure, asksKey, "AI quota reached", Ai.AiQuota.Reason(quota));
            }

            var take = Math.Clamp(CfgInt(cfg, "historyMessages", 20), 5, 100);
            var scope = CfgStr(cfg, "historyScope").Trim().ToLowerInvariant();
            if (scope is not ("channel" or "conversation")) scope = "all";
            DateTime? since = CfgBool(cfg, "sinceEntered") ? run.StartedAt : null;
            var history = contact == null
                ? new List<FlowAiLine>()
                : Decider.History(contact.Id, scope, thread?.Channel, thread?.Id, since, take, runCompany);
            var fields = new Dictionary<string, string>();
            if (contact != null)
            {
                try
                {
                    foreach (var (key, label, kind) in CustomFieldDefs())
                    {
                        var value = FormatAttribute(contact.Attributes, key, kind).Trim();
                        if (value != "") fields[label] = value;
                    }
                }
                catch { /* a malformed schema never blocks a decision */ }
            }
            var req = new FlowAiRequest
            {
                Kind = pick ? FlowAiDecision.Pick : FlowAiDecision.YesNo,
                Question = CfgStr(cfg, "question").Trim(),
                Options = options,
                Contact = contact,
                Fields = fields,
                History = history,
                LatestReply = Ctx(run, "last_reply"),
                Channel = thread?.Channel ?? "",
                Ref = $"flow:{FlowKey(run)}:{nodeId}:run{run.Id}",
            };
            var v = Decider.Decide(req);
            var conf = $"({v.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}{(v.Sandbox ? ", sandbox guess" : "")})";
            if (v.Sandbox) Log(run, nodeId, "sandbox guess — no AI model is configured");

            if (v.Failed)
            {
                Log(run, nodeId, $"AI could not decide: {v.Reason}");
                return Unsure(run, nodeId, cfg, pick, unsure, asksKey, "AI could not decide", v.Reason);
            }
            var confident = v.Answer != "unsure" && v.Confidence >= threshold;
            if (!confident)
                return Unsure(run, nodeId, cfg, pick, unsure, asksKey, $"AI unsure {conf}", v.Reason);

            SetCtx(run, asksKey, null);
            if (pick)
            {
                if (v.OptionIndex is int k && k >= 0 && k < options.Count)
                    return Settle(run, nodeId, options[k].Id, $"AI picked \"{Trim(options[k].Label, 60)}\" {conf}: {v.Reason}");
                return Settle(run, nodeId, "none", $"AI picked none of these {conf}: {v.Reason}");
            }
            return Settle(run, nodeId, v.Answer, $"AI decided {v.Answer.ToUpperInvariant()} {conf}: {v.Reason}");
        }
        catch (Exception ex)
        {
            // never out of Tick: an unexpected failure is just "unsure"
            Log(run, nodeId, $"AI decision failed: {Trim(ex.Message, 120)}");
            return Unsure(run, nodeId, cfg, pick, unsure, asksKey, "AI could not decide", "an unexpected error");
        }
    }

    /// <summary>Take a branch: log it and report it on the completed event.</summary>
    private string Settle(FlowRun run, string nodeId, string branch, string detail)
    {
        detail = detail.TrimEnd(' ', ':');
        Log(run, nodeId, detail);
        _completeDetail = Trim(detail, 300);
        return branch;
    }

    /// <summary>The step's "when unsure" choice: a branch, the end of the run,
    /// or park until the next reply and ask again (at most maxAsks times, then
    /// the fallback — NO / none of these).</summary>
    private string Unsure(FlowRun run, string nodeId, JsonElement cfg, bool pick, string unsure, string asksKey,
        string head, string reason)
    {
        var fallback = pick ? "none" : "no";
        var why = reason.Trim() == "" ? "" : ": " + reason.Trim();
        switch (unsure)
        {
            case "end":
                SetCtx(run, asksKey, null);
                return Settle(run, nodeId, EndBranch, $"{head} → end{why}");
            case "wait":
                var maxAsks = Math.Clamp(CfgInt(cfg, "maxAsks", 2), 1, 5);
                var asked = CtxInt(run, asksKey);
                if (asked >= maxAsks)
                {
                    SetCtx(run, asksKey, null);
                    return Settle(run, nodeId, fallback, $"{head} → {BranchWord(fallback)} after {maxAsks} re-ask{(maxAsks == 1 ? "" : "s")}{why}");
                }
                SetCtx(run, asksKey, asked + 1);
                var hours = Math.Max(0, CfgInt(cfg, "waitHours", 24));
                Park(run, nodeId, "reply", hours > 0 ? DateTime.UtcNow.AddHours(hours) : null,
                    Trim($"{head} — waiting for the next reply to ask again ({asked + 1} of {maxAsks}){why}", 300));
                return ParkedBranch;
            default:
                SetCtx(run, asksKey, null);
                var branch = unsure is "yes" or "no" or "none" ? unsure : fallback;
                return Settle(run, nodeId, branch, $"{head} → {BranchWord(branch)}{why}");
        }
    }

    private static string BranchWord(string branch) => branch switch
    {
        "yes" => "YES",
        "no" => "NO",
        "none" => "none of these",
        _ => branch,
    };

    /// <summary>The step's options (2–6), each with a stable handle id.</summary>
    internal static List<FlowAiOption> AiOptions(JsonElement cfg)
    {
        var outv = new List<FlowAiOption>();
        if (cfg.ValueKind != JsonValueKind.Object || !cfg.TryGetProperty("options", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return outv;
        var used = new HashSet<string>();
        foreach (var el in arr.EnumerateArray())
        {
            if (outv.Count >= 6) break;
            string label, desc, id;
            if (el.ValueKind == JsonValueKind.String) { label = el.GetString() ?? ""; desc = ""; id = ""; }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                label = Str(el, "label"); desc = Str(el, "description"); id = Str(el, "id").Trim().ToLowerInvariant();
            }
            else continue;
            if (label.Trim() == "") continue;
            if (id == "" || used.Contains(id))
                id = Enumerable.Range(1, 12).Select(n => $"opt-{n}").First(x => !used.Contains(x));
            used.Add(id);
            outv.Add(new FlowAiOption(id, label.Trim(), desc.Trim()));
        }
        return outv;
    }

    private static double CfgDouble(JsonElement cfg, string key, double fallback) =>
        double.TryParse(CfgStr(cfg, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
            ? n : fallback;
}
