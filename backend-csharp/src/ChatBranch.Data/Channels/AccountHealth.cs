using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The channel-account health WRITER (port of <c>whatsapp/health.py set_status</c>):
/// every path that moves <see cref="ChannelAccount.HealthStatus"/> because of an
/// observation (a gateway lifecycle push, inbound traffic proving a session is
/// alive…) goes through <see cref="SetStatus"/>, which records exactly ONE
/// <c>account.health</c> AuditLog row per real transition. Analytics → "Account
/// health events" reads those rows; before this existed nothing wrote them and
/// the card was permanently empty.
///
/// The detail format is the contract the card parses:
/// <c>"healthy → degraded — reason"</c> (target = the account NAME, which is how
/// the analytics entity filter matches rows to an outlet's accounts).
///
/// The reconnect reset (new credentials → unknown) deliberately stays silent and
/// does not come through here, as in Django's <c>reset_on_reconfigure</c>.
/// The caller saves.
/// </summary>
public static class AccountHealth
{
    public const string AuditAction = "account.health";

    /// <returns>true when the status actually changed (and an audit row was added).</returns>
    public static bool SetStatus(AppDbContext db, ChannelAccount account, string next,
        string? detail = null, string actor = "system")
    {
        var now = DateTime.UtcNow;
        var prev = string.IsNullOrWhiteSpace(account.HealthStatus) ? Health.Unknown : account.HealthStatus;
        if (detail != null) account.HealthDetail = detail;
        if (next == Health.Healthy)
        {
            account.LastOkAt = now;
            account.FailStreak = 0;
        }
        if (prev == next) return false;

        account.HealthStatus = next;
        account.HealthChangedAt = now;
        var reason = (detail ?? "").Trim();
        db.AuditLogs.Add(new AuditLog
        {
            Actor = actor,
            Action = AuditAction,
            Target = string.IsNullOrWhiteSpace(account.Name)
                ? $"{account.Channel} #{account.Id}" : account.Name,
            Detail = reason.Length > 0 ? $"{prev} → {next} — {reason}" : $"{prev} → {next}",
            CreatedAt = now,
        });
        return true;
    }
}
