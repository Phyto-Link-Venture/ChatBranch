using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Contacts;

/// <summary>What one discovery pass did for a contact.</summary>
public record DiscoveryResult(List<ContactIdentity> Identities, int Added, int Removed, int Queued, bool LookupAvailable);

/// <summary>
/// Identity auto-discovery (the registry behind the channel hub): a contact's
/// phone becomes its WhatsApp, voice and — when an SMS channel exists — SMS
/// identities, its email the email identity, with no customer action needed.
///
/// Identities live in <see cref="ContactIdentity"/> rows, NOT as empty threads:
/// a thread per imported contact would put thousands of empty conversations in
/// the inbox. The conversation itself is created the moment a message flows —
/// inbound phone / email matching links it to the contact, and a send from the
/// hub creates it through the hub's own registration path (entity sync and
/// routing apply there).
///
/// WhatsApp numbers are VERIFIED only through a connected QR (Baileys) number —
/// the Cloud API has no lookup — and never by messaging them. Verdicts are
/// cached 30 days, per number, across contacts; the lookups themselves run in
/// the background (<see cref="WhatsAppNumberChecker"/>, ≤ 1 per 2 s).
/// </summary>
public class ContactIdentityService
{
    /// <summary>
    /// The SMS channel id. A string literal on purpose: the channels work adds
    /// <c>Channels.Sms</c> in parallel — swap this for it once both are merged.
    /// SMS identities are only registered while an <c>sms</c> account exists.
    /// </summary>
    public const string SmsChannel = "sms";
    public const string VoiceChannel = "voice";
    public const int CacheDays = 30;
    /// <summary>A lookup that failed (gateway down…) is retried after this long.</summary>
    public const int UnknownRetryHours = 24;

    /// <summary>Apps where a phone number cannot be looked up — they need an invite link instead.</summary>
    public static readonly string[] NoLookupChannels =
    {
        ChatBranch.Domain.Channels.Facebook, ChatBranch.Domain.Channels.Instagram,
        ChatBranch.Domain.Channels.Telegram, "line", "viber", "wechat", "tiktok", "douyin", "zalo",
    };

    public const string NoQrDetail =
        "No WhatsApp QR number is connected — the Cloud API cannot look numbers up, so this one is unverified.";

    private readonly AppDbContext _db;
    private readonly ScopingService _sc;
    private readonly TimeProvider _clock;

