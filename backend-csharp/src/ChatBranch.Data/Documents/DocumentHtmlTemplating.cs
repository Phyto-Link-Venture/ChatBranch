using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HandlebarsDotNet;

namespace ChatBranch.Data.Documents;

/// <summary>A custom layout that can't print: the reason, and the 1-based template line when known.</summary>
public class DocumentHtmlException : Exception
{
    public int? Line { get; }

    public DocumentHtmlException(string message, int? line = null) : base(message) => Line = line;
}

/// <summary>
/// The custom layout's template engine — Handlebars.Net (MIT) with ChatBranch's
/// helpers. <c>{{x}}</c> HTML-ESCAPES every value (customer names and AI-written
/// descriptions flow in and must never become markup; the encoder escapes
/// <c>&amp; &lt; &gt; " ' ` =</c>), <c>{{{x}}}</c> prints raw and is allowed only for
/// values the server builds (<see cref="RawAllowed"/>: the logo's data URL). No
/// partials, no decorators. Every helper is culture-invariant and English / Malay
/// aware (the document's language):
/// <c>money</c> · <c>number</c> · <c>date</c> · <c>words</c> · <c>upper</c> ·
/// <c>lower</c> · <c>inc</c> · <c>eq</c> / <c>ne</c> / <c>gt</c> (block or sub-expression)
/// · <c>nl2br</c> (escape, then &lt;br&gt;) · <c>first_line</c> · <c>rest_lines</c> ·
/// <c>label</c> · <c>default</c> · <c>or</c>.
/// </summary>
public static class DocumentHtmlTemplating
{
    /// <summary>The only expressions a template may print raw with {{{…}}}.</summary>
    public static readonly string[] RawAllowed = { "business.logo", "@root.business.logo" };

    public sealed record Helper(string Name, string Usage, string Description);

    /// <summary>Every helper (the editor's reference panel and the AI prompt list these).</summary>
    public static readonly Helper[] Helpers =
    {
        new("money", "{{money totals.total}} · {{money totals.total \"RM\"}}", "An amount as 13,256.00, optionally after a currency word."),
        new("number", "{{number qty}} · {{number value 2}}", "A number with thousands separators (1,234.5), or with a fixed number of decimals."),
        new("date", "{{date document.issue_date \"dd.MM.yyyy\"}}", "A date in any .NET format (d, dd, MMM, MMMM, yyyy…); without a format, the language's usual one. Month names follow the document's language."),
        new("words", "{{words totals.total}} · {{words totals.total \"ms\"}}", "An amount in words — en \"Thirteen Thousand Two Hundred And Fifty Six Only\", ms \"… Sahaja\"; sen as \"… And Fifty Sen Only\"."),
        new("upper", "{{upper customer.name}}", "UPPER CASE."),
        new("lower", "{{lower text}}", "lower case."),
        new("inc", "{{inc @index}}", "A number plus one (a 1-based row number inside {{#each}})."),
        new("eq", "{{#eq document.type \"quotation\"}}…{{else}}…{{/eq}} · {{#if (eq a b)}}", "Equal (as text). Also as a sub-expression."),
        new("ne", "{{#ne document.type \"letter\"}}…{{/ne}}", "Not equal (as text)."),
        new("gt", "{{#gt totals.discount 0}}…{{/gt}}", "Greater than (as numbers)."),
        new("nl2br", "{{nl2br document.notes}}", "Escapes the text, then turns its line breaks into <br>."),
        new("first_line", "{{first_line description}}", "The first line of a text."),
        new("rest_lines", "{{nl2br (rest_lines description)}}", "Everything after the first line."),
        new("label", "{{label \"subtotal\"}}", "The document language's word for a built-in label (invoice, date, due, description, qty, amount, subtotal, total, bank_details, yours_faithfully…)."),
        new("default", "{{default fields.attn_tel customer.phone \"-\"}}", "The first value that isn't empty."),
        new("or", "{{#if (or terms business.bank_details)}}…{{/if}}", "The first value that isn't empty (for {{#if}})."),
    };

    // =========================================================================
    // compile + render
    // =========================================================================

