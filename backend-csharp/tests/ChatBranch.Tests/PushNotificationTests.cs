using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Workers;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A sender that records what it was asked to deliver, and can play dead.</summary>
public sealed class FakePushSender : IPushSender
{
    public List<(PushDevice Device, PushMessage Message)> Sent { get; } = new();
    /// <summary>Endpoints the "push service" has retired (404 / 410).</summary>
    public HashSet<string> Gone { get; } = new();
    /// <summary>Endpoints whose service is having a bad day.</summary>
    public HashSet<string> Failing { get; } = new();

    public Task<PushResult> SendAsync(PushDevice device, PushMessage message, CancellationToken ct = default)
    {
        if (Gone.Contains(device.Endpoint)) return Task.FromResult(new PushResult(PushOutcome.Gone, "gone"));
        if (Failing.Contains(device.Endpoint))
            return Task.FromResult(new PushResult(PushOutcome.Failed, "push service 500"));
        lock (Sent) Sent.Add((device, message));
        return Task.FromResult(new PushResult(PushOutcome.Sent));
    }
}

public class PushFactory : ApiFactory
{
    public FakePushSender Sender { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IPushSender>(Sender));
    }
}

/// <summary>
/// Notifications on a device, when ChatBranch is not open: a person registers the
/// browser or the phone app, and the worker carries the notifications they are
/// already given (NotificationService stays the only writer) to those devices.
/// The cryptography is checked against RFC 8291's own test vector.
/// </summary>
public class PushNotificationTests : IClassFixture<PushFactory>
{
    private readonly PushFactory _f;
    public PushNotificationTests(PushFactory f) => _f = f;

    private T Db<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _f.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>A browser subscription's shape: an https endpoint and the two keys.</summary>
    private static object WebDevice(string endpoint, string label = "Chrome on the laptop") => new
    {
        platform = "web",
        endpoint,
        p256dh = WebPushCrypto.B64Url(NewUaPublic()),
        auth = WebPushCrypto.B64Url(RandomNumberGenerator.GetBytes(16)),
        label,
    };

    private static byte[] NewUaPublic()
    {
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ec.PublicKey.ExportParameters();
        var raw = new byte[65];
        raw[0] = 0x04;
        q.Q.X!.CopyTo(raw, 1);
        q.Q.Y!.CopyTo(raw, 33);
        return raw;
    }

