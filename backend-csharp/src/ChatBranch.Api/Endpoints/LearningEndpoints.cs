using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Workers;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// AI learning (Data/Ai/Learning/): feedback on AI replies, the lessons drawn
/// from it, the retrieval index status and the quality stats.
///
/// * <c>POST/GET/DELETE /api/ai/feedback</c> — 👍/👎 on an AI-written message or
///   an AI-monitor draft (the caller's own rating; re-rating replaces it).
/// * <c>/api/ai/lessons</c> — the suggested-lesson queue and the active library
///   (approve / edit / dismiss / archive / turn into knowledge / hand-written rules).
/// * <c>/api/ai/learning/status|reindex|stats</c>.
///
/// Reads need <c>ai.monitor</c> (agents hold it); lesson writes and re-indexing
/// <c>ai.behaviour.manage</c>; deleting a lesson is admin+. Feedback and stats
/// follow the conversation scope (<see cref="ShellScope.ThreadEntityIds"/>),
/// lessons the library scope (<see cref="ShellScope.ScopeLibrary{T}"/>).
/// </summary>
public static class LearningEndpoints
{
    public const string MonitorCap = "ai.monitor";
    public const string BehaviourCap = "ai.behaviour.manage";

    public static void MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- feedback --------------------------------------------------------------
        app.MapPost("/api/ai/feedback", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, LearningService learning) =>
        {
            var body = await ReadJson(ctx);
            if (body is not JsonElement b || b.ValueKind != JsonValueKind.Object)
                return Results.Json(new { detail = "Send {message_id | draft_id, rating}." }, statusCode: 400);
            var messageId = Int(b, "message_id");
            var draftId = Int(b, "draft_id");
            var rating = Int(b, "rating");
            if (rating is not (1 or -1))
                return Results.Json(new { detail = "rating must be 1 or -1." }, statusCode: 400);
            var scope = ShellScope.ThreadEntityIds(db, sc, me, null);
            var actor = learning.ActorFor(me.Identity?.Name);
            var r = learning.Rate(actor, messageId, messageId == null ? draftId : null, rating.Value,
                StrList(b, "reasons"), Str(b, "note"), Str(b, "correction"),
                eid => scope == null || eid == null || scope.Contains(eid.Value));
            if (!r.Ok) return Results.Json(new { detail = r.Error }, statusCode: r.Status);
            if (r.Lesson != null) EmbeddingWorker.Kick();
            var fb = r.Feedback!;
            return Results.Ok(new
            {
                feedback = new
                {
                    id = fb.Id, rating = fb.Rating, reasons = fb.Reasons, note = fb.Note, correction = fb.Correction,
                    message_id = fb.MessageId, draft_id = fb.DraftId,
                    lesson_id = r.Lesson?.Id, lesson_status = r.Lesson?.Status,
                },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(MonitorCap);

        // the CALLER's ratings for the AI messages on screen (paints the thumbs)
        app.MapGet("/api/ai/feedback", async (string? message_ids, AppDbContext db, ClaimsPrincipal me,
            LearningService learning) =>
        {
            var ids = (message_ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct().Take(500).ToList();
            var actor = learning.ActorFor(me.Identity?.Name);
            var rows = ids.Count == 0 || actor.UserId == null
                ? new List<AiFeedback>()
                : await db.AiFeedback.AsNoTracking()
                    .Where(f => f.Kind == AiFeedbackKind.Explicit && f.UserId == actor.UserId
                                && f.MessageId != null && ids.Contains(f.MessageId.Value))
                    .ToListAsync();
            var map = rows.GroupBy(f => f.MessageId!.Value).ToDictionary(g => g.Key.ToString(), g =>
            {
                var f = g.OrderByDescending(x => x.UpdatedAt).First();
                return (object)new { id = f.Id, rating = f.Rating, reasons = f.Reasons, note = f.Note, correction = f.Correction };
            });
            return Results.Ok(new { feedback = map });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(MonitorCap);

        app.MapDelete("/api/ai/feedback/{id:int}", (int id, ClaimsPrincipal me, LearningService learning) =>
        {
            var actor = learning.ActorFor(me.Identity?.Name);
            if (actor.UserId is not int uid || !learning.Withdraw(id, uid))
                return Results.NotFound(new { detail = "Rating not found." });
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(MonitorCap);

        // ---- lessons -------------------------------------------------------------
        app.MapGet("/api/ai/lessons", async (string? status, string? kind, string? q, string? entity,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var baseQ = ShellScope.ScopeLibrary(db.AiLessons.AsNoTracking(), db, sc, me, entityId);
            if (!string.IsNullOrWhiteSpace(kind) && kind is not ("all" or "__all__"))
                baseQ = baseQ.Where(l => l.Kind == kind);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = ShellScope.Like(q.Trim());
                baseQ = baseQ.Where(l => EF.Functions.Like(l.Question, like, "\\") || EF.Functions.Like(l.Answer, like, "\\")
                                         || EF.Functions.Like(l.Avoid, like, "\\") || EF.Functions.Like(l.Note, like, "\\"));
            }
            var counts = await baseQ.GroupBy(l => l.Status).Select(g => new { status = g.Key, n = g.Count() }).ToListAsync();
            var statuses = (status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s != "all").ToList();
            var filtered = statuses.Count > 0 ? baseQ.Where(l => statuses.Contains(l.Status)) : baseQ;
            var rows = await filtered.OrderByDescending(l => l.UpdatedAt).ThenByDescending(l => l.Id).Take(500).ToListAsync();
            // the review queue: the most-asked suggestions first
            rows = rows.OrderByDescending(l => l.Status == AiLessonStatus.Suggested ? l.SourceCount : 0)
                .ThenByDescending(l => l.UpdatedAt).ThenByDescending(l => l.Id).ToList();
            return Results.Ok(new
            {
                lessons = await LessonPayloads(db, rows),
                counts = AiLessonStatus.All.ToDictionary(s => s, s => counts.FirstOrDefault(c => c.status == s)?.n ?? 0),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(MonitorCap);

        app.MapPost("/api/ai/lessons", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, LearningService learning) =>
        {
            var body = await ReadJson(ctx);
            if (body is not JsonElement b || b.ValueKind != JsonValueKind.Object)
                return Results.Json(new { detail = "Send {kind, question, answer}." }, statusCode: 400);
            var kind = (Str(b, "kind") ?? AiLessonKind.Rule).Trim().ToLowerInvariant();
            if (!AiLessonKind.All.Contains(kind))
                return Results.Json(new { detail = $"kind must be one of {string.Join(", ", AiLessonKind.All)}." }, statusCode: 400);
            var question = (Str(b, "question") ?? "").Trim();
            var answer = (Str(b, "answer") ?? Str(b, "text") ?? "").Trim();
            var avoid = (Str(b, "avoid") ?? "").Trim();
            if (answer.Length == 0)
                return Results.Json(new { detail = kind == AiLessonKind.Rule ? "Write the rule." : "answer is required." }, statusCode: 400);
            if (kind != AiLessonKind.Rule && question.Length == 0)
                return Results.Json(new { detail = "question is required." }, statusCode: 400);
            if (answer.Length > 2000 || question.Length > 1000 || avoid.Length > 2000)
                return Results.Json(new { detail = "Keep the question under 1000 and the answer under 2000 characters." }, statusCode: 400);
            // hand-written lessons go live by default; "suggested" parks one in the review queue
            var status = (Str(b, "status") ?? AiLessonStatus.Active).Trim().ToLowerInvariant();
            if (status is not (AiLessonStatus.Active or AiLessonStatus.Suggested))
                return Results.Json(new { detail = "status must be active or suggested." }, statusCode: 400);
            var entityId = await InboxEndpoints.EntityId(db, Str(b, "entity"));
            if (!CanWriteEntity(db, sc, me, entityId))
                return Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403);
            var actor = learning.ActorFor(me.Identity?.Name);
            var now = DateTime.UtcNow;
            var lesson = new AiLesson
            {
                EntityId = entityId, Kind = kind, Status = status,
                Question = kind == AiLessonKind.Rule ? "" : question, Answer = answer,
                Avoid = kind == AiLessonKind.Correction ? avoid : "",
                Note = (Str(b, "note") ?? "").Trim(), Channels = StrList(b, "channels"),
                SourceCount = 0, CreatedBy = actor.Username,
                ApprovedBy = status == AiLessonStatus.Active ? actor.Username : "",
                CreatedAt = now, UpdatedAt = now,
            };
            db.AiLessons.Add(lesson);
            await db.SaveChangesAsync();
            AuditTrail.Write(db, "ai.lesson.created", $"lesson:{lesson.Id}",
                $"{kind}: {TextAnalysis.Clip(kind == AiLessonKind.Rule ? answer : question, 200)}",
                actor: actor.Username, entityId: entityId, userId: actor.UserId);
            EmbeddingWorker.Kick();
            return Results.Ok(new { lesson = (await LessonPayloads(db, new List<AiLesson> { lesson }))[0] });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(BehaviourCap);

        app.MapMethods("/api/ai/lessons/{id:int}", new[] { "PATCH" }, async (int id, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, LearningService learning) =>
        {
            var lesson = await VisibleLesson(db, sc, me, id, tracked: true);
            if (lesson == null) return Results.NotFound(new { detail = "Lesson not found." });
            if (!CanWriteEntity(db, sc, me, lesson.EntityId))
                return Results.Json(new { detail = "That lesson is outside your scope." }, statusCode: 403);
            var body = await ReadJson(ctx);
            if (body is not JsonElement b || b.ValueKind != JsonValueKind.Object)
                return Results.Json(new { detail = "Send the fields to change." }, statusCode: 400);
            var actor = learning.ActorFor(me.Identity?.Name);

            var edits = new List<string>();
            void Set(string field, string? value, Func<string> get, Action<string> set)
            {
                if (value == null) return;
                var v = value.Trim();
                if (v == get()) return;
                set(v);
                edits.Add(field);
            }
            if (Str(b, "kind") is string k)
            {
                k = k.Trim().ToLowerInvariant();
                if (!AiLessonKind.All.Contains(k))
                    return Results.Json(new { detail = $"kind must be one of {string.Join(", ", AiLessonKind.All)}." }, statusCode: 400);
                Set("kind", k, () => lesson.Kind, v => lesson.Kind = v);
            }
            Set("question", Str(b, "question"), () => lesson.Question, v => lesson.Question = v);
            Set("answer", Str(b, "answer") ?? Str(b, "text"), () => lesson.Answer, v => lesson.Answer = v);
            Set("avoid", Str(b, "avoid"), () => lesson.Avoid, v => lesson.Avoid = v);
            Set("note", Str(b, "note"), () => lesson.Note, v => lesson.Note = v);
            if (b.TryGetProperty("channels", out _))
            {
                var channels = StrList(b, "channels");
                if (!channels.SequenceEqual(lesson.Channels)) { lesson.Channels = channels; edits.Add("channels"); }
            }
            if (lesson.Answer.Trim().Length == 0)
                return Results.Json(new { detail = "The answer can't be empty." }, statusCode: 400);
            if (lesson.Kind != AiLessonKind.Rule && lesson.Question.Trim().Length == 0)
                return Results.Json(new { detail = "The question can't be empty." }, statusCode: 400);
            if (lesson.Answer.Length > 2000 || lesson.Question.Length > 1000 || lesson.Avoid.Length > 2000)
                return Results.Json(new { detail = "Keep the question under 1000 and the answer under 2000 characters." }, statusCode: 400);
            if (edits.Count > 0)
            {
                lesson.UpdatedAt = DateTime.UtcNow;
                AuditTrail.Add(db, "ai.lesson.edited", $"lesson:{lesson.Id}", string.Join(", ", edits),
                    actor: actor.Username, entityId: lesson.EntityId, userId: actor.UserId);
            }
            if (Str(b, "status") is string status && status.Trim().Length > 0)
            {
                var problem = learning.Move(lesson, status, actor);
                if (problem != null) return Results.Json(new { detail = problem }, statusCode: 409);
            }
            await db.SaveChangesAsync();
            EmbeddingWorker.Kick();
            return Results.Ok(new { lesson = (await LessonPayloads(db, new List<AiLesson> { lesson }))[0] });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(BehaviourCap);

        app.MapDelete("/api/ai/lessons/{id:int}", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, LearningService learning) =>
        {
            var lesson = await VisibleLesson(db, sc, me, id, tracked: true);
            if (lesson == null) return Results.NotFound(new { detail = "Lesson not found." });
            var actor = learning.ActorFor(me.Identity?.Name);
            await db.AiFeedback.Where(f => f.LessonId == id).ExecuteUpdateAsync(u => u.SetProperty(f => f.LessonId, (int?)null));
            await db.AiEmbeddings.Where(e => e.OwnerType == EmbeddingOwner.Lesson && e.OwnerId == id).ExecuteDeleteAsync();
            db.AiLessons.Remove(lesson);
            AuditTrail.Add(db, "ai.lesson.deleted", $"lesson:{id}",
                TextAnalysis.Clip(lesson.Question.Length > 0 ? lesson.Question : lesson.Answer, 200),
                actor: actor.Username, entityId: lesson.EntityId, userId: actor.UserId);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus).RequireCapability(BehaviourCap);

        app.MapPost("/api/ai/lessons/{id:int}/to-knowledge", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, LearningService learning) =>
        {
            var lesson = await VisibleLesson(db, sc, me, id, tracked: true);
            if (lesson == null) return Results.NotFound(new { detail = "Lesson not found." });
            if (!CanWriteEntity(db, sc, me, lesson.EntityId))
                return Results.Json(new { detail = "That lesson is outside your scope." }, statusCode: 403);
            if (lesson.Answer.Trim().Length == 0)
                return Results.Json(new { detail = "The lesson has no answer to keep." }, statusCode: 400);
            var item = learning.ToKnowledge(lesson, learning.ActorFor(me.Identity?.Name));
            EmbeddingWorker.Kick();
            return Results.Ok(new { knowledge_id = item.Id, lesson_status = lesson.Status });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(BehaviourCap);

        // ---- retrieval index + learning mode -----------------------------------------
        app.MapGet("/api/ai/learning/status", async (string? entity, AppDbContext db, LearningService learning,
            EmbeddingIndexer indexer) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var s = indexer.Status();
            return Results.Ok(new
            {
                mode = learning.Mode(entityId),
                embedding = new
                {
                    provider = s.Provider, model = s.Model, configured = s.Configured,
                    indexed = s.Indexed, pending = s.Pending, last_error = s.LastError,
                    requested = s.Requested, chunks = s.Chunks, worker = EmbeddingWorker.Enabled,
                },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(MonitorCap);

        app.MapPost("/api/ai/learning/reindex", (AppDbContext db, ClaimsPrincipal me, EmbeddingIndexer indexer,
            LearningService learning) =>
        {
            var queued = indexer.Reset();
            var actor = learning.ActorFor(me.Identity?.Name);
            AuditTrail.Write(db, "ai.learning.reindex", "embeddings", $"{queued} chunks queued",
                actor: actor.Username, userId: actor.UserId);
            EmbeddingWorker.Kick();
            return Results.Ok(new { ok = true, queued });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(BehaviourCap);

        app.MapGet("/api/ai/learning/stats", async (int? days, string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, LearningService learning) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var scope = ShellScope.ThreadEntityIds(db, sc, me, entityId);
            return Results.Ok(learning.Stats(scope, days ?? 30));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(MonitorCap);
    }

    // ---- payloads -----------------------------------------------------------------------

    internal static async Task<List<object>> LessonPayloads(AppDbContext db, List<AiLesson> rows)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var entityIds = rows.Where(r => r.EntityId != null).Select(r => r.EntityId!.Value).Distinct().ToList();
        var entities = await db.Entities.AsNoTracking().Where(e => entityIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Slug, e.Name }).ToListAsync();
        var feedback = await db.AiFeedback.AsNoTracking()
            .Where(f => f.LessonId != null && ids.Contains(f.LessonId.Value))
            .OrderByDescending(f => f.Id).Take(Math.Max(50, ids.Count * 10)).ToListAsync();
        var userIds = feedback.Where(f => f.UserId != null).Select(f => f.UserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username }).ToDictionaryAsync(u => u.Id, u => u.Username);
        var threadIds = feedback.Where(f => f.ThreadId != null).Select(f => f.ThreadId!.Value).Distinct().ToList();
        var threads = await db.Threads.AsNoTracking().Where(t => threadIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Channel, t.Key }).ToDictionaryAsync(t => t.Id, t => $"{t.Channel}:{t.Key}");
        return rows.Select(l =>
        {
            var ent = entities.FirstOrDefault(e => e.Id == l.EntityId);
            return (object)new
            {
                id = l.Id, kind = l.Kind, status = l.Status, question = l.Question, answer = l.Answer,
                avoid = l.Avoid, note = l.Note, channels = l.Channels,
                entity = ent?.Slug, entity_name = ent?.Name ?? "",
                source_count = l.SourceCount, uses = l.Uses, last_used_at = l.LastUsedAt,
                created_by = l.CreatedBy, approved_by = l.ApprovedBy,
                created_at = l.CreatedAt, updated_at = l.UpdatedAt,
                feedback = feedback.Where(f => f.LessonId == l.Id).Take(5).Select(f => new
                {
                    id = f.Id, rating = f.Rating, kind = f.Kind,
                    user = f.UserId is int uid && users.TryGetValue(uid, out var name) ? name : "system",
                    created_at = f.CreatedAt,
                    thread = f.ThreadId is int tid && threads.TryGetValue(tid, out var key) ? key : null,
                    reasons = f.Reasons, note = f.Note, correction = f.Correction,
                }).ToList(),
            };
        }).ToList();
    }

    // ---- scope helpers ------------------------------------------------------------------

    private static async Task<AiLesson?> VisibleLesson(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        int id, bool tracked)
    {
        var q = ShellScope.ScopeLibrary(tracked ? db.AiLessons : db.AiLessons.AsNoTracking(), db, sc, me, null);
        return await q.FirstOrDefaultAsync(l => l.Id == id);
    }

    /// <summary>Admins write anywhere; an agent/custom role with assigned outlets only
    /// inside them (never an org-wide row); one with no assignment keeps the tenant view.</summary>
    private static bool CanWriteEntity(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? entityId)
    {
        var assigned = ShellScope.ScopeRoots(db, me);   // empty for superadmin + org-wide admins
        if (assigned.Count == 0) return true;
        return entityId is int eid && sc.EntitySubtreeIds(assigned).Contains(eid);
    }

    // ---- body readers ------------------------------------------------------------------

    private static async Task<JsonElement?> ReadJson(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.ContentLength is 0) return null;
            return await ctx.Request.ReadFromJsonAsync<JsonElement>();
        }
        catch { return null; }
    }

    private static string? Str(JsonElement b, string key) =>
        b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => v.ToString(),
            }
            : null;

    private static int? Int(JsonElement b, string key) =>
        b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => null,
            }
            : null;

    private static List<string> StrList(JsonElement b, string key)
    {
        if (!b.TryGetProperty(key, out var v)) return new List<string>();
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        if (v.ValueKind == JsonValueKind.String)
            return (v.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct().ToList();
        return new List<string>();
    }
}
