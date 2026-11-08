using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Read gates: knowing a page's URL must not hand its data to someone whose role
/// lacks the page's capability (the sidebar hiding the link is cosmetic). Every
/// gated GET is listed in <see cref="Gates"/> with the capabilities that open it;
/// a role with none gets 403, a role with any one of them gets through, and the
/// default agent keeps every read its pages use. The route-table walk at the end
/// fails on any new signed-in GET that carries neither a capability gate nor a
/// documented exemption — that is what keeps this from regressing.
/// </summary>
public class PermissionGatesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    private static readonly object Gate = new();
    private static int _contactId;
    private static int _tagId;

    public PermissionGatesTests(ApiFactory f)
    {
        _f = f;
        _ = _f.CreateClient(); // boot + seed
        Arrange();
    }

    private void Arrange()
    {
        lock (Gate)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = db.Contacts.FirstOrDefault(c => c.Name == "Gate Keeper");
            if (existing != null)
            {
                _contactId = existing.Id;
                _tagId = db.Tags.First(t => t.Slug == "gate-tag").Id;
                return;
            }
            var now = DateTime.UtcNow;
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var contact = new Contact
            {
                Name = "Gate Keeper", Phone = "+60100000777", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now,
            };
            var tag = new Tag { Name = "Gate tag", Slug = "gate-tag", AppliesTo = "contact", CreatedAt = now, UpdatedAt = now };
            db.Contacts.Add(contact);
            db.Tags.Add(tag);
            db.ChannelAccounts.Add(new ChannelAccount
            {
                Channel = "whatsapp", Provider = Providers.Cloud, Name = "Gate number", ExternalId = "PN-GATE",
                AccessToken = "EAA-gate-token-123456", CreatedAt = now,
            });
            db.SaveChanges();
            _contactId = contact.Id;
            _tagId = tag.Id;
        }
    }

    /// <summary>What the seeded agent role holds by default (AdminEndpoints.AgentAllow).</summary>
    private static readonly HashSet<string> AgentCaps = new()
    {
        "contacts.view", "chats.view", "chats.send", "flows.view", "analytics.view",
        "call_logs.view", "activity.view", "tags.view", "ai.monitor",
    };

    /// <summary>
    /// Every gated read, as a URL template ({c} = a contact id, {t} = a tag id) and
    /// the capabilities that open it (ANY of them). No read is gated by a role any
    /// more — every gate is a capability the Permissions page can grant.
    /// </summary>
    private static readonly (string Url, string[] Caps)[] Gates =
    {
        // AI monitor + behaviour
        ("/api/ai/monitor", new[] { "ai.monitor" }),
        ("/api/ai/monitor/stats", new[] { "ai.monitor" }),
        ("/api/ai/monitor/999999", new[] { "ai.monitor" }),
        ("/api/ai/behaviour", new[] { "ai.monitor", "ai.behaviour.manage" }),
        ("/api/ai/controls", new[] { "ai.monitor", "ai.behaviour.manage", "chats.view" }),
        ("/api/ai/handovers/stats", new[] { "ai.monitor", "chats.view" }),
        ("/api/ai/lessons", new[] { "ai.monitor" }),
        ("/api/ai/learning/status", new[] { "ai.monitor" }),
        ("/api/ai/learning/stats", new[] { "ai.monitor" }),
        ("/api/ai/feedback", new[] { "ai.monitor" }),
        ("/api/ai/playground/options", new[] { "ai.monitor", "ai.behaviour.manage" }),
        ("/api/ai/review/runs", new[] { "ai.monitor", "ai.behaviour.manage" }),
        ("/api/ai/review/runs/999999", new[] { "ai.monitor", "ai.behaviour.manage" }),
        ("/api/ai/review/options", new[] { "ai.behaviour.manage" }),
        ("/api/ai/tasks", new[] { "ai.monitor", "settings.ai.manage" }),
        ("/api/ai/tasks/999999/runs", new[] { "ai.monitor", "settings.ai.manage" }),
        // Library: knowledge, skills, persona
        ("/api/aicore/persona", new[] { "settings.ai.manage" }),
        ("/api/aicore/knowledge", new[] { "settings.ai.manage" }),
        ("/api/aicore/skills", new[] { "settings.ai.manage", "flows.manage" }),
        ("/api/aicore/skills/apps", new[] { "settings.ai.manage" }),
        ("/api/aicore/skills/builtins", new[] { "settings.ai.manage" }),
        ("/api/aicore/skills/documents/logo/00000000000000000000000000000000.png", new[] { "settings.ai.manage" }),
        ("/api/aicore/skills/documents/letterhead/00000000000000000000000000000000.pdf", new[] { "settings.ai.manage" }),
        ("/api/aicore/booking-hours", new[] { "integrations.view", "settings.ai.manage" }),
        // Analytics (home)
        ("/api/analytics/summary", new[] { "analytics.view" }),
        ("/api/analytics/reports", new[] { "analytics.view" }),
        ("/api/analytics/reports.csv?report=agent", new[] { "analytics.view" }),
        ("/api/analytics/flows", new[] { "flows.view" }),
        ("/api/messaging/engagement", new[] { "analytics.view" }),
        ("/api/whatsapp/channels/health", new[] { "analytics.view", "channels.view" }),
        ("/api/analytics/usage", new[] { "billing.manage" }),
        ("/api/billing/ai", new[] { "billing.view" }),
        ("/api/billing/ai/pdf", new[] { "billing.view" }),
        ("/api/billing/ai/settings", new[] { "billing.manage" }),
        ("/api/billing/ai/quotas", new[] { "billing.view", "billing.manage" }),
        // Activity
        ("/api/audit", new[] { "activity.view" }),
        ("/api/audit.csv", new[] { "activity.view" }),
        ("/api/audit/facets", new[] { "activity.view" }),
        ("/api/auth/audit", new[] { "users.view", "users.manage" }),
        // Chats + contacts
        ("/api/inbox/threads", new[] { "chats.view" }),
        ("/api/whatsapp/threads", new[] { "chats.view" }),
        ("/api/messaging/threads", new[] { "chats.view" }),
        ("/api/messaging/agents", new[] { "chats.view" }),
        ("/api/messaging/canned", new[] { "chats.view", "chats.send", "templates.manage" }),
        ("/api/messaging/contacts", new[] { "chats.view", "contacts.view" }),
        ("/api/messaging/contacts/{c}/conversation", new[] { "contacts.view", "chats.view" }),
        ("/api/messaging/timeline?phone=60100000777", new[] { "contacts.view", "chats.view" }),
        ("/api/whatsapp/media/999999", new[] { "chats.view" }),
        ("/api/messaging/media/999999", new[] { "chats.view" }),
        ("/api/messaging/messages/999999/email", new[] { "chats.view" }),
        ("/api/bookings", new[] { "chats.view" }),
        ("/api/bookings/calendars", new[] { "chats.view" }),
        ("/api/bookings/calendar-events", new[] { "chats.view" }),
        ("/api/documents", new[] { "chats.view" }),
        ("/api/documents/999999/file", new[] { "chats.view" }),
        ("/api/documents/form?channel=whatsapp&key=none", new[] { "chats.send" }),
        ("/api/documents/preview/00000000000000000000000000000000.pdf", new[] { "ai.monitor", "ai.behaviour.manage", "settings.ai.manage" }),
        ("/api/contacts", new[] { "contacts.view", "chats.view", "users.manage" }),
        ("/api/contacts/{c}", new[] { "contacts.view" }),
        ("/api/contacts/{c}/channels", new[] { "contacts.view", "chats.view" }),
        ("/api/contacts/{c}/calls", new[] { "contacts.view", "chats.view" }),
        ("/api/contacts/{c}/ai-mode", new[] { "contacts.view" }),
        ("/api/contacts/{c}/invites", new[] { "contacts.view" }),
        ("/api/contacts/access-requests", new[] { "contacts.manage", "chats.send" }),
        ("/api/v1/contacts", new[] { "contacts.view", "flows.manage" }),
        ("/api/v1/contacts/{c}", new[] { "contacts.view" }),
        ("/api/v1/contacts/{c}/entities", new[] { "contacts.view" }),
        ("/api/settings/custom-fields", new[]
            { "contacts.view", "flows.manage", "templates.manage", "script_templates.manage", "settings.custom_fields.manage" }),
        // Calls
        ("/api/call-logs", new[] { "call_logs.view" }),
        ("/api/calls/gate-room", new[] { "chats.view", "call_logs.view" }),
        ("/api/calls/gate-room/live", new[] { "chats.view", "call_logs.view" }),
        ("/api/calls/recording/gate-call", new[] { "call_logs.view" }),
        ("/api/whatsapp/calls/recording/nope.wav", new[] { "chats.view", "call_logs.view" }),
        ("/api/calls/script-templates", new[] { "script_templates.manage", "chats.send", "flows.manage" }),
        // Templates
        ("/api/whatsapp/templates", new[] { "chats.send", "templates.manage", "flows.manage" }),
        // Flows + automations
        ("/api/flows/library", new[] { "flows.view" }),
        ("/api/flows/runs", new[] { "flows.view" }),
        ("/api/flows/runs/999999", new[] { "flows.view" }),
        ("/api/flows/wf-nope/stats", new[] { "flows.view" }),
        ("/api/flows/queue", new[] { "flows.manage" }),
        ("/api/automations", new[] { "flows.manage" }),
        ("/api/automations/catalog", new[] { "flows.manage" }),
        // Tags + bindings
        ("/api/tags", new[] { "tags.view" }),
        ("/api/tags/{t}/targets", new[] { "tags.view" }),
        ("/api/tags/assignments", new[] { "tags.view" }),
        ("/api/contacts/{c}/tags", new[] { "tags.view" }),
        ("/api/entities/main/tags", new[] { "tags.view" }),
        ("/api/bindings/catalog", new[] { "users.view", "tags.view" }),
        ("/api/bindings/contact-entities?target={c}", new[] { "users.view", "contacts.view" }),
        ("/api/bindings/tag-contacts?target={t}", new[] { "users.view", "tags.view" }),
        ("/api/bindings/user-entities?target=1", new[] { "users.view" }),
        // Admin: users, teams, entities, roles
        ("/api/v1/users", new[] { "users.manage" }),
        ("/api/teams", new[] { "users.view", "chats.view", "flows.manage", "channels.view" }),
        ("/api/v1/brands", new[] { "entities.view" }),
        ("/api/auth/roles", new[] { "users.view", "users.manage", "rbac.manage" }),
        ("/api/auth/permissions", new[] { "users.view", "users.manage", "rbac.manage" }),
        // Channels + assignment
        ("/api/whatsapp/accounts", new[] { "chats.view", "channels.view", "templates.manage" }),
        ("/api/whatsapp/accounts/999999/baileys/status", new[] { "channels.view" }),
        ("/api/messaging/accounts", new[] { "channels.view" }),
        ("/api/messaging/settings", new[] { "channels.view" }),
        ("/api/whatsapp/settings", new[] { "channels.view", "app_keys.manage" }),
        ("/api/whatsapp/voice-settings", new[] { "channels.view" }),
        ("/api/widgets", new[] { "channels.view" }),
        ("/api/inboxes/999999/identify", new[] { "channels.view" }),
        // the marketplace bridge (Shopee / Lazada / TikTok Shop / Shopify shops)
        ("/api/commerce/status", new[] { "channels.view" }),
        ("/api/whatsapp/assignments", new[] { "account_assignment.manage" }),
        ("/api/whatsapp/routing-rules", new[] { "account_assignment.manage" }),
        ("/api/whatsapp/recovery", new[] { "account_assignment.manage", "analytics.view" }),
        // Integrations, developers, settings
        ("/api/integrations", new[] { "integrations.view" }),
        ("/api/integrations/providers", new[] { "integrations.view" }),
        ("/api/integrations/settings", new[] { "integrations.view", "app_keys.manage" }),
        ("/api/integrations/mcp", new[] { "api_keys.manage" }),
        ("/api/developers/mcp", new[] { "api_keys.manage" }),
        ("/api/auth/notification-prefs", new[] { "notifications.manage" }),
        ("/api/settings/ai-providers", new[] { "settings.ai_keys.manage" }),
        ("/api/settings?keys=quiet_start", new[] { "settings.raw.view" }),
        ("/api/settings/security", new[] { "security.manage" }),
        ("/api/settings/system-email", new[] { "app_keys.manage" }),
        // Test lab (testlab.run — superadmin only by default)
        ("/api/testlab/scenarios", new[] { "testlab.run" }),
        ("/api/testlab/status", new[] { "testlab.run" }),
        ("/api/testlab/runs", new[] { "testlab.run" }),
        ("/api/testlab/runs/999999", new[] { "testlab.run" }),
    };

    /// <summary>What a signed-in person with NO capability still reads: the shell and
    /// their own things (the pages they can open — Notifications, personal Settings).</summary>
    private static readonly string[] EveryoneReads =
    {
        "/api/auth/whoami", "/api/v1/whoami", "/api/auth/preferences", "/api/auth/notifications",
        "/api/notifications", "/api/notifications/preferences", "/api/views?scope=chats",
        "/api/settings/appearance", "/api/v1/entities", "/api/live", "/api/search?q=gate",
        "/api/developers/keys", "/api/integrations/mine", "/api/integrations/accounts",
        "/api/assistant/conversations", "/api/auth/me/account", "/api/auth/me/2fa",
    };

    private string Url(string template) =>
        template.Replace("{c}", _contactId.ToString()).Replace("{t}", _tagId.ToString());

    private static bool Refused(HttpStatusCode s) => s is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    private static bool Through(HttpStatusCode s) => !Refused(s) && (int)s < 500;

    private async Task<List<string>> Check(HttpClient c, IEnumerable<string> urls, Func<HttpStatusCode, bool> ok,
        string expected)
    {
        var bad = new List<string>();
        foreach (var url in urls)
        {
            var res = await c.GetAsync(Url(url));
            if (!ok(res.StatusCode)) bad.Add($"{url} → {(int)res.StatusCode}, expected {expected}");
        }
        return bad;
    }

    [Fact]
    public async Task A_role_with_no_capabilities_is_refused_every_gated_read()
    {
        var none = await SettingsTestKit.CustomRoleClient(_f, "Gates none");
        var bad = await Check(none, Gates.Select(g => g.Url), s => s == HttpStatusCode.Forbidden, "403");
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public async Task A_role_with_no_capabilities_still_reads_the_shell_and_its_own_things()
    {
        var none = await SettingsTestKit.CustomRoleClient(_f, "Gates shell");
        var bad = await Check(none, EveryoneReads, s => (int)s is >= 200 and < 300, "2xx");
        Assert.True(bad.Count == 0, string.Join("\n", bad));

        // …and those answers carry nothing gated: no conversations in the Live chip,
        // no capabilities in whoami, no search hits from pages it can't open
        var live = await none.GetFromJsonAsync<JsonElement>("/api/live");
        Assert.Equal(0, live.GetProperty("waiting_count").GetInt32());
        var who = await none.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        Assert.Equal(0, who.GetProperty("capabilities").GetArrayLength());
        var search = await none.GetFromJsonAsync<JsonElement>("/api/search?q=Gate");
        Assert.DoesNotContain("Gate Keeper", search.GetRawText());
    }

    [Fact]
    public async Task Each_gated_read_opens_for_a_role_holding_any_one_of_its_capabilities()
    {
        var clients = new Dictionary<string, HttpClient>();
        var bad = new List<string>();
        foreach (var cap in Gates.SelectMany(g => g.Caps).Distinct())
            clients[cap] = await SettingsTestKit.CustomRoleClient(_f, "Gates only " + cap, cap);
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");

        foreach (var (url, caps) in Gates)
        {
            Assert.True(caps.Length > 0, $"{url}: every read is gated by a capability, never a role");
            foreach (var cap in caps)
                bad.AddRange((await Check(clients[cap], new[] { url }, Through, "through"))
                    .Select(b => $"[{cap}] {b}"));
            // the default admin keeps every read it had: refused only where every capability
            // that opens it is one admins don't hold by default (superadmin's own)
            var adminHolds = caps.Any(c => !AdminExcluded.Contains(c));
            bad.AddRange((await Check(admin, new[] { url }, adminHolds ? Through : s => s == HttpStatusCode.Forbidden,
                adminHolds ? "admin to get through" : "403 for admin")).Select(b => $"[admin] {b}"));
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    /// <summary>The capabilities the default admin does NOT hold (AdminEndpoints.AdminExclude).</summary>
    private static readonly HashSet<string> AdminExcluded = new()
    {
        "rbac.manage", "app_keys.manage", "settings.ai_keys.manage", "billing.manage",
        "settings.ai_hard_rules.manage", "settings.raw.manage", "testlab.run", "security.manage",
    };

    [Fact]
    public async Task The_default_agent_keeps_every_read_its_pages_use_and_no_other()
    {
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        var bad = new List<string>();
        foreach (var (url, caps) in Gates)
        {
            var allowed = caps.Any(AgentCaps.Contains);
            bad.AddRange(await Check(agent, new[] { url },
                allowed ? Through : s => s == HttpStatusCode.Forbidden, allowed ? "through" : "403"));
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));

        // the inbox's sender picker gets names, never the account's ids or masked credentials
        var accounts = (await agent.GetFromJsonAsync<JsonElement>("/api/whatsapp/accounts?channel=all")).GetRawText();
        Assert.DoesNotContain("access_token_masked", accounts);
        Assert.DoesNotContain("phone_number_id", accounts);
        var admin = await SettingsTestKit.Client(_f, "admin", "admin123");
        Assert.Contains("access_token_masked",
            (await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/accounts?channel=all")).GetRawText());

        // the Tags page's binding view (tags.view) gets what a tag binds to — not the user list
        var pools = (await agent.GetFromJsonAsync<JsonElement>("/api/bindings/catalog")).GetProperty("pools");
        Assert.Equal(0, pools.GetProperty("users").GetArrayLength());
        Assert.Contains(pools.GetProperty("contacts").EnumerateArray(), c => c.GetProperty("id").GetInt32() == _contactId);
        // …and a binding kind beyond its pages stays closed
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/bindings/user-entities?target=1")).StatusCode);
    }

    // ---- the route table --------------------------------------------------------

    /// <summary>
    /// Signed-in GETs that carry no capability gate ON PURPOSE — each with the reason.
    /// A new entry needs a reason a reviewer agrees with; the default is a capability.
    /// </summary>
    private static readonly Dictionary<string, string> AnySignedInUser = new()
    {
        ["/api/auth/whoami"] = "the caller's own identity and capability list — the shell's first read",
        ["/api/v1/whoami"] = "the same, v1 shape",
        ["/api/auth/preferences"] = "the caller's own appearance preferences",
        ["/api/auth/notifications"] = "the caller's own bell",
        ["/api/notifications"] = "the caller's own notification inbox (/notifications is everyone's page)",
        ["/api/notifications/preferences"] = "the caller's own notification switches (Settings → My notifications)",
        ["/api/views"] = "the caller's own saved inbox views",
        ["/api/settings/appearance"] = "the organization's default theme, applied by the shell for everyone",
        ["/api/v1/entities"] = "the entity switcher + every EntityPicker; rows bounded to the caller's accessible entities",
        ["/api/live"] = "the top bar's Live chip on every page; conversations need chats.view inside, audit rows admin+",
        ["/api/search"] = "the command palette; each result type is gated inside by the capability of the page it opens",
        ["/api/whatsapp/stream"] = "the SSE change token (a timestamp + a count) — no conversation data",
        ["/api/developers/keys"] = "the caller's own API keys (never the secret); ?scope=tenant needs api_keys.manage inside",
        ["/api/integrations/mine"] = "Settings → My connections: the caller's own Google grants",
        ["/api/integrations/accounts"] = "without integrations.view, only the caller's own connections (checked inside)",
        ["/api/integrations/oauth/{provider}/start"] = "connect your OWN Google account; a shared row needs integrations.manage (inside the caller's entities) inside",
        ["/api/assistant/conversations"] = "the caller's own Ask ChatBranch history (the dock is on every page; owner-only rows)",
        ["/api/assistant/conversations/{id:int}"] = "one of the caller's own Ask ChatBranch conversations (someone else's answers 404)",
        ["/api/auth/me/account"] = "Settings → My account: the caller's own name, email, role, entities and session count",
        ["/api/auth/me/2fa"] = "Settings → My account: the caller's own two-step sign-in status (never a secret)",
        ["/api/push/config"] = "whether this server can alert a device, and the VAPID public key the browser subscribes with (a public key, by design)",
        ["/api/push/devices"] = "the caller's own devices (Settings → My notifications); another login's are never listed",
    };

    /// <summary>Gated, but inside the handler (no filter metadata) — each must also be in
    /// <see cref="Gates"/> so the HTTP legs above prove the gate is really there.</summary>
    private static readonly Dictionary<string, string> GatedInHandler = new()
    {
        ["/api/v1/users"] = "users.manage — checked in the handler to keep the v1 error envelope",
    };

    /// <summary>The anonymous GETs — docs/API_CONVENTIONS.md "Anonymous endpoints".</summary>
    private static readonly HashSet<string> Anonymous = new()
    {
        "/api/whatsapp/status", "/api/messaging/status",
        "/api/whatsapp/webhook", "/api/messaging/webhook", "/api/messaging/webhook/wechat/{id:int}",
        "/api/integrations/oauth/google/callback",
        "/api/widget/{token}/config", "/api/widget/{token}/messages",
        "/api/help/articles", "/api/help/articles/{slug}",
        // a customer's document download link (the 128-bit token is the credential)
        "/api/documents/public/{token}",
        // a provider (Twilio, YCloud) fetching an outbound attachment: the signed, expiring token is the credential
        "/api/media/public/{token}",
        // the AI bill PDF for the installed iOS app: a 5-minute HMAC-signed link bound to one person
        "/api/billing/ai/pdf/s/{token}",
        // a conversation's attachment for <audio>/<video>/download: an HMAC-signed link bound to one person + message
        "/api/media/s/{token}",
        "/api/pronunciation", "/api/voices", "/api/voice/stt-settings", "/api/voice/llm-settings",
        // /mcp and /widget.js under /api/ for /api/-only proxies (GET /api/mcp is a 405;
        // the MCP POST demands an API key inside the handler)
        "/api/mcp", "/api/widget.js",
        // the welcome email's set-password link: the 256-bit one-time token is the credential (stored as a hash)
        "/api/auth/set-password/{token}",
    };

    private static readonly HashSet<string> RolePolicies = new() { AuthPolicies.AdminPlus, AuthPolicies.SuperAdmin };

    private List<(string Path, EndpointMetadataCollection Meta)> ApiGets() =>
        _f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Select(e => (Path: e.RoutePattern.RawText ?? "", Meta: e.Metadata))
            .Where(e => e.Path.StartsWith("/api/", StringComparison.Ordinal))
            .ToList();

    private static bool Gated(EndpointMetadataCollection m) =>
        m.GetMetadata<RequiredCapabilities>() != null
        || m.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy != null && RolePolicies.Contains(a.Policy));

    [Fact]
    public void Every_signed_in_GET_carries_a_capability_gate_or_a_documented_exemption()
    {
        var gets = ApiGets();
        Assert.True(gets.Count > 100, $"only {gets.Count} GET routes found — is the route table loaded?");

        var ungated = gets
            .Where(e => e.Meta.GetMetadata<IAllowAnonymous>() == null)
            .Where(e => !Gated(e.Meta) && !AnySignedInUser.ContainsKey(e.Path) && !GatedInHandler.ContainsKey(e.Path))
            .Select(e => e.Path).Distinct().OrderBy(p => p).ToList();
        Assert.True(ungated.Count == 0,
            "These GETs hand data to ANY signed-in user. Gate each with .RequireCapability(…) / "
            + ".RequireAnyCapability(…) (or a role policy), or — only if every signed-in person "
            + "really may read it — add it to AnySignedInUser with the reason:\n" + string.Join("\n", ungated));

        // the exemption list stays honest: every entry is a real, otherwise-ungated GET
        foreach (var path in AnySignedInUser.Keys)
        {
            var hit = gets.Where(e => e.Path == path).ToList();
            Assert.True(hit.Count > 0, $"AnySignedInUser lists {path}, which is not a GET route any more");
            Assert.True(hit.All(e => !Gated(e.Meta) && e.Meta.GetMetadata<IAllowAnonymous>() == null),
                $"{path} is gated (or anonymous) now — drop it from AnySignedInUser");
        }

        foreach (var path in GatedInHandler.Keys)
            Assert.True(Gates.Any(g => g.Url.Split('?')[0] == path), $"{path} is gated in its handler but missing from Gates");

        // and nothing new turned anonymous without the docs saying so
        var anonymous = gets.Where(e => e.Meta.GetMetadata<IAllowAnonymous>() != null)
            .Select(e => e.Path).Distinct().Where(p => !Anonymous.Contains(p)).OrderBy(p => p).ToList();
        Assert.True(anonymous.Count == 0,
            "Anonymous GETs missing from docs/API_CONVENTIONS.md's list:\n" + string.Join("\n", anonymous));
    }

    [Fact]
    public async Task Every_gate_in_the_table_names_a_capability_from_the_catalog()
    {
        // a typo'd capability would be a gate nobody but superadmin can ever pass
        var super = await SettingsTestKit.Client(_f, "superadmin", "super123");
        var catalog = (await super.GetFromJsonAsync<JsonElement>("/api/auth/whoami")).GetProperty("capabilities")
            .EnumerateArray().Select(c => c.GetString() ?? "").ToHashSet(); // superadmin holds the whole catalog
        var unknown = ApiGets().Select(e => e.Meta.GetMetadata<RequiredCapabilities>()).Where(r => r != null)
            .SelectMany(r => r!.Any).Concat(Gates.SelectMany(g => g.Caps))
            .Where(c => !catalog.Contains(c)).Distinct().ToList();
        Assert.True(unknown.Count == 0, "Unknown capabilities: " + string.Join(", ", unknown));
    }
}
