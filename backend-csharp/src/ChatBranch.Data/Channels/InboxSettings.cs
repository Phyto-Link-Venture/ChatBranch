using Microsoft.EntityFrameworkCore;
using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// "Who answers it" for an inbox: the team that
/// owns new conversations arriving on a channel account, and whether the AI
/// replies on them by default. Stored in <see cref="ChannelAccount.Config"/>
/// next to <c>role</c> and the delivery knobs — no schema change, and an
/// account with neither key behaves exactly as before:
///
/// * <b>team_id</b> — new conversations on this inbox go to that team (and its
///   round-robin) BEFORE the entity's auto-assign team is considered. A team
///   that has since been deleted is ignored, never an error.
/// * <b>ai_default</b> — "on" / "off"; blank = inherit (WhatsApp: the account
///   role decides; other channels: AI on). Applied to a conversation when it is
///   first created, so an operator's later per-chat tick always wins.
/// * <b>ai_review_mode</b> — who checks the AI before it sends on this inbox
///   (auto | review_flagged | review_all | shadow); blank = inherit the
///   entity/organization AI-behaviour setting (AiBehaviourSettings reads it).
/// * <b>ai_enabled / ai_schedule / ai_schedule_mode / ai_never_tags</b> — this
///   inbox's own AI MASTER SWITCH (<see cref="ChatBranch.Data.Ai.AiControlKeys"/>),
///   the level below the entity. Unlike every other key here it can only ever
///   SUBTRACT: "off" silences this inbox and a schedule narrows its hours, while
///   "on" adds nothing, because the master switch's rule is "off anywhere up the
///   tree wins" (<see cref="ChatBranch.Data.Ai.AiInboxMaster"/>). Not to be
///   confused with <c>ai_default</c> above: that one is stamped on a conversation
///   when it is created, this one is a live gate on every message.
/// * <b>keep_recalled</b> — true: a message the customer recalls ("delete for
///   everyone" / unsend) stays readable, marked as deleted; absent: its words and
///   file are erased like on the customer's phone (<see cref="MessageRecalls"/>).
///   Only the channels that report a recall offer it (<see cref="MessageRecalls.Supports"/>).
/// </summary>
public static class InboxSettings
{
    public const string TeamKey = "team_id";
    public const string AiDefaultKey = "ai_default";
    public const string ReviewModeKey = ChatBranch.Data.Ai.AiBehaviourSettings.ReviewMode;
    public const string KeepRecalledKey = "keep_recalled";

    /// <summary>Does this inbox keep recalled messages (true) or erase them (false, the default)?</summary>
    public static bool KeepRecalled(ChannelAccount? account) =>
        account != null && account.Config.TryGetValue(KeepRecalledKey, out var raw) && raw != null
        && (raw is bool b ? b : raw.ToString()?.Trim().ToLowerInvariant() is "true" or "on" or "1");

    /// <summary>The inbox's own review mode, or "" when it inherits.</summary>
    public static string ReviewModeWire(ChannelAccount? account) =>
        account != null && account.Config.TryGetValue(ReviewModeKey, out var raw) && raw != null
            && ChatBranch.Data.Ai.AiBehaviourSettings.ReviewModes.Contains(raw.ToString()?.Trim().ToLowerInvariant() ?? "")
            ? raw.ToString()!.Trim().ToLowerInvariant()
            : "";

    /// <summary>The owning team id, or null when none is set.</summary>
    public static int? TeamId(ChannelAccount? account)
    {
        if (account == null || !account.Config.TryGetValue(TeamKey, out var raw) || raw == null) return null;
        return int.TryParse(raw.ToString(), out var id) && id > 0 ? id : null;
    }

    /// <summary>"on" → true, "off" → false, anything else → null (inherit).</summary>
    public static bool? AiDefault(ChannelAccount? account)
    {
        if (account == null || !account.Config.TryGetValue(AiDefaultKey, out var raw) || raw == null) return null;
        return raw.ToString()?.Trim().ToLowerInvariant() switch
        {
            "on" or "true" => true,
            "off" or "false" => false,
            _ => null,
        };
    }

    /// <summary>The wire value of <see cref="AiDefault"/>: "on" | "off" | "".</summary>
    public static string AiDefaultWire(ChannelAccount? account) => AiDefault(account) switch
    {
        true => "on",
        false => "off",
        null => "",
    };

    /// <summary>
    /// This inbox's OWN value for an AI behaviour key, or "" when it inherits.
    /// Only the keys in <see cref="ChatBranch.Data.Ai.AiBehaviourSettings"/> are
    /// meaningful — <c>AiBehaviourSettings.Resolve</c> reads exactly these from
    /// <see cref="ChannelAccount.Config"/> before the entity and organization rows.
    /// </summary>
    public static string BehaviourOwn(ChannelAccount? account, string key) =>
        account != null && account.Config.TryGetValue(key, out var raw) && raw != null
            ? (raw.ToString() ?? "").Trim()
            : "";

