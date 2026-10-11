using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Outbound for the channels in <see cref="MoreChannels"/>, called by
/// <see cref="OutboundSender"/> (the one live-or-sandbox seam) exactly like
/// <see cref="TelegramChannel"/>:
/// <list type="number">
///   <item>the carrying account by the shared send-time rules
///         (<see cref="Routing.ForThread"/>: a LINE / Viber / WeChat / TikTok / Douyin /
///         Zalo conversation keeps its own account; SMS spreads like email);
///         no configured account = <c>status="sandbox"</c>, nothing leaves;</item>
///   <item>the platform's reply window (<see cref="ChannelWindow"/>) — refused sends
///         become a failed row and the provider is never called;</item>
///   <item>the provider round-trip, recorded honestly: <c>sent</c> + the provider's id,
///         or a <c>failed</c> system row with its reason; the account's health moves
///         only for account-level failures (a refused token, a suspension, repeated
///         outages), never for a recipient's.</item>
/// </list>
/// </summary>
public sealed class MoreChannelSender
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly ThreadStore _threads;
    private readonly ChannelTransports _transports;
    private readonly TimeProvider _clock;

    public MoreChannelSender(AppDbContext db, ScopingService scoping, ThreadStore threads,
        ChannelTransports transports, TimeProvider? clock = null)
    {
        _db = db;
        _scoping = scoping;
        _threads = threads;
        _transports = transports;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The configured account that carries this thread (tracked), or null = sandbox.</summary>
    public ChannelAccount? Route(Thread thread, int? accountId, bool apply = false)
    {
        try
        {
            var r = new Routing(_db, _scoping).ForThread(thread, RouteNeed.Any, accountId ?? thread.AccountId, apply);
            var account = r.Account ?? r.DownAccount;
            if (account == null || account.Channel != thread.Channel || !MoreChannels.Configured(account)) return null;
            return _db.ChannelAccounts.Find(account.Id);
        }
        catch { return null; }
    }

    public bool WouldSendLive(Thread thread, int? accountId) =>
        _transports.For(thread.Channel) != null && Route(thread, accountId) != null;

    /// <summary>
    /// Why a send may not go out now because of the platform's reply window (null =
    /// it may, or the thread is not live — sandbox never dead-ends).
    /// </summary>
    public string? WindowBlock(Thread thread, int? accountId, bool humanAgent, DateTime? nowUtc = null)
    {
        if (!ChannelWindow.Applies(thread.Channel)) return null;
        if (Route(thread, accountId) == null) return null;
        var last = ChannelWindow.LastInbound(_db, thread);
        var state = ChannelWindow.StateAt(thread.Channel, last, nowUtc ?? Now);
        return ChannelWindow.Refusal(thread.Channel, state, humanAgent, everWrote: last != null);
    }

    /// <summary>Whether this channel's transport can carry an uploaded file (WeChat, Zalo).</summary>
    public bool SupportsMedia(string channel) => _transports.For(channel)?.SupportsMedia ?? false;

    public Message Send(Thread thread, string text, string source, int? accountId, OutboundMedia? media = null,
        bool humanAgent = false)
    {
        var transport = _transports.For(thread.Channel);
        var account = transport == null ? null : Route(thread, accountId, apply: true);
        if (account == null)
        {
            var sandbox = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sandbox", accountId: accountId ?? thread.AccountId);
            return WithMedia(sandbox, media);
        }
        if (thread.AccountId != account.Id)
        {
            thread.AccountId = account.Id;
            thread.Account = account;
            _scoping.SyncThreadEntity(thread, save: false);
            _db.SaveChanges();
        }

        // the platform's reply window, checked again on the send path itself
        var lastIn = _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Select(m => new { m.ExternalId, m.CreatedAt }).FirstOrDefault();
        if (ChannelWindow.Applies(thread.Channel))
        {
            var state = ChannelWindow.StateAt(thread.Channel, lastIn?.CreatedAt, Now);
            if (ChannelWindow.Refusal(thread.Channel, state, humanAgent, everWrote: lastIn != null) is { } refusal)
                return WithMedia(Failed(thread, account, text, refusal, $"window_closed: {refusal}"), media);
        }

        var target = new SendTarget(thread.Key, lastIn?.ExternalId,
            lastIn == null ? null : DateTime.SpecifyKind(lastIn.CreatedAt, DateTimeKind.Utc), thread.Id);
        TransportResult result;
        try
        {
            if (media != null)
            {
                var caption = text.StartsWith('[') && text.EndsWith(']') ? null : text; // "[image]" is a placeholder
                result = transport!.SupportsMedia
                    ? transport.SendMediaAsync(account, target, media, caption).GetAwaiter().GetResult()
                    : TransportResult.Fail(TransportFailure.Unsupported,
                        $"{MoreChannels.Label(thread.Channel)} can't carry an uploaded file from ChatBranch — send a link instead");
            }
            else
            {
                result = TransportResult.Fail(TransportFailure.Unsupported, "empty message");
                foreach (var chunk in ProviderHttp.Chunks(text, transport!.MaxText))
                {
                    result = transport.SendTextAsync(account, target, chunk).GetAwaiter().GetResult();
                    if (!result.Ok) break;
                }
            }
        }
        catch (Exception ex)
        {
            result = TransportResult.Fail(TransportFailure.Transient, $"send failed: {ex.GetType().Name}");
        }

        Message msg;
        if (result.Ok)
        {
            msg = _threads.AppendMessage(thread, MessageDirection.Out, source, text,
                status: "sent", externalId: result.ExternalId, accountId: account.Id);
            HealthRecorder.Ok(_db, account);
        }
        else
        {
            msg = Failed(thread, account, text, result.Detail,
                result.Code.Length > 0 ? $"{result.Code}: {result.Detail}" : result.Detail);
            Record(_db, account, result);
            if (result.RecipientOptedOut)
                ChannelOptOut.Set(_db, _threads, thread, true, "unsubscribed (the provider refuses to deliver)");
        }
        _db.SaveChanges(); // also persists a token the transport refreshed (Zalo)
        return WithMedia(msg, media);
    }

    /// <summary>A refused send's account consequence (callers save).</summary>
    public static void Record(AppDbContext db, ChannelAccount account, TransportResult result)
    {
        switch (result.Failure)
        {
            case TransportFailure.Auth:
                AccountHealth.SetStatus(db, account, Health.Disconnected, result.Detail);
                break;
            case TransportFailure.Banned:
                AccountHealth.SetStatus(db, account, Health.Banned, result.Detail);
                break;
            case TransportFailure.Billing:
                AccountHealth.SetStatus(db, account, Health.Degraded, result.Detail);
                break;
            case TransportFailure.RateLimit:
            case TransportFailure.Transient:
                HealthRecorder.Transient(db, account, result.Detail);
                break;
            default:
                break; // the recipient's problem, a closed window, an unsupported file: health untouched
        }
    }

    private Message Failed(Thread thread, ChannelAccount account, string text, string reason, string error)
    {
        var msg = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.System,
            $"[send failed: {reason}] {text}", status: "failed", accountId: account.Id);
        msg.Error = error.Length > 300 ? error[..300] : error;
        _db.SaveChanges();
        return msg;
    }

    private Message WithMedia(Message msg, OutboundMedia? media)
    {
        if (media == null) return msg;
        MediaStore.Stamp(msg, media.MediaType, media.RelPath, MessagingMedia.ContentTypeFor(media.FileName),
            media.FileName, MediaStore.SizeOf(media.AbsolutePath));
        _db.SaveChanges();
        return msg;
    }
}
