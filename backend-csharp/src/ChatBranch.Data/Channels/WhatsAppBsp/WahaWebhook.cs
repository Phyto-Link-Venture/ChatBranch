using System.Text.Json;

namespace ChatBranch.Data.Channels;

/// <summary>One customer message out of a WAHA <c>message</c> event, ready for the inbound path.</summary>
public sealed record WahaInbound(string From, string Text, string? Name, string MessageId, DateTime? Timestamp,
    string ReplyTo = "", string MediaUrl = "", string MediaMime = "", string MediaName = "", string MediaType = "");

/// <summary>A WAHA <c>session.status</c> event: the session's new status (and the number, once it has one).</summary>
public sealed record WahaStatusEvent(string Session, string Status, string Me, string MeName);

/// <summary>What one WAHA webhook delivery carries (at most one of the two we subscribe to).</summary>
public sealed record WahaEvent(string Type, WahaInbound? Message = null, WahaStatusEvent? Status = null);

/// <summary>
/// WAHA's webhook envelope, parsed (pure, never throws). Every delivery is
/// <c>{id, timestamp, event, session, metadata, me, engine, environment, payload}</c>
/// (<c>WAHAWebhook&lt;T&gt;</c> in WAHA's <c>src/structures/webhooks.dto.ts</c>); ChatBranch
/// subscribes to exactly two events and acknowledges every other one unread:
///
/// <list type="bullet">
///   <item><c>message</c> → <c>payload</c> is a <c>WAMessage</c>: <c>id</c> (WhatsApp's own
///         <c>false_&lt;chat&gt;_&lt;hash&gt;</c> id — the dedup key), <c>timestamp</c> (unix
///         SECONDS), <c>from</c> / <c>to</c> (<c>&lt;digits&gt;@c.us</c>), <c>fromMe</c>,
///         <c>body</c>, <c>hasMedia</c> + <c>media {url, mimetype, filename}</c>,
///         <c>location</c>, <c>vCards</c>, <c>replyTo</c> and the engine's raw
///         <c>_data</c> (where the sender's <c>notifyName</c> lives).</item>
///   <item><c>session.status</c> → <c>payload {name, status, statuses, data}</c>: the session
///         lifecycle that drives account health, and where the paired number first becomes
///         known (the envelope's <c>me.id</c>).</item>
/// </list>
///
/// Only 1:1 chats become conversations here. A group (<c>@g.us</c>), a channel
/// (<c>@newsletter</c>), <c>status@broadcast</c> and anything we sent ourselves
/// (<c>fromMe</c>) are read and dropped — a WAHA number is a personal line, and groups on it
/// are not wired to ChatBranch's group-chat support yet.
/// </summary>
public static class WahaWebhook
{
    public const string MessageEvent = "message";
    public const string SessionStatusEvent = "session.status";

    /// <summary>The chat-id suffix of a one-to-one WhatsApp conversation.</summary>
    public const string UserSuffix = "@c.us";

    public static WahaEvent Parse(JsonElement root)
    {
        var type = ProviderHttp.Str(root, "event");
        var payload = ProviderHttp.At(root, "payload");
        if (payload.ValueKind != JsonValueKind.Object) return new WahaEvent(type);

        if (type == SessionStatusEvent)
        {
            var status = ProviderHttp.Str(payload, "status").Trim().ToUpperInvariant();
            if (status.Length == 0) return new WahaEvent(type);
            return new WahaEvent(type, Status: new WahaStatusEvent(
                ProviderHttp.Str(payload, "name") is { Length: > 0 } n ? n : ProviderHttp.Str(root, "session"),
                status,
                WahaWhatsAppClient.Digits(ProviderHttp.Str(root, "me", "id")),
                ProviderHttp.Str(root, "me", "pushName")));
        }

        if (type != MessageEvent) return new WahaEvent(type);

        // our own sends come back on message.any, not message — but never trust that
        if (ProviderHttp.Str(payload, "fromMe").Equals("true", StringComparison.OrdinalIgnoreCase))
            return new WahaEvent(type);

        var chat = ProviderHttp.Str(payload, "from").Trim();
        if (!chat.EndsWith(UserSuffix, StringComparison.OrdinalIgnoreCase)) return new WahaEvent(type);
        var from = WahaWhatsAppClient.Digits(chat);
        var id = ProviderHttp.Str(payload, "id");
        if (from.Length == 0 || id.Length == 0) return new WahaEvent(type);

        var media = ProviderHttp.At(payload, "media");
        var mime = ProviderHttp.Str(media, "mimetype");
        var url = ProviderHttp.Str(media, "url");
        var fileName = ProviderHttp.Str(media, "filename");
        var hasMedia = ProviderHttp.Str(payload, "hasMedia").Equals("true", StringComparison.OrdinalIgnoreCase)
                       || url.Length > 0 || mime.Length > 0;
        var kind = MediaKind(mime);

        var text = (ProviderHttp.Str(payload, "body") ?? "").Trim();
        if (text.Length == 0) text = Location(ProviderHttp.At(payload, "location"));
        if (text.Length == 0 && ProviderHttp.At(payload, "vCards") is { ValueKind: JsonValueKind.Array } cards
            && cards.GetArrayLength() > 0)
            text = "[contact]";
        // a file with no caption is stored as its placeholder, like every other channel
        if (text.Length == 0 && hasMedia) text = $"[{kind}]";
        if (text.Length == 0) return new WahaEvent(type);

        var name = ProviderHttp.Str(payload, "_data", "notifyName");
        if (name.Length == 0) name = ProviderHttp.Str(payload, "_data", "pushName");

        return new WahaEvent(type, new WahaInbound(
            from, text, name.Length > 0 ? name : null, id, Time(ProviderHttp.Long(payload, "timestamp")),
            ProviderHttp.Str(payload, "replyTo", "id"),
            hasMedia ? url : "", mime, fileName, kind));
    }

    /// <summary>WhatsApp's own ids are per message; <c>timestamp</c> is unix SECONDS.</summary>
    private static DateTime? Time(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;

    /// <summary>The media bucket the inbox renders, from the provider's MIME type.</summary>
    public static string MediaKind(string? mime)
    {
        var m = (mime ?? "").Trim().ToLowerInvariant();
        if (m.StartsWith("image/")) return m.Contains("webp") ? "sticker" : "image";
        if (m.StartsWith("video/")) return "video";
        if (m.StartsWith("audio/")) return "audio";
        return "document";
    }

    private static string Location(JsonElement loc)
    {
        if (loc.ValueKind != JsonValueKind.Object) return "";
        var lat = ProviderHttp.Str(loc, "latitude");
        var lng = ProviderHttp.Str(loc, "longitude");
        if (lat.Length == 0 || lng.Length == 0) return "";
        var label = string.Join(", ", new[] { ProviderHttp.Str(loc, "name"), ProviderHttp.Str(loc, "address") }
            .Where(s => s.Length > 0));
        return $"[location] {lat},{lng}" + (label.Length > 0 ? $" {label}" : "");
    }
}
