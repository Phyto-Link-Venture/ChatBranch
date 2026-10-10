namespace ChatBranch.Api.Assistant;

/// <summary>
/// The compact product guide the operator assistant answers "how do I…" from —
/// distilled from CLAUDE.md and docs/. Each section carries the page it lives on
/// (a navigation chip) and the words people use when asking about it (the
/// sandbox matcher's vocabulary). Keep it short: it rides in every prompt.
/// </summary>
public static class AssistantGuide
{
    public sealed record Section(string Title, string Href, string Capability, string[] Keywords, string Body);

    public static readonly Section[] Sections =
    {
        new("Chats (unified inbox)", "/chats", "chats.view",
            new[] { "chat", "chats", "inbox", "conversation", "conversations", "reply", "message", "messages", "note", "notes", "label", "labels", "resolve", "snooze", "chatbot" },
            "Chats is one inbox for WhatsApp, email, Messenger, Instagram, Telegram and website chat. Open a " +
            "conversation to reply, add a private note (never sent to the customer), set its status (open, " +
            "pending, snoozed, resolved), assign it to a team or agent, add labels, or switch the chatbot off " +
            "for that chat. WhatsApp's 24-hour rule: once 24 hours pass since the customer's last message you " +
            "must open with an approved template."),
        new("Contacts", "/contacts", "contacts.view",
            new[] { "contact", "contacts", "customer", "customers", "import", "csv", "phone", "people" },
            "Contacts are the people you talk to. Add one with a name and a phone in international format " +
            "(+60123456789) or an email (either is enough), or import a CSV from Contacts → Import. Link a chat to its contact from the chat's " +
            "Contact panel. Each contact page shows every channel they use, their records and history."),
        new("Custom fields", "/settings/custom-fields", "settings.ai.manage",
            new[] { "custom", "field", "fields", "attribute", "attributes", "variable", "variables" },
            "Settings → Custom Fields adds your own fields to contacts. They appear on every contact and can be " +
            "used in templates as {{cf_<key>}}."),
        new("Channels", "/channels", "channels.view",
            new[] { "channel", "channels", "whatsapp", "email", "smtp", "imap", "telegram", "messenger", "instagram", "number", "connect", "baileys", "qr", "sandbox", "quiet", "pacing" },
            "Channels is where every provider account is added: WhatsApp (a Cloud API business number, or a " +
            "personal number paired by QR code), email (SMTP + IMAP), Messenger, Instagram and Telegram. " +
            "Quiet hours, send pacing and the voice AI are per account under Channels → edit account → " +
            "Delivery & AI. With no accounts linked everything runs in sandbox mode."),
        new("Website widget", "/channels", "channels.manage",
            new[] { "widget", "website", "site", "embed", "script", "visitor", "visitors" },
            "Channels → Website widget gives you a script tag. Paste it on your site and visitors can chat with " +
            "you without a phone number; their conversations land in Chats like any other channel."),
        new("Message templates", "/templates", "templates.manage",
            new[] { "template", "templates", "canned", "saved", "shortcut", "short", "quick", "reply" },
            "Message Templates are saved replies for every channel — type /short_code in the composer to insert " +
            "one. WhatsApp's Meta-approved templates are managed inside the WhatsApp channel settings."),
        new("Journeys", "/journeys", "flows.manage",
            new[] { "journey", "journeys", "flow", "flows", "workflow", "workflows", "sequence", "reminder", "builder", "drip", "campaign" },
            "Journeys (formerly the flow builder) draw multi-step sequences: send WhatsApp or email, place an AI or " +
            "script call, wait, then branch — on conditions (tags, fields, the reply, bookings…) or by letting the AI " +
            "decide from the conversation. Switch a journey to Live and it runs on its own."),
        new("Automations", "/automations", "flows.manage",
            new[] { "automation", "automations", "rule", "rules", "trigger", "auto", "routing", "route" },
            "Automations react to conversation events — a new conversation, a new message, a status change or " +
            "an assignment. When the conditions match, all matching rules apply their actions: add a label, " +
            "assign a team or agent, set a status, switch the chatbot off, add a note."),
        new("Teams", "/teams", "users.view",
            new[] { "team", "teams", "assign", "assignment", "round", "robin", "agents" },
            "Teams own conversations. A team with auto-assign shares new chats round-robin across its members; " +
            "assign from the chat header or with an automation."),
        new("Knowledge Base, AI Skills and Help Center", "/knowledge", "settings.ai.manage",
            new[] { "knowledge", "faq", "faqs", "policy", "policies", "ai", "bot", "skill", "skills", "help", "center", "article", "articles", "notice" },
            "The AI answers from the Knowledge Base (facts, policies, FAQs, events and notices that expire on " +
            "their own) and follows AI Skills (what to do when). Published articles also appear in the public " +
            "Help Center at /help."),
        new("AI playground", "/ai-playground", "ai.monitor",
            new[] { "playground", "simulate", "rehearse", "test", "testing", "prompt", "compare" },
            "The AI playground (Automate → AI playground) lets you chat with the AI as a customer would — a made-up " +
            "customer, an existing contact, or a copy of a real conversation — and see why it answered: the knowledge " +
            "and lessons it used, tools and checks, and what would happen in production (sent, held for review, handed " +
            "over, blocked or AI off). Admins can try another model, an unsaved persona or switch knowledge, lessons, " +
            "skills or memory off, and compare two set-ups side by side. Nothing is sent, stored or booked; a correction " +
            "you teach there becomes a lesson waiting in AI monitor → Learning."),
        new("Users, roles and permissions", "/users", "users.view",
            new[] { "user", "users", "role", "roles", "permission", "permissions", "login", "password", "agent", "admin", "invite" },
            "Users & Roles creates logins (superadmin, admin, agent or your own roles) and gives agents access to " +
            "specific entities. Permissions decides what each role may do."),
        new("Entities", "/entities", "entities.view",
            new[] { "entity", "entities", "branch", "branches", "outlet", "outlets", "location", "tenant", "brand" },
            "Entities are your outlets or branches, arranged as a tree. Head office sees its branches' data; a " +
            "branch never sees head office's. The switcher in the top bar narrows every page to one entity."),
        new("Dashboard and reports", "/dashboard", "analytics.view",
            new[] { "dashboard", "analytics", "report", "reports", "stats", "statistics", "metrics", "numbers", "usage", "cost" },
            "The Dashboard (the home page) shows the message funnel, engagement, call outcomes, per-account volume and AI usage; " +
            "Reports break conversations down by agent, team, label and channel."),
        new("Activity log", "/activity", "activity.view",
            new[] { "activity", "audit", "log", "logs", "history", "happened", "changed", "who" },
            "Activity is the record of everything people, API keys, AI apps (MCP), this assistant, automations, " +
            "flows and webhooks did. Filter by time, person, source, action or entity, and export CSV."),
        new("Developers: API keys and MCP", "/developers", "api_keys.manage",
            new[] { "api", "key", "keys", "developer", "developers", "mcp", "claude", "cursor", "token", "integration" },
            "Developers creates personal API keys for scripts and connects AI apps such as Claude or Cursor to " +
            "ChatBranch over MCP (the /mcp endpoint, authenticated with a key). It also manages external MCP " +
            "servers whose tools this assistant may use."),
        new("Integrations", "/integrations", "integrations.view",
            new[] { "google", "calendar", "drive", "gmail", "sheets", "shopify", "stripe", "payment", "oauth" },
            "Integrations connects Google Calendar, Drive, Gmail and Sheets (your own account or a shared one), " +
            "Shopify and Stripe. Flows can then book events, upload files, append rows or create payment links."),
        new("Calls", "/chats", "chats.view",
            new[] { "call", "calls", "voice", "phone", "dial", "transcript", "recording" },
            "AI voice calls start from a chat's call button or a flow step. While a call runs, the chat panel " +
            "shows the live transcript with Guide, Takeover, Escalate and End; past calls are in Call Logs."),
        new("Settings and appearance", "/settings", "settings.ai.manage",
            new[] { "setting", "settings", "theme", "dark", "appearance", "notification", "notifications", "config" },
            "Settings holds system configuration: app keys, notifications, custom fields and appearance (the " +
            "three themes, light or dark)."),
    };

