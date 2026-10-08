using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Integrations surface (/api/integrations/*). Sandbox is MANDATORY: with zero
/// credentials, list still answers, connect returns an operator-facing reason
/// instead of opening a socket, and actions return {ok:false, skipped:true} so a
/// flow step logs a clean skip and the run continues. Webhooks verify HMAC and
/// return 503 when no secret is configured — never "accept unsigned".
/// </summary>
public static class IntegrationsEndpoints
{
    public static readonly string[] Providers =
        { "google_calendar", "google_drive", "gmail", "google_sheets", "shopify", "stripe" };

    // Contract shared with frontend lib/flow/nodeConfig.ts INTEGRATION_ACTIONS.
    public static readonly string[] Actions =
    {
        "Create calendar event", "Find free slot", "Upload file to Drive",
        "Append row to Sheet", "Send via Gmail", "Update order status",
        "Create Stripe payment link",
    };

    public record ConnectRequest(string? Entity, Dictionary<string, object?>? Credentials);
    public record ActionRequest(string Provider, string Action, Dictionary<string, object?>? Args);

    // ---- provider catalog (integrations/catalog.py PROVIDERS) ----------------
    // Reproduced verbatim so the Settings → Integrations grid renders the same
    // cards, fields and blurbs the Django backend serves. Every Google product
    // asks for ITS OWN scope only — connecting Drive never grants Gmail.

    /// <summary>One operator-visible input on a provider card (config or secret).</summary>
    public record CatalogField(string Key, string Label, string Placeholder = "");

    public record CatalogProvider(
        string Id, string Label, string Kind, string Family, string Blurb,
        string[] Scopes, CatalogField[] ConfigFields, CatalogField[] SecretFields,
        string[] ProviderActions, string WebhookPath = "");

    private static readonly CatalogProvider[] Catalog =
    {
        new("google_calendar", "Google Calendar", "oauth", "google",
            "Book appointments and find free slots on a shared calendar.",
            new[] { "https://www.googleapis.com/auth/calendar.events",
                    "https://www.googleapis.com/auth/calendar.readonly" },
            new[] { new CatalogField("calendar_id", "Calendar id", "primary") },
            Array.Empty<CatalogField>(),
            new[] { "Create calendar event", "Find free slot" }),

        new("google_drive", "Google Drive", "oauth", "google",
            "Store documents a conversation produced (quotes, photos, receipts).",
            new[] { "https://www.googleapis.com/auth/drive.file" },
            new[] { new CatalogField("folder_id", "Target folder id", "(My Drive root)") },
            Array.Empty<CatalogField>(),
            new[] { "Upload file to Drive" }),

        new("gmail", "Gmail", "oauth", "google",
            "Send from a real Gmail mailbox instead of SMTP.",
            new[] { "https://www.googleapis.com/auth/gmail.send" },
            new[] { new CatalogField("from_email", "From address", "ops@example.com") },
            Array.Empty<CatalogField>(),
            new[] { "Send via Gmail" }),

        new("google_sheets", "Google Sheets", "oauth", "google",
            "Append a row per conversation outcome to an ops spreadsheet.",
            new[] { "https://www.googleapis.com/auth/spreadsheets" },
            new[] { new CatalogField("spreadsheet_id", "Spreadsheet id", "1AbC…"),
                    new CatalogField("sheet_range", "Range", "Sheet1!A:Z") },
            Array.Empty<CatalogField>(),
            new[] { "Append row to Sheet" }),

        new("shopify", "Shopify", "apikey", "shopify",
            "Orders flow in by webhook and become Records; steps can tag or note an order back.",
            Array.Empty<string>(),
            new[] { new CatalogField("shop_domain", "Shop domain", "my-store.myshopify.com"),
                    new CatalogField("api_version", "Admin API version", "2024-10") },
            new[] { new CatalogField("api_key", "Admin API access token"),
                    new CatalogField("webhook_secret", "Webhook signing secret") },
            new[] { "Update order status" },
            ShopifyWebhookPath),

        new("stripe", "Stripe", "apikey", "stripe",
            "Payments confirm themselves — payment_intent.succeeded books a RecordEvent " +
            "against the matching Record.",
            Array.Empty<string>(),
            Array.Empty<CatalogField>(),
            new[] { new CatalogField("api_key", "Secret key (sk_…)"),
                    new CatalogField("webhook_secret", "Webhook signing secret (whsec_…)") },
            new[] { "Create Stripe payment link" },
            StripeWebhookPath),
    };

    // The C# port serves the webhooks under /webhooks/<p> (plural) — the paths
    // handed to the operator must be the ones this backend actually listens on.
    private const string ShopifyWebhookPath = "/api/integrations/webhooks/shopify";
    private const string StripeWebhookPath = "/api/integrations/webhooks/stripe";

    /// <summary>Where the Google OAuth callback lands (catalog.redirect_uri).</summary>
    private const string GoogleCallbackPath = "/api/integrations/oauth/google/callback";

    // ---- global integration settings (catalog.GLOBAL_KEYS) -------------------
    // ONE Setting row holding JSON, with a per-field .env fallback — the same
    // precedence as voice_twilio_settings. Secrets round-trip masked and a value
    // still carrying "••" on the way back in is treated as untouched.

    private const string SettingsKey = "integrations_settings";

    private static readonly (string Key, string Env)[] GlobalKeys =
    {
        ("google_client_id", "GOOGLE_CLIENT_ID"),
        ("google_client_secret", "GOOGLE_CLIENT_SECRET"),
        ("google_redirect_uri", "GOOGLE_REDIRECT_URI"),
        ("shopify_webhook_secret", "SHOPIFY_WEBHOOK_SECRET"),
        ("stripe_webhook_secret", "STRIPE_WEBHOOK_SECRET"),
    };

    private static readonly string[] SecretKeys =
        { "google_client_secret", "shopify_webhook_secret", "stripe_webhook_secret" };

    /// <summary>
    /// Config keys this port uses to carry columns the C# <see cref="Integration"/>
    /// entity does not have (name, external_id, created_by, last_ok_at). They are
    /// stripped from the `config` bag the API returns, so the operator only ever
    /// sees the provider fields they filled in.
    /// </summary>
    private static readonly string[] InternalConfigKeys =
        { "name", "external_id", "created_by", "last_ok_at" };

    private const string SettingsLink = "/settings/integrations";

    public static void MapIntegrationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/integrations", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var connected = sc.ScopeInherited(db.Integrations.AsNoTracking(), entityId).ToList();
            var cards = Providers.Select(p =>
            {
                var row = connected.FirstOrDefault(i => i.Provider == p);
                return new
                {
                    provider = p,
                    status = row?.Status ?? "unknown",
                    statusDetail = row?.StatusDetail ?? "",
                    // credentials are NEVER returned unmasked
                    configured = row != null && row.Credentials.Count > 0,
                };
            });
            return Results.Ok(new { integrations = cards, actions = Actions });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Connect: superadmin. Sandbox → 400 with a reason, no socket opened.
        app.MapPost("/api/integrations/{provider}/connect",
            async (string provider, ConnectRequest body, AppDbContext db) =>
        {
            if (!Providers.Contains(provider))
                return Results.Json(new { detail = "unknown provider" }, statusCode: 400);
            var creds = body.Credentials ?? new();
            if (creds.Count == 0)
                return Results.Json(new
                {
                    ok = false,
                    detail = $"No credentials supplied for {provider}. Add them in Settings → " +
                             "Integrations before connecting (sandbox mode).",
                }, statusCode: 400);

            var entityId = await InboxEndpoints.EntityId(db, body.Entity);
            var row = await db.Integrations.FirstOrDefaultAsync(i => i.Provider == provider && i.EntityId == entityId)
                      ?? new Integration { Provider = provider, EntityId = entityId, CreatedAt = DateTime.UtcNow };
            if (row.Id == 0) db.Integrations.Add(row);
            row.Credentials = creds;
            row.Status = "connected";
            row.StatusDetail = "";
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { provider, status = row.Status });
        }).RequireAuthorization(AuthPolicies.SuperAdmin);

        // ---- provider catalog (views.providers) ----------------------------
        // Works with an empty database and zero credentials — that is the
        // template's shipping state, so it must never 500.
        app.MapGet("/api/integrations/providers", async (string? entity,
            AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = await sc.ScopeInherited(db.Integrations.AsNoTracking().Include(i => i.Owner), entityId).ToListAsync();
            var googleReady = GoogleOauthConfigured(sc);

            var providers = Catalog.Select(meta =>
            {
                var mine = rows.Where(r => r.Provider == meta.Id).ToList();
                return new
                {
                    id = meta.Id,
                    label = meta.Label,
                    kind = meta.Kind,
                    family = meta.Family,
                    blurb = meta.Blurb,
                    scopes = meta.Scopes,
                    config_fields = meta.ConfigFields,
                    secret_fields = meta.SecretFields,
                    actions = meta.ProviderActions,
                    webhook_path = meta.WebhookPath,
                    accounts = mine.Count,
                    connected = mine.Count(Connected),
                    oauth_configured = meta.Kind == "oauth" ? googleReady : true,
                };
            }).ToList();

            return Results.Ok(new
            {
                providers,
                google_oauth_configured = googleReady,
                sandbox = !rows.Any(Connected),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- integration rows (views.accounts) ------------------------------
        app.MapGet("/api/integrations/accounts", async (string? provider, string? entity,
            AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var qs = sc.ScopeInherited(db.Integrations.AsNoTracking().Include(i => i.Owner), entityId);
            var wanted = (provider ?? "").Trim();
            if (wanted.Length > 0) qs = qs.Where(i => i.Provider == wanted);
            var rows = await qs.OrderBy(i => i.Provider).ThenBy(i => i.Id).ToListAsync();
            return Results.Ok(new { accounts = rows.Select(Payload).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Create/connect an API-key provider (the add-account modal). admin+.
        app.MapPost("/api/integrations/accounts", async (HttpContext ctx, AppDbContext db,
            ClaimsPrincipal me) =>
        {
            var body = await ReadJsonBody(ctx);
            var provider = (AsText(body, "provider") ?? "").Trim();
            var meta = Meta(provider);
            if (meta == null)
                return Results.Json(new
                {
                    detail = $"provider must be one of {string.Join(", ", Catalog.Select(c => c.Id))}.",
                }, statusCode: 400);

            var entityId = await InboxEndpoints.EntityId(db, AsText(body, "entity"));
            var config = ObjectToDict(body, "config");
            var name = Cap((AsText(body, "name") ?? "").Trim(), 120);
            config["name"] = name.Length > 0 ? name : meta.Label;
            config["external_id"] = Cap((AsText(body, "external_id") ?? "").Trim(), 190);
            config["created_by"] = me.Identity?.Name ?? "";

            var creds = new Dictionary<string, object?>();
            foreach (var key in new[] { "api_key", "webhook_secret" })
            {
                var value = (AsText(body, key) ?? "").Trim();
                // a masked placeholder round-tripped from a GET is NOT a new secret
                if (value.Length > 0 && !value.Contains("••")) creds[key] = value;
            }

            var row = new Integration
            {
                Provider = provider,
                EntityId = entityId,
                Config = config,
                Credentials = creds,
                Status = Health.Unknown,
                StatusDetail = "",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Integrations.Add(row);
            await db.SaveChangesAsync();

            await Audit(db, ctx, me, "integration.create", $"{provider}:{Name(row)}");
            await NotifyAdmins(db, $"Integration added: {Name(row)}",
                               $"{meta.Label} — by {me.Identity?.Name ?? "system"}");
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // Delete a row entirely (views.account_detail DELETE). admin+.
        app.MapDelete("/api/integrations/accounts/{id:int}", async (int id, HttpContext ctx,
            AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.Integrations.FirstOrDefaultAsync(i => i.Id == id);
            if (row == null)
                return Results.Json(new { detail = "Integration not found." }, statusCode: 404);
            var label = $"{row.Provider}:{Name(row)}";
            db.Integrations.Remove(row);
            await db.SaveChangesAsync();
            await Audit(db, ctx, me, "integration.delete", label);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // One probe (views.account_test). Any authenticated role, like Django.
        // NOTE: the live provider probes are not ported yet — a stored-credential
        // row reports a clean SKIP rather than pretending it reached Google.
        app.MapPost("/api/integrations/accounts/{id:int}/test", async (int id, AppDbContext db) =>
        {
            var row = await db.Integrations.FirstOrDefaultAsync(i => i.Id == id);
            if (row == null)
                return Results.Json(new { detail = "Integration not found." }, statusCode: 404);

            var meta = Meta(row.Provider);
            var detail = !Connected(row)
                ? $"{meta?.Label ?? row.Provider} has no credentials yet."
                : $"Credentials are stored for {meta?.Label ?? row.Provider}, but the live " +
                  "connection probe is not available in this backend yet.";
            // skipped ⇒ 400 (views.account_test: ok ? 200 : skipped ? 400 : 502)
            return Results.Json(new
            {
                ok = false,
                skipped = true,
                detail,
                data = new { },
                account = Payload(row),
            }, statusCode: 400);
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Forget the grant, keep the row (views.account_disconnect). admin+.
        app.MapPost("/api/integrations/accounts/{id:int}/disconnect", async (int id,
            HttpContext ctx, AppDbContext db, ClaimsPrincipal me) =>
        {
            var row = await db.Integrations.FirstOrDefaultAsync(i => i.Id == id);
            if (row == null)
                return Results.Json(new { detail = "Integration not found." }, statusCode: 404);
            // oauth.revoke_local: drop the stored credentials only — name/config
            // survive so reconnecting is one click. Google's own revoke endpoint
            // is deliberately NOT called (a shared client holds other grants).
            row.Credentials = new Dictionary<string, object?>();
            row.Status = "revoked";
            row.StatusDetail = "disconnected by an operator";
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await Audit(db, ctx, me, "integration.disconnect", $"{row.Provider}:{Name(row)}");
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- global settings (views.settings_view) ---------------------------
        // GET = any authenticated role, secrets masked. PUT = SUPER ADMIN, the
        // same rule as every other organization-wide credential.
        app.MapGet("/api/integrations/settings", (HttpContext ctx, ScopingService sc) =>
            Results.Ok(SettingsPayload(ctx, sc)))
            .RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/integrations/settings", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var body = await ReadJsonBody(ctx);
            var stored = StoredGlobals(sc);
            var touched = new List<string>();
            foreach (var (key, _) in GlobalKeys)
            {
                if (!TryText(body, key, out var raw)) continue;
                if (raw.Contains("••")) continue;   // masked round-trip: untouched
                stored[key] = raw.Trim();           // "" clears back to the .env fallback
                touched.Add(key);
            }
            if (touched.Count == 0)
                return Results.Json(new { detail = "Nothing to update." }, statusCode: 400);

            sc.SetSetting(SettingsKey, JsonSerializer.Serialize(stored));
            await Audit(db, ctx, me, "settings.update", "integrations",
                        $"keys: {string.Join(", ", touched.OrderBy(k => k))}");
            return Results.Ok(SettingsPayload(ctx, sc));
        }).RequireAuthorization(AuthPolicies.SuperAdmin);

        // ---- OAuth start (views.oauth_start) ---------------------------------
        // The dashboard's Connect button calls this before navigating. The PKCE
        // round-trip itself is not ported yet, so this answers with the honest
        // "needs one-time setup" shape (HTTP 200, {configured:false, reason})
        // that the card already renders — never a 404 and never a socket opened.
        // ---- OAuth: start ------------------------------------------------
        // Returns a ready Google consent URL for THIS caller's own connection
        // (PKCE + signed state). Each user gets their own row, so two people in
        // one entity connect two different calendars instead of overwriting one.
        app.MapGet("/api/integrations/oauth/{provider}/start", async (string provider, string? entity,
            HttpContext ctx, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            IntegrationResolver resolver, GoogleOAuth oauth) =>
        {
            var meta = Meta(provider);
            if (meta == null)
                return Results.Json(new { detail = "Unknown provider." }, statusCode: 404);
            if (meta.Kind != "oauth")
                return Results.Json(new
                {
                    configured = false,
                    reason = $"{meta.Label} uses an API key, not OAuth — add an account instead.",
                    detail = $"{meta.Label} does not use OAuth.",
                }, statusCode: 400);
            if (!GoogleOauthConfigured(sc))
                return Results.Ok(new
                {
                    configured = false,
                    reason = "Google OAuth is not configured yet. A super admin registers the "
                             + "Google client once (Settings → App & Provider Keys); after that "
                             + "anyone connects their own account in one click.",
                });

            var entityId = await InboxEndpoints.EntityId(db, entity);
            var userId = await UserIdAsync(db, me);
            var row = await resolver.OwnRowAsync(provider, entityId, userId);
            try
            {
                var url = oauth.AuthorizeUrl(row, meta.Scopes, RedirectUri(ctx, sc));
                return Results.Ok(new { configured = true, authorize_url = url });
            }
            catch (GoogleOAuth.OAuthException e)
            {
                return Results.Ok(new { configured = false, reason = e.Message });
            }
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- OAuth: callback ---------------------------------------------
        // OPEN by design: the browser arrives from accounts.google.com with no
        // Authorization header, so the signed state IS the CSRF control. Always
        // bounces back to the dashboard with ?connected= or ?error=.
        app.MapGet(GoogleCallbackPath, async (string? code, string? state, string? error,
            HttpContext ctx, AppDbContext db, ScopingService sc, GoogleOAuth oauth) =>
        {
            var dashboard = DashboardUrl();
            if (!string.IsNullOrEmpty(error))
                return Results.Redirect($"{dashboard}?error={Uri.EscapeDataString(error!)}");
            try
            {
                var data = oauth.VerifyState(state);
                var row = await db.Integrations.FirstOrDefaultAsync(
                    i => i.Id == data.IntegrationId && i.Provider == data.Provider);
                if (row == null)
                    return Results.Redirect($"{dashboard}?error={Uri.EscapeDataString("That connection no longer exists.")}");
                if (string.IsNullOrEmpty(code))
                    return Results.Redirect($"{dashboard}?error={Uri.EscapeDataString("Google returned no authorization code.")}");
                await oauth.ExchangeCodeAsync(row, code!, RedirectUri(ctx, sc));
                var label = Meta(row.Provider)?.Label ?? row.Provider;
                // The browser carries no token here: attribute the connect to the row's owner.
                var owner = row.OwnerUserId is int ou
                    ? await db.Users.Where(u => u.Id == ou).Select(u => u.Username).FirstOrDefaultAsync() : null;
                ChatBranch.Data.Audit.AuditTrail.Write(db, "integration.connect", $"{row.Provider}", label,
                    actor: owner ?? "oauth", source: AuditSources.User, entityId: row.EntityId,
                    userId: row.OwnerUserId);
                return Results.Redirect($"{dashboard}?connected={Uri.EscapeDataString(label)}");
            }
            catch (GoogleOAuth.OAuthException e)
            {
                return Results.Redirect($"{dashboard}?error={Uri.EscapeDataString(e.Message)}");
            }
            catch
            {
                return Results.Redirect($"{dashboard}?error={Uri.EscapeDataString("The connection could not be completed.")}");
            }
        }).AllowAnonymous();

        // ---- flow-builder action dispatch --------------------------------
        // Runs the action for real against the resolved connection (this user's
        // own → the entity's → the organization's). A missing connection SKIPS
        // so the flow run continues; a provider error is reported honestly.
        app.MapPost("/api/integrations/action", async (ActionRequest body, AppDbContext db,
            IntegrationActions actions, ClaimsPrincipal me, string? entity) =>
        {
            var args = new Dictionary<string, string>();
            foreach (var (k, v) in body.Args ?? new()) args[k] = v?.ToString() ?? "";
            var contactId = int.TryParse(args.GetValueOrDefault("contact_id"), out var cid) ? cid : (int?)null;
            var contact = contactId is int id
                ? await db.Contacts.FirstOrDefaultAsync(c => c.Id == id)
                : null;
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var userId = await UserIdAsync(db, me);
            var result = await actions.RunAsync(body.Action, contact, args,
                args.GetValueOrDefault("text") ?? "", entityId, userId);
            return Results.Ok(new
            {
                ok = result.Ok,
                skipped = result.Skipped,
                detail = result.Detail,
                data = result.Data,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- webhooks: verify HMAC; no secret ⇒ 503 (never accept unsigned) ----
        app.MapPost("/api/integrations/webhooks/shopify",
            async (HttpContext ctx, AppDbContext db, ScopingService sc) =>
        {
            var secret = GlobalSettingValue(sc, "shopify_webhook_secret");
            if (string.IsNullOrEmpty(secret))
                return Results.Json(new { detail = "webhook secret not configured" }, statusCode: 503);
            var raw = await ReadBody(ctx);
            var hmac = ctx.Request.Headers["X-Shopify-Hmac-Sha256"].ToString();
            if (!WebhookVerify.Shopify(secret, raw, hmac))
                return Results.Json(new { detail = "invalid signature" }, statusCode: 401);
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        app.MapPost("/api/integrations/webhooks/stripe",
            async (HttpContext ctx, AppDbContext db, ScopingService sc) =>
        {
            var secret = GlobalSettingValue(sc, "stripe_webhook_secret");
            if (string.IsNullOrEmpty(secret))
                return Results.Json(new { detail = "webhook secret not configured" }, statusCode: 503);
            var raw = await ReadBody(ctx);
            var sig = ctx.Request.Headers["Stripe-Signature"].ToString();
            if (!WebhookVerify.Stripe(secret, raw, sig))
                return Results.Json(new { detail = "invalid signature" }, statusCode: 401);
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();
    }

    private static async Task<byte[]> ReadBody(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    // ======================= serialization ===================================

    private static CatalogProvider? Meta(string? provider) =>
        Catalog.FirstOrDefault(c => c.Id == provider);

    /// <summary>
    /// Integration.connected — has USABLE credentials (an OAuth access token or
    /// an API key). Anything else is a sandbox row.
    /// </summary>
    private static bool Connected(Integration row) =>
        Cred(row, "access_token") != "" || Cred(row, "api_key") != "";

    private static string Name(Integration row)
    {
        var stored = Conf(row, "name");
        return stored != "" ? stored : Meta(row.Provider)?.Label ?? row.Provider;
    }

    /// <summary>
    /// views._payload — the row as the dashboard sees it. SECRETS NEVER LEAVE
    /// THE SERVER: api_key / webhook_secret round-trip masked and the OAuth
    /// tokens surface only as a boolean.
    /// </summary>
    private static object Payload(Integration row)
    {
        var meta = Meta(row.Provider);
        var connected = Connected(row);
        return new
        {
            id = row.Id,
            provider = row.Provider,
            provider_label = meta?.Label ?? row.Provider,
            kind = meta?.Kind ?? "oauth",
            name = Name(row),
            external_id = Conf(row, "external_id"),
            config = PublicConfig(row),
            // Scopes actually granted (what came back on the token); before a
            // grant exists there is nothing to report.
            scopes = row.Scopes.Count > 0
                ? row.Scopes.ToArray()
                : connected ? meta?.Scopes ?? Array.Empty<string>() : Array.Empty<string>(),
            // Whose connection this is. null = shared by the whole entity.
            owner_user_id = row.OwnerUserId,
            owner = row.Owner?.Username,
            connected,
            api_key_masked = MaskSecret(Cred(row, "api_key")),
            webhook_secret_masked = MaskSecret(Cred(row, "webhook_secret")),
            has_refresh_token = Cred(row, "refresh_token") != "",
            status = row.Status,
            status_detail = row.StatusDetail,
            last_ok_at = Conf(row, "last_ok_at") is { Length: > 0 } ok ? ok : null,
            status_changed_at = Iso(row.UpdatedAt),
            created_by = Conf(row, "created_by"),
            created_at = Iso(row.CreatedAt),
        };
    }

    /// <summary>The config bag minus the keys this port uses for missing columns.</summary>
    private static Dictionary<string, object?> PublicConfig(Integration row)
    {
        var outv = new Dictionary<string, object?>();
        foreach (var (key, value) in row.Config)
            if (!InternalConfigKeys.Contains(key)) outv[key] = value;
        return outv;
    }

    private static string Cred(Integration row, string key) => BagString(row.Credentials, key);
    private static string Conf(Integration row, string key) => BagString(row.Config, key);

    /// <summary>A JSON bag value as text — the values arrive as JsonElement.</summary>
    private static string BagString(Dictionary<string, object?>? bag, string key)
    {
        if (bag == null || !bag.TryGetValue(key, out var raw) || raw is null) return "";
        if (raw is string s) return s.Trim();
        if (raw is JsonElement el)
            return el.ValueKind switch
            {
                JsonValueKind.String => (el.GetString() ?? "").Trim(),
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => el.ToString().Trim(),
            };
        return (raw.ToString() ?? "").Trim();
    }

    // ======================= global settings =================================

    /// <summary>
    /// The dashboard-saved row WITHOUT the env merge — the save path must write
    /// only what the operator typed, never bake .env into the DB
    /// (catalog._stored).
    /// </summary>
    private static Dictionary<string, string> StoredGlobals(ScopingService sc)
    {
        var outv = new Dictionary<string, string>();
        var raw = sc.SettingValue(SettingsKey);
        if (string.IsNullOrWhiteSpace(raw)) return outv;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return outv;
            foreach (var p in doc.RootElement.EnumerateObject())
                outv[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? "" : p.Value.ToString();
        }
        catch { /* malformed row → empty, never a 500 */ }
        return outv;
    }

    /// <summary>
    /// The effective value of one global key: the Setting JSON wins, then a
    /// plain Setting row of the same name (what the webhook handlers read before
    /// this surface existed), then .env — "" means "not configured anywhere".
    /// </summary>
    // One source for the org-wide keys — the OAuth service reads the same
    // helper, so "is Google configured?" can never disagree between them.
    private static string GlobalSettingValue(ScopingService sc, string key) =>
        IntegrationSettings.Value(sc, key);

    private static bool GoogleOauthConfigured(ScopingService sc) =>
        IntegrationSettings.GoogleConfigured(sc);

    /// <summary>
    /// Where Google sends the browser back: the configured value wins, otherwise
    /// it is derived from the request so a fresh clone works on localhost
    /// (catalog.redirect_uri).
    /// </summary>
    private static string RedirectUri(HttpContext ctx, ScopingService sc)
    {
        var configured = GlobalSettingValue(sc, "google_redirect_uri");
        if (configured != "") return configured;
        return $"{Origin(ctx)}{GoogleCallbackPath}";
    }

    /// <summary>Where the OAuth callback bounces the operator back to.</summary>
    private static string DashboardUrl()
    {
        var basis = (Environment.GetEnvironmentVariable("FRONTEND_URL") ?? "http://localhost:3000")
            .TrimEnd('/');
        return $"{basis}/integrations";
    }

    /// <summary>The signed-in user's pk — the owner of any grant they create.</summary>
    private static async Task<int?> UserIdAsync(AppDbContext db, ClaimsPrincipal me)
    {
        var username = me.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username);
        return user?.Id;
    }

    private static string Origin(HttpContext ctx)
    {
        var req = ctx.Request;
        var host = req.Host.HasValue ? req.Host.Value : "localhost:8000";
        var scheme = string.IsNullOrEmpty(req.Scheme) ? "http" : req.Scheme;
        return $"{scheme}://{host}";
    }

    /// <summary>views._settings_payload — secrets masked, provenance per key.</summary>
    private static Dictionary<string, object?> SettingsPayload(HttpContext ctx, ScopingService sc)
    {
        var stored = StoredGlobals(sc);
        var outv = new Dictionary<string, object?>();
        var source = new Dictionary<string, string>();
        foreach (var (key, _) in GlobalKeys)
        {
            var effective = GlobalSettingValue(sc, key);
            if (SecretKeys.Contains(key)) outv[$"{key}_masked"] = MaskSecret(effective);
            else outv[key] = effective;
            source[key] = (stored.GetValueOrDefault(key) ?? "").Trim() != "" ? "settings"
                        : effective != "" ? "env" : "";
        }
        outv["source"] = source;
        outv["google_oauth_configured"] = GoogleOauthConfigured(sc);
        outv["redirect_uri"] = RedirectUri(ctx, sc);
        // Absolute URLs: these are pasted into the Shopify/Stripe consoles.
        outv["webhook_paths"] = Catalog.Where(c => c.WebhookPath != "")
            .ToDictionary(c => c.Id, c => $"{Origin(ctx)}{c.WebhookPath}");
        return outv;
    }

    // ======================= primitives ======================================

    /// <summary>_mask: secrets round-trip as "••••1234", never in full.</summary>
    private static string MaskSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        return secret.Length >= 8 ? $"••••{secret[^4..]}" : "••••";
    }

    private static string Iso(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("o");

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];

    // ---- request body (partial patch: an absent key means "untouched") -------

    private static async Task<JsonElement> ReadJsonBody(HttpContext ctx)
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

    private static bool TryText(JsonElement body, string key, out string value)
    {
        value = "";
        if (body.ValueKind != JsonValueKind.Object) return false;
        if (!body.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return true;
    }

    private static string? AsText(JsonElement body, string key) =>
        TryText(body, key, out var value) ? value : null;

    private static Dictionary<string, object?> ObjectToDict(JsonElement body, string key)
    {
        var outv = new Dictionary<string, object?>();
        if (body.ValueKind != JsonValueKind.Object) return outv;
        if (!body.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object) return outv;
        foreach (var p in el.EnumerateObject())
            outv[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => p.Value.ToString(),
            };
        return outv;
    }

    // ---- audit + notification fan-out ---------------------------------------

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
    private static async Task NotifyAdmins(AppDbContext db, string title, string body)
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
                    Link = SettingsLink,
                    CreatedAt = DateTime.UtcNow,
                });
            if (uids.Count > 0) await db.SaveChangesAsync();
        }
        catch { }
    }
}
