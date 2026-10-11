using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Per-chat WhatsApp operations — the actions the <c>/chats</c> inbox fires on a
/// single conversation: the thread DETAIL (with its message page), starting a
/// chat, the three send paths, the AI/status/labels/account toggles, and the
/// call-control surface. Ported from <c>backend/whatsapp/views.py</c>
/// (<c>thread_detail</c>, <c>threads</c> POST, <c>send_message</c>,
/// <c>send_template</c>, <c>send_media</c>, <c>toggle_ai</c>,
/// <c>set_labels</c>, <c>switch_account</c>, <c>call_*</c>).
///
/// WIRE FORMAT IS snake_case — the dashboard reads these exact keys
/// (<c>wa_id</c>, <c>window_open</c>, <c>ai_enabled</c>, message <c>dir</c>/
/// <c>ts</c>…), so the payloads are built INLINE here rather than through the
/// camelCase <see cref="Dtos"/> mappers. Adding a key is safe; renaming one
/// breaks <c>frontend/src/app/chats/page.tsx</c>.
///
/// SANDBOX IS MANDATORY. With zero linked accounts every send stores locally
/// with status "sandbox" and every call control answers with a clean stub — the
/// page must never dead-end on a 500 just because no WhatsApp number is linked.
///
/// The thread LIST (<c>GET /api/whatsapp/threads</c>), the message pager and the
/// SSE stream live in <see cref="InboxEndpoints"/>; the config surface
/// (templates, routing rules, accounts) lives in
/// <see cref="WhatsAppSettingsEndpoints"/>.
/// </summary>
public static class WhatsAppThreadsEndpoints
{
    // ---- request bodies ------------------------------------------------------
    // The dashboard posts snake_case, and this app configures no global naming
    // policy, so every multi-word field carries an explicit [JsonPropertyName].

    public record CreateThreadRequest(
        string? Phone, string? Key, string? Channel, string? Name,
        [property: JsonPropertyName("account_id")] string? AccountId);

    /// <summary><c>reply_to</c> = the pk of the message this one quotes (same conversation).</summary>
    public record SendTextRequest(string? Text, bool Force = false,
        [property: JsonPropertyName("reply_to")] int? ReplyTo = null);

    /// <summary>
    /// <c>variables</c> fill the body's {{1}}…{{n}} (positional). A TEXT header and
    /// dynamic URL buttons take <c>header_variables</c> / <c>button_variables</c>
    /// when given (else the body value of the same number); a media header needs
    /// <c>header_media_url</c>.
    /// </summary>
    public record SendTemplateRequest(string? Name, string? Language, List<string>? Variables,
        [property: JsonPropertyName("header_variables")] List<string>? HeaderVariables = null,
        [property: JsonPropertyName("button_variables")] List<string>? ButtonVariables = null,
        [property: JsonPropertyName("header_media_url")] string? HeaderMediaUrl = null);

    public record ToggleAiRequest(bool Enabled);

    public record SetStatusRequest(string? Status);

    public record SetLabelsRequest(List<string>? Labels);

    public record SwitchAccountRequest(
        [property: JsonPropertyName("account_id")] string? AccountId);

    public record CallStartRequest(
        [property: JsonPropertyName("voice_provider")] string? VoiceProvider,
        [property: JsonPropertyName("voice_id")] string? VoiceId,
        bool Force = false);

    public record CallSayRequest(string? Mode, string? Text);

    /// <summary>Meta's customer-service window (store.WINDOW_HOURS).</summary>
    private const int WindowHours = 24;

    /// <summary>Default message page size for the detail view (views.thread_detail).</summary>
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private const long MaxMediaBytes = 16 * 1024 * 1024;

