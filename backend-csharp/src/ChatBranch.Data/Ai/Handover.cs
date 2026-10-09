using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>Why a conversation was handed to a person (wire values of <c>handover_kind</c>).</summary>
public static class HandoverKinds
{
    /// <summary>A reply check failed (or blocked) and a rewrite could not fix it.</summary>
    public const string CheckFailed = "check_failed";
    /// <summary>The AI told the customer a person will help ("a colleague will…").</summary>
    public const string AskedForHuman = "asked_for_human";
    /// <summary>The AI's answer could not be delivered (closed window, no number…).</summary>
    public const string SendBlocked = "send_blocked";
    /// <summary>A booking the AI tried to make failed (the booking tools).</summary>
    public const string BookingFailed = "booking_failed";
    /// <summary>The AI was not confident enough to answer.</summary>
    public const string LowConfidence = "low_confidence";
    /// <summary>An automation rule or a flow's Hand-over step.</summary>
    public const string Rule = "rule";
    /// <summary>A person flagged it for a human.</summary>
    public const string Manual = "manual";
    /// <summary>An unknown customer could not be matched to one contact (the identity exchange).</summary>
    public const string UnknownCustomer = "unknown_customer";

    public static readonly string[] All =
        { CheckFailed, AskedForHuman, SendBlocked, BookingFailed, LowConfidence, Rule, Manual, UnknownCustomer };

    public static string Label(string? kind) => kind switch
    {
        CheckFailed => "A reply check failed",
        AskedForHuman => "The AI asked for a person",
        SendBlocked => "The AI's reply could not be sent",
        BookingFailed => "A booking failed",
        LowConfidence => "Low confidence",
        Rule => "A rule or flow handed it over",
        Manual => "Flagged by a person",
        UnknownCustomer => "Unknown customer",
        _ => "Handed over",
    };
}

/// <summary>
/// Human handover — the ONE writer of the <c>handover_*</c> columns on a
/// conversation. Every site that passes a conversation to a person (the AI
/// gate's held drafts and failed sends, the assistant's handover outcome, the
/// AI's own "a person will help you", automation rules, a flow's Hand-over step,
/// a person flagging it, the booking/identity tools) calls <see cref="Mark"/>:
/// it stamps when/why/who, counts it, sets the conversation pending, adds the
/// private note, notifies the people who own it and audits <c>ai.handover</c>.
/// A person replying or resolving clears it (<see cref="Clear"/>; the fields
/// stay for history). Nothing here ever throws into a reply or webhook path.
/// </summary>
public static class Handover
{
    public const string AuditAction = "ai.handover";
    public const string ClearedAuditAction = "ai.handover.cleared";
    public const string RefKey = "ai-handover";

    public static bool IsOpen(Thread t) => t.HandoverAt != null && t.HandoverResolvedAt == null;

    /// <summary>
    /// Mark the conversation as handed to a person. Returns true when this
    /// opened a NEW handover (an already-open one just takes the latest reason —
    /// it is counted, noted and announced once). <paramref name="by"/> is
    /// <c>ai</c> · <c>rule:&lt;id&gt;</c> · <c>flow</c> · <c>agent:&lt;username&gt;</c>.
    /// <paramref name="note"/>/<paramref name="notify"/> = false where the caller
    /// already writes its own (the assistant's outcome, the AI monitor's
    /// "waiting for review" bell); <paramref name="save"/> = false to commit
    /// with the caller's own SaveChanges. <paramref name="standDown"/> (default)
    /// turns the chatbot off on the conversation — a person has it now, and
    /// "Let AI continue" turns it back on.
    /// </summary>
    public static bool Mark(AppDbContext db, Thread thread, string kind, string? reason, string by,
        bool note = true, bool notify = true, bool save = true, bool standDown = true)
    {
        try
        {
            var row = Tracked(db, thread);
            if (row == null) return false;
            var now = DateTime.UtcNow;
            kind = HandoverKinds.All.Contains(kind) ? kind : HandoverKinds.Manual;
            var why = Cap(string.IsNullOrWhiteSpace(reason) ? HandoverKinds.Label(kind) : reason.Trim(), 500);
            by = Cap(string.IsNullOrWhiteSpace(by) ? "ai" : by.Trim(), 80);

            var fresh = !IsOpen(row);
            if (fresh)
            {
                row.HandoverAt = now;
                row.HandoverResolvedAt = null;
                row.HandoverCount += 1;
            }
            row.HandoverReason = why;
            row.HandoverKind = kind;
            row.HandoverBy = by;
            if (row.Status is ThreadStatus.Open or ThreadStatus.Snoozed) row.Status = ThreadStatus.Pending;
            if (standDown) AiEligibility.Set(row, false);
            row.UpdatedAt = now;   // the SSE change token — the inbox refreshes
            Mirror(row, thread);

            if (note)
                db.Messages.Add(new Message
                {
                    ThreadId = row.Id, Direction = MessageDirection.Out, Source = MessageSource.System,
                    Private = true, Status = "note", AccountId = row.AccountId, CreatedAt = now,
                    Text = Cap($"{Who(by)} handed this conversation to a person — {Sentence(why)}"
                               + (standDown ? " The AI stands down until someone lets it continue." : ""), 1000),
                });
            AuditTrail.Add(db, AuditAction, $"{row.Channel}:{row.Key}", $"{kind}: {why}",
                actor: by, source: SourceOf(by), entityId: row.EntityId);
            if (notify && fresh) Notify(db, row, why);
            if (save) db.SaveChanges();
            return fresh;
        }
        catch
        {
            return false; // recording a handover must never break the path that asked for it
        }
    }