    /// <summary>
    /// Forget every device of a login. The tests share one host (and therefore one
    /// seeded "agent"), so a test that counts deliveries must start from nothing —
    /// otherwise it also pushes to the devices its neighbours registered.
    /// </summary>
    private void OnlyDevicesOf(string username)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var uid = db.Users.Single(u => u.Username == username).Id;
        db.PushDevices.RemoveRange(db.PushDevices.Where(d => d.UserId == uid));
        db.SaveChanges();
    }

    private async Task<int> Tick()
    {
        using var scope = _f.Services.CreateScope();
        var (_, delivered) = await PushWorker.RunAsync(scope.ServiceProvider);
        return delivered;
    }

    [Fact]
    public async Task Web_push_matches_the_rfc_8291_test_vector()
    {
        // RFC 8291 §5 — the only way to know the encryption is right without a browser
        const string plaintext = "When I grow up, I want to be a watermelon";
        var uaPublic = WebPushCrypto.FromB64Url(
            "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        var authSecret = WebPushCrypto.FromB64Url("BTBZMqHH6r4Tts7J_aSIgg");
        var salt = WebPushCrypto.FromB64Url("DGv6ra1nlYgDCS1FRnbzlw");
        using var sender = WebPushCrypto.ImportKeyPair(
            WebPushCrypto.FromB64Url("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            WebPushCrypto.FromB64Url(
                "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8"));

        var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(plaintext), uaPublic, authSecret, salt, sender);

        // RFC 8291 §5's published body, byte for byte
        Assert.Equal(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS"
            + "6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Q"
            + "ulcy4a-fN",
            WebPushCrypto.B64Url(body));
    }

    [Fact]
    public void The_vapid_header_is_a_signed_jwt_for_the_endpoints_own_origin()
    {
        var (priv, pub) = WebPushCrypto.NewVapidKeys();
        var header = WebPushCrypto.VapidHeader("https://fcm.googleapis.com/fcm/send/abc123", priv, pub,
            "mailto:ops@example.com");

        Assert.StartsWith("vapid t=", header);
        Assert.Contains($"k={WebPushCrypto.B64Url(pub)}", header);
        var jwt = header["vapid t=".Length..].Split(',')[0];
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        using var claims = JsonDocument.Parse(WebPushCrypto.FromB64Url(parts[1]));
        // the audience is the push service's origin alone — never the full URL
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:ops@example.com", claims.RootElement.GetProperty("sub").GetString());
        var exp = DateTimeOffset.FromUnixTimeSeconds(claims.RootElement.GetProperty("exp").GetInt64());
        Assert.InRange(exp, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24));   // RFC 8292's limit
    }

    [Fact]
    public async Task A_browser_registers_itself_and_the_keys_are_required()
    {
        var agent = await Platform.As(_f, "agent", "agent123");
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";

        var created = await Json(await agent.PostAsJsonAsync("/api/push/devices", WebDevice(endpoint)));
        Assert.Equal("web", created.GetProperty("platform").GetString());
        var id = created.GetProperty("id").GetInt32();

        // the same browser registering again refreshes the row, never adds a second
        await Json(await agent.PostAsJsonAsync("/api/push/devices", WebDevice(endpoint, "Chrome, renamed")));
        var mine = await Json(await agent.GetAsync("/api/push/devices"));
        Assert.Single(mine.GetProperty("devices").EnumerateArray(),
            d => d.GetProperty("id").GetInt32() == id && d.GetProperty("label").GetString() == "Chrome, renamed");

        // Web Push cannot deliver anything without the browser's own keys
        var noKeys = await agent.PostAsJsonAsync("/api/push/devices",
            new { platform = "web", endpoint = $"https://push.example.com/{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.BadRequest, noKeys.StatusCode);
        Assert.Contains("p256dh", await noKeys.Content.ReadAsStringAsync());

        // and the endpoint must be the service's https URL
        var notHttps = await agent.PostAsJsonAsync("/api/push/devices",
            new { platform = "web", endpoint = "http://push.example.com/x", p256dh = "a", auth = "b" });
        Assert.Equal(HttpStatusCode.BadRequest, notHttps.StatusCode);

        // the phone app registers a bare Firebase token — no keys involved
        var app = await Json(await agent.PostAsJsonAsync("/api/push/devices",
            new { platform = "android", endpoint = $"fcm-token-{Guid.NewGuid():N}", label = "Pixel" }));
        Assert.Equal("android", app.GetProperty("platform").GetString());
    }

    [Fact]
    public async Task A_device_is_its_owners_alone()
    {
        var agent = await Platform.As(_f, "agent", "agent123");
        var admin = await Platform.As(_f, "admin", "admin123");
        var created = await Json(await agent.PostAsJsonAsync("/api/push/devices",
            WebDevice($"https://push.example.com/{Guid.NewGuid():N}")));
        var id = created.GetProperty("id").GetInt32();

        // another login never sees it, and cannot remove it — a 404, not a 403
        var theirs = await Json(await admin.GetAsync("/api/push/devices"));
        Assert.DoesNotContain(theirs.GetProperty("devices").EnumerateArray(),
            d => d.GetProperty("id").GetInt32() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/push/devices/{id}")).StatusCode);

        // its owner can
        (await agent.DeleteAsync($"/api/push/devices/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await agent.DeleteAsync($"/api/push/devices/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_notification_reaches_the_persons_devices_once()
    {
        var agent = await Platform.As(_f, "agent", "agent123");
        OnlyDevicesOf("agent");
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        await Json(await agent.PostAsJsonAsync("/api/push/devices", WebDevice(endpoint)));
        var uid = Db(db => db.Users.Single(u => u.Username == "agent").Id);
        _f.Sender.Sent.Clear();

        // the bell already has it — the worker only carries it further
        Db(db =>
        {
            db.Notifications.Add(new Notification
            {
                UserId = uid, Kind = "message", Category = NotificationCategories.Messages,
                Title = "Mei Ling", Body = "Is the blue one in stock?", Link = "/chats?thread=whatsapp:60123",
                RefKey = "thread:4242", CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });

        Assert.Equal(1, await Tick());
        var (device, message) = Assert.Single(_f.Sender.Sent);
        Assert.Equal(endpoint, device.Endpoint);
        Assert.Equal("Mei Ling", message.Title);
        Assert.Equal("/chats?thread=whatsapp:60123", message.Link);
        // the tag is the bell's own collapse key, so a second alert about the same
        // conversation replaces the first instead of stacking up
        Assert.Equal("thread:4242", message.Tag);

        // and it is not pushed again on the next pass
        _f.Sender.Sent.Clear();
        Assert.Equal(0, await Tick());
        Assert.Empty(_f.Sender.Sent);
        Assert.NotNull(Db(db => db.PushDevices.Single(d => d.Endpoint == endpoint).LastPushAt));
    }

    [Fact]
    public async Task A_notification_already_read___or_stale___is_never_pushed()
    {
        var agent = await Platform.As(_f, "agent", "agent123");
        OnlyDevicesOf("agent");
        await Json(await agent.PostAsJsonAsync("/api/push/devices",
            WebDevice($"https://push.example.com/{Guid.NewGuid():N}")));
        var uid = Db(db => db.Users.Single(u => u.Username == "agent").Id);
        await Tick();   // move the cursor to now
        _f.Sender.Sent.Clear();

        Db(db =>
        {
            // someone has already seen this one in the dashboard
            db.Notifications.Add(new Notification
            {
                UserId = uid, Kind = "message", Title = "Read already", Read = true,
                CreatedAt = DateTime.UtcNow,
            });
            // and this one is yesterday's news — a server that was down must not
            // come back and buzz everyone
            db.Notifications.Add(new Notification
            {
                UserId = uid, Kind = "message", Title = "Yesterday",
                CreatedAt = DateTime.UtcNow.AddHours(-20),
            });
            return db.SaveChanges();
        });

        Assert.Equal(0, await Tick());
        Assert.Empty(_f.Sender.Sent);
    }

    [Fact]
    public async Task A_retired_subscription_is_forgotten_and_a_failing_one_is_parked()
    {
        var agent = await Platform.As(_f, "agent", "agent123");
        OnlyDevicesOf("agent");
        var gone = $"https://push.example.com/gone-{Guid.NewGuid():N}";
        var sick = $"https://push.example.com/sick-{Guid.NewGuid():N}";
        await Json(await agent.PostAsJsonAsync("/api/push/devices", WebDevice(gone, "Old phone")));
        await Json(await agent.PostAsJsonAsync("/api/push/devices", WebDevice(sick, "Flaky laptop")));
        _f.Sender.Gone.Add(gone);
        _f.Sender.Failing.Add(sick);
        var uid = Db(db => db.Users.Single(u => u.Username == "agent").Id);

        Db(db =>
        {
            db.Notifications.Add(new Notification
            {
                UserId = uid, Kind = "message", Title = "Anyone there?", CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });
        Assert.Equal(0, await Tick());

        // the protocol's own expiry: the row goes, nothing to chase
        Assert.False(Db(db => db.PushDevices.Any(d => d.Endpoint == gone)));
        // a service having a bad day keeps its row, with the reason on it
        var flaky = Db(db => db.PushDevices.Single(d => d.Endpoint == sick));
        Assert.Equal(1, flaky.Failures);
        Assert.Contains("500", flaky.LastError);

        // after enough refusals it is left alone until it re-registers
        Db(db =>
        {
            var row = db.PushDevices.Single(d => d.Endpoint == sick);
            row.Failures = PushDevice.MaxFailures;
            return db.SaveChanges();
        });
        _f.Sender.Sent.Clear();
        _f.Sender.Failing.Remove(sick);
        Db(db =>
        {
            db.Notifications.Add(new Notification
            {
                UserId = uid, Kind = "message", Title = "Still there?", CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });
        Assert.Equal(0, await Tick());
        Assert.Empty(_f.Sender.Sent);
    }

    [Fact]
    public async Task Browsers_need_no_account_anywhere___the_server_makes_its_own_vapid_pair()
    {
        var agent = await Platform.As(_f, "agent", "agent123");
        var config = await Json(await agent.GetAsync("/api/push/config"));

        Assert.True(config.GetProperty("web").GetBoolean());
        var key = config.GetProperty("vapid_public_key").GetString()!;
        // an uncompressed P-256 point is what a browser's applicationServerKey must be
        Assert.Equal(65, WebPushCrypto.FromB64Url(key).Length);
        Assert.Equal(0x04, WebPushCrypto.FromB64Url(key)[0]);
        // the phone app is the one that needs Firebase, and says so honestly
        Assert.False(config.GetProperty("app").GetBoolean());

        // the pair is kept, so subscriptions survive a restart
        var again = await Json(await agent.GetAsync("/api/push/config"));
        Assert.Equal(key, again.GetProperty("vapid_public_key").GetString());
        Assert.Equal(key, Db(db => db.Settings
            .Single(s => s.Key == PushSettings.VapidPublicKey && s.EntityId == null).Value));
    }

    [Fact]
    public async Task Running_a_pass_by_hand_needs_the_notifications_capability()
    {
        var agent = await Platform.As(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsync("/api/push/tick", null)).StatusCode);

        var superadmin = await Platform.As(_f, "superadmin", "super123");
        (await superadmin.PostAsync("/api/push/tick", null)).EnsureSuccessStatusCode();
    }
}
