using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Contacts;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Data.Tags;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Contacts (DATABASE-FIRST, port of <c>calls/contacts.py</c> +
/// <c>calls/views.py</c>), the per-contact unified channel hub, the SIP call
/// history, bulk import (<c>crm/import_views.py</c>) and script templates.
///
/// WIRE FORMAT IS snake_case — every payload here is built INLINE with the same
/// keys the Django views emit, NOT through the camelCase <see cref="Dtos"/>
/// mappers. The dashboard's <c>Contact</c> type (frontend/src/lib/mocks.ts) is
/// the contract: `company`, `records`, `history`, `entities` are all load-bearing
/// — <c>primaryRecord(c) = c.records[0]</c> throws outright without `records`.
/// </summary>
public static class ContactsEndpoints
{
    // `attributes` = the Custom Fields values (Settings → Custom Fields defines
    // the schema); merged onto the row's free-form JSON, which the AI reads.
    /// <remarks>
    /// PATCH is a PARTIAL update: every field is optional and an omitted one is
    /// left alone. Name/Phone were non-nullable, so a body carrying only
    /// `attributes` (e.g. a custom-fields-only save) assigned null over the
    /// contact's name and 500'd on the NOT NULL column.
    /// </remarks>
    /// The company binds as <c>companyName</c> (the MCP tools) or
    /// <c>company_name</c> (the dashboard, as on /api/v1/contacts).
    public record ContactWrite(string? Name, string? Phone, string? Email, string? Language,
        string? CompanyName, List<string>? Tags,
        Dictionary<string, object?>? Attributes = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("company_name")] string? CompanySnake = null)
    {
        public string? Company => CompanyName ?? CompanySnake;
    }
    public record IdentityWrite(string Channel, string Address);

    /// <summary>contacts.py _HISTORY_LIMIT — recent touchpoints, not a full feed.</summary>
    private const int HistoryLimit = 8;

    /// <summary>Channel display order for the hub (voice is appended last).</summary>
    private static readonly string[] HubChatChannels =
        { Channels.WhatsApp, Channels.Email, Channels.Facebook, Channels.Instagram, Channels.Telegram,
          Channels.Sms, Channels.Line, Channels.Viber, Channels.WeChat, Channels.Zalo, Channels.TikTok,
          Channels.Douyin };

    /// <summary>Accounts in these states are skipped by routing — not usable for outreach.</summary>
    private static readonly string[] DownHealth = { Health.Disconnected, Health.Banned };

    public static void MapContactsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- contact list / detail --------------------------------------------
        app.MapGet("/api/contacts", async (string? q, int? limit, string? entity, string? tag,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var query = ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc);
            if (entity is NoEntity)
            {
                // "Unassigned (no entity)": contacts linked to no entity at all —
                // found here so they can be given one
                query = query.Where(c => !c.EntityLinks.Any());
            }
            else if (!string.IsNullOrWhiteSpace(entity) && entity is not ("all" or "__all__"))
            {
                // Narrow to the picked outlet AND everything under it: a contact
                // on a child branch is visible to its parent, never the reverse.
                // A contact belongs there when linked to it OR when it has a
                // conversation there (a chat-made contact not linked yet).
                var picked = await InboxEndpoints.EntityId(db, entity);
                var ids = picked is int pid ? sc.PickSubtree(pid) : new List<int>();
                query = InEntities(query, ids);
            }
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(c => c.Name.Contains(q) || c.Phone.Contains(q) || c.Email.Contains(q));
            if (!string.IsNullOrWhiteSpace(tag))
            {
                // ?tag=<slug> — contacts carrying that registry tag
                var tagSlug = tag.Trim().ToLowerInvariant();
                query = query.Where(c => db.TagAssignments.Any(a => a.TargetType == TagService.ContactTarget
                                                                  && a.TargetId == c.Id && a.Tag!.Slug == tagSlug));
            }

            var rows = await query.OrderByDescending(c => c.UpdatedAt).Take(limit ?? 200).ToListAsync();
            return Results.Ok(new { contacts = await ContactItemsAsync(db, rows) });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // the Contacts page, the inbox (tag filter, call batches) and the Users
          // page's customer-login picker all list contacts
          .RequireAnyCapability("contacts.view", "chats.view", "users.manage");

        app.MapPost("/api/contacts", async (ContactWrite b, AppDbContext db, ClaimsPrincipal me, ScopingService sc,
            ChatBranch.Data.Contacts.ContactIdentityService identities) =>
        {
            // create (unlike PATCH) genuinely needs an identity: a name AND a way
            // to reach them — a phone, an email, or both
            var name = (b.Name ?? "").Trim();
            var phone = (b.Phone ?? "").Trim();
            var email = (b.Email ?? "").Trim();
            if (name.Length == 0 || (phone.Length == 0 && email.Length == 0))
                return Results.Json(new { detail = "name and a phone or email are required." }, statusCode: 400);
            if (email.Length > 0 && !ValidEmail(email))
                return Results.Json(new { detail = "That email address doesn't look right." }, statusCode: 400);
            var entityIds = CreationEntities(db, sc, me, new List<int>());
            if (await DuplicateError(db, sc, me, TenantId(me), phone, email, entityIds) is { } dup) return dup;
            var row = new Contact
            {
                Name = name, Phone = phone, Email = email, Language = b.Language ?? "en",
                CompanyName = b.Company ?? "", Tags = b.Tags ?? new(),
                Attributes = b.Attributes ?? new(),
                TenantId = TenantId(me),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Contacts.Add(row);
            await db.SaveChangesAsync();
            foreach (var eid in entityIds) ContactCompanies.LinkToEntity(db, row.Id, eid);
            if (entityIds.Count > 0) await db.SaveChangesAsync();
            // tag names → registry tags (Contact.Tags is the registry's cache)
            if (row.Tags.Count > 0) TagService.ApplyContactNames(db, row.Id, row.Tags, me.Identity?.Name ?? "");
            // the phone's WhatsApp / voice (/ SMS) and the email become identities
            try { identities.Discover(row); } catch { /* discovery never fails a create */ }
            return Results.Ok((await ContactItemsAsync(db, new List<Contact> { row })).First());
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        app.MapGet("/api/contacts/{id:int}", async (int id, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var c = await ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                .FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound("Contact not found.");
            return Results.Ok((await ContactItemsAsync(db, new List<Contact> { c })).First());
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.view");

        app.MapMethods("/api/contacts/{id:int}", new[] { "PATCH" },
            async (int id, ContactWrite b, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
                ChatBranch.Data.Contacts.ContactIdentityService identities) =>
        {
            var c = await ScopeContacts(db.Contacts, me, db, sc).FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound("Contact not found.");
            var (phoneBefore, emailBefore) = (c.Phone, c.Email);
            if (b.Email is { Length: > 0 } newEmail && !ValidEmail(newEmail.Trim()))
                return Results.Json(new { detail = "That email address doesn't look right." }, statusCode: 400);
            if ((b.Phone != null && b.Phone != c.Phone) || (b.Email != null && b.Email != c.Email))
            {
                // the per-company duplicate rule, against the contact's own companies
                var links = await db.ContactEntities.AsNoTracking().Where(l => l.ContactId == c.Id)
                    .Select(l => l.EntityId).ToListAsync();
                if (await DuplicateError(db, sc, me, c.TenantId,
                        b.Phone != null && b.Phone != c.Phone ? b.Phone : null,
                        b.Email != null && b.Email != c.Email ? b.Email : null, links, c.Id) is { } dup)
                    return dup;
            }
            if (b.Name != null) c.Name = b.Name;
            if (b.Phone != null) c.Phone = b.Phone;
            if (b.Email != null) c.Email = b.Email;
            if (b.Language != null) c.Language = b.Language;
            if (b.Company != null) c.CompanyName = b.Company;
            if (b.Tags != null) c.Tags = b.Tags;
            if (b.Attributes != null)
            {
                // Merge (not replace) so fields this form doesn't know about survive.
                foreach (var kv in b.Attributes) c.Attributes[kv.Key] = kv.Value;
            }
            c.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            if (b.Tags != null) TagService.ApplyContactNames(db, c.Id, b.Tags, me.Identity?.Name ?? "");
            if (c.Phone != phoneBefore || c.Email != emailBefore)
                try { identities.Discover(c); } catch { /* discovery never fails an edit */ }
            return Results.Ok((await ContactItemsAsync(db, new List<Contact> { c })).First());
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        // ---- unified channel hub ----------------------------------------------
        // One aggregated view of every channel identity this contact is reachable
        // on: threads linked by the Thread.contact FK PLUS the digits-endswith
        // phone fallback for WhatsApp threads created before linking, each
        // identity's carrying account (+ its health), last activity and history
        // counts, with a voice section built from CallRecord.
        app.MapGet("/api/contacts/{id:int}/channels", async (int id, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            ChatBranch.Data.Contacts.ContactIdentityService identityRegistry) =>
        {
            var contact = await ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound("Contact not found.");
            // registered identities (discovered from the phone / email, with the
            // WhatsApp lookup verdict) — merged into the thread-based view below
            var registry = identityRegistry.For(contact.Id);

            var digits = Digits(contact.Phone);
            // Same last-9-digits comparison whatsapp.store._match_contact does
            // (thread keys are already digit-normalized wa_ids).
            var tail = digits.Length >= 9 ? digits[^9..] : "";
            var threads = await db.Threads.AsNoTracking().Include(t => t.Account)
                .Where(t => t.ContactId == id ||
                            (tail != "" && t.Channel == Channels.WhatsApp &&
                             t.ContactId == null && t.Key.EndsWith(tail)))
                .ToListAsync();

            // One pass over this contact's messages — never N+1 per thread.
            var threadIds = threads.Select(t => t.Id).ToList();
            var msgs = await db.Messages.AsNoTracking()
                .Where(m => threadIds.Contains(m.ThreadId))
                .Select(m => new { m.ThreadId, m.Direction, m.CreatedAt })
                .ToListAsync();
            var byThread = msgs.GroupBy(m => m.ThreadId).ToDictionary(g => g.Key, g => new
            {
                Count = g.Count(),
                In = g.Count(m => m.Direction == MessageDirection.In),
                Out = g.Count(m => m.Direction == MessageDirection.Out),
                Last = g.Max(m => (DateTime?)m.CreatedAt),
                LastIn = g.Where(m => m.Direction == MessageDirection.In)
                    .Max(m => (DateTime?)m.CreatedAt),
                LastOut = g.Where(m => m.Direction == MessageDirection.Out)
                    .Max(m => (DateTime?)m.CreatedAt),
            });

            // Channel-level configuration, resolved once.
            var accounts = await db.ChannelAccounts.AsNoTracking().ToListAsync();
            var emailConfigured = accounts.Any(a => a.Channel == Channels.Email && AccountConfigured(a));

            var channels = new List<object>();
            foreach (var ch in HubChatChannels)
            {
                var ts = threads.Where(t => t.Channel == ch).ToList();
                // Chat channels appear once the contact has a thread on them;
                // email also appears (empty, disconnected) when an address is on file.
                if (ts.Count == 0 && !(ch == Channels.Email && contact.Email.Length > 0)
                    && !registry.Any(r => r.Channel == ch)) continue;

                var pairs = ts.Select(t =>
                {
                    byThread.TryGetValue(t.Id, out var agg);
                    var row = t.Account;

                    string? lastDirection;
                    if (agg?.LastIn is DateTime li && agg.LastOut is DateTime lo)
                        lastDirection = li >= lo ? MessageDirection.In : MessageDirection.Out;
                    else if (agg?.LastIn != null) lastDirection = MessageDirection.In;
                    else if (agg?.LastOut != null) lastDirection = MessageDirection.Out;
                    else lastDirection = null;

                    bool? windowOpen = null;
                    var usable = true; // non-whatsapp identities defer to the channel check
                    if (t.Channel == Channels.WhatsApp)
                    {
                        if (row == null)
                            windowOpen = true;                       // sandbox thread — no Meta window
                        else if (row.Provider == Providers.Baileys)
                            windowOpen = true;                       // WhatsApp Web has no 24h window
                        else
                            windowOpen = agg?.LastIn is DateTime lin &&
                                         DateTime.UtcNow - lin < TimeSpan.FromHours(24);
                        // Sandbox threads resolve no account → not connected.
                        usable = row != null && AccountConfigured(row) &&
                                 !DownHealth.Contains(row.HealthStatus);
                    }
                    else if (ChatBranch.Data.Channels.SocialWindow.Applies(t.Channel))
                    {
                        // Messenger / Instagram: Meta's 24h window on a live page
                        windowOpen = ChatBranch.Data.Channels.SocialWindow.ListWindow(
                            ChatBranch.Data.Channels.SocialWindow.Live(row, t.Channel), agg?.LastIn,
                            DateTime.UtcNow).Open;
                    }
                    else if (ChatBranch.Data.Channels.ChannelWindow.Applies(t.Channel))
                    {
                        // WeChat 48h, Zalo 7 days, TikTok / Douyin 48h on a live account
                        windowOpen = ChatBranch.Data.Channels.ChannelWindow.ListWindow(t.Channel,
                            ChatBranch.Data.Channels.ChannelWindow.Live(row, t.Channel), agg?.LastIn,
                            DateTime.UtcNow).Open;
                    }

                    var payload = new Dictionary<string, object?>
                    {
                        ["key"] = t.Key,
                        ["address"] = t.Address.Length > 0 ? t.Address : t.Key,
                        ["name"] = t.Name.Length > 0 ? t.Name
                            : (t.Address.Length > 0 ? t.Address : t.Key),
                        ["account_id"] = row?.Id.ToString(),
                        ["account_name"] = row?.Name,
                        ["account_provider"] = row == null ? null
                            : (row.Provider.Length > 0 ? row.Provider : Providers.Cloud),
                        ["account_health"] = row?.HealthStatus ?? Health.Unknown,
                        ["last_active"] = Iso(agg?.Last),
                        ["message_count"] = agg?.Count ?? 0,
                        ["inbound_count"] = agg?.In ?? 0,
                        ["outbound_count"] = agg?.Out ?? 0,
                        ["last_direction"] = lastDirection,
                        ["opted_out"] = t.OptedOut,
                        ["window_open"] = windowOpen,
                    };
                    RegistryFields(payload, registry.FirstOrDefault(r => r.Channel == t.Channel
                        && r.Address == (t.Channel == Channels.Email ? t.Key.ToLowerInvariant() : t.Key)));
                    return (payload, usable);
                })
                .OrderByDescending(p => (string?)p.payload["last_active"] ?? "")
                .ToList();

                var identities = pairs.Select(p => p.payload).ToList();
                // registered-only identities: no conversation yet on this address
                foreach (var r in registry.Where(r => r.Channel == ch))
                    if (!ts.Any(t => (ch == Channels.Email ? t.Key.ToLowerInvariant() : t.Key) == r.Address))
                        identities.Add(RegistryIdentity(r));

                // "connected" is deliberately simple, per channel.
                var connected = ch switch
                {
                    Channels.WhatsApp => pairs.Any(p => p.usable)
                        // a registered number we can reach on a working WhatsApp account
                        || (registry.Any(r => r.Channel == ch && r.Verification != IdentityVerdicts.NotOnWhatsApp)
                            && accounts.Any(a => a.Channel == ch && AccountConfigured(a) && !DownHealth.Contains(a.HealthStatus))),
                    Channels.Email => (ts.Count > 0 || registry.Any(r => r.Channel == ch)) && emailConfigured,
                    _ => ts.Count > 0 && accounts.Any(a => a.Channel == ch && a.AccessToken.Length > 0),
                };

                channels.Add(new Dictionary<string, object?>
                {
                    ["channel"] = ch,
                    ["connected"] = connected,
                    ["linked_accounts"] = identities
                        .Select(i => (string?)i["account_id"]).Where(a => a != null).Distinct().Count(),
                    ["last_active"] = identities.Select(i => (string?)i["last_active"])
                        .Where(a => a != null).OrderByDescending(a => a).FirstOrDefault(),
                    ["identities"] = identities,
                });
            }

            // SMS — registered identities only (an `sms` channel account exists)
            var sms = registry.Where(r => r.Channel == ChatBranch.Data.Contacts.ContactIdentityService.SmsChannel)
                .Select(RegistryIdentity).ToList();
            if (sms.Count > 0)
                channels.Add(new Dictionary<string, object?>
                {
                    ["channel"] = ChatBranch.Data.Contacts.ContactIdentityService.SmsChannel,
                    ["connected"] = true,
                    ["linked_accounts"] = 0,
                    ["last_active"] = null,
                    ["identities"] = sms,
                });

            // Voice — from CallRecord (FK-linked, plus phone-matched rows mirrored
            // from the voice agent's file store before any link existed).
            var callRows = await CallsForAsync(db, contact);
            var lastCall = callRows
                .OrderByDescending(r => r.EndedAt.Length > 0 ? r.EndedAt : r.StartedAt)
                .FirstOrDefault();
            channels.Add(new Dictionary<string, object?>
            {
                ["channel"] = "voice",
                ["connected"] = contact.Phone.Length > 0,
                ["calls"] = callRows.Count,
                ["last_call"] = lastCall == null ? null
                    : NullIfEmpty(lastCall.EndedAt.Length > 0 ? lastCall.EndedAt : lastCall.StartedAt),
                ["last_outcome"] = lastCall?.Outcome,
                ["number"] = registry.FirstOrDefault(r => r.Channel == ChatBranch.Data.Contacts.ContactIdentityService.VoiceChannel)
                    ?.Address ?? (contact.Phone.Length > 0 ? contact.Phone : null),
                ["registered"] = registry.Any(r => r.Channel == ChatBranch.Data.Contacts.ContactIdentityService.VoiceChannel),
            });

            var lookup = ChatBranch.Data.Contacts.ContactIdentityService.LookupAccounts(db).Count > 0;
            return Results.Ok(new
            {
                contact_id = contact.Id,
                name = contact.Name,
                phone = contact.Phone,
                email = contact.Email,
                channels,
                // discovery + invites (ChannelHub: verdict chips, "Find channels", "Invite to another app")
                identities = registry.Select(ChatBranch.Data.Contacts.ContactIdentityService.Payload),
                lookup_available = lookup,
                lookup_note = lookup
                    ? "WhatsApp numbers are checked through your connected QR number — never by messaging them."
                    : ChatBranch.Data.Contacts.ContactIdentityService.NoQrDetail,
                no_lookup_channels = ChatBranch.Data.Contacts.ContactIdentityService.NoLookupChannels,
                invite_channels = ChatBranch.Data.Contacts.ContactInviteService.LinkChannels
                    .Where(ch => accounts.Any(a => a.Channel == ch)).ToList(),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // rendered on the contact page AND in the chat's info panel
          .RequireAnyCapability("contacts.view", "chats.view");

        // Register a channel identity by hand (the counterpart to linking from an
        // existing chat): creates or claims the thread and links it, so a number
        // the operator knows about is reachable without waiting for an inbound.
        app.MapPost("/api/contacts/{id:int}/channels", async (int id, IdentityWrite b,
            AppDbContext db, ThreadStore store, ScopingService sc, ClaimsPrincipal me,
            ChatBranch.Data.Contacts.ContactIdentityService identities) =>
        {
            // the contact must be in the caller's scope (the contacts rule), and an
            // existing conversation in their inbox scope — out of scope = not found
            var contact = await ScopeContacts(db.Contacts, me, db, sc).FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound("Contact not found.");
            var thread = store.GetThread(b.Channel, b.Address);
            if (thread != null && !ChatBranch.Api.Auth.ThreadAccessFilter.InScope(db, sc, me, thread.EntityId))
                return NotFound("Conversation not found.");
            if (thread != null && thread.ContactId != null && thread.ContactId != id)
                return Results.Json(new
                {
                    detail = $"That {b.Channel} identity is already linked to " +
                             "another contact. Unlink it from their chat first.",
                }, statusCode: 409);
            // store and this endpoint share the same scoped DbContext, so the
            // returned thread is already tracked — just link and save.
            thread ??= store.GetOrCreateThread(b.Channel, b.Address, contact.Name);
            thread.ContactId = id;
            thread.Contact = contact;
            // The receiving account decides the entity, so it must be loaded
            // before SyncThreadEntity runs — an unloaded nav would silently
            // demote the thread to the contact's entity.
            if (thread.AccountId is int accountId && thread.Account == null)
                thread.Account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == accountId);
            sc.SyncThreadEntity(thread, save: false);
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            // a WhatsApp number added by hand gets its lookup verdict like the rest
            try { identities.Discover(contact); } catch { }
            return Results.Json(new
            {
                ok = true, channel = b.Channel, key = thread.Key, contact_id = id,
            }, statusCode: 201);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        // ---- SIP call history ---------------------------------------------------
        // Read-only: the transcript + outcome per AI voice call, rendered inline in
        // the /chats message stream and on the contact detail page. Kept cheap so
        // it stays pollable (no LiveKit/agent plumbing on this path).
        app.MapGet("/api/contacts/{id:int}/calls", async (int id, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            ChatBranch.Data.Channels.CallRecordings recordings) =>
        {
            var contact = await ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound("Contact not found.");

            var calls = (await CallsForAsync(db, contact))
                .Select(r => new
                {
                    call_id = r.CallId,
                    started_at = r.StartedAt,
                    ended_at = r.EndedAt,
                    summary = r.Summary,
                    outcome = r.Outcome.Length > 0 ? r.Outcome : "unclear",
                    followup_date = r.FollowupDate,
                    needs_human = r.NeedsHuman,
                    // the voice agent's WAV of this call (data/recordings/{call_id}.wav),
                    // streamed auth-gated by GET /api/calls/recording/{call_id}
                    recording = recordings.ResolveAgentCall(r.CallId) != null ? $"{r.CallId}.wav" : null,
                    transcript = ParseTranscript(r.Transcript),
                })
                .OrderByDescending(x => x.ended_at.Length > 0 ? x.ended_at : x.started_at)
                .ToList();
            return Results.Ok(new { calls });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // rendered on the contact page AND in the chat's info panel
          .RequireAnyCapability("contacts.view", "chats.view");

        // ---- bulk import --------------------------------------------------------
        // Parse + upsert; `?preview=1` computes the SAME plan without writing, so
        // the modal can show created/updated/skipped and let the operator correct
        // the column mapping before committing.
        // NB: the multipart body is read off HttpContext rather than bound as an
        // IFormFile parameter — parameter binding is what would drag in the
        // antiforgery filter, and this API authenticates by bearer token.
        app.MapPost("/api/contacts/import", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, ChatBranch.Data.Contacts.ContactImporter importer) =>
                await ImportAsync(ctx, db, sc, me, importer))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        // ---- script templates --------------------------------------------------
        app.MapGet("/api/calls/script-templates", async (string? entity, int? contact_id,
            AppDbContext db, ScopingService sc, FlowEngine flows, ClaimsPrincipal me) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = await ShellScope.ScopeLibrary(db.ScriptTemplates.AsNoTracking(), db, sc, me, entityId)
                .Include(s => s.Entity)
                .OrderBy(s => s.Name).ToListAsync();

            // ?contact_id=<pk> personalizes `rendered` with that contact's CRM
            // variables — the same catalog canned responses and the flow builder
            // render from. A failure there must not break the template list.
            Dictionary<string, string>? variables = null;
            if (contact_id is int cid)
            {
                // only a contact the caller may see (no other outlet's customer data)
                var contact = await ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                    .FirstOrDefaultAsync(c => c.Id == cid);
                if (contact != null)
                    try { variables = flows.VariablesFor(contact); } catch { variables = null; }
            }
            // Frontend (ScriptTemplatesSettings) reads `templates` — a `scripts`
            // key left it undefined and crashed on `sorted.length`.
            var scopes = LibraryEntities.Load(db, rows);
            return Results.Ok(new { templates = rows.Select(t => ScriptItem(t, variables, scopes)) });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // Templates → Call scripts, the chat's Script call button, the flow builder's call step
          .RequireAnyCapability("script_templates.manage", "chats.send", "flows.manage");

        app.MapPost("/api/calls/script-templates", async (JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (!await CanManageScripts(db, me)) return ScriptsForbidden();
            var name = (AsString(body, "name") ?? "").Trim();
            var content = (AsString(body, "content") ?? "").Trim();
            if (name.Length == 0) return Bad("name is required.");
            if (content.Length == 0) return Bad("content is required.");
            if (await db.ScriptTemplates.AnyAsync(s => s.Name == name))
                return Conflict($"A script template named \"{name}\" already exists.");
            // "Available to": entities [slug|id…] (or the older single `entity`); none = everyone
            var pick = await LibraryScope.ReadAsync(db, body);
            if (pick.Error != null) return pick.Error;
            if (await LibraryScope.Check(db, sc, me, null, pick.Ids) is { } denied) return denied;

            var row = new ScriptTemplate
            {
                Name = name, Content = content,
                EntityId = pick.Ids.Count > 0 ? pick.Ids[0] : null,
                CreatedBy = me.Identity?.Name ?? "",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.ScriptTemplates.Add(row);
            await db.SaveChangesAsync();
            LibraryEntities.Set(db, row, pick.Ids);
            await db.SaveChangesAsync();
            await db.Entry(row).Reference(s => s.Entity).LoadAsync();
            Audit(db, me, ctx, "script_template.create", name, "");
            return Results.Ok(ScriptItem(row, null, LibraryEntities.Load(db, new[] { row })));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapMethods("/api/calls/script-templates/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, AppDbContext db, ScopingService sc, ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (!await CanManageScripts(db, me)) return ScriptsForbidden();
            var row = await db.ScriptTemplates.FirstOrDefaultAsync(s => s.Id == id);
            if (row == null) return NotFound("Script template not found.");
            if (LibraryScope.CheckExisting(db, sc, me,
                    LibraryEntities.Ids(db, LibraryKinds.Script, row.Id, row.EntityId)) is { } locked) return locked;

            if (Has(body, "name"))
            {
                var name = (AsString(body, "name") ?? "").Trim();
                if (name.Length == 0) return Bad("name is required.");
                if (await db.ScriptTemplates.AnyAsync(s => s.Name == name && s.Id != row.Id))
                    return Conflict($"A script template named \"{name}\" already exists.");
                row.Name = name;
            }
            if (Has(body, "content"))
            {
                var content = (AsString(body, "content") ?? "").Trim();
                if (content.Length == 0) return Bad("content is required.");
                row.Content = content;
            }
            // entities [] / entity "" moves the script back to organization-wide (everyone)
            var pick = await LibraryScope.ReadAsync(db, body);
            if (pick.Error != null) return pick.Error;
            if (pick.Given)
            {
                var before = LibraryEntities.Ids(db, LibraryKinds.Script, row.Id, row.EntityId);
                if (await LibraryScope.Check(db, sc, me, before, pick.Ids) is { } denied) return denied;
                LibraryEntities.Set(db, row, pick.Ids);
            }
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await db.Entry(row).Reference(s => s.Entity).LoadAsync();
            Audit(db, me, ctx, "script_template.update", row.Name, "");
            return Results.Ok(ScriptItem(row, null, LibraryEntities.Load(db, new[] { row })));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/calls/script-templates/{id:int}", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (!await CanManageScripts(db, me)) return ScriptsForbidden();
            var row = await db.ScriptTemplates.FirstOrDefaultAsync(s => s.Id == id);
            if (row == null) return NotFound("Script template not found.");
            if (LibraryScope.CheckExisting(db, sc, me,
                    LibraryEntities.Ids(db, LibraryKinds.Script, row.Id, row.EntityId)) is { } locked) return locked;
            var name = row.Name;
            db.ScriptTemplates.Remove(row);
            LibraryEntities.Clear(db, LibraryKinds.Script, id);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "script_template.delete", name, "");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // ======================= contact payload (to_contact) ====================

    /// <summary>
    /// contacts.py <c>_from_model</c> + <c>to_contact</c> in one pass, bulk-loaded
    /// so a 200-row list costs a fixed number of queries. Every key here is
    /// snake_case and matches the dashboard's <c>Contact</c> type.
    /// </summary>
    private static async Task<List<object>> ContactItemsAsync(AppDbContext db, List<Contact> rows)
    {
        var ids = rows.Select(c => c.Id).ToList();
        if (ids.Count == 0) return new List<object>();

        var records = await db.Records.AsNoTracking()
            .Where(r => ids.Contains(r.ContactId)).OrderBy(r => r.Id).ToListAsync();
        var recordIds = records.Select(r => r.Id).ToList();
        var events = recordIds.Count == 0
            ? new List<RecordEvent>()
            : await db.RecordEvents.AsNoTracking()
                .Where(e => recordIds.Contains(e.RecordId)).OrderBy(e => e.Id).ToListAsync();
        var calls = await db.CallRecords.AsNoTracking()
            .Where(cr => cr.ContactId != null && ids.Contains(cr.ContactId.Value)).ToListAsync();
        var links = await db.ContactEntities.AsNoTracking().Include(l => l.Entity)
            .Where(l => ids.Contains(l.ContactId)).ToListAsync();

        var recordsBy = records.GroupBy(r => r.ContactId).ToDictionary(g => g.Key, g => g.ToList());
        var eventsBy = events.ToLookup(e => e.RecordId);
        var callsBy = calls.GroupBy(c => c.ContactId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var slugsBy = links.GroupBy(l => l.ContactId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Entity?.Slug ?? "")
                .Where(s => s.Length > 0).ToList());

        return rows.Select(c =>
        {
            var recs = recordsBy.TryGetValue(c.Id, out var rr) ? rr : new List<Record>();
            var crs = callsBy.TryGetValue(c.Id, out var cc) ? cc : new List<CallRecord>();
            return (object)new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["phone"] = c.Phone,
                ["email"] = c.Email,
                ["company"] = c.CompanyName,
                ["status"] = c.Status.Length > 0 ? c.Status : "pending",
                ["method"] = c.Method,
                ["language"] = c.Language.Length > 0 ? c.Language : "en",
                ["tags"] = c.Tags,
                ["attributes"] = c.Attributes,
                ["notes"] = c.Notes,
                // Entity slugs this contact is linked to — a tenant's contacts are
                // shared across its entities; this powers the topbar switcher.
                ["entities"] = slugsBy.TryGetValue(c.Id, out var sl) ? sl : new List<string>(),
                // The business objects a conversation with this contact may be
                // about. CRITICAL: primaryRecord(c) reads c.records[0].
                ["records"] = recs.Take(HistoryLimit).Select(RecordItem).ToList(),
                ["history"] = History(recs, eventsBy, crs),
            };
        }).ToList();
    }

    private static object RecordItem(Record r) => new
    {
        id = r.Id,
        type = r.Type,
        external_id = r.ExternalId,
        title = r.Title,
        status = r.Status,
        amount = r.Amount is decimal a ? decimal.ToDouble(a) : (double?)null,
        currency = r.Currency,
        due_date = r.DueDate?.ToString("yyyy-MM-dd"),
        data = r.Data,
    };

    /// <summary>contacts.py _history_for — recent touchpoints: AI calls, then record events.</summary>
    private static List<object> History(List<Record> records, ILookup<int, RecordEvent> eventsBy,
        List<CallRecord> calls)
    {
        var items = new List<object>();
        foreach (var call in calls.Take(HistoryLimit))
        {
            var raw = call.EndedAt.Length > 0 ? call.EndedAt : call.StartedAt;
            items.Add(new
            {
                type = "ai-call",
                date = (raw.Length > 16 ? raw[..16] : raw).Replace("T", ", "),
                text = call.Summary.Length > 0 ? call.Summary
                    : $"AI call — outcome: {call.Outcome}.",
            });
        }
        foreach (var r in records.Take(HistoryLimit))
            foreach (var ev in eventsBy[r.Id].Take(HistoryLimit))
                items.Add(new
                {
                    type = ev.Kind,
                    date = ev.At.ToString("dd MMM, HH:mm", CultureInfo.InvariantCulture),
                    text = EventText(r, ev),
                });
        return items.Take(HistoryLimit).ToList();
    }

    private static string EventText(Record r, RecordEvent ev)
    {
        var label = r.ExternalId.Length > 0 ? r.ExternalId
            : (r.Title.Length > 0 ? r.Title : r.Type);
        if (ev.Kind == "payment" && ev.Amount is decimal amount)
            return $"{label}: payment {r.Currency} " +
                   $"{amount.ToString("N2", CultureInfo.InvariantCulture)}.";
        if (ev.Kind == "status")
        {
            var to = DataStr(ev.Data, "to");
            return to.Length > 0 ? $"{label}: status → {to}." : $"{label}: status updated.";
        }
        var note = DataStr(ev.Data, "note");
        return note.Length > 0 ? $"{label}: {note}" : $"{label}: {ev.Kind.ToLowerInvariant()}.";
    }

    private static string DataStr(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var v) || v == null) return "";
        if (v is JsonElement el)
            return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
        return v.ToString() ?? "";
    }

    // ======================= calls ===========================================

    /// <summary>
    /// This contact's call records: FK-linked rows plus the phone-matched ones
    /// mirrored from the voice agent's file store before any link existed.
    /// </summary>
    private static async Task<List<CallRecord>> CallsForAsync(AppDbContext db, Contact contact)
    {
        var phone = contact.Phone;
        return await db.CallRecords.AsNoTracking()
            .Where(r => r.ContactId == contact.Id || (phone != "" && r.Phone == phone))
            .ToListAsync();
    }

    /// <summary>
    /// views._parse_call_transcript — the agent stores a SIP transcript as
    /// newline-joined "role: text" lines; split it back into turns for the UI.
    /// </summary>
    private static List<object> ParseTranscript(string? text)
    {
        var turns = new List<object>();
        foreach (var rawLine in (text ?? "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var i = line.IndexOf(':');
            turns.Add(i >= 0
                ? new { role = line[..i].Trim().ToLowerInvariant(), text = line[(i + 1)..].Trim() }
                : new { role = "user", text = line });
        }
        return turns;
    }

    // ======================= bulk import (contact books) =====================

    /// <summary>The field ids the old one-step modal mapped ({field: header}) — still accepted.</summary>
    private static readonly string[] LegacyFields =
        { "name", "phone", "email", "company_name", "language", "external_id", "notes", "tags" };

    /// <summary>
    /// Parse the upload (.csv / .xlsx / .vcf, or JSON rows — Google Contacts
    /// commits that way), apply the column mapping and options, and run the
    /// importer — a preview writes nothing and returns the same counts the
    /// commit will produce. Form / JSON keys: <c>column_mapping</c>
    /// {header: target} (or the legacy <c>mapping</c> {field: header}),
    /// <c>mode</c> skip | update_empty | overwrite, <c>entity</c> (slug, scope-
    /// checked), <c>tags</c>, <c>discover</c>, <c>preview</c>.
    /// </summary>
    private static async Task<IResult> ImportAsync(HttpContext ctx, AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, ChatBranch.Data.Contacts.ContactImporter importer)
    {
        var preview = IsTruthy(ctx.Request.Query["preview"]);
        ChatBranch.Data.Contacts.ImportTable table;
        Dictionary<string, string>? columnMapping, legacy;
        string? mode, entity;
        List<string> tags;
        bool? discover;

        if (ctx.Request.HasFormContentType)
        {
            if (ctx.Request.ContentLength is long len && len > ChatBranch.Data.Contacts.ContactImporter.MaxBytes + 256 * 1024)
                return TooLarge();
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(); }
            catch { return Bad("Could not read the upload."); }
            if (IsTruthy(form["preview"])) preview = true;
            var upload = form.Files["file"];
            if (upload == null) return Bad("No file uploaded (form field 'file').");
            if (upload.Length > ChatBranch.Data.Contacts.ContactImporter.MaxBytes) return TooLarge();

            var filename = (upload.FileName ?? "").ToLowerInvariant();
            try
            {
                if (filename.EndsWith(".xlsx"))
                {
                    using var ms = new MemoryStream();
                    await upload.CopyToAsync(ms);
                    ms.Position = 0;
                    table = ChatBranch.Data.Contacts.ImportReaders.Xlsx(ms);
                }
                else if (filename.EndsWith(".vcf") || filename.EndsWith(".vcard"))
                {
                    using var ms = new MemoryStream();
                    await upload.CopyToAsync(ms);
                    table = ChatBranch.Data.Contacts.ImportReaders.Vcf(ms.ToArray());
                }
                else if (filename.EndsWith(".csv") || filename.EndsWith(".tsv") || filename.EndsWith(".txt"))
                {
                    using var reader = new StreamReader(upload.OpenReadStream(), Encoding.UTF8);
                    table = ChatBranch.Data.Contacts.ImportReaders.Csv(await reader.ReadToEndAsync());
                }
                else if (filename.EndsWith(".xls"))
                    return Bad("Old Excel (.xls) files aren't supported — save the sheet as .xlsx or .csv.");
                else
                    return Bad("Unsupported file type — upload a .csv, .xlsx or .vcf (vCard) file.");
            }
            catch
            {
                return Bad("Could not read the file — is it a valid .csv, .xlsx or .vcf?");
            }
            columnMapping = ParseMapping(form["column_mapping"]);
            legacy = ParseMapping(form["mapping"]);
            mode = form["mode"].ToString();
            entity = form["entity"].ToString();
            tags = ParseTags(form["tags"].ToString());
            discover = form.ContainsKey("discover") ? IsTruthy(form["discover"]) : null;
        }
        else
        {
            // JSON: {rows: [{header: value}], column_mapping?, mapping?, mode?, entity?, tags?, discover?, preview?, source?}
            JsonElement body;
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { return Bad("No file uploaded (form field 'file')."); }
            if (body.ValueKind != JsonValueKind.Object ||
                !body.TryGetProperty("rows", out var rawRows) || rawRows.ValueKind != JsonValueKind.Array)
                return Bad("No file uploaded (form field 'file').");
            if (body.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.True) preview = true;
            var source = AsString(body, "source") is { Length: > 0 } src && src.Length <= 20 ? src : "json";
            table = ChatBranch.Data.Contacts.ImportReaders.Json(rawRows, source);
            columnMapping = StringMap(body, "column_mapping");
            legacy = StringMap(body, "mapping");
            mode = AsString(body, "mode");
            entity = AsString(body, "entity");
            tags = body.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                ? tg.EnumerateArray().Select(t => t.ToString().Trim()).Where(t => t.Length > 0).ToList()
                : ParseTags(AsString(body, "tags"));
            discover = body.TryGetProperty("discover", out var dv) && dv.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? dv.ValueKind == JsonValueKind.True : null;
        }

        if (table.Truncated > 0)
            return Bad($"The file has more than {ChatBranch.Data.Contacts.ContactImporter.MaxRows:N0} rows — " +
                       "split it into smaller files.");
        if (table.Headers.Count == 0 || table.Rows.Count == 0) return Bad("The file has no rows.");

        // the mapping: an explicit {header: target} wins; the legacy {field: header}
        // overrides the suggestion field by field; otherwise the suggestion
        Dictionary<string, string> mapping;
        if (columnMapping != null) mapping = importer.Clean(table.Headers, columnMapping);
        else
        {
            mapping = importer.Suggest(table.Headers);
            if (legacy != null)
                foreach (var (field, header) in legacy)
                {
                    if (!LegacyFields.Contains(field) || string.IsNullOrWhiteSpace(header) || !mapping.ContainsKey(header))
                        continue;
                    foreach (var k in mapping.Where(kv => kv.Value == field).Select(kv => kv.Key).ToList())
                        mapping[k] = "";
                    mapping[header] = field;
                }
        }

        int? entityId = null;
        var allowed = await ContactIdentityEndpoints.AllowedEntityIdsAsync(db, sc, me);
        if (!string.IsNullOrWhiteSpace(entity))
        {
            entityId = await InboxEndpoints.EntityId(db, entity);
            if (entityId == null) return Bad($"Unknown entity \"{entity}\".");
            if (allowed != null && !allowed.Contains(entityId.Value))
                return Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403);
        }

        var opt = new ChatBranch.Data.Contacts.ImportOptions
        {
            Preview = preview,
            Mode = ChatBranch.Data.Contacts.ContactImporter.Modes.Contains((mode ?? "").Trim())
                ? mode!.Trim() : ChatBranch.Data.Contacts.ContactImporter.ModeUpdateEmpty,
            EntityId = entityId,
            AllowedEntityIds = allowed,
            Tags = tags,
            Discover = discover ?? true,
            TenantId = TenantId(me),
            Actor = me.Identity?.Name ?? "import",
        };
        var report = importer.Run(table, mapping, opt);
        return Results.Ok(ImportPayload(importer, table, mapping, opt, report));
    }

    private static IResult TooLarge() =>
        Results.Json(new { detail = "The file is larger than 10 MB — split it into smaller files." }, statusCode: 413);

    /// <summary>
    /// The import response (preview and commit alike): counts, the detected
    /// columns with samples + their mapping, the targets a column can feed, and
    /// the row-numbered errors / warnings (the dashboard offers them as CSV).
    /// </summary>
    internal static Dictionary<string, object?> ImportPayload(ChatBranch.Data.Contacts.ContactImporter importer,
        ChatBranch.Data.Contacts.ImportTable table, Dictionary<string, string> mapping,
        ChatBranch.Data.Contacts.ImportOptions opt, ChatBranch.Data.Contacts.ImportReport report)
    {
        const int maxListed = 5000;
        var samples = ChatBranch.Data.Contacts.ContactImporter.Samples(table);
        // legacy {field: header}: the first column feeding each of the old fields
        var legacy = new Dictionary<string, string>();
        foreach (var (header, target) in mapping)
            if (LegacyFields.Contains(target) && !legacy.ContainsKey(target)) legacy[target] = header;
        return new Dictionary<string, object?>
        {
            ["preview"] = opt.Preview,
            ["source"] = table.Source,
            ["total"] = report.Total,
            ["created"] = report.Created,
            ["updated"] = report.Updated,
            ["unchanged"] = report.Unchanged,
            ["skipped"] = report.Skipped,
            ["merged"] = report.Merged,
            ["duplicates"] = report.Duplicates,
            ["queued_checks"] = report.Queued,
            ["mode"] = opt.Mode,
            ["modes"] = ChatBranch.Data.Contacts.ContactImporter.Modes,
            ["headers"] = table.Headers,
            ["columns"] = table.Headers.Select(h => new Dictionary<string, object?>
            {
                ["header"] = h,
                ["samples"] = samples.TryGetValue(h, out var s) ? s : new List<string>(),
                ["target"] = mapping.TryGetValue(h, out var t) ? t : "",
            }).ToList(),
            ["column_mapping"] = mapping,
            ["mapping"] = legacy,
            ["fields"] = LegacyFields,
            ["targets"] = importer.Targets().Select(t => new { value = t.Value, label = t.Label, group = t.Group, multi = t.Multi }),
            ["errors"] = report.Errors.Take(maxListed).ToList(),
            ["errors_total"] = report.Errors.Count,
            ["warnings"] = report.Warnings.Take(maxListed).ToList(),
            ["warnings_total"] = report.Warnings.Count,
            ["max_rows"] = ChatBranch.Data.Contacts.ContactImporter.MaxRows,
        };
    }

    private static List<string> ParseTags(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return new List<string>();
        if (s.StartsWith('['))
            try
            {
                return JsonSerializer.Deserialize<List<string>>(s)?.Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
                       ?? new List<string>();
            }
            catch { /* fall through to a comma list */ }
        return s.Replace(";", ",").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
    }

    private static Dictionary<string, string>? StringMap(JsonElement body, string key) =>
        body.TryGetProperty(key, out var mp) && mp.ValueKind == JsonValueKind.Object
            ? mp.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
            : null;

    private static Dictionary<string, string>? ParseMapping(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var el = JsonSerializer.Deserialize<JsonElement>(raw);
            if (el.ValueKind != JsonValueKind.Object) return null;
            return el.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        }
        catch { return null; }
    }

    // ======================= script templates ================================

    private static object ScriptItem(ScriptTemplate t, Dictionary<string, string>? variables,
        Dictionary<int, List<EntityRef>> scopes) => new
    {
        id = t.Id,
        name = t.Name,
        content = t.Content,
        created_by = t.CreatedBy,
        entity_id = t.EntityId,
        entity = t.Entity?.Slug,
        entity_name = t.Entity?.Name ?? "",
        // "Available to": every entity it was ticked for (primary first); global = everyone
        entities = LibraryEntities.Payload(scopes, t.Id),
        global = t.EntityId == null,

        updated_at = DateTime.SpecifyKind(t.UpdatedAt == default ? t.CreatedAt : t.UpdatedAt,
            DateTimeKind.Utc),
        // Contact-bound preview: {{name}}/{{record_title}}/… filled from the CRM.
        rendered = variables != null ? FlowEngine.Render(t.Content, variables) : t.Content,
    };

    /// <summary>Script writes follow the permission matrix (script_templates.manage).</summary>
    private static async Task<bool> CanManageScripts(AppDbContext db, ClaimsPrincipal me) =>
        await AdminEndpoints.HasCapabilityAsync(db, me.FindFirstValue(ClaimTypes.Role) ?? "",
            "script_templates.manage");

    private static IResult ScriptsForbidden() =>
        Results.Json(new { detail = "You need the \"Manage script templates\" permission." }, statusCode: 403);

    // ======================= helpers =========================================

    /// <summary>
    /// Tenant-scope a Contact queryset the way <c>tenant_from_request</c> does:
    /// superadmin is unscoped, everyone else sees only their own tenant's rows.
    /// </summary>
    internal static IQueryable<Contact> ScopeContacts(IQueryable<Contact> qs, ClaimsPrincipal me,
        AppDbContext db, ScopingService sc)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        if (role == Roles.SuperAdmin) return qs;
        if (TenantId(me) is not int tid) return qs;
        qs = qs.Where(c => c.TenantId == tid);
        // An org-wide admin manages the whole tenant. Everyone else (company
        // admins, agents, custom roles) sees ONLY the entities assigned to them —
        // plus everything beneath those entities, since scope flows up the tree,
        // not down. A contact belongs there when it is LINKED to one of them or
        // has a conversation there (a chat-made contact not yet linked).
        var assigned = ShellScope.ScopeRoots(db, me);
        if (assigned.Count == 0) return qs;   // org-wide admin / unassigned agent: the tenant view
        return InEntities(qs, sc.EntitySubtreeIds(assigned));
    }

    /// <summary>The contacts list's "Unassigned (no entity)" filter value.</summary>
    internal const string NoEntity = "__none__";

    /// <summary>A plausible email address (one @, a dot in the domain, no spaces).</summary>
    internal static bool ValidEmail(string email) =>
        System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    /// <summary>
    /// The duplicate guard every contact create / edit uses, PER COMPANY
    /// (<see cref="ContactCompanies.FindClash"/>): null = no clash, else a 409.
    /// A clash the caller can see names the contact and carries its id (the
    /// form offers "add this entity to that contact instead"); one they can't
    /// see never reveals who it is.
    /// </summary>
    internal static async Task<IResult?> DuplicateError(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        int? tenantId, string? phone, string? email, IReadOnlyCollection<int> entityIds, int? excludeId = null)
    {
        var clash = ContactCompanies.FindClash(db, sc, tenantId, phone, email, entityIds, excludeId);
        if (clash == null) return null;
        var visible = await ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc).AnyAsync(c => c.Id == clash.ContactId);
        var what = clash.Field == "phone" ? "number" : "email address";
        var message = visible
            ? $"{clash.Name} already uses that {what}."
            : $"That {what} already belongs to a contact of this company.";
        // A NEW contact clashing with one the caller can't see (another branch of
        // their company): an opaque, short-lived token they can turn into an
        // access request (POST /api/contacts/access-requests) — never the id.
        var access = !visible && excludeId == null
            ? HiddenClashAccess(db, sc, me, clash.ContactId, entityIds) : null;
        return Results.Json(new
        {
            detail = message,
            error = new { code = "conflict", message },
            field = clash.Field,
            existing_id = visible ? clash.ContactId : (int?)null,
            existing_name = visible ? clash.Name : null,
            request_token = access?.Token,
            company_name = access?.CompanyName,
            target_entity = access == null ? null : new { id = access.EntityId, name = access.EntityName },
        }, statusCode: 409);
    }

    private sealed record ClashAccess(string Token, int EntityId, string EntityName, string CompanyName);

    /// <summary>The access-request offer for a hidden clash: the requester's entity
    /// (inside the clashing contact's company and the caller's scope) + a token.</summary>
    private static ClashAccess? HiddenClashAccess(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        int contactId, IReadOnlyCollection<int> entityIds)
    {
        try
        {
            var username = me.Identity?.Name ?? "";
            if (username.Length == 0 || entityIds.Count == 0) return null;
            var bound = ShellScope.BoundEntityIds(db, sc, me);
            var linked = db.ContactEntities.AsNoTracking().Where(l => l.ContactId == contactId)
                .Select(l => l.EntityId).ToList();
            var theirs = ContactCompanies.CompaniesOf(sc, linked);
            var target = entityIds.Where(e => bound == null || bound.Contains(e))
                .OrderBy(e => theirs.Contains(ContactCompanies.CompanyOf(sc, e)) ? 0 : 1).FirstOrDefault();
            if (target == 0) return null;
            var company = ContactCompanies.CompanyOf(sc, target);
            var names = db.Entities.AsNoTracking().Where(e => e.Id == target || e.Id == company)
                .ToDictionary(e => e.Id, e => e.Name);
            return new ClashAccess(ContactAccessToken.Issue(sc, contactId, target, username), target,
                names.GetValueOrDefault(target, ""), names.GetValueOrDefault(company, ""));
        }
        catch
        {
            return null;   // the offer is a convenience — the 409 stands without it
        }
    }

    /// <summary>
    /// The entities a new contact is linked to: the requested ones the caller may
    /// use (inside their scope when they are bound to entities); a bound caller
    /// who names none gets their first assigned entity, so the contact they add
    /// stays visible to them.
    /// </summary>
    internal static List<int> CreationEntities(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        IEnumerable<int> requested)
    {
        var ids = requested.Distinct().ToList();
        var roots = ShellScope.ScopeRoots(db, me);
        if (roots.Count == 0) return ids;
        var bound = sc.EntitySubtreeIds(roots);
        ids = ids.Where(bound.Contains).ToList();
        return ids.Count > 0 ? ids : new List<int> { roots[0] };
    }

    /// <summary>Contacts that belong to these entities: linked to one, or with a
    /// conversation in one (the contacts entity filter and the bound scope).</summary>
    internal static IQueryable<Contact> InEntities(IQueryable<Contact> qs, List<int> ids) =>
        qs.Where(c => c.EntityLinks.Any(l => ids.Contains(l.EntityId))
                      || c.Threads.Any(t => t.EntityId != null && ids.Contains(t.EntityId.Value)));

    /// <summary>The entity ids explicitly assigned to this login (crm_profile ↔ entity).</summary>
    internal static List<int> AssignedEntityIds(AppDbContext db, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return new List<int>();
        return db.Profiles.AsNoTracking()
            .Where(p => p.User != null && p.User.Username == username)
            .SelectMany(p => p.Entities.Select(e => e.Id))
            .ToList();
    }

    private static int? TenantId(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;

    /// <summary>
    /// routing._configured: a WhatsApp cloud account needs a token + phone id
    /// (baileys pairs instead), email needs SMTP host+login, others a token.
    /// </summary>
    private static bool AccountConfigured(ChannelAccount a)
    {
        if (a.Channel == Channels.WhatsApp)
            return ChatBranch.Data.Channels.WhatsAppProviders.Configured(a);
        if (a.Channel == Channels.Email)
            return Cfg(a, "host").Length > 0 && Cfg(a, "username").Length > 0 &&
                   Cfg(a, "password").Length > 0;
        return a.AccessToken.Length > 0;
    }

    /// <summary>Attach a registry row's source + WhatsApp verdict to a hub identity.</summary>
    private static void RegistryFields(Dictionary<string, object?> payload, ContactIdentity? r)
    {
        payload["registered"] = r != null;
        payload["source"] = r?.Source;
        payload["verification"] = r == null || r.Channel != Channels.WhatsApp ? null
            : r.Verification.Length > 0 ? r.Verification : IdentityVerdicts.Unknown;
        payload["verification_detail"] = r?.VerificationDetail ?? "";
        payload["checked_at"] = r?.CheckedAt is DateTime at ? Iso(at) : null;
    }

    /// <summary>A registered identity with no conversation yet, in the hub's identity shape.</summary>
    private static Dictionary<string, object?> RegistryIdentity(ContactIdentity r)
    {
        var payload = new Dictionary<string, object?>
        {
            ["key"] = r.Address,
            ["address"] = r.Address,
            ["name"] = r.Address,
            ["account_id"] = null,
            ["account_name"] = null,
            ["account_provider"] = null,
            ["account_health"] = Health.Unknown,
            ["last_active"] = null,
            ["message_count"] = 0,
            ["inbound_count"] = 0,
            ["outbound_count"] = 0,
            ["last_direction"] = null,
            ["opted_out"] = false,
            ["window_open"] = null,
        };
        RegistryFields(payload, r);
        return payload;
    }

    private static string Cfg(ChannelAccount a, string key) =>
        a.Config.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    /// <summary>store.normalize_wa_id — digits only (a wa_id is never punctuated).</summary>
    private static string Digits(string? raw) =>
        new string((raw ?? "").Where(char.IsDigit).ToArray());

    private static string? Iso(DateTime? dt) =>
        dt is DateTime d ? d.ToString("yyyy-MM-ddTHH:mm:ss.ffffff") : null;

    private static string? NullIfEmpty(string s) => s.Length > 0 ? s : null;

    private static bool IsTruthy(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes";

    private static bool Has(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out _);

    private static string? AsString(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => el.ToString(),
        };
    }

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);
    private static IResult NotFound(string detail) => Results.Json(new { detail }, statusCode: 404);
    private static IResult Conflict(string detail) => Results.Json(new { detail }, statusCode: 409);

    private static void Audit(AppDbContext db, ClaimsPrincipal me, HttpContext ctx,
        string action, string target, string detail)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system",
                Action = action,
                Target = target,
                Detail = detail,
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* an audit failure must never fail the operation itself */ }
    }
}
