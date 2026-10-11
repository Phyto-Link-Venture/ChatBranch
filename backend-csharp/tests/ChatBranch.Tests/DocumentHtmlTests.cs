using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Documents;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// A stand-in for headless Chromium: records every page it was asked to print and
/// returns a real PDF holding the page's VISIBLE text (tags stripped, entities decoded)
/// — so a test can read what the document says. <see cref="Available"/> false = a
/// server with no renderer.
/// </summary>
public sealed class FakeHtmlPdf : IHtmlPdfConverter
{
    public bool Available { get; set; } = true;
    public List<string> Pages { get; } = new();

    public Task<byte[]> ConvertAsync(string html, HtmlPdfPage page, CancellationToken ct = default)
    {
        if (!Available) throw new HtmlRendererUnavailableException();
        var hardened = HtmlPdfHardening.Harden(html, page);
        lock (Pages) Pages.Add(hardened);
        return Task.FromResult(Pdf(VisibleText(hardened), page.Size == "Letter"));
    }

    /// <summary>What a browser would show: no head / style, tags as line breaks, entities decoded.</summary>
    public static List<string> VisibleText(string html)
    {
        var s = Regex.Replace(html, @"<(head|style)\b.*?</\1\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>|</(p|div|tr|li|h\d|table)>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        return s.Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim()).Where(l => l.Length > 0).ToList();
    }

    private static byte[] Pdf(List<string> lines, bool letter)
    {
        DocumentFonts.EnsureInstalled();
        using var doc = new PdfDocument();
        var font = new XFont(DocumentFonts.Sans, 8);
        PdfPage? page = null;
        XGraphics? gfx = null;
        double y = 0;
        foreach (var line in lines.DefaultIfEmpty(" "))
        {
            if (page == null || y > 800)
            {
                gfx?.Dispose();
                page = doc.AddPage();
                page.Width = XUnit.FromPoint(letter ? 612 : 595);
                page.Height = XUnit.FromPoint(letter ? 792 : 842);
                gfx = XGraphics.FromPdfPage(page);
                y = 30;
            }
            gfx!.DrawString(DocumentRenderer.Clean(line), font, XBrushes.Black, new XPoint(30, y));
            y += 11;
        }
        gfx?.Dispose();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }
}

/// <summary>A test host with a fake HTML printer and a scripted design model (off = sandbox).</summary>
public class HtmlDocsFactory : ApiFactory
{
    public FakeHtmlPdf Printer { get; } = new();
    public FakeDesignLlm Llm { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IHtmlPdfConverter>();
            s.AddSingleton<IHtmlPdfConverter>(Printer);
            s.RemoveAll<ILlmClient>();
            s.AddSingleton<ILlmClient>(Llm);
        });
    }
}

