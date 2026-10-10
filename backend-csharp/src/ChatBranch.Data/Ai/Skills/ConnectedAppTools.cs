using System.Globalization;
using System.Text.Json;
using ChatBranch.Data.Integrations;
using ChatBranch.Domain;

namespace ChatBranch.Data.Ai.Skills;

/// <summary>
/// The Payments (Stripe payment link) and Sheets (append a row) connected-app
/// tools — thin wrappers over the flow builder's integration actions
/// (<see cref="IntegrationActions"/>), resolved like every integration (the
/// conversation's outlet → its parents → the organization; automated work has
/// no acting user, so only SHARED connections are used). No connection ⇒ the
/// tool answers <c>{ok:false, skipped:true}</c> with words for the model, and
/// nothing is pretended.
/// <list type="bullet">
/// <item><b>payment_create_link</b> runs AT ONCE (the link must be in the reply's
/// text); the AI playground never creates one.</item>
/// <item><b>sheets_append_row</b> is DEFERRED: the row is recorded on the reply
/// (<see cref="AiReplyResult.Deferred"/>) and written when the reply is delivered
/// — a held or rejected reply logs nothing.</item>
/// </list>
/// </summary>
public class ConnectedAppTools
{
    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly IntegrationActions _actions;

    public ConnectedAppTools(AppDbContext db, ScopingService scoping, IntegrationActions? actions = null)
    {
        _db = db;
        _scoping = scoping;
        _actions = actions ?? new IntegrationActions(db, new IntegrationResolver(db, scoping),
            new GoogleOAuth(db, scoping, PlainHttpClientFactory.Instance), PlainHttpClientFactory.Instance);
    }

    public sealed record ToolBinding(AgentSkill Skill, string App, string Action, string Base,
        PaymentAppConfig? Payments, SheetsAppConfig? Sheets);

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static IEnumerable<(string Name, string Action, LlmTool Tool)> PaymentToolDefinitions(string b, PaymentAppConfig cfg) => new[]
    {
        (b + "_create_link", "create_link", new LlmTool(b + "_create_link",
            $"Create a secure Stripe payment link for this customer to pay an amount online (default currency {cfg.Currency}). "
            + "Only when the customer wants to pay and the amount is clear (an agreed price, an invoice total). Put the "
            + "returned url in your reply exactly as given. Never invent an amount; if unsure, ask.",
            Schema("{\"type\":\"object\",\"properties\":{"
                   + "\"amount\":{\"type\":\"number\",\"description\":\"The amount to pay, e.g. 108.00 (not cents)\"},"
                   + $"\"currency\":{{\"type\":\"string\",\"description\":\"3-letter code; default {cfg.Currency}\"}},"
                   + "\"description\":{\"type\":\"string\",\"description\":\"What the payment is for (shown on the payment page)\"}"
                   + "},\"required\":[\"amount\",\"description\"]}"))),
    };

    public static IEnumerable<(string Name, string Action, LlmTool Tool)> SheetsToolDefinitions(string b, SheetsAppConfig cfg)
    {
        var props = string.Join(",", cfg.Columns.Select(c =>
            $"{JsonSerializer.Serialize(c)}:{{\"type\":\"string\"}}"));
        return new[]
        {
            (b + "_append_row", "append_row", new LlmTool(b + "_append_row",
                $"Log one row to the business's \"{cfg.Label}\" Google Sheet (time, the customer's name, phone and email "
                + $"are added automatically). Fill in: {string.Join(", ", cfg.Columns)}. Use it once the details are "
                + "known (e.g. a confirmed lead or order). The row is written when your reply is sent; don't mention "
                + "the spreadsheet to the customer unless they ask.",
                Schema("{\"type\":\"object\",\"properties\":{\"values\":{\"type\":\"object\",\"properties\":{" + props
                       + "},\"description\":\"One value per column\"}},\"required\":[\"values\"]}"))),
        };
    }

    public async Task<(string Json, bool Ok)> ExecuteAsync(ToolBinding b, string? argsJson, SkillContext ctx,
        CancellationToken ct = default)
    {
        try
        {
            var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone();
            return b.App switch
            {
                AppSkills.Payments => await PaymentAsync(b, args, ctx, ct),
                AppSkills.Sheets => Sheets(b, args, ctx),
                _ => Refused($"unknown app '{b.App}'", ""),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Refused($"The {b.App} tool failed ({ex.GetType().Name}).", "Tell the customer a teammate will follow up.");
        }
    }

    private async Task<(string, bool)> PaymentAsync(ToolBinding b, JsonElement args, SkillContext ctx, CancellationToken ct)
    {
        var cfg = b.Payments ?? new PaymentAppConfig();
        var amount = Documents.DocumentMath.Num(args, "amount");
        if (amount is not decimal a || a <= 0) return Refused("amount must be a number above 0.", "Ask the customer or check the price.");
        a = Documents.DocumentMath.Round(a);
        if (a > cfg.MaxAmount)
            return Refused($"{a:0.00} is above this skill's limit ({cfg.MaxAmount:0.00}).", "A teammate must send a link for a larger amount.");
        var currency = (Str(args, "currency") ?? cfg.Currency).Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter)) currency = cfg.Currency;
        var description = (Str(args, "description") ?? "").Trim();
        if (description.Length == 0) description = "Payment";
        if (description.Length > 200) description = description[..200];

