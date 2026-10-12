using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>One inbound WhatsApp Cloud message (text, media, interactive reply…).</summary>
public sealed record CloudInbound(string PhoneNumberId, string From, string? Name, string Wamid, string Type,
    string Text, DateTime? Timestamp, string MediaId = "", string Mime = "", string Filename = "",
    string ReplyTo = "");

/// <summary>One inbound WhatsApp Cloud reaction: the customer reacted to the message
/// <see cref="TargetWamid"/> (an empty <see cref="Emoji"/> = they removed it).</summary>
public sealed record CloudReaction(string PhoneNumberId, string From, string TargetWamid, string Emoji);

/// <summary>One delivery receipt from <c>value.statuses[]</c>.</summary>
public sealed record CloudStatus(string PhoneNumberId, string Wamid, string Status, string RecipientId,
    DateTime? Timestamp, int? ErrorCode, string ErrorTitle);

public sealed record MessagingAttachment(string Type, string Url);

/// <summary>One Messenger / Instagram webhook event (<c>entry[].messaging[]</c>).</summary>
public sealed record MessagingEvent(string Channel, string PageId, string SenderId, string Kind, string Mid,
    string Text, DateTime? Timestamp, List<MessagingAttachment> Attachments, List<string> Mids, DateTime? Watermark)
{
    /// <summary>The mid this message replies to (<c>message.reply_to.mid</c>; "" = not a reply).</summary>
    public string ReplyTo { get; init; } = "";
}

/// <summary>
/// Parsing for Meta's webhooks (port of the whatsapp + messaging views' payload
/// handling) and the delivery-status writer. Pure functions over the JSON so the
/// endpoints stay thin and the shapes are unit-testable; nothing here throws on
/// a malformed payload (Meta must always get its 200).
/// </summary>
public static class MetaWebhook
{
    /// <summary>Message types the Cloud API delivers as downloadable media.</summary>
    private static readonly HashSet<string> CloudMediaTypes = new() { "image", "video", "audio", "document", "sticker" };

