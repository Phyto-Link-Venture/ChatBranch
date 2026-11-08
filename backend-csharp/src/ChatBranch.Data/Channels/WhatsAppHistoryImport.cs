using ChatBranch.Data.Contacts;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using ChannelIds = ChatBranch.Domain.Channels;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>One message out of a QR number's own history, as the gateway replays it.</summary>
/// <param name="WaId">The customer's number (digits).</param>
/// <param name="Name">Their WhatsApp display name, when the chat carried one.</param>
/// <param name="MsgId">WhatsApp's own id — what makes a re-import a no-op.</param>
/// <param name="FromMe">The number sent it (so: an outbound reply), else the customer did.</param>
/// <param name="Seconds">WhatsApp's own timestamp (unix seconds).</param>
public record HistoryMessage(string WaId, string Name, string MsgId, bool FromMe, long Seconds, string Text,
    string ReplyToExternalId = "");

/// <summary>What an import pass did, for the gateway's log and the endpoint's answer.</summary>
public record HistoryImportResult(int Stored, int Skipped, int Threads);

/// <summary>
/// The conversations a QR-paired number ALREADY had, read back from WhatsApp when the
/// number is linked (<c>POST /api/whatsapp/baileys/history</c>, pushed by the gateway's
/// <c>messaging-history.set</c> handler). Pairing used to start the inbox at zero: an
/// operator opening ChatBranch saw none of the chats that were on the phone, so a
/// customer mid-conversation looked like a stranger.
///
/// It is a BACKFILL, not an inbound: every message is written straight through
/// <see cref="ThreadStore"/> and nothing else runs — no AI reply, no flows, no
/// automation rules, no notifications, no STOP handling, no handover being answered,
/// no resolved conversation reopening. The model is the mailbox's Sent-folder import
/// (<see cref="ImapSync"/>): the words arrive, the side effects do not. A message
/// from the number itself is stored as that person's OUTBOUND reply
/// (<see cref="MessageSource.ExternalClient"/>, like <see cref="PhoneSentMessages"/>),
/// the customer's as a normal inbound.
///
/// How far back is the inbox's own setting (<see cref="BaileysSettings.ImportHistoryDays"/>,
/// 30 days by default); the gateway applies the window before pushing and this applies
/// it again, so a replay nobody asked for cannot slip in. WhatsApp only ever serves what
/// the paired phone still holds, so "everything" is its history, not ours.
/// </summary>
public static class WhatsAppHistoryImport
{
    /// <summary>Most messages one push may carry — the gateway sends history in batches.</summary>
    public const int MaxPerPush = 500;

    /// <summary>
    /// File a batch on the account's conversations. Idempotent: a message WhatsApp
    /// replays twice (or one ChatBranch already has) is skipped by its provider id.
    /// Never throws — a gateway push must not fail on bookkeeping.
    /// </summary>
    public static HistoryImportResult Import(AppDbContext db, ScopingService scoping, ThreadStore threads,
        InboundProcessor? inbound, ChannelAccount account, IReadOnlyList<HistoryMessage> messages)
    {
        var stored = 0;
        var skipped = 0;
        // the conversation and the status it had BEFORE history touched it
        var touched = new Dictionary<int, (Thread Thread, string Status)>();
        var cutoff = Cutoff(account);

        foreach (var m in messages.Take(MaxPerPush))
        {
            try
            {
                if (!Wanted(m, cutoff)) { skipped++; continue; }
                var thread = Thread(db, scoping, threads, inbound, account, m);
                if (thread == null) { skipped++; continue; }
                if (!touched.ContainsKey(thread.Id)) touched[thread.Id] = (thread, thread.Status);

                var before = db.Messages.Count(x => x.ThreadId == thread.Id && x.ExternalId == m.MsgId);
                if (before > 0) { skipped++; continue; }

                threads.AppendMessage(thread,
                    m.FromMe ? MessageDirection.Out : MessageDirection.In,
                    m.FromMe ? MessageSource.ExternalClient : MessageSource.Customer,
                    m.Text ?? "",
                    status: m.FromMe ? "sent" : "received",
                    externalId: m.MsgId,
                    accountId: account.Id,
                    providerTs: At(m.Seconds),
                    replyToExternalId: m.ReplyToExternalId ?? "");
                stored++;
            }
            catch
            {
                skipped++;   // one bad row never costs the batch
            }
        }

        foreach (var (thread, status) in touched.Values) Settle(db, thread, status);
        return new HistoryImportResult(stored, skipped, touched.Count);
    }

