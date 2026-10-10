using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using ChatBranch.Domain;

namespace ChatBranch.Api.Auth;

/// <summary>
/// Company admins (an admin with assigned entities — <see cref="ShellScope.IsCompanyAdmin"/>)
/// hold the admin capabilities INSIDE their companies, but never the ones that
/// only exist at the organization level: the permission matrix, app & provider
/// keys, AI keys, billing terms, the organization's notification defaults,
/// appearance, custom fields and pronunciation dictionary. Published to the async
/// flow right after authentication (like the API-key scopes), so every
/// capability check — route filters and in-handler <c>HasCapabilityAsync</c>
/// calls — drops them for the request, whatever the role matrix says.
/// </summary>
public static class CompanyAdminScope
{
    /// <summary>Capabilities that act on the whole organization — never a company admin's.</summary>
    public static readonly HashSet<string> OrgLevelCapabilities = new()
    {
        "rbac.manage", "app_keys.manage", "settings.ai_keys.manage", "billing.manage",
        "notifications.manage", "settings.appearance.org", "settings.custom_fields.manage",
        "pronunciation.manage",
    };

    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>True while the current request belongs to a company admin.</summary>
    public static bool Current => Active.Value;

    /// <summary>Tests and background seams can set it directly.</summary>
    public static void Set(bool value) => Active.Value = value;

    public static IApplicationBuilder UseCompanyAdminScope(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var on = false;
            if (ctx.User.Identity?.IsAuthenticated == true && ShellScope.RoleOf(ctx.User) == Roles.Admin)
            {
                try
                {
                    var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
                    on = ShellScope.IsCompanyAdmin(db, ctx.User);
                }
                catch (Exception) { /* unseeded DB: the plain admin view */ }
            }
            Active.Value = on;
            await next(ctx);
        });

    /// <summary>A capability list with the org-level ones removed for a company admin.</summary>
    public static List<string> Filter(List<string> caps, bool companyAdmin) =>
        companyAdmin ? caps.Where(c => !OrgLevelCapabilities.Contains(c)).ToList() : caps;
}
