using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>Phase 3: persona gating, knowledge retrieval, skills sole-writer, sandbox reply.</summary>
public class AiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AiTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Persona_write_is_superadmin_only()
    {
        var admin = await Authed("admin", "admin123");
        var denied = await admin.PutAsJsonAsync("/api/aicore/persona", new { persona = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var su = await Authed("superadmin", "super123");
        var ok = await su.PutAsJsonAsync("/api/aicore/persona", new { persona = "Be extra friendly." });
        ok.EnsureSuccessStatusCode();

        var read = await su.GetFromJsonAsync<JsonElement>("/api/aicore/persona");
        Assert.Equal("Be extra friendly.", read.GetProperty("persona").GetString());
        Assert.False(string.IsNullOrEmpty(read.GetProperty("hardRules").GetString()));
    }

    [Fact]
    public async Task Knowledge_preview_surfaces_matching_items_and_respects_window()
    {
        var admin = await Authed("admin", "admin123");
        // a live fact
        await admin.PostAsJsonAsync("/api/aicore/knowledge", new
        {
            kind = "fact", title = "Store hours", content = "We open 9am to 6pm daily.",
            tags = new[] { "hours", "open" }, channels = Array.Empty<string>(),
            priority = 5, active = true,
        });
        // an EXPIRED notice — must not appear
        await admin.PostAsJsonAsync("/api/aicore/knowledge", new
        {
            kind = "notice", title = "Old outage", content = "Systems were down.",
            channels = Array.Empty<string>(), priority = 9, active = true,
            startsAt = DateTime.UtcNow.AddDays(-10), endsAt = DateTime.UtcNow.AddDays(-5),
        });

        var preview = await admin.PostAsJsonAsync("/api/aicore/knowledge/preview",
            new { query = "what are your hours?", channel = "whatsapp" });
        preview.EnsureSuccessStatusCode();
        var block = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("block").GetString()!;
        Assert.Contains("Store hours", block);
        Assert.DoesNotContain("Old outage", block); // expired window
    }

    [Fact]
    public async Task Skills_put_upserts_and_prunes_its_scope()
    {
        var admin = await Authed("admin", "admin123");
        await admin.PutAsJsonAsync("/api/aicore/skills", new
        {
            skills = new[]
            {
                new { skillId = "s1", name = "Greet", useWhen = "always", instructions = "Say hi", position = 0 },
                new { skillId = "s2", name = "Close", useWhen = "end", instructions = "Say bye", position = 1 },
            },
        });
        var one = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills");
        Assert.Equal(2, one.GetProperty("skills").GetArrayLength());

        // second PUT with only s1 prunes s2
        await admin.PutAsJsonAsync("/api/aicore/skills", new
        {
            skills = new[]
            {
                new { skillId = "s1", name = "Greet", useWhen = "always", instructions = "Say hi", position = 0 },
            },
        });
        var two = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills");
        Assert.Equal(1, two.GetProperty("skills").GetArrayLength());
    }

    [Fact]
    public async Task Sandbox_reply_generates_text_for_a_thread()
    {
        var c = await Authed("admin", "admin123");
        var key = "60111222333";
        await c.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "whatsapp", key, text = "do you deliver?" });
        var res = await c.PostAsJsonAsync("/api/aicore/reply", new { channel = "whatsapp", key });
        res.EnsureSuccessStatusCode();
        var reply = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reply").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reply));
    }
}
