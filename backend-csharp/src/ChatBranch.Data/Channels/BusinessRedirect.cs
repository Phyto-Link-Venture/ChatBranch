using System.Text.Json;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The business-number redirect (port of <c>views._maybe_business_redirect</c>).
/// A reminder-role number (the Cloud business number) is a template cannon, not
/// a conversation home: when a linked contact writes to it and the chatbot is
/// OFF for that number, they get ONE queued auto-answer per 24 hours pointing
/// them somewhere a conversation can happen. Routing rules (entity → org →
/// default):
/// <list type="bullet">
///   <item><c>business_redirect</c> (default true) — the switch;</item>
///   <item><c>business_redirect_target</c> — <c>conversation</c> (default: a
///         wa.me link to the contact's conversation number — their assigned
///         healthy conversation number, else the least-loaded one),
///         <c>support</c> (a pointer to <c>support_contact</c>) or
///         <c>template</c> (the approved template named in
///         <c>business_redirect_template</c>; unset falls back to the wa.me nudge).</item>
/// </list>
/// With the chatbot ON for the business number (<c>reminder_ai_default</c> on,
/// or a per-chat tick) the redirect stands down — the AI answers instead. The
/// job is pinned to the receiving number (payload <c>account_id</c>) and sent
/// as <c>source: "system"</c>; the 24h throttle reads OutboundJob rows carrying
/// <c>payload.redirect</c>, never message text.
/// </summary>
public static class BusinessRedirect
{
    public const int ThrottleHours = 24;

    public static bool Enabled(ScopingService sc, int? entityId)
    {
        var v = sc.SettingValue("business_redirect", entityId).Trim().ToLowerInvariant();
        return v is not ("false" or "0" or "no" or "off");
    }

    public static string Target(ScopingService sc, int? entityId) =>
        sc.SettingValue("business_redirect_target", entityId).Trim().ToLowerInvariant() switch
        {
            "support" => "support",
            "template" => "template",
            _ => "conversation",
        };

