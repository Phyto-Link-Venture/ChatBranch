using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Domain;

namespace ChatBranch.Data.Documents;

/// <summary>One custom field a custom layout prints (the AI's tools and the operator's form ask for it).</summary>
public sealed record DocField(string Key, string Label, string Type, bool Required, string Default)
{
    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["key"] = Key, ["label"] = Label, ["type"] = Type, ["required"] = Required, ["default"] = Default,
    };
}

/// <summary>
/// The Documents skill's "Custom layout" — the <c>html</c> object of a documents
/// config used when <c>layout: "html"</c> (snake_case, <see cref="CurrentVersion"/>-versioned):
/// <list type="bullet">
/// <item><c>template</c> — the default Handlebars HTML (a whole document with its own
/// <c>&lt;style&gt;</c>), ≤ <see cref="MaxTemplateChars"/> characters; <c>templates</c> —
/// optional per-type overrides (invoice | quotation | receipt | letter).</item>
/// <item><c>fields</c> — up to <see cref="MaxFields"/> custom fields the template prints
/// (<c>{{fields.project}}</c>): extra arguments of the AI's <c>document_create_*</c> tools
/// and inputs on the operator's create form.</item>
/// <item><c>page</c> — size + <c>margin_mm</c> defaults (a template's own CSS <c>@page</c>
/// wins); <c>letterhead</c> — an uploaded PDF drawn under the pages, like the designer's;
/// <c>terms</c> / <c>bank_details</c> — the fixed words the template lists
/// (<c>terms[]</c>, <c>business.bank_details</c>).</item>
/// </list>
/// <see cref="From"/> is the ONE normalizer: unknown keys are refused with the first
/// problem, every template is SANITIZED (<see cref="HtmlSanitizer"/>) and — on a strict
/// read — compiled and test-rendered with sample data, so the skills PUT refuses a
/// template that can't print. <see cref="ToDictionary"/> is the canonical form.
/// </summary>
public sealed class DocumentHtmlConfig
{
    public const int CurrentVersion = 1;
    public const int MaxTemplateChars = 200 * 1024;
    public const int MaxFields = 20;
    public static readonly string[] FieldTypes = { "text", "multiline", "date", "number" };
    public static readonly string[] Keys =
        { "version", "template", "templates", "fields", "page", "letterhead", "terms", "bank_details" };

    /// <summary>
    /// Names a custom field may not take: the tools' own arguments and the data
    /// model's top-level names (a field called "notes" would shadow the real one).
    /// </summary>
    public static readonly string[] ReservedKeys =
    {
        "business", "document", "customer", "items", "totals", "fields", "terms", "page", "labels",
        "customer_name", "customer_company", "customer_address", "customer_email", "customer_phone",
        "recipient_name", "recipient_address", "due_date", "reference", "notes", "title", "payment_method",
        "paragraphs", "closing", "body", "number", "type", "currency", "language", "issue_date", "valid_until",
    };

    public int Version { get; set; } = CurrentVersion;
    public string Template { get; set; } = "";
    public Dictionary<string, string> Templates { get; set; } = new();
    public List<DocField> Fields { get; set; } = new();
    public string PageSize { get; set; } = "A4";
    public double MarginTop { get; set; } = 16;
    public double MarginRight { get; set; } = 18;
    public double MarginBottom { get; set; } = 20;
    public double MarginLeft { get; set; } = 18;
    public DocumentTemplate.LetterheadSpec Letterhead { get; set; } = new();
    public string Terms { get; set; } = "";
    public string BankDetails { get; set; } = "";

    /// <summary>The template a document of <paramref name="type"/> prints with (its override, else the default).</summary>
    public string TemplateFor(string type) =>
        Templates.TryGetValue(type, out var t) && t.Trim().Length > 0 ? t : Template;

