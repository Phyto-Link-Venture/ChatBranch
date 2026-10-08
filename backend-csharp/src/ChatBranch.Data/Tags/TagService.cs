using System.Globalization;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Tags;

/// <summary>
/// The tag registry's rules, in ONE place, so the REST endpoints, the bindings
/// screen, the seed and the flow engine cannot drift apart:
///
///   * <b>Group exclusivity</b> — applying a tag that has a group removes the
///     target's other tags of that group (Inner company ⇄ Outside company).
///   * <b>Write-through cache</b> — <see cref="Contact.Tags"/> stays the
///     denormalised list of the contact's tag NAMES, rewritten on every
///     assignment change, so the existing readers (flow triggers/decisions,
///     automations, AI context, CSV export) keep working untouched. The
///     reverse seam (<see cref="ApplyContactNames"/>) lets the old writers
///     (CSV import, contact PATCH with `tags`) keep writing names: the registry
///     is made to agree, creating organization-wide tags for unknown names.
///   * <b>Conversation tags</b> keep living on <see cref="Thread.Labels"/> as
///     slugs (the inbox's storage); the registry is their vocabulary.
///
/// Everything is synchronous on purpose: SQLite is, and the seed needs it.
/// </summary>
public static class TagService
{
    public const string ContactTarget = "contact";
    public const string EntityTarget = "entity";
    public const string ConversationTarget = "conversation";

    public static readonly string[] AppliesToValues = { ContactTarget, EntityTarget, ConversationTarget };

    /// <summary>Palette KEYS — the dashboard maps each to theme tokens.</summary>
    public static readonly string[] Palette = { "tangerine", "ink", "teal", "plum", "ochre", "blue", "green", "rose" };

    public static readonly string[] AttributeTypes = { "text", "number", "date", "select", "boolean", "phone", "email" };

    private const string SeedMarker = "tags_registry_v1";

    // ---- naming --------------------------------------------------------------

    /// <summary>"Inner company" → "inner-company" (the URL/label form).</summary>
    public static string Slugify(string? value)
    {
        var sb = new StringBuilder();
        foreach (var ch in (value ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length > 60 ? s[..60].Trim('-') : s;
    }

    /// <summary>"Licence no." → "licence_no" (an attribute key, safe in {{variables}}).</summary>
    public static string AttributeKey(string? value) => Slugify(value).Replace('-', '_');

    /// <summary>The slug as it appears inside a template variable (dashes are not allowed there).</summary>
    public static string VariableSlug(string slug) => slug.Replace('-', '_');

    /// <summary>Slug derived from <paramref name="name"/>, suffixed until globally unique.</summary>
    public static string UniqueSlug(AppDbContext db, string name, int? excludeId = null)
    {
        var root = Slugify(name);
        if (root.Length == 0) root = "tag";
        var slug = root;
        for (var n = 2; db.Tags.Any(t => t.Slug == slug && (excludeId == null || t.Id != excludeId)); n++)
            slug = $"{root}-{n}";
        return slug;
    }

    public static string PaletteFor(string seed)
    {
        var h = 0;
        foreach (var ch in seed) h = (h * 31 + ch) & 0x7fffffff;
        return Palette[h % Palette.Length];
    }

    // ---- validation -------------------------------------------------------------

    /// <summary>Normalise attribute definitions in place; returns an error or null.</summary>
    public static string? NormalizeAttributes(List<TagAttribute> attrs)
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < attrs.Count; i++)
        {
            var a = attrs[i];
            a.Label = (a.Label ?? "").Trim();
            if (a.Label.Length == 0) return $"Field #{i + 1} needs a label.";
            a.Key = AttributeKey(string.IsNullOrWhiteSpace(a.Key) ? a.Label : a.Key);
            if (a.Key.Length == 0) return $"'{a.Label}' produces an empty key.";
            if (!seen.Add(a.Key)) return $"Two fields share the key '{a.Key}'.";
            a.Type = (a.Type ?? "text").Trim().ToLowerInvariant();
            if (!AttributeTypes.Contains(a.Type)) return $"'{a.Label}' has unknown type '{a.Type}'.";
            a.Options = (a.Options ?? new()).Select(o => (o ?? "").Trim()).Where(o => o.Length > 0)
                .Distinct().ToList();
            if (a.Type == "select" && a.Options.Count == 0)
                return $"'{a.Label}' is a dropdown and needs at least one option.";
            if (a.Type != "select") a.Options = new();
        }
        return null;
    }

