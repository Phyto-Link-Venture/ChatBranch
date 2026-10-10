using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The AI bill: what each COMPANY (a top-level entity — its branches roll up
/// into it) owes for its LLM usage in a calendar month.
///
/// Cost = the LlmUsage ledger priced by <c>llm_prices</c> (USD, the same table
/// as Analytics → AI usage). The bill = cost × (1 + markup %) converted at the
/// billing exchange rate, plus a flat monthly fee. Markup, fee, currency and
/// rate live in ONE organization-wide Setting <c>ai_billing</c>, set by
/// <c>billing.manage</c> (superadmin by default) — never by the company.
///
/// A usage row's company: its own <c>entity_id</c> (stamped by the AI brain),
/// else its contact's entity when every link shares one company, else
/// "unattributed" (platform work: skill drafting, embeddings, the operator
/// assistant) — shown to billing managers, billed to nobody.
///
/// Readers with <c>billing.view</c> see the bill of the companies in their scope
/// (their assigned entities' companies; unassigned = every company) as BILLED
/// amounts only. Cost, markup and margin are for <c>billing.manage</c> only.
///
/// Terms are locked per month (<see cref="BillingSettings"/>): a change bills
/// the current UTC month and later, a past month keeps the terms that applied
/// to it. The AI usage quotas (<see cref="AiQuota"/>) live here too:
/// <c>GET/PUT /api/billing/ai/quotas</c>.
/// </summary>
public static class AiBillingEndpoints
{
    public const string ViewCap = "billing.view";
    public const string ManageCap = "billing.manage";
    /// <summary>The WRITE capability for AI usage quotas (admins hold it by default;
    /// billing.view alone — or an API key scoped to it — can only read).</summary>
    public const string QuotasCap = "billing.quotas.manage";
    public const string SettingKey = AiBilling.SettingKey;

    internal static BillingSettings Load(ScopingService sc) => AiBilling.Load(sc);

    public static void MapAiBillingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/billing/ai/settings", async (AppDbContext db, ScopingService sc) =>
        {
            var s = Load(sc);
            var companies = await db.Entities.AsNoTracking().Where(e => e.ParentId == null)
                .OrderBy(e => e.Name).Select(e => new { e.Id, e.Name, e.Slug }).ToListAsync();
            return Results.Ok(new
            {
                settings = s.CopyTerms(s.EffectiveFrom),
                current_month = AiBilling.MonthKey(DateTime.UtcNow),
                // newest first: the current terms, then what billed earlier months
                history = History(s),
                companies = companies.Select(c => new
                {
                    id = c.Id, name = c.Name, slug = c.Slug,
                    markup_pct = s.Companies.TryGetValue(c.Id.ToString(CultureInfo.InvariantCulture), out var t) ? t.MarkupPct : null,
                    monthly_fee = t?.MonthlyFee,
                    effective_markup_pct = s.MarkupFor(c.Id),
                    effective_monthly_fee = s.FeeFor(c.Id),
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapPut("/api/billing/ai/settings", async (BillingSettings body, AppDbContext db, ScopingService sc) =>
        {
            var currency = (body.Currency ?? "").Trim().ToUpperInvariant();
            if (currency.Length is < 3 or > 3 || !currency.All(c => c is >= 'A' and <= 'Z'))
                return Bad("currency must be a 3-letter code, e.g. MYR or USD.");
            if (body.UsdRate is <= 0 or > 100000) return Bad("usd_rate must be above 0 (1 USD = how many units of your currency).");
            if (!Ok(body.DefaultMarkupPct) || !OkFee(body.DefaultMonthlyFee))
                return Bad("Markup must be between 0 and 10000 %, fees between 0 and 1,000,000.");
            var topLevel = (await db.Entities.AsNoTracking().Where(e => e.ParentId == null)
                .Select(e => e.Id).ToListAsync()).ToHashSet();
            var companies = new Dictionary<string, CompanyTerms>();
            foreach (var (key, terms) in body.Companies ?? new())
            {
                if (terms == null)
                    return Bad($"Company '{key}' needs an object like {{\"markup_pct\": 20}} — leave it out to use the defaults.");
                if (!int.TryParse(key, out var id) || !topLevel.Contains(id))
                    return Bad($"Unknown company '{key}' — use a top-level entity id.");
                if (terms.MarkupPct is double m && !Ok(m) || terms.MonthlyFee is double f && !OkFee(f))
                    return Bad("Markup must be between 0 and 10000 %, fees between 0 and 1,000,000.");
                if (terms.MarkupPct != null || terms.MonthlyFee != null)
                    companies[id.ToString(CultureInfo.InvariantCulture)] = terms;
            }
            var incoming = new BillingTerms
            {
                Currency = currency, UsdRate = body.UsdRate,
                DefaultMarkupPct = body.DefaultMarkupPct, DefaultMonthlyFee = body.DefaultMonthlyFee,
                Companies = companies,
            };
            // a change bills this month and later; earlier months keep their terms
            var stored = AiBilling.Load(sc, out var hadRow);
            var clean = AiBilling.Apply(stored, hadRow, incoming, DateTime.UtcNow);
            sc.SetSetting(SettingKey, JsonSerializer.Serialize(clean));
            AiQuota.Invalidate();   // quota spend is priced with these terms
            ChatBranch.Data.Audit.AuditTrail.Write(db, "billing.settings", "ai_billing",
                $"Markup {clean.DefaultMarkupPct}% default, {companies.Count} company override(s), {currency} @ {clean.UsdRate}, from {clean.EffectiveFrom ?? "the start"}");
            return Results.Ok(new { settings = clean.CopyTerms(clean.EffectiveFrom), history = History(clean) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ManageCap);

        app.MapGet("/api/billing/ai", async (string? month, string? company, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IHttpClientFactory http) =>
        {
            var (bill, error) = await BuildBillAsync(month, company, db, sc, me, http);
            if (bill == null) return error!;
            object Summary(BillCompany c) => new
            {
                id = c.Id, name = c.Name, slug = c.Slug,
                requests = c.Requests,
                tokens = c.Tokens,
                usage_amount = c.UsageAmount,
                monthly_fee = c.MonthlyFee,
                total = c.Total,
                // billing managers only
                cost_usd = c.CostUsd,
                markup_pct = c.MarkupPct,
                margin = c.Margin,
            };
            var manager = bill.Manager;
            return Results.Ok(new
            {
                month = bill.Month,
                from = bill.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to = bill.To.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                currency = bill.Currency,
                is_manager = manager,
                companies = bill.Companies.Select(Summary).ToList(),
                company = bill.Company == null ? null : new
                {
                    summary = Summary(bill.Company.Summary),
                    lines = bill.Company.Lines.Select(l => new
                    {
                        feature = l.Feature, requests = l.Requests, tokens = l.Tokens, amount = l.Amount, cost_usd = l.CostUsd,
                    }).ToList(),
                    daily = bill.Company.Daily.Select(d => new { date = d.Date, amount = d.Amount }).ToList(),
                },
                unattributed = bill.Unattributed == null ? null
                    : new { requests = bill.Unattributed.Requests, tokens = bill.Unattributed.Tokens, cost_usd = bill.Unattributed.CostUsd },
                usd_rate = bill.UsdRate,
                pricing_note = manager
                    ? "Cost = what the provider charged per call (OpenRouter reports it); calls without a reported "
                      + "price are estimated from llm_prices over OpenRouter's public price list (USD per 1M tokens). "
                      + "Bill = cost × (1 + markup) × the exchange rate + the monthly fee."
                    : null,
                // the dashboard translates the note by this code (the text above stays for API callers)
                pricing_note_code = manager ? "provider_cost_markup_fee" : null,
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ViewCap);

        // The same bill as a PDF (A4, MigraDoc) — the dependable way to print or save it,
        // above all on an iPhone, where window.print() does nothing inside the installed
        // app. Same gate, scope and visibility rules as the JSON above.
        app.MapGet("/api/billing/ai/pdf", async (string? month, string? company, string? lang, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, IHttpClientFactory http) =>
        {
            var (bill, error) = await BuildBillAsync(month, company, db, sc, me, http);
            if (bill == null) return error!;
            var issuer = sc.SettingValue("business_name").Trim();
            var bytes = AiBillPdf.Render(bill, issuer, lang, DateTime.UtcNow);
            var who = bill.Company?.Summary.Slug is { Length: > 0 } slug ? SafeFile(slug) : "all";
            return Results.File(bytes, "application/pdf", $"ai-bill-{who}-{bill.Month}.pdf");
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ViewCap);

        // A short-lived signed link to the same PDF, for the installed iOS app: a
        // standalone PWA can neither print nor reliably open a blob URL, but it can
        // NAVIGATE to a plain https URL (Safari's viewer → the share sheet: save,
        // share, print). The link carries no auth header, so it is signed instead:
        // bound to this person (re-checked when it is opened — deactivated, demoted
        // or out of scope = refused), to this month / company / language, and
        // valid for BillLink.Lifetime (5 minutes). Sessions only — an API key has
        // the authenticated GET above.
        app.MapPost("/api/billing/ai/pdf/link", async (BillLinkRequest? body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, IHttpClientFactory http) =>
        {
            if (ApiKeyAuth.IsApiKey(me))
                return Results.Json(new { detail = "API keys download the bill with GET /api/billing/ai/pdf." }, statusCode: 403);
            if (!int.TryParse(me.FindFirstValue("profile_id"), out var profileId))
                return Results.Json(new { detail = "Sign in to download the bill." }, statusCode: 403);
            var month = body?.Month;
            var company = body?.Company;
            // validate now (bad month = 400, out-of-scope company = 404), so the link never 404s later
            var (bill, error) = await BuildBillAsync(month, company, db, sc, me, http);
            if (bill == null) return error!;
            var expires = DateTimeOffset.UtcNow.Add(BillLink.Lifetime);
            var token = BillLink.Sign(new BillLink.Claims(profileId, me.Identity?.Name ?? "", bill.Month,
                (company ?? "").Trim(), NormalizeLang(body?.Lang), expires.ToUnixTimeSeconds()));
            return Results.Ok(new { path = $"/api/billing/ai/pdf/s/{token}", expires_at = expires.UtcDateTime });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability(ViewCap)
          .WithMetadata(new ChatBranch.Api.Audit.SkipRequestAuditAttribute());

        // The signed link itself: anonymous at the door, the signature IS the credential;
        // the bill is then built AS the person who asked for it, with the same rules.
        app.MapGet("/api/billing/ai/pdf/s/{token}", async (string token, AppDbContext db, ScopingService sc,
            IHttpClientFactory http, HttpContext ctx) =>
        {
            var claims = BillLink.Verify(token, DateTimeOffset.UtcNow);
            if (claims == null)
                return Results.Json(new { detail = "This download link has expired — open the bill and tap Download PDF again." }, statusCode: 410);
            var profile = await db.Profiles.AsNoTracking().Include(p => p.User)
                .FirstOrDefaultAsync(p => p.Id == claims.ProfileId);
            if (profile?.User is not { IsActive: true } user
                || !string.Equals(user.Username, claims.Username, StringComparison.Ordinal))
                return Results.Json(new { detail = "This download link is no longer valid." }, statusCode: 403);
            var identity = new ClaimsIdentity(new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Role, profile.Role),
                new("profile_id", profile.Id.ToString(CultureInfo.InvariantCulture)),
            }.Concat(profile.TenantId is int tid
                ? new[] { new Claim("tenant_id", tid.ToString(CultureInfo.InvariantCulture)) }
                : Array.Empty<Claim>()), "bill-link");
            var asUser = new ClaimsPrincipal(identity);
            if (!await ShellScope.HasCapAsync(db, asUser, ViewCap))
                return Results.Json(new { detail = "You no longer have access to the AI bill." }, statusCode: 403);
            var (bill, error) = await BuildBillAsync(claims.Month, claims.Company, db, sc, asUser, http);
            if (bill == null) return error!;
            var bytes = AiBillPdf.Render(bill, sc.SettingValue("business_name").Trim(), claims.Lang, DateTime.UtcNow);
            var who = bill.Company?.Summary.Slug is { Length: > 0 } slug ? SafeFile(slug) : "all";
            // inline: iOS shows it in the viewer, whose share button saves, shares or prints it
            ctx.Response.Headers.ContentDisposition = $"inline; filename=\"ai-bill-{who}-{bill.Month}.pdf\"";
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Bytes(bytes, "application/pdf");
        }).AllowAnonymous();

        // ---- AI usage quotas (monthly caps in the billing currency) ------------------------
        // GET: every quota in the caller's scope with month-to-date spend. A billing
        // manager (billing.manage / superadmin) sees and edits everything; an admin
        // with billing.view edits branch (non-top-level) caps and users inside their
        // own scope and sees their companies' caps read-only.
        app.MapGet("/api/billing/ai/quotas", async (AppDbContext db, ScopingService sc, ClaimsPrincipal me) =>
        {
            var q = await QuotaScopeAsync(db, sc, me);
            AiQuota.Sweep(db, sc);   // the 80 % / 100 % warnings, once per scope per month
            var snap = AiQuota.Build(db, sc, DateTime.UtcNow, always: true);   // spend shows before any cap is set
            var entities = await db.Entities.AsNoTracking()
                .Select(e => new { e.Id, e.ParentId, e.Name, e.Slug }).ToListAsync();
            var visible = entities.Where(e => q.ReadEntities == null || q.ReadEntities.Contains(e.Id)).ToList();
            // companies → branches, depth-first
            var children = visible.ToLookup(e => visible.Any(p => p.Id == e.ParentId) ? e.ParentId : null);
            var ordered = new List<(int Id, int Depth)>();
            void Walk(int? parent, int depth)
            {
                foreach (var e in children[parent].OrderBy(e => e.Name))
                {
                    if (ordered.Count > 2000 || depth > 16) return;
                    ordered.Add((e.Id, depth));
                    Walk(e.Id, depth + 1);
                }
            }
            Walk(null, 0);
            var byId = entities.ToDictionary(e => e.Id);

            var users = await UsersInScope(db, q);
            object Row(AiQuotaStatus s) => new
            {
                cap = s.Cap > 0 ? s.Cap : (double?)null,
                spent = s.Spent,
                pct = s.Pct,
                state = s.State,
            };
            return Results.Ok(new
            {
                month = snap.Month,
                currency = snap.Currency,
                is_manager = q.Manager,
                can_edit = q.Manager || q.CanEditBranches,
                entities = ordered.Select(o =>
                {
                    var s = snap.EntityStatus(o.Id);
                    var e = byId[o.Id];
                    return new
                    {
                        id = e.Id, name = e.Name, slug = e.Slug, parent_id = e.ParentId,
                        company = e.ParentId == null, depth = o.Depth,
                        cap = s is { Cap: > 0 } ? s.Cap : (double?)null,
                        spent = s?.Spent ?? 0,
                        pct = s?.Pct ?? 0,
                        state = s?.State ?? AiQuotaStatus.Ok,
                        set_by_manager = snap.Config.ManagerSet(AiQuotaConfig.EntityKey(e.Id.ToString(CultureInfo.InvariantCulture))),
                        editable = q.CanEditEntity(e.Id, e.ParentId)
                                   && (q.Manager || !snap.Config.ManagerSet(AiQuotaConfig.EntityKey(e.Id.ToString(CultureInfo.InvariantCulture)))),
                    };
                }).ToList(),
                users = users.Select(u =>
                {
                    var s = snap.UserStatus(u.Username);
                    return new
                    {
                        username = u.Username, role = u.Role,
                        cap = s.Cap > 0 ? s.Cap : (double?)null,
                        spent = s.Spent, pct = s.Pct, state = s.State,
                        set_by_manager = snap.Config.ManagerSet(AiQuotaConfig.UserKey(u.Username)),
                        // nobody sets their own cap but a superadmin; an admin never
                        // touches a cap a billing manager set
                        editable = (q.Manager || q.CanEditBranches)
                                   && (q.Superadmin || !string.Equals(u.Username, me.Identity?.Name, StringComparison.OrdinalIgnoreCase))
                                   && (q.Manager || !snap.Config.ManagerSet(AiQuotaConfig.UserKey(u.Username))),
                    };
                }).ToList(),
                counts = new
                {
                    paused = snap.All().Count(s => s.State == AiQuotaStatus.Paused),
                    warn = snap.All().Count(s => s.State == AiQuotaStatus.Warn),
                },
                note = "Quota spend = the month's billed AI usage (same prices, markup and currency as the bill; "
                       + "the monthly fee does not count). A company cap counts everything its bill counts; a branch "
                       + "cap counts usage recorded for that branch and its sub-branches; a user cap counts the "
                       + "assistant, playground, skill drafting and summaries that person runs. At 100% the AI pauses "
                       + "until next month or until the cap is raised.",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability(ViewCap, ManageCap);

        // PUT {entities?: {"<id>": amount|null}, users?: {"<username>": amount|null}} — a MERGE:
        // keys sent are set (0 / null clears the cap), keys left out keep theirs.
        app.MapPut("/api/billing/ai/quotas", async (JsonElement body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return Bad("Send {entities: {\"<entity id>\": amount}, users: {\"<username>\": amount}}.");
            var q = await QuotaScopeAsync(db, sc, me);
            if (!q.Manager && !q.CanEditBranches)
                return Results.Json(new { detail = "Only an admin or a billing manager can change AI quotas." }, statusCode: 403);
            var config = AiQuota.LoadConfig(sc);
            var changes = new List<string>();

            if (body.TryGetProperty("entities", out var ents) && ents.ValueKind == JsonValueKind.Object)
            {
                var entities = await db.Entities.AsNoTracking()
                    .Select(e => new { e.Id, e.ParentId, e.Name }).ToDictionaryAsync(e => e.Id);
                foreach (var p in ents.EnumerateObject())
                {
                    if (!int.TryParse(p.Name, out var id) || !entities.TryGetValue(id, out var e))
                        return Bad($"Unknown entity '{p.Name}'.");
                    if (!TryAmount(p.Value, out var amount))
                        return Bad(AmountError);
                    if (!q.CanEditEntity(e.Id, e.ParentId))
                        return Results.Json(new
                        {
                            detail = e.ParentId == null
                                ? $"Only a billing manager can set the company cap of {e.Name}."
                                : $"{e.Name} is outside your scope.",
                        }, statusCode: 403);
                    var key = id.ToString(CultureInfo.InvariantCulture);
                    var mark = AiQuotaConfig.EntityKey(key);
                    if (!q.Manager && config.ManagerSet(mark))
                        return Results.Json(new { detail = $"A billing manager set the cap of {e.Name} — only a billing manager can change it." }, statusCode: 403);
                    if (amount > 0) config.Entities[key] = amount; else config.Entities.Remove(key);
                    MarkSetter(config, mark, q.Manager && amount > 0);
                    changes.Add($"{e.Name}: {(amount > 0 ? amount.ToString("0.##", CultureInfo.InvariantCulture) : "no cap")}");
                }
            }
            if (body.TryGetProperty("users", out var us) && us.ValueKind == JsonValueKind.Object)
            {
                var inScope = (await UsersInScope(db, q)).Select(u => u.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var known = (await db.Users.AsNoTracking().Select(u => u.Username).ToListAsync())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var p in us.EnumerateObject())
                {
                    var name = p.Name.Trim();
                    if (!known.Contains(name)) return Bad($"Unknown user '{name}'.");
                    if (!TryAmount(p.Value, out var amount))
                        return Bad(AmountError);
                    if (!q.Manager && !inScope.Contains(name))
                        return Results.Json(new { detail = $"{name} is outside your scope." }, statusCode: 403);
                    if (!q.Superadmin && string.Equals(name, me.Identity?.Name, StringComparison.OrdinalIgnoreCase))
                        return Results.Json(new { detail = "You can't change your own AI quota — ask a billing manager." }, statusCode: 403);
                    var mark = AiQuotaConfig.UserKey(name);
                    if (!q.Manager && config.ManagerSet(mark))
                        return Results.Json(new { detail = $"A billing manager set {name}'s cap — only a billing manager can change it." }, statusCode: 403);
                    if (amount > 0) config.Users[name] = amount; else config.Users.Remove(name);
                    MarkSetter(config, mark, q.Manager && amount > 0);
                    changes.Add($"user {name}: {(amount > 0 ? amount.ToString("0.##", CultureInfo.InvariantCulture) : "no cap")}");
                }
            }
            if (changes.Count == 0) return Bad("Nothing to change — send entities and/or users.");
            AiQuota.SaveConfig(sc, config);
            ChatBranch.Data.Audit.AuditTrail.Write(db, "billing.quotas", AiQuota.SettingKey,
                string.Join("; ", changes.Take(20)) + (changes.Count > 20 ? $" (+{changes.Count - 20} more)" : ""));
            var snap = AiQuota.Current(db, sc);
            return Results.Ok(new
            {
                ok = true,
                currency = snap.Currency,
                quotas = snap.All().Select(s => new
                {
                    scope = s.Scope, key = s.Key, name = s.Name, cap = s.Cap, spent = s.Spent, pct = s.Pct, state = s.State,
                }),
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireAnyCapability(QuotasCap, ManageCap);
    }

    private const string AmountError =
        "A quota must be a number of at least 0.01 (0 or empty = no cap).";

    /// <summary>Record whether a billing manager (true) or an admin set this cap.</summary>
    private static void MarkSetter(AiQuotaConfig config, string mark, bool byManager)
    {
        config.SetByManager.RemoveAll(k => string.Equals(k, mark, StringComparison.OrdinalIgnoreCase));
        if (byManager) config.SetByManager.Add(mark);
    }

    // ---- quota scope -----------------------------------------------------------------------

    /// <summary>
    /// Who may see and set which quotas. <see cref="ReadEntities"/> null = every
    /// entity. <see cref="EditEntities"/> = the branches an admin may cap (their
    /// assigned subtrees, else their tenant's entities); company caps are the
    /// billing manager's alone.
    /// </summary>
    private sealed class QuotaScope
    {
        public bool Manager { get; init; }
        public bool Superadmin { get; init; }
        public bool CanEditBranches { get; init; }
        public HashSet<int>? ReadEntities { get; init; }
        public HashSet<int> EditEntities { get; init; } = new();
        // a bound caller (company admin, assigned agent): only users bound inside these
        public HashSet<int>? UserEntities { get; init; }
        public int? TenantId { get; init; }

        public bool CanEditEntity(int id, int? parentId) =>
            Manager || CanEditBranches && parentId != null && EditEntities.Contains(id);
    }

    private static async Task<QuotaScope> QuotaScopeAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me)
    {
        var role = ShellScope.RoleOf(me);
        var tenant = int.TryParse(me.FindFirstValue("tenant_id"), out var t) ? t : (int?)null;
        if (role == Roles.SuperAdmin || await ShellScope.HasCapAsync(db, me, ManageCap))
            return new QuotaScope { Manager = true, Superadmin = role == Roles.SuperAdmin, CanEditBranches = true, TenantId = tenant };

        // the entities this person works in: assigned subtrees, else (admin) their tenant
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        HashSet<int> own;
        if (assigned.Count > 0) own = sc.EntitySubtreeIds(assigned).ToHashSet();
        else if (role == Roles.Admin && tenant is int tid)
            own = (await db.Entities.AsNoTracking().Where(e => e.TenantId == tid).Select(e => e.Id).ToListAsync()).ToHashSet();
        else own = new HashSet<int>();
        // …plus the companies (and every level) above them, read-only
        var read = new HashSet<int>(own);
        foreach (var id in own) read.UnionWith(sc.EntityChain(id));
        return new QuotaScope
        {
            Manager = false,
            // admins with the quota WRITE capability (theirs by default)
            CanEditBranches = role == Roles.Admin && await ShellScope.HasCapAsync(db, me, QuotasCap),
            ReadEntities = read,
            EditEntities = own,
            TenantId = tenant,
            UserEntities = assigned.Count > 0 ? own : null,
        };
    }

    private sealed record QuotaUser(string Username, string Role);

    /// <summary>Managers: every login. Others: the logins of their own tenant (never a superadmin).</summary>
    private static async Task<List<QuotaUser>> UsersInScope(AppDbContext db, QuotaScope q)
    {
        var rows = await db.Users.AsNoTracking().Where(u => u.IsActive)
            .Select(u => new
            {
                u.Username, u.IsSuperuser,
                Role = u.Profile != null ? u.Profile.Role : "",
                Tenant = u.Profile != null ? u.Profile.TenantId : null,
                Entities = u.Profile != null ? u.Profile.Entities.Select(e => e.Id).ToList() : new List<int>(),
            })
            .OrderBy(u => u.Username).Take(500).ToListAsync();
        return rows
            .Where(u => q.Manager || (q.TenantId != null && u.Tenant == q.TenantId && !u.IsSuperuser && u.Role != Roles.SuperAdmin))
            // a company admin's quota list: only the users bound inside their companies
            .Where(u => q.Manager || q.UserEntities == null
                        || (u.Entities.Count > 0 && u.Entities.All(q.UserEntities.Contains)))
            .Select(u => new QuotaUser(u.Username, u.IsSuperuser ? Roles.SuperAdmin : u.Role)).ToList();
    }

    private static bool TryAmount(JsonElement v, out double amount)
    {
        amount = 0;
        if (v.ValueKind is JsonValueKind.Null) return true;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = (v.GetString() ?? "").Trim();
            if (s.Length == 0) return true;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out amount)) return false;
        }
        else if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out amount)) return false;
        // 0 clears; anything else must survive the 2-decimal rounding (≥ 0.01)
        return double.IsFinite(amount) && (amount == 0 || amount >= 0.01) && amount <= 1_000_000_000;
    }

    /// <summary>The current and earlier terms, newest first (the settings screen's "Since …" list).</summary>
    private static List<object> History(BillingSettings s) => s.Timeline().Select(x => (object)new
    {
        effective_from = x.Terms.EffectiveFrom,
        until = x.Until,
        currency = x.Terms.Currency,
        usd_rate = x.Terms.UsdRate,
        default_markup_pct = x.Terms.DefaultMarkupPct,
        default_monthly_fee = x.Terms.DefaultMonthlyFee,
        companies = x.Terms.Companies.Count,
    }).ToList();

    /// <summary>
    /// The month's bill as the caller may see it — the ONE computation behind
    /// <c>GET /api/billing/ai</c> and its PDF. Returns the bill, or the error to answer.
    /// </summary>
    internal static async Task<(BillReport? Bill, IResult? Error)> BuildBillAsync(string? month, string? company,
        AppDbContext db, ScopingService sc, ClaimsPrincipal me, IHttpClientFactory http)
    {
        await ModelPriceCatalog.EnsureFreshAsync(http, sc);
        if (!TryMonth(month, out var from)) return (null, Bad("month must look like 2026-10 (years 2000–2200)."));
        var to = from.AddMonths(1);
        var manager = await ShellScope.HasCapAsync(db, me, ManageCap);
        // the terms that applied to THAT month — a later change never rewrites a past bill
        var settings = Load(sc).TermsFor(AiBilling.MonthKey(from));
        var prices = AnalyticsEndpoints.PriceTable(sc);

        // ---- the tree: entity -> its company (top-level ancestor)
        var entities = await db.Entities.AsNoTracking()
            .Select(e => new { e.Id, e.ParentId, e.Name, e.Slug, e.CreatedAt }).ToListAsync();
        var byId = entities.ToDictionary(e => e.Id);
        int? CompanyOf(int? entityId)
        {
            var cur = entityId;
            for (var i = 0; i < 32 && cur is int id && byId.TryGetValue(id, out var e); i++)
            {
                if (e.ParentId == null) return e.Id;
                cur = e.ParentId;
            }
            return null;
        }
        var contactCompany = (await db.ContactEntities.AsNoTracking()
                .Select(l => new { l.ContactId, l.EntityId }).ToListAsync())
            .GroupBy(l => l.ContactId)
            .Select(g => (g.Key, Companies: g.Select(l => CompanyOf(l.EntityId)).Distinct().ToList()))
            .Where(x => x.Companies.Count == 1 && x.Companies[0] != null)
            .ToDictionary(x => x.Key, x => x.Companies[0]!.Value);

        // ---- who may see which company
        var scope = await VisibleCompanies(db, sc, me, manager, CompanyOf);
        var companies = entities.Where(e => e.ParentId == null && (scope == null || scope.Contains(e.Id)))
            .OrderBy(e => e.Name).ToList();
        var picked = string.IsNullOrWhiteSpace(company) ? null
            : companies.FirstOrDefault(c => c.Slug == company || c.Id.ToString(CultureInfo.InvariantCulture) == company);
        if (!string.IsNullOrWhiteSpace(company) && picked == null)
            return (null, Results.NotFound(new { detail = $"Unknown company '{company}'." }));
        if (picked == null && !manager && companies.Count == 1) picked = companies[0];

        // ---- the month's ledger, priced per row
        var rows = (await db.LlmUsage.AsNoTracking().Where(u => u.CreatedAt >= from && u.CreatedAt < to)
                .Select(u => new
                {
                    u.Feature, u.Model, u.EntityId, u.ContactId, u.CreatedAt,
                    u.PromptTokens, u.CompletionTokens, u.AudioPromptTokens, u.AudioCompletionTokens, u.CostUsd,
                }).ToListAsync())
            .Select(u =>
            {
                var cost = AnalyticsEndpoints.RowCost(prices, u.Model, u.PromptTokens, u.CompletionTokens,
                    u.AudioPromptTokens, u.AudioCompletionTokens, u.CostUsd);
                var co = CompanyOf(u.EntityId)
                         ?? (u.ContactId is int cid && contactCompany.TryGetValue(cid, out var cc) ? cc : (int?)null);
                return new BillRow(co, u.Feature, u.CreatedAt,
                    (long)u.PromptTokens + u.CompletionTokens + u.AudioPromptTokens + u.AudioCompletionTokens, cost);
            }).ToList();

        double Billed(int companyId, double costUsd) =>
            // 4 decimals: a light month is fractions of a cent, which must not read as 0
            Math.Round(costUsd * (1 + settings.MarkupFor(companyId) / 100d) * settings.UsdRate, 4, MidpointRounding.AwayFromZero);

        BillCompany Summary(int companyId, string name, string slug)
        {
            var mine = rows.Where(r => r.Company == companyId).ToList();
            var cost = mine.Sum(r => r.CostUsd);
            var usage = Billed(companyId, cost);
            // no monthly fee for a month that ended before the company existed
            var existed = !byId.TryGetValue(companyId, out var row) || row.CreatedAt == default || row.CreatedAt < to;
            var fee = existed ? Math.Round(settings.FeeFor(companyId), 2) : 0d;
            return new BillCompany(companyId, name, slug, mine.Count, mine.Sum(r => r.Tokens), usage, fee,
                Math.Round(usage + fee, 4),
                // billing managers only
                manager ? Math.Round(cost, 4) : null,
                manager ? settings.MarkupFor(companyId) : null,
                manager ? Math.Round(usage + fee - cost * settings.UsdRate, 4) : null);
        }

        BillDetail? Detail()
        {
            if (picked == null) return null;
            var mine = rows.Where(r => r.Company == picked.Id).ToList();
            var lines = mine.GroupBy(r => r.Feature).Select(g => new BillLine(
                    g.Key, g.Count(), g.Sum(r => r.Tokens), Billed(picked.Id, g.Sum(r => r.CostUsd)),
                    manager ? Math.Round(g.Sum(r => r.CostUsd), 4) : null))
                .OrderByDescending(l => l.Amount).ThenByDescending(l => l.Requests).ToList();
            var daily = Enumerable.Range(0, (to - from).Days).Select(i => from.AddDays(i)).Select(d => new BillDay(
                d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Billed(picked.Id, mine.Where(r => r.CreatedAt.Date == d).Sum(r => r.CostUsd)))).ToList();
            return new BillDetail(Summary(picked.Id, picked.Name, picked.Slug), lines, daily);
        }

        var unattributed = rows.Where(r => r.Company == null).ToList();
        return (new BillReport(
            from.ToString("yyyy-MM", CultureInfo.InvariantCulture), from, to, settings.Currency, manager,
            companies.Select(c => Summary(c.Id, c.Name, c.Slug)).ToList(),
            Detail(),
            manager ? new BillUnattributed(unattributed.Count, unattributed.Sum(r => r.Tokens),
                Math.Round(unattributed.Sum(r => r.CostUsd), 4)) : null,
            manager ? settings.UsdRate : null), null);
    }

    private static string NormalizeLang(string? lang) =>
        (lang ?? "").Trim().ToLowerInvariant() is "ms" or "zh" ? lang!.Trim().ToLowerInvariant() : "en";

    /// <summary>A slug as a file-name part: letters, digits, '-' and '_' only.</summary>
    private static string SafeFile(string s)
    {
        var clean = new string(s.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
        return clean.Length > 0 ? clean[..Math.Min(clean.Length, 60)] : "company";
    }

    private sealed record BillRow(int? Company, string Feature, DateTime CreatedAt, long Tokens, double CostUsd);

    /// <summary>Company ids the caller may see; null = every company.</summary>
    private static Task<HashSet<int>?> VisibleCompanies(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        bool manager, Func<int?, int?> companyOf)
    {
        if (manager || ShellScope.RoleOf(me) == Roles.SuperAdmin) return Task.FromResult<HashSet<int>?>(null);
        var assigned = ContactsEndpoints.AssignedEntityIds(db, me.Identity?.Name);
        if (assigned.Count == 0) return Task.FromResult<HashSet<int>?>(null);
        return Task.FromResult<HashSet<int>?>(assigned.Select(a => companyOf(a)).OfType<int>().ToHashSet());
    }

    private static bool TryMonth(string? month, out DateTime from)
    {
        var now = DateTime.UtcNow;
        from = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        if (string.IsNullOrWhiteSpace(month)) return true;
        if (!DateTime.TryParseExact(month.Trim(), "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        // a month far outside the product's life (9999-12 would overflow "next month")
        if (parsed.Year is < 2000 or > 2200) return false;
        from = new DateTime(parsed.Year, parsed.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }

    private static bool Ok(double markup) => markup is >= 0 and <= 10000;
    private static bool OkFee(double fee) => fee is >= 0 and <= 1_000_000;
    private static IResult Bad(string detail) => Results.BadRequest(new { detail });
}
