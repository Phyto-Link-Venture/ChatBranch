using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api.Assistant;
using ChatBranch.Api.Endpoints;
using ChatBranch.Api.Tools;
using ChatBranch.Domain;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>The catalogue over the real MCP transport, with API keys: reads and writes per
/// domain, the confirm rule, RBAC and scope, secrets, and the assistant's tool router.</summary>
public class McpToolsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public McpToolsTests(ApiFactory f) => _f = f;

    private const string Png1x1 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static async Task<JsonElement> Ok(HttpClient c, string tool, object args)
    {
        var r = await Platform.CallTool(c, tool, args);
        Assert.True(r.TryGetProperty("isError", out var e), $"{tool}: {r}");
        Assert.False(e.GetBoolean(), $"{tool} failed: {r.GetProperty("content")[0].GetProperty("text").GetString()}");
        return r.GetProperty("structuredContent");
    }

    private static async Task<string> Err(HttpClient c, string tool, object args)
    {
        var r = await Platform.CallTool(c, tool, args);
        Assert.True(r.GetProperty("isError").GetBoolean(), $"{tool} unexpectedly succeeded: {r}");
        return r.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private static async Task<List<JsonElement>> ListTools(HttpClient c) =>
        (await Platform.Rpc(c, "tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray().ToList();

    [Fact]
    public async Task Tools_list_returns_the_whole_catalogue_with_schemas_and_annotations()
    {
        var super = Platform.Client(_f, Platform.MintKey(_f, "superadmin"));
        var tools = await ListTools(super);
        Assert.Equal(ToolRegistry.All.Count, tools.Count);
        foreach (var t in tools)
        {
            var name = t.GetProperty("name").GetString()!;
            Assert.Equal("object", t.GetProperty("inputSchema").GetProperty("type").GetString());
            var ann = t.GetProperty("annotations");
            foreach (var hint in new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" })
                Assert.True(ann.GetProperty(hint).ValueKind is JsonValueKind.True or JsonValueKind.False, $"{name}.{hint}");
            if (ann.GetProperty("destructiveHint").GetBoolean())
                Assert.False(ann.GetProperty("readOnlyHint").GetBoolean(), name);
        }
        JsonElement Ann(string n) => tools.Single(t => t.GetProperty("name").GetString() == n).GetProperty("annotations");
        Assert.True(Ann("contacts_delete").GetProperty("destructiveHint").GetBoolean());
        Assert.True(Ann("send_message").GetProperty("openWorldHint").GetBoolean());
        Assert.True(Ann("conversations_search").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(Ann("conversations_bulk_update").GetProperty("destructiveHint").GetBoolean());
        Assert.True(Ann("calls_hang_up").GetProperty("destructiveHint").GetBoolean());

        // a page size smaller than the catalogue pages it with an opaque cursor
        var all = ToolRegistry.All.Select(t => t.Name).ToList();
        var (first, cursor) = McpServerEndpoints.Page(all, null, 100);
        Assert.Equal(100, first.Count);
        Assert.NotNull(cursor);
        var seen = new List<string>(first);
        while (cursor != null)
        {
            var (more, next) = McpServerEndpoints.Page(all, cursor, 100);
            seen.AddRange(more);
            cursor = next;
        }
        Assert.Equal(all, seen);
        Assert.Throws<ArgumentException>(() => McpServerEndpoints.Page(all, "not-a-cursor!", 100));
        var bad = await Platform.Rpc(super, "tools/list", new { cursor = "%%%" });
        Assert.Equal(-32602, bad.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_read_per_domain_works_for_an_admin_key()
    {
        var admin = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        var adminSession = await Platform.As(_f, "admin", "admin123");
        await Platform.Simulate(adminSession, "whatsapp", "60133300001", "hello there");
        var contactId = Platform.WithDb(_f, db => db.Contacts.First().Id);

        var reads = new (string Tool, object Args)[]
        {
            ("me_get_profile", new { }),
            ("contacts_get_channels", new { id = contactId }),
            ("contacts_get_ai_mode", new { id = contactId }),
            ("contacts_list_invites", new { id = contactId }),
            ("contacts_get_timeline", new { phone = "+60133300001" }),
            ("conversations_search", new { status = new[] { "open" }, limit = 5 }),
            ("conversations_search", new { count_only = true }),
            ("conversations_list_needing_human", new { }),
            ("conversations_get_state", new { channel = "whatsapp", key = "60133300001" }),
            ("conversations_list_messages", new { channel = "whatsapp", key = "60133300001" }),
            ("conversations_get_identity_request", new { channel = "whatsapp", key = "60133300001" }),
            ("ai_list_drafts", new { status = "all" }), ("ai_get_monitor_stats", new { days = 7 }),
            ("ai_get_behaviour", new { }), ("ai_get_controls", new { }), ("ai_get_handover_stats", new { }),
            ("ai_list_lessons", new { }), ("ai_get_learning_status", new { }), ("ai_get_learning_stats", new { }),
            ("ai_get_persona", new { }),
            ("knowledge_list", new { }), ("knowledge_test_retrieval", new { query = "opening hours" }),
            ("skills_list", new { }), ("skills_list_builtins", new { }), ("skills_list_apps", new { }),
            ("bookings_list", new { }), ("bookings_get_hours", new { }),
            ("templates_list_quick_replies", new { }), ("templates_list_call_scripts", new { }),
            ("templates_list_whatsapp", new { }),
            ("tags_list", new { }), ("tags_list_assignments", new { target_type = "contact" }),
            ("flows_list_runs", new { }), ("flows_list_queue", new { }), ("automations_list", new { }),
            ("automations_get_catalog", new { }),
            ("teams_list", new { }), ("users_list", new { }), ("users_list_agents", new { }), ("users_list_roles", new { }),
            ("users_get_permission_matrix", new { }), ("entities_list", new { }), ("bindings_get_catalog", new { }),
            ("channels_list_accounts", new { }), ("channels_list_inbox_settings", new { }), ("channels_get_health", new { }),
            ("channels_get_recovery", new { }), ("channels_get_routing_rules", new { }), ("channels_list_assignments", new { }),
            ("channels_list_widgets", new { }),
            ("integrations_list", new { }), ("integrations_list_providers", new { }), ("integrations_list_connections", new { }),
            ("integrations_list_my_connections", new { }),
            ("calls_list_logs", new { }), ("voice_list_voices", new { }), ("voice_get_providers", new { }),
            ("voice_get_pronunciation", new { }),
            ("notifications_list", new { }), ("notifications_get_my_preferences", new { }),
            ("notifications_get_org_defaults", new { }),
            ("settings_get_my_preferences", new { }), ("settings_get_org_appearance", new { }),
            ("settings_get_custom_fields", new { }), ("views_list", new { }),
            ("analytics_get_reports", new { days = 7 }), ("analytics_get_flows", new { }), ("billing_get_ai_bill", new { }),
            ("analytics_get_engagement", new { }), ("analytics_get_live", new { }), ("get_analytics_summary", new { }),
            ("activity_search_log", new { page_size = 5 }), ("activity_get_facets", new { }),
            ("search_everything", new { q = "liew" }), ("developers_list_api_keys", new { }),
            ("developers_get_mcp_info", new { }), ("assistant_list_conversations", new { }),
        };
        foreach (var (tool, args) in reads) await Ok(admin, tool, args);

        // the inbox search projects rows and links each one
        var search = await Ok(admin, "conversations_search", new { q = "60133300001" });
        var row = search.GetProperty("threads").EnumerateArray().First(t => t.GetProperty("key").GetString() == "60133300001");
        Assert.Equal("/chats?thread=whatsapp:60133300001", row.GetProperty("href").GetString());
        // the text a model reads is plain JSON, not \u-escaped ("+60…", not "+60…")
        var text = (await Platform.CallTool(admin, "conversations_search", new { q = "60133300001" }))
            .GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("\"+60133300001\"", text);
    }

    [Fact]
    public async Task Writes_across_domains_go_through_the_dashboards_endpoints()
    {
        var admin = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        var session = await Platform.As(_f, "admin", "admin123");
        await Platform.Simulate(session, "whatsapp", "60133300002", "do you deliver?");
        var thread = new { channel = "whatsapp", key = "60133300002" };

        // contacts: import (preview, then commit), AI mode, identity
        var preview = await Ok(admin, "contacts_import",
            new { csv = "name,phone\nMcp Imported,+60177700001\n", preview = true });
        Assert.Contains("1", preview.GetRawText());
        await Ok(admin, "contacts_import", new { csv = "name,phone\nMcp Imported,+60177700001\n" });
        var imported = Platform.WithDb(_f, db => db.Contacts.Single(c => c.Name == "Mcp Imported").Id);
        await Ok(admin, "contacts_set_ai_mode", new { id = imported, mode = "never" });
        Assert.Equal("never", Platform.WithDb(_f, db => db.Contacts.Single(c => c.Id == imported).AiMode));
        await Ok(admin, "contacts_add_identity", new { id = imported, channel = "email", address = "mcp.imported@example.com" });

        // conversations: labels, link, handover, AI tick, media
        await Ok(admin, "conversations_set_labels", new { thread.channel, thread.key, labels = new[] { "VIP", "mcp" } });
        await Ok(admin, "conversations_link_contact", new { thread.channel, thread.key, contact_id = imported });
        await Ok(admin, "conversations_handover", new { thread.channel, thread.key, action = "flag", reason = "check this" });
        await Ok(admin, "conversations_set_ai", new { thread.channel, thread.key, enabled = false });
        var state = await Ok(admin, "conversations_get_state", new { thread.channel, thread.key });
        Assert.Contains("vip", state.GetRawText());
        var t = Platform.WithDb(_f, db => db.Threads.Single(x => x.Channel == "whatsapp" && x.Key == "60133300002"));
        Assert.Equal(imported, t.ContactId);
        Assert.NotNull(t.HandoverAt);
        await Ok(admin, "conversations_send_media",
            new { thread.channel, thread.key, file_base64 = Png1x1, file_name = "dot.png", caption = "a dot" });
        Assert.True(Platform.WithDb(_f, db => db.Messages.Any(m => m.ThreadId == t.Id && m.MediaType != "")));
        await Ok(admin, "conversations_start", new { channel = "email", to = "new.lead@example.com", name = "New lead" });
        Assert.True(Platform.WithDb(_f, db => db.Threads.Any(x => x.Channel == "email" && x.Key == "new.lead@example.com")));

        // knowledge: create, edit, upload, delete (destructive → confirm)
        var k = await Ok(admin, "knowledge_create", new { kind = "faq", title = "Delivery", question = "Do you deliver?", content = "Yes, island-wide within 3 days." });
        var kid = k.GetProperty("id").GetInt32();
        await Ok(admin, "knowledge_update", new { id = kid, priority = 5 });
        await Ok(admin, "knowledge_upload_document", new { text = "# Price list\nWidget: RM10", title = "Price list" });
        Assert.Contains("confirm", await Err(admin, "knowledge_delete", new { id = kid }));
        Assert.True(Platform.WithDb(_f, db => db.KnowledgeItems.Any(x => x.Id == kid)));
        await Ok(admin, "knowledge_delete", new { id = kid, confirm = true });
        Assert.False(Platform.WithDb(_f, db => db.KnowledgeItems.Any(x => x.Id == kid)));

        // tags + teams
        var tag = await Ok(admin, "tags_create", new { name = "Mcp VIP", applies_to = "contact", color = "teal" });
        var tagId = tag.TryGetProperty("tag", out var tg) ? tg.GetProperty("id").GetInt32() : tag.GetProperty("id").GetInt32();
        await Ok(admin, "tags_assign", new { id = tagId, add = new[] { imported } });
        Assert.Contains(imported.ToString(), (await Ok(admin, "tags_list_targets", new { id = tagId })).GetRawText());
        var team = await Ok(admin, "teams_create", new { name = "Mcp Team", auto_assign = true });
        var teamId = team.TryGetProperty("team", out var tm) ? tm.GetProperty("id").GetInt32() : team.GetProperty("id").GetInt32();
        var agentId = Platform.WithDb(_f, db => db.Users.Single(u => u.Username == "agent").Id);
        await Ok(admin, "teams_set_members", new { team_id = teamId, add = new[] { agentId } });

        // flows: create, go live, read, delete
        var saved = await Ok(admin, "flows_save_flow", new { name = "Mcp flow", hint = "made over MCP" });
        var flowId = saved.GetProperty("flow_id").GetString()!;
        await Ok(admin, "flows_set_live", new { flow_id = flowId, live = true });
        Assert.True(Platform.WithDb(_f, db => db.Workflows.Single(w => w.FlowId == flowId).Active));
        Assert.Equal("Mcp flow", (await Ok(admin, "flows_get_flow", new { flow_id = flowId })).GetProperty("name").GetString());
        await Ok(admin, "flows_delete_flow", new { flow_id = flowId, confirm = true });
        Assert.False(Platform.WithDb(_f, db => db.Workflows.Any(w => w.FlowId == flowId)));

        // skills, custom fields, views, quick replies, lessons, behaviour, automations
        await Ok(admin, "skills_save_skill", new { name = "Mcp refunds", use_when = "refund questions", instructions = "Be kind." });
        Assert.True(Platform.WithDb(_f, db => db.AgentSkills.Any(s => s.SkillId == "mcp_refunds")));
        await Ok(admin, "skills_delete_skill", new { id = "mcp_refunds", confirm = true });
        Assert.False(Platform.WithDb(_f, db => db.AgentSkills.Any(s => s.SkillId == "mcp_refunds")));
        await Ok(admin, "settings_save_custom_field", new { label = "Mcp Grade", kind = "select", options = new[] { "A", "B" } });
        Assert.Contains("mcp_grade", (await Ok(admin, "settings_get_custom_fields", new { })).GetRawText());
        await Ok(admin, "settings_delete_custom_field", new { key = "mcp_grade", confirm = true });
        await Ok(admin, "views_create", new { name = "Mcp VIPs", filters = new { labels = "vip" } });
        await Ok(admin, "templates_create_quick_reply", new { short_code = "mcpthanks", content = "Thanks {{first_name}}!" });
        await Ok(admin, "ai_create_lesson", new { kind = "rule", text = "Never promise same-day delivery." });
        await Ok(admin, "ai_update_behaviour", new { values = new { ai_review_mode = "review_all" } });
        var rule = await Ok(admin, "automations_create", new
        {
            name = "Mcp label", @event = "message_created",
            conditions = Array.Empty<object>(), actions = new[] { new { type = "add_label", value = "auto" } },
        });
        var ruleId = rule.TryGetProperty("rule", out var ru) ? ru.GetProperty("id").GetInt32() : rule.GetProperty("id").GetInt32();
        await Ok(admin, "automations_test_rule", new { id = ruleId, thread.channel, thread.key, message = "hi" });

        // channels: a widget keeps its PUBLIC token; entities: create + delete
        var widget = await Ok(admin, "channels_create_widget", new { name = "Mcp site" });
        Assert.StartsWith("wgt", widget.GetProperty("token").GetString() ?? "wgt", StringComparison.Ordinal);
        Assert.NotEqual("[redacted]", widget.GetProperty("token").GetString());
        var ent = await Ok(admin, "entities_create", new { name = "Mcp Branch", slug = "mcp-branch", parent = "main" });
        var entId = ent.TryGetProperty("id", out var eidEl) ? eidEl.GetInt32()
            : Platform.WithDb(_f, db => db.Entities.Single(e => e.Slug == "mcp-branch").Id);
        await Ok(admin, "entities_delete", new { id = entId, confirm = true });
        await Ok(admin, "notifications_mark_all_read", new { });

        // the writes ran as the key's owner, recorded under source mcp
        var rows = await Platform.Audit(session, "source=mcp");
        Assert.Contains(rows, r => r.GetProperty("action").GetString() == "thread.labels");
    }

    [Fact]
    public async Task More_writes_map_their_arguments_onto_the_endpoints()
    {
        var admin = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        var session = await Platform.As(_f, "admin", "admin123");
        var contact = (await Ok(admin, "create_contact", new { name = "Mcp Second", phone = "+60177700009" }))
            .GetProperty("contact").GetProperty("id").GetInt32();

        // AI master switch, routing rules, assignments, settings, preferences
        await Ok(admin, "ai_set_controls", new { mode = "off" });
        Assert.Contains("off", (await Ok(admin, "ai_get_controls", new { })).GetRawText());
        await Ok(admin, "ai_set_controls", new { mode = "on" });
        await Ok(admin, "channels_set_routing_rules", new { rules = new { cold_text_guard = "warn" } });
        Assert.Contains("warn", (await Ok(admin, "channels_get_routing_rules", new { })).GetRawText());
        await Ok(admin, "settings_set_my_preferences", new { locale = "ms" });
        Assert.Contains("ms", (await Ok(admin, "settings_get_my_preferences", new { })).GetRawText());
        await Ok(admin, "notifications_set_my_preferences", new { only_assigned = true });
        await Ok(admin, "notifications_set_org_defaults", new { categories = new { flows = false } });
        Assert.Contains("false", (await Ok(admin, "notifications_get_org_defaults", new { })).GetRawText());
        await Ok(admin, "voice_set_pronunciation", new { terms = new[] { new { term = "ChatBranch", alias = "chat branch" } } });
        Assert.Contains("chat branch", (await Ok(admin, "voice_get_pronunciation", new { })).GetRawText());

        // users within the admin's rights
        var user = await Ok(admin, "users_create", new { username = "mcp-agent-1", password = "Sup3r-Secret-pw!", role = "agent" });
        Assert.DoesNotContain("Sup3r-Secret-pw!", user.GetRawText());
        var uid = Platform.WithDb(_f, db => db.Users.Single(u => u.Username == "mcp-agent-1").Id);
        await Ok(admin, "users_update", new { id = uid, active = false });
        Assert.False(Platform.WithDb(_f, db => db.Users.Single(u => u.Id == uid).IsActive));
        await Ok(admin, "users_delete", new { id = uid, confirm = true });

        // bindings + entities + tags on an entity
        await Ok(admin, "entities_create", new { name = "Mcp Outlet", slug = "mcp-outlet", parent = "main" });
        await Ok(admin, "contacts_add_entity", new { id = contact, entity = "mcp-outlet" });
        Assert.Contains("mcp-outlet", (await Ok(admin, "contacts_list_entities", new { id = contact })).GetRawText());
        await Ok(admin, "contacts_remove_entity", new { id = contact, entity = "mcp-outlet", confirm = true });
        var outletId = Platform.WithDb(_f, db => db.Entities.Single(e => e.Slug == "mcp-outlet").Id);
        await Ok(admin, "bindings_apply", new { kind = "contact-entities", target = contact, add = new[] { outletId } });
        Assert.Contains(outletId, (await Ok(admin, "bindings_get", new { kind = "contact-entities", target = contact }))
            .GetProperty("member_ids").EnumerateArray().Select(x => x.GetInt32()));
        var etag = await Ok(admin, "tags_create", new { name = "Mcp Inner", applies_to = "entity", color = "ink" });
        var etagId = etag.TryGetProperty("tag", out var et) ? et.GetProperty("id").GetInt32() : etag.GetProperty("id").GetInt32();
        await Ok(admin, "entities_set_tags", new { slug = "mcp-outlet", tags = new[] { new { tag_id = etagId } } });
        Assert.Contains("Mcp Inner", (await Ok(admin, "entities_get_tags", new { slug = "mcp-outlet" })).GetRawText());
        await Ok(admin, "tags_update", new { id = etagId, color = "rose" });
        await Ok(admin, "tags_delete", new { id = etagId, force = true, confirm = true });

        // contact invites, channel assignment, call scripts
        var invite = await Ok(admin, "contacts_create_invite", new { id = contact, channel = "whatsapp" });
        Assert.Equal(8, invite.GetProperty("code").GetString()!.Length);
        await Ok(admin, "templates_create_call_script", new { name = "Mcp script", content = "Hello {{first_name}}" });

        // a manual flow started for a contact, the bulk inbox action, a dial with no voice line
        await Platform.Simulate(session, "whatsapp", "60133300009", "hi");
        var bulk = await Err(admin, "conversations_bulk_update", new { action = "resolve", threads = new[] { "whatsapp:60133300009" } });
        Assert.Contains("confirm", bulk);
        await Ok(admin, "conversations_bulk_update", new { action = "resolve", threads = new[] { "whatsapp:60133300009" }, confirm = true });
        Assert.Equal("resolved", Platform.WithDb(_f, db => db.Threads.Single(t => t.Key == "60133300009").Status));
        var dial = await Err(admin, "calls_dial", new { contact_id = contact });
        Assert.Contains("LIVEKIT", dial.ToUpperInvariant());
        var oauth = await Ok(admin, "integrations_start_oauth", new { provider = "google_calendar" });
        Assert.True(oauth.TryGetProperty("configured", out _));
        Assert.NotEmpty(await Platform.Audit(session, "source=mcp&action=tool.calls_dial"));
    }

    [Fact]
    public async Task Rbac_scope_and_read_only_keys_hold()
    {
        var agent = Platform.Client(_f, Platform.MintKey(_f, "agent"));
        var listed = (await ListTools(agent)).Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("conversations_search", listed);
        Assert.Contains("ai_list_drafts", listed);          // agents hold ai.monitor
        Assert.DoesNotContain("users_create", listed);       // users.manage
        Assert.DoesNotContain("contacts_delete", listed);    // contacts.manage
        Assert.DoesNotContain("entities_create", listed);
        Assert.DoesNotContain("channels_check_health", listed); // channels.manage
        Assert.Contains("users.manage", await Err(agent, "users_create", new { username = "x1", password = "Passw0rd!x" }));
        Assert.Contains("channels.manage", await Err(agent, "channels_check_health", new { }));
        Assert.DoesNotContain("voice_set_stt_provider", listed); // settings.ai.manage (the Voice & calls page)
        Assert.Contains("settings.ai.manage", await Err(agent, "voice_set_stt_provider", new { stt_provider = "openai" }));

        // a scoped key without any .manage/.send capability is read-only
        var readOnly = Platform.Client(_f, Platform.MintKey(_f, "admin", "contacts.view", "chats.view"));
        var roList = (await ListTools(readOnly)).ToList();
        Assert.All(roList, t => Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(),
            t.GetProperty("name").GetString()));
        Assert.Contains("read-only", await Err(readOnly, "views_create", new { name = "x", filters = new { } }));
        await Ok(readOnly, "conversations_search", new { limit = 1 });

        // a key scoped to tags can't use the shared bindings route to rebind users
        var tagsKey = Platform.Client(_f, Platform.MintKey(_f, "admin", "tags.view", "tags.manage"));
        Assert.Contains("users.manage", await Err(tagsKey, "bindings_apply",
            new { kind = "user-entities", target = 1, add = new[] { 1 } }));

        // out-of-scope conversation → not found, for every conversation tool
        var (a, b) = Platform.WithDb(_f, db =>
        {
            var tenant = db.Tenants.Single(x => x.Slug == "demo");
            var main = db.Entities.Single(x => x.Slug == "main");
            Entity Make(string slug) => new()
            {
                TenantId = tenant.Id, ParentId = main.Id, Name = slug, Slug = slug, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var ea = Make("mcp-a-" + Guid.NewGuid().ToString("N")[..5]);
            var eb = Make("mcp-b-" + Guid.NewGuid().ToString("N")[..5]);
            db.Entities.AddRange(ea, eb);
            db.SaveChanges();
            db.Threads.Add(new Thread
            {
                Channel = "whatsapp", Key = "60188800002", Address = "60188800002", Name = "theirs", EntityId = eb.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            var u = new AppUser { Username = "mcp-scoped-" + ea.Slug, Password = "x", IsActive = true };
            db.Users.Add(u);
            db.SaveChanges();
            var p = new Profile { UserId = u.Id, Role = Roles.Agent, TenantId = tenant.Id };
            p.Entities.Add(ea);
            db.Profiles.Add(p);
            db.SaveChanges();
            return (ea, eb);
        });
        var scoped = Platform.Client(_f, Platform.MintKey(_f, "mcp-scoped-" + a.Slug));
        foreach (var (tool, args) in new (string, object)[]
                 {
                     ("conversations_get_state", new { channel = "whatsapp", key = "60188800002" }),
                     ("conversations_set_labels", new { channel = "whatsapp", key = "60188800002", labels = new[] { "x" } }),
                     ("conversations_handover", new { channel = "whatsapp", key = "60188800002", action = "take" }),
                     ("conversations_suggest_reply", new { channel = "whatsapp", key = "60188800002" }),
                     ("conversations_start", new { channel = "whatsapp", to = "+60188800002" }),
                 })
            Assert.Contains("scope", await Err(scoped, tool, args));
        Assert.Contains("scope", await Err(scoped, "bookings_list", new { entity = b.Slug }));
        var mine = await Ok(scoped, "conversations_search", new { limit = 200 });
        Assert.DoesNotContain("60188800002", mine.GetRawText());
    }

    [Fact]
    public async Task Destructive_tools_need_confirm_over_mcp_and_secrets_never_leave()
    {
        var admin = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        var contact = await Ok(admin, "create_contact", new { name = "Mcp Deletable", phone = "+60199900001" });
        var id = contact.GetProperty("contact").GetProperty("id").GetInt32();
        var refused = await Err(admin, "contacts_delete", new { id });
        Assert.Contains("confirm", refused);
        Assert.True(Platform.WithDb(_f, db => db.Contacts.Any(c => c.Id == id)));
        Assert.Contains(await Platform.Audit(await Platform.As(_f, "admin", "admin123"), "source=mcp&action=tool.contacts_delete"),
            r => r.GetProperty("status").GetInt32() == 400);
        await Ok(admin, "contacts_delete", new { id, confirm = true });
        Assert.False(Platform.WithDb(_f, db => db.Contacts.Any(c => c.Id == id)));

        // a channel account's credentials never come back through a tool
        Platform.WithDb(_f, db =>
        {
            db.ChannelAccounts.Add(new ChannelAccount
            {
                Channel = "whatsapp", Provider = Providers.Cloud, Name = "Mcp secret number", ExternalId = "PN-MCP-1",
                AccessToken = "EAA-super-secret-token-987654321", CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });
        var accounts = await Ok(admin, "channels_list_accounts", new { });
        Assert.Contains("Mcp secret number", accounts.GetRawText());
        Assert.DoesNotContain("EAA-super-secret-token-987654321", accounts.GetRawText());
        var settings = await Ok(admin, "channels_list_inbox_settings", new { });
        Assert.DoesNotContain("EAA-super-secret-token-987654321", settings.GetRawText());

        // the redaction net itself
        var shaped = ToolResults.Compact(JsonSerializer.SerializeToElement(new
        {
            api_key = "sk-live-123", access_token_masked = "EAA…4321", smtp_password = "hunter2", count = 3,
            items = Enumerable.Range(0, 80).Select(i => new { channel = "sms", key = $"6012{i}" }).ToList(),
        }))!;
        Assert.Equal("[redacted]", shaped["api_key"]!.GetValue<string>());
        Assert.Equal("[redacted]", shaped["smtp_password"]!.GetValue<string>());
        Assert.Equal("EAA…4321", shaped["access_token_masked"]!.GetValue<string>());
        var text = shaped.ToJsonString();
        Assert.Contains("items_truncated", text);
        Assert.Contains("/chats?thread=sms:60120", text);
    }
}

/// <summary>Ask ChatBranch keeps a bounded tool list and loads more with find_tools.</summary>
public class AssistantToolRouterTests : IClassFixture<AssistantFactory>
{
    private readonly AssistantFactory _f;
    public AssistantToolRouterTests(AssistantFactory f) => _f = f;

    [Fact]
    public void The_router_picks_by_meaning_and_keeps_the_core()
    {
        var all = ToolRegistry.All;
        var picked = ToolRouter.Select(all, "approve the AI draft waiting for review").Select(t => t.Name).ToList();
        Assert.Contains("ai_approve_draft", picked);
        Assert.Contains("search_contacts", picked);
        Assert.True(picked.Count <= ToolRouter.Core.Length + ToolRouter.MaxPicked);
        Assert.Contains("bookings_cancel", ToolRouter.Find(all, "cancel the appointment").Select(t => t.Name));
        Assert.Contains("teams_create", ToolRouter.Find(all, "create a new team").Select(t => t.Name));
        Assert.Contains("entities_create", ToolRouter.Find(all, "add an outlet").Select(t => t.Name));
    }

    [Fact]
    public void Social_requests_pull_in_the_social_tools_in_english_malay_and_chinese()
    {
        var all = ToolRegistry.All;
        Assert.Contains("social_create_post", ToolRouter.Select(all, "schedule an Instagram post for Friday with a caption").Select(t => t.Name));
        Assert.Contains("social_list_posts", ToolRouter.Find(all, "which posts are scheduled on our Facebook Page?").Select(t => t.Name));
        Assert.Contains("social_hide_comment", ToolRouter.Find(all, "hide that spam comment on TikTok").Select(t => t.Name));
        Assert.Contains("social_list_accounts", ToolRouter.Find(all, "which social accounts are connected? LinkedIn, YouTube").Select(t => t.Name));
        Assert.Contains("social_create_post", ToolRouter.Select(all, "tolong jadual siaran baru dengan kapsyen").Select(t => t.Name));
        Assert.Contains("social_create_post", ToolRouter.Select(all, "帮我发帖到脸书").Select(t => t.Name));
        // the AI-draft request still finds the AI tools first
        Assert.Contains("ai_approve_draft", ToolRouter.Select(all, "approve the AI draft waiting for review").Select(t => t.Name));
        // writes stay confirm-first: publishing and deleting are outward-facing / destructive
        Assert.True(ToolRegistry.All.Single(t => t.Name == "social_delete_post").Destructive);
    }

    [Fact]
    public async Task Find_tools_loads_more_and_writes_still_wait_for_confirmation()
    {
        lock (_f.Llm)
        {
            _f.Llm.Enabled = true;
            _f.Llm.Script.Clear();
            _f.Llm.OfferedTools.Clear();
            _f.Llm.Script.Enqueue(ScriptedLlm.Calls(ToolRouter.FindToolsName, new { query = "create team" }));
            _f.Llm.Script.Enqueue(ScriptedLlm.Calls("teams_create", new { name = "Night shift" }));
            _f.Llm.Script.Enqueue(ScriptedLlm.Says("I've prepared the team — please confirm."));
        }
        try
        {
            var admin = await Platform.As(_f, "admin", "admin123");
            var res = await admin.PostAsJsonAsync("/api/assistant/chat",
                new { messages = new[] { new { role = "user", content = "hello there" } }, context = new { path = "/teams" } });
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            var first = _f.Llm.OfferedTools.First()!.Select(t => t.Name).ToList();
            Assert.Contains(ToolRouter.FindToolsName, first);
            Assert.True(first.Count < 50, $"offered {first.Count} tools");
            Assert.Contains("teams_create", _f.Llm.OfferedTools.Last()!.Select(t => t.Name)); // loaded by find_tools
            var pending = Assert.Single(body.GetProperty("pending_actions").EnumerateArray());
            Assert.Equal("teams_create", pending.GetProperty("tool").GetString());
            Assert.False(Platform.WithDb(_f, db => db.Teams.Any(t => t.Name == "Night shift")));
            var confirm = await admin.PostAsJsonAsync("/api/assistant/confirm", new { id = pending.GetProperty("id").GetString() });
            confirm.EnsureSuccessStatusCode();
            Assert.True(Platform.WithDb(_f, db => db.Teams.Any(t => t.Name == "Night shift")));
        }
        finally { _f.Llm.Enabled = false; }
    }

    [Fact]
    public async Task A_destructive_tool_confirmed_in_the_assistant_needs_no_confirm_argument()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var created = await admin.PostAsJsonAsync("/api/views", new { scope = "chats", name = "Doomed", filters = new { } });
        created.EnsureSuccessStatusCode();
        var viewId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        lock (_f.Llm)
        {
            _f.Llm.Enabled = true;
            _f.Llm.Script.Clear();
            _f.Llm.Script.Enqueue(ScriptedLlm.Calls("views_delete", new { id = viewId }));
            _f.Llm.Script.Enqueue(ScriptedLlm.Says("Ready to delete it — confirm below."));
        }
        try
        {
            var res = await admin.PostAsJsonAsync("/api/assistant/chat",
                new { messages = new[] { new { role = "user", content = "delete my Doomed view" } } });
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            var pending = Assert.Single(body.GetProperty("pending_actions").EnumerateArray());
            var confirm = await admin.PostAsJsonAsync("/api/assistant/confirm", new { id = pending.GetProperty("id").GetString() });
            Assert.True((await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        }
        finally { _f.Llm.Enabled = false; }
    }
}
