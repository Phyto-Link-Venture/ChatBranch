using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;

namespace ChatBranch.Api.Tools;

/// <summary>
/// Runs one of the app's OWN endpoints in-process, as a given user, without a
/// network hop. This is how the MCP/assistant tools reuse the dashboard's code
/// paths instead of re-implementing them: a tool that sends a message calls the
/// very handler the composer calls, so the opt-out, cold-text, 24h-window and
/// quiet-hours guards — and that handler's own audit row — apply unchanged.
///
/// The endpoint is looked up by its route TEMPLATE (as written in the Map call)
/// plus method; route values, query and a JSON body are supplied explicitly.
/// The endpoint's authorization policy is evaluated against the user first.
/// </summary>
public static class InternalApi
{
    public sealed record Response(int Status, JsonElement Body)
    {
        public bool Ok => Status is >= 200 and < 300;

        /// <summary>The handler's own error text (<c>detail</c>/<c>error</c>), if any.</summary>
        public string Detail()
        {
            if (Body.ValueKind == JsonValueKind.Object)
            {
                if (Body.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                    return d.GetString() ?? "";
                if (Body.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m))
                        return m.GetString() ?? "";
                }
            }
            return Status switch
            {
                401 => "Not signed in.",
                403 => "Not allowed for your role.",
                404 => "Not found.",
                _ => $"Request failed ({Status}).",
            };
        }
    }

    public static async Task<Response> InvokeAsync(HttpContext outer, ClaimsPrincipal user, string method,
        string routeTemplate, IDictionary<string, string>? routeValues = null, object? body = null,
        IDictionary<string, string?>? query = null, CancellationToken ct = default)
    {
        var sp = outer.RequestServices;
        var endpoint = Find(sp, method, routeTemplate);
        if (endpoint?.RequestDelegate == null)
            return Error(501, $"This build has no {method} {routeTemplate} endpoint.");

        // Same gate the authorization middleware would apply.
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() == null)
        {
            var data = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            if (data.Count > 0)
            {
                var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();
                var policy = await AuthorizationPolicy.CombineAsync(provider, data);
                if (policy != null)
                {
                    var authz = sp.GetRequiredService<IAuthorizationService>();
                    var result = await authz.AuthorizeAsync(user, null, policy);
                    if (!result.Succeeded)
                        return Error(user.Identity?.IsAuthenticated == true ? 403 : 401,
                            "Not allowed for your role.");
                }
            }
        }

        var values = routeValues ?? new Dictionary<string, string>();
        var ctx = new DefaultHttpContext
        {
            RequestServices = sp,
            User = user,
            TraceIdentifier = outer.TraceIdentifier,
        };
        var responseBody = new MemoryStream();
        ctx.Response.Body = responseBody;
        ctx.Request.Method = method;
        ctx.Request.Scheme = outer.Request.Scheme;
        ctx.Request.Host = outer.Request.Host;
        ctx.Request.Path = BuildPath(routeTemplate, values);
        if (query is { Count: > 0 })
            ctx.Request.QueryString = QueryString.Create(query.Where(kv => kv.Value != null)
                .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
        foreach (var (k, v) in values) ctx.Request.RouteValues[k] = v;
        ctx.Request.Headers.UserAgent = outer.Request.Headers.UserAgent;
        ctx.Connection.RemoteIpAddress = outer.Connection.RemoteIpAddress;
        if (body != null)
        {
            var bytes = body is JsonElement je
                ? Encoding.UTF8.GetBytes(je.GetRawText())
                : JsonSerializer.SerializeToUtf8Bytes(body);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
            ctx.Request.Body = new MemoryStream(bytes);
            // Minimal APIs only bind a body when the server says the request can have one.
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        }
        ctx.RequestAborted = ct;
        ctx.SetEndpoint(endpoint);

        await endpoint.RequestDelegate(ctx);

        var status = ctx.Response.StatusCode;
        responseBody.Position = 0;
        var raw = Encoding.UTF8.GetString(responseBody.ToArray());
        JsonElement parsed;
        try
        {
            parsed = raw.Length == 0
                ? JsonDocument.Parse("{}").RootElement.Clone()
                : JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch
        {
            parsed = JsonSerializer.SerializeToElement(new { detail = raw });
        }
        return new Response(status, parsed);
    }

    private static RouteEndpoint? Find(IServiceProvider sp, string method, string template)
    {
        var wanted = Normalize(template);
        var source = sp.GetRequiredService<EndpointDataSource>();
        return source.Endpoints.OfType<RouteEndpoint>()
            .Where(e => Normalize(e.RoutePattern.RawText ?? "") == wanted)
            .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                .Contains(method, StringComparer.OrdinalIgnoreCase) == true)
            .OrderBy(e => e.Order)
            .FirstOrDefault();
    }

    private static string Normalize(string template) =>
        "/" + template.Trim().Trim('/').ToLowerInvariant();

    /// <summary>"/api/x/{id:int}/y" + {id: 5} → "/api/x/5/y" (PathString holds it unescaped).</summary>
    private static string BuildPath(string template, IDictionary<string, string> values)
    {
        var parts = template.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (!p.StartsWith('{') || !p.EndsWith('}')) continue;
            var name = p.Trim('{', '}').Split(':', '=', '?')[0];
            parts[i] = values.TryGetValue(name, out var v) ? v : "";
        }
        return string.Join('/', parts);
    }

    private static Response Error(int status, string detail) =>
        new(status, JsonSerializer.SerializeToElement(new { detail }));

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
