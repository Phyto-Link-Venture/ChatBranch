using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Api.Audit;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The AI playground (<c>/ai-playground</c>): an operator chats with the AI as a
/// customer would, sees why it answered as it did, tries changes (another
/// model, an unsaved persona, knowledge / lessons / skills / memory off) and
/// compares set-ups — and NOTHING reaches a customer or touches real data. The
/// server is stateless: the browser sends the transcript every time.
///
/// * <c>GET /api/ai/playground/options?entity=</c> — what the page may offer:
///   configured providers (names and default models, never keys), the stored
///   persona (<c>ai.behaviour.manage</c> only), channels, the entity's
///   production policy, the learning mode, custom-field definitions.
/// * <c>POST /api/ai/playground/reply</c> — one reply through the real
///   generator (<see cref="AssistantService.GeneratePlaygroundAsync"/>) plus
///   everything the inspector shows and the production verdict
///   (<see cref="AiPlayground.Decide"/>). ≤ 60 turns, ≤ 4 000 chars a turn,
///   30 calls a minute per user (429).
/// * <c>POST /api/ai/playground/import</c> — a real conversation the caller can
///   see (<c>chats.view</c> + the inbox scope; out of scope = 404) as a
///   transcript. Read only.
///
/// Every route: signed in + <c>ai.monitor</c> or <c>ai.behaviour.manage</c>.
/// Overrides (provider / model, persona, a part switched off, the full prompt)
/// need <c>ai.behaviour.manage</c> (403). The entity must be in the caller's
/// scope (admins: any; agents / custom roles: their assigned subtree; an
/// unassigned one keeps the tenant-wide view); a real contact goes through
/// <see cref="ContactsEndpoints.ScopeContacts"/> (404). The POSTs write no
/// request-audit row — they change nothing.
/// </summary>
public static class AiPlaygroundEndpoints
{
    public const int MaxTurns = 60;
    public const int MaxChars = 4000;
    public const int MaxPersona = 8000;
    public const int ImportDefault = 20;

    /// <summary>The conversation channels a playground chat can stand in for.</summary>
    public static readonly string[] PlaygroundChannels =
    {
        Channels.WhatsApp, Channels.Website, Channels.Telegram, Channels.Email, Channels.Facebook,
        Channels.Instagram, Channels.Line, Channels.Viber, Channels.Sms, Channels.WeChat, Channels.TikTok,
        Channels.Douyin, Channels.Zalo,
    };

    private static readonly string[] Languages = { "en", "ms", "zh" };

    public static void MapAiPlaygroundEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ai/playground/options", async (string? entity, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, ILlmClient llm, LearningService learning) =>
        {
            var (entityId, err) = await EntityScope(db, sc, me, entity);
            if (err != null) return err;
            var canOverride = await ShellScope.HasCapAsync(db, me, AiMonitorEndpoints.BehaviourCap);
            var canImport = await ShellScope.HasCapAsync(db, me, "chats.view");
            var ent = entityId is int eid
                ? await db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => new { e.Slug, e.Name }).FirstOrDefaultAsync()
                : null;
            var real = llm as OpenAiLlmClient;
            var route = real?.Candidates().FirstOrDefault();
            var policy = AiBehaviourSettings.Resolve(sc, entityId);
            var ai = AiPlayground.Eligibility(db, sc, entityId, Channels.WhatsApp, null, null);
            var mode = learning.Mode(entityId);
            var actor = learning.ActorFor(me.Identity?.Name);
            var persona = new PersonaService(sc);
            return Results.Ok(new
            {
                entity = ent == null ? null : new { slug = ent.Slug, name = ent.Name },
                sandbox = !llm.IsConfigured,
                can_override = canOverride,
                can_teach = canOverride,
                can_import = canImport,
                can_manage_keys = await ShellScope.HasCapAsync(db, me, "settings.ai_keys.manage"),
                providers = canOverride && real != null
                    ? real.Configured().Select(p => new
                    {
                        id = p.Name,
                        label = OpenAiLlmClient.Labels.TryGetValue(p.Name, out var l) ? l : p.Name,
                        default_model = p.Model,
                    }).ToList<object>()
                    : new List<object>(),
                default_route = route == null ? null : new
                {
                    provider = route.Name,
                    label = OpenAiLlmClient.Labels.TryGetValue(route.Name, out var rl) ? rl : route.Name,
                    model = route.Model,
                },
                persona = canOverride ? persona.PersonaBody(entityId) : null,
                persona_is_default = canOverride
                    ? (bool?)string.IsNullOrWhiteSpace(sc.SettingValue("aicore_persona", entityId)) : null,
                channels = PlaygroundChannels,
                languages = Languages,
                toggles = new { knowledge = true, lessons = true, skills = true, memory = true },
                policy = new
                {
                    review_mode = policy.ReviewMode,
                    first_n = policy.FirstN,
                    autosend_minutes = policy.AutosendMinutes,
                    split = policy.SplitMode,
                    split_max_chars = policy.SplitMaxChars,
                },
                ai = new { enabled = ai.Enabled, rule = ai.Rule, reason = ai.Reason, forced = ai.Forced },
                learning_mode = mode,
                // what a lesson taught here becomes: live at once only in auto mode from an admin
                teach_status = mode == "auto" && actor.IsAdmin ? AiLessonStatus.Active : AiLessonStatus.Suggested,
                custom_fields = CustomFieldsEndpoints.CustomerFields(sc).Select(f => new
                {
                    key = f.Key, label = f.Label, kind = f.Kind, options = f.Options,
                }).ToList(),
                limits = new { turns = MaxTurns, chars = MaxChars, per_minute = PlaygroundRateLimiter.Limit, persona = MaxPersona },
            });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireAnyCapability(AiMonitorEndpoints.MonitorCap, AiMonitorEndpoints.BehaviourCap);

