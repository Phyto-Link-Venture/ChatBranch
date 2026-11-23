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
    /// <summary><c>account_id</c> = the number the fake message "arrives" on (its company's conversation,
    /// exactly like a webhook); none = the unattributed / only conversation, as before.</summary>
    public record SimulateRequest(string Channel, string Key, string Text, string? Name = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("account_id")] int? AccountId = null);

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
        // channel, ?channel=all reports every provider account. The full row
        // (ids, masked credentials, delivery config) is for channels.view /
        // templates.manage; the inbox gets the picker shape (PickerKeys).
        app.MapGet("/api/whatsapp/accounts", async (string? channel, string? entity, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var ch = string.IsNullOrWhiteSpace(channel) ? Channels.WhatsApp : channel.Trim();
            var q = db.ChannelAccounts.AsNoTracking().Include(a => a.Entity).AsQueryable();
            if (ch is not ("all" or "__all__")) q = q.Where(a => a.Channel == ch);
            // an entity-bound agent: only accounts their outlets may use (their
            // subtrees + ancestors + org-wide) — never a sibling outlet's numbers
            if (ShellScope.BoundInheritedIds(db, sc, me) is List<int> usable)
                q = q.Where(a => a.EntityId == null || usable.Contains(a.EntityId.Value));
            // someone limited to some channels: only those
            if (ShellScope.ChannelAccountIds(db, me) is List<int> mine)
                q = q.Where(a => mine.Contains(a.Id));
            // ?entity= (the switcher) narrows to what that outlet inherits
            if (ShellScope.ClampPick(ShellScope.BoundEntityIds(db, sc, me), await EntityId(db, entity)) is int picked)
                q = sc.ScopeInherited(q, picked);
            var rows = await q.OrderBy(a => a.Id).ToListAsync();

            var accounts = rows.Select(WhatsAppSettingsEndpoints.AccountPayload).ToList();
            // The read-only row backend/.env contributes — WhatsApp only, and
            // only when .env actually configures a usable transport.
            if (ch is Channels.WhatsApp or "all" or "__all__"
                && WhatsAppSettingsEndpoints.EnvAccountPayload() is object env)
                accounts.Add(env);
            if (!await ShellScope.HasCapAsync(db, me, "channels.view")
                && !await ShellScope.HasCapAsync(db, me, "templates.manage"))
                accounts = accounts.Select(PickerShape).ToList();
            return Results.Ok(new { accounts });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireAnyCapability("chats.view", "channels.view", "templates.manage");

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
            // superadmin + org-wide admins: unbound; company admins, agents: their entities
            {
                var assigned = ShellScope.ScopeRoots(db, me);
                if (assigned.Count > 0)
                {
                    var allowed = sc.EntitySubtreeIds(assigned);
                    if (entityId is not int picked || !allowed.Contains(picked))
                        entityId = assigned[0];   // their own scope, never the tenant's
                }
            }
            var rows = store.ListThreads(channel, entityId, status,
                                         limit is int n && n > 0 ? n : 100);
            // the channel allow-list narrows further (unattributed conversations stay visible)
            var access = ShellScope.Access(db, me);
            if (access.Limited)
                rows = rows.Where(t => ShellScope.ThreadVisible(t.EntityId, t.Channel, t.AccountId, null, access)).ToList();
            return Results.Ok(new { threads = await ThreadSummaries(db, sc, rows, entityId) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.view");

        app.MapGet("/api/whatsapp/threads/{channel}/{key}/messages",
            async (string channel, string key, int? limit, int? before, ThreadStore store, HttpContext ctx) =>
        {
            var thread = await ThreadResolver.LoadAsync(ctx);
            if (thread == null) return Results.NotFound(new { detail = "thread not found" });
            var msgs = store.GetMessages(thread, limit ?? 50, before);
            return Results.Ok(new { messages = msgs.Select(Dtos.MessageDto) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.view");

        // ---- sandbox send ------------------------------------------------------
        app.MapPost("/api/whatsapp/threads/{channel}/{key}/send",
            async (string channel, string key, SendRequest body, ThreadStore store, AppDbContext db,
             ScopingService sc, ChatBranch.Data.Channels.OutboundSender sender, ClaimsPrincipal me, HttpContext ctx) =>
        {
            // a named number must be one the caller may use
            if (ShellScope.AccountNotAllowed(db, me, body.AccountId) is { } notMine) return notMine;
            if (body.AccountId is int named && ShellScope.BoundInheritedIds(db, sc, me) is List<int> usable
                && await db.ChannelAccounts.AsNoTracking().Where(a => a.Id == named).Select(a => a.EntityId)
                    .FirstOrDefaultAsync() is int namedEntity && !usable.Contains(namedEntity))
                return Results.Json(new { detail = "That number belongs to an entity outside your scope." },
                    statusCode: 403);
            // the caller's own conversation (ThreadAccessFilter resolved it), else a new one
            // in their company — never another company's conversation with this customer
            var thread = await ThreadResolver.LoadAsync(ctx);
            if (thread == null)
            {
                var place = await ThreadStart.PlaceAsync(db, sc, me, accountId: body.AccountId);
                if (place.Error != null) return place.Error;
                thread = store.GetOrCreateInCompany(channel, key, place.Company, accountId: body.AccountId,
                    entityHint: place.Entity);
            }
            else if (body.AccountId is int other
                     && !ScopingService.SameCompanyOrShared(sc.ThreadCompany(thread), sc.AccountCompany(other)))
                return Results.Json(new { detail = "That number belongs to another company — this conversation "
                                                   + "can't go out on it." }, statusCode: 409);
            if (thread.OptedOut && !body.Force)
                return Results.Json(new { detail = "contact opted out; use force" }, statusCode: 409);
            // Routing at send time unless the caller named the account: the
            // thread's healthy number → sticky / least-loaded → failover.
            var accountId = body.AccountId;
            if (accountId == null)
            {
                var route = new ChatBranch.Data.Channels.Routing(db, sc).ForThread(thread,
                    channel == Channels.WhatsApp
                        ? ChatBranch.Data.Channels.RouteNeed.Conversation
                        : ChatBranch.Data.Channels.RouteNeed.Any, apply: true);
                if (!route.Ok) return Results.Json(new { detail = route.Error }, statusCode: 409);
                accountId = route.Account?.Id ?? thread.AccountId;
            }
            // Real send when the account is configured, sandbox otherwise.
            var msg = sender.Send(thread, body.Text, MessageSource.Agent, accountId);
            AiControlsEndpoints.HumanReplied(db, thread, msg, me); // answers an open handover
            return Results.Ok(Dtos.MessageDto(msg));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- simulate inbound (sandbox) ---------------------------------------
        app.MapPost("/api/whatsapp/simulate", async (SimulateRequest body, string? ai, ThreadStore store, AppDbContext db,
            ChatBranch.Data.Flows.FlowEngine engine, ChatBranch.Data.Channels.InboundProcessor inbound,
            ChatBranch.Data.Flows.AutomationService automation, ClaimsPrincipal me) =>
        {
            // someone bound to entities simulates only on their own numbers and conversations
            var sc = new ScopingService(db);
            if (ShellScope.BoundInheritedIds(db, sc, me) is List<int> usable)
            {
                if (body.AccountId is int aid && await db.ChannelAccounts.AsNoTracking().Where(a => a.Id == aid)
                        .Select(a => a.EntityId).FirstOrDefaultAsync() is int accountEntity
                    && !usable.Contains(accountEntity))
                    return Results.Json(new { detail = "That number belongs to an entity outside your scope." },
                        statusCode: 403);
                if (ShellScope.AccountNotAllowed(db, me, body.AccountId) is { } notMine) return notMine;
                if (store.FindForAccount(body.Channel, body.Key, body.AccountId) is { } target
                    && !ThreadAccessFilter.InScope(db, sc, me, target))
                    return Results.Json(new { detail = "Thread not found." }, statusCode: 404);
            }
            // The same get-or-create, contact link (phone / email, only a contact the
            // receiving company may see) and same-company re-attribution as a real
            // webhook: a simulated message to a number lands exactly where a real one
            // would — a second company's number gets that company's own conversation.
            var thread = inbound.ReceiveThread(body.Channel, body.Key, body.Name, body.AccountId);
            var msg = store.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
                body.Text, status: "received", accountId: body.AccountId);
            // STOP / START work in the sandbox exactly as on a live webhook
            inbound.ApplyOptOut(thread, body.Text);
            // background AI tasks read a simulated message like a real one (queued, run by their worker)
            ChatBranch.Data.Ai.Tasks.AiTaskService.Enqueue(db, new ScopingService(db), thread, msg);
            // event axis: resume any flow runs parked on a reply from this contact
            engine.OnInboundMessage(thread, body.Text);
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
            // ?ai=1 also runs the AI step a webhook runs (stale AI drafts expire,
            // debounce, coverage-gated auto-reply through the AI monitor). Opt-in:
            // a plain simulate stays a bare inbound, which the inbox relies on.
            if (ai is "1" or "true") inbound.AfterSimulatedInbound(thread);
            return Results.Ok(new { thread = Dtos.ThreadDto(thread), message = Dtos.MessageDto(msg) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- SSE change stream (all channels) ---------------------------------
        app.MapGet("/api/whatsapp/stream", async (HttpContext ctx, ThreadStore store, StreamChangeToken tokens,
            CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var last = "";
            var idle = 0;
            while (!ct.IsCancellationRequested && idle < 300) // ~10min cap
            {
                // shared across the process's open streams for a second (StreamChangeToken)
                var token = tokens.Get(store);
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
            async (string channel, string key, ChatBranch.Data.Ai.SummaryService svc, HttpContext ctx) =>
        {
            var thread = await ThreadResolver.LoadAsync(ctx);
            if (thread == null) return Results.NotFound(new { detail = "thread not found" });
            var brief = svc.Stored(thread);
            var (count, _) = svc.Extent(thread);
            return Results.Ok(new
            {
                summary = brief == null ? null : SummaryPayload(brief),
                stale = svc.IsStale(thread, brief),
                message_count = count,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.view");

        app.MapPost("/api/messaging/threads/{channel}/{key}/summary",
            async (string channel, string key, ChatBranch.Data.Ai.SummaryService svc, AppDbContext db,
                   ScopingService sc, ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (QuotaGuard.Refusal(db, sc, me) is { } quota) return quota;
            var thread = await ThreadResolver.LoadAsync(ctx);
            if (thread == null) return Results.NotFound(new { detail = "thread not found" });
            var brief = await svc.GenerateAsync(thread);
            var (count, _) = svc.Extent(thread);
            return Results.Ok(new
            {
                summary = brief == null ? null : SummaryPayload(brief),
                stale = false,
                message_count = count,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.view");

        // NOTE: POST /api/messaging/timeline/summary (the contact page's
        // "Generate summary") already lives in MessagingEndpoints — it handles
        // the {context:{record}} blob and the heuristic fallback. Registering a
        // second one here made every call an AmbiguousMatchException 500.

        app.MapGet("/api/messaging/canned", async (string? entity, int? contact_id, string? channel,
            AppDbContext db, ScopingService sc, FlowEngine flows, ClaimsPrincipal me) =>
        {
            var entityId = await EntityId(db, entity);
            var rows = ShellScope.ScopeLibrary(db.CannedResponses.AsNoTracking(), db, sc, me, entityId)
                .Include(c => c.Entity)
                .OrderBy(c => c.ShortCode).ToList();
            // ?channel=<thread channel> — only the replies offered there. An
            // empty channel list means "every channel"; a channel this build does
            // not know still gets those shared replies, never an error.
            if (!string.IsNullOrWhiteSpace(channel))
                rows = rows.Where(c => ChatBranch.Data.Channels.TemplateChannels.Offers(c.Channels, channel))
                    .ToList();

            Dictionary<string, string>? variables = null;
            if (contact_id is int cid)
            {
                // only a contact the caller may see — otherwise the rendered
                // variables would leak another outlet's customer data
                var contact = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                    .FirstOrDefaultAsync(c => c.Id == cid);
                // A rendering hiccup must never cost the operator their replies.
                if (contact != null)
                    try { variables = flows.VariablesFor(contact); } catch { }
            }
            var scopes = LibraryEntities.Load(db, rows);
            return Results.Ok(new { canned = rows.Select(c => CannedPayload(c, variables, scopes)) });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          // the chat composer's quick replies + Templates → Messages
          .RequireAnyCapability("chats.view", "chats.send", "templates.manage");

        // Writes are gated on the templates.manage CAPABILITY (admin by default,
        // grantable to custom roles from the permission matrix), checked here
        // rather than by a role policy so the matrix stays the single source.
        app.MapPost("/api/messaging/canned", async (JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (!await CanManageTemplates(db, me)) return CannedForbidden();
            var shortCode = CannedCode(CStr(body, "short_code"));
            var content = (CStr(body, "content") ?? "").Trim();
            if (shortCode.Length == 0) return CannedBad("short_code is required.");
            if (content.Length == 0) return CannedBad("content is required.");
            var (channels, chErr) = ChatBranch.Data.Channels.TemplateChannels.Parse(CList(body, "channels"));
            if (chErr != null) return CannedBad(chErr);
            // "Available to": entities [slug|id…] (or the older single `entity`); none = everyone
            var pick = await LibraryScope.ReadAsync(db, body);
            if (pick.Error != null) return pick.Error;
            if (await LibraryScope.Check(db, sc, me, null, pick.Ids) is { } denied) return denied;
            var entityId = pick.Ids.Count > 0 ? pick.Ids[0] : (int?)null;
            if (await db.CannedResponses.AnyAsync(c => c.ShortCode == shortCode && c.EntityId == entityId))
                return Results.Json(new { detail = "duplicate short_code" }, statusCode: 409);
            var row = new CannedResponse
            {
                ShortCode = shortCode, Content = content, EntityId = entityId, Channels = channels,
                CreatedBy = me.Identity?.Name ?? "",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.CannedResponses.Add(row);
            await db.SaveChangesAsync();
            LibraryEntities.Set(db, row, pick.Ids);
            await db.SaveChangesAsync();
            await db.Entry(row).Reference(c => c.Entity).LoadAsync();
            await CannedAudit(db, me, ctx, "canned.create", row);
            return Results.Ok(CannedPayload(row, null, LibraryEntities.Load(db, new[] { row })));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Partial update: only the keys the body carries change.
        app.MapMethods("/api/messaging/canned/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, AppDbContext db, ScopingService sc, ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (!await CanManageTemplates(db, me)) return CannedForbidden();
            var row = await db.CannedResponses.FirstOrDefaultAsync(c => c.Id == id);
            if (row == null) return Results.NotFound(new { detail = "Template not found." });
            if (LibraryScope.CheckExisting(db, sc, me,
                    LibraryEntities.Ids(db, LibraryKinds.Canned, row.Id, row.EntityId)) is { } locked) return locked;

            if (CStr(body, "short_code") is string rawCode)
            {
                var code = CannedCode(rawCode);
                if (code.Length == 0) return CannedBad("short_code cannot be empty.");
                row.ShortCode = code;
            }
            if (CStr(body, "content") is string rawContent)
            {
                var content = rawContent.Trim();
                if (content.Length == 0) return CannedBad("content cannot be empty.");
                row.Content = content;
            }
            if (CHas(body, "channels"))
            {
                var (channels, chErr) = ChatBranch.Data.Channels.TemplateChannels.Parse(CList(body, "channels"));
                if (chErr != null) return CannedBad(chErr);
                row.Channels = channels;
            }
            // entities [] / entity "" moves it back to organization-wide (everyone)
            var pick = await LibraryScope.ReadAsync(db, body);
            if (pick.Error != null) return pick.Error;
            if (pick.Given)
            {
                var before = LibraryEntities.Ids(db, LibraryKinds.Canned, row.Id, row.EntityId);
                if (await LibraryScope.Check(db, sc, me, before, pick.Ids) is { } denied) return denied;
                LibraryEntities.Set(db, row, pick.Ids);
            }

            if (await db.CannedResponses.AnyAsync(c => c.Id != row.Id && c.ShortCode == row.ShortCode
                                                       && c.EntityId == row.EntityId))
                return Results.Json(new { detail = "duplicate short_code" }, statusCode: 409);

            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await db.Entry(row).Reference(c => c.Entity).LoadAsync();
            await CannedAudit(db, me, ctx, "canned.update", row);
            return Results.Ok(CannedPayload(row, null, LibraryEntities.Load(db, new[] { row })));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/messaging/canned/{id:int}", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (!await CanManageTemplates(db, me)) return CannedForbidden();
            var row = await db.CannedResponses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (row != null && LibraryScope.CheckExisting(db, sc, me,
                    LibraryEntities.Ids(db, LibraryKinds.Canned, row.Id, row.EntityId)) is { } locked) return locked;
            await db.CannedResponses.Where(c => c.Id == id).ExecuteDeleteAsync();
            LibraryEntities.Clear(db, LibraryKinds.Canned, id);
            await db.SaveChangesAsync();
            if (row != null) await CannedAudit(db, me, ctx, "canned.delete", row);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    /// <summary>What the inbox needs to name and pick a sending account — no ids of
    /// the provider, no masked credentials, no delivery config.</summary>
    private static readonly string[] PickerKeys =
        { "id", "name", "channel", "provider", "role", "entity", "entity_name", "health_status", "configured", "env_managed" };

    private static object PickerShape(object full)
    {
        var el = JsonSerializer.SerializeToElement(full);
        return PickerKeys.Where(k => el.TryGetProperty(k, out _))
            .ToDictionary(k => k, k => el.GetProperty(k).Clone());
    }

    // ---- canned-response helpers ----------------------------------------------

    private static async Task<bool> CanManageTemplates(AppDbContext db, ClaimsPrincipal me) =>
        await AdminEndpoints.HasCapabilityAsync(db, me.FindFirstValue(ClaimTypes.Role) ?? "",
            "templates.manage");

    private static IResult CannedForbidden() =>
        Results.Json(new { detail = "You need the \"Manage message templates\" permission." }, statusCode: 403);

    private static IResult CannedBad(string detail) => Results.Json(new { detail }, statusCode: 400);

    /// <summary>What agents type after "/": trimmed, without a leading slash.</summary>
    private static string CannedCode(string? raw) => (raw ?? "").Trim().TrimStart('/').Trim();

    private static bool CHas(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out _);

    /// <summary>String value of a key; null when absent, "" when JSON null.</summary>
    private static string? CStr(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => v.ToString(),
            }
            : null;

    /// <summary>A string array (or one comma-separated string) as a list.</summary>
    private static List<string?> CList(JsonElement b, string key)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(key, out var v)) return new();
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())
                .ToList();
        if (v.ValueKind == JsonValueKind.String)
            return (v.GetString() ?? "").Split(',').Select(x => (string?)x).ToList();
        return new();
    }

    private static async Task CannedAudit(AppDbContext db, ClaimsPrincipal me, HttpContext ctx,
        string action, CannedResponse row)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system",
                Action = action,
                Target = $"/{row.ShortCode}",
                Detail = row.Channels.Count == 0 ? "all channels" : string.Join(", ", row.Channels),
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch { /* the trail never fails the write it records */ }
    }

    // ======================= payload builders ================================

    /// <summary>messaging/views._canned_payload — snake_case, `rendered` last.</summary>
    private static object CannedPayload(CannedResponse c, Dictionary<string, string>? variables,
        Dictionary<int, List<EntityRef>> scopes) => new
    {
        id = c.Id,
        short_code = c.ShortCode,
        content = c.Content,
        // [] = offered on every channel; else the thread channels it is limited to
        channels = c.Channels,
        created_by = c.CreatedBy,
        entity_id = c.EntityId,
        entity = c.Entity?.Slug,
        entity_name = c.Entity?.Name ?? "",
        // "Available to": every entity it was ticked for (primary first); global = everyone
        entities = LibraryEntities.Payload(scopes, c.Id),
        global = c.EntityId == null,
        created_at = DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc),
        updated_at = DateTime.SpecifyKind(c.UpdatedAt ?? c.CreatedAt, DateTimeKind.Utc),

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
        var allAccounts = await db.ChannelAccounts.AsNoTracking().ToListAsync();
        var waAccounts = allAccounts.Where(a => a.Channel == Channels.WhatsApp).ToList();
        var byId = waAccounts.ToDictionary(a => a.Id);
        // the AI verdict needs the conversation's OWN inbox on any channel: its
        // master switch and never-AI tags live in ChannelAccount.Config, and the
        // WhatsApp lookup above would hand a widget thread the fallback number
        var everyById = allAccounts.ToDictionary(a => a.Id);
        var configured = waAccounts.Where(WhatsAppSettingsEndpoints.IsConfigured).ToList();
        var envConfigured = WhatsAppSettingsEndpoints.EnvAccountConfigured();
        // .env contributes one more configured account to that count, but has no
        // row to fall back TO — so it can only make the fallback ambiguous.
        var fallback = configured.Count == 1 && !envConfigured ? configured[0] : null;
        var envIsSoleFallback = configured.Count == 0 && envConfigured;

        // Entity-scoped default for the chatbot tick on reminder-role numbers —
        // resolved for each thread's OWN entity, exactly as the AI gate does.
        var reminderDefaults = new Dictionary<int, bool>();
        bool ReminderDefaultOn(int? eid)
        {
            var k = eid ?? 0;
            if (!reminderDefaults.TryGetValue(k, out var on))
                reminderDefaults[k] = on = ChatBranch.Data.Ai.AiEligibility.ReminderDefaultOn(sc, eid);
            return on;
        }

        var aiRule = new ChatBranch.Data.Ai.AiControls(db, sc); // master switch / contact mode, cached per entity
        var outv = new List<object>();
        foreach (var t in threads)
        {
            var stat = stats.GetValueOrDefault(t.Id);
            var account = t.AccountId is int aid && byId.TryGetValue(aid, out var own)
                ? own : fallback;
            var aiVerdict = aiRule.Decide(t,
                t.AccountId is int ownId && everyById.TryGetValue(ownId, out var ownInbox) ? ownInbox : account);
            // Baileys and WAHA are normal WhatsApp accounts over WhatsApp Web — Meta's
            // 24h contact-service window does not apply to them. A sandbox thread
            // is always open too: nothing real is sent, and the demo flow must
            // never dead-end on a closed window.
            var live = account != null
                ? WhatsAppSettingsEndpoints.IsConfigured(account) : envIsSoleFallback;
            var baileys = account != null
                && ChatBranch.Data.Channels.WhatsAppProviders.IsUnofficial(account);

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
                // the conversation's identity: one per customer PER COMPANY
                thread_id = t.Id,
                company = MessagingEndpoints.ThreadCompanyPayload(db, t),
                phone = t.Address != "" ? t.Address : $"+{t.Key}",
                name = t.Name != "" ? t.Name : $"+{t.Key}",
                // a group conversation (Channels/GroupChats.cs): several people, no contact
                is_group = ChatBranch.Data.Channels.GroupChats.IsGroup(t),
                channel = t.Channel,
                status = t.Status,
                account_id = t.AccountId?.ToString(),
                account_name = t.Account?.Name ?? "",
                provider = account != null ? ChatBranch.Data.Channels.WhatsAppProviders.Of(account) : Providers.Cloud,
                ai_enabled = aiVerdict.Enabled,
                ai_reason = aiVerdict.Reason,
                handover = AiControlsEndpoints.HandoverPayload(t),
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

    /// <summary>
    /// <c>?entity=</c> → one entity id (null = every entity). A comma list (the
    /// dashboard's multi-select) resolves to its first pick the caller may see; the
    /// scoping helpers widen that id to every pick (<see cref="EntitySelection"/>).
    /// </summary>
    internal static async Task<int?> EntityId(AppDbContext db, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "all" or "__all__") return null;
        if (slug.Contains(','))
        {
            if (EntitySelection.Current is { Count: > 0 } picks) return picks[0];
            slug = slug.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(s => s is not ("all" or "__all__"));
            if (string.IsNullOrEmpty(slug)) return null;
        }
        var ent = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Slug == slug);
        return ent?.Id;
    }
}
