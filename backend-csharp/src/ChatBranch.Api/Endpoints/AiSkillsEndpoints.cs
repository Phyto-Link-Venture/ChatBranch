using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Bookings;
using ChatBranch.Data.Documents;
using ChatBranch.Data.Integrations;
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
///
/// Connected-app skills (kind <c>app</c>) carry a <c>config</c> instead of code
/// (validated on PUT; a row sent without <c>config</c> keeps its stored one);
/// <c>/apps</c> is their catalog and <c>/test</c> with <c>kind:"app"</c> previews
/// availability without booking anything.
/// </summary>
public static class AiSkillsEndpoints
{
    /// <summary>Wire shape of a skill: `id` is the string skill_id, and the
    /// fields are camelCase (`useWhen`, `onFail`) — Django's _skill_payload did
    /// the same. Every field past `position` is optional so a legacy client that
    /// only knows the prompt fields keeps working (absent = keep).</summary>
    public record SkillDto(string? Id, string? Name, string? UseWhen, string? Instructions, int Position,
        string? Kind = null, string? Description = null, string? Code = null, JsonElement? Parameters = null,
        JsonElement? Tests = null, bool? Enabled = null, List<string>? Channels = null, string? OnFail = null,
        JsonElement? Config = null,
        // "Available to": [slug|id…] — the scope the skill is saved in (the PUT's
        // `entity`) is always one of them; absent = keep. A global PUT takes none.
        JsonElement? Entities = null);

    public record SkillsWrite(string? Entity, List<SkillDto>? Skills);
    public record DraftAnswer(string? Question, string? Answer);
    /// <summary>`kind_hint` is the documented wire name; `kindHint` binds too.</summary>
    public record DraftRequest(string? Description, string? KindHint, List<DraftAnswer>? Answers,
        [property: JsonPropertyName("kind_hint")] string? KindHintSnake = null);
    /// <summary>Code skills: {kind, code, tests}. App skills (a DRY RUN, never
    /// books): {kind:"app", config, app_action:"availability", from_date?, to_date?, entity?}.</summary>
    public record TestRequest(string? Kind, string? Code, JsonElement? Tests, JsonElement? Config = null,
        [property: JsonPropertyName("app_action")] string? AppAction = null,
        [property: JsonPropertyName("from_date")] string? FromDate = null,
        [property: JsonPropertyName("to_date")] string? ToDate = null,
        string? Entity = null,
        [property: JsonPropertyName("document_type")] string? DocumentType = null);

    private static readonly HashSet<string> Kinds = new()
    {
        SkillKinds.Prompt, SkillKinds.Tool, SkillKinds.Check, SkillKinds.App,
    };

    public static void MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/aicore/skills", async (string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            // bounded by the caller's assigned outlets (ShellScope.ScopeLibrary)
            return Results.Ok(new { skills = List(db, ShellScope.ScopeLibrary(db.AgentSkills.AsNoTracking(), db, sc, me, entityId)) });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // the Skills page, and the flow builder's step skill picker
          .RequireAnyCapability("settings.ai.manage", "flows.manage");

