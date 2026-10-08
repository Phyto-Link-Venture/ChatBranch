using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// /api/aicore/skills — the SOLE writer of crm.AgentSkill — plus the code-skill
/// authoring surface: describe → clarify → code (<c>/draft</c>), the sandboxed
/// test runner (<c>/test</c>) and the read-only built-ins (<c>/builtins</c>).
///
/// PUT keeps its upsert-and-prune-within-one-scope contract. On top of it, an
/// ENABLED tool/check skill whose code or tests changed must parse, define its
/// entry point and pass its own tests, or the whole PUT is refused (400) — the
/// UI's "save anyway as disabled" is the escape hatch.
/// </summary>
public static class AiSkillsEndpoints
{
    /// <summary>Wire shape of a skill: `id` is the string skill_id, and the
    /// fields are camelCase (`useWhen`, `onFail`) — Django's _skill_payload did
    /// the same. Every field past `position` is optional so a legacy client that
    /// only knows the prompt fields keeps working (absent = keep).</summary>
    public record SkillDto(string? Id, string? Name, string? UseWhen, string? Instructions, int Position,
        string? Kind = null, string? Description = null, string? Code = null, JsonElement? Parameters = null,
        JsonElement? Tests = null, bool? Enabled = null, List<string>? Channels = null, string? OnFail = null);

    public record SkillsWrite(string? Entity, List<SkillDto>? Skills);
    public record DraftAnswer(string? Question, string? Answer);
    /// <summary>`kind_hint` is the documented wire name; `kindHint` binds too.</summary>
    public record DraftRequest(string? Description, string? KindHint, List<DraftAnswer>? Answers,
        [property: JsonPropertyName("kind_hint")] string? KindHintSnake = null);
    public record TestRequest(string? Kind, string? Code, JsonElement? Tests);

    private static readonly HashSet<string> Kinds = new() { SkillKinds.Prompt, SkillKinds.Tool, SkillKinds.Check };

