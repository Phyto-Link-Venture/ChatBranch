using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
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
    public record PersonaWrite(string Persona, string? Entity);
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
        // ---- persona -----------------------------------------------------------
        app.MapGet("/api/aicore/persona", async (string? entity, AppDbContext db,
            PersonaService persona, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var ent = await EntityRow(db, entityId);
            // Wire shape = aicore/views.py _persona_payload (snake_case). `is_default`
            // is what the UI reads to label the box "built-in default" — it must be
            // the STORED value being empty, not the resolved text matching
            // DEFAULT_PERSONA (an operator who typed the default back in is still
            // overriding it).
            return Results.Ok(new
            {
                persona = persona.PersonaBody(entityId),
                is_default = string.IsNullOrWhiteSpace(sc.SettingValue("aicore_persona", entityId)),
                @default = PersonaService.DefaultPersona,
                hard_rules = PersonaService.HardRules,
                entity = ent?.Slug,
                entity_name = ent?.Name ?? "",
                scope = sc.SettingScope("aicore_persona", entityId),
                global_persona = persona.PersonaBody(null),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/aicore/persona", async (PersonaWrite body, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, body.Entity);
            try { sc.SetSetting("aicore_persona", body.Persona, entityId); }
            catch (InvalidOperationException ex) { return Results.Json(new { detail = ex.Message }, statusCode: 400); }
            return Results.Ok(new { persona = sc.SettingValue("aicore_persona", entityId) });
        }).RequireAuthorization(AuthPolicies.SuperAdmin);

        // ---- knowledge base ----------------------------------------------------
        app.MapGet("/api/aicore/knowledge", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var ent = await EntityRow(db, entityId);
            // Include(Entity) so each item can report its OWNING outlet (which is
            // not the outlet being viewed — inherited global rows carry null).
            var rows = sc.ScopeInherited(db.KnowledgeItems.AsNoTracking(), entityId)
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
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
            return Results.Ok(KnowledgeDto(row, DateTime.UtcNow, await EntityRow(db, row.EntityId)));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

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
            return Results.Ok(KnowledgeDto(row, DateTime.UtcNow, await EntityRow(db, row.EntityId)));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapDelete("/api/aicore/knowledge/{id:int}", async (int id, AppDbContext db) =>
        {
            await db.KnowledgeItems.Where(k => k.Id == id).ExecuteDeleteAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // The exact block the AI receives — "why did the bot say that?" as a button.
        app.MapPost("/api/aicore/knowledge/preview", async (PreviewRequest b, AppDbContext db,
            KnowledgeService knowledge) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, b.Entity);
            var query = !string.IsNullOrWhiteSpace(b.Query) ? b.Query : b.Text;
            var block = knowledge.BuildContext(query, entityId, b.Channel);
            // Django-contract shape (aicore/views.py knowledge_preview): the page
            // renders `chars` / `budget` — omitting budget crashed it on
            // `preview.budget.toLocaleString`.
            return Results.Ok(new
            {
                block,
                full_prompt_block = block,
                chars = block.Length,
                budget = 1800,
                items = block.Split('\n').Count(ln => !string.IsNullOrWhiteSpace(ln)),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
            AssistantService assistant) =>
        {
            var thread = store.GetThread(body.Channel, body.Key);
            if (thread == null) return Results.NotFound(new { detail = "thread not found" });
            // An operator-requested draft: no handover side effects. `reply` is
            // what may be sent ("" when a reply check held it back); `draft` is
            // what the model wrote either way, `meta` the audit trail.
            var r = await assistant.GenerateReplyAsync(thread, applyOutcome: false);
            return Results.Ok(new { reply = r.Text, draft = r.Draft, action = r.Action, reason = r.Reason,
                meta = r.ToMeta() });
        }).RequireAuthorization(AuthPolicies.Authenticated);
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
