using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai.Learning;

/// <summary>Where semantic indexing stands (GET /api/ai/learning/status).</summary>
public sealed record IndexStatus(string Provider, string Model, bool Configured, string Requested,
    int Chunks, int Indexed, int Pending, string LastError);

/// <summary>
/// Keeps <c>crm_embedding</c> in step with the knowledge base and the lessons:
/// every chunk of an ACTIVE knowledge item and every active/suggested lesson
/// gets one vector; a row whose content hash or model no longer matches is
/// re-embedded, rows of deleted items/chunks are dropped. Run by the embedding
/// worker (never inline on a webhook or a reply) and by tests directly. After
/// lessons are embedded, near-duplicate suggestions are merged
/// (<see cref="LearningService.MergeNearDuplicates"/>).
/// </summary>
public class EmbeddingIndexer
{
    public const string LastErrorKey = "embedding_last_error";
    public const int BatchSize = 64;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly IEmbeddingClient _embeddings;

    public EmbeddingIndexer(AppDbContext db, ScopingService scoping, IEmbeddingClient? embeddings = null)
    {
        _db = db;
        _scoping = scoping;
        _embeddings = embeddings ?? NoEmbeddingClient.Instance;
    }

    private sealed record Wanted(string OwnerType, int OwnerId, int Index, string Text, string Hash);

    private List<Wanted> Desired()
    {
        var outv = new List<Wanted>();
        foreach (var k in _db.KnowledgeItems.AsNoTracking().Where(k => k.Active).ToList())
            foreach (var c in RetrievalService.ChunksFor(k))
                if (c.Text.Trim().Length > 0)
                    outv.Add(new Wanted(EmbeddingOwner.Knowledge, k.Id, c.Index, c.Text, c.Hash));
        foreach (var l in _db.AiLessons.AsNoTracking()
                     .Where(l => l.Status == AiLessonStatus.Active || l.Status == AiLessonStatus.Suggested).ToList())
        {
            var text = RetrievalService.LessonText(l);
            if (text.Length > 0) outv.Add(new Wanted(EmbeddingOwner.Lesson, l.Id, 0, text, TextAnalysis.Hash(text)));
        }
        return outv;
    }

    private EmbeddingConfig Config()
    {
        try { return _embeddings.Current(); }
        catch { return EmbeddingConfig.None; }
    }

    public IndexStatus Status()
    {
        var cfg = Config();
        var desired = Desired();
        var indexed = 0;
        if (cfg.Configured)
        {
            var rows = _db.AiEmbeddings.AsNoTracking().Where(e => e.Model == cfg.Model)
                .Select(e => new { e.OwnerType, e.OwnerId, e.ChunkIndex, e.ContentHash }).ToList()
                .Select(r => (r.OwnerType, r.OwnerId, r.ChunkIndex, r.ContentHash)).ToHashSet();
            indexed = desired.Count(w => rows.Contains((w.OwnerType, w.OwnerId, w.Index, w.Hash)));
        }
        return new IndexStatus(cfg.Provider, cfg.Model, cfg.Configured, cfg.Requested, desired.Count, indexed,
            cfg.Configured ? desired.Count - indexed : 0, _scoping.SettingValue(LastErrorKey));
    }

