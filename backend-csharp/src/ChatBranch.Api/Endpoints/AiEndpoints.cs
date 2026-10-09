using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// aicore surface (/api/aicore/*): persona (superadmin), knowledge base CRUD +
/// preview (admin+), skills (admin+, the SOLE writer of crm.AgentSkill — see
/// AiSkillsEndpoints), and a manual reply endpoint that runs the
/// channel-agnostic assistant.
/// </summary>
public static class AiEndpoints
{
    public record PersonaWrite(string? Persona, string? Entity,
        [property: System.Text.Json.Serialization.JsonPropertyName("house_rules")] string? HouseRules = null);
    /// <remarks>
    /// EVERY field is optional so PATCH is a real partial update: the list's
    /// on/off button sends `{"active": false}` alone, and non-nullable Kind /
    /// Title / Content used to bind as null and 500 on the NOT NULL columns
    /// (the toggle simply did nothing). Create supplies its own defaults.
    /// </remarks>
    public record KnowledgeWrite(string? Kind, string? Title, string? Question, string? Content,
        List<string>? Tags, List<string>? Channels, int? Priority, bool? Active,
        DateTime? StartsAt, DateTime? EndsAt, string? Entity,
        // Help Center: Published is PUBLIC readability, distinct from Active
        // (whether the AI may use it).
        bool? Published = null, string? Slug = null, string? Category = null);
    /// <summary>`text` is what the dashboard's preview box posts; `query` is the
    /// Django name. Accept both — binding only `query` made the preview silently
    /// ignore the message you typed and always return the whole block.</summary>
    public record PreviewRequest(string? Query, string? Text, string? Channel, string? Entity);
    public record ReplyRequest(string Channel, string Key);

