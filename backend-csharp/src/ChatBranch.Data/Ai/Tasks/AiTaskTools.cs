using System.Text.Json;
using ChatBranch.Data.Mcp;
using ChatBranch.Domain;

namespace ChatBranch.Data.Ai.Tasks;

/// <summary>
/// The tools a background AI task may be given — ONE catalog, read by the
/// editor (grouped, with "not connected" shown honestly) and by the runner
/// (only the task's allowlist is ever offered to the model). None of them can
/// write to the customer: there is no send tool, by construction.
/// </summary>
public static class AiTaskTools
{
    public const string GroupChatBranch = "chatbranch";
    public const string GroupApps = "apps";
    public const string GroupWebhook = "webhook";
    public const string GroupMcp = "mcp";

    /// <summary>The allowlist id prefix of an external MCP server's tool.</summary>
    public const string McpPrefix = "mcp:";

    public const string Note = "add_private_note";
    public const string Label = "add_conversation_label";
    public const string ContactField = "set_contact_field";
    public const string Handover = "hand_over_to_person";
    public const string SheetsRow = "sheets_append_row";
    public const string DriveUpload = "drive_upload_attachment";
    public const string CalendarEvent = "calendar_create_event";
    public const string Webhook = "webhook_post";

    // ---- Social media (a managed connection — Zernio; users never see that name) ----
    /// <summary>The "provider" of the social tools: connected = the managed-connection key is configured.</summary>
    public const string SocialProvider = "zernio";
    public const string SocialApp = "Social media";
    public const string SocialAccounts = "social_list_accounts";
    public const string SocialDraft = "social_draft_post";
    public const string SocialSchedule = "social_schedule_post";
    public const string SocialHide = "social_hide_comment";
    public const string SocialReply = "social_reply_comment";
    /// <summary>Per-task opt-ins (task.config): publishing / scheduling publicly, replying publicly.</summary>
    public const string AllowPublish = "allow_publish";
    public const string AllowReply = "allow_reply";

    /// <summary>A tool's per-task setting the editor renders (key, type, default, label).</summary>
    public sealed record Setting(string Key, string Type, object? Default, string Label);

    /// <summary>The settings each tool takes (beyond the Google ones the editor already knows).</summary>
    public static IReadOnlyList<Setting> SettingsOf(string id) => id switch
    {
        SocialSchedule => new[] { new Setting(AllowPublish, "bool", false, "Allow this task to publish or schedule posts publicly") },
        SocialReply => new[] { new Setting(AllowReply, "bool", false, "Allow this task to reply publicly under comments") },
        _ => Array.Empty<Setting>(),
    };

    /// <summary>The connected app a tool belongs to (the editor's group heading), "" for none.</summary>
    public static string AppOf(Def d) => d.Provider switch
    {
        SocialProvider => SocialApp,
        "google_sheets" => "Google Sheets",
        "google_drive" => "Google Drive",
        "google_calendar" => "Google Calendar",
        _ => "",
    };

    /// <summary>One built-in tool: its id (= the model's tool name), group, the
    /// integration provider it needs ("" = none), and whether it CHANGES something
    /// (a dry run previews those instead of calling them).</summary>
    public sealed record Def(string Id, string Group, string Provider, bool Write, string Label, string Description,
        string Schema);

