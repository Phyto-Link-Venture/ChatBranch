using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Sidecars;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A Baileys gateway that records what it was told, in order.</summary>
public sealed class SettingsGateway : IBaileysGateway
{
    public List<string> Calls { get; } = new();
    public bool IsConfigured => true;
    public string BaseUrl => "http://gateway.test";

    public Task<bool> SetPhoneNotificationsAsync(string a, bool keep, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add($"settings {a} {keep}");
        return Task.FromResult(true);
    }

    public Task<BaileysResult> SendTextAsync(string a, string to, string text, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "sent", "wamid-1"));
    public Task<BaileysResult> StartCallAsync(string a, string to, Dictionary<string, object?> d,
        CancellationToken ct = default) => Task.FromResult(new BaileysResult(false, "no calls in tests"));
    public Task<BaileysResult> EndCallAsync(string a, CancellationToken ct = default) =>
        Task.FromResult(new BaileysResult(true, "ended"));
    private static readonly BaileysStatus Connected = new("connected", null, "me", null);
    public Task<BaileysStatus> StatusAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
    public Task<BaileysStatus> StartAsync(string a, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add($"start {a}");
        return Task.FromResult(Connected);
    }
    public Task<BaileysStatus> LogoutAsync(string a, CancellationToken ct = default) => Task.FromResult(Connected);
}

public class PhoneNotificationsFactory : ApiFactory
{
    public SettingsGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IBaileysGateway>(Gateway));
    }
}

/// <summary>
/// "Keep phone notifications" on a QR (Baileys) number: stored on the account,
/// returned to the dashboard, handed to the gateway before pairing and pushed to a
/// paired session when it changes; other providers ignore it. The gateway's own
/// half (online only around sends) is backend/baileys_service/presence.js, self-tested.
/// </summary>
public class PhoneNotificationsTests : IClassFixture<PhoneNotificationsFactory>
{
    private readonly PhoneNotificationsFactory _f;
    public PhoneNotificationsTests(PhoneNotificationsFactory f) => _f = f;

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task The_setting_is_stored_handed_over_on_pairing_and_pushed_when_it_changes()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var created = await Json(await admin.PostAsJsonAsync("/api/whatsapp/accounts",
            new { provider = "baileys", name = "Shop phone", keep_phone_notifications = true }));
        Assert.True(created.GetProperty("keep_phone_notifications").GetBoolean());
        var id = created.GetProperty("id").GetString()!;

        // pairing: the gateway learns the setting BEFORE the session starts
        _f.Gateway.Calls.Clear();
        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}/baileys/start", new { }));
        var key = Platform.WithDb(_f, db => db.ChannelAccounts.Single(a => a.Id == int.Parse(id)).ExternalId);
        Assert.Equal(new[] { $"settings {key} True", $"start {key}" }, _f.Gateway.Calls);

        // switching it off on a paired number reaches the live session at once
        _f.Gateway.Calls.Clear();
        var off = await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}",
            new { keep_phone_notifications = false }));
        Assert.False(off.GetProperty("keep_phone_notifications").GetBoolean());
        Assert.Equal(new[] { $"settings {key} False" }, _f.Gateway.Calls);
        Assert.False(Platform.WithDb(_f, db =>
            BaileysSettings.KeepPhone(db.ChannelAccounts.Single(a => a.Id == int.Parse(id)))));

        // an unrelated edit pushes nothing
        _f.Gateway.Calls.Clear();
        await Json(await admin.PostAsJsonAsync($"/api/whatsapp/accounts/{id}", new { name = "Shop phone 2" }));
        Assert.Empty(_f.Gateway.Calls);
    }

    [Fact]
    public async Task Other_providers_ignore_it()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var cloud = await Json(await admin.PostAsJsonAsync("/api/whatsapp/accounts", new
        {
            provider = "cloud", name = "Cloud number", phone_number_id = $"pn-{Guid.NewGuid():N}"[..14],
            access_token = "token-123", keep_phone_notifications = true,
        }));
        Assert.False(cloud.GetProperty("keep_phone_notifications").GetBoolean());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void The_stored_value_reads_back(string? stored, bool expected)
    {
        var a = new ChannelAccount { Provider = Providers.Baileys };
        if (stored != null) a.Config = new Dictionary<string, object?> { [BaileysSettings.KeepPhoneNotifications] = JsonDocument.Parse(stored).RootElement };
        Assert.Equal(expected, BaileysSettings.KeepPhone(a));
    }
}
