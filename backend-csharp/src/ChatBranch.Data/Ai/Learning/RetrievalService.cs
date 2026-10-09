using System.Collections.Concurrent;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai.Learning;

/// <summary>
/// What retrieval searches FOR: the customer's latest message(s) — not the
/// whole thread — plus the message they were answering, as context. A plain
/// string converts implicitly (no context).
/// </summary>
public sealed record RetrievalQuery(string Text, string Context = "")
{
    public static implicit operator RetrievalQuery(string? text) => new(text ?? "");

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(Context);

    /// <summary>The text semantic search embeds: a short customer turn ("yes, how
    /// much?") leans on the message it answers; a full question stands alone.</summary>
    public string EmbedText
    {
        get
        {
            var t = (Text ?? "").Trim();
            if (t.Length >= 40 || string.IsNullOrWhiteSpace(Context)) return t.Length > 0 ? t : (Context ?? "").Trim();
            return (Context!.Trim() + "\n" + t).Trim();
        }
    }

    public override string ToString() => Text;

    /// <summary>
    /// From a thread's recent (non-private, oldest-first) messages: the latest
    /// run of customer messages, and the message before that run as context.
    /// Trailing outbound messages (a follow-up's case) are skipped over.
    /// </summary>
    public static RetrievalQuery FromHistory(IReadOnlyList<Message> history)
    {
        var i = history.Count - 1;
        while (i >= 0 && history[i].Direction != MessageDirection.In) i--;
        if (i < 0) return new RetrievalQuery("");
        var texts = new List<string>();
        while (i >= 0 && history[i].Direction == MessageDirection.In)
        {
            if (!string.IsNullOrWhiteSpace(history[i].Text)) texts.Insert(0, history[i].Text.Trim());
            i--;
        }
        var text = string.Join("\n", texts);
        if (text.Length > 1000) text = text[^1000..];
        var context = i >= 0 ? history[i].Text ?? "" : "";
        if (context.Length > 500) context = context[..500];
        return new RetrievalQuery(text, context);
    }
}

/// <summary>One searchable piece of a knowledge item. <see cref="Text"/> (title
/// prefixed) is what is indexed and embedded; <see cref="Body"/> is the piece itself.</summary>
public sealed record KnowledgeChunk(int ItemId, int Index, int Count, string Body, string Text, string Hash);

/// <summary>A scored candidate: a knowledge chunk or a lesson.</summary>
public sealed class RetrievalHit
{
    public string Type { get; init; } = "knowledge";
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string Chunk { get; init; } = "";
    public int ChunkIndex { get; init; }
    public int ChunkCount { get; init; } = 1;
    /// <summary>Final relevance: 0.7·semantic + 0.3·keyword with a vector, else keyword.</summary>
    public double Score { get; init; }
    /// <summary>Cosine to the query (0 without a vector).</summary>
    public double Semantic { get; init; }
    /// <summary>BM25, normalised to the best candidate (0–1).</summary>
    public double Keyword { get; init; }
    /// <summary>Share of the customer's content words the lesson's question has (lessons only).</summary>
    public double Coverage { get; init; }
    public bool HasVector { get; init; }
    public KnowledgeItem? Item { get; init; }
    public AiLesson? Lesson { get; init; }
}

public sealed class RetrievalResult
{
    public static readonly RetrievalResult Empty = new();
    /// <summary>Every knowledge chunk with any relevance, best first.</summary>
    public List<RetrievalHit> Knowledge { get; init; } = new();
    /// <summary>Lessons above the relevance threshold, best first.</summary>
    public List<RetrievalHit> Lessons { get; init; } = new();
    /// <summary>The query was embedded and vectors were compared.</summary>
    public bool Semantic { get; init; }
    public string Provider { get; init; } = "none";
    public string Model { get; init; } = "";
    /// <summary>Why semantic search was skipped this time ("" = it ran or wasn't configured).</summary>
    public string Error { get; init; } = "";
}

