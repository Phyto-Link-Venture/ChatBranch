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
    public async Task<(int Added, int Updated, int Removed, string Error)> SyncAsync(CancellationToken ct = default)
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
            Upsert(a, eid, pid);
            if (isNew) added++; else updated++;
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
