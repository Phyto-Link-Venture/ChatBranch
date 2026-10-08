using System.Text.Json;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Flows;

/// <summary>
/// The outbound pacing queue (port of flows/queue.py). Jobs are drained with
/// human-like pacing (jittered per-account gap) and caps, and each job is
/// CLAIMED ATOMICALLY (queued→sending guarded UPDATE) so two tickers can never
/// double-send the same job — the exact bug the Python fought. Account is
/// resolved at SEND time. In sandbox mode a send stores the message locally.
///
/// AI traffic goes through the AI monitor here too:
///  * <c>ai_reply</c> jobs (the debounced "wait for the customer to finish"
///    reply) are answered by <see cref="AiResponder"/> at send time;
///  * text jobs with payload <c>source: "ai"</c> (AI-composed flow messages)
///    are submitted to <see cref="AiGate"/> rather than sent blind;
///  * jobs carrying <c>draft_id</c> are the gate's own deliveries (bubbles of an
///    approved/auto-sent reply) — re-checked at send time, chained in order;
///  * <c>humanize: true</c> jobs are split and typed like a person.
/// </summary>
public class QueueService
{
    /// <summary>Job status for an AI message parked in the monitor for review.</summary>
    public const string HeldStatus = "held";

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly OutboundSender _sender;
    private readonly AiGate _gate;
    private readonly AiResponder _responder;
    private readonly TimeProvider _clock;
    private readonly QueueCallPlacer? _calls;
    private readonly Routing _routing;

    public QueueService(AppDbContext db, ScopingService scoping, ThreadStore threads,
        AssistantService assistant, OutboundSender sender,
        AiResponder? responder = null, AiGate? gate = null, TimeProvider? clock = null,
        QueueCallPlacer? calls = null)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _sender = sender;
        _clock = clock ?? TimeProvider.System;
        _gate = gate ?? new AiGate(db, scoping, threads, sender, _clock);
        _responder = responder ?? new AiResponder(db, scoping, new AiComposer(db, assistant), _gate, _clock);
        // null = no voice plumbing wired (bare unit-test construction): call jobs stay queued
        _calls = calls;
        _routing = new Routing(db, scoping);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public OutboundJob Enqueue(string channel, string kind, string toAddress,
        Dictionary<string, object?> payload, int? contactId = null, int? accountId = null,
        DateTime? notBefore = null, int? flowRunId = null, string flowNode = "")
    {
        var job = new OutboundJob
        {
            Channel = channel, Kind = kind, ToAddress = toAddress, Payload = payload,
            ContactId = contactId, AccountId = accountId, Status = JobStatus.Queued,
            NotBefore = notBefore, FlowRunId = flowRunId, FlowNode = flowNode,
            CreatedAt = Now, UpdatedAt = Now,
        };
        _db.OutboundJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    private int IntSetting(string key, int fallback, int? entityId = null)
    {
        var v = _scoping.SettingValue(key, entityId);
        return int.TryParse(v, out var n) ? n : fallback;
    }

    /// <summary>
    /// Drain due jobs, honouring per-account gap + daily cap. Returns the number
    /// actually sent. `maxThisPass` bounds one drain so pacing stays human.
    /// </summary>
    public int Drain(int maxThisPass = 5)
    {
        var dailyCap = IntSetting("queue_daily_cap", 200);
        var now = Now;
        var sent = 0;

        var due = _db.OutboundJobs
            .Where(j => j.Status == JobStatus.Queued && (j.NotBefore == null || j.NotBefore <= now))
            .OrderBy(j => j.CreatedAt)
            .Take(50)
            .ToList();

        // Per-account delivery settings resolved once per pass (pacing window +
        // quiet hours live on the ChannelAccount, falling back to the global
        // Setting rows) — see AccountDelivery. The account is the one the job
        // will ACTUALLY go out on (routing dry run: pin → the thread's healthy
        // number → sticky / least-loaded / failover), so a failed-over job paces
        // and waits for quiet hours on its new number.
        var accountCache = new Dictionary<int, ChannelAccount?>();
        ChannelAccount? AccountFor(OutboundJob job)
        {
            var id = PeekAccountId(job);
            if (id is not int aid) return null;
            if (!accountCache.TryGetValue(aid, out var acct))
                accountCache[aid] = acct = _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == aid);
            return acct;
        }

        var sipDialled = false;
        foreach (var job in due)
        {
            if (sent >= maxThisPass) break;

            var account = AccountFor(job);
            var accountId = account?.Id ?? job.AccountId;

            // quiet hours are checked at SEND time against the account that
            // will carry the message — the job simply waits until they end
            if (AccountDelivery.InQuietHours(account, _scoping, _clock.GetLocalNow().DateTime)) continue;

            // calls: one per number at a time (gateway-authoritative), a breather
            // between calls, a daily cap — and SIP, with no liveness endpoint,
            // dials at most once per pass. A busy number WAITS (no attempt burned).
            if (job.Kind == JobKind.Call)
            {
                if (_calls == null) continue;
                var sip = !QueueCallPlacer.IsWhatsApp(job);
                if (sip && sipDialled) continue;
                if (_calls.WaitReason(job) != null) continue;
                if (!TryClaim(job.Id)) continue;
                if (DeliverCall(job.Id))
                {
                    sent++;
                    if (sip) sipDialled = true;
                }
                continue;
            }

            // per-account gap: derived from the last sent job's sent_at. The AI
            // monitor's own deliveries (draft_id) and the bubbles of a split
            // message skip it: their timing already came from the debounce /
            // typing schedule or a person clicking send, and the parts of one
            // reply must not be spread 30s apart.
            if (!job.Payload.ContainsKey("draft_id") && !job.Payload.ContainsKey("bubble"))
            {
                var (minGap, maxGap) = AccountDelivery.PacingGap(account, _scoping);
                var gap = minGap + Random.Shared.Next(0, Math.Max(1, maxGap - minGap + 1));
                var lastSent = _db.OutboundJobs
                    .Where(j => j.Status == JobStatus.Sent && j.AccountId == accountId && j.SentAt != null)
                    .Max(j => (DateTime?)j.SentAt);
                if (lastSent != null && (now - lastSent.Value).TotalSeconds < gap) continue; // wait
            }

            // per-account daily cap
            var since = now.AddHours(-24);
            var todayCount = _db.OutboundJobs.Count(j =>
                j.Status == JobStatus.Sent && j.AccountId == accountId && j.SentAt >= since);
            if (todayCount >= dailyCap) continue;

            if (!TryClaim(job.Id)) continue; // atomic queued→sending; lost race → skip
            if (Deliver(job.Id)) sent++;
        }
        return sent;
    }

