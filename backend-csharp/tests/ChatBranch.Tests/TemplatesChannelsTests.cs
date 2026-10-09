using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The unified Templates module (message templates limited to channels, script
/// templates alongside) and the Channels "who answers it" inbox settings.
/// </summary>
public class TemplatesChannelsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public TemplatesChannelsTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user = "admin", string pass = "admin123")
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static List<string> Codes(JsonElement list) =>
        list.GetProperty("canned").EnumerateArray()
            .Select(x => x.GetProperty("short_code").GetString()!).ToList();

    private static List<string> ChannelsOf(JsonElement row) =>
        row.GetProperty("channels").EnumerateArray().Select(x => x.GetString()!).ToList();

    // ---- message templates: channel limits -------------------------------------

    [Fact]
    public async Task Canned_channel_filter_returns_shared_and_matching_templates()
    {
        var c = await Authed();
        (await c.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "cf-all", content = "for everyone" })).EnsureSuccessStatusCode();
        var wa = await c.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "cf-wa", content = "whatsapp only", channels = new[] { "whatsapp" } });
        wa.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "whatsapp" }, ChannelsOf(await wa.Content.ReadFromJsonAsync<JsonElement>()));
        // "messenger" is an alias — stored as the thread channel id "facebook"
        var social = await c.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "cf-social", content = "dm", channels = new[] { "messenger", "instagram" } });
        social.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "facebook", "instagram" },
            ChannelsOf(await social.Content.ReadFromJsonAsync<JsonElement>()));

        var forWa = Codes(await c.GetFromJsonAsync<JsonElement>("/api/messaging/canned?channel=whatsapp"));
        Assert.Contains("cf-all", forWa);
        Assert.Contains("cf-wa", forWa);
        Assert.DoesNotContain("cf-social", forWa);

        var forFb = Codes(await c.GetFromJsonAsync<JsonElement>("/api/messaging/canned?channel=facebook"));
        Assert.Contains("cf-social", forFb);
        Assert.Contains("cf-all", forFb);
        Assert.DoesNotContain("cf-wa", forFb);

        // an unknown channel still gets the shared replies, nothing limited elsewhere
        var forSms = Codes(await c.GetFromJsonAsync<JsonElement>("/api/messaging/canned?channel=sms"));
        Assert.Contains("cf-all", forSms);
        Assert.DoesNotContain("cf-wa", forSms);

        // no filter = the whole library (the Templates page)
        var all = Codes(await c.GetFromJsonAsync<JsonElement>("/api/messaging/canned"));
        Assert.Contains("cf-wa", all);
        Assert.Contains("cf-social", all);
    }

    [Fact]
    public async Task Canned_channels_are_validated_and_patch_is_partial()
    {
        var c = await Authed();
        var bad = await c.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "cv-bad", content = "x", channels = new[] { "pigeon" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var missing = await c.PostAsJsonAsync("/api/messaging/canned", new { short_code = "cv-empty" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        // ticking every channel collapses to [] (= all, incl. channels added later)
        var every = await c.PostAsJsonAsync("/api/messaging/canned", new
        {
            short_code = "/cv-every", content = "x",
            channels = new[] { "whatsapp", "telegram", "email", "facebook", "instagram", "website",
                "line", "viber", "sms", "wechat", "tiktok", "douyin", "zalo" },
        });
        every.EnsureSuccessStatusCode();
        var row = await every.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(ChannelsOf(row));
        Assert.Equal("cv-every", row.GetProperty("short_code").GetString()); // leading "/" dropped
        var id = row.GetProperty("id").GetInt32();

        var limit = await c.PatchAsJsonAsync($"/api/messaging/canned/{id}", new { channels = new[] { "email" } });
        limit.EnsureSuccessStatusCode();
        // content-only edit keeps the channel limit
        var edit = await c.PatchAsJsonAsync($"/api/messaging/canned/{id}", new { content = "changed" });
        edit.EnsureSuccessStatusCode();
        var edited = await edit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "email" }, ChannelsOf(edited));
        Assert.Equal("changed", edited.GetProperty("content").GetString());

        var badPatch = await c.PatchAsJsonAsync($"/api/messaging/canned/{id}", new { channels = new[] { "fax" } });
        Assert.Equal(HttpStatusCode.BadRequest, badPatch.StatusCode);

        // "all" clears the limit again
        var reset = await c.PatchAsJsonAsync($"/api/messaging/canned/{id}", new { channels = new[] { "all" } });
        Assert.Empty(ChannelsOf(await reset.Content.ReadFromJsonAsync<JsonElement>()));
    }

    [Fact]
    public async Task Canned_writes_follow_the_templates_manage_capability()
    {
        var agent = await Authed("agent", "agent123");
        var res = await agent.PostAsJsonAsync("/api/messaging/canned",
            new { short_code = "agent-try", content = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        // reading stays open to every signed-in role (the composer needs it)
        var list = await agent.GetAsync("/api/messaging/canned?channel=whatsapp");
        list.EnsureSuccessStatusCode();
    }

    [Fact]
    public void Template_channel_parser_normalizes_aliases()
    {
        var (list, err) = TemplateChannels.Parse(new[] { "IG", "web", "whatsapp", "whatsapp" });
        Assert.Null(err);
        Assert.Equal(new[] { "whatsapp", "instagram", "website" }, list);
        Assert.True(TemplateChannels.Offers(new List<string>(), "telegram"));
        Assert.True(TemplateChannels.Offers(new List<string> { "facebook" }, "messenger"));
        Assert.False(TemplateChannels.Offers(new List<string> { "facebook" }, "email"));
    }

    // ---- script templates are unaffected ---------------------------------------

    [Fact]
    public async Task Script_templates_keep_their_contract_and_gain_entity_binding()
    {
        var admin = await Authed();
        var create = await admin.PostAsJsonAsync("/api/calls/script-templates",
            new { name = "Unified script", content = "Hello {{name}}." });
        create.EnsureSuccessStatusCode();
        var row = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(row.TryGetProperty("channels", out _)); // scripts are voice-only
        Assert.Equal(JsonValueKind.Null, row.GetProperty("entity").ValueKind);
        var id = row.GetProperty("id").GetInt32();

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/calls/script-templates");
        Assert.Contains(list.GetProperty("templates").EnumerateArray(),
            s => s.GetProperty("name").GetString() == "Unified script");

        var patched = await admin.PatchAsJsonAsync($"/api/calls/script-templates/{id}",
            new { content = "Hi {{name}}." });
        patched.EnsureSuccessStatusCode();
        Assert.Equal("Unified script",
            (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());

        var agent = await Authed("agent", "agent123");
        var denied = await agent.PostAsJsonAsync("/api/calls/script-templates",
            new { name = "Agent script", content = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // ---- channels: who answers an inbox ----------------------------------------

    [Fact]
    public async Task Inbox_team_and_ai_default_round_trip_and_route_new_conversations()
    {
        var admin = await Authed();
        // a team with round-robin OFF — never picked by the entity fallback, so
        // the conversation can only reach it through the inbox binding
        var teamRes = await admin.PostAsJsonAsync("/api/teams", new { name = "Inbox owners", auto_assign = false });
        teamRes.EnsureSuccessStatusCode();
        var teamId = (await teamRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var bad = await admin.PostAsJsonAsync("/api/widgets", new { name = "Bad", team_id = 999999 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await admin.PostAsJsonAsync("/api/widgets",
            new { name = "Inbox widget", team_id = teamId, ai_default = "off" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var widget = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(teamId, widget.GetProperty("team_id").GetInt32());
        Assert.Equal("off", widget.GetProperty("ai_default").GetString());
        var token = widget.GetProperty("token").GetString();

        // a visitor starts a conversation on it
        var anon = _factory.CreateClient();
        var session = await (await anon.PostAsJsonAsync($"/api/widget/{token}/session", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var key = session.GetProperty("session").GetString();
        (await anon.PostAsJsonAsync($"/api/widget/{token}/messages", new { session = key, text = "hello" }))
            .EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var thread = await db.Threads.AsNoTracking().FirstAsync(t => t.Channel == "website" && t.Key == key);
            Assert.Equal(teamId, thread.TeamId);
            Assert.False(thread.AiEnabled);
        }

        // per-inbox AI review mode: validated, returned, clearable
        var wid = widget.GetProperty("id").GetInt32();
        Assert.Equal("", widget.GetProperty("ai_review_mode").GetString());
        var badMode = await admin.PatchAsJsonAsync($"/api/widgets/{wid}", new { ai_review_mode = "sometimes" });
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
        var held = await admin.PatchAsJsonAsync($"/api/widgets/{wid}", new { ai_review_mode = "review_all" });
        held.EnsureSuccessStatusCode();
        Assert.Equal("review_all", (await held.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ai_review_mode").GetString());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var acct = await db.ChannelAccounts.AsNoTracking().FirstAsync(a => a.Id == wid);
            Assert.Equal("review_all", acct.Config["ai_review_mode"]?.ToString());
        }
        (await admin.PatchAsJsonAsync($"/api/widgets/{wid}", new { ai_review_mode = "" })).EnsureSuccessStatusCode();

        // clearing both keys returns the inbox to inherit
        var id = widget.GetProperty("id").GetInt32();
        var cleared = await admin.PatchAsJsonAsync($"/api/widgets/{id}", new { team_id = "", ai_default = "" });
        cleared.EnsureSuccessStatusCode();
        var after = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, after.GetProperty("team_id").ValueKind);
        Assert.Equal("", after.GetProperty("ai_default").GetString());

        // deleting the inbox keeps its conversation (unlinked, not a FK failure)
        var del = await admin.DeleteAsync($"/api/widgets/{id}");
        del.EnsureSuccessStatusCode();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var thread = await db.Threads.AsNoTracking().FirstAsync(t => t.Channel == "website" && t.Key == key);
            Assert.Null(thread.AccountId);
        }
    }

    [Fact]
    public async Task Messaging_accounts_accept_inbox_settings_and_social_edits()
    {
        var admin = await Authed();
        var bad = await admin.PostAsJsonAsync("/api/messaging/accounts",
            new { channel = "telegram", name = "Bot", access_token = "123:abc", ai_default = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var res = await admin.PostAsJsonAsync("/api/messaging/accounts",
            new { channel = "telegram", name = "Support bot", access_token = "123:abc", ai_default = "on" });
        res.EnsureSuccessStatusCode();
        var acct = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("on", acct.GetProperty("ai_default").GetString());
        var id = acct.GetProperty("id").GetString();

        // social accounts can now be renamed / re-tokened in place
        var renamed = await admin.PatchAsJsonAsync($"/api/messaging/accounts/{id}",
            new { name = "Help bot", access_token = acct.GetProperty("access_token_masked").GetString() });
        renamed.EnsureSuccessStatusCode();
        var row = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Help bot", row.GetProperty("name").GetString());
        Assert.True(row.GetProperty("configured").GetBoolean()); // masked token kept the stored one
    }
}
