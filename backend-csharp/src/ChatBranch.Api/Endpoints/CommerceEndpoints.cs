using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The marketplace bridge: buyer chats from Shopee, Lazada and TikTok Shop
/// arriving in the unified inbox, and the shops themselves appearing as inboxes.
///
/// The `e-comerce-seller-mcp` sidecar owns the marketplace grant, verifies the
/// marketplace's own webhook signature and pushes each buyer message here —
/// exactly as <c>backend/baileys_service</c> does for a paired WhatsApp number,
/// and authenticated the same way: a shared secret in <c>X-Commerce-Secret</c>
/// (Setting <c>commerce_secret</c> → env <c>COMMERCE_SECRET</c>). No secret
/// configured means the bridge is CLOSED (503), and a wrong one is a 401 —
/// an open endpoint that writes into an inbox is an injection point.
///
/// The answer is meaningful: the sidecar checks it and retries, so this route
/// returns 200 only when the message was actually stored, 400 when the push can
/// never work (an unknown channel, no conversation key) and 500 when it might
/// work on a retry. A silent 200 over a dropped message is the one thing it
/// must not do.
/// </summary>
public static class CommerceEndpoints
{
    /// <summary>The sidecar client and the four transports (Program.cs calls this once).</summary>
    public static IServiceCollection AddCommerceChannels(this IServiceCollection services)
    {
        services.AddHttpClient(HttpCommerceGateway.ClientName).RemoveAllLoggers();
        services.AddSingleton<ICommerceGateway, HttpCommerceGateway>();
        // one IChannelTransport per marketplace channel, over the one gateway, so
        // OutboundSender → MoreChannelSender → Routing carry them with no edits
        foreach (var channel in CommerceChannels.All)
        {
            var id = channel;
            services.AddSingleton<IChannelTransport>(sp =>
                new CommerceTransport(id, sp.GetRequiredService<ICommerceGateway>()));
        }
        return services;
    }

    public static void MapCommerceEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- the sidecar pushes a buyer message (X-Commerce-Secret IS the auth) ----
        app.MapPost("/api/commerce/inbound", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            ThreadStore threads, InboundProcessor processor) =>
        {
            if (SecretState(ctx, sc) is { } refusal) return refusal;

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch
            {
                // malformed JSON will not get better on a retry
                return Results.Json(new { ok = false, stored = false, detail = "the body is not JSON" },
                    statusCode: 400);
            }

            // the channel, by name or — failing that — from the shop handle's marketplace
            var channel = CommerceChannels.ChannelFor(Str(body, "channel"))
                          ?? CommerceChannels.ChannelFor(CommerceChannels.ParseAccountKey(Str(body, "account")).Marketplace);
            if (channel == null)
                return Results.Json(new
                {
                    ok = false,
                    stored = false,
                    detail = $"unknown marketplace channel '{Str(body, "channel")}' — expected one of " +
                             string.Join(", ", CommerceChannels.All),
                }, statusCode: 400);

            var key = Str(body, "key");
            if (key.Length == 0)
                return Results.Json(new { ok = false, stored = false, detail = "key (the conversation id) is required" },
                    statusCode: 400);

            try
            {
                var account = await EnsureAccountAsync(db, channel, Str(body, "account"), Str(body, "shop_name"));
                if (account == null)
                    return Results.Json(new
                    {
                        ok = false,
                        stored = false,
                        detail = "account must be the shop handle, e.g. \"shopee:123456\"",
                    }, statusCode: 400);

                var text = Text(body);
                var messageId = Str(body, "message_id");
                var at = Timestamp(body);

                // A message the SHOP sent — on the marketplace's own app, or our own
                // send echoing back — is the person's outbound reply, not something
                // to answer: no AI, no flows, no automations, no reopen.
                if (Bool(body, "from_me"))
                {
                    var stored = RecordShopMessage(db, sc, threads, account, key, text, messageId, at);
                    return Results.Ok(new { ok = true, stored = stored != null, direction = "out" });
                }

                var msg = processor.ProcessInbound(channel, key, text, Str(body, "buyer_name"), account.Id,
                    messageId, providerTs: at);

                // inbound traffic proves the sidecar's grant is alive
                if (account.HealthStatus != Health.Healthy)
                {
                    AccountHealth.SetStatus(db, account, Health.Healthy, "buyer message received", actor: "webhook");
                    account.LastInboundAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                else if (account.LastInboundAt == null || account.LastInboundAt < DateTime.UtcNow.AddMinutes(-5))
                {
                    account.LastInboundAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }

                return Results.Ok(new { ok = true, stored = true, direction = "in", message_id = msg.Id });
            }
            catch (Exception)
            {
                // 500 so the sidecar retries — it stored nothing here
                return Results.Json(new { ok = false, stored = false, detail = "could not store the message" },
                    statusCode: 500);
            }
        }).AllowAnonymous();

