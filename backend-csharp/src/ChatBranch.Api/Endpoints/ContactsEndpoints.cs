using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
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
    public record ContactWrite(string? Name, string? Phone, string? Email, string? Language,
        string? CompanyName, List<string>? Tags,
        Dictionary<string, object?>? Attributes = null);
    public record IdentityWrite(string Channel, string Address);

    /// <summary>contacts.py _HISTORY_LIMIT — recent touchpoints, not a full feed.</summary>
    private const int HistoryLimit = 8;

    /// <summary>Channel display order for the hub (voice is appended last).</summary>
    private static readonly string[] HubChatChannels =
        { Channels.WhatsApp, Channels.Email, Channels.Facebook, Channels.Instagram, Channels.Telegram };

    /// <summary>Accounts in these states are skipped by routing — not usable for outreach.</summary>
    private static readonly string[] DownHealth = { Health.Disconnected, Health.Banned };

    public static void MapContactsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- contact list / detail --------------------------------------------
        app.MapGet("/api/contacts", async (string? q, int? limit, string? entity,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var query = ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc);
            if (!string.IsNullOrWhiteSpace(entity) && entity is not ("all" or "__all__"))
            {
                // Narrow to the picked outlet AND everything under it: a contact
                // on a child branch is visible to its parent, never the reverse.
                var picked = await db.Entities.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Slug == entity);
                var ids = picked == null ? new List<int>() : sc.EntitySubtreeIds(picked.Id);
                query = query.Where(c => c.EntityLinks.Any(l => ids.Contains(l.EntityId)));
            }
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(c => c.Name.Contains(q) || c.Phone.Contains(q) || c.Email.Contains(q));

            var rows = await query.OrderByDescending(c => c.UpdatedAt).Take(limit ?? 200).ToListAsync();
            return Results.Ok(new { contacts = await ContactItemsAsync(db, rows) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/contacts", async (ContactWrite b, AppDbContext db, ClaimsPrincipal me) =>
        {
            // create (unlike PATCH) genuinely needs an identity
            var name = (b.Name ?? "").Trim();
            var phone = (b.Phone ?? "").Trim();
            if (name.Length == 0 || phone.Length == 0)
                return Results.Json(new { detail = "name and phone are required." }, statusCode: 400);
            var row = new Contact
            {
                Name = name, Phone = phone, Email = b.Email ?? "", Language = b.Language ?? "en",
                CompanyName = b.CompanyName ?? "", Tags = b.Tags ?? new(),
                Attributes = b.Attributes ?? new(),
                TenantId = TenantId(me),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Contacts.Add(row);
            await db.SaveChangesAsync();
            return Results.Ok((await ContactItemsAsync(db, new List<Contact> { row })).First());
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapGet("/api/contacts/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var c = await ScopeContacts(db.Contacts.AsNoTracking(), me, db)
                .FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound("Contact not found.");
            return Results.Ok((await ContactItemsAsync(db, new List<Contact> { c })).First());
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapMethods("/api/contacts/{id:int}", new[] { "PATCH" },
            async (int id, ContactWrite b, AppDbContext db, ClaimsPrincipal me) =>
        {
            var c = await ScopeContacts(db.Contacts, me, db).FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return NotFound("Contact not found.");
            if (b.Name != null) c.Name = b.Name;
            if (b.Phone != null) c.Phone = b.Phone;
            if (b.Email != null) c.Email = b.Email;
            if (b.Language != null) c.Language = b.Language;
            if (b.CompanyName != null) c.CompanyName = b.CompanyName;
            if (b.Tags != null) c.Tags = b.Tags;
            if (b.Attributes != null)
            {
                // Merge (not replace) so fields this form doesn't know about survive.
                foreach (var kv in b.Attributes) c.Attributes[kv.Key] = kv.Value;
            }
            c.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok((await ContactItemsAsync(db, new List<Contact> { c })).First());
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- unified channel hub ----------------------------------------------
        // One aggregated view of every channel identity this contact is reachable
        // on: threads linked by the Thread.contact FK PLUS the digits-endswith
        // phone fallback for WhatsApp threads created before linking, each
        // identity's carrying account (+ its health), last activity and history
        // counts, with a voice section built from CallRecord.
        app.MapGet("/api/contacts/{id:int}/channels", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var contact = await ScopeContacts(db.Contacts.AsNoTracking(), me, db)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound("Contact not found.");

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
                if (ts.Count == 0 && !(ch == Channels.Email && contact.Email.Length > 0)) continue;

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
                    return (payload, usable);
                })
                .OrderByDescending(p => (string?)p.payload["last_active"] ?? "")
                .ToList();

                var identities = pairs.Select(p => p.payload).ToList();

                // "connected" is deliberately simple, per channel.
                var connected = ch switch
                {
                    Channels.WhatsApp => pairs.Any(p => p.usable),
                    Channels.Email => ts.Count > 0 && emailConfigured,
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
            });

            return Results.Ok(new
            {
                contact_id = contact.Id,
                name = contact.Name,
                phone = contact.Phone,
                email = contact.Email,
                channels,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Register a channel identity by hand (the counterpart to linking from an
        // existing chat): creates or claims the thread and links it, so a number
        // the operator knows about is reachable without waiting for an inbound.
        app.MapPost("/api/contacts/{id:int}/channels", async (int id, IdentityWrite b,
            AppDbContext db, ThreadStore store, ScopingService sc) =>
        {
            var contact = await db.Contacts.FindAsync(id);
            if (contact == null) return NotFound("Contact not found.");
            var thread = store.GetThread(b.Channel, b.Address);
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
            return Results.Json(new
            {
                ok = true, channel = b.Channel, key = thread.Key, contact_id = id,
            }, statusCode: 201);
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- SIP call history ---------------------------------------------------
        // Read-only: the transcript + outcome per AI voice call, rendered inline in
        // the /chats message stream and on the contact detail page. Kept cheap so
        // it stays pollable (no LiveKit/agent plumbing on this path).
        app.MapGet("/api/contacts/{id:int}/calls", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            var contact = await ScopeContacts(db.Contacts.AsNoTracking(), me, db)
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
                    // CallRecord carries no recording path (the recorders write
                    // WAVs next to the WhatsApp call transcripts, not here).
                    recording = (string?)null,
                    transcript = ParseTranscript(r.Transcript),
                })
                .OrderByDescending(x => x.ended_at.Length > 0 ? x.ended_at : x.started_at)
                .ToList();
            return Results.Ok(new { calls });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- bulk import --------------------------------------------------------
        // Parse + upsert; `?preview=1` computes the SAME plan without writing, so
        // the modal can show created/updated/skipped and let the operator correct
        // the column mapping before committing.
        // NB: the multipart body is read off HttpContext rather than bound as an
        // IFormFile parameter — parameter binding is what would drag in the
        // antiforgery filter, and this API authenticates by bearer token.
        app.MapPost("/api/contacts/import", async (HttpContext ctx, AppDbContext db,
            ClaimsPrincipal me) => await ImportAsync(ctx, db, me))
            .RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- script templates --------------------------------------------------
        app.MapGet("/api/calls/script-templates", async (string? entity, int? contact_id,
            AppDbContext db, ScopingService sc, FlowEngine flows) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var rows = await sc.ScopeInherited(db.ScriptTemplates.AsNoTracking(), entityId)
                .OrderBy(s => s.Name).ToListAsync();

            // ?contact_id=<pk> personalizes `rendered` with that contact's CRM
            // variables — the same catalog canned responses and the flow builder
            // render from. A failure there must not break the template list.
            Dictionary<string, string>? variables = null;
            if (contact_id is int cid)
            {
                var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid);
                if (contact != null)
                    try { variables = flows.VariablesFor(contact); } catch { variables = null; }
            }
            // Frontend (ScriptTemplatesSettings) reads `templates` — a `scripts`
            // key left it undefined and crashed on `sorted.length`.
            return Results.Ok(new { templates = rows.Select(t => ScriptItem(t, variables)) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/calls/script-templates", async (JsonElement body, AppDbContext db,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            var name = (AsString(body, "name") ?? "").Trim();
            var content = (AsString(body, "content") ?? "").Trim();
            if (name.Length == 0) return Bad("name is required.");
            if (content.Length == 0) return Bad("content is required.");
            if (await db.ScriptTemplates.AnyAsync(s => s.Name == name))
                return Conflict($"A script template named \"{name}\" already exists.");

            var row = new ScriptTemplate
            {
                Name = name, Content = content,
                EntityId = await InboxEndpoints.EntityId(db, AsString(body, "entity")),
                CreatedBy = me.Identity?.Name ?? "",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.ScriptTemplates.Add(row);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "script_template.create", name, "");
            return Results.Ok(ScriptItem(row, null));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapMethods("/api/calls/script-templates/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, AppDbContext db, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var row = await db.ScriptTemplates.FirstOrDefaultAsync(s => s.Id == id);
            if (row == null) return NotFound("Script template not found.");

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
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "script_template.update", row.Name, "");
            return Results.Ok(ScriptItem(row, null));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapDelete("/api/calls/script-templates/{id:int}", async (int id, AppDbContext db,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            var row = await db.ScriptTemplates.FirstOrDefaultAsync(s => s.Id == id);
            if (row == null) return NotFound("Script template not found.");
            var name = row.Name;
            db.ScriptTemplates.Remove(row);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "script_template.delete", name, "");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
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

    // ======================= bulk import (crm/importer.py) ===================

    /// <summary>field → accepted header names (lower-cased, EN + a little BM).</summary>
    private static readonly (string Field, string[] Aliases)[] FieldAliases =
    {
        ("name", new[] { "name", "full name", "contact name", "student name", "customer name", "nama" }),
        ("phone", new[] { "phone", "mobile", "phone number", "mobile number", "contact number",
                          "whatsapp", "hp", "no telefon", "telefon" }),
        ("email", new[] { "email", "e-mail", "email address", "emel" }),
        ("company_name", new[] { "company", "company name", "organization", "organisation", "syarikat" }),
        ("language", new[] { "language", "lang", "bahasa" }),
        ("external_id", new[] { "external id", "id", "member id", "student id", "customer id",
                                "ref", "reference" }),
        ("notes", new[] { "notes", "note", "remarks", "catatan" }),
        ("tags", new[] { "tags", "tag", "labels" }),
    };

    private static async Task<IResult> ImportAsync(HttpContext ctx, AppDbContext db, ClaimsPrincipal me)
    {
        var preview = IsTruthy(ctx.Request.Query["preview"]);
        List<string> headers;
        List<Dictionary<string, string>> rows;
        Dictionary<string, string>? overrides = null;

        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync();
            if (IsTruthy(form["preview"])) preview = true;
            var upload = form.Files["file"];
            if (upload == null) return Bad("No file uploaded (form field 'file').");

            var filename = (upload.FileName ?? "").ToLowerInvariant();
            if (filename.EndsWith(".xlsx"))
                // Django parses .xlsx with openpyxl; this API has no spreadsheet
                // reader, so say so instead of failing with a parse error.
                return Bad("Excel (.xlsx) import isn't supported by this API yet — " +
                           "save the sheet as .csv and upload that.");
            if (!filename.EndsWith(".csv"))
                return Bad("Unsupported file type — upload a .xlsx or .csv file.");

            string text;
            try
            {
                using var reader = new StreamReader(upload.OpenReadStream(), Encoding.UTF8);
                text = await reader.ReadToEndAsync();
            }
            catch
            {
                return Bad("Could not read the file — is it a valid .csv?");
            }
            (headers, rows) = ParseCsv(text);
            overrides = ParseMapping(form["mapping"]);
        }
        else
        {
            // JSON fallback: {rows: [{header: value, …}], mapping?: {}, preview?: bool}
            JsonElement body;
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { return Bad("No file uploaded (form field 'file')."); }

            if (body.ValueKind == JsonValueKind.Object &&
                body.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.True)
                preview = true;
            if (body.ValueKind != JsonValueKind.Object ||
                !body.TryGetProperty("rows", out var rawRows) || rawRows.ValueKind != JsonValueKind.Array)
                return Bad("No file uploaded (form field 'file').");

            rows = new List<Dictionary<string, string>>();
            headers = new List<string>();
            foreach (var el in rawRows.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var row = new Dictionary<string, string>();
                foreach (var prop in el.EnumerateObject())
                {
                    if (!headers.Contains(prop.Name)) headers.Add(prop.Name);
                    row[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? "" : prop.Value.ToString();
                }
                if (row.Values.Any(v => v.Trim().Length > 0)) rows.Add(row);
            }
            if (body.TryGetProperty("mapping", out var mp) && mp.ValueKind == JsonValueKind.Object)
                overrides = mp.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        }

        if (headers.Count == 0) return Bad("The file has no rows.");

        var mapping = DetectMapping(headers);
        if (overrides != null)
            foreach (var (field, header) in overrides)
                if (FieldAliases.Any(f => f.Field == field) && !string.IsNullOrWhiteSpace(header))
                    mapping[field] = header;

        var summary = await RunImportAsync(db, rows, mapping, TenantId(me), preview);
        return Results.Ok(new Dictionary<string, object?>
        {
            ["preview"] = preview,
            ["headers"] = headers,
            ["mapping"] = mapping,
            ["fields"] = FieldAliases.Select(f => f.Field).ToList(),
            ["total"] = summary.Total,
            ["created"] = summary.Created,
            ["updated"] = summary.Updated,
            ["skipped"] = summary.Skipped,
            ["errors"] = summary.Errors,
        });
    }

    private record ImportSummary(int Total, int Created, int Updated, int Skipped, List<object> Errors);

    /// <summary>
    /// importer.run_import — upsert with the SAME match order as the sync
    /// normalizer: (tenant, external_id) → (tenant, phone). dry_run computes the
    /// identical plan without writing, so the preview and the commit agree.
    /// </summary>
    private static async Task<ImportSummary> RunImportAsync(AppDbContext db,
        List<Dictionary<string, string>> rows, Dictionary<string, string> mapping,
        int? tenantId, bool dryRun)
    {
        int created = 0, updated = 0, skipped = 0;
        var errors = new List<object>();

        string Val(Dictionary<string, string> row, string field) =>
            mapping.TryGetValue(field, out var header) && row.TryGetValue(header, out var v)
                ? (v ?? "").Trim() : "";

        // Match INSIDE the importer's own tenant. Built as a queryable rather
        // than `c.TenantId == tenantId` so a tenant-less import compares
        // IS NULL — a parameterized null would match no row and re-create
        // every contact on every run.
        IQueryable<Contact> Scope() => tenantId is int tid
            ? db.Contacts.Where(c => c.TenantId == tid)
            : db.Contacts.Where(c => c.TenantId == null);

        var idx = 1;
        foreach (var row in rows)
        {
            idx++; // +2 overall: row 1 is the header
            var name = Val(row, "name");
            var phone = Val(row, "phone");
            if (name.Length == 0 || phone.Length == 0)
            {
                skipped++;
                errors.Add(new { row = idx, reason = "missing name or phone" });
                continue;
            }

            var ext = Val(row, "external_id");
            Contact? existing = null;
            if (ext.Length > 0)
                existing = await Scope().FirstOrDefaultAsync(c => c.ExternalId == ext);
            existing ??= await Scope().FirstOrDefaultAsync(c => c.Phone == phone);

            if (dryRun)
            {
                if (existing != null) updated++; else created++;
                continue;
            }

            var email = Val(row, "email");
            var company = Val(row, "company_name");
            var language = Val(row, "language");
            if (language.Length == 0) language = "en";
            var notes = Val(row, "notes");
            var tags = SplitTags(Val(row, "tags"));
            Contact? written = null;
            try
            {
                if (existing != null)
                {
                    existing.Name = name;
                    if (email.Length > 0) existing.Email = email;
                    if (company.Length > 0) existing.CompanyName = company;
                    if (language.Length > 0) existing.Language = language;
                    if (ext.Length > 0) existing.ExternalId = ext;
                    if (notes.Length > 0) existing.Notes = notes;
                    if (tags.Count > 0) existing.Tags = tags;
                    existing.UpdatedAt = DateTime.UtcNow;
                    written = existing;
                    await db.SaveChangesAsync();
                    updated++;
                }
                else
                {
                    written = new Contact
                    {
                        TenantId = tenantId, Name = name, Phone = phone, Email = email,
                        CompanyName = company, Language = language, ExternalId = ext,
                        Notes = notes, Tags = tags, Status = "active",
                        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                    };
                    db.Contacts.Add(written);
                    await db.SaveChangesAsync();
                    created++;
                }
            }
            catch (Exception e)
            {
                skipped++;
                errors.Add(new { row = idx, reason = e.GetType().Name });
                // Detach the row that blew up (a duplicate phone is the common
                // case) — leaving it tracked makes EVERY later SaveChanges
                // re-throw the same error and the whole import fail.
                if (written != null) db.Entry(written).State = EntityState.Detached;
            }
        }

        return new ImportSummary(rows.Count, created, updated, skipped, errors.Take(50).ToList());
    }

    private static Dictionary<string, string> DetectMapping(List<string> headers)
    {
        var lower = new Dictionary<string, string>();
        foreach (var h in headers)
        {
            var key = (h ?? "").Trim().ToLowerInvariant();
            if (key.Length > 0 && !lower.ContainsKey(key)) lower[key] = h!;
        }
        var mapping = new Dictionary<string, string>();
        foreach (var (field, aliases) in FieldAliases)
            foreach (var alias in aliases)
                if (lower.TryGetValue(alias, out var header)) { mapping[field] = header; break; }
        return mapping;
    }

    private static List<string> SplitTags(string raw) =>
        raw.Replace(";", ",").Split(',')
            .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

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

    /// <summary>Minimal RFC4180 CSV reader — quoted cells, doubled quotes, CRLF.</summary>
    private static (List<string> Headers, List<Dictionary<string, string>> Rows) ParseCsv(string text)
    {
        text = text.TrimStart('\uFEFF'); // BOM, if the reader left one behind
        var table = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    table.Add(row); row = new List<string>();
                    break;
                default: cell.Append(ch); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); table.Add(row); }
        if (table.Count == 0) return (new List<string>(), new List<Dictionary<string, string>>());

        var headers = table[0].Select(h => (h ?? "").Trim()).ToList();
        var rows = new List<Dictionary<string, string>>();
        foreach (var raw in table.Skip(1))
        {
            var cells = raw.Select(c => (c ?? "").Trim()).ToList();
            if (!cells.Any(c => c.Length > 0)) continue; // blank line
            var dict = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count; i++)
                if (headers[i].Length > 0) dict[headers[i]] = i < cells.Count ? cells[i] : "";
            rows.Add(dict);
        }
        return (headers, rows);
    }

    // ======================= script templates ================================

    private static object ScriptItem(ScriptTemplate t, Dictionary<string, string>? variables) => new
    {
        id = t.Id,
        name = t.Name,
        content = t.Content,
        created_by = t.CreatedBy,
        entity_id = t.EntityId,
        // Contact-bound preview: {{name}}/{{record_title}}/… filled from the CRM.
        rendered = variables != null ? FlowEngine.Render(t.Content, variables) : t.Content,
    };

    // ======================= helpers =========================================

    /// <summary>
    /// Tenant-scope a Contact queryset the way <c>tenant_from_request</c> does:
    /// superadmin is unscoped, everyone else sees only their own tenant's rows.
    /// </summary>
    private static IQueryable<Contact> ScopeContacts(IQueryable<Contact> qs, ClaimsPrincipal me,
        AppDbContext db, ScopingService? sc = null)
    {
        // Provider login ("their own world"): only the customers bound to them.
        // Applied FIRST and unconditionally — a provider who is also superadmin
        // is still a provider here.
        qs = ProviderScope.Contacts(qs, me, db);
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        if (role == Roles.SuperAdmin) return qs;
        if (TenantId(me) is not int tid) return qs;
        qs = qs.Where(c => c.TenantId == tid);
        // An admin manages the whole tenant. Everyone else (agents, custom roles)
        // sees ONLY the entities assigned to them — plus everything beneath those
        // entities, since scope flows up the tree, not down.
        if (role == Roles.Admin || sc == null) return qs;
        var assigned = AssignedEntityIds(db, me.Identity?.Name);
        if (assigned.Count == 0) return qs;   // unassigned agent keeps the tenant view
        var ids = sc.EntitySubtreeIds(assigned);
        return qs.Where(c => c.EntityLinks.Any(l => ids.Contains(l.EntityId)));
    }

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
            return a.Provider == Providers.Baileys ||
                   (a.AccessToken.Length > 0 && a.ExternalId.Length > 0);
        if (a.Channel == Channels.Email)
            return Cfg(a, "host").Length > 0 && Cfg(a, "username").Length > 0 &&
                   Cfg(a, "password").Length > 0;
        return a.AccessToken.Length > 0;
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
