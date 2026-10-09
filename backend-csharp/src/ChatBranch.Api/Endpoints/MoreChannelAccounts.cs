using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Api.Workers;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Api.Endpoints;

/// <summary>Where an account field lives on <see cref="ChannelAccount"/>.</summary>
public enum FieldStore { AccessToken, AppSecret, ExternalId, Config }

/// <summary>One credential / setting a channel's account takes (the setup drawer mirrors these keys).</summary>
public sealed record AccountField(string Key, FieldStore Store, string Label, bool Secret = false,
    bool Required = false, string? Pattern = null, string? PatternHint = null, string? Alias = null,
    bool Credential = true);

/// <summary>
/// The account half of LINE, Viber, SMS, WeChat, TikTok, Douyin and Zalo for
/// <c>/api/messaging/accounts</c> (<c>VoiceMessagingEndpoints</c> delegates in one
/// line per verb): the field table per channel, create / patch with masked
/// round-trips (a <c>••</c> value keeps the stored secret), the payload (every
/// secret masked, the webhook path, whether the platform must approve the app),
/// "Test" (the transport's probe, which also moves health) and "Register
/// webhook" (LINE and Viber have an API for it; the others are pasted by hand).
/// </summary>
public static class MoreChannelAccounts
{
    private const string Sid = "^AC[0-9a-fA-F]{32}$";
    private const string Msid = "^MG[0-9a-fA-F]{32}$";
    private const string E164 = @"^\+?[1-9]\d{6,14}$";

    public static readonly IReadOnlyDictionary<string, AccountField[]> Fields = new Dictionary<string, AccountField[]>
    {
        [ChannelIds.Line] = new[]
        {
            new AccountField("access_token", FieldStore.AccessToken, "Channel access token", Secret: true, Required: true),
            new AccountField("channel_secret", FieldStore.AppSecret, "Channel secret", Secret: true, Required: true,
                Alias: "app_secret"),
        },
        [ChannelIds.Viber] = new[]
        {
            new AccountField("access_token", FieldStore.AccessToken, "Auth token", Secret: true, Required: true),
            new AccountField("sender_name", FieldStore.Config, "Sender name", Credential: false,
                Pattern: "^.{1,28}$", PatternHint: "28 characters at most"),
        },
        [ChannelIds.Sms] = new[]
        {
            new AccountField("account_sid", FieldStore.Config, "Account SID", Required: true, Pattern: Sid,
                PatternHint: "AC followed by 32 hex characters"),
            new AccountField("access_token", FieldStore.AccessToken, "Auth token", Secret: true, Required: true),
            new AccountField("from_number", FieldStore.Config, "From number", Pattern: E164,
                PatternHint: "an E.164 number like +60123456789"),
            new AccountField("messaging_service_sid", FieldStore.Config, "Messaging Service SID", Pattern: Msid,
                PatternHint: "MG followed by 32 hex characters"),
        },
        [ChannelIds.WeChat] = new[]
        {
            new AccountField("app_id", FieldStore.Config, "AppID", Required: true, Pattern: "^wx[0-9A-Za-z]{6,32}$",
                PatternHint: "starts with wx"),
            new AccountField("app_secret", FieldStore.AppSecret, "AppSecret", Secret: true, Required: true),
            new AccountField("token", FieldStore.Config, "Server token", Secret: true, Required: true,
                Pattern: "^[A-Za-z0-9]{3,32}$", PatternHint: "3–32 letters and digits"),
            new AccountField("encoding_aes_key", FieldStore.Config, "EncodingAESKey", Secret: true,
                Pattern: "^[A-Za-z0-9]{43}$", PatternHint: "exactly 43 letters and digits"),
            new AccountField("external_id", FieldStore.ExternalId, "Original ID", Credential: false),
        },
        [ChannelIds.TikTok] = new[]
        {
            new AccountField("external_id", FieldStore.ExternalId, "Business ID", Required: true),
            new AccountField("access_token", FieldStore.AccessToken, "Business access token", Secret: true, Required: true),
            new AccountField("app_id", FieldStore.Config, "App ID", Credential: false),
            new AccountField("app_secret", FieldStore.AppSecret, "App secret", Secret: true, Required: true),
        },
        [ChannelIds.Douyin] = new[]
        {
            new AccountField("client_key", FieldStore.Config, "Client key", Required: true),
            new AccountField("client_secret", FieldStore.AppSecret, "Client secret", Secret: true, Required: true,
                Alias: "app_secret"),
            new AccountField("external_id", FieldStore.ExternalId, "Enterprise account open_id", Required: true),
            new AccountField("access_token", FieldStore.AccessToken, "Access token", Secret: true, Required: true),
            new AccountField("refresh_token", FieldStore.Config, "Refresh token", Secret: true),
        },
        [ChannelIds.Zalo] = new[]
        {
            new AccountField("app_id", FieldStore.Config, "App ID", Required: true, Pattern: "^[0-9]{5,}$",
                PatternHint: "digits only"),
            new AccountField("oa_secret_key", FieldStore.Config, "OA secret key", Secret: true, Required: true),
            new AccountField("access_token", FieldStore.AccessToken, "OA access token", Secret: true, Required: true),
            new AccountField("refresh_token", FieldStore.Config, "Refresh token", Secret: true),
            new AccountField("app_secret", FieldStore.AppSecret, "App secret key", Secret: true),
            new AccountField("external_id", FieldStore.ExternalId, "OA ID", Credential: false),
        },
    };

