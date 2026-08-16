using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data;

/// <summary>
/// Global ↔ entity resolution — the ONE place the binding rule lives, ported
/// from <c>crm/scoping.py</c>:
///
///     an entity-specific binding wins; the global one is the fallback.
///
/// Three verbs, and picking the wrong one is the bug this class exists to
/// prevent:
///   * <see cref="SettingValue"/>  — config: entity row → global row → "".
///   * <see cref="ScopeInherited"/> — libraries an entity INHERITS (its own PLUS global).
///   * <see cref="ScopeOwned"/>     — records that belong to exactly ONE entity.
///
/// <c>entityId == null</c> means "All entities" for the scope_* helpers and
/// "the global value" for the setting helpers. Every read is exception-safe:
/// a scoping failure degrades to the global value, never breaks a caller.
/// </summary>
public class ScopingService
{
    private readonly AppDbContext _db;
    public ScopingService(AppDbContext db) => _db = db;

    /// <summary>
    /// Setting keys an outlet may legitimately override. Anything NOT listed is
    /// organization-wide by design (credentials, webhook secrets, LLM prices…).
    /// Mirrors <c>scoping.ENTITY_SCOPED_SETTINGS</c> exactly.
    /// </summary>
    public static readonly IReadOnlySet<string> EntityScopedSettings = new HashSet<string>
    {
        "booking_hours",
        "aicore_persona",
        "quiet_start", "quiet_end",
        "queue_min_gap_s", "queue_max_gap_s", "queue_daily_cap",
        "queue_active_cap", "queue_active_window_h",
        "queue_call_gap_s", "queue_call_daily_cap",
        "ai_reply_mode", "reminder_ai_default", "ai_tool_use",
        "business_redirect", "business_redirect_target", "business_redirect_template",
        "recovery_mode", "recovery_template",
        "cold_text_guard", "cold_call_guard",
        "support_contact", "flow_call_voice", "email_subject",
    };

    public static bool IsEntityScoped(string? key) =>
        key != null && EntityScopedSettings.Contains(key);

    // ---- settings: entity row -> global row ---------------------------------

    /// <summary>
    /// The stored value governing <paramref name="key"/> for the entity, or ""
    /// when neither an override nor a global row has a non-blank value. A BLANK
    /// entity override falls through to the global row on purpose.
    /// </summary>
    public string SettingValue(string key, int? entityId = null)
    {
        try
        {
            if (entityId is int eid && IsEntityScoped(key))
            {
                var row = _db.Settings.AsNoTracking()
                    .FirstOrDefault(s => s.Key == key && s.EntityId == eid);
                if (row != null && !string.IsNullOrWhiteSpace(row.Value))
                    return row.Value.Trim();
            }
            var global = _db.Settings.AsNoTracking()
                .FirstOrDefault(s => s.Key == key && s.EntityId == null);
            if (global != null && !string.IsNullOrWhiteSpace(global.Value))
                return global.Value.Trim();
        }
        catch { /* degrade to "" — never break a send/webhook/tick path */ }
        return "";
    }

    /// <summary>Bulk SettingValue — same resolution, fewer queries.</summary>
    public Dictionary<string, string> SettingValues(IEnumerable<string> keys, int? entityId = null)
    {
        var wanted = keys.Select(k => k).ToList();
        var outv = new Dictionary<string, string>();
        try
        {
            foreach (var row in _db.Settings.AsNoTracking()
                         .Where(s => wanted.Contains(s.Key) && s.EntityId == null))
                if (!string.IsNullOrWhiteSpace(row.Value)) outv[row.Key] = row.Value.Trim();

            if (entityId is int eid)
            {
                var overridable = wanted.Where(IsEntityScoped).ToList();
                foreach (var row in _db.Settings.AsNoTracking()
                             .Where(s => overridable.Contains(s.Key) && s.EntityId == eid))
                    if (!string.IsNullOrWhiteSpace(row.Value)) outv[row.Key] = row.Value.Trim();
            }
        }
        catch { /* degrade to whatever resolved */ }
        return outv;
    }

