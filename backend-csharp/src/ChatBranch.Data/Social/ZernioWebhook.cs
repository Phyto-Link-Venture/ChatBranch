using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Social;

/// <summary>What handling one webhook event did (for the answer and the tests).</summary>
public sealed record ZernioOutcome(string Result, string Detail = "", int? ThreadId = null, int? MessageId = null);

/// <summary>
/// Turns one Zernio webhook event into inbox / account / post state. Payload shapes
/// follow Zernio's webhook reference: <c>comment.received</c> (comment + post +
/// account), <c>review.new</c> / <c>review.updated</c> (ReviewWebhookReview),
/// <c>message.received</c> / <c>message.sent</c> / <c>message.failed</c>
/// (InboxWebhookMessage + conversation + account), <c>account.connected</c> /
/// <c>account.disconnected</c>, and the post lifecycle (<c>post.*</c>).
/// The signature check and the event-id dedupe happen before this (the endpoint).
/// </summary>
public sealed class ZernioWebhook
{
    /// <summary>What the registered Zernio webhook subscribes to.</summary>
    public static readonly string[] Events =
    {
        "account.connected", "account.disconnected",
        "comment.received", "review.new", "review.updated",
        "message.received", "message.sent", "message.failed",
        "post.scheduled", "post.published", "post.partial", "post.failed", "post.cancelled",
        "post.platform.published", "post.platform.failed", "post.platform.deleted",
    };

    private readonly AppDbContext _db;
    private readonly ThreadStore _threads;
    private readonly InboundProcessor _inbound;
    private readonly SocialAccounts _accounts;

    public ZernioWebhook(AppDbContext db, ThreadStore threads, InboundProcessor inbound, SocialAccounts accounts)
    {
        _db = db;
        _threads = threads;
        _inbound = inbound;
        _accounts = accounts;
    }

    /// <summary>lowercase hex HMAC-SHA256 of the raw body with the webhook secret — Zernio's X-Zernio-Signature.</summary>
    public static string Sign(string secret, byte[] body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    public static bool Verify(string secret, byte[] body, string? signature)
    {
        var sent = (signature ?? "").Trim().ToLowerInvariant();
        if (sent.StartsWith("sha256=")) sent = sent[7..];
        if (sent.Length == 0 || secret.Length == 0) return false;
        var want = Sign(secret, body);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(want), Encoding.ASCII.GetBytes(sent));
    }

    public async Task<ZernioOutcome> HandleAsync(string eventName, JsonElement body, CancellationToken ct = default)
    {
        if (ZernioJson.Bool(body, "test")) return new ZernioOutcome("ignored", "test event");
        switch (eventName)
        {
            case "webhook.test": return new ZernioOutcome("ok", "test event");
            case "account.connected": return await AccountConnectedAsync(body, ct);
            case "account.disconnected": return AccountDisconnected(body);
            case "comment.received": return await CommentAsync(body, ct);
            case "review.new":
            case "review.updated": return await ReviewAsync(body, eventName == "review.updated", ct);
            case "message.received": return await MessageReceivedAsync(body, ct);
            case "message.sent": return await MessageSentAsync(body, ct);
            case "message.failed": return MessageFailed(body);
            default:
                if (eventName.StartsWith("post.", StringComparison.Ordinal))
                    return SocialPosts.ApplyWebhook(_db, eventName, body);
                return new ZernioOutcome("ignored", $"event {eventName} is not handled");
        }
    }

    // ---- accounts --------------------------------------------------------------

    private async Task<ZernioOutcome> AccountConnectedAsync(JsonElement body, CancellationToken ct)
    {
        var acct = ZernioJson.At(body, "account");
        var profileId = ZernioJson.Str(acct, "profileId");
        var external = ZernioJson.Arr(body, "profiles")
            .Where(p => ZernioJson.Str(p, "id") == profileId).Select(p => ZernioJson.Str(p, "externalId"))
            .FirstOrDefault() ?? "";
        var (known, entityId) = _accounts.EntityOfProfile(profileId, external);
        if (!known)
        {
            // a profile ChatBranch has not seen (the default one, typically): learn it from a sync
            var (_, _, _, error) = await _accounts.SyncAsync(ct);
            var mirrored = _accounts.Find(ZernioJson.Str(acct, "accountId"));
            return mirrored != null
                ? new ZernioOutcome("ok", "account synced")
                : new ZernioOutcome("ignored", error.Length > 0 ? error : "the account's profile is not ChatBranch's");
        }
        var row = _accounts.Upsert(acct, entityId, profileId);
        return new ZernioOutcome("ok", $"account {row.Name} connected");
    }

