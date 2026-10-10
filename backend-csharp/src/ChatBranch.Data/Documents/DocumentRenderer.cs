using System.Globalization;
using System.Text;
using ChatBranch.Domain;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using MigraDocument = MigraDoc.DocumentObjectModel.Document;

namespace ChatBranch.Data.Documents;

/// <summary>Who a document is addressed to.</summary>
public sealed record DocParty(string Name, string Company = "", string Address = "", string Email = "", string Phone = "");

/// <summary>Everything one document prints — built by <see cref="DocumentService"/>, numbers already computed.</summary>
public sealed class DocumentContent
{
    public string Type { get; init; } = DocumentTypes.Invoice;
    public string Number { get; init; } = "";
    /// <summary>A letter's subject line / a priced document's optional title.</summary>
    public string Title { get; init; } = "";
    public DateOnly IssueDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public DateOnly? ValidUntil { get; init; }
    public DocParty Customer { get; init; } = new("");
    public List<DocLine> Lines { get; init; } = new();
    public DocTotals Totals { get; init; } = new(0, 0, 0, 0);
    public string Currency { get; init; } = "MYR";
    public string Notes { get; init; } = "";
    public string Reference { get; init; } = "";
    public string PaymentMethod { get; init; } = "";
    /// <summary>A letter's body.</summary>
    public List<string> Paragraphs { get; init; } = new();
    public string Closing { get; init; } = "";
    /// <summary>en | ms — the printed labels.</summary>
    public string Language { get; init; } = "en";
}