    /// <summary>Every AI behaviour key this inbox overrides, key → its own value.</summary>
    public static Dictionary<string, string> BehaviourOverrides(ChannelAccount? account)
    {
        var own = new Dictionary<string, string>();
        if (account == null) return own;
        foreach (var key in ChatBranch.Data.Ai.AiBehaviourSettings.Keys)
        {
            var v = BehaviourOwn(account, key);
            if (v.Length > 0) own[key] = v;
        }
        return own;
    }

    /// <summary>
    /// Set this inbox's own value for an AI behaviour key. A blank value REMOVES
    /// the key, so the inbox inherits the entity/organization value again.
    /// Returns true when the stored config actually changed. The caller saves.
    /// </summary>
    public static bool SetBehaviour(ChannelAccount account, string key, string? value)
    {
        if (!ChatBranch.Data.Ai.AiBehaviourSettings.ByKey.ContainsKey(key)) return false;
        var cfg = new Dictionary<string, object?>(account.Config);
        var before = BehaviourOwn(account, key);
        var want = (value ?? "").Trim();
        if (want.Length == 0) cfg.Remove(key); else cfg[key] = want;
        if (before == want) return false;
        account.Config = cfg;
        return true;
    }

    // ---- the AI master switch, per inbox -------------------------------------
    // The same storage as the behaviour overrides above, but NOT the same rule:
    // AiControls.InboxMaster only ever uses these to switch the AI OFF.

    /// <summary>
    /// This inbox's OWN value for an AI master-switch key
    /// (<see cref="ChatBranch.Data.Ai.AiControlKeys"/>), or "" when it inherits
    /// the entity / organization level.
    /// </summary>
    public static string MasterOwn(ChannelAccount? account, string key) =>
        ChatBranch.Data.Ai.AiControlKeys.All.Contains(key) ? BehaviourOwn(account, key) : "";

    /// <summary>Every master-switch key this inbox overrides, key → its own value.</summary>
    public static Dictionary<string, string> MasterOverrides(ChannelAccount? account)
    {
        var own = new Dictionary<string, string>();
        if (account == null) return own;
        foreach (var key in ChatBranch.Data.Ai.AiControlKeys.All)
        {
            var v = MasterOwn(account, key);
            if (v.Length > 0) own[key] = v;
        }
        return own;
    }

    /// <summary>
    /// Set this inbox's own master-switch value, normalized the way the Setting
    /// rows store it. A blank value (or "inherit") REMOVES the key, so the inbox
    /// follows the entity / organization switch again. Returns true when the
    /// stored config actually changed. The caller saves.
    ///
    /// Storing "on" is allowed and means "this inbox adds no restriction" — it
    /// can never answer against a level above that is off or out of hours.
    /// </summary>
    public static bool SetMaster(ChannelAccount account, string key, string? value)
    {
        if (!ChatBranch.Data.Ai.AiControlKeys.All.Contains(key)) return false;
        var raw = (value ?? "").Trim();
        var want = raw.Length == 0 || raw.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            ? ""
            : key switch
            {
                ChatBranch.Data.Ai.AiControlKeys.Master => ChatBranch.Data.Ai.AiMasterModes.Normalize(raw),
                ChatBranch.Data.Ai.AiControlKeys.ScheduleMode => ChatBranch.Data.Ai.AiScheduleModes.Normalize(raw),
                ChatBranch.Data.Ai.AiControlKeys.Schedule =>
                    ChatBranch.Data.Ai.AiSchedule.Parse(raw) is { IsEmpty: false } sch ? sch.ToJson() : "",
                _ => string.Join(", ", ChatBranch.Data.Ai.AiControls.SplitTags(raw)),
            };
        var before = MasterOwn(account, key);
        if (before == want) return false;
        var cfg = new Dictionary<string, object?>(account.Config);
        if (want.Length == 0) cfg.Remove(key); else cfg[key] = want;
        account.Config = cfg;
        return true;
    }

    /// <summary>
    /// Apply the request's inbox keys to the account's config. Each argument is
    /// null when the request did not carry that key (= leave it alone); an
    /// empty string clears it back to inherit. Returns an error message for a
    /// bad value, else null. The caller saves.
    /// </summary>
    public static string? Apply(AppDbContext db, ChannelAccount account, string? teamRaw, string? aiRaw,
        string? reviewRaw = null, string? keepRecalledRaw = null)
    {
        var cfg = new Dictionary<string, object?>(account.Config);
        if (teamRaw != null)
        {
            var t = teamRaw.Trim();
            if (t is "" or "0" or "null")
                cfg.Remove(TeamKey);
            else
            {
                if (!int.TryParse(t, out var teamId) || !db.Teams.Any(x => x.Id == teamId))
                    return "team_id must be an existing team (or empty for none).";
                cfg[TeamKey] = teamId.ToString();
            }
        }
        if (aiRaw != null)
        {
            var a = aiRaw.Trim().ToLowerInvariant();
            if (a is "" or "inherit" or "null")
                cfg.Remove(AiDefaultKey);
            else if (a is "on" or "true")
                cfg[AiDefaultKey] = "on";
            else if (a is "off" or "false")
                cfg[AiDefaultKey] = "off";
            else
                return "ai_default must be \"on\", \"off\" or empty (inherit).";
        }
        if (reviewRaw != null)
        {
            var r = reviewRaw.Trim().ToLowerInvariant();
            if (r is "" or "inherit" or "null")
                cfg.Remove(ReviewModeKey);
            else if (ChatBranch.Data.Ai.AiBehaviourSettings.ReviewModes.Contains(r))
                cfg[ReviewModeKey] = r;
            else
                return $"ai_review_mode must be one of {string.Join(", ", ChatBranch.Data.Ai.AiBehaviourSettings.ReviewModes)} or empty (inherit).";
        }
        if (keepRecalledRaw != null)
        {
            var k = keepRecalledRaw.Trim().ToLowerInvariant();
            if (k is "true" or "on" or "1")
                cfg[KeepRecalledKey] = true;
            else if (k is "" or "false" or "off" or "0" or "null")
                cfg.Remove(KeepRecalledKey);
            else
                return "keep_recalled must be true or false.";
        }
        account.Config = cfg;
        return null;
    }

