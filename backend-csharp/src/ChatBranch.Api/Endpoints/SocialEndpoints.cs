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
///   <item><c>/api/social/*</c> — accounts (<c>social.view</c> / <c>social.manage</c>), posts
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

    private static IResult NotConfigured(IZernioClient zernio) =>
        Results.Json(new { code = NotConfiguredCode, detail = zernio.NotConfiguredReason }, statusCode: 409);

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
                try { await accounts.SyncAsync(); } catch { /* the mirror still answers */ }
            }
            var (filter, all) = await ListScope(db, sc, me, entity);
            var q = db.ChannelAccounts.AsNoTracking().Where(a => a.Channel == ChatBranch.Domain.Channels.Social);
            var rows = (await q.OrderBy(a => a.Name).ToListAsync())
                .Where(a => SocialChannels.Cfg(a, SocialChannels.StatusKey) != "removed")
                .Where(a => all || filter!.Contains(a.EntityId)).ToList();
            return Results.Ok(new { accounts = rows.Select(AccountRow), configured = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("social.view", "channels.view");

        app.MapPost("/api/social/connect", async (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, SocialAccounts accounts, HttpContext ctx) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var platform = PlatformAlias(Str(body, "platform"));
            if (!SocialChannels.ConnectPlatforms.Contains(platform))
                return Results.Json(new
                {
                    detail = $"platform must be one of {string.Join(", ", SocialChannels.ConnectPlatforms.OrderBy(p => p))}.",
                }, statusCode: 400);
            var (entityId, err) = await ActScope(db, sc, me, Str(body, "entity") ?? ctx.Request.Query["entity"]);
            if (err != null) return err;
            var (profileId, perr) = await accounts.EnsureProfileAsync(entityId);
            if (profileId.Length == 0)
                return Results.Json(new { detail = perr, code = "zernio_error" }, statusCode: 502);
            var redirect = Dashboard(ctx, sc) + "/channels?zernio=connected";
            var r = await zernio.ConnectUrlAsync(platform, profileId, redirect);
            if (!r.Ok) return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
            var url = r.Str("authUrl");
            if (url.Length == 0) return Results.Json(new { detail = "Zernio returned no connect URL." }, statusCode: 502);
            AuditTrail.Write(db, "social.connect", platform, $"profile {profileId}", entityId: entityId);
            return Results.Ok(new { url, platform, profile_id = profileId });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.manage");

        app.MapDelete("/api/social/accounts/{id}", async (string id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IZernioClient zernio) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var row = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Channel == ChatBranch.Domain.Channels.Social
                                                                        && a.ExternalId == id)
                      ?? (int.TryParse(id, out var pk)
                          ? await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == pk
                                                                              && a.Channel == ChatBranch.Domain.Channels.Social)
                          : null);
            if (row == null) return Results.Json(new { detail = "Social account not found." }, statusCode: 404);
            if (ShellScope.ObjectScopeError(db, sc, me, row.EntityId) is { } outside) return outside;
            var r = await zernio.DeleteAccountAsync(row.ExternalId);
            if (!r.Ok && r.Status != 404)
                return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
            var label = $"social:{row.Name}";
            // its conversations stay (back in sandbox), like any deleted inbox
            await InboxSettings.DetachAccountAsync(db, row.Id);
            db.ChannelAccounts.Remove(row);
            await db.SaveChangesAsync();
            AuditTrail.Write(db, "account.delete", label, "disconnected in Zernio", entityId: row.EntityId);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("social.manage");
    }

    /// <summary>The accounts list row.</summary>
    private static object AccountRow(ChannelAccount a)
    {
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
            ["can_publish"] = publish,
            ["can_comments"] = comments,
            ["can_dm"] = dm,
            ["can_reviews"] = reviews,
        };
    }

    /// <summary>The social part of an inbox account payload (GET /api/messaging/accounts).</summary>
    public static void AddAccountPayload(Dictionary<string, object?> outv, ChannelAccount a)
    {
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
            var ids = StrList(body, "account_ids").Distinct().ToList();
            var accounts = db.ChannelAccounts.AsNoTracking()
                .Where(a => a.Channel == ChatBranch.Domain.Channels.Social && ids.Contains(a.ExternalId)).ToList();
            var unknown = ids.Where(i => accounts.All(a => a.ExternalId != i)).ToList();
            if (unknown.Count > 0)
                return Results.Json(new { detail = $"Unknown social account(s): {string.Join(", ", unknown)}." }, statusCode: 400);
            // an entity posts through its OWN profile's accounts only
            if (accounts.Any(a => a.EntityId != post.EntityId))
                return Results.Json(new { detail = "Every account must belong to the same entity as the post." }, statusCode: 400);
            var cannot = accounts.Where(a => !SocialChannels.PublishPlatforms.Contains(SocialChannels.Platform(a))).ToList();
            if (cannot.Count > 0)
                return Results.Json(new { detail = $"These accounts cannot publish posts: {string.Join(", ", cannot.Select(a => a.Name))}." },
                    statusCode: 400);
            post.AccountIds = ids;
            post.Platforms = ids.Select(i => SocialChannels.Platform(accounts.First(a => a.ExternalId == i))).ToList();
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
    private static async Task PushAsync(AppDbContext db, IZernioClient zernio, SocialPost post, bool publishNow)
    {
        var accounts = db.ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == ChatBranch.Domain.Channels.Social && post.AccountIds.Contains(a.ExternalId)).ToList();
        var body = new Dictionary<string, object?>
        {
            ["content"] = post.Content,
            ["mediaItems"] = post.Media.Select(m => new Dictionary<string, object?>
            {
                ["url"] = m.GetValueOrDefault("url")?.ToString(), ["type"] = m.GetValueOrDefault("type")?.ToString(),
            }).ToList(),
            ["platforms"] = post.AccountIds.Select(id => new Dictionary<string, object?>
            {
                ["platform"] = SocialChannels.Platform(accounts.First(a => a.ExternalId == id)),
                ["accountId"] = id,
            }).ToList(),
            ["metadata"] = new Dictionary<string, object?> { [SocialPosts.MetadataKey] = post.Id.ToString() },
        };
        if (post.Status == SocialPosts.Draft) body["isDraft"] = true;
        else if (publishNow) body["publishNow"] = true;
        else
        {
            body["scheduledFor"] = DateTime.SpecifyKind(post.ScheduledAt!.Value, DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            if (post.ZernioPostId.Length > 0) body["isDraft"] = false;
        }
        if (post.Timezone.Length > 0) body["timezone"] = post.Timezone;

        var r = post.ZernioPostId.Length > 0
            ? await zernio.UpdatePostAsync(post.ZernioPostId, body)
            : await zernio.CreatePostAsync(body);
        if (r.At("post").ValueKind == JsonValueKind.Object) SocialPosts.Apply(post, r.At("post"));
        if (!r.Ok)
        {
            post.Status = SocialPosts.Failed;
            post.Error = r.Error.Length > 500 ? r.Error[..500] : r.Error;
        }
        post.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

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
