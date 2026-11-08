using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    internal const string MessagingSendMedia =
        "/api/messaging/threads/{channel:regex(^(email|telegram|facebook|instagram|line|viber|sms|wechat|tiktok|douyin|zalo)$)}/{key}/send-media";

    private static readonly string[] InboxRowFields =
    {
        "channel", "key", "name", "address", "status", "assignee", "team_id", "team_name", "labels", "contact_id",
        "contact_name", "contact_tags", "entity", "account_id", "account_name", "ai_enabled", "ai_reason",
        "handover", "window_open", "window_expires_at", "opted_out", "waiting", "has_call", "last_message",
        "last_dir", "last_ts", "message_count",
    };

    private static ToolArg[] InboxFilters() => new[]
    {
        A.Str("q", "Text over contact name / phone / email, conversation name / address AND message text."),
        A.StrList("status", "Any of these statuses.", oneOf: new[] { "open", "pending", "snoozed", "resolved" }),
        A.StrList("channel", "Any of these channels.", oneOf: EndpointTool.ChannelNames),
        A.StrList("account", "Sending account ids; \"none\" = no account bound."),
        A.StrList("assignee", "me | unassigned | a username (any of)."),
        A.StrList("team", "Team ids; \"none\" = no owning team."),
        A.StrList("entity", "Entity slugs (each with its branches); default = your whole scope."),
        A.StrList("labels", "Conversation labels."),
        A.Str("labels_mode", "Match any (default) or all of the labels.", oneOf: new[] { "any", "all" }),
        A.StrList("tags", "Contact tags (any of)."),
        A.Str("ai", "Only conversations where the AI is on / off.", oneOf: new[] { "on", "off" }),
        A.Str("window", "Only conversations whose 24-hour reply window is open / closed.", oneOf: new[] { "open", "closed" }),
        A.Bool("opted_out", "true = only opted-out conversations; false = exclude them."),
        A.Bool("waiting", "Only conversations waiting for our reply (the customer spoke last)."),
        A.Str("call", "Conversations with any call / a live call.", oneOf: new[] { "any", "live" }),
        A.Bool("linked", "true = linked to a contact; false = unknown senders."),
        A.Str("handover", "open = handed to a person and not yet answered; any = ever handed over; none = never.",
            oneOf: new[] { "open", "any", "none" }),
        A.StrList("handover_kind", "Handover kinds (any of).", oneOf: new[]
        {
            "check_failed", "asked_for_human", "send_blocked", "booking_failed", "low_confidence", "rule", "manual",
            "unknown_customer",
        }),
        A.Str("active_from", "Last activity on/after (ISO date-time)."),
        A.Str("active_to", "Last activity before (ISO date-time, exclusive)."),
        A.Str("created_from", "Created on/after (ISO)."),
        A.Str("created_to", "Created before (ISO, exclusive)."),
        A.Str("sort", "newest (default, last activity) | customer_newest (the customer's last message) | oldest_waiting | unread.",
            oneOf: new[] { "newest", "customer_newest", "oldest_waiting", "unread" }),
        A.Limit(50, 200),
        A.Str("cursor", "next_cursor from the previous page."),
        A.Bool("count_only", "Only count the matches (the filter pop-up's \"Show N chats\")."),
    };

    private static IEnumerable<IChatBranchTool> Conversations()
    {
        const string g = "conversations";
        var view = C("chats.view");
        var send = C("chats.send");

        yield return T(g, "conversations_search", "Search the inbox",
            "The unified inbox list with every server-side filter the /chats page has: status, channel, account, " +
            "assignee, team, entity, labels, contact tags, AI on/off, 24-hour window, opt-out, waiting, calls, " +
            "linked, handover, activity/created ranges, sort and cursor paging (or count_only). Each row carries " +
            "its deep link. Use list_conversations for a quick overview.",
            "GET", "/api/inbox/threads", view, InboxFilters(),
            shape: s => Rows(s.Body, "threads", InboxRowFields, 200),
            link: (_, _) => Page("Open the inbox", "/chats"));

        yield return T(g, "conversations_list_needing_human", "List conversations needing a human",
            "The 'Needs human' tab: conversations the AI handed to a person (a failed check, the customer asked " +
            "for a human, a blocked send or booking, a rule, an unknown customer…) that nobody has answered yet.",
            "GET", "/api/inbox/threads", view,
            new[] { A.StrList("entity", "Entity slugs (each with its branches)."), A.Limit(50, 200), A.Str("cursor", "next_cursor from the previous page.") },
            query: new() { ["handover"] = "open" },
            shape: s => Rows(s.Body, "threads", InboxRowFields, 200),
            link: (_, _) => Page("Open Needs human", "/chats"));

        yield return T(g, "conversations_get_state", "Get a conversation's state",
            "One conversation's management state: status, assignee, team, labels, linked contact, sending account, " +
            "24-hour / social window, opt-out, the effective AI verdict (ai_enabled + which rule decided), the " +
            "open handover, call state — with its latest messages.",
            "GET", "/api/messaging/threads/{channel}/{key}", view, thread: true, wa: "/api/whatsapp/threads/{key}",
            args: new[] { A.Limit(20, 100) }, shape: ThreadStateShape);

        yield return T(g, "conversations_list_messages", "Page through messages",
            "Messages of one conversation, newest page first; pass before (a message id) for the page before it.",
            "GET", "/api/whatsapp/threads/{channel}/{key}/messages", view, thread: true,
            args: new[] { A.Limit(50, 200), A.Int("before", "Only messages older than this message id.") });

        yield return Custom(g, "conversations_start", "Start a conversation",
            "Open (or find) the conversation with an address on a channel so you can message it: a WhatsApp/SMS " +
            "number (E.164), an email address, a chat id. Sends nothing. On WhatsApp, account_id picks the " +
            "sending number (else routing chooses).",
            new[] { "POST /api/messaging/threads", "POST /api/whatsapp/threads" }, StartConversationAsync, send,
            new[]
            {
                A.Str("channel", "Channel.", req: true, oneOf: EndpointTool.ChannelNames),
                A.Str("to", "Phone (E.164), email or chat id.", req: true),
                A.Str("name", "Display name for a new conversation."),
                A.Str("account_id", "WhatsApp only: the number to talk from."),
                A.Int("thread_id", ToolScope.ThreadIdHelp),
                A.Str("company", ToolScope.CompanyHelp),
                A.Str("entity", "For a NEW conversation: the entity (slug) it belongs to — decides its company. Default: yours."),
            }, idempotent: true);

        yield return T(g, "conversations_notify", "Send a transactional message",
            "Send ONE message your app originated — a login code, an order update, a payment reminder — to an " +
            "address on a channel, in a single call: it finds or opens the conversation, picks the number to send " +
            "from, and falls back to a template when WhatsApp's 24-hour window is shut. An opt-out (STOP) always " +
            "refuses, with no override. purpose=\"otp\" skips the cold-text guard (the customer just asked for the " +
            "code); purpose=\"notification\" keeps it, so this is not a way to push cold marketing. Read " +
            "message.status for what actually happened: \"sandbox\" means nothing left the building. Pass " +
            "idempotency_key and a retry reads the same message back instead of sending twice.",
            "POST", "/api/messaging/notify", send,
            new[]
            {
                A.Str("channel", "Channel.", req: true, oneOf: EndpointTool.ChannelNames),
                A.Str("to", "Phone (E.164 or digits), email address or chat id.", req: true),
                A.Str("text", "What to send (required unless template is given)."),
                A.Str("purpose", "otp (skips the cold-text guard) or notification (the default).",
                    oneOf: new[] { "otp", "notification" }),
                A.Str("idempotency_key", "Your own key for this message; a retry with it sends nothing twice."),
                A.Str("name", "Display name, for a NEW conversation."),
                A.Str("subject", "Email only: the subject line."),
                A.Str("ai", "For a NEW conversation only: on | off (default: the inbox's own setting).",
                    oneOf: new[] { "on", "off" }),
                A.Obj("template", "WhatsApp only: {name, language, variables[], header_variables[], "
                    + "button_variables[], media_url} — used when the 24-hour window is shut."),
                A.Bool("template_fallback", "Switch to the template when the window is shut (default true)."),
                A.Str("account_id", "Preferred sending number; routing still picks a usable one."),
                A.Int("thread_id", ToolScope.ThreadIdHelp),
                A.Str("company", ToolScope.CompanyHelp),
                A.Str("entity", "For a NEW conversation: the entity (slug) it belongs to."),
            });

        yield return T(g, "conversations_send_template", "Send a WhatsApp template",
            "Send an approved WhatsApp template — the only way to message someone whose 24-hour window is " +
            "closed. variables fill {{1}}…{{n}} of the body; header/button variables and a media header URL " +
            "when the template has them. Opt-out and routing apply (templates go out on a Cloud number).",
            "POST", "/api/whatsapp/threads/{key}/send-template", send, thread: true, channels: new[] { "whatsapp" },
            outside: true,
            args: new[]
            {
                A.Str("name", "Template name (templates_list_whatsapp).", req: true),
                A.Str("language", "Template language code, e.g. en, en_US, ms."),
                A.StrList("variables", "Body parameters in order."),
                A.StrList("header_variables", "TEXT header parameters."),
                A.StrList("button_variables", "Dynamic URL button parameters."),
                A.Str("header_media_url", "Public URL of the header image/video/document."),
            });

        yield return Custom(g, "conversations_send_media", "Send a file or image",
            "Send a file (image, video, audio, PDF, document) into one or several conversations, with an " +
            "optional caption. PREFER A SHORT REFERENCE over the file itself: media_url (any https link, on " +
            "EVERY channel — the server fetches it, including a Google Drive share link) or " +
            "source_message_id (a file ChatBranch already has, resent to another conversation, across " +
            "channels). file_base64 still works and is the last resort: writing a megabyte of base64 costs " +
            "minutes and forces the file to be shrunk, which a link does not. `targets` sends ONE file to up " +
            "to 20 conversations in a single call, each with its own result. Same guards as a typed message " +
            "(opt-out, windows, cold-file guard). voice_note: true (WhatsApp, with file_base64) sends an " +
            "audio file as a real VOICE NOTE — the bubble with the waveform — instead of an audio " +
            "attachment: mp3 / m4a / webm / wav… are converted to Ogg/Opus first, and a voice note carries " +
            "no caption.",
            new[] { "POST /api/messaging/send-file", "POST /api/whatsapp/threads/{key}/send-media",
                "POST /api/whatsapp/threads/{key}/send-voice", "POST " + MessagingSendMedia,
                "POST /api/messaging/threads/{channel}/{key}/send" },
            SendMediaAsync, send,
            new[]
            {
                A.Str("channel", "Conversation channel (or use targets).", oneOf: EndpointTool.ChannelNames),
                A.Str("key", "Conversation key (or use targets)."),
                A.Arr("targets", "Several conversations: [{channel, key, thread_id?, company?, subject?}] — "
                    + "the file is fetched once and delivered to each, up to 20."),
                A.Int("thread_id", ToolScope.ThreadIdHelp),
                A.Str("company", ToolScope.CompanyHelp),
                A.Str("media_url", "An https link to the file — works on every channel; the server downloads "
                    + "it. A Google Drive share link is understood (a private file needs the Google account "
                    + "connected in ChatBranch)."),
                A.Int("source_message_id", "Resend a file ChatBranch already stores: the id of the message "
                    + "that carries it (works across channels, e.g. an email attachment into a WhatsApp chat)."),
                A.Str("file_id", "A file somebody uploaded to the media library (media_list finds them) — "
                    + "the way to send something that exists only on a phone and is nowhere online."),
                A.Str("file_base64", "The file itself, base64 encoded (max 16 MB on WhatsApp, 20 MB "
                    + "elsewhere). Slow and size-limited by what the model can write — prefer media_url."),
                A.Str("file_name", "File name with extension (decides the type), e.g. invoice.pdf. Required "
                    + "with file_base64; optional otherwise."),
                A.Str("caption", "Text sent with the file."),
                A.Bool("voice_note", "WhatsApp: send the audio file (file_base64 + file_name: ogg, opus, mp3, m4a, " +
                    "webm, wav, aac, amr, 3gp) as a voice note with the waveform, not as an audio attachment."),
                A.Str("subject", "Email subject."),
                A.Bool("force", "Send to an opted-out contact as a deliberate manual exception."),
            }, outside: true);

        yield return T(g, "conversations_send_sticker", "Send a WhatsApp sticker",
            "Send a picture as a real WhatsApp STICKER (no bubble, no caption) rather than as an image. Give " +
            "it the same short references as conversations_send_media — media_url, source_message_id, file_id " +
            "(the media library) or file_base64 — and the server shapes it: scaled and padded onto a " +
            "transparent 512x512 canvas, compressed until it fits WhatsApp's limit, and stamped with the pack " +
            "name. An animated GIF or a short clip becomes an animated sticker. QR-paired WhatsApp numbers " +
            "only: official Cloud/BSP numbers are refused with the reason, and the refusal says which kind " +
            "of no it is. Same guards as a typed message (opt-out, the 24-hour window, the cold-outbound " +
            "guard); read status, where \"sandbox\" means nothing left the building.",
            "POST", "/api/messaging/send-sticker", send,
            new[]
            {
                A.Str("channel", "Conversation channel — whatsapp.", req: true, oneOf: EndpointTool.ChannelNames),
                A.Str("key", "Conversation key (the customer's number).", req: true),
                A.Str("media_url", "An https link to the picture; the server downloads it. A Google Drive "
                    + "share link is understood."),
                A.Int("source_message_id", "Turn a picture ChatBranch already stores into a sticker: the id "
                    + "of the message carrying it."),
                A.Str("file_id", "A picture uploaded to the media library (media_list finds them)."),
                A.Str("file_base64", "The picture itself, base64 encoded — the last resort; prefer media_url."),
                A.Str("file_name", "File name with extension, e.g. logo.png. Required with file_base64."),
                A.Str("pack", "The sticker pack's name, shown in WhatsApp's sticker info."),
                A.Str("author", "The pack's publisher (default: the business name)."),
                A.StrList("emojis", "Up to 3 emoji the sticker can be found by."),
                A.Int("thread_id", ToolScope.ThreadIdHelp),
                A.Str("company", ToolScope.CompanyHelp),
                A.Bool("force", "Send to an opted-out contact as a deliberate manual exception."),
            });

        yield return T(g, "conversations_set_ai", "Switch the AI on/off for a conversation",
            "The per-chat AI tick (per sending number on WhatsApp). A rule above it — the master switch or " +
            "schedule, the contact's 'never', a never-AI tag, an opt-out — still wins; conversations_get_state " +
            "shows the effective verdict.",
            "POST", "/api/messaging/threads/{channel}/{key}/ai", send, thread: true, wa: "/api/whatsapp/threads/{key}/ai",
            args: new[] { A.Bool("enabled", "true = AI on, false = AI off.", req: true) }, idempotent: true,
            shape: s => new { channel = s.Thread!.Channel, key = s.Thread.Key, ai_tick = B(s.Args, "enabled") });

        yield return T(g, "conversations_set_labels", "Set conversation labels",
            "Replace a conversation's labels (lower-cased, de-duplicated, max 20). Pass every label it should keep.",
            "POST", "/api/messaging/threads/{channel}/{key}/labels", send, thread: true,
            wa: "/api/whatsapp/threads/{key}/labels",
            args: new[] { A.StrList("labels", "The complete label list ([] clears).", req: true) }, idempotent: true,
            shape: s => new { channel = s.Thread!.Channel, key = s.Thread.Key, labels = ChatBranchTool.StrList(s.Args, "labels") });

        yield return T(g, "conversations_react_to_message", "React to a message with an emoji",
            "Put an emoji reaction on one message of a WhatsApp or Telegram conversation (pk = the message's pk " +
            "from conversations_list_messages / conversations_get_state); emoji \"\" removes yours, a new one " +
            "replaces it. WhatsApp Cloud only accepts it inside the 24-hour window; an opted-out conversation " +
            "needs force. A reaction never creates a message.",
            "POST", "/api/messaging/threads/{channel}/{key}/messages/{pk:int}/react", send, thread: true,
            args: new[]
            {
                A.Int("pk", "The message's pk.", req: true),
                A.Str("emoji", "One emoji, e.g. 👍 — \"\" removes your reaction.", req: true),
                A.Bool("force", "Override an opt-out (a manual, audited exception)."),
            }, idempotent: true);

        yield return T(g, "conversations_recall_message", "Delete a sent message for everyone",
            "WhatsApp's \"delete for everyone\" on a message the shop SENT (pk from " +
            "conversations_list_messages): it disappears from the customer's phone too. QR-paired numbers only — " +
            "Meta's Cloud API has no delete and says so — and only within about two days of sending. What is left " +
            "here follows the inbox's \"keep messages the customer deletes\" switch. This cannot be undone.",
            "POST", "/api/messaging/threads/{channel}/{key}/messages/{pk:int}/recall", send, thread: true,
            args: new[] { A.Int("pk", "The message's pk.", req: true) }, destructive: true);

        yield return T(g, "conversations_edit_message", "Edit a sent message",
            "Change the wording of a TEXT message the shop sent (pk from conversations_list_messages). The customer " +
            "sees the new wording marked as edited; the old one stays in the activity log. QR-paired numbers only, " +
            "and only within about 15 minutes of sending — past that, send a correction or delete it for everyone.",
            "POST", "/api/messaging/threads/{channel}/{key}/messages/{pk:int}/edit", send, thread: true,
            args: new[]
            {
                A.Int("pk", "The message's pk.", req: true),
                A.Str("text", "The corrected wording (the whole message, not a patch).", req: true),
            });

        yield return T(g, "conversations_forward_message", "Forward a message to another chat",
            "Forward one message (pk from conversations_list_messages) into ANOTHER conversation on the same " +
            "channel, with WhatsApp's own \"Forwarded\" marking. QR-paired numbers only, and only while the " +
            "gateway still holds the original (the recent ones) — otherwise it refuses rather than re-sending the " +
            "content unmarked, which would be a different thing. The target must be a conversation you can see.",
            "POST", "/api/messaging/threads/{channel}/{key}/messages/{pk:int}/forward", send, thread: true,
            args: new[]
            {
                A.Int("pk", "The message's pk.", req: true),
                A.Str("to_channel", "The target conversation's channel (the same one).", req: true),
                A.Str("to_key", "The target conversation's key.", req: true),
                A.Int("to_thread_id", "Its conversation id, when the customer talks to several companies."),
                A.Bool("force", "Override the target's opt-out (a manual, audited exception)."),
            });

        yield return T(g, "conversations_send_card", "Send a location, contact card or poll",
            "The message kinds beyond text and files: kind \"location\" (lat + lng, optionally named), " +
            "\"contact\" (a vCard: name + phones) or \"poll\" (a question and 2–12 options; " +
            "selectable_count above 1 lets the customer pick several). QR-paired WhatsApp numbers; an official " +
            "number is told what it can and cannot carry — its API has no polls at all.",
            "POST", "/api/messaging/threads/{channel}/{key}/send-card", send, thread: true,
            args: new[]
            {
                A.Str("kind", "location | contact | poll.", req: true, oneOf: new[] { "location", "contact", "poll" }),
                A.Num("lat", "location: latitude (-90..90)."),
                A.Num("lng", "location: longitude (-180..180)."),
                A.Str("name", "location: the place's name · contact: the person's name."),
                A.Str("address", "location: a street address."),
                A.StrList("phones", "contact: one or more phone numbers (the first is shown)."),
                A.Str("organization", "contact: a company name."),
                A.Str("question", "poll: the question."),
                A.StrList("options", "poll: 2–12 answers (duplicates are dropped)."),
                A.Int("selectable_count", "poll: how many a customer may pick (default 1)."),
                A.Bool("force", "Override an opt-out (a manual, audited exception)."),
            });

        yield return T(g, "conversations_link_contact", "Link a conversation to a contact",
            "Attach the conversation to a contact (contact_id), or unlink it (contact_id null). Linking moves the " +
            "chat to the contact's entity and closes an unknown-customer handover.",
            "POST", "/api/messaging/threads/{channel}/{key}/link", send, thread: true,
            args: new[] { A.Any("contact_id", "Contact id, or null to unlink.", req: true) }, idempotent: true);

        yield return T(g, "conversations_handover", "Take over or hand back a conversation",
            "Human handover: take = you answer from now on (the AI stands down); continue = let the AI carry on " +
            "and close the handover; flag = mark it for a person (kind manual) with a reason.",
            "POST", "/api/inbox/threads/{channel}/{key}/handover", send, thread: true,
            args: new[]
            {
                A.Str("action", "take | continue | flag.", req: true, oneOf: new[] { "take", "continue", "flag" }),
                A.Str("reason", "Why (shown in the note and the Needs-human tab)."),
                A.Bool("stand_down", "flag only: also switch the AI off (default true)."),
            });

        yield return T(g, "conversations_bulk_update", "Bulk-update conversations",
            "Apply one action to many conversations: ai_on | ai_off | resolve | reopen | assign (value = " +
            "username, \"\" to unassign). Pick them by threads [\"channel:key\", …] or by filter (the same keys " +
            "as conversations_search). Max 1000, scoped like the inbox. Destructive — confirm first.",
            "POST", "/api/inbox/threads/bulk", C("chats.manage"),
            new[]
            {
                A.Str("action", "What to do.", req: true, oneOf: new[] { "ai_on", "ai_off", "resolve", "reopen", "assign" }),
                A.StrList("threads", "Conversations as \"channel:key\"."),
                A.Obj("filter", "Instead of threads: an inbox filter, e.g. {\"status\":\"open\",\"labels\":\"vip\"}."),
                A.Str("value", "assign: the username (\"\" = unassign)."),
            }, destructive: true);

        yield return T(g, "conversations_get_summary", "Read a conversation summary",
            "The stored case summary of a conversation (what happened, open issues, next step), if one was made.",
            "GET", "/api/messaging/threads/{channel}/{key}/summary", view, thread: true);

        yield return T(g, "conversations_summarize", "Summarize a conversation",
            "Write (or refresh) the conversation's case summary with the AI and store it; heuristic without a model.",
            "POST", "/api/messaging/threads/{channel}/{key}/summary", view, thread: true, outside: true, idempotent: true);

        yield return T(g, "conversations_suggest_reply", "Suggest an AI reply",
            "Draft the reply the AI would send now (persona, knowledge, skills, lessons and contact memory " +
            "applied) WITHOUT sending it or booking anything — for you to review, edit and send with send_message.",
            "POST", "/api/aicore/reply", send, thread: true, write: false, outside: true,
            shape: s => Compact(s.Body));

        yield return T(g, "conversations_switch_account", "Switch the sending number",
            "Move a WhatsApp conversation onto another of your numbers (pins the contact's assignment; a system " +
            "note explains what the new number may send). Needs account_assignment.manage.",
            "POST", "/api/whatsapp/threads/{key}/account", C("account_assignment.manage"), thread: true, channels: new[] { "whatsapp" },
            args: new[] { A.Str("account_id", "The number's account id (channels_list_inboxes).", req: true) },
            shape: ThreadStateShape);

        yield return T(g, "conversations_get_identity_request", "Get the unknown-customer state",
            "For a conversation from an unknown sender: where the identity exchange stands (asked, matches " +
            "found, linked, created, gave up) — what the identity banner in the chat shows.",
            "GET", "/api/contacts/identify/{channel}/{key}", view, thread: true);

        yield return T(g, "conversations_create_contact_from_unknown", "Create or link the contact for an unknown sender",
            "Resolve an unknown sender by hand: link an existing contact (contact_id) or create one from the " +
            "details given — closes the identity request and its handover, like the chat banner's 'Create contact'.",
            "POST", "/api/contacts/identify/{channel}/{key}/contact", C("contacts.manage"), thread: true,
            args: new[]
            {
                A.Int("contact_id", "Link this existing contact instead of creating one."),
                A.Str("name", "New contact's name."),
                A.Str("phone", "E.164 phone."),
                A.Str("email", "Email."),
                A.Str("language", "en, ms, zh…"),
                A.Entity("Entity slug for the new contact."),
            });

        // ---- WhatsApp calls in a conversation -------------------------------
        var wa = new[] { "whatsapp" };
        yield return T(g, "conversations_start_call", "Start an AI voice call on WhatsApp",
            "Call the customer on WhatsApp (QR number) with the AI voice agent. Opt-out, do-not-call and the " +
            "cold-call guard apply; without LiveKit configured the answer says what to set.",
            "POST", "/api/whatsapp/threads/{key}/call/start", send, thread: true, channels: wa, outside: true,
            args: new[]
            {
                A.Str("voice_provider", "Voice engine override (openai, elevenlabs, ilmu…)."),
                A.Str("voice_id", "Voice id (voice_list_voices)."),
                A.Bool("force", "Admin override of the cold-call guard."),
            });

        yield return T(g, "conversations_end_call", "End the WhatsApp call",
            "Hang up the live WhatsApp call in this conversation (recorded as ended by the operator).",
            "POST", "/api/whatsapp/threads/{key}/call/end", send, thread: true, channels: wa, destructive: true,
            outside: true);

        yield return T(g, "conversations_call_say", "Steer or speak on the WhatsApp call",
            "During the live WhatsApp call: guide = tell the AI what to do next (it phrases it); takeover = the " +
            "AI says your text verbatim.",
            "POST", "/api/whatsapp/threads/{key}/call/say", send, thread: true, channels: wa,
            args: new[]
            {
                A.Str("text", "The instruction or the words to say.", req: true),
                A.Str("mode", "guide (default) | takeover.", oneOf: new[] { "guide", "takeover" }),
            });

        yield return T(g, "conversations_call_escalate", "Escalate the WhatsApp call",
            "Ask the AI on the live WhatsApp call to hand over to a person (it tells the customer and flags the chat).",
            "POST", "/api/whatsapp/threads/{key}/call/escalate", send, thread: true, channels: wa);

        yield return T(g, "conversations_get_live_call", "Read the live WhatsApp call",
            "The live transcript and state of the WhatsApp call in this conversation.",
            "GET", "/api/whatsapp/threads/{key}/call/live", view, thread: true, channels: wa);

        yield return T(g, "conversations_list_call_recordings", "List WhatsApp call recordings",
            "Recordings (with their transcripts) of WhatsApp calls in this conversation.",
            "GET", "/api/whatsapp/threads/{key}/call/recordings", view, thread: true, channels: wa);
    }

    /// <summary>A thread payload without its message wall: the state plus the latest few.</summary>
    private static object? ThreadStateShape(ShapeInput s)
    {
        var node = JsonNode.Parse(s.Body.GetRawText());
        var root = node is JsonObject o && o["thread"] is JsonObject inner ? inner : node as JsonObject;
        if (root == null) return ToolResults.Compact(s.Body);
        var keep = Math.Clamp(I(s.Args, "limit") ?? 20, 1, 100);
        var holder = node is JsonObject top && top["messages"] is JsonArray ? top : root;
        if (holder["messages"] is JsonArray msgs)
        {
            var recent = new JsonArray();
            foreach (var m in msgs.Skip(Math.Max(0, msgs.Count - keep)).ToList())
            {
                msgs.Remove(m);
                if (m is JsonObject mo)
                {
                    var slim = new JsonObject();
                    foreach (var f in new[] { "id", "pk", "direction", "dir", "source", "private", "text", "status",
                                 "media_type", "mediaType", "ts", "createdAt", "created_at", "account_name", "via" })
                        if (mo[f] is { } v) slim[f] = v.DeepClone();
                    recent.Add(slim);
                }
            }
            holder["messages"] = recent;
        }
        return ToolResults.Compact(JsonSerializer.SerializeToElement(node));
    }

    private static async Task<object?> StartConversationAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var channel = Need(a, "channel").ToLowerInvariant();
        var to = Need(a, "to");
        var key = channel is Channels.WhatsApp or Channels.Sms ? PhoneMatch.Digits(to) : to;
        // the endpoints resolve the caller's OWN conversation (one per customer per company):
        // out of scope = not found, several = thread_id / company say which
        var r = channel == Channels.WhatsApp
            ? await ctx.CallAsync("POST", "/api/whatsapp/threads",
                body: new Dictionary<string, object?>
                {
                    ["phone"] = to, ["name"] = S(a, "name"), ["account_id"] = S(a, "account_id"),
                    ["thread_id"] = I(a, "thread_id"), ["company"] = S(a, "company"), ["entity"] = S(a, "entity"),
                }, ct: ct)
            : await ctx.CallAsync("POST", "/api/messaging/threads",
                body: new Dictionary<string, object?>
                {
                    ["channel"] = channel, ["to"] = to, ["name"] = S(a, "name"),
                    ["thread_id"] = I(a, "thread_id"), ["company"] = S(a, "company"), ["entity"] = S(a, "entity"),
                }, ct: ct);
        // an existing conversation outside the caller's scope is not theirs to reopen
        if (r.Status == 404) throw new ToolException($"No conversation {channel}:{key} in your scope.", 404);
        ToolContext.Require(r);
        var body = r.Body.TryGetProperty("thread", out var th) && th.ValueKind == JsonValueKind.Object ? th : r.Body;
        var outKey = body.TryGetProperty("key", out var k) ? k.ToString()
            : body.TryGetProperty("wa_id", out var w) ? w.ToString() : key;
        int? threadId = body.TryGetProperty("thread_id", out var tid) && tid.TryGetInt32(out var tidv) ? tidv : null;
        ctx.Links.Add(new ToolLink("Open the conversation", ToolResults.ChatLink(channel, outKey, threadId)));
        return new
        {
            channel, key = outKey, thread_id = threadId, href = ToolResults.ChatLink(channel, outKey, threadId),
            created_or_found = true,
        };
    }

    private static async Task<object?> SendMediaAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        // A link, several targets, or a file ChatBranch already holds: all three go
        // through POST /api/messaging/send-file, which fetches or reads the file ONCE
        // and answers per target. A single-conversation file_base64 keeps the older
        // multipart path, byte for byte, so nothing about today's calls changes.
        // A VOICE NOTE is not one of those: it needs the audio itself (it is re-containered
        // to Ogg/Opus before it goes out), so it never takes the short-reference path —
        // otherwise a media_url would quietly send a plain audio attachment instead.
        var hasTargets = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("targets", out var tg)
                         && tg.ValueKind == JsonValueKind.Array && tg.GetArrayLength() > 0;
        if (B(a, "voice_note") != true
            && (hasTargets || S(a, "media_url") is { Length: > 0 } || S(a, "file_id") is { Length: > 0 }
                || I(a, "source_message_id") != null))
            return await SendFileAsync(ctx, a, ct);

        var t = await ToolScope.FindThreadAsync(ctx, Need(a, "channel"), Need(a, "key"), I(a, "thread_id"), S(a, "company"));
        var isWa = t.Channel == Channels.WhatsApp;
        var rv = new Dictionary<string, string> { ["channel"] = t.Channel, ["key"] = t.Key };
        InternalApi.Response r;
        if (B(a, "voice_note") == true)
        {
            // a real voice note (ptt + Ogg/Opus) — the route the dashboard's microphone uses,
            // which re-containers any other recording with ffmpeg (503 naming it when absent)
            if (!isWa) throw new ToolException("voice_note is WhatsApp only — on other channels send the audio as a file.");
            if (S(a, "file_base64") is not { Length: > 0 })
                throw new ToolException("voice_note needs the audio itself — pass file_base64 and file_name (e.g. reply.mp3).");
            if (S(a, "caption") is { Length: > 0 })
                throw new ToolException("A WhatsApp voice note carries no caption — send the text as its own message.");
            var bytes = Base64(a, "file_base64", 16L * 1024 * 1024);
            var name = S(a, "file_name") is { Length: > 0 } fn ? fn : "voice.ogg";
            if (!Path.HasExtension(name)) throw new ToolException("file_name needs an extension (e.g. reply.mp3) — it decides the audio format.");
            var fields = new Dictionary<string, string?> { ["force"] = B(a, "force") == true ? "true" : null };
            r = await ctx.CallAsync("POST", "/api/whatsapp/threads/{key}/send-voice", rv,
                query: ToolScope.ThreadQuery(t), ct: ct, form: Form(fields, "file", name, bytes));
        }
        else if (S(a, "file_base64") is { Length: > 0 })
        {
            var bytes = Base64(a, "file_base64", isWa ? 16L * 1024 * 1024 : 20L * 1024 * 1024);
            var name = S(a, "file_name") is { Length: > 0 } fn ? fn : "file";
            if (!Path.HasExtension(name)) throw new ToolException("file_name needs an extension (e.g. photo.jpg) — it decides the file type.");
            var fields = new Dictionary<string, string?>
            {
                ["caption"] = S(a, "caption"),
                ["subject"] = isWa ? null : S(a, "subject"),
                ["force"] = B(a, "force") == true ? "true" : null,
            };
            if (!isWa && t.Channel is Channels.Website)
                throw new ToolException("The website chat takes text only.");
            r = await ctx.CallAsync("POST", isWa ? "/api/whatsapp/threads/{key}/send-media" : MessagingSendMedia, rv,
                query: ToolScope.ThreadQuery(t), ct: ct, form: Form(fields, "file", name, bytes));
        }
        else throw new ToolException(
            "Pass media_url (an https link — best), source_message_id (a file ChatBranch already has), "
            + "or file_base64 + file_name.");
        ToolContext.Require(r);
        ctx.Links.Add(new ToolLink($"Open {(t.Name.Length > 0 ? t.Name : t.Key)}", ToolResults.ChatLink(t.Channel, t.Key, t.Id)));
        var status = r.Body.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                     && m.TryGetProperty("status", out var st) ? st.ToString() : "";
        return new { sent = true, channel = t.Channel, key = t.Key, thread_id = t.Id, delivery_status = status,
            href = ToolResults.ChatLink(t.Channel, t.Key, t.Id) };
    }

    /// <summary>
    /// The short-reference path: POST /api/messaging/send-file carries the file to every
    /// target and answers one result each, so a refusal on one conversation never costs
    /// the others. The tool passes the arguments straight through — the endpoint owns the
    /// rules — and turns each result into a chat link the operator can open.
    /// </summary>
    private static async Task<object?> SendFileAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["channel"] = S(a, "channel"),
            ["key"] = S(a, "key"),
            ["thread_id"] = I(a, "thread_id"),
            ["company"] = S(a, "company"),
            ["media_url"] = S(a, "media_url"),
            ["file_id"] = S(a, "file_id"),
            ["source_message_id"] = I(a, "source_message_id"),
            ["file_base64"] = S(a, "file_base64"),
            ["file_name"] = S(a, "file_name"),
            ["caption"] = S(a, "caption"),
            ["subject"] = S(a, "subject"),
            ["force"] = B(a, "force") == true,
        };
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("targets", out var targets)
            && targets.ValueKind == JsonValueKind.Array)
            body["targets"] = targets;

        var r = await ctx.CallAsync("POST", "/api/messaging/send-file", new Dictionary<string, string>(),
            body, ct: ct);
        ToolContext.Require(r);
        if (r.Body.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var row in results.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var channel = row.TryGetProperty("channel", out var c) ? c.GetString() ?? "" : "";
                var key = row.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                var id = row.TryGetProperty("thread_id", out var tid) && tid.TryGetInt32(out var n) ? n : (int?)null;
                if (channel.Length > 0 && key.Length > 0)
                    ctx.Links.Add(new ToolLink($"Open {key}", ToolResults.ChatLink(channel, key, id)));
            }
        return r.Body;
    }
}