    /// <summary>
    /// Unlink everything that points at an account before it is deleted, so
    /// its conversations SURVIVE the inbox (back in sandbox) instead of the
    /// delete failing on a foreign key — the schema has no ON DELETE clauses.
    /// </summary>
    public static async Task DetachAccountAsync(AppDbContext db, int accountId)
    {
        await db.Threads.Where(t => t.AccountId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AccountId, (int?)null));
        await db.Messages.Where(m => m.AccountId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.AccountId, (int?)null));
        await db.OutboundJobs.Where(j => j.AccountId == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.AccountId, (int?)null));
        await db.AccountAssignments.Where(a => a.AccountId == accountId).ExecuteDeleteAsync();
    }

    /// <summary>
    /// Stamp the inbox's AI default on a conversation that has just been
    /// created on it. Safe to call on every inbound: it only acts while the
    /// thread has no messages yet, so it never overrides an operator's tick.
    /// </summary>
    public static void ApplyToNewThread(AppDbContext db, ChatBranch.Domain.Thread thread)
    {
        try
        {
            if (thread.AccountId is not int accountId) return;
            if (db.Messages.Any(m => m.ThreadId == thread.Id)) return;
            var account = thread.Account?.Id == accountId
                ? thread.Account
                : db.ChannelAccounts.FirstOrDefault(a => a.Id == accountId);
            if (AiDefault(account) is not bool on || thread.AiEnabled == on) return;
            thread.AiEnabled = on;
            db.SaveChanges();
        }
        catch { /* a default must never break an inbound webhook */ }
    }
}

/// <summary>
/// The channel vocabulary a message template (CannedResponse) can be limited
/// to. Values are THREAD channel ids — what the inbox composer passes as
/// <c>?channel=</c> — so Messenger is stored as "facebook". Friendly aliases
/// are accepted on the way in.
/// </summary>
public static class TemplateChannels
{
    public static readonly string[] All =
    {
        Domain.Channels.WhatsApp, Domain.Channels.Telegram, Domain.Channels.Email,
        Domain.Channels.Facebook, Domain.Channels.Instagram, Domain.Channels.Website,
        Domain.Channels.Line, Domain.Channels.Viber, Domain.Channels.Sms, Domain.Channels.WeChat,
        Domain.Channels.TikTok, Domain.Channels.Douyin, Domain.Channels.Zalo,
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wa"] = Domain.Channels.WhatsApp,
        ["messenger"] = Domain.Channels.Facebook,
        ["fb"] = Domain.Channels.Facebook,
        ["ig"] = Domain.Channels.Instagram,
        ["mail"] = Domain.Channels.Email,
        ["web"] = Domain.Channels.Website,
        ["widget"] = Domain.Channels.Website,
    };

    /// <summary>Canonical id for a known channel (or alias), else null.</summary>
    public static string? Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        if (Aliases.TryGetValue(s, out var mapped)) return mapped;
        return All.Contains(s) ? s : null;
    }

    /// <summary>
    /// Validate a requested channel list. "all"/"*" anywhere, or every channel
    /// ticked, collapses to the empty list (= offered everywhere, including
    /// channels added later). Duplicates fold; order follows <see cref="All"/>.
    /// </summary>
    public static (List<string> Channels, string? Error) Parse(IEnumerable<string?> requested)
    {
        var set = new HashSet<string>();
        foreach (var raw in requested)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) continue;
            if (s is "all" or "*") return (new List<string>(), null);
            var ch = Normalize(s);
            if (ch == null)
                return (new List<string>(),
                    $"Unknown channel \"{s}\" — use any of {string.Join(", ", All)} (empty = all channels).");
            set.Add(ch);
        }
        if (set.Count == All.Length) return (new List<string>(), null);
        return (All.Where(set.Contains).ToList(), null);
    }

    /// <summary>Is a template with this channel list offered on <paramref name="channel"/>?</summary>
    public static bool Offers(IReadOnlyCollection<string> channels, string channel)
    {
        if (channels.Count == 0) return true;
        var ch = Normalize(channel) ?? channel.Trim().ToLowerInvariant();
        return channels.Contains(ch);
    }
}
