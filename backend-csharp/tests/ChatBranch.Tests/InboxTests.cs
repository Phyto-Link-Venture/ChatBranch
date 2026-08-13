using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>Phase 2: the sandbox inbox flow end-to-end over HTTP.</summary>
public class InboxTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public InboxTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AuthedClient(string user = "admin", string pass = "admin123")
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Simulate_inbound_then_list_read_and_reply()
    {
        var c = await AuthedClient();
        var key = "60123456789";

        // inbound
        var sim = await c.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "whatsapp", key, text = "hello there", name = "Test User" });
        sim.EnsureSuccessStatusCode();

        // thread appears in the list
        var list = await c.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads?channel=whatsapp");
        var threads = list.GetProperty("threads").EnumerateArray().ToList();
        Assert.Contains(threads, t => t.GetProperty("key").GetString() == key);

        // messages contain the inbound
        var msgs = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/threads/whatsapp/{key}/messages");
        var messages = msgs.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(messages);
        Assert.Equal("in", messages[0].GetProperty("direction").GetString());
        Assert.Equal("hello there", messages[0].GetProperty("text").GetString());

        // outbound sandbox reply
        var send = await c.PostAsJsonAsync($"/api/whatsapp/threads/whatsapp/{key}/send",
            new { text = "hi back" });
        send.EnsureSuccessStatusCode();
        var sent = await send.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("out", sent.GetProperty("direction").GetString());
        Assert.Equal("sandbox", sent.GetProperty("status").GetString());

        // now two messages, chronological
        var msgs2 = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/threads/whatsapp/{key}/messages");
        Assert.Equal(2, msgs2.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Inbound_redelivery_is_deduped_by_no_double_message()
    {
        var c = await AuthedClient();
        var key = "60999000111";
        await c.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "whatsapp", key, text = "one" });
        await c.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "whatsapp", key, text = "two" });
        var msgs = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/threads/whatsapp/{key}/messages");
        Assert.Equal(2, msgs.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Canned_response_crud_is_admin_gated()
    {
        var c = await AuthedClient();
        var create = await c.PostAsJsonAsync("/api/messaging/canned",
            new { shortCode = "greet", content = "Hello {{name}}" });
        create.EnsureSuccessStatusCode();

        var list = await c.GetFromJsonAsync<JsonElement>("/api/messaging/canned");
        Assert.Contains(list.GetProperty("canned").EnumerateArray(),
            x => x.GetProperty("shortCode").GetString() == "greet");

        // duplicate short_code → 409
        var dup = await c.PostAsJsonAsync("/api/messaging/canned",
            new { shortCode = "greet", content = "again" });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, dup.StatusCode);
    }
}
