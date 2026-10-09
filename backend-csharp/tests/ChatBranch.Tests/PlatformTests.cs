using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Mcp;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>Sign-in, API keys and JSON-RPC helpers shared by the platform tests.</summary>
internal static class Platform
{
    public static async Task<string> Login(WebApplicationFactory<Program> f, string user, string pass)
    {
        var c = f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    public static HttpClient Client(WebApplicationFactory<Program> f, string token)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static async Task<HttpClient> As(WebApplicationFactory<Program> f, string user, string pass) =>
        Client(f, await Login(f, user, pass));

    /// <summary>Mint a key straight into the DB (for users who may not create one themselves).</summary>
    public static string MintKey(WebApplicationFactory<Program> f, string username, params string[] scopes)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var uid = db.Users.Single(u => u.Username == username).Id;
        var (token, row) = ApiKeyAuth.Mint(uid, $"test-{Guid.NewGuid():N}", scopes, null);
        db.ApiKeys.Add(row);
        db.SaveChanges();
        return token;
    }

    public static T WithDb<T>(WebApplicationFactory<Program> f, Func<AppDbContext, T> work)
    {
        using var scope = f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static async Task<JsonElement> Rpc(HttpClient c, string method, object? @params = null, int id = 1)
    {
        var res = await c.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id, method, @params = @params ?? new { } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> CallTool(HttpClient c, string name, object args) =>
        (await Rpc(c, "tools/call", new { name, arguments = args })).GetProperty("result");

    public static async Task<List<JsonElement>> Audit(HttpClient c, string query)
    {
        var res = await c.GetFromJsonAsync<JsonElement>($"/api/audit?page_size=200&{query}");
        return res.GetProperty("entries").EnumerateArray().ToList();
    }

    public static async Task Simulate(HttpClient c, string channel, string key, string text = "hello")
    {
        var res = await c.PostAsJsonAsync("/api/whatsapp/simulate", new { channel, key, text, name = "Sim " + key });
        res.EnsureSuccessStatusCode();
    }
}

/// <summary>#20 — the activity log: middleware, provenance, visibility, filters.</summary>
public class AuditTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AuditTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Middleware_logs_a_mutation_the_endpoint_did_not_audit()
    {
        var c = await Platform.As(_f, "superadmin", "super123");
        var res = await c.PutAsJsonAsync("/api/settings", new { key = "quiet_start", value = "21:30" });
        res.EnsureSuccessStatusCode();
        var requestId = res.Headers.GetValues("X-Request-Id").Single();

        var rows = await Platform.Audit(c, $"q={Uri.EscapeDataString(requestId)}");
        var row = Assert.Single(rows);
        Assert.Equal("PUT /api/settings", row.GetProperty("action").GetString());
        Assert.Equal("user", row.GetProperty("source").GetString());
        Assert.Equal("superadmin", row.GetProperty("actor").GetString());
        Assert.Equal("PUT", row.GetProperty("method").GetString());
        Assert.Equal("/api/settings", row.GetProperty("path").GetString());
        Assert.Equal(200, row.GetProperty("status").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("user_id").ValueKind);
        // never the body
        Assert.DoesNotContain("21:30", row.GetRawText());
    }

    [Fact]
    public async Task An_audited_endpoint_gets_one_row_stamped_with_provenance()
    {
        var c = await Platform.As(_f, "admin", "admin123");
        var res = await c.PostAsJsonAsync("/api/teams", new { name = "Audit Squad" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var requestId = res.Headers.GetValues("X-Request-Id").Single();

        var rows = await Platform.Audit(c, $"q={Uri.EscapeDataString(requestId)}");
        var row = Assert.Single(rows); // the domain row, no generic duplicate
        Assert.Equal("team.create", row.GetProperty("action").GetString());
        Assert.Equal("user", row.GetProperty("source").GetString());
        Assert.Equal("POST", row.GetProperty("method").GetString());
        Assert.Equal("/api/teams", row.GetProperty("path").GetString());
        Assert.Equal(201, row.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Api_key_calls_are_source_api_and_scoped_read_only_keys_cannot_write()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var created = await admin.PostAsJsonAsync("/api/developers/keys", new { name = "ci script" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var keyClient = _f.CreateClient();
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", token);
        var res = await keyClient.PostAsJsonAsync("/api/teams", new { name = "Made by a key" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var rows = await Platform.Audit(admin, "source=api&action=team.create");
        Assert.Contains(rows, r => r.GetProperty("target").GetString() == "Made by a key"
                                   && r.GetProperty("actor").GetString() == "admin");

        // a key scoped to contacts.view only is read-only on REST
        var readOnly = Platform.Client(_f, Platform.MintKey(_f, "admin", "contacts.view"));
        var denied = await readOnly.PostAsJsonAsync("/api/teams", new { name = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/contacts")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_call_controls_are_recorded_too()
    {
        var anon = _f.CreateClient();
        var res = await anon.PostAsync("/api/calls/audit-room-1/hangup", null);
        var requestId = res.Headers.GetValues("X-Request-Id").Single();

        var sa = await Platform.As(_f, "superadmin", "super123");
        var row = Assert.Single(await Platform.Audit(sa, $"q={Uri.EscapeDataString(requestId)}"));
        Assert.Equal("POST /api/calls/{room}/hangup", row.GetProperty("action").GetString());
        Assert.Equal("anonymous", row.GetProperty("actor").GetString());
        Assert.Equal("room=audit-room-1", row.GetProperty("target").GetString());
        Assert.Equal((int)res.StatusCode, row.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Failed_logins_and_logouts_are_logged()
    {
        var anon = _f.CreateClient();
        var bad = await anon.PostAsJsonAsync("/api/auth/login", new { username = "agent", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var agent = await Platform.As(_f, "agent", "agent123");
        (await agent.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();

        var sa = await Platform.As(_f, "superadmin", "super123");
        var failed = await Platform.Audit(sa, "action=login.failed&actor=agent");
        var f = Assert.Single(failed);
        Assert.Equal(401, f.GetProperty("status").GetInt32());
        Assert.Equal("wrong password", f.GetProperty("detail").GetString());
        Assert.NotEmpty(await Platform.Audit(sa, "action=logout&actor=agent"));
    }

    [Fact]
    public async Task Admin_sees_their_tenant_and_agents_see_only_their_own_rows()
    {
        // a second tenant with its own user + activity
        Platform.WithDb(_f, db =>
        {
            var other = new Tenant { Name = "Other", Slug = "other-" + Guid.NewGuid().ToString("N")[..6],
                Status = "active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Tenants.Add(other);
            db.SaveChanges();
            var u = new AppUser { Username = "outsider-" + other.Slug, Password = "x", IsActive = true };
            db.Users.Add(u);
            db.SaveChanges();
            db.Profiles.Add(new Profile { UserId = u.Id, Role = Roles.Admin, TenantId = other.Id });
            db.AuditLogs.Add(new AuditLog { Actor = u.Username, UserId = u.Id, Action = "secret.thing",
                Source = "user", CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
            return 0;
        });

        var agent = await Platform.As(_f, "agent", "agent123");
        await Platform.Simulate(agent, "whatsapp", "60111000111", "agent visible");

        var admin = await Platform.As(_f, "admin", "admin123");
        var adminRows = await Platform.Audit(admin, "actor=agent");
        Assert.Contains(adminRows, r => r.GetProperty("action").GetString() == "POST /api/whatsapp/simulate");
        Assert.Empty(await Platform.Audit(admin, "action=secret.thing"));

        var agentRows = await Platform.Audit(agent, "");
        Assert.NotEmpty(agentRows);
        Assert.All(agentRows, r => Assert.Equal("agent", r.GetProperty("actor").GetString()));

        var sa = await Platform.As(_f, "superadmin", "super123");
        Assert.Single(await Platform.Audit(sa, "action=secret.thing"));
    }

    [Fact]
    public async Task Filters_paging_facets_and_csv()
    {
        var entityId = Platform.WithDb(_f, db => db.Entities.Single(e => e.Slug == "main").Id);
        var marker = "flt-" + Guid.NewGuid().ToString("N")[..8];
        Platform.WithDb(_f, db =>
        {
            var uid = db.Users.Single(u => u.Username == "admin").Id;
            db.AuditLogs.AddRange(
                new AuditLog { Actor = "admin", UserId = uid, Action = "thread.status", Target = marker,
                    Source = "mcp", EntityId = entityId, CreatedAt = DateTime.UtcNow.AddDays(-3) },
                new AuditLog { Actor = "admin", UserId = uid, Action = "thread.labels", Target = marker,
                    Source = "assistant", CreatedAt = DateTime.UtcNow },
                new AuditLog { Actor = "admin", UserId = uid, Action = "contact.import", Target = marker,
                    Detail = "=SUM(A1)", Source = "api", CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
            return 0;
        });
        var c = await Platform.As(_f, "admin", "admin123");

        Assert.Equal(3, (await Platform.Audit(c, $"q={marker}")).Count);
        Assert.Single(await Platform.Audit(c, $"q={marker}&source=mcp"));
        Assert.Equal(2, (await Platform.Audit(c, $"q={marker}&source=mcp,assistant")).Count);
        Assert.Equal(2, (await Platform.Audit(c, $"q={marker}&action=thread.*")).Count);
        Assert.Single(await Platform.Audit(c, $"q={marker}&entity=main"));
        var since = DateTime.UtcNow.AddDays(-1).ToString("o");
        Assert.Equal(2, (await Platform.Audit(c, $"q={marker}&from={Uri.EscapeDataString(since)}")).Count);

        var page = await c.GetFromJsonAsync<JsonElement>($"/api/audit?q={marker}&page_size=1&page=2");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(3, page.GetProperty("pages").GetInt32());
        Assert.Equal(2, page.GetProperty("page").GetInt32());
        Assert.Single(page.GetProperty("entries").EnumerateArray());

        var facets = await c.GetFromJsonAsync<JsonElement>("/api/audit/facets");
        Assert.Equal(AuditSources.All.Length, facets.GetProperty("sources").GetArrayLength());
        Assert.Contains(facets.GetProperty("actions").EnumerateArray(),
            a => a.GetProperty("value").GetString() == "contact.import");

        var csv = await c.GetStringAsync($"/api/audit.csv?q={marker}");
        Assert.StartsWith("created_at,actor,source", csv.TrimStart('﻿'));
        Assert.Contains($"\"{marker}\"", csv);
        Assert.Contains("\"'=SUM(A1)\"", csv); // formula injection defused
    }
}

/// <summary>API keys + ChatBranch as an MCP server (#18 part 1) + the MCP client (part 2).</summary>
public class McpServerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public McpServerTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Key_lifecycle_create_list_use_revoke()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var res = await admin.PostAsJsonAsync("/api/developers/keys", new { name = "laptop", expires_in_days = 30 });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        Assert.StartsWith("cbk_", token);
        var id = body.GetProperty("key").GetProperty("id").GetInt32();

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/developers/keys");
        var listed = list.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("id").GetInt32() == id);
        Assert.DoesNotContain(token, listed.GetRawText());
        Assert.Equal("active", listed.GetProperty("status").GetString());

        var keyed = Platform.Client(_f, token);
        var who = await keyed.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        Assert.Equal("admin", who.GetProperty("username").GetString());
        Assert.Equal("api", who.GetProperty("auth_source").GetString());

        (await admin.DeleteAsync($"/api/developers/keys/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyed.GetAsync("/api/auth/whoami")).StatusCode);

        // agents may not mint keys; a key may not mint keys
        var agent = await Platform.As(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/developers/keys", new { name = "x" })).StatusCode);
        var adminKey = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await adminKey.PostAsJsonAsync("/api/developers/keys", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Mcp_handshake_list_and_call_over_http()
    {
        var mcp = Platform.Client(_f, Platform.MintKey(_f, "admin"));

        var init = await mcp.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } },
        });
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.True(init.Headers.Contains("Mcp-Session-Id"));
        var result = (await init.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("ChatBranch", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));

        var note = await mcp.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", method = "notifications/initialized" });
        Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);
        Assert.Equal(JsonValueKind.Object, (await Platform.Rpc(mcp, "ping")).GetProperty("result").ValueKind);

        var tools = (await Platform.Rpc(mcp, "tools/list")).GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        foreach (var expected in new[] { "search_contacts", "get_contact", "create_contact", "update_contact",
                     "list_conversations", "get_conversation", "send_message", "add_note",
                     "set_conversation_status", "assign_conversation", "list_templates", "search_knowledge",
                     "list_flows", "get_analytics_summary", "list_activity", "list_tags" })
            Assert.Contains(expected, tools);

        var found = await Platform.CallTool(mcp, "search_contacts", new { query = "liewxen" });
        Assert.False(found.GetProperty("isError").GetBoolean());
        Assert.Equal("liewxen", found.GetProperty("structuredContent").GetProperty("contacts")[0]
            .GetProperty("name").GetString());

        // a write through the real send path → the endpoint's own row, source mcp
        var admin = await Platform.As(_f, "admin", "admin123");
        await Platform.Simulate(admin, "whatsapp", "60122334455", "need help");
        var sent = await Platform.CallTool(mcp, "send_message",
            new { channel = "whatsapp", key = "60122334455", text = "On it!" });
        Assert.False(sent.GetProperty("isError").GetBoolean());
        var rows = await Platform.Audit(admin, "source=mcp&action=message.send");
        Assert.Contains(rows, r => r.GetProperty("target").GetString() == "whatsapp:60122334455");
        Assert.NotEmpty(await Platform.Audit(admin, "source=mcp&action=tool.search_contacts"));

        // the reasons tools/call can fail
        var unknown = await Platform.Rpc(mcp, "tools/call", new { name = "drop_tables", arguments = new { } });
        Assert.Equal(-32602, unknown.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32601, (await Platform.Rpc(mcp, "resources/list")).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Mcp_requires_an_api_key_and_rejects_get()
    {
        var anon = _f.CreateClient();
        var res = await anon.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(-32001, (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetInt32());

        var session = await Platform.As(_f, "admin", "admin123"); // a dashboard token is not a key
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await session.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" })).StatusCode);

        var keyed = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await keyed.GetAsync("/mcp")).StatusCode);
        // the same endpoint behind an /api/-only proxy (what /developers hands out)
        var viaApi = await (await keyed.PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 7, method = "ping" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, viaApi.GetProperty("id").GetInt32());
        Assert.True(viaApi.TryGetProperty("result", out _));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await keyed.GetAsync("/api/mcp")).StatusCode);
    }

    [Fact]
    public async Task Mcp_enforces_capabilities()
    {
        // agents lack chats.manage; a scoped key narrows further
        var agentKey = Platform.Client(_f, Platform.MintKey(_f, "agent"));
        var admin = await Platform.As(_f, "admin", "admin123");
        await Platform.Simulate(admin, "whatsapp", "60155500011");
        var denied = await Platform.CallTool(agentKey, "set_conversation_status",
            new { channel = "whatsapp", key = "60155500011", status = "resolved" });
        Assert.True(denied.GetProperty("isError").GetBoolean());
        Assert.Contains("chats.manage", denied.GetProperty("content")[0].GetProperty("text").GetString());

        var scoped = Platform.Client(_f, Platform.MintKey(_f, "admin", "contacts.view"));
        var listed = (await Platform.Rpc(scoped, "tools/list")).GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("search_contacts", listed);
        Assert.DoesNotContain("send_message", listed);
        var refused = await Platform.CallTool(scoped, "send_message",
            new { channel = "whatsapp", key = "60155500011", text = "hi" });
        Assert.True(refused.GetProperty("isError").GetBoolean());

        var rows = await Platform.Audit(admin, "source=mcp&action=tool.send_message");
        Assert.Contains(rows, r => r.GetProperty("status").GetInt32() == 403);
    }

    [Fact]
    public async Task Mcp_enforces_entity_scope()
    {
        var (branchA, branchB) = Platform.WithDb(_f, db =>
        {
            var tenant = db.Tenants.Single(t => t.Slug == "demo");
            var main = db.Entities.Single(e => e.Slug == "main");
            Entity Make(string slug) => new()
            {
                TenantId = tenant.Id, ParentId = main.Id, Name = slug, Slug = slug,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var a = Make("scope-a-" + Guid.NewGuid().ToString("N")[..5]);
            var b = Make("scope-b-" + Guid.NewGuid().ToString("N")[..5]);
            db.Entities.AddRange(a, b);
            db.SaveChanges();
            Thread T(string key, int ent) => new()
            {
                Channel = "whatsapp", Key = key, Address = key, Name = key, EntityId = ent,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Threads.AddRange(T("60177000001", a.Id), T("60177000002", b.Id));
            // a fresh agent assigned to branch A only
            var u = new AppUser { Username = "scoped-agent-" + a.Slug, Password = "x", IsActive = true };
            db.Users.Add(u);
            db.SaveChanges();
            var p = new Profile { UserId = u.Id, Role = Roles.Agent, TenantId = tenant.Id };
            p.Entities.Add(a);
            db.Profiles.Add(p);
            db.SaveChanges();
            return (a, b);
        });
        var agent = Platform.Client(_f, Platform.MintKey(_f, "scoped-agent-" + branchA.Slug));

        var mine = await Platform.CallTool(agent, "get_conversation", new { channel = "whatsapp", key = "60177000001" });
        Assert.False(mine.GetProperty("isError").GetBoolean());

        var theirs = await Platform.CallTool(agent, "get_conversation", new { channel = "whatsapp", key = "60177000002" });
        Assert.True(theirs.GetProperty("isError").GetBoolean());
        Assert.Contains("scope", theirs.GetProperty("content")[0].GetProperty("text").GetString());

        var list = await Platform.CallTool(agent, "list_conversations", new { limit = 100 });
        var keys = list.GetProperty("structuredContent").GetProperty("conversations").EnumerateArray()
            .Select(c => c.GetProperty("key").GetString()).ToList();
        Assert.Contains("60177000001", keys);
        Assert.DoesNotContain("60177000002", keys);

        var widen = await Platform.CallTool(agent, "list_conversations", new { entity = branchB.Slug });
        Assert.True(widen.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task The_mcp_client_speaks_to_our_own_server()
    {
        var token = Platform.MintKey(_f, "admin");
        var http = _f.CreateClient();
        var row = new McpServerRow { Name = "Self", Url = new Uri(http.BaseAddress!, "/mcp").ToString(), AuthToken = token };
        var client = new McpClient(http, row);
        await client.InitializeAsync();
        Assert.Equal("ChatBranch", client.ServerInfo.GetProperty("name").GetString());
        var tools = await client.ListToolsAsync();
        Assert.True(tools.Count >= 16);
        Assert.True(tools.Single(t => t.Name == "search_contacts").ReadOnly);
        Assert.False(tools.Single(t => t.Name == "send_message").ReadOnly);
        var (text, isError) = McpClient.Flatten(await client.CallToolAsync("list_tags",
            JsonDocument.Parse("{}").RootElement));
        Assert.False(isError);
        Assert.Contains("contact_tags", text);

        row.AuthToken = "cbk_nope_nope";
        await Assert.ThrowsAsync<McpException>(() => new McpClient(http, row).InitializeAsync());
    }
}

/// <summary>A test host whose LLM is a scripted fake the tests drive.</summary>
public class AssistantFactory : ApiFactory
{
    public ScriptedLlm Llm { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<ILlmClient>();
            s.AddSingleton<ILlmClient>(Llm);
        });
}

/// <summary>Replays canned rounds; when the script is empty it behaves like no model at all.</summary>
public sealed class ScriptedLlm : ILlmClient
{
    public Queue<LlmChatResult> Script { get; } = new();
    public List<IReadOnlyList<LlmTool>?> OfferedTools { get; } = new();
    public bool Enabled { get; set; }
    public bool IsConfigured => Enabled;

    public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
        CancellationToken ct = default) => Task.FromResult(new LlmResult("", 0, 0, "test", "m"));

    public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
        CancellationToken ct = default)
    {
        OfferedTools.Add(tools);
        return Task.FromResult(Script.Count > 0 ? Script.Dequeue()
            : new LlmChatResult("(script exhausted)", Array.Empty<LlmToolCall>(), 1, 1, "test", "m"));
    }

    public static LlmChatResult Calls(string name, object args) =>
        new("", new[] { new LlmToolCall("c" + Guid.NewGuid().ToString("N")[..6], name, JsonSerializer.Serialize(args)) },
            10, 5, "test", "m");

    public static LlmChatResult Says(string text) => new(text, Array.Empty<LlmToolCall>(), 7, 3, "test", "m");
}

/// <summary>#17 — the operator assistant: reads run, writes wait for confirmation.</summary>
public class AssistantTests : IClassFixture<AssistantFactory>
{
    private readonly AssistantFactory _f;
    public AssistantTests(AssistantFactory f) => _f = f;

    private static object Ask(string text) =>
        new { messages = new[] { new { role = "user", content = text } }, context = new { path = "/dashboard" } };

    [Fact]
    public async Task Reads_execute_writes_wait_and_confirm_runs_them_once()
    {
        lock (_f.Llm)
        {
            _f.Llm.Enabled = true;
            _f.Llm.Script.Clear();
            _f.Llm.Script.Enqueue(ScriptedLlm.Calls("search_contacts", new { query = "Nadia" }));
            _f.Llm.Script.Enqueue(ScriptedLlm.Calls("create_contact", new { name = "Nadia Rahman", phone = "+60199887766" }));
            _f.Llm.Script.Enqueue(ScriptedLlm.Says("Nadia isn't a contact yet — I've prepared one for you to confirm."));
        }
        try
        {
            var admin = await Platform.As(_f, "admin", "admin123");
            var res = await admin.PostAsJsonAsync("/api/assistant/chat", Ask("add Nadia +60199887766"));
            res.EnsureSuccessStatusCode();
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("llm", body.GetProperty("mode").GetString());
            var steps = body.GetProperty("steps").EnumerateArray().ToList();
            Assert.Contains(steps, s => s.GetProperty("tool").GetString() == "search_contacts" && s.GetProperty("ok").GetBoolean());
            var pending = Assert.Single(body.GetProperty("pending_actions").EnumerateArray());
            Assert.Equal("create_contact", pending.GetProperty("tool").GetString());
            var id = pending.GetProperty("id").GetString()!;

            // proposed, not done
            var before = await admin.GetFromJsonAsync<JsonElement>("/api/contacts?q=Nadia");
            Assert.Equal(0, before.GetProperty("contacts").GetArrayLength());

            // someone else cannot confirm it
            var agent = await Platform.As(_f, "agent", "agent123");
            Assert.Equal(HttpStatusCode.NotFound,
                (await agent.PostAsJsonAsync("/api/assistant/confirm", new { id })).StatusCode);

            var confirm = await admin.PostAsJsonAsync("/api/assistant/confirm", new { id });
            confirm.EnsureSuccessStatusCode();
            Assert.True((await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
            var after = await admin.GetFromJsonAsync<JsonElement>("/api/contacts?q=Nadia");
            Assert.Equal(1, after.GetProperty("contacts").GetArrayLength());

            // once only
            Assert.Equal(HttpStatusCode.Conflict,
                (await admin.PostAsJsonAsync("/api/assistant/confirm", new { id })).StatusCode);

            var rows = await Platform.Audit(admin, "source=assistant");
            Assert.Contains(rows, r => r.GetProperty("action").GetString() == "tool.create_contact");
            Assert.Contains(rows, r => r.GetProperty("action").GetString() == "tool.search_contacts");
            Assert.True(Platform.WithDb(_f, db => db.LlmUsage.Any(u => u.Feature == "assistant")));
        }
        finally { _f.Llm.Enabled = false; }
    }

    [Fact]
    public async Task Agent_only_gets_the_tools_their_role_allows()
    {
        lock (_f.Llm)
        {
            _f.Llm.Enabled = true;
            _f.Llm.Script.Clear();
            _f.Llm.OfferedTools.Clear();
            _f.Llm.Script.Enqueue(ScriptedLlm.Says("Hello."));
        }
        try
        {
            var agent = await Platform.As(_f, "agent", "agent123");
            (await agent.PostAsJsonAsync("/api/assistant/chat", Ask("hi"))).EnsureSuccessStatusCode();
            var offered = _f.Llm.OfferedTools.Last()!.Select(t => t.Name).ToList();
            Assert.Contains("search_contacts", offered);
            Assert.DoesNotContain("create_contact", offered);      // contacts.manage
            Assert.DoesNotContain("set_conversation_status", offered); // chats.manage
        }
        finally { _f.Llm.Enabled = false; }
    }

    [Fact]
    public async Task Sandbox_mode_still_helps()
    {
        var admin = await Platform.As(_f, "admin", "admin123");

        async Task<JsonElement> Say(string text)
        {
            var r = await admin.PostAsJsonAsync("/api/assistant/chat", Ask(text));
            r.EnsureSuccessStatusCode();
            var b = await r.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("sandbox", b.GetProperty("mode").GetString());
            return b;
        }

        var found = await Say("find contact liewxen");
        Assert.Contains("liewxen", found.GetProperty("reply").GetString());
        Assert.Contains(found.GetProperty("navigation").EnumerateArray(),
            n => n.GetProperty("href").GetString()!.StartsWith("/contacts/"));

        var nav = await Say("go to analytics");
        Assert.Contains(nav.GetProperty("navigation").EnumerateArray(),
            n => n.GetProperty("href").GetString() == "/analytics");

        var howto = await Say("how do I connect WhatsApp?");
        Assert.Contains(howto.GetProperty("navigation").EnumerateArray(),
            n => n.GetProperty("href").GetString() == "/channels");

        await Platform.Simulate(admin, "whatsapp", "60188877766", "is anyone there?");
        var count = await Say("how many chats are waiting?");
        Assert.Contains("waiting", count.GetProperty("reply").GetString());

        var create = await Say("create contact Sandy Lee +60122223333");
        var pending = Assert.Single(create.GetProperty("pending_actions").EnumerateArray());
        var cancel = await admin.PostAsJsonAsync("/api/assistant/cancel", new { id = pending.GetProperty("id").GetString() });
        cancel.EnsureSuccessStatusCode();
        Assert.Equal("cancelled", (await cancel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }
}
