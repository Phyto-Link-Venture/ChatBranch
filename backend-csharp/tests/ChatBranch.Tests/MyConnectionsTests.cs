using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Settings → Personal → My connections: any signed-in person (no capability)
/// lists their own Google grants, starts a connect that comes back to the page,
/// and disconnects their own — never anyone else's.
/// </summary>
public class MyConnectionsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public MyConnectionsTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass, bool redirects = true)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = redirects });
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

    private static JsonElement Product(JsonElement mine, string provider) =>
        mine.GetProperty("connections").EnumerateArray().Single(p => p.GetProperty("provider").GetString() == provider);

    private static string StateOf(string authorizeUrl)
    {
        var query = new Uri(authorizeUrl).Query.TrimStart('?').Split('&')
            .Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));
        return query["state"];
    }

    [Fact]
    public async Task A_callback_loaded_twice_shows_connected_not_an_error()
    {
        var super = await Authed("superadmin", "super123");
        (await super.PutAsJsonAsync("/api/integrations/settings",
            new { google_client_id = "cid.apps.googleusercontent.com", google_client_secret = "shh" })).EnsureSuccessStatusCode();
        try
        {
            var agent = await Authed("agent", "agent123");
            var start = await agent.GetFromJsonAsync<JsonElement>("/api/integrations/oauth/google_sheets/start?return=connections");
            var state = StateOf(start.GetProperty("authorize_url").GetString()!);
            var id = InScope((sp, _) => sp.GetRequiredService<GoogleOAuth>().VerifyState(state)).IntegrationId;

            // the first load exchanged the code: verifier gone, token stored, healthy
            void Connected(DateTime at) => InScope((_, db) =>
            {
                var row = db.Integrations.Single(i => i.Id == id);
                row.Credentials = new Dictionary<string, object?> { ["access_token"] = "tok", ["refresh_token"] = "ref" };
                row.Status = "healthy";
                row.UpdatedAt = at;
                return db.SaveChanges();
            });
            Connected(DateTime.UtcNow);
            var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var url = $"/api/integrations/oauth/google/callback?code=same-code&state={Uri.EscapeDataString(state)}";
            var twin = await anon.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, twin.StatusCode);
            Assert.Contains("/settings/connections?connected=", twin.Headers.Location!.ToString());

            // long after the connect, a missing verifier is still an error
            Connected(DateTime.UtcNow.AddMinutes(-10));
            var stale = await anon.GetAsync(url);
            Assert.Contains("?error=", stale.Headers.Location!.ToString());
            Assert.Contains("PKCE", Uri.UnescapeDataString(stale.Headers.Location!.ToString()));
        }
        finally
        {
            (await super.PutAsJsonAsync("/api/integrations/settings",
                new { google_client_id = "", google_client_secret = "" })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task An_agent_lists_connects_and_disconnects_their_own_google_grants()
    {
        var agent = await Authed("agent", "agent123");
        var mine = await agent.GetFromJsonAsync<JsonElement>("/api/integrations/mine");
        var products = mine.GetProperty("connections").EnumerateArray().Select(p => p.GetProperty("provider").GetString()).ToList();
        Assert.Equal("google_calendar", products[0]);   // the personal grants that matter most first
        Assert.Equal("google_contacts", products[1]);
        Assert.DoesNotContain("shopify", products);     // API-key providers are shared, never personal
        Assert.Contains("assigned agent", Product(mine, "google_calendar").GetProperty("used_for").GetString());

        // the Google client is registered once by a super admin …
        var super = await Authed("superadmin", "super123");
        (await super.PutAsJsonAsync("/api/integrations/settings",
            new { google_client_id = "cid.apps.googleusercontent.com", google_client_secret = "shh" })).EnsureSuccessStatusCode();
        Assert.True((await agent.GetFromJsonAsync<JsonElement>("/api/integrations/mine")).GetProperty("google_configured").GetBoolean());

        // … then the agent connects their own calendar and lands back on the page
        var start = await agent.GetFromJsonAsync<JsonElement>("/api/integrations/oauth/google_calendar/start?return=connections");
        Assert.True(start.GetProperty("configured").GetBoolean());
        var state = StateOf(start.GetProperty("authorize_url").GetString()!);
        var data = InScope((sp, _) => sp.GetRequiredService<GoogleOAuth>().VerifyState(state));
        Assert.Equal("connections", data.ReturnTo);
        var noRedirect = await Authed("agent", "agent123", redirects: false);
        var refused = await noRedirect.GetAsync(
            $"/api/integrations/oauth/google/callback?error=access_denied&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.EndsWith("/settings/connections?error=access_denied", refused.Headers.Location!.ToString());

        // an unknown return key falls back to the Integrations page
        var other = await agent.GetFromJsonAsync<JsonElement>("/api/integrations/oauth/google_calendar/start?return=https://evil.example");
        Assert.Equal("", InScope((sp, _) => sp.GetRequiredService<GoogleOAuth>()
            .VerifyState(StateOf(other.GetProperty("authorize_url").GetString()!)).ReturnTo));

        // the grant lands on the agent's own row (simulated: Google is not called in tests)
        var rowId = InScope((_, db) =>
        {
            var row = db.Integrations.Single(i => i.Id == data.IntegrationId);
            row.Credentials = new Dictionary<string, object?> { ["access_token"] = "tok", ["refresh_token"] = "ref" };
            row.Status = "healthy";
            db.SaveChanges();
            return row.Id;
        });
        var after = Product(await agent.GetFromJsonAsync<JsonElement>("/api/integrations/mine"), "google_calendar");
        Assert.True(after.GetProperty("account").GetProperty("connected").GetBoolean());
        Assert.Equal("agent", after.GetProperty("account").GetProperty("owner").GetString());

        // nobody else sees it as theirs
        var admin = await Authed("admin", "admin123");
        var adminMine = Product(await admin.GetFromJsonAsync<JsonElement>("/api/integrations/mine"), "google_calendar");
        Assert.Equal(JsonValueKind.Null, adminMine.GetProperty("account").ValueKind);

        // the agent may disconnect their own grant (no integrations.manage needed)
        (await agent.PostAsync($"/api/integrations/accounts/{rowId}/disconnect", null)).EnsureSuccessStatusCode();
        var gone = Product(await agent.GetFromJsonAsync<JsonElement>("/api/integrations/mine"), "google_calendar");
        Assert.False(gone.GetProperty("account").GetProperty("connected").GetBoolean());

        // unregister the client again so the unconfigured case below holds in any order
        (await super.PutAsJsonAsync("/api/integrations/settings",
            new { google_client_id = "", google_client_secret = "" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Without_a_google_client_the_page_says_so_instead_of_failing()
    {
        var agent = await Authed("agent", "agent123");
        var start = await agent.GetFromJsonAsync<JsonElement>("/api/integrations/oauth/google_contacts/start?return=connections");
        if (start.GetProperty("configured").GetBoolean()) return; // the test above holds a client right now
        Assert.Contains("not configured", start.GetProperty("reason").GetString());
    }
}
