using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Zernio;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>Remembers when the managed accounts were last reconciled (the GET's cheap reconcile, per host).</summary>
public sealed class ZernioSyncClock
{
    public DateTime LastSync { get; set; } = DateTime.MinValue;
}

/// <summary>
/// ChatBranch × Zernio: Zernio as a MANAGED CONNECTION for native messaging channels
/// (WhatsApp, Messenger, Instagram DM, Telegram, SMS) — the customer never handles platform
/// credentials. Social-media features (posts, comments, reviews) are out of scope.
///
/// <list type="bullet">
///   <item><c>/api/zernio/*</c> — the deployment's ONE Zernio key (saved with the other app keys,
///         <c>POST /api/whatsapp/settings {zernio_api_key}</c>), its status, a test and the webhook
///         registration: <c>app_keys.manage</c> (superadmin by default).</item>
///   <item><c>POST /api/zernio/webhook</c> — anonymous; the HMAC signature IS the auth (no secret ⇒ 503,
///         bad ⇒ 401), deduped on the event id.</item>
///   <item><c>/api/social/*</c> — the managed accounts (<c>channels.view</c> / <c>channels.manage</c>; PATCH
///         binds one to an entity by moving it to that entity's Zernio profile), connect flows, phone
///         numbers. The path keeps its historical name (clients, MCP tools and Meta's registered
///         WhatsApp callback use it).</item>
/// </list>
/// Not configured ⇒ 409 <c>{code: "zernio_not_configured"}</c> on everything that needs Zernio.
/// </summary>
public static partial class ZernioEndpoints
{
    public const string NotConfiguredCode = "zernio_not_configured";
    public const string WebhookSecretSetting = "zernio_webhook_secret";
    public const string WebhookIdSetting = "zernio_webhook_id";
    public const string WebhookPath = "/api/zernio/webhook";

    public static IServiceCollection AddZernio(this IServiceCollection services)
    {
        services.AddHttpClient(HttpZernioClient.ClientName).RemoveAllLoggers();
        services.AddSingleton<IZernioClient, HttpZernioClient>();
        // Zernio as a connection method on NATIVE channels (ZernioNative): the send seam for
        // Messenger / Instagram / Telegram / SMS, and WhatsApp behind the Cloud router
        services.AddSingleton<ZernioNativeTransport>();
        services.AddSingleton<ZernioWhatsAppClient>();
        services.AddSingleton<ZernioSyncClock>();
        services.AddScoped<ZernioAccounts>();
        services.AddScoped<ZernioWebhook>();
        return services;
    }

    // =========================================================================

    public static void MapZernioEndpoints(this IEndpointRouteBuilder app)
    {
        MapZernio(app);
        MapAccounts(app);
        MapWhatsAppOnboarding(app);   // ZernioEndpoints.WhatsApp.cs
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
                accounts = await db.ChannelAccounts.CountAsync(a => a.Provider == Providers.Zernio),
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
            ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts, ZernioSyncClock clock) =>
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
            var q = db.ChannelAccounts.AsNoTracking().Where(a => a.Provider == Providers.Zernio);
            var rows = (await q.OrderBy(a => a.Name).ToListAsync())
                .Where(a => ZernioKeys.Cfg(a, ZernioKeys.StatusKey) != "removed")
                .Where(a => all || filter!.Contains(a.EntityId)).ToList();
            // a Messenger account's Facebook Pages (which one reaches the inbox)
            var zids = rows.Select(a => a.ExternalId).ToList();
            var pages = await db.SocialPages.AsNoTracking().Where(p => zids.Contains(p.ZernioAccountId)).ToListAsync();
            var names = await EntityNames(db, rows.Select(a => a.EntityId));
            return Results.Ok(new
            {
                accounts = rows.Select(a => AccountRow(a, names, pages.Where(p => p.ZernioAccountId == a.ExternalId))),
                configured = true,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.view");

        // Connect an account for an entity. {platform, entity?} →
        // flow "hosted" ({url}: the platform's own login, Zernio hosts any selection step),
        // "code" (Telegram: an access code + steps, then poll …/telegram/status) or
        // "select" (SMS: pick one of the profile's numbers, then POST …/sms). See ConnectFlow.
        app.MapPost("/api/social/connect", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts, HttpContext ctx) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var platform = PlatformAlias(Str(body, "platform"));
            if (!ZernioKeys.ConnectPlatforms.Contains(platform) && platform != "sms")
                return Results.Json(new
                {
                    detail = $"platform must be one of {string.Join(", ", ZernioKeys.ConnectPlatforms.Append("sms").OrderBy(p => p))}.",
                }, statusCode: 400);
            // the role's channel policy: a managed connection of a channel the role is given
            if (ShellScope.ConnectError(db, me, platform, Providers.Zernio) is { } notAllowed) return notAllowed;
            var (entityId, err) = await ActScope(db, sc, me, Str(body, "entity") ?? ctx.Request.Query["entity"]);
            if (err != null) return err;
            var (profileId, perr) = await accounts.EnsureProfileAsync(entityId);
            if (profileId.Length == 0)
                return Results.Json(new { detail = perr, code = "zernio_error" }, statusCode: 502);
            accounts.SetIntent(profileId, platform);
            var entitySlug = entityId is int eid ? await db.Entities.AsNoTracking().Where(e => e.Id == eid)
                .Select(e => e.Slug).FirstOrDefaultAsync() : null;
            var common = new Dictionary<string, object?>
            {
                ["platform"] = platform,
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
                AuditTrail.Write(db, "zernio.connect", platform, "managed connection: access code issued", entityId: entityId);
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

            // WhatsApp runs Embedded Signup: the mode (Cloud-API-only / coexistence), the
            // hosted number picker, the state round-trip and its own callback live in
            // ZernioEndpoints.WhatsApp.cs — everything else keeps the plain hosted connect.
            if (platform == "whatsapp")
                return await StartWhatsAppSignupAsync(body, db, sc, zernio, ctx, entityId, profileId, common);

            var redirect = Dashboard(ctx, sc) + "/channels?zernio=connected";
            var hosted = await zernio.ConnectUrlAsync(platform, profileId, redirect);
            if (!hosted.Ok) return Results.Json(new { detail = hosted.Error, code = "zernio_error" }, statusCode: 502);
            var url = hosted.Str("authUrl");
            if (url.Length == 0) return Results.Json(new { detail = "No connect URL came back." }, statusCode: 502);
            AuditTrail.Write(db, "zernio.connect", platform, "managed connection started", entityId: entityId);
            var outv = new Dictionary<string, object?>(common) { ["flow"] = "hosted", ["url"] = url };
            return Results.Ok(outv);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // Telegram's access code: pending | connected (the inbox is created now) | expired.
        app.MapGet("/api/social/connect/telegram/status", async (string? code, string? entity, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts) =>
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
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // Telegram, the bot already an admin: connect by chat id.
        app.MapPost("/api/social/connect/telegram", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            if (ShellScope.ConnectError(db, me, "telegram", Providers.Zernio) is { } notAllowed) return notAllowed;
            var chat = Cred(body, "chat_id").Trim();
            if (chat.Length == 0) return Results.Json(new { detail = "chat_id is required." }, statusCode: 400);
            var (entityId, err) = await ActScope(db, sc, me, Str(body, "entity"));
            if (err != null) return err;
            var (profileId, perr) = await accounts.EnsureProfileAsync(entityId);
            if (profileId.Length == 0) return Results.Json(new { detail = perr, code = "zernio_error" }, statusCode: 502);
            accounts.SetIntent(profileId, "telegram");
            var r = await zernio.TelegramConnectAsync(profileId, chat);
            if (!r.Ok)
                return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: r.Status is 400 or 404 ? 400 : 502);
            var row = await MirrorConnectedAsync(db, accounts, entityId, r.At("account"), "telegram", profileId);
            AuditTrail.Write(db, "zernio.connect", "telegram", $"managed connection: chat {chat}", entityId: entityId);
            return Results.Ok(new { ok = true, connected = true, status = "connected", account = row == null ? null : ConnectedRow(db, row) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // WhatsApp with the business's own Meta credentials (the headless alternative to the hosted signup).
        app.MapPost("/api/social/connect/whatsapp", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            if (ShellScope.ConnectError(db, me, "whatsapp", Providers.Zernio) is { } notAllowed) return notAllowed;
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
            accounts.SetIntent(profileId, "whatsapp");
            var r = await zernio.WhatsAppCredentialsAsync(profileId, token, waba, pnid, pin.Length > 0 ? pin : null);
            if (!r.Ok)
                return Results.Json(new
                {
                    detail = r.Error.Replace(token, "••••", StringComparison.Ordinal),
                    code = "zernio_error",
                    available_phone_numbers = r.At("availablePhoneNumbers").ValueKind == JsonValueKind.Array ? r.At("availablePhoneNumbers") : (JsonElement?)null,
                }, statusCode: r.Status is 400 or 401 or 409 ? r.Status : 502);
            var row = await MirrorConnectedAsync(db, accounts, entityId, r.At("account"), "whatsapp", profileId);
            AuditTrail.Write(db, "zernio.connect", "whatsapp", "managed connection: own credentials", entityId: entityId);
            return Results.Ok(new
            {
                ok = true,
                connected = true,
                status = "connected",
                warning = r.Str("registrationWarning") is { Length: > 0 } w ? w : null,
                account = row == null ? null : ConnectedRow(db, row),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // SMS: turn SMS on for one of the profile's numbers → its SMS account becomes the inbox.
        app.MapPost("/api/social/connect/sms", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            if (ShellScope.ConnectError(db, me, "sms", Providers.Zernio) is { } notAllowed) return notAllowed;
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
            AuditTrail.Write(db, "zernio.connect", "sms", $"managed connection: {r.Str("phoneNumber")}", entityId: entityId);
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
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        app.MapDelete("/api/social/accounts/{id}", async (string id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IZernioClient zernio) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var row = await FindAccount(db, id);
            if (row == null) return Results.Json(new { detail = "Managed account not found." }, statusCode: 404);
            if (ShellScope.ObjectScopeError(db, sc, me, row.EntityId) is { } outside) return outside;
            var r = await zernio.DeleteAccountAsync(row.ExternalId);
            if (!r.Ok && r.Status != 404)
                return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
            var label = $"{row.Channel}:{row.Name}";
            // its conversations stay (back in sandbox), like any deleted inbox
            foreach (var each in await db.ChannelAccounts.Where(a => a.Provider == Providers.Zernio
                                                                    && a.ExternalId == row.ExternalId).ToListAsync())
            {
                await InboxSettings.DetachAccountAsync(db, each.Id);
                db.SocialPages.RemoveRange(db.SocialPages.Where(p => p.ZernioAccountId == each.ExternalId));
                db.ZernioConversations.RemoveRange(db.ZernioConversations.Where(c => c.AccountId == each.Id));
                db.ChannelAccounts.Remove(each);
            }
            await db.SaveChangesAsync();
            AuditTrail.Write(db, "account.delete", label, "managed connection disconnected", entityId: row.EntityId);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage")
            .RequireAccountChannel(FindAccount);

        // Bind an account to an entity ({entity: slug | id}, or null / "" for the organization):
        // Zernio moves it to that entity's profile, and its conversations follow.
        app.MapPatch("/api/social/accounts/{id}", async (string id, Dictionary<string, JsonElement>? body,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            if (body == null || !body.ContainsKey("entity"))
                return Results.Json(new
                {
                    detail = "entity is required: an entity slug or id, or null / \"\" for the organization level.",
                }, statusCode: 400);
            var row = await FindAccount(db, id);
            if (row == null) return Results.Json(new { detail = "Managed account not found." }, statusCode: 404);
            // a bound caller never sees another company's account: not found, not "forbidden"
            if (row.EntityId is int cur && !Visible(db, sc, me, cur))
                return Results.Json(new { detail = "Managed account not found." }, statusCode: 404);
            if (ShellScope.CompanyLevelError(db, sc, me, row.EntityId) is { } outside) return outside;
            var (target, err) = await ActScope(db, sc, me, Str(body, "entity"));
            if (err != null) return err;

            var from = row.EntityId;
            var (error, status) = await accounts.MoveAsync(row, target);
            if (status != 0)
                return Results.Json(new { detail = status == 409 ? NotConfiguredDetail : error, code = status == 409 ? NotConfiguredCode : "zernio_error" },
                    statusCode: status);
            var names = await EntityNames(db, new[] { from, target });
            string Level(int? e) => e is int x && names.TryGetValue(x, out var n) ? n.Slug : "organization";
            AuditTrail.Write(db, "account.entity", $"{row.Channel}:{row.Name}",
                $"moved {Level(from)} → {Level(target)} (connection profile {ZernioKeys.Cfg(row, ZernioKeys.ProfileIdKey)})",
                entityId: target);
            return Results.Ok(await FullRow(db, row));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage")
            .RequireAccountChannel(FindAccount);

        // Which Facebook Page's Messenger conversations reach the inbox (Zernio's selectedPageId).
        app.MapPost("/api/social/accounts/{id}/pages/{pageId}/select", async (string id, string pageId,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var (row, page, missing) = await FindPage(db, sc, me, accounts, id, pageId);
            if (missing != null) return missing;
            if (ShellScope.CompanyLevelError(db, sc, me, row!.EntityId) is { } outside) return outside;
            var (error, status) = await accounts.SelectPageAsync(row, page!);
            if (status != 0)
                return Results.Json(new { detail = error, code = "zernio_error" }, statusCode: status);
            AuditTrail.Write(db, "account.update", $"{row.Channel}:{row.Name}", $"inbox Page → {page!.Name} ({page.PageId})",
                entityId: row.EntityId);
            return Results.Ok(await FullRow(db, row));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage")
            .RequireAccountChannel(FindAccount);
    }

    /// <summary>A Messenger account + one of its Pages, or the 404 (also for an account a bound caller cannot see).</summary>
    private static async Task<(ChannelAccount? Account, SocialPage? Page, IResult? Error)> FindPage(AppDbContext db,
        ScopingService sc, ClaimsPrincipal me, ZernioAccounts accounts, string id, string pageId)
    {
        var row = await FindAccount(db, id);
        if (row == null || (row.EntityId is int cur && !Visible(db, sc, me, cur)))
            return (null, null, Results.Json(new { detail = "Managed account not found." }, statusCode: 404));
        var page = await db.SocialPages.FirstOrDefaultAsync(p => p.ZernioAccountId == row.ExternalId && p.PageId == pageId);
        if (page == null)
        {
            // not mirrored yet: read Zernio's list once
            if (row.Channel == ChatBranch.Domain.Channels.Facebook) await accounts.SyncPagesAsync(row);
            page = await db.SocialPages.FirstOrDefaultAsync(p => p.ZernioAccountId == row.ExternalId && p.PageId == pageId);
        }
        if (page == null)
            return (row, null, Results.Json(new { detail = "That Facebook Page is not one of this account's Pages." },
                statusCode: 404));
        return (row, page, null);
    }

    /// <summary>One account's row with every one of its Pages (the PATCH / select answers).</summary>
    private static async Task<object> FullRow(AppDbContext db, ChannelAccount row)
    {
        var pages = await db.SocialPages.AsNoTracking().Where(p => p.ZernioAccountId == row.ExternalId).ToListAsync();
        var names = await EntityNames(db, new[] { row.EntityId });
        return AccountRow(row, names, pages);
    }

    /// <summary>A managed inbox by its Zernio account id, else by its ChannelAccount id.</summary>
    private static async Task<ChannelAccount?> FindAccount(AppDbContext db, string id) =>
        await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Provider == Providers.Zernio && a.ExternalId == id)
        ?? (int.TryParse(id, out var pk)
            ? await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == pk && a.Provider == Providers.Zernio)
            : null);

    /// <summary>Mirror an account a connect endpoint just made (Telegram / WhatsApp credentials / SMS) at once.</summary>
    private static async Task<ChannelAccount?> MirrorConnectedAsync(AppDbContext db, ZernioAccounts accounts, int? entityId,
        JsonElement acct, string platform, string? profileId = null)
    {
        if (acct.ValueKind != JsonValueKind.Object) return null;
        var d = JsonSerializer.Deserialize<Dictionary<string, object?>>(acct.GetRawText()) ?? new();
        d.TryAdd("platform", platform);
        if (!d.ContainsKey("_id") && d.TryGetValue("accountId", out var aid)) d["_id"] = aid;
        var el = JsonSerializer.SerializeToElement(d);
        profileId ??= (await accounts.EnsureProfileAsync(entityId, create: false)).ProfileId;
        return accounts.Upsert(el, entityId, profileId ?? "", intent: "inbox") ?? accounts.Find(ZernioJson.Str(el, "_id"));
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
            ["configured"] = ZernioNative.Configured(row),
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

    /// <summary>The managed accounts list row (one per Zernio account = one inbox).</summary>
    private static object AccountRow(ChannelAccount a, IReadOnlyDictionary<int, (string Name, string Slug)> names,
        IEnumerable<SocialPage>? pages = null)
    {
        var level = a.EntityId is int eid && names.TryGetValue(eid, out var n) ? n : ((string Name, string Slug)?)null;
        return new Dictionary<string, object?>
        {
            ["id"] = a.ExternalId,
            ["account_id"] = a.Id,
            ["channel"] = a.Channel,
            ["platform"] = ZernioKeys.Platform(a),
            ["username"] = ZernioKeys.Cfg(a, ZernioKeys.UsernameKey),
            ["display_name"] = ZernioKeys.Cfg(a, ZernioKeys.DisplayNameKey),
            ["name"] = a.Name,
            ["address"] = ZernioNative.Address(a),
            ["avatar"] = ZernioKeys.Cfg(a, ZernioKeys.AvatarKey),
            ["active"] = ZernioKeys.Cfg(a, ZernioKeys.StatusKey) != "disconnected",
            ["health"] = a.HealthStatus,
            ["entity_id"] = a.EntityId,
            ["entity_slug"] = level?.Slug,
            ["entity_name"] = level?.Name,
            ["method"] = ZernioNative.Method,
            ["method_label"] = ZernioNative.MethodLabel,
            // Messenger: the account's Facebook Pages — the selected one's conversations reach the inbox
            ["pages"] = a.Channel == ChatBranch.Domain.Channels.Facebook
                ? (pages ?? Enumerable.Empty<SocialPage>()).OrderByDescending(p => p.IsSelected).ThenBy(p => p.Name)
                    .Select(p => new Dictionary<string, object?>
                    {
                        ["id"] = p.PageId,
                        ["name"] = p.Name,
                        ["username"] = p.Username,
                        ["category"] = p.Category,
                        ["avatar"] = p.Avatar.Length > 0 ? p.Avatar : null,
                        ["selected"] = p.IsSelected,
                    }).ToList()
                : null,
        };
    }

    /// <summary>The managed-connection part of a NATIVE inbox payload (GET /api/messaging/accounts, /api/whatsapp/accounts).</summary>
    public static void AddNativePayload(Dictionary<string, object?> outv, ChannelAccount a)
    {
        foreach (var (k, v) in ZernioNative.Payload(a)) outv[k] = v;
        outv["configured"] = ZernioNative.Configured(a);
        outv["webhook_path"] = "";
        outv["access_token_masked"] = "";
    }

    private static string PlatformAlias(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "messenger" or "facebook_messenger" => "facebook",
        "ig" => "instagram",
        var p => p,
    };

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

    private static string? Str(Dictionary<string, JsonElement>? body, string key) =>
        body != null && body.TryGetValue(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            }
            : null;
}
