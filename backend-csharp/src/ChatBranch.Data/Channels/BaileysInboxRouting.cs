using ChatBranch.Data.Audit;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Which inbox a Baileys gateway push is allowed to land in.
///
/// The gateway (backend/baileys_service) names the paired number it is pushing for in
/// <c>account_id</c> — the gateway key, which is <c>ChannelAccount.ExternalId</c> (written by
/// <c>EnsureBaileysKey</c> BEFORE the gateway is ever told to start or pair a session, so a
/// legitimate push — the first message after pairing or after a reconnect included — always
/// names an existing row). A push whose key matches NO account used to be processed with
/// <c>accountId: null</c>: filed in the organization-wide unattributed bucket every company can
/// see, which the next company to touch that customer then adopted — the same shape as the
/// Cloud-webhook leak (<see cref="CloudInboxRouting"/>). Realistic sources: a number deleted in
/// ChatBranch whose session the gateway still restores from its auth directory on boot, or a
/// forged push from anything holding the shared secret.
///
/// Such a push is now RECORDED and DROPPED (still a 200, so the gateway does not retry what
/// will never be accepted): no thread, no message, no reaction, no media. The record is an
/// organization-level audit row — readable through the activity log — that deliberately
/// carries NO message text, NO sender number and NO sender name: an organization-level row
/// is visible more widely than the conversation would have been.
/// </summary>
public static class BaileysInboxRouting
{
    /// <summary>The audit action written for a dropped push.</summary>
    public const string UnknownAccountAudit = "whatsapp.baileys.unknown_account";

    /// <summary>
    /// Record a push from an unknown gateway key. <paramref name="what"/> says what was
    /// ignored ("message", "reaction", "media"). Nothing else from the push is echoed — not
    /// even its message id: the push is untrusted, and any free-text field of it could carry
    /// the words or the number this record must not hold. The key itself is capped. Never throws.
    /// </summary>
    public static void RecordUnknownAccount(AppDbContext db, string? accountKey, string what)
    {
        var key = Clean(accountKey, 64);
        var detail = $"A Baileys gateway push ({what}) named account_id \"{(key.Length > 0 ? key : "(none)")}\", "
                     + "which matches no WhatsApp QR number. Nothing was filed: no conversation was created or touched. If the number was deleted "
                     + "here, log it out on the gateway (or re-pair it as a new number under its company).";
        AuditTrail.Write(db, UnknownAccountAudit,
            target: $"baileys_account:{(key.Length > 0 ? key : "(none)")}",
            detail: detail, actor: "webhook", source: "webhook");
    }

    private static string Clean(string? s, int max)
    {
        var v = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return v.Length <= max ? v : v[..max];
    }
}
