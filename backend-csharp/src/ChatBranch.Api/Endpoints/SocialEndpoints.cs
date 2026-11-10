using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Social;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>Remembers when the social accounts were last reconciled (the GET's cheap reconcile, per host).</summary>
public sealed class SocialSyncClock
{
    public DateTime LastSync { get; set; } = DateTime.MinValue;
}

/// <summary>
/// ChatBranch × Zernio: social accounts as inboxes, comments / reviews / DMs in the
/// unified inbox, and social posts published or scheduled from ChatBranch.
///
/// <list type="bullet">
///   <item><c>/api/zernio/*</c> — the deployment's ONE Zernio key (saved with the other app keys,
///         <c>POST /api/whatsapp/settings {zernio_api_key}</c>), its status, a test and the webhook
///         registration: <c>app_keys.manage</c> (superadmin by default).</item>
///   <item><c>POST /api/zernio/webhook</c> — anonymous; the HMAC signature IS the auth (no secret ⇒ 503,
///         bad ⇒ 401), deduped on the event id.</item>
///   <item><c>/api/social/*</c> — accounts (<c>social.view</c> / <c>social.manage</c>; PATCH binds one to an
///         entity by moving it to that entity's Zernio profile), posts
///         (<c>social.view</c> / <c>social.publish</c>) and the comment actions (the thread's <c>chats.send</c>).</item>
/// </list>
/// Not configured ⇒ 409 <c>{code: "zernio_not_configured"}</c> on everything that needs Zernio.
/// </summary>
public static class SocialEndpoints
{
    public const string NotConfiguredCode = "zernio_not_configured";
    public const string WebhookSecretSetting = "zernio_webhook_secret";
    public const string WebhookIdSetting = "zernio_webhook_id";
    public const string WebhookPath = "/api/zernio/webhook";

    public static IServiceCollection AddSocialChannel(this IServiceCollection services)
    {
        services.AddHttpClient(HttpZernioClient.ClientName).RemoveAllLoggers();
        services.AddSingleton<IZernioClient, HttpZernioClient>();
        // one IChannelTransport for "social": OutboundSender → MoreChannelSender → Routing carry it
        services.AddSingleton<IChannelTransport>(sp =>
            new SocialTransport(sp.GetRequiredService<IZernioClient>(), sp.GetRequiredService<IServiceScopeFactory>()));
        // Zernio as a connection method on NATIVE channels (ZernioNative): the send seam for
        // Messenger / Instagram / Telegram / SMS, and WhatsApp behind the Cloud router
        services.AddSingleton<ZernioNativeTransport>();
        services.AddSingleton<ZernioWhatsAppClient>();
        services.AddSingleton<SocialSyncClock>();
        services.AddScoped<SocialAccounts>();
        services.AddScoped<ZernioWebhook>();
        return services;
    }

    // =========================================================================

    public static void MapSocialEndpoints(this IEndpointRouteBuilder app)
    {
        MapZernio(app);
        MapAccounts(app);
        MapThreadActions(app);
        MapPosts(app);
    }

    // ---- /api/zernio: the key, the status, the webhook ----------------------------

    private static void MapZernio(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/zernio/status", async (AppDbContext db, ScopingService sc, IZernioClient zernio,
            HttpContext ctx) =>
        {
            var (key, baseUrl) = HttpZernioClient.Resolve(sc);
            var profiles = await (from p in db.SocialProfiles.AsNoTracking()
                                  join e in db.Entities.AsNoTracking() on p.EntityId equals e.Id into ej
                                  from e in ej.DefaultIfEmpty()
                                  orderby p.EntityId
                                  select new { p.EntityId, EntityName = e != null ? e.Name : "", p.ProfileId })
                .ToListAsync();
            var hookId = sc.SettingValue(WebhookIdSetting);
            return Results.Ok(new
            {
                configured = zernio.IsConfigured,
                key_masked = WhatsAppSettingsEndpoints.Mask(key),
                base_url = baseUrl,
                webhook = new
                {
                    registered = hookId.Length > 0 && WebhookSecret(sc).Length > 0,
                    id = hookId.Length > 0 ? hookId : null,
                    url = MessagingAccountTransport.PublicBaseUrl(null, sc, ctx) + WebhookPath,
                    events = ZernioWebhook.Events,
                },
                profiles = profiles.Select(p => new
                {
                    entity_id = p.EntityId,
                    entity_name = p.EntityId == null ? "Organization" : p.EntityName,
                    profile_id = p.ProfileId,
                }),
                accounts = await db.ChannelAccounts.CountAsync(a => a.Channel == ChatBranch.Domain.Channels.Social),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("app_keys.manage");

        // Test = list the key's profiles.
        app.MapPost("/api/zernio/test", async (IZernioClient zernio) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var r = await zernio.ListProfilesAsync();
            if (!r.Ok) return Results.Json(new { ok = false, detail = r.Error, code = "zernio_error" }, statusCode: 502);
            return Results.Ok(new
            {
                ok = true,
                profiles = ZernioJson.Arr(r.Json, "profiles").Select(p => new
                {
                    id = ZernioJson.Str(p, "_id"),
                    name = ZernioJson.Str(p, "name"),
                    is_default = ZernioJson.Bool(p, "isDefault"),
                    external_id = ZernioJson.Str(p, "externalId"),
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("app_keys.manage").OrgWideOnly();

        // Create / update the ONE Zernio webhook pointing at {public base}/api/zernio/webhook,
        // with a secret generated here (Zernio never generates one).
        app.MapPost("/api/zernio/webhook/register", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, IZernioClient zernio, HttpContext ctx) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var baseUrl = MessagingAccountTransport.PublicBaseUrl(Str(body, "base_url"), sc, ctx);
            if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { detail = "No public URL: set public_base_url (or PUBLIC_BASE_URL)." }, statusCode: 400);
            var url = baseUrl + WebhookPath;
            var secret = WebhookSecret(sc);
            if (secret.Length == 0) secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

            var id = sc.SettingValue(WebhookIdSetting);
            if (id.Length == 0)
            {
                // one we registered before and lost track of: same URL
                var list = await zernio.ListWebhooksAsync();
                if (list.Ok)
                    id = ZernioJson.Arr(list.Json, "webhooks")
                        .Where(w => ZernioJson.Str(w, "url") == url).Select(w => ZernioJson.Str(w, "_id"))
                        .FirstOrDefault() ?? "";
            }
            ZernioReply r = id.Length > 0
                ? await zernio.UpdateWebhookAsync(id, url, secret, ZernioWebhook.Events)
                : await zernio.CreateWebhookAsync("ChatBranch", url, secret, ZernioWebhook.Events);
            if (!r.Ok && id.Length > 0 && r.Status == 404)
                r = await zernio.CreateWebhookAsync("ChatBranch", url, secret, ZernioWebhook.Events);
            if (!r.Ok) return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
            var newId = r.Str("webhook", "_id");
            if (newId.Length > 0) id = newId;
            sc.SetSetting(WebhookSecretSetting, secret);
            sc.SetSetting(WebhookIdSetting, id);
            AuditTrail.Write(db, "zernio.webhook.register", url, $"webhook {id}");
            return Results.Ok(new { registered = true, id, url, events = ZernioWebhook.Events });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("app_keys.manage").OrgWideOnly();

        // Zernio → ChatBranch. Signed (X-Zernio-Signature = hex HMAC-SHA256 of the raw body),
        // deduped on X-Zernio-Event-Id. 2xx = handled (or a duplicate); 500 = retry please.
        app.MapPost(WebhookPath, async (HttpContext ctx, AppDbContext db, ScopingService sc, ZernioWebhook handler) =>
        {
            var secret = WebhookSecret(sc);
            if (secret.Length == 0)
                return Results.Json(new
                {
                    ok = false, code = NotConfiguredCode,
                    detail = "The Zernio webhook is closed: register it first (Settings → Integrations → Zernio).",
                }, statusCode: 503);
            byte[] raw;
            using (var ms = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(ms);
                raw = ms.ToArray();
            }
            var sig = ctx.Request.Headers["X-Zernio-Signature"].ToString();
            if (sig.Length == 0) sig = ctx.Request.Headers["X-Late-Signature"].ToString();
            if (!ZernioWebhook.Verify(secret, raw, sig))
                return Results.Json(new { ok = false, detail = "bad or missing X-Zernio-Signature" }, statusCode: 401);

            JsonElement body;
            try { body = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch { return Results.Json(new { ok = false, detail = "the body is not JSON" }, statusCode: 400); }
            var eventId = ctx.Request.Headers["X-Zernio-Event-Id"].ToString();
            if (eventId.Length == 0) eventId = ctx.Request.Headers["X-Late-Event-Id"].ToString();
            if (eventId.Length == 0) eventId = ZernioJson.Str(body, "id");
            var eventName = ctx.Request.Headers["X-Zernio-Event"].ToString();
            if (eventName.Length == 0) eventName = ZernioJson.Str(body, "event");

            ZernioEvent? claim = null;
            if (eventId.Length > 0)
            {
                if (await db.ZernioEvents.AnyAsync(e => e.EventId == eventId))
                    return Results.Ok(new { ok = true, duplicate = true });
                claim = new ZernioEvent { EventId = eventId, Event = eventName, ReceivedAt = DateTime.UtcNow };
                db.ZernioEvents.Add(claim);
                try { await db.SaveChangesAsync(); }
                catch (DbUpdateException)
                {
                    return Results.Ok(new { ok = true, duplicate = true });
                }
            }
            try
            {
                var outcome = await handler.HandleAsync(eventName, body, ctx.RequestAborted);
                return Results.Ok(new
                {
                    ok = true, result = outcome.Result, detail = outcome.Detail,
                    thread_id = outcome.ThreadId, message_id = outcome.MessageId,
                });
            }
            catch (Exception)
            {
                // nothing durable happened: release the claim so Zernio's retry is processed
                try
                {
                    db.ChangeTracker.Clear();
                    if (claim != null) await db.ZernioEvents.Where(e => e.EventId == eventId).ExecuteDeleteAsync();
                }
                catch { }
                return Results.Json(new { ok = false, detail = "could not process the event" }, statusCode: 500);
            }
        }).AllowAnonymous();
    }

    private static string WebhookSecret(ScopingService sc)
    {
        var v = sc.SettingValue(WebhookSecretSetting).Trim();
        return v.Length > 0 ? v : (Environment.GetEnvironmentVariable("ZERNIO_WEBHOOK_SECRET") ?? "").Trim();
    }

    /// <summary>What normal users read when the managed-connection service has no key (the code stays machine-readable).</summary>
    public const string NotConfiguredDetail = "Managed connections aren't enabled yet. Ask your admin to set them up.";

    private static IResult NotConfigured(IZernioClient zernio) =>
        Results.Json(new { code = NotConfiguredCode, detail = NotConfiguredDetail }, statusCode: 409);

    // ---- accounts --------------------------------------------------------------

    private static void MapAccounts(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/social/accounts", async (string? entity, string? refresh, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IZernioClient zernio, SocialAccounts accounts, SocialSyncClock clock) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            // the cheap reconcile: at most once a minute unless asked
            if (refresh is "1" or "true" || DateTime.UtcNow - clock.LastSync > TimeSpan.FromSeconds(60))
            {
                clock.LastSync = DateTime.UtcNow;
                try { await accounts.SyncAsync(refreshPages: refresh is "1" or "true"); }
                catch { /* the mirror still answers */ }
            }
            var (filter, all) = await ListScope(db, sc, me, entity);
            var q = db.ChannelAccounts.AsNoTracking().Where(a => a.Channel == ChatBranch.Domain.Channels.Social);
            var rows = (await q.OrderBy(a => a.Name).ToListAsync())
                .Where(a => SocialChannels.Cfg(a, SocialChannels.StatusKey) != "removed")
                .Where(a => all || filter!.Contains(a.EntityId)).ToList();
            // a facebook account's Pages: only those this view's outlet may publish to
            var ids = rows.Select(a => a.Id).ToList();
            var pages = (await db.SocialPages.AsNoTracking().Where(p => ids.Contains(p.AccountId)).ToListAsync())
                .Where(p => all || filter!.Contains(p.EntityId ?? rows.First(a => a.Id == p.AccountId).EntityId))
                .ToList();
            var names = await EntityNames(db, rows.Select(a => a.EntityId).Concat(pages.Select(p => p.EntityId)));
            return Results.Ok(new
            {
                accounts = rows.Select(a => AccountRow(a, names, pages.Where(p => p.AccountId == a.Id))),
                configured = true,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("social.view", "channels.view");

        // Connect an account for an entity. {platform, entity?, purpose?: "inbox" | "social"} →
        // flow "hosted" ({url}: the platform's own login, Zernio hosts any selection step),
        // "code" (Telegram: an access code + steps, then poll …/telegram/status) or
        // "select" (SMS: pick one of the profile's numbers, then POST …/sms). See ConnectFlow.
        app.MapPost("/api/social/connect", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, SocialAccounts accounts, HttpContext ctx) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var platform = PlatformAlias(Str(body, "platform"));
            if (!SocialChannels.ConnectPlatforms.Contains(platform) && platform != "sms")
                return Results.Json(new
                {
                    detail = $"platform must be one of {string.Join(", ", SocialChannels.ConnectPlatforms.Append("sms").OrderBy(p => p))}.",
                }, statusCode: 400);
            var purpose = (Str(body, "purpose") ?? "").Trim().ToLowerInvariant();
            if (purpose.Length > 0 && purpose is not ("inbox" or "social"))
                return Results.Json(new { detail = "purpose must be \"inbox\" or \"social\"." }, statusCode: 400);
            if (purpose == "inbox" && ZernioNative.ChannelFor(platform) == null)
                return Results.Json(new
                {
                    detail = $"{SocialChannels.PlatformLabel(platform)} has no inbox channel here — connect it with purpose \"social\".",
                }, statusCode: 400);
            if (purpose == "social" && !ZernioNative.HasSocialRow(platform))
                return Results.Json(new { detail = $"{SocialChannels.PlatformLabel(platform)} is an inbox only." }, statusCode: 400);
            var (entityId, err) = await ActScope(db, sc, me, Str(body, "entity") ?? ctx.Request.Query["entity"]);
            if (err != null) return err;
            var (profileId, perr) = await accounts.EnsureProfileAsync(entityId);
            if (profileId.Length == 0)
                return Results.Json(new { detail = perr, code = "zernio_error" }, statusCode: 502);
            if (purpose.Length == 0 && ZernioNative.ChannelFor(platform) != null && !ZernioNative.HasSocialRow(platform))
                purpose = "inbox";
            accounts.SetIntent(profileId, platform, purpose);
            var entitySlug = entityId is int eid ? await db.Entities.AsNoTracking().Where(e => e.Id == eid)
                .Select(e => e.Slug).FirstOrDefaultAsync() : null;
            var common = new Dictionary<string, object?>
            {
                ["platform"] = platform,
                ["purpose"] = purpose.Length > 0 ? purpose : null,
                ["profile_id"] = profileId,
                ["entity"] = entitySlug,
                ["channel"] = ZernioNative.ChannelFor(platform),
            };

            if (platform == "telegram")
            {
                var r = await zernio.TelegramCodeAsync(profileId);
                if (!r.Ok) return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
                var code = r.Str("code");
                var bot = r.Str("botUsername");
                AuditTrail.Write(db, "social.connect", platform, "managed connection: access code issued", entityId: entityId);
                var botUrl = bot.Length > 0 ? $"https://t.me/{bot.TrimStart('@')}" : null;
                var steps = new List<object>
                {
                    new { title = "Add the bot as an admin", body = $"Add @{bot.TrimStart('@')} as an administrator of your group or channel.", url = botUrl, code = (string?)null },
                    new { title = "Send the code", body = $"Open a private chat with @{bot.TrimStart('@')} and send the code (with @yourchannel for a public channel).", url = botUrl, code = (string?)code },
                    new { title = "Wait for confirmation", body = "This page updates by itself once the bot has the code (valid 15 minutes).", url = (string?)null, code = (string?)null },
                };
                return Results.Ok(new Dictionary<string, object?>(common)
                {
                    ["flow"] = "code",
                    ["status"] = "pending",
                    ["status_url"] = $"/api/social/connect/telegram/status?code={Uri.EscapeDataString(code)}"
                                     + (entitySlug != null ? "&entity=" + Uri.EscapeDataString(entitySlug) : ""),
                    ["poll_interval_seconds"] = 3,
                    ["code"] = code,
                    ["bot_username"] = bot,
                    ["bot_url"] = botUrl,
                    ["expires_at"] = r.Str("expiresAt"),
                    ["expires_in"] = ZernioJson.Int(r.Json, "expiresIn"),
                    ["steps"] = steps,
                    // the bot already an admin: POST {credentials:{chat_id}} to /api/social/connect/telegram instead
                    ["alternative_submit_url"] = "/api/social/connect/telegram",
                });
            }
            if (platform == "sms")
            {
                var r = await zernio.ListPhoneNumbersAsync(profileId);
                if (!r.Ok) return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
                var numbers = ZernioJson.Arr(r.Json, "numbers")
                    // active numbers Zernio hosts (a number brought via WhatsApp signup cannot do SMS)
                    .Where(n => ZernioJson.Str(n, "status") is "active" or ""
                                && ZernioJson.At(n, "hostedByZernio").ValueKind != JsonValueKind.False)
                    .Select(n => new
                    {
                        id = ZernioJson.Str(n, "_id"),
                        phone_number = ZernioJson.Str(n, "phoneNumber"),
                        country = ZernioJson.Str(n, "country"),
                        sms_capable = ZernioJson.At(n, "smsCapable").ValueKind == JsonValueKind.False ? false : (bool?)ZernioJson.Bool(n, "smsCapable"),
                        sms_send_approved = ZernioJson.Bool(n, "smsSendApproved"),
                        registration_pending = ZernioJson.Bool(n, "smsRegistrationPending"),
                    }).ToList();
                return Results.Ok(new Dictionary<string, object?>(common)
                {
                    ["flow"] = "fields",
                    ["numbers"] = numbers,
                    ["steps"] = new[]
                    {
                        "Pick one of this outlet's numbers to receive and send SMS.",
                        "US numbers can receive at once but need an approved carrier registration before they send.",
                    },
                    ["submit_url"] = "/api/social/connect/sms",
                    ["fields"] = new object[]
                    {
                        new
                        {
                            key = "phone_number_id", label = "Number", type = "select", secret = false, required = true,
                            help = numbers.Count == 0 ? "This outlet has no phone number yet: buy or port one in your managed connection account first." : "",
                            placeholder = "Choose a number",
                            options = numbers.Select(n => new { value = n.id, label = n.phone_number }).ToList(),
                        },
                    },
                });
            }

            var redirect = Dashboard(ctx, sc) + "/channels?zernio=connected" + (purpose.Length > 0 ? "&purpose=" + purpose : "");
            var hosted = await zernio.ConnectUrlAsync(platform, profileId, redirect);
            if (!hosted.Ok) return Results.Json(new { detail = hosted.Error, code = "zernio_error" }, statusCode: 502);
            var url = hosted.Str("authUrl");
            if (url.Length == 0) return Results.Json(new { detail = "No connect URL came back." }, statusCode: 502);
            AuditTrail.Write(db, "social.connect", platform, "managed connection started", entityId: entityId);
            var outv = new Dictionary<string, object?>(common) { ["flow"] = "hosted", ["url"] = url };
            if (platform == "whatsapp")
                outv["alternatives"] = new object[]
                {
                    new
                    {
                        flow = "fields",
                        label = "Use my own WhatsApp Business credentials",
                        submit_url = "/api/social/connect/whatsapp",
                        fields = new object[]
                        {
                            new { key = "access_token", label = "Permanent access token (System User)", type = "password", secret = true, required = true, help = "Meta Business Suite → System users", placeholder = "EAAB…" },
                            new { key = "waba_id", label = "WhatsApp Business Account ID", type = "text", secret = false, required = true, help = "", placeholder = "" },
                            new { key = "phone_number_id", label = "Phone number ID", type = "text", secret = false, required = true, help = "", placeholder = "" },
                            new { key = "pin", label = "Two-step verification PIN (6 digits)", type = "text", secret = true, required = false, help = "Only when two-step verification is on for the number", placeholder = "123456" },
                        },
                    },
                };
            return Results.Ok(outv);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("social.manage", "channels.manage");

        // Telegram's access code: pending | connected (the inbox is created now) | expired.
        app.MapGet("/api/social/connect/telegram/status", async (string? code, string? entity, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, SocialAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            if (string.IsNullOrWhiteSpace(code)) return Results.Json(new { detail = "code is required." }, statusCode: 400);
            var (entityId, err) = await ActScope(db, sc, me, entity);
            if (err != null) return err;
            var r = await zernio.TelegramStatusAsync(code.Trim());
            if (!r.Ok)
                return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: r.Status == 404 ? 404 : 502);
            var status = r.Str("status");
            if (status != "connected")
                return Results.Ok(new { status, connected = false, expired = status == "expired",
                    expires_in = ZernioJson.Int(r.Json, "expiresIn"), message = r.Str("message") });
            var row = await MirrorConnectedAsync(db, accounts, entityId, r.At("account"), "telegram");
            return Results.Ok(new { status, connected = true, chat_title = r.Str("chatTitle"), chat_type = r.Str("chatType"),
                account = row == null ? null : ConnectedRow(db, row) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("social.manage", "channels.manage");

        // Telegram, the bot already an admin: connect by chat id.
        app.MapPost("/api/social/connect/telegram", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, SocialAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var chat = Cred(body, "chat_id").Trim();
            if (chat.Length == 0) return Results.Json(new { detail = "chat_id is required." }, statusCode: 400);
            var (entityId, err) = await ActScope(db, sc, me, Str(body, "entity"));
            if (err != null) return err;
            var (profileId, perr) = await accounts.EnsureProfileAsync(entityId);
            if (profileId.Length == 0) return Results.Json(new { detail = perr, code = "zernio_error" }, statusCode: 502);
            var purpose = (Str(body, "purpose") ?? "inbox").Trim().ToLowerInvariant();
            accounts.SetIntent(profileId, "telegram", purpose);
            var r = await zernio.TelegramConnectAsync(profileId, chat);
            if (!r.Ok)
                return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: r.Status is 400 or 404 ? 400 : 502);
            var row = await MirrorConnectedAsync(db, accounts, entityId, r.At("account"), "telegram", profileId);
            AuditTrail.Write(db, "social.connect", "telegram", $"managed connection: chat {chat}", entityId: entityId);
            return Results.Ok(new { ok = true, connected = true, status = "connected", account = row == null ? null : ConnectedRow(db, row) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("social.manage", "channels.manage");

        // WhatsApp with the business's own Meta credentials (the headless alternative to the hosted signup).
        app.MapPost("/api/social/connect/whatsapp", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, SocialAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var token = Cred(body, "access_token").Trim();
            var waba = Cred(body, "waba_id").Trim();
            var pnid = Cred(body, "phone_number_id").Trim();
            var pin = Cred(body, "pin").Trim();
            var missing = new[] { ("access_token", token), ("waba_id", waba), ("phone_number_id", pnid) }
                .Where(x => x.Item2.Length == 0).Select(x => x.Item1).ToList();
            if (missing.Count > 0)
                return Results.Json(new { detail = $"{string.Join(", ", missing)} required." }, statusCode: 400);
            if (pin.Length > 0 && (pin.Length != 6 || !pin.All(char.IsAsciiDigit)))
                return Results.Json(new { detail = "pin must be 6 digits." }, statusCode: 400);
            var (entityId, err) = await ActScope(db, sc, me, Str(body, "entity"));
            if (err != null) return err;
            var (profileId, perr) = await accounts.EnsureProfileAsync(entityId);
            if (profileId.Length == 0) return Results.Json(new { detail = perr, code = "zernio_error" }, statusCode: 502);
            accounts.SetIntent(profileId, "whatsapp", "inbox");
            var r = await zernio.WhatsAppCredentialsAsync(profileId, token, waba, pnid, pin.Length > 0 ? pin : null);
            if (!r.Ok)
                return Results.Json(new
                {
                    detail = r.Error.Replace(token, "••••", StringComparison.Ordinal),
                    code = "zernio_error",
                    available_phone_numbers = r.At("availablePhoneNumbers").ValueKind == JsonValueKind.Array ? r.At("availablePhoneNumbers") : (JsonElement?)null,
                }, statusCode: r.Status is 400 or 401 or 409 ? r.Status : 502);
            var row = await MirrorConnectedAsync(db, accounts, entityId, r.At("account"), "whatsapp", profileId);
            AuditTrail.Write(db, "social.connect", "whatsapp", "managed connection: own credentials", entityId: entityId);
            return Results.Ok(new
            {
                ok = true,
                connected = true,
                status = "connected",
                warning = r.Str("registrationWarning") is { Length: > 0 } w ? w : null,
                account = row == null ? null : ConnectedRow(db, row),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("social.manage", "channels.manage");

        // SMS: turn SMS on for one of the profile's numbers → its SMS account becomes the inbox.
        app.MapPost("/api/social/connect/sms", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, SocialAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var numberId = Cred(body, "phone_number_id").Trim();
            if (numberId.Length == 0) return Results.Json(new { detail = "phone_number_id is required." }, statusCode: 400);
            var (entityId, err) = await ActScope(db, sc, me, Str(body, "entity"));
            if (err != null) return err;
            var (profileId, perr) = await accounts.EnsureProfileAsync(entityId);
            if (profileId.Length == 0) return Results.Json(new { detail = perr, code = "zernio_error" }, statusCode: 502);
            var r = await zernio.EnableSmsAsync(numberId);
            if (!r.Ok)
                return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: r.Status is 404 or 422 ? 400 : 502);
            var registration = new
            {
                needs_registration = ZernioJson.Bool(r.Json, "needsRegistration"),
                already_registered = ZernioJson.Bool(r.Json, "alreadyRegistered"),
                registration_status = r.Str("registrationStatus") is { Length: > 0 } rs ? rs : null,
                reusable = r.At("reusable").ValueKind == JsonValueKind.Object,
            };
            if (!ZernioJson.Bool(r.Json, "enabled") || r.Str("id").Length == 0)
                return Results.Ok(new
                {
                    ok = false,
                    connected = false,
                    status = ZernioJson.Bool(r.Json, "notReady") ? "pending" : "unavailable",
                    message = r.Str("message"), registration, account = (object?)null,
                });
            var acct = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["_id"] = r.Str("id"), ["platform"] = "sms", ["username"] = r.Str("phoneNumber"),
                ["phoneNumber"] = r.Str("phoneNumber"), ["displayName"] = r.Str("phoneNumber"),
                // a US number without an approved registration receives but cannot send yet: still an inbox
                ["isActive"] = true,
            });
            var row = await MirrorConnectedAsync(db, accounts, entityId, acct, "sms", profileId);
            AuditTrail.Write(db, "social.connect", "sms", $"managed connection: {r.Str("phoneNumber")}", entityId: entityId);
            return Results.Ok(new
            {
                ok = true,
                connected = true,
                status = "connected",
                can_send = ZernioJson.Bool(r.Json, "isActive"),
                message = r.Str("message") is { Length: > 0 } m ? m : null,
                registration,
                account = row == null ? null : ConnectedRow(db, row),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("social.manage", "channels.manage");

        app.MapDelete("/api/social/accounts/{id}", async (string id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IZernioClient zernio) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var row = await FindAccount(db, id);
            if (row == null) return Results.Json(new { detail = "Social account not found." }, statusCode: 404);
            if (ShellScope.ObjectScopeError(db, sc, me, row.EntityId) is { } outside) return outside;
            var r = await zernio.DeleteAccountAsync(row.ExternalId);
            if (!r.Ok && r.Status != 404)
                return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
            var label = $"{row.Channel}:{row.Name}";
            // its conversations stay (back in sandbox), like any deleted inbox — its social row AND native inbox
            foreach (var each in await db.ChannelAccounts.Where(a => a.Provider == Providers.Zernio
                                                                    && a.ExternalId == row.ExternalId).ToListAsync())
            {
                await InboxSettings.DetachAccountAsync(db, each.Id);
                db.SocialPages.RemoveRange(db.SocialPages.Where(p => p.AccountId == each.Id));
                db.ZernioConversations.RemoveRange(db.ZernioConversations.Where(c => c.AccountId == each.Id));
                db.ChannelAccounts.Remove(each);
            }
            await db.SaveChangesAsync();
            AuditTrail.Write(db, "account.delete", label, "managed connection disconnected", entityId: row.EntityId);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.manage");

        // Bind an account to an entity ({entity: slug | id}, or null / "" for the organization):
        // Zernio moves it to that entity's profile, and its conversations and pending posts follow.
        app.MapPatch("/api/social/accounts/{id}", async (string id, Dictionary<string, JsonElement>? body,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, SocialAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            if (body == null || !body.ContainsKey("entity"))
                return Results.Json(new
                {
                    detail = "entity is required: an entity slug or id, or null / \"\" for the organization level.",
                }, statusCode: 400);
            var row = await FindAccount(db, id);
            if (row == null) return Results.Json(new { detail = "Social account not found." }, statusCode: 404);
            // a bound caller never sees another company's account: not found, not "forbidden"
            if (row.EntityId is int cur && !Visible(db, sc, me, cur))
                return Results.Json(new { detail = "Social account not found." }, statusCode: 404);
            if (ShellScope.CompanyLevelError(db, sc, me, row.EntityId) is { } outside) return outside;
            var (target, err) = await ActScope(db, sc, me, Str(body, "entity"));
            if (err != null) return err;

            var from = row.EntityId;
            var (threads, posts, error, status) = await accounts.MoveAsync(row, target);
            if (status != 0)
                return Results.Json(new { detail = status == 409 ? NotConfiguredDetail : error, code = status == 409 ? NotConfiguredCode : "zernio_error" },
                    statusCode: status);
            var names = await EntityNames(db, new[] { from, target });
            string Level(int? e) => e is int x && names.TryGetValue(x, out var n) ? n.Slug : "organization";
            AuditTrail.Write(db, "account.entity", $"social:{row.Name}",
                $"moved {Level(from)} → {Level(target)} (connection profile {SocialChannels.Cfg(row, SocialChannels.ProfileIdKey)}; " +
                $"{threads} conversation(s), {posts} post(s) re-scoped)", entityId: target);
            return Results.Ok(await FullRow(db, row));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.manage");

        // Bind one Facebook Page of a facebook account to an outlet ({entity}: slug | id; null / "" =
        // it follows the account's level). The outlet must sit at or under the account's level.
        app.MapPatch("/api/social/accounts/{id}/pages/{pageId}", async (string id, string pageId,
            Dictionary<string, JsonElement>? body, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            IZernioClient zernio, SocialAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            if (body == null || !body.ContainsKey("entity"))
                return Results.Json(new
                {
                    detail = "entity is required: an entity slug or id, or null / \"\" to follow the account's level.",
                }, statusCode: 400);
            var (row, page, missing) = await FindPage(db, sc, me, accounts, id, pageId);
            if (missing != null) return missing;
            var current = page!.EntityId ?? row!.EntityId;
            if (ShellScope.CompanyLevelError(db, sc, me, current) is { } outside) return outside;
            var (target, err) = await ActScope(db, sc, me, Str(body, "entity"));
            if (err != null) return err;
            if (target is int t && row!.EntityId is int acctLevel && !sc.EntityChain(t).Contains(acctLevel))
                return Results.Json(new
                {
                    detail = "That outlet is not under the account's own outlet: assign the account to the " +
                             "organization (or a common parent) first.",
                }, statusCode: 400);
            if (target == row!.EntityId) target = null; // the account's own level = follow it
            var from = page.EntityId ?? row.EntityId;
            var posts = await accounts.BindPageAsync(row, page, target);
            var names = await EntityNames(db, new[] { from, target ?? row.EntityId });
            string Level(int? e) => e is int x && names.TryGetValue(x, out var n) ? n.Slug : "organization";
            AuditTrail.Write(db, "account.entity", $"social:{row.Name} / {page.Name}",
                $"Facebook Page {page.PageId}: {Level(from)} → {Level(target ?? row.EntityId)} ({posts} post(s) re-scoped)",
                entityId: target ?? row.EntityId);
            return Results.Ok(await FullRow(db, row));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.manage");

        // Which Page receives the inbox's comments and messages (Zernio's selectedPageId).
        app.MapPost("/api/social/accounts/{id}/pages/{pageId}/select", async (string id, string pageId,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, SocialAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var (row, page, missing) = await FindPage(db, sc, me, accounts, id, pageId);
            if (missing != null) return missing;
            if (ShellScope.CompanyLevelError(db, sc, me, row!.EntityId) is { } outside) return outside;
            var (error, status) = await accounts.SelectPageAsync(row, page!);
            if (status != 0)
                return Results.Json(new { detail = error, code = "zernio_error" }, statusCode: status);
            AuditTrail.Write(db, "account.update", $"social:{row.Name}", $"inbox Page → {page!.Name} ({page.PageId})",
                entityId: row.EntityId);
            return Results.Ok(await FullRow(db, row));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.manage");
    }

    /// <summary>A facebook account + one of its Pages, or the 404 (also for an account a bound caller cannot see).</summary>
    private static async Task<(ChannelAccount? Account, SocialPage? Page, IResult? Error)> FindPage(AppDbContext db,
        ScopingService sc, ClaimsPrincipal me, SocialAccounts accounts, string id, string pageId)
    {
        var row = await FindAccount(db, id);
        if (row == null || (row.EntityId is int cur && !Visible(db, sc, me, cur)))
            return (null, null, Results.Json(new { detail = "Social account not found." }, statusCode: 404));
        var page = await db.SocialPages.FirstOrDefaultAsync(p => p.AccountId == row.Id && p.PageId == pageId);
        if (page == null)
        {
            // not mirrored yet: read Zernio's list once
            if (SocialChannels.Platform(row) == "facebook") await accounts.SyncPagesAsync(row);
            page = await db.SocialPages.FirstOrDefaultAsync(p => p.AccountId == row.Id && p.PageId == pageId);
        }
        if (page == null)
            return (row, null, Results.Json(new { detail = "That Facebook Page is not one of this account's Pages." },
                statusCode: 404));
        return (row, page, null);
    }

    /// <summary>One account's row with every one of its Pages (the PATCH / select answers).</summary>
    private static async Task<object> FullRow(AppDbContext db, ChannelAccount row)
    {
        var pages = await db.SocialPages.AsNoTracking().Where(p => p.AccountId == row.Id).ToListAsync();
        var names = await EntityNames(db, pages.Select(p => p.EntityId).Append(row.EntityId));
        return AccountRow(row, names, pages);
    }

    /// <summary>
    /// A Zernio account by its Zernio id (its social row, else its native inbox — WhatsApp / SMS
    /// have only the latter), else by either row's ChannelAccount id (a native id resolves to
    /// its social twin when there is one).
    /// </summary>
    private static async Task<ChannelAccount?> FindAccount(AppDbContext db, string id)
    {
        var rows = await db.ChannelAccounts.Where(a => a.Provider == Providers.Zernio && a.ExternalId == id).ToListAsync();
        if (rows.Count == 0 && int.TryParse(id, out var pk)
            && await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == pk && a.Provider == Providers.Zernio) is { } byPk)
            rows = await db.ChannelAccounts.Where(a => a.Provider == Providers.Zernio && a.ExternalId == byPk.ExternalId)
                .ToListAsync();
        return rows.FirstOrDefault(a => a.Channel == ChatBranch.Domain.Channels.Social) ?? rows.FirstOrDefault();
    }

    /// <summary>Mirror an account a connect endpoint just made (Telegram / WhatsApp credentials / SMS) at once.</summary>
    private static async Task<ChannelAccount?> MirrorConnectedAsync(AppDbContext db, SocialAccounts accounts, int? entityId,
        JsonElement acct, string platform, string? profileId = null)
    {
        if (acct.ValueKind != JsonValueKind.Object) return null;
        var d = JsonSerializer.Deserialize<Dictionary<string, object?>>(acct.GetRawText()) ?? new();
        d.TryAdd("platform", platform);
        if (!d.ContainsKey("_id") && d.TryGetValue("accountId", out var aid)) d["_id"] = aid;
        var el = JsonSerializer.SerializeToElement(d);
        profileId ??= (await accounts.EnsureProfileAsync(entityId, create: false)).ProfileId;
        accounts.Upsert(el, entityId, profileId ?? "", intent: "inbox");
        return accounts.FindNative(ZernioJson.Str(el, "_id")) ?? accounts.FindAny(ZernioJson.Str(el, "_id"));
    }

    /// <summary>A just-connected inbox as the Connected list shows it (the /api/messaging/accounts row shape).</summary>
    private static Dictionary<string, object?> ConnectedRow(AppDbContext db, ChannelAccount row)
    {
        var entity = row.EntityId is int e ? db.Entities.AsNoTracking().FirstOrDefault(x => x.Id == e) : null;
        var outv = new Dictionary<string, object?>
        {
            ["id"] = row.Id.ToString(),
            ["channel"] = row.Channel,
            ["name"] = row.Name,
            ["entity"] = entity?.Slug,
            ["entity_name"] = entity?.Name ?? "",
            ["configured"] = ZernioNative.Is(row) ? ZernioNative.Configured(row) : SocialChannels.Configured(row),
        };
        foreach (var (k, v) in ZernioNative.Payload(row)) outv[k] = v;
        return outv;
    }

    private static async Task<Dictionary<int, (string Name, string Slug)>> EntityNames(AppDbContext db,
        IEnumerable<int?> ids)
    {
        var want = ids.OfType<int>().Distinct().ToList();
        if (want.Count == 0) return new();
        return (await db.Entities.AsNoTracking().Where(e => want.Contains(e.Id))
                .Select(e => new { e.Id, e.Name, e.Slug }).ToListAsync())
            .ToDictionary(e => e.Id, e => (e.Name, e.Slug));
    }

    /// <summary>The accounts list row.</summary>
    private static object AccountRow(ChannelAccount a, IReadOnlyDictionary<int, (string Name, string Slug)> names,
        IEnumerable<SocialPage>? pages = null)
    {
        var level = a.EntityId is int eid && names.TryGetValue(eid, out var n) ? n : ((string Name, string Slug)?)null;
        var platform = SocialChannels.Platform(a);
        var (publish, comments, dm, reviews) = SocialChannels.Capabilities(platform);
        return new Dictionary<string, object?>
        {
            ["id"] = a.ExternalId,
            ["account_id"] = a.Id,
            ["platform"] = platform,
            ["username"] = SocialChannels.Cfg(a, SocialChannels.UsernameKey),
            ["display_name"] = SocialChannels.Cfg(a, SocialChannels.DisplayNameKey),
            ["name"] = a.Name,
            ["avatar"] = SocialChannels.Cfg(a, SocialChannels.AvatarKey),
            ["profile_url"] = SocialChannels.Cfg(a, SocialChannels.ProfileUrlKey),
            ["active"] = SocialChannels.Cfg(a, SocialChannels.StatusKey) != "disconnected",
            ["health"] = a.HealthStatus,
            ["entity_id"] = a.EntityId,
            ["entity_slug"] = level?.Slug,
            ["entity_name"] = level?.Name,
            ["can_publish"] = publish,
            ["can_comments"] = comments,
            ["can_dm"] = dm,
            ["can_reviews"] = reviews,
            ["method"] = ZernioNative.Method,
            ["method_label"] = ZernioNative.MethodLabel,
            // facebook: the Pages it publishes to (each a target "<id>:<page id>"), each with its outlet
            ["pages"] = platform == "facebook"
                ? (pages ?? Enumerable.Empty<SocialPage>()).OrderByDescending(p => p.IsSelected).ThenBy(p => p.Name)
                    .Select(p =>
                    {
                        var eff = p.EntityId ?? a.EntityId;
                        var pl = eff is int pe && names.TryGetValue(pe, out var pn) ? pn : ((string Name, string Slug)?)null;
                        return new Dictionary<string, object?>
                        {
                            ["id"] = p.PageId,
                            ["target"] = SocialChannels.Target(a.ExternalId, p.PageId),
                            ["name"] = p.Name,
                            ["username"] = p.Username,
                            ["category"] = p.Category,
                            ["avatar"] = p.Avatar.Length > 0 ? p.Avatar : null,
                            // the Page's own binding (null = it follows the account) and where it publishes
                            ["entity_id"] = p.EntityId,
                            ["effective_entity_id"] = eff,
                            ["entity_slug"] = pl?.Slug,
                            ["entity_name"] = pl?.Name,
                            ["selected"] = p.IsSelected,
                        };
                    }).ToList()
                : null,
        };
    }

    /// <summary>The social part of an inbox account payload (GET /api/messaging/accounts).</summary>
    /// <summary>The managed-connection part of a NATIVE inbox payload (GET /api/messaging/accounts, /api/whatsapp/accounts).</summary>
    public static void AddNativePayload(Dictionary<string, object?> outv, ChannelAccount a)
    {
        foreach (var (k, v) in ZernioNative.Payload(a)) outv[k] = v;
        outv["configured"] = ZernioNative.Configured(a);
        outv["webhook_path"] = "";
        outv["access_token_masked"] = "";
    }

    public static void AddAccountPayload(Dictionary<string, object?> outv, ChannelAccount a)
    {
        outv["provider"] = Providers.Zernio;
        outv["method"] = ZernioNative.Method;
        outv["method_label"] = ZernioNative.MethodLabel;
        outv["health_status"] = a.HealthStatus;
        outv["platform"] = SocialChannels.Platform(a);
        outv["username"] = SocialChannels.Cfg(a, SocialChannels.UsernameKey);
        outv["zernio_account_id"] = a.ExternalId;
        outv["configured"] = SocialChannels.Configured(a);
        outv["credentials_held_by"] = "zernio";
        outv["webhook_path"] = "";
    }

    private static string PlatformAlias(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "x" => "twitter",
        "messenger" or "facebook_messenger" => "facebook",
        "ig" => "instagram",
        "google_business" or "google-business" or "gmb" or "google" => "googlebusiness",
        var p => p,
    };

    // ---- comment actions ---------------------------------------------------------

    private static void MapThreadActions(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/social/threads/{key}/hide", async (string key, Dictionary<string, JsonElement>? body,
            AppDbContext db, IZernioClient zernio, HttpContext ctx) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var (thread, st, err) = await CommentThread(ctx, db);
            if (err != null) return err;
            if (!SocialChannels.HidePlatforms.Contains(st!.Platform))
                return Results.Json(new { detail = $"{SocialChannels.PlatformLabel(st.Platform)} does not let a comment be hidden." },
                    statusCode: 400);
            var hidden = BoolOf(body, "hidden", true);
            var r = await zernio.HideCommentAsync(st.ZernioAccountId, st.PostId, st.RootCommentId, hidden);
            if (!r.Ok) return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
            st.Hidden = hidden;
            st.UpdatedAt = DateTime.UtcNow;
            thread!.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            AuditTrail.Write(db, hidden ? "social.comment.hide" : "social.comment.unhide", $"social:{thread.Key}", "",
                entityId: thread.EntityId);
            return Results.Ok(new { hidden });
        }).AddEndpointFilter(SocialChannelRoute).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireThreadAccess("chats.send");

        app.MapPost("/api/social/threads/{key}/private-reply", async (string key,
            Dictionary<string, JsonElement>? body, AppDbContext db, ScopingService sc, IZernioClient zernio,
            HttpContext ctx) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var text = (Str(body, "text") ?? "").Trim();
            if (text.Length == 0) return Results.Json(new { detail = "text is required." }, statusCode: 400);
            var (thread, st, err) = await CommentThread(ctx, db);
            if (err != null) return err;
            if (!SocialChannels.PrivateReplyPlatforms.Contains(st!.Platform))
                return Results.Json(new { detail = "A private reply is possible on Instagram and Facebook comments only." },
                    statusCode: 400);
            if (st.PrivateReplied)
                return Results.Json(new { detail = "This comment already had its one private reply." }, statusCode: 409);
            var r = await zernio.PrivateReplyAsync(st.ZernioAccountId, st.PostId, st.RootCommentId, text);
            if (!r.Ok) return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
            st.PrivateReplied = true;
            st.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            // the DM itself lives in the commenter's DM conversation; here, a note says it went
            var threads = new ThreadStore(db, sc);
            var note = threads.AppendMessage(thread!, MessageDirection.Out, MessageSource.Agent,
                $"Private reply sent: {text}", status: "note", accountId: thread!.AccountId, priv: true);
            AuditTrail.Write(db, "social.comment.private_reply", $"social:{thread.Key}", text, entityId: thread.EntityId);
            return Results.Ok(new { sent = true, note_id = note.Id });
        }).AddEndpointFilter(SocialChannelRoute).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireThreadAccess("chats.send");
    }

    /// <summary>These routes name a SOCIAL conversation by key alone: tell the thread resolver its channel.</summary>
    private static async ValueTask<object?> SocialChannelRoute(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        ctx.HttpContext.Request.RouteValues["channel"] = ChatBranch.Domain.Channels.Social;
        return await next(ctx);
    }

    private static async Task<(Thread? Thread, SocialThread? Social, IResult? Error)> CommentThread(HttpContext ctx,
        AppDbContext db)
    {
        var thread = await ThreadResolver.LoadAsync(ctx, ChatBranch.Domain.Channels.Social);
        if (thread == null) return (null, null, Results.Json(new { detail = "Thread not found." }, statusCode: 404));
        var st = await db.SocialThreads.FirstOrDefaultAsync(s => s.ThreadId == thread.Id);
        if (st == null || st.Kind != SocialChannels.Comment || st.PostId.Length == 0)
            return (thread, null, Results.Json(new { detail = "This conversation is not a social comment." }, statusCode: 400));
        return (thread, st, null);
    }

    // ---- the inbox payload's `social` object ---------------------------------------

    /// <summary>The `social` object of every social thread / inbox row, by thread id.</summary>
    public static Dictionary<int, Dictionary<string, object?>> SocialMap(AppDbContext db, IEnumerable<int> threadIds)
    {
        var ids = threadIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        return db.SocialThreads.AsNoTracking().Where(s => ids.Contains(s.ThreadId)).ToList()
            .ToDictionary(s => s.ThreadId, SocialPayload);
    }

    /// <summary>Adds `social` to a thread payload when the thread is a social one (fluent, for initialisers).</summary>
    public static Dictionary<string, object?> WithSocial(this Dictionary<string, object?> payload, AppDbContext db, Thread t)
    {
        if (SocialFor(db, t) is { } social) payload["social"] = social;
        return payload;
    }

    public static Dictionary<string, object?>? SocialFor(AppDbContext db, Thread t) =>
        t.Channel != ChatBranch.Domain.Channels.Social
            ? null
            : db.SocialThreads.AsNoTracking().FirstOrDefault(s => s.ThreadId == t.Id) is { } st ? SocialPayload(st) : null;

    public static Dictionary<string, object?> SocialPayload(SocialThread s)
    {
        var o = new Dictionary<string, object?>
        {
            ["kind"] = s.Kind,
            ["platform"] = s.Platform,
            ["account_username"] = s.AccountUsername,
            ["author"] = new Dictionary<string, object?>
            {
                ["name"] = s.AuthorName,
                ["handle"] = s.AuthorHandle,
                ["avatar"] = s.AuthorAvatar,
            },
        };
        if (s.Kind == SocialChannels.Comment)
        {
            o["post"] = new Dictionary<string, object?>
            {
                ["id"] = s.ZernioPostId.Length > 0 ? s.ZernioPostId : s.PostId,
                ["text"] = s.PostText,
                ["permalink"] = s.PostPermalink,
                ["media_url"] = s.PostMediaUrl,
            };
            o["hidden"] = s.Hidden;
            o["can_hide"] = SocialChannels.HidePlatforms.Contains(s.Platform);
            o["can_private_reply"] = SocialChannels.PrivateReplyPlatforms.Contains(s.Platform) && !s.PrivateReplied;
            o["private_replied"] = s.PrivateReplied;
        }
        if (s.Kind == SocialChannels.Review) o["rating"] = s.Rating;
        return o;
    }

    // ---- posts --------------------------------------------------------------------

    private static void MapPosts(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/social/posts", async (string? entity, string? status, string? from, string? to,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, IZernioClient zernio) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var (filter, all) = await ListScope(db, sc, me, entity);
            var q = db.SocialPosts.AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                var wanted = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                q = q.Where(p => wanted.Contains(p.Status));
            }
            else q = q.Where(p => p.Status != SocialPosts.Deleted);
            if (ParseTime(from, null) is DateTime f) q = q.Where(p => (p.ScheduledAt ?? p.CreatedAt) >= f);
            if (ParseTime(to, null) is DateTime t) q = q.Where(p => (p.ScheduledAt ?? p.CreatedAt) <= t);
            var rows = (await q.OrderByDescending(p => p.ScheduledAt ?? p.CreatedAt).ThenByDescending(p => p.Id)
                    .Take(500).ToListAsync())
                .Where(p => all || filter!.Contains(p.EntityId)).ToList();

            // reconcile what should have finished by now (webhooks can be missed)
            var due = rows.Where(p => p.ZernioPostId.Length > 0
                                      && p.Status is SocialPosts.Scheduled or SocialPosts.Publishing
                                      && (p.ScheduledAt ?? p.UpdatedAt) < DateTime.UtcNow.AddMinutes(-2))
                .Take(10).ToList();
            foreach (var p in due)
            {
                var r = await zernio.GetPostAsync(p.ZernioPostId);
                if (r.Ok) SocialPosts.Apply(p, r.At("post"));
            }
            if (due.Count > 0) await db.SaveChangesAsync();
            return Results.Ok(new { posts = rows.Select(SocialPosts.Payload) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.view");

        app.MapGet("/api/social/posts/{id:int}", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IZernioClient zernio) =>
        {
            var post = await db.SocialPosts.FirstOrDefaultAsync(p => p.Id == id);
            if (post == null || !Visible(db, sc, me, post.EntityId))
                return Results.Json(new { detail = "Post not found." }, statusCode: 404);
            if (zernio.IsConfigured && post.ZernioPostId.Length > 0
                && post.Status is SocialPosts.Scheduled or SocialPosts.Publishing)
            {
                var r = await zernio.GetPostAsync(post.ZernioPostId);
                if (r.Ok)
                {
                    SocialPosts.Apply(post, r.At("post"));
                    await db.SaveChangesAsync();
                }
            }
            return Results.Ok(new { post = SocialPosts.Payload(post) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.view");

        app.MapPost("/api/social/posts", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, HttpContext ctx) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var (entityId, err) = await ActScope(db, sc, me,
                ctx.Request.Query["entity"].ToString() is { Length: > 0 } qe ? qe : Str(body, "entity"));
            if (err != null) return err;
            var post = new SocialPost
            {
                EntityId = entityId,
                CreatedBy = me.Identity?.Name ?? "",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            if (Fill(db, post, body, creating: true) is { } bad) return bad;
            db.SocialPosts.Add(post);
            await db.SaveChangesAsync();
            if (post.Status != SocialPosts.Draft) await PushAsync(db, zernio, post, PublishNow(body, post));
            AuditTrail.Write(db, "social.post.create", $"post #{post.Id}", $"{post.Status} → {string.Join(", ", post.Platforms)}",
                entityId: entityId);
            return Results.Ok(new { post = SocialPosts.Payload(post) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.publish");

        app.MapMethods("/api/social/posts/{id:int}", new[] { "PATCH" }, async (int id,
            Dictionary<string, JsonElement>? body, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            IZernioClient zernio) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var post = await db.SocialPosts.FirstOrDefaultAsync(p => p.Id == id);
            if (post == null || !Visible(db, sc, me, post.EntityId))
                return Results.Json(new { detail = "Post not found." }, statusCode: 404);
            if (ShellScope.ObjectScopeError(db, sc, me, post.EntityId) is { } outside) return outside;
            if (post.Status is SocialPosts.Published or SocialPosts.Deleted or SocialPosts.Publishing)
                return Results.Json(new { detail = $"A {post.Status} post cannot be edited." }, statusCode: 409);
            if (Fill(db, post, body, creating: false) is { } bad) return bad;
            post.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            if (post.ZernioPostId.Length > 0 || post.Status != SocialPosts.Draft)
                await PushAsync(db, zernio, post, PublishNow(body, post));
            AuditTrail.Write(db, "social.post.update", $"post #{post.Id}", post.Status, entityId: post.EntityId);
            return Results.Ok(new { post = SocialPosts.Payload(post) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.publish");

        app.MapDelete("/api/social/posts/{id:int}", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IZernioClient zernio) =>
        {
            var post = await db.SocialPosts.FirstOrDefaultAsync(p => p.Id == id);
            if (post == null || !Visible(db, sc, me, post.EntityId))
                return Results.Json(new { detail = "Post not found." }, statusCode: 404);
            if (ShellScope.ObjectScopeError(db, sc, me, post.EntityId) is { } outside) return outside;
            var warnings = new List<string>();
            if (post.ZernioPostId.Length > 0)
            {
                if (!zernio.IsConfigured) return NotConfigured(zernio);
                if (post.Status is SocialPosts.Published or SocialPosts.Partial)
                {
                    // a published post is taken down per platform (Instagram / TikTok cannot be)
                    foreach (var r in post.Results.Where(r => (r.GetValueOrDefault("status")?.ToString() ?? "") == "published"))
                    {
                        var platform = r.GetValueOrDefault("platform")?.ToString() ?? "";
                        var acct = r.GetValueOrDefault("account_id")?.ToString() ?? "";
                        var u = await zernio.UnpublishPostAsync(post.ZernioPostId, platform, acct);
                        if (!u.Ok) warnings.Add($"{SocialChannels.PlatformLabel(platform)}: {u.Error}");
                    }
                }
                else
                {
                    var d = await zernio.DeletePostAsync(post.ZernioPostId);
                    if (!d.Ok && d.Status != 404)
                        return Results.Json(new { detail = d.Error, code = "zernio_error" }, statusCode: 502);
                }
            }
            post.Status = SocialPosts.Deleted;
            post.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            AuditTrail.Write(db, "social.post.delete", $"post #{post.Id}", string.Join("; ", warnings), entityId: post.EntityId);
            return Results.Ok(new { ok = true, warnings });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.publish");

        app.MapPost("/api/social/posts/{id:int}/retry", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IZernioClient zernio) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var post = await db.SocialPosts.FirstOrDefaultAsync(p => p.Id == id);
            if (post == null || !Visible(db, sc, me, post.EntityId))
                return Results.Json(new { detail = "Post not found." }, statusCode: 404);
            if (ShellScope.ObjectScopeError(db, sc, me, post.EntityId) is { } outside) return outside;
            if (post.ZernioPostId.Length == 0 || post.Status is not (SocialPosts.Failed or SocialPosts.Partial))
                return Results.Json(new { detail = "Only a failed or partly published post can be retried." }, statusCode: 409);
            var r = await zernio.RetryPostAsync(post.ZernioPostId);
            if (r.At("post").ValueKind == JsonValueKind.Object) SocialPosts.Apply(post, r.At("post"));
            if (!r.Ok && post.Status != SocialPosts.Failed) post.Status = SocialPosts.Failed;
            if (!r.Ok) post.Error = r.Error;
            post.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            AuditTrail.Write(db, "social.post.retry", $"post #{post.Id}", post.Status, entityId: post.EntityId);
            return r.Ok || r.Status is 200 or 207
                ? Results.Ok(new { post = SocialPosts.Payload(post) })
                : Results.Json(new { detail = r.Error, code = "zernio_error", post = SocialPosts.Payload(post) }, statusCode: 502);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.publish");

        // Upload a file for a post: Zernio presigned upload → {url, type}.
        app.MapPost("/api/social/media", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IZernioClient zernio) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var (_, err) = await ActScope(db, sc, me, ctx.Request.Query["entity"]);
            if (err != null) return err;
            if (!ctx.Request.HasFormContentType)
                return Results.Json(new { detail = "Send a multipart form with the file in 'file'." }, statusCode: 400);
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return Results.Json(new { detail = "file is required." }, statusCode: 400);
            if (file.Length > MaxUpload)
                return Results.Json(new { detail = $"The file is too large (max {MaxUpload / 1048576} MB)." }, statusCode: 413);
            var contentType = MediaContentType(file.FileName, file.ContentType);
            if (contentType == null)
                return Results.Json(new { detail = "Unsupported file type — images (jpg, png, webp, gif), videos (mp4, mov, webm) or PDF." },
                    statusCode: 400);
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            var name = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(name)) name = "upload";
            var presign = await zernio.PresignMediaAsync(name, contentType, bytes.Length);
            if (!presign.Ok) return Results.Json(new { detail = presign.Error, code = "zernio_error" }, statusCode: 502);
            var up = await zernio.UploadMediaAsync(presign.Str("uploadUrl"), bytes, contentType);
            if (!up.Ok) return Results.Json(new { detail = up.Error, code = "zernio_error" }, statusCode: 502);
            return Results.Ok(new { url = presign.Str("publicUrl"), type = MediaKind(contentType) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.publish").DisableAntiforgery();

        // "Draft with AI": a caption from a brief, metered like the other interactive AI features.
        app.MapPost("/api/social/posts/draft", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, ILlmClient llm, HttpContext ctx) =>
        {
            var brief = (Str(body, "brief") ?? "").Trim();
            if (brief.Length == 0) return Results.Json(new { detail = "brief is required." }, statusCode: 400);
            if (brief.Length > 2000) brief = brief[..2000];
            if (QuotaGuard.Refusal(db, sc, me) is { } quota) return quota;
            var (entityId, err) = await ActScope(db, sc, me, ctx.Request.Query["entity"]);
            if (err != null) return err;
            var platforms = StrList(body, "platforms").Select(PlatformAlias).Where(p => p.Length > 0).Distinct().ToList();
            var tone = (Str(body, "tone") ?? "").Trim();
            if (!llm.IsConfigured)
                return Results.Ok(new { content = brief, heuristic = true });

            var limit = platforms.Contains("twitter") ? 280 : platforms.Contains("bluesky") ? 300 : 2200;
            var system =
                "You write social media posts for a business. Write ONE ready-to-publish post from the operator's brief. " +
                $"Keep it under {limit} characters" +
                (platforms.Count > 0 ? $", suitable for {string.Join(", ", platforms.Select(SocialChannels.PlatformLabel))}" : "") +
                (tone.Length > 0 ? $", in a {tone} tone" : "") +
                ". Use a few relevant hashtags at most. Never invent prices, dates, offers or facts the brief does not give. " +
                "Reply with the post text only — no quotes, no preamble.";
            LlmResult result;
            try { result = await llm.CompleteAsync(system, new[] { ("user", brief) }, ctx.RequestAborted); }
            catch (Exception)
            {
                return Results.Json(new { detail = "The AI model could not be reached." }, statusCode: 502);
            }
            try
            {
                db.LlmUsage.Add(new LlmUsage
                {
                    Feature = "social_draft", Provider = result.Provider, Model = result.Model, Channel = "social",
                    EntityId = entityId, UserName = me.Identity?.Name ?? "", Ref = "social:draft",
                    PromptTokens = result.PromptTokens, CompletionTokens = result.CompletionTokens, CostUsd = result.CostUsd,
                    Detail = new Dictionary<string, object?> { ["platforms"] = platforms },
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            catch { /* the ledger never breaks a draft */ }
            var content = (result.Text ?? "").Trim().Trim('"');
            return Results.Ok(new { content = content.Length > 0 ? content : brief, heuristic = result.Provider == "sandbox" });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.publish");
    }

    private const long MaxUpload = 100L * 1024 * 1024;

    /// <summary>The composer's body onto a post. null = fine, else the 400.</summary>
    private static IResult? Fill(AppDbContext db, SocialPost post, Dictionary<string, JsonElement>? body, bool creating)
    {
        bool Has(string k) => body != null && body.ContainsKey(k);
        if (creating || Has("content")) post.Content = (Str(body, "content") ?? "").Trim();
        if (creating || Has("media"))
        {
            var media = new List<Dictionary<string, object?>>();
            if (body != null && body.TryGetValue("media", out var m) && m.ValueKind == JsonValueKind.Array)
                foreach (var item in m.EnumerateArray())
                {
                    var url = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : ZernioJson.Str(item, "url");
                    if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        return Results.Json(new { detail = "Every media item needs a public https:// url." }, statusCode: 400);
                    var type = ZernioJson.Str(item, "type");
                    if (type is not ("image" or "video" or "gif" or "document")) type = GuessKind(url);
                    media.Add(new Dictionary<string, object?> { ["url"] = url, ["type"] = type });
                }
            post.Media = media;
        }
        if (creating || Has("account_ids"))
        {
            // "<account id>" or, for one Facebook Page of a facebook account, "<account id>:<page id>"
            var ids = StrList(body, "account_ids").Distinct().ToList();
            var targets = ids.Select(i => (Entry: i, Parts: SocialChannels.ParseTarget(i))).ToList();
            var acctIds = targets.Select(t => t.Parts.AccountId).Distinct().ToList();
            var accounts = db.ChannelAccounts.AsNoTracking()
                .Where(a => a.Channel == ChatBranch.Domain.Channels.Social && acctIds.Contains(a.ExternalId)).ToList();
            var unknown = targets.Where(t => accounts.All(a => a.ExternalId != t.Parts.AccountId)).Select(t => t.Entry).ToList();
            if (unknown.Count > 0)
                return Results.Json(new { detail = $"Unknown social account(s): {string.Join(", ", unknown)}." }, statusCode: 400);
            ChannelAccount Acct(string i) => accounts.First(a => a.ExternalId == i);
            var pageRows = db.SocialPages.AsNoTracking().Where(p => acctIds.Contains(p.ZernioAccountId)).ToList();
            var levels = new List<int?>();
            foreach (var (entry, (acct, pageId)) in targets)
            {
                if (pageId.Length == 0) { levels.Add(Acct(acct).EntityId); continue; }
                if (SocialChannels.Platform(Acct(acct)) != "facebook")
                    return Results.Json(new { detail = $"{entry}: only a Facebook account has Pages." }, statusCode: 400);
                var page = pageRows.FirstOrDefault(p => p.ZernioAccountId == acct && p.PageId == pageId);
                if (page == null)
                    return Results.Json(new { detail = $"{entry}: that Facebook Page is not one of the account's Pages." },
                        statusCode: 400);
                levels.Add(page.EntityId ?? Acct(acct).EntityId);
            }
            // an entity posts through its OWN profile's accounts (or the Pages bound to it) only
            if (levels.Any(l => l != post.EntityId))
                return Results.Json(new { detail = "Every account must belong to the same entity as the post." }, statusCode: 400);
            var cannot = accounts.Where(a => !SocialChannels.PublishPlatforms.Contains(SocialChannels.Platform(a))).ToList();
            if (cannot.Count > 0)
                return Results.Json(new { detail = $"These accounts cannot publish posts: {string.Join(", ", cannot.Select(a => a.Name))}." },
                    statusCode: 400);
            post.AccountIds = ids;
            post.Platforms = targets.Select(t => SocialChannels.Platform(Acct(t.Parts.AccountId))).ToList();
        }
        if (creating || Has("timezone")) post.Timezone = (Str(body, "timezone") ?? "").Trim();
        if (post.Timezone.Length > 0 && Zone(post.Timezone) == null)
            return Results.Json(new { detail = $"Unknown time zone '{post.Timezone}'." }, statusCode: 400);
        if (creating || Has("scheduled_at"))
        {
            var raw = (Str(body, "scheduled_at") ?? "").Trim();
            if (raw.Length == 0) post.ScheduledAt = null;
            else if (ParseTime(raw, post.Timezone) is DateTime at) post.ScheduledAt = at;
            else return Results.Json(new { detail = "scheduled_at must be an ISO 8601 date-time." }, statusCode: 400);
        }

        var draft = BoolOf(body, "draft", creating ? false : post.Status == SocialPosts.Draft && post.ZernioPostId.Length == 0);
        if (BoolOf(body, "publish_now", false)) draft = false;
        if (draft)
        {
            post.Status = SocialPosts.Draft;
            return null;
        }
        if (post.AccountIds.Count == 0)
            return Results.Json(new { detail = "Pick at least one account to publish to." }, statusCode: 400);
        if (post.Content.Length == 0 && post.Media.Count == 0)
            return Results.Json(new { detail = "Write something or attach media." }, statusCode: 400);
        post.Status = post.ScheduledAt is DateTime s && s > DateTime.UtcNow && !BoolOf(body, "publish_now", false)
            ? SocialPosts.Scheduled
            : SocialPosts.Publishing;
        return null;
    }

    private static bool PublishNow(Dictionary<string, JsonElement>? body, SocialPost post) =>
        BoolOf(body, "publish_now", false) || post.ScheduledAt == null || post.ScheduledAt <= DateTime.UtcNow;

    /// <summary>Create (or update) the Zernio post and apply its answer.</summary>
    private static Task PushAsync(AppDbContext db, IZernioClient zernio, SocialPost post, bool publishNow) =>
        SocialPosts.PushAsync(db, zernio, post, publishNow);

    // ---- scope ---------------------------------------------------------------------

    /// <summary>
    /// The entities a LIST may show. <c>entity=</c> empty = everything the caller sees
    /// (all, or their bound subtrees); a pick (a comma list from the switcher) = exactly
    /// those entities — a profile belongs to ONE entity, so no inheritance here.
    /// </summary>
    private static async Task<(List<int?>? Filter, bool All)> ListScope(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, string? entity)
    {
        var bound = ShellScope.BoundEntityIds(db, sc, me);
        if (string.IsNullOrWhiteSpace(entity) || entity is "all" or "__all__")
            return bound == null ? (null, true) : (bound.Select(b => (int?)b).ToList(), false);
        var picks = new List<int>();
        foreach (var part in entity.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part is "all" or "__all__") return bound == null ? (null, true) : (bound.Select(b => (int?)b).ToList(), false);
            if (await EntityOf(db, part) is int id) picks.Add(id);
        }
        if (bound != null) picks = picks.Where(bound.Contains).ToList();
        // An outlet INHERITS like every other library: the organization-wide rows (null)
        // and its ancestors' rows, plus its own — so accounts connected at the
        // organization level are usable from any outlet.
        var levels = new List<int?> { null };
        foreach (var p in picks)
            foreach (var id in sc.EntityChain(p)) if (!levels.Contains(id)) levels.Add(id);
        foreach (var p in picks) if (!levels.Contains(p)) levels.Add(p);
        return (levels, false);
    }

    /// <summary>The ONE level an action happens at (null = the organization), with the scope check.</summary>
    private static async Task<(int? EntityId, IResult? Error)> ActScope(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.Contains(',')) v = v.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        int? entityId = null;
        if (v.Length > 0 && v is not ("all" or "__all__"))
        {
            entityId = await EntityOf(db, v);
            if (entityId == null) return (null, Results.Json(new { detail = "Unknown entity." }, statusCode: 404));
        }
        return (entityId, ShellScope.CompanyLevelError(db, sc, me, entityId));
    }

    private static async Task<int?> EntityOf(AppDbContext db, string slugOrId)
    {
        if (int.TryParse(slugOrId, out var id))
            return await db.Entities.AnyAsync(e => e.Id == id) ? id : null;
        return await db.Entities.AsNoTracking().Where(e => e.Slug == slugOrId).Select(e => (int?)e.Id).FirstOrDefaultAsync();
    }

    private static bool Visible(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? entityId)
    {
        var bound = ShellScope.BoundEntityIds(db, sc, me);
        return bound == null || (entityId is int e && bound.Contains(e));
    }

    /// <summary>Where the dashboard lives (FRONTEND_URL, else this API's public origin).</summary>
    private static string Dashboard(HttpContext ctx, ScopingService sc)
    {
        var front = (Environment.GetEnvironmentVariable("FRONTEND_URL") ?? "").Trim().TrimEnd('/');
        return front.Length > 0 ? front : MessagingAccountTransport.PublicBaseUrl(null, sc, ctx);
    }

    // ---- small readers --------------------------------------------------------------

    /// <summary>A connect field: <c>credentials.{key}</c> (the stepper's shape), else the top-level key.</summary>
    private static string Cred(Dictionary<string, JsonElement>? body, string key)
    {
        if (body != null && body.TryGetValue("credentials", out var c) && c.ValueKind == JsonValueKind.Object
            && c.TryGetProperty(key, out var v))
            return (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : "") ?? "";
        return Str(body, key) ?? "";
    }

    private static string? Str(Dictionary<string, JsonElement>? body, string key) =>
        body != null && body.TryGetValue(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            }
            : null;

    private static List<string> StrList(Dictionary<string, JsonElement>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var v)) return new();
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(ZernioJson.Str).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (v.ValueKind == JsonValueKind.String)
            return (v.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return new();
    }

    private static bool BoolOf(Dictionary<string, JsonElement>? body, string key, bool dflt) =>
        body != null && body.TryGetValue(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => (v.GetString() ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes",
                _ => dflt,
            }
            : dflt;

    private static TimeZoneInfo? Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return null; }
    }

    /// <summary>ISO 8601 → UTC. A value with no offset is wall-clock time in <paramref name="zone"/> (UTC if none).</summary>
    private static DateTime? ParseTime(string? raw, string? zone)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return null;
        var hasOffset = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                        || System.Text.RegularExpressions.Regex.IsMatch(s, @"[+-]\d{2}:?\d{2}$");
        if (!hasOffset && !string.IsNullOrEmpty(zone) && Zone(zone) is { } tz)
        {
            var local = DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }
        return dto.UtcDateTime;
    }

    private static string? MediaContentType(string fileName, string? declared)
    {
        string[] allowed =
        {
            "image/jpeg", "image/png", "image/webp", "image/gif", "video/mp4", "video/mpeg", "video/quicktime",
            "video/webm", "video/x-m4v", "application/pdf",
        };
        var d = (declared ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (d == "image/jpg") d = "image/jpeg";
        if (allowed.Contains(d)) return d;
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".m4v" => "video/x-m4v",
            ".pdf" => "application/pdf",
            _ => null,
        };
    }

    private static string MediaKind(string contentType) => contentType switch
    {
        "image/gif" => "gif",
        var c when c.StartsWith("image/") => "image",
        var c when c.StartsWith("video/") => "video",
        _ => "document",
    };

    private static string GuessKind(string url)
    {
        var path = url.Split('?')[0].ToLowerInvariant();
        if (path.EndsWith(".gif")) return "gif";
        if (path.EndsWith(".mp4") || path.EndsWith(".mov") || path.EndsWith(".webm") || path.EndsWith(".m4v")) return "video";
        if (path.EndsWith(".pdf")) return "document";
        return "image";
    }
}
