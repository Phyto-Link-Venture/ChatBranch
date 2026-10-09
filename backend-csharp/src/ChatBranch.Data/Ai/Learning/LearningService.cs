using System.Text.Json;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai.Learning;

/// <summary>Who gave the feedback (null user = the system).</summary>
public sealed record LearningActor(int? UserId, string Username, string Role)
{
    public bool IsAdmin => Role is Roles.SuperAdmin or Roles.Admin;
}

/// <summary>The outcome of a rating: the row and the lesson it fed, or an error + HTTP status.</summary>
public sealed record FeedbackResult(AiFeedback? Feedback, AiLesson? Lesson, string? Error = null, int Status = 200)
{
    public bool Ok => Error == null;
}

/// <summary>
/// AI learning: how people's judgement of the AI's replies becomes lessons.
///
/// * Feedback — explicit 👍/👎 (one per user per reply, re-rating replaces it)
///   and implicit, written by the AI monitor's decisions: approve-as-is
///   (+1, weight 0.5), edit-then-send (−1, weight 0.5, the sent text as the
///   correction), reject (−1, the reason as the note). Every row snapshots the
///   customer's question and the AI's answer.
/// * Lessons — 👍/approved → <c>example</c>, 👎 with a correction / edited →
///   <c>correction</c>, plus hand-written <c>rule</c>s. Setting
///   <c>ai_learning_mode</c> (entity-scoped): review (default — every lesson
///   waits for a person with ai.behaviour.manage), auto (an admin's feedback
///   goes live at once; everyone else's still waits), off (feedback is counted,
///   nothing is learned). A suggestion whose question is near-identical to an
///   active/suggested lesson of the same scope MERGES into it (source_count++;
///   the latest correction wins — on an active lesson that sends it back to the
///   review queue unless an admin in auto mode gave it). Normalized-text matches
///   merge immediately; embedding near-duplicates (cosine ≥ 0.95) are merged by
///   the embedding worker (<see cref="MergeNearDuplicates"/>).
///
/// Nothing here may throw into the AI gate: the implicit hook swallows errors.
/// </summary>
public class LearningService
{
    public const string ModeKey = AiBehaviourSettings.LearningMode;
    public const int QuestionCap = 1000;
    public const double DuplicateCosine = 0.95;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly TimeProvider _clock;

