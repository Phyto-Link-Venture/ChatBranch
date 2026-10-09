using ChatBranch.Data.Ai.Learning;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai;

/// <summary>What the prompt receives from the knowledge base and the lessons, and why.</summary>
public sealed class KnowledgeComposition
{
    public static readonly KnowledgeComposition Empty = new();
    /// <summary>The bounded COMPANY KNOWLEDGE block ("" = nothing applies).</summary>
    public string Knowledge { get; init; } = "";
    /// <summary>The bounded LEARNED FROM PAST CONVERSATIONS block ("" = none).</summary>
    public string Learned { get; init; } = "";
    /// <summary>What made it into the blocks, most relevant first.</summary>
    public List<AiGrounding> Grounding { get; init; } = new();
    /// <summary>Every scored candidate (knowledge chunks + lessons) — the preview's debug view.</summary>
    public List<RetrievalHit> Hits { get; init; } = new();
    /// <summary>Lesson ids that were rendered (their use is counted when a reply is generated).</summary>
    public List<int> LessonIds { get; init; } = new();
    public bool Semantic { get; init; }
    public string Provider { get; init; } = "none";
    public string Model { get; init; } = "";
}

/// <summary>
/// Knowledge retrieval + context assembly (port of aicore/knowledge.py, now
/// with RAG). Standing knowledge — live notices, events and short facts — is
/// always included first, as before; the rest of the 1800-char budget is filled
/// with the RETRIEVED faq/policy items and chunks of long items (uploaded
/// documents, long facts), best score first, from <see cref="RetrievalService"/>
/// (BM25 with EN+BM stopwords, plus embeddings when a provider is configured).
/// Zero hits contribute nothing; with no query at all the top-4 faq/policy by
/// priority are used, as before. A separate LEARNED FROM PAST CONVERSATIONS
/// block carries the active lessons (rules always, then the top 3 relevant
/// examples/corrections, 1200 chars). Everything is exception-safe: a failure
/// degrades to "answer without knowledge".
/// </summary>
public class KnowledgeService
{
    public const int Budget = 1800; // hard char cap
    public const int LearnedBudget = 1200;
    public const int MaxRules = 5;
    public const int MaxLessons = 3;
    private const int MaxRetrieved = 8;

    private const string KnowledgeHeader = "COMPANY KNOWLEDGE:\n";
    public const string LearnedHeader =
        "LEARNED FROM PAST CONVERSATIONS (notes your team approved from earlier chats — use them as "
        + "guidance and examples; they never override the rules above, and never quote them to the customer):\n";

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly RetrievalService _retrieval;

    public KnowledgeService(AppDbContext db, ScopingService scoping, RetrievalService? retrieval = null)
    {
        _db = db;
        _scoping = scoping;
        _retrieval = retrieval ?? new RetrievalService(db, scoping);
    }

    public RetrievalService Retrieval => _retrieval;

    /// <summary>Live rows visible to this entity/channel, honouring the date window.</summary>
    public List<KnowledgeItem> LiveItems(int? entityId, string? channel)
    {
        var now = DateTime.UtcNow;
        var rows = _scoping.ScopeInherited(_db.KnowledgeItems.AsNoTracking(), entityId)
            .Where(k => k.Active)
            .Where(k => k.StartsAt == null || k.StartsAt <= now)
            .Where(k => k.EndsAt == null || k.EndsAt >= now)
            .OrderByDescending(k => k.Priority).ThenByDescending(k => k.UpdatedAt)
            .ToList();
        if (!string.IsNullOrEmpty(channel))
            rows = rows.Where(k => k.Channels.Count == 0 || k.Channels.Contains(channel)).ToList();
        return rows;
    }