    /// <summary>Queue the redirect for this inbound when every condition holds; null otherwise. Never throws.</summary>
    public static OutboundJob? Maybe(AppDbContext db, ScopingService sc, Thread thread, int? receivingAccountId,
        DateTime? now = null)
    {
        try
        {
            if (thread.Channel != ChatBranch.Domain.Channels.WhatsApp || receivingAccountId is not int rid) return null;
            if (thread.OptedOut || thread.ContactId is not int contactId) return null;   // coverage gate
            var receiving = db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == rid);
            if (receiving == null || AccountRoles.Of(receiving) != AccountRoles.Reminder) return null;
            // the conversation must be ON the receiving number (inbound re-attribution
            // puts it there); with the chatbot on for it, the AI answers instead
            if (thread.AccountId != rid || AiEligibility.Effective(db, sc, thread)) return null;
            if (!Enabled(sc, thread.EntityId)) return null;

            var at = now ?? DateTime.UtcNow;
            if (RecentlyRedirected(db, thread, at)) return null;

            var target = Target(sc, thread.EntityId);
            var payload = new Dictionary<string, object?>
            {
                ["redirect"] = true, ["target"] = target,
                ["account_id"] = rid, ["source"] = "system",
            };
            var kind = JobKind.Text;
            var template = sc.SettingValue("business_redirect_template", thread.EntityId).Trim();
            if (target == "template" && template.Length > 0)
            {
                kind = JobKind.Template;
                payload["name"] = template;
                payload["language"] = "en";
                payload["variables"] = "";
            }
            else
            {
                var contact = db.Contacts.AsNoTracking().FirstOrDefault(c => c.Id == contactId);
                payload["text"] = target == "support"
                    ? SupportText(sc, thread.EntityId, contact)
                    : ConversationText(db, sc, thread, contact, receiving) ?? SupportText(sc, thread.EntityId, contact);
            }

            var job = new OutboundJob
            {
                Channel = ChatBranch.Domain.Channels.WhatsApp, Kind = kind, ToAddress = thread.Key,
                Payload = payload, ContactId = contactId, AccountId = rid, Status = JobStatus.Queued,
                CreatedAt = at, UpdatedAt = at,
            };
            db.OutboundJobs.Add(job);
            Audit.AuditTrail.Add(db, "business.redirect", $"{thread.Channel}:{thread.Key}",
                $"queued {kind} redirect ({target}) on {receiving.Name}", actor: "routing",
                source: AuditSources.System, entityId: thread.EntityId);
            db.SaveChanges();
            return job;
        }
        catch { return null; }
    }

    /// <summary>One auto-answer per conversation per 24h — read off the queue, not the chat text.</summary>
    public static bool RecentlyRedirected(AppDbContext db, Thread thread, DateTime now)
    {
        var since = now.AddHours(-ThrottleHours);
        var digits = PhoneMatch.Digits(thread.Key);
        return db.OutboundJobs.AsNoTracking()
            .Where(j => j.Channel == thread.Channel && j.CreatedAt >= since)
            .ToList()
            .Any(j => (j.ToAddress == thread.Key || (digits.Length > 0 && PhoneMatch.Digits(j.ToAddress) == digits))
                      && j.Payload.TryGetValue("redirect", out var r) && IsTrue(r));
    }

    private static string FirstName(Contact? c)
    {
        var n = (c?.Name ?? "").Trim();
        if (n.Length == 0 || n.Any(char.IsDigit)) return "";
        return " " + n.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string SupportText(ScopingService sc, int? entityId, Contact? contact)
    {
        var support = sc.SettingValue("support_contact", entityId).Trim();
        return $"Hi{FirstName(contact)}! This number only sends updates and isn't monitored for replies. "
               + (support.Length > 0
                   ? $"For help, please contact {support}."
                   : "For help, please reach our support team through our usual contact channels.");
    }

    /// <summary>The wa.me nudge, or null when the contact has no healthy conversation number to go to.</summary>
    private static string? ConversationText(AppDbContext db, ScopingService sc, Thread thread, Contact? contact,
        ChannelAccount receiving)
    {
        var routing = new Routing(db, sc);
        ChannelAccount? home = null;
        if (contact != null)
        {
            var sticky = db.AccountAssignments.AsNoTracking().Include(a => a.Account)
                .FirstOrDefault(a => a.ContactId == contact.Id && a.Channel == ChatBranch.Domain.Channels.WhatsApp);
            if (sticky?.Account is { } a && Routing.IsEligible(a) && AccountRoles.Of(a) == AccountRoles.Conversation)
                home = a;
        }
        home ??= routing.LeastLoaded(routing.Eligible(ChatBranch.Domain.Channels.WhatsApp, RouteNeed.Conversation,
                thread.EntityId, excludeId: receiving.Id)
            .Where(a => AccountRoles.Of(a) == AccountRoles.Conversation).ToList(), ChatBranch.Domain.Channels.WhatsApp);
        var number = home == null ? null : PhoneNumberOf(home);
        if (number == null) return null;
        return $"Hi{FirstName(contact)}! This number only sends updates and isn't monitored for replies. "
               + $"To chat with us, message us on WhatsApp: https://wa.me/{number}";
    }

    /// <summary>The account's own phone number, digits only — config keys, else a numeric display name.</summary>
    public static string? PhoneNumberOf(ChannelAccount a)
    {
        foreach (var key in new[] { "phone", "number", "phone_number", "display_phone_number", "display_phone" })
            if (a.Config != null && a.Config.TryGetValue(key, out var v) && PhoneMatch.Digits(v?.ToString()) is { Length: >= 8 } d)
                return d;
        var fromName = PhoneMatch.Digits(a.Name);
        return fromName.Length >= 8 && a.Name.Count(char.IsLetter) == 0 ? fromName : null;
    }

    private static bool IsTrue(object? v) => v switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}
