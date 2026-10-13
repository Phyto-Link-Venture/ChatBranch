using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChatBranch.Domain;

namespace ChatBranch.Data.Integrations;

/// <summary>
/// Zoom meetings for calendar bookings (a calendar skill with <c>meeting: "zoom"</c>):
/// one meeting per booking, made when the booking is made and deleted when it is
/// cancelled. The connection is a Zoom <b>Server-to-Server OAuth</b> app — an
/// integration row (provider <c>zoom</c>) with <c>account_id</c> + <c>client_id</c>
/// (config) and <c>client_secret</c> (credentials); the access token
/// (<c>grant_type=account_credentials</c>, an hour) is cached in-process per row and
/// credentials. Built to Zoom's documented shapes; nothing here throws, logs a token
/// or returns a whole provider body.
/// </summary>
public class ZoomClient
{
    public const string Provider = "zoom";
    public const string TokenUrl = "https://zoom.us/oauth/token";
    public const string ApiBase = "https://api.zoom.us/v2";

    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;

    /// <summary>(row id + credential hash) → token and when to stop using it.</summary>
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Until)> Tokens = new();

    public ZoomClient(AppDbContext db, IHttpClientFactory http)
    {
        _db = db;
        _http = http;
    }

    public sealed record MeetingResult(bool Ok, bool NotConnected, string Error, string Id, string JoinUrl);

    public sealed record ProbeResult(bool Ok, bool NotConnected, string Error, string Account);

    /// <summary>The three values a Server-to-Server OAuth app needs ("" when missing).</summary>
    public static (string AccountId, string ClientId, string ClientSecret) Creds(Integration row)
    {
        string Get(string key)
        {
            if (row.Credentials.TryGetValue(key, out var c) && (c?.ToString() ?? "").Trim() is { Length: > 0 } cv) return cv;
            return row.Config.TryGetValue(key, out var v) ? (v?.ToString() ?? "").Trim() : "";
        }
        return (Get("account_id"), Get("client_id"), Get("client_secret"));
    }

    public static bool Configured(Integration row)
    {
        var (a, i, s) = Creds(row);
        return a.Length > 0 && i.Length > 0 && s.Length > 0;
    }

    /// <summary>An access token, or "" + why not.</summary>
    public async Task<(string Token, string Error, bool NotConnected)> TokenAsync(Integration row, CancellationToken ct = default)
    {
        var (account, client, secret) = Creds(row);
        if (account.Length == 0 || client.Length == 0 || secret.Length == 0)
            return ("", "Zoom is not connected (the Account ID, Client ID and Client secret are all needed).", true);
        var key = $"{row.Id}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{account}\n{client}\n{secret}")))[..16]}";
        if (Tokens.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow) return (cached.Token, "", false);

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client}:{secret}"));
        var r = await ProviderHttp.SendAsync(_http, HttpMethod.Post,
            $"{TokenUrl}?grant_type=account_credentials&account_id={Uri.EscapeDataString(account)}", "",
            formBody: new Dictionary<string, string>(),
            headers: new Dictionary<string, string> { ["Authorization"] = $"Basic {basic}" }, ct: ct);
        if (!r.Ok)
        {
            Record(row, false, $"Zoom sign-in failed: {r.Body}");
            return ("", $"Zoom sign-in failed ({r.Body}).", r.Status is 400 or 401 or 403);
        }
        var token = ProviderHttp.JsonField(r.Body, "access_token");
        if (token.Length == 0) return ("", "Zoom answered without an access token.", false);
        var seconds = int.TryParse(ProviderHttp.JsonField(r.Body, "expires_in"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var s) ? s : 3600;
        // a minute's margin: never hand out a token about to expire mid-call
        Tokens[key] = (token, DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60)));
        return (token, "", false);
    }

    /// <summary>A scheduled meeting (type 2) at <paramref name="startUtc"/> for <paramref name="minutes"/>.</summary>
    public async Task<MeetingResult> CreateMeetingAsync(Integration row, string topic, DateTime startUtc, int minutes,
        string? timeZone, string agenda, CancellationToken ct = default)
    {
        var (token, error, notConnected) = await TokenAsync(row, ct);
        if (token.Length == 0) return new MeetingResult(false, notConnected, error, "", "");
        var body = new Dictionary<string, object?>
        {
            ["topic"] = Cap(topic.Trim().Length > 0 ? topic.Trim() : "Appointment", 200),
            ["type"] = 2,
            ["start_time"] = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc)
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["duration"] = Math.Max(1, minutes),
            ["agenda"] = Cap(agenda, 1900),
            ["settings"] = new Dictionary<string, object?>
            {
                // the host lets the customer in — nobody else wanders into the room
                ["join_before_host"] = false,
                ["waiting_room"] = true,
            },
        };
        if (!string.IsNullOrWhiteSpace(timeZone)) body["timezone"] = timeZone;
        var r = await ProviderHttp.SendAsync(_http, HttpMethod.Post, $"{ApiBase}/users/me/meetings", token,
            jsonBody: body, ct: ct);
        Record(row, r.Ok, r.Ok ? "" : $"Zoom: {r.Body}");
        if (!r.Ok) return new MeetingResult(false, r.Status == 401, $"Zoom: {r.Body}", "", "");
        var join = ProviderHttp.JsonField(r.Body, "join_url");
        var id = ProviderHttp.JsonField(r.Body, "id");
        return join.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new MeetingResult(true, false, "", id, join)
            : new MeetingResult(false, false, "Zoom answered without a join link.", id, "");
    }

    /// <summary>Delete a meeting. One that is already gone (404) counts as deleted.</summary>
    public async Task<MeetingResult> DeleteMeetingAsync(Integration row, string meetingId, CancellationToken ct = default)
    {
        if (meetingId.Trim().Length == 0) return new MeetingResult(true, false, "", meetingId, "");
        var (token, error, notConnected) = await TokenAsync(row, ct);
        if (token.Length == 0) return new MeetingResult(false, notConnected, error, meetingId, "");
        var r = await ProviderHttp.SendAsync(_http, HttpMethod.Delete,
            $"{ApiBase}/meetings/{Uri.EscapeDataString(meetingId.Trim())}", token, ct: ct);
        return r.Ok || r.Status == 404
            ? new MeetingResult(true, false, "", meetingId, "")
            : new MeetingResult(false, r.Status == 401, $"Zoom: {r.Body}", meetingId, "");
    }

    /// <summary>The connection test: sign in and read the account's own user.</summary>
    public async Task<ProbeResult> ProbeAsync(Integration row, CancellationToken ct = default)
    {
        var (token, error, notConnected) = await TokenAsync(row, ct);
        if (token.Length == 0) return new ProbeResult(false, notConnected, error, "");
        var r = await ProviderHttp.SendAsync(_http, HttpMethod.Get, $"{ApiBase}/users/me", token, ct: ct);
        Record(row, r.Ok, r.Ok ? "" : $"Zoom: {r.Body}");
        return r.Ok
            ? new ProbeResult(true, false, "", ProviderHttp.JsonField(r.Body, "email"))
            : new ProbeResult(false, r.Status == 401, $"Zoom: {r.Body}", "");
    }

    /// <summary>The connection's health on its row, so a dead app shows on the Integrations page.</summary>
    private void Record(Integration row, bool ok, string detail)
    {
        try
        {
            var tracked = _db.Integrations.FirstOrDefault(i => i.Id == row.Id);
            if (tracked == null) return;
            var status = ok ? "connected" : "error";
            if (tracked.Status == status && tracked.StatusDetail == Cap(detail, 300)) return;
            tracked.Status = status;
            tracked.StatusDetail = Cap(detail, 300);
            tracked.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
        catch { /* health is a side effect */ }
    }

    /// <summary>Tests only: forget cached tokens.</summary>
    public static void ResetTokens() => Tokens.Clear();

    private static string Cap(string? s, int n) => (s ?? "").Length <= n ? s ?? "" : s![..n];
}