/// <summary>
/// The Documents app's "Custom layout" (HTML templates): the <c>html</c> normalizer and its
/// field rules, every helper (money, words en/ms with sen and zero, dates, lines, nl2br),
/// escaping of customer values, the sanitizer, the data model, the AI tool schema with the
/// custom fields, HTML mode end to end through the reply lifecycle with a fake printer,
/// the clean refusal without a renderer, the AI draft / assist with a fake model (retry,
/// starter fallback), the editor's endpoints and — when a browser is installed — ONE real
/// Chromium render proving JavaScript and the network are off.
/// </summary>
public class DocumentHtmlTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private const string Minimal = """
        <!doctype html><html><head><style>@page { size: A4; margin: 12mm } body { font-family: Arial }</style></head>
        <body><h1>{{business.name}}</h1><p>{{document.type_label}} {{document.number}}</p><p>{{customer.name}}</p>
        {{#each items}}<div>{{index}}) {{description}} {{amount_text}}</div>{{/each}}
        <p>PRO {{fields.project}}</p><p>{{money totals.total "RM"}} · {{totals.total_in_words}}</p>
        {{#each document.paragraphs}}<p>{{this}}</p>{{/each}}</body></html>
        """;

    private static object Html(string? template = null, object? fields = null, string terms = "", string bank = "") => new
    {
        template = template ?? Minimal,
        fields = fields ?? new object[]
        {
            new { key = "project", label = "PRO", type = "text", required = true },
            new { key = "signatory_name", label = "Signatory", type = "text", @default = "Mr Ng" },
        },
        terms,
        bank_details = bank,
    };

    private static DocumentAppConfig Cfg(object? html = null, string layout = "html", object? extra = null)
    {
        var raw = JsonSerializer.SerializeToNode(extra ?? new { })!.AsObject();
        raw["app"] = "documents";
        raw["business_name"] ??= "Canmos Lighting";
        raw["address"] ??= "No. 22, Jalan Perkasa 2\n36000 Teluk Intan";
        raw["layout"] = layout;
        raw["html"] = JsonSerializer.SerializeToNode(html ?? Html());
        var cfg = DocumentAppConfig.From(JsonSerializer.SerializeToElement(raw), out var error);
        Assert.True(error == null, error);
        return cfg;
    }

    private static DocumentContent Invoice(DocumentAppConfig cfg, string customer = "Sentul Perdana Sdn Bhd", object? fields = null,
        Contact? contact = null) =>
        DocumentService.Build(DocumentTypes.Invoice, J(new
        {
            customer_name = customer,
            items = new[] { new { description = "40% BALANCE CLAIM\nRM 8,285.00 X 40% X 4 UNITS", qty = 4, unit_price = 3314 } },
            fields = fields ?? new { project = "SENTUL PERDANA SDN BHD" },
        }), cfg, contact, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, "CL-261001").Content!;

    private static string? Problem(object html, string layout = "html")
    {
        var raw = JsonSerializer.SerializeToNode(new { app = "documents" })!.AsObject();
        raw["layout"] = layout;
        raw["html"] = JsonSerializer.SerializeToNode(html);
        DocumentAppConfig.From(JsonSerializer.SerializeToElement(raw), out var error);
        return error;
    }

    // =========================================================================
    // the normalizer
    // =========================================================================

    [Fact]
    public void The_html_config_normalizes_and_round_trips()
    {
        var cfg = Cfg(new
        {
            template = Minimal, templates = new { letter = "<!doctype html><p>{{document.title}}</p>" },
            fields = new object[]
            {
                new { key = "project", label = "PRO" },
                new { key = "due_on", label = "Pay by", type = "date", @default = "2026-11-01" },
                new { key = "lots", label = "Lots", type = "number", @default = "14" },
            },
            page = new { size = "letter", margin_mm = new { top = 10, right = 12, bottom = 14, left = 10 } },
            terms = "Line one\nLine two", bank_details = "Maybank 123",
        });
        Assert.True(cfg.IsHtml);
        var h = cfg.Html!;
        Assert.Equal("Letter", h.PageSize);
        Assert.Equal("text", h.Fields[0].Type);            // the default type
        Assert.Equal("PRO", h.Fields[0].Label);
        Assert.Contains("{{document.title}}", h.TemplateFor(DocumentTypes.Letter));
        Assert.Equal(Minimal.Replace("\r\n", "\n"), h.TemplateFor(DocumentTypes.Invoice));
        var canon = JsonSerializer.Serialize(cfg.ToDictionary());
        var again = DocumentAppConfig.From(JsonSerializer.SerializeToElement(cfg.ToDictionary()), out var e2);
        Assert.Null(e2);
        Assert.Equal(canon, JsonSerializer.Serialize(again.ToDictionary()));
        Assert.Equal("html", again.ToDictionary()["layout"]);

        // switching back to the designer keeps the layout (and prints with the designer)
        var designer = Cfg(layout: "designer");
        Assert.False(designer.IsHtml);
        Assert.NotNull(designer.Html);
        Assert.Empty(designer.CustomFields);

        // a config that never had a custom layout keeps its exact old shape
        var plain = DocumentAppConfig.From(J(new { app = "documents" }), out _);
        Assert.False(plain.ToDictionary().ContainsKey("layout"));
        Assert.False(plain.ToDictionary().ContainsKey("html"));
    }

    [Theory]
    [InlineData("{\"template\":\"<p>x</p>\",\"colour\":\"red\"}", "unknown key 'html.colour'")]
    [InlineData("{\"template\":\"<p>x</p>\",\"fields\":[{\"key\":\"Project\"}]}", "must start with a lowercase letter")]
    [InlineData("{\"template\":\"<p>x</p>\",\"fields\":[{\"key\":\"notes\"}]}", "built-in name")]
    [InlineData("{\"template\":\"<p>x</p>\",\"fields\":[{\"key\":\"a\"},{\"key\":\"a\"}]}", "used twice")]
    [InlineData("{\"template\":\"<p>x</p>\",\"fields\":[{\"key\":\"a\",\"type\":\"colour\"}]}", "html.fields[0].type must be one of")]
    [InlineData("{\"template\":\"<p>x</p>\",\"fields\":[{\"key\":\"a\",\"type\":\"date\",\"default\":\"tomorrow\"}]}", "doesn't fit the date type")]
    [InlineData("{\"template\":\"<p>x</p>\",\"fields\":[{\"key\":\"a\",\"size\":3}]}", "unknown key 'html.fields[0].size'")]
    [InlineData("{\"template\":\"<p>{{{customer.name}}}</p>\"}", "prints raw HTML")]
    [InlineData("{\"template\":\"<p>{{> footer}}</p>\"}", "Partials")]
    [InlineData("{\"template\":\"<p>\\n{{#each items}}</p>\"}", "#each")]
    [InlineData("{\"template\":\"<p>{{shout customer.name}}</p>\"}", "shout")]
    [InlineData("{\"template\":\"<p>x</p>\",\"page\":{\"size\":\"A3\"}}", "html.page.size must be one of")]
    [InlineData("{\"template\":\"<p>x</p>\",\"letterhead\":{\"file\":\"../../etc/passwd\"}}", "uploaded letterhead PDF")]
    [InlineData("{\"template\":\"\"}", "layout html needs a template")]
    public void A_bad_custom_layout_is_refused_with_the_first_problem(string html, string expected)
    {
        var error = Problem(JsonDocument.Parse(html).RootElement);
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Field_limits_and_template_size_are_enforced()
    {
        var many = Enumerable.Range(1, 21).Select(i => new { key = $"f{i}", label = $"F{i}" }).ToArray();
        Assert.Contains("up to 20 fields", Problem(new { template = "<p>x</p>", fields = many }));
        Assert.Contains("larger than 200 KB", Problem(new { template = "<p>" + new string('x', 210 * 1024) + "</p>" }));
        Assert.Contains("layout must be designer or html", Problem(new { template = "<p>x</p>" }, layout: "fancy"));
        // a compile error names its line
        var line = Problem(new { template = "<p>a</p>\n<p>b</p>\n<p>{{#if x}}c</p>" });
        Assert.Contains("Line 3", line);
    }

    [Fact]
    public void Every_starter_passes_the_normalizer_and_prints_every_type_in_both_languages()
    {
        foreach (var starter in DocumentHtmlStarters.All)
        {
            var html = starter.Config().ToDictionary();
            Assert.Null(Problem(html));
            var cfg = Cfg(html);
            foreach (var type in DocumentTypes.Numbered)
                foreach (var lang in new[] { "en", "ms" })
                {
                    var page = DocumentHtmlRenderer.RenderHtml(
                        DocumentHtmlRenderer.Sample(type, cfg, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, lang), cfg, null);
                    Assert.Contains("Canmos Lighting", page);
                    if (type != DocumentTypes.Letter) Assert.Contains("Sample service", page);
                }
        }
        Assert.Equal("malaysian-classic", DocumentHtmlStarters.All[0].Id);
    }

    // =========================================================================
    // helpers
    // =========================================================================

    private static string R(string template, object? model = null, string language = "en") =>
        DocumentHtmlTemplating.Render(template, model ?? new Dictionary<string, object?>(), language);

    [Theory]
    [InlineData(13256, "en", "Thirteen Thousand Two Hundred And Fifty Six Only")]
    [InlineData(13256.50, "en", "Thirteen Thousand Two Hundred And Fifty Six And Fifty Sen Only")]
    [InlineData(0, "en", "Zero Only")]
    [InlineData(0.05, "en", "Five Sen Only")]
    [InlineData(1050, "en", "One Thousand And Fifty Only")]
    [InlineData(100, "en", "One Hundred Only")]
    [InlineData(2_000_015, "en", "Two Million And Fifteen Only")]
    [InlineData(2640, "en", "Two Thousand Six Hundred And Forty Only")]
    [InlineData(13256, "ms", "Tiga Belas Ribu Dua Ratus Lima Puluh Enam Sahaja")]
    [InlineData(13256.50, "ms", "Tiga Belas Ribu Dua Ratus Lima Puluh Enam Dan Lima Puluh Sen Sahaja")]
    [InlineData(0, "ms", "Kosong Sahaja")]
    [InlineData(1100, "ms", "Seribu Seratus Sahaja")]
    [InlineData(11, "ms", "Sebelas Sahaja")]
    [InlineData(1_000_000, "ms", "Satu Juta Sahaja")]
    [InlineData(10.10, "ms", "Sepuluh Dan Sepuluh Sen Sahaja")]
    public void Amounts_in_words(double amount, string language, string expected)
    {
        Assert.Equal(expected, DocumentHtmlTemplating.Words((decimal)amount, language));
        Assert.Equal(expected, R("{{words x}}", new Dictionary<string, object?> { ["x"] = (decimal)amount }, language));
    }

    [Fact]
    public void Money_number_date_and_text_helpers()
    {
        var m = new Dictionary<string, object?>
        {
            ["total"] = 13256m, ["qty"] = 1.5m, ["d"] = "2026-10-08", ["text"] = "First line\nSecond <b>\nThird",
            ["empty"] = "", ["list"] = new List<string>(), ["full"] = new List<string> { "x" }, ["n"] = 0,
        };
        Assert.Equal("13,256.00|RM 13,256.00", R("{{money total}}|{{money total \"RM\"}}", m));
        Assert.Equal("1.5|1.50|13,256", R("{{number qty}}|{{number qty 2}}|{{number total}}", m));
        Assert.Equal("08.10.2026|8 Oct 2026|8 Okt 2026|Thursday 8 October|Khamis 8 Oktober",
            R("{{date d \"dd.MM.yyyy\"}}|{{date d}}|", m) + R("{{date d}}|", m, "ms")
            + R("{{date d \"dddd d MMMM\"}}|", m) + R("{{date d \"dddd d MMMM\"}}", m, "ms"));
        Assert.Equal("First line|Second &lt;b&gt;\nThird", R("{{first_line text}}|{{rest_lines text}}", m));
        Assert.Equal("First line<br>Second &lt;b&gt;<br>Third", R("{{nl2br text}}", m));
        Assert.Throws<DocumentHtmlException>(() => R("{{{nl2br text}}}", m));   // raw is only for the logo
        Assert.Equal("FIRST LINE\nSECOND &lt;B&gt;\nTHIRD|first line", R("{{upper text}}|{{lower (first_line text)}}", m));
        Assert.Equal("1|2|", R("{{#each full}}{{inc @index}}|{{inc 1}}|{{/each}}", m));
        Assert.Equal("Y|N|Y|N", R("{{#eq d \"2026-10-08\"}}Y{{else}}N{{/eq}}|{{#ne d \"2026-10-08\"}}Y{{else}}N{{/ne}}|"
                                  + "{{#gt total 100}}Y{{/gt}}|{{#if (eq n 1)}}Y{{else}}N{{/if}}", m));
        Assert.Equal("fallback|x|ok", R("{{default empty \"fallback\"}}|{{#each (default list full)}}{{this}}{{/each}}|"
                                       + "{{#if (or empty list full)}}ok{{/if}}", m));
        Assert.Equal("Subtotal|Jumlah kecil|Dear Sir / Madam,|Tuan / Puan,",
            R("{{label \"subtotal\"}}|", m) + R("{{label \"subtotal\"}}|", m, "ms") + R("{{label \"dear\"}}|", m) + R("{{label \"dear\"}}", m, "ms"));
    }

    [Fact]
    public void Customer_values_are_escaped_and_never_become_markup()
    {
        var cfg = Cfg();
        const string evil = "<b>x</b><img src=x onerror=alert(1)>'\"`=";
        var page = DocumentHtmlRenderer.RenderHtml(Invoice(cfg, customer: evil), cfg, null);
        Assert.DoesNotContain("<b>x</b>", page);
        Assert.DoesNotContain("<img src=x", page);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;&lt;img src&#61;x onerror&#61;alert(1)&gt;&#39;&quot;&#96;&#61;", page);

        // printed: the customer's name appears as TEXT on the PDF
        var printer = new FakeHtmlPdf();
        var pdf = DocumentHtmlRenderer.RenderPdfAsync(Invoice(cfg, customer: evil), cfg, null, null, printer).GetAwaiter().GetResult();
        Assert.Contains("<b>x</b><img", DocumentRig.PdfText(pdf));

        // an AI-written description with markup is escaped too, inside nl2br as well
        var html = DocumentHtmlTemplating.Render("{{nl2br d}}", new Dictionary<string, object?> { ["d"] = "<script>a</script>\nb" }, "en");
        Assert.Equal("&lt;script&gt;a&lt;/script&gt;<br>b", html);
    }

    // =========================================================================
    // the sanitizer
    // =========================================================================

    [Fact]
    public void The_sanitizer_strips_code_frames_handlers_and_remote_urls()
    {
        var dirty = """
            <!doctype html><html><head>
            <meta http-equiv="refresh" content="0;url=https://evil.example"><meta charset="utf-8"><base href="https://evil.example/">
            <link rel="stylesheet" href="https://evil.example/x.css">
            <style>@import url(https://evil.example/a.css); @import "b.css"; .a { background: url('https://evil.example/bg.png') }
              .b { background: url(data:image/png;base64,AAAA) } .c { background: u\72l(https://evil.example/esc.png) }
              .d { background-image: image-set("https://evil.example/x.png" 1x) }</style>
            <script>document.write('JS RAN')</script></head>
            <body onload="x()"><img src="https://evil.example/t.png" onerror="alert(1)"><img src="data:image/png;base64,AAAA">
            <a href="javascript:alert(1)">bad</a><a href="&#106;avascript:alert(1)">bad2</a><a href="#top">ok</a><a href="mailto:a@b.co">mail</a>
            <iframe src="https://evil.example"></iframe><object data="x"></object><embed src="x"><svg><a xlink:href="javascript:x">s</a></svg>
            <div style="background:url(http://evil.example/s.png)">styled</div><img srcset="https://evil.example/1.png 1x">
            <scr<script>x</script>ipt>alert(2)</script><img src="file:///etc/passwd"><img src="relative.png">
            </body></html>
            """;
        var clean = HtmlSanitizer.Sanitize(dirty);
        foreach (var gone in new[]
                 {
                     "<script", "JS RAN", "onload", "onerror", "evil.example", "javascript:", "&#106;avascript", "<iframe",
                     "<object", "<embed", "<link", "<base", "http-equiv", "srcset", "@import", "image-set", "file:", "relative.png",
                 })
            Assert.DoesNotContain(gone, clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<meta charset=\"utf-8\">", clean);
        Assert.Contains("<img src=\"data:image/png;base64,AAAA\">", clean);
        Assert.Contains("url(data:image/png;base64,AAAA)", clean);
        Assert.Contains("href=\"#top\"", clean);
        Assert.Contains("href=\"mailto:a@b.co\"", clean);
        Assert.Contains(">styled<", clean);
        Assert.Equal(clean, HtmlSanitizer.Sanitize(clean));   // idempotent

        // template mode keeps the server-built logo (decided again on the rendered page)
        var tpl = HtmlSanitizer.Sanitize("<img src=\"{{{business.logo}}}\"><img src=\"https://x.example/{{a}}\">", template: true);
        Assert.Contains("src=\"{{{business.logo}}}\"", tpl);
        var rendered = HtmlSanitizer.Sanitize("<img src=\"data:image/png;base64,AAAA\"><img src=\"https://x.example/1\">");
        Assert.Contains("data:image/png", rendered);
        Assert.DoesNotContain("x.example", rendered);

        // a stored template is sanitized when the config is saved
        var cfg = Cfg(Html("<!doctype html><p>{{business.name}}</p><script>alert(1)</script><img src=\"https://t.example/p.png\" onerror=\"x\">"));
        Assert.DoesNotContain("<script", cfg.Html!.Template);
        Assert.DoesNotContain("t.example", cfg.Html.Template);
    }

    [Fact]
    public void The_printer_hardens_every_page_with_a_policy_that_blocks_scripts_and_fetches()
    {
        var page = HtmlPdfHardening.Harden("<!doctype html><html><body><p>x</p></body></html>", new HtmlPdfPage("Letter", 1, 2, 3, 4));
        Assert.StartsWith("<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\"", page);
        Assert.Contains("default-src 'none'", page);
        Assert.Contains("script-src 'none'", page);
        Assert.Contains("@page{size:Letter;margin:1mm 2mm 3mm 4mm}", page);
        Assert.StartsWith("<!doctype html>", HtmlPdfHardening.Harden("<p>no doctype</p>", new HtmlPdfPage()));
    }

    // =========================================================================
    // the data model
    // =========================================================================

    [Fact]
    public void The_data_model_carries_business_document_customer_items_totals_fields_and_terms()
    {
        var cfg = Cfg(Html(terms: "Term one\n\nTerm two", bank: ""), extra: new
        {
            business_name = "Canmos Lighting", phone = "6016 - 205 3977", tax_rate = 8, tax_label = "SST",
            template = new { notes = new { bank_details = "HLB 152 010 033 51" } },   // the designer's words fill in
        });
        var c = Invoice(cfg, fields: new { project = "PT 9815" });
        var m = DocumentHtmlModel.Build(c, cfg, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
        var business = (Dictionary<string, object?>)m["business"]!;
        Assert.Equal(new List<string> { "No. 22, Jalan Perkasa 2", "36000 Teluk Intan" }, business["address_lines"]);
        Assert.StartsWith("data:image/png;base64,", (string)business["logo"]!);
        Assert.Equal("HLB 152 010 033 51", business["bank_details"]);
        var document = (Dictionary<string, object?>)m["document"]!;
        Assert.Equal("CL-261001", document["number"]);
        Assert.Equal("RM", document["currency_symbol"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", (string)document["issue_date"]!);
        var item = ((List<Dictionary<string, object?>>)m["items"]!)[0];
        Assert.Equal(1, item["index"]);
        Assert.Equal("40% BALANCE CLAIM", item["description_first_line"]);
        Assert.Equal(new List<string> { "RM 8,285.00 X 40% X 4 UNITS" }, item["description_rest"]);
        Assert.Equal("3,314.00", item["unit_price_text"]);
        Assert.Equal("13,256.00", item["amount_text"]);
        var totals = (Dictionary<string, object?>)m["totals"]!;
        Assert.Equal("14,316.48", totals["total_text"]);   // 13,256 + SST 8 %
        Assert.Equal("Fourteen Thousand Three Hundred And Sixteen And Forty Eight Sen Only", totals["total_in_words_en"]);
        Assert.Equal("Empat Belas Ribu Tiga Ratus Enam Belas Dan Empat Puluh Lapan Sen Sahaja", totals["total_in_words_ms"]);
        var fields = (Dictionary<string, object?>)m["fields"]!;
        Assert.Equal("PT 9815", fields["project"]);
        Assert.Equal("Mr Ng", fields["signatory_name"]);   // its default
        Assert.Equal(new List<string> { "Term one", "Term two" }, m["terms"]);
        // the reference panel lists the custom fields
        Assert.Contains(DocumentHtmlModel.Reference(cfg.Html), g => g.Id == "fields" && g.Vars.Any(v => v.Path == "fields.project"));
    }

    [Fact]
    public void Custom_fields_are_typed_required_and_defaulted_when_a_document_is_built()
    {
        var cfg = Cfg(Html(fields: new object[]
        {
            new { key = "project", label = "PRO", required = true },
            new { key = "lots", label = "Lots", type = "number" },
            new { key = "handover", label = "Handover date", type = "date" },
            new { key = "signatory_name", label = "Signatory", @default = "Mr Ng" },
        }));
        DocumentService.BuildResult B(object fields) => DocumentService.Build(DocumentTypes.Invoice,
            J(new { items = new[] { new { description = "x", unit_price = 1 } }, fields }), cfg, null,
            DateTimeOffset.UtcNow, TimeZoneInfo.Utc, "");
        var missing = B(new { lots = 3 });
        Assert.Contains("fields.project (PRO) is required", missing.Error);
        Assert.Contains("Ask the customer", missing.Advice);
        Assert.Contains("must be a number", B(new { project = "P", lots = "many" }).Error);
        Assert.Contains("YYYY-MM-DD", B(new { project = "P", handover = "next week" }).Error);
        var ok = B(new { project = "P", lots = "RM 1,400", handover = "2026-12-01T00:00:00" }).Content!;
        Assert.Equal("1400", ok.Fields["lots"]);
        Assert.Equal("2026-12-01", ok.Fields["handover"]);
        Assert.Equal("Mr Ng", ok.Fields["signatory_name"]);

        // the designer layout ignores custom fields entirely
        var designer = Cfg(layout: "designer");
        Assert.Null(DocumentService.Build(DocumentTypes.Invoice, J(new { items = new[] { new { description = "x", unit_price = 1 } } }),
            designer, null, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, "").Error);
    }

    // =========================================================================
    // the AI tool
    // =========================================================================

    [Fact]
    public void The_ai_tools_take_the_custom_fields_as_arguments()
    {
        var tools = DocumentService.ToolDefinitions("document", Cfg()).ToList();
        var invoice = tools.Single(t => t.Action == DocumentTypes.Invoice).Tool.Parameters;
        var fields = invoice.GetProperty("properties").GetProperty("fields");
        Assert.Equal("object", fields.GetProperty("type").GetString());
        Assert.Equal("PRO — required", fields.GetProperty("properties").GetProperty("project").GetProperty("description").GetString());
        Assert.Contains("default: Mr Ng", fields.GetProperty("properties").GetProperty("signatory_name").GetProperty("description").GetString());
        Assert.Equal(new[] { "project" }, fields.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("fields", invoice.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.True(invoice.GetProperty("properties").TryGetProperty("title", out _));   // a subject line ("REF")
        var letter = tools.Single(t => t.Action == DocumentTypes.Letter).Tool.Parameters;
        Assert.True(letter.GetProperty("properties").TryGetProperty("fields", out _));

        // the designer layout's tools are exactly as before
        var plain = DocumentService.ToolDefinitions("document", DocumentAppConfig.From(J(new { app = "documents" }), out _)).ToList();
        var p = plain.Single(t => t.Action == DocumentTypes.Invoice).Tool.Parameters;
        Assert.False(p.GetProperty("properties").TryGetProperty("fields", out _));
        Assert.False(p.GetProperty("properties").TryGetProperty("title", out _));
        Assert.Equal(new[] { "items" }, p.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
    }

    private static Func<IReadOnlyList<LlmMessage>, IReadOnlyList<LlmTool>?, LlmChatResult> InvoiceWith(object fields) => (msgs, _) =>
    {
        var last = msgs.LastOrDefault();
        if (last?.Role != "tool")
            return ToolScriptLlm.Calls(("document_create_invoice", JsonSerializer.Serialize(new
            {
                title = "40% Balance Claim",
                items = new[] { new { description = "40% BALANCE CLAIM\nPT 9815 to PT 9818", qty = 4, unit_price = 3314 } },
                fields,
            })));
        var json = JsonDocument.Parse(last.Content!).RootElement;
        return json.GetProperty("ok").GetBoolean()
            ? ToolScriptLlm.Says($"Here is invoice {json.GetProperty("number").GetString()} — total {json.GetProperty("total_label").GetString()}.")
            : ToolScriptLlm.Says("ERR " + json.GetProperty("error").GetString());
    };

    private static AgentSkill HtmlSkill(DocumentRig r) => r.Skill(new
    {
        app = "documents", business_name = "Canmos Lighting", currency = "MYR", layout = "html", html = Html(),
    });

    [Fact]
    public void Html_mode_makes_numbers_stores_and_sends_the_pdf_exactly_like_the_designer()
    {
        var printer = new FakeHtmlPdf();
        using var r = new DocumentRig(InvoiceWith(new { project = "SENTUL PERDANA SDN BHD" }), printer);
        r.Contact();
        HtmlSkill(r);
        var acct = r.LiveAccount();
        r.Say("60123000001", "Can I have the invoice for the balance claim?", acct.Id);

        var doc = Assert.Single(r.Documents());
        Assert.Equal(DocumentStatus.Sent, doc.Status);
        Assert.Equal("INV-0001", doc.Number);
        Assert.Equal(13256m, doc.Total);
        Assert.Equal("file", doc.Detail);
        Assert.StartsWith("text:Here is invoice INV-0001 — total MYR 13,256.00", r.Wa.Sent[0]);
        Assert.StartsWith("upload:application/pdf:INV-0001.pdf:", r.Wa.Sent[1]);
        Assert.StartsWith("media:document:INV-0001.pdf:📄 Invoice INV-0001", r.Wa.Sent[2]);
        var data = JsonSerializer.SerializeToElement(doc.Data);
        Assert.Equal("SENTUL PERDANA SDN BHD", data.GetProperty("fields").GetProperty("project").GetString());

        // what was printed: the custom layout with the AI's values, through the printer
        var page = Assert.Single(printer.Pages);
        Assert.Contains("PRO SENTUL PERDANA SDN BHD", page);
        Assert.Contains("RM 13,256.00 · Thirteen Thousand Two Hundred And Fifty Six Only", page);
        Assert.Contains("Content-Security-Policy", page);
        var file = r.Messages("60123000001").Single(m => m.MediaType == "document");
        var pdf = File.ReadAllBytes(r.Media.Resolve("whatsapp", file.MediaPath!)!);
        Assert.Contains("INV-0001", DocumentRig.PdfText(pdf));
        Assert.Contains("Aisha Rahman", DocumentRig.PdfText(pdf));
    }

    [Fact]
    public void A_missing_required_field_is_refused_to_the_model_and_nothing_is_numbered()
    {
        using var r = new DocumentRig(InvoiceWith(new { }));
        r.Contact();
        HtmlSkill(r);
        var acct = r.LiveAccount();
        r.Say("60123000001", "Invoice please", acct.Id);
        Assert.Empty(r.Documents());
        Assert.Contains(r.Wa.Sent, s => s.Contains("fields.project (PRO) is required"));
    }

    [Fact]
    public void Without_a_renderer_html_mode_is_refused_cleanly_never_printed_with_the_designer()
    {
        var printer = new FakeHtmlPdf { Available = false };
        using var r = new DocumentRig(InvoiceWith(new { project = "P" }), printer);
        r.Contact();
        var skill = HtmlSkill(r);
        var acct = r.LiveAccount();
        r.Say("60123000001", "Invoice please", acct.Id);
        Assert.Empty(r.Documents());
        Assert.Contains(r.Wa.Sent, s => s.Contains("custom layout is unavailable on this server"));
        Assert.Empty(printer.Pages);

        // the skill form's preview says so too
        var cfg = DocumentAppConfig.From(skill.Config);
        var preview = r.Docs.PreviewSampleAsync(DocumentTypes.Invoice, cfg, DateTimeOffset.UtcNow, TimeZoneInfo.Utc)
            .GetAwaiter().GetResult();
        Assert.Null(preview.Url);
        Assert.Contains("custom layout is unavailable on this server", preview.Error);
    }

    [Fact]
    public void The_letterhead_is_drawn_under_a_custom_layout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-html-lh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new DocumentService(null!, null!, new ChatBranch.Data.Channels.MediaStore(dir), html: new FakeHtmlPdf());
            var letterhead = DocumentTemplateTests.DesignPdf("ACME LETTERHEAD");
            var name = svc.SaveLetterhead(letterhead, out var error);
            Assert.Null(error);
            var html = Html();
            var node = JsonSerializer.SerializeToNode(html)!.AsObject();
            node["letterhead"] = JsonSerializer.SerializeToNode(new { file = name, pages = "all" });
            var cfg = Cfg(node);
            var pdf = svc.RenderAsync(Invoice(cfg), cfg, null).GetAwaiter().GetResult();
            var text = DocumentRig.PdfText(pdf);
            Assert.Contains("LETTERHEAD", text);
            Assert.Contains("CL-261001", text);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // =========================================================================
    // the AI: draft from a design, Ask AI
    // =========================================================================

    /// <summary>A PDF that reads like a formal Malaysian invoice (made with MigraDoc).</summary>
    private static byte[] MalaysianPdf()
    {
        DocumentFonts.EnsureInstalled();
        var doc = new MigraDoc.DocumentObjectModel.Document();
        doc.Styles["Normal"]!.Font.Name = DocumentFonts.Sans;
        var s = doc.AddSection();
        foreach (var line in new[]
                 {
                     "CANMOS LIGHTING", "No. 22, JALAN PERKASA 2, TAMAN PERKASA", "TEL: 6016 - 205 3977", "INVOICE",
                     "Ref No : CL-261001", "Attn : Sentul Perdana Sdn Bhd", "Dear Sir / Madam,", "NO DESCRIPTION QTY RATE (RM) AMOUNT (RM)",
                     "Ringgit Malaysia : Thirteen Thousand Only", "NOTE: -", "1) General Sales term and conditions apply",
                     "2) We reserve the right to charge interest", "3) Hong Leong Bank Account No : HLB 152 010 033 51",
                     "Your's faithfully,",
                 })
            s.AddParagraph(line);
        var renderer = new MigraDoc.Rendering.PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static DocumentTemplateAi.Upload Upload(byte[] pdf) => DocumentTemplateAi.Sniff(pdf, out _)!;

    [Fact]
    public async Task Without_a_model_the_draft_is_the_closest_starter_filled_from_the_pdf()
    {
        using var t = new TestDb();
        var ai = new DocumentHtmlAi(new SandboxLlmClient(), t.Db);
        var d = await ai.DraftAsync(Upload(MalaysianPdf()), "", null);
        Assert.Equal("starter", d.Mode);
        Assert.Equal("malaysian-classic", d.Starter);
        Assert.Contains("No AI model is configured", d.Note);
        Assert.Equal("General Sales term and conditions apply\nWe reserve the right to charge interest", d.Html.Terms);
        Assert.Equal("Hong Leong Bank Account No : HLB 152 010 033 51", d.Html.BankDetails);
        Assert.Equal("CANMOS LIGHTING", d.Business["business_name"]);
        Assert.Contains(d.Html.Fields, f => f.Key == "project");
        Assert.Contains("{{label \"amount_in_words\"}}", d.Html.Template);
    }

    [Fact]
    public async Task A_model_draft_is_validated_and_retried_once_with_the_problem()
    {
        using var t = new TestDb();
        var llm = new FakeDesignLlm { Enabled = true };
        var answers = new Queue<string>(new[]
        {
            // 1st: prints a customer value raw — refused
            JsonSerializer.Serialize(new { html = "<!doctype html><p>{{{customer.name}}}</p>", summary = "raw" }),
            // 2nd: fine
            JsonSerializer.Serialize(new
            {
                html = "<!doctype html><h1>{{business.name}}</h1><p>PRO : {{fields.project}}</p>{{#each items}}<p>{{description}}</p>{{/each}}",
                fields = new[] { new { key = "project", label = "PRO" } }, terms = "Term A", bank_details = "Bank B",
                business = new { business_name = "Canmos Lighting", phone = "6016" }, summary = "Reproduced the invoice.",
            }),
        });
        llm.Answer = _ => answers.Dequeue();
        var ai = new DocumentHtmlAi(llm, t.Db);
        var d = await ai.DraftAsync(Upload(MalaysianPdf()), "keep it formal", null);
        Assert.Equal("ai", d.Mode);
        Assert.Equal("Reproduced the invoice.", d.Summary);
        Assert.Contains("{{fields.project}}", d.Html.Template);
        Assert.Equal("Term A", d.Html.Terms);
        Assert.Equal("Canmos Lighting", d.Business["business_name"]);
        Assert.Equal(2, llm.Seen.Count);
        Assert.Contains("prints raw HTML", llm.Seen[1].Last().Content);       // the retry names the problem
        Assert.Contains("keep it formal", llm.Seen[0][0].Content);
        Assert.Contains("NOTE: -", llm.Seen[0][0].Content);                     // the text layer went along
        Assert.Equal(2, t.Db.LlmUsage.Count(u => u.Feature == DocumentTemplateAi.Feature));

        // never valid → the starter, saying so
        llm.Answer = _ => "{\"html\": \"<p>{{#if x}}</p>\"}";
        var bad = await ai.DraftAsync(Upload(MalaysianPdf()), "", null);
        Assert.Equal("starter", bad.Mode);
        Assert.Contains("didn't pass the checks", bad.Note);
    }

    [Fact]
    public async Task Ask_ai_edits_the_layout_or_leaves_it_unchanged_without_a_model()
    {
        using var t = new TestDb();
        var fields = new List<DocField> { new("project", "PRO", "text", false, "") };
        var sandbox = await new DocumentHtmlAi(new SandboxLlmClient(), t.Db).AssistAsync(Minimal, fields, "make it blue");
        Assert.Equal("sandbox", sandbox.Mode);
        Assert.Equal(Minimal, sandbox.Template);
        Assert.Contains("no AI model is configured", sandbox.Note);

        var llm = new FakeDesignLlm
        {
            Enabled = true,
            Answer = _ => JsonSerializer.Serialize(new
            {
                html = Minimal.Replace("<h1>", "<h1 style=\"color:#1D4ED8\">"),
                fields = new[] { new { key = "project", label = "PRO" }, new { key = "po_number", label = "PO" } },
                summary = "Blue business name; added a PO field.",
            }),
        };
        var edited = await new DocumentHtmlAi(llm, t.Db).AssistAsync(Minimal, fields, "make the name blue and add a PO field");
        Assert.Equal("ai", edited.Mode);
        Assert.Contains("color:#1D4ED8", edited.Template);
        Assert.Equal(new[] { "project", "po_number" }, edited.Fields.Select(f => f.Key));
        Assert.Contains("INSTRUCTION", llm.Seen[0][0].Content);
    }

    // =========================================================================
    // ONE real Chromium render (CI has Google Chrome; skipped quietly without a browser)
    // =========================================================================

    [Fact]
    public void The_browser_search_prefers_official_chrome_and_honours_the_setting()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb-browsers-" + Guid.NewGuid().ToString("N"));
        var chromiumDir = Path.Combine(root, "a");
        var chromeDir = Path.Combine(root, "b");
        try
        {
            Directory.CreateDirectory(chromiumDir);
            Directory.CreateDirectory(chromeDir);
            File.WriteAllText(Path.Combine(chromiumDir, "chromium"), "");
            File.WriteAllText(Path.Combine(chromeDir, "google-chrome"), "");
            var path = chromiumDir + Path.PathSeparator + chromeDir;

            // GitHub's runner: an unofficial Chromium snapshot earlier on the PATH than Chrome stable
            Assert.Equal(Path.Combine(chromeDir, "google-chrome"), ChromiumHtmlPdfConverter.FindBinary(null, path));
            Assert.Equal(Path.Combine(chromiumDir, "chromium"), ChromiumHtmlPdfConverter.FindBinary(null, chromiumDir));
            // the setting wins: a name on the PATH, an absolute path, or off
            Assert.Equal(Path.Combine(chromiumDir, "chromium"), ChromiumHtmlPdfConverter.FindBinary("chromium", path));
            Assert.Equal(Path.Combine(chromiumDir, "chromium"), ChromiumHtmlPdfConverter.FindBinary(Path.Combine(chromiumDir, "chromium"), ""));
            Assert.Null(ChromiumHtmlPdfConverter.FindBinary("off", path));
            Assert.Null(ChromiumHtmlPdfConverter.FindBinary(null, Path.Combine(root, "nothing-here")));
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp */ } }
    }

    private static string? Browser()
    {
        var found = ChromiumHtmlPdfConverter.FindBinary(Environment.GetEnvironmentVariable("DOCUMENT_HTML_CHROMIUM"));
        if (found != null) return found;
        // a developer machine with Playwright's Chromium
        try
        {
            return Directory.Exists("/opt/pw-browsers")
                ? Directory.EnumerateDirectories("/opt/pw-browsers", "chromium-*")
                    .Select(d => Path.Combine(d, "chrome-linux", "chrome")).FirstOrDefault(File.Exists)
                : null;
        }
        catch { return null; }
    }

    [Fact]
    public async Task Real_chromium_prints_the_layout_with_javascript_off_and_no_network()
    {
        var browser = Browser();
        if (browser == null) return; // no browser on this machine — the fake covers the rest
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var hits = 0;
        var accept = Task.Run(async () =>
        {
            try { while (true) { using var c = await listener.AcceptTcpClientAsync(); Interlocked.Increment(ref hits); } }
            catch { /* stopped */ }
        });
        // what this proves is no JavaScript and no network, not speed: a 2-vCPU CI runner busy with the
        // whole suite took most of the production 30 s limit just to start Chrome
        var chrome = new ChromiumHtmlPdfConverter(browser, TimeSpan.FromMinutes(2));

        // straight to the printer — no sanitizer in front: the printer itself keeps JS and the network off
        var raw = $$"""
            <!doctype html><html><head><meta charset="utf-8"><style>@page { size: A4; margin: 15mm }</style></head><body>
            <h1 id="h">STATIC HEADING</h1><p>Ringgit Malaysia : Thirteen Thousand Only</p>
            <script>document.getElementById('h').textContent = 'SCRIPT RAN'; document.write('SCRIPTWROTE');</script>
            <img src="http://127.0.0.1:{{port}}/pixel.png"><img src="http://localhost:{{port}}/local.png">
            <div style="background:url(http://127.0.0.1:{{port}}/bg.png)">bg</div>
            <link rel="stylesheet" href="http://127.0.0.1:{{port}}/x.css"><iframe src="http://127.0.0.1:{{port}}/frame"></iframe>
            </body></html>
            """;
        var pdf = await chrome.ConvertAsync(raw, new HtmlPdfPage());
        var text = DocumentRig.PdfText(pdf);
        Assert.Contains("STATIC HEADING", text);
        Assert.Contains("Thirteen Thousand Only", text);
        Assert.DoesNotContain("SCRIPT RAN", text);
        Assert.DoesNotContain("SCRIPTWROTE", text);
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref hits));

        // the Malaysian classic through the whole pipeline: the reference's own words, on A4
        var html = DocumentHtmlStarters.Get("malaysian-classic")!.Config().ToDictionary();
        html["terms"] = "General Sales term and conditions apply";
        html["bank_details"] = "Hong Leong Bank Account No : HLB 152 010 033 51";
        var cfg = Cfg(html, extra: new { business_name = "CANMOS LIGHTING", invoice_due_days = 0 });
        var content = DocumentService.Build(DocumentTypes.Invoice, J(new
        {
            customer_name = "Sentul Perdana Sdn Bhd <script>x</script>",
            title = "40% Balance Claim - Electrical Services (Type RT3) PT 9815 to PT 9818",
            items = new[] { new { description = "40% BALANCE CLAIM ON SUPPLY, DELIVERY,\nRM 8,285.00 X 40% X 4 UNITS", qty = 4, unit_price = 3314 } },
            fields = new { project = "SENTUL PERDANA SDN BHD", signatory_name = "Mr Ng" },
        }), cfg, null, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, "CL-261001").Content!;
        var doc = await DocumentHtmlRenderer.RenderPdfAsync(content, cfg, null, null, chrome);
        var words = DocumentRig.PdfText(doc);
        foreach (var expected in new[]
                 {
                     "CANMOS LIGHTING", "INVOICE", "CL-261001", "Sentul Perdana Sdn Bhd <script>x</script>", "SENTUL PERDANA SDN BHD",
                     "DESCRIPTION", "13,256.00", "Thirteen Thousand Two Hundred And Fifty Six Only", "Total Payment (RM) :",
                     "NOTE: -", "HLB 152 010 033 51", "DIRECTOR", "Mr Ng", "Acknowledge Received",
                 })
            Assert.Contains(expected, words);
        using (var read = UglyToad.PdfPig.PdfDocument.Open(doc))
        {
            Assert.InRange(read.GetPage(1).Width, 594, 597);
            Assert.InRange(read.GetPage(1).Height, 840, 843);
        }
        listener.Stop();
        Assert.Equal(0, Volatile.Read(ref hits));
    }
}

/// <summary>The custom layout's API: the editor's preview / draft / assist, the catalog, the PUT, the create form.</summary>
public class DocumentHtmlApiTests : IClassFixture<HtmlDocsFactory>
{
    private readonly HtmlDocsFactory _f;
    public DocumentHtmlApiTests(HtmlDocsFactory f) => _f = f;

    private static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string Starter => DocumentHtmlStarters.Get("malaysian-classic")!.Template;

    [Fact]
    public async Task The_catalog_lists_the_renderer_the_starters_and_the_reference()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var apps = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills/apps");
        var docs = apps.GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("app").GetString() == "documents");
        Assert.Equal("available", docs.GetProperty("html_renderer").GetString());
        Assert.Equal(new[] { "malaysian-classic", "clean-modern", "letter" },
            docs.GetProperty("html_starters").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        var reference = docs.GetProperty("html_reference");
        Assert.Contains(reference.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("vars").EnumerateArray()),
            v => v.GetProperty("path").GetString() == "totals.total_in_words");
        Assert.Contains(reference.GetProperty("helpers").EnumerateArray(), h => h.GetProperty("name").GetString() == "words");
        Assert.Contains("yours_faithfully", reference.GetProperty("labels").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task The_instant_preview_renders_sanitized_html_or_the_error_with_its_line()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var ok = await Ok(await admin.PostAsJsonAsync("/api/aicore/skills/documents/html/preview", new
        {
            html = Starter + "<script>alert(1)</script>", type = "quotation", language = "ms",
            fields = new[] { new { key = "project", label = "PRO", @default = "TAMAN PERKASA" } },
            config = new { app = "documents", business_name = "Canmos Lighting", html = new { terms = "Term one", bank_details = "HLB 1" } },
        }));
        Assert.True(ok.GetProperty("ok").GetBoolean(), ok.ToString());
        var html = ok.GetProperty("html").GetString()!;
        Assert.Contains("Canmos Lighting", html);
        Assert.Contains("SEBUT HARGA", html);              // a quotation, in Malay
        Assert.Contains("TAMAN PERKASA", html);
        Assert.Contains("Term one", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("Content-Security-Policy", html);

        var bad = await Ok(await admin.PostAsJsonAsync("/api/aicore/skills/documents/html/preview", new
        {
            html = "<!doctype html>\n<p>one</p>\n<p>{{money}</p>",
        }));
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.Equal(3, bad.GetProperty("line").GetInt32());
        Assert.StartsWith("Line 3", bad.GetProperty("error").GetString());

        // agents can't use the editor
        var agent = await Platform.As(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/aicore/skills/documents/html/preview", new { html = Starter })).StatusCode);
    }

    [Fact]
    public async Task Draft_and_assist_answer_in_sandbox_and_refuse_bad_input()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        _f.Llm.Enabled = false;
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4 not really a pdf"));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, "file", "design.pdf");
        var draft = await Ok(await admin.PostAsync("/api/aicore/skills/documents/html/draft", form));
        Assert.Equal("starter", draft.GetProperty("mode").GetString());
        Assert.False(string.IsNullOrEmpty(draft.GetProperty("html").GetProperty("template").GetString()));

        var gif = new MultipartFormDataContent();
        gif.Add(new ByteArrayContent(Encoding.ASCII.GetBytes("GIF89a....")), "file", "x.gif");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/aicore/skills/documents/html/draft", gif)).StatusCode);

        var assist = await Ok(await admin.PostAsJsonAsync("/api/aicore/skills/documents/html/assist", new
        {
            html = Starter, fields = Array.Empty<object>(), instruction = "make the title red",
        }));
        Assert.Equal("sandbox", assist.GetProperty("mode").GetString());
        Assert.Equal(Starter, assist.GetProperty("template").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/aicore/skills/documents/html/assist",
            new { html = Starter, instruction = "" })).StatusCode);
    }

    [Fact]
    public async Task The_skill_stores_a_custom_layout_and_an_operator_makes_a_document_with_its_fields()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var existing = (await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills")).GetProperty("skills")
            .EnumerateArray().Where(s => !s.TryGetProperty("entity", out var e) || e.ValueKind == JsonValueKind.Null)
            .Select(s => JsonSerializer.Deserialize<Dictionary<string, object?>>(s.GetRawText())!).ToList();
        existing.RemoveAll(s => s["id"]?.ToString() == "docs-html");
        Dictionary<string, object?> Skill(object html) => new()
        {
            ["id"] = "docs-html", ["name"] = "Documents", ["kind"] = "app", ["position"] = 0, ["enabled"] = true,
            ["config"] = new { app = "documents", business_name = "Canmos Lighting", prefixes = new { invoice = "CL-" }, layout = "html", html },
        };

        // a template that can't print is refused by the PUT, with the reason
        var bad = await admin.PutAsJsonAsync("/api/aicore/skills",
            new { skills = existing.Append(Skill(new { template = "<p>{{{customer.name}}}</p>" })).ToList() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("prints raw HTML", await bad.Content.ReadAsStringAsync());

        var fields = new object[]
        {
            new { key = "project", label = "PRO", required = true },
            new { key = "signatory_name", label = "Signatory", @default = "Mr Ng" },
        };
        var put = await Ok(await admin.PutAsJsonAsync("/api/aicore/skills", new
        {
            skills = existing.Append(Skill(new { template = Starter + "<script>x</script>", fields })).ToList(),
        }));
        var cfg = put.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "docs-html").GetProperty("config");
        Assert.Equal("html", cfg.GetProperty("layout").GetString());
        Assert.DoesNotContain("<script", cfg.GetProperty("html").GetProperty("template").GetString());

        // a conversation: the create form lists the custom fields
        var phone = "6013" + Random.Shared.Next(1000000, 9999999);
        Assert.True((await admin.PostAsJsonAsync("/api/whatsapp/simulate", new { channel = "whatsapp", key = phone, text = "Hi" })).IsSuccessStatusCode);
        var form = await Ok(await admin.GetAsync($"/api/documents/form?channel=whatsapp&key={phone}"));
        var invoice = form.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("type").GetString() == "invoice");
        Assert.Equal("html", invoice.GetProperty("layout").GetString());
        Assert.Equal("docs-html", invoice.GetProperty("skill").GetProperty("id").GetString());
        Assert.Equal(new[] { "project", "signatory_name" },
            invoice.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/documents/form?channel=whatsapp&key=nobody")).StatusCode);

        // required field missing → 400; given → the PDF is printed by the custom layout
        var missing = await admin.PostAsJsonAsync("/api/documents", new
        {
            channel = "whatsapp", key = phone, type = "invoice",
            fields = new { items = new[] { new { description = "Conduit", qty = 14, unit_price = 120 } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("fields.project", await missing.Content.ReadAsStringAsync());

        var before = _f.Printer.Pages.Count;
        var made = await Ok(await admin.PostAsJsonAsync("/api/documents", new
        {
            channel = "whatsapp", key = phone, type = "invoice",
            fields = new
            {
                customer_name = "Liew Khai Xen",
                items = new[] { new { description = "Conduit for PV solar system - PT 9861 to PT 9874 (14 lots)", qty = 14, unit_price = 120 } },
                fields = new { project = "PHYTOLINK" },
            },
        }));
        var doc = made.GetProperty("document");
        Assert.StartsWith("CL-", doc.GetProperty("number").GetString());
        Assert.Equal(1680m, doc.GetProperty("total").GetDecimal());
        var page = _f.Printer.Pages.Skip(before).Single();
        Assert.Contains("PHYTOLINK", page);
        Assert.Contains("One Thousand Six Hundred And Eighty Only", page);
        Assert.Contains("Mr Ng", page);
        var file = await admin.GetAsync(doc.GetProperty("url").GetString());
        Assert.Contains("Liew Khai Xen", DocumentRig.PdfText(await file.Content.ReadAsByteArrayAsync()));

        // the dry run ("PDF preview") prints the custom layout too
        var dry = await Ok(await admin.PostAsJsonAsync("/api/aicore/skills/test", new
        {
            kind = "app", app_action = "preview", document_type = "invoice",
            config = new { app = "documents", layout = "html", html = new { template = Starter, fields } },
        }));
        Assert.True(dry.GetProperty("ok").GetBoolean(), dry.ToString());

        // no renderer on the server: a clean 503, nothing numbered
        _f.Printer.Available = false;
        try
        {
            var refused = await admin.PostAsJsonAsync("/api/documents", new
            {
                channel = "whatsapp", key = phone, type = "invoice",
                fields = new { items = new[] { new { description = "x", unit_price = 1 } }, fields = new { project = "P" } },
            });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Contains("custom layout is unavailable", await refused.Content.ReadAsStringAsync());
            var apps = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills/apps");
            Assert.Equal("missing", apps.GetProperty("apps").EnumerateArray()
                .Single(a => a.GetProperty("app").GetString() == "documents").GetProperty("html_renderer").GetString());
        }
        finally { _f.Printer.Available = true; }
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Documents.CountAsync(d => d.SkillId == "docs-html"));
    }
}
