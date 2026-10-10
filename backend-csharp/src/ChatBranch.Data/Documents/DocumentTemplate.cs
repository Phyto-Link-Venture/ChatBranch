using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChatBranch.Domain;

namespace ChatBranch.Data.Documents;

/// <summary>
/// The look of a Documents skill's PDFs — the <c>template</c> object inside the
/// skill's config (snake_case JSON, <see cref="CurrentVersion"/>-versioned).
/// A preset (classic | modern | minimal | bold) fills in every value the
/// template doesn't give; the rest are explicit choices: page size and
/// margins, fonts (the embedded Liberation Sans / Serif), colours (any hex — a
/// PDF is not themed UI), the header (logo position and size, which business
/// lines, a tagline, a band), the title, how the bill-to and document details
/// are arranged, the items table (columns + labels, zebra rows, borders, header
/// colour), the totals block, the notes / payment terms / bank details, the
/// footer (text, page numbers), the ORDER of the body sections, an uploaded
/// letterhead PDF drawn under every page (or the first only) and per-type
/// overrides (invoice / quotation / receipt / letter).
///
/// <para><see cref="From(JsonElement, out string?)"/> is the ONE normalizer:
/// unknown keys and bad values are refused with the first problem, and
/// <see cref="ToDictionary"/> is the canonical form (every key, defaults
/// filled). A documents config WITHOUT a template renders exactly as before the
/// designer existed — that is <see cref="Classic"/>, the renderer's default.</para>
/// </summary>
public sealed class DocumentTemplate
{
    public const int CurrentVersion = 1;

    public static readonly string[] Presets = { "classic", "modern", "minimal", "bold" };
    public static readonly string[] PageSizes = { "A4", "Letter" };
    public static readonly string[] FontFaces = { "sans", "serif" };
    public static readonly string[] Positions = { "left", "center", "right" };
    public static readonly string[] HeaderLayouts = { "split", "stacked", "centered" };
    public static readonly string[] HeaderStyles = { "plain", "band" };
    public static readonly string[] RuleStyles = { "accent", "line", "none" };
    public static readonly string[] InfoArrangements = { "meta_in_header", "side_by_side", "stacked" };
    public static readonly string[] Columns = { "index", "description", "qty", "unit_price", "discount", "amount" };
    public static readonly string[] TableHeaderStyles = { "filled", "underline", "plain" };
    public static readonly string[] Borders = { "lines", "grid", "outer", "none" };
    public static readonly string[] TotalsPositions = { "right", "left", "full" };
    public static readonly string[] TotalsStyles = { "lines", "boxed", "band" };
    /// <summary>The body sections the renderer can put in any order (a letter uses info + its body at "items").</summary>
    public static readonly string[] SectionIds = { "info", "items", "totals", "notes", "payment", "terms", "bank_details" };
    public static readonly string[] LetterheadPages = { "all", "first" };
    /// <summary>What a per-type override may change (page, preset and letterhead are the whole template's).</summary>
    public static readonly string[] OverrideKeys =
        { "fonts", "colors", "header", "title", "info", "table", "totals", "notes", "footer", "sections" };

    public int Version { get; set; } = CurrentVersion;
    public string Preset { get; set; } = "classic";
    public PageSpec Page { get; set; } = new();
    public FontSpec Fonts { get; set; } = new();
    public ColorSpec Colors { get; set; } = new();
    public HeaderSpec Header { get; set; } = new();
    public TitleSpec Title { get; set; } = new();
    public InfoSpec Info { get; set; } = new();
    public TableSpec Table { get; set; } = new();
    public TotalsSpec Totals { get; set; } = new();
    public NotesSpec Notes { get; set; } = new();
    public FooterSpec Footer { get; set; } = new();
    public List<string> Sections { get; set; } = SectionIds.ToList();
    public LetterheadSpec Letterhead { get; set; } = new();
    /// <summary>Per document type: a PARTIAL template (only <see cref="OverrideKeys"/>), canonical values.</summary>
    public Dictionary<string, Dictionary<string, object?>> Overrides { get; set; } = new();

