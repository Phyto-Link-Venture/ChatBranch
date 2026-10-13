namespace ChatBranch.Data.Ai;

/// <summary>
/// The function-calling loop shared by every tool-using AI path (chat replies
/// with code skills, the in-app assistant, MCP-backed tools): ask the model,
/// run whatever tools it calls, feed the results back, repeat until it answers
/// in text or the round budget runs out.
///
/// Tool failures are reported back to the model as data ("error: …") rather
/// than thrown, so one bad tool never breaks a reply path.
/// </summary>
public static class ToolLoop
{
    public record Step(string Tool, string ArgumentsJson, string Result, bool Ok);

    public record Outcome(string Text, IReadOnlyList<Step> Steps, int PromptTokens,
        int CompletionTokens, string Provider, string Model, bool RoundsExhausted, double? CostUsd = null);

    public static async Task<Outcome> RunAsync(ILlmClient llm, string system,
        List<LlmMessage> messages, IReadOnlyList<LlmTool> tools,
        Func<LlmToolCall, CancellationToken, Task<(string result, bool ok)>> execute,
        int maxRounds = 5, string? model = null, double temperature = 0.4,
        CancellationToken ct = default, string? lastRoundNote = null)
    {
        var steps = new List<Step>();
        int pin = 0, pout = 0;
        string provider = "", usedModel = "";
        double? cost = null;
        for (var round = 0; round < maxRounds; round++)
        {
            // Last round: withhold tools so the model must answer in text.
            var offer = round == maxRounds - 1 ? null : tools;
            // …and say so: a model that still wants a tool otherwise answers with nothing at all
            if (offer == null && steps.Count > 0 && !string.IsNullOrWhiteSpace(lastRoundNote))
                messages.Add(new LlmMessage("system", lastRoundNote));
            var r = await llm.ChatAsync(system, messages, offer?.Count > 0 ? offer : null, model, temperature, ct);
            pin += r.PromptTokens;
            pout += r.CompletionTokens;
            if (r.CostUsd is double c) cost = (cost ?? 0) + c;
            provider = r.Provider;
            usedModel = r.Model;
            if (r.ToolCalls.Count == 0)
                return new Outcome(r.Text, steps, pin, pout, provider, usedModel, false, cost);

            messages.Add(new LlmMessage("assistant", string.IsNullOrEmpty(r.Text) ? null : r.Text, r.ToolCalls));
            foreach (var call in r.ToolCalls)
            {
                string result;
                bool ok;
                try
                {
                    (result, ok) = await execute(call, ct);
                }
                catch (Exception ex)
                {
                    (result, ok) = ($"error: {ex.Message}", false);
                }
                steps.Add(new Step(call.Name, call.ArgumentsJson, result, ok));
                messages.Add(new LlmMessage("tool", result, ToolCallId: call.Id));
            }
        }
        return new Outcome("", steps, pin, pout, provider, usedModel, true, cost);
    }
}
