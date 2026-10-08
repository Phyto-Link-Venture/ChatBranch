using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Describe → clarify → code. In sandbox mode the template library must
/// produce WORKING skills — every template's own tests pass in the sandbox —
/// and the clarify step must actually change the code.
/// </summary>
public class SkillDrafterTests
{
    private static SkillDrafter.DraftResult Draft(string description, string? kind = null,
        params (string q, string a)[] answers) =>
        SkillDrafter.Template(description, kind ?? "",
            answers.Select(x => new SkillDrafter.Answer(x.q, x.a)).ToList());

    private static List<SkillService.TestResult> RunTests(SkillDrafter.DraftSkill s)
    {
        using var t = new TestDb();
        var skills = new SkillService(t.Db, new ScopingService(t.Db));
        return skills.RunTests(s.Kind, s.Code, s.Tests);
    }

    [Theory]
    [InlineData("Tell the customer the current time here", "tool")]
    [InlineData("Make sure the AI never says the wrong weekday", "check")]
    [InlineData("What week number is it", "tool")]
    [InlineData("Never mention the wrong week number in a reply", "check")]
    [InlineData("Calculate the delivery date 3 business days from today", "tool")]
    [InlineData("Replies must never promise a date in the past", "check")]
    [InlineData("Normalize a phone number the customer gives", "tool")]
    [InlineData("Make sure replies never leak a phone number", "check")]
    [InlineData("Validate an email address", "tool")]
    [InlineData("Never share outside email addresses", "check")]
    [InlineData("Read the birth date from an IC number", "tool")]
    [InlineData("Never repeat a customer's IC number back to them", "check")]
    [InlineData("Our opening hours are Mon-Fri 9am-6pm, Sat 9-1, Sun closed. Tell people if we're open now", "tool")]
    [InlineData("Outside opening hours (Mon-Fri 9am-6pm, Sun closed) never say we're open", "check")]
    public void Every_template_produces_working_code_whose_tests_pass(string description, string expectedKind)
    {
        var r = Draft(description);
        Assert.Equal("draft", r.Status);
        Assert.Equal("template", r.Source);
        var s = r.Skill!;
        Assert.Equal(expectedKind, s.Kind);
        Assert.Null(SkillRuntime.Validate(s.Code, s.Kind));
        var results = RunTests(s);
        Assert.NotEmpty(results);
        Assert.All(results, x => Assert.True(x.Pass, $"{s.Name} / {x.Name}: {x.Error} got {x.Got}"));
        if (s.Kind == SkillKinds.Check)
            Assert.Contains(s.Tests.EnumerateArray(), c => c.GetProperty("expect").ValueKind == JsonValueKind.False);
    }

    [Fact]
    public void Opening_hours_ask_first_then_code_the_answer()
    {
        var first = Draft("Tell customers whether we're open right now");
        Assert.Equal("questions", first.Status);
        Assert.Single(first.Questions);

        var second = Draft("Tell customers whether we're open right now", null,
            (first.Questions[0], "Monday to Friday 10am to 7pm, Saturday 10-2, Sunday closed"));
        Assert.Equal("draft", second.Status);
        var code = second.Skill!.Code;
        Assert.Contains("Monday: ['10:00', '19:00']", code);
        Assert.Contains("Saturday: ['10:00', '14:00']", code);
        Assert.Contains("Sunday: null", code);
        Assert.All(RunTests(second.Skill), x => Assert.True(x.Pass, $"{x.Name}: {x.Error} {x.Got}"));
    }

    [Fact]
    public void Hours_parser_reads_common_shapes()
    {
        var h = SkillDrafter.ParseHours("Mon-Thu 08:30-17:00; Fri 8.30-12; weekends closed")!;
        Assert.Equal(("08:30", "17:00"), h["Wednesday"]!.Value);
        Assert.Equal(("08:30", "12:00"), h["Friday"]!.Value);
        Assert.Null(h["Saturday"]);
        Assert.Null(SkillDrafter.ParseHours("whenever you like"));
    }

