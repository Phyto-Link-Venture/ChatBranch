using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// More AI providers: the catalog drives the chat brain (key → endpoint → model,
/// Anthropic's required max_tokens, regional base URLs), the settings page
/// (provider tiles, saving a key, Test connection) and the price lookup for a
/// model called straight at its vendor.
/// </summary>
public class LlmProviderCatalogTests : IClassFixture<PlaygroundFactory>
{
    private readonly PlaygroundFactory _f;
    public LlmProviderCatalogTests(PlaygroundFactory f) => _f = f;

    /// <summary>Records the raw request; answers like any chat-completions endpoint.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public readonly List<(Uri Url, string Auth, JsonElement Body)> Calls = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (Calls) Calls.Add((request.RequestUri!, request.Headers.Authorization?.ToString() ?? "", body));
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { Content = new StringContent("{\"error\":\"invalid x-api-key\"}") };
            var payload = new
            {
                model = body.GetProperty("model").GetString(),
                choices = new[] { new { message = new { role = "assistant", content = "OK" } } },
                usage = new { prompt_tokens = 10, completion_tokens = 1 },
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public Factory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    [Fact]
    public void A_key_turns_a_provider_on_with_its_endpoint_and_model()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var client = new OpenAiLlmClient(new Factory(new Recorder()), sc);
        Assert.Empty(client.Configured());

        sc.SetSetting("anthropic_api_key", "sk-ant-test-1234");
        sc.SetSetting("qwen_api_key", "sk-qwen-5678");
        sc.SetSetting("qwen_base_url", "https://dashscope.aliyuncs.com/compatible-mode/v1/");
        sc.SetSetting("anthropic_chat_model", "claude-sonnet-5-5");

        var on = client.Configured().ToDictionary(p => p.Name);
        Assert.Equal(new[] { "anthropic", "qwen" }, client.Configured().Select(p => p.Name));
        Assert.Equal("https://api.anthropic.com/v1", on["anthropic"].BaseUrl);
        Assert.Equal("claude-sonnet-5-5", on["anthropic"].Model);
        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1", on["qwen"].BaseUrl);
        Assert.Equal("qwen-plus", on["qwen"].Model);
        Assert.True(client.SupportsVision); // Anthropic answers first and reads images

        // a pin by a nickname, and the automatic order honours the catalog
        sc.SetSetting("chat_provider", "kimi");
        Assert.Equal(new[] { "anthropic", "qwen" }, client.Candidates().Select(p => p.Name)); // kimi has no key
        sc.SetSetting("chat_provider", "qwen");
        Assert.Equal("qwen", Assert.Single(client.Candidates()).Name);
        Assert.Equal("anthropic", client.Pinned("claude")!.Candidates()[0].Name);
    }

    [Fact]
    public async Task Anthropic_gets_max_tokens_and_a_bearer_key()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        sc.SetSetting("anthropic_api_key", "sk-ant-test-1234");
        var rec = new Recorder();
        var client = new OpenAiLlmClient(new Factory(rec), sc);

        var r = await client.ChatAsync("sys", new List<LlmMessage> { new("user", "hi") });
        Assert.Equal("anthropic", r.Provider);
        var (url, auth, body) = Assert.Single(rec.Calls);
        Assert.Equal("https://api.anthropic.com/v1/chat/completions", url.ToString());
        Assert.Equal("Bearer sk-ant-test-1234", auth);
        Assert.Equal(OpenAiLlmClient.MaxTokensDefault, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("claude-haiku-5-5", body.GetProperty("model").GetString());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Test_connection_tries_a_typed_key_and_never_echoes_it()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var rec = new Recorder();
        var client = new OpenAiLlmClient(new Factory(rec), sc);

        var none = await client.TestAsync("deepseek");
        Assert.False(none.Ok);
        Assert.Contains("API key", none.Error);

        var ok = await client.TestAsync("deepseek", apiKey: "sk-typed-abcdef");
        Assert.True(ok.Ok);
        Assert.Equal("deepseek-chat", ok.Model);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", rec.Calls[^1].Url.ToString());

        rec.Status = HttpStatusCode.Unauthorized;
        var bad = await client.TestAsync("deepseek", apiKey: "sk-typed-abcdef");
        Assert.False(bad.Ok);
        Assert.Contains("401", bad.Error);
        Assert.DoesNotContain("sk-typed-abcdef", bad.Error);

        Assert.False((await client.TestAsync("nope")).Ok);
    }

