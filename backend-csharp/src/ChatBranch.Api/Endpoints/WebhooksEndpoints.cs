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
/// Cloud traffic is matched to ONE registered number (phone_number_id, plus the
/// WABA as a cross-check): once the install has a Cloud number, a payload that
/// matches none is recorded and dropped rather than filed in the organization-wide
/// unattributed bucket — see <see cref="CloudInboxRouting"/> for why, and for the
/// Setting that restores the old behaviour.
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

            var messages = MetaWebhook.CloudMessages(root);
            var reactions = MetaWebhook.CloudReactions(root);
            var statuses = MetaWebhook.CloudStatuses(root);

            // ---- route by REGISTERED number only (Channels/CloudInboxRouting) ----
            // A payload whose phone_number_id matches no account used to be processed
            // with accountId: null, which filed the conversation in the organization-wide
            // unattributed bucket — visible to every company, and adoptable by the first
            // company whose own number later touched it. On an install that HAS Cloud
            // numbers such a payload is now recorded and dropped (200, so Meta stops
            // retrying). The guard arms as soon as the install has ANY WhatsApp number,
            // whatever its provider: a Baileys- or WAHA-only install has no Cloud number
            // subscribed, so nothing Meta sends here can ever be legitimate — and this
            // endpoint is public and accepts unsigned posts while no Cloud account exists
            // (`required` above), so an unarmed guard would let anyone append text to a
            // real customer's conversation by posting their number. Only an install with
            // no WhatsApp number at all (sandbox, demo) keeps the old permissive filing,
            // and the Setting whatsapp_accept_unknown_numbers restores it anywhere.
            var routingArmed = env != null
                               || await db.ChannelAccounts.AnyAsync(a => a.Channel == "whatsapp"
                                   && a.ExternalId != "");
            var unroutable = new HashSet<string>(StringComparer.Ordinal);
            if (routingArmed && !CloudInboxRouting.AcceptUnknownNumbers(sc))
                foreach (var (pnid, waba) in MetaWebhook.CloudNumbers(root))
                {
                    // the env-default number is a legitimate account-less route (it carries
                    // the credentials of the one number the install was started with)
                    var known = AccountFor(pnid) ?? (env != null && env.ExternalId == pnid ? env : null);
                    var why = known == null
                        ? $"No WhatsApp number is registered with phone_number_id {pnid}"
                        : CloudInboxRouting.WabaMismatch(known, waba)
                            ? $"phone_number_id {pnid} belongs to WABA {known.WabaId}, "
                              + $"but the payload says {waba.Trim()}"
                            : "";
                    if (why.Length == 0) continue;
                    unroutable.Add(pnid);
                    CloudInboxRouting.RecordUnknownNumber(db, pnid, waba, why,
                        messages.Count(m => m.PhoneNumberId == pnid),
                        reactions.Count(r => r.PhoneNumberId == pnid),
                        statuses.Count(s => s.PhoneNumberId == pnid),
                        messages.FirstOrDefault(m => m.PhoneNumberId == pnid)?.Wamid ?? "");
                }

            foreach (var m in messages)
            {
                if (unroutable.Contains(m.PhoneNumberId)) continue;
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
            foreach (var r in reactions)
            {
                if (unroutable.Contains(r.PhoneNumberId)) continue;
                try { MessageReactions.ApplyInbound(db, "whatsapp", r.From, r.TargetWamid, r.Emoji, accountId: AccountFor(r.PhoneNumberId)?.Id); }
                catch { }
            }

            // delivery receipts: sent → delivered → read, or failed (+ 131047 window
            // close; account-level codes → health, per-recipient codes never)
            foreach (var st in statuses)
            {
                // a receipt bumps the thread's updated_at, so an unroutable number
                // is not allowed to touch one either
                if (unroutable.Contains(st.PhoneNumberId)) continue;
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
            // a GROUP keeps its whole jid as the conversation key ("1203…@g.us") — its digits
            // alone would look like a phone number (Channels/GroupChats.cs)
            var isGroup = GroupChats.IsGroup(Domain.Channels.WhatsApp, waId);
            if (isGroup) waId = GroupChats.WhatsAppKey(waId);
            if (string.IsNullOrEmpty(waId)) return Results.Ok(new { ok = true });
            // a reaction ({reaction: {msg_id, emoji}}) lands on the message it targets —
            // no bubble, no AI, no flows, no reopen (MessageReactions)
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("reaction", out var reaction)
                && reaction.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    var reactingTo = await BaileysAccountByKey(db, Str(body, "account_id"));
                    MessageReactions.ApplyInbound(db, "whatsapp", waId, Str(reaction, "msg_id") ?? "",
                        Str(reaction, "emoji") ?? "", accountId: reactingTo?.Id);
                }
                catch { }
                return Results.Ok(new { ok = true });
            }
            var account = await BaileysAccountByKey(db, Str(body, "account_id"));
            // Baileys pushes only text here (media arrives on /media afterwards);
            // an empty text is a media-only message — still record the touch.
            if (isGroup)
            {
                // {group: {jid, subject}, sender: {id, name, phone}, mentioned}: the conversation is
                // named after the group (never after whoever wrote), each message after its author
                var subject = body.TryGetProperty("group", out var g) ? Str(g, "subject") : null;
                InboundSender? sender = body.TryGetProperty("sender", out var s) && s.ValueKind == JsonValueKind.Object
                    ? new InboundSender(Str(s, "id") ?? "", Str(s, "name") ?? "", Str(s, "phone") ?? "")
                    : null;
                var mentioned = body.TryGetProperty("mentioned", out var mn)
                                && (mn.ValueKind == JsonValueKind.True
                                    || (mn.ValueKind == JsonValueKind.String && mn.GetString() is "1" or "true"));
                var gm = processor.ProcessInbound("whatsapp", waId, text ?? "", subject, account?.Id,
                    Str(body, "msg_id") ?? "", replyToExternalId: Str(body, "reply_to") ?? "",
                    sender: sender, addressedToUs: mentioned);
                try
                {
                    var gt = await db.Threads.FirstOrDefaultAsync(t => t.Id == gm.ThreadId);
                    if (gt != null) GroupChats.Rename(db, gt, subject);
                }
                catch { /* a name is cosmetic */ }
            }
            else
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

        // A message the person sent from the PHONE (or WhatsApp Web) of a paired number —
        // stored as their reply, not an inbound (Channels/PhoneSentMessages). An unknown
        // number has nothing to file it under: acked, never retried.
        app.MapPost("/api/whatsapp/baileys/from-phone", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, ThreadStore store, InboundProcessor processor) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true, stored = false }); }
            var account = await BaileysAccountByKey(db, Str(body, "account_id"));
            if (account == null) return Results.Ok(new { ok = true, stored = false });
            long ts = 0;
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("ts", out var tsEl)
                && tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var t)) ts = t;
            var msg = ChatBranch.Data.Channels.PhoneSentMessages.Record(db, sc, store, processor, account,
                Str(body, "wa_id") ?? "", Str(body, "text") ?? "", Str(body, "msg_id") ?? "",
                Str(body, "reply_to") ?? "", ts);
            if (msg != null && account.HealthStatus != Domain.Health.Healthy)
            {
                // the session just carried a message — it is alive
                ChatBranch.Data.Channels.AccountHealth.SetStatus(db, account, Domain.Health.Healthy,
                    "message sent from the phone", actor: "webhook");
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok = true, stored = msg != null });
        }).AllowAnonymous();

        // The chats the number ALREADY had, replayed by WhatsApp when it links and
        // pushed here in batches (backend/baileys_service/history.js). This is a
        // BACKFILL, not an inbound: Channels/WhatsAppHistoryImport stores the words
        // through ThreadStore and runs nothing else — no AI, no flows, no automations,
        // no notifications, no reopening a conversation someone had finished. The
        // answer is meaningful, as every gateway push's is: 200 with what was stored,
        // 400 for a body with no messages (never retryable), 404 for an unknown number.
        app.MapPost("/api/whatsapp/baileys/history", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, ThreadStore store, InboundProcessor processor) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Json(new { detail = "a JSON body is required" }, statusCode: 400); }
            var account = await BaileysAccountByKey(db, Str(body, "account_id"));
            if (account == null)
                return Results.Json(new { detail = "unknown account_id" }, statusCode: 404);
            if (body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("messages", out var list) || list.ValueKind != JsonValueKind.Array)
                return Results.Json(new { detail = "messages[] is required" }, statusCode: 400);

            var rows = new List<ChatBranch.Data.Channels.HistoryMessage>();
            foreach (var m in list.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                long ts = 0;
                if (m.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number
                    && tsEl.TryGetInt64(out var t)) ts = t;
                var fromMe = m.TryGetProperty("from_me", out var fm)
                             && (fm.ValueKind == JsonValueKind.True
                                 || (fm.ValueKind == JsonValueKind.String && fm.GetString() is "1" or "true"));
                rows.Add(new ChatBranch.Data.Channels.HistoryMessage(
                    Str(m, "wa_id") ?? "", Str(m, "name") ?? "", Str(m, "msg_id") ?? "", fromMe, ts,
                    Str(m, "text") ?? "", Str(m, "reply_to") ?? ""));
            }

            var result = ChatBranch.Data.Channels.WhatsAppHistoryImport.Import(db, sc, store, processor,
                account, rows);
            return Results.Ok(new
            {
                ok = true, stored = result.Stored, skipped = result.Skipped, threads = result.Threads,
            });
        }).AllowAnonymous();

        // "Delete for everyone" on a QR number: the customer's (or, from_me, a recall made on
        // the number's own phone). Kept or erased by the inbox's keep_recalled switch
        // (Channels/MessageRecalls). Unknown number / message: acked, nothing to do.
        app.MapPost("/api/whatsapp/baileys/recall", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, ThreadStore store, ChatBranch.Data.Ai.AiGate gate) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true, recalled = 0 }); }
            var account = await BaileysAccountByKey(db, Str(body, "account_id"));
            if (account == null) return Results.Ok(new { ok = true, recalled = 0 });
            var fromMe = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("from_me", out var fm)
                         && (fm.ValueKind == JsonValueKind.True || (fm.ValueKind == JsonValueKind.String && fm.GetString() is "1" or "true"));
            var rows = ChatBranch.Data.Channels.MessageRecalls.Record(db, store, account,
                GroupChats.WhatsAppKey(Str(body, "wa_id")), Str(body, "msg_id") ?? "",
                fromMe ? ChatBranch.Data.Channels.MessageRecalls.ByBusiness : ChatBranch.Data.Channels.MessageRecalls.ByCustomer,
                gate);
            return Results.Ok(new { ok = true, recalled = rows.Count });
        }).AllowAnonymous();

        // An edit WhatsApp already applied — made on our own number's phone (or
        // WhatsApp Web), or by the customer on their own message. It is the same
        // message re-worded, so it updates the row rather than adding a bubble;
        // the old wording goes to the activity log, as it does for an edit made
        // from the inbox (ONE writer, Channels/MessageEdits.cs).
        app.MapPost("/api/whatsapp/baileys/edited", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, ThreadStore store, ChatBranch.Data.Ai.AiGate gate) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true, edited = 0 }); }
            var account = await BaileysAccountByKey(db, Str(body, "account_id"));
            if (account == null) return Results.Ok(new { ok = true, edited = 0 });
            var fromMe = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("from_me", out var fm)
                         && (fm.ValueKind == JsonValueKind.True || (fm.ValueKind == JsonValueKind.String && fm.GetString() is "1" or "true"));
            var rows = ChatBranch.Data.Channels.MessageEdits.RecordExternal(db, store, account,
                GroupChats.WhatsAppKey(Str(body, "wa_id")), Str(body, "msg_id") ?? "",
                Str(body, "text") ?? "",
                fromMe ? ChatBranch.Data.Channels.MessageEdits.ByBusiness
                       : ChatBranch.Data.Channels.MessageEdits.ByCustomer,
                gate);
            return Results.Ok(new { ok = true, edited = rows.Count });
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
                // never paired — the QR expired unscanned (nothing can arrive)
                "qr_expired" => Domain.Health.Disconnected,
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
        // row is created through the normal inbound path first. A file that could
        // NOT be stored answers non-2xx (400 bad request, 413 too large, 500 save
        // failure) so the gateway knows to retry or to log the loss — never a 200.
        app.MapPost("/api/whatsapp/baileys/media", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, InboundProcessor processor, MediaStore media, ThreadStore store) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            static IResult NotStored(int status, string detail) =>
                Results.Json(new { ok = false, stored = false, detail }, statusCode: status);
            if (!ctx.Request.HasFormContentType) return NotStored(400, "multipart form expected");
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(); }
            catch (Exception e) when (e is InvalidDataException or BadHttpRequestException)
            {
                return e.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)
                    ? NotStored(413, "file too large")
                    : NotStored(400, "unreadable form");
            }
            // a group's whole jid, else the number's digits (Channels/GroupChats.cs)
            var waId = GroupChats.WhatsAppKey(form["wa_id"].ToString());
            var msgId = form["msg_id"].ToString().Trim();
            var declared = form["media_type"].ToString().Trim().ToLowerInvariant();
            var file = form.Files["file"];
            if (waId.Length == 0 || file == null || file.Length == 0)
                return NotStored(400, "wa_id and a non-empty file are required");
            if (file.Length > MediaStore.MaxBytes) return NotStored(413, "file too large");

            var type = declared is "image" or "video" or "audio" or "document"
                ? declared : MediaStore.TypeForMime(file.ContentType);
            // the placeholder /inbound stored (its provider id names the conversation), else a
            // new inbound on the pushing number — its company's conversation, never another's
            var account = await BaileysAccountByKey(db, form["account_id"].ToString());
            var msg = msgId.Length > 0
                ? (await db.Messages.Include(m => m.Thread)
                    .Where(m => m.ExternalId == msgId && m.Thread!.Channel == "whatsapp" && m.Thread.Key == waId)
                    .OrderByDescending(m => m.Id).ToListAsync())
                    .OrderByDescending(m => account != null && m.Thread!.AccountId == account.Id).FirstOrDefault()
                : null;
            // a file the person sent from the phone (from_me): their outbound message, never an inbound
            if (msg == null && form["from_me"].ToString() == "1")
            {
                if (account == null) return Results.Ok(new { ok = true, stored = false });
                msg = ChatBranch.Data.Channels.PhoneSentMessages.Record(db, sc, store, processor, account, waId,
                    $"[{type}]", msgId);
                if (msg == null) return NotStored(500, "could not record the message");
            }
            if (msg == null)
            {
                // the text push never landed: a group file still carries its author
                var senderId = form["sender_id"].ToString().Trim();
                var senderName = form["sender_name"].ToString().Trim();
                msg = processor.ProcessInbound("whatsapp", waId, $"[{type}]", null, account?.Id, msgId,
                    sender: senderId.Length > 0 || senderName.Length > 0
                        ? new InboundSender(senderId, senderName) : null);
            }
            var thread = await db.Threads.FirstAsync(t => t.Id == msg.ThreadId);
            if (msg.MediaPath.Length > 0) return Results.Ok(new { ok = true, stored = true }); // redelivery

            // the gateway names the file only when the customer did (a document);
            // a voice note / photo arrives as "media.<ext>" or the like — not a real name
            var givenName = form["file_name"].ToString();
            if (givenName.Length == 0 && type == "document") givenName = file.FileName;
            try
            {
                await using var stream = file.OpenReadStream();
                var rel = await media.SaveAsync("whatsapp", waId, stream,
                    MediaStore.ExtFor(file.ContentType, file.FileName));
                if (rel == null) return NotStored(413, "file too large");
                MediaStore.Stamp(msg, type, rel, file.ContentType, givenName, file.Length);
                thread!.UpdatedAt = DateTime.UtcNow; // the inbox stream re-renders the bubble
                await db.SaveChangesAsync();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DbUpdateException)
            {
                return NotStored(500, "could not store the file");
            }
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
            // no room: the calling NUMBER's conversation (its company's), never another company's
            thread ??= string.IsNullOrEmpty(waId)
                ? null
                : store.FindForAccount("whatsapp", waId, (await BaileysAccountByKey(db, Str(body, "account_id")))?.Id);
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
                Str(body, "reason") ?? "", string.IsNullOrEmpty(starter) ? "flow" : starter, account, voice,
                threadId: thread.Id, accountId: thread.AccountId);
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