/// <summary>
/// Renders a <see cref="DocumentContent"/> to a clean PDF with MigraDoc /
/// PDFsharp (MIT), laid out by the skill's <see cref="DocumentTemplate"/> (the
/// classic layout when the config has none — value for value the layout from
/// before the designer): a letterhead (logo, business name, address,
/// registration and tax numbers, contacts), the document title and number in
/// the accent colour, the customer, an itemised table with the server-computed
/// amounts, totals with the tax line, payment instructions, terms, bank
/// details, notes and a footer with page numbers — the body sections in the
/// template's order. Letters print a subject and paragraphs instead of the
/// table. An uploaded letterhead PDF is drawn UNDER the content of every page
/// (or the first only). Pure: bytes in, bytes out — no database, no files.
/// </summary>
public static class DocumentRenderer
{
    public static byte[] Render(DocumentContent c, DocumentAppConfig cfg, byte[]? logo = null, byte[]? letterhead = null)
    {
        DocumentFonts.EnsureInstalled();
        var tpl = cfg.TemplateFor(c.Type);
        var useCjk = DocumentFonts.CjkAvailable && TextOf(c, cfg, tpl).Any(DocumentFonts.HasCjk);
        var font = useCjk ? DocumentFonts.Cjk : Face(tpl.Fonts.Body);
        var heading = useCjk ? DocumentFonts.Cjk : Face(tpl.Fonts.Heading);
        var r = new Ctx
        {
            C = c, Cfg = cfg, T = tpl, L = Labels.For(c.Language),
            Accent = Parse(tpl.Colors.Accent.Length > 0 ? tpl.Colors.Accent : cfg.AccentHex),
            Muted = Parse(tpl.Colors.Muted), Line = Parse(tpl.Colors.Line),
            HeadingFont = heading != font ? heading : null,
        };

        var doc = new MigraDocument();
        doc.Info.Title = Clean($"{r.L.Title(c.Type)} {c.Number}".Trim());
        doc.Info.Author = Clean(cfg.BusinessName);
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = font;
        normal.Font.Size = tpl.Fonts.Size;
        normal.Font.Color = Parse(tpl.Colors.Text);
        normal.ParagraphFormat.SpaceAfter = 0;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        var letterSize = tpl.Page.Size == "Letter";
        section.PageSetup.PageFormat = letterSize ? PageFormat.Letter : PageFormat.A4;
        if (letterSize)
        {
            // the cloned default page carries A4's explicit size — Letter needs its own
            section.PageSetup.PageWidth = Unit.FromPoint(612);
            section.PageSetup.PageHeight = Unit.FromPoint(792);
        }
        section.PageSetup.TopMargin = Unit.FromMillimeter(tpl.Page.MarginTop);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(tpl.Page.MarginBottom);
        section.PageSetup.LeftMargin = Unit.FromMillimeter(tpl.Page.MarginLeft);
        section.PageSetup.RightMargin = Unit.FromMillimeter(tpl.Page.MarginRight);
        section.PageSetup.FooterDistance = Unit.FromMillimeter(9);
        r.Width = Unit.FromMillimeter((letterSize ? 215.9 : 210) - tpl.Page.MarginLeft - tpl.Page.MarginRight);

        Footer(section, r);
        Header(section, r, logo);

        foreach (var id in tpl.Sections)
        {
            if (c.Type == DocumentTypes.Letter)
            {
                // a letter: the addressee and its body (at the items' place) — no table, totals or payment blocks
                if (id == "info") Info(section, r);
                else if (id == "items") LetterBody(section, r);
                continue;
            }
            switch (id)
            {
                case "info": Info(section, r); break;
                case "items": Items(section, r); break;
                case "totals": TotalsBlock(section, r); break;
                case "notes": Block(section, r.L.Notes, c.Notes, r); break;
                case "payment":
                    if (c.Type != DocumentTypes.Receipt && tpl.Notes.ShowPaymentInstructions)
                        Block(section, r.L.Payment, cfg.PaymentInstructions, r);
                    break;
                case "terms": Block(section, r.L.Terms, tpl.Notes.Terms, r); break;
                case "bank_details": Block(section, r.L.BankDetails, tpl.Notes.BankDetails, r); break;
            }
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        if (letterhead is { Length: > 0 }) Underlay(renderer.PdfDocument, letterhead, tpl.Letterhead.Pages == "first");
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>What one render needs on hand.</summary>
    private sealed class Ctx
    {
        public DocumentContent C = null!;
        public DocumentAppConfig Cfg = null!;
        public DocumentTemplate T = null!;
        public Labels L = null!;
        public Color Accent, Muted, Line;
        public Unit Width;
        /// <summary>Set only when headings use a different face than the body.</summary>
        public string? HeadingFont;
        public bool MetaInHeader => T.Info.Arrangement == "meta_in_header";
        public bool Band => T.Header.Style == "band";
    }

    private static string Face(string face) => face == "serif" ? DocumentFonts.Serif : DocumentFonts.Sans;

    private static void Heading(Paragraph p, Ctx r)
    {
        if (r.HeadingFont != null) p.Format.Font.Name = r.HeadingFont;
    }

    private static ParagraphAlignment Align(string a) => a switch
    {
        "center" => ParagraphAlignment.Center,
        "right" => ParagraphAlignment.Right,
        _ => ParagraphAlignment.Left,
    };

    // ---- the letterhead background ----------------------------------------------------

    /// <summary>Draw the letterhead PDF's first page UNDER every page's content (or the first page's).</summary>
    private static void Underlay(PdfSharp.Pdf.PdfDocument pdf, byte[] letterhead, bool firstOnly)
    {
        try
        {
            using var src = new MemoryStream(letterhead);
            using var form = PdfSharp.Drawing.XPdfForm.FromStream(src);
            form.PageNumber = 1;
            for (var i = 0; i < pdf.PageCount; i++)
            {
                if (firstOnly && i > 0) break;
                var page = pdf.Pages[i];
                using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page, PdfSharp.Drawing.XGraphicsPdfPageOptions.Prepend);
                gfx.DrawImage(form, 0, 0, page.Width.Point, page.Height.Point);
            }
        }
        catch { /* an unreadable letterhead never stops the document */ }
    }

    // ---- footer -------------------------------------------------------------------------

    private static void Footer(Section section, Ctx r)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5;
        footer.Format.Font.Color = r.Muted;
        footer.Format.Alignment = Align(r.T.Footer.Align);
        var note = r.T.Footer.Text.Length > 0 ? r.T.Footer.Text : r.Cfg.FooterNote;
        var parts = new List<string>();
        if (note.Length > 0) parts.Add(Clean(note));
        if (r.T.Footer.ShowNumber && r.C.Number.Length > 0) parts.Add(r.C.Number);
        if (!r.T.Footer.PageNumbers)
        {
            if (parts.Count > 0) footer.AddText(string.Join("   ·   ", parts));
            return;
        }
        footer.AddText(string.Concat(parts.Select(p => p + "   ·   ")) + $"{r.L.Page} ");
        footer.AddPageField();
        footer.AddText($" {r.L.Of} ");
        footer.AddNumPagesField();
    }

