using System.Text.Json;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Channels;

/// <summary>
/// The account ROLE of a WhatsApp number (<c>wa_config.account_role</c>):
/// <c>Config["role"]</c>, defaulting by provider — a Cloud business number is
/// the compliant template cannon ("reminder"), a Baileys personal number is the
/// AI conversation layer ("conversation").
/// </summary>
public static class AccountRoles
{
    public const string Reminder = "reminder";
    public const string Conversation = "conversation";
    public static readonly string[] All = { Reminder, Conversation };

    public static string Of(ChannelAccount a)
    {
        if (a.Config != null && a.Config.TryGetValue("role", out var raw) && raw != null)
        {
            var s = raw is JsonElement el
                ? (el.ValueKind == JsonValueKind.String ? el.GetString() : null)
                : raw.ToString();
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s is Reminder or Conversation) return s;
        }
        return a.Provider == Providers.Baileys ? Conversation : Reminder;
    }
}

/// <summary>
/// What a send needs from the carrying account.
/// <list type="bullet">
///   <item><see cref="Role"/> — WhatsApp only, and only when the organization
///         actually uses that role (some configured number has it): a
///         conversational send never fails over onto the business number, but
///         a Cloud-only setup still routes.</item>
///   <item><see cref="Provider"/> — hard: a template needs an OFFICIAL number
///         (<see cref="WhatsAppProviders.AnyOfficial"/>: Cloud API, YCloud or Twilio)
///         and NEVER fails over to Baileys.</item>
///   <item><see cref="PreferRole"/> — soft: among eligible accounts, prefer this role.</item>
/// </list>
/// </summary>
public sealed record RouteNeed(string? Role = null, string? Provider = null, string? PreferRole = null)
{
    /// <summary>Free text, AI replies, queued text jobs.</summary>
    public static readonly RouteNeed Conversation = new(Role: AccountRoles.Conversation);
    /// <summary>A WhatsApp template: an official number (Cloud API / YCloud / Twilio), the business (reminder) one first.</summary>
    public static readonly RouteNeed Template = new(Provider: WhatsAppProviders.AnyOfficial, PreferRole: AccountRoles.Reminder);
    public static readonly RouteNeed Any = new();

    public bool IsTemplate => Provider != null;

    /// <summary>
    /// What a send on <paramref name="channel"/> needs: roles and providers are
    /// WhatsApp concepts (a template needs an official number, free text / AI a
    /// conversation number); every other channel takes any eligible account.
    /// </summary>
    public static RouteNeed For(string channel, bool template = false) =>
        channel != ChatBranch.Domain.Channels.WhatsApp ? Any
        : template ? Template
        : Conversation;
}

/// <summary>
/// The account a send will go out on. <see cref="Via"/> says why:
/// <c>sandbox</c> (no configured account to route between — the send stores
/// locally or uses the thread's own account), <c>current</c> (the thread's own
/// healthy number), <c>pinned</c>, <c>sticky</c> (the contact's assignment),
/// <c>override</c> (the assignment can't carry this send — e.g. a template on a
/// Baileys number — so another account carries this ONE send), <c>auto</c>
/// (least-loaded auto-assign), <c>failover</c> (the assigned number is
/// banned/disconnected), <c>sole</c> (the only usable account).
/// <see cref="Error"/> set = nothing may carry it; the caller refuses (409) and
/// leaves the thread untouched.
/// </summary>
public sealed record RouteResult(ChannelAccount? Account, string Via, ChannelAccount? DownAccount = null,
    string? Error = null)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Contact → account routing AT SEND TIME (port of <c>whatsapp/routing.py</c>),
