using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Workers;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// WhatsApp through <b>WAHA</b> — the operator's own self-hosted WhatsApp HTTP API
/// (<c>github.com/devlikeapro/waha</c>). An UNOFFICIAL transport, the same category as
/// Baileys: no templates, no Meta 24-hour window, no reminder role, and using it may breach
/// WhatsApp's Terms of Service with a real risk of the number being banned. Nothing here
/// runs a container of ours — the operator hosts WAHA and gives us a base URL, a session
/// name and (optionally) an API key.
///
/// <list type="table">
///   <item><term><c>POST /api/whatsapp/accounts/waha</c></term>
///         <description>Create the inbox with its connection settings. The provider-agnostic
///         parts (role, team, AI default, quiet hours…) are then saved through the shared
///         <c>POST /api/whatsapp/accounts/{id}</c>, exactly like every other number.</description></item>
///   <item><term><c>PUT /api/whatsapp/accounts/{id}/waha</c></term>
///         <description>Edit the base URL / session / engine / API key / webhook key. A
///         masked value (<c>••</c>) means "unchanged"; a credential change resets health
///         and re-probes.</description></item>
///   <item><term><c>POST /api/whatsapp/accounts/{id}/waha/start</c></term>
///         <description>Pair. Ensures WAHA holds the session with THIS account's webhook and
///         HMAC key (minted here when blank), starts it, and answers the session state —
///         with the QR to scan, or, with <c>{"phone": "+60123456789"}</c>, the 8-character
///         pairing code to type under WhatsApp → Linked devices.</description></item>
///   <item><term><c>GET /api/whatsapp/accounts/{id}/waha/status</c></term>
///         <description>The session state the pairing panel polls (WAHA's QR codes expire in
///         seconds, so a fresh one rides on every read).</description></item>
///   <item><term><c>POST /api/whatsapp/accounts/{id}/waha/logout</c></term>
///         <description>Log the number out of WAHA and stop the session.</description></item>
///   <item><term><c>POST /api/messaging/webhook/whatsapp-waha/{id}</c></term>
///         <description>Inbound. <c>X-Webhook-Hmac</c> (hex HMAC-SHA512 of the raw body) must
///         match this account's key: no key configured ⇒ 503, bad signature ⇒ 401, a stale
///         signed envelope ⇒ 401, an unknown account ⇒ 404. Verified traffic runs the ONE
///         inbound path (<see cref="InboundProcessor.ProcessInbound"/>, which is
///         <c>ThreadStore</c>) with WhatsApp's own message id, so a WAHA retry costs
///         nothing.</description></item>
/// </list>
/// </summary>
public static class WahaEndpoints
{
    private const int MaxBody = 2 * 1024 * 1024;
    private const string Capability = "channels.manage";

    /// <summary>How long Pair waits for a brand-new session to leave STARTING before asking
    /// WhatsApp for a pairing code (one attempt per number is a scarce thing).</summary>
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(12);

    public static void MapWahaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/messaging/webhook/whatsapp-waha/{id:int}", Inbound).AllowAnonymous();

