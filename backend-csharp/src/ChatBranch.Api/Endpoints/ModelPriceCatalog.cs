using System.Globalization;
using System.Text.Json;
using ChatBranch.Data;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// OpenRouter's public model price list (<c>GET /api/v1/models</c>, no key needed) as the
/// fallback for usage rows that carry no provider-reported cost — calls made before
/// <c>LlmUsage.CostUsd</c> existed, or providers that never report one. Prices are
/// stored in Setting <c>llm_prices_catalog</c> ({"model": [in, out]}, USD per 1M tokens)
/// and refreshed at most once a day when a report or the bill is read; a failed fetch
/// keeps the last good list and waits an hour. <c>LLM_PRICE_SYNC=0</c> turns the fetch off
/// (the test host does). Sits UNDER the operator's own <c>llm_prices</c>.
/// </summary>
public static class ModelPriceCatalog
{
    public const string SettingKey = "llm_prices_catalog";
    public const string StampKey = "llm_prices_catalog_at";
    private const string Url = "https://openrouter.ai/api/v1/models";
    private static DateTime _lastFailure = DateTime.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureFreshAsync(IHttpClientFactory http, ScopingService sc)
    {
        if (Environment.GetEnvironmentVariable("LLM_PRICE_SYNC") == "0") return;
        if (!Stale(sc) || DateTime.UtcNow - _lastFailure < TimeSpan.FromHours(1)) return;
        if (!await Gate.WaitAsync(0)) return;   // someone else is fetching — use what is stored
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var client = http.CreateClient("llm");
            using var resp = await client.GetAsync(Url, cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
            var prices = Parse(json);
            if (prices.Count == 0) throw new InvalidOperationException("empty price list");
            sc.SetSetting(SettingKey, JsonSerializer.Serialize(prices));
            sc.SetSetting(StampKey, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            _lastFailure = DateTime.UtcNow;   // reports still answer from the stored list
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool Stale(ScopingService sc) =>
        !DateTime.TryParse(sc.SettingValue(StampKey), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var at) || DateTime.UtcNow - at > TimeSpan.FromHours(24);

    /// <summary>OpenRouter quotes USD per TOKEN as strings; the table is USD per 1M tokens.</summary>
    public static Dictionary<string, double[]> Parse(JsonElement json)
    {
        var outp = new Dictionary<string, double[]>(StringComparer.Ordinal);
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array) return outp;
        foreach (var m in data.EnumerateArray())
        {
            if (!m.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (!m.TryGetProperty("pricing", out var p) || p.ValueKind != JsonValueKind.Object) continue;
            if (!Num(p, "prompt", out var pin) || !Num(p, "completion", out var pout)) continue;
            if (pin < 0 || pout < 0) continue;   // "-1" = variable pricing (routers)
            outp[id.GetString()!] = new[] { Math.Round(pin * 1_000_000d, 6), Math.Round(pout * 1_000_000d, 6) };
        }
        return outp;
    }

    private static bool Num(JsonElement o, string key, out double v)
    {
        v = 0;
        if (!o.TryGetProperty(key, out var e)) return false;
        if (e.ValueKind == JsonValueKind.Number) { v = e.GetDouble(); return true; }
        return e.ValueKind == JsonValueKind.String
               && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