    public static void MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- persona + house rules ----------------------------------------------
        // The persona is ONE value: the nearest level of the entity tree that sets
        // it wins (outlet → parent → … → organization → built-in default). House
        // rules ADD UP: an outlet keeps every rule above it and adds its own.
        app.MapGet("/api/aicore/persona", async (string? entity, AppDbContext db,
            PersonaService persona, ScopingService sc, ClaimsPrincipal me) =>
        {
            var (entityId, err) = await AiControlsEndpoints.EntityArg(db, entity);
            if (err != null) return err;
            return Results.Ok(await PersonaPayload(db, persona, sc, me, entityId));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        app.MapPut("/api/aicore/persona", async (PersonaWrite body, AppDbContext db, PersonaService persona,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var (entityId, err) = await AiControlsEndpoints.EntityArg(db, body.Entity);
            if (err != null) return err;
            if (!AiControlsEndpoints.MayEdit(db, sc, me, entityId))
                return Results.Json(new
                {
                    detail = entityId == null
                        ? "Only an admin can change the organization-wide persona and house rules."
                        : "That outlet is outside your scope.",
                }, statusCode: 403);
            if (body.Persona == null && body.HouseRules == null)
                return Results.BadRequest(new { detail = "Send persona and/or house_rules." });
            if ((body.Persona?.Length ?? 0) > 8000)
                return Results.BadRequest(new { detail = "The persona is too long (8000 characters at most)." });
            if ((body.HouseRules?.Length ?? 0) > PersonaService.MaxHouseRuleChars)
                return Results.BadRequest(new { detail = $"House rules are too long ({PersonaService.MaxHouseRuleChars} characters at most)." });

            var changed = new List<string>();
            void Write(string key, string? value)
            {
                var v = (value ?? "").Trim();
                // an outlet's blank value = no override: drop the row so it inherits again
                if (v.Length == 0 && entityId != null) sc.ClearSetting(key, entityId);
                else sc.SetSetting(key, v, entityId);
            }
            if (body.Persona != null) { Write("aicore_persona", body.Persona); changed.Add("persona"); }
            if (body.HouseRules != null)
            {
                Write(PersonaService.HouseRulesKey, string.Join("\n", PersonaService.SplitRules(body.HouseRules)));
                changed.Add("house rules");
            }
            var ent = await EntityRow(db, entityId);
            ChatBranch.Data.Audit.AuditTrail.Write(db, "ai.persona", ent != null ? $"entity:{ent.Slug}" : "global",
                $"Updated the {string.Join(" and ", changed)}", entityId: entityId);
            return Results.Ok(await PersonaPayload(db, persona, sc, me, entityId));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        // ---- knowledge base ----------------------------------------------------
        app.MapGet("/api/aicore/knowledge", async (string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var ent = await EntityRow(db, entityId);
            // Include(Entity) so each item can report its OWNING outlet (which is
            // not the outlet being viewed — inherited global rows carry null).
            // bounded by the caller's assigned outlets (ShellScope.ScopeLibrary)
            var rows = ShellScope.ScopeLibrary(db.KnowledgeItems.AsNoTracking(), db, sc, me, entityId)
                .Include(k => k.Entity)
                .OrderByDescending(k => k.Priority).ThenByDescending(k => k.UpdatedAt).ToList();
            // Django-contract shape: the KnowledgeSettings page reads items/counts/
            // kinds — returning a bare list left `data.kinds.map` undefined and
            // crashed the page. KIND_CHOICES mirror crm.KnowledgeItem.
            var kinds = new[]
            {
                new { id = "fact", label = "Business fact" },
                new { id = "policy", label = "Policy / T&C" },
                new { id = "faq", label = "FAQ" },
                new { id = "event", label = "Event / promotion" },
                new { id = "notice", label = "Sudden notice" },
            };
            var counts = kinds.ToDictionary(k => k.id, k => rows.Count(r => r.Kind == k.id));
            var now = DateTime.UtcNow;
            return Results.Ok(new
            {
                items = rows.Select(k => KnowledgeDto(k, now)),
                counts,
                kinds,
                budget = 1800,
                entity = ent?.Slug,
                entity_name = ent?.Name ?? "",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        app.MapPost("/api/aicore/knowledge", async (KnowledgeWrite b, AppDbContext db) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, b.Entity);
            var row = new KnowledgeItem
            {
                EntityId = entityId, Kind = b.Kind ?? "fact", Title = b.Title ?? "",
                Question = b.Question ?? "", Content = b.Content ?? "",
                Tags = b.Tags ?? new(), Channels = b.Channels ?? new(),
                Priority = b.Priority ?? 0, Active = b.Active ?? true,
                Published = b.Published ?? false,
                Slug = (b.Slug ?? "").Trim().ToLowerInvariant(),
                Category = (b.Category ?? "").Trim(),
                StartsAt = b.StartsAt, EndsAt = b.EndsAt,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.KnowledgeItems.Add(row);
            await db.SaveChangesAsync();
            Workers.EmbeddingWorker.Kick(); // semantic index catches up off the request
            return Results.Ok(KnowledgeDto(row, DateTime.UtcNow, await EntityRow(db, row.EntityId)));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        // Bound as raw JSON, not a DTO: a PATCH must tell "field absent" (leave
        // it) from "field null/empty" (clear it) — the date window is cleared by
        // sending "", and the list's on/off button sends `{"active": …}` alone.
        app.MapMethods("/api/aicore/knowledge/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement b, AppDbContext db) =>
        {
            var row = await db.KnowledgeItems.FindAsync(id);
            if (row == null) return Results.NotFound();
            if (KStr(b, "kind") is string kind && kind.Length > 0) row.Kind = kind;
            if (KStr(b, "title") is string title) row.Title = title;
            if (KStr(b, "question") is string question) row.Question = question;
            if (KStr(b, "content") is string content) row.Content = content;
            if (KList(b, "tags") is List<string> tags) row.Tags = tags;
            if (KList(b, "channels") is List<string> channels) row.Channels = channels;
            if (KInt(b, "priority") is int prio) row.Priority = prio;
            if (KBool(b, "active") is bool active) row.Active = active;
            if (KBool(b, "published") is bool pub) row.Published = pub;
            if (KStr(b, "slug") is string slug) row.Slug = slug.Trim().ToLowerInvariant();
            if (KStr(b, "category") is string cat) row.Category = cat;
            if (KHas(b, "starts_at")) row.StartsAt = KDate(b, "starts_at");
            if (KHas(b, "ends_at")) row.EndsAt = KDate(b, "ends_at");
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Workers.EmbeddingWorker.Kick();
            return Results.Ok(KnowledgeDto(row, DateTime.UtcNow, await EntityRow(db, row.EntityId)));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        app.MapDelete("/api/aicore/knowledge/{id:int}", async (int id, AppDbContext db) =>
        {
            await db.KnowledgeItems.Where(k => k.Id == id).ExecuteDeleteAsync();
            await db.AiEmbeddings.Where(e => e.OwnerType == EmbeddingOwner.Knowledge && e.OwnerId == id).ExecuteDeleteAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage");

        // The exact blocks the AI receives — "why did the bot say that?" as a button —
        // plus every scored retrieval candidate (the "Test retrieval" debug view).
        app.MapPost("/api/aicore/knowledge/preview", async (PreviewRequest b, AppDbContext db,
            KnowledgeService knowledge) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, b.Entity);
            var query = !string.IsNullOrWhiteSpace(b.Query) ? b.Query : b.Text;
            var c = knowledge.Compose(query, entityId, b.Channel, withLessons: true);
            var block = c.Knowledge;
            // Django-contract shape (aicore/views.py knowledge_preview): the page
            // renders `chars` / `budget` — omitting budget crashed it on
            // `preview.budget.toLocaleString`.
            return Results.Ok(new
            {
                block,
                full_prompt_block = c.Learned.Length == 0 ? block : (block.Length == 0 ? c.Learned : block + "\n\n" + c.Learned),
                chars = block.Length,
                budget = KnowledgeService.Budget,
                items = block.Split('\n').Count(ln => !string.IsNullOrWhiteSpace(ln)),
                learned_block = c.Learned,
                learned_chars = c.Learned.Length,
                learned_budget = KnowledgeService.LearnedBudget,
                semantic = c.Semantic,
                provider = c.Provider,
                model = c.Model,
                hits = c.Hits.Select(h => new
                {
                    type = h.Type, id = h.Id,
                    title = h.ChunkCount > 1 ? $"{h.Title} (part {h.ChunkIndex + 1}/{h.ChunkCount})" : h.Title,
                    chunk = TextAnalysis.Clip(h.Chunk, 400),
                    score = Math.Round(h.Score, 3), semantic = Math.Round(h.Semantic, 3),
                    keyword = Math.Round(h.Keyword, 3),
                }),
                grounding = c.Grounding.Select(g => g.ToMeta()),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // the Knowledge page and the AI monitor's retrieval tester
          .RequireAnyCapability("settings.ai.manage", "ai.monitor");

        // ---- document upload: PDF / .txt / .md / .csv → one knowledge item ---------
        // The text is extracted and kept; the file itself never is. Read with
        // ReadFormAsync (no IFormFile binding → no antiforgery metadata).
        app.MapPost("/api/aicore/knowledge/upload", async (HttpContext ctx, AppDbContext db, ClaimsPrincipal me) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { detail = "Upload a file (multipart/form-data, field 'file')." });
            if (ctx.Request.ContentLength is long len && len > DocumentExtractor.MaxBytes + 256 * 1024)
                return Results.Json(new { detail = "That file is too large — the limit is 10 MB." }, statusCode: 413);
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(); }
            catch (Exception) { return Results.BadRequest(new { detail = "The upload could not be read." }); }
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { detail = "Choose a file to upload." });
            if (file.Length > DocumentExtractor.MaxBytes)
                return Results.Json(new { detail = "That file is too large — the limit is 10 MB." }, statusCode: 413);
            var name = Path.GetFileName(file.FileName ?? "").Trim();
            if (name.Length == 0) name = "document.txt";
            if (!DocumentExtractor.Supported(name))
                return Results.Json(new { detail = "Upload a PDF, .txt, .md or .csv file." }, statusCode: 415);
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            var doc = DocumentExtractor.Extract(name, bytes);
            if (!doc.Ok) return Results.BadRequest(new { detail = doc.Error });

            var kinds = new[] { "fact", "policy", "faq", "event", "notice" };
            var kind = form["kind"].ToString().Trim().ToLowerInvariant();
            if (!kinds.Contains(kind)) kind = "fact";
            var title = form["title"].ToString().Trim();
            var entityId = await InboxEndpoints.EntityId(db, form["entity"].ToString());
            var channels = form["channels"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
            var now = DateTime.UtcNow;
            var row = new KnowledgeItem
            {
                EntityId = entityId, Kind = kind, Title = title.Length > 0 ? title : name, Content = doc.Text,
                Channels = channels, Active = true, Published = false, SourceFile = name,
                CreatedBy = me.Identity?.Name ?? "", CreatedAt = now, UpdatedAt = now,
            };
            db.KnowledgeItems.Add(row);
            await db.SaveChangesAsync();
            ChatBranch.Data.Audit.AuditTrail.Write(db, "knowledge.upload", $"knowledge:{row.Id}",
                $"{name} ({doc.Text.Length} chars)", entityId: entityId);
            Workers.EmbeddingWorker.Kick();
            return Results.Ok(new
            {
                item = KnowledgeDto(row, now, await EntityRow(db, row.EntityId)),
                chunks = RetrievalService.ChunksFor(row).Count,
                chars = doc.Text.Length,
                pages = doc.Pages,
                truncated = doc.Truncated,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai.manage").DisableAntiforgery();

        // ---- skills (sole writer of crm.AgentSkill) + code-skill authoring ----
        app.MapSkillEndpoints();

        // ---- manual reply (playground / operator "AI draft") -------------------
        // ---- booking hours (per-day appointment schedule; Setting booking_hours,
        //      entity-scoped) — drives the slots the AI offers + the booking guard,
        //      and belongs with the Google Calendar integration in the UI. --------
        app.MapGet("/api/aicore/booking-hours", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            return Results.Ok(BookingHoursPayload(sc, entityId, entity));
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // the booking-hours card on Integrations, or the AI configuration
          .RequireAnyCapability("integrations.view", "settings.ai.manage");

        app.MapPut("/api/aicore/booking-hours", async (string? entity, HttpContext ctx, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Json(new { detail = "invalid body" }, statusCode: 400); }
            // Accept {hours:{...}} or a bare {...} map of day -> [open,close]|null.
            var hours = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("hours", out var h) ? h : body;
            sc.SetSetting("booking_hours", hours.GetRawText(), entityId);
            return Results.Ok(BookingHoursPayload(sc, entityId, entity));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapDelete("/api/aicore/booking-hours", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            if (entityId == null)
                return Results.Json(new { detail = "Select an entity to remove its override. The organization default is edited, not deleted." }, statusCode: 400);
            sc.ClearSetting("booking_hours", entityId);
            return Results.Ok(BookingHoursPayload(sc, entityId, entity));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapPost("/api/aicore/reply", async (ReplyRequest body, ThreadStore store,
            AssistantService assistant, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var thread = store.GetThread(body.Channel, body.Key);
            // the conversation names come in the body, so the route filter can't see
            // them: the inbox's scope rule is applied here (out of scope = not found)
            if (thread == null || !ThreadAccessFilter.InScope(db, sc, me, thread.EntityId))
                return Results.NotFound(new { detail = "thread not found" });
            // An operator-requested draft: no handover side effects. `reply` is
            // what may be sent ("" when a reply check held it back); `draft` is
            // what the model wrote either way, `meta` the audit trail.
            var r = await assistant.GenerateReplyAsync(thread, applyOutcome: false);
            // a preview is never delivered through the AI gate, so it books nothing:
            // give back whatever its calendar tools reserved
            assistant.ReleasePending(r, "Operator preview — not sent through the AI, nothing was booked.");
            return Results.Ok(new { reply = r.Text, draft = r.Draft, action = r.Action, reason = r.Reason,
                meta = r.ToMeta() });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send");
    }

    private static Dictionary<string, int[]> DefaultBookingHours() => new()
    {
        ["mon"] = new[] { 9, 21 }, ["tue"] = new[] { 9, 21 }, ["wed"] = new[] { 9, 21 },
        ["thu"] = new[] { 9, 21 }, ["fri"] = new[] { 9, 21 },
    };

    private static object BookingHoursRaw(ScopingService sc, int? entityId)
    {
        var raw = sc.SettingValue("booking_hours", entityId);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return doc.RootElement.Clone();
            }
            catch { /* fall through to default */ }
        }
        return DefaultBookingHours();
    }

    private static object BookingHoursPayload(ScopingService sc, int? entityId, string? entitySlug) => new
    {
        hours = BookingHoursRaw(sc, entityId),
        scope = sc.SettingScope("booking_hours", entityId),
        entity = entitySlug,
        global_hours = BookingHoursRaw(sc, null),
        @default = DefaultBookingHours(),
    };

    /// <summary>Resolve an entity row for its slug/name, or null for "All entities".</summary>
    /// <summary>
    /// What the persona editor shows for one level: the effective persona and
    /// where it comes from, this level's own override, what it would inherit
    /// without one, and the house rules — inherited (read-only here) + its own.
    /// </summary>
    internal static async Task<object> PersonaPayload(AppDbContext db, PersonaService persona, ScopingService sc,
        ClaimsPrincipal me, int? entityId)
    {
        var ent = await EntityRow(db, entityId);
        var names = await db.Entities.AsNoTracking()
            .Select(e => new { e.Id, e.Name, e.Slug }).ToDictionaryAsync(e => e.Id);
        object Level(int? at) => at is int i && names.TryGetValue(i, out var n)
            ? new { scope = at == entityId ? "entity" : "parent", entity = n.Slug, entity_name = n.Name }
            : new { scope = "organization", entity = (string?)null, entity_name = "the organization" };

        var (value, at) = sc.SettingSource("aicore_persona", entityId);
        var own = entityId is int eid
            ? (await db.Settings.AsNoTracking().Where(x => x.Key == "aicore_persona" && x.EntityId == eid)
                .Select(x => x.Value).FirstOrDefaultAsync() ?? "").Trim()
            : value;
        // what this level would use without its own override: the level above's answer
        var parentId = entityId is int pid
            ? await db.Entities.AsNoTracking().Where(e => e.Id == pid).Select(e => e.ParentId).FirstOrDefaultAsync()
            : null;
        var (aboveValue, aboveAt) = entityId == null ? ("", (int?)null) : sc.SettingSource("aicore_persona", parentId);
        if (entityId != null && parentId == null) (aboveValue, aboveAt) = (sc.SettingValue("aicore_persona", null), null);

        var rules = persona.HouseRules(entityId);
        return new
        {
            persona = value.Length > 0 ? value : PersonaService.DefaultPersona,
            is_default = value.Length == 0,
            source = value.Length > 0 ? Level(at) : new { scope = "default", entity = (string?)null, entity_name = "the built-in default" },
            own_persona = own,
            inherited_persona = aboveValue.Length > 0 ? aboveValue : PersonaService.DefaultPersona,
            inherited_source = aboveValue.Length > 0 ? Level(aboveAt)
                : new { scope = "default", entity = (string?)null, entity_name = "the built-in default" },
            @default = PersonaService.DefaultPersona,
            hard_rules = PersonaService.HardRules,
            house_rules = new
            {
                own = rules.Where(r => r.At == entityId).Select(r => r.Rule).ToList(),
                inherited = rules.Where(r => r.At != entityId).Select(r => new
                {
                    rule = r.Rule,
                    from = Level(r.At),
                }).ToList(),
                max_chars = PersonaService.MaxHouseRuleChars,
            },
            preamble = persona.SystemPreamble(entityId),
            entity = ent?.Slug,
            entity_name = ent?.Name ?? "",
            scope = sc.SettingScope("aicore_persona", entityId),
            global_persona = persona.PersonaBody(null),
            can_manage = AiControlsEndpoints.MayEdit(db, sc, me, entityId),
        };
    }

    private static async Task<Entity?> EntityRow(AppDbContext db, int? entityId) =>
        entityId is int eid
            ? await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid)
            : null;

    /// <summary>
    /// Port of aicore/views.py <c>_item_payload</c> — snake_case, which is what the
    /// Knowledge Base page parses. live/expired/scheduled are precomputed here so
    /// the UI's chip agrees with what retrieval will actually do with the row.
    /// </summary>
    private static object KnowledgeDto(KnowledgeItem k, DateTime now, Entity? ent = null)
    {
        var owner = ent ?? k.Entity;
        var started = k.StartsAt == null || k.StartsAt <= now;
        var ended = k.EndsAt != null && k.EndsAt <= now;
        return new
        {
            id = k.Id, kind = k.Kind, title = k.Title, question = k.Question, content = k.Content,
            tags = k.Tags, channels = k.Channels, priority = k.Priority, active = k.Active,
            // Help Center: published is PUBLIC readability, active is whether
            // the AI may use it — an item can be either, both or neither.
            published = k.Published,
            slug = HelpCenterEndpoints.SlugFor(k),
            category = k.Category,
            starts_at = k.StartsAt, ends_at = k.EndsAt,
            entity_id = k.EntityId,
            entity = k.EntityId == null ? null : owner?.Slug,
            entity_name = k.EntityId == null ? "" : owner?.Name ?? "",
            // "global" = authored organization-wide and inherited by every outlet;
            // "entity" = this outlet's own. Drives the inherited/local badge.
            scope = k.EntityId != null ? "entity" : "global",
            live = k.Active && started && !ended,
            expired = ended,
            scheduled = !started,
            created_by = k.CreatedBy,
            updated_at = k.UpdatedAt,
            // the uploaded document the text came from ("" = typed in)
            source_file = k.SourceFile,
        };
    }

    // ---- knowledge PATCH readers (presence-aware) ---------------------------
    private static bool KHas(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out _);

    private static JsonElement? KGet(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v) ? v : null;

    private static string? KStr(JsonElement b, string key) => KGet(b, key) is JsonElement v
        ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null => "",
            JsonValueKind.Undefined => null,
            _ => v.ToString(),
        }
        : null;

    private static int? KInt(JsonElement b, string key) => KGet(b, key) is JsonElement v
        ? v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : null,
            JsonValueKind.String => int.TryParse(v.GetString(), out var n) ? n : null,
            _ => null,
        }
        : null;

    private static bool? KBool(JsonElement b, string key) => KGet(b, key) is JsonElement v
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        }
        : null;

    private static List<string>? KList(JsonElement b, string key) =>
        KGet(b, key) is JsonElement v && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                .Where(x => x.Length > 0).ToList()
            : null;

    /// <summary>"" / null clears the date; anything unparsable is treated as cleared
    /// rather than 400-ing a save the operator can't diagnose.</summary>
    private static DateTime? KDate(JsonElement b, string key)
    {
        var raw = KStr(b, key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal
            | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }
}