        app.MapPost("/api/ai/playground/reply", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, ILlmClient llm, AssistantService assistant, PlaygroundRateLimiter limiter) =>
        {
            var body = await ReadJson(ctx);
            if (body is not JsonElement b || b.ValueKind != JsonValueKind.Object)
                return Bad("Send {entity, channel, contact_id | profile, messages: [{role, text}], options}.");
            var user = me.Identity?.Name ?? "";
            if (QuotaGuard.Refusal(db, sc, me) is { } quota) return quota;
            if (!limiter.TryTake(user, DateTime.UtcNow, out var retry))
                return Results.Json(new
                {
                    detail = $"Slow down — the playground answers at most {PlaygroundRateLimiter.Limit} messages a minute. "
                             + $"Try again in {retry} s.",
                    retry_after = retry,
                }, statusCode: 429);

            // ---- options: anything beyond the defaults needs ai.behaviour.manage
            var o = Obj(b, "options");
            var provider = (Str(o, "provider") ?? "").Trim().ToLowerInvariant();
            var model = (Str(o, "model") ?? "").Trim();
            var persona = Str(o, "persona");
            var opts = new PlaygroundOptions(
                Persona: string.IsNullOrWhiteSpace(persona) ? null : persona,
                Knowledge: Bool(o, "knowledge") ?? true,
                Lessons: Bool(o, "lessons") ?? true,
                Skills: Bool(o, "skills") ?? true,
                Memory: Bool(o, "memory") ?? true);
            var showPrompt = Bool(o, "show_prompt") ?? false;
            var overrides = provider.Length > 0 || model.Length > 0 || opts.Persona != null || !opts.Knowledge
                            || !opts.Lessons || !opts.Skills || !opts.Memory || showPrompt;
            if (overrides && !await ShellScope.HasCapAsync(db, me, AiMonitorEndpoints.BehaviourCap))
                return Results.Json(new
                {
                    detail = "Trying changes (another model, a persona draft, parts switched off, the full prompt) "
                             + "needs the ai.behaviour.manage capability.",
                }, statusCode: 403);
            if (opts.Persona is { Length: > MaxPersona })
                return Bad($"Keep the persona under {MaxPersona} characters.");

            // ---- where: the entity (in scope), the channel, an inbox, a real conversation
            var (entityId, err) = await EntityScope(db, sc, me, Str(b, "entity"));
            if (err != null) return err;
            var channel = (Str(b, "channel") ?? Channels.WhatsApp).Trim().ToLowerInvariant();
            if (!PlaygroundChannels.Contains(channel))
                return Bad($"channel must be one of {string.Join(", ", PlaygroundChannels)}.");
            ChannelAccount? account = null;
            if (Int(b, "account_id") is int accountId)
            {
                account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId);
                if (account == null || !AccountVisible(db, sc, me, account))
                    return Results.NotFound(new { detail = "Inbox not found." });
            }
            Thread? real = null;
            if (Str(b, "thread") is { Length: > 0 } threadRef)
            {
                real = await FindThreadAsync(db, threadRef);
                if (real == null || !ThreadAccessFilter.InScope(db, sc, me, real.EntityId))
                    return Results.NotFound(new { detail = "Conversation not found." });
            }