/// <summary>
/// Retrieval (RAG) over the knowledge base and the learned lessons — ONE
/// service every generation uses (chat replies, follow-ups, AI flow messages,
/// the voice dispatch's knowledge block and the preview).
///
/// Keyword path (always): BM25 over the chunks with English + Malay stopwords
/// and light stemming (<see cref="TextAnalysis"/>). Semantic path (when an
/// embedding provider is configured): the query is embedded once and compared
/// by brute-force cosine with the stored chunk vectors (crm_embedding, written by
/// the embedding worker — never inline), the final score being
/// 0.7·semantic + 0.3·keyword. A provider failure falls back to keyword only.
/// Never throws.
/// </summary>
public class RetrievalService
{
    public const double SemanticWeight = 0.7;
    public const double KeywordWeight = 0.3;
    /// <summary>Below this cosine (and with no keyword overlap) a chunk is noise.</summary>
    public const double KnowledgeSemanticFloor = 0.30;
    /// <summary>A lesson must be a near-asking of the same question to apply.</summary>
    public const double LessonSemanticFloor = 0.60;
    public const double LessonCoverageFloor = 0.5;
    public static readonly TimeSpan QueryEmbedTimeout = TimeSpan.FromSeconds(8);

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly IEmbeddingClient _embeddings;

    public RetrievalService(AppDbContext db, ScopingService scoping, IEmbeddingClient? embeddings = null)
    {
        _db = db;
        _scoping = scoping;
        _embeddings = embeddings ?? NoEmbeddingClient.Instance;
    }

    public IEmbeddingClient Embeddings => _embeddings;

    // ---- chunks ---------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, List<KnowledgeChunk>> ChunkCache = new();
    private static readonly ConcurrentDictionary<string, string[]> TermCache = new();

    /// <summary>A knowledge item as searchable chunks (one for a short item).</summary>
    public static List<KnowledgeChunk> ChunksFor(KnowledgeItem k)
    {
        var key = $"{k.Id}|{TextAnalysis.Hash(k.Title + "\u001f" + k.Question + "\u001f" + k.Content)}";
        if (ChunkCache.TryGetValue(key, out var cached)) return cached;
        var head = (k.Title ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(k.Question)) head = $"{head} — Q: {k.Question.Trim()}".Trim(' ', '—');
        var bodies = TextAnalysis.Chunk(k.Content);
        if (bodies.Count == 0) bodies.Add("");
        var chunks = bodies.Select((body, i) =>
        {
            var text = head.Length == 0 ? body : body.Length == 0 ? head : $"{head}: {body}";
            return new KnowledgeChunk(k.Id, i, bodies.Count, body, text, TextAnalysis.Hash(text));
        }).ToList();
        if (ChunkCache.Count > 20_000) ChunkCache.Clear();
        ChunkCache[key] = chunks;
        return chunks;
    }

    /// <summary>What a lesson is matched on: its question (a rule: its text).</summary>
    public static string LessonText(AiLesson l) =>
        (l.Kind == AiLessonKind.Rule || string.IsNullOrWhiteSpace(l.Question) ? l.Answer : l.Question).Trim();

    private static string[] TermsCached(string text)
    {
        var key = TextAnalysis.Hash(text);
        if (TermCache.TryGetValue(key, out var t)) return t;
        var terms = TextAnalysis.Terms(text).ToArray();
        if (TermCache.Count > 100_000) TermCache.Clear();
        TermCache[key] = terms;
        return terms;
    }

    // ---- search ---------------------------------------------------------------

    public RetrievalResult Search(RetrievalQuery query, IReadOnlyList<KnowledgeItem> items,
        IReadOnlyList<AiLesson> lessons) =>
        SearchAsync(query, items, lessons).GetAwaiter().GetResult();

