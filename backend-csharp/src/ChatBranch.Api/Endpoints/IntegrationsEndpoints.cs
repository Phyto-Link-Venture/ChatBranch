using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Integrations surface (/api/integrations/*). Sandbox is MANDATORY: with zero
/// credentials, list still answers, connect returns an operator-facing reason
/// instead of opening a socket, and actions return {ok:false, skipped:true} so a
/// flow step logs a clean skip and the run continues. Webhooks verify HMAC and
/// return 503 when no secret is configured — never "accept unsigned".
/// </summary>
public static class IntegrationsEndpoints
{
    public static readonly string[] Providers =
        { "google_calendar", "google_drive", "gmail", "google_sheets", "shopify", "stripe" };

    // Contract shared with frontend lib/flow/nodeConfig.ts INTEGRATION_ACTIONS.
    public static readonly string[] Actions =
    {
        "Create calendar event", "Find free slot", "Upload file to Drive",
        "Append row to Sheet", "Send via Gmail", "Update order status",
        "Create Stripe payment link",
    };

    public record ConnectRequest(string? Entity, Dictionary<string, object?>? Credentials);
    public record ActionRequest(string Provider, string Action, Dictionary<string, object?>? Args);

    public static void MapIntegrationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/integrations", async (string? entity, AppDbContext db, ScopingService sc) =>
        {
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var connected = sc.ScopeInherited(db.Integrations.AsNoTracking(), entityId).ToList();
            var cards = Providers.Select(p =>
            {
                var row = connected.FirstOrDefault(i => i.Provider == p);
                return new
                {
                    provider = p,
                    status = row?.Status ?? "unknown",
                    statusDetail = row?.StatusDetail ?? "",
                    // credentials are NEVER returned unmasked
                    configured = row != null && row.Credentials.Count > 0,
                };
            });
            return Results.Ok(new { integrations = cards, actions = Actions });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Connect: superadmin. Sandbox → 400 with a reason, no socket opened.
        app.MapPost("/api/integrations/{provider}/connect",
            async (string provider, ConnectRequest body, AppDbContext db) =>
        {
            if (!Providers.Contains(provider))
                return Results.Json(new { detail = "unknown provider" }, statusCode: 400);
            var creds = body.Credentials ?? new();
            if (creds.Count == 0)
                return Results.Json(new
                {
                    ok = false,
                    detail = $"No credentials supplied for {provider}. Add them in Settings → " +
                             "Integrations before connecting (sandbox mode).",
                }, statusCode: 400);

            var entityId = await InboxEndpoints.EntityId(db, body.Entity);
            var row = await db.Integrations.FirstOrDefaultAsync(i => i.Provider == provider && i.EntityId == entityId)
                      ?? new Integration { Provider = provider, EntityId = entityId, CreatedAt = DateTime.UtcNow };
            if (row.Id == 0) db.Integrations.Add(row);
            row.Credentials = creds;
            row.Status = "connected";
            row.StatusDetail = "";
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { provider, status = row.Status });
        }).RequireAuthorization(AuthPolicies.SuperAdmin);

        // Flow-builder action dispatch — sandbox returns a clean skip.
        app.MapPost("/api/integrations/action", async (ActionRequest body, AppDbContext db) =>
        {
            var row = await db.Integrations.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Provider == body.Provider);
            if (row == null || row.Credentials.Count == 0)
                return Results.Ok(new { ok = false, skipped = true,
                    detail = $"{body.Provider} not connected — action '{body.Action}' skipped (sandbox)." });
            // A real dispatch would run here; connected-but-unimplemented also skips cleanly.
            return Results.Ok(new { ok = false, skipped = true,
                detail = $"action '{body.Action}' not yet implemented in the C# port" });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- webhooks: verify HMAC; no secret ⇒ 503 (never accept unsigned) ----
        app.MapPost("/api/integrations/webhooks/shopify",
            async (HttpContext ctx, AppDbContext db, ScopingService sc) =>
        {
            var secret = sc.SettingValue("shopify_webhook_secret");
            if (string.IsNullOrEmpty(secret))
                return Results.Json(new { detail = "webhook secret not configured" }, statusCode: 503);
            var raw = await ReadBody(ctx);
            var hmac = ctx.Request.Headers["X-Shopify-Hmac-Sha256"].ToString();
            if (!WebhookVerify.Shopify(secret, raw, hmac))
                return Results.Json(new { detail = "invalid signature" }, statusCode: 401);
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        app.MapPost("/api/integrations/webhooks/stripe",
            async (HttpContext ctx, AppDbContext db, ScopingService sc) =>
        {
            var secret = sc.SettingValue("stripe_webhook_secret");
            if (string.IsNullOrEmpty(secret))
                return Results.Json(new { detail = "webhook secret not configured" }, statusCode: 503);
            var raw = await ReadBody(ctx);
            var sig = ctx.Request.Headers["Stripe-Signature"].ToString();
            if (!WebhookVerify.Stripe(secret, raw, sig))
                return Results.Json(new { detail = "invalid signature" }, statusCode: 401);
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();
    }

    private static async Task<byte[]> ReadBody(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}
