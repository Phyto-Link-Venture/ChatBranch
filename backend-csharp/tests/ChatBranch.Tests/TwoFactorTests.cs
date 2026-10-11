using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Security;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Two-step sign-in (TOTP, RFC 6238): enrollment with a QR code / manual key confirmed by a
/// code, 10 one-time recovery codes, the two-step login (a signed short-lived challenge,
/// then a code — never the same code twice), turning it off (password + code), an admin's
/// reset, secrets encrypted at rest, and the organization policy that holds a session to
/// the setup routes until it is done. API keys are never affected.
/// </summary>
public class TwoFactorTests : IClassFixture<SecurityFactory>
{
    private readonly SecurityFactory _f;
    public TwoFactorTests(SecurityFactory f) => _f = f;

    private static int _peer;
    private static string NewPeer() => $"192.0.2.{Interlocked.Increment(ref _peer) % 250 + 1}";

    private string NewUser(string role = Roles.Agent, string pass = "right-pass-1")
    {
        var user = "tf_" + Guid.NewGuid().ToString("N")[..10];
        SettingsTestKit.CreateUser(_f, user, pass, role);
        return user;
    }

    private static string CodeFor(string manualKey, int stepOffset = 0) =>
        Totp.Code(Base32.Decode(manualKey)!, Totp.StepAt(DateTimeOffset.UtcNow) + stepOffset);

