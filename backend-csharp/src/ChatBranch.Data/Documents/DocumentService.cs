using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Documents;

/// <summary>
/// Files the AI makes for a customer — the Documents app (PDF invoices,
/// quotations, receipts, letters) and the QR app — and the ONE place their
/// rules live:
/// <list type="bullet">
/// <item><b>The server does the maths.</b> The model sends items (qty, unit
/// price, discount); <see cref="DocumentMath"/> computes every amount and the
/// series hands out the running number. The tool answers with those numbers at
/// once, so the reply quotes them exactly.</item>
/// <item><b>Made while writing, sent at delivery.</b> The tool renders the PDF,
/// stores it as conversation media and records a <see cref="DocumentStatus.Pending"/>
/// row; its id rides on the reply (<see cref="AiReplyResult.Attachments"/> →
/// the draft's <c>attachments</c>). When <see cref="AiGate"/> delivers the reply
/// the file follows the text through the channel's real media send
/// (<see cref="Deliver"/>); a channel that can't carry a file (LINE, Viber, SMS,
/// TikTok, Douyin, the website widget) gets a secure download link instead.
/// A reply that is rejected, expires, is shadowed or blocked VOIDS the document
/// (<see cref="Release(IEnumerable{int}, string)"/>) — the number stays used.</item>
/// <item><b>The playground previews.</b> A <see cref="Session.Preview"/> renders
/// the real PDF into a throw-away preview file: no row, no number taken, no
/// conversation media.</item>
/// </list>
/// Every sent / failed document writes an audit row and a private note
/// ("📄 Invoice INV-0012 sent"). Nothing here throws into a reply or send path.
/// </summary>
public class DocumentService
{
    /// <summary>At most this many files per reply.</summary>
    public const int MaxPerReply = 3;
    /// <summary>A customer's download link works this long.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(90);
    public const long MaxLogoBytes = 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly MediaStore _media;
    private readonly TimeProvider _clock;

    public DocumentService(AppDbContext db, ScopingService scoping, MediaStore? media = null, TimeProvider? clock = null)
    {
        _db = db;
        _scoping = scoping;
        _media = media ?? new MediaStore(Sidecars.VoiceAgentBridge.ResolveDataDir(null));
        _clock = clock ?? TimeProvider.System;
    }

    public MediaStore Media => _media;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // =========================================================================
    // the per-generation session + tool bindings
    // =========================================================================

    /// <summary>What one generation's file tools made (or, in the playground, previewed).</summary>
    public sealed class Session
    {
        /// <summary>The AI playground: render for real, keep nothing (no row, no number, no media).</summary>
        public bool Preview { get; set; }
        /// <summary>Documents made for this reply (sent when it is delivered).</summary>
        public List<int> DocumentIds { get; } = new();
        public List<DocumentPreview> Previews { get; } = new();
        /// <summary>Connected-app actions that run only when the reply is delivered (Sheets rows).</summary>
        public List<Dictionary<string, object?>> Deferred { get; } = new();
        internal HashSet<string> Types { get; } = new();
    }

    /// <summary>A playground preview: what the reply would have sent (nothing was kept).</summary>
    public sealed record DocumentPreview(string Type, string Label, string Number, string Currency, decimal Total,
        string Url, string FileName);

    /// <summary>One file tool the model may call.</summary>
    public sealed record ToolBinding(AgentSkill Skill, string App, string Action, string Base,
        DocumentAppConfig? Documents, QrAppConfig? Qr);

    public sealed record ToolOutcome(string Json, bool Ok);

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string ItemsSchema =
        "\"items\":{\"type\":\"array\",\"description\":\"The lines, in order. The server computes every amount — "
        + "send quantities and unit prices only.\",\"items\":{\"type\":\"object\",\"properties\":{"
        + "\"description\":{\"type\":\"string\"},"
        + "\"qty\":{\"type\":\"number\",\"description\":\"Quantity (default 1)\"},"
        + "\"unit_price\":{\"type\":\"number\",\"description\":\"Price of ONE unit, before tax, as a plain number\"},"
        + "\"discount\":{\"type\":[\"number\",\"string\"],\"description\":\"Optional: an amount off this line (5) or a percent (\\\"10%\\\")\"}"
        + "},\"required\":[\"description\",\"unit_price\"]}}";

    private const string CustomerSchema =
        "\"customer_name\":{\"type\":\"string\",\"description\":\"Who it is for (default: this customer's name)\"},"
        + "\"customer_company\":{\"type\":\"string\"},"
        + "\"customer_address\":{\"type\":\"string\",\"description\":\"Optional postal address, lines separated by \\\\n\"},"
        + "\"customer_email\":{\"type\":\"string\"},"
        + "\"customer_phone\":{\"type\":\"string\"},";

