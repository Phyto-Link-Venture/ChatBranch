using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Contacts;
using ChatBranch.Data.Integrations;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Contact identities beyond what an operator types in:
/// <list type="bullet">
/// <item><b>Discovery</b> — <c>POST /api/contacts/{id}/channels/discover</c>
///   ("Find channels"): register the phone/email identities and queue WhatsApp
///   lookups; <c>POST /api/contacts/identity-checks/run</c> works the lookup
///   queue once (the background worker's seam).</item>
/// <item><b>Invite links</b> — <c>POST/GET /api/contacts/{id}/invites</c>.</item>
/// <item><b>Unknown customers</b> — <c>GET/PUT /api/inboxes/{accountId}/identify</c>
///   (the per-inbox mode), <c>GET /api/contacts/identify/{channel}/{key}</c>
///   (the chat banner's state + prefill) and <c>POST …/contact</c> (the chat's
///   "Create contact" / link quick action).</item>
/// <item><b>Google Contacts</b> — <c>GET /api/contacts/import/google</c>, the
///   caller's own connection through the same preview/mapping pipeline as a file.</item>
/// </list>
/// Every route is signed-in + capability-gated; contacts are read through the
/// contacts list's own scope (tenant, then assigned entities).
/// </summary>
public static class ContactIdentityEndpoints
{
    /// <summary>The DI registrations (one call in Program.cs).</summary>
    public static void AddContactIdentity(this IServiceCollection services)
    {
        services.AddScoped<ContactIdentityService>();
        services.AddScoped<ContactInviteService>();
        services.AddScoped(sp => new IdentityRequestService(sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<ScopingService>(), sp.GetRequiredService<ThreadStore>(),
            sp.GetRequiredService<OutboundSender>(), sp.GetService<TimeProvider>()));
        services.AddScoped<ContactImporter>();
        services.AddScoped<WhatsAppNumberChecker>();
        services.AddSingleton(new NumberCheckLimiter());
        services.AddSingleton<IGooglePeopleClient, GooglePeopleClient>();
        services.AddHostedService<ChatBranch.Api.Workers.IdentityCheckWorker>();
    }

    public record InviteRequest(string? Channel, List<string>? Channels, int? AccountId);

