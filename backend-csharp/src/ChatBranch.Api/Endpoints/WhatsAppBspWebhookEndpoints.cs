using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// WhatsApp through a Business Solution Provider — YCloud and Twilio — one signed webhook
/// per ACCOUNT, following the newer channels' shape (<see cref="MoreChannelWebhookEndpoints"/>):
///
/// <list type="table">
///   <item><term><c>POST /api/messaging/webhook/whatsapp-ycloud/{id}</c></term>
///         <description><c>YCloud-Signature: t=…,s=…</c> — hex HMAC-SHA256(the endpoint's signing
///         secret, "t.body"), 5-minute window. Events: <c>whatsapp.inbound_message.received</c>
///         (text, media by link, location, buttons / interactive replies, contacts, quotes,
///         reactions) and <c>whatsapp.message.updated</c> (sent → delivered → read / failed).</description></item>
///   <item><term><c>POST /api/messaging/webhook/whatsapp-twilio/{id}</c></term>
///         <description><c>X-Twilio-Signature</c> — base64 HMAC-SHA1(the Auth Token, public URL +
///         sorted params), the SMS validator. The incoming-message webhook AND the status
///         callback (<c>MessageStatus</c>) share the URL.</description></item>
/// </list>
///
/// No secret configured ⇒ 503 (nothing is ever accepted unsigned); a bad or missing
/// signature ⇒ 401; an unknown account ⇒ 404. Verified traffic runs the ONE inbound path
/// (<see cref="InboundProcessor.ProcessInbound"/>) with the provider's message id (dedup)
/// and, where the provider sends one, its own timestamp (the 24h window); media download
/// off the ack (<see cref="MediaFetcher"/>); statuses move forward only and an
/// account-level error code moves health (<see cref="MetaWebhook.ApplyStatus"/>). Also here:
/// the anonymous signed media route <c>GET /api/media/public/{token}</c> Twilio (and YCloud's
/// fallback) fetch outbound files from (<see cref="MediaLinks"/>).
/// </summary>
public static class WhatsAppBspWebhookEndpoints
{
    private const int MaxBody = 2 * 1024 * 1024;
    private const string EmptyTwiml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response></Response>";

    /// <summary>The provider clients and the router every WhatsApp seam resolves (Program.cs calls this once).</summary>
    public static IServiceCollection AddWhatsAppProviders(this IServiceCollection services)
    {
        services.AddSingleton<WhatsAppCloudClient>();
        services.AddSingleton<YCloudWhatsAppClient>();
        services.AddSingleton<TwilioWhatsAppClient>();
        services.AddSingleton<IWhatsAppCloudClient>(sp => new WhatsAppClientRouter(
            sp.GetRequiredService<WhatsAppCloudClient>(),
            sp.GetRequiredService<YCloudWhatsAppClient>(),
            sp.GetRequiredService<TwilioWhatsAppClient>()));
        return services;
    }

    public static void MapWhatsAppBspWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/messaging/webhook/whatsapp-ycloud/{id:int}", YCloud).AllowAnonymous();
        app.MapPost("/api/messaging/webhook/whatsapp-twilio/{id:int}", Twilio).AllowAnonymous().DisableAntiforgery();

