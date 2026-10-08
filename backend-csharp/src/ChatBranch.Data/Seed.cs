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
        AddColumnIfMissing(db, "crm_profile", "contact_id", "INTEGER NULL");
        AddColumnIfMissing(db, "integrations_integration", "owner_user_id", "INTEGER NULL");
        AddColumnIfMissing(db, "integrations_integration", "scopes", "TEXT NULL");
        AddColumnIfMissing(db, "crm_thread", "team_id", "INTEGER NULL");
        AddColumnIfMissing(db, "crm_knowledgeitem", "published", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "crm_knowledgeitem", "slug", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_knowledgeitem", "category", "TEXT NOT NULL DEFAULT ''");
        AddTableIfMissing(db, "crm_team",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_team\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"name\" TEXT NOT NULL, \"description\" TEXT NOT NULL, "
            + "\"auto_assign\" INTEGER NOT NULL, \"last_assignee\" TEXT NOT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_automationrule",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_automationrule\" PRIMARY KEY AUTOINCREMENT, "
            + "\"entity_id\" INTEGER NULL, \"name\" TEXT NOT NULL, \"description\" TEXT NOT NULL, "
            + "\"event\" TEXT NOT NULL, \"active\" INTEGER NOT NULL, \"position\" INTEGER NOT NULL, "
            + "\"conditions\" TEXT NOT NULL, \"actions\" TEXT NOT NULL, "
            + "\"times_fired\" INTEGER NOT NULL, \"last_fired_at\" TEXT NULL, "
            + "\"created_at\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_teammember",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_teammember\" PRIMARY KEY AUTOINCREMENT, "
            + "\"team_id\" INTEGER NOT NULL, \"user_id\" INTEGER NOT NULL, \"created_at\" TEXT NOT NULL");
        AddColumnIfMissing(db, "crm_notification", "category", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_notification", "ref_key", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(db, "crm_profile", "preferences", "TEXT NULL");
        AddTableIfMissing(db, "crm_aidraft",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aidraft\" PRIMARY KEY AUTOINCREMENT, "
            + "\"thread_id\" INTEGER NULL, \"thread_key\" TEXT NOT NULL, \"channel\" TEXT NOT NULL, "
            + "\"contact_id\" INTEGER NULL, \"entity_id\" INTEGER NULL, \"account_id\" INTEGER NULL, "
            + "\"kind\" TEXT NOT NULL, \"content\" TEXT NOT NULL, \"original_content\" TEXT NOT NULL, "
            + "\"context\" TEXT NOT NULL, \"status\" TEXT NOT NULL, \"hold_reason\" TEXT NOT NULL, "
            + "\"decided_by\" TEXT NOT NULL, \"decided_at\" TEXT NULL, \"reject_reason\" TEXT NOT NULL, "
            + "\"send_after\" TEXT NULL, \"created_at\" TEXT NOT NULL, \"sent_at\" TEXT NULL, "
            + "\"sent_message_id\" INTEGER NULL, \"error\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
        AddTableIfMissing(db, "crm_aiconversationstate",
            "\"id\" INTEGER NOT NULL CONSTRAINT \"PK_crm_aiconversationstate\" PRIMARY KEY AUTOINCREMENT, "
            + "\"thread_id\" INTEGER NOT NULL, \"follow_ups_sent\" INTEGER NOT NULL, "
            + "\"last_follow_up_at\" TEXT NULL, \"next_follow_up_at\" TEXT NULL, "
            + "\"anchor_message_id\" INTEGER NULL, \"note\" TEXT NOT NULL, \"updated_at\" TEXT NOT NULL");
    }

    /// <summary>
    /// Create a table EnsureCreated() will never add to an existing database.
    /// The DDL must match what EF expects (snake_case columns, TEXT dates) or
    /// the first query against it throws.
    /// </summary>
    private static void AddTableIfMissing(AppDbContext db, string table, string ddl)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE TABLE IF NOT EXISTS \"{table}\" ({ddl})";
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
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

        // Production is clean by default: only the essential bootstrap (tenant,
        // entity, roles, one superadmin login, custom-field schema) is seeded.
        // The demo clutter — the extra admin/agent POC logins and the sample
        // "liewxen" test contact — is seeded ONLY when SEED_DEMO is truthy, so a
        // real deployment never shows fake contacts. Set SEED_DEMO=1 for a demo.
        var seedDemoRaw = Environment.GetEnvironmentVariable("SEED_DEMO");
        var seedDemo = seedDemoRaw is "1" or "true" or "TRUE" or "yes";

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

        // The superadmin login is essential (without it nobody can get in); the
        // admin/agent logins are demo-only. Allow the superadmin password to be
        // set from the environment so a real deployment isn't stuck on the POC
        // default — falls back to the POC password when unset.
        var superadminPassword =
            Environment.GetEnvironmentVariable("SUPERADMIN_PASSWORD") is { Length: > 0 } p
                ? p
                : DemoUsers.First(d => d.Role == Roles.SuperAdmin).Password;

        foreach (var demo in DemoUsers)
        {
            if (!seedDemo && demo.Role != Roles.SuperAdmin) continue;

            var password = demo.Role == Roles.SuperAdmin ? superadminPassword : demo.Password;
            var user = db.Users.Include(u => u.Profile)
                .FirstOrDefault(u => u.Username == demo.Username);
            if (user == null)
            {
                user = new AppUser
                {
                    Username = demo.Username,
                    Password = DjangoPasswordHasher.Hash(password),
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
        // fake customers to wade through. Idempotent on phone. Demo-only: a real
        // deployment starts with an empty contact list (SEED_DEMO gates it).
        const string testPhone = "+601133199692";
        if (seedDemo && !db.Contacts.Any(c => c.Phone == testPhone))
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
