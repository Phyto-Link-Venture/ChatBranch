using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The Permissions page lists every permission: the catalog it renders
/// (<c>GET /api/auth/permissions</c>) holds every capability the server checks
/// and the dashboard gates on, and the frontend's fallback copy
/// (<c>CAPABILITY_CATALOG</c> in <c>frontend/src/lib/auth.ts</c>) holds exactly
/// the same ids — so no permission can exist that a superadmin cannot see or toggle.
/// </summary>
public class PermissionCatalogTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PermissionCatalogTests(ApiFactory factory) => _factory = factory;

    private static readonly Regex CapId = new(@"[""']([a-z_]+(?:\.[a-z_]+)+)[""']", RegexOptions.Compiled);

    // lines that name a capability: the server's gates and the dashboard's
    private static readonly Regex BackendGate = new(
        @"RequireCapability|RequireAnyCapability|RequireThreadAccess|HasCapabilityAsync|RequireCapability\(|Denied\(db, me,|Cap\s*=",
        RegexOptions.Compiled);
    private static readonly Regex FrontendGate = new(@"\bcap:\s*['\[]|\bcan\(\s*'|\bcapability:\s*'", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "frontend", "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private async Task<HashSet<string>> ServedCatalog()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "superadmin", password = "super123" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var perms = await c.GetFromJsonAsync<JsonElement>("/api/auth/permissions");
        return perms.GetProperty("catalog").EnumerateArray()
            .Select(e => e.GetProperty("capability").GetString()!).ToHashSet();
    }

    private static HashSet<string> FrontendCatalog()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "frontend", "src", "lib", "auth.ts"));
        var start = src.IndexOf("export const CAPABILITY_CATALOG", StringComparison.Ordinal);
        var end = src.IndexOf("\n]", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "CAPABILITY_CATALOG not found in frontend/src/lib/auth.ts");
        return Regex.Matches(src[start..end], @"capability:\s*'([^']+)'").Select(m => m.Groups[1].Value).ToHashSet();
    }

    private static IEnumerable<(string File, string Cap)> Used(string dir, string[] exts, Regex gate)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(f => exts.Contains(Path.GetExtension(f))
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            foreach (var line in File.ReadLines(f).Where(l => gate.IsMatch(l)))
                foreach (Match m in CapId.Matches(line))
                    yield return (Path.GetRelativePath(RepoRoot(), f), m.Groups[1].Value);
    }

    [Fact]
    public async Task The_permissions_page_and_the_dashboard_list_the_same_permissions()
    {
        var served = await ServedCatalog();
        var front = FrontendCatalog();
        Assert.Empty(served.Except(front));   // the server knows one the page's fallback doesn't
        Assert.Empty(front.Except(served));   // the dashboard offers one the server would refuse
    }

    [Fact]
    public async Task Every_capability_the_code_checks_is_on_the_permissions_page()
    {
        var served = await ServedCatalog();
        var root = RepoRoot();
        var used = Used(Path.Combine(root, "backend-csharp", "src"), new[] { ".cs" }, BackendGate)
            .Concat(Used(Path.Combine(root, "frontend", "src"), new[] { ".ts", ".tsx" }, FrontendGate))
            // capability ids only: "<area>.<verb>" where the area is one the catalog uses
            .Where(u => served.Any(s => s.Split('.')[0] == u.Cap.Split('.')[0]))
            .ToList();
        Assert.NotEmpty(used);
        var missing = used.Where(u => !served.Contains(u.Cap))
            .Select(u => $"{u.Cap} ({u.File})").Distinct().ToList();
        Assert.True(missing.Count == 0, "Not on the Permissions page: " + string.Join(", ", missing));
    }
}

/// <summary>The AI's safety rules: org-wide, editable only with
/// <c>settings.ai_hard_rules.manage</c> (superadmin by default), reset by "".</summary>
public class HardRulesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public HardRulesTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Only_the_capability_holder_edits_them_and_every_prompt_follows()
    {
        var su = await Authed("superadmin", "super123");
        var admin = await Authed("admin", "admin123");

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/persona");
        Assert.False(before.GetProperty("can_edit_hard_rules").GetBoolean());
        Assert.False(before.GetProperty("hard_rules_custom").GetBoolean());

        var denied = await admin.PutAsJsonAsync("/api/aicore/persona", new { hard_rules = "Be rude." });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);

        const string mine = "You are the clinic's assistant. Never give medical diagnoses. Never invent prices.";
        var ok = await su.PutAsJsonAsync("/api/aicore/persona", new { hard_rules = mine });
        ok.EnsureSuccessStatusCode();
        var after = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(after.GetProperty("can_edit_hard_rules").GetBoolean());
        Assert.True(after.GetProperty("hard_rules_custom").GetBoolean());
        Assert.Equal(mine, after.GetProperty("hard_rules").GetString());
        Assert.StartsWith(mine, after.GetProperty("preamble").GetString());
        // the admin's view follows; the persona below is untouched
        var seen = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/persona");
        Assert.StartsWith(mine, seen.GetProperty("preamble").GetString());

        var reset = await su.PutAsJsonAsync("/api/aicore/persona", new { hard_rules = "" });
        reset.EnsureSuccessStatusCode();
        var back = await reset.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(back.GetProperty("hard_rules_custom").GetBoolean());
        Assert.Equal(back.GetProperty("hard_rules_default").GetString(), back.GetProperty("hard_rules").GetString());
    }
}
