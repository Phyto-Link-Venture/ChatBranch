using System.Text.Json;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Tools;

/// <summary>
/// ChatBranch's built-in tools. Reads query through the scoped stores; writes go
/// through the SAME endpoints the dashboard uses (<see cref="InternalApi"/>), so
/// every guard and audit row applies exactly as if a person clicked the button.
/// </summary>
public static class BuiltinTools
{
    public static IReadOnlyList<IChatBranchTool> Create() => new IChatBranchTool[]
    {
        new SearchContacts(), new GetContact(), new CreateContact(), new UpdateContact(),
        new ListConversations(), new GetConversation(), new SendMessage(), new AddNote(),
        new SetConversationStatus(), new AssignConversation(), new ListTemplates(),
        new SearchKnowledge(), new ListFlows(), new GetAnalyticsSummary(), new ListActivity(),
        new ListTags(),
    };

    internal static readonly string[] ThreadStatuses =
        { ThreadStatus.Open, ThreadStatus.Pending, ThreadStatus.Snoozed, ThreadStatus.Resolved };

    internal static string Iso(DateTime? dt) => dt is DateTime d
        ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ") : "";

    internal static string ChatHref(string channel, string key) =>
        $"/chats?channel={Uri.EscapeDataString(channel)}&key={Uri.EscapeDataString(key)}";

    /// <summary>The compact contact card every contact tool returns.</summary>
    internal static Dictionary<string, object?> ContactCard(JsonElement c)
    {
        string S(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
        object? Raw(string k) => c.TryGetProperty(k, out var v) ? v.Clone() : null;
        return new Dictionary<string, object?>
        {
            ["id"] = c.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : null,
            ["name"] = S("name"),
            ["phone"] = S("phone"),
            ["email"] = S("email"),
            ["company"] = S("company"),
            ["language"] = S("language"),
            ["tags"] = Raw("tags"),
            ["entities"] = Raw("entities"),
        };
    }

    internal static void LinkContact(ToolContext ctx, Dictionary<string, object?> card)
    {
        if (card["id"] is not int id) return;
        var name = card["name"] as string;
        ctx.Links.Add(new ToolLink($"Open {(string.IsNullOrEmpty(name) ? "contact" : name)}", $"/contacts/{id}"));
    }

    // ---- contacts -----------------------------------------------------------

    private sealed class SearchContacts : ChatBranchTool
    {
        public override string Name => "search_contacts";
        public override string Title => "Search contacts";
        public override string Description =>
            "Find contacts by name, phone number or email. Returns compact contact cards " +
            "(id, name, phone, email, company, tags). Use get_contact for the full record.";
        public override string? Capability => "contacts.view";
        protected override string Schema => """
        {"type":"object","properties":{
          "query":{"type":"string","description":"Text matched against name, phone and email. Empty lists the most recently updated contacts."},
          "entity":{"type":"string","description":"Entity slug to narrow to (includes its branches)."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"default":10}}}
        """;
        public override string Summarize(JsonElement a) =>
            Str(a, "query") is { Length: > 0 } q ? $"Search contacts for \"{Clip(q, 60)}\"" : "List recent contacts";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var ent = await ToolScope.EntityAsync(ctx, Str(a, "entity"));
            var r = ToolContext.Require(await ctx.CallAsync("GET", "/api/contacts", query: new Dictionary<string, string?>
            {
                ["q"] = Str(a, "query")?.Trim(),
                ["entity"] = ent?.Slug,
                ["limit"] = Limit(a, 10, 50).ToString(),
            }, ct: ct));
            var cards = r.Body.TryGetProperty("contacts", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(ContactCard).ToList()
                : new List<Dictionary<string, object?>>();
            foreach (var c in cards.Take(3)) LinkContact(ctx, c);
            return new { count = cards.Count, contacts = cards };
        }
    }

    private sealed class GetContact : ChatBranchTool
    {
        public override string Name => "get_contact";
        public override string Title => "Get a contact";
        public override string Description =>
            "The full record for one contact: details, custom fields, business records " +
            "(orders, bookings, tickets…) and their conversations on every channel.";
        public override string? Capability => "contacts.view";
        protected override string Schema => """
        {"type":"object","properties":{"id":{"type":"integer","description":"Contact id."}},"required":["id"]}
        """;
        public override string Summarize(JsonElement a) => $"Open contact #{Int(a, "id")}";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var id = Int(a, "id") ?? throw new ToolException("'id' is required.");
            var r = ToolContext.Require(await ctx.CallAsync("GET", "/api/contacts/{id:int}",
                new Dictionary<string, string> { ["id"] = id.ToString() }, ct: ct));
            var card = ContactCard(r.Body);
            card["attributes"] = r.Body.TryGetProperty("attributes", out var attrs) ? attrs.Clone() : null;
            card["records"] = r.Body.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array
                ? recs.EnumerateArray().Take(5).Select(x => x.Clone()).ToList() : new List<JsonElement>();

            var threads = await ctx.Db.Threads.AsNoTracking().Where(t => t.ContactId == id)
                .OrderByDescending(t => t.UpdatedAt).Take(10).ToListAsync(ct);
            var bounded = ToolScope.BoundedEntityIds(ctx);
            var visible = new List<object>();
            foreach (var t in threads)
                if (await ToolScope.CanSeeThreadAsync(ctx, t, bounded))
                    visible.Add(new
                    {
                        channel = t.Channel, key = t.Key, status = t.Status, assignee = t.Assignee,
                        updated_at = Iso(t.UpdatedAt),
                    });
            card["conversations"] = visible;
            LinkContact(ctx, card);
            return card;
        }
    }

