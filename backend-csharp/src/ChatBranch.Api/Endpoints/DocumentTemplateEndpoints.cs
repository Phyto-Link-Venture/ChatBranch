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
/// The Documents app's template designer (all <c>settings.ai.manage</c>, like the
/// rest of the skill form):
/// <list type="bullet">
/// <item><c>POST /api/aicore/skills/documents/letterhead</c> (multipart PDF ≤ 10 MB) —
/// keep its FIRST page as a letterhead background; <c>GET …/letterhead/{name}</c>
/// serves it back.</item>
/// <item><c>POST /api/aicore/skills/documents/template/analyze</c> (multipart: a PDF ≤
/// 10 MB or PNG / JPEG ≤ 5 MB, optional <c>template</c> + <c>config</c> JSON and
/// <c>keep_letterhead</c>) — "Upload my design": a proposed template + business
/// lines (<see cref="DocumentTemplateAi.AnalyzeAsync"/>) and a sample preview
/// rendered with them.</item>
/// <item><c>POST /api/aicore/skills/documents/template/assist</c> {template,
/// instruction} — "Ask AI": the edited template + a one-line summary.</item>
/// </list>
/// The two AI routes check the caller's AI quota first (429) and meter as
/// <c>document_template</c>. Nothing is saved to the skill: the form's Save is.
/// Previews of an edited template go through the skill test's documents dry
/// run (<c>POST /api/aicore/skills/test</c> with the unsaved config).
/// </summary>
public static class DocumentTemplateEndpoints
{
    public record AssistBody(JsonElement? Template, string? Instruction);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/aicore/skills/documents/letterhead", async (HttpContext ctx, DocumentService documents) =>
        {
            var (bytes, bad) = await ReadFileAsync(ctx, DocumentService.MaxLetterheadBytes, "The letterhead must be a PDF up to 10 MB.");
            if (bad != null) return bad;
            var name = documents.SaveLetterhead(bytes!, out var error);
            return name == null ? Bad(error ?? "Upload failed.")
                : Results.Ok(new { letterhead = name, url = $"/api/aicore/skills/documents/letterhead/{name}" });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage").DisableAntiforgery();

