using System.Text.Json;

namespace ChatBranch.Data.Ai;

/// <summary><see cref="CostUsd"/> = what the provider says the call cost (OpenRouter's
/// <c>usage.cost</c>); null = it didn't say, so reports estimate from <c>llm_prices</c>.</summary>
public record LlmResult(string Text, int PromptTokens, int CompletionTokens,
    string Provider, string Model, double? CostUsd = null);

/// <summary>A function the model may call: name, description and a JSON-schema
/// object describing its arguments (OpenAI "tools" format).</summary>
public record LlmTool(string Name, string Description, JsonElement Parameters);

/// <summary>One call the model asked for. <see cref="ArgumentsJson"/> is the raw
/// JSON string the model produced — parse defensively, it can be malformed.</summary>
public record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>A chat turn for tool-aware conversations. Roles: system | user |
/// assistant | tool. An assistant turn may carry <see cref="ToolCalls"/>; a tool
/// turn answers one call via <see cref="ToolCallId"/>. <see cref="Images"/> = image
/// inputs on a user turn as <c>data:</c> URLs (only for a model that reads images —
/// see <see cref="ILlmClient.SupportsVision"/>).</summary>
public record LlmMessage(string Role, string? Content,
    IReadOnlyList<LlmToolCall>? ToolCalls = null, string? ToolCallId = null,
    IReadOnlyList<string>? Images = null);

public record LlmChatResult(string Text, IReadOnlyList<LlmToolCall> ToolCalls,
    int PromptTokens, int CompletionTokens, string Provider, string Model, double? CostUsd = null);

/// <summary>
/// The pluggable LLM boundary. The real implementation posts to an
/// OpenAI-compatible chat-completions API (OpenRouter → OpenAI → local); the
/// sandbox one keeps the app fully runnable with zero credentials.
/// </summary>
public interface ILlmClient
{
    bool IsConfigured { get; }

    /// <summary>Can the model that would answer next read images (OpenAI-compatible
    /// <c>image_url</c> content parts)? False = send text only.</summary>
    bool SupportsVision => false;

    /// <summary>Plain completion: no tools, text out.</summary>
    Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
        CancellationToken ct = default);

    /// <summary>One tool-aware round. The caller runs any returned tool calls and
    /// sends their results back as <c>tool</c> messages (see <see cref="ToolLoop"/>).</summary>
    Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
        CancellationToken ct = default);
}

/// <summary>
/// Zero-credential fallback: echoes an acknowledgement so every AI path works in
/// sandbox mode. Deterministic (no network), which also keeps tests hermetic.
/// Never asks for tools.
/// </summary>
public class SandboxLlmClient : ILlmClient
{
    public bool IsConfigured => false;

    public Task<LlmResult> CompleteAsync(string system,
        IReadOnlyList<(string role, string content)> messages, CancellationToken ct = default)
    {
        var lastUser = messages.LastOrDefault(m => m.role == "user").content ?? "";
        return Task.FromResult(new LlmResult(Echo(lastUser), 0, 0, "sandbox", "sandbox"));
    }

    public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
        CancellationToken ct = default)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        return Task.FromResult(new LlmChatResult(Echo(lastUser), Array.Empty<LlmToolCall>(),
            0, 0, "sandbox", "sandbox"));
    }

    private static string Echo(string lastUser) => string.IsNullOrWhiteSpace(lastUser)
        ? "Hello! How can I help you today?"
        : $"Thanks for your message. (sandbox reply) You said: \"{Trim(lastUser, 120)}\".";

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
