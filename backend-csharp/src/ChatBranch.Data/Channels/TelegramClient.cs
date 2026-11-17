using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Data.Channels;

/// <summary>
/// One Bot API answer. <see cref="HttpStatus"/> 0 = the request never got an
/// HTTP response (DNS, refused, timeout). <see cref="Description"/> is
/// Telegram's own error text or a scrubbed exception message — never the token.
/// </summary>
public sealed record TelegramResult(bool Ok, int HttpStatus, string Description, JsonElement Result)
{
    public static TelegramResult Fail(int status, string description) =>
        new(false, status, description, default);

    /// <summary>The token is wrong or revoked (Telegram answers 401, or 404 for a malformed one).</summary>
    public bool TokenRejected => HttpStatus is 401 or 404;

    public string Str(string key) =>
        Result.ValueKind == JsonValueKind.Object && Result.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()
            : "";

    public long Long(string key) =>
        Result.ValueKind == JsonValueKind.Object && Result.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}

/// <summary>
/// Telegram Bot API client (port of messaging/telegram_client.py): a BotFather
/// token per <see cref="ChannelAccount"/> (<c>access_token</c>), a webhook
/// registered with <c>setWebhook</c>, <c>sendMessage</c> / <c>sendPhoto</c> /
/// <c>sendDocument</c> out, <c>getFile</c> for inbound media. There is no
/// messaging window — a bot may write to any chat that wrote to it first.
/// </summary>
public interface ITelegramClient
{
    Task<TelegramResult> CallAsync(string token, string method, object? payload = null,
        CancellationToken ct = default);

    /// <summary>Multipart upload (sendPhoto / sendDocument / sendAudio / sendVideo).</summary>
    Task<TelegramResult> SendFileAsync(string token, string method, string field, string chatId,
        byte[] content, string fileName, string? caption, CancellationToken ct = default);

    /// <summary>getFile + the file download. Null content = failed or larger than <paramref name="maxBytes"/>.</summary>
    Task<(byte[]? Content, string FileName, string Detail)> DownloadFileAsync(string token, string fileId,
        long maxBytes, CancellationToken ct = default);
}

public sealed class TelegramClient : ITelegramClient
{
    public const string DefaultBaseUrl = "https://api.telegram.org";
    private readonly IHttpClientFactory _http;
    private readonly string _base;

    public TelegramClient(IHttpClientFactory http)
        : this(http, Environment.GetEnvironmentVariable("TELEGRAM_API_BASE")) { }

    /// <param name="baseUrl">A self-hosted Bot API server (TELEGRAM_API_BASE); default api.telegram.org.</param>
    public TelegramClient(IHttpClientFactory http, string? baseUrl)
    {
        _http = http;
        _base = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
    }

    private HttpClient Client()
    {
        var c = _http.CreateClient("telegram");
        if (c.Timeout > TimeSpan.FromSeconds(60)) c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    public async Task<TelegramResult> CallAsync(string token, string method, object? payload = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return TelegramResult.Fail(0, "bot token not configured");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/bot{token}/{method}")
            {
                Content = JsonContent.Create(payload ?? new { }),
            };
            using var resp = await Client().SendAsync(req, ct);
            return await Read(resp, token, ct);
        }
        catch (Exception ex)
        {
            return TelegramResult.Fail(0, Scrub(ex.Message, token));
        }
    }