    public static bool Handles(string? channel) => MoreChannels.Handles(channel);

    public static bool CanSetWebhook(string channel) => channel is ChannelIds.Line or ChannelIds.Viber;

    private static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        return secret.Length >= 8 ? $"••••{secret[^4..]}" : "••••";
    }

    private static bool Untouched(string? value) => value != null && value.Contains("••");

    private static string? Str(Dictionary<string, JsonElement>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.Null => "",
            _ => null,
        };
    }

    private static string? Read(Dictionary<string, JsonElement>? body, AccountField f) =>
        Str(body, f.Key) ?? (f.Alias != null ? Str(body, f.Alias) : null);

    private static string Get(ChannelAccount a, AccountField f) => f.Store switch
    {
        FieldStore.AccessToken => a.AccessToken ?? "",
        FieldStore.AppSecret => a.AppSecret ?? "",
        FieldStore.ExternalId => a.ExternalId ?? "",
        _ => MoreChannels.Cfg(a, f.Key),
    };

    private static void Set(ChannelAccount a, AccountField f, string value)
    {
        switch (f.Store)
        {
            case FieldStore.AccessToken: a.AccessToken = value; break;
            case FieldStore.AppSecret: a.AppSecret = value; break;
            case FieldStore.ExternalId: a.ExternalId = value.Length > 200 ? value[..200] : value; break;
            default:
                var cfg = new Dictionary<string, object?>(a.Config ?? new());
                if (value.Length == 0) cfg.Remove(f.Key); else cfg[f.Key] = value;
                a.Config = cfg;
                break;
        }
    }

    private static string? Check(AccountField f, string value) =>
        value.Length > 0 && f.Pattern != null && !Regex.IsMatch(value, f.Pattern)
            ? $"{f.Label} doesn't look right — {f.PatternHint ?? "check the value"}."
            : null;

    /// <summary>Channel-wide rules on top of the per-field ones.</summary>
    private static string? Rules(ChannelAccount a)
    {
        if (a.Channel == ChannelIds.Sms && MoreChannels.Cfg(a, "from_number").Length == 0
            && MoreChannels.Cfg(a, "messaging_service_sid").Length == 0)
            return "An SMS inbox needs a From number or a Messaging Service SID.";
        return null;
    }

    /// <summary>The ExternalId SMS routes by: the from number (E.164) or the messaging service.</summary>
    private static void Normalize(ChannelAccount a)
    {
        if (a.Channel != ChannelIds.Sms) return;
        var from = MoreChannels.Cfg(a, "from_number");
        if (from.Length > 0)
        {
            var e164 = TwilioSmsTransport.E164(from);
            var cfg = new Dictionary<string, object?>(a.Config ?? new()) { ["from_number"] = e164 };
            a.Config = cfg;
            a.ExternalId = e164;
        }
        else a.ExternalId = MoreChannels.Cfg(a, "messaging_service_sid");
    }

    /// <summary>POST: a new account from the body (400 detail on a missing / malformed field).</summary>
    public static (ChannelAccount? Account, string? Error) Build(string channel, Dictionary<string, JsonElement>? body,
        string owner)
    {
        var account = new ChannelAccount
        {
            Channel = channel,
            Owner = owner,
            CreatedAt = DateTime.UtcNow,
            Config = new Dictionary<string, object?>(),
        };
        var name = (Str(body, "name") ?? "").Trim();
        account.Name = name.Length == 0 ? MoreChannels.Label(channel) : name.Length > 120 ? name[..120] : name;
        var missing = new List<string>();
        foreach (var f in Fields[channel])
        {
            var value = (Read(body, f) ?? "").Trim();
            if (value.Length == 0)
            {
                if (f.Required) missing.Add(f.Key);
                continue;
            }
            if (Check(f, value) is { } bad) return (null, bad);
            Set(account, f, value);
        }
        if (missing.Count > 0)
            return (null, $"{string.Join(", ", missing)} required for a {MoreChannels.Label(channel)} account.");
        if (Rules(account) is { } rule) return (null, rule);
        Normalize(account);
        return (account, null);
    }

    /// <summary>
    /// PATCH: apply the fields the body carries (a masked secret = keep the stored
    /// one; a required field cannot be blanked). Returns the changed keys and
    /// whether a CREDENTIAL changed (→ the reconnect reset + re-probe).
    /// </summary>
    public static (List<string> Touched, bool CredentialsChanged, string? Error) ApplyPatch(ChannelAccount a,
        Dictionary<string, JsonElement>? body)
    {
        var touched = new List<string>();
        var creds = false;
        var name = (Str(body, "name") ?? "").Trim();
        if (name.Length > 0 && name != a.Name)
        {
            a.Name = name.Length > 120 ? name[..120] : name;
            touched.Add("name");
        }
        foreach (var f in Fields[a.Channel])
        {
            var raw = Read(body, f);
            if (raw == null) continue;
            var value = raw.Trim();
            if (f.Secret && (value.Length == 0 || Untouched(value))) continue; // blank / masked = keep
            if (value.Length == 0 && f.Required) return (touched, creds, $"{f.Label} cannot be empty.");
            if (Check(f, value) is { } bad) return (touched, creds, bad);
            if (value == Get(a, f)) continue;
            Set(a, f, value);
            touched.Add(f.Key);
            if (f.Credential) creds = true;
        }
        if (Rules(a) is { } rule) return (touched, creds, rule);
        Normalize(a);
        return (touched, creds, null);
    }

    /// <summary>The account payload's channel part — every secret masked, never raw.</summary>
    public static void AddPayload(Dictionary<string, object?> outv, ChannelAccount a)
    {
        var cfg = new Dictionary<string, object?>();
        foreach (var f in Fields[a.Channel])
        {
            var v = Get(a, f);
            switch (f.Store)
            {
                case FieldStore.Config when f.Secret:
                    cfg[$"{f.Key}_masked"] = Mask(v);
                    break;
                case FieldStore.Config:
                    cfg[f.Key] = v;
                    break;
                case FieldStore.AppSecret:
                    outv["app_secret_masked"] = Mask(v);
                    if (f.Key != "app_secret") outv[$"{f.Key}_masked"] = Mask(v);
                    break;
                case FieldStore.AccessToken:
                    outv["access_token_masked"] = Mask(v);
                    break;
            }
        }
        outv["config"] = cfg;
        outv["configured"] = MoreChannels.Configured(a);
        outv["webhook_path"] = MoreChannels.WebhookPath(a);
        outv["requires_approval"] = MoreChannels.RequiresApproval(a.Channel);
        outv["window_hours"] = ChannelWindow.Rule(a.Channel)?.Hours;
        outv["can_set_webhook"] = CanSetWebhook(a.Channel);
    }

    /// <summary>
    /// POST …/test: the transport's probe. Healthy / degraded answer 200 (a
    /// degraded detail says what to fix); a refused credential 502. The verdict
    /// moves health like a prober run, and a learned id (LINE bot, Viber bot,
    /// Zalo OA) fills an empty external id.
    /// </summary>
    public static async Task<IResult> TestAsync(ChannelAccount account, HttpContext ctx)
    {
        var sp = ctx.RequestServices;
        var db = sp.GetRequiredService<AppDbContext>();
        var label = MoreChannels.Label(account.Channel);
        if (!MoreChannels.Configured(account))
            return Results.Json(new { detail = $"This {label} account is not fully configured yet." }, statusCode: 400);
        var transport = sp.GetRequiredService<ChannelTransports>().For(account.Channel);
        if (transport == null)
            return Results.Json(new { detail = $"No {label} transport is registered." }, statusCode: 501);
        var probe = await transport.ProbeAsync(account, ctx.RequestAborted);
        var v = probe.Verdict;
        if (v.Status == Health.Healthy) HealthRecorder.Ok(db, account, "account test passed");
        else if (v.Status != null) AccountHealth.SetStatus(db, account, v.Status, v.Detail, actor: "user");
        if (account.ExternalId.Length == 0 && probe.LearnedId.Length > 0
            && account.Channel is ChannelIds.Line or ChannelIds.Viber or ChannelIds.Zalo)
            account.ExternalId = probe.LearnedId;
        await db.SaveChangesAsync();
        if (v.Status is Health.Healthy or Health.Degraded)
            return Results.Ok(new
            {
                ok = true,
                warning = v.Status == Health.Degraded,
                name = probe.Name,
                id = account.ExternalId,
                detail = v.Status == Health.Healthy ? $"{v.Detail}." : $"Warning: {v.Detail}.",
                health_status = account.HealthStatus,
            });
        return Results.Json(new
        {
            detail = $"Connection failed: {v.Detail}",
            health_status = account.HealthStatus,
        }, statusCode: 502);
    }

    /// <summary>
    /// POST …/set-webhook for LINE (<c>PUT /v2/bot/channel/webhook/endpoint</c>)
    /// and Viber (<c>set_webhook</c> — Viber calls the URL straight away with a
    /// signed "webhook" event, which the endpoint answers). Same public-base
    /// resolution as Telegram; https only.
    /// </summary>
    public static async Task<IResult> SetWebhookAsync(ChannelAccount account, Dictionary<string, JsonElement>? body,
        HttpContext ctx)
    {
        var sp = ctx.RequestServices;
        var db = sp.GetRequiredService<AppDbContext>();
        var label = MoreChannels.Label(account.Channel);
        if (!MoreChannels.Configured(account))
            return Results.Json(new { detail = $"This {label} account is not fully configured yet." }, statusCode: 400);
        var baseUrl = MessagingAccountTransport.PublicBaseUrl(Str(body, "base_url"),
            sp.GetRequiredService<ScopingService>(), ctx);
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new
            {
                detail = $"base_url must be a public https:// origin ({label} only calls https). Pass base_url, " +
                         "or set PUBLIC_BASE_URL / the public_base_url setting.",
            }, statusCode: 400);
        var url = baseUrl + MoreChannels.WebhookPath(account);
        var transports = sp.GetRequiredService<ChannelTransports>();
        var result = account.Channel == ChannelIds.Line
            ? await transports.Get<LineTransport>()!.SetWebhookAsync(account, url, ctx.RequestAborted)
            : await transports.Get<ViberTransport>()!.SetWebhookAsync(account, url, ctx.RequestAborted);
        if (!result.Ok)
            return Results.Json(new { detail = $"Registering the webhook failed: {result.Detail}" }, statusCode: 502);
        AuditTrail.Write(db, "account.set_webhook", $"{account.Channel}:{account.Name}", "webhook registered");
        _ = sp.GetRequiredService<HealthRecheck>().Schedule(account.Id);
        return Results.Ok(new { ok = true, url });
    }
}