    public ContactIdentityService(AppDbContext db, ScopingService sc, TimeProvider? clock = null)
    {
        _db = db;
        _sc = sc;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Connected QR (Baileys) numbers that can answer a lookup.</summary>
    public static List<ChannelAccount> LookupAccounts(AppDbContext db) =>
        db.ChannelAccounts.AsNoTracking()
            .Where(a => a.Channel == ChatBranch.Domain.Channels.WhatsApp && a.Provider == Providers.Baileys
                        && a.ExternalId != "")
            .OrderBy(a => a.Id).ToList()
            .Where(a => !Routing.IsDown(a)).ToList();

    public bool SmsAvailable() => _db.ChannelAccounts.AsNoTracking().Any(a => a.Channel == SmsChannel);

    /// <summary>
    /// Register every identity the contact's phone / email / WhatsApp chats
    /// imply, drop the discovered ones a changed phone or email made stale, and
    /// queue WhatsApp lookups. Idempotent — safe on every create, edit and
    /// import. <paramref name="recheck"/> ("Find channels") also re-queues
    /// unverified numbers; a fresh verdict (≤ 30 days) is always kept.
    /// </summary>
    public DiscoveryResult Discover(Contact contact, string source = "auto", bool recheck = false, bool save = true)
    {
        var now = Now;
        var cc = PhoneNormalizer.CountryCode(_sc);
        var e164 = PhoneNormalizer.ToE164(contact.Phone, cc);
        var digits = PhoneMatch.Digits(e164);
        var email = (contact.Email ?? "").Trim().ToLowerInvariant();
        if (!email.Contains('@')) email = "";

        var existing = _db.ContactIdentities.Where(i => i.ContactId == contact.Id).ToList();
        var removed = 0;
        // a changed phone / email: the identities DISCOVERED from the old value go
        // (hand-added, invited and chat-derived ones stay)
        foreach (var old in existing.Where(i => i.Source is "auto" or "import").ToList())
        {
            var stale = old.Channel switch
            {
                ChatBranch.Domain.Channels.WhatsApp or SmsChannel => old.Address != digits,
                VoiceChannel => old.Address != e164,
                ChatBranch.Domain.Channels.Email => old.Address != email,
                _ => false,
            };
            if (!stale) continue;
            _db.ContactIdentities.Remove(old);
            existing.Remove(old);
            removed++;
        }

        var added = 0;
        ContactIdentity Upsert(string channel, string address, string src)
        {
            var row = existing.FirstOrDefault(i => i.Channel == channel && i.Address == address);
            if (row != null) return row;
            row = new ContactIdentity
            {
                ContactId = contact.Id, Channel = channel, Address = address, Source = src,
                CreatedAt = now, UpdatedAt = now,
            };
            _db.ContactIdentities.Add(row);
            existing.Add(row);
            added++;
            return row;
        }

        if (digits.Length > 0)
        {
            Upsert(ChatBranch.Domain.Channels.WhatsApp, digits, source);
            Upsert(VoiceChannel, e164, source);
            if (SmsAvailable()) Upsert(SmsChannel, digits, source);
        }
        if (email.Length > 0) Upsert(ChatBranch.Domain.Channels.Email, email, source);
        // every WhatsApp chat already linked to the contact is an identity too, so
        // a second number added by hand gets its lookup verdict as well
        var waKeys = _db.Threads.AsNoTracking()
            .Where(t => t.ContactId == contact.Id && t.Channel == ChatBranch.Domain.Channels.WhatsApp)
            .Select(t => t.Key).ToList();
        foreach (var key in waKeys)
        {
            var d = PhoneMatch.Digits(key);
            if (d.Length >= 8 && d == key) Upsert(ChatBranch.Domain.Channels.WhatsApp, d, "thread");
        }

        var lookup = LookupAccounts(_db).Count > 0;
        var queued = 0;
        foreach (var wa in existing.Where(i => i.Channel == ChatBranch.Domain.Channels.WhatsApp))
            if (ApplyVerdictOrQueue(wa, lookup, recheck, now)) queued++;

        if (save) _db.SaveChanges();
        return new DiscoveryResult(existing, added, removed, queued, lookup);
    }

    /// <summary>
    /// Decide a WhatsApp identity's verdict without a lookup when possible: a
    /// fresh verdict stays; another contact's fresh verdict for the same number
    /// is reused (the 30-day cache is per NUMBER); else queue a lookup when a QR
    /// number is connected, or mark it unknown. True = queued.
    /// </summary>
    private bool ApplyVerdictOrQueue(ContactIdentity wa, bool lookup, bool recheck, DateTime now)
    {
        if (Fresh(wa, now)) return false;
        if (wa.Verification == IdentityVerdicts.Pending)
        {
            if (lookup) return false;   // already queued
        }
        var cached = CachedVerdict(wa.Address, now, wa.Id);
        if (cached != null)
        {
            Stamp(wa, cached.Verification, cached.VerificationDetail, cached.CheckedAt, cached.CheckedAccountId, now);
            return false;
        }
        if (!lookup)
        {
            if (wa.Verification != IdentityVerdicts.Unknown || wa.VerificationDetail != NoQrDetail)
                Stamp(wa, IdentityVerdicts.Unknown, NoQrDetail, null, null, now);
            return false;
        }
        // a failed lookup is retried after a day (or right away on "Find channels")
        if (wa.Verification == IdentityVerdicts.Unknown && wa.CheckedAt is DateTime at && !recheck
            && now - at < TimeSpan.FromHours(UnknownRetryHours))
            return false;
        Stamp(wa, IdentityVerdicts.Pending, "", wa.CheckedAt, wa.CheckedAccountId, now);
        return true;
    }

    /// <summary>A verified / not-on-WhatsApp verdict younger than 30 days.</summary>
    public static bool Fresh(ContactIdentity i, DateTime now) =>
        i.Verification is IdentityVerdicts.Verified or IdentityVerdicts.NotOnWhatsApp
        && i.CheckedAt is DateTime at && now - at < TimeSpan.FromDays(CacheDays);

    /// <summary>Any contact's fresh verdict for this number (newest first), or null.</summary>
    public ContactIdentity? CachedVerdict(string address, DateTime now, int excludeId = 0)
    {
        var since = now.AddDays(-CacheDays);
        return _db.ContactIdentities.AsNoTracking()
            .Where(i => i.Channel == ChatBranch.Domain.Channels.WhatsApp && i.Address == address && i.Id != excludeId
                        && (i.Verification == IdentityVerdicts.Verified
                            || i.Verification == IdentityVerdicts.NotOnWhatsApp)
                        && i.CheckedAt != null && i.CheckedAt >= since)
            .OrderByDescending(i => i.CheckedAt).FirstOrDefault();
    }

    private static void Stamp(ContactIdentity i, string verdict, string detail, DateTime? at, int? accountId,
        DateTime now)
    {
        i.Verification = verdict;
        i.VerificationDetail = detail;
        i.CheckedAt = at;
        i.CheckedAccountId = accountId;
        i.UpdatedAt = now;
    }

    /// <summary>The contact's registry rows (hub + API payloads).</summary>
    public List<ContactIdentity> For(int contactId) =>
        _db.ContactIdentities.AsNoTracking().Where(i => i.ContactId == contactId)
            .OrderBy(i => i.Channel).ThenBy(i => i.Id).ToList();

    /// <summary>Wire shape of one registry row.</summary>
    public static Dictionary<string, object?> Payload(ContactIdentity i) => new()
    {
        ["id"] = i.Id,
        ["channel"] = i.Channel,
        ["address"] = i.Address,
        ["source"] = i.Source,
        ["verification"] = i.Channel == ChatBranch.Domain.Channels.WhatsApp
            ? (i.Verification.Length > 0 ? i.Verification : IdentityVerdicts.Unknown) : null,
        ["verification_detail"] = i.VerificationDetail,
        ["checked_at"] = i.CheckedAt is DateTime at ? DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("o") : null,
    };
}

/// <summary>
/// The lookup rate limit, per gateway: at most one check every
/// <see cref="Gap"/> (2 s by default). A singleton — every scope and the
/// background worker share it.
/// </summary>
public sealed class NumberCheckLimiter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _last = new();
    public TimeSpan Gap { get; }

