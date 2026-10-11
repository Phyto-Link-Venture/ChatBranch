using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Workers;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Data.Ai.Review;
using ChatBranch.Data.Audit;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The on-demand AI reviewer ("AI to monitor AI", the AI monitor's "AI review" tab).
///
/// * <c>POST /api/ai/review</c> {from?, to?, entity?, limit?} — <c>ai.behaviour.manage</c>.
///   Starts a run in the background and waits up to ~20 s for it; the answer is the
///   run (status <c>done</c>, or <c>running</c> — poll <c>GET …/runs/{id}</c>).
///   One run at a time (409 while another is running).
/// * <c>GET /api/ai/review/runs</c> — the last runs the caller may see + the reviewer
///   model that would run now; <c>GET /api/ai/review/runs/{id}</c> — a run + its findings.
///   <c>ai.monitor</c> or <c>ai.behaviour.manage</c>.
/// * <c>POST /api/ai/review/findings/{id}/apply</c> {as: lesson | knowledge | house_rule, text?}
///   — <c>ai.behaviour.manage</c> (knowledge and house rules also <c>settings.ai.manage</c>).
///
/// Scope mirrors the AI monitor's drafts: admins see everything (narrowed by
/// <c>?entity=</c>), agents and custom roles their assigned entities' subtree —
/// a run is visible only when every entity it read sits in the caller's scope,
/// and its findings are filtered again by the conversation's entity.
/// </summary>
public static class AiReviewEndpoints
{
    private const string MonitorCap = AiMonitorEndpoints.MonitorCap;
    /// <summary>One review at a time, process-wide: the "is one running?" check and
    /// the insert of the new run happen under this lock, so two parallel POSTs can
    /// never both start.</summary>
    private static readonly SemaphoreSlim StartLock = new(1, 1);
    private const string BehaviourCap = AiMonitorEndpoints.BehaviourCap;

    public static void MapAiReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/review", async (HttpContext ctx, AppDbContext db, ScopingService sc, ClaimsPrincipal me,
            IServiceScopeFactory scopes, AiReviewService review) =>
        {
            var body = await ReadJson(ctx);
            var now = DateTime.UtcNow;
            DateTime? Date(string key)
            {
                var raw = Str(body, key);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                // a date far outside the product's life (9999-12-31) is refused, not overflowed
                return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                    && d.Year is >= 2000 and <= 2200 ? d : DateTime.MinValue;
            }
            var to = Date("to");
            var from = Date("from");
            if (to == DateTime.MinValue || from == DateTime.MinValue)
                return Results.BadRequest(new { detail = "from and to must be dates (YYYY-MM-DD or ISO 8601) between 2000 and 2200." });
            // a bare date "to" means the end of that day
            var toAt = to is DateTime t0 ? (t0.TimeOfDay == TimeSpan.Zero ? t0.AddDays(1).AddSeconds(-1) : t0) : now;
            if (toAt > now) toAt = now;
            var days = Int(body, "days");
            var fromAt = from ?? toAt.AddDays(-(days is int d && d > 0 ? Math.Min(d, 366) : 7));
            if (fromAt >= toAt) return Results.BadRequest(new { detail = "from must be before to." });
            if ((toAt - fromAt).TotalDays > 366) return Results.BadRequest(new { detail = "Review at most a year at a time." });
            var limit = Int(body, "limit") ?? AiReviewService.DefaultLimit;
            if (limit < 1 || limit > AiReviewService.MaxLimit)
                return Results.BadRequest(new { detail = $"limit must be between 1 and {AiReviewService.MaxLimit}." });

            // optional reviewer for THIS run only ("provider:model" or a bare OpenRouter id);
            // it must name a configured provider — never a silent fallback to another vendor
            string? model = null;
            if (Str(body, "model") is { } rawModel && rawModel.Trim().Length > 0)
            {
                var (canonical, modelError) = review.CheckModel(rawModel);
                if (modelError != null) return Results.BadRequest(new { detail = modelError });
                model = canonical;
            }

            var (entityId, entityIds, err) = await RunScope(db, sc, me, Str(body, "entity"));
            if (err != null) return err;
            // AI usage quotas: the reviewer's calls are AI spend — the caller's own
            // cap, then the run's outlet (or any level above it)
            if (QuotaGuard.Refusal(db, sc, me) is { } userQuota) return userQuota;
            if (AiQuota.EntityBlock(db, sc, entityId) is { } entityQuota)
                return Results.Json(new { detail = AiQuota.Reason(entityQuota), quota = true },
                    statusCode: StatusCodes.Status429TooManyRequests);

            AiReviewRun run;
            await StartLock.WaitAsync();
            try
            {
                review.ExpireStale();
                var busy = await db.AiReviewRuns.AsNoTracking().Where(r => r.Status == AiReviewStatus.Running)
                    .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
                if (busy != null)
                    return Results.Json(new { detail = "A review is already running — wait for it to finish.", run_id = busy.Id },
                        statusCode: 409);

                run = review.Start(new AiReviewRequest
                {
                    From = fromAt, To = toAt, EntityId = entityId, EntityIds = entityIds, Limit = limit,
                    CreatedBy = me.Identity?.Name ?? "", Model = model,
                });
            }
            finally { StartLock.Release(); }
            AuditTrail.Write(db, "ai.review.run", $"ai_review:{run.Id}",
                $"Reviewing up to {run.Limit} AI replies from {fromAt:yyyy-MM-dd} to {toAt:yyyy-MM-dd} ({run.Mode}"
                + (run.Model.Length > 0 ? $", {run.Provider}:{run.Model}" : "") + ")",
                entityId: entityId);

            // the work runs in its own DI scope, outside the request's ambient context
            Task task;
            using (ExecutionContext.SuppressFlow())
                task = Task.Run(() =>
                {
                    using var scope = scopes.CreateScope();
                    scope.ServiceProvider.GetRequiredService<AiReviewService>().Execute(run.Id);
                });
            var waitSeconds = Math.Clamp(Int(body, "wait_seconds") ?? 20, 0, 25);
            if (waitSeconds > 0) await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(waitSeconds)));

            db.ChangeTracker.Clear();
            var fresh = await db.AiReviewRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);
            var scopeIds = (await AiMonitorEndpoints.ScopeFor(db, sc, me, null)).EntityIds;
            return Results.Ok(new { run = await RunDetail(db, fresh, scopeIds, null) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(BehaviourCap);

        app.MapGet("/api/ai/review/runs", async (string? entity, int? limit, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, AiReviewService review) =>
        {
            review.ExpireStale();
            var scope = await AiMonitorEndpoints.ScopeFor(db, sc, me, null);
            var picked = await InboxEndpoints.EntityId(db, entity);
            var rows = await db.AiReviewRuns.AsNoTracking().OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                .Take(AiReviewService.KeepRuns).ToListAsync();
            var visible = rows.Where(r => Visible(r, scope.EntityIds))
                .Where(r => picked == null || r.EntityId == picked || r.EntityIds.Contains(picked.Value))
                .Take(Math.Clamp(limit ?? 20, 1, AiReviewService.KeepRuns)).ToList();
            var reviewer = review.Reviewer();
            var (cfgProvider, cfgModel) = review.ConfiguredModel();
            return Results.Ok(new
            {
                runs = visible.Select(r => RunRow(r)).ToList(),
                reviewer = new
                {
                    mode = reviewer.Available ? AiReviewMode.Model : AiReviewMode.Heuristic,
                    provider = reviewer.Provider,
                    model = reviewer.Model,
                    configured_provider = cfgProvider,
                    configured_model = cfgModel,
                    note = reviewer.Note,
                    default_limit = AiReviewService.DefaultLimit,
                    max_limit = AiReviewService.MaxLimit,
                    batch_size = AiReviewService.BatchSize,
                },
                running = rows.Any(r => r.Status == AiReviewStatus.Running),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability(MonitorCap, BehaviourCap);

        // the reviewer picker: configured providers (never keys) with suggested
        // reviewer models, the current default and whether the caller may change it
        app.MapGet("/api/ai/review/options", async (AppDbContext db, ClaimsPrincipal me, ILlmClient llm,
            AiReviewService review) =>
        {
            var configured = AiProviderChoices.Configured(llm);
            var (raw, source) = review.ConfiguredSource();
            var (defProvider, defModel) = AiReviewService.ParseModel(raw);
            var ids = OpenAiLlmClient.DefaultOrder
                .Where(p => configured.Any(c => c.Id == p) || AiReviewService.SuggestedModels.ContainsKey(p)
                            || p == defProvider)
                .ToList();
            var providers = ids.Select(p =>
            {
                var choice = configured.FirstOrDefault(c => c.Id == p);
                var models = AiReviewService.SuggestedModels.TryGetValue(p, out var sm)
                    ? sm.ToList()
                    : choice != null ? new List<string> { choice.DefaultModel } : new List<string>();
                // the current default is always pickable, even when it is not a suggestion
                if (p == defProvider && defModel.Length > 0 && !models.Contains(defModel)) models.Insert(0, defModel);
                return new
                {
                    id = p,
                    label = AiProviderChoices.Label(p),
                    configured = choice != null,
                    models = models.Select(m => new
                    {
                        value = AiReviewService.Canonical(p, m),
                        model = m,
                        is_default = p == defProvider && m == defModel,
                    }).ToList(),
                };
            }).ToList();
            var reviewer = review.Reviewer();
            return Results.Ok(new
            {
                providers,
                @default = new
                {
                    value = AiReviewService.Canonical(defProvider, defModel),
                    raw,
                    provider = defProvider,
                    label = AiProviderChoices.Label(defProvider),
                    model = defModel,
                    source,
                    configured = configured.Any(c => c.Id == defProvider),
                },
                // what "the default" runs on right now (a fallback is named in the note)
                reviewer = new
                {
                    mode = reviewer.Available ? AiReviewMode.Model : AiReviewMode.Heuristic,
                    provider = reviewer.Provider,
                    model = reviewer.Model,
                    note = reviewer.Note,
                },
                can_set_default = await ShellScope.HasCapAsync(db, me, "settings.ai_keys.manage"),
                setting_key = AiReviewService.ModelKey,
                max_chars = AiReviewService.MaxModelChars,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(BehaviourCap);

        app.MapGet("/api/ai/review/runs/{id:int}", async (int id, string? verdict, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            var scope = await AiMonitorEndpoints.ScopeFor(db, sc, me, null);
            var run = await db.AiReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
            if (run == null || !Visible(run, scope.EntityIds)) return Results.NotFound(new { detail = "Review not found." });
            return Results.Ok(new { run = await RunDetail(db, run, scope.EntityIds, verdict) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability(MonitorCap, BehaviourCap);

        app.MapPost("/api/ai/review/findings/{id:int}/apply", async (int id, HttpContext ctx, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, LearningService learning) =>
        {
            var body = await ReadJson(ctx);
            var scope = await AiMonitorEndpoints.ScopeFor(db, sc, me, null);
            var finding = await db.AiReviewFindings.FirstOrDefaultAsync(f => f.Id == id);
            var run = finding == null ? null : await db.AiReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == finding.RunId);
            if (finding == null || run == null || !Visible(run, scope.EntityIds) || !FindingVisible(finding, scope.EntityIds))
                return Results.NotFound(new { detail = "Finding not found." });
            if (finding.Applied)
                return Results.Json(new { detail = $"Already added as a {finding.AppliedAs.Replace('_', ' ')}." }, statusCode: 409);
            var asWhat = (Str(body, "as") ?? "").Trim().ToLowerInvariant();
            // the edited suggestion when one is sent (blank = the reviewer's own text)
            if (asWhat is not ("lesson" or "knowledge" or "house_rule"))
                return Results.BadRequest(new { detail = "as must be lesson, knowledge or house_rule." });
            var text = (Str(body, "text") is { } edited && edited.Trim().Length > 0 ? edited : finding.FixText).Trim();
            if (text.Length == 0) return Results.BadRequest(new { detail = "There is no suggestion to add — write the text." });
            if (text.Length > 2000) return Results.BadRequest(new { detail = "Keep it under 2000 characters." });
            // optional `level`: the entity slug the lesson / knowledge / house rule
            // belongs to ("" = organization-wide). Absent = the conversation's entity.
            // The dashboard's confirm dialog always sends the level the person picked.
            var hasLevel = body is JsonElement bj && bj.ValueKind == JsonValueKind.Object
                           && bj.TryGetProperty("level", out var lv0)
                           && lv0.ValueKind is JsonValueKind.String or JsonValueKind.Null;
            int? entityLevel = finding.EntityId;
            if (hasLevel)
            {
                entityLevel = null;
                var slug = (Str(body, "level") ?? "").Trim();
                if (slug.Length > 0 && slug is not ("organization" or "global"))
                {
                    // only an entity the caller may see (their tenant / assigned subtree)
                    entityLevel = (await AdminEndpoints.SwitcherEntitiesAsync(db, me))
                        .Where(e => e.Slug == slug).Select(e => (int?)e.Id).FirstOrDefault();
                    if (entityLevel == null) return Results.NotFound(new { detail = $"Unknown entity '{slug}'." });
                }
            }
            var actor = learning.ActorFor(me.Identity?.Name);
            var role = me.FindFirstValue(ClaimTypes.Role) ?? "";
            var now = DateTime.UtcNow;
            var note = $"From AI review #{run.Id} (finding #{finding.Id}).";
            string appliedRef;
            object result;

            // ---- permission checks first, then an ATOMIC claim (… WHERE applied = 0),
            // so parallel applies of one finding can never all succeed
            var mayManageAi = await AdminEndpoints.HasCapabilityAsync(db, role, "settings.ai.manage");
            // a house rule goes to the CONVERSATION's level (its outlet) unless the
            // person picked another, never the run's
            var level = entityLevel;
            switch (asWhat)
            {
                case "lesson":
                    if (!MayWriteEntity(db, sc, me, entityLevel))
                        return Results.Json(new { detail = "That conversation's entity is outside your scope." }, statusCode: 403);
                    break;
                case "knowledge":
                    if (!mayManageAi)
                        return Results.Json(new { detail = "You need the 'settings.ai.manage' permission." }, statusCode: 403);
                    if (!MayWriteEntity(db, sc, me, entityLevel))
                        return Results.Json(new { detail = "That conversation's entity is outside your scope." }, statusCode: 403);
                    break;
                default:
                    if (!mayManageAi)
                        return Results.Json(new { detail = "You need the 'settings.ai.manage' permission." }, statusCode: 403);
                    if (!AiControlsEndpoints.MayEdit(db, sc, me, level))
                        return Results.Json(new
                        {
                            detail = level == null
                                ? "Only someone not limited to particular entities can change the organization-wide house rules."
                                : "That outlet is outside your scope.",
                        }, statusCode: 403);
                    break;
            }
            var claimed = await db.AiReviewFindings.Where(f => f.Id == finding.Id && !f.Applied)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.Applied, true).SetProperty(f => f.AppliedAs, asWhat));
            if (claimed == 0)
                return Results.Json(new { detail = "This finding was already added." }, statusCode: 409);
            async Task<IResult> Release(IResult answer)
            {
                await db.AiReviewFindings.Where(f => f.Id == finding.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(f => f.Applied, false).SetProperty(f => f.AppliedAs, ""));
                return answer;
            }

            switch (asWhat)
            {
                case "lesson":
                {
                    // a reviewer's suggestion is never live on its own: a person approves it
                    // in the Learning tab, whatever the learning mode
                    var status = AiLessonStatus.Suggested;
                    var lesson = new AiLesson
                    {
                        EntityId = entityLevel, Kind = AiLessonKind.Rule, Status = status,
                        Question = "", Answer = text, Note = note,
                        SourceCount = 0, CreatedBy = actor.Username,
                        ApprovedBy = "",
                        CreatedAt = now, UpdatedAt = now,
                    };
                    db.AiLessons.Add(lesson);
                    await db.SaveChangesAsync();
                    AuditTrail.Add(db, "ai.lesson.created", $"lesson:{lesson.Id}", $"rule (from AI review): {TextAnalysis.Clip(text, 200)}",
                        actor: actor.Username, entityId: entityLevel, userId: actor.UserId);
                    EmbeddingWorker.Kick();
                    appliedRef = $"lesson:{lesson.Id}";
                    result = new
                    {
                        lesson_id = lesson.Id, status,
                        message = "Added as a suggested lesson — approve it in the Learning tab before the AI uses it.",
                    };
                    break;
                }
                case "knowledge":
                {
                    var question = TextAnalysis.Clip(finding.CustomerExcerpt.Replace('\n', ' ').Trim(), 300);
                    var item = new KnowledgeItem
                    {
                        EntityId = entityLevel,
                        Kind = question.Length > 0 ? "faq" : "fact",
                        Title = TextAnalysis.Clip(text.Replace('\n', ' '), 120),
                        Question = question,
                        Content = text,
                        Active = false, Published = false,
                        CreatedBy = actor.Username, CreatedAt = now, UpdatedAt = now,
                    };
                    db.KnowledgeItems.Add(item);
                    await db.SaveChangesAsync();
                    AuditTrail.Add(db, "knowledge.created", $"knowledge:{item.Id}", $"draft from AI review: {TextAnalysis.Clip(item.Title, 200)}",
                        actor: actor.Username, entityId: entityLevel, userId: actor.UserId);
                    EmbeddingWorker.Kick();
                    appliedRef = $"knowledge:{item.Id}";
                    result = new
                    {
                        knowledge_id = item.Id, active = false,
                        message = "Saved as an inactive knowledge draft — check the wording and switch it on in Knowledge.",
                    };
                    break;
                }
                default:
                {
                    var line = string.Join(" ", PersonaService.SplitRules(text));
                    var row = await db.Settings.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Key == PersonaService.HouseRulesKey && s.EntityId == level);
                    var rules = PersonaService.SplitRules(row?.Value);
                    if (rules.Any(r => string.Equals(r, line, StringComparison.OrdinalIgnoreCase)))
                        return await Release(Results.Json(new { detail = "That house rule is already there." }, statusCode: 409));
                    rules.Add(line);
                    var joined = string.Join("\n", rules);
                    if (joined.Length > PersonaService.MaxHouseRuleChars)
                        return await Release(Results.BadRequest(new { detail = $"House rules are too long ({PersonaService.MaxHouseRuleChars} characters at most)." }));
                    sc.SetSetting(PersonaService.HouseRulesKey, joined, level);
                    var ent = level is int lv ? await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == lv) : null;
                    AuditTrail.Add(db, "ai.persona", ent != null ? $"entity:{ent.Slug}" : "global",
                        $"Added a house rule from AI review #{run.Id}: {TextAnalysis.Clip(line, 200)}",
                        actor: actor.Username, entityId: level, userId: actor.UserId);
                    appliedRef = "house_rule";
                    result = new
                    {
                        level = ent?.Slug, level_name = ent?.Name ?? "",
                        message = ent != null ? $"Added to {ent.Name}'s house rules." : "Added to the organization's house rules.",
                    };
                    break;
                }
            }

            finding.Applied = true;
            finding.AppliedAs = asWhat;
            finding.AppliedRef = appliedRef;
            finding.AppliedBy = actor.Username;
            finding.AppliedAt = now;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, applied_as = asWhat, applied_ref = appliedRef, result, finding = FindingRow(finding) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(BehaviourCap);
    }

    // ---- scope ------------------------------------------------------------------------

    /// <summary>
    /// The run's entity (the first pick) and every entity it may read: the picked
    /// entities' subtrees (comma list allowed), bounded by an agent's assigned
    /// subtree — no pick = the caller's whole scope (empty = unrestricted).
    /// </summary>
    private static async Task<(int? EntityId, List<int>? EntityIds, IResult? Error)> RunScope(AppDbContext db,
        ScopingService sc, ClaimsPrincipal me, string? entity)
    {
        var scope = await AiMonitorEndpoints.ScopeFor(db, sc, me, null);
        var allowed = scope.EntityIds;
        var slugs = (entity ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s is not ("all" or "__all__")).Distinct().ToList();
        if (slugs.Count == 0) return (null, allowed, null);
        var ents = await db.Entities.AsNoTracking().Where(e => slugs.Contains(e.Slug)).Select(e => new { e.Id, e.Slug }).ToListAsync();
        var unknown = slugs.FirstOrDefault(s => ents.All(e => e.Slug != s));
        if (unknown != null) return (null, null, Results.NotFound(new { detail = $"Unknown entity '{unknown}'." }));
        var picks = slugs.Select(s => ents.First(e => e.Slug == s).Id)
            .Where(id => allowed == null || allowed.Contains(id)).ToList();
        if (picks.Count == 0) return (null, null, Results.Json(new { detail = "That entity is outside your scope." }, statusCode: 403));
        var ids = picks.SelectMany(sc.EntitySubtreeIds).Distinct()
            .Where(id => allowed == null || allowed.Contains(id)).ToList();
        return (picks[0], ids, null);
    }

    /// <summary>A run is visible when it read only entities inside the caller's scope.</summary>
    private static bool Visible(AiReviewRun run, List<int>? allowed) =>
        allowed == null || (run.EntityIds.Count > 0 && run.EntityIds.All(allowed.Contains));

    private static bool FindingVisible(AiReviewFinding f, List<int>? allowed) =>
        allowed == null || f.EntityId == null || allowed.Contains(f.EntityId.Value);

    private static bool MayWriteEntity(AppDbContext db, ScopingService sc, ClaimsPrincipal me, int? entityId)
    {
        var assigned = ShellScope.ScopeRoots(db, me);   // empty for superadmin + org-wide admins
        if (assigned.Count == 0) return true;
        return entityId is int eid && sc.EntitySubtreeIds(assigned).Contains(eid);
    }

    // ---- payloads ---------------------------------------------------------------------

    private static object RunRow(AiReviewRun r) => new
    {
        id = r.Id,
        status = r.Status,
        mode = r.Mode,
        provider = r.Provider,
        model = r.Model,
        requested_model = r.RequestedModel,
        note = r.Note,
        error = r.Error,
        created_by = r.CreatedBy,
        created_at = r.CreatedAt,
        finished_at = r.FinishedAt,
        period_from = r.PeriodFrom,
        period_to = r.PeriodTo,
        limit = r.Limit,
        entity_id = r.EntityId,
        reviewed = r.Reviewed,
        issues = r.Issues,
        high_severity = r.HighSeverity,
        prompt_tokens = r.PromptTokens,
        completion_tokens = r.CompletionTokens,
        cost_usd = r.CostUsd,
        summary = ParseSummary(r.Summary),
    };

    private static async Task<object> RunDetail(AppDbContext db, AiReviewRun r, List<int>? allowed, string? verdict)
    {
        var findings = await db.AiReviewFindings.AsNoTracking().Where(f => f.RunId == r.Id).ToListAsync();
        findings = findings.Where(f => FindingVisible(f, allowed)).ToList();
        var counts = new { all = findings.Count, issues = findings.Count(f => f.Verdict == "issue") };
        if (verdict is "issue" or "issues") findings = findings.Where(f => f.Verdict == "issue").ToList();
        else if (verdict == "good") findings = findings.Where(f => f.Verdict == "good").ToList();
        var ordered = findings
            .OrderBy(f => f.Verdict == "issue" ? 0 : 1)
            .ThenBy(f => AiReviewService.SeverityRank(f.Severity))
            .ThenByDescending(f => f.Id).ToList();
        var ent = r.EntityId is int eid ? await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid) : null;
        var row = (Dictionary<string, object?>)ToDict(RunRow(r));
        row["entity"] = ent?.Slug;
        row["entity_name"] = ent?.Name ?? "";
        row["counts"] = counts;
        row["findings"] = ordered.Select(FindingRow).ToList();
        return row;
    }

    private static object FindingRow(AiReviewFinding f) => new
    {
        id = f.Id,
        run_id = f.RunId,
        draft_id = f.DraftId,
        channel = f.Channel,
        thread_key = f.ThreadKey,
        entity_id = f.EntityId,
        draft_status = f.DraftStatus,
        verdict = f.Verdict,
        severity = f.Severity,
        categories = f.Categories,
        explanation = f.Explanation,
        evidence = f.Evidence,
        fix = new { type = f.FixType, text = f.FixText },
        customer_excerpt = f.CustomerExcerpt,
        reply_excerpt = f.ReplyExcerpt,
        source = f.Source,
        applied = f.Applied,
        applied_as = f.AppliedAs,
        applied_ref = f.AppliedRef,
        applied_by = f.AppliedBy,
        applied_at = f.AppliedAt,
        links = new
        {
            chat = f.Channel.Length > 0 && f.ThreadKey.Length > 0
                ? $"/chats?thread={Uri.EscapeDataString(f.Channel)}:{Uri.EscapeDataString(f.ThreadKey)}" : null,
            draft = f.DraftId is int d ? $"/api/ai/monitor/{d}" : null,
        },
    };

    private static Dictionary<string, object?> ToDict(object o) =>
        o.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(o));

    private static object? ParseSummary(string raw)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(raw) ? "{}" : raw); }
        catch { return new { }; }
    }

    // ---- body readers ------------------------------------------------------------------

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

    private static int? Int(JsonElement? body, string key) =>
        int.TryParse(Str(body, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
