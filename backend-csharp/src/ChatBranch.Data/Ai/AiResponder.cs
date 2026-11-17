using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// How the AI answers a conversation like a person would, rather than firing
/// one reply per customer message:
///  * <b>wait for the customer to finish</b> — ONE queued ai_reply job per
///    thread whose not_before every new inbound pushes out (capped), with the
///    reply written at send time so a burst of messages gets one answer;
///  * <b>don't answer acknowledgements</b> — "ok", "thanks", a thumbs-up after an
///    answer needs no reply (optionally a short canned acknowledgement); with
///    reactions on (<c>ai_reactions</c>, a channel that has them) the AI reacts
///    instead of staying silent — 🙏 on thanks, ❤️ on an emoji, 👍 otherwise;
///  * <b>every reply passes the AI monitor</b> (<see cref="AiGate"/>).
/// Shared by the instant webhook path, the queue drain and the sandbox
/// /simulate path, so all three behave identically.
/// </summary>
public class AiResponder
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly AiComposer _composer;
    private readonly AiGate _gate;
    private readonly TimeProvider _clock;

    public AiResponder(AppDbContext db, ScopingService scoping, AiComposer composer, AiGate gate,
        TimeProvider? clock = null)
    {
        _db = db;
        _scoping = scoping;
        _composer = composer;
        _gate = gate;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>A customer message just arrived: replies still waiting for review are now stale.</summary>
    public void OnCustomerMessage(Thread thread)
    {
        try { _gate.ExpireForCustomerMessage(thread); }
        catch { /* never breaks the inbound path */ }
    }

    /// <summary>
    /// Debounce: get-or-create the thread's ONE queued ai_reply job and push its
    /// not_before to now + <paramref name="waitSeconds"/>, never beyond
    /// <paramref name="maxWaitSeconds"/> after the first message of the burst.
    /// </summary>
    public OutboundJob ScheduleReply(Thread thread, int waitSeconds, int maxWaitSeconds)
    {
        var now = Now;
        // THIS conversation's job — another company's conversation with the same
        // customer (same channel + address) keeps its own
        var store = new Stores.ThreadStore(_db, _scoping);
        var job = _db.OutboundJobs.Where(j => j.Kind == JobKind.AiReply
                && j.Status == JobStatus.Queued && j.Channel == thread.Channel && j.ToAddress == thread.Key)
            .ToList().FirstOrDefault(j => store.JobBelongsTo(j, thread));
        var target = now.AddSeconds(Math.Max(0, waitSeconds));
        if (job == null)
        {
            job = new OutboundJob
            {
                Channel = thread.Channel, Kind = JobKind.AiReply, ToAddress = thread.Key,
                Payload = new Dictionary<string, object?>
                {
                    ["wait_s"] = waitSeconds, [Stores.ThreadStore.JobThreadKey] = thread.Id,
                },
                ContactId = thread.ContactId, AccountId = thread.AccountId, Status = JobStatus.Queued,
                NotBefore = waitSeconds > 0 ? target : null, CreatedAt = now, UpdatedAt = now,
            };
            _db.OutboundJobs.Add(job);
            _db.SaveChanges();
            return job;
        }
        if (waitSeconds > 0)
        {
            var cap = job.CreatedAt.AddSeconds(Math.Max(waitSeconds, maxWaitSeconds));
            var next = target < cap ? target : cap;
            if (job.NotBefore == null || next > job.NotBefore) job.NotBefore = next;
            job.UpdatedAt = now;
            _db.SaveChanges();
        }
        return job;
    }

    /// <summary>
    /// Answer the thread now: guards, acknowledgement skip, compose, gate. Never
    /// throws — an AI failure must not break a webhook or a drain.
    /// </summary>
    public async Task<AiGateOutcome> RespondAsync(Thread thread, int? accountId = null, int? jobId = null,
        CancellationToken ct = default)
    {
        try
        {
            // cheap guards before spending tokens (the gate re-checks at send time)
            if (thread.OptedOut) return Out(AiGateStatus.Skipped, "The contact opted out.");
            if (!_gate.AiAllowed(thread)) return Out(AiGateStatus.Skipped, "AI is off for this conversation.");
            // a group has no contact by design (Channels/GroupChats.cs) — its AI tick is the gate
            if (thread.ContactId == null && thread.Channel != ChatBranch.Domain.Channels.Website
                && !ChatBranch.Data.Channels.GroupChats.IsGroup(thread))
                return Out(AiGateStatus.Skipped, "No linked contact.");

            var account = (accountId ?? thread.AccountId) is int aid
                ? _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == aid) : null;
            var policy = AiBehaviourSettings.Resolve(_scoping, thread.EntityId, account);

            var recent = _db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == thread.Id && !m.Private)
                .OrderByDescending(m => m.Id).Take(30).ToList();
            var lastReply = recent.FirstOrDefault(m => m.Direction == MessageDirection.Out
                && (m.Source == MessageSource.Ai || MessageSource.IsPerson(m.Source)) && m.Status != "failed");
            // a message the customer recalled ("delete for everyone") is never answered
            var unanswered = recent
                .Where(m => m.Direction == MessageDirection.In && m.RecalledAt == null
                            && (lastReply == null || m.Id > lastReply.Id))
                .ToList();
            if (unanswered.Count == 0) return Out(AiGateStatus.Skipped, "Nothing new to answer.");

            if (policy.AckSkip && lastReply != null
                && unanswered.All(m => Humanizer.IsAcknowledgement(m.Text))
                && !Humanizer.LeavesConversationOpen(lastReply.Text))
            {
                var ctx = new Dictionary<string, object?>
                {
                    ["answering"] = unanswered.OrderBy(m => m.Id).Select(m => (object?)new Dictionary<string, object?>
                    {
                        ["id"] = m.Id, ["text"] = m.Text, ["at"] = m.CreatedAt,
                    }).ToList(),
                    ["acknowledgement"] = true,
                };
                if (policy.AckText.Trim().Length == 0)
                {
                    // a reaction instead of silence — the way a person answers "ok thanks!"
                    var latest = unanswered.OrderByDescending(m => m.Id).First();
                    var emoji = policy.Reactions && HumanTouch.ReactionsPossible(thread.Channel, account)
                        ? MessageReactions.ForAi(thread.Channel, Humanizer.AckReaction(latest.Text)) : null;
                    if (emoji == null) return Out(AiGateStatus.Skipped, "Acknowledgement — no reply needed.");
                    ctx["reactions"] = AiReaction.ToMeta(new[] { new AiReaction(latest.Id, emoji) });
                    return _gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, "", ctx, accountId,
                        Humanize: false, Canned: true));
                }
                return _gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, policy.AckText, ctx,
                    accountId, Humanize: false, Canned: true));
            }

            var c = await _composer.ComposeReplyAsync(thread, ct);
            if (string.IsNullOrWhiteSpace(c.Text) && !c.HasReaction)
            {
                // never leave the customer in silence: a person picks it up, and the AI
                // stays on so their next message is still answered
                Handover.Mark(_db, thread, HandoverKinds.LowConfidence,
                    "The AI could not write a reply to the customer's last message "
                    + "(it returned no text). Please answer them.", "ai", standDown: false);
                return Out(AiGateStatus.Empty, "The AI produced no reply — handed over to a person.");
            }
            if (jobId is int jid) c.Meta["job_id"] = jid;
            return _gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, c.Text, c.Meta, accountId));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down is not the AI failing — no handover for it
            return Out(AiGateStatus.Empty, "AI reply cancelled.");
        }
        catch (Exception ex)
        {
            Handover.Mark(_db, thread, HandoverKinds.LowConfidence,
                $"The AI reply failed ({ex.Message}). Please answer the customer.", "ai", standDown: false);
            return Out(AiGateStatus.Empty, $"AI reply failed: {ex.Message}");
        }
    }

    private static AiGateOutcome Out(string status, string detail) =>
        new(status, 0, Array.Empty<int>(), detail);
}