    /// <summary>
    /// Close an open handover (a person replied, resolved it, or let the AI
    /// continue). The when/why/who stay for history. Returns false when nothing
    /// was open. <paramref name="by"/> as in <see cref="Mark"/>.
    /// </summary>
    public static bool Clear(AppDbContext db, Thread thread, string by, string why, bool save = true)
    {
        try
        {
            var row = Tracked(db, thread);
            if (row == null || !IsOpen(row)) return false;
            var now = DateTime.UtcNow;
            row.HandoverResolvedAt = now;
            row.UpdatedAt = now;
            Mirror(row, thread);
            AuditTrail.Add(db, ClearedAuditAction, $"{row.Channel}:{row.Key}", Cap(why, 300),
                actor: Cap(by, 80), source: SourceOf(by), entityId: row.EntityId);
            if (save) db.SaveChanges();
            return true;
        }
        catch { return false; }
    }

    /// <summary>A person sent the customer a reply — the handover is answered.</summary>
    public static bool OnHumanReply(AppDbContext db, Thread thread, string? username) =>
        Clear(db, thread, Agent(username), "A person replied.");

    /// <summary>The conversation was resolved.</summary>
    public static bool OnResolved(AppDbContext db, Thread thread, string? username) =>
        Clear(db, thread, Agent(username), "The conversation was resolved.");

    public static string Agent(string? username) =>
        $"agent:{(string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim())}";

    // ---- the AI gate's signals -----------------------------------------------------

    /// <summary>
    /// The AI gate's handover sites, decided from a submitted draft: the
    /// assistant refused it (its checks handed over or blocked) → check_failed;
    /// the reply itself tells the customer a person will help → asked_for_human.
    /// Replies and follow-ups only (a flow's outbound is not a conversation the
    /// customer is waiting on); canned acknowledgements never.
    /// </summary>
    public static void FromAiDraft(AppDbContext db, Thread thread, string draftKind, string text,
        IDictionary<string, object?> ctx, bool canned)
    {
        try
        {
            if (canned || draftKind is not (AiDraftKind.Reply or AiDraftKind.FollowUp)) return;
            var action = AiGate.Str(ctx, "action", AiActions.Send);
            if (action is AiActions.Handover or AiActions.Block)
            {
                var why = AiGate.Str(ctx, "reason", "");
                Mark(db, thread, HandoverKinds.CheckFailed,
                    why.Length > 0 ? $"The AI's reply checks stopped its answer: {why}"
                        : "The AI's reply checks stopped its answer.", "ai", notify: false);
                return;
            }
            // the reply itself still has to reach the customer (its later bubbles are
            // re-checked against the AI switch), so the AI does not stand down here;
            // and no note — the reply that follows says it (the banner + chip show it)
            if (Humanizer.HandsOver(text))
                Mark(db, thread, HandoverKinds.AskedForHuman, "The AI told the customer a person will help.", "ai",
                    note: false, standDown: false);
        }
        catch { /* never breaks the gate */ }
    }

