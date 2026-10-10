using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    private static IEnumerable<IChatBranchTool> Automation()
    {
        // ---- flows -------------------------------------------------------------
        const string f = "flows";
        var flowsView = C("flows.view");
        var flowsManage = C("flows.manage");
        var flowId = A.Str("flow_id", "The flow's id (flowId from list_flows).", req: true);

        yield return Custom(f, "flows_get_flow", "Read a flow",
            "One journey (a workflow) in full: trigger, steps (nodes) and branches (edges), live or " +
            "paused — the same graph the builder edits.",
            new[] { "GET /api/flows/library" }, GetFlowAsync, flowsView,
            new[] { flowId, A.Entity("The flow's entity slug; omit for organisation-wide flows.") }, write: false);

        yield return Custom(f, "flows_save_flow", "Create or update a flow",
            "Create a workflow or replace one's graph by flow_id — only that flow changes. nodes/edges are the flow " +
            "builder's React Flow JSON (read an existing one with flows_get_flow for the vocabulary: trigger, " +
            "action, wait, followup, decision, handover, update, end). New flows start paused unless live=true.",
            new[] { "PUT /api/flows/library", "GET /api/flows/library" }, SaveFlowAsync, flowsManage,
            new[]
            {
                A.Str("flow_id", "Existing flow id to update; omit to create."),
                A.Str("name", "Name."), A.Str("hint", "One-line description."),
                A.Arr("nodes", "React Flow nodes."), A.Arr("edges", "React Flow edges."),
                A.Bool("live", "Switch it on (true) or keep it paused."),
                A.Entity("Entity slug the flow belongs to; omit for organisation-wide."),
            });

        yield return Custom(f, "flows_set_live", "Pause or resume a flow",
            "Switch a workflow on (live) or off (paused). Going live only acts on what happens after this moment.",
            new[] { "PUT /api/flows/library", "GET /api/flows/library" }, SetFlowLiveAsync, flowsManage,
            new[] { flowId, A.Bool("live", "true = live, false = paused.", req: true), A.Entity("The flow's entity slug.") },
            idempotent: true);

        yield return Custom(f, "flows_delete_flow", "Delete a flow",
            "Delete a workflow (its run history stays in the stats). Destructive — confirm first.",
            new[] { "PUT /api/flows/library", "GET /api/flows/library" }, DeleteFlowAsync, flowsManage,
            new[] { flowId, A.Entity("The flow's entity slug.") }, destructive: true);

        yield return T(f, "flows_start_for_contacts", "Start a flow for contacts",
            "Run a 'Manual / API' flow for the given contacts now. Its message/call steps are queued and go out " +
            "through the normal pacing and guards.", "POST", "/api/flows/{flowId}/start", flowsManage,
            new[]
            {
                A.Str("flow_id", "The flow's id.", req: true, @as: "flowId"),
                A.IntList("contact_ids", "Contacts to start it for.", req: true, @as: "contactIds"),
                A.Entity("The flow's entity slug.", ArgIn.Query),
            }, outside: true);

        yield return T(f, "flows_get_stats", "Flow statistics",
            "A flow's runs: totals, people entering per day, reach and drop-off per step, failure reasons.",
            "GET", "/api/flows/{flowId}/stats", flowsView,
            new[] { A.Str("flow_id", "The flow's id.", req: true, @as: "flowId"), A.Days(), A.Entity() });

        yield return T(f, "flows_list_runs", "List flow runs",
            "Per-contact flow runs (paged): which flow, contact, current step, status (running, waiting, done, " +
            "failed), started/finished.", "GET", "/api/flows/runs", flowsView,
            new[]
            {
                A.Str("flow", "Flow id."), A.Str("status", "running | waiting | completed | failed | cancelled."),
                A.Str("contact", "Contact id or name."), A.Str("node", "Only runs that reached this step id."),
                A.Days(), A.Entity(), A.Page, A.PageSize(100),
            });

        yield return T(f, "flows_get_run", "Get a flow run",
            "One contact's path through a flow: every step event with its outcome.",
            "GET", "/api/flows/runs/{id:int}", flowsView, new[] { A.Id("flow run"), A.Entity() });

        yield return T(f, "flows_list_queue", "List the outbound queue",
            "The latest 200 queued sends and calls (flows, AI replies, follow-ups): kind, channel, recipient, " +
            "status (queued, sending, sent, failed, cancelled), scheduled time and error.",
            "GET", "/api/flows/queue", flowsManage);

        yield return T(f, "flows_enqueue_job", "Queue an outbound message",
            "Put one message/call on the outbound queue to an address (paced, quiet hours, daily caps, opt-out " +
            "applied at send time). kind text | template | ai_reply | call. Admin only.",
            "POST", "/api/flows/enqueue", flowsManage,
            new[]
            {
                A.Str("channel", "Channel.", req: true, oneOf: EndpointTool.ChannelNames),
                A.Str("kind", "text | template | ai_reply | call.", req: true, oneOf: new[] { "text", "template", "ai_reply", "call" }),
                A.Str("to_address", "Recipient address.", req: true, @as: "toAddress"),
                A.Obj("payload", "e.g. {text} or {template, language, variables}."),
                A.Int("contact_id", "Contact.", @as: "contactId"), A.Int("account_id", "Sending account.", @as: "accountId"),
            }, outside: true);

        yield return T(f, "flows_run_due_steps", "Run due flow steps now",
            "Advance every flow now instead of waiting for the 10-second worker (scheduled triggers, waits that " +
            "ended, follow-ups). Admin only.", "POST", "/api/flows/tick", flowsManage, outside: true);

        // ---- automations ---------------------------------------------------------
        const string au = "automations";
        yield return T(au, "automations_list", "List automation rules",
            "Conversation automation rules: on an event (conversation_created, message_created, status_changed, " +
            "assigned) ALL matching rules run in order — conditions and actions.",
            "GET", "/api/automations", flowsManage, link: (_, _) => Page("Open automations", "/automations"));
        yield return T(au, "automations_get_catalog", "Automation vocabulary",
            "The exact events, condition fields/operators and actions the rule engine understands (an unknown " +
            "operator matches nothing) — read it before writing a rule.", "GET", "/api/automations/catalog", flowsManage);
        var ruleFields = new[]
        {
            A.Str("name", "Name."),
            A.Str("event", "conversation_created | message_created | status_changed | assigned."),
            A.Arr("conditions", "[{field, op, value}] (automations_get_catalog)."),
            A.Arr("actions", "[{type, value}] e.g. assign_team, add_label, hand_over, ask_identity, send_message."),
            A.Bool("active", "On/off."), A.Int("position", "Order."), A.Str("description", "Description."),
            A.Entity("Entity slug; omit for organisation-wide.", ArgIn.Body),
        };
        yield return T(au, "automations_create", "Create an automation rule", "Add a conversation automation rule (admin): event + conditions + actions, in position order.",
            "POST", "/api/automations", flowsManage, ruleFields);
        yield return T(au, "automations_update", "Edit an automation rule", "Change an automation rule's event, conditions, actions or on/off (only the fields you pass).",
            "PATCH", "/api/automations/{id:int}", flowsManage, new[] { A.Id("rule") }.Concat(ruleFields).ToArray());
        yield return T(au, "automations_delete", "Delete an automation rule", "Delete a conversation automation rule for good (automations_update active=false only pauses it).",
            "DELETE", "/api/automations/{id:int}", flowsManage, new[] { A.Id("rule") }, destructive: true);
        yield return T(au, "automations_test_rule", "Dry-run an automation rule",
            "Would this rule fire on that conversation (with this message)? Answered without touching anything.",
            "POST", "/api/automations/{id:int}/test", flowsManage, thread: true,
            args: new[] { A.Id("rule"), A.Str("message", "A message text to test with.") }, write: false);
        yield return T(au, "automations_reorder", "Reorder automation rules",
            "Set the order automation rules run in (top first): ids = rule ids in the new order; unlisted rules follow.",
            "POST", "/api/automations/reorder", flowsManage, new[] { A.Arr("ids", "Rule ids, first to run first.") });
    }

    // ---- the flow library: read-modify-write through the builder's own endpoint ----

    private sealed record FlowLibrary(string? Entity, int? EntityId, List<JsonObject> Flows);

    /// <summary>The flows of exactly one scope, shaped as the PUT takes them.</summary>
    private static async Task<FlowLibrary> FlowScopeAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var entity = S(a, "entity") is { Length: > 0 } e ? e : null;
        var ent = await ToolScope.EntityAsync(ctx, entity);
        var r = ToolContext.Require(await ctx.CallAsync("GET", "/api/flows/library",
            query: new Dictionary<string, string?> { ["entity"] = ent?.Slug }, ct: ct));
        var flows = new List<JsonObject>();
        if (r.Body.TryGetProperty("workflows", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var w in arr.EnumerateArray())
            {
                int? eid = w.TryGetProperty("entityId", out var ev) && ev.ValueKind == JsonValueKind.Number ? ev.GetInt32() : null;
                if (eid != ent?.Id) continue; // inherited flows belong to another scope's save
                var o = new JsonObject();
                foreach (var key in new[] { "flowId", "name", "hint", "nodes", "edges", "active", "position" })
                    if (w.TryGetProperty(key, out var v)) o[key] = JsonNode.Parse(v.GetRawText());
                flows.Add(o);
            }
        return new FlowLibrary(ent?.Slug, ent?.Id, flows);
    }

    private static async Task SaveLibraryAsync(ToolContext ctx, FlowLibrary lib, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["entity"] = lib.Entity,
            ["workflows"] = new JsonArray(lib.Flows.Select(x => (JsonNode)x).ToArray()),
        };
        ToolContext.Require(await ctx.CallAsync("PUT", "/api/flows/library", body: payload, ct: ct));
        ctx.Links.Add(new ToolLink("Open Journeys", "/journeys"));
    }

    private static JsonObject FindFlow(FlowLibrary lib, string id) =>
        lib.Flows.FirstOrDefault(x => x["flowId"]?.GetValue<string>() == id)
        ?? throw new ToolException($"No flow '{id}' in {(lib.Entity ?? "the organisation-wide library")} " +
                                   "(pass entity for an outlet's flow).", 404);

    private static async Task<object?> GetFlowAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var lib = await FlowScopeAsync(ctx, a, ct);
        var flow = FindFlow(lib, Need(a, "flow_id"));
        return ToolResults.Compact(JsonSerializer.SerializeToElement(flow), new ToolResults.Options(MaxItems: 200, MaxChars: 60_000));
    }

    private static async Task<object?> SaveFlowAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var lib = await FlowScopeAsync(ctx, a, ct);
        var id = S(a, "flow_id") is { Length: > 0 } given ? given : null;
        JsonObject flow;
        var created = id == null;
        if (id == null)
        {
            id = "flow_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
            flow = new JsonObject
            {
                ["flowId"] = id, ["name"] = S(a, "name") ?? "Untitled flow", ["hint"] = "",
                ["nodes"] = new JsonArray(), ["edges"] = new JsonArray(), ["active"] = false, ["position"] = lib.Flows.Count,
            };
            lib.Flows.Add(flow);
        }
        else flow = FindFlow(lib, id);
        if (S(a, "name") is { Length: > 0 } name) flow["name"] = name;
        if (S(a, "hint") is { } hint) flow["hint"] = hint;
        if (El(a, "nodes") is { ValueKind: JsonValueKind.Array } nodes) flow["nodes"] = JsonNode.Parse(nodes.GetRawText());
        if (El(a, "edges") is { ValueKind: JsonValueKind.Array } edges) flow["edges"] = JsonNode.Parse(edges.GetRawText());
        if (B(a, "live") is bool live) flow["active"] = live;
        await SaveLibraryAsync(ctx, lib, ct);
        return new { saved = true, created, flow_id = id, entity = lib.Entity, live = flow["active"]?.GetValue<bool>() };
    }

    private static async Task<object?> SetFlowLiveAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var lib = await FlowScopeAsync(ctx, a, ct);
        var id = Need(a, "flow_id");
        var live = B(a, "live") ?? throw new ToolException("'live' is required (true or false).");
        FindFlow(lib, id)["active"] = live;
        await SaveLibraryAsync(ctx, lib, ct);
        return new { flow_id = id, live };
    }

    private static async Task<object?> DeleteFlowAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var lib = await FlowScopeAsync(ctx, a, ct);
        var id = Need(a, "flow_id");
        lib.Flows.Remove(FindFlow(lib, id));
        await SaveLibraryAsync(ctx, lib, ct);
        return new { deleted = true, flow_id = id };
    }
}
