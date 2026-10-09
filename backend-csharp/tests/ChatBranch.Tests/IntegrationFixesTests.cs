using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Flows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>Fixes found while integrating the overhaul: secrets in the generic
/// settings read, capability-gated AI library writes, one org timezone.</summary>
public class IntegrationFixesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public IntegrationFixesTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Generic_settings_read_never_returns_a_secret_in_clear()
    {
        var super = await SettingsTestKit.Client(_f, "superadmin", "super123");
        (await super.PutAsJsonAsync("/api/settings",
            new { key = "openrouter_api_key", value = "sk-or-v1-abcdefghijklmnop1234" })).EnsureSuccessStatusCode();
        (await super.PutAsJsonAsync("/api/settings", new { key = "quiet_start", value = "22:00" })).EnsureSuccessStatusCode();

        foreach (var who in new[] { ("admin", "admin123"), ("superadmin", "super123") })
        {
            var c = await SettingsTestKit.Client(_f, who.Item1, who.Item2);
            var read = await c.GetFromJsonAsync<JsonElement>("/api/settings?keys=openrouter_api_key,quiet_start");
            var values = read.GetProperty("values");
            Assert.Equal("••••1234", values.GetProperty("openrouter_api_key").GetString());
            Assert.Equal("22:00", values.GetProperty("quiet_start").GetString());
        }
        // the raw key/value store is admin+ (PermissionGatesTests): an agent reads none of it
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.GetAsync("/api/settings?keys=quiet_start")).StatusCode);
    }

    [Fact]
    public async Task Knowledge_writes_follow_the_settings_ai_capability_not_the_admin_role()
    {
        var curator = await SettingsTestKit.CustomRoleClient(_f, "Knowledge Curator", "settings.ai.manage");
        var ok = await curator.PostAsJsonAsync("/api/aicore/knowledge",
            new { title = "Opening hours", body = "9am to 6pm", kind = "fact" });
        Assert.True(ok.IsSuccessStatusCode, $"expected success, got {(int)ok.StatusCode}");

        var outsider = await SettingsTestKit.CustomRoleClient(_f, "Bystander", "contacts.view");
        var denied = await outsider.PostAsJsonAsync("/api/aicore/knowledge",
            new { title = "Nope", body = "x", kind = "fact" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public void Flow_clock_and_ai_date_tools_read_the_same_org_timezone()
    {
        using var scope = _f.Services.CreateScope();
        var sc = scope.ServiceProvider.GetRequiredService<ScopingService>();
        sc.SetSetting("org_timezone", "Asia/Kuala_Lumpur", null);
        try
        {
            Assert.Equal(BuiltinSkills.OrgTimeZone(sc).BaseUtcOffset, OrgClock.Zone(sc).BaseUtcOffset);
            Assert.Equal(TimeSpan.FromHours(8), OrgClock.Zone(sc).BaseUtcOffset);
        }
        finally
        {
            sc.SetSetting("org_timezone", "", null);
        }
    }
}