    // ---- header -------------------------------------------------------------------------

    private static void Header(Section section, Ctx r, byte[]? logo)
    {
        var t = r.T;
        var cfg = r.Cfg;
        var L = r.L;
        var c = r.C;
        var split = t.Header.Layout == "split";
        var logoPos = t.Header.LogoPosition;
        var hasLogo = logo is { Length: > 0 };

        // a centred logo sits above the whole header
        if (hasLogo && logoPos == "center")
        {
            var p = section.AddParagraph();
            p.Format.Alignment = ParagraphAlignment.Center;
            p.Format.SpaceAfter = Unit.FromMillimeter(2);
            Logo(p, logo!, t);
        }

        var head = section.AddTable();
        head.Borders.Visible = false;
        Color? onBand = null;
        if (r.Band)
        {
            head.Shading.Color = t.Header.BandColor.Length > 0 ? Parse(t.Header.BandColor) : r.Accent;
            head.LeftPadding = head.RightPadding = Unit.FromMillimeter(4);
            head.TopPadding = head.BottomPadding = Unit.FromMillimeter(3);
            onBand = Parse(t.Header.BandTextColor);
        }
        Cell bizCell, titleCell;
        if (split)
        {
            head.AddColumn(r.Width * 0.58);
            head.AddColumn(r.Width * 0.42);
            var hr = head.AddRow();
            bizCell = hr.Cells[0];
            titleCell = hr.Cells[1];
        }
        else
        {
            head.AddColumn(r.Width);
            bizCell = head.AddRow().Cells[0];
            titleCell = head.AddRow().Cells[0];
            if (t.Header.Layout == "centered") bizCell.Format.Alignment = ParagraphAlignment.Center;
        }
        titleCell.Format.Alignment = Align(t.Title.Align);
        var muted = onBand ?? r.Muted;

        // ---- the business block ----
        if (hasLogo && logoPos != "center")
        {
            // left: above the business name; right: on the title side of a split header, else its own line
            var target = logoPos == "right" && split ? titleCell : bizCell;
            var p = target.AddParagraph();
            if (logoPos == "right" && !split) p.Format.Alignment = ParagraphAlignment.Right;
            if (Logo(p, logo!, t)) target.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(1);
        }
        if (t.Header.ShowBusinessName)
        {
            var biz = bizCell.AddParagraph(Clean(cfg.BusinessName.Length > 0 ? cfg.BusinessName : " "));
            biz.Format.Font.Size = t.Header.BusinessNameSize;
            biz.Format.Font.Bold = true;
            biz.Format.SpaceAfter = Unit.FromPoint(2);
            if (onBand is Color bt) biz.Format.Font.Color = bt;
            Heading(biz, r);
        }
        if (t.Header.Tagline.Length > 0)
        {
            var tag = bizCell.AddParagraph(Clean(t.Header.Tagline));
            tag.Format.Font.Size = 9;
            tag.Format.Font.Italic = true;
            tag.Format.Font.Color = muted;
            tag.Format.SpaceAfter = Unit.FromPoint(2);
        }
        if (t.Header.ShowAddress) foreach (var l in SplitLines(cfg.Address)) Small(bizCell, l, muted);
        if (t.Header.ShowIds)
        {
            var ids = new List<string>();
            if (cfg.RegistrationNo.Length > 0) ids.Add($"{L.RegNo} {cfg.RegistrationNo}");
            if (cfg.TaxNo.Length > 0) ids.Add($"{(cfg.TaxLabel.Length > 0 ? cfg.TaxLabel : L.Tax)} {L.No} {cfg.TaxNo}");
            if (ids.Count > 0) Small(bizCell, string.Join("  ·  ", ids), muted);
        }
        if (t.Header.ShowContacts)
        {
            var contacts = new[] { cfg.Phone, cfg.Email, cfg.Website }.Where(s => s.Length > 0).ToList();
            if (contacts.Count > 0) Small(bizCell, string.Join("  ·  ", contacts), muted);
        }
        foreach (var l in t.Header.ExtraLines) Small(bizCell, l, muted);

        // ---- the title (+ the document details when they live in the header) ----
        if (!split) titleCell.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(2);
        if (c.Type != DocumentTypes.Letter && t.Title.Show) // a letter carries its own subject line instead
        {
            var text = L.Title(c.Type);
            var title = titleCell.AddParagraph(t.Title.Uppercase ? text.ToUpperInvariant() : text);
            title.Format.Font.Size = t.Title.Size;
            title.Format.Font.Bold = true;
            title.Format.Font.Color = t.Title.Color.Length > 0 ? Parse(t.Title.Color) : onBand ?? r.Accent;
            title.Format.SpaceAfter = Unit.FromPoint(4);
            Heading(title, r);
        }
        if (r.MetaInHeader) Meta(titleCell, r, onBand);

        switch (t.Header.Rule)
        {
            case "accent": Rule(section, r.Accent, 1.2, 5); break;
            case "line": Rule(section, r.Line, 0.75, 5); break;
            default:
                var gap = section.AddParagraph();
                gap.Format.SpaceBefore = Unit.FromMillimeter(3);
                gap.Format.SpaceAfter = Unit.FromMillimeter(5);
                break;
        }
    }

