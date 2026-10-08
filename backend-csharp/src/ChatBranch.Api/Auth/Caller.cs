using System.Security.Claims;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Auth;

/// <summary>Small readers for the signed-in caller's identity claims.</summary>
public static class Caller
{
    public static string Role(ClaimsPrincipal me) => me.FindFirstValue(ClaimTypes.Role) ?? "";

    public static string Username(ClaimsPrincipal me) => me.Identity?.Name ?? "";

    public static int? TenantId(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;

    public static bool IsAdminPlus(ClaimsPrincipal me) => Role(me) is Roles.Admin or Roles.SuperAdmin;

    /// <summary>The caller's auth_user id (API keys carry it; sessions resolve it via the profile).</summary>
    public static async Task<int?> UserIdAsync(AppDbContext db, ClaimsPrincipal me)
    {
        if (int.TryParse(me.FindFirstValue("user_id"), out var direct)) return direct;
        if (int.TryParse(me.FindFirstValue("profile_id"), out var pid))
            return await db.Profiles.AsNoTracking().Where(p => p.Id == pid)
                .Select(p => (int?)p.UserId).FirstOrDefaultAsync();
        var name = Username(me);
        if (name.Length == 0) return null;
        return await db.Users.AsNoTracking().Where(u => u.Username == name)
            .Select(u => (int?)u.Id).FirstOrDefaultAsync();
    }
}
