using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Integrations;

/// <summary>
/// Which connection answers for a provider, in one place.
///
/// Resolution order is <b>the acting user's own grant → the entity's shared
/// grant → its parent entities' (up the tree) → the organization-wide one</b>.
/// That is what makes "different people, different Google Calendars" work: a
/// user who connected their own calendar books against it, and everyone else
/// falls back to the shared account rather than failing. A branch inherits head
/// office's connection; head office never borrows a branch's.
/// </summary>
public class IntegrationResolver
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;

    public IntegrationResolver(AppDbContext db, ScopingService scoping)
    {
        _db = db;
        _scoping = scoping;
    }

    /// <summary>
    /// The connection to use, or null when nothing is connected. Only rows that
    /// actually carry credentials are considered — an empty row is a placeholder
    /// the operator started and abandoned, not a usable grant.
    /// </summary>
    public Integration? Active(string provider, int? entityId = null, int? userId = null)
    {
        var rows = _db.Integrations
            .Where(i => i.Provider == provider)
            .ToList()
            .Where(i => i.Credentials.Count > 0)
            .ToList();
        if (rows.Count == 0) return null;

        // 1. this user's own grant (in their entity, else anywhere they own)
        if (userId is int uid)
        {
            var own = rows.FirstOrDefault(i => i.OwnerUserId == uid && i.EntityId == entityId)
                      ?? rows.FirstOrDefault(i => i.OwnerUserId == uid);
            if (own != null) return own;
        }
        // 2. the entity's shared grant, then its parent's, up to the top of the tree —
        //    a branch inherits head office's connection, never the other way round
        if (entityId is int eid)
            foreach (var level in AncestorChain(eid))
            {
                var shared = rows.FirstOrDefault(i => i.OwnerUserId == null && i.EntityId == level);
                if (shared != null) return shared;
            }
        // 3. the organization-wide connection
        return rows.FirstOrDefault(i => i.OwnerUserId == null && i.EntityId == null);
    }

    /// <summary>The entity, then its parent, grandparent… (bounded, cycle-safe).</summary>
    public List<int> AncestorChain(int entityId)
    {
        var chain = new List<int>();
        int? current = entityId;
        while (current is int id && chain.Count < 32 && !chain.Contains(id))
        {
            chain.Add(id);
            current = _db.Entities.AsNoTracking().Where(e => e.Id == id).Select(e => e.ParentId).FirstOrDefault();
        }
        return chain;
    }

    /// <summary>
    /// The SHARED row a connect flow writes to: no owner, for one entity (and,
    /// through <see cref="Active"/>, its branches) or the whole organization
    /// (<paramref name="entityId"/> null). Created empty when missing.
    /// </summary>
    public async Task<Integration> SharedRowAsync(string provider, int? entityId, CancellationToken ct = default)
    {
        var row = await _db.Integrations.FirstOrDefaultAsync(
            i => i.Provider == provider && i.EntityId == entityId && i.OwnerUserId == null, ct);
        if (row != null) return row;
        row = new Integration
        {
            Provider = provider,
            EntityId = entityId,
            OwnerUserId = null,
            Status = "unknown",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Integrations.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// The row a connect flow should write to: this user's own row for the
    /// entity, created empty when it does not exist yet. Ownerless rows are
    /// never reused here — that is what used to let one person's tokens
    /// overwrite another's.
    /// </summary>
    public async Task<Integration> OwnRowAsync(string provider, int? entityId, int? userId,
        CancellationToken ct = default)
    {
        var row = await _db.Integrations.FirstOrDefaultAsync(
            i => i.Provider == provider && i.EntityId == entityId && i.OwnerUserId == userId, ct);
        if (row != null) return row;
        row = new Integration
        {
            Provider = provider,
            EntityId = entityId,
            OwnerUserId = userId,
            Status = "unknown",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Integrations.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }
}
