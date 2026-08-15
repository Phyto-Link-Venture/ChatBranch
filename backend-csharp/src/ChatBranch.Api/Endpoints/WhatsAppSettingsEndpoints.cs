using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Flows;
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
    };

    private static readonly string[] ChatProviders = { "ilmu", "openrouter", "openai", "google", "local" };

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
        { "access_token", "phone_number_id", "app_secret", "api_version", "provider", "waba_id" };

    // =========================================================================

    public static void MapWhatsAppSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- global app settings ------------------------------------------
        // Reads need any authenticated role; writes are SUPER ADMIN only.
        app.MapGet("/api/whatsapp/settings", async (AppDbContext db, ScopingService sc) =>
            Results.Ok(await GlobalPayload(db, sc)))
            .RequireAuthorization(AuthPolicies.Authenticated);

        // Django exposes this as POST (both Settings pages call POST); PUT is
        // accepted too so either verb patches the same partial body.
        Func<HttpContext, AppDbContext, ScopingService, ClaimsPrincipal, Task<IResult>> saveSettings =
            async (ctx, db, sc, me) =>
        {
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
        app.MapPost("/api/whatsapp/settings", saveSettings).RequireAuthorization(AuthPolicies.SuperAdmin);
        app.MapPut("/api/whatsapp/settings", saveSettings).RequireAuthorization(AuthPolicies.SuperAdmin);

        // ---- routing rules (client operating model) ------------------------
        app.MapGet("/api/whatsapp/routing-rules", async (string? entity, AppDbContext db,
            ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            return Results.Ok(RulesPayload(sc, entityId));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // PUT = admin+ (matches the assignments gating).
        app.MapPut("/api/whatsapp/routing-rules", async (string? entity, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
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
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { detail = ex.Message }, statusCode: 400);
            }

            var touched = patch.Keys.Concat(queuePatch.Keys).OrderBy(k => k);
            await Audit(db, ctx, me, "settings.update", "routing-rules",
                        $"keys: {string.Join(", ", touched)}");
            return Results.Ok(RulesPayload(sc, entityId));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- voice (Twilio SIP trunk) settings -----------------------------
        // Read = any authenticated role, password masked. The trunk id itself
        // (SIP_OUTBOUND_TRUNK_ID) stays .env-only: the voice-agent worker is a
        // separate process that reads it once at boot.
        app.MapGet("/api/whatsapp/voice-settings", (ScopingService sc) =>
            Results.Ok(VoicePayload(sc)))
            .RequireAuthorization(AuthPolicies.Authenticated);

        // ---- approved templates --------------------------------------------
        app.MapGet("/api/whatsapp/templates", async (string? account_id, string? wa_id,
            AppDbContext db, ScopingService sc, FlowEngine flows) =>
        {
            var account = await TemplateAccount(db, account_id);
            var bindings = TemplateBindings(sc, BindingsScope(account));

            // ?wa_id=… resolves each template's bound slots against that thread's
            // contact so the picker can pre-fill instead of asking the operator.
            Dictionary<string, string>? resolvedVars = null;
            var key = NormalizeWaId(wa_id);
            if (key != "")
            {
                var thread = await db.Threads.AsNoTracking().Include(t => t.Contact)
                    .FirstOrDefaultAsync(t => t.Channel == Channels.WhatsApp && t.Key == key);
                var contact = thread?.Contact;
                if (contact != null) resolvedVars = SafeVariables(flows, contact);
            }

            var templates = new List<object>();
            foreach (var t in DemoTemplates)
                templates.Add(TemplateDto(t.Name, t.Language, t.Category, t.Status, t.Body,
                                          new List<object>(), custom: false, bindings, resolvedVars));
            foreach (var t in CustomTemplates(sc))
                templates.Add(TemplateDto(t.Name, t.Language, t.Category, t.Status, t.Body,
                                          t.Buttons, custom: true, bindings, resolvedVars));

            // NOTE: live listing from Meta's registry is not ported yet — a linked
            // WABA reports sandbox=false but still serves the locally known set.
            return Results.Ok(new { templates, sandbox = account == null });
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- channel health monitor ----------------------------------------
        app.MapGet("/api/whatsapp/channels/health", async (string? entity, AppDbContext db,
                ScopingService sc) =>
            Results.Ok(await ChannelsHealthPayload(db, sc, await InboxEndpoints.EntityId(db, entity))))
            .RequireAuthorization(AuthPolicies.Authenticated);

        // POST = the dashboard card's "Check now" (admin+). There is no live
        // prober in this port — the Cloud/SMTP/Telegram probes live in the
        // Django checker thread — so this stamps the check time and re-reads
        // the monitor state instead of opening sockets. Same payload as the
        // GET, which is what lib/channelHealth.ts expects back.
        app.MapPost("/api/whatsapp/channels/health", async (string? entity, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            try { sc.SetSetting(CheckedAtKey, Iso(DateTime.UtcNow)); }
            catch { /* read-only DB: still answer with the current state */ }
            await Audit(db, ctx, me, "account.health_check", "channels",
                        "manual run from dashboard");
            return Results.Ok(await ChannelsHealthPayload(db, sc, entityId));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- conversation-account recovery funnel --------------------------
        // Conversation-role accounts currently banned/disconnected with at least
        // one assigned contact — the dashboard's one-click recovery list.
        app.MapGet("/api/whatsapp/recovery", async (AppDbContext db) =>
        {
            var assigned = await db.AccountAssignments.AsNoTracking()
                .Where(a => a.Channel == Channels.WhatsApp)
                .GroupBy(a => a.AccountId)
                .Select(g => new { AccountId = g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.AccountId, x => x.N);

            var down = await db.ChannelAccounts.AsNoTracking()
                .Where(a => a.Channel == Channels.WhatsApp && IneligibleStatuses.Contains(a.HealthStatus))
                .OrderBy(a => a.Id).ToListAsync();

            var accounts = down
                .Where(a => AccountRole(a) == "conversation")
                .Select(a => new
                {
                    account_id = a.Id.ToString(),
                    name = a.Name,
                    health = a.HealthStatus,
                    affected = assigned.GetValueOrDefault(a.Id, 0),
                })
                .Where(a => a.affected >= 1)
                .ToList();

            return Results.Ok(new { accounts });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST (admin+): queue the recovery template for every contact assigned
        // to this down conversation account — the manual counterpart of
        // recovery_mode="auto" (health.enqueue_recovery). The jobs ride the
        // outbound queue, which routes them through a reminder/Cloud account.
        app.MapPost("/api/whatsapp/recovery/{accountId:int}", async (int accountId, HttpContext ctx,
            AppDbContext db, ScopingService sc, QueueService queue, ClaimsPrincipal me) =>
        {
            var row = await db.ChannelAccounts
                .FirstOrDefaultAsync(a => a.Id == accountId && a.Channel == Channels.WhatsApp);
            if (row == null) return Results.NotFound(new { detail = "Account not found." });

            var template = sc.SettingValue("recovery_template", row.EntityId);
            if (template == "")
                return Results.Json(new
                {
                    detail = "No recovery template configured — set 'recovery_template' " +
                             "under Settings → Routing rules first.",
                }, statusCode: 409);

            // Templates NEVER fail over to Baileys: the carrier has to be a
            // healthy Cloud number (routing.eligible_accounts provider="cloud").
            var carriers = (await db.ChannelAccounts.AsNoTracking()
                    .Where(a => a.Channel == Channels.WhatsApp && a.Provider == Providers.Cloud)
                    .ToListAsync())
                .Where(a => IsConfigured(a) && !IneligibleStatuses.Contains(a.HealthStatus))
                .ToList();
            if (carriers.Count == 0)
                return Results.Json(new
                {
                    detail = "No healthy reminder (Cloud API) account to carry the " +
                             "recovery template — reconnect one under Settings → Channels.",
                }, statusCode: 409);

            var affected = await db.AccountAssignments.AsNoTracking().Include(a => a.Contact)
                .Where(a => a.AccountId == accountId && a.Channel == Channels.WhatsApp)
                .ToListAsync();
            var queued = 0;
            foreach (var asg in affected)
            {
                var phone = asg.Contact?.Phone ?? "";
                if (phone == "") continue; // nothing to address the template to
                // Account resolved AT SEND TIME by the queue (health failover),
                // so the job carries no pinned account.
                queue.Enqueue(Channels.WhatsApp, JobKind.Template, phone,
                    new Dictionary<string, object?>
                    {
                        ["name"] = template,
                        ["language"] = "en",
                        ["variables"] = new Dictionary<string, string>(),
                    }, contactId: asg.ContactId);
                queued++;
            }

            await Audit(db, ctx, me, "recovery.start", $"whatsapp:{row.Name}",
                        $"queued {queued} recovery template(s)");
            return Results.Ok(new { queued });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- account registry (create / edit / delete / test) ---------------
        // Reads live in InboxEndpoints (GET /accounts); every mutation is admin+.
        app.MapPost("/api/whatsapp/accounts", async (HttpContext ctx, AppDbContext db,
            ClaimsPrincipal me) =>
        {
            var body = await ReadBody(ctx);
            var provider = (AsString(body, "provider") ?? "").Trim().ToLowerInvariant();
            if (provider != Providers.Baileys) provider = Providers.Cloud;
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
            db.ChannelAccounts.Add(row);
            await db.SaveChangesAsync();

            await Audit(db, ctx, me, "account.create", $"whatsapp:{row.Name}");
            await NotifyManagers(db, $"WhatsApp account linked: {row.Name}",
                                 $"By {me.Identity?.Name ?? "someone"}");
            // Re-read untracked so the payload carries the entity slug without
            // attaching a second copy of the Entity to the tracked graph.
            var saved = await db.ChannelAccounts.AsNoTracking().Include(a => a.Entity)
                .FirstAsync(a => a.Id == row.Id);
            return Results.Ok(AccountPayload(saved));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

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

            Patch("name", () => row.Name, v => row.Name = v);
            Patch("phone_number_id", () => row.ExternalId, v => row.ExternalId = v);
            Patch("waba_id", () => row.WabaId, v => row.WabaId = v);
            Patch("access_token", () => row.AccessToken, v => row.AccessToken = v);
            Patch("app_secret", () => row.AppSecret, v => row.AppSecret = v);
            Patch("api_version", () => row.ApiVersion, v => row.ApiVersion = v);
            Patch("owner", () => row.Owner, v => row.Owner = v);
            Patch("provider", () => row.Provider, v => row.Provider = v);

            var role = (AsString(body, "role") ?? "").Trim().ToLowerInvariant();
            if (Has(body, "role") && AccountRoles.Contains(role))
                // role lives in the config JSON; changing it never needs a health reset
                row.Config = new Dictionary<string, object?>(row.Config) { ["role"] = role };

            var entityChanged = false;
            if (Has(body, "entity"))
            {
                // "" / null explicitly UNBINDS the number back to organization-wide.
                // Only the FK moves — EF fixes the navigation up, and touching it
                // by hand would attach a second copy of the Entity.
                var newEntityId = await InboxEndpoints.EntityId(db, AsString(body, "entity"));
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

            if (entityChanged)
                // Re-file this number's existing conversations under the new
                // outlet — otherwise binding a number leaves its whole history
                // in the old scope and the inbox looks like it lost chats.
                foreach (var thread in await db.Threads.Include(t => t.Contact).Include(t => t.Account)
                             .Where(t => t.AccountId == row.Id).ToListAsync())
                    sc.SyncThreadEntity(thread);

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
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapDelete("/api/whatsapp/accounts/{id:int}", async (int id, HttpContext ctx,
            AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.ChannelAccounts
                .FirstOrDefaultAsync(a => a.Id == id && a.Channel == Channels.WhatsApp);
            if (row == null) return Results.Json(new { detail = "Account not found." },
                                                 statusCode: 400);
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
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // Credential check. Django proves the keys by fetching the number
        // profile off Graph; no Graph client is ported here, so this reports
        // the CONFIGURED + monitor state instead of opening a socket — it can
        // say "these fields are filled in", never "Meta accepted them".
        app.MapPost("/api/whatsapp/accounts/{id:int}/test", async (int id, AppDbContext db) =>
        {
            var row = await db.ChannelAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id && a.Channel == Channels.WhatsApp);
            if (row == null || !IsConfigured(row))
                return Results.Json(new { detail = "Account not configured." }, statusCode: 400);
            var number = Cfg(row, "number");
            return Results.Ok(new
            {
                ok = row.HealthStatus != Health.Disconnected && row.HealthStatus != Health.Banned,
                display_phone_number = number != "" ? number : "",
                verified_name = row.Name,
                quality_rating = row.HealthStatus,
                detail = "Credentials are present (no live provider probe in this build).",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- Baileys pairing control (proxied to the Node gateway) ---------
        // Baileys is a supported channel: start/status/logout drive the sidecar
        // (backend/baileys_service) through IBaileysGateway. The gateway keys
        // sessions by account id — we use the row's ExternalId when set, else
        // the DB id (stable + unique), and persist that so inbound pushes
        // (/api/whatsapp/baileys/inbound {account_id}) route back to this row.
        // An unreachable gateway is a clean {state:"error", error} — never a 500.
        app.MapPost("/api/whatsapp/accounts/{id:int}/baileys/start", async (int id, HttpContext ctx,
            AppDbContext db, ClaimsPrincipal me, ChatBranch.Data.Sidecars.IBaileysGateway gw) =>
        {
            var acct = await BaileysAccount(db, id);
            if (acct == null) return BaileysNotFound();
            var key = await EnsureBaileysKey(db, acct);
            var st = await gw.StartAsync(key);
            await Audit(db, ctx, me, "account.baileys_start", $"whatsapp:{id}", st.State);
            return Results.Ok(BaileysStatePayload(st));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapGet("/api/whatsapp/accounts/{id:int}/baileys/status", async (int id, AppDbContext db,
            ChatBranch.Data.Sidecars.IBaileysGateway gw) =>
        {
            var acct = await BaileysAccount(db, id);
            if (acct == null) return BaileysNotFound();
            var key = await EnsureBaileysKey(db, acct);
            return Results.Ok(BaileysStatePayload(await gw.StatusAsync(key)));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/whatsapp/accounts/{id:int}/baileys/logout", async (int id, HttpContext ctx,
            AppDbContext db, ClaimsPrincipal me, ChatBranch.Data.Sidecars.IBaileysGateway gw) =>
        {
            var acct = await BaileysAccount(db, id);
            if (acct == null) return BaileysNotFound();
            var key = await EnsureBaileysKey(db, acct);
            var st = await gw.LogoutAsync(key);
            await Audit(db, ctx, me, "account.baileys_logout", $"whatsapp:{id}", st.State);
            return Results.Ok(BaileysStatePayload(st));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- template authoring --------------------------------------------
        // Live templates belong to Meta's registry, which this port does not
        // talk to yet (see the GET's note) — so writes always land in the local
        // Setting row and `sandbox` reports whether a live WABA is linked.
        app.MapPost("/api/whatsapp/templates", async (string? account_id, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
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

            if (DemoTemplates.Any(t => t.Name == name))
                return Results.Json(new { detail = $"A template named '{name}' already exists." },
                                    statusCode: 400);
            var existing = CustomTemplates(sc);
            if (existing.Any(t => t.Name == name))
                return Results.Json(new { detail = $"A template named '{name}' already exists." },
                                    statusCode: 400);

            var account = await TemplateAccount(db, account_id ?? AsString(body, "account_id"));
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
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // PATCH: content edits (body/category/buttons) and/or the variable
        // bindings — a bindings-only edit must never touch the template itself,
        // which is what keeps an APPROVED template out of re-review.
        app.MapMethods("/api/whatsapp/templates/{name}", new[] { "PATCH" },
            async (string name, string? account_id, HttpContext ctx, AppDbContext db,
                   ScopingService sc, ClaimsPrincipal me) =>
        {
            var body = await ReadBody(ctx);
            var account = await TemplateAccount(db, account_id ?? AsString(body, "account_id"));
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
                if (idx < 0) return Results.NotFound(new { detail = "Template not found." });
                existing[idx] = existing[idx] with
                {
                    Body = templateBody, Category = category, Buttons = buttons,
                };
                SaveCustomTemplates(sc, existing);
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
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapDelete("/api/whatsapp/templates/{name}", async (string name, string? account_id,
            HttpContext ctx, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            if (DemoTemplates.Any(t => t.Name == name))
                return Results.Json(new { detail = "Built-in demo templates can't be deleted." },
                                    statusCode: 400);
            var existing = CustomTemplates(sc);
            var remaining = existing.Where(t => t.Name != name).ToList();
            if (remaining.Count == existing.Count)
                return Results.NotFound(new { detail = "Template not found." });
            SaveCustomTemplates(sc, remaining);
            SaveBindings(sc, BindingsScope(await TemplateAccount(db, account_id)), name,
                         new List<string>());

            await Audit(db, ctx, me, "template.delete", $"whatsapp:{name}", "local");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- contact → account assignments (write) -------------------------
        // {"contact_id", "channel", "account_id" (null = back to auto), "pinned"}
        app.MapPut("/api/whatsapp/assignments", async (HttpContext ctx, AppDbContext db,
            ClaimsPrincipal me) =>
        {
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
        }).RequireAuthorization(AuthPolicies.AdminPlus);

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
        }).RequireAuthorization(AuthPolicies.SuperAdmin);

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
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    /// <summary>GET/POST /channels/health — views.channels_health().</summary>
    private static async Task<object> ChannelsHealthPayload(AppDbContext db, ScopingService sc,
        int? entityId)
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
            chat_provider_pref = Global(sc, "chat_provider_pref"),
            // quiet hours are not secrets — full values, "HH:MM" server-local time
            quiet_start = Global(sc, "quiet_start"),
            quiet_end = Global(sc, "quiet_end"),
            configured = configured.Any(IsConfigured) || EnvAccountConfigured(),
            ai_available = AiAvailable(sc),
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
            return one != null && IsConfigured(one) && one.WabaId != "" ? one : null;
        }

        var capable = (await db.ChannelAccounts.AsNoTracking()
                .Where(a => a.Channel == Channels.WhatsApp && a.WabaId != "")
                .OrderBy(a => a.Id).ToListAsync())
            .Where(IsConfigured).ToList();
        if (capable.Count == 0) return null;
        return capable.FirstOrDefault(a => AccountRole(a) == "reminder") ?? capable[0];
    }

    /// <summary>
    /// views._bindings_scope — bindings are stored per owning account (template
    /// names are per-WABA); everything without a live WABA shares "sandbox".
    /// </summary>
    private static string BindingsScope(ChannelAccount? account) =>
        account != null && account.WabaId.Length > 0 ? account.Id.ToString() : "sandbox";

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
    };

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
    internal static bool IsConfigured(ChannelAccount a)
    {
        if (a.Channel == Channels.WhatsApp)
            return a.Provider == Providers.Baileys
                   || (a.AccessToken != "" && a.ExternalId != "");
        if (a.Channel == Channels.Email)
            return Cfg(a, "host") != "" && Cfg(a, "username") != "" && Cfg(a, "password") != "";
        return a.AccessToken != "";
    }

    /// <summary>
    /// wa_config.account_role — "reminder" | "conversation". Unset/invalid values
    /// default by provider: cloud → reminder, baileys → conversation.
    /// </summary>
    internal static string AccountRole(ChannelAccount a)
    {
        var role = Cfg(a, "role").Trim().ToLowerInvariant();
        if (role is "reminder" or "conversation") return role;
        return a.Provider == Providers.Baileys ? "conversation" : "reminder";
    }

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
    internal static bool? AiOverride(ChatBranch.Domain.Thread t, int accountId)
    {
        if (!t.AiOverrides.TryGetValue(accountId.ToString(), out var raw) || raw is null) return null;
        if (raw is bool b) return b;
        if (raw is JsonElement el)
            return el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        return bool.TryParse(raw.ToString(), out var parsed) ? parsed : null;
    }

    /// <summary>
    /// store._effective_ai — the Chatbot tick for the thread's CURRENT sending
    /// account. Each account keeps its own per-chat override; with none, the
    /// account's ROLE decides (reminder off unless reminder_ai_default flips it,
    /// conversation on), so switching the sender shows that number's own
    /// setting. Account-less threads (sandbox, env pseudo-account) fall back to
    /// the legacy column.
    /// </summary>
    internal static bool EffectiveAi(ChatBranch.Domain.Thread t, ChannelAccount? account,
        bool reminderDefaultOn)
    {
        if (t.AccountId is not int accountId) return t.AiEnabled;
        if (AiOverride(t, accountId) is bool tick) return tick;
        if (account == null) return t.AiEnabled;
        return AccountRole(account) == "reminder" ? reminderDefaultOn : true;
    }

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
        Global(sc, "openrouter_api_key") != "" || Global(sc, "openai_api_key") != ""
        || Global(sc, "ilmu_api_key") != "" || Global(sc, "google_api_key") != "";

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
        try
        {
            var uids = await db.Profiles.AsNoTracking()
                .Where(p => p.Role == Roles.Admin || p.Role == Roles.SuperAdmin)
                .Select(p => p.UserId).ToListAsync();
            foreach (var uid in uids)
                db.Notifications.Add(new Notification
                {
                    UserId = uid,
                    Kind = "system",
                    Title = Cap(title, 160),
                    Body = Cap(body, 400),
                    Link = "/settings/channels",
                    CreatedAt = DateTime.UtcNow,
                });
            if (uids.Count > 0) await db.SaveChangesAsync();
        }
        catch { }
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}
