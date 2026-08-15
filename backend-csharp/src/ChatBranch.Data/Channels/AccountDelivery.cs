using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Per-account delivery + AI settings. The knobs that used to live only as
/// organization-wide Setting rows (Settings → AI Configuration) — quiet hours,
/// send pacing, the voice-call LLM brain and STT engine — are now configured
/// ON THE CHANNEL ACCOUNT itself (Settings → Channels → edit account), stored
/// in <see cref="ChannelAccount.Config"/> next to <c>role</c>.
///
/// Resolution is account → entity/global Setting row → env → built-in default,
/// so an account with nothing set behaves exactly as before. Every read path
/// (queue drain, template send gate, call dispatch) goes through here so the
/// precedence lives in ONE place.
/// </summary>
public static class AccountDelivery
{
    public const string QuietStart = "quiet_start";
    public const string QuietEnd = "quiet_end";
    public const string MinGap = "queue_min_gap_s";
    public const string MaxGap = "queue_max_gap_s";
    public const string LlmProvider = "llm_provider";
    public const string SttProvider = "stt_provider";

    public const int DefaultMinGap = 10;
    public const int DefaultMaxGap = 30;

    /// <summary>Config keys the accounts API reads/writes for this feature.</summary>
    public static readonly string[] Keys = { QuietStart, QuietEnd, MinGap, MaxGap, LlmProvider, SttProvider };

    /// <summary>The raw per-account value ("" = not overridden, inherit).</summary>
    public static string Own(ChannelAccount? account, string key) =>
        account != null && account.Config.TryGetValue(key, out var v) ? (v?.ToString() ?? "").Trim() : "";

    /// <summary>Account override → Setting row (entity then global) → env.</summary>
    public static string Resolve(ChannelAccount? account, string key, ScopingService scoping)
    {
        var own = Own(account, key);
        if (own.Length > 0) return own;
        var v = scoping.SettingValue(key, account?.EntityId);
        if (v.Length > 0) return v;
        return Environment.GetEnvironmentVariable(key.ToUpperInvariant()) ?? "";
    }

    /// <summary>Jittered pacing window (min, max) in seconds for this account.</summary>
    public static (int min, int max) PacingGap(ChannelAccount? account, ScopingService scoping)
    {
        var min = int.TryParse(Resolve(account, MinGap, scoping), out var lo) ? lo : DefaultMinGap;
        var max = int.TryParse(Resolve(account, MaxGap, scoping), out var hi) ? hi : DefaultMaxGap;
        if (max < min) max = min;
        return (min, max);
    }

    /// <summary>The account's quiet-hours window as raw HH:MM strings ("" = none).</summary>
    public static (string start, string end) QuietWindow(ChannelAccount? account, ScopingService scoping) =>
        (Resolve(account, QuietStart, scoping), Resolve(account, QuietEnd, scoping));

    /// <summary>
    /// wa_config.in_quiet_hours — server-local HH:MM window that may wrap
    /// midnight; both empty (or unparsable) = never quiet.
    /// </summary>
    public static bool InQuietHours(ChannelAccount? account, ScopingService scoping, DateTime? now = null)
    {
        var (s, e) = QuietWindow(account, scoping);
        if (!TryParseHhMm(s, out var qs) || !TryParseHhMm(e, out var qe)) return false;
        var t = TimeOnly.FromDateTime(now ?? DateTime.Now);
        return qs <= qe ? t >= qs && t < qe : t >= qs || t < qe;
    }

    public static bool TryParseHhMm(string value, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return TimeOnly.TryParseExact(value.Trim(), new[] { "HH:mm", "H:mm" }, out time);
    }
}
