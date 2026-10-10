using ChatBranch.Domain;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The ONE place that knows what a WhatsApp <see cref="ChannelAccount.Provider"/> means.
///
/// <list type="table">
///   <item><term>cloud</term><description>Meta's WhatsApp Cloud API (Graph). AccessToken = system-user token,
///         ExternalId = phone_number_id, WabaId, AppSecret = the Meta app secret (webhook signature).</description></item>
///   <item><term>baileys</term><description>A QR-paired personal number through the Node gateway (unofficial).</description></item>
///   <item><term>ycloud</term><description>YCloud (a Meta Business Solution Provider). AccessToken = the API key
///         (<c>X-API-Key</c>), ExternalId = the business number's digits (the <c>from</c>), WabaId = optional
///         WABA id (templates), AppSecret = the webhook endpoint's signing secret.</description></item>
///   <item><term>twilio</term><description>Twilio's WhatsApp sender. AccessToken = the Auth Token (REST + the
///         webhook signature), Config <c>account_sid</c>, <c>from_number</c> (the <c>whatsapp:+…</c> sender)
///         or <c>messaging_service_sid</c>, optional <c>api_key_sid</c> + <c>api_key_secret</c> (REST auth
///         instead of the token); ExternalId = the sender's digits (or the service SID).</description></item>
/// </list>
///
/// cloud, ycloud and twilio are OFFICIAL business numbers: templates, Meta's 24h
/// customer-service window, the reminder role by default — everything that used
/// to say "Cloud" now asks <see cref="IsOfficial(ChannelAccount)"/>.
/// </summary>
public static class WhatsAppProviders
{
    /// <summary>Every provider a WhatsApp account may have.</summary>
    public static readonly string[] All = { Providers.Cloud, Providers.Baileys, Providers.YCloud, Providers.Twilio };

    /// <summary>The official (Meta-sanctioned) ones: template-capable, bound by the 24h window.</summary>
    public static readonly string[] Official = { Providers.Cloud, Providers.YCloud, Providers.Twilio };

    /// <summary>A <see cref="RouteNeed.Provider"/> value meaning "any official provider".</summary>
    public const string AnyOfficial = "official";

    /// <summary>The provider id, "" / unknown read as cloud (the historical default).</summary>
    public static string Of(ChannelAccount a) => Normalize(a.Provider);

    public static string Normalize(string? provider)
    {
        var p = (provider ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(All, p) >= 0 ? p : Providers.Cloud;
    }

    /// <summary>Is this a provider id the API accepts (as typed)?</summary>
    public static bool Known(string? provider) => Array.IndexOf(All, (provider ?? "").Trim().ToLowerInvariant()) >= 0;

    public static bool IsOfficial(string? provider) => Normalize(provider) != Providers.Baileys;

    public static bool IsOfficial(ChannelAccount a) => IsOfficial(a.Provider);

    /// <summary>A Business Solution Provider (not Meta's own Graph API, not Baileys).</summary>
    public static bool IsBsp(string? provider) => Normalize(provider) is Providers.YCloud or Providers.Twilio;

    public static bool IsBsp(ChannelAccount a) => IsBsp(a.Provider);

    /// <summary>Meta's own Graph API (the provider the Meta webhook and Graph pings serve).</summary>
    public static bool IsMeta(ChannelAccount a) => Of(a) == Providers.Cloud;

    /// <summary>Does <paramref name="a"/> satisfy a routing provider need (a concrete id or <see cref="AnyOfficial"/>)?</summary>
    public static bool Matches(ChannelAccount a, string? need) =>
        need == null || (need == AnyOfficial ? IsOfficial(a) : Of(a) == need);

    public static string Cfg(ChannelAccount a, string key) => EmailConfig.Cfg(a.Config ?? new(), key);

    /// <summary>Credentials present to send for real (sandbox otherwise).</summary>
    public static bool Configured(ChannelAccount a) => Of(a) switch
    {
        Providers.Baileys => true, // paired through the gateway, no credentials here
        Providers.YCloud => a.AccessToken.Length > 0 && a.ExternalId.Length > 0,
        Providers.Twilio => TwilioRestAuth(a) && Cfg(a, "account_sid").Length > 0
                            && (Cfg(a, "from_number").Length > 0 || Cfg(a, "messaging_service_sid").Length > 0),
        _ => a.AccessToken.Length > 0 && a.ExternalId.Length > 0,
    };

    /// <summary>Twilio REST auth: the Auth Token, or an API Key SID + secret.</summary>
    public static bool TwilioRestAuth(ChannelAccount a) =>
        a.AccessToken.Length > 0 || (Cfg(a, "api_key_sid").Length > 0 && Cfg(a, "api_key_secret").Length > 0);

    /// <summary>Can this account send an emoji reaction? (Twilio's WhatsApp API has none.)</summary>
    public static bool SupportsReactions(ChannelAccount a) => Of(a) != Providers.Twilio;

    /// <summary>Does a reply carry the quoted message? (Twilio sends it unquoted.)</summary>
    public static bool SupportsQuotes(ChannelAccount a) => Of(a) != Providers.Twilio;

    /// <summary>
    /// Is there a live template registry behind this account? Cloud and YCloud read
    /// the WABA's templates (a WABA id is needed); Twilio lists its Content API
    /// templates per account.
    /// </summary>
    public static bool HasTemplateRegistry(ChannelAccount a) => Of(a) switch
    {
        Providers.Baileys => false,
        Providers.Twilio => true,
        _ => a.WabaId.Length > 0,
    };

    /// <summary>The webhook a BSP posts to (one per ACCOUNT, verified with that account's secret).</summary>
    public static string? WebhookPath(ChannelAccount a) => Of(a) switch
    {
        Providers.YCloud => $"/api/messaging/webhook/whatsapp-ycloud/{a.Id}",
        Providers.Twilio => $"/api/messaging/webhook/whatsapp-twilio/{a.Id}",
        _ => null,
    };

    public static string Label(string? provider) => Normalize(provider) switch
    {
        Providers.Baileys => "WhatsApp (QR)",
        Providers.YCloud => "WhatsApp (YCloud)",
        Providers.Twilio => "WhatsApp (Twilio)",
        _ => "WhatsApp Cloud API",
    };

    /// <summary>"+" and digits — what YCloud and Twilio expect for a number.</summary>
    public static string E164(string? raw)
    {
        var d = new string((raw ?? "").Where(char.IsAsciiDigit).ToArray());
        return d.Length == 0 ? "" : $"+{d}";
    }

    public static string Digits(string? raw) => new((raw ?? "").Where(char.IsAsciiDigit).ToArray());
}
