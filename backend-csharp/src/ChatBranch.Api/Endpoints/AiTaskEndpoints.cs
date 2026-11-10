using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Audit;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Tasks;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Integrations;
using ChatBranch.Data.Mcp;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Background AI tasks (<c>/ai-tasks</c>) — silent AI workers that read inbound
/// messages and act through tools, never answering the customer
/// (<see cref="AiTaskService"/>).
///
/// * <c>GET /api/ai/tasks?entity=</c> — the tasks the caller may see (the
///   library rule, <see cref="ShellScope.ScopeLibrary{T}"/>) with their recent
///   runs, plus the tool catalog (built-ins with "connected" for the entity, the
///   external MCP servers' tools). <c>ai.monitor</c> or <c>settings.ai.manage</c>.
/// * <c>POST / PUT /api/ai/tasks[/{id}]</c>, <c>DELETE /api/ai/tasks/{id}</c> —
///   <c>settings.ai.manage</c>; "Available to" through <see cref="LibraryScope"/>.
/// * <c>GET /api/ai/tasks/{id}/runs</c> — a task's runs, scoped like
///   conversations (a bound caller sees runs of their entities' chats and
///   unattributed ones). <c>ai.monitor</c> or <c>settings.ai.manage</c>.
/// * <c>POST /api/ai/tasks/{id}/test</c> — a DRY RUN on a sample message: what it
///   would extract and which tools it would call; tools that change something
///   are previewed, nothing is stored. <c>settings.ai.manage</c>; the caller's AI
///   quota applies (429).
/// * <c>POST /api/ai/tasks/tick</c> — run the due runs now (the worker's manual seam).
/// </summary>
public static class AiTaskEndpoints
{
    public const string ManageCap = "settings.ai.manage";
    public const string MonitorCap = "ai.monitor";

    /// <summary>The per-tool settings a task may carry.</summary>
    public static readonly string[] ConfigKeys =
    {
        "spreadsheet_id", "sheet_range", "sheet_columns", "drive_folder_id", "calendar_id", "webhook_url", "webhook_secret",
        // social media: explicit opt-ins to act publicly (off unless "true")
        AiTaskTools.AllowPublish, AiTaskTools.AllowReply,
    };

