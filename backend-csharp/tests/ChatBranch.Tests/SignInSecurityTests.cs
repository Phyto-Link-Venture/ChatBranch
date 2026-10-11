using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api.Security;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Sign-in protection (Security/LoginThrottle): failed passwords are throttled per
/// username AND per client IP, a lock answers 429 + Retry-After before the password is
/// looked at, an unknown username answers exactly like a wrong password, a lock is audited
/// and raises ONE collapsing admin alert, the client IP comes from X-Forwarded-For only
/// via a trusted proxy, and repeated bad API keys lock their IP out of key sign-in.
/// </summary>
public class SignInSecurityTests : IClassFixture<SecurityFactory>
{
    private readonly SecurityFactory _f;
    public SignInSecurityTests(SecurityFactory f) => _f = f;

    private static int _peer;
    /// <summary>A fresh client address per use, so one test's failures never count toward another's.</summary>
    private static string NewPeer() => $"203.0.113.{Interlocked.Increment(ref _peer) % 250 + 1}";

    private string NewUser(string role = Roles.Agent, string pass = "right-pass-1")
    {
        var user = "si_" + Guid.NewGuid().ToString("N")[..10];
        SettingsTestKit.CreateUser(_f, user, pass, role);
        return user;
    }

    private static Task<HttpResponseMessage> Login(HttpClient c, string user, string pass) =>
        c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });

    [Fact]
    public async Task Five_wrong_passwords_lock_the_username_and_even_the_right_one_waits()
    {
        var user = NewUser();
        var c = _f.From(NewPeer());
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, user, "wrong-" + i)).StatusCode);

        var locked = await Login(c, user, "right-pass-1");
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter?.Delta?.TotalSeconds > 0
                    || int.Parse(locked.Headers.GetValues("Retry-After").First()) > 0);
        var body = await locked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("too_many_attempts", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("retry_after").GetInt32() > 800); // ~15 min

        // the lock is on the USERNAME: the same address can still sign someone else in
        var other = NewUser();
        Assert.Equal(HttpStatusCode.OK, (await Login(c, other, "right-pass-1")).StatusCode);
        // …and from another address the locked username still waits
        Assert.Equal((HttpStatusCode)429, (await Login(_f.From(NewPeer()), user, "right-pass-1")).StatusCode);

        // audited
        var admin = await _f.SignedIn("admin", "admin123", NewPeer());
        var rows = await Platform.Audit(admin, "action=login.locked");
        Assert.Contains(rows, r => r.GetProperty("target").GetString() == user);
    }

    [Fact]
    public async Task An_unknown_username_answers_like_a_wrong_password_and_locks_the_same_way()
    {
        var c = _f.From(NewPeer());
        var known = NewUser();
        var wrong = await Login(c, known, "nope-nope-1");
        var unknown = await Login(c, "nobody_" + Guid.NewGuid().ToString("N")[..8], "nope-nope-1");
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());

        // a made-up username locks too — a lock never tells which usernames are real
        var ghost = "ghost_" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 5; i++) await Login(c, ghost, "x" + i);
        Assert.Equal((HttpStatusCode)429, (await Login(c, ghost, "x")).StatusCode);
    }

    [Fact]
    public async Task A_successful_sign_in_clears_the_usernames_count()
    {
        var user = NewUser();
        var c = _f.From(NewPeer());
        for (var i = 0; i < 4; i++) await Login(c, user, "wrong-" + i);
        Assert.Equal(HttpStatusCode.OK, (await Login(c, user, "right-pass-1")).StatusCode);
        for (var i = 0; i < 4; i++) await Login(c, user, "wrong-again-" + i);
        Assert.Equal(HttpStatusCode.OK, (await Login(c, user, "right-pass-1")).StatusCode);
    }

    [Fact]
    public async Task Thirty_failures_from_one_address_lock_that_address_only()
    {
        var c = _f.From(NewPeer());
        for (var i = 0; i < 30; i++)
            await Login(c, "spray_" + i + "_" + Guid.NewGuid().ToString("N")[..6], "Winter2026!");
        var user = NewUser();
        Assert.Equal((HttpStatusCode)429, (await Login(c, user, "right-pass-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(_f.From("192.0.2.77"), user, "right-pass-1")).StatusCode);
    }

    [Fact]
    public async Task An_address_lock_spares_people_who_signed_in_from_there_before()
    {
        // the office: a colleague signed in from here last week; someone at the same address
        // now keeps failing (a misconfigured script, a guessing tool) until the address locks
        var office = NewPeer();
        var colleague = NewUser();
        Assert.Equal(HttpStatusCode.OK, (await Login(_f.From(office), colleague, "right-pass-1")).StatusCode);
        var c = _f.From(office);
        for (var i = 0; i < 30; i++)
            await Login(c, "spray_" + i + "_" + Guid.NewGuid().ToString("N")[..6], "Winter2026!");

        // a newcomer at that address waits; the known colleague still signs in…
        Assert.Equal((HttpStatusCode)429, (await Login(c, NewUser(), "right-pass-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(c, colleague, "right-pass-1")).StatusCode);
        // …but a wrong password from the colleague still doesn't open the gate, and the
        // username's own lock always applies
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, colleague, "nope-nope-1")).StatusCode);
        for (var i = 0; i < 5; i++) await Login(_f.From(NewPeer()), colleague, "wrong-" + i);
        Assert.Equal((HttpStatusCode)429, (await Login(c, colleague, "right-pass-1")).StatusCode);
    }

    [Fact]
    public async Task A_lockout_burst_raises_one_collapsing_admin_alert()
    {
        var adminId = SettingsTestKit.UserId(_f, "admin");
        int Unread() => SettingsTestKit.WithDb(_f, db =>
            db.Notifications.Count(n => n.UserId == adminId && n.RefKey == "security:login_locked" && !n.Read));
        var c = _f.From(NewPeer());
        foreach (var user in new[] { NewUser(), NewUser(), NewUser() })
            for (var i = 0; i < 5; i++) await Login(c, user, "wrong-" + i);
        Assert.Equal(1, Unread());
    }

    [Fact]
    public async Task X_Forwarded_For_counts_only_through_a_trusted_proxy()
    {
        // an internet client rotating X-Forwarded-For is still ONE address: its own
        var attacker = "198.51.100.99";
        for (var i = 0; i < 30; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { username = "fx_" + Guid.NewGuid().ToString("N")[..6], password = "x" }),
            };
            req.Headers.Add(SecurityFactory.PeerHeader, attacker);
            req.Headers.Add("X-Forwarded-For", $"10.9.{i}.{i}");
            await _f.CreateClient().SendAsync(req);
        }
        Assert.Equal((HttpStatusCode)429, (await Login(_f.From(attacker), NewUser(), "right-pass-1")).StatusCode);

        // behind the bundled nginx (loopback, trusted) the forwarded client IS the client:
        // 30 failures for one forwarded address lock it, a different forwarded client still signs in
        HttpRequestMessage ViaProxy(string forwarded, string user, string pass)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { username = user, password = pass }),
            };
            req.Headers.Add(SecurityFactory.PeerHeader, "127.0.0.1");
            req.Headers.Add("X-Forwarded-For", forwarded);
            return req;
        }
        for (var i = 0; i < 30; i++)
            await _f.CreateClient().SendAsync(ViaProxy("198.51.100.150", "px_" + i + Guid.NewGuid().ToString("N")[..4], "x"));
        var victim = NewUser();
        Assert.Equal((HttpStatusCode)429, (await _f.CreateClient().SendAsync(ViaProxy("198.51.100.150", victim, "right-pass-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().SendAsync(ViaProxy("198.51.100.151", victim, "right-pass-1"))).StatusCode);
    }

    [Fact]
    public void Trusted_proxy_list_parses_ips_and_cidrs_and_defaults_to_private_ranges()
    {
        var defaults = ClientIp.ParseTrusted("");
        Assert.True(ClientIp.IsTrusted(IPAddress.Parse("127.0.0.1"), defaults));
        Assert.True(ClientIp.IsTrusted(IPAddress.Parse("172.18.0.5"), defaults));
        Assert.True(ClientIp.IsTrusted(IPAddress.Parse("::ffff:172.18.0.5"), defaults));
        Assert.False(ClientIp.IsTrusted(IPAddress.Parse("8.8.8.8"), defaults));

        var problems = new List<string>();
        var custom = ClientIp.ParseTrusted("10.1.2.3, 192.0.2.0/24 nonsense 300.1.1.1", problems);
        Assert.True(ClientIp.IsTrusted(IPAddress.Parse("192.0.2.200"), custom));
        Assert.True(ClientIp.IsTrusted(IPAddress.Parse("10.1.2.3"), custom));
        Assert.False(ClientIp.IsTrusted(IPAddress.Parse("10.1.2.4"), custom));
        Assert.Equal(new[] { "nonsense", "300.1.1.1" }, problems);
        Assert.Empty(ClientIp.ParseTrusted("none"));
    }

    [Fact]
    public async Task Repeated_bad_api_keys_lock_their_address_out_of_key_sign_in()
    {
        var peer = "198.51.100.201";
        var c = _f.From(peer);
        for (var i = 0; i < 20; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/whoami");
            req.Headers.Add("X-Api-Key", $"cbk_nosuchkey{i}_" + new string('a', 40));
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(req)).StatusCode);
        }
        // now even a valid key from that address waits; from another address it works
        var good = Platform.MintKey(_f, "admin");
        var blocked = new HttpRequestMessage(HttpMethod.Get, "/api/auth/whoami");
        blocked.Headers.Add("X-Api-Key", good);
        var res = await c.SendAsync(blocked);
        Assert.Equal((HttpStatusCode)429, res.StatusCode);
        Assert.Equal("too_many_attempts", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var elsewhere = _f.From("192.0.2.9");
        elsewhere.DefaultRequestHeaders.Add("X-Api-Key", good);
        Assert.Equal(HttpStatusCode.OK, (await elsewhere.GetAsync("/api/auth/whoami")).StatusCode);
    }

    [Fact]
    public async Task My_account_password_checks_count_toward_the_same_lock()
    {
        var user = NewUser();
        var c = await _f.SignedIn(user, "right-pass-1", NewPeer());
        for (var i = 0; i < 5; i++)
            await c.PostAsJsonAsync("/api/auth/me/password", new { current_password = "wrong-" + i, new_password = "another-pass-9" });
        Assert.Equal((HttpStatusCode)429, (await Login(_f.From(NewPeer()), user, "right-pass-1")).StatusCode);
    }

    // ---- security headers ------------------------------------------------------------

    [Fact]
    public async Task Api_answers_carry_the_security_headers_and_no_server_banner()
    {
        var res = await _f.From(NewPeer()).GetAsync("/api/whatsapp/status");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string H(string name) => res.Headers.TryGetValues(name, out var v) ? string.Join(",", v)
            : res.Content.Headers.TryGetValues(name, out var cv) ? string.Join(",", cv) : "";
        Assert.Equal("nosniff", H("X-Content-Type-Options"));
        Assert.Equal("no-referrer", H("Referrer-Policy"));
        Assert.Equal("noindex, nofollow", H("X-Robots-Tag"));
        Assert.Equal(SecurityHeaders.DataCsp, H("Content-Security-Policy"));
        Assert.Equal("DENY", H("X-Frame-Options"));
        Assert.Equal("", H("Server"));
        Assert.Equal("", H("Strict-Transport-Security")); // plain http

        // the widget script must load on other people's pages: no CSP / frame headers on it
        var js = await _f.From(NewPeer()).GetAsync("/api/widget.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.False(js.Headers.Contains("Content-Security-Policy"));
        Assert.False(js.Headers.Contains("X-Frame-Options"));
        Assert.Equal("nosniff", js.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Hsts_is_sent_for_https_via_a_trusted_proxy_but_never_on_an_untrusted_header()
    {
        async Task<bool> Hsts(string peer)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/whatsapp/status");
            req.Headers.Add(SecurityFactory.PeerHeader, peer);
            req.Headers.Add("X-Forwarded-Proto", "https");
            req.Headers.Add("X-Forwarded-For", "198.51.100.33");
            var res = await _f.CreateClient().SendAsync(req);
            return res.Headers.Contains("Strict-Transport-Security");
        }
        Assert.True(await Hsts("127.0.0.1"));      // nginx on the same host
        Assert.True(await Hsts("172.18.0.4"));     // a compose network
        Assert.False(await Hsts("198.51.100.44")); // straight from the internet
    }

    [Fact]
    public void Content_type_decides_the_frame_and_csp_headers()
    {
        string? Csp(string? contentType, string? preset = null)
        {
            var ctx = new DefaultHttpContext();
            ctx.Response.ContentType = contentType;
            if (preset != null) ctx.Response.Headers["Content-Security-Policy"] = preset;
            SecurityHeaders.Apply(ctx, "max-age=1");
            return ctx.Response.Headers.TryGetValue("Content-Security-Policy", out var v) ? v.ToString() : null;
        }
        Assert.Equal(SecurityHeaders.DataCsp, Csp("application/json; charset=utf-8"));
        Assert.Equal(SecurityHeaders.DataCsp, Csp("application/problem+json"));
        Assert.Equal(SecurityHeaders.DataCsp, Csp(null));
        Assert.Equal(SecurityHeaders.SandboxCsp, Csp("image/svg+xml"));   // an uploaded SVG never runs script
        Assert.Equal(SecurityHeaders.SandboxCsp, Csp("text/html"));
        Assert.Null(Csp("application/pdf"));                               // documents render / embed
        Assert.Null(Csp("image/png"));
        Assert.Null(Csp("audio/ogg"));
        Assert.Null(Csp("application/javascript"));                        // widget.js
        Assert.Equal("default-src 'none'; sandbox", Csp("audio/ogg", "default-src 'none'; sandbox")); // the endpoint's own wins
    }
}
