using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Shared plumbing for the Cloud-webhook routing suites: signing, payload shapes
/// (with or without <c>entry[].id</c>) and a registered Cloud number per company.
/// </summary>
internal static class CloudWebhookKit
{
    /// <summary>The Meta app secret every registered number below carries.</summary>
    public const string AppSecret = "pnid-routing-app-secret";

    public static StringContent Signed(string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(AppSecret));
        content.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
        return content;
    }

    /// <summary>One inbound text message. <paramref name="waba"/> null = no <c>entry[].id</c> at all.</summary>
    public static string Payload(string pnid, string from, string wamid, string text, string? waba = null)
    {
        object value = new
        {
            metadata = new { phone_number_id = pnid },
            contacts = new[] { new { profile = new { name = "Walk-in" }, wa_id = from } },
            messages = new[]
            {
                new { from, id = wamid, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                      type = "text", text = new { body = text } },
            },
        };
        var changes = new[] { new { value } };
        object entry = waba == null ? new { changes } : new { id = waba, changes };
        return JsonSerializer.Serialize(new { entry = new[] { entry } });
    }

    public static async Task<HttpClient> Authed(ApiFactory f, string user, string pass)
    {
        var c = f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static T WithDb<T>(ApiFactory f, Func<AppDbContext, T> work)
    {
        using var scope = f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>
    /// A company (top-level entity) carrying one registered Cloud number —
    /// get-or-create by number, since <c>uniq_channelaccount_wa_pnid</c> now allows
    /// exactly one account per number and every test in a class shares the host.
    /// </summary>
    public static (int Company, int Account) Company(ApiFactory f, string name, string pnid, string waba = "") =>
        WithDb(f, db =>
        {
            if (db.ChannelAccounts.AsNoTracking()
                    .FirstOrDefault(a => a.Channel == ChatBranch.Domain.Channels.WhatsApp && a.ExternalId == pnid) is { } existing)
                return (existing.EntityId ?? 0, existing.Id);
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            var entity = new Entity
            {
                TenantId = tenant.Id, Name = name,
                Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}"[..24],
                CreatedAt = now, UpdatedAt = now,
            };
            db.Entities.Add(entity);
            db.SaveChanges();
            var account = new ChannelAccount
            {
                Channel = ChatBranch.Domain.Channels.WhatsApp, Provider = ChatBranch.Domain.Providers.Cloud, Name = $"{name} number",
                ExternalId = pnid, WabaId = waba, AccessToken = $"tok-{pnid}", AppSecret = AppSecret,
                EntityId = entity.Id, CreatedAt = now,
            };
            db.ChannelAccounts.Add(account);
            db.SaveChanges();
            return (entity.Id, account.Id);
        });

    public static List<Thread> Threads(ApiFactory f, string key) =>
        WithDb(f, db => db.Threads.AsNoTracking().Where(t => t.Channel == ChatBranch.Domain.Channels.WhatsApp && t.Key == key).ToList());

    public static List<Message> Messages(ApiFactory f, int threadId) =>
        WithDb(f, db => db.Messages.AsNoTracking().Where(m => m.ThreadId == threadId).OrderBy(m => m.Id).ToList());

    public static List<AuditLog> UnknownNumberAudits(ApiFactory f, string pnid) =>
        WithDb(f, db => db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == CloudInboxRouting.UnknownNumberAudit && a.Target.Contains(pnid))
            .ToList());
}

