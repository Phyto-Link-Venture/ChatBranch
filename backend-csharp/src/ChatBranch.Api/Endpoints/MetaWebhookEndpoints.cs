using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Integrations;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The Meta webhook rules shared by WhatsApp Cloud (<c>/api/whatsapp/webhook</c>)
/// and Messenger / Instagram (<c>/api/messaging/webhook</c>): one verify token,
/// one signature check.
/// </summary>
internal static class MetaWebhookAuth
{
    /// <summary>The token shipped in the repo/docs — never accepted by a live verify.</summary>
    public const string ShippedVerifyToken = "chatbranch-verify";

    /// <summary>Setting <c>whatsapp_verify_token</c> → legacy <c>verify_token</c> → env.</summary>
    public static string ExpectedVerifyToken(ScopingService sc)
    {
        var token = sc.SettingValue("whatsapp_verify_token");
        if (string.IsNullOrEmpty(token)) token = sc.SettingValue("verify_token");
        if (string.IsNullOrEmpty(token)) token = Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN") ?? "";
        return token == ShippedVerifyToken ? "" : token;
    }

    /// <summary>Meta's <c>hub.challenge</c> handshake (GET).</summary>
    public static IResult Verify(HttpContext ctx, ScopingService sc)
    {
        var mode = ctx.Request.Query["hub.mode"].ToString();
        var token = ctx.Request.Query["hub.verify_token"].ToString();
        var challenge = ctx.Request.Query["hub.challenge"].ToString();
        var expected = ExpectedVerifyToken(sc);
        if (mode == "subscribe" && expected != "" && FixedEquals(token, expected))
            return Results.Text(challenge);
        return Results.StatusCode(403);
    }

