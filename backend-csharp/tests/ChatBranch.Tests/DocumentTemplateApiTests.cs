using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Documents;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A test host whose LLM is a scripted fake (off = the sandbox, like no key at all).</summary>
public class TemplateAiFactory : ApiFactory
{
    public FakeDesignLlm Llm { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<ILlmClient>();
            s.AddSingleton<ILlmClient>(Llm);
        });
    }
}

/// <summary>Records what the model was sent (text + images) and answers from a script.</summary>
public sealed class FakeDesignLlm : ILlmClient
{
    public bool Enabled { get; set; }
    public Func<IReadOnlyList<LlmMessage>, string>? Answer { get; set; }
    public List<IReadOnlyList<LlmMessage>> Seen { get; } = new();
    public bool IsConfigured => Enabled;

    public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
        CancellationToken ct = default) => Task.FromResult(new LlmResult("", 0, 0, "sandbox", "sandbox"));

    public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5,
        CancellationToken ct = default)
    {
        lock (Seen) Seen.Add(messages.ToList());
        if (!Enabled || Answer == null)
            return Task.FromResult(new LlmChatResult("", Array.Empty<LlmToolCall>(), 0, 0, "sandbox", "sandbox"));
        return Task.FromResult(new LlmChatResult(Answer(messages), Array.Empty<LlmToolCall>(), 120, 60, "openai", "gpt-vision-test"));
    }
}

/// <summary>
/// The template designer's API: "Upload my design" (a fake vision model → a
/// validated template; no model → the PDF text heuristic; an answer that never
/// validates → the heuristic too), the letterhead background upload, "Ask AI"
/// (fake model + the sandbox rule set), the skills PUT storing a template,
/// the gates (agent 403), size / magic-byte refusals and the AI quota (429).
/// </summary>
public class DocumentTemplateApiTests : IClassFixture<TemplateAiFactory>
{
    private readonly TemplateAiFactory _f;
    public DocumentTemplateApiTests(TemplateAiFactory f) => _f = f;

    private T With<T>(Func<AppDbContext, T> act) => SettingsTestKit.WithDb(_f, act);

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static MultipartFormDataContent Form(byte[] file, string name, string mime, Dictionary<string, string>? fields = null)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(part, "file", name);
        foreach (var (k, v) in fields ?? new()) form.Add(new StringContent(v), k);
        return form;
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private int Usage(string user) => With(db => db.LlmUsage.Count(u => u.Feature == DocumentTemplateAi.Feature && u.UserName == user));

    // ---- upload your own design ---------------------------------------------------------------