    // ---- account resolution (routing at send time) ----------------------------

    /// <summary>What the job needs from its carrier: a Cloud number for a
    /// template, a conversation number for WhatsApp text / AI replies.</summary>
    private static RouteNeed NeedFor(OutboundJob job) =>
        job.Kind == JobKind.Template ? RouteNeed.Template
        : job.Channel == ChatBranch.Domain.Channels.WhatsApp ? RouteNeed.Conversation
        : RouteNeed.Any;

    /// <summary>routing._pinned_account: payload <c>account_id</c> (the business redirect) fixes the carrier.</summary>
    private static int? Pinned(OutboundJob job) => AiGate.Num(job.Payload, "account_id") is double d ? (int)d : null;

    /// <summary>
    /// The thread a job belongs to. WhatsApp threads are keyed by the digits-only
    /// wa_id, but flows address jobs to the contact's phone ("+60…"): an existing
    /// thread under the literal address still wins, else the digits key — so a
    /// flow message lands in the same conversation the customer writes into.
    /// </summary>
    private string ThreadKey(OutboundJob job)
    {
        if (job.Channel != ChatBranch.Domain.Channels.WhatsApp) return job.ToAddress;
        var digits = PhoneMatch.Digits(job.ToAddress);
        if (digits.Length == 0 || digits == job.ToAddress) return job.ToAddress;
        var literal = _db.Threads.AsNoTracking().Any(t => t.Channel == job.Channel && t.Key == job.ToAddress);
        return literal ? job.ToAddress : digits;
    }

