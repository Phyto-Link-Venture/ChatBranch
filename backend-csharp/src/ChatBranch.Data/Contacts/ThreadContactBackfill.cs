using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Contacts;

/// <summary>
/// "Link my existing conversations to my contacts" — the backfill for
/// conversations that already existed when their contact was created.
///
/// A conversation is normally linked the moment a message arrives
/// (<see cref="InboundProcessor.MatchContact"/>): email by address, WhatsApp and
/// SMS by the last-8-digit phone rule, nothing else. A contact IMPORT
/// (<see cref="ContactImporter"/>) only writes contacts and their entity links —
/// it never touches <see cref="Thread.ContactId"/> — so a WhatsApp history
/// import that opened the conversations BEFORE the contacts existed leaves them
/// with no contact and no name, and nothing revisits them until the customer
/// writes again. This runs that same resolution over the conversations already
/// on file, on demand.
///
/// It reuses the inbound path's matchers verbatim
/// (<see cref="ContactCompanies.MatchPhone"/> / <see cref="ContactCompanies.MatchEmail"/>
/// with the RECEIVING entity, then <see cref="ContactCompanies.MayLink"/>), because
/// this installation holds several unrelated client companies as top-level
/// entities: a bare phone-tail match would attach one company's contact to
/// another company's conversation, which is exactly the leak of
/// incident 2026-10-11. There is no second matching rule here.
///
/// The rules it keeps:
/// <list type="bullet">
/// <item>Only a conversation whose <c>contact_id</c> is NULL is touched — an
///   existing link is never overwritten or moved.</item>
/// <item>Groups never take a contact (<see cref="GroupChats.IsGroup"/>), and
///   neither do WhatsApp status / broadcast jids (their digits would tail-match a
///   stranger) — see <see cref="Eligible"/>.</item>
/// <item>Only phone-keyed (<see cref="MoreChannels.PhoneKeyed"/>, WhatsApp) and
///   email conversations are candidates; a Messenger PSID, an Instagram IGSID, a
///   Telegram chat id or a widget session id is opaque and is linked by hand.</item>
/// <item>Idempotent: a second run links nothing — it only fills nulls, and the
///   matching is a pure function of the contacts on file.</item>
/// <item>Every linked conversation gets a fresh <see cref="Thread.UpdatedAt"/>
///   (the SSE change token the inbox polls) and is re-attributed through
///   <see cref="ScopingService.SyncThreadEntity"/>, exactly as the inbound path
///   does. Nothing here uses a bulk <c>ExecuteUpdate</c>.</item>
/// </list>
/// </summary>
public sealed class ThreadContactBackfill
{
    /// <summary>Conversations examined in one run unless the caller asks for more.</summary>
    public const int DefaultLimit = 2_000;
    public const int MaxLimit = 50_000;
    /// <summary>Example links carried back for the operator to eyeball.</summary>
    public const int SampleSize = 25;
    private const int SaveEvery = 200;

    private readonly AppDbContext _db;
    private readonly ScopingService _sc;

    public ThreadContactBackfill(AppDbContext db, ScopingService sc)
    {
        _db = db;
        _sc = sc;
    }

    /// <summary>One conversation the run linked (or, in a preview, would link).</summary>
    public sealed record Link(int ThreadId, string Channel, string Key, int ContactId, string ContactName);

    /// <summary>
    /// What a run did. <see cref="Refused"/> is the cross-company count that
    /// matters: a contact DOES hold this phone / address, but it belongs to
    /// another company, so the conversation is left alone.
    /// </summary>
    public sealed class Report
    {
        public bool Preview { get; init; }
        /// <summary>Unlinked conversations examined.</summary>
        public int Scanned { get; set; }
        public int Linked { get; set; }
        /// <summary>Conversations in scope that already had a contact (untouched).</summary>
        public int AlreadyLinked { get; set; }
        /// <summary>No contact holds that phone / address at all.</summary>
        public int NoMatch { get; set; }
        /// <summary>A contact holds it, but not one this conversation's company may link.</summary>
        public int Refused { get; set; }
        /// <summary>Groups, broadcasts, and channels whose key identifies nobody.</summary>
        public int Skipped { get; set; }
        /// <summary>More unlinked conversations remain beyond this run's limit.</summary>
        public bool More { get; set; }
        public List<Link> Sample { get; } = new();
    }

