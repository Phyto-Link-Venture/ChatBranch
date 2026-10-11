using System.Security.Cryptography;
using System.Text;

namespace ChatBranch.Api.Security;

/// <summary>
/// RFC 6238 time-based one-time passwords — what every authenticator app (Google
/// Authenticator, Microsoft Authenticator, 1Password, Authy…) shows: HMAC-SHA1, 30-second
/// steps, 6 digits. <see cref="Match"/> accepts the current step ±1 (clock drift) and
/// never a step at or before the last one used, so a code can't be replayed.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    public const int SecretBytes = 20; // 160 bits, RFC 4226's recommendation

    public static long StepAt(DateTimeOffset t) => t.ToUnixTimeSeconds() / PeriodSeconds;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The code for one time step (RFC 4226 dynamic truncation).</summary>
    public static string Code(byte[] secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xff);
            step >>= 8;
        }
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var mod = (int)Math.Pow(10, digits);
        return (binary % mod).ToString(new string('0', digits));
    }

    /// <summary>
    /// The time step <paramref name="code"/> is valid for — the current one ±<paramref name="drift"/> —
    /// or null. Steps at or before <paramref name="lastUsedStep"/> never match (replay).
    /// </summary>
    public static long? Match(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep, int drift = 1)
    {
        var digits = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != Digits) return null;
        var current = StepAt(now);
        long? hit = null;
        var wanted = Encoding.ASCII.GetBytes(digits);
        // check every candidate (no early exit) so timing says nothing about which step matched
        for (var s = current - drift; s <= current + drift; s++)
        {
            if (s <= lastUsedStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, s)), wanted)) hit ??= s;
        }
        return hit;
    }

    /// <summary>The <c>otpauth://</c> URI authenticator apps read from the QR code.</summary>
    public static string ProvisioningUri(string issuer, string account, byte[] secret)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}"
               + $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }
}

/// <summary>RFC 4648 base32 without padding — the "manual key" people type into an authenticator.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[]? Decode(string? text)
    {
        var clean = (text ?? "").ToUpperInvariant().Replace(" ", "").Replace("-", "").TrimEnd('=');
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return output.ToArray();
    }

    /// <summary>The key in groups of four ("JBSW Y3DP …") — easier to type.</summary>
    public static string Grouped(string key) =>
        string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
}
