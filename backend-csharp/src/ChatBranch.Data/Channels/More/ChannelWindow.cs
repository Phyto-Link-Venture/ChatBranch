using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>A platform's reply rule: how long after the customer's last message we may write.</summary>
/// <param name="Hours">Anything may be sent within this many hours of the customer's last message.</param>
/// <param name="HumanHours">0 = no extension; else a PERSON may still reply up to this many hours (Meta's HUMAN_AGENT pattern).</param>
public sealed record WindowRule(string Channel, string Label, int Hours, int HumanHours = 0);

/// <summary>
/// The reply-window registry for the channels added after Messenger / Instagram
/// (which keep <see cref="SocialWindow"/>): the same derive-at-read-time rule —
/// the newest INBOUND row's timestamp, only binding a LIVE account (a sandbox
/// thread never dead-ends) — with per-platform hours:
/// <list type="bullet">
///   <item>WeChat Official Account — 48 h (Customer Service messages; errcode 45015 after that);</item>
///   <item>Zalo OA — 7 days (consultation messages to users who interacted recently);</item>
///   <item>TikTok Business Messaging — 48 h (partner docs; unverified against the live API);</item>
///   <item>Douyin enterprise DMs — 48 h (unverified against the live API).</item>
/// </list>
/// LINE, Viber and SMS have no window. Enforced by <see cref="OutboundSender.SocialWindowBlock"/>
/// (and so by the send endpoints' 409, the AI gate's send-time block and the queue)
/// and again on the send path itself.
/// </summary>
public static class ChannelWindow
{
    private static readonly Dictionary<string, WindowRule> Rules = new()
    {
        [ChannelIds.WeChat] = new(ChannelIds.WeChat, "WeChat", 48),
        [ChannelIds.Zalo] = new(ChannelIds.Zalo, "Zalo", 7 * 24),
        [ChannelIds.TikTok] = new(ChannelIds.TikTok, "TikTok", 48),
        [ChannelIds.Douyin] = new(ChannelIds.Douyin, "Douyin", 48),
    };

    /// <summary>The windowed channel ids (an array, so EF can translate a Contains).</summary>
    public static readonly string[] Channels = Rules.Keys.ToArray();

    public static bool Applies(string? channel) => channel != null && Rules.ContainsKey(channel);

    public static WindowRule? Rule(string? channel) =>
        channel != null && Rules.TryGetValue(channel, out var r) ? r : null;

    /// <summary>An account that would send for real on this channel.</summary>
    public static bool Live(ChannelAccount? account, string channel) =>
        account != null && Applies(channel) && account.Channel == channel && MoreChannels.Configured(account);

    public static DateTime? LastInbound(AppDbContext db, Thread thread) => SocialWindow.LastInbound(db, thread);

    public static SocialWindowState StateAt(string channel, DateTime? lastInbound, DateTime nowUtc)
    {
        var rule = Rule(channel);
        if (rule == null) return SocialWindowState.Open;
        if (lastInbound is not DateTime raw) return SocialWindowState.Closed;
        var ts = DateTime.SpecifyKind(raw, DateTimeKind.Utc);
        if (nowUtc < ts.AddHours(rule.Hours)) return SocialWindowState.Open;
        if (rule.HumanHours > 0 && nowUtc < ts.AddHours(rule.HumanHours)) return SocialWindowState.HumanAgentOnly;
        return SocialWindowState.Closed;
    }

    private static string Span(int hours) => hours % 24 == 0 && hours >= 72 ? $"{hours / 24}-day" : $"{hours}-hour";

    /// <summary>Why this send may not go out (null = it may).</summary>
    public static string? Refusal(string channel, SocialWindowState state, bool humanAgent, bool everWrote = true)
    {
        var rule = Rule(channel);
        if (rule == null) return null;
        return state switch
        {
            SocialWindowState.Open => null,
            SocialWindowState.HumanAgentOnly when humanAgent => null,
            SocialWindowState.HumanAgentOnly =>
                $"{rule.Label}'s {Span(rule.Hours)} window has closed — AI and automated messages can't be sent " +
                "until the customer writes again. A person can still reply from the inbox for a while.",
            _ when !everWrote =>
                $"{rule.Label} only allows replies after the customer has written to you — nothing was sent.",
            _ => $"{rule.Label}'s {Span(rule.HumanHours > 0 ? rule.HumanHours : rule.Hours)} reply window has " +
                 "closed — nothing can be sent until the customer writes again.",
        };
    }

    /// <summary>The same payload keys as <see cref="SocialWindow.Fields"/> (not live → open, no expiry).</summary>
    public static Dictionary<string, object?> Fields(string channel, bool live, DateTime? lastInbound, DateTime nowUtc)
    {
        var rule = Rule(channel);
        if (!live || rule == null)
            return new()
            {
                ["window_open"] = true, ["window_expires_at"] = null,
                ["human_window_open"] = true, ["human_window_expires_at"] = null,
            };
        var state = StateAt(channel, lastInbound, nowUtc);
        DateTime? ts = lastInbound is DateTime raw ? DateTime.SpecifyKind(raw, DateTimeKind.Utc) : null;
        var humanEnd = rule.HumanHours > 0 ? rule.HumanHours : rule.Hours;
        return new()
        {
            ["window_open"] = state == SocialWindowState.Open,
            ["window_expires_at"] = ts?.AddHours(rule.Hours).ToString("o"),
            ["human_window_open"] = state != SocialWindowState.Closed,
            ["human_window_expires_at"] = ts?.AddHours(humanEnd).ToString("o"),
        };
    }

    /// <summary>The window for a list row: (open, expires-at). Not live → open.</summary>
    public static (bool Open, string? ExpiresAt) ListWindow(string channel, bool live, DateTime? lastInbound,
        DateTime nowUtc)
    {
        var rule = Rule(channel);
        if (!live || rule == null) return (true, null);
        if (lastInbound is not DateTime raw) return (false, null);
        var expires = DateTime.SpecifyKind(raw, DateTimeKind.Utc).AddHours(rule.Hours);
        return (nowUtc < expires, expires.ToString("o"));
    }

    /// <summary>Live accounts on windowed channels, for list builders that check many threads.</summary>
    public static Dictionary<int, ChannelAccount> LiveAccounts(AppDbContext db) =>
        db.ChannelAccounts.AsNoTracking()
            .Where(a => Channels.Contains(a.Channel)).ToList()
            .Where(MoreChannels.Configured)
            .ToDictionary(a => a.Id);
}
