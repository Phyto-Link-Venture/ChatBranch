using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// AI case summaries — the "what happened on this conversation so far" brief
/// shown on the chat info panel and the contact page (aicore/memory.py's
/// summarize path). Ported here because the C# API served neither endpoint, so
/// the chat panel 404'd on every thread and the contact page's "Generate
/// summary" button did nothing.
///
/// Persisted as <see cref="ConversationSummary"/> rows keyed by contact +
/// thread ref, so a summary survives a reload and is only regenerated on
/// demand. With no LLM credentials configured the summary is HEURISTIC (built
/// from the messages themselves) and says so — never a fabricated brief.
/// </summary>
public class SummaryService
{
    private readonly AppDbContext _db;
    private readonly ILlmClient _llm;
    private readonly AssistantService _assistant;

    public SummaryService(AppDbContext db, ILlmClient llm, AssistantService assistant)
    {
        _db = db;
        _llm = llm;
        _assistant = assistant;
    }

    public record Brief(string Summary, bool Heuristic, int MessageCount, DateTime AsOf, DateTime GeneratedAt);

    /// <summary>The stored brief for a thread, or null when none exists yet.</summary>
    public Brief? Stored(Thread thread)
    {
        var row = _db.ConversationSummaries.AsNoTracking()
            .Where(s => s.Ref == RefFor(thread))
            .OrderByDescending(s => s.Id)
            .FirstOrDefault();
        if (row == null) return null;
        var (count, asOf) = Extent(thread);
        return new Brief(row.Summary, row.Sentiment == "heuristic", count, asOf, row.CreatedAt);
    }

    /// <summary>
    /// A summary is stale once messages have arrived since it was generated —
    /// the panel labels it rather than silently re-running the model.
    /// </summary>
    public bool IsStale(Thread thread, Brief? brief)
    {
        if (brief == null) return false;
        var (_, asOf) = Extent(thread);
        return asOf > brief.GeneratedAt;
    }

    public (int count, DateTime asOf) Extent(Thread thread)
    {
        var count = _db.Messages.Count(m => m.ThreadId == thread.Id && !m.Private);
        var asOf = _db.Messages.Where(m => m.ThreadId == thread.Id && !m.Private)
            .Max(m => (DateTime?)m.CreatedAt) ?? thread.UpdatedAt;
        return (count, asOf);
    }

    /// <summary>Generate (and store) a fresh brief for this thread.</summary>
    public async Task<Brief?> GenerateAsync(Thread thread, CancellationToken ct = default)
    {
        var history = _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && !m.Private)
            .OrderByDescending(m => m.Id).Take(40).ToList();
        history.Reverse();
        if (history.Count == 0) return null;

