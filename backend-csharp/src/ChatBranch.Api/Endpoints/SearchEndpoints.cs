using System.Security.Claims;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The top-bar command palette's backend: <c>GET /api/search?q=&amp;types=&amp;limit=&amp;entity=</c>.
///
/// One request answers every result type the caller may see, grouped by type,
/// at most 8 per type. Each type is gated by the SAME capability as the page it
/// opens and bounded by the SAME scope as that page's list endpoint, so the
/// palette can never be used to peek at rows the screens would hide:
///
/// <list type="table">
///   <item><term>contacts</term><description>contacts.view · tenant + assigned-entity scope (ContactsEndpoints.ScopeContacts) · ?entity narrows to that subtree</description></item>
///   <item><term>conversations</term><description>chats.view · the inbox rule (ShellScope) · matches contact name, address AND message text</description></item>
///   <item><term>templates</term><description>canned replies (templates.manage) + script templates (script_templates.manage) · inherited scope</description></item>
///   <item><term>knowledge</term><description>settings.ai.manage · inherited scope</description></item>
///   <item><term>flows</term><description>flows.manage · inherited scope</description></item>
///   <item><term>users</term><description>users.manage · superadmin everyone, admin own tenant (the /api/v1/users tiering)</description></item>
///   <item><term>entities</term><description>entities.view · AdminEndpoints.AccessibleEntitiesAsync</description></item>
/// </list>
///
/// Pages are searched client-side from the nav the user can see. Every result
/// is <c>{type, id, title, subtitle, url}</c> (+ <c>channel</c> on
/// conversations so the palette can show the channel's logo).
/// </summary>
public static class SearchEndpoints
{
    public const int MinChars = 2;
    public const int MaxPerType = 8;

    private static readonly string[] AllTypes =
        { "contacts", "conversations", "templates", "knowledge", "flows", "users", "entities" };

    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/search", async (string? q, string? types, int? limit, string? entity,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var term = (q ?? "").Trim();
            if (term.Length > 120) term = term[..120];
            var wanted = ParseTypes(types);
            var per = Math.Clamp(limit ?? MaxPerType, 1, MaxPerType);
            if (term.Length < MinChars)
                return Results.Ok(new { q = term, min_chars = MinChars, results = Array.Empty<object>(), counts = new { } });

            var entityId = await InboxEndpoints.EntityId(db, entity);
            var like = ShellScope.Like(term);
            var hits = new List<SearchHit>();

            if (wanted.Contains("contacts") && await ShellScope.HasCapAsync(db, me, "contacts.view"))
                hits.AddRange(await ContactsAsync(db, sc, me, entity, like, per));
            if (wanted.Contains("conversations") && await ShellScope.HasCapAsync(db, me, "chats.view"))
                hits.AddRange(await ConversationsAsync(db, sc, me, entityId, term, like, per));
            if (wanted.Contains("templates"))
                hits.AddRange(await TemplatesAsync(db, sc, me, entityId, like, per));
            if (wanted.Contains("knowledge") && await ShellScope.HasCapAsync(db, me, "settings.ai.manage"))
                hits.AddRange(await KnowledgeAsync(db, sc, entityId, term, like, per));
            if (wanted.Contains("flows") && await ShellScope.HasCapAsync(db, me, "flows.manage"))
                hits.AddRange(await FlowsAsync(db, sc, entityId, like, per));
            if (wanted.Contains("users") && await ShellScope.HasCapAsync(db, me, "users.manage"))
                hits.AddRange(await UsersAsync(db, me, like, per));
            if (wanted.Contains("entities") && await ShellScope.HasCapAsync(db, me, "entities.view"))
                hits.AddRange(await EntitiesAsync(db, me, term, per));

