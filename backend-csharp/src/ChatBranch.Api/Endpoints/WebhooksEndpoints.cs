using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Integrations;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Live inbound webhooks (open routes — the signature IS the auth). Meta Cloud
/// (GET verify + signed POST), Telegram, and the email IMAP sync nudge. Each
/// routes inbound through <see cref="InboundProcessor"/> (store + dedup + flow
/// resume + coverage-gated AI reply). Signature is REQUIRED once an account
/// carries an app_secret; sandbox (no configured account) accepts unsigned.
/// </summary>
public static class WebhooksEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Meta Cloud: GET verification -------------------------------------
        app.MapGet("/api/whatsapp/webhook", (HttpContext ctx, ScopingService sc) =>
        {
            var mode = ctx.Request.Query["hub.mode"].ToString();
            var token = ctx.Request.Query["hub.verify_token"].ToString();
            var challenge = ctx.Request.Query["hub.challenge"].ToString();
            var expected = sc.SettingValue("whatsapp_verify_token");
            if (string.IsNullOrEmpty(expected))
                expected = Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN") ?? "";
            if (mode == "subscribe" && expected != "" && token == expected)
                return Results.Text(challenge);
            return Results.StatusCode(403);
        }).AllowAnonymous();

        // ---- Meta Cloud: POST inbound -----------------------------------------
        app.MapPost("/api/whatsapp/webhook", async (HttpContext ctx, AppDbContext db,
            InboundProcessor processor) =>
        {
            var raw = await ReadBody(ctx);
            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { return Results.Ok(new { ok = true }); } // never NACK on garbage

            var phoneNumberId = FindPhoneNumberId(root);
            var account = phoneNumberId != null
                ? await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Channel == "whatsapp"
                    && a.ExternalId == phoneNumberId)
                : null;

            // signature required once an account carries an app_secret
            if (account != null && !string.IsNullOrEmpty(account.AppSecret))
            {
                var sig = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
                if (!WebhookVerify.Meta(account.AppSecret, raw, sig))
                    return Results.StatusCode(401);
            }

            foreach (var (from, name, wamid, text, ts) in ExtractMessages(root))
                processor.ProcessInbound("whatsapp", from, text, name, account?.Id, wamid, ts);

            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        // ---- Telegram: POST inbound -------------------------------------------
        app.MapPost("/api/messaging/webhook/telegram/{token}", async (string token, HttpContext ctx,
            AppDbContext db, InboundProcessor processor) =>
        {
            var account = await db.ChannelAccounts.FirstOrDefaultAsync(
                a => a.Channel == "telegram" && a.AccessToken == token);
            var raw = await ReadBody(ctx);
            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch { return Results.Ok(new { ok = true }); }

            if (root.TryGetProperty("message", out var m) && m.TryGetProperty("chat", out var chat))
            {
                var chatId = chat.GetProperty("id").ToString();
                var text = m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var name = m.TryGetProperty("from", out var f) && f.TryGetProperty("first_name", out var fn)
                    ? fn.GetString() : null;
                if (text != "") processor.ProcessInbound("telegram", chatId, text, name, account?.Id);
            }
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        // ---- Email IMAP sync nudge (throttled; real IMAP client deferred) ------
        app.MapPost("/api/messaging/email/sync", () =>
            Results.Ok(new { ok = true, fetched = 0,
                detail = "IMAP client not configured (sandbox) — see CSHARP_PARITY.md" }))
            .RequireAuthorization(AuthPolicies.Authenticated);

        // ---- Baileys gateway pushes (X-Baileys-Secret IS the auth) -----------
        // The Node sidecar (backend/baileys_service) pushes here for a paired
        // personal number: inbound text, connection lifecycle, and delivery
        // status. Same secret it was started with (Setting baileys_secret →
        // env BAILEYS_SECRET → "baileys-local-dev"). `account_id` is the
        // gateway-side key = ChannelAccount.ExternalId (see EnsureBaileysKey).
        app.MapPost("/api/whatsapp/baileys/inbound", async (HttpContext ctx, AppDbContext db,
            ScopingService sc, InboundProcessor processor) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true }); } // never NACK on garbage
            var waId = Str(body, "wa_id");
            var text = Str(body, "text");
            if (string.IsNullOrEmpty(waId)) return Results.Ok(new { ok = true });
            var account = await BaileysAccountByKey(db, Str(body, "account_id"));
            // Baileys pushes only text here (media arrives on /media afterwards);
            // an empty text is a media-only message — still record the touch.
            processor.ProcessInbound("whatsapp", waId, text ?? "", Str(body, "name"),
                account?.Id, Str(body, "msg_id") ?? "");
            if (account != null && account.HealthStatus != Domain.Health.Healthy)
            {
                // Inbound traffic proves the session is alive — heal like Django does.
                account.HealthStatus = Domain.Health.Healthy;
                account.LastInboundAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        app.MapPost("/api/whatsapp/baileys/status-event", async (HttpContext ctx, AppDbContext db,
            ScopingService sc) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true }); }
            var account = await BaileysAccountByKey(db, Str(body, "account_id"));
            if (account == null) return Results.Ok(new { ok = true });
            var evt = (Str(body, "event") ?? "").ToLowerInvariant();
            var reason = (Str(body, "reason") ?? "").ToLowerInvariant();
            // Django health mapping: connected → healthy, disconnected → degraded,
            // logged_out / forbidden(403) → banned.
            var next = evt switch
            {
                "connected" => Domain.Health.Healthy,
                "logged_out" => Domain.Health.Banned,
                "disconnected" when reason.Contains("forbidden") || reason.Contains("logged") => Domain.Health.Banned,
                "disconnected" => Domain.Health.Degraded,
                _ => account.HealthStatus,
            };
            var changed = next != account.HealthStatus;
            account.HealthStatus = next;
            account.HealthDetail = string.IsNullOrEmpty(reason) ? evt : $"{evt}: {reason}";
            if (next == Domain.Health.Healthy) { account.LastOkAt = DateTime.UtcNow; account.FailStreak = 0; }
            if (changed) account.HealthChangedAt = DateTime.UtcNow;
            // The gateway is the only source of the paired number — learn it.
            var number = Str(body, "number");
            if (!string.IsNullOrEmpty(number) && string.IsNullOrEmpty(account.Name)) account.Name = number;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        app.MapPost("/api/whatsapp/baileys/message-status", async (HttpContext ctx, AppDbContext db,
            ScopingService sc) =>
        {
            if (!BaileysSecretOk(ctx, sc)) return Results.StatusCode(401);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(); }
            catch { return Results.Ok(new { ok = true }); }
            var msgId = Str(body, "msg_id");
            var status = Str(body, "status");
            if (string.IsNullOrEmpty(msgId) || string.IsNullOrEmpty(status)) return Results.Ok(new { ok = true });
            var msg = await db.Messages.Include(m => m.Thread).FirstOrDefaultAsync(m => m.ExternalId == msgId);
            if (msg != null)
            {
                msg.Status = status;
                if (msg.Thread != null) msg.Thread.UpdatedAt = DateTime.UtcNow; // bump so /stream refreshes
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        // Media (/media) and call events (/call-event) arrive from the gateway too;
        // media storage + the LiveKit call bridge are not in this build — ack them
        // so the gateway never retries into a 404.
        app.MapPost("/api/whatsapp/baileys/media", (HttpContext ctx, ScopingService sc) =>
            BaileysSecretOk(ctx, sc) ? Results.Ok(new { ok = true, stored = false }) : Results.StatusCode(401))
            .AllowAnonymous();
        app.MapPost("/api/whatsapp/baileys/call-event", (HttpContext ctx, ScopingService sc) =>
            BaileysSecretOk(ctx, sc) ? Results.Ok(new { ok = true }) : Results.StatusCode(401))
            .AllowAnonymous();
    }

    // ---- Baileys helpers ---------------------------------------------------
    private static bool BaileysSecretOk(HttpContext ctx, ScopingService sc)
    {
        var expected = sc.SettingValue("baileys_secret");
        if (string.IsNullOrWhiteSpace(expected))
            expected = Environment.GetEnvironmentVariable("BAILEYS_SECRET") ?? "baileys-local-dev";
        var got = ctx.Request.Headers["X-Baileys-Secret"].ToString();
        return got.Length > 0 && string.Equals(got, expected, StringComparison.Ordinal);
    }

    private static async Task<Domain.ChannelAccount?> BaileysAccountByKey(AppDbContext db, string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var acct = await db.ChannelAccounts.FirstOrDefaultAsync(a =>
            a.Channel == "whatsapp" && a.Provider == "baileys" && a.ExternalId == key);
        if (acct != null) return acct;
        // Rows paired before an ExternalId was persisted use their DB id as the key.
        return int.TryParse(key, out var id)
            ? await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == id && a.Provider == "baileys")
            : null;
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var n) && n.ValueKind == JsonValueKind.Number ? n.ToString() : null);

    private static string? FindPhoneNumberId(JsonElement root)
    {
        if (root.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var entry in entries.EnumerateArray())
                if (entry.TryGetProperty("changes", out var changes))
                    foreach (var ch in changes.EnumerateArray())
                        if (ch.TryGetProperty("value", out var val)
                            && val.TryGetProperty("metadata", out var meta)
                            && meta.TryGetProperty("phone_number_id", out var pid))
                            return pid.GetString();
        return null;
    }

    private static IEnumerable<(string from, string? name, string wamid, string text, DateTime? ts)>
        ExtractMessages(JsonElement root)
    {
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes)) continue;
            foreach (var ch in changes.EnumerateArray())
            {
                if (!ch.TryGetProperty("value", out var val)) continue;
                string? name = null;
                if (val.TryGetProperty("contacts", out var contacts) && contacts.GetArrayLength() > 0
                    && contacts[0].TryGetProperty("profile", out var prof)
                    && prof.TryGetProperty("name", out var nm))
                    name = nm.GetString();
                if (!val.TryGetProperty("messages", out var messages)) continue;
                foreach (var msg in messages.EnumerateArray())
                {
                    var type = msg.TryGetProperty("type", out var ty) ? ty.GetString() : "";
                    if (type != "text") continue; // media/interactive deferred
                    var from = msg.TryGetProperty("from", out var fr) ? fr.GetString() ?? "" : "";
                    var wamid = msg.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                    var text = msg.TryGetProperty("text", out var tx)
                        && tx.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
                    DateTime? ts = null;
                    if (msg.TryGetProperty("timestamp", out var tsv)
                        && long.TryParse(tsv.GetString(), out var unix))
                        ts = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                    if (from != "" && text != "") yield return (from, name, wamid, text, ts);
                }
            }
        }
    }

    private static async Task<byte[]> ReadBody(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}