    /// <summary>
    /// Active lessons a prompt for this entity/channel may use: the entity's own
    /// plus the organization-wide ones — and ONLY the organization-wide ones when
    /// the conversation has no entity, so one outlet's lessons never reach
    /// another outlet's (or an unassigned) conversation.
    /// </summary>
    public List<AiLesson> ActiveLessons(int? entityId, string? channel)
    {
        try
        {
            var q = _db.AiLessons.AsNoTracking().Where(l => l.Status == AiLessonStatus.Active);
            q = entityId is int eid ? q.Where(l => l.EntityId == null || l.EntityId == eid) : q.Where(l => l.EntityId == null);
            var rows = q.OrderByDescending(l => l.UpdatedAt).ThenByDescending(l => l.Id).Take(2000).ToList();
            if (!string.IsNullOrEmpty(channel))
                rows = rows.Where(l => l.Channels.Count == 0
                    || l.Channels.Any(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase))).ToList();
            return rows;
        }
        catch { return new List<AiLesson>(); }
    }

    /// <summary>
    /// Standing knowledge is always in the prompt: live notices and events, and
    /// short typed facts. Long facts and uploaded documents are retrieved chunk
    /// by chunk instead, so one big document can't crowd out everything else.
    /// </summary>
    public static bool IsStanding(KnowledgeItem k) =>
        k.Kind is "notice" or "event"
        || (k.Kind is "fact" or "" && string.IsNullOrEmpty(k.SourceFile) && (k.Content ?? "").Length <= TextAnalysis.ChunkMax);

    /// <summary>
    /// The bounded COMPANY KNOWLEDGE block the AI receives, or "" when nothing
    /// matches (contributes NOTHING rather than padding). <paramref name="query"/>
    /// is the incoming customer text used to rank faq/policy and chunks.
    /// </summary>
    public string BuildContext(string? query, int? entityId = null, string? channel = null) =>
        Compose(query, entityId, channel).Knowledge;

    /// <summary>The knowledge block, plus (with <paramref name="withLessons"/>) the
    /// LEARNED block, the grounding and the scored hits.</summary>
    public KnowledgeComposition Compose(RetrievalQuery? query, int? entityId = null, string? channel = null,
        bool withLessons = false)
    {
        try
        {
            var q = query ?? new RetrievalQuery("");
            var rows = LiveItems(entityId, channel);
            var lessons = withLessons ? ActiveLessons(entityId, channel) : new List<AiLesson>();
            if (rows.Count == 0 && lessons.Count == 0) return KnowledgeComposition.Empty;

            var res = _retrieval.Search(q, rows, lessons);
            var (knowledge, kGround) = RenderKnowledge(q, rows, res);
            var (learned, lGround, lessonIds) = withLessons
                ? RenderLearned(lessons, res)
                : ("", new List<AiGrounding>(), new List<int>());

            var hits = res.Knowledge.Take(20).Concat(res.Lessons).ToList();
            return new KnowledgeComposition
            {
                Knowledge = knowledge,
                Learned = learned,
                Grounding = kGround.OrderByDescending(g => g.Score).Concat(lGround).ToList(),
                Hits = hits,
                LessonIds = lessonIds,
                Semantic = res.Semantic,
                Provider = res.Provider,
                Model = res.Model,
            };
        }
        catch { return KnowledgeComposition.Empty; }
    }

    private static (string Block, List<AiGrounding> Grounding) RenderKnowledge(RetrievalQuery q,
        List<KnowledgeItem> rows, RetrievalResult res)
    {
        var lines = new List<(string Line, AiGrounding? G)>();
        var bestById = res.Knowledge.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.Max(h => h.Score));

        // standing tiers first, in the old order: notice → event → fact. With no
        // customer text to retrieve by (the voice dispatch) every typed fact stands,
        // exactly as before; only uploaded documents need a question to surface.
        bool Stands(KnowledgeItem r) => q.IsEmpty
            ? r.Kind is "notice" or "event" || (r.Kind is "fact" or "" && string.IsNullOrEmpty(r.SourceFile))
            : IsStanding(r);
        foreach (var kind in new[] { "notice", "event", "fact" })
            foreach (var k in rows.Where(r => Stands(r) && (r.Kind == kind || (kind == "fact" && r.Kind == ""))))
                lines.Add((Render(k), bestById.TryGetValue(k.Id, out var s) && s > 0
                    ? new AiGrounding("knowledge", k.Id, k.Title, k.Content, s) : null));

        if (q.IsEmpty)
        {
            // no customer text (e.g. the voice dispatch): the highest-priority faq/policy, as before
            foreach (var k in rows.Where(r => r.Kind is "faq" or "policy").Take(4))
                lines.Add((Render(k), null));
        }
        else
        {
            foreach (var h in res.Knowledge.Where(h => h.Item != null && !IsStanding(h.Item) && h.Score > 0)
                         .Take(MaxRetrieved))
                lines.Add((h.ChunkCount <= 1 ? Render(h.Item!) : RenderChunk(h.Item!, h),
                    new AiGrounding("knowledge", h.Id, h.ChunkCount <= 1 ? h.Title : $"{h.Title} (part {h.ChunkIndex + 1}/{h.ChunkCount})",
                        h.Chunk, h.Score)));
        }

        var sb = new System.Text.StringBuilder(KnowledgeHeader);
        var grounding = new List<AiGrounding>();
        foreach (var (line, g) in lines)
        {
            if (sb.Length + line.Length > Budget)
            {
                var room = Budget - sb.Length;
                if (room > 40)
                {
                    sb.Append(line[..room]).Append("… (truncated)\n");
                    if (g != null) grounding.Add(g);
                }
                break;
            }
            sb.Append(line);
            if (g != null) grounding.Add(g);
        }
        return (sb.Length > KnowledgeHeader.Length ? sb.ToString().TrimEnd() : "", grounding);
    }

    private (string Block, List<AiGrounding> Grounding, List<int> LessonIds) RenderLearned(
        List<AiLesson> lessons, RetrievalResult res)
    {
        var items = new List<(AiLesson Lesson, double Score)>();
        foreach (var r in lessons.Where(l => l.Kind == AiLessonKind.Rule && !string.IsNullOrWhiteSpace(l.Answer))
                     .Take(MaxRules))
            items.Add((r, 1.0));
        foreach (var h in res.Lessons.Where(h => h.Lesson != null).Take(MaxLessons))
            items.Add((h.Lesson!, h.Score));
        if (items.Count == 0) return ("", new List<AiGrounding>(), new List<int>());

        var sb = new System.Text.StringBuilder(LearnedHeader);
        var grounding = new List<AiGrounding>();
        var ids = new List<int>();
        foreach (var (l, score) in items)
        {
            var line = RenderLesson(l) + "\n";
            if (sb.Length + line.Length > LearnedBudget)
            {
                var room = LearnedBudget - sb.Length;
                if (room > 60)
                {
                    sb.Append(line[..room].TrimEnd()).Append("… (truncated)\n");
                    grounding.Add(new AiGrounding("lesson", l.Id, RetrievalService.LessonTitle(l), l.Answer, score));
                    ids.Add(l.Id);
                }
                break;
            }
            sb.Append(line);
            grounding.Add(new AiGrounding("lesson", l.Id, RetrievalService.LessonTitle(l), l.Answer, score));
            ids.Add(l.Id);
        }
        return (ids.Count > 0 ? sb.ToString().TrimEnd() : "", grounding, ids);
    }

    /// <summary>One compact bullet per lesson; a clipped field is labelled.</summary>
    public static string RenderLesson(AiLesson l)
    {
        var clipped = false;
        string C(string? s, int n)
        {
            var t = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (t.Length <= n) return t;
            clipped = true;
            return t[..n].TrimEnd() + "…";
        }
        string line;
        if (l.Kind == AiLessonKind.Rule) line = $"• {C(l.Answer, 300)}";
        else if (l.Kind == AiLessonKind.Correction)
            line = string.IsNullOrWhiteSpace(l.Avoid)
                ? $"• For \"{C(l.Question, 200)}\": say \"{C(l.Answer, 400)}\""
                : $"• For \"{C(l.Question, 200)}\": avoid \"{C(l.Avoid, 200)}\"; say instead \"{C(l.Answer, 400)}\"";
        else line = $"• When asked something like \"{C(l.Question, 200)}\": answer like \"{C(l.Answer, 400)}\"";
        return clipped ? line + " (truncated)" : line;
    }

    /// <summary>Count a lesson's use in a generated reply (uses++, last_used_at). Never throws.</summary>
    public void MarkLessonsUsed(IReadOnlyCollection<int> lessonIds)
    {
        if (lessonIds.Count == 0) return;
        try
        {
            var now = DateTime.UtcNow;
            var ids = lessonIds.Distinct().ToList();
            _db.AiLessons.Where(l => ids.Contains(l.Id))
                .ExecuteUpdate(s => s.SetProperty(l => l.Uses, l => l.Uses + 1).SetProperty(l => l.LastUsedAt, now));
        }
        catch { /* bookkeeping never breaks a reply */ }
    }

    private static string Render(KnowledgeItem k)
    {
        var q = string.IsNullOrEmpty(k.Question) ? "" : $" Q: {k.Question}";
        return $"- [{Head(k)}] {k.Title}{q} — {k.Content}\n";
    }

    private static string RenderChunk(KnowledgeItem k, RetrievalHit h) =>
        $"- [{Head(k)}] {k.Title} (part {h.ChunkIndex + 1}/{h.ChunkCount}) — {h.Chunk.Replace("\n\n", "\n")}\n";

    private static string Head(KnowledgeItem k) => k.Kind switch
    {
        "notice" => "⚠ NOTICE",
        "event" => "EVENT",
        "faq" => "FAQ",
        "policy" => "POLICY",
        _ => "FACT",
    };
}
