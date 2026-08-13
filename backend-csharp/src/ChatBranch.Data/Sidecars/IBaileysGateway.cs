namespace ChatBranch.Data.Sidecars;

public record BaileysResult(bool Ok, string Detail);

/// <summary>
/// The Node Baileys gateway (unofficial WhatsApp Web) stays a sidecar — no C#
/// port exists. This is the HTTP contract the backend uses to drive it
/// (X-Baileys-Secret auth, like /inbound). The sandbox default keeps the app
/// runnable with no gateway attached, exactly like the Django app.
/// </summary>
public interface IBaileysGateway
{
    bool IsConfigured { get; }
    Task<BaileysResult> SendTextAsync(string accountExternalId, string toWaId, string text,
        CancellationToken ct = default);
    Task<BaileysResult> StartCallAsync(string accountExternalId, string toWaId,
        Dictionary<string, object?> dispatch, CancellationToken ct = default);
}

/// <summary>Zero-config fallback: every call is a logged skip, run continues.</summary>
public class SandboxBaileysGateway : IBaileysGateway
{
    public bool IsConfigured => false;

    public Task<BaileysResult> SendTextAsync(string accountExternalId, string toWaId, string text,
        CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "baileys gateway not configured (sandbox)"));

    public Task<BaileysResult> StartCallAsync(string accountExternalId, string toWaId,
        Dictionary<string, object?> dispatch, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(false, "baileys gateway not configured (sandbox)"));
}
