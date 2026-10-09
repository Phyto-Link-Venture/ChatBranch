using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>POST /api/client-errors: signed-in only, logged, never stored or audited.</summary>
public class ClientErrorTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ClientErrorTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_crash_report_is_accepted_from_a_signed_in_user_only_and_writes_no_rows()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/client-errors", new { message = "x" })).StatusCode);

        var c = _factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login", new { username = "agent", password = "agent123" });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        int AuditRows()
        {
            using var scope = _factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.Count();
        }
        var before = AuditRows();
        var res = await c.PostAsJsonAsync("/api/client-errors", new
        {
            message = "TypeError: x is undefined", stack = new string('s', 10_000), path = "/chats", digest = "123",
            user_agent = "test",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(before, AuditRows());   // logged to the API log only
    }
}
