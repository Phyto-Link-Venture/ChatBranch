using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Zernio;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Tests;

/// <summary>
/// Self-serve WhatsApp onboarding through the managed connection: the Embedded Signup start (the
/// mode is asked for, the hosted picker is requested, the callback carries a one-time state), the
/// callback that mirrors exactly one inbox and survives a reload, and the phone numbers — the
/// monthly price on the list, and a confirmation plus an audit row on every purchase and release.
/// Every Zernio call goes to <see cref="FakeZernioClient"/>; nothing leaves the process.
/// </summary>
public class ZernioWhatsAppOnboardingTests : IClassFixture<ZernioFactory>
{
    private readonly ZernioFactory _f;

    public ZernioWhatsAppOnboardingTests(ZernioFactory f)
    {
        _f = f;
        _ = _f.CreateClient();
        _f.Zernio.Reset();
        _f.Zernio.Profiles.Add(new { _id = "prof-default", name = "Default", isDefault = true, externalId = (string?)null });
    }

    private T InDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<HttpClient> Admin() => SettingsTestKit.Client(_f, "admin", "admin123");

    /// <summary>The browser coming back from Meta / Zernio: no auth header, no redirect following.</summary>
    private HttpClient Browser() => _f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string U() => Guid.NewGuid().ToString("N")[..8];

