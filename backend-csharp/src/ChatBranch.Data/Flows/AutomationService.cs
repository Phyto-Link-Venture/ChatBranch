using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Flows;

/// <summary>
/// The automation-rule engine: when something happens to a conversation, check
/// each active rule's conditions and apply its actions.
///
/// Deliberately NOT the flow builder. A flow is contact-centric and
/// time-triggered (a reminder sequence over days); a rule is
/// conversation-centric and fires on an event that just happened. Both exist
/// because operators think in both shapes.
///
/// Two rules of the house:
/// <list type="bullet">
/// <item><b>Never throws into a webhook.</b> Every entry point is wrapped; a
/// broken rule degrades to "did nothing" and is recorded, never a 500 on an
/// inbound message.</item>
/// <item><b>All matching rules run</b>, in Position order — this is not a
/// first-match switch. An operator who wants exclusivity encodes it in the
/// conditions.</item>
/// </list>
/// </summary>
public class AutomationService
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;

    public AutomationService(AppDbContext db, ScopingService scoping)
    {
        _db = db;
        _scoping = scoping;
    }

    // ---- vocabulary (the UI reads this so the two can never drift) ----------

    public static readonly (string Id, string Label)[] Events =
    {
        ("conversation_created", "A conversation starts"),
        ("message_created", "A customer message arrives"),
        ("status_changed", "A conversation's status changes"),
        ("assigned", "A conversation is assigned"),
    };

    public static readonly (string Id, string Label)[] Fields =
    {
        ("message", "Message text"),
        ("channel", "Channel"),
        ("status", "Status"),
        ("assignee", "Assignee"),
        ("team", "Team"),
        ("labels", "Labels"),
        ("contact_name", "Contact name"),
        ("contact_tags", "Contact tags"),
        ("is_first_message", "Is the first message"),
        ("ai_on", "The chatbot is on"),
        ("handed_over", "Handed over to a human"),
        // "yes" when the conversation is linked to a contact, else "no"
        ("contact_linked", "Contact is linked (yes/no)"),
    };

    public static readonly (string Id, string Label)[] Operators =
    {
        ("contains", "contains"),
        ("not_contains", "does not contain"),
        ("equals", "is"),
        ("not_equals", "is not"),
        ("is_empty", "is empty"),
        ("is_not_empty", "is not empty"),
    };

    public static readonly (string Id, string Label)[] ActionTypes =
    {
        ("add_label", "Add label"),
        ("remove_label", "Remove label"),
        ("set_status", "Set status"),
        ("assign_team", "Assign to team"),
        ("assign_agent", "Assign to agent"),
        ("disable_ai", "Turn the chatbot off"),
        ("enable_ai", "Turn the chatbot on"),
        ("add_note", "Add a private note"),
        ("notify", "Notify the team"),
        ("hand_over", "Hand over to a human"),
        // value: "ask" (default) | "ask_and_create" — see Contacts/IdentityRequests.cs
        ("ask_identity", "Ask who they are (unknown customer)"),
    };

    // ---- entry points -------------------------------------------------------

    /// <summary>
    /// Run every active rule for this event. `messageText` is the message that
    /// triggered it (empty for non-message events). Returns how many rules fired.
    /// </summary>
    public int Run(string eventId, Thread thread, string messageText = "", bool isFirstMessage = false)
    {
        try
        {
            var rules = _scoping
                .ScopeInherited(_db.AutomationRules.AsNoTracking(), thread.EntityId)
                .Where(r => r.Active && r.Event == eventId)
                .OrderBy(r => r.Position).ThenBy(r => r.Id)
                .ToList();
            if (rules.Count == 0) return 0;

            var contact = thread.ContactId is int cid
                ? _db.Contacts.AsNoTracking().FirstOrDefault(c => c.Id == cid)
                : null;

            var fired = 0;
            foreach (var rule in rules)
            {
                if (!Matches(rule, thread, contact, messageText, isFirstMessage)) continue;
                Apply(rule, thread, messageText);
                fired++;
            }
            if (fired > 0) _db.SaveChanges();
            return fired;
        }
        catch
        {
            return 0;   // a broken rule never breaks an inbound webhook
        }
    }

    /// <summary>
    /// Dry run: would this rule match, without applying anything? Backs the
    /// editor's "test" button so a rule can be checked before it is enabled.
    /// </summary>
    public bool WouldMatch(AutomationRule rule, Thread thread, string messageText)
    {
        try
        {
            var contact = thread.ContactId is int cid
                ? _db.Contacts.AsNoTracking().FirstOrDefault(c => c.Id == cid)
                : null;
            return Matches(rule, thread, contact, messageText, isFirstMessage: false);
        }
        catch { return false; }
    }

    // ---- conditions ---------------------------------------------------------

    private bool Matches(AutomationRule rule, Thread thread, Contact? contact,
        string messageText, bool isFirstMessage)
    {
        foreach (var raw in rule.Conditions)
        {
            var cond = AsDict(raw);
            if (cond == null) continue;                     // malformed = ignored, not a match-all
            var field = Get(cond, "field");
            var op = Get(cond, "op");
            var wanted = Get(cond, "value");
            var actual = FieldValue(field, thread, contact, messageText, isFirstMessage);
            if (field is "ai_on" or "handed_over") (actual, wanted) = AiCondition(field, thread, wanted);
            if (!Compare(actual, op, wanted)) return false;  // ALL conditions must hold
        }
        return true;
    }

    private static string FieldValue(string field, Thread thread, Contact? contact,
        string messageText, bool isFirstMessage) => field switch
    {
        "message" => messageText,
        "channel" => thread.Channel,
        "status" => thread.Status,
        "assignee" => thread.Assignee,
        "team" => thread.TeamId?.ToString() ?? "",
        "labels" => string.Join(",", thread.Labels),
        "contact_name" => contact?.Name ?? "",
        "contact_tags" => contact == null ? "" : string.Join(",", contact.Tags),
        "is_first_message" => isFirstMessage ? "true" : "false",
        "contact_linked" => thread.ContactId != null ? "yes" : "no",
        _ => "",
    };

    private static bool Compare(string actual, string op, string wanted)
    {
        var a = (actual ?? "").Trim();
        var w = (wanted ?? "").Trim();
        return op switch
        {
            "contains" => w.Length == 0 || a.Contains(w, StringComparison.OrdinalIgnoreCase),
            "not_contains" => w.Length == 0 || !a.Contains(w, StringComparison.OrdinalIgnoreCase),
            "equals" => string.Equals(a, w, StringComparison.OrdinalIgnoreCase),
            "not_equals" => !string.Equals(a, w, StringComparison.OrdinalIgnoreCase),
            "is_empty" => a.Length == 0,
            "is_not_empty" => a.Length > 0,
            _ => false,   // an unknown operator matches NOTHING rather than everything
        };
    }

    // ---- actions ------------------------------------------------------------

    private void Apply(AutomationRule rule, Thread thread, string messageText)
    {
        var applied = new List<string>();
        foreach (var raw in rule.Actions)
        {
            var action = AsDict(raw);
            if (action == null) continue;
            var type = Get(action, "type");
            var value = Get(action, "value");
            switch (type)
            {
                case "add_label":
                    if (value.Length > 0 && !thread.Labels.Contains(value))
                        thread.Labels = new List<string>(thread.Labels) { value };
                    break;
                case "remove_label":
                    if (value.Length > 0 && thread.Labels.Contains(value))
                        thread.Labels = thread.Labels.Where(l => l != value).ToList();
                    break;
                case "set_status":
                    if (value.Length > 0) thread.Status = value;
                    break;
                case "assign_team":
                    if (int.TryParse(value, out var teamId)) thread.TeamId = teamId;
                    break;
                case "assign_agent":
                    thread.Assignee = value;
                    break;
                // the current number's own tick + the legacy column (AiEligibility),
                // so the switch reaches the auto-reply gate on a WhatsApp number too
                case "disable_ai":
                    ChatBranch.Data.Ai.AiEligibility.Set(thread, false);
                    break;
                case "enable_ai":
                    ChatBranch.Data.Ai.AiEligibility.Set(thread, true);
                    break;
                case "add_note":
                    // A private note is a message the customer never sees.
                    _db.Messages.Add(new Message
                    {
                        ThreadId = thread.Id, Direction = MessageDirection.Out,
                        Source = MessageSource.System, Private = true,
                        Text = value.Length > 0 ? value : $"Automation: {rule.Name}",
                        Status = "note", AccountId = thread.AccountId, CreatedAt = DateTime.UtcNow,
                    });
                    break;
                case "notify":
                    Notify(rule, thread, value.Length > 0 ? value : messageText);
                    break;
                case "hand_over":
                    // pending + note + bell + audit, and the chatbot stands down (Ai/Handover)
                    ChatBranch.Data.Ai.Handover.Mark(_db, thread, ChatBranch.Data.Ai.HandoverKinds.Rule,
                        value.Length > 0 ? value : $"Automation: {rule.Name}", $"rule:{rule.Id}", save: false);
                    break;
                case "ask_identity":
                    // arms the "who are you?" exchange; the inbound step sends the ask
                    if (!ChatBranch.Data.Contacts.IdentityRequestService.Arm(_db, thread, value)) continue;
                    break;
                default:
                    continue;   // unknown action type — skip, do not guess
            }
            applied.Add(type);
        }

        thread.UpdatedAt = DateTime.UtcNow;   // keeps the SSE stream fresh

        var tracked = _db.AutomationRules.FirstOrDefault(r => r.Id == rule.Id);
        if (tracked != null)
        {
            tracked.TimesFired++;
            tracked.LastFiredAt = DateTime.UtcNow;
        }
        _db.AuditLogs.Add(new AuditLog
        {
            Actor = "automation", Action = "automation.fired",
            Source = AuditSources.Automation, EntityId = thread.EntityId,
            Target = $"{thread.Channel}:{thread.Key}",
            Detail = $"{rule.Name} → {string.Join(", ", applied)}",
            CreatedAt = DateTime.UtcNow,
        });
    }

    private void Notify(AutomationRule rule, Thread thread, string text)
    {
        // Through the one notifier (org mutes + personal prefs); Run() saves.
        NotificationService.Notify(_db, NotificationCategories.Flows, "system",
            $"Automation · {rule.Name}", $"{thread.Name} ({thread.Channel}) — {text}",
            $"/chats?channel={Uri.EscapeDataString(thread.Channel)}&key={Uri.EscapeDataString(thread.Key)}",
            NotificationAudience.Admins(), new NotifyOptions { Thread = thread, Save = false });
    }

    /// <summary>
    /// The AI-control conditions: <c>ai_on</c> — the EFFECTIVE chatbot rule
    /// (Ai/AiEligibility, master switch and contact mode included);
    /// <c>handed_over</c> — a handover to a person is open. Both compare as
    /// true/false; yes/no/on/off in the rule's value mean the same.
    /// </summary>
    private (string Actual, string Wanted) AiCondition(string field, Thread thread, string wanted)
    {
        var on = field == "ai_on"
            ? ChatBranch.Data.Ai.AiEligibility.Effective(_db, _scoping, thread)
            : ChatBranch.Data.Ai.Handover.IsOpen(thread);
        var w = (wanted ?? "").Trim().ToLowerInvariant() switch
        {
            "yes" or "on" or "true" or "1" => "true",
            "no" or "off" or "false" or "0" => "false",
            var other => other,
        };
        return (on ? "true" : "false", w);
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// Conditions/actions round-trip through the JSON column as JsonElement or
    /// as a dictionary depending on whether the row was just written or loaded —
    /// normalize both.
    /// </summary>
    private static Dictionary<string, string>? AsDict(object? raw)
    {
        try
        {
            if (raw is JsonElement el && el.ValueKind == JsonValueKind.Object)
                return el.EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString());
            if (raw is Dictionary<string, object?> d)
                return d.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
            if (raw is Dictionary<string, string> s) return s;
        }
        catch { /* malformed entry */ }
        return null;
    }

    private static string Get(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) ? v ?? "" : "";

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