    public LearningService(AppDbContext db, ScopingService scoping, TimeProvider? clock = null)
    {
        _db = db;
        _scoping = scoping;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>review | auto | off for an entity (entity row → org row → "review").</summary>
    public string Mode(int? entityId)
    {
        var v = _scoping.SettingValue(ModeKey, entityId).Trim().ToLowerInvariant();
        return AiBehaviourSettings.LearningModes.Contains(v) ? v : AiBehaviourSettings.DefaultFor(ModeKey);
    }

    public LearningActor ActorFor(string? username)
    {
        var name = (username ?? "").Trim();
        if (name.Length == 0) return new LearningActor(null, "system", "");
        var user = _db.Users.AsNoTracking().Where(u => u.Username == name).Select(u => new { u.Id }).FirstOrDefault();
        var role = user == null ? "" : _db.Profiles.AsNoTracking().Where(p => p.UserId == user.Id)
            .Select(p => p.Role).FirstOrDefault() ?? "";
        return new LearningActor(user?.Id, name, role);
    }

    // ---- explicit feedback -------------------------------------------------------

    /// <summary>
    /// Rate an AI-written message (<paramref name="messageId"/>) or a draft in the
    /// monitor (<paramref name="draftId"/>). The two are linked when the draft was
    /// sent, so a reply rated in the chat and in the monitor is ONE rating.
    /// <paramref name="canSee"/> decides whether the caller may see a
    /// conversation of that entity (null = unassigned).
    /// </summary>
    public FeedbackResult Rate(LearningActor actor, int? messageId, int? draftId, int rating,
        IEnumerable<string>? reasons, string? note, string? correction, Func<int?, bool> canSee)
    {
        if (actor.UserId is not int userId) return new FeedbackResult(null, null, "Sign in to rate replies.", 401);
        if (rating is not (1 or -1)) return new FeedbackResult(null, null, "rating must be 1 or -1.", 400);
        var reasonList = (reasons ?? Array.Empty<string>()).Select(r => (r ?? "").Trim().ToLowerInvariant())
            .Where(r => r.Length > 0).Distinct().ToList();
        var unknown = reasonList.Where(r => !AiFeedbackReasons.All.Contains(r)).ToList();
        if (unknown.Count > 0)
            return new FeedbackResult(null, null,
                $"Unknown reason '{unknown[0]}'. Use: {string.Join(", ", AiFeedbackReasons.All)}.", 400);

        Message? msg = null;
        AiDraft? draft = null;
        if (messageId is int mid)
        {
            msg = _db.Messages.AsNoTracking().FirstOrDefault(m => m.Id == mid);
            if (msg == null) return new FeedbackResult(null, null, "Message not found.", 404);
            if (msg.Direction != MessageDirection.Out || msg.Source != MessageSource.Ai || msg.Private)
                return new FeedbackResult(null, null, "Only replies the AI wrote can be rated.", 400);
            draft = DraftForMessage(msg);
        }
        else if (draftId is int did)
        {
            draft = _db.AiDrafts.AsNoTracking().FirstOrDefault(d => d.Id == did);
            if (draft == null) return new FeedbackResult(null, null, "Draft not found.", 404);
            if (string.IsNullOrWhiteSpace(draft.Content) && string.IsNullOrWhiteSpace(draft.OriginalContent))
                return new FeedbackResult(null, null, "This draft has no text to rate.", 400);
            if (draft.SentMessageId is int smid) msg = _db.Messages.AsNoTracking().FirstOrDefault(m => m.Id == smid);
        }
        else return new FeedbackResult(null, null, "message_id or draft_id is required.", 400);

        var threadId = msg?.ThreadId ?? draft?.ThreadId;
        var thread = threadId is int tid ? _db.Threads.AsNoTracking().FirstOrDefault(t => t.Id == tid) : null;
        var entityId = thread?.EntityId ?? draft?.EntityId;
        if (!canSee(entityId))
            return new FeedbackResult(null, null, msg != null ? "Message not found." : "Draft not found.", 404);

        var msgKey = msg?.Id;
        var draftKey = draft?.Id;
        var existing = _db.AiFeedback
            .Where(f => f.Kind == AiFeedbackKind.Explicit && f.UserId == userId
                        && ((msgKey != null && f.MessageId == msgKey) || (draftKey != null && f.DraftId == draftKey)))
            .OrderBy(f => f.Id).FirstOrDefault();
        var previousLesson = existing?.LessonId;
        var now = Now;
        var fb = existing ?? new AiFeedback { Kind = AiFeedbackKind.Explicit, UserId = userId, CreatedAt = now };
        fb.EntityId = entityId;
        fb.ThreadId = threadId;
        fb.MessageId = msgKey ?? fb.MessageId;
        fb.DraftId = draftKey ?? fb.DraftId;
        fb.ContactId = thread?.ContactId ?? draft?.ContactId;
        fb.Channel = thread?.Channel ?? draft?.Channel ?? "";
        fb.Rating = rating;
        fb.Weight = 1;
        fb.Reasons = rating < 0 ? reasonList : new List<string>();
        fb.Note = Cap((note ?? "").Trim(), 1000);
        fb.Correction = rating < 0 ? Cap((correction ?? "").Trim(), 2000) : "";
        fb.Question = QuestionFor(msg, draft);
        fb.Answer = Cap(draft != null ? draft.Content : msg?.Text ?? "", 4000);
        fb.UpdatedAt = now;
        if (existing == null) _db.AiFeedback.Add(fb);
        try { _db.SaveChanges(); }
        catch (DbUpdateException)
        {
            return new FeedbackResult(null, null, "This rating is already being saved — try again.", 409);
        }

        var lesson = Suggest(fb, actor, previousLesson);
        if (previousLesson is int prev && prev != lesson?.Id) Unlink(prev);
        fb.LessonId = lesson?.Id;
        _db.SaveChanges();
        AuditTrail.Write(_db, "ai.feedback", msgKey != null ? $"message:{msgKey}" : $"draft:{draftKey}",
            Describe(fb), actor: actor.Username, entityId: entityId, userId: userId);
        return new FeedbackResult(fb, lesson);
    }

    /// <summary>Withdraw one's own rating (a suggestion it alone fed goes with it).</summary>
    public bool Withdraw(int feedbackId, int userId)
    {
        var fb = _db.AiFeedback.FirstOrDefault(f => f.Id == feedbackId && f.UserId == userId
                                                    && f.Kind == AiFeedbackKind.Explicit);
        if (fb == null) return false;
        var lessonId = fb.LessonId;
        _db.AiFeedback.Remove(fb);
        _db.SaveChanges();
        if (lessonId is int lid) Unlink(lid);
        return true;
    }

    /// <summary>The draft a sent AI message came from (the first bubble, or a later one of a split reply).</summary>
    public AiDraft? DraftForMessage(Message msg)
    {
        var d = _db.AiDrafts.AsNoTracking().FirstOrDefault(x => x.SentMessageId == msg.Id);
        if (d != null) return d;
        var text = (msg.Text ?? "").Trim();
        if (text.Length == 0) return null;
        return _db.AiDrafts.AsNoTracking()
            .Where(x => x.ThreadId == msg.ThreadId && x.CreatedAt <= msg.CreatedAt && x.SentAt != null)
            .OrderByDescending(x => x.Id).Take(5).ToList()
            .FirstOrDefault(x => x.Content.Contains(text));
    }

    // ---- implicit feedback (the AI monitor's decisions) ----------------------------

    /// <summary>
    /// A reviewer decided on a held draft: approved as written, edited then sent,
    /// or rejected. Called by <see cref="AiGate"/>; never throws.
    /// </summary>
    public static void OnDraftDecided(AppDbContext db, ScopingService scoping, AiDraft draft, string kind,
        string actor, string? reason = null, TimeProvider? clock = null)
    {
        try { new LearningService(db, scoping, clock).RecordDecision(draft, kind, actor, reason); }
        catch
        {
            // learning never breaks a review decision — and a half-written row must
            // not ride along on the gate's next SaveChanges
            foreach (var e in db.ChangeTracker.Entries()
                         .Where(e => e.Entity is AiFeedback or AiLesson
                                     && e.State is EntityState.Added or EntityState.Modified).ToList())
                e.State = EntityState.Detached;
        }
    }

    public AiFeedback? RecordDecision(AiDraft draft, string kind, string actorName, string? reason)
    {
        if (kind is not (AiFeedbackKind.Approved or AiFeedbackKind.Edited or AiFeedbackKind.Rejected)) return null;
        var actor = ActorFor(actorName);
        var now = Now;
        var aiText = kind == AiFeedbackKind.Edited && draft.OriginalContent.Length > 0 ? draft.OriginalContent : draft.Content;
        var fb = new AiFeedback
        {
            Kind = kind,
            Rating = kind == AiFeedbackKind.Approved ? 1 : -1,
            Weight = kind == AiFeedbackKind.Rejected ? 1 : 0.5,
            EntityId = draft.EntityId, ThreadId = draft.ThreadId, DraftId = draft.Id, MessageId = draft.SentMessageId,
            ContactId = draft.ContactId, Channel = draft.Channel,
            Note = kind == AiFeedbackKind.Rejected ? Cap((reason ?? "").Trim(), 1000) : "",
            Question = QuestionFor(null, draft),
            Answer = Cap(aiText, 4000),
            Correction = kind == AiFeedbackKind.Edited ? Cap(draft.Content, 2000) : "",
            UserId = actor.UserId,
            CreatedAt = now, UpdatedAt = now,
        };
        _db.AiFeedback.Add(fb);
        _db.SaveChanges();
        var lesson = Suggest(fb, actor, null);
        if (lesson != null)
        {
            fb.LessonId = lesson.Id;
            _db.SaveChanges();
        }
        AuditTrail.Write(_db, "ai.feedback", $"draft:{draft.Id}", Describe(fb), actor: actor.Username,
            entityId: draft.EntityId, userId: actor.UserId);
        return fb;
    }

    // ---- lessons -------------------------------------------------------------------

    /// <summary>
    /// Turn a feedback row into a lesson suggestion (or merge it into an existing
    /// one). Returns the lesson it fed, or null (mode off, nothing to learn from).
    /// </summary>
    public AiLesson? Suggest(AiFeedback fb, LearningActor actor, int? previousLessonId)
    {
        var mode = Mode(fb.EntityId);
        if (mode == "off") return null;

        string kind, answer, avoid = "";
        if (fb.Rating > 0 && fb.Kind is AiFeedbackKind.Explicit or AiFeedbackKind.Approved)
        {
            kind = AiLessonKind.Example;
            answer = fb.Answer;
        }
        else if (fb.Kind == AiFeedbackKind.Edited
                 || (fb.Kind == AiFeedbackKind.Explicit && fb.Rating < 0 && fb.Correction.Trim().Length > 0))
        {
            kind = AiLessonKind.Correction;
            answer = fb.Correction;
            avoid = fb.Answer;
        }
        else return null;
        answer = (answer ?? "").Trim();
        if (string.IsNullOrWhiteSpace(fb.Question) || answer.Length == 0) return null;

        var now = Now;
        var autoActive = mode == "auto" && actor.IsAdmin;
        var norm = TextAnalysis.NormalizeQuestion(fb.Question);
        var target = _db.AiLessons
            .Where(l => (l.Status == AiLessonStatus.Active || l.Status == AiLessonStatus.Suggested)
                        && l.Kind != AiLessonKind.Rule && l.EntityId == fb.EntityId)
            .OrderByDescending(l => l.Id).Take(5000).ToList()
            .FirstOrDefault(l => TextAnalysis.NormalizeQuestion(l.Question) == norm);

        if (target != null)
        {
            if (target.Id != previousLessonId) target.SourceCount++;
            if (kind == AiLessonKind.Correction)
                ApplyCorrection(target, answer, avoid, autoActive, actor.Username);
            target.UpdatedAt = now;
            _db.SaveChanges();
            return target;
        }

        var lesson = new AiLesson
        {
            EntityId = fb.EntityId, Kind = kind,
            Status = autoActive ? AiLessonStatus.Active : AiLessonStatus.Suggested,
            Question = Cap(fb.Question.Trim(), QuestionCap), Answer = Cap(answer, 2000), Avoid = Cap(avoid, 2000),
            Note = kind == AiLessonKind.Correction ? Cap(fb.Note, 500) : "",
            Channels = new List<string>(), SourceCount = 1,
            CreatedBy = actor.Username, ApprovedBy = autoActive ? actor.Username : "",
            CreatedAt = now, UpdatedAt = now,
        };
        _db.AiLessons.Add(lesson);
        _db.SaveChanges();
        if (autoActive)
            AuditTrail.Write(_db, "ai.lesson.approved", $"lesson:{lesson.Id}", "auto (learning mode auto)",
                actor: actor.Username, entityId: lesson.EntityId, userId: actor.UserId);
        return lesson;
    }

    /// <summary>"The latest correction wins": a suggestion is simply updated; an
    /// active lesson is updated in place only by an admin in auto mode — anyone
    /// else's correction sends it back to the review queue carrying the new answer.</summary>
    private void ApplyCorrection(AiLesson target, string answer, string avoid, bool autoActive, string actor)
    {
        var changed = target.Kind != AiLessonKind.Correction || target.Answer.Trim() != answer.Trim()
                      || (avoid.Length > 0 && target.Avoid.Trim() != avoid.Trim());
        if (!changed) return;
        if (target.Status == AiLessonStatus.Active && !autoActive)
        {
            target.Note = Cap(JoinNote(target.Note,
                $"Was live with: \"{TextAnalysis.Clip(target.Answer, 300)}\" — a newer correction needs review."), 1000);
            target.Status = AiLessonStatus.Suggested;
            target.ApprovedBy = "";
        }
        else if (target.Status == AiLessonStatus.Active)
            AuditTrail.Add(_db, "ai.lesson.edited", $"lesson:{target.Id}", "newer correction (learning mode auto)",
                actor: actor, entityId: target.EntityId);
        target.Kind = AiLessonKind.Correction;
        target.Answer = Cap(answer, 2000);
        if (avoid.Length > 0) target.Avoid = Cap(avoid, 2000);
    }

    /// <summary>A rating that fed this lesson was withdrawn or changed.</summary>
    private void Unlink(int lessonId)
    {
        var l = _db.AiLessons.FirstOrDefault(x => x.Id == lessonId);
        if (l == null) return;
        var still = _db.AiFeedback.Count(f => f.LessonId == lessonId);
        l.SourceCount = Math.Max(still, 0);
        if (l.Status == AiLessonStatus.Suggested && still == 0 && l.Kind != AiLessonKind.Rule)
        {
            _db.AiLessons.Remove(l); // an orphaned suggestion: nobody asked for it any more
            _db.AiEmbeddings.Where(e => e.OwnerType == EmbeddingOwner.Lesson && e.OwnerId == lessonId).ExecuteDelete();
        }
        else l.UpdatedAt = Now;
        _db.SaveChanges();
    }

    /// <summary>
    /// The embedding worker's semantic dedup: a SUGGESTED lesson whose question
    /// vector is within cosine ≥ 0.95 of another active/suggested lesson of the
    /// same scope is merged into the older one (counts summed, feedback re-pointed,
    /// the latest correction wins). Two active lessons are never merged blindly.
    /// </summary>
    public int MergeNearDuplicates(IReadOnlyDictionary<int, float[]> vectors)
    {
        var merged = 0;
        var lessons = _db.AiLessons
            .Where(l => (l.Status == AiLessonStatus.Active || l.Status == AiLessonStatus.Suggested)
                        && l.Kind != AiLessonKind.Rule)
            .OrderBy(l => l.Id).ToList();
        var gone = new HashSet<int>();
        foreach (var s in lessons.Where(l => l.Status == AiLessonStatus.Suggested).OrderByDescending(l => l.Id).ToList())
        {
            if (gone.Contains(s.Id) || !vectors.TryGetValue(s.Id, out var sv)) continue;
            var target = lessons.Where(t => t.Id < s.Id && !gone.Contains(t.Id) && t.EntityId == s.EntityId
                                            && vectors.ContainsKey(t.Id))
                .Select(t => (t, cos: VectorMath.Cosine(sv, vectors[t.Id])))
                .Where(x => x.cos >= DuplicateCosine)
                .OrderByDescending(x => x.cos).Select(x => x.t).FirstOrDefault();
            if (target == null) continue;
            target.SourceCount += Math.Max(1, s.SourceCount);
            if (s.Kind == AiLessonKind.Correction && s.UpdatedAt >= target.UpdatedAt)
                ApplyCorrection(target, s.Answer, s.Avoid, autoActive: false, actor: "system");
            target.UpdatedAt = Now;
            var sid = s.Id;
            _db.AiFeedback.Where(f => f.LessonId == sid).ExecuteUpdate(u => u.SetProperty(f => f.LessonId, target.Id));
            _db.AiEmbeddings.Where(e => e.OwnerType == EmbeddingOwner.Lesson && e.OwnerId == sid).ExecuteDelete();
            _db.AiLessons.Remove(s);
            gone.Add(sid);
            merged++;
        }
        if (merged > 0) _db.SaveChanges();
        return merged;
    }

    /// <summary>The status moves a person may make (anything else is a 409).</summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (AiLessonStatus.Suggested, AiLessonStatus.Active) => true,
        (AiLessonStatus.Suggested, AiLessonStatus.Dismissed) => true,
        (AiLessonStatus.Active, AiLessonStatus.Archived) => true,
        (AiLessonStatus.Archived, AiLessonStatus.Active) => true,
        _ => from == to,
    };

