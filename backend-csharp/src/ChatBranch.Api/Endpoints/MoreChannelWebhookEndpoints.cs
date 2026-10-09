using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Inbound webhooks for LINE, Viber, SMS (Twilio), WeChat, TikTok, Douyin and
/// Zalo — one per ACCOUNT (<c>/api/messaging/webhook/{channel}/{account_id}</c>),
/// so each request is verified with exactly that account's secret:
///
/// <list type="table">
///   <item><term>line</term><description><c>X-Line-Signature</c> — base64 HMAC-SHA256(channel secret, body)</description></item>
///   <item><term>viber</term><description><c>X-Viber-Content-Signature</c> — hex HMAC-SHA256(auth token, body)</description></item>
///   <item><term>sms</term><description><c>X-Twilio-Signature</c> — base64 HMAC-SHA1(auth token, public URL + sorted params)</description></item>
///   <item><term>wechat</term><description>sha1(sort(token, timestamp, nonce)) + a 5-minute freshness window and single-use nonces (plaintext mode signs no body); safe mode adds msg_signature over the ciphertext + AES</description></item>
///   <item><term>tiktok</term><description><c>TikTok-Signature: t=…,s=…</c> — hex HMAC-SHA256(app secret, "t.body"), 5-minute window</description></item>
///   <item><term>douyin</term><description><c>X-Douyin-Signature</c> — hex sha1(client_secret + body)</description></item>
///   <item><term>zalo</term><description><c>X-ZEvent-Signature: mac=</c> sha256(app id + body + timestamp + OA secret key)</description></item>
/// </list>
///
/// An account without its secret REFUSES inbound (503) — nothing is ever accepted
/// unsigned; a bad or missing signature is a 401; an unknown account a 404.
/// Verified traffic runs the ONE inbound path (<see cref="InboundProcessor.ProcessInbound"/>),
/// deduplicated on the provider's message id, heals the account's health, and
/// downloads media off the ack. Garbage and events the inbox doesn't use are
/// acked 200 — never a 5xx into a provider's retry loop.
/// </summary>
public static class MoreChannelWebhookEndpoints
{
    private const int MaxBody = 2 * 1024 * 1024;

    /// <summary>The transports, their registry and the send seam (Program.cs calls this once).</summary>
    public static IServiceCollection AddMoreChannels(this IServiceCollection services)
    {
        // several of these APIs carry a token in the URL (WeChat) or a header — no request logging
        services.AddHttpClient(ProviderHttp.ClientName).RemoveAllLoggers();
        services.AddSingleton<LineTransport>();
        services.AddSingleton<ViberTransport>();
        services.AddSingleton<TwilioSmsTransport>();
        services.AddSingleton<WeChatTransport>();
        services.AddSingleton<TikTokTransport>();
        services.AddSingleton<DouyinTransport>();
        services.AddSingleton<ZaloTransport>();
        services.AddSingleton<IChannelTransport>(sp => sp.GetRequiredService<LineTransport>());
        services.AddSingleton<IChannelTransport>(sp => sp.GetRequiredService<ViberTransport>());
        services.AddSingleton<IChannelTransport>(sp => sp.GetRequiredService<TwilioSmsTransport>());
        services.AddSingleton<IChannelTransport>(sp => sp.GetRequiredService<WeChatTransport>());
        services.AddSingleton<IChannelTransport>(sp => sp.GetRequiredService<TikTokTransport>());
        services.AddSingleton<IChannelTransport>(sp => sp.GetRequiredService<DouyinTransport>());
        services.AddSingleton<IChannelTransport>(sp => sp.GetRequiredService<ZaloTransport>());
        services.AddSingleton<ChannelTransports>();
        services.AddScoped<MoreChannelSender>();
        return services;
    }

