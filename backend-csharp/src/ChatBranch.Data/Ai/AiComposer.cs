using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>What the AI wrote plus everything a reviewer needs to judge it.</summary>
/// <remarks>
/// <see cref="Text"/> is what would be sent: the reply itself, or — when the
/// assistant's checks blocked it or asked for a person — the last draft it
/// wrote, so the monitor can show what was held back. Meta keys the gate reads
/// (all optional, the shape of <see cref="AiReplyResult.ToMeta"/> plus extras):
/// <c>answering</c>, <c>action</c> (send | handover | block), <c>reason</c>,
/// <c>tools</c> ([{name, ok, result}]), <c>checks</c> ([{name, ok, reason,
/// attempt}]), <c>retries</c>, <c>handover</c>, <c>awaiting_reply</c>,
/// <c>model</c>, <c>provider</c>, <c>prompt_tokens</c>, <c>completion_tokens</c>.
/// </remarks>
public record AiComposition(string Text, Dictionary<string, object?> Meta)
{
    /// <summary>The assistant refused to send it (handover / block) — a person must look.</summary>
    public bool Refused => AiGate.Str(Meta, "action", AiActions.Send) is AiActions.Handover or AiActions.Block;
}

/// <summary>
/// The ONE seam between the AI-ops layer (monitor, debounce, follow-ups) and
/// the assistant that writes the words. Generation runs with
/// <c>applyOutcome: false</c> — no handover side effects inside the assistant —
/// so the AI gate decides what a failed check or a handover request means
/// (held in the monitor for a person, never sent blind).
/// </summary>
public class AiComposer
{
    private readonly AppDbContext _db;
    private readonly AssistantService _assistant;

    public AiComposer(AppDbContext db, AssistantService assistant, ILlmClient? llm = null)
    {
        _db = db;
        _assistant = assistant;
        _ = llm; // kept for call-site compatibility; the assistant owns the model
    }

    /// <summary>Write the reply to the thread's latest customer messages.</summary>
    public async Task<AiComposition> ComposeReplyAsync(Thread thread, CancellationToken ct = default)
    {
        var r = await _assistant.GenerateReplyAsync(thread, applyOutcome: false, ct);
        return Map(r, BaseMeta(thread));
    }

    /// <summary>Write a follow-up for a conversation that went quiet (pure; sandbox has a fixed nudge).</summary>
    public async Task<AiComposition> ComposeFollowUpAsync(Thread thread, int attempt, string guidance,
        CancellationToken ct = default)
    {
        var r = await _assistant.GenerateFollowUpAsync(thread, attempt, guidance, ct);
        var meta = BaseMeta(thread);
        meta["attempt"] = attempt;
        return Map(r, meta);
    }

    private static AiComposition Map(AiReplyResult r, Dictionary<string, object?> meta)
    {
        foreach (var (k, v) in r.ToMeta()) meta[k] = v;
        var text = r.ShouldSend ? r.Text
            : r.Action is AiActions.Handover or AiActions.Block ? r.Draft
            : "";
        return new AiComposition((text ?? "").Trim(), meta);
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
        return new Dictionary<string, object?> { ["answering"] = answering };
    }
}
