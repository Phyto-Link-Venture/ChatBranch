using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Skills;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Documents;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>A Cloud client that records every text AND media send, in order.</summary>
internal sealed class MediaWhatsApp : IWhatsAppCloudClient
{
    public List<string> Sent { get; } = new();
    public bool FailMedia { get; set; }
    public bool IsConfigured(ChannelAccount account) =>
        account.Provider == Providers.Cloud && account.AccessToken.Length > 0 && account.ExternalId.Length > 0;

    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text, CancellationToken ct = default)
    {
        Sent.Add("text:" + text);
        return Task.FromResult(new SendOutcome(true, $"wamid.{Sent.Count}", ""));
    }

    public Task<SendOutcome> UploadMediaAsync(ChannelAccount account, byte[] bytes, string mime, string filename,
        CancellationToken ct = default)
    {
        Sent.Add($"upload:{mime}:{filename}:{bytes.Length}");
        return Task.FromResult(FailMedia
            ? new SendOutcome(false, "", "(#131053) Media upload error")
            : new SendOutcome(true, "media-1", ""));
    }

    public Task<SendOutcome> SendMediaAsync(ChannelAccount account, string toWaId, string type, string mediaId,
        string? caption, string? filename, CancellationToken ct = default)
    {
        Sent.Add($"media:{type}:{filename}:{caption}");
        return Task.FromResult(new SendOutcome(true, $"wamid.{Sent.Count}", ""));
    }
}

/// <summary>The AI-ops stack with the Documents app wired over a throw-away media dir.</summary>
internal sealed class DocumentRig : IDisposable
{
    /// <summary>Real time: inbound rows are stamped with the wall clock and the 24h window compares them.</summary>
    public static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;
    public TestDb T { get; } = new();
    public AppDbContext Db => T.Db;
    public ManualClock Clock { get; } = new(Start);
    public ToolScriptLlm Llm { get; }
    public MediaWhatsApp Wa { get; } = new();
    public ScopingService Sc { get; }
    public ThreadStore Threads { get; }
    public MediaStore Media { get; }
    public OutboundSender Sender { get; }
    public DocumentService Docs { get; }
    public SkillService Skills { get; }
    public AssistantService Assistant { get; }
    public AiGate Gate { get; }
    public AiResponder Responder { get; }
    public QueueService Queue { get; }
    public InboundProcessor Inbound { get; }
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cb-docs-" + Guid.NewGuid().ToString("N"));

    public DocumentRig(Func<IReadOnlyList<LlmMessage>, IReadOnlyList<LlmTool>?, LlmChatResult>? script = null,
        IHtmlPdfConverter? html = null)
    {
        Llm = new ToolScriptLlm(script ?? Invoice);
        Sc = new ScopingService(Db);
        Sc.SetSetting("org_timezone", "Asia/Kuala_Lumpur");
        Threads = new ThreadStore(Db, Sc);
        Media = new MediaStore(_dir);
        Sender = new OutboundSender(Db, Threads, Wa, new ChatBranch.Data.Sidecars.SandboxBaileysGateway(), media: Media);
        // the custom layout prints through a fake (DocumentHtmlTests) unless a test brings its own
        Docs = new DocumentService(Db, Sc, Media, Clock, html ?? new FakeHtmlPdf());
        Skills = new SkillService(Db, Sc, Clock, documents: Docs);
        Assistant = new AssistantService(Db, Sc, new PersonaService(Sc), new KnowledgeService(Db, Sc), Llm, Skills);
        var composer = new AiComposer(Db, Assistant, Llm);
        Gate = new AiGate(Db, Sc, Threads, Sender, Clock, new RecordingTyping(), documents: Docs);
        Responder = new AiResponder(Db, Sc, composer, Gate, Clock);
        Queue = new QueueService(Db, Sc, Threads, Assistant, Sender, Responder, Gate, Clock);
        Inbound = new InboundProcessor(Db, Threads, Assistant, Sender, new FlowEngine(Db, Sc, Queue), Queue, Sc,
            new AutomationService(Db, Sc), Responder);
        foreach (var (k, v) in new Dictionary<string, string>
                 {
                     ["ai_review_mode"] = "auto", ["ai_review_first_n"] = "0", ["ai_review_notify"] = "false",
                     ["ai_wait_seconds"] = "0", ["ai_typing"] = "false", ["ai_split"] = "off",
                     ["ai_ack_skip"] = "false", ["ai_followup"] = "false", ["ai_reply_mode"] = "instant",
                 })
            Sc.SetSetting(k, v);
    }

