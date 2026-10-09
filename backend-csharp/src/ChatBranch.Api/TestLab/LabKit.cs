using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>
/// The lab's library pieces — knowledge, skills, automation rules, flows,
/// quick replies, tags, custom fields — get-or-create by name inside the lab
/// entity, written through the dashboard's own endpoints where the app has a
/// "sole writer" (skills, automation rules, tags, quick replies). Anything a
/// scenario switches on is switched back off by its cleanup.
/// </summary>
public static class LabKit
{
    public const string CalendarSkillId = "testlab-calendar";
    public const string CalendarSkillName = "Test lab bookings";
    public const string GuardrailSkillId = "testlab-no-refund-promises";
    public const string GuardrailSkillName = "Test lab · no refund promises";

    // ---- knowledge ---------------------------------------------------------------------

    public static KnowledgeItem Knowledge(LabContext ctx, string title, string kind, string question, string content)
    {
        var row = ctx.Db.KnowledgeItems.FirstOrDefault(k => k.EntityId == ctx.Lab.Hq.Id && k.Title == title);
        if (row == null)
        {
            row = new KnowledgeItem
            {
                EntityId = ctx.Lab.Hq.Id, TenantId = ctx.Lab.TenantId, Title = title, CreatedBy = "testlab",
                CreatedAt = DateTime.UtcNow,
            };
            ctx.Db.KnowledgeItems.Add(row);
        }
        row.Kind = kind;
        row.Question = question;
        row.Content = content;
        row.Active = true;
        row.Priority = 5;
        row.UpdatedAt = DateTime.UtcNow;
        ctx.Db.SaveChanges();
        return row;
    }

    // ---- skills (through GET/PUT /api/aicore/skills, the only writer) -------------------

    private static readonly object CalendarConfig = new
    {
        app = "calendar",
        source = "chatbranch",
        hours = new Dictionary<string, string[][]>
        {
            ["mon"] = new[] { new[] { "00:00", "23:59" } }, ["tue"] = new[] { new[] { "00:00", "23:59" } },
            ["wed"] = new[] { new[] { "00:00", "23:59" } }, ["thu"] = new[] { new[] { "00:00", "23:59" } },
            ["fri"] = new[] { new[] { "00:00", "23:59" } }, ["sat"] = new[] { new[] { "00:00", "23:59" } },
            ["sun"] = new[] { new[] { "00:00", "23:59" } },
        },
        duration_minutes = 30,
        min_notice_minutes = 0,
        max_days_ahead = 14,
        max_active_per_contact = 5,
        actions = new[] { "availability", "book", "cancel" },
        event_title = "{{name}} — Test lab appointment",
    };

    private const string GuardrailCode =
        "function check(reply, ctx) {\n"
        + "  if (/full refund/i.test(reply)) return { ok: false, reason: 'promises a full refund', fix: 'Never promise a refund — a person decides.' };\n"
        + "  return { ok: true };\n"
        + "}";

    /// <summary>Switch the lab's two skills on/off (null = leave as is). The previous state comes back after the scenario.</summary>
    public static async Task<InternalApiResult> Skills(LabContext ctx, bool? calendar = null, bool? guardrail = null,
        bool restore = true)
    {
        var current = ctx.Db.AgentSkills.AsNoTracking().Where(s => s.EntityId == ctx.Lab.Hq.Id).ToList();
        bool On(string id) => current.FirstOrDefault(s => s.SkillId == id)?.Enabled ?? false;
        var wasCal = On(CalendarSkillId);
        var wasGuard = On(GuardrailSkillId);
        var res = await PutSkills(ctx, calendar ?? wasCal, guardrail ?? wasGuard);
        if (restore && (wasCal != (calendar ?? wasCal) || wasGuard != (guardrail ?? wasGuard)))
            ctx.Defer(() => PutSkills(ctx, wasCal, wasGuard).GetAwaiter().GetResult());
        return res;
    }

    public static async Task<InternalApiResult> PutSkills(LabContext ctx, bool calendar, bool guardrail)
    {
        // the lab's whole skill set in its scope (the PUT prunes what it isn't sent)
        var others = ctx.Db.AgentSkills.AsNoTracking()
            .Where(s => s.EntityId == ctx.Lab.Hq.Id && s.SkillId != CalendarSkillId && s.SkillId != GuardrailSkillId)
            .ToList()
            .Select(s => (object)new
            {
                id = s.SkillId, name = s.Name, useWhen = s.UseWhen, instructions = s.Instructions, position = 0,
                kind = s.Kind, enabled = s.Enabled,
            });
        var body = new
        {
            entity = LabFixture.HqSlug,
            skills = new object[]
            {
                new
                {
                    id = CalendarSkillId, name = CalendarSkillName, position = 0, kind = SkillKinds.App,
                    useWhen = "The customer wants to book, reschedule or cancel an appointment",
                    instructions = "Offer two or three free times and book only after they pick one.",
                    enabled = calendar, config = CalendarConfig,
                },
                new
                {
                    id = GuardrailSkillId, name = GuardrailSkillName, position = 1, kind = SkillKinds.Check,
                    useWhen = "", instructions = "", description = "Never promise a refund — a person decides.",
                    code = GuardrailCode, enabled = guardrail, onFail = "handover",
                },
            }.Concat(others).ToList(),
        };
        var r = await ctx.Api(ctx.Super, "PUT", "/api/aicore/skills", body);
        return new InternalApiResult(r.Status, r.Ok ? "" : r.Detail());
    }

