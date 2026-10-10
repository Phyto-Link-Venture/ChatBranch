using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    private static readonly string[] KnowledgeKinds = { "fact", "policy", "faq", "event", "notice" };
    /// <summary>The "Available to" argument of every multi-entity library write.</summary>
    internal const string EntitiesDesc =
        "Available to these entity slugs (each with its branches); [] = everyone (organisation-wide). " +
        "Wins over entity. Omit on an update to keep.";
    private static readonly string[] TagColours ={ "tangerine", "ink", "teal", "plum", "ochre", "blue", "green", "rose" };

    private static IEnumerable<IChatBranchTool> Library()
    {
        // ---- knowledge --------------------------------------------------------
        const string k = "knowledge";
        var ai = C("settings.ai.manage");
        yield return T(k, "knowledge_list", "List knowledge items",
            "Every knowledge-base entry the AI answers from (facts, policies, FAQs, events, notices) for the " +
            "organisation or an entity, with active/published flags and validity dates. Use search_knowledge to " +
            "search the live ones by text.", "GET", "/api/aicore/knowledge", ai, new[] { A.Entity() },
            link: (_, _) => Page("Open knowledge", "/knowledge"));

        var knowledgeFields = new[]
        {
            A.Str("title", "Title."),
            A.Str("content", "The content the AI reads (facts, policy text, the FAQ answer)."),
            A.Str("question", "faq: the question it answers."),
            A.StrList("tags", "Keywords."),
            A.StrList("channels", "Only on these channels (empty = all)."),
            A.Int("priority", "Higher wins when space runs out."),
            A.Bool("active", "Whether the AI may use it (default true)."),
            A.Bool("published", "Also show it in the public Help Center."),
            A.Str("category", "Help Center category."),
            A.Str("slug", "Help Center URL slug."),
            A.StrList("entities", EntitiesDesc),
        };
        yield return T(k, "knowledge_create", "Add a knowledge item",
            "Add to the knowledge base: kind fact | policy | faq | event | notice. Events and notices can carry " +
            "starts_at / ends_at and expire on their own. entities = only those entities (and their branches); " +
            "none = everyone (organisation-wide).",
            "POST", "/api/aicore/knowledge", ai,
            new[]
            {
                A.Str("kind", "Kind (default fact).", oneOf: KnowledgeKinds),
                A.Str("starts_at", "ISO date-time it becomes valid.", @as: "startsAt"),
                A.Str("ends_at", "ISO date-time it expires.", @as: "endsAt"),
                A.Entity("Entity slug; omit for organisation-wide.", ArgIn.Body),
            }.Concat(knowledgeFields).ToArray());

        yield return T(k, "knowledge_update", "Edit a knowledge item",
            "Change a knowledge item — only the fields you pass (active false = the AI stops using it).",
            "PATCH", "/api/aicore/knowledge/{id:int}", ai,
            new[]
            {
                A.Id("knowledge item"),
                A.Str("kind", "Kind.", oneOf: KnowledgeKinds),
                A.Str("starts_at", "ISO date-time it becomes valid (\"\" clears)."),
                A.Str("ends_at", "ISO date-time it expires (\"\" clears)."),
            }.Concat(knowledgeFields).ToArray());

        yield return T(k, "knowledge_delete", "Delete a knowledge item",
            "Delete a knowledge item and its search index entries. Prefer knowledge_update active=false to pause it.",
            "DELETE", "/api/aicore/knowledge/{id:int}", ai, new[] { A.Id("knowledge item") }, destructive: true);

        yield return T(k, "knowledge_test_retrieval", "Test knowledge retrieval",
            "'Test retrieval': exactly the knowledge and learned-lessons blocks the AI would receive for a " +
            "customer message, with every scored hit — to see why the AI answered as it did.",
            "POST", "/api/aicore/knowledge/preview", C("settings.ai.manage", "ai.monitor"),
            new[]
            {
                A.Str("query", "The customer message to test.", req: true),
                A.Str("channel", "As if on this channel."),
                A.Entity("As if for this entity.", ArgIn.Body),
            }, write: false);

        yield return Custom(k, "knowledge_upload_document", "Upload a document to the knowledge base",
            "Add a document (PDF with a text layer, .txt, .md or .csv; ≤10 MB) as one knowledge item that the AI " +
            "retrieves from in chunks. Pass plain text, or a base64 file with its name.",
            new[] { "POST /api/aicore/knowledge/upload" }, UploadKnowledgeAsync, ai,
            new[]
            {
                A.Str("text", "Plain/markdown text to store as a document."),
                A.Str("file_base64", "Or a .pdf/.txt/.md/.csv file, base64 encoded."),
                A.Str("file_name", "File name with extension (e.g. price-list.pdf)."),
                A.Str("title", "Title (default: the file name)."),
                A.Str("kind", "Kind (default faq).", oneOf: KnowledgeKinds),
                A.StrList("channels", "Only on these channels."),
                A.Entity("Entity slug; omit for organisation-wide."),
                A.StrList("entities", EntitiesDesc),
            });

        // ---- skills -----------------------------------------------------------
        const string s = "skills";
        yield return T(s, "skills_list", "List AI skills",
            "The AI's skills: prompt skills (when-to-use + instructions), code tools it can call, checks run on " +
            "every draft, and connected apps (calendar booking) — with their scope and whether they're enabled.",
            "GET", "/api/aicore/skills", C("settings.ai.manage", "flows.manage"), new[] { A.Entity() },
            link: (_, _) => Page("Open skills", "/skills"));

        var skillFields = new[]
        {
            A.Str("id", "Skill id (keeps an existing skill; omit to create — derived from the name)."),
            A.Str("name", "Name.", req: true),
            A.Str("kind", "prompt | tool | check | app.", oneOf: new[] { "prompt", "tool", "check", "app" }),
            A.Str("use_when", "prompt: when the AI should use it."),
            A.Str("instructions", "prompt: what to do."),
            A.Str("description", "tool/check: what it does (the model reads this)."),
            A.Str("code", "tool: JS `function run(args, ctx)`; check: `function check(reply, ctx)`."),
            A.Obj("parameters", "tool: JSON schema of its arguments."),
            A.Arr("tests", "tool/check: test cases (an enabled code skill must pass them)."),
            A.Bool("enabled", "Enabled (default true)."),
            A.StrList("channels", "Only on these channels."),
            A.Str("on_fail", "check: retry_then_handover | handover | block."),
            A.Obj("config", "app: the connected-app config (skills_list_apps shows the shape)."),
            A.Entity("Entity slug the skill is saved under; omit for organisation-wide."),
            A.StrList("entities", "Also available to these entity slugs (and their branches); the skill's own entity " +
                "is always included. Omit to keep; an organisation-wide skill takes none."),
        };
        yield return Custom(s, "skills_save_skill", "Create or update an AI skill",
            "Create a skill or update one by id — only that skill changes; the rest of its scope's library is kept. " +
            "An enabled code skill must pass its tests; an app skill's config is validated.",
            new[] { "PUT /api/aicore/skills", "GET /api/aicore/skills" }, SaveSkillAsync, ai, skillFields);

        yield return Custom(s, "skills_delete_skill", "Delete an AI skill",
            "Delete one skill from its scope (organisation or entity). Flows using it fall back to no skill.",
            new[] { "PUT /api/aicore/skills", "GET /api/aicore/skills" }, DeleteSkillAsync, ai,
            new[] { A.Str("id", "Skill id.", req: true), A.Entity("Entity slug of the skill; omit for organisation-wide.") },
            destructive: true);

        yield return T(s, "skills_list_apps", "List connectable apps for skills",
            "The connected-app catalogue for app skills (Google Calendar / ChatBranch calendar): sources with the " +
            "calendar connections you may use, actions, and default config.",
            "GET", "/api/aicore/skills/apps", ai, new[] { A.Entity() });

        yield return T(s, "skills_list_builtins", "List built-in AI tools",
            "The tools the AI always has (current_time, current_week, date_add, days_between, convert_timezone) " +
            "and the org timezone.", "GET", "/api/aicore/skills/builtins", ai);

        yield return T(s, "skills_draft_skill", "Draft a skill from a description",
            "Describe what the AI should be able to do; get up to 4 clarifying questions, then (with answers) a " +
            "ready-to-save skill draft. Saves nothing.", "POST", "/api/aicore/skills/draft", ai,
            new[]
            {
                A.Str("description", "What the skill should do.", req: true),
                A.Str("kind_hint", "prompt | tool | check.", oneOf: new[] { "prompt", "tool", "check" }),
                A.Arr("answers", "[{question, answer}] answers to the clarifying questions."),
            }, write: false, outside: true);

        yield return T(s, "skills_test_skill", "Test a skill",
            "Run a code skill's tests ({kind, code, tests}) or dry-run an app skill ({kind:\"app\", config, " +
            "app_action:\"availability\"}) — the calendar dry run books nothing.",
            "POST", "/api/aicore/skills/test", ai,
            new[]
            {
                A.Str("kind", "tool | check | app.", req: true, oneOf: new[] { "tool", "check", "app" }),
                A.Str("code", "tool/check code."),
                A.Arr("tests", "Test cases."),
                A.Obj("config", "app config."),
                A.Str("app_action", "app: availability.", oneOf: new[] { "availability" }),
                A.Str("from_date", "app: first day (YYYY-MM-DD)."),
                A.Str("to_date", "app: last day (YYYY-MM-DD)."),
                A.Entity("app: as this entity.", ArgIn.Body),
            }, write: false);

        // ---- bookings ---------------------------------------------------------
        const string b = "bookings";
        yield return T(b, "bookings_list", "List bookings",
            "Calendar bookings the AI made (and cancelled) in conversations you can see: who, when, which " +
            "calendar, status (pending, booked, cancelled, failed).", "GET", "/api/bookings", C("chats.view"),
            new[]
            {
                A.Int("contact_id", "One contact."), A.Str("thread", "One conversation as channel:key."),
                A.Str("status", "pending | booked | cancelling | cancelled | failed."),
                A.Str("from", "Starts on/after (ISO date)."), A.Str("to", "Starts before (ISO date)."),
                A.Bool("upcoming", "Only future bookings."), A.Entity(), A.Limit(50, 500),
            }, link: (_, _) => Page("Open bookings", "/bookings"));

        // ---- documents (the Documents / QR app skills) -------------------------
        yield return T("documents", "documents_list", "List documents",
            "PDF invoices, quotations, receipts, letters and QR codes the AI (or a teammate) made in conversations " +
            "you can see: number, total, status (pending = waiting for its reply, sent, void, failed).",
            "GET", "/api/documents", C("chats.view"),
            new[]
            {
                A.Str("thread", "One conversation as channel:key."), A.Int("contact_id", "One contact."),
                A.Str("type", "invoice | quotation | receipt | letter | qr."),
                A.Str("status", "pending | sent | void | failed | issued (comma list)."), A.Entity(), A.Limit(50, 500),
            });

        yield return T("documents", "documents_create", "Create a document",
            "Make a PDF invoice, quotation, receipt or letter in a conversation with its Documents skill " +
            "(letterhead, tax, numbering — the server computes every amount). fields: {items:[{description, qty, " +
            "unit_price, discount?}], notes?, reference?, due_date?, payment_method?} or, for a letter, {title, " +
            "paragraphs:[…]}. send=true sends it to the customer now (as a file, or a download link where the " +
            "channel can't carry files).",
            "POST", "/api/documents", C("chats.send"),
            new[]
            {
                A.Str("channel", "Conversation channel.", req: true, oneOf: EndpointTool.ChannelNames),
                A.Str("key", "Conversation key.", req: true),
                A.Str("type", "invoice | quotation | receipt | letter.", req: true,
                    oneOf: new[] { "invoice", "quotation", "receipt", "letter" }),
                A.Obj("fields", "What goes on the document (see the description).", req: true),
                A.Bool("send", "Send it to the customer now."),
            }, outside: true);

        yield return T(b, "bookings_cancel", "Cancel a booking",
            "Cancel a booking: frees the slot and deletes the Google Calendar event (attendees are notified).",
            "POST", "/api/bookings/{id:int}/cancel", C("chats.send"), new[] { A.Id("booking") },
            destructive: true, outside: true);

        yield return Custom(b, "bookings_check_availability", "Check calendar availability",
            "Free slots a calendar skill would offer right now (its weekday hours, buffers and notice, minus " +
            "Google busy times and existing bookings) — a dry run that books nothing.",
            new[] { "POST /api/aicore/skills/test", "GET /api/aicore/skills" }, AvailabilityAsync, ai,
            new[]
            {
                A.Str("skill_id", "The calendar app skill's id (skills_list).", req: true),
                A.Str("from_date", "First day (YYYY-MM-DD)."), A.Str("to_date", "Last day (YYYY-MM-DD)."),
                A.Entity("The skill's entity slug; omit for organisation-wide."),
            }, write: false, outside: true);

        yield return T(b, "bookings_get_hours", "Read booking hours",
            "The legacy 'Booking hours' panel (per weekday open/close) for the org or an entity. Note: booking " +
            "skills carry their own hours; this panel is stored but not read by the booking tools.",
            "GET", "/api/aicore/booking-hours", C("integrations.view", "settings.ai.manage"), new[] { A.Entity() });

        yield return T(b, "bookings_set_hours", "Set booking hours",
            "Set the 'Booking hours' panel: {mon:[\"09:00\",\"18:00\"], …, sun:null}.",
            "PUT", "/api/aicore/booking-hours", C("integrations.manage", "settings.ai.manage"),
            new[] { A.Entity(@in: ArgIn.Query), A.Obj("hours", "Day → [open, close] or null (closed).", req: true) });

        yield return T(b, "bookings_reset_hours", "Reset booking hours",
            "Remove an entity's booking-hours override (it inherits the organisation's again).",
            "DELETE", "/api/aicore/booking-hours", C("integrations.manage", "settings.ai.manage"),
            new[] { A.Entity() }, destructive: true);

        // ---- templates --------------------------------------------------------
        const string tp = "templates";
        var tplManage = C("templates.manage");
        yield return T(tp, "templates_list_quick_replies", "List quick replies",
            "Quick replies (canned responses, /short_code in the composer), with variables rendered for a contact " +
            "when contact_id is given. channel narrows to those offered on it.",
            "GET", "/api/messaging/canned", C("chats.view", "chats.send", "templates.manage"),
            new[] { A.Entity(), A.Int("contact_id", "Render {{variables}} for this contact."), A.Str("channel", "Channel.") },
            link: (_, _) => Page("Open templates", "/templates"));

        var cannedFields = new[]
        {
            A.Str("short_code", "The /code operators type."), A.Str("content", "The text ({{first_name}}, {{cf_*}}, {{tag_*}} variables work)."),
            A.StrList("channels", "Only offered on these channels (empty = all)."),
            A.Entity("Entity slug; omit for organisation-wide.", ArgIn.Body),
            A.StrList("entities", EntitiesDesc),
        };
        yield return T(tp, "templates_create_quick_reply", "Create a quick reply",
            "Add a quick reply (a canned response operators insert with /short_code in the composer).", "POST", "/api/messaging/canned", tplManage, cannedFields);
        yield return T(tp, "templates_update_quick_reply", "Edit a quick reply",
            "Change a quick reply's short code, text, channels or entity (only the fields you pass).", "PATCH", "/api/messaging/canned/{id:int}", tplManage,
            new[] { A.Id("quick reply") }.Concat(cannedFields).ToArray());
        yield return T(tp, "templates_delete_quick_reply", "Delete a quick reply",
            "Delete a quick reply (canned response) from the library.", "DELETE", "/api/messaging/canned/{id:int}", tplManage,
            new[] { A.Id("quick reply") }, destructive: true);

        var scripts = C("script_templates.manage");
        yield return T(tp, "templates_list_call_scripts", "List call scripts",
            "Call script templates (what a 'Script call' reads), rendered for a contact when contact_id is given.",
            "GET", "/api/calls/script-templates", C("script_templates.manage", "chats.send", "flows.manage"),
            new[] { A.Entity(), A.Int("contact_id", "Render variables for this contact.") });
        var scriptFields = new[]
        {
            A.Str("name", "Name."), A.Str("content", "The script ({{variables}} work)."),
            A.Entity("Entity slug; omit for organisation-wide.", ArgIn.Body),
            A.StrList("entities", EntitiesDesc),
        };
        yield return T(tp, "templates_create_call_script", "Create a call script",
            "Add a call script template (the text a 'Script call' flow step reads aloud).", "POST", "/api/calls/script-templates", scripts, scriptFields);
        yield return T(tp, "templates_update_call_script", "Edit a call script",
            "Change a call script template's name, text or entity.", "PATCH", "/api/calls/script-templates/{id:int}", scripts,
            new[] { A.Id("call script") }.Concat(scriptFields).ToArray());
        yield return T(tp, "templates_delete_call_script", "Delete a call script",
            "Delete a call script template from the library.", "DELETE", "/api/calls/script-templates/{id:int}", scripts,
            new[] { A.Id("call script") }, destructive: true);

        yield return T(tp, "templates_list_whatsapp", "List WhatsApp templates",
            "WhatsApp message templates per account: Meta's live registry for a Cloud number with a WABA " +
            "(name, language, category, status APPROVED/PENDING/REJECTED, body, parameters) or local ones.",
            "GET", "/api/whatsapp/templates", C("chats.send", "templates.manage", "flows.manage"),
            new[] { A.Str("account_id", "The WhatsApp account."), A.Str("wa_id", "Render variables for this number's contact.") });
        yield return T(tp, "templates_create_whatsapp", "Create a WhatsApp template",
            "Create a template — on a live WABA it is submitted to Meta for review (status PENDING). Body " +
            "variables are {{1}}, {{2}}…", "POST", "/api/whatsapp/templates", tplManage,
            new[]
            {
                A.Str("account_id", "The Cloud account."), A.Str("name", "lower_snake_case name.", req: true),
                A.Str("language", "e.g. en, en_US, ms."),
                A.Str("category", "MARKETING | UTILITY | AUTHENTICATION.", oneOf: new[] { "MARKETING", "UTILITY", "AUTHENTICATION" }),
                A.Str("body", "Body text.", req: true),
            }, outside: true);
        yield return T(tp, "templates_update_whatsapp", "Edit a WhatsApp template",
            "Edit a template (Meta re-reviews it) or its local variable bindings.",
            "PATCH", "/api/whatsapp/templates/{name}", tplManage,
            new[]
            {
                A.Str("name", "Template name.", req: true), A.Str("account_id", "The account.", @in: ArgIn.Query),
                A.Str("body", "New body."), A.Str("category", "Category."), A.Str("language", "Language."),
                A.Arr("buttons", "Buttons."), A.Obj("bindings", "Local variable bindings {\"1\": \"{{first_name}}\"}."),
            }, outside: true);
        yield return T(tp, "templates_delete_whatsapp", "Delete a WhatsApp template",
            "Delete a template (from Meta's registry on a live WABA).", "DELETE", "/api/whatsapp/templates/{name}",
            tplManage,
            new[] { A.Str("name", "Template name.", req: true), A.Str("account_id", "The account."), A.Str("id", "Meta template id.") },
            destructive: true, outside: true);

        // ---- tags ---------------------------------------------------------------
        const string tg = "tags";
        var tagsManage = C("tags.manage");
        yield return T(tg, "tags_list", "List the tag registry",
            "Registry tags with ids, colour, what they apply to (contact / entity / conversation), exclusive " +
            "group and attribute fields. (list_tags counts how often tags are used.)",
            "GET", "/api/tags", C("tags.view"),
            new[] { A.Str("applies_to", "contact | entity | conversation.", oneOf: new[] { "contact", "entity", "conversation" }), A.Entity() },
            link: (_, _) => Page("Open tags", "/tags"));
        var tagFields = new[]
        {
            A.Str("name", "Name."), A.Str("slug", "Slug (default from the name)."),
            A.Str("applies_to", "contact | entity | conversation.", oneOf: new[] { "contact", "entity", "conversation" }),
            A.Str("color", "Palette colour.", oneOf: TagColours), A.Str("description", "Description."),
            A.Str("group", "Exclusive group: a contact holds at most one tag of a group."),
            A.Arr("attributes", "Attribute fields [{key, label, kind}] (values set per contact/entity)."),
            A.Int("position", "Order."), A.Entity("Entity slug; omit for organisation-wide.", ArgIn.Body),
        };
        yield return T(tg, "tags_create", "Create a tag", "Add a tag to the registry (colour, what it applies to, exclusive group, attribute fields).", "POST", "/api/tags",
            tagsManage, tagFields);
        yield return T(tg, "tags_update", "Edit a tag", "Change a registry tag's name, colour, group or attribute fields (only the fields you pass).",
            "PATCH", "/api/tags/{id:int}", tagsManage, new[] { A.Id("tag") }.Concat(tagFields).ToArray());
        yield return T(tg, "tags_delete", "Delete a tag",
            "Delete a tag. A tag still in use needs force=true (it is removed from everything).",
            "DELETE", "/api/tags/{id:int}", tagsManage,
            new[] { A.Id("tag"), A.Bool("force", "Delete even if it is in use.") }, destructive: true);
        yield return T(tg, "tags_list_targets", "List what carries a tag",
            "The contacts or entities that carry a tag (with attribute values).", "GET", "/api/tags/{id:int}/targets",
            C("tags.view"), new[] { A.Id("tag") });
        yield return T(tg, "tags_assign", "Apply or remove a tag",
            "Put a tag on contacts/entities (add) or take it off (remove) — ids of whatever the tag applies to.",
            "POST", "/api/tags/{id:int}/targets", tagsManage,
            new[] { A.Id("tag"), A.IntList("add", "Ids to tag."), A.IntList("remove", "Ids to untag.") });
        yield return T(tg, "tags_list_assignments", "List tag assignments",
            "Every tag assignment of one target type (entity or contact), with values.",
            "GET", "/api/tags/assignments", C("tags.view"),
            new[] { A.Str("target_type", "entity (default) | contact.", oneOf: new[] { "entity", "contact" }) });
        yield return T(tg, "entities_get_tags", "Get an entity's tags",
            "The registry tags on an entity (e.g. inner company / outside company) with attribute values.",
            "GET", "/api/entities/{slug}/tags", C("tags.view"), new[] { A.Str("slug", "Entity slug.", req: true) });
        yield return T(tg, "entities_set_tags", "Set an entity's tags",
            "Replace an entity's registry tags: [{tag_id, values?}] — the complete list.",
            "PUT", "/api/entities/{slug}/tags", tagsManage,
            new[] { A.Str("slug", "Entity slug.", req: true), A.Arr("tags", "[{tag_id, values?}].", req: true) });
    }

    // ---- composite tools -----------------------------------------------------

    private static async Task<object?> UploadKnowledgeAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        const long max = 10 * 1024 * 1024;
        byte[] bytes;
        string name;
        if (S(a, "file_base64") is { Length: > 0 })
        {
            bytes = Base64(a, "file_base64", max);
            name = S(a, "file_name") is { Length: > 0 } fn ? fn : throw new ToolException("file_name is required with file_base64.");
        }
        else if (S(a, "text") is { Length: > 0 } text)
        {
            bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > max) throw new ToolException("The text is too large (max 10 MB).");
            var title = S(a, "title") is { Length: > 0 } t ? t : "document";
            name = S(a, "file_name") is { Length: > 0 } fn2 ? fn2 : new string(title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()) + ".md";
        }
        else throw new ToolException("Pass text, or file_base64 + file_name.");
        var fields = new Dictionary<string, string?>
        {
            ["title"] = S(a, "title"),
            ["kind"] = S(a, "kind"),
            ["entity"] = S(a, "entity"),
            ["channels"] = ChatBranchTool.StrList(a, "channels") is { Count: > 0 } ch ? string.Join(",", ch) : null,
        };
        if (ChatBranchTool.StrList(a, "entities") is { Count: > 0 } ents) fields["entities"] = string.Join(",", ents);
        var r = ToolContext.Require(await ctx.CallAsync("POST", "/api/aicore/knowledge/upload", ct: ct,
            form: Form(fields, "file", name, bytes)));
        ctx.Links.Add(new ToolLink("Open knowledge", "/knowledge"));
        return Compact(r.Body);
    }

    /// <summary>The skills of exactly one scope (entity slug, or null = organisation-wide), as the PUT takes them.</summary>
    private static async Task<List<JsonObject>> ScopeSkillsAsync(ToolContext ctx, string? entity, CancellationToken ct)
    {
        var r = ToolContext.Require(await ctx.CallAsync("GET", "/api/aicore/skills",
            query: new Dictionary<string, string?> { ["entity"] = entity }, ct: ct));
        var list = new List<JsonObject>();
        if (!r.Body.TryGetProperty("skills", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var sk in arr.EnumerateArray())
        {
            var slug = sk.TryGetProperty("entity", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if ((entity ?? "") != (slug ?? "")) continue;
            // the fields the PUT needs to keep a skill unchanged (absent = keep for the rest)
            var o = new JsonObject();
            foreach (var f in new[] { "id", "name", "useWhen", "instructions", "position", "kind", "enabled" })
                if (sk.TryGetProperty(f, out var v)) o[f] = JsonNode.Parse(v.GetRawText());
            list.Add(o);
        }
        return list;
    }

    private static async Task<object?> SaveSkillAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var entity = S(a, "entity") is { Length: > 0 } e ? e : null;
        var skills = await ScopeSkillsAsync(ctx, entity, ct);
        var name = Need(a, "name");
        var id = S(a, "id") is { Length: > 0 } given ? given
            : new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        var row = skills.FirstOrDefault(x => x["id"]?.GetValue<string>() == id);
        var isNew = row == null;
        if (row == null)
        {
            row = new JsonObject { ["id"] = id, ["position"] = skills.Count, ["useWhen"] = "", ["instructions"] = "" };
            skills.Add(row);
        }
        row["name"] = name;
        void Map(string arg, string field)
        {
            if (a.TryGetProperty(arg, out var v) && v.ValueKind != JsonValueKind.Undefined)
                row![field] = JsonNode.Parse(v.GetRawText());
        }
        Map("kind", "kind"); Map("use_when", "useWhen"); Map("instructions", "instructions");
        Map("description", "description"); Map("code", "code"); Map("parameters", "parameters"); Map("tests", "tests");
        Map("enabled", "enabled"); Map("channels", "channels"); Map("on_fail", "onFail"); Map("config", "config");
        Map("entities", "entities");


        var payload = new JsonObject { ["entity"] = entity, ["skills"] = new JsonArray(skills.Select(x => (JsonNode)x).ToArray()) };
        var r = ToolContext.Require(await ctx.CallAsync("PUT", "/api/aicore/skills", body: payload, ct: ct));
        ctx.Links.Add(new ToolLink("Open skills", "/skills"));
        return new { saved = true, created = isNew, id, entity, result = Compact(r.Body) };
    }

    private static async Task<object?> DeleteSkillAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var entity = S(a, "entity") is { Length: > 0 } e ? e : null;
        var id = Need(a, "id");
        var skills = await ScopeSkillsAsync(ctx, entity, ct);
        if (skills.RemoveAll(x => x["id"]?.GetValue<string>() == id) == 0)
            throw new ToolException($"No skill '{id}' in {(entity ?? "the organisation-wide library")}.", 404);
        var payload = new JsonObject { ["entity"] = entity, ["skills"] = new JsonArray(skills.Select(x => (JsonNode)x).ToArray()) };
        ToolContext.Require(await ctx.CallAsync("PUT", "/api/aicore/skills", body: payload, ct: ct));
        return new { deleted = true, id, entity };
    }

    private static async Task<object?> AvailabilityAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var id = Need(a, "skill_id");
        var entity = S(a, "entity") is { Length: > 0 } e ? e : null;
        var r = ToolContext.Require(await ctx.CallAsync("GET", "/api/aicore/skills",
            query: new Dictionary<string, string?> { ["entity"] = entity }, ct: ct));
        JsonElement? config = null;
        if (r.Body.TryGetProperty("skills", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var sk in arr.EnumerateArray())
                if (sk.TryGetProperty("id", out var sid) && sid.GetString() == id)
                {
                    if (!sk.TryGetProperty("kind", out var kind) || kind.GetString() != "app")
                        throw new ToolException($"Skill '{id}' is not a connected-app (calendar) skill.");
                    config = sk.TryGetProperty("config", out var c) ? c.Clone() : null;
                }
        if (config == null) throw new ToolException($"No skill '{id}' visible to you.", 404);
        var test = ToolContext.Require(await ctx.CallAsync("POST", "/api/aicore/skills/test", body: new Dictionary<string, object?>
        {
            ["kind"] = "app", ["config"] = config, ["app_action"] = "availability",
            ["from_date"] = S(a, "from_date"), ["to_date"] = S(a, "to_date"), ["entity"] = entity,
        }, ct: ct));
        return Compact(test.Body);
    }
}
