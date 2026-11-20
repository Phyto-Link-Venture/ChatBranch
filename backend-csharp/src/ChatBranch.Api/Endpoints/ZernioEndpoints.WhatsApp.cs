using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Zernio;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Self-serve WhatsApp onboarding through the managed connection (Zernio), so a client connects
/// their OWN number — or buys one — from the dashboard instead of someone pasting Meta System
/// User credentials.
///
/// <list type="bullet">
///   <item><c>POST /api/social/connect {platform:"whatsapp"}</c> → Meta's Embedded Signup, started
///         through Zernio. The MODE is NEVER implicit: <c>onboarding</c> must be
///         <see cref="WaCloudOnly"/> (Cloud API only — the number leaves the WhatsApp Business app)
///         or <see cref="WaCoexistence"/> (it keeps working there, a QR is scanned, and the API side
///         is capped). Zernio's own default — <c>onboarding</c> omitted — IS coexistence, which is
///         why a missing value is a 400 here rather than a silent choice.
///         <c>signup=hosted</c> (the default, Cloud-API-only mode) makes <c>authUrl</c> a page on
///         zernio.com that opens Meta's popup itself and connects exactly the number the user
///         picked — no Facebook SDK, no domain to register, and no second number picker for a
///         login that manages several WABAs.</item>
///   <item><c>GET /api/social/connect/whatsapp/callback</c> — where the browser lands. Anonymous by
///         design (it arrives from zernio.com / facebook.com with no Authorization header): the
///         single-use <c>cb</c> nonce minted at start IS the CSRF control, it is bound to the entity
///         and profile the operator chose, and it accepts ONE account id. It mirrors the connected
///         number into a <c>ChannelAccount</c> (channel <c>whatsapp</c>, provider <c>zernio</c>) at
///         once — before <c>account.connected</c> arrives — so an inbound message has an inbox to
///         land in, then bounces to the dashboard. The mirror is keyed on the Zernio account id, so
///         a reloaded callback can never make a second inbox.</item>
///   <item><c>/api/social/phone-numbers</c> — the numbers an outlet's profile owns, each with its
///         <c>monthly_cents</c>, plus the live per-country price so the cost is on screen BEFORE a
///         purchase. Purchase and release both demand an explicit confirmation (a purchase starts a
///         recurring charge; a release returns the number to the carrier pool and cannot be undone)
///         and both are audited. Neither is an MCP tool — see <c>McpExclusions</c>.</item>
/// </list>
/// The "bring my own Meta credentials" form stays as the advanced fallback
/// (<c>POST /api/social/connect/whatsapp</c>), offered as <c>alternatives</c> here.
/// </summary>
public static partial class ZernioEndpoints
{
    /// <summary>Embedded Signup on the Cloud API only: the number leaves the WhatsApp Business app.</summary>
    public const string WaCloudOnly = "api";

    /// <summary>Embedded Signup with coexistence: the number keeps working in the WhatsApp Business app.</summary>
    public const string WaCoexistence = "business_app";

    /// <summary>Zernio hosts the signup page and connects exactly the number the user picked.</summary>
    public const string WaHostedPicker = "hosted";

    /// <summary>Meta's own dialog, with Zernio's selection step after it.</summary>
    public const string WaMetaPicker = "meta";

    public const string WhatsAppCallbackPath = "/api/social/connect/whatsapp/callback";

    /// <summary>One pending signup round-trip, keyed by the nonce in the callback URL.</summary>
    private const string WaPendingPrefix = "zernio_wa_signup:";

    /// <summary>How long a started signup may take to come back.</summary>
    private static readonly TimeSpan WaPendingTtl = TimeSpan.FromHours(2);

    // =========================================================================
    // Start — POST /api/social/connect {platform: "whatsapp"} lands here
    // =========================================================================

