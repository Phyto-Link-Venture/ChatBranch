using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using AspNetIPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace ChatBranch.Api.Security;

/// <summary>
/// The client's IP address. Behind a reverse proxy every connection comes from the
/// proxy, so <see cref="UseTrustedProxies"/> (the FIRST middleware) rewrites the
/// connection's remote address from <c>X-Forwarded-For</c> — but ONLY when the
/// direct peer is a proxy listed in <c>TRUSTED_PROXIES</c> (default: loopback and the
/// private ranges Docker hands out). A header sent straight from the internet is
/// never believed. <c>X-Forwarded-Proto</c> follows the same rule, so
/// <c>Request.IsHttps</c> is true behind a TLS-terminating proxy (HSTS, links).
/// </summary>
public static class ClientIp
{
    /// <summary>The client address after the trusted-proxy rewrite, IPv4 shown as IPv4 ("" when unknown).</summary>
    public static string Of(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip == null) return "";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }

    /// <summary>The default trust list: loopback + RFC 1918 + IPv6 unique-local (Docker / compose networks).</summary>
    public static readonly string[] DefaultTrusted =
    {
        "127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7",
    };

    /// <summary>
    /// Parse <c>TRUSTED_PROXIES</c>: a comma (or space) list of IPs and CIDRs. "" = the
    /// defaults, "none" = trust nobody. Bad entries are skipped and reported.
    /// </summary>
    public static List<AspNetIPNetwork> ParseTrusted(string? raw, ICollection<string>? problems = null)
    {
        var text = (raw ?? "").Trim();
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) return new List<AspNetIPNetwork>();
        var items = text.Length == 0
            ? DefaultTrusted
            : text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nets = new List<AspNetIPNetwork>();
        foreach (var item in items)
        {
            if (TryNetwork(item, out var net))
            {
                nets.Add(net);
                // a dual-stack socket reports IPv4 peers as ::ffff:a.b.c.d — trust that spelling too
                if (net.Prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    nets.Add(new AspNetIPNetwork(net.Prefix.MapToIPv6(), 96 + net.PrefixLength));
            }
            else problems?.Add(item);
        }
        return nets;
    }

    private static bool TryNetwork(string item, out AspNetIPNetwork net)
    {
        net = null!;
        var slash = item.IndexOf('/');
        var addrText = slash < 0 ? item : item[..slash];
        if (!IPAddress.TryParse(addrText, out var addr)) return false;
        var max = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var len = max;
        if (slash >= 0 && (!int.TryParse(item[(slash + 1)..], out len) || len < 0 || len > max)) return false;
        net = new AspNetIPNetwork(addr, len);
        return true;
    }

    /// <summary>Is <paramref name="ip"/> inside one of <paramref name="nets"/>?</summary>
    public static bool IsTrusted(IPAddress? ip, IEnumerable<AspNetIPNetwork> nets) =>
        ip != null && nets.Any(n => n.Contains(ip));

    /// <summary>
    /// The first middleware: believe X-Forwarded-For / -Proto only from trusted proxies.
    /// Several trusted hops (a CDN in front of nginx, both listed) are walked back from
    /// the right until the first address that is not a trusted proxy — the client.
    /// </summary>
    public static IApplicationBuilder UseTrustedProxies(this IApplicationBuilder app, SecurityOptions options,
        ILogger? log = null)
    {
        var problems = new List<string>();
        var nets = ParseTrusted(options.TrustedProxies, problems);
        if (problems.Count > 0)
            log?.LogWarning("TRUSTED_PROXIES: ignored entries that are not an IP or CIDR: {Entries}",
                string.Join(", ", problems));
        var fh = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = null, // walk every trusted hop, stop at the first untrusted address
            RequireHeaderSymmetry = false,
        };
        fh.KnownNetworks.Clear();
        fh.KnownProxies.Clear();
        foreach (var n in nets) fh.KnownNetworks.Add(n);
        return app.UseForwardedHeaders(fh);
    }
}
