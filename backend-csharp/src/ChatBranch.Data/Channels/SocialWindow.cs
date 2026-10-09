using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>Where a Messenger / Instagram conversation stands against Meta's messaging window.</summary>
public enum SocialWindowState
{
    /// <summary>The customer wrote within 24 hours: anything may be sent (<c>RESPONSE</c>).</summary>
    Open,
    /// <summary>24 hours to 7 days: only a PERSON may reply, tagged <c>HUMAN_AGENT</c>.</summary>
    HumanAgentOnly,
    /// <summary>More than 7 days, or the customer never wrote: nothing may be sent.</summary>
    Closed,
}

/// <summary>
/// Meta's standard messaging window for Messenger and Instagram (the social
/// counterpart of WhatsApp's 24h rule, <see cref="WhatsAppWindow"/>):
///
///   * within 24h of the customer's last message — any reply, <c>messaging_type: RESPONSE</c>;
///   * 24h → 7 days — a HUMAN agent's reply only, sent as
///     <c>messaging_type: MESSAGE_TAG, tag: HUMAN_AGENT</c>. AI, flow, automation
///     and API sends must not use the tag, so they are refused;
///   * beyond 7 days (or no inbound at all) — refused for everyone.
///
/// Like WhatsApp's window it is derived at READ time from the newest inbound
/// row, whose timestamp is Meta's own (webhooks can arrive late), and it only
/// binds a LIVE page/account: a sandbox thread never dead-ends.
/// </summary>
public static class SocialWindow
{
    public const int StandardHours = 24;
    public const int HumanAgentDays = 7;
    public const string HumanAgentTag = "HUMAN_AGENT";

    public static bool Applies(string? channel) =>
        channel is ChannelIds.Facebook or ChannelIds.Instagram;

    /// <summary>A page / Instagram account that would send for real (a token on a social account).</summary>
    public static bool Live(ChannelAccount? account, string channel) =>
        account != null && Applies(channel) && account.Channel == channel
        && !string.IsNullOrEmpty(account.AccessToken);

    public static string Label(string channel) => channel == ChannelIds.Instagram ? "Instagram" : "Messenger";

    /// <summary>The newest inbound message's timestamp (UTC), or null when the customer never wrote.</summary>
    public static DateTime? LastInbound(AppDbContext db, Thread thread) =>
        db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
            .Max(m => (DateTime?)m.CreatedAt);

    public static SocialWindowState StateAt(DateTime? lastInbound, DateTime nowUtc)
    {
        if (lastInbound is not DateTime raw) return SocialWindowState.Closed;
        var ts = DateTime.SpecifyKind(raw, DateTimeKind.Utc);
        if (nowUtc < ts.AddHours(StandardHours)) return SocialWindowState.Open;
        if (nowUtc < ts.AddDays(HumanAgentDays)) return SocialWindowState.HumanAgentOnly;
        return SocialWindowState.Closed;
    }

    /// <summary>Why this send may not go out (null = it may).</summary>
    public static string? Refusal(string channel, SocialWindowState state, bool humanAgent, bool everWrote = true)
    {
        var name = Label(channel);
        return state switch
        {
            SocialWindowState.Open => null,
            SocialWindowState.HumanAgentOnly when humanAgent => null,
            SocialWindowState.HumanAgentOnly =>
                $"{name}'s 24-hour window has closed — AI and automated messages can't be sent until the " +
                $"customer writes again. A person can still reply from the inbox for {HumanAgentDays} days " +
                "after their last message.",
            _ when !everWrote =>
                $"{name} only allows replies after the customer has written to you — nothing was sent.",
            _ => $"{name}'s {HumanAgentDays}-day reply window has closed — nothing can be sent until the " +
                 "customer writes again.",
        };
    }

    /// <summary>The Send API tag for an ALLOWED send: null = <c>RESPONSE</c>, else <c>MESSAGE_TAG</c> + this tag.</summary>
    public static string? Tag(SocialWindowState state, bool humanAgent) =>
        state == SocialWindowState.HumanAgentOnly && humanAgent ? HumanAgentTag : null;

    /// <summary>
    /// The window as payloads show it: <c>window_open</c> (the 24h window) and
    /// <c>human_window_open</c> (a person may still reply), with their expiry
    /// times. Not live → always open, no expiry.
    /// </summary>
    public static Dictionary<string, object?> Fields(bool live, DateTime? lastInbound, DateTime nowUtc)
    {
        if (!live)
            return new()
            {
                ["window_open"] = true, ["window_expires_at"] = null,
                ["human_window_open"] = true, ["human_window_expires_at"] = null,
            };
        var state = StateAt(lastInbound, nowUtc);
        DateTime? ts = lastInbound is DateTime raw ? DateTime.SpecifyKind(raw, DateTimeKind.Utc) : null;
        return new()
        {
            ["window_open"] = state == SocialWindowState.Open,
            ["window_expires_at"] = ts?.AddHours(StandardHours).ToString("o"),
            ["human_window_open"] = state != SocialWindowState.Closed,
            ["human_window_expires_at"] = ts?.AddDays(HumanAgentDays).ToString("o"),
        };
    }

    /// <summary>The 24h window for a list row: (open, expires-at). Not live → open.</summary>
    public static (bool Open, string? ExpiresAt) ListWindow(bool live, DateTime? lastInbound, DateTime nowUtc)
    {
        if (!live) return (true, null);
        if (lastInbound is not DateTime raw) return (false, null);
        var expires = DateTime.SpecifyKind(raw, DateTimeKind.Utc).AddHours(StandardHours);
        return (nowUtc < expires, expires.ToString("o"));
    }
}