    private static bool Logo(Paragraph p, byte[] logo, DocumentTemplate t)
    {
        try
        {
            var img = p.AddImage("base64:" + Convert.ToBase64String(logo));
            img.LockAspectRatio = true;
            img.Height = Unit.FromMillimeter(t.Header.LogoSizeMm);
            return true;
        }
        catch { return false; } // an unreadable logo never stops the document
    }

    /// <summary>Number, date, due / valid until, reference — key + value lines.</summary>
    private static void Meta(DocumentObject container, Ctx r, Color? onBand = null)
    {
        var c = r.C;
        var L = r.L;
        var muted = onBand ?? r.Muted;
        if (c.Number.Length > 0) KeyValue(container, L.Number, c.Number, muted, onBand);
        KeyValue(container, L.Date, DateLabel(c.IssueDate, c.Language), muted, onBand);
        if (c.DueDate is DateOnly due) KeyValue(container, L.Due, DateLabel(due, c.Language), muted, onBand);
        if (c.ValidUntil is DateOnly vu) KeyValue(container, L.ValidUntil, DateLabel(vu, c.Language), muted, onBand);
        if (c.Reference.Length > 0) KeyValue(container, L.Reference, c.Reference, muted, onBand);
    }

    // ---- the addressee (+ the document details when they live in the body) -----------

    private static void Info(Section section, Ctx r)
    {
        var c = r.C;
        var hasCustomer = c.Customer.Name.Length + c.Customer.Company.Length > 0;
        if (r.MetaInHeader)
        {
            if (!hasCustomer) return;
            BillTo(section, r);
            section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(5);
            return;
        }
        if (r.T.Info.Arrangement == "stacked")
        {
            Meta(section, r);
            section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(4);
            if (!hasCustomer) return;
            BillTo(section, r);
            section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(5);
            return;
        }
        // side by side: the addressee left, the details right
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.AddColumn(r.Width * 0.58);
        table.AddColumn(r.Width * 0.42);
        var row = table.AddRow();
        if (hasCustomer) BillTo(row.Cells[0], r);
        row.Cells[1].Format.Alignment = ParagraphAlignment.Right;
        Meta(row.Cells[1], r);
        section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(5);
    }

    private static void BillTo(DocumentObject container, Ctx r)
    {
        var c = r.C;
        var L = r.L;
        var label = r.T.Info.BillToLabel.Length > 0 ? Clean(r.T.Info.BillToLabel)
            : c.Type == DocumentTypes.Letter ? L.To : c.Type == DocumentTypes.Receipt ? L.ReceivedFrom : L.BillTo;
        var to = Add(container, label);
        to.Format.Font.Size = 7.5;
        to.Format.Font.Bold = true;
        to.Format.Font.Color = r.Muted;
        to.Format.SpaceAfter = Unit.FromPoint(2);
        var who = Add(container, Clean(c.Customer.Name.Length > 0 ? c.Customer.Name : c.Customer.Company));
        who.Format.Font.Bold = true;
        who.Format.Font.Size = 10.5;
        if (c.Customer.Name.Length > 0 && c.Customer.Company.Length > 0) Small9(container, c.Customer.Company, null);
        foreach (var l in SplitLines(c.Customer.Address)) Small9(container, l, null);
        var reach = new[] { c.Customer.Phone, c.Customer.Email }.Where(s => s.Length > 0).ToList();
        if (reach.Count > 0) Small9(container, string.Join("  ·  ", reach), r.Muted);
    }