    private async Task<JsonElement> Start(HttpClient c, string? onboarding, string? signup = null)
    {
        var body = new Dictionary<string, object?> { ["platform"] = "whatsapp", ["purpose"] = "inbox" };
        if (onboarding != null) body["onboarding"] = onboarding;
        if (signup != null) body["signup"] = signup;
        var res = await c.PostAsJsonAsync("/api/social/connect", body);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A WhatsApp account Zernio lists in the default profile (what the callback mirrors).</summary>
    private string ListedAccount(string? phone = null)
    {
        var zid = "zwa-" + U();
        _f.Zernio.Accounts.Add(new
        {
            _id = zid, platform = "whatsapp", username = phone ?? "+60 12-345 6789", displayName = "Kedai Kopi",
            isActive = true, phoneNumber = phone ?? "+60 12-345 6789",
            profileId = new { _id = "prof-default", name = "Default" },
        });
        return zid;
    }

    // ---- the start: the mode is a decision, and the query says so --------------------

    [Fact]
    public async Task The_mode_must_be_chosen_and_the_hosted_signup_is_requested_with_our_callback()
    {
        var c = await Admin();

        // no mode = no start: coexistence is what Zernio would silently do
        var refused = await c.PostAsJsonAsync("/api/social/connect", new { platform = "whatsapp", purpose = "inbox" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var why = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("onboarding_required", why.GetProperty("code").GetString());
        Assert.Equal(new[] { "api", "business_app" },
            why.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()).ToArray());
        Assert.Empty(_f.Zernio.Of("ConnectUrl"));

        // Cloud API only + the hosted picker
        var api = await Start(c, "api");
        Assert.Equal("hosted", api.GetProperty("flow").GetString());
        Assert.Equal("api", api.GetProperty("onboarding").GetString());
        Assert.Equal("hosted", api.GetProperty("signup").GetString());
        Assert.False(api.GetProperty("coexistence").GetBoolean());
        var state = api.GetProperty("state").GetString()!;
        Assert.True(state.Length >= 24);
        var call = Assert.Single(_f.Zernio.Of("ConnectUrl"));
        Assert.Equal("whatsapp", call["platform"]);
        Assert.Equal("api", call["onboarding"]);
        Assert.Equal("hosted", call["signup"]);
        var redirect = call["redirect_url"]!.ToString()!;
        Assert.Contains("/api/social/connect/whatsapp/callback?cb=" + state, redirect);
        // the warnings a person must see before they run it
        var codes = api.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Contains("one_provider", codes);
        Assert.Contains("leaves_business_app", codes);
        // the credentials form stays reachable as the advanced fallback
        Assert.Equal("/api/social/connect/whatsapp",
            api.GetProperty("alternatives")[0].GetProperty("submit_url").GetString());

        // coexistence runs in Meta's own window (its QR step), so the hosted picker is not sent
        var co = await Start(c, "business_app");
        Assert.True(co.GetProperty("coexistence").GetBoolean());
        Assert.Equal("meta", co.GetProperty("signup").GetString());
        var coCall = _f.Zernio.Of("ConnectUrl").Last();
        Assert.Equal("business_app", coCall["onboarding"]);
        Assert.Null(coCall["signup"]);
        var coCodes = co.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Contains("coexistence_scan", coCodes);
        Assert.Contains("coexistence_limits", coCodes);

        // an unknown mode is refused before Zernio
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/social/connect", new { platform = "whatsapp", onboarding = "whatever" })).StatusCode);

        // every other platform's connect is untouched: neither parameter is sent
        await c.PostAsJsonAsync("/api/social/connect", new { platform = "instagram", purpose = "social" });
        var ig = _f.Zernio.Of("ConnectUrl").Last();
        Assert.Equal("instagram", ig["platform"]);
        Assert.Null(ig["onboarding"]);
        Assert.Null(ig["signup"]);
    }

    // ---- the callback ---------------------------------------------------------------

    [Fact]
    public async Task The_callback_mirrors_exactly_one_inbox_and_a_reload_adds_nothing()
    {
        var c = await Admin();
        var started = await Start(c, "api");
        var state = started.GetProperty("state").GetString()!;
        var zid = ListedAccount("+60 11-222 3333");

        var back = await Browser().GetAsync($"/api/social/connect/whatsapp/callback?cb={state}&connected=whatsapp&accountId={zid}&stage=callback");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        var landing = back.Headers.Location!.ToString();
        Assert.Contains("/channels?zernio=connected", landing);
        Assert.Contains("platform=whatsapp", landing);
        Assert.DoesNotContain("pending=1", landing);

        var rows = InDb(db => db.ChannelAccounts.AsNoTracking().Where(a => a.ExternalId == zid).ToList());
        var inbox = Assert.Single(rows);
        Assert.Equal(ChannelIds.WhatsApp, inbox.Channel);
        Assert.Equal(Providers.Zernio, inbox.Provider);
        Assert.Null(inbox.EntityId);                 // started at the organization level

        // the client reloads the callback: the same inbox, no second one, one completion audited
        var again = await Browser().GetAsync($"/api/social/connect/whatsapp/callback?cb={state}&connected=whatsapp&accountId={zid}");
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Contains("zernio=connected", again.Headers.Location!.ToString());
        Assert.Single(InDb(db => db.ChannelAccounts.AsNoTracking().Where(a => a.ExternalId == zid).ToList()));
        // one completion for THIS account, however often the callback is reloaded
        Assert.Single(InDb(db => db.AuditLogs.Where(a => a.Action == "zernio.connect" && a.Target == "whatsapp"
                                                         && a.Detail.Contains("completed")
                                                         && a.Detail.Contains(zid)).ToList()));
    }

    [Fact]
    public async Task The_number_lands_in_the_outlet_the_connect_was_started_for()
    {
        var c = await Admin();
        var name = "WA Outlet " + U();
        var entityId = InDb(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var e = new Entity { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), TenantId = tenant.Id };
            db.Entities.Add(e);
            db.SaveChanges();
            return e.Id;
        });
        var slug = InDb(db => db.Entities.Single(e => e.Id == entityId).Slug);

        var res = await c.PostAsJsonAsync("/api/social/connect",
            new { platform = "whatsapp", purpose = "inbox", entity = slug, onboarding = "business_app" });
        var started = await res.Content.ReadFromJsonAsync<JsonElement>();
        var state = started.GetProperty("state").GetString()!;
        var profile = started.GetProperty("profile_id").GetString()!;
        Assert.Equal($"cb-entity-{entityId}", Assert.Single(_f.Zernio.Of("CreateProfile"))["externalId"]);

        // the number the client picked, as Zernio lists it in THAT outlet's profile
        var zid = "zwa-" + U();
        _f.Zernio.Accounts.Add(new
        {
            _id = zid, platform = "whatsapp", username = "+60 19-888 7777", displayName = "Cawangan",
            isActive = true, phoneNumber = "+60 19-888 7777", profileId = profile,
        });
        var back = await Browser().GetAsync($"/api/social/connect/whatsapp/callback?cb={state}&connected=whatsapp&accountId={zid}");
        Assert.Contains("zernio=connected", back.Headers.Location!.ToString());

        var inbox = Assert.Single(InDb(db => db.ChannelAccounts.AsNoTracking().Where(a => a.ExternalId == zid).ToList()));
        Assert.Equal(ChannelIds.WhatsApp, inbox.Channel);
        Assert.Equal(entityId, inbox.EntityId);
        Assert.Equal(Providers.Zernio, inbox.Provider);
    }