    public async Task<TelegramResult> SendFileAsync(string token, string method, string field, string chatId,
        byte[] content, string fileName, string? caption, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return TelegramResult.Fail(0, "bot token not configured");
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId), "chat_id");
            if (!string.IsNullOrWhiteSpace(caption))
                form.Add(new StringContent(caption.Length > 1024 ? caption[..1024] : caption), "caption");
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue(MessagingMedia.ContentTypeFor(fileName));
            form.Add(file, field, fileName);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/bot{token}/{method}") { Content = form };
            using var resp = await Client().SendAsync(req, ct);
            return await Read(resp, token, ct);
        }
        catch (Exception ex)
        {
            return TelegramResult.Fail(0, Scrub(ex.Message, token));
        }
    }

    public async Task<(byte[]? Content, string FileName, string Detail)> DownloadFileAsync(string token,
        string fileId, long maxBytes, CancellationToken ct = default)
    {
        var info = await CallAsync(token, "getFile", new { file_id = fileId }, ct);
        if (!info.Ok) return (null, "", info.Description);
        var path = info.Str("file_path");
        if (path.Length == 0) return (null, "", "getFile returned no file_path");
        if (info.Long("file_size") is var size and > 0 && size > maxBytes) return (null, "", "file too large");
        try
        {
            using var resp = await Client().GetAsync($"{_base}/file/bot{token}/{path}",
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return (null, "", $"file download failed: HTTP {(int)resp.StatusCode}");
            if (resp.Content.Headers.ContentLength is long len && len > maxBytes) return (null, "", "file too large");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > maxBytes) return (null, "", "file too large");
            }
            return (ms.ToArray(), path.Split('/').Last(), "");
        }
        catch (Exception ex)
        {
            return (null, "", Scrub(ex.Message, token));
        }
    }

    private static async Task<TelegramResult> Read(HttpResponseMessage resp, string token, CancellationToken ct)
    {
        JsonElement body = default;
        try { body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct); }
        catch { /* non-JSON error page */ }
        var ok = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("ok", out var o)
                 && o.ValueKind == JsonValueKind.True;
        if (ok)
            return new TelegramResult(true, (int)resp.StatusCode, "",
                body.TryGetProperty("result", out var r) ? r.Clone() : default);
        var desc = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("description", out var d)
            ? d.GetString() ?? "" : "";
        var code = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error_code", out var e)
                   && e.TryGetInt32(out var ec) ? ec : (int)resp.StatusCode;
        return TelegramResult.Fail((int)resp.StatusCode,
            Scrub($"Telegram API {code}: {(desc.Length > 0 ? desc : resp.ReasonPhrase)}", token));
    }

    /// <summary>One line, capped, never the bot token.</summary>
    public static string Scrub(string? text, string token)
    {
        var s = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (!string.IsNullOrEmpty(token)) s = s.Replace(token, "<bot-token>");
        return s.Length > 200 ? s[..200] : s;
    }
}

/// <summary>Bot API conveniences over <see cref="ITelegramClient"/>.</summary>
public static class TelegramBot
{
    public static bool Configured(ChannelAccount a) =>
        a.Channel == ChannelIds.Telegram && !string.IsNullOrWhiteSpace(a.AccessToken);

    /// <summary>The <c>Config</c> key holding the bot's @username (learned from getMe on Test).</summary>
    public const string UsernameKey = "username";

    /// <summary>The bot's username without the "@" — <c>Config["username"]</c>, else an account
    /// name written as "@name" — or "" when unknown. A group message mentioning it is addressed to us.</summary>
    public static string Username(ChannelAccount a)
    {
        if (a.Config != null && a.Config.TryGetValue(UsernameKey, out var v) && v != null)
        {
            var s = (v is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : v.ToString()) ?? "";
            s = s.Trim().TrimStart('@');
            if (s.Length > 0) return s;
        }
        var n = (a.Name ?? "").Trim();
        return n.StartsWith('@') && n.Length > 1 && !n.Contains(' ') ? n[1..] : "";
    }

    public static Task<TelegramResult> GetMeAsync(this ITelegramClient c, ChannelAccount a, CancellationToken ct = default) =>
        c.CallAsync(a.AccessToken, "getMe", null, ct);

    public static Task<TelegramResult> GetWebhookInfoAsync(this ITelegramClient c, ChannelAccount a,
        CancellationToken ct = default) =>
        c.CallAsync(a.AccessToken, "getWebhookInfo", null, ct);

    public static Task<TelegramResult> SetWebhookAsync(this ITelegramClient c, ChannelAccount a, string url,
        string secretToken, CancellationToken ct = default) =>
        c.CallAsync(a.AccessToken, "setWebhook", new
        {
            url,
            secret_token = secretToken,
            allowed_updates = new[] { "message", "message_reaction" },
        }, ct);

    /// <summary>
    /// "typing…" in the chat (sendChatAction). Telegram shows it for ~5 s or until
    /// the bot's next message — the AI's human-like typing pause.
    /// </summary>
    public static Task<TelegramResult> SendChatActionAsync(this ITelegramClient c, ChannelAccount a, string chatId,
        string action = "typing", CancellationToken ct = default) =>
        c.CallAsync(a.AccessToken, "sendChatAction", new { chat_id = chatId, action }, ct);

    /// <summary>The webhook secret Telegram echoes in X-Telegram-Bot-Api-Secret-Token ("" = none set).</summary>
    public static string WebhookSecret(ChannelAccount a) => EmailConfig.Cfg(a.Config ?? new(), "webhook_secret");