    /// <summary>
    /// The WhatsApp branch of <c>POST /api/social/connect</c>: an Embedded Signup URL for the
    /// client's browser, with the mode chosen explicitly and a single-use state carried across the
    /// round trip.
    /// </summary>
    private static async Task<IResult> StartWhatsAppSignupAsync(Dictionary<string, JsonElement>? body,
        AppDbContext db, ScopingService sc, IZernioClient zernio, HttpContext ctx, int? entityId, string profileId,
        Dictionary<string, object?> common)
    {
        // The mode is a decision, not a default: omitting it at Zernio means COEXISTENCE.
        var onboarding = (Str(body, "onboarding") ?? "").Trim().ToLowerInvariant();
        if (onboarding.Length == 0)
            return Results.Json(new
            {
                code = "onboarding_required",
                detail = "Choose how the number is connected before starting: \"api\" (WhatsApp Business Platform " +
                         "only — it stops working in the WhatsApp Business app) or \"business_app\" (coexistence — " +
                         "it keeps working there, someone scans a QR code, and the API side is limited).",
                options = new object[]
                {
                    new { value = WaCloudOnly, label = "Cloud API only", warnings = WaWarnings(WaCloudOnly) },
                    new { value = WaCoexistence, label = "Keep the WhatsApp Business app (coexistence)", warnings = WaWarnings(WaCoexistence) },
                },
            }, statusCode: 400);
        if (onboarding is not (WaCloudOnly or WaCoexistence))
            return Results.Json(new
            {
                detail = $"onboarding must be \"{WaCloudOnly}\" (Cloud API only) or \"{WaCoexistence}\" (keep the WhatsApp Business app).",
            }, statusCode: 400);

        var signup = (Str(body, "signup") ?? "").Trim().ToLowerInvariant();
        if (signup.Length == 0) signup = WaHostedPicker;
        if (signup is not (WaHostedPicker or WaMetaPicker))
            return Results.Json(new
            {
                detail = $"signup must be \"{WaHostedPicker}\" (Zernio's page connects the number the user picked) " +
                         $"or \"{WaMetaPicker}\" (Meta's own dialog).",
            }, statusCode: 400);
        // Zernio documents the hosted page with onboarding=api only; coexistence needs Meta's own
        // window for its QR step, so the hosted picker is not forced on it.
        var hostedRefused = signup == WaHostedPicker && onboarding == WaCoexistence;
        if (hostedRefused) signup = WaMetaPicker;

        var apiBase = MessagingAccountTransport.PublicBaseUrl(null, sc, ctx);
        if (!apiBase.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new
            {
                detail = "No public URL for the connect callback: set public_base_url (or PUBLIC_BASE_URL) first.",
            }, statusCode: 400);

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var callback = $"{apiBase}{WhatsAppCallbackPath}?cb={nonce}";
        var hosted = await zernio.ConnectUrlAsync("whatsapp", profileId, callback, onboarding,
            signup == WaHostedPicker ? WaHostedPicker : null);
        if (!hosted.Ok)
            return Results.Json(new { detail = hosted.Error, code = "zernio_error" }, statusCode: 502);
        var url = hosted.Str("authUrl");
        if (url.Length == 0)
            return Results.Json(new { detail = "No connect URL came back." }, statusCode: 502);

        WaPrune(db);
        // No pending row = no callback could ever be trusted: fail before the person signs in.
        if (!WaSavePending(sc, nonce, new Dictionary<string, object?>
            {
                ["entity_id"] = entityId,
                ["profile_id"] = profileId,
                ["onboarding"] = onboarding,
                ["signup"] = signup,
                // Zernio's own state: compared when the callback carries one (it documents
                // connected / profileId / accountId / username / request_id / stage, not state)
                ["state"] = hosted.Str("state"),
                ["issued_at"] = DateTime.UtcNow.ToString("o"),
                ["account_id"] = "",
            }))
            return Results.Json(new
            {
                detail = "The connection could not be started here (its one-time state could not be stored).",
            }, statusCode: 500);

        AuditTrail.Write(db, "zernio.connect", "whatsapp",
            $"managed connection started (embedded signup, {(onboarding == WaCoexistence ? "coexistence" : "cloud API only")}, " +
            $"{(signup == WaHostedPicker ? "hosted" : "Meta")} number picker)", entityId: entityId);

        return Results.Ok(new Dictionary<string, object?>(common)
        {
            ["flow"] = "hosted",
            ["url"] = url,
            ["onboarding"] = onboarding,
            ["signup"] = signup,
            ["coexistence"] = onboarding == WaCoexistence,
            ["hosted_picker_unavailable"] = hostedRefused
                ? "Coexistence runs in Meta's own window (it shows the QR code there), so the hosted picker is not used."
                : null,
            ["state"] = nonce,
            ["callback_url"] = callback,
            ["warnings"] = WaWarnings(onboarding),
            ["alternatives"] = new object[] { WaCredentialsAlternative },
        });
    }