        // A provider fetching an outbound attachment (Twilio's MediaUrl). The signed,
        // expiring token IS the auth: it names one stored file and nothing else.
        app.MapGet("/api/media/public/{token}", (string token, ScopingService sc, MediaStore media, HttpContext ctx) =>
        {
            var hit = new MediaLinks(sc).Verify(token);
            if (hit is not { } named) return Results.NotFound();
            var (channel, rel) = named;
            var full = media.Resolve(channel, rel);
            if (full == null) return Results.NotFound();
            var mime = MediaStore.MimeFor(full);
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Cache-Control"] = "private, max-age=300";
            return MediaStore.IsInlineSafe(mime)
                ? Results.File(full, mime, enableRangeProcessing: true)
                : Results.File(full, mime, Path.GetFileName(full), enableRangeProcessing: true);
        }).AllowAnonymous();
    }

    // ---- shared ----------------------------------------------------------------------

    private static Task<ChannelAccount?> AccountAsync(AppDbContext db, int id, string provider) =>
        db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == id && a.Channel == Channels.WhatsApp && a.Provider == provider);

    private static IResult Unknown() => Results.Json(new { detail = "Unknown account." }, statusCode: 404);

    private static IResult NoSecret(string what) =>
        Results.Json(new { detail = $"{what} is not configured for this account — inbound messages are refused until it is set." },
            statusCode: 503);

    private static IResult BadSignature() => Results.Json(new { detail = "Bad signature." }, statusCode: 401);

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

    /// <summary>A detached copy with what a background download needs (Twilio: the SID / API key in Config).</summary>
    private static ChannelAccount Snapshot(ChannelAccount a) => new()
    {
        Id = a.Id, Channel = a.Channel, Provider = a.Provider, ExternalId = a.ExternalId, Name = a.Name,
        AccessToken = a.AccessToken, WabaId = a.WabaId, ApiVersion = a.ApiVersion,
        Config = new Dictionary<string, object?>(a.Config ?? new()),
    };

    /// <summary>
    /// One customer message through the shared inbound path. A redelivered provider id has
    /// already run every side effect (AI, flows, notifications) — skipped. Inbound proves the
    /// number works, so it heals.
    /// </summary>
    private static Message? Ingest(AppDbContext db, InboundProcessor processor, ChannelAccount account, string from,
        string text, string? name, string externalId, DateTime? ts, string replyTo)
    {
        if (from.Length == 0 || text.Length == 0) return null;
        if (externalId.Length > 0 && db.Messages.Any(m => m.ExternalId == externalId
                                                          && m.Direction == MessageDirection.In
                                                          && m.Thread!.Channel == Channels.WhatsApp))
            return null;
        var msg = processor.ProcessInbound(Channels.WhatsApp, from, text, name, account.Id, externalId, ts,
            replyToExternalId: replyTo);
        Heal(db, account, "inbound message received");
        return msg;
    }

    private static void Heal(AppDbContext db, ChannelAccount account, string why)
    {
        MetaErrors.MarkHealthy(db, account, why, actor: "webhook");
        account.LastInboundAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    /// <summary>A receipt for one of our messages, matched by the id the send stored (wamid or provider id).</summary>
    private static void ApplyStatus(AppDbContext db, ChannelAccount account, CloudStatus status, string altId)
    {
        if (MetaWebhook.ApplyStatus(db, status, account)) return;
        if (altId.Length > 0 && altId != status.Wamid) MetaWebhook.ApplyStatus(db, status with { Wamid = altId }, account);
    }

    // ---- YCloud ----------------------------------------------------------------------

    private static async Task<IResult> YCloud(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor,
        MediaFetcher fetcher, TimeProvider clock)
    {
        var account = await AccountAsync(db, id, Providers.YCloud);
        if (account == null) return Unknown();
        if (string.IsNullOrEmpty(account.AppSecret)) return NoSecret("The YCloud webhook signing secret");
        var body = await ReadBodyAsync(ctx);
        if (body == null) return Results.StatusCode(413);
        if (!YCloudWhatsAppClient.VerifySignature(account.AppSecret, body,
                ctx.Request.Headers[YCloudWhatsAppClient.SignatureHeader].ToString(), clock.GetUtcNow()))
            return BadSignature();

        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return Results.Ok(new { ok = true }); } // signed garbage: ack, never NACK
        try
        {
            var ev = YCloudWebhook.Parse(root, account.ExternalId);
            if (ev.Message is { } m)
            {
                var msg = Ingest(db, processor, account, m.From, m.Text, m.Name, m.Wamid, m.Timestamp, m.ReplyTo);
                // two-phase: placeholder stored now, the file (YCloud's link) attached off the ack
                if (msg != null && m.MediaId.Length > 0)
                    fetcher.FetchCloudMedia(msg.Id, Snapshot(account), m.MediaId, m.Mime, m.Filename, m.Type);
            }
            else if (ev.Reaction is { } r)
            {
                // a reaction is never a bubble, never the AI, flows, automations or a reopen
                MessageReactions.ApplyInbound(db, Channels.WhatsApp, r.From, r.TargetWamid, r.Emoji, accountId: account.Id);
                Heal(db, account, "inbound reaction received");
            }
            else if (ev.Status is { } st)
            {
                ApplyStatus(db, account, st.Status, st.ProviderId);
            }
        }
        catch { /* YCloud must still get its 200 */ }
        return Results.Ok(new { ok = true });
    }

    // ---- Twilio ----------------------------------------------------------------------

    private static async Task<IResult> Twilio(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor,
        MediaFetcher fetcher, ScopingService sc)
    {
        var account = await AccountAsync(db, id, Providers.Twilio);
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
            var f = TwilioWhatsAppForm.Parse(pairs);
            if (f.IsStatus)
            {
                if (f.OurStatus.Length > 0 && f.MessageSid.Length > 0)
                {
                    var title = f.ErrorCode > 0
                        ? $"Twilio {f.ErrorCode}" + (f.ErrorMessage.Length > 0 ? $": {f.ErrorMessage}" : "")
                        : f.ErrorMessage;
                    ApplyStatus(db, account, new CloudStatus(account.ExternalId, f.MessageSid, f.OurStatus, f.From,
                        DateTime.UtcNow, f.ErrorCode > 0 ? TwilioWhatsAppClient.MetaEquivalent(f.ErrorCode) : null, title), "");
                }
            }
            else if (f.IsReaction)
            {
                // never a bubble; stored on the message it targets when Twilio names one
                if (f.ReplyTo.Length > 0)
                    MessageReactions.ApplyInbound(db, Channels.WhatsApp, f.From, f.ReplyTo, f.Text, accountId: account.Id);
            }
            else
            {
                var msg = Ingest(db, processor, account, f.From, f.Text, f.ProfileName.Length > 0 ? f.ProfileName : null,
                    f.MessageSid, null, f.ReplyTo);
                if (msg != null && f.Media.Count > 0)
                    fetcher.FetchCloudMedia(msg.Id, Snapshot(account), f.Media[0].Ref, "", f.Media[0].FileName,
                        f.Media[0].MediaType);
            }
        }
        catch { /* Twilio must still get its 200 */ }
        return Results.Content(EmptyTwiml, "text/xml");
    }
}
