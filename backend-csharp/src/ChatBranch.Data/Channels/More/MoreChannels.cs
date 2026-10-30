using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The channel registry for the messaging apps added after the original set:
/// LINE, Viber, SMS (Twilio), WeChat Official Account, TikTok Business
/// Messaging, Douyin enterprise DMs and Zalo OA. One place answers "is this
/// account configured", "is the conversation bound to its account", "is it
/// phone-keyed" and "which inbox path verifies its webhook", so the routing,
/// the health prober, the accounts API and the send seam never enumerate the
/// seven ids themselves.
///
/// Where each channel keeps its credentials on <see cref="ChannelAccount"/>:
/// <list type="table">
///   <item><term>line</term><description>AccessToken = channel access token, AppSecret = channel secret, ExternalId = the bot's userId (learned).</description></item>
///   <item><term>viber</term><description>AccessToken = auth token (also the webhook HMAC key), ExternalId = the bot id (learned).</description></item>
///   <item><term>sms</term><description>AccessToken = Twilio auth token, Config account_sid / from_number / messaging_service_sid, ExternalId = the from number.</description></item>
///   <item><term>wechat</term><description>Config app_id / token / encoding_aes_key, AppSecret = AppSecret, ExternalId = the original id gh_… (learned).</description></item>
///   <item><term>tiktok</term><description>AccessToken = business access token, AppSecret = app secret (webhook HMAC), Config app_id, ExternalId = business_id.</description></item>
///   <item><term>douyin</term><description>AccessToken = the enterprise account's access token, AppSecret = client_secret (webhook signature), Config client_key / refresh_token, ExternalId = the account's open_id.</description></item>
///   <item><term>zalo</term><description>AccessToken = OA access token, AppSecret = app secret (token refresh), Config app_id / refresh_token / oa_secret_key (webhook mac), ExternalId = OA id (learned).</description></item>
/// </list>
/// </summary>
public static class MoreChannels
{
    public static readonly string[] All =
    {
        ChannelIds.Line, ChannelIds.Viber, ChannelIds.Sms, ChannelIds.WeChat,
        ChannelIds.TikTok, ChannelIds.Douyin, ChannelIds.Zalo,
        // The marketplace seller channels ride the same send path, routing and
        // health, so they belong in this registry — but their credentials live
        // in the seller-service sidecar, not on the account. Everything below
        // that differs for them defers to CommerceChannels.
        ChannelIds.Shopee, ChannelIds.Lazada, ChannelIds.TikTokShop, ChannelIds.Shopify,
    };

    public static bool Handles(string? channel) => channel != null && Array.IndexOf(All, channel) >= 0;

    /// <summary>Gated platforms: the API only works after the platform approves the app.</summary>
    public static bool RequiresApproval(string? channel) =>
        channel is ChannelIds.TikTok or ChannelIds.Douyin or ChannelIds.TikTokShop;

    /// <summary>
    /// The conversation identity is minted by the receiving account (a LINE userId,
    /// a Viber id, an OpenID, a Zalo user id…), so no other account can carry it.
    /// SMS is the exception: any of our numbers can text a phone number.
    /// </summary>
    public static bool AccountBound(string? channel) => Handles(channel) && channel != ChannelIds.Sms;

    /// <summary>
    /// A marketplace seller channel (<see cref="CommerceChannels"/>): it sends and
    /// routes like the rest, but the grant lives in the seller-service sidecar, so
    /// it has no typed credentials and no per-account webhook of its own.
    /// </summary>
    public static bool IsCommerce(string? channel) => CommerceChannels.Handles(channel);

    /// <summary>Phone-keyed channels link a contact by phone like WhatsApp does.</summary>
    public static bool PhoneKeyed(string? channel) => channel == ChannelIds.Sms;

    public static string Label(string channel) => channel switch
    {
        ChannelIds.Line => "LINE",
        ChannelIds.Viber => "Viber",
        ChannelIds.Sms => "SMS",
        ChannelIds.WeChat => "WeChat",
        ChannelIds.TikTok => "TikTok",
        ChannelIds.Douyin => "Douyin",
        ChannelIds.Zalo => "Zalo",
        _ => CommerceChannels.Handles(channel) ? CommerceChannels.Label(channel) : channel,
    };

    public static string Cfg(ChannelAccount a, string key) => EmailConfig.Cfg(a.Config ?? new(), key);