    public static void MapContactIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- discovery ("Find channels") -------------------------------------
        app.MapPost("/api/contacts/{id:int}/channels/discover", async (int id, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, ContactIdentityService identities) =>
        {
            var contact = await ContactsEndpoints.ScopeContacts(db.Contacts, me, db, sc)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound("Contact not found.");
            var r = identities.Discover(contact, "auto", recheck: true);
            return Results.Ok(new
            {
                contact_id = contact.Id,
                added = r.Added,
                removed = r.Removed,
                queued = r.Queued,
                lookup_available = r.LookupAvailable,
                lookup_note = r.LookupAvailable ? null : ContactIdentityService.NoQrDetail,
                identities = identities.For(contact.Id).Select(ContactIdentityService.Payload),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        // one pass of the WhatsApp lookup queue (the worker's manual seam)
        app.MapPost("/api/contacts/identity-checks/run", async (WhatsAppNumberChecker checker, CancellationToken ct) =>
        {
            var p = await checker.RunOnceAsync(ct);
            return Results.Ok(new
            {
                @checked = p.Checked, cached = p.Cached, unknown = p.Unknown,
                rate_limited = p.RateLimited, remaining = p.Remaining,
            });
        }).RequireAuthorization(AuthPolicies.AdminPlus).RequireCapability("contacts.manage");

        // ---- invite links ------------------------------------------------------
        app.MapPost("/api/contacts/{id:int}/invites", async (int id, InviteRequest? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, ContactInviteService invites, IServiceProvider sp) =>
        {
            var contact = await ContactsEndpoints.ScopeContacts(db.Contacts, me, db, sc)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound("Contact not found.");

            var requested = new List<string>();
            foreach (var raw in (body?.Channels ?? new List<string>()).Append(body?.Channel ?? ""))
            {
                var ch = ContactInviteService.NormalizeChannel(raw);
                if (ch == null) return Bad($"Unknown channel \"{raw}\".");
                if (ch.Length > 0 && !requested.Contains(ch)) requested.Add(ch);
            }
            if (body?.AccountId is int aid && !await db.ChannelAccounts.AnyAsync(a => a.Id == aid))
                return Bad("account_id must be an existing channel account.");

            // one channel = a single-use code for it; several / none = one code that binds once per app
            var invite = invites.Create(contact, requested.Count == 1 ? requested[0] : "", body?.AccountId,
                me.Identity?.Name ?? "");
            var telegram = sp.GetService<ITelegramClient>();
            var links = await invites.LinksAsync(invite, contact, requested,
                telegram == null ? null : async account => await TelegramUsername(db, telegram, account));
            var payload = invites.Payload(invite, links);
            return Results.Json(new Dictionary<string, object?>(payload)
            {
                ["invite"] = invites.Payload(invite),
            }, statusCode: 201);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        app.MapGet("/api/contacts/{id:int}/invites", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, ContactInviteService invites) =>
        {
            var contact = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound("Contact not found.");
            var rows = await db.ContactInvites.AsNoTracking().Where(i => i.ContactId == id)
                .OrderByDescending(i => i.Id).Take(50).ToListAsync();
            return Results.Ok(new { invites = rows.Select(r => invites.Payload(r)) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.view");

        // ---- per-inbox "unknown customers" setting ---------------------------
        app.MapGet("/api/inboxes/{accountId:int}/identify", async (int accountId, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var a = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId);
            if (a == null || !await InboxInScopeAsync(db, sc, me, a)) return NotFound("Inbox not found.");
            return Results.Ok(IdentifyPayload(a, sc));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.view");

        app.MapPut("/api/inboxes/{accountId:int}/identify", async (int accountId, JsonElement body,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var a = await db.ChannelAccounts.FirstOrDefaultAsync(x => x.Id == accountId);
            if (a == null || !await InboxInScopeAsync(db, sc, me, a)) return NotFound("Inbox not found.");
            var err = IdentifySettings.Apply(a, Str(body, IdentifySettings.ModeKey), Str(body, IdentifySettings.MessageKey));
            if (err != null) return Bad(err);
            ChatBranch.Data.Audit.AuditTrail.Add(db, "inbox.identify", $"account:{a.Id}",
                $"{a.Name}: unknown customers = {IdentifySettings.Mode(sc, a, a.EntityId)}", entityId: a.EntityId);
            await db.SaveChangesAsync();
            return Results.Ok(IdentifyPayload(a, sc));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");

        // ---- unknown customer: state for the chat + the quick action -----------
        app.MapGet("/api/contacts/identify/{channel}/{key}", async (string channel, string key, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IdentityRequestService identity) =>
        {
            var thread = await VisibleThread(db, sc, me, channel, key);
            if (thread == null) return NotFound("Conversation not found.");
            var req = identity.For(thread.Id);
            var suggest = identity.Suggest(thread, req);
            return Results.Ok(new
            {
                channel = thread.Channel,
                key = thread.Key,
                linked = thread.ContactId != null,
                contact_id = thread.ContactId,
                mode = IdentifySettings.Mode(sc, identity.AccountFor(thread), thread.EntityId),
                request = req == null ? null : IdentityRequestService.Payload(req),
                suggested = new { name = suggest.Name, phone = suggest.Phone, email = suggest.Email },
                can_create = await ShellScope.HasCapAsync(db, me, "contacts.manage"),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.view");

        app.MapPost("/api/contacts/identify/{channel}/{key}/contact", async (string channel, string key,
            JsonElement body, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            IdentityRequestService identity, InboundProcessor inbound) =>
        {
            var thread = await VisibleThread(db, sc, me, channel, key, tracked: true);
            if (thread == null) return NotFound("Conversation not found.");
            var actor = me.Identity?.Name ?? "";
            Contact contact;
            string state, note;
            if (Int(body, "contact_id") is int existingId)
            {
                var found = await ContactsEndpoints.ScopeContacts(db.Contacts, me, db, sc)
                    .FirstOrDefaultAsync(c => c.Id == existingId);
                if (found == null) return NotFound("Contact not found.");
                contact = found;
                state = IdentityStates.Linked;
                note = $"{actor} linked this conversation to {contact.Name}.";
            }
            else
            {
                if (thread.ContactId != null)
                    return Results.Json(new { detail = "This conversation is already linked to a contact." },
                        statusCode: 409);
                var cc = PhoneNormalizer.CountryCode(sc, thread.EntityId);
                var name = (Str(body, "name") ?? "").Trim();
                var rawPhone = (Str(body, "phone") ?? "").Trim();
                var phone = rawPhone.Length > 0 ? PhoneNormalizer.ToE164(rawPhone, cc) : "";
                if (rawPhone.Length > 0 && phone.Length == 0) return Bad($"\"{rawPhone}\" is not a phone number.");
                var email = (Str(body, "email") ?? "").Trim().ToLowerInvariant();
                if (email.Length > 0 && (!email.Contains('@') || email.Contains(' ')))
                    return Bad($"\"{email}\" is not an email address.");
                if (name.Length == 0) return Bad("name is required.");
                int? entityId = null;
                var slug = Str(body, "entity");
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    entityId = await InboxEndpoints.EntityId(db, slug);
                    if (entityId == null) return Bad("Unknown entity.");
                    var allowed = await AllowedEntityIdsAsync(db, sc, me);
                    if (allowed != null && !allowed.Contains(entityId.Value))
                        return Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403);
                }
                var tenant = identity.TenantFor(thread) ?? TenantId(me);
                contact = identity.CreateContact(identity.For(thread.Id), thread, identity.AccountFor(thread), tenant,
                    new IdentityDetails(name, phone, email, false), actor, tagSelfIdentified: false,
                    entityOverride: entityId, language: Str(body, "language"));
                state = IdentityStates.Created;
                note = $"{actor} created the contact {contact.Name} from this conversation.";
            }
            var waiting = identity.ResolveByHand(thread, contact, state, note);
            ChatBranch.Data.Audit.AuditTrail.Add(db,
                state == IdentityStates.Created ? "contact.created_from_chat" : "thread.link",
                $"{thread.Channel}:{thread.Key}", $"contact {contact.Id} ({contact.Name})", entityId: thread.EntityId);
            await db.SaveChangesAsync();
            // the customer was waiting on "who are you?": now that they are known,
            // the normal AI step answers them once (coverage gate, chatbot tick)
            if (waiting && CustomerWaiting(db, thread))
                try { inbound.AfterSimulatedInbound(thread); } catch { }
            return Results.Ok(new
            {
                contact = new { id = contact.Id, name = contact.Name, phone = contact.Phone, email = contact.Email },
                contact_id = contact.Id,
                contact_name = contact.Name,
                request = identity.For(thread.Id) is { } r ? IdentityRequestService.Payload(r) : null,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        // ---- Google Contacts import (preview; commit goes through POST /import) --
        app.MapGet("/api/contacts/import/google", async (string? mode, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IntegrationResolver resolver, GoogleOAuth oauth, IGooglePeopleClient people,
            ContactImporter importer, CancellationToken ct) =>
        {
            var uid = await db.Users.AsNoTracking().Where(u => u.Username == me.Identity!.Name)
                .Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
            var row = resolver.Active(GoogleContactsProvider, null, uid);
            var token = await oauth.AccessTokenAsync(row, ct);
            if (token.Length == 0)
                return Results.Json(new
                {
                    connected = false,
                    detail = "Connect Google first — Integrations → Google Contacts, then come back to import.",
                    connect_url = "/integrations",
                }, statusCode: 409);
            var (list, error) = await people.ConnectionsAsync(token, ContactImporter.MaxRows + 1, ct);
            if (error != null && list.Count == 0)
                return Results.Json(new { connected = true, detail = error }, statusCode: 502);
            var table = ImportReaders.Google(list);
            var opt = new ImportOptions
            {
                Preview = true, Mode = ContactImporter.Modes.Contains(mode ?? "") ? mode! : ContactImporter.ModeUpdateEmpty,
                TenantId = TenantId(me), Discover = false,
            };
            var mapping = importer.Suggest(table.Headers);
            var report = importer.Run(table, mapping, opt);
            var payload = ContactsEndpoints.ImportPayload(importer, table, mapping, opt, report);
            payload["connected"] = true;
            payload["warning"] = error;
            payload["rows"] = table.Rows;
            return Results.Ok(payload);
        }).RequireAuthorization(AuthPolicies.AdminPlus).RequireCapability("contacts.manage");
    }

    /// <summary>The integrations-catalog provider id for Google Contacts.</summary>
    public const string GoogleContactsProvider = "google_contacts";

    private static object IdentifyPayload(ChannelAccount a, ScopingService sc) => new
    {
        account_id = a.Id,
        channel = a.Channel,
        identify_unknown = IdentifySettings.ModeWire(a),
        identify_message = IdentifySettings.Message(a) ?? "",
        effective = IdentifySettings.Mode(sc, a, a.EntityId),
        modes = IdentifyModes.All,
        default_messages = new
        {
            en = IdentityRequestService.AskText("en", a.Channel, 1, null, ""),
            ms = IdentityRequestService.AskText("ms", a.Channel, 1, null, ""),
        },
    };

    /// <summary>Learn (and remember) a Telegram bot's @username for its t.me link.</summary>
    private static async Task<string?> TelegramUsername(AppDbContext db, ITelegramClient client, ChannelAccount account)
    {
        try
        {
            if (!TelegramBot.Configured(account)) return null;
            var me = await client.GetMeAsync(account);
            var user = me.Ok ? me.Str("username") : "";
            if (user.Length == 0) return null;
            var tracked = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == account.Id);
            if (tracked != null)
            {
                tracked.Config = new Dictionary<string, object?>(tracked.Config) { ["bot_username"] = user };
                await db.SaveChangesAsync();
            }
            return user;
        }
        catch { return null; }
    }

    /// <summary>An organization-wide inbox, or one inside the caller's entities.</summary>
    private static async Task<bool> InboxInScopeAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        ChannelAccount a)
    {
        if (a.EntityId is not int eid) return true;
        var allowed = await AllowedEntityIdsAsync(db, sc, me);
        return allowed == null || allowed.Contains(eid);
    }

    /// <summary>A conversation the caller may see (the inbox rule), or null.</summary>
    internal static async Task<Thread?> VisibleThread(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        string channel, string key, bool tracked = false)
    {
        IQueryable<Thread> q = db.Threads.Include(t => t.Account).Include(t => t.Contact);
        if (!tracked) q = q.AsNoTracking();
        var thread = await q.FirstOrDefaultAsync(t => t.Channel == channel && t.Key == key);
        if (thread == null) return null;
        var ids = ShellScope.ThreadEntityIds(db, sc, me, null);
        if (ids != null && thread.EntityId is int eid && !ids.Contains(eid)) return null;
        return thread;
    }

    /// <summary>
    /// The entities the caller may assign contacts to: null = any (superadmin),
    /// admins their tenant's, everyone else their assigned subtree (tenant-wide
    /// when nothing is assigned — the contacts rule).
    /// </summary>
    internal static async Task<HashSet<int>?> AllowedEntityIdsAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me)
    {
        var role = ShellScope.RoleOf(me);
        if (role == Roles.SuperAdmin) return null;
        var tenant = TenantId(me);
        var tenantIds = await db.Entities.AsNoTracking()
            .Where(e => tenant == null || e.TenantId == tenant).Select(e => e.Id).ToListAsync();
        if (role == Roles.Admin) return tenantIds.ToHashSet();
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        if (assigned.Count == 0) return tenantIds.ToHashSet();
        return sc.EntitySubtreeIds(assigned).ToHashSet();
    }

    /// <summary>The last customer-facing message is theirs, or our "who are you?" ask.</summary>
    private static bool CustomerWaiting(AppDbContext db, Thread thread)
    {
        var last = db.Messages.AsNoTracking().Where(m => m.ThreadId == thread.Id && !m.Private)
            .OrderByDescending(m => m.Id).FirstOrDefault();
        return last != null && (last.Direction == MessageDirection.In || last.Source == MessageSource.System);
    }

    internal static int? TenantId(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;

    private static string? Str(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => el.ToString(),
        };
    }

    private static int? Int(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);
    private static IResult NotFound(string detail) => Results.Json(new { detail }, statusCode: 404);
}