    public DocField? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["version"] = Version,
        ["template"] = Template,
        ["templates"] = DocumentTypes.Numbered.Where(Templates.ContainsKey).ToDictionary(k => k, k => (object?)Templates[k]),
        ["fields"] = Fields.Select(f => f.ToDictionary()).ToList(),
        ["page"] = new Dictionary<string, object?>
        {
            ["size"] = PageSize,
            ["margin_mm"] = new Dictionary<string, object?>
            {
                ["top"] = MarginTop, ["right"] = MarginRight, ["bottom"] = MarginBottom, ["left"] = MarginLeft,
            },
        },
        ["letterhead"] = new Dictionary<string, object?> { ["file"] = Letterhead.File, ["pages"] = Letterhead.Pages },
        ["terms"] = Terms,
        ["bank_details"] = BankDetails,
    };

    public DocumentHtmlConfig Copy() => From(JsonSerializer.SerializeToElement(ToDictionary()), out _, check: false)!;

    /// <summary>
    /// Validate + normalize a wire <c>html</c> object. <paramref name="check"/> (the
    /// skills PUT, the dry run, the editor's endpoints) also compiles and test-renders
    /// every template; a lenient read of a STORED config skips that (it was checked
    /// when it was saved, and the renderer sanitizes again). Returns null only when
    /// <paramref name="el"/> isn't an object; <paramref name="error"/> = the first problem.
    /// </summary>
    public static DocumentHtmlConfig? From(JsonElement el, out string? error, bool check = true)
    {
        var errors = new List<string>();
        var c = Read(el, errors, check);
        error = errors.FirstOrDefault();
        return c;
    }

    private static DocumentHtmlConfig? Read(JsonElement el, List<string> errors, bool check)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add("html must be a JSON object.");
            return null;
        }
        var root = new DocumentTemplate.Obj(el, "html", errors, Keys);
        var c = new DocumentHtmlConfig { Version = root.Int("version", CurrentVersion, 1, CurrentVersion) };

        c.Template = TemplateText(el, "template", "html.template", errors, check) ?? "";
        if (root.Child("templates", DocumentTypes.Numbered) is { } tps)
            foreach (var type in DocumentTypes.Numbered)
                if (TemplateText(tps.El, type, $"html.templates.{type}", errors, check) is { Length: > 0 } t)
                    c.Templates[type] = t;

        c.Fields = ReadFields(el, errors);

        if (root.Child("page", "size", "margin_mm") is { } page)
        {
            c.PageSize = page.Enum("size", DocumentTemplate.PageSizes, c.PageSize, ignoreCase: true);
            if (page.Child("margin_mm", "top", "right", "bottom", "left") is { } m)
            {
                c.MarginTop = m.Num("top", c.MarginTop, 0, 60);
                c.MarginRight = m.Num("right", c.MarginRight, 0, 60);
                c.MarginBottom = m.Num("bottom", c.MarginBottom, 0, 60);
                c.MarginLeft = m.Num("left", c.MarginLeft, 0, 60);
            }
        }
        if (root.Child("letterhead", "file", "pages") is { } lh)
        {
            var file = lh.Text("file", "", 64);
            if (file.Length > 0 && !DocumentService.IsLetterheadName(file))
            {
                errors.Add("html.letterhead.file must be an uploaded letterhead PDF.");
                file = "";
            }
            c.Letterhead.File = file;
            c.Letterhead.Pages = lh.Enum("pages", DocumentTemplate.LetterheadPages, c.Letterhead.Pages);
        }
        c.Terms = root.Text("terms", "", 2000);
        c.BankDetails = root.Text("bank_details", "", 1000);

        if (check && c.Template.Length > 0)
        {
            // every template must print: a sample of each type it serves, with sample data
            foreach (var type in DocumentTypes.Numbered)
            {
                var problem = DocumentHtmlRenderer.CheckSample(c, type);
                if (problem == null) continue;
                var which = c.Templates.ContainsKey(type) ? $"html.templates.{type}" : "html.template";
                errors.Add($"{which} can't print a {DocumentTypes.Label(type).ToLowerInvariant()}: {problem}");
                break;
            }
        }
        return c;
    }

    /// <summary>A template string: size-capped, sanitized, its Handlebars syntax checked.</summary>
    private static string? TemplateText(JsonElement el, string key, string path, List<string> errors, bool check)
    {
        if (!el.TryGetProperty(key, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (v.ValueKind != JsonValueKind.String) { errors.Add($"{path} must be text (an HTML document)."); return null; }
        var s = (v.GetString() ?? "").Replace("\r\n", "\n");
        if (s.Trim().Length == 0) return "";
        if (s.Length > MaxTemplateChars)
        {
            errors.Add($"{path} is larger than {MaxTemplateChars / 1024} KB.");
            return null;
        }
        if (!check) return s;
        var clean = HtmlSanitizer.Sanitize(s, template: true);
        if (DocumentHtmlTemplating.Check(clean) is { } problem) { errors.Add($"{path}: {problem}"); return clean; }
        return clean;
    }

    private static List<DocField> ReadFields(JsonElement el, List<string> errors)
    {
        var list = new List<DocField>();
        if (!el.TryGetProperty("fields", out var fs) || fs.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return list;
        if (fs.ValueKind != JsonValueKind.Array) { errors.Add("html.fields must be a list of {key, label, type}."); return list; }
        var i = 0;
        foreach (var f in fs.EnumerateArray())
        {
            i++;
            var path = $"html.fields[{i - 1}]";
            if (f.ValueKind != JsonValueKind.Object) { errors.Add($"{path} must be an object."); continue; }
            var o = new DocumentTemplate.Obj(f, path, errors, "key", "label", "type", "required", "default");
            var key = o.Text("key", "", 40);
            if (!Regex.IsMatch(key, "^[a-z][a-z0-9_]{0,31}$"))
            {
                errors.Add($"{path}.key must start with a lowercase letter and use only a–z, 0–9 and _ (up to 32), e.g. project.");
                continue;
            }
            if (ReservedKeys.Contains(key)) { errors.Add($"{path}.key '{key}' is a built-in name — pick another (e.g. {key}_2)."); continue; }
            if (list.Any(x => x.Key == key)) { errors.Add($"{path}.key '{key}' is used twice."); continue; }
            var label = o.Text("label", "", 60);
            if (label.Length == 0) label = key;
            var type = o.Enum("type", FieldTypes, "text");
            var required = o.Bool("required", false);
            var dflt = o.Text("default", "", type == "multiline" ? 2000 : 500);
            if (dflt.Length > 0 && FieldValue(type, dflt, out _) == null)
            {
                errors.Add($"{path}.default doesn't fit the {type} type{(type == "date" ? " (use YYYY-MM-DD)" : "")}.");
                dflt = "";
            }
            list.Add(new DocField(key, label, type, required, dflt));
        }
        if (list.Count > MaxFields) { errors.Add($"html.fields may hold up to {MaxFields} fields."); list = list.Take(MaxFields).ToList(); }
        return list;
    }

    /// <summary>A custom field's value checked against its type (null = doesn't fit). Dates → YYYY-MM-DD.</summary>
    public static string? FieldValue(string type, string raw, out string? why)
    {
        why = null;
        var s = raw.Trim();
        switch (type)
        {
            case "number":
                var cleaned = Regex.Replace(s, @"[^\d.\-]", "");
                if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                    return n.ToString(CultureInfo.InvariantCulture);
                why = "must be a number";
                return null;
            case "date":
                if (DateOnly.TryParseExact(s.Length >= 10 ? s[..10] : s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d))
                    return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                why = "must be a date as YYYY-MM-DD";
                return null;
            case "multiline":
                return s.Length <= 2000 ? s : s[..2000];
            default:
                s = s.Replace("\r", "").Replace('\n', ' ');
                return s.Length <= 500 ? s : s[..500];
        }
    }
}
