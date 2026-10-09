using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Contacts;
using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A Baileys gateway that answers number lookups from a script and sends nothing anywhere.</summary>
public sealed class CheckGateway : IBaileysGateway
{
    /// <summary>digits → exists (null = inconclusive).</summary>
    public Dictionary<string, bool?> Numbers { get; } = new();
    public List<(string Account, string Number)> Checks { get; } = new();
    public List<(string To, string Text)> Sent { get; } = new();
    public bool IsConfigured => true;
    public string BaseUrl => "http://gateway.test";

    public Task<BaileysCheck> CheckNumberAsync(string account, string number, CancellationToken ct = default)
    {
        lock (Checks) Checks.Add((account, number));
        return Task.FromResult(Numbers.TryGetValue(number, out var exists)
            ? new BaileysCheck(exists, exists == true ? $"{number}@s.whatsapp.net" : null,
                exists == null ? "timed out" : "")
            : new BaileysCheck(null, null, "gateway 500: account not connected"));
    }

    public Task<BaileysResult> SendTextAsync(string a, string to, string text, CancellationToken ct = default)
    {
        lock (Sent) Sent.Add((to, text));
        return Task.FromResult(new BaileysResult(true, "sent", $"wamid-{Guid.NewGuid():N}"));
    }

    public Task<BaileysResult> StartCallAsync(string a, string to, Dictionary<string, object?> d,
        CancellationToken ct = default) => Task.FromResult(new BaileysResult(false, "no calls in tests"));
    public Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "ended"));
    private static readonly BaileysStatus Connected = new("connected", null, "me", null);
    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
}

/// <summary>Google People API stand-in: a scripted contact book (or an error).</summary>
public sealed class FakePeople : IGooglePeopleClient
{
    public List<GooglePerson> People { get; } = new();
    public string? Error { get; set; }
    public List<string> Tokens { get; } = new();

    public Task<(List<GooglePerson> People, string? Error)> ConnectionsAsync(string accessToken, int max,
        CancellationToken ct = default)
    {
        Tokens.Add(accessToken);
        return Task.FromResult((Error != null ? new List<GooglePerson>() : People.Take(max).ToList(), Error));
    }
}

/// <summary>
/// The API host for the contacts-identity suites: a scripted Baileys gateway
/// (lookups), no lookup rate limit (the limiter has its own unit test), a fake
/// People API, and the lookup worker off so passes are driven explicitly.
/// </summary>
public class IdentityFactory : ApiFactory
{
    public CheckGateway Gateway { get; } = new();
    public FakePeople People { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<IBaileysGateway>(Gateway);
            s.AddSingleton(new NumberCheckLimiter(TimeSpan.Zero));
            s.AddSingleton<IGooglePeopleClient>(People);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("IDENTITY_CHECK_WORKER", "0");
        return base.CreateHost(builder);
    }
}

/// <summary>Shared plumbing for the identity / invite / unknown-customer / import suites.</summary>
public abstract class IdentityApiBase
{
    protected readonly IdentityFactory Factory;
    protected IdentityApiBase(IdentityFactory factory) => Factory = factory;