    /// <summary>
    /// Validate and coerce attribute values against the tag's definitions:
    /// unknown keys are dropped, blanks omitted, numbers/booleans/dates parsed.
    /// </summary>
    public static (Dictionary<string, object?> Values, string? Error) CleanValues(Tag tag,
        Dictionary<string, object?>? raw, bool enforceRequired = true)
    {
        var outv = new Dictionary<string, object?>();
        raw ??= new();
        foreach (var a in tag.Attributes)
        {
            raw.TryGetValue(a.Key, out var v);
            var text = AsText(v);
            if (a.Type == "boolean")
            {
                if (AsBool(v) is bool b) outv[a.Key] = b;
                else if (enforceRequired && a.Required) return (outv, $"{tag.Name}: {a.Label} is required.");
                continue;
            }
            if (text.Length == 0)
            {
                if (enforceRequired && a.Required) return (outv, $"{tag.Name}: {a.Label} is required.");
                continue;
            }
            switch (a.Type)
            {
                case "number":
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                        return (outv, $"{tag.Name}: {a.Label} must be a number.");
                    outv[a.Key] = num;
                    break;
                case "date":
                    if (!DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        return (outv, $"{tag.Name}: {a.Label} must be a date (YYYY-MM-DD).");
                    outv[a.Key] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    break;
                case "select":
                    if (a.Options.Count > 0 && !a.Options.Contains(text))
                        return (outv, $"{tag.Name}: {a.Label} must be one of {string.Join(", ", a.Options)}.");
                    outv[a.Key] = text;
                    break;
                case "email":
                    if (!text.Contains('@') || text.StartsWith('@') || text.EndsWith('@'))
                        return (outv, $"{tag.Name}: {a.Label} must be an email address.");
                    outv[a.Key] = text;
                    break;
                default:
                    outv[a.Key] = text;
                    break;
            }
        }
        return (outv, null);
    }

    private static string AsText(object? v) => v switch
    {
        null => "",
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.String => (el.GetString() ?? "").Trim(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        },
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => (v.ToString() ?? "").Trim(),
    };

    private static bool? AsBool(object? v)
    {
        if (v is bool b) return b;
        if (v is JsonElement { ValueKind: JsonValueKind.True }) return true;
        if (v is JsonElement { ValueKind: JsonValueKind.False }) return false;
        return AsText(v).ToLowerInvariant() switch
        {
            "true" or "yes" or "1" or "on" => true,
            "false" or "no" or "0" or "off" => false,
            _ => null,
        };
    }

    /// <summary>A stored value rendered the way the dashboard shows it.</summary>
    public static string FormatValue(object? v) => v switch
    {
        null => "",
        JsonElement { ValueKind: JsonValueKind.True } => "Yes",
        JsonElement { ValueKind: JsonValueKind.False } => "No",
        bool b => b ? "Yes" : "No",
        _ => AsText(v),
    };

    // ---- assignments (contact / entity) --------------------------------------------

    /// <summary>
    /// Apply <paramref name="tag"/> to one target (or refresh its values when it
    /// is already there). Same-group tags on the target are removed first — the
    /// exclusivity rule. Returns the names of the tags that were displaced.
    /// Caller saves; contact caches are refreshed here.
    /// </summary>
    public static List<string> Assign(AppDbContext db, Tag tag, string targetType, int targetId,
        Dictionary<string, object?> values, string actor)
    {
        var displaced = new List<string>();
        var existing = db.TagAssignments.FirstOrDefault(a =>
            a.TagId == tag.Id && a.TargetType == targetType && a.TargetId == targetId);
        if (existing != null)
        {
            existing.Values = values;
        }
        else
        {
            if (tag.GroupName.Length > 0)
            {
                var group = tag.GroupName.ToLower();
                var rivals = db.TagAssignments.Include(a => a.Tag)
                    .Where(a => a.TargetType == targetType && a.TargetId == targetId && a.TagId != tag.Id
                                && a.Tag!.AppliesTo == tag.AppliesTo && a.Tag.GroupName.ToLower() == group)
                    .ToList();
                displaced.AddRange(rivals.Select(r => r.Tag!.Name));
                db.TagAssignments.RemoveRange(rivals);
            }
            db.TagAssignments.Add(new TagAssignment
            {
                TagId = tag.Id, TargetType = targetType, TargetId = targetId, Values = values,
                CreatedBy = actor, CreatedAt = DateTime.UtcNow,
            });
        }
        db.SaveChanges();
        if (targetType == ContactTarget) RefreshContactCache(db, new[] { targetId });
        return displaced;
    }

    /// <summary>Remove one tag from these targets. Returns how many were removed.</summary>
    public static int Unassign(AppDbContext db, int tagId, string targetType, IEnumerable<int> targetIds)
    {
        var ids = targetIds.Distinct().ToList();
        if (ids.Count == 0) return 0;
        var rows = db.TagAssignments
            .Where(a => a.TagId == tagId && a.TargetType == targetType && ids.Contains(a.TargetId)).ToList();
        db.TagAssignments.RemoveRange(rows);
        db.SaveChanges();
        if (targetType == ContactTarget) RefreshContactCache(db, rows.Select(r => r.TargetId));
        return rows.Count;
    }

    /// <summary>
    /// Replace a target's whole tag set (the contact/entity editors' PUT). Every
    /// tag must apply to this target type; two tags of one group is an error
    /// (the editor would otherwise silently drop one).
    /// </summary>
    public static string? Replace(AppDbContext db, string targetType, int targetId,
        List<(int TagId, Dictionary<string, object?>? Values)> items, string actor,
        IReadOnlySet<int>? allowedTagIds = null)
    {
        var ids = items.Select(i => i.TagId).Distinct().ToList();
        var tags = db.Tags.Where(t => ids.Contains(t.Id)).ToList();
        if (tags.Count != ids.Count) return "One or more tags do not exist.";
        if (allowedTagIds != null && ids.Any(id => !allowedTagIds.Contains(id)))
            return "One or more tags are outside your scope.";
        var wrong = tags.FirstOrDefault(t => t.AppliesTo != targetType);
        if (wrong != null) return $"'{wrong.Name}' is a {wrong.AppliesTo} tag, not a {targetType} tag.";
        var clash = tags.Where(t => t.GroupName.Length > 0)
            .GroupBy(t => t.GroupName.ToLowerInvariant()).FirstOrDefault(g => g.Count() > 1);
        if (clash != null)
            return $"Only one tag of the group '{clash.First().GroupName}' can be applied " +
                   $"({string.Join(" / ", clash.Select(t => t.Name))}).";

        var cleaned = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var (tagId, raw) in items)
        {
            var tag = tags.First(t => t.Id == tagId);
            var (values, err) = CleanValues(tag, raw);
            if (err != null) return err;
            cleaned[tagId] = values;
        }

        var current = db.TagAssignments
            .Where(a => a.TargetType == targetType && a.TargetId == targetId).ToList();
        db.TagAssignments.RemoveRange(current.Where(a => !cleaned.ContainsKey(a.TagId)));
        foreach (var (tagId, values) in cleaned)
        {
            var row = current.FirstOrDefault(a => a.TagId == tagId);
            if (row != null) row.Values = values;
            else
                db.TagAssignments.Add(new TagAssignment
                {
                    TagId = tagId, TargetType = targetType, TargetId = targetId, Values = values,
                    CreatedBy = actor, CreatedAt = DateTime.UtcNow,
                });
        }
        db.SaveChanges();
        if (targetType == ContactTarget) RefreshContactCache(db, new[] { targetId });
        return null;
    }