    [Fact]
    public async Task Analyze_with_a_vision_model_returns_a_validated_template_and_a_preview()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var before = Usage("admin");
        _f.Llm.Enabled = true;
        _f.Llm.Answer = _ => """
            Here you go:
            {"template": {"preset": "bold", "colors": {"accent": "#123456"}, "header": {"logo_position": "right", "tagline": "Since 1999"},
                          "table": {"columns": ["description", "qty", "amount"], "zebra": true}},
             "business": {"business_name": "Acme Trading", "email": "hi@acme.example", "phone": "+60 3-1234 5678"},
             "summary": "Matched the navy band and the right-hand logo."}
            """;
        try
        {
            var r = await Ok(await admin.PostAsync("/api/aicore/skills/documents/template/analyze",
                Form(Png, "design.png", "image/png", new() { ["config"] = "{\"app\":\"documents\",\"email\":\"own@acme.example\"}" })));
            Assert.Equal("ai", r.GetProperty("mode").GetString());
            var t = r.GetProperty("template");
            Assert.Equal("bold", t.GetProperty("preset").GetString());
            Assert.Equal("#123456", t.GetProperty("colors").GetProperty("accent").GetString());
            Assert.Equal("band", t.GetProperty("header").GetProperty("style").GetString()); // filled from the preset
            Assert.Equal(1, t.GetProperty("version").GetInt32());
            Assert.Equal("Acme Trading", r.GetProperty("business").GetProperty("business_name").GetString());
            Assert.Contains("navy band", r.GetProperty("summary").GetString());
            // the picture went to the model as a data URL
            var sent = _f.Llm.Seen[^1][0];
            Assert.StartsWith("data:image/png;base64,", Assert.Single(sent.Images!));
            // the preview renders the proposal (the form's own email wins over the suggestion)
            var preview = r.GetProperty("preview");
            Assert.True(preview.GetProperty("ok").GetBoolean(), preview.ToString());
            var pdf = await admin.GetAsync(preview.GetProperty("url").GetString());
            Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
            var text = DocumentRig.PdfText(await pdf.Content.ReadAsByteArrayAsync());
            Assert.Contains("Acme Trading", text);
            Assert.Contains("Since 1999", text);
            Assert.Contains("own@acme.example", text);
            Assert.Equal(before + 1, Usage("admin"));
        }
        finally { _f.Llm.Enabled = false; }
    }

    [Fact]
    public async Task Analyze_a_pdf_sends_its_text_layer_and_can_keep_it_as_a_letterhead()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        _f.Llm.Enabled = true;
        // first answer breaks the rules, the retry is fine
        var calls = 0;
        _f.Llm.Answer = msgs => ++calls == 1
            ? "{\"template\": {\"colour\": \"blue\"}}"
            : "{\"template\": {\"preset\": \"minimal\"}, \"summary\": \"Minimal.\"}";
        try
        {
            var design = DocumentTemplateTests.DesignPdf("ACME LETTERHEAD");
            var r = await Ok(await admin.PostAsync("/api/aicore/skills/documents/template/analyze",
                Form(design, "design.pdf", "application/pdf", new() { ["keep_letterhead"] = "true" })));
            Assert.Equal("ai", r.GetProperty("mode").GetString());
            Assert.Equal("minimal", r.GetProperty("template").GetProperty("preset").GetString());
            Assert.Equal(2, calls);
            var first = _f.Llm.Seen[^2][0].Content!;
            Assert.Contains("TEXT LAYER", first);
            Assert.Contains("ACME LETTERHEAD", first);
            Assert.Contains("refused", _f.Llm.Seen[^1][^1].Content);   // the retry names the problem
            Assert.Contains("unknown key", _f.Llm.Seen[^1][^1].Content);

            var lh = r.GetProperty("letterhead").GetString()!;
            Assert.True(DocumentService.IsLetterheadName(lh));
            Assert.Equal(lh, r.GetProperty("template").GetProperty("letterhead").GetProperty("file").GetString());
            var file = await admin.GetAsync(r.GetProperty("letterhead_url").GetString());
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("application/pdf", file.Content.Headers.ContentType?.MediaType);
            // the preview prints over the background
            var preview = await admin.GetAsync(r.GetProperty("preview").GetProperty("url").GetString());
            var text = DocumentRig.PdfText(await preview.Content.ReadAsByteArrayAsync());
            Assert.Contains("LETTERHEAD", text);
            Assert.Contains("Sample", text);
        }
        finally { _f.Llm.Enabled = false; }
    }

    [Fact]
    public async Task Analyze_without_a_model_reads_the_pdf_text_heuristically()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        _f.Llm.Enabled = false;
        var r = await Ok(await admin.PostAsync("/api/aicore/skills/documents/template/analyze",
            Form(DocumentTemplateTests.DesignPdf("ACME LETTERHEAD"), "design.pdf", "application/pdf")));
        Assert.Equal("heuristic", r.GetProperty("mode").GetString());
        Assert.Contains("No AI model", r.GetProperty("note").GetString());
        var b = r.GetProperty("business");
        Assert.Equal("ACME LETTERHEAD", b.GetProperty("business_name").GetString());
        Assert.Equal("12 Jalan Bunga, Kuala Lumpur", b.GetProperty("address").GetString());
        Assert.Equal("202601234567", b.GetProperty("registration_no").GetString());
        Assert.Equal("hello@acme.example", b.GetProperty("email").GetString());
        Assert.Contains("6789", b.GetProperty("phone").GetString());
        var t = r.GetProperty("template");
        Assert.Equal("right", t.GetProperty("title").GetProperty("align").GetString());
        Assert.Equal("#1E3A8A", t.GetProperty("colors").GetProperty("accent").GetString());
        Assert.Contains("Maybank", t.GetProperty("notes").GetProperty("bank_details").GetString());
        Assert.Contains("choosing Acme", t.GetProperty("footer").GetProperty("text").GetString());
        Assert.StartsWith("Read from your PDF", r.GetProperty("summary").GetString());
        Assert.True(r.GetProperty("preview").GetProperty("ok").GetBoolean());

        // an image with no model: nothing to read, the template stays, the note says why
        var img = await Ok(await admin.PostAsync("/api/aicore/skills/documents/template/analyze",
            Form(Png, "design.png", "image/png")));
        Assert.Equal("heuristic", img.GetProperty("mode").GetString());
        Assert.Contains("only a PDF", img.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Analyze_falls_back_to_the_heuristic_when_the_model_never_validates()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        _f.Llm.Enabled = true;
        _f.Llm.Answer = _ => "I think it is a nice blue invoice.";
        try
        {
            var r = await Ok(await admin.PostAsync("/api/aicore/skills/documents/template/analyze",
                Form(DocumentTemplateTests.DesignPdf("ACME LETTERHEAD"), "design.pdf", "application/pdf")));
            Assert.Equal("heuristic", r.GetProperty("mode").GetString());
            Assert.Contains("didn't fit", r.GetProperty("note").GetString());
            Assert.Equal("ACME LETTERHEAD", r.GetProperty("business").GetProperty("business_name").GetString());
        }
        finally { _f.Llm.Enabled = false; }
    }

    // ---- ask AI -----------------------------------------------------------------------------------

    [Fact]
    public async Task Assist_without_a_model_applies_the_sandbox_rules()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        _f.Llm.Enabled = false;
        var r = await Ok(await admin.PostAsJsonAsync("/api/aicore/skills/documents/template/assist", new
        {
            template = new { preset = "classic", notes = new { bank_details = "Maybank 1234\nAcme Sdn Bhd" }, header = new { tagline = "Hi" } },
            instruction = "make it minimal with a navy header and put bank details in the footer, logo right",
        }));
        Assert.Equal("sandbox", r.GetProperty("mode").GetString());
        var t = r.GetProperty("template");
        Assert.Equal("minimal", t.GetProperty("preset").GetString());
        Assert.Equal("band", t.GetProperty("header").GetProperty("style").GetString());
        Assert.Equal("#1E3A8A", t.GetProperty("header").GetProperty("band_color").GetString());
        Assert.Equal("right", t.GetProperty("header").GetProperty("logo_position").GetString());
        Assert.Equal("Hi", t.GetProperty("header").GetProperty("tagline").GetString()); // words kept across a preset
        Assert.Equal("", t.GetProperty("notes").GetProperty("bank_details").GetString());
        Assert.Equal("Maybank 1234  ·  Acme Sdn Bhd", t.GetProperty("footer").GetProperty("text").GetString());
        var summary = r.GetProperty("summary").GetString()!;
        Assert.Contains("minimal", summary);
        Assert.Contains("bank details moved", summary);

        var none = await Ok(await admin.PostAsJsonAsync("/api/aicore/skills/documents/template/assist",
            new { instruction = "make it sparkle like a unicorn" }));
        Assert.StartsWith("No change", none.GetProperty("summary").GetString());

        var bad = await admin.PostAsJsonAsync("/api/aicore/skills/documents/template/assist",
            new { template = new { nonsense = 1 }, instruction = "bold" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("unknown key", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/aicore/skills/documents/template/assist",
            new { instruction = "" })).StatusCode);
    }

    [Fact]
    public async Task Assist_with_a_model_returns_the_edited_template_and_meters_it()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var before = Usage("admin");
        _f.Llm.Enabled = true;
        _f.Llm.Answer = msgs =>
        {
            Assert.Contains("teal title", msgs[0].Content);
            Assert.Contains("CURRENT TEMPLATE", msgs[0].Content);
            return "{\"template\": {\"preset\": \"modern\", \"title\": {\"color\": \"#0f766e\"}, " +
                   "\"letterhead\": {\"file\": \"\"}}, \"summary\": \"Teal title on the modern layout.\"}";
        };
        try
        {
            var lh = "0123456789abcdef0123456789abcdef.pdf";
            var r = await Ok(await admin.PostAsJsonAsync("/api/aicore/skills/documents/template/assist", new
            {
                template = new { preset = "modern", letterhead = new { file = lh } },
                instruction = "teal title please",
            }));
            Assert.Equal("ai", r.GetProperty("mode").GetString());
            Assert.Equal("#0F766E", r.GetProperty("template").GetProperty("title").GetProperty("color").GetString());
            Assert.Equal("Teal title on the modern layout.", r.GetProperty("summary").GetString());
            // the model can't drop (or invent) the uploaded letterhead
            Assert.Equal(lh, r.GetProperty("template").GetProperty("letterhead").GetProperty("file").GetString());
            Assert.Equal(before + 1, Usage("admin"));
        }
        finally { _f.Llm.Enabled = false; }
    }

    // ---- the skill stores it ----------------------------------------------------------------------

    [Fact]
    public async Task The_skills_put_stores_a_normalized_template_and_refuses_a_bad_one()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var existing = (await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills")).GetProperty("skills")
            .EnumerateArray().Where(s => !s.TryGetProperty("entity", out var e) || e.ValueKind == JsonValueKind.Null)
            .Select(s => JsonSerializer.Deserialize<Dictionary<string, object?>>(s.GetRawText())!).ToList();
        existing.RemoveAll(s => s["id"]?.ToString() == "docs-tpl");
        Dictionary<string, object?> Skill(object template) => new()
        {
            ["id"] = "docs-tpl", ["name"] = "Docs", ["kind"] = "app", ["position"] = 0, ["enabled"] = true,
            ["config"] = new { app = "documents", template },
        };
        var bad = await admin.PutAsJsonAsync("/api/aicore/skills",
            new { skills = existing.Append(Skill(new { header = new { logo = "left" } })).ToList() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("unknown key 'template.header.logo'", await bad.Content.ReadAsStringAsync());

        var put = await Ok(await admin.PutAsJsonAsync("/api/aicore/skills",
            new { skills = existing.Append(Skill(new { preset = "modern", colors = new { accent = "#abc" } })).ToList() }));
        var cfg = put.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "docs-tpl")
            .GetProperty("config");
        var t = cfg.GetProperty("template");
        Assert.Equal("#AABBCC", t.GetProperty("colors").GetProperty("accent").GetString());
        Assert.True(t.GetProperty("table").GetProperty("zebra").GetBoolean());   // the modern preset filled in

        // the catalog carries the four presets for the designer's gallery
        var apps = await admin.GetFromJsonAsync<JsonElement>("/api/aicore/skills/apps");
        var docs = apps.GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("app").GetString() == "documents");
        Assert.Equal(new[] { "classic", "modern", "minimal", "bold" },
            docs.GetProperty("template_presets").EnumerateObject().Select(p => p.Name));
    }

    // ---- gates, refusals, quota ------------------------------------------------------------------

    [Fact]
    public async Task Agents_are_refused_and_bad_uploads_are_refused_by_size_and_magic_bytes()
    {
        var agent = await Platform.As(_f, "agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync("/api/aicore/skills/documents/template/analyze",
            Form(Png, "d.png", "image/png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/aicore/skills/documents/template/assist",
            new { instruction = "bold" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync("/api/aicore/skills/documents/letterhead",
            Form(DocumentTemplateTests.DesignPdf("X"), "l.pdf", "application/pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(
            "/api/aicore/skills/documents/letterhead/0123456789abcdef0123456789abcdef.pdf")).StatusCode);

        var admin = await Platform.As(_f, "admin", "admin123");
        async Task<string> Refused(HttpResponseMessage res)
        {
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }
        // a text file named .pdf: refused by its bytes, not its name
        Assert.Contains("PDF, PNG or JPEG", await Refused(await admin.PostAsync("/api/aicore/skills/documents/template/analyze",
            Form(Encoding.UTF8.GetBytes("hello, I am not a PDF"), "fake.pdf", "application/pdf"))));
        var bigPng = new byte[6 * 1024 * 1024];
        Png.CopyTo(bigPng, 0);
        Assert.Contains("5 MB", await Refused(await admin.PostAsync("/api/aicore/skills/documents/template/analyze",
            Form(bigPng, "big.png", "image/png"))));
        var hugePdf = new byte[11 * 1024 * 1024];
        Encoding.ASCII.GetBytes("%PDF-1.4").CopyTo(hugePdf, 0);
        Assert.Contains("10 MB", await Refused(await admin.PostAsync("/api/aicore/skills/documents/template/analyze",
            Form(hugePdf, "huge.pdf", "application/pdf"))));
        Assert.Contains("PDF", await Refused(await admin.PostAsync("/api/aicore/skills/documents/letterhead",
            Form(Png, "l.png", "image/png"))));
        Assert.Contains("template", await Refused(await admin.PostAsync("/api/aicore/skills/documents/template/analyze",
            Form(Png, "d.png", "image/png", new() { ["template"] = "{\"preset\":\"neon\"}" }))));

        // the letterhead upload on its own
        var up = await Ok(await admin.PostAsync("/api/aicore/skills/documents/letterhead",
            Form(DocumentTemplateTests.DesignPdf("X", pages: 3), "l.pdf", "application/pdf")));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(up.GetProperty("url").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(
            "/api/aicore/skills/documents/letterhead/0123456789abcdef0123456789abcdef.pdf")).StatusCode);
    }

    [Fact]
    public async Task A_user_at_their_ai_quota_gets_429_from_analyze_and_assist()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var admin = await Platform.As(_f, "admin", "admin123");
        With(db =>
        {
            db.LlmUsage.Add(new LlmUsage
            {
                Feature = DocumentTemplateAi.Feature, Model = "gpt-4o", UserName = "admin", PromptTokens = 10,
                CostUsd = 3.0, CreatedAt = DateTime.UtcNow,
            });
            return db.SaveChanges();
        });
        AiQuota.Invalidate();
        (await su.PutAsJsonAsync("/api/billing/ai/quotas",
            new { users = new Dictionary<string, object> { ["admin"] = 1 } })).EnsureSuccessStatusCode();
        try
        {
            var a = await admin.PostAsync("/api/aicore/skills/documents/template/analyze", Form(Png, "d.png", "image/png"));
            Assert.Equal((HttpStatusCode)429, a.StatusCode);
            Assert.True((await a.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quota").GetBoolean());
            var b = await admin.PostAsJsonAsync("/api/aicore/skills/documents/template/assist", new { instruction = "bold" });
            Assert.Equal((HttpStatusCode)429, b.StatusCode);
        }
        finally
        {
            await su.PutAsJsonAsync("/api/billing/ai/quotas",
                new { users = new Dictionary<string, object?> { ["admin"] = null } });
            With(db =>
            {
                db.LlmUsage.RemoveRange(db.LlmUsage.Where(u => u.UserName == "admin" && u.CostUsd == 3.0));
                return db.SaveChanges();
            });
            AiQuota.Invalidate();
        }
    }

    // ---- the sandbox rule set, unit-level -------------------------------------------------------

    [Theory]
    [InlineData("bold", "preset", "bold")]
    [InlineData("use #0F766E", "accent", "#0F766E")]
    [InlineData("red table header", "table_header", "#B91C1C")]
    [InlineData("logo in the center", "logo", "center")]
    [InlineData("title on the left", "title", "left")]
    [InlineData("add zebra rows", "zebra", "True")]
    [InlineData("no borders", "border", "none")]
    [InlineData("serif headings", "heading", "serif")]
    [InlineData("US letter size", "page", "Letter")]
    [InlineData("hide page numbers", "page_numbers", "False")]
    public void Sandbox_rules_understand_the_common_requests(string instruction, string what, string expected)
    {
        var (t, changes) = DocumentTemplateAi.SandboxAssist(DocumentTemplate.Classic(), instruction);
        Assert.NotEmpty(changes);
        var actual = what switch
        {
            "preset" => t.Preset,
            "accent" => t.Colors.Accent,
            "table_header" => t.Table.HeaderColor,
            "logo" => t.Header.LogoPosition,
            "title" => t.Title.Align,
            "zebra" => t.Table.Zebra.ToString(),
            "border" => t.Table.Border,
            "heading" => t.Fonts.Heading,
            "page" => t.Page.Size,
            _ => t.Footer.PageNumbers.ToString(),
        };
        Assert.Equal(expected, actual);
    }
}