        app.MapPut("/api/aicore/skills", async (SkillsWrite body, AppDbContext db, ScopingService sc,
            SkillService skills, ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (body.Skills == null) return Bad("Body must carry skills[].");
            var entityId = await InboxEndpoints.EntityId(db, body.Entity);
            // An unknown slug must not silently become "organization-wide" — the
            // prune below would then wipe the global library.
            if (entityId == null && !string.IsNullOrWhiteSpace(body.Entity) && body.Entity is not ("all" or "__all__"))
                return Bad($"Unknown entity '{body.Entity}'.");
            // The PUT prunes its scope: a bound caller (a company admin, an
            // assigned agent) may only save a level inside their own entities —
            // never the organization-wide set.
            if (ShellScope.BoundEntityIds(db, sc, me) is List<int> bound
                && (entityId is not int lvl || !bound.Contains(lvl)))
                return Results.Json(new { detail = entityId == null
                    ? "Only an organization-wide admin can save the organization-wide skills."
                    : "That entity is outside your scope." }, statusCode: 403);

            // Upsert within THIS scope, prune scope rows not in the payload
            // (the "sole writer" contract — only touches its own scope).
            var existing = await db.AgentSkills.Where(s => s.EntityId == entityId).ToListAsync();

            // 1) validate everything before touching a row
            var seen = new HashSet<string>();
            // "Available to" per skill: the full set (this scope first) when the skill sent one
            var picks = new Dictionary<string, List<int>>();
            foreach (var s in body.Skills)
            {
                var skillId = SkillId(s.Id);
                if (skillId.Length == 0) continue;
                if (!seen.Add(skillId)) return Bad($"Skill id '{skillId}' appears twice.");
                var current = existing.FirstOrDefault(e => e.SkillId == skillId);
                {
                    var pick = await LibraryScope.ParseAsync(db, s.Entities, null, false);
                    if (pick.Error != null) return pick.Error;
                    List<int>? after = null;
                    if (pick.Given)
                    {
                        if (entityId == null && pick.Ids.Count > 0)
                            return Bad($"Skill '{s.Name}': a skill limited to entities is saved under one of them, not organization-wide.");
                        after = pick.Ids.Where(i => i != entityId).ToList();
                        if (entityId is int home) after.Insert(0, home);
                        picks[skillId] = after;
                    }
                    else if (current == null) after = entityId is int home ? new List<int> { home } : new List<int>();
                    if (after != null)
                    {
                        var before = current == null ? null
                            : LibraryEntities.Ids(db, LibraryKinds.Skill, current.Id, current.EntityId);
                        if (await LibraryScope.Check(db, sc, me, before, after) is { } denied) return denied;
                    }
                }
                var kind = s.Kind is null ? current?.Kind ?? SkillKinds.Prompt : s.Kind.Trim().ToLowerInvariant();
                if (!Kinds.Contains(kind)) return Bad($"Skill '{s.Name}': kind must be prompt, tool, check or app.");
                if (kind == SkillKinds.App)
                {
                    if (await AppConfigProblem(db, sc, me, s, current, entityId) is string problem)
                        return Bad($"Skill '{s.Name}': {problem}");
                    continue;
                }
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
                // connected-app skills store their config normalized (defaults filled in)
                if (row.Kind == SkillKinds.App && (s.Config is JsonElement { ValueKind: JsonValueKind.Object } || isNew
                                                   || row.Config.Count == 0))
                    row.Config = AppSkills.Normalize(
                        s.Config is JsonElement { ValueKind: JsonValueKind.Object } cfgEl ? cfgEl
                            : JsonSerializer.SerializeToElement(row.Config), out _);
                // Position comes from LIST ORDER, not the payload field — the
                // settings page reorders by dragging rows and never sends one.
                row.Position = pos++;
                if (isNew) { created++; row.UpdatedAt = DateTime.UtcNow; }
                else if (Signature(row) != before) { updated++; row.UpdatedAt = DateTime.UtcNow; }
            }
            var removed = existing.Where(e => !keep.Contains(e.SkillId)).ToList();
            db.AgentSkills.RemoveRange(removed);
            LibraryEntities.Clear(db, LibraryKinds.Skill, removed.Select(r => r.Id));
            await db.SaveChangesAsync();
            // the extra entity ticks need the rows' ids, so they go in after the first save
            if (picks.Count > 0)
            {
                foreach (var (skillId, ids) in picks)
                {
                    var row = db.AgentSkills.Local.FirstOrDefault(r => r.SkillId == skillId && r.EntityId == entityId);
                    if (row != null) LibraryEntities.Set(db, row, ids, entityId);
                }
                await db.SaveChangesAsync();
            }
            if (created + updated + removed.Count > 0)
                Audit(db, me, ctx, "skills.update", body.Entity is { Length: > 0 } slug ? slug : "organization",
                    $"created {created}, updated {updated}, removed {removed.Count}"
                    + (removed.Count > 0 ? $" ({string.Join(", ", removed.Select(r => r.Name))})" : ""));
            // Echo the saved library in the GET shape — the page swaps its draft
            // for this response, so a `{count}` reply blanked the editor.
            return Results.Ok(new { skills = List(db, sc, entityId) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        // ---- describe → clarify → code ----------------------------------------
        app.MapPost("/api/aicore/skills/draft", async (DraftRequest body, SkillDrafter drafter,
            AppDbContext db, ScopingService sc, System.Security.Claims.ClaimsPrincipal me, CancellationToken ct) =>
        {
            if (QuotaGuard.Refusal(db, sc, me) is { } quota) return quota;
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
                // "make a PDF / invoice / QR / payment link": a ready-made Connected app
                // does this for real — a code skill can't (no files, no network)
                suggest_app = r.SuggestApp,
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
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        // ---- run a skill's tests in the sandbox ---------------------------------
        app.MapPost("/api/aicore/skills/test", async (TestRequest body, SkillService skills, AppDbContext db,
            ClaimsPrincipal me, BookingService bookings, CancellationToken ct) =>
        {
            var kind = (body.Kind ?? SkillKinds.Tool).Trim().ToLowerInvariant();
            if (kind == SkillKinds.App)
            {
                var app = body.Config is JsonElement ce ? AppSkills.AppOf(ce) : AppSkills.Calendar;
                return app == AppSkills.Calendar
                    ? await AppDryRunAsync(body, db, me, bookings, ct)
                    : await ToolAppDryRunAsync(body, app, db, skills, ct);
            }
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
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        // ---- connected apps the AI can use (the "Connected app" kind) ------------
        // What the skill wizard offers: the calendar app, its sources (with the
        // Google Calendar connections the CALLER may see — shared rows, their own
        // personal one, everyone's for admins), its actions and config defaults.
        app.MapGet("/api/aicore/skills/apps", async (string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var uid = await db.Users.AsNoTracking().Where(u => u.Username == me.Identity!.Name)
                .Select(u => (int?)u.Id).FirstOrDefaultAsync();
            var admin = ShellScope.IsOrgWideAdmin(db, me);   // a company admin sees only their own personal rows
            var scoped = await ShellScope.ScopeLibrary(db.Integrations.AsNoTracking(), db, sc, me, entityId)
                .Where(i => i.Provider == BookingService.GoogleProvider).Include(i => i.Owner).Include(i => i.Entity)
                .ToListAsync();
            var own = uid is int u0
                ? await db.Integrations.AsNoTracking().Where(i => i.Provider == BookingService.GoogleProvider
                        && i.OwnerUserId == u0).Include(i => i.Owner).Include(i => i.Entity).ToListAsync()
                : new List<Integration>();
            var rows = scoped.Concat(own).GroupBy(i => i.Id).Select(g => g.First())
                .Where(i => i.OwnerUserId == null || i.OwnerUserId == uid || admin)
                .Where(i => Cred(i, "access_token").Length > 0)
                .OrderBy(i => i.OwnerUserId == null ? 0 : 1).ThenBy(i => i.Id).ToList();
            var connections = rows.Select(i => new
            {
                id = i.Id,
                label = ConnectionLabel(i),
                owner = i.OwnerUserId != null ? "user" : i.EntityId != null ? "entity" : "org",
                owner_name = i.Owner?.Username ?? i.Entity?.Name ?? "Organization",
                calendar_id = GoogleCalendarClient.CalendarIdFor(i, null),
                status = i.Status,
            }).ToList();
            var sharedNow = new IntegrationResolver(db, sc);
            bool Connected(string provider)
            {
                try { return sharedNow.Active(provider, entityId, null) != null; }
                catch { return false; }
            }
            return Results.Ok(new
            {
                apps = new object[]
                {
                    new
                    {
                        app = CalendarAppConfig.AppId,
                        label = "Calendar booking",
                        description = "Let the AI check free times on a calendar and book, cancel or reschedule appointments for the customer.",
                        timezone = BuiltinSkills.OrgTimeZone(sc).Id,
                        sources = new object[]
                        {
                            new { source = BookingSources.Google, label = "Google Calendar",
                                  connected = connections.Count > 0, connections },
                            new { source = BookingSources.ChatBranch, label = "ChatBranch bookings",
                                  connected = true },
                        },
                        actions = new[]
                        {
                            new { id = "availability", label = "Check availability" },
                            new { id = "book", label = "Book appointments" },
                            new { id = "cancel", label = "Cancel / reschedule" },
                        },
                        defaults = CalendarAppConfig.Defaults(),
                    },
                    new
                    {
                        app = DocumentAppConfig.AppId,
                        label = "Documents (PDF)",
                        description = "Let the AI create PDF invoices, quotations, receipts and letters on your letterhead and send them to the customer as a file. The server does every sum and numbers each document.",
                        connected = true,
                        actions = DocumentAppConfig.AllActions.Select(a => new { id = a, label = DocumentTypes.Label(a) }).ToList(),
                        accents = DocumentAppConfig.Accents.Keys.ToList(),
                        cjk_font = DocumentFonts.CjkAvailable,
                        defaults = DocumentAppConfig.Defaults(),
                    },
                    new
                    {
                        app = QrAppConfig.AppId,
                        label = "QR codes",
                        description = "Let the AI turn a link or short text into a QR code picture sent with its reply.",
                        connected = true,
                        defaults = QrAppConfig.Defaults(),
                    },
                    new
                    {
                        app = PaymentAppConfig.AppId,
                        label = "Payment links",
                        description = "Let the AI create a secure Stripe payment link for an agreed amount.",
                        connected = Connected("stripe"),
                        provider_label = "Stripe",
                        defaults = PaymentAppConfig.Defaults(),
                    },
                    new
                    {
                        app = SheetsAppConfig.AppId,
                        label = "Google Sheets log",
                        description = "Let the AI log a row (a lead, an order) to a Google Sheet when its reply is sent.",
                        connected = Connected("google_sheets"),
                        provider_label = "Google Sheets",
                        defaults = SheetsAppConfig.Defaults(),
                    },
                },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        // ---- the Documents app's logo (printed on the letterhead) ---------------
        app.MapPost("/api/aicore/skills/documents/logo", async (HttpContext ctx, DocumentService documents) =>
        {
            if (!ctx.Request.HasFormContentType) return Bad("file is required (multipart/form-data).");
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files["file"];
            if (file == null || file.Length == 0) return Bad("file is required (multipart/form-data).");
            if (file.Length > DocumentService.MaxLogoBytes) return Bad("The logo must be a PNG or JPEG up to 1 MB.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var name = documents.SaveLogo(ms.ToArray(), out var error);
            return name == null ? Bad(error ?? "Upload failed.") : Results.Ok(new { logo = name, url = $"/api/aicore/skills/documents/logo/{name}" });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage").DisableAntiforgery();

        app.MapGet("/api/aicore/skills/documents/logo/{name}", (string name, DocumentService documents) =>
        {
            var path = documents.LogoPath(name);
            return path == null
                ? Results.NotFound(new { detail = "Logo not found." })
                : Results.File(path, name.EndsWith(".png") ? "image/png" : "image/jpeg");
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

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
                [BuiltinSkills.RequestHandover] = "{\"reason\":\"The customer asks for compensation above the standard limit\"}",
                [BuiltinSkills.Calculate] = "{\"expression\":\"3 * 45.90 + 8%\",\"decimals\":2}",
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
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");
    }

    // ---- helpers ------------------------------------------------------------------

    private static List<object> List(AppDbContext db, ScopingService sc, int? entityId) =>
        List(db, sc.ScopeInherited(db.AgentSkills.AsNoTracking(), entityId));

    private static List<object> List(AppDbContext db, IQueryable<AgentSkill> scoped)
    {
        var rows = scoped
            .Include(s => s.Entity)
            .OrderBy(s => s.EntityId == null ? 0 : 1).ThenBy(s => s.Position).ThenBy(s => s.Id)
            .ToList();
        var scopes = LibraryEntities.Load(db, rows);
        return rows.Select(s => Dto(s, scopes)).ToList();
    }

    /// <summary>
    /// Port of aicore/views.py <c>_skill_payload</c> plus the code-skill fields:
    /// `id` is the STRING skill_id (the flow builder and the page both key on
    /// it); `kind` + `enabled` let the flow builder's picker group them;
    /// `entity`/`scope` say whose skill it is (inherited vs this outlet's own).
    /// </summary>
    private static object Dto(AgentSkill s, Dictionary<int, List<EntityRef>> scopes) => new
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
        // connected-app skills: the full config (defaults filled in); {} for other kinds
        config = s.Kind == SkillKinds.App ? AppSkills.Normalize(s.Config) : s.Config,
        app = s.Kind == SkillKinds.App ? AppSkills.AppOf(s.Config) : null,
        entity = s.Entity?.Slug,
        entity_name = s.Entity?.Name ?? "",
        scope = s.EntityId != null ? "entity" : "global",
        // "Available to": every entity it was ticked for (the scope it is saved in first)
        entities = LibraryEntities.Payload(scopes, s.Id),
        global = s.EntityId == null,
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
        s.Kind, s.Description, s.Code, s.Parameters, s.Tests, s.Enabled, string.Join(",", s.Channels), s.OnFail,
        JsonSerializer.Serialize(s.Config));

    private static AgentSkill Add(AppDbContext db, AgentSkill s) { db.AgentSkills.Add(s); return s; }

    // ---- connected-app skills ------------------------------------------------------

    /// <summary>
    /// Why an app skill's config can't be saved (null = fine): an invalid config
    /// (bad times, overlapping windows, duration/max-days out of range, unknown
    /// action/source/app) or — when the calendar choice is new or changed — a
    /// numeric calendar that isn't a connected Google Calendar this skill's scope
    /// can use and the caller may see. An unchanged calendar is not re-checked, so
    /// a later disconnect never blocks saving the rest of the library.
    /// </summary>
    private static async Task<string?> AppConfigProblem(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        SkillDto s, AgentSkill? current, int? entityId)
    {
        JsonElement el;
        if (s.Config is JsonElement given && given.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (given.ValueKind != JsonValueKind.Object) return "config must be a JSON object.";
            el = given;
        }
        else el = JsonSerializer.SerializeToElement(current?.Config ?? new Dictionary<string, object?>());
        // every app validates its own config; only a calendar names a connection to check
        AppSkills.Normalize(el, out var error);
        if (error != null) return error;
        if (AppSkills.AppOf(el) != AppSkills.Calendar) return null;
        var cfg = CalendarAppConfig.From(el, out _);
        if (cfg.Source != BookingSources.Google || cfg.IntegrationId is not int iid) return null;

        var before = current == null ? null : CalendarAppConfig.From(current.Config);
        if (before != null && before.Source == cfg.Source && before.Calendar == cfg.Calendar) return null;
        var row = await db.Integrations.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == iid && i.Provider == BookingService.GoogleProvider);
        if (row == null) return $"calendar {iid} is not a Google Calendar connection.";
        if (Cred(row, "access_token").Length == 0)
            return $"the Google Calendar connection {iid} is not connected — reconnect it at Integrations first.";
        var admin = ShellScope.IsOrgWideAdmin(db, me);   // a company admin sees only their own personal rows
        if (row.OwnerUserId is int owner && !admin)
        {
            var uid = await db.Users.AsNoTracking().Where(u => u.Username == (me.Identity!.Name ?? ""))
                .Select(u => (int?)u.Id).FirstOrDefaultAsync();
            if (owner != uid) return $"calendar {iid} is someone else's personal calendar.";
        }
        if (row.EntityId is int rowEntity && entityId is int skillEntity
            && !Lineage(db, sc, skillEntity).Contains(rowEntity))
            return $"calendar {iid} belongs to another outlet — pick one this outlet can use.";
        return null;
    }

    /// <summary>An entity, its ancestors and its subtree — the outlets whose
    /// connections a skill of that entity may book into.</summary>
    private static HashSet<int> Lineage(AppDbContext db, ScopingService sc, int entityId)
    {
        var ids = new HashSet<int>(sc.EntitySubtreeIds(entityId));
        int? cursor = entityId;
        for (var depth = 0; depth < 32 && cursor is int c; depth++)
        {
            cursor = db.Entities.AsNoTracking().Where(e => e.Id == c).Select(e => e.ParentId).FirstOrDefault();
            if (cursor is int p && !ids.Add(p)) break;
        }
        return ids;
    }

    /// <summary>
    /// "Preview availability" for an app skill being configured: the free times
    /// the AI would see, computed exactly as at reply time — a DRY RUN that never
    /// reserves or books. "The conversation's assigned agent" previews with the
    /// caller as that agent. Always 200 with {ok, slots, timezone, source, detail}.
    /// </summary>
    private static async Task<IResult> AppDryRunAsync(TestRequest body, AppDbContext db, ClaimsPrincipal me,
        BookingService bookings, CancellationToken ct)
    {
        var sc = new ScopingService(db);
        var orgTz = BuiltinSkills.OrgTimeZone(sc).Id;
        // {ok, slots, timezone, source, detail} + calendar_label / from_date / to_date / total when known
        IResult Answer(bool ok, string source, string tz, string detail, IEnumerable<object>? slots = null,
            Dictionary<string, object?>? extra = null)
        {
            var payload = new Dictionary<string, object?>
            {
                ["ok"] = ok, ["slots"] = (slots ?? Array.Empty<object>()).ToList(), ["timezone"] = tz,
                ["source"] = source, ["detail"] = detail,
                ["calendar_label"] = null, ["from_date"] = null, ["to_date"] = null, ["total"] = null,
            };
            foreach (var (k, v) in extra ?? new()) payload[k] = v;
            return Results.Ok(payload);
        }

        var el = body.Config is JsonElement c && c.ValueKind == JsonValueKind.Object
            ? c : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        var cfg = CalendarAppConfig.From(el, out var error);
        if (error != null) return Answer(false, cfg.Source, orgTz, error);
        var action = (body.AppAction ?? "availability").Trim().ToLowerInvariant();
        if (action != "availability")
            return Answer(false, cfg.Source, orgTz, "Only availability can be previewed — nothing is ever booked from here.");

        try
        {
            var entityId = await InboxEndpoints.EntityId(db, body.Entity);
            var tz = bookings.TimeZoneFor(cfg);
            var uid = await db.Users.AsNoTracking().Where(u => u.Username == (me.Identity!.Name ?? ""))
                .Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
            var skill = new AgentSkill { SkillId = "preview", EntityId = entityId, Kind = SkillKinds.App };
            var target = bookings.ResolveTarget(skill, cfg, entityId, uid);
            if (!target.Ok)
                return Answer(false, cfg.Source, tz.Id,
                    $"{target.Error} Connect Google Calendar at Integrations, or use ChatBranch bookings.");

            var today = BookingAvailability.Today(bookings.UtcNow, tz);
            var from = BookingService.ParseDate(body.FromDate, today) ?? today;
            var to = BookingService.ParseDate(body.ToDate, today) ?? from.AddDays(7);
            if (to < from) (from, to) = (to, from);
            var r = await bookings.FreeSlotsAsync(cfg, target, tz, from, to, ct: ct);
            var extra = new Dictionary<string, object?>
            {
                ["calendar_label"] = target.Label,
                ["from_date"] = r.From.ToString("yyyy-MM-dd"),
                ["to_date"] = r.To.ToString("yyyy-MM-dd"),
                ["total"] = r.Free.Count,
            };
            if (!r.Ok) return Answer(false, cfg.Source, tz.Id, r.Error, extra: extra);
            var offered = BookingAvailability.Spread(r.Free);
            var who = cfg.Source == BookingSources.Google && cfg.Calendar == "assignee"
                ? " Previewed with you as the conversation's assigned agent." : "";
            var detail = r.Free.Count == 0
                ? $"No free times between {r.From:yyyy-MM-dd} and {r.To:yyyy-MM-dd} on {target.Label}.{who}"
                : $"{r.Free.Count} free time{(r.Free.Count == 1 ? "" : "s")} between {r.From:yyyy-MM-dd} and "
                  + $"{r.To:yyyy-MM-dd} on {target.Label}; the AI is shown up to {BookingAvailability.MaxOffered} "
                  + $"spread across the days.{who} Nothing was booked.";
            return Answer(true, cfg.Source, tz.Id, detail, offered.Select(sl => (object)new
            {
                start = BookingAvailability.LocalIso(sl.LocalStart),
                end = BookingAvailability.LocalIso(sl.LocalEnd),
                label = BookingAvailability.StartLabel(sl.LocalStart),
            }), extra);
        }
        catch (Exception ex)
        {
            return Answer(false, cfg.Source, orgTz, $"The preview failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// The dry run of a non-calendar app skill being configured — nothing reaches
    /// a customer: Documents renders a SAMPLE document with the unsaved settings
    /// into a throw-away preview (no number taken), QR renders a sample code, and
    /// Payments / Sheets say whether their connection is there. Always 200 with
    /// {ok, detail, url?, number?, total?}.
    /// </summary>
    private static async Task<IResult> ToolAppDryRunAsync(TestRequest body, string app, AppDbContext db,
        SkillService skills, CancellationToken ct)
    {
        var el = body.Config is JsonElement c && c.ValueKind == JsonValueKind.Object
            ? c : JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        AppSkills.Normalize(el, out var error);
        if (error != null) return Results.Ok(new { ok = false, detail = error });
        try
        {
            var entityId = await InboxEndpoints.EntityId(db, body.Entity);
            switch (app)
            {
                case AppSkills.Documents:
                {
                    var cfg = DocumentAppConfig.From(el, out _);
                    var type = DocumentAppConfig.AllActions.Contains(body.DocumentType ?? "") ? body.DocumentType!
                        : cfg.AllowedActions.FirstOrDefault() ?? DocumentTypes.Invoice;
                    var r = await skills.Documents.PreviewSampleAsync(type, cfg, skills.UtcNow, skills.OrgTimeZone(), ct);
                    return Results.Ok(new
                    {
                        ok = r.Error == null, detail = r.Error ?? $"A sample {DocumentTypes.Label(type).ToLowerInvariant()} "
                            + "with your settings. Nothing was sent and no number was used.",
                        url = r.Url, number = r.Number, total = r.Total, currency = cfg.Currency,
                    });
                }
                case AppSkills.Qr:
                {
                    var r = await skills.Documents.PreviewQrAsync("https://example.com", QrAppConfig.From(el, out _), ct);
                    return Results.Ok(new { ok = true, detail = "A sample QR code (it opens https://example.com).", url = r });
                }
                default:
                {
                    var provider = app == AppSkills.Payments ? "stripe" : "google_sheets";
                    var label = app == AppSkills.Payments ? "Stripe" : "Google Sheets";
                    var row = new IntegrationResolver(db, new ScopingService(db)).Active(provider, entityId, null);
                    if (row == null)
                        return Results.Ok(new { ok = false, detail = $"{label} is not connected for this outlet — connect it at Integrations. Until then the AI says it can't do this and nothing is pretended." });
                    if (app == AppSkills.Sheets && SheetsAppConfig.From(el, out _).SpreadsheetId.Length == 0)
                        return Results.Ok(new { ok = false, detail = "Google Sheets is connected — now paste the spreadsheet's link or id." });
                    return Results.Ok(new { ok = true, detail = $"{label} is connected ({(row.EntityId != null ? "this outlet's connection" : "the organization's connection")}). Nothing was created." });
                }
            }
        }
        catch (Exception ex)
        {
            return Results.Ok(new { ok = false, detail = $"The preview failed ({ex.GetType().Name})." });
        }
    }

    private static string ConnectionLabel(Integration i)
    {
        var name = i.Config.TryGetValue("name", out var n) ? n?.ToString()?.Trim() ?? "" : "";
        if (name.Length > 0 && name != "Google Calendar") return name;
        if (i.OwnerUserId != null) return $"{i.Owner?.Username ?? "A teammate"}'s Google Calendar";
        if (i.EntityId != null) return $"{i.Entity?.Name ?? "Outlet"} shared calendar";
        return "Organization calendar";
    }

    private static string Cred(Integration i, string key) =>
        i.Credentials.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

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
