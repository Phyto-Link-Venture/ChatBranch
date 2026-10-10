using System.Text;
using System.Text.Json;
using ChatBranch.Data.Documents;
using ChatBranch.Domain;
using Xunit;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace ChatBranch.Tests;

/// <summary>
/// The Documents app's template designer, renderer side: the template
/// normalizer (accepts / refuses, unknown keys refused, per-type overrides),
/// every preset rendering a valid PDF, a config WITHOUT a template printing
/// every word exactly where it did before the designer existed (golden
/// fixtures), and the letterhead background drawn under the content.
/// </summary>
public class DocumentTemplateTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static DocumentAppConfig Cfg(object template, out string? error)
    {
        var raw = JsonSerializer.SerializeToNode(new
        {
            app = "documents", business_name = "Kedai Rambut Aisha", address = "12 Jalan Bunga\nKuala Lumpur",
            tax_rate = 8, payment_instructions = "Maybank 5123 4567 8901",
        })!.AsObject();
        raw["template"] = JsonSerializer.SerializeToNode(template);
        return DocumentAppConfig.From(JsonSerializer.SerializeToElement(raw), out error);
    }

    private static DocumentContent Invoice(DocumentAppConfig cfg, string type = DocumentTypes.Invoice) =>
        DocumentService.Build(type, J(new
        {
            customer_name = "Encik Ahmad", notes = "Thank you.",
            items = new object[]
            {
                new { description = "Haircut", qty = 2, unit_price = 45.5 },
                new { description = "Colour", qty = 1, unit_price = 120, discount = "10%" },
            },
        }), cfg, null, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, "INV-0042").Content!;

    // ---- back-compat -----------------------------------------------------------------------

    [Fact]
    public void A_config_without_a_template_prints_every_word_where_it_did_before_the_designer()
    {
        var cfg = DocumentLegacyGolden.Config();
        Assert.Null(cfg.Template);
        Assert.False(cfg.ToDictionary().ContainsKey("template")); // a stored config doesn't grow a template
        foreach (var (name, content) in DocumentLegacyGolden.Samples(cfg))
        {
            var expected = File.ReadAllText(DocumentLegacyGolden.FixturePath(name));
            var actual = DocumentLegacyGolden.Fingerprint(DocumentRenderer.Render(content, cfg));
            Assert.True(expected == actual, $"{name} changed:\n{FirstDiff(expected, actual)}");
        }
    }

    [Fact]
    public void The_classic_preset_given_explicitly_matches_no_template_at_all()
    {
        var legacy = DocumentLegacyGolden.Config();
        var raw = JsonSerializer.SerializeToNode(legacy.ToDictionary())!.AsObject();
        raw["template"] = JsonSerializer.SerializeToNode(new { version = 1, preset = "classic" });
        var cfg = DocumentAppConfig.From(JsonSerializer.SerializeToElement(raw), out var error);
        Assert.Null(error);
        Assert.NotNull(cfg.Template);
        foreach (var (name, content) in DocumentLegacyGolden.Samples(cfg))
            Assert.Equal(File.ReadAllText(DocumentLegacyGolden.FixturePath(name)),
                DocumentLegacyGolden.Fingerprint(DocumentRenderer.Render(content, cfg)));
    }

    private static string FirstDiff(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        for (var i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            var x = i < la.Length ? la[i] : "(end)";
            var y = i < lb.Length ? lb[i] : "(end)";
            if (x != y) return $"line {i + 1}: expected '{x}', got '{y}'";
        }
        return "(same)";
    }

    // ---- presets -----------------------------------------------------------------------------

    [Theory]
    [InlineData("classic")]
    [InlineData("modern")]
    [InlineData("minimal")]
    [InlineData("bold")]
    public void Every_preset_renders_a_valid_pdf_for_every_type(string preset)
    {
        var cfg = Cfg(new { preset }, out var error);
        Assert.Null(error);
        Assert.Equal(preset, cfg.Template!.Preset);
        foreach (var type in DocumentTypes.Numbered)
        {
            var content = type == DocumentTypes.Letter
                ? DocumentService.Build(type, J(new { title = "Booking", paragraphs = new[] { "We confirm your booking." } }),
                    cfg, new Contact { Name = "Siti" }, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, "LTR-0001").Content!
                : Invoice(cfg, type);
            var pdf = DocumentRenderer.Render(content, cfg);
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
            var text = DocumentRig.PdfText(pdf);
            Assert.Contains("Kedai", text);
            if (type != DocumentTypes.Letter)
            {
                Assert.Contains("Haircut", text);
                Assert.Contains("91.00", text);
            }
        }
    }

    [Fact]
    public void A_designed_template_changes_what_and_where_things_print()
    {
        var cfg = Cfg(new
        {
            preset = "modern",
            page = new { size = "Letter", margins_mm = new { top = 20, left = 25, right = 25 } },
            fonts = new { heading = "serif" },
            colors = new { accent = "#1e3a8a" },
            header = new { logo_position = "right", tagline = "Hair that turns heads", extra_lines = new[] { "Open daily 10am - 10pm" } },
            title = new { uppercase = true, align = "left" },
            table = new { columns = new[] { "description", "qty", "amount" }, labels = new { description = "Service" } },
            totals = new { position = "full", style = "boxed" },
            notes = new { terms = "Payment due in 14 days", bank_details = "Maybank 1234" },
            footer = new { text = "Visit us again", page_numbers = false },
            sections = new[] { "items", "totals", "info", "bank_details" },
        }, out var error);
        Assert.Null(error);
        var t = cfg.Template!;
        Assert.Equal("#1E3A8A", t.Colors.Accent);
        // the order list keeps every section — the missing ones follow in their usual order
        Assert.Equal(new[] { "items", "totals", "info", "bank_details", "notes", "payment", "terms" }, t.Sections);

        var pdf = DocumentRenderer.Render(Invoice(cfg), cfg);
        var text = DocumentRig.PdfText(pdf);
        Assert.Contains("Service", text);
        Assert.DoesNotContain("Unit price", text);
        Assert.Contains("turns heads", text);
        Assert.Contains("Open daily", text);
        Assert.Contains("TERMS", text);
        Assert.Contains("BANK DETAILS", text);
        Assert.Contains("Visit us again", text);
        Assert.DoesNotContain("Page 1", text);
        using var doc = PdfPigDocument.Open(pdf);
        Assert.InRange(doc.GetPage(1).Width, 611, 613);  // US Letter = 612 × 792 pt
        // the items table comes BEFORE the addressee
        var words = doc.GetPage(1).GetWords().ToList();
        var haircutY = words.First(w => w.Text == "Haircut").BoundingBox.Bottom;
        var ahmadY = words.First(w => w.Text == "Ahmad").BoundingBox.Bottom;
        Assert.True(haircutY > ahmadY, "the items should print above the bill-to block");
    }

    [Fact]
    public void Per_type_overrides_change_only_that_type()
    {
        var cfg = Cfg(new
        {
            preset = "classic",
            overrides = new
            {
                receipt = new { title = new { uppercase = false }, footer = new { text = "Paid with thanks" } },
            },
        }, out var error);
        Assert.Null(error);
        Assert.Equal(new[] { "receipt" }, cfg.Template!.Overrides.Keys);
        Assert.False(cfg.TemplateFor(DocumentTypes.Receipt).Title.Uppercase);
        Assert.True(cfg.TemplateFor(DocumentTypes.Invoice).Title.Uppercase);
        var receipt = DocumentRig.PdfText(DocumentRenderer.Render(Invoice(cfg, DocumentTypes.Receipt), cfg));
        Assert.Contains("Paid with thanks", receipt);
        Assert.Contains("Receipt", receipt);
        Assert.DoesNotContain("RECEIPT", receipt);
        var invoice = DocumentRig.PdfText(DocumentRenderer.Render(Invoice(cfg), cfg));
        Assert.DoesNotContain("Paid with thanks", invoice);
        Assert.Contains("INVOICE", invoice);

        // the canonical form round-trips (only the keys the override named)
        var again = DocumentTemplate.From(JsonSerializer.SerializeToElement(cfg.Template.ToDictionary()), out var e2)!;
        Assert.Null(e2);
        Assert.Equal(JsonSerializer.Serialize(cfg.Template.ToDictionary()), JsonSerializer.Serialize(again.ToDictionary()));
        var ov = JsonSerializer.SerializeToElement(again.ToDictionary())
            .GetProperty("overrides").GetProperty("receipt");
        Assert.Equal(new[] { "title", "footer" }, ov.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "uppercase" }, ov.GetProperty("title").EnumerateObject().Select(p => p.Name));
    }

    // ---- the normalizer ---------------------------------------------------------------------

    [Theory]
    [InlineData("{\"preset\":\"fancy\"}", "preset")]
    [InlineData("{\"colour\":{}}", "unknown key 'template.colour'")]
    [InlineData("{\"header\":{\"logo\":\"left\"}}", "unknown key 'template.header.logo'")]
    [InlineData("{\"colors\":{\"accent\":\"navy\"}}", "hex colour")]
    [InlineData("{\"colors\":{\"text\":\"\"}}", "hex colour")]
    [InlineData("{\"version\":2}", "version")]
    [InlineData("{\"page\":{\"size\":\"A3\"}}", "A4, Letter")]
    [InlineData("{\"page\":{\"margins_mm\":{\"top\":200}}}", "between")]
    [InlineData("{\"fonts\":{\"body\":\"comic\"}}", "sans, serif")]
    [InlineData("{\"table\":{\"columns\":[\"qty\",\"amount\"]}}", "description and amount")]
    [InlineData("{\"table\":{\"columns\":[\"description\",\"amount\",\"sku\"]}}", "unknown 'sku'")]
    [InlineData("{\"table\":{\"zebra\":\"yes\"}}", "true or false")]
    [InlineData("{\"sections\":[\"items\",\"signature\"]}", "unknown 'signature'")]
    [InlineData("{\"letterhead\":{\"file\":\"../../etc/passwd\"}}", "letterhead")]
    [InlineData("{\"header\":{\"extra_lines\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\"]}}", "up to 6")]
    [InlineData("{\"overrides\":{\"invoice\":{\"page\":{\"size\":\"Letter\"}}}}", "can't change 'page'")]
    [InlineData("{\"overrides\":{\"memo\":{}}}", "unknown key 'template.overrides.memo'")]
    [InlineData("{\"overrides\":{\"invoice\":{\"colors\":{\"accent\":\"red\"}}}}", "template.overrides.invoice.colors.accent")]
    [InlineData("[]", "JSON object")]
    public void The_normalizer_refuses_bad_templates_with_the_first_problem(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        DocumentTemplate.From(doc.RootElement, out var error);
        Assert.NotNull(error);
        Assert.Contains(expected, error);
        // and the skills PUT path refuses it too
        var raw = $"{{\"app\":\"documents\",\"template\":{json}}}";
        using var cfgDoc = JsonDocument.Parse(raw);
        ChatBranch.Data.Ai.Skills.AppSkills.Normalize(cfgDoc.RootElement, out var cfgError);
        Assert.NotNull(cfgError);
    }

    [Fact]
    public void The_normalizer_fills_defaults_and_normalizes_values()
    {
        using var doc = JsonDocument.Parse("""
            {"preset":"bold","page":{"size":"letter"},"colors":{"accent":"#abc","line":"e5e7eb"},
             "header":{"extra_lines":["  Lot 5  ", ""]},"table":{"columns":["amount","description","index"]},
             "sections":["totals"],"letterhead":{"file":""}}
            """);
        var t = DocumentTemplate.From(doc.RootElement, out var error)!;
        Assert.Null(error);
        Assert.Equal("Letter", t.Page.Size);
        Assert.Equal("#AABBCC", t.Colors.Accent);
        Assert.Equal("#E5E7EB", t.Colors.Line);
        Assert.Equal(new[] { "Lot 5" }, t.Header.ExtraLines);
        Assert.Equal(new[] { "index", "description", "amount" }, t.Table.Columns); // printed order is fixed
        Assert.Equal("band", t.Header.Style); // from the bold preset
        Assert.Equal("totals", t.Sections[0]);
        Assert.Equal(DocumentTemplate.SectionIds.Length, t.Sections.Count);
        var canon = t.ToDictionary();
        Assert.Equal(1, canon["version"]);
        Assert.Equal(DocumentTemplate.Presets, DocumentTemplate.PresetCatalog().Keys);
    }

    // ---- the letterhead background ---------------------------------------------------------

    [Fact]
    public void An_uploaded_pdf_becomes_a_letterhead_drawn_under_every_page_or_the_first()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-lh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new DocumentService(null!, null!, new ChatBranch.Data.Channels.MediaStore(dir));
            // a two-page "design": only its first page is kept
            var design = DesignPdf("ACME LETTERHEAD", pages: 2);
            Assert.Null(svc.SaveLetterhead(Encoding.ASCII.GetBytes("not a pdf at all"), out var bad));
            Assert.Contains("PDF", bad);
            var name = svc.SaveLetterhead(design, out var error);
            Assert.Null(error);
            Assert.True(DocumentService.IsLetterheadName(name!));
            var stored = File.ReadAllBytes(svc.LetterheadPath(name!)!);
            using (var kept = PdfPigDocument.Open(stored)) Assert.Equal(1, kept.NumberOfPages);

            foreach (var pages in new[] { "all", "first" })
            {
                var cfg = Cfg(new { letterhead = new { file = name, pages } }, out var cfgError);
                Assert.Null(cfgError);
                var long_ = DocumentService.Build(DocumentTypes.Invoice, J(new
                {
                    items = Enumerable.Range(1, 45).Select(i => (object)new { description = $"Item {i}", qty = 1, unit_price = 2 }).ToArray(),
                }), cfg, null, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, "INV-1").Content!;
                var pdf = DocumentRenderer.Render(long_, cfg, null, stored);
                using var doc = PdfPigDocument.Open(pdf);
                Assert.True(doc.NumberOfPages >= 2);
                var p1 = string.Join(" ", doc.GetPage(1).GetWords().Select(w => w.Text));
                var p2 = string.Join(" ", doc.GetPage(2).GetWords().Select(w => w.Text));
                Assert.Contains("LETTERHEAD", p1);
                Assert.Contains("Item 1", p1);   // the content still prints over it
                if (pages == "all") Assert.Contains("LETTERHEAD", p2);
                else Assert.DoesNotContain("LETTERHEAD", p2);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>A small PDF "design" with a line of text on each page (PDFsharp).</summary>
    internal static byte[] DesignPdf(string text, int pages = 1, string colorHex = "#1E3A8A")
    {
        DocumentFonts.EnsureInstalled();
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = pdf.AddPage();
            using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            var font = new PdfSharp.Drawing.XFont(DocumentFonts.Sans, 18, PdfSharp.Drawing.XFontStyleEx.Bold);
            var color = PdfSharp.Drawing.XColor.FromArgb(
                Convert.ToInt32(colorHex[1..3], 16), Convert.ToInt32(colorHex[3..5], 16), Convert.ToInt32(colorHex[5..7], 16));
            gfx.DrawString(text + (i > 0 ? $" page {i + 1}" : ""), font, new PdfSharp.Drawing.XSolidBrush(color), 40, 60);
            var small = new PdfSharp.Drawing.XFont(DocumentFonts.Sans, 9, PdfSharp.Drawing.XFontStyleEx.Regular);
            gfx.DrawString("12 Jalan Bunga, Kuala Lumpur", small, PdfSharp.Drawing.XBrushes.Gray, 40, 78);
            gfx.DrawString("Reg. No. 202601234567", small, PdfSharp.Drawing.XBrushes.Gray, 40, 90);
            gfx.DrawString("hello@acme.example  +60 12-345 6789", small, PdfSharp.Drawing.XBrushes.Gray, 40, 102);
            gfx.DrawString("INVOICE", font, new PdfSharp.Drawing.XSolidBrush(color), 450, 60);
            gfx.DrawString("Bank: Maybank 5123 4567 8901", small, PdfSharp.Drawing.XBrushes.Black, 40, 700);
            gfx.DrawString("Thank you for choosing Acme", small, PdfSharp.Drawing.XBrushes.Gray, 200, 800);
        }
        using var ms = new MemoryStream();
        pdf.Save(ms, false);
        return ms.ToArray();
    }
}