    [Fact]
    public void Unrecognised_requests_clarify_once_then_get_a_skeleton()
    {
        var first = Draft("Work out the loyalty tier from points");
        Assert.Equal("questions", first.Status);
        var second = Draft("Work out the loyalty tier from points", "tool", (first.Questions[0], "500 points = gold"));
        Assert.Equal("draft", second.Status);
        Assert.Contains("500 points = gold", second.Skill!.Code);
        Assert.All(RunTests(second.Skill), x => Assert.True(x.Pass, x.Error));
    }

    [Fact]
    public void Instructions_kind_needs_no_code()
    {
        var r = Draft("When someone asks for bulk pricing, collect the quantity and company name", "prompt");
        Assert.Equal("draft", r.Status);
        Assert.Equal(SkillKinds.Prompt, r.Skill!.Kind);
        Assert.Equal("someone asks for bulk pricing", r.Skill.UseWhen);
        Assert.StartsWith("Collect the quantity", r.Skill.Instructions);
        Assert.Equal("", r.Skill.Code);
    }

    [Fact]
    public void Llm_answers_are_parsed_tolerantly()
    {
        var q = SkillDrafter.Parse("Sure!\n```json\n{\"status\":\"questions\",\"questions\":[\"Which country?\",\"Which format?\"]}\n```", "");
        Assert.Equal("questions", q!.Status);
        Assert.Equal(2, q.Questions.Count);

        var d = SkillDrafter.Parse("""
            {"status":"draft","skill":{"name":"Fee","kind":"tool","use_when":"asked about fees",
             "description":"d","parameters":{"type":"object","properties":{}},
             "code":"function run(){ return { fee: 5 }; }","tests":[{"name":"t","args":{},"expect":{"fee":5}}]}}
            """, "");
        Assert.Equal("draft", d!.Status);
        Assert.Equal("Fee", d.Skill!.Name);
        Assert.All(RunTests(d.Skill), x => Assert.True(x.Pass));

        Assert.Null(SkillDrafter.Parse("no json here", ""));
    }

    /// <summary>A configured (fake) model that asks, then drafts.</summary>
    private sealed class ScriptedDrafter : ILlmClient
    {
        private readonly Queue<string> _texts;
        public List<string> Prompts { get; } = new();
        public ScriptedDrafter(params string[] texts) => _texts = new(texts);
        public bool IsConfigured => true;
        public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
            CancellationToken ct = default)
        {
            Prompts.Add(messages.Last().Content ?? "");
            return Task.FromResult(new LlmChatResult(_texts.Dequeue(), Array.Empty<LlmToolCall>(), 3, 4, "test", "m"));
        }
    }

    [Fact]
    public async Task With_a_model_the_answers_are_sent_back_and_garbage_falls_back_to_templates()
    {
        using var t = new TestDb();
        var llm = new ScriptedDrafter(
            "{\"status\":\"questions\",\"questions\":[\"Which days are you closed?\"]}",
            "not json at all");
        var drafter = new SkillDrafter(llm, t.Db);

        var q = await drafter.DraftAsync("Is the shop open now?", null, Array.Empty<SkillDrafter.Answer>());
        Assert.Equal("questions", q.Status);
        Assert.Equal("ai", q.Source);

        var d = await drafter.DraftAsync("Is the shop open now?", null,
            new[] { new SkillDrafter.Answer("Which days are you closed?", "Sundays") });
        Assert.Contains("Q: Which days are you closed?", llm.Prompts[1]);
        Assert.Contains("A: Sundays", llm.Prompts[1]);
        Assert.Equal("template", d.Source);          // the model's garbage → template fallback
        Assert.Equal(2, t.Fresh().LlmUsage.Count(u => u.Feature == "skill_draft"));
    }
}