/// <summary>
/// An inbound WhatsApp Cloud payload is matched to ONE registered number. Once the
/// install has a Cloud number, a payload whose <c>phone_number_id</c> matches none
/// is recorded and dropped — it used to be filed with no company, in the
/// organization-wide unattributed bucket every entity can see, which the next
/// company to touch that conversation then ADOPTED (a cross-tenant leak on an
/// install hosting unrelated businesses). The happy path is unchanged.
/// </summary>
public class WhatsAppNumberRoutingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public WhatsAppNumberRoutingTests(ApiFactory f) => _f = f;

    private static int _n;
    private static string Sender() => "60155" + (1000000 + Interlocked.Increment(ref _n));

    /// <summary>Company A owns PN-A, company B owns PN-B; PN-GHOST belongs to nobody.</summary>
    private (int A, int B) Companies()
    {
        var a = CloudWebhookKit.Company(_f, "Routing A", "PN-ROUTE-A");
        var b = CloudWebhookKit.Company(_f, "Routing B", "PN-ROUTE-B");
        return (a.Company, b.Company);
    }

    private async Task<HttpResponseMessage> Post(string json) =>
        await _f.CreateClient().PostAsync("/api/whatsapp/webhook", CloudWebhookKit.Signed(json));

    [Fact]
    public async Task Unknown_phone_number_id_opens_nothing_records_an_audit_row_and_still_answers_200()
    {
        Companies();
        var from = Sender();
        var res = await Post(CloudWebhookKit.Payload("PN-GHOST-1", from, "wamid.GHOST1", "my order is late"));
        res.EnsureSuccessStatusCode();   // 200: Meta must stop retrying

        Assert.Empty(CloudWebhookKit.Threads(_f, from));                       // no conversation …
        Assert.Empty(CloudWebhookKit.WithDb(_f,                                // … and no message anywhere
            db => db.Messages.AsNoTracking().Where(m => m.ExternalId == "wamid.GHOST1").ToList()));

        var audit = Assert.Single(CloudWebhookKit.UnknownNumberAudits(_f, "PN-GHOST-1"));
        Assert.Contains("PN-GHOST-1", audit.Target);
        Assert.Contains("1 message(s)", audit.Detail);
        Assert.Contains("wamid.GHOST1", audit.Detail);
        // the row is organization-wide, so it must not carry what the customer wrote
        Assert.DoesNotContain("my order is late", audit.Detail);
        Assert.DoesNotContain(from, audit.Detail);
    }

    /// <summary>
    /// THE regression guard. Against the old code the ghost payload created an
    /// unattributed thread which company B then adopted, so B's agent and B's AI
    /// read another company's customer. Now B gets its own, clean conversation
    /// holding only B's message. (The old behaviour is pinned, deliberately, in
    /// <see cref="WhatsAppUnknownNumberCompatTests"/>.)
    /// </summary>
    [Fact]
    public async Task An_unknown_numbers_conversation_is_never_adopted_by_another_company()
    {
        var (_, b) = Companies();
        var from = Sender();   // not a contact of anyone: nothing else can decide attribution

        (await Post(CloudWebhookKit.Payload("PN-GHOST-2", from, "wamid.GHOST2", "ghost text"))).EnsureSuccessStatusCode();
        Assert.Empty(CloudWebhookKit.Threads(_f, from));

        (await Post(CloudWebhookKit.Payload("PN-ROUTE-B", from, "wamid.B1", "hello B"))).EnsureSuccessStatusCode();

        var thread = Assert.Single(CloudWebhookKit.Threads(_f, from));
        Assert.Equal(b, thread.CompanyEntityId);                                // B's own conversation
        var texts = CloudWebhookKit.Messages(_f, thread.Id).Select(m => m.Text).ToList();
        Assert.Contains("hello B", texts);
        Assert.DoesNotContain(texts, t => t.Contains("ghost text"));            // nothing inherited
        Assert.Single(texts.Where(t => t.Contains("hello B")));
    }

    [Fact]
    public async Task A_registered_number_still_files_under_its_company_and_dedupes_on_wamid()
    {
        var (a, _) = Companies();
        var from = Sender();
        var payload = CloudWebhookKit.Payload("PN-ROUTE-A", from, "wamid.A-DUP", "are you open?");
        (await Post(payload)).EnsureSuccessStatusCode();
        (await Post(payload)).EnsureSuccessStatusCode();   // Meta's redelivery

        var thread = Assert.Single(CloudWebhookKit.Threads(_f, from));
        Assert.Equal(a, thread.CompanyEntityId);
        Assert.Equal(CloudWebhookKit.WithDb(_f, db => db.ChannelAccounts.Single(x => x.ExternalId == "PN-ROUTE-A").Id),
            thread.AccountId);
        var inbound = CloudWebhookKit.Messages(_f, thread.Id).Where(m => m.Direction == MessageDirection.In).ToList();
        Assert.Single(inbound.Where(m => m.ExternalId == "wamid.A-DUP"));       // the wamid dedupe still holds
        Assert.Empty(CloudWebhookKit.UnknownNumberAudits(_f, "PN-ROUTE-A"));
    }

    /// <summary>
    /// The WABA (<c>entry[].id</c>) is a cross-check, never the only guard: a
    /// payload that CONTRADICTS the number's stored WABA is refused, while a
    /// payload carrying no entry id at all (Meta does not formally specify it for
    /// <c>messages</c>) routes exactly as before.
    /// </summary>
    [Fact]
    public async Task A_contradicting_waba_is_refused_but_a_missing_entry_id_routes_normally()
    {
        Companies();
        var waba = CloudWebhookKit.Company(_f, "Routing W", "PN-ROUTE-W", waba: "WABA-RIGHT");

        var impostor = Sender();
        (await Post(CloudWebhookKit.Payload("PN-ROUTE-W", impostor, "wamid.W-WRONG", "hi", waba: "WABA-WRONG")))
            .EnsureSuccessStatusCode();
        Assert.Empty(CloudWebhookKit.Threads(_f, impostor));
        var audit = Assert.Single(CloudWebhookKit.UnknownNumberAudits(_f, "PN-ROUTE-W"));
        Assert.Contains("WABA-WRONG", audit.Detail);

        var silent = Sender();   // no entry[].id — must NOT be read as a mismatch
        (await Post(CloudWebhookKit.Payload("PN-ROUTE-W", silent, "wamid.W-NONE", "no entry id here")))
            .EnsureSuccessStatusCode();
        Assert.Equal(waba.Company, Assert.Single(CloudWebhookKit.Threads(_f, silent)).CompanyEntityId);

        var right = Sender();
        (await Post(CloudWebhookKit.Payload("PN-ROUTE-W", right, "wamid.W-RIGHT", "matching waba", waba: "WABA-RIGHT")))
            .EnsureSuccessStatusCode();
        Assert.Equal(waba.Company, Assert.Single(CloudWebhookKit.Threads(_f, right)).CompanyEntityId);
    }

    [Fact]
    public void The_payload_parser_surfaces_the_waba_next_to_the_number()
    {
        var root = JsonSerializer.Deserialize<JsonElement>(
            CloudWebhookKit.Payload("PN-X", "60123", "w1", "hi", waba: "WABA-X"));
        Assert.Equal(("PN-X", "WABA-X"), Assert.Single(MetaWebhook.CloudNumbers(root)));
        var bare = JsonSerializer.Deserialize<JsonElement>(CloudWebhookKit.Payload("PN-X", "60123", "w1", "hi"));
        Assert.Equal(("PN-X", ""), Assert.Single(MetaWebhook.CloudNumbers(bare)));
        Assert.Equal(new List<string> { "PN-X" }, MetaWebhook.PhoneNumberIds(bare));   // unchanged
        // garbage never throws (Meta must always get its 200)
        Assert.Empty(MetaWebhook.CloudNumbers(JsonSerializer.Deserialize<JsonElement>("""{"entry":"nope"}""")));
    }
}

