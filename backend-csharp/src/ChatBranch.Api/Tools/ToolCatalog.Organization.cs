using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    private static readonly string[] BindingKinds =
    {
        "user-entities", "team-users", "contact-entities", "tag-contacts", "tag-entities", "entity-accounts",
        "entity-contacts", "user-teams", "entity-users", "pricebook-entities",
    };

    private static readonly string[] NotificationCategories = { "messages", "account_health", "flows", "admin", "system" };

    private static IEnumerable<IChatBranchTool> Organization()
    {
        // ---- me, users, roles ---------------------------------------------------
        yield return T("me", "me_get_profile", "Who am I",
            "The signed-in identity this key/assistant acts as: username, role, tenant, assigned entities and " +
            "the capability list (what you may do).", "GET", "/api/auth/whoami");

        const string u = "users";
        var usersManage = C("users.manage");
        yield return T(u, "users_list", "List users",
            "Logins you may manage (superadmin: everyone; otherwise your tenant, or the users bound inside your entities): username, role, active, entities, " +
            "teams, last login, a linked customer contact.", "GET", "/api/v1/users", usersManage,
            link: (_, _) => Page("Open users", "/users"));
        yield return T(u, "users_list_agents", "List assignable agents",
            "Active people a conversation can be assigned to (username, name, role).",
            "GET", "/api/messaging/agents", C("chats.view"));
        yield return T(u, "users_create", "Create a user",
            "Create a login within your rights (an admin creates agents in their own tenant). With an email " +
            "address and sign-in emails set up, the person gets a welcome email with their username, the login " +
            "address and a one-time link to choose their own password (send_invite, on by default then) — no " +
            "password needed. Without one, set the password yourself; it is never shown again. The answer's " +
            "invite says whether the email went out.", "POST", "/api/v1/users", usersManage,
            new[]
            {
                A.Str("username", "Username.", req: true),
                A.Str("email", "Their email address (the welcome email goes there)."),
                A.Str("name", "Display name (greets them in the welcome email)."),
                A.Bool("send_invite", "Send the welcome email with a set-password link (default: on when email is given and sign-in emails are set up)."),
                A.Str("password", "Initial password — required only without a welcome email."),
                A.Str("role", "agent (default) | admin | a custom role slug."),
                A.IntList("entity_ids", "Entities this login is bounded to (none = the whole tenant)."),
                A.Int("contact_id", "Bind the login to a customer contact."),
            });
        yield return T(u, "users_update", "Update a user",
            "Change a user's role, entities, active flag, email or password (within your rights).",
            "POST", "/api/v1/users/{id:int}", usersManage,
            new[]
            {
                A.Id("user"), A.Str("role", "New role."), A.Bool("active", "false = disable the login."),
                A.Str("email", "Their email address (changing it cancels links already sent)."),
                A.Str("password", "New password."), A.IntList("entity_ids", "Entities (replaces)."),
                A.Int("contact_id", "Linked customer contact (null to unlink)."),
            }, idempotent: true);
        yield return T(u, "users_send_invite", "Resend a welcome email / send a password reset link",
            "Email a user a new one-time link to choose their password (valid 72 h; older links stop working). " +
            "For someone still waiting to set their first password it resends the welcome email; for someone who " +
            "already has a password pass reset=true to send a password reset link (their current password keeps " +
            "working until they choose a new one). Within your rights; at most 5 links per person per hour.",
            "POST", "/api/v1/users/{id:int}/invite", usersManage,
            new[] { A.Id("user"), A.Bool("reset", "true = a password reset link for someone who already has a password.") });
        yield return T(u, "users_delete", "Delete a user",
            "Delete a login (within your rights). Prefer users_update active=false to keep their history readable.",
            "DELETE", "/api/v1/users/{id:int}", usersManage, new[] { A.Id("user") }, destructive: true);
        yield return T(u, "users_list_roles", "List roles",
            "The built-in roles (superadmin, admin, agent) and any custom roles, with their labels.", "GET", "/api/auth/roles", C("users.view", "users.manage", "rbac.manage"));
        yield return T(u, "users_get_permission_matrix", "Read the permission matrix",
            "Which capability each role holds (the RBAC matrix). Editing it is a person's job (Admin → Permissions).",
            "GET", "/api/auth/permissions", C("users.view", "users.manage", "rbac.manage"),
            link: (_, _) => Page("Open permissions", "/permissions"));

        // ---- entities -------------------------------------------------------------
        const string e = "entities";
        var entManage = C("entities.manage");
        yield return T(e, "entities_list", "List entities",
            "The entity tree you can see (head office → branches/outlets): id, slug, name, parent.",
            "GET", "/api/v1/entities", link: (_, _) => Page("Open entities", "/entities"));
        yield return T(e, "entities_create", "Create an entity",
            "Add an entity (outlet/branch). parent = the entity it sits under (its tenant follows); none = top level.",
            "POST", "/api/v1/entities", entManage,
            new[]
            {
                A.Str("name", "Name.", req: true), A.Str("slug", "URL-safe slug.", req: true),
                A.Any("parent_id", "Parent entity id (0/null = top level)."), A.Str("parent", "Or the parent's slug."),
            });
        yield return T(e, "entities_update", "Update an entity",
            "Rename, re-slug or move an entity under another parent (no cycles, same tenant).",
            "PATCH", "/api/v1/entities/{id:int}", entManage,
            new[]
            {
                A.Id("entity"), A.Str("name", "Name."), A.Str("slug", "Slug."),
                A.Any("parent_id", "New parent id (0/null = top level)."), A.Str("parent", "Or the new parent's slug."),
            });
        yield return T(e, "entities_delete", "Delete an entity",
            "Delete an entity (refused while it has children).", "DELETE", "/api/v1/entities/{id:int}", entManage,
            new[] { A.Id("entity") }, destructive: true);

        // ---- teams ------------------------------------------------------------------
        const string tm = "teams";
        yield return T(tm, "teams_list", "List teams",
            "Teams with their members, entity and whether new conversations round-robin across members.",
            "GET", "/api/teams", C("users.view", "chats.view", "flows.manage", "channels.view"),
            link: (_, _) => Page("Open teams", "/teams"));
        var teamFields = new[]
        {
            A.Str("name", "Name."), A.Str("description", "Description."),
            A.Bool("auto_assign", "Round-robin new conversations across members."),
            A.Entity("Entity slug; omit for organisation-wide.", ArgIn.Body),
        };
        yield return T(tm, "teams_create", "Create a team", "Add a team. Add members with teams_set_members.",
            "POST", "/api/teams", usersManage, teamFields);
        yield return T(tm, "teams_update", "Update a team", "Rename a team, change its entity or switch round-robin auto-assign on/off.", "PATCH", "/api/teams/{id:int}", usersManage,
            new[] { A.Id("team") }.Concat(teamFields).ToArray());
        yield return T(tm, "teams_delete", "Delete a team",
            "Delete a team (its conversations keep their agent, lose the team).", "DELETE", "/api/teams/{id:int}",
            usersManage, new[] { A.Id("team") }, destructive: true);
        yield return Custom(tm, "teams_set_members", "Add or remove team members",
            "Add users to a team and/or remove them (user ids from users_list).",
            new[] { "POST /api/bindings/{kind}" }, async (ctx, a, ct) =>
            {
                var r = ToolContext.Require(await ctx.CallAsync("POST", "/api/bindings/{kind}",
                    new Dictionary<string, string> { ["kind"] = "team-users" }, new Dictionary<string, object?>
                    {
                        ["target"] = I(a, "team_id") ?? throw new ToolException("'team_id' is required."),
                        ["add"] = El(a, "add")?.Clone(), ["remove"] = El(a, "remove")?.Clone(),
                    }, ct: ct));
                return Compact(r.Body);
            }, usersManage,
            new[] { A.Int("team_id", "The team.", req: true), A.IntList("add", "User ids to add."), A.IntList("remove", "User ids to remove.") });

        // ---- bindings -------------------------------------------------------------
        const string bd = "bindings";
        yield return T(bd, "bindings_get_catalog", "Bindings overview",
            "Every many-to-many relationship you can manage (user↔entities, team↔members, contact↔entities, " +
            "tag↔contacts/entities, account↔entity) with the pools of ids on each side.",
            "GET", "/api/bindings/catalog", C("users.view", "tags.view"), link: (_, _) => Page("Open bindings", "/bindings"));
        var getArgs = new[] { A.Str("kind", "Binding kind.", req: true, oneOf: BindingKinds), A.Int("target", "Target id.", req: true) };
        yield return Custom(bd, "bindings_get", "Read one binding",
            "For one target (a user, team, contact, tag or entity id) the ids bound to it on the other side " +
            "(member_ids) — e.g. kind team-users, target = team id → its members.",
            new[] { "GET /api/bindings/{kind}" },
            (ctx, a, ct) => BindingAsync(ctx, a, "GET", getArgs, ct),
            C("users.view", "contacts.view", "tags.view", "billing.manage"), getArgs, write: false);
        var applyArgs = new[]
        {
            A.Str("kind", "Binding kind.", req: true, oneOf: BindingKinds), A.Int("target", "Target id.", req: true),
            A.IntList("add", "Ids to add."), A.IntList("remove", "Ids to remove."),
        };
        yield return Custom(bd, "bindings_apply", "Change a binding",
            "Add and/or remove members of one target's binding (e.g. kind entity-accounts: which numbers belong " +
            "to an entity — moving an account re-files its new conversations; kind pricebook-entities: which " +
            "entities an AI price book prices — target = the plan id, and a main entity's plan flows down to its " +
            "branches, so one call can price a hundred companies).",
            new[] { "POST /api/bindings/{kind}" },
            (ctx, a, ct) => BindingAsync(ctx, a, "POST", applyArgs, ct),
            C("users.manage", "contacts.manage", "tags.manage", "channels.manage", "billing.manage"), applyArgs);

        // ---- settings -------------------------------------------------------------
        const string st = "settings";
        yield return T(st, "settings_get_my_preferences", "Read my appearance & language",
            "Your own theme / mode / accent / text size / language / the time zone times are shown in, and the organisation defaults (incl. the business time zone).",
            "GET", "/api/auth/preferences");
        yield return T(st, "settings_set_my_preferences", "Set my appearance & language",
            "Set your own look, dashboard language (en | ms | zh) and the time zone times are shown in; null = back to the default.",
            "PUT", "/api/auth/preferences", null,
            new[]
            {
                A.Obj("appearance", "{theme: paper|graphite|soft|ocean|forest|midnight|rose|sand|nord|contrast, mode: light|dark|system, accent, textSize, fontScale (85-130|null), radius, headingFont, bodyFont, lineHeight, density, spacing (70-140|null), contentWidth, gutter, cardPadding, navLayout, navSide, navWidth, navStart, motion, surface} or null."),
                A.Any("locale", "en | ms | zh, or null."),
                A.Any("time_zone", "Show times in: \"org\" (the business time zone), a zone such as Asia/Kuala_Lumpur, or null (the device's own)."),
            }, idempotent: true);
        yield return T(st, "settings_get_org_appearance", "Read the organisation's default look",
            "The organisation-wide default theme, mode, accent, dashboard language and business time zone (+ where it comes from: setting | env | server).", "GET", "/api/settings/appearance");
        yield return T(st, "settings_set_org_appearance", "Set the organisation's default look",
            "Set the default theme / language for everyone who hasn't chosen their own, and the business time zone (the AI's clock, bookings, journeys, quiet hours).",
            "PUT", "/api/settings/appearance", C("settings.appearance.org"),
            new[]
            {
                A.Obj("appearance", "The default appearance (or null)."), A.Any("locale", "en | ms | zh, or null."),
                A.Any("time_zone", "The business time zone, e.g. Asia/Kuala_Lumpur (null clears it — the server's ORG_TIMEZONE / TZ, else its own clock)."),
            },
            idempotent: true);
        yield return T(st, "settings_get_custom_fields", "Read custom field definitions",
            "The contact custom fields (key, label, kind text|select|multiselect|number|boolean|date|json, options, " +
            "show_to_ai, entity_id). Values live on each contact's attributes and render in templates as " +
            "{{cf_<key>}}; a date is stored YYYY-MM-DD and a json field holds an object (flattened to " +
            "\"key: value\" wherever it is shown). show_to_ai = the AI answering a customer sees it.",
            "GET", "/api/settings/custom-fields",
            C("contacts.view", "flows.manage", "templates.manage", "script_templates.manage", "settings.custom_fields.manage"));
        yield return Custom(st, "settings_save_custom_field", "Add or change a custom field",
            "Add a contact custom field or change one by key — the other fields are kept.",
            new[] { "PUT /api/settings/custom-fields", "GET /api/settings/custom-fields" }, SaveCustomFieldAsync,
            C("settings.custom_fields.manage"),
            new[]
            {
                A.Str("label", "Label.", req: true), A.Str("key", "Key (default: from the label)."),
                A.Str("kind", "text | select | multiselect | number | boolean | date (stored YYYY-MM-DD) | "
                              + "json (an object, up to 8000 characters — for data an outside system syncs in).",
                    oneOf: ChatBranch.Data.Contacts.ContactCustomFields.Kinds),
                A.StrList("options", "select/multiselect options."), A.Bool("required", "Required on the form."),
                A.Bool("show_to_ai", "Let the AI answering a customer see this field's value (default off — "
                                     + "keep internal fields like a credit score off)."),
                A.Any("entity_id", "Restrict the field to one entity: only that entity's company sees it in its "
                                   + "AI prompt (null = organization-wide)."),
                A.Int("position", "Order."),
            }, idempotent: true);
        yield return Custom(st, "settings_delete_custom_field", "Delete a custom field",
            "Remove a custom field definition (values already stored on contacts stay in their attributes).",
            new[] { "PUT /api/settings/custom-fields", "GET /api/settings/custom-fields" }, DeleteCustomFieldAsync,
            C("settings.custom_fields.manage"), new[] { A.Str("key", "Field key.", req: true) }, destructive: true);

        // ---- notifications ----------------------------------------------------------
        const string n = "notifications";
        yield return T(n, "notifications_list", "List my notifications",
            "Your notification inbox: new messages, account health, flows, admin and system events (paged).",
            "GET", "/api/notifications", null,
            new[]
            {
                A.StrList("category", "Categories.", oneOf: NotificationCategories), A.Bool("unread", "Only unread."),
                A.Str("q", "Text."), A.Str("from", "From (ISO date)."), A.Str("to", "To (ISO date)."),
                A.Page, A.PageSize(100),
            }, link: (_, _) => Page("Open notifications", "/notifications"));
        yield return T(n, "notifications_mark", "Mark notifications read/unread",
            "Mark some of your own notifications read (or unread again) by id.", "POST", "/api/notifications/mark", null,
            new[] { A.IntList("ids", "Notification ids.", req: true), A.Bool("read", "true = read (default), false = unread.") },
            idempotent: true);
        yield return T(n, "notifications_mark_all_read", "Mark all notifications read",
            "Mark all your notifications read (optionally one category).", "POST", "/api/notifications/read-all", null,
            new[] { A.Str("category", "Only this category.", oneOf: NotificationCategories) }, idempotent: true);
        yield return T(n, "notifications_delete", "Delete notifications",
            "Delete your notifications by id, or every read one (read=true).", "POST", "/api/notifications/delete", null,
            new[] { A.IntList("ids", "Notification ids."), A.Bool("read", "true = delete all read notifications.") },
            destructive: true);
        yield return T(n, "notifications_get_my_preferences", "Read my notification switches",
            "Which notification categories you receive, and 'only conversations assigned to me / my teams'.",
            "GET", "/api/notifications/preferences");
        yield return T(n, "notifications_set_my_preferences", "Set my notification switches",
            "Turn your own notification categories on/off and/or only_assigned.", "PUT", "/api/notifications/preferences",
            null, new[] { A.Obj("user", "{category: true|false}."), A.Bool("only_assigned", "Only conversations assigned to me / my teams.") },
            idempotent: true);
        yield return T(n, "notifications_get_org_defaults", "Read organisation notification defaults",
            "Which notification categories are on for the organisation (a muted category reaches nobody).",
            "GET", "/api/auth/notification-prefs", C("notifications.manage"));
        yield return T(n, "notifications_set_org_defaults", "Set organisation notification defaults",
            "Mute or unmute notification categories for everyone: {messages, account_health, flows, admin, system}.",
            "PUT", "/api/auth/notification-prefs", C("notifications.manage"),
            new[] { A.Obj("categories", "{category: true|false}.", req: true, @in: ArgIn.Spread) }, idempotent: true);

        yield return T(n, "notifications_push_now", "Deliver pending notifications to devices",
            "Push the unread notifications people have just been given to their registered devices, "
            + "instead of waiting for the worker.",
            "POST", "/api/push/tick", C("notifications.manage"));

        // ---- saved views ------------------------------------------------------------
        const string v = "views";
        yield return T(v, "views_list", "List my saved inbox views",
            "Your saved inbox filter tabs (name + filters, in tab order).", "GET", "/api/views", null,
            new[] { A.Str("scope", "chats (default).") });
        yield return T(v, "views_create", "Save an inbox view",
            "Save a named inbox filter tab (filters use the conversations_search keys).", "POST", "/api/views", null,
            new[] { A.Str("name", "Tab name.", req: true), A.Obj("filters", "Filter object.", req: true), A.Str("scope", "chats (default).") });
        yield return T(v, "views_update", "Edit a saved view", "Rename one of your saved inbox tabs, change its filters or move it.",
            "PATCH", "/api/views/{id:int}", null,
            new[] { A.Id("view"), A.Str("name", "Name."), A.Obj("filters", "Filters."), A.Int("position", "Position.") });
        yield return T(v, "views_delete", "Delete a saved view", "Delete one of your own saved inbox filter tabs.",
            "DELETE", "/api/views/{id:int}", null, new[] { A.Id("view") }, destructive: true);
        yield return T(v, "views_reorder", "Reorder saved views", "Reorder your own saved inbox filter tabs (ids in the new order).",
            "POST", "/api/views/reorder", null,
            new[] { A.IntList("ids", "View ids in the new order.", req: true), A.Str("scope", "chats (default).") }, idempotent: true);
    }

    /// <summary>
    /// Each binding kind has its own capability (BindingsEndpoints.Kinds). The
    /// endpoint checks the ROLE; a scoped API key must hold it too, so a key
    /// scoped to tags can't rebind users through the same route.
    /// </summary>
    private static string[] BindingCaps(string kind, bool write) => kind switch
    {
        "contact-entities" or "entity-contacts" => new[] { write ? "contacts.manage" : "contacts.view" },
        "tag-contacts" or "tag-entities" => new[] { write ? "tags.manage" : "tags.view" },
        "entity-accounts" => write ? new[] { "channels.manage" } : new[] { "users.view" },
        _ => new[] { write ? "users.manage" : "users.view" },
    };

    private static async Task<object?> BindingAsync(ToolContext ctx, JsonElement a, string method, ToolArg[] args,
        CancellationToken ct)
    {
        var kind = Need(a, "kind");
        if (!BindingKinds.Contains(kind)) throw new ToolException($"kind must be one of {string.Join(", ", BindingKinds)}.");
        var caps = BindingCaps(kind, method != "GET");
        if (!caps.Any(ctx.Capabilities.Contains))
            throw new ToolException($"The {kind} binding needs the {string.Join(" / ", caps)} capability.", 403);
        var r = ToolContext.Require(await EndpointTool.CallEndpointAsync(ctx, method, "/api/bindings/{kind}", a, args, ct: ct));
        return Compact(r.Body);
    }

    private static async Task<(JsonArray Fields, JsonElement Raw)> CustomFieldsAsync(ToolContext ctx, CancellationToken ct)
    {
        var r = ToolContext.Require(await ctx.CallAsync("GET", "/api/settings/custom-fields", ct: ct));
        var fields = r.Body.TryGetProperty("customer", out var c) && c.ValueKind == JsonValueKind.Array
            ? (JsonArray)JsonNode.Parse(c.GetRawText())!
            : new JsonArray();
        return (fields, r.Body);
    }

    private static async Task<object?> SaveCustomFieldAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var (fields, _) = await CustomFieldsAsync(ctx, ct);
        var label = Need(a, "label");
        var key = S(a, "key") is { Length: > 0 } k ? k
            : new string(label.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        var field = fields.OfType<JsonObject>().FirstOrDefault(x => x["key"]?.GetValue<string>() == key);
        var created = field == null;
        if (field == null)
        {
            field = new JsonObject { ["key"] = key, ["kind"] = "text", ["options"] = new JsonArray(), ["required"] = false, ["position"] = fields.Count };
            fields.Add(field);
        }
        field["label"] = label;
        if (S(a, "kind") is { Length: > 0 } kind) field["kind"] = kind;
        if (El(a, "options") is { ValueKind: JsonValueKind.Array } opts) field["options"] = JsonNode.Parse(opts.GetRawText());
        if (B(a, "required") is bool req) field["required"] = req;
        if (B(a, "show_to_ai") is bool ai) field["show_to_ai"] = ai;
        if (El(a, "entity_id") is { } ent && ent.ValueKind != JsonValueKind.Undefined)
            field["entity_id"] = ent.ValueKind == JsonValueKind.Null ? null : I(a, "entity_id");
        if (I(a, "position") is int pos) field["position"] = pos;
        ToolContext.Require(await ctx.CallAsync("PUT", "/api/settings/custom-fields",
            body: new JsonObject { ["customer"] = fields }, ct: ct));
        return new
        {
            saved = true, created, key, variable = $"{{{{cf_{key}}}}}",
            shown_to_ai = field["show_to_ai"]?.GetValue<bool>() ?? false,
        };
    }

    private static async Task<object?> DeleteCustomFieldAsync(ToolContext ctx, JsonElement a, CancellationToken ct)
    {
        var (fields, _) = await CustomFieldsAsync(ctx, ct);
        var key = Need(a, "key");
        var field = fields.OfType<JsonObject>().FirstOrDefault(x => x["key"]?.GetValue<string>() == key)
                    ?? throw new ToolException($"No custom field '{key}'.", 404);
        fields.Remove(field);
        ToolContext.Require(await ctx.CallAsync("PUT", "/api/settings/custom-fields",
            body: new JsonObject { ["customer"] = fields }, ct: ct));
        return new { deleted = true, key };
    }
}