    /// <summary>1–256 chars of A-Z a-z 0-9 _ - (Telegram's secret_token alphabet).</summary>
    public static string NewWebhookSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Constant-time string equality that does not leak the length either.</summary>
    public static bool SecretEquals(string? a, string? b)
    {
        var ha = SHA256.HashData(Encoding.UTF8.GetBytes(a ?? ""));
        var hb = SHA256.HashData(Encoding.UTF8.GetBytes(b ?? ""));
        return CryptographicOperations.FixedTimeEquals(ha, hb) && (a ?? "").Length > 0;
    }

    /// <summary>The bot whose token is in the webhook path — compared in constant time.</summary>
    public static ChannelAccount? FindByToken(IEnumerable<ChannelAccount> telegramAccounts, string token)
    {
        ChannelAccount? found = null;
        foreach (var a in telegramAccounts)
            if (SecretEquals(a.AccessToken, token) && found == null) found = a; // no early exit: same work per row
        return found;
    }
}

/// <summary>
/// Outbound Telegram for <see cref="OutboundSender"/>: the bot that carries the
/// thread (or, for a thread with no bot yet, the routed one — see
/// <see cref="Route"/>) sends; no bot = sandbox.
/// </summary>
public sealed class TelegramChannel
{
    public const int MaxText = 4096;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly ITelegramClient _client;
    /// <summary>A Telegram inbox connected through Zernio (managed connection); null = not wired.</summary>
    private readonly ChatBranch.Data.Zernio.ZernioNativeTransport? _zernio;

    public TelegramChannel(AppDbContext db, ScopingService scoping, ThreadStore threads, ITelegramClient client,
        ChatBranch.Data.Zernio.ZernioNativeTransport? zernio = null)
    {
        _zernio = zernio;
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _client = client;
    }

    /// <summary>
    /// The bot that carries this thread, by the shared send-time rules
    /// (<see cref="Routing.ForThread"/>, channel "telegram"). A Telegram chat
    /// only works with the bot the customer started, so a thread keeps its own
    /// configured bot (a dead one fails honestly — never a failover onto a bot
    /// that cannot reach them); a thread with no bot yet takes the contact's
    /// sticky bot → the least-loaded eligible one (the outlet's own before the
    /// organization's) → the sole bot. Null = sandbox. Tracked, so health writes stick.
    /// </summary>
    public ChannelAccount? Route(Thread thread, int? accountId, bool apply = false)
    {
        try
        {
            var r = new Routing(_db, _scoping).ForThread(thread, RouteNeed.Any, accountId ?? thread.AccountId, apply);
            var bot = r.Account ?? r.DownAccount;
            return bot != null && (TelegramBot.Configured(bot) || _zernio?.Live(bot) == true)
                ? _db.ChannelAccounts.Find(bot.Id) : null;
        }
        catch { return null; }
    }

    public bool WouldSendLive(Thread thread, int? accountId) => Route(thread, accountId) != null;