    [Fact]
    public void A_vendor_model_is_priced_from_the_openrouter_list()
    {
        var prices = new Dictionary<string, (double In, double Out)>
        {
            ["anthropic/claude-haiku-5.5"] = (1.0, 5.0),
            ["qwen/qwen-plus"] = (0.4, 1.2),
            ["vendor-a/same-name"] = (1, 1),
            ["vendor-b/same-name"] = (2, 2),
            ["gpt-4o-mini"] = (0.15, 0.6),
        };
        Assert.True(LlmPricing.TryPrice(prices, "claude-haiku-5-5", out var claude));
        Assert.Equal((1.0, 5.0), claude);
        Assert.True(LlmPricing.TryPrice(prices, "qwen-plus", out _));
        Assert.True(LlmPricing.TryPrice(prices, "gpt-4o-mini", out _));
        Assert.False(LlmPricing.TryPrice(prices, "same-name", out _));      // ambiguous → unknown
        Assert.False(LlmPricing.TryPrice(prices, "nobody/claude-haiku-5-5", out _));
        Assert.Equal(1.0 + 5.0, LlmPricing.RowCost(prices, "claude-haiku-5-5", 1_000_000, 1_000_000, 0, 0, null), 6);
    }

    private async Task<HttpClient> Super()
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "superadmin", password = "super123" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task The_settings_page_lists_every_provider_saves_a_key_and_tests_it()
    {
        var su = await Super();
        var page = await su.GetFromJsonAsync<JsonElement>("/api/settings/ai-providers");
        var tiles = page.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal(LlmProviderCatalog.All.Count, tiles.Count);
        var mistral = tiles.Single(p => p.GetProperty("id").GetString() == "mistral");
        Assert.False(mistral.GetProperty("configured").GetBoolean());
        Assert.Equal("mistral_api_key", mistral.GetProperty("key_field").GetString());
        var qwen = tiles.Single(p => p.GetProperty("id").GetString() == "qwen");
        Assert.Equal(2, qwen.GetProperty("endpoints").GetArrayLength());

        var put = await su.PutAsJsonAsync("/api/settings/ai-providers",
            new Dictionary<string, string> { ["mistral_api_key"] = "mk-secret-998877", ["mistral_chat_model"] = "mistral-large-latest" });
        put.EnsureSuccessStatusCode();
        var after = await put.Content.ReadFromJsonAsync<JsonElement>();
        var saved = after.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "mistral");
        Assert.True(saved.GetProperty("configured").GetBoolean());
        Assert.Equal("••••8877", saved.GetProperty("key_masked").GetString());
        Assert.Equal("mistral-large-latest", saved.GetProperty("model").GetString());
        Assert.DoesNotContain("mk-secret-998877", after.GetRawText());

        // pin it by name; an unknown pin is refused
        (await su.PutAsJsonAsync("/api/settings/ai-providers", new { chat_provider_pref = "mistral" })).EnsureSuccessStatusCode();
        var refused = await su.PutAsJsonAsync("/api/settings/ai-providers", new { chat_provider_pref = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var test = await su.PostAsJsonAsync("/api/settings/ai-providers/test", new { provider = "mistral" });
        test.EnsureSuccessStatusCode();
        var verdict = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(verdict.GetProperty("ok").GetBoolean());
        Assert.Equal("mistral-large-latest", verdict.GetProperty("model").GetString());
        Assert.Equal("api.mistral.ai", _f.Chat.Last.Host);

        // an admin may not test keys (settings.ai_keys.manage is superadmin's)
        var admin = _f.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        var at = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", at);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsJsonAsync("/api/settings/ai-providers/test", new { provider = "mistral" })).StatusCode);

        // remove the key again so the shared host stays as it was
        (await su.PutAsJsonAsync("/api/settings/ai-providers",
            new Dictionary<string, string> { ["mistral_api_key"] = "", ["chat_provider_pref"] = "" })).EnsureSuccessStatusCode();
    }
}