    /// <summary>The tools of one Documents skill (only its enabled document types).</summary>
    public static IEnumerable<(string Name, string Action, LlmTool Tool)> ToolDefinitions(string b, DocumentAppConfig cfg)
    {
        var money = cfg.TaxRate > 0
            ? $"Prices are in {cfg.Currency} before {(cfg.TaxLabel.Length > 0 ? cfg.TaxLabel : "tax")}; {DocumentMath.Qty(cfg.TaxRate)}% is added on the subtotal."
            : $"Prices are in {cfg.Currency}; no tax is added.";
        const string after = " The PDF is sent to the customer as a file right after your reply (a download link on "
                             + "channels that can't carry files). In your reply, quote the document number and the "
                             + "total exactly as this tool returns them — never compute amounts yourself.";
        if (cfg.Allows(DocumentTypes.Invoice))
            yield return (b + "_create_invoice", DocumentTypes.Invoice, new LlmTool(b + "_create_invoice",
                "Create a PDF INVOICE for this customer for what they bought or agreed to buy. Only when the customer "
                + "asked for an invoice or bill, and the items, quantities and prices are clear (from your instructions "
                + $"or knowledge — never invent a price). {money}" + after,
                Schema("{\"type\":\"object\",\"properties\":{" + CustomerSchema + ItemsSchema + ","
                       + "\"due_date\":{\"type\":\"string\",\"description\":\"Optional YYYY-MM-DD (default: the business's payment terms)\"},"
                       + "\"reference\":{\"type\":\"string\",\"description\":\"Optional order / PO reference\"},"
                       + "\"notes\":{\"type\":\"string\",\"description\":\"Optional short note printed on the invoice\"}"
                       + "},\"required\":[\"items\"]}")));
        if (cfg.Allows(DocumentTypes.Quotation))
            yield return (b + "_create_quotation", DocumentTypes.Quotation, new LlmTool(b + "_create_quotation",
                "Create a PDF QUOTATION (price quote) for this customer. Use it when the customer asks for a quote or "
                + $"price list for specific items. Valid for {cfg.QuotationValidDays} days. {money}" + after,
                Schema("{\"type\":\"object\",\"properties\":{" + CustomerSchema + ItemsSchema + ","
                       + "\"title\":{\"type\":\"string\",\"description\":\"Optional heading, e.g. 'Kitchen renovation'\"},"
                       + "\"reference\":{\"type\":\"string\"},"
                       + "\"notes\":{\"type\":\"string\",\"description\":\"Optional terms or remarks printed on the quotation\"}"
                       + "},\"required\":[\"items\"]}")));
        if (cfg.Allows(DocumentTypes.Receipt))
            yield return (b + "_create_receipt", DocumentTypes.Receipt, new LlmTool(b + "_create_receipt",
                "Create a PDF RECEIPT confirming a payment the customer ALREADY made (only when a payment is confirmed "
                + $"in the conversation or your instructions — never on a promise to pay). {money}" + after,
                Schema("{\"type\":\"object\",\"properties\":{" + CustomerSchema + ItemsSchema + ","
                       + "\"payment_method\":{\"type\":\"string\",\"description\":\"e.g. 'Bank transfer', 'Card'\"},"
                       + "\"reference\":{\"type\":\"string\",\"description\":\"Payment / transaction reference\"},"
                       + "\"notes\":{\"type\":\"string\"}"
                       + "},\"required\":[\"items\"]}")));
        if (cfg.Allows(DocumentTypes.Letter))
            yield return (b + "_create_letter", DocumentTypes.Letter, new LlmTool(b + "_create_letter",
                "Create a PDF LETTER on the business's letterhead (a confirmation letter, an official notice, a "
                + "statement the customer asked for in writing). Write the body yourself in the customer's language. "
                + "The PDF is sent as a file right after your reply; mention it in the reply.",
                Schema("{\"type\":\"object\",\"properties\":{"
                       + "\"title\":{\"type\":\"string\",\"description\":\"Subject line\"},"
                       + "\"paragraphs\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"The body, one entry per paragraph\"},"
                       + "\"recipient_name\":{\"type\":\"string\",\"description\":\"Default: this customer's name\"},"
                       + "\"recipient_address\":{\"type\":\"string\"},"
                       + "\"closing\":{\"type\":\"string\",\"description\":\"Optional sign-off, e.g. 'Yours sincerely,'\"}"
                       + "},\"required\":[\"title\",\"paragraphs\"]}")));
    }

    public static IEnumerable<(string Name, string Action, LlmTool Tool)> QrToolDefinitions(string b) => new[]
    {
        (b + "_create", "create", new LlmTool(b + "_create",
            "Make a QR code image of a link or short text (a payment link, a location, a Wi-Fi code, a voucher code) "
            + "and send it to the customer as a picture right after your reply. Use it when the customer asks for a "
            + "QR code or when scanning is clearly easier for them.",
            Schema("{\"type\":\"object\",\"properties\":{"
                   + "\"text\":{\"type\":\"string\",\"description\":\"What the QR code contains (a URL or up to 1000 characters)\"},"
                   + "\"caption\":{\"type\":\"string\",\"description\":\"Optional short caption sent with the picture\"}"
                   + "},\"required\":[\"text\"]}"))),
    };

    // =========================================================================
    // running a tool
    // =========================================================================

