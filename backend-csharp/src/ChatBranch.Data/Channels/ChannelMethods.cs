using System.Text.Json;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The one vocabulary of HOW a channel is connected — the "connection method" a role
/// may be limited to (Role.ChannelAccess). WhatsApp's methods are its providers
/// (<see cref="WhatsAppProviders"/>); every other channel is either connected
/// <see cref="Direct"/> (its own credentials: a bot token, SMTP, a Twilio number…) or
/// through the managed <see cref="Providers.Zernio"/> connection where that exists.
/// A non-WhatsApp account's <c>Provider</c> column is otherwise meaningless (it keeps
/// the historical "cloud" default), so ask <see cref="Of(ChannelAccount)"/>, never the column.
/// </summary>
public static class ChannelMethods
{
    public const string Direct = "direct";

    public sealed record Method(string Id, string Label, bool Official);

    public sealed record ChannelDef(string Channel, string Label, IReadOnlyList<Method> Methods);

    private static readonly Method Managed = new(Providers.Zernio, "Managed connection", true);

    private static Method D(string label) => new(Direct, label, true);

    /// <summary>Every channel a role can be given, with its connection methods, in the order the UI shows them.</summary>
    public static readonly IReadOnlyList<ChannelDef> Catalog = new List<ChannelDef>
    {
        new(ChannelIds.WhatsApp, "WhatsApp", new[]
        {
            new Method(Providers.Cloud, "WhatsApp Cloud API (Meta)", true),
            new Method(Providers.YCloud, "YCloud", true),
            new Method(Providers.Twilio, "Twilio", true),
            Managed,
            new Method(Providers.Baileys, "QR code (WhatsApp Web)", false),
            new Method(Providers.Waha, "WAHA (self-hosted WhatsApp Web)", false),
        }),
        new(ChannelIds.Sms, "SMS", new[] { D("Twilio number"), Managed }),
        new(ChannelIds.Email, "Email", new[] { D("Mailbox (SMTP / IMAP)") }),
        new(ChannelIds.Telegram, "Telegram", new[] { D("Bot token"), Managed }),
        new(ChannelIds.Facebook, "Messenger", new[] { D("Page token"), Managed }),
        new(ChannelIds.Instagram, "Instagram", new[] { D("Account token"), Managed }),
        new(ChannelIds.Website, "Website chat", new[] { D("Website widget") }),
        new(ChannelIds.Line, "LINE", new[] { D("LINE Official Account") }),
        new(ChannelIds.Viber, "Viber", new[] { D("Viber bot") }),
        new(ChannelIds.WeChat, "WeChat", new[] { D("WeChat Official Account") }),
        new(ChannelIds.Zalo, "Zalo", new[] { D("Zalo Official Account") }),
        new(ChannelIds.TikTok, "TikTok", new[] { D("TikTok Business") }),
        new(ChannelIds.Douyin, "Douyin", new[] { D("Douyin enterprise account") }),
        new(ChannelIds.Shopee, "Shopee", new[] { D("Seller service") }),
        new(ChannelIds.Lazada, "Lazada", new[] { D("Seller service") }),
        new(ChannelIds.TikTokShop, "TikTok Shop", new[] { D("Seller service") }),
        new(ChannelIds.Shopify, "Shopify", new[] { D("Seller service") }),
    };

    public static ChannelDef? Find(string? channel) =>
        Catalog.FirstOrDefault(c => c.Channel == (channel ?? "").Trim().ToLowerInvariant());

    /// <summary>The method id of a channel + provider column pair.</summary>
    public static string Of(string? channel, string? provider)
    {
        if (channel == ChannelIds.WhatsApp) return WhatsAppProviders.Normalize(provider);
        return string.Equals(provider, Providers.Zernio, StringComparison.OrdinalIgnoreCase)
            ? Providers.Zernio : Direct;
    }

    public static string Of(ChannelAccount a) => Of(a.Channel, a.Provider);

