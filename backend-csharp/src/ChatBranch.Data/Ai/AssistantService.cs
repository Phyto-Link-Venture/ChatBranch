using System.Globalization;
using System.Text.Json;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// The channel-agnostic AI (port of aicore/assistant.py): assemble the system
/// prompt from persona hard-rules + CRM enrichment + knowledge + memory +
/// skills, then generate through the pluggable <see cref="ILlmClient"/>.
/// whatsapp/ai and every other channel are thin delegates to this, so
/// behaviour is identical everywhere.
///
/// Generation is TOOL-AWARE and SELF-CHECKING:
///  * the model may call the built-in skills (current time/week, date maths,
///    timezone conversion) and the operator's tool skills (sandboxed JS);
///  * every draft runs through the enabled reply-check skills; a failing draft
///    is regenerated with the failure reason (max 2 retries), then the check's
///    OnFail applies — handover (a human answers) or block.
///
/// It is a pure GENERATOR: it returns an <see cref="AiReplyResult"/> and never
/// sends. Callers decide. Every path is exception-safe — nothing here may
/// throw into a webhook.
/// </summary>
public partial class AssistantService
{
    public const int MaxCheckRetries = 2;
    private const int MaxToolRounds = 4;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly PersonaService _persona;
    private readonly KnowledgeService _knowledge;
    private readonly ILlmClient _llm;
    private readonly SkillService _skills;

    public AssistantService(AppDbContext db, ScopingService scoping, PersonaService persona,
        KnowledgeService knowledge, ILlmClient llm, SkillService? skills = null)
    {
        _db = db;
        _scoping = scoping;
        _persona = persona;
        _knowledge = knowledge;
        _llm = llm;
        _skills = skills ?? new SkillService(db, scoping);
    }

    // ---- prompt assembly ------------------------------------------------------

    /// <summary>Assemble the full system prompt for a given entity/channel/contact.</summary>
    public string BuildSystemPrompt(int? entityId, string? channel, Contact? contact, RetrievalQuery? query)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(_persona.SystemPreamble(entityId));

        var skills = SkillsBlock(_skills.InScope(entityId, channel));
        if (skills.Length > 0) sb.AppendLine().AppendLine(skills);

        var knowledge = KnowledgeAndLessons(query, entityId, channel); // + LEARNED block, grounding kept for RunAsync
        if (knowledge.Length > 0) sb.AppendLine().AppendLine(knowledge);

        if (contact != null)
        {
            sb.AppendLine().AppendLine(ContactBlock(contact));
            var memory = MemoryBlock(contact);
            if (memory.Length > 0) sb.AppendLine().AppendLine(memory);
        }

