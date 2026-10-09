using System.Globalization;
using System.Security.Claims;
using System.Text;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The activity log read API — everything people, API keys, AI apps (MCP), the
/// assistant, automations, flows, webhooks and the system did, filterable.
///
///   GET /api/audit         ?from=&amp;to=&amp;actor=&amp;source=&amp;action=&amp;entity=&amp;q=&amp;page=&amp;page_size=
///   GET /api/audit.csv     same filters, quoted CSV (export is itself audited)
///   GET /api/audit/facets  distinct actors / actions / sources for the filter bar
///
/// Visibility (capability <c>activity.view</c> required): superadmin sees every
/// row; an admin sees every row in their tenant — rows whose actor belongs to it
/// or whose entity is one of its entities (plus tenant-less system rows on a
/// single-tenant deployment); anyone else sees only their own rows. The old
/// <c>/api/auth/audit</c> keeps working unchanged.
/// </summary>
public static class AuditEndpoints
{
    public record Filters(string? From, string? To, string? Actor, string? Source, string? Action,
        string? Entity, string? Q);

    private const int MaxPageSize = 200;
    private const int MaxCsvRows = 10_000;

    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", async (string? from, string? to, string? actor, string? source,
            string? action, string? entity, string? q, int? page, int? page_size,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var visible = await VisibleAsync(db, me);
            if (visible == null) return Denied();
            var filtered = await ApplyAsync(visible, new Filters(from, to, actor, source, action, entity, q), db, sc);
            var size = Math.Clamp(page_size ?? 50, 1, MaxPageSize);
            var total = await filtered.CountAsync();
            var pages = Math.Max(1, (int)Math.Ceiling(total / (double)size));
            var p = Math.Clamp(page ?? 1, 1, pages);
            var rows = await filtered.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                .Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new
            {
                entries = await PayloadAsync(db, rows),
                total,
                page = p,
                page_size = size,
                pages,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("activity.view");

        app.MapGet("/api/audit.csv", async (string? from, string? to, string? actor, string? source,
            string? action, string? entity, string? q, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var visible = await VisibleAsync(db, me);
            if (visible == null) return Denied();
            var filters = new Filters(from, to, actor, source, action, entity, q);
            var rows = await (await ApplyAsync(visible, filters, db, sc))
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                .Take(MaxCsvRows).ToListAsync();
            var entities = await EntityNamesAsync(db, rows);

            var sb = new StringBuilder();
            sb.Append("created_at,actor,source,action,target,detail,entity,method,path,status,ip,user_agent,request_id\r\n");
            foreach (var r in rows)
            {
                var ent = r.EntityId is int eid && entities.TryGetValue(eid, out var e) ? e.Slug : "";
                sb.AppendJoin(',', new[]
                {
                    Csv(Utc(r.CreatedAt)), Csv(r.Actor), Csv(r.Source), Csv(r.Action), Csv(r.Target),
                    Csv(r.Detail), Csv(ent), Csv(r.Method), Csv(r.Path),
                    Csv(r.Status?.ToString(CultureInfo.InvariantCulture) ?? ""), Csv(r.Ip),
                    Csv(r.UserAgent), Csv(r.RequestId),
                }).Append("\r\n");
            }
            // Taking the log out of the building is itself worth a row.
            AuditTrail.Write(db, "activity.export", "activity log", $"{rows.Count} row(s)");
            var name = $"activity-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
                "text/csv; charset=utf-8", name);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("activity.view");

        app.MapGet("/api/audit/facets", async (AppDbContext db, ClaimsPrincipal me) =>
        {
            var visible = await VisibleAsync(db, me);
            if (visible == null) return Denied();
            var actors = await visible.GroupBy(a => a.Actor)
                .Select(g => new { value = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).Take(200).ToListAsync();
            var actions = await visible.GroupBy(a => a.Action)
                .Select(g => new { value = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).Take(300).ToListAsync();
            var sourceCounts = await visible.GroupBy(a => a.Source)
                .Select(g => new { value = g.Key, count = g.Count() }).ToListAsync();
            var sources = AuditSources.All.Select(s => new
            {
                value = s,
                count = sourceCounts.FirstOrDefault(x => x.value == s)?.count ?? 0,
            });
            return Results.Ok(new { actors, actions, sources });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("activity.view");
    }

    // ---- visibility + filters (shared with the list_activity tool) ---------

    /// <summary>The rows this caller may read, or null when they lack activity.view.</summary>
    internal static async Task<IQueryable<AuditLog>?> VisibleAsync(AppDbContext db, ClaimsPrincipal me)
    {
        var caps = await ApiKeyAuth.EffectiveCapabilitiesAsync(db, me);
        if (!caps.Contains("activity.view")) return null;
        var q = db.AuditLogs.AsNoTracking();
        var role = Caller.Role(me);
        if (role == Roles.SuperAdmin) return q;

        if (role == Roles.Admin && Caller.TenantId(me) is int tid)
        {
            var tenantUsers = db.Profiles.Where(p => p.TenantId == tid).Select(p => p.UserId);
            var tenantEntities = db.Entities.Where(e => e.TenantId == tid).Select(e => e.Id);
            // Background/system rows carry neither a user nor an entity. On a
            // single-tenant deployment they can only be this tenant's; with
            // several tenants they stay superadmin-only rather than leak.
            var singleTenant = await db.Tenants.CountAsync() <= 1;
            return q.Where(a =>
                (a.UserId != null && tenantUsers.Contains(a.UserId.Value))
                || (a.EntityId != null && tenantEntities.Contains(a.EntityId.Value))
                || (singleTenant && a.UserId == null && a.EntityId == null));
        }

        var uid = await Caller.UserIdAsync(db, me);
        return uid is int u ? q.Where(a => a.UserId == u) : q.Where(a => false);
    }

    internal static async Task<IQueryable<AuditLog>> ApplyAsync(IQueryable<AuditLog> q, Filters f,
        AppDbContext db, ScopingService sc)
    {
        if (ParseWhen(f.From, endOfDay: false) is DateTime from) q = q.Where(a => a.CreatedAt >= from);
        if (ParseWhen(f.To, endOfDay: true) is DateTime to) q = q.Where(a => a.CreatedAt < to);

        var actors = Split(f.Actor);
        if (actors.Count > 0) q = q.Where(a => actors.Contains(a.Actor));

        var sources = Split(f.Source);
        if (sources.Count > 0) q = q.Where(a => sources.Contains(a.Source));

        var action = (f.Action ?? "").Trim();
        if (action.EndsWith('*'))
        {
            var prefix = action.TrimEnd('*');
            if (prefix.Length > 0) q = q.Where(a => a.Action.StartsWith(prefix));
        }
        else if (action.Length > 0) q = q.Where(a => a.Action == action);

        var slug = (f.Entity ?? "").Trim();
        if (slug.Length > 0 && slug is not ("all" or "__all__"))
        {
            var ent = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Slug == slug);
            // An entity reads its own rows and every branch beneath it.
            var ids = ent == null ? new List<int>() : sc.EntitySubtreeIds(ent.Id);
            q = q.Where(a => a.EntityId != null && ids.Contains(a.EntityId.Value));
        }

        var text = (f.Q ?? "").Trim();
        if (text.Length > 0)
        {
            var like = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(a =>
                EF.Functions.Like(a.Actor, like, "\\") || EF.Functions.Like(a.Action, like, "\\")
                || EF.Functions.Like(a.Target, like, "\\") || EF.Functions.Like(a.Detail, like, "\\")
                || EF.Functions.Like(a.Path, like, "\\") || EF.Functions.Like(a.Ip, like, "\\")
                || EF.Functions.Like(a.RequestId, like, "\\"));
        }
        return q;
    }

    internal static async Task<List<Dictionary<string, object?>>> PayloadAsync(AppDbContext db,
        List<AuditLog> rows)
    {
        var entities = await EntityNamesAsync(db, rows);
        return rows.Select(r =>
        {
            entities.TryGetValue(r.EntityId ?? -1, out var e);
            return new Dictionary<string, object?>
            {
                ["id"] = r.Id,
                ["created_at"] = Utc(r.CreatedAt),
                ["actor"] = r.Actor,
                ["user_id"] = r.UserId,
                ["source"] = r.Source.Length > 0 ? r.Source : AuditSources.System,
                ["action"] = r.Action,
                ["target"] = r.Target,
                ["detail"] = r.Detail,
                ["entity_id"] = r.EntityId,
                ["entity"] = e.Slug,
                ["entity_name"] = e.Name,
                ["method"] = r.Method,
                ["path"] = r.Path,
                ["status"] = r.Status,
                ["ip"] = r.Ip,
                ["user_agent"] = r.UserAgent,
                ["request_id"] = r.RequestId,
            };
        }).ToList();
    }

    private static async Task<Dictionary<int, (string Slug, string Name)>> EntityNamesAsync(
        AppDbContext db, List<AuditLog> rows)
    {
        var ids = rows.Where(r => r.EntityId != null).Select(r => r.EntityId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new();
        return await db.Entities.AsNoTracking().Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => (e.Slug, e.Name));
    }

    // ---- helpers -------------------------------------------------------------

    private static IResult Denied() => Results.Json(new
    {
        detail = "The activity.view capability is required to read the activity log.",
    }, statusCode: 403);

    private static List<string> Split(string? raw) =>
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToList();

    /// <summary>ISO date or date-time; a bare date as <c>to</c> means "through that day".</summary>
    internal static DateTime? ParseWhen(string? raw, bool endOfDay)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return null;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return null;
        if (endOfDay && raw.Length <= 10) dt = dt.Date.AddDays(1);
        return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
    }

    private static string Utc(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>Always quoted; a leading = + - @ is defused so a spreadsheet never
    /// evaluates a logged value as a formula.</summary>
    private static string Csv(string? s)
    {
        s ??= "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
