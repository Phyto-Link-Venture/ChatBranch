using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Real chat-completions client (port of aicore/assistant.py + wa_config chat
/// provider resolution). Every provider is spoken to through its
/// OpenAI-compatible <c>/chat/completions</c> endpoint:
/// <list type="bullet">
///   <item><c>openrouter</c> — https://openrouter.ai/api/v1 (<c>openrouter_api_key</c>)</item>
///   <item><c>openai</c> — https://api.openai.com/v1 (<c>openai_api_key</c>)</item>
///   <item><c>ilmu</c> — YTL ILMU, <c>ILMU_BASE_URL</c> (default https://api.ilmu.ai/v1),
///         <c>ilmu_api_key</c>, model <c>ilmu-v3.1</c></item>
///   <item><c>google</c> — Gemini at https://generativelanguage.googleapis.com/v1beta/openai
///         (<c>google_api_key</c>), model <c>gemini-2.5-flash</c></item>
///   <item><c>local</c> — Ollama / LM Studio at <c>local_base_url</c> (optional <c>local_api_key</c>)</item>
/// </list>
/// Which one answers: the pinned provider (<c>chat_provider_pref</c>, mirrored to
/// <c>chat_provider</c>; env <c>CHAT_PROVIDER</c>) when it has a key — and ONLY
/// it: a pin is a deliberate choice (data residency for ILMU), so a failing pinned
/// provider degrades to the sandbox reply rather than leaking to another vendor.
/// Otherwise "Automatic": the providers in <c>chat_provider_order</c> (a JSON list
/// or comma list; env <c>CHAT_PROVIDER_ORDER</c>; default openrouter, openai,
/// ilmu, google, then local) that have a key, tried in that order — an error
/// moves on to the next. <c>chat_model</c> (env <c>WHATSAPP_CHAT_MODEL</c>, the
/// name the settings page shows; <c>CHAT_MODEL</c> also accepted) applies to the
/// first provider tried; a fallback uses its own default model, since a model id
/// rarely means the same thing at another vendor.
/// Keys are read from Setting rows with process-env fallback. Nothing
/// configured → the sandbox echo, so the app stays runnable with zero
/// credentials. Keys and provider bodies are never logged.
/// </summary>
public class OpenAiLlmClient : ILlmClient
{
    private readonly IHttpClientFactory _http;
    private readonly ScopingService _scoping;
    private readonly SandboxLlmClient _sandbox = new();

    public const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    public const string IlmuBaseUrlDefault = "https://api.ilmu.ai/v1";

    /// <summary>The automatic order when <c>chat_provider_order</c> names nothing usable.</summary>
    public static readonly string[] DefaultOrder = { "openrouter", "openai", "ilmu", "google", "local" };

    public OpenAiLlmClient(IHttpClientFactory http, ScopingService scoping)
    {
        _http = http;
        _scoping = scoping;
    }

    /// <summary>A client pinned to ONE provider (see <see cref="Pinned"/>).</summary>
    private OpenAiLlmClient(IHttpClientFactory http, ScopingService scoping, Provider only) : this(http, scoping) =>
        _only = only;

    /// <summary>Set by <see cref="Pinned"/>: this client speaks to that provider only.</summary>
    private readonly Provider? _only;

    public sealed record Provider(string Name, string BaseUrl, string ApiKey, string Model);

    /// <summary>Human names of the providers (the keys never leave the server; these may).</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["openrouter"] = "OpenRouter",
        ["openai"] = "OpenAI",
        ["ilmu"] = "ILMU",
        ["google"] = "Google Gemini",
        ["local"] = "Local model",
    };

    public bool IsConfigured => Candidates().Count > 0;

    /// <summary>
    /// The last provider failure this client swallowed (e.g. "openai: Response
    /// status code does not indicate success: 401 (Unauthorized).") — "" after a
    /// call that succeeded. The reply path degrades to the sandbox echo on a
    /// failure; this lets a caller that wants to (the AI playground) say why.
    /// It names the provider and the HTTP status, never a key or a body.
    /// </summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// Every provider that has a key (local: a base URL), in the default order,
    /// whatever the pin or the automatic order says — what may be chosen for a
    /// one-off call. Each carries its built-in default model.
    /// </summary>
    public List<Provider> Configured()
    {
        var keyed = Keyed();
        return DefaultOrder.Where(keyed.ContainsKey).Select(p => keyed[p]).ToList();
    }

    /// <summary>
    /// A client that speaks to ONE configured provider — and only it, like a
    /// pin: a failure degrades to the sandbox echo, never to another vendor —
    /// with <paramref name="model"/> instead of the provider's default when
    /// given. Null when that provider has no key.
    /// </summary>
    public OpenAiLlmClient? Pinned(string provider, string? model = null)
    {
        var name = (provider ?? "").Trim().ToLowerInvariant();
        if (name == "gemini") name = "google";
        var p = Configured().FirstOrDefault(x => x.Name == name);
        if (p == null) return null;
        return new OpenAiLlmClient(_http, _scoping,
            string.IsNullOrWhiteSpace(model) ? p : p with { Model = model.Trim() });
    }

    /// <summary>Setting row → the first non-empty env var → "".</summary>
    private string S(string key, params string[] envs)
    {
        var v = _scoping.SettingValue(key);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        foreach (var e in envs)
            if (Environment.GetEnvironmentVariable(e) is { Length: > 0 } ev && ev.Trim().Length > 0)
                return ev.Trim();
        return "";
    }

    /// <summary>A provider order value — a JSON list OR a comma/space list — as known names.</summary>
    public static List<string> ParseOrder(string? raw)
    {
        var members = new List<string>();
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return members;
        if (raw.StartsWith("["))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var el in doc.RootElement.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.String)
                            members.Add((el.GetString() ?? "").Trim().ToLowerInvariant());
            }
            catch { /* fall through to the list form */ }
        }
        if (members.Count == 0)
            members = raw.Split(new[] { ',', ' ', ';', '\t', '\n', '\r' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.Trim('"', '\'', '[', ']').ToLowerInvariant()).ToList();
        return members.Where(m => DefaultOrder.Contains(m)).Distinct().ToList();
    }

    /// <summary>
    /// The providers to try, in order (the pin alone when it is usable). The
    /// configured <c>chat_model</c> is set on the first only.
    /// </summary>
    public List<Provider> Candidates()
    {
        if (_only != null) return new List<Provider> { _only };
        var keyed = Keyed();
        if (keyed.Count == 0) return new List<Provider>();

        var pin = S("chat_provider_pref").ToLowerInvariant();
        if (pin.Length == 0) pin = S("chat_provider", "CHAT_PROVIDER").ToLowerInvariant();
        if (pin is "gemini") pin = "google";

        List<string> order;
        if (pin.Length > 0 && keyed.ContainsKey(pin)) order = new List<string> { pin };
        else
        {
            order = ParseOrder(S("chat_provider_order", "CHAT_PROVIDER_ORDER"));
            order.AddRange(DefaultOrder.Where(p => !order.Contains(p)));
        }
        var list = order.Where(keyed.ContainsKey).Select(p => keyed[p]).ToList();

        var chatModel = S("chat_model", "WHATSAPP_CHAT_MODEL", "CHAT_MODEL");
        if (chatModel.Length > 0 && list.Count > 0) list[0] = list[0] with { Model = chatModel };
        return list;
    }

    /// <summary>Every provider with a key (local: a base URL), by name, with its built-in default model.</summary>
    private Dictionary<string, Provider> Keyed()
    {
        var keyed = new Dictionary<string, Provider>();
        var openrouter = S("openrouter_api_key", "OPENROUTER_API_KEY");
        if (openrouter.Length > 0)
            keyed["openrouter"] = new("openrouter", "https://openrouter.ai/api/v1", openrouter, "openai/gpt-4o-mini");
        var openai = S("openai_api_key", "OPENAI_API_KEY");
        if (openai.Length > 0)
            keyed["openai"] = new("openai", "https://api.openai.com/v1", openai, "gpt-4o-mini");
        var ilmu = S("ilmu_api_key", "ILMU_API_KEY");
        if (ilmu.Length > 0)
        {
            var baseUrl = S("ilmu_base_url", "ILMU_BASE_URL");
            var model = S("ilmu_chat_model", "ILMU_LLM_MODEL");
            keyed["ilmu"] = new("ilmu", (baseUrl.Length > 0 ? baseUrl : IlmuBaseUrlDefault).TrimEnd('/'), ilmu,
                model.Length > 0 ? model : "ilmu-v3.1");
        }
        var google = S("google_api_key", "GOOGLE_API_KEY", "GEMINI_API_KEY");
        if (google.Length > 0)
            keyed["google"] = new("google", GeminiBaseUrl, google, "gemini-2.5-flash");
        var localBase = S("local_base_url", "LOCAL_BASE_URL");
        if (localBase.Length > 0)
            keyed["local"] = new("local", localBase.TrimEnd('/'), S("local_api_key", "LOCAL_API_KEY"), "local");
        return keyed;
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
        var candidates = Candidates();
        if (candidates.Count == 0) return await _sandbox.ChatAsync(system, messages, tools, model, temperature, ct);

        for (var i = 0; i < candidates.Count; i++)
        {
            try
            {
                // an explicit per-call model only means something to the first provider
                var useModel = i == 0 && !string.IsNullOrWhiteSpace(model) ? model! : candidates[i].Model;
                var r = await SendAsync(candidates[i], useModel, system, messages, tools, temperature, ct);
                LastError = "";
                return r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // provider error → the next provider in the automatic order
                var why = ex.Message.Length > 200 ? ex.Message[..200] + "…" : ex.Message;
                LastError = $"{candidates[i].Name}: {why}";
            }
        }
        // Every provider failed → degrade to sandbox rather than break the reply path.
        return await _sandbox.ChatAsync(system, messages, tools, model, temperature, ct);
    }

    private async Task<LlmChatResult> SendAsync(Provider provider, string model, string system,
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool>? tools, double temperature, CancellationToken ct)
    {
        var payloadMessages = new List<object> { new { role = "system", content = system } };
        payloadMessages.AddRange(messages.Select(ToWire));

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = payloadMessages,
            ["temperature"] = temperature,
        };
        // ILMU (ilmu-v3.1) and Gemini 2.5 are reasoning models: "low" turns the
        // extended thinking off — faster, and no think-block leaking into a reply
        // (the voice agent does the same; Gemini's OpenAI endpoint takes this knob)
        if (provider.Name is "ilmu" or "google") body["reasoning_effort"] = "low";
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
        if (provider.ApiKey.Length > 0)
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
        if (json.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            pin = usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            pout = usage.TryGetProperty("completion_tokens", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : 0;
        }
        var usedModel = json.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? model : model;
        return new LlmChatResult(StripThinking(text).Trim(), calls, pin, pout, provider.Name, usedModel);
    }

    private static readonly Regex ThinkBlock =
        new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A reasoning model's <c>&lt;think&gt;…&lt;/think&gt;</c> block is never a reply.</summary>
    private static string StripThinking(string text) =>
        text.Contains("<think>", StringComparison.OrdinalIgnoreCase) ? ThinkBlock.Replace(text, "") : text;

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