        if (ctx.Docs.Preview)
            return (JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true, ["status"] = "preview", ["url"] = "https://checkout.stripe.com/preview",
                ["amount"] = a.ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = currency,
                ["note"] = "AI playground: no payment link was created.",
            }), true);

        var cents = (long)(a * 100m);
        var config = new Dictionary<string, string>
        {
            ["amountCents"] = cents.ToString(CultureInfo.InvariantCulture),
            ["currency"] = currency.ToLowerInvariant(),
            ["productName"] = description,
            ["skipRecord"] = "1",
        };
        if (cfg.SuccessUrl.Length > 0) config["successUrl"] = cfg.SuccessUrl;
        var r = await _actions.RunAsync("Create Stripe payment link", ctx.ContactRecord, config, description,
            ctx.EntityId, null, ct);
        if (!r.Ok)
            return (JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = false, ["skipped"] = r.Skipped, ["error"] = r.Detail,
                ["advice"] = "Don't promise a payment link. Tell the customer a teammate will send the payment details.",
            }), false);
        var url = r.Data?.TryGetValue("url", out var u) == true ? u?.ToString() ?? "" : "";
        return (JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true, ["status"] = "created", ["url"] = url,
            ["amount"] = a.ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = currency,
            ["instructions"] = "Give the customer this url exactly as returned, with the amount and currency.",
        }), true);
    }

    private (string, bool) Sheets(ToolBinding b, JsonElement args, SkillContext ctx)
    {
        var cfg = b.Sheets ?? new SheetsAppConfig();
        if (cfg.SpreadsheetId.Length == 0)
            return Refused("No spreadsheet is set on this skill yet.", "Carry on with the customer; nothing was logged.", skipped: true);
        var values = args.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object ? v : args;
        var row = cfg.Columns.Select(c => Cap(Str(values, c) ?? "", 500)).ToList();
        if (row.All(x => x.Length == 0))
            return Refused($"Fill in at least one of: {string.Join(", ", cfg.Columns)}.", "");
        if (ctx.Docs.Deferred.Count(d => AiGate.Str(d, "app", "") == AppSkills.Sheets) >= 3)
            return Refused("At most 3 rows per reply.", "");
        if (!ctx.Docs.Preview)
            ctx.Docs.Deferred.Add(new Dictionary<string, object?>
            {
                ["app"] = AppSkills.Sheets, ["skill_id"] = b.Skill.SkillId, ["skill_entity_id"] = b.Skill.EntityId,
                ["spreadsheet_id"] = cfg.SpreadsheetId, ["sheet_range"] = cfg.SheetRange, ["values"] = row,
            });
        return (JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = true, ["status"] = ctx.Docs.Preview ? "preview" : "queued",
            ["row"] = cfg.Columns.Zip(row).ToDictionary(x => x.First, x => (object?)x.Second),
            ["note"] = ctx.Docs.Preview ? "AI playground: nothing is logged." : "The row is written when your reply is sent.",
        }), true);
    }

    /// <summary>
    /// Run a delivered reply's deferred actions (Sheets rows). Returns what happened,
    /// one line each, for the conversation's private note. Never throws.
    /// </summary>
    public List<string> RunDeferred(IEnumerable<object?> deferred, Contact? contact, int? entityId)
    {
        var done = new List<string>();
        foreach (var d in deferred)
        {
            try
            {
                if (AiGate.Str(d, "app", "") != AppSkills.Sheets) continue;
                var values = AiGate.StrList(d, "values");
                var row = new List<string>
                {
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                    contact?.Name ?? "", contact?.Phone ?? "", contact?.Email ?? "",
                };
                row.AddRange(values);
                var r = Task.Run(() => _actions.RunAsync("Append row to Sheet", contact, new Dictionary<string, string>
                {
                    ["spreadsheetId"] = AiGate.Str(d, "spreadsheet_id", ""),
                    ["sheetRange"] = AiGate.Str(d, "sheet_range", "Sheet1!A:Z"),
                    ["rowJson"] = JsonSerializer.Serialize(row),
                }, "", entityId, null)).GetAwaiter().GetResult();
                done.Add(r.Ok ? "📊 Row logged to Google Sheets" : $"📊 Google Sheets row not logged: {r.Detail}");
            }
            catch (Exception ex)
            {
                done.Add($"📊 Google Sheets row not logged ({ex.GetType().Name})");
            }
        }
        return done;
    }

    private static (string, bool) Refused(string error, string advice, bool skipped = false) =>
        (JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = false, ["skipped"] = skipped ? true : null, ["error"] = error,
            ["advice"] = advice.Length > 0 ? advice : null,
        }), false);

    private static string? Str(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText() : null
            : null;

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}
