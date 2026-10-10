using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Domain;
using Xunit;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace ChatBranch.Tests;

/// <summary>
/// The AI bill as a PDF (<c>GET /api/billing/ai/pdf</c>) and its signed link for the
/// installed iOS app: the same gate, scope and visibility rules as the JSON bill.
/// </summary>
public class AiBillPdfTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AiBillPdfTests(ApiFactory f) => _f = f;

    private const string Month = "2026-05";

    private static string PdfText(byte[] pdf)
    {
        using var doc = PdfPigDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    private static string Money(string currency, double v) =>
        $"{currency} {v.ToString(v != 0 && Math.Abs(v) < 1 ? "#,##0.0000" : "#,##0.00", CultureInfo.InvariantCulture)}";

    private static int _seeded;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Three companies with usage in May 2026, and an admin bound to two of them.</summary>
    private async Task<HttpClient> SeedAsync()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        await Gate.WaitAsync();
        try
        {
            if (_seeded == 0)
            {
                foreach (var (name, slug) in new[] { ("Pdf Alpha Co", "pdf-alpha"), ("Pdf Beta Co", "pdf-beta"), ("Pdf Gamma Co", "pdf-gamma") })
                    Assert.Equal(HttpStatusCode.Created, (await su.PostAsJsonAsync("/api/v1/entities", new { name, slug })).StatusCode);
                var ids = SettingsTestKit.WithDb(_f, db => db.Entities.Where(e => e.Slug.StartsWith("pdf-"))
                    .ToDictionary(e => e.Slug, e => e.Id));
                var at = new DateTime(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc);
                SettingsTestKit.WithDb(_f, db =>
                {
                    db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "pdf-test-model", EntityId = ids["pdf-alpha"], CostUsd = 2.0, PromptTokens = 1000, CreatedAt = at });
                    db.LlmUsage.Add(new LlmUsage { Feature = "summary", Model = "pdf-test-model", EntityId = ids["pdf-alpha"], CostUsd = 0.5, PromptTokens = 500, CreatedAt = at.AddDays(3) });
                    db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "pdf-test-model", EntityId = ids["pdf-beta"], CostUsd = 1.0, CreatedAt = at });
                    db.LlmUsage.Add(new LlmUsage { Feature = "chat_reply", Model = "pdf-test-model", EntityId = ids["pdf-gamma"], CostUsd = 4.0, CreatedAt = at });
                    db.SaveChanges();
                    return 0;
                });
                var res = await su.PostAsJsonAsync("/api/v1/users", new
                {
                    username = "pdf-admin", password = "pass12345", role = "admin",
                    entity_ids = new[] { ids["pdf-alpha"], ids["pdf-beta"] },
                });
                Assert.Equal(HttpStatusCode.Created, res.StatusCode);
                _seeded = 1;
            }
        }
        finally { Gate.Release(); }
        return su;
    }

    [Fact]
    public async Task One_company_pdf_matches_the_bill_and_hides_cost_from_non_managers()
    {
        var su = await SeedAsync();
        var res = await su.GetAsync($"/api/billing/ai/pdf?month={Month}&company=pdf-alpha");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ai-bill-pdf-alpha-2026-05.pdf", res.Content.Headers.ContentDisposition?.FileNameStar
            ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        // the figures are the JSON bill's (read at the same moment the terms apply)
        var bill = await su.GetFromJsonAsync<JsonElement>($"/api/billing/ai?month={Month}&company=pdf-alpha");
        var currency = bill.GetProperty("currency").GetString()!;
        var total = bill.GetProperty("company").GetProperty("summary").GetProperty("total").GetDouble();
        var text = PdfText(bytes);
        Assert.Contains("Pdf Alpha Co", text);
        Assert.Contains("May 2026", text);
        Assert.Contains(Money(currency, total), text);
        Assert.Contains("Chat replies", text);
        Assert.Contains("Margin", text);       // the manager's view
        Assert.Contains("Cost (USD)", text);

        // a company admin: billed amounts only
        var admin = await Platform.As(_f, "pdf-admin", "pass12345");
        var mine = PdfText(await admin.GetByteArrayAsync($"/api/billing/ai/pdf?month={Month}&company=pdf-alpha"));
        Assert.Contains("Pdf Alpha Co", mine);
        Assert.Contains("Total", mine);
        Assert.DoesNotContain("Margin", mine);
        Assert.DoesNotContain("Cost", mine);
        Assert.DoesNotContain("Markup", mine);

        // Malay labels on request
        var ms = PdfText(await admin.GetByteArrayAsync($"/api/billing/ai/pdf?month={Month}&company=pdf-alpha&lang=ms"));
        Assert.Contains("Mei 2026", ms);
        Assert.Contains("Jumlah", ms);
    }

    [Fact]
    public async Task Scope_and_validation_match_the_json_bill()
    {
        await SeedAsync();
        var admin = await Platform.As(_f, "pdf-admin", "pass12345");
        // another company — out of this admin's scope — is a 404, like the JSON
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/billing/ai/pdf?month={Month}&company=pdf-gamma")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/billing/ai?month={Month}&company=pdf-gamma")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/billing/ai/pdf?month=9999-12")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/billing/ai/pdf?month=May")).StatusCode);
        // agents have no billing.view
        var agent = await Platform.As(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"/api/billing/ai/pdf?month={Month}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync($"/api/billing/ai/pdf?month={Month}")).StatusCode);
    }

    [Fact]
    public async Task All_companies_lists_only_the_visible_ones_with_a_total()
    {
        var su = await SeedAsync();
        var admin = await Platform.As(_f, "pdf-admin", "pass12345");
        var res = await admin.GetAsync($"/api/billing/ai/pdf?month={Month}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("ai-bill-all-2026-05.pdf", res.Content.Headers.ContentDisposition?.FileNameStar
            ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var text = PdfText(await res.Content.ReadAsByteArrayAsync());
        Assert.Contains("All companies", text);
        Assert.Contains("Pdf Alpha Co", text);
        Assert.Contains("Pdf Beta Co", text);
        Assert.DoesNotContain("Pdf Gamma Co", text);
        Assert.DoesNotContain("Margin", text);
        Assert.DoesNotContain("belong to no company", text);
        var bill = await admin.GetFromJsonAsync<JsonElement>($"/api/billing/ai?month={Month}");
        var sum = bill.GetProperty("companies").EnumerateArray().Sum(c => c.GetProperty("total").GetDouble());
        Assert.Contains(Money(bill.GetProperty("currency").GetString()!, Math.Round(sum, 4)), text);

        // a billing manager sees every company and the margin column
        var all = PdfText(await su.GetByteArrayAsync($"/api/billing/ai/pdf?month={Month}"));
        Assert.Contains("Pdf Gamma Co", all);
        Assert.Contains("Margin", all);
    }

    [Fact]
    public async Task Signed_link_opens_without_auth_for_minutes_and_keeps_the_scope()
    {
        await SeedAsync();
        var admin = await Platform.As(_f, "pdf-admin", "pass12345");
        var anon = _f.CreateClient();

        var link = await admin.PostAsJsonAsync("/api/billing/ai/pdf/link", new { month = Month, company = "pdf-alpha" });
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var body = await link.Content.ReadFromJsonAsync<JsonElement>();
        var path = body.GetProperty("path").GetString()!;
        Assert.StartsWith("/api/billing/ai/pdf/s/", path);
        Assert.True(body.GetProperty("expires_at").GetDateTime() <= DateTime.UtcNow.AddMinutes(6));

        var res = await anon.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("inline", res.Content.Headers.ContentDisposition?.DispositionType);
        var text = PdfText(await res.Content.ReadAsByteArrayAsync());
        Assert.Contains("Pdf Alpha Co", text);
        Assert.DoesNotContain("Margin", text);   // still the admin's view, not a manager's

        // a tampered token is refused; so is a link for a company out of scope
        var token = path["/api/billing/ai/pdf/s/".Length..];
        var forged = token[..^2] + (token[^2] == 'A' ? "BB" : "AA");
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync("/api/billing/ai/pdf/s/" + forged)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync("/api/billing/ai/pdf/s/nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/billing/ai/pdf/link",
            new { month = Month, company = "pdf-gamma" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/billing/ai/pdf/link",
            new { month = "9999-12" })).StatusCode);

        // an expired token is refused
        var old = ChatBranch.Api.Endpoints.BillLink.Sign(new ChatBranch.Api.Endpoints.BillLink.Claims(
            1, "superadmin", Month, "", "en", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()));
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync("/api/billing/ai/pdf/s/" + old)).StatusCode);

        // API keys use the authenticated GET; the link is for sessions only
        var key = Platform.Client(_f, Platform.MintKey(_f, "admin", "billing.view"));
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsJsonAsync("/api/billing/ai/pdf/link", new { month = Month })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync($"/api/billing/ai/pdf?month={Month}")).StatusCode);
    }
}
