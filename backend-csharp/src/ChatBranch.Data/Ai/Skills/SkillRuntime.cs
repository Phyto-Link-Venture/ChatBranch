using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Runtime;

namespace ChatBranch.Data.Ai.Skills;

/// <summary>
/// The JavaScript sandbox code skills run in (Jint — a pure .NET interpreter,
/// no V8, no process). Every run gets a FRESH engine with:
///
///  * no CLR interop (no <c>System</c>, no <c>importNamespace</c>, no host
///    objects — only strings cross the boundary, as JSON);
///  * strict mode, no <c>eval</c> / <c>new Function</c>;
///  * a wall-clock timeout, a memory cap, a statement budget, a recursion cap,
///    a regex timeout and an array-size cap;
///  * no network, file, module or timer APIs (Jint ships none of them).
///
/// A tool skill defines <c>function run(args, ctx)</c> returning anything
/// JSON-serialisable; a check skill defines <c>function check(reply, ctx)</c>
/// returning <c>{ ok: true }</c> or <c>{ ok: false, reason, fix? }</c>.
/// Nothing here throws: syntax errors, runtime errors and limit breaches all
/// come back as data (<see cref="RunResult.ErrorKind"/>).
/// </summary>
public static class SkillRuntime
{
    public sealed record Limits(TimeSpan Timeout, long MemoryBytes, int MaxStatements, int MaxRecursion)
    {
        public static readonly Limits Default =
            new(TimeSpan.FromMilliseconds(250), 8L * 1024 * 1024, 5_000_000, 64);
    }

    /// <summary>Outcome of one sandboxed call. <see cref="Json"/> is the
    /// JSON-serialised return value ("null" when nothing came back).</summary>
    public sealed record RunResult(bool Ok, string Json, string? Error, string? ErrorKind, double Ms);

    /// <summary>Outcome of one reply check. <see cref="Errored"/> means the
    /// check itself failed to run — the reply was NOT judged.</summary>
    public sealed record CheckResult(bool Ok, string Reason, string Fix, string? Error, string? ErrorKind, double Ms)
    {
        public bool Errored => Error != null;
    }

    public const int MaxCodeLength = 20_000;

    /// <summary>Run a tool skill: <c>run(args, ctx)</c>.</summary>
    public static RunResult RunTool(string code, string? argsJson, SkillContext ctx, Limits? limits = null)
    {
        var args = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!;
        try { using var _ = JsonDocument.Parse(args); }
        catch { return new RunResult(false, "null", "Arguments are not valid JSON.", "invalid", 0); }
        return Invoke(code, "run", args, ctx, limits);
    }

    /// <summary>Run a check skill against a draft reply: <c>check(reply, ctx)</c>.</summary>
    public static CheckResult RunCheck(string code, string reply, SkillContext ctx, Limits? limits = null)
    {
        var r = Invoke(code, "check", JsonSerializer.Serialize(reply ?? ""), ctx, limits);
        if (!r.Ok) return new CheckResult(false, "", "", r.Error, r.ErrorKind, r.Ms);
        return Interpret(r.Json, r.Ms);
    }

    /// <summary>
    /// Save-time validation: the code parses and defines the entry point its
    /// kind needs. Returns null when fine, else an operator-readable reason.
    /// </summary>
    public static string? Validate(string? code, string kind)
    {
        if (string.IsNullOrWhiteSpace(code)) return "The skill has no code.";
        if (code.Length > MaxCodeLength) return $"The code is longer than {MaxCodeLength:N0} characters.";
        var entry = kind == Domain.SkillKinds.Check ? "check" : "run";
        _ = Warm.Value;
        try
        {
            var engine = NewEngine(new SkillContext(), Limits.Default);
            engine.Execute(Prelude, "prelude.js");
            engine.Execute(Normalize(code), "skill.js");
            var ok = engine.Evaluate(
                $"typeof {entry} === 'function' || (typeof module === 'object' && module.exports && typeof module.exports.{entry} === 'function')")
                .AsBoolean();
            return ok ? null : $"The code must define function {entry}({(entry == "check" ? "reply" : "args")}, ctx).";
        }
        catch (Exception ex)
        {
            var (kind2, msg) = Classify(ex, Limits.Default);
            msg = Explain(msg);
            return kind2 == "syntax" ? $"Syntax error: {msg}" : msg;
        }
    }

