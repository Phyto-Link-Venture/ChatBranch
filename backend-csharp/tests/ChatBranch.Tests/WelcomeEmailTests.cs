using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Api.Security;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A host whose SMTP is faked and whose dashboard lives at <see cref="BaseUrl"/> (a demo by default).</summary>
public class InviteFactory : SecurityFactory
{
    public FakeEmailTransport Email { get; } = new();
    protected virtual string BaseUrl => "https://chat.example.test";
    protected virtual bool Demo => true;
    protected override SecurityOptions Options => new() { RateLimitEnabled = false, IsDemo = Demo, LoginBaseUrl = BaseUrl };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IEmailTransport>(Email));
    }
}

/// <summary>No FRONTEND_URL / PUBLIC_BASE_URL: invites must be refused with a reason.</summary>
public class NoLoginAddressFactory : InviteFactory
{
    protected override string BaseUrl => "";
}

/// <summary>A real install (no SEED_DEMO): the strict password rule, seeded passwords must change.</summary>
public class RealInstallInviteFactory : InviteFactory
{
    protected override bool Demo => false;
}

/// <summary>Shared steps for the welcome-email suites.</summary>
internal static class Invites
{
    public static readonly Regex LinkToken = new(@"/set-password\?token=([A-Za-z0-9_-]+)");

