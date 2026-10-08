using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>One AI-written outbound asking to be sent.</summary>
public record AiSubmission(
    Thread Thread,
    string Kind,
    string Text,
    Dictionary<string, object?>? Context = null,
    int? AccountId = null,
    string? TemplateName = null,
    bool Humanize = true,
    bool Canned = false);

public static class AiGateStatus
{
    public const string Sent = "sent";            // delivered now
    public const string Scheduled = "scheduled";  // on its way (typing pause / quiet hours / template)
    public const string Held = "held";            // waiting for a person
    public const string Shadow = "shadow";        // recorded, never sent
    public const string Blocked = "blocked";      // a send-time guard refused it
    public const string Skipped = "skipped";      // nothing needed saying (acknowledgement)
    public const string Empty = "empty";          // the AI produced nothing
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";    // already decided / superseded / empty edit
}

public record AiGateOutcome(string Status, int DraftId, IReadOnlyList<int> MessageIds, string Detail)
{
    public bool Delivered => Status is AiGateStatus.Sent or AiGateStatus.Scheduled;
}

/// <summary>
/// The AI monitor's gate: EVERY AI-written outbound — instant replies, queued
/// ai_reply jobs, follow-ups, AI-composed flow messages — passes through
/// <see cref="Submit"/>. Per the effective review policy it either sends
/// straight away (still recording an <c>auto_sent</c> draft, so the monitor is
/// the complete trail of what the AI did) or holds it as <c>pending</c> for a
/// person. Approving sends through the SAME human-like send path, and the
/// send-time guards (opt-out, coverage gate, 24h window, quiet hours) are
/// re-checked at that moment — a draft approved an hour later is judged by the
/// conversation as it is then, not as it was.
/// </summary>
public class AiGate
{
    public const string ReviewLink = "/ai-monitor";
    public const int BubbleGapSeconds = 2;
    private const int WindowHours = 24;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly OutboundSender _sender;
    private readonly TimeProvider _clock;
    private readonly ITypingIndicator _typing;

    public AiGate(AppDbContext db, ScopingService scoping, ThreadStore threads, OutboundSender sender,
        TimeProvider? clock = null, ITypingIndicator? typing = null)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _sender = sender;
        _clock = clock ?? TimeProvider.System;
        _typing = typing ?? new NoTypingIndicator();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private DateTime LocalNow => _clock.GetLocalNow().DateTime;

    // ---- submit -------------------------------------------------------------

    public Task<AiGateOutcome> SubmitAsync(AiSubmission s) => Task.FromResult(Submit(s));

    public AiGateOutcome Submit(AiSubmission s)
    {
        var text = (s.Text ?? "").Trim();
        if (text.Length == 0)
            return new AiGateOutcome(AiGateStatus.Empty, 0, Array.Empty<int>(), "The AI produced no text.");

        var thread = s.Thread;
        var accountId = s.AccountId ?? thread.AccountId;
        var account = Account(accountId);
        var policy = AiBehaviourSettings.Resolve(_scoping, thread.EntityId, account);

        var ctx = s.Context != null ? new Dictionary<string, object?>(s.Context) : new();
        if (s.TemplateName != null) ctx["template"] = s.TemplateName;
        ctx["humanize"] = s.Humanize;
        if (s.Canned) ctx["canned"] = true;
        var flags = s.Canned ? new List<string>() : Flags(thread, text, ctx, policy, s.Kind);
        ctx["flags"] = flags;
        ctx["review_mode"] = policy.ReviewMode;

        var now = Now;
        var draft = new AiDraft
        {
            ThreadId = thread.Id, ThreadKey = thread.Key, Channel = thread.Channel,
            ContactId = thread.ContactId, EntityId = thread.EntityId, AccountId = accountId,
            Kind = s.Kind, Content = text, Context = ctx, CreatedAt = now, UpdatedAt = now,
        };

        if (policy.ReviewMode == "shadow")
        {
            draft.Status = AiDraftStatus.Shadow;
            draft.HoldReason = "Shadow mode — the AI drafts, people send.";
            Save(draft);
            return new AiGateOutcome(AiGateStatus.Shadow, draft.Id, Array.Empty<int>(), draft.HoldReason);
        }

        var hold = s.Canned ? new List<string>()
            : policy.ReviewMode == "review_all" ? new List<string> { "Every AI reply is reviewed" }
            : policy.ReviewMode == "review_flagged" ? flags
            : new List<string>();
        // The assistant itself refused (its checks blocked the draft, or it asked
        // for a person): never sent blind, whatever the mode — a person decides.
        var action = Str(ctx, "action", AiActions.Send);
        if (!s.Canned && action is AiActions.Handover or AiActions.Block)
        {
            hold = flags.Count > 0 ? new List<string>(flags) : new List<string> { "The AI's reply checks stopped this reply" };
            if (policy.ReviewMode == "review_all") hold.Insert(0, "Every AI reply is reviewed");
        }
        if (hold.Count > 0)
        {
            draft.Status = AiDraftStatus.Pending;
            draft.HoldReason = string.Join("; ", hold);
            draft.SendAfter = policy.AutosendMinutes > 0 ? now.AddMinutes(policy.AutosendMinutes) : null;
            Save(draft);
            NotifyReviewers(draft, thread, policy);
            return new AiGateOutcome(AiGateStatus.Held, draft.Id, Array.Empty<int>(), draft.HoldReason);
        }

        draft.Status = AiDraftStatus.AutoSent;
        draft.DecidedBy = "auto";
        draft.DecidedAt = now;
        Save(draft);
        return Deliver(draft, thread, account, policy, humanDecision: false);
    }

