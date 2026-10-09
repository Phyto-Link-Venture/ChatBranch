using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The HTTP surface of compliance + routing: STOP through the sandbox inbound,
/// the force override on every manual send endpoint, the Chatbot tick the
/// dashboard reads, capability routing on the template send, the recovery
/// endpoints, the routing-rules keyword knobs, record sync and custom-role users.
/// </summary>
public class ComplianceRoutingApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ComplianceRoutingApiTests(ApiFactory f) => _f = f;

    private Task<HttpClient> Admin() => SettingsTestKit.Client(_f, "admin", "admin123");
    private Task<HttpClient> Super() => SettingsTestKit.Client(_f, "superadmin", "super123");

    private static async Task<JsonElement> Json(HttpResponseMessage res) =>
        await res.Content.ReadFromJsonAsync<JsonElement>();

    private ChannelAccount SeedAccount(string name, string provider, string? role = null,
        string health = Health.Healthy, Dictionary<string, object?>? config = null) =>
        SettingsTestKit.WithDb(_f, db =>
        {
            var cfg = config ?? new Dictionary<string, object?>();
            if (role != null) cfg["role"] = role;
            var a = new ChannelAccount
            {
                Channel = "whatsapp", Provider = provider, Name = name,
                ExternalId = provider == Providers.Cloud ? $"PN-{name}" : $"bl-{name}",
                // never a reachable host: the test host's Cloud client would try Meta
                AccessToken = "", HealthStatus = health, Config = cfg, CreatedAt = DateTime.UtcNow,
            };
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a;
        });

    // ---- STOP + force --------------------------------------------------------

    [Fact]
    public async Task Stop_via_simulate_opts_out_and_every_manual_send_needs_force()
    {
        var admin = await Admin();
        (await admin.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "whatsapp", key = "60177000101", text = "STOP", name = "Opt" })).EnsureSuccessStatusCode();
        var detail = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177000101");
        Assert.True(detail.GetProperty("opted_out").GetBoolean());

        var refused = await admin.PostAsJsonAsync("/api/whatsapp/threads/60177000101/send", new { text = "hi" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var forced = await admin.PostAsJsonAsync("/api/whatsapp/threads/60177000101/send", new { text = "hi", force = true });
        forced.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(
            "/api/whatsapp/threads/whatsapp/60177000101/send", new { text = "x" })).StatusCode);

        // a non-WhatsApp channel: the messaging send enforces it too
        (await admin.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "telegram", key = "880001", text = "unsubscribe", name = "Tg" })).EnsureSuccessStatusCode();
        var tg = await admin.PostAsJsonAsync("/api/messaging/threads/telegram/880001/send", new { text = "promo" });
        Assert.Equal(HttpStatusCode.Conflict, tg.StatusCode);
        var tgForced = await admin.PostAsJsonAsync("/api/messaging/threads/telegram/880001/send",
            new { text = "account notice", force = true });
        tgForced.EnsureSuccessStatusCode();
        var tgThread = await admin.GetFromJsonAsync<JsonElement>("/api/messaging/threads/telegram/880001");
        Assert.True(tgThread.GetProperty("opted_out").GetBoolean());

        // START opts back in
        (await admin.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "telegram", key = "880001", text = "start" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/messaging/threads/telegram/880001/send", new { text = "welcome back" }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Routing_rules_carry_the_extra_stop_and_start_keywords()
    {
        var admin = await Admin();
        var put = await admin.PutAsJsonAsync("/api/whatsapp/routing-rules",
            new { opt_out_keywords = new[] { "batal", "STOP" }, opt_in_keywords = "ya, sambung" });
        put.EnsureSuccessStatusCode();
        var rules = await Json(put);
        Assert.Equal(new[] { "BATAL" }, rules.GetProperty("opt_out_keywords").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("STOP", rules.GetProperty("opt_out_keywords_effective").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "YA", "SAMBUNG" }, rules.GetProperty("opt_in_keywords").EnumerateArray().Select(e => e.GetString()));

        var clash = await admin.PutAsJsonAsync("/api/whatsapp/routing-rules", new { opt_in_keywords = new[] { "batal" } });
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);

        (await admin.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "whatsapp", key = "60177000102", text = "Batal" })).EnsureSuccessStatusCode();
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177000102"))
            .GetProperty("opted_out").GetBoolean());

        // leave the org-wide words as they were for the rest of the suite
        (await admin.PutAsJsonAsync("/api/whatsapp/routing-rules",
            new { opt_out_keywords = "", opt_in_keywords = "" })).EnsureSuccessStatusCode();
    }

    // ---- the Chatbot tick the dashboard shows is the gate ---------------------

    [Fact]
    public async Task A_business_number_shows_and_behaves_ai_off()
    {
        var admin = await Admin();
        await admin.PostAsJsonAsync("/api/contacts", new { name = "Biz Contact", phone = "60177000201" });
        var biz = SeedAccount("ApiBiz", Providers.Cloud);
        SettingsTestKit.WithDb(_f, db =>
        {
            var t = new Domain.Thread
            {
                Channel = "whatsapp", Key = "60177000201", Address = "60177000201", Name = "Biz Contact",
                AccountId = biz.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                ContactId = db.Contacts.First(c => c.Phone == "60177000201").Id,
            };
            db.Threads.Add(t);
            db.SaveChanges();
        });
        (await admin.PostAsJsonAsync("/api/whatsapp/simulate?ai=1",
            new { channel = "whatsapp", key = "60177000201", text = "hello?" })).EnsureSuccessStatusCode();

        var detail = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177000201");
        Assert.False(detail.GetProperty("ai_enabled").GetBoolean());
        Assert.DoesNotContain(detail.GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("source").GetString() == "ai");
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads");
        var row = list.GetProperty("threads").EnumerateArray().First(t => t.GetProperty("wa_id").GetString() == "60177000201");
        Assert.False(row.GetProperty("ai_enabled").GetBoolean());
        // …and the business redirect was queued instead
        var queue = await admin.GetFromJsonAsync<JsonElement>("/api/flows/queue");
        Assert.Contains(queue.GetProperty("jobs").EnumerateArray(),
            j => j.GetProperty("toAddress").GetString() == "60177000201" && j.GetProperty("kind").GetString() == "text");

        // ticking it ON is what the next inbound obeys
        (await admin.PostAsJsonAsync("/api/whatsapp/threads/60177000201/ai", new { enabled = true })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/whatsapp/simulate?ai=1",
            new { channel = "whatsapp", key = "60177000201", text = "still there?" })).EnsureSuccessStatusCode();
        detail = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177000201");
        Assert.True(detail.GetProperty("ai_enabled").GetBoolean());
        Assert.Contains(detail.GetProperty("messages").EnumerateArray(), m => m.GetProperty("source").GetString() == "ai");
    }

    // ---- capability routing on the manual template send -----------------------

    [Fact]
    public async Task A_template_with_every_cloud_number_down_is_refused_and_the_thread_untouched()
    {
        var admin = await Admin();
        var personal = SeedAccount("ApiPersonal", Providers.Baileys);
        SeedAccount("ApiDeadBiz", Providers.Cloud, health: Health.Banned,
            config: new Dictionary<string, object?>());
        SettingsTestKit.WithDb(_f, db =>
        {
            // a CONFIGURED Cloud row (token + id) that is banned
            var dead = db.ChannelAccounts.First(a => a.Name == "ApiDeadBiz");
            dead.AccessToken = "tok";
            db.Threads.Add(new Domain.Thread
            {
                Channel = "whatsapp", Key = "60177000301", Address = "60177000301", Name = "T",
                AccountId = personal.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        });

        var res = await admin.PostAsJsonAsync("/api/whatsapp/threads/60177000301/send-template",
            new { name = "order_confirmation", variables = new[] { "A", "B", "C" } });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("never fall back to a Baileys number", (await Json(res)).GetProperty("detail").GetString());
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/threads/60177000301");
        Assert.Equal(personal.Id.ToString(), after.GetProperty("account_id").GetString());
        Assert.Empty(after.GetProperty("messages").EnumerateArray());

        // unconfigure it again so the rest of the suite sees no Cloud number
        SettingsTestKit.WithDb(_f, db =>
        {
            db.ChannelAccounts.First(a => a.Name == "ApiDeadBiz").AccessToken = "";
            db.SaveChanges();
        });
    }

    // ---- recovery -------------------------------------------------------------

    [Fact]
    public async Task Recovery_lists_down_conversation_numbers_and_queues_templates_one_click()
    {
        var admin = await Admin();
        var phone = SeedAccount("ApiRecoverPhone", Providers.Baileys, health: Health.Banned);
        var contactId = SettingsTestKit.WithDb(_f, db =>
        {
            var c = new Contact { Name = "Rec", Phone = "+60 17-700 0401", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            db.AccountAssignments.Add(new AccountAssignment
            {
                ContactId = c.Id, Channel = "whatsapp", AccountId = phone.Id,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
            return c.Id;
        });

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/whatsapp/recovery");
        var row = list.GetProperty("accounts").EnumerateArray().First(a => a.GetProperty("name").GetString() == "ApiRecoverPhone");
        Assert.Equal(1, row.GetProperty("affected").GetInt32());
        Assert.False(row.GetProperty("can_recover").GetBoolean());       // no template, no Cloud carrier

        var noTemplate = await admin.PostAsync($"/api/whatsapp/recovery/{phone.Id}", null);
        Assert.Equal(HttpStatusCode.Conflict, noTemplate.StatusCode);

        (await admin.PutAsJsonAsync("/api/whatsapp/routing-rules", new { recovery_template = "we_moved" }))
            .EnsureSuccessStatusCode();
        var carrier = SeedAccount("ApiCarrier", Providers.Cloud);
        SettingsTestKit.WithDb(_f, db =>
        {
            db.ChannelAccounts.First(a => a.Id == carrier.Id).AccessToken = "tok";
            db.SaveChanges();
        });
        var ok = await admin.PostAsync($"/api/whatsapp/recovery/{phone.Id}", null);
        ok.EnsureSuccessStatusCode();
        Assert.Equal(1, (await Json(ok)).GetProperty("queued").GetInt32());
        var job = SettingsTestKit.WithDb(_f, db => db.OutboundJobs.ToList()
            .Last(j => j.ContactId == contactId && j.Kind == JobKind.Template));
        Assert.Equal("60177000401", job.ToAddress);
        Assert.Null(job.AccountId);

        // within 24h the same contact is not queued twice
        Assert.Equal(0, (await Json(await admin.PostAsync($"/api/whatsapp/recovery/{phone.Id}", null)))
            .GetProperty("queued").GetInt32());

        SettingsTestKit.WithDb(_f, db =>
        {
            db.OutboundJobs.RemoveRange(db.OutboundJobs.Where(j => j.ContactId == contactId));
            db.ChannelAccounts.First(a => a.Id == carrier.Id).AccessToken = "";
            db.SaveChanges();
        });
        (await admin.PutAsJsonAsync("/api/whatsapp/routing-rules", new { recovery_template = "" })).EnsureSuccessStatusCode();
    }
}
