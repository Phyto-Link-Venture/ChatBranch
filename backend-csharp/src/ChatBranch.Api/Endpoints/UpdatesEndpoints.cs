using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Versions and each company's update choice (Domain/Releases.cs).
/// <list type="bullet">
/// <item><c>GET /api/updates</c> — any signed-in user: the released versions, the
/// version the caller's company runs (<c>mine</c>), and — for someone who may
/// manage updates — each company they manage with its choice.</item>
/// <item><c>PUT /api/updates</c> — <c>updates.manage</c> (admins by default; a
/// company admin only for their own companies): hold or resume automatic minor
/// updates, approve a version while holding, or move to another major.</item>
/// <item><c>GET /api/updates/overview</c> — <c>updates.overview</c> (super admin
/// by default): every company's version and the recent changes, so the
/// developers always know who runs what.</item>
/// </list>
/// Every change writes an activity-log row (action <c>updates.policy</c>).
/// </summary>
public static class UpdatesEndpoints
{
    public const string ManageCap = "updates.manage";
    public const string OverviewCap = "updates.overview";
    private const string AuditAction = "updates.policy";

    public static void MapUpdatesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/updates", async (AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var policies = await db.UpdatePolicies.AsNoTracking().ToDictionaryAsync(p => p.EntityId);
            var companies = await CompaniesAsync(db);
            var mineCompany = OwnCompanies(db, sc, me).FirstOrDefault();
            policies.TryGetValue(mineCompany, out var minePolicy);
            var mine = mineCompany > 0 ? ProductReleases.Effective(minePolicy) : ProductReleases.Current;