    /// <param name="replyToExternalId">The Bot API message_id this message quotes (reply_parameters,
    /// allow_sending_without_reply — a deleted original never fails the send).</param>
    /// <param name="voiceNote">an Ogg/Opus <paramref name="media"/> goes out as a VOICE NOTE (sendVoice,
    /// no caption — the AI's voice replies); the stored message keeps <paramref name="text"/>.</param>
    public Message Send(Thread thread, string text, string source, int? accountId, OutboundMedia? media = null,
        string? replyToExternalId = null, bool voiceNote = false)
    {
        long.TryParse(replyToExternalId ?? "", out var replyTo);
        object? replyParams = replyTo > 0
            ? new { message_id = replyTo, allow_sending_without_reply = true }
            : null;
        var bot = Route(thread, accountId, apply: true);
        if (bot == null)
        {
            var sandbox = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sandbox", accountId: accountId ?? thread.AccountId);
            return WithMedia(sandbox, media);
        }
        if (thread.AccountId != bot.Id)
        {
            thread.AccountId = bot.Id;
            thread.Account = bot;
            _scoping.SyncThreadEntity(thread, save: false);
            _db.SaveChanges();
        }

        if (ChatBranch.Data.Zernio.ZernioNative.Is(bot) && _zernio != null)
            return WithMedia(ZernioSend(thread, bot, text, source, media, replyToExternalId), media);

        TelegramResult result;
        if (media != null && File.Exists(media.AbsolutePath)
            && new FileInfo(media.AbsolutePath).Length <= MessagingMedia.MaxBytes)
        {
            var (method, field) = voiceNote && media.MediaType == "audio" ? ("sendVoice", "voice") : media.MediaType switch
            {
                "image" => ("sendPhoto", "photo"),
                "video" => ("sendVideo", "video"),
                "audio" => ("sendAudio", "audio"),
                _ => ("sendDocument", "document"),
            };
            var caption = voiceNote || (text.StartsWith('[') && text.EndsWith(']')) ? null : text; // "[image]" is a placeholder
            result = _client.SendFileAsync(bot.AccessToken, method, field, thread.Key,
                File.ReadAllBytes(media.AbsolutePath), media.FileName, caption).GetAwaiter().GetResult();
        }
        else
        {
            result = TelegramResult.Fail(0, "empty message");
            var first = true;
            foreach (var chunk in Chunks(text))
            {
                // only the first chunk carries the quote
                object payload = first && replyParams != null
                    ? new { chat_id = thread.Key, text = chunk, reply_parameters = replyParams }
                    : new { chat_id = thread.Key, text = chunk };
                first = false;
                result = _client.CallAsync(bot.AccessToken, "sendMessage", payload).GetAwaiter().GetResult();
                if (!result.Ok) break;
            }
        }

        Message msg;
        if (result.Ok)
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sent", externalId: OutId(result), accountId: bot.Id);
            HealthRecorder.Ok(_db, bot);
        }
        else
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                $"[send failed: {result.Description}] {text}", status: "failed", accountId: bot.Id);
            HealthRecorder.TelegramSendFailure(_db, bot, result);
        }
        _db.SaveChanges();
        return WithMedia(msg, media);
    }

    /// <summary>
    /// setMessageReaction on a message of this chat (an empty emoji clears ours).
    /// Null bot = the conversation has no live bot (sandbox: the caller stores it
    /// locally). Telegram only accepts its own reaction set — a refused emoji
    /// comes back as its description (REACTION_INVALID).
    /// </summary>
    public (bool Live, bool Ok, string Detail) React(Thread thread, string messageId, string emoji)
    {
        var bot = Route(thread, thread.AccountId);
        if (bot == null) return (false, true, "sandbox");
        if (!long.TryParse(messageId, out var mid) || mid <= 0)
            return (true, false, "That message has no Telegram id, so it can't carry a reaction.");
        if (ChatBranch.Data.Zernio.ZernioNative.Is(bot) && _zernio != null)
        {
            var z = _zernio.ReactAsync(bot, thread.Key, messageId, emoji).GetAwaiter().GetResult();
            return (true, z.Ok, z.Ok ? "sent" : z.Detail);
        }
        var reaction = string.IsNullOrEmpty(emoji)
            ? Array.Empty<object>()
            : new object[] { new { type = "emoji", emoji } };
        var r = _client.CallAsync(bot.AccessToken, "setMessageReaction",
            new { chat_id = thread.Key, message_id = mid, reaction }).GetAwaiter().GetResult();
        return (true, r.Ok, r.Ok ? "sent" : r.Description);
    }

    /// <summary>A Telegram inbox carried by a managed connection (Zernio): its conversation endpoint.</summary>
    private Message ZernioSend(Thread thread, ChannelAccount bot, string text, string source, OutboundMedia? media,
        string? replyTo)
    {
        TransportResult result;
        if (media != null)
        {
            var caption = text.StartsWith('[') && text.EndsWith(']') ? null : text;
            result = _zernio!.SendMediaAsync(bot, new SendTarget(thread.Key, null, null, thread.Id), media, caption)
                .GetAwaiter().GetResult();
        }
        else
        {
            result = TransportResult.Fail(TransportFailure.Unsupported, "empty message");
            var first = true;
            foreach (var chunk in Chunks(text))
            {
                result = _zernio!.SendAsync(bot, thread.Key, chunk, replyTo: first ? replyTo : null)
                    .GetAwaiter().GetResult().ToTransport();
                first = false;
                if (!result.Ok) break;
            }
        }
        Message msg;
        if (result.Ok)
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sent", externalId: result.ExternalId, accountId: bot.Id);
            HealthRecorder.Ok(_db, bot);
        }
        else
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                $"[send failed: {result.Detail}] {text}", status: "failed", accountId: bot.Id);
            msg.Error = result.Detail.Length > 300 ? result.Detail[..300] : result.Detail;
            MoreChannelSender.Record(_db, bot, result);
        }
        _db.SaveChanges();
        return msg;
    }

    /// <summary>The Bot API message_id — one sequence per chat across both directions,
    /// so it never collides with an inbound id on the same thread.</summary>
    private static string OutId(TelegramResult r)
    {
        var id = r.Long("message_id");
        return id > 0 ? id.ToString() : "";
    }

    private static IEnumerable<string> Chunks(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        for (var i = 0; i < text.Length; i += MaxText)
            yield return text.Substring(i, Math.Min(MaxText, text.Length - i));
    }

    private Message WithMedia(Message msg, OutboundMedia? media)
    {
        if (media == null) return msg;
        MediaStore.Stamp(msg, media.MediaType, media.RelPath, MessagingMedia.ContentTypeFor(media.FileName),
            media.FileName, MediaStore.SizeOf(media.AbsolutePath));
        _db.SaveChanges();
        return msg;
    }
}

