using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatBranch.Data.Ai;

/// <summary>One company's overrides of the default billing terms.</summary>
public sealed class CompanyTerms
{
    [JsonPropertyName("markup_pct")] public double? MarkupPct { get; set; }
    [JsonPropertyName("monthly_fee")] public double? MonthlyFee { get; set; }
}

/// <summary>
/// The AI bill's terms as they applied from one calendar month on
/// (<see cref="EffectiveFrom"/>, <c>yyyy-MM</c>, UTC months like the bill).
/// </summary>
public class BillingTerms
{
    [JsonPropertyName("currency")] public string Currency { get; set; } = "USD";
    /// <summary>1 USD = this many units of <see cref="Currency"/>.</summary>
    [JsonPropertyName("usd_rate")] public double UsdRate { get; set; } = 1;
    [JsonPropertyName("default_markup_pct")] public double DefaultMarkupPct { get; set; }
    [JsonPropertyName("default_monthly_fee")] public double DefaultMonthlyFee { get; set; }
    /// <summary>Per company (top-level entity id as a string): overrides of the defaults.</summary>
    [JsonPropertyName("companies")] public Dictionary<string, CompanyTerms> Companies { get; set; } = new();
    /// <summary>The first month (yyyy-MM) these terms bill. Null = a legacy row: every earlier month.</summary>
    [JsonPropertyName("effective_from")] public string? EffectiveFrom { get; set; }

    public double MarkupFor(int? companyId) =>
        companyId is int id && Companies.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var c)
        && c.MarkupPct is double m ? m : DefaultMarkupPct;

    public double FeeFor(int companyId) =>
        Companies.TryGetValue(companyId.ToString(CultureInfo.InvariantCulture), out var c) && c.MonthlyFee is double f
            ? f : DefaultMonthlyFee;

    /// <summary>cost (USD) × (1 + markup %) × the exchange rate, unrounded.</summary>
    public double BilledRaw(int? companyId, double costUsd) =>
        costUsd * (1 + MarkupFor(companyId) / 100d) * UsdRate;

    /// <summary>A copy without the month stamp (for history and comparisons).</summary>
    public BillingTerms CopyTerms(string? effectiveFrom) => new()
    {
        Currency = Currency, UsdRate = UsdRate, DefaultMarkupPct = DefaultMarkupPct,
        DefaultMonthlyFee = DefaultMonthlyFee,
        Companies = Companies.ToDictionary(kv => kv.Key,
            kv => new CompanyTerms { MarkupPct = kv.Value.MarkupPct, MonthlyFee = kv.Value.MonthlyFee }),
        EffectiveFrom = effectiveFrom,
    };

    public bool SameTermsAs(BillingTerms other) =>
        JsonSerializer.Serialize(CopyTerms(null)) == JsonSerializer.Serialize(other.CopyTerms(null));
}

/// <summary>
/// The Setting <c>ai_billing</c>: the CURRENT terms (top level, backward
/// compatible with the pre-history shape) plus the terms that applied before
/// (<see cref="History"/>). A change applies to the current calendar month and
/// later; past months keep the terms that billed them. Months are UTC — the
/// same months the bill and the quotas use.
/// </summary>
public sealed class BillingSettings : BillingTerms
{
    [JsonPropertyName("history")] public List<BillingTerms> History { get; set; } = new();

    /// <summary>
    /// The terms that bill <paramref name="month"/> (yyyy-MM): the latest whose
    /// <c>effective_from</c> ≤ that month (null = every earlier month). A month
    /// older than every recorded set uses the oldest one.
    /// </summary>
    public BillingTerms TermsFor(string month)
    {
        var all = History.Append(this).ToList();
        var fitting = all.Where(t => t.EffectiveFrom == null || string.CompareOrdinal(t.EffectiveFrom, month) <= 0)
            .OrderBy(t => t.EffectiveFrom ?? "", StringComparer.Ordinal).LastOrDefault();
        return fitting ?? all.OrderBy(t => t.EffectiveFrom ?? "", StringComparer.Ordinal).First();
    }

