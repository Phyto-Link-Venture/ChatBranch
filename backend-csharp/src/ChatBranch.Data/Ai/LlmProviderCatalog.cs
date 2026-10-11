namespace ChatBranch.Data.Ai;

/// <summary>
/// Every chat provider ChatBranch can talk to, in ONE list. Each speaks an
/// OpenAI-compatible <c>/chat/completions</c> endpoint, so adding a vendor is a
/// row here — the chat brain (<see cref="OpenAiLlmClient"/>), the settings page
/// (<c>GET/PUT /api/settings/ai-providers</c>), the provider pickers of the AI
/// playground / reviewer and the price lookup all read this catalog.
/// <para>
/// Settings per provider: <see cref="KeySetting"/> (secret, env fallback
/// <see cref="KeyEnvs"/>), an optional base-URL override <see cref="BaseUrlSetting"/>
/// (regional endpoints, self-hosted gateways) and the provider's own default model
/// <see cref="ModelSetting"/> (else <see cref="DefaultModel"/>). The global
/// <c>chat_model</c> still overrides the model of whichever provider answers first.
/// </para>
/// </summary>
public static class LlmProviderCatalog
{
    public sealed record ProviderDef(
        string Id,
        string Label,
        /// <summary>global | asia | self — the settings page's sections.</summary>
        string Group,
        string KeySetting,
        string[] KeyEnvs,
        string BaseUrl,
        string BaseUrlSetting,
        string[] BaseUrlEnvs,
        string ModelSetting,
        string[] ModelEnvs,
        string DefaultModel,
        string[] Models,
        /// <summary>Takes OpenAI-style <c>image_url</c> parts (invoice photos, design uploads).</summary>
        bool Vision,
        /// <summary>A reasoning model whose thinking "low" turns off (faster, no think-block).</summary>
        bool ReasoningLow,
        /// <summary>Send <c>max_tokens</c> on every call (the endpoint requires it, or checks credit against the ceiling).</summary>
        bool SendsMaxTokens,
        /// <summary>The key may be empty (a local server) — the base URL is what turns it on.</summary>
        bool KeyOptional,
        /// <summary>Where to get a key (shown as a link).</summary>
        string KeyUrl,
        /// <summary>Known alternative endpoints (region) — label → base URL.</summary>
        IReadOnlyList<(string Label, string Url)> Endpoints,
        /// <summary>OpenRouter's vendor prefix for the price list ("anthropic" → anthropic/claude-…).</summary>
        string PriceVendor)
    {
        /// <summary>Send <c>temperature</c>. Anthropic's current Claude models refuse it
        /// ("`temperature` is deprecated for this model") — the client also drops any
        /// parameter a model rejects by itself (see <see cref="OpenAiLlmClient"/>).</summary>
        public bool SendsTemperature { get; init; } = true;
    }

    private static readonly (string, string)[] None = Array.Empty<(string, string)>();

