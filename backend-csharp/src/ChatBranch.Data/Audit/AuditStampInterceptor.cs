using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ChatBranch.Data.Audit;

/// <summary>
/// Stamps every NEW <see cref="AuditLog"/> row with provenance on save: the
/// source (user / api / mcp / assistant / …), the acting user's id, and the
/// request's method, path, IP, user agent and id — taken from the ambient
/// <see cref="AuditContext"/>. Only blanks are filled: a writer that sets a
/// field explicitly (a background job saying source="automation") always wins.
///
/// Registered once in <c>AppDbContext.OnConfiguring</c>, so the existing
/// per-endpoint audit helpers gain all of this with no edits.
/// </summary>
public sealed class AuditStampInterceptor : SaveChangesInterceptor
{
    public static readonly AuditStampInterceptor Instance = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Stamp(DbContext? db)
    {
        if (db == null) return;
        List<AuditLog> added;
        try
        {
            added = db.ChangeTracker.Entries<AuditLog>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity).ToList();
        }
        catch { return; }
        if (added.Count == 0) return;

        var ctx = AuditContext.Current;
        foreach (var row in added)
        {
            if (row.CreatedAt == default) row.CreatedAt = DateTime.UtcNow;
            if (ctx == null)
            {
                if (string.IsNullOrEmpty(row.Source)) row.Source = AuditSources.System;
                continue;
            }
            if (string.IsNullOrEmpty(row.Source)) row.Source = ctx.Source;
            // The acting user is only inferred for rows written AS that user —
            // an automation row written during a request names "automation".
            if (row.UserId == null && ctx.UserId != null
                && (string.IsNullOrEmpty(row.Actor) || row.Actor == "system" || row.Actor == ctx.Actor)
                && row.Source == ctx.Source)
            {
                row.UserId = ctx.UserId;
                if (string.IsNullOrEmpty(row.Actor) || row.Actor == "system") row.Actor = ctx.Actor;
            }
            if (row.EntityId == null && ctx.EntityId != null && row.Source == ctx.Source)
                row.EntityId = ctx.EntityId;
            if (string.IsNullOrEmpty(row.Method)) row.Method = ctx.Method;
            if (string.IsNullOrEmpty(row.Path)) row.Path = ctx.Path;
            if (string.IsNullOrEmpty(row.Ip)) row.Ip = ctx.Ip;
            if (string.IsNullOrEmpty(row.UserAgent)) row.UserAgent = ctx.UserAgent;
            if (string.IsNullOrEmpty(row.RequestId)) row.RequestId = ctx.RequestId;
            ctx.Track(row);
        }
    }
}
