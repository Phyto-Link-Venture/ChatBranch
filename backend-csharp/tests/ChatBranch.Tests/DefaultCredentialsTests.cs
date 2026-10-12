using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api.Security;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// A real install (no SEED_DEMO): signing in with a seeded password works but the session
/// may do nothing except change it; new passwords meet the policy (≥ 10 characters, not
/// the username, not a common password). SUPERADMIN_PASSWORD is never forced to change.
/// </summary>
public class DefaultCredentialsTests : IClassFixture<RealInstallFactory>
{
    private readonly RealInstallFactory _f;
    public DefaultCredentialsTests(RealInstallFactory f) => _f = f;

    private static int _peer;
    private static string NewPeer() => $"198.51.100.{Interlocked.Increment(ref _peer) % 250 + 1}";

    [Fact]
    public async Task A_seeded_password_signs_in_to_a_change_password_screen_only()
    {
        // the seed's agent login (agent123) — the superadmin is used by the other tests here
        var c = _f.From(NewPeer());
        var login = await c.PostAsJsonAsync("/api/auth/login", new { username = "agent", password = "agent123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("must_change_password").GetBoolean());
        Assert.Equal(SessionRestrictions.ChangePassword, body.GetProperty("restriction").GetString());
        c.DefaultRequestHeaders.Authorization = new("Bearer", body.GetProperty("token").GetString());

        var refused = await c.GetAsync("/api/contacts");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(SessionGate.Code, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(SessionRestrictions.ChangePassword,
            (await c.GetFromJsonAsync<JsonElement>("/api/auth/whoami")).GetProperty("restriction").GetString());

        // the new password must meet the policy
        async Task<string?> Try(string next)
        {
            var r = await c.PostAsJsonAsync("/api/auth/me/password", new { current_password = "agent123", new_password = next });
            return r.IsSuccessStatusCode ? null : (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
        }
        Assert.Equal("password_too_short", await Try("Short-1"));
        Assert.Equal("password_common", await Try("password123"));
        Assert.Equal("password_common", await Try("Admin123456"));
        Assert.Null(await Try("kopi-ais-tarik-42"));

        // the same session is a normal one now; the new password signs in unrestricted
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/contacts")).StatusCode);
        var fresh = await (await _f.From(NewPeer()).PostAsJsonAsync("/api/auth/login",
            new { username = "agent", password = "kopi-ais-tarik-42" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(fresh.GetProperty("must_change_password").GetBoolean());
        Assert.Equal("", fresh.GetProperty("restriction").GetString());
    }

    [Fact]
    public async Task Api_keys_are_never_held_to_the_password_change()
    {
        var keyed = _f.From(NewPeer());
        keyed.DefaultRequestHeaders.Add("X-Api-Key", Platform.MintKey(_f, "admin"));
        Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/api/contacts")).StatusCode);
    }

    [Fact]
    public async Task New_users_on_a_real_install_get_the_stricter_rule()
    {
        // admin's seeded password must change first — change it, then manage users
        var c = _f.From(NewPeer());
        var token = (await (await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/me/password",
            new { current_password = "admin123", new_password = "teh-tarik-kurang-manis" })).StatusCode);

        async Task<(HttpStatusCode, string?)> Create(string pw)
        {
            var r = await c.PostAsJsonAsync("/api/v1/users",
                new { username = "rp_" + Guid.NewGuid().ToString("N")[..8], password = pw, role = Roles.Agent });
            var b = await r.Content.ReadFromJsonAsync<JsonElement>();
            return (r.StatusCode, b.TryGetProperty("error", out var e) && e.TryGetProperty("reason", out var why) ? why.GetString() : null);
        }
        Assert.Equal((HttpStatusCode.BadRequest, "password_too_short"), await Create("seven-c"));
        Assert.Equal((HttpStatusCode.BadRequest, "password_common"), await Create("qwerty12345"));
        Assert.Equal(HttpStatusCode.Created, (await Create("roti-canai-banjir")).Item1);
    }

    [Fact]
    public void The_password_policy_rules()
    {
        var real = new SecurityOptions { IsDemo = false };
        var demo = new SecurityOptions { IsDemo = true };
        Assert.Equal("password_too_short", PasswordPolicy.Check("kopi-ai", "amy", real)?.Code);  // 7 characters
        Assert.Null(PasswordPolicy.Check("kopi-ais", "amy", real));                 // 8 is enough
        Assert.Null(PasswordPolicy.Check("abcdefgh1", "amy", demo));               // a demo checks only the length
        Assert.Equal("password_too_short", PasswordPolicy.Check("abc1234", "amy", demo)?.Code);
        Assert.Equal("password_is_username", PasswordPolicy.Check("Amy.Tan.2026", "amy.tan.2026", real)?.Code);
        Assert.Equal("password_common", PasswordPolicy.Check("Password123!", "amy", real)?.Code);
        Assert.Equal("password_common", PasswordPolicy.Check("1234567890", "amy", real)?.Code);
        Assert.Equal("password_common", PasswordPolicy.Check("malaysia2026", "amy", real)?.Code);
        Assert.Equal("password_common", PasswordPolicy.Check("aaaaaaaaaaaa", "amy", real)?.Code);
        Assert.Equal("password_common", PasswordPolicy.Check("super123", "amy", new SecurityOptions { IsDemo = false, PasswordMinLength = 8 })?.Code);
        Assert.Null(PasswordPolicy.Check("nasi-lemak-pedas", "amy", real));
        Assert.Equal(12, PasswordPolicy.Check("short", "amy", new SecurityOptions { IsDemo = false, PasswordMinLength = 12 })?.Min);
    }
}

/// <summary>The operator's own SUPERADMIN_PASSWORD is never forced to change.</summary>
public class ChosenSuperadminPasswordTests : IClassFixture<ChosenSuperadminPasswordTests.Factory>
{
    public class Factory : SecurityFactory
    {
        // the seed made the superadmin with the POC password; the operator set exactly that on purpose
        protected override SecurityOptions Options => new() { IsDemo = false, SuperadminPassword = "super123" };
    }

    private readonly Factory _f;
    public ChosenSuperadminPasswordTests(Factory f) => _f = f;

    [Fact]
    public async Task Signing_in_with_the_chosen_password_is_not_restricted()
    {
        var res = await _f.From("198.51.100.250").PostAsJsonAsync("/api/auth/login",
            new { username = "superadmin", password = "super123" });
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("must_change_password").GetBoolean());
        // a seeded password that is NOT the chosen one (admin123) still must change
        var admin = await (await _f.From("198.51.100.249").PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "admin123" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(admin.GetProperty("must_change_password").GetBoolean());
    }

    [Fact]
    public async Task A_demo_never_restricts_the_seeded_logins()
    {
        // the suite's own host IS a demo (SEED_DEMO=1): the POC logins work as before
        await using var demo = new SecurityFactory();
        var body = await (await demo.From("198.51.100.248").PostAsJsonAsync("/api/auth/login",
            new { username = "superadmin", password = "super123" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("must_change_password").GetBoolean());
        Assert.Equal("", body.GetProperty("restriction").GetString());
    }
}
