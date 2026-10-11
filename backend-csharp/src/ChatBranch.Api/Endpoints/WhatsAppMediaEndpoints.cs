using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Auth-gated file serving for the inbox (port of the media + recording views):
///
///   * <c>GET /api/whatsapp/media/{id}</c> and <c>GET /api/messaging/media/{id}</c>
///     — the attachment of message <c>id</c> (the URL every message payload
///     advertises). One implementation; both are scoped like the inbox: the
///     caller needs <c>chats.view</c> and the conversation must be inside their
///     entity scope (an unassigned conversation is visible like in the list).
///     Unknown, out-of-scope or file-less → 404 (no existence oracle).
///   * <c>GET /api/media/s/{token}</c> — the same attachment through a short-lived
///     SIGNED link (<see cref="MediaStreamLinks"/>; the payload's <c>stream_url</c>),
///     anonymous at the door so a plain <c>&lt;audio src&gt;</c> / <c>&lt;video src&gt;</c>
///     can stream it with Range; on open the person must still be active, hold
///     <c>chats.view</c> and see the conversation. <c>?download=1</c> = as an
///     attachment with the original name; <c>?format=m4a</c> = an AAC rendering of
///     Ogg/Opus/AMR audio when the server has ffmpeg (<see cref="AudioTranscoder"/>).
///     Bad token = 404, expired = 410.
///   * <c>GET /api/whatsapp/calls/recording/{name}</c> — a WhatsApp call's WAV
///     (gateway recording, or the agent's for the same room), scoped by the
///     conversation it belongs to.
///   * <c>GET /api/calls/recording/{call_id}</c> — the voice agent's recording of a
///     SIP call (<c>call_logs.view</c> + the call's contact in scope).
///
/// Range requests are honoured (audio seeking). Only image/audio/video/PDF/plain
/// text render inline — anything else (a customer's HTML or SVG "document") is
/// served as a download with <c>nosniff</c> + a sandbox CSP, so an attachment
/// can never run script in the API's origin.
/// </summary>
public static class WhatsAppMediaEndpoints
{
    public static void MapWhatsAppMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/whatsapp/media/{id:int}", ServeMessageMedia)
            .RequireAuthorization(AuthPolicies.Authenticated)
            .RequireCapability("chats.view");
        app.MapGet("/api/messaging/media/{id:int}", ServeMessageMedia)
            .RequireAuthorization(AuthPolicies.Authenticated)
            .RequireCapability("chats.view");

        // the signed link: the token IS the credential, the person is re-checked on open
        app.MapGet("/api/media/s/{token}", ServeSignedMedia).AllowAnonymous();

