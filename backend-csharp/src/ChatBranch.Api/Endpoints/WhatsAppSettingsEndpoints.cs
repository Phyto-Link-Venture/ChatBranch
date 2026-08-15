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
    private const string EnvAccountId = "env-default";

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
            // Bindings are stored per owning account (template names are per-WABA);
            // everything without a live WABA shares the "sandbox" scope.
            var scope = account != null && account.WabaId.Length > 0
                ? account.Id.ToString() : "sandbox";
            var bindings = TemplateBindings(sc, scope);

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
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);

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
            return Results.Ok(new
            {
                accounts,
                email = EmailHealth(sc),
                voice = VoiceHealth(sc),
                checked_at = checkedAt != "" ? checkedAt : Iso(DateTime.UtcNow),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

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
    private static object VoiceHealth(ScopingService sc)
    {
        if (!SipTrunkConfigured(sc))
            return new { configured = false, health = Health.Unknown, detail = "", live_check = false };
        var v = VoiceEffective(sc);
        if (v["sip_username"] == "" || v["sip_password"] == "")
            return new
            {
                configured = true,
                health = Health.Unknown,
                detail = "SIP trunk set — add the SIP username + password (Settings → Channels → " +
                         "Voice, or TWILIO_SIP_USERNAME / TWILIO_SIP_PASSWORD in .env) to dial",
                live_check = false,
            };
        return new { configured = true, health = Health.Healthy, detail = "", live_check = false };
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
    private static Dictionary<string, List<string>> TemplateBindings(ScopingService sc, string scope)
    {
        var outv = new Dictionary<string, List<string>>();
        var raw = sc.SettingValue(TemplateBindingsKey);
        if (raw == "") return outv;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return outv;
            if (!doc.RootElement.TryGetProperty(scope, out var forScope)
                || forScope.ValueKind != JsonValueKind.Object) return outv;
            foreach (var prop in forScope.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                outv[prop.Name] = prop.Value.EnumerateArray()
                    .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "")
                    .ToList();
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
    /// Channel-appropriate "usable" test (routing._configured): WhatsApp cloud
    /// needs token + phone_number_id, baileys is gateway-enforced, email needs
    /// SMTP host+login in config, other channels need an access_token.
    /// </summary>
    private static bool IsConfigured(ChannelAccount a)
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
    private static string AccountRole(ChannelAccount a)
    {
        var role = Cfg(a, "role").Trim().ToLowerInvariant();
        if (role is "reminder" or "conversation") return role;
        return a.Provider == Providers.Baileys ? "conversation" : "reminder";
    }

    private static string Cfg(ChannelAccount a, string key) =>
        a.Config.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static bool EnvAccountConfigured() =>
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

    private static string Env(string name) => (Environment.GetEnvironmentVariable(name) ?? "").Trim();

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

    private static bool AiAvailable(ScopingService sc) =>
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
    private static string Mask(string secret)
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

    private static string? Iso(DateTime? dt) =>
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

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}