    public sealed class PageSpec
    {
        public string Size { get; set; } = "A4";
        public double MarginTop { get; set; } = 16;
        public double MarginRight { get; set; } = 18;
        public double MarginBottom { get; set; } = 20;
        public double MarginLeft { get; set; } = 18;
    }

    public sealed class FontSpec
    {
        public string Body { get; set; } = "sans";
        public string Heading { get; set; } = "sans";
        public double Size { get; set; } = 9.5;
    }

    public sealed class ColorSpec
    {
        /// <summary>"" = the config's accent (a tag palette key).</summary>
        public string Accent { get; set; } = "";
        public string Text { get; set; } = "#1F2328";
        public string Muted { get; set; } = "#6B7280";
        public string Line { get; set; } = "#E5E7EB";
    }

    public sealed class HeaderSpec
    {
        public string Layout { get; set; } = "split";
        public string Style { get; set; } = "plain";
        /// <summary>"" = the accent.</summary>
        public string BandColor { get; set; } = "";
        public string BandTextColor { get; set; } = "#FFFFFF";
        public string LogoPosition { get; set; } = "left";
        public double LogoSizeMm { get; set; } = 16;
        public bool ShowBusinessName { get; set; } = true;
        public double BusinessNameSize { get; set; } = 14;
        public bool ShowAddress { get; set; } = true;
        public bool ShowIds { get; set; } = true;
        public bool ShowContacts { get; set; } = true;
        public List<string> ExtraLines { get; set; } = new();
        public string Tagline { get; set; } = "";
        public string Rule { get; set; } = "accent";
    }

    public sealed class TitleSpec
    {
        public bool Show { get; set; } = true;
        public string Align { get; set; } = "right";
        public double Size { get; set; } = 20;
        public bool Uppercase { get; set; } = true;
        /// <summary>"" = the accent (or the band's text colour inside a band).</summary>
        public string Color { get; set; } = "";
    }

    public sealed class InfoSpec
    {
        public string Arrangement { get; set; } = "meta_in_header";
        /// <summary>"" = the language's "BILL TO" / "RECEIVED FROM" / "TO".</summary>
        public string BillToLabel { get; set; } = "";
    }

    public sealed class TableSpec
    {
        public List<string> Columns { get; set; } = DocumentTemplate.Columns.ToList();
        /// <summary>Column id → printed heading ("" / absent = the language's word).</summary>
        public Dictionary<string, string> Labels { get; set; } = new();
        public string HeaderStyle { get; set; } = "filled";
        /// <summary>"" = the accent.</summary>
        public string HeaderColor { get; set; } = "";
        public string HeaderTextColor { get; set; } = "#FFFFFF";
        public bool Zebra { get; set; }
        public string ZebraColor { get; set; } = "#F5F6F8";
        public string Border { get; set; } = "lines";
    }

    public sealed class TotalsSpec
    {
        public string Position { get; set; } = "right";
        public string Style { get; set; } = "lines";
    }

    public sealed class NotesSpec
    {
        public string Terms { get; set; } = "";
        public string BankDetails { get; set; } = "";
        public bool ShowPaymentInstructions { get; set; } = true;
    }

    public sealed class FooterSpec
    {
        /// <summary>"" = the config's footer note.</summary>
        public string Text { get; set; } = "";
        public string Align { get; set; } = "center";
        public bool PageNumbers { get; set; } = true;
        public bool ShowNumber { get; set; } = true;
    }

    public sealed class LetterheadSpec
    {
        /// <summary>An uploaded letterhead PDF's stored name (<see cref="DocumentService.IsLetterheadName"/>), "" = none.</summary>
        public string File { get; set; } = "";
        public string Pages { get; set; } = "all";
    }

    // =========================================================================
    // presets
    // =========================================================================

