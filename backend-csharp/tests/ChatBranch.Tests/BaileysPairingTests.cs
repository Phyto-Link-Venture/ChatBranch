using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data.Sidecars;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A gateway that records how pairing was started and answers with a code.</summary>
public sealed class PairingGateway : IBaileysGateway
{
    public List<string> Calls { get; } = new();
    public bool IsConfigured => true;
    public string BaseUrl => "http://gateway.test";

    private static readonly BaileysStatus Waiting = new("qr", "data:image/png;base64,QR", null, null);

    public Task<bool> SetPhoneNotificationsAsync(string a, bool keep, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<BaileysResult> SendTextAsync(string a, string to, string text, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "sent", "wamid-1"));
    public Task<BaileysResult> StartCallAsync(string a, string to, Dictionary<string, object?> d,
        CancellationToken ct = default) => Task.FromResult(new BaileysResult(false, "no calls in tests"));
    public Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "ended"));

    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) => Task.FromResult(Waiting);

    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default) => StartAsync(a, null, ct);

    public Task<BaileysStatus> StartAsync(string a, string? pairPhone, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add($"start {a} phone={pairPhone ?? "-"}");
        return Task.FromResult(pairPhone is { Length: > 0 }
            // what the gateway answers once WhatsApp hands out a code
            ? new BaileysStatus("pairing_code", null, null, null, "ABCD1234", pairPhone)
            : Waiting);
    }

    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysStatus("logged_out", null, null, null));
}

public class BaileysPairingFactory : ApiFactory
{
    public PairingGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IBaileysGateway>(Gateway));
    }
}

/// <summary>
/// Linking a QR (Baileys) number by PHONE NUMBER — WhatsApp's "Link with phone
/// number" — next to the QR scan it has always had: the number is cleaned and
/// handed to the gateway, the code it answers with reaches the dashboard, and a
/// number that cannot be one is refused before a pairing attempt is spent (WhatsApp
/// rate-limits them per number). The gateway's own half (requestPairingCode on the
/// first handshake) is backend/baileys_service/pairing.js, self-tested.
/// </summary>
public class BaileysPairingTests : IClassFixture<BaileysPairingFactory>
{
    private readonly BaileysPairingFactory _f;
    public BaileysPairingTests(BaileysPairingFactory f) => _f = f;

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private async Task<(HttpClient Admin, string Id)> ANumber(string name)
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var created = await Json(await admin.PostAsJsonAsync("/api/whatsapp/accounts",
            new { provider = "baileys", name }));
        return (admin, created.GetProperty("id").GetString()!);
    }

    /// <summary>The gateway-side session key, which the first start mints and stores.</summary>
    private string Key(string id) =>
        Platform.WithDb(_f, db => db.ChannelAccounts.Single(a => a.Id == int.Parse(id)).ExternalId);

    [Fact]
    public async Task A_number_gets_a_pairing_code_to_type_on_the_phone()
    {
        var (admin, id) = await ANumber("Counter phone");
        _f.Gateway.Calls.Clear();

        // typed the way a person types it — spacing, dashes and the plus
        var st = await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/start",
            new { phone = "+60 12-345 6789" }));

        Assert.Equal(new[] { $"start {Key(id)} phone=60123456789" }, _f.Gateway.Calls);
        Assert.Equal("pairing_code", st.GetProperty("state").GetString());
        Assert.Equal("ABCD1234", st.GetProperty("pairing_code").GetString());
        Assert.Equal("60123456789", st.GetProperty("pairing_phone").GetString());
        Assert.True(st.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task No_number_still_pairs_by_qr()
    {
        var (admin, id) = await ANumber("Scan me");
        _f.Gateway.Calls.Clear();

        // the QR dialog posts an empty body, as it always has
        var st = await Json(await admin.PostAsync($"/api/whatsapp/accounts/{id}/baileys/start",
            new StringContent("", Encoding.UTF8, "application/json")));

        Assert.Equal(new[] { $"start {Key(id)} phone=-" }, _f.Gateway.Calls);
        Assert.Equal("qr", st.GetProperty("state").GetString());
        Assert.NotNull(st.GetProperty("qr").GetString());
        Assert.Equal(JsonValueKind.Null, st.GetProperty("pairing_code").ValueKind);
    }

    [Theory]
    [InlineData("0123456789")]        // a national trunk prefix, no country code
    [InlineData("12345")]             // too short to be a line
    [InlineData("1234567890123456")]  // longer than E.164 allows
    [InlineData("not a number")]
    public async Task An_impossible_number_is_refused_before_the_gateway_is_asked(string phone)
    {
        var (admin, id) = await ANumber($"Bad {phone}");
        _f.Gateway.Calls.Clear();

        var res = await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/start", new { phone });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("country code", await res.Content.ReadAsStringAsync());
        Assert.Empty(_f.Gateway.Calls);   // no pairing attempt was spent
    }

    [Fact]
    public async Task The_code_is_never_written_to_the_activity_log()
    {
        var (admin, id) = await ANumber("Quiet log");
        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/start",
            new { phone = "+60123456789" }));

        var rows = Platform.WithDb(_f, db => db.AuditLogs
            .Where(a => a.Action == "account.baileys_start")
            .Select(a => a.Detail).ToList());
        Assert.NotEmpty(rows);
        Assert.All(rows, d => Assert.DoesNotContain("ABCD1234", d));
    }

    [Theory]
    [InlineData("+60 12-345 6789", "60123456789")]
    [InlineData("0060123456789", "60123456789")]   // 00 international prefix
    [InlineData("60123456789", "60123456789")]
    [InlineData("0123456789", "")]                 // needs a country code
    [InlineData("12345", "")]
    [InlineData("1234567890123456", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_number_is_cleaned_the_same_way_the_gateway_cleans_it(string? raw, string expected) =>
        Assert.Equal(expected, BaileysPairing.Phone(raw));
}
