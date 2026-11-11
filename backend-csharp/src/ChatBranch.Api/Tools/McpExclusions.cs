namespace ChatBranch.Api.Tools;

/// <summary>
/// The signed-in dashboard endpoints that deliberately have NO MCP / assistant
/// tool — each with the reason. Everything else an operator can do is a tool
/// (<see cref="ToolCatalog"/>, <see cref="BuiltinTools"/>), and
/// <c>McpCoverageTests</c> fails the build on any new endpoint that is neither
/// reachable through a tool nor listed here.
///
/// A rule names one method + route template, or (<see cref="Prefix"/>) every
/// route under a path prefix. Reasons fall in a handful of groups:
/// credentials (provider/app/AI keys and anything that carries them), RBAC and
/// key minting, the assistant itself, binary/streaming/file exports (the JSON
/// twin IS a tool), legacy twins of a route a tool already uses, and testing aids.
/// </summary>
public static class McpExclusions
{
    public sealed record Rule(string Method, string Route, string Reason, bool Prefix = false)
    {
        public bool Matches(string method, string route) =>
            (Method == "*" || string.Equals(Method, method, StringComparison.OrdinalIgnoreCase))
            && (Prefix
                ? route.StartsWith(Route, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Route, route, StringComparison.OrdinalIgnoreCase));
    }

    private const string Credentials =
        "credential write: it stores provider/app secrets (tokens, passwords, API keys) — a person enters " +
        "those in the dashboard; tools never carry secrets";
    private const string CredentialRead =
        "credential settings page (masked secrets + webhook verify tokens) — no operator task needs it from an AI";
    private const string Pairing =
        "pairing a personal WhatsApp number: needs a person at the phone, scanning a QR or typing a pairing code";
    private const string Rbac =
        "RBAC: roles and the permission matrix are changed by a person (rbac.manage), never by an AI acting as one";
    private const string Assistant =
        "Ask ChatBranch itself (chat / confirm / cancel) — a tool calling the assistant would recurse";
    private const string OwnAccount =
        "credentials / own account: Settings → My account (name, email, password, sessions) is the person at the " +
        "keyboard — the writes refuse API keys (403), so an AI acting as them never changes them";
    private const string TwoFactor =
        "credentials / own account: two-step sign-in (authenticator secret, recovery codes) belongs to the person " +
        "holding the phone — the writes refuse API keys (403), so an AI acting as them never changes it";
    private const string SecurityPolicy =
        "the organization's sign-in security policy (require_2fa) is set by a person (security.manage), never by an AI acting as one";
    private const string MailView =
        "the inbox's mail-app view — cleaned HTML for a sandboxed frame; tools read the same mail's text " +
        "with conversations_list_messages";
    private const string Binary =
        "binary file stream (media / audio) — tools return ids and metadata, the dashboard plays or downloads the file";
    private const string Export =
        "file export (CSV / vCard) of data a JSON tool already returns";
    private const string Connecting =
        "connecting an AI app: a person approves it in their browser and manages their own connections — " +
        "an AI acting as them must not be able to grant, read or revoke its own access";
    private const string PushDevice =
        "a device alerts itself: the browser or phone app that holds the push subscription registers, tests and " +
        "forgets it from the session on that device — an AI acting as someone has no device to be alerted on";

