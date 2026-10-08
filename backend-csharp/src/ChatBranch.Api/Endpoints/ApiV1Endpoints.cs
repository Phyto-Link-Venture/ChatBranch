using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The /api/v1 surface (Django DRF twin) + the bulk record-sync pipeline. Uses
/// the same bearer token. "Brands" map to ingest.Entity rows (the outlet model
/// this port standardized on). Tenant scoping comes from the caller's claim.
/// </summary>
public static class ApiV1Endpoints
{
    public record BrandWrite(string Name, string Slug);
    public record SyncContact(string? Phone, string? Name, string? Email);
    public record SyncRecord(string ExternalId, string? Type, string? Title, string? Status,
        decimal? Amount, DateOnly? DueDate, SyncContact? Contact);
    public record SyncRequest(List<SyncRecord> Records);

    public static void MapApiV1Endpoints(this IEndpointRouteBuilder app)
    {
        // ---- GET /api/v1/whoami -----------------------------------------------
        // The identity + SCOPE probe (api/views.py WhoAmIView). Two consumers
        // depend on the resolved shape, not the raw claim:
        //   * EntityContext (the topbar entity switcher) intersects
        //     `entities` — accessible entity SLUGS — with /v1/entities, so an
        //     agent's switcher lists only what they may see;
        //   * EntitiesSettings reads `tenant` — the tenant SLUG — to preselect
        //     the tenant on create.
        // A raw `tenantId` claim satisfied neither (the frontend compares
        // slugs), so the numeric id is deliberately not echoed here.
        app.MapGet("/api/v1/whoami", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            var username = me.Identity?.Name ?? "";
            string? tenant = null;
            var entities = new List<string>();
            var capabilities = new List<string>();
            try
            {
                if (TenantId(me) is int tid)
                    tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tid)
                        .Select(t => t.Slug).FirstOrDefaultAsync();
                entities = (await AdminEndpoints.AccessibleEntitiesAsync(db, me))
                    .Select(e => e.Slug).ToList();
                capabilities = await AdminEndpoints.CapabilitiesForAsync(db, role);
            }
            catch (Exception)
            {
                // Unseeded/unmigrated sandbox DB: identity still resolves from
                // the claims, scope degrades to empty. whoami is the first call
                // every page makes — it must never 500 the whole dashboard.
            }
            return Results.Ok(new
            {
                username,
                // auth_user has no first_name column in this port (see
                // ManagedUserPayload) — name falls back to the username.
                name = username,
                role,
                tenant,
                entities,
                capabilities,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- brands (= entities) ----------------------------------------------
        app.MapGet("/api/v1/brands", async (ClaimsPrincipal me, AppDbContext db) =>
        {
            var tenantId = TenantId(me);
            var q = db.Entities.AsNoTracking().AsQueryable();
            if (tenantId is int tid) q = q.Where(e => e.TenantId == tid);
            var rows = await q.OrderBy(e => e.Name).ToListAsync();
            return Results.Ok(new { brands = rows.Select(e => new
            {
                id = e.Id, name = e.Name, slug = e.Slug, tenantId = e.TenantId,
            }) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/v1/brands", async (BrandWrite b, ClaimsPrincipal me, AppDbContext db) =>
        {
            var tenantId = TenantId(me);
            if (tenantId is not int tid)
                return Results.Json(new { detail = "no tenant in scope" }, statusCode: 400);
            if (db.Entities.Any(e => e.TenantId == tid && e.Slug == b.Slug))
                return Results.Json(new { detail = "duplicate slug" }, statusCode: 409);
            var row = new Entity { TenantId = tid, Name = b.Name, Slug = b.Slug,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Entities.Add(row);
            await db.SaveChangesAsync();
            return Results.Ok(new { id = row.Id, name = row.Name, slug = row.Slug });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- contacts (brand-scoped) ------------------------------------------
        app.MapGet("/api/v1/contacts", async (string? q, AppDbContext db) =>
        {
            var query = db.Contacts.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(c => c.Name.Contains(q) || c.Phone.Contains(q));
            var rows = await query.OrderByDescending(c => c.UpdatedAt).Take(200).ToListAsync();
            return Results.Ok(new { contacts = rows.Select(Dtos.ContactDto) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- POST /api/v1/contacts --------------------------------------------
        // The Contacts page's "Add contact" (api/views.py ContactsView.post). This
        // was never ported, so the dashboard's create button 405'd against a
        // GET-only route and silently did nothing. `entity_slugs` binds the new
        // contact to zero or more entities in the same call.
        app.MapPost("/api/v1/contacts", async (JsonElement body, ClaimsPrincipal me,
            AppDbContext db) =>
        {
            var name = (Str(body, "name") ?? "").Trim();
            var phone = (Str(body, "phone") ?? "").Trim();
            if (name.Length == 0 || phone.Length == 0)
                return V1Error("bad_request", "name and phone are required.", 400);

            var language = (Str(body, "language") ?? "en").Trim().ToLowerInvariant();
            if (language.Length == 0) language = "en";
            if (!SupportedLanguages.Contains(language))
                return V1Error("bad_request",
                    $"language must be one of {string.Join(", ", SupportedLanguages)}.", 400);

            // Same-phone contacts are a support nightmare (two histories for one
            // person), so reject rather than silently create a twin.
            var digits = DigitsOf(phone);
            if (digits.Length >= 8)
            {
                var clash = await db.Contacts.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Phone.EndsWith(digits.Substring(digits.Length - 8)));
                if (clash != null)
                    return V1Error("conflict",
                        $"{clash.Name} already uses that number.", 409);
            }

            var row = new Contact
            {
                Name = name,
                Phone = phone,
                Email = (Str(body, "email") ?? "").Trim(),
                Language = language,
                CompanyName = (Str(body, "company_name") ?? "").Trim(),
                Notes = (Str(body, "notes") ?? "").Trim(),
                Tags = StrList(body, "tags") ?? new(),
                Attributes = JsonObject(body, "attributes") ?? new(),
                TenantId = TenantId(me),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Contacts.Add(row);
            await db.SaveChangesAsync();

            // 0..many entity memberships, bounded by what the caller can manage.
            foreach (var slug in StrList(body, "entity_slugs") ?? new())
            {
                var entity = await db.Entities.FirstOrDefaultAsync(e => e.Slug == slug);
                if (entity == null) continue;
                if (TenantId(me) is int tid && entity.TenantId != tid) continue;
                db.ContactEntities.Add(new ContactEntity
                {
                    ContactId = row.Id, EntityId = entity.Id, CreatedAt = DateTime.UtcNow,
                });
            }
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "contact.create",
                Target = row.Phone, Detail = row.Name, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            // tag names → registry tags (Contact.Tags is the registry's cache)
            if (row.Tags.Count > 0)
                ChatBranch.Data.Tags.TagService.ApplyContactNames(db, row.Id, row.Tags, me.Identity?.Name ?? "");

            var saved = await db.Contacts.Include(c => c.EntityLinks).ThenInclude(l => l.Entity)
                .FirstAsync(c => c.Id == row.Id);
            return Results.Json(ContactPayload(saved), statusCode: 201);
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- GET /api/v1/contacts/{id} ----------------------------------------
        // The contact-detail read (api/views.py ContactDetailView). Scoped like
        // the list: superadmin all, admin their tenant, agent only contacts
        // active on an assigned entity — an out-of-scope contact is a 404, never
        // a 403, so the endpoint can't be used to probe for existence.
        app.MapGet("/api/v1/contacts/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var contact = await ScopedContactAsync(db, me, id);
            return contact == null ? ContactMissing() : Results.Ok(ContactPayload(contact));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- PATCH /api/v1/contacts/{id} --------------------------------------
        // A real partial update: every field the Contacts page's edit form can
        // change. It used to accept `language` ALONE and silently drop the rest,
        // so editing a name or a tag appeared to save and changed nothing.
        app.MapMethods("/api/v1/contacts/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, ClaimsPrincipal me, AppDbContext db) =>
        {
            var contact = await ScopedContactAsync(db, me, id, track: true);
            if (contact == null) return ContactMissing();

            var changed = new List<string>();
            if (Has(body, "language"))
            {
                var language = (Str(body, "language") ?? "").Trim().ToLowerInvariant();
                if (!SupportedLanguages.Contains(language))
                    return V1Error("bad_request",
                        $"language must be one of {string.Join(", ", SupportedLanguages)}.", 400);
                contact.Language = language;
                changed.Add("language");
            }
            if (Has(body, "name"))
            {
                var name = (Str(body, "name") ?? "").Trim();
                if (name.Length == 0) return V1Error("bad_request", "name cannot be empty.", 400);
                contact.Name = name;
                changed.Add("name");
            }
            if (Has(body, "phone"))
            {
                var phone = (Str(body, "phone") ?? "").Trim();
                if (phone.Length == 0) return V1Error("bad_request", "phone cannot be empty.", 400);
                contact.Phone = phone;
                changed.Add("phone");
            }
            if (Has(body, "email")) { contact.Email = (Str(body, "email") ?? "").Trim(); changed.Add("email"); }
            if (Has(body, "company_name"))
            {
                contact.CompanyName = (Str(body, "company_name") ?? "").Trim();
                changed.Add("company_name");
            }
            if (Has(body, "notes")) { contact.Notes = (Str(body, "notes") ?? "").Trim(); changed.Add("notes"); }
            if (Has(body, "status")) { contact.Status = (Str(body, "status") ?? "").Trim(); changed.Add("status"); }
            if (StrList(body, "tags") is List<string> tags) { contact.Tags = tags; changed.Add("tags"); }
            // 0..many entity memberships, edited from the same form that creates
            // them (they used to be settable ONLY at create time, which is why
            // "how do I bind a contact to an entity?" had no answer in the UI).
            if (StrList(body, "entity_slugs") is List<string> slugs)
            {
                var wanted = await db.Entities
                    .Where(e => slugs.Contains(e.Slug))
                    .Where(e => TenantId(me) == null || e.TenantId == TenantId(me))
                    .Select(e => e.Id).ToListAsync();
                var current = await db.ContactEntities
                    .Where(l => l.ContactId == contact.Id).ToListAsync();
                foreach (var link in current.Where(l => !wanted.Contains(l.EntityId)))
                    db.ContactEntities.Remove(link);
                foreach (var entityId in wanted.Where(id => current.All(l => l.EntityId != id)))
                    db.ContactEntities.Add(new ContactEntity
                    {
                        ContactId = contact.Id, EntityId = entityId, CreatedAt = DateTime.UtcNow,
                    });
                changed.Add("entities");
            }
            if (JsonObject(body, "attributes") is Dictionary<string, object?> attrs)
            {
                // merge, like the /api/contacts PATCH — custom fields this form
                // doesn't render must survive an edit
                var merged = new Dictionary<string, object?>(contact.Attributes);
                foreach (var kv in attrs) merged[kv.Key] = kv.Value;
                contact.Attributes = merged;
                changed.Add("attributes");
            }
            if (changed.Count > 0)
            {
                contact.UpdatedAt = DateTime.UtcNow;
                db.AuditLogs.Add(new AuditLog
                {
                    Actor = me.Identity?.Name ?? "system", Action = "contact.update",
                    Target = contact.Phone, Detail = $"fields: {string.Join(", ", changed)}",
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            if (changed.Contains("tags"))
                ChatBranch.Data.Tags.TagService.ApplyContactNames(db, contact.Id, contact.Tags, me.Identity?.Name ?? "");
            var fresh = await db.Contacts.AsNoTracking()
                .Include(c => c.EntityLinks).ThenInclude(l => l.Entity)
                .FirstAsync(c => c.Id == contact.Id);
            return Results.Ok(ContactPayload(fresh));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- DELETE /api/v1/contacts/{id} -------------------------------------
        // Hard-delete, gated on `contacts.manage` and scoped exactly like the
        // read. The dependent sweep mirrors Django's FK rules: rows the contact
        // OWNS go away (records + their events, entity links, tag assignments,
        // assignments, queued jobs, pending tool actions, summaries), while
        // conversation history (threads, call records, flow runs, LLM usage) is
        // preserved-but-unlinked — the DB has no ON DELETE clauses, so doing
        // this explicitly is what keeps the delete from failing on a constraint.
        app.MapDelete("/api/v1/contacts/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await AdminEndpoints.HasCapabilityAsync(db, role, "contacts.manage"))
                return V1Error("forbidden", "Admin role required to delete contacts.", 403);
            var contact = await ScopedContactAsync(db, me, id);
            if (contact == null) return ContactMissing();

            var phone = contact.Phone;
            try
            {
                await db.RecordEvents
                    .Where(e => db.Records.Any(r => r.Id == e.RecordId && r.ContactId == id))
                    .ExecuteDeleteAsync();
                await db.Records.Where(r => r.ContactId == id).ExecuteDeleteAsync();
                await db.ContactEntities.Where(l => l.ContactId == id).ExecuteDeleteAsync();
                await db.TagAssignments.Where(a => a.TargetType == "contact" && a.TargetId == id)
                    .ExecuteDeleteAsync();
                // Legacy rows of the retired Service Provider binding would block
                // the delete on a DB whose FKs carry no ON DELETE — sweep them
                // while the old table is still there (absent on new DBs).
                try { await db.Database.ExecuteSqlRawAsync("DELETE FROM crm_resourcelink WHERE contact_id = {0}", id); }
                catch { /* no legacy table */ }
                await db.AccountAssignments.Where(a => a.ContactId == id).ExecuteDeleteAsync();
                await db.PendingToolActions.Where(p => p.ContactId == id).ExecuteDeleteAsync();
                await db.ConversationSummaries.Where(s => s.ContactId == id).ExecuteDeleteAsync();
                await db.OutboundJobs.Where(j => j.ContactId == id).ExecuteDeleteAsync();
                await db.Threads.Where(t => t.ContactId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.ContactId, (int?)null));
                await db.CallRecords.Where(c => c.ContactId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ContactId, (int?)null));
                await db.FlowRuns.Where(f => f.ContactId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.ContactId, (int?)null));
                await db.LlmUsage.Where(u => u.ContactId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.ContactId, (int?)null));
                await db.Contacts.Where(c => c.Id == id).ExecuteDeleteAsync();
                db.AuditLogs.Add(new AuditLog
                {
                    Actor = me.Identity?.Name ?? "system", Action = "contact.delete",
                    Target = phone, CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception)
            {
                return V1Error("conflict",
                    "This contact still has linked data that blocks deletion.", 409);
            }
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- /api/v1/contacts/{id}/entities -----------------------------------
        // Per-entity membership WITHOUT touching the shared profile: a contact
        // row is tenant-shared, and these three verbs move only its links. This
        // is the "remove from THIS entity only" seam — plain contact DELETE
        // removes the profile from every entity at once
        // (api/views.py ContactEntitiesView; UI components/ContactEntitiesCard).
        app.MapGet("/api/v1/contacts/{id:int}/entities",
            async (int id, ClaimsPrincipal me, AppDbContext db) =>
        {
            var contact = await ScopedContactAsync(db, me, id);
            if (contact == null) return ContactMissing();
            return Results.Ok(new
            {
                entities = contact.EntityLinks.Where(l => l.Entity != null)
                    .Select(l => new { id = l.Entity!.Id, slug = l.Entity.Slug, name = l.Entity.Name })
                    .OrderBy(e => e.slug).ToList(),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/v1/contacts/{id:int}/entities",
            async (int id, JsonElement body, ClaimsPrincipal me, AppDbContext db) =>
        {
            var (found, target, err) = await ResolveContactEntityAsync(db, me, id, body);
            if (err != null) return err;
            Contact contact = found!;
            Entity entity = target!;
            var contactId = contact.Id;
            var entityId = entity.Id;
            var created = !await db.ContactEntities
                .AnyAsync(l => l.ContactId == contactId && l.EntityId == entityId);
            if (created)
            {
                db.ContactEntities.Add(new ContactEntity
                {
                    ContactId = contactId, EntityId = entityId, CreatedAt = DateTime.UtcNow,
                });
                db.AuditLogs.Add(new AuditLog
                {
                    Actor = me.Identity?.Name ?? "system", Action = "contact.entity_link",
                    Target = contact.Phone, Detail = $"+entity {entity.Slug}",
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            return Results.Ok(new
            {
                ok = true, created, entities = await LinkedSlugsAsync(db, id),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // The unlink verb carries {entity} in its DELETE body (Django's contract,
        // which ContactEntitiesCard already speaks). Minimal APIs refuse an
        // INFERRED body parameter on DELETE, so the body is read by hand here
        // rather than reshaping a wire format the frontend already sends.
        app.MapDelete("/api/v1/contacts/{id:int}/entities",
            async (int id, HttpContext ctx, ClaimsPrincipal me, AppDbContext db) =>
        {
            var body = await ReadBodyAsync(ctx);
            var (found, target, err) = await ResolveContactEntityAsync(db, me, id, body);
            if (err != null) return err;
            Contact contact = found!;
            Entity entity = target!;
            var contactId = contact.Id;
            var entityId = entity.Id;
            await db.ContactEntities
                .Where(l => l.ContactId == contactId && l.EntityId == entityId)
                .ExecuteDeleteAsync();
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "contact.entity_unlink",
                Target = contact.Phone, Detail = $"-entity {entity.Slug}",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, entities = await LinkedSlugsAsync(db, id) });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- entities (write side) --------------------------------------------
        // GET lives in AdminEndpoints; these are the create/update/delete verbs
        // the Entities & Tenants page drives (api/views.py EntitiesView /
        // EntityDetailView). Gate is the `entities.manage` capability; the
        // MANAGE scope is narrower than the read scope — superadmin any tenant,
        // admin only their own, agents none (they 403 on the capability first).
        app.MapPost("/api/v1/entities", async (JsonElement body,
            ClaimsPrincipal me, AppDbContext db) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await AdminEndpoints.HasCapabilityAsync(db, role, "entities.manage"))
                return V1Error("forbidden", "Admin role required.", 403);

            var name = (Str(body, "name") ?? "").Trim();
            var slug = (Str(body, "slug") ?? "").Trim().ToLowerInvariant();
            if (name.Length == 0 || slug.Length == 0)
                return V1Error("bad_request", "name and slug are required.", 400);

            // Hierarchy first: a parent entity decides the tenant, so operators
            // pick "which entity does this sit under" instead of typing a tenant
            // slug. `tenant` is still honoured (API back-compat) but is only
            // consulted for a TOP-LEVEL entity.
            Entity? parent = null;
            if (Has(body, "parent_id") || Has(body, "parent"))
            {
                parent = await ResolveParentAsync(db, me, body);
                if (parent == null && !ParentIsBlank(body))
                    return V1Error("bad_request",
                        "parent must be an entity you can manage (or blank for a top-level entity).", 400);
            }

            Tenant? tenant;
            if (parent != null)
            {
                // a child ALWAYS lives in its parent's tenant
                tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == parent.TenantId);
            }
            else if (role == Roles.SuperAdmin)
            {
                var tslug = (Str(body, "tenant") ?? "").Trim();
                tenant = tslug.Length > 0
                    ? await db.Tenants.FirstOrDefaultAsync(t => t.Slug == tslug)
                    // No tenant named and no parent: fall back to the caller's own
                    // tenant, then to the only tenant there is. Guessing between
                    // several would file the entity under the wrong organization.
                    : TenantId(me) is int stid
                        ? await db.Tenants.FirstOrDefaultAsync(t => t.Id == stid)
                        : await db.Tenants.CountAsync() == 1
                            ? await db.Tenants.FirstAsync()
                            : null;
                if (tenant == null)
                    return V1Error("bad_request",
                        "Pick a parent entity for this one (or name a tenant).", 400);
            }
            else
            {
                if (TenantId(me) is not int tid)
                    return V1Error("bad_request", "No tenant to create the entity under.", 400);
                tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tid);
            }
            if (tenant == null)
                return V1Error("bad_request", "No tenant to create the entity under.", 400);
            var tenantPk = tenant.Id;
            if (await db.Entities.AnyAsync(e => e.TenantId == tenantPk && e.Slug == slug))
                return V1Error("conflict", "Entity slug already exists in this tenant.", 409);

            var row = new Entity
            {
                TenantId = tenantPk, Tenant = tenant, Name = name, Slug = slug,
                ParentId = parent?.Id, Parent = parent,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Entities.Add(row);
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "entity.create",
                Target = $"{tenant.Slug}/{slug}", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Json(EntityPayload(row), statusCode: 201);
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapMethods("/api/v1/entities/{id:int}", new[] { "PATCH" },
            async (int id, JsonElement body, ClaimsPrincipal me, AppDbContext db) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await AdminEndpoints.HasCapabilityAsync(db, role, "entities.manage"))
                return V1Error("forbidden", "Admin role required to manage entities.", 403);
            var entity = await ManageableEntityAsync(db, me, id);
            if (entity == null) return EntityMissing();

            var changed = new List<string>();
            if (Has(body, "name"))
            {
                var name = (Str(body, "name") ?? "").Trim();
                if (name.Length == 0)
                    return V1Error("bad_request", "name cannot be empty.", 400);
                entity.Name = name;
                changed.Add("name");
            }
            if (Has(body, "slug"))
            {
                var slug = (Str(body, "slug") ?? "").Trim().ToLowerInvariant();
                if (slug.Length == 0)
                    return V1Error("bad_request", "slug cannot be empty.", 400);
                if (await db.Entities.AnyAsync(e =>
                        e.TenantId == entity.TenantId && e.Slug == slug && e.Id != entity.Id))
                    return V1Error("conflict", "Entity slug already exists in this tenant.", 409);
                entity.Slug = slug;
                changed.Add("slug");
            }
            // is_active is accepted (the edit modal always sends it) but there is
            // no ingest_entity.is_active column in this port yet — GET reports
            // every entity as live. Accepting-and-ignoring keeps the modal from
            // 400-ing on an is_active-only save; wire it up when the column lands.
            if (Has(body, "parent_id") || Has(body, "parent"))
            {
                if (ParentIsBlank(body))
                {
                    entity.ParentId = null;   // promote to top level
                }
                else
                {
                    var parent = await ResolveParentAsync(db, me, body);
                    if (parent == null)
                        return V1Error("bad_request",
                            "parent must be an entity you can manage (or blank for a top-level entity).", 400);
                    if (parent.Id == entity.Id)
                        return V1Error("bad_request", "An entity cannot be its own parent.", 400);
                    if (await IsDescendantAsync(db, parent.Id, entity.Id))
                        return V1Error("bad_request",
                            "That parent sits under this entity — the hierarchy would loop.", 400);
                    if (parent.TenantId != entity.TenantId)
                        return V1Error("bad_request",
                            "Parent and child must belong to the same tenant.", 400);
                    entity.ParentId = parent.Id;
                }
                changed.Add("parent");
            }
            if (Has(body, "is_active")) changed.Add("is_active");
            if (changed.Count == 0)
                return V1Error("bad_request",
                    "Nothing to update — send name, slug, parent and/or is_active.", 400);

            entity.UpdatedAt = DateTime.UtcNow;
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "entity.update",
                Target = $"{entity.Tenant?.Slug}/{entity.Slug}",
                Detail = $"fields: {string.Join(", ", changed)}", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Ok(EntityPayload(entity));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/v1/entities/{id:int}", async (int id,
            ClaimsPrincipal me, AppDbContext db) =>
        {
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await AdminEndpoints.HasCapabilityAsync(db, role, "entities.manage"))
                return V1Error("forbidden", "Admin role required to manage entities.", 403);
            var entity = await ManageableEntityAsync(db, me, id);
            if (entity == null) return EntityMissing();

            // Refuse while anything still depends on it — knowledge items would
            // be real data loss, contacts/agents would silently lose scope. To
            // retire an entity without deleting, PATCH is_active=false.
            var contacts = await db.ContactEntities.CountAsync(l => l.EntityId == id);
            var agents = await db.Profiles.CountAsync(p => p.Entities.Any(e => e.Id == id));
            var knowledge = await db.KnowledgeItems.CountAsync(k => k.EntityId == id);
            // Children too — deleting a parent would orphan its sub-entities.
            var children = await db.Entities.CountAsync(e => e.ParentId == id);
            // …and the tags this entity owns (its own library entries).
            var ownTags = await db.Tags.CountAsync(t => t.EntityId == id);
            if (contacts > 0 || agents > 0 || knowledge > 0 || children > 0 || ownTags > 0)
            {
                var parts = new List<string>();
                if (contacts > 0) parts.Add($"{contacts} contact(s)");
                if (agents > 0) parts.Add($"{agents} user(s)");
                if (knowledge > 0) parts.Add($"{knowledge} knowledge item(s)");
                if (children > 0) parts.Add($"{children} child entity(ies)");
                if (ownTags > 0) parts.Add($"{ownTags} tag(s)");
                return Results.Json(new
                {
                    error = new
                    {
                        code = "conflict",
                        message = $"This entity still has {string.Join(", ", parts)} — " +
                                  "reassign or remove them first.",
                    },
                    contacts, agents, knowledge, children,
                }, statusCode: 409);
            }

            var label = $"{entity.Tenant?.Slug}/{entity.Slug}";
            // the tags ON this entity (inner/outside company…) go with it
            db.TagAssignments.RemoveRange(db.TagAssignments.Where(a => a.TargetType == "entity" && a.TargetId == id));
            db.Entities.Remove(entity);
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "entity.delete",
                Target = label, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- users (write side) -----------------------------------------------
        // GET lives in AdminEndpoints; these port crm/user_service.py, where the
        // TIERED rules live in one place:
        //   superadmin — everyone, any role, any tenant;
        //   admin      — only agent-LEVEL users (agent + every custom role) in
        //                their own tenant, and may never assign admin/superadmin;
        //   anyone else — 403 on the `users.manage` capability first.
        // Capability gates the ACTION, tiering bounds the TARGET — both apply.
        app.MapPost("/api/v1/users", async (JsonElement body,
            ClaimsPrincipal me, AppDbContext db) =>
        {
            var actorRole = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await AdminEndpoints.HasCapabilityAsync(db, actorRole, "users.manage"))
                return V1Error("forbidden", "Admin role required to manage users.", 403);

            var username = (Str(body, "username") ?? "").Trim();
            var password = Str(body, "password") ?? "";
            var role = (Str(body, "role") ?? "").Trim();
            if (username.Length == 0)
                return V1Error("bad_request", "username is required.", 400);
            var lowered = username.ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Username.ToLower() == lowered))
                return V1Error("bad_request", $"Username '{username}' is already taken.", 400);
            if (password.Length < 8)
                return V1Error("bad_request", "Password must be at least 8 characters.", 400);
            var validRoles = await AdminEndpoints.ValidRoleSlugsAsync(db);
            if (!validRoles.Contains(role))
                return V1Error("bad_request",
                    $"role must be one of: {string.Join(", ", validRoles.OrderBy(r => r))}.", 400);
            if (actorRole == Roles.Admin && AdminEndpoints.IsPrivilegedRole(role))
                return V1Error("forbidden", "Admins can only create agent-level users.", 403);

            var entityIds = IntList(body, "entity_ids") ?? new List<int>();
            if (entityIds.Count > 0 && role != Roles.Agent)
                return V1Error("bad_request", "Entity access applies to agent users only.", 400);
            var (entities, entityErr) = await ResolveEntitiesAsync(db, entityIds);
            if (entityErr != null) return entityErr;
            var actorTenantId = TenantId(me);
            if (actorRole == Roles.Admin && entities.Count > 0 && actorTenantId is int atid
                && entities[0].TenantId != atid)
                return V1Error("forbidden", "Those entities are outside your tenant.", 403);

            // Tenant resolution, in precedence order: the entities pin it; else an
            // admin creates inside their own; else a superadmin may name one.
            int? tenantId;
            if (entities.Count > 0) tenantId = entities[0].TenantId;
            else if (actorRole == Roles.Admin) tenantId = actorTenantId;
            else if (actorRole == Roles.SuperAdmin && (Str(body, "tenant") ?? "").Trim() is { Length: > 0 } tslug)
            {
                var t = await db.Tenants.FirstOrDefaultAsync(x => x.Slug == tslug);
                if (t == null) return V1Error("bad_request", $"Unknown tenant '{tslug}'.", 400);
                tenantId = t.Id;
            }
            else tenantId = null;

            var user = new AppUser
            {
                Username = username,
                Password = DjangoPasswordHasher.Hash(password),
                IsActive = true,
                // Keep the Django-admin flag coherent with the app role
                // (_apply_role_flags); there is no is_staff column in this port.
                IsSuperuser = role == Roles.SuperAdmin,
            };
            try
            {
                // The user row lands first: Profile.user_id is a real FK, so the
                // id has to exist before the profile that carries the role.
                db.Users.Add(user);
                await db.SaveChangesAsync();

                var profile = new Profile { UserId = user.Id, Role = role, TenantId = tenantId };
                foreach (var e in entities) profile.Entities.Add(e);
                db.Profiles.Add(profile);
                db.AuditLogs.Add(new AuditLog
                {
                    Actor = me.Identity?.Name ?? "system", Action = "user.create",
                    Target = username, UserId = user.Id, Detail = $"role: {role}",
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception)
            {
                // A racing create on the same username loses the unique index —
                // report it the way the pre-check does, not as a 500.
                return V1Error("bad_request",
                    $"Could not create '{username}' — the username may already be taken.", 400);
            }
            return Results.Json(await ManagedUserAsync(db, user.Id), statusCode: 201);
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // POST (not PATCH) on the detail route, matching Django: one endpoint
        // serves every partial update the Users page issues — role change,
        // password reset, reactivate, entity access. A key that isn't in the
        // body is UNTOUCHED (that's why this reads JsonElement and not a record:
        // "absent" and "set to null/empty" must stay distinguishable).
        app.MapPost("/api/v1/users/{id:int}", async (int id, JsonElement body,
            ClaimsPrincipal me, AppDbContext db) =>
        {
            var actorRole = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await AdminEndpoints.HasCapabilityAsync(db, actorRole, "users.manage"))
                return V1Error("forbidden", "Admin role required to manage users.", 403);

            var target = await TargetUserAsync(db, id);
            if (target == null) return V1Error("not_found", "User not found.", 404);
            var profile = await EnsureProfileAsync(db, target);
            var tierErr = AdminMayManage(actorRole, TenantId(me), profile);
            if (tierErr != null) return tierErr;

            string? newRole = Has(body, "role") ? (Str(body, "role") ?? "").Trim() : null;
            bool? newActive = Has(body, "active") ? Bool(body, "active") : null;

            if (newRole != null)
            {
                var validRoles = await AdminEndpoints.ValidRoleSlugsAsync(db);
                if (!validRoles.Contains(newRole))
                    return V1Error("bad_request",
                        $"role must be one of: {string.Join(", ", validRoles.OrderBy(r => r))}.", 400);
                if (actorRole == Roles.Admin && AdminEndpoints.IsPrivilegedRole(newRole))
                    return V1Error("forbidden", "Admins can only assign agent-level roles.", 403);
            }
            if (Has(body, "active") && newActive == null)
                return V1Error("bad_request", "active must be true or false.", 400);
            string? newPassword = null;
            if (Has(body, "password"))
            {
                newPassword = Str(body, "password") ?? "";
                if (newPassword.Length < 8)
                    return V1Error("bad_request", "Password must be at least 8 characters.", 400);
            }
            // Anti-lockout: the platform must always keep one active superadmin.
            if (await WouldRemoveLastSuperAdminAsync(db, target, newRole, newActive))
                return V1Error("bad_request",
                    "Cannot demote or deactivate the last active super admin — " +
                    "promote another super admin first.", 400);

            List<Entity>? newEntities = null;
            if (Has(body, "entity_ids"))
            {
                var ids = IntList(body, "entity_ids");
                if (ids == null)
                    return V1Error("bad_request", "entity_ids must be a list of entity ids.", 400);
                var effectiveRole = newRole ?? profile.Role;
                if (ids.Count > 0 && effectiveRole != Roles.Agent)
                    return V1Error("bad_request", "Entity access applies to agent users only.", 400);
                var (resolved, err) = await ResolveEntitiesAsync(db, ids);
                if (err != null) return err;
                if (resolved.Count > 0 && profile.TenantId is int ptid
                    && ptid != resolved[0].TenantId)
                    return V1Error("bad_request",
                        "Those entities belong to a different tenant than this user.", 400);
                newEntities = resolved;
            }

            // Bind / unbind this login to a CONTACT — a registered customer who
            // signs in to use the integrations connected against their own
            // record. An int binds, null/0 unbinds.
            bool setContact = false; int? newContactId = null;
            if (Has(body, "contact_id"))
            {
                setContact = true;
                var cp = body.GetProperty("contact_id");
                if (cp.ValueKind == JsonValueKind.Number && cp.GetInt32() > 0)
                {
                    var cid = cp.GetInt32();
                    var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid);
                    if (contact == null)
                        return V1Error("bad_request", "contact_id must reference an existing contact.", 400);
                    if (contact.TenantId != null && profile.TenantId != null
                        && contact.TenantId != profile.TenantId)
                        return V1Error("bad_request",
                            "That contact belongs to a different tenant than this user.", 400);
                    // one login per customer — two logins sharing a contact would
                    // make "whose integration is this?" unanswerable
                    var taken = await db.Profiles.AsNoTracking()
                        .FirstOrDefaultAsync(pr => pr.ContactId == cid && pr.UserId != profile.UserId);
                    if (taken != null)
                        return V1Error("conflict", "That contact is already bound to another login.", 409);
                    newContactId = cid;
                }
                else if (cp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number))
                    return V1Error("bad_request", "contact_id must be a contact id or null.", 400);
            }

            var changed = new List<string>();
            if (setContact && profile.ContactId != newContactId)
            {
                profile.ContactId = newContactId;
                changed.Add("contact_id");
            }
            // `name` is accepted and ignored: auth_user has no first_name column
            // in this port, so managed_user_payload already reports the username.
            if (newRole != null && newRole != profile.Role)
            {
                profile.Role = newRole;
                target.IsSuperuser = newRole == Roles.SuperAdmin;
                if (newRole != Roles.Agent) profile.Entities.Clear();
                changed.Add("role");
            }
            if (newEntities != null)
            {
                profile.Entities.Clear();
                foreach (var e in newEntities) profile.Entities.Add(e);
                if (newEntities.Count > 0 && profile.TenantId == null)
                    profile.TenantId = newEntities[0].TenantId;
                changed.Add("entities");
            }
            if (newActive is bool active && active != target.IsActive)
            {
                target.IsActive = active;
                if (!active) await KillSessionsAsync(db, profile);
                changed.Add("active");
            }
            if (newPassword != null)
            {
                target.Password = DjangoPasswordHasher.Hash(newPassword);
                // Every bearer token — every session — dies with the password.
                await KillSessionsAsync(db, profile);
                changed.Add("password");
            }
            if (changed.Count > 0)
            {
                db.AuditLogs.Add(new AuditLog
                {
                    Actor = me.Identity?.Name ?? "system", Action = "user.update",
                    Target = target.Username, UserId = target.Id,
                    Detail = $"fields: {string.Join(", ", changed)}", CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            return Results.Ok(await ManagedUserAsync(db, id));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // DELETE deactivates rather than destroys (Django deactivate_user): the
        // user's audit trail and message attribution must survive, so the row
        // stays and every session is revoked instead.
        app.MapDelete("/api/v1/users/{id:int}", async (int id,
            ClaimsPrincipal me, AppDbContext db) =>
        {
            var actorRole = me.FindFirstValue(ClaimTypes.Role) ?? "";
            if (!await AdminEndpoints.HasCapabilityAsync(db, actorRole, "users.manage"))
                return V1Error("forbidden", "Admin role required to manage users.", 403);

            var target = await TargetUserAsync(db, id);
            if (target == null) return V1Error("not_found", "User not found.", 404);
            var profile = await EnsureProfileAsync(db, target);
            var tierErr = AdminMayManage(actorRole, TenantId(me), profile);
            if (tierErr != null) return tierErr;
            if (await WouldRemoveLastSuperAdminAsync(db, target, null, false))
                return V1Error("bad_request",
                    "Cannot deactivate the last active super admin — " +
                    "promote another super admin first.", 400);

            target.IsActive = false;
            await KillSessionsAsync(db, profile);
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system", Action = "user.deactivate",
                Target = target.Username, UserId = target.Id, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return Results.Ok(await ManagedUserAsync(db, id));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- bulk record sync (ingest) ----------------------------------------
        app.MapPost("/api/v1/sync/records", (SyncRequest body, ClaimsPrincipal me, AppDbContext db) =>
        {
            var tenantId = TenantId(me);
            int created = 0, updated = 0;
            foreach (var r in body.Records)
            {
                // phone-first contact match/create
                Contact? contact = null;
                if (!string.IsNullOrWhiteSpace(r.Contact?.Phone))
                {
                    contact = db.Contacts.FirstOrDefault(c => c.Phone == r.Contact!.Phone
                        && (tenantId == null || c.TenantId == tenantId));
                    if (contact == null)
                    {
                        contact = new Contact
                        {
                            Name = r.Contact!.Name ?? r.Contact.Phone!, Phone = r.Contact.Phone!,
                            Email = r.Contact.Email ?? "", TenantId = tenantId,
                            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                        };
                        db.Contacts.Add(contact);
                        db.SaveChanges();
                    }
                }
                if (contact == null) continue; // records need an identity

                var type = r.Type ?? "custom";
                var rec = db.Records.FirstOrDefault(x => x.ExternalId == r.ExternalId
                    && x.Type == type && (tenantId == null || x.TenantId == tenantId));
                if (rec == null)
                {
                    rec = new Record { ExternalId = r.ExternalId, Type = type, ContactId = contact.Id,
                        TenantId = tenantId, CreatedAt = DateTime.UtcNow };
                    db.Records.Add(rec);
                    created++;
                }
                else updated++;
                rec.Title = r.Title ?? rec.Title;
                rec.Status = r.Status ?? rec.Status;
                rec.Amount = r.Amount ?? rec.Amount;
                rec.DueDate = r.DueDate ?? rec.DueDate;
                rec.UpdatedAt = DateTime.UtcNow;
                db.SaveChanges();
            }
            return Results.Ok(new { created, updated, total = body.Records.Count });
        }).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    private static int? TenantId(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;

    // ---- v1 error envelope ------------------------------------------------
    // Every v1 failure is {error:{code,message}} — the dashboard's v1 fetch
    // helpers read `error.message` and surface it verbatim, so the message is
    // operator-facing text, not a stack-trace crumb.
    private static IResult V1Error(string code, string message, int status) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    private static IResult ContactMissing() =>
        V1Error("not_found", "No such contact (or not visible to you).", 404);

    private static IResult EntityMissing() =>
        V1Error("not_found", "No such entity (or not visible to you).", 404);

    // ---- JSON body readers -------------------------------------------------
    // These endpoints read JsonElement rather than a record because the wire
    // format is snake_case AND the partial-update verbs must tell "key absent"
    // (leave alone) from "key set to null/empty" (clear it) — Django's _UNSET.

    /// Reads a JSON body by hand. An unparseable/absent body degrades to
    /// `default(JsonElement)` (ValueKind.Undefined), which every reader below
    /// treats as "no keys present" — so a bodyless request is a clean 400 from
    /// the validation rules, never a 500 out of the binder.
    private static async Task<JsonElement> ReadBodyAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            return doc.RootElement.Clone();
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static bool Has(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out _);

    private static string? Str(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// null = the key is present but isn't a list of ints (a 400, not "empty").
    private static List<int>? IntList(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var v))
            return null;
        if (v.ValueKind != JsonValueKind.Array) return null;
        var ids = new List<int>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n)) ids.Add(n);
            else if (item.ValueKind == JsonValueKind.String
                && int.TryParse(item.GetString(), out var s)) ids.Add(s);
            else return null;
        }
        return ids;
    }

    // ---- contacts ----------------------------------------------------------

    /// crm.Contact.SUPPORTED_LANGUAGES — the codes the AI may conduct a call in.
    private static readonly string[] SupportedLanguages = { "auto", "en", "ms", "zh" };

    /// <summary>A JSON string array as a trimmed list; null when the key is absent
    /// (so a PATCH can tell "leave the tags alone" from "clear the tags").</summary>
    private static List<string>? StrList(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Array) return null;
        return v.EnumerateArray()
            .Select(x => (x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).Trim())
            .Where(x => x.Length > 0).ToList();
    }

    /// <summary>A JSON object as a plain dictionary (custom-field values); null
    /// when the key is absent.</summary>
    private static Dictionary<string, object?>? JsonObject(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Object) return null;
        var into = new Dictionary<string, object?>();
        foreach (var prop in v.EnumerateObject())
            into[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => prop.Value.TryGetInt64(out var n) ? n : prop.Value.GetDouble(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.Array => prop.Value.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                    .ToList(),
                _ => prop.Value.ToString(),
            };
        return into;
    }

    private static string DigitsOf(string raw) => new(raw.Where(char.IsDigit).ToArray());

    /// api/views.py ContactDetailView._scoped, narrowed to one row. superadmin
    /// sees all, admin their tenant, everyone else only contacts active on an
    /// entity assigned to them — so an out-of-scope id reads as "not found".
    private static async Task<Contact?> ScopedContactAsync(AppDbContext db,
        ClaimsPrincipal me, int id, bool track = false)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        var q = (track ? db.Contacts : db.Contacts.AsNoTracking())
            .Include(c => c.EntityLinks).ThenInclude(l => l.Entity)
            .Where(c => c.Id == id);
        if (role == Roles.SuperAdmin) return await q.FirstOrDefaultAsync();
        if (role == Roles.Admin)
            return TenantId(me) is int tid
                ? await q.Where(c => c.TenantId == tid).FirstOrDefaultAsync()
                : null;
        // agent (and every custom role): only their assigned entities.
        var ids = (await AdminEndpoints.AccessibleEntitiesAsync(db, me)).Select(e => e.Id).ToList();
        if (ids.Count == 0) return null;
        return await q.Where(c => c.EntityLinks.Any(l => ids.Contains(l.EntityId)))
            .FirstOrDefaultAsync();
    }

    /// api/serializers.py ContactSerializer, built inline in snake_case.
    private static object ContactPayload(Contact c) => new
    {
        id = c.Id,
        name = c.Name,
        phone = c.Phone,
        email = c.Email,
        language = c.Language,
        company_name = c.CompanyName,
        status = c.Status,
        method = c.Method,
        external_id = c.ExternalId,
        tags = c.Tags,
        attributes = c.Attributes,
        last_synced_at = c.LastSyncedAt,
        created_at = c.CreatedAt,
        updated_at = c.UpdatedAt,
        // The entities this tenant-shared contact is active on — the detail
        // page's per-entity membership editor reads exactly this.
        entities = c.EntityLinks.Where(l => l.Entity != null)
            .Select(l => new { slug = l.Entity!.Slug, name = l.Entity.Name }).ToList(),
    };

    private static async Task<List<string>> LinkedSlugsAsync(AppDbContext db, int contactId) =>
        await db.ContactEntities.AsNoTracking()
            .Where(l => l.ContactId == contactId && l.Entity != null)
            .Select(l => l.Entity!.Slug).OrderBy(s => s).ToListAsync();

    /// (contact, entity, error). The contact is scoped to the caller; the entity
    /// must live in the contact's OWN tenant and be one the caller may touch.
    private static async Task<(Contact?, Entity?, IResult?)> ResolveContactEntityAsync(
        AppDbContext db, ClaimsPrincipal me, int id, JsonElement body)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        if (!await AdminEndpoints.HasCapabilityAsync(db, role, "contacts.manage"))
            return (null, null, V1Error("forbidden",
                "Admin role required to manage entity links.", 403));
        var contact = await ScopedContactAsync(db, me, id);
        if (contact == null) return (null, null, ContactMissing());

        var slug = (Str(body, "entity") ?? "").Trim();
        var entity = slug.Length == 0 ? null : await db.Entities.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == contact.TenantId && e.Slug == slug);
        if (entity == null)
            return (null, null, V1Error("bad_request",
                "Unknown entity for this contact's tenant.", 400));
        if (role != Roles.SuperAdmin && role != Roles.Admin)
        {
            var accessible = await AdminEndpoints.AccessibleEntitiesAsync(db, me);
            if (accessible.All(e => e.Id != entity.Id))
                return (null, null, V1Error("forbidden",
                    "That entity is not assigned to you.", 403));
        }
        return (contact, entity, null);
    }

    // ---- entities ----------------------------------------------------------

    /// api/serializers.py EntitySerializer. `is_active` is hardcoded true for the
    /// same reason as the GET in AdminEndpoints — no such column in this port.
    private static object EntityPayload(Entity e) => new
    {
        id = e.Id,
        name = e.Name,
        slug = e.Slug,
        tenant = e.Tenant?.Slug ?? "",
        parent_id = e.ParentId,
        parent = e.Parent?.Slug,
        parent_name = e.Parent?.Name ?? "",
        is_active = true,
        created_at = e.CreatedAt,
    };

    /// <summary>
    /// `parent_id` as an int, accepting a JSON number OR a numeric string
    /// (Str() alone returns null for a number, which would read as "clear the
    /// parent" and silently promote a child to top level).
    /// </summary>
    private static int? ParentPk(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("parent_id", out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : null,
            JsonValueKind.String => int.TryParse((v.GetString() ?? "").Trim(), out var n) ? n : null,
            _ => null,
        };
    }

    /// <summary>True when the body explicitly clears the parent ("" / null / 0).</summary>
    private static bool ParentIsBlank(JsonElement body)
    {
        if (Has(body, "parent_id"))
        {
            var pk = ParentPk(body);
            // present but not a usable id (null / "" / 0) = clear
            if (pk is null or 0) return true;
            return false;
        }
        if (Has(body, "parent"))
            return (Str(body, "parent") ?? "").Trim().Length == 0;
        return false;
    }

    /// <summary>
    /// The parent named by `parent_id` (pk) or `parent` (slug), bounded by what
    /// the caller may manage — you cannot hang an entity under one you can't see.
    /// </summary>
    private static async Task<Entity?> ResolveParentAsync(AppDbContext db,
        ClaimsPrincipal me, JsonElement body)
    {
        if (ParentIsBlank(body)) return null;
        if (ParentPk(body) is int pid) return await ManageableEntityAsync(db, me, pid);
        var slug = (Str(body, "parent") ?? "").Trim().ToLowerInvariant();
        if (slug.Length == 0) return null;
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        var q = db.Entities.Include(e => e.Tenant).Where(e => e.Slug == slug);
        if (role != Roles.SuperAdmin)
        {
            if (TenantId(me) is not int tid) return null;
            q = q.Where(e => e.TenantId == tid);
        }
        return await q.FirstOrDefaultAsync();
    }

    /// <summary>Walks up from `startId`; true when `ancestorId` is on the path
    /// (so re-parenting there would create a cycle). Depth-capped as a backstop
    /// against a pre-existing loop in the data.</summary>
    private static async Task<bool> IsDescendantAsync(AppDbContext db, int startId, int ancestorId)
    {
        var cursor = (int?)startId;
        for (var hops = 0; cursor != null && hops < 64; hops++)
        {
            if (cursor == ancestorId) return true;
            cursor = await db.Entities.Where(e => e.Id == cursor)
                .Select(e => e.ParentId).FirstOrDefaultAsync();
        }
        return false;
    }

    /// Entities this caller may EDIT — deliberately narrower than the read scope
    /// (api/views.py EntityDetailView._manageable): superadmin any tenant, admin
    /// only their own, everyone else nothing.
    private static async Task<Entity?> ManageableEntityAsync(AppDbContext db,
        ClaimsPrincipal me, int id)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        var q = db.Entities.Include(e => e.Tenant).Include(e => e.Parent).Where(e => e.Id == id);
        if (role == Roles.SuperAdmin) return await q.FirstOrDefaultAsync();
        if (role == Roles.Admin)
            return TenantId(me) is int tid
                ? await q.Where(e => e.TenantId == tid).FirstOrDefaultAsync()
                : null;
        return null;
    }

    // ---- users (crm/user_service.py) ---------------------------------------

    private static Task<AppUser?> TargetUserAsync(AppDbContext db, int id) =>
        db.Users.Include(u => u.Profile!).ThenInclude(p => p.Entities)
                .Include(u => u.Profile!).ThenInclude(p => p.Contact)
            .FirstOrDefaultAsync(u => u.Id == id);

    /// Django's get_or_create(profile): a user with no Profile row still has an
    /// effective role, so materialize one before tiering decisions read it.
    private static async Task<Profile> EnsureProfileAsync(AppDbContext db, AppUser user)
    {
        if (user.Profile != null) return user.Profile;
        var profile = new Profile { UserId = user.Id, Role = AdminEndpoints.RoleOf(user) };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();
        user.Profile = profile;
        return profile;
    }

    /// _assert_admin_may_manage: an admin may act only on agent-level users
    /// (agent + every custom role) inside their own tenant — never on another
    /// admin or a superadmin. superadmin skips the check entirely.
    private static IResult? AdminMayManage(string actorRole, int? actorTenantId, Profile target)
    {
        if (actorRole != Roles.Admin) return null;
        if (AdminEndpoints.IsPrivilegedRole(target.Role))
            return V1Error("forbidden", "Admins can only manage agent-level users.", 403);
        if (target.TenantId != actorTenantId)
            return V1Error("forbidden", "That user is outside your tenant.", 403);
        return null;
    }

    /// _resolve_entities: every id must exist AND share ONE tenant (a user works
    /// for a single tenant).
    private static async Task<(List<Entity>, IResult?)> ResolveEntitiesAsync(
        AppDbContext db, List<int> ids)
    {
        if (ids.Count == 0) return (new List<Entity>(), null);
        var distinct = ids.Distinct().ToList();
        var rows = await db.Entities.Where(e => distinct.Contains(e.Id)).ToListAsync();
        if (rows.Count != distinct.Count)
            return (rows, V1Error("bad_request", "One or more entities do not exist.", 400));
        if (rows.Select(e => e.TenantId).Distinct().Count() > 1)
            return (rows, V1Error("bad_request",
                "All entities must belong to the same tenant.", 400));
        return (rows, null);
    }

    /// would_remove_last_superadmin — the platform must never lose its last
    /// active superadmin to a demotion or a deactivation.
    private static async Task<bool> WouldRemoveLastSuperAdminAsync(AppDbContext db,
        AppUser target, string? newRole, bool? newActive)
    {
        if (AdminEndpoints.RoleOf(target) != Roles.SuperAdmin || !target.IsActive) return false;
        var demoted = newRole != null && newRole != Roles.SuperAdmin;
        var deactivated = newActive == false;
        if (!demoted && !deactivated) return false;
        return !await db.Profiles.AnyAsync(p =>
            p.Role == Roles.SuperAdmin && p.UserId != target.Id && p.User!.IsActive);
    }

    /// A deactivation or a password change signs the user out EVERYWHERE — the
    /// legacy Profile.token and every AuthToken session row go together.
    private static async Task KillSessionsAsync(AppDbContext db, Profile profile)
    {
        profile.Token = "";
        await db.AuthTokens.Where(t => t.ProfileId == profile.Id).ExecuteDeleteAsync();
    }

    private static async Task<object> ManagedUserAsync(AppDbContext db, int id)
    {
        var row = await db.Users.AsNoTracking()
            .Include(u => u.Profile!).ThenInclude(p => p.Tenant)
            .Include(u => u.Profile!).ThenInclude(p => p.Entities)
                .Include(u => u.Profile!).ThenInclude(p => p.Contact)
            .FirstAsync(u => u.Id == id);
        return AdminEndpoints.ManagedUserPayload(row);
    }
}