/// <summary>One parsed Telegram update (only <c>message</c> updates carry a conversation).</summary>
public sealed record TelegramUpdate(string ChatId, string MessageId, string Name, string Text, DateTime? Date,
    TelegramMedia? Media)
{
    /// <summary>The message_id this message replies to ("" = not a reply).</summary>
    public string ReplyTo { get; init; } = "";
    /// <summary>A group / supergroup message (<see cref="GroupChats"/>): <see cref="Name"/> is
    /// then the group's title and the author is <see cref="Sender"/>.</summary>
    public bool IsGroup { get; init; }
    /// <summary>A group message's author (the Telegram user id + name); null in a private chat.</summary>
    public InboundSender? Sender { get; init; }
    /// <summary>A group message addressed to the bot: an @mention of it, a text mention of
    /// it, a bot command (bare or <c>/cmd@thisbot</c>) or a reply to one of its messages.</summary>
    public bool Mentioned { get; init; }
}

/// <summary>A downloadable attachment on an inbound Telegram message.</summary>
public sealed record TelegramMedia(string FileId, string MediaType, string FallbackName);

public static class TelegramInbound
{
    private static readonly string[] MediaKeys =
        { "photo", "document", "sticker", "voice", "audio", "video", "video_note", "animation", "location", "contact" };

