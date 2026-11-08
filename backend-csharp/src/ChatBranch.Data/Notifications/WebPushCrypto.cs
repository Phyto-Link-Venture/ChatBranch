using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatBranch.Data;

/// <summary>
/// The Web Push protocol's cryptography, by hand and with no third-party package:
/// message encryption (RFC 8291 over the aes128gcm content coding of RFC 8188) and
/// the VAPID identification header (RFC 8292).
///
/// A browser's subscription hands us an endpoint plus two values — the browser's
/// P-256 public key (<c>p256dh</c>) and a 16-byte <c>auth</c> secret. Only a payload
/// encrypted to those can be delivered: the push service (Mozilla's, Google's,
/// Apple's) relays bytes it cannot read, which is the whole point of the design and
/// the reason this cannot be a plain POST.
///
/// Everything here is deterministic given its salt and ephemeral key, so
/// <c>WebPushCryptoTests</c> checks it against RFC 8291's own test vector.
/// </summary>
public static class WebPushCrypto
{
    /// <summary>The record size we declare; one notification is far below it, so there is one record.</summary>
    public const int RecordSize = 4096;

    /// <summary>base64url without padding — how every value in this protocol travels.</summary>
    public static string B64Url(byte[] raw) =>
        Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Read a base64url value, padding tolerated (browsers send it either way).</summary>
    public static byte[] FromB64Url(string value)
    {
        var s = (value ?? "").Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// Encrypt one message for a subscription. <paramref name="salt"/> and
    /// <paramref name="ephemeral"/> are injectable so the RFC's vector can be
    /// reproduced; production passes neither and gets fresh random ones.
    /// </summary>
    public static byte[] Encrypt(byte[] payload, byte[] uaPublic, byte[] authSecret,
        byte[]? salt = null, ECDiffieHellman? ephemeral = null)
    {
        if (uaPublic.Length != 65 || uaPublic[0] != 0x04)
            throw new ArgumentException("p256dh must be a 65-byte uncompressed P-256 point", nameof(uaPublic));
        if (authSecret.Length != 16)
            throw new ArgumentException("auth must be 16 bytes", nameof(authSecret));

        salt ??= RandomNumberGenerator.GetBytes(16);
        var own = ephemeral ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var asPublic = Uncompressed(own.PublicKey.ExportParameters());
            var shared = own.DeriveRawSecretAgreement(Import(uaPublic).PublicKey);

            // RFC 8291 §3.3: the two public keys bind the keys to this subscription
            var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
            var prk = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo);
            var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, prk, 16, salt,
                Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
            var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, prk, 12, salt,
                Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

            // RFC 8188: the plaintext of the last record ends with the 0x02 delimiter
            var plain = Concat(payload, new byte[] { 0x02 });
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, plain, cipher, tag);

            // header: salt | record size | key length | ephemeral public key
            var header = Concat(salt, BigEndian(RecordSize), new byte[] { (byte)asPublic.Length }, asPublic);
            return Concat(header, cipher, tag);
        }
        finally
        {
            if (ephemeral == null) own.Dispose();
        }
    }

    /// <summary>
    /// The VAPID <c>Authorization</c> header value for an endpoint: a JWT signed with
    /// the server's own P-256 key, saying who is sending and for how long. The key is
    /// the deployment's identity to the push services — not a credential anyone issues
    /// us, which is why ChatBranch generates its own (<see cref="PushSettings"/>).
    /// </summary>
    public static string VapidHeader(string endpoint, byte[] privateKey, byte[] publicKey, string subject,
        DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        var claims = B64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience,
            // RFC 8292: at most 24 hours out; 12 keeps clock skew harmless
            ["exp"] = at.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));
        var signing = Encoding.ASCII.GetBytes($"{header}.{claims}");

        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey,
            Q = Point(publicKey),
        });
        // ES256 wants the raw r||s pair, not the DER encoding ECDsa defaults to
        var sig = ecdsa.SignData(signing, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{claims}.{B64Url(sig)}, k={B64Url(publicKey)}";
    }

    /// <summary>A fresh VAPID key pair: (private scalar, uncompressed public point).</summary>
    public static (byte[] Private, byte[] Public) NewVapidKeys()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(true);
        return (p.D!, Uncompressed(p));
    }

    /// <summary>
    /// An ECDH key pair from its raw halves (the private scalar and the uncompressed
    /// public point) — how a key that came from somewhere other than this process is
    /// loaded, and what lets the RFC's own test vector be reproduced.
    /// </summary>
    public static ECDiffieHellman ImportKeyPair(byte[] privateScalar, byte[] uncompressedPublic) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateScalar,
            Q = Point(uncompressedPublic),
        });

    private static ECDiffieHellman Import(byte[] uncompressed) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = Point(uncompressed),
        });

    private static ECPoint Point(byte[] uncompressed) => new()
    {
        X = uncompressed[1..33],
        Y = uncompressed[33..65],
    };

    private static byte[] Uncompressed(ECParameters p) =>
        Concat(new byte[] { 0x04 }, p.Q.X!, p.Q.Y!);

    private static byte[] BigEndian(int value) =>
        new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

    private static byte[] Concat(params byte[][] parts)
    {
        var out_ = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts)
        {
            p.CopyTo(out_, at);
            at += p.Length;
        }
        return out_;
    }
}
