using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// /api/aicore/skills: the new fields round-trip through the sole writer, an
/// enabled code skill must pass its own tests to be saved, and the authoring
/// endpoints (draft / test / builtins) work in sandbox mode.
/// </summary>
public class SkillsApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public SkillsApiTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private const string DoubleCode = "function run(args, ctx) { return { doubled: args.n * 2 }; }";

    [Fact]
    public async Task Code_skill_fields_round_trip_and_failing_tests_block_an_enabled_save()
    {
        var admin = await Authed("admin", "admin123");
        var put = await admin.PutAsJsonAsync("/api/aicore/skills?x=1", new
        {
            entity = (string?)null,
            skills = new object[]
            {
                new { id = "rt-prompt", name = "Greet", useWhen = "always", instructions = "Say hi", position = 0 },
                new
                {
                    id = "rt-tool", name = "Double It", useWhen = "asked to double", instructions = "", position = 1,
                    kind = "tool", description = "Doubles n", code = DoubleCode,
                    parameters = new { type = "object", properties = new { n = new { type = "integer" } } },
                    tests = new object[] { new { name = "2 → 4", args = new { n = 2 }, expect = new { doubled = 4 } } },
                    enabled = true, channels = new[] { "WhatsApp" }, onFail = "retry",
                },
                new
                {
                    id = "rt-check", name = "No guarantees", useWhen = "", instructions = "", position = 2,
                    kind = "check", code = "function check(r) { return { ok: !/guarantee/i.test(r), reason: 'no guarantees' }; }",
                    tests = new object[] { new { name = "blocks", input = "we guarantee", expect = false } },
                    enabled = true, onFail = "handover",
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var skills = (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("skills");
        var tool = skills.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "rt-tool");
        Assert.Equal("tool", tool.GetProperty("kind").GetString());
        Assert.Equal("double_it", tool.GetProperty("tool_name").GetString());
        Assert.True(tool.GetProperty("enabled").GetBoolean());
        Assert.Equal("whatsapp", tool.GetProperty("channels")[0].GetString());
        Assert.Equal("integer", tool.GetProperty("parameters").GetProperty("properties").GetProperty("n").GetProperty("type").GetString());
        Assert.Equal(1, tool.GetProperty("tests").GetArrayLength());
        Assert.Equal("global", tool.GetProperty("scope").GetString());
        var check = skills.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "rt-check");
        Assert.Equal("handover", check.GetProperty("onFail").GetString());
        var prompt = skills.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "rt-prompt");
        Assert.Equal("prompt", prompt.GetProperty("kind").GetString()); // legacy shape → prompt

        // an ENABLED skill whose test fails is refused …
        var failing = new
        {
            skills = new object[]
            {
                new
                {
                    id = "rt-tool", name = "Double It", useWhen = "", instructions = "", position = 0,
                    kind = "tool", code = DoubleCode, enabled = true,
                    tests = new object[] { new { name = "wrong", args = new { n = 2 }, expect = new { doubled = 5 } } },
                },
            },
        };
        var refused = await admin.PutAsJsonAsync("/api/aicore/skills", failing);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("wrong", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        // … but may be saved disabled (and that PUT prunes the other two)
        var disabled = await admin.PutAsJsonAsync("/api/aicore/skills", new
        {
            skills = new object[]
            {
                new
                {
                    id = "rt-tool", name = "Double It", useWhen = "", instructions = "", position = 0,
                    kind = "tool", code = DoubleCode, enabled = false,
                    tests = new object[] { new { name = "wrong", args = new { n = 2 }, expect = new { doubled = 5 } } },
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills");
        var only = Assert.Single(after.GetProperty("skills").EnumerateArray());
        Assert.False(only.GetProperty("enabled").GetBoolean());

        // syntax errors are refused when enabled
        var broken = await admin.PutAsJsonAsync("/api/aicore/skills", new
        {
            skills = new object[]
            {
                new { id = "rt-bad", name = "Bad", useWhen = "", instructions = "", position = 0, kind = "check",
                      code = "function check( {", enabled = true },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);

        // an unknown entity never falls back to the global scope
        var unknown = await admin.PutAsJsonAsync("/api/aicore/skills", new { entity = "nope", skills = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills")).GetProperty("skills").EnumerateArray());

        // the write is audited
        var audit = await (await Authed("superadmin", "super123")).GetAsync("/api/auth/audit");
        if (audit.IsSuccessStatusCode)
            Assert.Contains("skills.update", await audit.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Test_endpoint_reports_per_test_results()
    {
        var admin = await Authed("admin", "admin123");
        var res = await admin.PostAsJsonAsync("/api/aicore/skills/test", new
        {
            kind = "tool",
            code = DoubleCode,
            tests = new object[]
            {
                new { name = "ok", args = new { n = 3 }, expect = new { doubled = 6 } },
                new { name = "bad", args = new { n = 3 }, expect = new { doubled = 7 } },
            },
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("valid").GetBoolean());
        Assert.Equal(1, body.GetProperty("passed").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
        var bad = body.GetProperty("results")[1];
        Assert.False(bad.GetProperty("pass").GetBoolean());
        Assert.Equal(6, bad.GetProperty("got").GetProperty("doubled").GetInt32());
        Assert.Equal(7, bad.GetProperty("expected").GetProperty("doubled").GetInt32());

        var invalid = await (await admin.PostAsJsonAsync("/api/aicore/skills/test",
            new { kind = "check", code = "function nope() {}", tests = Array.Empty<object>() }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(invalid.GetProperty("valid").GetBoolean());
        Assert.Contains("check(", invalid.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Draft_endpoint_clarifies_then_drafts_in_sandbox_mode()
    {
        var admin = await Authed("admin", "admin123");
        var q = await (await admin.PostAsJsonAsync("/api/aicore/skills/draft",
            new { description = "Tell customers if the shop is open now" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("questions", q.GetProperty("status").GetString());
        var question = q.GetProperty("questions")[0].GetString();

        var d = await (await admin.PostAsJsonAsync("/api/aicore/skills/draft", new
        {
            description = "Tell customers if the shop is open now",
            answers = new[] { new { question, answer = "Mon-Sat 10am-8pm, Sun closed" } },
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("draft", d.GetProperty("status").GetString());
        var skill = d.GetProperty("skill");
        Assert.Equal("tool", skill.GetProperty("kind").GetString());
        Assert.Contains("Saturday: ['10:00', '20:00']", skill.GetProperty("code").GetString());

        // the draft's own tests pass through the test endpoint
        var run = await (await admin.PostAsJsonAsync("/api/aicore/skills/test", new
        {
            kind = "tool", code = skill.GetProperty("code").GetString(), tests = skill.GetProperty("tests"),
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, run.GetProperty("failed").GetInt32());

        var empty = await admin.PostAsJsonAsync("/api/aicore/skills/draft", new { description = " " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Builtins_are_listed_and_authoring_is_admin_only()
    {
        var agent = await Authed("agent", "agent123");
        var b = await agent.GetFromJsonAsync<JsonElement>("/api/aicore/skills/builtins");
        var names = b.GetProperty("builtins").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "current_time", "current_week", "date_add", "days_between", "convert_timezone" }, names);
        Assert.True(b.GetProperty("builtins")[0].GetProperty("example").TryGetProperty("weekday", out _));

        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/aicore/skills/draft", new { description = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/aicore/skills/test", new { kind = "tool", code = DoubleCode })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync("/api/aicore/skills", new { skills = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task Reply_endpoint_returns_the_audit_metadata()
    {
        var c = await Authed("admin", "admin123");
        await c.PostAsJsonAsync("/api/whatsapp/simulate", new { channel = "whatsapp", key = "60199888777", text = "what time is it?" });
        var res = await c.PostAsJsonAsync("/api/aicore/reply", new { channel = "whatsapp", key = "60199888777" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("send", body.GetProperty("action").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("reply").GetString()));
        Assert.True(body.GetProperty("meta").TryGetProperty("checks", out _));
    }
}
