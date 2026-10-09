using System.Security.Cryptography;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Tags;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.TestLab;

/// <summary>Everything the lab fenced off, as loaded by <see cref="LabFixture.Ensure"/>.</summary>
public sealed class LabState
{
    public int TenantId { get; init; }
    public Entity Hq { get; init; } = null!;
    public Entity North { get; init; } = null!;
    public Entity South { get; init; } = null!;
    public ChannelAccount Conversation { get; init; } = null!;
    public ChannelAccount Business { get; init; } = null!;
    public ChannelAccount Telegram { get; init; } = null!;
    public ChannelAccount Widget { get; init; } = null!;
    public ChannelAccount NorthLine { get; init; } = null!;
    public ChannelAccount SouthLine { get; init; } = null!;
    public Team FrontDesk { get; init; } = null!;
    public Team NorthDesk { get; init; } = null!;
    public Team SouthDesk { get; init; } = null!;
    public Tag LabTag { get; init; } = null!;
    public AppUser HqAgent { get; init; } = null!;
    public AppUser NorthAgent { get; init; } = null!;
    public AppUser SouthAgent { get; init; } = null!;
    public AppUser NoAccess { get; init; } = null!;

    public IReadOnlyList<int> EntityIds => new[] { Hq.Id, North.Id, South.Id };
}

/// <summary>
/// The fenced test data. Everything a scenario touches lives under ONE
/// top-level entity, <c>test-lab</c> ("Test lab") with two branches, created on
/// demand: sandbox channel accounts (a WhatsApp conversation line on the
/// Baileys provider with NO gateway id — so it routes like a real number but
/// every send stores locally; a Cloud business line with no token; a Telegram
/// bot with no token; a website widget; one line per branch), teams whose
/// members are the lab's own <c>testlab_*</c> users (so inbound bells reach
/// them, not your staff), a <c>test-lab</c> contact tag, and Setting rows on
/// the lab entities that make the AI answer instantly (no typing pauses, no
/// review) whatever the organization's defaults are. <see cref="LabReset"/>
/// deletes all of it and nothing else.
///
/// Why the conversation line matters: routing picks an outlet's OWN numbers
/// before the organization-wide ones, so the lab owning a "configured"
/// (Baileys) number keeps every lab conversation on lab numbers — a lab
/// message can never go out on one of your real accounts.
/// </summary>
public static class LabFixture
{
    public const string HqSlug = "test-lab";
    public const string NorthSlug = "test-lab-north";
    public const string SouthSlug = "test-lab-south";
    public const string UserPrefix = "testlab_";
    public const string RolePrefix = "testlab-";
    public const string NoAccessRole = "testlab-noaccess";
    public const string TagName = "test-lab";
    /// <summary>Every lab email address lives under the reserved .invalid TLD — it can never deliver.</summary>
    public const string EmailDomain = "testlab.invalid";
    public const string AccountPrefix = "Lab · ";
    /// <summary>Custom-field keys the lab adds to the org schema (removed again by reset).</summary>
    public const string FieldPrefix = "testlab_";

    /// <summary>The Setting rows on every lab entity (all entity-scoped keys).</summary>
    public static readonly (string Key, string Value)[] EntitySettings =
    {
        ("ai_reply_mode", "instant"),
        ("ai_review_mode", "auto"), ("ai_review_first_n", "0"), ("ai_review_notify", "false"),
        ("ai_wait_seconds", "0"), ("ai_typing", "false"), ("ai_split", "off"), ("ai_ack_skip", "false"),
        ("ai_followup", "false"), ("ai_learning_mode", "off"),
        ("reminder_ai_default", "off"),
        ("business_redirect", "true"), ("business_redirect_target", "conversation"),
    };

