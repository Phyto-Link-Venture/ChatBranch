namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    /// <summary>
    /// Managed connections (Zernio, never named to users): the WhatsApp / Messenger / Instagram / Telegram /
    /// SMS inboxes connected without handling platform credentials — list, connect, move, pick the Facebook
    /// Page, disconnect, phone numbers. Tool names keep their historical <c>social_</c> prefix (renaming
    /// would break MCP clients); ChatBranch has no social-media posting, comments or reviews.
    /// </summary>
    private static IEnumerable<IChatBranchTool> ManagedConnections()
    {
        const string g = "channels";
        var manage = C("channels.manage");

        yield return T(g, "social_list_accounts", "List managed messaging accounts",
            "The messaging accounts connected as managed connections (WhatsApp, Messenger, Instagram DM, Telegram, SMS) " +
            "for an entity: id (the connection's account id), channel, username / number, the outlet it is bound to, " +
            "whether it is active, and for Messenger the Facebook Pages (which one reaches the inbox).",
            "GET", "/api/social/accounts", C("channels.view"),
            new[] { A.Entity(), A.Bool("refresh", "Reconcile with the connection service first.") },
            link: (_, _) => Page("Open channels", "/channels"));

        yield return T(g, "social_connect_url", "Start connecting an account",
            "Start a managed connection for one messaging account, for an entity (or the organization): WhatsApp, " +
            "Messenger (facebook), Instagram, Telegram or SMS becomes an inbox of that channel. The answer says what comes next: flow " +
            "\"hosted\" = a sign-in link (url) to give the person; Telegram = an access code with steps (status_url " +
            "polls it — social_telegram_status); SMS = the outlet's numbers to choose from (social_connect_sms). " +
            "WhatsApp also needs onboarding: it decides whether the number keeps working in the WhatsApp Business " +
            "app, so ask the person and never choose for them — without it the answer is a 400 listing both " +
            "options with their consequences.",
            "POST", "/api/social/connect", manage,
            new[]
            {
                A.Str("platform", "whatsapp, facebook (Messenger), instagram, telegram or sms.", req: true),
                A.Str("onboarding", "WhatsApp only, and the person must choose: \"api\" = WhatsApp Business Platform " +
                                    "only (the number STOPS working in the WhatsApp Business app and the phone's " +
                                    "history does not come across), or \"business_app\" = coexistence (it keeps " +
                                    "working there, someone scans a QR code, and the API side is capped at 20 " +
                                    "messages/second with no groups, calls, disappearing or view-once messages)."),
                A.Str("signup", "WhatsApp only: \"hosted\" (the default — the connect page picks the number the " +
                                "person chose) or \"meta\" (Meta's own dialog)."),
                A.Entity("The entity slug that gets the account; omit for the organization."),
            }, outside: true);

        yield return T(g, "social_list_phone_numbers", "List managed phone numbers and prices",
            "The phone numbers an outlet's managed connection owns — id, number, country, status and " +
            "monthly_cents, the recurring monthly cost — plus the live per-country price of a NEW number, so the " +
            "cost is known before anyone buys. Buying a number and releasing one are deliberately NOT tools: both " +
            "spend the owner's money (a release cannot be undone), so a person does them in the dashboard.",
            "GET", "/api/social/phone-numbers", manage,
            new[]
            {
                A.Entity("The outlet whose numbers to list; omit for the organization."),
                A.Str("country", "Quote one country only (two-letter code, US / MY)."),
            });

        yield return T(g, "social_telegram_status", "Check a Telegram connection code",
            "Whether the access code from social_connect_url (platform telegram) has been sent to the bot yet: " +
            "pending, expired, or connected (the Telegram inbox exists from then on).",
            "GET", "/api/social/connect/telegram/status", manage,
            new[]
            {
                A.Str("code", "The access code social_connect_url returned.", req: true),
                A.Entity("The same entity the connect named."),
            });

        yield return T(g, "social_connect_telegram_chat", "Connect a Telegram group or channel by id",
            "Connect a Telegram group or channel whose admins already include the connection bot, by its chat id " +
            "(a group's negative id, or @channel). It becomes a Telegram inbox.",
            "POST", "/api/social/connect/telegram", manage,
            new[]
            {
                A.Str("chat_id", "The chat id (-100…) or @channelname.", req: true),
                A.Entity("The entity slug that gets the inbox; omit for the organization."),
            }, outside: true);

        yield return T(g, "social_connect_sms", "Turn on SMS for a number",
            "Turn SMS on for one of the outlet's phone numbers (social_connect_url with platform sms lists them) so " +
            "it becomes an SMS inbox. A US number receives at once but sends only after its carrier registration.",
            "POST", "/api/social/connect/sms", manage,
            new[]
            {
                A.Str("phone_number_id", "The number's id from the list.", req: true),
                A.Entity("The entity slug that gets the inbox; omit for the organization."),
            }, outside: true);

        yield return T(g, "social_assign_account", "Assign a managed messaging account to an outlet",
            "Bind a managed messaging inbox to an entity (outlet), or back to the organization level: it moves to " +
            "that entity's connection profile, and its existing conversations follow. " +
            "Organization-level accounts are usable from every outlet; an outlet's own accounts from that outlet only.",
            "PATCH", "/api/social/accounts/{id}", manage,
            new[]
            {
                A.Str("id", "The account id (social_list_accounts, or zernio_account_id on an inbox row).", req: true),
                A.Any("entity", "The entity slug (or id) to bind it to; null or \"\" for the organization level.", req: true),
            }, outside: true, idempotent: true);

        yield return T(g, "social_select_page", "Choose the Facebook Page for the inbox",
            "Switch which Facebook Page of a managed Messenger account reaches the inbox (the selected Page).",
            "POST", "/api/social/accounts/{id}/pages/{pageId}/select", manage,
            new[]
            {
                A.Str("id", "The Messenger account's id (social_list_accounts).", req: true),
                A.Str("pageId", "The Facebook Page id (the account's pages[].id).", req: true),
            }, outside: true, idempotent: true);

        yield return T(g, "social_disconnect_account", "Disconnect a managed messaging account",
            "Disconnect a managed connection (a WhatsApp / Messenger / Instagram / Telegram / SMS inbox connected " +
            "that way) and remove its inbox here (its conversations stay). The " +
            "platform grant is revoked — ask the person first.", "DELETE", "/api/social/accounts/{id}", manage,
            new[] { A.Str("id", "The account id (social_list_accounts, or zernio_account_id on an inbox row).", req: true) },
            destructive: true, outside: true);
    }
}
