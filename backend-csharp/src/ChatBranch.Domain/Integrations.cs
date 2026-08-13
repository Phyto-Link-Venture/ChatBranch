namespace ChatBranch.Domain;

/// <summary>
/// Third-party integration connection (Django app: integrations) — ChannelAccount's
/// twin. Credentials live in a JSON bag that NEVER leaves the server unmasked;
/// status is written only through a health path. Providers: google_calendar /
/// drive / gmail / sheets (OAuth2), shopify / stripe (API-key). Entity-scoped
/// with the same global-fallback rule.
/// </summary>
public class Integration : IEntityScoped
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string Provider { get; set; } = "";       // google_calendar|drive|gmail|sheets|shopify|stripe
    public string Status { get; set; } = "unknown";   // unknown|connected|disconnected|revoked
    public string StatusDetail { get; set; } = "";
    // Secrets — never serialized to the client unmasked.
    public Dictionary<string, object?> Credentials { get; set; } = new();
    // Non-secret per-provider settings.
    public Dictionary<string, object?> Config { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
