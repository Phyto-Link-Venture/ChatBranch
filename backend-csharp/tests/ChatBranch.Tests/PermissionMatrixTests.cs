using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Tests;

/// <summary>
/// "Every action follows the Permissions page": the operator actions that used to
/// be gated by a ROLE policy (admin+ / superadmin) are gated by a capability from
/// the catalog. For every one of them —
/// (a) a custom role holding only that capability gets past the gate (a 2xx, or the
///     handler's own 400 / 404 / 409 on the probe body — never 401 / 403),
/// (b) an admin whose matrix row denies it is refused (403),
/// (c) the built-in roles keep their defaults: superadmin through, admin through
///     unless the route was superadmin-only (then 403), agent 403.
/// Plus: no /api route carries a role policy any more, the matrix editor can't be
/// used to grant what its holder lacks, and someone BOUND to entities who is granted
/// a capability stays inside their entities (and never holds an org-level one).
/// </summary>
public class PermissionMatrixTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PermissionMatrixTests(ApiFactory f)
    {
        _f = f;
        _ = _f.CreateClient(); // boot + seed
    }

    /// <summary>One converted route: every group of <see cref="Caps"/> is required, ANY
    /// capability within a group. <see cref="NotAdmins"/> = admins were refused it before
    /// (superadmin-only, or rbac.manage checked in the handler) — its capability is one
    /// admins don't hold by default. <see cref="InHandler"/> = the
    /// first group is the kind-specific check made inside the handler (bindings).</summary>
    private sealed record Route(string Method, string Path, string[][] Caps, object? Body = null,
        string? Raw = null, bool NotAdmins = false, bool InHandler = false)
    {
        public override string ToString() => $"{Method} {Path}";
    }

    private static string[][] G(params string[] any) => new[] { any };

    private static readonly Route[] Routes =
    {
        // roles + the permission matrix (Users / Permissions)
        new("GET", "/api/auth/roles", G("users.view", "users.manage", "rbac.manage")),
        // rbac.manage was already checked inside these handlers: admins never held them
        new("POST", "/api/auth/roles", G("rbac.manage"), new { label = "" }, NotAdmins: true),
        new("PATCH", "/api/auth/roles/no-such-role", G("rbac.manage"), new { label = "Renamed" }, NotAdmins: true),
        new("DELETE", "/api/auth/roles/no-such-role", G("rbac.manage"), NotAdmins: true),
        new("GET", "/api/auth/permissions", G("users.view", "users.manage", "rbac.manage")),
        new("PUT", "/api/auth/permissions", G("rbac.manage"), new { matrix = new { } }, NotAdmins: true),
        new("GET", "/api/auth/audit", G("users.view", "users.manage")),
        // entities, contacts, record sync
        new("POST", "/api/v1/brands", G("entities.manage"), new { name = "Main", slug = "main" }),
        new("POST", "/api/v1/sync/records", G("contacts.manage"), new { }),
        new("POST", "/api/contacts", G("contacts.manage"), new { }),
        new("PATCH", "/api/contacts/999999", G("contacts.manage"), new { name = "x" }),
        new("POST", "/api/contacts/999999/channels", G("contacts.manage"), new { channel = "whatsapp", address = "60100000999" }),
        new("POST", "/api/contacts/import", G("contacts.manage"), new { }),
        new("POST", "/api/contacts/identity-checks/run", G("contacts.manage")),
        new("GET", "/api/contacts/import/google", G("contacts.manage")),
        // bindings: the kind's own capability, checked in the handler
        new("POST", "/api/bindings/user-entities", G("users.manage"), new { target = 0 }, InHandler: true),
        new("POST", "/api/bindings/contact-entities", G("contacts.manage"), new { target = 0 }, InHandler: true),
        new("POST", "/api/bindings/tag-contacts", G("tags.manage"), new { target = 0 }, InHandler: true),
        new("POST", "/api/bindings/entity-accounts", G("channels.manage"), new { target = 0 }, InHandler: true),
        // teams
        new("POST", "/api/teams", G("users.manage"), new { }),
        new("PATCH", "/api/teams/999999", G("users.manage"), new { name = "x" }),
        new("DELETE", "/api/teams/999999", G("users.manage")),
        // journeys, automations
        new("POST", "/api/flows/enqueue", G("flows.manage"),
            new { channel = "whatsapp", kind = "text", toAddress = "60100000998", payload = new { text = "matrix" } }),
        new("POST", "/api/flows/tick", G("flows.manage")),
        new("POST", "/api/automations", G("flows.manage"), new { }),
        new("PATCH", "/api/automations/999999", G("flows.manage"), new { name = "x" }),
        new("DELETE", "/api/automations/999999", G("flows.manage")),
        new("POST", "/api/automations/999999/test", G("flows.manage"), new { }),
        new("POST", "/api/automations/reorder", G("flows.manage"), new { ids = Array.Empty<int>() }),
        // AI
        new("DELETE", "/api/ai/lessons/999999", G("ai.behaviour.manage")),
        new("PUT", "/api/aicore/booking-hours", G("integrations.manage", "settings.ai.manage"), Raw: "not json"),
        new("DELETE", "/api/aicore/booking-hours", G("integrations.manage", "settings.ai.manage")),
        // voice & calls
        new("POST", "/api/voice/clone", G("settings.ai.manage"), new { }),
        new("POST", "/api/voice/clone-from", G("settings.ai.manage"), new { }),
        new("POST", "/api/voice/import", G("settings.ai.manage"), new { }),
        new("PATCH", "/api/voice/no-such-voice", G("settings.ai.manage"), new { name = "x" }),
        new("DELETE", "/api/voice/no-such-voice", G("settings.ai.manage")),
        new("PUT", "/api/voice/default-voice", G("settings.ai.manage"), new { voice = "no-such-voice" }),
        new("POST", "/api/voice/stt-settings", G("settings.ai.manage"), new { stt_provider = "nope" }),
        new("PUT", "/api/voice/stt-settings", G("settings.ai.manage"), new { stt_provider = "nope" }),
        new("POST", "/api/voice/llm-settings", G("settings.ai.manage"), new { llm_provider = "nope" }),
        new("PUT", "/api/voice/llm-settings", G("settings.ai.manage"), new { llm_provider = "nope" }),
        new("POST", "/api/pronunciation", G("pronunciation.manage"), new { }),
        // channels
        new("POST", "/api/messaging/accounts", G("channels.manage"), new { channel = "nope" }),
        new("PATCH", "/api/messaging/accounts/999999", G("channels.manage"), new { name = "x" }),
        new("DELETE", "/api/messaging/accounts/999999", G("channels.manage")),
        new("POST", "/api/messaging/accounts/999999/set-webhook", G("channels.manage"), new { }),
        new("POST", "/api/whatsapp/accounts", G("channels.manage"), new { provider = "baileys", name = "" }),
        new("POST", "/api/whatsapp/accounts/999999", G("channels.manage"), new { name = "x" }),
        new("DELETE", "/api/whatsapp/accounts/999999", G("channels.manage")),
        new("POST", "/api/whatsapp/accounts/999999/baileys/start", G("channels.manage")),
        new("POST", "/api/whatsapp/accounts/999999/baileys/logout", G("channels.manage")),
        new("POST", "/api/whatsapp/channels/health", G("channels.manage")),
        new("POST", "/api/widgets", G("channels.manage"), new { }),
        new("PATCH", "/api/widgets/999999", G("channels.manage"), new { name = "x" }),
        new("DELETE", "/api/widgets/999999", G("channels.manage")),
        new("POST", "/api/whatsapp/recovery/999999", G("account_assignment.manage")),
        new("POST", "/api/whatsapp/threads/matrix-none/account",
            new[] { new[] { "account_assignment.manage" }, new[] { "chats.send" } }, new { account_id = "x" }),
        new("POST", "/api/whatsapp/templates", G("templates.manage"), new { name = "NOT A NAME" }),
        new("PATCH", "/api/whatsapp/templates/no_such_template", G("templates.manage"), new { }),
        new("DELETE", "/api/whatsapp/templates/no_such_template", G("templates.manage")),
        // integrations + external MCP servers
        new("POST", "/api/integrations/accounts", G("integrations.manage"), new { provider = "nope" }),
        new("DELETE", "/api/integrations/accounts/999999", G("integrations.manage")),
        new("POST", "/api/integrations/mcp", G("api_keys.manage"), new { }),
        new("PATCH", "/api/integrations/mcp/999999", G("api_keys.manage"), new { name = "x" }),
        new("DELETE", "/api/integrations/mcp/999999", G("api_keys.manage")),
        new("POST", "/api/integrations/mcp/999999/test", G("api_keys.manage")),
        // the raw settings store
        new("GET", "/api/settings?keys=quiet_start", G("settings.raw.view")),
        // ---- the routes that were superadmin-only
        new("PUT", "/api/settings", G("settings.raw.manage"), new { key = "matrix_probe", value = "1" }, NotAdmins: true),
        new("POST", "/api/integrations/no-such-provider/connect",
            new[] { new[] { "integrations.manage" }, new[] { "app_keys.manage" } }, new { credentials = new { } }, NotAdmins: true),
        new("POST", "/api/messaging/settings", G("app_keys.manage"), new { }, NotAdmins: true),
        new("PUT", "/api/messaging/settings", G("app_keys.manage"), new { }, NotAdmins: true),
        new("PUT", "/api/whatsapp/voice-settings", G("app_keys.manage"), new { }, NotAdmins: true),
        // Settings → System email (the SMTP login welcome emails go out through)
        new("GET", "/api/settings/system-email", G("app_keys.manage"), NotAdmins: true),
        new("PUT", "/api/settings/system-email", G("app_keys.manage"), new { }, NotAdmins: true),
        new("POST", "/api/settings/system-email/test", G("app_keys.manage"), new { }, NotAdmins: true),
        new("GET", "/api/testlab/scenarios", G("testlab.run"), NotAdmins: true),
        new("GET", "/api/testlab/status", G("testlab.run"), NotAdmins: true),
        new("GET", "/api/testlab/runs", G("testlab.run"), NotAdmins: true),
        new("GET", "/api/testlab/runs/999999", G("testlab.run"), NotAdmins: true),
        new("POST", "/api/testlab/run", G("testlab.run"), new { ids = new[] { "no-such-scenario" } }, NotAdmins: true),
        new("POST", "/api/testlab/reset", G("testlab.run"), NotAdmins: true),
        new("POST", "/api/testlab/demo", G("testlab.run"), NotAdmins: true),
    };

    // ---- helpers -------------------------------------------------------------------

    private static bool Refused(HttpStatusCode s) => s is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    /// <summary>Past the gate: anything but a refusal or a crash.</summary>
    private static bool Through(HttpStatusCode s) => !Refused(s) && s != HttpStatusCode.InternalServerError;

    private static async Task<HttpStatusCode> Send(HttpClient c, Route r)
    {
        using var req = new HttpRequestMessage(new HttpMethod(r.Method), r.Path);
        if (r.Raw != null) req.Content = new StringContent(r.Raw, Encoding.UTF8, "application/json");
        else if (r.Body != null) req.Content = JsonContent.Create(r.Body);
        else if (r.Method is "POST" or "PUT" or "PATCH") req.Content = JsonContent.Create(new { });
        using var res = await c.SendAsync(req);
        return res.StatusCode;
    }

    private static int _roles;
    private readonly Dictionary<string, HttpClient> _custom = new();

    /// <summary>A custom role holding exactly these capabilities (and one user in it).</summary>
    private async Task<HttpClient> Holding(params string[] caps)
    {
        var key = string.Join("|", caps.OrderBy(x => x));
        if (_custom.TryGetValue(key, out var c)) return c;
        c = await SettingsTestKit.CustomRoleClient(_f, $"Matrix {Interlocked.Increment(ref _roles)}", caps);
        _custom[key] = c;
        return c;
    }

    private void SetRow(string role, IEnumerable<string> caps, bool? allowed) => SettingsTestKit.WithDb(_f, db =>
    {
        foreach (var cap in caps)
        {
            db.RolePermissions.RemoveRange(db.RolePermissions.Where(p => p.Role == role && p.Capability == cap));
            if (allowed is bool a)
                db.RolePermissions.Add(new RolePermission { Role = role, Capability = cap, Allowed = a, UpdatedAt = DateTime.UtcNow });
        }
        db.SaveChanges();
    });

    // ---- (a) the capability alone opens the route ----------------------------------

    [Fact]
    public async Task A_custom_role_holding_only_the_capability_gets_past_each_converted_gate()
    {
        var bad = new List<string>();
        foreach (var r in Routes)
            foreach (var first in r.Caps[0])
            {
                // one capability of the first group, plus one of each other group the route needs
                var caps = new[] { first }.Concat(r.Caps.Skip(1).Select(g => g[0])).ToArray();
                var s = await Send(await Holding(caps), r);
                if (!Through(s)) bad.Add($"{r} as [{string.Join(", ", caps)}] → {(int)s}");
            }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public async Task A_custom_role_without_the_capability_is_refused()
    {
        // holds every capability EXCEPT the route's first group — still 403
        var bad = new List<string>();
        foreach (var group in Routes.Select(r => r.Caps[0]).DistinctBy(g => string.Join("|", g)))
        {
            var c = await Holding(AllCaps.Except(group).ToArray());
            foreach (var r in Routes.Where(x => x.Caps[0].SequenceEqual(group)))
            {
                var s = await Send(c, r);
                if (s != HttpStatusCode.Forbidden) bad.Add($"{r} without [{string.Join(", ", group)}] → {(int)s}");
            }
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // ---- (b) an admin denied it on the Permissions page is refused -----------------

    [Fact]
    public async Task An_admin_whose_matrix_denies_the_capability_is_refused()
    {
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        var bad = new List<string>();
        foreach (var group in Routes.Where(r => !r.NotAdmins).Select(r => r.Caps[0])
                     .DistinctBy(g => string.Join("|", g)))
        {
            SetRow(Roles.Admin, group, false);
            try
            {
                foreach (var r in Routes.Where(x => !x.NotAdmins && x.Caps[0].SequenceEqual(group)))
                {
                    var s = await Send(admin, r);
                    if (s != HttpStatusCode.Forbidden) bad.Add($"{r} with admin denied [{string.Join(", ", group)}] → {(int)s}");
                }
            }
            finally { SetRow(Roles.Admin, group, null); }
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // ---- (c) the built-in roles keep their defaults --------------------------------

    [Fact]
    public async Task The_built_in_roles_keep_their_defaults()
    {
        var super = await SettingsTestKit.Client(_f, "superadmin", "super123");
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        var bad = new List<string>();
        foreach (var r in Routes)
        {
            var s = await Send(super, r);
            if (!Through(s)) bad.Add($"{r} superadmin → {(int)s}");
            s = await Send(admin, r);
            if (r.NotAdmins ? s != HttpStatusCode.Forbidden : !Through(s))
                bad.Add($"{r} admin → {(int)s} (expected {(r.NotAdmins ? "403" : "through")})");
            s = await Send(agent, r);
            if (s != HttpStatusCode.Forbidden) bad.Add($"{r} agent → {(int)s} (expected 403)");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // ---- the route table -----------------------------------------------------------

    private List<RouteEndpoint> ApiEndpoints() =>
        _f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith("/api/", StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void No_api_route_is_gated_by_a_role_policy()
    {
        var roleGated = ApiEndpoints()
            .Where(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(a => a.Policy is AuthPolicies.AdminPlus or AuthPolicies.SuperAdmin))
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>())} {e.RoutePattern.RawText}")
            .ToList();
        Assert.True(roleGated.Count == 0,
            "These routes are gated by a ROLE — gate them by a capability from the catalog so the Permissions page " +
            "governs them (RequireCapability / RequireAnyCapability):\n" + string.Join("\n", roleGated));
    }

    [Fact]
    public void Every_converted_route_carries_its_capability_gate_in_the_route_table()
    {
        var eps = ApiEndpoints();
        var bad = new List<string>();
        foreach (var r in Routes.Where(x => !x.InHandler))
        {
            var path = r.Path.Split('?')[0];
            var ep = eps.FirstOrDefault(e =>
                e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(r.Method) == true
                && RoutePatternMatcher(e.RoutePattern.RawText ?? "", path));
            if (ep == null) { bad.Add($"{r}: no such route"); continue; }
            var groups = ep.Metadata.GetOrderedMetadata<RequiredCapabilities>()
                .Select(c => string.Join("|", c.Any.OrderBy(x => x))).ToHashSet();
            foreach (var g in r.Caps.Select(g => string.Join("|", g.OrderBy(x => x))))
                if (!groups.Contains(g)) bad.Add($"{r}: expected the gate [{g}], the route has [{string.Join("] [", groups)}]");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    /// <summary>A literal path against a route template ({x} / {x:int} = one segment).</summary>
    private static bool RoutePatternMatcher(string template, string path)
    {
        var t = template.Trim('/').Split('/');
        var p = path.Trim('/').Split('/');
        if (t.Length != p.Length) return false;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i].StartsWith('{')) continue;
            if (!string.Equals(t[i], p[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static readonly string[] AllCaps =
        ((string[])typeof(ChatBranch.Api.Endpoints.AdminEndpoints)
            .GetField("CapabilityIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!);

    // ---- anti-escalation: the matrix editor ----------------------------------------

    [Fact]
    public async Task A_matrix_editor_below_superadmin_only_switches_capabilities_it_holds()
    {
        var editor = await Holding("rbac.manage", "users.view", "tags.view");
        var su = await SettingsTestKit.Client(_f, "superadmin", "super123");
        var target = await su.PostAsJsonAsync("/api/auth/roles", new { label = "Matrix target" });
        var slug = (await target.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;

        // a capability the editor holds: fine (and the whole-matrix echo of unchanged rows is fine too)
        var ok = await editor.PutAsJsonAsync("/api/auth/permissions",
            new { matrix = new Dictionary<string, Dictionary<string, bool>> { [slug] = new() { ["tags.view"] = true } } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var snapshot = await ok.Content.ReadFromJsonAsync<JsonElement>();
        var full = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(
            snapshot.GetProperty("matrix").GetRawText())!;
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsJsonAsync("/api/auth/permissions", new { matrix = full })).StatusCode);

        // one it doesn't hold: refused, nothing written
        var grant = await editor.PutAsJsonAsync("/api/auth/permissions",
            new { matrix = new Dictionary<string, Dictionary<string, bool>> { [slug] = new() { ["billing.manage"] = true } } });
        Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);
        Assert.False(Platform.WithDb(_f, db => db.RolePermissions.Any(p => p.Role == slug && p.Capability == "billing.manage" && p.Allowed)));
        // …nor taking one away it doesn't hold
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync("/api/auth/permissions",
            new { matrix = new Dictionary<string, Dictionary<string, bool>> { [Roles.Admin] = new() { ["contacts.manage"] = false } } })).StatusCode);
        // …nor the superadmin row
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync("/api/auth/permissions",
            new { matrix = new Dictionary<string, Dictionary<string, bool>> { [Roles.SuperAdmin] = new() { ["tags.view"] = false } } })).StatusCode);
        // a superadmin still switches anything
        Assert.Equal(HttpStatusCode.OK, (await su.PutAsJsonAsync("/api/auth/permissions",
            new { matrix = new Dictionary<string, Dictionary<string, bool>> { [slug] = new() { ["billing.manage"] = true } } })).StatusCode);
    }

    // ---- anti-escalation: user management ------------------------------------------

    [Fact]
    public async Task A_user_manager_below_superadmin_never_hands_out_more_than_it_holds()
    {
        var su = await SettingsTestKit.Client(_f, "superadmin", "super123");
        var agentCaps = new[] { "contacts.view", "chats.view", "chats.send", "flows.view", "analytics.view",
            "call_logs.view", "activity.view", "tags.view", "ai.monitor" };
        var hr = await Holding(agentCaps.Concat(new[] { "users.view", "users.manage" }).ToArray());
        var sfx = Guid.NewGuid().ToString("N")[..6];

        // an agent: every one of its permissions is the manager's → fine
        var agent = await hr.PostAsJsonAsync("/api/v1/users", new { username = $"hr-agent-{sfx}", password = "password123", role = Roles.Agent });
        Assert.Equal(HttpStatusCode.Created, agent.StatusCode);
        // a custom role holding billing.manage: refused
        var big = (await (await su.PostAsJsonAsync("/api/auth/roles", new { label = $"Big {sfx}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        (await su.PutAsJsonAsync("/api/auth/permissions", new
        {
            matrix = new Dictionary<string, Dictionary<string, bool>> { [big] = new() { ["billing.manage"] = true } },
        })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsJsonAsync("/api/v1/users",
            new { username = $"hr-big-{sfx}", password = "password123", role = big })).StatusCode);
        var agentId = (await agent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsJsonAsync($"/api/v1/users/{agentId}", new { role = big })).StatusCode);
        // never an admin, and never their own login
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsJsonAsync("/api/v1/users",
            new { username = $"hr-admin-{sfx}", password = "password123", role = Roles.Admin })).StatusCode);
        var mine = await hr.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        var myId = SettingsTestKit.UserId(_f, mine.GetProperty("username").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsJsonAsync($"/api/v1/users/{myId}", new { role = Roles.Agent })).StatusCode);
        // the users list follows the capability: the tenant, not just themselves
        var list = await hr.GetFromJsonAsync<JsonElement>("/api/v1/users");
        Assert.Contains(list.GetProperty("users").EnumerateArray(), u => u.GetProperty("username").GetString() == $"hr-agent-{sfx}");
    }

    // ---- bound people stay inside their entities -----------------------------------

    private (int A, string ASlug, int B) TwoCompanies(string sfx) => SettingsTestKit.WithDb(_f, db =>
    {
        var tenant = db.Tenants.First(t => t.Slug == "demo");
        var now = DateTime.UtcNow;
        var a = new Entity { TenantId = tenant.Id, Name = $"Matrix A {sfx}", Slug = $"matrix-a-{sfx}", CreatedAt = now, UpdatedAt = now };
        var b = new Entity { TenantId = tenant.Id, Name = $"Matrix B {sfx}", Slug = $"matrix-b-{sfx}", CreatedAt = now, UpdatedAt = now };
        db.Entities.AddRange(a, b);
        db.SaveChanges();
        return (a.Id, a.Slug, b.Id);
    });

    [Fact]
    public async Task A_bound_role_granted_a_capability_stays_inside_its_entities()
    {
        var sfx = Guid.NewGuid().ToString("N")[..6];
        var (a, aSlug, b) = TwoCompanies(sfx);
        var su = await SettingsTestKit.Client(_f, "superadmin", "super123");
        var role = (await (await su.PostAsJsonAsync("/api/auth/roles", new { label = $"Branch lead {sfx}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString()!;
        (await su.PutAsJsonAsync("/api/auth/permissions", new
        {
            matrix = new Dictionary<string, Dictionary<string, bool>>
            {
                [role] = new() { ["channels.view"] = true, ["channels.manage"] = true, ["users.manage"] = true,
                                 ["app_keys.manage"] = true, ["settings.ai.manage"] = true },
            },
        })).EnsureSuccessStatusCode();
        var username = $"branchlead{sfx}";
        var created = await su.PostAsJsonAsync("/api/v1/users",
            new { username, password = "password123", role, entity_ids = new[] { a } });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var lead = await SettingsTestKit.Client(_f, username, "password123");

        // inside their entity: through; another company or the organization level: 403
        var mine = await lead.PostAsJsonAsync("/api/teams", new { name = $"Team {sfx}", entity = aSlug });
        Assert.Equal(HttpStatusCode.Created, mine.StatusCode);
        var otherSlug = SettingsTestKit.WithDb(_f, db => db.Entities.First(e => e.Id == b).Slug);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PostAsJsonAsync("/api/teams", new { name = "Theirs", entity = otherSlug })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PostAsJsonAsync("/api/teams", new { name = "Everyone's" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PostAsJsonAsync("/api/messaging/accounts",
            new { channel = "viber", name = "org-wide", access_token = "tok-org" })).StatusCode);
        // organization-wide voice settings and the voice library are not a bound person's
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PutAsJsonAsync("/api/voice/stt-settings", new { stt_provider = "openai" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.DeleteAsync("/api/voice/no-such-voice")).StatusCode);
        // an organization-level capability is dropped for a bound person, whatever the matrix says
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PutAsJsonAsync("/api/messaging/settings", new { smtp_host = "smtp.example.test" })).StatusCode);
        var who = await lead.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        var caps = who.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("channels.manage", caps);
        Assert.DoesNotContain("app_keys.manage", caps);
        // the same capability, unbound, reaches the organization level
        var free = await Holding("channels.view", "channels.manage");
        var orgWide = await free.PostAsJsonAsync("/api/messaging/accounts",
            new { channel = "viber", name = $"org-wide {sfx}", access_token = "tok-org-2" });
        Assert.Equal(HttpStatusCode.OK, orgWide.StatusCode);
    }
}