    // ---- priced documents (invoice, quotation, receipt) ---------------------------------

    private static void Items(Section section, Ctx r)
    {
        var c = r.C;
        var t = r.T.Table;
        var L = r.L;
        if (c.Title.Length > 0)
        {
            var ti = section.AddParagraph(Clean(c.Title));
            ti.Format.Font.Bold = true;
            ti.Format.Font.Size = 11;
            ti.Format.SpaceAfter = Unit.FromMillimeter(3);
            Heading(ti, r);
        }
        var hasDiscount = c.Lines.Any(l => l.Discount > 0);
        var cols = t.Columns.Where(col => col != "discount" || hasDiscount).ToList();
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.TopPadding = Unit.FromPoint(4);
        table.BottomPadding = Unit.FromPoint(4);
        var fixedShare = cols.Where(col => col != "description").Sum(col => ColumnShare(col, hasDiscount));
        foreach (var col in cols)
            table.AddColumn(r.Width * (col == "description" ? DescriptionShare(cols, hasDiscount, fixedShare) : ColumnShare(col, hasDiscount)));

        string HeadingOf(string col) => t.Labels.TryGetValue(col, out var label) && label.Length > 0 ? Clean(label) : col switch
        {
            "index" => "#",
            "description" => L.Description,
            "qty" => L.Qty,
            "unit_price" => L.UnitPrice,
            "discount" => L.Discount,
            _ => L.Amount,
        };
        var headerColor = t.HeaderColor.Length > 0 ? Parse(t.HeaderColor) : r.Accent;
        var hrow = table.AddRow();
        hrow.HeadingFormat = true;
        if (t.HeaderStyle == "filled") hrow.Shading.Color = headerColor;
        else
        {
            hrow.Borders.Bottom.Color = t.HeaderStyle == "underline" ? headerColor : r.Line;
            hrow.Borders.Bottom.Width = t.HeaderStyle == "underline" ? 1 : 0.75;
        }
        for (var i = 0; i < cols.Count; i++)
        {
            var p = hrow.Cells[i].AddParagraph(HeadingOf(cols[i]));
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 8.5;
            p.Format.Font.Color = t.HeaderStyle switch
            {
                "filled" => Parse(t.HeaderTextColor),
                "underline" => headerColor,
                _ => r.Muted,
            };
            if (Numeric(cols[i])) p.Format.Alignment = ParagraphAlignment.Right;
        }
        var n = 0;
        foreach (var l in c.Lines)
        {
            n++;
            var row = table.AddRow();
            if (t.Border == "lines")
            {
                row.Borders.Bottom.Color = r.Line;
                row.Borders.Bottom.Width = 0.5;
            }
            if (t.Zebra && n % 2 == 0) row.Shading.Color = Parse(t.ZebraColor);
            for (var i = 0; i < cols.Count; i++)
            {
                var text = cols[i] switch
                {
                    "index" => n.ToString(CultureInfo.InvariantCulture),
                    "description" => Clean(l.Description),
                    "qty" => DocumentMath.Qty(l.Qty),
                    "unit_price" => DocumentMath.Money(l.UnitPrice),
                    "discount" => l.Discount > 0
                        ? l.DiscountLabel.EndsWith('%') ? $"{l.DiscountLabel} (-{DocumentMath.Money(l.Discount)})" : $"-{DocumentMath.Money(l.Discount)}"
                        : "",
                    _ => DocumentMath.Money(l.Amount),
                };
                var p = row.Cells[i].AddParagraph(text);
                if (Numeric(cols[i])) p.Format.Alignment = ParagraphAlignment.Right;
                if (cols[i] == "index") p.Format.Font.Color = r.Muted;
            }
        }
        switch (t.Border)
        {
            case "grid":
                table.Borders.Visible = true;
                table.Borders.Color = r.Line;
                table.Borders.Width = 0.5;
                break;
            case "outer":
                table.SetEdge(0, 0, cols.Count, table.Rows.Count, Edge.Box, BorderStyle.Single, 0.75, r.Line);
                break;
        }
    }

    private static bool Numeric(string col) => col is "qty" or "unit_price" or "discount" or "amount";