    /// <summary>The scripted customer journey: make an invoice, then quote its number and total.</summary>
    public static LlmChatResult Invoice(IReadOnlyList<LlmMessage> msgs, IReadOnlyList<LlmTool>? tools)
    {
        var last = msgs.LastOrDefault();
        if (last?.Role != "tool")
            return ToolScriptLlm.Calls(("document_create_invoice",
                "{\"items\":[{\"description\":\"Haircut\",\"qty\":2,\"unit_price\":45.5},"
                + "{\"description\":\"Hair wash\",\"qty\":1,\"unit_price\":20,\"discount\":\"10%\"}]}"));
        var json = JsonDocument.Parse(last.Content!).RootElement;
        return json.GetProperty("ok").GetBoolean()
            ? ToolScriptLlm.Says($"Here is invoice {json.GetProperty("number").GetString()} — total {json.GetProperty("total_label").GetString()}.")
            : ToolScriptLlm.Says("Sorry — a teammate will send your invoice.");
    }

    public Contact Contact(string name = "Aisha Rahman", string phone = "60123000001", string language = "en")
    {
        var c = new Contact { Name = name, Phone = phone, Email = "aisha@example.com", Language = language,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        Db.Contacts.Add(c);
        Db.SaveChanges();
        return c;
    }

    public AgentSkill Skill(object? config = null, string id = "docs-1", int? entityId = null)
    {
        var el = JsonSerializer.SerializeToElement(config ?? new
        {
            app = "documents", business_name = "Kedai Rambut Aisha", currency = "MYR", tax_label = "SST", tax_rate = 8,
        });
        var cfg = AppSkills.Normalize(el, out var error);
        Assert.Null(error);
        var s = new AgentSkill
        {
            SkillId = id, Name = "Documents", Kind = SkillKinds.App, Enabled = true, EntityId = entityId,
            UseWhen = "The customer asks for an invoice, quotation or receipt", Config = cfg,
        };
        Db.AgentSkills.Add(s);
        Db.SaveChanges();
        return s;
    }

    public ChannelAccount LiveAccount()
    {
        var a = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Cloud, Name = "Live", ExternalId = "PN-LIVE",
            AccessToken = "tok", CreatedAt = DateTime.UtcNow,
            Config = new Dictionary<string, object?> { ["role"] = AccountRoles.Conversation },
        };
        Db.ChannelAccounts.Add(a);
        Db.SaveChanges();
        return a;
    }

    public void Set(string key, string value) => Sc.SetSetting(key, value);

    public Message Say(string phone, string text, int? accountId = null) =>
        Inbound.ProcessInbound("whatsapp", phone, text, accountId: accountId);

    public Thread ThreadOf(string phone) => Db.Threads.First(t => t.Channel == "whatsapp" && t.Key == phone);

    public List<Message> Messages(string phone)
    {
        var tid = ThreadOf(phone).Id;
        return T.Fresh().Messages.Where(m => m.ThreadId == tid).OrderBy(m => m.Id).ToList();
    }

    public List<Document> Documents() => T.Fresh().Documents.OrderBy(d => d.Id).ToList();

    public void Dispose()
    {
        T.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    public static string PdfText(byte[] pdf)
    {
        using var doc = PdfPigDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }
}

/// <summary>
/// The Documents app skill (PDF invoices / quotations / receipts / letters) and
/// the QR, Payments and Sheets apps: exact server-side totals and numbering, a
/// real A4 PDF, the reserve → send-after-the-text lifecycle through the AI gate
/// (auto, held → reject / approve, shadow), a download link on channels that
/// can't carry files, the playground preview that keeps nothing, the calculator
/// built-in, config validation and the skill wizard's steering.
/// </summary>
public class DocumentTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    // ---- maths -----------------------------------------------------------------------

    [Fact]
    public void Totals_tax_and_discounts_are_computed_exactly_by_the_server()
    {
        var lines = DocumentMath.Lines(J(new object[]
        {
            new { description = "Shampoo", qty = 3, unit_price = 19.99 },
            new { description = "Clip", qty = 2, unit_price = 0.125 },            // 0.25
            new { description = "Colour", qty = 1, unit_price = 100, discount = "10%" },
            new { description = "Trim", qty = "1.5", unit_price = "RM 1,000.10", discount = 0.15 },
        }), out var error);
        Assert.Null(error);
        Assert.Equal(new[] { 59.97m, 0.25m, 90.00m, 1500.00m }, lines.Select(l => l.Amount).ToArray());
        Assert.Equal(10m, lines[2].Discount);
        Assert.Equal("10%", lines[2].DiscountLabel);
        Assert.Equal(1500.15m - 0.15m, lines[3].Amount);

        var t = DocumentMath.Totals(lines, 8m);
        Assert.Equal(1650.22m, t.Subtotal);
        Assert.Equal(132.02m, t.Tax);      // 132.0176 → 132.02
        Assert.Equal(1782.24m, t.Total);
        Assert.Equal(10.15m, t.Discount);

        // half away from zero, never banker's rounding
        Assert.Equal(0.01m, DocumentMath.Lines(J(new[] { new { description = "x", qty = 1, unit_price = 0.005 } }), out _)[0].Amount);
        Assert.Equal(0.13m, DocumentMath.Totals(new[] { new DocLine("x", 1, 2.5m, 0, "", 2.5m) }, 5m).Tax); // 0.125 → 0.13

        string? Bad(object items) { DocumentMath.Lines(J(items), out var e); return e; }
        Assert.Contains("unit_price", Bad(new[] { new { description = "x", qty = 1 } }));
        Assert.Contains("larger than the line", Bad(new[] { new { description = "x", qty = 1, unit_price = 5, discount = 6 } }));
        Assert.Contains("qty", Bad(new[] { new { description = "x", qty = 0, unit_price = 5 } }));
        Assert.Contains("description", Bad(new[] { new { qty = 1, unit_price = 5 } }));
        Assert.Contains("at least one", Bad(Array.Empty<object>()));
    }

