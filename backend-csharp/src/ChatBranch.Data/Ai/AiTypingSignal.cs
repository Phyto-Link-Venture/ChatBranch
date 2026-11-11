using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// "Is an AI reply genuinely on its way to this conversation right now?" — the
/// honest signal behind the website widget's typing indicator.
///
/// It is READ-ONLY: it never schedules, cancels or touches a job, a draft or the
/// gate. It only reads the state the AI ops layer already writes, so a channel
/// that can render an indicator (the widget) can show one for exactly as long as
/// something is actually being composed.
///
/// What counts as "on its way" (both pinned to THIS thread):
/// <list type="bullet">
/// <item>a queued or claimed <c>ai_reply</c> job (<see cref="AiResponder.ScheduleReply"/>):
/// the "wait for the customer to finish" window plus the compose itself — the
/// claim turns the job's status to <c>sending</c> while the model writes, which
/// is the bulk of the wait;</item>
/// <item>a queued or claimed delivery job of an AI draft marked
/// <c>typing: true</c> (<see cref="AiGate.StartHumanSend"/>): the <c>ai_typing</c>
/// pause before a bubble lands, and the pause before each later bubble.</item>
/// </list>
///
/// What deliberately does NOT count — a dot animation with nothing coming is
/// worse than silence:
/// <list type="bullet">
/// <item><b>no job at all</b> — the AI is off for the inbox / conversation / by
/// schedule, the contact opted out, the chat is with a person, or the message was
/// acknowledgement-skipped: nothing was ever queued;</item>
/// <item><b>a reply held for review</b> (<c>ai_review_mode</c>) — the job is
/// closed as <c>held</c> the moment the draft exists, so typing covers the
/// compose window only and stops when a person becomes the bottleneck. A
/// reviewer may take hours; the visitor is not told to keep waiting;</item>
/// <item><b>a job that failed, was cancelled, was superseded or already sent</b>
/// — only <c>queued</c>/<c>sending</c> count, so the indicator clears by itself;</item>
/// <item><b>quiet hours</b> — the gate enqueues those with <c>typing: false</c>,
/// and they can sit for hours;</item>
/// <item><b>a long debounce</b> — a job due further out than
/// <see cref="LeadSeconds"/> is a deliberate wait, not composition, so a widget
/// with a minutes-long <c>ai_wait_seconds</c> shows nothing until the reply is
/// genuinely close;</item>
/// <item><b>a stale job</b> — anything more than <see cref="MaxSeconds"/> PAST
/// its own due time (a stalled drain, a claim whose worker died, a job parked by
/// pacing or quiet hours) stops counting, so an indicator can never stick on.</item>
/// </list>
/// </summary>
public static class AiTypingSignal
{
    /// <summary>
    /// The claimed-but-not-yet-finished status <see cref="Flows.QueueService"/>
    /// writes between <c>queued</c> and the outcome (it is not a
    /// <see cref="JobStatus"/> value). An <c>ai_reply</c> job wears it while the
    /// model is actually writing.
    /// </summary>
    private const string Sending = "sending";

    /// <summary>How far ahead a due time may be and still mean "composing now".</summary>
    public const int LeadSeconds = 30;

    /// <summary>
    /// How far PAST its due time a job may still count. The queue drains every 5 s
    /// and a compose takes seconds, so anything this overdue is stuck, parked or
    /// forgotten — and the indicator clears rather than sticking on.
    /// </summary>
    public const int MaxSeconds = 120;

    /// <summary>
    /// Is an AI reply on its way to <paramref name="thread"/>? Never throws: a
    /// broken signal must not break a visitor's poll.
    /// </summary>
    /// <param name="threads">used only to confirm a job belongs to THIS thread —
    /// never another company's conversation with the same customer.</param>
    public static bool Pending(AppDbContext db, ThreadStore threads, Thread thread, DateTime nowUtc)
    {
        try
        {
            // the window a job is "being written in": from LeadSeconds before it is
            // due until MaxSeconds after, and nothing outside it
            var horizon = nowUtc.AddSeconds(LeadSeconds);
            var floor = nowUtc.AddSeconds(-MaxSeconds);
            // the channel + address narrows it to this customer; JobBelongsTo then
            // settles which conversation (and which company) the job is for
            var jobs = db.OutboundJobs.AsNoTracking()
                .Where(j => (j.Status == JobStatus.Queued || j.Status == Sending)
                            && j.Channel == thread.Channel && j.ToAddress == thread.Key
                            && (j.Kind == JobKind.AiReply || j.Kind == JobKind.Text))
                .OrderByDescending(j => j.Id)
                .Take(20)
                .ToList();

            foreach (var job in jobs)
            {
                if (!Composing(job)) continue;
                // due = when it is expected to go out (null = as soon as the queue
                // drains). A CLAIMED job is being written right now, and the claim
                // stamped UpdatedAt, so the compose gets its own window from there.
                var due = job.NotBefore ?? job.CreatedAt;
                if (job.Status == Sending && job.UpdatedAt > due) due = job.UpdatedAt;
                if (due > horizon) continue;                      // a deliberate wait, not composition
                if (due < floor) continue;                        // long overdue: stuck or parked, never stick on
                if (!threads.JobBelongsTo(job, thread)) continue;  // another conversation's reply
                return true;
            }
            return false;
        }
        catch
        {
            return false;   // unknown is "not typing": silence beats a lie
        }
    }

    /// <summary>
    /// Is this job the AI writing or about to speak? An <c>ai_reply</c> job always
    /// is; a text job only when it carries an AI draft AND the <c>ai_typing</c>
    /// pause that the indicator is meant to fill.
    /// </summary>
    private static bool Composing(OutboundJob job) =>
        job.Kind == JobKind.AiReply
        || (job.Kind == JobKind.Text
            && AiGate.Num(job.Payload, "draft_id") is > 0
            && AiGate.Bool(job.Payload, "typing") == true);
}
