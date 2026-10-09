using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// How far a connection reaches. My connections = personal (its owner only);
/// the Integrations page = SHARED, for the whole organization or one entity and
/// its branches. Resolution walks own → entity → parent entities → organization:
/// a branch inherits head office's connection, head office never borrows a
/// branch's, and a sibling branch never sees another's.
/// </summary>
public class IntegrationScopeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private static readonly object Gate = new();

    public IntegrationScopeTests(ApiFactory factory)
    {
        _factory = factory;
        _ = _factory.CreateClient(); // boot + seed
        lock (Gate) Arrange();
    }

    private void Arrange()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Entities.Any(e => e.Slug == "is-hq")) return;
        var tenant = db.Tenants.First(t => t.Slug == "demo");
        var now = DateTime.UtcNow;
        var hq = new Entity { TenantId = tenant.Id, Name = "IS Head office", Slug = "is-hq", CreatedAt = now, UpdatedAt = now };
        db.Entities.Add(hq);
        db.SaveChanges();
        var north = new Entity { TenantId = tenant.Id, ParentId = hq.Id, Name = "IS North", Slug = "is-north", CreatedAt = now, UpdatedAt = now };
        var south = new Entity { TenantId = tenant.Id, ParentId = hq.Id, Name = "IS South", Slug = "is-south", CreatedAt = now, UpdatedAt = now };
        db.Entities.AddRange(north, south);
        db.SaveChanges();
        var kl = new Entity { TenantId = tenant.Id, ParentId = north.Id, Name = "IS North KL", Slug = "is-north-kl", CreatedAt = now, UpdatedAt = now };
        db.Entities.Add(kl);
        db.SaveChanges();
    }

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private T InScope<T>(Func<IServiceProvider, AppDbContext, T> f)
    {
        using var scope = _factory.Services.CreateScope();
        return f(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private int EntityIdOf(string slug) => InScope((_, db) => db.Entities.Single(e => e.Slug == slug).Id);
    private int UserIdOf(string username) => InScope((_, db) => db.Users.Single(u => u.Username == username).Id);

    private static string StateOf(string authorizeUrl)
    {
        var query = new Uri(authorizeUrl).Query.TrimStart('?').Split('&')
            .Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));
        return query["state"];
    }

    /// <summary>A connected row for <paramref name="provider"/> (the grant is simulated — Google is not called).</summary>
    private int Grant(string provider, string token, int? entityId, int? ownerUserId)
    {
        return InScope((_, db) =>
        {
            var row = new Integration
            {
                Provider = provider, EntityId = entityId, OwnerUserId = ownerUserId, Status = "healthy",
                Credentials = new Dictionary<string, object?> { ["access_token"] = token },
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Integrations.Add(row);
            db.SaveChanges();
            return row.Id;
        });
    }

    private string? Resolve(string provider, string? entitySlug, int? userId) => InScope((sp, _) =>
    {
        int? entityId = entitySlug == null ? null : EntityIdOf(entitySlug);
        var row = sp.GetRequiredService<IntegrationResolver>().Active(provider, entityId, userId);
        return row?.Credentials.GetValueOrDefault("access_token")?.ToString();
    });

    [Fact]
    public async Task A_shared_connect_writes_an_ownerless_row_for_the_organization_or_one_entity()
    {
        var super = await Authed("superadmin", "super123");
        (await super.PutAsJsonAsync("/api/integrations/settings",
            new { google_client_id = "cid.apps.googleusercontent.com", google_client_secret = "shh" })).EnsureSuccessStatusCode();
        try
        {
            var admin = await Authed("admin", "admin123");
            var org = await admin.GetFromJsonAsync<JsonElement>("/api/integrations/oauth/google_drive/start?for=shared");
            Assert.True(org.GetProperty("configured").GetBoolean());
            var orgRowId = InScope((sp, _) => sp.GetRequiredService<GoogleOAuth>()
                .VerifyState(StateOf(org.GetProperty("authorize_url").GetString()!)).IntegrationId);
            var branch = await admin.GetFromJsonAsync<JsonElement>("/api/integrations/oauth/google_drive/start?for=shared&entity=is-north");
            var branchRowId = InScope((sp, _) => sp.GetRequiredService<GoogleOAuth>()
                .VerifyState(StateOf(branch.GetProperty("authorize_url").GetString()!)).IntegrationId);
            var personal = await admin.GetFromJsonAsync<JsonElement>("/api/integrations/oauth/google_drive/start");
            var personalRowId = InScope((sp, _) => sp.GetRequiredService<GoogleOAuth>()
                .VerifyState(StateOf(personal.GetProperty("authorize_url").GetString()!)).IntegrationId);

            var (orgRow, branchRow, personalRow) = InScope((_, db) => (
                db.Integrations.Single(i => i.Id == orgRowId),
                db.Integrations.Single(i => i.Id == branchRowId),
                db.Integrations.Single(i => i.Id == personalRowId)));
            Assert.Null(orgRow.OwnerUserId);
            Assert.Null(orgRow.EntityId);
            Assert.Null(branchRow.OwnerUserId);
            Assert.Equal(EntityIdOf("is-north"), branchRow.EntityId);
            Assert.Equal(UserIdOf("admin"), personalRow.OwnerUserId);

            // starting the same shared connect again reuses the row (reconnect = one click)
            var again = await admin.GetFromJsonAsync<JsonElement>("/api/integrations/oauth/google_drive/start?for=shared&entity=is-north");
            Assert.Equal(branchRowId, InScope((sp, _) => sp.GetRequiredService<GoogleOAuth>()
                .VerifyState(StateOf(again.GetProperty("authorize_url").GetString()!)).IntegrationId));

            // the list says how far each one reaches
            var accounts = (await admin.GetFromJsonAsync<JsonElement>("/api/integrations/accounts?provider=google_drive"))
                .GetProperty("accounts").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetInt32());
            Assert.Equal("organization", accounts[orgRowId].GetProperty("scope").GetString());
            Assert.Equal("entity", accounts[branchRowId].GetProperty("scope").GetString());
            Assert.Equal("IS North", accounts[branchRowId].GetProperty("entity_name").GetString());
            Assert.Equal("is-north", accounts[branchRowId].GetProperty("entity_slug").GetString());
            Assert.Equal("personal", accounts[personalRowId].GetProperty("scope").GetString());

            // an unknown entity is refused rather than silently made organization-wide
            Assert.Equal(HttpStatusCode.NotFound,
                (await admin.GetAsync("/api/integrations/oauth/google_drive/start?for=shared&entity=no-such-outlet")).StatusCode);

            // only an admin with integrations.manage connects a shared account; anyone connects their own
            var agent = await Authed("agent", "agent123");
            Assert.Equal(HttpStatusCode.Forbidden,
                (await agent.GetAsync("/api/integrations/oauth/google_drive/start?for=shared")).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await agent.GetAsync("/api/integrations/oauth/google_drive/start")).StatusCode);
        }
        finally
        {
            (await super.PutAsJsonAsync("/api/integrations/settings",
                new { google_client_id = "", google_client_secret = "" })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task An_api_key_account_can_be_shared_with_one_entity()
    {
        var admin = await Authed("admin", "admin123");
        var created = await admin.PostAsJsonAsync("/api/integrations/accounts",
            new { provider = "stripe", name = "South payments", entity = "is-south", api_key = "sk_test_x" });
        created.EnsureSuccessStatusCode();
        var row = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entity", row.GetProperty("scope").GetString());
        Assert.Equal("IS South", row.GetProperty("entity_name").GetString());

        var org = await (await admin.PostAsJsonAsync("/api/integrations/accounts",
            new { provider = "shopify", name = "Store", api_key = "shpat_x" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("organization", org.GetProperty("scope").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/integrations/accounts",
            new { provider = "stripe", entity = "no-such-outlet", api_key = "sk_test_y" })).StatusCode);
    }

    [Fact]
    public void Resolution_walks_up_the_tree_never_down_or_sideways()
    {
        const string p = "google_sheets";
        var agentId = UserIdOf("agent");

        // nothing connected → nothing resolves
        Assert.Null(Resolve(p, "is-north-kl", null));

        // a branch's own connection serves the branch and its sub-branches only
        Grant(p, "north", EntityIdOf("is-north"), null);
        Assert.Equal("north", Resolve(p, "is-north", null));
        Assert.Equal("north", Resolve(p, "is-north-kl", null));
        Assert.Null(Resolve(p, "is-hq", null));       // head office never borrows a branch's
        Assert.Null(Resolve(p, "is-south", null));    // nor does a sibling
        Assert.Null(Resolve(p, null, null));          // nor the organization

        // head office's connection reaches every branch that has none of its own
        Grant(p, "hq", EntityIdOf("is-hq"), null);
        Assert.Equal("hq", Resolve(p, "is-south", null));
        Assert.Equal("north", Resolve(p, "is-north-kl", null)); // the nearest level wins

        // the organization-wide one is the last fallback
        Grant(p, "org", null, null);
        Assert.Equal("org", Resolve(p, null, null));
        Assert.Equal("hq", Resolve(p, "is-south", null));

        // a person's own grant beats every shared one, for them only
        Grant(p, "agent-own", null, agentId);
        Assert.Equal("agent-own", Resolve(p, "is-north-kl", agentId));
        Assert.Equal("north", Resolve(p, "is-north-kl", UserIdOf("admin")));
    }

    [Fact]
    public void The_ancestor_chain_climbs_from_the_entity_to_the_top()
    {
        var chain = InScope((sp, _) => sp.GetRequiredService<IntegrationResolver>().AncestorChain(EntityIdOf("is-north-kl")));
        Assert.Equal(new[] { EntityIdOf("is-north-kl"), EntityIdOf("is-north"), EntityIdOf("is-hq") }, chain);
    }
}
