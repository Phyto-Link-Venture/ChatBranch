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
///     <c>user:&lt;username&gt;</c>.
///
/// Two batched reads per page, never one per bubble.
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
                    };
                else if (m.ReplyToExternalId.Length > 0)
                    p["reply_to"] = new Dictionary<string, object?> { ["id"] = m.ReplyToExternalId, ["missing"] = true };
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

    /// <summary>One message (a send answer).</summary>
    public static Dictionary<string, object?> Decorate(AppDbContext db, Message m, Dictionary<string, object?> payload)
    {
        Decorate(db, new[] { m }, new[] { payload });
        return payload;
    }
}
