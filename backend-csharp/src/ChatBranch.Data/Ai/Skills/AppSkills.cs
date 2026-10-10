using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Bookings;
using ChatBranch.Data.Documents;

namespace ChatBranch.Data.Ai.Skills;

/// <summary>
/// The "Connected app" skills (<c>AgentSkill.Kind = "app"</c>) — ready-made,
/// real capabilities an admin switches on without writing code. The app is named
/// by <c>config.app</c>:
/// <list type="bullet">
/// <item><c>calendar</c> — check free times, book / cancel (<see cref="CalendarAppConfig"/>).</item>
/// <item><c>documents</c> — PDF invoices, quotations, receipts and letters (<see cref="DocumentAppConfig"/>).</item>
/// <item><c>qr</c> — a QR code image (<see cref="QrAppConfig"/>).</item>
/// <item><c>payments</c> — a Stripe payment link (<see cref="PaymentAppConfig"/>).</item>
/// <item><c>sheets</c> — append a row to a Google Sheet (<see cref="SheetsAppConfig"/>).</item>
/// </list>
/// This class is the ONE dispatcher: the skills PUT validates and normalizes
/// through <see cref="Normalize(JsonElement, out string?)"/>, payloads and the
/// tool builder read through <see cref="Normalize(Dictionary{string, object?}?)"/>.
/// A config with no <c>app</c> key is a calendar (the first app, stored before
/// the key mattered).
/// </summary>
public static class AppSkills
{
    public const string Calendar = CalendarAppConfig.AppId;
    public const string Documents = DocumentAppConfig.AppId;
    public const string Qr = QrAppConfig.AppId;
    public const string Payments = PaymentAppConfig.AppId;
    public const string Sheets = SheetsAppConfig.AppId;

    public static readonly string[] All = { Calendar, Documents, Qr, Payments, Sheets };

    /// <summary>The app a stored config names ("" → calendar).</summary>
    public static string AppOf(Dictionary<string, object?>? cfg)
    {
        if (cfg == null || !cfg.TryGetValue("app", out var v) || v == null) return Calendar;
        var s = v is JsonElement el ? (el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString()) : v.ToString();
        s = (s ?? "").Trim().ToLowerInvariant();
        return s.Length == 0 ? Calendar : s;
    }

    public static string AppOf(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("app", out var a)
            || a.ValueKind != JsonValueKind.String) return Calendar;
        var s = (a.GetString() ?? "").Trim().ToLowerInvariant();
        return s.Length == 0 ? Calendar : s;
    }

    /// <summary>Validate a wire config. Returns the normalized config (defaults filled
    /// in); <paramref name="error"/> is the first problem (null = valid).</summary>
    public static Dictionary<string, object?> Normalize(JsonElement el, out string? error)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            error = "config must be a JSON object.";
            return new CalendarAppConfig().ToDictionary();
        }
        switch (AppOf(el))
        {
            case Calendar: return CalendarAppConfig.From(el, out error).ToDictionary();
            case Documents: return DocumentAppConfig.From(el, out error).ToDictionary();
            case Qr: return QrAppConfig.From(el, out error).ToDictionary();
            case Payments: return PaymentAppConfig.From(el, out error).ToDictionary();
            case Sheets: return SheetsAppConfig.From(el, out error).ToDictionary();
            default:
                error = $"unknown app '{AppOf(el)}' (use {string.Join(", ", All)}).";
                return new CalendarAppConfig().ToDictionary();
        }
    }

    /// <summary>Lenient read of a stored config: anything unusable takes its default.</summary>
    public static Dictionary<string, object?> Normalize(Dictionary<string, object?>? raw)
    {
        try
        {
            var el = JsonSerializer.SerializeToElement(raw ?? new Dictionary<string, object?>());
            var app = AppOf(el);
            if (!All.Contains(app)) return new Dictionary<string, object?>(raw ?? new()); // keep what we can't read
            return Normalize(el, out _);
        }
        catch { return new CalendarAppConfig().ToDictionary(); }
    }

    // ---- shared JSON readers (wire configs are snake_case JSON) ------------------

    internal static string? Str(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    internal static string Text(JsonElement el, string key, string dflt, int max, string label, List<string> errors)
    {
        var s = Str(el, key);
        if (s == null) return dflt;
        s = s.Trim();
        if (s.Length > max)
        {
            errors.Add($"{label} is longer than {max:N0} characters.");
            return s[..max];
        }
        return s;
    }

    internal static int Int(JsonElement el, string key, int dflt, int min, int max, string label, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)
            || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return dflt;
        int n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out n)) { }
        else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out n)) { }
        else { errors.Add($"{label} must be a whole number."); return dflt; }
        if (n < min || n > max) { errors.Add($"{label} must be between {min} and {max}."); return dflt; }
        return n;
    }

    internal static decimal Dec(JsonElement el, string key, decimal dflt, decimal min, decimal max, string label,
        List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)
            || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return dflt;
        decimal n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out n)) { }
        else if (v.ValueKind == JsonValueKind.String && decimal.TryParse((v.GetString() ?? "").Trim(),
                     NumberStyles.Number, CultureInfo.InvariantCulture, out n)) { }
        else { errors.Add($"{label} must be a number."); return dflt; }
        if (n < min || n > max) { errors.Add($"{label} must be between {min} and {max}."); return dflt; }
        return n;
    }

    internal static List<string> Actions(JsonElement el, string[] known, List<string> dflt, List<string> errors)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("actions", out var acts)
            || acts.ValueKind == JsonValueKind.Null) return dflt;
        if (acts.ValueKind != JsonValueKind.Array) { errors.Add("actions must be a list."); return dflt; }
        var list = new List<string>();
        foreach (var a in acts.EnumerateArray())
        {
            var name = (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.ToString()).Trim().ToLowerInvariant();
            if (!known.Contains(name)) errors.Add($"unknown action '{name}' (use {string.Join(", ", known)}).");
            else if (!list.Contains(name)) list.Add(name);
        }
        if (list.Count == 0) errors.Add("pick at least one action.");
        return known.Where(list.Contains).ToList();
    }

    /// <summary>A three-letter ISO currency code ("MYR").</summary>
    internal static string Currency(JsonElement el, string dflt, List<string> errors, string key = "currency")
    {
        var c = (Str(el, key) ?? dflt).Trim().ToUpperInvariant();
        if (c.Length == 0) return dflt;
        if (!Regex.IsMatch(c, "^[A-Z]{3}$")) { errors.Add($"{key} must be a 3-letter currency code like MYR."); return dflt; }
        return c;
    }
}

