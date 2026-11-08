using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatBranch.Data;

/// <summary>What one notification looks like on a device.</summary>
/// <param name="Link">Where tapping it goes — a dashboard path such as <c>/chats?thread=…</c>.</param>
/// <param name="Tag">Collapse key: a later alert about the same conversation replaces the earlier one.</param>
public record PushMessage(string Title, string Body, string Link, string Tag = "", string Category = "");

/// <summary>What happened to one device.</summary>
public enum PushOutcome
{
    Sent,
    /// <summary>The subscription is retired (404 / 410, or an unregistered token) — delete the row.</summary>
    Gone,
    /// <summary>A transient or configuration failure — the row stays, the reason is kept.</summary>
    Failed,
    /// <summary>Nothing is configured for this platform; the device was not tried.</summary>
    Skipped,
}

public record PushResult(PushOutcome Outcome, string Detail = "");

/// <summary>Sends one message to one device. Swapped for a fake in tests.</summary>
public interface IPushSender
{
    Task<PushResult> SendAsync(PushDevice device, PushMessage message, CancellationToken ct = default);
}

/// <summary>
/// The real sender: Web Push (RFC 8291 + VAPID) for browsers and the installed PWA,
/// Firebase Cloud Messaging for the phone app — FCM fronts APNs, so one credential
/// covers Android and iOS.
///
/// Nothing here throws at a caller: every failure comes back as a
/// <see cref="PushResult"/>, because a notification that cannot be delivered must
/// never cost the thing that raised it. A subscription the push service has retired
/// answers 404 / 410 and is reported <see cref="PushOutcome.Gone"/> so the row can be
/// dropped — that is the protocol's own expiry mechanism, not an error to chase.
/// </summary>
public class HttpPushSender : IPushSender
{
    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HttpPushSender> _log;

    // One Firebase access token per process, refreshed before it expires.
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static string _fcmToken = "";
    private static DateTime _fcmTokenUntil = DateTime.MinValue;

    public HttpPushSender(IHttpClientFactory http, IServiceScopeFactory scopes, ILogger<HttpPushSender> log)
    {
        _http = http;
        _scopes = scopes;
        _log = log;
    }

