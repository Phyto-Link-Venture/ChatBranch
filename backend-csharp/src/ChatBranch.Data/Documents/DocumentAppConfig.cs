using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Domain;

namespace ChatBranch.Data.Documents;

/// <summary>
/// The configuration of a Documents "Connected app" skill
/// (<c>AgentSkill.Config</c>, kind <c>app</c>, <c>app: "documents"</c>):
/// the letterhead (business name, address, registration / tax numbers,
/// contacts, logo, accent colour), money rules (currency, tax label + rate),
/// the words printed on every document (payment instructions, footer) and the
/// numbering (prefix + first number per type), and optionally the designed
/// look (<c>template</c>, <see cref="DocumentTemplate"/>) — or, with
/// <c>layout: "html"</c>, a custom HTML layout (<c>html</c>, <see cref="DocumentHtmlConfig"/>)
/// printed by headless Chromium. Snake_case on the wire;
/// <see cref="From(JsonElement, out string?)"/> is what the skills PUT refuses a
/// bad config with.
/// </summary>
public sealed class DocumentAppConfig
{
    public const string AppId = "documents";

    /// <summary>The tools a skill may offer, one per document type.</summary>
    public static readonly string[] AllActions = DocumentTypes.Numbered;

    /// <summary>Tag palette keys (the same as tags) → the accent printed on the document.</summary>
    public static readonly Dictionary<string, string> Accents = new()
    {
        ["tangerine"] = "#E8590C", ["ink"] = "#1F2328", ["teal"] = "#0F766E", ["plum"] = "#7E3A8C",
        ["ochre"] = "#B7791F", ["blue"] = "#1D4ED8", ["green"] = "#15803D", ["rose"] = "#BE123C",
    };

    public static readonly string[] Languages = { "auto", "en", "ms" };

    public const string DesignerLayout = "designer";
    public const string HtmlLayout = "html";
    public static readonly string[] Layouts = { DesignerLayout, HtmlLayout };