            // ---- who: a real contact (scoped, read only) or a made-up profile
            int? contactId = null;
            PlaygroundProfile? profile = null;
            if (Int(b, "contact_id") is int cid)
            {
                var visible = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                    .AnyAsync(c => c.Id == cid);
                if (!visible) return Results.NotFound(new { detail = "Contact not found." });
                contactId = cid;
            }
            else if (Obj(b, "profile") is JsonElement p && p.ValueKind == JsonValueKind.Object)
            {
                var language = (Str(p, "language") ?? "en").Trim().ToLowerInvariant();
                if (!Languages.Contains(language)) return Bad("profile.language must be en, ms or zh.");
                var name = (Str(p, "name") ?? "").Trim();
                if (name.Length > 120) return Bad("Keep the name under 120 characters.");
                var tags = StrList(p, "tags").Select(t => t.Length > 64 ? t[..64] : t).Take(30).ToList();
                var fields = new Dictionary<string, object?>();
                if (Obj(p, "fields") is JsonElement f && f.ValueKind == JsonValueKind.Object)
                    foreach (var prop in f.EnumerateObject().Take(50))
                        fields[prop.Name.Length > 64 ? prop.Name[..64] : prop.Name] = FieldValue(prop.Value);
                profile = new PlaygroundProfile(name, language, tags, fields);
            }

            // ---- the transcript
            if (!b.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
                return Bad("messages must be a list of {role: \"customer\" | \"ai\", text}.");
            var turns = new List<PlaygroundTurn>();
            foreach (var m in msgs.EnumerateArray())
            {
                var role = (Str(m, "role") ?? "").Trim().ToLowerInvariant();
                if (role is "user") role = PlaygroundTurn.Customer;
                if (role is "assistant") role = PlaygroundTurn.Ai;
                if (role is not (PlaygroundTurn.Customer or PlaygroundTurn.Ai))
                    return Bad("Each message needs role \"customer\" or \"ai\".");
                var text = (Str(m, "text") ?? "").Trim();
                if (text.Length > MaxChars) return Bad($"Keep each message under {MaxChars} characters.");
                if (text.Length > 0) turns.Add(new PlaygroundTurn(role, text));
            }
            if (turns.Count > MaxTurns) return Bad($"At most {MaxTurns} messages — reset the chat or drop the oldest.");
            if (turns.Count == 0 || turns[^1].Role != PlaygroundTurn.Customer)
                return Bad("End the transcript with the customer's message — that is what the AI answers.");

            // ---- the model: the configured order, or ONE configured provider (+ model)
            ILlmClient model_ = llm;
            if (provider.Length > 0)
            {
                var pinned = (llm as OpenAiLlmClient)?.Pinned(provider, model.Length > 0 ? model : null);
                if (pinned == null)
                    return Bad($"The provider '{provider}' isn't configured — add its key in Settings → AI keys & models.");
                model_ = pinned;
            }
            else if (model.Length > 0) return Bad("Pick the provider the model belongs to.");
            if (model.Length > 120) return Bad("That model name is too long.");

            var watch = Stopwatch.StartNew();
            var result = await assistant.GeneratePlaygroundAsync(new PlaygroundInput(entityId, channel, contactId, profile,
                turns, opts, user, real?.Assignee is { Length: > 0 } a ? a : user), model_, ctx.RequestAborted);
            watch.Stop();
            var r = result.Reply;

            var policy = AiBehaviourSettings.Resolve(sc, entityId, account ?? (real?.AccountId is int ra
                ? await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ra) : null));
            var ai = AiPlayground.Eligibility(db, sc, entityId, channel, account, result.Contact, real);
            var prior = turns.Count(t => t.Role == PlaygroundTurn.Ai);
            var production = AiPlayground.Decide(policy, ai, r, prior);
            var said = r.ShouldSend ? r.Text : r.Draft;
            var bubbles = string.IsNullOrWhiteSpace(said)
                ? new List<string>()
                : Humanizer.Split(said, policy.SplitMode, policy.SplitMaxChars) is { Count: > 0 } split ? split
                : new List<string> { said.Trim() };
            var (cost, priced) = r.CostUsd is double paid ? (Math.Round(paid, 6), true)
                : EstimateCost(sc, r.Model, r.PromptTokens, r.CompletionTokens);
            var used = r.Grounding.Select(g => (g.Type, g.Id)).ToHashSet();
            var previewing = result.Bookings.Count > 0;