    public async Task<PushResult> SendAsync(PushDevice device, PushMessage message, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var scoping = scope.ServiceProvider.GetRequiredService<ScopingService>();
            return PushPlatforms.IsFirebase(device.Platform)
                ? await SendFirebaseAsync(scoping, device, message, ct)
                : await SendWebAsync(db, scoping, device, message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "push to device {Device} failed", device.Id);
            return new PushResult(PushOutcome.Failed, e.Message);
        }
    }

    // ---- Web Push (browsers, the installed PWA) ----------------------------

    private async Task<PushResult> SendWebAsync(AppDbContext db, ScopingService scoping, PushDevice device,
        PushMessage message, CancellationToken ct)
    {
        var vapid = PushSettings.GetOrCreateVapid(db, scoping);
        if (vapid == null) return new PushResult(PushOutcome.Skipped, "no VAPID keys could be stored");
        if (device.Endpoint.Length == 0 || device.P256dh.Length == 0 || device.Auth.Length == 0)
            return new PushResult(PushOutcome.Gone, "incomplete subscription");

        byte[] body;
        try
        {
            body = WebPushCrypto.Encrypt(
                JsonSerializer.SerializeToUtf8Bytes(Payload(message)),
                WebPushCrypto.FromB64Url(device.P256dh),
                WebPushCrypto.FromB64Url(device.Auth));
        }
        catch (Exception e)
        {
            // keys the browser will never accept again — nothing to retry
            return new PushResult(PushOutcome.Gone, $"subscription keys rejected: {e.Message}");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, device.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("aes128gcm");
        req.Headers.TryAddWithoutValidation("Authorization",
            WebPushCrypto.VapidHeader(device.Endpoint, vapid.Private, vapid.Public, vapid.Subject));
        req.Headers.TryAddWithoutValidation("TTL", "86400");
        // "normal" lets the service hold it for a sleeping device instead of dropping it
        req.Headers.TryAddWithoutValidation("Urgency", "normal");
        if (message.Tag.Length > 0) req.Headers.TryAddWithoutValidation("Topic", Topic(message.Tag));

        var client = _http.CreateClient("push");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var res = await client.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return new PushResult(PushOutcome.Sent);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return new PushResult(PushOutcome.Gone, $"push service says the subscription is gone ({(int)res.StatusCode})");
        var detail = await Detail(res, ct);
        return new PushResult(PushOutcome.Failed, $"push service {(int)res.StatusCode}: {detail}");
    }

    /// <summary>
    /// A Web Push <c>Topic</c> must be a short base64url token, so a tag like
    /// "thread:42" is hashed into one instead of being rejected.
    /// </summary>
    private static string Topic(string tag) =>
        WebPushCrypto.B64Url(SHA256.HashData(Encoding.UTF8.GetBytes(tag)))[..16];

    // ---- Firebase (the phone app on Android and iOS) -----------------------

    private async Task<PushResult> SendFirebaseAsync(ScopingService scoping, PushDevice device,
        PushMessage message, CancellationToken ct)
    {
        var fb = PushSettings.GetFirebase(scoping);
        if (fb == null)
            return new PushResult(PushOutcome.Skipped,
                "the phone app needs a Firebase service account (Settings → App & provider keys)");
        var token = await FirebaseTokenAsync(fb, ct);
        if (token.Length == 0) return new PushResult(PushOutcome.Failed, "Firebase refused the service account");

        var payload = new Dictionary<string, object?>
        {
            ["message"] = new Dictionary<string, object?>
            {
                ["token"] = device.Endpoint,
                ["notification"] = new { title = message.Title, body = message.Body },
                // the app reads these to open the right screen
                ["data"] = new Dictionary<string, string>
                {
                    ["link"] = message.Link,
                    ["tag"] = message.Tag,
                    ["category"] = message.Category,
                },
                ["android"] = new Dictionary<string, object?>
                {
                    ["priority"] = "high",
                    ["collapse_key"] = message.Tag.Length > 0 ? message.Tag : null,
                },
                ["apns"] = new Dictionary<string, object?>
                {
                    ["headers"] = new Dictionary<string, string> { ["apns-priority"] = "10" },
                    // APNs' own key names: thread-id is what groups alerts on iOS
                    ["payload"] = new Dictionary<string, object?>
                    {
                        ["aps"] = new Dictionary<string, object?>
                        {
                            ["sound"] = "default",
                            ["thread-id"] = message.Tag,
                        },
                    },
                },
            },
        };

        var client = _http.CreateClient("push");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://fcm.googleapis.com/v1/projects/{fb.ProjectId}/messages:send")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await client.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return new PushResult(PushOutcome.Sent);
        var detail = await Detail(res, ct);
        // FCM's own words for a token that has been uninstalled or replaced
        if (res.StatusCode is HttpStatusCode.NotFound
            || detail.Contains("UNREGISTERED", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("registration-token-not-registered", StringComparison.OrdinalIgnoreCase)
            || (res.StatusCode == HttpStatusCode.BadRequest
                && detail.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase)
                && detail.Contains("token", StringComparison.OrdinalIgnoreCase)))
            return new PushResult(PushOutcome.Gone, $"Firebase says the device token is gone: {detail}");
        return new PushResult(PushOutcome.Failed, $"Firebase {(int)res.StatusCode}: {detail}");
    }

    /// <summary>
    /// An OAuth2 access token for FCM, minted from the service account's own signed
    /// assertion (no SDK) and cached in-process until shortly before it expires.
    /// </summary>
    private async Task<string> FirebaseTokenAsync(PushSettings.Firebase fb, CancellationToken ct)
    {
        if (_fcmToken.Length > 0 && DateTime.UtcNow < _fcmTokenUntil) return _fcmToken;
        await TokenGate.WaitAsync(ct);
        try
        {
            if (_fcmToken.Length > 0 && DateTime.UtcNow < _fcmTokenUntil) return _fcmToken;
            var now = DateTimeOffset.UtcNow;
            var header = WebPushCrypto.B64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var claims = WebPushCrypto.B64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["iss"] = fb.ClientEmail,
                ["scope"] = "https://www.googleapis.com/auth/firebase.messaging",
                ["aud"] = "https://oauth2.googleapis.com/token",
                ["iat"] = now.ToUnixTimeSeconds(),
                ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(fb.PrivateKeyPem);
            var sig = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var assertion = $"{header}.{claims}.{WebPushCrypto.B64Url(sig)}";

            var client = _http.CreateClient("push");
            client.Timeout = TimeSpan.FromSeconds(15);
            using var res = await client.PostAsync("https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                }), ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("Firebase token request failed: {Status}", (int)res.StatusCode);
                return "";
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() ?? "" : "";
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s)
                ? s : 3600;
            if (token.Length > 0)
            {
                _fcmToken = token;
                _fcmTokenUntil = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 300));
            }
            return token;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Firebase token could not be minted");
            return "";
        }
        finally
        {
            TokenGate.Release();
        }
    }

    /// <summary>What the service worker and the app receive.</summary>
    private static Dictionary<string, string> Payload(PushMessage m) => new()
    {
        ["title"] = m.Title,
        ["body"] = m.Body,
        ["link"] = m.Link,
        ["tag"] = m.Tag,
        ["category"] = m.Category,
    };

    private static async Task<string> Detail(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            return text.Length > 300 ? text[..300] : text;
        }
        catch
        {
            return res.ReasonPhrase ?? "";
        }
    }
}

