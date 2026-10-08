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
        var r = await ChatAsync(system, messages.Select(m => new LlmMessage(m.role, m.content)).ToList(),
            null, null, 0.5, ct);
        return new LlmResult(r.Text, r.PromptTokens, r.CompletionTokens, r.Provider, r.Model);
    }

    public async Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
        CancellationToken ct = default)
    {
        var provider = Resolve();
        if (provider == null) return await _sandbox.ChatAsync(system, messages, tools, model, temperature, ct);

        try
        {
            var payloadMessages = new List<object> { new { role = "system", content = system } };
            payloadMessages.AddRange(messages.Select(ToWire));

            var body = new Dictionary<string, object?>
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? provider.Model : model,
                ["messages"] = payloadMessages,
                ["temperature"] = temperature,
            };
            if (tools is { Count: > 0 })
            {
                body["tools"] = tools.Select(t => new
                {
                    type = "function",
                    function = new { name = t.Name, description = t.Description, parameters = t.Parameters },
                }).ToList();
                body["tool_choice"] = "auto";
            }

            var client = _http.CreateClient("llm");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{provider.BaseUrl}/chat/completions");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {provider.ApiKey}");
            if (provider.Name == "openrouter")
            {
                req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://chatbranch.local");
                req.Headers.TryAddWithoutValidation("X-Title", "ChatBranch");
            }
            req.Content = JsonContent.Create(body);

            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

            var msg = json.GetProperty("choices")[0].GetProperty("message");
            var text = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            var calls = new List<LlmToolCall>();
            if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in tc.EnumerateArray())
                {
                    if (!call.TryGetProperty("function", out var fn)) continue;
                    var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var args = fn.TryGetProperty("arguments", out var a)
                        ? (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText())
                        : "{}";
                    if (name.Length > 0)
                        calls.Add(new LlmToolCall(id.Length > 0 ? id : $"call_{calls.Count}", name, args));
                }
            }
            int pin = 0, pout = 0;
            if (json.TryGetProperty("usage", out var usage))
            {
                pin = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
                pout = usage.TryGetProperty("completion_tokens", out var q) ? q.GetInt32() : 0;
            }
            var usedModel = json.TryGetProperty("model", out var m) ? m.GetString() ?? provider.Model : provider.Model;
            return new LlmChatResult(text.Trim(), calls, pin, pout, provider.Name, usedModel);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Provider error → degrade to sandbox rather than break the reply path.
            return await _sandbox.ChatAsync(system, messages, tools, model, temperature, ct);
        }
    }

    /// <summary>OpenAI wire shape for one turn (assistant tool calls, tool results).</summary>
    private static object ToWire(LlmMessage m)
    {
        if (m.Role == "tool")
            return new { role = "tool", tool_call_id = m.ToolCallId ?? "", content = m.Content ?? "" };
        if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            return new
            {
                role = "assistant",
                content = m.Content,
                tool_calls = m.ToolCalls.Select(t => new
                {
                    id = t.Id,
                    type = "function",
                    function = new { name = t.Name, arguments = t.ArgumentsJson },
                }).ToList(),
            };
        return new { role = m.Role, content = m.Content ?? "" };
    }
}