    /// <summary>Apply a status move + audit it. Returns an error for a move that isn't allowed.</summary>
    public string? Move(AiLesson l, string to, LearningActor actor)
    {
        to = (to ?? "").Trim().ToLowerInvariant();
        if (!AiLessonStatus.All.Contains(to)) return $"status must be one of {string.Join(", ", AiLessonStatus.All)}.";
        if (!CanMove(l.Status, to))
            return $"A {l.Status} lesson can't become {to} (allowed: suggested → active | dismissed, "
                   + "active → archived, archived → active).";
        if (l.Status == to) return null;
        l.Status = to;
        l.UpdatedAt = Now;
        if (to == AiLessonStatus.Active) l.ApprovedBy = actor.Username;
        AuditTrail.Add(_db, to switch
        {
            AiLessonStatus.Active => "ai.lesson.approved",
            AiLessonStatus.Dismissed => "ai.lesson.dismissed",
            _ => "ai.lesson.archived",
        }, $"lesson:{l.Id}", TextAnalysis.Clip(l.Question.Length > 0 ? l.Question : l.Answer, 200),
            actor: actor.Username, entityId: l.EntityId, userId: actor.UserId);
        return null;
    }

    /// <summary>
    /// Turn a lesson into a knowledge item (an FAQ; a rule becomes a policy) and
    /// archive the lesson. Returns the new item.
    /// </summary>
    public KnowledgeItem ToKnowledge(AiLesson l, LearningActor actor)
    {
        var now = Now;
        var rule = l.Kind == AiLessonKind.Rule;
        var item = new KnowledgeItem
        {
            EntityId = l.EntityId,
            Kind = rule ? "policy" : "faq",
            Title = TextAnalysis.Clip(rule ? l.Answer : l.Question, 120).Replace('\n', ' '),
            Question = rule ? "" : l.Question,
            Content = l.Answer,
            Channels = new List<string>(l.Channels),
            Active = true, Published = false,
            CreatedBy = actor.Username, CreatedAt = now, UpdatedAt = now,
        };
        _db.KnowledgeItems.Add(item);
        _db.SaveChanges();
        if (l.Status != AiLessonStatus.Archived)
        {
            l.Status = AiLessonStatus.Archived;
            l.UpdatedAt = now;
        }
        l.Note = Cap(JoinNote(l.Note, $"Turned into knowledge #{item.Id}."), 1000);
        AuditTrail.Add(_db, "ai.lesson.archived", $"lesson:{l.Id}", $"turned into knowledge #{item.Id}",
            actor: actor.Username, entityId: l.EntityId, userId: actor.UserId);
        _db.SaveChanges();
        return item;
    }

