using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>Automation rules: the Test button's condition-by-condition dry run, and reordering.</summary>
public class AutomationsApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AutomationsApiTests(ApiFactory f) => _f = f;

    private async Task<int> Create(HttpClient c, string name, object[] conditions)
    {
        var r = await c.PostAsJsonAsync("/api/automations", new
        {
            name, @event = "message_created", conditions,
            actions = new object[] { new { type = "add_label", value = "x" } },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Test_explains_each_condition_and_reorder_sets_the_run_order()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var key = "60177" + Random.Shared.Next(100000, 999999);
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Threads.Add(new Thread { Channel = "whatsapp", Key = key, Address = key, Name = "Rule test",
                Status = "open", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        var a = await Create(admin, "Order A", new object[]
        {
            new { field = "message", op = "contains", value = "refund" },
            new { field = "contact_linked", op = "equals", value = "no" },
        });
        var b = await Create(admin, "Order B", Array.Empty<object>());
        try
        {
            // the dry run says which condition held and which didn't
            var res = await (await admin.PostAsJsonAsync($"/api/automations/{a}/test",
                new { channel = "whatsapp", key, message = "hello there" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(res.GetProperty("matches").GetBoolean());
            var checks = res.GetProperty("conditions").EnumerateArray().ToList();
            Assert.Equal(2, checks.Count);
            Assert.False(checks[0].GetProperty("ok").GetBoolean());
            Assert.Equal("hello there", checks[0].GetProperty("actual").GetString());
            Assert.True(checks[1].GetProperty("ok").GetBoolean());   // the thread has no contact

            var yes = await (await admin.PostAsJsonAsync($"/api/automations/{a}/test",
                new { channel = "whatsapp", key, message = "I want a refund" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(yes.GetProperty("matches").GetBoolean());

            // a new rule runs last; reorder puts B first
            var list = await admin.GetFromJsonAsync<JsonElement>("/api/automations");
            var order = list.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();
            Assert.True(order.IndexOf(a) < order.IndexOf(b));

            var agent = await Platform.As(_f, "agent", "agent123");
            Assert.Equal(HttpStatusCode.Forbidden,
                (await agent.PostAsJsonAsync("/api/automations/reorder", new { ids = new[] { b, a } })).StatusCode);
            (await admin.PostAsJsonAsync("/api/automations/reorder", new { ids = new[] { b, a } })).EnsureSuccessStatusCode();
            list = await admin.GetFromJsonAsync<JsonElement>("/api/automations");
            order = list.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();
            Assert.True(order.IndexOf(b) < order.IndexOf(a));
            Assert.Equal(HttpStatusCode.BadRequest,
                (await admin.PostAsJsonAsync("/api/automations/reorder", new { ids = "nope" })).StatusCode);
        }
        finally
        {
            await admin.DeleteAsync($"/api/automations/{a}");
            await admin.DeleteAsync($"/api/automations/{b}");
        }
    }

    [Fact]
    public async Task The_voice_picked_on_Voice_and_calls_is_saved_for_everyone()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var agent = await Platform.As(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PutAsJsonAsync("/api/voice/default-voice", new { voice = "cedar" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/voice/default-voice", new { voice = "nope" })).StatusCode);
        (await admin.PutAsJsonAsync("/api/voice/default-voice", new { voice = "cedar" })).EnsureSuccessStatusCode();
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/voices");
        Assert.Equal("cedar", list.GetProperty("default_voice").GetString());
    }
}