    /// <summary>
    /// Run the backfill. <paramref name="preview"/> writes nothing and reports the
    /// same counts a commit would produce. <paramref name="entityId"/> narrows to
    /// that entity's subtree (an unattributed conversation is only reached by an
    /// unscoped run), <paramref name="channel"/> to one channel.
    /// <paramref name="allowedEntityIds"/> is the caller's HARD bound — the
    /// entities they may act on at all (null = any, superadmin); a bounded caller
    /// therefore never reaches an unattributed conversation.
    /// </summary>
    public Report Run(bool preview, int? entityId = null, string? channel = null, int? limit = null,
        IReadOnlyCollection<int>? allowedEntityIds = null)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var report = new Report { Preview = preview };
        var want = (channel ?? "").Trim().ToLowerInvariant();

        IQueryable<Thread> scoped = _db.Threads.AsNoTracking();
        if (want.Length > 0) scoped = scoped.Where(t => t.Channel == want);
        scoped = _sc.ScopeOwned(scoped, entityId);
        if (allowedEntityIds != null)
        {
            var bound = allowedEntityIds.Distinct().ToList();
            scoped = scoped.Where(t => t.EntityId != null && bound.Contains(t.EntityId.Value));
        }
        report.AlreadyLinked = scoped.Count(t => t.ContactId != null);

        // + 1 so the answer can say whether a second run has anything left to do
        var pending = scoped.Where(t => t.ContactId == null).OrderBy(t => t.Id).Take(take + 1)
            .Select(t => new { t.Id, t.Channel, t.Key, t.AccountId, t.EntityId, t.CompanyEntityId })
            .ToList();
        report.More = pending.Count > take;
        if (pending.Count == 0) return report;