    // ---- snapshots --------------------------------------------------------------------

    /// <summary>
    /// The customer message(s) a reply answered: a draft's recorded
    /// <c>answering</c> list, else the inbound run before the message (skipping
    /// earlier bubbles of the same reply). Capped to the last 1000 chars.
    /// </summary>
    public string QuestionFor(Message? msg, AiDraft? draft)
    {
        try
        {
            if (draft != null)
            {
                var texts = AiGate.Items(draft.Context, "answering")
                    .Select(x => AiGate.Str(x, "text", "")).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                if (texts.Count > 0) return Tail(string.Join("\n", texts.Select(t => t.Trim())), QuestionCap);
            }
            int? threadId = msg?.ThreadId ?? draft?.ThreadId;
            if (threadId is not int tid) return "";
            var q = _db.Messages.AsNoTracking().Where(m => m.ThreadId == tid && !m.Private);
            if (msg != null) q = q.Where(m => m.Id < msg.Id);
            else if (draft != null) { var at = draft.CreatedAt; q = q.Where(m => m.CreatedAt <= at); }
            var prior = q.OrderByDescending(m => m.Id).Take(30).ToList();
            var i = 0;
            var anchor = msg?.CreatedAt ?? draft?.CreatedAt ?? DateTime.MaxValue;
            // earlier bubbles of the same AI reply sit right before a later bubble
            while (i < prior.Count && prior[i].Direction == MessageDirection.Out && prior[i].Source == MessageSource.Ai
                   && (anchor - prior[i].CreatedAt).TotalMinutes <= 10) i++;
            var collected = new List<string>();
            while (i < prior.Count && prior[i].Direction == MessageDirection.In)
            {
                if (!string.IsNullOrWhiteSpace(prior[i].Text)) collected.Insert(0, prior[i].Text.Trim());
                i++;
            }
            return Tail(string.Join("\n", collected), QuestionCap);
        }
        catch { return ""; }
    }

