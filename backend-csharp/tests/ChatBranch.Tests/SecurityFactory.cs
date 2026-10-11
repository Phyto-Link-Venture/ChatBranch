using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Tests;

/// <summary>
/// A test host with its own <see cref="SecurityOptions"/> (the process environment is
/// shared by every host, so the security knobs are swapped as a service instead) and a
/// client address per request: the in-memory test server has none, so a startup filter —
/// running before the app's own first middleware, like a socket would — sets it from the
/// <c>X-Test-Peer</c> header (default <see cref="DefaultPeer"/>, a public address).
/// </summary>
public class SecurityFactory : ApiFactory
{
    public const string PeerHeader = "X-Test-Peer";
    public const string DefaultPeer = "198.51.100.7";

    /// <summary>The knobs this host runs with (the defaults: rate limits off like the suite, a demo).</summary>
    protected virtual SecurityOptions Options => new() { RateLimitEnabled = false, IsDemo = true };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Options);
            services.AddSingleton<IStartupFilter, PeerAddressFilter>();
        });
    }

    /// <summary>A client whose requests come from <paramref name="peer"/>.</summary>
    public HttpClient From(string peer)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add(PeerHeader, peer);
        return c;
    }

    public async Task<HttpClient> SignedIn(string user, string pass, string peer = DefaultPeer)
    {
        var c = From(peer);
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private sealed class PeerAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                var peer = ctx.Request.Headers[PeerHeader].ToString();
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer.Length > 0 ? peer : DefaultPeer);
                await n(ctx);
            });
            next(app);
        };
    }
}

/// <summary>A real install: no SEED_DEMO (seeded passwords must change, the stricter password rule).</summary>
public class RealInstallFactory : SecurityFactory
{
    protected override SecurityOptions Options => new() { RateLimitEnabled = false, IsDemo = false };
}

/// <summary>Rate limits ON, and low, so a test can reach them in a few requests.</summary>
public class RateLimitedFactory : SecurityFactory
{
    protected override SecurityOptions Options => new()
    {
        RateLimitEnabled = true, IsDemo = true,
        AnonymousPerMinute = 6, UserPerMinute = 12, LoginPerMinute = 3, WidgetPerMinute = 2,
        PublicLinkPerMinute = 2, MediaPerMinute = 1000, OAuthPerMinute = 2,
    };
}