    /// <summary>Credentials present to send for real (sandbox otherwise).</summary>
    public static bool Configured(ChannelAccount a)
    {
        // a marketplace shop holds no credential here — it names a shop the
        // sidecar can act for (CommerceChannels.Configured)
        if (CommerceChannels.Handles(a.Channel)) return CommerceChannels.Configured(a);
        var token = !string.IsNullOrWhiteSpace(a.AccessToken);
        return a.Channel switch
        {
            ChannelIds.Line => token && !string.IsNullOrWhiteSpace(a.AppSecret),
            ChannelIds.Viber => token,
            ChannelIds.Sms => token && Cfg(a, "account_sid").Length > 0
                              && (Cfg(a, "from_number").Length > 0 || Cfg(a, "messaging_service_sid").Length > 0),
            ChannelIds.WeChat => Cfg(a, "app_id").Length > 0 && !string.IsNullOrWhiteSpace(a.AppSecret),
            ChannelIds.TikTok => token && a.ExternalId.Length > 0,
            ChannelIds.Douyin => token && a.ExternalId.Length > 0,
            ChannelIds.Zalo => token,
            _ => false,
        };
    }

    /// <summary>
    /// The account's inbound webhook path (each verifies with that account's own
    /// secret). A marketplace shop has none: the seller service verifies the
    /// marketplace's signature and pushes to <c>/api/commerce/inbound</c>, so
    /// there is no URL for the operator to paste anywhere.
    /// </summary>
    public static string WebhookPath(ChannelAccount a) =>
        CommerceChannels.Handles(a.Channel) ? "" : $"/api/messaging/webhook/{a.Channel}/{a.Id}";

    // ---- invite codes riding on the first message ---------------------------------

    /// <summary>A referral payload as the contacts binder reads it: <c>[ref:CODE] </c> — safe characters only.</summary>
    public static string? CleanRef(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var clean = Regex.Replace(payload.Trim(), @"[^A-Za-z0-9_\-:.@]", "");
        if (clean.Length == 0) return null;
        return clean.Length > 64 ? clean[..64] : clean;
    }

    /// <summary>Prefix <paramref name="text"/> with <c>[ref:CODE] </c> — only when the payload is non-empty.</summary>
    public static string WithRef(string text, string? payload) =>
        CleanRef(payload) is { } code ? $"[ref:{code}] {text}" : text;
}

/// <summary>How a provider refused a send — decides what the account's health does with it.</summary>
public enum TransportFailure
{
    None,
    /// <summary>Token / secret refused or revoked → disconnected.</summary>
    Auth,
    /// <summary>The account is blocked or suspended by the platform → banned.</summary>
    Banned,
    /// <summary>This recipient can't be reached (blocked us, unknown id…) → health untouched.</summary>
    Recipient,
    /// <summary>The platform's reply window forbids it → health untouched.</summary>
    Window,
    /// <summary>Throttled → transient (degraded once it keeps happening).</summary>
    RateLimit,
    /// <summary>Network / 5xx → transient.</summary>
    Transient,
    /// <summary>The platform can't carry this (e.g. a document on WeChat) → health untouched.</summary>
    Unsupported,
    /// <summary>Out of quota / balance → degraded.</summary>
    Billing,
}

/// <summary>One provider round-trip. <see cref="Detail"/> is the provider's own short reason, never a secret.</summary>
public sealed record TransportResult(bool Ok, string ExternalId = "", string Detail = "",
    TransportFailure Failure = TransportFailure.None, int HttpStatus = 0, string Code = "")
{
    /// <summary>The provider says this recipient unsubscribed (Twilio 21610) — mirror it on the conversation.</summary>
    public bool RecipientOptedOut { get; init; }

    public static TransportResult Success(string externalId) => new(true, externalId);

    public static TransportResult Fail(TransportFailure failure, string detail, int status = 0, string code = "") =>
        new(false, "", detail, failure, status, code);
}

/// <summary>Who a send goes to, with what the thread knows about the customer's last message.</summary>
public sealed record SendTarget(string Key, string? LastInboundId, DateTime? LastInboundAt, int ThreadId = 0);

/// <summary>A downloadable attachment on an inbound message (an id or a URL, by provider).</summary>
public sealed record InboundMediaRef(string Ref, string MediaType, string FileName);

/// <summary>A probe answer plus what the platform told us about the account.</summary>
public sealed record TransportProbe(ProbeVerdict Verdict, string Name = "", string LearnedId = "");

