using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatBranch.Data.Contacts;

/// <summary>One person from the caller's Google Contacts.</summary>
public record GooglePerson(string Name, string GivenName, string FamilyName, List<string> Phones,
    List<string> Emails, string Organization, string Note, string Birthday);

/// <summary>
/// Google People API — the caller's own contact book
/// (<c>people/me/connections</c>, paged). Behind an interface so tests swap
/// in a fake; never logs the token or a response body.
/// </summary>
public interface IGooglePeopleClient
{
    Task<(List<GooglePerson> People, string? Error)> ConnectionsAsync(string accessToken, int max,
        CancellationToken ct = default);
}

public sealed class GooglePeopleClient : IGooglePeopleClient
{
    public const string Scope = "https://www.googleapis.com/auth/contacts.readonly";
    public const string BaseUrl = "https://people.googleapis.com/v1/people/me/connections";
    private const string Fields = "names,emailAddresses,phoneNumbers,organizations,biographies,birthdays";

    private readonly IHttpClientFactory _http;
    public GooglePeopleClient(IHttpClientFactory http) => _http = http;

    public async Task<(List<GooglePerson> People, string? Error)> ConnectionsAsync(string accessToken, int max,
        CancellationToken ct = default)
    {
        var people = new List<GooglePerson>();
        string? page = null;
        try
        {
            var client = _http.CreateClient("google-people");
            client.Timeout = TimeSpan.FromSeconds(30);
            for (var guard = 0; guard < 100 && people.Count < max; guard++)
            {
                var url = $"{BaseUrl}?personFields={Fields}&pageSize=1000&sortOrder=FIRST_NAME_ASCENDING"
                          + (page != null ? $"&pageToken={Uri.EscapeDataString(page)}" : "");
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    return (people, (int)resp.StatusCode is 401 or 403
                        ? $"Google refused the request ({(int)resp.StatusCode}) — reconnect Google Contacts and allow access to your contacts."
                        : $"Google Contacts answered {(int)resp.StatusCode}.");
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                people.AddRange(ParsePage(doc.RootElement));
                page = doc.RootElement.TryGetProperty("nextPageToken", out var np) && np.ValueKind == JsonValueKind.String
                    ? np.GetString() : null;
                if (string.IsNullOrEmpty(page)) break;
            }
        }
        catch (HttpRequestException) { return (people, "Google Contacts is unreachable right now."); }
        catch (TaskCanceledException) { return (people, "Google Contacts timed out."); }
        catch (JsonException) { return (people, "Google Contacts answered something unreadable."); }
        return (people.Take(max).ToList(), null);
    }

    /// <summary>One <c>connections</c> page → people (public so tests can feed a captured page).</summary>
    public static List<GooglePerson> ParsePage(JsonElement root)
    {
        var outv = new List<GooglePerson>();
        if (!root.TryGetProperty("connections", out var conns) || conns.ValueKind != JsonValueKind.Array) return outv;
        foreach (var p in conns.EnumerateArray())
        {
            var name = Primary(p, "names");
            var phones = All(p, "phoneNumbers")
                .Select(x => Str(x, "canonicalForm") is { Length: > 0 } c ? c : Str(x, "value"))
                .Where(x => x.Length > 0).Distinct().ToList();
            var emails = All(p, "emailAddresses").Select(x => Str(x, "value")).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var org = Primary(p, "organizations") is { } o ? Str(o, "name") : "";
            var note = Primary(p, "biographies") is { } b ? Str(b, "value") : "";
            var bday = "";
            if (Primary(p, "birthdays") is { } bd && bd.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                int Num(string k) => d.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : 0;
                var (y, m, day) = (Num("year"), Num("month"), Num("day"));
                if (m > 0 && day > 0) bday = y > 0 ? $"{y:0000}-{m:00}-{day:00}" : $"--{m:00}-{day:00}";
            }
            var display = name is { } n ? Str(n, "displayName") : "";
            var given = name is { } g ? Str(g, "givenName") : "";
            var family = name is { } f ? Str(f, "familyName") : "";
            if (display.Length == 0 && phones.Count == 0 && emails.Count == 0) continue;
            outv.Add(new GooglePerson(display, given, family, phones, emails, org, note, bday));
        }
        return outv;
    }

    private static IEnumerable<JsonElement> All(JsonElement p, string key) =>
        p.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().OrderByDescending(IsPrimary).ToList()
            : Enumerable.Empty<JsonElement>();

    private static JsonElement? Primary(JsonElement p, string key)
    {
        foreach (var x in All(p, key)) return x;
        return null;
    }

    private static bool IsPrimary(JsonElement x) =>
        x.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
        && m.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim() : "";
}
