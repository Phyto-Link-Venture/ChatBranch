using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
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
///
/// Above and around that tick sit the AI controls (<see cref="Decide(Thread, ChannelAccount?, bool, AiPolicyInputs)"/>),
/// in this order: opted out → the master switch / schedule (<c>off</c> wins) →
/// the contact's <c>never</c> mode / a never-AI tag → the per-chat tick → the
/// contact's <c>always</c> mode → the inbox's <c>ai_default</c> → the role default.
/// Every verdict carries the reason the dashboard shows (<c>ai_reason</c>).
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
    public static bool Effective(Thread t, ChannelAccount? account, bool reminderDefaultOn) =>
        TickLayer(t, account, reminderDefaultOn, contactAlways: false).On;

    /// <summary>
    /// The full rule with the account, the contact and the AI controls resolved
    /// from the database — what every gate (inbound auto-reply, queued ai_reply,
    /// the AI monitor's send-time guard, follow-ups) and every payload reads.
    /// </summary>
    public static bool Effective(AppDbContext db, ScopingService scoping, Thread t, DateTime? nowUtc = null) =>
        Decide(db, scoping, t, nowUtc).Enabled;

    /// <summary><see cref="Effective(AppDbContext, ScopingService, Thread, DateTime?)"/> with its reason.</summary>
    public static AiVerdict Decide(AppDbContext db, ScopingService scoping, Thread t, DateTime? nowUtc = null)
    {
        try
        {
            ChannelAccount? account = null;
            if (t.Channel == ChatBranch.Domain.Channels.WhatsApp && t.AccountId is int accountId)
                account = t.Account?.Id == accountId ? t.Account : db.ChannelAccounts.Find(accountId);
            var (mode, tags) = ContactInputs(db, t);
            return new AiControls(db, scoping, nowUtc).Decide(t, account, mode, tags);
        }
        catch
        {
            // a broken lookup must never switch the AI ON
            return new AiVerdict(false, AiRules.Error, "Off: the AI settings could not be read", true);
        }
    }

    /// <summary>The contact's AI mode + tag names (loaded only when the thread does not carry them).</summary>
    public static (string Mode, List<string> Tags) ContactInputs(AppDbContext db, Thread t)
    {
        if (t.ContactId is not int cid) return ("", new List<string>());
        if (t.Contact?.Id == cid) return (t.Contact.AiMode ?? "", t.Contact.Tags ?? new List<string>());
        var c = db.Contacts.AsNoTracking().Where(x => x.Id == cid)
            .Select(x => new { x.AiMode, x.Tags }).FirstOrDefault();
        return (c?.AiMode ?? "", c?.Tags ?? new List<string>());
    }

    /// <summary>
    /// THE precedence rule (pure): opted out → master switch / schedule →
    /// contact never / never-AI tag → per-chat tick → contact always → inbox
    /// default → role default. Rules above the tick are <see cref="AiVerdict.Forced"/>.
    /// </summary>
    public static AiVerdict Decide(Thread t, ChannelAccount? account, bool reminderDefaultOn, AiPolicyInputs p)
    {
        if (t.OptedOut)
            return new AiVerdict(false, AiRules.OptedOut, "Off: this contact opted out (STOP)", true);
        if (p.Master.Mode == AiMasterModes.Off)
            return new AiVerdict(false, AiRules.MasterOff, $"Off: AI is switched off for {p.Master.ScopeName}", true);
        if (p.Master.Mode == AiMasterModes.Schedule && !p.Master.AllowsAt(p.NowUtc, p.Zone))
            return new AiVerdict(false, AiRules.Schedule, "Off: outside the AI schedule", true);
        var mode = ContactAiModes.Normalize(p.ContactMode);
        if (mode == ContactAiModes.Never)
            return new AiVerdict(false, AiRules.ContactNever, "Off: this contact is set to never use AI", true);
        if (AiControls.NeverTagHit(p.ContactTags, p.NeverTags) is string tag)
            return new AiVerdict(false, AiRules.NeverTag, $"Off: this contact is tagged “{tag}” — AI never answers it", true);

        var (on, rule) = TickLayer(t, account, reminderDefaultOn, mode == ContactAiModes.Always);
        var inbox = account?.Name is { Length: > 0 } n ? $"the {n} inbox" : "this inbox";
        var reason = rule switch
        {
            AiRules.ChatTick => on ? "On: switched on for this chat" : "Off: switched off for this chat",
            AiRules.ContactAlways => "On: this contact is set to always use AI",
            AiRules.InboxDefault => on ? $"On: {inbox} answers with AI" : $"Off: {inbox} has AI off by default",
            AiRules.RoleDefault => on
                ? "On: conversation numbers answer with AI"
                : "Off: business (reminder) numbers have AI off by default",
            _ => on ? "On: AI answers this chat" : "Off: AI is off for this chat",
        };
        if (!on && rule == AiRules.ChatTick && Handover.IsOpen(t))
            reason = "Off: handed to a person — waiting for a human";
        if (on && p.Master.Mode == AiMasterModes.Schedule) reason += " (inside the AI schedule)";
        return new AiVerdict(on, rule, reason, false);
    }

    /// <summary>
    /// The per-chat layer: the tick (per sending account on WhatsApp) → the
    /// contact's "always" → the inbox default → the role default.
    /// </summary>
    internal static (bool On, string Rule) TickLayer(Thread t, ChannelAccount? account, bool reminderDefaultOn,
        bool contactAlways)
    {
        if (t.Channel != ChatBranch.Domain.Channels.WhatsApp || t.AccountId is not int accountId)
            return Unbound(t, contactAlways);
        if (Override(t, accountId) is bool tick) return (tick, AiRules.ChatTick);
        if (account == null || account.Id != accountId) return Unbound(t, contactAlways);
        var inboxDefault = InboxSettings.AiDefault(account);
        // an explicit OFF from before per-account ticks — unless the inbox's own
        // "off" default is what put it there (then "always" may still win)
        if (!t.AiEnabled && !HasAccountTicks(t) && inboxDefault != false) return (false, AiRules.ChatTick);
        if (contactAlways) return (true, AiRules.ContactAlways);
        if (inboxDefault is bool d) return (d, AiRules.InboxDefault);
        return (AccountRoles.Of(account) == AccountRoles.Reminder ? reminderDefaultOn : true, AiRules.RoleDefault);
    }

    /// <summary>A thread with no sending account: its flag, which an explicit tick marks as a decision.</summary>
    private static (bool On, string Rule) Unbound(Thread t, bool contactAlways)
    {
        if (HasThreadTick(t)) return (t.AiEnabled, AiRules.ChatTick);
        if (contactAlways) return (true, AiRules.ContactAlways);
        return (t.AiEnabled, AiRules.Thread);
    }

    /// <summary>
    /// The marker <see cref="Set"/> leaves on a thread with no sending account:
    /// its <c>AiEnabled</c> is then an operator's decision, not a stamped default.
    /// </summary>
    public const string ThreadTickKey = "thread";

    private static bool HasThreadTick(Thread t) =>
        t.AiOverrides != null && t.AiOverrides.ContainsKey(ThreadTickKey);

    private static bool HasAccountTicks(Thread t) =>
        t.AiOverrides != null && t.AiOverrides.Keys.Any(k => int.TryParse(k, out _));

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
        else
            t.AiOverrides = new Dictionary<string, object?>(t.AiOverrides ?? new()) { [ThreadTickKey] = enabled };
        t.AiEnabled = enabled;
        t.UpdatedAt = DateTime.UtcNow;
    }
}