    /// <summary>
    /// The review_flagged signals, recorded on every draft (so the activity view
    /// shows them even for replies that went straight out).
    /// </summary>
    private List<string> Flags(Thread thread, string text, Dictionary<string, object?> ctx,
        AiBehaviour policy, string kind)
    {
        var reasons = new List<string>();
        var action = Str(ctx, "action", AiActions.Send);
        if (action == AiActions.Block)
        {
            var why = Str(ctx, "reason", "");
            reasons.Add(why.Length > 0 ? $"A reply check blocked it: {why}" : "A reply check blocked it");
        }
        // only the checks run on the FINAL draft count (an earlier attempt that a
        // retry fixed shows up as "needed a retry" instead)
        var checks = Items(ctx, "checks").ToList();
        var lastAttempt = checks.Select(c => Num(c, "attempt") ?? 0).DefaultIfEmpty(0).Max();
        foreach (var check in checks.Where(c => (Num(c, "attempt") ?? 0) == lastAttempt))
            if (Bool(check, "passed") == false || Bool(check, "ok") == false)
            {
                var why = Str(check, "reason", "");
                reasons.Add($"Check failed: {Str(check, "name", "check")}{(why.Length > 0 ? $" — {why}" : "")}");
            }
        foreach (var tool in Items(ctx, "tools"))
            if (Bool(tool, "ok") == false)
                reasons.Add($"Tool failed: {Str(tool, "name", "tool")}");
        if (Num(ctx, "retries") is double r && r > 0)
            reasons.Add($"The AI needed {r:0} retr{(r == 1 ? "y" : "ies")}");
        if (Bool(ctx, "rounds_exhausted") == true)
            reasons.Add("The AI ran out of tool rounds");
        if (Bool(ctx, "handover") == true || Humanizer.HandsOver(text))
            reasons.Add("The AI wants to hand over to a person");
        if (Num(ctx, "confidence") is double conf && conf < 0.5)
            reasons.Add("Low confidence");
        if (Humanizer.Hedges(text))
            reasons.Add("The reply sounds unsure");
        reasons.AddRange(Humanizer.SensitiveHits(text, policy.Keywords)
            .Select(h => char.ToUpperInvariant(h[0]) + h[1..]));
        if (kind == AiDraftKind.Reply && policy.FirstN > 0)
        {
            var delivered = _db.AiDrafts.AsNoTracking().Count(d => d.ThreadId == thread.Id
                && d.Kind == AiDraftKind.Reply
                && (d.Status == AiDraftStatus.AutoSent || d.Status == AiDraftStatus.Approved
                    || d.Status == AiDraftStatus.Edited || d.Status == AiDraftStatus.Sent));
            if (delivered < policy.FirstN)
                reasons.Add(policy.FirstN == 1
                    ? "The first AI reply in this conversation"
                    : $"One of the first {policy.FirstN} AI replies in this conversation");
        }
        return reasons.Distinct().ToList();
    }

