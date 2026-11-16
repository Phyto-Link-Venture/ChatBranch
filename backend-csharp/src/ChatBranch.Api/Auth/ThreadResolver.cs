using System.Security.Claims;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Auth;

/// <summary>
/// Which conversation a signed-in request means when it names one by channel +
/// key. Since a customer has ONE conversation PER COMPANY (incident 2026-10-11),
/// (channel, key) alone can name several: this is the one rule that turns it
/// into exactly one, for every per-conversation route
/// (<see cref="ThreadAccessFilter"/>), every body / query that names a
/// conversation, and the MCP tools.
///
/// <list type="number">
///   <item>Only conversations the caller may SEE count (the inbox rule,
///         <see cref="ShellScope.ThreadEntityIds"/> + the caller's channel allow-list,
///         <see cref="ShellScope.ThreadVisible"/>; unassigned = everyone's).</item>
///   <item><c>thread_id</c> picks one exactly (it must be one of them, else 404).</item>
///   <item><c>company</c> (a top-level entity id or slug, or <c>none</c> for the
///         unattributed conversation) narrows to that company's.</item>
///   <item>Someone bound to entities only ever means their own company's
///         conversation (an unattributed one only when their company has none).</item>
///   <item>One left → it. None → the caller has no conversation there (a send may
///         start one in their company). Several → <b>409 with the candidates</b>,
///         never a silent pick.</item>
/// </list>
/// </summary>
public static class ThreadResolver
{
    public const string ItemKey = "cb.thread_pick";

    public enum Outcome { Found, None, Ambiguous, NotFound }

    /// <summary>One conversation a caller could have meant (the 409's candidate list).</summary>
    public sealed record Candidate(int ThreadId, int? CompanyId, string CompanyName, int? EntityId,
        string EntityName, int? AccountId, string AccountName, string Name, DateTime UpdatedAt);

    public sealed record Pick(Outcome Outcome, int? ThreadId, IReadOnlyList<Candidate> Candidates)
    {
        public static readonly Pick NoneYet = new(Outcome.None, null, Array.Empty<Candidate>());
        public static readonly Pick Missing = new(Outcome.NotFound, null, Array.Empty<Candidate>());
    }

    /// <summary>The lookup variants of a route's key: as given, trimmed and — WhatsApp — digits only.</summary>
    public static List<string> KeyVariants(string channel, string? raw)
    {
        raw ??= "";
        var keys = new List<string> { raw, raw.Trim() };
        if (channel == ChatBranch.Domain.Channels.WhatsApp || channel == ChatBranch.Domain.Channels.Sms)
            keys.Add(new string(raw.Where(char.IsDigit).ToArray()));
        return keys.Where(x => x.Length > 0).Distinct().ToList();
    }

    /// <summary>The <c>thread_id</c> / <c>company</c> disambiguators of a request (query string).</summary>
    public static (int? ThreadId, string? Company) Hints(HttpContext http)
    {
        var q = http.Request.Query;
        int? tid = int.TryParse(q["thread_id"].ToString(), out var t) && t > 0 ? t : null;
        var company = q["company"].ToString().Trim();
        return (tid, company.Length > 0 ? company : null);
    }