    /// <summary>Per-inbox config every lab account carries (the account wins over any Setting).</summary>
    public static Dictionary<string, object?> BaseConfig(int? teamId, string role = "", bool? aiOn = true)
    {
        var quietOff = "{" + string.Join(",", AccountDelivery.Days.Select(d =>
            $"\"{d}\":{{\"on\":false,\"start\":\"00:00\",\"end\":\"00:00\"}}")) + "}";
        var cfg = new Dictionary<string, object?>
        {
            ["identify_unknown"] = "off",
            ["ai_review_mode"] = "auto",
            ["ai_review_first_n"] = "0",
            ["ai_review_notify"] = "false",
            ["ai_wait_seconds"] = "0",
            ["ai_typing"] = "false",
            ["ai_split"] = "off",
            ["ai_ack_skip"] = "false",
            ["ai_followup"] = "false",
            ["queue_min_gap_s"] = "0",
            ["queue_max_gap_s"] = "0",
            ["quiet_schedule"] = quietOff,
            ["testlab"] = "true",
        };
        // null = no inbox default: the account ROLE decides (a reminder number answers with no AI)
        if (aiOn is bool on) cfg["ai_default"] = on ? "on" : "off";
        if (teamId is int t) cfg["team_id"] = t.ToString();
        if (role.Length > 0) cfg["role"] = role;
        return cfg;
    }

    // ---- ensure ---------------------------------------------------------------------