    public NumberCheckLimiter(TimeSpan? gap = null) => Gap = gap ?? TimeSpan.FromSeconds(2);

    /// <summary>Take the slot for <paramref name="gateway"/> if the gap has passed.</summary>
    public bool TryAcquire(string gateway, DateTime now)
    {
        lock (_lock)
        {
            if (_last.TryGetValue(gateway, out var last) && now - last < Gap) return false;
            _last[gateway] = now;
            return true;
        }
    }
}

/// <summary>What one checker pass did.</summary>
public record CheckPass(int Checked, int Cached, int Unknown, bool RateLimited, int Remaining);

/// <summary>
/// Works the WhatsApp lookup queue (identities in <c>pending</c>): one lookup
/// per pass through a connected QR number, rate-limited per gateway, the
/// verdict applied to every pending row for the same number. No QR number
/// connected ⇒ every pending row becomes <c>unknown</c> (Cloud has no lookup).
/// Never messages anyone. Driven by the background worker and by
/// <c>POST /api/contacts/identity-checks/run</c>.
/// </summary>
public class WhatsAppNumberChecker
{
    private readonly AppDbContext _db;
    private readonly IBaileysGateway _gateway;
    private readonly NumberCheckLimiter _limiter;
    private readonly TimeProvider _clock;
    private static int _rotation;

