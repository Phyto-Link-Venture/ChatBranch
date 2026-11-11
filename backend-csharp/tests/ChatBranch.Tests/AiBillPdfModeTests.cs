using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Domain;
using Xunit;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace ChatBranch.Tests;

/// <summary>
/// The AI bill PDF says HOW each company is priced. Price books added flat and unit
/// plans, but the PDF's manager-only "Markup" cell kept printing the markup percentage —
/// "0%" for a flat- or unit-priced client, which reads as "billed at cost". The PDF now
/// prints the pricing mode and the plan (as the JSON carries them: mode, plan_label,
/// plan_inherited), and the percentage only where a markup is what priced the month.
/// </summary>
public class AiBillPdfModeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AiBillPdfModeTests(ApiFactory f) => _f = f;

    private static string PdfText(byte[] pdf)
    {
        using var doc = PdfPigDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    private static int _seeded;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string Month = DateTime.UtcNow.ToString("yyyy-MM");

    /// <summary>A flat-priced, a unit-priced and a markup-priced company, each with usage this month.</summary>
    private async Task<HttpClient> SeedAsync()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        await Gate.WaitAsync();
        try
        {
            if (_seeded == 1) return su;
            foreach (var (name, slug) in new[] { ("Mode Alpha Co", "mode-flat"), ("Mode Beta Co", "mode-unit"), ("Mode Gamma Co", "mode-markup") })
                Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities", new { name, slug })).StatusCode);
            var ids = SettingsTestKit.WithDb(_f, db => db.Entities.Where(e => e.Slug.StartsWith("mode-"))
                .ToDictionary(e => e.Slug, e => e.Id));
            async Task Plan(object body, int entity)
            {
                var res = await su.PostAsJsonAsync("/api/billing/ai/price-books", body);
                res.EnsureSuccessStatusCode();
                var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan").GetProperty("id").GetInt32();
                (await su.PostAsJsonAsync("/api/bindings/pricebook-entities",
                    new { target = id, add = new[] { entity }, remove = Array.Empty<int>() })).EnsureSuccessStatusCode();
            }
            await Plan(new { label = "Gold Plan", slug = "pdfmode-flat", mode = "flat", flat_amount = 300 }, ids["mode-flat"]);
            await Plan(new { label = "Meter Plan", slug = "pdfmode-unit", mode = "unit", unit_per_reply = 0.05 }, ids["mode-unit"]);
            await Plan(new { label = "Plus Plan", slug = "pdfmode-markup", mode = "markup", markup_pct = 25 }, ids["mode-markup"]);
            var at = DateTime.UtcNow.AddMinutes(-5);
            SettingsTestKit.WithDb(_f, db =>
            {
                foreach (var e in ids.Values)
                    db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "pdf-mode-model", EntityId = e, CostUsd = 2.0,
                        PromptTokens = 1000, CreatedAt = at });
                db.SaveChanges();
                return 0;
            });
            _seeded = 1;
        }
        finally { Gate.Release(); }
        return su;
    }

    [Fact]
    public async Task A_flat_priced_company_pdf_says_flat_and_names_its_plan_not_zero_percent()
    {
        var su = await SeedAsync();
        var bill = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai?month={Month}&company=mode-flat");
        Assert.Equal("flat", bill.GetProperty("company").GetProperty("summary").GetProperty("mode").GetString());

        var text = PdfText(await su.GetByteArrayAsync($"/api/billing/ai/pdf?month={Month}&company=mode-flat"));
        Assert.Contains("Pricing", text);
        Assert.Contains("Flat", text);
        Assert.Contains("Gold Plan", text);
        Assert.DoesNotContain("0%", text);

        var unit = PdfText(await su.GetByteArrayAsync($"/api/billing/ai/pdf?month={Month}&company=mode-unit"));
        Assert.Contains("Per unit", unit);
        Assert.Contains("Meter Plan", unit);
        Assert.DoesNotContain("0%", unit);

        // a markup-priced company keeps its percentage
        var markup = PdfText(await su.GetByteArrayAsync($"/api/billing/ai/pdf?month={Month}&company=mode-markup"));
        Assert.Contains("25%", markup);
        Assert.Contains("Plus Plan", markup);
    }

    [Fact]
    public async Task The_all_companies_pdf_prints_each_companys_pricing_mode()
    {
        var su = await SeedAsync();
        var text = PdfText(await su.GetByteArrayAsync($"/api/billing/ai/pdf?month={Month}"));
        Assert.Contains("Pricing", text);
        Assert.Contains("Flat", text);
        Assert.Contains("Per unit", text);
        Assert.Contains("Markup 25%", text);
    }

    [Fact]
    public async Task Pricing_labels_are_translated()
    {
        var su = await SeedAsync();
        var ms = PdfText(await su.GetByteArrayAsync($"/api/billing/ai/pdf?month={Month}&company=mode-flat&lang=ms"));
        Assert.Contains("Harga", ms);
        Assert.Contains("Tetap", ms);
    }
}
