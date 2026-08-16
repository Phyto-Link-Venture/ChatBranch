using System.Text.Json;

namespace ChatBranch.Data.Integrations;

/// <summary>
/// The organization-wide integration keys (Google OAuth client, webhook signing
/// secrets), read the ONE way.
///
/// They live inside a single JSON Setting row (<c>integrations_settings</c>),
/// not as one row per key — reading them with a plain
/// <c>SettingValue("google_client_id")</c> silently finds nothing, which is
/// exactly how the OAuth service first shipped believing no client was
/// registered. Resolution is: the JSON blob → a same-named Setting row → env.
/// </summary>
public static class IntegrationSettings
{
    public const string SettingsKey = "integrations_settings";

    /// <summary>Key → the environment variable that backs it.</summary>
    public static readonly (string Key, string Env)[] Keys =
    {
        ("google_client_id", "GOOGLE_CLIENT_ID"),
        ("google_client_secret", "GOOGLE_CLIENT_SECRET"),
        ("google_redirect_uri", "GOOGLE_REDIRECT_URI"),
        ("shopify_webhook_secret", "SHOPIFY_WEBHOOK_SECRET"),
        ("stripe_webhook_secret", "STRIPE_WEBHOOK_SECRET"),
    };

    /// <summary>The stored JSON blob as a flat dictionary (empty when unset).</summary>
    public static Dictionary<string, string> Stored(ScopingService sc)
    {
        var outv = new Dictionary<string, string>();
        var raw = sc.SettingValue(SettingsKey);
        if (string.IsNullOrWhiteSpace(raw)) return outv;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return outv;
            foreach (var p in doc.RootElement.EnumerateObject())
                outv[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? ""
                    : p.Value.ToString();
        }
        catch { /* malformed blob = nothing configured */ }
        return outv;
    }

    /// <summary>Effective value: stored blob → Setting row → env → "".</summary>
    public static string Value(ScopingService sc, string key)
    {
        var stored = (Stored(sc).GetValueOrDefault(key) ?? "").Trim();
        if (stored != "") return stored;
        var row = sc.SettingValue(key);
        if (row != "") return row;
        var envName = Keys.FirstOrDefault(g => g.Key == key).Env;
        return envName == null ? "" : (Environment.GetEnvironmentVariable(envName) ?? "").Trim();
    }

    public static bool GoogleConfigured(ScopingService sc) =>
        Value(sc, "google_client_id") != "" && Value(sc, "google_client_secret") != "";
}
