using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatBranch.Data.Contacts;

/// <summary>
/// The opaque <c>request_token</c> a hidden-clash 409 hands out ("this customer
/// already exists in another branch of your company"): an ENCRYPTED, signed,
/// short-lived reference to the existing contact, the requester's target entity
/// and the requester — so the 409 never reveals the contact id (base64 JSON
/// would), a token can't be edited to point at another contact, and one user's
/// token is useless to another. AES-GCM under a key derived from the stored
/// organization secret (Setting <c>contact_access_secret</c>, generated once).
/// </summary>
public static class ContactAccessToken
{
    public const string SecretKey = "contact_access_secret";

    /// <summary>How long a token stays valid.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    public sealed record Payload(int ContactId, int EntityId, string Username, DateTimeOffset IssuedAt);

    public static string Issue(ScopingService sc, int contactId, int entityId, string username,
        DateTimeOffset? now = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            c = contactId,
            e = entityId,
            u = username,
            t = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
        });
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[json.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Key(sc), 16)) aes.Encrypt(nonce, json, cipher, tag);
        var all = new byte[nonce.Length + cipher.Length + tag.Length];
        nonce.CopyTo(all, 0);
        cipher.CopyTo(all, nonce.Length);
        tag.CopyTo(all, nonce.Length + cipher.Length);
        return Base64Url(all);
    }

    /// <summary>The token's payload, or null when it is tampered, foreign or expired.</summary>
    public static Payload? Read(ScopingService sc, string? token, DateTimeOffset? now = null)
    {
        try
        {
            var raw = FromBase64Url((token ?? "").Trim());
            if (raw.Length < 12 + 16 + 2) return null;
            var nonce = raw[..12];
            var tag = raw[^16..];
            var cipher = raw[12..^16];
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(Key(sc), 16)) aes.Decrypt(nonce, cipher, tag, plain);
            using var doc = JsonDocument.Parse(plain);
            var r = doc.RootElement;
            var issued = DateTimeOffset.FromUnixTimeSeconds(r.GetProperty("t").GetInt64());
            var age = (now ?? DateTimeOffset.UtcNow) - issued;
            if (age > MaxAge || age < TimeSpan.FromMinutes(-5)) return null;
            return new Payload(r.GetProperty("c").GetInt32(), r.GetProperty("e").GetInt32(),
                r.GetProperty("u").GetString() ?? "", issued);
        }
        catch
        {
            return null;   // not base64, wrong key, edited bytes, bad JSON — all the same: invalid
        }
    }

    private static byte[] Key(ScopingService sc)
    {
        var stored = sc.SettingValue(SecretKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            stored = Base64Url(RandomNumberGenerator.GetBytes(32));
            sc.SetSetting(SecretKey, stored);
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes("contact-access:" + stored));
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }
}
