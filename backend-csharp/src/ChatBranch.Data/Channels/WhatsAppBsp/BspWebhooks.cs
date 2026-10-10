using System.Globalization;
using System.Text.Json;

namespace ChatBranch.Data.Channels;

/// <summary>One YCloud delivery status: the wamid AND YCloud's own id (we store whichever a send returned).</summary>
public sealed record BspStatus(CloudStatus Status, string ProviderId);

/// <summary>What one YCloud webhook event carries (at most one of the three).</summary>
public sealed record YCloudEvent(string Type, CloudInbound? Message, CloudReaction? Reaction, BspStatus? Status);

/// <summary>
/// YCloud's webhook events (pure parsing, never throws): an envelope
/// <c>{id, type, apiVersion, createTime, …}</c> with
/// <list type="bullet">
///   <item><c>whatsapp.inbound_message.received</c> → <c>whatsappInboundMessage</c>: Meta's message object
///         (type + the per-type object, <c>context</c> for a quote, <c>reaction</c>) plus
///         <c>from</c> ("+…"), <c>wamid</c>, <c>sendTime</c> (ISO) and <c>customerProfile.name</c>;
///         media objects carry a download <c>link</c>;</item>
///   <item><c>whatsapp.message.updated</c> → <c>whatsappMessage</c>: <c>id</c>, <c>wamid</c>, <c>status</c>
///         (accepted / sent / delivered / read / failed), <c>errorCode</c>, <c>errorMessage</c>,
///         <c>whatsappApiError</c> (Meta's error), and the per-state times.</item>
/// </list>
/// Everything else is acknowledged and ignored.
/// </summary>
public static class YCloudWebhook
{
    public const string InboundType = "whatsapp.inbound_message.received";
    public const string StatusType = "whatsapp.message.updated";

