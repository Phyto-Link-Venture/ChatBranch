using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The email + social side of the unified inbox — everything under
/// <c>/api/messaging/</c> that the /chats page, the dashboard tiles and the
/// contact page call. Ported endpoint for endpoint from
/// <c>backend/messaging/views.py</c> (+ <c>store.py</c> / <c>timeline.py</c>,
/// whose dict shapes ARE the wire contract).
///
/// Three rules hold everywhere here:
///   * WIRE FORMAT IS snake_case — these payloads are built inline, NOT through
///     the camelCase <c>Dtos</c> mappers, because the Next.js frontend consumes
///     Django's exact keys (wa_id/key, ai_enabled, last_ts, contact_id…).
///   * SANDBOX-SAFE — with zero credentials every endpoint still answers.
///     Outbound messages store locally with status "sandbox" (no SMTP/Meta/
///     Telegram transport is ported yet), exactly as the Django app behaves
///     with an unconfigured channel. Nothing here may 500.
///   * The messaging channels SHARE crm.Thread/crm.Message with WhatsApp — the
///     <c>channel</c> column is the only separator, so the thread list excludes
///     "whatsapp" while identity linking and the merged contact view are
///     deliberately channel-agnostic.
///
/// Auth mirrors Django: every endpoint needs a bearer role except
/// <c>status</c> (and the webhooks, which live in WebhooksEndpoints).
/// </summary>
public static class MessagingEndpoints
{
    // Django messaging/views.VALID_CHANNELS = ("email",) + msg_config.SOCIAL_CHANNELS
    private static readonly string[] ValidChannels =
        { Channels.Email, Channels.Facebook, Channels.Instagram, Channels.Telegram };

    private static readonly string[] ValidStatuses =
        { ThreadStatus.Open, ThreadStatus.Pending, ThreadStatus.Snoozed, ThreadStatus.Resolved };

    public static void MapMessagingEndpoints(this IEndpointRouteBuilder app)
    {
        MapStatusAndPeople(app);
        MapContacts(app);
        MapThreads(app);
        MapThreadActions(app);
        MapEngagementAndTimeline(app);
    }

    // =========================================================================
    // helpers
    // =========================================================================

    /// <summary>
    /// ISO-8601 with an explicit Z. Every timestamp we store is written as
    /// <c>DateTime.UtcNow</c>, but SQLite hands them back with Kind=Unspecified
    /// — formatting those raw would make the browser read UTC as local time and
    /// shift every "5m ago" in the inbox.
    /// </summary>
    private static string Iso(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("o");

    private static string Digits(string? raw) => Regex.Replace(raw ?? "", @"\D", "");

    private static string Env(string name) =>
        (Environment.GetEnvironmentVariable(name) ?? "").Trim();

    /// <summary>Setting row (dashboard) wins; else the .env var; else "".</summary>
    private static string StrOverEnv(ScopingService sc, string key, string env)
    {
        var v = sc.SettingValue(key);
        return !string.IsNullOrWhiteSpace(v) ? v.Trim() : Env(env);
    }

    // ---- request-body reading (same idiom as VoiceMessagingEndpoints) -------

    private static bool HasKey(Dictionary<string, JsonElement>? body, string key) =>
        body != null && body.ContainsKey(key);

    private static string? Str(Dictionary<string, JsonElement>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool BoolOf(Dictionary<string, JsonElement>? body, string key, bool dflt)
    {
        if (body == null || !body.TryGetValue(key, out var el)) return dflt;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => el.GetString() is "true" or "True" or "1",
            JsonValueKind.Number => el.TryGetInt32(out var n) && n != 0,
            _ => dflt,
        };
    }

    /// <summary>The int at <paramref name="key"/>, or null when absent/JSON null/not a number.</summary>
    private static int? IntOrNull(Dictionary<string, JsonElement>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    private static void Audit(AppDbContext db, ClaimsPrincipal me, HttpContext ctx,
        string action, string target, string detail = "")
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system",
                Action = action,
                Target = target,
                Detail = detail.Length > 200 ? detail[..200] : detail,
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* an audit failure must never fail the operation itself */ }
    }

    // ---- payload builders (store.py `_serialize*`, key for key) -------------

    /// <summary>Media is served auth-gated by the channel's OWN app.</summary>
    private static string MediaUrl(string channel, int messagePk) =>
        $"/api/{(channel == Channels.WhatsApp ? "whatsapp" : "messaging")}/media/{messagePk}";

    /// <summary>store._serialize_message. <paramref name="accountName"/> powers "via &lt;account&gt;".</summary>
    private static Dictionary<string, object?> MessagePayload(Message m, string channel,
        string? accountName)
    {
        var outv = new Dictionary<string, object?>
        {
            ["id"] = string.IsNullOrEmpty(m.ExternalId) ? $"db-{m.Id}" : m.ExternalId,
            ["dir"] = m.Direction,
            ["text"] = m.Text,
            ["ts"] = Iso(m.CreatedAt),
            ["status"] = m.Status,
            ["source"] = m.Source,
            ["private"] = m.Private,
        };
        if (m.AccountId is int accountId)
        {
            outv["account_id"] = accountId.ToString();
            outv["account_name"] = accountName ?? "";
        }
        if (!string.IsNullOrEmpty(m.MediaPath))
            outv["media"] = new Dictionary<string, object?>
            {
                ["type"] = string.IsNullOrEmpty(m.MediaType) ? "document" : m.MediaType,
                ["url"] = MediaUrl(channel, m.Id),
            };
        return outv;
    }

