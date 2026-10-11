using System.Globalization;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;

namespace ChatBranch.Data.Documents;

/// <summary>
/// The data a custom layout's template sees — built in ONE place (<see cref="Build"/>)
/// and described in ONE place (<see cref="Reference"/>, which the editor's reference
/// panel and the AI's prompt both show):
/// <code>
/// business  {name, address, address_lines[], registration_no, tax_no, phone, email, website,
///            logo (a data: URL or ""), bank_details, payment_instructions, footer_note, accent}
/// document  {type, type_label, number, title, reference, issue_date, due_date, valid_until,
///            currency, language, notes, payment_method, paragraphs[] (letters), closing}
/// customer  {name, company, address, address_lines[], email, phone}
/// items[]   {index (1, 2, …), description, description_first_line, description_rest[], qty,
///            unit_price, discount, discount_label, amount + qty_text, unit_price_text, discount_text, amount_text}
/// totals    {subtotal, discount, tax, tax_label, tax_rate, total + *_text, total_in_words_en,
///            total_in_words_ms, total_in_words (the document's language)}
/// fields    {the custom fields}
/// terms[]   (the layout's terms, one per line)
/// page      {size, margin_mm {top, right, bottom, left}}
/// </code>
/// Dates are ISO (YYYY-MM-DD) — format them with the <c>date</c> helper; numbers are
/// raw decimals next to pre-formatted <c>*_text</c> strings (13,256.00).
/// </summary>
public static class DocumentHtmlModel
{
    public static Dictionary<string, object?> Build(DocumentContent c, DocumentAppConfig cfg, byte[]? logo)
    {
        var html = cfg.Html ?? new DocumentHtmlConfig();
        var designer = cfg.Template;
        var bank = html.BankDetails.Length > 0 ? html.BankDetails : designer?.Notes.BankDetails ?? "";
        var terms = html.Terms.Length > 0 ? html.Terms : designer?.Notes.Terms ?? "";
        string Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        var L = DocumentRenderer.Labels.For(c.Language);
        var taxLabel = cfg.TaxLabel.Length > 0 ? cfg.TaxLabel : L.Tax;

        var fields = new Dictionary<string, object?>();
        foreach (var f in html.Fields)
            fields[f.Key] = c.Fields.TryGetValue(f.Key, out var v) ? v : f.Default;
        foreach (var (k, v) in c.Fields) fields.TryAdd(k, v);

        var n = 0;
        return new Dictionary<string, object?>
        {
            ["business"] = new Dictionary<string, object?>
            {
                ["name"] = cfg.BusinessName,
                ["address"] = cfg.Address,
                ["address_lines"] = Lines(cfg.Address),
                ["registration_no"] = cfg.RegistrationNo,
                ["tax_no"] = cfg.TaxNo,
                ["phone"] = cfg.Phone,
                ["email"] = cfg.Email,
                ["website"] = cfg.Website,
                ["logo"] = LogoDataUrl(logo),
                ["bank_details"] = bank,
                ["payment_instructions"] = cfg.PaymentInstructions,
                ["footer_note"] = cfg.FooterNote,
                ["accent"] = designer?.Colors.Accent is { Length: > 0 } a ? a : cfg.AccentHex,
            },
            ["document"] = new Dictionary<string, object?>
            {
                ["type"] = c.Type,
                ["type_label"] = L.Title(c.Type),
                ["number"] = c.Number,
                ["title"] = c.Title,
                ["reference"] = c.Reference,
                ["issue_date"] = Iso(c.IssueDate),
                ["due_date"] = Iso(c.DueDate),
                ["valid_until"] = Iso(c.ValidUntil),
                ["currency"] = c.Currency,
                ["currency_symbol"] = CurrencySymbol(c.Currency),
                ["language"] = c.Language,
                ["notes"] = c.Notes,
                ["payment_method"] = c.PaymentMethod,
                ["paragraphs"] = c.Paragraphs.ToList(),
                ["closing"] = c.Closing.Length > 0 ? c.Closing : c.Type == DocumentTypes.Letter ? L.Sincerely : "",
            },
            ["customer"] = new Dictionary<string, object?>
            {
                ["name"] = c.Customer.Name,
                ["company"] = c.Customer.Company,
                ["address"] = c.Customer.Address,
                ["address_lines"] = Lines(c.Customer.Address),
                ["email"] = c.Customer.Email,
                ["phone"] = c.Customer.Phone,
            },
            ["items"] = c.Lines.Select(l => new Dictionary<string, object?>
            {
                ["index"] = ++n,
                ["description"] = l.Description,
                ["description_first_line"] = DocumentHtmlTemplating.FirstLine(l.Description),
                ["description_rest"] = DocumentHtmlTemplating.RestLines(l.Description),
                ["qty"] = l.Qty,
                ["unit_price"] = l.UnitPrice,
                ["discount"] = l.Discount,
                ["discount_label"] = l.DiscountLabel,
                ["amount"] = l.Amount,
                ["qty_text"] = DocumentMath.Qty(l.Qty),
                ["unit_price_text"] = DocumentMath.Money(l.UnitPrice),
                ["discount_text"] = l.Discount > 0 ? DocumentMath.Money(l.Discount) : "",
                ["amount_text"] = DocumentMath.Money(l.Amount),
            }).ToList(),
            ["totals"] = new Dictionary<string, object?>
            {
                ["subtotal"] = c.Totals.Subtotal,
                ["discount"] = c.Totals.Discount,
                ["tax"] = c.Totals.Tax,
                ["tax_label"] = taxLabel,
                ["tax_rate"] = cfg.TaxRate,
                ["total"] = c.Totals.Total,
                ["subtotal_text"] = DocumentMath.Money(c.Totals.Subtotal),
                ["discount_text"] = DocumentMath.Money(c.Totals.Discount),
                ["tax_text"] = DocumentMath.Money(c.Totals.Tax),
                ["tax_rate_text"] = DocumentMath.Qty(cfg.TaxRate),
                ["total_text"] = DocumentMath.Money(c.Totals.Total),
                ["total_in_words_en"] = DocumentHtmlTemplating.Words(c.Totals.Total, "en"),
                ["total_in_words_ms"] = DocumentHtmlTemplating.Words(c.Totals.Total, "ms"),
                ["total_in_words"] = DocumentHtmlTemplating.Words(c.Totals.Total, c.Language),
            },
            ["fields"] = fields,
            ["terms"] = Lines(terms),
            ["page"] = new Dictionary<string, object?>
            {
                ["size"] = html.PageSize,
                ["margin_mm"] = new Dictionary<string, object?>
                {
                    ["top"] = html.MarginTop, ["right"] = html.MarginRight,
                    ["bottom"] = html.MarginBottom, ["left"] = html.MarginLeft,
                },
            },
        };
    }

