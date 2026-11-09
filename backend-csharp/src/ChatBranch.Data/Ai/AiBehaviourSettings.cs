using System.Globalization;
using ChatBranch.Domain;

namespace ChatBranch.Data.Ai;

/// <summary>
/// The effective AI review + behaviour policy for one conversation. Built by
/// <see cref="AiBehaviourSettings.Resolve"/>; every AI send path reads this one
/// record, so the precedence rule lives in a single place.
/// </summary>
public sealed record AiBehaviour(
    string ReviewMode,
    int AutosendMinutes,
    int FirstN,
    IReadOnlyList<string> Keywords,
    bool NotifyReviewers,
    int WaitSeconds,
    int WaitMaxSeconds,
    bool TypingEnabled,
    double TypingCps,
    int TypingMinSeconds,
    int TypingMaxSeconds,
    string SplitMode,
    int SplitMaxChars,
    bool AckSkip,
    string AckText,
    bool FollowUpEnabled,
    double FollowUpFirstHours,
    double FollowUpBetweenHours,
    int FollowUpMax,
    string FollowUpTemplate,
    string FollowUpGuidance,
    string EmojiStyle = "light",
    bool Reactions = true,
    bool QuoteReplies = true,
    string VoiceReplies = "off",
    string VoiceId = "");

/// <summary>
/// Setting keys for the AI monitor (review policy) and the human-like
/// conversation behaviour, with plain-language labels the settings page
/// renders straight from <see cref="Fields"/> — the catalog and the engine can
/// never drift.
///
/// Resolution: channel-account override (<c>ChannelAccount.Config[key]</c>) →
/// entity Setting row → organization Setting row → built-in default. Every key
/// is in <see cref="ScopingService.EntityScopedSettings"/>.
///
/// The built-in defaults for the "act like a person" knobs (wait, typing,
/// splitting, acknowledgement skipping) are ON; env <c>AI_HUMANIZE=0</c> turns
/// those DEFAULTS off (explicit Setting rows still win) — the test host uses it
/// so the sandbox replies inline and assertions stay deterministic.
/// </summary>
public static class AiBehaviourSettings
{
    public record Field(string Key, string Group, string Type, string Default, string Label,
        string Help, string[]? Options = null, double? Min = null, double? Max = null,
        bool Humanize = false);

    public const string ReviewMode = "ai_review_mode";
    public const string AutosendMinutes = "ai_review_autosend_minutes";
    public const string FirstN = "ai_review_first_n";
    public const string Keywords = "ai_review_keywords";
    public const string Notify = "ai_review_notify";
    public const string WaitSeconds = "ai_wait_seconds";
    public const string WaitMaxSeconds = "ai_wait_max_seconds";
    public const string Typing = "ai_typing";
    public const string TypingCps = "ai_typing_cps";
    public const string TypingMin = "ai_typing_min_s";
    public const string TypingMax = "ai_typing_max_s";
    public const string Split = "ai_split";
    public const string SplitMaxChars = "ai_split_max_chars";
    public const string AckSkip = "ai_ack_skip";
    public const string AckText = "ai_ack_text";
    public const string FollowUp = "ai_followup";
    public const string FollowUpFirstHours = "ai_followup_first_hours";
    public const string FollowUpBetweenHours = "ai_followup_between_hours";
    public const string FollowUpMax = "ai_followup_max";
    public const string FollowUpTemplate = "ai_followup_template";
    public const string FollowUpGuidance = "ai_followup_guidance";
    /// <summary>How feedback becomes lessons (Ai/Learning/LearningService.cs).</summary>
    public const string LearningMode = "ai_learning_mode";
    /// <summary>Human-like touches (Ai/HumanTouch.cs): emoji in the words, reactions, quoted replies.</summary>
    public const string EmojiStyle = "ai_emoji_style";
    public const string Reactions = "ai_reactions";
    public const string QuoteReplies = "ai_quote_replies";
    /// <summary>AI voice replies (Ai/Voice/VoiceReplies.cs): off | mirror | always + an ElevenLabs voice id.</summary>
    public const string VoiceReplies = "ai_voice_replies";
    public const string VoiceId = "ai_voice_id";

