using System.Text.Json;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Social;

/// <summary>
/// Entity → Zernio profile, and Zernio account → <see cref="ChannelAccount"/>.
///
/// ONE Zernio key serves the deployment; each ChatBranch entity gets its own profile,
/// created on demand with <c>externalId = "cb-entity-&lt;id&gt;"</c>, and the
/// organization level uses the key's DEFAULT profile. Every connected Zernio account
/// is mirrored as a ChannelAccount (channel <c>social</c>, provider <c>zernio</c>,
/// ExternalId = Zernio's account id, EntityId = its profile's entity), so routing,
/// inbox settings, health and scoping treat it like any other inbox. The mirror is
/// refreshed on the <c>account.*</c> webhooks and by a cheap reconcile on read.
/// </summary>
public sealed class SocialAccounts
{
    private readonly AppDbContext _db;
    private readonly IZernioClient _zernio;

    public SocialAccounts(AppDbContext db, IZernioClient zernio)
    {
        _db = db;
        _zernio = zernio;
    }

    // ---- profiles --------------------------------------------------------------

    /// <summary>
    /// The Zernio profile of an entity (null = the organization's default profile),
    /// creating the entity's profile when <paramref name="create"/> and none exists.
    /// </summary>
    public async Task<(string ProfileId, string Error)> EnsureProfileAsync(int? entityId, bool create = true,
        CancellationToken ct = default)
    {
        var known = await _db.SocialProfiles.AsNoTracking()
            .Where(p => p.EntityId == entityId).Select(p => p.ProfileId).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(known)) return (known, "");
        if (!_zernio.IsConfigured) return ("", _zernio.NotConfiguredReason);

        if (entityId is not int eid)
        {
            var list = await _zernio.ListProfilesAsync(null, ct);
            if (!list.Ok) return ("", list.Error);
            var all = ZernioJson.Arr(list.Json, "profiles").ToList();
            var dflt = all.FirstOrDefault(p => ZernioJson.Bool(p, "isDefault"));
            if (dflt.ValueKind != JsonValueKind.Object) dflt = all.FirstOrDefault();
            if (dflt.ValueKind != JsonValueKind.Object) return ("", "the Zernio account has no profile yet");
            return (Remember(null, dflt), "");
        }