    /// <summary>The first syntax problem with a template (null = it compiles and uses only what is allowed).</summary>
    public static string? Check(string template)
    {
        if (Forbidden(template) is { } why) return why;
        try
        {
            Compiled(template, "en");
            return null;
        }
        catch (DocumentHtmlException ex) { return ex.Message; }
        catch (Exception ex) { return Describe(ex, template).Message; }
    }

    /// <summary>Render a template with a data model; a template problem throws <see cref="DocumentHtmlException"/>.</summary>
    public static string Render(string template, object model, string language)
    {
        if (Forbidden(template) is { } why) throw new DocumentHtmlException(why, LineOfForbidden(template));
        var compiled = Compiled(template, language == "ms" ? "ms" : "en");
        try { return compiled(model); }
        catch (Exception ex) { throw Describe(ex, template); }
    }

    // compiled templates, reused (a layout prints many documents; the editor previews it while typing)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HandlebarsTemplate<object, object>> Cache = new();

    private static HandlebarsTemplate<object, object> Compiled(string template, string language)
    {
        var key = language + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(template)));
        if (Cache.TryGetValue(key, out var hit)) return hit;
        HandlebarsTemplate<object, object> compiled;
        try { compiled = Environment(language).Compile(template); }
        catch (Exception ex) { throw Describe(ex, template); }
        if (Cache.Count >= 64) Cache.Clear();
        Cache[key] = compiled;
        return compiled;
    }

    private static readonly Regex Triple = new(@"\{\{\{\s*([^}]*?)\s*\}\}\}", RegexOptions.Compiled);
    private static readonly Regex Unsupported = new(@"\{\{~?\s*(#?>|#?\*|&)", RegexOptions.Compiled);

    private static string? Forbidden(string template)
    {
        if (Unsupported.Match(template) is { Success: true } u)
            return u.Groups[1].Value.Contains('&')
                ? "{{& …}} prints raw HTML — use {{…}}."
                : "Partials and decorators ({{> …}}, {{#* …}}) aren't supported — write the HTML in this template.";
        foreach (Match m in Triple.Matches(template))
            if (!RawAllowed.Contains(m.Groups[1].Value.Trim()))
                return $"{{{{{{{m.Groups[1].Value.Trim()}}}}}}} prints raw HTML — only {{{{{{business.logo}}}}}} may; use {{{{{m.Groups[1].Value.Trim()}}}}} (it is escaped).";
        return null;
    }

    private static int? LineOfForbidden(string template)
    {
        var m = Unsupported.Match(template);
        if (!m.Success)
            m = Triple.Matches(template).FirstOrDefault(x => !RawAllowed.Contains(x.Groups[1].Value.Trim())) ?? Match.Empty;
        return m.Success ? LineAt(template, m.Index) : null;
    }

    private static int LineAt(string s, int index) => s.AsSpan(0, Math.Min(index, s.Length)).Count('\n') + 1;

    /// <summary>A Handlebars error as words + the line it happened on (parser errors say "Occured at: line:col", 0-based).</summary>
    private static DocumentHtmlException Describe(Exception ex, string template)
    {
        var msg = (ex.InnerException is HandlebarsException inner && ex is not HandlebarsException ? inner : ex).Message;
        msg = Regex.Replace(msg, @"\s+", " ").Trim();
        int? line = null;
        if (Regex.Match(msg, @"Occured at:\s*(\d+):(\d+)") is { Success: true } at)
        {
            line = int.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
            msg = msg[..at.Index].Trim().TrimEnd('/').Trim();
        }
        else if (Regex.Match(msg, @"closing element '/?([^']+)'") is { Success: true } close
                 && template.IndexOf("{{/" + close.Groups[1].Value.TrimStart('/'), StringComparison.Ordinal) is var ci and >= 0)
            line = LineAt(template, ci);
        else if (Regex.Match(msg, @"block expression '#?([^']+)'") is { Success: true } open
                 && template.LastIndexOf("{{#" + open.Groups[1].Value.TrimStart('#'), StringComparison.Ordinal) is var oi and >= 0)
            line = LineAt(template, oi);
        else if (Regex.Match(msg, @"Helper '([^']+)'") is { Success: true } helper
                 && Regex.Match(template, @"\{\{[#/]?\s*\(?\s*" + Regex.Escape(helper.Groups[1].Value) + @"\b") is { Success: true } hm)
            line = LineAt(template, hm.Index);
        if (msg.Length > 300) msg = msg[..300];
        return new DocumentHtmlException(line is int l ? $"Line {l}: {msg}" : msg, line);
    }

    // =========================================================================
    // the environment
    // =========================================================================

    /// <summary>HTML escaping for {{…}}: &amp; &lt; &gt; " ' ` = (non-ASCII stays readable).</summary>
    private sealed class Encoder : ITextEncoder
    {
        public void Encode(StringBuilder text, TextWriter target) => Encode(text.ToString(), target);

        public void Encode(string text, TextWriter target)
        {
            foreach (var ch in text) Write(ch, target);
        }

        public void Encode<T>(T text, TextWriter target) where T : IEnumerator<char>
        {
            while (text.MoveNext()) Write(text.Current, target);
        }

        private static void Write(char ch, TextWriter w)
        {
            switch (ch)
            {
                case '&': w.Write("&amp;"); break;
                case '<': w.Write("&lt;"); break;
                case '>': w.Write("&gt;"); break;
                case '"': w.Write("&quot;"); break;
                case '\'': w.Write("&#39;"); break;
                case '`': w.Write("&#96;"); break;
                case '=': w.Write("&#61;"); break;
                default: w.Write(ch); break;
            }
        }
    }

    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        using var w = new StringWriter(CultureInfo.InvariantCulture);
        new Encoder().Encode(s, w);
        return w.ToString();
    }

    private static IHandlebars Environment(string language)
    {
        var ms = language == "ms";
        var env = Handlebars.Create(new HandlebarsConfiguration
        {
            TextEncoder = new Encoder(),
            FormatProvider = CultureInfo.InvariantCulture,
        });
        string Arg(Arguments a, int i) => i < a.Length ? Text(a[i]) : "";

        env.RegisterHelper("money", (Context _, Arguments a) =>
        {
            var v = Dec(a.Length > 0 ? a[0] : null);
            var text = v is decimal d ? DocumentMath.Money(d) : "";
            var cur = Arg(a, 1);
            return cur.Length > 0 && text.Length > 0 ? $"{cur} {text}" : text;
        });
        env.RegisterHelper("number", (Context _, Arguments a) =>
        {
            if (Dec(a.Length > 0 ? a[0] : null) is not decimal d) return "";
            if (a.Length > 1 && int.TryParse(Arg(a, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var places))
                return d.ToString("#,##0." + new string('0', Math.Clamp(places, 0, 6)), CultureInfo.InvariantCulture).TrimEnd('.');
            return d.ToString("#,##0.####", CultureInfo.InvariantCulture);
        });
        env.RegisterHelper("date", (Context _, Arguments a) => FormatDate(Arg(a, 0), Arg(a, 1), language));
        env.RegisterHelper("words", (Context _, Arguments a) =>
            Dec(a.Length > 0 ? a[0] : null) is decimal d ? Words(d, a.Length > 1 ? Arg(a, 1) : language) : "");
        env.RegisterHelper("upper", (Context _, Arguments a) => Arg(a, 0).ToUpperInvariant());
        env.RegisterHelper("lower", (Context _, Arguments a) => Arg(a, 0).ToLowerInvariant());
        env.RegisterHelper("inc", (Context _, Arguments a) => (Dec(a.Length > 0 ? a[0] : null) ?? 0m) + 1);
        env.RegisterHelper("first_line", (Context _, Arguments a) => FirstLine(Arg(a, 0)));
        env.RegisterHelper("rest_lines", (Context _, Arguments a) => string.Join("\n", RestLines(Arg(a, 0))));
        env.RegisterHelper("label", (Context _, Arguments a) => DocumentHtmlLabels.Get(Arg(a, 0), ms));
        env.RegisterHelper("default", (Context _, Arguments a) =>
        {
            for (var i = 0; i < a.Length; i++)
                if (!Empty(a[i])) return a[i];
            return "";
        });
        env.RegisterHelper("or", (Context _, Arguments a) =>
        {
            for (var i = 0; i < a.Length; i++)
                if (!Empty(a[i]) && a[i] is not false) return a[i];
            return "";
        });
        env.RegisterHelper("nl2br", (EncodedTextWriter w, Context _, Arguments a) =>
        {
            var lines = Arg(a, 0).Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) w.WriteSafeString("<br>");
                w.WriteSafeString(Escape(lines[i]));
            }
        });

        static bool Eq(Arguments a) => a.Length >= 2 && Text(a[0]) == Text(a[1]);
        static bool Gt(Arguments a) => a.Length >= 2 && Dec(a[0]) is decimal x && Dec(a[1]) is decimal y && x > y;
        env.RegisterHelper("eq", (Context _, Arguments a) => Eq(a));
        env.RegisterHelper("ne", (Context _, Arguments a) => !Eq(a));
        env.RegisterHelper("gt", (Context _, Arguments a) => Gt(a));
        env.RegisterHelper("eq", (EncodedTextWriter w, BlockHelperOptions o, Context c, Arguments a) =>
        {
            if (Eq(a)) o.Template(w, c); else o.Inverse(w, c);
        });
        env.RegisterHelper("ne", (EncodedTextWriter w, BlockHelperOptions o, Context c, Arguments a) =>
        {
            if (!Eq(a)) o.Template(w, c); else o.Inverse(w, c);
        });
        env.RegisterHelper("gt", (EncodedTextWriter w, BlockHelperOptions o, Context c, Arguments a) =>
        {
            if (Gt(a)) o.Template(w, c); else o.Inverse(w, c);
        });
        return env;
    }

    private static bool Empty(object? v) => v switch
    {
        null => true,
        string s => s.Trim().Length == 0,
        ICollection c => c.Count == 0,
        _ => false,
    };

    private static string Text(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable and not string => "",
        _ => v.ToString() ?? "",
    };

    private static decimal? Dec(object? v)
    {
        switch (v)
        {
            case null: return null;
            case decimal d: return d;
            case int i: return i;
            case long l: return l;
            case double db: return double.IsFinite(db) ? (decimal)db : null;
            case float f: return float.IsFinite(f) ? (decimal)f : null;
        }
        var s = Regex.Replace(Text(v), @"[^\d.\-]", "");
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    public static string FirstLine(string? text) =>
        (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    public static List<string> RestLines(string? text)
    {
        var lines = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        var first = lines.FindIndex(l => l.Length > 0);
        return first < 0 ? new List<string>() : lines.Skip(first + 1).Where(l => l.Length > 0).ToList();
    }

    // =========================================================================
    // dates
    // =========================================================================

    private static readonly DateTimeFormatInfo MalayDates = MakeMalay();

    private static DateTimeFormatInfo MakeMalay()
    {
        var f = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
        f.MonthNames = new[] { "Januari", "Februari", "Mac", "April", "Mei", "Jun", "Julai", "Ogos", "September", "Oktober", "November", "Disember", "" };
        f.MonthGenitiveNames = f.MonthNames;
        f.AbbreviatedMonthNames = DocumentRenderer.Labels.MalayMonths.Append("").ToArray();
        f.AbbreviatedMonthGenitiveNames = f.AbbreviatedMonthNames;
        f.DayNames = new[] { "Ahad", "Isnin", "Selasa", "Rabu", "Khamis", "Jumaat", "Sabtu" };
        f.AbbreviatedDayNames = new[] { "Ahd", "Isn", "Sel", "Rab", "Kha", "Jum", "Sab" };
        return f;
    }

    /// <summary>An ISO date (YYYY-MM-DD…) in a .NET format; "" when it isn't a date. No format = the language's usual one.</summary>
    public static string FormatDate(string? iso, string? format, string language)
    {
        var s = (iso ?? "").Trim();
        if (s.Length < 10 || !DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d)) return s;
        if (string.IsNullOrWhiteSpace(format)) return DocumentRenderer.DateLabel(d, language);
        try
        {
            return d.ToDateTime(TimeOnly.MinValue).ToString(format,
                language == "ms" ? MalayDates : CultureInfo.InvariantCulture.DateTimeFormat);
        }
        catch (FormatException) { return DocumentRenderer.DateLabel(d, language); }
    }

    // =========================================================================
    // amounts in words
    // =========================================================================

    private static readonly string[] EnOnes =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve",
        "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    };
    private static readonly string[] EnTens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };
    private static readonly string[] MsOnes = { "Kosong", "Satu", "Dua", "Tiga", "Empat", "Lima", "Enam", "Tujuh", "Lapan", "Sembilan" };

    /// <summary>
    /// An amount in words, the way a Malaysian cheque or invoice writes it:
    /// en "Thirteen Thousand Two Hundred And Fifty Six Only" (sen: "… And Fifty Sen
    /// Only"), ms "Tiga Belas Ribu Dua Ratus Lima Puluh Enam Sahaja" ("… Dan Lima Puluh
    /// Sen Sahaja"). Rounded to the sen, half away from zero; up to 999,999,999,999.99.
    /// </summary>
    public static string Words(decimal amount, string? language)
    {
        var ms = (language ?? "").Trim().ToLowerInvariant() == "ms";
        var v = Math.Abs(DocumentMath.Round(amount));
        if (v >= 1_000_000_000_000m) return DocumentMath.Money(amount);
        var whole = (long)Math.Floor(v);
        var sen = (int)((v - whole) * 100);
        var parts = new List<string>();
        if (whole > 0 || sen == 0) parts.Add(ms ? MalayWords(whole) : EnglishWords(whole));
        if (sen > 0)
        {
            var senWords = (ms ? MalayWords(sen) : EnglishWords(sen)) + " Sen";
            parts.Add(whole > 0 ? (ms ? "Dan " : "And ") + senWords : senWords);
        }
        var text = string.Join(" ", parts) + (ms ? " Sahaja" : " Only");
        return amount < 0 ? (ms ? "Tolak " : "Minus ") + text : text;
    }

    private static string EnglishWords(long n)
    {
        if (n == 0) return EnOnes[0];
        var groups = new (long Size, string Name)[] { (1_000_000_000, "Billion"), (1_000_000, "Million"), (1_000, "Thousand"), (1, "") };
        var words = new List<string>();
        foreach (var (size, name) in groups)
        {
            var chunk = (int)(n / size % 1000);
            if (chunk == 0) continue;
            // "One Thousand And Fifty": a last group under a hundred after bigger ones takes "And"
            var andBefore = size == 1 && chunk < 100 && n >= 1000;
            words.Add((andBefore ? "And " : "") + EnglishHundreds(chunk) + (name.Length > 0 ? " " + name : ""));
        }
        return string.Join(" ", words);
    }

    private static string EnglishHundreds(int n)
    {
        var parts = new List<string>();
        if (n >= 100) parts.Add(EnOnes[n / 100] + " Hundred");
        var rest = n % 100;
        if (rest > 0)
        {
            var tens = rest < 20 ? EnOnes[rest] : EnTens[rest / 10] + (rest % 10 > 0 ? " " + EnOnes[rest % 10] : "");
            parts.Add(n >= 100 ? "And " + tens : tens);
        }
        return string.Join(" ", parts);
    }

    private static string MalayWords(long n)
    {
        if (n == 0) return MsOnes[0];
        var words = new List<string>();
        foreach (var (size, name, one) in new (long, string, string)[]
                 {
                     (1_000_000_000, "Bilion", "Satu Bilion"), (1_000_000, "Juta", "Satu Juta"), (1_000, "Ribu", "Seribu"), (1, "", ""),
                 })
        {
            var chunk = (int)(n / size % 1000);
            if (chunk == 0) continue;
            if (chunk == 1 && size > 1) words.Add(one);
            else words.Add(MalayHundreds(chunk) + (name.Length > 0 ? " " + name : ""));
        }
        return string.Join(" ", words);
    }

    private static string MalayHundreds(int n)
    {
        var parts = new List<string>();
        var h = n / 100;
        if (h == 1) parts.Add("Seratus");
        else if (h > 1) parts.Add(MsOnes[h] + " Ratus");
        var rest = n % 100;
        if (rest == 0) return string.Join(" ", parts);
        if (rest < 10) parts.Add(MsOnes[rest]);
        else if (rest == 10) parts.Add("Sepuluh");
        else if (rest == 11) parts.Add("Sebelas");
        else if (rest < 20) parts.Add(MsOnes[rest % 10] + " Belas");
        else parts.Add(MsOnes[rest / 10] + " Puluh" + (rest % 10 > 0 ? " " + MsOnes[rest % 10] : ""));
        return string.Join(" ", parts);
    }
}

