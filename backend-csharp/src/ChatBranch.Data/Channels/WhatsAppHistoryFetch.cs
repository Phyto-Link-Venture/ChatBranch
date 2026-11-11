using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The API's half of ON-DEMAND history: asking a QR (Baileys) number for the OLDER
/// messages WhatsApp's replay on pair did not send.
///
/// The replay is shallow by nature — WhatsApp hands a new companion the chat LIST
/// and a message or two each (one measured number: 287 conversations, 386 messages),
/// and <see cref="WhatsAppHistoryImport"/> files whatever arrives. Going deeper means
/// asking per chat, which the gateway does with
/// <c>sock.fetchMessageHistory(count, oldestMsgKey, oldestMsgTimestampMs)</c>.
///
/// ⚠ That is traffic no human generates on an unofficial transport, and a ban is
/// permanent. The run therefore lives in the gateway, where the pacing and the caps
/// are (backend/baileys_service/history.js): operator-triggered only, one run at a
/// time per number, one request in flight, a jittered gap between requests, a hard
/// cap per chat and per run, and a stop the moment WhatsApp stops serving. Nothing
/// in the API may start a run on its own — no pairing hook, no background job.
///
/// This class contributes the two things only the database knows:
///   * <see cref="Seeds"/> — the oldest message we already hold per chat, which is
///     the anchor a request asks from. The gateway keeps its own cursor, but a
///     number paired before this feature existed has none, so the seeds are what
///     make an existing inbox deepenable at all. They can only move a cursor
///     further back (the gateway keeps whichever is older), so re-seeding is safe.
///   * <see cref="Depth"/> — the bounds on "how much", kept identical to the
///     gateway's own clamp so the dashboard cannot ask for something that will be
///     silently reduced.
///
/// What comes back is pushed by the gateway through the SAME
/// <c>POST /api/whatsapp/baileys/history</c> path as the first import, so the
/// filtering (1:1 chats, text and placeholders, no historical media), the window
/// and the dedupe all stay in one place.
/// </summary>
public static class WhatsAppHistoryFetch
{
    /// <summary>Messages per chat a run asks for when the operator names no depth.</summary>
    public const int DefaultDepth = 200;

    /// <summary>
    /// The most one run will ever ask for per chat — the gateway's per-chat cap
    /// (6 requests) times what one request may carry (50 messages). Asking for more
    /// is refused rather than quietly trimmed.
    /// </summary>
    public const int MaxDepth = 300;

    /// <summary>
    /// Chats seeded in one request, most recently active first. A number with
    /// thousands of chats cannot hand them all over in one body, and a run can only
    /// spend 20 requests anyway — the rest keep their turn for the next run.
    /// </summary>
    public const int MaxSeedChats = 500;

    /// <summary>The capability an operator needs: a run talks to WhatsApp.</summary>
    public const string Capability = "channels.manage";

    /// <summary>"How many messages per chat" as asked — refused when out of bounds.</summary>
    public static bool TryDepth(int? asked, out int depth, out string error)
    {
        depth = asked ?? DefaultDepth;
        error = "";
        if (asked == null) return true;
        if (depth < 1 || depth > MaxDepth)
        {
            error = $"depth must be between 1 and {MaxDepth} messages per chat — WhatsApp serves what the paired "
                    + "phone still holds, and one run is deliberately capped.";
            depth = DefaultDepth;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The oldest message we hold per 1:1 WhatsApp chat of this number: the anchors the
    /// gateway asks WhatsApp from. Group threads are skipped (their history is never
    /// imported) and so is any chat whose messages carry no WhatsApp id — there would
    /// be nothing to anchor on.
    /// </summary>
    public static List<BaileysHistorySeed> Seeds(AppDbContext db, ChannelAccount account, string? waId = null,
        int max = MaxSeedChats)
    {
        var wanted = new string((waId ?? "").Where(char.IsDigit).ToArray());
        var threads = db.Threads.AsNoTracking()
            .Where(t => t.Channel == ChannelIds.WhatsApp && t.AccountId == account.Id);
        if (wanted.Length > 0) threads = threads.Where(t => t.Key == wanted);
        var rows = threads
            .OrderByDescending(t => t.UpdatedAt)
            .Take(Math.Max(1, max))
            .Select(t => new
            {
                t.Key,
                // ...and only a message THIS number carried: a conversation can hold
                // messages sent through another account (an operator switched number),
                // and a Cloud wamid is an anchor the paired phone never had
                Oldest = db.Messages.AsNoTracking()
                    .Where(m => m.ThreadId == t.Id && m.ExternalId != "" && m.AccountId == account.Id)
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new { m.ExternalId, m.Direction, m.CreatedAt })
                    .FirstOrDefault(),
            })
            .ToList();

        var seeds = new List<BaileysHistorySeed>();
        foreach (var r in rows)
        {
            if (r.Oldest == null) continue;
            // a group thread is keyed by its …@g.us jid: never imported, never asked about
            if (GroupChats.IsGroup(ChannelIds.WhatsApp, r.Key)) continue;
            var digits = new string((r.Key ?? "").Where(char.IsDigit).ToArray());
            if (digits.Length == 0) continue;
            var seconds = new DateTimeOffset(DateTime.SpecifyKind(r.Oldest.CreatedAt, DateTimeKind.Utc))
                .ToUnixTimeSeconds();
            if (seconds <= 0) continue;
            seeds.Add(new BaileysHistorySeed(digits, r.Oldest.ExternalId,
                r.Oldest.Direction == MessageDirection.Out, seconds));
        }
        return seeds;
    }

    /// <summary>
    /// The gateway's refusal as a person should read it: its own sentence, without the
    /// "gateway 409:" transport prefix the HTTP client puts in front of it.
    /// </summary>
    public static string Reason(string error)
    {
        var text = (error ?? "").Trim();
        var colon = text.StartsWith("gateway ", StringComparison.Ordinal) ? text.IndexOf(": ", StringComparison.Ordinal) : -1;
        var detail = colon > 0 ? text[(colon + 2)..].Trim() : text;
        if (detail.Length == 0) return text;
        return char.ToUpperInvariant(detail[0]) + detail[1..];
    }

    /// <summary>The run as the dashboard and the MCP read it (snake_case, like every payload).</summary>
    public static object Payload(BaileysHistoryRun run) => new
    {
        ok = run.Error.Length == 0,
        running = run.Running,
        aborted = run.Aborted,
        depth = run.Depth,
        requests = run.Requests,
        requests_cap = run.RequestsCap,
        received = run.Received,
        imported = run.Imported,
        timeouts = run.Timeouts,
        errors = run.Errors,
        chats_total = run.ChatsTotal,
        chats_touched = run.ChatsTouched,
        chats_done = run.ChatsDone,
        chats_known = run.ChatsKnown,
        chats_exhausted = run.ChatsExhausted,
        stop_reason = run.StopReason,
        next_gap_s = run.NextGapS,
        started_at = run.StartedAt,
        finished_at = run.FinishedAt,
        last_run_at = run.LastRunAt,
        min_gap_s = run.MinGapS,
        max_gap_s = run.MaxGapS,
        cooldown_s = run.CooldownS,
        day_used = run.DayUsed,
        day_left = run.DayLeft,
        day_cap = run.DayCap,
        error = run.Error,
        chats = run.Chats.Select(c => new
        {
            wa_id = c.WaId, requests = c.Requests, received = c.Received, imported = c.Imported,
            done = c.Done, reason = c.Reason,
        }).ToList(),
    };
}