    protected async Task<HttpClient> Authed(string user = "admin", string pass = "admin123")
    {
        var c = Factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    protected T InScope<T>(Func<IServiceProvider, AppDbContext, T> work)
    {
        using var scope = Factory.Services.CreateScope();
        return work(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected void InScope(Action<IServiceProvider, AppDbContext> work) =>
        InScope<int>((sp, db) => { work(sp, db); return 0; });

    /// <summary>A unique 11-digit Malaysian mobile (60 + 1 + 8 digits) per call.</summary>
    protected static string NewNumber()
    {
        var n = Interlocked.Increment(ref _seq);
        return $"601{(Environment.ProcessId % 90 + 10):00}{n:000000}";
    }
    private static int _seq = Random.Shared.Next(1000, 900000);

    protected async Task<int> CreateContact(HttpClient c, string name, string phone, string email = "")
    {
        var res = await c.PostAsJsonAsync("/api/contacts", new { name, phone, email });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    protected int AddAccount(string channel, string name, string provider = "cloud", string externalId = "",
        Dictionary<string, object?>? config = null, string token = "", string appSecret = "", int? entityId = null) =>
        InScope((_, db) =>
        {
            var a = new ChannelAccount
            {
                Channel = channel, Provider = provider, Name = name, ExternalId = externalId, AccessToken = token,
                AppSecret = appSecret, Config = config ?? new(), EntityId = entityId, CreatedAt = DateTime.UtcNow,
            };
            db.ChannelAccounts.Add(a);
            db.SaveChanges();
            return a.Id;
        });
}

/// <summary>
/// Part A — identities discovered from the contact's own phone / email on
/// create, edit and hand registration, and the WhatsApp lookup queue
/// (verified / not on WhatsApp / unknown, the 30-day cache, the rate limit).
/// </summary>
public class ContactIdentityTests : IdentityApiBase, IClassFixture<IdentityFactory>
{
    public ContactIdentityTests(IdentityFactory factory) : base(factory) { }

    // ---- phone normalisation -------------------------------------------------

    [Theory]
    [InlineData("012-345 6789", "+60123456789")]
    [InlineData("60123456789", "+60123456789")]
    [InlineData("+60 12 345 6789", "+60123456789")]
    [InlineData("0060123456789", "+60123456789")]
    [InlineData("123456789", "+60123456789")]
    [InlineData("+1 (415) 555-0123", "+14155550123")]
    [InlineData("6591234567890", "+6591234567890")]
    [InlineData("12345", "")]
    [InlineData("call me", "")]
    [InlineData("", "")]
    public void Phones_become_E164_with_the_default_country_code(string raw, string expected) =>
        Assert.Equal(expected, PhoneNormalizer.ToE164(raw, "60"));

    // ---- discovery on create / edit / hand registration ----------------------------

    private async Task<JsonElement> Hub(HttpClient c, int id) =>
        await c.GetFromJsonAsync<JsonElement>($"/api/contacts/{id}/channels");

    private static JsonElement Channel(JsonElement hub, string ch) =>
        hub.GetProperty("channels").EnumerateArray().First(x => x.GetProperty("channel").GetString() == ch);

    [Fact]
    public async Task Creating_a_contact_registers_its_WhatsApp_voice_and_email_identities()
    {
        var admin = await Authed();
        var number = NewNumber();
        var id = await CreateContact(admin, "Disco Dina", "0" + number[2..], "Dina@Example.com");

        var hub = await Hub(admin, id);
        var wa = Channel(hub, "whatsapp").GetProperty("identities").EnumerateArray().Single();
        Assert.Equal(number, wa.GetProperty("key").GetString());          // digits of the E.164 form
        Assert.True(wa.GetProperty("registered").GetBoolean());
        Assert.Equal(0, wa.GetProperty("message_count").GetInt32());
        var email = Channel(hub, "email").GetProperty("identities").EnumerateArray().Single();
        Assert.Equal("dina@example.com", email.GetProperty("address").GetString());
        Assert.Equal("+" + number, Channel(hub, "voice").GetProperty("number").GetString());
        Assert.Contains(hub.GetProperty("no_lookup_channels").EnumerateArray(), x => x.GetString() == "telegram");
        // no empty conversation lands in the inbox for a registered identity
        Assert.False(InScope((_, db) => db.Threads.Any(t => t.Key == number)));
    }

    [Fact]
    public void Without_a_QR_number_the_WhatsApp_identity_is_unverified_and_says_why()
    {
        // this factory's DB has no Baileys account until a test adds one, so use a
        // throwaway in-memory DB for the pure "no QR number" rule
        using var t = new TestDb();
        var c = new Contact { Name = "Q", Phone = "+60111222333", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Contacts.Add(c);
        t.Db.SaveChanges();
        var r = new ContactIdentityService(t.Db, new ScopingService(t.Db)).Discover(c);
        Assert.False(r.LookupAvailable);
        var wa = r.Identities.Single(i => i.Channel == "whatsapp");
        Assert.Equal(IdentityVerdicts.Unknown, wa.Verification);
        Assert.Contains("Cloud API", wa.VerificationDetail);
    }

    [Fact]
    public async Task Editing_the_phone_replaces_the_discovered_identities_and_SMS_needs_an_sms_account()
    {
        var admin = await Authed();
        var first = NewNumber();
        var second = NewNumber();
        var id = await CreateContact(admin, "Edit Ed", "+" + first);
        Assert.False(InScope((_, db) => db.ContactIdentities.Any(i => i.ContactId == id && i.Channel == "sms")));

        AddAccount("sms", "SMS line");
        var patch = await admin.PatchAsJsonAsync($"/api/contacts/{id}", new { phone = "+" + second });
        patch.EnsureSuccessStatusCode();

        var rows = InScope((_, db) => db.ContactIdentities.AsNoTracking().Where(i => i.ContactId == id).ToList());
        Assert.DoesNotContain(rows, r => r.Address.Contains(first));
        Assert.Contains(rows, r => r.Channel == "whatsapp" && r.Address == second);
        Assert.Contains(rows, r => r.Channel == "voice" && r.Address == "+" + second);
        Assert.Contains(rows, r => r.Channel == "sms" && r.Address == second);
        var hub = await Hub(admin, id);
        Assert.Equal(second, Channel(hub, "sms").GetProperty("identities")[0].GetProperty("address").GetString());
    }

    [Fact]
    public async Task WhatsApp_lookups_go_through_the_QR_number_and_land_on_the_hub()
    {
        var admin = await Authed();
        AddAccount("whatsapp", "QR phone", provider: "baileys", externalId: "qr-lookup");
        var on = NewNumber();
        var off = NewNumber();
        var odd = NewNumber();
        Factory.Gateway.Numbers[on] = true;
        Factory.Gateway.Numbers[off] = false;
        Factory.Gateway.Numbers[odd] = null;
        var onId = await CreateContact(admin, "On Wa", "+" + on);
        var offId = await CreateContact(admin, "Off Wa", "+" + off);
        var oddId = await CreateContact(admin, "Odd Wa", "+" + odd);

        for (var i = 0; i < 20; i++)
        {
            var pass = await (await admin.PostAsync("/api/contacts/identity-checks/run", null))
                .Content.ReadFromJsonAsync<JsonElement>();
            if (pass.GetProperty("remaining").GetInt32() == 0) break;
        }

        string Verdict(JsonElement hub) => Channel(hub, "whatsapp").GetProperty("identities")[0]
            .GetProperty("verification").GetString()!;
        Assert.Equal("verified", Verdict(await Hub(admin, onId)));
        Assert.Equal("not_on_whatsapp", Verdict(await Hub(admin, offId)));
        Assert.Equal("unknown", Verdict(await Hub(admin, oddId)));
        Assert.Contains(Factory.Gateway.Checks, c => c.Account == "qr-lookup" && c.Number == on);
        // a lookup is a lookup — nothing was ever sent to the numbers
        Assert.DoesNotContain(Factory.Gateway.Sent, s => s.To == on || s.To == off);

        // "Find channels" keeps a fresh verdict (30-day cache) and re-queues the unknown one
        var checksBefore = Factory.Gateway.Checks.Count(c => c.Number == on);
        var find = await (await admin.PostAsync($"/api/contacts/{onId}/channels/discover", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, find.GetProperty("queued").GetInt32());
        var again = await (await admin.PostAsync($"/api/contacts/{oddId}/channels/discover", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, again.GetProperty("queued").GetInt32());

        // the same number on another contact reuses the verdict without a lookup
        var twinId = await CreateContact(admin, "Twin On", "0" + on[2..]);
        Assert.Equal("verified", Verdict(await Hub(admin, twinId)));
        Assert.Equal(checksBefore, Factory.Gateway.Checks.Count(c => c.Number == on));
    }

    // ---- the checker itself (rate limit, cache, no QR) on a throwaway DB -----------------

    private static (TestDb T, ManualClock Clock) CheckerDb(params string[] numbers)
    {
        var t = new TestDb();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        t.Db.ChannelAccounts.Add(new ChannelAccount
        {
            Channel = "whatsapp", Provider = "baileys", Name = "QR", ExternalId = "qr-1", CreatedAt = DateTime.UtcNow,
        });
        foreach (var n in numbers)
        {
            var c = new Contact { Name = n, Phone = "+" + n, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            t.Db.Contacts.Add(c);
            t.Db.SaveChanges();
            new ContactIdentityService(t.Db, new ScopingService(t.Db), clock).Discover(c);
        }
        return (t, clock);
    }

    [Fact]
    public async Task The_checker_does_at_most_one_lookup_per_two_seconds_per_gateway()
    {
        var (t, clock) = CheckerDb("60111000001", "60111000002", "60111000003");
        using var _ = t;
        var gw = new CheckGateway();
        gw.Numbers["60111000001"] = true;
        gw.Numbers["60111000002"] = false;
        gw.Numbers["60111000003"] = true;
        var checker = new WhatsAppNumberChecker(t.Db, gw, new NumberCheckLimiter(), clock);

        Assert.Equal(1, (await checker.RunOnceAsync()).Checked);
        var blocked = await checker.RunOnceAsync();           // same instant
        Assert.True(blocked.RateLimited);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await checker.RunOnceAsync()).RateLimited);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await checker.RunOnceAsync()).Checked);
        Assert.Equal(2, gw.Checks.Count);
        clock.Advance(TimeSpan.FromSeconds(2));
        var last = await checker.RunOnceAsync();
        Assert.Equal(0, last.Remaining);
        Assert.Equal(3, gw.Checks.Count);
        Assert.Equal(new[] { "not_on_whatsapp", "verified", "verified" },
            t.Db.ContactIdentities.Where(i => i.Channel == "whatsapp").OrderBy(i => i.Address)
                .Select(i => i.Verification).ToArray().OrderBy(v => v).ToArray());
    }

    [Fact]
    public async Task Verdicts_are_cached_thirty_days_per_number()
    {
        var (t, clock) = CheckerDb("60111000009");
        using var _ = t;
        var gw = new CheckGateway();
        gw.Numbers["60111000009"] = true;
        var limiter = new NumberCheckLimiter(TimeSpan.Zero);
        await new WhatsAppNumberChecker(t.Db, gw, limiter, clock).RunOnceAsync();
        Assert.Single(gw.Checks);

        // a second contact with the same number: cached, no lookup
        var twin = new Contact { Name = "Twin", Phone = "0111000009", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.Contacts.Add(twin);
        t.Db.SaveChanges();
        clock.Advance(TimeSpan.FromDays(10));
        var svc = new ContactIdentityService(t.Db, new ScopingService(t.Db), clock);
        svc.Discover(twin);
        Assert.Equal("verified", t.Db.ContactIdentities.Single(i => i.ContactId == twin.Id && i.Channel == "whatsapp").Verification);
        Assert.Single(gw.Checks);

        // after 30 days the verdict is stale: "Find channels" queues a fresh lookup
        clock.Advance(TimeSpan.FromDays(21));
        Assert.Equal(1, svc.Discover(twin, recheck: true).Queued);
        await new WhatsAppNumberChecker(t.Db, gw, limiter, clock).RunOnceAsync();
        Assert.Equal(2, gw.Checks.Count);
    }

    [Fact]
    public async Task With_no_QR_number_connected_pending_lookups_become_unknown()
    {
        var (t, clock) = CheckerDb("60111000010");
        using var _ = t;
        // the QR number gets banned before the queue is worked
        var qr = t.Db.ChannelAccounts.Single();
        qr.HealthStatus = Health.Banned;
        t.Db.SaveChanges();
        var gw = new CheckGateway();
        var pass = await new WhatsAppNumberChecker(t.Db, gw, new NumberCheckLimiter(), clock).RunOnceAsync();
        Assert.Equal(1, pass.Unknown);
        Assert.Empty(gw.Checks);
        Assert.Equal(IdentityVerdicts.Unknown, t.Db.ContactIdentities.Single(i => i.Channel == "whatsapp").Verification);
    }

    [Fact]
    public async Task Discovery_needs_contacts_manage_and_the_run_seam_is_admin_only()
    {
        var admin = await Authed();
        var id = await CreateContact(admin, "Perm Pat", "+" + NewNumber());
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsync($"/api/contacts/{id}/channels/discover", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsync("/api/contacts/identity-checks/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Factory.CreateClient().PostAsync($"/api/contacts/{id}/channels/discover", null)).StatusCode);
    }
}
