using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data;

/// <summary>An entity as a library payload names it.</summary>
public record EntityRef(int Id, string Slug, string Name);

/// <summary>
/// Reads and writes the "available to" set of a multi-entity library row
/// (<see cref="IMultiEntityItem"/>): its primary <c>EntityId</c> plus its extra
/// <see cref="LibraryEntity"/> ticks. The VISIBILITY rule is not here — it is
/// <see cref="ScopingService.LibraryVisible{T}"/>; this class only keeps the
/// storage invariant (primary first, no duplicate, a global row has no ticks).
/// </summary>
public static class LibraryEntities
{
    /// <summary>
    /// Every entity id the item is available to, primary first; empty = global.
    /// </summary>
    public static List<int> Ids(AppDbContext db, string kind, int itemId, int? primary)
    {
        var outv = new List<int>();
        if (primary is int p) outv.Add(p);
        try
        {
            outv.AddRange(db.LibraryEntities.AsNoTracking()
                .Where(l => l.Kind == kind && l.ItemId == itemId)
                .OrderBy(l => l.Id).Select(l => l.EntityId).ToList());
        }
        catch { /* the primary alone */ }
        return outv.Distinct().ToList();
    }

    /// <summary>
    /// Replace the item's set with <paramref name="entityIds"/> (order kept):
    /// the first becomes the primary <c>EntityId</c>, the rest tick rows. An
    /// empty list = global. <paramref name="primary"/> forces the primary (AI
    /// skills keep the scope they are edited in); it is added when missing.
    /// The caller saves (the item may be new — call after it has an id).
    /// </summary>
    public static void Set<T>(AppDbContext db, T item, IReadOnlyList<int> entityIds, int? primary = null)
        where T : class, IMultiEntityItem
    {
        var kind = LibraryKinds.For(typeof(T))
                   ?? throw new InvalidOperationException($"{typeof(T).Name} is not a multi-entity library type.");
        var ids = entityIds.Distinct().ToList();
        if (primary is int forced)
        {
            ids.Remove(forced);
            ids.Insert(0, forced);
        }
        var newPrimary = ids.Count > 0 ? ids[0] : (int?)null;
        SetPrimary(db, item, newPrimary);
        var extras = ids.Skip(1).ToList();
        var rows = db.LibraryEntities.Where(l => l.Kind == kind && l.ItemId == item.Id).ToList();
        db.LibraryEntities.RemoveRange(rows.Where(r => !extras.Contains(r.EntityId)));
        foreach (var e in extras.Where(e => rows.All(r => r.EntityId != e)))
            db.LibraryEntities.Add(new LibraryEntity
            {
                Kind = kind, ItemId = item.Id, EntityId = e, CreatedAt = DateTime.UtcNow,
            });
    }

    /// <summary>Forget every tick of a deleted item (the caller saves).</summary>
    public static void Clear(AppDbContext db, string kind, int itemId) =>
        db.LibraryEntities.RemoveRange(db.LibraryEntities.Where(l => l.Kind == kind && l.ItemId == itemId));

    /// <summary>Forget every tick of several deleted items (the caller saves).</summary>
    public static void Clear(AppDbContext db, string kind, IEnumerable<int> itemIds)
    {
        var ids = itemIds.ToList();
        if (ids.Count == 0) return;
        db.LibraryEntities.RemoveRange(db.LibraryEntities.Where(l => l.Kind == kind && ids.Contains(l.ItemId)));
    }

    /// <summary>
    /// An entity is being deleted: drop its ticks, and promote a remaining tick
    /// to primary on any row whose primary it was — so a row shared with another
    /// outlet never falls back to "global" by accident. The caller saves.
    /// </summary>
    public static void OnEntityDeleted(AppDbContext db, int entityId)
    {
        var ticks = db.LibraryEntities.Where(l => l.EntityId == entityId).ToList();
        db.LibraryEntities.RemoveRange(ticks);
        void Promote<T>(IQueryable<T> rows, string kind) where T : class, IMultiEntityItem
        {
            foreach (var row in rows.Where(r => r.EntityId == entityId).ToList())
            {
                var next = db.LibraryEntities
                    .Where(l => l.Kind == kind && l.ItemId == row.Id && l.EntityId != entityId)
                    .OrderBy(l => l.Id).FirstOrDefault();
                if (next == null) continue;
                SetPrimary(db, row, next.EntityId);
                db.LibraryEntities.Remove(next);
            }
        }
        Promote(db.CannedResponses, LibraryKinds.Canned);
        Promote(db.ScriptTemplates, LibraryKinds.Script);
        Promote(db.KnowledgeItems, LibraryKinds.Knowledge);
        Promote(db.AgentSkills, LibraryKinds.Skill);
        Promote(db.AutomationRules, LibraryKinds.Automation);
    }

    /// <summary>
    /// Bulk: item id → its entities (primary first) for a page of rows, in two
    /// queries. Empty list = global.
    /// </summary>
    public static Dictionary<int, List<EntityRef>> Load<T>(AppDbContext db, IEnumerable<T> items)
        where T : class, IMultiEntityItem
    {
        var list = items.ToList();
        var outv = new Dictionary<int, List<EntityRef>>();
        if (list.Count == 0) return outv;
        var kind = LibraryKinds.For(typeof(T))!;
        var itemIds = list.Select(i => i.Id).Distinct().ToList();
        List<LibraryEntity> ticks;
        try
        {
            ticks = db.LibraryEntities.AsNoTracking()
                .Where(l => l.Kind == kind && itemIds.Contains(l.ItemId)).OrderBy(l => l.Id).ToList();
        }
        catch { ticks = new List<LibraryEntity>(); }
        var entityIds = list.Where(i => i.EntityId != null).Select(i => i.EntityId!.Value)
            .Concat(ticks.Select(t => t.EntityId)).Distinct().ToList();
        var names = entityIds.Count == 0
            ? new Dictionary<int, EntityRef>()
            : db.Entities.AsNoTracking().Where(e => entityIds.Contains(e.Id))
                .Select(e => new EntityRef(e.Id, e.Slug, e.Name)).ToList().ToDictionary(e => e.Id);
        foreach (var item in list)
        {
            var refs = new List<EntityRef>();
            if (item.EntityId is int p && names.TryGetValue(p, out var pr)) refs.Add(pr);
            foreach (var t in ticks.Where(t => t.ItemId == item.Id))
                if (names.TryGetValue(t.EntityId, out var r) && refs.All(x => x.Id != r.Id)) refs.Add(r);
            outv[item.Id] = refs;
        }
        return outv;
    }

    /// <summary>The payload fields: <c>entities</c> [{id, slug, name}] (primary first) + <c>global</c>.</summary>
    public static object[] Payload(Dictionary<int, List<EntityRef>> loaded, int itemId) =>
        (loaded.TryGetValue(itemId, out var refs) ? refs : new List<EntityRef>())
        .Select(r => (object)new { id = r.Id, slug = r.Slug, name = r.Name }).ToArray();

    private static void SetPrimary<T>(AppDbContext db, T item, int? entityId) where T : class, IMultiEntityItem
    {
        if (item.EntityId == entityId) return;
        // EntityId has a public setter on every library type; the interface only exposes the getter
        var prop = typeof(T).GetProperty(nameof(IEntityScoped.EntityId))!;
        prop.SetValue(item, entityId);
        // the navigation would otherwise win over the FK on save
        var nav = typeof(T).GetProperty("Entity");
        if (nav != null && nav.GetValue(item) is Entity e && e.Id != entityId) nav.SetValue(item, null);
    }
}