    public static async Task<Pick> ResolveAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        string channel, IReadOnlyCollection<string> keys, int? threadId = null, string? company = null)
    {
        if (keys.Count == 0 && threadId == null) return Pick.NoneYet;
        // a thread_id must name a conversation of THIS channel + key when the request names one
        // (only a thread_id alone, with no key, finds the conversation by its id)
        var byIdOnly = keys.Count == 0;
        var rows = await db.Threads.AsNoTracking()
            .Where(t => t.Channel == channel && (byIdOnly ? t.Id == threadId : keys.Contains(t.Key)))
            .Select(t => new Row(t.Id, t.EntityId, t.CompanyEntityId, t.AccountId, t.Name, t.UpdatedAt))
            .ToListAsync();
        if (rows.Count == 0) return threadId != null ? Pick.Missing : Pick.NoneYet;

        var allowed = ShellScope.ThreadEntityIds(db, sc, me, null);
        var channels = ShellScope.ChannelAccountIds(db, me);
        var visible = rows.Where(r => ShellScope.ThreadVisible(r.EntityId, r.AccountId, allowed, channels)).ToList();
        int? CompanyOf(Row r) => r.CompanyEntityId ?? sc.CompanyOf(r.EntityId);

        if (threadId is int tid)
            return visible.FirstOrDefault(r => r.Id == tid) is { } hit
                ? new Pick(Outcome.Found, hit.Id, Array.Empty<Candidate>())
                : Pick.Missing;

        var roots = ShellScope.ScopeRoots(db, me);
        var mine = roots.Select(r => sc.CompanyOf(r)).OfType<int>().ToHashSet();
        if (company != null)
        {
            var want = await CompanyIdAsync(db, sc, company);
            if (want.Unknown) return Pick.Missing;
            visible = visible.Where(r => CompanyOf(r) == want.Id).ToList();
            rows = rows.Where(r => CompanyOf(r) == want.Id).ToList();
        }

        // a person bound to entities means THEIR company's conversation — an unattributed
        // one only while their company has none
        if (roots.Count > 0 && visible.Count > 1)
        {
            var own = visible.Where(r => CompanyOf(r) is int c && mine.Contains(c)).ToList();
            if (own.Count > 0) visible = own;
        }

        if (visible.Count == 0)
            // their own company already talks to this customer, in a branch they may not
            // open — or on a channel outside their allow-list: hidden (404) — never a
            // second conversation for the same company
            return (roots.Count > 0 && rows.Any(r => CompanyOf(r) is int c && mine.Contains(c)))
                   || (channels != null && rows.Any(r => r.EntityId is int e
                       && (allowed == null || allowed.Contains(e))))
                ? Pick.Missing
                : Pick.NoneYet;   // only other companies' conversations: none of the caller's yet
        return visible.Count == 1
            ? new Pick(Outcome.Found, visible[0].Id, Array.Empty<Candidate>())
            : new Pick(Outcome.Ambiguous, null, await CandidatesAsync(db, sc, visible));
    }

    /// <summary>
    /// Resolve for the current request (route values, query hints) once, and keep the
    /// answer on the request for the handler (<see cref="LoadAsync"/>).
    /// </summary>
    public static async Task<Pick> ForRequestAsync(HttpContext http, string? channel = null, string? key = null)
    {
        if (http.Items.TryGetValue(ItemKey, out var cached) && cached is Pick p) return p;
        var values = http.Request.RouteValues;
        key ??= values.TryGetValue("key", out var k) ? k?.ToString() : null;
        channel ??= (values.TryGetValue("channel", out var c) ? c?.ToString() : null)
                    ?? ChatBranch.Domain.Channels.WhatsApp;
        var (tid, company) = Hints(http);
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var sc = http.RequestServices.GetRequiredService<ScopingService>();
        // a route that names one of the conversation's MESSAGES ({pk}) names the conversation too
        if (tid == null && company == null && values.TryGetValue("pk", out var pkRaw)
            && int.TryParse(pkRaw?.ToString(), out var pk))
            tid = await db.Messages.AsNoTracking().Where(m => m.Id == pk).Select(m => (int?)m.ThreadId)
                .FirstOrDefaultAsync();
        var pick = await ResolveAsync(db, sc, http.User, channel, KeyVariants(channel, key), tid, company);
        http.Items[ItemKey] = pick;
        return pick;
    }

    /// <summary>
    /// The conversation the current per-conversation request names (tracked, contact +
    /// account loaded), or null when the caller has none there. Never "the first thread
    /// with that key".
    /// </summary>
    public static async Task<Thread?> LoadAsync(HttpContext http, string? channel = null, string? key = null)
    {
        var pick = await ForRequestAsync(http, channel, key);
        if (pick.ThreadId is not int id) return null;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        return await db.Threads.Include(t => t.Contact).Include(t => t.Account).FirstOrDefaultAsync(t => t.Id == id);
    }

    /// <summary>
    /// For a conversation named in a BODY or query (<c>{channel, key, thread_id?, company?}</c>):
    /// the one the caller means, loaded and tracked, or the refusal (404 none / unknown, 409
    /// several). Never "the first thread with that key".
    /// </summary>
    public static async Task<(IResult? Error, Thread? Thread)> FindAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, string? channel, string? key, int? threadId = null, string? company = null)
    {
        var ch = (channel ?? "").Trim();
        if (ch.Length == 0 && threadId is int only)
            ch = await db.Threads.AsNoTracking().Where(t => t.Id == only).Select(t => t.Channel).FirstOrDefaultAsync() ?? "";
        var pick = await ResolveAsync(db, sc, me, ch, KeyVariants(ch, key), threadId, company);
        if (Refusal(pick) is { } refused) return (refused, null);
        if (pick.ThreadId is not int id)
            return (Results.Json(new { detail = "Thread not found." }, statusCode: StatusCodes.Status404NotFound), null);
        return (null, await db.Threads.Include(t => t.Contact).Include(t => t.Account).FirstAsync(t => t.Id == id));
    }

    /// <summary>The refusal for a pick that names no single conversation (null = go on).</summary>
    public static IResult? Refusal(Pick pick) => pick.Outcome switch
    {
        Outcome.NotFound => Results.Json(new { detail = "Thread not found." }, statusCode: StatusCodes.Status404NotFound),
        Outcome.Ambiguous => Ambiguous(pick.Candidates),
        _ => null,
    };

    /// <summary>409: this customer has a conversation with several companies the caller sees.</summary>
    public static IResult Ambiguous(IReadOnlyList<Candidate> candidates) => Results.Json(new
    {
        detail = "This customer has a conversation with more than one company — say which one "
                 + "(thread_id, or company = the company's id or slug).",
        code = "ambiguous_thread",
        candidates = candidates.Select(CandidatePayload).ToList(),
    }, statusCode: StatusCodes.Status409Conflict);

    public static object CandidatePayload(Candidate c) => new
    {
        thread_id = c.ThreadId,
        name = c.Name,
        company = c.CompanyId is int id ? new { id, name = c.CompanyName } : null,
        entity = c.EntityId is int e ? new { id = e, name = c.EntityName } : null,
        account = c.AccountId is int a ? new { id = a, name = c.AccountName } : null,
        updated_at = DateTime.SpecifyKind(c.UpdatedAt, DateTimeKind.Utc).ToString("o"),
    };

    /// <summary>A <c>company</c> hint: a top-level entity id or slug, or none / unattributed / 0.</summary>
    public static async Task<(bool Unknown, int? Id)> CompanyIdAsync(AppDbContext db, ScopingService sc, string hint)
    {
        var h = hint.Trim();
        if (h is "none" or "unattributed" or "0" or "null") return (false, null);
        int? entity = int.TryParse(h, out var n)
            ? await db.Entities.AsNoTracking().Where(e => e.Id == n).Select(e => (int?)e.Id).FirstOrDefaultAsync()
            : await db.Entities.AsNoTracking().Where(e => e.Slug == h).Select(e => (int?)e.Id).FirstOrDefaultAsync();
        // a branch names its company
        return entity is int id ? (false, sc.CompanyOf(id)) : (true, null);
    }

    private sealed record Row(int Id, int? EntityId, int? CompanyEntityId, int? AccountId, string Name, DateTime UpdatedAt);

    private static async Task<List<Candidate>> CandidatesAsync(AppDbContext db, ScopingService sc, List<Row> rows)
    {
        var entityIds = rows.SelectMany(r => new[] { r.EntityId, r.CompanyEntityId ?? sc.CompanyOf(r.EntityId) })
            .OfType<int>().Distinct().ToList();
        var names = await db.Entities.AsNoTracking().Where(e => entityIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name);
        var accountIds = rows.Select(r => r.AccountId).OfType<int>().Distinct().ToList();
        var accounts = await db.ChannelAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name);
        return rows.OrderByDescending(r => r.UpdatedAt).Select(r =>
        {
            var company = r.CompanyEntityId ?? sc.CompanyOf(r.EntityId);
            return new Candidate(r.Id, company, company is int c ? names.GetValueOrDefault(c, "") : "",
                r.EntityId, r.EntityId is int e ? names.GetValueOrDefault(e, "") : "",
                r.AccountId, r.AccountId is int a ? accounts.GetValueOrDefault(a, "") : "", r.Name, r.UpdatedAt);
        }).ToList();
    }
}
