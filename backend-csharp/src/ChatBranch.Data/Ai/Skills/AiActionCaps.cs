using System.Globalization;
using System.Text.Json;
using ChatBranch.Data.Audit;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai.Skills;

/// <summary>
/// Per-conversation caps on the AI's SIDE-EFFECT tools over a rolling 24 hours, so a
/// scripted "customer" can't farm invoices, payment links or bookings out of the chat
/// AI. Counted from what was really made for the conversation (crm_document /
/// crm_booking rows the AI created, <c>ai.payment_link</c> audit rows) — preview runs
/// (the AI playground) make nothing and are never counted or capped. A capped call
/// answers the model <c>{ok:false, capped:true}</c> with advice to hand over, and writes
/// an <c>ai.tool_capped</c> audit row. Env knobs (0 = no cap):
/// <c>AI_CAP_DOCUMENTS_PER_DAY</c> (10 — documents + QR codes),
/// <c>AI_CAP_PAYMENT_LINKS_PER_DAY</c> (5), <c>AI_CAP_BOOKINGS_PER_DAY</c> (5).
/// </summary>
public static class AiActionCaps
{
    public const string Documents = "documents";
    public const string PaymentLinks = "payment_links";
    public const string Bookings = "bookings";

    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public static int LimitFor(string kind) => kind switch
    {
        Documents => Env("AI_CAP_DOCUMENTS_PER_DAY", 10),
        PaymentLinks => Env("AI_CAP_PAYMENT_LINKS_PER_DAY", 5),
        Bookings => Env("AI_CAP_BOOKINGS_PER_DAY", 5),
        _ => 0,
    };

    private static int Env(string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
        && n >= 0 ? n : fallback;

    public static string ThreadTarget(int threadId) => $"thread:{threadId}";

    /// <summary>How many of <paramref name="kind"/> the AI made for this conversation since <paramref name="sinceUtc"/>.</summary>
    public static int Used(AppDbContext db, string kind, int threadId, DateTime sinceUtc)
    {
        var target = ThreadTarget(threadId);
        return kind switch
        {
            Documents => db.Documents.AsNoTracking()
                .Count(d => d.ThreadId == threadId && d.CreatedBy == "ai" && d.CreatedAt >= sinceUtc),
            Bookings => db.Bookings.AsNoTracking()
                .Count(b => b.ThreadId == threadId && b.CreatedBy == "ai" && b.CreatedAt >= sinceUtc),
            PaymentLinks => db.AuditLogs.AsNoTracking()
                .Count(a => a.Action == "ai.payment_link" && a.Target == target && a.CreatedAt >= sinceUtc),
            _ => 0,
        };
    }

    /// <summary>
    /// Null = go ahead. Otherwise the tool result to hand the model (the call is refused).
    /// Never throws: a counting failure lets the call through (the tool's own checks still apply).
    /// </summary>
    public static string? Refusal(AppDbContext db, string kind, int? threadId, bool preview, DateTimeOffset now)
    {
        if (preview || threadId is not int tid) return null;
        var limit = LimitFor(kind);
        if (limit <= 0) return null;
        int used;
        try { used = Used(db, kind, tid, now.UtcDateTime - Window); }
        catch { return null; }
        if (used < limit) return null;
        AuditTrail.Write(db, "ai.tool_capped", ThreadTarget(tid),
            $"{kind}: {used} in the last 24 h (limit {limit}) — refused", actor: "ai", source: "automation");
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["capped"] = true,
            ["error"] = $"This conversation reached its limit of {limit} {kind.Replace('_', ' ')} in 24 hours.",
            ["advice"] = "Don't make another. Tell the customer a member of the team will help with this, "
                         + "and call request_handover with the reason.",
        });
    }

    /// <summary>Count a payment link the AI created for the conversation (and leave the trail).</summary>
    public static void RecordPaymentLink(AppDbContext db, int? threadId, int? entityId, string amount, string currency)
    {
        if (threadId is not int tid) return;
        AuditTrail.Write(db, "ai.payment_link", ThreadTarget(tid), $"{currency} {amount}",
            actor: "ai", source: "automation", entityId: entityId);
    }
}
