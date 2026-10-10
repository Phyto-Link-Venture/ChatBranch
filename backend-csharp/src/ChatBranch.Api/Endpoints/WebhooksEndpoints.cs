using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Integrations;
using ChatBranch.Data.Stores;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Live inbound webhooks (open routes — the signature IS the auth). Meta Cloud
/// (GET verify + signed POST: messages incl. media, delivery statuses),
/// Telegram, the Baileys gateway pushes, and the email IMAP sync nudge. Each
/// routes inbound through <see cref="InboundProcessor"/> (store + dedup + flow
/// resume + coverage-gated AI reply). The Cloud signature is REQUIRED once any
/// Cloud account is configured; sandbox (nothing configured) accepts unsigned.
/// Messenger / Instagram live in <see cref="MetaWebhookEndpoints"/>.
/// </summary>
public static class WebhooksEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Meta Cloud: GET verification -------------------------------------
        // Same token + rules as the Messenger/Instagram webhook (MetaWebhookAuth):
        // Setting whatsapp_verify_token → verify_token → env; the shipped default
        // is never accepted.
        app.MapGet("/api/whatsapp/webhook", (HttpContext ctx, ScopingService sc) =>
            MetaWebhookAuth.Verify(ctx, sc)).AllowAnonymous();

        // ---- Meta Cloud: POST inbound + delivery statuses ----------------------
        // Signature: X-Hub-Signature-256 is REQUIRED once any Cloud account is
        // configured (a DB row with token + phone_number_id, or the env-default
        // WHATSAPP_* pair) or the addressed account carries an app_secret; it is
        // verified against the known Meta app secrets. A live setup without any
        // app secret answers 401 — set the account's app secret. With nothing
        // configured (sandbox) unsigned posts are still accepted.
        app.MapPost("/api/whatsapp/webhook", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            InboundProcessor processor, MediaFetcher fetcher) =>
        {
            var raw = await ReadBody(ctx);
            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { return Results.Ok(new { ok = true }); } // never NACK on garbage (no side effects)

            var pnids = MetaWebhook.PhoneNumberIds(root);
            // Meta's own Cloud API numbers only — YCloud / Twilio numbers have their own
            // signed webhooks (WhatsAppBspWebhookEndpoints) and never route through here
            var accounts = await db.ChannelAccounts
                .Where(a => a.Channel == "whatsapp" && (a.Provider == "cloud" || a.Provider == "")
                            && pnids.Contains(a.ExternalId))
                .ToListAsync();
            var env = MetaWebhookAuth.EnvCloudAccount();
            var required = env != null || accounts.Any(a => a.AppSecret != "")
                           || await db.ChannelAccounts.AnyAsync(a => a.Channel == "whatsapp"
                                                                     && (a.Provider == "cloud" || a.Provider == "")
                                                                     && a.AccessToken != "" && a.ExternalId != "");
            var signature = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
            if (required || signature.Length > 0)
            {
                var secrets = MetaWebhookAuth.KnownSecrets(db, sc);
                if (secrets.Count == 0)
                {
                    if (required)
                        return Results.Json(new
                        {
                            detail = "WhatsApp webhook signature required — set the account's app secret.",
                        }, statusCode: 401);
                }
                else if (!MetaWebhookAuth.Signed(secrets, raw, signature))
                    return Results.StatusCode(401);
            }

            ChatBranch.Domain.ChannelAccount? AccountFor(string pnid) =>
                accounts.FirstOrDefault(a => a.ExternalId == pnid);

            foreach (var m in MetaWebhook.CloudMessages(root))
            {
                try
                {
                    // a redelivered wamid already ran every side effect (AI, flows,
                    // notifications) the first time — don't run them twice
                    if (m.Wamid.Length > 0 && await db.Messages.AnyAsync(x => x.ExternalId == m.Wamid
                            && x.Direction == "in" && x.Thread!.Channel == "whatsapp"))
                        continue;
                    var account = AccountFor(m.PhoneNumberId);
                    // Meta's own timestamp, not arrival time: the 24h window is
                    // computed from it (webhooks can arrive hours late)
                    var msg = processor.ProcessInbound("whatsapp", m.From, m.Text, m.Name, account?.Id,
                        m.Wamid, m.Timestamp, replyToExternalId: m.ReplyTo);
                    if (m.MediaId.Length > 0)
                    {
                        // two-phase: placeholder stored now, the file attached off the ack
                        var source = account ?? (env != null && env.ExternalId == m.PhoneNumberId ? env : null);
                        if (source != null && source.AccessToken != "")
                            fetcher.FetchCloudMedia(msg.Id, MetaWebhookAuth.Snapshot(source), m.MediaId, m.Mime,
                                m.Filename, m.Type);
                    }
                    if (account != null)
                    {
                        // inbound traffic proves the number is alive
                        MetaErrors.MarkHealthy(db, account, "inbound message received", actor: "webhook");
                        account.LastInboundAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    }
                }
                catch { /* one bad message never fails the batch */ }
            }

            // reactions: stored on the message they target — never a bubble, never the
            // AI, flows, automations or a reopen (MessageReactions)
            foreach (var r in MetaWebhook.CloudReactions(root))
            {
                try { MessageReactions.ApplyInbound(db, "whatsapp", r.From, r.TargetWamid, r.Emoji); }
                catch { }
            }

            // delivery receipts: sent → delivered → read, or failed (+ 131047 window
            // close; account-level codes → health, per-recipient codes never)
            foreach (var st in MetaWebhook.CloudStatuses(root))
            {
                try { MetaWebhook.ApplyStatus(db, st, AccountFor(st.PhoneNumberId)); }
                catch { }
            }

            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        // Telegram (/api/messaging/webhook/telegram/{token}) lives in
        // TelegramWebhookEndpoints; the email IMAP sync in MessagingTransportEndpoints.

        // ---- Baileys gateway pushes (X-Baileys-Secret IS the auth) -----------
        // The Node sidecar (backend/baileys_service) pushes here for a paired
        // personal number: inbound text, connection lifecycle, and delivery
        // status. Same secret it was started with (Setting baileys_secret →
        // env BAILEYS_SECRET → "baileys-local-dev"). `account_id` is the
        // gateway-side key = ChannelAccount.ExternalId (see EnsureBaileysKey).
        app.MapPost("/api/whatsapp/baileys/inbound", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, InboundProcessor processor) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true }); } // never NACK on garbage
            var waId = Str(body, "wa_id");
            var text = Str(body, "text");
            if (string.IsNullOrEmpty(waId)) return Results.Ok(new { ok = true });
            // a reaction ({reaction: {msg_id, emoji}}) lands on the message it targets —
            // no bubble, no AI, no flows, no reopen (MessageReactions)
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reaction", out var reaction)
                && reaction.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    MessageReactions.ApplyInbound(db, "whatsapp", waId, Str(reaction, "msg_id") ?? "",
                        Str(reaction, "emoji") ?? "");
                }
                catch { }
                return Results.Ok(new { ok = true });
            }
            var account = await BaileysAccountByKey(db, Str(body, "account_id"));
            // Baileys pushes only text here (media arrives on /media afterwards);
            // an empty text is a media-only message — still record the touch.
            processor.ProcessInbound("whatsapp", waId, text ?? "", Str(body, "name"),
                account?.Id, Str(body, "msg_id") ?? "", replyToExternalId: Str(body, "reply_to") ?? "");
            if (account != null && account.HealthStatus != Domain.Health.Healthy)
            {
                // Inbound traffic proves the session is alive — heal like Django does.
                ChatBranch.Data.Channels.AccountHealth.SetStatus(db, account, Domain.Health.Healthy,
                    "inbound message received", actor: "webhook");
                account.LastInboundAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        app.MapPost("/api/whatsapp/baileys/status-event", async (HttpContext ctx, AppDbContext db,
            ScopingService sc) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true }); }
            var account = await BaileysAccountByKey(db, Str(body, "account_id"));
            if (account == null) return Results.Ok(new { ok = true });
            var evt = (Str(body, "event") ?? "").ToLowerInvariant();
            var reason = (Str(body, "reason") ?? "").ToLowerInvariant();
            // Django health mapping: connected → healthy, disconnected → degraded,
            // logged_out / forbidden(403) → banned.
            var next = evt switch
            {
                "connected" => Domain.Health.Healthy,
                "logged_out" => Domain.Health.Banned,
                "disconnected" when reason.Contains("forbidden") || reason.Contains("logged") => Domain.Health.Banned,
                "disconnected" => Domain.Health.Degraded,
                _ => account.HealthStatus,
            };
            // The gateway is the only source of the paired number — learn it.
            var number = Str(body, "number");
            if (!string.IsNullOrEmpty(number) && string.IsNullOrEmpty(account.Name)) account.Name = number;
            // One audit row per real transition (Analytics → health events).
            ChatBranch.Data.Channels.AccountHealth.SetStatus(db, account, next,
                string.IsNullOrEmpty(reason) ? evt : $"{evt}: {reason}", actor: "webhook");
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        app.MapPost("/api/whatsapp/baileys/message-status", async (HttpContext ctx, AppDbContext db,
            ScopingService sc) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true }); }
            var msgId = Str(body, "msg_id");
            var status = Str(body, "status");
            if (string.IsNullOrEmpty(msgId) || string.IsNullOrEmpty(status)) return Results.Ok(new { ok = true });
            var msg = await db.Messages.Include(m => m.Thread).FirstOrDefaultAsync(m => m.ExternalId == msgId);
            if (msg != null)
            {
                msg.Status = status;
                if (msg.Thread != null) msg.Thread.UpdatedAt = DateTime.UtcNow; // bump so /stream refreshes
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        // Inbound media, phase two: the gateway (the only holder of the Signal keys)
        // decrypts the attachment and posts it here AFTER pushing the message's
        // text placeholder to /inbound — multipart wa_id, msg_id, media_type, file.
        // The file lands in data/whatsapp/media/{wa_id}/ and attaches to the
        // message with that msg_id (20 MB cap). If the text push never landed, the
        // row is created through the normal inbound path first.
        app.MapPost("/api/whatsapp/baileys/media", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, InboundProcessor processor, MediaStore media) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            if (!ctx.Request.HasFormContentType) return Results.Ok(new { ok = true, stored = false });
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(); }
            catch { return Results.Ok(new { ok = true, stored = false }); }
            var waId = new string(form["wa_id"].ToString().Where(char.IsDigit).ToArray());
            var msgId = form["msg_id"].ToString().Trim();
            var declared = form["media_type"].ToString().Trim().ToLowerInvariant();
            var file = form.Files["file"];
            if (waId.Length == 0 || file == null || file.Length == 0)
                return Results.Ok(new { ok = true, stored = false });
            if (file.Length > MediaStore.MaxBytes)
                return Results.Ok(new { ok = true, stored = false, detail = "file too large" });

            var type = declared is "image" or "video" or "audio" or "document"
                ? declared : MediaStore.TypeForMime(file.ContentType);
            var thread = await db.Threads.FirstOrDefaultAsync(t => t.Channel == "whatsapp" && t.Key == waId);
            var msg = thread != null && msgId.Length > 0
                ? await db.Messages.FirstOrDefaultAsync(m => m.ThreadId == thread.Id && m.ExternalId == msgId)
                : null;
            if (msg == null)
            {
                msg = processor.ProcessInbound("whatsapp", waId, $"[{type}]", null, thread?.AccountId, msgId);
                thread = await db.Threads.FirstAsync(t => t.Id == msg.ThreadId);
            }
            if (msg.MediaPath.Length > 0) return Results.Ok(new { ok = true, stored = true }); // redelivery

            await using var stream = file.OpenReadStream();
            var rel = await media.SaveAsync("whatsapp", waId, stream,
                MediaStore.ExtFor(file.ContentType, file.FileName));
            if (rel == null) return Results.Ok(new { ok = true, stored = false, detail = "file too large" });
            msg.MediaType = type;
            msg.MediaPath = rel;
            thread!.UpdatedAt = DateTime.UtcNow; // the inbox stream re-renders the bubble
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, stored = true });
        }).AllowAnonymous().DisableAntiforgery();

        // Call lifecycle for a WhatsApp call the API started (call/start):
        // ringing → connected → ended, each carrying the LiveKit room. The thread
        // is found by that room (falling back to the callee's wa_id); an event
        // for a call nobody tracks is acked and ignored.
        app.MapPost("/api/whatsapp/baileys/call-event", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, ThreadStore store, CallRecordings recordings,
            ChatBranch.Data.Sidecars.VoiceAgentBridge voice) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true }); }
            var room = Str(body, "room");
            var waId = Str(body, "wa_id");
            var thread = !string.IsNullOrEmpty(room)
                ? await db.Threads.FirstOrDefaultAsync(t => t.CallRoom == room)
                : null;
            thread ??= string.IsNullOrEmpty(waId)
                ? null
                : await db.Threads.FirstOrDefaultAsync(t => t.Channel == "whatsapp" && t.Key == waId);
            if (thread == null) return Results.Ok(new { ok = true });

            var live = thread.CallStatus is "connecting" or "ringing" or "in_call";
            switch ((Str(body, "event") ?? "").ToLowerInvariant())
            {
                case "ringing" when live:
                    thread.CallStatus = "ringing";
                    break;
                case "connected" when live:
                    thread.CallStatus = "in_call";
                    break;
                case "ended" when live:
                    // the recording's paired transcript ({wa_id}-{ms}.json) — the
                    // /chats "Calls" section and the WAV route read it
                    SaveCallTranscript(db, recordings, voice, thread, body);
                    thread.CallStatus = "ended";
                    thread.CallId = null;
                    var reason = Str(body, "reason");
                    store.AppendMessage(thread, Domain.MessageDirection.Out, Domain.MessageSource.System,
                        string.IsNullOrEmpty(reason) ? "WhatsApp call ended." : $"WhatsApp call ended ({reason}).",
                        status: "sent", accountId: thread.AccountId);
                    break;
                default:
                    return Results.Ok(new { ok = true }); // stale or unknown event
            }
            thread.UpdatedAt = DateTime.UtcNow; // the /chats stream refreshes on this
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();
    }

    /// <summary>
    /// wa_calls.save_call_transcript — on the gateway's <c>ended</c> push, write
    /// the call's transcript + provenance next to its recording. Best-effort.
    /// </summary>
    private static void SaveCallTranscript(AppDbContext db, CallRecordings recordings,
        ChatBranch.Data.Sidecars.VoiceAgentBridge voice, Domain.Thread thread, JsonElement body)
    {
        try
        {
            long? recStarted = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("rec_started_at", out var rs) && rs.ValueKind == JsonValueKind.Number
                && rs.TryGetInt64(out var ms) ? ms : null;
            // who placed it: the operator's call.start audit row, else a queued (flow) call
            var starter = db.AuditLogs.AsNoTracking()
                .Where(a => a.Action == "call.start" && a.Target == $"whatsapp:{thread.Key}")
                .OrderByDescending(a => a.Id).Select(a => a.Actor).FirstOrDefault();
            var account = thread.AccountId is int aid
                ? db.ChannelAccounts.AsNoTracking().Where(a => a.Id == aid).Select(a => a.Name).FirstOrDefault() ?? ""
                : "";
            recordings.SaveEnded(thread.Key, Str(body, "recording"), thread.CallRoom, recStarted,
                Str(body, "reason") ?? "", string.IsNullOrEmpty(starter) ? "flow" : starter, account, voice);
        }
        catch { /* the call state still resets */ }
    }

    // ---- Baileys helpers ---------------------------------------------------
    private static bool BaileysSecretOk(HttpContext ctx, ScopingService sc)
    {
        var expected = sc.SettingValue("baileys_secret");
        if (string.IsNullOrWhiteSpace(expected))
            expected = Environment.GetEnvironmentVariable("BAILEYS_SECRET") ?? "baileys-local-dev";
        var got = ctx.Request.Headers["X-Baileys-Secret"].ToString();
        return got.Length > 0 && string.Equals(got, expected, StringComparison.Ordinal);
    }

    private static async Task<Domain.ChannelAccount?> BaileysAccountByKey(AppDbContext db, string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var acct = await db.ChannelAccounts.FirstOrDefaultAsync(a =>
            a.Channel == "whatsapp" && a.Provider == "baileys" && a.ExternalId == key);
        if (acct != null) return acct;
        // Rows paired before an ExternalId was persisted use their DB id as the key.
        return int.TryParse(key, out var id)
            ? await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == id && a.Provider == "baileys")
            : null;
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var n) && n.ValueKind == JsonValueKind.Number ? n.ToString() : null);

    private static async Task<byte[]> ReadBody(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}
