using ChatBranch.Data.Ai;
using ChatBranch.Data.Contacts;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using ChannelIds = ChatBranch.Domain.Channels;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// A message a person sent from the PHONE (or WhatsApp Web) of a QR-paired (Baileys)
/// number — pushed by the gateway on <c>/api/whatsapp/baileys/from-phone</c>. Before
/// this, ChatBranch never saw it: the chat looked unanswered, the AI could answer the
/// customer a second time and an open handover stayed open. It is stored as that
/// person's OUTBOUND reply (<see cref="MessageSource.ExternalClient"/>, status sent, the
/// phone's own time), like a mail from the Sent folder (<see cref="ImapSync"/>), so
/// everything a person's reply does follows on its own: the handover is answered, a
/// queued AI reply finds nothing left to answer (<see cref="AiResponder"/>), a held AI
/// draft is superseded (<see cref="AiGate"/>), follow-ups stop. Deliberately NOT the
/// inbound path — no AI, flows, automations, notifications or STOP. ChatBranch's own
/// sends never come back here (the gateway skips them, and the provider id dedupes).
/// </summary>
public static class PhoneSentMessages
{
    /// <summary>Who answered a handover from the phone (<c>agent:phone</c>).</summary>
    public const string By = "phone";

    /// <summary>
    /// Record it on the number's conversation with that customer (its company's; a new
    /// one when the person started the chat from the phone — linked to the matching
    /// contact, chatbot off: a person is having this conversation). Returns the message
    /// (the existing one for a redelivery or ChatBranch's own send), or null when there
    /// is nothing to file. Never throws.
    /// </summary>
    public static Message? Record(AppDbContext db, ScopingService scoping, ThreadStore threads,
        InboundProcessor? inbound, ChannelAccount account, string waId, string text, string msgId,
        string replyToExternalId = "", long sentAtSeconds = 0)
    {
        try
        {
            // a group's whole jid (a reply typed into the group on the phone), else the digits
            waId = GroupChats.WhatsAppKey(waId);
            if (waId.Length == 0) return null;
            msgId = (msgId ?? "").Trim();
            if (msgId.Length > 0)
            {
                var known = db.Messages.Include(m => m.Thread)
                    .Where(m => m.ExternalId == msgId && m.Thread!.Channel == ChannelIds.WhatsApp && m.Thread.Key == waId)
                    .OrderByDescending(m => m.Id).FirstOrDefault();
                if (known != null) return known;   // ChatBranch sent it, or this is a redelivery
            }

            var thread = threads.FindForAccount(ChannelIds.WhatsApp, waId, account.Id);
            var started = thread == null;
            thread ??= threads.GetOrCreateThread(ChannelIds.WhatsApp, waId,
                GroupChats.IsGroup(ChannelIds.WhatsApp, waId) ? GroupChats.DefaultName(ChannelIds.WhatsApp) : null,
                account.Id);
            if (started) Started(db, scoping, inbound, thread, account);

            var msg = threads.AppendMessage(thread, MessageDirection.Out, MessageSource.ExternalClient, text ?? "",
                status: "sent", externalId: msgId, accountId: account.Id, providerTs: SentAt(sentAtSeconds),
                replyToExternalId: replyToExternalId ?? "");
            Handover.Clear(db, thread, Handover.Agent(By), "A person replied from the phone.");
            return msg;
        }
        catch
        {
            return null; // a webhook never fails on bookkeeping
        }
    }

    /// <summary>A chat the person started from the phone: the customer's contact, and no chatbot.</summary>
    private static void Started(AppDbContext db, ScopingService scoping, InboundProcessor? inbound, Thread thread,
        ChannelAccount account)
    {
        if (thread.ContactId == null && inbound?.MatchContact(ChannelIds.WhatsApp, thread.Key, account.EntityId) is { } c
            && ContactCompanies.MayLink(db, scoping, c.Id, scoping.ThreadCompany(thread)))
        {
            thread.ContactId = c.Id;
            db.SaveChanges();
            scoping.SyncThreadEntity(thread);
        }
        AiEligibility.Set(thread, false);
        db.SaveChanges();
    }

    /// <summary>The phone's own time, unless it is missing or implausible (then now).</summary>
    public static DateTime? SentAt(long seconds)
    {
        if (seconds <= 0) return null;
        var at = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        var now = DateTime.UtcNow;
        return at > now.AddMinutes(1) || at < now.AddDays(-1) ? null : at;
    }
}