/// <summary>
/// The words the <c>label</c> helper prints — the designer's EN / BM label table
/// (<see cref="DocumentRenderer.Labels"/>) plus the lines a formal Malaysian
/// document uses (Attn, Dear Sir / Madam, amount in words, signature block).
/// </summary>
public static class DocumentHtmlLabels
{
    private static readonly Dictionary<string, (string En, string Ms)> Extra = new()
    {
        ["ref_no"] = ("Ref No", "No. Rujukan"),
        ["attn"] = ("Attn", "U/P"),
        ["tel"] = ("Tel", "Tel"),
        ["fax"] = ("Fax", "Faks"),
        ["dear"] = ("Dear Sir / Madam,", "Tuan / Puan,"),
        ["project"] = ("PRO", "PROJEK"),
        ["ref"] = ("REF", "RUJ"),
        ["index"] = ("NO", "BIL"),
        ["rate"] = ("Rate", "Kadar"),
        ["amount_in_words"] = ("Ringgit Malaysia", "Ringgit Malaysia"),
        ["total_payment"] = ("Total Payment", "Jumlah Bayaran"),
        ["note"] = ("NOTE", "CATATAN"),
        ["yours_faithfully"] = ("Yours faithfully,", "Yang benar,"),
        ["acknowledge_received"] = ("Acknowledge Received", "Diterima Oleh"),
        ["chop_sign"] = ("(CHOP & SIGN)", "(COP & TANDATANGAN)"),
        ["thank_you"] = ("Thank you for your business.", "Terima kasih atas sokongan anda."),
        ["customer"] = ("Customer", "Pelanggan"),
        ["subject"] = ("Subject", "Perkara"),
    };

