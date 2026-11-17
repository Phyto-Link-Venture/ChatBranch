using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Zernio;

/// <summary>What handling one webhook event did (for the answer and the tests).</summary>
public sealed record ZernioOutcome(string Result, string Detail = "", int? ThreadId = null, int? MessageId = null);

/// <summary>
/// Turns one Zernio webhook event into inbox / account state. Payload shapes follow
/// Zernio's webhook reference: <c>message.received</c> / <c>message.sent</c> /
/// <c>message.failed</c> / <c>message.delivered</c> / <c>message.read</c>
/// (InboxWebhookMessage + conversation + account), <c>account.connected</c> /
/// <c>account.disconnected</c>. Social-media events (comments, reviews, posts) are
/// out of ChatBranch's scope: answered 200 and ignored.
/// The signature check and the event-id dedupe happen before this (the endpoint).
/// </summary>
public sealed class ZernioWebhook
{
    /// <summary>What the registered Zernio webhook subscribes to.</summary>
    public static readonly string[] Events =
    {
        "account.connected", "account.disconnected",
        "message.received", "message.sent", "message.failed", "message.delivered", "message.read",
    };

    private readonly AppDbContext _db;
    private readonly ThreadStore _threads;
    private readonly InboundProcessor _inbound;
    private readonly ZernioAccounts _accounts;
    /// <summary>Inbound attachments of native-channel conversations, fetched off the ack.</summary>
    private readonly MediaFetcher? _fetcher;

