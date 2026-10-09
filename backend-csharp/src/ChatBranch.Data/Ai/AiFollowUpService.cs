using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Follow-ups for normal AI conversations that went quiet (flows have their
/// own "Follow up if no reply" step). Rules, all per conversation:
///  * only after the AI's last message asked something or left the conversation
///    open (ends with a question, or the AI marked it awaiting a reply);
///  * first follow-up after <c>ai_followup_first_hours</c>, later ones every
///    <c>ai_followup_between_hours</c>, at most <c>ai_followup_max</c> per silence;
///  * stops on any customer reply (the count restarts), opt-out, a resolved
///    conversation, AI switched off (a person took over) or a teammate replying;
///  * quiet hours postpone it; a closed WhatsApp 24h window uses the configured
///    re-engagement template, or skips with a note in the chat;
///  * the text is written by the AI and passes the AI monitor like any AI message.
///
/// The decision is recomputed from the conversation itself on every tick, so a
/// reply on any channel or send path is honoured without hooks.
/// </summary>
public class AiFollowUpService
{
    private const int HorizonDays = 30;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly AiComposer _composer;
    private readonly AiGate _gate;
    private readonly TimeProvider _clock;

    public AiFollowUpService(AppDbContext db, ScopingService scoping, ThreadStore threads,
        AiComposer composer, AiGate gate, TimeProvider? clock = null)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _composer = composer;
        _gate = gate;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private DateTime LocalNow => _clock.GetLocalNow().DateTime;

    public record TickResult(int Considered, int Submitted);

    /// <summary>One pass over recently active conversations. Never throws.</summary>
    public TickResult Tick(int maxSubmissions = 25)
    {
        var considered = 0;
        var submitted = 0;
        List<Thread> threads;
        try
        {
            var horizon = Now.AddDays(-HorizonDays);
            threads = _db.Threads.Include(t => t.Account)
                // a WhatsApp thread can be AI-on through its per-account tick or
                // role while the legacy column is off — the loop applies the real rule
                .Where(t => !t.OptedOut && (t.AiEnabled || t.AccountId != null
                                            || (t.Contact != null && t.Contact.AiMode == ContactAiModes.Always))
                            && t.Status != ThreadStatus.Resolved
                            && t.UpdatedAt >= horizon)
                .OrderBy(t => t.UpdatedAt).Take(500).ToList();
        }
        catch { return new TickResult(0, 0); }

        foreach (var t in threads)
        {
            if (submitted >= maxSubmissions) break;
            try
            {
                var policy = AiBehaviourSettings.Resolve(_scoping, t.EntityId, t.Account);
                if (!policy.FollowUpEnabled || policy.FollowUpMax <= 0) continue;
                if (!_gate.AiAllowed(t)) continue;   // the effective tick (AiEligibility)
                if (t.ContactId == null && t.Channel != ChatBranch.Domain.Channels.Website) continue; // coverage gate
                considered++;
                if (Consider(t, policy)) submitted++;
            }
            catch { /* one bad conversation never stops the pass */ }
        }
        return new TickResult(considered, submitted);
    }

    /// <summary>Decide for one conversation; true when a follow-up was submitted.</summary>
    private bool Consider(Thread t, AiBehaviour policy)
    {
        var now = Now;
        var msgs = _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == t.Id && !m.Private)
            .OrderByDescending(m => m.Id).Take(40).ToList();
        var lastIn = msgs.FirstOrDefault(m => m.Direction == MessageDirection.In);
        var state = State(t.Id);

        // any customer message restarts the sequence
        if (state.AnchorMessageId != lastIn?.Id)
        {
            state.AnchorMessageId = lastIn?.Id;
            state.FollowUpsSent = 0;
            state.LastFollowUpAt = null;
            state.NextFollowUpAt = null;
            state.Note = "";
        }

        var after = msgs.Where(m => m.Direction == MessageDirection.Out && (lastIn == null || m.Id > lastIn.Id))
            .OrderBy(m => m.Id).ToList();
        if (after.Count == 0) return Done(state, null, ""); // the customer spoke last
        if (after.Any(m => m.Source == MessageSource.Agent))
            return Done(state, null, "A teammate replied — follow-ups stopped.");
        if (state.FollowUpsSent >= policy.FollowUpMax)
            return Done(state, null, state.Note.Length > 0 ? state.Note : "All follow-ups sent.");
        if (_db.AiDrafts.Any(d => d.ThreadId == t.Id && d.Kind == AiDraftKind.FollowUp
                                  && d.Status == AiDraftStatus.Pending))
            return Done(state, state.NextFollowUpAt, "A follow-up is waiting for review.");

        DateTime due;
        if (state.FollowUpsSent == 0)
        {
            var anchor = after.LastOrDefault(m => m.Source == MessageSource.Ai);
            if (anchor == null) return Done(state, null, "");
            if (!LeftOpen(t, anchor))
                return Done(state, null, "The last AI message didn't ask anything — no follow-up needed.");
            due = anchor.CreatedAt.AddHours(policy.FollowUpFirstHours);
        }
        else
        {
            due = (state.LastFollowUpAt ?? now).AddHours(policy.FollowUpBetweenHours);
        }
        if (now < due) return Done(state, due, "");

        var account = t.Account;
        if (AccountDelivery.InQuietHours(account, _scoping, LocalNow))
            return Done(state, NextAllowed(account, now), "Waiting for quiet hours to end.");

        var attempt = state.FollowUpsSent + 1;
        if (_gate.WindowClosed(t, account?.Id))
        {
            if (policy.FollowUpTemplate.Length > 0)
            {
                _gate.Submit(new AiSubmission(t, AiDraftKind.FollowUp,
                    $"[template: {policy.FollowUpTemplate}]",
                    new Dictionary<string, object?> { ["attempt"] = attempt, ["reason"] = "window_closed" },
                    account?.Id, TemplateName: policy.FollowUpTemplate, Humanize: false));
                Advance(state, policy, now, "WhatsApp window closed — sent the re-engagement template.");
                return true;
            }
            state.FollowUpsSent = policy.FollowUpMax;
            _threads.AppendMessage(t, MessageDirection.Out, MessageSource.System,
                "AI follow-up skipped: the WhatsApp 24-hour window has closed and no re-engagement "
                + "template is set (AI monitor → Settings).", status: "note", priv: true);
            return Done(state, null, "Skipped: WhatsApp 24-hour window closed and no re-engagement template is set.");
        }

        var c = _composer.ComposeFollowUpAsync(t, attempt, policy.FollowUpGuidance).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(c.Text)) return Done(state, due, "The AI produced no follow-up.");
        c.Meta["attempt"] = attempt;
        c.Meta["after_message_id"] = after[^1].Id;
        var outcome = _gate.Submit(new AiSubmission(t, AiDraftKind.FollowUp, c.Text, c.Meta, account?.Id));
        Advance(state, policy, now, outcome.Status switch
        {
            AiGateStatus.Held => "Follow-up waiting for review.",
            AiGateStatus.Shadow => "Follow-up drafted (shadow mode).",
            AiGateStatus.Blocked => $"Follow-up blocked: {outcome.Detail}",
            _ => $"Follow-up {attempt} of {policy.FollowUpMax} sent.",
        });
        return true;
    }

    /// <summary>Did the AI's last message leave the conversation open?</summary>
    private bool LeftOpen(Thread t, Message anchor)
    {
        if (Humanizer.LeavesConversationOpen(anchor.Text)) return true;
        // a split reply: judge the whole draft, and honour an explicit "awaiting reply" mark
        var draft = _db.AiDrafts.AsNoTracking()
            .Where(d => d.ThreadId == t.Id && d.SentAt != null && d.Kind != AiDraftKind.FollowUp)
            .OrderByDescending(d => d.Id).FirstOrDefault();
        if (draft == null) return false;
        return AiGate.Bool(draft.Context, "awaiting_reply") == true
               || Humanizer.LeavesConversationOpen(draft.Content);
    }

    private void Advance(AiConversationState state, AiBehaviour policy, DateTime now, string note)
    {
        state.FollowUpsSent += 1;
        state.LastFollowUpAt = now;
        state.NextFollowUpAt = state.FollowUpsSent < policy.FollowUpMax
            ? now.AddHours(policy.FollowUpBetweenHours) : null;
        state.Note = note;
        state.UpdatedAt = now;
        _db.SaveChanges();
    }

    private bool Done(AiConversationState state, DateTime? next, string note)
    {
        state.NextFollowUpAt = next;
        state.Note = note;
        state.UpdatedAt = Now;
        _db.SaveChanges();
        return false;
    }

    /// <summary>The first moment (15-minute steps, up to 2 days) outside quiet hours.</summary>
    private DateTime? NextAllowed(ChannelAccount? account, DateTime nowUtc)
    {
        var local = LocalNow;
        for (var step = 1; step <= 192; step++)
        {
            var probe = local.AddMinutes(15 * step);
            if (!AccountDelivery.InQuietHours(account, _scoping, probe))
                return nowUtc.AddMinutes(15 * step);
        }
        return null;
    }

    private AiConversationState State(int threadId)
    {
        var s = _db.AiConversationStates.FirstOrDefault(x => x.ThreadId == threadId);
        if (s != null) return s;
        s = new AiConversationState { ThreadId = threadId, UpdatedAt = Now, AnchorMessageId = -1 };
        _db.AiConversationStates.Add(s);
        return s;
    }

    /// <summary>Follow-up state for the monitor / chat panel (null when none tracked).</summary>
    public AiConversationState? StateFor(int threadId) =>
        _db.AiConversationStates.AsNoTracking().FirstOrDefault(x => x.ThreadId == threadId);
}
