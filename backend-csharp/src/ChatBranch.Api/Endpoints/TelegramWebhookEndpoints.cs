using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using Microsoft.EntityFrameworkCore;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Telegram Bot API updates (port of messaging/views.telegram_webhook), the
/// Chatwoot-style path <c>POST /api/messaging/webhook/telegram/{bot-token}</c>.
///
/// Authentication, two layers:
///  * the bot token in the path routes to the account — compared in constant
///    time against every linked bot; an unknown token is a 404;
///  * when the webhook was registered from the dashboard ("Register webhook"
///    → setWebhook with a <c>secret_token</c>, stored as
///    <c>config.webhook_secret</c>), Telegram echoes it in
///    <c>X-Telegram-Bot-Api-Secret-Token</c> and a missing/wrong header is a 401.
///    A webhook registered by hand (no stored secret) relies on the path token.
///
/// The update runs the normal inbound pipeline; an attachment (photo, document,
/// voice, video, static sticker) is downloaded via getFile OFF the ack and hung
/// on the message when it lands. Inbound traffic heals the bot's health.
/// Garbage, non-message updates and redeliveries are acked 200 without side
/// effects — never a 5xx into Telegram's retry loop.
/// </summary>
public static class TelegramWebhookEndpoints
{
    public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    public static void MapTelegramWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/messaging/webhook/telegram/{token}", async (string token, HttpContext ctx,
            AppDbContext db, InboundProcessor processor, IServiceScopeFactory scopes) =>
        {
            var bots = await db.ChannelAccounts
                .Where(a => a.Channel == ChannelIds.Telegram && a.AccessToken != "").ToListAsync();
            var account = TelegramBot.FindByToken(bots, token);
            if (account == null) return Results.Json(new { detail = "Unknown bot token." }, statusCode: 404);

            var secret = TelegramBot.WebhookSecret(account);
            if (secret.Length > 0 && !TelegramBot.SecretEquals(ctx.Request.Headers[SecretHeader].ToString(), secret))
                return Results.Json(new { detail = "Bad webhook secret." }, statusCode: 401);

            JsonElement root;
            try { root = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { return Results.Ok(new { ok = true }); } // never NACK on garbage

            var update = TelegramInbound.Parse(root);
            if (update == null) return Results.Ok(new { ok = true, ignored = true });

            try
            {
                // a redelivery (Telegram retries a slow ack) must not page anyone twice
                var known = update.MessageId.Length > 0 && await db.Messages.AnyAsync(m =>
                    m.ExternalId == update.MessageId && m.Thread!.Channel == ChannelIds.Telegram
                    && m.Thread.Key == update.ChatId);
                if (known) return Results.Ok(new { ok = true, duplicate = true });

                var msg = processor.ProcessInbound(ChannelIds.Telegram, update.ChatId, update.Text,
                    update.Name, account.Id, update.MessageId, update.Date);

                // an inbound update proves the bot (and its webhook) work
                account.LastInboundAt = DateTime.UtcNow;
                HealthRecorder.Ok(db, account);
                await db.SaveChangesAsync();

                if (update.Media is { } media)
                    _ = Task.Run(async () =>
                    {
                        using var scope = scopes.CreateScope();
                        var sp = scope.ServiceProvider;
                        await TelegramInbound.AttachMediaAsync(sp.GetRequiredService<AppDbContext>(),
                            sp.GetRequiredService<ITelegramClient>(), sp.GetRequiredService<VoiceAgentBridge>().DataDir,
                            account.Id, msg.Id, media);
                    });
            }
            catch { /* stored what we could; Telegram must still get its 200 */ }
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();
    }
}