    private ZernioOutcome AccountDisconnected(JsonElement body)
    {
        var acct = ZernioJson.At(body, "account");
        var removed = ZernioJson.Str(acct, "disconnectionType") == "intentional";
        var row = _accounts.MarkDisconnected(ZernioJson.Str(acct, "accountId"), ZernioJson.Str(acct, "reason"), removed);
        return row == null ? new ZernioOutcome("ignored", "unknown account") : new ZernioOutcome("ok", "account disconnected");
    }

    /// <summary>The mirrored inbox an inbox event belongs to (learnt from a sync when new).</summary>
    private async Task<ChannelAccount?> AccountForAsync(JsonElement body, CancellationToken ct)
    {
        var acct = ZernioJson.At(body, "account");
        var id = ZernioJson.Str(acct, "accountId");
        if (id.Length == 0) id = ZernioJson.Str(acct, "id");
        var row = _accounts.Find(id);
        if (row != null) return row;
        var profileId = ZernioJson.Str(acct, "profileId");
        foreach (var p in ZernioJson.Arr(body, "profiles"))
        {
            var pid = ZernioJson.Str(p, "id");
            if (profileId.Length > 0 && pid != profileId) continue;
            var (known, entityId) = _accounts.EntityOfProfile(pid, ZernioJson.Str(p, "externalId"));
            if (known) return _accounts.Upsert(acct, entityId, pid);
        }
        await _accounts.SyncAsync(ct);
        return _accounts.Find(id);
    }

    // ---- comments ----------------------------------------------------------------

    private async Task<ZernioOutcome> CommentAsync(JsonElement body, CancellationToken ct)
    {
        var account = await AccountForAsync(body, ct);
        if (account == null) return new ZernioOutcome("ignored", "unknown account");
        var c = ZernioJson.At(body, "comment");
        var post = ZernioJson.At(body, "post");
        var commentId = ZernioJson.Str(c, "id");
        if (commentId.Length == 0) return new ZernioOutcome("ignored", "no comment id");
        var platform = ZernioJson.Str(c, "platform");
        if (platform.Length == 0) platform = SocialChannels.Platform(account);
        var acctId = account.ExternalId;

        // our own reply coming back (Meta re-delivers the account's own comments): already stored
        if (await KnownOutboundAsync(commentId, ct)) return new ZernioOutcome("duplicate", "our own reply");

        // replies join the thread of their top-level comment
        var parent = ZernioJson.Str(c, "parentCommentId");
        var root = commentId;
        SocialThread? ctxRow = null;
        if (ZernioJson.Bool(c, "isReply") && parent.Length > 0)
        {
            ctxRow = await _db.SocialThreads.FirstOrDefaultAsync(s => s.ZernioAccountId == acctId
                                                                    && s.RootCommentId == parent, ct);
            if (ctxRow == null)
            {
                var viaMessage = await _db.Messages.AsNoTracking()
                    .Where(m => m.ExternalId == parent && m.Thread!.Channel == ChatBranch.Domain.Channels.Social)
                    .Select(m => (int?)m.ThreadId).FirstOrDefaultAsync(ct);
                if (viaMessage is int tid)
                    ctxRow = await _db.SocialThreads.FirstOrDefaultAsync(s => s.ThreadId == tid, ct);
            }
            root = ctxRow?.RootCommentId is { Length: > 0 } r ? r : parent;
        }
        var key = ctxRow != null
            ? _db.Threads.AsNoTracking().Where(t => t.Id == ctxRow.ThreadId).Select(t => t.Key).First()
            : SocialChannels.ThreadKey(SocialChannels.Comment, acctId, root);

        var author = ZernioJson.At(c, "author");
        var authorName = FirstNonEmpty(ZernioJson.Str(author, "name"), ZernioJson.Str(author, "username"), "Someone");
        var text = ZernioJson.Str(c, "text");
        if (text.Length == 0 && ZernioJson.At(c, "attachment").ValueKind == JsonValueKind.Object)
            text = "[" + FirstNonEmpty(ZernioJson.Str(c, "attachment", "type"), "attachment") + "]"
                   + (ZernioJson.Str(c, "attachment", "url") is { Length: > 0 } u ? " " + u : "");
        var at = Time(ZernioJson.Str(c, "createdAt"));

        // the account's OWN comment written on the platform: the person's outbound reply
        if (ZernioJson.Bool(author, "isOwnAccount"))
        {
            var existing = await _db.Threads.FirstOrDefaultAsync(t => t.Channel == ChatBranch.Domain.Channels.Social
                                                                       && t.Key == key, ct);
            if (existing == null) return new ZernioOutcome("ignored", "the account's own comment");
            var own = _threads.AppendMessage(existing, MessageDirection.Out, MessageSource.ExternalClient, text,
                status: "sent", externalId: commentId, accountId: account.Id, providerTs: at);
            return new ZernioOutcome("ok", "own reply recorded", existing.Id, own.Id);
        }

        var thread = _threads.GetOrCreateThread(ChatBranch.Domain.Channels.Social, key, authorName, account.Id);
        var st = Context(thread, SocialChannels.Comment, platform, account);
        if (st.PostId.Length == 0) st.PostId = ZernioJson.Str(post, "platformPostId");
        if (st.PostId.Length == 0) st.PostId = ZernioJson.Str(c, "platformPostId");
        if (st.ZernioPostId.Length == 0) st.ZernioPostId = FirstNonEmpty(ZernioJson.Str(post, "id"), ZernioJson.Str(c, "postId"));
        if (ZernioJson.Str(post, "content") is { Length: > 0 } pt) st.PostText = Clip(pt, 2000);
        if (ZernioJson.Str(post, "permalink") is { Length: > 0 } pl) st.PostPermalink = pl;
        if (ZernioJson.Str(post, "imageUrl") is { Length: > 0 } pi) st.PostMediaUrl = pi;
        if (st.RootCommentId.Length == 0) st.RootCommentId = root;
        if (st.AuthorId.Length == 0)
        {
            st.AuthorId = ZernioJson.Str(author, "id");
            st.AuthorName = authorName;
            st.AuthorHandle = ZernioJson.Str(author, "username");
            st.AuthorAvatar = ZernioJson.Str(author, "picture");
        }
        st.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var msg = _inbound.ProcessInbound(ChatBranch.Domain.Channels.Social, key, text, authorName, account.Id,
            commentId, at, sender: new InboundSender(ZernioJson.Str(author, "id"), authorName));
        Heal(account);
        return new ZernioOutcome("ok", "comment stored", msg.ThreadId, msg.Id);
    }

