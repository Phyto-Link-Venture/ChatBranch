using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    public record SendTextRequest(string? Text, bool Force = false);

    public record SendTemplateRequest(string? Name, string? Language, List<string>? Variables);

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

    /// <summary>
    /// Sandbox template set — mirrors WhatsAppSettingsEndpoints.DemoTemplates so a
    /// template send works with zero Meta credentials (views.DEMO_TEMPLATES).
    /// </summary>
    private static readonly Dictionary<string, string> DemoTemplateBodies = new()
    {
        ["order_confirmation"] =
            "Hi {{1}}, thanks for your order {{2}}. We'll have it ready by {{3}}. " +
            "Reply here if you have any questions.",
        ["delivery_update"] =
            "Hi {{1}}, an update on {{2}}: it's on the way and should arrive on {{3}}. " +
            "Reply here if anything needs to change.",
        ["appointment_reminder"] =
            "Hi {{1}}, a reminder about {{2}} on {{3}}. Reply here to reschedule.",
    };

    private static readonly Regex PlaceholderRe = new(@"\{\{\s*(\d+)\s*\}\}", RegexOptions.Compiled);

    private const string ColdTextBlockDetail =
        "This number has never received a message from this contact. Cold free-text "
        + "from a personal number carries ban risk — open with an approved template, "
        + "or have the contact message first. Admins may override with force=true.";

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
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- start a chat ----------------------------------------------------
        // views.threads POST. The inbox's "new chat" row posts {phone}; the body
        // also accepts {key}/{channel} so the same endpoint opens a thread the
        // unified inbox already knows by key.
        app.MapPost("/api/whatsapp/threads", async (CreateThreadRequest body, AppDbContext db,
            ScopingService sc, ThreadStore store, IWhatsAppCloudClient wa) =>
        {
            var raw = (body.Phone ?? body.Key ?? "").Trim();
            if (raw.Length == 0)
                return Results.BadRequest(new { detail = "phone is required." });
            var waId = NormalizeWaId(raw);
            if (waId.Length == 0)
                return Results.BadRequest(new { detail = "phone is required." });

            var channel = string.IsNullOrWhiteSpace(body.Channel)
                ? ChatBranch.Domain.Channels.WhatsApp : body.Channel!.Trim();
            var accountId = await ResolveNewThreadAccount(db, wa, waId, body.AccountId);
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
        }).RequireAuthorization(AuthPolicies.Authenticated);

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

            var account = await EffectiveAccount(db, wa, thread);
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
                    return Results.Json(new { detail = ColdTextBlockDetail }, statusCode: 409);
                if (guard == "warn") coldNote = ColdTextWarnNote;
            }
            if (live && !WindowOpen(db, thread, account))
                return Results.Json(new
                {
                    detail = "WhatsApp 24h window closed — use a template.",
                }, statusCode: 409);

            // OutboundSender is the one live-or-sandbox seam: configured account →
            // graph.facebook.com, otherwise stored locally with status "sandbox".
            var msg = sender.Send(thread, text, MessageSource.Agent, account?.Id);
            if (coldNote.Length > 0)
                store.AppendMessage(thread, MessageDirection.Out, MessageSource.System, coldNote,
                    status: live ? "sent" : "sandbox", accountId: account?.Id);
            await Audit(db, ctx, me, "message.send", $"whatsapp:{thread.Key}", Cap(text, 200));
            return Results.Ok(new { message = MessagePayload(msg, account) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- send: template --------------------------------------------------
        // views.send_template — the only way to (re)open a conversation outside
        // the 24h window, so it deliberately does NOT check window_open.
        app.MapPost("/api/whatsapp/threads/{key}/send-template", async (string key,
            SendTemplateRequest body, AppDbContext db, ScopingService sc, OutboundSender sender,
            IWhatsAppCloudClient wa, ClaimsPrincipal me, HttpContext ctx) =>
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
            // Templates never fail over to Baileys (no template API on WhatsApp
            // Web); a Baileys account sends the rendered body as free text.
            var account = await EffectiveAccount(db, wa, thread);
            // quiet hours are a per-account setting (Settings → Channels → edit
            // account), falling back to the organization-wide value
            if (AccountDelivery.InQuietHours(account, sc))
                return Results.Json(new
                {
                    detail = "Quiet hours are in effect for this account (Settings → Channels → "
                             + "edit account) — template sends resume when they end.",
                }, statusCode: 409);
            var bodyText = DemoTemplateBodies.TryGetValue(name, out var demo) ? demo : "";
            if (bodyText.Length == 0)
            {
                bodyText = CustomTemplateBody(sc, name);
                if (bodyText.Length == 0 && !(account != null && wa.IsConfigured(account)))
                    return Results.BadRequest(new { detail = $"Unknown sandbox template '{name}'." });
            }
            var rendered = bodyText.Length > 0
                ? RenderTemplateBody(bodyText, variables)
                : $"[template: {name}]"
                  + (variables.Count > 0 ? $" — {string.Join(", ", variables)}" : "");

            // No template endpoint on IWhatsAppCloudClient in this build: a live
            // account sends the RENDERED body as text (and OutboundSender records
            // a Meta rejection honestly instead of faking success); sandbox stores
            // it locally exactly like Django's sandbox branch.
            var msg = sender.Send(thread, rendered, MessageSource.Agent, account?.Id);
            await Audit(db, ctx, me, "message.send", $"whatsapp:{thread.Key}", $"template:{name}");
            return Results.Ok(new { message = MessagePayload(msg, account) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- send: media (multipart) -----------------------------------------
        // views.send_media. The file is ALWAYS kept on disk (live or sandbox) so
        // the inbox can render the bubble either way.
        app.MapPost("/api/whatsapp/threads/{key}/send-media", async (string key, HttpContext ctx,
            AppDbContext db, ThreadStore store, IWhatsAppCloudClient wa,
            IConfiguration cfg, ClaimsPrincipal me) =>
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
            if (live && !WindowOpen(db, thread, account))
                return Results.Json(new
                {
                    detail = "WhatsApp 24h window closed — use a template.",
                }, statusCode: 409);

            var relPath = await StoreMediaFile(cfg, thread.Key, ext, upload);
            var text = caption.Length > 0 ? caption : $"[{kind.Type}]";
            // No media upload path on IWhatsAppCloudClient in this build, so the
            // row is stored locally (status "sandbox") even on a live account —
            // the bubble renders, nothing is silently reported as delivered.
            var msg = store.AppendMessage(thread, MessageDirection.Out, MessageSource.Agent, text,
                status: "sandbox", accountId: account?.Id);
            msg.MediaType = kind.Type;
            msg.MediaPath = relPath;
            await db.SaveChangesAsync();
            await Audit(db, ctx, me, "message.send", $"whatsapp:{thread.Key}",
                $"media:{kind.Type} {Cap(text, 150)}");
            return Results.Ok(new { message = MessagePayload(msg, account) });
        }).RequireAuthorization(AuthPolicies.Authenticated).DisableAntiforgery();

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
            if (thread.AccountId is int acct)
            {
                var overrides = new Dictionary<string, object?>(thread.AiOverrides)
                {
                    [acct.ToString()] = body.Enabled,
                };
                thread.AiOverrides = overrides;
            }
            thread.AiEnabled = body.Enabled;   // account-less/sandbox fallback stays in sync
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await Audit(db, ctx, me, "thread.ai_toggle", $"whatsapp:{thread.Key}",
                $"enabled={body.Enabled}");
            return Results.Ok(await ThreadPayload(db, sc, wa, thread, DefaultLimit, null));
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
            await db.SaveChangesAsync();
            await Audit(db, ctx, me, "thread.status", $"whatsapp:{thread.Key}", status);
            return Results.Ok(await ThreadPayload(db, sc, wa, thread, DefaultLimit, null));
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
        }).RequireAuthorization(AuthPolicies.AdminPlus);

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
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Recordings — STUB. Call audio is written by the Baileys gateway / call
        // bridge, neither of which exists here, and there is no
        // /api/whatsapp/calls/recording/<name> streamer to point a url at, so an
        // empty list is the honest answer (dead players would be worse).
        app.MapGet("/api/whatsapp/threads/{key}/call/recordings", async (string key, AppDbContext db) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            return Results.Ok(new { recordings = Array.Empty<object>() });
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
                    detail = "WhatsApp Cloud API calling is not available in this build (its call bridge "
                             + "was removed); calls run over a linked Baileys (WhatsApp Web) number.",
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
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // End. A live Baileys call is hung up at the gateway and its room
        // deleted (best-effort: either may already be gone); then the thread's
        // call state resets (wa_calls.end_call) and the panel closes cleanly.
        app.MapPost("/api/whatsapp/threads/{key}/call/end", async (string key, AppDbContext db,
            ThreadStore store, IWhatsAppCloudClient wa, IBaileysGateway baileys, LiveKitClient livekit,
            ClaimsPrincipal me, HttpContext ctx, CancellationToken ct) =>
        {
            var thread = await FindThread(db, key);
            if (thread == null) return Results.NotFound(new { detail = "Thread not found." });
            await Audit(db, ctx, me, "call.end", $"whatsapp:{thread.Key}");
            var wasLive = thread.CallStatus is "connecting" or "ringing" or "in_call";
            var real = !string.IsNullOrEmpty(thread.CallRoom);
            if (real)
            {
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
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
        }).RequireAuthorization(AuthPolicies.Authenticated);
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

        return new
        {
            wa_id = thread.Key,
            phone = thread.Address.Length > 0 ? thread.Address : $"+{thread.Key}",
            name = thread.Name.Length > 0 ? thread.Name : $"+{thread.Key}",
            account_id = thread.AccountId?.ToString(),
            provider = isBaileys ? Providers.Baileys : Providers.Cloud,
            ai_enabled = EffectiveAi(sc, thread, account),
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
            messages = page.Select(m => MessagePayload(
                m, names.TryGetValue(m.AccountId ?? -1, out var n) ? n : null)).ToList(),
            has_more = hasMore,
            created = thread.CreatedAt.ToString("o"),
            updated = thread.UpdatedAt.ToString("o"),
        };
    }

    /// <summary>store._serialize_message — the bubble the chat column renders.</summary>
    private static object MessagePayload(Message m, ChannelAccount? account) =>
        MessagePayload(m, account?.Name);

    private static object MessagePayload(Message m, string? accountName)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = m.ExternalId.Length > 0 ? m.ExternalId : $"db-{m.Id}",
            ["pk"] = m.Id,                       // stable pagination cursor + media key
            ["dir"] = m.Direction,
            ["text"] = m.Text,
            ["ts"] = m.CreatedAt.ToString("o"),
            ["status"] = m.Status,
            ["source"] = m.Source,
        };
        if (m.Private) payload["private"] = true;
        if (m.AccountId is int acct)
        {
            // lets the chat UI show "via <number>" and flag mid-thread switches
            payload["account_id"] = acct.ToString();
            if (!string.IsNullOrEmpty(accountName)) payload["account_name"] = accountName;
        }
        if (m.MediaPath.Length > 0)
            payload["media"] = new
            {
                type = m.MediaType.Length > 0 ? m.MediaType : "document",
                url = $"/api/whatsapp/media/{m.Id}",
            };
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
    private static async Task CloseWindow(AppDbContext db, Thread thread, int? exceptAccountId)
    {
        var cutoff = DateTime.UtcNow.AddHours(-WindowHours - 1);
        var rows = await db.Messages
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In
                        && m.CreatedAt > cutoff
                        && (exceptAccountId == null || m.AccountId != exceptAccountId))
            .ToListAsync();
        if (rows.Count == 0) return;
        foreach (var m in rows) m.CreatedAt = cutoff;
        // the SSE change token watches updated_at — a bulk edit must bump it too
        thread.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// store._effective_ai — the Chatbot tick for the thread's CURRENT sending
    /// account. An explicit per-account override wins; otherwise the account's
    /// ROLE decides (reminder off unless the reminder_ai_default rule flips it,
    /// conversation on). Account-less/sandbox threads use the legacy column.
    /// </summary>
    private static bool EffectiveAi(ScopingService sc, Thread thread, ChannelAccount? account)
    {
        if (thread.AccountId is not int acct) return thread.AiEnabled;
        if (thread.AiOverrides.TryGetValue(acct.ToString(), out var raw))
        {
            if (raw is bool b) return b;
            if (raw is JsonElement el && el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return el.GetBoolean();
        }
        if (account == null) return thread.AiEnabled;
        if (InboxSettings.AiDefault(account) is bool inboxDefault) return inboxDefault;
        if (AccountRole(account) == "reminder")
            return Rule(sc, "reminder_ai_default", "off") == "on";
        return true;
    }

    /// <summary>
    /// wa_config.account_role — config["role"], defaulting by provider: a Cloud
    /// business number is the compliant template cannon ("reminder"), a Baileys
    /// personal number is the AI negotiation layer ("conversation").
    /// </summary>
    private static string AccountRole(ChannelAccount account)
    {
        if (account.Config.TryGetValue("role", out var raw))
        {
            var role = raw is JsonElement el && el.ValueKind == JsonValueKind.String
                ? el.GetString() : raw?.ToString();
            if (role is "reminder" or "conversation") return role;
        }
        return account.Provider == Providers.Baileys ? "conversation" : "reminder";
    }

    // ======================= settings helpers ================================

    /// <summary>A routing-rule Setting row (global scope), with its shipped default.</summary>
    private static string Rule(ScopingService sc, string key, string fallback)
    {
        var v = (sc.SettingValue(key) ?? "").Trim();
        return v.Length > 0 ? v : fallback;
    }

    /// <summary>
    /// Body text of an operator-created sandbox template (wa_config.custom_templates,
    /// Setting <c>whatsapp_custom_templates</c> — the same rows Settings →
    /// Message Templates writes).
    /// </summary>
    private static string CustomTemplateBody(ScopingService sc, string name)
    {
        try
        {
            var raw = sc.SettingValue("whatsapp_custom_templates");
            if (string.IsNullOrWhiteSpace(raw)) return "";
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return "";
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                if (!t.TryGetProperty("name", out var n) || n.GetString() != name) continue;
                return t.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            }
        }
        catch { /* a malformed row degrades to "no custom template" */ }
        return "";
    }

    /// <summary>
    /// views._render_template_body — fill {{1}}/{{2}}… from the positional list.
    /// A missing value leaves the placeholder visible rather than erroring.
    /// </summary>
    private static string RenderTemplateBody(string body, List<string> variables) =>
        PlaceholderRe.Replace(body ?? "", m =>
        {
            var i = int.Parse(m.Groups[1].Value) - 1;
            return i >= 0 && i < variables.Count ? variables[i] : m.Value;
        });

    // ======================= media + misc ====================================

    /// <summary>
    /// Persist an upload under data/whatsapp/media/{wa_id}/ and return the
    /// thread-relative path stored on the Message (store.MEDIA_DIR layout).
    /// </summary>
    private static async Task<string> StoreMediaFile(IConfiguration cfg, string waId, string ext,
        IFormFile upload)
    {
        var dataDir = cfg["DataDir"] ?? Path.Combine(AppContext.BaseDirectory, "data");
        var dir = Path.Combine(dataDir, "whatsapp", "media", waId);
        Directory.CreateDirectory(dir);
        var filename = $"{Guid.NewGuid():N}{ext}";
        await using var stream = File.Create(Path.Combine(dir, filename));
        await upload.CopyToAsync(stream);
        return $"{waId}/{filename}";
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