        var entity = await _db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid, ct);
        if (entity == null) return ("", "unknown entity");
        var externalId = SocialChannels.ProfileExternalId(eid);
        var found = await _zernio.ListProfilesAsync(externalId, ct);
        if (found.Ok)
        {
            var hit = ZernioJson.Arr(found.Json, "profiles")
                .FirstOrDefault(p => ZernioJson.Str(p, "externalId") == externalId);
            if (hit.ValueKind == JsonValueKind.Object) return (Remember(eid, hit), "");
        }
        if (!create) return ("", "");

        // profile names are unique per Zernio team: the entity's name, else name + id
        foreach (var name in new[] { entity.Name, $"{entity.Name} (ChatBranch {eid})" })
        {
            var made = await _zernio.CreateProfileAsync(name, externalId, ct);
            if (made.Ok)
            {
                var profile = made.At("profile");
                if (ZernioJson.Str(profile, "_id").Length > 0) return (Remember(eid, profile), "");
                return ("", "Zernio created the profile but returned no id");
            }
            if (made.Status != 409) return ("", made.Error);
            // a 409 on our OWN externalId means it exists after all
            var again = await _zernio.ListProfilesAsync(externalId, ct);
            var mine = ZernioJson.Arr(again.Json, "profiles")
                .FirstOrDefault(p => ZernioJson.Str(p, "externalId") == externalId);
            if (mine.ValueKind == JsonValueKind.Object) return (Remember(eid, mine), "");
        }
        return ("", "Zernio refused to create a profile for this entity (name taken)");
    }

    private string Remember(int? entityId, JsonElement profile)
    {
        var id = ZernioJson.Str(profile, "_id");
        var existing = _db.SocialProfiles.FirstOrDefault(p => p.ProfileId == id);
        if (existing == null)
        {
            _db.SocialProfiles.Add(new SocialProfile
            {
                EntityId = entityId,
                ProfileId = id,
                ExternalId = ZernioJson.Str(profile, "externalId"),
                Name = ZernioJson.Str(profile, "name"),
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
        return id;
    }

    /// <summary>The entity a Zernio profile belongs to: our table, else its externalId; (found, entity).</summary>
    public (bool Known, int? EntityId) EntityOfProfile(string profileId, string externalId = "", bool isDefault = false)
    {
        var row = _db.SocialProfiles.AsNoTracking().FirstOrDefault(p => p.ProfileId == profileId);
        if (row != null) return (true, row.EntityId);
        if (SocialChannels.EntityFromExternalId(externalId) is int eid && _db.Entities.Any(e => e.Id == eid))
        {
            _db.SocialProfiles.Add(new SocialProfile
            {
                EntityId = eid, ProfileId = profileId, ExternalId = externalId, CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
            return (true, eid);
        }
        if (isDefault && !_db.SocialProfiles.Any(p => p.EntityId == null))
        {
            _db.SocialProfiles.Add(new SocialProfile { ProfileId = profileId, CreatedAt = DateTime.UtcNow });
            _db.SaveChanges();
            return (true, null);
        }
        return (false, null);
    }

    // ---- accounts --------------------------------------------------------------

    public ChannelAccount? Find(string zernioAccountId) =>
        zernioAccountId.Length == 0
            ? null
            : _db.ChannelAccounts.FirstOrDefault(a => a.Channel == ChatBranch.Domain.Channels.Social
                                                      && a.ExternalId == zernioAccountId);

    /// <summary>Create or refresh the mirror of one Zernio account (a /v1/accounts row or a webhook's account).</summary>
    public ChannelAccount Upsert(JsonElement acct, int? entityId, string profileId)
    {
        var id = ZernioJson.Str(acct, "_id");
        if (id.Length == 0) id = ZernioJson.Str(acct, "accountId");
        if (id.Length == 0) id = ZernioJson.Str(acct, "id");
        var platform = ZernioJson.Str(acct, "platform").ToLowerInvariant();
        var username = ZernioJson.Str(acct, "username");
        var display = ZernioJson.Str(acct, "displayName");
        var hasActive = ZernioJson.At(acct, "isActive").ValueKind is JsonValueKind.True or JsonValueKind.False;
        var active = !hasActive || ZernioJson.Bool(acct, "isActive");
        if (ZernioJson.Bool(acct, "needsReconnection")) active = false;

        var row = Find(id);
        var isNew = row == null;
        row ??= new ChannelAccount
        {
            Channel = ChatBranch.Domain.Channels.Social,
            Provider = Providers.Zernio,
            ExternalId = id,
            CreatedAt = DateTime.UtcNow,
            HealthStatus = Health.Unknown,
            // a public reply written by a machine needs an operator's say-so
            Config = new Dictionary<string, object?> { [InboxSettings.AiDefaultKey] = "off" },
        };
        var cfg = new Dictionary<string, object?>(row.Config ?? new());
        if (platform.Length > 0) cfg[SocialChannels.PlatformKey] = platform;
        if (username.Length > 0) cfg[SocialChannels.UsernameKey] = username;
        if (display.Length > 0) cfg[SocialChannels.DisplayNameKey] = display;
        var avatar = ZernioJson.Str(acct, "profilePicture");
        if (avatar.Length > 0) cfg[SocialChannels.AvatarKey] = avatar;
        var url = ZernioJson.Str(acct, "profileUrl");
        if (url.Length > 0) cfg[SocialChannels.ProfileUrlKey] = url;
        // Just moved here (PATCH /api/social/accounts/{id}): Zernio may still report the
        // OLD profile for a moment — the local binding wins within the grace window.
        if (!isNew && RecentlyMoved(row) && profileId.Length > 0
            && profileId != SocialChannels.Cfg(row, SocialChannels.ProfileIdKey))
        {
            profileId = SocialChannels.Cfg(row, SocialChannels.ProfileIdKey);
            entityId = row.EntityId;
        }
        if (profileId.Length > 0) cfg[SocialChannels.ProfileIdKey] = profileId;
        cfg[SocialChannels.StatusKey] = active ? "connected" : "disconnected";
        row.Config = cfg;
        row.Name = SocialChannels.AccountName(SocialChannels.Cfg(row, SocialChannels.PlatformKey),
            SocialChannels.Cfg(row, SocialChannels.UsernameKey), SocialChannels.Cfg(row, SocialChannels.DisplayNameKey));
        row.EntityId = entityId;
        if (isNew)
        {
            row.HealthStatus = active ? Health.Healthy : Health.Disconnected;
            _db.ChannelAccounts.Add(row);
            AuditTrail.Add(_db, "account.create", $"social:{row.Name}", "connected through Zernio",
                source: AuditSources.Webhook, entityId: entityId);
        }
        else
        {
            var want = active ? Health.Healthy : Health.Disconnected;
            if (row.HealthStatus != want)
                AccountHealth.SetStatus(_db, row, want, active ? "connected in Zernio" : "needs reconnecting in Zernio",
                    actor: "zernio");
        }
        _db.SaveChanges();
        return row;
    }

    /// <summary>How long a move's local binding outranks a Zernio listing that still shows the old profile.</summary>
    public static readonly TimeSpan MoveGrace = TimeSpan.FromMinutes(10);

    private static bool RecentlyMoved(ChannelAccount row) =>
        DateTime.TryParse(SocialChannels.Cfg(row, SocialChannels.MovedAtKey), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var at)
        && DateTime.UtcNow - at < MoveGrace;

    /// <summary>
    /// Bind a social account to an entity (null = the organization): the entity's Zernio
    /// profile is ensured (created on demand), Zernio moves the account there, and the
    /// mirror follows — the account's EntityId and profile id, its existing social
    /// conversations, and its not-yet-published posts (when every account of the post
    /// now sits at the target). Status is the HTTP status for a failure (0 = ok).
    /// </summary>
    public async Task<(int Threads, int Posts, string Error, int Status)> MoveAsync(ChannelAccount row, int? target,
        CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured) return (0, 0, _zernio.NotConfiguredReason, 409);
        var (profileId, err) = await EnsureProfileAsync(target, create: true, ct);
        if (profileId.Length == 0)
            return (0, 0, err.Length > 0 ? err : "Zernio has no profile for that level", 502);
        if (profileId != SocialChannels.Cfg(row, SocialChannels.ProfileIdKey))
        {
            var r = await _zernio.MoveAccountAsync(row.ExternalId, profileId, ct);
            if (!r.Ok) return (0, 0, r.Error, r.Status is 404 ? 404 : 502);
        }

        row.EntityId = target;
        row.Config = new Dictionary<string, object?>(row.Config ?? new())
        {
            [SocialChannels.ProfileIdKey] = profileId,
            [SocialChannels.MovedAtKey] = DateTime.UtcNow.ToString("o"),
        };
        await _db.SaveChangesAsync(ct);
        // a Page bound to an outlet that is no longer under the account's level follows the account again
        if (target is int t)
        {
            var sc = new ScopingService(_db);
            foreach (var page in _db.SocialPages.Where(p => p.AccountId == row.Id && p.EntityId != null).ToList())
                if (!sc.EntityChain(page.EntityId).Contains(t))
                {
                    page.EntityId = null;
                    page.UpdatedAt = DateTime.UtcNow;
                }
        }
        var threads = RescopeThreads(row, target);
        var posts = RescopePosts(row, target);
        await _db.SaveChangesAsync(ct);
        return (threads, posts, "", 0);
    }

    /// <summary>The account's social conversations (keyed kind:&lt;account&gt;:…) follow it to <paramref name="target"/>.</summary>
    private int RescopeThreads(ChannelAccount row, int? target)
    {
        var sc = new ScopingService(_db);
        var company = sc.CompanyOf(target);
        var prefixes = SocialChannels.Kinds.Select(k => $"{k}:{row.ExternalId}:").ToList();
        var rows = _db.Threads.Where(t => t.Channel == ChatBranch.Domain.Channels.Social
                                          && (t.Key.StartsWith(prefixes[0]) || t.Key.StartsWith(prefixes[1])
                                              || t.Key.StartsWith(prefixes[2])))
            .ToList()
            .Where(t => SocialChannels.ParseKey(t.Key).AccountId == row.ExternalId)
            .ToList();
        var moved = 0;
        foreach (var t in rows)
        {
            var changed = t.EntityId != target;
            t.EntityId = target;
            // the company slot follows, unless (channel, key) is already taken there
            if (t.CompanyEntityId != company)
            {
                var free = company is int c
                    ? sc.CompanySlotFree(t, c)
                    : !_db.Threads.Any(x => x.Id != t.Id && x.Channel == t.Channel && x.Key == t.Key
                                            && x.CompanyEntityId == null);
                if (free)
                {
                    t.CompanyEntityId = company;
                    changed = true;
                }
            }
            if (!changed) continue;
            t.UpdatedAt = DateTime.UtcNow;
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Not-yet-published posts (draft, scheduled, failed) that go to this account follow
    /// it — when every one of their targets now sits at <paramref name="target"/> (a post
    /// publishes through one entity's accounts only; a Page bound to its own outlet keeps it).
    /// </summary>
    private int RescopePosts(ChannelAccount row, int? target)
    {
        var pending = new[] { SocialPosts.Draft, SocialPosts.Scheduled, SocialPosts.Failed };
        var posts = _db.SocialPosts.Where(p => pending.Contains(p.Status) && p.EntityId != target).ToList()
            .Where(p => p.AccountIds.Any(e => SocialChannels.ParseTarget(e).AccountId == row.ExternalId)).ToList();
        var moved = 0;
        foreach (var p in posts)
        {
            if (!p.AccountIds.All(e => TargetEntity(e) is (true, var at) && at == target)) continue;
            p.EntityId = target;
            p.UpdatedAt = DateTime.UtcNow;
            moved++;
        }
        return moved;
    }

    // ---- Facebook Pages --------------------------------------------------------------

    /// <summary>The level a post target publishes at: a Page's own outlet, else its account's; (found, entity).</summary>
    public (bool Found, int? EntityId) TargetEntity(string entry)
    {
        var (acct, pageId) = SocialChannels.ParseTarget(entry);
        var row = _db.ChannelAccounts.Local.FirstOrDefault(a => a.Channel == ChatBranch.Domain.Channels.Social
                                                                && a.ExternalId == acct)
                  ?? _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Channel == ChatBranch.Domain.Channels.Social
                                                                            && a.ExternalId == acct);
        if (row == null) return (false, null);
        if (pageId.Length == 0) return (true, row.EntityId);
        var page = _db.SocialPages.Local.FirstOrDefault(p => p.ZernioAccountId == acct && p.PageId == pageId)
                   ?? _db.SocialPages.AsNoTracking().FirstOrDefault(p => p.ZernioAccountId == acct && p.PageId == pageId);
        return page == null ? (false, null) : (true, page.EntityId ?? row.EntityId);
    }

    /// <summary>
    /// Mirror a facebook account's Pages (Zernio's cached list; <paramref name="refresh"/>
    /// re-reads Meta, which Zernio rate-limits to once a minute). Pages Zernio stops listing
    /// go — only when it lists some, so an empty Meta answer never wipes the bindings.
    /// </summary>
    public async Task<(int Pages, string Error)> SyncPagesAsync(ChannelAccount row, bool refresh = false,
        CancellationToken ct = default)
    {
        var r = await _zernio.FacebookPagesAsync(row.ExternalId, refresh, ct);
        if (!r.Ok) return (0, r.Error);
        var selected = ZernioJson.Str(r.Json, "selectedPageId");
        var listed = ZernioJson.Arr(r.Json, "pages").Where(p => ZernioJson.Str(p, "id").Length > 0).ToList();
        var existing = _db.SocialPages.Where(p => p.ZernioAccountId == row.ExternalId).ToList();
        foreach (var p in listed)
        {
            var id = ZernioJson.Str(p, "id");
            var page = existing.FirstOrDefault(x => x.PageId == id);
            if (page == null)
            {
                page = new SocialPage
                {
                    AccountId = row.Id, ZernioAccountId = row.ExternalId, PageId = id, CreatedAt = DateTime.UtcNow,
                };
                _db.SocialPages.Add(page);
                existing.Add(page);
            }
            page.AccountId = row.Id;
            page.Name = ZernioJson.Str(p, "name");
            page.Username = ZernioJson.Str(p, "username");
            page.Category = ZernioJson.Str(p, "category");
            var pic = ZernioJson.Str(p, "picture", "data", "url");
            if (pic.Length == 0) pic = ZernioJson.Str(p, "picture");
            if (pic.Length > 0) page.Avatar = pic;
            page.IsSelected = id == selected;
            page.UpdatedAt = DateTime.UtcNow;
        }
        if (listed.Count > 0)
        {
            var keep = listed.Select(p => ZernioJson.Str(p, "id")).ToHashSet();
            foreach (var gone in existing.Where(x => !keep.Contains(x.PageId))) _db.SocialPages.Remove(gone);
        }
        await _db.SaveChangesAsync(ct);
        return (listed.Count, "");
    }

    /// <summary>Zernio's selected Page = the one whose comments and messages reach the inbox.</summary>
    public async Task<(string Error, int Status)> SelectPageAsync(ChannelAccount row, SocialPage page,
        CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured) return (_zernio.NotConfiguredReason, 409);
        var r = await _zernio.SelectFacebookPageAsync(row.ExternalId, page.PageId, ct);
        if (!r.Ok) return (r.Error, r.Status is 400 or 404 or 409 ? r.Status : 502);
        foreach (var p in _db.SocialPages.Where(p => p.ZernioAccountId == row.ExternalId).ToList())
            p.IsSelected = p.Id == page.Id;
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ("", 0);
    }

    /// <summary>
    /// Bind a Page to an outlet (null = it follows the account), and its not-yet-published
    /// single-target posts follow. Returns the number of posts re-scoped.
    /// </summary>
    public async Task<int> BindPageAsync(ChannelAccount row, SocialPage page, int? entityId, CancellationToken ct = default)
    {
        page.EntityId = entityId;
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var level = entityId ?? row.EntityId;
        var target = SocialChannels.Target(row.ExternalId, page.PageId);
        var pending = new[] { SocialPosts.Draft, SocialPosts.Scheduled, SocialPosts.Failed };
        var moved = 0;
        foreach (var p in _db.SocialPosts.Where(p => pending.Contains(p.Status) && p.EntityId != level).ToList()
                     .Where(p => p.AccountIds.Contains(target)))
        {
            if (!p.AccountIds.All(e => TargetEntity(e) is (true, var at) && at == level)) continue;
            p.EntityId = level;
            p.UpdatedAt = DateTime.UtcNow;
            moved++;
        }
        await _db.SaveChangesAsync(ct);
        return moved;
    }

    /// <summary>Zernio says an account stopped working (or was removed): the inbox stays, marked down.</summary>
    public ChannelAccount? MarkDisconnected(string zernioAccountId, string reason, bool removed)
    {
        var row = Find(zernioAccountId);
        if (row == null) return null;
        row.Config = new Dictionary<string, object?>(row.Config ?? new())
        {
            [SocialChannels.StatusKey] = removed ? "removed" : "disconnected",
        };
        AccountHealth.SetStatus(_db, row, Health.Disconnected,
            reason.Length > 0 ? reason : removed ? "removed in Zernio" : "disconnected in Zernio", actor: "zernio");
        _db.SaveChanges();
        return row;
    }

    /// <summary>
    /// Reconcile every Zernio account into the mirror (profiles first, so each account
    /// lands on its entity). Accounts in profiles ChatBranch does not own are left alone;
    /// a mirrored account Zernio no longer lists is marked removed (its conversations stay).
    /// </summary>
    public async Task<(int Added, int Updated, int Removed, string Error)> SyncAsync(bool refreshPages = false,
        CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured) return (0, 0, 0, _zernio.NotConfiguredReason);
        var profiles = await _zernio.ListProfilesAsync(null, ct);
        if (!profiles.Ok) return (0, 0, 0, profiles.Error);
        var owner = new Dictionary<string, int?>();
        foreach (var p in ZernioJson.Arr(profiles.Json, "profiles"))
        {
            var pid = ZernioJson.Str(p, "_id");
            var (known, eid) = EntityOfProfile(pid, ZernioJson.Str(p, "externalId"), ZernioJson.Bool(p, "isDefault"));
            if (known) owner[pid] = eid;
        }
        var accounts = await _zernio.ListAccountsAsync(null, ct);
        if (!accounts.Ok) return (0, 0, 0, accounts.Error);

        int added = 0, updated = 0, removed = 0;
        var seen = new HashSet<string>();
        foreach (var a in ZernioJson.Arr(accounts.Json, "accounts"))
        {
            var platform = ZernioJson.Str(a, "platform");
            // ads, blogs and CRM connections are no inbox and no posting target
            if (!SocialChannels.ConnectPlatforms.Contains(platform)) continue;
            var pid = ZernioJson.Id(ZernioJson.At(a, "profileId"));
            if (!owner.TryGetValue(pid, out var eid)) continue;
            var id = ZernioJson.Str(a, "_id");
            seen.Add(id);
            var isNew = Find(id) == null;
            var row = Upsert(a, eid, pid);
            if (isNew) added++; else updated++;
            if (SocialChannels.Platform(row) == "facebook")
                try { await SyncPagesAsync(row, refreshPages, ct); } catch { /* the Pages list catches up next time */ }
        }
        var mirrored = await _db.ChannelAccounts
            .Where(x => x.Channel == ChatBranch.Domain.Channels.Social).ToListAsync(ct);
        foreach (var row in mirrored.Where(r => !seen.Contains(r.ExternalId)
                                                && SocialChannels.Cfg(r, SocialChannels.StatusKey) != "removed"))
        {
            // only accounts of profiles we own can be judged missing
            var pid = SocialChannels.Cfg(row, SocialChannels.ProfileIdKey);
            if (pid.Length > 0 && !owner.ContainsKey(pid)) continue;
            MarkDisconnected(row.ExternalId, "Zernio no longer lists this account", removed: true);
            removed++;
        }
        return (added, updated, removed, "");
    }
}