    // ---- reviews -------------------------------------------------------------------

    private async Task<ZernioOutcome> ReviewAsync(JsonElement body, bool updated, CancellationToken ct)
    {
        var account = await AccountForAsync(body, ct);
        if (account == null) return new ZernioOutcome("ignored", "unknown account");
        var r = ZernioJson.At(body, "review");
        var reviewId = ZernioJson.Str(r, "id");
        if (reviewId.Length == 0) return new ZernioOutcome("ignored", "no review id");
        var key = SocialChannels.ThreadKey(SocialChannels.Review, account.ExternalId, reviewId);
        var rating = Math.Clamp(ZernioJson.Int(r, "rating"), 0, 5);
        var reviewer = ZernioJson.At(r, "reviewer");
        var name = FirstNonEmpty(ZernioJson.Str(reviewer, "name"), "A reviewer");
        var text = ZernioJson.Str(r, "text");
        var shown = text.Length > 0 ? text : $"{new string('★', rating)}{new string('☆', 5 - rating)} (rating only)";

        var thread = _threads.GetOrCreateThread(ChatBranch.Domain.Channels.Social, key, name, account.Id);
        var st = Context(thread, SocialChannels.Review, ZernioJson.Str(r, "platform") is { Length: > 0 } p ? p : "googlebusiness",
            account);
        var changed = st.Rating != rating;
        st.Rating = rating;
        st.ReviewId = reviewId;
        if (st.AuthorName.Length == 0)
        {
            st.AuthorId = ZernioJson.Str(reviewer, "id");
            st.AuthorName = name;
            st.AuthorAvatar = ZernioJson.Str(reviewer, "profileImage");
        }
        st.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // the review itself: once on review.new; again only when the reviewer EDITED it
        var lastIn = await _db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In)
            .OrderByDescending(m => m.Id).Select(m => m.Text).FirstOrDefaultAsync(ct);
        Message? stored = null;
        if (lastIn == null || (updated && (lastIn != shown || changed)))
        {
            var ext = lastIn == null ? reviewId : $"{reviewId}#{Hash(shown + rating)}";
            stored = _inbound.ProcessInbound(ChatBranch.Domain.Channels.Social, key, shown, name, account.Id, ext,
                Time(ZernioJson.Str(r, "createdAt")), sender: new InboundSender(ZernioJson.Str(reviewer, "id"), name));
        }

