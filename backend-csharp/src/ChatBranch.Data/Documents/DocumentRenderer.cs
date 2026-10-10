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
/// Renders a <see cref="DocumentContent"/> to a clean A4 PDF with MigraDoc /
/// PDFsharp (MIT): a letterhead (logo, business name, address, registration and
/// tax numbers, contacts), the document title and number in the skill's accent
/// colour, the customer, an itemised table with the server-computed amounts,
/// totals with the tax line, payment instructions, notes and a footer with page
/// numbers. Letters print a subject and paragraphs instead of the table.
/// Pure: bytes in, bytes out — no database, no files except an optional logo.
/// </summary>
public static class DocumentRenderer
{
    public static byte[] Render(DocumentContent c, DocumentAppConfig cfg, byte[]? logo = null)
    {
        DocumentFonts.EnsureInstalled();
        var useCjk = DocumentFonts.CjkAvailable && TextOf(c, cfg).Any(DocumentFonts.HasCjk);
        var font = useCjk ? DocumentFonts.Cjk : DocumentFonts.Sans;
        var L = Labels.For(c.Language);
        var accent = Parse(cfg.AccentHex);
        var muted = new Color(0x6B, 0x72, 0x80);
        var line = new Color(0xE5, 0xE7, 0xEB);

        var doc = new MigraDocument();
        doc.Info.Title = Clean($"{L.Title(c.Type)} {c.Number}".Trim());
        doc.Info.Author = Clean(cfg.BusinessName);
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = font;
        normal.Font.Size = 9.5;
        normal.Font.Color = new Color(0x1F, 0x23, 0x28);
        normal.ParagraphFormat.SpaceAfter = 0;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromMillimeter(16);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(20);
        section.PageSetup.LeftMargin = Unit.FromMillimeter(18);
        section.PageSetup.RightMargin = Unit.FromMillimeter(18);
        section.PageSetup.FooterDistance = Unit.FromMillimeter(9);
        var width = Unit.FromMillimeter(210 - 36);

        // ---- footer: the note + page x of y ----
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5;
        footer.Format.Font.Color = muted;
        footer.Format.Alignment = ParagraphAlignment.Center;
        if (cfg.FooterNote.Length > 0) footer.AddText(Clean(cfg.FooterNote) + "   ·   ");
        footer.AddText($"{(c.Number.Length > 0 ? c.Number + "   ·   " : "")}{L.Page} ");
        footer.AddPageField();
        footer.AddText($" {L.Of} ");
        footer.AddNumPagesField();

        // ---- letterhead (left) + title block (right) ----
        var head = section.AddTable();
        head.Borders.Visible = false;
        head.AddColumn(width * 0.58);
        head.AddColumn(width * 0.42);
        var hr = head.AddRow();
        var left = hr.Cells[0];
        if (logo is { Length: > 0 })
        {
            try
            {
                var img = left.AddParagraph().AddImage("base64:" + Convert.ToBase64String(logo));
                img.LockAspectRatio = true;
                img.Height = Unit.FromMillimeter(16);
                left.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(1);
            }
            catch { /* an unreadable logo never stops the document */ }
        }
        var biz = left.AddParagraph(Clean(cfg.BusinessName.Length > 0 ? cfg.BusinessName : " "));
        biz.Format.Font.Size = 14;
        biz.Format.Font.Bold = true;
        biz.Format.SpaceAfter = Unit.FromPoint(2);
        foreach (var l in SplitLines(cfg.Address)) Small(left, l, muted);
        var ids = new List<string>();
        if (cfg.RegistrationNo.Length > 0) ids.Add($"{L.RegNo} {cfg.RegistrationNo}");
        if (cfg.TaxNo.Length > 0) ids.Add($"{(cfg.TaxLabel.Length > 0 ? cfg.TaxLabel : L.Tax)} {L.No} {cfg.TaxNo}");
        if (ids.Count > 0) Small(left, string.Join("  ·  ", ids), muted);
        var contacts = new[] { cfg.Phone, cfg.Email, cfg.Website }.Where(s => s.Length > 0).ToList();
        if (contacts.Count > 0) Small(left, string.Join("  ·  ", contacts), muted);

        var right = hr.Cells[1];
        right.Format.Alignment = ParagraphAlignment.Right;
        if (c.Type != DocumentTypes.Letter) // a letter carries its own subject line instead
        {
            var title = right.AddParagraph(L.Title(c.Type).ToUpperInvariant());
            title.Format.Font.Size = 20;
            title.Format.Font.Bold = true;
            title.Format.Font.Color = accent;
            title.Format.SpaceAfter = Unit.FromPoint(4);
        }
        if (c.Number.Length > 0) KeyValue(right, L.Number, c.Number, muted);
        KeyValue(right, L.Date, DateLabel(c.IssueDate, c.Language), muted);
        if (c.DueDate is DateOnly due) KeyValue(right, L.Due, DateLabel(due, c.Language), muted);
        if (c.ValidUntil is DateOnly vu) KeyValue(right, L.ValidUntil, DateLabel(vu, c.Language), muted);
        if (c.Reference.Length > 0) KeyValue(right, L.Reference, c.Reference, muted);

        Rule(section, accent, 1.2, 5);

        // ---- addressee ----
        if (c.Customer.Name.Length + c.Customer.Company.Length > 0)
        {
            var to = section.AddParagraph(c.Type == DocumentTypes.Letter ? L.To : c.Type == DocumentTypes.Receipt ? L.ReceivedFrom : L.BillTo);
            to.Format.Font.Size = 7.5;
            to.Format.Font.Bold = true;
            to.Format.Font.Color = muted;
            to.Format.SpaceAfter = Unit.FromPoint(2);
            var who = section.AddParagraph(Clean(c.Customer.Name.Length > 0 ? c.Customer.Name : c.Customer.Company));
            who.Format.Font.Bold = true;
            who.Format.Font.Size = 10.5;
            if (c.Customer.Name.Length > 0 && c.Customer.Company.Length > 0) Small(section, c.Customer.Company, null);
            foreach (var l in SplitLines(c.Customer.Address)) Small(section, l, null);
            var reach = new[] { c.Customer.Phone, c.Customer.Email }.Where(s => s.Length > 0).ToList();
            if (reach.Count > 0) Small(section, string.Join("  ·  ", reach), muted);
            section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(5);
        }

        if (c.Type == DocumentTypes.Letter) LetterBody(section, c, cfg, L, accent);
        else Priced(section, c, cfg, L, accent, muted, line, width);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    // ---- priced documents (invoice, quotation, receipt) ---------------------------------

    private static void Priced(Section section, DocumentContent c, DocumentAppConfig cfg, Labels L, Color accent,
        Color muted, Color line, Unit width)
    {
        if (c.Title.Length > 0)
        {
            var t = section.AddParagraph(Clean(c.Title));
            t.Format.Font.Bold = true;
            t.Format.Font.Size = 11;
            t.Format.SpaceAfter = Unit.FromMillimeter(3);
        }
        var hasDiscount = c.Lines.Any(l => l.Discount > 0);
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.TopPadding = Unit.FromPoint(4);
        table.BottomPadding = Unit.FromPoint(4);
        var cols = hasDiscount
            ? new[] { 0.06, 0.42, 0.10, 0.15, 0.12, 0.15 }
            : new[] { 0.06, 0.52, 0.10, 0.16, 0.16 };
        foreach (var f in cols) table.AddColumn(width * f);
        var headers = hasDiscount
            ? new[] { "#", L.Description, L.Qty, L.UnitPrice, L.Discount, L.Amount }
            : new[] { "#", L.Description, L.Qty, L.UnitPrice, L.Amount };
        var hrow = table.AddRow();
        hrow.HeadingFormat = true;
        hrow.Shading.Color = accent;
        for (var i = 0; i < headers.Length; i++)
        {
            var p = hrow.Cells[i].AddParagraph(headers[i]);
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 8.5;
            p.Format.Font.Color = Colors.White;
            if (i >= 2) p.Format.Alignment = ParagraphAlignment.Right;
        }
        var n = 0;
        foreach (var l in c.Lines)
        {
            n++;
            var r = table.AddRow();
            r.Borders.Bottom.Color = line;
            r.Borders.Bottom.Width = 0.5;
            var cells = new List<string> { n.ToString(CultureInfo.InvariantCulture), Clean(l.Description),
                DocumentMath.Qty(l.Qty), DocumentMath.Money(l.UnitPrice) };
            if (hasDiscount) cells.Add(l.Discount > 0 ? l.DiscountLabel.EndsWith('%') ? $"{l.DiscountLabel} (-{DocumentMath.Money(l.Discount)})" : $"-{DocumentMath.Money(l.Discount)}" : "");
            cells.Add(DocumentMath.Money(l.Amount));
            for (var i = 0; i < cells.Count; i++)
            {
                var p = r.Cells[i].AddParagraph(cells[i]);
                if (i >= 2) p.Format.Alignment = ParagraphAlignment.Right;
                if (i == 0) p.Format.Font.Color = muted;
            }
        }

        // ---- totals ----
        section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(2);
        var totals = section.AddTable();
        totals.Borders.Visible = false;
        totals.TopPadding = Unit.FromPoint(2.5);
        totals.BottomPadding = Unit.FromPoint(2.5);
        totals.AddColumn(width * 0.55);
        totals.AddColumn(width * 0.27);
        totals.AddColumn(width * 0.18);
        void Total(string label, string value, bool strong = false)
        {
            var r = totals.AddRow();
            var lp = r.Cells[1].AddParagraph(label);
            lp.Format.Alignment = ParagraphAlignment.Right;
            var vp = r.Cells[2].AddParagraph(value);
            vp.Format.Alignment = ParagraphAlignment.Right;
            if (!strong) { lp.Format.Font.Color = muted; return; }
            lp.Format.Font.Bold = vp.Format.Font.Bold = true;
            lp.Format.Font.Size = vp.Format.Font.Size = 11;
            r.Cells[1].Borders.Top.Color = r.Cells[2].Borders.Top.Color = accent;
            r.Cells[1].Borders.Top.Width = r.Cells[2].Borders.Top.Width = 1;
        }
        Total(L.Subtotal, DocumentMath.Money(c.Totals.Subtotal));
        if (c.Totals.Discount > 0) Total(L.DiscountsIncluded, DocumentMath.Money(c.Totals.Discount));
        if (cfg.TaxRate > 0)
            Total($"{(cfg.TaxLabel.Length > 0 ? cfg.TaxLabel : L.Tax)} {DocumentMath.Qty(cfg.TaxRate)}%",
                DocumentMath.Money(c.Totals.Tax));
        Total(c.Type == DocumentTypes.Receipt ? $"{L.AmountPaid} ({c.Currency})" : $"{L.Total} ({c.Currency})",
            DocumentMath.Money(c.Totals.Total), strong: true);

        if (c.Type == DocumentTypes.Receipt)
        {
            var paid = section.AddParagraph(L.Paid.ToUpperInvariant()
                                            + (c.PaymentMethod.Length > 0 ? $"  ·  {Clean(c.PaymentMethod)}" : ""));
            paid.Format.Font.Bold = true;
            paid.Format.Font.Size = 12;
            paid.Format.Font.Color = accent;
            paid.Format.SpaceBefore = Unit.FromMillimeter(4);
        }

        Block(section, L.Notes, c.Notes, muted);
        if (c.Type != DocumentTypes.Receipt) Block(section, L.Payment, cfg.PaymentInstructions, muted);
    }

    // ---- letters ------------------------------------------------------------------

    private static void LetterBody(Section section, DocumentContent c, DocumentAppConfig cfg, Labels L, Color accent)
    {
        if (c.Title.Length > 0)
        {
            var t = section.AddParagraph(Clean(c.Title));
            t.Format.Font.Bold = true;
            t.Format.Font.Size = 11.5;
            t.Format.SpaceAfter = Unit.FromMillimeter(4);
        }
        foreach (var para in c.Paragraphs)
        {
            var p = section.AddParagraph(Clean(para));
            p.Format.Font.Size = 10.5;
            p.Format.LineSpacingRule = LineSpacingRule.OnePtFive;
            p.Format.SpaceAfter = Unit.FromMillimeter(3);
        }
        var closing = section.AddParagraph(Clean(c.Closing.Length > 0 ? c.Closing : L.Sincerely));
        closing.Format.SpaceBefore = Unit.FromMillimeter(6);
        closing.Format.Font.Size = 10.5;
        if (cfg.BusinessName.Length > 0)
        {
            var sig = section.AddParagraph(Clean(cfg.BusinessName));
            sig.Format.SpaceBefore = Unit.FromMillimeter(10);
            sig.Format.Font.Bold = true;
            sig.Format.Font.Size = 10.5;
            sig.Format.Font.Color = accent;
        }
    }

    // ---- helpers ------------------------------------------------------------------

    private static void Block(Section section, string label, string text, Color muted)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var h = section.AddParagraph(label);
        h.Format.Font.Size = 7.5;
        h.Format.Font.Bold = true;
        h.Format.Font.Color = muted;
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

    private static void Small(Cell cell, string text, Color? color)
    {
        var p = cell.AddParagraph(Clean(text));
        p.Format.Font.Size = 8.5;
        if (color is Color c) p.Format.Font.Color = c;
    }

    private static void Small(Section section, string text, Color? color)
    {
        var p = section.AddParagraph(Clean(text));
        p.Format.Font.Size = 9;
        if (color is Color c) p.Format.Font.Color = c;
    }

    private static void KeyValue(Cell cell, string key, string value, Color muted)
    {
        var p = cell.AddParagraph();
        p.Format.Font.Size = 9;
        var k = p.AddFormattedText(key + "  ");
        k.Color = muted;
        p.AddFormattedText(Clean(value)).Bold = true;
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

    private static IEnumerable<string> TextOf(DocumentContent c, DocumentAppConfig cfg)
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
            "No. Pendaftaran", "No.", "Yang benar,");

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