    // ---- stats + knowledge gaps --------------------------------------------------------

    /// <param name="scope">Entity ids whose conversations the caller sees (null = all); unassigned always shown.</param>
    public object Stats(List<int>? scope, int days)
    {
        var span = Math.Clamp(days, 1, 365);
        var now = Now;
        var since = now.Date.AddDays(-(span - 1));
        var fq = _db.AiFeedback.AsNoTracking().Where(f => f.CreatedAt >= since);
        if (scope != null) fq = fq.Where(f => f.EntityId == null || scope.Contains(f.EntityId.Value));
        var rows = fq.Select(f => new
        {
            f.Kind, f.Rating, f.Reasons, f.Question, f.CreatedAt, f.ThreadId,
        }).Take(50_000).ToList();

        var explicitRows = rows.Where(r => r.Kind == AiFeedbackKind.Explicit).ToList();
        var up = explicitRows.Count(r => r.Rating > 0);
        var down = explicitRows.Count(r => r.Rating < 0);
        var totals = new
        {
            rated = explicitRows.Count,
            up,
            down,
            up_rate = up + down == 0 ? (double?)null : Math.Round(up * 100.0 / (up + down), 1),
            approved = rows.Count(r => r.Kind == AiFeedbackKind.Approved),
            edited = rows.Count(r => r.Kind == AiFeedbackKind.Edited),
            rejected = rows.Count(r => r.Kind == AiFeedbackKind.Rejected),
        };
        var daily = Enumerable.Range(0, span).Select(i => since.AddDays(i)).Select(day => new
        {
            day = day.ToString("yyyy-MM-dd"),
            up = explicitRows.Count(r => r.CreatedAt.Date == day && r.Rating > 0),
            down = explicitRows.Count(r => r.CreatedAt.Date == day && r.Rating < 0),
        }).ToList();
        var reasons = explicitRows.Where(r => r.Rating < 0).SelectMany(r => r.Reasons.Distinct())
            .GroupBy(r => r).Select(g => new { reason = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count).ThenBy(x => Array.IndexOf(AiFeedbackReasons.All, x.reason)).ToList();

        // ---- knowledge gaps: what the AI could not answer
        var samples = new List<(string Question, int? ThreadId, DateTime At)>();
        foreach (var r in explicitRows.Where(r => r.Rating < 0
                     && (r.Reasons.Contains(AiFeedbackReasons.WrongInfo) || r.Reasons.Contains(AiFeedbackReasons.DidntAnswer))))
            samples.Add((r.Question, r.ThreadId, r.CreatedAt));
        var dq = _db.AiDrafts.AsNoTracking().Where(d => d.CreatedAt >= since);
        if (scope != null) dq = dq.Where(d => d.EntityId == null || scope.Contains(d.EntityId.Value));
        foreach (var d in dq.OrderByDescending(d => d.Id).Take(5000).ToList())
        {
            var action = AiGate.Str(d.Context, "action", "");
            var flags = AiGate.Items(d.Context, "flags").Select(x => x is JsonElement j ? j.ToString() : x?.ToString() ?? "");
            var handedOver = action is AiActions.Handover or AiActions.Block
                             || AiGate.Bool(d.Context, "handover") == true
                             || flags.Any(f => f.Contains("hand over", StringComparison.OrdinalIgnoreCase));
            if (!handedOver) continue;
            var q = QuestionFor(null, d);
            if (q.Length > 0) samples.Add((q, d.ThreadId, d.CreatedAt));
        }
        var gaps = Gaps(samples);
        return new { days = span, totals, daily, reasons, gaps };
    }

