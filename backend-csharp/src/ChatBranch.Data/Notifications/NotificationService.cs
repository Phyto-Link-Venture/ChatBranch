using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

// Deliberately in the root ChatBranch.Data namespace: every writer (the flow
// engine, automations, the endpoints) reaches it without a new using.
namespace ChatBranch.Data;

/// <summary>
/// The notification categories — the mute groups (organization-wide Setting
/// rows <c>notif_&lt;category&gt;</c>), the per-user in-app switches, and the
/// inbox filter all speak this one vocabulary.
/// </summary>
public static class NotificationCategories
{
    public const string Messages = "messages";
    public const string AccountHealth = "account_health";
    public const string Flows = "flows";
    public const string Admin = "admin";
    public const string System = "system";

    /// <summary>Display order everywhere (settings pages, inbox filter).</summary>
    public static readonly string[] All = { Messages, AccountHealth, Flows, Admin, System };

    public static bool IsKnown(string? category) =>
        category != null && All.Contains(category);

    /// <summary>
    /// A known category wins; otherwise derive it from the kind the way the
    /// Django notifier did (message → messages, anything else → admin). Rows
    /// written before the category column existed read through this too.
    /// </summary>
    public static string Normalize(string? category, string? kind) =>
        IsKnown(category) ? category! : kind == "message" ? Messages : Admin;
}

/// <summary>Who a notification is addressed to, before preferences filter it.</summary>
public sealed class NotificationAudience
{
    public IReadOnlyCollection<string> RoleSlugs { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<int> UserIds { get; init; } = Array.Empty<int>();
    /// <summary>When set, admins are limited to this tenant (superadmins always
    /// pass) — a conversation in one tenant never pages another tenant's admins.</summary>
    public int? TenantId { get; init; }

    public static NotificationAudience ForRoles(params string[] roles) => new() { RoleSlugs = roles };
    public static NotificationAudience ForUsers(IEnumerable<int> ids) => new() { UserIds = ids.Distinct().ToArray() };
    public static NotificationAudience Admins(int? tenantId = null) => new()
    {
        RoleSlugs = new[] { Roles.SuperAdmin, Roles.Admin },
        TenantId = tenantId,
    };
}

public sealed class NotifyOptions
{
    /// <summary>The conversation the alert is about — drives the per-user
    /// "only conversations assigned to me / my teams" filter.</summary>
    public Thread? Thread { get; init; }

    /// <summary>Collapse key: at most ONE unread row per (user, key). A repeat
    /// refreshes that row (text, link, time) instead of stacking a new one.</summary>
    public string RefKey { get; init; } = "";

    /// <summary>Title used when a repeat refreshes an existing unread row
    /// (e.g. "New messages from …"); null keeps the normal title.</summary>
    public string? CollapsedTitle { get; init; }

    /// <summary>false = only add the rows; the caller's SaveChanges commits them
    /// together with its own work.</summary>
    public bool Save { get; init; } = true;
}

/// <summary>
/// A user's own notification choices, stored under <c>notifications</c> in
/// <see cref="Profile.Preferences"/>: one in-app switch per category plus
/// "only conversations assigned to me or my teams" for messages. Anything
/// missing or unreadable means ON — preferences fail OPEN.
/// </summary>
public sealed class UserNotificationPrefs
{
    public Dictionary<string, bool> InApp { get; } =
        NotificationCategories.All.ToDictionary(c => c, _ => true);
    public bool OnlyAssigned { get; set; }

    public bool Enabled(string category) => InApp.GetValueOrDefault(category, true);

    public static UserNotificationPrefs FromProfileJson(string? json) =>
        From(ProfilePreferences.Load(json)["notifications"] as JsonObject);