/// <summary>
/// The compatibility Setting <c>whatsapp_accept_unknown_numbers</c> puts the OLD
/// permissive bucketing back: an unmatched number files into the unattributed
/// bucket, which the next company to touch the conversation adopts. This test
/// pins that behaviour — and is the proof that the strict suite above would FAIL
/// against the pre-fix code, since this is exactly what it used to do with the
/// Setting absent. Its own host: the Setting is organization-wide.
/// </summary>
public class WhatsAppUnknownNumberCompatTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public WhatsAppUnknownNumberCompatTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task The_compatibility_setting_restores_the_old_unattributed_bucketing()
    {
        var su = await CloudWebhookKit.Authed(_f, "superadmin", "super123");
        (await su.PutAsJsonAsync("/api/settings",
            new { key = CloudInboxRouting.AcceptUnknownSetting, value = "on" })).EnsureSuccessStatusCode();
        var b = CloudWebhookKit.Company(_f, "Compat B", "PN-COMPAT-B").Company;
        var from = "60155900001";

        var c = _f.CreateClient();
        (await c.PostAsync("/api/whatsapp/webhook",
            CloudWebhookKit.Signed(CloudWebhookKit.Payload("PN-COMPAT-GHOST", from, "wamid.C1", "ghost text"))))
            .EnsureSuccessStatusCode();

        // the old behaviour: an unattributed conversation in the shared bucket
        var bucketed = Assert.Single(CloudWebhookKit.Threads(_f, from));
        Assert.Null(bucketed.CompanyEntityId);
        Assert.Contains(CloudWebhookKit.Messages(_f, bucketed.Id), m => m.Text.Contains("ghost text"));
        Assert.Empty(CloudWebhookKit.UnknownNumberAudits(_f, "PN-COMPAT-GHOST"));   // nothing dropped, nothing logged

        // … which company B then ADOPTS, inheriting the other number's conversation
        (await c.PostAsync("/api/whatsapp/webhook",
            CloudWebhookKit.Signed(CloudWebhookKit.Payload("PN-COMPAT-B", from, "wamid.C2", "hello B"))))
            .EnsureSuccessStatusCode();
        var adopted = Assert.Single(CloudWebhookKit.Threads(_f, from));
        Assert.Equal(bucketed.Id, adopted.Id);
        Assert.Equal(b, adopted.CompanyEntityId);
        var texts = CloudWebhookKit.Messages(_f, adopted.Id).Select(m => m.Text).ToList();
        Assert.Contains(texts, t => t.Contains("ghost text"));   // the leak, switched back on deliberately
        Assert.Contains(texts, t => t.Contains("hello B"));
    }
}