    /// <summary>The pre-designer layout, value for value.</summary>
    public static DocumentTemplate Classic() => new();

    public static DocumentTemplate ForPreset(string preset)
    {
        var t = new DocumentTemplate { Preset = Presets.Contains(preset) ? preset : "classic" };
        switch (t.Preset)
        {
            case "modern":
                t.Colors.Muted = "#64748B";
                t.Colors.Line = "#E2E8F0";
                t.Header.Rule = "none";
                t.Header.BusinessNameSize = 15;
                t.Title.Uppercase = false;
                t.Title.Size = 22;
                t.Info.Arrangement = "side_by_side";
                t.Table.Zebra = true;
                t.Table.ZebraColor = "#F1F5F9";
                t.Table.Border = "none";
                t.Totals.Style = "band";
                t.Footer.Align = "left";
                break;
            case "minimal":
                t.Fonts.Heading = "serif";
                t.Colors.Muted = "#737373";
                t.Colors.Line = "#E5E5E5";
                t.Header.Rule = "line";
                t.Header.LogoSizeMm = 12;
                t.Header.BusinessNameSize = 13;
                t.Title.Size = 16;
                t.Title.Color = "#1F2328";
                t.Info.Arrangement = "side_by_side";
                t.Table.HeaderStyle = "underline";
                t.Table.HeaderColor = "#1F2328";
                break;
            case "bold":
                t.Header.Style = "band";
                t.Header.Rule = "none";
                t.Header.BusinessNameSize = 16;
                t.Title.Size = 24;
                t.Info.Arrangement = "side_by_side";
                t.Table.Border = "grid";
                t.Totals.Style = "boxed";
                break;
        }
        return t;
    }

    /// <summary>The four presets' canonical templates (the designer's "Start from…" gallery).</summary>
    public static Dictionary<string, Dictionary<string, object?>> PresetCatalog() =>
        Presets.ToDictionary(p => p, p => ForPreset(p).ToDictionary());

    /// <summary>
    /// A preset's values with this template's WORDS kept (header lines, tagline,
    /// labels, terms, bank details, footer text, letterhead, page size): what
    /// "make it minimal" means.
    /// </summary>
    public DocumentTemplate WithPreset(string preset)
    {
        var t = ForPreset(preset);
        t.Page = Clone(Page);
        t.Header.ExtraLines = Header.ExtraLines.ToList();
        t.Header.Tagline = Header.Tagline;
        t.Header.LogoPosition = Header.LogoPosition;
        t.Table.Columns = Table.Columns.ToList();
        t.Table.Labels = new(Table.Labels);
        t.Notes = Clone(Notes);
        t.Footer.Text = Footer.Text;
        t.Footer.PageNumbers = Footer.PageNumbers;
        t.Sections = Sections.ToList();
        t.Letterhead = Clone(Letterhead);
        if (Colors.Accent.Length > 0) t.Colors.Accent = Colors.Accent;
        return t;
    }