        // A PRE-FILTER only: "does any contact share this identity at all?", built
        // once with the very rules the matchers use (PhoneMatch.Tail, lower-cased
        // email), so "absent here" is exactly "the matcher would find nothing".
        // Without it every unmatched conversation would re-scan the contact table.
        // The DECISION always stays with ContactCompanies.
        var tails = new HashSet<string>();
        var mails = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in _db.Contacts.AsNoTracking().Select(c => new { c.Phone, c.Email }).ToList())
        {
            if (PhoneMatch.Tail(c.Phone) is string tail) tails.Add(tail);
            var mail = (c.Email ?? "").Trim().ToLowerInvariant();
            if (mail.Length > 0) mails.Add(mail);
        }

        // Memo over the matchers' own inputs: the same identity received by the
        // same entity always resolves to the same contact, and the only tables the
        // matchers read (crm_contact, crm_contactentity, the entity tree) do not
        // change while the run reads. Threads do — and no matcher reads them.
        var resolved = new Dictionary<(string Identity, int Entity), int?>();

        var planned = new List<Link>();
        foreach (var row in pending.Take(take))
        {
            report.Scanned++;
            if (!Eligible(row.Channel, row.Key)) { report.Skipped++; continue; }
            var isEmail = row.Channel == ChatBranch.Domain.Channels.Email;
            var identity = isEmail ? row.Key.Trim().ToLowerInvariant() : PhoneMatch.Tail(row.Key)!;
            if (isEmail ? !mails.Contains(identity) : !tails.Contains(identity)) { report.NoMatch++; continue; }

            // the entity the inbound path would have used: the receiving account's, else the thread's own
            var receiving = ReceivingEntity(row.AccountId, row.EntityId);
            var key = (identity, receiving ?? 0);
            if (!resolved.TryGetValue(key, out var contactId))
            {
                var match = isEmail
                    ? ContactCompanies.MatchEmail(_db, _sc, row.Key, receiving)
                    : ContactCompanies.MatchPhone(_db, _sc, row.Key, receiving);
                resolved[key] = contactId = match?.Id;
            }
            // Pick() already refuses another company's contact; MayLink is the same
            // guard the inbound path applies against the thread's own company.
            var company = row.CompanyEntityId ?? _sc.CompanyOf(row.EntityId);
            if (contactId is not int cid || !ContactCompanies.MayLink(_db, _sc, cid, company))
            {
                report.Refused++;
                continue;
            }
            planned.Add(new Link(row.Id, row.Channel, row.Key, cid, ""));
        }

        if (preview)
        {
            report.Linked = planned.Count;
            Describe(report, planned);
            return report;
        }

        foreach (var chunk in planned.Chunk(SaveEvery))
        {
            var ids = chunk.Select(x => x.ThreadId).ToList();
            var threads = _db.Threads.Where(t => ids.Contains(t.Id)).ToList();
            var written = new List<Thread>();
            foreach (var t in threads)
            {
                var pick = chunk.First(x => x.ThreadId == t.Id);
                // re-checked at write time: another path may have linked the
                // conversation (or moved the contact) since the scan
                if (t.ContactId != null) { report.AlreadyLinked++; continue; }
                if (!ContactCompanies.MayLink(_db, _sc, pick.ContactId, _sc.ThreadCompany(t)))
                {
                    report.Refused++;
                    continue;
                }
                t.ContactId = pick.ContactId;
                t.UpdatedAt = DateTime.UtcNow; // the SSE change token the inbox polls
                written.Add(t);
                report.Linked++;
            }
            if (written.Count == 0) continue;
            _db.SaveChanges();
            // the denormalized entity follows the new contact link, as inbound does
            foreach (var t in written) _sc.SyncThreadEntity(t, save: false);
            _db.SaveChanges();
            Describe(report, written
                .Select(t => chunk.First(x => x.ThreadId == t.Id))
                .ToList());
        }
        return report;
    }

    /// <summary>Name the first <see cref="SampleSize"/> links for the operator.</summary>
    private void Describe(Report report, IReadOnlyCollection<Link> links)
    {
        var room = SampleSize - report.Sample.Count;
        if (room <= 0 || links.Count == 0) return;
        var want = links.Take(room).ToList();
        var ids = want.Select(l => l.ContactId).Distinct().ToList();
        var names = _db.Contacts.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionary(c => c.Id, c => c.Name);
        foreach (var l in want)
            report.Sample.Add(l with { ContactName = names.TryGetValue(l.ContactId, out var n) ? n : "" });
    }

    /// <summary>
    /// The entity the inbound path resolves a contact against — the receiving
    /// account's, else the conversation's own (InboundProcessor.ReceivingEntity).
    /// </summary>
    private int? ReceivingEntity(int? accountId, int? threadEntityId) =>
        accountId is int a
            ? _db.ChannelAccounts.AsNoTracking().Where(x => x.Id == a).Select(x => x.EntityId).FirstOrDefault()
              ?? threadEntityId
            : threadEntityId;

    /// <summary>
    /// May this (channel, key) be matched at all — the inbound path's rule
    /// (InboundProcessor.MatchContact): email by address, WhatsApp / SMS by phone
    /// tail, nothing else. A group is out (<see cref="GroupChats.IsGroup"/>), and
    /// so is any phone-keyed key that is not plain digits: WhatsApp's
    /// <c>status@broadcast</c> and <c>…@broadcast</c> jids carry digits that would
    /// tail-match a stranger.
    /// </summary>
    public static bool Eligible(string? channel, string? key)
    {
        var k = key ?? "";
        if (k.Length == 0) return false;
        if (GroupChats.IsGroup(channel, k)) return false;
        if (channel == ChatBranch.Domain.Channels.Email) return k.Contains('@');
        if (channel != ChatBranch.Domain.Channels.WhatsApp && !MoreChannels.PhoneKeyed(channel)) return false;
        return k.All(char.IsAsciiDigit) && PhoneMatch.Tail(k) != null;
    }
}