    /// <summary>Rewrite <see cref="Contact.Tags"/> from the registry for these contacts.</summary>
    public static void RefreshContactCache(AppDbContext db, IEnumerable<int> contactIds)
    {
        var ids = contactIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var names = db.TagAssignments.AsNoTracking()
            .Where(a => a.TargetType == ContactTarget && ids.Contains(a.TargetId)
                        && a.Tag!.AppliesTo == ContactTarget)
            .Select(a => new { a.TargetId, a.Tag!.Name, a.Tag.Position })
            .ToList();
        foreach (var contact in db.Contacts.Where(c => ids.Contains(c.Id)).ToList())
        {
            var next = names.Where(n => n.TargetId == contact.Id)
                .OrderBy(n => n.Position).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(n => n.Name).Distinct().ToList();
            if (next.SequenceEqual(contact.Tags)) continue;
            contact.Tags = next;
            contact.UpdatedAt = DateTime.UtcNow;
        }
        db.SaveChanges();
    }

    /// <summary>
    /// The reverse write-through: something wrote tag NAMES onto a contact (CSV
    /// import, contact PATCH with `tags`). Make the registry agree — reuse the
    /// contact tag with that name/slug, else create an organization-wide one —
    /// then rewrite the cache from the registry. Later names win a group clash.
    /// </summary>
    public static void ApplyContactNames(AppDbContext db, int contactId, IEnumerable<string>? names, string actor)
    {
        if (contactId <= 0) return;
        var wanted = new List<Tag>();
        foreach (var raw in names ?? Enumerable.Empty<string>())
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            var tag = FindOrCreateContactTag(db, name);
            if (wanted.Any(t => t.Id == tag.Id)) continue;
            if (tag.GroupName.Length > 0)
                wanted.RemoveAll(t => string.Equals(t.GroupName, tag.GroupName, StringComparison.OrdinalIgnoreCase));
            wanted.Add(tag);
        }
        var keep = wanted.Select(t => t.Id).ToHashSet();
        var current = db.TagAssignments
            .Where(a => a.TargetType == ContactTarget && a.TargetId == contactId).ToList();
        db.TagAssignments.RemoveRange(current.Where(a => !keep.Contains(a.TagId)));
        foreach (var tag in wanted.Where(t => current.All(a => a.TagId != t.Id)))
            db.TagAssignments.Add(new TagAssignment
            {
                TagId = tag.Id, TargetType = ContactTarget, TargetId = contactId,
                CreatedBy = actor, CreatedAt = DateTime.UtcNow,
            });
        db.SaveChanges();
        RefreshContactCache(db, new[] { contactId });
    }

    private static Tag FindOrCreateContactTag(AppDbContext db, string name)
    {
        var lower = name.ToLower();
        var slug = Slugify(name);
        var tag = db.Tags.Where(t => t.AppliesTo == ContactTarget && t.Name.ToLower() == lower)
                      .OrderBy(t => t.EntityId == null ? 0 : 1).ThenBy(t => t.Id).FirstOrDefault()
                  ?? (slug.Length > 0
                      ? db.Tags.Where(t => t.AppliesTo == ContactTarget && t.Slug == slug)
                          .OrderBy(t => t.Id).FirstOrDefault()
                      : null);
        if (tag != null) return tag;
        tag = new Tag
        {
            Name = name.Length > 80 ? name[..80] : name,
            Slug = UniqueSlug(db, name),
            AppliesTo = ContactTarget,
            Color = PaletteFor(name.ToLowerInvariant()),
            Position = (db.Tags.Where(t => t.AppliesTo == ContactTarget).Max(t => (int?)t.Position) ?? -1) + 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Tags.Add(tag);
        db.SaveChanges();
        return tag;
    }

    // ---- conversation tags (Thread.Labels) -------------------------------------------

    /// <summary>Ids of the threads carrying this conversation tag.</summary>
    public static List<int> ConversationTargets(AppDbContext db, string slug) =>
        db.Threads.AsNoTracking().Select(t => new { t.Id, t.Labels }).ToList()
            .Where(t => t.Labels.Contains(slug)).Select(t => t.Id).ToList();

    /// <summary>Add/remove a conversation tag on threads; group rivals are removed on add.</summary>
    public static (int Added, int Removed) ApplyConversationDiff(AppDbContext db, Tag tag,
        IEnumerable<int> add, IEnumerable<int> remove)
    {
        var addIds = add.Distinct().ToList();
        var removeIds = remove.Distinct().ToList();
        var rivals = tag.GroupName.Length == 0
            ? new List<string>()
            : db.Tags.AsNoTracking()
                .Where(t => t.AppliesTo == ConversationTarget && t.Id != tag.Id
                            && t.GroupName.ToLower() == tag.GroupName.ToLower())
                .Select(t => t.Slug).ToList();
        int added = 0, removed = 0;
        foreach (var thread in db.Threads.Where(t => addIds.Contains(t.Id) || removeIds.Contains(t.Id)).ToList())
        {
            var labels = new List<string>(thread.Labels);
            if (addIds.Contains(thread.Id) && !labels.Contains(tag.Slug))
            {
                labels.RemoveAll(rivals.Contains);
                labels.Add(tag.Slug);
                added++;
            }
            else if (removeIds.Contains(thread.Id) && labels.Remove(tag.Slug)) removed++;
            else continue;
            thread.Labels = labels;
            thread.UpdatedAt = DateTime.UtcNow;   // keeps the inbox SSE stream fresh
        }
        db.SaveChanges();
        return (added, removed);
    }

    // ---- usage + deletion ---------------------------------------------------------

    /// <summary>How many targets carry each tag.</summary>
    public static Dictionary<int, int> UsageCounts(AppDbContext db, IReadOnlyCollection<Tag> tags)
    {
        var ids = tags.Select(t => t.Id).ToList();
        var counts = db.TagAssignments.AsNoTracking().Where(a => ids.Contains(a.TagId))
            .GroupBy(a => a.TagId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionary(x => x.Key, x => x.N);
        var convo = tags.Where(t => t.AppliesTo == ConversationTarget).ToList();
        if (convo.Count > 0)
        {
            var labels = db.Threads.AsNoTracking().Select(t => t.Labels).ToList();
            foreach (var t in convo) counts[t.Id] = labels.Count(l => l.Contains(t.Slug));
        }
        return counts;
    }

    /// <summary>Delete a tag and everything that carries it (assignments, labels, caches).</summary>
    public static void Delete(AppDbContext db, Tag tag)
    {
        var rows = db.TagAssignments.Where(a => a.TagId == tag.Id).ToList();
        var contactIds = rows.Where(r => r.TargetType == ContactTarget).Select(r => r.TargetId).ToList();
        db.TagAssignments.RemoveRange(rows);
        if (tag.AppliesTo == ConversationTarget)
        {
            var ids = ConversationTargets(db, tag.Slug);
            foreach (var thread in db.Threads.Where(t => ids.Contains(t.Id)).ToList())
            {
                thread.Labels = thread.Labels.Where(l => l != tag.Slug).ToList();
                thread.UpdatedAt = DateTime.UtcNow;
            }
        }
        db.Tags.Remove(tag);
        db.SaveChanges();
        RefreshContactCache(db, contactIds);
    }

    // ---- template variables ----------------------------------------------------------

    /// <summary>
    /// Tag attributes as template variables: <c>{{tag_&lt;slug&gt;_&lt;key&gt;}}</c>
    /// (dashes in the slug become underscores). Every attribute of every
    /// contact/entity tag resolves — to "" when the contact does not carry the
    /// tag — so a template never leaks a raw placeholder. The contact's own
    /// tags win; tags on the contact's entities fill the rest.
    /// </summary>
    public static void MergeVariables(AppDbContext db, Dictionary<string, string> vars, Contact contact)
    {
        try
        {
            var tags = db.Tags.AsNoTracking()
                .Where(t => t.AppliesTo == ContactTarget || t.AppliesTo == EntityTarget).ToList();
            var withAttrs = tags.Where(t => t.Attributes.Count > 0).ToList();
            if (withAttrs.Count == 0) return;
            foreach (var t in withAttrs)
            foreach (var a in t.Attributes)
                vars[$"tag_{VariableSlug(t.Slug)}_{a.Key}"] = "";

            var entityIds = db.ContactEntities.AsNoTracking().Where(l => l.ContactId == contact.Id)
                .Select(l => l.EntityId).ToList();
            var assignments = db.TagAssignments.AsNoTracking()
                .Where(a => (a.TargetType == ContactTarget && a.TargetId == contact.Id)
                            || (a.TargetType == EntityTarget && entityIds.Contains(a.TargetId)))
                .ToList()
                // contact first so its values win over the entity's
                .OrderBy(a => a.TargetType == ContactTarget ? 1 : 0);
            foreach (var a in assignments)
            {
                var tag = withAttrs.FirstOrDefault(t => t.Id == a.TagId);
                if (tag == null) continue;
                foreach (var def in tag.Attributes)
                {
                    var text = a.Values.TryGetValue(def.Key, out var v) ? FormatValue(v) : "";
                    if (text.Length > 0) vars[$"tag_{VariableSlug(tag.Slug)}_{def.Key}"] = text;
                }
            }
        }
        catch
        {
            // tag variables are a nicety — never break a send
        }
    }

    // ---- seed -------------------------------------------------------------------------

    /// <summary>The conversation tag set the inbox shipped hard-coded (lib/tags.ts).</summary>
    public static readonly (string Slug, string Name, string Color)[] ConversationDefaults =
    {
        ("follow-up", "Follow-up needed", "green"),
        ("resolved", "Resolved", "green"),
        ("in-progress", "In progress", "teal"),
        ("urgent", "Urgent", "ochre"),
        ("dispute", "Dispute", "rose"),
        ("declined", "Declined", "rose"),
        ("callback", "Callback requested", "tangerine"),
        ("no-answer", "No answer", "ink"),
        ("wrong-number", "Wrong number", "ink"),
        ("escalate", "Escalate", "plum"),
    };

    /// <summary>
    /// One-time registry bootstrap (idempotent, marker-guarded): the inbox's
    /// conversation labels become registry tags, every existing free-text
    /// contact tag becomes a contact tag with an assignment, and — demo only —
    /// a few starter tags show what groups and attributes are for.
    /// </summary>
    public static void SeedRegistry(AppDbContext db, bool seedDemo)
    {
        if (db.Settings.Any(s => s.Key == SeedMarker && s.EntityId == null)) return;

        for (var i = 0; i < ConversationDefaults.Length; i++)
        {
            var (slug, name, color) = ConversationDefaults[i];
            if (db.Tags.Any(t => t.Slug == slug)) continue;
            db.Tags.Add(new Tag
            {
                Slug = slug, Name = name, Color = color, AppliesTo = ConversationTarget, Position = i,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
        db.SaveChanges();

        // existing free-text contact tags → registry tags + assignments
        foreach (var c in db.Contacts.AsNoTracking().Select(c => new { c.Id, c.Tags }).ToList())
            if (c.Tags.Count > 0) ApplyContactNames(db, c.Id, c.Tags, "system");

        if (seedDemo)
        {
            Starter(db, "Inner company", EntityTarget, "Company type", "teal",
                "Part of our own group.", new());
            Starter(db, "Outside company", EntityTarget, "Company type", "ochre",
                "A client, supplier or partner outside the group.", new()
                {
                    new() { Key = "contract_end", Label = "Contract end", Type = "date" },
                    new() { Key = "account_manager", Label = "Account manager", Type = "text" },
                });
            Starter(db, "Boss", ContactTarget, "Role", "plum", "Decision maker.", new());
            Starter(db, "Employee", ContactTarget, "Role", "blue", "Staff member.", new()
            {
                new() { Key = "employee_id", Label = "Employee ID", Type = "text" },
                new() { Key = "department", Label = "Department", Type = "text" },
            });
            Starter(db, "Driver", ContactTarget, "Role", "green", "Drives for us.", new()
            {
                new() { Key = "licence_no", Label = "Licence no.", Type = "text" },
                new() { Key = "vehicle_plate", Label = "Vehicle plate", Type = "text" },
            });
        }

        db.Settings.Add(new Setting { Key = SeedMarker, Value = "1", UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    private static void Starter(AppDbContext db, string name, string appliesTo, string group, string color,
        string description, List<TagAttribute> attrs)
    {
        var slug = Slugify(name);
        if (db.Tags.Any(t => t.Slug == slug)) return;
        db.Tags.Add(new Tag
        {
            Name = name, Slug = slug, AppliesTo = appliesTo, GroupName = group, Color = color,
            Description = description, Attributes = attrs,
            Position = db.Tags.Count(t => t.AppliesTo == appliesTo),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }
}
