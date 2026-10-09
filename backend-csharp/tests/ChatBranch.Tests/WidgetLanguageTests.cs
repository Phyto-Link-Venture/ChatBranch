using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The website widget speaks the visitor's language: an optional `language`
/// (auto | en | ms | zh) on the widget, validated and round-tripped by the admin
/// API and handed to the public /config, and a /widget.js that carries an
/// English, Malay and Chinese dictionary of its own words.
/// </summary>
public class WidgetLanguageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public WidgetLanguageTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Admin()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin123" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<JsonElement> PublicConfig(string token) =>
        await _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/widget/{token}/config");

    [Fact]
    public async Task Widget_language_defaults_to_auto_is_validated_and_reaches_the_public_config()
    {
        var admin = await Admin();

        // no language given → auto; the operator's blank texts are not filled in
        // server-side (the script supplies them in the visitor's language)
        var created = await admin.PostAsJsonAsync("/api/widgets", new { name = "Lang widget" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var widget = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("auto", widget.GetProperty("language").GetString());
        var id = widget.GetProperty("id").GetInt32();
        var token = widget.GetProperty("token").GetString()!;

        var cfg = await PublicConfig(token);
        Assert.Equal("auto", cfg.GetProperty("language").GetString());
        Assert.Equal("", cfg.GetProperty("greeting").GetString());
        Assert.Equal("", cfg.GetProperty("placeholder").GetString());
        Assert.Equal("", cfg.GetProperty("title").GetString());

        // a fixed language (any case) is stored normalized and served
        var ms = await admin.PatchAsJsonAsync($"/api/widgets/{id}", new { language = "MS", greeting = "Selamat datang!" });
        ms.EnsureSuccessStatusCode();
        Assert.Equal("ms", (await ms.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("language").GetString());
        cfg = await PublicConfig(token);
        Assert.Equal("ms", cfg.GetProperty("language").GetString());
        Assert.Equal("Selamat datang!", cfg.GetProperty("greeting").GetString());

        // an unknown language is refused on update and on create, and changes nothing
        var bad = await admin.PatchAsJsonAsync($"/api/widgets/{id}", new { language = "klingon" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("language", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal("ms", (await PublicConfig(token)).GetProperty("language").GetString());
        var badCreate = await admin.PostAsJsonAsync("/api/widgets", new { name = "Bad lang", language = "fr" });
        Assert.Equal(HttpStatusCode.BadRequest, badCreate.StatusCode);

        // zh on create; "" puts a widget back on auto
        var zh = await admin.PostAsJsonAsync("/api/widgets", new { name = "Chinese widget", language = "zh" });
        Assert.Equal(HttpStatusCode.Created, zh.StatusCode);
        Assert.Equal("zh", (await zh.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("language").GetString());
        var reset = await admin.PatchAsJsonAsync($"/api/widgets/{id}", new { language = "" });
        reset.EnsureSuccessStatusCode();
        Assert.Equal("auto", (await PublicConfig(token)).GetProperty("language").GetString());

        // the list carries it too
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/widgets");
        Assert.All(list.GetProperty("widgets").EnumerateArray(),
            w => Assert.Contains(w.GetProperty("language").GetString(), new[] { "auto", "en", "ms", "zh" }));
    }

    [Fact]
    public async Task Widget_script_is_utf8_and_carries_english_malay_and_chinese_dictionaries()
    {
        var res = await _factory.CreateClient().GetAsync("/widget.js");
        res.EnsureSuccessStatusCode();
        Assert.Equal("utf-8", res.Content.Headers.ContentType?.CharSet?.ToLowerInvariant());
        var js = await res.Content.ReadAsStringAsync();

        Assert.Contains("var I18N", js);
        foreach (var lang in new[] { "en: {", "ms: {", "zh: {" }) Assert.Contains(lang, js);
        // one word from each dictionary, read back intact
        Assert.Contains("'Send'", js);
        Assert.Contains("'Hantar'", js);
        Assert.Contains("'发送'", js);
        Assert.Contains("'Sembang dengan kami'", js);
        Assert.Contains("'与我们聊天'", js);
        // the choice: configured language → the visitor's browser → English
        Assert.Contains("pickLang(cfg.language)", js);
        Assert.Contains("navigator.languages", js);
        Assert.Contains("return 'en'", js);
    }
}
