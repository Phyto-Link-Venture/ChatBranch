using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Bookings;
using ChatBranch.Data.Documents;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Ai.Skills;

/// <summary>
/// Everything the AI needs from the skill library at reply time: which skills
/// are in scope (entity + channel + enabled), the tool list the model is
/// offered (built-ins + tool skills), executing a call the model made, running
/// reply checks, and running a skill's own test cases. The ONE place these
/// rules live — chat replies, flow messages and follow-ups all come through here.
/// </summary>
public class SkillService
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly TimeProvider _clock;
    private readonly BookingService _bookings;
    private readonly DocumentService _documents;
    private readonly ConnectedAppTools _connected;

    public SkillService(AppDbContext db, ScopingService scoping, TimeProvider? clock = null,
        BookingService? bookings = null, DocumentService? documents = null, ConnectedAppTools? connected = null)
    {
        _db = db;
        _scoping = scoping;
        _clock = clock ?? TimeProvider.System;
        _bookings = bookings ?? new BookingService(db, scoping, clock: _clock);
        _documents = documents ?? new DocumentService(db, scoping, clock: _clock);
        _connected = connected ?? new ConnectedAppTools(db, scoping);
    }

    /// <summary>The calendar booking service app tools run on (shared with the AI gate in DI).</summary>
    public BookingService Bookings => _bookings;

    /// <summary>The Documents / QR app tools (files made for a reply, sent at delivery).</summary>
    public DocumentService Documents => _documents;

    /// <summary>The Payments / Sheets app tools.</summary>
    public ConnectedAppTools Connected => _connected;

    public DateTimeOffset UtcNow => _clock.GetUtcNow();

    public TimeZoneInfo OrgTimeZone() => BuiltinSkills.OrgTimeZone(_scoping);

    // ---- scope -----------------------------------------------------------------

    /// <summary>
    /// Enabled skills that apply to a conversation: the organization-wide ones
    /// plus the entity's own (ScopeInherited), filtered by channel (empty list =
    /// every channel). A conversation with NO entity sees only the
    /// organization-wide skills — never another outlet's.
    /// </summary>
    public List<AgentSkill> InScope(int? entityId, string? channel)
    {
        try
        {
            IQueryable<AgentSkill> q = _db.AgentSkills.AsNoTracking().Where(s => s.Enabled);
            q = entityId is int ? _scoping.ScopeInherited(q, entityId) : q.Where(s => s.EntityId == null);
            var rows = q.OrderBy(s => s.Position).ThenBy(s => s.Id).ToList();
            var ch = (channel ?? "").Trim().ToLowerInvariant();
            return rows.Where(s => s.Channels.Count == 0 || ch.Length == 0
                                   || s.Channels.Any(c => c.Trim().ToLowerInvariant() == ch)).ToList();
        }
        catch { return new List<AgentSkill>(); }
    }

    /// <summary>The sandbox context for a reply.</summary>
    public SkillContext Context(Contact? contact, string? channel, string? lastMessage) => new()
    {
        UtcNow = UtcNow,
        TimeZone = OrgTimeZone(),
        Channel = channel ?? "",
        LastMessage = lastMessage ?? "",
        Contact = SkillContext.ContactView(contact),
    };

    /// <summary>The context for a reply in a conversation — what app tools need
    /// (which conversation, contact, outlet and assignee) on top of the sandbox view.</summary>
    public SkillContext Context(Contact? contact, string? channel, string? lastMessage, int? threadId,
        int? entityId, string? assignee) => new()
    {
        UtcNow = UtcNow,
        TimeZone = OrgTimeZone(),
        Channel = channel ?? "",
        LastMessage = lastMessage ?? "",
        Contact = SkillContext.ContactView(contact),
        ThreadId = threadId,
        ContactId = contact?.Id,
        ContactRecord = contact,
        EntityId = entityId,
        Assignee = assignee ?? "",
    };

    // ---- tools -------------------------------------------------------------------

    public sealed class ToolSet
    {
        public List<LlmTool> Tools { get; } = new();
        public Dictionary<string, AgentSkill> Skills { get; } = new();
        /// <summary>App tools (connected-app skills): tool name → what it runs.</summary>
        public Dictionary<string, BookingService.ToolBinding> Apps { get; } = new();
        /// <summary>App skill SkillId → the base name of its tools ("calendar", "calendar_2"…).</summary>
        public Dictionary<string, string> AppBases { get; } = new();
        /// <summary>Documents / QR app tools: tool name → what it makes.</summary>
        public Dictionary<string, DocumentService.ToolBinding> Files { get; } = new();
        /// <summary>Payments / Sheets app tools: tool name → what it runs.</summary>
        public Dictionary<string, ConnectedAppTools.ToolBinding> Connected { get; } = new();
    }

    /// <summary>The first free base name: "document", then "document_2", "document_3"…</summary>
    private static string FreeBase(string stem, HashSet<string> taken, Func<string, IEnumerable<string>> namesFor)
    {
        var b = stem;
        for (var i = 2; namesFor(b).Any(taken.Contains); i++) b = $"{stem}_{i}";
        return b;
    }

    /// <summary>Built-ins + the enabled tool skills + the enabled app skills given, with unique names.</summary>
    public ToolSet BuildTools(IEnumerable<AgentSkill> skills)
    {
        var set = new ToolSet();
        set.Tools.AddRange(BuiltinSkills.Tools());
        var taken = new HashSet<string>(set.Tools.Select(t => t.Name));
        var list = skills.ToList();
        foreach (var s in list.Where(s => s.Kind == SkillKinds.Tool && !string.IsNullOrWhiteSpace(s.Code)))
        {
            var name = ToolName(s);
            for (var i = 2; taken.Contains(name); i++) name = $"{ToolName(s)}_{i}";
            taken.Add(name);
            set.Skills[name] = s;
            set.Tools.Add(new LlmTool(name, ToolDescription(s), ParameterSchema(s.Parameters)));
        }
        foreach (var s in list.Where(s => s.Kind == SkillKinds.App && s.Enabled))
        {
            var app = AppSkills.AppOf(s.Config);
            if (app != AppSkills.Calendar)
            {
                try { AddAppTools(set, taken, s, app); }
                catch { /* a broken app config offers no tools rather than breaking the reply */ }
                continue;
            }
            try
            {
                var cfg = CalendarAppConfig.From(s.Config);
                if (cfg.App != CalendarAppConfig.AppId || cfg.AllowedActions.Count == 0) continue;
                var tz = _bookings.TimeZoneFor(cfg);
                var baseName = "calendar";
                for (var i = 2; BookingService.ToolDefinitions(baseName, cfg, tz.Id).Any(t => taken.Contains(t.Name)); i++)
                    baseName = $"calendar_{i}";
                set.AppBases[s.SkillId] = baseName;
                foreach (var (name, action, tool) in BookingService.ToolDefinitions(baseName, cfg, tz.Id))
                {
                    taken.Add(name);
                    set.Apps[name] = new BookingService.ToolBinding(s, cfg, action, baseName);
                    set.Tools.Add(tool);
                }
            }
            catch { /* a broken app config offers no tools rather than breaking the reply */ }
        }
        return set;
    }

    /// <summary>The tools of one non-calendar app skill (documents, qr, payments, sheets).</summary>
    private void AddAppTools(ToolSet set, HashSet<string> taken, AgentSkill s, string app)
    {
        switch (app)
        {
            case AppSkills.Documents:
            {
                var cfg = DocumentAppConfig.From(s.Config);
                if (cfg.AllowedActions.Count == 0) return;
                var b = FreeBase("document", taken, x => DocumentService.ToolDefinitions(x, cfg).Select(t => t.Name));
                set.AppBases[s.SkillId] = b;
                foreach (var (name, action, tool) in DocumentService.ToolDefinitions(b, cfg))
                {
                    taken.Add(name);
                    set.Files[name] = new DocumentService.ToolBinding(s, app, action, b, cfg, null);
                    set.Tools.Add(tool);
                }
                break;
            }
            case AppSkills.Qr:
            {
                var cfg = QrAppConfig.From(s.Config);
                var b = FreeBase("qr", taken, x => DocumentService.QrToolDefinitions(x).Select(t => t.Name));
                set.AppBases[s.SkillId] = b;
                foreach (var (name, action, tool) in DocumentService.QrToolDefinitions(b))
                {
                    taken.Add(name);
                    set.Files[name] = new DocumentService.ToolBinding(s, app, action, b, null, cfg);
                    set.Tools.Add(tool);
                }
                break;
            }
            case AppSkills.Payments:
            {
                var cfg = PaymentAppConfig.From(s.Config);
                var b = FreeBase("payment", taken, x => ConnectedAppTools.PaymentToolDefinitions(x, cfg).Select(t => t.Name));
                set.AppBases[s.SkillId] = b;
                foreach (var (name, action, tool) in ConnectedAppTools.PaymentToolDefinitions(b, cfg))
                {
                    taken.Add(name);
                    set.Connected[name] = new ConnectedAppTools.ToolBinding(s, app, action, b, cfg, null);
                    set.Tools.Add(tool);
                }
                break;
            }
            case AppSkills.Sheets:
            {
                var cfg = SheetsAppConfig.From(s.Config);
                var b = FreeBase("sheets", taken, x => ConnectedAppTools.SheetsToolDefinitions(x, cfg).Select(t => t.Name));
                set.AppBases[s.SkillId] = b;
                foreach (var (name, action, tool) in ConnectedAppTools.SheetsToolDefinitions(b, cfg))
                {
                    taken.Add(name);
                    set.Connected[name] = new ConnectedAppTools.ToolBinding(s, app, action, b, null, cfg);
                    set.Tools.Add(tool);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Run one call the model made — app tools (calendar, documents, QR, payments,
    /// sheets) asynchronously, every other tool through <see cref="ExecuteTool"/>.
    /// Never throws.
    /// </summary>
    public async Task<(string result, bool ok)> ExecuteToolAsync(ToolSet set, LlmToolCall call, SkillContext ctx,
        CancellationToken ct = default)
    {
        // side-effect tools are capped per conversation per 24 h (AiActionCaps) — a scripted
        // "customer" can't farm invoices, payment links or bookings out of the chat AI
        if (set.Files.TryGetValue(call.Name, out var file))
        {
            if (AiActionCaps.Refusal(_db, AiActionCaps.Documents, ctx.ThreadId, ctx.Docs.Preview, ctx.UtcNow) is { } capped)
                return (capped, false);
            try
            {
                var r = await _documents.ExecuteToolAsync(file, call.ArgumentsJson, ctx, ct);
                return (r.Json, r.Ok);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return ($"error: {ex.Message}", false); }
        }
        if (set.Connected.TryGetValue(call.Name, out var connected))
        {
            if (connected.App == AppSkills.Payments
                && AiActionCaps.Refusal(_db, AiActionCaps.PaymentLinks, ctx.ThreadId, ctx.Docs.Preview, ctx.UtcNow) is { } capped)
                return (capped, false);
            try { return await _connected.ExecuteAsync(connected, call.ArgumentsJson, ctx, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return ($"error: {ex.Message}", false); }
        }
        if (!set.Apps.TryGetValue(call.Name, out var binding)) return ExecuteTool(set, call, ctx);
        if (binding.Action == "book"
            && AiActionCaps.Refusal(_db, AiActionCaps.Bookings, ctx.ThreadId, ctx.Apps.Preview, ctx.UtcNow) is { } cappedBooking)
            return (cappedBooking, false);
        try
        {
            var r = await _bookings.ExecuteToolAsync(binding, call.ArgumentsJson, ctx, ct);
            return (r.Json, r.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return ($"error: {ex.Message}", false);
        }
    }

    /// <summary>
    /// Run one call the model made. Unknown tools and sandbox faults come back as
    /// "error: …" data (the tool loop feeds that to the model) — never thrown.
    /// </summary>
    public (string result, bool ok) ExecuteTool(ToolSet set, LlmToolCall call, SkillContext ctx)
    {
        try
        {
            if (BuiltinSkills.IsBuiltin(call.Name))
                return (BuiltinSkills.Execute(call.Name, call.ArgumentsJson, ctx.UtcNow, ctx.TimeZone), true);
            if (!set.Skills.TryGetValue(call.Name, out var skill))
                return ($"error: unknown tool '{call.Name}'", false);
            var r = SkillRuntime.RunTool(skill.Code, call.ArgumentsJson, ctx);
            return r.Ok ? (Cap(r.Json, 4000), true) : ($"error: {r.Error}", false);
        }
        catch (Exception ex)
        {
            return ($"error: {ex.Message}", false);
        }
    }

    // ---- checks ------------------------------------------------------------------

    public sealed record CheckRun(string SkillId, string Name, bool Ok, string Reason, string Fix,
        string? Error, double Ms, string OnFail);

    /// <summary>
    /// Run every enabled check skill on a draft reply. A check that cannot run
    /// (runtime fault, timeout) is recorded with its error and does NOT block
    /// the reply — a broken validator must not silence the assistant; save-time
    /// validation and the skill's own tests are what keep that rare.
    /// </summary>
    public List<CheckRun> RunChecks(IEnumerable<AgentSkill> skills, string reply, SkillContext ctx)
    {
        var runs = new List<CheckRun>();
        foreach (var s in skills.Where(s => s.Kind == SkillKinds.Check && !string.IsNullOrWhiteSpace(s.Code)))
        {
            try
            {
                var r = SkillRuntime.RunCheck(s.Code, reply, ctx);
                runs.Add(new CheckRun(s.SkillId, s.Name, r.Ok || r.Errored, r.Reason, r.Fix, r.Error, r.Ms,
                    NormalizeOnFail(s.OnFail)));
            }
            catch (Exception ex)
            {
                runs.Add(new CheckRun(s.SkillId, s.Name, true, "", "", ex.Message, 0, NormalizeOnFail(s.OnFail)));
            }
        }
        return runs;
    }

    public static string NormalizeOnFail(string? v) => v switch
    {
        "handover" => "handover",
        "block" => "block",
        _ => "retry",
    };

    // ---- tests ---------------------------------------------------------------------

    public sealed record TestResult(string Name, bool Pass, JsonElement? Got, JsonElement? Expected,
        string? Error, double Ms);

    /// <summary>
    /// Run a skill's test cases in the sandbox. Each case: <c>{name, args|input,
    /// expect, now?, timezone?, channel?, contact?, lastMessage?}</c>. A tool's
    /// `expect` is matched as a SUBSET of the result (objects need only carry the
    /// listed keys); a check's `expect` may be a bare true/false (= ok) or an
    /// object matched against <c>{ok, reason, fix}</c>. A case with no `expect`
    /// passes when the code runs without error.
    /// </summary>
    public List<TestResult> RunTests(string kind, string code, JsonElement tests)
    {
        var results = new List<TestResult>();
        if (tests.ValueKind != JsonValueKind.Array) return results;
        var i = 0;
        foreach (var t in tests.EnumerateArray().Take(50))
        {
            i++;
            var name = t.ValueKind == JsonValueKind.Object && t.TryGetProperty("name", out var n)
                       && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } nm
                ? nm : $"Test {i}";
            try
            {
                results.Add(RunOne(kind, code, t, name));
            }
            catch (Exception ex)
            {
                results.Add(new TestResult(name, false, null, null, ex.Message, 0));
            }
        }
        return results;
    }

    private TestResult RunOne(string kind, string code, JsonElement t, string name)
    {
        if (t.ValueKind != JsonValueKind.Object)
            return new TestResult(name, false, null, null, "A test case must be an object.", 0);
        var ctx = TestContext(t);
        JsonElement? expected = t.TryGetProperty("expect", out var e) ? e.Clone() : null;

        if (kind == SkillKinds.Check)
        {
            var reply = t.TryGetProperty("input", out var inp) ? AsText(inp)
                : t.TryGetProperty("reply", out var rp) ? AsText(rp) : "";
            var r = SkillRuntime.RunCheck(code, reply, ctx);
            if (r.Errored) return new TestResult(name, false, null, expected, r.Error, r.Ms);
            var got = JsonSerializer.SerializeToElement(new { ok = r.Ok, reason = r.Reason, fix = r.Fix });
            bool pass = expected switch
            {
                null => true,
                { ValueKind: JsonValueKind.True } => r.Ok,
                { ValueKind: JsonValueKind.False } => !r.Ok,
                JsonElement ex => Matches(ex, got),
            };
            return new TestResult(name, pass, got, expected, null, r.Ms);
        }

        var args = t.TryGetProperty("args", out var a) ? a.GetRawText()
            : t.TryGetProperty("input", out var i2) && i2.ValueKind == JsonValueKind.Object ? i2.GetRawText() : "{}";
        var run = SkillRuntime.RunTool(code, args, ctx);
        if (!run.Ok) return new TestResult(name, false, null, expected, run.Error, run.Ms);
        var value = JsonDocument.Parse(run.Json).RootElement.Clone();
        return new TestResult(name, expected is not JsonElement exp || Matches(exp, value), value, expected, null, run.Ms);
    }

    /// <summary>A test's own clock/zone/contact, defaulting to now in the org zone.</summary>
    private SkillContext TestContext(JsonElement t)
    {
        var tz = (t.TryGetProperty("timezone", out var z) && z.ValueKind == JsonValueKind.String
                     ? BuiltinSkills.ResolveTimeZone(z.GetString()) : null) ?? OrgTimeZone();
        var now = UtcNow;
        if (t.TryGetProperty("now", out var nw) && nw.ValueKind == JsonValueKind.String
            && ParseInstant(nw.GetString() ?? "", tz) is DateTimeOffset fixedNow)
            now = fixedNow;
        var contact = new Dictionary<string, object?>();
        if (t.TryGetProperty("contact", out var c) && c.ValueKind == JsonValueKind.Object)
            contact = JsonSerializer.Deserialize<Dictionary<string, object?>>(c.GetRawText()) ?? new();
        return new SkillContext
        {
            UtcNow = now,
            TimeZone = tz,
            Channel = t.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String ? ch.GetString() ?? "" : "whatsapp",
            LastMessage = t.TryGetProperty("lastMessage", out var lm) ? AsText(lm) : "",
            Contact = contact,
        };
    }

    /// <summary>"2026-10-05T10:00:00+08:00" as given; an offset-less value is
    /// read as wall-clock time in <paramref name="tz"/>.</summary>
    public static DateTimeOffset? ParseInstant(string raw, TimeZoneInfo tz)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return null;
        var hasOffset = raw.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(raw, @"[+-]\d{2}:?\d{2}$");
        if (hasOffset && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            var wall = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            return new DateTimeOffset(wall, tz.GetUtcOffset(wall));
        }
        return null;
    }

    /// <summary>Subset match: every key/element the expectation lists must match.</summary>
    public static bool Matches(JsonElement expected, JsonElement actual)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                if (actual.ValueKind != JsonValueKind.Object) return false;
                foreach (var p in expected.EnumerateObject())
                    if (!actual.TryGetProperty(p.Name, out var av) || !Matches(p.Value, av)) return false;
                return true;
            case JsonValueKind.Array:
                if (actual.ValueKind != JsonValueKind.Array) return false;
                var ea = expected.EnumerateArray().ToList();
                var aa = actual.EnumerateArray().ToList();
                if (ea.Count != aa.Count) return false;
                for (var i = 0; i < ea.Count; i++) if (!Matches(ea[i], aa[i])) return false;
                return true;
            case JsonValueKind.Number:
                return actual.ValueKind == JsonValueKind.Number
                       && Math.Abs(expected.GetDouble() - actual.GetDouble()) < 1e-9;
            case JsonValueKind.String:
                return actual.ValueKind == JsonValueKind.String && expected.GetString() == actual.GetString();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return actual.ValueKind == expected.ValueKind;
            case JsonValueKind.Null:
                return actual.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
            default:
                return true;
        }
    }

    // ---- naming / schema ------------------------------------------------------------

    /// <summary>The function name the model calls: the skill name in snake_case.</summary>
    public static string ToolName(AgentSkill s)
    {
        var slug = Regex.Replace((s.Name ?? "").ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (slug.Length == 0)
            slug = Regex.Replace((s.SkillId ?? "").ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (slug.Length == 0 || char.IsDigit(slug[0])) slug = "skill_" + slug;
        if (slug.Length > 56) slug = slug[..56].TrimEnd('_');
        return BuiltinSkills.IsBuiltin(slug) ? slug + "_custom" : slug;
    }

    private static string ToolDescription(AgentSkill s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.Description)) parts.Add(s.Description.Trim());
        else parts.Add(s.Name);
        if (!string.IsNullOrWhiteSpace(s.UseWhen)) parts.Add($"Use when: {s.UseWhen.Trim()}");
        return Cap(string.Join(" ", parts), 1000);
    }

    /// <summary>A tool's argument schema; anything unusable becomes "no arguments".</summary>
    public static JsonElement ParameterSchema(string? raw)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var el = JsonDocument.Parse(raw).RootElement;
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (el.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String) return el.Clone();
                    // a bare {name: {type…}} map → wrap it as properties
                    var wrapped = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = el.Clone() };
                    return JsonSerializer.SerializeToElement(wrapped);
                }
            }
        }
        catch { /* fall through */ }
        return JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
    }

    private static string AsText(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