    private sealed class CreateContact : ChatBranchTool
    {
        public override string Name => "create_contact";
        public override string Title => "Create a contact";
        public override string Description =>
            "Add a new contact. Name and phone (E.164, e.g. +60123456789) are required.";
        public override string? Capability => "contacts.manage";
        public override bool IsWrite => true;
        protected override string Schema => """
        {"type":"object","properties":{
          "name":{"type":"string"},"phone":{"type":"string","description":"E.164 phone number."},
          "email":{"type":"string"},"company":{"type":"string"},
          "language":{"type":"string","description":"en, ms, zh…"},
          "tags":{"type":"array","items":{"type":"string"}}},
         "required":["name","phone"]}
        """;
        public override string Summarize(JsonElement a) =>
            $"Create contact {Str(a, "name") ?? "?"} ({Str(a, "phone") ?? "no phone"})";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var body = new Dictionary<string, object?>
            {
                ["name"] = Req(a, "name"),
                ["phone"] = Req(a, "phone"),
                ["email"] = Str(a, "email") ?? "",
                ["companyName"] = Str(a, "company") ?? "",
                ["language"] = Str(a, "language") is { Length: > 0 } l ? l : "en",
                ["tags"] = StrList(a, "tags") ?? new List<string>(),
            };
            var r = ToolContext.Require(await ctx.CallAsync("POST", "/api/contacts", body: body, ct: ct));
            var card = ContactCard(r.Body);
            LinkContact(ctx, card);
            return new { created = true, contact = card };
        }
    }

    private sealed class UpdateContact : ChatBranchTool
    {
        public override string Name => "update_contact";
        public override string Title => "Update a contact";
        public override string Description =>
            "Change a contact's details. Only the fields you pass are changed; tags replace " +
            "the whole list; attributes (custom fields) are merged.";
        public override string? Capability => "contacts.manage";
        public override bool IsWrite => true;
        protected override string Schema => """
        {"type":"object","properties":{
          "id":{"type":"integer"},"name":{"type":"string"},"phone":{"type":"string"},
          "email":{"type":"string"},"company":{"type":"string"},"language":{"type":"string"},
          "tags":{"type":"array","items":{"type":"string"}},
          "attributes":{"type":"object","description":"Custom field values by key."}},
         "required":["id"]}
        """;
        public override string Summarize(JsonElement a)
        {
            var fields = new[] { "name", "phone", "email", "company", "language", "tags", "attributes" }
                .Where(f => Has(a, f)).ToList();
            return $"Update contact #{Int(a, "id")}: {(fields.Count > 0 ? string.Join(", ", fields) : "no changes")}";
        }

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var id = Int(a, "id") ?? throw new ToolException("'id' is required.");
            var body = new Dictionary<string, object?>();
            if (Has(a, "name")) body["name"] = Str(a, "name");
            if (Has(a, "phone")) body["phone"] = Str(a, "phone");
            if (Has(a, "email")) body["email"] = Str(a, "email");
            if (Has(a, "company")) body["companyName"] = Str(a, "company");
            if (Has(a, "language")) body["language"] = Str(a, "language");
            if (Has(a, "tags")) body["tags"] = StrList(a, "tags") ?? new List<string>();
            if (a.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                body["attributes"] = attrs.Clone();
            if (body.Count == 0) throw new ToolException("Nothing to change — pass at least one field.");
            var r = ToolContext.Require(await ctx.CallAsync("PATCH", "/api/contacts/{id:int}",
                new Dictionary<string, string> { ["id"] = id.ToString() }, body, ct: ct));
            var card = ContactCard(r.Body);
            LinkContact(ctx, card);
            return new { updated = true, contact = card };
        }
    }

    // ---- conversations --------------------------------------------------------

    private sealed class ListConversations : ChatBranchTool
    {
        public override string Name => "list_conversations";
        public override string Title => "List conversations";
        public override string Description =>
            "Conversations in the unified inbox (WhatsApp, email, Messenger, Instagram, Telegram, " +
            "website chat), newest activity first. Filter by status, channel, entity, assignee, or " +
            "only those waiting for a reply (the customer spoke last).";
        public override string? Capability => "chats.view";
        protected override string Schema => """
        {"type":"object","properties":{
          "status":{"type":"string","enum":["open","pending","snoozed","resolved"]},
          "channel":{"type":"string","enum":["whatsapp","email","facebook","instagram","telegram","website","line","viber","sms","wechat","tiktok","douyin","zalo"]},
          "entity":{"type":"string","description":"Entity slug (includes its branches)."},
          "assignee":{"type":"string","description":"Username; \"me\" for yourself, \"none\" for unassigned."},
          "awaiting_reply":{"type":"boolean","description":"Only conversations whose last message came from the customer."},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":20}}}
        """;
        public override string Summarize(JsonElement a)
        {
            var bits = new[] { Str(a, "status"), Str(a, "channel"), Str(a, "assignee") }
                .Where(s => !string.IsNullOrEmpty(s)).ToList();
            return bits.Count > 0 ? $"List conversations ({string.Join(", ", bits)})" : "List conversations";
        }

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var status = Str(a, "status")?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(status) && !ThreadStatuses.Contains(status))
                throw new ToolException($"status must be one of {string.Join(", ", ThreadStatuses)}.");
            var channel = Str(a, "channel")?.Trim().ToLowerInvariant();
            var ent = await ToolScope.EntityAsync(ctx, Str(a, "entity"));
            var store = ctx.Services.GetRequiredService<ThreadStore>();
            var rows = store.ListThreads(string.IsNullOrEmpty(channel) ? null : channel, ent?.Id,
                string.IsNullOrEmpty(status) ? null : status, 500);

            var bounded = ToolScope.BoundedEntityIds(ctx);
            var visible = new List<Thread>();
            foreach (var t in rows)
                if (await ToolScope.CanSeeThreadAsync(ctx, t, bounded)) visible.Add(t);

            var assignee = Str(a, "assignee")?.Trim();
            if (assignee == "me") assignee = ctx.Username;
            if (assignee == "none") visible = visible.Where(t => t.Assignee == "").ToList();
            else if (!string.IsNullOrEmpty(assignee)) visible = visible.Where(t => t.Assignee == assignee).ToList();

            var ids = visible.Select(t => t.Id).ToList();
            var last = await ctx.Db.Threads.AsNoTracking().Where(t => ids.Contains(t.Id))
                .Select(t => new
                {
                    t.Id,
                    Last = t.Messages.Where(m => !m.Private).OrderByDescending(m => m.Id)
                        .Select(m => new { m.Text, m.Direction, m.CreatedAt }).FirstOrDefault(),
                }).ToDictionaryAsync(x => x.Id, x => x.Last, ct);

            var items = visible.Select(t =>
            {
                last.TryGetValue(t.Id, out var lm);
                return new
                {
                    channel = t.Channel,
                    key = t.Key,
                    name = t.Name.Length > 0 ? t.Name : t.Key,
                    status = t.Status,
                    assignee = t.Assignee,
                    team_id = t.TeamId,
                    contact_id = t.ContactId,
                    contact_name = t.Contact?.Name ?? "",
                    labels = t.Labels,
                    last_message = Clip(lm?.Text, 160),
                    last_from = lm == null ? "" : lm.Direction == MessageDirection.In ? "customer" : "us",
                    last_at = Iso(lm?.CreatedAt ?? t.UpdatedAt),
                    awaiting_reply = lm?.Direction == MessageDirection.In,
                };
            }).ToList();
            var awaiting = items.Count(i => i.awaiting_reply);
            if (a.TryGetProperty("awaiting_reply", out var ar) && ar.ValueKind == JsonValueKind.True)
                items = items.Where(i => i.awaiting_reply).ToList();

            var limit = Limit(a, 20, 100);
            ctx.Links.Add(new ToolLink("Open the inbox", "/chats"));
            foreach (var i in items.Take(2))
                ctx.Links.Add(new ToolLink($"Open {i.name}", ChatHref(i.channel, i.key)));
            return new
            {
                total = items.Count,
                awaiting_reply = awaiting,
                conversations = items.Take(limit),
            };
        }
    }

    private sealed class GetConversation : ChatBranchTool
    {
        public override string Name => "get_conversation";
        public override string Title => "Read a conversation";
        public override string Description =>
            "One conversation with its most recent messages (oldest first), including private " +
            "notes, plus its status, assignee, labels and linked contact.";
        public override string? Capability => "chats.view";
        protected override string Schema => """
        {"type":"object","properties":{
          "channel":{"type":"string","description":"whatsapp, email, facebook, instagram, telegram or website."},
          "key":{"type":"string","description":"The conversation key (WhatsApp number, email address, chat id…)."},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":20}},
         "required":["channel","key"]}
        """;
        public override string Summarize(JsonElement a) => $"Read conversation {Str(a, "channel")}:{Str(a, "key")}";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var t = await ToolScope.FindThreadAsync(ctx, Req(a, "channel"), Req(a, "key"));
            var store = ctx.Services.GetRequiredService<ThreadStore>();
            var msgs = store.GetMessages(t, Limit(a, 20, 100));
            ctx.Links.Add(new ToolLink($"Open {(t.Name.Length > 0 ? t.Name : t.Key)}", ChatHref(t.Channel, t.Key)));
            return new
            {
                channel = t.Channel,
                key = t.Key,
                name = t.Name.Length > 0 ? t.Name : t.Key,
                status = t.Status,
                assignee = t.Assignee,
                team_id = t.TeamId,
                labels = t.Labels,
                opted_out = t.OptedOut,
                contact = t.Contact == null ? null : new { id = t.Contact.Id, name = t.Contact.Name },
                messages = msgs.Select(m => new
                {
                    id = m.Id,
                    from = m.Private ? "note" : m.Direction == MessageDirection.In ? "customer" : m.Source,
                    text = Clip(m.Text, 1200),
                    status = m.Status,
                    at = Iso(m.CreatedAt),
                }),
            };
        }
    }

    private sealed class SendMessage : ChatBranchTool
    {
        public override string Name => "send_message";
        public override string Title => "Send a message";
        public override string Description =>
            "Reply to a customer in an existing conversation, as you. Goes through the normal " +
            "send path: opt-outs, WhatsApp's 24-hour window and the cold-message guard all apply, " +
            "and a refusal explains why.";
        public override string? Capability => "chats.send";
        public override bool IsWrite => true;
        protected override string Schema => """
        {"type":"object","properties":{
          "channel":{"type":"string"},"key":{"type":"string"},
          "text":{"type":"string","description":"The message to send."}},
         "required":["channel","key","text"]}
        """;
        public override string Summarize(JsonElement a) =>
            $"Send \"{Clip(Str(a, "text"), 80)}\" to {Str(a, "channel")}:{Str(a, "key")}";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var text = Req(a, "text");
            var t = await ToolScope.FindThreadAsync(ctx, Req(a, "channel"), Req(a, "key"));
            var r = t.Channel == Channels.WhatsApp
                ? await ctx.CallAsync("POST", "/api/whatsapp/threads/{key}/send",
                    new Dictionary<string, string> { ["key"] = t.Key }, new { text }, ct: ct)
                : await ctx.CallAsync("POST", "/api/messaging/threads/{channel}/{key}/send",
                    new Dictionary<string, string> { ["channel"] = t.Channel, ["key"] = t.Key },
                    new { text }, ct: ct);
            ToolContext.Require(r);
            var status = r.Body.TryGetProperty("message", out var m) && m.TryGetProperty("status", out var s)
                ? s.ToString() : "";
            ctx.Links.Add(new ToolLink($"Open {(t.Name.Length > 0 ? t.Name : t.Key)}", ChatHref(t.Channel, t.Key)));
            return new { sent = true, channel = t.Channel, key = t.Key, delivery_status = status };
        }
    }

    private sealed class AddNote : ChatBranchTool
    {
        public override string Name => "add_note";
        public override string Title => "Add a private note";
        public override string Description =>
            "Add an internal note to a conversation. Notes are never sent to the customer and are " +
            "kept out of the AI's context.";
        public override string? Capability => "chats.send";
        public override bool IsWrite => true;
        protected override string Schema => """
        {"type":"object","properties":{"channel":{"type":"string"},"key":{"type":"string"},
          "text":{"type":"string"}},"required":["channel","key","text"]}
        """;
        public override string Summarize(JsonElement a) =>
            $"Note on {Str(a, "channel")}:{Str(a, "key")}: \"{Clip(Str(a, "text"), 80)}\"";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var text = Req(a, "text");
            var t = await ToolScope.FindThreadAsync(ctx, Req(a, "channel"), Req(a, "key"));
            ToolContext.Require(await ctx.CallAsync("POST", "/api/messaging/threads/{channel}/{key}/note",
                new Dictionary<string, string> { ["channel"] = t.Channel, ["key"] = t.Key },
                new { text }, ct: ct));
            return new { noted = true, channel = t.Channel, key = t.Key };
        }
    }

    private sealed class SetConversationStatus : ChatBranchTool
    {
        public override string Name => "set_conversation_status";
        public override string Title => "Set conversation status";
        public override string Description =>
            "Mark a conversation open, pending, snoozed or resolved. A customer reply re-opens " +
            "resolved and snoozed conversations automatically.";
        public override string? Capability => "chats.manage";
        public override bool IsWrite => true;
        protected override string Schema => """
        {"type":"object","properties":{"channel":{"type":"string"},"key":{"type":"string"},
          "status":{"type":"string","enum":["open","pending","snoozed","resolved"]}},
         "required":["channel","key","status"]}
        """;
        public override string Summarize(JsonElement a) =>
            $"Mark {Str(a, "channel")}:{Str(a, "key")} as {Str(a, "status")}";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var status = Req(a, "status").ToLowerInvariant();
            if (!ThreadStatuses.Contains(status))
                throw new ToolException($"status must be one of {string.Join(", ", ThreadStatuses)}.");
            var t = await ToolScope.FindThreadAsync(ctx, Req(a, "channel"), Req(a, "key"));
            ToolContext.Require(await ctx.CallAsync("POST", "/api/messaging/threads/{channel}/{key}/status",
                new Dictionary<string, string> { ["channel"] = t.Channel, ["key"] = t.Key },
                new { status }, ct: ct));
            return new { channel = t.Channel, key = t.Key, status };
        }
    }

    private sealed class AssignConversation : ChatBranchTool
    {
        public override string Name => "assign_conversation";
        public override string Title => "Assign a conversation";
        public override string Description =>
            "Hand a conversation to a team (by team_id) and/or an agent (by username). " +
            "An empty assignee clears the agent; team_id 0 clears the team.";
        public override string? Capability => "chats.manage";
        public override bool IsWrite => true;
        protected override string Schema => """
        {"type":"object","properties":{"channel":{"type":"string"},"key":{"type":"string"},
          "assignee":{"type":"string","description":"Username of the agent, or \"\" to unassign."},
          "team_id":{"type":"integer","description":"Team id, or 0 to clear the team."}},
         "required":["channel","key"]}
        """;
        public override string Summarize(JsonElement a)
        {
            var who = Has(a, "assignee") ? (Str(a, "assignee") is { Length: > 0 } u ? u : "nobody") : null;
            var team = Int(a, "team_id") is int tid ? (tid == 0 ? "no team" : $"team #{tid}") : null;
            var to = string.Join(" / ", new[] { who, team }.Where(x => x != null));
            return $"Assign {Str(a, "channel")}:{Str(a, "key")} to {(to.Length > 0 ? to : "?")}";
        }

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            if (!Has(a, "assignee") && !Has(a, "team_id"))
                throw new ToolException("Pass an assignee, a team_id, or both.");
            var t = await ToolScope.FindThreadAsync(ctx, Req(a, "channel"), Req(a, "key"));
            var rv = new Dictionary<string, string> { ["channel"] = t.Channel, ["key"] = t.Key };
            if (Int(a, "team_id") is int teamId)
                ToolContext.Require(await ctx.CallAsync("POST", "/api/teams/assign",
                    body: new { channel = t.Channel, key = t.Key, team_id = teamId }, ct: ct));
            string? assignee = null;
            if (Has(a, "assignee"))
            {
                assignee = (Str(a, "assignee") ?? "").Trim();
                if (assignee == "me") assignee = ctx.Username;
                if (assignee.Length > 0 && !await ctx.Db.Users.AnyAsync(u => u.Username == assignee && u.IsActive, ct))
                    throw new ToolException($"No active user '{assignee}'.", 404);
                ToolContext.Require(await ctx.CallAsync("POST", "/api/messaging/threads/{channel}/{key}/assign",
                    rv, new { assignee }, ct: ct));
            }
            return new { channel = t.Channel, key = t.Key, assignee, team_id = Int(a, "team_id") };
        }
    }

    // ---- library + knowledge ----------------------------------------------------

    private sealed class ListTemplates : ChatBranchTool
    {
        public override string Name => "list_templates";
        public override string Title => "List message templates";
        public override string Description =>
            "Saved replies (canned responses, inserted with /short_code in the composer) and call " +
            "script templates available to an entity.";
        public override string? Capability => "chats.view";
        protected override string Schema => """
        {"type":"object","properties":{
          "query":{"type":"string","description":"Filter by short code, name or text."},
          "entity":{"type":"string"}}}
        """;
        public override string Summarize(JsonElement a) => "List message templates";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var ent = await ToolScope.EntityAsync(ctx, Str(a, "entity"));
            var sc = ctx.Services.GetRequiredService<ScopingService>();
            var q = (Str(a, "query") ?? "").Trim().ToLowerInvariant();
            var canned = (await sc.ScopeInherited(ctx.Db.CannedResponses.AsNoTracking(), ent?.Id)
                    .OrderBy(c => c.ShortCode).ToListAsync(ct))
                .Where(c => q.Length == 0 || c.ShortCode.ToLowerInvariant().Contains(q)
                            || c.Content.ToLowerInvariant().Contains(q))
                .Take(50)
                .Select(c => new { id = c.Id, short_code = c.ShortCode, content = Clip(c.Content, 400) })
                .ToList();
            var scripts = (await sc.ScopeInherited(ctx.Db.ScriptTemplates.AsNoTracking(), ent?.Id)
                    .OrderBy(s => s.Name).ToListAsync(ct))
                .Where(s => q.Length == 0 || s.Name.ToLowerInvariant().Contains(q))
                .Take(25)
                .Select(s => new { id = s.Id, name = s.Name })
                .ToList();
            ctx.Links.Add(new ToolLink("Open message templates", "/templates"));
            return new { canned_responses = canned, script_templates = scripts };
        }
    }

    private sealed class SearchKnowledge : ChatBranchTool
    {
        public override string Name => "search_knowledge";
        public override string Title => "Search the knowledge base";
        public override string Description =>
            "Search the company knowledge base the AI answers from (facts, policies, FAQs, events, " +
            "notices). Only live entries are returned — expired notices are excluded.";
        public override string? Capability => "chats.view";
        protected override string Schema => """
        {"type":"object","properties":{
          "query":{"type":"string"},"entity":{"type":"string"},
          "limit":{"type":"integer","minimum":1,"maximum":20,"default":5}},
         "required":["query"]}
        """;
        public override string Summarize(JsonElement a) => $"Search knowledge for \"{Clip(Str(a, "query"), 60)}\"";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var query = Req(a, "query");
            var ent = await ToolScope.EntityAsync(ctx, Str(a, "entity"));
            var knowledge = ctx.Services.GetRequiredService<KnowledgeService>();
            var words = query.ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', '?', '!', ';', ':', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3).ToHashSet();
            var hits = knowledge.LiveItems(ent?.Id, null)
                .Select(k =>
                {
                    var hay = $"{k.Title} {k.Question} {k.Content} {k.Category} {string.Join(" ", k.Tags)}"
                        .ToLowerInvariant();
                    return (k, score: words.Count(hay.Contains));
                })
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score).ThenByDescending(x => x.k.Priority)
                .Take(Limit(a, 5, 20))
                .Select(x => new
                {
                    id = x.k.Id, kind = x.k.Kind, title = x.k.Title, question = x.k.Question,
                    content = Clip(x.k.Content, 700), category = x.k.Category, published = x.k.Published,
                })
                .ToList();
            if (hits.Any(h => h.published)) ctx.Links.Add(new ToolLink("Open the Help Center", "/help"));
            return new { count = hits.Count, results = hits };
        }
    }

    private sealed class ListFlows : ChatBranchTool
    {
        public override string Name => "list_flows";
        public override string Title => "List flows";
        public override string Description =>
            "Workflows from the flow builder (automated multi-step sequences) and whether each is live.";
        public override string? Capability => "flows.view";
        protected override string Schema => """
        {"type":"object","properties":{
          "entity":{"type":"string"},
          "live_only":{"type":"boolean","description":"Only flows that are switched on."}}}
        """;
        public override string Summarize(JsonElement a) => "List flows";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var ent = await ToolScope.EntityAsync(ctx, Str(a, "entity"));
            var sc = ctx.Services.GetRequiredService<ScopingService>();
            var rows = await sc.ScopeInherited(ctx.Db.Workflows.AsNoTracking(), ent?.Id)
                .OrderBy(w => w.Position).ThenBy(w => w.CreatedAt).ToListAsync(ct);
            if (a.TryGetProperty("live_only", out var lo) && lo.ValueKind == JsonValueKind.True)
                rows = rows.Where(w => w.Active).ToList();
            if (ctx.Capabilities.Contains("flows.manage"))
                ctx.Links.Add(new ToolLink("Open the flow builder", "/flow-builder"));
            return new
            {
                count = rows.Count,
                flows = rows.Select(w => new
                {
                    id = w.Id, flow_id = w.FlowId, name = w.Name, hint = w.Hint, live = w.Active,
                    steps = w.Nodes.Count, entity_id = w.EntityId,
                }),
            };
        }
    }

    private sealed class GetAnalyticsSummary : ChatBranchTool
    {
        private static readonly HashSet<string> Bulky = new() { "daily", "health_events" };

        public override string Name => "get_analytics_summary";
        public override string Title => "Get the analytics summary";
        public override string Description =>
            "Headline numbers for a period: message funnel (sent, delivered, read, replied), " +
            "contacts engaged, call outcomes, per-account volume and per-entity breakdown.";
        public override string? Capability => "analytics.view";
        protected override string Schema => """
        {"type":"object","properties":{
          "days":{"type":"integer","enum":[7,30,90],"default":30},"entity":{"type":"string"}}}
        """;
        public override string Summarize(JsonElement a) => $"Analytics for the last {Int(a, "days") ?? 30} days";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var ent = await ToolScope.EntityAsync(ctx, Str(a, "entity"));
            var r = ToolContext.Require(await ctx.CallAsync("GET", "/api/analytics/summary",
                query: new Dictionary<string, string?>
                {
                    ["days"] = (Int(a, "days") ?? 30).ToString(), ["entity"] = ent?.Slug,
                }, ct: ct));
            ctx.Links.Add(new ToolLink("Open analytics", "/analytics"));
            if (r.Body.ValueKind != JsonValueKind.Object) return r.Body;
            return r.Body.EnumerateObject().Where(p => !Bulky.Contains(p.Name))
                .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        }
    }

    private sealed class ListActivity : ChatBranchTool
    {
        public override string Name => "list_activity";
        public override string Title => "Read the activity log";
        public override string Description =>
            "Recent entries from the activity log — who did what, when, and how (user, API key, " +
            "AI app via MCP, assistant, automation, flow, webhook, system). You only see what " +
            "your role may see.";
        public override string? Capability => "activity.view";
        protected override string Schema => """
        {"type":"object","properties":{
          "from":{"type":"string","description":"ISO date/time lower bound."},
          "to":{"type":"string","description":"ISO date/time upper bound (a bare date includes the whole day)."},
          "actor":{"type":"string","description":"Username."},
          "source":{"type":"string","enum":["user","api","mcp","assistant","automation","flow","webhook","system"]},
          "action":{"type":"string","description":"Exact action, or a prefix ending in * (e.g. thread.*)."},
          "q":{"type":"string","description":"Free text across actor, action, target, detail and path."},
          "entity":{"type":"string","description":"Entity slug (includes its branches)."},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":20}}}
        """;
        public override string Summarize(JsonElement a) => "Read the activity log";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var visible = await AuditEndpoints.VisibleAsync(ctx.Db, ctx.User)
                          ?? throw new ToolException("The activity.view capability is required.", 403);
            var sc = ctx.Services.GetRequiredService<ScopingService>();
            var filtered = await AuditEndpoints.ApplyAsync(visible, new AuditEndpoints.Filters(
                Str(a, "from"), Str(a, "to"), Str(a, "actor"), Str(a, "source"), Str(a, "action"),
                Str(a, "entity"), Str(a, "q")), ctx.Db, sc);
            var rows = await filtered.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Take(Limit(a, 20, 100)).ToListAsync(ct);
            ctx.Links.Add(new ToolLink("Open the activity log", "/activity"));
            return new
            {
                count = rows.Count,
                entries = rows.Select(r => new
                {
                    at = Iso(r.CreatedAt), actor = r.Actor, source = r.Source, action = r.Action,
                    target = r.Target, detail = Clip(r.Detail, 200), status = r.Status,
                }),
            };
        }
    }

    private sealed class ListTags : ChatBranchTool
    {
        public override string Name => "list_tags";
        public override string Title => "List tags";
        public override string Description =>
            "The tags in use on contacts and the labels on conversations, with how often each is used.";
        public override string? Capability => "contacts.view";
        protected override string Schema => """
        {"type":"object","properties":{"query":{"type":"string","description":"Only tags containing this text."}}}
        """;
        public override string Summarize(JsonElement a) => "List tags";

        public override async Task<object?> ExecuteAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
        {
            var q = (Str(a, "query") ?? "").Trim().ToLowerInvariant();
            var contacts = ctx.Db.Contacts.AsNoTracking();
            if (ctx.Role != Roles.SuperAdmin && Auth.Caller.TenantId(ctx.User) is int tid)
                contacts = contacts.Where(c => c.TenantId == tid);
            if (ToolScope.BoundedEntityIds(ctx) is { } ids)
                contacts = contacts.Where(c => c.EntityLinks.Any(l => ids.Contains(l.EntityId)));
            var contactTags = (await contacts.Select(c => c.Tags).ToListAsync(ct))
                .SelectMany(t => t).Select(t => t.Trim()).Where(t => t.Length > 0);

            var bounded = ToolScope.BoundedEntityIds(ctx);
            var threads = await ctx.Db.Threads.AsNoTracking()
                .OrderByDescending(t => t.UpdatedAt).Take(2000).ToListAsync(ct);
            var labels = new List<string>();
            foreach (var t in threads)
                if (t.Labels.Count > 0 && await ToolScope.CanSeeThreadAsync(ctx, t, bounded))
                    labels.AddRange(t.Labels);

            object Count(IEnumerable<string> xs) => xs
                .Where(x => q.Length == 0 || x.ToLowerInvariant().Contains(q))
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { tag = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).ThenBy(x => x.tag).Take(100).ToList();
            return new { contact_tags = Count(contactTags), conversation_labels = Count(labels) };
        }
    }
}
