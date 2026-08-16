using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// End-to-end HTTP tests that boot the real ASP.NET Core host: status probe,
/// the login → whoami flow against seeded demo users, and the superadmin gate
/// on settings writes. Each factory gets its own throwaway SQLite file.
/// </summary>
public class ApiSmokeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ApiSmokeTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Status_is_open_and_reports_the_csharp_stack()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/api/whatsapp/status");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("asp.net-core", body.GetProperty("stack").GetString());
    }

    [Fact]
    public async Task Login_with_seeded_superadmin_then_whoami()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "superadmin", password = "super123" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var who = await client.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        Assert.Equal("superadmin", who.GetProperty("username").GetString());
        Assert.Equal("superadmin", who.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Bad_password_is_rejected()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Settings_write_is_superadmin_gated_and_scoping_holds()
    {
        var client = _factory.CreateClient();

        // agent cannot write a global setting
        var agentToken = await Login(client, "agent", "agent123");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);
        var denied = await client.PutAsJsonAsync("/api/settings",
            new { key = "quiet_start", value = "22:00" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // superadmin can, and the value reads back
        var superToken = await Login(client, "superadmin", "super123");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", superToken);
        var ok = await client.PutAsJsonAsync("/api/settings",
            new { key = "quiet_start", value = "22:00" });
        ok.EnsureSuccessStatusCode();

        var read = await client.GetFromJsonAsync<JsonElement>("/api/settings?keys=quiet_start");
        Assert.Equal("22:00", read.GetProperty("values").GetProperty("quiet_start").GetString());

        // an organization-wide key refuses a per-entity override → 400
        var refused = await client.PutAsJsonAsync("/api/settings",
            new { key = "openai_api_key", value = "sk", entity = "main" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    private static async Task<string> Login(HttpClient client, string user, string pass)
    {
        client.DefaultRequestHeaders.Authorization = null;
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }
}

/// <summary>Boots Program with a throwaway SQLite file per test run.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"chatbranch-test-{Guid.NewGuid():N}.sqlite3");

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Disable the background queue worker so drains are driven explicitly by
        // POST /api/flows/tick in tests (deterministic, no timing races).
        Environment.SetEnvironmentVariable("QUEUE_WORKER", "0");
        Environment.SetEnvironmentVariable("FLOWS_WORKER", "0");
        // Production seeds clean (no admin/agent logins) since the deploy pass,
        // but the suite signs in as those users on nearly every test. A test
        // host IS a demo environment, so it asks for the demo seed itself
        // rather than depending on CI to export the variable.
        Environment.SetEnvironmentVariable("SEED_DEMO", "1");
        builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = $"Data Source={_dbPath}" }));
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