    public sealed record InternalApiResult(int Status, string Detail)
    {
        public bool Ok => Status is >= 200 and < 300;
    }

    // ---- automation rules (through /api/automations) ------------------------------------

    /// <summary>Get-or-create a lab rule by name, set ACTIVE with these conditions/actions; paused again afterwards.</summary>
    public static async Task<int?> Rule(LabContext ctx, string name, string evt, object[] conditions, object[] actions)
    {
        var existing = ctx.Db.AutomationRules.AsNoTracking()
            .FirstOrDefault(r => r.EntityId == ctx.Lab.Hq.Id && r.Name == name);
        int? id;
        if (existing == null)
        {
            var created = await ctx.Api(ctx.Super, "POST", "/api/automations", new
            {
                name, @event = evt, entity = LabFixture.HqSlug, active = true, conditions, actions,
                description = "Created by the Test lab.",
            });
            if (!created.Ok) return null;
            id = LabContext.Int(created.Body, "id");
        }
        else
        {
            id = existing.Id;
            var patched = await ctx.Api(ctx.Super, "PATCH", "/api/automations/{id:int}",
                new { active = true, @event = evt, conditions, actions },
                route: new() { ["id"] = existing.Id.ToString() });
            if (!patched.Ok) return null;
        }
        if (id is int rid)
            ctx.Defer(() => ctx.Api(ctx.Super, "PATCH", "/api/automations/{id:int}", new { active = false },
                route: new() { ["id"] = rid.ToString() }).GetAwaiter().GetResult());
        return id;
    }

    // ---- flows (fixture rows in the lab entity; the engine is what is under test) -------

    /// <summary>A lab flow, LIVE from now (paused again after the scenario).</summary>
    public static Workflow Flow(LabContext ctx, string flowId, string name, List<object?> nodes, List<object?> edges)
    {
        var wf = ctx.Db.Workflows.FirstOrDefault(w => w.EntityId == ctx.Lab.Hq.Id && w.FlowId == flowId);
        if (wf == null)
        {
            wf = new Workflow { EntityId = ctx.Lab.Hq.Id, FlowId = flowId, CreatedAt = DateTime.UtcNow };
            ctx.Db.Workflows.Add(wf);
        }
        wf.Name = name;
        wf.Hint = "Created by the Test lab.";
        wf.Nodes = nodes;
        wf.Edges = edges;
        wf.Active = true;
        wf.LiveSince = DateTime.UtcNow.AddSeconds(-1);
        wf.UpdatedBy = "testlab";
        wf.UpdatedAt = DateTime.UtcNow;
        ctx.Db.SaveChanges();
        var id = wf.Id;
        ctx.Defer(() =>
        {
            ctx.Db.Workflows.Where(w => w.Id == id).ExecuteUpdate(u => u
                .SetProperty(w => w.Active, false).SetProperty(w => w.LiveSince, (DateTime?)null));
        });
        return wf;
    }

    public static object Node(string id, string type, object config) => new { id, type, data = new { config } };

    public static object Edge(string id, string source, string target, string timing = "Immediately",
        string? handle = null) =>
        handle == null
            ? new { id, source, target, data = new { config = new { timing } } }
            : new { id, source, target, sourceHandle = handle, data = new { config = new { timing } } };

    // ---- teams ------------------------------------------------------------------------------

    public static Team Team(LabContext ctx, string name, bool autoAssign, params int[] userIds) =>
        LabFixture.EnsureTeam(ctx.Db, name, ctx.Lab.Hq.Id, autoAssign, userIds);

    // ---- custom fields (the org schema; lab keys are testlab_*, removed by reset) ----------

    public static void CustomField(LabContext ctx, string key, string label, string kind, params string[] options)
    {
        if (!key.StartsWith(LabFixture.FieldPrefix)) throw new ArgumentException("lab fields are testlab_*");
        var row = ctx.Db.Settings.FirstOrDefault(s => s.Key == "custom_fields" && s.EntityId == null);
        JsonObject root;
        try { root = (row == null || string.IsNullOrWhiteSpace(row.Value) ? null : JsonNode.Parse(row.Value) as JsonObject) ?? new JsonObject(); }
        catch { root = new JsonObject(); }
        if (root["customer"] is not JsonArray arr) root["customer"] = arr = new JsonArray();
        if (arr.Any(f => f?["key"]?.GetValue<string>() == key)) return;
        arr.Add(new JsonObject
        {
            ["key"] = key, ["label"] = label, ["kind"] = kind,
            ["options"] = new JsonArray(options.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
            ["required"] = false, ["position"] = arr.Count,
        });
        if (row == null)
        {
            row = new Setting { Key = "custom_fields", EntityId = null };
            ctx.Db.Settings.Add(row);
        }
        row.Value = root.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;
        ctx.Db.SaveChanges();
    }

    // ---- draft context readers ------------------------------------------------------------------

    public static bool GroundedIn(AiDraft d, int knowledgeId) =>
        AiGate.Items(d.Context, "grounding").Any(g => AiGate.Str(g, "type", "") == "knowledge"
                                                      && (int)(AiGate.Num(g, "id") ?? 0) == knowledgeId);

    public static int Count(JsonElement body, string arrayKey) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(arrayKey, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.GetArrayLength() : -1;
}