    /// <summary>Get-or-create the whole fixture (idempotent; re-applies the lab settings).</summary>
    public static LabState Ensure(AppDbContext db, ScopingService sc, int? tenantHint)
    {
        var tenantId = tenantHint is int t && db.Tenants.Any(x => x.Id == t)
            ? t
            : db.Tenants.OrderBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefault()
              ?? CreateTenant(db);

        var hq = EnsureEntity(db, tenantId, HqSlug, "Test lab", null);
        var north = EnsureEntity(db, hq.TenantId, NorthSlug, "Test lab · North", hq.Id);
        var south = EnsureEntity(db, hq.TenantId, SouthSlug, "Test lab · South", hq.Id);
        foreach (var e in new[] { hq, north, south })
            foreach (var (k, v) in EntitySettings)
                if (sc.SettingValue(k, e.Id) != v || !db.Settings.Any(s => s.Key == k && s.EntityId == e.Id))
                    sc.SetSetting(k, v, e.Id);
        // the master switch is set on the head office only (branches inherit it)
        if (!db.Settings.Any(s => s.Key == AiControlKeys.Master && s.EntityId == hq.Id && s.Value == "on"))
            sc.SetSetting(AiControlKeys.Master, "on", hq.Id);

        var hqAgent = EnsureUser(db, "testlab_hq", Roles.Agent, hq.TenantId, hq.Id);
        var northAgent = EnsureUser(db, "testlab_north", Roles.Agent, hq.TenantId, north.Id);
        var southAgent = EnsureUser(db, "testlab_south", Roles.Agent, hq.TenantId, south.Id);
        EnsureNoAccessRole(db);
        var noAccess = EnsureUser(db, "testlab_noaccess", NoAccessRole, hq.TenantId, null);

        var front = EnsureTeam(db, "Test lab · Front desk", hq.Id, true, hqAgent.Id);
        var northDesk = EnsureTeam(db, "Test lab · North desk", north.Id, true, northAgent.Id);
        var southDesk = EnsureTeam(db, "Test lab · South desk", south.Id, true, southAgent.Id);

        var conversation = EnsureAccount(db, "Lab conversation line", ChatBranch.Domain.Channels.WhatsApp,
            Providers.Baileys, hq.Id, BaseConfig(front.Id, AccountRoles.Conversation),
            extra: new() { ["phone"] = "60300000101" });
        var business = EnsureAccount(db, "Lab business line", ChatBranch.Domain.Channels.WhatsApp,
            Providers.Cloud, hq.Id, BaseConfig(front.Id, AccountRoles.Reminder, aiOn: null));
        var telegram = EnsureAccount(db, "Lab Telegram bot", ChatBranch.Domain.Channels.Telegram, "bot", hq.Id,
            BaseConfig(front.Id, aiOn: false), extra: new() { ["bot_username"] = "testlab_demo_bot" });
        var widget = EnsureAccount(db, "Lab website widget", ChatBranch.Domain.Channels.Website, "widget", hq.Id,
            BaseConfig(front.Id), extra: new()
            {
                ["title"] = "Test lab", ["greeting"] = "Hi! This is the Test lab widget.", ["ask_identity"] = "true",
            });
        var northLine = EnsureAccount(db, "Lab North line", ChatBranch.Domain.Channels.WhatsApp, Providers.Baileys,
            north.Id, BaseConfig(northDesk.Id, AccountRoles.Conversation));
        var southLine = EnsureAccount(db, "Lab South line", ChatBranch.Domain.Channels.WhatsApp, Providers.Baileys,
            south.Id, BaseConfig(southDesk.Id, AccountRoles.Conversation));

        var tag = db.Tags.FirstOrDefault(x => x.EntityId == hq.Id && x.AppliesTo == TagService.ContactTarget
                                              && x.Name == TagName);
        if (tag == null)
        {
            tag = new Tag
            {
                Name = TagName, Slug = TagService.UniqueSlug(db, TagName), AppliesTo = TagService.ContactTarget,
                Color = "ochre", EntityId = hq.Id,
                Description = "Created by the Test lab — removed by “Reset test data”.",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Tags.Add(tag);
            db.SaveChanges();
        }

        return new LabState
        {
            TenantId = hq.TenantId, Hq = hq, North = north, South = south,
            Conversation = conversation, Business = business, Telegram = telegram, Widget = widget,
            NorthLine = northLine, SouthLine = southLine,
            FrontDesk = front, NorthDesk = northDesk, SouthDesk = southDesk, LabTag = tag,
            HqAgent = hqAgent, NorthAgent = northAgent, SouthAgent = southAgent, NoAccess = noAccess,
        };
    }

    /// <summary>The lab's head office, if the fixture exists (any tenant).</summary>
    public static Entity? FindHq(AppDbContext db) =>
        db.Entities.AsNoTracking().Where(e => e.Slug == HqSlug && e.ParentId == null).OrderBy(e => e.Id)
            .FirstOrDefault();

    private static int CreateTenant(AppDbContext db)
    {
        var tenant = new Tenant
        {
            Name = "Demo Tenant", Slug = "demo", Status = "active",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant.Id;
    }

    private static Entity EnsureEntity(AppDbContext db, int tenantId, string slug, string name, int? parentId)
    {
        var row = db.Entities.FirstOrDefault(e => e.Slug == slug && e.TenantId == tenantId)
                  ?? db.Entities.FirstOrDefault(e => e.Slug == slug && e.ParentId == parentId);
        if (row != null)
        {
            if (row.ParentId != parentId || row.Name != name)
            {
                row.ParentId = parentId;
                row.Name = name;
                row.UpdatedAt = DateTime.UtcNow;
                db.SaveChanges();
            }
            return row;
        }
        row = new Entity
        {
            TenantId = tenantId, Slug = slug, Name = name, ParentId = parentId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Entities.Add(row);
        db.SaveChanges();
        return row;
    }

    private static void EnsureNoAccessRole(AppDbContext db)
    {
        if (!db.Roles.Any(r => r.Slug == NoAccessRole))
        {
            db.Roles.Add(new Role
            {
                Slug = NoAccessRole, Label = "Test lab · no access", IsSystem = false, CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        // a fresh role grants NOTHING (the same all-false matrix POST /api/auth/roles seeds)
        var have = db.RolePermissions.Where(p => p.Role == NoAccessRole).ToList();
        // superadmin holds the whole catalog — the list of every capability id
        var catalog = Endpoints.AdminEndpoints.CapabilitiesForAsync(db, Roles.SuperAdmin).GetAwaiter().GetResult();
        foreach (var cap in catalog)
        {
            var row = have.FirstOrDefault(p => p.Capability == cap);
            if (row == null)
                db.RolePermissions.Add(new RolePermission
                {
                    Role = NoAccessRole, Capability = cap, Allowed = false, UpdatedAt = DateTime.UtcNow,
                });
            else if (row.Allowed)
            {
                row.Allowed = false;
                row.UpdatedAt = DateTime.UtcNow;
            }
        }
        db.SaveChanges();
    }

    /// <summary>A lab login (never signed into: its password is random and thrown away).</summary>
    private static AppUser EnsureUser(AppDbContext db, string username, string role, int tenantId, int? entityId)
    {
        var user = db.Users.Include(u => u.Profile).ThenInclude(p => p!.Entities)
            .FirstOrDefault(u => u.Username == username);
        if (user == null)
        {
            user = new AppUser
            {
                Username = username, IsActive = true,
                Password = DjangoPasswordHasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(24))),
            };
            db.Users.Add(user);
            db.SaveChanges();
        }
        if (!user.IsActive)
        {
            user.IsActive = true;
            db.SaveChanges();
        }
        var profile = user.Profile;
        if (profile == null)
        {
            profile = new Profile { UserId = user.Id, Role = role, TenantId = tenantId };
            db.Profiles.Add(profile);
            db.SaveChanges();
            user.Profile = profile;
        }
        if (profile.Role != role || profile.TenantId != tenantId)
        {
            profile.Role = role;
            profile.TenantId = tenantId;
            db.SaveChanges();
        }
        db.Entry(profile).Collection(p => p.Entities).Load();
        var wanted = entityId is int eid ? new[] { eid } : Array.Empty<int>();
        if (!profile.Entities.Select(e => e.Id).OrderBy(x => x).SequenceEqual(wanted))
        {
            profile.Entities.Clear();
            foreach (var id in wanted) profile.Entities.Add(db.Entities.Find(id)!);
            db.SaveChanges();
        }
        return user;
    }

    /// <summary>A lab team in this entity (get-or-create by name) whose members are exactly these users.</summary>
    public static Team EnsureTeam(AppDbContext db, string name, int entityId, bool autoAssign, params int[] userIds)
    {
        var team = db.Teams.Include(t => t.Members).FirstOrDefault(t => t.Name == name && t.EntityId == entityId);
        if (team == null)
        {
            team = new Team
            {
                Name = name, EntityId = entityId, AutoAssign = autoAssign,
                Description = "Created by the Test lab.",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Teams.Add(team);
            db.SaveChanges();
        }
        if (team.AutoAssign != autoAssign)
        {
            team.AutoAssign = autoAssign;
            db.SaveChanges();
        }
        var have = team.Members.Select(m => m.UserId).OrderBy(x => x).ToList();
        if (!have.SequenceEqual(userIds.OrderBy(x => x)))
        {
            db.TeamMembers.RemoveRange(team.Members.ToList());
            db.SaveChanges();
            foreach (var uid in userIds)
                db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = uid, CreatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        return team;
    }

    /// <summary>
    /// A lab inbox (get-or-create by name in that entity), RESET to a sandbox:
    /// no token, no gateway id, health unknown, exactly this config. A scenario
    /// that needs its own inbox settings (review mode, quiet hours, unknown
    /// customers…) gets its own account this way, reused run after run.
    /// </summary>
    public static ChannelAccount EnsureAccount(AppDbContext db, string name, string channel, string provider,
        int entityId, Dictionary<string, object?> config, Dictionary<string, object?>? extra = null)
    {
        var row = db.ChannelAccounts.FirstOrDefault(a => a.Name == name && a.EntityId == entityId && a.Channel == channel);
        var cfg = new Dictionary<string, object?>(config);
        if (extra != null) foreach (var (k, v) in extra) cfg[k] = v;
        if (row == null)
        {
            row = new ChannelAccount
            {
                Name = name, Channel = channel, Provider = provider, EntityId = entityId, Owner = "testlab",
                ExternalId = channel == ChatBranch.Domain.Channels.Website
                    ? Endpoints.WidgetEndpoints.NewWidgetToken() : "",
                CreatedAt = DateTime.UtcNow,
            };
            db.ChannelAccounts.Add(row);
        }
        // never a live credential: sends store locally ("sandbox"), nothing leaves the machine
        row.Provider = provider;
        row.AccessToken = "";
        row.AppSecret = "";
        row.WabaId = "";
        if (channel != ChatBranch.Domain.Channels.Website) row.ExternalId = "";
        row.Config = cfg;
        row.HealthStatus = channel == ChatBranch.Domain.Channels.Website ? Health.Healthy : Health.Unknown;
        row.HealthDetail = "Test lab sandbox account";
        row.FailStreak = 0;
        db.SaveChanges();
        return row;
    }
}