    /// <summary>Dry run: the account this job would go out on now (no writes).</summary>
    private int? PeekAccountId(OutboundJob job)
    {
        try
        {
            if (Pinned(job) is int pin) return pin;
            var key = ThreadKey(job);
            var thread = _db.Threads.AsNoTracking()
                .FirstOrDefault(t => t.Channel == job.Channel && t.Key == key);
            if (job.Kind == JobKind.Call || thread == null) return job.AccountId ?? thread?.AccountId;
            var r = _routing.ForThread(thread, NeedFor(job), thread.AccountId ?? job.AccountId, apply: false);
            return r.Account?.Id ?? thread.AccountId ?? job.AccountId;
        }
        catch { return job.AccountId; }
    }

    /// <summary>
    /// Resolve and stamp the carrying account for a claimed job. A refusal
    /// (nothing eligible — e.g. no healthy Cloud number for a template) comes
    /// back with an error; the thread is left untouched.
    /// </summary>
    private RouteResult? RouteForSend(OutboundJob job, Thread thread)
    {
        int? carrier;
        RouteResult? route = null;
        if (Pinned(job) is int pin) carrier = pin;
        else
        {
            route = _routing.ForThread(thread, NeedFor(job), thread.AccountId ?? job.AccountId, apply: true);
            if (!route.Ok) return route;
            carrier = route.Account?.Id ?? thread.AccountId ?? job.AccountId;
        }
        if (carrier != job.AccountId)
        {
            job.AccountId = carrier;
            job.UpdatedAt = Now;
            _db.SaveChanges();
        }
        return route;
    }

