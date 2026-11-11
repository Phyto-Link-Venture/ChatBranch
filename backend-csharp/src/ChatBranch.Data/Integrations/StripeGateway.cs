namespace ChatBranch.Data.Integrations;

/// <summary>One answer from the Stripe API: (ok, body or a capped error, HTTP status; 0 = no answer).</summary>
public sealed record StripeResponse(bool Ok, string Body, int Status)
{
    /// <summary>Stripe answered with a request error (4xx): nothing was created, retrying the same request is pointless.</summary>
    public bool Refused => !Ok && Status is >= 400 and < 500;
}

/// <summary>
/// The platform's calls to the Stripe API — the seam tests replace (the
/// <c>IZernioClient</c> / <c>ICommerceGateway</c> precedent), so the suite never
/// reaches api.stripe.com. Raw HTTP with form bodies, exactly like
/// <c>IntegrationActions.CreatePaymentLinkAsync</c>: no Stripe SDK in this repo.
/// </summary>
public interface IStripeGateway
{
    /// <summary>
    /// POST /v1/checkout/sessions. <paramref name="idempotencyKey"/> is sent as
    /// Stripe's <c>Idempotency-Key</c> header: the same key with the same form
    /// returns the SAME session, so a retried creation can never open two.
    /// </summary>
    Task<StripeResponse> CreateCheckoutSessionAsync(string secretKey, IReadOnlyDictionary<string, string> form,
        string idempotencyKey, CancellationToken ct = default);
}

/// <summary>The real thing: <see cref="ProviderHttp"/> against https://api.stripe.com.</summary>
public sealed class StripeHttpGateway : IStripeGateway
{
    public const string CheckoutSessionsUrl = "https://api.stripe.com/v1/checkout/sessions";
    private readonly IHttpClientFactory _http;
    public StripeHttpGateway(IHttpClientFactory http) => _http = http;

    public async Task<StripeResponse> CreateCheckoutSessionAsync(string secretKey,
        IReadOnlyDictionary<string, string> form, string idempotencyKey, CancellationToken ct = default)
    {
        var r = await ProviderHttp.SendAsync(_http, HttpMethod.Post, CheckoutSessionsUrl, secretKey,
            formBody: new Dictionary<string, string>(form),
            headers: new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey }, ct: ct);
        return new StripeResponse(r.Ok, r.Body, r.Status);
    }
}

/// <summary>A gateway that never leaves the process (the test host's default).</summary>
public sealed class NullStripeGateway : IStripeGateway
{
    public Task<StripeResponse> CreateCheckoutSessionAsync(string secretKey, IReadOnlyDictionary<string, string> form,
        string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(new StripeResponse(false, "Stripe is not reachable from this host.", 0));
}

/// <summary>
/// The platform's Stripe configuration, read the ONE way (<see cref="IntegrationSettings"/>):
/// <c>STRIPE_SECRET_KEY</c> (env first), <c>STRIPE_WEBHOOK_SECRET</c>, <c>STRIPE_MODE</c>
/// (test | live, default test).
/// </summary>
public static class StripeConfig
{
    public const string SecretKeyKey = "stripe_secret_key";
    public const string WebhookSecretKey = "stripe_webhook_secret";
    public const string ModeKey = "stripe_mode";
    public const string Test = "test";
    public const string Live = "live";

    public static string SecretKey(ScopingService sc) => IntegrationSettings.Value(sc, SecretKeyKey);
    public static string WebhookSecret(ScopingService sc) => IntegrationSettings.Value(sc, WebhookSecretKey);

    /// <summary>"live" only when configured exactly so; anything else is "test".</summary>
    public static string Mode(ScopingService sc) =>
        string.Equals(IntegrationSettings.Value(sc, ModeKey).Trim(), Live, StringComparison.OrdinalIgnoreCase)
            ? Live : Test;

    /// <summary>A secret (sk_) or restricted (rk_) key of the configured mode.</summary>
    public static bool KeyMatchesMode(string key, string mode) =>
        key.StartsWith($"sk_{mode}_", StringComparison.Ordinal) || key.StartsWith($"rk_{mode}_", StringComparison.Ordinal);

    // Stripe's zero-decimal currencies (charged in whole units). Three-decimal
    // currencies (BHD, JOD, KWD, OMR, TND) need special rounding and are refused.
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
        { "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF" };
    private static readonly HashSet<string> ThreeDecimal = new(StringComparer.OrdinalIgnoreCase)
        { "BHD", "JOD", "KWD", "OMR", "TND" };

    /// <summary>Minor units per major unit (100 for MYR/USD, 1 for JPY), or null when unsupported.</summary>
    public static int? MinorFactor(string currency)
    {
        var c = (currency ?? "").Trim();
        if (c.Length != 3 || ThreeDecimal.Contains(c)) return null;
        return ZeroDecimal.Contains(c) ? 1 : 100;
    }
}
