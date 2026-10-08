using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// The channel-agnostic AI (port of aicore/assistant.py): assemble the system
/// prompt from persona hard-rules + CRM enrichment + knowledge + skills, then
/// generate a reply through the pluggable <see cref="ILlmClient"/>. whatsapp/ai
/// and every other channel are thin delegates to this, so behaviour is
/// identical everywhere. Every path is exception-safe.
/// </summary>
public class AssistantService
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly PersonaService _persona;
    private readonly KnowledgeService _knowledge;
    private readonly ILlmClient _llm;

    public AssistantService(AppDbContext db, ScopingService scoping, PersonaService persona,
        KnowledgeService knowledge, ILlmClient llm)
    {
        _db = db;
        _scoping = scoping;
        _persona = persona;
        _knowledge = knowledge;
        _llm = llm;
    }

    /// <summary>Assemble the full system prompt for a given entity/channel/contact.</summary>
    public string BuildSystemPrompt(int? entityId, string? channel, Contact? contact, string? query)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(_persona.SystemPreamble(entityId));

        var skills = SkillsBlock(entityId);
        if (skills.Length > 0) sb.AppendLine().AppendLine(skills);

        var knowledge = _knowledge.BuildContext(query, entityId, channel);
        if (knowledge.Length > 0) sb.AppendLine().AppendLine(knowledge);

        if (contact != null) sb.AppendLine().AppendLine(ContactBlock(contact));

        sb.AppendLine().AppendLine($"Today's date is {DateTime.UtcNow:yyyy-MM-dd}.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Generate a reply for a thread's latest inbound. Records usage. Returns the
    /// reply text (sandbox echo when no LLM is configured).
    /// </summary>
    public async Task<string> GenerateReplyAsync(Thread thread, CancellationToken ct = default)
    {
        try
        {
            var contact = thread.ContactId is int cid
                ? await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct)
                : null;

            var history = _db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == thread.Id && !m.Private)
                .OrderByDescending(m => m.Id).Take(20).ToList();
            history.Reverse();
            var lastUser = history.LastOrDefault(m => m.Direction == MessageDirection.In)?.Text ?? "";

            var system = BuildSystemPrompt(thread.EntityId, thread.Channel, contact, lastUser);
            var messages = history.Select(m =>
                (role: m.Direction == MessageDirection.In ? "user" : "assistant", content: m.Text))
                .ToList();

            var result = await _llm.CompleteAsync(system, messages, ct);
            RecordUsage(result, thread, contact);
            return RenderPlaceholders(result.Text, contact);
        }
        catch
        {
            return ""; // never break a send/webhook path
        }
    }

    public string SkillsBlock(int? entityId)
    {
        var skills = _scoping.ScopeInherited(_db.AgentSkills.AsNoTracking(), entityId)
            .OrderBy(s => s.Position).ToList();
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

    private void RecordUsage(LlmResult r, Thread thread, Contact? contact)
    {
        try
        {
            _db.LlmUsage.Add(new LlmUsage
            {
                Feature = "chat_reply", Provider = r.Provider, Model = r.Model,
                Channel = thread.Channel, ContactId = contact?.Id, Ref = thread.Key,
                PromptTokens = r.PromptTokens, CompletionTokens = r.CompletionTokens,
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
        catch { /* ledger failure never breaks the reply path */ }
    }

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