    /// <summary>The classic column shares; the description takes what is left.</summary>
    private static double ColumnShare(string col, bool hasDiscount) => col switch
    {
        "index" => 0.06,
        "qty" => 0.10,
        "unit_price" => hasDiscount ? 0.15 : 0.16,
        "discount" => 0.12,
        "amount" => hasDiscount ? 0.15 : 0.16,
        _ => 0,
    };

    /// <summary>The classic description shares exactly (0.52 / 0.42); otherwise the rest.</summary>
    private static double DescriptionShare(List<string> cols, bool hasDiscount, double fixedShare) =>
        cols.Count == (hasDiscount ? 6 : 5) ? (hasDiscount ? 0.42 : 0.52) : 1 - fixedShare;

    private static void TotalsBlock(Section section, Ctx r)
    {
        var c = r.C;
        var cfg = r.Cfg;
        var L = r.L;
        var style = r.T.Totals.Style;
        var position = r.T.Totals.Position;
        section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(2);
        var totals = section.AddTable();
        totals.Borders.Visible = false;
        totals.TopPadding = Unit.FromPoint(2.5);
        totals.BottomPadding = Unit.FromPoint(2.5);
        int labelCol, valueCol;
        switch (position)
        {
            case "left":
                totals.AddColumn(r.Width * 0.27);
                totals.AddColumn(r.Width * 0.18);
                totals.AddColumn(r.Width * 0.55);
                (labelCol, valueCol) = (0, 1);
                break;
            case "full":
                totals.AddColumn(r.Width * 0.75);
                totals.AddColumn(r.Width * 0.25);
                (labelCol, valueCol) = (0, 1);
                break;
            default:
                totals.AddColumn(r.Width * 0.55);
                totals.AddColumn(r.Width * 0.27);
                totals.AddColumn(r.Width * 0.18);
                (labelCol, valueCol) = (1, 2);
                break;
        }
        void Total(string label, string value, bool strong = false)
        {
            var row = totals.AddRow();
            var lc = row.Cells[labelCol];
            var vc = row.Cells[valueCol];
            var lp = lc.AddParagraph(label);
            lp.Format.Alignment = position == "full" ? ParagraphAlignment.Left : ParagraphAlignment.Right;
            var vp = vc.AddParagraph(value);
            vp.Format.Alignment = ParagraphAlignment.Right;
            if (style == "boxed")
            {
                lc.Borders.Bottom.Color = vc.Borders.Bottom.Color = r.Line;
                lc.Borders.Bottom.Width = vc.Borders.Bottom.Width = 0.5;
            }
            if (!strong) { lp.Format.Font.Color = r.Muted; return; }
            lp.Format.Font.Bold = vp.Format.Font.Bold = true;
            lp.Format.Font.Size = vp.Format.Font.Size = 11;
            switch (style)
            {
                case "band":
                    lc.Shading.Color = vc.Shading.Color = r.Accent;
                    lp.Format.Font.Color = vp.Format.Font.Color = Colors.White;
                    break;
                case "boxed":
                    lc.Shading.Color = vc.Shading.Color = Parse(r.T.Table.ZebraColor);
                    lc.Borders.Top.Color = vc.Borders.Top.Color = r.Accent;
                    lc.Borders.Top.Width = vc.Borders.Top.Width = 1;
                    break;
                default:
                    lc.Borders.Top.Color = vc.Borders.Top.Color = r.Accent;
                    lc.Borders.Top.Width = vc.Borders.Top.Width = 1;
                    break;
            }
        }
        Total(L.Subtotal, DocumentMath.Money(c.Totals.Subtotal));
        if (c.Totals.Discount > 0) Total(L.DiscountsIncluded, DocumentMath.Money(c.Totals.Discount));
        if (cfg.TaxRate > 0)
            Total($"{(cfg.TaxLabel.Length > 0 ? cfg.TaxLabel : L.Tax)} {DocumentMath.Qty(cfg.TaxRate)}%",
                DocumentMath.Money(c.Totals.Tax));
        Total(c.Type == DocumentTypes.Receipt ? $"{L.AmountPaid} ({c.Currency})" : $"{L.Total} ({c.Currency})",
            DocumentMath.Money(c.Totals.Total), strong: true);
        if (style == "boxed") totals.SetEdge(labelCol, 0, 2, totals.Rows.Count, Edge.Box, BorderStyle.Single, 0.5, r.Line);

        if (c.Type == DocumentTypes.Receipt)
        {
            var paid = section.AddParagraph(L.Paid.ToUpperInvariant()
                                            + (c.PaymentMethod.Length > 0 ? $"  ·  {Clean(c.PaymentMethod)}" : ""));
            paid.Format.Font.Bold = true;
            paid.Format.Font.Size = 12;
            paid.Format.Font.Color = r.Accent;
            paid.Format.SpaceBefore = Unit.FromMillimeter(4);
        }
    }

