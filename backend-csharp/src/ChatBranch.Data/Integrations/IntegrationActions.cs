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
    private readonly GoogleCalendarClient _calendar;

    public IntegrationActions(AppDbContext db, IntegrationResolver resolver, GoogleOAuth oauth,
        IHttpClientFactory http, GoogleCalendarClient? calendar = null)
    {
        _db = db;
        _resolver = resolver;
        _oauth = oauth;
        _http = http;
        _calendar = calendar ?? new GoogleCalendarClient(db, oauth, http);
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
            var result = action switch
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
            // every action that touched a provider lands in the activity log
            ChatBranch.Data.Audit.AuditTrail.Write(_db, "integration.action", $"{action} ({Label(provider)})",
                $"{(result.Ok ? "ok" : result.Skipped ? "skipped" : "failed")}: {result.Detail}",
                entityId: entityId, userId: userId);
            return result;
        }
        catch (Exception e)
        {
            // an integration must never break a flow run
            return Fail($"{Label(provider)}: {e.GetType().Name}");
        }
    }

    // ---- Google Calendar (HTTP in GoogleCalendarClient, shared with the AI's booking tools) ----

    private async Task<Result> CreateCalendarEventAsync(Integration row, Contact? contact,
        Dictionary<string, string> cfg, string text, CancellationToken ct)
    {
        var calendarId = Pick(cfg, "calendarId", Cfg(row, "calendar_id"), "primary");
        var start = ParseWhen(Get(cfg, "eventStart"));
        var minutes = Math.Max(5, IntOr(Get(cfg, "eventMinutes"), 30));
        var summary = Trim(Pick(cfg, "eventTitle", text, "Appointment"), 200);
        var r = await _calendar.InsertEventAsync(row, calendarId, new GoogleCalendarClient.EventSpec(
            summary, Trim(text, 2000), start, start.AddMinutes(minutes),
            AttendeeEmail: string.IsNullOrEmpty(contact?.Email) ? null : contact!.Email), ct);
        if (r.NotConnected) return Skip("Google Calendar is not connected — step skipped.");
        if (!r.Ok) return Fail(r.Error);
        return Done($"calendar event created ({summary})", new()
        {
            ["event_id"] = r.Id,
            ["html_link"] = r.HtmlLink,
        });
    }

    private async Task<Result> FindFreeSlotAsync(Integration row, Dictionary<string, string> cfg,
        CancellationToken ct)
    {
        var calendarId = Pick(cfg, "calendarId", Cfg(row, "calendar_id"), "primary");
        var start = ParseWhen(Get(cfg, "eventStart"));
        var horizon = Math.Max(1, IntOr(Get(cfg, "searchDays"), 7));
        var minutes = Math.Max(5, IntOr(Get(cfg, "eventMinutes"), 30));
        var end = start.AddDays(horizon);

        var r = await _calendar.FreeBusyAsync(row, calendarId, start, end, ct);
        if (r.NotConnected) return Skip("Google Calendar is not connected — step skipped.");
        if (!r.Ok) return Fail(r.Error);

        var slot = FirstGap(start, end, r.Busy.ToList(), minutes);
        if (slot == null) return Done("no free slot found in the search window", new() { ["slot"] = null });
        return Done($"free slot found at {slot.Value:yyyy-MM-dd HH:mm}", new() { ["slot"] = slot.Value.ToString("o") });
    }

    /// <summary>First gap of `minutes` between `from` and `to` (actions._first_gap).</summary>
    private static DateTime? FirstGap(DateTime from, DateTime to,
        List<(DateTime Start, DateTime End)> busy, int minutes)
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

    /// <summary>
    /// Upload a FILE (a customer's attachment) to the resolved Google Drive
    /// connection — for the background AI tasks, never reachable from a flow
    /// step's config (a step names no server file). Same invariants as
    /// <see cref="RunAsync"/>: no connection skips, a provider error fails, it
    /// never throws, and it lands in the activity log.
    /// </summary>
    public async Task<Result> UploadDriveBytesAsync(byte[] bytes, string fileName, string mime, string folderId,
        int? entityId, CancellationToken ct = default)
    {
        var row = _resolver.Active("google_drive", entityId, null);
        if (row == null) return Skip("Google Drive is not connected — file not uploaded.");
        try
        {
            var token = await _oauth.AccessTokenAsync(row, ct);
            if (token.Length == 0) return Skip("Google Drive is not connected — file not uploaded.");
            var name = Trim(string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName.Trim(), 120);
            var folder = folderId.Length > 0 ? folderId : Cfg(row, "folder_id");
            var metadata = new Dictionary<string, object?> { ["name"] = name, ["mimeType"] = mime };
            if (folder.Length > 0) metadata["parents"] = new[] { folder };

            var (ok, created) = await SendAsync(HttpMethod.Post,
                "https://www.googleapis.com/drive/v3/files?fields=id,webViewLink", token, jsonBody: metadata, ct: ct);
            RecordResult(row, ok, ok ? "" : created);
            if (!ok) return Fail($"Google Drive: {created}");
            var fileId = JsonField(created, "id");
            var r = await ProviderHttp.SendAsync(_http, HttpMethod.Patch,
                $"https://www.googleapis.com/upload/drive/v3/files/{fileId}?uploadType=media", token,
                rawContentType: mime, ct: ct, bytes: bytes);
            var result = r.Ok
                ? Done($"uploaded {name} to Drive", new()
                {
                    ["file_id"] = fileId,
                    ["link"] = JsonField(created, "webViewLink"),
                })
                : Fail($"Google Drive upload: {r.Body}");
            ChatBranch.Data.Audit.AuditTrail.Write(_db, "integration.action", "Upload attachment to Drive (Google Drive)",
                $"{(result.Ok ? "ok" : "failed")}: {result.Detail}", entityId: entityId);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            return Fail($"Google Drive: {e.GetType().Name}");
        }
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

        // the AI's Sheets skill hands over its own row (rowJson); a flow step logs the fixed columns
        object?[] cells = new object?[]
        {
            DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            contact?.Name ?? "", contact?.Phone ?? "", contact?.Email ?? "",
            Trim(text, 500),
        };
        if (Get(cfg, "rowJson") is { Length: > 0 } rowJson)
        {
            try
            {
                cells = (System.Text.Json.JsonSerializer.Deserialize<List<string>>(rowJson) ?? new())
                    .Select(c => (object?)Trim(c, 500)).Take(30).ToArray();
            }
            catch { return Fail("Google Sheets: the row is not a list of values."); }
        }
        // USER_ENTERED parses what it gets: a customer-supplied "=IMPORTXML(…)" / "=HYPERLINK(…)" would
        // run as a formula in the business's sheet — every text cell is stored as text instead
        var values = new[] { cells.Select(c => (object?)SheetCell(c?.ToString() ?? "")).ToArray() };
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
        // header values never carry a line break (no injected Bcc: / extra headers)
        static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ");
        var to = OneLine(Pick(cfg, "to", contact?.Email ?? "", "")).Trim();
        if (to.Length == 0) return Skip("No recipient email for this contact — Gmail step skipped.");
        var subject = OneLine(Trim(Pick(cfg, "subject", "", "A message from us"), 200));
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
        // the AI's payment-link skill names its own amount and touches no record (skipRecord)
        var record = contact == null || Get(cfg, "skipRecord") == "1"
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
        if (Get(cfg, "currency") is { Length: 3 } cur) currency = cur.ToLowerInvariant();
        if (amount <= 0) return Skip("no amount on the record — Stripe payment link skipped");

        var form = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["success_url"] = Pick(cfg, "successUrl", "", "https://example.com/thanks"),
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = currency,
            ["line_items[0][price_data][unit_amount]"] = amount.ToString(),
            ["line_items[0][price_data][product_data][name]"] =
                Get(cfg, "productName") is { Length: > 0 } pn ? Trim(pn, 200)
                : record?.Title is { Length: > 0 } t ? t : "Payment",
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

    /// <summary>One provider call (<see cref="ProviderHttp"/>). Returns (ok, body);
    /// never throws — a transport failure comes back as (false, "…").</summary>
    private async Task<(bool ok, string body)> SendAsync(HttpMethod method, string url, string token,
        Dictionary<string, object?>? jsonBody = null, Dictionary<string, string>? formBody = null,
        string? rawBody = null, string? rawContentType = null,
        Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        var r = await ProviderHttp.SendAsync(_http, method, url, token, jsonBody, formBody, rawBody,
            rawContentType, headers, ct);
        return (r.Ok, r.Body);
    }

    private static string JsonField(string body, string key) => ProviderHttp.JsonField(body, key);

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

    /// <summary>
    /// A spreadsheet cell that can't become a formula (CSV / formula injection): text starting
    /// with = + - @ (or a tab / carriage return) gets Sheets' leading apostrophe, which shows the
    /// text as typed. A plain number ("12", "-5.5") stays a number; "+6012…" phones stay text.
    /// </summary>
    public static string SheetCell(string value)
    {
        if (value.Length == 0) return value;
        var first = value[0];
        if (first is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            if (first == '-' && decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                return value;
            return "'" + value;
        }
        return value;
    }
}