/// <summary>
/// <c>app: "qr"</c> — the AI turns a text or link into a QR code image that is
/// sent after its reply (same reserve → send-at-delivery path as documents).
/// </summary>
public sealed class QrAppConfig
{
    public const string AppId = "qr";
    public string App => AppId;
    /// <summary>The caption sent with the image ("" = none).</summary>
    public string Caption { get; init; } = "";
    /// <summary>Pixels per QR module (4–20).</summary>
    public int PixelsPerModule { get; init; } = 10;

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["app"] = AppId,
        ["caption"] = Caption,
        ["pixels_per_module"] = PixelsPerModule,
    };

    public static Dictionary<string, object?> Defaults() => new QrAppConfig().ToDictionary();

    public static QrAppConfig From(Dictionary<string, object?>? raw)
    {
        try { return From(JsonSerializer.SerializeToElement(raw ?? new()), out _); }
        catch { return new QrAppConfig(); }
    }

    public static QrAppConfig From(JsonElement el, out string? error)
    {
        var errors = new List<string>();
        var cfg = new QrAppConfig
        {
            Caption = AppSkills.Text(el, "caption", "", 300, "caption", errors),
            PixelsPerModule = AppSkills.Int(el, "pixels_per_module", 10, 4, 20, "pixels_per_module", errors),
        };
        error = errors.FirstOrDefault();
        return cfg;
    }
}

