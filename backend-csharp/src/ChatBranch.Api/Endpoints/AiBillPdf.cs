using System.Globalization;
using ChatBranch.Data.Documents;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using PdfDocumentRenderer = MigraDoc.Rendering.PdfDocumentRenderer;
using DocumentRenderer = ChatBranch.Data.Documents.DocumentRenderer;
using MigraDocument = MigraDoc.DocumentObjectModel.Document;

namespace ChatBranch.Api.Endpoints;

/// <summary>One company's line on the bill. Cost, markup and margin are null unless the caller holds billing.manage.</summary>
internal sealed record BillCompany(int Id, string Name, string Slug, int Requests, long Tokens, double UsageAmount,
    double MonthlyFee, double Total, double? CostUsd, double? MarkupPct, double? Margin,
    // the price book that priced this company's month, and where it came from
    // (ChatBranch.Data.Ai.AiPriceBooks) — null/markup when nothing but the defaults applied
    string? PlanSlug = null, string? PlanLabel = null, string Mode = "markup", bool PlanInherited = false);

internal sealed record BillLine(string Feature, int Requests, long Tokens, double Amount, double? CostUsd);

internal sealed record BillDay(string Date, double Amount);

internal sealed record BillDetail(BillCompany Summary, List<BillLine> Lines, List<BillDay> Daily);

internal sealed record BillUnattributed(int Requests, long Tokens, double CostUsd);

/// <summary>The month's bill as one caller may see it (<see cref="AiBillingEndpoints.BuildBillAsync"/>).</summary>
internal sealed record BillReport(string Month, DateTime From, DateTime To, string Currency, bool Manager,
    List<BillCompany> Companies, BillDetail? Company, BillUnattributed? Unattributed, double? UsdRate);