    public ZernioWebhook(AppDbContext db, ThreadStore threads, InboundProcessor inbound, ZernioAccounts accounts,
        MediaFetcher? fetcher = null)
    {
        _fetcher = fetcher;
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
            case "message.received": return await MessageReceivedAsync(body, ct);
            case "message.sent": return await MessageSentAsync(body, ct);
            case "message.failed": return MessageFailed(body);
            case "message.delivered": return MessageStatus(body, "delivered");
            case "message.read": return MessageStatus(body, "read");
            default:
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
            var (_, _, _, error) = await _accounts.SyncAsync(ct: ct);
            var mirrored = _accounts.Find(ZernioJson.Str(acct, "accountId"));
            return mirrored != null
                ? new ZernioOutcome("ok", "account synced")
                : new ZernioOutcome("ignored", error.Length > 0 ? error : "the account's profile is not ChatBranch's");
        }
        var row = _accounts.Upsert(acct, entityId, profileId);
        return row == null
            ? new ZernioOutcome("ignored", "not a messaging account (or not wanted as an inbox here)")
            : new ZernioOutcome("ok", $"account {row.Name} connected");
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
        await _accounts.SyncAsync(ct: ct);
        return _accounts.Find(id);
    }

    // ---- messages --------------------------------------------------------------------

    private async Task<ZernioOutcome> MessageReceivedAsync(JsonElement body, CancellationToken ct)
    {
        var account = await AccountForAsync(body, ct);
        if (account == null) return new ZernioOutcome("ignored", "unknown account (or not a messaging inbox here)");
        var m = ZernioJson.At(body, "message");
        if (ZernioJson.Str(m, "direction") == "outgoing") return await MessageSentAsync(body, ct);
        return await NativeInboundAsync(account, body, ct);
    }

    /// <summary>
    /// A DM on a native channel carried by a Zernio account: the native thread key (phone digits,
    /// PSID, chat id), Zernio's conversation id remembered for replies, the message through the
    /// SAME inbound pipeline as a native connection (contact link by phone, windows, STOP, flows,
    /// automations, the AI; a group goes the group-chat way), attachments fetched off the ack.
    /// </summary>
    private async Task<ZernioOutcome> NativeInboundAsync(ChannelAccount row, JsonElement body, CancellationToken ct)
    {
        var m = ZernioJson.At(body, "message");
        var conv = ZernioJson.At(body, "conversation");
        var key = ZernioNative.Key(row.Channel, m, conv);
        if (key.Length == 0) return new ZernioOutcome("ignored", "the message names no sender");
        var pmid = FirstNonEmpty(ZernioJson.Str(m, "platformMessageId"), ZernioJson.Str(m, "id"));
        // a redelivery Zernio gave a new event id: the message is already here
        if (pmid.Length > 0 && await _db.Messages.AnyAsync(x => x.ExternalId == pmid && x.Direction == MessageDirection.In
                                                                && x.Thread!.Channel == row.Channel, ct))
            return new ZernioOutcome("duplicate", "message already stored");

        var conversationId = FirstNonEmpty(ZernioJson.Str(m, "conversationId"), ZernioJson.Str(conv, "id"));
        var sender = ZernioJson.At(m, "sender");
        ZernioNative.Remember(_db, row, key, conversationId,
            FirstNonEmpty(ZernioJson.Str(sender, "id"), ZernioJson.Str(conv, "participantId")));

        var group = GroupChats.IsGroup(row.Channel, key);
        var who = FirstNonEmpty(ZernioJson.Str(sender, "name"), ZernioJson.Str(sender, "username"),
            ZernioJson.Str(sender, "whatsappUsername"), ZernioJson.Str(sender, "phoneNumber"));
        var name = group
            ? FirstNonEmpty(ZernioJson.Str(conv, "participantName"), GroupChats.DefaultName(row.Channel))
            : FirstNonEmpty(who, ZernioJson.Str(conv, "participantName"), key);

        var text = ZernioJson.Str(m, "text");
        var atts = ZernioJson.Arr(m, "attachments").ToList();
        string Kind(JsonElement a) => ZernioJson.Str(a, "type") switch
        {
            "image" or "sticker" => "image",
            "video" => "video",
            "audio" => "audio",
            _ => "document",
        };
        if (text.Length == 0 && atts.Count > 0) text = $"[{Kind(atts[0])}]";
        // further attachments stay readable as links (their CDN links may expire)
        foreach (var extra in atts.Skip(1))
            text += $"\n[{Kind(extra)}]" + (ZernioJson.Str(extra, "url") is { Length: > 0 } u ? " " + u : "");
        if (text.Length == 0) text = "[message]";

        var meta = ZernioJson.At(body, "metadata");
        var quoted = FirstNonEmpty(ZernioJson.Str(meta, "quotedMessage", "platformMessageId"),
            ZernioJson.Str(meta, "quotedMessageId"));
        var at = Time(ZernioJson.Str(m, "sentAt"));
        Message msg;
        if (ZernioJson.Bool(meta, "standby"))
        {
            // another app answers this conversation (Meta's handover): shown, never answered by us
            var t = _threads.GetOrCreateThread(row.Channel, key, name, row.Id);
            msg = _threads.AppendMessage(t, MessageDirection.In, MessageSource.Customer, text, status: "received",
                externalId: pmid, accountId: row.Id, providerTs: at, replyToExternalId: quoted);
        }
        else
        {
            var author = group
                ? new InboundSender(FirstNonEmpty(ZernioJson.Str(sender, "id"), "unknown"), FirstNonEmpty(who, "Someone"),
                    WhatsAppProviders.Digits(ZernioJson.Str(sender, "phoneNumber")))
                : null;
            var username = ZernioKeys.Cfg(row, ZernioKeys.UsernameKey).TrimStart('@');
            var addressed = group && (quoted.Length > 0
                                      || (username.Length > 0 && text.Contains("@" + username, StringComparison.OrdinalIgnoreCase))
                                      || text.StartsWith('/'));
            msg = _inbound.ProcessInbound(row.Channel, key, text, name, row.Id, pmid, at, quoted, author, addressed);
        }
        if (atts.Count > 0 && _fetcher != null && ZernioJson.Str(atts[0], "url") is { Length: > 0 } url)
            _fetcher.FetchZernio(msg.Id, url, Kind(atts[0]));
        Heal(row);
        return new ZernioOutcome("ok", $"{row.Channel} message stored", msg.ThreadId, msg.Id);
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
        var zid = FirstNonEmpty(ZernioJson.Str(body, "account", "accountId"), ZernioJson.Str(body, "account", "id"));
        var conversationId = ZernioJson.Str(m, "conversationId");
        // a reply written in the platform's own app: the person's outbound message
        if (_accounts.Find(zid) is not { } row || conversationId.Length == 0)
            return new ZernioOutcome("ignored", "unknown conversation");
        var map = await _db.ZernioConversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AccountId == row.Id && c.ConversationId == conversationId, ct);
        var thread = map == null ? null : await _db.Threads.FirstOrDefaultAsync(t => t.Channel == row.Channel
                                                                                    && t.Key == map.Key
                                                                                    && t.AccountId == row.Id, ct);
        if (thread == null) return new ZernioOutcome("ignored", "no such conversation here");
        var own = _threads.AppendMessage(thread, MessageDirection.Out, MessageSource.ExternalClient,
            ZernioJson.Str(m, "text"), status: "sent", externalId: pmid, accountId: row.Id,
            providerTs: Time(ZernioJson.Str(m, "sentAt")));
        return new ZernioOutcome("ok", "reply from the platform app recorded", thread.Id, own.Id);
    }

    private ZernioOutcome MessageFailed(JsonElement body)
    {
        var pmid = ZernioJson.Str(body, "message", "platformMessageId");
        var ours = pmid.Length == 0 ? null : OutboundAsync(pmid, default).GetAwaiter().GetResult();
        if (ours == null) return new ZernioOutcome("ignored", "not a message sent from here");
        var err = ZernioJson.At(body, "error");
        var detail = FirstNonEmpty(ZernioJson.Str(err, "message"), ZernioJson.Str(err, "title"), "delivery failed");
        // WhatsApp via Zernio: Meta's codes mean what they mean natively (131047 closes the window)
        if (_db.Threads.AsNoTracking().Where(t => t.Id == ours.ThreadId).Select(t => t.Channel).FirstOrDefault()
            == ChatBranch.Domain.Channels.WhatsApp)
        {
            int? metaCode = int.TryParse(ZernioJson.Str(err, "code"), out var mc) ? mc : null;
            MetaWebhook.ApplyStatus(_db, new CloudStatus("", pmid, "failed", "", Time(ZernioJson.Str(body, "statusAt")),
                metaCode, detail));
            return new ZernioOutcome("ok", "send marked failed", ours.ThreadId, ours.Id);
        }
        ours.Status = "failed";
        ours.Error = Clip(FirstNonEmpty(ZernioJson.Str(err, "code") is { Length: > 0 } code ? $"{code}: {detail}" : "",
            detail), 300);
        var thread = _db.Threads.Find(ours.ThreadId);
        if (thread != null) thread.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
        return new ZernioOutcome("ok", "send marked failed", ours.ThreadId, ours.Id);
    }

    private static int Rank(string status) => status switch
    {
        "sending" or "queued" => 0,
        "sent" => 1,
        "delivered" => 2,
        "read" => 3,
        "failed" => 4,
        _ => 0,
    };

    /// <summary>message.delivered / message.read for a message sent from here: forward-only.</summary>
    private ZernioOutcome MessageStatus(JsonElement body, string status)
    {
        var pmid = ZernioJson.Str(body, "message", "platformMessageId");
        var ours = pmid.Length == 0 ? null : OutboundAsync(pmid, default).GetAwaiter().GetResult();
        if (ours == null) return new ZernioOutcome("ignored", "not a message sent from here");
        var channel = _db.Threads.AsNoTracking().Where(t => t.Id == ours.ThreadId).Select(t => t.Channel).FirstOrDefault();
        if (channel == ChatBranch.Domain.Channels.WhatsApp)
        {
            MetaWebhook.ApplyStatus(_db, new CloudStatus("", pmid, status, "", Time(ZernioJson.Str(body, "statusAt")), null, ""));
            return new ZernioOutcome("ok", $"marked {status}", ours.ThreadId, ours.Id);
        }
        if (Rank(ours.Status) >= Rank(status) && ours.Status is not ("sending" or "queued"))
            return new ZernioOutcome("ok", "status unchanged", ours.ThreadId, ours.Id);
        ours.Status = status;
        var thread = _db.Threads.Find(ours.ThreadId);
        if (thread != null) thread.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
        return new ZernioOutcome("ok", $"marked {status}", ours.ThreadId, ours.Id);
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>Our outbound message with that platform id on a Zernio-carried inbox.</summary>
    private Task<Message?> OutboundAsync(string externalId, CancellationToken ct) =>
        _db.Messages.Where(x => x.ExternalId == externalId && x.Direction == MessageDirection.Out
                                && x.Account!.Provider == Providers.Zernio)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);

    /// <summary>Inbound traffic proves the account works.</summary>
    private void Heal(ChannelAccount account)
    {
        try
        {
            if (account.HealthStatus != Health.Healthy)
                AccountHealth.SetStatus(_db, account, Health.Healthy, "inbound event (managed connection)", actor: "webhook");
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

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