/// for every channel that has <see cref="ChannelAccount"/> rows:
/// <list type="number">
///   <item>the thread's own number while it is healthy (and, for a template, a Cloud number);</item>
///   <item>the contact's sticky <see cref="AccountAssignment"/>;</item>
///   <item>no assignment yet → the least-loaded eligible account (fewest
///         assignments on the channel, ties by id) becomes the contact's
///         unpinned assignment — an even spread;</item>
///   <item>the assigned number is banned/disconnected → FAILOVER: the
///         assignment moves (and loses its pin), the thread moves at send time,
///         a system note lands in the chat and admins are notified.</item>
/// </list>
/// Eligible = configured and not banned/disconnected, among the accounts the
/// conversation's entity may use (its own — nearest level of the entity tree —
/// else the organization-wide ones; an outlet with its own number never falls
/// back to the shared one). With no configured account at all nothing is
/// routed: sandbox stays the zero-credential default.
///
/// The rules are channel-blind except where a channel's identity says
/// otherwise: account ROLES, providers and templates are WhatsApp-only
/// (<see cref="RouteNeed.For"/>); on an <see cref="AccountBound"/> channel
/// (Telegram, Messenger, Instagram, the widget) the conversation exists only on
/// the account that received it, so it never fails over or spreads; on an
/// <see cref="AddressPortable"/> channel (email) any mailbox can write to the
/// address, so even an unattributed conversation takes the least-loaded one.
/// </summary>
public class Routing
{
    public static readonly IReadOnlyList<string> DownStatuses = new[] { Health.Disconnected, Health.Banned };

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;

    public Routing(AppDbContext db, ScopingService scoping)
    {
        _db = db;
        _scoping = scoping;
    }

    // ---- eligibility ----------------------------------------------------------

    /// <summary>
    /// routing._configured: a WhatsApp Cloud number needs a token + phone number
    /// id (a Baileys number pairs through the gateway instead), email needs SMTP
    /// host + login, every other channel an access token.
    /// </summary>
    public static bool IsConfigured(ChannelAccount a)
    {
        if (a.Channel == ChatBranch.Domain.Channels.WhatsApp)
            return WhatsAppProviders.Configured(a);
        if (a.Channel == ChatBranch.Domain.Channels.Email)
            return EmailConfig.AccountConfigured(a);
        if (MoreChannels.Handles(a.Channel)) return MoreChannels.Configured(a);
        return a.AccessToken != "";
    }

    public static bool IsDown(ChannelAccount a) => DownStatuses.Contains(a.HealthStatus);

    public static bool IsEligible(ChannelAccount a) => IsConfigured(a) && !IsDown(a);

    /// <summary>
    /// A channel whose conversation identity is minted BY the receiving account —
    /// a Telegram chat only works with the bot the customer started, a
    /// Messenger/Instagram PSID is page-scoped, a widget session belongs to its
    /// widget. No other account can carry such a conversation, so a thread with a
    /// configured account keeps it, even while it is down (the send fails
    /// honestly instead of going out on an account that cannot reach them).
    /// </summary>
    public static bool AccountBound(string channel) =>
        channel is ChatBranch.Domain.Channels.Telegram or ChatBranch.Domain.Channels.Facebook
            or ChatBranch.Domain.Channels.Instagram or ChatBranch.Domain.Channels.Website
        || MoreChannels.AccountBound(channel); // LINE, Viber, WeChat, TikTok, Douyin, Zalo

    /// <summary>
    /// A channel whose address works from ANY of our accounts (every mailbox can
    /// write to an email address), so a conversation with no contact and no
    /// account yet still takes the least-loaded eligible one rather than staying
    /// unrouted. WhatsApp could, but deliberately never guesses a number for an
    /// unknown sender (cold-text exposure).
    /// </summary>
    public static bool AddressPortable(string channel) =>
        channel == ChatBranch.Domain.Channels.Email || channel == ChatBranch.Domain.Channels.Sms;

    public static string ProviderOf(ChannelAccount a) =>
        string.IsNullOrEmpty(a.Provider) ? Providers.Cloud : a.Provider;

    private static bool Satisfies(ChannelAccount a, RouteNeed need) => WhatsAppProviders.Matches(a, need.Provider);

    /// <summary>
    /// The accounts of <paramref name="channel"/> a conversation of this entity
    /// may go out on: the entity's own (walking up the tree to the nearest level
    /// that has a configured one), else the organization-wide ones. An
    /// unattributed conversation (entity null) uses the ORGANIZATION-WIDE ones
    /// only — never an outlet's number (auto-assigning an unassigned contact to a
    /// fenced outlet's number would file the conversation under that outlet).
    /// </summary>
    public List<ChannelAccount> Pool(string channel, int? entityId)
    {
        var all = _db.ChannelAccounts.AsNoTracking().Where(a => a.Channel == channel)
            .OrderBy(a => a.Id).ToList();
        if (entityId is not int start) return all.Where(a => a.EntityId == null).ToList();
        var seen = new HashSet<int>();
        int? cursor = start;
        for (var depth = 0; cursor is int id && depth < 16 && seen.Add(id); depth++)
        {
            var own = all.Where(a => a.EntityId == id).ToList();
            if (own.Any(IsConfigured)) return own;
            cursor = _db.Entities.AsNoTracking().Where(e => e.Id == id).Select(e => e.ParentId).FirstOrDefault();
        }
        return all.Where(a => a.EntityId == null).ToList();
    }

