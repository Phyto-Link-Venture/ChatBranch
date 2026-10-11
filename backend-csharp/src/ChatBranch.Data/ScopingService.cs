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
///   * <see cref="SettingValue"/>  — config: the NEAREST level wins — the entity's
///     own row → its parent's → … → the global row → "".
///   * <see cref="ScopeInherited"/> — libraries an entity INHERITS: its own PLUS
///     every ancestor's PLUS the global ones (they add up down the tree).
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
        // AI monitor + human-like behaviour (Ai/AiBehaviourSettings.cs)
        "ai_review_mode", "ai_review_autosend_minutes", "ai_review_first_n",
        "ai_review_keywords", "ai_review_notify",
        "ai_wait_seconds", "ai_wait_max_seconds",
        "ai_typing", "ai_typing_cps", "ai_typing_min_s", "ai_typing_max_s",
        "ai_split", "ai_split_max_chars", "ai_ack_skip", "ai_ack_text",
        "ai_followup", "ai_followup_first_hours", "ai_followup_between_hours",
        "ai_followup_max", "ai_followup_template", "ai_followup_guidance",
        "ai_emoji_style", "ai_reactions", "ai_quote_replies", "ai_voice_replies", "ai_voice_id",
        // AI learning: how feedback becomes lessons (Ai/Learning/LearningService.cs)
        "ai_learning_mode",
        // AI master switch + schedule + never-AI tags (Ai/AiControls.cs)
        "ai_enabled", "ai_schedule", "ai_schedule_mode", "ai_never_tags",
        // House rules: lines that ADD UP down the tree (SettingValuesDownTree)
        "ai_house_rules",
    };

    public static bool IsEntityScoped(string? key) =>
        key != null && EntityScopedSettings.Contains(key);

    // ---- the entity tree ------------------------------------------------------

    /// <summary>
    /// The entity and its ancestors, NEAREST first (the entity itself, its
    /// parent, … the top-level entity). Cycle-safe and depth-capped.
    /// </summary>
    public List<int> EntityChain(int? entityId)
    {
        var chain = new List<int>();
        if (entityId is not int start) return chain;
        try
        {
            var cur = (int?)start;
            while (cur is int id && !chain.Contains(id) && chain.Count < 16)
            {
                var row = _db.Entities.AsNoTracking().Where(e => e.Id == id)
                    .Select(e => new { e.ParentId }).FirstOrDefault();
                if (row == null) break;
                chain.Add(id);
                cur = row.ParentId;
            }
        }
        catch { /* degrade to what was walked */ }
        if (chain.Count == 0) chain.Add(start);
        return chain;
    }

    // ---- settings: nearest level wins ------------------------------------------

    /// <summary>
    /// The stored value governing <paramref name="key"/> for the entity: the
    /// nearest level of the tree that sets it (the entity's own row, then its
    /// parent's, … up to the top-level entity), else the global row, else "".
    /// A BLANK override falls through to the level above on purpose.
    /// </summary>
    public string SettingValue(string key, int? entityId = null) => SettingSource(key, entityId).Value;

    /// <summary>
    /// <see cref="SettingValue"/> plus WHERE the value came from: the entity id of
    /// the level that set it, null for the global row (or nothing stored).
    /// </summary>
    public (string Value, int? At) SettingSource(string key, int? entityId = null)
    {
        try
        {
            var chain = IsEntityScoped(key) ? EntityChain(entityId) : new List<int>();
            var rows = _db.Settings.AsNoTracking()
                .Where(s => s.Key == key && (s.EntityId == null || chain.Contains(s.EntityId.Value)))
                .Select(s => new { s.EntityId, s.Value }).ToList();
            foreach (var level in chain)
            {
                var row = rows.FirstOrDefault(r => r.EntityId == level);
                if (row != null && !string.IsNullOrWhiteSpace(row.Value)) return (row.Value.Trim(), level);
            }
            var global = rows.FirstOrDefault(r => r.EntityId == null);
            if (global != null && !string.IsNullOrWhiteSpace(global.Value)) return (global.Value.Trim(), null);
        }
        catch { /* degrade to "" — never break a send/webhook/tick path */ }
        return ("", null);
    }

    /// <summary>
    /// Every level's non-blank value of an ADDITIVE key, organization first and
    /// the entity itself last: (entity id — null = the organization, value).
    /// House rules use it: a branch keeps head office's rules and adds its own.
    /// </summary>
    public List<(int? At, string Value)> SettingValuesDownTree(string key, int? entityId)
    {
        var outv = new List<(int? At, string Value)>();
        try
        {
            var chain = IsEntityScoped(key) ? EntityChain(entityId) : new List<int>();
            var rows = _db.Settings.AsNoTracking()
                .Where(s => s.Key == key && (s.EntityId == null || chain.Contains(s.EntityId.Value)))
                .Select(s => new { s.EntityId, s.Value }).ToList();
            var global = rows.FirstOrDefault(r => r.EntityId == null);
            if (global != null && !string.IsNullOrWhiteSpace(global.Value)) outv.Add((null, global.Value.Trim()));
            foreach (var level in Enumerable.Reverse(chain))
            {
                var row = rows.FirstOrDefault(r => r.EntityId == level);
                if (row != null && !string.IsNullOrWhiteSpace(row.Value)) outv.Add((level, row.Value.Trim()));
            }
        }
        catch { /* degrade to what was read */ }
        return outv;
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

            if (entityId is int)
            {
                var overridable = wanted.Where(IsEntityScoped).ToList();
                var chain = EntityChain(entityId);
                var rows = _db.Settings.AsNoTracking()
                    .Where(s => overridable.Contains(s.Key) && s.EntityId != null && chain.Contains(s.EntityId.Value))
                    .ToList();
                // farthest ancestor first, so the nearest level is written last and wins
                foreach (var level in Enumerable.Reverse(chain))
                    foreach (var row in rows.Where(r => r.EntityId == level))
                        if (!string.IsNullOrWhiteSpace(row.Value)) outv[row.Key] = row.Value.Trim();
            }
        }
        catch { /* degrade to whatever resolved */ }
        return outv;
    }

    /// <summary>
    /// Provenance for the settings UI: "entity" when this entity overrides the
    /// key, "parent" when it inherits an ancestor entity's value, "global" when
    /// it inherits a stored org value, "" when neither.
    /// </summary>
    public string SettingScope(string key, int? entityId = null)
    {
        try
        {
            if (entityId is int eid && IsEntityScoped(key))
            {
                var (value, at) = SettingSource(key, eid);
                if (at == eid) return "entity";
                if (at != null && value.Length > 0) return "parent";
            }
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
    /// Libraries an entity inherits: its OWN rows PLUS every ancestor's (a branch
    /// uses head office's knowledge, skills, templates…) PLUS the global ones.
    /// Never a sibling's or a child's. entityId == null (All entities) does not narrow.
    /// </summary>
    public IQueryable<T> ScopeInherited<T>(IQueryable<T> qs, int? entityId)
        where T : class, IEntityScoped
    {
        if (entityId is not int eid) return qs;
        // a multi-selection (EntitySelection) = what ANY ticked entity inherits
        var chain = EntitySelection.Covers(eid)
            ? EntitySelection.Current!.SelectMany(id => EntityChain(id)).Distinct().ToList()
            : EntityChain(eid);
        return LibraryVisible(qs, chain);
    }

    /// <summary>
    /// THE multi-entity library rule, in one place: a row is visible to a set of
    /// entity ids when it is GLOBAL (no entity), its primary
    /// <see cref="IEntityScoped.EntityId"/> is in the set, or — for the
    /// multi-entity library kinds (<see cref="LibraryKinds"/>) — one of its extra
    /// "available to" ticks (<c>crm_libraryentity</c>) is. Callers pass the set an
    /// outlet may read: <see cref="ScopeInherited"/> passes the outlet's chain
    /// (itself + ancestors — so a tick for head office reaches its branches, and
    /// a sibling outside the ticks never sees the row), ShellScope passes an
    /// assigned user's visible ids. A legacy row with only EntityId (no tick rows)
    /// behaves exactly as before.
    /// </summary>
    public IQueryable<T> LibraryVisible<T>(IQueryable<T> qs, IReadOnlyCollection<int> entityIds)
        where T : class, IEntityScoped
    {
        var ids = entityIds.Distinct().ToList();
        System.Linq.Expressions.Expression<Func<T, bool>> basePred =
            r => r.EntityId == null || ids.Contains(r.EntityId.Value);
        var kind = LibraryKinds.For(typeof(T));
        if (kind == null || ids.Count == 0) return qs.Where(basePred);
        List<int> extra;
        try
        {
            extra = _db.LibraryEntities.AsNoTracking()
                .Where(l => l.Kind == kind && ids.Contains(l.EntityId))
                .Select(l => l.ItemId).Distinct().ToList();
        }
        catch { extra = new List<int>(); } // a table hiccup degrades to the primary entity only
        if (extra.Count == 0) return qs.Where(basePred);
        var p = basePred.Parameters[0];
        var holder = new ExtraIds { Ids = extra };
        var contains = System.Linq.Expressions.Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains),
            new[] { typeof(int) },
            System.Linq.Expressions.Expression.Property(
                System.Linq.Expressions.Expression.Constant(holder), nameof(ExtraIds.Ids)),
            System.Linq.Expressions.Expression.Property(p, "Id"));
        var body = System.Linq.Expressions.Expression.OrElse(basePred.Body, contains);
        return qs.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, p));
    }

    /// <summary>Closure-like holder so EF parameterizes the id list instead of inlining it.</summary>
    private sealed class ExtraIds { public List<int> Ids { get; init; } = new(); }

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
        var ids = PickSubtree(eid);
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

    /// <summary>
    /// The subtree of the entity a request PICKED (<c>?entity=</c>): its own subtree, or —
    /// when it is one of several ticked entities (<see cref="EntitySelection"/>) — the union
    /// of every ticked entity's subtree. Use it only for a picked filter, never for an
    /// assignment or a permission check.
    /// </summary>
    public List<int> PickSubtree(int entityId) =>
        EntitySelection.Covers(entityId) ? EntitySubtreeIds(EntitySelection.Current!) : EntitySubtreeIds(entityId);

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
