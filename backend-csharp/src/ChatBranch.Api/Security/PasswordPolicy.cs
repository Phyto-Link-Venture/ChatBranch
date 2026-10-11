using ChatBranch.Data;

namespace ChatBranch.Api.Security;

/// <summary>
/// The rule every NEW password meets — creating a user, an admin's reset, My
/// account's change and the forced change of a seeded password. On a real install
/// (no <c>SEED_DEMO</c>): at least <c>PASSWORD_MIN_LENGTH</c> (10) characters, not the
/// username, not one of the common passwords below (nor a seeded POC password). A
/// demo keeps the old 8-character floor so its scripted logins stay as they are.
/// Existing passwords are never re-checked — the rule applies when one is set.
/// </summary>
public static class PasswordPolicy
{
    public const int DemoMinLength = 8;
    public const int MaxLength = 1024;

    /// <summary>A refusal: the stable <c>Code</c> the dashboard translates, English <c>Detail</c> for API callers.</summary>
    public sealed record Problem(string Code, string Detail, int Min);

    /// <summary>Null = acceptable.</summary>
    public static Problem? Check(string? password, string? username, SecurityOptions options)
    {
        var pw = password ?? "";
        var min = options.RealInstall ? options.PasswordMinLength : DemoMinLength;
        if (pw.Length < min)
            return new Problem("password_too_short", $"The password must be at least {min} characters.", min);
        if (pw.Length > MaxLength)
            return new Problem("password_too_long", $"The password can be at most {MaxLength} characters.", min);
        if (!options.RealInstall) return null;
        var lowered = pw.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(username) && lowered == username.Trim().ToLowerInvariant())
            return new Problem("password_is_username", "The password can't be the same as the username.", min);
        if (IsCommon(lowered))
            return new Problem("password_common",
                "That password is too common — attackers try it first. Choose something less predictable.", min);
        if (pw.Distinct().Count() < 4)
            return new Problem("password_common",
                "That password is too predictable — use more different characters.", min);
        return null;
    }

    /// <summary>The JSON answer for a refusal (400, with <c>field</c> when the form names one).</summary>
    public static IResult Refusal(Problem p, string? field) =>
        Results.Json(new { detail = p.Detail, field, code = p.Code, min = p.Min }, statusCode: 400);

    public static bool IsCommon(string lowered) =>
        Common.Contains(lowered)
        || Seed.DemoUsers.Any(d => d.Password == lowered)
        // "password1", "password123!", "qwerty2024"… — a common word with digits / symbols bolted on
        || Common.Contains(lowered.TrimEnd('!', '@', '#', '$', '.', '?', '*').TrimEnd("0123456789".ToCharArray()));

    /// <summary>A small built-in list — the passwords guessing tools try first (lowercase).</summary>
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "password", "passw0rd", "p@ssw0rd", "p@ssword", "pa$$word", "password1", "password123", "passwords",
        "123456", "1234567", "12345678", "123456789", "1234567890", "12345678910", "0123456789", "987654321",
        "9876543210", "111111", "1111111111", "000000", "0000000000", "123123", "123123123", "112233",
        "121212", "654321", "666666", "696969", "777777", "888888", "999999", "1q2w3e4r", "1q2w3e4r5t",
        "1qaz2wsx", "qwerty", "qwertyuiop", "qwerty123", "qwertyui", "asdfgh", "asdfghjkl", "zxcvbnm",
        "abc123", "abcd1234", "abcdefg", "abcdefgh", "abcdefghij", "aa123456", "a1b2c3d4", "iloveyou",
        "iloveyou1", "admin", "admin123", "admin1234", "administrator", "root", "toor", "superadmin",
        "superuser", "welcome", "welcome1", "welcome123", "letmein", "letmein1", "changeme", "changeme123",
        "default", "secret", "secret123", "master", "monkey", "dragon", "football", "baseball", "sunshine",
        "princess", "shadow", "superman", "batman", "trustno1", "starwars", "whatever", "freedom",
        "computer", "internet", "login", "login123", "test", "test123", "test1234", "testing", "guest",
        "user", "user123", "demo", "demo123", "sample", "temp", "temp123", "temporary", "qazwsxedc",
        "zaq12wsx", "michael", "jennifer", "charlie", "jordan23", "liverpool", "chelsea", "arsenal",
        "malaysia", "malaysia123", "kualalumpur", "singapore", "chatbranch", "chatbranch123", "chatwood",
        "company", "company123", "business", "office", "office123", "support", "support123", "service",
        "customer", "sales", "marketing", "manager", "staff", "staff123", "agent", "agent123", "operator",
    };
}