    /// <summary>routing.eligible_accounts — configured, not down, matching the need.</summary>
    public List<ChannelAccount> Eligible(string channel, RouteNeed need, int? entityId, int? excludeId = null)
    {
        var configured = Pool(channel, entityId).Where(IsConfigured).ToList();
        if (need.Provider is string provider)
            configured = configured.Where(a => WhatsAppProviders.Matches(a, provider)).ToList();
        var whatsapp = channel == ChatBranch.Domain.Channels.WhatsApp;
        if (whatsapp && need.Role is string role && configured.Any(a => AccountRoles.Of(a) == role))
            configured = configured.Where(a => AccountRoles.Of(a) == role).ToList();
        var eligible = configured.Where(a => !IsDown(a) && a.Id != excludeId).ToList();
        if (whatsapp && need.PreferRole is string pref && eligible.Any(a => AccountRoles.Of(a) == pref))
            eligible = eligible.Where(a => AccountRoles.Of(a) == pref).ToList();
        return eligible;
    }

    /// <summary>Fewest contact assignments on the channel first (even distribution), ties by id.</summary>
    public ChannelAccount? LeastLoaded(IReadOnlyCollection<ChannelAccount> candidates, string channel)
    {
        if (candidates.Count == 0) return null;
        var ids = candidates.Select(a => a.Id).ToList();
        var load = _db.AccountAssignments.AsNoTracking()
            .Where(x => x.Channel == channel && ids.Contains(x.AccountId))
            .GroupBy(x => x.AccountId)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionary(x => x.Key, x => x.N);
        return candidates.OrderBy(a => load.GetValueOrDefault(a.Id)).ThenBy(a => a.Id).First();
    }

    // ---- contact → account ----------------------------------------------------

