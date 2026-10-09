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
    public async Task Persona_and_house_rules_follow_the_entity_tree()
    {
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync("/api/aicore/persona", new { persona = "x" })).StatusCode);

        var admin = await Authed("admin", "admin123");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/entities",
            new { name = "Persona Head", slug = "persona-head", parent = "main" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/entities",
            new { name = "Persona Outlet", slug = "persona-outlet", parent = "persona-head" })).StatusCode);
        try
        {
            (await admin.PutAsJsonAsync("/api/aicore/persona",
                new { persona = "Org voice.", house_rules = "- Be polite\n\nNever share another customer's data" }))
                .EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync("/api/aicore/persona",
                new { persona = "Head office voice.", house_rules = "Quote list prices only", entity = "persona-head" }))
                .EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync("/api/aicore/persona",
                new { house_rules = "Mention the Sunday hours", entity = "persona-outlet" })).EnsureSuccessStatusCode();

            // the outlet has no persona of its own → its parent's, not the org's
            var outlet = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/persona?entity=persona-outlet");
            Assert.Equal("Head office voice.", outlet.GetProperty("persona").GetString());
            Assert.Equal("parent", outlet.GetProperty("source").GetProperty("scope").GetString());
            Assert.Equal("persona-head", outlet.GetProperty("source").GetProperty("entity").GetString());
            Assert.Equal("", outlet.GetProperty("own_persona").GetString());
            // house rules add up, organization first
            var inherited = outlet.GetProperty("house_rules").GetProperty("inherited").EnumerateArray()
                .Select(r => r.GetProperty("rule").GetString()).ToList();
            Assert.Equal(new[] { "Be polite", "Never share another customer's data", "Quote list prices only" }, inherited);
            Assert.Equal(new[] { "Mention the Sunday hours" }, outlet.GetProperty("house_rules").GetProperty("own")
                .EnumerateArray().Select(r => r.GetString()).ToArray());
            var preamble = outlet.GetProperty("preamble").GetString()!;
            Assert.True(preamble.IndexOf("HOUSE RULES", StringComparison.Ordinal)
                        < preamble.IndexOf("Head office voice.", StringComparison.Ordinal));
            Assert.Contains("- Mention the Sunday hours", preamble);
            Assert.Contains("- Be polite", preamble);
            Assert.True(outlet.GetProperty("can_manage").GetBoolean());

            // its own persona replaces the parent's; an empty one drops the override again
            (await admin.PutAsJsonAsync("/api/aicore/persona", new { persona = "Outlet voice.", entity = "persona-outlet" }))
                .EnsureSuccessStatusCode();
            var own = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/persona?entity=persona-outlet");
            Assert.Equal("Outlet voice.", own.GetProperty("persona").GetString());
            Assert.Equal("entity", own.GetProperty("source").GetProperty("scope").GetString());
            Assert.Equal("Head office voice.", own.GetProperty("inherited_persona").GetString());
            (await admin.PutAsJsonAsync("/api/aicore/persona", new { persona = "", entity = "persona-outlet" }))
                .EnsureSuccessStatusCode();
            Assert.Equal("Head office voice.", (await admin.GetFromJsonAsync<JsonElement>(
                "/api/aicore/persona?entity=persona-outlet")).GetProperty("persona").GetString());

            Assert.Equal(HttpStatusCode.NotFound,
                (await admin.PutAsJsonAsync("/api/aicore/persona", new { persona = "x", entity = "no-such-outlet" })).StatusCode);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/aicore/persona", new { persona = "", house_rules = "" });
        }
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
                new { id = "s1", name = "Greet", useWhen = "always", instructions = "Say hi", position = 0 },
                new { id = "s2", name = "Close", useWhen = "end", instructions = "Say bye", position = 1 },
            },
        });
        var one = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills");
        Assert.Equal(2, one.GetProperty("skills").GetArrayLength());

        // second PUT with only s1 prunes s2
        await admin.PutAsJsonAsync("/api/aicore/skills", new
        {
            skills = new[]
            {
                new { id = "s1", name = "Greet", useWhen = "always", instructions = "Say hi", position = 0 },
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
