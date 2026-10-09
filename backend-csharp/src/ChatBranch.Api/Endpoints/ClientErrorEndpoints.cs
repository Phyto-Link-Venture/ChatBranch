using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Audit;
using ChatBranch.Api.Auth;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The dashboard's crash reports. When a page throws, its error boundary posts
/// <c>{message, stack, path, digest, user_agent}</c> here; it lands in the API's
/// log (Warning, category <c>ChatBranch.ClientErrors</c>) with the signed-in
/// user, so "Application error" on someone's screen comes with a stack trace
/// on the server. Nothing is stored in the database and the request is not
/// written to the activity log.
/// </summary>
public static class ClientErrorEndpoints
{
    private const int MaxField = 4000;

    public static void MapClientErrorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/client-errors", (JsonElement body, ClaimsPrincipal me, ILoggerFactory logs) =>
        {
            string Field(string name)
            {
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v)) return "";
                var s = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
                s = s.Replace('\r', ' ');
                return s.Length > MaxField ? s[..MaxField] : s;
            }
            var log = logs.CreateLogger("ChatBranch.ClientErrors");
            log.LogWarning(
                "Dashboard error for {User} ({Role}) on {Path}: {Message} [digest {Digest}] [{UserAgent}]\n{Stack}",
                me.Identity?.Name ?? "?", me.FindFirstValue(ClaimTypes.Role) ?? "?",
                Field("path"), Field("message"), Field("digest"), Field("user_agent"), Field("stack"));
            return Results.Ok(new { ok = true });
        })
        .RequireAuthorization(AuthPolicies.Authenticated)
        .WithMetadata(new SkipRequestAuditAttribute());
    }
}
