using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Sidecars;

/// <summary>
/// Folds what the voice agent wrote on disk into the database: every finished
/// record in calls.json becomes a <see cref="CallRecord"/> (call logs, the
/// summary endpoint), and the call's token counts — the final live write's
/// <c>llm_usage</c> — become <see cref="LlmUsage"/> rows (feature voice_call,
/// plus the end-of-call summary). Idempotent: keyed by call id, so polling it
/// is safe.
/// </summary>
public static class VoiceCallSync
{
    public static async Task<int> SyncAsync(AppDbContext db, VoiceAgentBridge bridge, CancellationToken ct = default)
    {
        var records = bridge.CallRecords();
        if (records.Count == 0) return 0;
        var ids = records.Select(r => Str(r, "call_id")).Where(id => id.Length > 0).Distinct().ToList();
        var known = (await db.CallRecords.AsNoTracking().Where(c => ids.Contains(c.CallId))
            .Select(c => c.CallId).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var r in records)
        {
            var callId = Str(r, "call_id");
            if (callId.Length == 0 || !known.Add(callId)) continue;
            var contact = r.TryGetProperty("contact", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var contactId = await ExistingContactAsync(db, contact, ct);
            var now = DateTime.UtcNow;
            db.CallRecords.Add(new CallRecord
            {
                CallId = callId,
                ContactId = contactId,
                Phone = Str(contact, "phone"),
                Name = Str(contact, "contact_name"),
                StartedAt = Str(r, "started_at"),
                EndedAt = Str(r, "ended_at"),
                Transcript = Str(r, "transcript"),
                Summary = Str(r, "summary"),
                Outcome = Str(r, "outcome") is { Length: > 0 } outcome ? outcome : "unclear",
                FollowupDate = Str(r, "followup_date") is { Length: > 0 } date ? date : null,
                NeedsHuman = r.TryGetProperty("needs_human", out var nh) && nh.ValueKind == JsonValueKind.True,
            });
            AddUsage(db, bridge.LiveTranscript(callId), callId, contactId, now);
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>The record's contact id, when that contact still exists (the FK must hold).</summary>
    private static async Task<int?> ExistingContactAsync(AppDbContext db, JsonElement contact, CancellationToken ct)
    {
        if (!int.TryParse(Str(contact, "contact_id"), out var id)) return null;
        return await db.Contacts.AsNoTracking().AnyAsync(x => x.Id == id, ct) ? id : null;
    }

    private static void AddUsage(AppDbContext db, JsonElement live, string callId, int? contactId, DateTime now)
    {
        if (live.ValueKind != JsonValueKind.Object || !live.TryGetProperty("llm_usage", out var u)
            || u.ValueKind != JsonValueKind.Object) return;
        var call = new LlmUsage
        {
            Feature = "voice_call", Provider = Str(u, "provider"), Model = Str(u, "model"), Channel = "voice",
            ContactId = contactId, Ref = callId, CreatedAt = now,
            PromptTokens = Int(u, "prompt"), CompletionTokens = Int(u, "completion"),
            AudioPromptTokens = Int(u, "audio_prompt"), AudioCompletionTokens = Int(u, "audio_completion"),
        };
        if (call.PromptTokens + call.CompletionTokens + call.AudioPromptTokens + call.AudioCompletionTokens > 0)
            db.LlmUsage.Add(call);
        if (u.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object)
            db.LlmUsage.Add(new LlmUsage
            {
                Feature = "summary", Model = Str(s, "model"), Channel = "voice", ContactId = contactId,
                Ref = callId, CreatedAt = now, PromptTokens = Int(s, "prompt"), CompletionTokens = Int(s, "completion"),
            });
    }

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.ToString(),
                _ => "",
            }
            : "";

    private static int Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
