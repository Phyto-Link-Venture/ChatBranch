using System.Text.Json;
using ChatBranch.Data.Ai;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>The shared function-calling loop: runs requested tools, feeds results
/// back, stops on a text answer, and never lets a throwing tool escape.</summary>
public class ToolLoopTests
{
    /// <summary>Scripted model: replays one canned result per round and records
    /// what it was sent.</summary>
    private sealed class ScriptedLlm : ILlmClient
    {
        private readonly Queue<LlmChatResult> _script;
        public List<List<LlmMessage>> Seen { get; } = new();
        public List<bool> OfferedTools { get; } = new();
        public ScriptedLlm(params LlmChatResult[] script) => _script = new(script);
        public bool IsConfigured => true;

        public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
            CancellationToken ct = default)
        {
            Seen.Add(messages.ToList());
            OfferedTools.Add(tools is { Count: > 0 });
            return Task.FromResult(_script.Dequeue());
        }
    }

    private static LlmChatResult Calls(params LlmToolCall[] calls) =>
        new("", calls, 10, 5, "test", "m");

    private static LlmChatResult Says(string text) =>
        new(text, Array.Empty<LlmToolCall>(), 7, 3, "test", "m");

    private static readonly LlmTool Clock = new("current_time", "Now",
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement);

    [Fact]
    public async Task Runs_tools_then_returns_the_text_answer()
    {
        var llm = new ScriptedLlm(Calls(new LlmToolCall("c1", "current_time", "{}")), Says("It is 10:00."));
        var messages = new List<LlmMessage> { new("user", "what time is it?") };

        var outcome = await ToolLoop.RunAsync(llm, "sys", messages, new[] { Clock },
            (call, _) => Task.FromResult(("10:00", true)));

        Assert.Equal("It is 10:00.", outcome.Text);
        Assert.Single(outcome.Steps);
        Assert.Equal("current_time", outcome.Steps[0].Tool);
        Assert.Equal(17, outcome.PromptTokens);
        // Round 2 saw the assistant tool call and the tool result.
        var second = llm.Seen[1];
        Assert.Equal("assistant", second[1].Role);
        Assert.Equal("tool", second[2].Role);
        Assert.Equal("c1", second[2].ToolCallId);
        Assert.Equal("10:00", second[2].Content);
    }

    [Fact]
    public async Task A_throwing_tool_is_reported_as_data_and_the_last_round_withholds_tools()
    {
        var llm = new ScriptedLlm(
            Calls(new LlmToolCall("a", "current_time", "{}")),
            Calls(new LlmToolCall("b", "current_time", "{}")),
            Says("gave up"));
        var messages = new List<LlmMessage> { new("user", "hi") };

        var outcome = await ToolLoop.RunAsync(llm, "sys", messages, new[] { Clock },
            (_, _) => throw new InvalidOperationException("boom"), maxRounds: 3);

        Assert.Equal("gave up", outcome.Text);
        Assert.All(outcome.Steps, s => Assert.False(s.Ok));
        Assert.StartsWith("error: boom", outcome.Steps[0].Result);
        Assert.Equal(new[] { true, true, false }, llm.OfferedTools);
    }

    [Fact]
    public async Task Sandbox_client_never_asks_for_tools()
    {
        var outcome = await ToolLoop.RunAsync(new SandboxLlmClient(), "sys",
            new List<LlmMessage> { new("user", "hello") }, new[] { Clock },
            (_, _) => Task.FromResult(("x", true)));
        Assert.Empty(outcome.Steps);
        Assert.Contains("sandbox", outcome.Text);
    }
}
