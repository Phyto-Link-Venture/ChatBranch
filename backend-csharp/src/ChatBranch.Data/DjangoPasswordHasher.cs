using System.Security.Cryptography;
using System.Text;

namespace ChatBranch.Data;

/// <summary>
/// Interop with Django's default password hasher (PBKDF2-SHA256) so the C#
/// backend can authenticate users created by the Django app against the SAME
/// SQLite database, and issue new hashes Django would also accept.
///
/// Django encodes a hash as: <c>pbkdf2_sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;b64(dk)&gt;</c>
/// where dk = PBKDF2(HMAC-SHA256, password, salt, iterations, 32 bytes).
/// </summary>
public static class DjangoPasswordHasher
{
    private const string Algorithm = "pbkdf2_sha256";
    private const int DefaultIterations = 600_000; // Django 4.2 default
    private const int KeyLength = 32;
    private const int SaltLength = 16;

    public static bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return false;
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm) return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;

        var salt = parts[2];
        var expected = parts[3];
        var actual = Pbkdf2(password, salt, iterations);
        // Constant-time comparison (Django uses hmac.compare_digest).
        var a = Encoding.ASCII.GetBytes(actual);
        var b = Encoding.ASCII.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Work factor for NEW hashes: Django's default, unless
    /// <c>PASSWORD_HASH_ITERATIONS</c> asks for another (never below 1 000). The
    /// test host lowers it — every test host seeds hashed logins and the suite
    /// signs in hundreds of times, which at 600 000 rounds saturated a 2-vCPU CI
    /// runner. Verification always uses the count stored IN the hash, so a
    /// change here never locks anyone out.
    /// </summary>
    public static int NewHashIterations() =>
        NewHashIterations(Environment.GetEnvironmentVariable("PASSWORD_HASH_ITERATIONS"));

    /// <summary>The rule behind <see cref="NewHashIterations()"/> for a configured value.</summary>
    public static int NewHashIterations(string? configured) =>
        int.TryParse(configured, out var n) && n > 0 ? Math.Max(n, 1_000) : DefaultIterations;

    public static string Hash(string password, int? iterations = null)
    {
        var iters = iterations ?? NewHashIterations();
        var saltBytes = RandomNumberGenerator.GetBytes(SaltLength);
        // Django salts are alphanumeric ASCII; base64url-ish is fine as long as
        // we hash the exact salt string we store.
        var salt = Convert.ToBase64String(saltBytes)
            .Replace("+", "").Replace("/", "").Replace("=", "")[..12];
        var dk = Pbkdf2(password, salt, iters);
        return $"{Algorithm}${iters}${salt}${dk}";
    }

    private static string Pbkdf2(string password, string salt, int iterations)
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(salt),
            iterations,
            HashAlgorithmName.SHA256,
            KeyLength);
        return Convert.ToBase64String(derived);
    }
}