    public WhatsAppNumberChecker(AppDbContext db, IBaileysGateway gateway, NumberCheckLimiter limiter,
        TimeProvider? clock = null)
    {
        _db = db;
        _gateway = gateway;
        _limiter = limiter;
        _clock = clock ?? TimeProvider.System;
    }

    private IQueryable<ContactIdentity> PendingQuery() =>
        _db.ContactIdentities.Where(i => i.Channel == ChatBranch.Domain.Channels.WhatsApp
                                         && i.Verification == IdentityVerdicts.Pending);

    public async Task<CheckPass> RunOnceAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var next = await PendingQuery().OrderBy(i => i.UpdatedAt).ThenBy(i => i.Id).FirstOrDefaultAsync(ct);
        if (next == null) return new CheckPass(0, 0, 0, false, 0);

        var accounts = ContactIdentityService.LookupAccounts(_db);
        if (accounts.Count == 0 || !_gateway.IsConfigured)
        {
            // nothing can answer: every queued number is unverified, not stuck
            var all = await PendingQuery().ToListAsync(ct);
            foreach (var i in all)
            {
                i.Verification = IdentityVerdicts.Unknown;
                i.VerificationDetail = ContactIdentityService.NoQrDetail;
                i.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(ct);
            return new CheckPass(0, 0, all.Count, false, 0);
        }

        // the 30-day cache answers without a lookup
        var registry = new ContactIdentityService(_db, new ScopingService(_db), _clock);
        var cached = registry.CachedVerdict(next.Address, now, next.Id);
        if (cached != null)
        {
            var n = await ApplyAsync(next.Address, cached.Verification, "", cached.CheckedAt ?? now,
                cached.CheckedAccountId, now, ct);
            return new CheckPass(0, n, 0, false, await PendingQuery().CountAsync(ct));
        }

        if (!_limiter.TryAcquire(_gateway.BaseUrl, now))
            return new CheckPass(0, 0, 0, true, await PendingQuery().CountAsync(ct));

        var account = accounts[(int)((uint)Interlocked.Increment(ref _rotation) % (uint)accounts.Count)];
        BaileysCheck r;
        try { r = await _gateway.CheckNumberAsync(account.ExternalId, next.Address, ct); }
        catch (Exception e) { r = new BaileysCheck(null, null, e.GetType().Name); }

        var verdict = r.Exists switch
        {
            true => IdentityVerdicts.Verified,
            false => IdentityVerdicts.NotOnWhatsApp,
            null => IdentityVerdicts.Unknown,
        };
        var detail = verdict == IdentityVerdicts.Unknown
            ? Trim($"Lookup via {account.Name} was inconclusive: {r.Error}", 300)
            : $"Checked via {account.Name}";
        await ApplyAsync(next.Address, verdict, detail, now, account.Id, now, ct);
        return new CheckPass(1, 0, verdict == IdentityVerdicts.Unknown ? 1 : 0, false,
            await PendingQuery().CountAsync(ct));
    }

    /// <summary>Stamp the verdict on every pending row for this number.</summary>
    private async Task<int> ApplyAsync(string address, string verdict, string detail, DateTime checkedAt,
        int? accountId, DateTime now, CancellationToken ct)
    {
        var rows = await PendingQuery().Where(i => i.Address == address).ToListAsync(ct);
        foreach (var i in rows)
        {
            i.Verification = verdict;
            i.VerificationDetail = detail.Length > 0 ? detail : "From an earlier lookup of this number";
            i.CheckedAt = checkedAt;
            i.CheckedAccountId = accountId;
            i.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
