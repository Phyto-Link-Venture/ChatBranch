using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data;

/// <summary>
/// Idempotent bootstrap — the C# equivalent of the parts of
/// <c>manage.py seed_demo</c> Phase 1 needs: a default tenant + entity, the
/// three built-in roles, and the demo users (POC creds, rotate before deploy).
/// Safe to call on every startup; get-or-create semantics never clobber edits.
/// </summary>
public static class Seed
{
    public record DemoUser(string Username, string Password, string Role);

    public static readonly DemoUser[] DemoUsers =
    {
        new("superadmin", "super123", Roles.SuperAdmin),
        new("admin", "admin123", Roles.Admin),
        new("agent", "agent123", Roles.Agent),
    };

    /// <summary>
    /// Additive schema upgrades for databases created before a column existed.
    /// EnsureCreated() only builds a schema from scratch — it never alters an
    /// existing one — so each new nullable column is added here, idempotently
    /// (SQLite ALTER TABLE ADD COLUMN; skipped when already present). Keep this
    /// list append-only.
    /// </summary>
    private static void UpgradeSchema(AppDbContext db)
    {
        AddColumnIfMissing(db, "crm_profile", "resource_id", "INTEGER NULL");
        AddColumnIfMissing(db, "ingest_entity", "parent_id", "INTEGER NULL");
    }

    private static void AddColumnIfMissing(AppDbContext db, string table, string column, string ddlType)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using (var check = conn.CreateCommand())
            {
                check.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var r = check.ExecuteReader();
                while (r.Read())
                    if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {ddlType}";
            alter.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    public static void Run(AppDbContext db)
    {
        db.Database.EnsureCreated();
        UpgradeSchema(db);

        var tenant = db.Tenants.FirstOrDefault(t => t.Slug == "demo");
        if (tenant == null)
        {
            tenant = new Tenant { Name = "Demo Tenant", Slug = "demo", Status = "active",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Tenants.Add(tenant);
            db.SaveChanges();
        }

        if (!db.Entities.Any(e => e.TenantId == tenant.Id && e.Slug == "main"))
        {
            db.Entities.Add(new Entity { TenantId = tenant.Id, Name = "Main", Slug = "main",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        foreach (var (slug, label, system) in new[]
                 {
                     (Roles.SuperAdmin, "Super admin", true),
                     (Roles.Admin, "Admin", true),
                     (Roles.Agent, "Agent", true),
                 })
        {
            if (!db.Roles.Any(r => r.Slug == slug))
                db.Roles.Add(new Role { Slug = slug, Label = label, IsSystem = system,
                    CreatedAt = DateTime.UtcNow });
        }
        db.SaveChanges();

        foreach (var demo in DemoUsers)
        {
            var user = db.Users.Include(u => u.Profile)
                .FirstOrDefault(u => u.Username == demo.Username);
            if (user == null)
            {
                user = new AppUser
                {
                    Username = demo.Username,
                    Password = DjangoPasswordHasher.Hash(demo.Password),
                    IsActive = true,
                    IsSuperuser = demo.Role == Roles.SuperAdmin,
                };
                db.Users.Add(user);
                db.SaveChanges();
            }
            if (user.Profile == null)
            {
                db.Profiles.Add(new Profile
                {
                    UserId = user.Id,
                    Role = demo.Role,
                    TenantId = tenant.Id,
                });
                db.SaveChanges();
            }
        }

        // A starter "Provider" role for Service Provider logins (teachers,
        // suppliers…) — a CUSTOM role (not system) so admins can rename it, tune
        // its permissions in the matrix, or clone it into Teacher / Supplier
        // roles. Read-mostly by default: their own contacts + chats + calendar.
        // Row-level "own world" scoping is enforced by Profile.ResourceId, not by
        // this role — the role only decides which ACTIONS are allowed.
        if (!db.Roles.Any(r => r.Slug == "provider"))
        {
            db.Roles.Add(new Role { Slug = "provider", Label = "Provider", IsSystem = false,
                CreatedAt = DateTime.UtcNow });
            var allow = new HashSet<string>
            {
                "contacts.view", "chats.view", "chats.send", "integrations.view",
                "integrations.manage", "call_logs.view",
            };
            foreach (var cap in new[]
                     {
                         "contacts.view", "contacts.manage", "chats.view", "chats.send", "chats.manage",
                         "flows.view", "flows.manage", "channels.view", "channels.manage",
                         "integrations.view", "integrations.manage", "users.view", "users.manage",
                         "entities.view", "entities.manage", "templates.manage",
                         "script_templates.manage", "notifications.manage", "analytics.view",
                         "call_logs.view", "activity.view", "settings.ai.manage",
                         "account_assignment.manage", "app_keys.manage", "pronunciation.manage",
                         "rbac.manage",
                     })
                db.RolePermissions.Add(new RolePermission
                {
                    Role = "provider", Capability = cap, Allowed = allow.Contains(cap),
                    UpdatedAt = DateTime.UtcNow,
                });
            db.SaveChanges();
        }

        // Default Custom Fields schema — seeded ONLY when none exists yet, so it
        // never overwrites a deployment's own labels. Service Providers are a
        // generic concept (teachers, suppliers, coaches…), so the out-of-the-box
        // provider field is a "Type" dropdown that tells them apart. Admins edit
        // or extend this from Settings → Custom Fields.
        if (!db.Settings.Any(s => s.Key == "custom_fields" && s.EntityId == null))
        {
            const string defaultSchema =
                "{\"provider\":[{\"key\":\"type\",\"label\":\"Type\",\"kind\":\"select\"," +
                "\"options\":[\"Teacher\",\"Supplier\"],\"required\":true,\"position\":0}]," +
                "\"customer\":[]}";
            db.Settings.Add(new Setting
            {
                Key = "custom_fields", Value = defaultSchema, EntityId = null,
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        // ONE test customer only (the operator's own number) — enough to exercise
        // the contact page, chats and AI replies end-to-end without a pile of
        // fake customers to wade through. Idempotent on phone.
        const string testPhone = "+601133199692";
        if (!db.Contacts.Any(c => c.Phone == testPhone))
        {
            db.Contacts.Add(new Contact
            {
                Name = "liewxen",
                Phone = testPhone,
                Email = "liewxen@gmail.com",
                Language = "en",
                Status = "active",
                TenantId = tenant.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
    }
}