    public static readonly string[] ReviewModes = { "auto", "review_flagged", "review_all", "shadow" };
    public static readonly string[] SplitModes = { "off", "auto", "always" };
    public static readonly string[] EmojiStyles = { "off", "light", "expressive" };
    public static readonly string[] QuoteModes = { "auto", "off" };
    public static readonly string[] VoiceModes = { "off", "mirror", "always" };
    public static readonly string[] LearningModes = { "review", "auto", "off" };

    public const string DefaultKeywords =
        "price, cost, discount, refund, payment, invoice, deposit, fee, free, guarantee, "
        + "promise, contract, deadline, appointment, booking, cancel";

    public static readonly Field[] Fields =
    {
        // ---- review (the AI monitor) ----------------------------------------
        new(ReviewMode, "review", "select", "auto", "Who checks the AI before it sends",
            "Send straight away, hold only replies that look risky, hold everything for a person, "
            + "or shadow mode (the AI writes drafts but never sends — for trialling it safely).",
            ReviewModes),
        new(AutosendMinutes, "review", "number", "0", "Send held replies automatically after (minutes)",
            "0 means a held reply waits for a person. Any other number sends it on its own once "
            + "that many minutes pass with nobody acting.", Min: 0, Max: 1440),
        new(FirstN, "review", "number", "2", "Review the first replies in every conversation",
            "In \"review risky replies\" mode, the first N AI replies of each conversation are held "
            + "so a person sees how the AI opens.", Min: 0, Max: 50),
        new(Keywords, "review", "text", DefaultKeywords, "Words that make a reply risky",
            "Comma-separated. A reply mentioning one of these — or a money amount or a date — is "
            + "held in \"review risky replies\" mode."),
        new(Notify, "review", "bool", "true", "Notify reviewers when a reply is waiting",
            "A bell notification to the conversation's assignee and the admins — one unread alert "
            + "per person that newer held replies refresh, so it never floods."),

        // ---- wait for the customer to finish --------------------------------
        new(WaitSeconds, "wait", "number", "25", "Wait for the customer to finish (seconds)",
            "Hold off this long after the customer's last message before writing, so several short "
            + "messages get one answer. Each new message restarts the wait. 0 replies at once.",
            Min: 0, Max: 600, Humanize: true),
        new(WaitMaxSeconds, "wait", "number", "120", "…but never wait longer than (seconds)",
            "However many messages keep arriving, the reply is written no later than this.",
            Min: 0, Max: 1800),
        new(AckSkip, "wait", "bool", "true", "Acknowledgements don't need a reply",
            "When the customer only says \"ok\", \"thanks\" or sends a thumbs-up after an answer, "
            + "the AI stays quiet instead of replying to it.", Humanize: true),
        new(AckText, "wait", "text", "", "Optional short acknowledgement",
            "If set, sent instead of a full reply to an acknowledgement (for example \"Noted, thanks!\"). "
            + "Leave blank to stay silent — or to react with an emoji when reactions are on."),

        // ---- typing like a person -------------------------------------------
        new(Typing, "typing", "bool", "true", "Show typing and pause before sending",
            "Shows \"typing…\" where the channel supports it (WhatsApp) and waits in proportion "
            + "to the reply's length.", Humanize: true),
        new(TypingCps, "typing", "number", "8", "Typing speed (characters per second)",
            "Higher is faster. 8 reads like a quick typist.", Min: 1, Max: 100),
        new(TypingMin, "typing", "number", "2", "Shortest pause (seconds)", "", Min: 0, Max: 60),
        new(TypingMax, "typing", "number", "12", "Longest pause (seconds)", "", Min: 0, Max: 120),

        // ---- split long replies ---------------------------------------------
        new(Split, "split", "select", "auto", "Split long replies into shorter messages",
            "Off sends one message. Auto splits only replies longer than the limit. Always splits "
            + "at paragraph breaks. Never more than 3 messages, never mid-sentence.",
            SplitModes, Humanize: true),
        new(SplitMaxChars, "split", "number", "320", "Longest single message (characters)", "",
            Min: 80, Max: 4000),

        // ---- human-like touches: emoji, reactions, quoted replies ------------
        new(EmojiStyle, "human", "select", "light", "Emoji in replies",
            "Off: no emoji. Light: an occasional emoji where a person would use one — at most one per message, "
            + "never in a complaint or an apology, and few when the customer uses none. Expressive: warmer and "
            + "a little more often. Email never gets emoji, and a persona that says otherwise wins.",
            EmojiStyles, Humanize: true),
        new(Reactions, "human", "bool", "true", "React to the customer's messages",
            "On WhatsApp and Telegram the AI may react to the customer's latest message — 👍 on \"ok\", 🙏 on "
            + "\"thanks\", ❤️ on a photo — at most one reaction per reply. A reaction alone can answer a simple "
            + "acknowledgement instead of silence. It is added when the reply is sent, never while it is held.",
            Humanize: true),
        new(QuoteReplies, "human", "select", "auto", "Quote the message being answered",
            "Auto: when the customer sent several messages at once, or the answer refers to an older message, "
            + "the reply quotes the message it answers (WhatsApp and Telegram). Off: never quote.",
            QuoteModes, Humanize: true),
        new(VoiceReplies, "human", "select", "off", "Reply with voice notes",
            "Needs an ElevenLabs API key (Settings → AI keys & models). Mirror: when the customer's latest message "
            + "was a voice note, the AI answers with one. Always: every reply is a voice note. The words are kept "
            + "on the message for the team. Works on WhatsApp (QR numbers, Cloud API, YCloud), Telegram, Messenger "
            + "and Instagram; everywhere else — and when a reply has a link, is very long, or the voice can't be "
            + "made — it goes as text.", VoiceModes),
        new(VoiceId, "human", "text", "", "Voice for voice replies",
            "Optional. A voice from the voice library (its id, e.g. a clone — ElevenLabs or VoiceStudio) or a raw "
            + "ElevenLabs voice ID. Empty uses the organization's call voice when it is an ElevenLabs or VoiceStudio "
            + "voice, else ElevenLabs' default voice (or VoiceStudio's when only VoiceStudio is set up)."),

        // ---- follow-ups when the customer goes quiet ------------------------
        new(FollowUp, "followup", "bool", "false", "Follow up when the customer goes quiet",
            "Only after the AI's last message asked something or left the conversation open. Stops "
            + "as soon as the customer replies, opts out, the conversation is resolved or a person "
            + "takes over."),
        new(FollowUpFirstHours, "followup", "number", "4", "First follow-up after (hours)", "",
            Min: 0.05, Max: 720),
        new(FollowUpBetweenHours, "followup", "number", "24", "Then wait between follow-ups (hours)", "",
            Min: 0.05, Max: 720),
        new(FollowUpMax, "followup", "number", "2", "Most follow-ups per silence", "", Min: 0, Max: 10),
        new(FollowUpTemplate, "followup", "text", "",
            "Re-engagement template for closed WhatsApp windows",
            "WhatsApp only allows free text within 24 hours of the customer's last message. After "
            + "that a follow-up uses this approved template, or is skipped (and noted in the chat) "
            + "when none is set."),
        new(FollowUpGuidance, "followup", "textarea", "", "Guidance for follow-up messages",
            "Optional instructions for how follow-ups should sound, e.g. \"Keep it to one friendly "
            + "line and offer to call\"."),

        // ---- learning from feedback (lessons) --------------------------------
        new(LearningMode, "learning", "select", "review", "Learn from feedback",
            "Thumbs up/down, approvals and edits suggest lessons the AI uses in later replies. Review: "
            + "every lesson waits for a person to approve it. Auto: lessons from admins' feedback go live "
            + "at once (everyone else's still wait). Off: feedback is only counted, nothing is learned.",
            LearningModes),
    };

