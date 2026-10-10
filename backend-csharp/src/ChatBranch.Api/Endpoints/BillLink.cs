using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatBranch.Api.Endpoints;

/// <summary>Body of <c>POST /api/billing/ai/pdf/link</c>.</summary>
public sealed record BillLinkRequest(
    [property: JsonPropertyName("month")] string? Month,
    [property: JsonPropertyName("company")] string? Company,
    [property: JsonPropertyName("lang")] string? Lang);

/// <summary>
/// The signed, short-lived download link of the AI bill's PDF (for the installed
/// iOS app, which cannot send an auth header on a navigation). The token is
/// <c>base64url(json).base64url(HMAC-SHA256)</c> with a per-process random key:
/// nothing is stored, a restart voids every link (they live 5 minutes anyway), and
/// a token cannot be forged or edited — the person, month, company, language and
/// expiry are all signed.
/// </summary>
public static class BillLink
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public sealed record Claims(
        [property: JsonPropertyName("p")] int ProfileId,
        [property: JsonPropertyName("u")] string Username,
        [property: JsonPropertyName("m")] string Month,
        [property: JsonPropertyName("c")] string Company,
        [property: JsonPropertyName("l")] string Lang,
        [property: JsonPropertyName("e")] long ExpiresUnix);

    public static string Sign(Claims c)
    {
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(c));
        return body + "." + B64(HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes(body)));
    }

    /// <summary>The claims of a genuine, unexpired token; null otherwise.</summary>
    public static Claims? Verify(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        var expected = HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes(parts[0]));
        byte[] given;
        try { given = UnB64(parts[1]); } catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return null;
        Claims? c;
        try { c = JsonSerializer.Deserialize<Claims>(UnB64(parts[0])); }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
        if (c == null || c.ExpiresUnix < now.ToUnixTimeSeconds()) return null;
        // never longer than the lifetime, whatever the clock said when it was signed
        if (c.ExpiresUnix > now.Add(Lifetime).AddSeconds(30).ToUnixTimeSeconds()) return null;
        return c;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }
}