    // ---- decisions ------------------------------------------------------------

    /// <summary>
    /// Approve (or edit &amp; send) a held draft. Shadow drafts may be used too —
    /// a person choosing to send the AI's words. Re-runs every send-time guard.
    /// </summary>
    public AiGateOutcome Approve(int id, string actor, string? editedContent = null, bool force = false)
    {
        var draft = _db.AiDrafts.FirstOrDefault(d => d.Id == id);
        if (draft == null) return Outcome(AiGateStatus.NotFound, 0, "Draft not found.");
        if (draft.Status is not (AiDraftStatus.Pending or AiDraftStatus.Shadow))
            return Outcome(AiGateStatus.Conflict, draft.Id, $"This draft is already {Label(draft.Status)}.");
        var thread = ThreadFor(draft);
        if (thread == null) return Outcome(AiGateStatus.NotFound, draft.Id, "The conversation no longer exists.");
        if (!force && Superseded(draft))
            return Outcome(AiGateStatus.Conflict, draft.Id,
                "A teammate replied after this draft was written. Approve again with force to send it anyway.");

        var content = editedContent?.Trim();
        if (content is { Length: 0 })
            return Outcome(AiGateStatus.Conflict, draft.Id, "The reply is empty.");
        var edited = content != null && content != draft.Content;
        if (edited)
        {
            draft.OriginalContent = draft.Content;
            draft.Content = content!;
        }
        draft.Status = edited ? AiDraftStatus.Edited : AiDraftStatus.Approved;
        draft.DecidedBy = actor;
        draft.DecidedAt = Now;
        draft.SendAfter = null;
        draft.UpdatedAt = Now;
        _db.SaveChanges();

        var account = Account(draft.AccountId ?? thread.AccountId);
        var policy = AiBehaviourSettings.Resolve(_scoping, thread.EntityId, account);
        return Deliver(draft, thread, account, policy, humanDecision: true);
    }

    public AiGateOutcome Reject(int id, string actor, string? reason)
    {
        var draft = _db.AiDrafts.FirstOrDefault(d => d.Id == id);
        if (draft == null) return Outcome(AiGateStatus.NotFound, 0, "Draft not found.");
        if (draft.Status is not (AiDraftStatus.Pending or AiDraftStatus.Shadow))
            return Outcome(AiGateStatus.Conflict, draft.Id, $"This draft is already {Label(draft.Status)}.");
        var r = (reason ?? "").Trim();
        draft.Status = AiDraftStatus.Rejected;
        draft.RejectReason = r.Length > 500 ? r[..500] : r;
        draft.DecidedBy = actor;
        draft.DecidedAt = Now;
        draft.SendAfter = null;
        draft.UpdatedAt = Now;
        _db.SaveChanges();
        BumpThread(draft.ThreadId);
        return Outcome("rejected", draft.Id, "Rejected.");
    }

    /// <summary>
    /// A new customer message makes any reply still waiting for review stale —
    /// expire it, so the fresh reply covers everything the customer said.
    /// </summary>
    public int ExpireForCustomerMessage(Thread thread)
    {
        var stale = _db.AiDrafts.Where(d => d.ThreadId == thread.Id && d.Status == AiDraftStatus.Pending
                && (d.Kind == AiDraftKind.Reply || d.Kind == AiDraftKind.FollowUp)).ToList();
        foreach (var d in stale)
            Expire(d, "The customer wrote again before this was sent — a fresh reply covers everything they said.");
        if (stale.Count > 0)
        {
            _db.SaveChanges();
            BumpThread(thread.Id);
        }
        return stale.Count;
    }

