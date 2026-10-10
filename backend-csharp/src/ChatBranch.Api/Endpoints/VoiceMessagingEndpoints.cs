using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Voice registry, voice pipeline selectors, AI &amp; provider settings and the
/// messaging channel-account registry — the endpoints the Settings screens
/// (AI Configuration, AI &amp; Providers, Channels) and the voice picker call.
///
/// Ports, key for key:
///   * <c>calls/views.voices_list</c> + <c>calls/voices.py</c>   → GET /api/voices
///   * <c>calls/views.stt_settings|llm_settings</c>              → /api/voice/{stt,llm}-settings
///   * <c>core/settings_api.ai_providers</c> + core/app_settings → /api/settings/ai-providers
///   * <c>messaging/views.accounts|account_detail</c>            → /api/messaging/accounts*
///
/// Two rules hold everywhere here:
///   * SANDBOX-SAFE — with zero credentials every GET still answers (presets
///     only, defaults, empty account list); nothing throws a 500.
///   * SECRETS NEVER LEAVE — API keys, SMTP passwords and page/bot tokens are
///     returned masked ("••••1234"). A masked value PUT/PATCHed back is treated
///     as "untouched" and keeps the stored secret, the same round-trip idiom
///     Django uses.
/// </summary>
public static class VoiceMessagingEndpoints
{
    // ---- shared helpers -----------------------------------------------------