    public async Task<RetrievalResult> SearchAsync(RetrievalQuery query, IReadOnlyList<KnowledgeItem> items,
        IReadOnlyList<AiLesson> lessons, CancellationToken ct = default)
    {
        try
        {
            if (query.IsEmpty || (items.Count == 0 && lessons.Count == 0)) return RetrievalResult.Empty;

            var weights = QueryWeights(query);
            var textTerms = TextAnalysis.Terms(query.Text).Distinct().ToList();

            // the query vector (one embedding call), or null → keyword only
            var cfg = SafeConfig();
            float[]? qvec = null;
            var error = "";
            if (cfg.Configured)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(QueryEmbedTimeout);
                    var batch = await _embeddings.EmbedAsync(new[] { query.EmbedText }, cts.Token);
                    qvec = batch.Vectors.Count > 0 ? batch.Vectors[0] : null;
                    Meter(batch, "query", 1);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { error = Short(ex.Message); }
            }

            // ---- knowledge chunks
            var chunks = items.SelectMany(k => ChunksFor(k).Select(c => (Item: k, Chunk: c))).ToList();
            var kHits = new List<RetrievalHit>();
            if (chunks.Count > 0)
            {
                var docs = chunks.Select(c => (IReadOnlyList<string>)TermsCached(
                    c.Chunk.Text + " " + string.Join(" ", c.Item.Tags))).ToList();
                var bm = Bm25.Score(docs, weights);
                var max = bm.DefaultIfEmpty(0).Max();
                var vectors = qvec == null ? new Dictionary<(int, int), float[]>()
                    : Vectors(EmbeddingOwner.Knowledge, chunks.Select(c => (c.Item.Id, c.Chunk.Index, c.Chunk.Hash)), cfg.Model);
                for (var i = 0; i < chunks.Count; i++)
                {
                    var (item, chunk) = chunks[i];
                    var kw = max > 0 ? bm[i] / max : 0;
                    var hasVec = vectors.TryGetValue((item.Id, chunk.Index), out var v);
                    var sem = hasVec ? Math.Max(0, VectorMath.Cosine(qvec!, v!)) : 0;
                    var relevant = bm[i] > 0 || (hasVec && sem >= KnowledgeSemanticFloor);
                    if (!relevant) continue;
                    kHits.Add(new RetrievalHit
                    {
                        Type = "knowledge", Id = item.Id, Title = item.Title, Chunk = chunk.Body,
                        ChunkIndex = chunk.Index, ChunkCount = chunk.Count,
                        Score = hasVec ? SemanticWeight * sem + KeywordWeight * kw : kw,
                        Semantic = sem, Keyword = kw, HasVector = hasVec, Item = item,
                    });
                }
            }

            // ---- lessons (examples + corrections; rules are not ranked — they always apply)
            var lHits = new List<RetrievalHit>();
            var ranked = lessons.Where(l => l.Kind != AiLessonKind.Rule && LessonText(l).Length > 0).ToList();
            if (ranked.Count > 0)
            {
                var docs = ranked.Select(l => (IReadOnlyList<string>)TermsCached(LessonText(l))).ToList();
                var bm = Bm25.Score(docs, weights);
                var max = bm.DefaultIfEmpty(0).Max();
                var vectors = qvec == null ? new Dictionary<(int, int), float[]>()
                    : Vectors(EmbeddingOwner.Lesson, ranked.Select(l => (l.Id, 0, TextAnalysis.Hash(LessonText(l)))), cfg.Model);
                for (var i = 0; i < ranked.Count; i++)
                {
                    var l = ranked[i];
                    var kw = max > 0 ? bm[i] / max : 0;
                    var hasVec = vectors.TryGetValue((l.Id, 0), out var v);
                    var sem = hasVec ? Math.Max(0, VectorMath.Cosine(qvec!, v!)) : 0;
                    var terms = docs[i].ToHashSet();
                    var coverage = textTerms.Count == 0 ? 0 : textTerms.Count(terms.Contains) / (double)textTerms.Count;
                    var relevant = (bm[i] > 0 && coverage >= LessonCoverageFloor) || (hasVec && sem >= LessonSemanticFloor);
                    if (!relevant) continue;
                    lHits.Add(new RetrievalHit
                    {
                        Type = "lesson", Id = l.Id, Title = LessonTitle(l), Chunk = l.Answer,
                        Score = hasVec ? SemanticWeight * sem + KeywordWeight * kw : kw,
                        Semantic = sem, Keyword = kw, Coverage = coverage, HasVector = hasVec, Lesson = l,
                    });
                }
            }

            return new RetrievalResult
            {
                Knowledge = kHits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Item?.Priority ?? 0).ToList(),
                Lessons = lHits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Lesson?.SourceCount ?? 0).ToList(),
                Semantic = qvec != null,
                Provider = cfg.Provider,
                Model = cfg.Model,
                Error = error,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return RetrievalResult.Empty; } // retrieval trouble = answer without it
    }

    public static string LessonTitle(AiLesson l) => l.Kind == AiLessonKind.Rule
        ? $"Rule #{l.Id}"
        : $"Lesson #{l.Id}: {TextAnalysis.Clip(l.Question, 80)}";

    /// <summary>Query terms: the customer's words count fully, the context's at 0.35.</summary>
    public static Dictionary<string, double> QueryWeights(RetrievalQuery q)
    {
        var w = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var t in TextAnalysis.Terms(q.Text).Distinct()) w[t] = 1.0;
        foreach (var t in TextAnalysis.Terms(q.Context).Distinct()) w.TryAdd(t, 0.35);
        return w;
    }

    private EmbeddingConfig SafeConfig()
    {
        try { return _embeddings.Current(); }
        catch { return EmbeddingConfig.None; }
    }

    /// <summary>
    /// The CURRENT vectors for the wanted chunks: rows whose model matches the
    /// configured one and whose content hash matches the chunk as it is now (a
    /// stale row is ignored until the worker re-embeds it). Decoded vectors come
    /// from the in-memory cache when present.
    /// </summary>
    private Dictionary<(int, int), float[]> Vectors(string ownerType,
        IEnumerable<(int OwnerId, int Index, string Hash)> wanted, string model)
    {
        var outv = new Dictionary<(int, int), float[]>();
        var want = wanted.GroupBy(w => (w.OwnerId, w.Index)).ToDictionary(g => g.Key, g => g.First().Hash);
        if (want.Count == 0 || model.Length == 0) return outv;
        var ids = want.Keys.Select(k => k.OwnerId).Distinct().ToList();
        var rows = _db.AiEmbeddings.AsNoTracking()
            .Where(e => e.OwnerType == ownerType && e.Model == model && ids.Contains(e.OwnerId))
            .Select(e => new { e.Id, e.OwnerId, e.ChunkIndex, e.ContentHash })
            .ToList();
        var missing = new List<int>();
        foreach (var r in rows)
        {
            if (!want.TryGetValue((r.OwnerId, r.ChunkIndex), out var hash) || hash != r.ContentHash) continue;
            if (VectorCache.TryGet(model, hash, out var v)) outv[(r.OwnerId, r.ChunkIndex)] = v;
            else missing.Add(r.Id);
        }
        if (missing.Count > 0)
        {
            var loaded = _db.AiEmbeddings.AsNoTracking().Where(e => missing.Contains(e.Id))
                .Select(e => new { e.OwnerId, e.ChunkIndex, e.ContentHash, e.Vector }).ToList();
            foreach (var r in loaded)
            {
                var v = VectorMath.FromBlob(r.Vector);
                if (v.Length == 0) continue;
                VectorCache.Put(model, r.ContentHash, v);
                outv[(r.OwnerId, r.ChunkIndex)] = v;
            }
        }
        return outv;
    }

    /// <summary>Embedding calls are metered like chat calls (feature <c>embedding</c>).</summary>
    public void Meter(EmbeddingBatch batch, string reference, int texts) =>
        MeterEmbedding(_db, batch, reference, texts);

    public static void MeterEmbedding(AppDbContext db, EmbeddingBatch batch, string reference, int texts)
    {
        try
        {
            db.LlmUsage.Add(new LlmUsage
            {
                Feature = "embedding", Provider = batch.Provider, Model = batch.Model, Ref = reference,
                PromptTokens = batch.PromptTokens,
                Detail = new Dictionary<string, object?> { ["texts"] = texts },
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* the ledger never breaks retrieval */ }
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200];
}

/// <summary>Okapi BM25 (k1 = 1.2, b = 0.75) with weighted query terms.</summary>
public static class Bm25
{
    public static double[] Score(IReadOnlyList<IReadOnlyList<string>> docs, IReadOnlyDictionary<string, double> query,
        double k1 = 1.2, double b = 0.75)
    {
        var scores = new double[docs.Count];
        if (docs.Count == 0 || query.Count == 0) return scores;
        var avgdl = Math.Max(1.0, docs.Average(d => (double)d.Count));
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        var tfs = new List<Dictionary<string, int>>(docs.Count);
        foreach (var d in docs)
        {
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in d)
                if (query.ContainsKey(t)) tf[t] = tf.GetValueOrDefault(t) + 1;
            foreach (var t in tf.Keys) df[t] = df.GetValueOrDefault(t) + 1;
            tfs.Add(tf);
        }
        var n = docs.Count;
        for (var i = 0; i < docs.Count; i++)
        {
            double s = 0;
            var dl = docs[i].Count;
            foreach (var (term, f) in tfs[i])
            {
                var idf = Math.Log(1 + (n - df[term] + 0.5) / (df[term] + 0.5));
                s += query[term] * idf * f * (k1 + 1) / (f + k1 * (1 - b + b * dl / avgdl));
            }
            scores[i] = s;
        }
        return scores;
    }
}