    [Fact]
    public void Numbers_run_per_series_from_the_configured_start_and_void_ones_stay_used()
    {
        using var r = new DocumentRig();
        var skill = r.Skill(new
        {
            app = "documents", start_numbers = new { invoice = 120 }, prefixes = new { invoice = "INV/2026/" },
            number_digits = 5,
        });
        var cfg = DocumentAppConfig.From(skill.Config);
        DocumentContent Content() => DocumentService.Build(DocumentTypes.Invoice,
            J(new { items = new[] { new { description = "A", qty = 1, unit_price = 10 } } }), cfg, null,
            DocumentRig.Start, TimeZoneInfo.Utc, "").Content!;

        var a = r.Docs.Issue(skill, DocumentTypes.Invoice, cfg, Content(), null, null, null, "ai");
        var b = r.Docs.Issue(skill, DocumentTypes.Invoice, cfg, Content(), null, null, null, "ai");
        Assert.Equal("INV/2026/00120", a.Number);
        Assert.Equal("INV/2026/00121", b.Number);
        Assert.Equal(DocumentStatus.Pending, a.Status);

        r.Docs.Release(new[] { b.Id }, "rejected");
        var c = r.Docs.Issue(skill, DocumentTypes.Invoice, cfg, Content(), null, null, null, "agent:admin");
        Assert.Equal("INV/2026/00122", c.Number);   // a void number is never reused
        Assert.Equal(DocumentStatus.Issued, c.Status);
        Assert.Equal(DocumentStatus.Void, r.Documents().Single(d => d.Id == b.Id).Status);

        // another type and another skill keep their own series
        var q = r.Docs.Issue(skill, DocumentTypes.Quotation, cfg, DocumentService.Build(DocumentTypes.Quotation,
            J(new { items = new[] { new { description = "A", qty = 1, unit_price = 10 } } }), cfg, null,
            DocumentRig.Start, TimeZoneInfo.Utc, "").Content!, null, null, null, "ai");
        Assert.Equal("QUO-00001", q.Number);
        var other = r.Skill(new { app = "documents" }, id: "docs-2");
        Assert.Equal("INV-0001", r.Docs.Issue(other, DocumentTypes.Invoice, DocumentAppConfig.From(other.Config),
            Content(), null, null, null, "ai").Number);
    }

    // ---- the PDF --------------------------------------------------------------------

    [Fact]
    public void The_pdf_is_a_real_a4_document_with_the_number_items_and_totals()
    {
        var cfg = DocumentAppConfig.From(J(new
        {
            app = "documents", business_name = "Kedai Rambut Aisha", address = "12 Jalan Bunga\nKuala Lumpur",
            registration_no = "202601234567", tax_no = "W10-1234", tax_label = "SST", tax_rate = 8,
            payment_instructions = "Maybank 5123 4567 8901", accent = "teal",
        }), out var error);
        Assert.Null(error);
        var content = DocumentService.Build(DocumentTypes.Invoice, J(new
        {
            customer_name = "Encik Ahmad", notes = "Terima kasih — sila bayar dalam 14 hari.",
            items = new object[] { new { description = "Potongan rambut (lelaki)", qty = 2, unit_price = 45.5 } },
        }), cfg, null, DocumentRig.Start, TimeZoneInfo.Utc, "INV-0042").Content!;

        var pdf = DocumentRenderer.Render(content, cfg);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        var text = DocumentRig.PdfText(pdf);
        Assert.Contains("INV-0042", text);
        Assert.Contains("Kedai", text);
        Assert.Contains("Potongan", text);
        Assert.Contains("91.00", text);      // 2 × 45.50
        Assert.Contains("7.28", text);       // SST 8 %
        Assert.Contains("98.28", text);      // total
        Assert.Contains("Maybank", text);
        Assert.Contains("INVOICE", text);
        using (var doc = PdfPigDocument.Open(pdf))
        {
            var page = doc.GetPage(1);
            Assert.InRange(page.Width, 594, 597);   // A4 = 595 × 842 pt
            Assert.InRange(page.Height, 840, 843);
        }

        // Malay labels for a Malay-speaking customer (language auto)
        var ms = DocumentService.Build(DocumentTypes.Receipt, J(new
        {
            items = new[] { new { description = "Bayaran deposit", qty = 1, unit_price = 50 } }, payment_method = "DuitNow",
        }), cfg, new Contact { Name = "Siti", Language = "ms" }, DocumentRig.Start, TimeZoneInfo.Utc, "RCP-0001").Content!;
        var msText = DocumentRig.PdfText(DocumentRenderer.Render(ms, cfg));
        Assert.Contains("RESIT", msText);
        Assert.Contains("DuitNow", msText);

        // a letter: subject + paragraphs on the letterhead
        var letter = DocumentService.Build(DocumentTypes.Letter, J(new
        {
            title = "Confirmation of booking", paragraphs = new[] { "We confirm your booking.", "See you soon." },
        }), cfg, new Contact { Name = "Siti" }, DocumentRig.Start, TimeZoneInfo.Utc, "LTR-0001").Content!;
        var letterText = DocumentRig.PdfText(DocumentRenderer.Render(letter, cfg));
        Assert.Contains("Confirmation", letterText);
        Assert.Contains("sincerely", letterText);
    }