    /// <summary>
    /// Worker pass: expire held drafts a teammate already answered, then send the
    /// ones whose auto-send deadline passed with nobody acting.
    /// </summary>
    public int ProcessDue(int max = 50)
    {
        var now = Now;
        var pending = _db.AiDrafts.Where(d => d.Status == AiDraftStatus.Pending)
            .OrderBy(d => d.Id).Take(500).ToList();
        foreach (var d in pending.Where(Superseded))
        {
            Expire(d, "A teammate replied before this was reviewed.");
            BumpThread(d.ThreadId);
        }
        _db.SaveChanges();

        var sent = 0;
        foreach (var d in pending.Where(d => d.Status == AiDraftStatus.Pending
                                             && d.SendAfter != null && d.SendAfter <= now).Take(max))
        {
            var thread = ThreadFor(d);
            if (thread == null)
            {
                Fail(d, "The conversation no longer exists.");
                continue;
            }
            d.Status = AiDraftStatus.Sent;
            d.DecidedBy = "auto-send (no reviewer acted)";
            d.DecidedAt = now;
            d.UpdatedAt = now;
            _db.SaveChanges();
            var account = Account(d.AccountId ?? thread.AccountId);
            var policy = AiBehaviourSettings.Resolve(_scoping, thread.EntityId, account);
            if (Deliver(d, thread, account, policy, humanDecision: false).Delivered) sent++;
        }
        return sent;
    }

    // ---- delivery -------------------------------------------------------------

    private AiGateOutcome Deliver(AiDraft draft, Thread thread, ChannelAccount? account, AiBehaviour policy,
        bool humanDecision)
    {
        var template = Str(draft.Context, "template", "");
        var block = SendTimeBlock(thread, account?.Id ?? draft.AccountId, isTemplate: template.Length > 0,
            humanDecision: humanDecision);
        if (block != null)
        {
            Fail(draft, block);
            return Outcome(AiGateStatus.Blocked, draft.Id, block);
        }

        var extra = new Dictionary<string, object?> { ["draft_id"] = draft.Id };
        if (template.Length > 0)
        {
            Enqueue(thread, account, JobKind.Template, new Dictionary<string, object?>(extra)
            {
                ["name"] = template, ["language"] = "en", ["variables"] = "", ["source"] = "ai",
            }, Now);
            BumpThread(thread.Id);
            return Outcome(AiGateStatus.Scheduled, draft.Id, "Re-engagement template queued.");
        }

        var humanize = Bool(draft.Context, "humanize") != false;
        var start = StartHumanSend(thread, draft.Content, MessageSource.Ai, account, policy, humanize, extra);
        if (start.MessageId is int mid)
        {
            draft.SentAt = Now;
            draft.SentMessageId = mid;
            draft.UpdatedAt = Now;
            _db.SaveChanges();
            return new AiGateOutcome(AiGateStatus.Sent, draft.Id, new[] { mid }, start.Detail);
        }
        BumpThread(thread.Id);
        return Outcome(AiGateStatus.Scheduled, draft.Id, start.Detail);
    }

    public record HumanSendStart(int? MessageId, int Bubbles, string Detail);

    /// <summary>
    /// The human-like send path, shared by AI replies and queue jobs marked
    /// <c>humanize: true</c>: split into 1–3 bubbles, then either send the first
    /// now (no typing pause, not in quiet hours) or schedule it after a typing
    /// pause, with the "typing…" indicator shown where the channel supports it.
    /// Later bubbles chain through the queue (<see cref="ContinueHumanSend"/>), so
    /// their order is guaranteed and nothing ever sleeps on a request thread.
    /// </summary>
    public HumanSendStart StartHumanSend(Thread thread, string text, string source, ChannelAccount? account,
        AiBehaviour policy, bool humanize, Dictionary<string, object?> extra)
    {
        var bubbles = humanize
            ? Humanizer.Split(text, policy.SplitMode, policy.SplitMaxChars)
            : new List<string> { text.Trim() };
        if (bubbles.Count == 0) bubbles.Add(text);
        var typing = humanize && policy.TypingEnabled;
        var quiet = AccountDelivery.InQuietHours(account, _scoping, LocalNow);
        var payload = new Dictionary<string, object?>(extra)
        {
            ["source"] = source, ["typing"] = typing, ["bubbles"] = bubbles.Count,
        };

        if (!typing && !quiet)
        {
            var msg = _sender.Send(thread, bubbles[0], source, account?.Id ?? thread.AccountId);
            if (bubbles.Count > 1)
                EnqueueBubbles(thread, account, bubbles.Skip(1).ToList(), 2, payload,
                    TimeSpan.FromSeconds(BubbleGapSeconds));
            return new HumanSendStart(msg.Id, bubbles.Count,
                bubbles.Count > 1 ? $"Sent in {bubbles.Count} messages." : "Sent.");
        }

        var delay = typing
            ? Humanizer.TypingDelay(bubbles[0], policy.TypingCps, policy.TypingMinSeconds, policy.TypingMaxSeconds)
            : TimeSpan.Zero;
        if (typing && !quiet) StartTyping(thread, account);
        EnqueueBubbles(thread, account, bubbles, 1, payload, delay);
        return new HumanSendStart(null, bubbles.Count, quiet
            ? "Quiet hours — it will be sent when they end."
            : $"Typing… sends in {Math.Ceiling(delay.TotalSeconds):0}s.");
    }

