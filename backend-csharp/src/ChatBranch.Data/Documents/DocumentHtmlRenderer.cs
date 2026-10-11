using ChatBranch.Domain;

namespace ChatBranch.Data.Documents;

/// <summary>
/// A custom-layout document, start to finish: the type's template (sanitized again)
/// → Handlebars with the data model (<see cref="DocumentHtmlModel"/>, every value
/// escaped) → the rendered page sanitized → headless Chromium
/// (<see cref="IHtmlPdfConverter"/>, scripts and fetches blocked) → the uploaded
/// letterhead drawn under the pages (the designer's own underlay). No renderer = a
/// clean refusal (<see cref="HtmlRendererUnavailableException"/>), never the designer
/// layout instead.
/// </summary>
public static class DocumentHtmlRenderer
{
    public const int MaxOutputChars = 4_000_000;

    /// <summary>The page as it will print (sanitized; the converter hardens it when it prints).</summary>
    public static string RenderHtml(DocumentContent c, DocumentAppConfig cfg, byte[]? logo)
    {
        var html = cfg.Html ?? throw new DocumentHtmlException("This skill has no custom layout yet — add a template.");
        var template = HtmlSanitizer.Sanitize(html.TemplateFor(c.Type), template: true);
        if (template.Trim().Length == 0) throw new DocumentHtmlException("The custom layout is empty — add a template.");
        var output = DocumentHtmlTemplating.Render(template, DocumentHtmlModel.Build(c, cfg, logo), c.Language);
        if (output.Length > MaxOutputChars) throw new DocumentHtmlException("The custom layout printed a page that is too large.");
        return HtmlSanitizer.Sanitize(output);
    }

    public static async Task<byte[]> RenderPdfAsync(DocumentContent c, DocumentAppConfig cfg, byte[]? logo,
        byte[]? letterhead, IHtmlPdfConverter converter, CancellationToken ct = default)
    {
        if (!converter.Available) throw new HtmlRendererUnavailableException();
        var html = RenderHtml(c, cfg, logo);
        var pdf = await converter.ConvertAsync(html, HtmlPdfPage.Of(cfg.Html), ct);
        return letterhead is { Length: > 0 }
            ? DocumentRenderer.UnderlayPdf(pdf, letterhead, cfg.Html?.Letterhead.Pages == "first")
            : pdf;
    }

    /// <summary>The editor's instant preview: the sanitized page, hardened exactly as it would print.</summary>
    public static string PreviewHtml(DocumentContent c, DocumentAppConfig cfg, byte[]? logo) =>
        HtmlPdfHardening.Harden(RenderHtml(c, cfg, logo), HtmlPdfPage.Of(cfg.Html));

    /// <summary>A sample document of <paramref name="type"/> with a layout config (unsaved settings welcome).</summary>
    public static DocumentContent Sample(string type, DocumentAppConfig cfg, DateTimeOffset utcNow, TimeZoneInfo tz,
        string? language = null)
    {
        var build = DocumentService.Build(type, DocumentHtmlModel.SampleArgs(type, cfg.Html), cfg, null, utcNow, tz,
            cfg.FormatNumber(type, cfg.StartNumber(type)));
        if (build.Error != null) throw new DocumentHtmlException(build.Error);
        var content = build.Content!;
        return language is "en" or "ms" ? content.WithLanguage(language) : content;
    }

    /// <summary>Why a layout can't print a sample of <paramref name="type"/> (null = it can) — the normalizer's check.</summary>
    public static string? CheckSample(DocumentHtmlConfig html, string type)
    {
        try
        {
            var cfg = new DocumentAppConfig
            {
                BusinessName = "Sample Business Sdn Bhd", Address = "No. 1, Jalan Contoh\n50000 Kuala Lumpur",
                Phone = "+60 3-1234 5678", Email = "hello@example.com", Layout = DocumentAppConfig.HtmlLayout, Html = html,
            };
            RenderHtml(Sample(type, cfg, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, "en"), cfg, null);
            return null;
        }
        catch (DocumentHtmlException ex) { return ex.Message; }
        catch (Exception ex) { return $"it failed ({ex.GetType().Name})."; }
    }
}