    public static YCloudEvent Parse(JsonElement root, string ourNumber)
    {
        var type = ProviderHttp.Str(root, "type");
        if (type == InboundType && root.TryGetProperty("whatsappInboundMessage", out var m)
            && m.ValueKind == JsonValueKind.Object)
        {
            var from = WhatsAppProviders.Digits(ProviderHttp.Str(m, "from"));
            var wamid = ProviderHttp.Str(m, "wamid") is { Length: > 0 } w ? w : ProviderHttp.Str(m, "id");
            var name = ProviderHttp.Str(m, "customerProfile", "name");
            if (from.Length == 0) return new YCloudEvent(type, null, null, null);
            if (ProviderHttp.Str(m, "type") == "reaction")
            {
                var target = ProviderHttp.Str(m, "reaction", "message_id");
                return target.Length == 0
                    ? new YCloudEvent(type, null, null, null)
                    : new YCloudEvent(type, null,
                        new CloudReaction(ourNumber, from, target, ProviderHttp.Str(m, "reaction", "emoji")), null);
            }
            var msg = MetaWebhook.ParseMessage(m, ourNumber, name.Length > 0 ? name : null, fromOverride: from,
                idOverride: wamid, tsOverride: Time(ProviderHttp.Str(m, "sendTime")), mediaByLink: true);
            return new YCloudEvent(type, msg, null, null);
        }
        if (type == StatusType && root.TryGetProperty("whatsappMessage", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            var status = ProviderHttp.Str(s, "status").ToLowerInvariant();
            var wamid = ProviderHttp.Str(s, "wamid");
            var id = ProviderHttp.Str(s, "id");
            int? code = null;
            var meta = (int)ProviderHttp.Long(s, "whatsappApiError", "code");
            if (meta > 0) code = meta;
            else if (int.TryParse(ProviderHttp.Str(s, "errorCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ec)
                     && ec > 0) code = ec;
            var title = ProviderHttp.Str(s, "errorMessage") is { Length: > 0 } em ? em
                : ProviderHttp.Str(s, "whatsappApiError", "message");
            var details = ProviderHttp.Str(s, "whatsappApiError", "error_data", "details");
            if (details.Length > 0 && !title.Contains(details)) title = title.Length > 0 ? $"{title} — {details}" : details;
            var ts = Time(ProviderHttp.Str(s, status switch
            {
                "delivered" => "deliverTime",
                "read" => "readTime",
                "sent" => "sendTime",
                _ => "updateTime",
            })) ?? Time(ProviderHttp.Str(s, "updateTime"));
            if (status.Length == 0 || (wamid.Length == 0 && id.Length == 0)) return new YCloudEvent(type, null, null, null);
            return new YCloudEvent(type, null, null, new BspStatus(
                new CloudStatus(ourNumber, wamid.Length > 0 ? wamid : id, status, WhatsAppProviders.Digits(ProviderHttp.Str(s, "to")),
                    ts, code, title), id));
        }
        return new YCloudEvent(type, null, null, null);
    }

    private static DateTime? Time(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.UtcDateTime : null;
}

/// <summary>
/// One Twilio WhatsApp webhook form (the incoming-message webhook AND the status callback share
/// the URL): <c>MessageStatus</c> / <c>SmsStatus</c> other than <c>received</c> = a delivery
/// status for <c>MessageSid</c> (queued / sending / sent / delivered / read / undelivered /
/// failed + <c>ErrorCode</c>); otherwise an inbound message — <c>From=whatsapp:+…</c>,
/// <c>ProfileName</c>, <c>Body</c>, <c>NumMedia</c> + <c>MediaUrl{n}</c> / <c>MediaContentType{n}</c>,
/// <c>Latitude</c> / <c>Longitude</c> / <c>Address</c> / <c>Label</c> (a location), <c>ButtonText</c>
/// (a quick-reply tap) and <c>OriginalRepliedMessageSid</c> (the message the customer quoted).
/// A <c>MessageType=reaction</c> form is recognised and never becomes a bubble.
/// </summary>
public sealed record TwilioWhatsAppForm(string Kind, string From, string ProfileName, string Text, string MessageSid,
    string Status, int ErrorCode, string ErrorMessage, string ReplyTo, List<InboundMediaRef> Media)
{
    public bool IsStatus => Kind == "status";
    public bool IsReaction => Kind == "reaction";
    public bool IsMessage => Kind == "message";

    private static readonly HashSet<string> StatusWords = new()
    {
        "accepted", "scheduled", "queued", "sending", "sent", "delivered", "read", "undelivered", "failed", "canceled",
    };

    public static TwilioWhatsAppForm Parse(IReadOnlyList<KeyValuePair<string, string>> form)
    {
        string F(string k) => form.FirstOrDefault(p => p.Key == k).Value ?? "";
        var status = (F("MessageStatus") is { Length: > 0 } ms ? ms : F("SmsStatus")).Trim().ToLowerInvariant();
        var from = F("From");
        if (from.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)) from = from[9..];
        var digits = WhatsAppProviders.Digits(F("WaId") is { Length: > 0 } wa ? wa : from);
        int.TryParse(F("ErrorCode"), out var errorCode);
        if (StatusWords.Contains(status))
            return new TwilioWhatsAppForm("status", digits, "", "", F("MessageSid"), status, errorCode,
                F("ErrorMessage"), "", new());

        var media = SmsInbound.Parse(form).Media;
        var type = F("MessageType").Trim().ToLowerInvariant();
        if (type == "reaction")
            return new TwilioWhatsAppForm("reaction", digits, F("ProfileName"), F("Body"), F("MessageSid"), "", 0, "",
                F("OriginalRepliedMessageSid"), new());
        var body = F("Body");
        var text = body.Trim().Length > 0 ? body : "";
        if (text.Length == 0 && F("ButtonText") is { Length: > 0 } button) text = button;
        if (F("Latitude") is { Length: > 0 } lat && F("Longitude") is { Length: > 0 } lng)
        {
            var label = string.Join(", ", new[] { F("Label"), F("Address") }.Where(s => s.Length > 0));
            text = $"[location] {lat},{lng}" + (label.Length > 0 ? $" {label}" : "");
        }
        if (text.Length == 0 && media.Count > 0)
            text = $"[{media[0].MediaType}]" + (media.Count > 1 ? $" (+{media.Count - 1} more)" : "");
        if (text.Length == 0) text = $"[{(type.Length > 0 ? type : "message")}]";
        return new TwilioWhatsAppForm("message", digits, F("ProfileName"), text, F("MessageSid"), "", 0, "",
            F("OriginalRepliedMessageSid"), media);
    }

    /// <summary>Twilio's status words → ours (undelivered is a failure; queued/sending stay "sent" at most).</summary>
    public string OurStatus => Status switch
    {
        "sent" => "sent",
        "delivered" => "delivered",
        "read" => "read",
        "undelivered" or "failed" or "canceled" => "failed",
        _ => "",
    };
}
