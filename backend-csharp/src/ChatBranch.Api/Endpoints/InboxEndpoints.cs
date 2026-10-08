using System.Text.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Unified-inbox surface (whatsapp + messaging). In sandbox mode (no linked
/// account) sends store locally with status "sandbox" and <c>/simulate</c> fakes
/// inbound — same rule as the Django app. The SSE <c>/stream</c> lives under the
/// whatsapp app but covers ALL channels via crm.Thread.
/// </summary>
public static class InboxEndpoints
{
    /// <summary>Wire shape the chat panel's ThreadSummary type expects.</summary>
    private static object SummaryPayload(ChatBranch.Data.Ai.SummaryService.Brief b) => new
    {
        summary = b.Summary,
        heuristic = b.Heuristic,
        message_count = b.MessageCount,
        as_of = b.AsOf,
        generated_at = b.GeneratedAt,
    };

    public record SendRequest(string Text, bool Force = false, int? AccountId = null);
    public record SimulateRequest(string Channel, string Key, string Text, string? Name = null);
    // Wire is snake_case: the dashboard posts {short_code, content} — bind that
    // name explicitly (the default camelCase binder left ShortCode null → 500).
    public record CannedWrite(
        [property: JsonPropertyName("short_code")] string ShortCode,
        string Content,
        string? Entity = null);

    public static void MapInboxEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- status ------------------------------------------------------------
        // Open like Django's (the /chats page reads it before anything else) and
        // deliberately re-mapped over the boot-time liveness stub in Program.cs —
        // a lower Order wins the route match, so this shape is what ships.
        app.MapGet("/api/whatsapp/status", async (AppDbContext db, ScopingService sc) =>
        {
            var accounts = await db.ChannelAccounts.AsNoTracking()
                .Where(a => a.Channel == Channels.WhatsApp).ToListAsync();
            var envAccount = WhatsAppSettingsEndpoints.EnvAccountConfigured();
            var configured = accounts.Any(WhatsAppSettingsEndpoints.IsConfigured) || envAccount;
            return Results.Ok(new
            {
                // liveness fields (kept — the open probe + smoke test read them)
                ok = true,
                service = "chatbranch-csharp",
                stack = "asp.net-core",
                configured,
                // no linked account -> everything stores locally and /simulate
                // fakes inbound; the composer shows the sandbox banner
                sandbox = !configured,
                ai_available = WhatsAppSettingsEndpoints.AiAvailable(sc),
                accounts = accounts.Count + (envAccount ? 1 : 0),
            });
        }).AllowAnonymous().Add(b => ((RouteEndpointBuilder)b).Order = -1);

        // ---- accounts ----------------------------------------------------------
        // WhatsApp accounts by default (the Settings → Channels → WhatsApp panel
        // and the chats sender picker both read this); ?channel=<c> switches
        // channel, ?channel=all reports every provider account.
        app.MapGet("/api/whatsapp/accounts", async (string? channel, AppDbContext db) =>
        {
            var ch = string.IsNullOrWhiteSpace(channel) ? Channels.WhatsApp : channel.Trim();
            var q = db.ChannelAccounts.AsNoTracking().Include(a => a.Entity).AsQueryable();
            if (ch is not ("all" or "__all__")) q = q.Where(a => a.Channel == ch);
            var rows = await q.OrderBy(a => a.Id).ToListAsync();

            var accounts = rows.Select(WhatsAppSettingsEndpoints.AccountPayload).ToList();
            // The read-only row backend/.env contributes — WhatsApp only, and
            // only when .env actually configures a usable transport.
            if (ch is Channels.WhatsApp or "all" or "__all__"
                && WhatsAppSettingsEndpoints.EnvAccountPayload() is object env)
                accounts.Add(env);
            return Results.Ok(new { accounts });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- threads -----------------------------------------------------------
        // One row per conversation for the inbox list: identity, the 24h-window
        // truth, the per-account chatbot tick and the last-message preview.
        app.MapGet("/api/whatsapp/threads", async (string? channel, string? entity, string? status,
            int? limit, AppDbContext db, ThreadStore store, ScopingService sc, ClaimsPrincipal me) =>
        {
            var entityId = await EntityId(db, entity);
            // Agents (and custom roles) see only the entities assigned to them —
            // and everything beneath those entities. Asking for an outlet outside
            // that scope narrows to their own rather than widening the inbox.
            var role = me.FindFirstValue(System.Security.Claims.ClaimTypes.Role) ?? "";
            if (role is not (Roles.SuperAdmin or Roles.Admin))
            {
                var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
                if (assigned.Count > 0)
                {
                    var allowed = sc.EntitySubtreeIds(assigned);
                    if (entityId is not int picked || !allowed.Contains(picked))
                        entityId = assigned[0];   // their own scope, never the tenant's
                }
            }
            var rows = store.ListThreads(channel, entityId, status,
                                         limit is int n && n > 0 ? n : 100);
            // Provider login: only conversations with THEIR bound customers.
            if (ProviderScope.ResourceId(me) is int rid)
            {
                var bound = db.ResourceLinks.AsNoTracking()
                    .Where(l => l.ResourceId == rid && l.Active).Select(l => l.ContactId).ToHashSet();
                rows = rows.Where(t => t.ContactId is int cid && bound.Contains(cid)).ToList();
            }
            return Results.Ok(new { threads = await ThreadSummaries(db, sc, rows, entityId) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapGet("/api/whatsapp/threads/{channel}/{key}/messages",
            (string channel, string key, int? limit, int? before, ThreadStore store) =>
        {
            var thread = store.GetThread(channel, key);
            if (thread == null) return Results.NotFound(new { detail = "thread not found" });
            var msgs = store.GetMessages(thread, limit ?? 50, before);
            return Results.Ok(new { messages = msgs.Select(Dtos.MessageDto) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- sandbox send ------------------------------------------------------
        app.MapPost("/api/whatsapp/threads/{channel}/{key}/send",
            (string channel, string key, SendRequest body, ThreadStore store,
             ChatBranch.Data.Channels.OutboundSender sender) =>
        {
            var thread = store.GetOrCreateThread(channel, key);
            if (thread.OptedOut && !body.Force)
                return Results.Json(new { detail = "contact opted out; use force" }, statusCode: 409);
            // Real send when the account is configured, sandbox otherwise.
            var msg = sender.Send(thread, body.Text, MessageSource.Agent, body.AccountId ?? thread.AccountId);
            return Results.Ok(Dtos.MessageDto(msg));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- simulate inbound (sandbox) ---------------------------------------
        app.MapPost("/api/whatsapp/simulate", async (SimulateRequest body, ThreadStore store, AppDbContext db,
            ChatBranch.Data.Flows.FlowEngine engine, ChatBranch.Data.Channels.InboundProcessor inbound,
            ChatBranch.Data.Flows.AutomationService automation) =>
        {
            // Link the thread to its contact the same way a real webhook does
            // (phone/email match) — otherwise simulated chats stay unlinked and
            // never appear in a provider's scoped inbox.
            var matched = inbound.MatchContact(body.Channel, body.Key);
            var thread = store.GetOrCreateThread(body.Channel, body.Key, body.Name, contactId: matched?.Id);
            if (thread.ContactId == null && matched != null) { thread.ContactId = matched.Id; }
            var msg = store.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
                body.Text, status: "received");
            // event axis: resume any flow runs parked on a reply from this contact
            engine.OnInboundMessage(body.Channel, body.Key, body.Text);
            // A simulated inbound is still an INBOUND: route it to a team the
            // same way the live webhook does, or the sandbox behaves differently
            // from production in a way that hides bugs.
            await TeamsEndpoints.AutoAssignAsync(db, thread);
            // …and through the automation rules, for the same reason.
            var firstInbound = db.Messages.Count(m => m.ThreadId == thread.Id
                                                      && m.Direction == MessageDirection.In) <= 1;
            if (firstInbound) automation.Run("conversation_created", thread, body.Text, true);
            automation.Run("message_created", thread, body.Text, firstInbound);
            await NotificationService.NotifyInboundMessageAsync(db, thread, body.Text);
            // …and the AI step (monitor, debounce, coverage-gated auto-reply)
            inbound.AfterSimulatedInbound(thread);
            return Results.Ok(new { thread = Dtos.ThreadDto(thread), message = Dtos.MessageDto(msg) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- SSE change stream (all channels) ---------------------------------
        app.MapGet("/api/whatsapp/stream", async (HttpContext ctx, ThreadStore store, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var last = "";
            var idle = 0;
            while (!ct.IsCancellationRequested && idle < 300) // ~10min cap
            {
                var token = store.ChangeToken();
                if (token != last)
                {
                    last = token;
                    await ctx.Response.WriteAsync($"event: change\ndata: {token}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    idle = 0;
                }
                else
                {
                    if (idle % 15 == 0) // keepalive comment
                    {
                        await ctx.Response.WriteAsync(": keepalive\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                    idle++;
                }
                await Task.Delay(2000, ct);
            }
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- canned responses (cross-channel shared templates) -----------------
        // ?contact_id=<pk> personalizes each reply's `rendered` text with that
        // contact's CRM variables; unlinked chats get the raw content back.
        // ---- AI case summary --------------------------------------------------
        // The chat info panel reads this per thread and the contact page posts
        // the timeline variant. Neither was ported, so the panel 404'd on every
        // conversation and "Generate summary" silently did nothing.
        app.MapGet("/api/messaging/threads/{channel}/{key}/summary",
            (string channel, string key, ThreadStore store, ChatBranch.Data.Ai.SummaryService svc) =>
        {
            var thread = store.GetThread(channel, key);
            if (thread == null) return Results.NotFound(new { detail = "thread not found" });
            var brief = svc.Stored(thread);
            var (count, _) = svc.Extent(thread);
            return Results.Ok(new
            {
                summary = brief == null ? null : SummaryPayload(brief),
                stale = svc.IsStale(thread, brief),
                message_count = count,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/messaging/threads/{channel}/{key}/summary",
            async (string channel, string key, ThreadStore store,
                   ChatBranch.Data.Ai.SummaryService svc) =>
        {
            var thread = store.GetThread(channel, key);
            if (thread == null) return Results.NotFound(new { detail = "thread not found" });
            var brief = await svc.GenerateAsync(thread);
            var (count, _) = svc.Extent(thread);
            return Results.Ok(new
            {
                summary = brief == null ? null : SummaryPayload(brief),
                stale = false,
                message_count = count,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // NOTE: POST /api/messaging/timeline/summary (the contact page's
        // "Generate summary") already lives in MessagingEndpoints — it handles
        // the {context:{record}} blob and the heuristic fallback. Registering a
        // second one here made every call an AmbiguousMatchException 500.

        app.MapGet("/api/messaging/canned", async (string? entity, int? contact_id,
            AppDbContext db, ScopingService sc, FlowEngine flows) =>
        {
            var entityId = await EntityId(db, entity);
            var rows = sc.ScopeInherited(db.CannedResponses.AsNoTracking(), entityId)
                .OrderBy(c => c.ShortCode).ToList();

            Dictionary<string, string>? variables = null;
            if (contact_id is int cid)
            {
                var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid);
                // A rendering hiccup must never cost the operator their replies.
                if (contact != null)
                    try { variables = flows.VariablesFor(contact); } catch { }
            }
            return Results.Ok(new { canned = rows.Select(c => CannedPayload(c, variables)) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/messaging/canned", async (CannedWrite body, AppDbContext db) =>
        {
            var entityId = await EntityId(db, body.Entity);
            if (db.CannedResponses.Any(c => c.ShortCode == body.ShortCode && c.EntityId == entityId))
                return Results.Json(new { detail = "duplicate short_code" }, statusCode: 409);
            var row = new CannedResponse
            {
                ShortCode = body.ShortCode, Content = body.Content, EntityId = entityId,
                CreatedAt = DateTime.UtcNow,
            };
            db.CannedResponses.Add(row);
            await db.SaveChangesAsync();
            return Results.Ok(CannedPayload(row, null));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapMethods("/api/messaging/canned/{id:int}", new[] { "PATCH" },
            async (int id, CannedWrite body, AppDbContext db) =>
        {
            var row = await db.CannedResponses.FindAsync(id);
            if (row == null) return Results.NotFound();
            row.Content = body.Content;
            if (!string.IsNullOrEmpty(body.ShortCode)) row.ShortCode = body.ShortCode;
            await db.SaveChangesAsync();
            return Results.Ok(CannedPayload(row, null));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapDelete("/api/messaging/canned/{id:int}", async (int id, AppDbContext db) =>
        {
            await db.CannedResponses.Where(c => c.Id == id).ExecuteDeleteAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    // ======================= payload builders ================================

    /// <summary>messaging/views._canned_payload — snake_case, `rendered` last.</summary>
    private static object CannedPayload(CannedResponse c, Dictionary<string, string>? variables) => new
    {
        id = c.Id,
        short_code = c.ShortCode,
        content = c.Content,
        created_by = c.CreatedBy,
        entity_id = c.EntityId,
        // Contact-bound quick reply: {{name}}/{{amount}}/… filled from the CRM
        // (same variable catalog as the flow builder). Raw content when unlinked.
        rendered = variables != null ? FlowEngine.Render(c.Content, variables) : c.Content,
    };

    /// <summary>
    /// whatsapp/store.list_threads — the inbox row shape. The aggregates (last
    /// message, newest inbound, message count) are correlated subqueries rather
    /// than a per-thread round trip, and every one of them is needed: the
    /// newest INBOUND time is what the 24h window is measured from.
    /// </summary>
    private static async Task<List<object>> ThreadSummaries(AppDbContext db, ScopingService sc,
        List<Thread> threads, int? entityId)
    {
        var ids = threads.Select(t => t.Id).ToList();
        var stats = (await db.Threads.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new
            {
                t.Id,
                Count = t.Messages.Count(),
                LastTs = t.Messages.Max(m => (DateTime?)m.CreatedAt),
                LastInTs = t.Messages.Where(m => m.Direction == MessageDirection.In)
                    .Max(m => (DateTime?)m.CreatedAt),
                Last = t.Messages.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                    .Select(m => new { m.Text, m.Direction, m.Status }).FirstOrDefault(),
            }).ToListAsync()).ToDictionary(x => x.Id);

        // The account lookup mirrors wa_config.get_account(): an unknown/missing
        // account id falls back to the SOLE configured account, and never guesses
        // between several — a wrong pick would report another number's window.
        var waAccounts = await db.ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == Channels.WhatsApp).ToListAsync();
        var byId = waAccounts.ToDictionary(a => a.Id);
        var configured = waAccounts.Where(WhatsAppSettingsEndpoints.IsConfigured).ToList();
        var envConfigured = WhatsAppSettingsEndpoints.EnvAccountConfigured();
        // .env contributes one more configured account to that count, but has no
        // row to fall back TO — so it can only make the fallback ambiguous.
        var fallback = configured.Count == 1 && !envConfigured ? configured[0] : null;
        var envIsSoleFallback = configured.Count == 0 && envConfigured;

        // Entity-scoped default for the chatbot tick on reminder-role numbers.
        var reminderDefaultOn = sc.SettingValue("reminder_ai_default", entityId) == "on";

        var outv = new List<object>();
        foreach (var t in threads)
        {
            var stat = stats.GetValueOrDefault(t.Id);
            var account = t.AccountId is int aid && byId.TryGetValue(aid, out var own)
                ? own : fallback;
            // Baileys is a normal WhatsApp account over WhatsApp Web — Meta's
            // 24h contact-service window does not apply to it. A sandbox thread
            // is always open too: nothing real is sent, and the demo flow must
            // never dead-end on a closed window.
            var live = account != null
                ? WhatsAppSettingsEndpoints.IsConfigured(account) : envIsSoleFallback;
            var baileys = account?.Provider == Providers.Baileys;

            var windowOpen = true;
            string? windowExpiresAt = null;
            if (live && !baileys)
            {
                if (stat?.LastInTs is DateTime lastIn)
                {
                    var expires = DateTime.SpecifyKind(lastIn, DateTimeKind.Utc).AddHours(24);
                    windowOpen = DateTime.UtcNow < expires;
                    windowExpiresAt = expires.ToString("o");
                }
                else
                {
                    windowOpen = false; // contact never wrote in -> template territory
                }
            }

            outv.Add(new
            {
                wa_id = t.Key,
                phone = t.Address != "" ? t.Address : $"+{t.Key}",
                name = t.Name != "" ? t.Name : $"+{t.Key}",
                channel = t.Channel,
                status = t.Status,
                account_id = t.AccountId?.ToString(),
                account_name = t.Account?.Name ?? "",
                provider = baileys ? Providers.Baileys : Providers.Cloud,
                ai_enabled = WhatsAppSettingsEndpoints.EffectiveAi(t, account, reminderDefaultOn),
                assignee = t.Assignee,
                team_id = t.TeamId,
                opted_out = t.OptedOut,
                window_open = windowOpen,
                window_expires_at = windowExpiresAt,
                contact_id = t.ContactId,
                contact_name = t.Contact?.Name ?? "",
                labels = t.Labels,
                call = new
                {
                    permission = t.CallPermission,
                    call_id = t.CallId,
                    status = t.CallStatus,
                    room = t.CallRoom,
                },
                last_message = stat?.Last?.Text ?? "",
                // direction + delivery status of the newest message — the chat
                // list shows WhatsApp-style ticks on each row's last outbound
                last_dir = stat?.Last?.Direction ?? "",
                last_status = stat?.Last?.Status ?? "",
                last_ts = DateTime.SpecifyKind(stat?.LastTs ?? t.UpdatedAt, DateTimeKind.Utc)
                    .ToString("o"),
                message_count = stat?.Count ?? 0,
            });
        }
        return outv;
    }

    internal static async Task<int?> EntityId(AppDbContext db, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "all" or "__all__") return null;
        var ent = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Slug == slug);
        return ent?.Id;
    }
}