    /// <summary>
    /// routing.account_for_contact: sticky assignment → least-loaded auto-assign
    /// → failover. <paramref name="apply"/>=false is a dry run (no writes).
    /// Templates never write assignments: a Cloud number that carries ONE
    /// template is a per-send choice, not the contact's conversation home.
    /// </summary>
    public RouteResult AccountForContact(int contactId, string channel, RouteNeed need, int? entityId,
        bool apply)
    {
        var sticky = _db.AccountAssignments.Include(x => x.Account)
            .FirstOrDefault(x => x.ContactId == contactId && x.Channel == channel);
        var assigned = sticky?.Account;

        // a sticky number of ANOTHER company never carries this company's conversation (the
        // contact may talk to several): this send goes out on one of the conversation's own,
        // and the other company's assignment is left alone
        var company = _scoping.CompanyOf(entityId);
        if (assigned != null && !ScopingService.SameCompanyOrShared(company, _scoping.AccountCompany(assigned)))
        {
            var own = LeastLoaded(Eligible(channel, need, entityId), channel);
            return own != null ? new RouteResult(own, "override") : NoneEligible(channel, need, null);
        }

        if (assigned != null && IsEligible(assigned))
        {
            if (Satisfies(assigned, need)) return new RouteResult(assigned, "sticky");
            // healthy, but it can't carry this send (a template on a Baileys
            // number): another account carries this one send; the assignment stays
            var alt = LeastLoaded(Eligible(channel, need, entityId), channel);
            return alt != null ? new RouteResult(alt, "override") : NoneEligible(channel, need, null);
        }

        if (assigned != null)
        {
            var pick = LeastLoaded(Eligible(channel, need, entityId, excludeId: assigned.Id), channel);
            if (pick == null) return NoneEligible(channel, need, assigned);
            if (apply && !need.IsTemplate)
            {
                // failover clears the pin: the operator pinned a number that is gone
                sticky!.AccountId = pick.Id;
                sticky.Account = _db.ChannelAccounts.Find(pick.Id);
                sticky.Pinned = false;
                sticky.UpdatedAt = DateTime.UtcNow;
                _db.SaveChanges();
            }
            return new RouteResult(pick, "failover", DownAccount: assigned);
        }

        var auto = LeastLoaded(Eligible(channel, need, entityId), channel);
        if (auto == null) return NoneEligible(channel, need, null);
        if (apply && !need.IsTemplate && sticky == null)
        {
            _db.AccountAssignments.Add(new AccountAssignment
            {
                ContactId = contactId, Channel = channel, AccountId = auto.Id, Pinned = false,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
        return new RouteResult(auto, "auto");
    }

    // ---- thread → account (the send-time entry point) ---------------------------

    /// <summary>
    /// The account a send on this thread goes out on. <paramref name="preferredAccountId"/>
    /// is the account the job/caller had in mind (it is honoured while healthy).
    /// With <paramref name="apply"/>, a different result MOVES the thread there
    /// (entity re-synced), and moving off a banned/disconnected number appends a
    /// system note, audits <c>account.failover</c> and notifies admins.
    /// </summary>
    public RouteResult ForThread(Thread thread, RouteNeed need, int? preferredAccountId = null, bool apply = false)
    {
        // roles, providers and templates only mean something on WhatsApp
        if (thread.Channel != ChatBranch.Domain.Channels.WhatsApp) need = RouteNeed.Any;
        var currentId = preferredAccountId ?? thread.AccountId;
        var current = currentId is int cid
            ? _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == cid) : null;
        // a number of ANOTHER company never carries this conversation (a job's pin, a stale
        // preference): fall back to the conversation's own number
        var company = _scoping.ThreadCompany(thread);
        bool Fits(ChannelAccount a) => ScopingService.SameCompanyOrShared(company, _scoping.AccountCompany(a));
        if (current != null && !Fits(current))
        {
            current = thread.AccountId is int own && own != current.Id
                ? _db.ChannelAccounts.AsNoTracking().FirstOrDefault(a => a.Id == own) : null;
            if (current != null && !Fits(current)) current = null;
        }
        if (current != null && IsEligible(current) && Satisfies(current, need))
            return new RouteResult(current, "current");
        // the conversation only exists on the account that received it
        if (current != null && AccountBound(thread.Channel) && current.Channel == thread.Channel
            && IsConfigured(current))
            return new RouteResult(current, "bound", DownAccount: IsDown(current) ? current : null);

        var configured = Pool(thread.Channel, thread.EntityId).Where(IsConfigured).ToList();
        // nothing to route between: the zero-credential sandbox
        if (configured.Count == 0) return new RouteResult(current, "sandbox");
        // a template in an organization with no official number at all: it goes out
        // on whatever number the conversation would use (Baileys renders it as text)
        if (need.Provider is string p && !configured.Any(a => WhatsAppProviders.Matches(a, p)))
            return ForThread(thread, need with { Provider = null, PreferRole = null }, preferredAccountId, apply);

        RouteResult r;
        if (thread.ContactId is int contactId)
        {
            r = AccountForContact(contactId, thread.Channel, need, thread.EntityId, apply);
            // the assignment named the very number the thread is stuck on
            if (r.Ok && current != null && r.Account?.Id == current.Id && !IsEligible(current))
                r = NoneEligible(thread.Channel, need, current);
        }
        else if (current == null)
        {
            // an unknown sender with no number yet: only the sole usable account,
            // never a guess between several — except where every account can
            // write to the address (email): the least-loaded one, no assignment
            var candidates = Eligible(thread.Channel, need, thread.EntityId);
            r = candidates.Count == 1 ? new RouteResult(candidates[0], "sole")
                : candidates.Count > 1 && AddressPortable(thread.Channel)
                    ? new RouteResult(LeastLoaded(candidates, thread.Channel), "auto")
                    : new RouteResult(null, "sandbox");
        }
        else
        {
            var pick = LeastLoaded(Eligible(thread.Channel, need, thread.EntityId, excludeId: current.Id),
                thread.Channel);
            var down = IsEligible(current) ? null : current;
            r = pick == null
                ? NoneEligible(thread.Channel, need, down)
                : new RouteResult(pick, down != null ? "failover" : "override", DownAccount: down);
        }

        if (!r.Ok || r.Account == null) return r;
        if (apply && r.Account.Id != thread.AccountId) Move(thread, r.Account, current);
        if (r.DownAccount == null && current != null && !IsEligible(current))
            r = r with { DownAccount = current };
        return r;
    }

    /// <summary>Move the thread onto <paramref name="target"/>; a move off a dead number is announced.</summary>
    private void Move(Thread thread, ChannelAccount target, ChannelAccount? previous)
    {
        var tracked = _db.ChannelAccounts.Find(target.Id);
        if (tracked == null) return;
        // never onto another company's number (Fits above already keeps it out)
        if (!ScopingService.SameCompanyOrShared(_scoping.ThreadCompany(thread), _scoping.AccountCompany(tracked))) return;
        thread.AccountId = tracked.Id;
        thread.Account = tracked;
        _scoping.SyncThreadEntity(thread, save: false);
        thread.UpdatedAt = DateTime.UtcNow;
        _db.SaveChanges();

        if (previous == null || IsEligible(previous) || previous.Id == tracked.Id) return;
        try
        {
            var state = IsDown(previous) ? previous.HealthStatus : "not configured";
            var who = string.IsNullOrWhiteSpace(thread.Name) ? thread.Key : thread.Name;
            var note = $"[{Label(previous)} is {state} — this conversation now sends from {Label(tracked)}.]";
            new ThreadStore(_db, _scoping).AppendMessage(thread, MessageDirection.Out, MessageSource.System,
                note, status: "sent", accountId: tracked.Id);
            Audit.AuditTrail.Add(_db, "account.failover", $"{thread.Channel}:{thread.Key}",
                $"{Label(previous)} ({state}) → {Label(tracked)}", actor: "routing",
                source: AuditSources.System, entityId: thread.EntityId);
            _db.SaveChanges();
            int? tenantId = thread.EntityId is int eid
                ? _db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => (int?)e.TenantId).FirstOrDefault()
                : null;
            NotificationService.Notify(_db, NotificationCategories.AccountHealth, "account_health",
                $"Conversation moved off {Label(previous)}",
                $"{Label(previous)} is {state} — {who} now sends from {Label(tracked)}.",
                ThreadCompanies.ChatHref(thread),
                NotificationAudience.Admins(tenantId),
                new NotifyOptions
                {
                    RefKey = $"failover:{previous.Id}",
                    CollapsedTitle = $"Conversations moved off {Label(previous)}",
                });
        }
        catch { /* the move happened; the announcement is best-effort */ }
    }

    private static string Label(ChannelAccount a) =>
        string.IsNullOrWhiteSpace(a.Name) ? $"{a.Channel} #{a.Id}" : a.Name;

    private static RouteResult NoneEligible(string channel, RouteNeed need, ChannelAccount? down)
    {
        var why = down != null
            ? $" — {Label(down)} is {(IsDown(down) ? down.HealthStatus : "not configured")}"
            : "";
        string detail;
        if (need.IsTemplate)
            detail = $"No healthy official WhatsApp business number (Cloud API, YCloud or Twilio) can carry a template{why}. "
                     + "Templates never fall back to a Baileys number — reconnect a business number under Channels.";
        else if (channel == ChatBranch.Domain.Channels.WhatsApp && need.Role == AccountRoles.Conversation)
            detail = $"No healthy WhatsApp conversation number is available{why}. "
                     + "Reconnect it or link another under Channels.";
        else
            detail = $"No healthy {Noun(channel)} is available{why}. Reconnect one under Channels.";
        return new RouteResult(null, "none", DownAccount: down, Error: detail);
    }

    private static string Noun(string channel) => channel switch
    {
        ChatBranch.Domain.Channels.Email => "email mailbox",
        ChatBranch.Domain.Channels.Telegram => "Telegram bot",
        ChatBranch.Domain.Channels.WhatsApp => "WhatsApp number",
        ChatBranch.Domain.Channels.Sms => "SMS number",
        _ when MoreChannels.Handles(channel) => $"{MoreChannels.Label(channel)} account",
        _ => $"{channel} account",
    };
}