/// <summary>
/// <c>app: "payments"</c> — the AI creates a Stripe Checkout payment link for an
/// amount it states, through the organization's (or the outlet's) Stripe
/// connection at Integrations. No connection ⇒ the tool says so and nothing is
/// pretended.
/// </summary>
public sealed class PaymentAppConfig
{
    public const string AppId = "payments";
    public string App => AppId;
    public string Provider { get; init; } = "stripe";
    public string Currency { get; init; } = "MYR";
    /// <summary>The largest amount the AI may ask for (a guard against a wrong number).</summary>
    public decimal MaxAmount { get; init; } = 10000m;
    /// <summary>Where Stripe sends the customer after paying ("" = Stripe's default thanks page).</summary>
    public string SuccessUrl { get; init; } = "";

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["app"] = AppId,
        ["provider"] = Provider,
        ["currency"] = Currency,
        ["max_amount"] = MaxAmount,
        ["success_url"] = SuccessUrl,
    };

    public static Dictionary<string, object?> Defaults() => new PaymentAppConfig().ToDictionary();

    public static PaymentAppConfig From(Dictionary<string, object?>? raw)
    {
        try { return From(JsonSerializer.SerializeToElement(raw ?? new()), out _); }
        catch { return new PaymentAppConfig(); }
    }

    public static PaymentAppConfig From(JsonElement el, out string? error)
    {
        var errors = new List<string>();
        var provider = (AppSkills.Str(el, "provider") ?? "stripe").Trim().ToLowerInvariant();
        if (provider != "stripe") { errors.Add("provider must be \"stripe\"."); provider = "stripe"; }
        var success = AppSkills.Text(el, "success_url", "", 500, "success_url", errors);
        if (success.Length > 0 && !Regex.IsMatch(success, "^https://", RegexOptions.IgnoreCase))
        {
            errors.Add("success_url must start with https://.");
            success = "";
        }
        var cfg = new PaymentAppConfig
        {
            Provider = provider,
            Currency = AppSkills.Currency(el, "MYR", errors),
            MaxAmount = AppSkills.Dec(el, "max_amount", 10000m, 1m, 1_000_000m, "max_amount", errors),
            SuccessUrl = success,
        };
        error = errors.FirstOrDefault();
        return cfg;
    }
}

/// <summary>
/// <c>app: "sheets"</c> — the AI logs a row (a lead, an order…) to a Google
/// Sheet through the Google Sheets connection at Integrations. The row is
/// written when the reply is DELIVERED (a held or rejected reply logs nothing):
/// [time, contact name, phone, email, …the configured columns].
/// </summary>
public sealed class SheetsAppConfig
{
    public const string AppId = "sheets";
    public string App => AppId;
    public string SpreadsheetId { get; init; } = "";
    public string SheetRange { get; init; } = "Sheet1!A:Z";
    /// <summary>The values the AI fills in, in column order (after time, name, phone, email).</summary>
    public List<string> Columns { get; init; } = new() { "Request", "Details" };
    /// <summary>What the log is ("Leads", "Orders") — words for the model.</summary>
    public string Label { get; init; } = "Leads";

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["app"] = AppId,
        ["spreadsheet_id"] = SpreadsheetId,
        ["sheet_range"] = SheetRange,
        ["columns"] = Columns.ToList(),
        ["label"] = Label,
    };

    public static Dictionary<string, object?> Defaults() => new SheetsAppConfig().ToDictionary();

    public static SheetsAppConfig From(Dictionary<string, object?>? raw)
    {
        try { return From(JsonSerializer.SerializeToElement(raw ?? new()), out _); }
        catch { return new SheetsAppConfig(); }
    }

    public static SheetsAppConfig From(JsonElement el, out string? error)
    {
        var errors = new List<string>();
        var id = AppSkills.Text(el, "spreadsheet_id", "", 200, "spreadsheet_id", errors);
        // a pasted sheet URL → its id
        var m = Regex.Match(id, @"/spreadsheets/d/([A-Za-z0-9_-]+)");
        if (m.Success) id = m.Groups[1].Value;
        if (id.Length > 0 && !Regex.IsMatch(id, "^[A-Za-z0-9_-]{10,200}$"))
        {
            errors.Add("spreadsheet_id must be a Google Sheets id (or its URL).");
            id = "";
        }
        var range = AppSkills.Text(el, "sheet_range", "Sheet1!A:Z", 100, "sheet_range", errors);
        if (range.Length == 0) range = "Sheet1!A:Z";
        var columns = new SheetsAppConfig().Columns;
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("columns", out var cols)
            && cols.ValueKind != JsonValueKind.Null)
        {
            if (cols.ValueKind != JsonValueKind.Array) errors.Add("columns must be a list of names.");
            else
            {
                columns = cols.EnumerateArray()
                    .Select(c => (c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString()).Trim())
                    .Where(c => c.Length > 0).Distinct().ToList();
                if (columns.Count == 0) errors.Add("name at least one column for the AI to fill in.");
                if (columns.Count > 20) { errors.Add("at most 20 columns."); columns = columns.Take(20).ToList(); }
                if (columns.Any(c => c.Length > 60)) errors.Add("a column name is longer than 60 characters.");
            }
        }
        var cfg = new SheetsAppConfig
        {
            SpreadsheetId = id,
            SheetRange = range,
            Columns = columns,
            Label = AppSkills.Text(el, "label", "Leads", 60, "label", errors) is { Length: > 0 } l ? l : "Leads",
        };
        error = errors.FirstOrDefault();
        return cfg;
    }
}
