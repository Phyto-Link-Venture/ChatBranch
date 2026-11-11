using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Channels;

/// <summary>
/// One Cloud webhook message that carries NO phone number (<c>messages[].from</c> omitted):
/// its sender is known only by the business-scoped user id (BSUID, <c>from_user_id</c> /
/// <c>contacts[].user_id</c>) and, when they have one, their WhatsApp username.
/// </summary>
public sealed record CloudUnkeyedInbound(string PhoneNumberId, string UserId, string Username, string Wamid, string Type);

/// <summary>
/// WhatsApp usernames. Meta's message webhook now identifies every sender by a BSUID
/// (<c>contacts[].user_id</c>, <c>messages[].from_user_id</c>), and for a user who adopted a
/// username and whose phone number is unavailable it OMITS <c>contacts[].wa_id</c> and
/// <c>messages[].from</c> altogether. ChatBranch keys a WhatsApp conversation on the phone
/// number (<c>Thread.Key</c> = wa_id), so such a message used to vanish: the parser returned
/// nothing, no record anywhere.
///
/// It is NOT keyed on the BSUID — yet. Doing that safely needs three things this codebase
/// does not have: (1) nothing may treat the key as a phone number — <c>PhoneMatch.Digits</c>
/// runs on WhatsApp thread keys (contact matching, <c>ThreadStore.JobKey</c>, the canonical
/// "60…" phone rule), and the digits inside a BSUID would match a random contact by phone tail
/// and could be dialled; (2) outbound must address such a user by <c>recipient</c> (the BSUID),
/// not <c>to</c> (our Cloud client only sends <c>to</c>); (3) the same person arriving later
/// WITH a phone number must land in the same conversation, not a second one (no BSUID ↔ wa_id
/// link is stored). Until then the message is RECORDED, visibly, for the company whose number
/// received it — an audit row scoped to that number's entity, with the message id, type,
/// username and BSUID (so an operator can find the person in WhatsApp) but never the words.
/// A number with no company (organization-wide, or the env-default number) gets an
/// organization-level row WITHOUT the username / BSUID, like the other drop records.
/// </summary>
public static class WhatsAppUsernameSenders
{
    /// <summary>The audit action written for a message that had no phone number to key it on.</summary>
    public const string Audit = "whatsapp.webhook.no_phone_number";

    /// <summary>
    /// Record one phone-less message (never throws). <paramref name="account"/> is the
    /// receiving number's row, null for the env-default number (no row to scope to).
    /// A redelivery of the same message id writes nothing new.
    /// </summary>
    public static void Record(AppDbContext db, ChannelAccount? account, CloudUnkeyedInbound m)
    {
        try
        {
            if (m.Wamid.Length > 0
                && db.AuditLogs.AsNoTracking().Any(a => a.Action == Audit && a.Detail.Contains(m.Wamid)))
                return;
            var scoped = account?.EntityId is int;
            var who = scoped
                ? (m.Username.Length > 0 ? $"username @{m.Username.TrimStart('@')}, " : "")
                  + (m.UserId.Length > 0 ? $"business-scoped user id {m.UserId}" : "no user id")
                : "a WhatsApp username holder";
            var detail = $"A WhatsApp {(m.Type.Length > 0 ? m.Type : "message")} from {who} arrived without a phone "
                         + $"number (message id {(m.Wamid.Length > 0 ? m.Wamid : "(none)")}). ChatBranch keys WhatsApp "
                         + "conversations on the phone number, so it was NOT filed in a conversation and cannot be "
                         + "answered from here — reply from the WhatsApp app, or ask the customer for their number.";
            AuditTrail.Write(db, Audit,
                target: account != null ? $"whatsapp_account:#{account.Id}" : $"phone_number_id:{m.PhoneNumberId}",
                detail: detail, actor: "webhook", source: "webhook", entityId: account?.EntityId);
        }
        catch { /* a record never breaks the webhook */ }
    }
}
