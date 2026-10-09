using ChatBranch.Data.Ai.Learning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Tests;

/// <summary>
/// A deterministic embedding "model": words map to concepts (so "postage" and
/// "delivery" land on the same dimension, like a real model would place them
/// close), everything else to its stem, hashed into 256 dimensions. No network.
/// </summary>
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public bool Configured { get; set; } = true;
    public bool Fail { get; set; }
    public string Model { get; set; } = "fake-embed-1";
    private int _calls, _texts;
    public int Calls => _calls;
    public int Texts => _texts;

    private static readonly Dictionary<string, string> Concepts = new()
    {
        ["postage"] = "ship", ["shipping"] = "ship", ["ship"] = "ship", ["delivery"] = "ship", ["deliver"] = "ship",
        ["courier"] = "ship", ["penghantaran"] = "ship", ["hantar"] = "ship",
        ["cost"] = "price", ["costs"] = "price", ["price"] = "price", ["fee"] = "price", ["charge"] = "price",
        ["much"] = "price", ["harga"] = "price", ["kos"] = "price",
        ["open"] = "hours", ["opening"] = "hours", ["hours"] = "hours", ["times"] = "hours", ["time"] = "hours",
        ["buka"] = "hours",
        ["refund"] = "refund", ["money"] = "refund", ["back"] = "refund", ["return"] = "refund",
    };

    public EmbeddingConfig Current() => Configured ? new EmbeddingConfig("fake", Model, true, "") : EmbeddingConfig.None;

    public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _texts, texts.Count);
        if (Fail) throw new HttpRequestException("fake embeddings answered 503 Service Unavailable.");
        return Task.FromResult(new EmbeddingBatch(texts.Select(Vec).ToList(), texts.Sum(t => t.Length / 4), "fake", Model));
    }

    public static float[] Vec(string text)
    {
        var v = new float[256];
        foreach (var w in TextAnalysis.Words(text))
        {
            string key;
            if (Concepts.TryGetValue(w, out var c)) key = c;
            else if (TextAnalysis.IsStopword(w) || w.Length < 2) continue;
            else key = TextAnalysis.Stem(w);
            v[Bucket(key)] += 1;
        }
        return VectorMath.Normalize(v);
    }

    private static int Bucket(string key)
    {
        uint h = 2166136261;
        foreach (var ch in key) { h ^= ch; h *= 16777619; }
        return (int)(h % 256);
    }
}

/// <summary>The API host with the fake embedding model (semantic retrieval on).</summary>
public sealed class LearningFactory : ApiFactory
{
    public FakeEmbeddingClient Embeddings { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IEmbeddingClient>(Embeddings));
    }
}
