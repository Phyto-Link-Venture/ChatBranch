using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Analytics;

/// <summary>
/// Reporting (port of the analytics parts of calls/views.py). The message
/// funnel, per-account volumes (including the null-account bucket), call
/// outcomes, and a zero-filled daily series; plus LLM usage/billing with costs
/// ESTIMATED at read time from a price table so edits apply retroactively.
/// </summary>
public class AnalyticsService
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;

    public AnalyticsService(AppDbContext db, ScopingService scoping)
    {
        _db = db;
        _scoping = scoping;
    }

    // USD per 1M tokens — usage.PRICES_DEFAULT stand-in (Setting llm_prices overrides).
    private static readonly Dictionary<string, (double pin, double pout)> PriceDefault = new()
    {
        ["sandbox"] = (0, 0),
        ["gpt-4o-mini"] = (0.15, 0.60),
        ["gpt-4o"] = (2.50, 10.00),
    };

    public object Summary(int days, int? entityId)
    {
        var since = DateTime.UtcNow.Date.AddDays(-Math.Max(1, days) + 1);

        var threads = _scoping.ScopeOwned(_db.Threads.AsNoTracking(), entityId, includeUnassigned: true);
        var threadIds = threads.Select(t => t.Id);
        var msgs = _db.Messages.AsNoTracking().Where(m => threadIds.Contains(m.ThreadId) && m.CreatedAt >= since);

        var inbound = msgs.Count(m => m.Direction == MessageDirection.In);
        var outbound = msgs.Count(m => m.Direction == MessageDirection.Out);
        // "replied" approximation: threads with both an inbound and a later outbound.
        var replied = threads.Count(t =>
            _db.Messages.Any(m => m.ThreadId == t.Id && m.Direction == MessageDirection.In) &&
            _db.Messages.Any(m => m.ThreadId == t.Id && m.Direction == MessageDirection.Out));

        var perAccount = msgs
            .GroupBy(m => m.AccountId)
            .Select(g => new { accountId = g.Key, count = g.Count() })
            .ToList();

        var callOutcomes = _db.CallRecords.AsNoTracking()
            .GroupBy(c => c.Outcome)
            .Select(g => new { outcome = g.Key, count = g.Count() })
            .ToList();

        // zero-filled daily series
        var byDay = msgs.Where(m => m.Direction == MessageDirection.Out)
            .GroupBy(m => m.CreatedAt.Date)
            .ToDictionary(g => g.Key, g => g.Count());
        var series = Enumerable.Range(0, Math.Max(1, days))
            .Select(i => since.AddDays(i))
            .Select(d => new { date = d.ToString("yyyy-MM-dd"), outbound = byDay.GetValueOrDefault(d, 0) })
            .ToList();

        return new
        {
            days,
            funnel = new { inbound, outbound, replied },
            perAccount,
            callOutcomes,
            series,
        };
    }

    public object Usage(int days, int? entityId)
    {
        var since = DateTime.UtcNow.Date.AddDays(-Math.Max(1, days) + 1);
        var prices = LoadPrices(entityId);

        var rows = _db.LlmUsage.AsNoTracking().Where(u => u.CreatedAt >= since).ToList();
        double Cost(LlmUsage u)
        {
            var (pin, pout) = prices.GetValueOrDefault(u.Model, (0, 0));
            return u.PromptTokens / 1_000_000.0 * pin + u.CompletionTokens / 1_000_000.0 * pout;
        }

        var byFeature = rows.GroupBy(u => u.Feature).Select(g => new
        {
            feature = g.Key,
            calls = g.Count(),
            promptTokens = g.Sum(u => u.PromptTokens),
            completionTokens = g.Sum(u => u.CompletionTokens),
            cost = Math.Round(g.Sum(Cost), 4),
        }).ToList();

        var byContact = rows.Where(u => u.ContactId != null)
            .GroupBy(u => u.ContactId)
            .Select(g => new { contactId = g.Key, cost = Math.Round(g.Sum(Cost), 4) })
            .OrderByDescending(x => x.cost).Take(50).ToList();

        return new
        {
            days,
            totalCost = Math.Round(rows.Sum(Cost), 4),
            byFeature,
            byContact,
        };
    }

    private Dictionary<string, (double, double)> LoadPrices(int? entityId)
    {
        try
        {
            var raw = _scoping.SettingValue("llm_prices", entityId);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parsed = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, double[]>>(raw);
                if (parsed != null)
                    return parsed.ToDictionary(kv => kv.Key,
                        kv => (kv.Value.ElementAtOrDefault(0), kv.Value.ElementAtOrDefault(1)));
            }
        }
        catch { }
        return PriceDefault;
    }
}