    /// <summary>The providers, in the default automatic order.</summary>
    public static readonly IReadOnlyList<ProviderDef> All = new ProviderDef[]
    {
        new("openrouter", "OpenRouter", "global", "openrouter_api_key", new[] { "OPENROUTER_API_KEY" },
            "https://openrouter.ai/api/v1", "", Array.Empty<string>(), "openrouter_chat_model", Array.Empty<string>(),
            "openai/gpt-4o-mini",
            new[] { "openai/gpt-4o-mini", "openai/gpt-4o", "anthropic/claude-sonnet-5.5", "anthropic/claude-haiku-5.5",
                    "google/gemini-2.5-flash", "qwen/qwen-plus", "deepseek/deepseek-chat", "meta-llama/llama-3.3-70b-instruct" },
            Vision: true, ReasoningLow: false, SendsMaxTokens: true, KeyOptional: false,
            "https://openrouter.ai/keys", None, ""),
        new("openai", "OpenAI", "global", "openai_api_key", new[] { "OPENAI_API_KEY" },
            "https://api.openai.com/v1", "", Array.Empty<string>(), "openai_chat_model", Array.Empty<string>(),
            "gpt-4o-mini", new[] { "gpt-4o-mini", "gpt-4o", "gpt-4.1", "gpt-4.1-mini", "gpt-5", "gpt-5-mini" },
            Vision: true, ReasoningLow: false, SendsMaxTokens: false, KeyOptional: false,
            "https://platform.openai.com/api-keys", None, "openai"),
        new("anthropic", "Anthropic (Claude)", "global", "anthropic_api_key", new[] { "ANTHROPIC_API_KEY" },
            "https://api.anthropic.com/v1", "", Array.Empty<string>(), "anthropic_chat_model", Array.Empty<string>(),
            "claude-haiku-5-5", new[] { "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5" },
            Vision: true, ReasoningLow: false, SendsMaxTokens: true, KeyOptional: false,
            "https://console.anthropic.com/settings/keys", None, "anthropic") { SendsTemperature = false },
        new("ilmu", "ILMU", "asia", "ilmu_api_key", new[] { "ILMU_API_KEY" },
            OpenAiLlmClient.IlmuBaseUrlDefault, "ilmu_base_url", new[] { "ILMU_BASE_URL" },
            "ilmu_chat_model", new[] { "ILMU_LLM_MODEL" }, "ilmu-v3.1", new[] { "ilmu-v3.1" },
            Vision: false, ReasoningLow: true, SendsMaxTokens: false, KeyOptional: false,
            "https://ilmu.ai", None, ""),
        new("google", "Google Gemini", "global", "google_api_key", new[] { "GOOGLE_API_KEY", "GEMINI_API_KEY" },
            OpenAiLlmClient.GeminiBaseUrl, "", Array.Empty<string>(), "google_chat_model", Array.Empty<string>(),
            "gemini-2.5-flash", new[] { "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.5-flash-lite", "gemini-2.0-flash" },
            Vision: true, ReasoningLow: true, SendsMaxTokens: false, KeyOptional: false,
            "https://aistudio.google.com/apikey", None, "google"),
        new("mistral", "Mistral", "global", "mistral_api_key", new[] { "MISTRAL_API_KEY" },
            "https://api.mistral.ai/v1", "", Array.Empty<string>(), "mistral_chat_model", Array.Empty<string>(),
            "mistral-small-latest", new[] { "mistral-small-latest", "mistral-medium-latest", "mistral-large-latest" },
            Vision: false, ReasoningLow: false, SendsMaxTokens: false, KeyOptional: false,
            "https://console.mistral.ai/api-keys", None, "mistralai"),
        new("xai", "xAI (Grok)", "global", "xai_api_key", new[] { "XAI_API_KEY" },
            "https://api.x.ai/v1", "", Array.Empty<string>(), "xai_chat_model", Array.Empty<string>(),
            "grok-3-mini", new[] { "grok-3-mini", "grok-3", "grok-4" },
            Vision: false, ReasoningLow: false, SendsMaxTokens: false, KeyOptional: false,
            "https://console.x.ai", None, "x-ai"),
        new("groq", "Groq", "global", "groq_api_key", new[] { "GROQ_API_KEY" },
            "https://api.groq.com/openai/v1", "", Array.Empty<string>(), "groq_chat_model", Array.Empty<string>(),
            "llama-3.3-70b-versatile", new[] { "llama-3.3-70b-versatile", "llama-3.1-8b-instant", "qwen/qwen3-32b" },
            Vision: false, ReasoningLow: false, SendsMaxTokens: false, KeyOptional: false,
            "https://console.groq.com/keys", None, ""),
        new("deepseek", "DeepSeek", "asia", "deepseek_api_key", new[] { "DEEPSEEK_API_KEY" },
            "https://api.deepseek.com/v1", "", Array.Empty<string>(), "deepseek_chat_model", Array.Empty<string>(),
            "deepseek-chat", new[] { "deepseek-chat", "deepseek-reasoner" },
            Vision: false, ReasoningLow: false, SendsMaxTokens: false, KeyOptional: false,
            "https://platform.deepseek.com/api_keys", None, "deepseek"),
        new("qwen", "Qwen (Alibaba Cloud)", "asia", "qwen_api_key", new[] { "DASHSCOPE_API_KEY", "QWEN_API_KEY" },
            "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen_base_url", new[] { "QWEN_BASE_URL" },
            "qwen_chat_model", Array.Empty<string>(),
            "qwen-plus", new[] { "qwen-plus", "qwen-max", "qwen-turbo", "qwen-flash", "qwen3-max" },
            Vision: false, ReasoningLow: false, SendsMaxTokens: false, KeyOptional: false,
            "https://bailian.console.alibabacloud.com/?apiKey=1",
            new[]
            {
                ("International (Singapore)", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1"),
                ("China (Beijing)", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
            }, "qwen"),
        new("moonshot", "Kimi (Moonshot)", "asia", "moonshot_api_key", new[] { "MOONSHOT_API_KEY" },
            "https://api.moonshot.ai/v1", "moonshot_base_url", new[] { "MOONSHOT_BASE_URL" },
            "moonshot_chat_model", Array.Empty<string>(),
            "kimi-k2-turbo-preview", new[] { "kimi-k2-turbo-preview", "kimi-k2-0905-preview", "moonshot-v1-8k" },
            Vision: false, ReasoningLow: false, SendsMaxTokens: false, KeyOptional: false,
            "https://platform.moonshot.ai/console/api-keys",
            new[]
            {
                ("International", "https://api.moonshot.ai/v1"),
                ("China", "https://api.moonshot.cn/v1"),
            }, "moonshotai"),
        new("zhipu", "Zhipu GLM (Z.ai)", "asia", "zhipu_api_key", new[] { "ZHIPU_API_KEY", "ZAI_API_KEY" },
            "https://api.z.ai/api/paas/v4", "zhipu_base_url", new[] { "ZHIPU_BASE_URL" },
            "zhipu_chat_model", Array.Empty<string>(),
            "glm-4.5-air", new[] { "glm-4.5-air", "glm-4.5", "glm-4.6" },
            Vision: false, ReasoningLow: false, SendsMaxTokens: false, KeyOptional: false,
            "https://z.ai/manage-apikey/apikey-list",
            new[]
            {
                ("International (Z.ai)", "https://api.z.ai/api/paas/v4"),
                ("China (BigModel)", "https://open.bigmodel.cn/api/paas/v4"),
            }, "z-ai"),
        new("local", "Local or custom", "self", "local_api_key", new[] { "LOCAL_API_KEY" },
            "", "local_base_url", new[] { "LOCAL_BASE_URL" }, "local_chat_model", Array.Empty<string>(),
            "local", new[] { "llama3.1", "llama3.2", "qwen2.5", "mistral", "gemma2" },
            Vision: false, ReasoningLow: false, SendsMaxTokens: false, KeyOptional: true, "",
            new[]
            {
                ("Ollama", "http://localhost:11434/v1"),
                ("LM Studio", "http://localhost:1234/v1"),
            }, ""),
    };

    public static readonly IReadOnlyDictionary<string, ProviderDef> ById =
        All.ToDictionary(p => p.Id);

    /// <summary>Provider ids in the default automatic order.</summary>
    public static readonly string[] Ids = All.Select(p => p.Id).ToArray();

    /// <summary>An id as people write it ("gemini", "claude", "Kimi") → the catalog id, or "".</summary>
    public static string Normalize(string? id)
    {
        var v = (id ?? "").Trim().ToLowerInvariant();
        v = v switch
        {
            "gemini" => "google",
            "claude" => "anthropic",
            "kimi" => "moonshot",
            "glm" or "zai" or "z.ai" => "zhipu",
            "dashscope" or "alibaba" => "qwen",
            "grok" => "xai",
            _ => v,
        };
        return ById.ContainsKey(v) ? v : "";
    }
}
