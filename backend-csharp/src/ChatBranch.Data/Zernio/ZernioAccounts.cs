using System.Text.Json;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Zernio;

/// <summary>
/// Entity → Zernio profile, and Zernio account → <see cref="ChannelAccount"/>.
///
/// ONE Zernio key serves the deployment; each ChatBranch entity gets its own profile,
/// created on demand with <c>externalId = "cb-entity-&lt;id&gt;"</c>, and the
/// organization level uses the key's DEFAULT profile. Every connected Zernio account
/// of a messaging platform (WhatsApp, Messenger, Instagram, Telegram, SMS) is mirrored
/// as ONE ChannelAccount on its native channel (provider <c>zernio</c>, ExternalId =
/// Zernio's account id, EntityId = its profile's entity — see <see cref="ZernioNative"/>),
/// so routing, inbox settings, health and scoping treat it like any other inbox.
/// Accounts of other platforms (social networks) are not mirrored: out of scope.
/// The mirror is refreshed on the <c>account.*</c> webhooks and by a cheap reconcile on read.
/// </summary>
public sealed class ZernioAccounts
{
    private readonly AppDbContext _db;
    private readonly IZernioClient _zernio;

    public ZernioAccounts(AppDbContext db, IZernioClient zernio)
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
            if (dflt.ValueKind != JsonValueKind.Object) return ("", "the managed connection has no profile yet");
            return (Remember(null, dflt), "");
        }

        var entity = await _db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid, ct);
        if (entity == null) return ("", "unknown entity");
        var externalId = ZernioKeys.ProfileExternalId(eid);
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
                return ("", "the managed connection created the profile but returned no id");
            }
            if (made.Status != 409) return ("", made.Error);
            // a 409 on our OWN externalId means it exists after all
            var again = await _zernio.ListProfilesAsync(externalId, ct);
            var mine = ZernioJson.Arr(again.Json, "profiles")
                .FirstOrDefault(p => ZernioJson.Str(p, "externalId") == externalId);
            if (mine.ValueKind == JsonValueKind.Object) return (Remember(eid, mine), "");
        }
        return ("", "the managed connection refused to create a profile for this entity (name taken)");
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
        if (ZernioKeys.EntityFromExternalId(externalId) is int eid && _db.Entities.Any(e => e.Id == eid))
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

    /// <summary>The inbox (native channel, provider zernio) mirroring a Zernio account, or null.</summary>
    public ChannelAccount? Find(string zernioAccountId) =>
        zernioAccountId.Length == 0
            ? null
            : _db.ChannelAccounts.FirstOrDefault(a => a.Provider == Providers.Zernio && a.ExternalId == zernioAccountId);

    // ---- connect intent --------------------------------------------------------------

    private static string IntentKey(string profileId, string platform) => $"zernio_intent:{profileId}:{platform}";

    /// <summary>
    /// Remember that the next account of this platform in this profile was connected ON PURPOSE
    /// from Channels (POST /api/social/connect): it becomes an inbox even beside another
    /// connection of the same channel.
    /// </summary>
    public void SetIntent(string profileId, string platform)
    {
        if (profileId.Length == 0) return;
        try { new ScopingService(_db).SetSetting(IntentKey(profileId, platform), "inbox"); } catch { }
    }

    private string TakeIntent(string profileId, string platform)
    {
        if (profileId.Length == 0) return "";
        try
        {
            var sc = new ScopingService(_db);
            var v = sc.SettingValue(IntentKey(profileId, platform));
            if (v.Length > 0) sc.SetSetting(IntentKey(profileId, platform), "");
            return v;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Does a NEW Zernio account become an inbox? An explicit connect does; WhatsApp and SMS
    /// always do; Messenger / Instagram / Telegram do unless the level already has a non-Zernio
    /// inbox of that channel (its DMs keep arriving there — no double inbox).
    /// </summary>
    private bool WantsInbox(string platform, string channel, int? entityId, string intent)
    {
        if (intent == "inbox" || platform is "whatsapp" or "sms") return true;
        return !_db.ChannelAccounts.Any(a => a.Channel == channel && a.EntityId == entityId && a.Provider != Providers.Zernio);
    }

    /// <summary>
    /// Create or refresh the inbox of one Zernio account (a /v1/accounts row or a webhook's
    /// account). Null = not a messaging platform, or a new account the level does not want
    /// as an inbox (see <see cref="WantsInbox"/>).
    /// </summary>
    public ChannelAccount? Upsert(JsonElement acct, int? entityId, string profileId, string? intent = null)
    {
        var zid = AccountId(acct);
        var plat = ZernioJson.Str(acct, "platform").ToLowerInvariant();
        var channel = ZernioNative.ChannelFor(plat);
        if (channel == null || zid.Length == 0) return null;
        var row = Find(zid);
        if (row == null)
        {
            var why = intent ?? TakeIntent(profileId, plat);
            if (!WantsInbox(plat, channel, entityId, why)) return null;
        }
        return UpsertRow(acct, row, channel, entityId, profileId);
    }

    private static string AccountId(JsonElement acct) =>
        FirstNonEmpty(ZernioJson.Str(acct, "_id"), ZernioJson.Str(acct, "accountId"), ZernioJson.Str(acct, "id"));

    private ChannelAccount UpsertRow(JsonElement acct, ChannelAccount? row, string channel, int? entityId, string profileId)
    {
        var zid = AccountId(acct);
        var platform = ZernioJson.Str(acct, "platform").ToLowerInvariant();
        if (platform.Length == 0) platform = ZernioNative.PlatformFor(channel);
        var username = ZernioJson.Str(acct, "username");
        var display = ZernioJson.Str(acct, "displayName");
        var hasActive = ZernioJson.At(acct, "isActive").ValueKind is JsonValueKind.True or JsonValueKind.False;
        var active = !hasActive || ZernioJson.Bool(acct, "isActive");
        if (ZernioJson.Bool(acct, "needsReconnection")) active = false;

        var isNew = row == null;
        row ??= new ChannelAccount
        {
            Channel = channel,
            Provider = Providers.Zernio,
            ExternalId = zid,
            CreatedAt = DateTime.UtcNow,
            HealthStatus = Health.Unknown,
            Config = new Dictionary<string, object?>(),
        };
        var cfg = new Dictionary<string, object?>(row.Config ?? new())
        {
            [ZernioNative.AccountIdKey] = zid,
            [ZernioKeys.PlatformKey] = platform,
        };
        if (username.Length > 0) cfg[ZernioKeys.UsernameKey] = username;
        if (display.Length > 0) cfg[ZernioKeys.DisplayNameKey] = display;
        var avatar = ZernioJson.Str(acct, "profilePicture");
        if (avatar.Length > 0) cfg[ZernioKeys.AvatarKey] = avatar;
        var phone = FirstNonEmpty(ZernioJson.Str(acct, "phoneNumber"), ZernioJson.Str(acct, "metadata", "phoneNumber"),
            ZernioJson.Str(acct, "metadata", "displayPhoneNumber"));
        var address = channel switch
        {
            ChatBranch.Domain.Channels.WhatsApp or ChatBranch.Domain.Channels.Sms =>
                FirstNonEmpty(phone, username.Any(char.IsDigit) ? username : ""),
            ChatBranch.Domain.Channels.Facebook => FirstNonEmpty(SelectedPage(zid)?.Name ?? "", display, username),
            _ => username.Length > 0 ? "@" + username.TrimStart('@') : display,
        };
        if (address.Length > 0) cfg[ZernioNative.AddressKey] = address;

        // Just moved here (PATCH /api/social/accounts/{id}): Zernio may still report the OLD
        // profile for a moment — the local binding wins within the grace window.
        int? level = entityId;
        if (!isNew && RecentlyMoved(row) && profileId.Length > 0
            && profileId != ZernioKeys.Cfg(row, ZernioKeys.ProfileIdKey))
        {
            profileId = ZernioKeys.Cfg(row, ZernioKeys.ProfileIdKey);
            level = row.EntityId;
        }
        if (profileId.Length > 0) cfg[ZernioKeys.ProfileIdKey] = profileId;
        cfg[ZernioKeys.StatusKey] = active ? "connected" : "disconnected";
        row.Config = cfg;
        if (ZernioKeys.Cfg(row, ZernioNative.CustomNameKey).Length == 0)
            row.Name = InboxName(channel, username, display, address);
        var rebound = !isNew && row.EntityId != level;
        row.EntityId = level;
        if (isNew)
        {
            row.HealthStatus = active ? Health.Healthy : Health.Disconnected;
            _db.ChannelAccounts.Add(row);
            var created = AuditTrail.Add(_db, "account.create", $"{channel}:{row.Name}", "managed connection",
                source: AuditSources.Webhook, entityId: level);
            // another mirror of this account (the webhook / the onboarding callback / a sync)
            // inserted it first: refresh THAT row instead of failing the request
            if (!TryInsertMirror(row, created))
                return Find(zid) is { } winner && winner.Channel == channel
                    ? UpsertRow(acct, winner, channel, entityId, profileId)
                    : throw new InvalidOperationException($"Zernio account {zid} is mirrored on another channel.");
        }
        else
        {
            var want = active ? Health.Healthy : Health.Disconnected;
            if (row.HealthStatus != want)
                AccountHealth.SetStatus(_db, row, want, active ? "connected" : "the managed connection needs reconnecting",
                    actor: "zernio");
        }
        _db.SaveChanges();
        if (rebound) Rebound(row);
        return row;
    }

    private static string InboxName(string channel, string username, string display, string address)
    {
        var label = channel switch
        {
            ChatBranch.Domain.Channels.WhatsApp => "WhatsApp",
            ChatBranch.Domain.Channels.Facebook => "Messenger",
            ChatBranch.Domain.Channels.Instagram => "Instagram",
            ChatBranch.Domain.Channels.Telegram => "Telegram",
            ChatBranch.Domain.Channels.Sms => "SMS",
            _ => channel,
        };
        var who = channel is ChatBranch.Domain.Channels.Instagram or ChatBranch.Domain.Channels.Telegram && username.Length > 0
            ? "@" + username.TrimStart('@')
            : FirstNonEmpty(channel == ChatBranch.Domain.Channels.Facebook ? address : "", display, address, username, "account");
        var full = $"{who} ({label})";
        return full.Length > 120 ? full[..120] : full;
    }

    private static string FirstNonEmpty(params string[] v) => v.FirstOrDefault(x => x.Length > 0) ?? "";

    /// <summary>The Facebook Page whose Messenger conversations reach a managed Messenger inbox.</summary>
    private SocialPage? SelectedPage(string zernioAccountId) =>
        zernioAccountId.Length == 0
            ? null
            : _db.SocialPages.Local.FirstOrDefault(p => p.ZernioAccountId == zernioAccountId && p.IsSelected)
              ?? _db.SocialPages.AsNoTracking().FirstOrDefault(p => p.ZernioAccountId == zernioAccountId && p.IsSelected);

    /// <summary>A row's level changed: its existing conversations follow (within one company).</summary>
    private void Rebound(ChannelAccount row)
    {
        try
        {
            ChatBranch.Data.Stores.ThreadCompanies.AfterAccountRebound(_db, new ScopingService(_db), row.Id);
            _db.SaveChanges();
        }
        catch { /* the rebind itself stands */ }
    }

    /// <summary>
    /// Save a brand-new mirror row. False = another mirror of the same Zernio account got there
    /// first (the partial unique indexes <c>uniq_channelaccount_zernio_wa</c> /
    /// <c>uniq_channelaccount_zernio_account</c> refused this one): the row and its
    /// "account.create" audit row are detached — left tracked, every later save in this request
    /// would try the same insert again and fail — and the caller refreshes the winner.
    /// Any other failure propagates.
    /// </summary>
    private bool TryInsertMirror(ChannelAccount row, AuditLog created)
    {
        try
        {
            _db.SaveChanges();
            return true;
        }
        catch (DbUpdateException e) when (IsDuplicateMirror(e))
        {
            _db.Entry(row).State = EntityState.Detached;
            _db.Entry(created).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Did a save trip one of the Zernio mirror indexes? PostgreSQL names the index
    /// ("duplicate key value violates unique constraint …", 23505); SQLite names the columns
    /// ("UNIQUE constraint failed: crm_channelaccount.external_id").
    /// </summary>
    internal static bool IsDuplicateMirror(DbUpdateException e)
    {
        var msg = (e.InnerException?.Message ?? e.Message) ?? "";
        return msg.Contains(AppDbContext.ZernioWhatsAppIndex, StringComparison.OrdinalIgnoreCase)
               || msg.Contains(AppDbContext.ZernioAccountIndex, StringComparison.OrdinalIgnoreCase)
               || ((msg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
                   && msg.Contains("external_id", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>How long a move's local binding outranks a Zernio listing that still shows the old profile.</summary>
    public static readonly TimeSpan MoveGrace = TimeSpan.FromMinutes(10);

    private static bool RecentlyMoved(ChannelAccount row) =>
        DateTime.TryParse(ZernioKeys.Cfg(row, ZernioKeys.MovedAtKey), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var at)
        && DateTime.UtcNow - at < MoveGrace;

    /// <summary>
    /// Bind a managed inbox to an entity (null = the organization): the entity's Zernio profile
    /// is ensured (created on demand), Zernio moves the account there, and the inbox and its
    /// existing conversations follow. Status is the HTTP status for a failure (0 = ok).
    /// </summary>
    public async Task<(string Error, int Status)> MoveAsync(ChannelAccount row, int? target, CancellationToken ct = default)
    {
        if (!_zernio.IsConfigured) return (_zernio.NotConfiguredReason, 409);
        var (profileId, err) = await EnsureProfileAsync(target, create: true, ct);
        if (profileId.Length == 0)
            return (err.Length > 0 ? err : "no connection profile for that level", 502);
        if (profileId != ZernioKeys.Cfg(row, ZernioKeys.ProfileIdKey))
        {
            var r = await _zernio.MoveAccountAsync(row.ExternalId, profileId, ct);
            if (!r.Ok) return (r.Error, r.Status is 404 ? 404 : 502);
        }
        row.EntityId = target;
        row.Config = new Dictionary<string, object?>(row.Config ?? new())
        {
            [ZernioKeys.ProfileIdKey] = profileId,
            [ZernioKeys.MovedAtKey] = DateTime.UtcNow.ToString("o"),
        };
        await _db.SaveChangesAsync(ct);
        Rebound(row);
        return ("", 0);
    }

    // ---- Facebook Pages (a managed Messenger inbox) --------------------------------------

    /// <summary>
    /// Mirror a Messenger account's Pages (Zernio's cached list; <paramref name="refresh"/>
    /// re-reads Meta, which Zernio rate-limits to once a minute). Pages Zernio stops listing
    /// go — only when it lists some, so an empty Meta answer never wipes the list.
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
        RefreshAddress(row);
        return (listed.Count, "");
    }

    /// <summary>Zernio's selected Page = the one whose Messenger conversations reach the inbox.</summary>
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
        RefreshAddress(row);
        return ("", 0);
    }

    /// <summary>A Messenger inbox shows its selected Page's name as the address customers see.</summary>
    private void RefreshAddress(ChannelAccount row)
    {
        if (row.Channel != ChatBranch.Domain.Channels.Facebook) return;
        if (SelectedPage(row.ExternalId)?.Name is not { Length: > 0 } pageName) return;
        if (ZernioNative.Address(row) == pageName) return;
        row.Config = new Dictionary<string, object?>(row.Config ?? new()) { [ZernioNative.AddressKey] = pageName };
        _db.SaveChanges();
    }

    /// <summary>Zernio says an account stopped working (or was removed): the inbox stays, marked down.</summary>
    public ChannelAccount? MarkDisconnected(string zernioAccountId, string reason, bool removed)
    {
        var rows = zernioAccountId.Length == 0
            ? new List<ChannelAccount>()
            : _db.ChannelAccounts.Where(a => a.Provider == Providers.Zernio && a.ExternalId == zernioAccountId).ToList();
        if (rows.Count == 0) return null;
        foreach (var row in rows)
        {
            row.Config = new Dictionary<string, object?>(row.Config ?? new())
            {
                [ZernioKeys.StatusKey] = removed ? "removed" : "disconnected",
            };
            AccountHealth.SetStatus(_db, row, Health.Disconnected,
                reason.Length > 0 ? reason : removed ? "the managed connection was removed" : "the managed connection is disconnected",
                actor: "zernio");
        }
        _db.SaveChanges();
        return rows[0];
    }

    /// <summary>
    /// Reconcile every Zernio messaging account into the mirror (profiles first, so each
    /// account lands on its entity). Accounts in profiles ChatBranch does not own, and accounts
    /// of non-messaging platforms, are left alone; a mirrored account Zernio no longer lists
    /// is marked removed (its conversations stay).
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
            // social networks, ads, blogs and CRM connections are no messaging inbox
            if (ZernioNative.ChannelFor(ZernioJson.Str(a, "platform")) == null) continue;
            var pid = ZernioJson.Id(ZernioJson.At(a, "profileId"));
            if (!owner.TryGetValue(pid, out var eid)) continue;
            var id = ZernioJson.Str(a, "_id");
            seen.Add(id);
            var isNew = Find(id) == null;
            var row = Upsert(a, eid, pid);
            if (row == null) continue;
            if (isNew) added++; else updated++;
            if (row.Channel == ChatBranch.Domain.Channels.Facebook)
                try { await SyncPagesAsync(row, refreshPages, ct); } catch { /* the Pages list catches up next time */ }
        }
        var mirrored = (await _db.ChannelAccounts
                .Where(x => x.Provider == Providers.Zernio).ToListAsync(ct))
            .GroupBy(x => x.ExternalId).Select(g => g.First()).ToList();
        foreach (var row in mirrored.Where(r => !seen.Contains(r.ExternalId)
                                                && ZernioKeys.Cfg(r, ZernioKeys.StatusKey) != "removed"))
        {
            // only accounts of profiles we own can be judged missing
            var pid = ZernioKeys.Cfg(row, ZernioKeys.ProfileIdKey);
            if (pid.Length > 0 && !owner.ContainsKey(pid)) continue;
            MarkDisconnected(row.ExternalId, "the managed connection no longer lists this account", removed: true);
            removed++;
        }
        return (added, updated, removed, "");
    }
}