/// <summary>
/// One provider. Every implementation is a singleton over <see cref="IHttpClientFactory"/>
/// (named client <see cref="ProviderHttp.ClientName"/>, request logging off — several
/// of these APIs put a token in the URL), never throws, and never returns a secret
/// in a detail string. Tests run the REAL implementations against a fake HTTP handler.
/// </summary>
public interface IChannelTransport
{
    string Channel { get; }
    /// <summary>Longest text one message may carry; longer text is split.</summary>
    int MaxText { get; }
    /// <summary>Can upload a stored file (images at least).</summary>
    bool SupportsMedia { get; }
    Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text, CancellationToken ct = default);
    Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media, string? caption,
        CancellationToken ct = default);
    Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default);
    Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default);
}

/// <summary>Every <see cref="IChannelTransport"/>, by channel id (singleton).</summary>
public sealed class ChannelTransports
{
    private readonly Dictionary<string, IChannelTransport> _byChannel;

    public ChannelTransports(IEnumerable<IChannelTransport> transports) =>
        _byChannel = transports.GroupBy(t => t.Channel).ToDictionary(g => g.Key, g => g.Last());

    public IChannelTransport? For(string? channel) =>
        channel != null && _byChannel.TryGetValue(channel, out var t) ? t : null;

    public T? Get<T>() where T : class, IChannelTransport => _byChannel.Values.OfType<T>().FirstOrDefault();
}

/// <summary>A provider answer: <see cref="Status"/> 0 = no HTTP response (DNS, refused, timeout).</summary>
public sealed record HttpReply(int Status, JsonElement Json, string Error)
{
    public bool Is2xx => Status is >= 200 and < 300;
    public string Str(params string[] path) => ProviderHttp.Str(Json, path);
    public long Long(params string[] path) => ProviderHttp.Long(Json, path);
}

/// <summary>The HTTP plumbing every new transport shares.</summary>
public static class ProviderHttp
{
    /// <summary>The named client — registered with request logging OFF (tokens ride in URLs/headers).</summary>
    public const string ClientName = "channels";
    private const int MaxJsonBytes = 256 * 1024;

    /// <summary>UTF-8 JSON that keeps CJK / Vietnamese as raw characters (some Chinese APIs mangle \u escapes).</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static HttpClient Client(IHttpClientFactory f)
    {
        var c = f.CreateClient(ClientName);
        if (c.Timeout > TimeSpan.FromSeconds(60)) c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    public static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

    public static AuthenticationHeaderValue Basic(string user, string pass) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));

    /// <summary>Send and parse a JSON answer. Never throws; errors are scrubbed of <paramref name="secrets"/>.</summary>
    public static async Task<HttpReply> SendAsync(IHttpClientFactory f, HttpRequestMessage req, CancellationToken ct,
        params string?[] secrets)
    {
        try
        {
            using var resp = await Client(f).SendAsync(req, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            JsonElement json = default;
            if (bytes.Length > 0 && bytes.Length <= MaxJsonBytes)
            {
                try { json = JsonSerializer.Deserialize<JsonElement>(bytes); }
                catch { /* not JSON — an HTML error page, a binary body */ }
            }
            return new HttpReply((int)resp.StatusCode, json, "");
        }
        catch (Exception ex)
        {
            return new HttpReply(0, default, Scrub(ex.Message, secrets));
        }
        finally
        {
            req.Dispose();
        }
    }

    /// <summary>
    /// Download a file (≤ <paramref name="maxBytes"/>). A JSON body where a file was
    /// expected is a provider error (WeChat answers <c>{"errcode":…}</c> with 200).
    /// </summary>
    public static async Task<(byte[]? Content, string Detail)> DownloadAsync(IHttpClientFactory f,
        HttpRequestMessage req, long maxBytes, CancellationToken ct, params string?[] secrets)
    {
        try
        {
            using var resp = await Client(f).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return (null, $"download failed: HTTP {(int)resp.StatusCode}");
            if (resp.Content.Headers.ContentLength is long len && len > maxBytes) return (null, "file too large");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > maxBytes) return (null, "file too large");
            }
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                return (null, "provider answered with an error instead of the file");
            return (ms.ToArray(), "");
        }
        catch (Exception ex)
        {
            return (null, Scrub(ex.Message, secrets));
        }
        finally
        {
            req.Dispose();
        }
    }

    /// <summary>One line, capped, with every secret replaced.</summary>
    public static string Scrub(string? text, params string?[] secrets)
    {
        var s = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        foreach (var secret in secrets)
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 4) s = s.Replace(secret, "<secret>");
        return s.Length > 200 ? s[..200] : s;
    }

    public static JsonElement At(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var next)) e = next;
            else if (e.ValueKind == JsonValueKind.Array && int.TryParse(p, out var i) && i < e.GetArrayLength()) e = e[i];
            else return default;
        }
        return e;
    }

    public static string Str(JsonElement e, params string[] path)
    {
        var v = At(e, path);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    public static long Long(JsonElement e, params string[] path)
    {
        var v = At(e, path);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var m) ? m : 0;
    }

    /// <summary>Constant-time comparison of two byte strings (length leak avoided by hashing first).</summary>
    public static bool SameBytes(byte[] a, byte[] b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b)) && a.Length > 0;

    /// <summary>Constant-time comparison of two strings (case-sensitive).</summary>
    public static bool SameText(string? a, string? b) =>
        SameBytes(Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string Hmac256Hex(string key, byte[] body) =>
        Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body));

    public static IEnumerable<string> Chunks(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        if (max <= 0) { yield return text; yield break; }
        for (var i = 0; i < text.Length; i += max)
            yield return text.Substring(i, Math.Min(max, text.Length - i));
    }
}