    /// <summary>
    /// Provenance for the settings UI: "entity" when this entity overrides the
    /// key, "global" when it inherits a stored org value, "" when neither.
    /// </summary>
    public string SettingScope(string key, int? entityId = null)
    {
        try
        {
            if (entityId is int eid && IsEntityScoped(key) &&
                _db.Settings.Any(s => s.Key == key && s.EntityId == eid))
                return "entity";
            if (_db.Settings.Any(s => s.Key == key && s.EntityId == null))
                return "global";
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Write the global row (entityId == null) or an entity override. Overriding
    /// a key that is not entity-scoped is refused loudly — a programming error,
    /// not operator input (mirrors <c>set_setting</c>).
    /// </summary>
    public void SetSetting(string key, string? value, int? entityId = null)
    {
        if (entityId is int && !IsEntityScoped(key))
            throw new InvalidOperationException(
                $"Setting '{key}' is organization-wide and cannot be overridden per entity.");

        var row = _db.Settings.FirstOrDefault(s => s.Key == key && s.EntityId == entityId);
        if (row == null)
        {
            row = new Setting { Key = key, EntityId = entityId };
            _db.Settings.Add(row);
        }
        row.Value = (value ?? "").Trim();
        row.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();
    }

    /// <summary>Drop an entity's override so it inherits the global value again.</summary>
    public bool ClearSetting(string key, int? entityId)
    {
        if (entityId is not int eid) return false; // refuse to delete the global row
        var row = _db.Settings.FirstOrDefault(s => s.Key == key && s.EntityId == eid);
        if (row == null) return false;
        _db.Settings.Remove(row);
        _db.SaveChanges();
        return true;
    }

    // ---- querysets: inherited libraries vs entity-owned records --------------

    /// <summary>
    /// Libraries an entity inherits: its OWN rows PLUS the global ones.
    /// entityId == null (All entities) does not narrow.
    /// </summary>
    public IQueryable<T> ScopeInherited<T>(IQueryable<T> qs, int? entityId)
        where T : class, IEntityScoped
    {
        if (entityId is not int eid) return qs;
        return qs.Where(r => r.EntityId == null || r.EntityId == eid);
    }

    /// <summary>
    /// Records that belong to exactly one entity. entityId == null does not
    /// narrow. <paramref name="includeUnassigned"/> also returns global/no-entity
    /// rows (the inbox "unassigned" bucket).
    ///
    /// Scope flows UP the entity tree: an entity sees its own rows AND those of
    /// every entity beneath it, never its parent's. Head office reads a branch's
    /// conversations; the branch does not read head office's.
    /// </summary>
    public IQueryable<T> ScopeOwned<T>(IQueryable<T> qs, int? entityId, bool includeUnassigned = false)
        where T : class, IEntityScoped
    {
        if (entityId is not int eid) return qs;
        var ids = EntitySubtreeIds(eid);
        if (includeUnassigned)
            return qs.Where(r => r.EntityId == null || (r.EntityId != null && ids.Contains(r.EntityId.Value)));
        return qs.Where(r => r.EntityId != null && ids.Contains(r.EntityId.Value));
    }

    /// <summary>
    /// An entity plus every entity beneath it. Breadth-first with a depth cap so
    /// a cycle in the data (which the API refuses to create, but a hand-edited
    /// row could still hold) can never spin here.
    /// </summary>
    public List<int> EntitySubtreeIds(int entityId)
    {
        var ids = new List<int> { entityId };
        var frontier = new List<int> { entityId };
        for (var depth = 0; depth < 32 && frontier.Count > 0; depth++)
        {
            var next = _db.Entities.AsNoTracking()
                .Where(e => e.ParentId != null && frontier.Contains(e.ParentId.Value) && !ids.Contains(e.Id))
                .Select(e => e.Id).ToList();
            if (next.Count == 0) break;
            ids.AddRange(next);
            frontier = next;
        }
        return ids;
    }

    /// <summary>The subtrees of several entities, deduped (a user's whole scope).</summary>
    public List<int> EntitySubtreeIds(IEnumerable<int> entityIds)
    {
        var all = new HashSet<int>();
        foreach (var id in entityIds) all.UnionWith(EntitySubtreeIds(id));
        return all.ToList();
    }

    // ---- thread attribution -------------------------------------------------

    /// <summary>
    /// Which entity a conversation belongs to: the receiving account's entity,
    /// else the linked contact's entity (only when unambiguous), else null
    /// ("unassigned"). Ported from <c>scoping.thread_entity</c>.
    /// </summary>
    public int? ThreadEntity(ChannelAccount? account, Contact? contact)
    {
        try
        {
            if (account?.EntityId is int accEnt) return accEnt;
            if (contact != null)
            {
                var links = _db.ContactEntities.AsNoTracking()
                    .Where(l => l.ContactId == contact.Id)
                    .Select(l => l.EntityId).Take(2).ToList();
                if (links.Count == 1) return links[0];
                // Multi-entity contact on a shared number: no basis to choose.
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Recompute and store thread.EntityId. Call wherever a thread's account or
    /// contact link changes so the denormalized column cannot drift.
    /// </summary>
    public int? SyncThreadEntity(Thread thread, bool save = true)
    {
        try
        {
            var ent = ThreadEntity(thread.Account, thread.Contact);
            if (thread.EntityId != ent)
            {
                thread.EntityId = ent;
                thread.UpdatedAt = DateTime.UtcNow;
                if (save) _db.SaveChanges();
            }
            return ent;
        }
        catch { return null; }
    }
}
