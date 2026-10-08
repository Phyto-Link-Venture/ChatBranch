using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Audit;

/// <summary>
/// The one helper new code should use to record an action. Rows written here (or
/// by any older per-file helper) are stamped with request provenance by
/// <see cref="AuditStampInterceptor"/>; pass <c>source</c> only when the writer
/// is not the request's own actor (a background job, an automation, a flow).
/// </summary>
public static class AuditTrail
{
    /// <summary>Add a row to the context WITHOUT saving (the caller's save commits it).</summary>
    public static AuditLog Add(AppDbContext db, string action, string target = "", string detail = "",
        string? actor = null, string? source = null, int? entityId = null, int? userId = null)
    {
        var row = new AuditLog
        {
            Action = Cap(action, 120),
            Target = Cap(target, 300),
            Detail = Cap(detail, 1000),
            Actor = string.IsNullOrEmpty(actor) ? (AuditContext.Current?.Actor ?? "system") : actor,
            Source = source ?? "",
            EntityId = entityId,
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
        };
        db.AuditLogs.Add(row);
        return row;
    }

    /// <summary>Add + save. Never throws — an audit failure must not fail the action.</summary>
    public static void Write(AppDbContext db, string action, string target = "", string detail = "",
        string? actor = null, string? source = null, int? entityId = null, int? userId = null)
    {
        try
        {
            Add(db, action, target, detail, actor, source, entityId, userId);
            db.SaveChanges();
        }
        catch { /* never break the caller */ }
    }

    public static string Cap(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n] + "…";
    }

    /// <summary>
    /// Boot-time upkeep, called from Seed.UpgradeSchema: the paging index, and a
    /// NON-destructive backfill of rows written before provenance existed —
    /// blank sources become user/system/webhook/automation from the actor name,
    /// and a blank user id is resolved from the actor's username so old rows are
    /// visible to their tenant's admins. Only blanks are filled; idempotent.
    /// </summary>
    public static void UpgradeIndexesAndBackfill(AppDbContext db)
    {
        try
        {
            db.Database.ExecuteSqlRaw(
                "CREATE INDEX IF NOT EXISTS \"idx_auditlog_created_at\" ON \"crm_auditlog\" (\"created_at\")");
            db.Database.ExecuteSqlRaw(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"uniq_apikey_prefix\" ON \"crm_apikey\" (\"prefix\")");
            db.Database.ExecuteSqlRaw(
                "UPDATE \"crm_auditlog\" SET \"user_id\" = (SELECT u.\"id\" FROM \"auth_user\" u "
                + "WHERE u.\"username\" = \"crm_auditlog\".\"actor\") "
                + "WHERE \"user_id\" IS NULL AND \"source\" = '' "
                + "AND \"actor\" NOT IN ('', 'system', 'webhook', 'automation')");
            db.Database.ExecuteSqlRaw(
                "UPDATE \"crm_auditlog\" SET \"source\" = CASE "
                + "WHEN \"actor\" = 'webhook' THEN 'webhook' "
                + "WHEN \"actor\" = 'automation' THEN 'automation' "
                + "WHEN \"actor\" IN ('', 'system') THEN 'system' ELSE 'user' END "
                + "WHERE \"source\" = ''");
        }
        catch { /* upkeep must never block boot */ }
    }
}
