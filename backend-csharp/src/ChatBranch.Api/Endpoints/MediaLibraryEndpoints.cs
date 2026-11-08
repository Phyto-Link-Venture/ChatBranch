using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The media library — a file uploaded ONCE and sent by its id afterwards.
///
/// <c>media_url</c> covers a file that is online and <c>source_message_id</c> one
/// ChatBranch already has; this is the third case, and the one that was impossible:
/// a photo or a scan that exists only on somebody's phone. They drop it in here, and
/// from then on sending it costs a few hundred characters
/// (<c>{"file_id": "f_8231"}</c>) instead of a megabyte of base64 the AI has to spell
/// out character by character.
///
/// <code>
/// POST   /api/media/library            upload (multipart)              chats.send
/// GET    /api/media/library            recent uploads, searchable      chats.view
/// GET    /api/media/library/{id}/file  the file itself                 chats.view
/// DELETE /api/media/library/{id}       forget one                      chats.send
/// POST   /api/media/library/upload-url a 15-minute upload link         chats.send
/// PUT    /api/media/library/u/{token}  that link's own upload          (the token IS the credential)
/// </code>
///
/// An upload belongs to an outlet (the uploader's, when they are bound to one), so a
/// branch's files are not another branch's to send. It is kept
/// <see cref="MediaLibraryItem.RetentionDays"/> days; the sweep runs on upload and on
/// listing, which is often enough for something measured in days and costs no worker.
/// </summary>
public static class MediaLibraryEndpoints
{
    /// <summary>The upload cap — the largest any channel accepts.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>How long a presigned upload link is good for.</summary>
    public static readonly TimeSpan UploadWindow = TimeSpan.FromMinutes(15);

    /// <summary>Per process: a restart voids every outstanding upload link.</summary>
    private static readonly byte[] UploadKey = RandomNumberGenerator.GetBytes(32);

    public static void MapMediaLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- upload ---------------------------------------------------------
        app.MapPost("/api/media/library", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            MediaStore media, ClaimsPrincipal me) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Problem(400, "no_file", "file is required (multipart/form-data).");
            var form = await ctx.Request.ReadFormAsync();
            var upload = form.Files["file"];
            if (upload == null || upload.Length == 0)
                return Problem(400, "no_file", "file is required (multipart/form-data).");
            if (upload.Length > MaxBytes)
                return Problem(413, "too_large", $"That file is larger than {MaxBytes / (1024 * 1024)} MB.");

            var entity = await EntityForAsync(db, sc, me, form["entity"].ToString());
            var name = MessagingMedia.SafeName(upload.FileName, "file.bin");
            var row = await StoreAsync(db, media, upload.OpenReadStream(), name,
                upload.ContentType ?? "", await UserIdAsync(db, me), entity, ctx.RequestAborted);
            if (row == null) return Problem(413, "too_large", "That file is larger than this library allows.");

            AuditTrail.Add(db, "media.uploaded", row.FileId, $"{row.Kind} {row.Name} ({row.Size} bytes)");
            await Sweep(db, media);
            await db.SaveChangesAsync();
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send").DisableAntiforgery();

