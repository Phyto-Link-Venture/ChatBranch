using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>What the AI wrote plus everything a reviewer needs to judge it.</summary>
/// <remarks>
/// Meta keys the gate understands (all optional):
/// <c>answering</c> (customer messages being answered), <c>tools</c>
/// ([{name, ok, result}]), <c>checks</c> ([{name, passed, detail}]),
/// <c>retries</c>, <c>handover</c> (bool), <c>awaiting_reply</c> (bool),
/// <c>model</c>, <c>provider</c>, <c>prompt_tokens</c>, <c>completion_tokens</c>.
/// </remarks>
public record AiComposition(string Text, Dictionary<string, object?> Meta);

/// <summary>
/// The ONE seam between the AI-ops layer (monitor, debounce, follow-ups) and
/// the assistant that writes the words. Everything else calls
/// <see cref="ComposeReplyAsync"/> / <see cref="ComposeFollowUpAsync"/>, so when
/// <c>AssistantService.GenerateReplyAsync</c> starts returning a richer result
/// (tools called, checks run, retries, handover, model/tokens) only the mapping
/// in this file changes.
/// </summary>
public class AiComposer
{
    private readonly AppDbContext _db;
    private readonly AssistantService _assistant;
    private readonly ILlmClient? _llm;

    public AiComposer(AppDbContext db, AssistantService assistant, ILlmClient? llm = null)
    {
        _db = db;
        _assistant = assistant;
        _llm = llm;
    }

    /// <summary>Write the reply to the thread's latest customer messages.</summary>
    public async Task<AiComposition> ComposeReplyAsync(Thread thread, CancellationToken ct = default)
    {
        var usageBefore = LastUsageId();
        // INTEGRATION SEAM: when GenerateReplyAsync returns an AiReplyResult, read
        // .Text here and copy its metadata (tools, checks, retries, handover,
        // model, tokens, awaiting_reply) into `meta` under the keys documented on
        // AiComposition — the gate's review_flagged rules already read them.
        var text = await _assistant.GenerateReplyAsync(thread, ct);
        var meta = BaseMeta(thread);
        AttachUsage(meta, thread, usageBefore, "chat_reply");
        return new AiComposition(text ?? "", meta);
    }

    /// <summary>
    /// Write a follow-up for a conversation that went quiet. Private stand-in for
    /// <c>AssistantService.GenerateFollowUpAsync(thread, attempt, guidance)</c>
    /// (same shape); swap the body for that call once it lands. Sandbox (no LLM
    /// configured) falls back to a fixed, friendly nudge.
    /// </summary>
    public async Task<AiComposition> ComposeFollowUpAsync(Thread thread, int attempt, string guidance,
        CancellationToken ct = default)
    {
        var meta = BaseMeta(thread);
        meta["attempt"] = attempt;
        var contact = thread.ContactId is int cid
            ? await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct)
            : null;
        var first = FirstName(contact?.Name ?? thread.Name);

        if (_llm == null || !_llm.IsConfigured)
        {
            meta["model"] = "sandbox";
            return new AiComposition(SandboxFollowUp(first, attempt), meta);
        }

        try
        {
            var history = _db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == thread.Id && !m.Private)
                .OrderByDescending(m => m.Id).Take(20).ToList();
            history.Reverse();
            var lastAi = history.LastOrDefault(m => m.Direction == MessageDirection.Out)?.Text ?? "";
            var system = _assistant.BuildSystemPrompt(thread.EntityId, thread.Channel, contact, lastAi)
                + "\n\nFOLLOW-UP TASK:\nThe customer has not replied to your last message. Write ONE short, "
                + "friendly follow-up (one or two sentences) that gently re-opens the conversation. Do not "
                + "repeat your previous message, do not pressure, do not invent offers or facts. "
                + (attempt > 1 ? "This is a later follow-up: keep it lighter and make clear they can reply any time. " : "")
                + (string.IsNullOrWhiteSpace(guidance) ? "" : $"Operator guidance: {guidance.Trim()}");
            var messages = history.Select(m =>
                (role: m.Direction == MessageDirection.In ? "user" : "assistant", content: m.Text)).ToList();
            messages.Add((role: "user", content: "(no reply from the customer yet — write the follow-up)"));
            var r = await _llm.CompleteAsync(system, messages, ct);
            meta["model"] = r.Model;
            meta["provider"] = r.Provider;
            meta["prompt_tokens"] = r.PromptTokens;
            meta["completion_tokens"] = r.CompletionTokens;
            RecordUsage(r, thread, contact);
            var text = AssistantService.RenderPlaceholders(r.Text ?? "", contact).Trim();
            return new AiComposition(text.Length > 0 ? text : SandboxFollowUp(first, attempt), meta);
        }
        catch
        {
            return new AiComposition(SandboxFollowUp(first, attempt), meta);
        }
    }

    public static string SandboxFollowUp(string firstName, int attempt)
    {
        var hi = string.IsNullOrWhiteSpace(firstName) ? "Hi" : $"Hi {firstName}";
        return attempt <= 1
            ? $"{hi}, just checking in — is there anything else I can help you with?"
            : $"{hi}, I'll leave this with you for now. Reply here any time and we'll pick it up.";
    }

    /// <summary>The customer messages the reply answers: everything since our last outbound.</summary>
    private Dictionary<string, object?> BaseMeta(Thread thread)
    {
        var recent = _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && !m.Private)
            .OrderByDescending(m => m.Id).Take(12).ToList();
        var answering = new List<object?>();
        foreach (var m in recent)
        {
            if (m.Direction == MessageDirection.Out) break;
            answering.Insert(0, new Dictionary<string, object?>
            {
                ["id"] = m.Id, ["text"] = m.Text, ["at"] = m.CreatedAt,
            });
        }
        return new Dictionary<string, object?>
        {
            ["answering"] = answering,
            ["tools"] = new List<object?>(),
            ["checks"] = new List<object?>(),
            ["retries"] = 0,
        };
    }

    private int LastUsageId()
    {
        try { return _db.LlmUsage.AsNoTracking().Max(u => (int?)u.Id) ?? 0; }
        catch { return 0; }
    }

    /// <summary>Model + tokens from the usage row the assistant just wrote for this thread.</summary>
    private void AttachUsage(Dictionary<string, object?> meta, Thread thread, int afterId, string feature)
    {
        try
        {
            var row = _db.LlmUsage.AsNoTracking()
                .Where(u => u.Id > afterId && u.Feature == feature && u.Ref == thread.Key)
                .OrderByDescending(u => u.Id).FirstOrDefault();
            if (row == null) return;
            meta["model"] = row.Model;
            meta["provider"] = row.Provider;
            meta["prompt_tokens"] = row.PromptTokens;
            meta["completion_tokens"] = row.CompletionTokens;
        }
        catch { /* metadata is best-effort */ }
    }

    private void RecordUsage(LlmResult r, Thread thread, Contact? contact)
    {
        try
        {
            _db.LlmUsage.Add(new LlmUsage
            {
                Feature = "follow_up", Provider = r.Provider, Model = r.Model,
                Channel = thread.Channel, ContactId = contact?.Id, Ref = thread.Key,
                PromptTokens = r.PromptTokens, CompletionTokens = r.CompletionTokens,
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
        catch { /* ledger failure never breaks the follow-up */ }
    }

    private static string FirstName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n.Any(char.IsDigit)) return "";   // a phone-number "name" reads badly
        return n.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }
}
