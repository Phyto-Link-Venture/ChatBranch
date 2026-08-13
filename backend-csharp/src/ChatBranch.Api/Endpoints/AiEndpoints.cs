using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// aicore surface (/api/aicore/*): persona (superadmin), knowledge base CRUD +
/// preview (admin+), skills (admin+, the SOLE writer of crm.AgentSkill), and a
/// manual reply endpoint that runs the channel-agnostic assistant.
/// </summary>
public static class AiEndpoints
{
    public record PersonaWrite(string Persona, string? Entity);
    public record KnowledgeWrite(string Kind, string Title, string? Question, string Content,
        List<string>? Tags, List<string>? Channels, int Priority, bool Active,
        DateTime? StartsAt, DateTime? EndsAt, string? Entity);
    public record PreviewRequest(string? Query, string? Channel, string? Entity);
    public record SkillDto(string SkillId, string Name, string? UseWhen, string? Instructions, int Position);
    public record SkillsWrite(string? Entity, List<SkillDto> Skills);
    public record ReplyRequest(string Channel, string Key);

    public static void MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- persona -----------------------------------------------------------
        app.MapGet("/api/aicore/persona", async (string? entity, AppDbContext db,
            PersonaService persona, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            return Results.Ok(new
            {
                hardRules = PersonaService.HardRules,
                persona = persona.PersonaBody(entityId),
                @default = PersonaService.DefaultPersona,
                scope = sc.SettingScope("aicore_persona", entityId),
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
            var rows = sc.ScopeInherited(db.KnowledgeItems.AsNoTracking(), entityId)
                .OrderByDescending(k => k.Priority).ThenByDescending(k => k.UpdatedAt).ToList();
            return Results.Ok(new { knowledge = rows.Select(KnowledgeDto) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/aicore/knowledge", async (KnowledgeWrite b, AppDbContext db) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, b.Entity);
            var row = new KnowledgeItem
            {
                EntityId = entityId, Kind = b.Kind, Title = b.Title, Question = b.Question ?? "",
                Content = b.Content, Tags = b.Tags ?? new(), Channels = b.Channels ?? new(),
                Priority = b.Priority, Active = b.Active, StartsAt = b.StartsAt, EndsAt = b.EndsAt,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.KnowledgeItems.Add(row);
            await db.SaveChangesAsync();
            return Results.Ok(KnowledgeDto(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapMethods("/api/aicore/knowledge/{id:int}", new[] { "PATCH" },
            async (int id, KnowledgeWrite b, AppDbContext db) =>
        {
            var row = await db.KnowledgeItems.FindAsync(id);
            if (row == null) return Results.NotFound();
            row.Kind = b.Kind; row.Title = b.Title; row.Question = b.Question ?? "";
            row.Content = b.Content; row.Tags = b.Tags ?? new(); row.Channels = b.Channels ?? new();
            row.Priority = b.Priority; row.Active = b.Active;
            row.StartsAt = b.StartsAt; row.EndsAt = b.EndsAt; row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(KnowledgeDto(row));
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
            var block = knowledge.BuildContext(b.Query, entityId, b.Channel);
            return Results.Ok(new { block, chars = block.Length });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- skills (sole writer of crm.AgentSkill) ----------------------------
        app.MapGet("/api/aicore/skills", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = sc.ScopeInherited(db.AgentSkills.AsNoTracking(), entityId)
                .OrderBy(s => s.Position).ToList();
            return Results.Ok(new { skills = rows.Select(SkillDtoOut) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/aicore/skills", async (SkillsWrite body, AppDbContext db) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, body.Entity);
            // Upsert within THIS scope, prune scope rows not in the payload
            // (the "sole writer" contract — only touches its own scope).
            var existing = await db.AgentSkills.Where(s => s.EntityId == entityId).ToListAsync();
            var keep = new HashSet<string>();
            foreach (var s in body.Skills)
            {
                keep.Add(s.SkillId);
                var row = existing.FirstOrDefault(e => e.SkillId == s.SkillId)
                          ?? Add(db, new AgentSkill { SkillId = s.SkillId, EntityId = entityId });
                row.Name = s.Name; row.UseWhen = s.UseWhen ?? ""; row.Instructions = s.Instructions ?? "";
                row.Position = s.Position;
            }
            db.AgentSkills.RemoveRange(existing.Where(e => !keep.Contains(e.SkillId)));
            await db.SaveChangesAsync();
            return Results.Ok(new { count = body.Skills.Count });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- manual reply (playground / operator "AI draft") -------------------
        app.MapPost("/api/aicore/reply", async (ReplyRequest body, ThreadStore store,
            AssistantService assistant) =>
        {
            var thread = store.GetThread(body.Channel, body.Key);
            if (thread == null) return Results.NotFound(new { detail = "thread not found" });
            var reply = await assistant.GenerateReplyAsync(thread);
            return Results.Ok(new { reply });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    private static AgentSkill Add(AppDbContext db, AgentSkill s) { db.AgentSkills.Add(s); return s; }

    private static object KnowledgeDto(KnowledgeItem k) => new
    {
        id = k.Id, kind = k.Kind, title = k.Title, question = k.Question, content = k.Content,
        tags = k.Tags, channels = k.Channels, priority = k.Priority, active = k.Active,
        startsAt = k.StartsAt, endsAt = k.EndsAt, entityId = k.EntityId,
    };

    private static object SkillDtoOut(AgentSkill s) => new
    {
        id = s.Id, skillId = s.SkillId, name = s.Name, useWhen = s.UseWhen,
        instructions = s.Instructions, position = s.Position, entityId = s.EntityId,
    };
}
