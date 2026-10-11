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
        /// <summary>Refuse a request like a real provider would (status + body), or null to answer.</summary>
        public Func<JsonElement, (HttpStatusCode Status, string Body)?>? Refuse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (Calls) Calls.Add((request.RequestUri!, request.Headers.Authorization?.ToString() ?? "", body));
            if (Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { Content = new StringContent("{\"error\":\"invalid x-api-key\"}") };
            if (Refuse?.Invoke(body) is { } no)
                return new HttpResponseMessage(no.Status) { Content = new StringContent(no.Body) };
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
        // Anthropic's current Claude models refuse temperature — it is never sent
        Assert.False(body.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData(400, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"`temperature` is deprecated for this model.\"}}", "temperature")]
    [InlineData(400, "{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.\"}}", "max_tokens")]
    [InlineData(400, "{\"error\":{\"message\":\"Unsupported value: 'temperature' does not support 0.5 with this model. Only the default (1) value is supported.\"}}", "temperature")]
    [InlineData(422, "{\"detail\":\"Extra inputs are not permitted: reasoning_effort\"}", "reasoning_effort")]
    [InlineData(400, "{\"error\":\"tool_choice is not supported\"}", "tool_choice")]
    [InlineData(400, "{\"error\":\"invalid model id\"}", null)]
    [InlineData(401, "{\"error\":\"temperature\"}", null)]
    public void A_refused_parameter_is_recognised(int status, string detail, string? expected) =>
        Assert.Equal(expected, OpenAiLlmClient.RefusedParam(status, detail,
            new[] { "model", "messages", "temperature", "max_tokens", "reasoning_effort", "tool_choice", "tools" }));

    [Fact]
    public async Task A_model_that_refuses_a_parameter_gets_it_again_without_and_it_is_remembered()
    {
        OpenAiLlmClient.ResetRefusedParams();
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        // a vendor whose new model refuses temperature AND wants max_completion_tokens
        sc.SetSetting("openrouter_api_key", "sk-or-1234567");
        sc.SetSetting("openrouter_chat_model", "vendor/strict-model");
        var rec = new Recorder
        {
            Refuse = b =>
                b.TryGetProperty("temperature", out _)
                    ? (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"`temperature` is deprecated for this model.\"}}")
                : b.TryGetProperty("max_tokens", out _)
                    ? (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens'. Use 'max_completion_tokens' instead.\"}}")
                : null,
        };
        var client = new OpenAiLlmClient(new Factory(rec), sc);
        var tools = new[] { new LlmTool("lookup", "find a thing", JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement) };

        var r = await client.ChatAsync("sys", new List<LlmMessage> { new("user", "hi") }, tools);
        Assert.Equal("OK", r.Text);
        Assert.Equal("openrouter", r.Provider);
        Assert.Equal(3, rec.Calls.Count);                      // refused twice, answered once
        var ok = rec.Calls[^1].Body;
        Assert.False(ok.TryGetProperty("temperature", out _));
        Assert.False(ok.TryGetProperty("max_tokens", out _));
        Assert.Equal(OpenAiLlmClient.OpenRouterMaxTokensDefault, ok.GetProperty("max_completion_tokens").GetInt32());
        Assert.True(ok.TryGetProperty("tools", out _));        // tools always survive

        // the refusals are remembered: the next call goes right the first time
        var again = await client.ChatAsync("sys", new List<LlmMessage> { new("user", "again") });
        Assert.Equal("OK", again.Text);
        Assert.Equal(4, rec.Calls.Count);
        OpenAiLlmClient.ResetRefusedParams();
    }

    [Fact]
    public async Task Every_provider_failing_is_recorded_and_told_to_admins_and_cleared_by_the_next_success()
    {
        OpenAiLlmClient.ResetRefusedParams();
        LlmHealth.Reset();
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        sc.SetSetting("deepseek_api_key", "sk-ds-1234567");
        var rec = new Recorder { Status = HttpStatusCode.Unauthorized };
        var client = new OpenAiLlmClient(new Factory(rec), sc);

        var r = await client.ChatAsync("sys", new List<LlmMessage> { new("user", "hi") });
        Assert.NotEqual("deepseek", r.Provider);               // the sandbox answered
        var fail = LlmHealth.Read(sc);
        Assert.NotNull(fail);
        Assert.Contains("deepseek", fail!.Error);
        Assert.Contains("401", fail.Error);
        Assert.DoesNotContain("sk-ds-1234567", fail.Error);

        rec.Status = HttpStatusCode.OK;
        var ok = await client.ChatAsync("sys", new List<LlmMessage> { new("user", "hi") });
        Assert.Equal("deepseek", ok.Provider);
        Assert.Null(LlmHealth.Read(sc));
        LlmHealth.Reset();
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
        Assert.True(page.TryGetProperty("last_error", out _));
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

        // voice-note transcription: its own switch, validated
        (await su.PutAsJsonAsync("/api/settings/ai-providers", new { transcription_provider = "whisper" })).EnsureSuccessStatusCode();
        var stt = await su.GetFromJsonAsync<JsonElement>("/api/settings/ai-providers");
        Assert.Equal("local", stt.GetProperty("transcription_provider").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await su.PutAsJsonAsync("/api/settings/ai-providers", new { transcription_provider = "bogus" })).StatusCode);
        (await su.PutAsJsonAsync("/api/settings/ai-providers", new { transcription_provider = "" })).EnsureSuccessStatusCode();

        // remove the key again so the shared host stays as it was
        (await su.PutAsJsonAsync("/api/settings/ai-providers",
            new Dictionary<string, string> { ["mistral_api_key"] = "", ["chat_provider_pref"] = "" })).EnsureSuccessStatusCode();
    }
}