/// <summary>
/// The AI bill as an A4 PDF (MigraDoc / PDFsharp, the embedded fonts of
/// <see cref="DocumentFonts"/>): a header with the issuer, the company (or "All
/// companies"), the month and when it was generated; then one company's summary,
/// its lines by feature and the days with usage — or every visible company with a
/// total row. Cost, markup, margin and the unattributed bucket appear only when the
/// bill carries them (billing managers), exactly like the JSON. Labels: English by
/// default, Malay for <c>ms</c>, Chinese for <c>zh</c> only when a CJK font is
/// available (else English — the Documents rule). Pure: bill in, bytes out.
/// </summary>
internal static class AiBillPdf
{
    public static byte[] Render(BillReport bill, string issuer, string? lang, DateTime nowUtc)
    {
        DocumentFonts.EnsureInstalled();
        var language = (lang ?? "").Trim().ToLowerInvariant() switch
        {
            "ms" => "ms",
            "zh" when DocumentFonts.CjkAvailable => "zh",
            _ => "en",
        };
        var L = Labels.For(language);
        var texts = new List<string> { issuer };
        texts.AddRange(bill.Companies.Select(c => c.Name));
        if (bill.Company != null) texts.Add(bill.Company.Summary.Name);
        var useCjk = DocumentFonts.CjkAvailable && (language == "zh" || texts.Any(DocumentFonts.HasCjk));

        var ink = new Color(0x1F, 0x23, 0x28);
        var muted = new Color(0x6B, 0x72, 0x80);
        var line = new Color(0xE5, 0xE7, 0xEB);
        var head = new Color(0xF3, 0xF4, 0xF6);

        var doc = new MigraDocument();
        var who = bill.Company?.Summary.Name ?? L.AllCompanies;
        doc.Info.Title = DocumentRenderer.Clean($"{L.Title} — {who} — {MonthLabel(bill.From, language)}");
        doc.Info.Author = DocumentRenderer.Clean(issuer.Length > 0 ? issuer : "ChatBranch");
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = useCjk ? DocumentFonts.Cjk : DocumentFonts.Sans;
        normal.Font.Size = 9.5;
        normal.Font.Color = ink;
        normal.ParagraphFormat.SpaceAfter = 0;

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromMillimeter(16);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(20);
        section.PageSetup.LeftMargin = Unit.FromMillimeter(18);
        section.PageSetup.RightMargin = Unit.FromMillimeter(18);
        section.PageSetup.FooterDistance = Unit.FromMillimeter(9);
        var width = Unit.FromMillimeter(210 - 36);

        // ---- footer: what this is + page x of y
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5;
        footer.Format.Font.Color = muted;
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText(DocumentRenderer.Clean($"{L.Title} · {who} · {MonthLabel(bill.From, language)}   ·   {L.Page} "));
        footer.AddPageField();
        footer.AddText($" {L.Of} ");
        footer.AddNumPagesField();

        // ---- header: issuer + title (left), month / period / generated (right)
        var top = section.AddTable();
        top.Borders.Visible = false;
        top.AddColumn(width * 0.58);
        top.AddColumn(width * 0.42);
        var hr = top.AddRow();
        var left = hr.Cells[0];
        var biz = left.AddParagraph(DocumentRenderer.Clean(issuer.Length > 0 ? issuer : "ChatBranch"));
        biz.Format.Font.Size = 9;
        biz.Format.Font.Color = muted;
        biz.Format.SpaceAfter = Unit.FromPoint(3);
        var title = left.AddParagraph(L.Title.ToUpperInvariant());
        title.Format.Font.Size = 16;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromPoint(3);
        var name = left.AddParagraph(DocumentRenderer.Clean(who));
        name.Format.Font.Size = 12;
        name.Format.Font.Bold = true;

        var right = hr.Cells[1];
        right.Format.Alignment = ParagraphAlignment.Right;
        KeyValue(right, L.Month, MonthLabel(bill.From, language), muted);
        KeyValue(right, L.Period, $"{DateLabel(bill.From, language)} – {DateLabel(bill.To.AddDays(-1), language)}", muted);
        KeyValue(right, L.Currency, bill.Currency, muted);
        KeyValue(right, L.Generated, $"{DateLabel(nowUtc, language)} {nowUtc.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC", muted);

        var rule = section.AddParagraph();
        rule.Format.SpaceBefore = Unit.FromMillimeter(3);
        rule.Format.SpaceAfter = Unit.FromMillimeter(5);
        rule.Format.Borders.Bottom.Color = ink;
        rule.Format.Borders.Bottom.Width = 1;

        string Money(double v) => Amount(bill.Currency, v);
        if (bill.Company is { } detail) OneCompany(section, bill, detail, L, Money, muted, line, head, width);
        else AllCompanies(section, bill, L, Money, muted, line, head, width);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    // ---- one company: summary, lines by feature, the days with usage --------------------

    private static void OneCompany(Section section, BillReport bill, BillDetail d, Labels L, Func<double, string> money,
        Color muted, Color line, Color head, Unit width)
    {
        var s = d.Summary;
        Heading(section, L.Summary, muted);
        var sum = section.AddTable();
        sum.Borders.Visible = false;
        sum.TopPadding = Unit.FromPoint(3);
        sum.BottomPadding = Unit.FromPoint(3);
        sum.AddColumn(width * 0.6);
        sum.AddColumn(width * 0.4);
        void Row(string label, string value, bool strong = false)
        {
            var r = sum.AddRow();
            r.Borders.Bottom.Color = line;
            r.Borders.Bottom.Width = 0.5;
            var lp = r.Cells[0].AddParagraph(label);
            var v = r.Cells[1].AddParagraph(value);
            v.Format.Alignment = ParagraphAlignment.Right;
            if (!strong) return;
            lp.Format.Font.Bold = true;
            lp.Format.Font.Size = 11;
            v.Format.Font.Bold = true;
            v.Format.Font.Size = 11;
        }
        Row(L.Requests, s.Requests.ToString("#,##0", CultureInfo.InvariantCulture));
        Row(L.Tokens, s.Tokens.ToString("#,##0", CultureInfo.InvariantCulture));
        Row(L.Usage, money(s.UsageAmount));
        Row(L.MonthlyFee, money(s.MonthlyFee));
        Row(L.Total, money(s.Total), strong: true);
        // how the month was priced (the JSON's plan_label / plan_inherited / mode — for everyone);
        // the markup PERCENTAGE only for billing managers, and only when a markup priced it
        if (!string.IsNullOrWhiteSpace(s.PlanLabel)) Row(L.Plan, PlanText(s, L));
        Row(L.Pricing, PriceText(s, L));
        // billing managers only — the JSON carries these as null for everyone else
        if (s.CostUsd is double cost) Row(L.Cost, Amount("USD", cost));
        if (s.Margin is double margin) Row(L.Margin, money(margin));

        Heading(section, L.ByFeature, muted);
        var manager = d.Lines.Any(l => l.CostUsd != null) || s.CostUsd != null;
        var cols = manager ? new[] { 0.40, 0.13, 0.17, 0.15, 0.15 } : new[] { 0.46, 0.16, 0.19, 0.19 };
        var t = Table(section, width, cols);
        var headers = new List<string> { L.Item, L.Requests, L.Tokens };
        if (manager) headers.Add(L.Cost);
        headers.Add(L.Amount);
        Header(t, headers, head);
        if (d.Lines.Count == 0)
        {
            var r = t.AddRow();
            r.Cells[0].MergeRight = headers.Count - 1;
            var p = r.Cells[0].AddParagraph(L.NoUsage);
            p.Format.Font.Color = muted;
        }
        foreach (var l in d.Lines)
        {
            var cells = new List<string>
            {
                L.Feature(l.Feature),
                l.Requests.ToString("#,##0", CultureInfo.InvariantCulture),
                l.Tokens.ToString("#,##0", CultureInfo.InvariantCulture),
            };
            if (manager) cells.Add(Amount("USD", l.CostUsd ?? 0));
            cells.Add(money(l.Amount));
            Body(t, cells, line);
        }
        var fee = new List<string> { L.MonthlyFee, "", "" };
        if (manager) fee.Add("");
        fee.Add(money(s.MonthlyFee));
        Body(t, fee, line);
        var total = new List<string> { L.Total, s.Requests.ToString("#,##0", CultureInfo.InvariantCulture),
            s.Tokens.ToString("#,##0", CultureInfo.InvariantCulture) };
        if (manager) total.Add(Amount("USD", s.CostUsd ?? 0));
        total.Add(money(s.Total));
        Body(t, total, line, strong: true);

        // the days with usage (at most 31 rows — short enough to print in full)
        var days = d.Daily.Where(x => x.Amount != 0).ToList();
        if (days.Count > 0)
        {
            Heading(section, L.Daily, muted);
            var dt = Table(section, width, new[] { 0.5, 0.5 });
            Header(dt, new List<string> { L.Date, L.Amount }, head);
            foreach (var x in days)
            {
                var date = DateTime.ParseExact(x.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                Body(dt, new List<string> { DateLabel(date, L.Language), money(x.Amount) }, line);
            }
        }
    }

    // ---- every company the caller may see, with a total row ------------------------------

    private static void AllCompanies(Section section, BillReport bill, Labels L, Func<double, string> money,
        Color muted, Color line, Color head, Unit width)
    {
        Heading(section, L.Companies, muted);
        var manager = bill.Manager;
        // "Pricing" = the mode (and plan) that priced each company's month — a flat- or
        // unit-priced company never reads as "0%" markup
        var cols = manager
            ? new[] { 0.24, 0.09, 0.14, 0.13, 0.16, 0.12, 0.12 }
            : new[] { 0.32, 0.12, 0.19, 0.18, 0.19 };
        var t = Table(section, width, cols);
        var headers = manager
            ? new List<string> { L.Company, L.Requests, L.Usage, L.Cost, L.Pricing, L.Total, L.Margin }
            : new List<string> { L.Company, L.Requests, L.Usage, L.Pricing, L.Total };
        Header(t, headers, head);
        if (bill.Companies.Count == 0)
        {
            var r = t.AddRow();
            r.Cells[0].MergeRight = headers.Count - 1;
            r.Cells[0].AddParagraph(L.NoCompanies).Format.Font.Color = muted;
        }
        foreach (var c in bill.Companies)
        {
            var cells = manager
                ? new List<string>
                {
                    c.Name, c.Requests.ToString("#,##0", CultureInfo.InvariantCulture), money(c.UsageAmount),
                    Amount("USD", c.CostUsd ?? 0), PriceWithPlan(c, L),
                    money(c.Total), money(c.Margin ?? 0),
                }
                : new List<string>
                {
                    c.Name, c.Requests.ToString("#,##0", CultureInfo.InvariantCulture), money(c.UsageAmount),
                    PriceWithPlan(c, L), money(c.Total),
                };
            Body(t, cells, line);
        }
        var req = bill.Companies.Sum(c => c.Requests).ToString("#,##0", CultureInfo.InvariantCulture);
        var usage = money(Math.Round(bill.Companies.Sum(c => c.UsageAmount), 4));
        var total = money(Math.Round(bill.Companies.Sum(c => c.Total), 4));
        Body(t, manager
            ? new List<string>
            {
                L.Total, req, usage, Amount("USD", Math.Round(bill.Companies.Sum(c => c.CostUsd ?? 0), 4)), "",
                total, money(Math.Round(bill.Companies.Sum(c => c.Margin ?? 0), 4)),
            }
            : new List<string> { L.Total, req, usage, "", total }, line, strong: true);

        if (bill.Unattributed is { Requests: > 0 } u)
        {
            var p = section.AddParagraph(string.Format(CultureInfo.InvariantCulture, L.Unattributed,
                u.Requests.ToString("#,##0", CultureInfo.InvariantCulture), Amount("USD", u.CostUsd)));
            p.Format.Font.Size = 8.5;
            p.Format.Font.Color = muted;
            p.Format.SpaceBefore = Unit.FromMillimeter(4);
        }
    }

    // ---- pricing -------------------------------------------------------------------------

    /// <summary>
    /// How a company's month was priced, in words: "Markup 25%" (the percentage only when
    /// the bill carries it — billing managers), "Flat", "Per unit". Without the percentage a
    /// markup reads "Usage-based": the client sees that usage is billed, not the margin.
    /// </summary>
    internal static string PriceText(BillCompany c, Labels L) => c.Mode switch
    {
        ChatBranch.Domain.AiPriceModes.Flat => L.ModeFlat,
        ChatBranch.Domain.AiPriceModes.Unit => L.ModeUnit,
        _ => c.MarkupPct is double pct
            ? $"{L.Markup} {pct.ToString("0.##", CultureInfo.InvariantCulture)}%"
            : L.ModeUsage,
    };

    /// <summary>The plan's name, "(inherited)" when it came from a level above the company.</summary>
    internal static string PlanText(BillCompany c, Labels L) =>
        string.IsNullOrWhiteSpace(c.PlanLabel) ? ""
        : c.PlanInherited ? $"{c.PlanLabel!.Trim()} ({L.Inherited})" : c.PlanLabel!.Trim();

    private static string PriceWithPlan(BillCompany c, Labels L) =>
        PlanText(c, L) is { Length: > 0 } plan ? $"{PriceText(c, L)} · {plan}" : PriceText(c, L);

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>The bill's money rule: 2 decimals, 4 below 1 so a light month never reads as 0.</summary>
    internal static string Amount(string currency, double v)
    {
        var fmt = v != 0 && Math.Abs(v) < 1 ? "#,##0.0000" : "#,##0.00";
        return $"{currency} {v.ToString(fmt, CultureInfo.InvariantCulture)}";
    }

    private static Table Table(Section section, Unit width, double[] cols)
    {
        var t = section.AddTable();
        t.Borders.Visible = false;
        t.TopPadding = Unit.FromPoint(3.5);
        t.BottomPadding = Unit.FromPoint(3.5);
        foreach (var f in cols) t.AddColumn(width * f);
        return t;
    }

    private static void Header(Table t, List<string> headers, Color head)
    {
        var r = t.AddRow();
        r.HeadingFormat = true;   // repeats on every page
        r.Shading.Color = head;
        for (var i = 0; i < headers.Count; i++)
        {
            var p = r.Cells[i].AddParagraph(headers[i]);
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 8.5;
            if (i > 0) p.Format.Alignment = ParagraphAlignment.Right;
        }
    }

    private static void Body(Table t, List<string> cells, Color line, bool strong = false)
    {
        var r = t.AddRow();
        r.Borders.Bottom.Color = line;
        r.Borders.Bottom.Width = 0.5;
        if (strong)
        {
            r.Borders.Top.Color = new Color(0x1F, 0x23, 0x28);
            r.Borders.Top.Width = 1;
        }
        for (var i = 0; i < cells.Count; i++)
        {
            var p = r.Cells[i].AddParagraph(DocumentRenderer.Clean(cells[i]));
            if (i > 0) p.Format.Alignment = ParagraphAlignment.Right;
            if (strong) p.Format.Font.Bold = true;
        }
    }

    private static void Heading(Section section, string text, Color muted)
    {
        var h = section.AddParagraph(text.ToUpperInvariant());
        h.Format.Font.Size = 8;
        h.Format.Font.Bold = true;
        h.Format.Font.Color = muted;
        h.Format.SpaceBefore = Unit.FromMillimeter(5);
        h.Format.SpaceAfter = Unit.FromPoint(3);
        h.Format.KeepWithNext = true;
    }

    private static void KeyValue(Cell cell, string key, string value, Color muted)
    {
        var p = cell.AddParagraph();
        p.Format.Font.Size = 9;
        p.AddFormattedText(key + "  ").Color = muted;
        p.AddFormattedText(DocumentRenderer.Clean(value)).Bold = true;
    }

    private static readonly string[] MalayMonthsLong =
        { "Januari", "Februari", "Mac", "April", "Mei", "Jun", "Julai", "Ogos", "September", "Oktober", "November", "Disember" };

    internal static string MonthLabel(DateTime d, string language) => language switch
    {
        "ms" => $"{MalayMonthsLong[d.Month - 1]} {d.Year}",
        "zh" => $"{d.Year}年{d.Month}月",
        _ => d.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
    };

    private static string DateLabel(DateTime d, string language) => language switch
    {
        "ms" => DocumentRenderer.DateLabel(DateOnly.FromDateTime(d), "ms"),
        "zh" => $"{d.Year}年{d.Month}月{d.Day}日",
        _ => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
    };

    /// <summary>The printed words (en / ms / zh). Keep in step with the dashboard's billing namespace.</summary>
    internal sealed record Labels(string Language, string Title, string AllCompanies, string Companies, string Company,
        string Month, string Period, string Currency, string Generated, string Summary, string Requests, string Tokens,
        string Usage, string MonthlyFee, string Total, string Cost, string Markup, string Margin, string ByFeature,
        string Item, string Amount, string Daily, string Date, string NoUsage, string NoCompanies, string Unattributed,
        string Page, string Of, IReadOnlyDictionary<string, string> Features)
    {
        // pricing (price books: markup / flat / unit) — init-only so the positional list stays as it was
        public string Pricing { get; init; } = "Pricing";
        public string Plan { get; init; } = "Plan";
        public string Inherited { get; init; } = "inherited";
        public string ModeFlat { get; init; } = "Flat";
        public string ModeUnit { get; init; } = "Per unit";
        public string ModeUsage { get; init; } = "Usage-based";

        public string Feature(string key) => Features.TryGetValue(key, out var v) ? v : key;

        public static readonly Labels En = new("en", "AI usage bill", "All companies", "Companies", "Company",
            "Month", "Period", "Currency", "Generated", "Summary", "Requests", "Tokens", "AI usage",
            "Monthly platform fee", "Total", "Cost (USD)", "Markup", "Margin", "By feature", "Item", "Amount",
            "Daily usage", "Date", "No AI usage this month.", "No companies.",
            "{0} requests ({1}) belong to no company — skill drafting, embeddings and the operator assistant. They are not billed.",
            "Page", "of", new Dictionary<string, string>
            {
                ["chat_reply"] = "Chat replies", ["follow_up"] = "Follow-ups", ["flow_message"] = "Journey messages",
                ["flow_decision"] = "Journey decisions", ["voice_call"] = "Voice calls", ["summary"] = "AI summaries",
                ["timeline"] = "Timeline summaries", ["playground"] = "AI playground", ["assistant"] = "Ask ChatBranch",
                ["embedding"] = "Knowledge indexing", ["skill_draft"] = "Skill drafting", ["document_template"] = "Document templates", ["ai_review"] = "AI review",
            });

        public static readonly Labels Ms = new("ms", "Bil penggunaan AI", "Semua syarikat", "Syarikat", "Syarikat",
            "Bulan", "Tempoh", "Mata wang", "Dijana", "Ringkasan", "Permintaan", "Token", "Penggunaan AI",
            "Yuran platform bulanan", "Jumlah", "Kos (USD)", "Markup", "Margin", "Mengikut ciri", "Perkara", "Amaun",
            "Penggunaan harian", "Tarikh", "Tiada penggunaan AI bulan ini.", "Tiada syarikat.",
            "{0} permintaan ({1}) bukan milik mana-mana syarikat — draf kemahiran, pengindeksan dan pembantu operator. Ia tidak dibilkan.",
            "Muka surat", "daripada", new Dictionary<string, string>
            {
                ["chat_reply"] = "Balasan chat", ["follow_up"] = "Susulan", ["flow_message"] = "Mesej perjalanan",
                ["flow_decision"] = "Keputusan perjalanan", ["voice_call"] = "Panggilan suara", ["summary"] = "Ringkasan AI",
                ["timeline"] = "Ringkasan garis masa", ["playground"] = "Ruang uji AI", ["assistant"] = "Tanya ChatBranch",
                ["embedding"] = "Pengindeksan pengetahuan", ["skill_draft"] = "Draf kemahiran", ["document_template"] = "Templat dokumen", ["ai_review"] = "Semakan AI",
            })
        {
            Pricing = "Harga", Plan = "Pelan", Inherited = "diwarisi", ModeFlat = "Tetap", ModeUnit = "Seunit",
            ModeUsage = "Ikut penggunaan",
        };

        public static readonly Labels Zh = new("zh", "AI 用量账单", "所有公司", "公司", "公司",
            "月份", "期间", "货币", "生成时间", "摘要", "请求", "Token", "AI 用量",
            "平台月费", "合计", "成本（USD）", "加价", "利润", "按功能", "项目", "金额",
            "每日用量", "日期", "本月没有 AI 用量。", "没有公司。",
            "{0} 次请求（{1}）不属于任何公司——技能起草、知识索引和操作员助手。这些不计费。",
            "页码", "/", new Dictionary<string, string>
            {
                ["chat_reply"] = "聊天回复", ["follow_up"] = "跟进", ["flow_message"] = "旅程消息",
                ["flow_decision"] = "旅程判断", ["voice_call"] = "语音通话", ["summary"] = "AI 摘要",
                ["timeline"] = "时间线摘要", ["playground"] = "AI 试验场", ["assistant"] = "问 ChatBranch",
                ["embedding"] = "知识索引", ["skill_draft"] = "技能起草", ["document_template"] = "文档模板", ["ai_review"] = "AI 审查",
            })
        {
            Pricing = "定价", Plan = "方案", Inherited = "继承", ModeFlat = "固定", ModeUnit = "按单位", ModeUsage = "按用量",
        };

        public static Labels For(string language) => language switch { "ms" => Ms, "zh" => Zh, _ => En };
    }
}