        // ---- the operator's side ------------------------------------------------

        // Is the seller service attached, and which shops does it hold?
        app.MapGet("/api/commerce/status", async (AppDbContext db, ICommerceGateway gateway) =>
        {
            var accounts = await db.ChannelAccounts.AsNoTracking()
                .Where(a => CommerceChannels.All.Contains(a.Channel))
                .OrderBy(a => a.Channel).ThenBy(a => a.Name)
                .ToListAsync();
            IReadOnlyList<CommerceShop> shops = Array.Empty<CommerceShop>();
            var error = gateway.NotConfiguredReason;
            if (gateway.IsConfigured) (shops, error) = await gateway.ShopsAsync();

            return Results.Ok(new
            {
                attached = gateway.IsConfigured && error.Length == 0,
                configured = gateway.IsConfigured,
                url = gateway.BaseUrl,
                reason = gateway.IsConfigured ? error : gateway.NotConfiguredReason,
                marketplaces = CommerceChannels.All.Select(c => new
                {
                    channel = c,
                    label = CommerceChannels.Label(c),
                    carries_chat = CommerceChannels.CarriesChat(c),
                }),
                inboxes = accounts.Select(a => new
                {
                    id = a.Id,
                    channel = a.Channel,
                    shop = a.ExternalId,
                    name = a.Name,
                    health = a.HealthStatus,
                    entity_id = a.EntityId,
                }),
                shops = shops.Select(s => new
                {
                    channel = s.Channel,
                    shop = s.Account,
                    shop_name = s.ShopName,
                    region = s.Region,
                    status = s.Status,
                    in_inbox = accounts.Any(a => a.Channel == s.Channel && a.ExternalId == s.Account),
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.view");

        // Pull the connected shops from the seller service and make an inbox for
        // each, so a shop appears before its first buyer message arrives.
        app.MapPost("/api/commerce/shops/sync", async (AppDbContext db, ICommerceGateway gateway) =>
        {
            if (!gateway.IsConfigured)
                return Results.Json(new { detail = gateway.NotConfiguredReason }, statusCode: 503);
            var (shops, error) = await gateway.ShopsAsync();
            if (error.Length > 0) return Results.Json(new { detail = error }, statusCode: 502);

            var added = new List<string>();
            var renamed = new List<string>();
            foreach (var shop in shops)
            {
                var existing = await db.ChannelAccounts
                    .FirstOrDefaultAsync(a => a.Channel == shop.Channel && a.ExternalId == shop.Account);
                if (existing == null)
                {
                    db.ChannelAccounts.Add(NewAccount(shop.Channel, shop.Account, shop.ShopName));
                    added.Add(shop.Account);
                    continue;
                }
                var name = AccountName(shop.Channel, shop.Account, shop.ShopName);
                if (shop.ShopName.Length > 0 && existing.Name != name)
                {
                    existing.Name = name;
                    renamed.Add(shop.Account);
                }
            }
            if (added.Count > 0 || renamed.Count > 0) await db.SaveChangesAsync();
            AuditTrail.Write(db, "commerce.shops.sync", gateway.BaseUrl,
                $"{shops.Count} shop(s): {added.Count} added, {renamed.Count} renamed");

            return Results.Ok(new { ok = true, shops = shops.Count, added, renamed });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // Ask the seller service to poll the marketplaces that have no chat webhook
        // (Lazada) — the "check now" of marketplace chat.
        app.MapPost("/api/commerce/poll", async (ICommerceGateway gateway) =>
        {
            if (!gateway.IsConfigured)
                return Results.Json(new { detail = gateway.NotConfiguredReason }, statusCode: 503);
            var result = await gateway.PollChatsAsync();
            return result.Ok
                ? Results.Ok(new { ok = true })
                : Results.Json(new { detail = result.Detail }, statusCode: 502);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>
    /// 503 when no shared secret is configured, 401 when the one sent is wrong,
    /// null when the caller is the sidecar. Never "accept unauthenticated".
    /// </summary>
    private static IResult? SecretState(HttpContext ctx, ScopingService sc)
    {
        var expected = sc.SettingValue("commerce_secret");
        if (string.IsNullOrWhiteSpace(expected))
            expected = Environment.GetEnvironmentVariable("COMMERCE_SECRET") ?? "";
        expected = expected.Trim();
        if (expected.Length == 0)
            return Results.Json(new
            {
                ok = false,
                stored = false,
                detail = "The marketplace bridge is closed: set commerce_secret (or COMMERCE_SECRET) to the same " +
                         "value as CHATBRANCH_SECRET in the seller service.",
            }, statusCode: 503);

        var sent = ctx.Request.Headers[HttpCommerceGateway.SecretHeader].ToString().Trim();
        if (sent.Length == 0 || !FixedEquals(sent, expected))
            return Results.Json(new { ok = false, stored = false, detail = "bad or missing X-Commerce-Secret" },
                statusCode: 401);
        return null;
    }

    private static bool FixedEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// The shop's inbox, created on first contact. A marketplace grant cannot be
    /// typed into ChatBranch, so a shop the sidecar knows about has to be able to
    /// appear by itself — otherwise its buyer messages would have nowhere to go.
    /// It starts organisation-wide and unassigned; the operator then gives it an
    /// entity, a team and an AI default like any other inbox.
    /// </summary>
    private static async Task<ChannelAccount?> EnsureAccountAsync(AppDbContext db, string channel, string accountKey,
        string shopName)
    {
        var (marketplace, shopId) = CommerceChannels.ParseAccountKey(accountKey);
        if (marketplace.Length == 0 || shopId.Length == 0) return null;
        // trust the channel we resolved, and normalise the handle to its spelling
        var key = CommerceChannels.AccountKey(channel, shopId);

        var existing = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Channel == channel && a.ExternalId == key);
        if (existing != null)
        {
            if (shopName.Length > 0)
            {
                var name = AccountName(channel, key, shopName);
                if (existing.Name != name)
                {
                    existing.Name = name;
                    await db.SaveChangesAsync();
                }
            }
            return existing;
        }

        var account = NewAccount(channel, key, shopName);
        db.ChannelAccounts.Add(account);
        AuditTrail.Add(db, "account.create", $"{channel}:{account.Name}",
            "created from the seller service's first buyer message", source: "webhook");
        await db.SaveChangesAsync();
        return account;
    }

    private static ChannelAccount NewAccount(string channel, string accountKey, string shopName) => new()
    {
        Channel = channel,
        ExternalId = accountKey,
        Name = AccountName(channel, accountKey, shopName),
        CreatedAt = DateTime.UtcNow,
        HealthStatus = Health.Healthy,
        Config = new Dictionary<string, object?>(),
    };

    private static string AccountName(string channel, string accountKey, string shopName)
    {
        var name = shopName.Trim();
        if (name.Length == 0) name = CommerceChannels.ParseAccountKey(accountKey).ShopId;
        var full = $"{CommerceChannels.Label(channel)} · {name}";
        return full.Length > 120 ? full[..120] : full;
    }

    /// <summary>
    /// A message the shop sent outside ChatBranch (in Seller Centre, or our own
    /// send coming back on a webhook): the person's own outbound reply. Deduped on
    /// the marketplace's id so our sends are never stored twice, and it clears an
    /// open handover the way a reply from the phone does.
    /// </summary>
    private static Message? RecordShopMessage(AppDbContext db, ScopingService sc, ThreadStore threads,
        ChannelAccount account, string key, string text, string messageId, DateTime? at)
    {
        try
        {
            if (messageId.Length > 0)
            {
                var known = db.Messages.Include(m => m.Thread)
                    .Where(m => m.ExternalId == messageId && m.Thread!.Channel == account.Channel
                                && m.Thread.Key == key)
                    .OrderByDescending(m => m.Id)
                    .FirstOrDefault();
                if (known != null) return known; // ChatBranch sent it, or this is a redelivery
            }
            var thread = threads.GetOrCreateThread(account.Channel, key, null, account.Id);
            var msg = threads.AppendMessage(thread, MessageDirection.Out, MessageSource.ExternalClient, text,
                status: "sent", externalId: messageId, accountId: account.Id, providerTs: at);
            ChatBranch.Data.Ai.Handover.Clear(db, thread, ChatBranch.Data.Ai.Handover.Agent("seller-centre"),
                "The shop replied in the marketplace's own app.");
            return msg;
        }
        catch
        {
            return null; // a push never fails on bookkeeping
        }
    }

    /// <summary>
    /// What the bubble shows. Marketplace media is LINKED, not downloaded: the
    /// sidecar hands over the marketplace's own URL (often signed and short-lived),
    /// so the operator gets the placeholder and the link rather than a file that
    /// may already have expired.
    /// </summary>
    private static string Text(JsonElement body)
    {
        var text = Str(body, "text");
        var url = Str(body, "media_url");
        if (url.Length == 0) return text;
        if (text.Length == 0) return url;
        return text.Contains(url, StringComparison.Ordinal) ? text : $"{text}\n{url}";
    }

    private static DateTime? Timestamp(JsonElement body)
    {
        var raw = Str(body, "timestamp");
        if (raw.Length == 0) return null;
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                             | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
    }

    private static string Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.ToString(),
                _ => "",
            }
            : "";

    private static bool Bool(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
}