    public static UserNotificationPrefs From(JsonObject? node)
    {
        var prefs = new UserNotificationPrefs();
        if (node == null) return prefs;
        try
        {
            foreach (var cat in NotificationCategories.All)
                if (node[cat] is JsonValue v && v.TryGetValue<bool>(out var on)) prefs.InApp[cat] = on;
            if (node["only_assigned"] is JsonValue oa && oa.TryGetValue<bool>(out var only))
                prefs.OnlyAssigned = only;
        }
        catch { /* fail open */ }
        return prefs;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        foreach (var (cat, on) in InApp) o[cat] = on;
        o["only_assigned"] = OnlyAssigned;
        return o;
    }
}

/// <summary>Read/write helpers for the Profile.preferences JSON document.</summary>
public static class ProfilePreferences
{
    /// <summary>The document as a mutable object — never null; garbage reads as empty.</summary>
    public static JsonObject Load(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    public static string? Save(JsonObject doc) => doc.Count == 0 ? null : doc.ToJsonString();
}

/// <summary>
/// The ONE way a notification gets written. Every writer — the inbound-message
/// hook, the flow engine, automations, integration and channel-account changes
/// — funnels through <see cref="NotifyAsync"/>, which applies, in order:
/// <list type="number">
///   <item>the organization-wide mute for the category (Setting
///   <c>notif_&lt;category&gt;</c> = "false"; fail-open on any read error);</item>
///   <item>each recipient's own in-app switch for the category;</item>
///   <item>for messages, the recipient's "only assigned to me / my teams" filter;</item>
///   <item>the collapse key (one unread row per user per conversation).</item>
/// </list>
/// It never throws: a notification is a side effect and must not break the
/// webhook, flow tick or settings save that triggered it.
/// </summary>
public static class NotificationService
{
    public const string OrgMutePrefix = "notif_";

    public static int Notify(AppDbContext db, string category, string kind, string title,
        string body, string link, NotificationAudience audience, NotifyOptions? options = null) =>
        NotifyAsync(db, category, kind, title, body, link, audience, options).GetAwaiter().GetResult();

    public static async Task<int> NotifyAsync(AppDbContext db, string category, string kind,
        string title, string body, string link, NotificationAudience audience,
        NotifyOptions? options = null, CancellationToken ct = default)
    {
        var added = new List<Notification>();
        try
        {
            category = NotificationCategories.Normalize(category, kind);
            if (!OrgEnabled(db, category)) return 0;
            var opts = options ?? new NotifyOptions();

            var recipients = await RecipientsAsync(db, audience, ct);
            if (recipients.Count == 0) return 0;

            var now = DateTime.UtcNow;
            var written = 0;
            foreach (var r in recipients)
            {
                var prefs = UserNotificationPrefs.FromProfileJson(r.Preferences);
                if (!prefs.Enabled(category)) continue;
                if (category == NotificationCategories.Messages && prefs.OnlyAssigned
                    && opts.Thread != null && !await IsMineAsync(db, opts.Thread, r.UserId, r.Username, ct))
                    continue;

                if (opts.RefKey.Length > 0)
                {
                    var open = await db.Notifications.FirstOrDefaultAsync(
                        n => n.UserId == r.UserId && n.RefKey == opts.RefKey && !n.Read, ct);
                    if (open != null)
                    {
                        open.Title = Cap(opts.CollapsedTitle ?? title, 160);
                        open.Body = Cap(body, 400);
                        open.Link = Cap(link, 300);
                        open.Kind = kind;
                        open.Category = category;
                        open.CreatedAt = now;   // resurfaces at the top of the bell
                        written++;
                        continue;
                    }
                }

                var row = new Notification
                {
                    UserId = r.UserId,
                    Kind = kind,
                    Category = category,
                    Title = Cap(title, 160),
                    Body = Cap(body, 400),
                    Link = Cap(link, 300),
                    RefKey = Cap(opts.RefKey, 120),
                    CreatedAt = now,
                };
                db.Notifications.Add(row);
                added.Add(row);
                written++;
            }

            if (opts.Save && written > 0) await db.SaveChangesAsync(ct);
            return written;
        }
        catch
        {
            // Leave nothing half-added behind for the caller's next SaveChanges.
            foreach (var row in added)
                try { db.Entry(row).State = EntityState.Detached; } catch { }
            return 0;
        }
    }

    /// <summary>
    /// A customer wrote in. Addressed to the thread's assignee, else every member
    /// of its team, else the admins of its tenant; collapsed to one unread row
    /// per user per conversation (later messages refresh it).
    /// </summary>
    public static int NotifyInboundMessage(AppDbContext db, Thread thread, string text) =>
        NotifyInboundMessageAsync(db, thread, text).GetAwaiter().GetResult();

