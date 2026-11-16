using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// <c>POST /api/messaging/notify</c> — ONE call to send a message an APP originated: a
/// login code, an order update, a payment reminder.
///
/// An order system or a shop back office is not a chat app. Before this it had to
/// <c>POST /api/messaging/threads</c> to open the conversation and then
/// <c>…/{channel}/{key}/send</c> to deliver it — two round trips — and reinvent what
/// ChatBranch already knows: that free text into a closed WhatsApp window needs a
/// template, that the cold-text guard exists, that a retried request must not send twice.
///
/// It is not a new transport and not a new guard set: it is
/// <see cref="ThreadOpen"/> + <see cref="Routing"/> + <see cref="OutboundSender"/> behind
/// one request, with idempotency added. Everything it refuses, the inbox refuses too.
///
/// Two rules are deliberate and not negotiable here:
///
/// * <b>It never answers 404.</b> A client reads 404 as "this ChatBranch has no /notify
///   yet" and falls back to the two-call path; answering 404 for anything about the
///   REQUEST would hide a real refusal behind that fallback. So an unknown entity is 422
///   and an out-of-scope one is 403.
/// * <b>Opt-out always wins.</b> There is no <c>force</c>. An app cannot judge consent;
///   an operator can still make an audited exception from the inbox, which is a person's
///   decision, on the record.
///
/// <c>purpose</c> is the one thing that changes behaviour: <c>otp</c> skips the cold-text
/// guard (the customer asked for the code seconds ago — it is the most customer-initiated
/// message there is, and blocking it breaks a login), <c>notification</c> keeps it (so a
/// shop cannot push cold bulk text through an API key and get its number banned).
/// </summary>
public static class MessagingNotifyEndpoints
{
    /// <summary>
    /// Channels an app may originate on: every channel a person can start a chat on
    /// (<c>MessagingEndpoints.ValidChannels</c>) plus WhatsApp. The website widget is
    /// absent on purpose — its conversations are keyed by a session id the server mints
    /// for a visitor, so there is no address to originate to.
    /// </summary>
    public static readonly string[] NotifyChannels =
    {
        Channels.WhatsApp, Channels.Email, Channels.Facebook, Channels.Instagram, Channels.Telegram,
        Channels.Line, Channels.Viber, Channels.Sms, Channels.WeChat, Channels.TikTok, Channels.Douyin,
        Channels.Zalo,
    };

    /// <summary>What the message is for. It decides the cold-text guard, nothing else.</summary>
    public const string PurposeOtp = "otp";
    public const string PurposeNotification = "notification";

    /// <summary>Longest text one call may carry (WhatsApp's own body limit is below this).</summary>
    public const int MaxTextLength = 4096;

    /// <summary>Longest caller key; it is an opaque string, so only the length is ours to cap.</summary>
    public const int MaxIdempotencyKeyLength = 128;

