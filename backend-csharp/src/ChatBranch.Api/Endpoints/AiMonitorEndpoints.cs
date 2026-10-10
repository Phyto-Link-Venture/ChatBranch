using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The AI monitor (human-in-the-loop review of everything the AI writes) and
/// the "AI behaviour" settings (review policy + human-like conversation knobs).
///
/// * <c>GET /api/ai/monitor</c> — drafts, entity-scoped exactly like the inbox
///   thread list; <c>?thread=&lt;channel&gt;:&lt;key&gt;&amp;status=pending</c> is the
///   seam a chat banner uses to show "an AI reply is waiting".
/// * approve / edit &amp; send / reject (single + bulk) go through
///   <see cref="AiGate"/>, which re-runs every send-time guard.
/// * <c>GET/PUT/DELETE /api/ai/behaviour?entity=</c> — Setting rows,
///   entity-overridable, catalog in <see cref="AiBehaviourSettings"/>.
///
/// Capabilities: <c>ai.monitor</c> (view + decide), <c>ai.behaviour.manage</c>
/// (change the rules). Every decision and settings change is audited. Draft
/// changes bump Thread.updated_at, so <c>/api/whatsapp/stream</c> doubles as the
/// monitor's live-update signal.
/// </summary>
public static class AiMonitorEndpoints
{
    public const string MonitorCap = "ai.monitor";
    public const string BehaviourCap = "ai.behaviour.manage";

    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    private static readonly (string Id, string Title, string Description)[] Groups =
    {
        ("review", "Review before sending",
            "Decide which AI messages a person checks before they reach the customer."),
        ("wait", "Wait for the customer to finish",
            "Answer a burst of short messages once, and let simple acknowledgements be."),
        ("typing", "Type like a person",
            "Show typing where the channel supports it and pause in proportion to the reply."),
        ("split", "Split long replies",
            "Long answers arrive as a few shorter messages, like a person would send them."),
        ("human", "Human-like",
            "Emoji, emoji reactions and quoted replies — the small touches of a person typing on their phone."),
        ("followup", "Follow up when the customer goes quiet",
            "A gentle nudge after the AI asked something and heard nothing back."),
        ("learning", "Learn from feedback",
            "Turn thumbs up/down, approvals and edits into lessons the AI uses in later replies."),
    };