    public static void MapAiTaskEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ai/tasks", async (string? entity, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            IntegrationResolver resolver, ChatBranch.Data.Social.IZernioClient zernio) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = await ShellScope.ScopeLibrary(db.AiTasks.AsNoTracking(), db, sc, me, entityId)
                .OrderBy(t => t.Name).ThenBy(t => t.Id).ToListAsync();
            var scopes = LibraryEntities.Load(db, rows);
            var ids = rows.Select(r => r.Id).ToList();
            var bound = ShellScope.ThreadEntityIds(db, sc, me, null);
            var recent = await ScopeRuns(db.AiTaskRuns.AsNoTracking().Where(r => ids.Contains(r.TaskId)
                    && r.Status != AiTaskRunStatus.Pending && r.Status != AiTaskRunStatus.Running), bound)
                .OrderByDescending(r => r.Id).Take(400).ToListAsync();
            var canManage = await ShellScope.HasCapAsync(db, me, ManageCap);
            return Results.Ok(new
            {
                tasks = rows.Select(t => Payload(t, scopes, recent.Where(r => r.TaskId == t.Id).Take(5).ToList())),
                catalog = Catalog(sc, resolver, entityId, zernio.IsConfigured),
                can_manage = canManage,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability(MonitorCap, ManageCap);

        app.MapPost("/api/ai/tasks", async (JsonElement body, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var task = new AiTask { CreatedBy = me.Identity?.Name ?? "", CreatedAt = DateTime.UtcNow };
            if (Apply(task, body, creating: true) is { } bad) return bad;
            var pick = await LibraryScope.ReadAsync(db, body);
            if (pick.Error != null) return pick.Error;
            if (await LibraryScope.Check(db, sc, me, null, pick.Ids) is { } denied) return denied;
            task.EntityId = pick.Ids.Count > 0 ? pick.Ids[0] : null;
            task.UpdatedAt = DateTime.UtcNow;
            db.AiTasks.Add(task);
            await db.SaveChangesAsync();
            LibraryEntities.Set(db, task, pick.Ids);
            AuditTrail.Add(db, "ai_task.create", task.Name, Summary(task), entityId: task.EntityId);
            await db.SaveChangesAsync();
            return Results.Json(Payload(task, LibraryEntities.Load(db, new[] { task }), new()), statusCode: 201);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapPut("/api/ai/tasks/{id:int}", async (int id, JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var task = await Visible(db, sc, me, id, tracked: true);
            if (task == null) return NotFound();
            var before = LibraryEntities.Ids(db, LibraryKinds.AiTask, task.Id, task.EntityId);
            if (LibraryScope.CheckExisting(db, sc, me, before) is { } locked) return locked;
            if (Apply(task, body, creating: false) is { } bad) return bad;
            var pick = await LibraryScope.ReadAsync(db, body);
            if (pick.Error != null) return pick.Error;
            if (pick.Given)
            {
                if (await LibraryScope.Check(db, sc, me, before, pick.Ids) is { } denied) return denied;
                LibraryEntities.Set(db, task, pick.Ids);
            }
            task.UpdatedAt = DateTime.UtcNow;
            AuditTrail.Add(db, "ai_task.update", task.Name, Summary(task), entityId: task.EntityId);
            await db.SaveChangesAsync();
            return Results.Ok(Payload(task, LibraryEntities.Load(db, new[] { task }), new()));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapDelete("/api/ai/tasks/{id:int}", async (int id, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var task = await Visible(db, sc, me, id, tracked: true);
            if (task == null) return NotFound();
            if (LibraryScope.CheckExisting(db, sc, me,
                    LibraryEntities.Ids(db, LibraryKinds.AiTask, task.Id, task.EntityId)) is { } locked) return locked;
            db.AiTasks.Remove(task);
            LibraryEntities.Clear(db, LibraryKinds.AiTask, task.Id);
            // its history goes with it (runs are this task's record, not the conversation's —
            // the private notes in the chats stay)
            db.AiTaskRuns.RemoveRange(db.AiTaskRuns.Where(r => r.TaskId == task.Id));
            AuditTrail.Add(db, "ai_task.delete", task.Name, "", entityId: task.EntityId);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapGet("/api/ai/tasks/{id:int}/runs", async (int id, string? status, int? limit, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var task = await Visible(db, sc, me, id, tracked: false);
            if (task == null) return NotFound();
            var q = ScopeRuns(db.AiTaskRuns.AsNoTracking().Where(r => r.TaskId == id),
                ShellScope.ThreadEntityIds(db, sc, me, null));
            if (!string.IsNullOrWhiteSpace(status) && status != "all") q = q.Where(r => r.Status == status);
            var rows = await q.OrderByDescending(r => r.Id).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync();
            var msgIds = rows.Select(r => r.MessageId).Distinct().ToList();
            var msgs = await db.Messages.AsNoTracking().Where(m => msgIds.Contains(m.Id))
                .Select(m => new { m.Id, m.Text, m.MediaType }).ToDictionaryAsync(m => m.Id);
            return Results.Ok(new
            {
                task = new { id = task.Id, name = task.Name },
                runs = rows.Select(r => RunPayload(r, msgs.TryGetValue(r.MessageId, out var m) ? m.Text : "",
                    msgs.TryGetValue(r.MessageId, out var m2) ? m2.MediaType : "")),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability(MonitorCap, ManageCap);

        app.MapPost("/api/ai/tasks/{id:int}/test", async (int id, JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, AiTaskService service, CancellationToken ct) =>
        {
            var stored = await Visible(db, sc, me, id, tracked: false);
            if (stored == null) return NotFound();
            if (QuotaGuard.Refusal(db, sc, me) is { } quota) return quota;
            // unsaved edits from the editor ride along as `task` — tested, never stored
            var draft = Copy(stored);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("task", out var t)
                && t.ValueKind == JsonValueKind.Object && Apply(draft, t, creating: false) is { } bad) return bad;
            var text = Str(body, "text") ?? "";
            if (text.Length > 4000) return Bad("text is at most 4000 characters.");
            AiTaskService.Attachment? attachment = null;
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("attachment", out var a)
                && a.ValueKind == JsonValueKind.Object)
            {
                var name = Path.GetFileName(Str(a, "name") ?? "");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(Str(a, "base64") ?? ""); }
                catch { return Bad("attachment.base64 is not valid base64."); }
                if (bytes.Length == 0) return Bad("The attachment is empty.");
                if (bytes.Length > 10 * 1024 * 1024) return Bad("The attachment is larger than 10 MB.");
                var mime = Str(a, "mime") is { Length: > 0 } mm ? mm : ChatBranch.Data.Channels.MediaStore.MimeFor(name);
                attachment = new AiTaskService.Attachment(name.Length > 0 ? name : "attachment", mime, bytes);
            }
            if (text.Trim().Length == 0 && attachment == null) return Bad("Give a sample message or an attachment.");
            var o = await service.TestAsync(draft, text, attachment, draft.EntityId, ct);
            return Results.Ok(new
            {
                dry_run = true,
                ok = !o.ModelFailed,
                error = o.Error,
                relevant = o.Relevant,
                summary = o.Summary,
                extracted = JsonDocument.Parse(o.ExtractedJson).RootElement,
                steps = o.Steps.Select(s => new { tool = s.Tool, args = s.Args, result = s.Result, ok = s.Ok, preview = s.Preview }),
                notes = o.Notes,
                provider = o.Provider,
                model = o.Model,
                prompt_tokens = o.PromptTokens,
                completion_tokens = o.CompletionTokens,
                attachment_conditions_met = AiTaskService.AttachmentMatches(draft, attachment),
                keywords_match = AiTaskService.KeywordsMatch(draft, text),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap)
          .WithMetadata(new SkipRequestAuditAttribute());

        app.MapPost("/api/ai/tasks/tick", async (AiTaskService service, CancellationToken ct) =>
        {
            var ran = await service.RunDueAsync(50, ct: ct);
            return Results.Ok(new { ran });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);
    }

    // ---- scope ------------------------------------------------------------------------

    private static async Task<AiTask?> Visible(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int id, bool tracked)
    {
        var q = tracked ? db.AiTasks.AsQueryable() : db.AiTasks.AsNoTracking();
        return await ShellScope.ScopeLibrary(q, db, sc, me, null).FirstOrDefaultAsync(t => t.Id == id);
    }

    /// <summary>Runs of conversations the caller may read (null = unbound — everything).</summary>
    private static IQueryable<AiTaskRun> ScopeRuns(IQueryable<AiTaskRun> q, List<int>? entityIds) =>
        entityIds is null ? q : q.Where(r => r.EntityId == null || entityIds.Contains(r.EntityId.Value));

    // ---- catalog ------------------------------------------------------------------------

    private static object Catalog(ScopingService sc, IntegrationResolver resolver, int? entityId, bool socialReady)
    {
        bool Connected(string provider) => provider.Length == 0
            || (provider == AiTaskTools.SocialProvider ? socialReady : resolver.Active(provider, entityId, null) != null);
        var servers = McpServerRegistry.Load(sc);
        return new
        {
            builtins = AiTaskTools.Builtins.Select(d => new
            {
                id = d.Id, group = d.Group, label = d.Label, description = d.Description, provider = d.Provider,
                connected = Connected(d.Provider), write = d.Write,
                // the connected app's name (the editor's heading) and the tool's own opt-in settings
                app = AiTaskTools.AppOf(d) is { Length: > 0 } app ? app : null,
                settings = AiTaskTools.SettingsOf(d.Id).Select(x => new { key = x.Key, type = x.Type, @default = x.Default, label = x.Label }),
                // only meaningful on a social comment conversation
                comment_only = d.Id is AiTaskTools.SocialHide or AiTaskTools.SocialReply,
            }),
            mcp = AiTaskTools.Mcp(sc).Select(e => new
            {
                id = AiTaskTools.McpPrefix + e.QualifiedName, server = e.Server.Name, name = e.Tool.Name,
                description = e.Tool.Description.Length > 0 ? e.Tool.Description : e.Tool.Title,
                read_only = e.Tool.ReadOnly, connected = true,
            }),
            mcp_servers = servers.Count,
            mcp_servers_ready = servers.Count(s => s.Enabled && s.Status == "healthy"),
            attachments = AiTaskAttachments.All,
            linked = AiTaskLinked.All,
        };
    }

    // ---- write ------------------------------------------------------------------------

    /// <summary>Apply a body's fields to a task. Null = fine, else the 400.</summary>
    private static IResult? Apply(AiTask task, JsonElement body, bool creating)
    {
        if (body.ValueKind != JsonValueKind.Object) return Bad("Send a JSON object.");
        if (creating || Has(body, "name"))
        {
            var name = (Str(body, "name") ?? "").Trim();
            if (name.Length == 0) return Bad("name is required.");
            if (name.Length > 120) return Bad("name is at most 120 characters.");
            task.Name = name;
        }
        if (Bool(body, "enabled") is bool on) task.Enabled = on;
        if (Has(body, "channels"))
        {
            var ch = StrList(body, "channels").Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().ToList();
            if (ch.Any(c => c.Length > 30)) return Bad("Unknown channel.");
            task.Channels = ch;
        }
        if (Has(body, "trigger"))
        {
            var trig = (Str(body, "trigger") ?? "").Trim();
            if (trig is not ("" or "inbound_message")) return Bad("trigger must be inbound_message.");
            task.Trigger = "inbound_message";
        }
        if (Has(body, "attachment"))
        {
            var att = (Str(body, "attachment") ?? "").Trim().ToLowerInvariant();
            if (att == "none") att = "";
            if (!AiTaskAttachments.All.Contains(att)) return Bad("attachment must be '', any, pdf or image.");
            task.Attachment = att;
        }
        if (Has(body, "linked"))
        {
            var linked = (Str(body, "linked") ?? "").Trim().ToLowerInvariant();
            if (linked == "any") linked = "";
            if (!AiTaskLinked.All.Contains(linked)) return Bad("linked must be '', yes or no.");
            task.Linked = linked;
        }
        if (Has(body, "keywords"))
        {
            var words = StrList(body, "keywords").Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList();
            if (words.Count > 30 || words.Any(w => w.Length > 60)) return Bad("At most 30 keywords of 60 characters.");
            task.Keywords = words;
        }
        if (creating || Has(body, "instructions"))
        {
            var ins = (Str(body, "instructions") ?? "").Trim();
            if (ins.Length == 0) return Bad("instructions are required — say what to look for and what to do.");
            if (ins.Length > 8000) return Bad("instructions are at most 8000 characters.");
            task.Instructions = ins;
        }
        if (Has(body, "tools"))
        {
            var tools = StrList(body, "tools").Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            if (tools.Count > 20) return Bad("At most 20 tools.");
            if (tools.FirstOrDefault(x => !AiTaskTools.KnownId(x)) is { } unknown) return Bad($"Unknown tool '{unknown}'.");
            task.Tools = tools;
        }
        if (Has(body, "config"))
        {
            if (!body.TryGetProperty("config", out var cfg) || cfg.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                return Bad("config must be an object.");
            var next = new Dictionary<string, object?>(task.Config);
            if (cfg.ValueKind == JsonValueKind.Object)
                foreach (var p in cfg.EnumerateObject())
                {
                    if (!ConfigKeys.Contains(p.Name)) return Bad($"Unknown setting '{p.Name}'.");
                    var v = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? "").Trim()
                        : p.Value.ValueKind == JsonValueKind.Null ? "" : p.Value.ToString();
                    if (v.Length > 1000) return Bad($"{p.Name} is too long.");
                    if (p.Name is AiTaskTools.AllowPublish or AiTaskTools.AllowReply)
                        v = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ? "true" : "";
                    // a masked secret coming back unchanged keeps the stored one
                    if (p.Name == "webhook_secret" && v.Contains('•')) continue;
                    if (v.Length == 0) next.Remove(p.Name);
                    else next[p.Name] = v;
                }
            task.Config = next;
        }
        var url = AiTaskService.Conf(task, "webhook_url");
        if (url.Length > 0 && AiTaskService.WebhookUrlProblem(url) is string urlBad) return Bad(urlBad);
        if (task.Tools.Contains(AiTaskTools.Webhook)
            && (url.Length == 0 || AiTaskService.Conf(task, "webhook_secret").Length < 16))
            return Bad("The webhook needs an https URL and a signing secret of at least 16 characters.");
        return null;
    }

    private static AiTask Copy(AiTask t) => new()
    {
        Id = t.Id, EntityId = t.EntityId, Name = t.Name, Enabled = t.Enabled, Channels = t.Channels.ToList(),
        Trigger = t.Trigger, Attachment = t.Attachment, Keywords = t.Keywords.ToList(), Linked = t.Linked,
        Instructions = t.Instructions, Tools = t.Tools.ToList(), Config = new Dictionary<string, object?>(t.Config),
    };

    private static string Summary(AiTask t) =>
        $"{(t.Enabled ? "on" : "off")}; tools: {(t.Tools.Count == 0 ? "none" : string.Join(", ", t.Tools))}";

    // ---- payloads -------------------------------------------------------------------------

    private static object Payload(AiTask t, Dictionary<int, List<EntityRef>> scopes, List<AiTaskRun> recent)
    {
        var config = new Dictionary<string, object?>();
        foreach (var k in ConfigKeys)
        {
            var v = AiTaskService.Conf(t, k);
            if (v.Length == 0) continue;
            config[k] = k == "webhook_secret" ? McpServerRegistry.Mask(v) : v;
        }
        return new
        {
            id = t.Id,
            name = t.Name,
            enabled = t.Enabled,
            channels = t.Channels,
            trigger = t.Trigger,
            attachment = t.Attachment,
            keywords = t.Keywords,
            linked = t.Linked,
            instructions = t.Instructions,
            tools = t.Tools,
            config,
            entities = LibraryEntities.Payload(scopes, t.Id),
            global = t.EntityId == null,
            run_count = t.RunCount,
            last_run_at = t.LastRunAt,
            created_by = t.CreatedBy,
            updated_at = t.UpdatedAt,
            recent = recent.Select(r => new { id = r.Id, status = r.Status, summary = r.Summary, finished_at = r.FinishedAt }),
        };
    }

    private static object RunPayload(AiTaskRun r, string text, string mediaType)
    {
        JsonElement extracted, steps;
        try { extracted = JsonDocument.Parse(r.Extracted).RootElement.Clone(); }
        catch { extracted = JsonDocument.Parse("{}").RootElement.Clone(); }
        try { steps = JsonDocument.Parse(r.Steps).RootElement.Clone(); }
        catch { steps = JsonDocument.Parse("[]").RootElement.Clone(); }
        return new
        {
            id = r.Id,
            status = r.Status,
            channel = r.Channel,
            key = r.ThreadKey,
            chat_url = ShellScope.ChatUrl(r.Channel, r.ThreadKey, r.ThreadId),
            message_id = r.MessageId,
            message = ShellScope.Snip(text, 200),
            media_type = mediaType,
            summary = r.Summary,
            error = r.Error,
            extracted,
            steps,
            provider = r.Provider,
            model = r.Model,
            prompt_tokens = r.PromptTokens,
            completion_tokens = r.CompletionTokens,
            created_at = r.CreatedAt,
            finished_at = r.FinishedAt,
        };
    }

    // ---- helpers ------------------------------------------------------------------------

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);
    private static IResult NotFound() => Results.NotFound(new { detail = "Task not found." });

    private static bool Has(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out _);

    private static string? Str(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => v.ToString(),
            }
            : null;

    private static bool? Bool(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static List<string> StrList(JsonElement b, string key)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(key, out var v)) return new();
        if (v.ValueKind == JsonValueKind.String)
            return (v.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (v.ValueKind != JsonValueKind.Array) return new();
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList();
    }
}