/// <summary>Zero-config fallback: nothing is sent and the reason says so.</summary>
public class SandboxPushSender : IPushSender
{
    public Task<PushResult> SendAsync(PushDevice device, PushMessage message, CancellationToken ct = default) =>
        Task.FromResult(new PushResult(PushOutcome.Skipped, "push is not configured on this server"));
}

/// <summary>
/// Turning the notifications a person has into pushes on their devices.
/// <see cref="NotificationService"/> stays the only writer of what a person is told;
/// this only carries an existing row to their devices, so every mute, per-user switch
/// and "only my conversations" rule has already been applied by the time it is asked.
/// </summary>
public static class PushDelivery
{
    /// <summary>
    /// Push one notification to every device of its recipient. Devices the push
    /// service has retired are deleted; a device that keeps failing is parked after
    /// <see cref="PushDevice.MaxFailures"/>. Returns how many were delivered. Never throws.
    /// </summary>
    public static async Task<int> SendAsync(AppDbContext db, IPushSender sender, Notification notification,
        PushMessage message, CancellationToken ct = default)
    {
        try
        {
            var devices = await db.PushDevices
                .Where(d => d.UserId == notification.UserId && d.Failures < PushDevice.MaxFailures)
                .ToListAsync(ct);
            if (devices.Count == 0) return 0;

            var sent = 0;
            var gone = new List<PushDevice>();
            foreach (var device in devices)
            {
                var result = await sender.SendAsync(device, message, ct);
                switch (result.Outcome)
                {
                    case PushOutcome.Sent:
                        device.LastPushAt = DateTime.UtcNow;
                        device.Failures = 0;
                        device.LastError = "";
                        sent++;
                        break;
                    case PushOutcome.Gone:
                        gone.Add(device);
                        break;
                    case PushOutcome.Failed:
                        device.Failures++;
                        device.LastError = Cap(result.Detail);
                        break;
                    case PushOutcome.Skipped:
                        device.LastError = Cap(result.Detail);
                        break;
                }
            }
            if (gone.Count > 0) db.PushDevices.RemoveRange(gone);
            await db.SaveChangesAsync(ct);
            return sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return 0;   // a notification is already stored; delivery is best effort
        }
    }

    /// <summary>
    /// The device-facing shape of a notification row: its own words, and the tag that
    /// makes a second alert about the same conversation replace the first rather than
    /// stack up — the same collapse the bell does with <c>RefKey</c>.
    /// </summary>
    public static PushMessage From(Notification n) => new(
        n.Title.Length > 0 ? n.Title : "ChatBranch",
        n.Body,
        n.Link,
        n.RefKey,
        NotificationCategories.Normalize(n.Category, n.Kind));

    private static string Cap(string s) => s.Length > 300 ? s[..300] : s;
}