    /// <summary>After one bubble was delivered, schedule the next (with its own typing pause).</summary>
    public void ContinueHumanSend(OutboundJob job, Thread thread)
    {
        var rest = StrList(job.Payload, "rest");
        if (rest.Count == 0) return;
        var account = Account(job.AccountId ?? thread.AccountId);
        var policy = AiBehaviourSettings.Resolve(_scoping, thread.EntityId, account);
        var typing = Bool(job.Payload, "typing") == true;
        var delay = typing
            ? Humanizer.TypingDelay(rest[0], policy.TypingCps, policy.TypingMinSeconds, policy.TypingMaxSeconds)
            : TimeSpan.FromSeconds(BubbleGapSeconds);
        if (typing) StartTyping(thread, account);
        var carry = new Dictionary<string, object?>();
        foreach (var (k, v) in job.Payload)
            if (k is not ("text" or "rest" or "bubble")) carry[k] = v;
        var index = (int)(Num(job.Payload, "bubble") ?? 1) + 1;
        EnqueueBubbles(thread, account, rest, index, carry, delay);
    }

    private void EnqueueBubbles(Thread thread, ChannelAccount? account, List<string> bubbles, int index,
        Dictionary<string, object?> payload, TimeSpan delay)
    {
        var p = new Dictionary<string, object?>(payload)
        {
            ["text"] = bubbles[0],
            ["rest"] = bubbles.Skip(1).ToList(),
            ["bubble"] = index,
        };
        Enqueue(thread, account, JobKind.Text, p, Now.Add(delay));
    }

    private void Enqueue(Thread thread, ChannelAccount? account, string kind,
        Dictionary<string, object?> payload, DateTime notBefore)
    {
        var now = Now;
        _db.OutboundJobs.Add(new OutboundJob
        {
            Channel = thread.Channel, Kind = kind, ToAddress = thread.Key, Payload = payload,
            ContactId = thread.ContactId, AccountId = account?.Id ?? thread.AccountId,
            Status = JobStatus.Queued, NotBefore = notBefore, CreatedAt = now, UpdatedAt = now,
        });
        _db.SaveChanges();
    }

    private void StartTyping(Thread thread, ChannelAccount? account)
    {
        try
        {
            if (account == null || !_sender.WouldSendLive(thread, account.Id)) return;
            _typing.Start(thread, account);
        }
        catch { /* presence is best-effort */ }
    }

    /// <summary>The queue delivered a bubble of this draft — record when the reply first reached the customer.</summary>
    public void MarkDraftSent(int draftId, int messageId)
    {
        var d = _db.AiDrafts.FirstOrDefault(x => x.Id == draftId);
        if (d == null || d.SentAt != null) return;
        d.SentAt = Now;
        d.SentMessageId = messageId;
        d.UpdatedAt = Now;
        _db.SaveChanges();
    }

    /// <summary>A scheduled delivery was refused at send time.</summary>
    public void MarkDraftFailed(int draftId, string error)
    {
        var d = _db.AiDrafts.FirstOrDefault(x => x.Id == draftId);
        if (d == null) return;
        if (d.SentAt == null) Fail(d, error);
        else
        {
            d.Error = error;  // a later bubble failed; the reply already reached them
            d.UpdatedAt = Now;
            _db.SaveChanges();
        }
    }

    // ---- send-time guards -----------------------------------------------------

