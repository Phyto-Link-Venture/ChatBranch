using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Settings → My account (/api/auth/me/*): a person edits only THEIR OWN name,
/// email, password and sessions; API keys can't; wrong passwords are throttled.
/// Each test uses its own login so the shared host's counters never collide.
/// </summary>
public class MyAccountTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public MyAccountTests(ApiFactory f) => _f = f;

    private async Task<(HttpClient Client, string User)> NewLogin(string role = Roles.Agent, string pass = "start-pass-1")
    {
        var user = "me_" + Guid.NewGuid().ToString("N")[..10];
        SettingsTestKit.CreateUser(_f, user, pass, role);
        return (await SettingsTestKit.Client(_f, user, pass), user);
    }

    [Fact]
    public async Task Changes_own_name_and_email_and_the_shell_name_follows()
    {
        var (c, user) = await NewLogin();
        var before = await c.GetFromJsonAsync<JsonElement>("/api/auth/me/account");
        Assert.Equal(user, before.GetProperty("username").GetString());
        Assert.Equal(user, before.GetProperty("name").GetString()); // no name yet → username
        Assert.Equal(1, before.GetProperty("active_sessions").GetInt32());
        Assert.Equal(Roles.Agent, before.GetProperty("role").GetString());

        var res = await c.PatchAsJsonAsync("/api/auth/me/account",
            new { first_name = " Aisyah ", last_name = "Rahman", email = $"{user}@example.com" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var after = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Aisyah", after.GetProperty("first_name").GetString());
        Assert.Equal("Aisyah Rahman", after.GetProperty("name").GetString());
        Assert.Equal($"{user}@example.com", after.GetProperty("email").GetString());

        var who = await c.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        Assert.Equal("Aisyah Rahman", who.GetProperty("name").GetString());
        Assert.Equal(user, who.GetProperty("username").GetString());

        // a bad email is a 400, a taken one a 409, the username never moves
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PatchAsJsonAsync("/api/auth/me/account", new { email = "not-an-email" })).StatusCode);
        var (other, _) = await NewLogin();
        Assert.Equal(HttpStatusCode.Conflict,
            (await other.PatchAsJsonAsync("/api/auth/me/account", new { email = $"{user.ToUpperInvariant()}@EXAMPLE.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PatchAsJsonAsync("/api/auth/me/account", new { username = "someone-else" })).StatusCode);
    }

    [Fact]
    public async Task Wrong_current_password_is_400_then_429_after_the_limit()
    {
        var (c, _) = await NewLogin();
        for (var i = 0; i < 5; i++)
        {
            var res = await c.PostAsJsonAsync("/api/auth/me/password",
                new { current_password = "wrong-" + i, new_password = "another-pass-9" });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Equal("current_password", (await res.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("field").GetString());
        }
        // throttled now — even the right password waits
        var limited = await c.PostAsJsonAsync("/api/auth/me/password",
            new { current_password = "start-pass-1", new_password = "another-pass-9" });
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
    }

    [Fact]
    public async Task Short_or_unchanged_new_password_is_rejected()
    {
        var (c, _) = await NewLogin();
        var shortPw = await c.PostAsJsonAsync("/api/auth/me/password",
            new { current_password = "start-pass-1", new_password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPw.StatusCode);
        Assert.Equal("new_password", (await shortPw.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("field").GetString());
        var same = await c.PostAsJsonAsync("/api/auth/me/password",
            new { current_password = "start-pass-1", new_password = "start-pass-1" });
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
    }

    [Fact]
    public async Task Success_signs_out_other_sessions_but_keeps_this_one()
    {
        var (c, user) = await NewLogin();
        var laptop = await SettingsTestKit.Client(_f, user, "start-pass-1");
        var phone = await SettingsTestKit.Client(_f, user, "start-pass-1");
        Assert.Equal(3, (await c.GetFromJsonAsync<JsonElement>("/api/auth/me/account"))
            .GetProperty("active_sessions").GetInt32());

        var res = await c.PostAsJsonAsync("/api/auth/me/password",
            new { current_password = "start-pass-1", new_password = "brand-new-pass-7" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signed_out").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/auth/me/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/auth/me/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me/account")).StatusCode);

        // the new password signs in, the old one doesn't
        var fresh = _f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostAsJsonAsync("/api/auth/login",
            new { username = user, password = "brand-new-pass-7" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/auth/login",
            new { username = user, password = "start-pass-1" })).StatusCode);

        // audited without the secret
        var admin = await Platform.As(_f, "admin", "admin123");
        var rows = await Platform.Audit(admin, "action=user.password_changed");
        var row = Assert.Single(rows, r => r.GetProperty("target").GetString() == user);
        var text = row.GetRawText();
        Assert.DoesNotContain("brand-new-pass-7", text);
        Assert.DoesNotContain("start-pass-1", text);

        // "Sign out other devices" on its own
        var again = await SettingsTestKit.Client(_f, user, "brand-new-pass-7");
        var revoke = await c.PostAsync("/api/auth/me/sessions/revoke-others", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var body = await revoke.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("signed_out").GetInt32()); // `again` + the new-password sign-in above
        Assert.Equal(1, body.GetProperty("active_sessions").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.GetAsync("/api/auth/me/account")).StatusCode);
    }

    [Fact]
    public async Task An_api_key_cannot_change_the_password_or_the_account()
    {
        var (_, user) = await NewLogin();
        var key = Platform.Client(_f, Platform.MintKey(_f, user));
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/auth/me/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await key.PostAsJsonAsync("/api/auth/me/password",
            new { current_password = "start-pass-1", new_password = "key-chosen-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await key.PostAsync("/api/auth/me/sessions/revoke-others", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await key.PatchAsJsonAsync("/api/auth/me/account", new { first_name = "Key" })).StatusCode);
    }

    [Fact]
    public async Task One_user_never_changes_another_users_account()
    {
        var (alice, aliceUser) = await NewLogin(Roles.Admin);
        var (_, bobUser) = await NewLogin();
        // there is no id to aim at: extra keys are ignored, the caller is always the target
        var res = await alice.PatchAsJsonAsync("/api/auth/me/account",
            new { first_name = "Alice", user_id = SettingsTestKit.UserId(_f, bobUser), username = aliceUser });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(aliceUser, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());

        var bob = await SettingsTestKit.Client(_f, bobUser, "start-pass-1");
        var bobView = await bob.GetFromJsonAsync<JsonElement>("/api/auth/me/account");
        Assert.Equal("", bobView.GetProperty("first_name").GetString());

        // alice's own password works for alice only; bob's password is untouched
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/auth/me/password",
            new { current_password = "start-pass-1", new_password = "alice-new-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = bobUser, password = "start-pass-1" })).StatusCode);

        // unauthenticated → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/auth/me/account")).StatusCode);
    }
}
