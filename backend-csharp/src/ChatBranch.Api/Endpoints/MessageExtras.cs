using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The quote + reaction keys every message payload of the inbox carries (both
/// thread payloads — <c>/api/whatsapp/threads/{key}</c> and
/// <c>/api/messaging/threads/{channel}/{key}</c> — and the send answers):
///
///   * <c>reply_to</c> — the message this one quotes: <c>{pk, id, dir, source, text,
///     media_type}</c> (text clipped), or <c>{id, missing: true}</c> when the provider
///     named a message older than our history;
///   * <c>reactions</c> — <c>[{emoji, actor, at}]</c>, actor <c>contact</c> or
///     <c>user:&lt;username&gt;</c>;
///   * <c>recalled</c> — <c>{at, by: customer|business, kept}</c> when the message was
///     taken back on the channel ("delete for everyone" — <c>Channels/MessageRecalls.cs</c>);
///     kept = its words are still here, else they were erased. A quote of it carries
///     <c>recalled: true</c>;
///   * <c>edited_at</c> — when the shop edited this message on the channel
///     (WhatsApp's ~15-minute edit; <c>Channels/MessageEdits.cs</c>). The text is
///     already the NEW wording — the old one lives in the activity log;
///   * <c>email</c> — an inbound / imported mail's card: <c>{subject, from, to, cc, has_html}</c>
///     (the HTML itself: <c>GET /api/messaging/messages/{id}/email</c>); and on each of its
///     extra attachments (their own messages) <c>email_part_of</c> = the mail's pk, so the
///     inbox shows them inside the card.
///
/// Batched reads per page, never one per bubble.
/// </summary>
public static class MessageExtras
{
    private const int QuoteChars = 200;

    public static void Decorate(AppDbContext db, IReadOnlyList<Message> messages,
        IReadOnlyList<Dictionary<string, object?>> payloads)
    {
        if (messages.Count == 0 || messages.Count != payloads.Count) return;
        try
        {
            var quotedIds = messages.Where(m => m.ReplyToId != null).Select(m => m.ReplyToId!.Value).Distinct().ToList();
            var quoted = quotedIds.Count == 0
                ? new Dictionary<int, Message>()
                : db.Messages.AsNoTracking().Where(m => quotedIds.Contains(m.Id)).ToDictionary(m => m.Id);
            var reactions = MessageReactions.For(db, messages.Select(m => m.Id));
            var ids = messages.Select(m => m.Id).ToList();
            var mails = db.EmailHeaders.AsNoTracking()
                .Where(h => ids.Contains(h.MessageId) && h.FromLine != "")
                .Select(h => new { h.MessageId, h.Subject, h.FromLine, h.ToLine, h.CcLine, HasHtml = h.Html != "" })
                .ToList().GroupBy(h => h.MessageId).ToDictionary(g => g.Key, g => g.First());
            // "<mid>-att2" → the mail whose Message-ID is "<mid>"
            var mailByExternal = messages.Where(m => mails.ContainsKey(m.Id) && m.ExternalId.Length > 0)
                .GroupBy(m => m.ExternalId).ToDictionary(g => g.Key, g => g.First().Id);
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                var p = payloads[i];
                if (m.ReplyToId is int qid && quoted.TryGetValue(qid, out var q))
                    p["reply_to"] = new Dictionary<string, object?>
                    {
                        ["pk"] = q.Id,
                        ["id"] = q.ExternalId.Length > 0 ? q.ExternalId : $"db-{q.Id}",
                        ["dir"] = q.Direction,
                        ["source"] = q.Source,
                        ["text"] = q.Text.Length > QuoteChars ? q.Text[..QuoteChars] + "…" : q.Text,
                        ["media_type"] = q.MediaPath.Length > 0 ? (q.MediaType.Length > 0 ? q.MediaType : "document") : null,
                        ["recalled"] = q.RecalledAt != null ? true : null,
                    };
                else if (m.ReplyToExternalId.Length > 0)
                    p["reply_to"] = new Dictionary<string, object?> { ["id"] = m.ReplyToExternalId, ["missing"] = true };
                if (m.RecalledAt is DateTime recalledAt)
                    p["recalled"] = new Dictionary<string, object?>
                    {
                        ["at"] = DateTime.SpecifyKind(recalledAt, DateTimeKind.Utc).ToString("o"),
                        ["by"] = m.RecalledBy.Length > 0 ? m.RecalledBy : MessageRecalls.ByCustomer,
                        ["kept"] = m.Text.Length > 0 || m.MediaPath.Length > 0,
                    };
                // an edit the shop made: the text above is already the new wording,
                // so the bubble only needs to say that it changed (Channels/MessageEdits.cs)
                if (m.EditedAt is DateTime editedAt)
                    p["edited_at"] = DateTime.SpecifyKind(editedAt, DateTimeKind.Utc).ToString("o");
                if (mails.TryGetValue(m.Id, out var mail))
                    p["email"] = new Dictionary<string, object?>
                    {
                        ["subject"] = mail.Subject,
                        ["from"] = mail.FromLine,
                        ["to"] = mail.ToLine,
                        ["cc"] = mail.CcLine,
                        ["has_html"] = mail.HasHtml,
                    };
                else if (EmailPartOf(m.ExternalId) is { } parent && mailByExternal.TryGetValue(parent, out var parentPk))
                    p["email_part_of"] = parentPk;
                if (reactions.TryGetValue(m.Id, out var rs))
                    p["reactions"] = rs.Select(r => new Dictionary<string, object?>
                    {
                        ["emoji"] = r.Emoji,
                        ["actor"] = r.Actor,
                        ["at"] = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc).ToString("o"),
                    }).ToList();
            }
        }
        catch { /* a decoration never breaks the thread view */ }
    }

    /// <summary>"&lt;mid&gt;-att3" → "&lt;mid&gt;" (an email's extra attachment); else null.</summary>
    public static string? EmailPartOf(string externalId)
    {
        var i = externalId.LastIndexOf("-att", StringComparison.Ordinal);
        return i > 0 && externalId.StartsWith('<') && externalId[i - 1] == '>'
               && externalId.Length > i + 4 && externalId[(i + 4)..].All(char.IsAsciiDigit)
            ? externalId[..i] : null;
    }

    /// <summary>One message (a send answer).</summary>
    public static Dictionary<string, object?> Decorate(AppDbContext db, Message m, Dictionary<string, object?> payload)
    {
        Decorate(db, new[] { m }, new[] { payload });
        return payload;
    }
}