        var contact = thread.ContactId is int cid
            ? await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct)
            : null;
        var transcript = string.Join("\n", history.Select(m =>
            $"{(m.Direction == MessageDirection.In ? "Customer" : "Us")}: {Trim(m.Text, 300)}"));

        var (text, heuristic) = await SummariseAsync(transcript, contact, thread.Channel, ct);
        var (count, asOf) = Extent(thread);
        var now = DateTime.UtcNow;

        // A summary row is contact memory (crm_conversationsummary.contact_id is
        // a required FK), so a conversation with no linked contact gets its
        // brief back without storing one — writing ContactId 0 used to fail the
        // FK and turn the whole request into a 500.
        if (contact == null)
            return new Brief(text, heuristic, count, asOf, now);

        // one row per thread — replace rather than pile up revisions
        var existing = _db.ConversationSummaries
            .Where(s => s.Ref == RefFor(thread)).ToList();
        _db.ConversationSummaries.RemoveRange(existing);
        _db.ConversationSummaries.Add(new ConversationSummary
        {
            ContactId = contact.Id,
            Source = "chat",
            Channel = thread.Channel,
            Ref = RefFor(thread),
            Summary = text,
            Sentiment = heuristic ? "heuristic" : "",
            CreatedAt = now,
        });
        // Durable memory: the contact's rolling profile is what the AI reads on
        // the next conversation (memory.build_context).
        var tracked = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contact.Id, ct);
        if (tracked != null) { tracked.AiProfileSummary = Trim(text, 600); tracked.UpdatedAt = now; }
        await _db.SaveChangesAsync(ct);
        return new Brief(text, heuristic, count, asOf, now);
    }

    /// <summary>
    /// Free-form brief for the contact page (name/phone + a record blob rather
    /// than one thread) — every interaction the CRM knows about, condensed.
    /// </summary>
    public async Task<(string summary, bool heuristic, int eventCount)> ForContactAsync(
        Contact contact, CancellationToken ct = default)
    {
        var messages = _db.Messages.AsNoTracking().Include(m => m.Thread)
            .Where(m => m.Thread != null && m.Thread.ContactId == contact.Id && !m.Private)
            .OrderByDescending(m => m.Id).Take(60).ToList();
        messages.Reverse();
        var calls = _db.CallRecords.AsNoTracking().Count(c => c.ContactId == contact.Id);
        var eventCount = messages.Count + calls;
        if (eventCount == 0)
            return ("No interactions recorded yet for this contact.", true, 0);

        var transcript = string.Join("\n", messages.Select(m =>
            $"{(m.Direction == MessageDirection.In ? "Customer" : "Us")} [{m.Thread?.Channel ?? ""}]: {Trim(m.Text, 260)}"));
        if (calls > 0) transcript += $"\n({calls} voice call(s) on record.)";
        var (text, heuristic) = await SummariseAsync(transcript, contact, "", ct);
        return (text, heuristic, eventCount);
    }

    private async Task<(string text, bool heuristic)> SummariseAsync(string transcript,
        Contact? contact, string channel, CancellationToken ct)
    {
        if (!_llm.IsConfigured) return (Heuristic(transcript, contact), true);
        try
        {
            var who = contact != null ? $"{contact.Name} ({contact.Phone})" : "this customer";
            var system =
                "You write a short internal case brief for a support agent picking up a conversation. "
                + "Three sentences at most: what the customer wants, where it stands, and the single best "
                + "next action. Plain factual prose, no greeting, no bullet points, no markdown. "
                + "Never invent facts that are not in the transcript.";
            var result = await _llm.CompleteAsync(system, new[]
            {
                ("user", $"Conversation with {who}{(channel.Length > 0 ? $" on {channel}" : "")}:\n\n{transcript}"),
            }, ct);
            var text = (result.Text ?? "").Trim();
            if (text.Length == 0) return (Heuristic(transcript, contact), true);
            _db.LlmUsage.Add(new LlmUsage
            {
                Feature = "summary", Provider = result.Provider, Model = result.Model,
                Channel = channel, ContactId = contact?.Id, Ref = "summary", UserName = AiQuota.CurrentUser(),
                PromptTokens = result.PromptTokens, CompletionTokens = result.CompletionTokens, CostUsd = result.CostUsd,
                CreatedAt = DateTime.UtcNow,
            });
            return (AssistantService.RenderPlaceholders(text, contact), false);
        }
        catch
        {
            // provider hiccup — a heuristic brief beats an error banner
            return (Heuristic(transcript, contact), true);
        }
    }

    /// <summary>
    /// No-LLM fallback: state plainly what the transcript contains rather than
    /// pretending to have understood it. Flagged `heuristic` so the UI labels it.
    /// </summary>
    private static string Heuristic(string transcript, Contact? contact)
    {
        var lines = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var fromCustomer = lines.Where(l => l.StartsWith("Customer")).ToList();
        var last = fromCustomer.LastOrDefault() ?? lines.LastOrDefault() ?? "";
        var name = string.IsNullOrWhiteSpace(contact?.Name) ? "the customer" : contact!.Name;
        var opener = lines.Length == 1
            ? $"1 message exchanged with {name}."
            : $"{lines.Length} messages exchanged with {name}.";
        if (last.Length == 0) return opener;
        var idx = last.IndexOf(':');
        var lastText = idx >= 0 && idx + 1 < last.Length ? last[(idx + 1)..].Trim() : last;
        return $"{opener} Their most recent message: “{Trim(lastText, 200)}”. "
               + "No AI provider is configured, so this is a plain recap rather than an analysis.";
    }

    private static string RefFor(Thread thread) => $"{thread.Channel}:{thread.Key}";

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
