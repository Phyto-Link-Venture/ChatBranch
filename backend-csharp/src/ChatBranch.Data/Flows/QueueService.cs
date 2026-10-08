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
/// </summary>
public class QueueService
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly AssistantService _assistant;
    private readonly OutboundSender _sender;

    public QueueService(AppDbContext db, ScopingService scoping, ThreadStore threads,
        AssistantService assistant, OutboundSender sender)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _assistant = assistant;
        _sender = sender;
    }

    public OutboundJob Enqueue(string channel, string kind, string toAddress,
        Dictionary<string, object?> payload, int? contactId = null, int? accountId = null,
        DateTime? notBefore = null, int? flowRunId = null, string flowNode = "")
    {
        var job = new OutboundJob
        {
            Channel = channel, Kind = kind, ToAddress = toAddress, Payload = payload,
            ContactId = contactId, AccountId = accountId, Status = JobStatus.Queued,
            NotBefore = notBefore, FlowRunId = flowRunId, FlowNode = flowNode,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
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
        var now = DateTime.UtcNow;
        var sent = 0;

        var due = _db.OutboundJobs
            .Where(j => j.Status == JobStatus.Queued && (j.NotBefore == null || j.NotBefore <= now))
            .OrderBy(j => j.CreatedAt)
            .Take(50)
            .ToList();

        // Per-account delivery settings resolved once per pass (pacing window +
        // quiet hours live on the ChannelAccount, falling back to the global
        // Setting rows) — see AccountDelivery.
        var accountCache = new Dictionary<int, ChannelAccount?>();
        ChannelAccount? AccountFor(OutboundJob job)
        {
            var id = job.AccountId ?? _db.Threads.AsNoTracking()
                .Where(t => t.Channel == job.Channel && t.Key == job.ToAddress)
                .Select(t => t.AccountId).FirstOrDefault();
            if (id is not int aid) return null;
            if (!accountCache.TryGetValue(aid, out var acct))
                accountCache[aid] = acct = _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == aid);
            return acct;
        }

        foreach (var job in due)
        {
            if (sent >= maxThisPass) break;
            if (job.Kind == JobKind.Call) continue; // calls serialise elsewhere (Phase 5)

            var accountId = job.AccountId;
            var account = AccountFor(job);

            // quiet hours are checked at SEND time against the account that
            // will carry the message — the job simply waits until they end
            if (AccountDelivery.InQuietHours(account, _scoping)) continue;

            // per-account gap: derived from the last sent job's sent_at
            var (minGap, maxGap) = AccountDelivery.PacingGap(account, _scoping);
            var gap = minGap + Random.Shared.Next(0, Math.Max(1, maxGap - minGap + 1));
            var lastSent = _db.OutboundJobs
                .Where(j => j.Status == JobStatus.Sent && j.AccountId == accountId && j.SentAt != null)
                .Max(j => (DateTime?)j.SentAt);
            if (lastSent != null && (now - lastSent.Value).TotalSeconds < gap) continue; // wait

            // per-account daily cap
            var since = now.AddHours(-24);
            var todayCount = _db.OutboundJobs.Count(j =>
                j.Status == JobStatus.Sent && j.AccountId == accountId && j.SentAt >= since);
            if (todayCount >= dailyCap) continue;

            if (!TryClaim(job.Id)) continue; // atomic queued→sending; lost race → skip
            Deliver(job.Id);
            sent++;
        }
        return sent;
    }

    /// <summary>Atomic claim: only one worker wins the queued→sending transition.</summary>
    private bool TryClaim(int jobId)
    {
        var affected = _db.OutboundJobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Queued)
            .ExecuteUpdate(s => s
                .SetProperty(j => j.Status, "sending")
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow));
        return affected == 1;
    }

    private void Deliver(int jobId)
    {
        var job = _db.OutboundJobs.First(j => j.Id == jobId);
        try
        {
            var text = ResolveText(job);
            var thread = _threads.GetOrCreateThread(job.Channel, job.ToAddress,
                contactId: job.ContactId, accountId: job.AccountId);

            if (job.Kind == JobKind.AiReply)
            {
                // generated AT SEND time so several inbound get one coherent answer
                var ai = _assistant.GenerateReplyAsync(thread).GetAwaiter().GetResult();
                text = ai.ShouldSend ? ai.Text : "";
                if (string.IsNullOrWhiteSpace(text))
                {
                    job.Status = JobStatus.Cancelled; // guard declined → cancelled, not failed
                    job.LastError = ai.Action is AiActions.Handover or AiActions.Block
                        ? $"ai_reply {ai.Action}: {ai.Reason}"
                        : "ai_reply guard declined";
                    job.UpdatedAt = DateTime.UtcNow;
                    _db.SaveChanges();
                    return;
                }
            }

            var src = job.Payload.TryGetValue("source", out var s) ? s?.ToString() : null;
            var source = src == "system"
                ? MessageSource.System
                : (job.Kind == JobKind.AiReply || src == "ai" ? MessageSource.Ai : MessageSource.Agent);

            // Real send when the account is configured, sandbox otherwise.
            var msg = _sender.Send(thread, text, source, job.AccountId);

            job.Status = JobStatus.Sent;
            job.SentAt = DateTime.UtcNow;
            job.SentMessageId = msg.Id;
            job.Attempts += 1;
            job.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.LastError = ex.Message;
            job.Attempts += 1;
            job.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
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