    /// <summary>How a currency is written on a document: MYR → RM, SGD → S$, … (else the code itself).</summary>
    public static string CurrencySymbol(string? code) => (code ?? "").ToUpperInvariant() switch
    {
        "MYR" => "RM", "SGD" => "S$", "USD" => "US$", "IDR" => "Rp", "THB" => "฿", "PHP" => "₱", "GBP" => "£",
        "EUR" => "€", "AUD" => "A$", "BND" => "B$", "CNY" => "RMB", "HKD" => "HK$", "INR" => "₹", "JPY" => "¥",
        var other => other,
    };

    private static List<string> Lines(string? s) =>
        (s ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>A logo as a data: URL (PNG / JPEG by magic bytes), "" when there is none.</summary>
    public static string LogoDataUrl(byte[]? logo)
    {
        if (logo is not { Length: > 3 }) return "";
        var mime = logo[0] == 0x89 && logo[1] == 0x50 ? "image/png" : logo[0] == 0xFF && logo[1] == 0xD8 ? "image/jpeg" : null;
        return mime == null ? "" : $"data:{mime};base64,{Convert.ToBase64String(logo)}";
    }

    // =========================================================================
    // sample data (previews, validation, the AI's checks)
    // =========================================================================

    /// <summary>The tool / form arguments of a sample document (the same sample the designer's preview prints).</summary>
    public static JsonElement SampleArgs(string type, DocumentHtmlConfig? html)
    {
        var fields = new Dictionary<string, object?>();
        foreach (var f in html?.Fields ?? new())
            fields[f.Key] = f.Default.Length > 0 ? f.Default : SampleValue(f);
        return type == DocumentTypes.Letter
            ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["customer_name"] = "Aisha Rahman", ["title"] = "Confirmation of your appointment",
                ["recipient_address"] = "12 Jalan Bunga\n50450 Kuala Lumpur",
                ["paragraphs"] = new[]
                {
                    "Thank you for choosing us. This letter confirms your appointment on Monday at 10:00.",
                    "Please arrive ten minutes early and bring this letter with you. Reply to this message if you need to change the time.",
                },
                ["fields"] = fields,
            })
            : JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["customer_name"] = "Aisha Rahman", ["customer_company"] = "Example Trading Sdn Bhd",
                ["customer_address"] = "12 Jalan Bunga\n50450 Kuala Lumpur",
                ["customer_email"] = "aisha@example.com", ["customer_phone"] = "+60 12-345 6789",
                ["title"] = "Sample project",
                ["reference"] = "PO-1001",
                ["items"] = new object[]
                {
                    new Dictionary<string, object?> { ["description"] = "Sample service\nLabour and materials, 2 visits", ["qty"] = 2, ["unit_price"] = 50 },
                    new Dictionary<string, object?> { ["description"] = "Sample product", ["qty"] = 1, ["unit_price"] = 120, ["discount"] = "10%" },
                },
                ["notes"] = "This is a sample — no number was used.",
                ["payment_method"] = type == DocumentTypes.Receipt ? "Bank transfer" : null,
                ["fields"] = fields,
            });
    }

    private static string SampleValue(DocField f) => f.Type switch
    {
        "date" => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "number" => "1",
        _ => $"Sample {f.Label}",
    };

    // =========================================================================
    // the reference (the editor's panel + the AI prompt)
    // =========================================================================

    public sealed record Var(string Path, string Example, string Description);

    public sealed record VarGroup(string Id, List<Var> Vars);

    /// <summary>Every variable a template can print, grouped, with an example value.</summary>
    public static List<VarGroup> Reference(DocumentHtmlConfig? html = null)
    {
        var groups = new List<VarGroup>
        {
            new("business", new()
            {
                new("business.name", "Canmos Lighting", "The business name."),
                new("business.address", "No. 22, Jalan Perkasa 2…", "The address (several lines)."),
                new("business.address_lines", "[\"No. 22, Jalan Perkasa 2\", …]", "The address, one entry per line — {{#each business.address_lines}}{{this}}<br>{{/each}}."),
                new("business.registration_no", "202601234567", "Company registration number."),
                new("business.tax_no", "W10-1234", "Tax (SST) number."),
                new("business.phone", "6016 - 205 3977", "Phone."),
                new("business.email", "hello@example.com", "Email."),
                new("business.website", "example.com", "Website."),
                new("business.logo", "data:image/png;base64,…", "The uploaded logo as a data URL (\"\" when none) — the ONE value printed raw: <img src=\"{{{business.logo}}}\">."),
                new("business.bank_details", "Hong Leong Bank Account No : HLB 152 010 033 51", "Bank details (this layout's, set under Words)."),
                new("business.payment_instructions", "Pay within 14 days", "The skill's payment instructions."),
                new("business.footer_note", "Thank you for your business.", "The skill's footer note."),
                new("business.accent", "#1F2328", "The accent colour (hex)."),
            }),
            new("document", new()
            {
                new("document.type", "invoice", "invoice | quotation | receipt | letter."),
                new("document.type_label", "Invoice", "The type in the document's language (Invoice / Invois)."),
                new("document.number", "CL-261001", "The running number."),
                new("document.title", "40% Balance Claim - Electrical Services", "Subject / heading (a letter's subject line)."),
                new("document.reference", "PO-1001", "Order / PO / payment reference."),
                new("document.issue_date", "2026-10-08", "Issue date (ISO) — {{date document.issue_date \"dd.MM.yyyy\"}}."),
                new("document.due_date", "2026-10-22", "Invoice due date (ISO, \"\" if none)."),
                new("document.valid_until", "2026-10-22", "Quotation valid until (ISO, \"\" if none)."),
                new("document.currency", "MYR", "Currency code."),
                new("document.currency_symbol", "RM", "How the currency is written (MYR → RM, SGD → S$…)."),
                new("document.language", "en", "en | ms — what label / words / date print in."),
                new("document.notes", "Thank you.", "A note written for this document."),
                new("document.payment_method", "Bank transfer", "Receipts: how it was paid."),
                new("document.paragraphs", "[\"We confirm…\", …]", "Letters: the body, one entry per paragraph."),
                new("document.closing", "Yours sincerely,", "Letters: the sign-off."),
            }),
            new("customer", new()
            {
                new("customer.name", "Sentul Perdana Sdn Bhd", "Who it is for."),
                new("customer.company", "Sentul Perdana Sdn Bhd", "Their company."),
                new("customer.address", "1 Jalan Satu…", "Their address (several lines)."),
                new("customer.address_lines", "[\"1 Jalan Satu\", …]", "Their address, one entry per line."),
                new("customer.email", "ahmad@example.com", "Email."),
                new("customer.phone", "+60 12-345 6789", "Phone."),
            }),
            new("items", new()
            {
                new("items", "{{#each items}}…{{/each}}", "The priced lines (not on letters)."),
                new("index", "1", "Inside {{#each items}}: the line number (1, 2, …)."),
                new("description", "40% BALANCE CLAIM…", "The whole description (may hold line breaks — {{nl2br description}})."),
                new("description_first_line", "40% BALANCE CLAIM ON SUPPLY, DELIVERY,", "Its first line."),
                new("description_rest", "[\"INSTALLATION, TESTING…\", …]", "The lines after the first."),
                new("qty", "4", "Quantity (number); qty_text = \"4\"."),
                new("unit_price", "3314.00", "Unit price (number); unit_price_text = \"3,314.00\"."),
                new("discount", "0", "Discount amount (number); discount_text, discount_label (\"10%\")."),
                new("amount", "13256.00", "Line amount (number); amount_text = \"13,256.00\"."),
            }),
            new("totals", new()
            {
                new("totals.subtotal", "13256.00", "Subtotal (number); totals.subtotal_text = \"13,256.00\"."),
                new("totals.discount", "0.00", "Discounts included (number); totals.discount_text."),
                new("totals.tax", "0.00", "Tax (number); totals.tax_text."),
                new("totals.tax_label", "SST", "The tax's name."),
                new("totals.tax_rate", "0", "Tax rate in % (number); totals.tax_rate_text."),
                new("totals.total", "13256.00", "Total (number); totals.total_text = \"13,256.00\"."),
                new("totals.total_in_words_en", "Thirteen Thousand Two Hundred And Fifty Six Only", "The total in English words."),
                new("totals.total_in_words_ms", "Tiga Belas Ribu Dua Ratus Lima Puluh Enam Sahaja", "The total in Malay words."),
                new("totals.total_in_words", "Thirteen Thousand Two Hundred And Fifty Six Only", "The total in the document's language."),
            }),
            new("other", new()
            {
                new("terms", "{{#each terms}}<li>{{this}}</li>{{/each}}", "This layout's terms, one entry per line."),
                new("page.size", "A4", "The page size (A4 | Letter); page.margin_mm.top … — the default @page."),
            }),
        };
        if (html is { Fields.Count: > 0 })
            groups.Add(new VarGroup("fields", html.Fields.Select(f =>
                new Var($"fields.{f.Key}", f.Default.Length > 0 ? f.Default : $"Sample {f.Label}", f.Label)).ToList()));
        else
            groups.Add(new VarGroup("fields", new() { new("fields.project", "SENTUL PERDANA SDN BHD", "A custom field (add them under Fields).") }));
        return groups;
    }

    /// <summary>The data model + helpers as plain text (the AI's system prompt).</summary>
    public static string ReferenceText(DocumentHtmlConfig? html = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DATA MODEL (Handlebars variables):");
        foreach (var g in Reference(html))
        {
            sb.AppendLine($"- {g.Id}:");
            foreach (var v in g.Vars) sb.AppendLine($"    {v.Path} — {v.Description} e.g. {v.Example}");
        }
        sb.AppendLine("HELPERS:");
        foreach (var h in DocumentHtmlTemplating.Helpers) sb.AppendLine($"- {h.Usage} — {h.Description}");
        sb.AppendLine($"LABEL KEYS for {{{{label \"…\"}}}}: {string.Join(", ", DocumentHtmlLabels.Keys)}.");
        return sb.ToString();
    }
}