    [Fact]
    public async Task A_callback_without_our_state_or_with_a_foreign_account_connects_nothing()
    {
        var c = await Admin();
        var zid = ListedAccount();

        // never started here
        var forged = await Browser().GetAsync($"/api/social/connect/whatsapp/callback?cb=deadbeefdeadbeefdeadbeefdeadbeef&accountId={zid}");
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);
        Assert.Contains("zernio=error", forged.Headers.Location!.ToString());
        Assert.Empty(InDb(db => db.ChannelAccounts.AsNoTracking().Where(a => a.ExternalId == zid).ToList()));

        // no state at all
        var bare = await Browser().GetAsync($"/api/social/connect/whatsapp/callback?accountId={zid}");
        Assert.Equal(HttpStatusCode.Redirect, bare.StatusCode);
        Assert.Contains("zernio=error", bare.Headers.Location!.ToString());

        // a real round trip, but Zernio's own state does not match the one it gave us
        var started = await Start(c, "api");
        var state = started.GetProperty("state").GetString()!;
        var mismatch = await Browser().GetAsync(
            $"/api/social/connect/whatsapp/callback?cb={state}&state=not-the-one&accountId={zid}");
        Assert.Contains("zernio=error", mismatch.Headers.Location!.ToString());
        Assert.Empty(InDb(db => db.ChannelAccounts.AsNoTracking().Where(a => a.ExternalId == zid).ToList()));

        // an id Zernio does not list in this profile is never mirrored blind
        var unknown = await Browser().GetAsync(
            $"/api/social/connect/whatsapp/callback?cb={state}&accountId=zwa-not-listed");
        var where = unknown.Headers.Location!.ToString();
        Assert.Contains("zernio=connected", where);
        Assert.Contains("pending=1", where);         // the webhook / reconcile makes the inbox
        Assert.Empty(InDb(db => db.ChannelAccounts.AsNoTracking().Where(a => a.ExternalId == "zwa-not-listed").ToList()));