    public string App => AppId;
    public List<string> AllowedActions { get; init; } = AllActions.ToList();
    public string BusinessName { get; init; } = "";
    public string Address { get; init; } = "";
    public string RegistrationNo { get; init; } = "";
    public string TaxNo { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Email { get; init; } = "";
    public string Website { get; init; } = "";
    public string Currency { get; init; } = "MYR";
    /// <summary>"SST", "GST", "VAT" — "" with a zero rate = no tax line.</summary>
    public string TaxLabel { get; init; } = "SST";
    /// <summary>Percent, e.g. 8 for 8 %.</summary>
    public decimal TaxRate { get; init; }
    public string PaymentInstructions { get; init; } = "";
    public string FooterNote { get; init; } = "Thank you for your business.";
    /// <summary>Days a quotation stays valid (printed as "Valid until").</summary>
    public int QuotationValidDays { get; init; } = 14;
    /// <summary>Days until an invoice is due (printed as "Due").</summary>
    public int InvoiceDueDays { get; init; } = 14;
    public Dictionary<string, string> Prefixes { get; init; } = DefaultPrefixes();
    /// <summary>The first number of each series (a later issued number always wins).</summary>
    public Dictionary<string, int> StartNumbers { get; init; } = AllActions.ToDictionary(a => a, _ => 1);
    /// <summary>Zero padding of the running number (INV-0012 = 4).</summary>
    public int NumberDigits { get; init; } = 4;
    /// <summary>A tag palette key (<see cref="Accents"/>).</summary>
    public string Accent { get; init; } = "ink";
    /// <summary>An uploaded logo's stored name (<see cref="DocumentService.LogoPath"/>), "" = none.</summary>
    public string Logo { get; init; } = "";
    /// <summary>The printed labels: auto (the contact's language: ms → Malay, else English), en, ms.</summary>
    public string Language { get; init; } = "auto";
    /// <summary>The designed look (<see cref="DocumentTemplate"/>); null = the classic
    /// layout, exactly as before the template designer existed.</summary>
    public DocumentTemplate? Template { get; init; }
    /// <summary>designer (the template designer / classic layout — the default) | html (<see cref="Html"/>).</summary>
    public string Layout { get; init; } = DesignerLayout;
    /// <summary>The custom HTML layout; kept while the designer is in use, so switching back loses nothing.</summary>
    public DocumentHtmlConfig? Html { get; init; }

    /// <summary>Documents print with the custom HTML layout.</summary>
    public bool IsHtml => Layout == HtmlLayout && Html != null;

    /// <summary>The custom fields this skill's documents take (none in the designer layout).</summary>
    public List<DocField> CustomFields => IsHtml ? Html!.Fields : new List<DocField>();

    /// <summary>The template a document of <paramref name="type"/> prints with.</summary>
    public DocumentTemplate TemplateFor(string type) => Template?.ForType(type) ?? DocumentTemplate.Classic();

    public bool Allows(string type) => AllowedActions.Contains(type);

    public static Dictionary<string, string> DefaultPrefixes() => new()
    {
        [DocumentTypes.Invoice] = "INV-",
        [DocumentTypes.Quotation] = "QUO-",
        [DocumentTypes.Receipt] = "RCP-",
        [DocumentTypes.Letter] = "LTR-",
    };

    public string AccentHex => Accents.TryGetValue(Accent, out var hex) ? hex : Accents["ink"];

    public string Prefix(string type) => Prefixes.TryGetValue(type, out var p) ? p : "";

    public int StartNumber(string type) => StartNumbers.TryGetValue(type, out var n) && n > 0 ? n : 1;

    public string FormatNumber(string type, int seq) =>
        Prefix(type) + seq.ToString(new string('0', Math.Clamp(NumberDigits, 1, 10)));

    public static Dictionary<string, object?> Defaults() => new DocumentAppConfig().ToDictionary();

    public Dictionary<string, object?> ToDictionary()
    {
        var d = Fields();
        if (Template != null) d["template"] = Template.ToDictionary();
        // a config that never had a custom layout keeps its exact old shape
        if (Html != null || Layout != DesignerLayout)
        {
            d["layout"] = Layout;
            d["html"] = Html?.ToDictionary();
        }
        return d;
    }

    private Dictionary<string, object?> Fields() => new()
    {
        ["app"] = AppId,
        ["actions"] = AllowedActions.ToList(),
        ["business_name"] = BusinessName,
        ["address"] = Address,
        ["registration_no"] = RegistrationNo,
        ["tax_no"] = TaxNo,
        ["phone"] = Phone,
        ["email"] = Email,
        ["website"] = Website,
        ["currency"] = Currency,
        ["tax_label"] = TaxLabel,
        ["tax_rate"] = TaxRate,
        ["payment_instructions"] = PaymentInstructions,
        ["footer_note"] = FooterNote,
        ["quotation_valid_days"] = QuotationValidDays,
        ["invoice_due_days"] = InvoiceDueDays,
        ["prefixes"] = AllActions.ToDictionary(a => a, a => (object?)Prefix(a)),
        ["start_numbers"] = AllActions.ToDictionary(a => a, a => (object?)StartNumber(a)),
        ["number_digits"] = NumberDigits,
        ["accent"] = Accent,
        ["logo"] = Logo,
        ["language"] = Language,
    };

    /// <summary>A STORED config, read leniently (its custom layout was checked when it was saved).</summary>
    public static DocumentAppConfig From(Dictionary<string, object?>? raw)
    {
        try { return From(JsonSerializer.SerializeToElement(raw ?? new()), out _, check: false); }
        catch { return new DocumentAppConfig(); }
    }

    /// <summary>
    /// Validate a wire config (<paramref name="error"/> = the first problem). <paramref name="check"/>
    /// also compiles and test-prints a custom layout's templates (the skills PUT, the dry run).
    /// </summary>
    public static DocumentAppConfig From(JsonElement el, out string? error, bool check = true)
    {
        var errors = new List<string>();
        if (el.ValueKind != JsonValueKind.Object)
        {
            error = "config must be a JSON object.";
            return new DocumentAppConfig();
        }
        var d = new DocumentAppConfig();
        string T(string key, string dflt, int max) => AppSkills.Text(el, key, dflt, max, key, errors);

        var email = T("email", "", 200);
        if (email.Length > 0 && !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
        {
            errors.Add("email doesn't look like an email address.");
            email = "";
        }

        var prefixes = DefaultPrefixes();
        if (el.TryGetProperty("prefixes", out var px) && px.ValueKind == JsonValueKind.Object)
            foreach (var p in px.EnumerateObject())
            {
                if (!AllActions.Contains(p.Name)) { errors.Add($"unknown document type '{p.Name}' in prefixes."); continue; }
                var v = (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "").Trim();
                if (v.Length > 12 || !Regex.IsMatch(v, @"^[A-Za-z0-9/_.\- ]*$"))
                {
                    errors.Add($"the {p.Name} prefix may use up to 12 letters, digits, '-', '/', '_' or '.'.");
                    continue;
                }
                prefixes[p.Name] = v;
            }

        var starts = AllActions.ToDictionary(a => a, _ => 1);
        if (el.TryGetProperty("start_numbers", out var sn) && sn.ValueKind == JsonValueKind.Object)
            foreach (var p in sn.EnumerateObject())
            {
                if (!AllActions.Contains(p.Name)) { errors.Add($"unknown document type '{p.Name}' in start_numbers."); continue; }
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n) && n is >= 1 and <= 99_999_999)
                    starts[p.Name] = n;
                else if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out var ns)
                         && ns is >= 1 and <= 99_999_999)
                    starts[p.Name] = ns;
                else errors.Add($"the first {p.Name} number must be a whole number from 1 to 99,999,999.");
            }

        var accent = (AppSkills.Str(el, "accent") ?? d.Accent).Trim().ToLowerInvariant();
        if (accent.Length == 0) accent = d.Accent;
        if (!Accents.ContainsKey(accent))
        {
            errors.Add($"accent must be one of {string.Join(", ", Accents.Keys)}.");
            accent = d.Accent;
        }

        var logo = (AppSkills.Str(el, "logo") ?? "").Trim();
        if (logo.Length > 0 && !DocumentService.IsLogoName(logo))
        {
            errors.Add("logo must be an uploaded logo (upload a PNG or JPEG on the skill).");
            logo = "";
        }

        var language = (AppSkills.Str(el, "language") ?? "auto").Trim().ToLowerInvariant();
        if (!Languages.Contains(language)) { errors.Add("language must be auto, en or ms."); language = "auto"; }

        DocumentTemplate? template = null;
        if (el.TryGetProperty("template", out var tpl) && tpl.ValueKind != JsonValueKind.Null)
        {
            template = DocumentTemplate.From(tpl, out var tplError);
            if (tplError != null) errors.Add(tplError);
        }

        var layout = (AppSkills.Str(el, "layout") ?? DesignerLayout).Trim().ToLowerInvariant();
        if (layout.Length == 0) layout = DesignerLayout;
        if (!Layouts.Contains(layout)) { errors.Add("layout must be designer or html."); layout = DesignerLayout; }
        DocumentHtmlConfig? html = null;
        if (el.TryGetProperty("html", out var hv) && hv.ValueKind != JsonValueKind.Null)
        {
            html = DocumentHtmlConfig.From(hv, out var htmlError, check);
            if (htmlError != null) errors.Add(htmlError);
        }
        if (layout == HtmlLayout && (html == null || html.Template.Trim().Length == 0))
        {
            errors.Add("layout html needs a template (html.template) — start from a ready-made layout or upload your design.");
            if (html == null) layout = DesignerLayout;
        }

        var taxLabel = T("tax_label", d.TaxLabel, 20);
        var cfg = new DocumentAppConfig
        {
            AllowedActions = AppSkills.Actions(el, AllActions, d.AllowedActions, errors),
            BusinessName = T("business_name", "", 120),
            Address = T("address", "", 500),
            RegistrationNo = T("registration_no", "", 60),
            TaxNo = T("tax_no", "", 60),
            Phone = T("phone", "", 60),
            Email = email,
            Website = T("website", "", 200),
            Currency = AppSkills.Currency(el, d.Currency, errors),
            TaxLabel = taxLabel,
            TaxRate = AppSkills.Dec(el, "tax_rate", 0m, 0m, 100m, "tax_rate", errors),
            PaymentInstructions = T("payment_instructions", "", 1000),
            FooterNote = T("footer_note", d.FooterNote, 500),
            QuotationValidDays = AppSkills.Int(el, "quotation_valid_days", d.QuotationValidDays, 1, 365,
                "quotation_valid_days", errors),
            InvoiceDueDays = AppSkills.Int(el, "invoice_due_days", d.InvoiceDueDays, 0, 365, "invoice_due_days", errors),
            Prefixes = prefixes,
            StartNumbers = starts,
            NumberDigits = AppSkills.Int(el, "number_digits", d.NumberDigits, 1, 10, "number_digits", errors),
            Accent = accent,
            Logo = logo,
            Language = language,
            Template = template,
            Layout = layout,
            Html = html,
        };
        error = errors.FirstOrDefault();
        return cfg;
    }
}
