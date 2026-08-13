using ChatBranch.Domain;
using Microsoft.AspNetCore.Authorization;

namespace ChatBranch.Api.Auth;

/// <summary>
/// Server-side RBAC gates, mirroring the Django rule set: global settings +
/// credentials → superadmin; account management + audit log → admin or above;
/// everything else → any authenticated caller.
/// </summary>
public static class AuthPolicies
{
    public const string Authenticated = "authenticated";
    public const string AdminPlus = "admin+";
    public const string SuperAdmin = "superadmin";

    public static void AddChatBranchPolicies(this AuthorizationOptions o)
    {
        o.AddPolicy(Authenticated, p => p.RequireAuthenticatedUser());
        o.AddPolicy(AdminPlus, p => p.RequireRole(Roles.Admin, Roles.SuperAdmin));
        o.AddPolicy(SuperAdmin, p => p.RequireRole(Roles.SuperAdmin));
    }
}
