using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data.Sidecars;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>Phase 5: contacts, channel hub, analytics, script templates, voice bridge IPC.</summary>
public class ContactsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ContactsTests(ApiFactory factory) => _factory = factory;

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
    public async Task Create_read_update_contact_and_link_a_channel()
    {
        var c = await Authed("admin", "admin123");
        var create = await c.PostAsJsonAsync("/api/contacts",
            new { name = "Alice", phone = "60123000111", email = "a@x.com", tags = new[] { "vip" } });
        create.EnsureSuccessStatusCode();
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var got = await c.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}");
        Assert.Equal("Alice", got.GetProperty("name").GetString());

        // link a whatsapp identity, then the hub reflects it
        var link = await c.PostAsJsonAsync($"/api/contacts/{id}/channels",
            new { channel = "whatsapp", address = "60123000111" });
        link.EnsureSuccessStatusCode();

        var hub = await c.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}/channels");
        Assert.Contains(hub.GetProperty("channels").EnumerateArray(),
            ch => ch.GetProperty("channel").GetString() == "whatsapp");
    }

    [Fact]
    public async Task Analytics_summary_and_usage_return_shape()
    {
        var admin = await Authed("admin", "admin123");
        var summary = await admin.GetFromJsonAsync<JsonElement>("/api/analytics/summary?days=7");
        // Django-contract shape (frontend/src/components/analytics/types.ts)
        Assert.True(summary.TryGetProperty("funnel", out _));
        Assert.True(summary.TryGetProperty("daily", out var daily));
        // Django walks since.date()..today INCLUSIVE, so days=7 → 8 buckets
        Assert.Equal(8, daily.GetArrayLength()); // zero-filled daily series
        Assert.True(summary.GetProperty("call_outcomes").TryGetProperty("total", out _));

        var usage = await admin.GetFromJsonAsync<JsonElement>("/api/analytics/usage?days=7");
        Assert.True(usage.GetProperty("totals").TryGetProperty("est_cost_usd", out _));
        Assert.True(usage.TryGetProperty("by_model", out _));
    }

    [Fact]
    public async Task Script_templates_crud()
    {
        var admin = await Authed("admin", "admin123");
        var create = await admin.PostAsJsonAsync("/api/calls/script-templates",
            new { name = "Reminder call", content = "Hello {{name}}, this is a reminder." });
        create.EnsureSuccessStatusCode();
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/calls/script-templates");
        // key is `templates` (what ScriptTemplatesSettings reads)
        Assert.Contains(list.GetProperty("templates").EnumerateArray(),
            s => s.GetProperty("name").GetString() == "Reminder call");
    }

    [Fact]
    public void Voice_agent_bridge_round_trips_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cb-voice-{Guid.NewGuid():N}");
        try
        {
            var bridge = new VoiceAgentBridge(dir);

            // no live file yet → empty envelope
            var empty = bridge.LiveTranscript("room-1");
            Assert.Equal("idle", empty.GetProperty("status").GetString());

            // instructions append to the consume-once inbox list the agent pops
            bridge.Say("room-1", "Please hold.");
            bridge.Guide("room-1", "Offer Friday.");
            var inbox = Path.Combine(dir, "inbox", "room-1.json");
            Assert.True(File.Exists(inbox));
            var doc = JsonDocument.Parse(File.ReadAllText(inbox));
            Assert.Equal(2, doc.RootElement.GetArrayLength());
            Assert.Equal("takeover", doc.RootElement[0].GetProperty("mode").GetString());
            Assert.Equal("Offer Friday.", doc.RootElement[1].GetProperty("text").GetString());

            // a live transcript written by the "agent" is read back verbatim
            Directory.CreateDirectory(Path.Combine(dir, "live"));
            File.WriteAllText(Path.Combine(dir, "live", "room-1.json"),
                """{"room":"room-1","status":"active","turns":[{"who":"agent","t":"Hi"}]}""");
            var live = bridge.LiveTranscript("room-1");
            Assert.Equal("active", live.GetProperty("status").GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
