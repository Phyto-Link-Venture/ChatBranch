using System.Text.Json;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Sidecars;

/// <summary>What the dial site chose for one call (everything optional but the number).</summary>
public sealed record VoiceCallOptions(
    string Phone,
    string? Name = null,
    string? Language = null,
    string? VoiceProvider = null,
    string? Voice = null,
    string? LlmProvider = null,
    string? SttProvider = null,
    string? Script = null,
    string? Objective = null,
    int? EntityId = null);

/// <summary>
/// Builds the agent dispatch metadata — the JSON contract documented key by key
/// in backend/voice_agent/src/metadata.ts. The agent never touches the
/// database, so everything it needs about the contact, the record the call is
/// about and the company's knowledge/skills/memory is composed here.
/// </summary>
public static class VoiceDispatch
{
    /// <summary>Durable contact memory budget (chars), the same bound the chat memory block keeps.</summary>
    private const int MemoryBudget = 1500;

    public static async Task<Dictionary<string, object?>> BuildAsync(AppDbContext db, KnowledgeService knowledge,
        AssistantService assistant, ScopingService scoping, string dataDir, Contact? contact, VoiceCallOptions o,
        CancellationToken ct = default)
    {
        var md = new Dictionary<string, object?> { ["phone"] = o.Phone };
        void Put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) md[key] = value.Trim();
        }

        var entityId = o.EntityId;
        if (contact != null)
        {
            md["contact_id"] = contact.Id;
            entityId ??= await db.ContactEntities.AsNoTracking()
                .Where(l => l.ContactId == contact.Id).OrderBy(l => l.Id)
                .Select(l => (int?)l.EntityId).FirstOrDefaultAsync(ct);
            if (contact.Tags.Count > 0) md["tags"] = contact.Tags;
            AddRecord(md, await PrimaryRecordAsync(db, contact.Id, ct));
            Put("memory", MemoryBlock(contact));
        }
        Put("contact_name", o.Name ?? contact?.Name);
        // "calling on behalf of …": the contact's company of record, else the business name.
        Put("company_name", contact?.CompanyName is { Length: > 0 } company ? company : scoping.SettingValue("business_name"));
        Put("language", o.Language ?? contact?.Language);
        Put("objective", o.Objective);
        Put("knowledge", knowledge.BuildContext(null, entityId, "voice"));
        Put("skills", assistant.SkillsBlock(entityId));

        var provider = (o.VoiceProvider ?? "").Trim().ToLowerInvariant();
        var voice = o.Voice;
        // no voice picked for this call → the organization's voice from Voice & calls (with its provider)
        if (string.IsNullOrWhiteSpace(voice) && provider.Length == 0
            && scoping.SettingValue("default_voice") is { Length: > 0 } saved)
        {
            voice = saved;
            provider = scoping.SettingValue("default_voice_provider").Trim().ToLowerInvariant();
        }
        Put("voice_provider", provider);
        Put("voice", ResolveVoice(dataDir, provider, voice));
        Put("llm_provider", o.LlmProvider);
        Put("stt_provider", o.SttProvider);
        Put("script", o.Script);
        return md;
    }

    /// <summary>The record a call is about: the contact's first open record, else its first.</summary>
    private static async Task<Record?> PrimaryRecordAsync(AppDbContext db, int contactId, CancellationToken ct)
    {
        var records = await db.Records.AsNoTracking().Where(r => r.ContactId == contactId)
            .OrderBy(r => r.Id).ToListAsync(ct);
        return records.FirstOrDefault(r => !Flows.FlowEngine.ClosedStatuses.Contains(r.Status.ToLowerInvariant()))
               ?? records.FirstOrDefault();
    }

    private static void AddRecord(Dictionary<string, object?> md, Record? r)
    {
        if (r == null) return;
        md["record_type"] = r.Type;
        md["record_id"] = r.ExternalId.Length > 0 ? r.ExternalId : r.Id.ToString();
        md["record_title"] = r.Title;
        md["record_status"] = r.Status;
        if (r.Amount is decimal amount) md["record_amount"] = $"{r.Currency} {amount:N2}";
        md["record_currency"] = r.Currency;
        if (r.DueDate is DateOnly due) md["record_due_date"] = due.ToString("yyyy-MM-dd");
        if (r.Data.Count > 0) md["record_data"] = r.Data;
    }

    /// <summary>What earlier conversations taught us about the contact, as a short block.</summary>
    private static string MemoryBlock(Contact c)
    {
        var lines = new List<string>();
        if (c.AiProfileSummary.Length > 0) lines.Add(c.AiProfileSummary);
        if (c.PreferredContactTime.Length > 0) lines.Add($"Best time to reach them: {c.PreferredContactTime}.");
        if (c.CommunicationStyle.Length > 0) lines.Add($"Communication style: {c.CommunicationStyle}.");
        if (c.SentimentTrend.Length > 0) lines.Add($"Sentiment trend: {c.SentimentTrend}.");
        var block = string.Join("\n", lines);
        return block.Length <= MemoryBudget ? block : block[..MemoryBudget];
    }

    /// <summary>
    /// The dashboard lists ElevenLabs clones by its own id; the agent needs the
    /// real ElevenLabs voice_id (voices.json <c>elevenVoiceId</c>). Anything else
    /// passes through unchanged.
    /// </summary>
    public static string? ResolveVoice(string dataDir, string provider, string? voice)
    {
        if (provider != "elevenlabs" || string.IsNullOrWhiteSpace(voice)) return voice;
        try
        {
            var path = Path.Combine(dataDir, "voices.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("voices", out var voices) && voices.ValueKind == JsonValueKind.Array)
                    foreach (var v in voices.EnumerateArray())
                        if (v.TryGetProperty("id", out var id) && id.GetString() == voice
                            && v.TryGetProperty("elevenVoiceId", out var el) && el.GetString() is { Length: > 0 } real)
                            return real;
            }
        }
        catch { /* unreadable registry → the id as given */ }
        return voice;
    }
}