    [Fact]
    public void Chinese_text_is_refused_clearly_when_no_cjk_font_is_installed()
    {
        var cfg = new DocumentAppConfig();
        var r = DocumentService.Build(DocumentTypes.Invoice, J(new
        {
            items = new[] { new { description = "剪发", qty = 1, unit_price = 30 } },
        }), cfg, null, DocumentRig.Start, TimeZoneInfo.Utc, "");
        if (DocumentFonts.CjkAvailable)
        {
            Assert.Null(r.Error);   // a CJK font is installed: the PDF is written in it
            var pdf = DocumentRenderer.Render(r.Content!, cfg);
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        }
        else
        {
            Assert.Contains("Chinese", r.Error);
            Assert.Contains("English or Malay", r.Advice);
        }
    }

    // ---- the reply lifecycle --------------------------------------------------------

    [Fact]
    public void An_auto_sent_reply_sends_the_pdf_as_a_file_right_after_its_text()
    {
        using var r = new DocumentRig();
        r.Contact();
        r.Skill();
        var acct = r.LiveAccount();
        r.Say("60123000001", "Boleh hantar invois untuk 2 potongan rambut dan cuci rambut?", acct.Id);

        var doc = Assert.Single(r.Documents());
        Assert.True(doc.Status == DocumentStatus.Sent, doc.Detail + " | " + string.Join(" | ", r.Wa.Sent) + " | " + string.Join(" | ", r.T.Fresh().AiDrafts.Select(d => d.Status + ":" + d.Error)));
        Assert.Equal("INV-0001", doc.Number);
        Assert.Equal(91.00m + 18.00m, doc.Subtotal);
        Assert.Equal(8.72m, doc.Tax);
        Assert.Equal(117.72m, doc.Total);
        Assert.Equal("file", doc.Detail);
        Assert.NotNull(doc.DraftId);

        // the provider saw the text FIRST, then the upload and the document
        Assert.Equal(3, r.Wa.Sent.Count);
        Assert.StartsWith("text:Here is invoice INV-0001 — total MYR 117.72", r.Wa.Sent[0]);
        Assert.StartsWith("upload:application/pdf:INV-0001.pdf:", r.Wa.Sent[1]);
        Assert.StartsWith("media:document:INV-0001.pdf:📄 Invoice INV-0001", r.Wa.Sent[2]);

        var msgs = r.Messages("60123000001");
        var aiText = msgs.Single(m => m.Source == MessageSource.Ai && string.IsNullOrEmpty(m.MediaType));
        var file = msgs.Single(m => m.MediaType == "document");
        Assert.True(file.Id > aiText.Id);
        Assert.Equal(doc.MessageId, file.Id);
        Assert.NotNull(r.Media.Resolve("whatsapp", file.MediaPath!));
        Assert.Contains(msgs, m => m.Private && m.Text.StartsWith("📄 Invoice INV-0001 sent (MYR 117.72)"));
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == "document.sent" && a.Target == $"document:{doc.Id}");