    private static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        return secret.Length >= 8 ? $"••••{secret[^4..]}" : "••••";
    }

    /// <summary>A masked placeholder round-tripped from a GET — keep the stored secret.</summary>
    private static bool Untouched(string? value) => value != null && value.Contains("••");

    private static string Env(string name) =>
        (Environment.GetEnvironmentVariable(name) ?? "").Trim();

    /// <summary>Setting row (dashboard) wins; else the .env var; else "".</summary>
    private static string StrOverEnv(ScopingService sc, string key, string env)
    {
        var v = sc.SettingValue(key);
        return !string.IsNullOrWhiteSpace(v) ? v.Trim() : Env(env);
    }

    private static string? Str(this Dictionary<string, JsonElement>? body, string key)
    {
        if (body == null || !body.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool Has(this Dictionary<string, JsonElement>? body, string key) =>
        body != null && body.ContainsKey(key);

    private static bool BoolOf(this Dictionary<string, JsonElement>? body, string key, bool dflt)
    {
        if (body == null || !body.TryGetValue(key, out var el)) return dflt;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => el.GetString() is "true" or "True" or "1",
            JsonValueKind.Number => el.TryGetInt32(out var n) && n != 0,
            _ => dflt,
        };
    }

    private static void Audit(AppDbContext db, ClaimsPrincipal me, HttpContext ctx,
        string action, string target, string detail)
    {
        try
        {
            db.AuditLogs.Add(new AuditLog
            {
                Actor = me.Identity?.Name ?? "system",
                Action = action,
                Target = target,
                Detail = detail,
                Ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* an audit failure must never fail the operation itself */ }
    }

    public static void MapVoiceMessagingEndpoints(this IEndpointRouteBuilder app)
    {
        MapVoices(app);
        MapVoicePipelineSettings(app);
        MapAiProviders(app);
        MapMessagingAccounts(app);
    }

    // =========================================================================
    // 1. Voice registry — GET /api/voices
    // =========================================================================

    /// <summary>
    /// Preset voices, identical to <c>calls/voices.PRESETS</c> (and to the
    /// frontend's lib/mocks VOICES fallback). marin/cedar/alloy speak via OpenAI
    /// TTS; puck is a Gemini realtime voice with no standalone TTS endpoint.
    /// </summary>
    private static readonly List<Dictionary<string, object?>> VoicePresets = new()
    {
        new() { ["id"] = "marin", ["name"] = "Marin", ["gender"] = "Female", ["accent"] = "Neutral EN",
                ["language"] = "en", ["persona"] = "advisor", ["provider"] = "openai",
                ["status"] = "ready", ["custom"] = false, ["tag"] = "Default" },
        new() { ["id"] = "cedar", ["name"] = "Cedar", ["gender"] = "Male", ["accent"] = "Neutral EN",
                ["language"] = "en", ["persona"] = "negotiator", ["provider"] = "openai",
                ["status"] = "ready", ["custom"] = false },
        new() { ["id"] = "alloy", ["name"] = "Alloy", ["gender"] = "Neutral", ["accent"] = "US EN",
                ["language"] = "en", ["persona"] = "officer", ["provider"] = "openai",
                ["status"] = "ready", ["custom"] = false },
        new() { ["id"] = "puck", ["name"] = "Puck", ["gender"] = "Male", ["accent"] = "US EN",
                ["language"] = "en", ["persona"] = "counsellor", ["provider"] = "gemini",
                ["status"] = "ready", ["custom"] = false },
    };

    // The keys a voice record may expose. `elevenVoiceId` is deliberately NOT
    // here — the upstream id stays server-side, exactly as in Django.
    private static readonly string[] VoiceKeys =
    {
        "id", "name", "gender", "accent", "age", "description",
        "language", "persona", "provider", "status", "custom",
    };

    private static string DataDir(IConfiguration config) =>
        config["DataDir"] ?? Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// Cloned/imported ElevenLabs voices from <c>data/voices.json</c> — the SAME
    /// file the Django app and the Django-free voice agent share. Missing or
    /// malformed file ⇒ no clones, presets only (the sandbox default).
    /// </summary>
    private static List<Dictionary<string, object?>> ClonedVoices(IConfiguration config)
    {
        var outv = new List<Dictionary<string, object?>>();
        try
        {
            var path = Path.Combine(DataDir(config), "voices.json");
            if (!File.Exists(path)) return outv;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("voices", out var arr) ||
                arr.ValueKind != JsonValueKind.Array) return outv;

            foreach (var v in arr.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                var item = new Dictionary<string, object?>();
                foreach (var key in VoiceKeys)
                {
                    if (!v.TryGetProperty(key, out var el)) continue;
                    item[key] = el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => el.TryGetInt64(out var n) ? n : el.GetDouble(),
                        _ => null,
                    };
                }
                if (v.TryGetProperty("tag", out var tag) &&
                    tag.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(tag.GetString()))
                    item["tag"] = tag.GetString();
                if (item.ContainsKey("id")) outv.Add(item);
            }
        }
        catch { /* consent-sensitive local store — a bad file degrades to presets */ }
        return outv;
    }

    private static void MapVoices(IEndpointRouteBuilder app)
    {
        // GET /api/voices — merged preset + cloned voice list.
        // Open like Django's calls/views.voices_list (no rbac gate): the voice
        // picker fetches it without auth headers, and it exposes no secrets.
        app.MapGet("/api/voices", (IConfiguration config, ScopingService sc) =>
        {
            var voices = new List<Dictionary<string, object?>>(VoicePresets);
            voices.AddRange(ClonedVoices(config));
            // the organization's chosen voice (Voice & calls) — what calls use when none is picked
            return Results.Ok(new { voices, default_voice = sc.SettingValue(DefaultVoiceKey) });
        }).AllowAnonymous();

        // The voice picked on Voice & calls, saved for the whole organization:
        // every call that doesn't name a voice uses it (VoiceDispatch). The
        // voice's own provider is saved with it, so a clone keeps its pipeline.
        var saveDefault = (DefaultVoiceWrite body, IConfiguration config, ScopingService sc) =>
        {
            var id = (body?.voice ?? "").Trim();
            var all = new List<Dictionary<string, object?>>(VoicePresets);
            all.AddRange(ClonedVoices(config));
            var hit = all.FirstOrDefault(v => v.TryGetValue("id", out var x) && x as string == id);
            if (hit == null)
                return Results.Json(new { detail = "Unknown voice — pick one from the library." }, statusCode: 400);
            var provider = hit.TryGetValue("provider", out var p) && p is string ps && ps.Length > 0 ? ps : "elevenlabs";
            sc.SetSetting(DefaultVoiceKey, id);
            sc.SetSetting(DefaultVoiceProviderKey, provider);
            return Results.Ok(new { default_voice = id, provider });
        };
        app.MapPut("/api/voice/default-voice", saveDefault).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    internal const string DefaultVoiceKey = "default_voice";
    internal const string DefaultVoiceProviderKey = "default_voice_provider";
    public record DefaultVoiceWrite(string? voice);

    // =========================================================================
    // 2. Voice pipeline selectors — /api/voice/{stt,llm}-settings
    // =========================================================================

    // calls/voice_settings.STT_PROVIDERS / LLM_PROVIDERS, verbatim.
    internal static readonly string[] SttProviders = { "openai", "elevenlabs", "ilmu" };
    internal static readonly string[] LlmProviders = { "openai", "ilmu", "openrouter", "gemini" };
    private const string DefaultSttProvider = "elevenlabs";

    private static string DefaultLlmProvider()
    {
        var envLlm = Env("PIPELINE_LLM_PROVIDER").ToLowerInvariant();
        return LlmProviders.Contains(envLlm) ? envLlm : "openai";
    }

    public record SttSettingsWrite(string? stt_provider);
    public record LlmSettingsWrite(string? llm_provider);

    private static void MapVoicePipelineSettings(IEndpointRouteBuilder app)
    {
        // Read fresh on every dial, so a change here applies to the NEXT call
        // with no worker restart — the "live wire" the Django views document.
        // Open like calls/views (the selects fetch without auth headers).

        app.MapGet("/api/voice/stt-settings", (ScopingService sc) =>
        {
            var v = sc.SettingValue("stt_provider");
            return Results.Ok(new
            {
                stt_provider = SttProviders.Contains(v) ? v : DefaultSttProvider,
            });
        }).AllowAnonymous();

        // POST is what the frontend sends (SttProviderSelect); PUT is accepted
        // too so either verb works.
        var saveStt = (SttSettingsWrite body, ScopingService sc) =>
        {
            var provider = (body?.stt_provider ?? "").Trim().ToLowerInvariant();
            if (!SttProviders.Contains(provider))
                return Results.Json(new
                {
                    detail = $"stt_provider must be one of {string.Join(", ", SttProviders)}.",
                }, statusCode: 400);
            sc.SetSetting("stt_provider", provider);
            return Results.Ok(new { stt_provider = provider });
        };
        app.MapPost("/api/voice/stt-settings", saveStt).RequireAuthorization(AuthPolicies.AdminPlus);
        app.MapPut("/api/voice/stt-settings", saveStt).RequireAuthorization(AuthPolicies.AdminPlus);

        app.MapGet("/api/voice/llm-settings", (ScopingService sc) =>
        {
            var v = sc.SettingValue("llm_provider");
            return Results.Ok(new
            {
                llm_provider = LlmProviders.Contains(v) ? v : DefaultLlmProvider(),
            });
        }).AllowAnonymous();

        var saveLlm = (LlmSettingsWrite body, ScopingService sc) =>
        {
            var provider = (body?.llm_provider ?? "").Trim().ToLowerInvariant();
            if (!LlmProviders.Contains(provider))
                return Results.Json(new
                {
                    detail = $"llm_provider must be one of {string.Join(", ", LlmProviders)}.",
                }, statusCode: 400);
            sc.SetSetting("llm_provider", provider);
            return Results.Ok(new { llm_provider = provider });
        };
        app.MapPost("/api/voice/llm-settings", saveLlm).RequireAuthorization(AuthPolicies.AdminPlus);
        app.MapPut("/api/voice/llm-settings", saveLlm).RequireAuthorization(AuthPolicies.AdminPlus);
    }

    // =========================================================================
    // 3. AI & provider settings — /api/settings/ai-providers
    // =========================================================================

    /// <summary>
    /// Self-describing field schema — the AI &amp; Providers tab is BUILT from
    /// this payload (label + kind + which are masked), so a new field needs a
    /// row here plus an entry in <see cref="EnvFor"/> / <see cref="IntTunables"/>
    /// and nothing in the frontend. Mirrors core/settings_api.FIELD_META.
    /// </summary>
    public sealed class FieldMeta
    {
        public string key { get; init; } = "";
        public string label { get; init; } = "";
        public string group { get; init; } = "";
        public string kind { get; init; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? min { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? help { get; init; }
    }

    private static readonly FieldMeta[] AiFields =
    {
        new() { key = "openai_api_key", label = "OpenAI API key", group = "providers", kind = "secret" },
        new() { key = "openrouter_api_key", label = "OpenRouter API key", group = "providers", kind = "secret" },
        new() { key = "google_api_key", label = "Google Gemini API key", group = "providers", kind = "secret" },
        new() { key = "ilmu_api_key", label = "ILMU API key (chat)", group = "providers", kind = "secret",
                help = "Used for AI chat/summaries. Live voice calls read the key from the agent's .env." },
        new() { key = "elevenlabs_api_key", label = "ElevenLabs API key", group = "providers", kind = "secret",
                help = "Voice listing / cloning / preview / TTS. Live voice calls read the key from the agent's .env." },
        new() { key = "chat_provider_pref", label = "Chat AI provider", group = "providers", kind = "select" },
        new() { key = "chat_provider_order", label = "Provider fallback order", group = "providers", kind = "order",
                help = "Only used in Automatic mode — the priority the chat brain tries providers " +
                       "in, picking the first with a key set." },
        new() { key = "chat_model", label = "Chat model", group = "providers", kind = "model",
                help = "Applies to whichever provider wins. Empty = that provider's default model; " +
                       "pick Custom… to enter any model id." },
        new() { key = "ai_reviewer_model", label = "AI reviewer model", group = "providers", kind = "text",
                help = "The stronger model the AI monitor's \"AI review\" uses to check past replies (only when someone " +
                       "clicks Review now). Empty = anthropic/claude-sonnet-5.5 on OpenRouter; write provider:model " +
                       "(e.g. openai:gpt-4.1) for another provider. Without that provider's key the normal chat model reviews." },
        new() { key = "local_base_url", label = "Local model base URL", group = "providers", kind = "text",
                help = "For the Local (Ollama / LM Studio) provider — e.g. http://localhost:11434/v1 " +
                       "(Ollama) or http://localhost:1234/v1 (LM Studio). Then pin 'local' above and set " +
                       "the chat model to the model name (e.g. llama3.1). The server must reach this URL." },
        new() { key = "local_api_key", label = "Local model API key (optional)", group = "providers", kind = "secret",
                help = "Optional — Ollama ignores it; some gateways require one." },
        // semantic retrieval (AI learning): Data/Ai/Learning/EmbeddingClient.cs
        new() { key = "embedding_provider", label = "Embeddings provider (semantic search)", group = "embeddings",
                kind = "select",
                help = "Lets the AI find knowledge and lessons by meaning, not just matching words. Automatic " +
                       "uses the first of OpenAI → Gemini → OpenRouter → Local that has a key; Off keeps keyword " +
                       "search only. Uses the API keys above." },
        new() { key = "embedding_model", label = "Embeddings model", group = "embeddings", kind = "text",
                help = "Empty = the provider's default (OpenAI text-embedding-3-small, Gemini gemini-embedding-001, " +
                       "OpenRouter openai/text-embedding-3-small, Local nomic-embed-text). Changing it re-indexes " +
                       "everything in the background." },
        new() { key = "email_subject", label = "Default email subject", group = "tunables", kind = "text" },
        new() { key = "flows_tick_seconds", label = "Flow engine tick (seconds)", group = "tunables", kind = "int", min = 1 },
        new() { key = "whatsapp_health_interval", label = "Channel health check interval (seconds)",
                group = "tunables", kind = "int", min = 30 },
        new() { key = "aicore_summary_interval", label = "Idle-chat summariser interval (seconds)",
                group = "tunables", kind = "int", min = 5,
                help = "Read when the summariser thread starts — a change applies on the next restart." },
        new() { key = "aicore_summary_idle_min", label = "Idle-chat threshold (minutes)",
                group = "tunables", kind = "int", min = 1 },
    };

    // Setting key -> the .env var it falls back to (core/app_settings._WA_STR_ENV
    // + _OWN_STR_ENV + _MSG_KEYS, merged).
    private static readonly Dictionary<string, string> EnvFor = new()
    {
        ["openai_api_key"] = "OPENAI_API_KEY",
        ["openrouter_api_key"] = "OPENROUTER_API_KEY",
        ["google_api_key"] = "GOOGLE_API_KEY",
        ["ilmu_api_key"] = "ILMU_API_KEY",
        ["elevenlabs_api_key"] = "ELEVENLABS_API_KEY",
        ["local_base_url"] = "LOCAL_BASE_URL",
        ["local_api_key"] = "LOCAL_API_KEY",
        ["chat_model"] = "WHATSAPP_CHAT_MODEL",
        ["chat_provider_pref"] = "CHAT_PROVIDER",
        ["email_subject"] = "EMAIL_DEFAULT_SUBJECT",
        ["embedding_provider"] = "EMBEDDING_PROVIDER",
        ["embedding_model"] = "EMBEDDING_MODEL",
        ["ai_reviewer_model"] = "AI_REVIEWER_MODEL",
    };

    // key -> (default, minimum, env var). Mirrors app_settings._INT_TUNABLES.
    private static readonly Dictionary<string, (int Default, int Min, string Env)> IntTunables = new()
    {
        ["flows_tick_seconds"] = (10, 1, "FLOWS_TICK_SECONDS"),
        ["whatsapp_health_interval"] = (300, 30, "WHATSAPP_HEALTH_INTERVAL"),
        ["aicore_summary_interval"] = (600, 5, "AICORE_SUMMARY_INTERVAL"),
        ["aicore_summary_idle_min"] = (120, 1, "AICORE_SUMMARY_IDLE_MIN"),
    };

    private static readonly string[] SecretKeys =
        AiFields.Where(f => f.kind == "secret").Select(f => f.key).ToArray();
    private static readonly string[] StringKeys =
        AiFields.Where(f => f.kind is "text" or "select" or "model").Select(f => f.key).ToArray();

    // wa_config.CHAT_PROVIDERS — "" (first) means automatic priority order.
    private static readonly string[] ChatProviders = { "ilmu", "openrouter", "openai", "google", "local" };
    private static readonly string[] DefaultProviderOrder = { "openrouter", "openai", "ilmu", "google" };
    private const string DefaultEmailSubject = "Regarding your account";
    private const string ChatProviderOrderKey = "chat_provider_order";

    /// <summary>
    /// The pinned chat provider. Django owns this as <c>chat_provider_pref</c>;
    /// the C# chat brain (Ai/OpenAiLlmClient) reads <c>chat_provider</c>. We READ
    /// both and WRITE both, so one dashboard field drives both readers and there
    /// is no silently-ignored pin.
    /// </summary>
    private static string ChatProviderPref(ScopingService sc)
    {
        var v = sc.SettingValue("chat_provider_pref");
        if (string.IsNullOrWhiteSpace(v)) v = sc.SettingValue("chat_provider");
        return !string.IsNullOrWhiteSpace(v) ? v.Trim() : Env("CHAT_PROVIDER");
    }

    /// <summary>
    /// Parse a chat_provider_order value — a JSON list OR a comma/space CSV — to
    /// de-duplicated KNOWN providers. Unusable input ⇒ empty (caller defaults).
    /// </summary>
    private static List<string> ParseProviderOrder(string? raw)
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
            catch { /* fall through to CSV */ }
        }
        if (members.Count == 0)
            members = raw.Split(new[] { ',', ' ', '\t', '\n', '\r' },
                                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(t => t.ToLowerInvariant()).ToList();
        return members;
    }

    private static List<string> ChatProviderOrder(ScopingService sc)
    {
        var raw = StrOverEnv(sc, ChatProviderOrderKey, "CHAT_PROVIDER_ORDER");
        var known = ParseProviderOrder(raw).Where(p => ChatProviders.Contains(p)).Distinct().ToList();
        return known.Count > 0 ? known : DefaultProviderOrder.ToList();
    }

    private static int IntOverEnv(ScopingService sc, string key)
    {
        var (dflt, min, env) = IntTunables[key];
        var raw = StrOverEnv(sc, key, env);
        if (!int.TryParse(raw, out var n)) return dflt;
        return Math.Max(min, n);
    }

    private static string EmailSubject(ScopingService sc)
    {
        var v = StrOverEnv(sc, "email_subject", "EMAIL_DEFAULT_SUBJECT");
        return v.Length > 0 ? v : DefaultEmailSubject;
    }

    private static bool AiAvailable(ScopingService sc) =>
        StrOverEnv(sc, "openrouter_api_key", "OPENROUTER_API_KEY").Length > 0 ||
        StrOverEnv(sc, "openai_api_key", "OPENAI_API_KEY").Length > 0 ||
        StrOverEnv(sc, "ilmu_api_key", "ILMU_API_KEY").Length > 0 ||
        StrOverEnv(sc, "google_api_key", "GOOGLE_API_KEY").Length > 0;

    /// <summary>Where a value comes from: a dashboard row, backend/.env, or nothing.</summary>
    private static string SourceOf(ScopingService sc, string key)
    {
        var stored = key == "chat_provider_pref"
            ? (sc.SettingValue("chat_provider_pref") is { Length: > 0 } a ? a
               : sc.SettingValue("chat_provider"))
            : sc.SettingValue(key);
        if (!string.IsNullOrWhiteSpace(stored)) return "settings";
        var env = key == ChatProviderOrderKey ? "CHAT_PROVIDER_ORDER"
            : IntTunables.TryGetValue(key, out var t) ? t.Env
            : EnvFor.GetValueOrDefault(key, "");
        return env.Length > 0 && Env(env).Length > 0 ? "env" : "";
    }

    private static object ChatModelOptions() => new[]
    {
        new { provider = "openai", label = "OpenAI",
              models = new[] { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini" } },
        new { provider = "openrouter", label = "OpenRouter",
              models = new[] { "openai/gpt-4o-mini", "openai/gpt-4o", "anthropic/claude-3.5-sonnet",
                               "google/gemini-2.0-flash-001", "meta-llama/llama-3.1-70b-instruct" } },
        new { provider = "google", label = "Google Gemini",
              models = new[] { "gemini-2.5-flash", "gemini-2.0-flash", "gemini-1.5-pro", "gemini-1.5-flash" } },
        new { provider = "ilmu", label = "ILMU", models = new[] { "ilmu-v3.1" } },
        new { provider = "local", label = "Local (Ollama / LM Studio)",
              models = new[] { "llama3.1", "llama3.2", "qwen2.5", "mistral", "gemma2" } },
    };

    private const string EnvOnlyNote =
        "Live voice calls read ILMU_API_KEY / ELEVENLABS_API_KEY / PIPELINE_LLM_PROVIDER " +
        "from the voice-agent's .env at call time — the agent process is Django-free and " +
        "cannot read these settings. Keys here power AI chat, summaries and server-side " +
        "voice tooling.";

    /// <summary>The GET/PUT response body — secrets masked, everything else effective.</summary>
    private static Dictionary<string, object?> AiProvidersPayload(ScopingService sc)
    {
        var outv = new Dictionary<string, object?>
        {
            ["fields"] = AiFields,
            ["chat_provider_options"] = new[] { "" }.Concat(ChatProviders).ToArray(),
            ["chat_model_options"] = ChatModelOptions(),
            // embeddings (semantic search): "" = automatic, "none" = keyword only
            ["embedding_provider_options"] = ChatBranch.Data.Ai.Learning.OpenAiEmbeddingClient.Choices,
            ["embedding_model_options"] = ChatBranch.Data.Ai.Learning.OpenAiEmbeddingClient.DefaultModels
                .Select(kv => new { provider = kv.Key, models = new[] { kv.Value } }).ToList(),
        };
        var embedding = ChatBranch.Data.Ai.Learning.OpenAiEmbeddingClient.CurrentFor(sc);
        outv["embedding_status"] = new
        {
            provider = embedding.Provider, model = embedding.Model, configured = embedding.Configured,
        };

        foreach (var f in AiFields)
        {
            switch (f.kind)
            {
                case "secret":
                    outv[$"{f.key}_masked"] = Mask(StrOverEnv(sc, f.key, EnvFor[f.key]));
                    break;
                case "int":
                    outv[f.key] = IntOverEnv(sc, f.key);
                    break;
                case "order":
                    outv[f.key] = ChatProviderOrder(sc);
                    break;
                case "select" when f.key == "chat_provider_pref":
                    outv[f.key] = ChatProviderPref(sc);
                    break;
                case "text" when f.key == "email_subject":
                    outv[f.key] = EmailSubject(sc);
                    break;
                default:
                    outv[f.key] = StrOverEnv(sc, f.key, EnvFor.GetValueOrDefault(f.key, ""));
                    break;
            }
        }

        var sources = new Dictionary<string, string>();
        foreach (var f in AiFields) sources[f.key] = SourceOf(sc, f.key);
        sources[ChatProviderOrderKey] = SourceOf(sc, ChatProviderOrderKey);
        outv["source"] = sources;
        outv["ai_available"] = AiAvailable(sc);
        outv["env_only_note"] = EnvOnlyNote;
        return outv;
    }

    private static void MapAiProviders(IEndpointRouteBuilder app)
    {
        // GET: any authenticated role — secrets are masked.
        app.MapGet("/api/settings/ai-providers", (ScopingService sc) =>
            Results.Ok(AiProvidersPayload(sc)))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("settings.ai_keys.manage");

        // PUT: the settings.ai_keys.manage capability (Settings → AI → AI keys &
        // models) — superadmin-only by default, like every org-wide credential.
        app.MapPut("/api/settings/ai-providers", async (Dictionary<string, JsonElement>? body,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, HttpContext ctx) =>
        {
            if (await SettingsEndpoints.RequireCapability(db, me, "settings.ai_keys.manage") is { } denied)
                return denied;
            var patch = new Dictionary<string, object?>();

            // secrets + free-text/select/model strings: honour the masked round-trip
            foreach (var key in SecretKeys.Concat(StringKeys))
            {
                var value = body.Str(key);
                if (value != null && !Untouched(value)) patch[key] = value.Trim();
            }

            // the provider pin must be a known provider or "" (automatic)
            if (patch.TryGetValue("chat_provider_pref", out var prefRaw))
            {
                var pref = ((string)prefRaw!).ToLowerInvariant();
                if (pref.Length > 0 && !ChatProviders.Contains(pref))
                    return Results.Json(new
                    {
                        detail = "chat_provider_pref must be one of " +
                                 $"{string.Join(", ", ChatProviders)} or empty (auto).",
                    }, statusCode: 400);
                patch["chat_provider_pref"] = pref;
            }

            // the embeddings provider: automatic (""), a known provider, or none
            if (patch.TryGetValue("embedding_provider", out var embRaw))
            {
                var emb = ((string)embRaw!).ToLowerInvariant();
                if (emb is "gemini") emb = "google";
                if (!ChatBranch.Data.Ai.Learning.OpenAiEmbeddingClient.Choices.Contains(emb))
                    return Results.Json(new
                    {
                        detail = "embedding_provider must be one of openai, google, openrouter, local, none or empty (automatic).",
                    }, statusCode: 400);
                patch["embedding_provider"] = emb;
            }

            // fallback order: a list (or JSON/CSV string) of KNOWN providers
            if (body.Has(ChatProviderOrderKey))
            {
                var members = ParseProviderOrder(body.Str(ChatProviderOrderKey)
                                                 ?? RawJsonOf(body, ChatProviderOrderKey));
                var invalid = members.Where(m => !ChatProviders.Contains(m)).Distinct().OrderBy(m => m).ToList();
                if (invalid.Count > 0)
                    return Results.Json(new
                    {
                        detail = $"chat_provider_order has unknown providers: {string.Join(", ", invalid)}. " +
                                 $"Allowed: {string.Join(", ", ChatProviders.OrderBy(p => p))}.",
                    }, statusCode: 400);
                patch[ChatProviderOrderKey] = members.Distinct().ToList();
            }

            // integer tunables: parse + enforce the safe minimum
            foreach (var (key, spec) in IntTunables)
            {
                if (!body.Has(key)) continue;
                var raw = body.Str(key);
                if (string.IsNullOrWhiteSpace(raw)) continue; // nothing to change
                if (!int.TryParse(raw.Trim(), out var num))
                    return Results.Json(new { detail = $"{key} must be an integer." }, statusCode: 400);
                if (num < spec.Min)
                    return Results.Json(new { detail = $"{key} must be at least {spec.Min}." }, statusCode: 400);
                patch[key] = num;
            }

            if (patch.Count == 0)
                return Results.Json(new { detail = "Nothing to update." }, statusCode: 400);

            foreach (var (key, value) in patch)
            {
                if (key == ChatProviderOrderKey)
                {
                    sc.SetSetting(key, JsonSerializer.Serialize((List<string>)value!));
                    continue;
                }
                var text = value is int n ? n.ToString() : (string?)value ?? "";
                sc.SetSetting(key, text);
                // Keep the C# chat brain's own pin key in step with Django's.
                if (key == "chat_provider_pref") sc.SetSetting("chat_provider", text);
            }

            Audit(db, me, ctx, "settings.update", "ai-providers",
                $"keys: {string.Join(", ", patch.Keys.OrderBy(k => k))}");
            return Results.Ok(AiProvidersPayload(sc));
        }).RequireAuthorization(AuthPolicies.Authenticated);
    }

    /// <summary>Raw JSON text for a key — lets an ARRAY body value reach the CSV/JSON parser.</summary>
    private static string RawJsonOf(Dictionary<string, JsonElement>? body, string key) =>
        body != null && body.TryGetValue(key, out var el) ? el.GetRawText() : "";

    // =========================================================================
    // 4. Messaging channel accounts — /api/messaging/*
    // =========================================================================

    private static readonly string[] SocialChannels = { "facebook", "instagram", "telegram" };
    private static readonly string[] ManagedChannels =
    {
        "email", "facebook", "instagram", "telegram",
        // LINE, Viber, SMS, WeChat, TikTok, Douyin, Zalo — MoreChannelAccounts
        "line", "viber", "sms", "wechat", "tiktok", "douyin", "zalo",
    };

    private static string CfgStr(Dictionary<string, object?> cfg, string key)
    {
        if (!cfg.TryGetValue(key, out var v) || v is null) return "";
        if (v is JsonElement el)
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? "",
                JsonValueKind.Number => el.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "",
            };
        return v.ToString() ?? "";
    }

    private static int CfgInt(Dictionary<string, object?> cfg, string key, int dflt) =>
        int.TryParse(CfgStr(cfg, key), out var n) ? n : dflt;

    private static bool CfgBool(Dictionary<string, object?> cfg, string key, bool dflt)
    {
        var s = CfgStr(cfg, key);
        return s.Length == 0 ? dflt : s is "true" or "True" or "1";
    }

    /// <summary>An email account is usable once its SMTP host + login are set.</summary>
    private static bool EmailAccountConfigured(Dictionary<string, object?> cfg) =>
        CfgStr(cfg, "host").Length > 0 && CfgStr(cfg, "username").Length > 0 &&
        CfgStr(cfg, "password").Length > 0;

    /// <summary>messaging/views._account_payload — secrets masked, never raw.</summary>
    private static Dictionary<string, object?> AccountPayload(ChannelAccount a)
    {
        var outv = new Dictionary<string, object?>
        {
            ["id"] = a.Id.ToString(),
            ["channel"] = a.Channel,
            ["name"] = a.Name,
            ["external_id"] = a.ExternalId,
            ["owner"] = a.Owner,
            // Explicit Z: the column is written as UtcNow but SQLite hands it
            // back with Kind=Unspecified, and a stamp without a zone reads as
            // LOCAL time in the browser.
            ["created"] = a.CreatedAt == default ? ""
                : DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc).ToString("o"),
            ["entity"] = a.Entity?.Slug,
            ["entity_name"] = a.Entity?.Name ?? "",
            // "Who answers it": owning team (null = none) + AI default ("" = inherit)
            ["team_id"] = ChatBranch.Data.Channels.InboxSettings.TeamId(a),
            ["ai_default"] = ChatBranch.Data.Channels.InboxSettings.AiDefaultWire(a),
            ["ai_review_mode"] = ChatBranch.Data.Channels.InboxSettings.ReviewModeWire(a),
        };
        var cfg = a.Config ?? new Dictionary<string, object?>();
        if (a.Channel == "email")
        {
            outv["config"] = new Dictionary<string, object?>
            {
                ["host"] = CfgStr(cfg, "host"),
                ["port"] = CfgInt(cfg, "port", 587),
                ["username"] = CfgStr(cfg, "username"),
                ["use_tls"] = CfgBool(cfg, "use_tls", true),
                ["from_email"] = CfgStr(cfg, "from_email") is { Length: > 0 } f ? f : a.ExternalId,
                ["password_masked"] = Mask(CfgStr(cfg, "password")),
                // The edit form pre-fills `config.password` and PATCHes it back
                // verbatim (ChannelsSettings.tsx), so the mask has to live under
                // that key too — Untouched() then keeps the stored secret.
                ["password"] = Mask(CfgStr(cfg, "password")),
                // inbound (IMAP) — blank = the default IMAP host + this mailbox's SMTP login
                ["imap_host"] = CfgStr(cfg, "imap_host"),
                ["imap_port"] = CfgStr(cfg, "imap_port"),
                ["imap_username"] = CfgStr(cfg, "imap_username"),
                ["imap_password_masked"] = Mask(CfgStr(cfg, "imap_password")),
            };
            outv["configured"] = EmailAccountConfigured(cfg);
        }
        else
        {
            outv["access_token_masked"] = Mask(a.AccessToken);
            outv["configured"] = !string.IsNullOrEmpty(a.AccessToken);
            // Messenger / Instagram: the Meta app secret that verifies the webhook
            if (a.Channel is "facebook" or "instagram") outv["app_secret_masked"] = Mask(a.AppSecret);
            if (MoreChannelAccounts.Handles(a.Channel)) MoreChannelAccounts.AddPayload(outv, a);
        }
        return outv;
    }

    private static async Task<ChannelAccount?> ManagedAccount(AppDbContext db, string id)
    {
        if (!int.TryParse(id, out var pk)) return null;
        return await db.ChannelAccounts.Include(a => a.Entity)
            .FirstOrDefaultAsync(a => a.Id == pk && ManagedChannels.Contains(a.Channel));
    }

    /// <summary>
    /// Rebinding an account moves its existing conversations to the new outlet,
    /// so binding a mailbox does not strand its history in the old scope
    /// (messaging/msg_config.update_account does the same).
    /// </summary>
    private static void ResyncThreads(AppDbContext db, ScopingService sc, int accountId)
    {
        try
        {
            var threads = db.Threads.Include(t => t.Account).Include(t => t.Contact)
                .Where(t => t.AccountId == accountId).ToList();
            foreach (var t in threads) sc.SyncThreadEntity(t, save: false);
            db.SaveChanges();
        }
        catch { /* the rebind itself already succeeded */ }
    }

    private static void MapMessagingAccounts(IEndpointRouteBuilder app)
    {
        // ---- GET /api/messaging/accounts — the Channels page's account list ----
        app.MapGet("/api/messaging/accounts", async (AppDbContext db) =>
        {
            var rows = await db.ChannelAccounts.AsNoTracking().Include(a => a.Entity)
                .Where(a => ManagedChannels.Contains(a.Channel))
                .OrderBy(a => a.Id).ToListAsync();
            return Results.Ok(new { accounts = rows.Select(AccountPayload) });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.view");

        // ---- POST /api/messaging/accounts — link a mailbox or a social account ----
        app.MapPost("/api/messaging/accounts", async (Dictionary<string, JsonElement>? body,
            AppDbContext db, ScopingService sc, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var channel = (body.Str("channel") ?? "").Trim().ToLowerInvariant();
            var owner = (body.Str("owner") ?? "").Trim() is { Length: > 0 } o
                ? o : me.Identity?.Name ?? "admin";

            if (channel == "email")
            {
                var host = (body.Str("host") ?? "").Trim();
                var username = (body.Str("username") ?? "").Trim();
                var password = body.Str("password") ?? "";
                var fromEmail = (body.Str("from_email") ?? "").Trim();
                var missing = new List<string>();
                if (host.Length == 0) missing.Add("host");
                if (username.Length == 0) missing.Add("username");
                if (password.Length == 0) missing.Add("password");
                if (fromEmail.Length == 0) missing.Add("from_email");
                if (missing.Count > 0)
                    return Results.Json(new
                    {
                        detail = $"{string.Join(", ", missing)} required for an email account.",
                    }, statusCode: 400);
                if (!fromEmail.Contains('@'))
                    return Results.Json(new { detail = "from_email must be a valid address." }, statusCode: 400);
                var (port, portErr) = ParsePort(body.Str("port"));
                if (portErr != null) return portErr;

                var account = new ChannelAccount
                {
                    Channel = "email",
                    Name = (body.Str("name") ?? "").Trim() is { Length: > 0 } n ? n : fromEmail,
                    ExternalId = fromEmail,
                    Owner = owner,
                    AccessToken = "",
                    CreatedAt = DateTime.UtcNow,
                    Config = new Dictionary<string, object?>
                    {
                        ["host"] = host,
                        ["port"] = port,
                        ["username"] = username,
                        ["password"] = password,
                        ["use_tls"] = body.BoolOf("use_tls", true),
                        ["from_email"] = fromEmail,
                    },
                };
                if (MessagingAccountTransport.ApplyImap(account.Config, body) is IResult imapErr) return imapErr;
                account.EntityId = await InboxEndpoints.EntityId(db, body.Str("entity"));
                if (ApplyInbox(db, account, body) is IResult inboxErr) return inboxErr;
                db.ChannelAccounts.Add(account);
                await db.SaveChangesAsync();
                await db.Entry(account).Reference(a => a.Entity).LoadAsync();
                Audit(db, me, ctx, "account.create", $"email:{account.Name}", "");
                MessagingAccountTransport.Created(account, ctx);
                return Results.Ok(AccountPayload(account));
            }

            if (MoreChannelAccounts.Handles(channel))
            {
                // LINE, Viber, SMS, WeChat, TikTok, Douyin, Zalo: per-channel credential fields
                var (more, moreErr) = MoreChannelAccounts.Build(channel, body, owner);
                if (moreErr != null) return Results.Json(new { detail = moreErr }, statusCode: 400);
                more!.EntityId = await InboxEndpoints.EntityId(db, body.Str("entity"));
                if (ApplyInbox(db, more, body) is IResult moreInboxErr) return moreInboxErr;
                db.ChannelAccounts.Add(more);
                await db.SaveChangesAsync();
                await db.Entry(more).Reference(a => a.Entity).LoadAsync();
                Audit(db, me, ctx, "account.create", $"{more.Channel}:{more.Name}", "");
                MessagingAccountTransport.Created(more, ctx);
                return Results.Ok(AccountPayload(more));
            }

            if (!SocialChannels.Contains(channel))
                return Results.Json(new
                {
                    detail = $"channel must be one of {string.Join(", ", ManagedChannels)}.",
                }, statusCode: 400);

            var token = body.Str("access_token") ?? "";
            if (token.Length == 0)
                return Results.Json(new
                {
                    detail = "access_token (page/bot token) is required.",
                }, statusCode: 400);

            var social = new ChannelAccount
            {
                Channel = channel,
                Name = (body.Str("name") ?? "").Trim() is { Length: > 0 } sn ? sn : channel,
                ExternalId = (body.Str("external_id") ?? "").Trim(),
                AccessToken = token,
                // Messenger / Instagram: verifies X-Hub-Signature-256 on /api/messaging/webhook
                AppSecret = channel is "facebook" or "instagram" ? (body.Str("app_secret") ?? "").Trim() : "",
                Owner = owner,
                CreatedAt = DateTime.UtcNow,
            };
            social.EntityId = await InboxEndpoints.EntityId(db, body.Str("entity"));
            if (ApplyInbox(db, social, body) is IResult socialInboxErr) return socialInboxErr;
            db.ChannelAccounts.Add(social);
            await db.SaveChangesAsync();
            await db.Entry(social).Reference(a => a.Entity).LoadAsync();
            Audit(db, me, ctx, "account.create", $"{social.Channel}:{social.Name}", "");
            MessagingAccountTransport.Created(social, ctx);
            return Results.Ok(AccountPayload(social));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- PATCH /api/messaging/accounts/{id} — rebind, or edit SMTP config ----
        app.MapPatch("/api/messaging/accounts/{id}", async (string id,
            Dictionary<string, JsonElement>? body, AppDbContext db, ScopingService sc,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            var account = await ManagedAccount(db, id);
            if (account == null)
                return Results.Json(new { detail = "Account not found." }, statusCode: 404);

            // Entity binding is editable on EVERY managed channel — a bot or page
            // belongs to an outlet just as much as a mailbox does.
            if (body.Has("entity"))
            {
                var newEntity = await InboxEndpoints.EntityId(db, body.Str("entity"));
                var rebind = account.EntityId != newEntity;
                account.EntityId = newEntity;
                await db.SaveChangesAsync();
                await db.Entry(account).Reference(a => a.Entity).LoadAsync();
                if (rebind) ResyncThreads(db, sc, account.Id);
                Audit(db, me, ctx, "account.entity", $"{account.Channel}:{account.Name}",
                    $"bound to {account.Entity?.Slug ?? "organization-wide"}");
                if (body!.Count == 1) return Results.Ok(AccountPayload(account));
            }

            // Inbox settings (owning team, AI default) — editable on every channel.
            var inboxTouched = body.Has(ChatBranch.Data.Channels.InboxSettings.TeamKey)
                               || body.Has(ChatBranch.Data.Channels.InboxSettings.AiDefaultKey);
            if (inboxTouched && ApplyInbox(db, account, body) is IResult inboxErr) return inboxErr;

            if (MoreChannelAccounts.Handles(account.Channel))
            {
                var (moreTouched, moreCreds, moreErr) = MoreChannelAccounts.ApplyPatch(account, body);
                if (moreErr != null) return Results.Json(new { detail = moreErr }, statusCode: 400);
                if (moreTouched.Count == 0 && !inboxTouched)
                    return Results.Json(new { detail = "Nothing to update." }, statusCode: 400);
                await db.SaveChangesAsync();
                if (moreCreds) await MessagingAccountTransport.CredentialsChangedAsync(account, ctx);
                if (inboxTouched) moreTouched.Add("inbox");
                Audit(db, me, ctx, "account.update", $"{account.Channel}:{account.Name}",
                    $"keys: {string.Join(", ", moreTouched.OrderBy(k => k))}");
                return Results.Ok(AccountPayload(account));
            }

            if (account.Channel != "email")
            {
                // Social accounts: rename, re-point the page/IG id, or re-token
                // (a masked placeholder round-trips as "keep the stored token").
                var socialTouched = new List<string>();
                var newName = (body.Str("name") ?? "").Trim();
                if (newName.Length > 0)
                {
                    account.Name = newName.Length > 120 ? newName[..120] : newName;
                    socialTouched.Add("name");
                }
                if (body.Has("external_id"))
                {
                    account.ExternalId = (body.Str("external_id") ?? "").Trim();
                    socialTouched.Add("external_id");
                }
                var newToken = body.Str("access_token") ?? "";
                if (newToken.Trim().Length > 0 && !Untouched(newToken))
                {
                    account.AccessToken = newToken.Trim();
                    // a re-tokened account must become routable again now
                    account.HealthStatus = Health.Unknown;
                    account.HealthDetail = "";
                    account.FailStreak = 0;
                    account.HealthChangedAt = DateTime.UtcNow;
                    socialTouched.Add("access_token");
                }
                var newSecret = body.Str("app_secret") ?? "";
                if (account.Channel is "facebook" or "instagram" && body.Has("app_secret")
                    && !Untouched(newSecret))
                {
                    account.AppSecret = newSecret.Trim();
                    socialTouched.Add("app_secret");
                }
                if (socialTouched.Count == 0 && !inboxTouched)
                    return Results.Json(new { detail = "Nothing to update." }, statusCode: 400);
                await db.SaveChangesAsync();
                if (socialTouched.Contains("access_token"))
                    await MessagingAccountTransport.CredentialsChangedAsync(account, ctx);
                if (inboxTouched) socialTouched.Add("inbox");
                Audit(db, me, ctx, "account.update", $"{account.Channel}:{account.Name}",
                    $"keys: {string.Join(", ", socialTouched.OrderBy(k => k))}");
                return Results.Ok(AccountPayload(account));
            }

            var cfg = new Dictionary<string, object?>(account.Config ?? new());
            var touched = new List<string>();
            foreach (var key in new[] { "host", "username" })
            {
                if (!body.Has(key)) continue;
                var value = (body.Str(key) ?? "").Trim();
                if (value.Length == 0)
                    return Results.Json(new { detail = $"{key} cannot be empty." }, statusCode: 400);
                cfg[key] = value;
                touched.Add(key);
            }
            if (body.Has("password"))
            {
                var value = body.Str("password") ?? "";
                // The masked placeholder round-trips from GET — keep the stored secret.
                if (value.Length > 0 && !Untouched(value)) { cfg["password"] = value; touched.Add("password"); }
            }
            if (body.Has("port"))
            {
                var (port, portErr) = ParsePort(body.Str("port"));
                if (portErr != null) return portErr;
                cfg["port"] = port;
                touched.Add("port");
            }
            if (body.Has("use_tls")) { cfg["use_tls"] = body.BoolOf("use_tls", true); touched.Add("use_tls"); }
            if (body.Has("from_email"))
            {
                var value = (body.Str("from_email") ?? "").Trim();
                if (!value.Contains('@'))
                    return Results.Json(new { detail = "from_email must be a valid address." }, statusCode: 400);
                cfg["from_email"] = value;
                account.ExternalId = value; // the from-address doubles as the external id
                touched.Add("from_email");
            }
            if (MessagingAccountTransport.ApplyImap(cfg, body, touched) is IResult imapErr) return imapErr;
            var name = (body.Str("name") ?? "").Trim();
            if (touched.Count == 0 && name.Length == 0 && !inboxTouched)
                return Results.Json(new { detail = "Nothing to update." }, statusCode: 400);
            if (name.Length > 0)
            {
                account.Name = name.Length > 120 ? name[..120] : name;
                touched.Add("name");
            }
            if (inboxTouched) touched.Add("inbox");
            // the edit form re-sends every field — only a real change is a reconnect
            var credsChanged = MessagingAccountTransport.EmailCredentialKeys.Any(k =>
                ChatBranch.Data.Channels.EmailConfig.Cfg(account.Config ?? new(), k, trim: false)
                != ChatBranch.Data.Channels.EmailConfig.Cfg(cfg, k, trim: false));
            account.Config = cfg;
            await db.SaveChangesAsync();
            if (credsChanged) await MessagingAccountTransport.CredentialsChangedAsync(account, ctx);
            Audit(db, me, ctx, "account.update", $"email:{account.Name}",
                $"keys: {string.Join(", ", touched.OrderBy(k => k))}");
            return Results.Ok(AccountPayload(account));
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- DELETE /api/messaging/accounts/{id} ----
        app.MapDelete("/api/messaging/accounts/{id}", async (string id, AppDbContext db,
            ClaimsPrincipal me, HttpContext ctx) =>
        {
            var account = await ManagedAccount(db, id);
            if (account == null)
                return Results.Json(new { detail = "Account not found." }, statusCode: 404);
            var label = $"{account.Channel}:{account.Name}";
            // conversations stay (back in sandbox) — unlink them first
            await ChatBranch.Data.Channels.InboxSettings.DetachAccountAsync(db, account.Id);
            db.ChannelAccounts.Remove(account);
            await db.SaveChangesAsync();
            Audit(db, me, ctx, "account.delete", label, "");
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        // ---- POST /api/messaging/accounts/{id}/test ----
        // Every channel is live: Messenger / Instagram (GET /me with the page
        // token), email (SMTP connect + login, optional test mail) and Telegram
        // (getMe) — MessagingAccountTransport. Anything else reports honestly
        // (501) while still telling the operator whether the stored
        // configuration LOOKS complete.
        app.MapPost("/api/messaging/accounts/{id}/test", async (string id, AppDbContext db,
            ChatBranch.Data.Channels.IMetaMessagingClient meta, HttpContext ctx) =>
        {
            var account = await ManagedAccount(db, id);
            if (account == null)
                return Results.Json(new { detail = "Account not found." }, statusCode: 404);
            if (account.Channel is "facebook" or "instagram")
                return await MetaAccountTest(db, meta, account);
            if (await MessagingAccountTransport.TestAsync(account, ctx) is IResult live) return live;
            var configured = account.Channel == "email"
                ? EmailAccountConfigured(account.Config ?? new())
                : !string.IsNullOrEmpty(account.AccessToken);
            return Results.Json(new
            {
                ok = false,
                detail = configured
                    ? $"Stored {account.Channel} configuration looks complete, but live connection " +
                      "testing is not wired up in this backend yet."
                    : $"This {account.Channel} account is not fully configured yet.",
            }, statusCode: 501);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");   // a Test talks to the provider (and can send mail)

        // ---- POST /api/messaging/accounts/{id}/set-webhook ----
        app.MapPost("/api/messaging/accounts/{id}/set-webhook", async (string id,
            Dictionary<string, JsonElement>? body, AppDbContext db, HttpContext ctx) =>
        {
            var account = await ManagedAccount(db, id);
            if (account == null)
                return Results.Json(new { detail = "Account not found." }, statusCode: 404);
            // LINE (webhook endpoint API) and Viber (set_webhook)
            if (MoreChannelAccounts.CanSetWebhook(account.Channel))
                return await MoreChannelAccounts.SetWebhookAsync(account, body, ctx);
            if (account.Channel != "telegram")
                return Results.Json(new
                {
                    detail = "Webhook registration is for Telegram, LINE and Viber accounts — paste the webhook URL "
                             + "into the platform's console for the others.",
                }, statusCode: 400);
            // setWebhook with a secret_token; the bot token (the path secret) is never echoed back
            return await MessagingAccountTransport.SetWebhookAsync(account, body, ctx);
        }).RequireAuthorization(AuthPolicies.AdminPlus);

        MapEmailSettings(app);
    }

    /// <summary>
    /// Messenger / Instagram "Test connection": GET /me with the page token. A
    /// success heals the account, a refused token disconnects it — both through
    /// AccountHealth.SetStatus (via MetaErrors), one audit row per transition.
    /// </summary>
    private static async Task<IResult> MetaAccountTest(AppDbContext db,
        ChatBranch.Data.Channels.IMetaMessagingClient meta, ChannelAccount account)
    {
        if (!meta.IsConfigured(account))
            return Results.Json(new { ok = false, detail = $"This {account.Channel} account has no page token yet." },
                statusCode: 400);
        var r = await meta.GetMeAsync(account);
        if (r.Ok)
        {
            ChatBranch.Data.Channels.MetaErrors.MarkHealthy(db, account, "account test passed", actor: "user");
            await db.SaveChangesAsync();
            var name = ChatBranch.Data.Channels.MetaGraph.Str(r.Body, "name");
            return Results.Ok(new
            {
                ok = true,
                detail = $"Connected{(name.Length > 0 ? $" as {name}" : "")}.",
                name,
                id = ChatBranch.Data.Channels.MetaGraph.Str(r.Body, "id"),
                health_status = account.HealthStatus,
            });
        }
        // a token Meta refuses is a dead connection (a classified code — rate
        // limit, policy block — keeps its own verdict); an unreachable Graph is
        // not the account's fault — health stays put
        if (r.ErrorCode != null)
        {
            if (ChatBranch.Data.Channels.MetaErrors.Classify(r.ErrorCode, r.ErrorSubcode).Health != null)
                ChatBranch.Data.Channels.MetaErrors.ApplyToAccount(db, account, r.ErrorCode, r.ErrorSubcode,
                    r.Detail, actor: "user");
            else
                ChatBranch.Data.Channels.AccountHealth.SetStatus(db, account, Health.Disconnected,
                    $"account test failed: {r.Detail}", actor: "user");
        }
        await db.SaveChangesAsync();
        return Results.Json(new
        {
            ok = false,
            detail = $"Meta refused the page token: {r.Detail}",
            health_status = account.HealthStatus,
        }, statusCode: r.ErrorCode != null ? 400 : 502);
    }

    /// <summary>
    /// Apply the inbox keys (team_id, ai_default) a write body carries; absent
    /// keys stay untouched, a blank/null value clears back to inherit.
    /// </summary>
    private static IResult? ApplyInbox(AppDbContext db, ChannelAccount account,
        Dictionary<string, JsonElement>? body)
    {
        string? Raw(string key) => body.Has(key) ? body.Str(key) ?? "" : null;
        var err = ChatBranch.Data.Channels.InboxSettings.Apply(db, account,
            Raw(ChatBranch.Data.Channels.InboxSettings.TeamKey),
            Raw(ChatBranch.Data.Channels.InboxSettings.AiDefaultKey),
            Raw(ChatBranch.Data.Channels.InboxSettings.ReviewModeKey));
        return err == null ? null : Results.Json(new { detail = err }, statusCode: 400);
    }

    private static (int Port, IResult? Error) ParsePort(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (587, null); // the SMTP default
        if (!int.TryParse(raw.Trim(), out var n))
            return (0, Results.Json(new { detail = "port must be an integer." }, statusCode: 400));
        return (n, null);
    }

    // ---- global SMTP fallback settings — /api/messaging/settings ----------------
    // The Channels page loads this ALONGSIDE the account list in one Promise.all,
    // so a 404 here blanks the whole tab. Ported from messaging/views.settings_view
    // (+ msg_config.email_settings): Setting row wins, .env is the fallback.

    private static readonly Dictionary<string, (string Env, string Default)> EmailSettingKeys = new()
    {
        ["smtp_host"] = ("SMTP_HOST", ""),
        ["smtp_port"] = ("SMTP_PORT", "587"),
        ["smtp_username"] = ("SMTP_USERNAME", ""),
        ["smtp_from"] = ("SMTP_FROM", ""),
        ["smtp_tls"] = ("SMTP_TLS", "true"),
        ["email_subject"] = ("EMAIL_DEFAULT_SUBJECT", DefaultEmailSubject),
        ["imap_host"] = ("IMAP_HOST", ""),
        ["imap_port"] = ("IMAP_PORT", "993"),
    };
    private const string SmtpPasswordKey = "smtp_password";

    private static string EmailSetting(ScopingService sc, string key)
    {
        var (env, dflt) = EmailSettingKeys[key];
        var v = StrOverEnv(sc, key, env);
        return v.Length > 0 ? v : dflt;
    }

    private static Dictionary<string, object?> EmailSettingsPayload(ScopingService sc)
    {
        var outv = new Dictionary<string, object?>();
        foreach (var key in EmailSettingKeys.Keys) outv[key] = EmailSetting(sc, key);
        outv["smtp_password_masked"] = Mask(StrOverEnv(sc, SmtpPasswordKey, "SMTP_PASSWORD"));
        outv["configured"] = EmailSetting(sc, "smtp_host").Length > 0 &&
                             EmailSetting(sc, "smtp_from").Length > 0;
        return outv;
    }

    private static void MapEmailSettings(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/messaging/settings", (ScopingService sc) =>
            Results.Ok(EmailSettingsPayload(sc)))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.view");

        var save = (Dictionary<string, JsonElement>? body, AppDbContext db,
            ScopingService sc, ClaimsPrincipal me, HttpContext ctx) =>
        {
            var touched = new List<string>();
            foreach (var key in EmailSettingKeys.Keys.Append(SmtpPasswordKey))
            {
                var value = body.Str(key);
                if (value == null || Untouched(value)) continue; // masked ⇒ untouched
                sc.SetSetting(key, value);
                touched.Add(key);
            }
            if (touched.Count == 0)
                return Results.Json(new { detail = "Nothing to update." }, statusCode: 400);
            Audit(db, me, ctx, "settings.update", "email-smtp",
                $"keys: {string.Join(", ", touched.OrderBy(k => k))}");
            return Results.Ok(EmailSettingsPayload(sc));
        };
        app.MapPost("/api/messaging/settings", save).RequireAuthorization(AuthPolicies.SuperAdmin);
        app.MapPut("/api/messaging/settings", save).RequireAuthorization(AuthPolicies.SuperAdmin);

        app.MapPost("/api/messaging/settings/test", async (Dictionary<string, JsonElement>? body,
            HttpContext ctx) =>
        {
            var to = (body.Str("to") ?? "").Trim();
            if (!to.Contains('@'))
                return Results.Json(new { detail = "Valid 'to' email required." }, statusCode: 400);
            return await MessagingAccountTransport.DefaultSmtpTestAsync(to, ctx);
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("channels.manage");   // a Test talks to the provider (and can send mail)
    }
}
