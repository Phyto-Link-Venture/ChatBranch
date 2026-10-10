using System.Text.Json;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Conversation-account recovery (port of <c>health._on_conversation_account_down</c>
/// + <c>health.enqueue_recovery</c>). When a conversation-role WhatsApp number
/// transitions INTO banned/disconnected, admins are told "Account X down — N
/// contacts affected", and with <c>recovery_mode = auto</c> the
/// <c>recovery_template</c> is queued for every contact assigned to it. The
/// template jobs carry no account: the queue routes them at send time to a
/// healthy Cloud number (the business one first) — templates never go out on
/// Baileys. <c>one_click</c> (the default) leaves that to
/// <c>POST /api/whatsapp/recovery/{id}</c>, which calls <see cref="Enqueue"/>.
/// A contact is never sent the same account's recovery template twice within
/// 24 hours, and opted-out conversations are skipped.
/// </summary>
public static class Recovery
{
    public const int DedupHours = 24;

    public sealed record Result(int Queued, int Skipped, int Affected, string? Error = null);

    public static string Mode(ScopingService sc, int? entityId) =>
        sc.SettingValue("recovery_mode", entityId).Trim().ToLowerInvariant() == "auto" ? "auto" : "one_click";

    public static string Template(ScopingService sc, int? entityId) =>
        sc.SettingValue("recovery_template", entityId).Trim();

    public static int AffectedCount(AppDbContext db, int accountId) =>
        db.AccountAssignments.Count(a => a.AccountId == accountId && a.Channel == ChatBranch.Domain.Channels.WhatsApp);

    /// <summary>
    /// The <see cref="AccountHealth.SetStatus"/> hook, run on every REAL
    /// transition. Adds rows only — the caller's save commits them. Never throws.
    /// </summary>
    /// <summary>True for a conversation-role WhatsApp number transitioning INTO
    /// banned/disconnected — the case <see cref="OnTransition"/> answers (and the
    /// generic health bell then stands down, so admins get one alert, not two).</summary>
    public static bool Applies(ChannelAccount account, string prev, string next) =>
        account.Channel == ChatBranch.Domain.Channels.WhatsApp
        && AccountRoles.Of(account) == AccountRoles.Conversation
        && Routing.DownStatuses.Contains(next) && !Routing.DownStatuses.Contains(prev);

    public static void OnTransition(AppDbContext db, ChannelAccount account, string prev, string next)
    {
        try
        {
            if (!Applies(account, prev, next)) return;

            var sc = new ScopingService(db);
            var affected = AffectedCount(db, account.Id);
            var label = string.IsNullOrWhiteSpace(account.Name) ? $"WhatsApp #{account.Id}" : account.Name;
            var mode = Mode(sc, account.EntityId);
            string tail;
            if (affected == 0) tail = "No contacts are assigned to it.";
            else if (mode == "auto")
            {
                var r = Enqueue(db, sc, account, save: false);
                tail = r.Error ?? $"Queued the recovery template for {r.Queued} contact(s).";
            }
            else
                tail = Template(sc, account.EntityId).Length > 0
                    ? "Send the recovery template from Account assignment → Recovery."
                    : "Set a recovery template under Routing rules to win them back.";

            int? tenantId = account.EntityId is int eid
                ? db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => (int?)e.TenantId).FirstOrDefault()
                : null;
            NotificationService.Notify(db, NotificationCategories.AccountHealth, "account_health",
                $"Account {label} down — {affected} contact{(affected == 1 ? "" : "s")} affected",
                $"{label} is {next}. {tail}", "/assignments",
                NotificationAudience.Admins(tenantId),
                new NotifyOptions { RefKey = $"recovery:{account.Id}", Save = false });
        }
        catch { /* a health transition must never fail on its side effects */ }
    }

    /// <summary>
    /// Queue the recovery template for every contact assigned to
    /// <paramref name="account"/> (the manual one-click and the auto path).
    /// </summary>
    public static Result Enqueue(AppDbContext db, ScopingService sc, ChannelAccount account, bool save = true)
    {
        var affected = db.AccountAssignments.AsNoTracking().Include(a => a.Contact)
            .Where(a => a.AccountId == account.Id && a.Channel == ChatBranch.Domain.Channels.WhatsApp)
            .OrderBy(a => a.ContactId)
            .ToList();
        var template = Template(sc, account.EntityId);
        if (template == "")
            return new Result(0, 0, affected.Count,
                "No recovery template configured — set 'recovery_template' under Routing rules first.");
        var routing = new Routing(db, sc);
        if (routing.Eligible(ChatBranch.Domain.Channels.WhatsApp, RouteNeed.Template, account.EntityId).Count == 0)
            return new Result(0, 0, affected.Count,
                "No healthy reminder (official business: Cloud API, YCloud or Twilio) account to carry the recovery template — "
                + "reconnect one under Channels.");

        var now = DateTime.UtcNow;
        var since = now.AddHours(-DedupHours);
        var recent = db.OutboundJobs.AsNoTracking()
            .Where(j => j.Channel == ChatBranch.Domain.Channels.WhatsApp && j.Kind == JobKind.Template
                        && j.CreatedAt >= since && j.ContactId != null)
            .ToList()
            .Where(j => j.Payload.TryGetValue("recovery_account", out var v) && AsInt(v) == account.Id)
            .Select(j => j.ContactId!.Value)
            .ToHashSet();

        int queued = 0, skipped = 0;
        foreach (var asg in affected)
        {
            var waId = PhoneMatch.Digits(asg.Contact?.Phone);
            if (waId.Length < 6 || recent.Contains(asg.ContactId)) { skipped++; continue; }
            var optedOut = db.Threads.AsNoTracking().Any(t => t.Channel == ChatBranch.Domain.Channels.WhatsApp
                && t.ContactId == asg.ContactId && t.OptedOut);
            if (optedOut) { skipped++; continue; }
            db.OutboundJobs.Add(new OutboundJob
            {
                Channel = ChatBranch.Domain.Channels.WhatsApp, Kind = JobKind.Template, ToAddress = waId,
                Payload = new Dictionary<string, object?>
                {
                    ["name"] = template, ["language"] = "en", ["variables"] = "",
                    ["recovery"] = true, ["recovery_account"] = account.Id, ["source"] = "system",
                },
                ContactId = asg.ContactId, Status = JobStatus.Queued, CreatedAt = now, UpdatedAt = now,
            });
            queued++;
        }
        if (save && queued > 0) db.SaveChanges();
        return new Result(queued, skipped, affected.Count);
    }

    private static int? AsInt(object? v) => v switch
    {
        int i => i,
        long l => (int)l,
        JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetInt32(out var n) => n,
        string s when int.TryParse(s, out var n) => n,
        _ => null,
    };
}