    public async Task<ToolOutcome> ExecuteToolAsync(ToolBinding binding, string? argsJson, SkillContext ctx,
        CancellationToken ct = default)
    {
        try
        {
            var args = ParseArgs(argsJson);
            var session = ctx.Docs;
            if (!session.Preview && session.DocumentIds.Count >= MaxPerReply || session.Preview && session.Previews.Count >= MaxPerReply)
                return Refused($"At most {MaxPerReply} files per reply.", "Send what you made; offer the rest in your next message.");
            return binding.App == AppSkills.Qr
                ? await QrAsync(binding, args, ctx, ct)
                : await DocumentAsync(binding, args, ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Refused($"The document tool failed ({ex.GetType().Name}).",
                "Don't promise a document. Tell the customer a teammate will send it.");
        }
    }

    private async Task<ToolOutcome> DocumentAsync(ToolBinding b, JsonElement args, SkillContext ctx, CancellationToken ct)
    {
        var cfg = b.Documents ?? new DocumentAppConfig();
        var type = b.Action;
        if (!cfg.Allows(type)) return Refused($"This skill can't make a {type}.", "");
        if (!ctx.Docs.Types.Add(type))
            return Refused($"A {DocumentTypes.Label(type).ToLowerInvariant()} was already made for this reply.",
                "Send that one; make another in your next message if the customer needs it.");

        var build = Build(type, args, cfg, ctx.ContactRecord, ctx.UtcNow, ctx.TimeZone, "");
        if (build.Error != null)
        {
            ctx.Docs.Types.Remove(type);
            return Refused(build.Error, build.Advice);
        }
        var content = build.Content!;
        var logo = LoadLogo(cfg.Logo);

        if (ctx.Docs.Preview)
        {
            // the playground: the number this would get, nothing taken or kept
            var next = NextSeq(SeriesFor(b.Skill, type), cfg.StartNumber(type));
            content = content.With(cfg.FormatNumber(type, next));
            var bytes = DocumentRenderer.Render(content, cfg, logo);
            var (url, file) = await SavePreviewAsync(bytes, ".pdf", content.Number, ct);
            ctx.Docs.Previews.Add(new DocumentPreview(type, DocumentTypes.Label(type), content.Number, content.Currency,
                content.Totals.Total, url, file));
            return Ok(ToolPayload(content, cfg, file, preview: true));
        }

        var thread = ctx.ThreadId is int tid ? _db.Threads.AsNoTracking().FirstOrDefault(t => t.Id == tid) : null;
        var row = Issue(b.Skill, type, cfg, content, ctx.ContactRecord?.Id ?? ctx.ContactId, thread,
            ctx.EntityId ?? thread?.EntityId, "ai", logo);
        ctx.Docs.DocumentIds.Add(row.Id);
        return Ok(ToolPayload(ContentOf(row) ?? content.With(row.Number), cfg, row.FileName, preview: false));
    }

    private async Task<ToolOutcome> QrAsync(ToolBinding b, JsonElement args, SkillContext ctx, CancellationToken ct)
    {
        var cfg = b.Qr ?? new QrAppConfig();
        var text = (Arg(args, "text") ?? "").Trim();
        if (text.Length == 0) return Refused("text is required — what should the QR code contain?", "");
        if (text.Length > 1000) return Refused("That's too long for a QR code (max 1000 characters).", "Shorten it or use a link.");
        if (!ctx.Docs.Types.Add("qr:" + text)) return Refused("That QR code was already made for this reply.", "");
        var caption = (Arg(args, "caption") ?? cfg.Caption).Trim();
        if (caption.Length > 300) caption = caption[..300];
        var png = QrPng(text, cfg.PixelsPerModule);

        if (ctx.Docs.Preview)
        {
            var (url, file) = await SavePreviewAsync(png, ".png", "qr-code", ct);
            ctx.Docs.Previews.Add(new DocumentPreview(DocumentTypes.Qr, DocumentTypes.Label(DocumentTypes.Qr), "", "", 0,
                url, file));
            return Ok(QrPayload(text, file, preview: true));
        }
        var thread = ctx.ThreadId is int tid ? _db.Threads.AsNoTracking().FirstOrDefault(t => t.Id == tid) : null;
        var row = Store(new Document
        {
            Type = DocumentTypes.Qr, SkillId = b.Skill.SkillId, Title = caption, CreatedBy = "ai",
            ContactId = ctx.ContactRecord?.Id ?? ctx.ContactId, ThreadId = thread?.Id,
            EntityId = ctx.EntityId ?? thread?.EntityId, Mime = "image/png", FileName = "qr-code.png",
            Data = new Dictionary<string, object?> { ["text"] = text, ["caption"] = caption },
        }, thread, png, ".png");
        ctx.Docs.DocumentIds.Add(row.Id);
        return Ok(QrPayload(text, row.FileName, preview: false));
    }

    private static string QrPayload(string text, string file, bool preview) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["ok"] = true,
        ["status"] = preview ? "preview" : "ready",
        ["file"] = file,
        ["contains"] = text,
        ["instructions"] = "The QR code is sent to the customer as a picture right after your reply. Mention it "
                           + "briefly (\"Here's the QR code\"); don't paste its contents again unless it helps.",
    });

    public static byte[] QrPng(string text, int pixelsPerModule = 10)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(Math.Clamp(pixelsPerModule, 4, 20));
    }

    // =========================================================================
    // building + issuing a document (shared by the AI tools and the operator)
    // =========================================================================

    public sealed record BuildResult(DocumentContent? Content, string? Error, string Advice = "");

    /// <summary>Read the tool / form arguments into what the PDF prints (numbers computed, no number yet).</summary>
    public static BuildResult Build(string type, JsonElement args, DocumentAppConfig cfg, Contact? contact,
        DateTimeOffset utcNow, TimeZoneInfo tz, string number)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, tz).DateTime);
        var language = cfg.Language is "en" or "ms" ? cfg.Language
            : (contact?.Language ?? "").Trim().ToLowerInvariant() is var cl && (cl.StartsWith("ms") || cl.StartsWith("bm") || cl == "malay")
                ? "ms" : "en";
        string A(string key, int max) => Cap((Arg(args, key) ?? "").Trim(), max);

        var customer = new DocParty(
            A("customer_name", 120) is { Length: > 0 } n ? n : A("recipient_name", 120) is { Length: > 0 } rn ? rn : contact?.Name ?? "",
            A("customer_company", 120) is { Length: > 0 } co ? co : contact?.CompanyName ?? "",
            A("customer_address", 500) is { Length: > 0 } ad ? ad : A("recipient_address", 500),
            A("customer_email", 200) is { Length: > 0 } em ? em : type == DocumentTypes.Letter ? "" : contact?.Email ?? "",
            A("customer_phone", 60) is { Length: > 0 } ph ? ph : type == DocumentTypes.Letter ? "" : contact?.Phone ?? "");

        if (type == DocumentTypes.Letter)
        {
            var title = A("title", 160);
            if (title.Length == 0) return new BuildResult(null, "A letter needs a title (its subject line).");
            var paragraphs = new List<string>();
            if (args.TryGetProperty("paragraphs", out var ps) && ps.ValueKind == JsonValueKind.Array)
                paragraphs.AddRange(ps.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString()));
            else if (Arg(args, "body") is { Length: > 0 } body)
                paragraphs.AddRange(Regex.Split(body.Replace("\r", ""), @"\n\s*\n"));
            paragraphs = paragraphs.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (paragraphs.Count == 0) return new BuildResult(null, "A letter needs at least one paragraph.");
            if (paragraphs.Count > 30 || paragraphs.Sum(p => p.Length) > 8000)
                return new BuildResult(null, "That letter is too long (max 30 paragraphs, 8,000 characters).", "Shorten it.");
            var letter = new DocumentContent
            {
                Type = type, Number = number, Title = title, IssueDate = today, Customer = customer,
                Paragraphs = paragraphs, Closing = A("closing", 80), Language = language, Currency = cfg.Currency,
            };
            return CjkCheck(letter, cfg);
        }

        if (!args.TryGetProperty("items", out var items))
            return new BuildResult(null, "items are required: [{description, qty, unit_price}].");
        var lines = DocumentMath.Lines(items, out var lineError);
        if (lineError != null)
            return new BuildResult(null, lineError, "Fix the items and call the tool again; ask the customer if something is unclear.");
        var totals = DocumentMath.Totals(lines, cfg.TaxRate);

        DateOnly? due = null, valid = null;
        if (type == DocumentTypes.Invoice)
        {
            due = cfg.InvoiceDueDays > 0 ? today.AddDays(cfg.InvoiceDueDays) : null;
            if (A("due_date", 20) is { Length: > 0 } dd)
            {
                if (!DateOnly.TryParseExact(dd.Length >= 10 ? dd[..10] : dd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsed))
                    return new BuildResult(null, $"due_date must be YYYY-MM-DD (got '{dd}').");
                if (parsed < today) return new BuildResult(null, "due_date can't be in the past.");
                due = parsed;
            }
        }
        if (type == DocumentTypes.Quotation) valid = today.AddDays(cfg.QuotationValidDays);

        var content = new DocumentContent
        {
            Type = type, Number = number, Title = A("title", 120), IssueDate = today, DueDate = due, ValidUntil = valid,
            Customer = customer, Lines = lines, Totals = totals, Currency = cfg.Currency, Notes = A("notes", 1000),
            Reference = A("reference", 60), PaymentMethod = A("payment_method", 60), Language = language,
        };
        return CjkCheck(content, cfg);
    }

    private static BuildResult CjkCheck(DocumentContent c, DocumentAppConfig cfg)
    {
        var texts = new[] { c.Title, c.Notes, c.Reference, c.PaymentMethod, c.Closing, c.Customer.Name, c.Customer.Company,
                c.Customer.Address, cfg.BusinessName, cfg.Address, cfg.PaymentInstructions, cfg.FooterNote }
            .Concat(c.Lines.Select(l => l.Description)).Concat(c.Paragraphs);
        if (texts.Any(DocumentFonts.HasCjk) && !DocumentFonts.CjkAvailable)
            return new BuildResult(null,
                "Chinese / Japanese / Korean characters can't be printed in documents on this server (no CJK font is installed).",
                "Write the document in English or Malay (names may be romanised) and call the tool again.");
        return new BuildResult(c, null);
    }

    /// <summary>The running-number series of a skill's document type.</summary>
    public static string SeriesFor(AgentSkill skill, string type) =>
        $"{(skill.EntityId is int e ? e.ToString(CultureInfo.InvariantCulture) : "org")}:{skill.SkillId}:{type}";

    public int NextSeq(string series, int start)
    {
        var max = _db.Documents.AsNoTracking().Where(d => d.Series == series).Max(d => (int?)d.Seq) ?? 0;
        return Math.Max(start, max + 1);
    }

    /// <summary>
    /// Take the next number, render, store the file in the conversation's media
    /// and record the row (pending — <paramref name="createdBy"/> "ai" — or issued
    /// for an operator). Two replies racing for a number: the unique index refuses
    /// one and it takes the next.
    /// </summary>
    public Document Issue(AgentSkill skill, string type, DocumentAppConfig cfg, DocumentContent content, int? contactId,
        Thread? thread, int? entityId, string createdBy, byte[]? logo = null)
    {
        var series = SeriesFor(skill, type);
        for (var attempt = 0; ; attempt++)
        {
            var seq = NextSeq(series, cfg.StartNumber(type));
            var numbered = content.With(cfg.FormatNumber(type, seq));
            var bytes = DocumentRenderer.Render(numbered, cfg, logo ?? LoadLogo(cfg.Logo));
            var row = new Document
            {
                Type = type, SkillId = skill.SkillId, Series = series, Seq = seq, Number = numbered.Number,
                Title = numbered.Title, Currency = numbered.Currency, Subtotal = numbered.Totals.Subtotal,
                Discount = numbered.Totals.Discount, Tax = numbered.Totals.Tax, Total = numbered.Totals.Total,
                Data = DataOf(numbered, cfg), ContactId = contactId, ThreadId = thread?.Id, EntityId = entityId,
                CreatedBy = createdBy, Mime = "application/pdf",
                FileName = SafeFileName(numbered.Number.Length > 0 ? numbered.Number : type) + ".pdf",
                Status = createdBy == "ai" ? DocumentStatus.Pending : DocumentStatus.Issued,
            };
            try
            {
                return Store(row, thread, bytes, ".pdf");
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                _db.ChangeTracker.Clear(); // somebody else took that number — try the next one
            }
        }
    }

    private Document Store(Document row, Thread? thread, byte[] bytes, string ext)
    {
        var channel = thread?.Channel ?? "documents";
        var key = thread?.Key ?? (row.ContactId is int cid ? $"contact-{cid}" : "unassigned");
        // Task.Run: off any captured sync context, so the blocking wait can never deadlock
        row.FilePath = Task.Run(() => _media.SaveAsync(channel, key, bytes, ext)).GetAwaiter().GetResult();
        row.FileChannel = channel;
        row.SizeBytes = bytes.LongLength;
        row.PublicToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        row.CreatedAt = row.UpdatedAt = Now;
        _db.Documents.Add(row);
        try
        {
            _db.SaveChanges();
        }
        catch
        {
            try { if (_media.Resolve(channel, row.FilePath) is { } f) File.Delete(f); } catch { }
            throw;
        }
        return row;
    }

    private static Dictionary<string, object?> DataOf(DocumentContent c, DocumentAppConfig cfg) => new()
    {
        ["issue_date"] = c.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["due_date"] = c.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["valid_until"] = c.ValidUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["customer"] = new Dictionary<string, object?>
        {
            ["name"] = c.Customer.Name, ["company"] = c.Customer.Company, ["address"] = c.Customer.Address,
            ["email"] = c.Customer.Email, ["phone"] = c.Customer.Phone,
        },
        ["lines"] = c.Lines.Select(l => new Dictionary<string, object?>
        {
            ["description"] = l.Description, ["qty"] = l.Qty, ["unit_price"] = l.UnitPrice,
            ["discount"] = l.Discount, ["discount_label"] = l.DiscountLabel, ["amount"] = l.Amount,
        }).ToList(),
        ["tax_label"] = cfg.TaxLabel,
        ["tax_rate"] = cfg.TaxRate,
        ["notes"] = c.Notes,
        ["reference"] = c.Reference,
        ["payment_method"] = c.PaymentMethod,
        ["paragraphs"] = c.Paragraphs.ToList(),
        ["language"] = c.Language,
    };

    /// <summary>The stored row's numbers as content (what the tool reports back).</summary>
    private static DocumentContent? ContentOf(Document d)
    {
        try
        {
            var el = JsonSerializer.SerializeToElement(d.Data);
            var lines = el.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array
                ? ls.EnumerateArray().Select(l => new DocLine(l.GetProperty("description").GetString() ?? "",
                    l.GetProperty("qty").GetDecimal(), l.GetProperty("unit_price").GetDecimal(),
                    l.GetProperty("discount").GetDecimal(), l.GetProperty("discount_label").GetString() ?? "",
                    l.GetProperty("amount").GetDecimal())).ToList()
                : new List<DocLine>();
            DateOnly? D(string k) => el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? DateOnly.ParseExact(v.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            return new DocumentContent
            {
                Type = d.Type, Number = d.Number, Title = d.Title, Currency = d.Currency, Lines = lines,
                Totals = new DocTotals(d.Subtotal, d.Discount, d.Tax, d.Total),
                IssueDate = D("issue_date") ?? DateOnly.FromDateTime(d.CreatedAt), DueDate = D("due_date"),
                ValidUntil = D("valid_until"),
            };
        }
        catch { return null; }
    }

    private static string ToolPayload(DocumentContent c, DocumentAppConfig cfg, string file, bool preview)
    {
        var p = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["status"] = preview ? "preview" : "ready",
            ["document"] = DocumentTypes.Label(c.Type),
            ["number"] = c.Number,
            ["file"] = file,
        };
        if (c.Type != DocumentTypes.Letter)
        {
            p["currency"] = c.Currency;
            p["items"] = c.Lines.Select(l => new Dictionary<string, object?>
            {
                ["description"] = l.Description, ["qty"] = l.Qty, ["unit_price"] = DocumentMath.Money(l.UnitPrice),
                ["discount"] = l.Discount > 0 ? DocumentMath.Money(l.Discount) : null, ["amount"] = DocumentMath.Money(l.Amount),
            }).ToList();
            p["subtotal"] = DocumentMath.Money(c.Totals.Subtotal);
            if (cfg.TaxRate > 0)
            {
                p["tax_label"] = cfg.TaxLabel.Length > 0 ? cfg.TaxLabel : "Tax";
                p["tax_rate_percent"] = cfg.TaxRate;
                p["tax"] = DocumentMath.Money(c.Totals.Tax);
            }
            p["total"] = DocumentMath.Money(c.Totals.Total);
            p["total_label"] = $"{c.Currency} {DocumentMath.Money(c.Totals.Total)}";
            if (c.DueDate is DateOnly due) p["due_date"] = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (c.ValidUntil is DateOnly vu) p["valid_until"] = vu.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        p["instructions"] = (preview ? "(AI playground: a preview — nothing is sent or kept.) " : "")
                            + "The PDF goes to the customer right after your reply. In your reply, mention the "
                            + $"{DocumentTypes.Label(c.Type).ToLowerInvariant()} number"
                            + (c.Type == DocumentTypes.Letter ? "" : " and the total exactly as given here")
                            + "; don't list every item again.";
        return JsonSerializer.Serialize(p);
    }

    // =========================================================================
    // the reply's lifecycle: bind → deliver | release
    // =========================================================================

    /// <summary>A held / auto-sent draft owns its documents (so the monitor shows them).</summary>
    public void Bind(IEnumerable<int> ids, int draftId)
    {
        try
        {
            var list = ids.Distinct().ToList();
            if (list.Count == 0) return;
            foreach (var d in _db.Documents.Where(d => list.Contains(d.Id) && d.DraftId == null))
                d.DraftId = draftId;
            _db.SaveChanges();
        }
        catch { /* binding is bookkeeping only */ }
    }

    /// <summary>The reply will never be delivered as written: its pending documents are void.</summary>
    public int Release(IEnumerable<int> ids, string reason)
    {
        try
        {
            var list = ids.Distinct().ToList();
            if (list.Count == 0) return 0;
            var rows = _db.Documents.Where(d => list.Contains(d.Id) && d.Status == DocumentStatus.Pending).ToList();
            foreach (var d in rows)
            {
                d.Status = DocumentStatus.Void;
                d.Detail = Cap(reason, 500);
                d.UpdatedAt = Now;
            }
            _db.SaveChanges();
            return rows.Count;
        }
        catch { return 0; }
    }

    public int Release(Dictionary<string, object?>? draftContext, string reason) =>
        Release(AiAttachments.From(draftContext), reason);

    /// <summary>
    /// Send a reply's documents after its text: the channel's real media send where
    /// the channel carries files, a secure download link where it doesn't (and as
    /// the fallback when a file send is refused). Each is sent once (pending →
    /// sent); a note + audit row records it. Never throws.
    /// </summary>
    public List<Message> Deliver(IEnumerable<int> ids, Thread thread, int? accountId, OutboundSender sender,
        string source, string actor)
    {
        var sent = new List<Message>();
        foreach (var id in ids.Distinct())
        {
            try
            {
                var doc = _db.Documents.FirstOrDefault(d => d.Id == id);
                if (doc == null || doc.Status is not (DocumentStatus.Pending or DocumentStatus.Issued)) continue;
                var msg = SendOne(doc, thread, accountId, sender, source);
                if (msg != null) sent.Add(msg);
                Record(doc, thread, actor);
            }
            catch { /* one document never stops the next or the reply */ }
        }
        return sent;
    }

    /// <summary>The channels whose outbound path carries a FILE (everything else gets a link).</summary>
    public bool CarriesFiles(Thread thread, OutboundSender sender) => thread.Channel switch
    {
        ChatBranch.Domain.Channels.WhatsApp or ChatBranch.Domain.Channels.Facebook
            or ChatBranch.Domain.Channels.Instagram or ChatBranch.Domain.Channels.Telegram
            or ChatBranch.Domain.Channels.Email => true,
        _ => MoreChannels.Handles(thread.Channel) && sender.ChannelCarriesFiles(thread.Channel),
    };

    private Message? SendOne(Document doc, Thread thread, int? accountId, OutboundSender sender, string source)
    {
        var label = Label(doc);
        var full = _media.Resolve(doc.FileChannel, doc.FilePath);
        var mediaType = doc.Mime.StartsWith("image/") ? "image" : "document";
        Message? msg = null;
        var how = "file";
        if (full != null && CarriesFiles(thread, sender))
        {
            // the file lives in the conversation's own media root when it was made
            // for this conversation; otherwise copy it there first
            var rel = doc.FilePath;
            if (doc.FileChannel != thread.Channel || _media.Resolve(thread.Channel, rel) == null)
                rel = Task.Run(() => _media.SaveAsync(thread.Channel, thread.Key, File.ReadAllBytes(full),
                    Path.GetExtension(doc.FileName))).GetAwaiter().GetResult();
            var abs = _media.Resolve(thread.Channel, rel)!;
            var caption = doc.Type == DocumentTypes.Qr ? doc.Title : $"📄 {label}";
            msg = thread.Channel is ChatBranch.Domain.Channels.Email or ChatBranch.Domain.Channels.Telegram
                ? sender.Send(thread, caption.Length > 0 ? caption : $"[{mediaType}]", source, accountId,
                    media: new OutboundMedia(abs, rel, mediaType, doc.FileName))
                : sender.SendMedia(thread, rel, mediaType, doc.Mime, caption.Length > 0 ? caption : null, doc.FileName,
                    source, accountId);
            if (msg.Status == "failed")
            {
                doc.Detail = Cap($"file refused: {msg.Text}", 500);
                msg = null; // fall back to the link
            }
        }
        if (msg == null)
        {
            how = "link";
            var text = doc.Type == DocumentTypes.Qr
                ? $"{(doc.Title.Length > 0 ? doc.Title + " — " : "")}QR code: {PublicUrl(doc)}"
                : $"📄 {label}: {PublicUrl(doc)}";
            msg = sender.Send(thread, text, source, accountId);
        }
        doc.MessageId = msg.Id;
        doc.UpdatedAt = Now;
        if (msg.Status == "failed")
        {
            doc.Status = DocumentStatus.Failed;
            doc.Detail = Cap($"{(doc.Detail.Length > 0 ? doc.Detail + "; " : "")}link refused: {msg.Text}", 500);
        }
        else
        {
            doc.Status = DocumentStatus.Sent;
            doc.SentAt = Now;
            doc.Detail = doc.Detail.Length > 0 ? Cap($"{how} ({doc.Detail})", 500) : how;
            if (doc.ThreadId == null) doc.ThreadId = thread.Id;
        }
        _db.SaveChanges();
        return msg;
    }

    private void Record(Document doc, Thread thread, string actor)
    {
        try
        {
            var ok = doc.Status == DocumentStatus.Sent;
            var money = doc.Type is DocumentTypes.Letter or DocumentTypes.Qr ? "" : $" ({doc.Currency} {DocumentMath.Money(doc.Total)})";
            var via = doc.Detail.StartsWith("link") ? " as a download link" : "";
            Audit.AuditTrail.Add(_db, ok ? "document.sent" : "document.failed", $"document:{doc.Id}",
                $"{Label(doc)}{money} → {thread.Channel}:{thread.Key}{via}" + (ok ? "" : $" — {doc.Detail}"),
                actor: actor, entityId: doc.EntityId);
            _db.Messages.Add(new Message
            {
                ThreadId = thread.Id, Direction = MessageDirection.Out, Source = MessageSource.System,
                Private = true, Status = "note", AccountId = thread.AccountId, CreatedAt = DateTime.UtcNow,
                Text = ok ? $"📄 {Label(doc)} sent{money}{via}" : $"📄 {Label(doc)} could not be sent: {doc.Detail}",
            });
            var tracked = _db.Threads.FirstOrDefault(t => t.Id == thread.Id);
            if (tracked != null) tracked.UpdatedAt = DateTime.UtcNow;
            _db.SaveChanges();
        }
        catch { /* the record is a side effect */ }
    }

    public static string Label(Document d) => d.Type == DocumentTypes.Qr
        ? "QR code"
        : $"{DocumentTypes.Label(d.Type)} {d.Number}".Trim();

    // =========================================================================
    // files: download links, previews, logos
    // =========================================================================

    /// <summary>The customer's download link (public, unguessable, <see cref="LinkLifetime"/>).</summary>
    public string PublicUrl(Document d) => $"{PublicBase(_scoping)}/api/documents/public/{d.PublicToken}";

    /// <summary>Setting public_base_url → PUBLIC_BASE_URL → FRONTEND_URL (a single-origin proxy forwards /api/) → localhost.</summary>
    public static string PublicBase(ScopingService sc)
    {
        var b = "";
        try { b = sc.SettingValue("public_base_url").Trim(); } catch { }
        if (b.Length == 0) b = (Environment.GetEnvironmentVariable("PUBLIC_BASE_URL") ?? "").Trim();
        if (b.Length == 0) b = (Environment.GetEnvironmentVariable("FRONTEND_URL") ?? "").Trim();
        if (b.Length == 0) b = "http://localhost:8090";
        return b.TrimEnd('/');
    }

    /// <summary>A public token → the document, while its link is valid (voided documents never).</summary>
    public Document? ByPublicToken(string token)
    {
        if (!Regex.IsMatch(token ?? "", "^[a-f0-9]{32}$")) return null;
        var d = _db.Documents.AsNoTracking().FirstOrDefault(x => x.PublicToken == token);
        if (d == null || d.Status is DocumentStatus.Void or DocumentStatus.Pending) return null;
        return Now - d.CreatedAt > LinkLifetime ? null : d;
    }

    public string? FileOf(Document d) => _media.Resolve(d.FileChannel, d.FilePath);

    private string PreviewDir => Path.Combine(_media.DataDir, "documents", "preview");

    private async Task<(string Url, string File)> SavePreviewAsync(byte[] bytes, string ext, string name,
        CancellationToken ct)
    {
        Directory.CreateDirectory(PreviewDir);
        CleanPreviews();
        var id = Guid.NewGuid().ToString("N") + ext;
        await File.WriteAllBytesAsync(Path.Combine(PreviewDir, id), bytes, ct);
        return ($"/api/documents/preview/{id}", SafeFileName(name.Length > 0 ? name : "preview") + ext);
    }

    public sealed record SamplePreview(string? Url, string Number, decimal? Total, string? Error);

    /// <summary>The skill form's "Preview PDF": a sample document with unsaved settings (no number used).</summary>
    public async Task<SamplePreview> PreviewSampleAsync(string type, DocumentAppConfig cfg, DateTimeOffset utcNow,
        TimeZoneInfo tz, CancellationToken ct = default)
    {
        var sample = type == DocumentTypes.Letter
            ? JsonSerializer.SerializeToElement(new
            {
                customer_name = "Aisha Rahman", title = "Confirmation of your appointment",
                paragraphs = new[]
                {
                    "Thank you for choosing us. This letter confirms your appointment on Monday at 10:00.",
                    "Please arrive ten minutes early and bring this letter with you. Reply to this message if you need to change the time.",
                },
            })
            : JsonSerializer.SerializeToElement(new
            {
                customer_name = "Aisha Rahman", customer_company = "Example Trading Sdn Bhd",
                customer_email = "aisha@example.com",
                items = new object[]
                {
                    new { description = "Sample service", qty = 2, unit_price = 50 },
                    new { description = "Sample product", qty = 1, unit_price = 120, discount = "10%" },
                },
                notes = "This is a sample — no number was used.",
                payment_method = type == DocumentTypes.Receipt ? "Bank transfer" : null,
            });
        var build = Build(type, sample, cfg, null, utcNow, tz, cfg.FormatNumber(type, cfg.StartNumber(type)));
        if (build.Error != null) return new SamplePreview(null, "", null, build.Error);
        var bytes = DocumentRenderer.Render(build.Content!, cfg, LoadLogo(cfg.Logo));
        var (url, _) = await SavePreviewAsync(bytes, ".pdf", build.Content!.Number, ct);
        return new SamplePreview(url, build.Content.Number, type == DocumentTypes.Letter ? null : build.Content.Totals.Total, null);
    }

    public async Task<string> PreviewQrAsync(string text, QrAppConfig cfg, CancellationToken ct = default) =>
        (await SavePreviewAsync(QrPng(text, cfg.PixelsPerModule), ".png", "qr-code", ct)).Url;

    /// <summary>A preview file by its id (32 hex + .pdf/.png), null when missing.</summary>
    public string? PreviewFile(string id)
    {
        if (!Regex.IsMatch(id ?? "", @"^[a-f0-9]{32}\.(pdf|png)$")) return null;
        var p = Path.Combine(PreviewDir, id!);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Previews are throw-away: anything older than a day goes.</summary>
    private void CleanPreviews()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(PreviewDir))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f);
        }
        catch { }
    }

    private string LogoDir => Path.Combine(_media.DataDir, "documents", "logos");

    public static bool IsLogoName(string name) => Regex.IsMatch(name ?? "", @"^[a-f0-9]{32}\.(png|jpg)$");

    public string? LogoPath(string name)
    {
        if (!IsLogoName(name)) return null;
        var p = Path.Combine(LogoDir, name);
        return File.Exists(p) ? p : null;
    }

    private byte[]? LoadLogo(string name)
    {
        try { return LogoPath(name) is { } p ? File.ReadAllBytes(p) : null; }
        catch { return null; }
    }

    /// <summary>Store an uploaded logo (PNG or JPEG by its magic bytes, ≤ 1 MB); the name goes in the config.</summary>
    public string? SaveLogo(byte[] bytes, out string? error)
    {
        error = null;
        if (bytes.Length == 0 || bytes.Length > MaxLogoBytes) { error = "The logo must be a PNG or JPEG up to 1 MB."; return null; }
        var ext = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 ? ".png"
            : bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF ? ".jpg" : null;
        if (ext == null) { error = "The logo must be a PNG or JPEG image."; return null; }
        Directory.CreateDirectory(LogoDir);
        var name = Guid.NewGuid().ToString("N") + ext;
        File.WriteAllBytes(Path.Combine(LogoDir, name), bytes);
        return name;
    }

    // =========================================================================
    // payloads
    // =========================================================================

    /// <summary>The wire shape of a document (lists, the monitor, the chat).</summary>
    public static Dictionary<string, object?> Payload(Document d) => new()
    {
        ["id"] = d.Id,
        ["type"] = d.Type,
        ["label"] = Label(d),
        ["number"] = d.Number,
        ["title"] = d.Title,
        ["status"] = d.Status,
        ["currency"] = d.Currency,
        ["total"] = d.Type is DocumentTypes.Letter or DocumentTypes.Qr ? null : d.Total,
        ["file_name"] = d.FileName,
        ["url"] = $"/api/documents/{d.Id}/file",
        ["thread_id"] = d.ThreadId,
        ["contact_id"] = d.ContactId,
        ["draft_id"] = d.DraftId,
        ["created_by"] = d.CreatedBy,
        ["detail"] = d.Detail,
        ["created_at"] = DateTime.SpecifyKind(d.CreatedAt, DateTimeKind.Utc).ToString("o"),
        ["sent_at"] = d.SentAt is DateTime s ? DateTime.SpecifyKind(s, DateTimeKind.Utc).ToString("o") : null,
    };

    // =========================================================================
    // helpers
    // =========================================================================

    private static ToolOutcome Ok(string json) => new(json.Length <= 4000 ? json : json[..4000], true);

    private static ToolOutcome Refused(string error, string advice) =>
        new(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ok"] = false, ["error"] = error, ["advice"] = advice.Length > 0 ? advice : null,
        }), false);

    private static JsonElement ParseArgs(string? json)
    {
        try
        {
            var el = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone();
            return el.ValueKind == JsonValueKind.Object ? el : JsonDocument.Parse("{}").RootElement.Clone();
        }
        catch { throw new ArgumentException("arguments are not valid JSON"); }
    }

    private static string? Arg(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    public static string SafeFileName(string s)
    {
        var clean = new string((s ?? "").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.').ToArray())
            .Trim('.');
        return clean.Length == 0 ? "document" : clean.Length > 60 ? clean[..60] : clean;
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>Content helpers.</summary>
internal static class DocumentContentExtensions
{
    public static DocumentContent With(this DocumentContent c, string number) => new()
    {
        Type = c.Type, Number = number, Title = c.Title, IssueDate = c.IssueDate, DueDate = c.DueDate,
        ValidUntil = c.ValidUntil, Customer = c.Customer, Lines = c.Lines, Totals = c.Totals, Currency = c.Currency,
        Notes = c.Notes, Reference = c.Reference, PaymentMethod = c.PaymentMethod, Paragraphs = c.Paragraphs,
        Closing = c.Closing, Language = c.Language,
    };
}

/// <summary>
/// The files an AI reply carries (<see cref="AiReplyResult.Attachments"/>), as
/// they ride in a draft / job context: <c>attachments: [{type:"document", document_id}]</c>.
/// </summary>
public static class AiAttachments
{
    public static List<Dictionary<string, object?>> ToMeta(IEnumerable<int> ids) =>
        ids.Distinct().Select(id => new Dictionary<string, object?> { ["type"] = "document", ["document_id"] = id }).ToList();

    public static List<int> From(Dictionary<string, object?>? ctx)
    {
        var list = new List<int>();
        if (ctx == null) return list;
        try
        {
            foreach (var item in AiGate.Items(ctx, "attachments"))
                if (AiGate.Num(item, "document_id") is double id) list.Add((int)id);
        }
        catch { }
        return list.Distinct().ToList();
    }
}
