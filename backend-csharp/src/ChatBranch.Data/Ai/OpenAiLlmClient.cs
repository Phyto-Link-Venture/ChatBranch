using System.Net.Http.Json;
using System.Text.Json;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Real chat-completions client (port of aicore/assistant.py + wa_config chat
/// provider resolution). Priority: OpenRouter → OpenAI → local (Ollama/LM
/// Studio, OpenAI-compatible). Keys read from Setting rows (openai_api_key /
/// openrouter_api_key / local_base_url…) with env fallback. When nothing is
/// configured it falls back to the sandbox echo, so the app stays fully runnable
/// with zero credentials — the same rule as the Django app.
/// </summary>
public class OpenAiLlmClient : ILlmClient
{
    private readonly IHttpClientFactory _http;
    private readonly ScopingService _scoping;
    private readonly SandboxLlmClient _sandbox = new();

    public OpenAiLlmClient(IHttpClientFactory http, ScopingService scoping)
    {
        _http = http;
        _scoping = scoping;
    }

    private record Provider(string Name, string BaseUrl, string ApiKey, string Model);

    public bool IsConfigured => Resolve() != null;

    private Provider? Resolve()
    {
        string S(string key) => _scoping.SettingValue(key)
            is { Length: > 0 } v ? v : Environment.GetEnvironmentVariable(key.ToUpperInvariant()) ?? "";

        var model = S("chat_model");
        var openrouter = S("openrouter_api_key");
        var openai = S("openai_api_key");
        var localBase = S("local_base_url");
        var localKey = S("local_api_key");

        var pin = _scoping.SettingValue("chat_provider");
        var candidates = new List<Provider>();
        if (!string.IsNullOrEmpty(openrouter))
            candidates.Add(new("openrouter", "https://openrouter.ai/api/v1", openrouter,
                model is { Length: > 0 } ? model : "openai/gpt-4o-mini"));
        if (!string.IsNullOrEmpty(openai))
            candidates.Add(new("openai", "https://api.openai.com/v1", openai,
                model is { Length: > 0 } ? model : "gpt-4o-mini"));
        if (!string.IsNullOrEmpty(localBase))
            candidates.Add(new("local", localBase.TrimEnd('/'), localKey,
                model is { Length: > 0 } ? model : "local"));

        if (candidates.Count == 0) return null;
        if (!string.IsNullOrEmpty(pin))
        {
            var pinned = candidates.FirstOrDefault(c => c.Name == pin);
            if (pinned != null) return pinned;
        }
        return candidates[0];
    }

    public async Task<LlmResult> CompleteAsync(string system,
        IReadOnlyList<(string role, string content)> messages, CancellationToken ct = default)
    {
        var provider = Resolve();
        if (provider == null) return await _sandbox.CompleteAsync(system, messages, ct);

        try
        {
            var payloadMessages = new List<object> { new { role = "system", content = system } };
            payloadMessages.AddRange(messages.Select(m => (object)new { role = m.role, content = m.content }));

            var client = _http.CreateClient("llm");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{provider.BaseUrl}/chat/completions");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {provider.ApiKey}");
            if (provider.Name == "openrouter")
            {
                req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://chatbranch.local");
                req.Headers.TryAddWithoutValidation("X-Title", "ChatBranch");
            }
            req.Content = JsonContent.Create(new
            {
                model = provider.Model,
                messages = payloadMessages,
                temperature = 0.5,
            });

            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            var text = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            int pin = 0, pout = 0;
            if (json.TryGetProperty("usage", out var usage))
            {
                pin = usage.TryGetProperty("prompt_tokens", out var a) ? a.GetInt32() : 0;
                pout = usage.TryGetProperty("completion_tokens", out var b) ? b.GetInt32() : 0;
            }
            return new LlmResult(text.Trim(), pin, pout, provider.Name, provider.Model);
        }
        catch
        {
            // Provider error → degrade to sandbox rather than break the reply path.
            return await _sandbox.CompleteAsync(system, messages, ct);
        }
    }
}
