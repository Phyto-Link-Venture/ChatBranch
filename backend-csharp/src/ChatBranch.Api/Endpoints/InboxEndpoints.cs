using System.Text;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
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
    public record SendRequest(string Text, bool Force = false, int? AccountId = null);
    public record SimulateRequest(string Channel, string Key, string Text, string? Name = null);
    public record CannedWrite(string ShortCode, string Content, string? Entity = null);

    public static void MapInboxEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- accounts ----------------------------------------------------------
        app.MapGet("/api/whatsapp/accounts", async (string? channel, AppDbContext db) =>
        {
            var q = db.ChannelAccounts.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(channel)) q = q.Where(a => a.Channel == channel);
            var rows = await q.OrderBy(a => a.CreatedAt).ToListAsync();
            return Results.Ok(rows.Select(Dtos.AccountDto));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- threads -----------------------------------------------------------
        app.MapGet("/api/whatsapp/threads", async (string? channel, string? entity, string? status,
            AppDbContext db, ThreadStore store) =>
        {
            var entityId = await EntityId(db, entity);
            var rows = store.ListThreads(channel, entityId, status);
            return Results.Ok(new { threads = rows.Select(Dtos.ThreadDto) });
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
        app.MapPost("/api/whatsapp/simulate", (SimulateRequest body, ThreadStore store,
            ChatBranch.Data.Flows.FlowEngine engine) =>
        {
            var thread = store.GetOrCreateThread(body.Channel, body.Key, body.Name);
            var msg = store.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
                body.Text, status: "received");
            // event axis: resume any flow runs parked on a reply from this contact
            engine.OnInboundMessage(body.Channel, body.Key, body.Text);
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
        app.MapGet("/api/messaging/canned", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await EntityId(db, entity);
            var rows = sc.ScopeInherited(db.CannedResponses.AsNoTracking(), entityId)
                .OrderBy(c => c.ShortCode).ToList();
            return Results.Ok(new { canned = rows.Select(Dtos.CannedDto) });
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
            return Results.Ok(Dtos.CannedDto(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapMethods("/api/messaging/canned/{id:int}", new[] { "PATCH" },
            async (int id, CannedWrite body, AppDbContext db) =>
        {
            var row = await db.CannedResponses.FindAsync(id);
            if (row == null) return Results.NotFound();
            row.Content = body.Content;
            if (!string.IsNullOrEmpty(body.ShortCode)) row.ShortCode = body.ShortCode;
            await db.SaveChangesAsync();
            return Results.Ok(Dtos.CannedDto(row));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapDelete("/api/messaging/canned/{id:int}", async (int id, AppDbContext db) =>
        {
            await db.CannedResponses.Where(c => c.Id == id).ExecuteDeleteAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    internal static async Task<int?> EntityId(AppDbContext db, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "all" or "__all__") return null;
        var ent = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Slug == slug);
        return ent?.Id;
    }
}