            return Results.Ok(new
            {
                q = term,
                min_chars = MinChars,
                results = hits.Select(h => h.ToWire()),
                counts = hits.GroupBy(h => h.Type).ToDictionary(g => g.Key, g => g.Count()),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    private record SearchHit(string Type, string Id, string Title, string Subtitle, string Url,
        string? Channel = null)
    {
        public object ToWire() => Channel is null
            ? new { type = Type, id = Id, title = Title, subtitle = Subtitle, url = Url }
            : new { type = Type, id = Id, title = Title, subtitle = Subtitle, url = Url, channel = Channel };
    }

    private static HashSet<string> ParseTypes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new HashSet<string>(AllTypes);
        var picked = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).Where(AllTypes.Contains).ToHashSet();
        return picked.Count > 0 ? picked : new HashSet<string>(AllTypes);
    }

    // ---- contacts ------------------------------------------------------------

    private static async Task<List<SearchHit>> ContactsAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, string? entity, string like, int per)
    {
        var query = ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc);
        if (!string.IsNullOrWhiteSpace(entity) && entity is not ("all" or "__all__"))
        {
            // Same narrowing as GET /api/contacts?entity=: the picked outlet and
            // everything under it, intersected with the caller's own scope.
            var picked = await InboxEndpoints.EntityId(db, entity);
            var ids = picked is int pid ? sc.PickSubtree(pid) : new List<int>();
            query = query.Where(c => c.EntityLinks.Any(l => ids.Contains(l.EntityId)));
        }
        var rows = await query
            .Where(c => EF.Functions.Like(c.Name, like, "\\") || EF.Functions.Like(c.Phone, like, "\\")
                        || EF.Functions.Like(c.Email, like, "\\") || EF.Functions.Like(c.CompanyName, like, "\\"))
            .OrderByDescending(c => c.UpdatedAt).Take(per)
            .Select(c => new { c.Id, c.Name, c.Phone, c.Email, c.CompanyName })
            .ToListAsync();
        return rows.Select(c => new SearchHit("contact", c.Id.ToString(),
            c.Name.Length > 0 ? c.Name : (c.Phone.Length > 0 ? c.Phone : c.Email),
            string.Join(" · ", new[] { c.Phone, c.Email, c.CompanyName }.Where(s => !string.IsNullOrWhiteSpace(s))),
            $"/contacts/{c.Id}")).ToList();
    }

    // ---- conversations -------------------------------------------------------

    private static async Task<List<SearchHit>> ConversationsAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, int? entityId, string term, string like, int per)
    {
        var scoped = ShellScope.ScopeThreads(db.Threads.AsNoTracking(),
            ShellScope.ThreadEntityIds(db, sc, me, entityId));

        // Identity matches first (who the conversation is with), then threads
        // whose MESSAGES mention the term — private notes included, since the
        // operator wrote them and the inbox shows them.
        var byIdentity = await scoped
            .Where(t => EF.Functions.Like(t.Name, like, "\\") || EF.Functions.Like(t.Address, like, "\\")
                        || EF.Functions.Like(t.Key, like, "\\")
                        || (t.Contact != null && EF.Functions.Like(t.Contact.Name, like, "\\")))
            .OrderByDescending(t => t.UpdatedAt).Take(per)
            .Select(t => new ThreadRow(t.Id, t.Channel, t.Key, t.Name, t.Address,
                t.Contact != null ? t.Contact.Name : null))
            .ToListAsync();

        var byText = new List<ThreadRow>();
        var snippets = new Dictionary<int, string>();
        if (byIdentity.Count < per)
        {
            var already = byIdentity.Select(t => t.Id).ToList();
            var msgHits = await db.Messages.AsNoTracking()
                .Where(m => EF.Functions.Like(m.Text, like, "\\") && !already.Contains(m.ThreadId)
                            && scoped.Any(t => t.Id == m.ThreadId))
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new { m.ThreadId, m.Text })
                .Take(200).ToListAsync();
            foreach (var m in msgHits)
                if (!snippets.ContainsKey(m.ThreadId)) snippets[m.ThreadId] = m.Text;
            var ids = snippets.Keys.Take(per - byIdentity.Count).ToList();
            byText = (await db.Threads.AsNoTracking().Where(t => ids.Contains(t.Id))
                    .Select(t => new ThreadRow(t.Id, t.Channel, t.Key, t.Name, t.Address,
                        t.Contact != null ? t.Contact.Name : null))
                    .ToListAsync())
                .OrderBy(t => ids.IndexOf(t.Id)).ToList();
        }

        return byIdentity.Concat(byText).Select(t =>
        {
            var who = !string.IsNullOrWhiteSpace(t.ContactName) ? t.ContactName!
                : t.Name.Length > 0 ? t.Name : (t.Address.Length > 0 ? t.Address : t.Key);
            var sub = snippets.TryGetValue(t.Id, out var text)
                ? $"{ChannelLabel(t.Channel)} · “{ShellScope.SnipAround(text, term)}”"
                : $"{ChannelLabel(t.Channel)} · {(t.Address.Length > 0 ? t.Address : t.Key)}";
            return new SearchHit("conversation", $"{t.Channel}:{t.Key}", who, sub,
                ShellScope.ChatUrl(t.Channel, t.Key), t.Channel);
        }).ToList();
    }

    private record ThreadRow(int Id, string Channel, string Key, string Name, string Address, string? ContactName);

    internal static string ChannelLabel(string channel) => channel switch
    {
        Channels.WhatsApp => "WhatsApp",
        Channels.Email => "Email",
        Channels.Telegram => "Telegram",
        Channels.Facebook => "Messenger",
        Channels.Instagram => "Instagram",
        "website" => "Website chat",
        "voice" => "Voice",
        _ when ChatBranch.Data.Channels.MoreChannels.Handles(channel) =>
            ChatBranch.Data.Channels.MoreChannels.Label(channel),
        _ => channel.Length > 0 ? char.ToUpperInvariant(channel[0]) + channel[1..] : "Chat",
    };

    // ---- library ---------------------------------------------------------------

    private static async Task<List<SearchHit>> TemplatesAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, int? entityId, string like, int per)
    {
        var outv = new List<SearchHit>();
        if (await ShellScope.HasCapAsync(db, me, "templates.manage"))
        {
            var canned = await sc.ScopeInherited(db.CannedResponses.AsNoTracking(), entityId)
                .Where(c => EF.Functions.Like(c.ShortCode, like, "\\") || EF.Functions.Like(c.Content, like, "\\"))
                .OrderBy(c => c.ShortCode).Take(per)
                .Select(c => new { c.Id, c.ShortCode, c.Content }).ToListAsync();
            outv.AddRange(canned.Select(c => new SearchHit("template", $"canned:{c.Id}",
                "/" + c.ShortCode, "Reply template · " + ShellScope.Snip(c.Content, 70), "/templates")));
        }
        if (outv.Count < per && await ShellScope.HasCapAsync(db, me, "script_templates.manage"))
        {
            var scripts = await sc.ScopeInherited(db.ScriptTemplates.AsNoTracking(), entityId)
                .Where(s => EF.Functions.Like(s.Name, like, "\\") || EF.Functions.Like(s.Content, like, "\\"))
                .OrderBy(s => s.Name).Take(per - outv.Count)
                .Select(s => new { s.Id, s.Name, s.Content }).ToListAsync();
            outv.AddRange(scripts.Select(s => new SearchHit("template", $"script:{s.Id}",
                s.Name, "Call script · " + ShellScope.Snip(s.Content, 70), "/script-templates")));
        }
        return outv;
    }

    private static async Task<List<SearchHit>> KnowledgeAsync(AppDbContext db, ScopingService sc,
        int? entityId, string term, string like, int per)
    {
        var rows = await sc.ScopeInherited(db.KnowledgeItems.AsNoTracking(), entityId)
            .Where(k => EF.Functions.Like(k.Title, like, "\\") || EF.Functions.Like(k.Question, like, "\\")
                        || EF.Functions.Like(k.Content, like, "\\") || EF.Functions.Like(k.Category, like, "\\"))
            .OrderByDescending(k => k.Priority).ThenByDescending(k => k.Id).Take(per)
            .Select(k => new { k.Id, k.Kind, k.Title, k.Question, k.Content }).ToListAsync();
        return rows.Select(k => new SearchHit("knowledge", k.Id.ToString(),
            k.Title.Length > 0 ? k.Title : (k.Question.Length > 0 ? k.Question : ShellScope.Snip(k.Content, 60)),
            $"{KindLabel(k.Kind)} · {ShellScope.SnipAround(k.Content, term, 70)}",
            "/knowledge")).ToList();
    }

    private static string KindLabel(string kind) => kind switch
    {
        "fact" => "Business fact",
        "policy" => "Policy",
        "faq" => "FAQ",
        "event" => "Event",
        "notice" => "Notice",
        _ => "Knowledge",
    };

    private static async Task<List<SearchHit>> FlowsAsync(AppDbContext db, ScopingService sc,
        int? entityId, string like, int per)
    {
        var rows = await sc.ScopeInherited(db.Workflows.AsNoTracking(), entityId)
            .Where(w => EF.Functions.Like(w.Name, like, "\\") || EF.Functions.Like(w.Hint, like, "\\"))
            .OrderBy(w => w.Position).ThenBy(w => w.Id).Take(per)
            .Select(w => new { w.Id, w.FlowId, w.Name, w.Hint, w.Active }).ToListAsync();
        return rows.Select(w =>
        {
            var id = w.FlowId.Length > 0 ? w.FlowId : w.Id.ToString();
            return new SearchHit("flow", id,
                w.Name.Length > 0 ? w.Name : "Untitled flow",
                (w.Active ? "Live" : "Draft") + (w.Hint.Length > 0 ? " · " + ShellScope.Snip(w.Hint, 70) : ""),
                "/journeys?flow=" + Uri.EscapeDataString(id)); // opens that journey in the editor
        }).ToList();
    }

    // ---- admin -----------------------------------------------------------------

    private static async Task<List<SearchHit>> UsersAsync(AppDbContext db, ClaimsPrincipal me,
        string like, int per)
    {
        var role = ShellScope.RoleOf(me);
        var q = db.Users.AsNoTracking().Include(u => u.Profile!).ThenInclude(p => p.Tenant).AsQueryable();
        // The /api/v1/users tiering: superadmin everyone, admin their own tenant,
        // any other holder of users.manage just themselves.
        if (role == Roles.Admin)
        {
            var tenantId = int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : (int?)null;
            q = q.Where(u => u.Profile != null && u.Profile.TenantId == tenantId);
        }
        else if (role != Roles.SuperAdmin)
        {
            var username = me.Identity?.Name ?? "";
            q = q.Where(u => u.Username == username);
        }
        var rows = await q.Where(u => EF.Functions.Like(u.Username, like, "\\"))
            .OrderBy(u => u.Username).Take(per).ToListAsync();
        return rows.Select(u => new SearchHit("user", u.Id.ToString(), u.Username,
            RoleLabel(AdminEndpoints.RoleOf(u)) + (u.IsActive ? "" : " · deactivated"), "/users")).ToList();
    }

    private static string RoleLabel(string role) => role switch
    {
        Roles.SuperAdmin => "Super admin",
        Roles.Admin => "Admin",
        Roles.Agent => "Agent",
        _ => role.Length > 0 ? char.ToUpperInvariant(role[0]) + role[1..] : "User",
    };

    private static async Task<List<SearchHit>> EntitiesAsync(AppDbContext db, ClaimsPrincipal me,
        string term, int per)
    {
        // Small table, already scoped by the same helper /api/v1/entities uses —
        // match in memory so the comparison is properly case-insensitive.
        var rows = (await AdminEndpoints.AccessibleEntitiesAsync(db, me))
            .Where(e => e.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || e.Slug.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name).Take(per).ToList();
        return rows.Select(e => new SearchHit("entity", e.Slug, e.Name,
            e.Parent != null ? $"Branch of {e.Parent.Name}" : "Main entity", "/entities")).ToList();
    }
}