    public static readonly IReadOnlyList<Rule> All = new Rule[]
    {
        // social media through Zernio: the key, its webhook secret and file bytes stay with a person
        new("GET", "/api/zernio/status", CredentialRead),
        new("POST", "/api/zernio/test", "tests the deployment's Zernio API key (Settings → Integrations) — a credential check a person runs after saving the key"),
        new("POST", "/api/zernio/webhook/register", Credentials + " (it generates and stores the Zernio webhook signing secret)"),
        new("POST", "/api/social/connect/whatsapp", Credentials + " (a business's own Meta System User token for a managed WhatsApp connection — typed by a person, never by an assistant)"),
        new("POST", "/api/social/media", "binary upload (multipart) of a post's photo or video — social_create_post takes public https media URLs instead"),
        new("POST", "/api/social/phone-numbers/purchase",
            "spends the owner's money: buying a number starts a recurring monthly charge on their bill. A person " +
            "decides that in the dashboard with the price in front of them (social_list_phone_numbers quotes it)"),
        new("DELETE", "/api/social/phone-numbers/{id}",
            "irreversible: releasing a number returns it to the carrier pool and it cannot be taken back (any " +
            "inbox on it stops working). A person confirms it in the dashboard by typing the number out"),
        // ---- the media library: the bytes themselves ---------------------------
        new("POST", "/api/media/library",
            "binary upload (multipart): a person drops a file in from the dashboard or their phone. " +
            "An AI caller asks for an upload link with media_create_upload and PUTs to it, then sends " +
            "by file_id"),
        new("GET", "/api/media/library/{fileId}/file", Binary + " — media_list returns each file's id, name and type"),

        // ---- connecting an AI app by signing in --------------------------------
        new("GET", "/api/oauth/authorize/{request}", Connecting),
        new("POST", "/api/oauth/authorize/{request}", Connecting),
        new("GET", "/api/oauth/connections", Connecting),
        new("DELETE", "/api/oauth/connections/{id:int}", Connecting),

        // ---- push notifications on a person's own devices ---------------------
        new("GET", "/api/push/config", PushDevice),
        new("GET", "/api/push/devices", PushDevice),
        new("POST", "/api/push/devices", PushDevice),
        new("DELETE", "/api/push/devices/{id:int}", PushDevice),
        new("POST", "/api/push/test", PushDevice),

        // ---- credentials: provider, app and AI keys ---------------------------
        new("PUT", "/api/settings", "raw Setting rows, credentials included (settings.raw.manage) — " + Credentials),
        new("GET", "/api/settings", "raw Setting rows (credential keys included) — the specific settings tools read their own keys"),
        new("GET", "/api/settings/ai-providers", "AI keys & models (settings.ai_keys.manage) — " + CredentialRead),
        new("PUT", "/api/settings/ai-providers", "AI provider keys — " + Credentials),
        new("POST", "/api/settings/ai-providers/test", "tries an AI provider key (one typed in the panel may ride along) — " + Credentials),
        new("GET", "/api/whatsapp/settings", "WhatsApp Cloud app settings + verify token — " + CredentialRead),
        new("POST", "/api/whatsapp/settings", Credentials),
        new("PUT", "/api/whatsapp/settings", Credentials),
        new("GET", "/api/messaging/settings", "default SMTP/IMAP mailbox settings — " + CredentialRead),
        new("POST", "/api/messaging/settings", Credentials),
        new("PUT", "/api/messaging/settings", Credentials),
        new("POST", "/api/messaging/settings/test", "tests the default SMTP credentials being edited on the same settings page — " + CredentialRead),
        new("GET", "/api/integrations/settings", "App & provider keys (OAuth client ids/secrets) — " + CredentialRead),
        new("PUT", "/api/integrations/settings", Credentials),
        new("POST", "/api/integrations/{provider}/connect", "connects Shopify/Stripe with API keys — " + Credentials),
        new("POST", "/api/integrations/accounts", "creates an integration row with its API key / webhook secret — " + Credentials),
        new("POST", "/api/integrations/mcp", "external MCP server definitions carry auth tokens/headers — " + Credentials),
        new("PATCH", "/api/integrations/mcp/{id:int}", "external MCP server definitions carry auth tokens/headers — " + Credentials),
        new("DELETE", "/api/integrations/mcp/{id:int}", "external MCP servers are managed with their credentials by a person at /developers"),
        new("POST", "/api/whatsapp/accounts", "WhatsApp number create (Cloud API / YCloud / Twilio credentials) — " + Credentials),
        new("POST", "/api/whatsapp/accounts/{id:int}", "WhatsApp number update (Cloud API / YCloud / Twilio credentials) — " + Credentials),
        new("DELETE", "/api/whatsapp/accounts/{id:int}", "removing a number is part of the credential lifecycle at /channels (its conversations stay)"),
        new("POST", "/api/messaging/accounts", "inbox create (bot tokens, page tokens, SMTP/IMAP passwords) — " + Credentials),
        new("PATCH", "/api/messaging/accounts/{id}", "inbox update (bot tokens, page tokens, SMTP/IMAP passwords) — " + Credentials),
        new("DELETE", "/api/messaging/accounts/{id}", "removing an inbox is part of the credential lifecycle at /channels (its conversations stay)"),
        new("GET", "/api/whatsapp/voice-settings", "SIP trunk settings (masked password) — " + CredentialRead),
        new("PUT", "/api/whatsapp/voice-settings", "SIP trunk username/password — " + Credentials),
        new("POST", "/api/whatsapp/voice-settings/test", "tests the SIP credentials being edited on the same settings page — " + CredentialRead),
        new("POST", "/api/whatsapp/accounts/{id:int}/baileys/start", Pairing),
        new("POST", "/api/whatsapp/accounts/{id:int}/baileys/logout", Pairing),
        new("GET", "/api/whatsapp/accounts/{id:int}/baileys/status", Pairing + " (the status carries the pairing QR / code)"),
        new("POST", "/api/whatsapp/accounts/waha", "WhatsApp number create through the operator's own WAHA server "
            + "(its base URL + API key + webhook HMAC key) — " + Credentials),
        new("PUT", "/api/whatsapp/accounts/{id:int}/waha", "WAHA connection settings (base URL, API key, webhook "
            + "HMAC key) — " + Credentials),
        new("POST", "/api/whatsapp/accounts/{id:int}/waha/start", Pairing),
        new("POST", "/api/whatsapp/accounts/{id:int}/waha/logout", Pairing),
        new("GET", "/api/whatsapp/accounts/{id:int}/waha/status", Pairing + " (the status carries the pairing QR / code)"),

        // ---- RBAC, keys, sessions -------------------------------------------
        new("PUT", "/api/auth/permissions", Rbac),
        new("POST", "/api/auth/roles", Rbac),
        new("PATCH", "/api/auth/roles/{slug}", Rbac),
        new("DELETE", "/api/auth/roles/{slug}", Rbac),
        new("POST", "/api/developers/keys", "mints an API key: the secret is shown once, to a person — an AI never creates keys"),
        new("POST", "/api/developers/mcp/test", "the dashboard's self-probe of this very MCP endpoint"),
        new("POST", "/api/auth/logout", "ends a dashboard session; an API key has no session to end"),
        new("GET", "/api/auth/me/account", OwnAccount),
        new("PATCH", "/api/auth/me/account", OwnAccount),
        new("POST", "/api/auth/me/password", OwnAccount),
        new("POST", "/api/auth/me/sessions/revoke-others", OwnAccount),
        // two-step sign-in + the organization's sign-in security policy
        new("GET", "/api/auth/me/2fa", TwoFactor),
        new("POST", "/api/auth/me/2fa/setup", TwoFactor),
        new("POST", "/api/auth/me/2fa/confirm", TwoFactor),
        new("POST", "/api/auth/me/2fa/recovery-codes", TwoFactor),
        new("POST", "/api/auth/me/2fa/disable", TwoFactor),
        new("POST", "/api/v1/users/{id:int}/2fa/reset",
            "credentials: resetting someone's two-step sign-in (a lost phone) is a person's decision at /users — sessions only, API keys get 403"),
        new("GET", "/api/settings/security", SecurityPolicy),
        new("PUT", "/api/settings/security", SecurityPolicy),
        // Settings → System email: the SMTP login sign-in emails go out through
        new("GET", "/api/settings/system-email", "the System email (SMTP for welcome emails and password links) — " + CredentialRead),
        new("PUT", "/api/settings/system-email", "the System email's SMTP login — " + Credentials),
        new("POST", "/api/settings/system-email/test",
            "sends a test email through the System email's SMTP login to the person at the keyboard — part of entering credentials"),
        new("POST", "/api/client-errors", "the dashboard's own crash reporter (a browser telling the server it broke) — not an operator action"),

        // ---- the assistant itself -------------------------------------------
        new("POST", "/api/assistant/chat", Assistant),
        new("POST", "/api/assistant/confirm", Assistant),
        new("POST", "/api/assistant/cancel", Assistant),

        // ---- binary, streaming, exports -------------------------------------
        new("GET", "/api/whatsapp/stream", "server-sent event stream (the inbox's live change token) — MCP here is request/response; poll the list tools"),
        new("GET", "/api/messaging/messages/{id:int}/email", MailView),
        new("GET", "/api/calls/recording/{callId}", Binary),
        new("GET", "/api/whatsapp/calls/recording/{name}", Binary),
        new("GET", "/api/documents/{id:int}/file", Binary + " (documents_list gives each document's number, total and status)"),
        new("GET", "/api/documents/preview/{id}", "a throw-away preview file from the AI playground / the skill form — " + Binary),
        new("GET", "/api/aicore/skills/documents/logo/{name}", "the Documents skill's uploaded logo image — " + Binary),
        new("POST", "/api/aicore/skills/documents/logo", "uploads a logo image (multipart) for the Documents skill's letterhead — set it from the Skills page"),
        new("GET", "/api/aicore/skills/documents/letterhead/{name}", "the Documents template's uploaded letterhead background PDF — " + Binary),
        new("POST", "/api/aicore/skills/documents/letterhead", "uploads a letterhead background PDF (multipart) for the Documents template — set it from the template designer on the Skills page"),
        new("POST", "/api/aicore/skills/documents/template/analyze", "reads an uploaded design (multipart PDF / PNG / JPEG) into a proposed Documents template — a file upload for the template designer; skills_assist_document_template edits a template from words"),
        new("POST", "/api/aicore/skills/documents/html/draft", "drafts a custom layout (HTML template) from an uploaded design (multipart PDF / PNG / JPEG) — a file upload for the custom layout editor; skills_assist_document_layout edits a layout from words and skills_preview_document_layout renders one"),
        new("POST", "/api/voice/preview", "returns synthesized audio — " + Binary),
        new("POST", "/api/voice/clone", "the dashboard's multipart upload — MCP clones through voice_clone_voice (/api/voice/clone-from), which takes references instead of raw audio"),
        new("GET", "/api/billing/ai/pdf", "the AI bill as a PDF — " + Binary + " (billing_get_ai_bill returns the same figures)"),
        new("POST", "/api/billing/ai/pdf/link", "mints a 5-minute signed link to the AI bill PDF for a browser that cannot send an auth header (the installed iOS app); sessions only, API keys get 403 — " + Binary),
        new("GET", "/api/audit.csv", Export + " (activity_search_log)"),
        new("GET", "/api/analytics/reports.csv", Export + " (analytics_get_reports)"),
        new("GET", "/api/messaging/contacts/vcard", Export + " (search_contacts)"),

        // ---- legacy twins of routes a tool already uses -----------------------
        new("GET", "/api/whatsapp/threads", "legacy per-channel list, superseded by GET /api/inbox/threads (conversations_search / list_conversations)"),
        new("GET", "/api/messaging/threads", "legacy per-channel list, superseded by GET /api/inbox/threads (conversations_search / list_conversations)"),
        new("POST", "/api/whatsapp/threads/{channel}/{key}/send",
            "legacy sandbox send that creates the thread and skips the composer's cold-text and window checks — tools send through the composer's own routes (send_message)"),
        new("GET", "/api/messaging/contacts", "the composer's contact picker — same rows as GET /api/contacts (search_contacts)"),
        new("GET", "/api/v1/whoami", "REST v1 twin of GET /api/auth/whoami (me_get_profile)"),
        new("GET", "/api/v1/contacts", "REST v1 twin of GET /api/contacts (search_contacts)"),
        new("POST", "/api/v1/contacts", "REST v1 twin of POST /api/contacts (create_contact)"),
        new("GET", "/api/v1/contacts/{id:int}", "REST v1 twin of GET /api/contacts/{id} (get_contact)"),
        new("PATCH", "/api/v1/contacts/{id:int}", "REST v1 twin of PATCH /api/contacts/{id} (update_contact)"),
        new("GET", "/api/v1/brands", "legacy alias: brands ARE entities (entities_list)"),
        new("POST", "/api/v1/brands", "legacy alias: brands ARE entities (entities_create)"),
        new("GET", "/api/auth/audit", "legacy admin audit feed, superseded by GET /api/audit (activity_search_log)"),
        new("GET", "/api/auth/notifications", "legacy bell feed, twin of GET /api/notifications (notifications_list)"),
        new("POST", "/api/auth/notifications", "legacy bell mark-read, twin of POST /api/notifications/mark (notifications_mark)"),
        new("POST", "/api/auth/notifications/read", "legacy bell mark-read, twin of POST /api/notifications/mark (notifications_mark)"),
        new("POST", "/api/notifications/{id:int}/read", "per-row twin of POST /api/notifications/mark {ids, read} (notifications_mark)"),
        new("POST", "/api/notifications/{id:int}/unread", "per-row twin of POST /api/notifications/mark {ids, read} (notifications_mark)"),
        new("DELETE", "/api/notifications/{id:int}", "per-row twin of POST /api/notifications/delete {ids} (notifications_delete)"),
        new("POST", "/api/voice/stt-settings", "legacy verb alias of PUT /api/voice/stt-settings (voice_set_stt_provider)"),
        new("POST", "/api/voice/llm-settings", "legacy verb alias of PUT /api/voice/llm-settings (voice_set_llm_provider)"),

        // ---- testing aids ---------------------------------------------------
        new("POST", "/api/whatsapp/simulate",
            "testing aid that fakes an INBOUND customer message — an AI must never speak as a customer; the Test lab owns this"),
        new("*", "/api/testlab/", "the Test lab: scenario runs and demo data for trying ChatBranch, not operator work", Prefix: true),
    };

    /// <summary>The exclusion covering this endpoint, if any.</summary>
    public static Rule? For(string method, string route) => All.FirstOrDefault(r => r.Matches(method, route));
}
