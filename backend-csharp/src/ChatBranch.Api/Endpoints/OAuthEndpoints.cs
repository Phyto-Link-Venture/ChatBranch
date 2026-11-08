using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Connecting an AI app to this ChatBranch by SIGNING IN — the second way in, next to a
/// personal API key.
///
/// A key asks a non-developer to create one, copy a secret out of a one-time dialog and
/// paste it into the app's settings without losing a character. This asks them to press
/// Allow. It is OAuth 2.1 with PKCE and dynamic client registration, which is exactly
/// what MCP clients (Claude among them) already look for, so there is nothing to
/// configure on either side: paste <c>&lt;your-chatbranch&gt;/api/mcp</c> into the app and
/// it discovers the rest.
///
/// <code>
/// GET  /.well-known/oauth-protected-resource[/api/mcp]  which authorization server to use
/// GET  /.well-known/oauth-authorization-server          where to register, authorize, get tokens
/// POST /api/oauth/register                              the app registers ITSELF (RFC 7591)
/// GET  /api/oauth/authorize                             → the dashboard's Allow page
/// GET  /api/oauth/authorize/{request}                   what that page shows   (session)
/// POST /api/oauth/authorize/{request}                   Allow / Deny           (session)
/// POST /api/oauth/token                                 code → tokens, refresh → tokens
/// POST /api/oauth/revoke                                the app gives a token back
/// GET  /api/oauth/connections                           the caller's connected apps (session)
/// DELETE /api/oauth/connections/{id}                    disconnect one         (session)
/// </code>
///
/// The flow's security rests on four things, and none of them is the client being
/// trustworthy: the redirect URI must match what the client registered, EXACTLY; the
/// code is single-use, five minutes long and bound to that client; PKCE S256 is
/// required, so a stolen code cannot be redeemed by whoever stole it; and the person's
/// own session is what authorizes it — this endpoint never sees a password.
/// </summary>
public static class OAuthEndpoints
{
    /// <summary>The MCP endpoint these tokens are for.</summary>
    public const string McpPath = "/api/mcp";

    /// <summary>Where the dashboard shows the Allow page.</summary>
    public const string ConsentPath = "/connect";

    public static void MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- discovery ------------------------------------------------------
        // An MCP client that gets a 401 reads these two documents and knows what to do.
        // Anonymous by necessity: they are what a client reads BEFORE it has anything.
        var protectedResource = (HttpContext ctx, ScopingService sc) =>
        {
            var baseUrl = Origin(ctx, sc);
            return Results.Ok(new Dictionary<string, object?>
            {
                ["resource"] = baseUrl + McpPath,
                ["authorization_servers"] = new[] { baseUrl },
                ["scopes_supported"] = new[] { McpOAuth.ScopeFull, McpOAuth.ScopeRead },
                ["bearer_methods_supported"] = new[] { "header" },
                ["resource_documentation"] = baseUrl + "/developers",
            });
        };
        app.MapGet("/.well-known/oauth-protected-resource", protectedResource).AllowAnonymous();
        // clients also ask with the resource's own path appended (RFC 9728 §3.1)
        app.MapGet("/.well-known/oauth-protected-resource/{**path}", protectedResource).AllowAnonymous();

        var authServer = (HttpContext ctx, ScopingService sc) =>
        {
            var baseUrl = Origin(ctx, sc);
            return Results.Ok(new Dictionary<string, object?>
            {
                ["issuer"] = baseUrl,
                ["authorization_endpoint"] = baseUrl + "/api/oauth/authorize",
                ["token_endpoint"] = baseUrl + "/api/oauth/token",
                ["registration_endpoint"] = baseUrl + "/api/oauth/register",
                ["revocation_endpoint"] = baseUrl + "/api/oauth/revoke",
                ["scopes_supported"] = new[] { McpOAuth.ScopeFull, McpOAuth.ScopeRead },
                ["response_types_supported"] = new[] { "code" },
                ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
                // S256 only: "plain" proves nothing an interceptor could not also produce
                ["code_challenge_methods_supported"] = new[] { "S256" },
                ["token_endpoint_auth_methods_supported"] = new[] { "none" },
                ["service_documentation"] = baseUrl + "/developers",
            });
        };
        app.MapGet("/.well-known/oauth-authorization-server", authServer).AllowAnonymous();
        app.MapGet("/.well-known/oauth-authorization-server/{**path}", authServer).AllowAnonymous();

