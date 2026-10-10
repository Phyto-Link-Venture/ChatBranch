using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Documents;

/// <summary>One priced line as printed: the server's numbers, never the model's.</summary>
public sealed record DocLine(string Description, decimal Qty, decimal UnitPrice, decimal Discount,
    string DiscountLabel, decimal Amount);

/// <summary>The money at the bottom of a document.</summary>
public sealed record DocTotals(decimal Subtotal, decimal Discount, decimal Tax, decimal Total);

/// <summary>
/// The arithmetic of a priced document, exact and in ONE place: the model sends
/// quantities and unit prices, the server computes every amount. Money is
/// <see cref="decimal"/> rounded to 2 places half away from zero (how a cashier
/// rounds 0.005); each line is rounded before it is added up, tax is computed
/// once on the subtotal.
/// <list type="bullet">
/// <item>line gross = qty × unit price (rounded)</item>
/// <item>line discount = an amount ("5", 5) or a percent of the gross ("10%"), never more than the gross</item>
/// <item>line amount = gross − discount</item>
/// <item>subtotal = Σ line amounts; tax = subtotal × rate % (rounded); total = subtotal + tax</item>
/// </list>
/// </summary>
public static class DocumentMath
{
    public const int MaxLines = 50;
    public const decimal MaxQty = 1_000_000m;
    public const decimal MaxUnitPrice = 100_000_000m;

    public static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Price the lines; <paramref name="error"/> names the first bad one (null = all fine).</summary>
    public static List<DocLine> Lines(JsonElement items, out string? error)
    {
        error = null;
        var lines = new List<DocLine>();
        if (items.ValueKind != JsonValueKind.Array) { error = "items must be a list of {description, qty, unit_price}."; return lines; }
        var n = 0;
        foreach (var it in items.EnumerateArray())
        {
            n++;
            if (n > MaxLines) { error = $"at most {MaxLines} items on one document."; return lines; }
            if (it.ValueKind != JsonValueKind.Object) { error = $"item {n} must be an object."; return lines; }
            var desc = (Str(it, "description") ?? Str(it, "name") ?? Str(it, "item") ?? "").Trim();
            if (desc.Length == 0) { error = $"item {n} needs a description."; return lines; }
            if (desc.Length > 300) desc = desc[..300];
            var qty = Num(it, "qty") ?? Num(it, "quantity") ?? 1m;
            if (qty <= 0 || qty > MaxQty) { error = $"item {n} ('{desc}'): qty must be more than 0."; return lines; }
            var price = Num(it, "unit_price") ?? Num(it, "price") ?? Num(it, "unitPrice");
            if (price is not decimal unit) { error = $"item {n} ('{desc}') needs a unit_price (a number)."; return lines; }
            if (unit < 0 || unit > MaxUnitPrice) { error = $"item {n} ('{desc}'): unit_price must be 0 or more."; return lines; }
            if (DecimalPlaces(unit) > 4 || DecimalPlaces(qty) > 4) { error = $"item {n} ('{desc}') has too many decimals."; return lines; }

            var gross = Round(qty * unit);
            var (discount, label, dErr) = Discount(it, gross);
            if (dErr != null) { error = $"item {n} ('{desc}'): {dErr}"; return lines; }
            lines.Add(new DocLine(desc, qty, unit, discount, label, gross - discount));
        }
        if (lines.Count == 0) error = "add at least one item.";
        return lines;
    }

    public static DocTotals Totals(IEnumerable<DocLine> lines, decimal taxRatePercent)
    {
        var list = lines.ToList();
        var subtotal = list.Sum(l => l.Amount);
        var discount = list.Sum(l => l.Discount);
        var tax = taxRatePercent > 0 ? Round(subtotal * taxRatePercent / 100m) : 0m;
        return new DocTotals(subtotal, discount, tax, subtotal + tax);
    }

    /// <summary>"1,234.50" — money as printed (invariant digits, 2 places).</summary>
    public static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>"2", "1.5" — a quantity without trailing zeros.</summary>
    public static string Qty(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static (decimal Amount, string Label, string? Error) Discount(JsonElement it, decimal gross)
    {
        if (!it.TryGetProperty("discount", out var d) || d.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return (0m, "", null);
        decimal amount;
        string label;
        if (d.ValueKind == JsonValueKind.String && Regex.Match((d.GetString() ?? "").Trim(), @"^(\d+(?:\.\d+)?)\s*%$")
                is { Success: true } m)
        {
            var pct = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (pct > 100) return (0, "", "a discount can't be more than 100%.");
            amount = Round(gross * pct / 100m);
            label = $"{Qty(pct)}%";
        }
        else
        {
            var v = d.ValueKind switch
            {
                JsonValueKind.Number when d.TryGetDecimal(out var x) => (decimal?)x,
                JsonValueKind.String when decimal.TryParse((d.GetString() ?? "").Trim(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var x) => x,
                _ => null,
            };
            if (v is not decimal val) return (0, "", "discount must be an amount (5) or a percent (\"10%\").");
            if (val < 0) return (0, "", "a discount can't be negative.");
            amount = Round(val);
            label = Money(amount);
        }
        if (amount > gross) return (0, "", "the discount is larger than the line.");
        return (amount, label, null);
    }

    private static int DecimalPlaces(decimal v) => (decimal.GetBits(v)[3] >> 16) & 0xFF;

    private static string? Str(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    internal static decimal? Num(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String)
        {
            // "RM 1,250.00" / "1250" — currency words and thousands separators dropped
            var s = Regex.Replace(v.GetString() ?? "", @"[^\d.\-]", "");
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p)) return p;
        }
        return null;
    }
}
