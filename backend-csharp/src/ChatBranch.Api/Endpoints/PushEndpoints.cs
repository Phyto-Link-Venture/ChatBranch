using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Workers;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Notifications on a device, when ChatBranch is not open.
///
/// <code>
/// GET    /api/push/config          → {supported, web, app, vapid_public_key}
/// GET    /api/push/devices         → the caller's own devices
/// POST   /api/push/devices         {platform, endpoint, p256dh?, auth?, label?} → register / refresh
/// DELETE /api/push/devices/{id}    → forget one of the caller's own devices
/// POST   /api/push/test            → send a test alert to the caller's own devices
/// POST   /api/push/tick            → run a delivery pass now (notifications.manage)
/// </code>
///
/// A device is the caller's own thing, like their preferences and their bell: these
/// routes carry no capability and are scoped to the signed-in person, and one login
/// can never see or delete another's device. Registration is idempotent on the
/// endpoint (a browser hands back the same subscription, and the phone app the same
/// token), so re-granting permission refreshes a row instead of adding another.
///
/// Browsers need no account anywhere: the VAPID key pair is generated and kept by
/// this deployment (<see cref="PushSettings"/>). The phone app needs a Firebase
/// service account, and `config` says plainly whether it has one.
/// </summary>
public static class PushEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/push/config", (AppDbContext db, ScopingService sc) =>
        {
            var vapid = PushSettings.GetOrCreateVapid(db, sc);
            var app_ = PushSettings.GetFirebase(sc) != null;
            return Results.Ok(new
            {
                supported = vapid != null || app_,
                web = vapid != null,
                app = app_,
                vapid_public_key = vapid?.PublicKeyB64 ?? "",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapGet("/api/push/devices", async (AppDbContext db, ClaimsPrincipal me) =>
        {
            if (await UserId(db, me) is not int uid) return Results.Ok(new { devices = Array.Empty<object>() });
            var rows = await db.PushDevices.AsNoTracking().Where(d => d.UserId == uid)
                .OrderByDescending(d => d.Id).ToListAsync();
            return Results.Ok(new { devices = rows.Select(Payload).ToList() });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/push/devices", async (JsonElement body, AppDbContext db, ClaimsPrincipal me) =>
        {
            if (await UserId(db, me) is not int uid)
                return Results.Json(new { detail = "No login to attach this device to." }, statusCode: 400);

            var platform = (Str(body, "platform") ?? PushPlatforms.Web).Trim().ToLowerInvariant();
            if (!PushPlatforms.IsKnown(platform))
                return Results.Json(new
                {
                    detail = $"platform must be one of {string.Join(", ", PushPlatforms.All)}.",
                }, statusCode: 400);

            var endpoint = (Str(body, "endpoint") ?? "").Trim();
            if (endpoint.Length is 0 or > 1000)
                return Results.Json(new
                {
                    detail = "endpoint is required — the browser's subscription endpoint, or the app's device token.",
                }, statusCode: 400);

            var p256dh = (Str(body, "p256dh") ?? "").Trim();
            var auth = (Str(body, "auth") ?? "").Trim();
            if (platform == PushPlatforms.Web)
            {
                // Web Push cannot deliver anything without the browser's own keys
                if (p256dh.Length == 0 || auth.Length == 0)
                    return Results.Json(new
                    {
                        detail = "p256dh and auth are required for a browser subscription.",
                    }, statusCode: 400);
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                    return Results.Json(new
                    {
                        detail = "endpoint must be the https URL the browser's push service gave.",
                    }, statusCode: 400);
            }

            // the same device registering again refreshes its row, whoever owns it:
            // a shared browser profile moves with the person now signed in
            var row = await db.PushDevices.FirstOrDefaultAsync(d => d.Endpoint == endpoint);
            if (row == null)
            {
                row = new PushDevice { Endpoint = endpoint, CreatedAt = DateTime.UtcNow };
                db.PushDevices.Add(row);
            }
            row.UserId = uid;
            row.Platform = platform;
            row.P256dh = Cap(p256dh, 200);
            row.Auth = Cap(auth, 100);
            row.Label = Cap((Str(body, "label") ?? "").Trim(), 80);
            row.Failures = 0;
            row.LastError = "";
            await db.SaveChangesAsync();
            return Results.Ok(Payload(row));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/push/devices/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal me) =>
        {
            if (await UserId(db, me) is not int uid) return Results.NotFound(new { detail = "No such device." });
            // another login's device is a 404, never someone else's to remove
            var removed = await db.PushDevices.Where(d => d.Id == id && d.UserId == uid).ExecuteDeleteAsync();
            return removed > 0
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new { detail = "No such device." });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/push/test", async (AppDbContext db, ClaimsPrincipal me, IPushSender sender) =>
        {
            if (await UserId(db, me) is not int uid)
                return Results.Json(new { detail = "No login to test." }, statusCode: 400);
            var devices = await db.PushDevices.Where(d => d.UserId == uid).ToListAsync();
            if (devices.Count == 0)
                return Results.Json(new { detail = "No device is registered for this login yet." }, statusCode: 400);

            var message = new PushMessage("ChatBranch", "Notifications are working on this device.",
                "/notifications", "", NotificationCategories.System);
            var sent = 0;
            var problems = new List<string>();
            foreach (var device in devices)
            {
                var r = await sender.SendAsync(device, message);
                if (r.Outcome == PushOutcome.Sent) { sent++; device.LastPushAt = DateTime.UtcNow; device.Failures = 0; device.LastError = ""; }
                else
                {
                    problems.Add(r.Detail.Length > 0 ? r.Detail : r.Outcome.ToString());
                    if (r.Outcome == PushOutcome.Gone) db.PushDevices.Remove(device);
                    else device.LastError = Cap(r.Detail, 300);
                }
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = sent > 0, sent, problems });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // The worker's manual seam: run a delivery pass now. Org-level maintenance,
        // like the other ticks — and what the test host drives instead of the worker.
        app.MapPost("/api/push/tick", async (HttpContext ctx) =>
        {
            var (notifications, delivered) = await PushWorker.RunAsync(ctx.RequestServices);
            return Results.Ok(new { ok = true, notifications, delivered });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("notifications.manage");
    }

    private static object Payload(PushDevice d) => new
    {
        id = d.Id,
        platform = d.Platform,
        label = d.Label,
        created_at = d.CreatedAt,
        last_push_at = d.LastPushAt,
        last_error = d.LastError,
        // the browser compares this to the subscription it holds, to know it is the
        // same device without ever showing the endpoint
        endpoint_tail = d.Endpoint.Length > 12 ? d.Endpoint[^12..] : d.Endpoint,
    };

    private static async Task<int?> UserId(AppDbContext db, ClaimsPrincipal me)
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

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    private static string Cap(string s, int max) => s.Length > max ? s[..max] : s;
}
