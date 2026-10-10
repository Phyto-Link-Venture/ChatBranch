using System.Text;
using System.Text.Json;

namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    private static IEnumerable<IChatBranchTool> Contacts()
    {
        const string g = "contacts";
        var view = C("contacts.view");
        var viewOrChats = C("contacts.view", "chats.view");
        var manage = C("contacts.manage");

        yield return T(g, "contacts_delete", "Delete a contact",
            "Permanently delete a contact (their conversations stay, unlinked). Refused while the contact is " +
            "still bound to a login. Destructive — confirm with the person first.",
            "DELETE", "/api/v1/contacts/{id:int}", manage, new[] { A.Id("contact") }, destructive: true);

        yield return T(g, "contacts_get_ai_mode", "Get a contact's AI mode",
            "Whether the AI may answer this contact: follow (the normal rules), always, or never — plus the " +
            "effective verdict and which rule decided it (a never-AI tag wins over the contact's own mode).",
            "GET", "/api/contacts/{id:int}/ai-mode", view, new[] { A.Id("contact") });

        yield return T(g, "contacts_set_ai_mode", "Set a contact's AI mode",
            "Set 'AI for this contact': follow = use the inbox/chat rules, always = the AI answers whenever the " +
            "master switch allows, never = the AI never answers this person on any channel.",
            "PUT", "/api/contacts/{id:int}/ai-mode", manage,
            new[] { A.Id("contact"), A.Str("mode", "follow | always | never.", req: true, oneOf: new[] { "follow", "always", "never" }) });

        yield return T(g, "contacts_list_calls", "List a contact's calls",
            "Voice calls with this contact (SIP and WhatsApp): when, outcome, duration, summary, and whether a " +
            "recording exists.", "GET", "/api/contacts/{id:int}/calls", viewOrChats, new[] { A.Id("contact") });

        yield return T(g, "contacts_get_channels", "Get a contact's channel profile",
            "The contact's identities across every channel (WhatsApp, email, SMS, Telegram, Messenger…): the " +
            "account each conversation runs on and its health, in/out message counts, last activity, whether " +
            "the 24-hour window is open, opt-outs, and the WhatsApp verdict (verified / not_on_whatsapp / unknown).",
            "GET", "/api/contacts/{id:int}/channels", viewOrChats, new[] { A.Id("contact") });

        yield return T(g, "contacts_add_identity", "Register a channel identity",
            "Register an address for a contact by hand (e.g. their email, a second WhatsApp number, a " +
            "Telegram chat id) so inbound messages from it link to them. Sends nothing.",
            "POST", "/api/contacts/{id:int}/channels", manage,
            new[]
            {
                A.Id("contact"),
                A.Str("channel", "Channel of the address (whatsapp, email, sms, voice, telegram, …).", req: true),
                A.Str("address", "The address on that channel (E.164 phone, email, chat id…).", req: true),
            });

        yield return T(g, "contacts_discover_channels", "Find a contact's channels",
            "'Find channels': derive the contact's identities from their phone/email and check, through a " +
            "connected QR WhatsApp number, whether the phone is on WhatsApp. Never sends a message.",
            "POST", "/api/contacts/{id:int}/channels/discover", manage, new[] { A.Id("contact") }, outside: true);

        yield return T(g, "contacts_create_invite", "Invite a contact to another app",
            "'Invite to another app': an 8-character code (30 days) and the links the person taps to start a " +
            "chat on WhatsApp / Telegram / Messenger / Instagram / email, plus a QR. Their first message carrying " +
            "the code links the new conversation to this contact. Creates the invite; you deliver the link.",
            "POST", "/api/contacts/{id:int}/invites", manage,
            new[]
            {
                A.Id("contact"),
                A.Str("channel", "One channel to invite to (whatsapp, telegram, facebook, instagram, email)."),
                A.StrList("channels", "Several channels at once."),
                A.Int("account_id", "Invite to this specific number/bot/page.", @as: "accountId"),
            });

        yield return T(g, "contacts_list_invites", "List a contact's invites",
            "The contact's open and used channel invites (code, channel, link, expiry, used at).",
            "GET", "/api/contacts/{id:int}/invites", view, new[] { A.Id("contact") });

        yield return T(g, "contacts_get_tags", "Get a contact's tags",
            "The registry tags on a contact with their attribute values (e.g. a 'Driver' tag with licence_no).",
            "GET", "/api/contacts/{id:int}/tags", C("tags.view"), new[] { A.Id("contact") });

        yield return T(g, "contacts_set_tags", "Set a contact's tags",
            "Replace the contact's registry tags (and attribute values). Group-exclusive tags replace each other. " +
            "Pass every tag the contact should keep — read contacts_get_tags first. Tag ids come from tags_list.",
            "PUT", "/api/contacts/{id:int}/tags", C("tags.manage"),
            new[]
            {
                A.Id("contact"),
                A.Arr("tags", "[{tag_id, values?: {attribute_key: value}}] — the complete list.", req: true),
            });

        yield return T(g, "contacts_list_entities", "List a contact's entities",
            "The entities (outlets/branches) a contact belongs to.",
            "GET", "/api/v1/contacts/{id:int}/entities", view, new[] { A.Id("contact") });

        yield return T(g, "contacts_add_entity", "Add a contact to an entity",
            "Attach a contact to an entity (outlet/branch) — a contact on a branch is visible to its head office.",
            "POST", "/api/v1/contacts/{id:int}/entities", manage,
            new[] { A.Id("contact"), A.Str("entity", "Entity slug.", req: true) });

        yield return T(g, "contacts_remove_entity", "Remove a contact from an entity",
            "Detach a contact from one entity. Their conversations stay; who can see the contact may change.",
            "DELETE", "/api/v1/contacts/{id:int}/entities", manage,
            new[] { A.Id("contact"), A.Str("entity", "Entity slug.", req: true, @in: ArgIn.Body) }, destructive: true);

        yield return T(g, "contacts_run_identity_checks", "Run WhatsApp number checks",
            "Run one pass of the WhatsApp identity checker now (looks up pending numbers through a connected QR " +
            "number, ≤1 lookup / 2 s, 30-day cache). Never sends a message.",
            "POST", "/api/contacts/identity-checks/run", manage, outside: true);

        yield return Custom(g, "contacts_import", "Import contacts",
            "Import contacts from CSV text, a base64 .csv/.xlsx/.vcf file, or rows of {header: value}. Phones " +
            "are normalised to E.164. duplicates: skip (default) | update_empty | overwrite. Run with " +
            "preview=true first: it writes nothing and returns the counts and the suggested column mapping.",
            new[] { "POST /api/contacts/import" }, ImportContactsAsync, manage,
            new[]
            {
                A.Str("csv", "CSV text with a header row (the simplest way)."),
                A.Str("file_base64", "A .csv, .xlsx or .vcf file, base64 encoded (with file_name)."),
                A.Str("file_name", "Name of the base64 file, e.g. contacts.xlsx."),
                A.Arr("rows", "Rows as objects of {column header: value}."),
                A.Obj("column_mapping", "{column header: target field} — name, phone, email, company, language, tags, notes, or cf_<custom field key>."),
                A.Str("mode", "What to do with a contact that already exists.", oneOf: new[] { "skip", "update_empty", "overwrite" }),
                A.Entity("Entity slug the new contacts belong to."),
                A.StrList("tags", "Tags to add to every imported contact."),
                A.Bool("discover", "Also find each contact's channels after import."),
                A.Bool("preview", "true = dry run: counts and mapping only, nothing written."),
            });

        yield return T(g, "contacts_preview_google_import", "Preview a Google Contacts import",
            "Read YOUR connected Google Contacts (Settings → My connections) as import rows with the duplicate " +
            "counts — a preview; commit them with contacts_import (rows).",
            "GET", "/api/contacts/import/google", manage,
            new[] { A.Str("mode", "Duplicate handling to preview.", oneOf: new[] { "skip", "update_empty", "overwrite" }) },
            outside: true);

        yield return T(g, "contacts_get_conversation_history", "Read a contact's messages",
            "Every message with this contact across all their conversations and channels, newest last.",
            "GET", "/api/messaging/contacts/{id:int}/conversation", viewOrChats,
            new[] { A.Id("contact"), A.Limit(50, 500) });

        yield return T(g, "contacts_get_timeline", "Get a communications timeline",
            "The communications timeline for a phone number or email: messages, calls and business records in " +
            "date order, with the stored summary.", "GET", "/api/messaging/timeline", viewOrChats,
            new[] { A.Str("phone", "E.164 phone number."), A.Str("email", "Email address."), A.Limit(50, 500) });

        yield return T(g, "contacts_summarize_timeline", "Summarize a contact's history",
            "An AI summary of everything with this phone/email (messages, calls, records): open issues, " +
            "follow-ups, sentiment. Uses the configured model; falls back to a heuristic summary.",
            "POST", "/api/messaging/timeline/summary", viewOrChats,
            new[] { A.Str("phone", "E.164 phone number."), A.Str("email", "Email address.") },
            write: false, outside: true);

        yield return T(g, "contacts_sync_records", "Sync business records",
            "Upsert business records (orders, invoices, bookings, tickets…) onto contacts by external_id — what " +
            "an external system pushes through /api/v1/sync/records. Each record: {external_id, type, title, " +
            "status, amount, due_date, contact: {phone|email|name}, custom: {…}}.",
            "POST", "/api/v1/sync/records", manage,
            new[] { A.Arr("records", "The records to upsert.", req: true) });

        // ---- "Request access" to a contact of another branch ----------------------
        var askers = C("contacts.manage", "chats.send");
        yield return T(g, "contacts_request_access", "Request access to a contact",
            "When adding a contact fails because the phone/email belongs to a customer of ANOTHER branch of the " +
            "same company (create_contact returns created=false with a request_token), ask an admin to share " +
            "that customer with your branch. Nothing about the customer is shown until an admin approves. A " +
            "second identical request returns the pending one.",
            "POST", "/api/contacts/access-requests", askers,
            new[]
            {
                A.Str("request_token", "The request_token from the failed add (valid 30 minutes).", req: true),
                A.Str("note", "Optional note for the admin (why you need this customer)."),
            });

        yield return T(g, "contacts_list_access_requests", "List contact access requests",
            "Access requests to share a customer with another branch: your own requests (status, branch, note — " +
            "no customer details before approval) and, for admins, the requests you can decide (with the " +
            "customer's name, masked phone and current entities). counts.to_decide = waiting for you.",
            "GET", "/api/contacts/access-requests", askers,
            new[]
            {
                A.Str("status", "Which requests (default pending).",
                    oneOf: new[] { "pending", "approved", "denied", "cancelled", "all" }),
            });

        yield return Custom(g, "contacts_decide_access_request", "Approve or deny a contact access request",
            "Approve (the customer is added to the requested branch, keeping its other entities; the requester " +
            "is notified) or deny (with an optional reason sent to the requester) a pending access request. " +
            "Needs an admin who manages both the customer's branch and the requested one.",
            new[] { "POST /api/contacts/access-requests/{id:int}/approve",
                    "POST /api/contacts/access-requests/{id:int}/deny" },
            DecideAccessRequestAsync, manage,
            new[]
            {
                A.Id("access request"),
                A.Str("decision", "approve or deny.", req: true, oneOf: new[] { "approve", "deny" }),
                A.Str("reason", "Why it was denied (sent to the requester)."),
            });

        yield return T(g, "contacts_cancel_access_request", "Cancel your contact access request",
            "Withdraw one of YOUR pending access requests.",
            "POST", "/api/contacts/access-requests/{id:int}/cancel", askers, new[] { A.Id("access request") });
    }

    private static async Task<object?> DecideAccessRequestAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var id = I(a, "id") ?? throw new ToolException("'id' is required.");
        var decision = Need(a, "decision").Trim().ToLowerInvariant();
        if (decision is not ("approve" or "deny")) throw new ToolException("decision must be approve or deny.");
        var body = decision == "deny" ? new Dictionary<string, object?> { ["reason"] = S(a, "reason") ?? "" }
            : new Dictionary<string, object?>();
        var r = ToolContext.Require(await ctx.CallAsync("POST", "/api/contacts/access-requests/{id:int}/" + decision,
            routeValues: new Dictionary<string, string> { ["id"] = id.ToString() }, body: body, ct: ct));
        ctx.Links.Add(new ToolLink("Open access requests", $"/contacts?access={id}"));
        return Compact(r.Body);
    }

    private static async Task<object?> ImportContactsAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        const long max = 10 * 1024 * 1024;
        var options = new Dictionary<string, object?>();
        if (El(a, "column_mapping") is { ValueKind: JsonValueKind.Object } cm) options["column_mapping"] = cm.Clone();
        if (S(a, "mode") is { Length: > 0 } mode) options["mode"] = mode;
        if (S(a, "entity") is { Length: > 0 } ent) options["entity"] = ent;
        if (ChatBranchTool.StrList(a, "tags") is { Count: > 0 } tags) options["tags"] = tags;
        if (B(a, "discover") is bool d) options["discover"] = d;
        var preview = B(a, "preview") == true;

        InternalApi.Response r;
        if (El(a, "rows") is { ValueKind: JsonValueKind.Array } rows)
        {
            options["rows"] = rows.Clone();
            options["preview"] = preview;
            r = await ctx.CallAsync("POST", "/api/contacts/import", body: options, ct: ct);
        }
        else
        {
            byte[] bytes;
            string fileName;
            if (S(a, "csv") is { Length: > 0 } csv)
            {
                bytes = Encoding.UTF8.GetBytes(csv);
                fileName = "import.csv";
                if (bytes.Length > max) throw new ToolException("The CSV is too large (max 10 MB).");
            }
            else if (S(a, "file_base64") is { Length: > 0 })
            {
                bytes = Base64(a, "file_base64", max);
                fileName = S(a, "file_name") is { Length: > 0 } fn ? fn : "import.csv";
            }
            else throw new ToolException("Pass csv (text), file_base64 + file_name, or rows.");

            var fields = new Dictionary<string, string?>
            {
                ["preview"] = preview ? "true" : null,
                ["mode"] = options.GetValueOrDefault("mode") as string,
                ["entity"] = options.GetValueOrDefault("entity") as string,
                ["tags"] = options.GetValueOrDefault("tags") is List<string> tl ? string.Join(",", tl) : null,
                ["discover"] = options.GetValueOrDefault("discover") is bool dv ? (dv ? "true" : "false") : null,
                ["column_mapping"] = options.GetValueOrDefault("column_mapping") is JsonElement m ? m.GetRawText() : null,
            };
            r = await ctx.CallAsync("POST", "/api/contacts/import", ct: ct, form: Form(fields, "file", fileName, bytes));
        }
        ToolContext.Require(r);
        ctx.Links.Add(new ToolLink("Open contacts", "/contacts"));
        return Compact(r.Body);
    }
}
