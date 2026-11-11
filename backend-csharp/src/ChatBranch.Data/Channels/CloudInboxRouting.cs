using ChatBranch.Data.Audit;
using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Which inbox a WhatsApp Cloud webhook payload is allowed to land in.
///
/// The Cloud webhook matches an inbound payload to a <see cref="ChannelAccount"/>
/// by <c>phone_number_id</c> (<c>Endpoints/WebhooksEndpoints</c>). Until this
/// guard existed, a payload whose number matched NOTHING was still processed with
/// <c>accountId: null</c>, so <c>ThreadStore</c> filed the conversation with no
/// company — the organization-wide unattributed bucket EVERY entity can see — and
/// the first company whose own number later touched that conversation adopted it
/// (<c>ThreadStore.Settle</c>). On an install that hosts unrelated client
/// companies that is a cross-tenant leak, not a tidiness problem: one wrong or
/// unregistered number puts a real customer chat in a shared bucket and then
/// hands it to an unrelated business.
///
/// So on an install that has ANY WhatsApp number, an unmatched payload is recorded
/// and dropped: no thread, no message, no reaction, no status — and still a 200, so
/// Meta stops retrying something we will never accept. A Baileys- or WAHA-only
/// install is armed too: it has no Cloud number subscribed, so no Cloud payload can
/// be legitimate, and this endpoint is public and unsigned until a Cloud account
/// exists. Only an install with no WhatsApp number at all (sandbox, demo) is
/// untouched: there is nothing to route by, and "sandbox must never raise".
///
/// There is no unknown-SENDER store to record this on — the "unknown customers"
/// surface (<c>channels_get_unknown_customers</c> → <c>/api/inboxes/{id}/identify</c>)
/// is a per-INBOX setting for senders who are not contacts yet, and an unmatched
/// payload has no inbox by definition. The record is therefore an audit row
/// (<see cref="UnknownNumberAudit"/>), readable through the existing activity log
/// (<c>GET /api/audit</c>, <c>activity_search_log</c>). It deliberately carries NO
/// message text and no sender number: an organization-level row is visible more
/// widely than the conversation would have been, so putting the customer's words
/// in it would re-leak what the drop just prevented.
/// </summary>
public static class CloudInboxRouting
{
    /// <summary>
    /// Organization-level Setting that restores the OLD permissive bucketing
    /// (an unmatched <c>phone_number_id</c> files into the unattributed bucket,
    /// which a company can then adopt). Off by default — the safe behaviour is the
    /// default and this is the escape hatch for an install that depended on the old
    /// one. Not entity-scoped (webhook intake is organization-wide, like the
    /// signature secrets): keep it out of <c>ScopingService.EntityScopedSettings</c>.
    /// </summary>
    public const string AcceptUnknownSetting = "whatsapp_accept_unknown_numbers";

    /// <summary>The audit action written for a dropped payload.</summary>
    public const string UnknownNumberAudit = "whatsapp.webhook.unknown_number";

    /// <summary>Is the old permissive bucketing switched back on? Never throws (webhook path).</summary>
    public static bool AcceptUnknownNumbers(ScopingService? scoping)
    {
        try
        {
            var raw = (scoping?.SettingValue(AcceptUnknownSetting) ?? "").Trim().ToLowerInvariant();
            return raw is "1" or "on" or "true" or "yes";
        }
        catch { return false; }
    }

    /// <summary>
    /// Does the payload's WABA (<c>entry[].id</c>) contradict the number we matched?
    /// Only when BOTH are non-blank and differ — a missing, blank or unknown WABA on
    /// either side is never a reason to drop a real customer's message, because
    /// <c>entry[].id</c> is not formally specified for <c>messages</c> and an
    /// operator may simply have left <c>waba_id</c> empty (defence in depth only).
    /// </summary>
    public static bool WabaMismatch(ChannelAccount account, string payloadWaba)
    {
        var mine = (account.WabaId ?? "").Trim();
        var theirs = (payloadWaba ?? "").Trim();
        return mine.Length > 0 && theirs.Length > 0
               && !string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Record a dropped payload: one audit row per (payload, phone_number_id),
    /// counting what was ignored. No message text, no sender number — see the class
    /// docstring. Never throws (<see cref="AuditTrail.Write"/> swallows its own errors).
    /// </summary>
    public static void RecordUnknownNumber(AppDbContext db, string phoneNumberId, string payloadWaba,
        string why, int messages, int reactions, int statuses, string sampleWamid = "")
    {
        var waba = (payloadWaba ?? "").Trim();
        var detail = $"{why}. Ignored: {messages} message(s), {reactions} reaction(s), {statuses} status(es)"
                     + (sampleWamid.Length > 0 ? $"; first message id {sampleWamid}" : "")
                     + $". No conversation was created or touched. Register this number under its company "
                     + $"(Channels → add a WhatsApp Cloud number), or set the Setting "
                     + $"{AcceptUnknownSetting}=on to restore the old behaviour.";
        AuditTrail.Write(db, UnknownNumberAudit,
            target: $"phone_number_id:{phoneNumberId}" + (waba.Length > 0 ? $" waba:{waba}" : ""),
            detail: detail, actor: "webhook", source: "webhook");
    }
}