    /// <summary>
    /// How far back this inbox imports, as a UTC instant — null when the window is
    /// open ("everything WhatsApp sends").
    /// </summary>
    public static DateTime? Cutoff(ChannelAccount account) =>
        BaileysSettings.ImportHistoryDays(account) switch
        {
            BaileysSettings.HistoryAll => null,
            var days when days > 0 => DateTime.UtcNow.AddDays(-days),
            // off: nothing is wanted, which Wanted() reads as "in the future"
            _ => DateTime.UtcNow.AddYears(1),
        };

    /// <summary>Is this message one the inbox asked for — a real id, inside the window?</summary>
    public static bool Wanted(HistoryMessage m, DateTime? cutoff)
    {
        if (string.IsNullOrWhiteSpace(m.MsgId)) return false;
        if (At(m.Seconds) is not DateTime at) return false;
        return cutoff is not DateTime from || at >= from;
    }

    /// <summary>
    /// The conversation this message belongs on, creating it the way a chat started
    /// from the phone is created: the company of the receiving number, the matching
    /// contact when that company may see it, and the inbox's own AI default — which
    /// is only ever stamped on a conversation with no messages, so an import can
    /// never overrule an operator's later tick.
    /// </summary>
    private static Thread? Thread(AppDbContext db, ScopingService scoping, ThreadStore threads,
        InboundProcessor? inbound, ChannelAccount account, HistoryMessage m)
    {
        var waId = new string((m.WaId ?? "").Where(char.IsDigit).ToArray());
        if (waId.Length == 0) return null;

        var thread = threads.FindForAccount(ChannelIds.WhatsApp, waId, account.Id);
        if (thread != null) return thread;

        thread = threads.GetOrCreateThread(ChannelIds.WhatsApp, waId, m.Name, account.Id);
        if (thread.ContactId == null
            && inbound?.MatchContact(ChannelIds.WhatsApp, waId, account.EntityId) is { } contact
            && ContactCompanies.MayLink(db, scoping, contact.Id, scoping.ThreadCompany(thread)))
        {
            thread.ContactId = contact.Id;
            db.SaveChanges();
            scoping.SyncThreadEntity(thread);
        }
        InboxSettings.ApplyToNewThread(db, thread);
        return thread;
    }

    /// <summary>
    /// Put the conversation back the way history found it. <see cref="ThreadStore.AppendMessage"/>
    /// treats every inbound as live: it stamps <c>UpdatedAt</c> with NOW and reopens a
    /// resolved or snoozed conversation. Neither is true of a message from last week —
    /// it would jump a dormant chat to the top of the inbox and reopen work someone
    /// finished — so the status is restored and <c>UpdatedAt</c> is set to the newest
    /// message the conversation actually holds.
    /// </summary>
    private static void Settle(AppDbContext db, Thread thread, string statusBefore)
    {
        try
        {
            thread.Status = statusBefore;
            var newest = db.Messages.Where(m => m.ThreadId == thread.Id)
                .Max(m => (DateTime?)m.CreatedAt);
            if (newest is DateTime at && at < thread.UpdatedAt) thread.UpdatedAt = at;
            db.SaveChanges();
        }
        catch { /* ordering is cosmetic — never lose the import over it */ }
    }

    /// <summary>
    /// WhatsApp's own timestamp, or null when it is missing or cannot be true (a
    /// message from the future, or from before WhatsApp existed).
    /// </summary>
    public static DateTime? At(long seconds)
    {
        if (seconds <= 0) return null;
        var at = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return at > DateTime.UtcNow.AddMinutes(5) || at.Year < 2009 ? null : at;
    }
}