    /// <summary>
    /// One indexing pass: embed up to <paramref name="maxTexts"/> missing/stale
    /// chunks. Returns how many were embedded (0 = up to date, nothing
    /// configured, or the provider failed — see <see cref="IndexStatus.LastError"/>).
    /// </summary>
    public async Task<int> SyncAsync(int maxTexts = 256, CancellationToken ct = default)
    {
        var cfg = Config();
        if (!cfg.Configured) return 0;
        var desired = Desired();
        var want = desired.GroupBy(w => (w.OwnerType, w.OwnerId, w.Index)).ToDictionary(g => g.Key, g => g.First());
        var rows = _db.AiEmbeddings.AsNoTracking()
            .Select(e => new { e.Id, e.OwnerType, e.OwnerId, e.ChunkIndex, e.ContentHash, e.Model }).ToList();

        // chunks that no longer exist (item deleted / deactivated / shortened, lesson archived)
        var orphans = rows.Where(r => !want.ContainsKey((r.OwnerType, r.OwnerId, r.ChunkIndex))).Select(r => r.Id).ToList();
        foreach (var part in orphans.Chunk(500))
        {
            var ids = part.ToList();
            _db.AiEmbeddings.Where(e => ids.Contains(e.Id)).ExecuteDelete();
        }

        var current = rows.Where(r => want.TryGetValue((r.OwnerType, r.OwnerId, r.ChunkIndex), out var w)
                                      && w.Hash == r.ContentHash && r.Model == cfg.Model)
            .Select(r => (r.OwnerType, r.OwnerId, r.ChunkIndex)).ToHashSet();
        var todo = desired.Where(w => !current.Contains((w.OwnerType, w.OwnerId, w.Index)))
            .Take(Math.Max(1, maxTexts)).ToList();

        var embedded = 0;
        var lessonsTouched = false;
        try
        {
            foreach (var batch in todo.Chunk(BatchSize))
            {
                var result = await _embeddings.EmbedAsync(batch.Select(w => w.Text).ToList(), ct);
                RetrievalService.MeterEmbedding(_db, result, "index", batch.Length);
                var keys = batch.Select(w => w.OwnerId).Distinct().ToList();
                var existing = _db.AiEmbeddings.Where(e => keys.Contains(e.OwnerId)).ToList();
                var now = DateTime.UtcNow;
                for (var i = 0; i < batch.Length; i++)
                {
                    var w = batch[i];
                    var vec = result.Vectors[i];
                    var row = existing.FirstOrDefault(e => e.OwnerType == w.OwnerType && e.OwnerId == w.OwnerId
                                                           && e.ChunkIndex == w.Index);
                    if (row == null)
                    {
                        row = new AiEmbedding { OwnerType = w.OwnerType, OwnerId = w.OwnerId, ChunkIndex = w.Index };
                        _db.AiEmbeddings.Add(row);
                        existing.Add(row);
                    }
                    row.Text = w.Text.Length <= 4000 ? w.Text : w.Text[..4000];
                    row.ContentHash = w.Hash;
                    row.Model = cfg.Model;
                    row.Dims = vec.Length;
                    row.Vector = VectorMath.ToBlob(vec);
                    row.UpdatedAt = now;
                    VectorCache.Put(cfg.Model, w.Hash, vec);
                    if (w.OwnerType == EmbeddingOwner.Lesson) lessonsTouched = true;
                }
                _db.SaveChanges();
                embedded += batch.Length;
            }
            if (lessonsTouched) new LearningService(_db, _scoping).MergeNearDuplicates(LessonVectors(cfg.Model));
            SetLastError("");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            foreach (var e in _db.ChangeTracker.Entries<AiEmbedding>()
                         .Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
                e.State = EntityState.Detached;
            SetLastError(Short(ex.Message));
        }
        return embedded;
    }

    /// <summary>Vectors of the active/suggested lessons for the current model (dedup input).</summary>
    private Dictionary<int, float[]> LessonVectors(string model)
    {
        var live = _db.AiLessons.AsNoTracking()
            .Where(l => (l.Status == AiLessonStatus.Active || l.Status == AiLessonStatus.Suggested)
                        && l.Kind != AiLessonKind.Rule).ToList()
            .ToDictionary(l => l.Id, l => TextAnalysis.Hash(RetrievalService.LessonText(l)));
        var ids = live.Keys.ToList();
        var outv = new Dictionary<int, float[]>();
        foreach (var r in _db.AiEmbeddings.AsNoTracking()
                     .Where(e => e.OwnerType == EmbeddingOwner.Lesson && e.Model == model && ids.Contains(e.OwnerId))
                     .Select(e => new { e.OwnerId, e.ContentHash, e.Vector }).ToList())
            if (live.TryGetValue(r.OwnerId, out var hash) && hash == r.ContentHash)
                outv[r.OwnerId] = VectorCache.TryGet(model, hash, out var v) ? v : VectorMath.FromBlob(r.Vector);
        return outv;
    }

    /// <summary>Throw every vector away so the next passes re-embed everything.
    /// Returns how many chunks are now waiting.</summary>
    public int Reset()
    {
        _db.AiEmbeddings.ExecuteDelete();
        VectorCache.Clear();
        SetLastError("");
        return Desired().Count;
    }

    private void SetLastError(string message)
    {
        try
        {
            if (_scoping.SettingValue(LastErrorKey) == message) return;
            _scoping.SetSetting(LastErrorKey, message);
        }
        catch { /* status bookkeeping never breaks indexing */ }
    }

    private static string Short(string s) => s.Length <= 300 ? s : s[..300];
}