    /// <summary>Set up and confirm two-step sign-in; returns the manual key and the recovery codes.</summary>
    private static async Task<(string Key, List<string> Codes)> Enroll(HttpClient c)
    {
        var setup = await c.PostAsJsonAsync("/api/auth/me/2fa/setup", new { });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var s = await setup.Content.ReadFromJsonAsync<JsonElement>();
        var key = s.GetProperty("manual_key").GetString()!.Replace(" ", "");
        var confirm = await c.PostAsJsonAsync("/api/auth/me/2fa/confirm", new { code = CodeFor(key) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var codes = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recovery_codes")
            .EnumerateArray().Select(x => x.GetString()!).ToList();
        return (key, codes);
    }

    [Fact]
    public void Totp_matches_the_rfc_6238_vectors_and_base32_round_trips()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("287082", Totp.Code(secret, 59 / 30));           // RFC 6238: 94287082
        Assert.Equal("081804", Totp.Code(secret, 1111111109 / 30));   // 07081804
        Assert.Equal("005924", Totp.Code(secret, 1234567890 / 30));   // 89005924
        Assert.Equal("279037", Totp.Code(secret, 2000000000 / 30));   // 69279037

        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        Assert.Equal(1111111109 / 30, Totp.Match(secret, "081 804", now, 0));
        Assert.Null(Totp.Match(secret, "081804", now, lastUsedStep: 1111111109 / 30)); // replay
        Assert.Equal(1111111109 / 30 - 1, Totp.Match(secret, Totp.Code(secret, 1111111109 / 30 - 1), now, 0)); // drift
        Assert.Null(Totp.Match(secret, Totp.Code(secret, 1111111109 / 30 - 2), now, 0));

        var raw = Totp.NewSecret();
        Assert.Equal(raw, Base32.Decode(Base32.Encode(raw)));
        Assert.Equal("JBSWY3DPEHPK3PXP", Base32.Encode(new byte[] { 0x48, 0x65, 0x6c, 0x6c, 0x6f, 0x21, 0xde, 0xad, 0xbe, 0xef }));
        Assert.Contains("secret=", Totp.ProvisioningUri("ChatBranch", "amy", raw));
    }

    [Fact]
    public async Task Enrolment_needs_a_code_then_sign_in_takes_two_steps_and_a_code_works_once()
    {
        var user = NewUser();
        var peer = NewPeer();
        var c = await _f.SignedIn(user, "right-pass-1", peer);

        // setup: a QR code + manual key, nothing on until a code confirms it
        var setup = await (await c.PostAsJsonAsync("/api/auth/me/2fa/setup", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("otpauth://totp/", setup.GetProperty("otpauth_uri").GetString());
        Assert.Contains("<svg", setup.GetProperty("qr_svg").GetString());
        var key = setup.GetProperty("manual_key").GetString()!.Replace(" ", "");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/auth/me/2fa/confirm", new { code = "000000" })).StatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/auth/me/2fa")).GetProperty("enabled").GetBoolean());

        var confirm = await c.PostAsJsonAsync("/api/auth/me/2fa/confirm", new { code = CodeFor(key) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var codes = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recovery_codes");
        Assert.Equal(10, codes.GetArrayLength());
        var status = await c.GetFromJsonAsync<JsonElement>("/api/auth/me/2fa");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(10, status.GetProperty("recovery_codes_left").GetInt32());
        Assert.DoesNotContain(key, status.GetRawText());

        // the secret is encrypted at rest and the recovery codes are only hashes
        var uid = SettingsTestKit.UserId(_f, user);
        var row = SettingsTestKit.WithDb(_f, db => db.UserTwoFactors.Single(t => t.UserId == uid));
        Assert.DoesNotContain(key, row.Secret);
        Assert.DoesNotContain(codes[0].GetString()!, string.Join(",", row.RecoveryCodes));

        // sign-in: the password alone gives a challenge, not a token
        var anon = _f.From(peer);
        var step1 = await anon.PostAsJsonAsync("/api/auth/login", new { username = user, password = "right-pass-1" });
        Assert.Equal(HttpStatusCode.OK, step1.StatusCode);
        var s1 = await step1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(s1.GetProperty("two_factor_required").GetBoolean());
        Assert.False(s1.TryGetProperty("token", out _));
        var challenge = s1.GetProperty("challenge").GetString();

        var bad = await anon.PostAsJsonAsync("/api/auth/login/2fa", new { challenge, code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("invalid_code", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // the confirm used this 30-second step; the next step's code is within the ±1 drift
        var next = CodeFor(key, +1);
        var step2 = await anon.PostAsJsonAsync("/api/auth/login/2fa", new { challenge, code = next });
        Assert.Equal(HttpStatusCode.OK, step2.StatusCode);
        var token = (await step2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var signedIn = _f.From(peer);
        signedIn.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/auth/whoami")).StatusCode);

        // a challenge signs in once; a code is never accepted twice
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/auth/login/2fa", new { challenge, code = next })).StatusCode);
        var again = await (await anon.PostAsJsonAsync("/api/auth/login", new { username = user, password = "right-pass-1" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var replay = await anon.PostAsJsonAsync("/api/auth/login/2fa",
            new { challenge = again.GetProperty("challenge").GetString(), code = next });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // a forged / garbled challenge is just "expired"
        var forged = await anon.PostAsJsonAsync("/api/auth/login/2fa", new { challenge = "CfDJ8-not-a-challenge", code = next });
        Assert.Equal("challenge_expired", (await forged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        var user = NewUser();
        var c = await _f.SignedIn(user, "right-pass-1", NewPeer());
        var (_, codes) = await Enroll(c);
        var anon = _f.From(NewPeer());

        async Task<HttpResponseMessage> WithCode(string code)
        {
            var s1 = await (await anon.PostAsJsonAsync("/api/auth/login", new { username = user, password = "right-pass-1" }))
                .Content.ReadFromJsonAsync<JsonElement>();
            return await anon.PostAsJsonAsync("/api/auth/login/2fa", new { challenge = s1.GetProperty("challenge").GetString(), code });
        }
        var first = await WithCode(codes[3].ToUpperInvariant().Replace("-", " "));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(9, (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recovery_codes_left").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithCode(codes[3])).StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_needs_the_password_and_a_code_and_api_keys_cant_touch_it()
    {
        var user = NewUser();
        var c = await _f.SignedIn(user, "right-pass-1", NewPeer());
        var (key, codes) = await Enroll(c);

        var wrongPw = await c.PostAsJsonAsync("/api/auth/me/2fa/disable", new { password = "nope-nope-1", code = codes[0] });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPw.StatusCode);
        Assert.Equal("wrong_password", (await wrongPw.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var wrongCode = await c.PostAsJsonAsync("/api/auth/me/2fa/disable", new { password = "right-pass-1", code = "000000" });
        Assert.Equal("invalid_code", (await wrongCode.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // an API key acts as its owner but is not the owner holding the phone
        var keyed = _f.From(NewPeer());
        keyed.DefaultRequestHeaders.Add("X-Api-Key", Platform.MintKey(_f, user));
        Assert.Equal(HttpStatusCode.Forbidden, (await keyed.PostAsJsonAsync("/api/auth/me/2fa/setup", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await keyed.PostAsJsonAsync("/api/auth/me/2fa/disable", new { password = "right-pass-1", code = codes[1] })).StatusCode);
        // …and still works while the person has two-step sign-in on
        Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/api/auth/whoami")).StatusCode);

        var off = await c.PostAsJsonAsync("/api/auth/me/2fa/disable", new { password = "right-pass-1", code = codes[1] });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var login = await (await _f.From(NewPeer()).PostAsJsonAsync("/api/auth/login",
            new { username = user, password = "right-pass-1" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(login.TryGetProperty("token", out _)); // one step again
        _ = key;
    }

    [Fact]
    public async Task An_admin_resets_an_agents_two_step_sign_in_and_signs_them_out()
    {
        var user = NewUser();
        var c = await _f.SignedIn(user, "right-pass-1", NewPeer());
        await Enroll(c);
        var id = SettingsTestKit.UserId(_f, user);

        var list = await (await _f.SignedIn("admin", "admin123", NewPeer())).GetFromJsonAsync<JsonElement>("/api/v1/users");
        Assert.True(list.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == id)
            .GetProperty("two_factor").GetBoolean());

        // an agent can't; nobody resets their own here
        var agent = await _f.SignedIn("agent", "agent123", NewPeer());
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/api/v1/users/{id}/2fa/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/v1/users/{id}/2fa/reset", null)).StatusCode);

        var admin = await _f.SignedIn("admin", "admin123", NewPeer());
        var reset = await admin.PostAsync($"/api/v1/users/{id}/2fa/reset", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False((await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("two_factor").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/whoami")).StatusCode); // signed out
        var login = await (await _f.From(NewPeer()).PostAsJsonAsync("/api/auth/login",
            new { username = user, password = "right-pass-1" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(login.TryGetProperty("token", out _));
        Assert.Contains(await Platform.Audit(admin, "action=user.2fa_reset"), r => r.GetProperty("target").GetString() == user);

        // admins can't reset another admin's (same tiering as every user edit)
        var otherAdmin = NewUser(Roles.Admin);
        var oc = await _f.SignedIn(otherAdmin, "right-pass-1", NewPeer());
        await Enroll(oc);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsync($"/api/v1/users/{SettingsTestKit.UserId(_f, otherAdmin)}/2fa/reset", null)).StatusCode);
    }

    /// <summary>Write the policy straight to the database (a superadmin who saved "admins" is
    /// held to setup themselves, so the test moves the policy on without them).</summary>
    private void SetPolicy(string value)
    {
        SettingsTestKit.WithDb(_f, db =>
        {
            db.Settings.RemoveRange(db.Settings.Where(x => x.Key == TwoFactorService.PolicyKey && x.EntityId == null));
            if (value != TwoFactorService.PolicyOff)
                db.Settings.Add(new Setting { Key = TwoFactorService.PolicyKey, Value = value, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        });
        TwoFactorService.Invalidate();
    }

    [Fact]
    public async Task The_policy_holds_an_unenrolled_session_to_setup_until_it_is_done()
    {
        var super = await _f.SignedIn("superadmin", "super123", NewPeer());
        // security.manage is the superadmin's by default — an admin is refused
        var admin = await _f.SignedIn("admin", "admin123", NewPeer());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/settings/security")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PutAsJsonAsync("/api/settings/security", new { require_2fa = "off" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await super.PutAsJsonAsync("/api/settings/security", new { require_2fa = "sometimes" })).StatusCode);
        var before = await super.GetFromJsonAsync<JsonElement>("/api/settings/security");
        Assert.Equal("off", before.GetProperty("require_2fa").GetString());

        var user = NewUser(Roles.Agent);
        var c = await _f.SignedIn(user, "right-pass-1", NewPeer());
        var keyed = _f.From(NewPeer());
        keyed.DefaultRequestHeaders.Add("X-Api-Key", Platform.MintKey(_f, user));
        try
        {
            // admins only: the agent is untouched — the superadmin who saved it is now held to setup
            var put = await super.PutAsJsonAsync("/api/settings/security", new { require_2fa = "admins" });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal("admins", (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("require_2fa").GetString());
            TwoFactorService.Invalidate();
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/contacts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await super.GetAsync("/api/contacts")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await super.GetAsync("/api/auth/me/2fa")).StatusCode);

            // everyone: the agent's open session reaches only the setup routes
            SetPolicy(TwoFactorService.PolicyEveryone);
            var refused = await c.GetAsync("/api/contacts");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(SessionGate.Code, body.GetProperty("code").GetString());
            Assert.Equal(SessionRestrictions.EnrollTwoFactor, body.GetProperty("restriction").GetString());
            var who = await c.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
            Assert.Equal(SessionRestrictions.EnrollTwoFactor, who.GetProperty("restriction").GetString());
            // a fresh sign-in says so straight away
            var login = await (await _f.From(NewPeer()).PostAsJsonAsync("/api/auth/login",
                new { username = user, password = "right-pass-1" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(login.GetProperty("two_factor_setup_required").GetBoolean());
            // API keys are never held back
            Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/api/contacts")).StatusCode);

            // setting it up opens everything; turning it off is refused while required
            var (_, codes) = await Enroll(c);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/contacts")).StatusCode);
            var off = await c.PostAsJsonAsync("/api/auth/me/2fa/disable", new { password = "right-pass-1", code = codes[0] });
            Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        }
        finally
        {
            SetPolicy(TwoFactorService.PolicyOff);
        }

        var settings = await super.GetFromJsonAsync<JsonElement>("/api/settings/security");
        Assert.Equal("off", settings.GetProperty("require_2fa").GetString());
        Assert.True(settings.GetProperty("users_enrolled").GetInt32() >= 1);
        Assert.Contains(await Platform.Audit(super, "action=security.policy"),
            r => r.GetProperty("detail").GetString()!.Contains("admins"));
    }
}