        sb.AppendLine().AppendLine(ClockLine());
        sb.AppendLine("For the current time, today's date or weekday, week numbers and date arithmetic, "
                      + "call the date/time tools instead of guessing.");
        return sb.ToString().TrimEnd();
    }

    private string ClockLine()
    {
        var tz = _skills.OrgTimeZone();
        var local = TimeZoneInfo.ConvertTime(_skills.UtcNow, tz);
        return $"Current date and time: {local.ToString("dddd, d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)} "
               + $"({tz.Id}, UTC{local.ToString("zzz", CultureInfo.InvariantCulture)}).";
    }

    /// <summary>The SKILLS prompt section for an entity (voice dispatch reads it
    /// too): enabled instruction skills in scope.</summary>
    public string SkillsBlock(int? entityId, string? channel = null) =>
        SkillsBlock(_skills.InScope(entityId, channel));

    /// <summary>Prompt (instruction) skills only — tool and check skills reach the
    /// model as callable tools and as post-checks, not as prose.</summary>
    private static string SkillsBlock(IEnumerable<AgentSkill> inScope)
    {
        var skills = inScope.Where(s => s.Kind is SkillKinds.Prompt or "").ToList();
        if (skills.Count == 0) return "";
        var sb = new System.Text.StringBuilder("SKILLS:\n");
        foreach (var s in skills)
        {
            var when = string.IsNullOrWhiteSpace(s.UseWhen) ? "" : $" (use when: {s.UseWhen})";
            sb.AppendLine($"- {s.Name}{when}: {s.Instructions}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string ContactBlock(Contact c)
    {
        var bits = new List<string> { $"Name: {c.Name}" };
        if (!string.IsNullOrEmpty(c.CompanyName)) bits.Add($"Company: {c.CompanyName}");
        if (!string.IsNullOrEmpty(c.Language)) bits.Add($"Language: {c.Language}");
        if (c.Tags.Count > 0) bits.Add($"Tags: {string.Join(", ", c.Tags)}");
        if (!string.IsNullOrEmpty(c.AiProfileSummary)) bits.Add($"Memory: {c.AiProfileSummary}");
        return "CONTACT:\n" + string.Join("\n", bits.Select(b => "- " + b));
    }

    /// <summary>
    /// Durable memory (aicore/memory.py build_context): learned preferences plus
    /// the latest conversation summaries, bounded so it never crowds the prompt.
    /// </summary>
    private string MemoryBlock(Contact c)
    {
        try
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(c.PreferredContactTime)) lines.Add($"Prefers contact: {c.PreferredContactTime}");
            if (!string.IsNullOrWhiteSpace(c.CommunicationStyle)) lines.Add($"Style: {c.CommunicationStyle}");
            if (!string.IsNullOrWhiteSpace(c.SentimentTrend)) lines.Add($"Sentiment: {c.SentimentTrend}");
            foreach (var s in _db.ConversationSummaries.AsNoTracking().Where(s => s.ContactId == c.Id)
                         .OrderByDescending(s => s.Id).Take(3).ToList())
                if (!string.IsNullOrWhiteSpace(s.Summary))
                    lines.Add($"{s.CreatedAt:yyyy-MM-dd} ({s.Source}): {Trim(s.Summary, 400)}");
            if (lines.Count == 0) return "";
            return Trim("CONTACT MEMORY:\n" + string.Join("\n", lines.Select(l => "- " + l)), 1500);
        }
        catch { return ""; }
    }

    // ---- chat replies -----------------------------------------------------------

    /// <summary>
    /// Generate a reply for a thread's latest inbound. A reply that keeps failing
    /// its checks is not returned for sending (Text = ""); the handover/block is
    /// recorded on the thread (private note, status pending, notification).
    /// </summary>
    public Task<AiReplyResult> GenerateReplyAsync(Thread thread, CancellationToken ct = default) =>
        GenerateReplyAsync(thread, applyOutcome: true, ct);

    /// <param name="applyOutcome">false = no side effects at all (a preview /
    /// operator draft): no note, no status change, no notification.</param>
    public async Task<AiReplyResult> GenerateReplyAsync(Thread thread, bool applyOutcome,
        CancellationToken ct = default)
    {
        try
        {
            var contact = await ContactAsync(thread.ContactId, ct);
            var history = History(thread.Id, 20);
            var lastUser = history.LastOrDefault(m => m.Direction == MessageDirection.In)?.Text ?? "";

            var system = BuildSystemPrompt(thread.EntityId, thread.Channel, contact, RetrievalQuery.FromHistory(history));
            var messages = history.Select(m => new LlmMessage(
                m.Direction == MessageDirection.In ? "user" : "assistant", m.Text)).ToList();

            var result = await RunAsync(new Spec(system, messages, thread.EntityId, thread.Channel, contact,
                lastUser, "chat_reply", thread.Key), _llm, ct);
            if (applyOutcome) ApplyOutcome(thread, result);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return AiReplyResult.Failed("chat_reply", ex.Message); // never break a send/webhook path
        }
    }

    // ---- follow-ups -------------------------------------------------------------------

    /// <summary>
    /// A short, natural nudge for a conversation that went quiet. Pure generator:
    /// no side effects beyond usage metering. References the open question,
    /// never repeats the previous message, respects persona/skills/checks.
    /// Sandbox mode returns a fixed, attempt-specific nudge (still checked).
    /// </summary>
    public async Task<AiReplyResult> GenerateFollowUpAsync(Thread thread, int attempt, string? guidance,
        CancellationToken ct = default)
    {
        try
        {
            attempt = Math.Max(1, attempt);
            var contact = await ContactAsync(thread.ContactId, ct);
            var history = History(thread.Id, 20);
            var lastUser = history.LastOrDefault(m => m.Direction == MessageDirection.In)?.Text ?? "";
            var lastOut = history.LastOrDefault(m => m.Direction == MessageDirection.Out)?.Text ?? "";

            var system = BuildSystemPrompt(thread.EntityId, thread.Channel, contact, RetrievalQuery.FromHistory(history)) + "\n\n" +
                $"FOLLOW-UP: The customer has not replied to your last message. Write follow-up #{attempt}: "
                + "one or two short, natural sentences that refer to the open question or request in this "
                + "conversation. Never repeat your previous message word for word, don't pressure, don't "
                + "over-apologise, and write only the message itself."
                + (string.IsNullOrWhiteSpace(guidance) ? "" : $"\nOperator guidance: {guidance.Trim()}");
            var messages = history.Select(m => new LlmMessage(
                m.Direction == MessageDirection.In ? "user" : "assistant", m.Text)).ToList();
            messages.Add(new LlmMessage("system", $"Write follow-up #{attempt} now."));

            var llm = _llm.IsConfigured ? _llm : new FixedTextLlm(SandboxFollowUp(contact, attempt, lastOut));
            return await RunAsync(new Spec(system, messages, thread.EntityId, thread.Channel, contact, lastUser,
                "follow_up", thread.Key) { NotRepeating = lastOut }, llm, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return AiReplyResult.Failed("follow_up", ex.Message);
        }
    }

    private static string SandboxFollowUp(Contact? c, int attempt, string lastOut)
    {
        var first = (c?.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var hi = string.IsNullOrEmpty(first) ? "Hi" : $"Hi {first}";
        var options = new[]
        {
            $"{hi}, just checking in — did you get a chance to look at my last message?",
            $"{hi}, following up in case my earlier message got buried. Happy to help whenever you're ready.",
            $"{hi}, I'll leave it here for now — just reply whenever you'd like to pick this up.",
        };
        var pick = options[Math.Min(attempt, options.Length) - 1];
        if (string.Equals(pick.Trim(), lastOut.Trim(), StringComparison.OrdinalIgnoreCase))
            pick = options[attempt % options.Length];
        return pick;
    }

    // ---- flow messages ------------------------------------------------------------------

    /// <summary>
    /// The message-composition seam of a flow Action step. When the step names a
    /// skill (config <c>skill</c> = an AgentSkill SkillId, or a legacy skill
    /// NAME) or selects "AI message", the AI writes the message — the step's
    /// rendered script is guidance, the skill steers it, tools and checks apply.
    /// Otherwise (or with no AI model configured) the rendered script is returned
    /// unchanged, exactly as before. Pure generator: the flow engine sends.
    /// </summary>
    public async Task<AiReplyResult> ComposeActionTextAsync(FlowRun run, Contact? contact, JsonElement config,
        string renderedScript, CancellationToken ct = default)
    {
        AiReplyResult Static(string reason = "") => new()
        {
            Text = renderedScript, Draft = renderedScript, Action = AiActions.Send, Feature = "flow_static",
            Composed = false, Reason = reason,
        };
        try
        {
            var skillRef = Cfg(config, "skill").Trim();
            var channels = CfgList(config, "channels");
            var aiMessage = channels.Any(c => c.Equals("AI message", StringComparison.OrdinalIgnoreCase))
                            || Cfg(config, "aiMessage") == "true";
            if (skillRef.Length == 0 && !aiMessage) return Static();
            if (!_llm.IsConfigured)
                return Static("No AI model is configured (sandbox) — the step's script was used as written.");

            var entityId = await FlowEntityAsync(run, contact, ct);
            AgentSkill? skill = null;
            if (skillRef.Length > 0)
            {
                skill = FindSkill(skillRef, entityId);
                if (skill == null && !aiMessage) return Static($"Skill '{skillRef}' not found — the step's script was used.");
            }

            var channel = channels.Select(c => c.ToLowerInvariant())
                .FirstOrDefault(c => c is "whatsapp" or "email" or "telegram")
                ?? await LatestChannelAsync(contact, ct) ?? "whatsapp";
            var thread = contact == null ? null : await _db.Threads.AsNoTracking()
                .Where(t => t.ContactId == contact.Id && t.Channel == channel)
                .OrderByDescending(t => t.UpdatedAt).FirstOrDefaultAsync(ct);

            var sb = new System.Text.StringBuilder(BuildSystemPrompt(entityId, channel, contact, renderedScript));
            sb.AppendLine().AppendLine()
              .AppendLine("TASK: You are writing ONE outbound message to this customer as a step of an automated "
                          + "workflow — it is not a reply to something they just said. Write only the message text.");
            if (skill != null)
            {
                sb.AppendLine().Append($"Use the skill \"{skill.Name}\"");
                switch (skill.Kind)
                {
                    case SkillKinds.Tool:
                        sb.AppendLine($": call the tool `{SkillService.ToolName(skill)}` to get the facts the message needs.");
                        break;
                    case SkillKinds.Check:
                        sb.AppendLine($": the message must satisfy this rule — {FirstNonEmpty(skill.Description, skill.UseWhen, skill.Name)}.");
                        break;
                    default:
                        sb.AppendLine(":").AppendLine(FirstNonEmpty(skill.Instructions, skill.Description));
                        break;
                }
            }
            sb.AppendLine().AppendLine(string.IsNullOrWhiteSpace(renderedScript)
                ? "The step gives no script: write a short, friendly message that fits the skill and the conversation."
                : "The step's script (use it as guidance for intent and content; keep links, amounts, dates and names exactly):\n\""
                  + renderedScript + "\"");

            var messages = new List<LlmMessage>();
            if (thread != null)
                messages.AddRange(History(thread.Id, 10).Select(m => new LlmMessage(
                    m.Direction == MessageDirection.In ? "user" : "assistant", m.Text)));
            messages.Add(new LlmMessage("system", "Write the message now."));

            var nodeId = run.CurrentNode;
            var result = await RunAsync(new Spec(sb.ToString().TrimEnd(), messages, entityId, channel, contact,
                thread == null ? "" : History(thread.Id, 10).LastOrDefault(m => m.Direction == MessageDirection.In)?.Text ?? "",
                "flow_message", $"flow:{run.Id}:{nodeId}"), _llm, ct);
            // The real client degrades to the sandbox echo when the provider
            // errors — an echo must never reach a customer as a flow message.
            if (result.Provider == "sandbox")
                return Static("The AI model could not be reached — the step's script was used as written.");
            result = result with { SkillId = skill?.SkillId };
            if (!result.ShouldSend) ApplyOutcome(null, result, contact);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A composing failure must not cost the customer the message: fall
            // back to the step's script, exactly what the step said before.
            return Static($"AI composition failed ({ex.Message}) — the step's script was used.");
        }
    }

    private AgentSkill? FindSkill(string skillRef, int? entityId)
    {
        var rows = (entityId is int ? _scoping.ScopeInherited(_db.AgentSkills.AsNoTracking(), entityId)
                : _db.AgentSkills.AsNoTracking()).ToList();
        // SkillId first (the contract); legacy flow nodes stored the NAME.
        return rows.Where(s => s.SkillId == skillRef).OrderByDescending(s => s.EntityId == entityId).FirstOrDefault()
               ?? rows.Where(s => string.Equals(s.Name.Trim(), skillRef, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(s => s.EntityId == entityId).FirstOrDefault();
    }

    private async Task<int?> FlowEntityAsync(FlowRun run, Contact? contact, CancellationToken ct)
    {
        var wfEntity = await _db.Workflows.AsNoTracking().Where(w => w.Id == run.WorkflowId)
            .Select(w => w.EntityId).FirstOrDefaultAsync(ct);
        if (wfEntity != null) return wfEntity;
        if (contact == null) return null;
        var links = await _db.ContactEntities.AsNoTracking().Where(l => l.ContactId == contact.Id)
            .Select(l => l.EntityId).Take(2).ToListAsync(ct);
        return links.Count == 1 ? links[0] : null;
    }

    private async Task<string?> LatestChannelAsync(Contact? contact, CancellationToken ct)
    {
        if (contact == null) return null;
        return await _db.Threads.AsNoTracking()
            .Where(t => t.ContactId == contact.Id && (t.Channel == "whatsapp" || t.Channel == "email" || t.Channel == "telegram"))
            .OrderByDescending(t => t.UpdatedAt).Select(t => t.Channel).FirstOrDefaultAsync(ct);
    }

    // ---- the generate → check → retry engine ----------------------------------------------

    private sealed record Spec(string System, List<LlmMessage> Messages, int? EntityId, string Channel,
        Contact? Contact, string LastMessage, string Feature, string Ref)
    {
        /// <summary>Follow-ups: the draft must not equal this (the previous message).</summary>
        public string NotRepeating { get; init; } = "";
    }

    private async Task<AiReplyResult> RunAsync(Spec spec, ILlmClient llm, CancellationToken ct)
    {
        var inScope = _skills.InScope(spec.EntityId, spec.Channel);
        var toolSet = _skills.BuildTools(inScope);
        var checkSkills = inScope.Where(s => s.Kind == SkillKinds.Check).ToList();
        var ctx = _skills.Context(spec.Contact, spec.Channel, spec.LastMessage);
        var metered = new MeteredLlm(llm);

        var tools = new List<AiToolCall>();
        var checks = new List<AiCheckRun>();
        var draft = "";
        var failing = new List<SkillService.CheckRun>();
        var action = "";        // "" after the loop = checks never passed
        var reason = "";
        var attempt = 0;

        for (; attempt <= MaxCheckRetries; attempt++)
        {
            metered.Attempt = attempt;
            var messages = new List<LlmMessage>(spec.Messages);
            if (attempt > 0) messages.Add(new LlmMessage("system", Feedback(draft, failing)));
            var outcome = await ToolLoop.RunAsync(metered, spec.System, messages, toolSet.Tools,
                (call, _) => Task.FromResult(_skills.ExecuteTool(toolSet, call, ctx)),
                maxRounds: MaxToolRounds, ct: ct);
            tools.AddRange(outcome.Steps.Select(s => new AiToolCall(s.Tool, Trim(s.ArgumentsJson, 500),
                Trim(s.Result, 500), s.Ok)));
            var text = RenderPlaceholders((outcome.Text ?? "").Trim(), spec.Contact);
            if (text.Length == 0)
            {
                // nothing on a retry = the model could not fix the draft → fall
                // through to the failing-checks outcome with the last real draft
                if (attempt == 0)
                {
                    action = AiActions.Empty;
                    reason = outcome.RoundsExhausted ? "The model kept calling tools and never answered." : "The model returned nothing.";
                }
                break;
            }
            draft = text;

            var runs = _skills.RunChecks(checkSkills, draft, ctx);
            if (spec.NotRepeating.Length > 0 &&
                string.Equals(Normalize(draft), Normalize(spec.NotRepeating), StringComparison.OrdinalIgnoreCase))
                runs.Add(new SkillService.CheckRun("builtin:no_repeat", "Not a repeat", false,
                    "The follow-up repeats the previous message word for word.", "Write a different, shorter nudge.",
                    null, 0, "retry"));
            checks.AddRange(runs.Select(r => new AiCheckRun(r.SkillId, r.Name, r.Ok, r.Reason, r.Fix, r.Error, attempt)));
            failing = runs.Where(r => !r.Ok).ToList();
            if (failing.Count == 0) { action = AiActions.Send; break; }
            // A check whose OnFail is "handover" flags a situation a rewrite
            // cannot fix (a refund dispute, legal threat…): straight to a human.
            if (failing.Any(f => f.OnFail == "handover")) { action = AiActions.Handover; break; }
        }

        if (action == "")
        {
            // Still failing after the retries: block only when EVERY failing check
            // says block; an exhausted "retry" means a human answers.
            action = failing.All(f => f.OnFail == "block") ? AiActions.Block : AiActions.Handover;
        }
        if (failing.Count > 0 && action is AiActions.Handover or AiActions.Block)
            reason = string.Join("; ", failing.Select(f => $"{f.Name}: {f.Reason}"));

        var result = new AiReplyResult
        {
            Text = action == AiActions.Send ? draft : "",
            Draft = draft,
            Action = action,
            Reason = reason,
            Retries = Math.Min(attempt, MaxCheckRetries),
            Tools = tools,
            Checks = checks,
            Grounding = TakeGrounding(),
            Provider = metered.Calls.Count > 0 ? metered.Calls[^1].Result.Provider : "",
            Model = metered.Calls.Count > 0 ? metered.Calls[^1].Result.Model : "",
            PromptTokens = metered.Calls.Sum(c => c.Result.PromptTokens),
            CompletionTokens = metered.Calls.Sum(c => c.Result.CompletionTokens),
            Feature = spec.Feature,
        };
        RecordUsage(metered, spec, result);
        return result;
    }

    private static string Feedback(string draft, IEnumerable<SkillService.CheckRun> failing)
    {
        var sb = new System.Text.StringBuilder(
            "Your previous draft was rejected by an automatic reply check and was NOT sent.\n");
        sb.AppendLine($"Rejected draft: \"{Trim(draft, 600)}\"");
        sb.AppendLine("Problems:");
        foreach (var f in failing)
            sb.AppendLine($"- {f.Name}: {f.Reason}" + (string.IsNullOrWhiteSpace(f.Fix) ? "" : $" (fix: {f.Fix})"));
        sb.Append("Write a new message to the customer that fixes these problems. Output only the message text.");
        return sb.ToString();
    }

    private static string Normalize(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();

    // ---- outcome: handover / block -------------------------------------------------------

    /// <summary>
    /// Record a held-back draft. Handover: private note with the reason and the
    /// draft, conversation set to pending, the team/assignee/admins notified.
    /// Block: the private note only. Both write an audit row. No-op for any
    /// other action. Never throws.
    /// </summary>
    public void ApplyOutcome(Thread? thread, AiReplyResult r, Contact? contact = null)
    {
        if (r.Action is not (AiActions.Handover or AiActions.Block)) return;
        try
        {
            var handover = r.Action == AiActions.Handover;
            Thread? tracked = thread == null ? null : _db.Threads.FirstOrDefault(t => t.Id == thread.Id);
            if (tracked != null)
            {
                _db.Messages.Add(new Message
                {
                    ThreadId = tracked.Id, Direction = MessageDirection.Out, Source = MessageSource.System,
                    Private = true, Status = "note", AccountId = tracked.AccountId, CreatedAt = DateTime.UtcNow,
                    Text = Trim((handover
                                    ? "AI reply held for a human — it failed its reply checks. "
                                    : "AI reply blocked by a reply check. ")
                                + $"Reason: {r.Reason}"
                                + (string.IsNullOrWhiteSpace(r.Draft) ? "" : $"\nDraft (not sent): \"{Trim(r.Draft, 600)}\""), 1500),
                });
                if (handover && tracked.Status is ThreadStatus.Open or ThreadStatus.Snoozed)
                    tracked.Status = ThreadStatus.Pending;
                tracked.UpdatedAt = DateTime.UtcNow; // the SSE stream refreshes the inbox
            }
            if (handover) Notify(tracked, contact, r);
            _db.AuditLogs.Add(new AuditLog
            {
                Actor = "ai",
                Action = r.Feature == "flow_message"
                    ? (handover ? "ai.flow_message.handover" : "ai.flow_message.blocked")
                    : (handover ? "ai.reply.handover" : "ai.reply.blocked"),
                Target = tracked != null ? $"{tracked.Channel}:{tracked.Key}" : contact?.Name ?? "",
                Detail = Trim(r.Reason, 500),
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
        catch { /* recording a handover must never break the reply path */ }
    }

    private void Notify(Thread? thread, Contact? contact, AiReplyResult r)
    {
        if (_scoping.SettingValue("notif_messages") is "false" or "0" or "off") return; // operator muted
        var users = _db.Profiles.AsNoTracking()
            .Where(p => p.Role == Roles.SuperAdmin || p.Role == Roles.Admin)
            .Select(p => p.UserId).ToList();
        if (thread != null)
        {
            if (thread.TeamId is int teamId)
                users.AddRange(_db.TeamMembers.AsNoTracking().Where(m => m.TeamId == teamId).Select(m => m.UserId));
            if (!string.IsNullOrWhiteSpace(thread.Assignee))
                users.AddRange(_db.Users.AsNoTracking().Where(u => u.Username == thread.Assignee).Select(u => u.Id));
        }
        var who = thread?.Name is { Length: > 0 } n ? n : contact?.Name ?? "a customer";
        foreach (var uid in users.Distinct())
            _db.Notifications.Add(new Notification
            {
                UserId = uid, Kind = "message",
                Title = Trim($"AI needs a human · {who}", 160),
                Body = Trim(r.Reason, 400),
                Link = thread != null
                    ? $"/chats?channel={Uri.EscapeDataString(thread.Channel)}&key={Uri.EscapeDataString(thread.Key)}"
                    : "",
                CreatedAt = DateTime.UtcNow,
            });
    }

    // ---- metering ---------------------------------------------------------------------------

    /// <summary>Records every model round so usage is metered PER ROUND.</summary>
    private sealed class MeteredLlm : ILlmClient
    {
        private readonly ILlmClient _inner;
        public MeteredLlm(ILlmClient inner) => _inner = inner;
        public int Attempt { get; set; }
        public List<(int Attempt, LlmChatResult Result)> Calls { get; } = new();
        public bool IsConfigured => _inner.IsConfigured;

        public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
            CancellationToken ct = default) => _inner.CompleteAsync(system, messages, ct);

        public async Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
            CancellationToken ct = default)
        {
            var r = await _inner.ChatAsync(system, messages, tools, model, temperature, ct);
            Calls.Add((Attempt, r));
            return r;
        }
    }

    /// <summary>Sandbox stand-in that "writes" a fixed text (follow-ups without a model).</summary>
    private sealed class FixedTextLlm : ILlmClient
    {
        private readonly string _text;
        public FixedTextLlm(string text) => _text = text;
        public bool IsConfigured => false;

        public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
            CancellationToken ct = default) => Task.FromResult(new LlmResult(_text, 0, 0, "sandbox", "sandbox"));

        public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
            CancellationToken ct = default) =>
            Task.FromResult(new LlmChatResult(_text, Array.Empty<LlmToolCall>(), 0, 0, "sandbox", "sandbox"));
    }

    private void RecordUsage(MeteredLlm metered, Spec spec, AiReplyResult result)
    {
        try
        {
            var i = 0;
            foreach (var (attempt, r) in metered.Calls)
            {
                i++;
                var detail = new Dictionary<string, object?>
                {
                    ["round"] = i,
                    ["attempt"] = attempt,
                    ["tools"] = r.ToolCalls.Select(t => t.Name).ToList(),
                };
                if (i == metered.Calls.Count)
                {
                    detail["action"] = result.Action;
                    detail["retries"] = result.Retries;
                    detail["checks"] = result.Checks.Select(c => new Dictionary<string, object?>
                    {
                        ["name"] = c.Name, ["ok"] = c.Ok, ["attempt"] = c.Attempt,
                    }).ToList();
                    if (result.Reason.Length > 0) detail["reason"] = Trim(result.Reason, 300);
                }
                _db.LlmUsage.Add(new LlmUsage
                {
                    Feature = spec.Feature, Provider = r.Provider, Model = r.Model,
                    Channel = spec.Channel, ContactId = spec.Contact?.Id, Ref = spec.Ref,
                    PromptTokens = r.PromptTokens, CompletionTokens = r.CompletionTokens,
                    Detail = detail, CreatedAt = DateTime.UtcNow,
                });
            }
            _db.SaveChanges();
        }
        catch { /* ledger failure never breaks the reply path */ }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private async Task<Contact?> ContactAsync(int? contactId, CancellationToken ct) =>
        contactId is int cid ? await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;

    private List<Message> History(int threadId, int take)
    {
        var history = _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == threadId && !m.Private)
            .OrderByDescending(m => m.Id).Take(take).ToList();
        history.Reverse();
        return history;
    }

    private static string Cfg(JsonElement cfg, string key)
    {
        if (cfg.ValueKind != JsonValueKind.Object || !cfg.TryGetProperty(key, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.ToString(),
        };
    }

    private static List<string> CfgList(JsonElement cfg, string key)
    {
        if (cfg.ValueKind != JsonValueKind.Object || !cfg.TryGetProperty(key, out var v)) return new();
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                .Where(s => s.Length > 0).ToList();
        return v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } one ? new() { one } : new();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string Trim(string? s, int n) => (s ?? "").Length <= n ? s ?? "" : s![..n] + "…";

    /// <summary>Render leaked {{placeholders}} from the CRM (mirrors the Django catalog).</summary>
    public static string RenderPlaceholders(string text, Contact? c)
    {
        if (string.IsNullOrEmpty(text) || c == null) return text;
        return text
            .Replace("{{name}}", c.Name)
            .Replace("{{company}}", c.CompanyName)
            .Replace("{{email}}", c.Email);
    }
}