    /// <summary>store._serialize — the full thread the chat pane renders.</summary>
    private static Dictionary<string, object?> ThreadPayload(Thread t, List<Message> messages) =>
        new()
        {
            ["key"] = t.Key,
            ["channel"] = t.Channel,
            ["address"] = string.IsNullOrEmpty(t.Address) ? t.Key : t.Address,
            ["name"] = string.IsNullOrEmpty(t.Name) ? t.Key : t.Name,
            ["ai_enabled"] = t.AiEnabled,
            ["status"] = t.Status,
            ["assignee"] = t.Assignee,
            ["labels"] = t.Labels ?? new List<string>(),
            ["contact_id"] = t.ContactId,
            ["contact_name"] = t.ContactId != null ? t.Contact?.Name ?? "" : "",
            ["messages"] = messages
                .Select(m => MessagePayload(m, t.Channel, m.Account?.Name ?? t.Account?.Name))
                .ToList(),
            ["created"] = Iso(t.CreatedAt),
            ["updated"] = Iso(t.UpdatedAt),
        };

    /// <summary>Load a thread's messages oldest→newest (crm.Message Meta.ordering).</summary>
    private static List<Message> MessagesOf(AppDbContext db, int threadId) =>
        db.Messages.AsNoTracking().Include(m => m.Account)
            .Where(m => m.ThreadId == threadId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToList();

    /// <summary>store._get — the messaging store never normalizes beyond a trim.</summary>
    private static Thread? GetThread(AppDbContext db, string channel, string key) =>
        db.Threads.Include(t => t.Contact).Include(t => t.Account)
            .FirstOrDefault(t => t.Channel == channel && t.Key == (key ?? "").Trim());

    // =========================================================================
    // 1. status + agents
    // =========================================================================

    private static void MapStatusAndPeople(IEndpointRouteBuilder app)
    {
        // GET /api/messaging/status — per-channel gating for the composer.
        // OPEN, like Django's messaging/views.status: the chats page reads it
        // beside whatsapp/status and it exposes no secrets, only booleans.
        app.MapGet("/api/messaging/status", async (AppDbContext db, ScopingService sc) =>
        {
            try
            {
                // Global SMTP fallback (msg_config.email_configured) OR any
                // configured per-mailbox ChannelAccount.
                var smtpHost = StrOverEnv(sc, "smtp_host", "SMTP_HOST");
                var smtpFrom = StrOverEnv(sc, "smtp_from", "SMTP_FROM");
                var smtpUser = StrOverEnv(sc, "smtp_username", "SMTP_USERNAME");
                var smtpPass = StrOverEnv(sc, "smtp_password", "SMTP_PASSWORD");
                var imapHost = StrOverEnv(sc, "imap_host", "IMAP_HOST");

                var emailAccounts = await db.ChannelAccounts.AsNoTracking()
                    .Where(a => a.Channel == Channels.Email).ToListAsync();
                var anyMailbox = emailAccounts.Any(a => EmailAccountConfigured(a.Config));

                var socialTokens = await db.ChannelAccounts.AsNoTracking()
                    .Where(a => a.AccessToken != "")
                    .Select(a => a.Channel).ToListAsync();

                return Results.Ok(new Dictionary<string, object?>
                {
                    ["email_configured"] = (smtpHost.Length > 0 && smtpFrom.Length > 0) || anyMailbox,
                    ["imap_configured"] = imapHost.Length > 0 && smtpUser.Length > 0 && smtpPass.Length > 0,
                    ["facebook_configured"] = socialTokens.Contains(Channels.Facebook),
                    ["instagram_configured"] = socialTokens.Contains(Channels.Instagram),
                    ["telegram_configured"] = socialTokens.Contains(Channels.Telegram),
                    ["ai_available"] = AiAvailable(sc),
                });
            }
            catch
            {
                // Sandbox floor: the composer must still render, so answer
                // "nothing configured" rather than failing the whole page.
                return Results.Ok(new Dictionary<string, object?>
                {
                    ["email_configured"] = false,
                    ["imap_configured"] = false,
                    ["facebook_configured"] = false,
                    ["instagram_configured"] = false,
                    ["telegram_configured"] = false,
                    ["ai_available"] = false,
                });
            }
        }).AllowAnonymous();

        // GET /api/messaging/agents — the assignee dropdown. Any authenticated
        // caller: the list carries no secrets, just names (the /api/auth/users
        // admin endpoint is the gated one).
        app.MapGet("/api/messaging/agents", async (AppDbContext db) =>
        {
            var rows = await db.Profiles.AsNoTracking().Include(p => p.User)
                .Where(p => p.User != null && p.User.IsActive)
                .OrderBy(p => p.User!.Username)
                .Select(p => new { p.User!.Username, p.Role })
                .ToListAsync();
            return Results.Ok(new
            {
                agents = rows.Select(r => new Dictionary<string, object?>
                {
                    ["username"] = r.Username,
                    // AppUser carries no first/last name — Django falls back to
                    // the username for exactly that case.
                    ["name"] = r.Username,
                    ["role"] = r.Role,
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    /// <summary>msg_config.email_account_configured — SMTP host + login present.</summary>
    private static bool EmailAccountConfigured(Dictionary<string, object?>? cfg)
    {
        if (cfg == null) return false;
        string V(string k) => cfg.TryGetValue(k, out var v) && v != null
            ? (v is JsonElement el
                ? el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString()
                : v.ToString() ?? "")
            : "";
        return V("host").Length > 0 && V("username").Length > 0 && V("password").Length > 0;
    }

    /// <summary>wa_config.ai_available — any chat provider key, Setting row or .env.</summary>
    private static bool AiAvailable(ScopingService sc) =>
        StrOverEnv(sc, "openrouter_api_key", "OPENROUTER_API_KEY").Length > 0 ||
        StrOverEnv(sc, "openai_api_key", "OPENAI_API_KEY").Length > 0 ||
        StrOverEnv(sc, "ilmu_api_key", "ILMU_API_KEY").Length > 0 ||
        StrOverEnv(sc, "google_api_key", "GOOGLE_API_KEY").Length > 0;

    // =========================================================================
    // 2. contacts — link picker, vCard export, merged conversation
    // =========================================================================

    private static void MapContacts(IEndpointRouteBuilder app)
    {
        // GET /api/messaging/contacts — the "Link to a contact" picker.
        app.MapGet("/api/messaging/contacts", async (AppDbContext db) =>
        {
            var rows = await db.Contacts.AsNoTracking().OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, c.Phone, c.Email }).ToListAsync();
            return Results.Ok(new
            {
                contacts = rows.Select(c => new Dictionary<string, object?>
                {
                    ["id"] = c.Id, ["name"] = c.Name, ["phone"] = c.Phone, ["email"] = c.Email,
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // GET /api/messaging/contacts/vcard — every contact as a .vcf download.
        // Address-book contacts are exempt from WhatsApp's cold-contact (463
        // reach-out) restriction, so importing this on a paired Baileys phone is
        // what makes reminders deliver.
        app.MapGet("/api/messaging/contacts/vcard", async (AppDbContext db, HttpContext ctx,
            ClaimsPrincipal me) =>
        {
            var rows = await db.Contacts.AsNoTracking().Where(c => c.Phone != "")
                .OrderBy(c => c.Name).Select(c => new { c.Name, c.Phone, c.Email }).ToListAsync();
            var sb = new StringBuilder();
            foreach (var c in rows)
            {
                sb.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
                sb.Append($"FN:{c.Name}\r\n");
                sb.Append($"N:{c.Name};;;;\r\n");
                sb.Append($"TEL;TYPE=CELL:+{Digits(c.Phone)}\r\n");
                if (!string.IsNullOrEmpty(c.Email)) sb.Append($"EMAIL:{c.Email}\r\n");
                sb.Append("END:VCARD\r\n");
            }
            Audit(db, me, ctx, "contacts.vcard_export", "", $"{rows.Count} contacts");
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"contacts.vcf\"";
            return Results.Text(sb.ToString(), "text/vcard; charset=utf-8");
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // GET /api/messaging/contacts/{id}/conversation — the combined view:
        // every message to/from this contact across EVERY channel (WhatsApp
        // included), oldest first, each annotated with its channel + thread key.
        app.MapGet("/api/messaging/contacts/{id:int}/conversation",
            async (int id, int? limit, AppDbContext db) =>
        {
            var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null)
                return Results.Json(new { detail = "Contact not found." }, statusCode: 404);

            var threads = await db.Threads.AsNoTracking().Where(t => t.ContactId == id)
                .OrderBy(t => t.Channel).ThenBy(t => t.Key).ToListAsync();
            var take = Math.Max(1, Math.Min(limit ?? 200, 1000));
            var threadIds = threads.Select(t => t.Id).ToList();

            // newest `take`, then flipped back into chronological order
            var newest = await db.Messages.AsNoTracking().Include(m => m.Account)
                .Where(m => threadIds.Contains(m.ThreadId))
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Take(take).ToListAsync();
            newest.Reverse();

            var byId = threads.ToDictionary(t => t.Id);
            var messages = new List<Dictionary<string, object?>>();
            foreach (var m in newest)
            {
                if (!byId.TryGetValue(m.ThreadId, out var thread)) continue;
                var row = MessagePayload(m, thread.Channel, m.Account?.Name);
                row["channel"] = thread.Channel;
                row["key"] = thread.Key;
                messages.Add(row);
            }

            return Results.Ok(new Dictionary<string, object?>
            {
                ["contact"] = new Dictionary<string, object?>
                {
                    ["id"] = contact.Id, ["name"] = contact.Name,
                    ["phone"] = contact.Phone, ["email"] = contact.Email,
                },
                ["channels"] = threads.Select(t => new Dictionary<string, object?>
                {
                    ["channel"] = t.Channel, ["key"] = t.Key,
                    ["name"] = string.IsNullOrEmpty(t.Name) ? t.Key : t.Name,
                }).ToList(),
                ["messages"] = messages,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // =========================================================================
    // 3. threads — list, open, detail
    // =========================================================================

    private static void MapThreads(IEndpointRouteBuilder app)
    {
        // GET /api/messaging/threads?entity= — every NON-WhatsApp thread.
        // scope_owned + include_unassigned: an outlet sees its own chats plus
        // the untagged ones somebody still has to answer.
        app.MapGet("/api/messaging/threads", async (string? entity, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            IQueryable<Thread> q = db.Threads.AsNoTracking().Include(t => t.Contact)
                .Where(t => t.Channel != Channels.WhatsApp);
            // Provider login: only conversations with THEIR bound customers.
            q = ProviderScope.Threads(q, me, db);
            var rows = await sc.ScopeOwned(q, entityId, includeUnassigned: true).ToListAsync();

            var ids = rows.Select(t => t.Id).ToList();
            var last = await db.Messages.AsNoTracking()
                .Where(m => ids.Contains(m.ThreadId))
                .Select(m => new { m.ThreadId, m.Id, m.Text, m.Private, m.CreatedAt })
                .ToListAsync();
            var byThread = last.GroupBy(m => m.ThreadId).ToDictionary(
                g => g.Key,
                g => new
                {
                    Count = g.Count(),
                    Last = g.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Last(),
                });

            var threads = rows.Select(t =>
            {
                byThread.TryGetValue(t.Id, out var agg);
                return new Dictionary<string, object?>
                {
                    ["key"] = t.Key,
                    ["channel"] = t.Channel,
                    ["address"] = string.IsNullOrEmpty(t.Address) ? t.Key : t.Address,
                    ["name"] = string.IsNullOrEmpty(t.Name) ? t.Key : t.Name,
                    ["ai_enabled"] = t.AiEnabled,
                    ["status"] = t.Status,
                    ["assignee"] = t.Assignee,
                    ["team_id"] = t.TeamId,
                    ["labels"] = t.Labels ?? new List<string>(),
                    ["contact_id"] = t.ContactId,
                    ["contact_name"] = t.ContactId != null ? t.Contact?.Name ?? "" : "",
                    // a private note previews as "[note] …", never as a reply
                    ["last_message"] = agg == null ? ""
                        : agg.Last.Private ? $"[note] {agg.Last.Text}" : agg.Last.Text,
                    ["last_ts"] = agg == null ? Iso(t.UpdatedAt) : Iso(agg.Last.CreatedAt),
                    ["message_count"] = agg?.Count ?? 0,
                };
            })
            .OrderByDescending(t => (string)(t["last_ts"] ?? ""), StringComparer.Ordinal)
            .ToList();

            return Results.Ok(new { threads });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST /api/messaging/threads {channel, to, name?} — open a conversation.
        app.MapPost("/api/messaging/threads", async (Dictionary<string, JsonElement>? body,
            AppDbContext db, ThreadStore store, ScopingService sc) =>
        {
            var channel = (Str(body, "channel") ?? "").Trim().ToLowerInvariant();
            var to = (Str(body, "to") ?? "").Trim();
            if (!ValidChannels.Contains(channel))
                return Results.Json(new
                {
                    detail = $"channel must be one of {string.Join(", ", ValidChannels)}.",
                }, statusCode: 400);
            if (to.Length == 0 || (channel == Channels.Email && !to.Contains('@')))
                return Results.Json(new { detail = "Valid recipient ('to') required." }, statusCode: 400);

            var existing = GetThread(db, channel, to);
            // store._match_contact: email keys match Contact.email. Social ids
            // (PSID/IGSID/TG chat id) have no CRM counterpart — those threads
            // are linked by hand from the chat's Contact panel.
            var matched = channel == Channels.Email
                ? await db.Contacts.FirstOrDefaultAsync(c => c.Email.ToLower() == to.ToLower())
                : null;

            var thread = store.GetOrCreateThread(channel, to, Str(body, "name"),
                contactId: existing?.ContactId ?? matched?.Id);
            if (existing == null && channel == Channels.Email && thread.AiEnabled)
            {
                thread.AiEnabled = false; // email has no auto-reply
                await db.SaveChangesAsync();
            }
            else if (existing != null && thread.ContactId == null && matched != null)
            {
                thread.ContactId = matched.Id; // backfill a pre-link thread
                thread.Contact = matched;
                // The link decided the outlet — re-derive the denormalized entity.
                sc.SyncThreadEntity(thread, save: false);
                await db.SaveChangesAsync();
            }
            await db.Entry(thread).Reference(t => t.Contact).LoadAsync();
            return Results.Ok(ThreadPayload(thread, MessagesOf(db, thread.Id)));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // GET /api/messaging/threads/{channel}/{key} — the full thread.
        app.MapGet("/api/messaging/threads/{channel}/{key}", (string channel, string key,
            AppDbContext db) =>
        {
            var thread = GetThread(db, channel, key);
            if (thread == null)
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            return Results.Ok(ThreadPayload(thread, MessagesOf(db, thread.Id)));
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // =========================================================================
    // 4. thread actions — send / ai / status / assign / labels / note / link
    // =========================================================================

    private static void MapThreadActions(IEndpointRouteBuilder app)
    {
        // POST …/send {text} — SANDBOX: no SMTP/Meta/Telegram transport is
        // ported yet, so the message is stored locally with status "sandbox"
        // (what Django does for an unconfigured channel). It shows in the
        // thread, in the timeline and in analytics — it is simply not claimed
        // to have left the building.
        app.MapPost("/api/messaging/threads/{channel}/{key}/send",
            (string channel, string key, Dictionary<string, JsonElement>? body,
             AppDbContext db, ThreadStore store, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var text = (Str(body, "text") ?? "").Trim();
            if (text.Length == 0)
                return Results.Json(new { detail = "text is required." }, statusCode: 400);
            var thread = GetThread(db, channel, key);
            if (thread == null)
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);

            var msg = store.AppendMessage(thread, MessageDirection.Out, MessageSource.Agent, text,
                status: "sandbox", accountId: thread.AccountId);
            Audit(db, me, ctx, "message.send", $"{channel}:{key}", text);
            return Results.Ok(new { message = MessagePayload(msg, thread.Channel, thread.Account?.Name) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST …/ai {enabled} — per-thread AI auto-reply tick (social channels;
        // email threads ship with it off).
        app.MapPost("/api/messaging/threads/{channel}/{key}/ai",
            async (string channel, string key, Dictionary<string, JsonElement>? body,
                   AppDbContext db) =>
        {
            var thread = GetThread(db, channel, key);
            if (thread == null)
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            thread.AiEnabled = BoolOf(body, "enabled", false);
            thread.UpdatedAt = DateTime.UtcNow; // the SSE change token watches this
            await db.SaveChangesAsync();
            return Results.Ok(new { ai_enabled = thread.AiEnabled });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST …/status {status} — open | pending | snoozed | resolved.
        // (Inbound contact messages re-open resolved/snoozed threads in the store.)
        app.MapPost("/api/messaging/threads/{channel}/{key}/status",
            async (string channel, string key, Dictionary<string, JsonElement>? body,
                   AppDbContext db, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var value = (Str(body, "status") ?? "").Trim().ToLowerInvariant();
            if (!ValidStatuses.Contains(value))
                return Results.Json(new
                {
                    detail = $"status must be one of {string.Join(", ", ValidStatuses)}.",
                }, statusCode: 400);
            var thread = GetThread(db, channel, key);
            if (thread == null)
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            thread.Status = value;
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "thread.status", $"{channel}:{key}", value);
            return Results.Ok(new { status = thread.Status });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST …/assign {assignee} — "" clears the assignment.
        app.MapPost("/api/messaging/threads/{channel}/{key}/assign",
            async (string channel, string key, Dictionary<string, JsonElement>? body,
                   AppDbContext db, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var thread = GetThread(db, channel, key);
            if (thread == null)
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            var assignee = (Str(body, "assignee") ?? "").Trim();
            thread.Assignee = assignee.Length > 64 ? assignee[..64] : assignee;
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "thread.assign", $"{channel}:{key}",
                thread.Assignee.Length > 0 ? thread.Assignee : "(unassigned)");
            return Results.Ok(new { assignee = thread.Assignee });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST …/labels {labels: []} — replaces the list (lowercased, deduped,
        // 48 chars each, 20 max — the store's own normalization).
        app.MapPost("/api/messaging/threads/{channel}/{key}/labels",
            async (string channel, string key, Dictionary<string, JsonElement>? body,
                   AppDbContext db, ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (body == null || !body.TryGetValue("labels", out var raw) ||
                raw.ValueKind != JsonValueKind.Array)
                return Results.Json(new { detail = "labels must be a list of strings." }, statusCode: 400);
            var thread = GetThread(db, channel, key);
            if (thread == null)
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);

            var labels = new List<string>();
            foreach (var el in raw.EnumerateArray())
            {
                var label = (el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString())
                    .Trim().ToLowerInvariant();
                if (label.Length > 48) label = label[..48];
                if (label.Length > 0 && !labels.Contains(label)) labels.Add(label);
                if (labels.Count == 20) break;
            }
            thread.Labels = labels;
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "thread.labels", $"{channel}:{key}", string.Join(",", labels));
            return Results.Ok(new { labels = thread.Labels });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST …/note {text} — a private note: stored on the thread, NEVER sent
        // to the contact and kept out of the AI's context.
        app.MapPost("/api/messaging/threads/{channel}/{key}/note",
            (string channel, string key, Dictionary<string, JsonElement>? body,
             AppDbContext db, ThreadStore store, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var text = (Str(body, "text") ?? "").Trim();
            if (text.Length == 0)
                return Results.Json(new { detail = "text is required." }, statusCode: 400);
            var thread = GetThread(db, channel, key);
            if (thread == null)
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            var msg = store.AppendMessage(thread, MessageDirection.Out, MessageSource.Agent, text,
                status: "note", accountId: thread.AccountId, priv: true);
            Audit(db, me, ctx, "thread.note", $"{channel}:{key}", text);
            return Results.Ok(new { message = MessagePayload(msg, thread.Channel, thread.Account?.Name) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST …/link {contact_id} — attach the conversation to a CRM contact
        // (null unlinks). Channel-agnostic ON PURPOSE: the /chats page routes
        // WhatsApp threads here too, since identity linking is shared.
        app.MapPost("/api/messaging/threads/{channel}/{key}/link",
            async (string channel, string key, Dictionary<string, JsonElement>? body,
                   AppDbContext db, ScopingService sc, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var thread = GetThread(db, channel, key);
            if (thread == null)
                return Results.Json(new { detail = "Thread not found." }, statusCode: 404);

            var contactId = IntOrNull(body, "contact_id");
            if (contactId is int cid)
            {
                var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == cid);
                if (contact == null)
                    return Results.Json(new { detail = "Contact not found." }, statusCode: 404);
                thread.ContactId = contact.Id;
                thread.Contact = contact;
            }
            else
            {
                thread.ContactId = null;
                thread.Contact = null;
            }
            // The contact link is the only entity signal on these channels, so
            // linking (or unlinking) is what assigns the chat to an outlet.
            sc.SyncThreadEntity(thread, save: false);
            thread.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "thread.link", $"{channel}:{key}",
                $"contact={(contactId?.ToString() ?? "none")}");
            return Results.Ok(new
            {
                contact_id = thread.ContactId,
                contact_name = thread.ContactId != null ? thread.Contact?.Name ?? "" : "",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // =========================================================================
    // 5. engagement stats + per-contact timeline & AI summary
    // =========================================================================

    private static void MapEngagementAndTimeline(IEndpointRouteBuilder app)
    {
        // GET /api/messaging/engagement?entity= — the dashboard tiles.
        // Cross-channel (WhatsApp included, timeline.engagement_stats scans both
        // stores); call counters stay organization-wide.
        app.MapGet("/api/messaging/engagement", async (string? entity, AppDbContext db,
            ScopingService sc) =>
        {
            var stats = new Dictionary<string, object?>
            {
                ["threads_total"] = 0, ["awaiting_reply"] = 0, ["replied"] = 0,
                ["calls_total"] = 0, ["needs_human"] = 0, ["followups_set"] = 0,
            };
            try
            {
                var entityId = await InboxEndpoints.EntityId(db, entity);
                var ids = await sc.ScopeOwned(db.Threads.AsNoTracking(), entityId,
                        includeUnassigned: true)
                    .Select(t => t.Id).ToListAsync();

                // System messages (redirects, account-switch notes) are not
                // engagement — Django filters them out before counting.
                var msgs = await db.Messages.AsNoTracking()
                    .Where(m => ids.Contains(m.ThreadId) && m.Source != MessageSource.System)
                    .Select(m => new { m.ThreadId, m.Direction, m.Id, m.CreatedAt })
                    .ToListAsync();

                var threadsTotal = 0;
                var replied = 0;
                var awaiting = 0;
                foreach (var group in msgs.GroupBy(m => m.ThreadId))
                {
                    threadsTotal++;
                    if (group.Any(m => m.Direction == MessageDirection.In)) replied++;
                    var last = group.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Last();
                    if (last.Direction == MessageDirection.Out) awaiting++;
                }

                var calls = await db.CallRecords.AsNoTracking()
                    .Select(c => new { c.NeedsHuman, c.FollowupDate }).ToListAsync();

                stats["threads_total"] = threadsTotal;
                stats["awaiting_reply"] = awaiting;
                stats["replied"] = replied;
                stats["calls_total"] = calls.Count;
                stats["needs_human"] = calls.Count(c => c.NeedsHuman);
                stats["followups_set"] = calls.Count(c => !string.IsNullOrEmpty(c.FollowupDate));
            }
            catch { /* dashboard tiles degrade to zeros, never to a broken page */ }
            return Results.Ok(new { stats });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // GET /api/messaging/timeline?phone=&email=&limit= — every communication
        // across voice/WhatsApp/email/social, oldest first, plus the cached AI
        // summary. `total` is ALWAYS the full count so the contact page knows
        // how much more there is behind its preview.
        app.MapGet("/api/messaging/timeline", (string? phone, string? email, int? limit,
            AppDbContext db) =>
        {
            phone = (phone ?? "").Trim();
            email = (email ?? "").Trim();
            if (phone.Length == 0 && email.Length == 0)
                return Results.Json(new { detail = "phone or email query param required." },
                    statusCode: 400);

            var (events, contact) = CollectEvents(db, phone, email);
            var take = limit ?? 0;
            var slice = take > 0 && events.Count > take
                ? events.Skip(events.Count - take).ToList()
                : events;
            return Results.Ok(new Dictionary<string, object?>
            {
                ["events"] = slice,
                ["total"] = events.Count,
                ["summary"] = contact == null ? null : CachedSummary(db, contact.Id),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST /api/messaging/timeline/summary {name, phone?, email?, record?}
        // — (re)generate the AI case summary from the full cross-channel
        // timeline. Without an AI key a deterministic heuristic summary is
        // produced, so the feature still demos in sandbox.
        app.MapPost("/api/messaging/timeline/summary", async (Dictionary<string, JsonElement>? body,
            AppDbContext db, ILlmClient llm, CancellationToken ct) =>
        {
            var phone = (Str(body, "phone") ?? "").Trim();
            var email = (Str(body, "email") ?? "").Trim();
            if (phone.Length == 0 && email.Length == 0)
                return Results.Json(new { detail = "phone or email required." }, statusCode: 400);
            var name = (Str(body, "name") ?? "").Trim();

            // The contact page posts {context: {record}}; Django reads a
            // top-level `record`. Accept both rather than silently dropping it.
            var record = Str(body, "record") ?? "";
            if (record.Length == 0 && body != null &&
                body.TryGetValue("context", out var context) &&
                context.ValueKind == JsonValueKind.Object &&
                context.TryGetProperty("record", out var rec) && rec.ValueKind != JsonValueKind.Null)
                record = rec.ValueKind == JsonValueKind.String ? rec.GetString() ?? "" : rec.GetRawText();

            var (events, contact) = CollectEvents(db, phone, email);
            var result = await GenerateSummary(db, llm, name, phone, email, record, events, contact, ct);
            return Results.Ok(result);
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // ---- timeline.collect_events -------------------------------------------

    /// <summary>
    /// Every communication for this contact, oldest first, plus the CRM contact
    /// we matched (null when none).
    ///
    /// CONTACT-CENTRIC like Django: the contact's linked threads (any channel —
    /// the hub can bind numbers/addresses that differ from the CRM ones) are the
    /// source of truth; exact phone/email key lookups remain the fallback for
    /// unlinked threads. Deduped by (channel, key).
    /// </summary>
    private static (List<Dictionary<string, object?>> Events, Contact? Contact) CollectEvents(
        AppDbContext db, string phone, string email)
    {
        var events = new List<Dictionary<string, object?>>();
        var digits = Digits(phone);
        Contact? contact = null;
        try
        {
            // ---- AI voice calls (agent-written records) ----------------------
            if (digits.Length > 0)
            {
                var calls = db.CallRecords.AsNoTracking().ToList()
                    .Where(r => Digits(r.Phone) == digits).ToList();
                foreach (var c in calls)
                {
                    var outcome = string.IsNullOrEmpty(c.Outcome) ? "unclear" : c.Outcome;
                    events.Add(new Dictionary<string, object?>
                    {
                        ["channel"] = "voice",
                        ["dir"] = MessageDirection.Out,
                        ["source"] = MessageSource.Ai,
                        ["text"] = string.IsNullOrEmpty(c.Summary)
                            ? $"Voice call — outcome: {outcome}." : c.Summary,
                        ["ts"] = string.IsNullOrEmpty(c.EndedAt) ? c.StartedAt : c.EndedAt,
                        ["extra"] = new Dictionary<string, object?>
                        {
                            ["outcome"] = outcome,
                            ["followup_date"] = c.FollowupDate,
                            ["needs_human"] = c.NeedsHuman,
                            ["call_id"] = c.CallId,
                        },
                    });
                }
            }

            // ---- identity: which CRM contact are we talking about? -----------
            // Stored phones are formatted ("+60 18-445 6677"), so the match is
            // on digits — done in memory because SQLite cannot strip them.
            if (digits.Length > 0)
            {
                var tail = digits.Length >= 9 ? digits[^9..] : digits;
                contact = db.Contacts.AsNoTracking().Where(c => c.Phone != "").ToList()
                    .FirstOrDefault(c => Digits(c.Phone).EndsWith(tail));
            }
            if (contact == null && email.Length > 0)
                contact = db.Contacts.AsNoTracking()
                    .FirstOrDefault(c => c.Email.ToLower() == email.ToLower());

            // ---- the threads that count --------------------------------------
            var threads = new List<Thread>();
            var seen = new HashSet<string>();
            void Consider(Thread? t)
            {
                if (t == null) return;
                if (!seen.Add($"{t.Channel} {t.Key}")) return;
                threads.Add(t);
            }

            if (contact is Contact linked)
            {
                var owned = db.Threads.AsNoTracking().Where(x => x.ContactId == linked.Id).ToList();
                foreach (var t in owned) Consider(t);
                if (!string.IsNullOrEmpty(linked.Email))
                    Consider(db.Threads.AsNoTracking().FirstOrDefault(
                        t => t.Channel == Channels.Email && t.Key == linked.Email));
            }
            if (digits.Length > 0)
                Consider(db.Threads.AsNoTracking().FirstOrDefault(
                    t => t.Channel == Channels.WhatsApp && t.Key == digits));
            if (email.Length > 0)
                Consider(db.Threads.AsNoTracking().FirstOrDefault(
                    t => t.Channel == Channels.Email && t.Key == email));

            // ---- their messages (private notes stay OFF the timeline) --------
            var ids = threads.Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                var byId = threads.ToDictionary(t => t.Id);
                var rows = db.Messages.AsNoTracking()
                    .Where(x => ids.Contains(x.ThreadId) && !x.Private)
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
                foreach (var m in rows)
                {
                    var thread = byId[m.ThreadId];
                    var e = new Dictionary<string, object?>
                    {
                        ["channel"] = thread.Channel,
                        ["dir"] = m.Direction,
                        ["source"] = m.Source,
                        ["text"] = m.Text,
                        ["ts"] = Iso(m.CreatedAt),
                    };
                    if (!string.IsNullOrEmpty(m.MediaPath))
                        e["media"] = new Dictionary<string, object?>
                        {
                            ["type"] = string.IsNullOrEmpty(m.MediaType) ? "document" : m.MediaType,
                            ["url"] = MediaUrl(thread.Channel, m.Id),
                        };
                    events.Add(e);
                }
            }

            events = events
                .OrderBy(e => (string)(e["ts"] ?? ""), StringComparer.Ordinal)
                .ToList();
        }
        catch { /* a partial timeline beats a 500 on the contact page */ }
        return (events, contact);
    }

    // ---- timeline.generate_summary / cached_summary -------------------------

    /// <summary>
    /// Django caches contact summaries as files under
    /// <c>data/messaging/summaries/</c>. Here the cache is a
    /// <c>crm.ConversationSummary</c> row with source "timeline" — which means a
    /// summary is only remembered once the timeline resolves to a CRM contact
    /// (an unmatched phone still gets a summary, it just isn't cached).
    /// The generation metadata the contact page renders (event_count/as_of) rides
    /// in Facts[0]; Sentiment records whether the text came from the LLM.
    /// </summary>
    private static Dictionary<string, object?>? CachedSummary(AppDbContext db, int contactId)
    {
        try
        {
            var row = db.ConversationSummaries.AsNoTracking()
                .Where(s => s.ContactId == contactId && s.Source == "timeline")
                .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
                .FirstOrDefault();
            if (row == null) return null;

            var meta = row.Facts.FirstOrDefault();
            int eventCount = 0;
            var asOf = "";
            if (meta is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("event_count", out var ec) && ec.TryGetInt32(out var n))
                    eventCount = n;
                if (el.TryGetProperty("as_of", out var a) && a.ValueKind == JsonValueKind.String)
                    asOf = a.GetString() ?? "";
            }
            return new Dictionary<string, object?>
            {
                ["summary"] = row.Summary,
                ["heuristic"] = row.Sentiment == "heuristic",
                ["incremental"] = false,
                ["event_count"] = eventCount,
                ["as_of"] = asOf,
                ["generated_at"] = Iso(row.CreatedAt),
            };
        }
        catch { return null; }
    }

    private static async Task<Dictionary<string, object?>> GenerateSummary(AppDbContext db,
        ILlmClient llm, string name, string phone, string email, string record,
        List<Dictionary<string, object?>> events, Contact? contact, CancellationToken ct)
    {
        var heuristic = true;
        var text = HeuristicSummary(name, events);

        if (llm.IsConfigured && events.Count > 0)
        {
            // Full pass every time — Django's rolling "previous summary + only
            // the newer events" optimization is not ported.
            var header = $"Contact: {name} · phone {phone}"
                         + (record.Length > 0 ? $" · {record}" : "") + ".\n\n";
            var prompt = header
                + "Communication log (oldest→newest):\n"
                + string.Join("\n", EventLines(events.TakeLast(40)))
                + "\n\nWrite a 4-6 sentence relationship summary: engagement level per channel, "
                + "any commitments or complaints, contact sentiment, and ONE recommended next "
                + "action. Factual — no speculation presented as fact.";
            try
            {
                var result = await llm.CompleteAsync(
                    "You are a contact-conversation analyst. Be concise and factual.",
                    new[] { ("user", prompt) }, ct);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    text = result.Text.Trim();
                    heuristic = false;
                }
            }
            catch (Exception e)
            {
                // Keep serving a usable summary through a quota/network hiccup.
                var detail = e.Message.Split('\n')[0];
                text += $" [AI error: {(detail.Length > 140 ? detail[..140] : detail)}]";
            }
        }

        var asOf = events.Count > 0 ? (string)(events[^1]["ts"] ?? "") : "";
        var payload = new Dictionary<string, object?>
        {
            ["summary"] = text,
            ["heuristic"] = heuristic,
            ["incremental"] = false,
            ["event_count"] = events.Count,
            ["as_of"] = asOf,          // staleness anchor: the newest event seen
            ["generated_at"] = Iso(DateTime.UtcNow),
        };

        if (contact != null)
        {
            try
            {
                db.ConversationSummaries.Add(new ConversationSummary
                {
                    ContactId = contact.Id,
                    Source = "timeline",
                    Channel = "",
                    Ref = phone.Length > 0 ? phone : email,
                    Summary = text,
                    Sentiment = heuristic ? "heuristic" : "ai",
                    Facts = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["event_count"] = events.Count,
                            ["as_of"] = asOf,
                        },
                    },
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
            catch { /* the summary is still returned — caching is best-effort */ }
        }
        return payload;
    }

    private static IEnumerable<string> EventLines(IEnumerable<Dictionary<string, object?>> events) =>
        events.Select(e =>
        {
            var ts = (string)(e["ts"] ?? "");
            var text = (string)(e["text"] ?? "");
            var media = e.ContainsKey("media") ? "[media] " : "";
            return $"[{(ts.Length > 16 ? ts[..16] : ts)}] {e["channel"]}/{e.GetValueOrDefault("source")} "
                   + ((string?)e["dir"] == MessageDirection.In ? "←contact" : "→outbound") + ": "
                   + media + (text.Length > 300 ? text[..300] : text);
        });

    /// <summary>timeline._heuristic_summary — the zero-credential fallback.</summary>
    private static string HeuristicSummary(string name, List<Dictionary<string, object?>> events)
    {
        if (events.Count == 0)
            return $"No recorded communications with {(name.Length > 0 ? name : "this contact")} yet.";

        var byChannel = new Dictionary<string, int>();
        var replies = 0;
        Dictionary<string, object?>? lastIn = null;
        foreach (var e in events)
        {
            var channel = (string)(e["channel"] ?? "");
            byChannel[channel] = byChannel.GetValueOrDefault(channel) + 1;
            if ((string?)e["dir"] == MessageDirection.In &&
                (string?)e.GetValueOrDefault("source") == MessageSource.Customer)
            {
                replies++;
                lastIn = e;
            }
        }

        var parts = new List<string>
        {
            $"{events.Count} contacts on record " +
            $"({string.Join(", ", byChannel.Select(kv => $"{kv.Value}× {kv.Key}"))}); " +
            $"{replies} contact replies.",
        };
        var outcomes = events
            .Where(e => (string?)e["channel"] == "voice" && e.GetValueOrDefault("extra") is Dictionary<string, object?>)
            .Select(e => (string?)((Dictionary<string, object?>)e["extra"]!).GetValueOrDefault("outcome"))
            .Where(o => !string.IsNullOrEmpty(o)).ToList();
        if (outcomes.Count > 0) parts.Add($"Last call outcome: {outcomes[^1]}.");
        if (lastIn != null)
        {
            var t = (string)(lastIn["text"] ?? "");
            parts.Add($"Last contact message: \"{(t.Length > 120 ? t[..120] : t)}\".");
        }
        parts.Add("(Heuristic summary — add an OpenAI/OpenRouter key in " +
                  "Settings → AI & Providers for AI analysis.)");
        return string.Join(" ", parts);
    }
}
