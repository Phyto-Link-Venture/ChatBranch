using System.Text.Json;

namespace ChatBranch.Data.Ai;

/// <summary>
/// What one <c>crm_llmusage</c> row cost (USD) — the ONE pricing rule behind
/// Analytics → AI usage, the AI bill and the AI quotas, so a quota's spend is
/// exactly what the bill shows.
/// </summary>
public static class LlmPricing
{
    /// <summary>OpenRouter's public price list (written by the API's ModelPriceCatalog).</summary>
    public const string CatalogKey = "llm_prices_catalog";
    /// <summary>The operator's own table — wins over the catalog.</summary>
    public const string PricesKey = "llm_prices";

    // USD per 1M tokens: model -> (input, output). ":audio" rows price the audio
    // tokens of realtime speech models. Mirrors usage.PRICES_DEFAULT — a floor of
    // common defaults; operators maintain the live table in Setting `llm_prices`.
    private static readonly Dictionary<string, (double In, double Out)> PricesDefault = new()
    {
        ["gpt-4o-mini"] = (0.15, 0.60),
        ["openai/gpt-4o-mini"] = (0.15, 0.60),
        ["gpt-4o"] = (2.50, 10.00),
        ["gpt-4.1-mini"] = (0.40, 1.60),
        ["gpt-realtime"] = (4.00, 16.00),
        ["gpt-realtime:audio"] = (32.00, 64.00),
        ["gemini-2.5-flash"] = (0.30, 2.50),
    };

    /// <summary>
    /// Effective per-model prices: Setting <c>llm_prices</c> (JSON
    /// {"model": [in, out]}) over OpenRouter's public list over the shipped
    /// defaults. The keys are organization-wide; malformed entries are ignored.
    /// </summary>
    public static Dictionary<string, (double In, double Out)> PriceTable(ScopingService sc)
    {
        var merged = new Dictionary<string, (double In, double Out)>(PricesDefault);
        Merge(merged, sc.SettingValue(CatalogKey));   // OpenRouter's list
        Merge(merged, sc.SettingValue(PricesKey));    // the operator's own wins
        return merged;
    }

    /// <summary>The cost of one usage row: the provider's own price when it reported one,
    /// else tokens × the table (text + ":audio" rows). Unknown models cost 0.</summary>
    public static double RowCost(Dictionary<string, (double In, double Out)> prices, string? model,
        long prompt, long completion, long audioPrompt, long audioCompletion, double? reported)
    {
        if (reported is double r) return r;
        var cost = 0d;
        if (TryPrice(prices, model, out var p))
            cost += (prompt * p.In + completion * p.Out) / 1_000_000d;
        if (prices.TryGetValue((model ?? "") + ":audio", out var a))
            cost += (audioPrompt * a.In + audioCompletion * a.Out) / 1_000_000d;
        return cost;
    }

    /// <summary>
    /// A model's price: the exact id, else — for a model called straight at its
    /// vendor (Anthropic's <c>claude-haiku-5-5</c>, Qwen's <c>qwen-plus</c>) — the
    /// ONE OpenRouter row whose name after the vendor prefix matches it, dots and
    /// dashes alike (<c>anthropic/claude-haiku-5.5</c>). Ambiguous = unknown.
    /// </summary>
    public static bool TryPrice(Dictionary<string, (double In, double Out)> prices, string? model,
        out (double In, double Out) price)
    {
        price = default;
        if (string.IsNullOrEmpty(model)) return false;
        if (prices.TryGetValue(model, out price)) return true;
        if (model.Contains('/')) return false;
        var index = SuffixIndex.GetValue(prices, BuildSuffixIndex);
        if (index.TryGetValue(Norm(model), out var hit) && hit is { } found) { price = found; return true; }
        return false;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Dictionary<string, (double In, double Out)>, Dictionary<string, (double In, double Out)?>> SuffixIndex = new();

    private static string Norm(string model) => model.Trim().ToLowerInvariant().Replace('.', '-');

    private static Dictionary<string, (double In, double Out)?> BuildSuffixIndex(
        Dictionary<string, (double In, double Out)> prices)
    {
        var index = new Dictionary<string, (double In, double Out)?>();
        foreach (var (key, value) in prices)
        {
            var slash = key.IndexOf('/');
            if (slash < 0 || key.EndsWith(":audio", StringComparison.Ordinal)) continue;
            var suffix = Norm(key[(slash + 1)..]);
            // two vendors with the same model name → no guess (null marks it ambiguous)
            index[suffix] = index.ContainsKey(suffix) && index[suffix] != value ? null : value;
        }
        return index;
    }

    private static void Merge(Dictionary<string, (double In, double Out)> merged, string? raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Array || entry.Value.GetArrayLength() < 2)
                    continue;
                var pair = entry.Value.EnumerateArray().Take(2).ToList();
                if (pair[0].ValueKind != JsonValueKind.Number || pair[1].ValueKind != JsonValueKind.Number)
                    continue;
                merged[entry.Name] = (pair[0].GetDouble(), pair[1].GetDouble());
            }
        }
        catch (Exception) { /* degrade to the defaults — never fail the report */ }
    }
}
