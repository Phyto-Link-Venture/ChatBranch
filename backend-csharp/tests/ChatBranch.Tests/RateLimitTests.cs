using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api.Security;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Request rate limits (Security/RateLimiting), turned ON — and low — for this host only
/// (the rest of the suite runs with RATE_LIMIT=0): stricter for sign-in, per IP for
/// anonymous callers, per session for signed-in ones, provider webhooks exempt, and a
/// refusal is 429 JSON with Retry-After.
/// </summary>
public class RateLimitTests : IClassFixture<RateLimitedFactory>
{
    private readonly RateLimitedFactory _f;
    public RateLimitTests(RateLimitedFactory f) => _f = f;

    private static int _peer;
    private static string NewPeer() => $"203.0.113.{Interlocked.Increment(ref _peer) % 250 + 1}";

    [Fact]
    public async Task Sign_in_is_limited_per_address_with_a_clear_429()
    {
        var c = _f.From(NewPeer());
        for (var i = 0; i < 3; i++)
            Assert.NotEqual((HttpStatusCode)429,
                (await c.PostAsJsonAsync("/api/auth/login", new { username = "rl_" + i, password = "x" })).StatusCode);
        var limited = await c.PostAsJsonAsync("/api/auth/login", new { username = "rl_4", password = "x" });
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) >= 1);
        var body = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limited", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("detail").GetString()));

        // another address is untouched
        Assert.Equal(HttpStatusCode.OK, (await _f.From(NewPeer()).PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin123" })).StatusCode);
    }

    [Fact]
    public async Task Anonymous_reads_are_limited_per_address()
    {
        var c = _f.From(NewPeer());
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) codes.Add((await c.GetAsync("/api/help/articles")).StatusCode);
        Assert.Equal(6, codes.Count(s => s == HttpStatusCode.OK));
        Assert.Equal((HttpStatusCode)429, codes[^1]);
    }

    [Fact]
    public async Task Signed_in_callers_are_limited_per_session_not_per_address()
    {
        var peer = NewPeer();
        var a = await _f.SignedIn("admin", "admin123", peer);
        var b = await _f.SignedIn("agent", "agent123", NewPeer());
        var seen = new List<HttpStatusCode>();
        for (var i = 0; i < 14; i++) seen.Add((await a.GetAsync("/api/auth/whoami")).StatusCode);
        Assert.Contains((HttpStatusCode)429, seen);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/auth/whoami")).StatusCode);
        // a made-up bearer token is no fresh bucket: it counts as anonymous for its address
        var fake = _f.From(NewPeer());
        fake.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-real-token");
        var fakes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) fakes.Add((await fake.GetAsync("/api/help/articles")).StatusCode);
        Assert.Contains((HttpStatusCode)429, fakes);
    }

    [Fact]
    public async Task Provider_webhooks_are_never_rate_limited()
    {
        var c = _f.From(NewPeer());
        for (var i = 0; i < 20; i++)
        {
            var res = await c.PostAsync("/api/whatsapp/webhook", JsonContent.Create(new { entry = Array.Empty<object>() }));
            Assert.NotEqual((HttpStatusCode)429, res.StatusCode);
        }
    }

    [Fact]
    public void Requests_fall_in_the_right_class()
    {
        Assert.Equal(RateLimiting.Login, RateLimiting.Classify("POST", "/api/auth/login", false));
        Assert.Equal(RateLimiting.Login, RateLimiting.Classify("POST", "/api/auth/login/2fa", false));
        Assert.Equal(RateLimiting.Widget, RateLimiting.Classify("POST", "/api/widget/abc/session", false));
        Assert.Equal(RateLimiting.Widget, RateLimiting.Classify("POST", "/api/widget/abc/messages", false));
        Assert.Equal(RateLimiting.Anonymous, RateLimiting.Classify("GET", "/api/widget/abc/messages", false));
        Assert.Equal(RateLimiting.PublicLink, RateLimiting.Classify("GET", "/api/documents/public/tok", false));
        Assert.Equal(RateLimiting.PublicLink, RateLimiting.Classify("GET", "/api/billing/ai/pdf/s/tok", false));
        Assert.Equal(RateLimiting.Media, RateLimiting.Classify("GET", "/api/media/s/tok", true));
        Assert.Equal(RateLimiting.OAuth, RateLimiting.Classify("GET", "/api/integrations/oauth/google/callback", false));
        Assert.Equal(RateLimiting.Exempt, RateLimiting.Classify("POST", "/api/whatsapp/webhook", false));
        Assert.Equal(RateLimiting.Exempt, RateLimiting.Classify("POST", "/api/messaging/webhook/line/3", false));
        Assert.Equal(RateLimiting.Exempt, RateLimiting.Classify("POST", "/api/whatsapp/baileys/inbound", false));
        Assert.Equal(RateLimiting.Exempt, RateLimiting.Classify("POST", "/api/integrations/webhooks/stripe", false));
        Assert.Equal(RateLimiting.User, RateLimiting.Classify("GET", "/api/contacts", true));
        Assert.Equal(RateLimiting.Anonymous, RateLimiting.Classify("GET", "/api/help/articles", false));
    }
}