    /// <summary>Every key (the reference panel lists them).</summary>
    public static IEnumerable<string> Keys => Base(DocumentRenderer.Labels.En).Keys.Concat(Extra.Keys);

    public static string Get(string key, bool ms)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        if (Extra.TryGetValue(k, out var e)) return ms ? e.Ms : e.En;
        return Base(ms ? DocumentRenderer.Labels.Ms : DocumentRenderer.Labels.En).TryGetValue(k, out var v) ? v : key ?? "";
    }

    private static Dictionary<string, string> Base(DocumentRenderer.Labels l) => new()
    {
        ["invoice"] = l.Invoice, ["quotation"] = l.Quotation, ["receipt"] = l.Receipt, ["letter"] = l.Letter,
        ["number"] = l.Number, ["date"] = l.Date, ["due"] = l.Due, ["valid_until"] = l.ValidUntil,
        ["reference"] = l.Reference, ["bill_to"] = l.BillTo, ["received_from"] = l.ReceivedFrom, ["to"] = l.To,
        ["description"] = l.Description, ["qty"] = l.Qty, ["unit_price"] = l.UnitPrice, ["discount"] = l.Discount,
        ["amount"] = l.Amount, ["subtotal"] = l.Subtotal, ["discounts_included"] = l.DiscountsIncluded, ["tax"] = l.Tax,
        ["total"] = l.Total, ["amount_paid"] = l.AmountPaid, ["paid"] = l.Paid, ["notes"] = l.Notes,
        ["payment"] = l.Payment, ["page"] = l.Page, ["of"] = l.Of, ["reg_no"] = l.RegNo, ["no"] = l.No,
        ["sincerely"] = l.Sincerely, ["terms"] = l.Terms, ["bank_details"] = l.BankDetails,
    };
}
