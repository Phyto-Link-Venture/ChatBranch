using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// A conversation key with exactly 8 digits (e.g. a short Telegram user id) used to
/// crash FlowEngine.ContactFor (it checked &lt; 8 digits, then took the last 9).
/// </summary>
public class ShortKeyInboundTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ShortKeyInboundTests(ApiFactory f) => _f = f;

    [Theory]
    [InlineData("81234567")]   // 8 digits — the crash
    [InlineData("1234567")]    // 7 digits
    [InlineData("812345678")]  // 9 digits
    public async Task Telegram_inbound_with_a_short_numeric_key_is_accepted(string key)
    {
        var c = _f.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await c.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "telegram", key = "sk-" + key, text = "hello" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        res = await c.PostAsJsonAsync("/api/whatsapp/simulate", new { channel = "telegram", key, text = "hello" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