    public static void MapMessagingNotifyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/messaging/notify", async (JsonElement body, HttpContext ctx, AppDbContext db,
            ThreadStore store, ScopingService sc, OutboundSender sender, IWhatsAppCloudClient wa,
            WhatsAppTemplateCatalog catalog, ClaimsPrincipal me) =>
        {
            // ---- 1. the request ------------------------------------------------
            var channel = (Str(body, "channel") ?? "").Trim().ToLowerInvariant();
            if (!NotifyChannels.Contains(channel))
                return Fail(422, "bad_channel",
                    $"channel must be one of {string.Join(", ", NotifyChannels)}.");

            var to = (Str(body, "to") ?? "").Trim();
            // phone-keyed channels store the digits as the conversation key, as the
            // webhooks do — so "+60 12-345 6789" and "60123456789" are one conversation
            if (ThreadOpen.PhoneKeyed(channel)) to = PhoneMatch.Digits(to);
            if (to.Length == 0 || (channel == Channels.Email && !to.Contains('@')))
                return Fail(422, "bad_recipient",
                    channel == Channels.Email
                        ? "to must be an email address."
                        : "to is required (a phone number's digits, or the channel's chat id).");

            var text = (Str(body, "text") ?? "").Trim();
            var template = Template(body);
            if (text.Length == 0 && template == null)
                return Fail(422, "nothing_to_send", "Give text, or a template, or both.");
            if (text.Length > MaxTextLength)
                return Fail(422, "nothing_to_send", $"text is longer than {MaxTextLength} characters.");

            var purpose = (Str(body, "purpose") ?? PurposeNotification).Trim().ToLowerInvariant();
            if (purpose.Length == 0) purpose = PurposeNotification;
            if (purpose is not (PurposeOtp or PurposeNotification))
                return Fail(422, "bad_purpose", $"purpose must be \"{PurposeOtp}\" or \"{PurposeNotification}\".");

            if (template != null && channel != Channels.WhatsApp)
                return Fail(422, "template_unsupported", "Only WhatsApp has templates.");

            var key = (Str(body, "idempotency_key") ?? "").Trim();
            if (key.Length > MaxIdempotencyKeyLength)
                return Fail(422, "bad_idempotency_key",
                    $"idempotency_key is longer than {MaxIdempotencyKeyLength} characters.");
            if (key.Any(c => c < 0x20 || c > 0x7e))
                return Fail(422, "bad_idempotency_key", "idempotency_key must be printable ASCII.");

            // ---- 2. already done? ----------------------------------------------
            // A retried request reads its own message back and sends nothing. The key is
            // global on purpose: callers prefix theirs ("onel-otp-…"), and two apps
            // colliding is a caller bug where the second legitimately reads the first's.
            if (key.Length > 0 && await ByKeyAsync(db, key) is { } already)
                return Results.Ok(await PayloadAsync(db, already, idempotent: true));

            // ---- 3. the conversation -------------------------------------------
            var opened = await ThreadOpen.FindOrOpenAsync(db, store, sc, me, channel, to,
                IntOrNull(body, "thread_id"), Str(body, "company"), Str(body, "entity"), Str(body, "name"));
            if (!opened.Ok) return OpenRefusal(opened);
            var thread = opened.Thread!;
            // the conversation is named in the BODY, so the inbox's entity scope is checked
            // here rather than by the route filter (as POST /api/aicore/reply does)
            if (!ThreadAccessFilter.InScope(db, sc, me, thread))
                return Fail(403, "out_of_scope", "That conversation is outside your scope.");

            // a brand-new conversation may be opened with the chatbot off: a shop that only
            // sends codes does not necessarily want it answering someone who replies "?"
            if (opened.Started && AiTick(body) is bool want)
            {
                ChatBranch.Data.Ai.AiEligibility.Set(thread, want);
                await db.SaveChangesAsync();
            }

            // ---- 4. opt-out, always ---------------------------------------------
            if (thread.OptedOut)
                return Fail(409, "opted_out",
                    $"This contact sent STOP on {channel}, so ChatBranch will not message them there. "
                    + "Use another channel, or have them send START.");

            // ---- 5. the number to send from --------------------------------------
            if (!ShellScope.AccountAllowed(db, me, IntOrNull(body, "account_id")))
                return Fail(403, "out_of_scope", "That channel isn't one of yours — ask a manager for access.");
            var sendingTemplate = template != null;
            var routing = new Routing(db, sc);
            var route = routing.ForThread(thread, RouteNeed.For(channel, template: sendingTemplate),
                preferredAccountId: IntOrNull(body, "account_id"), apply: true);
            if (!route.Ok) return Fail(409, "no_account", route.Error);
            var account = route.Account;
            var live = account != null && channel == Channels.WhatsApp && wa.IsConfigured(account);

            // ---- 6. the cold-text guard (notification only) ----------------------
            var coldNote = "";
            if (purpose == PurposeNotification && text.Length > 0 && !sendingTemplate
                && account != null && AccountRole(account) == "conversation"
                && !await db.Messages.AnyAsync(m => m.ThreadId == thread.Id
                                                    && m.Direction == MessageDirection.In))
            {
                var guard = Guard(sc, thread);
                if (guard == "block")
                    return Fail(409, "cold_text",
                        "This number has never received a message from this contact. Cold free text from a "
                        + "personal number carries ban risk — send a template instead, or have the contact "
                        + "message first. (purpose=\"otp\" is exempt: a code the customer just asked for.)");
                if (guard == "warn") coldNote = ColdWarnNote;
            }

            // ---- 7. WhatsApp's 24-hour window ------------------------------------
            // Only a LIVE official number has one; Baileys and sandbox read as open.
            var windowShut = live && !WindowOpen(db, thread, account);
            if (windowShut)
            {
                if (template == null)
                    return Fail(409, "window_closed", "WhatsApp 24h window closed — use a template.");
                if (!TemplateFallback(body))
                    return Fail(409, "window_closed",
                        "WhatsApp 24h window closed, and template_fallback is false — "
                        + "nothing was sent. Allow the fallback, or send the template yourself.");
                sendingTemplate = true;
            }
            else if (template != null && text.Length > 0)
            {
                // the window is open: free text is cheaper and carries no approval
                // surprises, so the template stays unused
                sendingTemplate = false;
            }

            // a live number must still hold the template's name
            if (sendingTemplate && !live && await catalog.FindAsync(account, template!.Name, template.Language) == null)
                return Fail(422, "unknown_template", $"Unknown sandbox template '{template.Name}'.");

            // ---- 8. send ----------------------------------------------------------
            // MessageSource.System, never Agent: nobody typed this, so it must not run
            // AiControlsEndpoints.HumanReplied — an order update is not a reply and must
            // never close an open handover or stand the AI down. The status is NOT touched
            // either: a transactional send neither resolves nor reopens a conversation.
            Message msg;
            try
            {
                using (store.ClaimIdempotency(key))
                    msg = sendingTemplate
                        ? sender.SendTemplate(thread, template!.Name, template.Language, template.Args,
                            MessageSource.System, account?.Id)
                        : sender.Send(thread, text, MessageSource.System, account?.Id,
                            subject: Str(body, "subject"));
            }
            catch (DbUpdateException) when (key.Length > 0)
            {
                // Two identical requests at once: the unique index refused the second row.
                // Answer with the first one's message rather than storing a duplicate.
                Detach(db);
                return await ByKeyAsync(db, key) is { } raced
                    ? Results.Ok(await PayloadAsync(db, raced, idempotent: true))
                    : Fail(409, "in_flight",
                        "The same idempotency_key is being sent right now — retry in a moment.");
            }

            if (coldNote.Length > 0)
                store.AppendMessage(thread, MessageDirection.Out, MessageSource.System, coldNote,
                    status: msg.Status, accountId: account?.Id);

            AuditTrail.Add(db, "message.notify", $"{channel}:{thread.Key}",
                $"{purpose}: {Cap(sendingTemplate ? $"template:{template!.Name}" : text, 200)}");
            await db.SaveChangesAsync();

            return Results.Ok(await PayloadAsync(db, msg, idempotent: false, sentAs: sendingTemplate ? "template" : "text"));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send");
    }

    private const string ColdWarnNote =
        "[Cold outbound: this contact has never messaged this number. Ban risk is "
        + "higher on first contact.]";

    /// <summary>A refusal body: human <c>detail</c> plus the <c>code</c> a client switches on.</summary>
    private static IResult Fail(int status, string code, string detail) =>
        Results.Json(new { detail, code }, statusCode: status);

    /// <summary>
    /// The conversation could not be resolved or opened. Mapped so this route NEVER
    /// answers 404 (§4.1): a client reads 404 as "no such endpoint".
    /// </summary>
    private static IResult OpenRefusal(ThreadOpen.Result opened) => opened switch
    {
        { Pick: ThreadResolver.Outcome.Ambiguous } => Results.Json(new
        {
            detail = "This customer has a conversation in more than one of your companies — "
                     + "name one with thread_id or company.",
            code = "company_required",
            candidates = opened.Candidates,
        }, statusCode: 409),
        { Placement: ThreadStart.Why.UnknownEntity } => Fail(422, "unknown_entity",
            "No entity or company of that name."),
        { Placement: ThreadStart.Why.OutOfScope } => Fail(403, "out_of_scope",
            "That entity or company is outside your scope."),
        { Placement: ThreadStart.Why.CompanyRequired } => Fail(409, "company_required",
            "You work for more than one company — say which one with company or entity."),
        // the resolver hid a conversation of the caller's own company that lives in a
        // branch they may not open: a scope refusal, never a 404 here
        _ => Fail(403, "out_of_scope", "That conversation is outside your scope."),
    };

    /// <summary>The message a key already named, or null. Only an OUTBOUND row counts.</summary>
    private static Task<Message?> ByKeyAsync(AppDbContext db, string key) =>
        db.Messages.AsNoTracking().Include(m => m.Thread).Include(m => m.Account)
            .Where(m => m.IdempotencyKey == key && m.Direction == MessageDirection.Out)
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync();

    /// <summary>
    /// Leave nothing half-added behind after a rejected insert, so the next read of the
    /// context is clean (the same care <see cref="NotificationService"/> takes).
    /// </summary>
    private static void Detach(AppDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            try { entry.State = EntityState.Detached; } catch { }
    }

    private static async Task<object> PayloadAsync(AppDbContext db, Message msg, bool idempotent,
        string? sentAs = null)
    {
        var thread = msg.Thread ?? await db.Threads.AsNoTracking().FirstAsync(t => t.Id == msg.ThreadId);
        var account = msg.Account ?? (msg.AccountId is int id
            ? await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id)
            : null);
        var company = thread.CompanyEntityId is int cid
            ? await db.Entities.AsNoTracking().Where(e => e.Id == cid)
                .Select(e => new { id = e.Id, name = e.Name }).FirstOrDefaultAsync()
            : null;
        return new
        {
            message = new
            {
                id = msg.Id,
                external_id = msg.ExternalId,
                // the truth, never relabelled: "sandbox" means nothing left the building
                status = msg.Status,
                error = msg.Error,
                source = msg.Source,
                created_at = msg.CreatedAt,
            },
            thread = new
            {
                thread_id = thread.Id,
                channel = thread.Channel,
                key = thread.Key,
                company,
                url = $"/chats?thread={thread.Channel}:{thread.Key}",
            },
            account = account == null
                ? null
                : new { id = account.Id, name = account.Name, provider = account.Provider },
            // on an idempotent replay this is what the FIRST call sent
            sent_as = sentAs ?? (msg.Text.Length > 0 ? "text" : "template"),
            idempotent,
        };
    }

    /// <summary>The template to send, or null when the request named none.</summary>
    private static TemplateRequest? Template(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("template", out var t) || t.ValueKind != JsonValueKind.Object)
            return null;
        var name = (Str(t, "name") ?? "").Trim();
        if (name.Length == 0) return null;
        return new TemplateRequest(name, (Str(t, "language") ?? "").Trim(),
            new TemplateArgs(Strings(t, "variables"), Strings(t, "header_variables"),
                Strings(t, "button_variables"), Str(t, "media_url") ?? ""));
    }

    private sealed record TemplateRequest(string Name, string Language, TemplateArgs Args);

    /// <summary>Default true: a template given with a closed window is there to be used.</summary>
    private static bool TemplateFallback(JsonElement body) =>
        !(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("template_fallback", out var f)
          && (f.ValueKind == JsonValueKind.False
              || (f.ValueKind == JsonValueKind.String && f.GetString() is "false" or "0")));

    /// <summary>The chatbot tick a NEW conversation opens with: null = the inbox's default.</summary>
    private static bool? AiTick(JsonElement body) =>
        (Str(body, "ai") ?? "").Trim().ToLowerInvariant() switch
        {
            "on" or "true" => true,
            "off" or "false" => false,
            _ => null,
        };

    /// <summary>The cold-text guard for the conversation's own outlet (block | warn | allow).</summary>
    private static string Guard(ScopingService sc, Thread thread)
    {
        var v = (sc.SettingValue("cold_text_guard", thread.EntityId) ?? "").Trim().ToLowerInvariant();
        return v.Length > 0 ? v : "block";
    }

    /// <summary>A reminder (business) number or a conversation (personal) one.</summary>
    private static string AccountRole(ChannelAccount account) =>
        account.Config.TryGetValue("role", out var raw) && (raw?.ToString() ?? "") is { Length: > 0 } role
            ? role
            : WhatsAppProviders.IsOfficial(account) ? "reminder" : "conversation";

    /// <summary>The 24h window as of now — Baileys, WAHA and sandbox have none.</summary>
    private static bool WindowOpen(AppDbContext db, Thread thread, ChannelAccount? account)
    {
        // only an OFFICIAL number is bound by Meta's window (Baileys / WAHA are WhatsApp Web)
        if (!WhatsAppProviders.WindowApplies(account)) return true;
        var lastIn = db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
            .Max(m => (DateTime?)m.CreatedAt);
        return lastIn is DateTime ts && DateTime.UtcNow < ts.AddHours(24);
    }

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.ToString()
            : null;

    private static int? IntOrNull(JsonElement body, string key)
    {
        var raw = Str(body, key);
        return int.TryParse(raw, out var n) && n > 0 ? n : null;
    }

    private static List<string> Strings(JsonElement body, string key)
    {
        var list = new List<string>();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var arr)
            && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                list.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString());
        return list;
    }

    private static string Cap(string s, int max) => s.Length > max ? s[..max] : s;
}