    /// <summary>
    /// What a person MUST be told before they run the signup, per mode. The dashboard shows its own
    /// translated copy; these texts serve an API caller.
    /// </summary>
    private static object[] WaWarnings(string onboarding)
    {
        var oneProvider = new
        {
            code = "one_provider",
            text = "A number pairs with ONE provider at a time. Disconnect it from its previous provider first " +
                   "(WhatsApp Business app → Settings → Account → Business Platform → Disconnect).",
        };
        return onboarding == WaCoexistence
            ? new object[]
            {
                oneProvider,
                new
                {
                    code = "coexistence_scan",
                    text = "Someone has to be at the phone: Meta shows a QR code that is scanned with the WhatsApp " +
                           "Business app. A number that is only on the consumer WhatsApp app does not qualify.",
                },
                new
                {
                    code = "coexistence_limits",
                    text = "The number keeps working in the WhatsApp Business app and messages are mirrored both ways " +
                           "(up to 6 months of history syncs), but the API side is fixed at 20 messages per second, " +
                           "groups made in the app are not visible, and calls, disappearing and view-once messages " +
                           "are off for that number.",
                },
            }
            : new object[]
            {
                oneProvider,
                new
                {
                    code = "leaves_business_app",
                    text = "The number moves to the WhatsApp Business Platform: it STOPS working in the WhatsApp " +
                           "Business app, and the chat history on the phone does not come across. A number in use " +
                           "with the consumer WhatsApp app cannot be connected at all.",
                },
                new
                {
                    code = "hard_to_reverse",
                    text = "Going back means deregistering the number from the Cloud API, and the history does not " +
                           "return. Choose coexistence instead if the business wants to keep using the app.",
                },
            };
    }

    /// <summary>The advanced fallback: the business's own Meta System User credentials.</summary>
    private static readonly object WaCredentialsAlternative = new
    {
        flow = "fields",
        label = "Use my own WhatsApp Business credentials",
        submit_url = "/api/social/connect/whatsapp",
        fields = new object[]
        {
            new { key = "access_token", label = "Permanent access token (System User)", type = "password", secret = true, required = true, help = "Meta Business Suite → System users", placeholder = "EAAB…" },
            new { key = "waba_id", label = "WhatsApp Business Account ID", type = "text", secret = false, required = true, help = "", placeholder = "" },
            new { key = "phone_number_id", label = "Phone number ID", type = "text", secret = false, required = true, help = "", placeholder = "" },
            new { key = "pin", label = "Two-step verification PIN (6 digits)", type = "text", secret = true, required = false, help = "Only when two-step verification is on for the number", placeholder = "123456" },
        },
    };

    // =========================================================================
    // The callback + the phone numbers
    // =========================================================================

