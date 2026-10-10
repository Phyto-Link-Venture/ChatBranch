using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatBranch.Data.Ai.Learning;

/// <summary>Which embedding provider/model would answer right now (no network).</summary>
/// <param name="Provider">openai | google | local | none.</param>
/// <param name="Requested">The stored choice: "" (automatic), openai, google, local or none.</param>
public sealed record EmbeddingConfig(string Provider, string Model, bool Configured, string Requested)
{
    public static readonly EmbeddingConfig None = new("none", "", false, "");
}

/// <summary>One embedding call's vectors (L2-normalised, input order) + metering.</summary>
public sealed record EmbeddingBatch(IReadOnlyList<float[]> Vectors, int PromptTokens, string Provider, string Model);

/// <summary>
/// The embedding boundary semantic retrieval sits behind. The real client talks
/// to an OpenAI-compatible <c>/embeddings</c> endpoint; tests swap in a
/// deterministic fake; with nothing configured retrieval is keyword-only.
/// </summary>
public interface IEmbeddingClient
{
    EmbeddingConfig Current();

    /// <summary>Embed the texts. Throws on a provider failure — callers catch and
    /// fall back to keyword retrieval.</summary>
    Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>Nothing configured: keyword retrieval only.</summary>
public sealed class NoEmbeddingClient : IEmbeddingClient
{
    public static readonly NoEmbeddingClient Instance = new();
    public EmbeddingConfig Current() => EmbeddingConfig.None;

    public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
        throw new InvalidOperationException("No embedding provider is configured.");
}

/// <summary>
/// Embeddings over the OpenAI-compatible <c>/embeddings</c> API:
/// <list type="bullet">
///   <item><c>openai</c> — https://api.openai.com/v1, <c>openai_api_key</c>, <c>text-embedding-3-small</c></item>
///   <item><c>google</c> — Gemini's OpenAI-compatible endpoint, <c>google_api_key</c>, <c>gemini-embedding-001</c>
///         (text-embedding-004 was retired by Google in January 2026)</item>
///   <item><c>openrouter</c> — https://openrouter.ai/api/v1, <c>openrouter_api_key</c>,
///         <c>openai/text-embedding-3-small</c> (OpenRouter's embedding models, billed as OpenRouter credits —
///         Anthropic makes no embedding model, so a BYOK Anthropic key never covers these)</item>
///   <item><c>local</c> — <c>local_base_url</c> (Ollama / LM Studio), optional <c>local_api_key</c>,
///         <c>nomic-embed-text</c></item>
/// </list>
/// Setting <c>embedding_provider</c> (env <c>EMBEDDING_PROVIDER</c>) picks one;
/// "" = automatic (the first configured of openai → google → openrouter → local), <c>none</c>
/// = keyword retrieval only. <c>embedding_model</c> (env <c>EMBEDDING_MODEL</c>)
/// overrides the model. Keys come from the same Setting rows (env fallback) as
/// the chat brain and are never logged; provider bodies are never logged either.
/// </summary>
public class OpenAiEmbeddingClient : IEmbeddingClient
{
    public const string ProviderKey = "embedding_provider";
    public const string ModelKey = "embedding_model";
    public static readonly string[] Providers = { "openai", "google", "openrouter", "local" };
    /// <summary>Accepted values of <c>embedding_provider</c> ("" = automatic).</summary>
    public static readonly string[] Choices = { "", "openai", "google", "openrouter", "local", "none" };
    public static readonly IReadOnlyDictionary<string, string> DefaultModels = new Dictionary<string, string>
    {
        ["openai"] = "text-embedding-3-small",
        ["google"] = "gemini-embedding-001",
        ["openrouter"] = "openai/text-embedding-3-small",
        ["local"] = "nomic-embed-text",
    };
    private const int MaxInputChars = 8000;

    private readonly IHttpClientFactory _http;
    private readonly ScopingService _scoping;

    public OpenAiEmbeddingClient(IHttpClientFactory http, ScopingService scoping)
    {
        _http = http;
        _scoping = scoping;
    }

    private sealed record Endpoint(string Provider, string BaseUrl, string ApiKey, string Model);

    private static string S(ScopingService scoping, string key, params string[] envs)
    {
        var v = scoping.SettingValue(key);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        foreach (var e in envs)
            if (Environment.GetEnvironmentVariable(e) is { Length: > 0 } ev && ev.Trim().Length > 0)
                return ev.Trim();
        return "";
    }

