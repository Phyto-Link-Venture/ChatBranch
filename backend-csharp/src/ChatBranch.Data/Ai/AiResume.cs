using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// "Let the AI continue" (and the bulk "AI on") answers the message the customer is
/// waiting on. Turning the chatbot back on used to change nothing until the customer
/// wrote AGAIN: the message they sent while a person had the chat stayed unanswered.
/// Now, when the conversation's last customer-visible message is theirs and under
/// <see cref="WaitingWindow"/> old, ONE ai_reply job is queued for it — the normal
/// queue drain answers it through the AI gate, every guard included (opt-out, the
/// AI switch, quiet hours, the 24h window).
/// </summary>
public static class AiResume
{
    /// <summary>A message older than this is left alone — the customer has moved on.</summary>
    public static readonly TimeSpan WaitingWindow = TimeSpan.FromHours(24);

    public const string AuditAction = "ai.resume.reply";
    /// <summary>The job payload key naming the message being answered (the idempotency key).</summary>
    public const string PayloadKey = "resume_for";

    /// <summary>
    /// Queue the reply to the waiting message, if there is one. Idempotent: one job
    /// per conversation × message — a second click, or a job already queued for the
    /// conversation, adds nothing; a reply waiting for review in the AI monitor is left
    /// to its reviewer. Call AFTER the chatbot is on and saved. Never throws; true when
    /// a job was queued.
    /// </summary>
    public static bool QueueWaitingReply(AppDbContext db, ScopingService scoping, Thread thread, string actor,
        DateTime? nowUtc = null)
    {
        try
        {
            var now = nowUtc ?? DateTime.UtcNow;
            if (thread.OptedOut) return false;
            if (thread.ContactId == null && thread.Channel != ChatBranch.Domain.Channels.Website) return false;
            if (!AiEligibility.Effective(db, scoping, thread, now)) return false;

            var last = db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == thread.Id && !m.Private)
                .OrderByDescending(m => m.Id).FirstOrDefault();
            if (last == null || last.Direction != MessageDirection.In) return false;
            if (now - DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc) > WaitingWindow) return false;

            // a held AI reply is the answer a person decides on
            if (db.AiDrafts.AsNoTracking().Any(d => d.ThreadId == thread.Id && d.Status == AiDraftStatus.Pending))
                return false;

            var store = new Stores.ThreadStore(db, scoping);
            var jobs = db.OutboundJobs.AsNoTracking()
                .Where(j => j.Kind == JobKind.AiReply && j.Channel == thread.Channel && j.ToAddress == thread.Key)
                .OrderByDescending(j => j.Id).Take(50).ToList()
                .Where(j => store.JobBelongsTo(j, thread)).ToList();
            if (jobs.Any(j => j.Status is JobStatus.Queued or "sending")) return false;
            if (jobs.Any(j => AiGate.Num(j.Payload, PayloadKey) is double id && (int)id == last.Id)) return false;

            db.OutboundJobs.Add(new OutboundJob
            {
                Channel = thread.Channel, Kind = JobKind.AiReply, ToAddress = thread.Key,
                Payload = new Dictionary<string, object?>
                {
                    ["wait_s"] = 0, [Stores.ThreadStore.JobThreadKey] = thread.Id, [PayloadKey] = last.Id,
                },
                ContactId = thread.ContactId, AccountId = thread.AccountId, Status = JobStatus.Queued,
                CreatedAt = now, UpdatedAt = now,
            });
            AuditTrail.Add(db, AuditAction, $"{thread.Channel}:{thread.Key}",
                "Answered the waiting message after the AI was let continue", actor: actor, entityId: thread.EntityId);
            db.SaveChanges();
            return true;
        }
        catch { return false; }
    }
}