    /// <summary>
    /// Why an AI message may not be sent right now (null = clear to send). Quiet
    /// hours are not a refusal — delivery waits for them instead.
    /// </summary>
    public string? SendTimeBlock(Thread thread, int? accountId, bool isTemplate = false, bool humanDecision = false)
    {
        if (thread.OptedOut) return "The contact opted out (STOP) — nothing was sent.";
        if (thread.ContactId == null && thread.Channel != ChatBranch.Domain.Channels.Website)
            return "This conversation isn't linked to a contact — the AI only writes to known contacts.";
        if (!humanDecision && !AiAllowed(thread))
            return "AI is turned off for this conversation.";
        if (!isTemplate && WindowClosed(thread, accountId))
            return "WhatsApp's 24-hour window has closed — only a template can be sent now.";
        return null;
    }

    /// <summary>The thread's chatbot switch, including an explicit per-account OFF tick.</summary>
    public static bool AiAllowed(Thread t)
    {
        if (!t.AiEnabled) return false;
        if (t.AccountId is int acct && t.AiOverrides.TryGetValue(acct.ToString(), out var raw))
        {
            if (raw is bool b) return b;
            if (raw is JsonElement el && el.ValueKind == JsonValueKind.False) return false;
            if (raw is string s && bool.TryParse(s, out var parsed)) return parsed;
        }
        return true;
    }

    /// <summary>Meta's 24h rule: only a LIVE WhatsApp Cloud number is bound by it.</summary>
    public bool WindowClosed(Thread thread, int? accountId)
    {
        if (thread.Channel != ChatBranch.Domain.Channels.WhatsApp) return false;
        var account = Account(accountId ?? thread.AccountId);
        if (account == null || account.Provider == Providers.Baileys) return false;
        if (!_sender.WouldSendLive(thread, account.Id)) return false;
        var lastIn = _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
            .Max(m => (DateTime?)m.CreatedAt);
        return lastIn is not DateTime ts || Now >= ts.AddHours(WindowHours);
    }

    /// <summary>A person (not the AI) wrote to the customer after the draft was created.</summary>
    private bool Superseded(AiDraft d)
    {
        if (d.ThreadId is not int tid) return false;
        return _db.Messages.AsNoTracking().Any(m => m.ThreadId == tid
            && m.Direction == MessageDirection.Out && m.Source == MessageSource.Agent
            && !m.Private && m.CreatedAt > d.CreatedAt);
    }

    // ---- reviewers -----------------------------------------------------------