    private static Endpoint? Resolve(ScopingService scoping, out string requested)
    {
        string S(string key, params string[] envs) => OpenAiEmbeddingClient.S(scoping, key, envs);
        requested = S(ProviderKey, "EMBEDDING_PROVIDER").ToLowerInvariant();
        if (requested is "gemini") requested = "google";
        if (requested is "open_router" or "open-router") requested = "openrouter";
        if (requested is "off" or "disabled") requested = "none";
        if (requested == "none") return null;

        var keyed = new Dictionary<string, Endpoint>();
        var openai = S("openai_api_key", "OPENAI_API_KEY");
        if (openai.Length > 0) keyed["openai"] = new("openai", "https://api.openai.com/v1", openai, DefaultModels["openai"]);
        var google = S("google_api_key", "GOOGLE_API_KEY", "GEMINI_API_KEY");
        if (google.Length > 0) keyed["google"] = new("google", OpenAiLlmClient.GeminiBaseUrl, google, DefaultModels["google"]);
        var openrouter = S("openrouter_api_key", "OPENROUTER_API_KEY");
        if (openrouter.Length > 0)
            keyed["openrouter"] = new("openrouter", "https://openrouter.ai/api/v1", openrouter, DefaultModels["openrouter"]);
        var local = S("local_base_url", "LOCAL_BASE_URL");
        if (local.Length > 0)
            keyed["local"] = new("local", local.TrimEnd('/'), S("local_api_key", "LOCAL_API_KEY"), DefaultModels["local"]);

        Endpoint? pick = null;
        if (Providers.Contains(requested)) pick = keyed.GetValueOrDefault(requested);
        else pick = Providers.Where(keyed.ContainsKey).Select(p => keyed[p]).FirstOrDefault();
        if (pick == null) return null;
        var model = S(ModelKey, "EMBEDDING_MODEL");
        return model.Length > 0 ? pick with { Model = model } : pick;
    }

    public EmbeddingConfig Current() => CurrentFor(_scoping);

    /// <summary>The effective provider/model for these settings (no network, never throws).</summary>
    public static EmbeddingConfig CurrentFor(ScopingService scoping)
    {
        try
        {
            var ep = Resolve(scoping, out var requested);
            return ep == null
                ? EmbeddingConfig.None with { Requested = requested }
                : new EmbeddingConfig(ep.Provider, ep.Model, true, requested);
        }
        catch { return EmbeddingConfig.None; }
    }

    public async Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var ep = Resolve(_scoping, out _) ?? throw new InvalidOperationException("No embedding provider is configured.");
        if (texts.Count == 0) return new EmbeddingBatch(Array.Empty<float[]>(), 0, ep.Provider, ep.Model);

        var client = _http.CreateClient("embeddings");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ep.BaseUrl}/embeddings");
        if (ep.ApiKey.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ep.ApiKey}");
        req.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["model"] = ep.Model,
            ["input"] = texts.Select(t => (t ?? "").Length <= MaxInputChars ? t ?? "" : t![..MaxInputChars]).ToList(),
        });
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            // status only — a provider body can echo the request or the key
            throw new HttpRequestException($"{ep.Provider} embeddings answered {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var vectors = new float[texts.Count][];
        if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var row in data.EnumerateArray())
            {
                var index = row.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : i;
                i++;
                if (index < 0 || index >= vectors.Length || !row.TryGetProperty("embedding", out var emb)
                    || emb.ValueKind != JsonValueKind.Array) continue;
                vectors[index] = VectorMath.Normalize(emb.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray());
            }
        }
        if (vectors.Any(v => v == null || v.Length == 0))
            throw new InvalidOperationException($"{ep.Provider} embeddings returned an incomplete answer.");
        var tokens = 0;
        if (json.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            tokens = usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32()
                : usage.TryGetProperty("total_tokens", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
        var model = json.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? ep.Model : ep.Model;
        // the model WE asked for keys the stored vectors (providers echo variants, e.g. "models/…")
        _ = model;
        return new EmbeddingBatch(vectors, tokens, ep.Provider, ep.Model);
    }
}

/// <summary>Vector helpers: normalisation, cosine (dot of unit vectors), BLOB codec.</summary>
public static class VectorMath
{
    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * (double)x;
        if (sum <= 0) return v;
        var n = (float)Math.Sqrt(sum);
        var outv = new float[v.Length];
        for (var i = 0; i < v.Length; i++) outv[i] = v[i] / n;
        return outv;
    }

    /// <summary>Cosine of two unit vectors (0 when the dimensions differ).</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * (double)b[i];
        return dot;
    }

    /// <summary>float32 little-endian, as stored in <c>crm_embedding.vector</c>.</summary>
    public static byte[] ToBlob(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), v[i]);
        return bytes;
    }

    public static float[] FromBlob(byte[]? blob)
    {
        if (blob == null || blob.Length < 4) return Array.Empty<float>();
        var v = new float[blob.Length / 4];
        for (var i = 0; i < v.Length; i++)
            v[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * 4, 4));
        return v;
    }
}

/// <summary>
/// Decoded vectors kept in memory, keyed by model + content hash, so a search
/// reads only the small metadata columns and decodes a BLOB once per chunk
/// version. Bounded: cleared wholesale past <see cref="MaxEntries"/>.
/// </summary>
public static class VectorCache
{
    public const int MaxEntries = 120_000;
    private static readonly ConcurrentDictionary<string, float[]> Vectors = new();

    public static string Key(string model, string hash) => model + "|" + hash;

    public static bool TryGet(string model, string hash, out float[] vector) =>
        Vectors.TryGetValue(Key(model, hash), out vector!);

    public static void Put(string model, string hash, float[] vector)
    {
        if (Vectors.Count >= MaxEntries) Vectors.Clear();
        Vectors[Key(model, hash)] = vector;
    }

    public static void Clear() => Vectors.Clear();
}