        // ---- the app registers itself (RFC 7591) ----------------------------
        // Open registration is the point: a client nobody configured can still ask a
        // PERSON for access, and a client id on its own can do nothing at all.
        app.MapPost("/api/oauth/register", async (JsonElement body, AppDbContext db) =>
        {
            var redirects = Strings(body, "redirect_uris")
                .Select(u => u.Trim()).Where(u => u.Length > 0).Distinct().ToList();
            if (redirects.Count == 0)
                return Error(400, "invalid_redirect_uri", "redirect_uris is required.");
            if (redirects.Count > 10)
                return Error(400, "invalid_redirect_uri", "At most 10 redirect_uris.");
            foreach (var uri in redirects)
                if (BadRedirect(uri) is { } why)
                    return Error(400, "invalid_redirect_uri", why);

            var name = Trim(Str(body, "client_name") ?? "", 120);
            if (name.Length == 0) name = "An AI app";
            var row = new OAuthClient
            {
                ClientId = McpOAuth.NewClientId(),
                Name = name,
                RedirectUris = redirects,
                Uri = Trim(Str(body, "client_uri") ?? "", 300),
                CreatedAt = DateTime.UtcNow,
            };
            db.OAuthClients.Add(row);
            await db.SaveChangesAsync();

            return Results.Json(new Dictionary<string, object?>
            {
                ["client_id"] = row.ClientId,
                ["client_id_issued_at"] = new DateTimeOffset(row.CreatedAt).ToUnixTimeSeconds(),
                ["client_name"] = row.Name,
                ["redirect_uris"] = row.RedirectUris,
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                // a public client: it holds no secret, which is why PKCE is required
                ["token_endpoint_auth_method"] = "none",
            }, statusCode: 201);
        }).AllowAnonymous();

        // ---- the person approves it -----------------------------------------
        // Anonymous because the browser arrives here before anything: the request is
        // stored and the person is sent to the dashboard, which signs them in if needed.
        app.MapGet("/api/oauth/authorize", async (HttpContext ctx, AppDbContext db, ScopingService sc) =>
        {
            var q = ctx.Request.Query;
            var clientId = (q["client_id"].ToString() ?? "").Trim();
            var redirectUri = (q["redirect_uri"].ToString() ?? "").Trim();
            var state = (q["state"].ToString() ?? "").Trim();
            var challenge = (q["code_challenge"].ToString() ?? "").Trim();
            var method = (q["code_challenge_method"].ToString() ?? "").Trim();
            var responseType = (q["response_type"].ToString() ?? "").Trim();

            var client = await db.OAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId);
            // Before the redirect URI is known-good, an error must NOT be redirected
            // anywhere (RFC 6749 §4.1.2.1) — that is how an open redirector is built.
            if (client == null)
                return Error(400, "invalid_client", "Unknown client_id. Register the app first.");
            if (!client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
                return Error(400, "invalid_request",
                    "redirect_uri does not match what this app registered.");

            // from here on the app gets its errors at its own redirect, as it should
            if (!string.Equals(responseType, "code", StringComparison.Ordinal))
                return Redirect(redirectUri, state, "unsupported_response_type", "Only response_type=code.");
            if (challenge.Length == 0)
                return Redirect(redirectUri, state, "invalid_request", "code_challenge is required (PKCE).");
            if (!string.Equals(method, "S256", StringComparison.OrdinalIgnoreCase))
                return Redirect(redirectUri, state, "invalid_request", "code_challenge_method must be S256.");

            var row = new OAuthAuthorization
            {
                RequestId = McpOAuth.NewRequestId(),
                ClientId = client.ClientId,
                RedirectUri = redirectUri,
                Scope = Trim(q["scope"].ToString() ?? "", 300),
                State = Trim(state, 500),
                CodeChallenge = Trim(challenge, 200),
                CodeChallengeMethod = "S256",
                Resource = Trim(q["resource"].ToString() ?? "", 300),
                ExpiresAt = DateTime.UtcNow.Add(McpOAuth.AuthorizationLifetime),
                CreatedAt = DateTime.UtcNow,
            };
            db.OAuthAuthorizations.Add(row);
            await db.SaveChangesAsync();

            // the dashboard signs them in (if they are not) and shows what is being asked
            var dashboard = Dashboard(ctx, sc);
            return Results.Redirect($"{dashboard}{ConsentPath}?request={row.RequestId}");
        }).AllowAnonymous();