    public static readonly IReadOnlyList<Def> Builtins = new[]
    {
        new Def(Note, GroupChatBranch, "", true, "Add a private note",
            "Add a private note to this conversation for the team (the customer never sees it). Use it for anything " +
            "a person should know, e.g. what you extracted or what is missing.",
            """{"type":"object","properties":{"text":{"type":"string","description":"The note (at most 1000 characters)"}},"required":["text"]}"""),
        new Def(Label, GroupChatBranch, "", true, "Add a conversation label",
            "Add a label to this conversation (e.g. invoice, receipt, needs-review) so the team can filter on it.",
            """{"type":"object","properties":{"label":{"type":"string","description":"A short label, lowercase words joined by dashes"}},"required":["label"]}"""),
        new Def(ContactField, GroupChatBranch, "", true, "Set a contact custom field",
            "Set one of the contact's custom fields (only fields the business defined; the conversation must be " +
            "linked to a contact).",
            """{"type":"object","properties":{"key":{"type":"string","description":"The custom field key"},"value":{"type":"string","description":"The value"}},"required":["key","value"]}"""),
        new Def(Handover, GroupChatBranch, "", true, "Hand over to a person",
            "Flag this conversation for a person (a tracked handover with your reason) when something needs a human: " +
            "an unreadable document, a total that does not add up, a complaint.",
            """{"type":"object","properties":{"reason":{"type":"string","description":"Why a person should look"}},"required":["reason"]}"""),
        new Def(SheetsRow, GroupApps, "google_sheets", true, "Google Sheets: append a row",
            "Append ONE row to the business's Google Sheet. The time received and the customer's name and phone are " +
            "added automatically in front; give the remaining cells, in column order.",
            """{"type":"object","properties":{"values":{"type":"array","items":{"type":"string"},"description":"The row's cells in column order"}},"required":["values"]}"""),
        new Def(DriveUpload, GroupApps, "google_drive", true, "Google Drive: save the attachment",
            "Save the customer's attachment (the file in this message) to the business's Google Drive folder. Only " +
            "when the message HAS an attachment. Optionally give it a clear file name (e.g. \"INV-123 Acme.pdf\").",
            """{"type":"object","properties":{"file_name":{"type":"string","description":"File name to use (the original extension is kept)"}}}"""),
        new Def(CalendarEvent, GroupApps, "google_calendar", true, "Google Calendar: create an event",
            "Create an event on the business's Google Calendar (e.g. a payment due date). It does NOT invite the customer.",
            """{"type":"object","properties":{"title":{"type":"string"},"start":{"type":"string","description":"ISO 8601 date-time, e.g. 2026-11-02T09:00:00+08:00"},"minutes":{"type":"integer","description":"Length in minutes (default 30)"},"description":{"type":"string"}},"required":["title","start"]}"""),
        new Def(SocialAccounts, GroupApps, SocialProvider, false, "Social media: list accounts",
            "List the social media accounts this outlet can post to (id, platform, username, Facebook Pages as " +
            "\"<account id>:<page id>\" targets). Read-only.",
            """{"type":"object","properties":{}}"""),
        new Def(SocialDraft, GroupApps, SocialProvider, true, "Social media: save a draft post",
            "Save a DRAFT social post in ChatBranch for a person to review and publish — nothing is published. " +
            "Give the text and the target account ids (social_list_accounts).",
            """{"type":"object","properties":{"content":{"type":"string","description":"The post text"},"account_ids":{"type":"array","items":{"type":"string"},"description":"Target account ids or <account id>:<page id>"}},"required":["content"]}"""),
        new Def(SocialSchedule, GroupApps, SocialProvider, true, "Social media: schedule or publish a post",
            "Schedule (scheduled_at) or publish now a social post on the target accounts. PUBLIC — only when this " +
            "task was allowed to publish; otherwise save a draft instead.",
            """{"type":"object","properties":{"content":{"type":"string"},"account_ids":{"type":"array","items":{"type":"string"}},"scheduled_at":{"type":"string","description":"ISO 8601 time; omit to publish now"},"timezone":{"type":"string","description":"IANA time zone for a time without offset"}},"required":["content","account_ids"]}"""),
        new Def(SocialHide, GroupApps, SocialProvider, true, "Social media: hide the comment",
            "Hide (or unhide) the public comment THIS conversation is about (social comment conversations only; " +
            "Facebook, Instagram, Threads, X, TikTok). Use it for spam or abuse.",
            """{"type":"object","properties":{"hidden":{"type":"boolean","description":"true = hide (default), false = unhide"}}}"""),
        new Def(SocialReply, GroupApps, SocialProvider, true, "Social media: reply under the comment",
            "Post a short PUBLIC reply under the comment THIS conversation is about (social comment conversations " +
            "only, and only when this task was allowed to reply). Never share anything personal.",
            """{"type":"object","properties":{"text":{"type":"string","description":"The public reply (one to three short sentences)"}},"required":["text"]}"""),
        new Def(Webhook, GroupWebhook, "", true, "Send to another app (signed webhook)",
            "POST what you extracted, as JSON, to the business's own system (a signed webhook). Use it once per message.",
            """{"type":"object","properties":{"data":{"type":"object","description":"The JSON object to send"}},"required":["data"]}"""),
    };

    public static Def? Builtin(string id) => Builtins.FirstOrDefault(d => d.Id == id);

    public static JsonElement SchemaOf(Def d) => JsonDocument.Parse(d.Schema).RootElement.Clone();

    /// <summary>Is this a tool id the catalog knows (a built-in or an external MCP tool)?</summary>
    public static bool KnownId(string id) =>
        Builtin(id) != null || (id.StartsWith(McpPrefix) && id.Length > McpPrefix.Length && id.Length <= 70);

    /// <summary>The external MCP tools the operator assistant could use (enabled, tested servers, their allowlist).</summary>
    public static List<ExternalMcpTools.ExternalTool> Mcp(ScopingService sc)
    {
        try { return ExternalMcpTools.Available(sc); }
        catch { return new List<ExternalMcpTools.ExternalTool>(); }
    }

    /// <summary>A redacted, capped copy of a string for the run's record (no tokens, keys or bearer headers).</summary>
    public static string Redact(string? s, int max = 2000)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var r = System.Text.RegularExpressions.Regex.Replace(s,
            @"(?i)(bearer\s+)[A-Za-z0-9._\-~+/=]{8,}|\b(sk|rk|pk)_(live|test)_[A-Za-z0-9]{6,}|\bsk-[A-Za-z0-9_\-]{8,}|\bya29\.[A-Za-z0-9._\-]{8,}|\bcbk_[A-Za-z0-9_]{8,}",
            m => m.Groups[1].Success ? m.Groups[1].Value + "•••" : "•••");
        return r.Length <= max ? r : r[..max] + "…";
    }
}
