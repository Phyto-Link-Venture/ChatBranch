using System.Globalization;
using System.Text;
using System.Text.Json;
using ChatBranch.Data.Documents;
using ChatBranch.Domain;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace ChatBranch.Tests;

/// <summary>
/// Fixed documents rendered with a config that has NO template, and a
/// position-level fingerprint of each PDF (every word with its x / y to 0.1 pt,
/// per page). The fixtures under <c>Fixtures/documents-legacy/</c> were captured
/// from the renderer BEFORE the template designer existed —
/// <c>DocumentTemplateTests</c> checks today's renderer still prints every word
/// in the same place.
/// </summary>
internal static class DocumentLegacyGolden
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
    private static readonly DateTimeOffset Day = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    public static DocumentAppConfig Config() => DocumentAppConfig.From(J(new
    {
        app = "documents", business_name = "Kedai Rambut Aisha", address = "12 Jalan Bunga\nKuala Lumpur",
        registration_no = "202601234567", tax_no = "W10-1234", tax_label = "SST", tax_rate = 8,
        phone = "+60 12-345 6789", email = "hello@example.com", website = "example.com",
        payment_instructions = "Maybank 5123 4567 8901\nPay within 14 days", accent = "teal",
        footer_note = "Thank you for your business.",
    }), out _);

    public static IEnumerable<(string Name, DocumentContent Content)> Samples(DocumentAppConfig cfg)
    {
        DocumentContent B(string type, object args, string number, Contact? c = null) =>
            DocumentService.Build(type, J(args), cfg, c, Day, TimeZoneInfo.Utc, number).Content!;
        yield return ("invoice", B(DocumentTypes.Invoice, new
        {
            customer_name = "Encik Ahmad", customer_company = "Ahmad Trading", customer_address = "1 Jalan Satu\nPetaling Jaya",
            customer_email = "ahmad@example.com", notes = "Thank you.", reference = "PO-77",
            items = new object[]
            {
                new { description = "Haircut", qty = 2, unit_price = 45.5 },
                new { description = "Colour", qty = 1, unit_price = 120, discount = "10%" },
            },
        }, "INV-0042"));
        yield return ("quotation", B(DocumentTypes.Quotation, new
        {
            customer_name = "Encik Ahmad", title = "Wedding package",
            items = new object[] { new { description = "Bridal hair", qty = 1, unit_price = 800 } },
        }, "QUO-0007"));
        yield return ("receipt-ms", B(DocumentTypes.Receipt, new
        {
            items = new[] { new { description = "Bayaran deposit", qty = 1, unit_price = 50 } }, payment_method = "DuitNow",
        }, "RCP-0001", new Contact { Name = "Siti", Language = "ms" }));
        yield return ("letter", B(DocumentTypes.Letter, new
        {
            title = "Confirmation of booking", paragraphs = new[] { "We confirm your booking.", "See you soon." },
        }, "LTR-0001", new Contact { Name = "Siti" }));
        yield return ("long-invoice", B(DocumentTypes.Invoice, new
        {
            customer_name = "Encik Ahmad",
            items = Enumerable.Range(1, 45).Select(i => (object)new { description = $"Line item number {i}", qty = i, unit_price = 1.5 }).ToArray(),
        }, "INV-0043"));
    }

    public static string Fingerprint(byte[] pdf)
    {
        using var doc = PdfPigDocument.Open(pdf);
        var sb = new StringBuilder();
        foreach (var p in doc.GetPages())
        {
            sb.Append("#page ").Append(p.Number).Append(' ')
              .Append(p.Width.ToString("0", CultureInfo.InvariantCulture)).Append('x')
              .Append(p.Height.ToString("0", CultureInfo.InvariantCulture)).Append('\n');
            foreach (var w in p.GetWords())
                sb.Append(w.Text).Append('@')
                  .Append(w.BoundingBox.Left.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
                  .Append(w.BoundingBox.Bottom.ToString("0.0", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    public static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "documents-legacy", name + ".txt");
}
