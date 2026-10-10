using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data.Sidecars;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

/// <summary>
/// Boots Program with a throwaway SQLite file and data directory per test run,
/// and a LiveKit client that is never configured — a developer's backend/.env
/// must not let the suite place real calls.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"chatbranch-test-{Guid.NewGuid():N}.sqlite3");

    /// <summary>Shared by every host in the run (the env var is process-wide).</summary>
    public static readonly string DataDir =
        Path.Combine(Path.GetTempPath(), $"chatbranch-test-data-{Environment.ProcessId}");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.AddSingleton(sp => new LiveKitClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            () => new LiveKitSettings("", "", "", LiveKitSettings.DefaultAgentName, ""))));

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Disable the background queue worker so drains are driven explicitly by
        // POST /api/flows/tick in tests (deterministic, no timing races).
        Environment.SetEnvironmentVariable("QUEUE_WORKER", "0");
        Environment.SetEnvironmentVariable("FLOWS_WORKER", "0");
        // AI ops: no background auto-send/follow-up pass, and the human-like
        // DEFAULTS (wait for the customer, typing pauses, splitting) off so the
        // sandbox answers inline; tests that need them set the Setting rows.
        Environment.SetEnvironmentVariable("AI_FOLLOWUP_WORKER", "0");
        // No periodic channel-health probes (nor post-save re-checks) against real
        // providers; TransportFactory opts back in with fake transports.
        Environment.SetEnvironmentVariable("HEALTH_WORKER", "0");
        // No background embedding passes: the semantic index is driven
        // explicitly (EmbeddingIndexer.SyncAsync) with a fake embedding client.
        Environment.SetEnvironmentVariable("EMBEDDING_WORKER", "0");
        // no background WhatsApp number lookups (POST /api/contacts/identity-checks/run drives them)
        Environment.SetEnvironmentVariable("IDENTITY_CHECK_WORKER", "0");
        // no fetch of OpenRouter's public price list — reports price from the seeded table only
        Environment.SetEnvironmentVariable("LLM_PRICE_SYNC", "0");
        Environment.SetEnvironmentVariable("AI_HUMANIZE", "0");
        // Production seeds clean (no admin/agent logins) since the deploy pass,
        // but the suite signs in as those users on nearly every test. A test
        // host IS a demo environment, so it asks for the demo seed itself
        // rather than depending on CI to export the variable.
        Environment.SetEnvironmentVariable("SEED_DEMO", "1");
        // Fast password hashes (Django's test-settings practice): 600 000 PBKDF2
        // rounds per seeded user and per sign-in saturated a 2-vCPU runner until
        // logins timed out. Production keeps Django's default.
        Environment.SetEnvironmentVariable("PASSWORD_HASH_ITERATIONS", "1000");
        // Media, voices.json and the voice-agent files go to a throwaway dir,
        // never into a checkout's live backend/data.
        Environment.SetEnvironmentVariable("CHATBRANCH_DATA_DIR", DataDir);
        builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = $"Data Source={_dbPath}" }));
        // Every SQL statement at Information level buried the one failing test
        // in hundreds of thousands of CI log lines; warnings and errors still show.
        builder.ConfigureLogging(l =>
        {
            l.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
            l.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        });
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