    private static IEnumerable<JsonElement> Changes(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entry", out var entries)
            || entries.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("changes", out var changes)
                || changes.ValueKind != JsonValueKind.Array) continue;
            foreach (var ch in changes.EnumerateArray())
                if (ch.ValueKind == JsonValueKind.Object && ch.TryGetProperty("value", out var val)
                    && val.ValueKind == JsonValueKind.Object)
                    yield return val;
        }
    }

    private static string PhoneNumberId(JsonElement value) =>
        value.TryGetProperty("metadata", out var meta) ? MetaGraph.Str(meta, "phone_number_id") : "";

    /// <summary>Every phone_number_id a Cloud payload addresses (a batch can span numbers).</summary>
    public static List<string> PhoneNumberIds(JsonElement root) =>
        Changes(root).Select(PhoneNumberId).Where(p => p.Length > 0).Distinct().ToList();

    private static DateTime? UnixSeconds(JsonElement e, string key)
    {
        var raw = MetaGraph.Str(e, key);
        return long.TryParse(raw, out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : null;
    }

    /// <summary>Inbound messages. Media becomes a text placeholder plus the media id to download.</summary>
    public static List<CloudInbound> CloudMessages(JsonElement root)
    {
        var outv = new List<CloudInbound>();
        foreach (var val in Changes(root))
        {
            var pnid = PhoneNumberId(val);
            string? name = null;
            if (val.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array
                && contacts.GetArrayLength() > 0 && contacts[0].TryGetProperty("profile", out var prof))
                name = MetaGraph.Str(prof, "name") is { Length: > 0 } n ? n : null;
            if (!val.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var msg in messages.EnumerateArray())
                if (ParseMessage(msg, pnid, name) is { } one) outv.Add(one);
        }
        return outv;
    }

    /// <summary>
    /// One Meta-shaped message object (<c>messages[]</c> of a Cloud webhook — YCloud's
    /// <c>whatsappInboundMessage</c> has the same per-type shape) → our inbound record;
    /// null when it is not a customer message (a reaction, a system notice) or is empty.
    /// The overrides carry what a BSP names differently (YCloud's <c>from</c> is "+…",
    /// its time is <c>sendTime</c>, its media carry a download <c>link</c>).
    /// </summary>
    public static CloudInbound? ParseMessage(JsonElement msg, string pnid, string? name, string? fromOverride = null,
        string? idOverride = null, DateTime? tsOverride = null, bool mediaByLink = false)
    {
        var type = MetaGraph.Str(msg, "type");
        var from = fromOverride ?? MetaGraph.Str(msg, "from");
        var wamid = idOverride ?? MetaGraph.Str(msg, "id");
        if (from.Length == 0) return null;
        var ts = tsOverride ?? UnixSeconds(msg, "timestamp");
        string text = "", mediaId = "", mime = "", filename = "";
        switch (type)
        {
            case "text":
                text = msg.TryGetProperty("text", out var tx) ? MetaGraph.Str(tx, "body") : "";
                break;
            case "button": // quick-reply tap on a template
                text = msg.TryGetProperty("button", out var btn) ? MetaGraph.Str(btn, "text") : "";
                break;
            case "interactive":
                if (msg.TryGetProperty("interactive", out var ia))
                    foreach (var k in new[] { "button_reply", "list_reply", "nfm_reply" })
                        if (ia.TryGetProperty(k, out var r))
                        {
                            text = MetaGraph.Str(r, "title") is { Length: > 0 } title ? title : MetaGraph.Str(r, "body");
                            break;
                        }
                break;
            case "location":
                if (msg.TryGetProperty("location", out var loc))
                {
                    var label = string.Join(", ", new[] { MetaGraph.Str(loc, "name"), MetaGraph.Str(loc, "address") }
                        .Where(s => s.Length > 0));
                    text = $"[location] {MetaGraph.Str(loc, "latitude")},{MetaGraph.Str(loc, "longitude")}"
                           + (label.Length > 0 ? $" {label}" : "");
                }
                break;
            case "contacts":
                // the first shared card as "[contact] Name · +phone" (the bubble renders a card)
                text = "[contact card]";
                if (msg.TryGetProperty("contacts", out var cards) && cards.ValueKind == JsonValueKind.Array
                    && cards.GetArrayLength() > 0 && cards[0].ValueKind == JsonValueKind.Object)
                {
                    var card = cards[0];
                    var cardName = card.TryGetProperty("name", out var nm) ? MetaGraph.Str(nm, "formatted_name") : "";
                    var cardPhone = card.TryGetProperty("phones", out var ph) && ph.ValueKind == JsonValueKind.Array
                                    && ph.GetArrayLength() > 0 ? MetaGraph.Str(ph[0], "phone") : "";
                    text = MessageCards.Contact(cardName, cardPhone);
                }
                break;
            case "reaction": // CloudReactions — a reaction is never a bubble
            case "system":
            case "ephemeral":
                return null; // not a customer message (and must not wake the AI)
            default:
                if (CloudMediaTypes.Contains(type) && msg.TryGetProperty(type, out var media))
                {
                    mediaId = mediaByLink && MetaGraph.Str(media, "link") is { Length: > 0 } link
                        ? link : MetaGraph.Str(media, "id");
                    mime = MetaGraph.Str(media, "mime_type");
                    filename = MetaGraph.Str(media, "filename");
                    var caption = MetaGraph.Str(media, "caption");
                    // stickers are webp images in the bubble
                    text = caption.Length > 0 ? caption : $"[{(type == "sticker" ? "sticker" : type)}]";
                }
                else
                {
                    text = $"[{(type.Length > 0 ? type : "message")}]";
                }
                break;
        }
        if (text.Length == 0) return null;
        // a quoted reply carries the quoted wamid in context.id (a forward carries none)
        var replyTo = msg.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.Object
            ? MetaGraph.Str(ctx, "id") : "";
        return new CloudInbound(pnid, from, name, wamid, type, text, ts, mediaId, mime, filename, replyTo);
    }

    /// <summary>Inbound reactions (<c>type: reaction</c>, <c>reaction.message_id</c> + <c>emoji</c>).</summary>
    public static List<CloudReaction> CloudReactions(JsonElement root)
    {
        var outv = new List<CloudReaction>();
        foreach (var val in Changes(root))
        {
            if (!val.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                continue;
            var pnid = PhoneNumberId(val);
            foreach (var msg in messages.EnumerateArray())
            {
                if (MetaGraph.Str(msg, "type") != "reaction" || !msg.TryGetProperty("reaction", out var r)
                    || r.ValueKind != JsonValueKind.Object) continue;
                var from = MetaGraph.Str(msg, "from");
                var target = MetaGraph.Str(r, "message_id");
                if (from.Length == 0 || target.Length == 0) continue;
                outv.Add(new CloudReaction(pnid, from, target, MetaGraph.Str(r, "emoji")));
            }
        }
        return outv;
    }

    /// <summary>Delivery receipts (<c>value.statuses[]</c>).</summary>
    public static List<CloudStatus> CloudStatuses(JsonElement root)
    {
        var outv = new List<CloudStatus>();
        foreach (var val in Changes(root))
        {
            if (!val.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
                continue;
            var pnid = PhoneNumberId(val);
            foreach (var s in statuses.EnumerateArray())
            {
                int? code = null;
                var title = "";
                if (s.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array
                    && errs.GetArrayLength() > 0)
                {
                    var e = errs[0];
                    if (e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                        && c.TryGetInt32(out var n)) code = n;
                    title = MetaGraph.Str(e, "title") is { Length: > 0 } t ? t : MetaGraph.Str(e, "message");
                    if (e.TryGetProperty("error_data", out var d) && MetaGraph.Str(d, "details") is { Length: > 0 } det)
                        title = title.Length > 0 ? $"{title} — {det}" : det;
                }
                var wamid = MetaGraph.Str(s, "id");
                var status = MetaGraph.Str(s, "status").ToLowerInvariant();
                if (wamid.Length == 0 || status.Length == 0) continue;
                outv.Add(new CloudStatus(pnid, wamid, status, MetaGraph.Str(s, "recipient_id"),
                    UnixSeconds(s, "timestamp"), code, title));
            }
        }
        return outv;
    }

    private static int Rank(string status) => status switch
    {
        "sent" => 1,
        "delivered" => 2,
        "read" => 3,
        "failed" => 4,
        _ => 0,
    };

    /// <summary>
    /// Apply one receipt to the outbound message carrying that wamid: statuses
    /// only move FORWARD (Meta's webhooks arrive out of order), the thread's
    /// updated_at is bumped so the inbox SSE refreshes, a failure records its
    /// error, 131047 closes the window (the composer flips to template mode), and
    /// an ACCOUNT-level code — never a per-recipient one — moves the sending
    /// account's health through <see cref="AccountHealth.SetStatus"/>.
    /// Returns true when a message changed. Saves.
    /// </summary>
    public static bool ApplyStatus(AppDbContext db, CloudStatus s, ChannelAccount? receivingAccount = null)
    {
        var next = Rank(s.Status);
        if (next == 0) return false; // "deleted", "warning" … not a delivery state we track
        var msg = db.Messages.Include(m => m.Thread).Include(m => m.Account)
            .FirstOrDefault(m => m.ExternalId == s.Wamid && m.Direction == MessageDirection.Out
                                 && m.Thread!.Channel == ChatBranch.Domain.Channels.WhatsApp);
        if (msg?.Thread == null) return false;
        if (Rank(msg.Status) >= next) return false;
        msg.Status = s.Status;
        var thread = msg.Thread;
        var account = msg.Account ?? receivingAccount;
        if (s.Status == "failed")
        {
            msg.Error = Cap(s.ErrorCode is int code ? $"{code}: {s.ErrorTitle}".TrimEnd(' ', ':') : s.ErrorTitle, 300);
            if (s.ErrorCode == MetaErrors.WindowClosed)
                // Meta's window is per OUR number: a thread that has since moved to
                // another number keeps that number's own replies (its window is real)
                WhatsAppWindow.Close(db, thread,
                    exceptAccountId: thread.AccountId != null && thread.AccountId != msg.AccountId ? thread.AccountId : null,
                    save: false);
            MetaErrors.ApplyToAccount(db, account, s.ErrorCode, null, s.ErrorTitle, actor: "webhook");
        }
        else if (s.Status is "delivered" or "read")
        {
            MetaErrors.MarkHealthy(db, account, $"message {s.Status}", actor: "webhook");
        }
        thread.UpdatedAt = DateTime.UtcNow; // the SSE change token
        db.SaveChanges();
        return true;
    }

    // ---- Messenger / Instagram ----------------------------------------------------

    private static DateTime? UnixMillis(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var ms))
            return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    }

    /// <summary>
    /// <c>object: page</c> (Messenger) or <c>object: instagram</c> events: customer
    /// messages and postbacks (echoes of our own sends are skipped), plus
    /// delivery / read receipts. The thread key is the sender's PSID / IGSID and
    /// the entry id is the page / Instagram account the event belongs to.
    /// </summary>
    public static List<MessagingEvent> MessagingEvents(JsonElement root)
    {
        var outv = new List<MessagingEvent>();
        if (root.ValueKind != JsonValueKind.Object) return outv;
        var obj = MetaGraph.Str(root, "object");
        var channel = obj switch
        {
            "page" => ChatBranch.Domain.Channels.Facebook,
            "instagram" => ChatBranch.Domain.Channels.Instagram,
            _ => "",
        };
        if (channel.Length == 0 || !root.TryGetProperty("entry", out var entries)
            || entries.ValueKind != JsonValueKind.Array) return outv;
        foreach (var entry in entries.EnumerateArray())
        {
            var pageId = MetaGraph.Str(entry, "id");
            if (!entry.TryGetProperty("messaging", out var events) || events.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var ev in events.EnumerateArray())
            {
                var sender = ev.TryGetProperty("sender", out var s) ? MetaGraph.Str(s, "id") : "";
                var recipient = ev.TryGetProperty("recipient", out var r) ? MetaGraph.Str(r, "id") : "";
                var ts = UnixMillis(ev, "timestamp");
                if (ev.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    if (m.TryGetProperty("is_echo", out var echo) && echo.ValueKind == JsonValueKind.True) continue;
                    if (sender.Length == 0 || sender == pageId) continue;
                    // the customer unsent it ("is_deleted": true, Instagram — Messenger the same shape)
                    if (m.TryGetProperty("is_deleted", out var gone) && gone.ValueKind == JsonValueKind.True)
                    {
                        if (MetaGraph.Str(m, "mid") is { Length: > 0 } goneMid)
                            outv.Add(new MessagingEvent(channel, pageId, sender, "deleted", goneMid, "", ts, new(), new(), null));
                        continue;
                    }
                    var attachments = new List<MessagingAttachment>();
                    if (m.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
                        foreach (var a in atts.EnumerateArray())
                        {
                            var url = a.TryGetProperty("payload", out var p) ? MetaGraph.Str(p, "url") : "";
                            attachments.Add(new MessagingAttachment(MetaGraph.Str(a, "type"), url));
                        }
                    var text = MetaGraph.Str(m, "text");
                    if (text.Length == 0 && attachments.Count > 0)
                        text = $"[{AttachmentKind(attachments[0].Type)}]";
                    if (text.Length == 0) continue;
                    var replyTo = m.TryGetProperty("reply_to", out var rt) ? MetaGraph.Str(rt, "mid") : "";
                    outv.Add(new MessagingEvent(channel, pageId, sender, "message", MetaGraph.Str(m, "mid"), text, ts,
                        attachments, new(), null) { ReplyTo = replyTo });
                }
                else if (ev.TryGetProperty("postback", out var pb) && pb.ValueKind == JsonValueKind.Object)
                {
                    if (sender.Length == 0 || sender == pageId) continue;
                    var text = MetaGraph.Str(pb, "title") is { Length: > 0 } t ? t : MetaGraph.Str(pb, "payload");
                    if (text.Length == 0) continue;
                    var mid = MetaGraph.Str(pb, "mid");
                    outv.Add(new MessagingEvent(channel, pageId, sender, "message",
                        mid.Length > 0 ? mid : $"postback-{sender}-{ts?.Ticks}", text, ts, new(), new(), null));
                }
                else if (ev.TryGetProperty("delivery", out var dl) && dl.ValueKind == JsonValueKind.Object)
                {
                    var mids = dl.TryGetProperty("mids", out var ms) && ms.ValueKind == JsonValueKind.Array
                        ? ms.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                        : new List<string>();
                    // the customer is the delivery's sender; our page is the recipient
                    outv.Add(new MessagingEvent(channel, pageId, sender, "delivery", "", "", ts, new(), mids,
                        UnixMillis(dl, "watermark")));
                }
                else if (ev.TryGetProperty("read", out var rd) && rd.ValueKind == JsonValueKind.Object)
                {
                    var mids = MetaGraph.Str(rd, "mid") is { Length: > 0 } one ? new List<string> { one } : new();
                    outv.Add(new MessagingEvent(channel, pageId, sender, "read", "", "", ts, new(), mids,
                        UnixMillis(rd, "watermark")));
                }
                _ = recipient;
            }
        }
        return outv;
    }

    /// <summary>Messenger attachment type → our bubble type.</summary>
    public static string AttachmentKind(string type) => type switch
    {
        "image" => "image",
        "video" => "video",
        "audio" => "audio",
        "file" => "document",
        _ => type.Length > 0 ? type : "attachment",
    };

    /// <summary>
    /// Messenger / Instagram receipts: <c>delivery</c> lists mids (or a watermark),
    /// <c>read</c> a watermark (Messenger) or a mid (Instagram) — every outbound
    /// message of that conversation up to it moves forward. Saves when something changed.
    /// </summary>
    public static int ApplyMessagingReceipt(AppDbContext db, MessagingEvent ev)
    {
        var target = ev.Kind == "read" ? "read" : "delivered";
        // the page's own conversation (its company's), never another company's chat with that sender
        var account = db.ChannelAccounts.AsNoTracking()
            .FirstOrDefault(a => a.Channel == ev.Channel && a.ExternalId == ev.PageId);
        var thread = new Stores.ThreadStore(db, new ScopingService(db)).FindForAccount(ev.Channel, ev.SenderId, account?.Id);
        if (thread == null) return 0;
        var q = db.Messages.Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.Out
                                       && m.ExternalId != "");
        List<Message> rows;
        if (ev.Mids.Count > 0)
        {
            var mids = ev.Mids;
            rows = q.Where(m => mids.Contains(m.ExternalId)).ToList();
            if (ev.Kind == "read" && rows.Count > 0)
            {
                var upTo = rows.Max(m => m.CreatedAt);
                rows = q.Where(m => m.CreatedAt <= upTo).ToList();
            }
        }
        else if (ev.Watermark is DateTime wm)
            rows = q.Where(m => m.CreatedAt <= wm).ToList();
        else return 0;
        var changed = 0;
        foreach (var m in rows)
            if (Rank(m.Status) < Rank(target)) { m.Status = target; changed++; }
        if (changed > 0)
        {
            thread.UpdatedAt = DateTime.UtcNow;
            db.SaveChanges();
        }
        return changed;
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>
/// The WhatsApp 24h customer-service window helpers (port of
/// <c>store.close_window</c>).
/// </summary>
public static class WhatsAppWindow
{
    public const int Hours = 24;

    /// <summary>
    /// Cap inbound timestamps so the window reads CLOSED and the composer flips to
    /// template mode. <paramref name="exceptAccountId"/> keeps one number's own
    /// inbounds intact (an account switch / a thread that moved on: that number's
    /// window is genuinely open). Bumps the thread's updated_at (SSE).
    /// </summary>
    public static int Close(AppDbContext db, Thread thread, int? exceptAccountId, bool save = true)
    {
        var cutoff = DateTime.UtcNow.AddHours(-Hours - 1);
        var rows = db.Messages
            .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In
                        && m.CreatedAt > cutoff
                        && (exceptAccountId == null || m.AccountId != exceptAccountId))
            .ToList();
        foreach (var m in rows) m.CreatedAt = cutoff;
        if (rows.Count > 0) thread.UpdatedAt = DateTime.UtcNow;
        if (save && rows.Count > 0) db.SaveChanges();
        return rows.Count;
    }
}
