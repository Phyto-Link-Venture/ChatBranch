using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Data.Integrations;

/// <summary>
/// The flow-builder integration actions, executed for real. Ported from
/// <c>backend/integrations/actions.py</c>; the C# dispatcher used to answer
/// every call with "not yet implemented in the C# port", so a connected
/// calendar still booked nothing.
///
/// Two invariants hold for every action:
/// <list type="bullet">
/// <item><b>A missing connection SKIPS, it does not fail.</b> `{ok:false,
/// skipped:true}` lets the flow run continue and logs a clear reason — an
/// unconfigured integration is the template's shipping state.</item>
/// <item><b>A provider error is reported honestly</b> (`ok:false`, no skip) and
/// recorded on the row's health, but never throws into the run.</item>
/// </list>
/// </summary>
public class IntegrationActions
{
    private readonly AppDbContext _db;
    private readonly IntegrationResolver _resolver;
    private readonly GoogleOAuth _oauth;
    private readonly IHttpClientFactory _http;

    public IntegrationActions(AppDbContext db, IntegrationResolver resolver, GoogleOAuth oauth,
        IHttpClientFactory http)
    {
        _db = db;
        _resolver = resolver;
        _oauth = oauth;
        _http = http;
    }

    /// <summary>Action label → provider (actions.ACTIONS, verbatim).</summary>
    public static readonly Dictionary<string, string> Providers = new()
    {
        ["Create calendar event"] = "google_calendar",
        ["Find free slot"] = "google_calendar",
        ["Upload file to Drive"] = "google_drive",
        ["Append row to Sheet"] = "google_sheets",
        ["Send via Gmail"] = "gmail",
        ["Update order status"] = "shopify",
        ["Create Stripe payment link"] = "stripe",
    };

    public record Result(bool Ok, bool Skipped, string Detail, Dictionary<string, object?>? Data = null);

    private static Result Skip(string detail) => new(false, true, detail);
    private static Result Fail(string detail) => new(false, false, detail);
    private static Result Done(string detail, Dictionary<string, object?>? data = null) =>
        new(true, false, detail, data);

    /// <summary>Run one action by its flow-builder label.</summary>
    public async Task<Result> RunAsync(string action, Contact? contact, Dictionary<string, string> config,
        string text, int? entityId, int? userId, CancellationToken ct = default)
    {
        if (!Providers.TryGetValue(action, out var provider))
            return Skip($"Unknown integration action '{action}' — step skipped.");
        var row = _resolver.Active(provider, entityId, userId);
        if (row == null) return Skip($"{Label(provider)} is not connected — step skipped.");

        try
        {
            return action switch
            {
                "Create calendar event" => await CreateCalendarEventAsync(row, contact, config, text, ct),
                "Find free slot" => await FindFreeSlotAsync(row, config, ct),
                "Upload file to Drive" => await UploadDriveFileAsync(row, config, text, ct),
                "Append row to Sheet" => await AppendSheetRowAsync(row, contact, config, text, ct),
                "Send via Gmail" => await SendGmailAsync(row, contact, config, text, ct),
                "Update order status" => await UpdateOrderStatusAsync(row, contact, config, ct),
                "Create Stripe payment link" => await CreatePaymentLinkAsync(row, contact, config, ct),
                _ => Skip($"Action '{action}' has no implementation — step skipped."),
            };
        }
        catch (Exception e)
        {
            // an integration must never break a flow run
            return Fail($"{Label(provider)}: {e.GetType().Name}");
        }
    }

    // ---- Google Calendar ----------------------------------------------------