    /// <summary>Parse a webhook body. Null = nothing for the inbox (not a message, a bot echo, garbage).</summary>
    /// <param name="botUsername">The receiving bot's @username (without the @; "" = unknown) and
    /// <paramref name="botId"/> its user id — how a group message addressed to it is recognised.</param>
    public static TelegramUpdate? Parse(JsonElement root, string botUsername = "", string botId = "")
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("message", out var m)
            || m.ValueKind != JsonValueKind.Object) return null;
        if (!m.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object
            || !chat.TryGetProperty("id", out var cid)) return null;
        var chatId = cid.ToString();
        if (chatId.Length == 0) return null;
        var from = m.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
        if (from.ValueKind == JsonValueKind.Object && from.TryGetProperty("is_bot", out var isBot)
            && isBot.ValueKind == JsonValueKind.True) return null;

        string S(JsonElement e, string k) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        var name = string.Join(" ", new[] { S(from, "first_name"), S(from, "last_name") }
            .Where(x => x.Length > 0)).Trim();
        if (name.Length == 0) name = S(from, "username");
        // a group / supergroup: the conversation is the group (named after its title), the
        // person who wrote is the message's sender
        var chatType = S(chat, "type");
        var isGroup = chatType is "group" or "supergroup" || (chatType.Length == 0 && chatId.StartsWith('-'));
        InboundSender? sender = null;
        if (isGroup)
        {
            var fromId = from.ValueKind == JsonValueKind.Object && from.TryGetProperty("id", out var fid)
                ? fid.ToString() : "";
            sender = new InboundSender(fromId, name.Length > 0 ? name : fromId);
            name = S(chat, "title");
        }
        if (name.Length == 0) name = S(chat, "title");
        if (name.Length == 0) name = isGroup ? GroupChats.DefaultName(ChannelIds.Telegram) : chatId;

        var text = S(m, "text");
        if (text.Length == 0) text = S(m, "caption");
        // a shared pin / contact card in the inbox's card shapes ("[location] lat,lng label",
        // "[contact] Name · +phone") — the bubble renders them as cards
        if (text.Length == 0 && m.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
            && loc.TryGetProperty("latitude", out var lat) && loc.TryGetProperty("longitude", out var lng))
            text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[location] {lat},{lng}");
        if (text.Length == 0 && m.TryGetProperty("contact", out var card) && card.ValueKind == JsonValueKind.Object)
            text = MessageCards.Contact(
                string.Join(" ", new[] { S(card, "first_name"), S(card, "last_name") }.Where(x => x.Length > 0)),
                S(card, "phone_number"));
        if (text.Length == 0)
            text = MediaKeys.FirstOrDefault(k => m.TryGetProperty(k, out _)) is { } kind ? $"[{kind}]" : "[message]";

        DateTime? date = m.TryGetProperty("date", out var d) && d.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : null;
        var msgId = m.TryGetProperty("message_id", out var mid) ? mid.ToString() : "";
        var replyTo = m.TryGetProperty("reply_to_message", out var rt) && rt.ValueKind == JsonValueKind.Object
                      && rt.TryGetProperty("message_id", out var rid) ? rid.ToString() : "";
        return new TelegramUpdate(chatId, msgId, name, text, date, Media(m))
        {
            ReplyTo = replyTo, IsGroup = isGroup, Sender = sender,
            Mentioned = isGroup && AddressedToBot(m, botUsername, botId),
        };
    }

    /// <summary>
    /// Is this group message talking to the bot? An <c>@botname</c> mention, a text mention of
    /// the bot's user, a bot command (bare, or <c>/cmd@botname</c> naming THIS bot) or a reply
    /// to one of the bot's messages. With the bot's username unknown, any @mention of a bot
    /// account name ending in "bot" and any reply to a bot counts — a bot in privacy mode only
    /// receives those anyway.
    /// </summary>
    public static bool AddressedToBot(JsonElement m, string botUsername, string botId)
    {
        var user = (botUsername ?? "").Trim().TrimStart('@');
        var id = (botId ?? "").Trim();
        var text = m.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? ""
            : m.TryGetProperty("caption", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        foreach (var key in new[] { "entities", "caption_entities" })
        {
            if (!m.TryGetProperty(key, out var ents) || ents.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in ents.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                var piece = Slice(text, e);
                switch (type)
                {
                    case "mention":
                        var named = piece.TrimStart('@');
                        if (user.Length > 0 ? named.Equals(user, StringComparison.OrdinalIgnoreCase)
                                : named.EndsWith("bot", StringComparison.OrdinalIgnoreCase)) return true;
                        break;
                    case "text_mention":
                        if (id.Length > 0 && e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                            && u.TryGetProperty("id", out var uid) && uid.ToString() == id) return true;
                        break;
                    case "bot_command":
                        var at = piece.IndexOf('@');
                        if (at < 0 || user.Length == 0
                            || piece[(at + 1)..].Equals(user, StringComparison.OrdinalIgnoreCase)) return true;
                        break;
                }
            }
        }
        if (m.TryGetProperty("reply_to_message", out var rt) && rt.ValueKind == JsonValueKind.Object
            && rt.TryGetProperty("from", out var rf) && rf.ValueKind == JsonValueKind.Object)
        {
            if (id.Length > 0) return rf.TryGetProperty("id", out var rid) && rid.ToString() == id;
            return rf.TryGetProperty("is_bot", out var rb) && rb.ValueKind == JsonValueKind.True;
        }
        return false;
    }

    /// <summary>The text an entity covers (Telegram counts UTF-16 code units, as .NET strings do).</summary>
    private static string Slice(string text, JsonElement entity)
    {
        if (!entity.TryGetProperty("offset", out var o) || !o.TryGetInt32(out var off)
            || !entity.TryGetProperty("length", out var l) || !l.TryGetInt32(out var len)) return "";
        if (off < 0 || len <= 0 || off + len > text.Length) return "";
        return text.Substring(off, len);
    }

    /// <summary>
    /// A <c>message_reaction</c> update (the customer reacted to a message in a
    /// private chat): the chat, the message, and the reaction now set — the first
    /// emoji of <c>new_reaction</c>, "" when they cleared it. Null = not one.
    /// </summary>
    public static (string ChatId, string MessageId, string Emoji)? ParseReaction(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("message_reaction", out var r)
            || r.ValueKind != JsonValueKind.Object) return null;
        if (!r.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object
            || !chat.TryGetProperty("id", out var cid)) return null;
        if (r.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
            && u.TryGetProperty("is_bot", out var bot) && bot.ValueKind == JsonValueKind.True) return null;
        var mid = r.TryGetProperty("message_id", out var m) ? m.ToString() : "";
        if (mid.Length == 0) return null;
        var emoji = "";
        if (r.TryGetProperty("new_reaction", out var nr) && nr.ValueKind == JsonValueKind.Array)
            foreach (var x in nr.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("emoji", out var e)
                    && e.ValueKind == JsonValueKind.String && (e.GetString() ?? "").Length > 0)
                {
                    emoji = e.GetString()!;
                    break;
                }
        return (cid.ToString(), mid, emoji);
    }

    /// <summary>messaging/views._telegram_media — what to download, or null.</summary>
    public static TelegramMedia? Media(JsonElement m)
    {
        string Id(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("file_id", out var v)
            ? v.GetString() ?? "" : "";
        string Name(JsonElement e, string dflt) =>
            e.TryGetProperty("file_name", out var v) && v.GetString() is { Length: > 0 } n ? n : dflt;

        if (m.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Array && photo.GetArrayLength() > 0)
            return new TelegramMedia(Id(photo[photo.GetArrayLength() - 1]), "image", "photo.jpg"); // last = largest
        if (m.TryGetProperty("document", out var doc) && doc.ValueKind == JsonValueKind.Object)
        {
            var name = Name(doc, "file.bin");
            return new TelegramMedia(Id(doc), MessagingMedia.TypeFor(name), name);
        }
        if (m.TryGetProperty("sticker", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            // a video sticker is a WebM clip (plays in <video>); an animated one is a
            // Lottie .tgs no browser renders — its still thumbnail stands in for it
            if (st.TryGetProperty("is_video", out var iv) && iv.ValueKind == JsonValueKind.True)
                return new TelegramMedia(Id(st), "video", "sticker.webm");
            if (st.TryGetProperty("is_animated", out var an) && an.ValueKind == JsonValueKind.True)
            {
                var thumb = st.TryGetProperty("thumbnail", out var th) ? th
                    : st.TryGetProperty("thumb", out var th2) ? th2 : default;
                var thumbId = Id(thumb);
                return thumbId.Length > 0 ? new TelegramMedia(thumbId, "image", "sticker.webp") : null;
            }
            return new TelegramMedia(Id(st), "image", "sticker.webp");
        }
        if (m.TryGetProperty("voice", out var voice) && voice.ValueKind == JsonValueKind.Object)
            return new TelegramMedia(Id(voice), "audio", "voice.ogg");
        if (m.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object)
            return new TelegramMedia(Id(audio), "audio", Name(audio, "audio.mp3"));
        if (m.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
            return new TelegramMedia(Id(video), "video", Name(video, "video.mp4"));
        if (m.TryGetProperty("video_note", out var note) && note.ValueKind == JsonValueKind.Object)
            return new TelegramMedia(Id(note), "video", "video-note.mp4");
        return null;
    }

    /// <summary>
    /// Download an inbound attachment and hang it on its message (off the
    /// webhook ack — Telegram retries slow webhooks). Best-effort: a failure
    /// keeps the text placeholder. Returns true when the media was attached.
    /// </summary>
    public static async Task<bool> AttachMediaAsync(AppDbContext db, ITelegramClient client, string dataDir,
        int accountId, int messageId, TelegramMedia media, CancellationToken ct = default)
    {
        try
        {
            if (media.FileId.Length == 0) return false;
            var account = db.ChannelAccounts.FirstOrDefault(a => a.Id == accountId);
            var msg = db.Messages.FirstOrDefault(x => x.Id == messageId);
            if (account == null || msg == null) return false;
            var (content, remoteName, _) = await client.DownloadFileAsync(account.AccessToken, media.FileId,
                MessagingMedia.MaxBytes, ct);
            if (content == null || content.Length == 0) return false;
            var thread = db.Threads.First(t => t.Id == msg.ThreadId);
            var name = remoteName.Contains('.') ? remoteName : media.FallbackName;
            var rel = MessagingMedia.Save(dataDir, ChannelIds.Telegram, thread.Key, name, content);
            MediaStore.Stamp(msg, media.MediaType, rel, MessagingMedia.ContentTypeFor(name), name, content.LongLength);
            thread.UpdatedAt = DateTime.UtcNow; // the /chats stream refreshes on this
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