/// <summary>
/// An install with NO Cloud number configured (sandbox, demo, a Baileys-only
/// install) is untouched: there is nothing to route by, unsigned posts are still
/// accepted and any number still opens a conversation — "sandbox must never raise".
/// </summary>
public class WhatsAppSandboxWebhookTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public WhatsAppSandboxWebhookTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task An_install_with_no_cloud_numbers_accepts_any_phone_number_id()
    {
        var from = "60155800001";
        var json = CloudWebhookKit.Payload("PN-SANDBOX", from, "wamid.SB1", "first time writing");
        // unsigned, as a sandbox install's Meta test payloads arrive
        (await _f.CreateClient().PostAsync("/api/whatsapp/webhook",
            new StringContent(json, Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();

        var thread = Assert.Single(CloudWebhookKit.Threads(_f, from));
        Assert.Null(thread.CompanyEntityId);
        Assert.Contains(CloudWebhookKit.Messages(_f, thread.Id), m => m.Text.Contains("first time writing"));
        Assert.Empty(CloudWebhookKit.UnknownNumberAudits(_f, "PN-SANDBOX"));
    }
}

/// <summary>
/// The production shape: WhatsApp numbers exist, but none of them is a Cloud number
/// (QR-paired through Baileys). No Cloud number is subscribed with Meta, so nothing
/// arriving on the Cloud webhook can be legitimate — and the endpoint is public and
/// unsigned while no Cloud account exists. An unsigned post naming a real customer's
/// number must therefore be dropped, not filed into that customer's conversation.
/// </summary>
public class WhatsAppUnofficialOnlyWebhookTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public WhatsAppUnofficialOnlyWebhookTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task A_baileys_only_install_drops_an_unsigned_cloud_payload()
    {
        CloudWebhookKit.WithDb(_f, db =>
        {
            db.ChannelAccounts.Add(new ChannelAccount
            {
                Channel = ChatBranch.Domain.Channels.WhatsApp, Provider = Providers.Baileys,
                Name = "WhatsApp (QR)", ExternalId = "3", CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });

        var from = "60155800002";
        var json = CloudWebhookKit.Payload("PN-GHOST", from, "wamid.GH1", "appended by a stranger");
        var res = await _f.CreateClient().PostAsync("/api/whatsapp/webhook",
            new StringContent(json, Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();                                  // 200: Meta must stop retrying

        Assert.Empty(CloudWebhookKit.Threads(_f, from));                // nothing filed, nowhere
        var audit = Assert.Single(CloudWebhookKit.UnknownNumberAudits(_f, "PN-GHOST"));
        Assert.DoesNotContain("appended by a stranger", audit.Detail ?? "");
        Assert.DoesNotContain(from, audit.Detail ?? "");
    }
}

/// <summary>
/// <c>uniq_channelaccount_wa_pnid</c> — two WhatsApp accounts may no longer hold the
/// same number, because the Cloud webhook matches by it and "the first row wins" was
/// a coin flip between two companies. On an EXISTING database the index is added by
/// <c>Seed.UpgradeSchema</c> (and by <c>PostgresSchema.Sync</c> from the same EF
/// mapping) — defensively: an install that already holds a duplicate must still boot.
/// </summary>
public class WhatsAppNumberIndexTests
{
    private static DbContextOptions<AppDbContext> Options(string file) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={file}").Options;

    private static List<string> AccountIndexes(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'crm_channelaccount'";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private static ChannelAccount Wa(string pnid, string provider = "cloud") => new()
    {
        Channel = ChatBranch.Domain.Channels.WhatsApp, Provider = provider, Name = $"n-{pnid}", ExternalId = pnid,
        CreatedAt = DateTime.UtcNow,
    };

    private static void InTempDb(Action<Func<AppDbContext>> body)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cb-wa-pnid-{Guid.NewGuid():N}.sqlite3");
        var options = Options(file);
        try { body(() => new AppDbContext(options)); }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_second_account_on_the_same_whatsapp_number_is_rejected()
    {
        InTempDb(open =>
        {
            using (var db = open()) Seed.Run(db);
            using (var db = open())
            {
                Assert.Contains(AppDbContext.WhatsAppNumberIndex, AccountIndexes(db));
                db.ChannelAccounts.Add(Wa("PN-ONE"));
                db.SaveChanges();
                db.ChannelAccounts.Add(Wa("PN-ONE", provider: ""));           // same number, another row
                Assert.Throws<DbUpdateException>(() => db.SaveChanges());
            }
            using (var db = open())
            {
                // rows with NO number yet are not duplicates of each other (partial index)
                db.ChannelAccounts.Add(Wa(""));
                db.ChannelAccounts.Add(Wa(""));
                db.SaveChanges();
                // nor are the OTHER WhatsApp providers: a BSP / QR row is addressed by its
                // own webhook URL or gateway key, and one number may sit on two of them
                // while it is being migrated
                db.ChannelAccounts.Add(Wa("60123456789", provider: "twilio"));
                db.ChannelAccounts.Add(Wa("60123456789", provider: "ycloud"));
                db.ChannelAccounts.Add(Wa("60123456789", provider: "baileys"));
                db.SaveChanges();
                // and another channel may reuse the string
                db.ChannelAccounts.Add(new ChannelAccount
                {
                    Channel = ChatBranch.Domain.Channels.Telegram, Name = "tg", ExternalId = "PN-ONE", CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
            }
        });
    }

    [Fact]
    public void Upgrading_an_existing_database_creates_the_number_index()
    {
        InTempDb(open =>
        {
            using (var db = open()) Seed.Run(db);
            using (var db = open())
            {
                db.Database.ExecuteSqlRaw($"DROP INDEX \"{AppDbContext.WhatsAppNumberIndex}\"");
                db.ChannelAccounts.Add(Wa("PN-KEEP"));
                db.SaveChanges();
                Assert.DoesNotContain(AppDbContext.WhatsAppNumberIndex, AccountIndexes(db));
            }
            using (var db = open()) Seed.Run(db);              // the boot after the deploy
            using (var db = open())
            {
                Assert.Contains(AppDbContext.WhatsAppNumberIndex, AccountIndexes(db));
                Assert.Single(db.ChannelAccounts.Where(a => a.ExternalId == "PN-KEEP"));   // data kept
            }
        });
    }

    /// <summary>
    /// A partial unique index that aborts startup would be worse than the bug it
    /// prevents: a database that already holds a duplicate boots, keeps both rows
    /// and simply has no index (the clash is logged for an operator).
    /// </summary>
    [Fact]
    public void A_database_that_already_holds_a_duplicate_number_still_boots()
    {
        InTempDb(open =>
        {
            using (var db = open()) Seed.Run(db);
            using (var db = open())
            {
                db.Database.ExecuteSqlRaw($"DROP INDEX \"{AppDbContext.WhatsAppNumberIndex}\"");
                db.ChannelAccounts.Add(Wa("PN-CLASH"));
                db.ChannelAccounts.Add(Wa("PN-CLASH", provider: ""));
                db.SaveChanges();
            }
            using (var db = open()) Seed.Run(db);              // must NOT throw
            using (var db = open())
            {
                Assert.DoesNotContain(AppDbContext.WhatsAppNumberIndex, AccountIndexes(db));
                Assert.Equal(2, db.ChannelAccounts.Count(a => a.ExternalId == "PN-CLASH"));
            }
        });
    }
}