    private async Task<Result> CreateCalendarEventAsync(Integration row, Contact? contact,
        Dictionary<string, string> cfg, string text, CancellationToken ct)
    {
        var token = await _oauth.AccessTokenAsync(row, ct);
        if (token.Length == 0) return Skip("Google Calendar is not connected — step skipped.");
        var calendarId = Pick(cfg, "calendarId", Cfg(row, "calendar_id"), "primary");
        var start = ParseWhen(Get(cfg, "eventStart"));
        var minutes = Math.Max(5, IntOr(Get(cfg, "eventMinutes"), 30));
        var body = new Dictionary<string, object?>
        {
            ["summary"] = Trim(Pick(cfg, "eventTitle", text, "Appointment"), 200),
            ["description"] = Trim(text, 2000),
            ["start"] = new Dictionary<string, object?> { ["dateTime"] = start.ToString("o") },
            ["end"] = new Dictionary<string, object?> { ["dateTime"] = start.AddMinutes(minutes).ToString("o") },
        };
        if (!string.IsNullOrEmpty(contact?.Email))
            body["attendees"] = new[] { new Dictionary<string, object?> { ["email"] = contact!.Email } };

        var (ok, payload) = await SendAsync(HttpMethod.Post,
            $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendarId)}/events",
            token, jsonBody: body, ct: ct);
        RecordResult(row, ok, ok ? "" : payload);
        if (!ok) return Fail($"Google Calendar: {payload}");
        return Done($"calendar event created ({body["summary"]})", new()
        {
            ["event_id"] = JsonField(payload, "id"),
            ["html_link"] = JsonField(payload, "htmlLink"),
        });
    }

    private async Task<Result> FindFreeSlotAsync(Integration row, Dictionary<string, string> cfg,
        CancellationToken ct)
    {
        var token = await _oauth.AccessTokenAsync(row, ct);
        if (token.Length == 0) return Skip("Google Calendar is not connected — step skipped.");
        var calendarId = Pick(cfg, "calendarId", Cfg(row, "calendar_id"), "primary");
        var start = ParseWhen(Get(cfg, "eventStart"));
        var horizon = Math.Max(1, IntOr(Get(cfg, "searchDays"), 7));
        var minutes = Math.Max(5, IntOr(Get(cfg, "eventMinutes"), 30));
        var end = start.AddDays(horizon);

        var (ok, payload) = await SendAsync(HttpMethod.Post,
            "https://www.googleapis.com/calendar/v3/freeBusy", token,
            jsonBody: new Dictionary<string, object?>
            {
                ["timeMin"] = start.ToString("o"),
                ["timeMax"] = end.ToString("o"),
                ["items"] = new[] { new Dictionary<string, object?> { ["id"] = calendarId } },
            }, ct: ct);
        RecordResult(row, ok, ok ? "" : payload);
        if (!ok) return Fail($"Google Calendar: {payload}");

        var busy = BusySpans(payload, calendarId);
        var slot = FirstGap(start, end, busy, minutes);
        if (slot == null) return Done("no free slot found in the search window", new() { ["slot"] = null });
        return Done($"free slot found at {slot.Value:yyyy-MM-dd HH:mm}", new() { ["slot"] = slot.Value.ToString("o") });
    }

    /// <summary>Busy spans for the calendar out of a freeBusy response.</summary>
    private static List<(DateTime start, DateTime end)> BusySpans(string payload, string calendarId)
    {
        var spans = new List<(DateTime, DateTime)>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("calendars", out var cals)) return spans;
            if (!cals.TryGetProperty(calendarId, out var cal)) return spans;
            if (!cal.TryGetProperty("busy", out var busy) || busy.ValueKind != JsonValueKind.Array) return spans;
            foreach (var b in busy.EnumerateArray())
            {
                if (DateTime.TryParse(b.GetProperty("start").GetString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var s)
                    && DateTime.TryParse(b.GetProperty("end").GetString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var e))
                    spans.Add((s, e));
            }
        }
        catch { /* a malformed body just means "no known busy time" */ }
        return spans.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>First gap of `minutes` between `from` and `to` (actions._first_gap).</summary>
    private static DateTime? FirstGap(DateTime from, DateTime to,
        List<(DateTime start, DateTime end)> busy, int minutes)
    {
        var cursor = from;
        foreach (var (bs, be) in busy)
        {
            if (bs - cursor >= TimeSpan.FromMinutes(minutes)) return cursor;
            if (be > cursor) cursor = be;
        }
        return to - cursor >= TimeSpan.FromMinutes(minutes) ? cursor : null;
    }

    // ---- Google Drive -------------------------------------------------------

    private async Task<Result> UploadDriveFileAsync(Integration row, Dictionary<string, string> cfg,
        string text, CancellationToken ct)
    {
        var token = await _oauth.AccessTokenAsync(row, ct);
        if (token.Length == 0) return Skip("Google Drive is not connected — step skipped.");
        var filename = Trim(Pick(cfg, "fileName", "", "chatbranch-note.txt"), 120);
        var folder = Pick(cfg, "folderId", Cfg(row, "folder_id"), "");
        var metadata = new Dictionary<string, object?> { ["name"] = filename };
        if (folder.Length > 0) metadata["parents"] = new[] { folder };

        // Two-step upload: create the file, then PATCH its media body.
        var (ok, created) = await SendAsync(HttpMethod.Post,
            "https://www.googleapis.com/drive/v3/files", token, jsonBody: metadata, ct: ct);
        RecordResult(row, ok, ok ? "" : created);
        if (!ok) return Fail($"Google Drive: {created}");
        var fileId = JsonField(created, "id");

        var (ok2, payload2) = await SendAsync(HttpMethod.Patch,
            $"https://www.googleapis.com/upload/drive/v3/files/{fileId}?uploadType=media", token,
            rawBody: text ?? "", rawContentType: "text/plain", ct: ct);
        if (!ok2) return Fail($"Google Drive upload: {payload2}");
        return Done($"uploaded {filename} to Drive", new() { ["file_id"] = fileId });
    }

    // ---- Google Sheets ------------------------------------------------------

    private async Task<Result> AppendSheetRowAsync(Integration row, Contact? contact,
        Dictionary<string, string> cfg, string text, CancellationToken ct)
    {
        var token = await _oauth.AccessTokenAsync(row, ct);
        if (token.Length == 0) return Skip("Google Sheets is not connected — step skipped.");
        var sheetId = Pick(cfg, "spreadsheetId", Cfg(row, "spreadsheet_id"), "");
        if (sheetId.Length == 0)
            return Skip("No spreadsheet id configured on the step or the account — skipped.");
        var range = Pick(cfg, "sheetRange", Cfg(row, "sheet_range"), "Sheet1!A:Z");

        var values = new[]
        {
            new object?[]
            {
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                contact?.Name ?? "", contact?.Phone ?? "", contact?.Email ?? "",
                Trim(text, 500),
            },
        };
        var (ok, payload) = await SendAsync(HttpMethod.Post,
            $"https://sheets.googleapis.com/v4/spreadsheets/{sheetId}/values/"
            + $"{Uri.EscapeDataString(range)}:append?valueInputOption=USER_ENTERED",
            token, jsonBody: new Dictionary<string, object?> { ["values"] = values }, ct: ct);
        RecordResult(row, ok, ok ? "" : payload);
        if (!ok) return Fail($"Google Sheets: {payload}");
        return Done("row appended to the sheet");
    }

    // ---- Gmail --------------------------------------------------------------

    private async Task<Result> SendGmailAsync(Integration row, Contact? contact,
        Dictionary<string, string> cfg, string text, CancellationToken ct)
    {
        var token = await _oauth.AccessTokenAsync(row, ct);
        if (token.Length == 0) return Skip("Gmail is not connected — step skipped.");
        var to = Pick(cfg, "to", contact?.Email ?? "", "");
        if (to.Length == 0) return Skip("No recipient email for this contact — Gmail step skipped.");
        var subject = Trim(Pick(cfg, "subject", "", "A message from us"), 200);
        var raw = $"To: {to}\r\nSubject: {subject}\r\n"
                  + $"Content-Type: text/plain; charset=UTF-8\r\n\r\n{text ?? ""}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var (ok, payload) = await SendAsync(HttpMethod.Post,
            "https://gmail.googleapis.com/gmail/v1/users/me/messages/send", token,
            jsonBody: new Dictionary<string, object?> { ["raw"] = encoded }, ct: ct);
        RecordResult(row, ok, ok ? "" : payload);
        if (!ok) return Fail($"Gmail: {payload}");
        return Done($"email sent via Gmail → {to}", new() { ["message_id"] = JsonField(payload, "id") });
    }

    // ---- Shopify ------------------------------------------------------------

    private async Task<Result> UpdateOrderStatusAsync(Integration row, Contact? contact,
        Dictionary<string, string> cfg, CancellationToken ct)
    {
        if (contact == null) return Skip("no contact on this run — order status step skipped");
        var record = _db.Records.Where(r => r.ContactId == contact.Id && r.Status != "closed")
            .OrderBy(r => r.Id).FirstOrDefault();
        if (record == null) return Skip("contact has no record — order status step skipped");

        var newStatus = Pick(cfg, "orderStatus", "", "");
        var old = record.Status;
        if (newStatus.Length > 0)
        {
            record.Status = newStatus;
            record.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }

        var shop = Cfg(row, "shop_domain");
        var apiKey = GoogleOAuth.Cred(row, "api_key");
        if (shop.Length == 0 || apiKey.Length == 0 || record.ExternalId.Length == 0)
            return Done($"record status {old} → {record.Status} (Shopify not connected)");

        var version = Cfg(row, "api_version") is { Length: > 0 } v ? v : "2024-07";
        var (ok, payload) = await SendAsync(HttpMethod.Put,
            $"https://{shop}/admin/api/{version}/orders/{record.ExternalId}.json", "",
            jsonBody: new Dictionary<string, object?>
            {
                ["order"] = new Dictionary<string, object?>
                {
                    ["id"] = record.ExternalId,
                    ["note"] = $"status: {record.Status}",
                },
            },
            headers: new Dictionary<string, string> { ["X-Shopify-Access-Token"] = apiKey }, ct: ct);
        RecordResult(row, ok, ok ? "" : payload);
        return ok
            ? Done($"record status {old} → {record.Status} (noted on Shopify order {record.ExternalId})")
            : Fail($"Shopify: {payload}");
    }

    // ---- Stripe -------------------------------------------------------------

    private async Task<Result> CreatePaymentLinkAsync(Integration row, Contact? contact,
        Dictionary<string, string> cfg, CancellationToken ct)
    {
        var apiKey = GoogleOAuth.Cred(row, "api_key");
        if (apiKey.Length == 0) return Skip("Stripe is not connected — step skipped.");
        var record = contact == null
            ? null
            : _db.Records.Where(r => r.ContactId == contact.Id && r.Status != "closed")
                .OrderBy(r => r.Id).FirstOrDefault();

        int amount = 0;
        var currency = "myr";
        if (record?.Amount is decimal amt)
        {
            amount = (int)Math.Round(amt * 100);
            currency = (record.Currency.Length > 0 ? record.Currency : "MYR").ToLowerInvariant();
        }
        var overrideAmount = IntOr(Get(cfg, "amountCents"), 0);
        if (overrideAmount > 0) amount = overrideAmount;
        if (amount <= 0) return Skip("no amount on the record — Stripe payment link skipped");

        var form = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["success_url"] = Pick(cfg, "successUrl", "", "https://example.com/thanks"),
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = currency,
            ["line_items[0][price_data][unit_amount]"] = amount.ToString(),
            ["line_items[0][price_data][product_data][name]"] =
                record?.Title is { Length: > 0 } t ? t : "Payment",
        };
        var (ok, payload) = await SendAsync(HttpMethod.Post,
            "https://api.stripe.com/v1/checkout/sessions", apiKey, formBody: form, ct: ct);
        RecordResult(row, ok, ok ? "" : payload);
        if (!ok) return Fail($"Stripe: {payload}");

        var url = JsonField(payload, "url");
        if (record != null && url.Length > 0)
        {
            // {{link}} renders from record.data["link"] — fill it unless the
            // operator already set one by hand.
            var data = new Dictionary<string, object?>(record.Data) { ["payment_link"] = url };
            if (!data.ContainsKey("link") || string.IsNullOrWhiteSpace(data["link"]?.ToString()))
                data["link"] = url;
            record.Data = data;
            record.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
        return Done("Stripe payment link created", new() { ["url"] = url });
    }

    // ---- HTTP + helpers -----------------------------------------------------

    /// <summary>
    /// One provider call. Returns (ok, body). A bearer token is sent when given;
    /// extra headers cover Shopify's token header. Never throws — a transport
    /// failure comes back as (false, "…").
    /// </summary>
    private async Task<(bool ok, string body)> SendAsync(HttpMethod method, string url, string token,
        Dictionary<string, object?>? jsonBody = null, Dictionary<string, string>? formBody = null,
        string? rawBody = null, string? rawContentType = null,
        Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var req = new HttpRequestMessage(method, url);
            if (token.Length > 0)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            foreach (var kv in headers ?? new()) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            if (jsonBody != null)
                req.Content = new StringContent(JsonSerializer.Serialize(jsonBody), Encoding.UTF8, "application/json");
            else if (formBody != null)
                req.Content = new FormUrlEncodedContent(formBody);
            else if (rawBody != null)
                req.Content = new StringContent(rawBody, Encoding.UTF8, rawContentType ?? "text/plain");

            var resp = await client.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return (true, text);
            // surface the provider's own message, capped — never the whole body
            return (false, $"HTTP {(int)resp.StatusCode} {Trim(ProviderMessage(text), 200)}");
        }
        catch (Exception e)
        {
            return (false, e.GetType().Name);
        }
    }

    /// <summary>The human-readable bit of a provider error body.</summary>
    private static string ProviderMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "";
                if (err.TryGetProperty("message", out var m)) return m.GetString() ?? "";
            }
        }
        catch { /* not JSON */ }
        return body;
    }

    private static string JsonField(string body, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(key, out var v) ? v.ToString() : "";
        }
        catch { return ""; }
    }

    /// <summary>Health is recorded per call so a dead grant surfaces in the UI.</summary>
    private void RecordResult(Integration row, bool ok, string detail)
    {
        try
        {
            row.Status = ok ? "healthy" : "degraded";
            row.StatusDetail = ok ? "" : Trim(detail, 200);
            row.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
        catch { /* the ledger never breaks the action */ }
    }

    private static string Label(string provider) => provider switch
    {
        "google_calendar" => "Google Calendar",
        "google_drive" => "Google Drive",
        "google_sheets" => "Google Sheets",
        "gmail" => "Gmail",
        "shopify" => "Shopify",
        "stripe" => "Stripe",
        _ => provider,
    };

    private static string Cfg(Integration row, string key) =>
        row.Config.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static string Get(Dictionary<string, string> cfg, string key) =>
        cfg.TryGetValue(key, out var v) ? v ?? "" : "";

    /// <summary>Step config → account config → default, first non-empty.</summary>
    private static string Pick(Dictionary<string, string> cfg, string key, string fallback, string dflt)
    {
        var v = Get(cfg, key);
        if (v.Length > 0) return v;
        return fallback.Length > 0 ? fallback : dflt;
    }

    private static int IntOr(string raw, int dflt) => int.TryParse(raw, out var n) ? n : dflt;

    /// <summary>actions._parse_when — an ISO time, else "now".</summary>
    private static DateTime ParseWhen(string raw) =>
        DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : DateTime.UtcNow;

    private static string Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
