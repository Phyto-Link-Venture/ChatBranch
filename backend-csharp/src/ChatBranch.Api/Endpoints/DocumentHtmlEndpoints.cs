using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Documents;
using ChatBranch.Domain;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The Documents app's "Custom layout" (HTML templates) editor — all
/// <c>settings.ai.manage</c>, like the rest of the skill form; nothing is saved here
/// (the skills PUT stores <c>layout</c> + <c>html</c>):
/// <list type="bullet">
/// <item><c>POST /api/aicore/skills/documents/html/preview</c> {html, fields, type,
/// language, config?} — the INSTANT preview: the template rendered with sample data
/// (and the unsaved config's business lines, terms, bank details, logo), sanitized and
/// hardened exactly as it would print → <c>{ok, html}</c> for a sandboxed iframe, or
/// <c>{ok: false, error, line}</c> (a Handlebars error with its line). The exact PDF is
/// the skill test's documents dry run (headless Chromium).</item>
/// <item><c>POST …/documents/html/draft</c> (multipart: a PDF ≤ 10 MB or PNG / JPEG ≤ 5 MB,
/// optional <c>instruction</c>, optional <c>config</c>) — "Upload my design":
/// <see cref="DocumentHtmlAi.DraftAsync"/> → {mode ai | starter, starter, html (a whole
/// layout config), business, summary, note}.</item>
/// <item><c>POST …/documents/html/assist</c> {html, fields, instruction} — "Ask AI": the
/// edited template + fields + a one-line summary (sandbox: unchanged + a note).</item>
/// </list>
/// The two AI routes check the caller's AI quota first (429) and meter as
/// <c>document_template</c>.
/// </summary>
public static class DocumentHtmlEndpoints
{
    public record PreviewBody(string? Html, JsonElement? Fields, string? Type, string? Language, JsonElement? Config);

