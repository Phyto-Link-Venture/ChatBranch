using System.Security.Cryptography;
using System.Text;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// SMS / MMS through Twilio's Programmable Messaging REST API (Basic auth
/// AccountSid:AuthToken):
/// <list type="bullet">
///   <item>out — <c>POST /2010-04-01/Accounts/{sid}/Messages.json</c> with <c>From</c>
///         (the inbox number) or <c>MessagingServiceSid</c>; text up to 1600 chars per
///         message (Twilio concatenates segments). MMS needs a PUBLIC media URL, so
///         uploads are refused honestly;</item>
///   <item>in — the incoming-message webhook (form post), verified with
///         <c>X-Twilio-Signature</c> = base64(HMAC-SHA1(auth token, URL + sorted
///         key+value pairs)); MMS media (<c>MediaUrl0</c>) is downloaded with Basic
///         auth off the ack; Twilio's own STOP/START handling (<c>OptOutType</c>) is
///         mirrored onto the conversation;</item>
///   <item>health — <c>GET /Accounts/{sid}.json</c> (active / suspended / closed).</item>
/// </list>
/// The thread key is the E.164 number's digits — SMS is phone-keyed, so contacts
/// link by phone like WhatsApp.
/// </summary>
public sealed class TwilioSmsTransport : IChannelTransport
{
    public const string ApiBase = "https://api.twilio.com/2010-04-01";
    public const string SignatureHeader = "X-Twilio-Signature";

    private readonly IHttpClientFactory _http;

    public TwilioSmsTransport(IHttpClientFactory http) => _http = http;

    public string Channel => ChannelIds.Sms;
    public int MaxText => 1600;
    public bool SupportsMedia => false;

    private static string Sid(ChannelAccount a) => MoreChannels.Cfg(a, "account_sid");

    /// <summary>"+" and digits — what Twilio expects in To / From.</summary>
    public static string E164(string? raw)
    {
        var d = PhoneMatch.Digits(raw);
        return d.Length == 0 ? "" : $"+{d}";
    }

