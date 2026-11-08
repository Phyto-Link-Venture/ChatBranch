using System.Text.Json;
using ChatBranch.Domain;

namespace ChatBranch.Data;

/// <summary>
/// What the server needs to reach a device, resolved Setting row → process
/// environment in one place.
///
/// **Web Push needs no account anywhere.** Its VAPID key pair is the deployment's
/// own identity to the push services, not a credential someone issues, so ChatBranch
/// generates one the first time it is asked and keeps it in two Setting rows. Browser
/// notifications therefore work with zero configuration, like the rest of the sandbox
/// path.
///
/// **The phone app needs Firebase**, because Apple and Google only accept pushes from
/// a registered sender. A service-account JSON (Firebase console → Project settings →
/// Service accounts) in Setting <c>fcm_service_account</c> — or env
/// <c>FCM_SERVICE_ACCOUNT</c> — switches it on; without one the app's own devices are
/// skipped with a clear reason rather than failing.
/// </summary>
public static class PushSettings
{
    public const string VapidPrivateKey = "push_vapid_private";
    public const string VapidPublicKey = "push_vapid_public";
    public const string VapidSubjectKey = "push_vapid_subject";
    public const string FcmServiceAccountKey = "fcm_service_account";

    public record Vapid(byte[] Private, byte[] Public, string Subject)
    {
        /// <summary>What the browser needs to subscribe (applicationServerKey).</summary>
        public string PublicKeyB64 => WebPushCrypto.B64Url(Public);
    }

    public record Firebase(string ProjectId, string ClientEmail, string PrivateKeyPem)
    {
        public bool Ok => ProjectId.Length > 0 && ClientEmail.Length > 0 && PrivateKeyPem.Length > 0;
    }

    /// <summary>
    /// The deployment's VAPID pair, generating and saving one the first time. Returns
    /// null only when the keys could not be stored — the caller then skips web pushes
    /// rather than minting a key pair per process, which would invalidate every
    /// subscription on restart.
    /// </summary>
    public static Vapid? GetOrCreateVapid(AppDbContext db, ScopingService scoping)
    {
        try
        {
            var priv = Read(scoping, VapidPrivateKey, "VAPID_PRIVATE_KEY");
            var pub = Read(scoping, VapidPublicKey, "VAPID_PUBLIC_KEY");
            if (priv.Length == 0 || pub.Length == 0)
            {
                var (p, q) = WebPushCrypto.NewVapidKeys();
                priv = WebPushCrypto.B64Url(p);
                pub = WebPushCrypto.B64Url(q);
                scoping.SetSetting(VapidPrivateKey, priv);
                scoping.SetSetting(VapidPublicKey, pub);
            }
            return new Vapid(WebPushCrypto.FromB64Url(priv), WebPushCrypto.FromB64Url(pub), Subject(scoping));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The VAPID public key alone — what the dashboard hands the browser.</summary>
    public static string? PublicKey(AppDbContext db, ScopingService scoping) =>
        GetOrCreateVapid(db, scoping)?.PublicKeyB64;

    /// <summary>
    /// Who is sending, per RFC 8292: the deployment's public address when it has one
    /// (an https URL is allowed and is the most honest answer), else a mailto.
    /// </summary>
    public static string Subject(ScopingService scoping)
    {
        var own = (scoping.SettingValue(VapidSubjectKey) ?? "").Trim();
        if (own.Length > 0) return own;
        var env = (Environment.GetEnvironmentVariable("VAPID_SUBJECT") ?? "").Trim();
        if (env.Length > 0) return env;
        var baseUrl = (scoping.SettingValue("public_base_url") ?? "").Trim();
        if (baseUrl.Length == 0)
            baseUrl = (Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")
                       ?? Environment.GetEnvironmentVariable("FRONTEND_URL") ?? "").Trim();
        return baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? baseUrl.TrimEnd('/')
            : "mailto:push@chatbranch.invalid";
    }

    /// <summary>The Firebase service account, or null when the phone app is not set up.</summary>
    public static Firebase? GetFirebase(ScopingService scoping)
    {
        try
        {
            var raw = (scoping.SettingValue(FcmServiceAccountKey) ?? "").Trim();
            if (raw.Length == 0)
                raw = (Environment.GetEnvironmentVariable("FCM_SERVICE_ACCOUNT")
                       ?? Environment.GetEnvironmentVariable("FCM_SERVICE_ACCOUNT_JSON") ?? "").Trim();
            if (raw.Length == 0) return null;
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string Get(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            var fb = new Firebase(Get("project_id"), Get("client_email"), Get("private_key"));
            return fb.Ok ? fb : null;
        }
        catch
        {
            return null;   // a malformed service account is "not configured", never a crash
        }
    }

    /// <summary>Is there any way at all to reach a device of this platform?</summary>
    public static bool Supports(AppDbContext db, ScopingService scoping, string platform) =>
        PushPlatforms.IsFirebase(platform)
            ? GetFirebase(scoping) != null
            : GetOrCreateVapid(db, scoping) != null;

    private static string Read(ScopingService scoping, string key, string envKey)
    {
        var v = (scoping.SettingValue(key) ?? "").Trim();
        return v.Length > 0 ? v : (Environment.GetEnvironmentVariable(envKey) ?? "").Trim();
    }
}