    // ---- internals ----------------------------------------------------------

    /// <summary>
    /// One throwaway run with a generous budget the first time the sandbox is
    /// used in a process: Jint's own JIT warm-up (hundreds of ms on a cold,
    /// busy machine) must not be billed to the first skill's 250 ms.
    /// </summary>
    private static readonly Lazy<bool> Warm = new(() =>
    {
        try
        {
            var engine = NewEngine(new SkillContext(), Limits.Default with { Timeout = TimeSpan.FromSeconds(10), MemoryBytes = 64L * 1024 * 1024 });
            engine.SetValue("__ctx_json", "{}");
            engine.Execute(Prelude, "prelude.js");
            engine.Execute("function run(a, c) { return [dates.addDays('2026-01-01', 1), /x/.test('x'), JSON.stringify({ a: 1 })]; }", "warm.js");
            engine.Evaluate("JSON.stringify(run({}, JSON.parse(__ctx_json)))");
        }
        catch { /* warm-up is best effort */ }
        return true;
    });

    private static RunResult Invoke(string code, string entry, string argJson, SkillContext ctx, Limits? limits)
    {
        _ = Warm.Value;
        limits ??= Limits.Default;
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(code))
            return new RunResult(false, "null", "The skill has no code.", "invalid", 0);
        if (code.Length > MaxCodeLength)
            return new RunResult(false, "null", $"The code is longer than {MaxCodeLength:N0} characters.", "invalid", 0);
        try
        {
            var engine = NewEngine(ctx, limits);
            engine.SetValue("__ctx_json", JsonSerializer.Serialize(ctx.ToDictionary()));
            engine.SetValue("__arg_json", argJson);
            engine.Execute(Prelude, "prelude.js");
            engine.Execute(Normalize(code), "skill.js");
            var json = engine.Evaluate($$"""
                (function () {
                  var fn = typeof {{entry}} === 'function' ? {{entry}}
                    : (typeof module === 'object' && module.exports && typeof module.exports.{{entry}} === 'function'
                        ? module.exports.{{entry}} : null);
                  if (!fn) throw new Error('The code must define function {{entry}}(...).');
                  var out = fn(JSON.parse(__arg_json), JSON.parse(__ctx_json));
                  if (out === undefined) return 'null';
                  var s = JSON.stringify(out);
                  return s === undefined ? 'null' : s;
                })()
                """, "invoke.js").AsString();
            return new RunResult(true, json, null, null, Ms(sw));
        }
        catch (Exception ex)
        {
            var (kind, msg) = Classify(ex, limits);
            return new RunResult(false, "null", msg, kind, Ms(sw));
        }
    }

    private static Engine NewEngine(SkillContext ctx, Limits limits) => new(o =>
    {
        o.Strict();
        o.TimeoutInterval(limits.Timeout);
        o.LimitMemory(limits.MemoryBytes);
        o.MaxStatements(limits.MaxStatements);
        o.LimitRecursion(limits.MaxRecursion);
        o.DisableStringCompilation();
        o.Interop.Enabled = false;
        o.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(100);
        o.Constraints.MaxArraySize = 100_000;
        // `new Date()` inside a skill agrees with ctx.now (and a test's fixed
        // clock), and local-time getters answer in the organization's zone.
        o.TimeZone = ctx.TimeZone;
        o.TimeSystem = new FixedClock(ctx.UtcNow, ctx.TimeZone);
    });

    /// <summary>Interpret a check's return value. Accepts true/false as shorthand.</summary>
    private static CheckResult Interpret(string json, double ms)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.True) return new CheckResult(true, "", "", null, null, ms);
            if (root.ValueKind == JsonValueKind.False)
                return new CheckResult(false, "The reply failed this check.", "", null, null, ms);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok)
                && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var reason = Str(root, "reason");
                var fix = Str(root, "fix");
                return ok.ValueKind == JsonValueKind.True
                    ? new CheckResult(true, reason, fix, null, null, ms)
                    : new CheckResult(false, reason.Length > 0 ? reason : "The reply failed this check.", fix, null, null, ms);
            }
        }
        catch { /* fall through */ }
        return new CheckResult(false, "", "",
            "check() must return { ok: true } or { ok: false, reason: '…' }.", "invalid", ms);
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static (string kind, string message) Classify(Exception ex, Limits limits)
    {
        var name = ex.GetType().Name;
        if (ex is TimeoutException || name.Contains("Timeout"))
            return ("timeout", $"Stopped: the code ran longer than {limits.Timeout.TotalMilliseconds:0} ms.");
        if (name.Contains("MemoryLimit"))
            return ("memory", $"Stopped: the code used more than {limits.MemoryBytes / (1024 * 1024)} MB of memory.");
        if (name.Contains("StatementsCount"))
            return ("statements", "Stopped: the code ran too many statements (an endless loop?).");
        if (name.Contains("RecursionDepth"))
            return ("recursion", "Stopped: the code recursed too deeply.");
        if (name.Contains("Parse") || name.Contains("Syntax"))
            return ("syntax", ex.Message);
        if (ex is JavaScriptException js)
        {
            var msg = js.Message;
            var line = js.Location.Start.Line;
            var where = line > 0 && js.Location.SourceFile == "skill.js" ? $" (line {line})" : "";
            var errName = js.Error is Jint.Native.Object.ObjectInstance o ? o.Get("name").ToString() : "";
            var kind = errName == "SyntaxError" || msg.StartsWith("SyntaxError", StringComparison.Ordinal)
                ? "syntax" : "runtime";
            return (kind, Explain(msg) + where);
        }
        if (ex is RegexMatchTimeoutException) return ("timeout", "Stopped: a regular expression took too long.");
        return ("runtime", ex.Message);
    }

    /// <summary>What a skill reaches for when it tries something the sandbox can never do.</summary>
    private static readonly Regex Impossible = new(
        @"\b(fetch|XMLHttpRequest|WebSocket|require|process|fs|Buffer|jsPDF|PDFDocument|pdfkit|PDFLib|QRCode|"
        + @"window|document|navigator|localStorage|sessionStorage|setTimeout|setInterval|FileReader|Blob)\b is not defined",
        RegexOptions.Compiled);

    /// <summary>
    /// The sandbox's limits, said plainly next to the engine's error: a skill that
    /// calls <c>fetch</c>, <c>require('fs')</c> or a PDF library gets "X is not
    /// defined" — this adds WHY and what to use instead.
    /// </summary>
    public const string SandboxLimits =
        "Code skills run in a sandbox with no internet, no files and no browser or Node.js libraries (250 ms), "
        + "so they can't make a PDF, an image or a web request. Use a ready-made Connected app instead "
        + "(Documents for PDF invoices / quotations / receipts / letters, QR codes, Payment links, Google Sheets log).";

    public static string Explain(string message)
    {
        if (Impossible.IsMatch(message ?? "")
            || (message ?? "").Contains("Cannot use import", StringComparison.OrdinalIgnoreCase)
            || (message ?? "").Contains("import declarations", StringComparison.OrdinalIgnoreCase))
            return $"{message} — {SandboxLimits}";
        return message ?? "";
    }

    private static double Ms(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 1);

    private static readonly Regex ExportRe = new(@"^(\s*)export\s+(default\s+)?", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Accept `export function run…` / `export default function check…`
    /// (people paste module-style code) by dropping the export keyword.</summary>
    private static string Normalize(string code) => ExportRe.Replace(code, "$1");

    /// <summary>
    /// Trusted helpers every skill can use: <c>dates.*</c> on plain
    /// "YYYY-MM-DD" strings (UTC calendar arithmetic, so no timezone surprises)
    /// and a CommonJS-style <c>module.exports</c> fallback.
    /// </summary>
    internal const string Prelude = """
        globalThis.module = { exports: {} };
        globalThis.dates = (function () {
          var DAY = 86400000;
          var NAMES = ['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'];
          function pad(n) { return (n < 10 ? '0' : '') + n; }
          function parse(d) {
            if (d instanceof Date) return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()));
            var m = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(d));
            if (!m) throw new Error('dates: expected YYYY-MM-DD, got ' + d);
            return new Date(Date.UTC(+m[1], +m[2] - 1, +m[3]));
          }
          function fmt(d) { return d.getUTCFullYear() + '-' + pad(d.getUTCMonth() + 1) + '-' + pad(d.getUTCDate()); }
          function isoWeekday(d) { var w = parse(d).getUTCDay(); return w === 0 ? 7 : w; }
          function addDays(d, n) { return fmt(new Date(parse(d).getTime() + Math.round(n) * DAY)); }
          function isWeekend(d) { return isoWeekday(d) >= 6; }
          function addBusinessDays(d, n) {
            var cur = fmt(parse(d)), step = n < 0 ? -1 : 1, left = Math.abs(Math.round(n));
            while (left > 0) { cur = addDays(cur, step); if (!isWeekend(cur)) left--; }
            return cur;
          }
          function daysBetween(a, b) { return Math.round((parse(b).getTime() - parse(a).getTime()) / DAY); }
          function isoWeek(d) {
            var t = parse(d), wd = isoWeekday(fmt(t));
            var thursday = new Date(t.getTime() + (4 - wd) * DAY);
            var year = thursday.getUTCFullYear();
            var jan1 = Date.UTC(year, 0, 1);
            return { year: year, week: Math.floor((thursday.getTime() - jan1) / DAY / 7) + 1 };
          }
          function weekStart(d) { return addDays(d, 1 - isoWeekday(d)); }
          function weekEnd(d) { return addDays(d, 7 - isoWeekday(d)); }
          function weekday(d) { return NAMES[parse(d).getUTCDay()]; }
          function minutes(hhmm) {
            var m = /^(\d{1,2}):(\d{2})/.exec(String(hhmm));
            if (!m) throw new Error('dates: expected HH:mm, got ' + hhmm);
            return (+m[1]) * 60 + (+m[2]);
          }
          return Object.freeze({
            parse: parse, format: fmt, addDays: addDays, addBusinessDays: addBusinessDays,
            daysBetween: daysBetween, isoWeek: isoWeek, isoWeekday: isoWeekday,
            weekday: weekday, weekStart: weekStart, weekEnd: weekEnd, isWeekend: isWeekend,
            minutes: minutes, WEEKDAYS: NAMES.slice()
          });
        })();
        """;

    /// <summary>Jint's clock: a fixed "now" (the reply's, or a test's) in the org zone.</summary>
    private sealed class FixedClock : ITimeSystem
    {
        private readonly DateTimeOffset _now;
        private readonly DefaultTimeSystem _inner;

        public FixedClock(DateTimeOffset now, TimeZoneInfo tz)
        {
            _now = now;
            _inner = new DefaultTimeSystem(tz, CultureInfo.InvariantCulture);
        }

        public DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
        public TimeZoneInfo DefaultTimeZone => _inner.DefaultTimeZone;
        public bool TryParse(string date, out long epochMilliseconds) => _inner.TryParse(date, out epochMilliseconds);
        public TimeSpan GetUtcOffset(long epochMilliseconds) => _inner.GetUtcOffset(epochMilliseconds);
    }
}
