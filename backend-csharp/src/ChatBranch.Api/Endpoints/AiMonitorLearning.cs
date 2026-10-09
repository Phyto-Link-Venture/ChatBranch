using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The AI-learning fields of an AI-monitor draft payload, kept out of
/// AiMonitorEndpoints: <c>my_feedback</c> (the caller's own 👍/👎 on the draft or
/// the message it became) and <c>grounding</c> (what the reply was based on —
/// knowledge items and lessons, from the generation's metadata).
/// </summary>
internal static class AiMonitorLearning
{
    /// <summary>draft id → {id, rating, reasons, note, correction} for the caller.</summary>
    public static async Task<Dictionary<int, object>> MyFeedbackAsync(AppDbContext db, List<AiDraft> drafts,
        ClaimsPrincipal? me)
    {
        var outv = new Dictionary<int, object>();
        try
        {
            var name = me?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(name) || drafts.Count == 0) return outv;
            var userId = await db.Users.AsNoTracking().Where(u => u.Username == name)
                .Select(u => (int?)u.Id).FirstOrDefaultAsync();
            if (userId == null) return outv;
            var draftIds = drafts.Select(d => d.Id).ToList();
            var messageIds = drafts.Where(d => d.SentMessageId != null).Select(d => d.SentMessageId!.Value).ToList();
            var rows = await db.AiFeedback.AsNoTracking()
                .Where(f => f.Kind == AiFeedbackKind.Explicit && f.UserId == userId
                            && ((f.DraftId != null && draftIds.Contains(f.DraftId.Value))
                                || (f.MessageId != null && messageIds.Contains(f.MessageId.Value))))
                .OrderByDescending(f => f.UpdatedAt).ToListAsync();
            foreach (var d in drafts)
            {
                var f = rows.FirstOrDefault(r => r.DraftId == d.Id)
                        ?? (d.SentMessageId is int mid ? rows.FirstOrDefault(r => r.MessageId == mid) : null);
                if (f != null)
                    outv[d.Id] = new { id = f.Id, rating = f.Rating, reasons = f.Reasons, note = f.Note, correction = f.Correction };
            }
        }
        catch { /* the monitor still lists drafts without the thumbs */ }
        return outv;
    }

    /// <summary>[{type, id, title, score}] — one entry per item/lesson, most relevant first.</summary>
    public static List<object> Grounding(Dictionary<string, object?> context)
    {
        try
        {
            return AiGate.Items(context, "grounding")
                .Select(g => new
                {
                    type = AiGate.Str(g, "type", "knowledge"),
                    id = (int)(AiGate.Num(g, "id") ?? 0),
                    title = AiGate.Str(g, "title", ""),
                    score = Math.Round(AiGate.Num(g, "score") ?? 0, 3),
                })
                .Where(g => g.id > 0)
                .GroupBy(g => (g.type, g.id))
                .Select(grp => grp.OrderByDescending(x => x.score).First())
                .OrderByDescending(g => g.score)
                .Select(g => (object)g)
                .ToList();
        }
        catch { return new List<object>(); }
    }
}