        // Meta refused: the person is told why, and nothing is connected
        var denied = await Browser().GetAsync(
            $"/api/social/connect/whatsapp/callback?cb={state}&error=oauth_denied&error_message=Consent+denied&stage=authorize");
        Assert.Contains("zernio=error", denied.Headers.Location!.ToString());
        Assert.Contains("Consent", Uri.UnescapeDataString(denied.Headers.Location!.ToString()));
    }

    // ---- the phone numbers ----------------------------------------------------------

    private void Offer(string code, int cents, bool kyc = false) =>
        _f.Zernio.Countries.Add(new
        {
            code, monthlyCents = cents, callsAvailable = true, smsAvailable = true, whatsappAvailable = true,
            needsKyc = kyc, inStock = true,
        });

    [Fact]
    public async Task The_list_quotes_the_monthly_price_of_what_is_owned_and_of_a_new_number()
    {
        var c = await Admin();
        Offer("US", 300);
        Offer("MY", 500, kyc: true);
        _f.Zernio.PhoneNumbers.Add(new
        {
            _id = "num-1", phoneNumber = "+14155550100", country = "US", status = "active", monthlyCents = 300,
            hostedByZernio = true, smsCapable = true, profileId = "prof-default",
        });
        // the organization level reads the key's default profile
        await Start(c, "api");

        var list = await c.GetFromJsonAsync<JsonElement>("/api/social/phone-numbers");
        var mine = Assert.Single(list.GetProperty("numbers").EnumerateArray().ToList());
        Assert.Equal("+14155550100", mine.GetProperty("phone_number").GetString());
        Assert.Equal(300, mine.GetProperty("monthly_cents").GetInt32());
        Assert.Equal("USD", mine.GetProperty("currency").GetString());
        var us = list.GetProperty("countries").EnumerateArray().First(x => x.GetProperty("code").GetString() == "US");
        Assert.Equal(300, us.GetProperty("monthly_cents").GetInt32());
        Assert.True(list.GetProperty("countries").EnumerateArray()
            .First(x => x.GetProperty("code").GetString() == "MY").GetProperty("needs_kyc").GetBoolean());
        // the profile filter is always sent: a bare list would be every company's numbers
        Assert.Equal("prof-default", _f.Zernio.Of("ListPhoneNumbers").Last()["profileId"]);
    }

    [Fact]
    public async Task Buying_a_number_needs_the_price_confirmed_and_an_intent_id_and_is_audited()
    {
        var c = await Admin();
        Offer("US", 300);

        // no confirmation: the answer is the price, and nothing is bought
        var quote = await c.PostAsJsonAsync("/api/social/phone-numbers/purchase", new { country = "US" });
        Assert.Equal(HttpStatusCode.BadRequest, quote.StatusCode);
        var q = await quote.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("confirmation_required", q.GetProperty("code").GetString());
        Assert.Equal(300, q.GetProperty("monthly_cents").GetInt32());
        Assert.Empty(_f.Zernio.Of("PurchasePhoneNumber"));

        // confirmed, but without an intent id a retry could buy twice
        var noIntent = await c.PostAsJsonAsync("/api/social/phone-numbers/purchase",
            new { country = "US", confirm = true });
        Assert.Equal(HttpStatusCode.BadRequest, noIntent.StatusCode);
        Assert.Equal("purchase_intent_required",
            (await noIntent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(_f.Zernio.Of("PurchasePhoneNumber"));

        var bought = await c.PostAsJsonAsync("/api/social/phone-numbers/purchase", new
        {
            country = "US", confirm = true, wants_sms = true, purchase_intent_id = "cb-order-1",
        });
        Assert.Equal(HttpStatusCode.OK, bought.StatusCode);
        var got = await bought.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(got.GetProperty("ok").GetBoolean());
        Assert.Equal(300, got.GetProperty("number").GetProperty("monthly_cents").GetInt32());
        var sent = Assert.Single(_f.Zernio.Of("PurchasePhoneNumber"));
        Assert.Equal("US", sent["country"]);
        Assert.Equal(true, sent["wantsSms"]);
        Assert.Equal(true, sent["connectWhatsapp"]);      // never left to Zernio's default
        Assert.Equal("cb-order-1", sent["purchaseIntentId"]);
        var audit = Assert.Single(InDb(db => db.AuditLogs.Where(a => a.Action == "zernio.phone_number.purchase").ToList()));
        Assert.Contains("US$3.00/month", audit.Detail);
    }

    [Fact]
    public async Task Releasing_a_number_needs_it_typed_out_and_is_audited()
    {
        var c = await Admin();
        _f.Zernio.PhoneNumbers.Add(new
        {
            _id = "num-9", phoneNumber = "+14155559999", country = "US", status = "active", monthlyCents = 300,
            hostedByZernio = true, profileId = "prof-default",
        });
        await Start(c, "api");                            // the default profile becomes known

        // no confirmation
        var ask = await c.DeleteAsync("/api/social/phone-numbers/num-9");
        Assert.Equal(HttpStatusCode.BadRequest, ask.StatusCode);
        Assert.Equal("confirmation_required",
            (await ask.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // confirmed, but the wrong number typed
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.DeleteAsync("/api/social/phone-numbers/num-9?confirm=true&phone_number=%2B14155550000")).StatusCode);
        Assert.Empty(_f.Zernio.Released);

        var done = await c.DeleteAsync("/api/social/phone-numbers/num-9?confirm=true&phone_number=%2B14155559999");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal("num-9", Assert.Single(_f.Zernio.Released));
        var audit = Assert.Single(InDb(db => db.AuditLogs.Where(a => a.Action == "zernio.phone_number.release").ToList()));
        Assert.Equal("+14155559999", audit.Target);
        Assert.Contains("irreversible", audit.Detail);
    }

    [Fact]
    public async Task Phone_numbers_and_the_signup_need_the_capability()
    {
        var agent = await SettingsTestKit.Client(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/social/phone-numbers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/social/phone-numbers/purchase",
                new { country = "US", confirm = true, purchase_intent_id = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.DeleteAsync("/api/social/phone-numbers/num-1?confirm=true&phone_number=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/social/connect", new { platform = "whatsapp", onboarding = "api" })).StatusCode);
        Assert.Empty(_f.Zernio.Of("PurchasePhoneNumber"));
        Assert.Empty(_f.Zernio.Released);
    }

    [Fact]
    public async Task Without_a_managed_connection_everything_degrades_and_nothing_throws()
    {
        var c = await Admin();
        _f.Zernio.Configured = false;

        foreach (var res in new[]
                 {
                     await c.PostAsJsonAsync("/api/social/connect", new { platform = "whatsapp", onboarding = "api" }),
                     await c.GetAsync("/api/social/phone-numbers"),
                     await c.PostAsJsonAsync("/api/social/phone-numbers/purchase",
                         new { country = "US", confirm = true, purchase_intent_id = "x" }),
                     await c.DeleteAsync("/api/social/phone-numbers/num-1?confirm=true&phone_number=1"),
                 })
        {
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(SocialEndpointsCode, body.GetProperty("code").GetString());
            Assert.DoesNotContain("Zernio", body.GetProperty("detail").GetString());
        }
        // the callback refuses too — and never 500s
        var back = await Browser().GetAsync("/api/social/connect/whatsapp/callback?cb=abc123&accountId=zwa-1");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Contains("zernio=error", back.Headers.Location!.ToString());

        // the not-configured stub answers the reason for every new call, and raises nothing
        var stub = new NullZernioClient();
        foreach (var reply in new[]
                 {
                     await stub.ConnectUrlAsync("whatsapp", "p", "https://x/cb", "api", "hosted"),
                     await stub.GetPhoneNumberAsync("num-1"),
                     await stub.PhoneNumberCountriesAsync(),
                     await stub.PurchasePhoneNumberAsync("p", "US", null, null, false, true, "i"),
                     await stub.ReleasePhoneNumberAsync("num-1"),
                 })
        {
            Assert.False(reply.Ok);
            Assert.Contains("not connected", reply.Error);
        }
    }

    private const string SocialEndpointsCode = "zernio_not_configured";

    // ---- the real client's query string ---------------------------------------------

    /// <summary>Records the URI of every request and answers an empty 200.</summary>
    private sealed class UriRecorder : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Uris { get; } = new();

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"authUrl\":\"https://zernio.com/connect/whatsapp?t=1\",\"state\":\"zs\"}",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task The_real_client_sends_onboarding_and_signup_only_where_they_belong()
    {
        // the HTTP client itself, not the fake: the query string is what Zernio reads
        InDb(db => { new ScopingService(db).SetSetting("zernio_api_key", "sk_test_query_check"); return 0; });
        try
        {
            var http = new UriRecorder();
            var client = new HttpZernioClient(http, _f.Services.GetRequiredService<IServiceScopeFactory>());

            var wa = await client.ConnectUrlAsync("whatsapp", "prof-1", "https://cb.example/api/social/connect/whatsapp/callback?cb=abc",
                "api", "hosted");
            Assert.True(wa.Ok);
            var waUri = http.Uris.Last();
            Assert.Contains("/connect/whatsapp?profileId=prof-1", waUri);
            Assert.Contains("onboarding=api", waUri);
            Assert.Contains("signup=hosted", waUri);
            Assert.Contains("redirect_url=https%3A%2F%2Fcb.example%2Fapi%2Fsocial%2Fconnect%2Fwhatsapp%2Fcallback%3Fcb%3Dabc", waUri);

            // coexistence without the hosted picker: onboarding only
            await client.ConnectUrlAsync("whatsapp", "prof-1", "https://cb.example/cb", "business_app", null);
            Assert.Contains("onboarding=business_app", http.Uris.Last());
            Assert.DoesNotContain("signup=", http.Uris.Last());

            // every other platform's URL is exactly what it was
            await client.ConnectUrlAsync("instagram", "prof-1", "https://cb.example/cb");
            var ig = http.Uris.Last();
            Assert.DoesNotContain("onboarding=", ig);
            Assert.DoesNotContain("signup=", ig);

            // the phone-number paths
            await client.PhoneNumberCountriesAsync();
            Assert.EndsWith("/phone-numbers/countries", http.Uris.Last());
            await client.GetPhoneNumberAsync("num-1");
            Assert.EndsWith("/phone-numbers/num-1", http.Uris.Last());
            await client.PurchasePhoneNumberAsync("prof-1", "US", null, null, false, true, "cb-1");
            Assert.EndsWith("/phone-numbers/purchase", http.Uris.Last());
            await client.ReleasePhoneNumberAsync("num-1");
            Assert.EndsWith("/phone-numbers/num-1", http.Uris.Last());
        }
        finally
        {
            InDb(db => { new ScopingService(db).SetSetting("zernio_api_key", ""); return 0; });
        }
    }
}
