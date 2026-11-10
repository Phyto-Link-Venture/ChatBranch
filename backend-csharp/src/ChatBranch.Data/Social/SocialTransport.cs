using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Data.Social;

/// <summary>
/// The transport of the <c>social</c> channel: one <see cref="IChannelTransport"/>, so
/// the existing send path (<see cref="OutboundSender"/> → <see cref="MoreChannelSender"/>
/// → routing → health) carries comment replies, review replies and DMs with no
/// further wiring. What a send DOES depends on the conversation's kind:
/// <list type="bullet">
///   <item>comment → a public reply to the thread's top-level comment (<c>POST /v1/inbox/comments/{postId}</c>);</item>
///   <item>review → the business's reply to the review (Google keeps ONE reply per review, so a
///         second send replaces the first);</item>
///   <item>dm → a message in the Zernio conversation.</item>
/// </list>
/// The ids a reply needs (the post id, the full review name, the conversation id)
/// are on <see cref="SocialThread"/>, read through a fresh scope (this is a singleton).
/// </summary>
public sealed class SocialTransport : IChannelTransport
{
    private readonly IZernioClient _zernio;
    private readonly IServiceScopeFactory _scopes;

    public SocialTransport(IZernioClient zernio, IServiceScopeFactory scopes)
    {
        _zernio = zernio;
        _scopes = scopes;
    }

    public string Channel => ChatBranch.Domain.Channels.Social;

    /// <summary>
    /// High on purpose: splitting a public reply into several comments is worse than a
    /// platform refusing an over-long one (X's 280) honestly.
    /// </summary>
    public int MaxText => 8000;

    public bool SupportsMedia => false;

    private SocialThread? Context(SendTarget to)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (to.ThreadId > 0)
        {
            var row = db.SocialThreads.AsNoTracking().FirstOrDefault(s => s.ThreadId == to.ThreadId);
            if (row != null) return row;
        }
        var threadId = db.Threads.AsNoTracking()
            .Where(t => t.Channel == ChatBranch.Domain.Channels.Social && t.Key == to.Key)
            .Select(t => (int?)t.Id).FirstOrDefault();
        return threadId is int id ? db.SocialThreads.AsNoTracking().FirstOrDefault(s => s.ThreadId == id) : null;
    }

    public async Task<TransportResult> SendTextAsync(ChannelAccount a, SendTarget to, string text,
        CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured)
            return TransportResult.Fail(TransportFailure.Transient, _zernio.NotConfiguredReason);
        var st = Context(to);
        var kind = st?.Kind ?? SocialChannels.KindOf(to.Key);
        if (st == null)
            return TransportResult.Fail(TransportFailure.Recipient,
                "this social conversation has lost its context (post / review / conversation id) — reply on the platform");

        ZernioReply r;
        string externalId;
        switch (kind)
        {
            case SocialChannels.Comment:
                if (st.PostId.Length == 0)
                    return TransportResult.Fail(TransportFailure.Recipient, "the comment's post is unknown");
                r = await _zernio.ReplyToCommentAsync(a.ExternalId, st.PostId, st.RootCommentId, text, ct);
                externalId = r.Str("data", "commentId");
                break;
            case SocialChannels.Review:
                r = await _zernio.ReplyToReviewAsync(a.ExternalId, st.ReviewId, text, ct);
                externalId = r.Str("reply", "id");
                if (externalId.Length == 0 && r.Ok) externalId = $"reply:{SocialChannels.KeyPart(st.ReviewId)}:{DateTime.UtcNow.Ticks}";
                break;
            case SocialChannels.Dm:
                r = await _zernio.SendMessageAsync(a.ExternalId, st.ConversationId, text, ct);
                externalId = r.Str("data", "messageId");
                break;
            default:
                return TransportResult.Fail(TransportFailure.Unsupported, "not a social conversation key");
        }
        return r.Ok ? TransportResult.Success(externalId) : Fail(r);
    }

    public Task<TransportResult> SendMediaAsync(ChannelAccount a, SendTarget to, OutboundMedia media, string? caption,
        CancellationToken ct = default) =>
        Task.FromResult(TransportResult.Fail(TransportFailure.Unsupported,
            "a social reply cannot carry an uploaded file from ChatBranch — send a link instead"));

    /// <summary>Health = does Zernio still hold this account, active.</summary>
    public async Task<TransportProbe> ProbeAsync(ChannelAccount a, CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured)
            return new TransportProbe(new ProbeVerdict(null, _zernio.NotConfiguredReason, Transient: true));
        var r = await _zernio.ListAccountsAsync(null, ct);
        if (!r.Ok)
            return new TransportProbe(new ProbeVerdict(r.Status == 401 ? Health.Disconnected : null,
                r.Error, Transient: r.Status != 401));
        var mine = ZernioJson.Arr(r.Json, "accounts").FirstOrDefault(x => ZernioJson.Str(x, "_id") == a.ExternalId);
        if (mine.ValueKind != System.Text.Json.JsonValueKind.Object)
            return new TransportProbe(new ProbeVerdict(Health.Disconnected,
                "Zernio no longer holds this account — connect it again"));
        var active = ZernioJson.Bool(mine, "isActive") && !ZernioJson.Bool(mine, "needsReconnection");
        return new TransportProbe(active
                ? new ProbeVerdict(Health.Healthy, "")
                : new ProbeVerdict(Health.Disconnected, "the platform connection needs to be renewed in Zernio"),
            "");
    }

    public Task<(byte[]? Content, string Detail)> DownloadAsync(ChannelAccount a, InboundMediaRef media,
        CancellationToken ct = default) =>
        Task.FromResult<(byte[]?, string)>((null, "social media is linked, not downloaded"));

    /// <summary>What Zernio's refusal means for the account's health.</summary>
    public static TransportResult Fail(ZernioReply r)
    {
        var failure = r.Status switch
        {
            401 => TransportFailure.Auth,
            402 => TransportFailure.Billing,
            409 when r.Error.Contains("disconnect", StringComparison.OrdinalIgnoreCase) => TransportFailure.Auth,
            429 => TransportFailure.RateLimit,
            0 or >= 500 => TransportFailure.Transient,
            _ => TransportFailure.Recipient, // 400 / 403 / 404 / 422: this comment, review or person
        };
        return TransportResult.Fail(failure, r.Error.Length > 0 ? r.Error : $"Zernio answered HTTP {r.Status}", r.Status);
    }
}
