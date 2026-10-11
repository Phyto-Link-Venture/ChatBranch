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
            }, idempotent: true);

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
            "Send a file (image, video, audio, PDF, document) into a conversation, with an optional caption. " +
            "Pass the file as base64 (+ file_name) — WhatsApp needs the file itself; Messenger/Instagram/Telegram " +
            "also take a public media_url. Same guards as a typed message (opt-out, windows, cold-file guard).",
            new[] { "POST /api/whatsapp/threads/{key}/send-media", "POST " + MessagingSendMedia,
                "POST /api/messaging/threads/{channel}/{key}/send" },
            SendMediaAsync, send,
            new[]
            {
                A.Str("channel", "Conversation channel.", req: true, oneOf: EndpointTool.ChannelNames),
                A.Str("key", "Conversation key.", req: true),
                A.Str("file_base64", "The file, base64 encoded (max 16 MB on WhatsApp, 20 MB elsewhere)."),
                A.Str("file_name", "File name with extension (decides the type), e.g. invoice.pdf."),
                A.Str("media_url", "Instead of a file: a public URL (Messenger / Instagram / Telegram)."),
                A.Str("media_type", "With media_url: image | video | audio | file.", oneOf: new[] { "image", "video", "audio", "file" }),
                A.Str("caption", "Text sent with the file."),
                A.Str("subject", "Email subject."),
                A.Bool("force", "Send to an opted-out contact as a deliberate manual exception."),
            }, outside: true);

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
            "note explains what the new number may send). Admin only.",
            "POST", "/api/whatsapp/threads/{key}/account", send, thread: true, channels: new[] { "whatsapp" },
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
        // An existing conversation outside the caller's scope is not theirs to reopen.
        var key = channel is Channels.WhatsApp or Channels.Sms ? PhoneMatch.Digits(to) : to;
        var existing = await ctx.Db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Channel == channel && t.Key == key, ct);
        if (existing != null && !await ToolScope.CanSeeThreadAsync(ctx, existing))
            throw new ToolException($"No conversation {channel}:{key} in your scope.", 404);

        var r = channel == Channels.WhatsApp
            ? await ctx.CallAsync("POST", "/api/whatsapp/threads",
                body: new Dictionary<string, object?> { ["phone"] = to, ["name"] = S(a, "name"), ["account_id"] = S(a, "account_id") }, ct: ct)
            : await ctx.CallAsync("POST", "/api/messaging/threads",
                body: new Dictionary<string, object?> { ["channel"] = channel, ["to"] = to, ["name"] = S(a, "name") }, ct: ct);
        ToolContext.Require(r);
        var body = r.Body.TryGetProperty("thread", out var th) && th.ValueKind == JsonValueKind.Object ? th : r.Body;
        var outKey = body.TryGetProperty("key", out var k) ? k.ToString() : key;
        ctx.Links.Add(new ToolLink("Open the conversation", ToolResults.ChatLink(channel, outKey)));
        return new { channel, key = outKey, href = ToolResults.ChatLink(channel, outKey), created_or_found = true };
    }

    private static async Task<object?> SendMediaAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var t = await ToolScope.FindThreadAsync(ctx, Need(a, "channel"), Need(a, "key"));
        var isWa = t.Channel == Channels.WhatsApp;
        var rv = new Dictionary<string, string> { ["channel"] = t.Channel, ["key"] = t.Key };
        InternalApi.Response r;
        if (S(a, "file_base64") is { Length: > 0 })
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
                ct: ct, form: Form(fields, "file", name, bytes));
        }
        else if (S(a, "media_url") is { Length: > 0 } url)
        {
            if (isWa) throw new ToolException("WhatsApp needs the file itself — pass file_base64 and file_name.");
            r = await ctx.CallAsync("POST", "/api/messaging/threads/{channel}/{key}/send", rv,
                new Dictionary<string, object?>
                {
                    ["media_url"] = url,
                    ["media_type"] = S(a, "media_type") ?? "image",
                    ["text"] = S(a, "caption") ?? "",
                    ["subject"] = S(a, "subject"),
                    ["force"] = B(a, "force") == true,
                }, ct: ct);
        }
        else throw new ToolException("Pass file_base64 + file_name, or media_url.");
        ToolContext.Require(r);
        ctx.Links.Add(new ToolLink($"Open {(t.Name.Length > 0 ? t.Name : t.Key)}", ToolResults.ChatLink(t.Channel, t.Key)));
        var status = r.Body.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                     && m.TryGetProperty("status", out var st) ? st.ToString() : "";
        return new { sent = true, channel = t.Channel, key = t.Key, delivery_status = status,
            href = ToolResults.ChatLink(t.Channel, t.Key) };
    }
}
