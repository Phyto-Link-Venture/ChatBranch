using System.Text;
using System.Text.Json;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// STOP / START keyword handling on EVERY inbound channel (port of the
/// whatsapp + messaging webhooks' opt-out step). A customer message that IS an
/// opt-out keyword — the whole message, case- and punctuation-insensitive, so
/// "stop." counts and "don't stop" does not — sets <see cref="Thread.OptedOut"/>:
/// from then on the AI, templates, flow and queue sends are refused on that
/// conversation (a manual send needs <c>{"force": true}</c>), queued jobs for it
/// are cancelled, a private system note records why, and the change is audited.
/// Nothing is sent back. An opt-in keyword (START / UNSTOP / MULA) reverses it.
///
/// Built-in keywords: STOP, UNSUBSCRIBE, BERHENTI (out) and START, UNSTOP, MULA
/// (in). Organization-wide Setting rows <c>opt_out_keywords</c> /
/// <c>opt_in_keywords</c> (a JSON list or a comma/newline-separated string) ADD
/// to them — compliance words are never removable. The contact model has no
/// opt-out column of its own, so the flag lives on the conversation (channel ×
/// address), which is also what every send path checks.
/// </summary>
public static class OptOut
{
    public static readonly string[] DefaultOutWords = { "STOP", "UNSUBSCRIBE", "BERHENTI" };
    public static readonly string[] DefaultInWords = { "START", "UNSTOP", "MULA" };
    public const string OutKey = "opt_out_keywords";
    public const string InKey = "opt_in_keywords";

    public const string OptedOut = "opted_out";
    public const string OptedIn = "opted_in";

    /// <summary>Upper-cased, letters/digits only, single-spaced: "  Stop! " → "STOP".</summary>
    public static string Normalize(string? text)
    {
        var sb = new StringBuilder();
        var space = false;
        foreach (var ch in (text ?? "").Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (space && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(ch));
                space = false;
            }
            else if (char.IsWhiteSpace(ch)) space = true;
            // punctuation and emoji are dropped without splitting a word
        }
        return sb.ToString();
    }

    /// <summary>A Setting's keyword list: a JSON array or a comma/semicolon/newline string, normalized.</summary>
    public static List<string> ParseList(string? raw)
    {
        var outv = new List<string>();
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return outv;
        IEnumerable<string> parts;
        if (raw.StartsWith("["))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                parts = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString() ?? "").ToList()
                    : new List<string>();
            }
            catch { parts = raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries); }
        }
        else parts = raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            var n = Normalize(p);
            if (n.Length > 0 && !outv.Contains(n)) outv.Add(n);
        }
        return outv;
    }

    public static IReadOnlyCollection<string> OutWords(ScopingService scoping) =>
        DefaultOutWords.Concat(ParseList(scoping.SettingValue(OutKey))).Distinct().ToList();

    public static IReadOnlyCollection<string> InWords(ScopingService scoping) =>
        DefaultInWords.Concat(ParseList(scoping.SettingValue(InKey))).Distinct().ToList();

    /// <summary>"out" / "in" when the message IS a keyword, else null. Opt-out wins a tie.</summary>
    public static string? Classify(ScopingService scoping, string? text)
    {
        var n = Normalize(text);
        if (n.Length == 0 || n.Length > 40) return null;
        if (OutWords(scoping).Contains(n)) return "out";
        if (InWords(scoping).Contains(n)) return "in";
        return null;
    }

    /// <summary>
    /// Apply a customer message to the conversation's opt-out state. Returns
    /// <see cref="OptedOut"/> / <see cref="OptedIn"/> when the state CHANGED,
    /// null otherwise. Never throws — it runs inside webhooks.
    /// </summary>
    public static string? Apply(AppDbContext db, ScopingService scoping, ThreadStore threads, Thread thread,
        string? text, string actor = "customer")
    {
        try
        {
            var kind = Classify(scoping, text);
            if (kind == null) return null;
            var word = Normalize(text);
            var optOut = kind == "out";
            if (thread.OptedOut == optOut) return null;   // already in that state

            thread.OptedOut = optOut;
            thread.UpdatedAt = DateTime.UtcNow;
            db.SaveChanges();

            var cancelled = optOut ? CancelQueuedJobs(db, thread) : 0;
            var note = optOut
                ? $"[Contact opted out ({word}) — AI replies, templates, flow and queued messages to this "
                  + "conversation are blocked until they send START. A manual send needs an explicit override."
                  + (cancelled > 0 ? $" {cancelled} queued message(s) cancelled." : "") + "]"
                : $"[Contact opted back in ({word}) — messages to this conversation are allowed again.]";
            threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System, note,
                status: "note", priv: true);
            Audit.AuditTrail.Write(db, optOut ? "contact.opt_out" : "contact.opt_in",
                $"{thread.Channel}:{thread.Key}",
                $"keyword: {word}" + (cancelled > 0 ? $"; cancelled {cancelled} queued job(s)" : ""),
                actor: actor, source: AuditSources.Webhook, entityId: thread.EntityId);
            return optOut ? OptedOut : OptedIn;
        }
        catch { return null; }
    }

    /// <summary>
    /// Cancel every queued job addressed to this conversation (WhatsApp jobs may
    /// carry the contact's "+60…" phone instead of the digits-only key), except
    /// an operator's explicit <c>force</c> send. Calls are kept: the call placer
    /// refuses opted-out contacts itself and un-parks the flow run.
    /// </summary>
    public static int CancelQueuedJobs(AppDbContext db, Thread thread)
    {
        var digits = PhoneMatch.Digits(thread.Key);
        var whatsapp = thread.Channel == ChatBranch.Domain.Channels.WhatsApp;
        var queued = db.OutboundJobs
            .Where(j => j.Status == JobStatus.Queued && j.Channel == thread.Channel && j.Kind != JobKind.Call)
            .ToList()
            .Where(j => j.ToAddress == thread.Key
                        || (whatsapp && digits.Length > 0 && PhoneMatch.Digits(j.ToAddress) == digits))
            .Where(j => !(j.Payload.TryGetValue("force", out var f) && IsTrue(f)))
            .ToList();
        var now = DateTime.UtcNow;
        foreach (var j in queued)
        {
            j.Status = JobStatus.Cancelled;
            j.LastError = "contact opted out (STOP)";
            j.UpdatedAt = now;
        }
        if (queued.Count > 0) db.SaveChanges();
        return queued.Count;
    }

    private static bool IsTrue(object? v) => v switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