    private static T Clone<T>(T o) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(o))!;

    public DocumentTemplate Copy() => From(JsonSerializer.SerializeToElement(ToDictionary()), out _)!;

    // =========================================================================
    // per type
    // =========================================================================

    /// <summary>The template a document of <paramref name="type"/> prints with (its override merged in).</summary>
    public DocumentTemplate ForType(string type)
    {
        if (!Overrides.TryGetValue(type, out var ov) || ov.Count == 0) return this;
        var merged = Merge(JsonSerializer.SerializeToNode(ToDictionary())!.AsObject(), JsonSerializer.SerializeToNode(ov)!.AsObject());
        merged.Remove("overrides");
        return From(JsonSerializer.SerializeToElement(merged), out _) ?? this;
    }

    private static JsonObject Merge(JsonObject baseObj, JsonObject over)
    {
        foreach (var (k, v) in over)
        {
            if (v is JsonObject vo && baseObj[k] is JsonObject bo) Merge(bo, vo);
            else baseObj[k] = v?.DeepClone();
        }
        return baseObj;
    }

    // =========================================================================
    // wire
    // =========================================================================

    public Dictionary<string, object?> ToDictionary(bool withOverrides = true)
    {
        var d = new Dictionary<string, object?>
        {
            ["version"] = Version,
            ["preset"] = Preset,
            ["page"] = new Dictionary<string, object?>
            {
                ["size"] = Page.Size,
                ["margins_mm"] = new Dictionary<string, object?>
                {
                    ["top"] = Page.MarginTop, ["right"] = Page.MarginRight,
                    ["bottom"] = Page.MarginBottom, ["left"] = Page.MarginLeft,
                },
            },
            ["fonts"] = new Dictionary<string, object?> { ["body"] = Fonts.Body, ["heading"] = Fonts.Heading, ["size"] = Fonts.Size },
            ["colors"] = new Dictionary<string, object?>
            {
                ["accent"] = Colors.Accent, ["text"] = Colors.Text, ["muted"] = Colors.Muted, ["line"] = Colors.Line,
            },
            ["header"] = new Dictionary<string, object?>
            {
                ["layout"] = Header.Layout, ["style"] = Header.Style, ["band_color"] = Header.BandColor,
                ["band_text_color"] = Header.BandTextColor, ["logo_position"] = Header.LogoPosition,
                ["logo_size_mm"] = Header.LogoSizeMm, ["show_business_name"] = Header.ShowBusinessName,
                ["business_name_size"] = Header.BusinessNameSize, ["show_address"] = Header.ShowAddress,
                ["show_ids"] = Header.ShowIds, ["show_contacts"] = Header.ShowContacts,
                ["extra_lines"] = Header.ExtraLines.ToList(), ["tagline"] = Header.Tagline, ["rule"] = Header.Rule,
            },
            ["title"] = new Dictionary<string, object?>
            {
                ["show"] = Title.Show, ["align"] = Title.Align, ["size"] = Title.Size,
                ["uppercase"] = Title.Uppercase, ["color"] = Title.Color,
            },
            ["info"] = new Dictionary<string, object?> { ["arrangement"] = Info.Arrangement, ["bill_to_label"] = Info.BillToLabel },
            ["table"] = new Dictionary<string, object?>
            {
                ["columns"] = Table.Columns.ToList(),
                ["labels"] = Columns.Where(c => Table.Labels.ContainsKey(c)).ToDictionary(c => c, c => (object?)Table.Labels[c]),
                ["header_style"] = Table.HeaderStyle, ["header_color"] = Table.HeaderColor,
                ["header_text_color"] = Table.HeaderTextColor, ["zebra"] = Table.Zebra,
                ["zebra_color"] = Table.ZebraColor, ["border"] = Table.Border,
            },
            ["totals"] = new Dictionary<string, object?> { ["position"] = Totals.Position, ["style"] = Totals.Style },
            ["notes"] = new Dictionary<string, object?>
            {
                ["terms"] = Notes.Terms, ["bank_details"] = Notes.BankDetails,
                ["show_payment_instructions"] = Notes.ShowPaymentInstructions,
            },
            ["footer"] = new Dictionary<string, object?>
            {
                ["text"] = Footer.Text, ["align"] = Footer.Align, ["page_numbers"] = Footer.PageNumbers,
                ["show_number"] = Footer.ShowNumber,
            },
            ["sections"] = Sections.ToList(),
            ["letterhead"] = new Dictionary<string, object?> { ["file"] = Letterhead.File, ["pages"] = Letterhead.Pages },
        };
        if (withOverrides)
            d["overrides"] = DocumentTypes.Numbered.Where(Overrides.ContainsKey)
                .ToDictionary(k => k, k => (object?)Overrides[k]);
        return d;
    }

    /// <summary>
    /// Validate + normalize a wire template. Returns null only when
    /// <paramref name="el"/> isn't an object; <paramref name="error"/> is the
    /// first problem (null = valid). Absent keys take the preset's value.
    /// </summary>
    public static DocumentTemplate? From(JsonElement el, out string? error)
    {
        var errors = new List<string>();
        var t = Read(el, "template", errors, allowOverrides: true);
        error = errors.FirstOrDefault();
        return t;
    }

    private static DocumentTemplate? Read(JsonElement el, string path, List<string> errors, bool allowOverrides)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path} must be a JSON object.");
            return null;
        }
        var root = new Obj(el, path, errors, "version", "preset", "page", "fonts", "colors", "header", "title", "info",
            "table", "totals", "notes", "footer", "sections", "letterhead", "overrides");
        var version = root.Int("version", CurrentVersion, 1, CurrentVersion);
        var t = ForPreset(root.Enum("preset", Presets, "classic"));
        t.Version = version;

        if (root.Child("page", "size", "margins_mm") is { } page)
        {
            t.Page.Size = page.Enum("size", PageSizes, t.Page.Size, ignoreCase: true);
            if (page.Child("margins_mm", "top", "right", "bottom", "left") is { } m)
            {
                t.Page.MarginTop = m.Num("top", t.Page.MarginTop, 5, 60);
                t.Page.MarginRight = m.Num("right", t.Page.MarginRight, 5, 60);
                t.Page.MarginBottom = m.Num("bottom", t.Page.MarginBottom, 10, 60);
                t.Page.MarginLeft = m.Num("left", t.Page.MarginLeft, 5, 60);
            }
        }
        if (root.Child("fonts", "body", "heading", "size") is { } f)
        {
            t.Fonts.Body = f.Enum("body", FontFaces, t.Fonts.Body);
            t.Fonts.Heading = f.Enum("heading", FontFaces, t.Fonts.Heading);
            t.Fonts.Size = f.Num("size", t.Fonts.Size, 7, 13);
        }
        if (root.Child("colors", "accent", "text", "muted", "line") is { } c)
        {
            t.Colors.Accent = c.Color("accent", t.Colors.Accent, allowEmpty: true);
            t.Colors.Text = c.Color("text", t.Colors.Text);
            t.Colors.Muted = c.Color("muted", t.Colors.Muted);
            t.Colors.Line = c.Color("line", t.Colors.Line);
        }
        if (root.Child("header", "layout", "style", "band_color", "band_text_color", "logo_position", "logo_size_mm",
                "show_business_name", "business_name_size", "show_address", "show_ids", "show_contacts", "extra_lines",
                "tagline", "rule") is { } h)
        {
            t.Header.Layout = h.Enum("layout", HeaderLayouts, t.Header.Layout);
            t.Header.Style = h.Enum("style", HeaderStyles, t.Header.Style);
            t.Header.BandColor = h.Color("band_color", t.Header.BandColor, allowEmpty: true);
            t.Header.BandTextColor = h.Color("band_text_color", t.Header.BandTextColor);
            t.Header.LogoPosition = h.Enum("logo_position", Positions, t.Header.LogoPosition);
            t.Header.LogoSizeMm = h.Num("logo_size_mm", t.Header.LogoSizeMm, 6, 50);
            t.Header.ShowBusinessName = h.Bool("show_business_name", t.Header.ShowBusinessName);
            t.Header.BusinessNameSize = h.Num("business_name_size", t.Header.BusinessNameSize, 8, 32);
            t.Header.ShowAddress = h.Bool("show_address", t.Header.ShowAddress);
            t.Header.ShowIds = h.Bool("show_ids", t.Header.ShowIds);
            t.Header.ShowContacts = h.Bool("show_contacts", t.Header.ShowContacts);
            t.Header.ExtraLines = h.Lines("extra_lines", t.Header.ExtraLines, 6, 200);
            t.Header.Tagline = h.Text("tagline", t.Header.Tagline, 160);
            t.Header.Rule = h.Enum("rule", RuleStyles, t.Header.Rule);
        }
        if (root.Child("title", "show", "align", "size", "uppercase", "color") is { } ti)
        {
            t.Title.Show = ti.Bool("show", t.Title.Show);
            t.Title.Align = ti.Enum("align", Positions, t.Title.Align);
            t.Title.Size = ti.Num("size", t.Title.Size, 10, 40);
            t.Title.Uppercase = ti.Bool("uppercase", t.Title.Uppercase);
            t.Title.Color = ti.Color("color", t.Title.Color, allowEmpty: true);
        }
        if (root.Child("info", "arrangement", "bill_to_label") is { } inf)
        {
            t.Info.Arrangement = inf.Enum("arrangement", InfoArrangements, t.Info.Arrangement);
            t.Info.BillToLabel = inf.Text("bill_to_label", t.Info.BillToLabel, 40);
        }
        if (root.Child("table", "columns", "labels", "header_style", "header_color", "header_text_color", "zebra",
                "zebra_color", "border") is { } tb)
        {
            var cols = tb.Ids("columns", Columns, t.Table.Columns);
            if (!cols.Contains("description") || !cols.Contains("amount"))
            {
                errors.Add($"{tb.Path}.columns must include description and amount.");
                cols = t.Table.Columns;
            }
            t.Table.Columns = Columns.Where(cols.Contains).ToList(); // the printed order is fixed
            if (tb.Child("labels", Columns) is { } lb)
            {
                var labels = new Dictionary<string, string>();
                foreach (var col in Columns)
                {
                    var v = lb.Text(col, "", 40);
                    if (v.Length > 0) labels[col] = v;
                }
                t.Table.Labels = labels;
            }
            t.Table.HeaderStyle = tb.Enum("header_style", TableHeaderStyles, t.Table.HeaderStyle);
            t.Table.HeaderColor = tb.Color("header_color", t.Table.HeaderColor, allowEmpty: true);
            t.Table.HeaderTextColor = tb.Color("header_text_color", t.Table.HeaderTextColor);
            t.Table.Zebra = tb.Bool("zebra", t.Table.Zebra);
            t.Table.ZebraColor = tb.Color("zebra_color", t.Table.ZebraColor);
            t.Table.Border = tb.Enum("border", Borders, t.Table.Border);
        }
        if (root.Child("totals", "position", "style") is { } to)
        {
            t.Totals.Position = to.Enum("position", TotalsPositions, t.Totals.Position);
            t.Totals.Style = to.Enum("style", TotalsStyles, t.Totals.Style);
        }
        if (root.Child("notes", "terms", "bank_details", "show_payment_instructions") is { } n)
        {
            t.Notes.Terms = n.Text("terms", t.Notes.Terms, 1000);
            t.Notes.BankDetails = n.Text("bank_details", t.Notes.BankDetails, 1000);
            t.Notes.ShowPaymentInstructions = n.Bool("show_payment_instructions", t.Notes.ShowPaymentInstructions);
        }
        if (root.Child("footer", "text", "align", "page_numbers", "show_number") is { } ft)
        {
            t.Footer.Text = ft.Text("text", t.Footer.Text, 300);
            t.Footer.Align = ft.Enum("align", Positions, t.Footer.Align);
            t.Footer.PageNumbers = ft.Bool("page_numbers", t.Footer.PageNumbers);
            t.Footer.ShowNumber = ft.Bool("show_number", t.Footer.ShowNumber);
        }
        if (root.Has("sections"))
        {
            var order = root.Ids("sections", SectionIds, t.Sections);
            // a section left out keeps its place at the end — the order list never hides a block
            t.Sections = order.Concat(SectionIds.Where(s => !order.Contains(s))).ToList();
        }
        if (root.Child("letterhead", "file", "pages") is { } lh)
        {
            var file = lh.Text("file", "", 64);
            if (file.Length > 0 && !DocumentService.IsLetterheadName(file))
            {
                errors.Add($"{lh.Path}.file must be an uploaded letterhead PDF.");
                file = "";
            }
            t.Letterhead.File = file;
            t.Letterhead.Pages = lh.Enum("pages", LetterheadPages, t.Letterhead.Pages);
        }

        if (root.Has("overrides"))
        {
            if (!allowOverrides) errors.Add($"{path}.overrides can't be nested.");
            else if (root.Child("overrides", DocumentTypes.Numbered) is { } ov)
            {
                var baseNode = JsonSerializer.SerializeToNode(t.ToDictionary(withOverrides: false))!.AsObject();
                foreach (var type in DocumentTypes.Numbered)
                {
                    if (!ov.El.TryGetProperty(type, out var part) || part.ValueKind == JsonValueKind.Null) continue;
                    var p = $"{ov.Path}.{type}";
                    if (part.ValueKind != JsonValueKind.Object) { errors.Add($"{p} must be a JSON object."); continue; }
                    var bad = part.EnumerateObject().Select(x => x.Name).Where(k => !OverrideKeys.Contains(k)).ToList();
                    if (bad.Count > 0)
                    {
                        errors.Add($"{p} can't change '{bad[0]}' (an override may set {string.Join(", ", OverrideKeys)}).");
                        continue;
                    }
                    if (!part.EnumerateObject().Any()) continue;
                    var merged = Merge(baseNode.DeepClone().AsObject(), JsonNode.Parse(part.GetRawText())!.AsObject());
                    var before = errors.Count;
                    var resolved = Read(JsonSerializer.SerializeToElement(merged), p, errors, allowOverrides: false);
                    if (resolved == null || errors.Count > before) continue;
                    // keep only what the override names, in canonical form
                    var canon = JsonSerializer.SerializeToNode(resolved.ToDictionary(withOverrides: false))!.AsObject();
                    t.Overrides[type] = Project(part, canon);
                }
            }
        }
        return t;
    }

    /// <summary>The canonical values of exactly the keys <paramref name="shape"/> names.</summary>
    private static Dictionary<string, object?> Project(JsonElement shape, JsonObject canon)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in shape.EnumerateObject())
        {
            var v = canon[p.Name];
            if (p.Value.ValueKind == JsonValueKind.Object && v is JsonObject vo
                && p.Name is not ("labels")) d[p.Name] = Project(p.Value, vo);
            else d[p.Name] = v == null ? null : JsonSerializer.Deserialize<JsonElement>(v.ToJsonString());
        }
        return d;
    }

    // ---- strict reader -------------------------------------------------------------

    /// <summary>One JSON object of the template: unknown keys are refused, values checked.</summary>
    private sealed class Obj
    {
        public JsonElement El { get; }
        public string Path { get; }
        private readonly List<string> _errors;

        public Obj(JsonElement el, string path, List<string> errors, params string[] known)
        {
            El = el;
            Path = path;
            _errors = errors;
            foreach (var p in el.EnumerateObject())
                if (!known.Contains(p.Name))
                    _errors.Add($"unknown key '{path}.{p.Name}'.");
        }

        private bool Get(string key, out JsonElement v) =>
            El.TryGetProperty(key, out v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

        public bool Has(string key) => Get(key, out _);

        public Obj? Child(string key, params string[] known)
        {
            if (!Get(key, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Object) { _errors.Add($"{Path}.{key} must be a JSON object."); return null; }
            return new Obj(v, $"{Path}.{key}", _errors, known);
        }

        public string Enum(string key, string[] allowed, string dflt, bool ignoreCase = false)
        {
            if (!Get(key, out var v)) return dflt;
            var s = (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).Trim();
            var hit = allowed.FirstOrDefault(a => string.Equals(a, s,
                ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                ?? allowed.FirstOrDefault(a => string.Equals(a, s.ToLowerInvariant(), StringComparison.Ordinal));
            if (hit != null) return hit;
            _errors.Add($"{Path}.{key} must be one of {string.Join(", ", allowed)}.");
            return dflt;
        }

        public bool Bool(string key, bool dflt)
        {
            if (!Get(key, out var v)) return dflt;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            _errors.Add($"{Path}.{key} must be true or false.");
            return dflt;
        }

        public double Num(string key, double dflt, double min, double max)
        {
            if (!Get(key, out var v)) return dflt;
            double n;
            if (v.ValueKind == JsonValueKind.Number) n = v.GetDouble();
            else if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float,
                         CultureInfo.InvariantCulture, out var s)) n = s;
            else { _errors.Add($"{Path}.{key} must be a number."); return dflt; }
            if (double.IsNaN(n) || n < min || n > max)
            {
                _errors.Add($"{Path}.{key} must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
                return dflt;
            }
            return Math.Round(n, 1);
        }

        public int Int(string key, int dflt, int min, int max)
        {
            if (!Get(key, out var v)) return dflt;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= min && n <= max) return n;
            _errors.Add($"{Path}.{key} must be {(min == max ? min.ToString(CultureInfo.InvariantCulture) : $"a whole number from {min} to {max}")}.");
            return dflt;
        }

        public string Text(string key, string dflt, int max)
        {
            if (!Get(key, out var v)) return dflt;
            if (v.ValueKind != JsonValueKind.String) { _errors.Add($"{Path}.{key} must be text."); return dflt; }
            var s = (v.GetString() ?? "").Trim();
            if (s.Length > max) { _errors.Add($"{Path}.{key} is longer than {max:N0} characters."); return s[..max]; }
            return s;
        }

        public List<string> Lines(string key, List<string> dflt, int maxItems, int maxLen)
        {
            if (!Get(key, out var v)) return dflt;
            if (v.ValueKind != JsonValueKind.Array) { _errors.Add($"{Path}.{key} must be a list of text lines."); return dflt; }
            var list = new List<string>();
            foreach (var x in v.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.String) { _errors.Add($"{Path}.{key} must be a list of text lines."); return dflt; }
                var s = (x.GetString() ?? "").Trim();
                if (s.Length == 0) continue;
                if (s.Length > maxLen) { _errors.Add($"{Path}.{key}: a line is longer than {maxLen} characters."); return dflt; }
                list.Add(s);
            }
            if (list.Count > maxItems) { _errors.Add($"{Path}.{key} may hold up to {maxItems} lines."); return dflt; }
            return list;
        }

        public List<string> Ids(string key, string[] known, List<string> dflt)
        {
            if (!Get(key, out var v)) return dflt;
            if (v.ValueKind != JsonValueKind.Array) { _errors.Add($"{Path}.{key} must be a list."); return dflt; }
            var list = new List<string>();
            foreach (var x in v.EnumerateArray())
            {
                var s = (x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).Trim().ToLowerInvariant();
                if (!known.Contains(s)) { _errors.Add($"{Path}.{key}: unknown '{s}' (use {string.Join(", ", known)})."); return dflt; }
                if (!list.Contains(s)) list.Add(s);
            }
            return list;
        }

        public string Color(string key, string dflt, bool allowEmpty = false)
        {
            if (!Get(key, out var v)) return dflt;
            var s = (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "").Trim();
            if (s.Length == 0 && allowEmpty) return "";
            if (NormalizeHex(s) is { } hex) return hex;
            _errors.Add($"{Path}.{key} must be a hex colour like #1D4ED8{(allowEmpty ? " (or empty for the accent)" : "")}.");
            return dflt;
        }
    }

    /// <summary>"#abc" / "1d4ed8" / "#1D4ED8" → "#1D4ED8"; anything else null.</summary>
    public static string? NormalizeHex(string? s)
    {
        s = (s ?? "").Trim().TrimStart('#');
        if (Regex.IsMatch(s, "^[0-9a-fA-F]{3}$")) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        return Regex.IsMatch(s, "^[0-9a-fA-F]{6}$") ? "#" + s.ToUpperInvariant() : null;
    }
}
