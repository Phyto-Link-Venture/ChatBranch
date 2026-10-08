using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>One inbound WhatsApp Cloud message (text, media, interactive reply…).</summary>
public sealed record CloudInbound(string PhoneNumberId, string From, string? Name, string Wamid, string Type,
    string Text, DateTime? Timestamp, string MediaId = "", string Mime = "", string Filename = "");

/// <summary>One delivery receipt from <c>value.statuses[]</c>.</summary>
public sealed record CloudStatus(string PhoneNumberId, string Wamid, string Status, string RecipientId,
    DateTime? Timestamp, int? ErrorCode, string ErrorTitle);

public sealed record MessagingAttachment(string Type, string Url);

/// <summary>One Messenger / Instagram webhook event (<c>entry[].messaging[]</c>).</summary>
public sealed record MessagingEvent(string Channel, string PageId, string SenderId, string Kind, string Mid,
    string Text, DateTime? Timestamp, List<MessagingAttachment> Attachments, List<string> Mids, DateTime? Watermark);

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
            {
                var type = MetaGraph.Str(msg, "type");
                var from = MetaGraph.Str(msg, "from");
                var wamid = MetaGraph.Str(msg, "id");
                if (from.Length == 0) continue;
                var ts = UnixSeconds(msg, "timestamp");
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
                        text = "[contact card]";
                        break;
                    case "reaction":
                    case "system":
                    case "ephemeral":
                        continue; // not a customer message (and must not wake the AI)
                    default:
                        if (CloudMediaTypes.Contains(type) && msg.TryGetProperty(type, out var media))
                        {
                            mediaId = MetaGraph.Str(media, "id");
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
                if (text.Length == 0) continue;
                outv.Add(new CloudInbound(pnid, from, name, wamid, type, text, ts, mediaId, mime, filename));
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
                    outv.Add(new MessagingEvent(channel, pageId, sender, "message", MetaGraph.Str(m, "mid"), text, ts,
                        attachments, new(), null));
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
        var thread = db.Threads.FirstOrDefault(t => t.Channel == ev.Channel && t.Key == ev.SenderId);
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