        // the file is the PDF
        var pdf = File.ReadAllBytes(r.Media.Resolve("whatsapp", file.MediaPath!)!);
        Assert.Contains("INV-0001", DocumentRig.PdfText(pdf));
    }

    [Fact]
    public void A_refused_file_falls_back_to_a_download_link_never_silently()
    {
        using var r = new DocumentRig();
        r.Contact();
        r.Skill();
        var acct = r.LiveAccount();
        r.Wa.FailMedia = true;
        r.Say("60123000001", "Invoice please", acct.Id);

        var doc = Assert.Single(r.Documents());
        Assert.Equal(DocumentStatus.Sent, doc.Status);
        Assert.StartsWith("link", doc.Detail);
        Assert.Contains(r.Wa.Sent, s => s.StartsWith("text:📄 Invoice INV-0001: ") && s.Contains($"/api/documents/public/{doc.PublicToken}"));
    }

    [Fact]
    public void A_held_reply_sends_nothing_until_approved_and_a_rejected_one_voids_its_document()
    {
        using var r = new DocumentRig();
        r.Contact();
        r.Skill();
        var acct = r.LiveAccount();
        r.Set("ai_review_mode", "review_all");

        r.Say("60123000001", "Invoice please", acct.Id);
        var held = Assert.Single(r.Documents());
        Assert.Equal(DocumentStatus.Pending, held.Status);
        Assert.Empty(r.Wa.Sent);
        var draft = r.T.Fresh().AiDrafts.Single();
        Assert.Equal(AiDraftStatus.Pending, draft.Status);
        Assert.Equal(draft.Id, held.DraftId);
        Assert.Equal(new[] { held.Id }, AiAttachments.From(draft.Context));

        r.Gate.Reject(draft.Id, "admin", "wrong price");
        Assert.Equal(DocumentStatus.Void, r.Documents().Single().Status);
        Assert.Empty(r.Wa.Sent);
        Assert.DoesNotContain(r.Messages("60123000001"), m => !string.IsNullOrEmpty(m.MediaType));

        // a second request, approved by a person: text, then the file
        r.Say("60123000001", "Please send the invoice again", acct.Id);
        var second = r.Documents().Last();
        Assert.Equal("INV-0002", second.Number);     // the void INV-0001 stays used
        var d2 = r.T.Fresh().AiDrafts.OrderBy(d => d.Id).Last();
        var o = r.Gate.Approve(d2.Id, "admin");
        Assert.True(o.Delivered);
        Assert.Equal(DocumentStatus.Sent, r.Documents().Last().Status);
        Assert.StartsWith("text:", r.Wa.Sent[0]);
        Assert.StartsWith("media:document:INV-0002.pdf", r.Wa.Sent[^1]);
        Assert.Contains(r.T.Fresh().AuditLogs, a => a.Action == "document.sent" && a.Actor == "admin");
    }

    [Fact]
    public void Shadow_mode_never_sends_a_document()
    {
        using var r = new DocumentRig();
        r.Contact();
        r.Skill();
        var acct = r.LiveAccount();
        r.Set("ai_review_mode", "shadow");
        r.Say("60123000001", "Invoice please", acct.Id);
        Assert.Equal(DocumentStatus.Void, Assert.Single(r.Documents()).Status);
        Assert.Empty(r.Wa.Sent);
    }

    [Fact]
    public void A_split_reply_sends_its_file_after_the_last_bubble()
    {
        using var r = new DocumentRig((msgs, tools) =>
        {
            if (msgs.LastOrDefault()?.Role != "tool")
                return ToolScriptLlm.Calls(("document_create_quotation",
                    "{\"items\":[{\"description\":\"Renovation\",\"qty\":1,\"unit_price\":5000}]}"));
            return ToolScriptLlm.Says("Thanks for asking! I've prepared your quotation.\n\nIt is valid for 14 days. Let me know if you have questions.");
        });
        r.Contact();
        r.Skill();
        var acct = r.LiveAccount();
        r.Set("ai_split", "always");
        r.Say("60123000001", "Quote please", acct.Id);
        // bubble 1 went inline; bubble 2 is queued with the file after it
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        r.Queue.Drain();
        r.Clock.Advance(TimeSpan.FromSeconds(5));
        r.Queue.Drain();

        var texts = r.Wa.Sent.Where(s => s.StartsWith("text:")).ToList();
        Assert.True(texts.Count >= 2, string.Join(" | ", r.Wa.Sent));
        var lastText = r.Wa.Sent.FindLastIndex(s => s.StartsWith("text:"));
        var media = r.Wa.Sent.FindIndex(s => s.StartsWith("media:document:QUO-0001.pdf"));
        Assert.True(media > lastText, string.Join(" | ", r.Wa.Sent));
        Assert.Equal(1, r.Wa.Sent.Count(s => s.StartsWith("media:")));
        Assert.Equal(DocumentStatus.Sent, Assert.Single(r.Documents()).Status);
    }

    [Fact]
    public void Channels_without_files_get_a_secure_download_link()
    {
        using var r = new DocumentRig();
        var c = r.Contact();
        var skill = r.Skill();
        var cfg = DocumentAppConfig.From(skill.Config);
        foreach (var channel in new[] { "line", "website" })
        {
            var thread = r.Threads.GetOrCreateThread(channel, "U-" + channel, contactId: c.Id);
            var content = DocumentService.Build(DocumentTypes.Quotation,
                J(new { items = new[] { new { description = "A", qty = 1, unit_price = 10 } } }), cfg, c,
                DocumentRig.Start, TimeZoneInfo.Utc, "").Content!;
            var doc = r.Docs.Issue(skill, DocumentTypes.Quotation, cfg, content, c.Id, thread, null, "ai");
            r.Docs.Deliver(new[] { doc.Id }, thread, null, r.Sender, MessageSource.Ai, "ai");

            var fresh = r.Documents().Single(d => d.Id == doc.Id);
            Assert.Equal(DocumentStatus.Sent, fresh.Status);
            Assert.Equal("link", fresh.Detail);
            var msg = r.T.Fresh().Messages.Single(m => m.Id == fresh.MessageId);
            Assert.True(string.IsNullOrEmpty(msg.MediaType));
            Assert.Contains($"/api/documents/public/{fresh.PublicToken}", msg.Text);
            Assert.Matches("^[a-f0-9]{32}$", fresh.PublicToken);
            Assert.NotNull(r.Docs.ByPublicToken(fresh.PublicToken));
        }
        // a pending (undelivered) or void document has no public link
        var pending = r.Docs.Issue(skill, DocumentTypes.Invoice, cfg, DocumentService.Build(DocumentTypes.Invoice,
            J(new { items = new[] { new { description = "A", qty = 1, unit_price = 10 } } }), cfg, c,
            DocumentRig.Start, TimeZoneInfo.Utc, "").Content!, c.Id, null, null, "ai");
        Assert.Null(r.Docs.ByPublicToken(pending.PublicToken));
        Assert.Null(r.Docs.ByPublicToken("not-a-token"));
    }

    [Fact]
    public void A_qr_code_goes_out_as_an_image_after_the_text()
    {
        using var r = new DocumentRig((msgs, tools) => msgs.LastOrDefault()?.Role != "tool"
            ? ToolScriptLlm.Calls(("qr_create", "{\"text\":\"https://pay.example.com/abc\",\"caption\":\"Scan to pay\"}"))
            : ToolScriptLlm.Says("Here's the QR code to pay."));
        r.Contact();
        var s = new AgentSkill
        {
            SkillId = "qr-1", Name = "QR codes", Kind = SkillKinds.App, Enabled = true,
            Config = AppSkills.Normalize(J(new { app = "qr" }), out _),
        };
        r.Db.AgentSkills.Add(s);
        r.Db.SaveChanges();
        var acct = r.LiveAccount();
        r.Say("60123000001", "Can I have a QR to pay?", acct.Id);

        var doc = Assert.Single(r.Documents());
        Assert.Equal(DocumentTypes.Qr, doc.Type);
        Assert.Equal(DocumentStatus.Sent, doc.Status);
        Assert.StartsWith("text:Here's the QR code", r.Wa.Sent[0]);
        Assert.StartsWith("upload:image/png:qr-code.png", r.Wa.Sent[1]);
        Assert.StartsWith("media:image:qr-code.png:Scan to pay", r.Wa.Sent[2]);
        var png = File.ReadAllBytes(r.Docs.FileOf(doc)!);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
    }

    [Fact]
    public async Task The_playground_previews_a_real_pdf_and_keeps_nothing()
    {
        using var r = new DocumentRig();
        r.Skill();
        var result = await r.Assistant.GeneratePlaygroundAsync(new PlaygroundInput(null, "whatsapp", null,
            new PlaygroundProfile("Test", "en", Array.Empty<string>(), new Dictionary<string, object?>()),
            new[] { new PlaygroundTurn(PlaygroundTurn.Customer, "Invoice please") }, PlaygroundOptions.Defaults));

        Assert.Contains("INV-0001", result.Reply.Text);
        var preview = Assert.Single(result.Documents!);
        Assert.Equal("INV-0001", preview.Number);
        Assert.Equal(117.72m, preview.Total);
        Assert.StartsWith("/api/documents/preview/", preview.Url);
        var file = r.Docs.PreviewFile(preview.Url.Split('/').Last());
        Assert.NotNull(file);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(file!), 0, 4));

        Assert.Empty(r.Documents());                // no row, no number taken
        Assert.Empty(r.T.Fresh().Messages);
        Assert.Empty(r.Wa.Sent);
        Assert.Empty(result.Reply.Attachments);
        foreach (var root in new[] { "messaging", "whatsapp" })   // no conversation media written
        {
            var dir = Path.Combine(r.Media.DataDir, root);
            Assert.False(Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any());
        }
    }

    [Fact]
    public void A_document_tool_refuses_bad_items_and_the_model_gets_the_reason()
    {
        using var r = new DocumentRig((msgs, tools) => msgs.LastOrDefault()?.Role != "tool"
            ? ToolScriptLlm.Calls(("document_create_invoice", "{\"items\":[{\"description\":\"Mystery\",\"qty\":1}]}"))
            : ToolScriptLlm.Says(msgs.Last().Content!.Contains("unit_price") ? "What price did we agree?" : "??"));
        r.Contact();
        r.Skill();
        var acct = r.LiveAccount();
        r.Say("60123000001", "Invoice please", acct.Id);
        Assert.Empty(r.Documents());
        Assert.Equal("text:What price did we agree?", Assert.Single(r.Wa.Sent));
    }

    // ---- the calculator --------------------------------------------------------------

    [Theory]
    [InlineData("1 + 2 * 3", null, "7")]
    [InlineData("(1 + 2) * 3", null, "9")]
    [InlineData("0.1 + 0.2", null, "0.3")]
    [InlineData("200 + 10%", null, "220")]
    [InlineData("200 - 10%", null, "180")]
    [InlineData("200 * 8%", null, "16")]
    [InlineData("RM 1,250.50 x 3", null, "3751.5")]
    [InlineData("10 / 3", 2, "3.33")]
    [InlineData("2 / 3", null, "0.6666666667")]
    [InlineData("round(2.345, 2)", null, "2.35")]
    [InlineData("-2^2", null, "-4")]
    [InlineData("2^-1", null, "0.5")]
    [InlineData("max(3, 9.5, 2) - min(4, 1)", null, "8.5")]
    [InlineData("(3 * 45.90 - 10%) * 1.08", 2, "133.84")]
    [InlineData("12 ÷ 4 × 2", null, "6")]
    public void Calculator_is_exact(string expression, int? decimals, string expected)
    {
        var json = BuiltinSkills.Execute("calculate",
            JsonSerializer.Serialize(new { expression, decimals }), DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        Assert.Equal(expected, JsonDocument.Parse(json).RootElement.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("1 / 0", "division by zero")]
    [InlineData("2 +", "ends too early")]
    [InlineData("alert(1)", "unknown name")]
    [InlineData("(1 + 2", "missing")]
    [InlineData("2 ^ 0.5", "whole numbers")]
    public void Calculator_refuses_what_it_cannot_do(string expression, string why)
    {
        var ex = Assert.Throws<ArgumentException>(() => BuiltinSkills.Execute("calculate",
            JsonSerializer.Serialize(new { expression }), DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
        Assert.Contains(why, ex.Message);
        Assert.Contains(BuiltinSkills.All, b => b.Name == "calculate");
    }

    // ---- config validation + steering -----------------------------------------------

    [Fact]
    public void App_configs_validate_and_normalize()
    {
        string? Problem(object raw)
        {
            AppSkills.Normalize(J(raw), out var e);
            return e;
        }
        Assert.Null(Problem(new { app = "documents" }));
        Assert.Contains("currency", Problem(new { app = "documents", currency = "Ringgit" }));
        Assert.Contains("tax_rate", Problem(new { app = "documents", tax_rate = 150 }));
        Assert.Contains("accent", Problem(new { app = "documents", accent = "#ff0000" }));
        Assert.Contains("logo", Problem(new { app = "documents", logo = "../../etc/passwd" }));
        Assert.Contains("prefix", Problem(new { app = "documents", prefixes = new { invoice = "<script>" } }));
        Assert.Contains("unknown action", Problem(new { app = "documents", actions = new[] { "invoice", "contract" } }));
        Assert.Contains("email", Problem(new { app = "documents", email = "not an email" }));
        Assert.Contains("unknown app", Problem(new { app = "teleport" }));
        Assert.Contains("https", Problem(new { app = "payments", success_url = "http://x.com" }));
        Assert.Null(Problem(new { app = "qr" }));
        Assert.Null(Problem(new { }));                         // no app key = a calendar

        var sheets = AppSkills.Normalize(J(new
        {
            app = "sheets", spreadsheet_id = "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789/edit#gid=0",
            columns = new[] { "Product", "Quantity" },
        }), out var err);
        Assert.Null(err);
        Assert.Equal("1AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789", sheets["spreadsheet_id"]);

        var d = DocumentAppConfig.Defaults();
        Assert.Equal("MYR", d["currency"]);
        Assert.Equal(new[] { "invoice", "quotation", "receipt", "letter" }, (List<string>)d["actions"]!);
        Assert.Equal("documents", AppSkills.AppOf(d));
    }

    [Fact]
    public void Tools_are_offered_per_enabled_action_and_named_uniquely()
    {
        using var r = new DocumentRig();
        r.Skill(new { app = "documents", actions = new[] { "invoice", "receipt" } });
        r.Skill(new { app = "documents" }, id: "docs-2");
        var set = r.Skills.BuildTools(r.Skills.InScope(null, "whatsapp"));
        var names = set.Tools.Select(t => t.Name).ToList();
        Assert.Contains("document_create_invoice", names);
        Assert.Contains("document_create_receipt", names);
        Assert.DoesNotContain("document_create_quotation", names);
        Assert.Contains("document_2_create_quotation", names);
        Assert.Contains("calculate", names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public async Task The_skill_wizard_steers_file_requests_to_the_ready_made_apps()
    {
        using var t = new TestDb();
        var drafter = new SkillDrafter(new SandboxLlmClient(), t.Db);
        var r = await drafter.DraftAsync("Create a pdf skill that sends the customer an invoice", null, Array.Empty<SkillDrafter.Answer>());
        Assert.Equal("use_app", r.Status);
        Assert.Equal("documents", r.SuggestApp);
        Assert.Contains("can't create files", r.Note);
        Assert.Equal("qr", (await drafter.DraftAsync("make a QR code for the menu", "tool", Array.Empty<SkillDrafter.Answer>())).SuggestApp);
        // instructions ABOUT invoices are a fine prompt skill
        Assert.NotEqual("use_app", (await drafter.DraftAsync("Explain our invoice terms politely", "prompt", Array.Empty<SkillDrafter.Answer>())).Status);
    }

    [Fact]
    public void A_code_skill_that_reaches_for_the_network_or_files_is_told_why_it_cannot()
    {
        var run = SkillRuntime.RunTool("function run(args, ctx) { return fetch('https://example.com'); }", "{}",
            new SkillContext());
        Assert.False(run.Ok);
        Assert.Contains("no internet, no files", run.Error);
        Assert.Contains("Documents", run.Error);
        var pdf = SkillRuntime.RunTool("function run(args, ctx) { var d = new jsPDF(); return d.output(); }", "{}",
            new SkillContext());
        Assert.Contains("can't make a PDF", pdf.Error);
    }
}

/// <summary>The documents API: the skills PUT, the apps catalog, the list, the file and the public link.</summary>
public class DocumentApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DocumentApiTests(ApiFactory f) => _f = f;

    private async Task<HttpClient> As(string user, string pass)
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Documents_skill_round_trips_previews_and_makes_an_operator_document()
    {
        var admin = await As("admin", "admin123");
        var cat = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills/apps");
        var apps = cat.GetProperty("apps").EnumerateArray().Select(a => a.GetProperty("app").GetString()).ToList();
        Assert.Equal(new[] { "calendar", "documents", "qr", "payments", "sheets" }, apps);

        // a bad config is refused by name; a good one is stored normalized
        var bad = await admin.PutAsJsonAsync("/api/aicore/skills", new
        {
            skills = new[] { new { id = "docs-api", name = "Documents", kind = "app", position = 0,
                config = new { app = "documents", tax_rate = 101 } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("tax_rate", await bad.Content.ReadAsStringAsync());

        var existing = (await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills")).GetProperty("skills")
            .EnumerateArray().Where(s => !s.TryGetProperty("entity", out var e) || e.ValueKind == JsonValueKind.Null)
            .Select(s => JsonSerializer.Deserialize<Dictionary<string, object?>>(s.GetRawText())!).ToList();
        existing.RemoveAll(s => s["id"]?.ToString() == "docs-api");
        existing.Add(new Dictionary<string, object?>
        {
            ["id"] = "docs-api", ["name"] = "Documents", ["kind"] = "app", ["position"] = 0, ["enabled"] = true,
            ["config"] = new { app = "documents", business_name = "ACME Sdn Bhd", tax_rate = 6, prefixes = new { invoice = "AC-" } },
        });
        var put = await admin.PutAsJsonAsync("/api/aicore/skills", new { skills = existing });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("skills").EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "docs-api");
        Assert.Equal("documents", saved.GetProperty("app").GetString());
        Assert.Equal("AC-", saved.GetProperty("config").GetProperty("prefixes").GetProperty("invoice").GetString());
        Assert.Equal("MYR", saved.GetProperty("config").GetProperty("currency").GetString());

        // "Preview PDF": a sample, no number used
        var dry = await (await admin.PostAsJsonAsync("/api/aicore/skills/test", new
        {
            kind = "app", app_action = "preview", document_type = "invoice",
            config = new { app = "documents", business_name = "ACME Sdn Bhd", tax_rate = 6, prefixes = new { invoice = "AC-" } },
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dry.GetProperty("ok").GetBoolean(), dry.ToString());
        Assert.Equal("AC-0001", dry.GetProperty("number").GetString());
        var preview = await admin.GetAsync(dry.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("application/pdf", preview.Content.Headers.ContentType?.MediaType);

        // an operator makes one by hand in a conversation and sends it
        var phone = "6012" + Random.Shared.Next(1000000, 9999999);
        var sim = await admin.PostAsJsonAsync("/api/whatsapp/simulate", new { channel = "whatsapp", key = phone, text = "Hi, I need an invoice" });
        Assert.True(sim.IsSuccessStatusCode, await sim.Content.ReadAsStringAsync());
        var made = await admin.PostAsJsonAsync("/api/documents", new
        {
            channel = "whatsapp", key = phone, type = "invoice", send = true,
            fields = new { items = new[] { new { description = "Consulting", qty = 2, unit_price = 150 } } },
        });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var doc = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document");
        Assert.StartsWith("AC-", doc.GetProperty("number").GetString());
        Assert.Equal("sent", doc.GetProperty("status").GetString());
        Assert.Equal(318m, doc.GetProperty("total").GetDecimal());   // 300 + 6 %

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/documents?thread=whatsapp:{phone}");
        Assert.Single(list.GetProperty("documents").EnumerateArray());
        var file = await admin.GetAsync(doc.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await file.Content.ReadAsByteArrayAsync())[..4]));

        // the public link: anonymous, by token only
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.GetProperty("id").GetInt32());
        var anon = _f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/documents/public/{row.PublicToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/documents/public/{new string('0', 32)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(doc.GetProperty("url").GetString())).StatusCode);

        // a type the skill doesn't make / a conversation without a Documents skill for it
        var refused = await admin.PostAsJsonAsync("/api/documents", new
        {
            channel = "whatsapp", key = phone, type = "contract", fields = new { },
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }
}
