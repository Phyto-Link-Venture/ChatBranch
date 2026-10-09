namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    private static IEnumerable<IChatBranchTool> Ai()
    {
        const string g = "ai";
        var monitor = C("ai.monitor");
        var behaviour = C("ai.behaviour.manage");
        var monitorOrBehaviour = C("ai.monitor", "ai.behaviour.manage");
        var draftId = A.Id("AI draft");

        // ---- AI monitor -----------------------------------------------------
        yield return T(g, "ai_list_drafts", "List AI replies (AI monitor)",
            "The AI monitor: every AI-written reply with its decision — waiting for review, auto-sent, approved, " +
            "edited, rejected, expired, blocked — why it was held, what it was grounded on, and any calendar " +
            "bookings it reserved. Filter by status/channel/kind/text/conversation.",
            "GET", "/api/ai/monitor", monitor,
            new[]
            {
                A.Str("status", "e.g. pending (waiting for review), auto_sent, approved, edited, rejected, expired, blocked, shadow; all = every one."),
                A.Str("channel", "Channel."),
                A.Entity(),
                A.Str("kind", "reply | followup | flow …"),
                A.Str("q", "Text in the draft or the customer's message."),
                A.Str("thread", "One conversation as channel:key."),
                A.Str("sort", "newest (default) | oldest.", oneOf: new[] { "newest", "oldest" }),
                A.Page, A.PageSize(100),
            }, link: (_, _) => Page("Open the AI monitor", "/ai-monitor"));

        yield return T(g, "ai_get_draft", "Get an AI draft",
            "One AI reply in full: the customer's message, the draft, the checks that ran, retries, grounding " +
            "(knowledge, lessons, skills it used), reserved bookings and your own feedback.",
            "GET", "/api/ai/monitor/{id:int}", monitor, new[] { draftId });

        yield return T(g, "ai_approve_draft", "Approve and send an AI draft",
            "Approve a held AI reply: it is sent now through the normal guards (opt-out, coverage, 24-hour / " +
            "social window, quiet hours wait) and any calendar booking it holds is made first.",
            "POST", "/api/ai/monitor/{id:int}/approve", monitor,
            new[] { draftId, A.Bool("force", "Send even though a guard warned (where an operator may override).") },
            outside: true);

        yield return T(g, "ai_edit_and_send_draft", "Edit and send an AI draft",
            "Replace the AI's text with yours and send it (recorded as an edit — the AI learns from the correction).",
            "POST", "/api/ai/monitor/{id:int}/send", monitor,
            new[] { draftId, A.Str("content", "The text to send instead.", req: true), A.Bool("force", "Override a warning guard.") },
            outside: true);

        yield return T(g, "ai_reject_draft", "Reject an AI draft",
            "Reject a held AI reply — nothing is sent, its reserved bookings are released, and the reason teaches the AI.",
            "POST", "/api/ai/monitor/{id:int}/reject", monitor,
            new[] { draftId, A.Str("reason", "Why it was wrong.") });

        yield return T(g, "ai_bulk_decide_drafts", "Approve or reject many AI drafts",
            "Approve (send) or reject several held AI replies at once. Destructive — confirm first.",
            "POST", "/api/ai/monitor/bulk", monitor,
            new[]
            {
                A.Str("action", "approve | reject.", req: true, oneOf: new[] { "approve", "reject" }),
                A.IntList("ids", "Draft ids.", req: true),
                A.Str("reason", "reject: why."),
            }, destructive: true, outside: true);

        yield return T(g, "ai_get_monitor_stats", "AI monitor statistics",
            "How the AI did over a period: replies written, auto-sent vs reviewed, approval / edit / reject " +
            "rates, blocked sends, average review time.",
            "GET", "/api/ai/monitor/stats", monitor, new[] { A.Days(), A.Entity() });

        yield return T(g, "ai_run_due_tasks", "Run due AI tasks now",
            "Run the AI's housekeeping now instead of waiting for the 30-second worker: due auto-sends, follow-ups " +
            "to customers who went quiet, and releasing orphaned calendar reservations.",
            "POST", "/api/ai/monitor/tick", behaviour, outside: true);

        yield return T(g, "ai_get_handover_stats", "Handover statistics",
            "How often conversations were handed to a person, by kind (check failed, asked for a human, blocked " +
            "send, booking failed, rule, manual, unknown customer), who resolved them and how fast.",
            "GET", "/api/ai/handovers/stats", C("ai.monitor", "chats.view"), new[] { A.Days(), A.Entity() });

        // ---- behaviour + controls -------------------------------------------
        yield return T(g, "ai_get_behaviour", "Read AI behaviour settings",
            "The AI review policy and human-like behaviour for the organisation or one entity: ai_review_mode " +
            "(auto | review_flagged | review_all | shadow), autosend minutes, first-N review, risky keywords, " +
            "reply wait/debounce, typing pauses, follow-ups, learning mode — each with its inherited value.",
            "GET", "/api/ai/behaviour", monitorOrBehaviour, new[] { A.Entity("Entity slug; omit for the organisation.") });

        yield return T(g, "ai_update_behaviour", "Change AI behaviour settings",
            "Set AI behaviour values for the organisation or an entity override — keys exactly as ai_get_behaviour " +
            "lists them, e.g. {\"ai_review_mode\":\"review_all\",\"ai_learning_mode\":\"review\"}.",
            "PUT", "/api/ai/behaviour", behaviour,
            new[] { A.Entity("Entity slug; omit for the organisation.", ArgIn.Query), A.Obj("values", "{setting key: value}.", req: true) });

        yield return T(g, "ai_reset_behaviour", "Reset an AI behaviour override",
            "Remove one behaviour value at this level so it inherits again (entity → organisation → default).",
            "DELETE", "/api/ai/behaviour", behaviour,
            new[] { A.Entity("Entity slug; omit for the organisation."), A.Str("key", "The setting key to reset.", req: true) },
            destructive: true);

        yield return T(g, "ai_get_controls", "Read the AI master switch",
            "The AI master switch for the organisation or an entity: on | off | schedule (per-weekday windows, " +
            "inside/outside), the never-AI tags, and what is inherited from the level above.",
            "GET", "/api/ai/controls", C("ai.monitor", "ai.behaviour.manage", "chats.view"),
            new[] { A.Entity("Entity slug; omit for the organisation.") });

        yield return T(g, "ai_set_controls", "Set the AI master switch",
            "Switch the AI on/off or onto a schedule for the organisation or an entity (inherit = follow the level " +
            "above; off anywhere up the tree wins). schedule: {mon:[[\"09:00\",\"18:00\"]], …} in the org timezone.",
            "PUT", "/api/ai/controls", behaviour,
            new[]
            {
                A.Entity("Entity slug; omit for the organisation.", ArgIn.Query),
                A.Str("mode", "on | off | schedule | inherit.", oneOf: new[] { "on", "off", "schedule", "inherit" }),
                A.Obj("schedule", "Per-weekday windows."),
                A.Str("schedule_mode", "AI on inside or outside the windows.", oneOf: new[] { "inside", "outside", "inherit" }),
                A.StrList("never_tags", "Contact tags the AI never answers (they add up across levels)."),
            });

        yield return T(g, "ai_get_persona", "Read the AI persona",
            "The chat persona (voice, tone, what the business is) for the organisation or an entity. The safety " +
            "rules above it are fixed.", "GET", "/api/aicore/persona", C("settings.ai.manage"),
            new[] { A.Entity("Entity slug; omit for the organisation.") });

        yield return T(g, "ai_set_persona", "Set the AI persona",
            "Replace the chat persona for the organisation or one entity. Superadmin only.",
            "PUT", "/api/aicore/persona", C("settings.ai.manage"),
            new[] { A.Str("persona", "The full persona text.", req: true), A.Entity("Entity slug; omit for the organisation.", ArgIn.Body) });

        // ---- learning -------------------------------------------------------
        yield return T(g, "ai_rate_reply", "Rate an AI reply",
            "👍 (1) or 👎 (-1) on an AI-written message (message_id) or an AI-monitor draft (draft_id) — your own " +
            "rating; rating again replaces it. With 👎 add reasons, a note and the correct answer: that becomes a " +
            "lesson (reviewed first, unless the learning mode is auto).",
            "POST", "/api/ai/feedback", monitor,
            new[]
            {
                A.Int("message_id", "The AI message id (pk in conversation payloads)."),
                A.Int("draft_id", "Or the AI-monitor draft id."),
                A.Int("rating", "1 = good, -1 = bad.", req: true, min: -1, max: 1),
                A.StrList("reasons", "Why it was bad.", oneOf: new[] { "wrong_info", "didnt_answer", "tone", "too_long", "should_handover", "other" }),
                A.Str("note", "Free text."),
                A.Str("correction", "What it should have said."),
            });

        yield return T(g, "ai_list_my_feedback", "Read my AI ratings",
            "Your own 👍/👎 ratings on the given AI-written messages.", "GET", "/api/ai/feedback", monitor,
            new[] { A.Str("message_ids", "Comma-separated message ids.", req: true) });

        yield return T(g, "ai_withdraw_feedback", "Withdraw an AI rating",
            "Delete one of your own ratings (and the suggestion it created, if still waiting).",
            "DELETE", "/api/ai/feedback/{id:int}", monitor, new[] { A.Id("feedback") }, destructive: true);

        yield return T(g, "ai_list_lessons", "List AI lessons",
            "What the AI learned from past conversations: examples, corrections and rules — the suggestions " +
            "waiting for review and the active library.", "GET", "/api/ai/lessons", monitor,
            new[]
            {
                A.Str("status", "suggested | active | archived | dismissed.", oneOf: new[] { "suggested", "active", "archived", "dismissed" }),
                A.Str("kind", "example | correction | rule.", oneOf: new[] { "example", "correction", "rule" }),
                A.Str("q", "Text search."),
                A.Entity(),
            }, link: (_, _) => Page("Open AI learning", "/ai-monitor?tab=learning"));

        yield return T(g, "ai_create_lesson", "Write an AI lesson",
            "Teach the AI directly: a rule (\"never promise same-day delivery\"), an example Q&A, or a correction " +
            "(question, the wrong answer to avoid, the right answer). Lessons are data — the safety rules win and " +
            "they never cross entities.", "POST", "/api/ai/lessons", behaviour,
            new[]
            {
                A.Str("kind", "rule (default) | example | correction.", oneOf: new[] { "rule", "example", "correction" }),
                A.Str("text", "rule: the rule."),
                A.Str("question", "example/correction: the customer's question."),
                A.Str("answer", "example/correction: the right answer."),
                A.Str("avoid", "correction: what not to say."),
                A.StrList("channels", "Only on these channels (empty = all)."),
                A.Entity("Entity slug; omit for organisation-wide.", ArgIn.Body),
                A.Str("status", "active (default) or suggested.", oneOf: new[] { "active", "suggested" }),
                A.Str("note", "Why."),
            });

        yield return T(g, "ai_update_lesson", "Edit or approve an AI lesson",
            "Edit a lesson, or move it: status active = approve a suggestion, dismissed = drop it, archived = retire it.",
            "PATCH", "/api/ai/lessons/{id:int}", behaviour,
            new[]
            {
                A.Id("lesson"),
                A.Str("status", "New status.", oneOf: new[] { "suggested", "active", "archived", "dismissed" }),
                A.Str("kind", "example | correction | rule.", oneOf: new[] { "example", "correction", "rule" }),
                A.Str("text", "Rule text."), A.Str("question", "Question."), A.Str("answer", "Answer."),
                A.Str("avoid", "What not to say."), A.StrList("channels", "Channels."), A.Str("note", "Note."),
            });

        yield return T(g, "ai_delete_lesson", "Delete an AI lesson",
            "Delete a lesson for good (admin). Prefer ai_update_lesson status archived to keep the history.",
            "DELETE", "/api/ai/lessons/{id:int}", behaviour, new[] { A.Id("lesson") }, destructive: true);

        yield return T(g, "ai_convert_lesson_to_knowledge", "Turn a lesson into knowledge",
            "Promote a lesson into the knowledge base (an example/correction → a FAQ, a rule → a policy).",
            "POST", "/api/ai/lessons/{id:int}/to-knowledge", behaviour, new[] { A.Id("lesson") });

        yield return T(g, "ai_get_learning_status", "AI learning / retrieval status",
            "The retrieval index: embeddings provider and model, items indexed vs pending, the last indexing error.",
            "GET", "/api/ai/learning/status", monitor, new[] { A.Entity() });

        yield return T(g, "ai_reindex_learning", "Re-index knowledge and lessons",
            "Queue every knowledge item and lesson for re-embedding (after changing the embeddings provider/model).",
            "POST", "/api/ai/learning/reindex", behaviour, outside: true);

        yield return T(g, "ai_get_learning_stats", "AI quality statistics",
            "The 👍 rate over time, the most common 👎 reasons, and knowledge gaps (questions the AI could not ground).",
            "GET", "/api/ai/learning/stats", monitor, new[] { A.Days(), A.Entity() });

        // ---- AI playground (nothing is sent, stored or booked) ----------------
        yield return T(g, "ai_get_playground_options", "AI playground options",
            "What the AI playground can try for an entity: the configured AI providers and their default models " +
            "(never keys), the stored persona (with ai.behaviour.manage), the channels, the production review " +
            "policy and AI switch, the learning mode and the custom fields a made-up customer can carry.",
            "GET", "/api/ai/playground/options", monitorOrBehaviour,
            new[] { A.Entity("Entity slug; omit for the organisation.") },
            link: (_, _) => Page("Open the AI playground", "/ai-playground"));

        yield return T(g, "ai_playground_reply", "Try the AI in the playground",
            "Have the AI answer a made-up (or imported) conversation exactly as it would answer a customer — " +
            "nothing is sent, stored or booked. Returns the reply as the bubbles it would send, what would happen " +
            "in production (send | hold | handover | blocked | ai_off, and why), what it was based on (knowledge " +
            "hits with scores, lessons), the tools it called (calendar bookings previewed only), its checks and " +
            "retries, provider, model, tokens and estimated cost. Trying changes (provider/model, a persona draft, " +
            "knowledge/lessons/skills/memory off, show_prompt) needs ai.behaviour.manage.",
            "POST", "/api/ai/playground/reply", monitorOrBehaviour,
            new[]
            {
                A.Arr("messages", "The transcript, oldest first: [{role: \"customer\" | \"ai\", text}], ending with " +
                                  "the customer's message (at most 60 turns, 4000 characters each).", req: true),
                A.Entity("Entity (outlet) slug whose knowledge, skills, persona and policy apply; omit for the organisation."),
                A.Str("channel", "The channel the chat stands in for (default whatsapp).",
                    oneOf: Endpoints.AiPlaygroundEndpoints.PlaygroundChannels),
                A.Int("contact_id", "Answer as if to this real contact (read only: their profile and memory)."),
                A.Obj("profile", "Or a made-up customer: {name, language: en | ms | zh, tags: [...], fields: {key: value}}."),
                A.Int("account_id", "The inbox whose review policy applies (optional)."),
                A.Str("thread", "A real conversation (channel:key) whose AI switch applies (optional)."),
                A.Obj("options", "{provider, model, persona, knowledge, lessons, skills, memory, show_prompt} — " +
                                 "anything but the defaults needs ai.behaviour.manage."),
            }, write: false);

        yield return T(g, "ai_playground_import", "Copy a conversation into the playground",
            "Read the last messages (default 20, max 60) of a real conversation you can see as a playground " +
            "transcript, with its entity, channel, inbox and contact. Read only — nothing is changed.",
            "POST", "/api/ai/playground/import", monitorOrBehaviour,
            new[]
            {
                A.Str("channel", "The conversation's channel.", req: true),
                A.Str("key", "The conversation's key.", req: true),
                A.Int("take", "How many of its latest messages (default 20, max 60).", min: 1, max: 60),
            }, write: false);
    }
}
