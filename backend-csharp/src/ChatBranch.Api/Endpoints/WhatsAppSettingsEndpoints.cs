using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The WhatsApp *configuration* surface (/api/whatsapp/{settings, routing-rules,
/// voice-settings, templates, assignments, channels/health, recovery}), ported
/// from <c>backend/whatsapp/views.py</c> + <c>wa_config.py</c> / <c>health.py</c>
/// / <c>routing.py</c> / <c>flows/queue.py</c>.
///
/// Everything here is backed by <c>Setting</c> rows (through
/// <see cref="ScopingService"/>) plus reads of ChannelAccount / AccountAssignment,
/// so SANDBOX IS MANDATORY: with zero accounts and zero Setting rows every
/// endpoint still answers with valid data — empty arrays, the shipped rule
/// defaults, the three demo templates — and never 500s. The dashboard's Settings
/// pages and the channel-health card render off these exact JSON keys.
///
/// Secrets NEVER leave the server in full: they round-trip masked
/// ("••••1234") and a value still carrying "••" on the way back in is treated
/// as "untouched" and skipped, so re-saving a form can't clobber a real key.
/// </summary>
public static class WhatsAppSettingsEndpoints
{
    // ---- global app settings (wa_config.GLOBAL_KEYS) --------------------------

    /// <summary>Every key POST/PUT /settings accepts, in wa_config's order.</summary>
    private static readonly string[] GlobalKeys =
    {
        "verify_token", "chat_model",
        "openai_api_key", "openrouter_api_key", "google_api_key", "ilmu_api_key",
        "local_base_url", "local_api_key",
        "chat_provider_pref", "quiet_start", "quiet_end",
        // the phone app's push credential: a Firebase service-account JSON
        PushSettings.FcmServiceAccountKey,
        // the deployment's ONE Zernio API key (social media — Data/Social)
        "zernio_api_key",
    };

    /// <summary>Setting key → the .env name that is its fallback (wa_config._GLOBAL_ENV).</summary>
    private static readonly Dictionary<string, string> GlobalEnv = new()
    {
        ["verify_token"] = "WHATSAPP_VERIFY_TOKEN",
        ["chat_model"] = "WHATSAPP_CHAT_MODEL",
        ["openai_api_key"] = "OPENAI_API_KEY",
        ["openrouter_api_key"] = "OPENROUTER_API_KEY",
        ["google_api_key"] = "GOOGLE_API_KEY",
        ["ilmu_api_key"] = "ILMU_API_KEY",
        ["local_base_url"] = "LOCAL_BASE_URL",
        ["local_api_key"] = "LOCAL_API_KEY",
        ["chat_provider_pref"] = "CHAT_PROVIDER",
        ["quiet_start"] = "WHATSAPP_QUIET_START",
        ["quiet_end"] = "WHATSAPP_QUIET_END",
        [PushSettings.FcmServiceAccountKey] = "FCM_SERVICE_ACCOUNT",
        ["zernio_api_key"] = "ZERNIO_API_KEY",
    };

    private static readonly string[] ChatProviders = ChatBranch.Data.Ai.LlmProviderCatalog.Ids;

    /// <summary>
    /// The value shipped in the repo/docs — anyone can read it, so it must never
    /// be the token a live webhook verifies against (wa_config.verify_token).
    /// </summary>
    private const string DefaultVerifyToken = "chatbranch-verify";

    /// <summary>
    /// The webhook reads <c>whatsapp_verify_token</c> (WebhooksEndpoints); Django
    /// stores the same value under <c>verify_token</c>. Both are written on save
    /// so a DB shared with the Python backend cannot disagree with our own webhook.
    /// </summary>
    private const string VerifyTokenKey = "whatsapp_verify_token";
    private const string VerifyTokenKeyLegacy = "verify_token";

    // ---- routing rules (wa_config.RULES_DEFAULTS / RULE_CHOICES) --------------

    private static readonly Dictionary<string, string> RuleDefaults = new()
    {
        ["ai_reply_mode"] = "queued",
        ["business_redirect"] = "true",
        ["business_redirect_target"] = "conversation",
        ["support_contact"] = "",
        ["business_redirect_template"] = "",
        ["recovery_mode"] = "one_click",
        ["recovery_template"] = "",
        ["cold_text_guard"] = "block",
        ["cold_call_guard"] = "block",
        ["reminder_ai_default"] = "off",
    };

    private static readonly Dictionary<string, string[]> RuleChoices = new()
    {
        ["ai_reply_mode"] = new[] { "queued", "instant" },
        ["business_redirect_target"] = new[] { "conversation", "support", "template" },
        ["recovery_mode"] = new[] { "one_click", "auto" },
        ["cold_text_guard"] = new[] { "block", "warn", "allow" },
        ["cold_call_guard"] = new[] { "block", "warn", "allow" },
        ["reminder_ai_default"] = new[] { "off", "on" },
    };

    // Outbound-queue pacing (flows/queue.py SETTING_DEFAULTS) — the pair the
    // AI Configuration page edits through this endpoint.
    private const int QueueMinGapDefault = 10;
    private const int QueueMaxGapDefault = 30;

    // ---- channel health / voice ----------------------------------------------

    private const string VoiceSettingsKey = "voice_twilio_settings";
    private const string EmailHealthKey = "email_health";
    private const string CheckedAtKey = "channel_health_checked_at";
    internal const string EnvAccountId = "env-default";

    /// <summary>Not routable: sends through these will fail (routing.INELIGIBLE_STATUSES).</summary>
    private static readonly string[] IneligibleStatuses = { Health.Disconnected, Health.Banned };

    // ---- sandbox templates ----------------------------------------------------

    private const string CustomTemplatesKey = "whatsapp_custom_templates";
    private const string TemplateBindingsKey = "whatsapp_template_bindings";

    /// <summary>
    /// Sandbox template set — neutral stand-ins for the WABA's real approved
    /// templates so the whole template flow works with zero Meta creds.
    /// </summary>
    private static readonly (string Name, string Language, string Category, string Status, string Body)[]
        DemoTemplates =
        {
            ("order_confirmation", "en", "UTILITY", "APPROVED",
             "Hi {{1}}, thanks for your order {{2}}. We'll have it ready by {{3}}. " +
             "Reply here if you have any questions."),
            ("delivery_update", "en", "UTILITY", "APPROVED",
             "Hi {{1}}, an update on {{2}}: it's on the way and should arrive on {{3}}. " +
             "Reply here if anything needs to change."),
            ("appointment_reminder", "en", "UTILITY", "APPROVED",
             "Hi {{1}}, a reminder about {{2}} on {{3}}. Reply here to reschedule."),
        };

    private static readonly Regex PlaceholderRe = new(@"\{\{\s*(\d+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>Meta's template-name rule (views.TEMPLATE_NAME_RE).</summary>
    private static readonly Regex TemplateNameRe = new(@"^[a-z0-9_]{1,120}$", RegexOptions.Compiled);

    private static readonly string[] TemplateCategories = { "UTILITY", "MARKETING", "AUTHENTICATION" };
    private static readonly string[] TemplateButtonTypes = { "QUICK_REPLY", "URL", "PHONE_NUMBER" };

    /// <summary>
    /// The flows-engine variable catalog (views.TEMPLATE_BINDING_KEYS) — the CRM
    /// fields a template {{n}} slot may bind to. "" = the operator fills it by hand.
    /// </summary>
    private static readonly string[] TemplateBindingKeys =
    {
        "name", "first_name", "phone", "email", "language", "company",
        "record_id", "record_type", "record_status", "record_title",
        "amount", "currency", "due_date", "days_until_due", "days_overdue",
        "tracking_url", "link",
        "business_name", "agent_name", "support_number", "support_email", "website",
        "best_contact_time", "preferred_channel", "last_contact_date", "situation",
        "whatsapp_assignment_account_number", "whatsapp_assignment_account_name",
        "account_number", "account_name",
    };

    // ---- account write surface (wa_config.ACCOUNT_KEYS / ACCOUNT_ROLES) -------

    private static readonly string[] AccountRoles = { "reminder", "conversation" };

    /// <summary>
    /// Editing any of these re-tokens the transport, so the account must go back
    /// to "unknown" health — a stale "disconnected" verdict otherwise keeps
    /// failover steering sends away from a number that now works
    /// (health.reset_on_reconfigure).
    /// </summary>
    private static readonly string[] CredentialKeys =
    {
        "access_token", "phone_number_id", "app_secret", "api_version", "provider", "waba_id",
        // YCloud / Twilio (WhatsAppProviders)
        "phone_number", "account_sid", "from_number", "messaging_service_sid", "api_key_sid", "api_key_secret",
    };

    /// <summary>Twilio's plain settings and its one Config secret (masked on the way out).</summary>
    private static readonly string[] TwilioPlainKeys = { "account_sid", "from_number", "messaging_service_sid", "api_key_sid" };
    private const string TwilioSecretKey = "api_key_secret";

    // =========================================================================

    public static void MapWhatsAppSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- global app settings ------------------------------------------
        // Reads need any authenticated role; writes are SUPER ADMIN only.
        app.MapGet("/api/whatsapp/settings", async (AppDbContext db, ScopingService sc) =>
            Results.Ok(await GlobalPayload(db, sc)))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("channels.view", "app_keys.manage");

        // Django exposes this as POST (both Settings pages call POST); PUT is
        // accepted too so either verb patches the same partial body.
        Func<HttpContext, AppDbContext, ScopingService, ClaimsPrincipal, Task<IResult>> saveSettings =
            async (ctx, db, sc, me) =>
        {
            // Settings → App & provider keys: capability-gated (superadmin always).
            if (await SettingsEndpoints.RequireCapability(db, me, "app_keys.manage") is { } denied) return denied;
            var body = await ReadBody(ctx);
            var patch = new Dictionary<string, string>();
            foreach (var key in GlobalKeys)
            {
                // untouched masked placeholders round-trip from GET — never store them
                if (TryString(body, key, out var raw) && !raw.Contains("••"))
                    patch[key] = raw;
            }

            foreach (var key in new[] { "quiet_start", "quiet_end" })
            {
                if (patch.TryGetValue(key, out var hhmm) && hhmm.Trim() != "" && ParseHhmm(hhmm) == null)
                    return Results.Json(new { detail = $"{key} must be 'HH:MM' (24h) or empty." },
                                        statusCode: 400);
            }

            var pref = (patch.GetValueOrDefault("chat_provider_pref") ?? "").Trim().ToLowerInvariant();
            if (pref != "" && !ChatProviders.Contains(pref))
                return Results.Json(new
                {
                    detail = $"chat_provider_pref must be one of {string.Join(", ", ChatProviders)} " +
                             "or empty (auto).",
                }, statusCode: 400);

            foreach (var (key, value) in patch)
            {
                // App keys are organization-wide BY DESIGN — always the global row.
                sc.SetSetting(StorageKey(key), value.Trim());
                // The LLM client resolves its pin from `chat_provider`; keep it in
                // step so saving on Settings → AI & Providers actually takes effect.
                if (key == "chat_provider_pref") sc.SetSetting("chat_provider", value.Trim().ToLowerInvariant());
                if (key == "verify_token") sc.SetSetting(VerifyTokenKeyLegacy, value.Trim());
            }

            await Audit(db, ctx, me, "settings.update", "app-keys",
                        $"keys: {string.Join(", ", patch.Keys.OrderBy(k => k))}");
            return Results.Ok(await GlobalPayload(db, sc));
        };
        app.MapPost("/api/whatsapp/settings", saveSettings).RequireAuthorization(AuthPolicies.Authenticated).OrgWideOnly();
        app.MapPut("/api/whatsapp/settings", saveSettings).RequireAuthorization(AuthPolicies.Authenticated).OrgWideOnly();

        // ---- routing rules (client operating model) ------------------------
        app.MapGet("/api/whatsapp/routing-rules", async (string? entity, AppDbContext db,
            ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            return Results.Ok(RulesPayload(sc, entityId));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(AssignmentsEndpoints.Capability);

        // PUT = the account_assignment.manage capability (matches the assignments gating).
        app.MapPut("/api/whatsapp/routing-rules", async (string? entity, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            if (await SettingsEndpoints.RequireCapability(db, me, AssignmentsEndpoints.Capability) is { } denied)
                return denied;
            var entityId = await InboxEndpoints.EntityId(db, entity);
            if (ShellScope.CompanyLevelError(db, sc, me, entityId) is { } outside) return outside;
            var body = await ReadBody(ctx);
            var patch = new Dictionary<string, string>();

            foreach (var (key, choices) in RuleChoices)
            {
                if (!Has(body, key)) continue;
                var value = (AsString(body, key) ?? "").Trim().ToLowerInvariant();
                if (!choices.Contains(value))
                    return Results.Json(new { detail = $"{key} must be one of: {string.Join(", ", choices)}." },
                                        statusCode: 400);
                patch[key] = value;
            }

            if (Has(body, "business_redirect"))
            {
                var el = body.GetProperty("business_redirect");
                if (el.ValueKind == JsonValueKind.True) patch["business_redirect"] = "true";
                else if (el.ValueKind == JsonValueKind.False) patch["business_redirect"] = "false";
                else
                {
                    var raw = (AsString(body, "business_redirect") ?? "").Trim().ToLowerInvariant();
                    if (raw is "true" or "false") patch["business_redirect"] = raw;
                    else return Results.Json(new { detail = "business_redirect must be true or false." },
                                             statusCode: 400);
                }
            }

            foreach (var key in new[] { "recovery_template", "business_redirect_template" })
            {
                if (!Has(body, key)) continue;
                if (!TryString(body, key, out var v))
                    return Results.Json(new
                    {
                        detail = $"{key} must be a template name string (empty = none).",
                    }, statusCode: 400);
                patch[key] = v.Trim();
            }

            if (Has(body, "support_contact"))
            {
                if (!TryString(body, "support_contact", out var v))
                    return Results.Json(new
                    {
                        detail = "support_contact must be a string (empty = generic wording).",
                    }, statusCode: 400);
                var trimmed = v.Trim();
                patch["support_contact"] = trimmed.Length > 200 ? trimmed[..200] : trimmed;
            }

            // STOP / START keywords (organization-wide — compliance words are not
            // per-outlet): the built-ins always apply, these ADD to them. A list
            // or a comma/newline string; stored as a JSON list of the extras.
            var keywordPatch = new Dictionary<string, string>();
            foreach (var (key, builtins) in new[]
                     {
                         (OptOut.OutKey, OptOut.DefaultOutWords), (OptOut.InKey, OptOut.DefaultInWords),
                     })
            {
                if (!Has(body, key)) continue;
                var el = body.GetProperty(key);
                var raw = el.ValueKind switch
                {
                    JsonValueKind.Array => el.GetRawText(),
                    JsonValueKind.String => el.GetString() ?? "",
                    JsonValueKind.Null => "",
                    _ => null,
                };
                if (raw == null)
                    return Results.Json(new { detail = $"{key} must be a list of words (or a comma-separated string)." },
                                        statusCode: 400);
                var extras = OptOut.ParseList(raw).Where(w => !builtins.Contains(w)).ToList();
                if (extras.Any(w => w.Length > 40))
                    return Results.Json(new { detail = $"{key}: keywords are single short words or phrases (40 characters max)." },
                                        statusCode: 400);
                keywordPatch[key] = extras.Count == 0 ? "" : JsonSerializer.Serialize(extras);
            }
            if (keywordPatch.Count > 0)
            {
                string Words(string key) => keywordPatch.TryGetValue(key, out var v) ? v : sc.SettingValue(key);
                var outs = OptOut.ParseList(Words(OptOut.OutKey)).Concat(OptOut.DefaultOutWords);
                var clash = OptOut.ParseList(Words(OptOut.InKey)).Concat(OptOut.DefaultInWords)
                    .Intersect(outs).ToList();
                if (clash.Count > 0)
                    return Results.Json(new { detail = $"A keyword can't both opt out and opt in: {string.Join(", ", clash)}." },
                                        statusCode: 400);
            }

            var queuePatch = new Dictionary<string, int>();
            foreach (var key in new[] { "queue_min_gap_s", "queue_max_gap_s" })
            {
                if (!Has(body, key)) continue;
                var n = AsInt(body, key);
                if (n is null || n.Value < 0)
                    return Results.Json(new
                    {
                        detail = $"{key} must be a whole number of seconds (0 or more).",
                    }, statusCode: 400);
                queuePatch[key] = n.Value;
            }
            if (queuePatch.Count > 0)
            {
                // cross-field check against the EFFECTIVE other bound, so patching
                // one key can't silently invert the min<=max pacing window
                var minGap = queuePatch.TryGetValue("queue_min_gap_s", out var lo)
                    ? lo : IntSetting(sc, "queue_min_gap_s", QueueMinGapDefault, entityId);
                var maxGap = queuePatch.TryGetValue("queue_max_gap_s", out var hi)
                    ? hi : IntSetting(sc, "queue_max_gap_s", QueueMaxGapDefault, entityId);
                if (minGap > maxGap)
                    return Results.Json(new { detail = "queue_min_gap_s cannot exceed queue_max_gap_s." },
                                        statusCode: 400);
            }

            try
            {
                foreach (var (key, value) in patch) sc.SetSetting(key, value, entityId);
                foreach (var (key, value) in queuePatch)
                    sc.SetSetting(key, value.ToString(), entityId);
                foreach (var (key, value) in keywordPatch) sc.SetSetting(key, value);   // org-wide
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { detail = ex.Message }, statusCode: 400);
            }

            var touched = patch.Keys.Concat(queuePatch.Keys).Concat(keywordPatch.Keys).OrderBy(k => k);
            await Audit(db, ctx, me, "settings.update", "routing-rules",
                        $"keys: {string.Join(", ", touched)}");
            return Results.Ok(RulesPayload(sc, entityId));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- voice (Twilio SIP trunk) settings -----------------------------
        // Read = any authenticated role, password masked. The trunk id itself
        // (SIP_OUTBOUND_TRUNK_ID) stays .env-only: the voice-agent worker is a
        // separate process that reads it once at boot.
        app.MapGet("/api/whatsapp/voice-settings", (ScopingService sc) =>
            Results.Ok(VoicePayload(sc)))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.view");

        // ---- approved templates --------------------------------------------
        app.MapGet("/api/whatsapp/templates", async (string? account_id, string? wa_id, int? thread_id,
            AppDbContext db, ScopingService sc, FlowEngine flows, WhatsAppTemplateCatalog catalog,
            ClaimsPrincipal me) =>
        {
            var account = await TemplateAccount(db, account_id);
            var bindings = TemplateBindings(sc, BindingsScope(account));

            // ?wa_id=… resolves each template's bound slots against that thread's
            // contact so the picker can pre-fill instead of asking the operator.
            Dictionary<string, string>? resolvedVars = null;
            var key = NormalizeWaId(wa_id);
            if (key != "")
            {
                // only THE conversation the caller means and may open (one per company; an
                // ambiguous one pre-fills nothing — never another company's customer data)
                var pick = await ChatBranch.Api.Auth.ThreadResolver.ResolveAsync(db, sc, me, Channels.WhatsApp,
                    new[] { key }, thread_id);
                var thread = pick.ThreadId is int tid
                    ? await db.Threads.AsNoTracking().Include(t => t.Contact).FirstOrDefaultAsync(t => t.Id == tid)
                    : null;
                var contact = thread != null && await WhatsAppMediaEndpoints.CanSeeThread(db, sc, me, thread)
                    ? thread.Contact : null;
                if (contact != null) resolvedVars = SafeVariables(flows, contact);
            }

            // A live WABA lists Meta's registry (GET /{waba_id}/message_templates,
            // paged — always fresh here, which also refreshes the send-path cache)
            // merged with the operator's custom ones; sandbox = demo + custom.
            var (defs, _, error) = await catalog.ListAsync(account, fresh: true);
            var templates = defs.Select(t => TemplateDto(t, bindings, resolvedVars)).ToList();
            return error == null
                ? Results.Ok(new { templates, sandbox = account == null })
                : Results.Ok(new { templates, sandbox = account == null, error = $"Meta template listing failed: {error}" });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // the chat's template picker, Templates → WhatsApp and the flow builder's template step
          .RequireAnyCapability("chats.send", "templates.manage", "flows.manage");

        // ---- contact → account assignments ---------------------------------
        app.MapGet("/api/whatsapp/assignments", async (string? channel, AppDbContext db) =>
        {
            var ch = string.IsNullOrWhiteSpace(channel) ? Channels.WhatsApp : channel.Trim();

            var counts = await db.AccountAssignments.AsNoTracking()
                .Where(a => a.Channel == ch)
                .GroupBy(a => a.AccountId)
                .Select(g => new { AccountId = g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.AccountId, x => x.N);

            var accounts = (await db.ChannelAccounts.AsNoTracking()
                    .Where(a => a.Channel == ch).OrderBy(a => a.Id).ToListAsync())
                .Select(a => new
                {
                    id = a.Id.ToString(),
                    name = a.Name,
                    provider = string.IsNullOrEmpty(a.Provider) ? Providers.Cloud : a.Provider,
                    health = a.HealthStatus,
                    contacts = counts.GetValueOrDefault(a.Id, 0),
                    role = AccountRole(a),
                }).ToList();

            // Channels with ≥1 ChannelAccount row — the dashboard's channel switcher.
            var channels = await db.ChannelAccounts.AsNoTracking()
                .Select(a => a.Channel).Distinct().OrderBy(c => c).ToListAsync();

            var rows = (await db.AccountAssignments.AsNoTracking()
                    .Include(a => a.Contact).Include(a => a.Account)
                    .Where(a => a.Channel == ch).OrderBy(a => a.ContactId).ToListAsync())
                .Select(a => new
                {
                    contact_id = a.ContactId,
                    contact_name = a.Contact?.Name ?? "",
                    phone = a.Contact?.Phone ?? "",
                    channel = a.Channel,
                    account_id = a.AccountId.ToString(),
                    account_name = a.Account?.Name ?? "",
                    pinned = a.Pinned,
                    updated_at = Iso(a.UpdatedAt),
                }).ToList();

            return Results.Ok(new { channel = ch, channels, accounts, assignments = rows });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(AssignmentsEndpoints.Capability);

        // ---- channel health monitor ----------------------------------------
        app.MapGet("/api/whatsapp/channels/health", async (string? entity, AppDbContext db,
                ScopingService sc, ClaimsPrincipal me) =>
        {
            // an entity-bound agent sees only the numbers their outlets may use
            // (their subtrees + ancestors + the org-wide ones); an out-of-scope
            // ?entity= narrows to that instead of widening
            var usable = ShellScope.BoundInheritedIds(db, sc, me);
            var entityId = ShellScope.ClampPick(ShellScope.BoundEntityIds(db, sc, me),
                await InboxEndpoints.EntityId(db, entity));
            return Results.Ok(await ChannelsHealthPayload(db, sc, entityId, usable));
        })
            .RequireAuthorization(AuthPolicies.Authenticated)
            // the Analytics health card and the Channels page
            .RequireAnyCapability("analytics.view", "channels.view");

        // POST = the dashboard card's "Check now" (channels.manage): probe the
        // accounts this caller's card shows RIGHT NOW (ChannelHealthProber — Graph
        // ping, getMe/getWebhookInfo, SMTP login; the periodic HealthProbeWorker
        // runs the same pass every WHATSAPP_HEALTH_INTERVAL s), then answer with
        // the GET payload, which is what lib/channelHealth.ts expects back —
        // scoped exactly like the GET (a bound caller: the accounts their outlets
        // may use; an out-of-scope ?entity= narrows instead of widening).
        app.MapPost("/api/whatsapp/channels/health", async (string? entity, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, ChannelHealthProber prober) =>
        {
            var usable = ShellScope.BoundInheritedIds(db, sc, me);
            var entityId = ShellScope.ClampPick(ShellScope.BoundEntityIds(db, sc, me),
                await InboxEndpoints.EntityId(db, entity));
            var probe = await HealthScopeIds(db, entityId);
            if (usable != null)
                probe = await db.ChannelAccounts.AsNoTracking()
                    .Where(a => probe.Contains(a.Id) && (a.EntityId == null || usable.Contains(a.EntityId.Value)))
                    .Select(a => a.Id).ToListAsync();
            try { await prober.RunAsync(probe, includeGlobalEmail: true, ctx.RequestAborted); }
            catch { /* a failed probe pass still answers with the current state */ }
            await Audit(db, ctx, me, "account.health_check", "channels",
                        "manual run from dashboard");
            return Results.Ok(await ChannelsHealthPayload(db, sc, entityId, usable));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");   // a probe talks to every provider

        // ---- conversation-account recovery funnel --------------------------
        // Conversation-role accounts currently banned/disconnected with at least
        // one assigned contact — the dashboard's one-click recovery list.
        app.MapGet("/api/whatsapp/recovery", async (AppDbContext db, ScopingService sc) =>
        {
            var assigned = await db.AccountAssignments.AsNoTracking()
                .Where(a => a.Channel == Channels.WhatsApp)
                .GroupBy(a => a.AccountId)
                .Select(g => new { AccountId = g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.AccountId, x => x.N);

            var down = await db.ChannelAccounts.AsNoTracking()
                .Where(a => a.Channel == Channels.WhatsApp && IneligibleStatuses.Contains(a.HealthStatus))
                .OrderBy(a => a.Id).ToListAsync();

            var routing = new Routing(db, sc);
            var accounts = down
                .Where(a => AccountRole(a) == "conversation")
                .Select(a => new
                {
                    account_id = a.Id.ToString(),
                    name = a.Name,
                    health = a.HealthStatus,
                    affected = assigned.GetValueOrDefault(a.Id, 0),
                    // what the one-click button will do for THIS account's outlet
                    recovery_mode = Recovery.Mode(sc, a.EntityId),
                    recovery_template = Recovery.Template(sc, a.EntityId),
                    can_recover = Recovery.Template(sc, a.EntityId) != ""
                        && routing.Eligible(Channels.WhatsApp, RouteNeed.Template, a.EntityId).Count > 0,
                })
                .Where(a => a.affected >= 1)
                .ToList();

            return Results.Ok(new { accounts });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // the down-number alarm on Analytics' health card and on Account assignment
          .RequireAnyCapability(AssignmentsEndpoints.Capability, "analytics.view");

        // POST (admin+): queue the recovery template for every contact assigned
        // to this down conversation account — the manual counterpart of
        // recovery_mode="auto" (health.enqueue_recovery). The jobs ride the
        // outbound queue, which routes them through a reminder/Cloud account.
        app.MapPost("/api/whatsapp/recovery/{accountId:int}", async (int accountId, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var row = await db.ChannelAccounts
                .FirstOrDefaultAsync(a => a.Id == accountId && a.Channel == Channels.WhatsApp);
            if (row == null) return Results.NotFound(new { detail = "Account not found." });
            if (ShellScope.CompanyLevelError(db, sc, me, row.EntityId) is { } outside) return outside;

            // Same path as recovery_mode = auto (Recovery.Enqueue): one template
            // job per assigned contact (wa_id-keyed, deduped per 24h, opted-out
            // skipped). Jobs carry no account — the queue routes each one AT SEND
            // TIME to a healthy Cloud number; templates never go out on Baileys.
            var result = Recovery.Enqueue(db, sc, row);
            if (result.Error != null)
                return Results.Json(new { detail = result.Error }, statusCode: 409);

            await Audit(db, ctx, me, "recovery.start", $"whatsapp:{row.Name}",
                        $"queued {result.Queued} recovery template(s)"
                        + (result.Skipped > 0 ? $", skipped {result.Skipped}" : ""));
            return Results.Ok(new { queued = result.Queued, skipped = result.Skipped, affected = result.Affected });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(AssignmentsEndpoints.Capability);

        // ---- account registry (create / edit / delete / test) ---------------
        // Reads live in InboxEndpoints (GET /accounts); every mutation is admin+.
        app.MapPost("/api/whatsapp/accounts", async (HttpContext ctx, AppDbContext db,
            ClaimsPrincipal me) =>
        {
            var body = await ReadBody(ctx);
            var provider = (AsString(body, "provider") ?? "").Trim().ToLowerInvariant();
            if (provider == Providers.Zernio)
                return Results.Json(new
                {
                    detail = "A managed WhatsApp connection is made with POST /api/social/connect {platform: \"whatsapp\", purpose: \"inbox\"}.",
                }, statusCode: 400);
            if (!WhatsAppProviders.Known(provider)) provider = Providers.Cloud;
            var name = (AsString(body, "name") ?? "").Trim();
            var pnid = (AsString(body, "phone_number_id") ?? "").Trim();
            var token = (AsString(body, "access_token") ?? "").Trim();

            if (provider == Providers.Baileys)
            {
                // A Baileys number carries no credentials — it is QR-paired, so
                // the name is the only thing the operator can get wrong here.
                if (name == "")
                    return Results.Json(new { detail = "name is required for a Baileys account." },
                                        statusCode: 400);
            }
            else if (WhatsAppProviders.IsBsp(provider))
            {
                // YCloud / Twilio: their own credential shapes (BspCredentialError)
                if (BspCredentialError(provider, body, creating: true, current: null) is string bspErr)
                    return Results.Json(new { detail = bspErr }, statusCode: 400);
            }
            else if (pnid == "" || token == "")
            {
                return Results.Json(new { detail = "phone_number_id and access_token are required." },
                                    statusCode: 400);
            }

            if (RoleError(body) is IResult roleErr) return roleErr;
            var role = (AsString(body, "role") ?? "").Trim().ToLowerInvariant();

            var apiVersion = (AsString(body, "api_version") ?? "").Trim();
            var owner = (AsString(body, "owner") ?? "").Trim();
            var row = new ChannelAccount
            {
                Channel = Channels.WhatsApp,
                Provider = provider,
                Name = name != "" ? name : "WhatsApp account",
                ExternalId = pnid,
                WabaId = (AsString(body, "waba_id") ?? "").Trim(),
                AccessToken = token,
                AppSecret = (AsString(body, "app_secret") ?? "").Trim(),
                ApiVersion = apiVersion != "" ? apiVersion : "v23.0",
                Owner = owner != "" ? owner : me.Identity?.Name ?? "admin",
                Config = AccountRoles.Contains(role)
                    ? new Dictionary<string, object?> { ["role"] = role }
                    : new Dictionary<string, object?>(),
                // Unknown/blank slug -> null = a global number. Never guess an
                // outlet: a wrongly-bound number files every one of its chats
                // under the wrong branch with no obvious clue why.
                EntityId = await InboxEndpoints.EntityId(db, AsString(body, "entity")),
                CreatedAt = DateTime.UtcNow,
            };
            // a company admin links numbers for their own companies only
            if (ShellScope.CompanyLevelError(db, ctx.RequestServices.GetRequiredService<ScopingService>(), me, row.EntityId) is { } outside)
                return outside;
            if (WhatsAppProviders.IsBsp(provider)) ApplyBsp(row, body);
            if (ApplyDelivery(row, body) is IResult deliveryErr) return deliveryErr;
            ApplyPhoneNotifications(row, body);   // QR numbers: handed to the gateway when it pairs
            if (ApplyHistoryImport(row, body) is IResult historyErr) return historyErr;
            // inbox "who answers it" keys: owning team + AI reply default
            if (InboxSettings.Apply(db, row, AsString(body, InboxSettings.TeamKey),
                    AsString(body, InboxSettings.AiDefaultKey),
                    AsString(body, InboxSettings.ReviewModeKey),
                    AsString(body, InboxSettings.KeepRecalledKey)) is string inboxErr)
                return Results.Json(new { detail = inboxErr }, statusCode: 400);
            db.ChannelAccounts.Add(row);
            await db.SaveChangesAsync();
            // Twilio: per-message StatusCallback = this account's own webhook (needs the id)
            if (StampStatusCallback(row, ctx)) await db.SaveChangesAsync();
            // a first real verdict within seconds (Cloud: Graph token ping; Baileys: no-op)
            _ = ctx.RequestServices.GetRequiredService<ChatBranch.Api.Workers.HealthRecheck>().Schedule(row.Id);

            await Audit(db, ctx, me, "account.create", $"whatsapp:{row.Name}");
            await NotifyManagers(db, $"WhatsApp account linked: {row.Name}",
                                 $"By {me.Identity?.Name ?? "someone"}");
            // Re-read untracked so the payload carries the entity slug without
            // attaching a second copy of the Entity to the tracked graph.
            var saved = await db.ChannelAccounts.AsNoTracking().Include(a => a.Entity)
                .FirstAsync(a => a.Id == row.Id);
            return Results.Ok(AccountPayload(saved));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // Django's account edit speaks POST (not PATCH) — the dashboard's role
        // switcher and entity binder both post here.
        app.MapPost("/api/whatsapp/accounts/{id:int}", async (int id, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var row = await db.ChannelAccounts.Include(a => a.Entity)
                .FirstOrDefaultAsync(a => a.Id == id && a.Channel == Channels.WhatsApp);
            if (row == null) return Results.Json(new { detail = "Account not found." },
                                                 statusCode: 400);
            var body = await ReadBody(ctx);
            if (RoleError(body) is IResult roleErr) return roleErr;
            if (ShellScope.CompanyLevelError(db, sc, me, row.EntityId) is { } outside) return outside;
            // a managed connection: its credentials (and its outlet — its profile) are not edited here
            if (ChatBranch.Data.Social.ZernioNative.Is(row)
                && new[] { "phone_number_id", "waba_id", "access_token", "app_secret", "api_version", "provider", "entity",
                           "phone_number" }.Any(k => TryString(body, k, out var v) && !v.Contains("••")
                                                     && v.Trim() != (k == "provider" ? Providers.Zernio : "")))
                return Results.Json(new { detail = ChatBranch.Data.Social.ZernioNative.NotEditable }, statusCode: 400);

            var oldRole = AccountRole(row);
            var credsChanged = false;

            // A masked placeholder ("••••1234") means "untouched" — storing it
            // would clobber the real key with dots.
            void Patch(string key, Func<string> read, Action<string> write)
            {
                if (!TryString(body, key, out var raw) || raw.Contains("••")) return;
                var value = raw.Trim();
                if (read() != value && CredentialKeys.Contains(key)) credsChanged = true;
                write(value);
            }

            Patch("name", () => row.Name, v =>
            {
                if (ChatBranch.Data.Social.ZernioNative.Is(row)) ChatBranch.Data.Social.ZernioNative.Rename(row, v);
                else row.Name = v;
            });
            Patch("phone_number_id", () => row.ExternalId, v => row.ExternalId = v);
            Patch("waba_id", () => row.WabaId, v => row.WabaId = v);
            Patch("access_token", () => row.AccessToken, v => row.AccessToken = v);
            Patch("app_secret", () => row.AppSecret, v => row.AppSecret = v);
            Patch("api_version", () => row.ApiVersion, v => row.ApiVersion = v);
            Patch("owner", () => row.Owner, v => row.Owner = v);
            Patch("provider", () => row.Provider, v =>
            {
                // nobody turns a number INTO (or out of) a managed connection by editing it
                if (WhatsAppProviders.Known(v) && v.ToLowerInvariant() != Providers.Zernio
                    && row.Provider != Providers.Zernio) row.Provider = v.ToLowerInvariant();
            });
            if (WhatsAppProviders.IsBsp(row))
            {
                // YCloud's number (E.164 → digits) and Twilio's Config keys; a masked secret = untouched
                if (BspCredentialError(row.Provider, body, creating: false, current: row) is string bspErr)
                    return Results.Json(new { detail = bspErr }, statusCode: 400);
                foreach (var key in new[] { "phone_number" }.Concat(TwilioPlainKeys).Append(TwilioSecretKey))
                    if (TryString(body, key, out var raw) && !raw.Contains("••") && CredentialKeys.Contains(key))
                        credsChanged |= BspValue(row, key) != raw.Trim();
                ApplyBsp(row, body);
                StampStatusCallback(row, ctx);
            }

            var role = (AsString(body, "role") ?? "").Trim().ToLowerInvariant();
            if (Has(body, "role") && AccountRoles.Contains(role))
                // role lives in the config JSON; changing it never needs a health reset
                row.Config = new Dictionary<string, object?>(row.Config) { ["role"] = role };

            // per-account delivery + AI knobs (quiet hours, send pacing, voice-call
            // brain/STT) — same config JSON, no health reset either
            if (ApplyDelivery(row, body) is IResult deliveryErr) return deliveryErr;
            var phoneNotificationsChanged = ApplyPhoneNotifications(row, body);
            if (ApplyHistoryImport(row, body) is IResult historyErr) return historyErr;
            if (InboxSettings.Apply(db, row, AsString(body, InboxSettings.TeamKey),
                    AsString(body, InboxSettings.AiDefaultKey),
                    AsString(body, InboxSettings.ReviewModeKey),
                    AsString(body, InboxSettings.KeepRecalledKey)) is string inboxErr)
                return Results.Json(new { detail = inboxErr }, statusCode: 400);

            var entityChanged = false;
            if (Has(body, "entity"))
            {
                // "" / null explicitly UNBINDS the number back to organization-wide.
                // Only the FK moves — EF fixes the navigation up, and touching it
                // by hand would attach a second copy of the Entity.
                var newEntityId = await InboxEndpoints.EntityId(db, AsString(body, "entity"));
                if (ShellScope.CompanyLevelError(db, sc, me, newEntityId) is { } outsideNew) return outsideNew;
                entityChanged = row.EntityId != newEntityId;
                row.EntityId = newEntityId;
            }

            if (credsChanged)
            {
                // A reconnected account must become routable NOW — a stale
                // "disconnected" verdict otherwise keeps failing sends over to
                // other accounts (health.reset_on_reconfigure).
                row.HealthStatus = Health.Unknown;
                row.HealthDetail = "";
                row.FailStreak = 0;
                row.HealthChangedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            // …and re-probe it now, off the request, instead of at the next checker pass
            if (credsChanged) _ = ctx.RequestServices.GetRequiredService<ChatBranch.Api.Workers.HealthRecheck>().Schedule(row.Id);
            // "Keep phone notifications" takes effect on the live session at once (a number
            // never paired gets it when it pairs; an unreachable gateway when it next pairs)
            if (phoneNotificationsChanged && !string.IsNullOrWhiteSpace(row.ExternalId))
                await ctx.RequestServices.GetRequiredService<ChatBranch.Data.Sidecars.IBaileysGateway>()
                    .SetPhoneNotificationsAsync(row.ExternalId, BaileysSettings.KeepPhone(row));

            if (entityChanged)
                // Re-file this number's existing conversations under the new
                // outlet — otherwise binding a number leaves its whole history
                // in the old scope and the inbox looks like it lost chats.
                // (within one company: another company's conversation lets go of the number)
            {
                ChatBranch.Data.Stores.ThreadCompanies.AfterAccountRebound(db, sc, row.Id);
                await db.SaveChangesAsync();
            }

            // Becoming the reminder cannon retires the chatbot across this
            // account's existing threads. The AI tick is per account
            // (Thread.ai_overrides + role default), so stamp an explicit OFF
            // tick for THIS account wherever it was effectively on — flipping
            // the role back later then never re-enables anything by itself.
            if (AccountRole(row) == "reminder" && oldRole != "reminder")
            {
                var key = row.Id.ToString();
                var flipped = 0;
                foreach (var thread in await db.Threads.Where(t => t.Channel == Channels.WhatsApp
                                                              && t.AccountId == row.Id).ToListAsync())
                {
                    if (AiOverride(thread, row.Id) == false) continue; // already off
                    thread.AiOverrides =
                        new Dictionary<string, object?>(thread.AiOverrides) { [key] = false };
                    thread.AiEnabled = false;
                    thread.UpdatedAt = DateTime.UtcNow;   // keeps the SSE stream fresh
                    flipped++;
                }
                if (flipped > 0)
                {
                    await db.SaveChangesAsync();
                    await Audit(db, ctx, me, "account.role", $"whatsapp:{row.Name}",
                                $"reminder role — chatbot off on {flipped} thread(s)");
                }
            }

            await Audit(db, ctx, me, "account.update", $"whatsapp:{row.Name}");
            var saved = await db.ChannelAccounts.AsNoTracking().Include(a => a.Entity)
                .FirstAsync(a => a.Id == row.Id);
            return Results.Ok(AccountPayload(saved));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        app.MapDelete("/api/whatsapp/accounts/{id:int}", async (int id, HttpContext ctx,
            AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.ChannelAccounts
                .FirstOrDefaultAsync(a => a.Id == id && a.Channel == Channels.WhatsApp);
            if (row == null) return Results.Json(new { detail = "Account not found." },
                                                 statusCode: 400);
            if (ShellScope.CompanyLevelError(db, ctx.RequestServices.GetRequiredService<ScopingService>(), me, row.EntityId) is { } outside)
                return outside;
            if (ChatBranch.Data.Social.ZernioNative.Is(row))
                return Results.Json(new
                {
                    detail = "This number is a managed connection: disconnect it with DELETE /api/social/accounts/{id}.",
                    zernio_account_id = row.ExternalId,
                }, statusCode: 409);
            // Chats stay: the threads (and their message history) go back to
            // sandbox rather than vanishing with the number. Detaching first is
            // also what keeps the optional FKs from blocking the delete.
            await db.Threads.Where(t => t.AccountId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.AccountId, (int?)null));
            await db.Messages.Where(m => m.AccountId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.AccountId, (int?)null));
            await db.OutboundJobs.Where(j => j.AccountId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.AccountId, (int?)null));
            await db.AccountAssignments.Where(a => a.AccountId == id).ExecuteDeleteAsync();
            db.ChannelAccounts.Remove(row);
            await db.SaveChangesAsync();

            await Audit(db, ctx, me, "account.delete", $"whatsapp:{id}");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // Credential check (views.test_account): a Cloud number is proven by
        // fetching its profile off Graph (GET /{phone_number_id}); the result
        // moves the account's health through AccountHealth.SetStatus (success
        // heals, an account-level Meta error degrades / disconnects / bans it).
        // A Baileys number reports its monitor state — the gateway owns it.
        app.MapPost("/api/whatsapp/accounts/{id:int}/test", async (int id, AppDbContext db,
            IWhatsAppCloudClient wa, CancellationToken ct) =>
        {
            var row = await db.ChannelAccounts
                .FirstOrDefaultAsync(a => a.Id == id && a.Channel == Channels.WhatsApp, ct);
            if (row == null || !IsConfigured(row))
                return Results.Json(new { detail = "Account not configured." }, statusCode: 400);
            if (WhatsAppProviders.IsBsp(row))
            {
                // YCloud: the number's profile (or the balance); Twilio: the account — each
                // provider's own authenticated GET, the same probe the health checker runs
                var probe = await wa.ProbeBspAsync(row, ct);
                if (probe == null)
                    return Results.Json(new { detail = "Account not configured." }, statusCode: 400);
                var label = WhatsAppProviders.Label(row.Provider);
                var v = probe.Verdict;
                if (v.Status == Health.Healthy) MetaErrors.MarkHealthy(db, row, "account test passed", actor: "user");
                else if (v.Status != null) AccountHealth.SetStatus(db, row, v.Status, v.Detail, actor: "user");
                await db.SaveChangesAsync(ct);
                if (v.Status == Health.Healthy)
                    return Results.Ok(new
                    {
                        ok = true,
                        display_phone_number = probe.Number,
                        verified_name = probe.Name,
                        quality_rating = probe.Quality,
                        detail = $"{label} accepted the credentials.",
                        webhook_path = WhatsAppProviders.WebhookPath(row),
                    });
                return Results.Json(new
                {
                    ok = false,
                    detail = v.Transient
                        ? $"Couldn't check the credentials — {v.Detail}. Try again in a moment."
                        : $"{label}: {v.Detail}",
                    health_status = row.HealthStatus,
                }, statusCode: v.Transient ? 502 : 400);
            }
            if (row.Provider == Providers.Baileys || !wa.IsConfigured(row))
            {
                var number = Cfg(row, "number");
                return Results.Ok(new
                {
                    ok = row.HealthStatus != Health.Disconnected && row.HealthStatus != Health.Banned,
                    display_phone_number = number,
                    verified_name = row.Name,
                    quality_rating = row.HealthStatus,
                    detail = "Paired over WhatsApp Web — the gateway reports this number's state.",
                });
            }
            var r = await wa.GetPhoneNumberAsync(row, ct);
            if (r.Ok)
            {
                MetaErrors.MarkHealthy(db, row, "account test passed", actor: "user");
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    ok = true,
                    display_phone_number = MetaGraph.Str(r.Body, "display_phone_number"),
                    verified_name = MetaGraph.Str(r.Body, "verified_name"),
                    quality_rating = MetaGraph.Str(r.Body, "quality_rating"),
                    detail = "Meta accepted the credentials.",
                });
            }
            MetaErrors.ApplyToAccount(db, row, r.ErrorCode, r.ErrorSubcode, r.Detail, actor: "user");
            await db.SaveChangesAsync(ct);
            return Results.Json(new
            {
                ok = false,
                // an error code = Meta answered and said no; none = Meta was never reached
                detail = r.ErrorCode != null
                    ? $"Meta refused the credentials: {r.Detail}"
                    : $"Couldn't check the credentials — {r.Detail}. Try again in a moment.",
                health_status = row.HealthStatus,
            }, statusCode: r.ErrorCode != null ? 400 : 502);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");   // a Test talks to the provider (and can send mail)

        // ---- Baileys pairing control (proxied to the Node gateway) ---------
        // Baileys is a supported channel: start/status/logout drive the sidecar
        // (backend/baileys_service) through IBaileysGateway. The gateway keys
        // sessions by account id — we use the row's ExternalId when set, else
        // the DB id (stable + unique), and persist that so inbound pushes
        // (/api/whatsapp/baileys/inbound {account_id}) route back to this row.
        // An unreachable gateway is a clean {state:"error", error} — never a 500.
        // A body of {} pairs by QR; {"phone": "+60123456789"} pairs by PHONE NUMBER —
        // WhatsApp answers with an 8-character code to type under Linked devices →
        // "Link with phone number", which is the only way in when the phone cannot
        // point its camera at the dashboard. The code never reaches the audit row.
        app.MapPost("/api/whatsapp/accounts/{id:int}/baileys/start", async (int id, HttpContext ctx,
            AppDbContext db, ClaimsPrincipal me, ChatBranch.Data.Sidecars.IBaileysGateway gw) =>
        {
            var acct = await BaileysAccount(db, id);
            if (acct == null) return BaileysNotFound();
            // someone bound to entities pairs their own entities' numbers only
            if (ShellScope.ObjectScopeError(db, ctx.RequestServices.GetRequiredService<ScopingService>(), me,
                    acct.EntityId) is { } outside)
                return outside;
            var body = await ReadBody(ctx);
            var phoneRaw = (AsString(body, "phone") ?? "").Trim();
            var pairPhone = "";
            if (phoneRaw.Length > 0)
            {
                pairPhone = BaileysPairing.Phone(phoneRaw);
                if (pairPhone.Length == 0)
                    return Results.Json(new
                    {
                        detail = "phone must be the number's full international form with its country code "
                                 + "(for example +60123456789) — digits only, no leading 0.",
                    }, statusCode: 400);
            }
            var key = await EnsureBaileysKey(db, acct);
            // The session starts with the number's own settings: "keep phone
            // notifications", and how far back to import its existing chats — that one
            // decides a socket option, so pairing is the only moment it can be chosen.
            await gw.SetSettingsAsync(key, BaileysSettings.KeepPhone(acct),
                BaileysSettings.ImportHistoryDays(acct));
            var st = await gw.StartAsync(key, pairPhone.Length > 0 ? pairPhone : null);
            await Audit(db, ctx, me, "account.baileys_start", $"whatsapp:{id}",
                pairPhone.Length > 0 ? $"{st.State} (by phone number)" : st.State);
            return Results.Ok(BaileysStatePayload(st));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        app.MapGet("/api/whatsapp/accounts/{id:int}/baileys/status", async (int id, AppDbContext db,
            ChatBranch.Data.Sidecars.IBaileysGateway gw) =>
        {
            var acct = await BaileysAccount(db, id);
            if (acct == null) return BaileysNotFound();
            var key = await EnsureBaileysKey(db, acct);
            return Results.Ok(BaileysStatePayload(await gw.StatusAsync(key)));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.view");

        app.MapPost("/api/whatsapp/accounts/{id:int}/baileys/logout", async (int id, HttpContext ctx,
            AppDbContext db, ClaimsPrincipal me, ChatBranch.Data.Sidecars.IBaileysGateway gw) =>
        {
            var acct = await BaileysAccount(db, id);
            if (acct == null) return BaileysNotFound();
            if (ShellScope.ObjectScopeError(db, ctx.RequestServices.GetRequiredService<ScopingService>(), me,
                    acct.EntityId) is { } outside)
                return outside;
            var key = await EnsureBaileysKey(db, acct);
            var st = await gw.LogoutAsync(key);
            await Audit(db, ctx, me, "account.baileys_logout", $"whatsapp:{id}", st.State);
            return Results.Ok(BaileysStatePayload(st));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // ---- template authoring --------------------------------------------
        // A live WABA (configured Cloud account with a waba_id) writes to Meta's
        // registry: create = POST /{waba_id}/message_templates (submitted for
        // review, status PENDING), edit = POST /{template_id}, delete = DELETE
        // /{waba_id}/message_templates?name=…. Without one — or for an
        // operator-made local template — writes land in the local Setting row.
        // Variable bindings always stay local (per account scope).
        app.MapPost("/api/whatsapp/templates", async (string? account_id, HttpContext ctx,
            AppDbContext db, ScopingService sc, IWhatsAppCloudClient wa, ClaimsPrincipal me) =>
        {
            var body = await ReadBody(ctx);
            var name = (AsString(body, "name") ?? "").Trim().ToLowerInvariant();
            var language = (AsString(body, "language") ?? "").Trim();
            if (language == "") language = "en";
            var category = (AsString(body, "category") ?? "").Trim().ToUpperInvariant();
            if (category == "") category = "UTILITY";
            var templateBody = (AsString(body, "body") ?? "").Trim();

            if (!TemplateNameRe.IsMatch(name))
                return Results.Json(new
                {
                    detail = "name must be lowercase letters, digits and underscores (max 120 chars).",
                }, statusCode: 400);
            if (!TemplateCategories.Contains(category))
                return Results.Json(new
                {
                    detail = $"category must be one of {string.Join(", ", TemplateCategories)}.",
                }, statusCode: 400);
            if (templateBody == "")
                return Results.Json(new { detail = "body is required." }, statusCode: 400);
            if (templateBody.Length > 1024)
                return Results.Json(new { detail = "body exceeds WhatsApp's 1024-character limit." },
                                    statusCode: 400);

            var bindings = ValidBindings(body);
            if (bindings == null)
                return Results.Json(new
                {
                    detail = $"Unknown binding key — valid keys: {string.Join(", ", TemplateBindingKeys)}.",
                }, statusCode: 400);
            var buttons = ValidButtons(body);
            if (buttons == null) return Results.Json(new { detail = ButtonsError }, statusCode: 400);

            var account = await TemplateAccount(db, account_id ?? AsString(body, "account_id"));
            // someone bound to entities authors templates on their own entities' numbers only
            // (an org-wide WABA / the local registry: a company admin's old reach, no one else bound)
            if (ShellScope.ObjectScopeError(db, sc, me, account?.EntityId) is { } outside) return outside;
            if (ReadOnlyRegistry(account) is { } readOnly) return readOnly;
            if (account != null)
            {
                // live: submit to Meta for review (YCloud relays it to Meta)
                var created = await wa.CreateTemplateAsync(account,
                    MetaTemplatePayload(name, language, category, templateBody, buttons, Examples(body)));
                if (!created.Ok)
                    return Results.Json(new { detail = $"{RegistryName(account)} rejected the template: {created.Detail}" },
                                        statusCode: created.ErrorCode != null ? 400 : 502);
                WhatsAppTemplateCatalog.Invalidate(account.Id);
                SaveBindings(sc, BindingsScope(account), name, bindings);
                var status = MetaGraph.Str(created.Body, "status") is { Length: > 0 } st ? st.ToUpperInvariant() : "PENDING";
                await Audit(db, ctx, me, "template.create", $"whatsapp:{name}", "meta");
                return Results.Ok(new
                {
                    template = TemplateDto(name, language, category, status, templateBody, buttons, true,
                                           new Dictionary<string, List<string>> { [name] = bindings }, null),
                    sandbox = false,
                });
            }

            if (DemoTemplates.Any(t => t.Name == name))
                return Results.Json(new { detail = $"A template named '{name}' already exists." },
                                    statusCode: 400);
            var existing = CustomTemplates(sc);
            if (existing.Any(t => t.Name == name))
                return Results.Json(new { detail = $"A template named '{name}' already exists." },
                                    statusCode: 400);

            existing.Add(new CustomTemplate(name, language, category, "APPROVED", templateBody, buttons));
            SaveCustomTemplates(sc, existing);
            SaveBindings(sc, BindingsScope(account), name, bindings);

            await Audit(db, ctx, me, "template.create", $"whatsapp:{name}", "local");
            return Results.Ok(new
            {
                template = TemplateDto(name, language, category, "APPROVED", templateBody, buttons,
                                       true, new Dictionary<string, List<string>> { [name] = bindings },
                                       null),
                sandbox = account == null,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("templates.manage");

        // PATCH: content edits (body/category/buttons) and/or the variable
        // bindings — a bindings-only edit must never touch the template itself,
        // which is what keeps an APPROVED template out of re-review.
        app.MapMethods("/api/whatsapp/templates/{name}", new[] { "PATCH" },
            async (string name, string? account_id, HttpContext ctx, AppDbContext db,
                   ScopingService sc, IWhatsAppCloudClient wa, WhatsAppTemplateCatalog catalog,
                   ClaimsPrincipal me) =>
        {
            var body = await ReadBody(ctx);
            var account = await TemplateAccount(db, account_id ?? AsString(body, "account_id"));
            if (ShellScope.ObjectScopeError(db, sc, me, account?.EntityId) is { } outside) return outside;
            var scope = BindingsScope(account);
            var editsContent = Has(body, "body") || Has(body, "category") || Has(body, "buttons");

            if (editsContent)
            {
                var templateBody = (AsString(body, "body") ?? "").Trim();
                var category = (AsString(body, "category") ?? "").Trim().ToUpperInvariant();
                if (category == "") category = "UTILITY";
                if (templateBody == "")
                    return Results.Json(new { detail = "body is required." }, statusCode: 400);
                if (templateBody.Length > 1024)
                    return Results.Json(new { detail = "body exceeds WhatsApp's 1024-character limit." },
                                        statusCode: 400);
                if (!TemplateCategories.Contains(category))
                    return Results.Json(new
                    {
                        detail = $"category must be one of {string.Join(", ", TemplateCategories)}.",
                    }, statusCode: 400);
                var buttons = ValidButtons(body);
                if (buttons == null) return Results.Json(new { detail = ButtonsError }, statusCode: 400);

                if (DemoTemplates.Any(t => t.Name == name))
                    return Results.Json(new { detail = "Built-in demo templates can't be edited." },
                                        statusCode: 400);
                var existing = CustomTemplates(sc);
                var idx = existing.FindIndex(t => t.Name == name);
                if (idx >= 0)
                {
                    existing[idx] = existing[idx] with
                    {
                        Body = templateBody, Category = category, Buttons = buttons,
                    };
                    SaveCustomTemplates(sc, existing);
                }
                else if (ReadOnlyRegistry(account) is { } readOnly) return readOnly;
                else if (account != null)
                {
                    // live: edit at Meta (re-enters review) — by the id the page
                    // sent, else the registry's id for that name
                    var templateId = AsString(body, "id") ?? "";
                    if (templateId.Trim().Length == 0)
                    {
                        var (live, _) = await catalog.LiveAsync(account, fresh: true);
                        templateId = live.FirstOrDefault(t => t.Name == name)?.Id ?? "";
                    }
                    if (templateId.Trim().Length == 0)
                        return Results.NotFound(new { detail = "Template not found." });
                    var payload = MetaTemplatePayload(name, null, category, templateBody, buttons, Examples(body));
                    payload.Remove("name");
                    payload.Remove("language");
                    var edited = await wa.EditTemplateAsync(account, templateId.Trim(), payload);
                    if (!edited.Ok)
                        return Results.Json(new { detail = $"{RegistryName(account)} rejected the edit: {edited.Detail}" },
                                            statusCode: edited.ErrorCode != null ? 400 : 502);
                    WhatsAppTemplateCatalog.Invalidate(account.Id);
                }
                else return Results.NotFound(new { detail = "Template not found." });
            }

            if (Has(body, "bindings"))
            {
                var bindings = ValidBindings(body);
                if (bindings == null)
                    return Results.Json(new
                    {
                        detail = $"Unknown binding key — valid keys: {string.Join(", ", TemplateBindingKeys)}.",
                    }, statusCode: 400);
                SaveBindings(sc, scope, name, bindings);
            }

            await Audit(db, ctx, me, "template.update", $"whatsapp:{name}",
                        editsContent ? "content" : "bindings");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("templates.manage");

        app.MapDelete("/api/whatsapp/templates/{name}", async (string name, string? account_id, string? id,
            HttpContext ctx, AppDbContext db, ScopingService sc, IWhatsAppCloudClient wa, ClaimsPrincipal me) =>
        {
            if (DemoTemplates.Any(t => t.Name == name))
                return Results.Json(new { detail = "Built-in demo templates can't be deleted." },
                                    statusCode: 400);
            var account = await TemplateAccount(db, account_id);
            if (ShellScope.ObjectScopeError(db, sc, me, account?.EntityId) is { } outside) return outside;
            var existing = CustomTemplates(sc);
            var remaining = existing.Where(t => t.Name != name).ToList();
            var where = "local";
            if (remaining.Count != existing.Count)
                SaveCustomTemplates(sc, remaining);
            else if (ReadOnlyRegistry(account) is { } readOnly) return readOnly;
            else if (account != null)
            {
                // live: remove it from the WABA (every language of that name)
                var deleted = await wa.DeleteTemplateAsync(account, name, id);
                if (!deleted.Ok)
                    return Results.Json(new { detail = $"{RegistryName(account)} refused the delete: {deleted.Detail}" },
                                        statusCode: deleted.ErrorCode != null ? 400 : 502);
                WhatsAppTemplateCatalog.Invalidate(account.Id);
                where = "meta";
            }
            else return Results.NotFound(new { detail = "Template not found." });
            SaveBindings(sc, BindingsScope(account), name, new List<string>());

            await Audit(db, ctx, me, "template.delete", $"whatsapp:{name}", where);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("templates.manage");

        // ---- contact → account assignments (write) -------------------------
        // {"contact_id", "channel", "account_id" (null = back to auto), "pinned"}
        app.MapPut("/api/whatsapp/assignments", async (HttpContext ctx, AppDbContext db,
            ClaimsPrincipal me) =>
        {
            if (await SettingsEndpoints.RequireCapability(db, me, AssignmentsEndpoints.Capability) is { } denied)
                return denied;
            var body = await ReadBody(ctx);
            var channel = (AsString(body, "channel") ?? "").Trim();
            if (channel == "") channel = Channels.WhatsApp;

            var contactId = AsInt(body, "contact_id");
            var contact = contactId is int cid
                ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid)
                : null;
            if (contact == null) return Results.NotFound(new { detail = "Contact not found." });

            var rawAccount = AsString(body, "account_id") ?? "";
            var pinned = AsBool(body, "pinned") ?? false;

            if (rawAccount.Trim() == "")
            {
                // Cleared: the contact goes back to automatic (least-loaded,
                // health-aware) routing on the next send.
                await db.AccountAssignments
                    .Where(a => a.ContactId == contact.Id && a.Channel == channel)
                    .ExecuteDeleteAsync();
                await Audit(db, ctx, me, "assignment.clear", $"{channel}:{contact.Name}");
                return Results.Ok(new { ok = true });
            }
            if (!int.TryParse(rawAccount.Trim(), out var accountId))
                return Results.Json(new
                {
                    detail = "The env-default account is not assignable — add it as a " +
                             "dashboard account first.",
                }, statusCode: 400);

            var account = await db.ChannelAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == accountId && a.Channel == channel);
            if (account == null) return Results.NotFound(new { detail = "Account not found." });

            var asg = await db.AccountAssignments.Include(a => a.Contact).Include(a => a.Account)
                .FirstOrDefaultAsync(a => a.ContactId == contact.Id && a.Channel == channel);
            if (asg == null)
            {
                asg = new AccountAssignment
                {
                    ContactId = contact.Id, Channel = channel, CreatedAt = DateTime.UtcNow,
                };
                db.AccountAssignments.Add(asg);
            }
            asg.AccountId = account.Id;
            asg.Pinned = pinned;
            asg.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await Audit(db, ctx, me, "assignment.set", $"{channel}:{contact.Name}",
                        $"{account.Name} pinned={pinned}");
            return Results.Ok(new
            {
                contact_id = contact.Id,
                contact_name = contact.Name,
                phone = contact.Phone,
                channel,
                account_id = account.Id.ToString(),
                account_name = account.Name,
                pinned,
                updated_at = Iso(asg.UpdatedAt),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- voice (Twilio SIP trunk) writes -------------------------------
        // PUT = SUPER ADMIN, same rule as the other credential surfaces.
        // SIP_OUTBOUND_TRUNK_ID stays .env-only — the voice-agent worker is a
        // separate process that reads it once at boot.
        app.MapPut("/api/whatsapp/voice-settings", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var body = await ReadBody(ctx);
            var stored = StoredVoiceSettings(sc);
            var touched = new List<string>();
            foreach (var key in new[] { "sip_username", "sip_password", "termination_uri" })
            {
                // untouched masked placeholders round-trip from GET — keep the
                // stored secret rather than writing dots over it
                if (!TryString(body, key, out var raw) || raw.Contains("••")) continue;
                stored[key] = raw.Trim();
                touched.Add(key);
            }
            if (touched.Count == 0)
                return Results.Json(new { detail = "Nothing to update." }, statusCode: 400);

            sc.SetSetting(VoiceSettingsKey, JsonSerializer.Serialize(stored));
            await Audit(db, ctx, me, "settings.update", "voice-twilio",
                        $"keys: {string.Join(", ", touched.OrderBy(k => k))}");
            return Results.Ok(VoicePayload(sc));
        }).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireCapability("app_keys.manage");   // SIP trunk credentials (App & provider keys)

        // No REST endpoint proves a SIP credential short of placing a real
        // call, so "test" reports the computed configured/health state.
        app.MapPost("/api/whatsapp/voice-settings/test", (ScopingService sc) =>
        {
            var (configured, health, detail) = VoiceState(sc);
            if (!configured || health != Health.Healthy)
                return Results.Json(new
                {
                    detail = detail != "" ? detail : "SIP username and password are required.",
                }, statusCode: 400);
            return Results.Ok(new { ok = true, health, detail = "SIP trunk creds are set." });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");   // a Test talks to the provider (and can send mail)
    }

    /// <summary>
    /// The account ids the health card shows for this scope (the same
    /// own-else-shared preference as <see cref="ChannelsHealthPayload"/>), so
    /// "Check now" probes exactly what the caller sees.
    /// </summary>
    private static async Task<List<int>> HealthScopeIds(AppDbContext db, int? entityId)
    {
        var all = await db.ChannelAccounts.AsNoTracking().Select(a => new { a.Id, a.EntityId }).ToListAsync();
        if (entityId is not int eid) return all.Select(a => a.Id).ToList();
        var own = all.Where(a => a.EntityId == eid).Select(a => a.Id).ToList();
        return own.Count > 0 ? own : all.Where(a => a.EntityId == null).Select(a => a.Id).ToList();
    }

    /// <summary>GET/POST /channels/health — views.channels_health().</summary>
    private static async Task<object> ChannelsHealthPayload(AppDbContext db, ScopingService sc,
        int? entityId, List<int>? usable = null)
    {
        var assigned = await db.AccountAssignments.AsNoTracking()
            .GroupBy(a => a.AccountId)
            .Select(g => new { AccountId = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.N);

        // Entity scope: an outlet's card shows the numbers it actually sends
        // through — its OWN if it has any, else the shared organization ones.
        // A preference, not a union (scoping.accounts_for_entity).
        var all = await db.ChannelAccounts.AsNoTracking().Include(a => a.Entity)
            .OrderBy(a => a.Id).ToListAsync();
        if (usable != null)
            all = all.Where(a => a.EntityId == null || usable.Contains(a.EntityId.Value)).ToList();
        var scoped = all;
        if (entityId is int eid)
        {
            var own = all.Where(a => a.EntityId == eid).ToList();
            scoped = own.Count > 0 ? own : all.Where(a => a.EntityId == null).ToList();
        }

        var accounts = scoped.Select(a => new
        {
            id = a.Id.ToString(),
            name = a.Name,
            channel = a.Channel,
            provider = string.IsNullOrEmpty(a.Provider) ? Providers.Cloud : a.Provider,
            entity = a.Entity?.Slug,
            entity_name = a.Entity?.Name ?? "",
            configured = IsConfigured(a),
            health = a.HealthStatus,
            detail = a.HealthDetail,
            last_ok_at = Iso(a.LastOkAt),
            last_inbound_at = Iso(a.LastInboundAt),
            fail_streak = a.FailStreak,
            changed_at = Iso(a.HealthChangedAt),
            assigned_contacts = assigned.GetValueOrDefault(a.Id, 0),
        }).ToList<object>();

        // The env-default pseudo-account only belongs in the health picture
        // when .env actually configures a usable transport.
        if (EnvAccountConfigured())
            accounts.Add(new
            {
                id = EnvAccountId,
                name = Env("WHATSAPP_ACCOUNT_NAME") is { Length: > 0 } n ? n : "From backend/.env",
                channel = Channels.WhatsApp,
                provider = Providers.Cloud,
                entity = (string?)null,
                entity_name = "",
                configured = true,
                health = Health.Unknown,   // no DB row -> the monitor never tracks it
                detail = "",
                last_ok_at = (string?)null,
                last_inbound_at = (string?)null,
                fail_streak = 0,
                changed_at = (string?)null,
                assigned_contacts = 0,
            });

        var checkedAt = sc.SettingValue(CheckedAtKey);
        return new
        {
            accounts,
            email = EmailHealth(sc),
            voice = VoiceHealth(sc),
            checked_at = checkedAt != "" ? checkedAt : Iso(DateTime.UtcNow),
        };
    }

    // ======================= payload builders ================================

    /// <summary>GET /settings — wa_config._global_payload().</summary>
    private static async Task<object> GlobalPayload(AppDbContext db, ScopingService sc)
    {
        var configured = await db.ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == Channels.WhatsApp)
            .ToListAsync();

        return new
        {
            // accessor, not the raw merge: it swaps the insecure shipped default
            // for the auto-generated secret
            verify_token = VerifyToken(sc),
            chat_model = Global(sc, "chat_model"),
            // secrets never leave the server in full — masked for display only
            openai_api_key_masked = Mask(Global(sc, "openai_api_key")),
            openrouter_api_key_masked = Mask(Global(sc, "openrouter_api_key")),
            google_api_key_masked = Mask(Global(sc, "google_api_key")),
            ilmu_api_key_masked = Mask(Global(sc, "ilmu_api_key")),
            zernio_api_key_masked = Mask(Global(sc, "zernio_api_key")),
            chat_provider_pref = Global(sc, "chat_provider_pref"),
            // quiet hours are not secrets — full values, "HH:MM" server-local time
            quiet_start = Global(sc, "quiet_start"),
            quiet_end = Global(sc, "quiet_end"),
            configured = configured.Any(IsConfigured) || EnvAccountConfigured(),
            ai_available = AiAvailable(sc),
            // the phone app's push credential: never echoed — only whether it is
            // there, and the non-secret project it belongs to
            fcm_service_account_set = PushSettings.GetFirebase(sc) != null,
            fcm_project_id = PushSettings.GetFirebase(sc)?.ProjectId ?? "",
            webhook_path = "/api/whatsapp/webhook",
        };
    }

    /// <summary>GET/PUT /routing-rules — views._rules_payload().</summary>
    private static object RulesPayload(ScopingService sc, int? entityId)
    {
        string Rule(string key)
        {
            var v = sc.SettingValue(key, entityId);
            if (v == "") return RuleDefaults[key];
            if (RuleChoices.TryGetValue(key, out var choices))
                return choices.Contains(v.ToLowerInvariant()) ? v.ToLowerInvariant() : RuleDefaults[key];
            return v;
        }

        var redirect = sc.SettingValue("business_redirect", entityId);
        if (redirect == "") redirect = RuleDefaults["business_redirect"];

        return new
        {
            ai_reply_mode = Rule("ai_reply_mode"),
            business_redirect = !new[] { "false", "0", "no", "off", "" }
                .Contains(redirect.Trim().ToLowerInvariant()),
            business_redirect_target = Rule("business_redirect_target"),
            support_contact = Rule("support_contact"),
            business_redirect_template = Rule("business_redirect_template"),
            recovery_mode = Rule("recovery_mode"),
            recovery_template = Rule("recovery_template"),
            cold_text_guard = Rule("cold_text_guard"),
            cold_call_guard = Rule("cold_call_guard"),
            reminder_ai_default = Rule("reminder_ai_default"),
            // outbound-queue pacing (Setting > env > default), edited from
            // Settings → AI Configuration → Send Pacing
            queue_min_gap_s = IntSetting(sc, "queue_min_gap_s", QueueMinGapDefault, entityId),
            queue_max_gap_s = IntSetting(sc, "queue_max_gap_s", QueueMaxGapDefault, entityId),
            // STOP / START (organization-wide): the effective lists, and the
            // operator's own additions to the built-ins (what PUT takes)
            opt_out_keywords = OptOut.ParseList(sc.SettingValue(OptOut.OutKey)),
            opt_in_keywords = OptOut.ParseList(sc.SettingValue(OptOut.InKey)),
            opt_out_keywords_effective = OptOut.OutWords(sc),
            opt_in_keywords_effective = OptOut.InWords(sc),
        };
    }

    /// <summary>GET /voice-settings — views._voice_settings_payload().</summary>
    private static object VoicePayload(ScopingService sc)
    {
        var stored = StoredVoiceSettings(sc);
        var effective = VoiceEffective(sc);

        string Source(string key) =>
            (stored.GetValueOrDefault(key) ?? "").Trim() != "" ? "settings"
            : effective[key] != "" ? "env" : "";

        return new
        {
            sip_username = effective["sip_username"],
            sip_password_masked = Mask(effective["sip_password"]),
            termination_uri = effective["termination_uri"],
            // where each effective value comes from: "settings" (dashboard row),
            // "env" (backend/.env fallback), or "" (not set anywhere)
            source = new
            {
                sip_username = Source("sip_username"),
                sip_password = Source("sip_password"),
                termination_uri = Source("termination_uri"),
            },
            sip_trunk_configured = SipTrunkConfigured(sc),
            health = VoiceHealth(sc),
        };
    }

    /// <summary>
    /// health.voice_health() — config-presence ONLY (live_check always false):
    /// no REST endpoint proves a SIP credential short of placing a real call.
    /// </summary>
    private static (bool Configured, string Status, string Detail) VoiceState(ScopingService sc)
    {
        if (!SipTrunkConfigured(sc)) return (false, Health.Unknown, "");
        var v = VoiceEffective(sc);
        if (v["sip_username"] == "" || v["sip_password"] == "")
            return (true, Health.Unknown,
                    "SIP trunk set — add the SIP username + password (Settings → Channels → " +
                    "Voice, or TWILIO_SIP_USERNAME / TWILIO_SIP_PASSWORD in .env) to dial");
        return (true, Health.Healthy, "");
    }

    private static object VoiceHealth(ScopingService sc)
    {
        var (configured, health, detail) = VoiceState(sc);
        return new { configured, health, detail, live_check = false };
    }

    /// <summary>health.email_health() — "unknown" until a check has run.</summary>
    private static object EmailHealth(ScopingService sc)
    {
        var host = Effective(sc, "smtp_host", "SMTP_HOST");
        var from = Effective(sc, "smtp_from", "SMTP_FROM");
        if (host == "" || from == "")
            return new { configured = false, health = Health.Unknown, detail = "" };

        var stored = ReadJsonObject(sc.SettingValue(EmailHealthKey));
        return new
        {
            configured = true,
            health = stored.GetValueOrDefault("health") is { Length: > 0 } h ? h : Health.Unknown,
            detail = stored.GetValueOrDefault("detail") ?? "",
        };
    }

    // ======================= templates =======================================

    /// <summary>
    /// The live template account (configured + waba_id), else null = sandbox.
    /// Without an explicit account_id, resolve from the TEMPLATE-CAPABLE pool
    /// only (Cloud accounts with a waba_id) preferring role=reminder — mirroring
    /// how template SENDS route.
    /// </summary>
    private static async Task<ChannelAccount?> TemplateAccount(AppDbContext db, string? accountId)
    {
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            if (!int.TryParse(accountId, out var id)) return null;
            var one = await db.ChannelAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id && a.Channel == Channels.WhatsApp);
            return one != null && IsConfigured(one) && WhatsAppProviders.HasTemplateRegistry(one) ? one : null;
        }

        // Cloud API / YCloud with a WABA id, Twilio (its Content API) — never Baileys
        var capable = (await db.ChannelAccounts.AsNoTracking()
                .Where(a => a.Channel == Channels.WhatsApp)
                .OrderBy(a => a.Id).ToListAsync())
            .Where(a => IsConfigured(a) && WhatsAppProviders.HasTemplateRegistry(a)).ToList();
        if (capable.Count == 0) return null;
        return capable.FirstOrDefault(a => AccountRole(a) == "reminder") ?? capable[0];
    }

    /// <summary>
    /// views._bindings_scope — bindings are stored per owning account (template
    /// names are per-WABA); everything without a live WABA shares "sandbox".
    /// </summary>
    private static string BindingsScope(ChannelAccount? account) =>
        account != null && WhatsAppProviders.HasTemplateRegistry(account) ? account.Id.ToString() : "sandbox";

    /// <summary>Who owns a live template registry, for the operator's error text.</summary>
    private static string RegistryName(ChannelAccount a) => WhatsAppProviders.Of(a) switch
    {
        Providers.YCloud => "YCloud",
        Providers.Twilio => "Twilio",
        _ => "Meta",
    };

    /// <summary>Twilio's Content templates are listed (and sent) here, but authored in Twilio's console.</summary>
    private static IResult? ReadOnlyRegistry(ChannelAccount? a) =>
        a != null && WhatsAppProviders.Of(a) == Providers.Twilio
            ? Results.Json(new
            {
                detail = "Twilio Content templates are read-only here — create, edit or delete them in the Twilio "
                         + "console (Messaging → Content Template Builder); they appear in this list once saved.",
            }, statusCode: 409)
            : null;

    /// <summary>
    /// views._valid_bindings — one entry per {{n}} slot, "" = the operator fills
    /// it by hand. null when the payload names a key the catalog doesn't have
    /// (a silent blank would ship where a phone number should be).
    /// </summary>
    private static List<string>? ValidBindings(JsonElement body)
    {
        var outv = new List<string>();
        if (!body.TryGetProperty("bindings", out var raw)) return outv;
        if (raw.ValueKind != JsonValueKind.Array) return outv;
        foreach (var el in raw.EnumerateArray())
        {
            var key = (el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "").Trim();
            if (key != "" && !TemplateBindingKeys.Contains(key)) return null;
            outv.Add(key);
        }
        return outv;
    }

    private const string ButtonsError =
        "buttons must be up to 3 of QUICK_REPLY / URL / PHONE_NUMBER, each with text ≤ 25 " +
        "chars (URL buttons need a static http(s) url).";

    /// <summary>
    /// views._valid_buttons — STATIC buttons only (no {{n}} in URLs), so a send
    /// never has to carry button parameters. null = invalid payload.
    /// </summary>
    private static List<object>? ValidButtons(JsonElement body)
    {
        var outv = new List<object>();
        if (!body.TryGetProperty("buttons", out var raw)) return outv;
        if (raw.ValueKind == JsonValueKind.Null) return outv;
        if (raw.ValueKind != JsonValueKind.Array) return null;
        if (raw.GetArrayLength() > 3) return null;

        foreach (var el in raw.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            var type = Prop(el, "type").Trim().ToUpperInvariant();
            var text = Prop(el, "text").Trim();
            if (!TemplateButtonTypes.Contains(type) || text == "" || text.Length > 25) return null;
            var button = new Dictionary<string, string> { ["type"] = type, ["text"] = text };
            if (type == "URL")
            {
                var url = Prop(el, "url").Trim();
                if (!(url.StartsWith("http://") || url.StartsWith("https://")) || url.Contains("{{"))
                    return null;
                button["url"] = url;
            }
            else if (type == "PHONE_NUMBER")
            {
                var phone = Prop(el, "phone_number").Trim();
                if (phone == "") return null;
                button["phone_number"] = phone;
            }
            outv.Add(button);
        }
        return outv;
    }

    private static void SaveCustomTemplates(ScopingService sc, List<CustomTemplate> templates) =>
        sc.SetSetting(CustomTemplatesKey, JsonSerializer.Serialize(templates.Select(t => new
        {
            name = t.Name,
            language = t.Language,
            category = t.Category,
            status = t.Status,
            body = t.Body,
            buttons = t.Buttons,
        })));

    /// <summary>
    /// Store (or, with an all-empty list, drop) one template's bindings inside
    /// its account scope. The other scopes in the row are preserved — they
    /// belong to other WABAs.
    /// </summary>
    private static void SaveBindings(ScopingService sc, string scope, string name,
        List<string> bindings)
    {
        var root = AllTemplateBindings(sc);
        var forScope = root.TryGetValue(scope, out var m) ? m : new Dictionary<string, List<string>>();
        if (bindings.Any(b => b != "")) forScope[name] = bindings;
        else forScope.Remove(name);
        root[scope] = forScope;
        sc.SetSetting(TemplateBindingsKey, JsonSerializer.Serialize(root));
    }

    private static object TemplateDto(string name, string language, string category, string status,
        string body, List<object> buttons, bool custom,
        Dictionary<string, List<string>> bindings, Dictionary<string, string>? resolvedVars)
    {
        var slots = bindings.GetValueOrDefault(name) ?? new List<string>();
        List<string>? resolved = null;
        if (resolvedVars is Dictionary<string, string> vars && slots.Any(s => s != ""))
            resolved = slots.Select(s => s == "" ? "" : vars.GetValueOrDefault(s, "")).ToList();
        return new
        {
            name,
            language,
            category,
            status,
            body,
            buttons,
            variables = VariableCount(body),
            custom,
            bindings = slots,
            resolved,
        };
    }

    /// <summary>The picker / settings row for one catalog template (demo, custom or live).</summary>
    private static object TemplateDto(WaTemplateDef t, Dictionary<string, List<string>> bindings,
        Dictionary<string, string>? resolvedVars)
    {
        var slots = bindings.GetValueOrDefault(t.Name) ?? new List<string>();
        List<string>? resolved = null;
        if (resolvedVars is Dictionary<string, string> vars && slots.Any(s => s != ""))
            resolved = slots.Select(s => s == "" ? "" : vars.GetValueOrDefault(s, "")).ToList();
        return new
        {
            id = t.Id.Length > 0 ? t.Id : null,
            name = t.Name,
            language = t.Language,
            category = t.Category,
            status = t.Status,
            body = t.Body,
            buttons = t.Buttons.Select(b =>
            {
                var d = new Dictionary<string, string> { ["type"] = b.Type, ["text"] = b.Text };
                if (b.Url.Length > 0) d["url"] = b.Url;
                if (b.PhoneNumber.Length > 0) d["phone_number"] = b.PhoneNumber;
                return d;
            }).ToList(),
            header = t.HeaderFormat.Length > 0 ? new { format = t.HeaderFormat, text = t.HeaderText } : null,
            footer = t.Footer.Length > 0 ? t.Footer : null,
            variables = WhatsAppTemplateCatalog.VariableCount(t.Body),
            // demo templates are built in; custom + live ones are editable
            custom = t.Source != "demo",
            source = t.Source,
            bindings = slots,
            resolved,
        };
    }

    /// <summary>The example values the editor sends (a list, or one comma-separated string).</summary>
    private static List<string> Examples(JsonElement body)
    {
        if (!body.TryGetProperty("example", out var raw)) return new();
        if (raw.ValueKind == JsonValueKind.Array)
            return raw.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString())
                .Where(e => e.Trim().Length > 0).ToList();
        if (raw.ValueKind == JsonValueKind.String)
            return (raw.GetString() ?? "").Split(',').Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        return new();
    }

    /// <summary>
    /// The Graph body for a new / edited template: BODY (+ the example values Meta
    /// requires for every {{n}}, padded when the editor gave fewer) and BUTTONS.
    /// </summary>
    private static Dictionary<string, object?> MetaTemplatePayload(string name, string? language, string category,
        string body, List<object> buttons, List<string> examples)
    {
        var bodyComponent = new Dictionary<string, object?> { ["type"] = "BODY", ["text"] = body };
        var count = VariableCount(body);
        if (count > 0)
        {
            var values = Enumerable.Range(0, count)
                .Select(i => i < examples.Count ? examples[i] : $"example {i + 1}").ToList();
            bodyComponent["example"] = new { body_text = new[] { values } };
        }
        var components = new List<object> { bodyComponent };
        if (buttons.Count > 0) components.Add(new { type = "BUTTONS", buttons });
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["language"] = language ?? "en",
            ["category"] = category,
            ["components"] = components,
        };
    }

    /// <summary>Highest {{n}} placeholder index in a template body (0 = none).</summary>
    private static int VariableCount(string body)
    {
        var max = 0;
        foreach (Match m in PlaceholderRe.Matches(body ?? ""))
            if (int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        return max;
    }

    private record CustomTemplate(string Name, string Language, string Category, string Status,
        string Body, List<object> Buttons);

    /// <summary>
    /// Templates created from Settings → Message Templates while NO live WABA is
    /// linked — one Setting row so the whole flow works offline.
    /// </summary>
    private static List<CustomTemplate> CustomTemplates(ScopingService sc)
    {
        var outv = new List<CustomTemplate>();
        var raw = sc.SettingValue(CustomTemplatesKey);
        if (raw == "") return outv;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return outv;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var buttons = new List<object>();
                if (el.TryGetProperty("buttons", out var b) && b.ValueKind == JsonValueKind.Array)
                    foreach (var one in b.EnumerateArray())
                        buttons.Add(JsonSerializer.Deserialize<object>(one.GetRawText())!);
                outv.Add(new CustomTemplate(
                    Prop(el, "name"), Prop(el, "language", "en"), Prop(el, "category", "UTILITY"),
                    Prop(el, "status", "APPROVED"), Prop(el, "body"), buttons));
            }
        }
        catch { /* a malformed row degrades to "no custom templates" */ }
        return outv;
    }

    /// <summary>{template_name: bindings} for one account scope (wa_config.template_bindings).</summary>
    private static Dictionary<string, List<string>> TemplateBindings(ScopingService sc, string scope) =>
        AllTemplateBindings(sc).TryGetValue(scope, out var forScope)
            ? forScope : new Dictionary<string, List<string>>();

    /// <summary>The whole {scope: {template_name: bindings}} row — one parser,
    /// so a write can never drop another WABA's scope.</summary>
    private static Dictionary<string, Dictionary<string, List<string>>> AllTemplateBindings(
        ScopingService sc)
    {
        var outv = new Dictionary<string, Dictionary<string, List<string>>>();
        var raw = sc.SettingValue(TemplateBindingsKey);
        if (raw == "") return outv;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return outv;
            foreach (var scope in doc.RootElement.EnumerateObject())
            {
                if (scope.Value.ValueKind != JsonValueKind.Object) continue;
                var forScope = new Dictionary<string, List<string>>();
                foreach (var prop in scope.Value.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                    forScope[prop.Name] = prop.Value.EnumerateArray()
                        .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "")
                        .ToList();
                }
                outv[scope.Name] = forScope;
            }
        }
        catch { /* malformed row -> no bindings, never a 500 */ }
        return outv;
    }

    private static Dictionary<string, string>? SafeVariables(FlowEngine flows, Contact contact)
    {
        // The picker's pre-fill must never break the template list.
        try { return flows.VariablesFor(contact); }
        catch { return null; }
    }

    /// <summary>store.normalize_wa_id — digits only (a wa_id is never punctuated).</summary>
    private static string NormalizeWaId(string? raw) =>
        new string((raw ?? "").Where(char.IsDigit).ToArray());

    // ======================= account helpers =================================

    /// <summary>
    /// views._account_payload — the ONE account shape the dashboard reads
    /// (Settings → Channels → WhatsApp, the chats page's sender picker). Wire
    /// format is snake_case and <c>id</c> is a STRING, because the env-managed
    /// pseudo-account has no integer pk. Secrets only ever leave masked.
    /// </summary>
    internal static object AccountPayload(ChannelAccount a) => new
    {
        id = a.Id.ToString(),
        name = a.Name,
        channel = a.Channel,
        provider = string.IsNullOrEmpty(a.Provider) ? Providers.Cloud : a.Provider,
        // operating role (reminder cannon vs conversation layer) — always
        // resolved, so an unset config defaults by provider
        role = AccountRole(a),
        phone_number_id = a.ExternalId,
        waba_id = a.WabaId,               // not a secret; needed for templates
        api_version = string.IsNullOrEmpty(a.ApiVersion) ? "v23.0" : a.ApiVersion,
        owner = a.Owner,
        created = Iso(a.CreatedAt) ?? "",
        access_token_masked = Mask(a.AccessToken),
        app_secret_masked = Mask(a.AppSecret),
        configured = IsConfigured(a),
        env_managed = false,
        // Entity binding: which outlet owns this number. null = a GLOBAL number
        // the whole organization shares — it also decides which outlet inbound
        // chats land under.
        entity = a.Entity?.Slug,
        entity_name = a.Entity?.Name ?? "",
        health_status = a.HealthStatus,
        // Per-account overrides ("" = inherit the organization-wide value).
        // Edited from Settings → Channels → edit account → Delivery & AI.
        delivery = DeliveryPayload(a),
        // "Who answers it": owning team (null = none) + AI default ("" = role decides)
        team_id = InboxSettings.TeamId(a),
        ai_default = InboxSettings.AiDefaultWire(a),
        ai_review_mode = InboxSettings.ReviewModeWire(a),
        // recalled messages ("delete for everyone"): kept, or erased (default) — QR numbers report them
        keep_recalled = InboxSettings.KeepRecalled(a),
        recalls_supported = MessageRecalls.Supports(a),
        // official = template-capable + bound by the 24h window (Cloud API, YCloud, Twilio)
        official = WhatsAppProviders.IsOfficial(a),
        provider_label = WhatsAppProviders.Label(a.Provider),
        template_registry = WhatsAppProviders.HasTemplateRegistry(a),
        supports_reactions = WhatsAppProviders.SupportsReactions(a),
        supports_quotes = WhatsAppProviders.SupportsQuotes(a),
        // YCloud / Twilio: this account's own signed webhook + its provider settings
        webhook_path = WhatsAppProviders.WebhookPath(a),
        bsp = BspPayload(a),
        // QR numbers: stay offline between sends so the phone keeps its notifications
        keep_phone_notifications = BaileysSettings.KeepPhone(a),
        // QR numbers: how much of the number's own chat history to import when it links
        import_history_days = BaileysSettings.ImportHistoryDays(a),
        import_history_supported = WhatsAppProviders.Of(a) == Providers.Baileys,
        // the connection method a Connected card shows: cloud | qr | ycloud | twilio | managed
        method = WhatsAppProviders.Of(a) switch
        {
            Providers.Baileys => "qr",
            Providers.Zernio => ChatBranch.Data.Social.ZernioNative.Method,
            var p => p,
        },
        method_label = WhatsAppProviders.Of(a) == Providers.Zernio ? ChatBranch.Data.Social.ZernioNative.MethodLabel : null,
        // a managed connection (Zernio): its number, status and the twin ids — null otherwise
        managed = ChatBranch.Data.Social.ZernioNative.Is(a) ? ChatBranch.Data.Social.ZernioNative.Payload(a) : null,
    };

    /// <summary>The provider-specific, non-secret settings of a YCloud / Twilio account (secrets masked).</summary>
    internal static Dictionary<string, object?>? BspPayload(ChannelAccount a) => WhatsAppProviders.Of(a) switch
    {
        Providers.YCloud => new Dictionary<string, object?>
        {
            ["phone_number"] = WhatsAppProviders.E164(a.ExternalId),
            ["api_key_masked"] = Mask(a.AccessToken),
            ["webhook_secret_masked"] = Mask(a.AppSecret),
        },
        Providers.Twilio => new Dictionary<string, object?>
        {
            ["account_sid"] = WhatsAppProviders.Cfg(a, "account_sid"),
            ["from_number"] = WhatsAppProviders.Cfg(a, "from_number"),
            ["messaging_service_sid"] = WhatsAppProviders.Cfg(a, "messaging_service_sid"),
            ["api_key_sid"] = WhatsAppProviders.Cfg(a, "api_key_sid"),
            ["api_key_secret_masked"] = Mask(WhatsAppProviders.Cfg(a, TwilioSecretKey)),
            ["auth_token_masked"] = Mask(a.AccessToken),
        },
        _ => null,
    };

    /// <summary>
    /// What a YCloud / Twilio account write needs: YCloud a business number (E.164) + API
    /// key; Twilio an Account SID (AC…), an Auth Token or an API Key SID + secret, and a
    /// WhatsApp sender number or a Messaging Service SID (MG…). On an edit only what the
    /// body changes is checked, against what the row already holds.
    /// </summary>
    private static string? BspCredentialError(string provider, JsonElement body, bool creating, ChannelAccount? current)
    {
        string Val(string key, string existing) =>
            TryString(body, key, out var raw) && !raw.Contains("••") ? raw.Trim() : creating ? "" : existing;
        if (WhatsAppProviders.Normalize(provider) == Providers.YCloud)
        {
            var phone = Val("phone_number", current == null ? "" : WhatsAppProviders.E164(current.ExternalId));
            if (phone.Length == 0) phone = Val("phone_number_id", "");
            var key = Val("access_token", current?.AccessToken ?? "");
            if (WhatsAppProviders.Digits(phone).Length is < 7 or > 15)
                return "phone_number is required — the WhatsApp business number in E.164 form (+60123456789).";
            if (key.Length == 0) return "access_token (the YCloud API key) is required.";
            return null;
        }
        string Cfg(string k) => current == null ? "" : WhatsAppProviders.Cfg(current, k);
        var sid = Val("account_sid", Cfg("account_sid"));
        var token = Val("access_token", current?.AccessToken ?? "");
        var keySid = Val("api_key_sid", Cfg("api_key_sid"));
        var keySecret = Val(TwilioSecretKey, Cfg(TwilioSecretKey));
        var from = Val("from_number", Cfg("from_number"));
        var service = Val("messaging_service_sid", Cfg("messaging_service_sid"));
        if (!System.Text.RegularExpressions.Regex.IsMatch(sid, "^AC[0-9a-fA-F]{32}$"))
            return "account_sid must be your Twilio Account SID (AC followed by 32 hex characters).";
        if (token.Length == 0 && (keySid.Length == 0 || keySecret.Length == 0))
            return "access_token (the Twilio Auth Token) — or api_key_sid + api_key_secret — is required.";
        if (keySid.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(keySid, "^SK[0-9a-fA-F]{32}$"))
            return "api_key_sid must be a Twilio API Key SID (SK followed by 32 hex characters).";
        var fromDigits = WhatsAppProviders.Digits(from.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) ? from[9..] : from);
        if (from.Length > 0 && fromDigits.Length is < 7 or > 15)
            return "from_number must be the WhatsApp sender in E.164 form (+14155238886 or whatsapp:+14155238886).";
        if (service.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(service, "^MG[0-9a-fA-F]{32}$"))
            return "messaging_service_sid must be a Messaging Service SID (MG followed by 32 hex characters).";
        if (from.Length == 0 && service.Length == 0)
            return "Set the WhatsApp sender (from_number) or a messaging_service_sid.";
        return null;
    }

    /// <summary>The stored value behind a BSP write key (to tell a real change from a re-send).</summary>
    private static string BspValue(ChannelAccount row, string key) => key switch
    {
        "phone_number" => WhatsAppProviders.E164(row.ExternalId),
        _ => WhatsAppProviders.Cfg(row, key),
    };

    /// <summary>
    /// Store a YCloud / Twilio write. YCloud: the number → ExternalId (digits). Twilio: the SID,
    /// sender, service, API key in Config (a masked secret = untouched), ExternalId = the sender's
    /// digits (else the service SID). The token / secret / WABA keys go through the shared fields.
    /// </summary>
    private static void ApplyBsp(ChannelAccount row, JsonElement body)
    {
        if (WhatsAppProviders.Of(row) == Providers.YCloud)
        {
            var phone = TryString(body, "phone_number", out var p) ? p : TryString(body, "phone_number_id", out var q) ? q : "";
            if (WhatsAppProviders.Digits(phone).Length > 0) row.ExternalId = WhatsAppProviders.Digits(phone);
            return;
        }
        var cfg = new Dictionary<string, object?>(row.Config);
        foreach (var key in TwilioPlainKeys)
            if (TryString(body, key, out var raw)) cfg[key] = raw.Trim();
        if (TryString(body, TwilioSecretKey, out var secret) && !secret.Contains("••")) cfg[TwilioSecretKey] = secret.Trim();
        if (EmailConfig.Cfg(cfg, "from_number") is { Length: > 0 } from)
        {
            var bare = from.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) ? from[9..] : from;
            cfg["from_number"] = WhatsAppProviders.E164(bare);
        }
        row.Config = cfg;
        var fromNumber = EmailConfig.Cfg(cfg, "from_number");
        row.ExternalId = fromNumber.Length > 0 ? WhatsAppProviders.Digits(fromNumber) : EmailConfig.Cfg(cfg, "messaging_service_sid");
    }

    /// <summary>
    /// Twilio sends delivery statuses to a per-message StatusCallback: this account's own
    /// webhook, when the API knows a public https address for itself. True when it changed.
    /// </summary>
    private static bool StampStatusCallback(ChannelAccount row, HttpContext ctx)
    {
        if (WhatsAppProviders.Of(row) != Providers.Twilio || row.Id == 0) return false;
        var sc = ctx.RequestServices.GetRequiredService<ScopingService>();
        var b = MessagingAccountTransport.PublicBaseUrl(null, sc, ctx);
        var url = MediaLinks.Reachable(b) ? b + WhatsAppProviders.WebhookPath(row) : "";
        if (WhatsAppProviders.Cfg(row, "status_callback") == url) return false;
        row.Config = new Dictionary<string, object?>(row.Config) { ["status_callback"] = url };
        return true;
    }

    /// <summary>
    /// "Keep phone notifications" on a QR (Baileys) number (<see cref="BaileysSettings"/>):
    /// the body's <c>keep_phone_notifications</c> true / false. Other providers ignore it.
    /// True when it changed.
    /// </summary>
    /// <summary>
    /// "Import past chats" on a QR (Baileys) number: the body's
    /// <c>import_history_days</c> (0 = off, 30 / 90, -1 = everything WhatsApp sends).
    /// Other providers ignore it — the official APIs serve no history at all. Returns a
    /// 400 result for a value that is not one of the offered windows.
    /// </summary>
    private static IResult? ApplyHistoryImport(ChannelAccount row, JsonElement body)
    {
        if (WhatsAppProviders.Of(row) != Providers.Baileys
            || !Has(body, BaileysSettings.ImportHistoryDaysKey)) return null;
        var raw = (AsString(body, BaileysSettings.ImportHistoryDaysKey) ?? "").Trim();
        if (!int.TryParse(raw, out var days) || !BaileysSettings.HistoryChoices.Contains(days))
            return Results.Json(new
            {
                detail = "import_history_days must be one of "
                         + string.Join(", ", BaileysSettings.HistoryChoices)
                         + " (0 = import nothing, -1 = everything WhatsApp sends).",
            }, statusCode: 400);
        row.Config = new Dictionary<string, object?>(row.Config)
        {
            [BaileysSettings.ImportHistoryDaysKey] = days.ToString(),
        };
        return null;
    }

    private static bool ApplyPhoneNotifications(ChannelAccount row, JsonElement body)
    {
        if (WhatsAppProviders.Of(row) != Providers.Baileys
            || AsBool(body, BaileysSettings.KeepPhoneNotifications) is not bool on) return false;
        var before = BaileysSettings.KeepPhone(row);
        var cfg = new Dictionary<string, object?>(row.Config);
        if (on) cfg[BaileysSettings.KeepPhoneNotifications] = true;
        else cfg.Remove(BaileysSettings.KeepPhoneNotifications);
        row.Config = cfg;
        return before != on;
    }

    internal static Dictionary<string, string> DeliveryPayload(ChannelAccount a) =>
        AccountDelivery.Keys.ToDictionary(k => k, k => AccountDelivery.Own(a, k));

    /// <summary>
    /// Validate + store the per-account delivery keys carried at the top level
    /// of an account write body. Blank/null clears the override (inherit). Keys
    /// absent from the body are untouched. Returns a 400 result on bad input.
    /// </summary>
    internal static IResult? ApplyDelivery(ChannelAccount row, JsonElement body)
    {
        if (!AccountDelivery.Keys.Any(k => Has(body, k))) return null;
        var cfg = new Dictionary<string, object?>(row.Config);
        string Next(string key) => Has(body, key)
            ? (AsString(body, key) ?? "").Trim()
            : AccountDelivery.Own(row, key);

        foreach (var key in new[] { AccountDelivery.QuietStart, AccountDelivery.QuietEnd })
        {
            var v = Next(key);
            if (v.Length > 0 && !AccountDelivery.TryParseHhMm(v, out _))
                return Results.Json(new { detail = $"{key} must be HH:MM (24h) or blank." },
                                    statusCode: 400);
        }
        // Per-weekday schedule: valid JSON with at least one parsable day, or
        // blank. Refusing malformed JSON here beats silently ignoring it later.
        var scheduleRaw = Next(AccountDelivery.QuietSchedule);
        if (scheduleRaw.Length > 0 && AccountDelivery.ParseSchedule(scheduleRaw).Count == 0)
            return Results.Json(new
            {
                detail = "quiet_schedule must be JSON like "
                         + "{\"mon\":{\"on\":true,\"start\":\"21:00\",\"end\":\"09:00\"}} or blank.",
            }, statusCode: 400);
        var minRaw = Next(AccountDelivery.MinGap);
        var maxRaw = Next(AccountDelivery.MaxGap);
        int? min = null, max = null;
        if (minRaw.Length > 0)
        {
            if (!int.TryParse(minRaw, out var lo) || lo < 0)
                return Results.Json(new { detail = "queue_min_gap_s must be a whole number of seconds (0 or more)." },
                                    statusCode: 400);
            min = lo;
        }
        if (maxRaw.Length > 0)
        {
            if (!int.TryParse(maxRaw, out var hi) || hi < 0)
                return Results.Json(new { detail = "queue_max_gap_s must be a whole number of seconds (0 or more)." },
                                    statusCode: 400);
            max = hi;
        }
        if (min is int a && max is int b && a > b)
            return Results.Json(new { detail = "queue_min_gap_s cannot exceed queue_max_gap_s." },
                                statusCode: 400);
        var llm = Next(AccountDelivery.LlmProvider).ToLowerInvariant();
        if (llm.Length > 0 && !VoiceMessagingEndpoints.LlmProviders.Contains(llm))
            return Results.Json(new
            {
                detail = $"llm_provider must be one of {string.Join(", ", VoiceMessagingEndpoints.LlmProviders)} or blank.",
            }, statusCode: 400);
        var stt = Next(AccountDelivery.SttProvider).ToLowerInvariant();
        if (stt.Length > 0 && !VoiceMessagingEndpoints.SttProviders.Contains(stt))
            return Results.Json(new
            {
                detail = $"stt_provider must be one of {string.Join(", ", VoiceMessagingEndpoints.SttProviders)} or blank.",
            }, statusCode: 400);

        void Put(string key, string value)
        {
            if (!Has(body, key)) return;
            if (value.Length == 0) cfg.Remove(key); else cfg[key] = value;
        }
        Put(AccountDelivery.QuietStart, Next(AccountDelivery.QuietStart));
        Put(AccountDelivery.QuietEnd, Next(AccountDelivery.QuietEnd));
        Put(AccountDelivery.QuietSchedule, scheduleRaw);
        Put(AccountDelivery.MinGap, min?.ToString() ?? "");
        Put(AccountDelivery.MaxGap, max?.ToString() ?? "");
        Put(AccountDelivery.LlmProvider, llm);
        Put(AccountDelivery.SttProvider, stt);
        row.Config = cfg;
        return null;
    }

    /// <summary>
    /// wa_config._env_account rendered through the same payload — the
    /// read-only row the dashboard shows when backend/.env carries WhatsApp
    /// credentials. null when .env configures nothing usable.
    /// </summary>
    internal static object? EnvAccountPayload()
    {
        if (!EnvAccountConfigured()) return null;
        var apiVersion = Env("WHATSAPP_API_VERSION");
        return new
        {
            id = EnvAccountId,
            name = Env("WHATSAPP_ACCOUNT_NAME") is { Length: > 0 } n ? n : "From backend/.env",
            channel = Channels.WhatsApp,
            provider = Providers.Cloud,
            role = "reminder",
            phone_number_id = Env("WHATSAPP_PHONE_NUMBER_ID"),
            waba_id = Env("WHATSAPP_WABA_ID"),
            api_version = apiVersion != "" ? apiVersion : "v23.0",
            owner = Roles.SuperAdmin,
            created = "",
            access_token_masked = Mask(Env("WHATSAPP_ACCESS_TOKEN")),
            app_secret_masked = Mask(Env("WHATSAPP_APP_SECRET")),
            configured = true,
            env_managed = true,
            entity = (string?)null,
            entity_name = "",
            // no DB row -> the health monitor never tracks it
            health_status = Health.Unknown,
        };
    }

    /// <summary>
    /// Channel-appropriate "usable" test (routing._configured): WhatsApp cloud
    /// needs token + phone_number_id, baileys is gateway-enforced, email needs
    /// SMTP host+login in config, other channels need an access_token.
    /// </summary>
    internal static bool IsConfigured(ChannelAccount a) => Routing.IsConfigured(a);

    /// <summary>
    /// wa_config.account_role — "reminder" | "conversation". Unset/invalid values
    /// default by provider: cloud → reminder, baileys → conversation.
    /// </summary>
    internal static string AccountRole(ChannelAccount a) => ChatBranch.Data.Channels.AccountRoles.Of(a);

    internal static string Cfg(ChannelAccount a, string key) =>
        a.Config.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    /// <summary>views._invalid_role — 400 when the payload carries a bad role.</summary>
    private static IResult? RoleError(JsonElement body)
    {
        if (!Has(body, "role")) return null;
        var role = (AsString(body, "role") ?? "").Trim().ToLowerInvariant();
        return AccountRoles.Contains(role)
            ? null
            : Results.Json(new { detail = "role must be 'reminder' or 'conversation'." },
                           statusCode: 400);
    }

    /// <summary>
    /// This account's explicit per-chat chatbot tick on a thread, or null when
    /// the thread has none and the account's ROLE decides (store._effective_ai).
    /// </summary>
    internal static bool? AiOverride(ChatBranch.Domain.Thread t, int accountId) =>
        ChatBranch.Data.Ai.AiEligibility.Override(t, accountId);

    /// <summary>
    /// store._effective_ai — the Chatbot tick for the thread's CURRENT sending
    /// account. ONE rule (<see cref="ChatBranch.Data.Ai.AiEligibility"/>) shared
    /// with the inbound auto-reply gate, the queue and the AI monitor, so what
    /// the dashboard shows is what the AI does.
    /// </summary>
    internal static bool EffectiveAi(ChatBranch.Domain.Thread t, ChannelAccount? account,
        bool reminderDefaultOn) =>
        ChatBranch.Data.Ai.AiEligibility.Effective(t, account, reminderDefaultOn);

    // ---- Baileys pairing (sandbox stub) --------------------------------------

    private static async Task<ChannelAccount?> BaileysAccount(AppDbContext db, int id) =>
        await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(
            a => a.Id == id && a.Channel == Channels.WhatsApp && a.Provider == Providers.Baileys);

    private static IResult BaileysNotFound() =>
        Results.NotFound(new { detail = "Not a Baileys account." });

    /// <summary>
    /// The pairing state shape the QR dialog polls. There is no gateway to
    /// proxy to in this port, so it stays "stopped" with the reason in `error`
    /// rather than inventing a QR code nobody can scan.
    /// </summary>
    private static object BaileysStatePayload(ChatBranch.Data.Sidecars.BaileysStatus st) => new
    {
        ok = st.State is not "error",
        state = st.State,
        qr = st.Qr,
        // state "pairing_code": the person types this on the phone instead of scanning
        pairing_code = st.PairingCode,
        pairing_phone = st.PairingPhone,
        me = st.Me,
        error = st.Error,
    };

    /// <summary>
    /// The gateway-side account id for a Baileys row: ExternalId when set, else
    /// the DB id — persisted so the mapping is stable across restarts and the
    /// gateway's inbound pushes ({account_id}) resolve to this row.
    /// </summary>
    private static async Task<string> EnsureBaileysKey(AppDbContext db, ChannelAccount acct)
    {
        if (!string.IsNullOrWhiteSpace(acct.ExternalId)) return acct.ExternalId;
        var tracked = await db.ChannelAccounts.FirstAsync(a => a.Id == acct.Id);
        tracked.ExternalId = acct.Id.ToString();
        await db.SaveChangesAsync();
        return tracked.ExternalId;
    }

    internal static bool EnvAccountConfigured() =>
        Env("WHATSAPP_ACCESS_TOKEN") != "" && Env("WHATSAPP_PHONE_NUMBER_ID") != "";

    // ======================= settings helpers ================================

    /// <summary>Setting key actually used for storage (the webhook's spelling wins).</summary>
    private static string StorageKey(string key) => key == "verify_token" ? VerifyTokenKey : key;

    /// <summary>An org-wide app setting: Setting row → .env → "".</summary>
    private static string Global(ScopingService sc, string key)
    {
        var v = sc.SettingValue(StorageKey(key));
        if (v == "" && key == "verify_token") v = sc.SettingValue(VerifyTokenKeyLegacy);
        if (v != "") return v;
        return GlobalEnv.TryGetValue(key, out var envName) ? Env(envName) : "";
    }

    private static string Effective(ScopingService sc, string key, string envName)
    {
        var v = sc.SettingValue(key);
        return v != "" ? v : Env(envName);
    }

    internal static string Env(string name) => (Environment.GetEnvironmentVariable(name) ?? "").Trim();

    /// <summary>
    /// The effective webhook verify token — NEVER the shipped default. When
    /// neither env nor a Setting row overrides it, generate a random secret once
    /// and persist it (wa_config.verify_token).
    /// </summary>
    private static string VerifyToken(ScopingService sc)
    {
        var token = Global(sc, "verify_token");
        if (token != "" && token != DefaultVerifyToken) return token;
        var generated = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("+", "").Replace("/", "").Replace("=", "");
        try
        {
            sc.SetSetting(VerifyTokenKey, generated);
            sc.SetSetting(VerifyTokenKeyLegacy, generated);
        }
        catch { /* read-only DB: still report a usable token rather than 500 */ }
        return generated;
    }

    internal static bool AiAvailable(ScopingService sc) =>
        ChatBranch.Data.Ai.LlmProviderCatalog.All.Any(p => !p.KeyOptional && Global(sc, p.KeySetting) != "");

    private static int IntSetting(ScopingService sc, string key, int fallback, int? entityId = null)
    {
        var v = sc.SettingValue(key, entityId);
        if (v == "") v = Env(key.ToUpperInvariant());
        return int.TryParse(v, out var n) ? n : fallback;
    }

    private static Dictionary<string, string> StoredVoiceSettings(ScopingService sc) =>
        ReadJsonObject(sc.SettingValue(VoiceSettingsKey));

    /// <summary>
    /// Effective Twilio SIP trunk creds: the dashboard row wins per field, .env
    /// is the fallback — same precedence as every other channel's config.
    /// </summary>
    private static Dictionary<string, string> VoiceEffective(ScopingService sc)
    {
        var stored = StoredVoiceSettings(sc);
        string Pick(string key, string envName)
        {
            var v = (stored.GetValueOrDefault(key) ?? "").Trim();
            return v != "" ? v : Env(envName);
        }
        return new Dictionary<string, string>
        {
            ["sip_username"] = Pick("sip_username", "TWILIO_SIP_USERNAME"),
            ["sip_password"] = Pick("sip_password", "TWILIO_SIP_PASSWORD"),
            ["termination_uri"] = Pick("termination_uri", "TWILIO_TERMINATION_URI"),
        };
    }

    /// <summary>
    /// The LiveKit outbound trunk id is .env-managed (one-time setup, read by the
    /// Django-free agent worker at boot); the termination URI may come from the
    /// dashboard row instead.
    /// </summary>
    private static bool SipTrunkConfigured(ScopingService sc) =>
        Env("SIP_OUTBOUND_TRUNK_ID") != "" && VoiceEffective(sc)["termination_uri"] != "";

    /// <summary>Flat {string: string} view of a JSON-object Setting row.</summary>
    private static Dictionary<string, string> ReadJsonObject(string raw)
    {
        var outv = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(raw)) return outv;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return outv;
            foreach (var prop in doc.RootElement.EnumerateObject())
                outv[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.ToString();
        }
        catch { /* malformed row -> empty, never a 500 */ }
        return outv;
    }

    // ======================= primitives ======================================

    /// <summary>_mask: secrets round-trip as "••••1234", never in full.</summary>
    internal static string Mask(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        return secret.Length >= 8 ? $"••••{secret[^4..]}" : "••••";
    }

    /// <summary>'21:30' -> minutes since midnight (1290), or null when invalid/empty.</summary>
    private static int? ParseHhmm(string? value)
    {
        var parts = (value ?? "").Trim().Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        if (h < 0 || h > 23 || m < 0 || m > 59) return null;
        return h * 60 + m;
    }

    internal static string? Iso(DateTime? dt) =>
        dt is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("o") : null;

    private static string Prop(JsonElement el, string name, string fallback = "") =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    // ---- request-body helpers (partial patch: absent key == untouched) -------

    private static async Task<JsonElement> ReadBody(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            return doc.RootElement.Clone();
        }
        catch
        {
            // covers an empty body AND non-JSON garbage (Django's _json_body)
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    private static bool Has(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out _);

    private static bool TryString(JsonElement body, string key, out string value)
    {
        value = "";
        if (body.ValueKind != JsonValueKind.Object) return false;
        if (!body.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return true;
    }

    private static string? AsString(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => el.ToString(),
        };
    }

    private static bool? AsBool(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String)
            return (el.GetString() ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes";
        return null;
    }

    private static int? AsInt(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString()?.Trim(), out var s)) return s;
        return null;
    }

    // ---- audit --------------------------------------------------------------

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

    /// <summary>rbac.notify(("superadmin","admin"), …) — never breaks the write.</summary>
    private static async Task NotifyManagers(AppDbContext db, string title, string body)
    {
        await NotificationService.NotifyAsync(db, NotificationCategories.Admin, "system",
            title, body, "/channels", NotificationAudience.Admins());
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}