    private static bool FixedEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));

    /// <summary>
    /// Every Meta app secret we know: the WhatsApp / Messenger / Instagram
    /// accounts' own, Setting <c>meta_app_secret</c>, env <c>META_APP_SECRET</c> /
    /// <c>WHATSAPP_APP_SECRET</c>. One Meta app usually serves all three
    /// products, so a page whose own row has no secret still verifies against
    /// the app's. All of them are ours — a signature valid under any is Meta's.
    /// </summary>
    public static List<string> KnownSecrets(AppDbContext db, ScopingService sc)
    {
        var secrets = db.ChannelAccounts.AsNoTracking()
            .Where(a => a.AppSecret != "" && (a.Channel == Channels.WhatsApp || a.Channel == Channels.Facebook
                                              || a.Channel == Channels.Instagram))
            .Select(a => a.AppSecret).ToList();
        secrets.Add(sc.SettingValue("meta_app_secret"));
        secrets.Add(Environment.GetEnvironmentVariable("META_APP_SECRET") ?? "");
        secrets.Add(Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET") ?? "");
        return secrets.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList();
    }

    public static bool Signed(IEnumerable<string> secrets, byte[] raw, string header) =>
        header.Length > 0 && secrets.Any(s => WebhookVerify.Meta(s, raw, header));

    /// <summary>The read-only env-default Cloud account (WHATSAPP_* in .env), detached, or null.</summary>
    public static ChannelAccount? EnvCloudAccount()
    {
        var token = Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN") ?? "";
        var pnid = Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID") ?? "";
        if (token == "" || pnid == "") return null;
        return new ChannelAccount
        {
            Channel = Channels.WhatsApp, Provider = Providers.Cloud, Name = "env-default",
            ExternalId = pnid, AccessToken = token,
            WabaId = Environment.GetEnvironmentVariable("WHATSAPP_WABA_ID") ?? "",
            AppSecret = Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET") ?? "",
            ApiVersion = Environment.GetEnvironmentVariable("WHATSAPP_API_VERSION") is { Length: > 0 } v ? v : "v23.0",
        };
    }

    /// <summary>A detached copy carrying just what a background download needs.</summary>
    public static ChannelAccount Snapshot(ChannelAccount a) => new()
    {
        Id = a.Id, Channel = a.Channel, Provider = a.Provider, ExternalId = a.ExternalId,
        AccessToken = a.AccessToken, ApiVersion = a.ApiVersion, WabaId = a.WabaId, Name = a.Name,
    };

    public static async Task<byte[]> ReadBody(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// Messenger + Instagram intake (port of <c>messaging/views.webhook</c>):
///
///   * <c>GET /api/messaging/webhook</c> — the hub.challenge handshake, same verify
///     token as WhatsApp (Settings → App &amp; provider keys);
///   * <c>POST /api/messaging/webhook</c> — objects <c>page</c> / <c>instagram</c>.
///     <b>X-Hub-Signature-256 is REQUIRED</b>: verified against the known Meta app
///     secrets (<see cref="MetaWebhookAuth.KnownSecrets"/>); no secret configured
///     ⇒ 401 — this closes the old "Messenger/IG webhook has no signature check"
///     gap. Each event routes by the entry id (page / Instagram account id) to its
///     ChannelAccount and goes through <see cref="InboundProcessor"/> (dedup by
///     mid, contact link, flows, team routing, automations, notifications, the
///     coverage-gated AI). Attachments download from Meta's CDN off the ack
///     (one media per message row); delivery / read receipts move our outbound
///     bubbles' ticks.
/// </summary>
public static class MetaWebhookEndpoints
{
    public static void MapMetaWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/messaging/webhook", (HttpContext ctx, ScopingService sc) =>
            MetaWebhookAuth.Verify(ctx, sc)).AllowAnonymous();

        app.MapPost("/api/messaging/webhook", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            InboundProcessor processor, ThreadStore store, MediaFetcher fetcher, IMetaMessagingClient meta,
            ChatBranch.Data.Contacts.ContactInviteService invites) =>
        {
            var raw = await MetaWebhookAuth.ReadBody(ctx);
            var secrets = MetaWebhookAuth.KnownSecrets(db, sc);
            if (secrets.Count == 0)
                return Results.Json(new
                {
                    detail = "No Meta app secret is configured — set the page's app secret (or Setting "
                             + "meta_app_secret) so Messenger/Instagram webhooks can be verified.",
                }, statusCode: 401);
            if (!MetaWebhookAuth.Signed(secrets, raw, ctx.Request.Headers["X-Hub-Signature-256"].ToString()))
                return Results.StatusCode(401);

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { return Results.Ok(new { ok = true }); } // signed garbage: ack, never NACK

            // invite links: an m.me ?ref= referral binds the chat to its contact
            // BEFORE the message events below run (messaging_referrals, or the
            // referral riding on a postback / message)
            ChatBranch.Data.Contacts.InviteReferrals.Bind(root, db, store, invites);

            foreach (var ev in MetaWebhook.MessagingEvents(root))
            {
                try { await Handle(ev, db, processor, store, fetcher, meta); }
                catch { /* one bad event never fails the batch */ }
            }
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();
    }

    private static async Task Handle(MessagingEvent ev, AppDbContext db, InboundProcessor processor,
        ThreadStore store, MediaFetcher fetcher, IMetaMessagingClient meta)
    {
        if (ev.Kind != "message")
        {
            MetaWebhook.ApplyMessagingReceipt(db, ev);
            return;
        }
        // redelivery: Meta retries until it sees a 200 — the first copy already ran
        // every side effect (AI, flows, notifications), so a repeat does nothing
        if (ev.Mid.Length > 0 && await db.Messages.AnyAsync(m => m.ExternalId == ev.Mid
                && m.Direction == MessageDirection.In && m.Thread!.Channel == ev.Channel))
            return;

        var account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Channel == ev.Channel
                                                                        && a.ExternalId == ev.PageId);
        var isNew = !await db.Threads.AnyAsync(t => t.Channel == ev.Channel && t.Key == ev.SenderId);
        var msg = processor.ProcessInbound(ev.Channel, ev.SenderId, ev.Text, null, account?.Id, ev.Mid, ev.Timestamp,
            replyToExternalId: ev.ReplyTo);

        // attachments: the first rides on the message itself, every further one is
        // its own row (one media per message), each downloaded off the ack
        for (var i = 0; i < ev.Attachments.Count; i++)
        {
            var att = ev.Attachments[i];
            if (att.Url.Length == 0) continue;
            var target = msg;
            if (i > 0)
            {
                var thread = store.GetThread(ev.Channel, ev.SenderId);
                if (thread == null) break;
                target = store.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
                    $"[{MetaWebhook.AttachmentKind(att.Type)}]", status: "received",
                    externalId: ev.Mid.Length > 0 ? $"{ev.Mid}#{i + 1}" : "", accountId: account?.Id,
                    providerTs: ev.Timestamp);
            }
            fetcher.FetchUrl(target.Id, att.Url, att.Type);
        }

        if (account != null)
        {
            // inbound traffic proves the page connection is alive
            MetaErrors.MarkHealthy(db, account, "inbound message received", actor: "webhook");
            account.LastInboundAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            if (isNew && meta.IsConfigured(account)) fetcher.FetchProfileName(msg.ThreadId, account.Id);
        }
    }
}
