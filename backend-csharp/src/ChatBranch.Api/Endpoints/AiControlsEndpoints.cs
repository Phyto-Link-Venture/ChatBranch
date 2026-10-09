using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// AI on/off controls and human-handover tracking.
///
/// * <c>GET/PUT /api/ai/controls?entity=</c> — the master switch (Setting
///   <c>ai_enabled</c>: on | off | schedule), the schedule (<c>ai_schedule</c>
///   per-weekday windows + <c>ai_schedule_mode</c> inside|outside, org timezone)
///   and the never-AI tags (<c>ai_never_tags</c>), per outlet or org-wide,
///   resolved up the entity tree. GET: ai.monitor / ai.behaviour.manage /
///   chats.view · PUT: ai.behaviour.manage (+ the outlet must be in the caller's scope).
/// * <c>GET/PUT /api/contacts/{id}/ai-mode</c> — "" | never | always (contacts.manage to write).
/// * <c>POST /api/inbox/threads/bulk</c> — ai_on | ai_off | resolve | reopen | assign
///   over picked conversations or everything a list filter matches (chats.manage,
///   scoped like the list, at most 1000, one audit row).
/// * <c>POST /api/inbox/threads/{channel}/{key}/handover</c> — take | continue | flag (chats.send).
/// * <c>GET /api/ai/handovers/stats?days=&amp;entity=</c> — handovers over time, by kind, the rate.
/// </summary>
public static class AiControlsEndpoints
{
    public const int BulkMax = 1000;

    private static readonly string[] BulkActions = { "ai_on", "ai_off", "resolve", "reopen", "assign" };

    public static void MapAiControlsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- master switch + schedule + never-tags --------------------------------
        app.MapGet("/api/ai/controls", async (string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var (entityId, err) = await EntityArg(db, entity);
            if (err != null) return err;
            return Results.Ok(await ControlsPayload(db, sc, me, entityId));
        }).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireAnyCapability("ai.monitor", AiMonitorEndpoints.BehaviourCap, "chats.view");