    /// <summary>
    /// An AI reply the gate could not deliver (a closed 24h window, no number
    /// to send from, Meta's messaging window…) leaves the customer without an
    /// answer — a person must step in. Not when the reason is that nobody should
    /// write at all (opted out, AI off, an unlinked conversation).
    /// </summary>
    public static void FromFailedDraft(AppDbContext db, AiDraft draft, string why)
    {
        try
        {
            if (draft.Kind != AiDraftKind.Reply || draft.ThreadId is not int tid) return;
            var thread = db.Threads.Find(tid);
            if (thread == null || thread.OptedOut) return;
            if (thread.ContactId == null && thread.Channel != ChatBranch.Domain.Channels.Website) return;
            if (!AiEligibility.Effective(db, new ScopingService(db), thread)) return;
            Mark(db, thread, HandoverKinds.SendBlocked, $"The AI's reply could not be sent: {why}", "ai");
        }
        catch { /* never breaks the gate */ }
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>The tracked row for this thread (the caller's instance when it is tracked).</summary>
    private static Thread? Tracked(AppDbContext db, Thread thread)
    {
        if (db.Entry(thread).State != EntityState.Detached) return thread;
        return db.Threads.Find(thread.Id);
    }

    private static void Mirror(Thread from, Thread to)
    {
        if (ReferenceEquals(from, to)) return;
        to.HandoverAt = from.HandoverAt;
        to.HandoverReason = from.HandoverReason;
        to.HandoverBy = from.HandoverBy;
        to.HandoverKind = from.HandoverKind;
        to.HandoverResolvedAt = from.HandoverResolvedAt;
        to.HandoverCount = from.HandoverCount;
        to.Status = from.Status;
        to.AiEnabled = from.AiEnabled;
        to.AiOverrides = from.AiOverrides;
        to.UpdatedAt = from.UpdatedAt;
    }

    private static string Who(string by) =>
        by == "ai" ? "The AI"
        : by == "flow" ? "A flow"
        : by.StartsWith("rule:") ? "An automation rule"
        : by.StartsWith("agent:") ? by[6..]
        : by;

    private static string SourceOf(string by) =>
        by.StartsWith("rule:") ? AuditSources.Automation
        : by == "flow" ? AuditSources.Flow
        : by.StartsWith("agent:") ? (AuditContext.Current?.Source is { Length: > 0 } s ? s : AuditSources.User)
        : AuditSources.System;

    /// <summary>The conversation's owners: its assignee, its team, else the tenant's admins.</summary>
    private static void Notify(AppDbContext db, Thread t, string why)
    {
        try
        {
            var ids = new List<int>();
            if (!string.IsNullOrWhiteSpace(t.Assignee))
                ids.AddRange(db.Users.AsNoTracking().Where(u => u.Username == t.Assignee).Select(u => u.Id));
            if (t.TeamId is int team)
                ids.AddRange(db.TeamMembers.AsNoTracking().Where(m => m.TeamId == team).Select(m => m.UserId));
            int? tenantId = t.EntityId is int eid
                ? db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => (int?)e.TenantId).FirstOrDefault()
                : null;
            var who = string.IsNullOrWhiteSpace(t.Name) ? t.Key : t.Name;
            NotificationService.Notify(db, NotificationCategories.Messages, "message",
                Cap($"Needs a human · {who}", 160), Cap(why, 400),
                $"/chats?thread={Uri.EscapeDataString(t.Channel)}:{Uri.EscapeDataString(t.Key)}",
                new NotificationAudience
                {
                    UserIds = ids.Distinct().ToArray(),
                    RoleSlugs = ids.Count == 0 ? new[] { Roles.SuperAdmin, Roles.Admin } : Array.Empty<string>(),
                    TenantId = tenantId,
                },
                new NotifyOptions { Thread = t, RefKey = $"{RefKey}:{t.Id}", Save = false });
        }
        catch { /* a bell never blocks a handover */ }
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>The reason as a sentence, so the note's next sentence doesn't run on.</summary>
    private static string Sentence(string text)
    {
        var t = (text ?? "").TrimEnd();
        return t.Length == 0 || ".!?…".Contains(t[^1]) ? t : t + ".";
    }
}
