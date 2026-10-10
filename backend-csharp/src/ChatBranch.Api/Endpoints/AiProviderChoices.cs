using ChatBranch.Data.Ai;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The configured chat providers a person may pick for a one-off call — names,
/// labels and built-in default models, NEVER keys. Shared by the AI playground's
/// options (<c>GET /api/ai/playground/options</c>) and the AI reviewer's
/// (<c>GET /api/ai/review/options</c>), so both pickers list the same providers.
/// </summary>
public static class AiProviderChoices
{
    public sealed record Choice(string Id, string Label, string DefaultModel);

    public static string Label(string provider) =>
        OpenAiLlmClient.Labels.TryGetValue(provider, out var l) ? l : provider;

    /// <summary>Every provider with a key (local: a base URL), in the default order;
    /// empty when the AI runs on something other than the real client (sandbox).</summary>
    public static List<Choice> Configured(ILlmClient? llm) =>
        llm is OpenAiLlmClient real
            ? real.Configured().Select(p => new Choice(p.Name, Label(p.Name), p.Model)).ToList()
            : new List<Choice>();
}