        app.MapPut("/api/ai/controls", async (string? entity, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var (entityId, err) = await EntityArg(db, entity);
            if (err != null) return err;
            if (!MayEdit(db, sc, me, entityId))
                return Results.Json(new
                {
                    detail = entityId == null
                        ? "Only an admin can change the organization-wide AI switch."
                        : "That outlet is outside your scope.",
                }, statusCode: 403);
            JsonElement body;
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { return Bad("Send a JSON object."); }
            if (body.ValueKind != JsonValueKind.Object) return Bad("Send a JSON object.");

            var writes = new List<(string Key, string? Value)>();
            if (body.TryGetProperty("mode", out var modeEl))
            {
                var raw = modeEl.ValueKind == JsonValueKind.String ? (modeEl.GetString() ?? "").Trim().ToLowerInvariant() : "";
                if (modeEl.ValueKind == JsonValueKind.Null || raw is "" or "inherit")
                    writes.Add((AiControlKeys.Master, null));
                else if (AiMasterModes.All.Contains(raw)) writes.Add((AiControlKeys.Master, raw));
                else return Bad("mode must be on, off, schedule or inherit.");
            }
            if (body.TryGetProperty("schedule", out var schedEl))
            {
                if (schedEl.ValueKind == JsonValueKind.Null) writes.Add((AiControlKeys.Schedule, null));
                else if (schedEl.ValueKind != JsonValueKind.Object)
                    return Bad("schedule must be an object like {\"mon\": [[\"18:00\", \"23:59\"]]}.");
                else
                {
                    var parsed = AiSchedule.From(schedEl);
                    writes.Add((AiControlKeys.Schedule, parsed.IsEmpty ? null : parsed.ToJson()));
                }
            }
            if (body.TryGetProperty("schedule_mode", out var smEl))
            {
                var raw = smEl.ValueKind == JsonValueKind.String ? (smEl.GetString() ?? "").Trim().ToLowerInvariant() : "";
                if (smEl.ValueKind == JsonValueKind.Null || raw is "" or "inherit") writes.Add((AiControlKeys.ScheduleMode, null));
                else if (raw is AiScheduleModes.Inside or AiScheduleModes.Outside) writes.Add((AiControlKeys.ScheduleMode, raw));
                else return Bad("schedule_mode must be inside or outside.");
            }
            if (body.TryGetProperty("never_tags", out var tagsEl))
            {
                var tags = tagsEl.ValueKind switch
                {
                    JsonValueKind.Array => tagsEl.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList(),
                    JsonValueKind.String => AiControls.SplitTags(tagsEl.GetString()),
                    _ => new List<string>(),
                };
                var clean = AiControls.SplitTags(string.Join(",", tags));
                if (clean.Count > 50) return Bad("At most 50 never-AI tags.");
                writes.Add((AiControlKeys.NeverTags, clean.Count == 0 ? null : string.Join(", ", clean)));
            }
            if (writes.Count == 0) return Bad("Nothing to change — send mode, schedule, schedule_mode or never_tags.");

            // "schedule" needs at least one window once everything above is applied
            // (an inheriting outlet takes its parent's switch, already validated there)
            var masterWrite = writes.FirstOrDefault(w => w.Key == AiControlKeys.Master);
            var effectiveMode = masterWrite.Key != null
                ? masterWrite.Value ?? ""
                : new AiControls(db, sc) { AlwaysLoadSchedule = true }.Master(entityId).Mode;
            if (effectiveMode == AiMasterModes.Schedule)
            {
                var schedWrite = writes.FirstOrDefault(w => w.Key == AiControlKeys.Schedule);
                var schedule = schedWrite.Key != null
                    ? AiSchedule.Parse(schedWrite.Value)
                    : new AiControls(db, sc) { AlwaysLoadSchedule = true }.Master(entityId).Schedule;
                if (schedule.IsEmpty)
                    return Bad("Add at least one time window before switching the AI to a schedule.");
            }

            foreach (var (key, value) in writes)
            {
                if (value == null)
                {
                    if (entityId is int eid) sc.ClearSetting(key, eid);
                    else sc.SetSetting(key, "", null);
                }
                else sc.SetSetting(key, value, entityId);
            }
            var scopeLabel = entityId == null ? "organization" : $"entity:{entity}";
            AuditTrail.Write(db, "ai.controls.update", scopeLabel,
                AuditTrail.Cap(string.Join(", ", writes.Select(w => $"{w.Key}={w.Value ?? "(inherit)"}")), 900),
                actor: me.Identity?.Name);
            BumpAllThreads(db); // every open chat re-reads its AI state
            return Results.Ok(await ControlsPayload(db, sc, me, entityId));
        }).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireCapability(AiMonitorEndpoints.BehaviourCap);