    // ---- letters ------------------------------------------------------------------

    private static void LetterBody(Section section, Ctx r)
    {
        var c = r.C;
        if (c.Title.Length > 0)
        {
            var t = section.AddParagraph(Clean(c.Title));
            t.Format.Font.Bold = true;
            t.Format.Font.Size = 11.5;
            t.Format.SpaceAfter = Unit.FromMillimeter(4);
            Heading(t, r);
        }
        foreach (var para in c.Paragraphs)
        {
            var p = section.AddParagraph(Clean(para));
            p.Format.Font.Size = 10.5;
            p.Format.LineSpacingRule = LineSpacingRule.OnePtFive;
            p.Format.SpaceAfter = Unit.FromMillimeter(3);
        }
        var closing = section.AddParagraph(Clean(c.Closing.Length > 0 ? c.Closing : r.L.Sincerely));
        closing.Format.SpaceBefore = Unit.FromMillimeter(6);
        closing.Format.Font.Size = 10.5;
        if (r.Cfg.BusinessName.Length > 0)
        {
            var sig = section.AddParagraph(Clean(r.Cfg.BusinessName));
            sig.Format.SpaceBefore = Unit.FromMillimeter(10);
            sig.Format.Font.Bold = true;
            sig.Format.Font.Size = 10.5;
            sig.Format.Font.Color = r.Accent;
            Heading(sig, r);
        }
    }

    // ---- helpers ------------------------------------------------------------------