        app.MapPost("/api/whatsapp/accounts/waha", Create)
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);
        app.MapPut("/api/whatsapp/accounts/{id:int}/waha", Update)
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);
        app.MapPost("/api/whatsapp/accounts/{id:int}/waha/start", Start)
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);
        app.MapGet("/api/whatsapp/accounts/{id:int}/waha/status", Status)
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.view");
        app.MapPost("/api/whatsapp/accounts/{id:int}/waha/logout", Logout)
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(Capability);
    }

    // ---- small helpers ----------------------------------------------------------------

    private static async Task<JsonElement> ReadBody(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            return doc.RootElement.Clone();
        }
        catch { return default; }
    }

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => v.ToString(),
            }
            : null;

    private static bool Has(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out _);

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);

    private static Task<ChannelAccount?> WahaAccount(AppDbContext db, int id) =>
        db.ChannelAccounts.Include(a => a.Entity)
            .FirstOrDefaultAsync(a => a.Id == id && a.Channel == Channels.WhatsApp && a.Provider == Providers.Waha);

    private static IResult NotFound() =>
        Results.Json(new { detail = "No WAHA number with that id." }, statusCode: 404);

    /// <summary>A fresh webhook HMAC key — WAHA signs with it, we verify with it, nobody types it.</summary>
    private static string NewSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>Where WAHA should post this account's events, from the API's own public address.</summary>
    private static string WebhookUrl(ChannelAccount a, ScopingService sc, HttpContext ctx)
    {
        var b = MessagingAccountTransport.PublicBaseUrl(null, sc, ctx);
        return b.Length == 0 ? "" : b + WhatsAppProviders.WebhookPath(a);
    }

    /// <summary>
    /// What the pairing panel renders. WAHA's own status word is passed through unchanged —
    /// the UI must not have its own idea of the lifecycle. Secrets never appear.
    /// </summary>
    private static object StatePayload(ChannelAccount a, WahaSession s, string webhookUrl) => new
    {
        status = s.Status,
        // the pairing states are PROGRESS, not a fault: the panel keeps waiting on them
        transient = WahaStatus.Transient(s.Status),
        working = s.Working,
        reachable = s.Reachable,
        qr = s.Qr.Length > 0 ? s.Qr : null,
        pairing_code = s.PairingCode.Length > 0 ? s.PairingCode : null,
        pairing_phone = s.PairingPhone.Length > 0 ? s.PairingPhone : null,
        // PASSKEY_CONFIRMATION_REQUIRED: a number to CONFIRM on the phone, not to type as a code
        confirm_code = s.ConfirmCode.Length > 0 ? s.ConfirmCode : null,
        me = s.Me.Length > 0 ? s.Me : null,
        me_name = s.MeName.Length > 0 ? s.MeName : null,
        detail = s.Detail.Length > 0 ? s.Detail : null,
        session = WahaConfig.Session(a),
        engine = WahaConfig.Engine(a) is { Length: > 0 } e ? e : null,
        // operator-entered, not a secret: the drawer prefills the connection form from it
        base_url = WahaConfig.BaseUrl(a) is { Length: > 0 } b ? b : null,
        webhook_url = webhookUrl.Length > 0 ? webhookUrl : null,
        webhook_configured = a.AppSecret.Length > 0,
        api_key_set = a.AccessToken.Length > 0,
    };

    /// <summary>
    /// A WORKING session names the number: store it as the account's ExternalId the first
    /// time, the way LINE, Viber and Zalo learn their own ids.
    /// </summary>
    private static bool LearnNumber(ChannelAccount a, string digits)
    {
        if (digits.Length == 0 || a.ExternalId == digits) return false;
        a.ExternalId = digits;
        return true;
    }

    // ---- the account ------------------------------------------------------------------

    /// <summary>Read the five connection values off a body; null = not sent, "" = cleared.</summary>
    private static IResult? ApplyConnection(ChannelAccount row, JsonElement body, out bool credsChanged)
    {
        credsChanged = false;
        if (Has(body, WahaConfig.BaseUrlKey))
        {
            if (!WahaConfig.ValidBaseUrl(Str(body, WahaConfig.BaseUrlKey), out var url))
                return Bad("base_url must be the http(s) address WAHA listens on, for example "
                           + "https://waha.example.com or http://waha:3000.");
            credsChanged |= WahaConfig.BaseUrl(row) != url;
            Cfg(row, WahaConfig.BaseUrlKey, url);
        }
        if (Has(body, WahaConfig.SessionKey))
        {
            if (!WahaConfig.ValidSession(Str(body, WahaConfig.SessionKey), out var session))
                return Bad("session must be a short name of letters, digits, '-', '_' or '.' — WAHA's own default "
                           + "is \"default\".");
            credsChanged |= WahaConfig.Session(row) != session;
            Cfg(row, WahaConfig.SessionKey, session);
        }
        if (Has(body, WahaConfig.EngineKey))
        {
            if (!WahaConfig.ValidEngine(Str(body, WahaConfig.EngineKey), out var engine))
                return Bad($"engine, when given, is one of {string.Join(", ", WahaConfig.Engines)} — it is chosen on "
                           + "the WAHA side (WHATSAPP_DEFAULT_ENGINE) and recorded here for reference only.");
            Cfg(row, WahaConfig.EngineKey, engine);
        }
        // the API key and the webhook key: a masked placeholder means "leave it alone"
        if (Str(body, "api_key") is { } key && !key.Contains("••"))
        {
            credsChanged |= row.AccessToken != key.Trim();
            row.AccessToken = key.Trim();
        }
        if (Str(body, "webhook_secret") is { } secret && !secret.Contains("••"))
        {
            credsChanged |= row.AppSecret != secret.Trim();
            row.AppSecret = secret.Trim();
        }
        return null;
    }

    private static void Cfg(ChannelAccount row, string key, string value) =>
        row.Config = new Dictionary<string, object?>(row.Config ?? new()) { [key] = value };

    /// <summary>
    /// Two inboxes on the same WAHA base URL + session would overwrite each other's webhook on
    /// that session — the second one silently stealing the first one's messages. Refused.
    /// </summary>
    private static IResult? DuplicateSession(AppDbContext db, ChannelAccount row)
    {
        var url = WahaConfig.BaseUrl(row);
        var session = WahaConfig.Session(row);
        if (url.Length == 0) return null;
        var clash = db.ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == Channels.WhatsApp && a.Provider == Providers.Waha && a.Id != row.Id)
            .ToList()
            .FirstOrDefault(a => WahaConfig.BaseUrl(a).Equals(url, StringComparison.OrdinalIgnoreCase)
                                 && WahaConfig.Session(a).Equals(session, StringComparison.OrdinalIgnoreCase));
        return clash == null ? null
            : Bad($"\"{clash.Name}\" already uses the session \"{session}\" on that WAHA server. "
                  + "One session carries one number — give this inbox its own session name.");
    }

    private static async Task<IResult> Create(HttpContext ctx, AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        var body = await ReadBody(ctx);
        var name = (Str(body, "name") ?? "").Trim();
        if (name.Length == 0) return Bad("name is required for a WAHA number.");
        if (!Has(body, WahaConfig.BaseUrlKey) || (Str(body, WahaConfig.BaseUrlKey) ?? "").Trim().Length == 0)
            return Bad("base_url is required: the http(s) address of the WAHA server you run.");

        var row = new ChannelAccount
        {
            Channel = Channels.WhatsApp,
            Provider = Providers.Waha,
            Name = name,
            ExternalId = "",            // learned from WAHA when the session reaches WORKING
            WabaId = "",
            AccessToken = "",
            AppSecret = "",
            ApiVersion = "",
            Owner = me.Identity?.Name ?? "admin",
            // an unofficial personal line is a CONVERSATION number, never the business one
            Config = new Dictionary<string, object?> { ["role"] = AccountRoles.Conversation },
            EntityId = await InboxEndpoints.EntityId(db, Str(body, "entity")),
            CreatedAt = DateTime.UtcNow,
        };
        if (ShellScope.CompanyLevelError(db, sc, me, row.EntityId) is { } outside) return outside;
        if (ApplyConnection(row, body, out _) is { } err) return err;
        if (InboxSettings.Apply(db, row, Str(body, InboxSettings.TeamKey), Str(body, InboxSettings.AiDefaultKey),
                Str(body, InboxSettings.ReviewModeKey), Str(body, InboxSettings.KeepRecalledKey)) is string inboxErr)
            return Bad(inboxErr);

        if (DuplicateSession(db, row) is { } clash) return clash;
        db.ChannelAccounts.Add(row);
        await db.SaveChangesAsync();
        // an unpaired number must not sit "unknown" (= eligible) while least-loaded routing
        // hands it real customers: probe it now, so a 404 / STOPPED session reads disconnected
        _ = ctx.RequestServices.GetRequiredService<HealthRecheck>().Schedule(row.Id);
        AuditTrail.Write(db, "account.create", $"whatsapp:{row.Name}",
            $"WAHA (unofficial), session {WahaConfig.Session(row)}",
            actor: me.Identity?.Name, source: AuditSources.User, entityId: row.EntityId);
        var saved = await db.ChannelAccounts.AsNoTracking().Include(a => a.Entity).FirstAsync(a => a.Id == row.Id);
        return Results.Ok(WhatsAppSettingsEndpoints.AccountPayload(saved));
    }

    private static async Task<IResult> Update(int id, HttpContext ctx, AppDbContext db, ScopingService sc,
        ClaimsPrincipal me)
    {
        var row = await WahaAccount(db, id);
        if (row == null) return NotFound();
        if (ShellScope.CompanyLevelError(db, sc, me, row.EntityId) is { } outside) return outside;
        var body = await ReadBody(ctx);
        if (ApplyConnection(row, body, out var credsChanged) is { } err) return err;
        if (DuplicateSession(db, row) is { } clash) return clash;
        await db.SaveChangesAsync();
        if (credsChanged)
        {
            // a new address / key / session is a new connection: forget the stale verdict and re-probe
            AccountHealth.SetStatus(db, row, Health.Unknown, "WAHA connection settings changed", actor: "user");
            await db.SaveChangesAsync();
            _ = ctx.RequestServices.GetRequiredService<HealthRecheck>().Schedule(row.Id);
        }
        AuditTrail.Write(db, "account.update", $"whatsapp:{row.Name}",
            credsChanged ? "WAHA connection settings changed" : "WAHA settings saved",
            actor: me.Identity?.Name, source: AuditSources.User, entityId: row.EntityId);
        var saved = await db.ChannelAccounts.AsNoTracking().Include(a => a.Entity).FirstAsync(a => a.Id == row.Id);
        return Results.Ok(WhatsAppSettingsEndpoints.AccountPayload(saved));
    }

    // ---- pairing ----------------------------------------------------------------------

    private static async Task<IResult> Start(int id, HttpContext ctx, AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, WahaWhatsAppClient waha)
    {
        var row = await WahaAccount(db, id);
        if (row == null) return NotFound();
        if (ShellScope.ObjectScopeError(db, sc, me, row.EntityId) is { } outside) return outside;
        if (!WhatsAppProviders.Configured(row))
            return Bad("Set the WAHA base URL and session name before pairing.");

        var body = await ReadBody(ctx);
        var phoneRaw = (Str(body, "phone") ?? "").Trim();
        var phone = "";
        if (phoneRaw.Length > 0)
        {
            phone = WhatsAppProviders.Digits(phoneRaw);
            if (phone.Length is < 7 or > 15 || phone.StartsWith('0'))
                return Bad("phone must be the number's full international form with its country code "
                           + "(for example +60123456789) — digits only, no leading 0.");
        }

        // WAHA signs its webhooks with a key we choose; mint one the first time so inbound is
        // never accepted unsigned, and hand it to the session with the URL.
        var minted = false;
        if (row.AppSecret.Length == 0)
        {
            row.AppSecret = NewSecret();
            minted = true;
            await db.SaveChangesAsync();
        }
        var url = WebhookUrl(row, sc, ctx);
        if (url.Length == 0)
            return Bad("This API has no public address to give WAHA. Set public_base_url (Settings → App & "
                       + "provider keys) or PUBLIC_BASE_URL to an address your WAHA server can reach.");

        var state = await waha.StartAsync(row, url, row.AppSecret, ctx.RequestAborted);
        if (phone.Length > 0 && !state.Working)
        {
            // A session WAHA has only just created is STARTING: it has no socket yet, so a code
            // asked for now is simply refused — and WhatsApp rate-limits codes PER NUMBER, so a
            // wasted attempt costs the person a real one. Wait for the socket, then ask once.
            if (state.Status == WahaStatus.Starting)
                state = await waha.SettleAsync(row, SettleBudget, withQr: true, ctx.RequestAborted);
            if (!state.Working)
            {
                var (code, detail) = await waha.RequestPairingCodeAsync(row, phone, ctx.RequestAborted);
                // the QR stays as the fallback when WhatsApp refuses to issue one
                if (code.Length > 0) state = state with { PairingCode = code, PairingPhone = phone, Detail = "" };
                else if (state.Qr.Length == 0) state = state with { Detail = detail };
            }
        }
        if (LearnNumber(row, state.Me)) await db.SaveChangesAsync();
        // the code is a credential for the length of the attempt: the audit row never carries it
        AuditTrail.Write(db, "account.waha_start", $"whatsapp:{row.Name}",
            phone.Length > 0 ? $"{state.Status} (by phone number)" : state.Status
                + (minted ? "; webhook key minted" : ""),
            actor: me.Identity?.Name, source: AuditSources.User, entityId: row.EntityId);
        return Results.Ok(StatePayload(row, state, url));
    }

    private static async Task<IResult> Status(int id, HttpContext ctx, AppDbContext db, ScopingService sc,
        WahaWhatsAppClient waha)
    {
        var row = await WahaAccount(db, id);
        if (row == null) return NotFound();
        if (!WhatsAppProviders.Configured(row))
            return Results.Ok(StatePayload(row, new WahaSession(WahaStatus.Unknown,
                Detail: "Set the WAHA base URL and session name first."), ""));
        var state = await waha.StatusAsync(row, withQr: true, ctx.RequestAborted);
        if (LearnNumber(row, state.Me)) await db.SaveChangesAsync();
        return Results.Ok(StatePayload(row, state, WebhookUrl(row, sc, ctx)));
    }

    private static async Task<IResult> Logout(int id, HttpContext ctx, AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, WahaWhatsAppClient waha)
    {
        var row = await WahaAccount(db, id);
        if (row == null) return NotFound();
        if (ShellScope.ObjectScopeError(db, sc, me, row.EntityId) is { } outside) return outside;
        var state = await waha.LogoutAsync(row, ctx.RequestAborted);
        AuditTrail.Write(db, "account.waha_logout", $"whatsapp:{row.Name}", state.Status,
            actor: me.Identity?.Name, source: AuditSources.User, entityId: row.EntityId);
        return Results.Ok(StatePayload(row, state, WebhookUrl(row, sc, ctx)));
    }

    // ---- inbound ----------------------------------------------------------------------

    private static async Task<byte[]?> RawBodyAsync(HttpContext ctx)
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

    /// <summary>A detached copy with what the background media download needs.</summary>
    private static ChannelAccount Snapshot(ChannelAccount a) => new()
    {
        Id = a.Id, Channel = a.Channel, Provider = a.Provider, ExternalId = a.ExternalId, Name = a.Name,
        AccessToken = a.AccessToken, WabaId = a.WabaId, ApiVersion = a.ApiVersion,
        Config = new Dictionary<string, object?>(a.Config ?? new()),
    };

    private static async Task<IResult> Inbound(int id, HttpContext ctx, AppDbContext db, InboundProcessor processor,
        MediaFetcher fetcher, TimeProvider clock)
    {
        var account = await db.ChannelAccounts.FirstOrDefaultAsync(
            a => a.Id == id && a.Channel == Channels.WhatsApp && a.Provider == Providers.Waha);
        if (account == null) return Results.Json(new { detail = "Unknown account." }, statusCode: 404);
        if (account.AppSecret.Length == 0)
            return Results.Json(new
            {
                detail = "The WAHA webhook HMAC key is not configured for this account — inbound messages are "
                         + "refused until it is set (press Pair, which mints one and hands it to the session).",
            }, statusCode: 503);

        var body = await RawBodyAsync(ctx);
        if (body == null) return Results.StatusCode(413);
        if (!WahaWhatsAppClient.VerifySignature(account.AppSecret, body,
                ctx.Request.Headers[WahaWhatsAppClient.SignatureHeader].ToString()))
            return Results.Json(new { detail = "Bad signature." }, statusCode: 401);

        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(body); }
        catch { return Results.Ok(new { ok = true }); }   // signed garbage: ack, never NACK
        if (!WahaWhatsAppClient.Fresh(root, clock.GetUtcNow()))
            return Results.Json(new { detail = "Stale event." }, statusCode: 401);

        try
        {
            // one WAHA can run many sessions; only this account's own traffic is ours to store
            var envelope = ProviderHttp.Str(root, "session");
            if (envelope.Length > 0
                && !envelope.Equals(WahaConfig.Session(account), StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { ok = true });
            var ev = WahaWebhook.Parse(root);
            if (ev.Message is { } m)
            {
                // a redelivered id has already run every side effect (AI, flows, notifications)
                var seen = db.Messages.Any(x => x.ExternalId == m.MessageId
                                                && x.Direction == MessageDirection.In
                                                && x.Thread!.Channel == Channels.WhatsApp);
                if (!seen)
                {
                    var msg = processor.ProcessInbound(Channels.WhatsApp, m.From, m.Text, m.Name, account.Id,
                        m.MessageId, m.Timestamp, replyToExternalId: m.ReplyTo);
                    // inbound proves the session works, so it heals the number
                    MetaErrors.MarkHealthy(db, account, "inbound message received", actor: "webhook");
                    account.LastInboundAt = DateTime.UtcNow;
                    db.SaveChanges();
                    // two-phase: the placeholder is stored now, WAHA's file attached off the ack
                    if (msg != null && m.MediaUrl.Length > 0)
                        fetcher.FetchCloudMedia(msg.Id, Snapshot(account), m.MediaUrl, m.MediaMime, m.MediaName,
                            m.MediaType);
                }
            }
            else if (ev.Status is { } st)
            {
                ApplySessionStatus(db, account, st);
            }
        }
        catch { /* WAHA must still get its 200 */ }
        return Results.Ok(new { ok = true });
    }

    /// <summary>
    /// A <c>session.status</c> push → account health through the single writer
    /// (<see cref="AccountHealth.SetStatus"/>), with the pairing states left alone: STARTING
    /// and the two PASSKEY ones are the flow working, not a fault.
    /// </summary>
    private static void ApplySessionStatus(AppDbContext db, ChannelAccount account, WahaStatusEvent st)
    {
        if (!string.Equals(st.Session, WahaConfig.Session(account), StringComparison.OrdinalIgnoreCase)) return;
        LearnNumber(account, st.Me);
        var verdict = WahaWhatsAppClient.Verdict(account,
            new WahaSession(st.Status, Me: st.Me, MeName: st.MeName, HttpStatus: 200));
        if (verdict.Status is string next && !verdict.Transient)
            AccountHealth.SetStatus(db, account, next, verdict.Detail, actor: "webhook");
        db.SaveChanges();
    }
}
