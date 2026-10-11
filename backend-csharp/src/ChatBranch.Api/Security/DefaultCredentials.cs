using ChatBranch.Data;
using ChatBranch.Domain;

namespace ChatBranch.Api.Security;

/// <summary>
/// Seeded passwords on a real install. The seed gives the superadmin the POC password
/// <c>super123</c> unless <c>SUPERADMIN_PASSWORD</c> is set (and a database that once ran
/// with <c>SEED_DEMO</c> may still hold <c>admin123</c> / <c>agent123</c>). On a real
/// install (no <c>SEED_DEMO</c>) signing in with one of them succeeds — nobody is locked
/// out — but the session is restricted to changing the password first
/// (<see cref="SessionGate"/>). The operator's own <c>SUPERADMIN_PASSWORD</c> is never
/// forced to change; a demo is untouched.
/// </summary>
public static class DefaultCredentials
{
    public static bool MustChange(SecurityOptions options, AppUser user, string typedPassword)
    {
        if (!options.RealInstall) return false;
        var seeded = Seed.SeededPasswordFor(user.Username);
        if (seeded == null || !string.Equals(seeded, typedPassword, StringComparison.Ordinal)) return false;
        var superadmin = Seed.DemoUsers.First(d => d.Role == Roles.SuperAdmin).Username;
        // the operator chose this exact password for the superadmin on purpose
        if (user.Username == superadmin && options.SuperadminPassword.Length > 0
            && string.Equals(options.SuperadminPassword, typedPassword, StringComparison.Ordinal))
            return false;
        return true;
    }
}
