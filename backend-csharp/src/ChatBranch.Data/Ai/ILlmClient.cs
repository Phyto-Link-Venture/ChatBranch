namespace ChatBranch.Data.Ai;

public record LlmResult(string Text, int PromptTokens, int CompletionTokens,
    string Provider, string Model);

/// <summary>
/// The pluggable LLM boundary. A real implementation (OpenRouter/OpenAI, added
/// in a later pass) posts the assembled messages; the sandbox one keeps the app
/// fully runnable with zero credentials — the same rule as the Django app.
/// </summary>
public interface ILlmClient
{
    bool IsConfigured { get; }
    Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
        CancellationToken ct = default);
}

/// <summary>
/// Zero-credential fallback: echoes an acknowledgement so every AI path works in
/// sandbox mode. Deterministic (no network), which also keeps tests hermetic.
/// </summary>
public class SandboxLlmClient : ILlmClient
{
    public bool IsConfigured => false;

    public Task<LlmResult> CompleteAsync(string system,
        IReadOnlyList<(string role, string content)> messages, CancellationToken ct = default)
    {
        var lastUser = messages.LastOrDefault(m => m.role == "user").content ?? "";
        var reply = string.IsNullOrWhiteSpace(lastUser)
            ? "Hello! How can I help you today?"
            : $"Thanks for your message. (sandbox reply) You said: \"{Trim(lastUser, 120)}\".";
        return Task.FromResult(new LlmResult(reply, 0, 0, "sandbox", "sandbox"));
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