    private List<object> Gaps(List<(string Question, int? ThreadId, DateTime At)> samples)
    {
        var groups = new List<(HashSet<string> Core, List<(string Q, int? T, DateTime At, List<string> Words)> Items)>();
        foreach (var s in samples.OrderBy(s => s.At))
        {
            var words = TextAnalysis.Words(s.Question).Where(w => w.Length >= 2 && !TextAnalysis.IsStopword(w)).ToList();
            var terms = words.Select(TextAnalysis.Stem).Where(t => t.Length >= 2).ToHashSet();
            if (terms.Count == 0) continue;
            var home = groups.FirstOrDefault(g =>
            {
                var shared = g.Core.Count(terms.Contains);
                var union = g.Core.Count + terms.Count - shared;
                return shared >= 2 || (union > 0 && shared / (double)union >= 0.5);
            });
            if (home.Items == null)
            {
                home = (terms, new List<(string, int?, DateTime, List<string>)>());
                groups.Add(home);
            }
            home.Items.Add((s.Question, s.ThreadId, s.At, words));
        }
        var threadIds = groups.SelectMany(g => g.Items.Select(i => i.T)).Where(t => t != null).Select(t => t!.Value)
            .Distinct().ToList();
        var threads = _db.Threads.AsNoTracking().Where(t => threadIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Channel, t.Key }).ToList().ToDictionary(t => t.Id);
        return groups.Select(g =>
            {
                var latest = g.Items.OrderByDescending(i => i.At).First();
                // the topic: the group's most frequent content words (shown as typed, not stemmed)
                var surface = g.Items.SelectMany(i => i.Words.Distinct()).GroupBy(TextAnalysis.Stem)
                    .Select(x => (n: x.Count(), core: g.Core.Contains(x.Key),
                        word: x.GroupBy(w => w).OrderByDescending(y => y.Count()).First().Key))
                    .OrderByDescending(x => x.n).ThenByDescending(x => x.core).ThenBy(x => x.word, StringComparer.Ordinal)
                    .Take(3).Select(x => x.word);
                var thread = latest.T is int t && threads.TryGetValue(t, out var th) ? $"{th.Channel}:{th.Key}" : null;
                return (count: g.Items.Count, at: latest.At, row: (object)new
                {
                    topic = string.Join(", ", surface),
                    count = g.Items.Count,
                    example = TextAnalysis.Clip(latest.Q, 300),
                    thread,
                    last_at = latest.At,
                });
            })
            .OrderByDescending(x => x.count).ThenByDescending(x => x.at).Take(10).Select(x => x.row).ToList();
    }

    // ---- helpers --------------------------------------------------------------------

    private static string Describe(AiFeedback fb)
    {
        var sign = fb.Rating > 0 ? "+1" : "-1";
        var bits = new List<string> { fb.Kind == AiFeedbackKind.Explicit ? sign : $"{fb.Kind} ({sign})" };
        if (fb.Reasons.Count > 0) bits.Add(string.Join(", ", fb.Reasons));
        if (fb.Correction.Length > 0) bits.Add("with a correction");
        return string.Join(" · ", bits);
    }

    private static string JoinNote(string existing, string add) =>
        string.IsNullOrWhiteSpace(existing) ? add : existing.TrimEnd() + "\n" + add;

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];

    private static string Cap(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n];
    }
}
