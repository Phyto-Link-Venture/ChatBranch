using System.Net.Http.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai;

/// <summary>
/// "typing…" for channels that support it. Best-effort and fire-and-forget: a
/// presence update that fails must never delay or break a send.
/// </summary>
public interface ITypingIndicator
{
    /// <summary>Show the customer that a reply is being typed on this account.</summary>
    void Start(Thread thread, ChannelAccount account);
}

/// <summary>No-op indicator (sandbox, tests).</summary>
public sealed class NoTypingIndicator : ITypingIndicator
{
    public void Start(Thread thread, ChannelAccount account) { }
}

/// <summary>
/// The registered indicator: picks the channel's own implementation — Telegram
/// bots through <see cref="TelegramTypingIndicator"/>, everything else (WhatsApp
/// Cloud + Baileys; a no-op elsewhere) through <see cref="LiveTypingIndicator"/>.
/// </summary>
public sealed class ChannelTypingIndicator : ITypingIndicator
{
    private readonly ITypingIndicator _whatsapp;
    private readonly ITypingIndicator _telegram;

    public ChannelTypingIndicator(ITypingIndicator whatsapp, ITypingIndicator telegram)
    {
        _whatsapp = whatsapp;
        _telegram = telegram;
    }

    public void Start(Thread thread, ChannelAccount account)
    {
        try
        {
            if (thread.Channel == ChatBranch.Domain.Channels.Telegram) _telegram.Start(thread, account);
            else _whatsapp.Start(thread, account);
        }
        catch { /* presence is best-effort */ }
    }
}

/// <summary>
/// Telegram: <c>sendChatAction(typing)</c> from the bot that carries the chat.
/// Fire-and-forget — the Bot API call runs off the caller's thread and any
/// failure (bad token, network, a blocked bot) is swallowed; health is never
/// touched by a presence call.
/// </summary>
public sealed class TelegramTypingIndicator : ITypingIndicator
{
    private readonly ChatBranch.Data.Channels.ITelegramClient _client;

    public TelegramTypingIndicator(ChatBranch.Data.Channels.ITelegramClient client) => _client = client;

    public void Start(Thread thread, ChannelAccount account) => _ = StartAsync(thread, account);

    /// <summary>The same call, awaitable (tests). Never throws; false = nothing was sent.</summary>
    public Task<bool> StartAsync(Thread thread, ChannelAccount account)
    {
        try
        {
            if (thread.Channel != ChatBranch.Domain.Channels.Telegram
                || !ChatBranch.Data.Channels.TelegramBot.Configured(account)
                || string.IsNullOrEmpty(thread.Key)) return Task.FromResult(false);
            var chatId = thread.Key;
            return Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var r = await ChatBranch.Data.Channels.TelegramBot.SendChatActionAsync(_client, account, chatId,
                        "typing", cts.Token);
                    return r.Ok;
                }
                catch { return false; }
            });
        }
        catch { return Task.FromResult(false); }
    }
}

/// <summary>
/// The live indicator:
///  * WhatsApp Cloud — marks the customer's latest message as read and shows the
///    typing indicator (Graph <c>/{phone_number_id}/messages</c> with
///    <c>status: read</c> + <c>typing_indicator</c>; Meta clears it on the next
///    send or after ~25 s).
///  * Baileys — the gateway's <c>POST /accounts/{id}/presence</c>
///    (<c>sendPresenceUpdate('composing')</c>).
///  * Telegram has its own (<see cref="TelegramTypingIndicator"/>, routed by
///    <see cref="ChannelTypingIndicator"/>); every other channel has no typing
///    signal and nothing is sent.
/// </summary>
public sealed class LiveTypingIndicator : ITypingIndicator
{
    private readonly IHttpClientFactory _http;
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;

    public LiveTypingIndicator(IHttpClientFactory http, AppDbContext db, ScopingService scoping)
    {
        _http = http;
        _db = db;
        _scoping = scoping;
    }

    public void Start(Thread thread, ChannelAccount account)
    {
        try
        {
            if (thread.Channel != ChatBranch.Domain.Channels.WhatsApp) return;
            if (account.Provider == Providers.Baileys)
            {
                if (string.IsNullOrEmpty(account.ExternalId)) return;
                var url = Setting("baileys_url", "BAILEYS_URL", "http://127.0.0.1:3020").TrimEnd('/');
                var secret = Setting("baileys_secret", "BAILEYS_SECRET", "baileys-local-dev");
                var path = $"{url}/accounts/{Uri.EscapeDataString(account.ExternalId)}/presence";
                var to = thread.Key;
                Fire(async client =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, path);
                    req.Headers.TryAddWithoutValidation("X-Baileys-Secret", secret);
                    req.Content = JsonContent.Create(new { to, state = "composing" });
                    using var _ = await client.SendAsync(req);
                });
                return;
            }

            if (string.IsNullOrEmpty(account.AccessToken) || string.IsNullOrEmpty(account.ExternalId)) return;
            // the typing indicator rides on the read receipt of the customer's latest message
            var wamid = _db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In && m.ExternalId != "")
                .OrderByDescending(m => m.Id).Select(m => m.ExternalId).FirstOrDefault();
            if (string.IsNullOrEmpty(wamid)) return;
            var version = string.IsNullOrEmpty(account.ApiVersion) ? "v23.0" : account.ApiVersion;
            var graph = $"https://graph.facebook.com/{version}/{account.ExternalId}/messages";
            var token = account.AccessToken;
            Fire(async client =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, graph);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                req.Content = JsonContent.Create(new
                {
                    messaging_product = "whatsapp",
                    status = "read",
                    message_id = wamid,
                    typing_indicator = new { type = "text" },
                });
                using var _ = await client.SendAsync(req);
            });
        }
        catch { /* presence is best-effort */ }
    }

    private void Fire(Func<HttpClient, Task> call)
    {
        var client = _http.CreateClient("typing");
        client.Timeout = TimeSpan.FromSeconds(5);
        _ = Task.Run(async () =>
        {
            try { await call(client); } catch { /* never surfaces */ }
        });
    }

    private string Setting(string key, string env, string fallback)
    {
        var v = _scoping.SettingValue(key);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        var e = Environment.GetEnvironmentVariable(env);
        return string.IsNullOrWhiteSpace(e) ? fallback : e.Trim();
    }
}
