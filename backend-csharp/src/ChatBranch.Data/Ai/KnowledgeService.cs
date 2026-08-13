using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Knowledge retrieval + context assembly (port of aicore/knowledge.py).
/// Retrieval is keyword + recency + priority — NO embeddings, the same
/// deliberate choice as memory. Tiers notice → event → fact (all live rows),
/// then the top-4 keyword-matched faq/policy. Everything is exception-safe: a
/// failure degrades to "answer without knowledge".
/// </summary>
public class KnowledgeService
{
    private const int Budget = 1800; // hard char cap
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;

    public KnowledgeService(AppDbContext db, ScopingService scoping)
    {
        _db = db;
        _scoping = scoping;
    }

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
    /// The bounded COMPANY KNOWLEDGE block the AI receives, or "" when nothing
    /// matches (contributes NOTHING rather than padding). <paramref name="query"/>
    /// is the incoming customer text used to keyword-rank faq/policy.
    /// </summary>
    public string BuildContext(string? query, int? entityId = null, string? channel = null)
    {
        try
        {
            var rows = LiveItems(entityId, channel);
            if (rows.Count == 0) return "";

            var notices = rows.Where(k => k.Kind == "notice").ToList();
            var events = rows.Where(k => k.Kind == "event").ToList();
            var facts = rows.Where(k => k.Kind == "fact").ToList();
            var qa = rows.Where(k => k.Kind is "faq" or "policy").ToList();

            var words = Tokenize(query);
            var topQa = qa
                .Select(k => (k, score: Score(k, words)))
                .OrderByDescending(x => x.score).ThenByDescending(x => x.k.Priority)
                .Take(4).Select(x => x.k).ToList();

            var picked = new List<KnowledgeItem>();
            picked.AddRange(notices);
            picked.AddRange(events);
            picked.AddRange(facts);
            picked.AddRange(topQa);

            var sb = new System.Text.StringBuilder("COMPANY KNOWLEDGE:\n");
            foreach (var k in picked)
            {
                var line = Render(k);
                if (sb.Length + line.Length > Budget)
                {
                    var room = Budget - sb.Length;
                    if (room > 40) sb.Append(line[..room]).Append("… (truncated)\n");
                    break;
                }
                sb.Append(line);
            }
            return sb.Length > "COMPANY KNOWLEDGE:\n".Length ? sb.ToString().TrimEnd() : "";
        }
        catch { return ""; }
    }

    private static string Render(KnowledgeItem k)
    {
        var head = k.Kind switch
        {
            "notice" => "⚠ NOTICE",
            "event" => "EVENT",
            "faq" => "FAQ",
            "policy" => "POLICY",
            _ => "FACT",
        };
        var q = string.IsNullOrEmpty(k.Question) ? "" : $" Q: {k.Question}";
        return $"- [{head}] {k.Title}{q} — {k.Content}\n";
    }

    private static int Score(KnowledgeItem k, HashSet<string> words)
    {
        if (words.Count == 0) return k.Priority;
        var hay = (k.Title + " " + k.Question + " " + k.Content + " " + string.Join(" ", k.Tags))
            .ToLowerInvariant();
        return words.Count(w => hay.Contains(w)) * 10 + k.Priority;
    }

    private static HashSet<string> Tokenize(string? s) =>
        (s ?? "").ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', ',', '.', '?', '!', ';', ':' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3).ToHashSet();
}