    /// <summary>
    /// The cold-text guard for queued free text (Setting <c>cold_text_guard</c>,
    /// "block" by default): a WhatsApp conversation-role number never opens a
    /// conversation with free text — the contact has to have written to the
    /// conversation first. An explicit <c>force</c> job is let through.
    /// </summary>
    private bool ColdTextBlocked(Thread thread, OutboundJob job)
    {
        if (thread.Channel != ChatBranch.Domain.Channels.WhatsApp || job.AccountId is not int aid) return false;
        if (AiGate.Bool(job.Payload, "force") == true) return false;
        var account = _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == aid);
        if (account == null || AccountRoles.Of(account) != AccountRoles.Conversation) return false;
        if (!_sender.WouldSendLive(thread, aid)) return false;   // sandbox never bans anyone
        var guard = _scoping.SettingValue("cold_text_guard", thread.EntityId).Trim().ToLowerInvariant();
        if (guard is "warn" or "allow") return false;
        return !_db.Messages.AsNoTracking().Any(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In);
    }

    /// <summary>Atomic claim: only one worker wins the queued→sending transition.</summary>
    private bool TryClaim(int jobId)
    {
        var affected = _db.OutboundJobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Queued)
            .ExecuteUpdate(s => s
                .SetProperty(j => j.Status, "sending")
                .SetProperty(j => j.UpdatedAt, Now));
        return affected == 1;
    }

    /// <summary>Deliver one claimed job. True when something went out (or is on its way).</summary>
    private bool Deliver(int jobId)
    {
        var job = _db.OutboundJobs.First(j => j.Id == jobId);
        var draftId = (int?)AiGate.Num(job.Payload, "draft_id");
        try
        {
            var thread = _threads.GetOrCreateThread(job.Channel, ThreadKey(job),
                contactId: job.ContactId, accountId: job.AccountId);
            var source = AiGate.Str(job.Payload, "source", "");

            // the carrying account, resolved NOW (routing: pin → the thread's
            // healthy number → sticky / least-loaded / failover, which moves the
            // thread). Stamped on the job so pacing and caps count the real number.
            var route = RouteForSend(job, thread);
            if (route is { Ok: false })
            {
                if (draftId is int blocked) _gate.MarkDraftFailed(blocked, route.Error!);
                return Fail(job, route.Error!);
            }

            // the debounced AI reply: written NOW, so a burst of messages gets one answer
            if (job.Kind == JobKind.AiReply)
            {
                var outcome = _responder.RespondAsync(thread, job.AccountId, job.Id).GetAwaiter().GetResult();
                return Finish(job, outcome);
            }

            // an AI-composed message from a flow: through the monitor like any AI message
            if (source == "ai" && draftId == null && job.Kind == JobKind.Text)
            {
                var ctx = FlowContext(job);
                var outcome = _gate.Submit(new AiSubmission(thread, AiDraftKind.FlowMessage, ResolveText(job),
                    ctx, job.AccountId, Humanize: AiGate.Bool(job.Payload, "humanize") == true));
                return Finish(job, outcome);
            }

            // compliance at send time: an opt-out stops everything but an explicit force;
            // free text cannot cross a closed WhatsApp 24h window (templates can)
            if (draftId is int did)
            {
                var block = _gate.SendTimeBlock(thread, job.AccountId, isTemplate: job.Kind == JobKind.Template);
                if (block != null)
                {
                    _gate.MarkDraftFailed(did, block);
                    return Fail(job, block);
                }
            }
            else
            {
                if (thread.OptedOut && AiGate.Bool(job.Payload, "force") != true)
                    return Fail(job, "contact opted out");
                if (job.Kind == JobKind.Text && _gate.WindowClosed(thread, job.AccountId))
                    return Fail(job, "WhatsApp 24h window closed — use a template");
                if (job.Kind == JobKind.Text && ColdTextBlocked(thread, job))
                    return Fail(job, "cold text blocked — this contact has never messaged this conversation "
                                     + "number (Routing rules → cold-text guard); open with a template");
            }

            var messageSource = source switch
            {
                "system" => MessageSource.System,
                "ai" => MessageSource.Ai,
                _ => MessageSource.Agent,
            };

            // "send like a person": split + typing pause for a job that asked for it
            if (job.Kind == JobKind.Text && draftId == null && AiGate.Bool(job.Payload, "humanize") == true
                && !job.Payload.ContainsKey("bubble"))
            {
                var account = job.AccountId is int aid
                    ? _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == aid) : null;
                var policy = AiBehaviourSettings.Resolve(_scoping, thread.EntityId, account ?? thread.Account);
                var extra = new Dictionary<string, object?>();
                foreach (var (k, v) in job.Payload)
                    if (k is not ("text" or "humanize")) extra[k] = v;
                var start = _gate.StartHumanSend(thread, ResolveText(job), messageSource,
                    account ?? thread.Account, policy, humanize: true, extra);
                return Sent(job, start.MessageId);
            }

            var text = ResolveText(job);
            // Real send when the account is configured, sandbox otherwise.
            var msg = _sender.Send(thread, text, messageSource, job.AccountId);
            if (draftId is int sentDraft) _gate.MarkDraftSent(sentDraft, msg.Id);
            Sent(job, msg.Id);
            if (job.Payload.ContainsKey("rest")) _gate.ContinueHumanSend(job, thread);
            return true;
        }
        catch (Exception ex)
        {
            if (draftId is int did) { try { _gate.MarkDraftFailed(did, ex.Message); } catch { } }
            return Fail(job, ex.Message);
        }
    }

    /// <summary>
    /// Place one claimed call job. <c>sent</c> means DIALLED, not completed. A
    /// busy line puts the job back in the queue untouched; a refusal fails it and
    /// un-parks the flow run that was waiting on the call.
    /// </summary>
    private bool DeliverCall(int jobId)
    {
        var job = _db.OutboundJobs.First(j => j.Id == jobId);
        CallAttempt attempt;
        try { attempt = _calls!.Place(job); }
        catch (Exception ex) { attempt = new CallAttempt(CallAttempt.Failed, ex.Message); }

        if (attempt.AccountId is int aid) job.AccountId = aid;
        switch (attempt.Outcome)
        {
            case CallAttempt.Placed:
                job.Payload = new Dictionary<string, object?>(job.Payload) { ["room"] = attempt.Room };
                return Sent(job, null);
            case CallAttempt.Wait:
                // a wait, not an attempt: back to queued. The claim was an
                // ExecuteUpdate the tracker never saw, so the tracked Status still
                // reads "queued" — mark it modified or EF would skip the write.
                job.Status = JobStatus.Queued;
                job.LastError = $"waiting: {attempt.Detail}";
                job.UpdatedAt = Now;
                _db.Entry(job).Property(j => j.Status).IsModified = true;
                _db.SaveChanges();
                return false;
            default:
                Fail(job, attempt.Detail);
                _calls!.UnparkRun(job, attempt.Detail);
                return false;
        }
    }

    /// <summary>Map an AI outcome onto the job that produced it.</summary>
    private bool Finish(OutboundJob job, AiGateOutcome outcome)
    {
        switch (outcome.Status)
        {
            case AiGateStatus.Sent:
            case AiGateStatus.Scheduled:
                Sent(job, outcome.MessageIds.Count > 0 ? outcome.MessageIds[0] : null);
                return true;
            case AiGateStatus.Held:
            case AiGateStatus.Shadow:
                Close(job, HeldStatus, $"{(outcome.Status == AiGateStatus.Held ? "held for review" : "shadow draft")}"
                                       + $" — AI monitor draft #{outcome.DraftId}");
                return false;
            case AiGateStatus.Blocked when outcome.DraftId > 0:
                Fail(job, outcome.Detail);
                return false;
            default:
                // guard declined / nothing to say → cancelled, not failed
                Close(job, JobStatus.Cancelled, outcome.Detail.Length > 0 ? outcome.Detail : "ai_reply guard declined");
                return false;
        }
    }

    private bool Sent(OutboundJob job, int? messageId)
    {
        job.Status = JobStatus.Sent;
        job.SentAt = Now;
        job.SentMessageId = messageId;
        job.Attempts += 1;
        job.UpdatedAt = Now;
        ChatBranch.Data.Audit.AuditTrail.Add(_db, "queue.send", $"{job.Channel}:{job.ToAddress}",
            $"{job.Kind} job #{job.Id}{(messageId is int mid ? $" → message #{mid}" : "")}", actor: "queue",
            source: job.FlowRunId != null ? AuditSources.Flow : AuditSources.System, entityId: EntityOf(job));
        _db.SaveChanges();
        return true;
    }

    private bool Fail(OutboundJob job, string error)
    {
        job.Status = JobStatus.Failed;
        job.LastError = error;
        job.Attempts += 1;
        job.UpdatedAt = Now;
        ChatBranch.Data.Audit.AuditTrail.Add(_db, "queue.failed", $"{job.Channel}:{job.ToAddress}",
            $"{job.Kind} job #{job.Id}: {error}", actor: "queue",
            source: job.FlowRunId != null ? AuditSources.Flow : AuditSources.System, entityId: EntityOf(job));
        _db.SaveChanges();
        return false;
    }

    private int? EntityOf(OutboundJob job)
    {
        try
        {
            return _db.Threads.AsNoTracking().Where(t => t.Channel == job.Channel && t.Key == job.ToAddress)
                .Select(t => t.EntityId).FirstOrDefault();
        }
        catch { return null; }
    }

    private void Close(OutboundJob job, string status, string detail)
    {
        job.Status = status;
        job.LastError = detail;
        job.Attempts += 1;
        job.UpdatedAt = Now;
        _db.SaveChanges();
    }

    /// <summary>
    /// Review context for an AI-composed flow message: the flow refs plus the
    /// composer's metadata (payload <c>ai_meta</c>: tools, checks, model…) lifted
    /// to the top level, where the gate's review rules read it.
    /// </summary>
    private static Dictionary<string, object?> FlowContext(OutboundJob job)
    {
        var ctx = new Dictionary<string, object?>
        {
            ["job_id"] = job.Id, ["flow_run_id"] = job.FlowRunId, ["flow_node"] = job.FlowNode,
        };
        if (job.Payload.TryGetValue("ai_meta", out var meta) && meta != null)
        {
            ctx["ai_meta"] = meta;
            if (meta is JsonElement { ValueKind: JsonValueKind.Object } el)
                foreach (var p in el.EnumerateObject()) ctx.TryAdd(p.Name, p.Value.Clone());
            else if (meta is IDictionary<string, object?> d)
                foreach (var (k, v) in d) ctx.TryAdd(k, v);
        }
        return ctx;
    }

    private static string ResolveText(OutboundJob job)
    {
        if (job.Kind == JobKind.Template)
        {
            // Baileys/sandbox templates render as free text.
            var name = job.Payload.TryGetValue("name", out var n) ? n?.ToString() : "";
            var vars = job.Payload.TryGetValue("variables", out var v) ? v?.ToString() : "";
            return $"[template:{name}] {vars}".Trim();
        }
        return job.Payload.TryGetValue("text", out var t) ? t?.ToString() ?? "" : "";
    }
}
