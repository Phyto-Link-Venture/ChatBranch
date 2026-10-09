using System.Text.Json;

namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    private static IEnumerable<IChatBranchTool> ChannelTools()
    {
        // ---- channels / inboxes ---------------------------------------------------
        const string ch = "channels";
        var chView = C("channels.view");
        var chManage = C("channels.manage");
        var assignCap = C("account_assignment.manage");

        yield return T(ch, "channels_list_accounts", "List channel accounts",
            "Every connected number, mailbox, bot, page and widget (or one channel): name, channel, provider " +
            "(cloud / baileys / …), role (reminder / conversation), entity, health status. Secrets are never " +
            "included; without channels.view you get the picker shape (names only).",
            "GET", "/api/whatsapp/accounts", C("chats.view", "channels.view", "templates.manage"),
            new[] { A.Str("channel", "One channel, or all (default).") }, query: new() { ["channel"] = "all" },
            link: (_, _) => Page("Open channels", "/channels"));

        yield return T(ch, "channels_list_inbox_settings", "List inbox connection settings",
            "The managed inboxes (email, Telegram, Messenger, Instagram, LINE, Viber, SMS, WeChat, TikTok, Douyin, " +
            "Zalo) with their masked connection fields, webhook URLs and 'who answers it' settings (team, AI " +
            "default, AI review mode).", "GET", "/api/messaging/accounts", chView);

        yield return Custom(ch, "channels_test_account", "Test a channel account",
            "Probe one account against its provider now (Graph ping, getMe, SMTP login, …) and record its health.",
            new[] { "POST /api/whatsapp/accounts/{id:int}/test", "POST /api/messaging/accounts/{id}/test" },
            async (ctx, a, ct) =>
            {
                var id = I(a, "account_id") ?? throw new ToolException("'account_id' is required.");
                var channel = (S(a, "channel") ?? "whatsapp").Trim().ToLowerInvariant();
                var rv = new Dictionary<string, string> { ["id"] = id.ToString() };
                var r = channel == ChatBranch.Domain.Channels.WhatsApp
                    ? await ctx.CallAsync("POST", "/api/whatsapp/accounts/{id:int}/test", rv, ct: ct)
                    : await ctx.CallAsync("POST", "/api/messaging/accounts/{id}/test", rv, ct: ct);
                return Compact(ToolContext.Require(r).Body);
            }, chManage,
            new[]
            {
                A.Int("account_id", "The account id (channels_list_accounts).", req: true),
                A.Str("channel", "The account's channel (decides which test runs).", req: true, oneOf: EndpointTool.ChannelNames),
            }, outside: true, idempotent: true);

        yield return T(ch, "channels_register_webhook", "Register an inbox webhook",
            "Register ChatBranch's signed webhook with the provider for one inbox (Telegram setWebhook, LINE, " +
            "Viber set_webhook, …) using its stored credentials.", "POST", "/api/messaging/accounts/{id}/set-webhook",
            chManage, new[] { A.Id("account") }, outside: true, idempotent: true);

        yield return T(ch, "channels_sync_email", "Fetch new email now",
            "Pull new mail from every IMAP mailbox now (throttled to once per 30 s; force skips the throttle).",
            "POST", "/api/messaging/email/sync", C("chats.view", "channels.view"),
            new[] { A.Bool("force", "Ignore the 30-second throttle.") }, outside: true);

        yield return T(ch, "channels_get_health", "Channel health",
            "Health of every account (healthy / degraded / disconnected / banned / unknown), recent problem and " +
            "recovery events, per entity.", "GET", "/api/whatsapp/channels/health", C("analytics.view", "channels.view"),
            new[] { A.Entity() });

        yield return T(ch, "channels_check_health", "Check channel health now",
            "'Check now': probe every account in your scope against its provider and update health.",
            "POST", "/api/whatsapp/channels/health", chManage, new[] { A.Entity(@in: ArgIn.Query) }, outside: true);

        yield return T(ch, "channels_get_recovery", "Recovery overview",
            "For each conversation number: how many contacts are assigned to it — who would be affected if it went down.",
            "GET", "/api/whatsapp/recovery", C("account_assignment.manage", "analytics.view"));

        yield return T(ch, "channels_run_recovery", "Message contacts of a downed number",
            "Recovery: queue the recovery template to every contact assigned to this number (24-hour de-dup), so " +
            "they can reach you on a working one. Messages many customers — confirm first.",
            "POST", "/api/whatsapp/recovery/{accountId:int}", assignCap,
            new[] { A.Int("account_id", "The number that went down.", req: true, @as: "accountId") },
            destructive: true, outside: true);

        yield return T(ch, "channels_get_routing_rules", "Read routing rules",
            "Send-time routing knobs: ai_reply_mode (queued | instant), cold_text_guard / cold_call_guard " +
            "(block | warn | allow), reminder_ai_default, business_redirect (+ target/template), recovery_mode and " +
            "template, send pacing, extra opt-out / opt-in keywords.", "GET", "/api/whatsapp/routing-rules", assignCap,
            new[] { A.Entity() }, link: (_, _) => Page("Open account assignment", "/assignments"));

        yield return T(ch, "channels_set_routing_rules", "Change routing rules",
            "Set routing knobs (keys as channels_get_routing_rules returns them) for the organisation or an entity.",
            "PUT", "/api/whatsapp/routing-rules", assignCap,
            new[] { A.Entity(@in: ArgIn.Query), A.Obj("rules", "{key: value} to change.", req: true, @in: ArgIn.Spread) },
            idempotent: true);

        yield return T(ch, "channels_list_assignments", "List account assignments",
            "Sticky contact → number assignments for a channel (which number each contact is served from, pinned or not).",
            "GET", "/api/whatsapp/assignments", assignCap, new[] { A.Str("channel", "Channel (default whatsapp).") });

        yield return T(ch, "channels_set_assignment", "Assign a contact to a number",
            "Set (or clear, account_id \"\") which number serves a contact on a channel; pinned keeps rebalancing away.",
            "PUT", "/api/whatsapp/assignments", assignCap,
            new[]
            {
                A.Int("contact_id", "Contact.", req: true), A.Str("account_id", "Account id, or \"\" to clear.", req: true),
                A.Str("channel", "Channel (default whatsapp)."), A.Bool("pinned", "Pin it."),
            });

        yield return T(ch, "channels_rebalance_assignments", "Rebalance assignments",
            "Spread every UNPINNED assignment evenly over the eligible numbers of a channel (moves contacts).",
            "POST", "/api/whatsapp/assignments/rebalance", assignCap,
            new[] { A.Str("channel", "Channel (default whatsapp).") }, destructive: true);

        yield return T(ch, "channels_list_widgets", "List website chat widgets",
            "Website chat widgets: name, public token (it ships in the embed snippet — not a secret), look, entity, " +
            "conversation count.", "GET", "/api/widgets", chView, new[] { A.Entity() },
            options: new ToolResults.Options(KeepKeys: new HashSet<string> { "token" }));

        var widgetFields = new[]
        {
            A.Str("name", "Name."), A.Str("title", "Header title."), A.Str("greeting", "First message."),
            A.Str("accent", "Accent colour (#hex)."), A.Str("placeholder", "Input placeholder."),
            A.Str("ask_identity", "Ask visitors for name/email first (on/off)."),
            A.Any("team_id", "Team that answers new chats (null = none)."),
            A.Str("ai_default", "on | off | \"\" (inherit).", oneOf: new[] { "on", "off", "" }),
            A.Str("ai_review_mode", "auto | review_flagged | review_all | shadow | \"\" (inherit)."),
            A.Entity("Entity slug.", ArgIn.Body),
        };
        yield return T(ch, "channels_create_widget", "Create a website chat widget",
            "Create a website chat widget (returns its public token and embed details).", "POST", "/api/widgets", chManage,
            widgetFields, options: new ToolResults.Options(KeepKeys: new HashSet<string> { "token" }));
        yield return T(ch, "channels_update_widget", "Update a website chat widget",
            "Change a widget's look, entity or who answers it; rotate_token invalidates every old embed.",
            "PATCH", "/api/widgets/{id:int}", chManage,
            new[] { A.Id("widget"), A.Bool("rotate_token", "Issue a new public token.") }.Concat(widgetFields).ToArray(),
            options: new ToolResults.Options(KeepKeys: new HashSet<string> { "token" }));
        yield return T(ch, "channels_delete_widget", "Delete a website chat widget",
            "Delete a website chat widget — its embed stops working; its conversations stay in the inbox.", "DELETE", "/api/widgets/{id:int}", chManage,
            new[] { A.Id("widget") }, destructive: true);

        yield return T(ch, "channels_get_unknown_customers", "Read an inbox's unknown-customer setting",
            "What an inbox does with senders who aren't contacts yet: off | ask | ask_and_create, and the message used.",
            "GET", "/api/inboxes/{accountId:int}/identify", chView,
            new[] { A.Int("account_id", "The inbox (account) id.", req: true, @as: "accountId") });
        yield return T(ch, "channels_set_unknown_customers", "Set an inbox's unknown-customer setting",
            "off = do nothing; ask = ask the sender who they are and link a match; ask_and_create = also create " +
            "the contact when nobody matches. identify_message overrides the question.",
            "PUT", "/api/inboxes/{accountId:int}/identify", chManage,
            new[]
            {
                A.Int("account_id", "The inbox (account) id.", req: true, @as: "accountId"),
                A.Str("identify_unknown", "off | ask | ask_and_create | \"\" (inherit).", oneOf: new[] { "off", "ask", "ask_and_create", "" }),
                A.Str("identify_message", "Custom question."),
            }, idempotent: true);

        // ---- integrations ---------------------------------------------------------
        const string ig = "integrations";
        yield return T(ig, "integrations_list", "List integrations",
            "Connected apps (Google Calendar / Drive / Gmail / Sheets / Contacts, Shopify, Stripe…) and their state " +
            "for the organisation or an entity.", "GET", "/api/integrations", C("integrations.view"), new[] { A.Entity() },
            link: (_, _) => Page("Open integrations", "/integrations"));
        yield return T(ig, "integrations_list_providers", "List integration providers",
            "The providers you can connect, what each offers and whether it is configured.",
            "GET", "/api/integrations/providers", C("integrations.view"), new[] { A.Entity() });
        yield return T(ig, "integrations_list_connections", "List integration connections",
            "Connection rows (organisation, entity and personal) — credentials are never included. Without " +
            "integrations.view you see only your own.", "GET", "/api/integrations/accounts", null,
            new[] { A.Str("provider", "One provider."), A.Entity() });
        yield return T(ig, "integrations_list_my_connections", "List my connections",
            "Settings → My connections: the Google products you connect for yourself and your own grant of each " +
            "(a booking skill set to the assigned agent's calendar books into yours).",
            "GET", "/api/integrations/mine", link: (_, _) => Page("Open my connections", "/settings/connections"));
        yield return T(ig, "integrations_start_oauth", "Start connecting a Google product",
            "Get the Google authorize URL to connect a product for YOURSELF (or, as an admin with entity, for an " +
            "outlet). Give the URL to the person — they sign in with Google in their browser.",
            "GET", "/api/integrations/oauth/{provider}/start", null,
            new[]
            {
                A.Str("provider", "google_calendar, google_drive, gmail, google_sheets or google_contacts.", req: true),
                A.Entity("Connect a shared row for this entity (admin + integrations.manage)."),
                A.Str("return", "Where the browser lands afterwards: connections (default) or integrations.", oneOf: new[] { "connections", "integrations" }),
            }, query: new() { ["return"] = "connections" });
        yield return T(ig, "integrations_test_connection", "Test an integration connection",
            "Check a connection against its provider (token refresh, a cheap API call).",
            "POST", "/api/integrations/accounts/{id:int}/test", null, new[] { A.Id("connection") }, outside: true, idempotent: true);
        yield return T(ig, "integrations_disconnect", "Disconnect an integration",
            "Revoke a connection's grant (your own personal one, or a shared one with integrations.manage). Flows " +
            "and booking skills using it stop until it is reconnected.",
            "POST", "/api/integrations/accounts/{id:int}/disconnect", null, new[] { A.Id("connection") },
            destructive: true, outside: true);
        yield return T(ig, "integrations_delete_connection", "Delete an integration connection",
            "Delete an integration connection row entirely (admin) — flows and skills using it skip until reconnected.", "DELETE", "/api/integrations/accounts/{id:int}",
            C("integrations.manage"), new[] { A.Id("connection") }, destructive: true);
        yield return T(ig, "integrations_run_action", "Run an integration action",
            "Run one connected-app action now, as a flow step would: e.g. provider google_calendar action " +
            "'Find free slot' / 'Create event', google_sheets 'Append row', gmail 'Send email', shopify 'Look up " +
            "order', stripe 'Create payment link'. args are the action's fields. Skips cleanly when not connected.",
            "POST", "/api/integrations/action", C("integrations.manage", "flows.manage"),
            new[]
            {
                A.Str("provider", "Provider id.", req: true), A.Str("action", "Action label.", req: true),
                A.Obj("args", "The action's fields (strings)."), A.Entity(@in: ArgIn.Query),
            }, outside: true);
        yield return T(ig, "integrations_list_mcp_servers", "List external MCP servers",
            "External MCP servers Ask ChatBranch may use (masked auth), their tool allowlists and health.",
            "GET", "/api/integrations/mcp", C("api_keys.manage"));
        yield return T(ig, "integrations_test_mcp_server", "Test an external MCP server",
            "Connect to an external MCP server (initialize + tools/list) and record its health.",
            "POST", "/api/integrations/mcp/{id:int}/test", C("api_keys.manage"), new[] { A.Id("MCP server") },
            outside: true, idempotent: true);

        // ---- calls ------------------------------------------------------------------
        const string cl = "calls";
        yield return T(cl, "calls_dial", "Place an AI phone call",
            "Dial a contact (contact_id) or a number (E.164) over SIP with the AI voice agent; script = read this " +
            "text verbatim and hang up. Do-not-call and opt-outs apply; without LiveKit/SIP configured the answer " +
            "names what to set. Returns the room id for the live tools.",
            "POST", "/api/calls/dial", C("chats.send"),
            new[]
            {
                A.Int("contact_id", "Contact to call."), A.Str("phone", "Or an E.164 number."),
                A.Str("name", "Name for a number."), A.Str("language", "en | ms | zh."),
                A.Str("voice_id", "Voice (voice_list_voices).", @as: "voiceId"),
                A.Str("voice_provider", "Voice engine.", @as: "voiceProvider"),
                A.Str("script", "Read exactly this, then hang up (script call)."),
            }, outside: true);
        var room = A.Str("room", "The call's room id.", req: true);
        yield return T(cl, "calls_get_call", "Get a call",
            "One call: contact, phone, outcome, summary, sentiment, follow-up, transcript.",
            "GET", "/api/calls/{room}", C("chats.view", "call_logs.view"), new[] { room });
        yield return T(cl, "calls_get_live", "Read a live call",
            "The live transcript and state of a SIP call in progress.", "GET", "/api/calls/{room}/live",
            C("chats.view", "call_logs.view"), new[] { room });
        yield return T(cl, "calls_say", "Steer or speak on a live call",
            "guide = tell the AI what to do next; takeover = the AI says your text verbatim.",
            "POST", "/api/calls/{room}/say", C("chats.send"),
            new[] { room, A.Str("text", "Instruction or words.", req: true), A.Str("mode", "guide (default) | takeover.", oneOf: new[] { "guide", "takeover" }) });
        yield return T(cl, "calls_escalate", "Escalate a live call",
            "Ask the AI on a live SIP call to hand over to a person (it tells the caller).", "POST", "/api/calls/{room}/escalate", C("chats.send"),
            new[] { room });
        yield return T(cl, "calls_hang_up", "Hang up a call",
            "End a live SIP call (recorded as ended by the operator).", "POST", "/api/calls/{room}/hangup", C("chats.send"),
            new[] { room }, destructive: true, outside: true);
        yield return T(cl, "calls_list_logs", "List call logs",
            "Finished calls: who, when, outcome, summary, whether a recording exists.", "GET", "/api/call-logs",
            C("call_logs.view"), link: (_, _) => Page("Open call logs", "/call-logs"));

        // ---- voice library ---------------------------------------------------------
        const string vo = "voice";
        yield return T(vo, "voice_list_voices", "List voices",
            "The voice library the AI calls with (ids, names, gender, language, engine).", "GET", "/api/voices");
        yield return Custom(vo, "voice_get_providers", "Read voice engine settings",
            "Which speech-to-text and language model the voice agent uses.",
            new[] { "GET /api/voice/stt-settings", "GET /api/voice/llm-settings" }, async (ctx, a, ct) =>
            {
                var stt = ToolContext.Require(await ctx.CallAsync("GET", "/api/voice/stt-settings", ct: ct));
                var llm = ToolContext.Require(await ctx.CallAsync("GET", "/api/voice/llm-settings", ct: ct));
                return new { stt = Compact(stt.Body), llm = Compact(llm.Body) };
            }, write: false);
        yield return T(vo, "voice_set_stt_provider", "Set the voice speech-to-text",
            "Choose the speech-to-text engine the AI voice agent uses on calls (admin).", "PUT", "/api/voice/stt-settings", null,
            new[] { A.Str("stt_provider", "Provider id.", req: true) }, idempotent: true);
        yield return T(vo, "voice_set_llm_provider", "Set the voice language model",
            "Choose the language model the AI voice agent thinks with on calls (admin).", "PUT", "/api/voice/llm-settings", null,
            new[] { A.Str("llm_provider", "Provider id.", req: true) }, idempotent: true);
        yield return T(vo, "voice_import_voice", "Add a voice to the library",
            "Import a voice from the provider's library by its voice id.", "POST", "/api/voice/import", null,
            new[]
            {
                A.Str("voice_id", "Provider voice id.", req: true, @as: "voiceId"), A.Str("name", "Name."),
                A.Str("gender", "Gender."), A.Str("persona", "When to use it."),
            });
        yield return T(vo, "voice_update_voice", "Edit a voice",
            "Rename or describe a voice in the call voice library (admin).", "PATCH", "/api/voice/{voiceId}", null,
            new[]
            {
                A.Str("voice_id", "Voice id.", req: true, @as: "voiceId"), A.Str("name", "Name."), A.Str("gender", "Gender."),
                A.Str("accent", "Accent."), A.Str("age", "Age."), A.Str("description", "Description."),
                A.Str("language", "en | ms | zh."), A.Str("persona", "Persona."),
            });
        yield return T(vo, "voice_delete_voice", "Delete a voice", "Remove a voice from the call voice library (admin).",
            "DELETE", "/api/voice/{voiceId}", null, new[] { A.Str("voice_id", "Voice id.", req: true, @as: "voiceId") },
            destructive: true);
        yield return T(vo, "voice_get_pronunciation", "Read the pronunciation list",
            "How the voice agent pronounces brand names and terms ({term, alias}).", "GET", "/api/pronunciation");
        yield return T(vo, "voice_set_pronunciation", "Set the pronunciation list",
            "Replace the pronunciation list — pass every {term, alias} to keep (read it first).",
            "POST", "/api/pronunciation", C("pronunciation.manage"),
            new[] { A.Arr("terms", "[{term, alias}] — the complete list.", req: true) }, idempotent: true);
    }
}