    public static void MapAiMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- list ---------------------------------------------------------------
        app.MapGet("/api/ai/monitor", async (string? status, string? channel, string? entity,
            string? kind, string? q, string? thread, int? page, int? page_size, string? sort,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, TimeProvider clock) =>
        {
            var scope = await ScopeFor(db, sc, me, entity);
            var baseQ = Scoped(db.AiDrafts.AsNoTracking(), scope);
            if (!string.IsNullOrWhiteSpace(channel) && channel is not ("all" or "__all__"))
                baseQ = baseQ.Where(d => d.Channel == channel);
            if (!string.IsNullOrWhiteSpace(kind) && kind != "all")
                baseQ = baseQ.Where(d => d.Kind == kind);
            if (!string.IsNullOrWhiteSpace(thread) && thread.Contains(':'))
            {
                var ch = thread[..thread.IndexOf(':')];
                var key = thread[(thread.IndexOf(':') + 1)..];
                baseQ = baseQ.Where(d => d.Channel == ch && d.ThreadKey == key);
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                baseQ = baseQ.Where(d => EF.Functions.Like(d.Content, like)
                    || EF.Functions.Like(d.ThreadKey, like)
                    || db.Threads.Any(t => t.Id == d.ThreadId && EF.Functions.Like(t.Name, like)));
            }

            var counts = await baseQ.GroupBy(d => d.Status)
                .Select(g => new { status = g.Key, n = g.Count() }).ToListAsync();

            var statuses = (status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s != "all").ToList();
            var filtered = statuses.Count > 0 ? baseQ.Where(d => statuses.Contains(d.Status)) : baseQ;
            var total = await filtered.CountAsync();
            var size = Math.Clamp(page_size ?? DefaultPageSize, 1, MaxPageSize);
            var pageNo = Math.Max(1, page ?? 1);
            var ordered = sort == "oldest"
                ? filtered.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
                : filtered.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id);
            var rows = await ordered.Skip((pageNo - 1) * size).Take(size).ToListAsync();

            var payload = await Payloads(db, rows, clock, withRecent: rows.Any(r => r.Status == AiDraftStatus.Pending), me: me);
            return Results.Ok(new
            {
                drafts = payload,
                total,
                page = pageNo,
                page_size = size,
                counts = AiDraftStatus.All.ToDictionary(s => s, s => counts.FirstOrDefault(c => c.status == s)?.n ?? 0),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(MonitorCap);

        // ---- stats (registered before /{id} so the literal wins) ----------------
        app.MapGet("/api/ai/monitor/stats", async (int? days, string? entity, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, TimeProvider clock) =>
        {
            var span = Math.Clamp(days ?? 30, 1, 365);
            var now = clock.GetUtcNow().UtcDateTime;
            var since = now.Date.AddDays(-(span - 1));
            var scope = await ScopeFor(db, sc, me, entity);
            var rows = await Scoped(db.AiDrafts.AsNoTracking(), scope)
                .Where(d => d.CreatedAt >= since)
                .Select(d => new
                {
                    d.Status, d.Kind, d.HoldReason, d.RejectReason, d.CreatedAt, d.DecidedAt, d.DecidedBy,
                })
                .Take(50000).ToListAsync();

            int Count(string s) => rows.Count(r => r.Status == s);
            var human = rows.Where(r => r.Status is AiDraftStatus.Approved or AiDraftStatus.Edited
                                                 or AiDraftStatus.Rejected
                                        && r.DecidedAt != null).ToList();
            var reviewSeconds = human.Select(r => (r.DecidedAt!.Value - r.CreatedAt).TotalSeconds)
                .Where(x => x >= 0).OrderBy(x => x).ToList();
            double? median = reviewSeconds.Count == 0 ? null
                : reviewSeconds.Count % 2 == 1 ? reviewSeconds[reviewSeconds.Count / 2]
                : (reviewSeconds[reviewSeconds.Count / 2 - 1] + reviewSeconds[reviewSeconds.Count / 2]) / 2;
            var approved = Count(AiDraftStatus.Approved);
            var edited = Count(AiDraftStatus.Edited);
            var rejected = Count(AiDraftStatus.Rejected);

            var rejectReasons = rows.Where(r => r.Status == AiDraftStatus.Rejected)
                .GroupBy(r => string.IsNullOrWhiteSpace(r.RejectReason) ? "" : r.RejectReason.Trim().ToLowerInvariant())
                .Select(g => new
                {
                    reason = g.Key.Length == 0 ? "(no reason given)" : g.First().RejectReason.Trim(),
                    count = g.Count(),
                })
                .OrderByDescending(x => x.count).Take(8).ToList();
            var holdReasons = rows.Where(r => r.HoldReason.Length > 0 && r.Status != AiDraftStatus.Shadow)
                .SelectMany(r => r.HoldReason.Split("; ", StringSplitOptions.RemoveEmptyEntries))
                .GroupBy(x => x).Select(g => new { reason = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).Take(8).ToList();

            var daily = Enumerable.Range(0, span).Select(i => since.AddDays(i)).Select(day => new
            {
                date = day.ToString("yyyy-MM-dd"),
                auto_sent = rows.Count(r => r.CreatedAt.Date == day && r.Status == AiDraftStatus.AutoSent),
                held = rows.Count(r => r.CreatedAt.Date == day && r.HoldReason.Length > 0
                                       && r.Status != AiDraftStatus.Shadow),
                rejected = rows.Count(r => r.CreatedAt.Date == day && r.Status == AiDraftStatus.Rejected),
            }).ToList();

            return Results.Ok(new
            {
                days = span,
                total = rows.Count,
                held = rows.Count(r => r.HoldReason.Length > 0 && r.Status != AiDraftStatus.Shadow),
                pending = Count(AiDraftStatus.Pending),
                approved,
                edited,
                rejected,
                auto_sent = Count(AiDraftStatus.AutoSent),
                sent_after_deadline = Count(AiDraftStatus.Sent),
                expired = Count(AiDraftStatus.Expired),
                failed = Count(AiDraftStatus.Failed),
                shadow = Count(AiDraftStatus.Shadow),
                approval_rate = approved + edited + rejected == 0 ? (double?)null
                    : Math.Round((approved + edited) * 100.0 / (approved + edited + rejected), 1),
                edit_rate = approved + edited == 0 ? (double?)null
                    : Math.Round(edited * 100.0 / (approved + edited), 1),
                median_review_seconds = median is double m ? Math.Round(m, 1) : (double?)null,
                by_kind = AiDraftKind.All.ToDictionary(k => k, k => rows.Count(r => r.Kind == k)),
                top_reject_reasons = rejectReasons,
                top_hold_reasons = holdReasons,
                daily,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(MonitorCap);

        // ---- manual worker pass (tests / live checks) ---------------------------
        app.MapPost("/api/ai/monitor/tick", async (AppDbContext db, ClaimsPrincipal me,
            AiGate gate, AiFollowUpService followUps, ChatBranch.Data.Bookings.BookingService bookings) =>
        {
            if (!await Can(db, me, BehaviourCap)) return Forbidden(BehaviourCap);
            var autosent = gate.ProcessDue();
            var f = followUps.Tick();
            var released = bookings.Housekeep();
            return Results.Ok(new { autosent, followups = f.Submitted, considered = f.Considered,
                bookings_released = released });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- bulk decisions -------------------------------------------------------
        app.MapPost("/api/ai/monitor/bulk", async (JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, HttpContext ctx, AiGate gate) =>
        {
            if (!await Can(db, me, MonitorCap)) return Forbidden(MonitorCap);
            var action = Str(body, "action")?.Trim().ToLowerInvariant() ?? "";
            if (action is not ("approve" or "reject"))
                return Results.Json(new { detail = "action must be 'approve' or 'reject'." }, statusCode: 400);
            var ids = new List<int>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("ids", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) ids.Add(n);
            if (ids.Count == 0) return Results.Json(new { detail = "ids is required." }, statusCode: 400);
            if (ids.Count > 100) return Results.Json(new { detail = "At most 100 drafts at a time." }, statusCode: 400);

            var scope = await ScopeFor(db, sc, me, null);
            var visible = await Scoped(db.AiDrafts.AsNoTracking(), scope)
                .Where(d => ids.Contains(d.Id)).Select(d => d.Id).ToListAsync();
            var actor = me.Identity?.Name ?? "unknown";
            var reason = Str(body, "reason");
            var results = new List<object>();
            foreach (var id in ids.Distinct())
            {
                if (!visible.Contains(id))
                {
                    results.Add(new { id, ok = false, status = AiGateStatus.NotFound, detail = "Draft not found." });
                    continue;
                }
                var o = action == "approve" ? gate.Approve(id, actor) : gate.Reject(id, actor, reason);
                await Audit(db, ctx, me, action == "approve" ? "ai.draft.approve" : "ai.draft.reject",
                    $"draft:{id}", action == "approve" ? $"{o.Status}: {o.Detail}" : (reason ?? ""));
                results.Add(new { id, ok = o.Delivered || o.Status == "rejected", status = o.Status, detail = o.Detail });
            }
            return Results.Ok(new { results });
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- one draft ------------------------------------------------------------
        app.MapGet("/api/ai/monitor/{id:int}", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, TimeProvider clock, AiFollowUpService followUps) =>
        {
            var draft = await Visible(db, sc, me, id);
            if (draft == null) return Results.NotFound(new { detail = "Draft not found." });
            var payload = (await Payloads(db, new List<AiDraft> { draft }, clock, withRecent: true, recentCount: 12, me: me))[0];
            var state = draft.ThreadId is int tid ? followUps.StateFor(tid) : null;
            return Results.Ok(new
            {
                draft = payload,
                follow_up = state == null ? null : new
                {
                    sent = state.FollowUpsSent,
                    last_at = state.LastFollowUpAt,
                    next_at = state.NextFollowUpAt,
                    note = state.Note,
                },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(MonitorCap);

        app.MapPost("/api/ai/monitor/{id:int}/approve", async (int id, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, AiGate gate, TimeProvider clock) =>
        {
            if (!await Can(db, me, MonitorCap)) return Forbidden(MonitorCap);
            if (await Visible(db, sc, me, id) == null) return Results.NotFound(new { detail = "Draft not found." });
            var body = await ReadJson(ctx);
            var o = gate.Approve(id, me.Identity?.Name ?? "unknown", null, Bool(body, "force") == true);
            await Audit(db, ctx, me, "ai.draft.approve", $"draft:{id}", $"{o.Status}: {o.Detail}");
            return await Decision(db, o, clock);
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/ai/monitor/{id:int}/send", async (int id, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, AiGate gate, TimeProvider clock) =>
        {
            if (!await Can(db, me, MonitorCap)) return Forbidden(MonitorCap);
            if (await Visible(db, sc, me, id) == null) return Results.NotFound(new { detail = "Draft not found." });
            var body = await ReadJson(ctx);
            var content = Str(body, "content");
            if (string.IsNullOrWhiteSpace(content))
                return Results.Json(new { detail = "content is required." }, statusCode: 400);
            var o = gate.Approve(id, me.Identity?.Name ?? "unknown", content, Bool(body, "force") == true);
            await Audit(db, ctx, me, "ai.draft.edit_send", $"draft:{id}", $"{o.Status}: {Cap(content, 300)}");
            return await Decision(db, o, clock);
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapPost("/api/ai/monitor/{id:int}/reject", async (int id, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, AiGate gate, TimeProvider clock) =>
        {
            if (!await Can(db, me, MonitorCap)) return Forbidden(MonitorCap);
            if (await Visible(db, sc, me, id) == null) return Results.NotFound(new { detail = "Draft not found." });
            var body = await ReadJson(ctx);
            var reason = Str(body, "reason");
            var o = gate.Reject(id, me.Identity?.Name ?? "unknown", reason);
            await Audit(db, ctx, me, "ai.draft.reject", $"draft:{id}", reason ?? "");
            return await Decision(db, o, clock);
        }).RequireAuthorization(AuthPolicies.Authenticated);

        // ---- AI behaviour settings ----------------------------------------------
        app.MapGet("/api/ai/behaviour", async (string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var (entityId, err) = await BehaviourEntity(db, entity);
            if (err != null) return err;
            return Results.Ok(await BehaviourPayload(db, sc, me, entityId, entity));
        }).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireAnyCapability(MonitorCap, BehaviourCap);

        app.MapPut("/api/ai/behaviour", async (string? entity, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            if (!await Can(db, me, BehaviourCap)) return Forbidden(BehaviourCap);
            var (entityId, err) = await BehaviourEntity(db, entity);
            if (err != null) return err;
            if (!AiControlsEndpoints.MayEdit(db, sc, me, entityId))
                return Results.Json(new { detail = entityId == null
                    ? "Only an organization-wide admin can change the organization's AI behaviour."
                    : "That entity is outside your scope." }, statusCode: 403);
            var body = await ReadJson(ctx);
            if (body is not JsonElement root || root.ValueKind != JsonValueKind.Object)
                return Results.Json(new { detail = "Send {values: {key: value}}." }, statusCode: 400);
            var values = root.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object ? v : root;

            var writes = new List<(string Key, string Value)>();
            var errors = new Dictionary<string, string>();
            foreach (var p in values.EnumerateObject())
            {
                if (!AiBehaviourSettings.ByKey.ContainsKey(p.Name)) continue; // ignore unrelated keys
                var raw = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => p.Value.GetRawText(),
                };
                var problem = AiBehaviourSettings.Normalize(p.Name, raw, out var normalized);
                if (problem != null) errors[p.Name] = problem;
                else writes.Add((p.Name, normalized));
            }
            if (errors.Count > 0)
                return Results.Json(new { detail = errors.Values.First(), errors }, statusCode: 400);
            foreach (var (key, value) in writes) sc.SetSetting(key, value, entityId);
            if (writes.Count > 0)
                await Audit(db, ctx, me, "ai.behaviour.update", entity is { Length: > 0 } ? $"entity:{entity}" : "organization",
                    Cap(string.Join(", ", writes.Select(w => $"{w.Key}={w.Value}")), 1000));
            return Results.Ok(await BehaviourPayload(db, sc, me, entityId, entity));
        }).RequireAuthorization(AuthPolicies.Authenticated);

        app.MapDelete("/api/ai/behaviour", async (string? entity, string? key, HttpContext ctx,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            if (!await Can(db, me, BehaviourCap)) return Forbidden(BehaviourCap);
            var (entityId, err) = await BehaviourEntity(db, entity);
            if (err != null) return err;
            if (entityId == null)
                return Results.Json(new
                {
                    detail = "Pick an entity to remove its overrides. Organization defaults are edited, not deleted.",
                }, statusCode: 400);
            if (!AiControlsEndpoints.MayEdit(db, sc, me, entityId))
                return Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403);
            var keys = string.IsNullOrWhiteSpace(key) ? AiBehaviourSettings.Keys.ToList() : new List<string> { key };
            if (keys.Any(k => !AiBehaviourSettings.ByKey.ContainsKey(k)))
                return Results.Json(new { detail = $"Unknown setting '{key}'." }, statusCode: 400);
            var cleared = keys.Count(k => sc.ClearSetting(k, entityId));
            await Audit(db, ctx, me, "ai.behaviour.reset", $"entity:{entity}",
                string.IsNullOrWhiteSpace(key) ? $"all ({cleared} cleared)" : key);
            return Results.Ok(await BehaviourPayload(db, sc, me, entityId, entity));
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    // ---- payloads ---------------------------------------------------------------

    private static async Task<List<object>> Payloads(AppDbContext db, List<AiDraft> rows, TimeProvider clock,
        bool withRecent, int recentCount = 6, ClaimsPrincipal? me = null)
    {
        var myFeedback = await AiMonitorLearning.MyFeedbackAsync(db, rows, me); // AI learning: the caller's 👍/👎
        var threadIds = rows.Where(r => r.ThreadId != null).Select(r => r.ThreadId!.Value).Distinct().ToList();
        var threads = await db.Threads.AsNoTracking().Where(t => threadIds.Contains(t.Id)).ToListAsync();
        var contactIds = rows.Where(r => r.ContactId != null).Select(r => r.ContactId!.Value).Distinct().ToList();
        var contacts = await db.Contacts.AsNoTracking().Where(c => contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToListAsync();
        var accountIds = rows.Where(r => r.AccountId != null).Select(r => r.AccountId!.Value).Distinct().ToList();
        var accounts = await db.ChannelAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.Provider }).ToListAsync();
        var entityIds = rows.Where(r => r.EntityId != null).Select(r => r.EntityId!.Value).Distinct().ToList();
        var entities = await db.Entities.AsNoTracking().Where(e => entityIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Slug, e.Name }).ToListAsync();

        // a teammate's reply after the draft was written makes it stale
        var pendingThreads = rows.Where(r => r.Status == AiDraftStatus.Pending && r.ThreadId != null)
            .Select(r => r.ThreadId!.Value).Distinct().ToList();
        var humanReplies = await db.Messages.AsNoTracking()
            .Where(m => pendingThreads.Contains(m.ThreadId) && m.Direction == MessageDirection.Out
                        && m.Source == MessageSource.Agent && !m.Private)
            .GroupBy(m => m.ThreadId).Select(g => new { ThreadId = g.Key, Last = g.Max(m => m.CreatedAt) })
            .ToListAsync();

        var recentByThread = new Dictionary<int, List<object>>();
        if (withRecent)
            foreach (var tid in rows.Where(r => r.Status is AiDraftStatus.Pending or AiDraftStatus.Shadow || rows.Count == 1)
                         .Where(r => r.ThreadId != null).Select(r => r.ThreadId!.Value).Distinct())
            {
                var msgs = await db.Messages.AsNoTracking().Where(m => m.ThreadId == tid && !m.Private)
                    .OrderByDescending(m => m.Id).Take(recentCount).ToListAsync();
                msgs.Reverse();
                recentByThread[tid] = msgs.Select(m => (object)new
                {
                    id = m.Id, direction = m.Direction, source = m.Source, text = m.Text, at = m.CreatedAt,
                }).ToList();
            }

        // calendar bookings the draft's tools reserved (approving makes them)
        var actionsByDraft = rows.ToDictionary(r => r.Id, r => AiPendingAction.From(r.Context));
        var bookingIds = actionsByDraft.Values.SelectMany(a => a).Select(a => a.BookingId).Distinct().ToList();
        var bookingRows = bookingIds.Count == 0
            ? new Dictionary<int, Booking>()
            : await db.Bookings.AsNoTracking().Where(b => bookingIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id);

        // files the draft's tools made (Documents PDFs, QR codes) — sent after the text on delivery
        var docsByDraft = rows.ToDictionary(r => r.Id, r => ChatBranch.Data.Documents.AiAttachments.From(r.Context));
        var docIds = docsByDraft.Values.SelectMany(x => x).Distinct().ToList();
        var docRows = docIds.Count == 0
            ? new Dictionary<int, Document>()
            : await db.Documents.AsNoTracking().Where(x => docIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        // the human-like touches: the reaction it will make (or made) and the message it quotes
        var touchIds = rows.SelectMany(r => AiReaction.From(r.Context).Select(x => x.MessageId)
                .Concat(HumanTouch.ReplyTo(r.Context) is int q ? new[] { q } : Array.Empty<int>()))
            .Distinct().ToList();
        var touchText = touchIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Messages.AsNoTracking().Where(m => touchIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => m.Text.Length <= 200 ? m.Text : m.Text.Substring(0, 200) + "…");

        var now = clock.GetUtcNow().UtcDateTime;
        var scoping = new ScopingService(db);
        return rows.Select(d =>
        {
            var t = threads.FirstOrDefault(x => x.Id == d.ThreadId);
            var c = contacts.FirstOrDefault(x => x.Id == d.ContactId);
            var a = accounts.FirstOrDefault(x => x.Id == d.AccountId);
            var e = entities.FirstOrDefault(x => x.Id == d.EntityId);
            var lastHuman = humanReplies.FirstOrDefault(h => h.ThreadId == d.ThreadId)?.Last;
            return (object)new
            {
                id = d.Id,
                kind = d.Kind,
                status = d.Status,
                status_label = AiGate.Label(d.Status),
                channel = d.Channel,
                thread = t == null ? null : new
                {
                    id = t.Id, key = t.Key, name = t.Name, status = t.Status, assignee = t.Assignee,
                    ai_enabled = AiEligibility.Effective(db, scoping, t), opted_out = t.OptedOut,
                },
                thread_key = d.ThreadKey,
                contact = c == null ? null : new { id = c.Id, name = c.Name },
                account = a == null ? null : new { id = a.Id, name = a.Name, provider = a.Provider },
                entity = e == null ? null : new { slug = e.Slug, name = e.Name },
                content = d.Content,
                original_content = d.OriginalContent,
                hold_reason = d.HoldReason,
                reasons = d.HoldReason.Length == 0 || d.Status == AiDraftStatus.Shadow
                    ? new List<string>()
                    : d.HoldReason.Split("; ", StringSplitOptions.RemoveEmptyEntries).ToList(),
                flags = AiGate.Items(d.Context, "flags").Select(x => x is JsonElement j ? j.ToString() : x?.ToString() ?? "").ToList(),
                context = d.Context,
                my_feedback = myFeedback.GetValueOrDefault(d.Id),
                grounding = AiMonitorLearning.Grounding(d.Context),
                created_at = d.CreatedAt,
                updated_at = d.UpdatedAt,
                send_after = d.SendAfter,
                seconds_left = d.SendAfter is DateTime sa ? Math.Max(0, (int)Math.Ceiling((sa - now).TotalSeconds)) : (int?)null,
                decided_by = d.DecidedBy,
                decided_at = d.DecidedAt,
                reject_reason = d.RejectReason,
                sent_at = d.SentAt,
                sent_message_id = d.SentMessageId,
                error = d.Error,
                superseded = d.Status == AiDraftStatus.Pending && lastHuman is DateTime lh && lh > d.CreatedAt,
                recent = d.ThreadId is int rt && recentByThread.TryGetValue(rt, out var rec) ? rec : null,
                bookings = BookingsEndpoints.MonitorItems(actionsByDraft[d.Id], bookingRows),
                documents = docsByDraft[d.Id].Where(docRows.ContainsKey)
                    .Select(id => ChatBranch.Data.Documents.DocumentService.Payload(docRows[id])).ToList(),
                // made when the reply is delivered (before its text) — a held reply has not reacted yet
                reactions = AiReaction.From(d.Context).Select(x => new
                {
                    message_id = x.MessageId,
                    emoji = x.Emoji,
                    text = touchText.GetValueOrDefault(x.MessageId, ""),
                    done = AiGate.Bool(d.Context, "reacted") == true,
                    error = AiGate.Str(d.Context, "reaction_error", ""),
                }).ToList(),
                quote = HumanTouch.ReplyTo(d.Context) is int qid
                    ? new { message_id = qid, text = touchText.GetValueOrDefault(qid, "") }
                    : null,
            };
        }).ToList();
    }

    private static async Task<IResult> Decision(AppDbContext db, AiGateOutcome o, TimeProvider clock)
    {
        object? draft = null;
        if (o.DraftId > 0)
        {
            var row = await db.AiDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == o.DraftId);
            if (row != null) draft = (await Payloads(db, new List<AiDraft> { row }, clock, withRecent: false))[0];
        }
        var body = new { ok = o.Delivered || o.Status == "rejected", status = o.Status, detail = o.Detail, draft };
        return o.Status switch
        {
            AiGateStatus.NotFound => Results.Json(body, statusCode: 404),
            AiGateStatus.Conflict or AiGateStatus.Blocked => Results.Json(body, statusCode: 409),
            _ => Results.Ok(body),
        };
    }

    private static async Task<object> BehaviourPayload(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        int? entityId, string? entitySlug)
    {
        var keys = AiBehaviourSettings.Keys.ToList();
        var orgOnly = sc.SettingValues(keys);
        // what this level inherits: its parent's resolved values (up the tree), else the org's
        var parentId = entityId is int pe
            ? await db.Entities.AsNoTracking().Where(e => e.Id == pe).Select(e => e.ParentId).FirstOrDefaultAsync()
            : null;
        var global = parentId != null ? sc.SettingValues(keys, parentId) : orgOnly;
        var own = entityId is int eid
            ? await db.Settings.AsNoTracking().Where(s => s.EntityId == eid && keys.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, s => s.Value)
            : new Dictionary<string, string>();
        var entityName = entityId is int id2
            ? await db.Entities.AsNoTracking().Where(e => e.Id == id2).Select(e => e.Name).FirstOrDefaultAsync()
            : null;
        var effective = AiBehaviourSettings.Resolve(sc, entityId);
        return new
        {
            entity = entitySlug is { Length: > 0 } && entityId != null ? entitySlug : null,
            entity_name = entityName,
            can_manage = await Can(db, me, BehaviourCap),
            groups = Groups.Select(g => new { id = g.Id, title = g.Title, description = g.Description }),
            fields = AiBehaviourSettings.Fields.Select(f =>
            {
                var orgValue = global.TryGetValue(f.Key, out var gv) ? gv : AiBehaviourSettings.DefaultFor(f.Key);
                var hasOwn = own.TryGetValue(f.Key, out var ov) && !string.IsNullOrWhiteSpace(ov);
                var fromParent = global.TryGetValue(f.Key, out var pv)
                                 && (!orgOnly.TryGetValue(f.Key, out var gv2) || gv2 != pv);
                var scope = entityId != null
                    ? (hasOwn ? "entity" : fromParent ? "parent" : global.ContainsKey(f.Key) ? "global" : "default")
                    : (global.ContainsKey(f.Key) ? "global" : "default");
                return new
                {
                    key = f.Key, group = f.Group, type = f.Type, label = f.Label, help = f.Help,
                    options = f.Options, min = f.Min, max = f.Max,
                    @default = AiBehaviourSettings.DefaultFor(f.Key),
                    value = hasOwn ? ov!.Trim() : orgValue,
                    inherited_value = orgValue,
                    scope,
                };
            }),
            effective,
        };
    }

    // ---- scoping ----------------------------------------------------------------

    /// <summary>
    /// Which entities' drafts the caller may see. Mirrors the inbox thread list:
    /// admins see all (optionally narrowed by ?entity), agents/custom roles are
    /// bounded by their assigned entities — an out-of-scope ?entity narrows to
    /// their own scope rather than widening. Unassigned (null-entity) drafts stay
    /// visible, as unassigned conversations do in the inbox.
    /// </summary>
    internal record DraftScope(List<int>? EntityIds);

    internal static async Task<DraftScope> ScopeFor(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        string? entitySlug)
    {
        var picked = await InboxEndpoints.EntityId(db, entitySlug);
        var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
        // superadmin + org-wide admins: unbound; company admins, agents: their entities
        {
            var assigned = ShellScope.ScopeRoots(db, me);
            if (assigned.Count > 0)
            {
                var allowed = sc.EntitySubtreeIds(assigned);
                if (picked is int p && allowed.Contains(p)) return new DraftScope(sc.PickSubtree(p).Where(allowed.Contains).ToList());
                return new DraftScope(allowed);
            }
        }
        return new DraftScope(picked is int pk ? sc.PickSubtree(pk) : null);
    }

    private static IQueryable<AiDraft> Scoped(IQueryable<AiDraft> q, DraftScope scope) =>
        scope.EntityIds is { } ids
            ? q.Where(d => d.EntityId == null || ids.Contains(d.EntityId.Value))
            : q;

    private static async Task<AiDraft?> Visible(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int id)
    {
        var scope = await ScopeFor(db, sc, me, null);
        return await Scoped(db.AiDrafts.AsNoTracking(), scope).FirstOrDefaultAsync(d => d.Id == id);
    }

    private static async Task<(int? Id, IResult? Error)> BehaviourEntity(AppDbContext db, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "all" or "__all__") return (null, null);
        var id = await InboxEndpoints.EntityId(db, slug);
        return id == null
            ? (null, Results.Json(new { detail = $"Unknown entity '{slug}'." }, statusCode: 404))
            : (id, null);
    }

    // ---- helpers ----------------------------------------------------------------

    private static async Task<bool> Can(AppDbContext db, ClaimsPrincipal me, string capability) =>
        await AdminEndpoints.HasCapabilityAsync(db, me.FindFirstValue(ClaimTypes.Role) ?? "", capability);

    private static IResult Forbidden(string capability) =>
        Results.Json(new { detail = $"You need the '{capability}' permission." }, statusCode: 403);

    private static async Task<JsonElement?> ReadJson(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.ContentLength is 0) return null;
            return await ctx.Request.ReadFromJsonAsync<JsonElement>();
        }
        catch { return null; }
    }

    private static string? Str(JsonElement? body, string key) =>
        body is JsonElement b && b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.ToString()
            : null;

    private static string? Str(JsonElement body, string key) => Str((JsonElement?)body, key);

    private static bool? Bool(JsonElement? body, string key) =>
        body is JsonElement b && b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    /// <summary>One audit row; the trail must never break the decision it records.</summary>
    private static async Task Audit(AppDbContext db, HttpContext ctx, ClaimsPrincipal me,
        string action, string target, string detail)
    {
        try
        {
            var username = me.Identity?.Name ?? "anonymous";
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username);
            db.AuditLogs.Add(new AuditLog
            {
                UserId = user?.Id,
                Actor = Cap(username, 150),
                Action = Cap(action, 64),
                Target = Cap(target, 190),
                Detail = Cap(detail, 1000),
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        catch { }
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}