    private static void Block(Section section, string label, string text, Ctx r)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var h = section.AddParagraph(label);
        h.Format.Font.Size = 7.5;
        h.Format.Font.Bold = true;
        h.Format.Font.Color = r.Muted;
        h.Format.SpaceBefore = Unit.FromMillimeter(6);
        h.Format.SpaceAfter = Unit.FromPoint(2);
        foreach (var l in SplitLines(text)) section.AddParagraph(Clean(l)).Format.SpaceAfter = Unit.FromPoint(1);
    }

    private static void Rule(Section section, Color color, double width, double spaceMm)
    {
        var p = section.AddParagraph();
        p.Format.SpaceBefore = Unit.FromMillimeter(3);
        p.Format.SpaceAfter = Unit.FromMillimeter(spaceMm);
        p.Format.Borders.Bottom.Color = color;
        p.Format.Borders.Bottom.Width = width;
    }

    private static Paragraph Add(DocumentObject container, string text) => container switch
    {
        Cell cell => cell.AddParagraph(text),
        Section s => s.AddParagraph(text),
        _ => throw new ArgumentException("A paragraph goes in a section or a cell.", nameof(container)),
    };

    /// <summary>A small line of the letterhead (8.5 pt).</summary>
    private static void Small(Cell cell, string text, Color? color)
    {
        var p = cell.AddParagraph(Clean(text));
        p.Format.Font.Size = 8.5;
        if (color is Color c) p.Format.Font.Color = c;
    }

    /// <summary>A small line under the addressee (9 pt).</summary>
    private static void Small9(DocumentObject container, string text, Color? color)
    {
        var p = Add(container, Clean(text));
        p.Format.Font.Size = 9;
        if (color is Color c) p.Format.Font.Color = c;
    }

    private static void KeyValue(DocumentObject container, string key, string value, Color muted, Color? strong = null)
    {
        var p = container is Cell cell ? cell.AddParagraph() : ((Section)container).AddParagraph();
        p.Format.Font.Size = 9;
        var k = p.AddFormattedText(key + "  ");
        k.Color = muted;
        var v = p.AddFormattedText(Clean(value));
        v.Bold = true;
        if (strong is Color s) v.Color = s;
    }

    private static IEnumerable<string> SplitLines(string? text) =>
        (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static Color Parse(string hex)
    {
        var h = hex.TrimStart('#');
        return new Color(Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    /// <summary>Text the embedded fonts can print: no control characters, no emoji / astral symbols.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch)) { i++; continue; }   // emoji & friends: no glyphs
            if (char.IsLowSurrogate(ch)) continue;
            if (char.IsControl(ch)) { if (ch == '\t') sb.Append(' '); continue; }
            if (ch is >= '︀' and <= '️' or '‍') continue; // variation selectors / ZWJ
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> TextOf(DocumentContent c, DocumentAppConfig cfg, DocumentTemplate t)
    {
        yield return cfg.BusinessName;
        yield return cfg.Address;
        yield return cfg.PaymentInstructions;
        yield return cfg.FooterNote;
        yield return c.Title;
        yield return c.Notes;
        yield return c.Reference;
        yield return c.PaymentMethod;
        yield return c.Closing;
        yield return c.Customer.Name;
        yield return c.Customer.Company;
        yield return c.Customer.Address;
        foreach (var l in c.Lines) yield return l.Description;
        foreach (var p in c.Paragraphs) yield return p;
        foreach (var x in TemplateText(t)) yield return x;
    }

    /// <summary>The words a template itself prints (the CJK font choice and the CJK refusal read them too).</summary>
    public static IEnumerable<string> TemplateText(DocumentTemplate? t)
    {
        if (t == null) yield break;
        yield return t.Header.Tagline;
        foreach (var l in t.Header.ExtraLines) yield return l;
        yield return t.Info.BillToLabel;
        foreach (var l in t.Table.Labels.Values) yield return l;
        yield return t.Notes.Terms;
        yield return t.Notes.BankDetails;
        yield return t.Footer.Text;
    }

    public static string DateLabel(DateOnly d, string language) =>
        language == "ms"
            ? $"{d.Day} {Labels.MalayMonths[d.Month - 1]} {d.Year}"
            : d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The printed words, English or Malay.</summary>
    public sealed record Labels(string Invoice, string Quotation, string Receipt, string Letter, string Number,
        string Date, string Due, string ValidUntil, string Reference, string BillTo, string ReceivedFrom, string To,
        string Description, string Qty, string UnitPrice, string Discount, string Amount, string Subtotal,
        string DiscountsIncluded, string Tax, string Total, string AmountPaid, string Paid, string Notes,
        string Payment, string Page, string Of, string RegNo, string No, string Sincerely)
    {
        public string Terms { get; init; } = "TERMS";
        public string BankDetails { get; init; } = "BANK DETAILS";

        public static readonly string[] MalayMonths =
            { "Jan", "Feb", "Mac", "Apr", "Mei", "Jun", "Jul", "Ogo", "Sep", "Okt", "Nov", "Dis" };

        public static readonly Labels En = new("Invoice", "Quotation", "Receipt", "Letter", "No.", "Date", "Due",
            "Valid until", "Ref.", "BILL TO", "RECEIVED FROM", "TO", "Description", "Qty", "Unit price", "Discount",
            "Amount", "Subtotal", "Discounts included", "Tax", "Total", "Amount paid", "Paid", "NOTES", "PAYMENT",
            "Page", "of", "Reg. No.", "No.", "Yours sincerely,");

        public static readonly Labels Ms = new("Invois", "Sebut Harga", "Resit", "Surat", "No.", "Tarikh",
            "Tarikh akhir", "Sah sehingga", "Rujukan", "KEPADA", "DITERIMA DARIPADA", "KEPADA", "Perihal",
            "Kuantiti", "Harga seunit", "Diskaun", "Jumlah", "Jumlah kecil", "Termasuk diskaun", "Cukai",
            "Jumlah keseluruhan", "Jumlah dibayar", "Telah dibayar", "CATATAN", "PEMBAYARAN", "Muka surat", "daripada",
            "No. Pendaftaran", "No.", "Yang benar,") { Terms = "TERMA", BankDetails = "MAKLUMAT BANK" };

        public static Labels For(string? language) => language == "ms" ? Ms : En;

        public string Title(string type) => type switch
        {
            DocumentTypes.Quotation => Quotation,
            DocumentTypes.Receipt => Receipt,
            DocumentTypes.Letter => Letter,
            _ => Invoice,
        };
    }
}