    public record AssistBody(string? Html, JsonElement? Fields, string? Instruction);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/aicore/skills/documents/html/preview", (PreviewBody body, SkillService skills, DocumentService documents) =>
        {
            var template = body.Html ?? "";
            if (template.Length > DocumentHtmlConfig.MaxTemplateChars)
                return DocumentTemplateEndpoints.Bad($"The template is larger than {DocumentHtmlConfig.MaxTemplateChars / 1024} KB.");
            var type = DocumentTypes.Numbered.Contains((body.Type ?? "").Trim().ToLowerInvariant())
                ? body.Type!.Trim().ToLowerInvariant() : DocumentTypes.Invoice;
            var language = (body.Language ?? "").Trim().ToLowerInvariant() is "ms" ? "ms" : "en";
            if (template.Trim().Length == 0)
                return Results.Ok(new { ok = false, error = "The template is empty — start from a ready-made layout.", line = (int?)null });
            var (cfg, problem) = PreviewConfig(body.Config, template, body.Fields);
            if (problem != null) return Results.Ok(new { ok = false, error = problem, line = (int?)null });
            try
            {
                var content = DocumentHtmlRenderer.Sample(type, cfg, skills.UtcNow, skills.OrgTimeZone(), language);
                var html = DocumentHtmlRenderer.PreviewHtml(content, cfg, documents.LoadLogo(cfg.Logo));
                return Results.Ok(new { ok = true, html, type, language, number = content.Number });
            }
            catch (DocumentHtmlException ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message, line = ex.Line });
            }
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        app.MapPost("/api/aicore/skills/documents/html/draft", async (HttpContext ctx, DocumentHtmlAi ai, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, CancellationToken ct) =>
        {
            if (QuotaGuard.Refusal(db, sc, me) is { } quota) return quota;
            var (bytes, bad) = await DocumentTemplateEndpoints.ReadFileAsync(ctx, DocumentTemplateAi.MaxPdfBytes,
                "Upload a PDF up to 10 MB, or a PNG / JPEG up to 5 MB.");
            if (bad != null) return bad;
            var upload = DocumentTemplateAi.Sniff(bytes!, out var sniffError);
            if (upload == null) return DocumentTemplateEndpoints.Bad(sniffError!);
            var form = ctx.Request.Form;
            var instruction = form["instruction"].ToString().Trim();
            if (instruction.Length > 1000) return DocumentTemplateEndpoints.Bad("Keep the instruction under 1,000 characters.");
            DocumentHtmlConfig? current = null;
            if (form["config"].ToString() is { Length: > 0 } cj)
            {
                if (!DocumentTemplateEndpoints.TryJson(cj, out var cel) || cel.ValueKind != JsonValueKind.Object)
                    return DocumentTemplateEndpoints.Bad("config must be a JSON object.");
                if (cel.TryGetProperty("html", out var hel) && hel.ValueKind == JsonValueKind.Object)
                    current = DocumentHtmlConfig.From(hel, out _, check: false);
            }
            var draft = await ai.DraftAsync(upload, instruction, current, ct);
            return Results.Ok(new
            {
                mode = draft.Mode,
                starter = draft.Starter,
                html = draft.Html.ToDictionary(),
                business = draft.Business,
                summary = draft.Summary,
                note = draft.Note,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage").DisableAntiforgery();

        app.MapPost("/api/aicore/skills/documents/html/assist", async (AssistBody body, DocumentHtmlAi ai, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, CancellationToken ct) =>
        {
            if (QuotaGuard.Refusal(db, sc, me) is { } quota) return quota;
            var instruction = (body.Instruction ?? "").Trim();
            if (instruction.Length == 0)
                return DocumentTemplateEndpoints.Bad("Say what to change, e.g. \"put the bank details under the total\".");
            if (instruction.Length > 1000) return DocumentTemplateEndpoints.Bad("Keep the instruction under 1,000 characters.");
            var template = body.Html ?? "";
            if (template.Trim().Length == 0) return DocumentTemplateEndpoints.Bad("html (the current template) is required.");
            if (template.Length > DocumentHtmlConfig.MaxTemplateChars)
                return DocumentTemplateEndpoints.Bad($"The template is larger than {DocumentHtmlConfig.MaxTemplateChars / 1024} KB.");
            var fields = DocumentHtmlConfig.From(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["fields"] = body.Fields is JsonElement f && f.ValueKind == JsonValueKind.Array ? f : null,
            }), out var error, check: false);
            if (error != null) return DocumentTemplateEndpoints.Bad(error);
            var r = await ai.AssistAsync(template, fields!.Fields, instruction, ct);
            return Results.Ok(new
            {
                mode = r.Mode,
                template = r.Template,
                fields = r.Fields.Select(x => x.ToDictionary()).ToList(),
                summary = r.Summary,
                note = r.Note,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");
    }

    /// <summary>
    /// The unsaved documents config with the editor's template + fields as its custom
    /// layout. A half-filled form still previews: a problem outside the layout drops the
    /// business lines instead of refusing; a problem in the layout (a bad field) is returned.
    /// </summary>
    private static (DocumentAppConfig Cfg, string? Problem) PreviewConfig(JsonElement? config, string template, JsonElement? fields)
    {
        var raw = config is JsonElement c && c.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(c.GetRawText())!.AsObject() : new JsonObject();
        var html = raw["html"] as JsonObject ?? new JsonObject();
        html = JsonNode.Parse(html.ToJsonString())!.AsObject();
        html["template"] = template;
        if (fields is JsonElement f && f.ValueKind == JsonValueKind.Array) html["fields"] = JsonNode.Parse(f.GetRawText());
        raw["app"] = DocumentAppConfig.AppId;
        raw["layout"] = DocumentAppConfig.HtmlLayout;
        raw["html"] = html;
        var cfg = DocumentAppConfig.From(JsonSerializer.SerializeToElement(raw), out var error, check: false);
        if (error == null) return (cfg, null);
        if (error.StartsWith("html", StringComparison.Ordinal)) return (cfg, error);
        var bare = new JsonObject
        {
            ["app"] = DocumentAppConfig.AppId, ["layout"] = DocumentAppConfig.HtmlLayout, ["html"] = html.DeepClone(),
        };
        cfg = DocumentAppConfig.From(JsonSerializer.SerializeToElement(bare), out var again, check: false);
        return (cfg, again);
    }
}