    /// <summary>Every page the assistant may suggest, for chips and "go to …".</summary>
    public static readonly (string Name, string Href, string Capability, string[] Aliases)[] Pages =
    {
        ("Dashboard", "/dashboard", "analytics.view", new[] { "dashboard", "home", "overview", "analytics", "reports", "stats" }),
        ("Chats", "/chats", "chats.view", new[] { "chats", "chat", "inbox", "conversations", "messages" }),
        ("Contacts", "/contacts", "contacts.view", new[] { "contacts", "contact", "customers", "people" }),
        ("Journeys", "/journeys", "flows.manage", new[] { "journeys", "journey", "flow builder", "flows", "flow", "workflows" }),
        ("Automations", "/automations", "flows.manage", new[] { "automations", "automation", "rules" }),
        ("AI playground", "/ai-playground", "ai.monitor", new[] { "ai playground", "playground", "test the ai", "try the ai" }),
        ("Channels", "/channels", "channels.view", new[] { "channels", "channel", "whatsapp", "accounts" }),
        ("Integrations", "/integrations", "integrations.view", new[] { "integrations", "integration", "google" }),
        ("Users & Roles", "/users", "users.view", new[] { "users", "user", "roles", "logins" }),
        ("Teams", "/teams", "users.view", new[] { "teams", "team" }),
        ("Entities", "/entities", "entities.view", new[] { "entities", "entity", "branches", "outlets" }),
        ("Permissions", "/permissions", "rbac.manage", new[] { "permissions", "permission matrix" }),
        ("Knowledge Base", "/knowledge", "settings.ai.manage", new[] { "knowledge base", "knowledge", "faq" }),
        ("AI Skills", "/skills", "settings.ai.manage", new[] { "ai skills", "skills" }),
        ("Message Templates", "/templates", "templates.manage", new[] { "message templates", "templates", "canned responses" }),
        ("Script Templates", "/script-templates", "script_templates.manage", new[] { "script templates", "scripts" }),
        ("Call Logs", "/call-logs", "call_logs.view", new[] { "call logs", "calls", "call history" }),
        ("Activity", "/activity", "activity.view", new[] { "activity", "activity log", "audit log", "audit" }),
        ("Notifications", "/notifications", "notifications.manage", new[] { "notifications", "notification settings" }),
        ("Developers", "/developers", "api_keys.manage", new[] { "developers", "api keys", "api", "mcp" }),
        ("Settings", "/settings", "settings.ai.manage", new[] { "settings", "configuration" }),
        ("Help Center", "/help", "", new[] { "help center", "help" }),
    };

    /// <summary>The guide as a prompt block, limited to what this user may open.</summary>
    public static string PromptBlock(ISet<string> caps)
    {
        var sb = new System.Text.StringBuilder("PRODUCT GUIDE (answer how-to questions from this; cite the page path):\n");
        foreach (var s in Sections)
            sb.Append("- ").Append(s.Title).Append(" (").Append(s.Href)
                .Append(caps.Contains(s.Capability) || s.Capability.Length == 0 ? "" : "; not available to this user")
                .Append("): ").Append(s.Body).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>Best guide section for a question (keyword overlap), or null.</summary>
    public static Section? Match(string text)
    {
        var words = Words(text);
        if (words.Count == 0) return null;
        var best = Sections
            .Select(s => (s, score: s.Keywords.Count(k => words.Contains(k))
                                    + (words.Contains(s.Title.ToLowerInvariant()) ? 2 : 0)))
            .OrderByDescending(x => x.score)
            .First();
        return best.score > 0 ? best.s : null;
    }

    public static HashSet<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), "[^a-z0-9+]+")
            .Where(w => w.Length >= 2)
            .ToHashSet();
}