    public static readonly Dictionary<string, Field> ByKey = Fields.ToDictionary(f => f.Key);

    public static IEnumerable<string> Keys => Fields.Select(f => f.Key);

    /// <summary>Env AI_HUMANIZE=0 switches the human-like DEFAULTS off.</summary>
    private static bool HumanizeDefaultsOff =>
        Environment.GetEnvironmentVariable("AI_HUMANIZE") is "0" or "false" or "off";

    /// <summary>The built-in default for a key (honours AI_HUMANIZE=0).</summary>
    public static string DefaultFor(string key)
    {
        if (!ByKey.TryGetValue(key, out var f)) return "";
        if (f.Humanize && HumanizeDefaultsOff)
            return f.Type == "bool" ? "false" : f.Type == "select" ? "off" : "0";
        return f.Default;
    }

    /// <summary>
    /// The policy for a conversation: account override → entity row → global row →
    /// default. Never throws — a broken settings table degrades to defaults.
    /// </summary>
    public static AiBehaviour Resolve(ScopingService scoping, int? entityId, ChannelAccount? account = null)
    {
        Dictionary<string, string> stored;
        try { stored = scoping.SettingValues(Keys, entityId); }
        catch { stored = new Dictionary<string, string>(); }

        string S(string key)
        {
            var own = account != null && account.Config.TryGetValue(key, out var v)
                ? (v?.ToString() ?? "").Trim() : "";
            if (own.Length > 0) return own;
            return stored.TryGetValue(key, out var s) && s.Length > 0 ? s : DefaultFor(key);
        }
        int I(string key) => ParseInt(S(key), ParseInt(DefaultFor(key), 0));
        double D(string key) => ParseDouble(S(key), ParseDouble(DefaultFor(key), 0));
        bool B(string key) => ParseBool(S(key));

        var mode = S(ReviewMode).ToLowerInvariant();
        if (!ReviewModes.Contains(mode)) mode = "auto";
        var split = S(Split).ToLowerInvariant();
        if (!SplitModes.Contains(split)) split = "off";
        var min = Math.Max(0, I(TypingMin));
        var max = Math.Max(min, I(TypingMax));
        var emoji = S(EmojiStyle).ToLowerInvariant();
        if (!EmojiStyles.Contains(emoji)) emoji = DefaultFor(EmojiStyle);

        return new AiBehaviour(
            ReviewMode: mode,
            AutosendMinutes: Math.Max(0, I(AutosendMinutes)),
            FirstN: Math.Max(0, I(FirstN)),
            Keywords: ParseList(S(Keywords)),
            NotifyReviewers: B(Notify),
            WaitSeconds: Math.Max(0, I(WaitSeconds)),
            WaitMaxSeconds: Math.Max(0, I(WaitMaxSeconds)),
            TypingEnabled: B(Typing),
            TypingCps: Math.Max(0.5, D(TypingCps)),
            TypingMinSeconds: min,
            TypingMaxSeconds: max,
            SplitMode: split,
            SplitMaxChars: Math.Max(40, I(SplitMaxChars)),
            AckSkip: B(AckSkip),
            AckText: S(AckText),
            FollowUpEnabled: B(FollowUp),
            FollowUpFirstHours: Math.Max(0, D(FollowUpFirstHours)),
            FollowUpBetweenHours: Math.Max(0, D(FollowUpBetweenHours)),
            FollowUpMax: Math.Max(0, I(FollowUpMax)),
            FollowUpTemplate: S(FollowUpTemplate),
            FollowUpGuidance: S(FollowUpGuidance),
            EmojiStyle: emoji,
            Reactions: B(Reactions),
            QuoteReplies: S(QuoteReplies).ToLowerInvariant() != "off",
            VoiceReplies: VoiceModes.Contains(S(VoiceReplies).ToLowerInvariant()) ? S(VoiceReplies).ToLowerInvariant() : "off",
            VoiceId: S(VoiceId));
    }