    /// <summary>Is <paramref name="method"/> one of <paramref name="channel"/>'s methods?</summary>
    public static bool Known(string channel, string method) =>
        Find(channel)?.Methods.Any(m => m.Id == method) == true;

    public static string Label(string channel, string method)
    {
        var def = Find(channel);
        var m = def?.Methods.FirstOrDefault(x => x.Id == method);
        return $"{def?.Label ?? channel} ({m?.Label ?? method})";
    }
}

/// <summary>
/// A role's channel policy: which channels, and which connection methods of each,
/// people holding the role may CONNECT and USE. Stored as JSON on
/// <c>crm_role.channel_access</c>: <c>""</c> = every channel and method (the default,
/// unchanged behaviour); otherwise <c>{"whatsapp": ["cloud","ycloud"], "sms": ["direct"]}</c>
/// — a channel that is absent is not allowed at all. Superadmin is never limited.
/// </summary>
public sealed class RoleChannelPolicy
{
    private readonly Dictionary<string, HashSet<string>> _allowed;

    private RoleChannelPolicy(Dictionary<string, HashSet<string>> allowed) => _allowed = allowed;

    public IReadOnlyDictionary<string, HashSet<string>> Allowed => _allowed;

    public bool AllowsChannel(string? channel) =>
        channel != null && _allowed.TryGetValue(channel, out var m) && m.Count > 0;

    public bool Allows(string? channel, string method) =>
        channel != null && _allowed.TryGetValue(channel, out var m) && m.Contains(method);

    public bool Allows(ChannelAccount a) => Allows(a.Channel, ChannelMethods.Of(a));

    /// <summary>The allowed channels (with at least one method).</summary>
    public List<string> Channels => _allowed.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();

    /// <summary>Parse the stored column; "" / unreadable = null (no limit).</summary>
    public static RoleChannelPolicy? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stored);
            if (raw == null) return null;
            return new RoleChannelPolicy(raw.ToDictionary(kv => kv.Key,
                kv => kv.Value.Where(v => !string.IsNullOrWhiteSpace(v)).ToHashSet()));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Validate a request body (<c>null</c> = clear the limit; an object of channel → method
    /// ids). Unknown channels or methods are refused, never silently dropped. Returns the
    /// canonical stored JSON ("" = no limit) or an error.
    /// </summary>
    public static (string? Stored, string? Error) FromRequest(JsonElement? body)
    {
        if (body is not JsonElement el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return ("", null);
        if (el.ValueKind != JsonValueKind.Object)
            return (null, "channels must be null (every channel) or an object of {channel: [method, …]}.");
        var canon = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in el.EnumerateObject())
        {
            var def = ChannelMethods.Find(p.Name);
            if (def == null) return (null, $"Unknown channel '{p.Name}'.");
            if (p.Value.ValueKind != JsonValueKind.Array)
                return (null, $"channels['{p.Name}'] must be a list of connection methods.");
            var methods = new List<string>();
            foreach (var v in p.Value.EnumerateArray())
            {
                var id = v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim().ToLowerInvariant() : "";
                if (!ChannelMethods.Known(def.Channel, id))
                    return (null, $"'{(v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())}' is not a connection method of {def.Label}.");
                if (!methods.Contains(id)) methods.Add(id);
            }
            if (methods.Count > 0)
                canon[def.Channel] = def.Methods.Select(m => m.Id).Where(methods.Contains).ToList();
        }
        return (JsonSerializer.Serialize(canon), null);
    }

    /// <summary>The wire shape: null = every channel, else {channel: [methods]} in catalog order.</summary>
    public static Dictionary<string, List<string>>? ToPayload(RoleChannelPolicy? policy) =>
        policy == null
            ? null
            : ChannelMethods.Catalog
                .Where(c => policy.AllowsChannel(c.Channel))
                .ToDictionary(c => c.Channel,
                    c => c.Methods.Select(m => m.Id).Where(id => policy.Allows(c.Channel, id)).ToList());
}