        app.MapGet("/api/aicore/skills/documents/letterhead/{name}", (string name, DocumentService documents) =>
        {
            var path = documents.LetterheadPath(name);
            return path == null ? Results.NotFound(new { detail = "Letterhead not found." }) : Results.File(path, "application/pdf");
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        app.MapPost("/api/aicore/skills/documents/template/analyze", async (HttpContext ctx, DocumentTemplateAi ai,
            DocumentService documents, SkillService skills, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            CancellationToken ct) =>
        {
            if (QuotaGuard.Refusal(db, sc, me) is { } quota) return quota;
            var (bytes, bad) = await ReadFileAsync(ctx, DocumentTemplateAi.MaxPdfBytes,
                "Upload a PDF up to 10 MB, or a PNG / JPEG up to 5 MB.");
            if (bad != null) return bad;
            var upload = DocumentTemplateAi.Sniff(bytes!, out var sniffError);
            if (upload == null) return Bad(sniffError!);

            var form = ctx.Request.Form;
            DocumentTemplate? current = null;
            if (form["template"].ToString() is { Length: > 0 } tj)
            {
                if (!TryJson(tj, out var tel)) return Bad("template must be JSON.");
                if (tel.ValueKind != JsonValueKind.Null)
                {
                    current = DocumentTemplate.From(tel, out var terr);
                    if (terr != null) return Bad(terr);
                }
            }
            JsonObject? config = null;
            if (form["config"].ToString() is { Length: > 0 } cj)
            {
                if (!TryJson(cj, out var cel) || cel.ValueKind != JsonValueKind.Object) return Bad("config must be a JSON object.");
                config = JsonNode.Parse(cel.GetRawText())!.AsObject();
            }

            // "keep my PDF as the background": its first page, stored like the logo
            string? letterhead = null;
            var notes = new List<string>();
            if (IsTrue(form["keep_letterhead"].ToString()))
            {
                if (upload.Kind != "pdf") notes.Add("Only a PDF can be kept as a letterhead background.");
                else
                {
                    letterhead = documents.SaveLetterhead(upload.Bytes, out var lhError);
                    if (letterhead == null) notes.Add(lhError ?? "The letterhead couldn't be kept.");
                }
            }

            var proposal = await ai.AnalyzeAsync(upload, current, ct);
            var template = proposal.Template;
            if (letterhead != null) template.Letterhead = new DocumentTemplate.LetterheadSpec { File = letterhead, Pages = "all" };
            if (proposal.Note.Length > 0) notes.Insert(0, proposal.Note);

            // a sample rendered with the proposal (business lines fill only what the form left empty)
            var cfgNode = config ?? new JsonObject();
            cfgNode["app"] = DocumentAppConfig.AppId;
            foreach (var (k, v) in proposal.Business)
                if (!(cfgNode[k] is JsonValue jv && jv.TryGetValue<string>(out var have) && have.Trim().Length > 0))
                    cfgNode[k] = v;
            cfgNode["template"] = JsonSerializer.SerializeToNode(template.ToDictionary());
            var preview = await PreviewAsync(cfgNode, form["document_type"].ToString(), skills, ct);

            return Results.Ok(new
            {
                mode = proposal.Mode,
                template = template.ToDictionary(),
                business = proposal.Business,
                summary = proposal.Summary,
                note = string.Join(" ", notes),
                letterhead,
                letterhead_url = letterhead == null ? null : $"/api/aicore/skills/documents/letterhead/{letterhead}",
                preview,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage").DisableAntiforgery();

        app.MapPost("/api/aicore/skills/documents/template/assist", async (AssistBody body, DocumentTemplateAi ai,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, CancellationToken ct) =>
        {
            if (QuotaGuard.Refusal(db, sc, me) is { } quota) return quota;
            var instruction = (body.Instruction ?? "").Trim();
            if (instruction.Length == 0) return Bad("Say what to change, e.g. \"make it minimal with a navy header\".");
            if (instruction.Length > 1000) return Bad("Keep the instruction under 1,000 characters.");
            var current = DocumentTemplate.Classic();
            if (body.Template is JsonElement te && te.ValueKind != JsonValueKind.Null)
            {
                current = DocumentTemplate.From(te, out var error)!;
                if (error != null || current == null) return Bad(error ?? "template must be a JSON object.");
            }
            var r = await ai.AssistAsync(current, instruction, ct);
            return Results.Ok(new { mode = r.Mode, template = r.Template.ToDictionary(), summary = r.Summary, note = r.Note });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");
    }

    /// <summary>The documents dry run with a config (never throws; {ok, url?, detail}).</summary>
    private static async Task<object> PreviewAsync(JsonObject cfgNode, string? type, SkillService skills, CancellationToken ct)
    {
        try
        {
            var cfg = DocumentAppConfig.From(JsonSerializer.SerializeToElement(cfgNode), out var error);
            if (error != null)
            {
                // a half-filled form still previews the design: drop what didn't validate
                var fallback = new JsonObject { ["app"] = DocumentAppConfig.AppId, ["template"] = cfgNode["template"]?.DeepClone() };
                cfg = DocumentAppConfig.From(JsonSerializer.SerializeToElement(fallback), out _);
            }
            var docType = DocumentAppConfig.AllActions.Contains(type ?? "") ? type! : DocumentTypes.Invoice;
            var r = await skills.Documents.PreviewSampleAsync(docType, cfg, skills.UtcNow, skills.OrgTimeZone(), ct);
            return new { ok = r.Error == null, url = r.Url, detail = r.Error ?? "" };
        }
        catch (Exception ex)
        {
            return new { ok = false, url = (string?)null, detail = $"The preview failed ({ex.GetType().Name})." };
        }
    }

    internal static async Task<(byte[]? Bytes, IResult? Bad)> ReadFileAsync(HttpContext ctx, long max, string tooBig)
    {
        if (!ctx.Request.HasFormContentType) return (null, Bad("file is required (multipart/form-data)."));
        var form = await ctx.Request.ReadFormAsync();
        var file = form.Files["file"];
        if (file == null || file.Length == 0) return (null, Bad("file is required (multipart/form-data)."));
        if (file.Length > max) return (null, Bad(tooBig));
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        return (ms.ToArray(), null);
    }

    internal static bool TryJson(string s, out JsonElement el)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            el = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            el = default;
            return false;
        }
    }

    private static bool IsTrue(string s) => s.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    internal static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);
}