    private static void MapWhatsAppOnboarding(IEndpointRouteBuilder app)
    {
        // Where Embedded Signup sends the browser back. OPEN by design: it arrives from zernio.com
        // (or facebook.com) with no Authorization header, so the single-use `cb` nonce is the CSRF
        // control. It always bounces to the dashboard, never renders anything itself.
        app.MapGet(WhatsAppCallbackPath, async (string? cb, string? state, string? accountId, string? error,
            string? error_message, string? step, HttpContext ctx, AppDbContext db, ScopingService sc,
            IZernioClient zernio, ZernioAccounts accounts) =>
        {
            var landing = Dashboard(ctx, sc) + "/channels";
            // platform=whatsapp on both outcomes: the dashboard's banner is this flow's, not
            // every managed connect's (the generic hosted flow lands here without it)
            IResult Bad(string detail) =>
                Results.Redirect($"{landing}?zernio=error&platform=whatsapp&detail={Uri.EscapeDataString(detail)}");

            var pending = WaPending(sc, cb);
            if (pending == null)
                return Bad("This connect link has expired or was already used — start the connection again.");
            // Zernio's documented callback params do not include `state`; when one IS sent it must
            // be the state this round trip was started with.
            var expected = WaStr(pending, "state");
            var got = (state ?? "").Trim();
            if (got.Length > 0 && expected.Length > 0 && !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(got)))
                return Bad("The connection could not be verified (state mismatch) — start it again.");

            if (!string.IsNullOrWhiteSpace(error))
                // the pending link stays usable, so the person can simply try again
                return Bad(string.IsNullOrWhiteSpace(error_message) ? error!.Trim() : error_message!.Trim());
            // we never ask for headless mode, so a selection step means the flow did not finish
            if ((step ?? "").Trim() == "select_phone_number")
                return Bad("WhatsApp stopped to ask which number to use. Start again and pick the number in the " +
                           "connect window.");

            var zid = (accountId ?? "").Trim();
            var already = WaStr(pending, "account_id");
            if (zid.Length == 0) zid = already;
            if (zid.Length == 0)
                return Bad("WhatsApp came back without an account id — the number was not connected.");
            // this round trip connects ONE number: a second, different id is not ours
            if (already.Length > 0 && zid != already)
                return Bad("This connect link already finished for another number — start a new connection.");

            var entityId = WaInt(pending, "entity_id");
            var profileId = WaStr(pending, "profile_id");
            // Mirror only an account Zernio really lists in THIS profile: an anonymous route never
            // creates an inbox from an id handed to it.
            JsonElement acct = default;
            if (zernio.IsConfigured)
            {
                var list = await zernio.ListAccountsAsync(profileId.Length > 0 ? profileId : null, ctx.RequestAborted);
                if (list.Ok)
                {
                    var hit = ZernioJson.Arr(list.Json, "accounts").FirstOrDefault(a =>
                        (ZernioJson.Str(a, "_id") == zid || ZernioJson.Str(a, "accountId") == zid)
                        && ZernioJson.Str(a, "platform").ToLowerInvariant() == "whatsapp"
                        && (profileId.Length == 0 || ZernioJson.Id(ZernioJson.At(a, "profileId")) == profileId));
                    if (hit.ValueKind == JsonValueKind.Object) acct = hit;
                }
            }
            ChannelAccount? row = null;
            if (acct.ValueKind == JsonValueKind.Object)
                try
                {
                    // keyed on the Zernio account id: a reloaded callback refreshes the SAME inbox
                    row = await MirrorConnectedAsync(db, accounts, entityId, acct, "whatsapp",
                        profileId.Length > 0 ? profileId : null);
                }
                catch (Exception)
                {
                    row = null;
                }
            if (already.Length == 0)
            {
                pending["account_id"] = zid;
                WaSavePending(sc, cb!, pending);
                AuditTrail.Write(db, "zernio.connect", "whatsapp",
                    "managed connection completed (embedded signup, " +
                    $"{(WaStr(pending, "onboarding") == WaCoexistence ? "coexistence" : "cloud API only")}; account {zid}" +
                    (row == null ? "; inbox pending" : "") + ")",
                    actor: "zernio", entityId: entityId);
            }
            // row == null: the account.connected webhook (or the next reconcile) makes the inbox
            var q = $"?zernio=connected&purpose=inbox&platform=whatsapp&account={Uri.EscapeDataString(zid)}";
            if (row == null) q += "&pending=1";
            return Results.Redirect(landing + q);
        }).AllowAnonymous();