    public async Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default)
    {
        var sid = Sid(a);
        var form = new List<KeyValuePair<string, string>>
        {
            new("To", E164(to.Key)),
            new("Body", text),
        };
        var service = MoreChannels.Cfg(a, "messaging_service_sid");
        var from = MoreChannels.Cfg(a, "from_number");
        if (service.Length > 0) form.Add(new("MessagingServiceSid", service));
        else form.Add(new("From", E164(from)));
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{ApiBase}/Accounts/{Uri.EscapeDataString(sid)}/Messages.json")
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Authorization = ProviderHttp.Basic(sid, a.AccessToken);
        return Result(await ProviderHttp.SendAsync(_http, req, ct, a.AccessToken));
    }

    public Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media, string? caption,
        CancellationToken ct = default) =>
        Task.FromResult(TransportResult.Fail(TransportFailure.Unsupported,
            "MMS needs a public media URL — send the file as a link instead"));

    /// <summary>
    /// Twilio errors (<c>{"code":…,"message":…}</c>): auth and sender problems are the
    /// account's, an unreachable / unsubscribed number is the recipient's (21610 also
    /// marks the conversation opted out — Twilio will refuse it until they text START).
    /// </summary>
    public static TransportResult Result(HttpReply r)
    {
        if (r.Is2xx) return TransportResult.Success(r.Str("sid"));
        if (r.Status == 0) return TransportResult.Fail(TransportFailure.Transient, $"Twilio unreachable: {r.Error}");
        var code = (int)r.Long("code");
        var message = r.Str("message");
        var why = $"Twilio {(code > 0 ? code.ToString() : r.Status.ToString())}: {(message.Length > 0 ? message : "error")}";
        var failure = code switch
        {
            20003 or 20404 => TransportFailure.Auth,                     // bad credentials / unknown account
            20005 or 10001 => TransportFailure.Banned,                   // account not active / suspended
            21606 or 21212 or 21659 or 21660 => TransportFailure.Auth, // the From isn't ours / not SMS-capable
            20429 or 14107 or 30022 => TransportFailure.RateLimit,
            30001 => TransportFailure.Transient,                          // queue overflow
            _ when r.Status == 401 => TransportFailure.Auth,
            _ when r.Status == 429 => TransportFailure.RateLimit,
            _ when r.Status >= 500 => TransportFailure.Transient,
            _ => TransportFailure.Recipient,                              // 21211 invalid To, 21610 unsubscribed, 21614 not mobile…
        };
        return TransportResult.Fail(failure, why, r.Status, code > 0 ? code.ToString() : "")
            with { RecipientOptedOut = code == 21610 };
    }

    public async Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        var sid = Sid(a);
        var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/Accounts/{Uri.EscapeDataString(sid)}.json");
        req.Headers.Authorization = ProviderHttp.Basic(sid, a.AccessToken);
        var r = await ProviderHttp.SendAsync(_http, req, ct, a.AccessToken);
        if (!r.Is2xx)
        {
            var res = Result(r);
            return new(res.Failure == TransportFailure.Auth
                ? new ProbeVerdict(Health.Disconnected, res.Detail)
                : new ProbeVerdict(null, res.Detail, Transient: true));
        }
        var name = r.Str("friendly_name");
        return r.Str("status") switch
        {
            "suspended" => new(new ProbeVerdict(Health.Banned, "Twilio account suspended"), name),
            "closed" => new(new ProbeVerdict(Health.Disconnected, "Twilio account closed"), name),
            _ => new(new ProbeVerdict(Health.Healthy, name.Length > 0 ? $"Connected as {name}" : "Connected"), name),
        };
    }

    /// <summary>
    /// MMS media: Twilio-hosted (api.twilio.com) only — our credentials are never
    /// sent to any other host. Twilio redirects to its CDN with a pre-signed URL.
    /// </summary>
    public Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(media.Ref, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host == "api.twilio.com" || uri.Host.EndsWith(".twilio.com", StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult<(byte[]?, string)>((null, "not a Twilio media URL"));
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = ProviderHttp.Basic(Sid(a), a.AccessToken);
        return ProviderHttp.DownloadAsync(_http, req, MessagingMedia.MaxBytes, ct, a.AccessToken);
    }

    // ---- webhook signature ---------------------------------------------------------

    /// <summary>
    /// Twilio's request signature: the full URL Twilio called (scheme, host, path and
    /// query) followed by every POST parameter as name+value, names sorted
    /// case-sensitively; HMAC-SHA1 with the auth token, base64.
    /// </summary>
    public static string Sign(string authToken, string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        var sb = new StringBuilder(url);
        foreach (var (k, v) in form.OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal))
            sb.Append(k).Append(v);
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken),
            Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>True when the header matches the signature for ANY of the candidate public URLs.</summary>
    public static bool VerifySignature(string authToken, IEnumerable<string> candidateUrls,
        IReadOnlyList<KeyValuePair<string, string>> form, string? header)
    {
        if (string.IsNullOrEmpty(authToken) || string.IsNullOrWhiteSpace(header)) return false;
        var ok = false;
        foreach (var url in candidateUrls.Where(u => u.Length > 0).Distinct())
            ok |= ProviderHttp.SameText(Sign(authToken, url, form), header.Trim()); // no early exit
        return ok;
    }
}

/// <summary>One inbound SMS / MMS from Twilio's webhook form.</summary>
public sealed record SmsInbound(string From, string To, string Body, string MessageSid, string OptOutType,
    List<InboundMediaRef> Media)
{
    public static SmsInbound Parse(IReadOnlyList<KeyValuePair<string, string>> form)
    {
        string F(string k) => form.FirstOrDefault(p => p.Key == k).Value ?? "";
        var media = new List<InboundMediaRef>();
        if (int.TryParse(F("NumMedia"), out var n))
            for (var i = 0; i < Math.Min(n, 10); i++)
            {
                var url = F($"MediaUrl{i}");
                if (url.Length == 0) continue;
                var mime = F($"MediaContentType{i}");
                var type = mime.StartsWith("image/") ? "image" : mime.StartsWith("video/") ? "video"
                    : mime.StartsWith("audio/") ? "audio" : "document";
                var ext = mime switch
                {
                    "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp",
                    "video/mp4" => ".mp4", "audio/mpeg" => ".mp3", "audio/ogg" => ".ogg", "audio/amr" => ".amr",
                    "application/pdf" => ".pdf", "text/vcard" or "text/x-vcard" => ".vcf",
                    _ => ".bin",
                };
                media.Add(new InboundMediaRef(url, type, $"mms-{i + 1}{ext}"));
            }
        return new SmsInbound(F("From"), F("To"), F("Body"), F("MessageSid"),
            F("OptOutType").Trim().ToUpperInvariant(), media);
    }

    /// <summary>The bubble text: the body, else a placeholder for the media.</summary>
    public string Text =>
        Body.Trim().Length > 0 ? Body
        : Media.Count > 0 ? $"[{Media[0].MediaType}]" + (Media.Count > 1 ? $" (+{Media.Count - 1} more)" : "")
        : "[message]";
}