    public static async Task ConfigureSystemEmail(HttpClient super, string host = "smtp.acme.test")
    {
        var res = await super.PutAsJsonAsync("/api/settings/system-email", new
        {
            from_name = "Acme Support", from_email = "noreply@acme.test", host, port = 587,
            security = "starttls", username = "mailer", password = "smtp-secret-pass",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    public static async Task ClearSystemEmail(HttpClient super)
    {
        var res = await super.PutAsJsonAsync("/api/settings/system-email", new
        {
            from_name = "", from_email = "", host = "", port = 587, security = "starttls", username = "", password = "",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    public static async Task SetDefaultMail(HttpClient super, string host, string from)
    {
        var res = await super.PutAsJsonAsync("/api/messaging/settings", new { smtp_host = host, smtp_from = from });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    public static string Text(MimeMessage m) => m.TextBody ?? "";
    public static string Html(MimeMessage m) => m.HtmlBody ?? "";

    public static string TokenOf(MimeMessage m)
    {
        var hit = LinkToken.Match(Text(m));
        Assert.True(hit.Success, "the email carries no set-password link:\n" + Text(m));
        return hit.Groups[1].Value;
    }

    public static async Task<JsonElement> Body(HttpResponseMessage r) =>
        await r.Content.ReadFromJsonAsync<JsonElement>();

    public static string NewName(string prefix = "wel") => prefix + "_" + Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// The welcome email: creating a user emails them their username, the login address and a
/// one-time link to choose their own password (never a password); the link works once, for
/// 72 hours, and a resend, a deactivation or an email change revokes it; setting the password
/// applies the policy and signs every session out; an admin can resend (or send a reset link)
/// within the user tiers, at most 5 an hour; Settings → System email round-trips its password
/// masked, falls back to the default mail settings and sends a test to the caller.
/// </summary>
public class WelcomeEmailTests : IClassFixture<InviteFactory>
{
    private readonly InviteFactory _f;
    public WelcomeEmailTests(InviteFactory f) => _f = f;

    private static int _peer;
    private static string NewPeer() => $"198.18.0.{Interlocked.Increment(ref _peer) % 250 + 1}";

    private async Task<HttpClient> Super()
    {
        var c = await _f.SignedIn("superadmin", "super123", NewPeer());
        await Invites.SetDefaultMail(c, "", "");
        await Invites.ConfigureSystemEmail(c);
        _f.Email.Reset();
        return c;
    }

    private async Task<(int Id, MimeMessage Mail)> CreateInvited(HttpClient by, string username, string email,
        object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["username"] = username, ["email"] = email, ["role"] = Roles.Agent, ["name"] = "Aina Rahman" };
        if (extra != null)
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var res = await by.PostAsJsonAsync("/api/v1/users", body);
        var json = await Invites.Body(res);
        Assert.True(res.StatusCode == HttpStatusCode.Created, json.GetRawText());
        Assert.True(json.GetProperty("invite").GetProperty("sent").GetBoolean(), json.GetRawText());
        var mail = _f.Email.Sent.Last(s => s.Message.To.Mailboxes.Any(m => m.Address == email)).Message;
        return (json.GetProperty("id").GetInt32(), mail);
    }

    private async Task<HttpResponseMessage> SetPassword(string token, string password, string? peer = null) =>
        await _f.From(peer ?? NewPeer()).PostAsJsonAsync("/api/auth/set-password", new { token, password });

    private async Task<HttpStatusCode> Login(string user, string pass) =>
        (await _f.From(NewPeer()).PostAsJsonAsync("/api/auth/login", new { username = user, password = pass })).StatusCode;

    [Fact]
    public async Task Creating_a_user_with_an_email_sends_one_welcome_email_whose_link_works_once()
    {
        var super = await Super();
        var user = Invites.NewName();
        var email = $"{user}@example.test";
        var res = await super.PostAsJsonAsync("/api/v1/users", new { username = user, email, name = "Aina Rahman", role = Roles.Agent });
        var json = await Invites.Body(res);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        // no password needed: the invite went out, the login waits for its person
        var invite = json.GetProperty("invite");
        Assert.True(invite.GetProperty("sent").GetBoolean());
        var expires = invite.GetProperty("expires_at").GetDateTime().ToUniversalTime();
        Assert.InRange((expires - DateTime.UtcNow).TotalHours, 71.5, 72.1);
        Assert.False(json.GetProperty("password_set").GetBoolean());
        Assert.Equal(email, json.GetProperty("email").GetString());
        Assert.Equal("pending", json.GetProperty("invite_link").GetProperty("status").GetString());
        Assert.Equal("Aina Rahman", json.GetProperty("name").GetString());

        // exactly one email, to that address, through the System email
        var sent = _f.Email.Sent.ToList();
        Assert.Single(sent);
        var (server, mail) = sent[0];
        Assert.Equal("smtp.acme.test", server.Host);
        Assert.Equal("starttls", server.TlsMode);
        Assert.Equal(email, mail.To.Mailboxes.Single().Address);
        Assert.Equal("noreply@acme.test", mail.From.Mailboxes.Single().Address);
        Assert.Equal("Acme Support", mail.From.Mailboxes.Single().Name);
        var text = Invites.Text(mail);
        Assert.Contains("Hello Aina Rahman,", text);
        Assert.Contains($"Your username: {user}", text);
        Assert.Contains("Sign in at: https://chat.example.test/login", text);
        Assert.Contains("If you didn't expect this email", text);
        Assert.Contains("https://chat.example.test/set-password?token=", text);
        Assert.Contains("superadmin added you to", mail.Subject);
        Assert.Contains("Choose my password", Invites.Html(mail)); // the button
        Assert.Equal("auto-generated", mail.Headers["Auto-Submitted"]);
        var token = Invites.TokenOf(mail);
        Assert.Equal(43, token.Length);

        // only the hash is stored
        SettingsTestKit.WithDb(_f, db =>
        {
            var row = db.PasswordTokens.Single(t => t.UserId == json.GetProperty("id").GetInt32());
            Assert.NotEqual(token, row.TokenHash);
            Assert.Equal(PasswordLinks.Hash(token), row.TokenHash);
            Assert.Equal(PasswordTokenPurposes.Invite, row.Purpose);
        });

        // nobody signs in before choosing a password (and the answer is the generic one)
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(user, ""));
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(user, "anything-at-all"));

        var peek = await _f.From(NewPeer()).GetAsync($"/api/auth/set-password/{token}");
        Assert.Equal(HttpStatusCode.OK, peek.StatusCode);
        var info = await Invites.Body(peek);
        Assert.Equal(user, info.GetProperty("username").GetString());
        Assert.Equal("invite", info.GetProperty("purpose").GetString());

        // the policy applies (a demo: 8 characters) — and a refusal doesn't spend the link
        var weak = await SetPassword(token, "short");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("password_too_short", (await Invites.Body(weak)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.OK, (await SetPassword(token, "kopi-o-kosong-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Login(user, "kopi-o-kosong-1"));

        // once only
        Assert.Equal(HttpStatusCode.NotFound, (await SetPassword(token, "another-pass-22")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.From(NewPeer()).GetAsync($"/api/auth/set-password/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Login(user, "kopi-o-kosong-1"));

        // the list now shows a person with a password and no open link; both steps are audited
        var list = await super.GetFromJsonAsync<JsonElement>("/api/v1/users");
        var row2 = list.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("username").GetString() == user);
        Assert.True(row2.GetProperty("password_set").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row2.GetProperty("invite_link").ValueKind);
        SettingsTestKit.WithDb(_f, db =>
        {
            Assert.True(db.AuditLogs.Any(a => a.Action == "user.invite_sent" && a.Target == user));
            Assert.True(db.AuditLogs.Any(a => a.Action == "user.password_set" && a.Target == user));
        });
    }

    [Fact]
    public async Task The_email_never_contains_a_password()
    {
        var super = await Super();
        var user = Invites.NewName();
        const string password = "nasi-lemak-sambal-9";
        var (_, mail) = await CreateInvited(super, user, $"{user}@example.test", new { password, send_invite = true });
        Assert.DoesNotContain(password, Invites.Text(mail));
        Assert.DoesNotContain(password, Invites.Html(mail));
        Assert.DoesNotContain(password, mail.ToString());
        // the password an admin set works; the link lets them choose their own instead
        Assert.Equal(HttpStatusCode.OK, await Login(user, password));
        Assert.Equal(HttpStatusCode.OK, (await SetPassword(Invites.TokenOf(mail), "my-own-choice-77")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(user, password));
        Assert.Equal(HttpStatusCode.OK, await Login(user, "my-own-choice-77"));
    }

    [Fact]
    public async Task A_resend_revokes_the_older_link_and_deactivation_revokes_them_all()
    {
        var super = await Super();
        var user = Invites.NewName();
        var email = $"{user}@example.test";
        var (id, first) = await CreateInvited(super, user, email);
        var oldToken = Invites.TokenOf(first);

        var resend = await super.PostAsJsonAsync($"/api/v1/users/{id}/invite", new { });
        var body = await Invites.Body(resend);
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        Assert.True(body.GetProperty("invite").GetProperty("sent").GetBoolean());
        var newToken = Invites.TokenOf(_f.Email.Sent.Last().Message);
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.From(NewPeer()).GetAsync($"/api/auth/set-password/{oldToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.From(NewPeer()).GetAsync($"/api/auth/set-password/{newToken}")).StatusCode);

        // a resend with no body works too
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsync($"/api/v1/users/{id}/invite", null)).StatusCode);
        var latest = Invites.TokenOf(_f.Email.Sent.Last().Message);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.From(NewPeer()).GetAsync($"/api/auth/set-password/{newToken}")).StatusCode);

        // changing the address revokes what went to the old one
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsJsonAsync($"/api/v1/users/{id}", new { email = $"new-{email}" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.From(NewPeer()).GetAsync($"/api/auth/set-password/{latest}")).StatusCode);

        // deactivation revokes
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsync($"/api/v1/users/{id}/invite", null)).StatusCode);
        var beforeDeactivate = Invites.TokenOf(_f.Email.Sent.Last().Message);
        Assert.Equal($"new-{email}", _f.Email.Sent.Last().Message.To.Mailboxes.Single().Address);
        Assert.Equal(HttpStatusCode.OK, (await super.DeleteAsync($"/api/v1/users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetPassword(beforeDeactivate, "too-late-for-this-1")).StatusCode);
        // …and a deactivated login gets no new link
        var refused = await super.PostAsync($"/api/v1/users/{id}/invite", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("inactive", (await Invites.Body(refused)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bad_used_revoked_and_expired_links_all_get_the_same_404()
    {
        var super = await Super();
        var user = Invites.NewName();
        var (id, mail) = await CreateInvited(super, user, $"{user}@example.test");
        var token = Invites.TokenOf(mail);

        var unknown = await _f.From(NewPeer()).GetAsync("/api/auth/set-password/" + PasswordLinks.NewToken());
        var garbage = await _f.From(NewPeer()).GetAsync("/api/auth/set-password/not-a-token");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, garbage.StatusCode);
        var generic = await unknown.Content.ReadAsStringAsync();
        Assert.Equal(generic, await garbage.Content.ReadAsStringAsync());
        Assert.Contains("invalid_link", generic);

        // expire it
        SettingsTestKit.WithDb(_f, db =>
        {
            var row = db.PasswordTokens.Single(t => t.UserId == id && t.UsedAt == null && t.RevokedAt == null);
            row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            db.SaveChanges();
        });
        var expired = await _f.From(NewPeer()).GetAsync($"/api/auth/set-password/{token}");
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(generic, await expired.Content.ReadAsStringAsync());
        var post = await SetPassword(token, "expired-link-pass-1");
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(generic, await post.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await SetPassword("", "whatever-pass-1")).StatusCode);

        // the Users list says the link expired; a resend fixes it
        var list = await super.GetFromJsonAsync<JsonElement>("/api/v1/users");
        var row2 = list.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == id);
        Assert.Equal("expired", row2.GetProperty("invite_link").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsync($"/api/v1/users/{id}/invite", null)).StatusCode);
    }

    [Fact]
    public async Task A_reset_link_needs_reset_true_and_setting_the_password_signs_every_session_out()
    {
        var super = await Super();
        var user = Invites.NewName();
        var id = SettingsTestKit.CreateUser(_f, user, "old-password-1", Roles.Agent);
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsJsonAsync($"/api/v1/users/{id}", new { email = $"{user}@example.test" })).StatusCode);
        var session = await _f.SignedIn(user, "old-password-1", NewPeer());
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/whoami")).StatusCode);

        // they already have a password: a plain resend is refused, a reset link isn't
        var plain = await super.PostAsJsonAsync($"/api/v1/users/{id}/invite", new { });
        Assert.Equal(HttpStatusCode.Conflict, plain.StatusCode);
        Assert.Equal("password_already_set", (await Invites.Body(plain)).GetProperty("error").GetProperty("code").GetString());
        var reset = await super.PostAsJsonAsync($"/api/v1/users/{id}/invite", new { reset = true });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal("reset", (await Invites.Body(reset)).GetProperty("invite").GetProperty("purpose").GetString());
        var mail = _f.Email.Sent.Last().Message;
        Assert.Contains("Your current password keeps working", Invites.Text(mail));
        Assert.Contains("Choose a new password", Invites.Html(mail));

        // until the link is used, the old password and the session still work
        Assert.Equal(HttpStatusCode.OK, await Login(user, "old-password-1"));
        Assert.Equal(HttpStatusCode.OK, (await SetPassword(Invites.TokenOf(mail), "new-password-22")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/whoami")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(user, "old-password-1"));
        Assert.Equal(HttpStatusCode.OK, await Login(user, "new-password-22"));
    }

    [Fact]
    public async Task Links_to_one_person_are_limited_to_five_an_hour()
    {
        var super = await Super();
        var user = Invites.NewName();
        var (id, _) = await CreateInvited(super, user, $"{user}@example.test"); // 1
        for (var i = 0; i < 4; i++)                                               // 2…5
            Assert.Equal(HttpStatusCode.OK, (await super.PostAsync($"/api/v1/users/{id}/invite", null)).StatusCode);
        var limited = await super.PostAsync($"/api/v1/users/{id}/invite", null);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) >= 1);
        Assert.Equal("too_many_invites", (await Invites.Body(limited)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(5, _f.Email.Sent.Count);

        // a failed send doesn't count (nobody got that link)
        var other = Invites.NewName();
        var (otherId, _) = await CreateInvited(super, other, $"{other}@example.test");
        _f.Email.SendResult = _ => new EmailResult(false, "550 mailbox unavailable", EmailFailure.Recipient);
        for (var i = 0; i < 6; i++)
        {
            var failed = await super.PostAsync($"/api/v1/users/{otherId}/invite", null);
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        }
        _f.Email.SendResult = _ => EmailResult.Success;
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsync($"/api/v1/users/{otherId}/invite", null)).StatusCode);
    }

    [Fact]
    public async Task Resending_follows_the_user_tiers()
    {
        var super = await Super();
        var admin = await _f.SignedIn("admin", "admin123", NewPeer());
        var agent = await _f.SignedIn("agent", "agent123", NewPeer());
        int Id(string u) => SettingsTestKit.UserId(_f, u);
        foreach (var name in new[] { "superadmin", "admin" })
            Assert.Equal(HttpStatusCode.OK, (await super.PostAsJsonAsync($"/api/v1/users/{Id(name)}",
                new { email = $"{name}-{Guid.NewGuid():N}@example.test" })).StatusCode);

        // an admin can't send a reset link to a superadmin, nor to an (org-wide) admin, nor to themselves
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsJsonAsync($"/api/v1/users/{Id("superadmin")}/invite", new { reset = true })).StatusCode);
        var otherAdmin = Invites.NewName();
        var otherAdminId = SettingsTestKit.CreateUser(_f, otherAdmin, "admin-pass-12", Roles.Admin);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsJsonAsync($"/api/v1/users/{otherAdminId}/invite", new { reset = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsJsonAsync($"/api/v1/users/{Id("admin")}/invite", new { reset = true })).StatusCode);
        // an agent (no users.manage) can't send anything
        var victim = Invites.NewName();
        var (victimId, _) = await CreateInvited(super, victim, $"{victim}@example.test", new { tenant = "demo" });
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/api/v1/users/{victimId}/invite", null)).StatusCode);
        // an admin manages agent-level users in their tenant: resend works — and the email names who sent it
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/users/{victimId}/invite", null)).StatusCode);
        Assert.StartsWith("admin added you to", _f.Email.Sent.Last().Message.Subject);
        // the superadmin may send a reset link to an admin
        Assert.Equal(HttpStatusCode.OK,
            (await super.PostAsJsonAsync($"/api/v1/users/{Id("admin")}/invite", new { reset = true })).StatusCode);
    }

    [Fact]
    public async Task Without_a_system_email_it_falls_back_to_the_default_mail_settings_else_refuses()
    {
        var super = await Super();
        await Invites.ClearSystemEmail(super);
        await Invites.SetDefaultMail(super, "", "");
        _f.Email.Reset();

        // nothing to send with: the Users page is told, an explicit invite is refused before anything is created
        var users = await super.GetFromJsonAsync<JsonElement>("/api/v1/users");
        Assert.False(users.GetProperty("invites").GetProperty("ready").GetBoolean());
        Assert.Equal("email_not_configured", users.GetProperty("invites").GetProperty("problem").GetString());
        var name = Invites.NewName();
        var refused = await super.PostAsJsonAsync("/api/v1/users",
            new { username = name, email = $"{name}@example.test", role = Roles.Agent, send_invite = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("email_not_configured", (await Invites.Body(refused)).GetProperty("error").GetProperty("code").GetString());
        Assert.False(SettingsTestKit.WithDb(_f, db => db.Users.Any(u => u.Username == name)));
        // without send_invite the old flow applies: a password is needed, no email goes out
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PostAsJsonAsync("/api/v1/users",
            new { username = name, email = $"{name}@example.test", role = Roles.Agent })).StatusCode);
        var plain = await super.PostAsJsonAsync("/api/v1/users",
            new { username = name, email = $"{name}@example.test", role = Roles.Agent, password = "plain-old-pass-1" });
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        Assert.False((await Invites.Body(plain)).GetProperty("invite").GetProperty("requested").GetBoolean());
        Assert.Empty(_f.Email.Sent);

        // the default mail settings carry it when they are configured
        await Invites.SetDefaultMail(super, "smtp.default.test", "hello@default.test");
        var viaDefault = Invites.NewName();
        await CreateInvited(super, viaDefault, $"{viaDefault}@example.test");
        var (server, mail) = _f.Email.Sent.Single();
        Assert.Equal("smtp.default.test", server.Host);
        Assert.Equal("hello@default.test", mail.From.Mailboxes.Single().Address);
        var settings = await super.GetFromJsonAsync<JsonElement>("/api/settings/system-email");
        Assert.Equal("default", settings.GetProperty("source").GetString());

        // and the System email wins once it is set
        await Invites.ConfigureSystemEmail(super, "smtp.system.test");
        _f.Email.Reset();
        var viaSystem = Invites.NewName();
        await CreateInvited(super, viaSystem, $"{viaSystem}@example.test");
        Assert.Equal("smtp.system.test", _f.Email.Sent.Single().Server.Host);
        await Invites.SetDefaultMail(super, "", "");
    }

    [Fact]
    public async Task A_failed_send_keeps_the_user_and_says_why()
    {
        var super = await Super();
        _f.Email.SendResult = _ => new EmailResult(false, "SMTP login refused: 535 bad credentials smtp-secret-pass", EmailFailure.Auth);
        try
        {
            var name = Invites.NewName();
            var res = await super.PostAsJsonAsync("/api/v1/users", new { username = name, email = $"{name}@example.test", role = Roles.Agent });
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            var invite = (await Invites.Body(res)).GetProperty("invite");
            Assert.False(invite.GetProperty("sent").GetBoolean());
            Assert.Equal("send_failed", invite.GetProperty("code").GetString());
            // the reason comes back — never the SMTP password
            Assert.Contains("535", invite.GetProperty("error").GetString());
            Assert.DoesNotContain("smtp-secret-pass", invite.GetProperty("error").GetString());
            // the user exists, waiting; no live link was left behind
            var id = SettingsTestKit.UserId(_f, name);
            Assert.False(SettingsTestKit.WithDb(_f, db => db.PasswordTokens.Any(t => t.UserId == id)));
            Assert.True(SettingsTestKit.WithDb(_f, db => db.AuditLogs.Any(a => a.Action == "user.invite_failed" && a.Target == name
                && !a.Detail.Contains("smtp-secret-pass"))));
        }
        finally
        {
            _f.Email.SendResult = _ => EmailResult.Success;
        }
    }

    [Fact]
    public async Task The_email_speaks_the_persons_language()
    {
        var super = await Super();
        Assert.Equal(HttpStatusCode.OK, (await super.PutAsJsonAsync("/api/settings/appearance", new { locale = "ms" })).StatusCode);
        try
        {
            var name = Invites.NewName();
            var (_, mail) = await CreateInvited(super, name, $"{name}@example.test");
            Assert.Contains("telah menambah anda", mail.Subject);
            Assert.Contains($"Nama pengguna anda: {name}", Invites.Text(mail));
            Assert.Contains("Pilih kata laluan saya", Invites.Html(mail));
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, (await super.PutAsJsonAsync("/api/settings/appearance", new { locale = (string?)null })).StatusCode);
        }
    }

    [Fact]
    public async Task System_email_settings_round_trip_masked_and_send_a_test_to_the_caller()
    {
        var super = await Super();
        var s = await super.GetFromJsonAsync<JsonElement>("/api/settings/system-email");
        Assert.Equal("smtp.acme.test", s.GetProperty("host").GetString());
        Assert.Equal("system", s.GetProperty("source").GetString());
        Assert.True(s.GetProperty("configured").GetBoolean());
        Assert.True(s.GetProperty("invites").GetProperty("ready").GetBoolean());
        Assert.Equal("https://chat.example.test/login", s.GetProperty("invites").GetProperty("login_url").GetString());
        var masked = s.GetProperty("password_masked").GetString()!;
        Assert.StartsWith("••••", masked);
        Assert.DoesNotContain("smtp-secret-pass", s.GetRawText());

        // the masked value sent back keeps the stored password; other fields change
        var put = await super.PutAsJsonAsync("/api/settings/system-email", new
        {
            from_name = "Acme Desk", from_email = "desk@acme.test", host = "smtp.acme.test", port = 465,
            security = "ssl", username = "mailer", password = masked,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        SettingsTestKit.WithDb(_f, db =>
        {
            var stored = SystemEmail.Load(new ScopingService(db));
            Assert.Equal("smtp-secret-pass", stored.Password);
            Assert.Equal("ssl", stored.Security);
            Assert.Equal(465, stored.Port);
            Assert.Equal("Acme Desk", stored.FromName);
            // organization-wide, and never through the raw settings read
            Assert.True(db.Settings.Any(x => x.Key == SystemEmail.SettingKey && x.EntityId == null));
        });
        var raw = await super.GetFromJsonAsync<JsonElement>($"/api/settings?keys={SystemEmail.SettingKey}");
        Assert.DoesNotContain("smtp-secret-pass", raw.GetRawText());

        // bad input
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PutAsJsonAsync("/api/settings/system-email",
            new { from_email = "not-an-address" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PutAsJsonAsync("/api/settings/system-email",
            new { security = "tls1.0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await super.PutAsJsonAsync("/api/settings/system-email",
            new { port = 70000 })).StatusCode);

        // the test goes to the caller's own address — they need one first
        SettingsTestKit.WithDb(_f, db =>
        {
            db.Users.Single(u => u.Username == "superadmin").Email = "";
            db.SaveChanges();
        });
        var noOwn = await super.PostAsJsonAsync("/api/settings/system-email/test", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noOwn.StatusCode);
        Assert.Equal("no_own_email", (await Invites.Body(noOwn)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await super.PatchAsJsonAsync("/api/auth/me/account", new { email = "boss@acme.test" })).StatusCode);
        _f.Email.Reset();
        var test = await super.PostAsJsonAsync("/api/settings/system-email/test", new { });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var (server, mail) = _f.Email.Sent.Single();
        Assert.Equal("boss@acme.test", mail.To.Mailboxes.Single().Address);
        Assert.Equal("ssl", server.TlsMode);
        Assert.Equal("ChatBranch test email", mail.Subject);

        // a failure says why — without the password
        _f.Email.SendResult = srv => new EmailResult(false, $"SMTP login refused: bad password {srv.Password}", EmailFailure.Auth);
        try
        {
            var failed = await super.PostAsJsonAsync("/api/settings/system-email/test", new { });
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
            var detail = (await Invites.Body(failed)).GetProperty("detail").GetString()!;
            Assert.Contains("SMTP login refused", detail);
            Assert.DoesNotContain("smtp-secret-pass", detail);
        }
        finally
        {
            _f.Email.SendResult = _ => EmailResult.Success;
        }

        // admins don't hold app_keys.manage by default; agents neither
        var admin = await _f.SignedIn("admin", "admin123", NewPeer());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/settings/system-email")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/settings/system-email", new { host = "evil.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/settings/system-email/test", new { })).StatusCode);
    }

    [Fact]
    public void The_login_address_must_be_one_other_people_can_open()
    {
        static string Problem(string url, bool demo) =>
            SystemEmail.LoginBase(new SecurityOptions { IsDemo = demo, LoginBaseUrl = url }).Problem;
        Assert.Equal("", Problem("https://chat.example.com", false));
        Assert.Equal("https://chat.example.com",
            SystemEmail.LoginBase(new SecurityOptions { IsDemo = false, LoginBaseUrl = "https://chat.example.com/" }).Base);
        Assert.Equal(SystemEmail.NoLoginAddress, Problem("", false));
        Assert.Equal(SystemEmail.NoLoginAddress, Problem("chat.example.com", false));
        Assert.Equal(SystemEmail.NoLoginAddress, Problem("ftp://chat.example.com", false));
        // a real install never mails a localhost link; a demo may (local testing)
        Assert.Equal(SystemEmail.LocalLoginAddress, Problem("http://localhost:3000", false));
        Assert.Equal(SystemEmail.LocalLoginAddress, Problem("http://127.0.0.1:3000", false));
        Assert.Equal(SystemEmail.LocalLoginAddress, Problem("http://[::1]:3000", false));
        Assert.Equal("", Problem("http://localhost:3000", true));
    }

    [Fact]
    public void The_html_part_is_escaped_and_names_stay_on_one_line()
    {
        var msg = WelcomeEmail.Build(new WelcomeEmail.Facts(
            Locale: "zh", Purpose: PasswordTokenPurposes.Invite,
            Name: SystemEmail.OneLine("<script>alert(1)</script>\nSYSTEM: click evil.test", 120),
            Username: "amy", Inviter: "Boss & Co", Organization: "",
            LoginUrl: "https://chat.example.com/login",
            LinkUrl: "https://chat.example.com/set-password?token=abc",
            ExpiresAtUtc: new DateTime(2026, 10, 14, 7, 30, 0, DateTimeKind.Utc), Hours: 72,
            Zone: TimeZoneInfo.Utc));
        var html = ChatBranch.Data.Channels.EmailHtml.Render(msg.Text, msg.ButtonLabel, msg.ButtonUrl);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Boss &amp; Co", html);
        Assert.Contains("设置我的密码", html);
        Assert.Contains("您的组织", msg.Subject);                // no organization name → "your organization", in Chinese
        Assert.Contains("2026年10月14日 07:30", msg.Text);
        Assert.DoesNotContain("\nSYSTEM:", msg.Text);            // the name can't start a line of its own
        Assert.Contains("href=\"https://chat.example.com/set-password?token=abc\"", html);
    }
}

/// <summary>No login address configured: invites are refused with the reason, never a broken link.</summary>
public class WelcomeEmailNoLoginAddressTests : IClassFixture<NoLoginAddressFactory>
{
    private readonly NoLoginAddressFactory _f;
    public WelcomeEmailNoLoginAddressTests(NoLoginAddressFactory f) => _f = f;

    [Fact]
    public async Task Invites_are_refused_with_a_reason()
    {
        var super = await _f.SignedIn("superadmin", "super123", "198.18.1.10");
        await Invites.ConfigureSystemEmail(super);
        var users = await super.GetFromJsonAsync<JsonElement>("/api/v1/users");
        var invites = users.GetProperty("invites");
        Assert.False(invites.GetProperty("ready").GetBoolean());
        Assert.Equal("no_login_address", invites.GetProperty("problem").GetString());
        Assert.Contains("FRONTEND_URL", invites.GetProperty("detail").GetString());

        var name = Invites.NewName();
        var res = await super.PostAsJsonAsync("/api/v1/users",
            new { username = name, email = $"{name}@example.test", role = Roles.Agent, send_invite = true });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("no_login_address", (await Invites.Body(res)).GetProperty("error").GetProperty("code").GetString());
        // the default is "no invite" then: the admin sets the password as before
        var plain = await super.PostAsJsonAsync("/api/v1/users",
            new { username = name, email = $"{name}@example.test", role = Roles.Agent, password = "plain-old-pass-1" });
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        var id = (await Invites.Body(plain)).GetProperty("id").GetInt32();
        var reset = await super.PostAsJsonAsync($"/api/v1/users/{id}/invite", new { reset = true });
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Equal("no_login_address", (await Invites.Body(reset)).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_f.Email.Sent);
    }
}

/// <summary>A real install: the strict password rule on the link, and no "change your initial password" left behind.</summary>
public class WelcomeEmailRealInstallTests : IClassFixture<RealInstallInviteFactory>
{
    private readonly RealInstallInviteFactory _f;
    public WelcomeEmailRealInstallTests(RealInstallInviteFactory f) => _f = f;

    [Fact]
    public async Task The_link_applies_the_strict_rule_and_clears_a_forced_password_change()
    {
        // the superadmin's seeded password must change before anything else
        var super = await _f.SignedIn("superadmin", "super123", "198.18.2.10");
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsJsonAsync("/api/auth/me/password",
            new { current_password = "super123", new_password = "roti-telur-bawang-9" })).StatusCode);
        await Invites.ConfigureSystemEmail(super);

        // admin still has the seeded admin123: its session is held to "change your password"
        var held = await _f.SignedIn("admin", "admin123", "198.18.2.11");
        var who = await held.GetFromJsonAsync<JsonElement>("/api/auth/whoami");
        Assert.Equal("change_password", who.GetProperty("restriction").GetString());

        var adminId = SettingsTestKit.UserId(_f, "admin");
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsJsonAsync($"/api/v1/users/{adminId}",
            new { email = "admin@acme.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await super.PostAsJsonAsync($"/api/v1/users/{adminId}/invite",
            new { reset = true })).StatusCode);
        var token = Invites.TokenOf(_f.Email.Sent.Last().Message);

        var c = _f.From("198.18.2.12");
        var common = await c.PostAsJsonAsync("/api/auth/set-password", new { token, password = "password1234" });
        Assert.Equal(HttpStatusCode.BadRequest, common.StatusCode);
        Assert.Equal("password_common", (await Invites.Body(common)).GetProperty("code").GetString());
        var same = await c.PostAsJsonAsync("/api/auth/set-password", new { token, password = "admin" });
        Assert.Equal("password_too_short", (await Invites.Body(same)).GetProperty("code").GetString());
        var info = await c.GetFromJsonAsync<JsonElement>($"/api/auth/set-password/{token}");
        Assert.Equal(8, info.GetProperty("password_min_length").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/set-password",
            new { token, password = "teh-halia-panas-3" })).StatusCode);
        // the held session is gone; the new sign-in is a normal one
        Assert.Equal(HttpStatusCode.Unauthorized, (await held.GetAsync("/api/auth/whoami")).StatusCode);
        var login = await _f.From("198.18.2.13").PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = "teh-halia-panas-3" });
        var body = await Invites.Body(login);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(body.GetProperty("must_change_password").GetBoolean());
        Assert.Equal("", body.GetProperty("restriction").GetString());
    }
}