            return Results.Ok(new
            {
                reply = r.ShouldSend ? r.Text : "",
                draft = r.Draft,
                bubbles,
                action = r.Action,
                reason = r.Reason,
                production = new
                {
                    verdict = production.Verdict,
                    why = production.Why,
                    rule = production.Rule,
                    kind = production.Kind,
                    review_mode = production.ReviewMode,
                    shadow = production.Shadow,
                    flags = production.Flags,
                    ai = new { enabled = ai.Enabled, rule = ai.Rule, reason = ai.Reason, forced = ai.Forced },
                },
                grounding = r.Grounding.Select(g => new
                {
                    type = g.Type, id = g.Id, title = g.Title,
                    chunk = g.Chunk.Length <= 400 ? g.Chunk : g.Chunk[..400] + "…",
                    score = Math.Round(g.Score, 3),
                }).ToList(),
                hits = result.Hits.Take(20).Select(h => new
                {
                    type = h.Type, id = h.Id, title = h.Title,
                    part = h.ChunkCount > 1 ? $"{h.ChunkIndex + 1}/{h.ChunkCount}" : null,
                    score = Math.Round(h.Score, 3), semantic = Math.Round(h.Semantic, 3),
                    keyword = Math.Round(h.Keyword, 3), used = used.Contains((h.Type, h.Id)),
                }).ToList(),
                tools = r.Tools.Select(t => new
                {
                    name = t.Name, args = t.Args, result = t.Result, ok = t.Ok,
                    dry_run = previewing && CalendarWrite.IsMatch(t.Name),
                }).ToList(),
                tools_offered = result.ToolsOffered,
                bookings_previewed = result.Bookings.Select(x => new
                {
                    type = x.Type, label = x.Label, start = x.Start, timezone = x.Timezone, calendar = x.Calendar,
                    booking_id = x.BookingId,
                }).ToList(),
                checks = r.Checks.Select(c => new
                {
                    name = c.Name, passed = c.Ok, reason = c.Reason, fix = c.Fix, error = c.Error, attempt = c.Attempt,
                }).ToList(),
                checks_configured = result.ChecksConfigured,
                retries = r.Retries,
                provider = r.Provider,
                model = r.Model,
                sandbox = r.Provider is "sandbox" or "",
                provider_error = (model_ as OpenAiLlmClient)?.LastError is { Length: > 0 } pe ? pe : null,
                tokens = new { prompt = r.PromptTokens, completion = r.CompletionTokens },
                cost_estimate = new { usd = cost, priced },
                latency_ms = watch.ElapsedMilliseconds,
                contact = result.Contact == null ? null : new
                {
                    id = result.Contact.Id > 0 ? result.Contact.Id : (int?)null,
                    name = result.Contact.Name,
                    made_up = result.Contact.Id <= 0,
                },
                options = new
                {
                    provider = provider.Length > 0 ? provider : null,
                    model = model.Length > 0 ? model : null,
                    persona = opts.Persona != null,
                    knowledge = opts.Knowledge, lessons = opts.Lessons, skills = opts.Skills, memory = opts.Memory,
                },
                prompt = showPrompt
                    ? new
                    {
                        system = result.SystemPrompt,
                        messages = result.Messages.Select(m => new { role = m.Role, content = m.Content ?? "" }).ToList(),
                    }
                    : null,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireAnyCapability(AiMonitorEndpoints.MonitorCap, AiMonitorEndpoints.BehaviourCap)
          .WithMetadata(new SkipRequestAuditAttribute());

        app.MapPost("/api/ai/playground/import", async (HttpContext ctx, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            if (!await ShellScope.HasCapAsync(db, me, "chats.view"))
                return Results.Json(new { detail = "Importing a conversation needs the chats.view capability." },
                    statusCode: 403);
            var body = await ReadJson(ctx);
            if (body is not JsonElement b || b.ValueKind != JsonValueKind.Object)
                return Bad("Send {channel, key, take?}.");
            var channel = (Str(b, "channel") ?? "").Trim().ToLowerInvariant();
            var key = (Str(b, "key") ?? "").Trim();
            if (channel.Length == 0 || key.Length == 0) return Bad("Send the conversation's channel and key.");
            var take = Math.Clamp(Int(b, "take") ?? ImportDefault, 1, MaxTurns);

            var t = await FindThreadAsync(db, $"{channel}:{key}");
            if (t == null || !ThreadAccessFilter.InScope(db, sc, me, t.EntityId))
                return Results.NotFound(new { detail = "Conversation not found." });

            var rows = await db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == t.Id && !m.Private && m.Text != "")
                .OrderByDescending(m => m.Id).Take(take).ToListAsync();
            rows.Reverse();
            var messages = rows.Select(m => new
            {
                role = m.Direction == MessageDirection.In ? PlaygroundTurn.Customer : PlaygroundTurn.Ai,
                text = m.Text.Length <= MaxChars ? m.Text : m.Text[..MaxChars],
            }).ToList();

            object? contact = null;
            if (t.ContactId is int cid)
            {
                var c = await ContactsEndpoints.ScopeContacts(db.Contacts.AsNoTracking(), me, db, sc)
                    .Where(x => x.Id == cid).Select(x => new { x.Id, x.Name, x.Language, x.Tags }).FirstOrDefaultAsync();
                if (c != null) contact = new { id = c.Id, name = c.Name, language = c.Language, tags = c.Tags };
            }
            var ent = t.EntityId is int eid
                ? await db.Entities.AsNoTracking().Where(e => e.Id == eid).Select(e => new { e.Slug, e.Name }).FirstOrDefaultAsync()
                : null;
            var acct = t.AccountId is int aid
                ? await db.ChannelAccounts.AsNoTracking().Where(a => a.Id == aid).Select(a => new { a.Id, a.Name }).FirstOrDefaultAsync()
                : null;
            return Results.Ok(new
            {
                thread = $"{t.Channel}:{t.Key}",
                name = string.IsNullOrWhiteSpace(t.Name) ? t.Key : t.Name,
                channel = PlaygroundChannels.Contains(t.Channel) ? t.Channel : Channels.WhatsApp,
                entity = ent == null ? null : new { slug = ent.Slug, name = ent.Name },
                account = acct == null ? null : new { id = acct.Id, name = acct.Name },
                contact,
                messages,
                total = await db.Messages.AsNoTracking().CountAsync(m => m.ThreadId == t.Id && !m.Private && m.Text != ""),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated)
          .RequireAnyCapability(AiMonitorEndpoints.MonitorCap, AiMonitorEndpoints.BehaviourCap)
          .WithMetadata(new SkipRequestAuditAttribute());
    }

    // ---- scope ----------------------------------------------------------------------------

    /// <summary>
    /// The outlet the playground speaks for: unknown (or another tenant's) = 404;
    /// admins any; agents / custom roles inside their assigned subtree (403), and
    /// the organization-wide view only without an assignment (the tenant-wide view).
    /// </summary>
    private static async Task<(int? Id, IResult? Error)> EntityScope(AppDbContext db, ScopingService sc,
        ClaimsPrincipal me, string? slug)
    {
        var role = ShellScope.RoleOf(me);
        var admin = role is Roles.SuperAdmin or Roles.Admin;
        var assigned = admin ? new List<int>() : ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        if (string.IsNullOrWhiteSpace(slug) || slug is "all" or "__all__")
            return admin || assigned.Count == 0
                ? (null, null)
                : (null, Results.Json(new { detail = "Pick one of your outlets." }, statusCode: 403));
        var ent = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Slug == slug);
        if (ent == null || (role != Roles.SuperAdmin && TenantOf(me) is int tid && ent.TenantId != tid))
            return (null, Results.NotFound(new { detail = $"Unknown entity '{slug}'." }));
        if (!admin && assigned.Count > 0 && !sc.EntitySubtreeIds(assigned).Contains(ent.Id))
            return (null, Results.Json(new { detail = "That outlet is outside your scope." }, statusCode: 403));
        return (ent.Id, null);
    }

    /// <summary>An inbox the caller may test against: org-wide, or in their assigned subtree.</summary>
    private static bool AccountVisible(AppDbContext db, ScopingService sc, ClaimsPrincipal me, ChannelAccount a)
    {
        if (a.EntityId is not int eid || ShellScope.RoleOf(me) is Roles.SuperAdmin or Roles.Admin) return true;
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        return assigned.Count == 0 || sc.EntitySubtreeIds(assigned).Contains(eid);
    }

    private static int? TenantOf(ClaimsPrincipal me) =>
        int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : null;

    /// <summary>"channel:key" → the conversation (WhatsApp keys also as digits only, like the inbox routes).</summary>
    private static async Task<Thread?> FindThreadAsync(AppDbContext db, string reference)
    {
        var i = reference.IndexOf(':');
        if (i <= 0 || i == reference.Length - 1) return null;
        var channel = reference[..i].Trim().ToLowerInvariant();
        var key = reference[(i + 1)..].Trim();
        var keys = new List<string> { key };
        if (channel == Channels.WhatsApp) keys.Add(new string(key.Where(char.IsDigit).ToArray()));
        keys = keys.Where(k => k.Length > 0).Distinct().ToList();
        return await db.Threads.AsNoTracking().Include(t => t.Contact)
            .FirstOrDefaultAsync(t => t.Channel == channel && keys.Contains(t.Key));
    }

    // ---- small helpers --------------------------------------------------------------------

    /// <summary>A calendar tool that would change something (book / cancel) — previewed in the playground.</summary>
    private static readonly Regex CalendarWrite = new(@"^calendar(_\d+)?_(book|cancel)$", RegexOptions.Compiled);

    /// <summary>The usage report's rule (<c>llm_prices</c>, USD per 1M tokens; unknown models cost 0).</summary>
    internal static (double Usd, bool Priced) EstimateCost(ScopingService sc, string? model, int prompt, int completion)
    {
        var prices = AnalyticsEndpoints.PriceTable(sc);
        if (string.IsNullOrEmpty(model) || !prices.TryGetValue(model, out var p)) return (0, false);
        return (Math.Round((prompt * p.In + completion * p.Out) / 1_000_000d, 6), true);
    }

    private static string Clip(string? s, int n) => (s ?? "").Length <= n ? s ?? "" : s![..n];

    private static object? FieldValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => Clip(v.GetString(), 500),
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => v.EnumerateArray().Take(20)
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList(),
        _ => null,
    };

    private static async Task<JsonElement?> ReadJson(HttpContext ctx)
    {
        try
        {
            if (ctx.Request.ContentLength is 0) return null;
            return await ctx.Request.ReadFromJsonAsync<JsonElement>();
        }
        catch { return null; }
    }

    private static JsonElement? Obj(JsonElement? e, string key) =>
        e is JsonElement el && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v)
        && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string? Str(JsonElement? e, string key) => Obj(e, key) is JsonElement v
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number ? v.ToString() : null
        : null;