    /// <summary>
    /// Validate + normalize one value for storage. Returns an error message for
    /// operator input that does not fit the field, null when it is fine.
    /// </summary>
    public static string? Normalize(string key, string? raw, out string value)
    {
        value = (raw ?? "").Trim();
        if (!ByKey.TryGetValue(key, out var f)) return $"Unknown setting '{key}'.";
        switch (f.Type)
        {
            case "bool":
                value = ParseBool(value) ? "true" : "false";
                return null;
            case "select":
                value = value.ToLowerInvariant();
                return f.Options!.Contains(value)
                    ? null : $"{f.Label}: must be one of {string.Join(", ", f.Options!)}.";
            case "number":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    return $"{f.Label}: must be a number.";
                if (f.Min is double lo && n < lo) return $"{f.Label}: must be at least {lo}.";
                if (f.Max is double hi && n > hi) return $"{f.Label}: must be at most {hi}.";
                value = n.ToString(CultureInfo.InvariantCulture);
                return null;
            default:
                if (value.Length > 2000) return $"{f.Label}: keep it under 2000 characters.";
                return null;
        }
    }

    public static bool ParseBool(string? v) =>
        (v ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    private static int ParseInt(string? v, int fallback) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? (int)Math.Round(d) : fallback;

    private static double ParseDouble(string? v, double fallback) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    public static List<string> ParseList(string? raw) =>
        (raw ?? "").Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(k => k.Trim()).Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