    public static void MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/aicore/skills", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            return Results.Ok(new { skills = List(db, sc, entityId) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/aicore/skills", async (SkillsWrite body, AppDbContext db, ScopingService sc,
            SkillService skills, ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (body.Skills == null) return Bad("Body must carry skills[].");
            var entityId = await InboxEndpoints.EntityId(db, body.Entity);
            // An unknown slug must not silently become "organization-wide" — the
            // prune below would then wipe the global library.
            if (entityId == null && !string.IsNullOrWhiteSpace(body.Entity) && body.Entity is not ("all" or "__all__"))
                return Bad($"Unknown entity '{body.Entity}'.");

            // Upsert within THIS scope, prune scope rows not in the payload
            // (the "sole writer" contract — only touches its own scope).
            var existing = await db.AgentSkills.Where(s => s.EntityId == entityId).ToListAsync();

            // 1) validate everything before touching a row
            var seen = new HashSet<string>();
            foreach (var s in body.Skills)
            {
                var skillId = SkillId(s.Id);
                if (skillId.Length == 0) continue;
                if (!seen.Add(skillId)) return Bad($"Skill id '{skillId}' appears twice.");
                var current = existing.FirstOrDefault(e => e.SkillId == skillId);
                var kind = s.Kind is null ? current?.Kind ?? SkillKinds.Prompt : s.Kind.Trim().ToLowerInvariant();
                if (!Kinds.Contains(kind)) return Bad($"Skill '{s.Name}': kind must be prompt, tool or check.");
                if (s.Parameters is JsonElement p && RawJson(p) is string praw && praw.Length > 0 && !IsJsonObject(praw))
                    return Bad($"Skill '{s.Name}': parameters must be a JSON object (a JSON schema).");
                if (s.Tests is JsonElement t && RawJson(t) is string traw && traw.Length > 0 && !IsJsonArray(traw))
                    return Bad($"Skill '{s.Name}': tests must be a JSON array.");
                if (!SkillKinds.IsCode(kind)) continue;

                var code = s.Code ?? current?.Code ?? "";
                var testsRaw = s.Tests is JsonElement tt ? RawJson(tt) ?? "" : current?.Tests ?? "";
                var enabled = s.Enabled ?? current?.Enabled ?? true;
                var changed = current == null || current.Code != code || current.Tests != testsRaw
                              || current.Kind != kind || (!current.Enabled && enabled);
                if (code.Length > SkillRuntime.MaxCodeLength)
                    return Bad($"Skill '{s.Name}': the code is longer than {SkillRuntime.MaxCodeLength:N0} characters.");
                if (!enabled || !changed) continue;

                if (SkillRuntime.Validate(code, kind) is string invalid)
                    return Bad($"Skill '{s.Name}': {invalid} Fix it or save the skill disabled.");
                if (testsRaw.Length > 0)
                {
                    var failed = skills.RunTests(kind, code, JsonDocument.Parse(testsRaw).RootElement)
                        .FirstOrDefault(r => !r.Pass);
                    if (failed != null)
                        return Bad($"Skill '{s.Name}' is enabled but its test \"{failed.Name}\" fails"
                                   + (failed.Error is { Length: > 0 } err ? $" ({err})" : "")
                                   + ". Fix it or save the skill disabled.");
                }
            }

            // 2) apply
            var keep = new HashSet<string>();
            int created = 0, updated = 0;
            var pos = 0;
            foreach (var s in body.Skills)
            {
                // `id` on the wire is the STRING skill_id the browser mints
                // ("as-xxxxx"), never the numeric pk. Blank ones are dropped
                // rather than creating an unaddressable row (mirrors Django).
                var skillId = SkillId(s.Id);
                if (skillId.Length == 0) continue;
                keep.Add(skillId);
                var row = existing.FirstOrDefault(e => e.SkillId == skillId);
                var isNew = row == null;
                row ??= Add(db, new AgentSkill { SkillId = skillId, EntityId = entityId });
                var before = Signature(row);
                row.Name = string.IsNullOrWhiteSpace(s.Name) ? "Agent skill" : s.Name.Trim();
                row.UseWhen = s.UseWhen ?? "";
                row.Instructions = s.Instructions ?? "";
                if (s.Kind != null) row.Kind = s.Kind.Trim().ToLowerInvariant();
                if (s.Description != null) row.Description = s.Description;
                if (s.Code != null) row.Code = s.Code;
                if (s.Parameters is JsonElement p) row.Parameters = RawJson(p) ?? "";
                if (s.Tests is JsonElement t) row.Tests = RawJson(t) ?? "";
                if (s.Enabled is bool en) row.Enabled = en;
                if (s.Channels != null)
                    row.Channels = s.Channels.Select(c => (c ?? "").Trim().ToLowerInvariant())
                        .Where(c => c.Length > 0).Distinct().ToList();
                if (s.OnFail != null) row.OnFail = SkillService.NormalizeOnFail(s.OnFail.Trim().ToLowerInvariant());
                // Position comes from LIST ORDER, not the payload field — the
                // settings page reorders by dragging rows and never sends one.
                row.Position = pos++;
                if (isNew) { created++; row.UpdatedAt = DateTime.UtcNow; }
                else if (Signature(row) != before) { updated++; row.UpdatedAt = DateTime.UtcNow; }
            }
            var removed = existing.Where(e => !keep.Contains(e.SkillId)).ToList();
            db.AgentSkills.RemoveRange(removed);
            await db.SaveChangesAsync();
            if (created + updated + removed.Count > 0)
                Audit(db, me, ctx, "skills.update", body.Entity is { Length: > 0 } slug ? slug : "organization",
                    $"created {created}, updated {updated}, removed {removed.Count}"
                    + (removed.Count > 0 ? $" ({string.Join(", ", removed.Select(r => r.Name))})" : ""));
            // Echo the saved library in the GET shape — the page swaps its draft
            // for this response, so a `{count}` reply blanked the editor.
            return Results.Ok(new { skills = List(db, sc, entityId) });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- describe → clarify → code ----------------------------------------
        app.MapPost("/api/aicore/skills/draft", async (DraftRequest body, SkillDrafter drafter,
            CancellationToken ct) =>
        {
            var description = (body.Description ?? "").Trim();
            if (description.Length == 0) return Bad("Describe what the skill should do.");
            if (description.Length > 4000) return Bad("Keep the description under 4,000 characters.");
            var answers = (body.Answers ?? new())
                .Where(a => !string.IsNullOrWhiteSpace(a.Answer))
                .Select(a => new SkillDrafter.Answer((a.Question ?? "").Trim(), a.Answer!.Trim()))
                .Take(20).ToList();
            var r = await drafter.DraftAsync(description, body.KindHintSnake ?? body.KindHint, answers, ct);
            return Results.Ok(new
            {
                status = r.Status,
                questions = r.Questions,
                source = r.Source,
                note = r.Note,
                skill = r.Skill == null ? null : new
                {
                    name = r.Skill.Name,
                    kind = r.Skill.Kind,
                    use_when = r.Skill.UseWhen,
                    description = r.Skill.Description,
                    parameters = r.Skill.Parameters,
                    code = r.Skill.Code,
                    tests = r.Skill.Tests,
                    instructions = r.Skill.Instructions,
                    on_fail = r.Skill.OnFail,
                },
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- run a skill's tests in the sandbox ---------------------------------
        app.MapPost("/api/aicore/skills/test", (TestRequest body, SkillService skills) =>
        {
            var kind = (body.Kind ?? SkillKinds.Tool).Trim().ToLowerInvariant();
            if (!SkillKinds.IsCode(kind)) return Bad("Only tool and check skills have code to test.");
            var code = body.Code ?? "";
            var invalid = SkillRuntime.Validate(code, kind);
            var tests = body.Tests is JsonElement t && t.ValueKind == JsonValueKind.Array
                ? t : JsonDocument.Parse("[]").RootElement;
            var results = invalid == null
                ? skills.RunTests(kind, code, tests)
                : new List<SkillService.TestResult>();
            return Results.Ok(new
            {
                valid = invalid == null,
                error = invalid,
                total = results.Count,
                passed = results.Count(r => r.Pass),
                failed = results.Count(r => !r.Pass),
                results = results.Select(r => new
                {
                    name = r.Name, pass = r.Pass, got = r.Got, expected = r.Expected, error = r.Error, ms = r.Ms,
                }),
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- built-ins (read-only; always offered to the model as tools) --------
        app.MapGet("/api/aicore/skills/builtins", (SkillService skills) =>
        {
            var tz = skills.OrgTimeZone();
            var now = skills.UtcNow;
            object? Example(string name, string args)
            {
                try { return JsonDocument.Parse(BuiltinSkills.Execute(name, args, now, tz)).RootElement.Clone(); }
                catch { return null; }
            }
            var samples = new Dictionary<string, string>
            {
                ["current_time"] = "{}",
                ["current_week"] = "{}",
                ["date_add"] = "{\"amount\":3,\"unit\":\"business_days\"}",
                ["days_between"] = $"{{\"from\":\"{TimeZoneInfo.ConvertTime(now, tz).AddDays(-30):yyyy-MM-dd}\"}}",
                ["convert_timezone"] = "{\"to\":\"Europe/London\"}",
            };
            return Results.Ok(new
            {
                timezone = tz.Id,
                now = TimeZoneInfo.ConvertTime(now, tz).ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
                builtins = BuiltinSkills.All.Select(b => new
                {
                    name = b.Name,
                    label = b.Label,
                    description = b.Description,
                    parameters = JsonDocument.Parse(b.ParametersJson).RootElement.Clone(),
                    example_args = JsonDocument.Parse(samples[b.Name]).RootElement.Clone(),
                    example = Example(b.Name, samples[b.Name]),
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // ---- helpers ------------------------------------------------------------------

    private static List<object> List(AppDbContext db, ScopingService sc, int? entityId) =>
        sc.ScopeInherited(db.AgentSkills.AsNoTracking(), entityId)
            .Include(s => s.Entity)
            .OrderBy(s => s.EntityId == null ? 0 : 1).ThenBy(s => s.Position).ThenBy(s => s.Id)
            .ToList()
            .Select(Dto).ToList();

    /// <summary>
    /// Port of aicore/views.py <c>_skill_payload</c> plus the code-skill fields:
    /// `id` is the STRING skill_id (the flow builder and the page both key on
    /// it); `kind` + `enabled` let the flow builder's picker group them;
    /// `entity`/`scope` say whose skill it is (inherited vs this outlet's own).
    /// </summary>
    private static object Dto(AgentSkill s) => new
    {
        id = s.SkillId,
        name = s.Name,
        useWhen = s.UseWhen,
        instructions = s.Instructions,
        position = s.Position,
        kind = string.IsNullOrEmpty(s.Kind) ? SkillKinds.Prompt : s.Kind,
        description = s.Description,
        code = s.Code,
        parameters = ParseOrNull(s.Parameters),
        tests = ParseOrNull(s.Tests) ?? (object)Array.Empty<object>(),
        enabled = s.Enabled,
        channels = s.Channels,
        onFail = SkillService.NormalizeOnFail(s.OnFail),
        updatedAt = s.UpdatedAt,
        tool_name = s.Kind == SkillKinds.Tool ? SkillService.ToolName(s) : null,
        entity = s.Entity?.Slug,
        entity_name = s.Entity?.Name ?? "",
        scope = s.EntityId != null ? "entity" : "global",
    };

    private static JsonElement? ParseOrNull(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonDocument.Parse(raw).RootElement.Clone(); }
        catch { return null; }
    }

    /// <summary>Parameters/tests arrive as JSON (object/array) or as a JSON string.</summary>
    private static string? RawJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => (e.GetString() ?? "").Trim(),
        _ => e.GetRawText(),
    };

    private static bool IsJsonObject(string raw)
    {
        try { return JsonDocument.Parse(raw).RootElement.ValueKind == JsonValueKind.Object; }
        catch { return false; }
    }

    private static bool IsJsonArray(string raw)
    {
        try { return JsonDocument.Parse(raw).RootElement.ValueKind == JsonValueKind.Array; }
        catch { return false; }
    }

    private static string SkillId(string? raw)
    {
        var id = (raw ?? "").Trim();
        return id.Length > 64 ? id[..64] : id;
    }

    private static string Signature(AgentSkill s) => string.Join("\u0001", s.Name, s.UseWhen, s.Instructions,
        s.Kind, s.Description, s.Code, s.Parameters, s.Tests, s.Enabled, string.Join(",", s.Channels), s.OnFail);

    private static AgentSkill Add(AppDbContext db, AgentSkill s) { db.AgentSkills.Add(s); return s; }

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);

    private static void Audit(AppDbContext db, ClaimsPrincipal me, HttpContext ctx,
        string action, string target, string detail)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system",
                Action = action,
                Target = target,
                Detail = detail.Length > 500 ? detail[..500] : detail,
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* an audit failure must never fail the operation itself */ }
    }
}