        // ---- per-contact AI mode ------------------------------------------------------
        app.MapGet("/api/contacts/{id:int}/ai-mode", async (int id, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var c = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                .FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return Results.NotFound(new { detail = "Contact not found." });
            return Results.Ok(await ContactModePayload(db, sc, me, c));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.view");

        app.MapPut("/api/contacts/{id:int}/ai-mode", async (int id, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var c = await ContactsEndpoints.ScopeContacts(db.Contacts, me, db, sc).FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return Results.NotFound(new { detail = "Contact not found." });
            JsonElement body;
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { return Bad("Send {\"mode\": \"\" | \"never\" | \"always\"}."); }
            var raw = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("mode", out var m)
                ? (m.ValueKind == JsonValueKind.String ? m.GetString() : m.ValueKind == JsonValueKind.Null ? "" : null)
                : null;
            if (ContactAiModes.Parse(raw) is not string mode)
                return Bad("mode must be \"\" (follow the chat), \"never\" or \"always\".");
            var before = c.AiMode ?? "";
            c.AiMode = mode;
            c.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            // the contact's conversations re-read their AI state on the stream
            await db.Threads.Where(t => t.ContactId == c.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, DateTime.UtcNow));
            AuditTrail.Write(db, "contact.ai_mode", $"contact:{c.Id}",
                $"{(before.Length > 0 ? before : "follow")} → {(mode.Length > 0 ? mode : "follow")}",
                actor: me.Identity?.Name);
            return Results.Ok(await ContactModePayload(db, sc, me, c));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("contacts.manage");

        // ---- bulk actions over the inbox -----------------------------------------------
        app.MapPost("/api/inbox/threads/bulk", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            JsonElement body;
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { return Bad("Send {threads: [\"channel:key\"…] | filter: {…}, action}."); }
            if (body.ValueKind != JsonValueKind.Object) return Bad("Send a JSON object.");
            var action = body.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                ? (a.GetString() ?? "").Trim().ToLowerInvariant() : "";
            if (!BulkActions.Contains(action))
                return Bad($"action must be one of {string.Join(", ", BulkActions)}.");
            var value = body.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "").Trim()
                : body.TryGetProperty("assignee", out var asg) && asg.ValueKind == JsonValueKind.String
                    ? (asg.GetString() ?? "").Trim() : "";
            if (value.Length > 64) value = value[..64];

            List<int> ids;
            var skipped = new List<string>();
            if (body.TryGetProperty("threads", out var refsEl) && refsEl.ValueKind == JsonValueKind.Array)
            {
                var refs = refsEl.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "")
                    .Where(x => x.Contains(':')).Distinct().ToList();
                if (refs.Count == 0) return Bad("threads must list \"channel:key\" references.");
                if (refs.Count > BulkMax) return TooMany(refs.Count);
                var visible = await InboxThreadsEndpoints.VisibleEntityIdsAsync(db, sc, me);
                ids = new List<int>();
                foreach (var r in refs)
                {
                    var i = r.IndexOf(':');
                    var ch = r[..i].Trim().ToLowerInvariant();
                    var key = r[(i + 1)..].Trim();
                    var row = await db.Threads.AsNoTracking().Where(t => t.Channel == ch && t.Key == key)
                        .Select(t => new { t.Id, t.EntityId }).FirstOrDefaultAsync();
                    if (row == null || (visible != null && row.EntityId is int e && !visible.Contains(e)))
                        skipped.Add(r);
                    else ids.Add(row.Id);
                }
            }
            else if (body.TryGetProperty("filter", out var filterEl) && filterEl.ValueKind == JsonValueKind.Object)
            {
                ids = await InboxThreadsEndpoints.MatchingThreadIdsAsync(db, sc, me, ToQuery(filterEl));
                if (ids.Count > BulkMax) return TooMany(ids.Count);
            }
            else return Bad("Pick the conversations: threads: [\"channel:key\"…] or filter: {…the list's query…}.");

            var actor = me.Identity?.Name ?? "unknown";
            var changed = 0;
            foreach (var chunk in ids.Chunk(200))
            {
                var rows = await db.Threads.Where(t => chunk.Contains(t.Id)).ToListAsync();
                foreach (var t in rows)
                {
                    var before = (t.Status, t.Assignee, t.AiEnabled, Handover.IsOpen(t));
                    switch (action)
                    {
                        case "ai_on":
                            AiEligibility.Set(t, true);
                            Handover.Clear(db, t, Handover.Agent(actor), "The AI was switched back on.", save: false);
                            break;
                        case "ai_off":
                            AiEligibility.Set(t, false);
                            break;
                        case "resolve":
                            t.Status = ThreadStatus.Resolved;
                            Handover.Clear(db, t, Handover.Agent(actor), "The conversation was resolved.", save: false);
                            break;
                        case "reopen":
                            t.Status = ThreadStatus.Open;
                            break;
                        case "assign":
                            t.Assignee = value;
                            break;
                    }
                    t.UpdatedAt = DateTime.UtcNow;
                    if (before != (t.Status, t.Assignee, t.AiEnabled, Handover.IsOpen(t)) || action is "ai_on" or "ai_off")
                        changed++;
                }
                await db.SaveChangesAsync();
            }
            AuditTrail.Write(db, "inbox.bulk", $"{action} ×{ids.Count}",
                $"{ids.Count} conversation{(ids.Count == 1 ? "" : "s")}"
                + (action == "assign" ? $" → {(value.Length > 0 ? value : "(unassigned)")}" : "")
                + (skipped.Count > 0 ? $" · {skipped.Count} skipped" : ""),
                actor: actor);
            return Results.Ok(new { ok = true, action, matched = ids.Count, changed, skipped });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.manage");

        // ---- one conversation's handover: take it / let the AI continue / flag -----------
        app.MapPost("/api/inbox/threads/{channel}/{key}/handover", async (string channel, string key,
            HttpContext ctx, AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var visible = await InboxThreadsEndpoints.VisibleEntityIdsAsync(db, sc, me);
            var t = await db.Threads.Include(x => x.Contact).Include(x => x.Account)
                .FirstOrDefaultAsync(x => x.Channel == channel && x.Key == key);
            if (t == null || (visible != null && t.EntityId is int e && !visible.Contains(e)))
                return Results.NotFound(new { detail = "Conversation not found." });
            JsonElement body = default;
            try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
            catch { /* empty body */ }
            string Prop(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var p)
                && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
            var action = Prop("action").ToLowerInvariant();
            var me_ = me.Identity?.Name ?? "";
            switch (action)
            {
                case "take":
                    t.Assignee = me_.Length > 64 ? me_[..64] : me_;
                    if (t.Status == ThreadStatus.Pending) t.Status = ThreadStatus.Open;
                    t.UpdatedAt = DateTime.UtcNow;
                    AuditTrail.Add(db, "thread.handover.take", $"{t.Channel}:{t.Key}", t.HandoverReason, actor: me_);
                    await db.SaveChangesAsync();
                    break;
                case "continue":
                    Handover.Clear(db, t, Handover.Agent(me_), "A person let the AI continue.", save: false);
                    AiEligibility.Set(t, true);
                    if (t.Status == ThreadStatus.Pending) t.Status = ThreadStatus.Open;
                    AuditTrail.Add(db, "thread.handover.continue", $"{t.Channel}:{t.Key}", "chatbot on", actor: me_);
                    await db.SaveChangesAsync();
                    break;
                case "flag":
                    var reason = Prop("reason");
                    Handover.Mark(db, t, HandoverKinds.Manual, reason.Length > 0 ? reason : "Flagged for a person.",
                        Handover.Agent(me_), standDown: !(body.ValueKind == JsonValueKind.Object
                            && body.TryGetProperty("stand_down", out var sd) && sd.ValueKind == JsonValueKind.False));
                    break;
                default:
                    return Bad("action must be take, continue or flag.");
            }
            var verdict = AiEligibility.Decide(db, sc, t);
            return Results.Ok(new
            {
                ok = true,
                action,
                status = t.Status,
                assignee = t.Assignee,
                ai_enabled = verdict.Enabled,
                ai_reason = verdict.Reason,
                ai_rule = verdict.Rule,
                ai_forced = verdict.Forced,
                handover = HandoverPayload(t),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireThreadAccess("chats.send");

        // ---- handover stats --------------------------------------------------------------
        app.MapGet("/api/ai/handovers/stats", async (int? days, string? entity, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me) =>
        {
            var span = Math.Clamp(days ?? 30, 1, 365);
            var zone = OrgClock.Zone(sc);
            var since = OrgClock.TodayStartUtc(zone).AddDays(-(span - 1));
            var entityId = await InboxEndpoints.EntityId(db, entity);
            var ids = ShellScope.ThreadEntityIds(db, sc, me, entityId);

            var events = await db.AuditLogs.AsNoTracking()
                .Where(x => x.Action == Handover.AuditAction && x.CreatedAt >= since)
                .Where(x => ids == null || x.EntityId == null || ids.Contains(x.EntityId.Value))
                .Select(x => new { x.CreatedAt, x.Detail, x.Actor, x.Target })
                .Take(50000).ToListAsync();
            var drafts = (await db.AiDrafts.AsNoTracking()
                .Where(d => d.CreatedAt >= since && d.ThreadId != null)
                .Where(d => ids == null || d.EntityId == null || ids.Contains(d.EntityId.Value))
                .Select(d => new { d.Channel, d.ThreadKey, d.CreatedAt })
                .Take(100000).ToListAsync())
                .Select(d => new { Target = $"{d.Channel}:{d.ThreadKey}", d.CreatedAt }).ToList();
            var open = await ShellScope.ScopeThreads(db.Threads.AsNoTracking(), ids)
                .CountAsync(t => t.HandoverAt != null && t.HandoverResolvedAt == null);
            var answered = await ShellScope.ScopeThreads(db.Threads.AsNoTracking(), ids)
                .Where(t => t.HandoverAt != null && t.HandoverResolvedAt != null && t.HandoverResolvedAt >= since)
                .Select(t => new { t.HandoverAt, t.HandoverResolvedAt }).ToListAsync();

            string KindOf(string detail)
            {
                var i = detail.IndexOf(':');
                var k = i > 0 ? detail[..i] : "";
                return HandoverKinds.All.Contains(k) ? k : HandoverKinds.Manual;
            }
            string ByOf(string actor) =>
                actor == "ai" ? "ai" : actor == "flow" ? "flow" : actor.StartsWith("rule:") ? "rule"
                : actor.StartsWith("agent:") ? "agent" : "other";

            var dayKeys = OrgClock.LastDays(span, zone);
            var byDay = events.GroupBy(x => OrgClock.Day(x.CreatedAt, zone)).ToDictionary(g => g.Key, g => g.Count());
            // the rate: conversations handed over ÷ conversations the AI worked on or
            // that were handed over (so a rule's or a person's handover counts too, ≤ 100%)
            HashSet<string> Handed(IEnumerable<string> t) => t.ToHashSet();
            var handedByDay = events.GroupBy(x => OrgClock.Day(x.CreatedAt, zone))
                .ToDictionary(g => g.Key, g => Handed(g.Select(x => x.Target)));
            var aiByDay = drafts.GroupBy(x => OrgClock.Day(x.CreatedAt, zone))
                .ToDictionary(g => g.Key, g => g.Select(x => x.Target).ToHashSet());
            var handedAll = Handed(events.Select(x => x.Target));
            var aiAll = drafts.Select(d => d.Target).ToHashSet();
            var aiConversations = aiAll.Count;
            var conversations = aiAll.Union(handedAll).Count();
            double? Rate(int n, int d) => d == 0 ? null : Math.Round((double)n / d, 4);
            int Union(string day) => (aiByDay.GetValueOrDefault(day) ?? new HashSet<string>())
                .Union(handedByDay.GetValueOrDefault(day) ?? new HashSet<string>()).Count();
            var minutes = answered
                .Select(x => (x.HandoverResolvedAt!.Value - x.HandoverAt!.Value).TotalMinutes)
                .Where(m => m >= 0).ToList();

            return Results.Ok(new
            {
                days = span,
                since = DateTime.SpecifyKind(since, DateTimeKind.Utc),
                total = events.Count,
                open,
                ai_conversations = aiConversations,
                handed_over_conversations = handedAll.Count,
                conversations,
                rate = Rate(handedAll.Count, conversations),
                avg_minutes_to_human = minutes.Count == 0 ? (double?)null : Math.Round(minutes.Average(), 1),
                by_kind = HandoverKinds.All
                    .Select(k => new { kind = k, label = HandoverKinds.Label(k), count = events.Count(x => KindOf(x.Detail) == k) })
                    .Where(x => x.count > 0).OrderByDescending(x => x.count).ToList(),
                by_source = events.GroupBy(x => ByOf(x.Actor))
                    .Select(g => new { by = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList(),
                series = dayKeys.Select(d => new
                {
                    day = d,
                    handovers = byDay.GetValueOrDefault(d),
                    ai_conversations = aiByDay.GetValueOrDefault(d)?.Count ?? 0,
                    rate = Rate(handedByDay.GetValueOrDefault(d)?.Count ?? 0, Union(d)),
                }).ToList(),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability("ai.monitor", "chats.view");
    }

    // ---- payload helpers (shared with the thread payloads) ---------------------------------

    /// <summary>
    /// A person's reply reached the customer: an open handover is answered.
    /// API keys and MCP clients are automation, not a person; a failed send
    /// answers nothing. Never throws.
    /// </summary>
    internal static void HumanReplied(AppDbContext db, Thread thread, Message? sent, ClaimsPrincipal me)
    {
        try
        {
            if (sent == null || sent.Private || sent.Status == "failed") return;
            if (!MessagingTransportEndpoints.IsHumanSender(me)) return;
            Handover.OnHumanReply(db, thread, me.Identity?.Name);
        }
        catch { /* never fails the send that already happened */ }
    }

    /// <summary>A conversation moved to resolved: an open handover is closed (the caller saves).</summary>
    internal static void StatusChanged(AppDbContext db, Thread thread, string status, ClaimsPrincipal me)
    {
        if (status == ThreadStatus.Resolved)
            Handover.Clear(db, thread, Handover.Agent(me.Identity?.Name), "The conversation was resolved.", save: false);
    }

    /// <summary>The <c>handover</c> object on a thread payload — null when it was never handed over.</summary>
    public static Dictionary<string, object?>? HandoverPayload(Thread t)
    {
        if (t.HandoverAt is not DateTime at) return null;
        return new Dictionary<string, object?>
        {
            ["open"] = Handover.IsOpen(t),
            ["at"] = DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("o"),
            ["reason"] = t.HandoverReason,
            ["kind"] = t.HandoverKind,
            ["kind_label"] = HandoverKinds.Label(t.HandoverKind),
            ["by"] = t.HandoverBy,
            ["resolved_at"] = t.HandoverResolvedAt is DateTime r
                ? DateTime.SpecifyKind(r, DateTimeKind.Utc).ToString("o") : null,
            ["count"] = t.HandoverCount,
        };
    }

    /// <summary>ai_enabled + ai_reason + ai_rule + ai_forced + handover, for a dictionary payload.</summary>
    public static void AddAiFields(Dictionary<string, object?> payload, AppDbContext db, ScopingService sc, Thread t)
    {
        var v = AiEligibility.Decide(db, sc, t);
        payload["ai_enabled"] = v.Enabled;
        payload["ai_reason"] = v.Reason;
        payload["ai_rule"] = v.Rule;
        payload["ai_forced"] = v.Forced;
        payload["handover"] = HandoverPayload(t);
    }

    private static async Task<object> ControlsPayload(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        int? entityId)
    {
        var ai = new AiControls(db, sc) { AlwaysLoadSchedule = true };
        var master = ai.Master(entityId);
        var own = await db.Settings.AsNoTracking()
            .Where(s => AiControlKeys.All.Contains(s.Key) && s.EntityId == entityId)
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        string Own(string k) => own.TryGetValue(k, out var v) ? (v ?? "").Trim() : "";
        var ownTags = AiControls.SplitTags(Own(AiControlKeys.NeverTags));
        var allTags = ai.NeverTags(entityId);
        var slug = entityId is int eid
            ? await db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => e.Slug).FirstOrDefaultAsync()
            : null;
        var nowOn = master.AllowsAt(ai.NowUtc, ai.Zone);
        var scopeName = ai.EntityName(entityId);
        return new
        {
            entity = slug,
            entity_name = scopeName,
            mode = master.Mode,
            mode_from = master.ModeFrom,
            mode_set_at = master.ModeFrom == "default" ? null : master.ScopeName,
            own = new
            {
                mode = Own(AiControlKeys.Master) is { Length: > 0 } m ? AiMasterModes.Normalize(m) : "",
                schedule = Own(AiControlKeys.Schedule) is { Length: > 0 } s ? AiSchedule.Parse(s).ToWire() : null,
                schedule_mode = Own(AiControlKeys.ScheduleMode) is { Length: > 0 } sm ? AiScheduleModes.Normalize(sm) : "",
                never_tags = ownTags,
            },
            schedule = master.Schedule.ToWire(),
            schedule_mode = master.ScheduleMode,
            never_tags = allTags,
            inherited_never_tags = allTags.Where(x => !ownTags.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList(),
            now_on = nowOn,
            next_change_at = master.NextChangeUtc(ai.NowUtc, ai.Zone) is DateTime nc
                ? DateTime.SpecifyKind(nc, DateTimeKind.Utc).ToString("o") : null,
            timezone = ai.Zone.Id,
            label = $"AI is {(nowOn ? "ON" : "OFF")} for {scopeName}"
                    + (master.Mode == AiMasterModes.Schedule ? " (on a schedule)" : ""),
            can_manage = await ShellScope.HasCapAsync(db, me, AiMonitorEndpoints.BehaviourCap)
                         && MayEdit(db, sc, me, entityId),
        };
    }

    private static async Task<object> ContactModePayload(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        Contact c)
    {
        var entityId = await db.Threads.AsNoTracking().Where(t => t.ContactId == c.Id && t.EntityId != null)
            .OrderByDescending(t => t.UpdatedAt).Select(t => t.EntityId).FirstOrDefaultAsync();
        var ai = new AiControls(db, sc);
        var hit = AiControls.NeverTagHit(c.Tags, ai.NeverTags(entityId));
        return new
        {
            contact_id = c.Id,
            mode = ContactAiModes.Normalize(c.AiMode),
            never_tag = hit,
            effective = hit != null ? "never" : ContactAiModes.Normalize(c.AiMode) is { Length: > 0 } m ? m : "follow",
            can_manage = await ShellScope.HasCapAsync(db, me, "contacts.manage"),
        };
    }

    // ---- small helpers ------------------------------------------------------------------

    private static async Task<(int? Id, IResult? Error)> EntityArg(AppDbContext db, string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "all" or "__all__") return (null, null);
        var id = await InboxEndpoints.EntityId(db, slug);
        return id is int i ? (i, null) : (null, Results.NotFound(new { detail = $"Unknown entity '{slug}'." }));
    }

    /// <summary>Org-wide: admins; an outlet: inside the caller's assigned subtree (admins: any).</summary>
    private static bool MayEdit(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? entityId)
    {
        var role = ShellScope.RoleOf(me);
        if (role is Roles.SuperAdmin or Roles.Admin) return true;
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        if (entityId == null) return false;          // org-wide is an admin decision
        if (assigned.Count == 0) return true;        // unassigned: the tenant-wide view
        return sc.EntitySubtreeIds(assigned).Contains(entityId.Value);
    }

    /// <summary>The list filter object → the query the list endpoint parses (arrays become comma lists).</summary>
    private static IQueryCollection ToQuery(JsonElement filter)
    {
        var d = new Dictionary<string, StringValues>();
        foreach (var p in filter.EnumerateObject())
        {
            var v = p.Value.ValueKind switch
            {
                JsonValueKind.Array => string.Join(",", p.Value.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())),
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => p.Value.ToString(),
            };
            if (p.Name is "limit" or "cursor" or "count_only" or "before") continue;
            if (v.Length > 0) d[p.Name] = v;
        }
        return new QueryCollection(d);
    }

    private static void BumpAllThreads(AppDbContext db)
    {
        try
        {
            // one row is enough to move the SSE change token (max updated_at)
            var newest = db.Threads.OrderByDescending(t => t.UpdatedAt).Select(t => t.Id).FirstOrDefault();
            if (newest > 0)
                db.Threads.Where(t => t.Id == newest)
                    .ExecuteUpdate(s => s.SetProperty(t => t.UpdatedAt, DateTime.UtcNow));
        }
        catch { /* the stream refreshes on its next change */ }
    }

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);

    private static IResult TooMany(int n) => Results.Json(new
    {
        detail = $"That is {n} conversations — bulk actions take at most {BulkMax}. Narrow the filter first.",
        matched = n,
    }, statusCode: 400);
}