    /// <summary>The current and past terms, newest first, with the month each ran until.</summary>
    public List<(BillingTerms Terms, string? Until)> Timeline()
    {
        var ordered = AiBilling.Collapse(History.Append(this).OrderBy(t => t.EffectiveFrom ?? "", StringComparer.Ordinal));
        var outv = new List<(BillingTerms, string?)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            string? until = null;
            if (i + 1 < ordered.Count && ordered[i + 1].EffectiveFrom is string next
                && DateTime.TryParseExact(next, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                until = d.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            outv.Add((ordered[i], until));
        }
        outv.Reverse();
        return outv;
    }
}

/// <summary>Reads the <c>ai_billing</c> Setting (see <see cref="BillingSettings"/>).</summary>
public static class AiBilling
{
    public const string SettingKey = "ai_billing";
    public const int MaxHistory = 120;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static BillingSettings Load(ScopingService sc) => Load(sc, out _);

    /// <summary><paramref name="stored"/> = a row exists (a first save has nothing to keep).</summary>
    public static BillingSettings Load(ScopingService sc, out bool stored)
    {
        stored = false;
        try
        {
            var raw = sc.SettingValue(SettingKey);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                stored = true;
                var s = JsonSerializer.Deserialize<BillingSettings>(raw, Json) ?? new BillingSettings();
                s.History ??= new();
                s.Companies ??= new();
                foreach (var h in s.History) h.Companies ??= new();
                return s;
            }
        }
        catch { /* a malformed row bills at the defaults rather than failing */ }
        return new BillingSettings();
    }

    /// <summary>The calendar month (UTC) a moment falls in, as yyyy-MM.</summary>
    public static string MonthKey(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static DateTime MonthStart(DateTime utc) => new(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The terms to store after a save: new terms replace this month's; terms
    /// that started in an earlier month move into the history and the new ones
    /// start this month. Saving unchanged terms changes nothing.
    /// </summary>
    public static BillingSettings Apply(BillingSettings stored, bool hadRow, BillingTerms incoming, DateTime nowUtc)
    {
        var month = MonthKey(nowUtc);
        var next = new BillingSettings
        {
            Currency = incoming.Currency, UsdRate = incoming.UsdRate,
            DefaultMarkupPct = incoming.DefaultMarkupPct, DefaultMonthlyFee = incoming.DefaultMonthlyFee,
            Companies = incoming.Companies, EffectiveFrom = month,
            History = stored.History.Select(h => h.CopyTerms(h.EffectiveFrom)).ToList(),
        };
        if (!hadRow) return next;
        var startedEarlier = stored.EffectiveFrom == null || string.CompareOrdinal(stored.EffectiveFrom, month) < 0;
        if (startedEarlier)
        {
            if (stored.SameTermsAs(incoming)) next.EffectiveFrom = stored.EffectiveFrom;   // nothing changed
            else next.History.Add(stored.CopyTerms(stored.EffectiveFrom));
        }
        next.History = Collapse(next.History.OrderBy(h => h.EffectiveFrom ?? "", StringComparer.Ordinal))
            .TakeLast(MaxHistory).ToList();
        // back to the terms that ran just before (changed, then changed back):
        // one period, not two identical ones
        if (next.History.Count > 0 && next.History[^1].SameTermsAs(next))
        {
            next.EffectiveFrom = next.History[^1].EffectiveFrom;
            next.History.RemoveAt(next.History.Count - 1);
        }
        return next;
    }

    /// <summary>Consecutive periods with identical terms are ONE period that
    /// started at the earlier month (oldest first in, oldest first out).</summary>
    public static List<BillingTerms> Collapse(IEnumerable<BillingTerms> ordered)
    {
        var outv = new List<BillingTerms>();
        foreach (var t in ordered)
            if (outv.Count == 0 || !outv[^1].SameTermsAs(t)) outv.Add(t);
        return outv;
    }
}
