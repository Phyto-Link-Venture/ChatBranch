using System.Text.Json;
using ChatBranch.Data.Ai.Skills;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The Jint sandbox code skills run in: results come back as JSON, every fault
/// (syntax, runtime, limits) comes back as DATA, and the sandbox exposes no
/// host: no CLR, no eval, no require/fetch.
/// </summary>
public class SkillRuntimeTests
{
    private static readonly TimeZoneInfo Kl = BuiltinSkills.ResolveTimeZone("Asia/Kuala_Lumpur")!;

    // Thursday 2026-10-08 14:30 in Kuala Lumpur (UTC+8)
    private static SkillContext Ctx() => new()
    {
        UtcNow = new DateTimeOffset(2026, 10, 8, 6, 30, 0, TimeSpan.Zero),
        TimeZone = Kl,
        Channel = "whatsapp",
        LastMessage = "hi",
        Contact = new Dictionary<string, object?> { ["name"] = "Aisha Rahman", ["tags"] = new[] { "vip" } },
    };

    [Fact]
    public void Tool_returns_json_and_sees_the_context()
    {
        var r = SkillRuntime.RunTool("""
            function run(args, ctx) {
              return { n: args.n * 2, day: ctx.weekday, time: ctx.time, today: ctx.today, who: ctx.contact.name,
                       tz: ctx.timezone, plus3: dates.addBusinessDays(ctx.today, 3), week: dates.isoWeek(ctx.today).week };
            }
            """, "{\"n\":21}", Ctx());
        Assert.True(r.Ok, r.Error);
        var j = JsonDocument.Parse(r.Json).RootElement;
        Assert.Equal(42, j.GetProperty("n").GetInt32());
        Assert.Equal("Thursday", j.GetProperty("day").GetString());
        Assert.Equal("14:30", j.GetProperty("time").GetString());
        Assert.Equal("2026-10-08", j.GetProperty("today").GetString());
        Assert.Equal("Aisha Rahman", j.GetProperty("who").GetString());
        Assert.Equal("2026-10-13", j.GetProperty("plus3").GetString());
        Assert.Equal(41, j.GetProperty("week").GetInt32());
    }

    [Fact]
    public void Js_date_uses_the_fixed_clock_and_the_org_timezone()
    {
        var r = SkillRuntime.RunTool(
            "function run(){ const d = new Date(); return { iso: d.toISOString(), hour: d.getHours() }; }", "{}", Ctx());
        Assert.True(r.Ok, r.Error);
        var j = JsonDocument.Parse(r.Json).RootElement;
        Assert.Equal("2026-10-08T06:30:00.000Z", j.GetProperty("iso").GetString());
        Assert.Equal(14, j.GetProperty("hour").GetInt32());
    }

    [Fact]
    public void Export_keyword_and_module_exports_are_accepted()
    {
        Assert.True(SkillRuntime.RunTool("export function run() { return 1; }", "{}", Ctx()).Ok);
        Assert.True(SkillRuntime.RunTool("module.exports = { run: function () { return 2; } };", "{}", Ctx()).Ok);
    }

    [Fact]
    public void An_infinite_loop_is_stopped_quickly()
    {
        var r = SkillRuntime.RunTool("function run() { while (true) {} }", "{}", Ctx());
        Assert.False(r.Ok);
        Assert.Contains(r.ErrorKind, new[] { "timeout", "statements" });
        Assert.True(r.Ms < 3000, $"took {r.Ms} ms");
    }

    [Fact]
    public void Memory_is_capped()
    {
        var r = SkillRuntime.RunTool("""
            function run() {
              const keep = [];
              for (let i = 0; i < 90000; i++) keep.push({ i: i, s: 'x'.repeat(2000) + i });
              return keep.length;
            }
            """, "{}", Ctx());
        Assert.False(r.Ok);
        Assert.Equal("memory", r.ErrorKind);
    }

    [Fact]
    public void Deep_recursion_is_capped()
    {
        var r = SkillRuntime.RunTool("function f(n) { return f(n + 1) + 1; } function run() { return f(0); }", "{}", Ctx());
        Assert.False(r.Ok);
        Assert.Equal("recursion", r.ErrorKind);
    }

    [Fact]
    public void No_host_access_no_clr_no_eval()
    {
        var probe = SkillRuntime.RunTool("""
            function run() {
              return [typeof System, typeof importNamespace, typeof require, typeof fetch,
                      typeof XMLHttpRequest, typeof setTimeout, typeof process].join('|');
            }
            """, "{}", Ctx());
        Assert.True(probe.Ok, probe.Error);
        Assert.Equal("\"undefined|undefined|undefined|undefined|undefined|undefined|undefined\"", probe.Json);

        var clr = SkillRuntime.RunTool("function run() { return importNamespace('System.IO').File.ReadAllText('/etc/hostname'); }",
            "{}", Ctx());
        Assert.False(clr.Ok);
        Assert.Equal("runtime", clr.ErrorKind);

        var ev = SkillRuntime.RunTool("function run() { return eval('1 + 1'); }", "{}", Ctx());
        Assert.False(ev.Ok);
        var fn = SkillRuntime.RunTool("function run() { return new Function('return 1')(); }", "{}", Ctx());
        Assert.False(fn.Ok);
    }

    [Fact]
    public void Syntax_and_runtime_errors_come_back_as_data()
    {
        var syntax = SkillRuntime.RunTool("function run( { return 1 }", "{}", Ctx());
        Assert.False(syntax.Ok);
        Assert.Equal("syntax", syntax.ErrorKind);
        Assert.NotNull(SkillRuntime.Validate("function run( {", "tool"));

        var thrown = SkillRuntime.RunTool("function run() { throw new Error('nope'); }", "{}", Ctx());
        Assert.False(thrown.Ok);
        Assert.Equal("runtime", thrown.ErrorKind);
        Assert.Contains("nope", thrown.Error);

        Assert.NotNull(SkillRuntime.Validate("function helper() {}", "check")); // no check()
        Assert.Null(SkillRuntime.Validate("function check(reply, ctx) { return true; }", "check"));
    }

    [Fact]
    public void Checks_accept_objects_and_boolean_shorthand()
    {
        const string code = """
            function check(reply, ctx) {
              if (/guarantee/i.test(reply)) return { ok: false, reason: 'No guarantees.', fix: 'Say "we aim to".' };
              return reply.length > 0;
            }
            """;
        var bad = SkillRuntime.RunCheck(code, "We guarantee it", Ctx());
        Assert.False(bad.Ok);
        Assert.False(bad.Errored);
        Assert.Equal("No guarantees.", bad.Reason);
        Assert.Equal("Say \"we aim to\".", bad.Fix);
        Assert.True(SkillRuntime.RunCheck(code, "We aim to deliver Friday", Ctx()).Ok);

        var odd = SkillRuntime.RunCheck("function check() { return 'maybe'; }", "x", Ctx());
        Assert.True(odd.Errored);
        Assert.Equal("invalid", odd.ErrorKind);
    }

    [Fact]
    public void A_runaway_regex_is_stopped()
    {
        var r = SkillRuntime.RunCheck("function check(reply) { return { ok: !/^(a+)+$/.test(reply) }; }",
            new string('a', 40) + "!", Ctx());
        Assert.True(r.Errored);
    }
}
