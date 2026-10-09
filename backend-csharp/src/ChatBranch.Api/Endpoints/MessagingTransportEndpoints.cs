using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Workers;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The live email + Telegram transport surface (port of the transport halves
/// of messaging/views.py):
///  * <c>POST /api/messaging/email/sync</c> — IMAP pull (<see cref="EmailSyncService"/>);
///  * <c>GET /api/messaging/media/{pk}</c> — a non-WhatsApp message's attachment,
///    auth-gated and scoped like the inbox;
///  * <c>POST /api/messaging/threads/{email|telegram|facebook|instagram}/{key}/send-media</c>
///    — an attachment out through <see cref="OutboundSender"/> (Messenger /
///    Instagram upload the file to the Send API, inside Meta's messaging window);
/// plus the account actions the accounts API delegates here
/// (<see cref="MessagingAccountTransport"/>): Test, Register webhook, the
/// default-SMTP test mail, and the reconnect reset after a credential save.
/// </summary>
public static class MessagingTransportEndpoints
{
    private static readonly string[] AttachChannels =
        { ChannelIds.Email, ChannelIds.Telegram, ChannelIds.Facebook, ChannelIds.Instagram };

    /// <summary>
    /// True when a PERSON is sending (a signed-in dashboard user, or the operator
    /// assistant acting on their confirmation) — false for an API key or an MCP
    /// client, which are automation. Decides whether a Messenger / Instagram reply
    /// may use Meta's HUMAN_AGENT tag after the 24h window.
    /// </summary>
    public static bool IsHumanSender(ClaimsPrincipal me) =>
        me.Identity?.IsAuthenticated == true && !ApiKeyAuth.IsApiKey(me)
        && AuditContext.Current?.Source is not (AuditSources.Api or AuditSources.Mcp);

    public static void MapMessagingTransportEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- IMAP pull: /chats nudges it every 60 s; real IMAP at most every 30 s ----
        app.MapPost("/api/messaging/email/sync", async (HttpContext ctx, EmailSyncService sync) =>
        {
            var force = false;
            try
            {
                if ((ctx.Request.ContentLength ?? 0) > 0 || ctx.Request.ContentType?.Contains("json") == true)
                {
                    var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
                    force = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("force", out var f)
                            && f.ValueKind == JsonValueKind.True;
                }
            }
            catch { /* an empty or odd body is a plain nudge */ }

            EmailSyncReport r;
            try { r = await sync.SyncAsync(force, ctx.RequestAborted); }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    ok = false, configured = true, fetched = 0, skipped = 0,
                    errors = new[] { $"sync failed: {ex.GetType().Name}" },
                });
            }
            return Results.Ok(new
            {
                ok = r.Errors.Count == 0,
                configured = r.Configured,
                fetched = r.Fetched,
                skipped = r.Skipped,
                duplicates = r.Duplicates,
                mailboxes = r.Mailboxes,
                throttled = r.Throttled,
                errors = r.Errors,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // attachment download: GET /api/messaging/media/{id} is served by
        // WhatsAppMediaEndpoints (one implementation for every channel).

        // ---- attachment send (email / Telegram / Messenger / Instagram) ----
        app.MapPost("/api/messaging/threads/{channel:regex(^(email|telegram|facebook|instagram)$)}/{key}/send-media",
            async (string channel, string key, HttpContext ctx, AppDbContext db, OutboundSender sender,
                VoiceAgentBridge bridge, ClaimsPrincipal me) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { detail = "file is required (multipart/form-data)." });
            var form = await ctx.Request.ReadFormAsync();
            var upload = form.Files["file"];
            if (upload == null || upload.Length == 0)
                return Results.BadRequest(new { detail = "file is required (multipart/form-data)." });
            if (upload.Length > MessagingMedia.MaxBytes)
                return Results.BadRequest(new { detail = "File too large (max 20 MB)." });
            var caption = form["caption"].ToString().Trim();
            var subject = form["subject"].ToString().Trim();
            var force = form["force"].ToString().ToLowerInvariant() is "1" or "true" or "yes";

            var thread = await db.Threads.Include(t => t.Account).Include(t => t.Contact)
                .FirstOrDefaultAsync(t => t.Channel == channel && t.Key == key.Trim());
            if (thread == null) return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            // STOP is honoured on every channel: a manual exception must say so
            if (thread.OptedOut && !force)
                return Results.Json(new
                {
                    detail = "Contact opted out (STOP). Resend with force=true to override as a manual exception.",
                }, statusCode: 409);
            var human = IsHumanSender(me);
            if (sender.SocialWindowBlock(thread, thread.AccountId, human) is { } windowClosed)
                return Results.Json(new { detail = windowClosed, window_open = false }, statusCode: 409);

            using var ms = new MemoryStream();
            await upload.CopyToAsync(ms);
            var fileName = MessagingMedia.SafeName(upload.FileName);
            var rel = MessagingMedia.Save(bridge.DataDir, channel, thread.Key, fileName, ms.ToArray());
            var type = MessagingMedia.TypeFor(fileName);
            var media = new OutboundMedia(MessagingMedia.Resolve(bridge.DataDir, rel)!, rel, type, fileName);
            var text = caption.Length > 0 ? caption : $"[{type}]";

            // Messenger / Instagram upload the stored file (multipart) through the
            // window-aware path; email and Telegram attach it to their own send
            var msg = SocialWindow.Applies(channel)
                ? sender.SendMedia(thread, rel, type, MessagingMedia.ContentTypeFor(fileName),
                    caption.Length > 0 ? caption : null, fileName, MessageSource.Agent, null, humanAgent: human)
                : sender.Send(thread, text, MessageSource.Agent, null,
                    subject: subject.Length > 0 ? subject : null, media: media);
            AuditTrail.Write(db, "message.send", $"{channel}:{thread.Key}", $"media:{type} {Cap(text, 150)}",
                actor: me.Identity?.Name);
            return Results.Ok(new
            {
                message = new Dictionary<string, object?>
                {
                    ["id"] = string.IsNullOrEmpty(msg.ExternalId) ? $"db-{msg.Id}" : msg.ExternalId,
                    ["dir"] = msg.Direction,
                    ["text"] = msg.Text,
                    ["ts"] = DateTime.SpecifyKind(msg.CreatedAt, DateTimeKind.Utc).ToString("o"),
                    ["status"] = msg.Status,
                    ["source"] = msg.Source,
                    ["private"] = msg.Private,
                    ["account_id"] = msg.AccountId?.ToString(),
                    ["media"] = new { type = msg.MediaType, url = $"/api/messaging/media/{msg.Id}" },
                },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).DisableAntiforgery();
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>
/// The live halves of the messaging accounts API, called from
/// <c>VoiceMessagingEndpoints</c> so that file keeps its shape (each handler
/// delegates in one line and keeps its own answer for channels handled
/// elsewhere). Services come from the request so the call sites stay unchanged.
/// </summary>
public static class MessagingAccountTransport
{
    /// <summary>
    /// POST /api/messaging/accounts/{id}/test for email and Telegram; null =
    /// not this transport's channel. Email: SMTP connect + login against THIS
    /// mailbox, then a test mail when the body names <c>to</c>. Telegram:
    /// getMe (+ whether a webhook is registered). Failures answer 502 with the
    /// provider's reason — never a secret.
    /// </summary>
    public static async Task<IResult?> TestAsync(ChannelAccount account, HttpContext ctx)
    {
        var sp = ctx.RequestServices;
        var body = await ReadBody(ctx);
        if (account.Channel == ChannelIds.Email)
        {
            if (!EmailConfig.AccountConfigured(account))
                return Results.Json(new { detail = "This email account is not fully configured yet (host, username, password)." },
                    statusCode: 400);
            var server = EmailConfig.ForAccount(account, sp.GetRequiredService<ScopingService>());
            var probe = await sp.GetRequiredService<IEmailTransport>().ProbeAsync(server, ctx.RequestAborted);
            if (!probe.Ok) return Results.Json(new { detail = $"Connection failed: {probe.Detail}" }, statusCode: 502);
            var to = (Str(body, "to") ?? "").Trim();
            if (to.Length > 0)
            {
                if (!to.Contains('@'))
                    return Results.Json(new { detail = "Valid 'to' email required." }, statusCode: 400);
                var sent = await sp.GetRequiredService<EmailChannel>().SendTestAsync(server, to, ctx.RequestAborted);
                if (!sent.Ok) return Results.Json(new { detail = $"Send failed: {sent.Detail}" }, statusCode: 502);
                return Results.Ok(new { ok = true, name = account.Name, id = account.ExternalId,
                    detail = $"Test email sent to {to}." });
            }
            return Results.Ok(new { ok = true, name = account.Name, id = account.ExternalId,
                detail = $"SMTP connection and login to {server.Host} work." });
        }
        if (account.Channel == ChannelIds.Telegram)
        {
            if (!TelegramBot.Configured(account))
                return Results.Json(new { detail = "This Telegram bot has no token yet." }, statusCode: 400);
            var client = sp.GetRequiredService<ITelegramClient>();
            var me = await client.GetMeAsync(account, ctx.RequestAborted);
            if (!me.Ok)
                return Results.Json(new
                {
                    detail = $"Connection failed: {(me.TokenRejected ? "bot token invalid or revoked" : me.Description)}",
                }, statusCode: 502);
            var username = me.Str("username");
            var name = username.Length > 0 ? $"@{username}" : me.Str("first_name");
            var botId = me.Str("id");
            if (account.ExternalId.Length == 0 && botId.Length > 0)
            {
                // learn the bot id (Django filled it at link time)
                var db = sp.GetRequiredService<AppDbContext>();
                account.ExternalId = botId;
                await db.SaveChangesAsync();
            }
            var hook = await client.GetWebhookInfoAsync(account, ctx.RequestAborted);
            var hookNote = !hook.Ok ? "" : hook.Str("url").Length > 0
                ? " — webhook registered."
                : " — no webhook yet: register it so messages arrive.";
            return Results.Ok(new { ok = true, name, id = botId, detail = $"Connected as {name}{hookNote}" });
        }
        return null;
    }

    /// <summary>
    /// POST /api/messaging/accounts/{id}/set-webhook (Telegram): register
    /// <c>{base}/api/messaging/webhook/telegram/{token}</c> with a fresh
    /// <c>secret_token</c>, stored on the account so the webhook can verify it.
    /// Base = body <c>base_url</c> → Setting <c>public_base_url</c> → env
    /// <c>PUBLIC_BASE_URL</c> → the request's own (forwarded) origin; Telegram
    /// only calls https. The answer shows the URL with the token redacted.
    /// </summary>
    public static async Task<IResult> SetWebhookAsync(ChannelAccount account,
        Dictionary<string, JsonElement>? body, HttpContext ctx)
    {
        var sp = ctx.RequestServices;
        var db = sp.GetRequiredService<AppDbContext>();
        if (!TelegramBot.Configured(account))
            return Results.Json(new { detail = "This Telegram bot has no token yet." }, statusCode: 400);
        var baseUrl = PublicBaseUrl(Str(body, "base_url"), sp.GetRequiredService<ScopingService>(), ctx);
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new
            {
                detail = "base_url must be a public https:// origin (Telegram only calls https). Pass base_url, " +
                         "or set PUBLIC_BASE_URL / the public_base_url setting.",
            }, statusCode: 400);

        var path = $"/api/messaging/webhook/telegram/{account.AccessToken}";
        var secret = TelegramBot.NewWebhookSecret();
        var result = await sp.GetRequiredService<ITelegramClient>()
            .SetWebhookAsync(account, baseUrl + path, secret, ctx.RequestAborted);
        if (!result.Ok)
            return Results.Json(new { detail = $"setWebhook failed: {result.Description}" }, statusCode: 502);

        account.Config = new Dictionary<string, object?>(account.Config ?? new()) { ["webhook_secret"] = secret };
        await db.SaveChangesAsync();
        AuditTrail.Write(db, "account.set_webhook", $"telegram:{account.Name}", "webhook registered");
        _ = sp.GetRequiredService<HealthRecheck>().Schedule(account.Id);
        // the bot token is the path secret — it never goes back to the browser
        return Results.Ok(new { ok = true, url = $"{baseUrl}/api/messaging/webhook/telegram/<bot-token>" });
    }

    /// <summary>POST /api/messaging/settings/test — a test mail through the default SMTP settings.</summary>
    public static async Task<IResult> DefaultSmtpTestAsync(string to, HttpContext ctx)
    {
        var sp = ctx.RequestServices;
        var server = EmailConfig.GlobalSettings(sp.GetRequiredService<ScopingService>());
        if (!server.SmtpConfigured)
            return Results.Json(new { detail = "SMTP not configured — set the host and from-address first." },
                statusCode: 400);
        var sent = await sp.GetRequiredService<EmailChannel>().SendTestAsync(server, to, ctx.RequestAborted);
        return sent.Ok
            ? Results.Ok(new { ok = true, detail = $"Test email sent to {to}." })
            : Results.Json(new { detail = $"Send failed: {sent.Detail}" }, statusCode: 502);
    }

    /// <summary>
    /// After a credential save: forget the stale verdict silently and re-probe
    /// in the background (health.reset_on_reconfigure). The caller has saved
    /// the new credentials; this saves the reset.
    /// </summary>
    public static async Task CredentialsChangedAsync(ChannelAccount account, HttpContext ctx)
    {
        try
        {
            var sp = ctx.RequestServices;
            AccountHealth.ResetForReconfigure(account);
            // a re-tokened bot has no webhook carrying our old secret
            if (account.Channel == ChannelIds.Telegram && account.Config?.ContainsKey("webhook_secret") == true)
            {
                var cfg = new Dictionary<string, object?>(account.Config);
                cfg.Remove("webhook_secret");
                account.Config = cfg;
            }
            await sp.GetRequiredService<AppDbContext>().SaveChangesAsync();
            _ = sp.GetRequiredService<HealthRecheck>().Schedule(account.Id);
        }
        catch { /* the save itself succeeded */ }
    }

    /// <summary>
    /// A new account's credentials: probe it in the background so its card
    /// shows a real verdict within seconds (and Telegram learns its bot id).
    /// </summary>
    public static void Created(ChannelAccount account, HttpContext ctx)
    {
        try { _ = ctx.RequestServices.GetRequiredService<HealthRecheck>().Schedule(account.Id); }
        catch { /* the account is saved either way */ }
    }

    public static readonly string[] ImapKeys = { "imap_host", "imap_port", "imap_username", "imap_password" };

    /// <summary>Config keys whose change means "new credentials" for a mailbox.</summary>
    public static readonly string[] EmailCredentialKeys =
        { "host", "port", "username", "password", "use_tls", "from_email",
          "imap_host", "imap_port", "imap_username", "imap_password" };

    /// <summary>
    /// The optional per-mailbox IMAP keys on an email account write. Blank
    /// clears back to inherit; a masked password round-trip keeps the stored
    /// one. Returns a 400 for a bad port. <paramref name="touched"/> collects
    /// the keys that changed.
    /// </summary>
    public static IResult? ApplyImap(Dictionary<string, object?> cfg, Dictionary<string, JsonElement>? body,
        List<string>? touched = null)
    {
        if (body == null) return null;
        foreach (var key in ImapKeys)
        {
            if (!body.TryGetValue(key, out var el)) continue;
            var raw = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? "",
                JsonValueKind.Number => el.ToString(),
                JsonValueKind.Null => "",
                _ => null,
            };
            if (raw == null) continue;
            if (key == "imap_password")
            {
                if (raw.Contains("••")) continue; // masked placeholder = unchanged
            }
            else raw = raw.Trim();
            if (key == "imap_port" && raw.Length > 0
                && !(int.TryParse(raw, out var port) && port is > 0 and < 65536))
                return Results.Json(new { detail = "imap_port must be a port number." }, statusCode: 400);
            if (raw.Length == 0) cfg.Remove(key);
            else cfg[key] = key == "imap_port" ? int.Parse(raw) : raw;
            touched?.Add(key);
        }
        return null;
    }

    /// <summary>The public origin Telegram (or anyone) should call back on.</summary>
    public static string PublicBaseUrl(string? explicitBase, ScopingService sc, HttpContext ctx)
    {
        var b = (explicitBase ?? "").Trim();
        if (b.Length == 0) { try { b = sc.SettingValue("public_base_url").Trim(); } catch { b = ""; } }
        if (b.Length == 0) b = (Environment.GetEnvironmentVariable("PUBLIC_BASE_URL") ?? "").Trim();
        if (b.Length == 0)
        {
            var req = ctx.Request;
            var proto = req.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
            var host = req.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim();
            if (proto.Length == 0) proto = req.Scheme;
            if (host.Length == 0) host = req.Host.HasValue ? req.Host.Value : "";
            b = host.Length > 0 ? $"{proto}://{host}" : "";
        }
        return b.TrimEnd('/');
    }

    /// <summary>The JSON body of a handler that did not bind one itself (null when empty/odd).</summary>
    private static async Task<Dictionary<string, JsonElement>?> ReadBody(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.ContentLength is 0) return null;
            return await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(ctx.Request.Body);
        }
        catch { return null; }
    }

    private static string? Str(Dictionary<string, JsonElement>? body, string key) =>
        body != null && body.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;
}