/// <summary>
/// The platform said the customer unsubscribed / unfollowed / blocked us (Viber
/// <c>unsubscribed</c>, WeChat / LINE / Zalo unfollow, Twilio <c>OptOutType=STOP</c>)
/// — or came back. Mirrors <see cref="OptOut.Apply"/> (cancel queued jobs, a
/// private note, an audit row) for a signal that isn't a keyword. Never throws.
/// </summary>
public static class ChannelOptOut
{
    public static bool Set(AppDbContext db, ThreadStore threads, Thread thread, bool optOut, string reason)
    {
        try
        {
            if (thread.OptedOut == optOut) return false;
            thread.OptedOut = optOut;
            thread.UpdatedAt = DateTime.UtcNow;
            db.SaveChanges();
            var cancelled = optOut ? OptOut.CancelQueuedJobs(db, thread) : 0;
            var label = MoreChannels.Label(thread.Channel);
            var note = optOut
                ? $"[Contact {reason} on {label} — AI replies, flow and queued messages to this conversation are "
                  + "blocked. A manual send needs an explicit override."
                  + (cancelled > 0 ? $" {cancelled} queued message(s) cancelled." : "") + "]"
                : $"[Contact {reason} on {label} — messages to this conversation are allowed again.]";
            threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System, note, status: "note", priv: true);
            AuditTrail.Write(db, optOut ? "contact.opt_out" : "contact.opt_in", $"{thread.Channel}:{thread.Key}",
                $"platform: {reason}" + (cancelled > 0 ? $"; cancelled {cancelled} queued job(s)" : ""),
                actor: "customer", source: AuditSources.Webhook, entityId: thread.EntityId);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>
/// A referral payload that arrived BEFORE the customer's first message (Viber's
/// <c>conversation_started</c> context): kept as a private note on the thread
/// and put in front of their first real message as <c>[ref:CODE] </c>, where the
/// contacts' invite binder reads it.
/// </summary>
public static partial class PendingRef
{
    [GeneratedRegex(@"\[pending-ref:([A-Za-z0-9_\-:.@]{1,64})\]")]
    private static partial Regex Marker();

    public static void Remember(AppDbContext db, ThreadStore threads, Thread thread, string? payload)
    {
        if (MoreChannels.CleanRef(payload) is not { } code) return;
        var hasInbound = db.Messages.Any(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In);
        if (hasInbound) return;
        threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
            $"[pending-ref:{code}] Opened the chat from an invite link — the code rides on their first message.",
            status: "note", priv: true);
    }

    /// <summary>The waiting code for a thread whose customer has not written yet (null otherwise).</summary>
    public static string? Take(AppDbContext db, string channel, string key, int? accountId = null)
    {
        // the waiting code of THIS account's conversation (another company's chat keeps its own)
        var thread = new ThreadStore(db, new ScopingService(db)).FindForAccount(channel, key, accountId);
        if (thread == null) return null;
        if (db.Messages.Any(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)) return null;
        var note = db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Private && m.Source == MessageSource.System)
            .OrderByDescending(m => m.Id).Select(m => m.Text).ToList()
            .FirstOrDefault(t => Marker().IsMatch(t));
        return note == null ? null : Marker().Match(note).Groups[1].Value;
    }
}
