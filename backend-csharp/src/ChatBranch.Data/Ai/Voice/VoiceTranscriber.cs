using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai.Voice;

/// <summary>
/// Customer voice notes the AI can READ: speech-to-text for inbound audio, stored once
/// on <c>Message.Transcript</c> (<c>crm_message.transcript</c>). Provider = OpenAI
/// (<c>gpt-4o-mini-transcribe</c>, <c>openai_api_key</c>) else ElevenLabs
/// (<c>scribe_v1</c>, <c>elevenlabs_api_key</c>) else none — the message stays "[audio]".
///
/// It runs LAZILY, off the webhook: right before the AI writes a reply
/// (<see cref="AiComposer"/>), for the conversation's latest customer messages whose
/// audio file has been downloaded (media is fetched off the ack) and that carry no
/// transcript yet — bounded by a timeout so a slow provider never stalls a reply.
/// The AI's view (<see cref="Display"/>), the case summary and the message payload
/// (<c>transcript</c>) read the stored words. Metered as <c>LlmUsage</c> feature
/// <c>transcription</c>; skipped when the entity's AI quota is reached.
/// </summary>
public class VoiceTranscriber
{
    public const string Feature = "transcription";
    public const long MaxBytes = 25L * 1024 * 1024; // OpenAI's own upload ceiling
    private const int Lookback = 6;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly MediaStore? _media;
    private readonly SpeechClient _speech;

    public VoiceTranscriber(AppDbContext db, ScopingService scoping, MediaStore? media = null,
        SpeechClient? speech = null, IHttpClientFactory? http = null)
    {
        _db = db;
        _scoping = scoping;
        _media = media;
        _speech = speech ?? new SpeechClient(scoping, http);
    }

    /// <summary>How a message reads to the AI / the summary: "[voice note] &lt;words&gt;" when transcribed.</summary>
    public static string Display(Message m)
    {
        var said = (m.Transcript ?? "").Trim();
        if (said.Length == 0) return m.Text;
        var text = (m.Text ?? "").Trim();
        return text.Length == 0 || text.StartsWith('[') && text.EndsWith(']')
            ? $"[voice note] {said}"
            : $"{text}\n[voice note] {said}";
    }

    /// <summary>
    /// Transcribe the latest customer audio messages of a thread that have a file and no
    /// transcript. Returns how many were transcribed. Never throws.
    /// </summary>
    public async Task<int> TranscribePendingAsync(Thread thread, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            if (_media == null || _speech.SttProvider.Length == 0) return 0;
            var recent = await _db.Messages
                .Where(m => m.ThreadId == thread.Id && !m.Private)
                .OrderByDescending(m => m.Id).Take(Lookback).ToListAsync(ct);
            var todo = recent.Where(m => m.Direction == MessageDirection.In && m.MediaType == "audio"
                                         && m.MediaPath.Length > 0 && m.Transcript.Length == 0)
                .OrderBy(m => m.Id).ToList();
            if (todo.Count == 0) return 0;
            try
            {
                if (AiQuota.EntityBlock(_db, _scoping, thread.EntityId) != null) return 0;
            }
            catch { /* fail open */ }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
            var done = 0;
            foreach (var m in todo)
            {
                if (cts.IsCancellationRequested) break;
                if (await TranscribeAsync(thread, m, cts.Token)) done++;
            }
            if (done > 0)
            {
                var t = await _db.Threads.FirstOrDefaultAsync(x => x.Id == thread.Id, ct);
                if (t != null) { t.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct); }
            }
            return done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return 0; }
    }

    private async Task<bool> TranscribeAsync(Thread thread, Message m, CancellationToken ct)
    {
        var full = _media!.Resolve(thread.Channel, m.MediaPath);
        if (full == null) return false;
        var info = new FileInfo(full);
        if (info.Length == 0 || info.Length > MaxBytes) return false;
        var bytes = await File.ReadAllBytesAsync(full, ct);
        var r = await _speech.TranscribeAsync(bytes, Path.GetFileName(full), MediaStore.MimeFor(full), ct);
        if (!r.Ok) return false;
        try
        {
            _db.LlmUsage.Add(new LlmUsage
            {
                Feature = Feature, Provider = r.Provider, Model = r.Model, Channel = thread.Channel,
                ContactId = thread.ContactId, EntityId = thread.EntityId, Ref = $"message:{m.Id}",
                PromptTokens = r.PromptTokens, CompletionTokens = r.CompletionTokens,
                Detail = new Dictionary<string, object?> { ["bytes"] = bytes.Length, ["message_id"] = m.Id },
                CreatedAt = DateTime.UtcNow,
            });
        }
        catch { /* metering never blocks the words */ }
        // an empty transcript (silence) is stored as a marker so it is not retried every reply
        m.Transcript = r.Text.Length > 0 ? (r.Text.Length > 4000 ? r.Text[..4000] : r.Text) : "…";
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
