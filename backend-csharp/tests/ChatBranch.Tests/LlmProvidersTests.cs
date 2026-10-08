using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The chat brain's provider resolution: ILMU and Gemini through their
/// OpenAI-compatible endpoints, the pin, the automatic order and its error
/// fallback, and the chat_model key — all against a fake HTTP handler.
/// </summary>
public class LlmProvidersTests
{
    /// <summary>Answers chat-completions per host; records every request (URL, auth, body).</summary>
    private sealed class FakeLlmHttp : HttpMessageHandler
    {
        public List<(string Url, string Auth, JsonElement Body)> Requests { get; } = new();
        public HashSet<string> FailingHosts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var url = request.RequestUri!.ToString();
            Requests.Add((url, request.Headers.Authorization?.ToString() ?? "", body));
            if (FailingHosts.Contains(request.RequestUri.Host))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
            var model = body.GetProperty("model").GetString();
            var json = JsonSerializer.Serialize(new
            {
                model,
                choices = new[] { new { message = new { role = "assistant",
                    content = $"<think>internal</think>reply from {request.RequestUri.Host}" } } },
                usage = new { prompt_tokens = 11, completion_tokens = 7 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (OpenAiLlmClient Client, FakeLlmHttp Http, ScopingService Sc, TestDb Db) Rig()
    {
        var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var http = new FakeLlmHttp();
        return (new OpenAiLlmClient(new Factory(http), sc), http, sc, t);
    }

    private static Task<LlmChatResult> Ask(OpenAiLlmClient c) =>
        c.ChatAsync("system", new List<LlmMessage> { new("user", "hello") });

    [Fact]
    public async Task A_pinned_ilmu_goes_to_ilmu_with_its_model_and_thinking_off()
    {
        var (client, http, sc, db) = Rig();
        using var dbScope = db;
        sc.SetSetting("openrouter_api_key", "or-key");
        sc.SetSetting("ilmu_api_key", "ilmu-key");
        sc.SetSetting("chat_provider_pref", "ilmu");

        var r = await Ask(client);

        var (url, auth, body) = Assert.Single(http.Requests);
        Assert.Equal("https://api.ilmu.ai/v1/chat/completions", url);
        Assert.Equal("Bearer ilmu-key", auth);
        Assert.Equal("ilmu-v3.1", body.GetProperty("model").GetString());
        Assert.Equal("low", body.GetProperty("reasoning_effort").GetString());
        Assert.Equal("ilmu", r.Provider);
        Assert.Equal("reply from api.ilmu.ai", r.Text);         // a think-block never reaches a reply
        Assert.Equal(11, r.PromptTokens);
    }

    [Fact]
    public async Task A_pinned_google_uses_geminis_openai_endpoint_and_the_chat_model()
    {
        var (client, http, sc, db) = Rig();
        using var dbScope = db;
        sc.SetSetting("openai_api_key", "sk-openai");
        sc.SetSetting("google_api_key", "g-key");
        sc.SetSetting("chat_provider", "google");          // the mirror key alone still pins
        sc.SetSetting("chat_model", "gemini-2.0-flash");

        var r = await Ask(client);

        var (url, auth, body) = Assert.Single(http.Requests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", url);
        Assert.Equal("Bearer g-key", auth);
        Assert.Equal("gemini-2.0-flash", body.GetProperty("model").GetString());
        Assert.Equal("google", r.Provider);
    }

    [Fact]
    public async Task A_failing_pinned_provider_degrades_to_sandbox_without_leaking_to_another_vendor()
    {
        var (client, http, sc, db) = Rig();
        using var dbScope = db;
        sc.SetSetting("openai_api_key", "sk-openai");
        sc.SetSetting("ilmu_api_key", "ilmu-key");
        sc.SetSetting("chat_provider_pref", "ilmu");
        http.FailingHosts.Add("api.ilmu.ai");

        var r = await Ask(client);

        Assert.Single(http.Requests);                      // ILMU only
        Assert.Equal("sandbox", r.Provider);
    }

    [Fact]
    public async Task Automatic_mode_follows_chat_provider_order_and_falls_through_on_errors()
    {
        var (client, http, sc, db) = Rig();
        using var dbScope = db;
        sc.SetSetting("openrouter_api_key", "or-key");
        sc.SetSetting("openai_api_key", "sk-openai");
        sc.SetSetting("google_api_key", "g-key");
        sc.SetSetting("chat_provider_order", "[\"google\",\"openai\",\"openrouter\"]");
        sc.SetSetting("chat_model", "gemini-2.5-pro");
        http.FailingHosts.Add("generativelanguage.googleapis.com");

        var r = await Ask(client);

        Assert.Equal(2, http.Requests.Count);
        Assert.StartsWith("https://generativelanguage.googleapis.com/", http.Requests[0].Url);
        Assert.Equal("gemini-2.5-pro", http.Requests[0].Body.GetProperty("model").GetString());
        Assert.Equal("https://api.openai.com/v1/chat/completions", http.Requests[1].Url);
        // the fallback uses its OWN default model — a Gemini model id means nothing to OpenAI
        Assert.Equal("gpt-4o-mini", http.Requests[1].Body.GetProperty("model").GetString());
        Assert.False(http.Requests[1].Body.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("openai", r.Provider);
    }

    [Fact]
    public void A_comma_list_order_and_an_unusable_pin_resolve_sensibly()
    {
        var (client, _, sc, db) = Rig();
        using var __ = db;
        sc.SetSetting("openai_api_key", "sk-openai");
        sc.SetSetting("ilmu_api_key", "ilmu-key");
        sc.SetSetting("chat_provider_order", "ilmu, openai");
        sc.SetSetting("chat_provider_pref", "google");     // pinned, but no Google key → automatic

        var names = client.Candidates().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "ilmu", "openai" }, names);
        Assert.Equal(new[] { "ilmu", "openai", "google" }, OpenAiLlmClient.ParseOrder("ilmu, openai,bogus google"));
    }

    [Fact]
    public void The_settings_page_env_name_for_the_chat_model_is_honoured()
    {
        var (client, _, sc, db) = Rig();
        using var __ = db;
        sc.SetSetting("openai_api_key", "sk-openai");
        var before = Environment.GetEnvironmentVariable("WHATSAPP_CHAT_MODEL");
        try
        {
            Environment.SetEnvironmentVariable("WHATSAPP_CHAT_MODEL", "gpt-4.1-mini");
            Assert.Equal("gpt-4.1-mini", client.Candidates()[0].Model);
            sc.SetSetting("chat_model", "gpt-4o");           // a dashboard row beats the env
            Assert.Equal("gpt-4o", client.Candidates()[0].Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHATSAPP_CHAT_MODEL", before);
        }
    }

    [Fact]
    public async Task Nothing_configured_is_the_sandbox()
    {
        var (client, http, _, db) = Rig();
        using var __ = db;
        Assert.False(client.IsConfigured);
        var r = await Ask(client);
        Assert.Equal("sandbox", r.Provider);
        Assert.Empty(http.Requests);
    }
}
