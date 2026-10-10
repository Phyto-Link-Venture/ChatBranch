using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Tags;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The tag registry (<c>/api/tags</c>) and the per-target tag editors
/// (<c>/api/contacts/{id}/tags</c>, <c>/api/entities/{slug}/tags</c>).
///
/// Reads need <c>tags.view</c>, every write <c>tags.manage</c> — checked here,
/// server-side, against the same matrix the Permissions page edits. Tags are a
/// LIBRARY (ScopeInherited): an entity sees its own tags plus the
/// organization-wide ones. The rules themselves (group exclusivity, the
/// Contact.Tags cache, attribute validation) live in <see cref="TagService"/>.
/// </summary>
public static class TagsEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void MapTagsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- registry: list ------------------------------------------------------
        app.MapGet("/api/tags", async (string? applies_to, string? entity, ClaimsPrincipal me,
            AppDbContext db, ScopingService sc) =>
        {
            var q = VisibleTags(db, sc, me);
            var entityId = await InboxEndpoints.EntityId(db, entity);
            q = sc.ScopeInherited(q, entityId);
            var kind = (applies_to ?? "").Trim().ToLowerInvariant();
            if (kind.Length > 0) q = q.Where(t => t.AppliesTo == kind);
            var rows = await q.Include(t => t.Entity)
                .OrderBy(t => t.AppliesTo).ThenBy(t => t.Position).ThenBy(t => t.Name).ToListAsync();
            var usage = TagService.UsageCounts(db, rows);
            return Results.Ok(new
            {
                tags = rows.Select(t => Payload(t, usage.GetValueOrDefault(t.Id))).ToList(),
                palette = TagService.Palette,
                attribute_types = TagService.AttributeTypes,
                applies_to = TagService.AppliesToValues,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("tags.view");

        // ---- registry: create ----------------------------------------------------
        app.MapPost("/api/tags", async (JsonElement body, ClaimsPrincipal me, AppDbContext db,
            ScopingService sc, HttpContext ctx) =>
        {
            if (await Denied(db, me, "tags.manage") is IResult denied) return denied;
            var name = (Str(body, "name") ?? "").Trim();
            if (name.Length == 0) return Bad("name is required.");
            if (name.Length > 80) return Bad("name must be 80 characters or fewer.");
            var appliesTo = (Str(body, "applies_to") ?? TagService.ContactTarget).Trim().ToLowerInvariant();
            if (!TagService.AppliesToValues.Contains(appliesTo))
                return Bad($"applies_to must be one of {string.Join(", ", TagService.AppliesToValues)}.");

            var (entityId, entityErr) = await ResolveEntityAsync(db, me, Str(body, "entity"));
            if (entityErr != null) return entityErr;

            var color = (Str(body, "color") ?? "").Trim().ToLowerInvariant();
            if (color.Length == 0) color = TagService.PaletteFor(name.ToLowerInvariant());
            if (!TagService.Palette.Contains(color))
                return Bad($"color must be one of {string.Join(", ", TagService.Palette)}.");

            var (attrs, attrErr) = Attributes(body, appliesTo);
            if (attrErr != null) return Bad(attrErr);

            var lower = name.ToLower();
            if (await db.Tags.AnyAsync(t => t.AppliesTo == appliesTo && t.EntityId == entityId
                                            && t.Name.ToLower() == lower))
                return Conflict($"A {appliesTo} tag called '{name}' already exists here.");

            string slug;
            if (Str(body, "slug") is { Length: > 0 } wanted)
            {
                slug = TagService.Slugify(wanted);
                if (slug.Length == 0) return Bad("slug must contain letters or digits.");
                if (await db.Tags.AnyAsync(t => t.Slug == slug)) return Conflict($"The slug '{slug}' is taken.");
            }
            else slug = TagService.UniqueSlug(db, name);

            var row = new Tag
            {
                Name = name, Slug = slug, Color = color, AppliesTo = appliesTo,
                Description = (Str(body, "description") ?? "").Trim(),
                GroupName = Group(body),
                Attributes = attrs ?? new(),
                EntityId = entityId,
                Position = Int(body, "position")
                           ?? (await db.Tags.Where(t => t.AppliesTo == appliesTo)
                               .MaxAsync(t => (int?)t.Position) ?? -1) + 1,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Tags.Add(row);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "tag.create", $"{appliesTo}:{row.Slug}", row.Name);
            var fresh = await db.Tags.AsNoTracking().Include(t => t.Entity).FirstAsync(t => t.Id == row.Id);
            return Results.Json(Payload(fresh, 0), statusCode: 201);
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- registry: update ----------------------------------------------------
        app.MapMethods("/api/tags/{id:int}", new[] { "PATCH" }, async (int id, JsonElement body,
            ClaimsPrincipal me, AppDbContext db, ScopingService sc, HttpContext ctx) =>
        {
            if (await Denied(db, me, "tags.manage") is IResult denied) return denied;
            var row = await VisibleTags(db, sc, me, track: true).FirstOrDefaultAsync(t => t.Id == id);
            if (row == null) return NotFound("Tag not found.");
            if (LibraryScope.CheckExisting(db, sc, me, row.EntityId is int te ? new[] { te } : Array.Empty<int>()) is { } locked)
                return locked;
            var usage = TagService.UsageCounts(db, new[] { row }).GetValueOrDefault(row.Id);
            var changed = new List<string>();
            var renamed = false;

            if (Has(body, "applies_to"))
            {
                var appliesTo = (Str(body, "applies_to") ?? "").Trim().ToLowerInvariant();
                if (!TagService.AppliesToValues.Contains(appliesTo))
                    return Bad($"applies_to must be one of {string.Join(", ", TagService.AppliesToValues)}.");
                if (appliesTo != row.AppliesTo)
                {
                    if (usage > 0)
                        return Conflict($"'{row.Name}' is in use on {usage} target(s) — remove it from them before changing what it applies to.");
                    row.AppliesTo = appliesTo;
                    changed.Add("applies_to");
                }
            }
            if (Has(body, "entity"))
            {
                var (entityId, entityErr) = await ResolveEntityAsync(db, me, Str(body, "entity"));
                if (entityErr != null) return entityErr;
                row.EntityId = entityId;
                changed.Add("entity");
            }
            if (Has(body, "name"))
            {
                var name = (Str(body, "name") ?? "").Trim();
                if (name.Length == 0) return Bad("name cannot be empty.");
                if (name.Length > 80) return Bad("name must be 80 characters or fewer.");
                var lower = name.ToLower();
                if (await db.Tags.AnyAsync(t => t.Id != row.Id && t.AppliesTo == row.AppliesTo
                                                && t.EntityId == row.EntityId && t.Name.ToLower() == lower))
                    return Conflict($"A {row.AppliesTo} tag called '{name}' already exists here.");
                renamed = name != row.Name;
                row.Name = name;
                changed.Add("name");
            }
            if (Has(body, "color"))
            {
                var color = (Str(body, "color") ?? "").Trim().ToLowerInvariant();
                if (!TagService.Palette.Contains(color))
                    return Bad($"color must be one of {string.Join(", ", TagService.Palette)}.");
                row.Color = color;
                changed.Add("color");
            }
            if (Has(body, "description")) { row.Description = (Str(body, "description") ?? "").Trim(); changed.Add("description"); }
            if (Has(body, "group")) { row.GroupName = Group(body); changed.Add("group"); }
            if (Has(body, "attributes"))
            {
                var (attrs, attrErr) = Attributes(body, row.AppliesTo);
                if (attrErr != null) return Bad(attrErr);
                row.Attributes = attrs ?? new();
                changed.Add("attributes");
            }
            if (Int(body, "position") is int pos) { row.Position = pos; changed.Add("position"); }
            if (changed.Count == 0) return Bad("Nothing to update.");

            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            if (renamed && row.AppliesTo == TagService.ContactTarget)
                TagService.RefreshContactCache(db, await db.TagAssignments
                    .Where(a => a.TagId == row.Id && a.TargetType == TagService.ContactTarget)
                    .Select(a => a.TargetId).ToListAsync());
            Audit(db, me, ctx, "tag.update", $"{row.AppliesTo}:{row.Slug}", $"fields: {string.Join(", ", changed)}");
            var fresh = await db.Tags.AsNoTracking().Include(t => t.Entity).FirstAsync(t => t.Id == row.Id);
            return Results.Ok(Payload(fresh, usage));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- registry: delete ----------------------------------------------------
        // A tag in use answers 409 with its usage count; `?force=1` removes it from
        // every contact / entity / conversation that carries it.
        app.MapDelete("/api/tags/{id:int}", async (int id, string? force, ClaimsPrincipal me,
            AppDbContext db, ScopingService sc, HttpContext ctx) =>
        {
            if (await Denied(db, me, "tags.manage") is IResult denied) return denied;
            var row = await VisibleTags(db, sc, me, track: true).FirstOrDefaultAsync(t => t.Id == id);
            if (row == null) return NotFound("Tag not found.");
            if (LibraryScope.CheckExisting(db, sc, me, row.EntityId is int te ? new[] { te } : Array.Empty<int>()) is { } locked)
                return locked;
            var usage = TagService.UsageCounts(db, new[] { row }).GetValueOrDefault(row.Id);
            var forced = (force ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes";
            if (usage > 0 && !forced)
                return Results.Json(new
                {
                    detail = $"'{row.Name}' is applied to {usage} {Noun(row.AppliesTo, usage)} — " +
                             "delete anyway to remove it from all of them.",
                    usage,
                }, statusCode: 409);
            var label = $"{row.AppliesTo}:{row.Slug}";
            TagService.Delete(db, row);
            Audit(db, me, ctx, "tag.delete", label, usage > 0 ? $"removed from {usage}" : "");
            return Results.Ok(new { ok = true, removed_from = usage });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- who carries a tag (the binding view) ----------------------------------
        app.MapGet("/api/tags/{id:int}/targets", async (int id, ClaimsPrincipal me, AppDbContext db,
            ScopingService sc) =>
        {
            var tag = await VisibleTags(db, sc, me).Include(t => t.Entity)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (tag == null) return NotFound("Tag not found.");
            return Results.Ok(await TargetsPayloadAsync(db, tag));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("tags.view");

        // {add:[ids], remove:[ids]} — a DIFF, like /api/bindings, so a filtered
        // view can never unbind what it was not showing.
        app.MapPost("/api/tags/{id:int}/targets", async (int id, JsonElement body, ClaimsPrincipal me,
            AppDbContext db, ScopingService sc, HttpContext ctx) =>
        {
            if (await Denied(db, me, "tags.manage") is IResult denied) return denied;
            var tag = await VisibleTags(db, sc, me, track: true).Include(t => t.Entity)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (tag == null) return NotFound("Tag not found.");
            var add = IntList(body, "add");
            var remove = IntList(body, "remove");
            if (add.Intersect(remove).Any()) return Bad("The same id cannot be added and removed.");

            int added, removed;
            var actor = me.Identity?.Name ?? "system";
            if (tag.AppliesTo == TagService.ConversationTarget)
            {
                (added, removed) = TagService.ApplyConversationDiff(db, tag, add, remove);
            }
            else
            {
                var current = (await TargetIdsAsync(db, tag)).ToHashSet();
                var valid = tag.AppliesTo == TagService.ContactTarget
                    ? await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                        .Where(c => add.Contains(c.Id)).Select(c => c.Id).ToListAsync()
                    : (await AdminEndpoints.AccessibleEntitiesAsync(db, me))
                        .Where(e => add.Contains(e.Id)).Select(e => e.Id).ToList();
                var toAdd = add.Distinct().Where(i => valid.Contains(i) && !current.Contains(i)).ToList();
                foreach (var targetId in toAdd)
                    TagService.Assign(db, tag, tag.AppliesTo, targetId, new Dictionary<string, object?>(), actor);
                added = toAdd.Count;
                removed = TagService.Unassign(db, tag.Id, tag.AppliesTo, remove.Where(current.Contains));
            }
            Audit(db, me, ctx, "tag.targets", $"{tag.AppliesTo}:{tag.Slug}", $"+{added} / -{removed}");
            var payload = await TargetsPayloadAsync(db, tag);
            return Results.Ok(new { ok = true, added, removed, payload.target_ids, payload.items });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // Every assignment of one target type in a single read — the Entities
        // page shows each entity's tags without one request per row.
        app.MapGet("/api/tags/assignments", async (string? target_type, ClaimsPrincipal me,
            AppDbContext db, ScopingService sc) =>
        {
            var type = (target_type ?? TagService.EntityTarget).Trim().ToLowerInvariant();
            if (type is not (TagService.ContactTarget or TagService.EntityTarget))
                return Bad("target_type must be contact or entity.");
            var visible = await VisibleTags(db, sc, me).Select(t => t.Id).ToListAsync();
            var q = db.TagAssignments.AsNoTracking()
                .Where(a => a.TargetType == type && visible.Contains(a.TagId));
            if (type == TagService.EntityTarget)
            {
                var ids = (await AdminEndpoints.AccessibleEntitiesAsync(db, me)).Select(e => e.Id).ToList();
                q = q.Where(a => ids.Contains(a.TargetId));
            }
            else
            {
                var ids = ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc).Select(c => c.Id);
                q = q.Where(a => ids.Contains(a.TargetId));
            }
            var rows = await q.OrderBy(a => a.Id).ToListAsync();
            return Results.Ok(new
            {
                assignments = rows.Select(a => new
                {
                    tag_id = a.TagId, target_id = a.TargetId, values = a.Values,
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("tags.view");

        // ---- contact tags ----------------------------------------------------------
        app.MapGet("/api/contacts/{id:int}/tags", async (int id, ClaimsPrincipal me, AppDbContext db,
            ScopingService sc) =>
        {
            var contact = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound("Contact not found.");
            return Results.Ok(new
            {
                contact_id = id,
                tags = await AssignedPayloadAsync(db, TagService.ContactTarget, id),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("tags.view");

        app.MapPut("/api/contacts/{id:int}/tags", async (int id, JsonElement body, ClaimsPrincipal me,
            AppDbContext db, ScopingService sc, HttpContext ctx) =>
        {
            if (await Denied(db, me, "tags.manage") is IResult denied) return denied;
            var contact = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                .FirstOrDefaultAsync(c => c.Id == id);
            if (contact == null) return NotFound("Contact not found.");
            var (items, parseErr) = ParseItems(body);
            if (parseErr != null) return Bad(parseErr);
            var allowed = (await VisibleTags(db, sc, me).Select(t => t.Id).ToListAsync()).ToHashSet();
            var err = TagService.Replace(db, TagService.ContactTarget, id, items!, me.Identity?.Name ?? "", allowed);
            if (err != null) return Bad(err);
            Audit(db, me, ctx, "contact.tags", contact.Phone, $"{items!.Count} tag(s)");
            return Results.Ok(new
            {
                contact_id = id,
                tags = await AssignedPayloadAsync(db, TagService.ContactTarget, id),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- entity tags -----------------------------------------------------------
        // {slug} also accepts the numeric id: slugs are unique per tenant only.
        app.MapGet("/api/entities/{slug}/tags", async (string slug, ClaimsPrincipal me, AppDbContext db) =>
        {
            var entity = await FindEntityAsync(db, me, slug);
            if (entity == null) return NotFound("Entity not found.");
            return Results.Ok(new
            {
                entity = entity.Slug, entity_id = entity.Id,
                tags = await AssignedPayloadAsync(db, TagService.EntityTarget, entity.Id),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("tags.view");

        app.MapPut("/api/entities/{slug}/tags", async (string slug, JsonElement body, ClaimsPrincipal me,
            AppDbContext db, ScopingService sc, HttpContext ctx) =>
        {
            if (await Denied(db, me, "tags.manage") is IResult denied) return denied;
            var entity = await FindEntityAsync(db, me, slug);
            if (entity == null) return NotFound("Entity not found.");
            var (items, parseErr) = ParseItems(body);
            if (parseErr != null) return Bad(parseErr);
            var allowed = (await VisibleTags(db, sc, me).Select(t => t.Id).ToListAsync()).ToHashSet();
            var err = TagService.Replace(db, TagService.EntityTarget, entity.Id, items!, me.Identity?.Name ?? "", allowed);
            if (err != null) return Bad(err);
            Audit(db, me, ctx, "entity.tags", entity.Slug, $"{items!.Count} tag(s)");
            return Results.Ok(new
            {
                entity = entity.Slug, entity_id = entity.Id,
                tags = await AssignedPayloadAsync(db, TagService.EntityTarget, entity.Id),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // ---- scoping ------------------------------------------------------------------

    /// <summary>
    /// The tags this caller may see. Admin and superadmin: all. Agents and custom
    /// roles with assigned entities: organization-wide tags plus those of their
    /// entities' subtrees (an agent with no assignment keeps the tenant view).
    /// </summary>
    private static IQueryable<Tag> VisibleTags(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, bool track = false)
    {
        IQueryable<Tag> q = track ? db.Tags : db.Tags.AsNoTracking();
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        var assigned = ShellScope.ScopeRoots(db, me);   // empty for superadmin + org-wide admins
        if (assigned.Count == 0) return q;
        var ids = sc.EntitySubtreeIds(assigned);
        return q.Where(t => t.EntityId == null || ids.Contains(t.EntityId.Value));
    }

    /// <summary>"" / null → organization-wide; a slug must resolve to an entity the caller can see.</summary>
    private static async Task<(int?, IResult?)> ResolveEntityAsync(AppDbContext db, ClaimsPrincipal me, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "all" or "__all__")
            // organization-wide: not for a caller bound to entities (a company admin)
            return LibraryScope.MayGoGlobal(db, me)
                ? (null, null)
                : (null, Results.Json(new { detail = "Only an organization-wide admin can make an organization-wide tag — pick one of your entities." }, statusCode: 403));
        var entity = await FindEntityAsync(db, me, slug);
        return entity == null
            ? (null, Bad($"Unknown entity '{slug}'."))
            : (entity.Id, null);
    }

    private static async Task<Entity?> FindEntityAsync(AppDbContext db, ClaimsPrincipal me, string slugOrId)
    {
        var visible = await AdminEndpoints.AccessibleEntitiesAsync(db, me);
        return int.TryParse(slugOrId, out var id)
            ? visible.FirstOrDefault(e => e.Id == id)
            : visible.FirstOrDefault(e => e.Slug == slugOrId);
    }

    // ---- payloads -------------------------------------------------------------------

    private static object Payload(Tag t, int usage) => new
    {
        id = t.Id,
        name = t.Name,
        slug = t.Slug,
        color = t.Color,
        description = t.Description,
        applies_to = t.AppliesTo,
        group = t.GroupName,
        attributes = t.Attributes,
        position = t.Position,
        entity = t.Entity?.Slug,
        entity_id = t.EntityId,
        entity_name = t.Entity?.Name ?? "",
        usage,
        variable_prefix = t.AppliesTo == TagService.ConversationTarget
            ? "" : $"tag_{TagService.VariableSlug(t.Slug)}_",
        created_at = t.CreatedAt,
        updated_at = t.UpdatedAt,
    };

    private static async Task<List<int>> TargetIdsAsync(AppDbContext db, Tag tag) =>
        tag.AppliesTo == TagService.ConversationTarget
            ? TagService.ConversationTargets(db, tag.Slug)
            : await db.TagAssignments.AsNoTracking()
                .Where(a => a.TagId == tag.Id && a.TargetType == tag.AppliesTo)
                .Select(a => a.TargetId).ToListAsync();

    private record TargetsResult(object tag, string target_type, List<int> target_ids, List<object> items);

    private static async Task<TargetsResult> TargetsPayloadAsync(AppDbContext db, Tag tag)
    {
        var usage = TagService.UsageCounts(db, new[] { tag }).GetValueOrDefault(tag.Id);
        if (tag.AppliesTo == TagService.ConversationTarget)
        {
            var ids = TagService.ConversationTargets(db, tag.Slug);
            return new TargetsResult(Payload(tag, usage), tag.AppliesTo, ids,
                ids.Select(i => (object)new { target_id = i, values = new Dictionary<string, object?>() }).ToList());
        }
        var rows = await db.TagAssignments.AsNoTracking()
            .Where(a => a.TagId == tag.Id && a.TargetType == tag.AppliesTo).OrderBy(a => a.Id).ToListAsync();
        return new TargetsResult(Payload(tag, usage), tag.AppliesTo, rows.Select(r => r.TargetId).ToList(),
            rows.Select(r => (object)new { target_id = r.TargetId, values = r.Values, created_by = r.CreatedBy }).ToList());
    }

    private static async Task<List<object>> AssignedPayloadAsync(AppDbContext db, string targetType, int targetId)
    {
        var rows = await db.TagAssignments.AsNoTracking().Include(a => a.Tag)
            .Where(a => a.TargetType == targetType && a.TargetId == targetId && a.Tag != null)
            .ToListAsync();
        return rows.OrderBy(a => a.Tag!.Position).ThenBy(a => a.Tag!.Name)
            .Select(a => (object)new
            {
                tag_id = a.TagId,
                name = a.Tag!.Name,
                slug = a.Tag.Slug,
                color = a.Tag.Color,
                group = a.Tag.GroupName,
                description = a.Tag.Description,
                attributes = a.Tag.Attributes,
                values = a.Values,
                missing_required = a.Tag.Attributes
                    .Where(d => d.Required && (!a.Values.TryGetValue(d.Key, out var v)
                                               || TagService.FormatValue(v).Length == 0))
                    .Select(d => d.Label).ToList(),
                created_by = a.CreatedBy,
                created_at = a.CreatedAt,
            }).ToList();
    }

    private static string Noun(string appliesTo, int n) => appliesTo switch
    {
        TagService.ConversationTarget => n == 1 ? "conversation" : "conversations",
        TagService.EntityTarget => n == 1 ? "entity" : "entities",
        _ => n == 1 ? "contact" : "contacts",
    };

    // ---- body parsing ---------------------------------------------------------------

    private static (List<TagAttribute>? Attrs, string? Error) Attributes(JsonElement body, string appliesTo)
    {
        if (!Has(body, "attributes")) return (new List<TagAttribute>(), null);
        var el = body.GetProperty("attributes");
        if (el.ValueKind == JsonValueKind.Null) return (new List<TagAttribute>(), null);
        if (el.ValueKind != JsonValueKind.Array) return (null, "attributes must be a list of fields.");
        List<TagAttribute> attrs;
        try { attrs = JsonSerializer.Deserialize<List<TagAttribute>>(el.GetRawText(), Web) ?? new(); }
        catch { return (null, "attributes: malformed field list."); }
        if (attrs.Count > 0 && appliesTo == TagService.ConversationTarget)
            return (null, "Conversation tags cannot carry attributes.");
        if (attrs.Count > 30) return (null, "A tag can carry at most 30 fields.");
        var err = TagService.NormalizeAttributes(attrs);
        return err != null ? (null, err) : (attrs, null);
    }

    private static string Group(JsonElement body)
    {
        var g = (Str(body, "group") ?? "").Trim();
        return g.Length > 60 ? g[..60] : g;
    }

    /// <summary>{tags:[{tag_id, values}]} → (tag id, raw values) pairs.</summary>
    private static (List<(int TagId, Dictionary<string, object?>? Values)>? Items, string? Error) ParseItems(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("tags", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return (null, "Send {\"tags\": [{\"tag_id\": 1, \"values\": {}}]}.");
        var items = new List<(int, Dictionary<string, object?>?)>();
        foreach (var el in arr.EnumerateArray())
        {
            int? tagId = el.ValueKind switch
            {
                JsonValueKind.Number => el.TryGetInt32(out var n) ? n : null,
                JsonValueKind.Object => Int(el, "tag_id"),
                _ => null,
            };
            if (tagId is not int tid) return (null, "Every entry needs a tag_id.");
            Dictionary<string, object?>? values = null;
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("values", out var v)
                && v.ValueKind == JsonValueKind.Object)
                values = JsonSerializer.Deserialize<Dictionary<string, object?>>(v.GetRawText(), Web);
            if (items.Any(i => i.Item1 == tid)) continue;
            items.Add((tid, values));
        }
        return (items, null);
    }

    // ---- helpers ----------------------------------------------------------------------

    private static async Task<IResult?> Denied(AppDbContext db, ClaimsPrincipal me, string capability)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        return await AdminEndpoints.HasCapabilityAsync(db, role, capability)
            ? null
            : Results.Json(new { detail = $"The {capability} capability is required." }, statusCode: 403);
    }

    private static void Audit(AppDbContext db, ClaimsPrincipal me, HttpContext ctx,
        string action, string target, string detail)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = action, Target = target, Detail = detail,
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "", CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* an audit failure must never fail the operation itself */ }
    }

    private static bool Has(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out _);

    private static string? Str(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => v.ToString(),
            }
            : null;

    private static int? Int(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt32(out var n) ? n : null,
                JsonValueKind.String => int.TryParse(v.GetString(), out var n) ? n : null,
                _ => null,
            }
            : null;

    private static List<int> IntList(JsonElement b, string key)
    {
        var outv = new List<int>();
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(key, out var v)) return outv;
        if (v.ValueKind != JsonValueKind.Array) return outv;
        foreach (var el in v.EnumerateArray())
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) outv.Add(n);
            else if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var m)) outv.Add(m);
        return outv;
    }

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);
    private static IResult NotFound(string detail) => Results.Json(new { detail }, statusCode: 404);
    private static IResult Conflict(string detail) => Results.Json(new { detail }, statusCode: 409);
}
