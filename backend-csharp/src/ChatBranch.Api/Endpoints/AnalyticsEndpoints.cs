using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Analytics surface (/api/analytics/*): the message/call summary and the LLM
/// usage/billing report. Port of <c>calls/views.py analytics_summary</c> +
/// <c>analytics_usage</c>.
///
/// The payloads are built INLINE as anonymous objects with snake_case member
/// names — that is the wire contract the dashboard reads
/// (frontend/src/components/analytics/types.ts). The default web JSON policy
/// only lower-cases a leading capital, so a snake_case identifier survives
/// serialization byte-for-byte; do NOT route these through a PascalCase DTO.
///
/// Both handlers are total: any failure degrades to the same shape with zeros
/// and empty lists, because /dashboard renders the response unconditionally and
/// a 500 blanks the whole page.
/// </summary>
public static class AnalyticsEndpoints
{
    // Projections pulled into memory. The window bounds the row count, and the
    // funnel/session/day math is per-row Python in Django anyway — keeping it in
    // C# avoids provider-specific aggregate translation failing at runtime.
    private record MsgRow(int ThreadId, int? AccountId, string Direction, bool Private,
        string Status, DateTime CreatedAt);
    private record CallRow(int? ContactId, string StartedAt, string EndedAt, string Outcome,
        bool NeedsHuman);
    private record EntityLink(int ContactId, int EntityId);
    private record UsageRow(string Feature, string Provider, string Model, int? ContactId,
        string? ContactName, int PromptTokens, int CompletionTokens, int AudioPromptTokens,
        int AudioCompletionTokens, DateTime CreatedAt, double? CostUsd);

    public static void MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analytics/summary", async (string? days, string? entity,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var window = ParseDays(days);
            // an entity-bound caller: an out-of-scope ?entity= narrows to their own scope
            var entityId = ShellScope.ClampPick(ShellScope.BoundEntityIds(db, sc, me),
                await InboxEndpoints.EntityId(db, entity));
            var ent = entityId is int eid
                ? await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid)
                : null;
            try
            {
                return await SummaryAsync(db, sc, me, window, ent);
            }
            catch (Exception)
            {
                return EmptySummary(window, ent);
            }
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("analytics.view");

        // The organization-wide ledger at COST (USD) — what the platform pays.
        // billing.manage only (superadmin by default): companies see their marked-up
        // bill at /api/billing/ai instead, never the cost.
        app.MapGet("/api/analytics/usage", async (string? days, string? entity, AppDbContext db, ScopingService sc,
            IHttpClientFactory http) =>
        {
            var window = ParseDays(days);
            try
            {
                await ModelPriceCatalog.EnsureFreshAsync(http, sc);
                var entityId = await InboxEndpoints.EntityId(db, entity);
                return await UsageAsync(db, sc, window, entityId);
            }
            catch (Exception)
            {
                return EmptyUsage(window);
            }
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(AiBillingEndpoints.ManageCap);
    }

    // ---- summary -----------------------------------------------------------

    private static async Task<IResult> SummaryAsync(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, int days, Entity? ent)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-days);
        // CallRecord.started_at/ended_at are ISO-8601 STRINGS written by the
        // Django-free agent, so the window filter is an ordinal prefix compare.
        var sinceKey = since.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        int? entityId = ent?.Id;

        // ---- 0. entity scope. Every section below is built off these bases.
        // An entity-bound agent sees only their assigned subtrees (+ the
        // unassigned conversations the inbox shows them), never the tenant.
        var bound = ShellScope.BoundEntityIds(db, sc, me);
        var boundInherited = ShellScope.BoundInheritedIds(db, sc, me);
        var threadBase = sc.ScopeOwned(db.Threads.AsNoTracking(), entityId);
        if (bound != null)
            threadBase = entityId is int
                ? threadBase.Where(t => t.EntityId != null && bound.Contains(t.EntityId.Value))
                : ShellScope.ScopeThreads(db.Threads.AsNoTracking(), bound);
        var threadRows = await threadBase
            .Select(t => new { t.Id, t.EntityId, t.ContactId }).ToListAsync();
        var threadMeta = threadRows.ToDictionary(t => t.Id, t => (t.EntityId, t.ContactId));

        var links = await db.ContactEntities.AsNoTracking()
            .Select(l => new EntityLink(l.ContactId, l.EntityId)).ToListAsync();
        // Contacts the entity owns — the join Django spells `contact__entity_links__entity`.
        // the picked entity — or every ticked one (EntitySelection)
        HashSet<int>? picks = entityId is int pe
            ? (EntitySelection.Covers(pe) ? EntitySelection.Current!.ToHashSet() : new HashSet<int> { pe })
            : null;
        if (picks != null && bound != null) picks.IntersectWith(bound);
        var contactPicks = picks ?? bound?.ToHashSet();
        HashSet<int>? entityContacts = contactPicks != null
            ? links.Where(l => contactPicks.Contains(l.EntityId)).Select(l => l.ContactId).ToHashSet()
            : null;

        var msgs = (await db.Messages.AsNoTracking().Where(m => m.CreatedAt >= since)
                .Select(m => new MsgRow(m.ThreadId, m.AccountId, m.Direction, m.Private,
                    m.Status, m.CreatedAt)).ToListAsync())
            .Where(m => threadMeta.ContainsKey(m.ThreadId)).ToList();

        // ---- 1. message funnel
        var sent = msgs.Count(IsOut);
        var delivered = msgs.Count(m => IsOut(m) && (m.Status == "delivered" || m.Status == "read"));
        var read = msgs.Count(m => IsOut(m) && m.Status == "read");
        var replied = Replied(msgs);

        // ---- 2. engagement + records
        var callRows = await db.CallRecords.AsNoTracking()
            .Select(c => new CallRow(c.ContactId, c.StartedAt, c.EndedAt, c.Outcome, c.NeedsHuman))
            .ToListAsync();
        if (entityContacts is HashSet<int> ownedCalls)
            callRows = callRows.Where(c => c.ContactId is int cid && ownedCalls.Contains(cid)).ToList();
        var callsIn = callRows.Where(c => InCallWindow(c, sinceKey)).ToList();

        var contacted = new HashSet<int>();
        foreach (var m in msgs.Where(IsOut))
            if (threadMeta.TryGetValue(m.ThreadId, out var meta) && meta.ContactId is int cid)
                contacted.Add(cid);
        foreach (var c in callsIn)
            if (c.ContactId is int cid) contacted.Add(cid);
        if (entityContacts != null) contacted.IntersectWith(entityContacts);

        var resolved = 0;
        if (contacted.Count > 0)
        {
            // Contact has no resolution timestamp — "resolved" is the CURRENT status.
            var resolvedIds = (await db.Contacts.AsNoTracking()
                .Where(c => c.Status == "resolved").Select(c => c.Id).ToListAsync()).ToHashSet();
            resolved = contacted.Count(resolvedIds.Contains);
        }

        var recRows = await db.Records.AsNoTracking().Where(r => r.UpdatedAt >= since)
            .Select(r => new { r.ContactId, r.Status, r.Amount }).ToListAsync();
        if (entityContacts is HashSet<int> ownedRecords)
            recRows = recRows.Where(r => ownedRecords.Contains(r.ContactId)).ToList();
        var recordsByStatus = recRows.GroupBy(r => r.Status)
            .Select(g => new
            {
                status = g.Key,
                count = g.Count(),
                amount = (double)g.Sum(r => r.Amount ?? 0m),
            })
            .OrderByDescending(x => x.count).ThenBy(x => x.status, StringComparer.Ordinal).ToList();

        // ---- 3. per-account volumes (incl. the null-account "Unattributed" bucket)
        var acctAll = await db.ChannelAccounts.AsNoTracking()
            .Select(a => new { a.Id, a.EntityId, a.Name, a.Channel, a.Provider, a.HealthStatus })
            .ToListAsync();
        var acctScoped = acctAll;
        if (picks != null)
        {
            // accounts_for_entity: a PREFERENCE, not a union — an outlet with its
            // own numbers never falls back to the shared organization pool.
            var own = acctAll.Where(a => a.EntityId is int ae && picks.Contains(ae)).ToList();
            acctScoped = own.Count > 0 ? own : acctAll.Where(a => a.EntityId == null).ToList();
        }
        else if (boundInherited != null)
            acctScoped = acctAll.Where(a => a.EntityId == null || boundInherited.Contains(a.EntityId.Value)).ToList();
        var acctMeta = acctScoped.ToDictionary(a => a.Id);
        var accounts = msgs.GroupBy(m => m.AccountId)
            .Select(g => new
            {
                id = g.Key,
                outbound = g.Count(IsOut),
                inbound = g.Count(m => m.Direction == MessageDirection.In),
            })
            .Where(x => x.outbound > 0 || x.inbound > 0)   // thread had only private notes
            .OrderByDescending(x => x.outbound).ThenByDescending(x => x.inbound)
            .Select(x =>
            {
                var known = x.id is int id && acctMeta.TryGetValue(id, out var row) ? row : null;
                return new
                {
                    account_id = x.id?.ToString(),
                    name = known?.Name ?? "Unattributed",
                    channel = known?.Channel,
                    provider = known?.Provider,
                    health_status = known?.HealthStatus ?? Health.Unknown,
                    outbound = x.outbound,
                    inbound = x.inbound,
                };
            }).ToList();

        // ---- 4. call outcomes
        var outcomes = callsIn.GroupBy(c => c.Outcome)
            .Select(g => new { outcome = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count).ThenBy(x => x.outcome, StringComparer.Ordinal).ToList();

        // ---- 5. account health history. AuditLog has no entity column — health
        // rows identify the account by NAME, so an entity view keeps only the
        // accounts that entity actually uses.
        var healthRows = await db.AuditLogs.AsNoTracking()
            .Where(l => l.Action == "account.health" && l.CreatedAt >= since)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new { l.Actor, l.Target, l.Detail, l.CreatedAt }).ToListAsync();
        if (entityId is int || bound != null)
        {
            var names = acctScoped.Select(a => a.Name).ToHashSet();
            healthRows = healthRows.Where(r => names.Contains(r.Target)).ToList();
        }
        var healthEvents = healthRows.Take(50).Select(r => new
        {
            actor = r.Actor,
            target = r.Target,
            detail = r.Detail,
            created_at = Iso(r.CreatedAt),
        }).ToList();

        // ---- 6. daily series (zero-filled, UTC days)
        var dayMsgs = msgs.GroupBy(m => m.CreatedAt.Date).ToDictionary(
            g => g.Key,
            g => (outbound: g.Count(IsOut),
                  inbound: g.Count(m => m.Direction == MessageDirection.In)));

        var evRows = await db.RecordEvents.AsNoTracking().Where(e => e.At >= since)
            .Select(e => new { e.At, ContactId = e.Record!.ContactId }).ToListAsync();
        if (entityContacts is HashSet<int> ownedEvents)
            evRows = evRows.Where(e => ownedEvents.Contains(e.ContactId)).ToList();
        var dayEvents = evRows.GroupBy(e => e.At.Date).ToDictionary(g => g.Key, g => g.Count());

        var dayCalls = new Dictionary<string, int>();
        foreach (var c in callsIn)
        {
            var stamp = string.IsNullOrEmpty(c.EndedAt) ? c.StartedAt : c.EndedAt;
            if (stamp.Length < 10) continue;
            var key = stamp[..10];
            dayCalls[key] = dayCalls.GetValueOrDefault(key) + 1;
        }

        var daily = new List<object>();
        for (var day = since.Date; day <= now.Date; day = day.AddDays(1))
        {
            var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            dayMsgs.TryGetValue(day, out var volumes);
            daily.Add(new
            {
                date = key,
                outbound = volumes.outbound,
                inbound = volumes.inbound,
                calls = dayCalls.GetValueOrDefault(key),
                record_events = dayEvents.GetValueOrDefault(day),
            });
        }

        // ---- session averages. Avg call length covers in-window CallRecords whose
        // started/ended stamps BOTH parse (an empty ended_at = never completed).
        var callSecs = new List<double>();
        foreach (var c in callRows.Where(c => AtLeast(c.StartedAt, sinceKey)))
            if (TryStamp(c.StartedAt, out var start) && TryStamp(c.EndedAt, out var end) && end > start)
                callSecs.Add((end - start).TotalSeconds);

        var (chatSessions, sessionMsgs, multiSecs) = ChatSessions(msgs);

        // ---- first response + the prior window of equal length, so the KPI
        // strip can say "▲ 8% vs prior" without a second round trip.
        var (frMedian, frSamples) = FirstResponse(msgs);
        var priorSince = since.AddDays(-days);
        var prior = (await db.Messages.AsNoTracking()
                .Where(m => m.CreatedAt >= priorSince && m.CreatedAt < since)
                .Select(m => new MsgRow(m.ThreadId, m.AccountId, m.Direction, m.Private,
                    m.Status, m.CreatedAt)).ToListAsync())
            .Where(m => threadMeta.ContainsKey(m.ThreadId)).ToList();
        var priorSent = prior.Count(IsOut);
        var priorRead = prior.Count(m => IsOut(m) && m.Status == "read");
        var priorReplied = Replied(prior);
        var (priorFr, priorFrSamples) = FirstResponse(prior);

        return Results.Ok(new
        {
            days,
            since = Iso(since),
            generated_at = Iso(now),
            // Which scope produced these numbers — the UI labels the page with it
            // so a filtered figure is never mistaken for a company-wide one.
            entity = ent?.Slug,
            entity_name = ent?.Name ?? "All entities",
            funnel = new
            {
                sent,
                delivered,
                read,
                replied,
                delivered_rate = Rate(delivered, sent),
                read_rate = Rate(read, sent),
                reply_rate = Rate(replied, sent),
            },
            engagement = new { contacted = contacted.Count, resolved },
            records_by_status = recordsByStatus,
            accounts,
            call_outcomes = new
            {
                total = callsIn.Count,
                needs_human = callsIn.Count(c => c.NeedsHuman),
                outcomes,
            },
            health_events = healthEvents,
            daily,
            sessions = new
            {
                avg_call_seconds = callSecs.Count > 0 ? (long)Math.Round(callSecs.Average()) : 0L,
                calls_counted = callSecs.Count,
                chat_sessions = chatSessions,
                avg_chat_messages = chatSessions > 0
                    ? Math.Round(sessionMsgs / (double)chatSessions, 1) : 0d,
                avg_chat_session_seconds = multiSecs.Count > 0
                    ? (long)Math.Round(multiSecs.Average()) : 0L,
            },
            // Median time from a customer's message to the first reply after it
            // (any outbound, human or AI); null when nothing was answered.
            first_response = new
            {
                median_seconds = frMedian,
                samples = frSamples,
            },
            prior = new
            {
                since = Iso(priorSince),
                sent = priorSent,
                read_rate = Rate(priorRead, priorSent),
                reply_rate = Rate(priorReplied, priorSent),
                first_response_median_seconds = priorFrSamples > 0 ? priorFr : null,
            },
            // FRANCHISE VIEW: in the All-entities scope, the same headline numbers
            // broken out per outlet. Empty when one entity is already selected
            // (the page IS that row).
            by_entity = entityId is null
                ? await ByEntityAsync(db, me, msgs, threadMeta, callsIn, links)
                : new List<object>(),
        });
    }

    /// <summary>
    /// replied — an APPROXIMATION counted per THREAD, not per message: a thread
    /// counts as replied when its first in-window outbound is followed by an
    /// in-window inbound. When the earliest in-window inbound comes after that
    /// outbound the 48h window is enforced exactly; when the thread also had
    /// inbound BEFORE it (already a two-way conversation) any later inbound
    /// counts and the bound is skipped.
    /// </summary>
    private static int Replied(List<MsgRow> msgs)
    {
        var window = TimeSpan.FromHours(48);
        var replied = 0;
        foreach (var g in msgs.GroupBy(m => m.ThreadId))
        {
            DateTime? firstOut = g.Where(IsOut).Select(m => (DateTime?)m.CreatedAt).Min();
            var inbound = g.Where(m => m.Direction == MessageDirection.In).ToList();
            if (firstOut is not DateTime fo || inbound.Count == 0) continue;
            if (inbound.Max(m => m.CreatedAt) <= fo) continue;
            var firstIn = inbound.Min(m => m.CreatedAt);
            if (firstIn > fo)          // earliest inbound IS the reply -> exact 48h check
            {
                if (firstIn - fo <= window) replied++;
            }
            else replied++;            // inbound on both sides -> active two-way thread
        }
        return replied;
    }

    /// <summary>
    /// First response: per thread, the first customer message after our last
    /// reply opens a wait; the next outbound closes it. Median over every closed
    /// wait in the window (private notes excluded). Waits still open at the end
    /// of the window are not counted — they show up in the live "waiting" list.
    /// </summary>
    private static (long? MedianSeconds, int Samples) FirstResponse(List<MsgRow> msgs)
    {
        var waits = new List<double>();
        foreach (var g in msgs.Where(m => !m.Private).GroupBy(m => m.ThreadId))
        {
            DateTime? open = null;
            foreach (var m in g.OrderBy(m => m.CreatedAt))
            {
                if (m.Direction == MessageDirection.In) open ??= m.CreatedAt;
                else if (IsOut(m) && open is DateTime o)
                {
                    waits.Add(Math.Max(0, (m.CreatedAt - o).TotalSeconds));
                    open = null;
                }
            }
        }
        if (waits.Count == 0) return (null, 0);
        waits.Sort();
        var mid = waits.Count / 2;
        var median = waits.Count % 2 == 1 ? waits[mid] : (waits[mid - 1] + waits[mid]) / 2;
        return ((long)Math.Round(median), waits.Count);
    }

    /// <summary>
    /// Chat sessions: consecutive messages in one thread with gaps &lt;= 30 min
    /// count as ONE session (any direction; private notes excluded, same rule as
    /// the funnel). Durations cover multi-message sessions only — a
    /// single-message session has no length to speak of, but still counts toward
    /// messages-per-session.
    /// </summary>
    private static (int Sessions, int Messages, List<double> MultiSecs) ChatSessions(List<MsgRow> msgs)
    {
        var gap = TimeSpan.FromMinutes(30);
        int sessions = 0, messages = 0;
        var multiSecs = new List<double>();
        int? curThread = null;
        DateTime curStart = default, curLast = default;
        var curN = 0;

        void Close()
        {
            if (curN == 0) return;
            sessions++;
            messages += curN;
            if (curN > 1) multiSecs.Add((curLast - curStart).TotalSeconds);
        }

        foreach (var m in msgs.Where(m => !m.Private).OrderBy(m => m.ThreadId).ThenBy(m => m.CreatedAt))
        {
            if (curThread != m.ThreadId || (curN > 0 && m.CreatedAt - curLast > gap))
            {
                Close();
                curThread = m.ThreadId;
                curStart = m.CreatedAt;
                curN = 0;
            }
            curLast = m.CreatedAt;
            curN++;
        }
        Close();
        return (sessions, messages, multiSecs);
    }

    /// <summary>
    /// Per-outlet headline figures for the All-entities view — one row per entity
    /// the caller may see, plus an "Unassigned" row when conversations landed on a
    /// shared number before anyone tagged them (hiding those would make the rows
    /// silently fail to add up to the total).
    /// </summary>
    private static async Task<List<object>> ByEntityAsync(AppDbContext db, ClaimsPrincipal me,
        List<MsgRow> msgs, Dictionary<int, (int? EntityId, int? ContactId)> threadMeta,
        List<CallRow> callsIn, List<EntityLink> links)
    {
        var rows = new List<object>();
        var entities = await VisibleEntitiesAsync(db, me);
        if (entities.Count == 0) return rows;

        var contactsPerEntity = links.GroupBy(l => l.EntityId)
            .ToDictionary(g => g.Key, g => g.Select(l => l.ContactId).ToHashSet());

        object Row(string name, string? slug, List<MsgRow> scoped, int contacts, int calls) => new
        {
            entity = slug,
            name,
            outbound = scoped.Count(IsOut),
            inbound = scoped.Count(m => m.Direction == MessageDirection.In),
            contacts,
            calls,
        };

        foreach (var ent in entities)
        {
            var own = contactsPerEntity.GetValueOrDefault(ent.Id) ?? new HashSet<int>();
            var scoped = msgs.Where(m => threadMeta.TryGetValue(m.ThreadId, out var meta)
                                         && meta.EntityId == ent.Id).ToList();
            rows.Add(Row(ent.Name, ent.Slug, scoped, own.Count,
                callsIn.Count(c => c.ContactId is int cid && own.Contains(cid))));
        }

        var unassigned = msgs.Where(m => threadMeta.TryGetValue(m.ThreadId, out var meta)
                                         && meta.EntityId == null).ToList();
        if (unassigned.Any(m => IsOut(m) || m.Direction == MessageDirection.In))
            rows.Add(Row("Unassigned", null, unassigned, 0, 0));
        return rows;
    }

    /// <summary>
    /// Entities the caller may see, RBAC-bounded: superadmin = every tenant,
    /// admin = their own tenant, everyone else = the explicitly assigned entities
    /// within their tenant. Mirrors <c>scoping.visible_entities</c>.
    /// </summary>
    private static async Task<List<Entity>> VisibleEntitiesAsync(AppDbContext db, ClaimsPrincipal me)
    {
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        var tenantId = int.TryParse(me.FindFirstValue("tenant_id"), out var parsed) ? parsed : (int?)null;
        var q = db.Entities.AsNoTracking().AsQueryable();

        if (role == Roles.SuperAdmin)
        {
            // every tenant
        }
        else if (role == Roles.Admin)
        {
            if (tenantId is not int tid) return new List<Entity>();
            q = q.Where(e => e.TenantId == tid);
        }
        else
        {
            // agent (and every custom role): only the explicitly assigned
            // entities, constrained to their tenant as defense in depth.
            var username = me.Identity?.Name;
            if (string.IsNullOrWhiteSpace(username) || tenantId is not int atid)
                return new List<Entity>();
            var userId = await db.Users.AsNoTracking().Where(u => u.Username == username)
                .Select(u => (int?)u.Id).FirstOrDefaultAsync();
            if (userId is not int uid) return new List<Entity>();
            var profile = await db.Profiles.AsNoTracking().Include(p => p.Entities)
                .FirstOrDefaultAsync(p => p.UserId == uid);
            var ids = profile?.Entities.Select(e => e.Id).ToList() ?? new List<int>();
            if (ids.Count == 0) return new List<Entity>();
            q = q.Where(e => ids.Contains(e.Id) && e.TenantId == atid);
        }
        return await q.OrderBy(e => e.Slug).ToListAsync();
    }

    private static IResult EmptySummary(int days, Entity? ent)
    {
        var now = DateTime.UtcNow;
        return Results.Ok(new
        {
            days,
            since = Iso(now.AddDays(-days)),
            generated_at = Iso(now),
            entity = ent?.Slug,
            entity_name = ent?.Name ?? "All entities",
            funnel = new
            {
                sent = 0, delivered = 0, read = 0, replied = 0,
                delivered_rate = 0d, read_rate = 0d, reply_rate = 0d,
            },
            engagement = new { contacted = 0, resolved = 0 },
            records_by_status = new List<object>(),
            accounts = new List<object>(),
            call_outcomes = new { total = 0, needs_human = 0, outcomes = new List<object>() },
            health_events = new List<object>(),
            daily = new List<object>(),
            sessions = new
            {
                avg_call_seconds = 0L, calls_counted = 0, chat_sessions = 0,
                avg_chat_messages = 0d, avg_chat_session_seconds = 0L,
            },
            first_response = new { median_seconds = (long?)null, samples = 0 },
            by_entity = new List<object>(),
        });
    }

    // ---- usage -------------------------------------------------------------

    private const string PricingNote =
        "The provider's own price per call where it reports one (OpenRouter); otherwise " +
        "estimated from the llm_prices table (USD per 1M tokens) over OpenRouter's public " +
        "price list. Models priced nowhere are costed at 0.";

    private static async Task<IResult> UsageAsync(AppDbContext db, ScopingService sc, int days, int? entityId = null)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-days);
        var prices = PriceTable(sc);

        var q = db.LlmUsage.AsNoTracking().Where(u => u.CreatedAt >= since);
        // the entity switcher: rows stamped with an entity in the picked subtrees, plus
        // unstamped rows whose contact belongs there (the bill's own fallback); usage
        // tied to no entity at all (skill drafting, embeddings…) shows only under All
        if (entityId is int eid)
        {
            var ids = sc.PickSubtree(eid);
            var contactIds = db.ContactEntities.AsNoTracking()
                .Where(l => ids.Contains(l.EntityId)).Select(l => l.ContactId);
            q = q.Where(u => (u.EntityId != null && ids.Contains(u.EntityId.Value))
                             || (u.EntityId == null && u.ContactId != null && contactIds.Contains(u.ContactId.Value)));
        }
        var rows = await q
            .Select(u => new UsageRow(u.Feature, u.Provider, u.Model, u.ContactId,
                u.Contact!.Name, u.PromptTokens, u.CompletionTokens, u.AudioPromptTokens,
                u.AudioCompletionTokens, u.CreatedAt, u.CostUsd))
            .ToListAsync();

        // Unknown models cost 0 (reported as `priced: false` rather than guessed).
        double Cost(string? model, Totals t)
        {
            var name = model ?? "";
            var cost = 0d;
            if (prices.TryGetValue(name, out var text))
                cost += (t.Prompt * text.In + t.Completion * text.Out) / 1_000_000d;
            if (prices.TryGetValue(name + ":audio", out var audio))
                cost += (t.AudioPrompt * audio.In + t.AudioCompletion * audio.Out) / 1_000_000d;
            return Math.Round(cost, 6);
        }

        // A feature/contact/day mixes models, so cost must be priced PER MODEL
        // before the tokens are collapsed — otherwise the whole group is billed
        // at one model's rate.
        // A row the provider priced keeps that price; the rest are estimated per model.
        double CostOf(IEnumerable<UsageRow> group) =>
            Math.Round(group.Where(r => r.CostUsd != null).Sum(r => r.CostUsd!.Value)
                       + group.Where(r => r.CostUsd == null).GroupBy(r => r.Model).Sum(g => Cost(g.Key, Sum(g))), 6);

        var byModel = rows.GroupBy(r => new { r.Model, r.Provider }).Select(g =>
        {
            var t = Sum(g);
            return new
            {
                model = g.Key.Model,
                provider = g.Key.Provider,
                prompt_tokens = t.Prompt,
                completion_tokens = t.Completion,
                audio_prompt_tokens = t.AudioPrompt,
                audio_completion_tokens = t.AudioCompletion,
                requests = t.Requests,
                est_cost_usd = CostOf(g),
                priced = g.All(r => r.CostUsd != null) || prices.ContainsKey(g.Key.Model ?? ""),
            };
        }).OrderByDescending(r => r.est_cost_usd).ToList();

        var byFeature = rows.GroupBy(r => r.Feature).Select(g =>
        {
            var t = Sum(g);
            return new
            {
                feature = g.Key,
                prompt_tokens = t.Prompt,
                completion_tokens = t.Completion,
                audio_prompt_tokens = t.AudioPrompt,
                audio_completion_tokens = t.AudioCompletion,
                requests = t.Requests,
                est_cost_usd = CostOf(g),
            };
        }).OrderByDescending(r => r.est_cost_usd).ToList();

        var byContact = rows.GroupBy(r => new { r.ContactId, r.ContactName }).Select(g =>
        {
            var t = Sum(g);
            return new
            {
                contact_id = g.Key.ContactId,
                name = !string.IsNullOrEmpty(g.Key.ContactName) ? g.Key.ContactName
                    : g.Key.ContactId is null ? "(no contact)" : "",
                prompt_tokens = t.Prompt,
                completion_tokens = t.Completion,
                audio_prompt_tokens = t.AudioPrompt,
                audio_completion_tokens = t.AudioCompletion,
                requests = t.Requests,
                est_cost_usd = CostOf(g),
            };
        }).OrderByDescending(r => r.est_cost_usd).Take(20).ToList();   // top 20 = the billing view

        var byDay = rows.GroupBy(r => r.CreatedAt.Date)
            .ToDictionary(g => g.Key, g => (tokens: Sum(g).AllTokens, cost: CostOf(g)));
        var daily = new List<object>();
        for (var i = 0; i < days; i++)
        {
            var day = since.AddDays(i + 1).Date;
            byDay.TryGetValue(day, out var point);
            daily.Add(new
            {
                date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                tokens = point.tokens,
                est_cost_usd = point.cost,
            });
        }

        var totals = Sum(rows);
        return Results.Ok(new
        {
            days,
            since = Iso(since),
            generated_at = Iso(now),
            totals = new
            {
                prompt_tokens = totals.Prompt,
                completion_tokens = totals.Completion,
                audio_prompt_tokens = totals.AudioPrompt,
                audio_completion_tokens = totals.AudioCompletion,
                requests = totals.Requests,
                est_cost_usd = Math.Round(byModel.Sum(r => r.est_cost_usd), 4),
            },
            by_feature = byFeature,
            by_model = byModel,
            by_contact = byContact,
            daily,
            pricing_note = PricingNote,
        });
    }

    private static IResult EmptyUsage(int days)
    {
        var now = DateTime.UtcNow;
        return Results.Ok(new
        {
            days,
            since = Iso(now.AddDays(-days)),
            generated_at = Iso(now),
            totals = new
            {
                prompt_tokens = 0L, completion_tokens = 0L, audio_prompt_tokens = 0L,
                audio_completion_tokens = 0L, requests = 0, est_cost_usd = 0d,
            },
            by_feature = new List<object>(),
            by_model = new List<object>(),
            by_contact = new List<object>(),
            daily = new List<object>(),
            pricing_note = PricingNote,
        });
    }

    /// <summary>
    /// Effective per-model prices: Setting <c>llm_prices</c> (JSON
    /// {"model": [in, out]}) over OpenRouter's public list
    /// (<see cref="ModelPriceCatalog"/>) over the shipped defaults. The key is NOT
    /// entity-scoped by design, so this always resolves the organization row.
    /// Malformed entries are ignored rather than failing the report.
    /// </summary>
    internal static Dictionary<string, (double In, double Out)> PriceTable(ScopingService sc) =>
        ChatBranch.Data.Ai.LlmPricing.PriceTable(sc);

    /// <summary>The cost of one usage row (<see cref="ChatBranch.Data.Ai.LlmPricing.RowCost"/>).</summary>
    internal static double RowCost(Dictionary<string, (double In, double Out)> prices, string? model,
        long prompt, long completion, long audioPrompt, long audioCompletion, double? reported) =>
        ChatBranch.Data.Ai.LlmPricing.RowCost(prices, model, prompt, completion, audioPrompt, audioCompletion, reported);

    private record struct Totals(long Prompt, long Completion, long AudioPrompt,
        long AudioCompletion, int Requests)
    {
        public long AllTokens => Prompt + Completion + AudioPrompt + AudioCompletion;
    }

    private static Totals Sum(IEnumerable<UsageRow> rows)
    {
        var t = new Totals();
        foreach (var r in rows)
            t = new Totals(t.Prompt + r.PromptTokens, t.Completion + r.CompletionTokens,
                t.AudioPrompt + r.AudioPromptTokens, t.AudioCompletion + r.AudioCompletionTokens,
                t.Requests + 1);
        return t;
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>`days` defaults to 30 and is clamped to 1..365; junk is not a 400.</summary>
    private static int ParseDays(string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            days = 30;
        return Math.Max(1, Math.Min(days, 365));
    }

    /// <summary>Outbound as the funnel counts it — private notes never reach the contact.</summary>
    private static bool IsOut(MsgRow m) => m.Direction == MessageDirection.Out && !m.Private;

    private static bool AtLeast(string? stamp, string sinceKey) =>
        string.CompareOrdinal(stamp ?? "", sinceKey) >= 0;

    /// <summary>Records with an empty ended_at fall back to started_at.</summary>
    private static bool InCallWindow(CallRow c, string sinceKey) =>
        AtLeast(c.EndedAt, sinceKey) ||
        (string.IsNullOrEmpty(c.EndedAt) && AtLeast(c.StartedAt, sinceKey));

    private static bool TryStamp(string? raw, out DateTime value) =>
        DateTime.TryParse(raw ?? "", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);

    private static double Rate(int n, int d) => d > 0 ? Math.Round(n / (double)d, 4) : 0d;

    private static string Iso(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("o");
}