        // a reply written in Google itself: the person's outbound answer (ours are already stored)
        if (ZernioJson.Bool(r, "hasReply") && ZernioJson.Str(r, "reply", "text") is { Length: > 0 } reply)
        {
            var ours = await _db.Messages.AnyAsync(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.Out
                                                        && m.Status != "failed" && m.Text == reply, ct);
            if (!ours)
                _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.ExternalClient, reply,
                    status: "sent", externalId: $"reply:{Hash(reply)}", accountId: account.Id,
                    providerTs: Time(ZernioJson.Str(r, "reply", "createdAt")));
        }
        Heal(account);
        return new ZernioOutcome("ok", stored != null ? "review stored" : "review unchanged", thread.Id, stored?.Id);
    }

    // ---- DMs -------------------------------------------------------------------------

    private async Task<ZernioOutcome> MessageReceivedAsync(JsonElement body, CancellationToken ct)
    {
        var account = await AccountForAsync(body, ct);
        if (account == null) return new ZernioOutcome("ignored", "unknown account");
        var m = ZernioJson.At(body, "message");
        if (ZernioJson.Str(m, "direction") == "outgoing") return await MessageSentAsync(body, ct);
        var platform = FirstNonEmpty(ZernioJson.Str(m, "platform"), SocialChannels.Platform(account));

        // a platform with a native ChatBranch inbox in this entity already carries its DMs
        if (SocialChannels.NativeChannel(platform) is { } native
            && await _db.ChannelAccounts.AnyAsync(a => a.Channel == native && a.EntityId == account.EntityId, ct))
            return new ZernioOutcome("skipped",
                $"{SocialChannels.PlatformLabel(platform)} DMs arrive through the native {native} inbox");

        var conversationId = FirstNonEmpty(ZernioJson.Str(m, "conversationId"), ZernioJson.Str(body, "conversation", "id"));
        if (conversationId.Length == 0) return new ZernioOutcome("ignored", "no conversation id");
        var key = SocialChannels.ThreadKey(SocialChannels.Dm, account.ExternalId, conversationId);
        var sender = ZernioJson.At(m, "sender");
        var name = FirstNonEmpty(ZernioJson.Str(sender, "name"), ZernioJson.Str(sender, "username"),
            ZernioJson.Str(body, "conversation", "participantName"), "Someone");
        var text = ZernioJson.Str(m, "text");
        foreach (var att in ZernioJson.Arr(m, "attachments"))
        {
            var url = ZernioJson.Str(att, "url");
            var kind = FirstNonEmpty(ZernioJson.Str(att, "type"), "file");
            text = (text.Length > 0 ? text + "\n" : "") + $"[{kind}]" + (url.Length > 0 ? " " + url : "");
        }

        var thread = _threads.GetOrCreateThread(ChatBranch.Domain.Channels.Social, key, name, account.Id);
        var st = Context(thread, SocialChannels.Dm, platform, account);
        st.ConversationId = conversationId;
        if (st.AuthorName.Length == 0)
        {
            st.AuthorId = ZernioJson.Str(sender, "id");
            st.AuthorName = name;
            st.AuthorHandle = ZernioJson.Str(sender, "username");
            st.AuthorAvatar = ZernioJson.Str(sender, "picture");
        }
        st.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var msg = _inbound.ProcessInbound(ChatBranch.Domain.Channels.Social, key, text, name, account.Id,
            FirstNonEmpty(ZernioJson.Str(m, "platformMessageId"), ZernioJson.Str(m, "id")),
            Time(ZernioJson.Str(m, "sentAt")),
            sender: new InboundSender(ZernioJson.Str(sender, "id"), name, ZernioJson.Str(sender, "phoneNumber")));
        Heal(account);
        return new ZernioOutcome("ok", "dm stored", msg.ThreadId, msg.Id);
    }

    /// <summary>message.sent: confirms our send, or records a reply written in the platform's own app.</summary>
    private async Task<ZernioOutcome> MessageSentAsync(JsonElement body, CancellationToken ct)
    {
        var m = ZernioJson.At(body, "message");
        var pmid = ZernioJson.Str(m, "platformMessageId");
        var ours = pmid.Length == 0 ? null : await OutboundAsync(pmid, ct);
        if (ours != null)
        {
            if (ours.Status is "sending" or "queued")
            {
                ours.Status = "sent";
                await _db.SaveChangesAsync(ct);
            }
            return new ZernioOutcome("ok", "send confirmed", ours.ThreadId, ours.Id);
        }
        // a send through Zernio's API that is not ours (another app on the same key) is not ours to show
        if (ZernioJson.Str(m, "sentVia") is "api" or "bulk-api") return new ZernioOutcome("ignored", "another API sender");
        var account = _accounts.Find(FirstNonEmpty(ZernioJson.Str(body, "account", "accountId"),
            ZernioJson.Str(body, "account", "id")));
        var conversationId = ZernioJson.Str(m, "conversationId");
        if (account == null || conversationId.Length == 0) return new ZernioOutcome("ignored", "unknown conversation");
        var key = SocialChannels.ThreadKey(SocialChannels.Dm, account.ExternalId, conversationId);
        var thread = await _db.Threads.FirstOrDefaultAsync(t => t.Channel == ChatBranch.Domain.Channels.Social
                                                                && t.Key == key, ct);
        if (thread == null) return new ZernioOutcome("ignored", "no such DM conversation here");
        var msg = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.ExternalClient,
            ZernioJson.Str(m, "text"), status: "sent", externalId: pmid, accountId: account.Id,
            providerTs: Time(ZernioJson.Str(m, "sentAt")));
        return new ZernioOutcome("ok", "reply from the platform app recorded", thread.Id, msg.Id);
    }

    private ZernioOutcome MessageFailed(JsonElement body)
    {
        var pmid = ZernioJson.Str(body, "message", "platformMessageId");
        var ours = pmid.Length == 0 ? null : OutboundAsync(pmid, default).GetAwaiter().GetResult();
        if (ours == null) return new ZernioOutcome("ignored", "not a message sent from here");
        var err = ZernioJson.At(body, "error");
        var detail = FirstNonEmpty(ZernioJson.Str(err, "message"), ZernioJson.Str(err, "title"), "delivery failed");
        ours.Status = "failed";
        ours.Error = Clip(FirstNonEmpty(ZernioJson.Str(err, "code") is { Length: > 0 } code ? $"{code}: {detail}" : "",
            detail), 300);
        var thread = _db.Threads.Find(ours.ThreadId);
        if (thread != null) thread.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
        return new ZernioOutcome("ok", "send marked failed", ours.ThreadId, ours.Id);
    }

    // ---- helpers ---------------------------------------------------------------------

    private Task<Message?> OutboundAsync(string externalId, CancellationToken ct) =>
        _db.Messages.Where(x => x.ExternalId == externalId && x.Direction == MessageDirection.Out
                                && x.Thread!.Channel == ChatBranch.Domain.Channels.Social)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);

    private Task<bool> KnownOutboundAsync(string externalId, CancellationToken ct) =>
        _db.Messages.AnyAsync(x => x.ExternalId == externalId && x.Direction == MessageDirection.Out
                                   && x.Thread!.Channel == ChatBranch.Domain.Channels.Social, ct);

    /// <summary>The thread's social context row (tracked), created on first sight.</summary>
    private SocialThread Context(Thread thread, string kind, string platform, ChannelAccount account)
    {
        var st = _db.SocialThreads.FirstOrDefault(s => s.ThreadId == thread.Id);
        if (st != null)
        {
            if (st.Platform.Length == 0) st.Platform = platform;
            return st;
        }
        st = new SocialThread
        {
            ThreadId = thread.Id,
            Kind = kind,
            Platform = platform.ToLowerInvariant(),
            ZernioAccountId = account.ExternalId,
            AccountUsername = SocialChannels.Cfg(account, SocialChannels.UsernameKey),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.SocialThreads.Add(st);
        return st;
    }

    /// <summary>Inbound traffic proves the account works.</summary>
    private void Heal(ChannelAccount account)
    {
        try
        {
            if (account.HealthStatus != Health.Healthy)
                AccountHealth.SetStatus(_db, account, Health.Healthy, "inbound event from Zernio", actor: "webhook");
            account.LastInboundAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
        catch { /* bookkeeping never fails a webhook */ }
    }

    private static DateTime? Time(string raw) =>
        DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                     | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
