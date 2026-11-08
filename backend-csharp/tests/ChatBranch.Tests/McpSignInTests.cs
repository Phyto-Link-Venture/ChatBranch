using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Connecting an AI app by SIGNING IN instead of pasting an API key: the whole OAuth 2.1
/// flow MCP clients already speak — discovery, the app registering itself, a person
/// pressing Allow, the code becoming tokens, and what those tokens may then do.
///
/// The interesting assertions are the refusals: a redirect URI that was not registered,
/// a code used twice, a PKCE verifier that does not match, an app trying to approve
/// itself, and a read-only connection being unable to send.
/// </summary>
public class McpSignInTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public McpSignInTests(ApiFactory f) => _f = f;

    private T Db<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _f.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>A client that does not follow redirects, so the flow's hops are visible.</summary>
    private HttpClient Raw() => _f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    private const string Redirect = "https://claude.ai/api/mcp/auth_callback";

    /// <summary>A PKCE pair, as a client makes one.</summary>
    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private async Task<string> RegisterAsync(string name = "Claude", string redirect = Redirect)
    {
        var res = await Raw().PostAsJsonAsync("/api/oauth/register",
            new { client_name = name, redirect_uris = new[] { redirect } });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Ok(res)).GetProperty("client_id").GetString()!;
    }

    /// <summary>Open the sign-in page and read the request id out of the redirect.</summary>
    private async Task<string> StartAsync(string clientId, string challenge, string state = "st-1",
        string scope = McpOAuth.ScopeFull, string redirect = Redirect)
    {
        var res = await Raw().GetAsync($"/api/oauth/authorize?response_type=code&client_id={clientId}"
            + $"&redirect_uri={Uri.EscapeDataString(redirect)}&state={state}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&scope={Uri.EscapeDataString(scope)}");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.Contains("/connect?request=", location);
        return location.Split("request=")[1].Split('&')[0];
    }

    /// <summary>Press Allow as a person, and read the code out of the redirect it gives back.</summary>
    private async Task<string> ApproveAsync(HttpClient person, string request, bool readOnly = false)
    {
        var answer = await Ok(await person.PostAsJsonAsync($"/api/oauth/authorize/{request}",
            new { allow = true, read_only = readOnly }));
        Assert.True(answer.GetProperty("allowed").GetBoolean());
        var redirect = answer.GetProperty("redirect").GetString()!;
        Assert.StartsWith(Redirect, redirect);
        Assert.Contains("state=st-1", redirect);
        return redirect.Split("code=")[1].Split('&')[0];
    }

    private async Task<JsonElement> TokensAsync(string clientId, string code, string verifier)
    {
        var res = await Raw().PostAsync("/api/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = clientId,
                ["redirect_uri"] = Redirect,
                ["code_verifier"] = verifier,
            }));
        return await Ok(res);
    }

    private HttpClient WithToken(string access)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return c;
    }

    // ---- discovery ---------------------------------------------------------

    [Fact]
    public async Task An_mcp_client_with_no_token_is_told_where_to_sign_in()
    {
        var res = await Raw().PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        // the one header that makes "paste the URL into the app" work
        var challenge = Assert.Single(res.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("/.well-known/oauth-protected-resource", challenge.Parameter);
    }

    [Fact]
    public async Task The_two_discovery_documents_say_what_a_client_needs()
    {
        var resource = await Ok(await Raw().GetAsync("/.well-known/oauth-protected-resource"));
        Assert.EndsWith("/api/mcp", resource.GetProperty("resource").GetString());
        var server = resource.GetProperty("authorization_servers").EnumerateArray().First().GetString()!;

        // a client also asks with the resource path appended
        await Ok(await Raw().GetAsync("/.well-known/oauth-protected-resource/api/mcp"));

        var meta = await Ok(await Raw().GetAsync("/.well-known/oauth-authorization-server"));
        Assert.Equal(server, meta.GetProperty("issuer").GetString());
        Assert.EndsWith("/api/oauth/authorize", meta.GetProperty("authorization_endpoint").GetString());
        Assert.EndsWith("/api/oauth/token", meta.GetProperty("token_endpoint").GetString());
        Assert.EndsWith("/api/oauth/register", meta.GetProperty("registration_endpoint").GetString());
        // S256 only: "plain" would prove nothing an interceptor could not also produce
        Assert.Equal(new[] { "S256" },
            meta.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    // ---- the flow ----------------------------------------------------------

    [Fact]
    public async Task An_app_registers___a_person_allows___and_the_app_can_use_mcp()
    {
        var clientId = await RegisterAsync();
        var (verifier, challenge) = Pkce();
        var request = await StartAsync(clientId, challenge);

        // the Allow page sees whose decision it is and what is being asked
        var admin = await Platform.As(_f, "admin", "admin123");
        var info = await Ok(await admin.GetAsync($"/api/oauth/authorize/{request}"));
        Assert.Equal("Claude", info.GetProperty("client_name").GetString());
        Assert.Equal("claude.ai", info.GetProperty("redirect_host").GetString());
        Assert.Equal("admin", info.GetProperty("user").GetString());
        Assert.False(info.GetProperty("read_only").GetBoolean());

        var code = await ApproveAsync(admin, request);
        var tokens = await TokensAsync(clientId, code, verifier);
        var access = tokens.GetProperty("access_token").GetString()!;
        Assert.StartsWith(McpOAuth.AccessPrefix, access);
        Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());
        Assert.True(tokens.GetProperty("expires_in").GetInt32() > 0);
        Assert.StartsWith(McpOAuth.RefreshPrefix, tokens.GetProperty("refresh_token").GetString());

        // and that token works on the MCP endpoint, as the person who allowed it
        var list = await Ok(await WithToken(access).PostAsJsonAsync("/api/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" }));
        Assert.True(list.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
    }

    [Fact]
    public async Task A_refresh_token_keeps_the_app_working___and_is_spent_when_used()
    {
        var clientId = await RegisterAsync("Refreshing app");
        var (verifier, challenge) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var code = await ApproveAsync(admin, await StartAsync(clientId, challenge));
        var first = await TokensAsync(clientId, code, verifier);
        var refresh = first.GetProperty("refresh_token").GetString()!;

        var second = await Ok(await Raw().PostAsync("/api/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId,
            })));
        var renewed = second.GetProperty("access_token").GetString()!;
        Assert.NotEqual(first.GetProperty("access_token").GetString(), renewed);
        await Ok(await WithToken(renewed).PostAsJsonAsync("/api/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" }));

        // the old refresh token is spent: a replay buys nothing
        var replay = await Raw().PostAsync("/api/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = clientId,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    // ---- the refusals ------------------------------------------------------

    [Fact]
    public async Task A_redirect_uri_the_app_did_not_register_is_refused_without_redirecting()
    {
        var clientId = await RegisterAsync("Picky app");
        var (_, challenge) = Pkce();

        var res = await Raw().GetAsync($"/api/oauth/authorize?response_type=code&client_id={clientId}"
            + $"&redirect_uri={Uri.EscapeDataString("https://evil.example.com/catch")}"
            + $"&state=st-1&code_challenge={challenge}&code_challenge_method=S256");

        // NOT a redirect: sending the error to an unverified URI is how an open
        // redirector gets built (RFC 6749 §4.1.2.1)
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Null(res.Headers.Location);
        Assert.Contains("registered", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Pkce_is_required___and_must_match()
    {
        var clientId = await RegisterAsync("PKCE app");

        // no challenge at all
        var none = await Raw().GetAsync($"/api/oauth/authorize?response_type=code&client_id={clientId}"
            + $"&redirect_uri={Uri.EscapeDataString(Redirect)}&state=st-1");
        Assert.Equal(HttpStatusCode.Redirect, none.StatusCode);
        Assert.Contains("code_challenge", none.Headers.Location!.ToString());

        // the wrong verifier
        var (_, challenge) = Pkce();
        var (otherVerifier, _) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var code = await ApproveAsync(admin, await StartAsync(clientId, challenge));
        var res = await Raw().PostAsync("/api/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
                ["redirect_uri"] = Redirect, ["code_verifier"] = otherVerifier,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("code_verifier", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_code_works_once()
    {
        var clientId = await RegisterAsync("Once app");
        var (verifier, challenge) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var code = await ApproveAsync(admin, await StartAsync(clientId, challenge));

        await TokensAsync(clientId, code, verifier);
        var again = await Raw().PostAsync("/api/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = clientId,
                ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("already been used", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_code_cannot_be_redeemed_by_another_app()
    {
        var mine = await RegisterAsync("Mine");
        var theirs = await RegisterAsync("Theirs");
        var (verifier, challenge) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var code = await ApproveAsync(admin, await StartAsync(mine, challenge));

        var res = await Raw().PostAsync("/api/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["client_id"] = theirs,
                ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
            }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("another app", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deny_sends_the_app_away_with_nothing()
    {
        var clientId = await RegisterAsync("Denied app");
        var (_, challenge) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var request = await StartAsync(clientId, challenge);

        var answer = await Ok(await admin.PostAsJsonAsync($"/api/oauth/authorize/{request}",
            new { allow = false }));
        Assert.False(answer.GetProperty("allowed").GetBoolean());
        Assert.Contains("error=access_denied", answer.GetProperty("redirect").GetString());

        // and the request is spent: Deny is final
        var again = await admin.PostAsJsonAsync($"/api/oauth/authorize/{request}", new { allow = true });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_connected_app_cannot_approve_its_own_next_connection()
    {
        var clientId = await RegisterAsync("Bootstrapping app");
        var (verifier, challenge) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var code = await ApproveAsync(admin, await StartAsync(clientId, challenge));
        var access = (await TokensAsync(clientId, code, verifier)).GetProperty("access_token").GetString()!;

        // a second request, which the app then tries to approve with its own token
        var (_, challenge2) = Pkce();
        var second = await StartAsync(clientId, challenge2);
        var app = WithToken(access);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync($"/api/oauth/authorize/{second}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await app.PostAsJsonAsync($"/api/oauth/authorize/{second}", new { allow = true })).StatusCode);
    }

    // ---- what the token may do ---------------------------------------------

    [Fact]
    public async Task A_read_only_connection_can_look_but_not_send()
    {
        var clientId = await RegisterAsync("Read-only app");
        var (verifier, challenge) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var code = await ApproveAsync(admin, await StartAsync(clientId, challenge, scope: McpOAuth.ScopeRead),
            readOnly: true);
        var tokens = await TokensAsync(clientId, code, verifier);
        Assert.Equal(McpOAuth.ScopeRead, tokens.GetProperty("scope").GetString());
        var app = WithToken(tokens.GetProperty("access_token").GetString()!);

        // reading is fine
        await Ok(await app.GetAsync("/api/contacts"));
        // sending is not — the same scope rule a read-only API key gets
        var send = await app.PostAsJsonAsync("/api/messaging/notify", new
        {
            channel = "whatsapp", to = "60123456789", text = "hi", purpose = "otp",
        });
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
    }

    [Fact]
    public async Task A_full_connection_acts_as_the_person_who_allowed_it()
    {
        var clientId = await RegisterAsync("Agent's app");
        var (verifier, challenge) = Pkce();
        // an AGENT approves it, so the app gets an agent's capabilities, not an admin's
        var agent = await Platform.As(_f, "agent", "agent123");
        var code = await ApproveAsync(agent, await StartAsync(clientId, challenge));
        var app = WithToken((await TokensAsync(clientId, code, verifier)).GetProperty("access_token").GetString()!);

        var who = await Ok(await app.GetAsync("/api/auth/whoami"));
        Assert.Equal("agent", who.GetProperty("username").GetString());
        // an agent holds chats.view but not settings.ai_keys.manage: the app is bounded
        // exactly the same way
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("/api/settings/ai-providers")).StatusCode);
    }

    [Fact]
    public async Task A_connected_app_is_a_program___not_a_person_at_the_keyboard()
    {
        var clientId = await RegisterAsync("Automation");
        var (verifier, challenge) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var code = await ApproveAsync(admin, await StartAsync(clientId, challenge));
        var app = WithToken((await TokensAsync(clientId, code, verifier)).GetProperty("access_token").GetString()!);

        // Settings → My account refuses a program, exactly as it refuses an API key
        var res = await app.PostAsJsonAsync("/api/auth/me/password",
            new { current_password = "admin123", new_password = "something-else-1" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- the person stays in charge ----------------------------------------

    [Fact]
    public async Task A_person_sees_their_connections_and_can_end_one()
    {
        var clientId = await RegisterAsync("Revokable app");
        var (verifier, challenge) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var code = await ApproveAsync(admin, await StartAsync(clientId, challenge));
        var access = (await TokensAsync(clientId, code, verifier)).GetProperty("access_token").GetString()!;
        var app = WithToken(access);
        await Ok(await app.PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }));

        var mine = await Ok(await admin.GetAsync("/api/oauth/connections"));
        var row = Assert.Single(mine.GetProperty("connections").EnumerateArray()
            .Where(c => c.GetProperty("client_name").GetString() == "Revokable app"));
        Assert.False(row.GetProperty("read_only").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("last_used_at").ValueKind);

        (await admin.DeleteAsync($"/api/oauth/connections/{row.GetProperty("id").GetInt32()}"))
            .EnsureSuccessStatusCode();

        // the token stops working at once
        var after = await app.PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Another_login_cannot_see_or_end_someone_elses_connection()
    {
        var clientId = await RegisterAsync("Private app");
        var (verifier, challenge) = Pkce();
        var agent = await Platform.As(_f, "agent", "agent123");
        var code = await ApproveAsync(agent, await StartAsync(clientId, challenge));
        await TokensAsync(clientId, code, verifier);
        var id = Db(db => db.OAuthTokens.OrderByDescending(t => t.Id).First().Id);

        var admin = await Platform.As(_f, "admin", "admin123");
        var theirs = await Ok(await admin.GetAsync("/api/oauth/connections"));
        Assert.DoesNotContain(theirs.GetProperty("connections").EnumerateArray(),
            c => c.GetProperty("id").GetInt32() == id);
        // a 404, never someone else's to end
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/oauth/connections/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_app_can_hand_its_token_back()
    {
        var clientId = await RegisterAsync("Polite app");
        var (verifier, challenge) = Pkce();
        var admin = await Platform.As(_f, "admin", "admin123");
        var code = await ApproveAsync(admin, await StartAsync(clientId, challenge));
        var access = (await TokensAsync(clientId, code, verifier)).GetProperty("access_token").GetString()!;

        var revoke = await Raw().PostAsync("/api/oauth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = access }));
        revoke.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(access).PostAsJsonAsync("/api/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" })).StatusCode);

        // RFC 7009: revoking something unknown is still a 200 — it is not the caller's business
        (await Raw().PostAsync("/api/oauth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "cbo_nope_nope" })))
            .EnsureSuccessStatusCode();
    }

    // ---- registration ------------------------------------------------------

    [Theory]
    [InlineData("http://evil.example.com/catch", "127.0.0.1")]
    [InlineData("https://ok.example.com/cb#frag", "fragment")]
    [InlineData("not-a-url", "absolute")]
    public async Task A_redirect_uri_that_cannot_be_trusted_is_refused_at_registration(string uri, string why)
    {
        var res = await Raw().PostAsJsonAsync("/api/oauth/register",
            new { client_name = "Bad app", redirect_uris = new[] { uri } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(why, await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8976/callback")]   // how a desktop app receives its redirect
    [InlineData("http://localhost:3000/cb")]
    [InlineData("com.example.app:/oauth")]            // a native app's own scheme
    public async Task The_redirect_shapes_real_apps_use_are_accepted(string uri)
    {
        var res = await Raw().PostAsJsonAsync("/api/oauth/register",
            new { client_name = "Desktop app", redirect_uris = new[] { uri } });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Registration_needs_a_redirect_uri()
    {
        var res = await Raw().PostAsJsonAsync("/api/oauth/register", new { client_name = "Nowhere app" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("redirect_uris", await res.Content.ReadAsStringAsync());
    }

    // ---- the pure half ------------------------------------------------------

    [Fact]
    public void Pkce_verification_accepts_only_a_real_s256_proof()
    {
        var (verifier, challenge) = Pkce();
        Assert.True(McpOAuth.VerifyPkce(challenge, "S256", verifier));
        Assert.False(McpOAuth.VerifyPkce(challenge, "S256", Pkce().Verifier));
        // "plain" proves nothing an interceptor could not also produce
        Assert.False(McpOAuth.VerifyPkce(verifier, "plain", verifier));
        Assert.False(McpOAuth.VerifyPkce("", "S256", verifier));
        Assert.False(McpOAuth.VerifyPkce(challenge, "S256", "too-short"));
    }

    [Fact]
    public void Read_only_means_every_capability_that_changes_nothing()
    {
        var scopes = Db(db => McpOAuth.ScopesForAsync(db, Roles.Admin, readOnly: true)
            .GetAwaiter().GetResult());
        Assert.NotEmpty(scopes);
        Assert.All(scopes, s => Assert.False(ApiKeyAuth.IsWriteCapability(s), s));
        Assert.Contains("chats.view", scopes);
        Assert.DoesNotContain("chats.send", scopes);

        // "everything I can do" carries no scope list at all, like an unscoped key
        Assert.Empty(Db(db => McpOAuth.ScopesForAsync(db, Roles.Admin, readOnly: false)
            .GetAwaiter().GetResult()));
    }
}