        // ---- list -----------------------------------------------------------
        app.MapGet("/api/media/library", async (string? q, int? limit, string? entity, HttpContext ctx,
            AppDbContext db, ScopingService sc, MediaStore media, ClaimsPrincipal me) =>
        {
            await Sweep(db, media);
            await db.SaveChangesAsync();

            var rows = Scoped(db.MediaLibrary.AsNoTracking(), db, sc, me);
            // the entity switcher narrows it like every other library page: that outlet's
            // uploads plus the organization-wide ones (never a sibling's)
            if (await InboxEndpoints.EntityId(db, entity) is int picked)
            {
                var subtree = sc.EntitySubtreeIds(new[] { picked });
                rows = rows.Where(m => m.EntityId == null || subtree.Contains(m.EntityId.Value));
            }
            var search = (q ?? "").Trim();
            if (search.Length > 0)
                rows = rows.Where(m => EF.Functions.Like(m.Name, $"%{search}%"));
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var list = await rows.OrderByDescending(m => m.Id).Take(take).ToListAsync();
            return Results.Ok(new { files = list.Select(Payload).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("chats.view", "chats.send");

        // ---- the file itself -------------------------------------------------
        app.MapGet("/api/media/library/{fileId}/file", async (string fileId, AppDbContext db,
            ScopingService sc, MediaStore media, ClaimsPrincipal me) =>
        {
            var row = await Scoped(db.MediaLibrary.AsNoTracking(), db, sc, me)
                .FirstOrDefaultAsync(m => m.FileId == fileId);
            if (row == null) return Results.NotFound(new { detail = "No such file." });
            var path = media.ResolveLibrary(row.Path);
            if (path == null) return Results.NotFound(new { detail = "That file is no longer on this server." });
            // a customer's file never renders inline in the dashboard's own origin
            return Results.File(path, row.Mime.Length > 0 ? row.Mime : "application/octet-stream",
                fileDownloadName: row.Name, enableRangeProcessing: true);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("chats.view", "chats.send");

        // ---- forget one ------------------------------------------------------
        app.MapDelete("/api/media/library/{fileId}", async (string fileId, AppDbContext db, ScopingService sc,
            MediaStore media, ClaimsPrincipal me) =>
        {
            var row = await Scoped(db.MediaLibrary, db, sc, me).FirstOrDefaultAsync(m => m.FileId == fileId);
            if (row == null) return Results.NotFound(new { detail = "No such file." });
            media.DeleteLibrary(row.Path);
            db.MediaLibrary.Remove(row);
            AuditTrail.Add(db, "media.deleted", row.FileId, row.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send");

        // ---- a link something else can upload to -----------------------------
        // For an agent that CAN reach the server but cannot send multipart through its
        // tool call: ask for a link, PUT the bytes at it. The token is signed, carries
        // the uploader and the outlet, and is good for fifteen minutes.
        app.MapPost("/api/media/library/upload-url", async (JsonElement body, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var name = MessagingMedia.SafeName(Str(body, "file_name") ?? "", "");
            if (name.Length == 0 || !Path.HasExtension(name))
                return Problem(422, "file_name_required",
                    "file_name is required and needs an extension (for example invoice.pdf).");
            var entity = await EntityForAsync(db, sc, me, Str(body, "entity") ?? "");
            var userId = await UserIdAsync(db, me);
            var fileId = NewFileId();
            var expires = DateTime.UtcNow.Add(UploadWindow);
            var token = MintUploadToken(fileId, name, userId, entity, expires);
            var baseUrl = MessagingAccountTransport.PublicBaseUrl(null, sc, ctx);
            return Results.Ok(new
            {
                file_id = fileId,
                upload_url = $"{baseUrl}/api/media/library/u/{token}",
                method = "PUT",
                expires_at = expires,
                max_bytes = MaxBytes,
                // what to do with it afterwards, so a caller needs no second document
                send_with = new { file_id = fileId },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send");

        // The presigned upload. Anonymous at the door — the signed token IS the
        // credential, and it names the file id, the uploader and the outlet, so nothing
        // about the upload can be changed by whoever holds the link.
        app.MapPut("/api/media/library/u/{token}", async (string token, HttpContext ctx, AppDbContext db,
            MediaStore media) =>
        {
            if (ReadUploadToken(token) is not { } claim)
                return Problem(403, "bad_upload_link", "That upload link is not valid or has expired.");
            if (await db.MediaLibrary.AnyAsync(m => m.FileId == claim.FileId))
                return Problem(409, "already_uploaded", "That upload link has already been used.");
            if ((ctx.Request.ContentLength ?? 0) > MaxBytes)
                return Problem(413, "too_large", $"That file is larger than {MaxBytes / (1024 * 1024)} MB.");

            var row = await StoreAsync(db, media, ctx.Request.Body, claim.Name,
                ctx.Request.ContentType ?? "", claim.UserId, claim.EntityId, ctx.RequestAborted,
                fileId: claim.FileId);
            if (row == null) return Problem(413, "too_large", "That file is larger than this library allows.");
            AuditTrail.Add(db, "media.uploaded", row.FileId,
                $"{row.Kind} {row.Name} ({row.Size} bytes, upload link)");
            await db.SaveChangesAsync();
            return Results.Ok(Payload(row));
        }).AllowAnonymous().DisableAntiforgery();
    }

    // =========================================================================
    // sending one
    // =========================================================================

    /// <summary>
    /// A library file as something to send, or null when the caller may not use it.
    /// <c>POST /api/messaging/send-file</c> reads it this way, which is why a branch's
    /// upload is not another branch's to send.
    /// </summary>
    public static async Task<MediaLibraryItem?> ForSendAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, string fileId) =>
        await Scoped(db.MediaLibrary.AsNoTracking(), db, sc, me)
            .FirstOrDefaultAsync(m => m.FileId == fileId);

    /// <summary>Note that a library file went out, so the library can show what is used.</summary>
    public static async Task MarkSentAsync(AppDbContext db, string fileId)
    {
        try
        {
            var now = DateTime.UtcNow;
            await db.MediaLibrary.Where(m => m.FileId == fileId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastSentAt, now));
        }
        catch { /* bookkeeping never costs a send */ }
    }

    // =========================================================================
    // helpers
    // =========================================================================

    /// <summary>
    /// What this caller may see: organization-wide uploads, plus the outlets they are
    /// bound to (unbound callers see everything, like every other library).
    /// </summary>
    private static IQueryable<MediaLibraryItem> Scoped(IQueryable<MediaLibraryItem> q, AppDbContext db,
        ScopingService sc, ClaimsPrincipal me)
    {
        var bound = ShellScope.BoundInheritedIds(db, sc, me);
        return bound == null ? q : q.Where(m => m.EntityId == null || bound.Contains(m.EntityId.Value));
    }

    /// <summary>
    /// Which outlet an upload belongs to: the one the request names (inside the
    /// caller's scope), else the caller's own when they are bound to exactly one, else
    /// organization-wide.
    /// </summary>
    private static async Task<int?> EntityForAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        string? named)
    {
        if (!string.IsNullOrWhiteSpace(named))
        {
            var asked = await InboxEndpoints.EntityId(db, named.Trim());
            var bound = ShellScope.BoundEntityIds(db, sc, me);
            if (asked is int e && (bound == null || bound.Contains(e))) return e;
            return null;
        }
        var roots = ShellScope.ScopeRoots(db, me);
        return roots.Count == 1 ? roots[0] : null;
    }

    private static async Task<MediaLibraryItem?> StoreAsync(AppDbContext db, MediaStore media, Stream source,
        string name, string contentType, int? userId, int? entityId, CancellationToken ct,
        string? fileId = null)
    {
        var mime = (contentType.Split(';')[0] ?? "").Trim().ToLowerInvariant();
        if (mime.Length == 0 || mime == "application/octet-stream")
            mime = MessagingMedia.ContentTypeFor(name);
        var path = await media.SaveLibraryAsync(source, MediaStore.ExtFor(mime, name), MaxBytes, ct);
        if (path == null) return null;
        var full = media.ResolveLibrary(path);
        var row = new MediaLibraryItem
        {
            FileId = fileId ?? NewFileId(),
            Name = name,
            Mime = mime,
            Kind = MediaStore.TypeForMime(mime) is var k && k != "document" ? k : MessagingMedia.TypeFor(name),
            Size = full != null ? new FileInfo(full).Length : 0,
            Path = path,
            UserId = userId,
            EntityId = entityId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(MediaLibraryItem.RetentionDays),
        };
        db.MediaLibrary.Add(row);
        return row;
    }

    /// <summary>
    /// Drop what is past its keep-by date, bytes and row together. Run on upload and on
    /// listing: a retention measured in days does not need a worker of its own.
    /// </summary>
    private static async Task Sweep(AppDbContext db, MediaStore media)
    {
        try
        {
            var now = DateTime.UtcNow;
            var stale = await db.MediaLibrary.Where(m => m.ExpiresAt <= now).Take(100).ToListAsync();
            if (stale.Count == 0) return;
            foreach (var row in stale) media.DeleteLibrary(row.Path);
            db.MediaLibrary.RemoveRange(stale);
        }
        catch { /* housekeeping never breaks the request it rode in on */ }
    }

    private static object Payload(MediaLibraryItem m) => new
    {
        file_id = m.FileId,
        name = m.Name,
        mime = m.Mime,
        kind = m.Kind,
        size = m.Size,
        created_at = m.CreatedAt,
        expires_at = m.ExpiresAt,
        last_sent_at = m.LastSentAt,
        entity_id = m.EntityId,
        url = $"/api/media/library/{m.FileId}/file",
    };

    private static string NewFileId() =>
        "f_" + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 16);

    // ---- the presigned upload token -----------------------------------------

    private sealed record UploadClaim(string FileId, string Name, int? UserId, int? EntityId);

    private static string MintUploadToken(string fileId, string name, int? userId, int? entityId,
        DateTime expires)
    {
        var payload = $"{fileId}.{B64(name)}.{userId?.ToString() ?? ""}.{entityId?.ToString() ?? ""}"
                      + $".{new DateTimeOffset(expires).ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    private static UploadClaim? ReadUploadToken(string token)
    {
        var parts = (token ?? "").Split('.');
        if (parts.Length != 6) return null;
        var payload = string.Join('.', parts[..5]);
        var expected = Encoding.ASCII.GetBytes(Sign(payload));
        var actual = Encoding.ASCII.GetBytes(parts[5]);
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            return null;
        if (!long.TryParse(parts[4], out var unix)) return null;
        if (DateTimeOffset.FromUnixTimeSeconds(unix) <= DateTimeOffset.UtcNow) return null;
        var name = FromB64(parts[1]);
        if (name.Length == 0) return null;
        return new UploadClaim(parts[0], name,
            int.TryParse(parts[2], out var uid) ? uid : null,
            int.TryParse(parts[3], out var eid) ? eid : null);
    }

    private static string Sign(string payload) =>
        Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(UploadKey,
            Encoding.UTF8.GetBytes(payload))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string B64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FromB64(string value)
    {
        try
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            return Encoding.UTF8.GetString(Convert.FromBase64String(
                s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')));
        }
        catch
        {
            return "";
        }
    }

    private static async Task<int?> UserIdAsync(AppDbContext db, ClaimsPrincipal me)
    {
        var username = me.Identity?.Name ?? "";
        if (username.Length == 0) return null;
        try
        {
            return await db.Users.AsNoTracking().Where(u => u.Username == username)
                .Select(u => (int?)u.Id).FirstOrDefaultAsync();
        }
        catch { return null; }
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Json(new { detail, code }, statusCode: status);

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;
}
