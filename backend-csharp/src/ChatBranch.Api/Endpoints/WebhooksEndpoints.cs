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
    }

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