    public static void MapMoreChannelWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/messaging/webhook/line/{id:int}", Line).AllowAnonymous();
        app.MapPost("/api/messaging/webhook/viber/{id:int}", Viber).AllowAnonymous();
        app.MapPost("/api/messaging/webhook/sms/{id:int}", Sms).AllowAnonymous().DisableAntiforgery();
        app.MapGet("/api/messaging/webhook/wechat/{id:int}", WeChatVerify).AllowAnonymous();
        app.MapPost("/api/messaging/webhook/wechat/{id:int}", WeChat).AllowAnonymous();
        app.MapPost("/api/messaging/webhook/tiktok/{id:int}", TikTok).AllowAnonymous();
        app.MapPost("/api/messaging/webhook/douyin/{id:int}", Douyin).AllowAnonymous();
        app.MapPost("/api/messaging/webhook/zalo/{id:int}", Zalo).AllowAnonymous();
    }

    // ---- shared ----------------------------------------------------------------------

    private static Task<ChannelAccount?> AccountAsync(AppDbContext db, int id, string channel) =>
        db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == id && a.Channel == channel);

    private static IResult Unknown() => Results.Json(new { detail = "Unknown account." }, statusCode: 404);

    private static IResult NoSecret(string what) =>
        Results.Json(new { detail = $"{what} is not configured for this account — inbound messages are refused until it is set." },
            statusCode: 503);

    private static IResult BadSignature() => Results.Json(new { detail = "Bad signature." }, statusCode: 401);

    private static IResult Ack(object? extra = null) => Results.Ok(extra ?? new { ok = true });

    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxBody) return null;
        }
        return ms.ToArray();
    }

    private static JsonElement? Json(byte[] body)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return null; }
    }

    /// <summary>
    /// Store one customer message through the shared inbound path. A redelivery
    /// (same provider id on the same conversation) is skipped; a waiting invite
    /// code (or <paramref name="refPayload"/>) rides in front as <c>[ref:CODE] </c>.
    /// Inbound proves the account works, so it heals.
    /// </summary>
    private static Message? Ingest(AppDbContext db, InboundProcessor processor, ChannelAccount account, string key,
        string text, string? name, string externalId, DateTime? ts, string? refPayload = null)
    {
        if (key.Length == 0) return null;
        var channel = account.Channel;
        if (externalId.Length > 0 && db.Messages.Any(m => m.ExternalId == externalId
                                                          && m.Thread!.Channel == channel && m.Thread.Key == key))
            return null;
        var code = MoreChannels.CleanRef(refPayload) ?? PendingRef.Take(db, channel, key);
        var body = code != null ? $"[ref:{code}] {text}" : text;
        var msg = processor.ProcessInbound(channel, key, body, name, account.Id, externalId, ts);
        account.LastInboundAt = DateTime.UtcNow;
        HealthRecorder.Ok(db, account);
        db.SaveChanges();
        return msg;
    }

    /// <summary>Download an attachment off the ack and hang it on its message. Best effort.</summary>
    private static void AttachLater(IServiceScopeFactory scopes, int accountId, int messageId, InboundMediaRef media)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<AppDbContext>();
                var account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == accountId);
                var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId);
                var transport = sp.GetRequiredService<ChannelTransports>().For(account?.Channel);
                if (account == null || msg == null || transport == null) return;
                var (content, _) = await transport.DownloadAsync(account, media);
                if (content == null || content.Length == 0) return;
                var thread = await db.Threads.FirstAsync(t => t.Id == msg.ThreadId);
                msg.MediaPath = MessagingMedia.Save(sp.GetRequiredService<VoiceAgentBridge>().DataDir,
                    account.Channel, thread.Key, media.FileName, content);
                msg.MediaType = media.MediaType;
                thread.UpdatedAt = DateTime.UtcNow; // the /chats stream refreshes on this
                await db.SaveChangesAsync(); // also keeps a token the transport refreshed
            }
            catch { /* the text placeholder stays */ }
        });
    }

    /// <summary>Name a brand-new thread from the provider's profile API, off the ack.</summary>
    private static void NameLater(IServiceScopeFactory scopes, int accountId, string channel, string key,
        Func<IServiceProvider, ChannelAccount, Task<string>> lookup)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<AppDbContext>();
                var thread = await db.Threads.FirstOrDefaultAsync(t => t.Channel == channel && t.Key == key);
                var account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == accountId);
                if (thread == null || account == null || (thread.Name.Length > 0 && thread.Name != key)) return;
                var name = (await lookup(sp, account)).Trim();
                if (name.Length == 0) return;
                thread.Name = name.Length > 120 ? name[..120] : name;
                thread.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            catch { /* the id stays as the name */ }
        });
    }

    /// <summary>A platform follow / unfollow / unsubscribe on a conversation we already have.</summary>
    private static void OptState(AppDbContext db, ThreadStore threads, string channel, string key, bool optOut,
        string reason)
    {
        var thread = db.Threads.FirstOrDefault(t => t.Channel == channel && t.Key == key);
        if (thread != null) ChannelOptOut.Set(db, threads, thread, optOut, reason);
    }

    private static void Learn(AppDbContext db, ChannelAccount account, string externalId)
    {
        if (account.ExternalId.Length > 0 || externalId.Length == 0) return;
        account.ExternalId = externalId.Length > 200 ? externalId[..200] : externalId;
        db.SaveChanges();
    }

    // ---- LINE ------------------------------------------------------------------------

    private static async Task<IResult> Line(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor,
        ThreadStore threads, ChannelTransports transports, IServiceScopeFactory scopes)
    {
        var account = await AccountAsync(db, id, ChannelIds.Line);
        if (account == null) return Unknown();
        if (string.IsNullOrEmpty(account.AppSecret)) return NoSecret("The LINE channel secret");
        var body = await ReadBodyAsync(ctx);
        if (body == null || !LineTransport.VerifySignature(account.AppSecret, body,
                ctx.Request.Headers[LineTransport.SignatureHeader].ToString()))
            return BadSignature();
        if (Json(body) is not { } root) return Ack();
        try
        {
            var (destination, events) = LineInbound.Parse(root);
            Learn(db, account, destination);
            var line = transports.Get<LineTransport>();
            foreach (var e in events)
            {
                switch (e.Type)
                {
                    case "follow":
                        OptState(db, threads, ChannelIds.Line, e.UserId, false, "added the account as a friend again");
                        line?.RememberReplyToken(account.Id, e.UserId, e.ReplyToken, DateTime.UtcNow);
                        continue;
                    case "unfollow":
                        OptState(db, threads, ChannelIds.Line, e.UserId, true, "blocked the account");
                        continue;
                }
                // remembered first, so an instant AI answer replies for free
                line?.RememberReplyToken(account.Id, e.UserId, e.ReplyToken, DateTime.UtcNow);
                var msg = Ingest(db, processor, account, e.UserId, e.Text, null, e.MessageId, e.Timestamp);
                if (msg == null) continue;
                if (e.Media is { } media) AttachLater(scopes, account.Id, msg.Id, media);
                if (line != null)
                    NameLater(scopes, account.Id, ChannelIds.Line, e.UserId, (_, a) => line.ProfileNameAsync(a, e.UserId));
            }
        }
        catch { /* stored what we could; LINE must still get its 200 */ }
        return Ack();
    }

    // ---- Viber -----------------------------------------------------------------------

    private static async Task<IResult> Viber(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor,
        ThreadStore threads, IServiceScopeFactory scopes)
    {
        var account = await AccountAsync(db, id, ChannelIds.Viber);
        if (account == null) return Unknown();
        if (string.IsNullOrEmpty(account.AccessToken)) return NoSecret("The Viber auth token");
        var body = await ReadBodyAsync(ctx);
        if (body == null || !ViberTransport.VerifySignature(account.AccessToken, body,
                ctx.Request.Headers[ViberTransport.SignatureHeader].ToString()))
            return BadSignature();
        if (Json(body) is not { } root || ViberInbound.Parse(root) is not { } e) return Ack();
        try
        {
            switch (e.Event)
            {
                case "message":
                    var msg = Ingest(db, processor, account, e.UserKey, e.Text,
                        e.Name.Length > 0 ? e.Name : null, e.MessageToken, e.Timestamp);
                    if (msg != null && e.Media is { } media) AttachLater(scopes, account.Id, msg.Id, media);
                    break;
                case "subscribed":
                    OptState(db, threads, ChannelIds.Viber, e.UserKey, false, "subscribed again");
                    break;
                case "unsubscribed":
                    OptState(db, threads, ChannelIds.Viber, e.UserKey, true, "unsubscribed");
                    break;
                case "conversation_started":
                    // an invite link's context: kept until their first real message carries it
                    if (MoreChannels.CleanRef(e.Context) != null && e.UserKey.Length > 0)
                    {
                        var thread = threads.GetOrCreateThread(ChannelIds.Viber, e.UserKey,
                            e.Name.Length > 0 ? e.Name : null, account.Id);
                        PendingRef.Remember(db, threads, thread, e.Context);
                    }
                    break;
                case "delivered":
                case "seen":
                case "failed":
                    Tick(db, e);
                    break;
            }
        }
        catch { /* Viber must still get its 200 */ }
        return Ack();
    }

    /// <summary>A delivery receipt on our message: forward-only sent → delivered → read; failed keeps Viber's reason.</summary>
    private static void Tick(AppDbContext db, ViberEvent e)
    {
        if (e.MessageToken.Length == 0 || e.UserKey.Length == 0) return;
        var msg = db.Messages.Include(m => m.Thread).FirstOrDefault(m => m.ExternalId == e.MessageToken
            && m.Direction == MessageDirection.Out && m.Thread!.Channel == ChannelIds.Viber && m.Thread.Key == e.UserKey);
        if (msg == null) return;
        var rank = new Dictionary<string, int> { ["sent"] = 1, ["delivered"] = 2, ["read"] = 3 };
        var next = e.Event switch { "seen" => "read", "delivered" => "delivered", _ => "failed" };
        if (next == "failed")
        {
            if (msg.Status is "delivered" or "read") return;
            msg.Status = "failed";
            msg.Error = e.Description.Length > 0 ? (e.Description.Length > 300 ? e.Description[..300] : e.Description) : "Viber: delivery failed";
        }
        else if (rank.GetValueOrDefault(msg.Status) < rank[next]) msg.Status = next;
        else return;
        msg.Thread!.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    // ---- SMS (Twilio) -----------------------------------------------------------------

    private const string EmptyTwiml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response></Response>";

    private static async Task<IResult> Sms(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor,
        ThreadStore threads, ScopingService sc, IServiceScopeFactory scopes)
    {
        var account = await AccountAsync(db, id, ChannelIds.Sms);
        if (account == null) return Unknown();
        if (string.IsNullOrEmpty(account.AccessToken)) return NoSecret("The Twilio auth token");
        if (!ctx.Request.HasFormContentType) return BadSignature();
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var pairs = form.SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v ?? "")))
            .ToList();
        var tail = $"{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}";
        var publicUrl = MessagingAccountTransport.PublicBaseUrl(null, sc, ctx) + tail;
        var ownUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{tail}";
        if (!TwilioSmsTransport.VerifySignature(account.AccessToken, new[] { publicUrl, ownUrl }, pairs,
                ctx.Request.Headers[TwilioSmsTransport.SignatureHeader].ToString()))
            return BadSignature();
        try
        {
            var sms = SmsInbound.Parse(pairs);
            var key = PhoneMatch.Digits(sms.From);
            var msg = Ingest(db, processor, account, key, sms.Text, key.Length > 0 ? $"+{key}" : null,
                sms.MessageSid, null);
            // Twilio's own opt-out handling (Advanced Opt-Out keywords, including custom ones)
            if (sms.OptOutType == "STOP")
                OptState(db, threads, ChannelIds.Sms, key, true, "texted an opt-out keyword");
            else if (sms.OptOutType == "START")
                OptState(db, threads, ChannelIds.Sms, key, false, "texted an opt-in keyword");
            if (msg != null && sms.Media.Count > 0) AttachLater(scopes, account.Id, msg.Id, sms.Media[0]);
        }
        catch { /* Twilio must still get its 200 */ }
        return Results.Content(EmptyTwiml, "text/xml");
    }

    // ---- WeChat ----------------------------------------------------------------------

    public static readonly TimeSpan WeChatFreshness = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, DateTime> WeChatNonces = new();

    private static IResult Text(string s) => Results.Text(s, "text/plain", Encoding.UTF8);

    private static async Task<IResult> WeChatVerify(int id, HttpContext ctx, AppDbContext db)
    {
        var account = await AccountAsync(db, id, ChannelIds.WeChat);
        if (account == null) return Unknown();
        var token = MoreChannels.Cfg(account, "token");
        if (token.Length == 0) return NoSecret("The WeChat server token");
        var q = ctx.Request.Query;
        if (!WeChatCrypto.Verify(q["signature"].ToString(), token, q["timestamp"].ToString(), q["nonce"].ToString()))
            return BadSignature();
        return Text(q["echostr"].ToString());
    }

    /// <summary>
    /// Plaintext mode signs only (token, timestamp, nonce), not the body, so a
    /// captured URL must not be replayable: the timestamp must be fresh and the
    /// nonce unused. A seen nonce is acked without processing anything.
    /// </summary>
    private static bool FreshNonce(int accountId, string timestamp, string nonce, out bool stale)
    {
        stale = !long.TryParse(timestamp, out var unix)
                || (DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime).Duration() > WeChatFreshness;
        if (stale) return false;
        var now = DateTime.UtcNow;
        if (WeChatNonces.Count > 10000)
            foreach (var (k, at) in WeChatNonces)
                if (now - at > WeChatFreshness * 2) WeChatNonces.TryRemove(k, out _);
        return WeChatNonces.TryAdd($"{accountId}:{timestamp}:{nonce}", now);
    }

    private static async Task<IResult> WeChat(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor,
        ThreadStore threads, IServiceScopeFactory scopes)
    {
        var account = await AccountAsync(db, id, ChannelIds.WeChat);
        if (account == null) return Unknown();
        var token = MoreChannels.Cfg(account, "token");
        if (token.Length == 0) return NoSecret("The WeChat server token");
        var q = ctx.Request.Query;
        string timestamp = q["timestamp"].ToString(), nonce = q["nonce"].ToString();
        if (!WeChatCrypto.Verify(q["signature"].ToString(), token, timestamp, nonce)) return BadSignature();
        var fresh = FreshNonce(account.Id, timestamp, nonce, out var stale);
        if (stale) return BadSignature();
        if (!fresh) return Text("success"); // WeChat's own retry of a request we already took

        var raw = await ReadBodyAsync(ctx);
        if (raw == null || WeChatInbound.Load(Encoding.UTF8.GetString(raw)) is not { } xml) return Text("success");
        var encrypted = WeChatInbound.EncryptedPart(xml);
        if (q["encrypt_type"].ToString() == "aes" || encrypted.Length > 0)
        {
            var aesKey = MoreChannels.Cfg(account, "encoding_aes_key");
            if (aesKey.Length == 0) return NoSecret("The WeChat EncodingAESKey (safe mode)");
            if (!WeChatCrypto.Verify(q["msg_signature"].ToString(), token, timestamp, nonce, encrypted))
                return BadSignature();
            var plain = WeChatCrypto.Decrypt(aesKey, encrypted, MoreChannels.Cfg(account, "app_id"));
            if (plain == null || WeChatInbound.Load(plain) is not { } inner) return BadSignature();
            xml = inner;
        }
        try
        {
            var push = WeChatInbound.Parse(xml);
            Learn(db, account, push.ToUser);
            var name = push.OpenId.Length > 6 ? $"WeChat user …{push.OpenId[^6..]}" : "WeChat user";
            switch (push.Kind)
            {
                case "subscribe":
                    // back from an unfollow: allowed again BEFORE anything answers them
                    OptState(db, threads, ChannelIds.WeChat, push.OpenId, false, "followed the account again");
                    Ingest(db, processor, account, push.OpenId, push.Text, name, push.ExternalId, push.Timestamp, push.Ref);
                    break;
                case "scan":
                case "message":
                    var msg = Ingest(db, processor, account, push.OpenId, push.Text, name, push.ExternalId,
                        push.Timestamp, push.Kind == "scan" ? push.Ref : null);
                    if (msg != null && push.Media is { } media) AttachLater(scopes, account.Id, msg.Id, media);
                    break;
                case "unsubscribe":
                    OptState(db, threads, ChannelIds.WeChat, push.OpenId, true, "unfollowed the account");
                    break;
            }
        }
        catch { /* WeChat must still get its "success" */ }
        return Text("success");
    }

    // ---- TikTok ----------------------------------------------------------------------

    private static async Task<IResult> TikTok(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor)
    {
        var account = await AccountAsync(db, id, ChannelIds.TikTok);
        if (account == null) return Unknown();
        if (string.IsNullOrEmpty(account.AppSecret)) return NoSecret("The TikTok app secret");
        var body = await ReadBodyAsync(ctx);
        if (body == null || !TikTokTransport.VerifySignature(account.AppSecret, body,
                ctx.Request.Headers[TikTokTransport.SignatureHeader].ToString(), DateTime.UtcNow))
            return BadSignature();
        if (Json(body) is not { } root || TikTokInbound.Parse(root, account.ExternalId) is not { } m) return Ack();
        try
        {
            Ingest(db, processor, account, m.ConversationId, m.Text, m.Name.Length > 0 ? m.Name : "TikTok user",
                m.MessageId, m.Timestamp);
        }
        catch { /* TikTok must still get its 200 */ }
        return Ack();
    }

    // ---- Douyin ----------------------------------------------------------------------

    private static async Task<IResult> Douyin(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor)
    {
        var account = await AccountAsync(db, id, ChannelIds.Douyin);
        if (account == null) return Unknown();
        if (string.IsNullOrEmpty(account.AppSecret)) return NoSecret("The Douyin client_secret");
        var body = await ReadBodyAsync(ctx);
        if (body == null || !DouyinTransport.VerifySignature(account.AppSecret, body,
                ctx.Request.Headers[DouyinTransport.SignatureHeader].ToString()))
            return BadSignature();
        if (Json(body) is not { } root || DouyinInbound.Parse(root) is not { } e) return Ack();
        if (e.Kind == "verify")
        {
            // echo the challenge back exactly as sent (a number in Douyin's docs)
            var challenge = ProviderHttp.At(root, "content", "challenge");
            return Results.Json(new Dictionary<string, object?>
            {
                ["challenge"] = challenge.ValueKind == JsonValueKind.Number && challenge.TryGetInt64(out var n)
                    ? n : e.Challenge,
            });
        }
        if (e.Kind != "message") return Ack();
        try
        {
            // one app's webhook may serve several enterprise accounts: route by the receiving open_id
            var target = account;
            if (e.ToUser.Length > 0 && account.ExternalId.Length > 0 && e.ToUser != account.ExternalId)
            {
                var appKey = MoreChannels.Cfg(account, "client_key");
                target = db.ChannelAccounts.Where(a => a.Channel == ChannelIds.Douyin && a.ExternalId == e.ToUser)
                    .AsEnumerable().FirstOrDefault(a => MoreChannels.Cfg(a, "client_key") == appKey);
            }
            if (target == null || e.FromUser.Length == 0 || e.FromUser == target.ExternalId) return Ack();
            Ingest(db, processor, target, e.FromUser, e.Text, e.Name.Length > 0 ? e.Name : "Douyin user",
                e.ExternalId, e.Timestamp);
        }
        catch { /* Douyin must still get its 200 */ }
        return Ack();
    }

    // ---- Zalo ------------------------------------------------------------------------

    private static async Task<IResult> Zalo(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor,
        ThreadStore threads, ChannelTransports transports, IServiceScopeFactory scopes)
    {
        var account = await AccountAsync(db, id, ChannelIds.Zalo);
        if (account == null) return Unknown();
        var appId = MoreChannels.Cfg(account, "app_id");
        var oaSecret = MoreChannels.Cfg(account, "oa_secret_key");
        if (appId.Length == 0 || oaSecret.Length == 0) return NoSecret("The Zalo app id + OA secret key");
        var raw = await ReadBodyAsync(ctx);
        if (raw == null) return BadSignature();
        var body = Encoding.UTF8.GetString(raw);
        var root = Json(raw);
        var timestamp = root is { } r ? ProviderHttp.Str(r, "timestamp") : "";
        if (!ZaloTransport.VerifySignature(appId, oaSecret, body, timestamp,
                ctx.Request.Headers[ZaloTransport.SignatureHeader].ToString()))
            return BadSignature();
        if (root is not { } json || ZaloInbound.Parse(json) is not { } e) return Ack();
        try
        {
            switch (e.Kind)
            {
                case "follow":
                    OptState(db, threads, ChannelIds.Zalo, e.UserId, false, "followed the account again");
                    break;
                case "unfollow":
                    OptState(db, threads, ChannelIds.Zalo, e.UserId, true, "unfollowed the account");
                    break;
                case "message":
                    Learn(db, account, e.OaId);
                    var msg = Ingest(db, processor, account, e.UserId, e.Text, null, e.MessageId, e.Timestamp);
                    if (msg == null) break;
                    if (e.Media is { } media) AttachLater(scopes, account.Id, msg.Id, media);
                    if (transports.Get<ZaloTransport>() is { } zalo)
                        NameLater(scopes, account.Id, ChannelIds.Zalo, e.UserId, (_, a) => zalo.UserNameAsync(a, e.UserId));
                    break;
            }
        }
        catch { /* Zalo must still get its 200 */ }
        return Ack();
    }
}