        app.MapGet("/api/whatsapp/calls/recording/{name}", async (string name, AppDbContext db,
            ScopingService sc, CallRecordings recordings, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var full = recordings.Resolve(name);
            if (full == null) return NotFound();
            // whose call: the gateway names files by wa_id; an agent recording by room
            var waId = CallRecordings.WaIdOf(name);
            Thread? thread = null;
            if (waId != null)
                thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(
                    t => t.Channel == Channels.WhatsApp && t.Key == waId);
            else
            {
                // an agent recording ({room}.wav): the live call's thread, else the
                // finished call's number (CallRecord, folded from calls.json)
                var room = Path.GetFileNameWithoutExtension(name);
                thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.CallRoom == room);
                if (thread == null)
                {
                    var phone = await db.CallRecords.AsNoTracking().Where(r => r.CallId == room)
                        .Select(r => r.Phone).FirstOrDefaultAsync() ?? "";
                    var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
                    if (digits.Length > 0)
                        thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(
                            t => t.Channel == Channels.WhatsApp && t.Key == digits);
                }
            }
            if (thread != null)
            {
                if (!await CanSeeThread(db, sc, me, thread)) return NotFound();
            }
            else if (!await ShellScope.HasCapAsync(db, me, "call_logs.view")
                     || !await CallInScope(db, sc, me, Path.GetFileNameWithoutExtension(name)))
                return NotFound();
            return Audio(ctx, full);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("chats.view", "call_logs.view");

        app.MapGet("/api/calls/recording/{callId}", async (string callId, AppDbContext db, ScopingService sc,
            CallRecordings recordings, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var full = recordings.ResolveAgentCall(callId);
            if (full == null) return NotFound();
            if (!await ShellScope.HasCapAsync(db, me, "call_logs.view") || !await CallInScope(db, sc, me, callId))
                return NotFound();
            return Audio(ctx, full);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("call_logs.view");
    }

    private static IResult NotFound() => Results.NotFound(new { detail = "Not found." });

    private static async Task<IResult> ServeMessageMedia(int id, AppDbContext db, ScopingService sc,
        MediaStore store, ClaimsPrincipal me, HttpContext ctx)
    {
        var msg = await db.Messages.AsNoTracking().Include(m => m.Thread)
            .FirstOrDefaultAsync(m => m.Id == id);
        if (msg?.Thread == null || string.IsNullOrEmpty(msg.MediaPath)) return NotFound();
        if (!await CanSeeThread(db, sc, me, msg.Thread)) return NotFound();
        var full = store.Resolve(msg.Thread.Channel, msg.MediaPath);
        if (full == null) return NotFound();
        return ServeFile(ctx, msg, full, download: false);
    }

    /// <summary>The file itself: inline when the type is safe to render from our origin,
    /// else (or on request) as an attachment with the original name; Range always.</summary>
    private static IResult ServeFile(HttpContext ctx, Message msg, string full, bool download)
    {
        var mime = MediaStore.MimeFor(full);
        Harden(ctx);
        return MediaStore.IsInlineSafe(mime) && !download
            ? Results.File(full, mime, enableRangeProcessing: true)
            : Results.File(full, MediaStore.IsInlineSafe(mime) ? mime : "application/octet-stream",
                fileDownloadName: MediaStreamLinks.DisplayName(msg), enableRangeProcessing: true);
    }

    private static async Task<IResult> ServeSignedMedia(string token, AppDbContext db, ScopingService sc,
        MediaStore store, HttpContext ctx)
    {
        var verdict = MediaStreamLinks.Parse(token, DateTimeOffset.UtcNow, out var messageId, out var profileId,
            out var expires, out var sig);
        if (verdict == MediaStreamLinks.Verdict.Malformed) return NotFound();
        var profile = await db.Profiles.AsNoTracking().Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == profileId);
        if (profile?.User == null
            || !MediaStreamLinks.SignatureOk(messageId, profileId, profile.User.Username, expires, sig))
            return NotFound();
        if (verdict == MediaStreamLinks.Verdict.Expired)
            return Results.Json(new { detail = "This media link has expired — reopen the conversation." },
                statusCode: StatusCodes.Status410Gone);
        if (!profile.User.IsActive) return NotFound();

        var msg = await db.Messages.AsNoTracking().Include(m => m.Thread)
            .FirstOrDefaultAsync(m => m.Id == messageId);
        if (msg?.Thread == null || string.IsNullOrEmpty(msg.MediaPath)) return NotFound();
        // the conversation is re-checked AS the person the link was made for
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, profile.User.Username),
            new(ClaimTypes.Role, profile.Role),
            new("profile_id", profile.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (profile.TenantId is int tid)
            claims.Add(new Claim("tenant_id", tid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var asUser = new ClaimsPrincipal(new ClaimsIdentity(claims, "media-link"));
        if (!await CanSeeThread(db, sc, asUser, msg.Thread)) return NotFound();
        var full = store.Resolve(msg.Thread.Channel, msg.MediaPath);
        if (full == null) return NotFound();
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";

        var format = ctx.Request.Query["format"].ToString().Trim().ToLowerInvariant();
        if (format == "m4a")
        {
            var mime = msg.MediaMime.Length > 0 ? msg.MediaMime : MediaStore.MimeFor(full);
            if (!AudioTranscoder.CanTranscode(mime) && !AudioTranscoder.CanTranscode(MediaStore.MimeFor(full)))
                return Results.Json(new { detail = "Only Ogg/Opus, AMR or WebM audio has an M4A version." },
                    statusCode: StatusCodes.Status400BadRequest);
            var m4a = await AudioTranscoder.ToM4aAsync(full, ctx.RequestAborted);
            if (m4a == null)
                return Results.Json(new { detail = "This server cannot convert audio (ffmpeg is not installed) — download the file instead." },
                    statusCode: StatusCodes.Status501NotImplemented);
            Harden(ctx);
            return Results.File(m4a, "audio/mp4", enableRangeProcessing: true);
        }
        var download = ctx.Request.Query["download"].ToString() is "1" or "true";
        return ServeFile(ctx, msg, full, download);
    }

    private static IResult Audio(HttpContext ctx, string full)
    {
        Harden(ctx);
        return Results.File(full, "audio/wav", enableRangeProcessing: true);
    }

    private static void Harden(HttpContext ctx)
    {
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
    }

    /// <summary>
    /// The inbox's read rule for one conversation: <c>chats.view</c>, and inside
    /// the caller's entity scope (unassigned conversations are always visible,
    /// exactly like <c>GET /api/whatsapp/threads</c>).
    /// </summary>
    internal static async Task<bool> CanSeeThread(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        Thread thread)
    {
        if (!await ShellScope.HasCapAsync(db, me, "chats.view")) return false;
        var ids = ShellScope.ThreadEntityIds(db, sc, me, null);
        return ids == null || thread.EntityId == null || ids.Contains(thread.EntityId.Value);
    }

    /// <summary>A recorded call's contact must be one the caller may see (no contact = org-level row).</summary>
    private static async Task<bool> CallInScope(AppDbContext db, ScopingService sc, ClaimsPrincipal me, string callId)
    {
        var record = await db.CallRecords.AsNoTracking().FirstOrDefaultAsync(r => r.CallId == callId);
        if (record?.ContactId is not int contactId) return true;
        return await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
            .AnyAsync(c => c.Id == contactId);
    }
}