        // The numbers THIS outlet's profile owns (monthly_cents each) + the live per-country price,
        // so the cost is on screen before anyone buys. No profile yet = no numbers: a bare list of
        // the whole Zernio team would show other companies' numbers.
        app.MapGet("/api/social/phone-numbers", async (string? entity, string? country, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var (entityId, err) = await ActScope(db, sc, me, entity);
            if (err != null) return err;
            var (profileId, _) = await accounts.EnsureProfileAsync(entityId, create: false);
            var numbers = new List<WaNumber>();
            if (profileId.Length > 0)
            {
                var r = await zernio.ListPhoneNumbersAsync(profileId);
                if (!r.Ok) return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
                numbers = ZernioJson.Arr(r.Json, "numbers").Select(WaNumberRow).ToList();
            }
            // the catalogue: what a NEW number costs per month, per country — the quote before a purchase
            var offer = await zernio.PhoneNumberCountriesAsync();
            var countries = (offer.Ok ? ZernioJson.Arr(offer.Json, "countries") : Enumerable.Empty<JsonElement>())
                .Select(c => new
                {
                    code = ZernioJson.Str(c, "code"),
                    monthly_cents = ZernioJson.Int(c, "monthlyCents"),
                    currency = "USD",
                    whatsapp = ZernioJson.At(c, "whatsappAvailable").ValueKind != JsonValueKind.False,
                    sms = ZernioJson.At(c, "smsAvailable").ValueKind != JsonValueKind.False,
                    calls = ZernioJson.At(c, "callsAvailable").ValueKind != JsonValueKind.False,
                    needs_kyc = ZernioJson.Bool(c, "needsKyc"),
                    in_stock = ZernioJson.At(c, "inStock").ValueKind != JsonValueKind.False,
                })
                .Where(c => c.code.Length > 0
                            && (string.IsNullOrWhiteSpace(country)
                                || string.Equals(c.code, country!.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return Results.Ok(new
            {
                numbers,
                countries,
                countries_detail = offer.Ok ? null : offer.Error,
                currency = "USD",
                entity_id = entityId,
                profile_id = profileId.Length > 0 ? profileId : null,
                notes = new[]
                {
                    "A number bought here is pre-verified for WhatsApp and carries calls and SMS on the same number.",
                    "The monthly price is charged in full when the number activates, and again on the 1st of each month.",
                    "Releasing a number stops future months, returns it to the carrier pool and cannot be undone.",
                },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // Buy a number for an outlet. SPENDS MONEY, every month: confirm must be true (the price is
        // answered when it is not), purchase_intent_id makes a retry safe, and the buy is audited.
        app.MapPost("/api/social/phone-numbers/purchase", async (Dictionary<string, JsonElement>? body,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var country = (Str(body, "country") ?? "").Trim().ToUpperInvariant();
            if (country.Length != 2 || !country.All(char.IsAsciiLetterUpper))
                return Results.Json(new { detail = "country is required: a two-letter country code (US, MY…)." },
                    statusCode: 400);
            var (entityId, err) = await ActScope(db, sc, me, Str(body, "entity"));
            if (err != null) return err;
            var wantsSms = BoolOf(body, "wants_sms", false);
            var connectWhatsapp = BoolOf(body, "connect_whatsapp", true);
            // the role's channel policy: the number becomes a managed WhatsApp (and maybe SMS) inbox
            if (connectWhatsapp && ShellScope.ConnectError(db, me, "whatsapp", Providers.Zernio) is { } waNotAllowed)
                return waNotAllowed;
            if (wantsSms && ShellScope.ConnectError(db, me, "sms", Providers.Zernio) is { } smsNotAllowed)
                return smsNotAllowed;
            var areaCode = (Str(body, "area_code") ?? "").Trim();
            var exact = (Str(body, "phone_number") ?? "").Trim();

            // what it costs, before anything is bought
            var offer = await zernio.PhoneNumberCountriesAsync();
            var quote = (offer.Ok ? ZernioJson.Arr(offer.Json, "countries") : Enumerable.Empty<JsonElement>())
                .FirstOrDefault(c => string.Equals(ZernioJson.Str(c, "code"), country, StringComparison.OrdinalIgnoreCase));
            var monthlyCents = quote.ValueKind == JsonValueKind.Object ? ZernioJson.Int(quote, "monthlyCents") : 0;
            if (!BoolOf(body, "confirm", false))
                return Results.Json(new
                {
                    code = "confirmation_required",
                    detail = monthlyCents > 0
                        ? $"Buying a {country} number starts a recurring charge of US${Money(monthlyCents)} per month. " +
                          "Ask the person, then send confirm: true."
                        : $"Buying a {country} number starts a recurring monthly charge. Ask the person, then send confirm: true.",
                    country,
                    monthly_cents = monthlyCents,
                    currency = "USD",
                    needs_kyc = quote.ValueKind == JsonValueKind.Object && ZernioJson.Bool(quote, "needsKyc"),
                }, statusCode: 400);
            // one key per decision, minted by whoever asked: a retry must not buy a second number
            var intent = (Str(body, "purchase_intent_id") ?? "").Trim();
            if (intent.Length == 0)
                return Results.Json(new
                {
                    code = "purchase_intent_required",
                    detail = "purchase_intent_id is required: one id for this purchase, resent unchanged on a retry, " +
                             "so a timeout cannot buy a second number.",
                }, statusCode: 400);
            if (intent.Length > 120)
                return Results.Json(new { detail = "purchase_intent_id is too long (120 characters)." }, statusCode: 400);

            var (profileId, perr) = await accounts.EnsureProfileAsync(entityId);
            if (profileId.Length == 0)
                return Results.Json(new { detail = perr, code = "zernio_error" }, statusCode: 502);
            var r = await zernio.PurchasePhoneNumberAsync(profileId, country,
                areaCode.Length > 0 ? areaCode : null, exact.Length > 0 ? exact : null, wantsSms, connectWhatsapp,
                intent);
            if (!r.Ok)
                return Results.Json(new
                {
                    detail = r.Error, code = "zernio_error", purchase_intent_id = intent,
                }, statusCode: r.Status is 400 or 402 or 409 or 422 ? r.Status : 502);
            var number = WaNumberRow(WaPhoneBody(r));
            var cents = number.monthly_cents > 0 ? number.monthly_cents : monthlyCents;
            AuditTrail.Write(db, "zernio.phone_number.purchase",
                number.phone_number.Length > 0 ? number.phone_number : country,
                $"bought a {country} number (US${Money(cents)}/month, WhatsApp {(connectWhatsapp ? "on" : "off")}, " +
                $"SMS {(wantsSms ? "on" : "off")}; intent {intent})", entityId: entityId);
            return Results.Ok(new
            {
                ok = true,
                // 202 = a regulated country: the number arrives after Zernio's KYC review
                pending_kyc = r.Status == 202 || ZernioJson.Str(r.Json, "code") == "kyc_required",
                number,
                purchase_intent_id = intent,
                message = r.Str("message"),
                connect_whatsapp = connectWhatsapp,
                // Zernio starts the WhatsApp flow for a bought number itself, and the number is
                // pre-verified — but a person still runs Embedded Signup and picks it (it shows as
                // "Verified" in Meta's picker), which is where the WABA is chosen
                next = connectWhatsapp
                    ? "The number is ready for WhatsApp. Finish the connection with POST /api/social/connect " +
                      "{platform:\"whatsapp\", onboarding:\"api\"} and pick it in Meta's picker (it shows as Verified)."
                    : null,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // Release a number. IRREVERSIBLE: confirm=true AND the number typed out are both required.
        app.MapDelete("/api/social/phone-numbers/{id}", async (string id, string? confirm, string? phone_number,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, IZernioClient zernio, ZernioAccounts accounts) =>
        {
            if (!zernio.IsConfigured) return NotConfigured(zernio);
            var one = await zernio.GetPhoneNumberAsync(id);
            if (!one.Ok)
                return Results.Json(new { detail = one.Error, code = "zernio_error" },
                    statusCode: one.Status == 404 ? 404 : 502);
            var raw = WaPhoneBody(one);
            var number = WaNumberRow(raw);
            // the number's OWN profile decides whose it is — never the entity the caller named
            var numberProfile = ZernioJson.Id(ZernioJson.At(raw, "profileId"));
            int? level = null;
            // no profile on the row (Zernio does not document one on this read): treat the number
            // as the organization's, so a company-bound admin cannot release what is not theirs
            if (numberProfile.Length == 0 && !ShellScope.MayActAt(db, sc, me, null))
                return Results.Json(new
                {
                    detail = "This number is not bound to your outlet — only someone who manages the whole " +
                             "organization can release it.",
                }, statusCode: 403);
            if (numberProfile.Length > 0)
            {
                var owner = await db.SocialProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ProfileId == numberProfile);
                if (owner == null)
                    return Results.Json(new { detail = "That number is not one of this installation's." }, statusCode: 404);
                if (ShellScope.ObjectScopeError(db, sc, me, owner.EntityId) is { } outside) return outside;
                level = owner.EntityId;
            }
            // is it an inbox here? (its digits are a managed inbox's address)
            var digits = new string(number.phone_number.Where(char.IsAsciiDigit).ToArray());
            var inUse = digits.Length > 0
                ? (await db.ChannelAccounts.AsNoTracking().Where(a => a.Provider == Providers.Zernio).ToListAsync())
                    .Where(a => new string(ZernioKeys.Cfg(a, ZernioNative.AddressKey).Where(char.IsAsciiDigit).ToArray()) == digits)
                    .Select(a => a.Name).ToList()
                : new List<string>();
            var yes = (confirm ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes";
            var typed = new string((phone_number ?? "").Where(char.IsAsciiDigit).ToArray());
            if (!yes || typed.Length == 0)
                return Results.Json(new
                {
                    code = "confirmation_required",
                    detail = $"Releasing {(number.phone_number.Length > 0 ? number.phone_number : id)} returns it to the " +
                             "carrier pool and cannot be undone. Ask the person, then call again with confirm=true and " +
                             "phone_number set to the number itself.",
                    number,
                    in_use_by = inUse,
                }, statusCode: 400);
            if (digits.Length > 0 && typed != digits)
                return Results.Json(new { detail = "That is not this number — check it and type it again.", number },
                    statusCode: 400);
            var r = await zernio.ReleasePhoneNumberAsync(id);
            if (!r.Ok && r.Status != 404)
                return Results.Json(new { detail = r.Error, code = "zernio_error" }, statusCode: 502);
            AuditTrail.Write(db, "zernio.phone_number.release",
                number.phone_number.Length > 0 ? number.phone_number : id,
                "released to the carrier pool (irreversible)"
                + (inUse.Count > 0 ? $"; was in use by {string.Join(", ", inUse)}" : ""),
                entityId: level);
            return Results.Ok(new { ok = true, released = true, number, in_use_by = inUse });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");
    }

    // ---- readers ---------------------------------------------------------------

    /// <summary>Cents → "3.00", never the host's decimal comma (these strings reach an audit row).</summary>
    private static string Money(int cents) =>
        (cents / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A purchase / get answers either the number itself or <c>{phoneNumber:{…}}</c>.</summary>
    private static JsonElement WaPhoneBody(ZernioReply r) =>
        r.At("phoneNumber").ValueKind == JsonValueKind.Object ? r.At("phoneNumber")
        : r.At("number").ValueKind == JsonValueKind.Object ? r.At("number")
        : r.Json;

    /// <summary>One owned number as the dashboard reads it — <c>monthly_cents</c> always present.</summary>
    private static WaNumber WaNumberRow(JsonElement n) => new(
        ZernioJson.Str(n, "_id") is { Length: > 0 } i ? i : ZernioJson.Str(n, "id"),
        ZernioJson.Str(n, "phoneNumber"),
        ZernioJson.Str(n, "country"),
        ZernioJson.Str(n, "status"),
        ZernioJson.Int(n, "monthlyCents"),
        "USD",
        ZernioJson.At(n, "hostedByZernio").ValueKind != JsonValueKind.False,
        ZernioJson.At(n, "smsCapable").ValueKind != JsonValueKind.False,
        ZernioJson.Bool(n, "whatsappConnected") || ZernioJson.Str(n, "whatsappStatus") is "connected" or "active");

    private record WaNumber(string id, string phone_number, string country, string status, int monthly_cents,
        string currency, bool hosted_by_zernio, bool sms_capable, bool whatsapp_connected);

    // ---- the pending signup (one Setting row per round trip) ----------------------

    private static bool WaSavePending(ScopingService sc, string nonce, Dictionary<string, object?> data)
    {
        try
        {
            sc.SetSetting(WaPendingPrefix + nonce, JsonSerializer.Serialize(data));
            return true;
        }
        catch { return false; }
    }

    private static Dictionary<string, object?>? WaPending(ScopingService sc, string? nonce)
    {
        var key = (nonce ?? "").Trim();
        if (key.Length is 0 or > 64 || !key.All(char.IsAsciiHexDigit)) return null;
        string raw;
        try { raw = sc.SettingValue(WaPendingPrefix + key); } catch { return null; }
        if (raw.Length == 0) return null;
        Dictionary<string, object?>? data;
        try { data = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw); }
        catch { return null; }
        if (data == null) return null;
        if (DateTime.TryParse(WaStr(data, "issued_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var at) && DateTime.UtcNow - at > WaPendingTtl)
            return null;
        return data;
    }

    /// <summary>Expired round trips are dropped when a new one starts (they are Setting rows).</summary>
    private static void WaPrune(AppDbContext db)
    {
        try
        {
            var cutoff = DateTime.UtcNow - WaPendingTtl;
            var stale = db.Settings.Where(s => s.EntityId == null && s.Key.StartsWith(WaPendingPrefix)
                                               && s.UpdatedAt < cutoff).ToList();
            if (stale.Count == 0) return;
            db.Settings.RemoveRange(stale);
            db.SaveChanges();
        }
        catch { /* housekeeping never fails a connect */ }
    }

    private static string WaStr(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v != null
            ? v is JsonElement e ? ZernioJson.Str(e) : v.ToString() ?? ""
            : "";

    private static int? WaInt(Dictionary<string, object?> d, string key) =>
        int.TryParse(WaStr(d, key), out var n) ? n : null;
}