            var canManage = await ShellScope.HasCapAsync(db, me, ManageCap);
            var managed = canManage ? Manageable(db, sc, me, companies) : new HashSet<int>();
            return Results.Ok(new
            {
                current = ProductReleases.Current.ToString(),
                mine = mine.ToString(),
                mine_company = mineCompany > 0 ? companies.GetValueOrDefault(mineCompany) : null,
                releases = ReleasesJson(),
                can_manage = canManage,
                can_overview = await ShellScope.HasCapAsync(db, me, OverviewCap),
                companies = companies.Where(c => managed.Contains(c.Key)).OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(c => CompanyJson(c.Key, c.Value, policies.GetValueOrDefault(c.Key))).ToList(),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPut("/api/updates", async (JsonElement body, HttpContext ctx, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            if (body.ValueKind != JsonValueKind.Object) return Bad("Send a JSON object.");
            var companies = await CompaniesAsync(db);
            var company = ResolveCompany(companies, Str(body, "company"));
            if (company == null) return Bad("Which company? Send company (a top-level entity's id or name).");
            if (!Manageable(db, sc, me, companies).Contains(company.Value))
                return Results.Json(new { detail = "That company is outside your scope." }, statusCode: 403);

            var row = await db.UpdatePolicies.FirstOrDefaultAsync(p => p.EntityId == company.Value);
            var before = row == null ? Describe(null) : Describe(row);
            row ??= db.UpdatePolicies.Add(new UpdatePolicy { EntityId = company.Value }).Entity;

            // a move to another major first, so a hold or an approval below applies within it
            if (body.TryGetProperty("pinned_major", out var pm) && pm.ValueKind == JsonValueKind.Number)
            {
                var major = pm.GetInt32();
                if (!ProductReleases.Majors.Contains(major))
                    return Bad($"Version {major} isn't available. Available: {string.Join(", ", ProductReleases.Majors)}.");
                if (major != row.PinnedMajor)
                {
                    row.PinnedMajor = major;
                    if (row.MinorMode == UpdateModes.Hold) row.HeldVersion = ProductReleases.LatestIn(major).ToString();
                }
            }

            var mode = Str(body, "minor_mode");
            if (mode.Length > 0)
            {
                if (mode is not (UpdateModes.Auto or UpdateModes.Hold))
                    return Bad("minor_mode must be \"auto\" or \"hold\".");
                if (mode == UpdateModes.Hold && row.MinorMode != UpdateModes.Hold)
                    row.HeldVersion = ProductReleases.Effective(row).ToString(); // nothing changes for the team today
                if (mode == UpdateModes.Auto) row.HeldVersion = "";
                row.MinorMode = mode;
            }

            var approve = Str(body, "approve");
            if (approve.Length > 0)
            {
                if (!SemVer.TryParse(approve, out var v) || !ProductReleases.Exists(v))
                    return Bad($"'{approve}' isn't a released version.");
                if (v.Major != row.PinnedMajor)
                    return Bad($"Version {v} belongs to version {v.Major}; this company is on version {row.PinnedMajor}.");
                if (row.MinorMode != UpdateModes.Hold)
                    return Bad("This company already gets new versions automatically.");
                row.HeldVersion = v.ToString();
            }

            var after = Describe(row);
            if (after == before) return Results.Ok(CompanyJson(company.Value, companies[company.Value], row));

            row.UpdatedBy = Cap(me.Identity?.Name ?? "", 150);
            row.UpdatedAt = DateTime.UtcNow;
            db.AuditLogs.Add(new AuditLog
            {
                Actor = Cap(me.Identity?.Name ?? "anonymous", 150),
                Action = AuditAction,
                Target = Cap($"company:{company.Value} {companies[company.Value]}", 190),
                Detail = Cap($"{before} → {after}", 1000),
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Ok(CompanyJson(company.Value, companies[company.Value], row));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapGet("/api/updates/overview", async (AppDbContext db) =>
        {
            var policies = await db.UpdatePolicies.AsNoTracking().ToDictionaryAsync(p => p.EntityId);
            var companies = await CompaniesAsync(db);
            var changes = await db.AuditLogs.AsNoTracking().Where(a => a.Action == AuditAction)
                .OrderByDescending(a => a.CreatedAt).Take(50)
                .Select(a => new { at = a.CreatedAt, by = a.Actor, company = a.Target, change = a.Detail })
                .ToListAsync();
            var rows = companies.OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
                .Select(c => CompanyJson(c.Key, c.Value, policies.GetValueOrDefault(c.Key))).ToList();
            return Results.Ok(new
            {
                current = ProductReleases.Current.ToString(),
                releases = ReleasesJson(),
                companies = rows,
                changes,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(OverviewCap);
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>Every company (top-level entity): id → name.</summary>
    private static Task<Dictionary<int, string>> CompaniesAsync(AppDbContext db) =>
        db.Entities.AsNoTracking().Where(e => e.ParentId == null).ToDictionaryAsync(e => e.Id, e => e.Name);

    /// <summary>The caller's own companies (from their assigned entities); empty when unbound.</summary>
    private static List<int> OwnCompanies(AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        ShellScope.ScopeRoots(db, me).Select(id => sc.CompanyOf(id)).OfType<int>().Distinct().ToList();

    /// <summary>Companies the caller may manage: their own when bound, every company otherwise.</summary>
    private static HashSet<int> Manageable(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        Dictionary<int, string> companies)
    {
        var own = OwnCompanies(db, sc, me);
        return own.Count > 0 ? own.ToHashSet() : companies.Keys.ToHashSet();
    }

    private static int? ResolveCompany(Dictionary<int, string> companies, string raw)
    {
        if (raw.Length == 0) return null;
        if (int.TryParse(raw, out var id)) return companies.ContainsKey(id) ? id : null;
        var hit = companies.Where(c => string.Equals(c.Value, raw, StringComparison.OrdinalIgnoreCase)).Select(c => (int?)c.Key);
        return hit.FirstOrDefault();
    }

    private static object CompanyJson(int id, string name, UpdatePolicy? p) => new
    {
        id,
        name,
        version = ProductReleases.Effective(p).ToString(),
        latest_in_major = ProductReleases.LatestIn(p?.PinnedMajor ?? ProductReleases.BaselineMajor).ToString(),
        pinned_major = p?.PinnedMajor ?? ProductReleases.BaselineMajor,
        minor_mode = p?.MinorMode ?? UpdateModes.Auto,
        held_version = p?.HeldVersion ?? "",
        updated_by = p?.UpdatedBy ?? "",
        updated_at = p?.UpdatedAt,
    };

    private static List<object> ReleasesJson() =>
        ProductReleases.All.Reverse().Select(r => (object)new { version = r.Version.ToString(), date = r.Date.ToString("yyyy-MM-dd") }).ToList();

    private static string Describe(UpdatePolicy? p) =>
        p == null || (p.MinorMode == UpdateModes.Auto && p.PinnedMajor == ProductReleases.BaselineMajor)
            ? $"version {p?.PinnedMajor ?? ProductReleases.BaselineMajor}, new features automatic"
            : p.MinorMode == UpdateModes.Hold
                ? $"version {p.PinnedMajor}, holding at {p.HeldVersion}"
                : $"version {p.PinnedMajor}, new features automatic";

    private static string Str(JsonElement body, string key) =>
        body.TryGetProperty(key, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString()?.Trim() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" }
            : "";

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    private static IResult Bad(string detail) => Results.Json(new { detail, error = detail }, statusCode: 400);
}
