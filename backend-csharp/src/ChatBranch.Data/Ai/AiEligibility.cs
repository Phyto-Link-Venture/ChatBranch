using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Is the chatbot on for this conversation? (port of <c>store._effective_ai</c>)
/// — the ONE rule behind both what the dashboard shows (the Chatbot tick on
/// every thread payload) and what the AI actually does (the inbound auto-reply
/// gate, the queued ai_reply drain re-check, the AI monitor's send-time guard,
/// follow-ups). Before this existed the UI read the per-account rule while the
/// gates read the legacy <c>Thread.AiEnabled</c> column, so a business number
/// could show "AI off" and still auto-reply.
///
/// WhatsApp threads with a sending account — the tick is PER SENDING ACCOUNT:
/// <list type="number">
///   <item>an explicit per-account tick (<c>Thread.AiOverrides[account]</c>) wins;</item>
///   <item>an explicit OFF recorded before the thread had any per-account tick
///         (<c>AiEnabled=false</c>, no overrides) stays off — the AI never turns
///         itself on just because a number was attached;</item>
///   <item>the inbox's own <c>ai_default</c> (Channels → Who answers it);</item>
///   <item>the account ROLE: conversation numbers on, reminder (business)
///         numbers off unless the <c>reminder_ai_default</c> rule is "on".</item>
/// </list>
/// Every other thread (account-less/sandbox WhatsApp, Telegram, email, social,
/// the website widget) uses the thread's own <c>AiEnabled</c> flag — the
/// inbox's default is stamped onto it when the conversation is created.
/// </summary>
public static class AiEligibility
{
    public const string ReminderDefaultKey = "reminder_ai_default";

    /// <summary>This account's explicit tick on the thread, or null when it has none.</summary>
    public static bool? Override(Thread t, int accountId)
    {
        if (t.AiOverrides == null
            || !t.AiOverrides.TryGetValue(accountId.ToString(), out var raw) || raw is null) return null;
        return raw switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } el => bool.TryParse(el.GetString(), out var p) ? p : null,
            _ => bool.TryParse(raw.ToString(), out var parsed) ? parsed : null,
        };
    }

    /// <summary>
    /// The pure rule. <paramref name="account"/> is the thread's sending account
    /// (null when it has none or it no longer exists); <paramref name="reminderDefaultOn"/>
    /// is the <c>reminder_ai_default</c> rule for the thread's entity.
    /// </summary>
    public static bool Effective(Thread t, ChannelAccount? account, bool reminderDefaultOn)
    {
        if (t.Channel != ChatBranch.Domain.Channels.WhatsApp || t.AccountId is not int accountId)
            return t.AiEnabled;
        if (Override(t, accountId) is bool tick) return tick;
        if (account == null || account.Id != accountId) return t.AiEnabled;
        if (!t.AiEnabled && (t.AiOverrides == null || t.AiOverrides.Count == 0)) return false;
        if (InboxSettings.AiDefault(account) is bool inboxDefault) return inboxDefault;
        return AccountRoles.Of(account) == AccountRoles.Reminder ? reminderDefaultOn : true;
    }

    /// <summary>The rule with the account and the entity's reminder default resolved from the database.</summary>
    public static bool Effective(AppDbContext db, ScopingService scoping, Thread t)
    {
        try
        {
            if (t.Channel != ChatBranch.Domain.Channels.WhatsApp || t.AccountId is not int accountId)
                return t.AiEnabled;
            var account = t.Account?.Id == accountId ? t.Account : db.ChannelAccounts.Find(accountId);
            return Effective(t, account, ReminderDefaultOn(scoping, t.EntityId));
        }
        catch
        {
            // a broken lookup must never switch the AI ON
            return false;
        }
    }

    /// <summary>The <c>reminder_ai_default</c> routing rule for an entity (entity row → org row → "off").</summary>
    public static bool ReminderDefaultOn(ScopingService scoping, int? entityId) =>
        scoping.SettingValue(ReminderDefaultKey, entityId).Trim().ToLowerInvariant() == "on";

    /// <summary>
    /// Record an operator/automation decision: the tick of the thread's CURRENT
    /// sending account (WhatsApp) plus the legacy column, so both readers agree.
    /// The caller saves.
    /// </summary>
    public static void Set(Thread t, bool enabled)
    {
        if (t.Channel == ChatBranch.Domain.Channels.WhatsApp && t.AccountId is int acct)
            t.AiOverrides = new Dictionary<string, object?>(t.AiOverrides ?? new()) { [acct.ToString()] = enabled };
        t.AiEnabled = enabled;
        t.UpdatedAt = DateTime.UtcNow;
    }
}