    /// <summary>
    /// Bell notification for the people who review — the conversation's assignee
    /// plus the admins of its tenant — through the central NotificationService
    /// (org mute, personal switches, "only assigned to me"). The collapse key
    /// keeps it to ONE unread "waiting for review" row per reviewer that later
    /// drafts refresh, rather than a row per draft.
    /// </summary>
    private void NotifyReviewers(AiDraft draft, Thread thread, AiBehaviour policy)
    {
        if (!policy.NotifyReviewers) return;
        try
        {
            var ids = new List<int>();
            if (!string.IsNullOrWhiteSpace(thread.Assignee))
            {
                var assignee = _db.Users.AsNoTracking()
                    .Where(u => u.Username == thread.Assignee).Select(u => (int?)u.Id).FirstOrDefault();
                if (assignee is int aid) ids.Add(aid);
            }
            int? tenantId = thread.EntityId is int eid
                ? _db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => (int?)e.TenantId).FirstOrDefault()
                : null;
            var who = string.IsNullOrWhiteSpace(thread.Name) ? thread.Key : thread.Name;
            var snippet = draft.Content.Length > 140 ? draft.Content[..140] + "…" : draft.Content;
            NotificationService.Notify(_db, NotificationCategories.Messages, "message",
                "AI reply waiting for review", Cap($"{who} ({thread.Channel}): {snippet}", 400),
                $"{ReviewLink}?tab=review",
                new NotificationAudience
                {
                    RoleSlugs = new[] { Roles.SuperAdmin, Roles.Admin },
                    UserIds = ids,
                    TenantId = tenantId,
                },
                new NotifyOptions
                {
                    Thread = thread,
                    RefKey = "ai-review",
                    CollapsedTitle = "AI replies waiting for review",
                });
        }
        catch { /* a notification never blocks the gate */ }
    }

    // ---- helpers -------------------------------------------------------------

    private void Save(AiDraft draft)
    {
        _db.AiDrafts.Add(draft);
        _db.SaveChanges();
        BumpThread(draft.ThreadId);
    }

    private void Expire(AiDraft d, string why)
    {
        d.Status = AiDraftStatus.Expired;
        d.DecidedBy = "system";
        d.DecidedAt = Now;
        d.Error = why;
        d.SendAfter = null;
        d.UpdatedAt = Now;
    }

    private void Fail(AiDraft d, string why)
    {
        d.Status = AiDraftStatus.Failed;
        d.Error = why;
        d.SendAfter = null;
        d.UpdatedAt = Now;
        _db.SaveChanges();
        BumpThread(d.ThreadId);
    }

    /// <summary>
    /// The inbox SSE change token watches Thread.updated_at — bumping it is how
    /// the monitor (and the chat) learn a draft changed. Real wall-clock time on
    /// purpose: the token compares against ThreadStore's own bumps.
    /// </summary>
    private void BumpThread(int? threadId)
    {
        if (threadId is not int id) return;
        try
        {
            _db.Threads.Where(t => t.Id == id)
                .ExecuteUpdate(s => s.SetProperty(t => t.UpdatedAt, DateTime.UtcNow));
        }
        catch { /* the stream simply refreshes on its next change */ }
    }

    private Thread? ThreadFor(AiDraft d) => d.ThreadId is int tid
        ? _db.Threads.Include(t => t.Account).Include(t => t.Contact).FirstOrDefault(t => t.Id == tid)
        : null;

    private ChannelAccount? Account(int? id) =>
        id is int aid ? _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == aid) : null;

    private static AiGateOutcome Outcome(string status, int draftId, string detail) =>
        new(status, draftId, Array.Empty<int>(), detail);

    public static string Label(string status) => status switch
    {
        AiDraftStatus.AutoSent => "sent automatically",
        AiDraftStatus.Sent => "sent after the review deadline",
        _ => status,
    };

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];

    // Context values round-trip through a JSON column: freshly written they are
    // CLR objects, re-loaded they are JsonElements. Read both.

    public static IEnumerable<object?> Items(Dictionary<string, object?> ctx, string key)
    {
        if (!ctx.TryGetValue(key, out var v) || v == null) return Array.Empty<object?>();
        if (v is JsonElement el)
            return el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().Select(x => (object?)x).ToList()
                : Array.Empty<object?>();
        if (v is System.Collections.IEnumerable e and not string) return e.Cast<object?>().ToList();
        return Array.Empty<object?>();
    }

    public static object? Get(object? obj, string key)
    {
        switch (obj)
        {
            case JsonElement el when el.ValueKind == JsonValueKind.Object:
                return el.TryGetProperty(key, out var p) ? p : null;
            case IDictionary<string, object?> d:
                return d.TryGetValue(key, out var v) ? v : null;
            case System.Collections.IDictionary nd:
                return nd.Contains(key) ? nd[key] : null;
            default:
                return obj?.GetType().GetProperty(key)?.GetValue(obj);
        }
    }

    public static bool? Bool(object? obj, string key) => Get(obj, key) switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        string s when bool.TryParse(s, out var p) => p,
        _ => null,
    };

    public static double? Num(object? obj, string key) => Get(obj, key) switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        decimal m => (double)m,
        JsonElement { ValueKind: JsonValueKind.Number } el => el.GetDouble(),
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    public static string Str(object? obj, string key, string fallback) => Get(obj, key) switch
    {
        null => fallback,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString() ?? fallback,
        JsonElement { ValueKind: JsonValueKind.Null } => fallback,
        JsonElement el => el.ToString(),
        var o => o.ToString() ?? fallback,
    };

    public static List<string> StrList(object? obj, string key) => Get(obj, key) switch
    {
        JsonElement { ValueKind: JsonValueKind.Array } el =>
            el.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                .Where(s => s.Length > 0).ToList(),
        IEnumerable<string> list => list.Where(s => !string.IsNullOrEmpty(s)).ToList(),
        System.Collections.IEnumerable e and not string => e.Cast<object?>()
            .Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToList(),
        _ => new List<string>(),
    };
}
