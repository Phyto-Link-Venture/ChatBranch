using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>No-op WhatsApp client for unit tests — always "not configured" (sandbox path).</summary>
internal class StubWhatsApp : IWhatsAppCloudClient
{
    public bool IsConfigured(ChannelAccount account) => false;
    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text,
        CancellationToken ct = default) => Task.FromResult(new SendOutcome(false, "", "stub"));
}

/// <summary>Phase 4: outbound queue (atomic claim + pacing) and workflow library.</summary>
public class FlowsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public FlowsTests(ApiFactory factory) => _factory = factory;

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
    public async Task Enqueue_then_tick_sends_and_appears_in_thread()
    {
        var c = await Authed("admin", "admin123");
        var enq = await c.PostAsJsonAsync("/api/flows/enqueue", new
        {
            channel = "whatsapp", kind = "text", toAddress = "60122334455",
            payload = new { text = "queued hello" },
        });
        enq.EnsureSuccessStatusCode();

        var tick = await c.PostAsJsonAsync("/api/flows/tick", new { });
        tick.EnsureSuccessStatusCode();
        Assert.Equal(1, (await tick.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("drained").GetInt32());

        var msgs = await c.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/whatsapp/60122334455/messages");
        var texts = msgs.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("text").GetString()).ToList();
        Assert.Contains("queued hello", texts);
    }

    [Fact]
    public void Atomic_claim_prevents_double_send()
    {
        // Two QueueService instances over the SAME in-memory DB race to drain one
        // job. The guarded queued→sending UPDATE must let exactly one win.
        using var t = new TestDb();
        var scoping = new ScopingService(t.Db);
        var threads = new ThreadStore(t.Db, scoping);
        var assistant = new AssistantService(t.Db, scoping,
            new PersonaService(scoping), new KnowledgeService(t.Db, scoping), new SandboxLlmClient());

        var sender1 = new OutboundSender(t.Db, threads, new StubWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        var q1 = new QueueService(t.Db, scoping, threads, assistant, sender1);
        q1.Enqueue("whatsapp", "text", "60100000000", new() { ["text"] = "once" });

        // Second context/service sharing the same connection.
        var db2 = t.Fresh();
        var sc2 = new ScopingService(db2);
        var threads2 = new ThreadStore(db2, sc2);
        var q2 = new QueueService(db2, sc2, threads2,
            new AssistantService(db2, sc2, new PersonaService(sc2), new KnowledgeService(db2, sc2), new SandboxLlmClient()),
            new OutboundSender(db2, threads2, new StubWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway()));

        var a = q1.Drain(10);
        var b = q2.Drain(10);
        Assert.Equal(1, a + b); // exactly one send total

        var msgCount = t.Fresh().Messages.Count(m => m.Text == "once");
        Assert.Equal(1, msgCount);
    }

    [Fact]
    public async Task Workflow_library_put_prunes_within_scope()
    {
        var c = await Authed("admin", "admin123");
        await c.PutAsJsonAsync("/api/flows/library", new
        {
            workflows = new[]
            {
                new { flowId = "wf-1", name = "Reminder", hint = "", nodes = Array.Empty<object>(),
                      edges = Array.Empty<object>(), active = true, position = 0 },
                new { flowId = "wf-2", name = "Winback", hint = "", nodes = Array.Empty<object>(),
                      edges = Array.Empty<object>(), active = false, position = 1 },
            },
        });
        var one = await c.GetFromJsonAsync<JsonElement>("/api/flows/library");
        Assert.Equal(2, one.GetProperty("workflows").GetArrayLength());

        await c.PutAsJsonAsync("/api/flows/library", new
        {
            workflows = new[]
            {
                new { flowId = "wf-1", name = "Reminder v2", hint = "", nodes = Array.Empty<object>(),
                      edges = Array.Empty<object>(), active = true, position = 0 },
            },
        });
        var two = await c.GetFromJsonAsync<JsonElement>("/api/flows/library");
        Assert.Equal(1, two.GetProperty("workflows").GetArrayLength());
        Assert.Equal("Reminder v2", two.GetProperty("workflows")[0].GetProperty("name").GetString());
    }
}