        // What the Allow page shows. Session-only: this is the person's own decision,
        // so a connected app can never read (or answer) another one's request.
        app.MapGet("/api/oauth/authorize/{request}", async (string request, AppDbContext db,
            ClaimsPrincipal me) =>
        {
            if (ApiKeyAuth.IsApiKey(me))
                return Error(403, "invalid_request", "Only a signed-in person can approve a connection.");
            var row = await db.OAuthAuthorizations.AsNoTracking()
                .FirstOrDefaultAsync(a => a.RequestId == request);
            if (row == null || row.UsedAt != null || row.ExpiresAt <= DateTime.UtcNow)
                return Error(404, "invalid_request", "That connection request has expired — start again in the app.");
            var client = await db.OAuthClients.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ClientId == row.ClientId);
            return Results.Ok(new
            {
                request = row.RequestId,
                client_name = client?.Name ?? "An AI app",
                client_uri = client?.Uri ?? "",
                // the app's own words, shown as a claim and never trusted as a fact
                redirect_host = Host(row.RedirectUri),
                scope = row.Scope,
                read_only = McpOAuth.WantsReadOnly(row.Scope),
                user = me.Identity?.Name ?? "",
                expires_at = row.ExpiresAt,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Allow / Deny. The person's session is the authorization — no password here.
        app.MapPost("/api/oauth/authorize/{request}", async (string request, JsonElement body,
            HttpContext ctx, AppDbContext db, ClaimsPrincipal me) =>
        {
            if (ApiKeyAuth.IsApiKey(me))
                return Error(403, "invalid_request", "Only a signed-in person can approve a connection.");
            var row = await db.OAuthAuthorizations.FirstOrDefaultAsync(a => a.RequestId == request);
            if (row == null || row.UsedAt != null || row.ExpiresAt <= DateTime.UtcNow)
                return Error(404, "invalid_request", "That connection request has expired — start again in the app.");

            var allow = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("allow", out var a)
                        && a.ValueKind != JsonValueKind.False
                        && !(a.ValueKind == JsonValueKind.String && a.GetString() is "false" or "0");
            if (!allow)
            {
                row.UsedAt = DateTime.UtcNow;   // spend it: Deny is final
                await db.SaveChangesAsync();
                return Results.Ok(new
                {
                    redirect = Location(row.RedirectUri, row.State, "access_denied",
                        "The person did not allow the connection."),
                    allowed = false,
                });
            }

            var username = me.Identity?.Name ?? "";
            var profile = await db.Profiles.Include(p => p.User)
                .FirstOrDefaultAsync(p => p.User!.Username == username);
            if (profile?.User == null)
                return Error(403, "invalid_request", "No login to connect the app to.");

            var readOnly = body.TryGetProperty("read_only", out var ro)
                ? ro.ValueKind == JsonValueKind.True
                  || (ro.ValueKind == JsonValueKind.String && ro.GetString() is "true" or "1")
                : McpOAuth.WantsReadOnly(row.Scope);

            var (code, hash) = McpOAuth.NewCode();
            row.UserId = profile.UserId;
            row.Scopes = await McpOAuth.ScopesForAsync(db, profile.Role, readOnly);
            row.CodeHash = hash;
            // the code is worth minutes, not the quarter hour the page had
            row.ExpiresAt = DateTime.UtcNow.Add(McpOAuth.CodeLifetime);
            AuditTrail.Add(db, "oauth.approved", row.ClientId,
                $"{(readOnly ? "read-only" : "full")} access for {profile.User.Username}");
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                redirect = Location(row.RedirectUri, row.State, code: code),
                allowed = true,
                read_only = readOnly,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- tokens ----------------------------------------------------------
        // Anonymous: a public client has no secret, which is what PKCE replaces.
        app.MapPost("/api/oauth/token", async (HttpContext ctx, AppDbContext db) =>
        {
            var form = await ReadFormAsync(ctx);
            var grant = Get(form, "grant_type");
            return grant switch
            {
                "authorization_code" => await CodeGrantAsync(db, form),
                "refresh_token" => await RefreshGrantAsync(db, form),
                _ => Error(400, "unsupported_grant_type",
                    "grant_type must be authorization_code or refresh_token."),
            };
        }).AllowAnonymous().DisableAntiforgery();

        // An app handing a token back (RFC 7009). Always 200, as the RFC says: whether
        // the token existed is not the caller's business.
        app.MapPost("/api/oauth/revoke", async (HttpContext ctx, AppDbContext db) =>
        {
            var form = await ReadFormAsync(ctx);
            var token = Get(form, "token");
            if (token.Length > 0)
            {
                var row = await McpOAuth.ResolveAccessAsync(db, token)
                          ?? await McpOAuth.ResolveRefreshAsync(db, token);
                if (row != null)
                {
                    var tracked = await db.OAuthTokens.FirstOrDefaultAsync(t => t.Id == row.Id);
                    if (tracked is { RevokedAt: null })
                    {
                        tracked.RevokedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    }
                }
            }
            return Results.Ok(new { });
        }).AllowAnonymous().DisableAntiforgery();

        // ---- the person's connected apps -------------------------------------
        app.MapGet("/api/oauth/connections", async (AppDbContext db, ClaimsPrincipal me) =>
        {
            if (await UserIdAsync(db, me) is not int uid)
                return Results.Ok(new { connections = Array.Empty<object>() });
            var rows = await db.OAuthTokens.AsNoTracking()
                .Where(t => t.UserId == uid && t.RevokedAt == null)
                .OrderByDescending(t => t.Id).ToListAsync();
            var names = await db.OAuthClients.AsNoTracking()
                .Where(c => rows.Select(r => r.ClientId).Contains(c.ClientId))
                .ToDictionaryAsync(c => c.ClientId, c => c.Name);
            return Results.Ok(new
            {
                connections = rows.Select(t => new
                {
                    id = t.Id,
                    client_name = names.TryGetValue(t.ClientId, out var n) ? n : "An AI app",
                    read_only = t.Scopes.Count > 0,
                    connected_at = t.CreatedAt,
                    last_used_at = t.LastUsedAt,
                    expires_at = t.ExpiresAt,
                }).ToList(),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/oauth/connections/{id:int}", async (int id, AppDbContext db,
            ClaimsPrincipal me) =>
        {
            if (await UserIdAsync(db, me) is not int uid)
                return Results.NotFound(new { detail = "No such connection." });
            // another login's connection is a 404, never someone else's to disconnect
            var row = await db.OAuthTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == uid);
            if (row == null) return Results.NotFound(new { detail = "No such connection." });
            if (row.RevokedAt == null)
            {
                row.RevokedAt = DateTime.UtcNow;
                AuditTrail.Add(db, "oauth.disconnected", row.ClientId, "disconnected by its owner");
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // =========================================================================
    // grants
    // =========================================================================

    private static async Task<IResult> CodeGrantAsync(AppDbContext db, Dictionary<string, string> form)
    {
        var code = Get(form, "code");
        var clientId = Get(form, "client_id");
        var redirectUri = Get(form, "redirect_uri");
        var verifier = Get(form, "code_verifier");
        if (code.Length == 0 || clientId.Length == 0 || verifier.Length == 0)
            return Error(400, "invalid_request", "code, client_id and code_verifier are required.");

        var hash = McpOAuth.Sha256(code);
        var row = await db.OAuthAuthorizations.FirstOrDefaultAsync(a => a.CodeHash == hash);
        if (row == null || row.UserId == null)
            return Error(400, "invalid_grant", "That code is not valid.");
        // Spend it first, whatever happens next: a code is single-use, and a failed
        // attempt must not leave it usable for whoever tries again.
        var alreadyUsed = row.UsedAt != null;
        var expired = row.ExpiresAt <= DateTime.UtcNow;
        row.UsedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (alreadyUsed) return Error(400, "invalid_grant", "That code has already been used.");
        if (expired) return Error(400, "invalid_grant", "That code has expired.");
        if (!string.Equals(row.ClientId, clientId, StringComparison.Ordinal))
            return Error(400, "invalid_grant", "That code was issued to another app.");
        if (redirectUri.Length > 0 && !string.Equals(row.RedirectUri, redirectUri, StringComparison.Ordinal))
            return Error(400, "invalid_grant", "redirect_uri does not match the authorization.");
        if (!McpOAuth.VerifyPkce(row.CodeChallenge, row.CodeChallengeMethod, verifier))
            return Error(400, "invalid_grant", "code_verifier does not match the challenge.");

        var (access, refresh, token) = McpOAuth.Mint(row.ClientId, row.UserId.Value, row.Scopes, row.Scope);
        db.OAuthTokens.Add(token);
        await db.OAuthClients.Where(c => c.ClientId == row.ClientId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return Tokens(access, refresh, token);
    }

    private static async Task<IResult> RefreshGrantAsync(AppDbContext db, Dictionary<string, string> form)
    {
        var presented = Get(form, "refresh_token");
        if (presented.Length == 0) return Error(400, "invalid_request", "refresh_token is required.");
        var found = await McpOAuth.ResolveRefreshAsync(db, presented);
        if (found == null) return Error(400, "invalid_grant", "That refresh token is not valid.");
        var clientId = Get(form, "client_id");
        if (clientId.Length > 0 && !string.Equals(found.ClientId, clientId, StringComparison.Ordinal))
            return Error(400, "invalid_grant", "That refresh token belongs to another app.");

        var row = await db.OAuthTokens.FirstAsync(t => t.Id == found.Id);
        // rotate: the presented refresh token is spent, so a replay buys nothing
        var (access, refresh) = McpOAuth.Rotate(row);
        await db.SaveChangesAsync();
        return Tokens(access, refresh, row);
    }

    private static IResult Tokens(string access, string refresh, OAuthToken row) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)(row.ExpiresAt - DateTime.UtcNow).TotalSeconds,
            ["refresh_token"] = refresh,
            ["scope"] = row.Scopes.Count > 0 ? McpOAuth.ScopeRead : McpOAuth.ScopeFull,
        });

    // =========================================================================
    // helpers
    // =========================================================================

    /// <summary>
    /// Why a redirect URI cannot be registered. https anywhere, http only on loopback
    /// (which is how a desktop app receives its redirect), and never a fragment.
    /// </summary>
    private static string? BadRedirect(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return $"redirect_uri must be an absolute URL ({uri}).";
        if (parsed.Fragment.Length > 0) return "A redirect_uri must not carry a fragment.";
        if (parsed.Scheme == Uri.UriSchemeHttps) return null;
        if (parsed.Scheme == Uri.UriSchemeHttp)
            return parsed.IsLoopback ? null : "http is only allowed on 127.0.0.1 / localhost.";
        // a private scheme is how a native app receives its redirect
        return parsed.Scheme.Contains('.') ? null
            : $"redirect_uri must be https, http on loopback, or a reverse-DNS app scheme ({uri}).";
    }

    /// <summary>An OAuth error, in OAuth's own shape plus the <c>detail</c> ChatBranch uses.</summary>
    private static IResult Error(int status, string error, string detail) =>
        Results.Json(new { error, error_description = detail, detail }, statusCode: status);

    /// <summary>An error the APP should see, at its own redirect.</summary>
    private static IResult Redirect(string redirectUri, string state, string error, string detail) =>
        Results.Redirect(Location(redirectUri, state, error, detail));

    /// <summary>The redirect back to the app, carrying either a code or an error.</summary>
    private static string Location(string redirectUri, string state, string? error = null,
        string? detail = null, string? code = null)
    {
        var sep = redirectUri.Contains('?') ? "&" : "?";
        var parts = new List<string>();
        if (code != null) parts.Add("code=" + Uri.EscapeDataString(code));
        if (error != null) parts.Add("error=" + Uri.EscapeDataString(error));
        if (detail != null) parts.Add("error_description=" + Uri.EscapeDataString(detail));
        if (state.Length > 0) parts.Add("state=" + Uri.EscapeDataString(state));
        return redirectUri + sep + string.Join("&", parts);
    }

    /// <summary>This API's own public origin — what a client is told to talk to.</summary>
    private static string Origin(HttpContext ctx, ScopingService sc) =>
        MessagingAccountTransport.PublicBaseUrl(null, sc, ctx);

    /// <summary>
    /// Where the dashboard lives, for the Allow page. <c>FRONTEND_URL</c> first (a
    /// separate dashboard origin), else this API's own origin (a single-origin
    /// deployment, where /api is proxied).
    /// </summary>
    private static string Dashboard(HttpContext ctx, ScopingService sc)
    {
        var front = (Environment.GetEnvironmentVariable("FRONTEND_URL") ?? "").Trim().TrimEnd('/');
        return front.Length > 0 ? front : Origin(ctx, sc);
    }

    private static string Host(string uri)
    {
        try { return new Uri(uri).Host; } catch { return uri; }
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

    /// <summary>
    /// The token endpoint takes form-encoded parameters (RFC 6749), but some clients
    /// send JSON. Read either, so a connection never fails over a content type.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpContext ctx)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (ctx.Request.HasFormContentType)
            {
                foreach (var kv in await ctx.Request.ReadFormAsync())
                    map[kv.Key] = kv.Value.ToString();
                return map;
            }
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            if (body.ValueKind == JsonValueKind.Object)
                foreach (var prop in body.EnumerateObject())
                    map[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.ToString();
        }
        catch { /* an unreadable body is simply no parameters */ }
        return map;
    }

    private static string Get(Dictionary<string, string> form, string key) =>
        form.TryGetValue(key, out var v) ? v.Trim() : "";

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    private static List<string> Strings(JsonElement body, string key)
    {
        var list = new List<string>();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var arr)
            && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    list.Add(s);
        return list;
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