    /// <summary>Allowed upload extension → (Meta media type, mime), views.MEDIA_EXTENSIONS.</summary>
    private static readonly Dictionary<string, (string Type, string Mime)> MediaExtensions = new()
    {
        [".jpg"] = ("image", "image/jpeg"),
        [".jpeg"] = ("image", "image/jpeg"),
        [".png"] = ("image", "image/png"),
        [".webp"] = ("image", "image/webp"),
        [".pdf"] = ("document", "application/pdf"),
        [".doc"] = ("document", "application/msword"),
        [".docx"] = ("document",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".xls"] = ("document", "application/vnd.ms-excel"),
        [".xlsx"] = ("document",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        [".csv"] = ("document", "text/csv"),
        [".txt"] = ("document", "text/plain"),
        [".mp3"] = ("audio", "audio/mpeg"),
        [".ogg"] = ("audio", "audio/ogg"),
        [".m4a"] = ("audio", "audio/mp4"),
        [".mp4"] = ("video", "video/mp4"),
    };

    private const string ColdTextBlockDetail =
        "This number has never received a message from this contact. Cold free-text "
        + "from a personal number carries ban risk — open with an approved template, "
        + "or have the contact message first. An admin may press Send anyway (API: force=true).";

    private const string ColdTextWarnNote =
        "[Cold outbound: this contact has never messaged this number. Ban risk is "
        + "higher on first contact.]";

    /// <summary>wa_calls.ESCALATE_TEXT — the guide the operator's Escalate button pushes.</summary>
    private const string EscalateText =
        "Politely tell the caller that a human agent will follow up with them "
        + "shortly, then wrap up.";

    // =========================================================================

    public static void MapWhatsAppThreadsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- thread detail (+ inline message page) ---------------------------
        // views.thread_detail. `?limit=` (default 50, capped at 500) and
        // `?before=<pk>` page backwards through history.
        app.MapGet("/api/whatsapp/threads/{key}", async (string key, int? limit, int? before,
            AppDbContext db, ScopingService sc, IWhatsAppCloudClient wa) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            return Results.Ok(await ThreadPayload(db, sc, wa, thread, take, before));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.view");

        // ---- start a chat ----------------------------------------------------
        // views.threads POST. The inbox's "new chat" row posts {phone}; the body
        // also accepts {key}/{channel} so the same endpoint opens a thread the
        // unified inbox already knows by key.
        app.MapPost("/api/whatsapp/threads", async (CreateThreadRequest body, AppDbContext db,
            ScopingService sc, ThreadStore store, IWhatsAppCloudClient wa, ClaimsPrincipal me) =>
        {
            var raw = (body.Phone ?? body.Key ?? "").Trim();
            if (raw.Length == 0)
                return Results.BadRequest(new { detail = "phone is required." });
            var waId = NormalizeWaId(raw);
            if (waId.Length == 0)
                return Results.BadRequest(new { detail = "phone is required." });

            var channel = string.IsNullOrWhiteSpace(body.Channel)
                ? ChatBranch.Domain.Channels.WhatsApp : body.Channel!.Trim();
            // an existing conversation outside the caller's scope is not theirs to open
            var existing = await db.Threads.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Channel == channel && t.Key == waId);
            if (existing != null && !ThreadAccessFilter.InScope(db, sc, me, existing.EntityId))
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            var explicitAccount = !string.IsNullOrWhiteSpace(body.AccountId);
            // moving a conversation that already has a number is the per-chat
            // switch (POST …/{key}/account), an admin's call — never a side effect
            if (existing?.AccountId is int current && explicitAccount && !IsAdminPlus(me)
                && int.TryParse(body.AccountId!.Trim(), out var asked) && asked != current)
                return Results.Json(new { detail = "Only an admin can move an existing conversation to another number." },
                    statusCode: 403);
            var accountId = existing?.AccountId != null && !explicitAccount
                ? existing.AccountId
                : await ResolveNewThreadAccount(db, wa, waId, body.AccountId);
            // a number that would put the conversation in another outlet: refused when
            // asked for by name, otherwise left for send-time routing to decide
            if (accountId is int picked)
            {
                var pickedEntity = await db.ChannelAccounts.AsNoTracking()
                    .Where(a => a.Id == picked).Select(a => a.EntityId).FirstOrDefaultAsync();
                if (!ThreadAccessFilter.InScope(db, sc, me, pickedEntity))
                {
                    if (explicitAccount)
                        return Results.Json(new { detail = "That number belongs to an outlet outside your scope." },
                            statusCode: 403);
                    accountId = null;
                }
            }
            // Same store path as InboxEndpoints /simulate, so a chat started here
            // and one created by an inbound webhook are the same row.
            var thread = store.GetOrCreateThread(channel, waId, body.Name);
            if (accountId is int acct && thread.AccountId != acct)
            {
                thread.AccountId = acct;
                sc.SyncThreadEntity(thread, save: false);
                thread.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                await db.Entry(thread).Reference(t => t.Account).LoadAsync();
            }
            return Results.Ok(await ThreadPayload(db, sc, wa, thread, DefaultLimit, null));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send");

        // ---- send: free text -------------------------------------------------
        // views.send_message. Compliance order matters and mirrors Django:
        // opt-out -> cold-text guard -> 24h window -> send.
        app.MapPost("/api/whatsapp/threads/{key}/send", async (string key, SendTextRequest body,
            AppDbContext db, ScopingService sc, ThreadStore store, OutboundSender sender,
            IWhatsAppCloudClient wa, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var text = (body.Text ?? "").Trim();
            if (text.Length == 0)
                return Results.BadRequest(new { detail = "text is required." });
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            if (thread.OptedOut && !body.Force)
                return Results.Json(new
                {
                    detail = "Contact opted out of WhatsApp contact (STOP). "
                             + "Resend with force=true to override as a manual exception.",
                }, statusCode: 409);

            // Routing at send time: the thread's own healthy number, else the
            // contact's sticky / least-loaded conversation number, failing over
            // off a banned/disconnected one (the move happens just before sending).
            var routing = new Routing(db, sc);
            var route = routing.ForThread(thread, RouteNeed.Conversation, apply: false);
            if (!route.Ok) return Results.Json(new { detail = route.Error }, statusCode: 409);
            var account = route.Account;
            var live = account != null && wa.IsConfigured(account);

            // Cold-text guard: free text into a zero-inbound thread on a
            // conversation-role number is the ban-risk case (Setting cold_text_guard).
            var coldNote = "";
            if (account != null && AccountRole(account) == "conversation"
                && !await db.Messages.AnyAsync(m => m.ThreadId == thread.Id
                                                    && m.Direction == MessageDirection.In))
            {
                var guard = Rule(sc, "cold_text_guard", "block");
                if (guard == "block" && !(body.Force && IsAdminPlus(me)))
                    // code + can_override let the composer offer an admin "Send anyway"
                    return Results.Json(new { detail = ColdTextBlockDetail, code = "cold_text",
                        can_override = IsAdminPlus(me) }, statusCode: 409);
                if (guard == "warn") coldNote = ColdTextWarnNote;
            }
            if (live && !WindowOpen(db, thread, account))
                return Results.Json(new
                {
                    detail = "WhatsApp 24h window closed — use a template.",
                }, statusCode: 409);

            if (account != null && account.Id != thread.AccountId)
                routing.ForThread(thread, RouteNeed.Conversation, apply: true);
            // OutboundSender is the one live-or-sandbox seam: configured account →
            // graph.facebook.com, otherwise stored locally with status "sandbox".
            // a quote of a message of THIS conversation (anything else is ignored, never a failure)
            var quoted = body.ReplyTo is int replyPk
                ? await db.Messages.FirstOrDefaultAsync(m => m.Id == replyPk && m.ThreadId == thread.Id)
                : null;
            var msg = sender.Send(thread, text, MessageSource.Agent, account?.Id, replyTo: quoted);
            AiControlsEndpoints.HumanReplied(db, thread, msg, me); // answers an open handover
            if (coldNote.Length > 0)
                store.AppendMessage(thread, MessageDirection.Out, MessageSource.System, coldNote,
                    status: live ? "sent" : "sandbox", accountId: account?.Id);
            await Audit(db, ctx, me, "message.send", $"whatsapp:{thread.Key}", Cap(text, 200));
            return Results.Ok(new { message = MessageExtras.Decorate(db, msg, MessagePayload(msg, account)) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- send: template --------------------------------------------------
        // views.send_template — the only way to (re)open a conversation outside
        // the 24h window, so it deliberately does NOT check window_open. A
        // configured Cloud number sends a REAL template message (name, language,
        // header/body/button parameters) and the inbox stores the rendered body;
        // Baileys and sandbox store/send the rendered body as text.
        app.MapPost("/api/whatsapp/threads/{key}/send-template", async (string key,
            SendTemplateRequest body, AppDbContext db, ScopingService sc, OutboundSender sender,
            IWhatsAppCloudClient wa, WhatsAppTemplateCatalog catalog, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length == 0)
                return Results.BadRequest(new { detail = "name is required." });
            var variables = (body.Variables ?? new List<string>())
                .Select(v => v ?? "").ToList();
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            if (thread.OptedOut)
                return Results.Json(new
                {
                    detail = "Contact opted out of WhatsApp contact — template sends are blocked.",
                }, statusCode: 409);
            // Capability routing: a template goes out on a healthy Cloud number
            // (the business/reminder one first) — a Baileys assignment is
            // overridden for this one send, and templates NEVER fail over to
            // Baileys: with Cloud numbers configured but none healthy → 409 and
            // the thread is untouched. An organization with no Cloud number at
            // all keeps the thread's own number (Baileys renders it as text).
            var routing = new Routing(db, sc);
            var route = routing.ForThread(thread, RouteNeed.Template, apply: false);
            if (!route.Ok) return Results.Json(new { detail = route.Error }, statusCode: 409);
            var account = route.Account;
            // quiet hours are a per-account setting (Settings → Channels → edit
            // account), falling back to the organization-wide value
            if (AccountDelivery.InQuietHours(account, sc))
                return Results.Json(new
                {
                    detail = "Quiet hours are in effect for this account (Settings → Channels → "
                             + "edit account) — template sends resume when they end.",
                }, statusCode: 409);
            // Without a live number only the known (demo + custom) set exists; a
            // live number lets Meta be the judge of the name.
            if (!(account != null && wa.IsConfigured(account))
                && await catalog.FindAsync(account, name, body.Language) == null)
                return Results.BadRequest(new { detail = $"Unknown sandbox template '{name}'." });

            // per-send restamp: the conversation continues on the number that carried it
            if (account != null && account.Id != thread.AccountId)
                routing.ForThread(thread, RouteNeed.Template, apply: true);
            var msg = sender.SendTemplate(thread, name, body.Language,
                new TemplateArgs(variables, body.HeaderVariables, body.ButtonVariables, body.HeaderMediaUrl),
                MessageSource.Agent, account?.Id);
            AiControlsEndpoints.HumanReplied(db, thread, msg, me); // answers an open handover
            await Audit(db, ctx, me, "message.send", $"whatsapp:{thread.Key}", $"template:{name}");
            return Results.Ok(new { message = MessagePayload(msg, account) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- send: media (multipart) -----------------------------------------
        // views.send_media. The file is ALWAYS kept on disk (live or sandbox) so
        // the inbox can render the bubble either way; a configured Cloud number
        // then uploads it to /{phone_number_id}/media and sends it by id, a
        // Baileys number hands it to the gateway's send-media.
        app.MapPost("/api/whatsapp/threads/{key}/send-media", async (string key, HttpContext ctx,
            AppDbContext db, ScopingService sc, OutboundSender sender, IWhatsAppCloudClient wa,
            MediaStore media, ClaimsPrincipal me) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { detail = "file is required (multipart/form-data)." });
            var form = await ctx.Request.ReadFormAsync();
            var upload = form.Files["file"];
            if (upload == null || upload.Length == 0)
                return Results.BadRequest(new { detail = "file is required (multipart/form-data)." });
            var caption = form["caption"].ToString().Trim();
            var force = form["force"].ToString().ToLowerInvariant() is "1" or "true" or "yes";

            var ext = Path.GetExtension(upload.FileName ?? "").ToLowerInvariant();
            if (!MediaExtensions.TryGetValue(ext, out var kind))
            {
                var allowed = string.Join(", ", MediaExtensions.Keys.Select(e => e.TrimStart('.'))
                    .Distinct().OrderBy(e => e));
                return Results.BadRequest(new
                {
                    detail = $"File type '{(ext.Length > 0 ? ext : "?")}' not allowed. Allowed: {allowed}.",
                });
            }
            if (upload.Length > MaxMediaBytes)
                return Results.BadRequest(new { detail = "File too large (max 16 MB)." });

            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            if (thread.OptedOut && !force)
                return Results.Json(new
                {
                    detail = "Contact opted out of WhatsApp contact (STOP). "
                             + "Resend with force=true to override as a manual exception.",
                }, statusCode: 409);
            var account = await EffectiveAccount(db, wa, thread);
            var live = account != null && wa.IsConfigured(account);
            // a file into a zero-inbound thread on a conversation (Baileys) number
            // is the same ban-risk case as cold free text (Setting cold_text_guard)
            if (account != null && AccountRole(account) == "conversation"
                && Rule(sc, "cold_text_guard", "block") == "block" && !(force && IsAdminPlus(me))
                && !await db.Messages.AnyAsync(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In))
                return Results.Json(new { detail = ColdTextBlockDetail }, statusCode: 409);
            if (live && !WindowOpen(db, thread, account))
                return Results.Json(new
                {
                    detail = "WhatsApp 24h window closed — use a template.",
                }, statusCode: 409);

            string relPath;
            await using (var stream = upload.OpenReadStream())
                relPath = await media.SaveAsync(ChatBranch.Domain.Channels.WhatsApp, thread.Key, stream, ext)
                          ?? "";
            if (relPath.Length == 0)
                return Results.BadRequest(new { detail = "File too large (max 16 MB)." });
            var text = caption.Length > 0 ? caption : $"[{kind.Type}]";
            var msg = sender.SendMedia(thread, relPath, kind.Type, kind.Mime, caption,
                Path.GetFileName(upload.FileName ?? ""), MessageSource.Agent, account?.Id);
            AiControlsEndpoints.HumanReplied(db, thread, msg, me); // answers an open handover
            await Audit(db, ctx, me, "message.send", $"whatsapp:{thread.Key}",
                $"media:{kind.Type} {Cap(text, 150)}");
            return Results.Ok(new { message = MessagePayload(msg, account) });
        }).RequireAuthorization(AuthPolicies.Authenticated).DisableAntiforgery().RequireThreadAccess("chats.send");

        // ---- chatbot tick ----------------------------------------------------
        // views.toggle_ai. The tick is PER SENDING ACCOUNT (Thread.ai_overrides):
        // a chat moving between the business and personal numbers uses the
        // CURRENT number's own setting, so a switch never mutates AI state.
        app.MapPost("/api/whatsapp/threads/{key}/ai", async (string key, ToggleAiRequest body,
            AppDbContext db, ScopingService sc, IWhatsAppCloudClient wa,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            // the current number's tick (or, account-less, the thread's own) + the legacy column
            ChatBranch.Data.Ai.AiEligibility.Set(thread, body.Enabled);
            await db.SaveChangesAsync();
            await Audit(db, ctx, me, "thread.ai_toggle", $"whatsapp:{thread.Key}",
                $"enabled={body.Enabled}");
            return Results.Ok(await ThreadPayload(db, sc, wa, thread, DefaultLimit, null));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- conversation status (open / resolved / …) ------------------------
        // Chatwoot-style management, shared with the messaging channels because
        // every conversation is one crm.Thread row.
        app.MapPost("/api/whatsapp/threads/{key}/status", async (string key, SetStatusRequest body,
            AppDbContext db, ScopingService sc, IWhatsAppCloudClient wa,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            var status = (body.Status ?? "").Trim().ToLowerInvariant();
            var allowed = new[]
            {
                ThreadStatus.Open, ThreadStatus.Pending, ThreadStatus.Snoozed, ThreadStatus.Resolved,
            };
            if (!allowed.Contains(status))
                return Results.BadRequest(new
                {
                    detail = $"status must be one of: {string.Join(", ", allowed)}.",
                });
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            thread.Status = status;
            thread.UpdatedAt = DateTime.UtcNow;
            AiControlsEndpoints.StatusChanged(db, thread, status, me); // resolving closes a handover
            await db.SaveChangesAsync();
            await Audit(db, ctx, me, "thread.status", $"whatsapp:{thread.Key}", status);
            return Results.Ok(await ThreadPayload(db, sc, wa, thread, DefaultLimit, null));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- labels ----------------------------------------------------------
        // views.set_labels — deduped + lowercased in the store, same as messaging.
        app.MapPost("/api/whatsapp/threads/{key}/labels", async (string key, SetLabelsRequest body,
            AppDbContext db, ScopingService sc, IWhatsAppCloudClient wa,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (body.Labels == null)
                return Results.BadRequest(new { detail = "labels must be a list of strings." });
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            var clean = new List<string>();
            foreach (var raw in body.Labels)
            {
                var s = (raw ?? "").Trim().ToLowerInvariant();
                if (s.Length > 0 && !clean.Contains(s)) clean.Add(s);
            }
            thread.Labels = clean;
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await Audit(db, ctx, me, "thread.labels", $"whatsapp:{thread.Key}",
                string.Join(",", clean));
            return Results.Ok(await ThreadPayload(db, sc, wa, thread, DefaultLimit, null));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- switch the sending account (admin+) ------------------------------
        // views.switch_account. Meta's window is per OUR number, so the note the
        // operator sees depends on what this number already knows about the
        // contact: provider (Baileys = ban risk, no templates), whether it has
        // messaged them before, and whether they replied to it within 24h. The
        // contact's sticky assignment is pinned so the queue/flows follow.
        app.MapPost("/api/whatsapp/threads/{key}/account", async (string key,
            SwitchAccountRequest body, AppDbContext db, ScopingService sc, ThreadStore store,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            var accountId = (body.AccountId ?? "").Trim();
            if (!int.TryParse(accountId, out var acctPk))
                return Results.BadRequest(new
                {
                    detail = accountId == "env-default"
                        ? "The env-default account is not assignable — add it as a "
                          + "dashboard account first."
                        : "account_id required.",
                });
            var row = await db.ChannelAccounts.FirstOrDefaultAsync(
                a => a.Id == acctPk && a.Channel == ChatBranch.Domain.Channels.WhatsApp);
            if (row == null) return Results.NotFound(new { detail = "Account not found." });
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            if (thread.AccountId == row.Id)
                return Results.Ok(new { account_id = accountId, account_name = row.Name });

            var isBaileys = (row.Provider.Length > 0 ? row.Provider : Providers.Cloud) == Providers.Baileys;
            var priorContact = await db.Messages.AnyAsync(
                m => m.ThreadId == thread.Id && m.AccountId == row.Id);
            var windowStart = DateTime.UtcNow.AddHours(-WindowHours);
            var recentReply = await db.Messages.AnyAsync(
                m => m.ThreadId == thread.Id && m.AccountId == row.Id
                     && m.Direction == MessageDirection.In && m.CreatedAt >= windowStart);

            thread.AccountId = row.Id;
            thread.Account = row;
            sc.SyncThreadEntity(thread, save: false);
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            string note;
            if (isBaileys)
            {
                note = priorContact
                    ? $"[Sender account switched to {row.Name} (personal number via Baileys) — "
                      + "this number has messaged the contact before; no template needed.]"
                    : $"[Sender account switched to {row.Name} (personal number via Baileys) — "
                      + "first contact from this number: cold messages from an unknown personal "
                      + "number carry ban risk. Safest is to have the contact message this "
                      + "number first.]";
            }
            else if (recentReply)
            {
                note = $"[Sender account switched to {row.Name} — the contact replied to this "
                       + "number recently, so the 24h window is open; free text is fine.]";
            }
            else
            {
                // The window is per our-number: cap every inbound that did NOT
                // arrive on the new account so the composer flips to template mode
                // (store.close_window with except_account_id).
                await CloseWindow(db, thread, exceptAccountId: row.Id);
                note = priorContact
                    ? $"[Sender account switched to {row.Name} — the contact knows this number "
                      + "but hasn't replied to it in 24h; open with an approved template.]"
                    : $"[Sender account switched to {row.Name} — entity-new number for this "
                      + "contact (their side shows a new chat); open with an approved template.]";
            }

            if (thread.ContactId is int contactId)
            {
                var pin = await db.AccountAssignments.FirstOrDefaultAsync(
                    a => a.ContactId == contactId
                         && a.Channel == ChatBranch.Domain.Channels.WhatsApp);
                if (pin == null)
                {
                    db.AccountAssignments.Add(new AccountAssignment
                    {
                        ContactId = contactId,
                        Channel = ChatBranch.Domain.Channels.WhatsApp,
                        AccountId = row.Id,
                        Pinned = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                }
                else
                {
                    pin.AccountId = row.Id;
                    pin.Pinned = true;
                    pin.UpdatedAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync();
            }
            store.AppendMessage(thread, MessageDirection.Out, MessageSource.System, note,
                status: "sent", accountId: row.Id);
            await Audit(db, ctx, me, "thread.account_switch", $"whatsapp:{thread.Key}", row.Name);
            return Results.Ok(new
            {
                account_id = accountId,
                account_name = row.Name,
                provider = row.Provider.Length > 0 ? row.Provider : Providers.Cloud,
                prior_contact = priorContact,
                window_open_with_new_account = isBaileys || recentReply,
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus).RequireThreadAccess("chats.send");

        // =====================================================================
        // Call controls.
        //
        // A WhatsApp voice call runs over the Baileys transport: the API
        // dispatches the voice agent into a fresh LiveKit room (LiveKitClient)
        // and hands the gateway a room-join token; the gateway dials the
        // contact, joins as `whatsapp_borrower` and pushes ringing / connected /
        // ended back to /api/whatsapp/baileys/call-event. The transcript and
        // Guide/Takeover then go through the agent's FILE contract
        // (VoiceAgentBridge). With no linked account the whole call UI still
        // works in sandbox mode — the /chats panel must degrade, never 500.
        // =====================================================================

        // Live transcript — polled every ~1.5s by the info panel while a call is
        // up, so it stays a cheap file read (wa_calls.live_transcript).
        app.MapGet("/api/whatsapp/threads/{key}/call/live", async (string key, AppDbContext db,
            VoiceAgentBridge voice) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            var turns = new List<object>();
            var done = false;
            if (!string.IsNullOrEmpty(thread.CallRoom))
            {
                var live = voice.LiveTranscript(thread.CallRoom!);
                if (live.ValueKind == JsonValueKind.Object)
                {
                    if (live.TryGetProperty("turns", out var t) && t.ValueKind == JsonValueKind.Array)
                        turns = t.EnumerateArray().Select(x => (object)x.Clone()).ToList();
                    if (live.TryGetProperty("done", out var d)
                        && d.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        done = d.GetBoolean();
                }
            }
            return Results.Ok(new
            {
                status = thread.CallStatus.Length > 0 ? thread.CallStatus : "idle",
                call_id = thread.CallId,
                room = thread.CallRoom,
                turns,
                done,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.view");

        // Recordings — one entry per past call of this conversation: the Baileys
        // gateway's WAV ({wa_id}-{ms}.wav, else the voice agent's recording of the
        // same room) + its paired transcript and who started / ended it, newest
        // first. Played through GET /api/whatsapp/calls/recording/{name}.
        app.MapGet("/api/whatsapp/threads/{key}/call/recordings", async (string key, AppDbContext db,
            ScopingService sc, CallRecordings recordings, VoiceAgentBridge voice, ClaimsPrincipal me) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            if (!await WhatsAppMediaEndpoints.CanSeeThread(db, sc, me, thread))
                return Results.NotFound(new { detail = "Thread not found." });
            return Results.Ok(new { recordings = recordings.ListFor(thread.Key, voice) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.view");

        // Start. A linked Baileys account places a real call. With no linked
        // account Django's sandbox branch stays: the thread's call state flips
        // to in_call with the "wacid.sandbox" id and a system note lands in the
        // stream, so the call UI (pill, panel, End button) is demoable without a
        // WhatsApp number. A Cloud API account answers 501: its WebRTC call
        // bridge went with the Django backend.
        app.MapPost("/api/whatsapp/threads/{key}/call/start", async (string key,
            CallStartRequest? body, AppDbContext db, ThreadStore store, ScopingService sc,
            IWhatsAppCloudClient wa, IBaileysGateway baileys, LiveKitClient livekit, VoiceAgentBridge voice,
            ChatBranch.Data.Ai.KnowledgeService knowledge, ChatBranch.Data.Ai.AssistantService assistant,
            ClaimsPrincipal me, HttpContext ctx, CancellationToken ct) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            await Audit(db, ctx, me, "call.start", $"whatsapp:{thread.Key}");

            // Opt-out is absolute — not overridable by force, same as Django.
            if (thread.OptedOut)
                return Results.Json(new
                {
                    detail = "Contact opted out — calls are blocked.",
                }, statusCode: 409);
            if (thread.CallStatus is "connecting" or "ringing" or "in_call")
                return Results.Json(new
                {
                    detail = "A call is already in progress for this contact.",
                }, statusCode: 409);
            // AI usage quotas: the caller's own cap, then the conversation's outlet
            if (QuotaGuard.Refusal(db, sc, me) is { } userQuota) return userQuota;
            if (ChatBranch.Data.Ai.AiQuota.EntityBlock(db, sc, thread.EntityId) is { } entityQuota)
                return Results.Json(new { ok = false, detail = ChatBranch.Data.Ai.AiQuota.Reason(entityQuota), quota = true },
                    statusCode: 429);

            var account = await EffectiveAccount(db, wa, thread);
            // The conversation brain + STT engine are PER ACCOUNT (Settings →
            // Channels → edit account → Delivery & AI), falling back to the
            // organization-wide value.
            var llmProvider = AccountDelivery.Resolve(account, AccountDelivery.LlmProvider, sc);
            var sttProvider = AccountDelivery.Resolve(account, AccountDelivery.SttProvider, sc);
            if (account != null && wa.IsConfigured(account))
                return Results.Json(new
                {
                    ok = false,
                    detail = $"{WhatsAppProviders.Label(account.Provider)} calling is not available in this build "
                             + "(the official business APIs need a separate calling bridge); calls run over a "
                             + "linked Baileys (WhatsApp Web) number.",
                }, statusCode: 501);

            if (account is { Provider: Providers.Baileys, ExternalId.Length: > 0 } && baileys.IsConfigured)
            {
                if (livekit.Settings.MissingForServer() is string reason)
                    return Results.Json(new { ok = false, detail = reason }, statusCode: 503);
                var contact = thread.ContactId is int cid
                    ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct)
                    : null;
                if (contact?.DoNotCall == true)
                    return Results.Json(new { detail = "Contact is marked do-not-call." }, statusCode: 409);

                var room = LiveKitClient.NewRoomName();
                // The CRM contact's identity (not the WhatsApp push-name) rides in
                // the metadata; the agent greets only once wa.callStatus=active.
                var metadata = await VoiceDispatch.BuildAsync(db, knowledge, assistant, sc, voice.DataDir, contact,
                    new VoiceCallOptions("+" + thread.Key, VoiceProvider: body?.VoiceProvider, Voice: body?.VoiceId,
                        LlmProvider: llmProvider, SttProvider: sttProvider, EntityId: thread.EntityId), ct);
                var dispatched = await livekit.CreateDispatchAsync(room, JsonSerializer.Serialize(metadata), ct);
                if (!dispatched.Ok)
                    return Results.Json(new { ok = false, detail = dispatched.Detail }, statusCode: 502);
                var started = await baileys.StartCallAsync(account.ExternalId, thread.Key, new()
                {
                    ["room"] = room,
                    ["livekit_url"] = livekit.Settings.Url,
                    ["token"] = livekit.ParticipantToken(room, LiveKitClient.WhatsAppIdentity,
                        contact?.Name ?? thread.Key, TimeSpan.FromHours(2)),
                    ["max_seconds"] = MaxCallSeconds(),
                }, ct);
                if (!started.Ok)
                {
                    await livekit.DeleteRoomAsync(room, ct); // don't leave the agent waiting in an empty room
                    return Results.Json(new { ok = false, detail = started.Detail }, statusCode: 502);
                }

                thread.CallId = started.ExternalId.Length > 0 ? started.ExternalId : room;
                thread.CallStatus = "connecting";
                thread.CallRoom = room;
                thread.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                store.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                    "WhatsApp call started — voice agent dispatched.", status: "sent", accountId: account.Id);
                return Results.Ok(new
                {
                    call_id = thread.CallId,
                    status = thread.CallStatus,
                    room,
                    voice_provider = body?.VoiceProvider ?? "",
                    voice_id = body?.VoiceId ?? "",
                    llm_provider = llmProvider,
                    stt_provider = sttProvider,
                    sandbox = false,
                });
            }

            thread.CallId = "wacid.sandbox";
            thread.CallStatus = "in_call";
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            store.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                "[sandbox] WhatsApp call started — voice agent connected.", status: "sandbox",
                accountId: thread.AccountId);
            // The agent-voice picker's choice is echoed back rather than dropped.
            return Results.Ok(new
            {
                call_id = "wacid.sandbox",
                status = "in_call",
                room = thread.CallRoom,
                voice_provider = body?.VoiceProvider ?? "",
                voice_id = body?.VoiceId ?? "",
                llm_provider = llmProvider,
                stt_provider = sttProvider,
                sandbox = true,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // End. A live Baileys call is hung up at the gateway and its room
        // deleted (best-effort: either may already be gone); then the thread's
        // call state resets (wa_calls.end_call) and the panel closes cleanly.
        app.MapPost("/api/whatsapp/threads/{key}/call/end", async (string key, AppDbContext db,
            ThreadStore store, IWhatsAppCloudClient wa, IBaileysGateway baileys, LiveKitClient livekit,
            VoiceAgentBridge voice, ClaimsPrincipal me, HttpContext ctx, CancellationToken ct) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            await Audit(db, ctx, me, "call.end", $"whatsapp:{thread.Key}");
            var wasLive = thread.CallStatus is "connecting" or "ringing" or "in_call";
            var real = !string.IsNullOrEmpty(thread.CallRoom);
            if (real)
            {
                // the agent records who ended the call (ended_by=operator)
                try { voice.MarkOperatorEnd(thread.CallRoom!); } catch { /* provenance only */ }
                var account = await EffectiveAccount(db, wa, thread);
                if (account is { Provider: Providers.Baileys, ExternalId.Length: > 0 } && baileys.IsConfigured)
                    await baileys.EndCallAsync(account.ExternalId, ct);
                if (livekit.Settings.MissingForServer() == null)
                    await livekit.DeleteRoomAsync(thread.CallRoom!, ct);
            }
            thread.CallId = null;
            thread.CallStatus = "ended";
            thread.CallRoom = null;
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (wasLive)
                store.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                    "WhatsApp call ended.", status: real ? "sent" : "sandbox", accountId: thread.AccountId);
            return Results.Ok(new { status = "ended" });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // Say (guide | takeover) — into the voice agent's file inbox when a room
        // is attached; a clean 409 otherwise (wa_calls.push_say).
        app.MapPost("/api/whatsapp/threads/{key}/call/say", async (string key, CallSayRequest body,
            AppDbContext db, VoiceAgentBridge voice, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            var mode = (body.Mode ?? "guide").Trim();
            if (mode is not ("guide" or "takeover"))
                return Results.BadRequest(new { detail = "mode must be 'guide' or 'takeover'." });
            var text = (body.Text ?? "").Trim();
            if (text.Length == 0)
                return Results.BadRequest(new { detail = "text is required." });
            if (string.IsNullOrEmpty(thread.CallRoom))
                return Results.Json(new
                {
                    detail = "No live call is in progress for this contact.",
                }, statusCode: 409);
            voice.PushInstruction(thread.CallRoom!, mode, text);
            await Audit(db, ctx, me, "call.say", $"whatsapp:{thread.Key}",
                $"[{mode}] {Cap(text, 120)}");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // Escalate — the same guide push with the fixed hand-off wording.
        app.MapPost("/api/whatsapp/threads/{key}/call/escalate", async (string key,
            AppDbContext db, VoiceAgentBridge voice, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            if (string.IsNullOrEmpty(thread.CallRoom))
                return Results.Json(new
                {
                    detail = "No live call is in progress for this contact.",
                }, statusCode: 409);
            voice.PushInstruction(thread.CallRoom!, "guide", EscalateText);
            await Audit(db, ctx, me, "call.escalate", $"whatsapp:{thread.Key}");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");
    }

    // ======================= payload builders ================================

    /// <summary>
    /// store._serialize — the thread dict the /chats page binds to, snake_case,
    /// with the newest <paramref name="limit"/> messages inline (oldest→newest).
    /// </summary>
    private static async Task<object> ThreadPayload(AppDbContext db, ScopingService sc,
        IWhatsAppCloudClient wa, Thread thread, int limit, int? before)
    {
        var account = await EffectiveAccount(db, wa, thread);
        var live = account != null && wa.IsConfigured(account);
        var isBaileys = account != null && account.Provider == Providers.Baileys;

        IQueryable<Message> q = db.Messages.AsNoTracking().Where(m => m.ThreadId == thread.Id);
        if (before is int b) q = q.Where(m => m.Id < b);
        // take limit+1 to learn whether an older page exists, then chronological
        var newest = await q.OrderByDescending(m => m.Id).Take(limit + 1).ToListAsync();
        var hasMore = newest.Count > limit;
        var page = newest.Take(limit).ToList();
        page.Reverse();

        var accountIds = page.Where(m => m.AccountId != null).Select(m => m.AccountId!.Value)
            .Distinct().ToList();
        var names = await db.ChannelAccounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name);

        // Baileys is a normal WhatsApp account over WhatsApp Web — Meta's 24h
        // customer-service window does not apply. Sandbox is always open so the
        // demo never dead-ends.
        var windowOpen = true;
        string? windowExpires = null;
        if (live && !isBaileys)
        {
            var lastIn = await db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
                .MaxAsync(m => (DateTime?)m.CreatedAt);
            if (lastIn is DateTime ts)
            {
                var expires = ts.AddHours(WindowHours);
                windowOpen = DateTime.UtcNow < expires;
                windowExpires = expires.ToString("o");
            }
            else
            {
                windowOpen = false;   // contact never wrote in → template territory
            }
        }

        var aiVerdict = ChatBranch.Data.Ai.AiEligibility.Decide(db, sc, thread); // the full AI rule + its reason
        return new
        {
            wa_id = thread.Key,
            phone = thread.Address.Length > 0 ? thread.Address : $"+{thread.Key}",
            name = thread.Name.Length > 0 ? thread.Name : $"+{thread.Key}",
            account_id = thread.AccountId?.ToString(),
            provider = account != null ? WhatsAppProviders.Of(account) : Providers.Cloud,
            ai_enabled = aiVerdict.Enabled,
            ai_reason = aiVerdict.Reason,
            ai_rule = aiVerdict.Rule,
            ai_forced = aiVerdict.Forced,
            handover = AiControlsEndpoints.HandoverPayload(thread),
            status = thread.Status,
            assignee = thread.Assignee,
            team_id = thread.TeamId,
            opted_out = thread.OptedOut,
            window_open = windowOpen,
            window_expires_at = windowExpires,
            contact_id = thread.ContactId,
            contact_name = thread.Contact?.Name ?? "",
            labels = thread.Labels,
            call = new
            {
                permission = thread.CallPermission,
                call_id = thread.CallId,
                status = thread.CallStatus,
                room = thread.CallRoom,
            },
            messages = Decorated(db, page, page.Select(m => MessagePayload(
                m, names.TryGetValue(m.AccountId ?? -1, out var n) ? n : null)).ToList()),
            has_more = hasMore,
            created = thread.CreatedAt.ToString("o"),
            updated = thread.UpdatedAt.ToString("o"),
        };
    }

    /// <summary>store._serialize_message — the bubble the chat column renders.</summary>
    private static Dictionary<string, object?> MessagePayload(Message m, ChannelAccount? account) =>
        MessagePayload(m, account?.Name);

    /// <summary>Quotes + reactions on a page of bubbles (<see cref="MessageExtras"/>).</summary>
    private static List<Dictionary<string, object?>> Decorated(AppDbContext db, List<Message> page,
        List<Dictionary<string, object?>> payloads)
    {
        MessageExtras.Decorate(db, page, payloads);
        return payloads;
    }

    private static Dictionary<string, object?> MessagePayload(Message m, string? accountName)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = m.ExternalId.Length > 0 ? m.ExternalId : $"db-{m.Id}",
            ["pk"] = m.Id,                       // stable pagination cursor + media key
            ["dir"] = m.Direction,
            ["text"] = m.Text,
            // SQLite hands DateTimes back Kind=Unspecified — without the Z the
            // browser reads UTC as local time and every bubble shifts.
            ["ts"] = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc).ToString("o"),
            ["status"] = m.Status,
            ["source"] = m.Source,
        };
        if (m.Private) payload["private"] = true;
        // Meta's reason for a failed delivery (status webhook / send error)
        if (!string.IsNullOrEmpty(m.Error)) payload["error"] = m.Error;
        // what a voice note said (speech-to-text, Ai/Voice/VoiceTranscriber.cs)
        if (!string.IsNullOrEmpty(m.Transcript)) payload["transcript"] = m.Transcript;
        if (m.AccountId is int acct)
        {
            // lets the chat UI show "via <number>" and flag mid-thread switches
            payload["account_id"] = acct.ToString();
            if (!string.IsNullOrEmpty(accountName)) payload["account_name"] = accountName;
        }
        if (m.MediaPath.Length > 0)
            payload["media"] = MediaStreamLinks.Payload(m, ChatBranch.Domain.Channels.WhatsApp);
        return payload;
    }

    // ======================= thread + account helpers ========================

    private static Task<Thread?> FindThread(AppDbContext db, string key)
    {
        var waId = NormalizeWaId(key);
        return db.Threads.Include(t => t.Contact).Include(t => t.Account)
            .FirstOrDefaultAsync(t => t.Channel == ChatBranch.Domain.Channels.WhatsApp
                                      && (t.Key == waId || t.Key == key));
    }

    /// <summary>
    /// store._live_account_lookup: the thread's own account, or — when it has
    /// none — the SOLE configured WhatsApp account (an unambiguous single-number
    /// setup shouldn't have to be pinned onto every thread).
    /// </summary>
    private static async Task<ChannelAccount?> EffectiveAccount(AppDbContext db,
        IWhatsAppCloudClient wa, Thread thread)
    {
        if (thread.AccountId is int id)
            return thread.Account ?? await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == id);
        var configured = await db.ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == ChatBranch.Domain.Channels.WhatsApp).ToListAsync();
        var usable = configured.Where(a => wa.IsConfigured(a) || a.Provider == Providers.Baileys)
            .ToList();
        return usable.Count == 1 ? usable[0] : null;
    }

    /// <summary>
    /// views.threads POST routing: an explicit account_id wins, then the
    /// contact's sticky AccountAssignment, then the sole configured number.
    /// </summary>
    private static async Task<int?> ResolveNewThreadAccount(AppDbContext db,
        IWhatsAppCloudClient wa, string waId, string? explicitId)
    {
        if (int.TryParse((explicitId ?? "").Trim(), out var pk)
            && await db.ChannelAccounts.AnyAsync(a => a.Id == pk))
            return pk;

        var contact = await MatchContact(db, waId);
        if (contact != null)
        {
            var pin = await db.AccountAssignments.AsNoTracking().FirstOrDefaultAsync(
                a => a.ContactId == contact.Id
                     && a.Channel == ChatBranch.Domain.Channels.WhatsApp);
            if (pin != null) return pin.AccountId;
        }
        var usable = (await db.ChannelAccounts.AsNoTracking()
                .Where(a => a.Channel == ChatBranch.Domain.Channels.WhatsApp).ToListAsync())
            .Where(a => wa.IsConfigured(a) || a.Provider == Providers.Baileys).ToList();
        return usable.Count == 1 ? usable[0].Id : null;
    }

    /// <summary>store._match_contact — CRM lookup by normalized phone digits.</summary>
    private static async Task<Contact?> MatchContact(AppDbContext db, string waId)
    {
        if (waId.Length < 6) return null;
        var candidates = await db.Contacts.AsNoTracking()
            .Where(c => c.Phone != "").Select(c => new { c.Id, c.Phone }).ToListAsync();
        var hit = candidates.FirstOrDefault(c => NormalizeWaId(c.Phone) == waId);
        return hit == null ? null : await db.Contacts.FirstOrDefaultAsync(c => c.Id == hit.Id);
    }

    /// <summary>The 24h window as of now, without rebuilding the whole payload.</summary>
    private static bool WindowOpen(AppDbContext db, Thread thread, ChannelAccount? account)
    {
        if (account == null || account.Provider == Providers.Baileys) return true;
        var lastIn = db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
            .Max(m => (DateTime?)m.CreatedAt);
        return lastIn is DateTime ts && DateTime.UtcNow < ts.AddHours(WindowHours);
    }

    /// <summary>
    /// store.close_window — cap inbound timestamps so the window reads closed.
    /// <paramref name="exceptAccountId"/> keeps the NEW number's own inbounds
    /// intact on an account switch (their window is genuinely open).
    /// </summary>
    private static Task CloseWindow(AppDbContext db, Thread thread, int? exceptAccountId)
    {
        WhatsAppWindow.Close(db, thread, exceptAccountId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// store._effective_ai — the Chatbot tick for the thread's CURRENT sending
    /// account (<see cref="ChatBranch.Data.Ai.AiEligibility"/>, the same rule
    /// the inbound auto-reply gate applies).
    /// </summary>
    private static bool EffectiveAi(ScopingService sc, Thread thread, ChannelAccount? account) =>
        ChatBranch.Data.Ai.AiEligibility.Effective(thread, account,
            ChatBranch.Data.Ai.AiEligibility.ReminderDefaultOn(sc, thread.EntityId));

    /// <summary>
    /// wa_config.account_role — config["role"], defaulting by provider: a Cloud
    /// business number is the compliant template cannon ("reminder"), a Baileys
    /// personal number is the AI negotiation layer ("conversation").
    /// </summary>
    private static string AccountRole(ChannelAccount account) => AccountRoles.Of(account);

    // ======================= settings helpers ================================

    /// <summary>A routing-rule Setting row (global scope), with its shipped default.</summary>
    private static string Rule(ScopingService sc, string key, string fallback)
    {
        var v = (sc.SettingValue(key) ?? "").Trim();
        return v.Length > 0 ? v : fallback;
    }

    /// <summary>store.normalize_wa_id — digits only (a wa_id is never punctuated).</summary>
    private static string NormalizeWaId(string? raw) =>
        new((raw ?? "").Where(char.IsDigit).ToArray());

    private static bool IsAdminPlus(ClaimsPrincipal me) =>
        me.IsInRole(Roles.Admin) || me.IsInRole(Roles.SuperAdmin);

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>The gateway's own call-length cap, matching the agent's MAX_CALL_SECONDS failsafe.</summary>
    private static int MaxCallSeconds() =>
        int.TryParse(Environment.GetEnvironmentVariable("MAX_CALL_SECONDS"), out var s) && s > 0 ? s : 300;

    /// <summary>One audit row; the trail must never break the write it records.</summary>
    private static async Task Audit(AppDbContext db, HttpContext ctx, ClaimsPrincipal me,
        string action, string target = "", string detail = "")
    {
        try
        {
            var username = me.Identity?.Name ?? "anonymous";
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username);
            db.AuditLogs.Add(new AuditLog
            {
                UserId = user?.Id,
                Actor = Cap(username, 150),
                Action = Cap(action, 64),
                Target = Cap(target, 190),
                Detail = Cap(detail, 1000),
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch { }
    }
}
