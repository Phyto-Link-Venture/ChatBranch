using System.Security.Cryptography;
using System.Text;

namespace ChatBranch.Data.Integrations;

/// <summary>
/// Webhook signature verification (port of integrations/webhooks.py). Shopify:
/// base64 HMAC-SHA256 of the raw body in X-Shopify-Hmac-Sha256. Stripe:
/// <c>t=…,v1=…</c> with a replay window. Both use constant-time comparison, and
/// NO secret configured ⇒ caller returns 503 (never "accept unsigned").
/// </summary>
public static class WebhookVerify
{
    /// <summary>
    /// Meta/WhatsApp webhook: X-Hub-Signature-256 is <c>sha256=&lt;hex&gt;</c> of the
    /// raw body keyed by the app_secret. Constant-time compared.
    /// </summary>
    public static bool Meta(string appSecret, byte[] rawBody, string header)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(header)) return false;
        var sig = header.StartsWith("sha256=") ? header["sha256=".Length..] : header;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var computed = Convert.ToHexString(h.ComputeHash(rawBody)).ToLowerInvariant();
        return FixedTimeEquals(computed, sig.ToLowerInvariant());
    }

    public static bool Shopify(string secret, byte[] rawBody, string headerHmac)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(headerHmac)) return false;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToBase64String(h.ComputeHash(rawBody));
        return FixedTimeEquals(computed, headerHmac);
    }

    public static bool Stripe(string secret, byte[] rawBody, string sigHeader, int toleranceSeconds = 300)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(sigHeader)) return false;
        // header: t=timestamp,v1=signature[,v1=...]
        string? t = null;
        var v1 = new List<string>();
        foreach (var part in sigHeader.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t") t = kv[1];
            else if (kv[0] == "v1") v1.Add(kv[1]);
        }
        if (t == null || v1.Count == 0) return false;
        if (!long.TryParse(t, out var ts)) return false;
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
        if (Math.Abs(age) > toleranceSeconds) return false; // replay window

        var signed = Encoding.UTF8.GetBytes($"{t}.").Concat(rawBody).ToArray();
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToHexString(h.ComputeHash(signed)).ToLowerInvariant();
        return v1.Any(sig => FixedTimeEquals(computed, sig));
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