    public static async Task<int> NotifyInboundMessageAsync(AppDbContext db, Thread thread,
        string text, CancellationToken ct = default)
    {
        try
        {
            if (thread.Id == 0) return 0;
            var audience = await InboundAudienceAsync(db, thread, ct);
            var who = string.IsNullOrWhiteSpace(thread.Name) ? thread.Key : thread.Name.Trim();
            var body = string.IsNullOrWhiteSpace(text) ? "(attachment)" : text.Trim();
            var link = $"/chats?channel={Uri.EscapeDataString(thread.Channel)}&key={Uri.EscapeDataString(thread.Key)}";
            return await NotifyAsync(db, NotificationCategories.Messages, "message",
                $"New message from {who}", body, link, audience, new NotifyOptions
                {
                    Thread = thread,
                    RefKey = $"thread:{thread.Id}",
                    CollapsedTitle = $"New messages from {who}",
                }, ct);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Is the organization-wide switch for this category on? Only the exact
    /// value "false" mutes; a missing row or a read failure means ON.
    /// </summary>
    public static bool OrgEnabled(AppDbContext db, string category)
    {
        try
        {
            var key = OrgMutePrefix + category;
            var raw = db.Settings.AsNoTracking()
                .Where(s => s.Key == key && s.EntityId == null)
                .Select(s => s.Value).FirstOrDefault();
            return !string.Equals((raw ?? "").Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    // ---- internals ------------------------------------------------------------

    private sealed record Recipient(int UserId, string Username, string? Preferences);

    private static async Task<List<Recipient>> RecipientsAsync(AppDbContext db,
        NotificationAudience audience, CancellationToken ct)
    {
        var byId = new Dictionary<int, Recipient>();

        if (audience.UserIds.Count > 0)
        {
            var ids = audience.UserIds.ToList();
            var rows = await db.Users.AsNoTracking()
                .Where(u => ids.Contains(u.Id) && u.IsActive)
                .Select(u => new { u.Id, u.Username, Prefs = u.Profile != null ? u.Profile.Preferences : null })
                .ToListAsync(ct);
            foreach (var u in rows) byId[u.Id] = new Recipient(u.Id, u.Username, u.Prefs);
        }

        if (audience.RoleSlugs.Count > 0)
        {
            var roles = audience.RoleSlugs.ToList();
            var tenant = audience.TenantId;
            var rows = await db.Profiles.AsNoTracking()
                .Where(p => roles.Contains(p.Role) && p.User != null && p.User.IsActive)
                .Where(p => tenant == null || p.Role == Roles.SuperAdmin || p.TenantId == tenant)
                .Select(p => new { p.UserId, p.User!.Username, p.Preferences })
                .ToListAsync(ct);
            foreach (var p in rows) byId[p.UserId] = new Recipient(p.UserId, p.Username, p.Preferences);
        }

        return byId.Values.OrderBy(r => r.UserId).ToList();
    }

    private static async Task<NotificationAudience> InboundAudienceAsync(AppDbContext db,
        Thread thread, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(thread.Assignee))
        {
            var name = thread.Assignee.Trim();
            var id = await db.Users.AsNoTracking()
                .Where(u => u.Username == name && u.IsActive)
                .Select(u => (int?)u.Id).FirstOrDefaultAsync(ct);
            if (id is int uid) return NotificationAudience.ForUsers(new[] { uid });
        }
        if (thread.TeamId is int teamId)
        {
            var members = await db.TeamMembers.AsNoTracking()
                .Where(m => m.TeamId == teamId && m.User != null && m.User.IsActive)
                .Select(m => m.UserId).ToListAsync(ct);
            if (members.Count > 0) return NotificationAudience.ForUsers(members);
        }
        int? tenantId = null;
        if (thread.EntityId is int eid)
            tenantId = await db.Entities.AsNoTracking()
                .Where(e => e.Id == eid).Select(e => (int?)e.TenantId).FirstOrDefaultAsync(ct);
        return NotificationAudience.Admins(tenantId);
    }

    private static async Task<bool> IsMineAsync(AppDbContext db, Thread thread, int userId,
        string username, CancellationToken ct)
    {
        // Mine = assigned to me, or owned by a team I belong to. A conversation
        // with neither is nobody's own, so "only mine" skips it.
        if (!string.IsNullOrWhiteSpace(thread.Assignee)
            && string.Equals(thread.Assignee.Trim(), username, StringComparison.OrdinalIgnoreCase))
            return true;
        if (thread.TeamId is int teamId)
            return await db.TeamMembers.AsNoTracking()
                .AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct);
        return false;
    }

    private static string Cap(string? s, int max)
    {
        s ??= "";
        return s.Length <= max ? s : s[..max];
    }
}