    private static int? Int(JsonElement? e, string key) => Obj(e, key) is JsonElement v
        ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
          : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s : null
        : null;

    private static bool? Bool(JsonElement? e, string key) => Obj(e, key) is JsonElement v
        ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        : null;

    private static List<string> StrList(JsonElement? e, string key) => Obj(e, key) is JsonElement v
        ? v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => (x.GetString() ?? "").Trim())
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : new List<string>()
        : new List<string>();

    private static IResult Bad(string detail) => Results.Json(new { detail }, statusCode: 400);
}

/// <summary>
/// The playground's per-user rate limit: at most <see cref="Limit"/> replies in
/// any rolling minute (each one is a real model call). In memory, per process —
/// a guard against a runaway loop, not a quota.
/// </summary>
public sealed class PlaygroundRateLimiter
{
    public const int Limit = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Count one call for <paramref name="user"/>; false (with the wait in seconds) when over the limit.</summary>
    public bool TryTake(string user, DateTime nowUtc, out int retryAfterSeconds)
    {
        var q = _hits.GetOrAdd(user ?? "", _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && nowUtc - q.Peek() >= Window) q.Dequeue();
            if (q.Count >= Limit)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((q.Peek() + Window - nowUtc).TotalSeconds));
                return false;
            }
            q.Enqueue(nowUtc);
            retryAfterSeconds = 0;
            return true;
        }
    }
}
