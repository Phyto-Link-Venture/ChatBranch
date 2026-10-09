using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// "What would happen in production" for a playground reply — the verdict the
/// real pipeline would reach, decided by the SAME pure rules it uses, with no
/// side effects: the AI controls (<see cref="AiEligibility"/>: master switch,
/// schedule, never-AI tags, the contact's AI mode, the chat's tick), the AI
/// monitor's review policy (<see cref="AiGate.Review"/> over
/// <see cref="AiGate.FlagReasons"/>) and the handover the gate would start
/// (<see cref="Handover.KindForAiDraft"/>).
/// </summary>
public static class AiPlayground
{
    public static class Verdicts
    {
        /// <summary>Sent straight away.</summary>
        public const string Send = "send";
        /// <summary>Held for a person (review policy) or recorded only (shadow mode).</summary>
        public const string Hold = "hold";
        /// <summary>A person takes over (the reply checks asked for one, or the reply says one will help).</summary>
        public const string Handover = "handover";
        /// <summary>A reply check blocked it, or the AI produced nothing.</summary>
        public const string Blocked = "blocked";
        /// <summary>The AI would not answer here at all (the AI controls say no).</summary>
        public const string AiOff = "ai_off";
    }

    /// <param name="Verdict">One of <see cref="Verdicts"/>.</param>
    /// <param name="Why">Plain words (English — server text is data).</param>
    /// <param name="Rule">ai_off: the AI-controls rule; hold: the review mode.</param>
    /// <param name="Kind">handover / blocked: the handover kind the gate would start.</param>
    public sealed record Production(string Verdict, string Why, string? Rule, string? Kind, string ReviewMode,
        bool Shadow, IReadOnlyList<string> Flags, AiVerdict Ai);

    /// <summary>
    /// The AI controls for the conversation the playground stands in for: an
    /// imported real conversation as it is (read only), else a brand-new one in
    /// this outlet — on <paramref name="account"/>'s inbox when one is known (its
    /// AI default stamped like a new thread's) — for this contact (or made-up
    /// customer: its tags, no AI mode).
    /// </summary>
    public static AiVerdict Eligibility(AppDbContext db, ScopingService sc, int? entityId, string channel,
        ChannelAccount? account, Contact? contact, Thread? real = null, DateTime? nowUtc = null)
    {
        try
        {
            if (real != null) return AiEligibility.Decide(db, sc, real, nowUtc);
            var probe = new Thread
            {
                EntityId = entityId, Channel = channel, AccountId = account?.Id,
                AiEnabled = InboxSettings.AiDefault(account) ?? true,
            };
            return new AiControls(db, sc, nowUtc).Decide(probe, account, contact?.AiMode, contact?.Tags);
        }
        catch
        {
            return new AiVerdict(false, AiRules.Error, "Off: the AI settings could not be read", true);
        }
    }

    /// <summary>
    /// The verdict for one generated reply. <paramref name="priorAiReplies"/> =
    /// the AI turns already in the transcript (the first-N review rule).
    /// </summary>
    public static Production Decide(AiBehaviour policy, AiVerdict ai, AiReplyResult r, int priorAiReplies)
    {
        var text = r.ShouldSend ? r.Text : r.Draft;
        var meta = r.ToMeta();
        var flags = string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : AiGate.FlagReasons(text, meta, policy, AiDraftKind.Reply, () => priorAiReplies);
        Production P(string verdict, string why, string? rule = null, string? kind = null, bool shadow = false) =>
            new(verdict, why, rule, kind, policy.ReviewMode, shadow, flags, ai);

        if (!ai.Enabled) return P(Verdicts.AiOff, ai.Reason, ai.Rule);
        if (r.Action is AiActions.Empty or AiActions.Error || string.IsNullOrWhiteSpace(text))
            return P(Verdicts.Blocked, r.Reason.Length > 0 ? $"The AI produced no reply: {r.Reason}" : "The AI produced no reply.");

        var review = AiGate.Review(policy, flags, r.Action, canned: false);
        if (review.Status == AiGateStatus.Shadow)
            return P(Verdicts.Hold, AiGate.ShadowReason, "shadow", shadow: true);
        var kind = Handover.KindForAiDraft(AiDraftKind.Reply, text, r.Action, canned: false);
        if (r.Action == AiActions.Block)
            return P(Verdicts.Blocked, $"A reply check blocked it: {r.Reason}. It waits for a person.", null, kind);
        if (r.Action == AiActions.Handover)
            return P(Verdicts.Handover, $"The reply checks asked for a person: {r.Reason}", null, kind);
        if (review.Status == AiGateStatus.Held)
            return P(Verdicts.Hold, string.Join("; ", review.Reasons), policy.ReviewMode);
        if (kind == HandoverKinds.AskedForHuman)
            return P(Verdicts.Handover, "Sent — and the chat is flagged for a person, because the reply says one will help.",
                null, kind);
        return P(Verdicts.Send, policy.ReviewMode == "review_flagged"
            ? "Nothing risky was found, so it is sent straight away."
            : "Sent straight away.");
    }
}
