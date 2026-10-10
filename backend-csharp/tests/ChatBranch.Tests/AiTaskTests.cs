using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Tasks;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Integrations;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>Google Sheets / Drive and a webhook receiver, answered in-process.</summary>
internal sealed class FakeTaskHttp : HttpMessageHandler, IHttpClientFactory
{
    public sealed record Call(string Method, Uri Url, string Body, byte[] Bytes, Dictionary<string, string> Headers);
    public List<Call> Calls { get; } = new();
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        lock (Calls) Calls.Add(new Call(request.Method.Method, request.RequestUri!, Encoding.UTF8.GetString(bytes), bytes, headers));
        var host = request.RequestUri!.Host;
        object payload = host switch
        {
            "www.googleapis.com" when request.Method == HttpMethod.Post =>
                new { id = "drive-file-1", webViewLink = "https://drive.google.com/file/d/drive-file-1" },
            _ => new { ok = true },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    public IEnumerable<Call> Sheets => Calls.Where(c => c.Url.Host == "sheets.googleapis.com");
    public IEnumerable<Call> DriveUploads => Calls.Where(c => c.Url.Host == "www.googleapis.com" && c.Method == "PATCH");
}

/// <summary>The inbound pipeline + the AI task service with a scripted model and fake Google.</summary>
internal sealed class AiTaskRig : IDisposable
{
    public TestDb T { get; } = new($"aitask_{Guid.NewGuid():N}");
    public AppDbContext Db => T.Db;
    public ScopingService Sc { get; }
    public ThreadStore Threads { get; }
    public ToolScriptLlm Llm { get; } = new((_, _) => ToolScriptLlm.Says("{\"relevant\":false}"));
    public FakeTaskHttp Http { get; } = new();
    public MediaStore Media { get; }
    public InboundProcessor Inbound { get; }
    public AiTaskService Tasks { get; }
    private readonly string _dir;

    public AiTaskRig()
    {
        AiQuota.Invalidate();
        _dir = Path.Combine(Path.GetTempPath(), $"aitask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        Media = new MediaStore(_dir);
        Sc = new ScopingService(Db);
        Threads = new ThreadStore(Db, Sc);
        var sender = new OutboundSender(Db, Threads, new LiveWhatsApp(), new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        var chat = new SandboxLlmClient();
        var assistant = new AssistantService(Db, Sc, new PersonaService(Sc), new KnowledgeService(Db, Sc), chat);
        var gate = new AiGate(Db, Sc, Threads, sender);
        var responder = new AiResponder(Db, Sc, new AiComposer(Db, assistant, chat), gate);
        var queue = new QueueService(Db, Sc, Threads, assistant, sender, responder, gate);
        Inbound = new InboundProcessor(Db, Threads, assistant, sender, new FlowEngine(Db, Sc, queue), queue, Sc,
            new AutomationService(Db, Sc), responder);
        var actions = new IntegrationActions(Db, new IntegrationResolver(Db, Sc), new GoogleOAuth(Db, Sc, Http), Http);
        Tasks = new AiTaskService(Db, Sc, Llm, actions, Http, Media);
    }

    public Entity Entity(string slug, int? parent = null)
    {
        var tenant = Db.Tenants.FirstOrDefault() ?? Add(new Tenant { Name = "Org", Slug = "org", Status = "active" });
        return Add(new Entity { TenantId = tenant.Id, Name = slug, Slug = slug, ParentId = parent,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
    }

    public ChannelAccount Account(int? entityId, string externalId) => Add(new ChannelAccount
    {
        Channel = Channels.Telegram, Provider = "telegram", Name = externalId, ExternalId = externalId,
        EntityId = entityId, CreatedAt = DateTime.UtcNow,
    });

    public T Add<T>(T row) where T : class
    {
        Db.Add(row);
        Db.SaveChanges();
        return row;
    }

    public AiTask Task(string name = "Save invoices", int? entityId = null, IEnumerable<string>? tools = null,
        string attachment = "", IEnumerable<string>? keywords = null, string linked = "", bool enabled = true,
        IEnumerable<string>? channels = null) => Add(new AiTask
    {
        Name = name, EntityId = entityId, Enabled = enabled, Attachment = attachment, Linked = linked,
        Keywords = keywords?.ToList() ?? new(), Channels = channels?.ToList() ?? new(),
        Instructions = "If the customer sends an invoice, extract supplier, number, date, total and currency, " +
                       "append a row to the Invoices sheet and save the file to Drive.",
        Tools = tools?.ToList() ?? new() { AiTaskTools.SheetsRow, AiTaskTools.DriveUpload, AiTaskTools.Note },
        Config = new() { ["spreadsheet_id"] = "sheet-123", ["sheet_range"] = "Invoices!A:Z" },
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    });

    public void Google(string provider) => Add(new Integration
    {
        Provider = provider, Status = "healthy", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        Credentials = new Dictionary<string, object?> { ["access_token"] = "ya29.test-token" },
    });

    /// <summary>An inbound message through the real pipeline, with an optional attachment stored like the media fetcher does.</summary>
    public Message Inbound_(string text, ChannelAccount? account = null, string key = "chat-1",
        (string Ext, string Type, byte[] Bytes)? file = null)
    {
        var msg = Inbound.ProcessInbound(Channels.Telegram, key, text, "Aisha", account?.Id,
            wamid: $"tg-{Guid.NewGuid():N}");
        if (file is { } f)
        {
            var row = Db.Messages.Find(msg.Id)!;
            row.MediaPath = Media.SaveAsync(Channels.Telegram, key, f.Bytes, f.Ext).GetAwaiter().GetResult();
            row.MediaType = f.Type;
            Db.SaveChanges();
        }
        return msg;
    }

    /// <summary>Drive the worker as if its delay had passed.</summary>
    public int RunDue() => System.Threading.Tasks.Task.Run(() => Tasks.RunDueAsync(nowUtc: DateTime.UtcNow.AddMinutes(5)))
        .GetAwaiter().GetResult();

    public static byte[] Pdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var y = 750.0;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new UglyToad.PdfPig.Core.PdfPoint(50, y), font);
            y -= 20;
        }
        return builder.Build();
    }

    public void Dispose()
    {
        T.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public class AiTaskTests
{
    /// <summary>The usual invoice run: read the PDF, append the row, save the file, then report.</summary>
    private static LlmChatResult InvoiceScript(IReadOnlyList<LlmMessage> msgs, IReadOnlyList<LlmTool>? tools)
    {
        var done = msgs.Where(m => m.Role == "tool").ToList();
        if (done.Count == 0)
            return ToolScriptLlm.Calls(
                (AiTaskTools.SheetsRow, "{\"values\":[\"Acme Supplies\",\"INV-123\",\"2026-10-01\",\"1250.00\",\"MYR\"]}"),
                (AiTaskTools.DriveUpload, "{\"file_name\":\"INV-123 Acme\"}"));
        return ToolScriptLlm.Says("{\"relevant\":true,\"extracted\":{\"supplier\":\"Acme Supplies\",\"invoice_number\":" +
                                  "\"INV-123\",\"total\":\"1250.00\",\"currency\":\"MYR\"},\"summary\":\"Invoice INV-123 " +
                                  "from Acme Supplies (MYR 1250.00) saved to Sheets and Drive\"}");
    }

    [Fact]
    public void An_inbound_pdf_runs_the_task_extracts_calls_its_tools_and_notes_it_but_never_messages_the_customer()
    {
        using var rig = new AiTaskRig();
        rig.Google("google_sheets");
        rig.Google("google_drive");
        var task = rig.Task(attachment: AiTaskAttachments.Pdf);
        rig.Llm.Script = InvoiceScript;

        var msg = rig.Inbound_("here is the invoice",
            file: (".pdf", "document", AiTaskRig.Pdf("INVOICE INV-123", "Acme Supplies", "Total MYR 1,250.00")));
        var pending = rig.Db.AiTaskRuns.AsNoTracking().Single();
        Assert.Equal(AiTaskRunStatus.Pending, pending.Status); // queued, not run inline
        var outBefore = rig.Db.Messages.Count(m => m.Direction == MessageDirection.Out && !m.Private);
        var jobsBefore = rig.Db.OutboundJobs.Count();

        Assert.Equal(1, rig.RunDue());

        var run = rig.Db.AiTaskRuns.AsNoTracking().Single();
        Assert.Equal(AiTaskRunStatus.Ok, run.Status);
        Assert.Equal(msg.Id, run.MessageId);
        Assert.Contains("INV-123", run.Extracted);
        Assert.Contains(AiTaskTools.SheetsRow, run.Steps);
        Assert.Contains("INV-123", run.Summary);
        // the model saw the PDF's text layer
        var seen = rig.Llm.Seen.First().Messages.First(m => m.Role == "user").Content!;
        Assert.Contains("INV-123", seen);
        Assert.Contains("Acme Supplies", seen);
        // the sheet row (time, name, phone in front) and the file in Drive, for real (fake HTTP)
        var sheet = Assert.Single(rig.Http.Sheets);
        Assert.Contains("sheet-123", sheet.Url.ToString());
        Assert.Contains("INV-123", sheet.Body);
        var upload = Assert.Single(rig.Http.DriveUploads);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(upload.Bytes, 0, 4));
        Assert.Contains(rig.Http.Calls, c => c.Method == "POST" && c.Body.Contains("INV-123 Acme.pdf"));
        // the private note + audit + usage
        var note = rig.Db.Messages.AsNoTracking().Where(m => m.Private).OrderByDescending(m => m.Id).First();
        Assert.StartsWith("🤖 Save invoices:", note.Text);
        Assert.Contains(rig.Db.AuditLogs.AsNoTracking(), a => a.Action == AiTaskService.AuditAction);
        Assert.Contains(rig.Db.LlmUsage.AsNoTracking(), u => u.Feature == AiTaskService.Feature && u.UserName == "");
        Assert.Equal(1, rig.Db.AiTasks.AsNoTracking().Single(t => t.Id == task.Id).RunCount);
        // NEVER a message to the customer
        Assert.Equal(outBefore, rig.Db.Messages.Count(m => m.Direction == MessageDirection.Out && !m.Private));
        Assert.Equal(jobsBefore, rig.Db.OutboundJobs.Count());
        // no send tool was ever offered
        Assert.All(rig.Llm.Seen, s => Assert.DoesNotContain(s.Tools, n => n.Contains("send")));
    }

    [Fact]
    public void Idempotent_per_task_and_message()
    {
        using var rig = new AiTaskRig();
        rig.Task(tools: new[] { AiTaskTools.Note });
        rig.Llm.Script = (msgs, _) => msgs.Any(m => m.Role == "tool")
            ? ToolScriptLlm.Says("{\"relevant\":true,\"summary\":\"noted\"}")
            : ToolScriptLlm.Calls((AiTaskTools.Note, "{\"text\":\"seen\"}"));
        var msg = rig.Inbound_("receipt attached");
        var thread = rig.Db.Threads.Single();
        // a redelivered webhook / a second enqueue never queues twice
        Assert.Equal(0, AiTaskService.Enqueue(rig.Db, rig.Sc, thread, msg));
        Assert.Equal(1, rig.RunDue());
        Assert.Equal(0, AiTaskService.Enqueue(rig.Db, rig.Sc, thread, msg));
        Assert.Equal(0, rig.RunDue());
        Assert.Single(rig.Db.AiTaskRuns.AsNoTracking());
        Assert.Single(rig.Llm.Seen.Where(s => s.Messages.Count == 1));
    }

    [Fact]
    public void A_tool_outside_the_allowlist_is_refused()
    {
        using var rig = new AiTaskRig();
        rig.Task(tools: new[] { AiTaskTools.Note });
        rig.Llm.Script = (msgs, _) => msgs.Any(m => m.Role == "tool")
            ? ToolScriptLlm.Says("{\"relevant\":true,\"summary\":\"tried\"}")
            : ToolScriptLlm.Calls((AiTaskTools.Label, "{\"label\":\"invoice\"}"), (AiTaskTools.Handover, "{\"reason\":\"x\"}"));
        rig.Inbound_("invoice");
        rig.RunDue();
        // only the allowlisted tool was offered
        Assert.Equal(new[] { AiTaskTools.Note }, rig.Llm.Seen[0].Tools);
        var run = rig.Db.AiTaskRuns.AsNoTracking().Single();
        Assert.Equal(AiTaskRunStatus.Failed, run.Status);
        Assert.Contains("not allowed", run.Steps);
        var thread = rig.Db.Threads.AsNoTracking().Single();
        Assert.Empty(thread.Labels);
        Assert.Null(thread.HandoverAt);
    }

    [Fact]
    public void Conditions_filter_the_messages()
    {
        using var rig = new AiTaskRig();
        rig.Task("PDF only", attachment: AiTaskAttachments.Pdf);
        rig.Task("Keyword", keywords: new[] { "receipt", "resit" });
        rig.Task("Email only", channels: new[] { Channels.Email });
        rig.Task("Linked only", linked: AiTaskLinked.Yes);

        rig.Inbound_("hello there", file: (".jpg", "image", new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 }));
        // queued: the PDF task (checked at run time) and the linked-only task; not the keyword or email task
        var queued = rig.Db.AiTaskRuns.AsNoTracking().Select(r => r.TaskId).ToList();
        var names = rig.Db.AiTasks.AsNoTracking().Where(t => queued.Contains(t.Id)).Select(t => t.Name).ToList();
        Assert.DoesNotContain("Keyword", names);
        Assert.DoesNotContain("Email only", names);
        rig.RunDue();
        // an image is not a PDF and the chat is not linked: neither ran, nothing recorded
        Assert.Empty(rig.Db.AiTaskRuns.AsNoTracking());
        Assert.Empty(rig.Llm.Seen);

        rig.Inbound_("Here is my RESIT", key: "chat-2");
        rig.RunDue();
        var ran = rig.Db.AiTaskRuns.AsNoTracking().ToList();
        Assert.Single(ran);
        Assert.Equal("Keyword", rig.Db.AiTasks.AsNoTracking().Single(t => t.Id == ran[0].TaskId).Name);
    }

    [Fact]
    public void A_disabled_task_or_another_outlets_task_does_not_run()
    {
        using var rig = new AiTaskRig();
        var a = rig.Entity("branch-a");
        var b = rig.Entity("branch-b");
        rig.Task("Off", enabled: false);
        rig.Task("For B", entityId: b.Id);
        var mine = rig.Task("For A", entityId: a.Id);
        var account = rig.Account(a.Id, "bot-a");
        rig.Inbound_("invoice please", account);
        var queued = rig.Db.AiTaskRuns.AsNoTracking().ToList();
        Assert.Single(queued);
        Assert.Equal(mine.Id, queued[0].TaskId);
        Assert.Equal(a.Id, queued[0].EntityId);

        // switched off after it was queued → skipped, said plainly
        rig.Db.AiTasks.Single(t => t.Id == mine.Id).Enabled = false;
        rig.Db.SaveChanges();
        rig.RunDue();
        var run = rig.Db.AiTaskRuns.AsNoTracking().Single();
        Assert.Equal(AiTaskRunStatus.Skipped, run.Status);
        Assert.Contains("switched off", run.Error);
        Assert.Empty(rig.Llm.Seen);
    }

    [Fact]
    public void Master_switch_off_or_quota_reached_skips_the_run()
    {
        using var rig = new AiTaskRig();
        var a = rig.Entity("outlet-q");
        rig.Task(entityId: a.Id, tools: new[] { AiTaskTools.Note });
        var account = rig.Account(a.Id, "bot-q");

        rig.Sc.SetSetting(AiControlKeys.Master, AiMasterModes.Off);
        rig.Inbound_("invoice 1", account);
        rig.RunDue();
        var first = rig.Db.AiTaskRuns.AsNoTracking().Single();
        Assert.Equal(AiTaskRunStatus.Skipped, first.Status);
        Assert.Contains("switched off", first.Error);

        rig.Sc.SetSetting(AiControlKeys.Master, AiMasterModes.On);
        AiQuota.SaveConfig(rig.Sc, new AiQuotaConfig { Entities = { [a.Id.ToString()] = 1 } });
        rig.Add(new LlmUsage { Feature = "chat_reply", Model = "gpt-4o", EntityId = a.Id, CostUsd = 50, CreatedAt = DateTime.UtcNow });
        AiQuota.Invalidate();
        rig.Inbound_("invoice 2", account);
        rig.RunDue();
        var second = rig.Db.AiTaskRuns.AsNoTracking().OrderBy(r => r.Id).Last();
        Assert.Equal(AiTaskRunStatus.Skipped, second.Status);
        Assert.Contains("quota", second.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Llm.Seen);
        AiQuota.Invalidate();
    }

    [Fact]
    public void An_irrelevant_message_is_skipped_without_a_note()
    {
        using var rig = new AiTaskRig();
        rig.Task(tools: new[] { AiTaskTools.Note });
        rig.Inbound_("what time do you open?");
        var notesBefore = rig.Db.Messages.Count(m => m.Private);
        rig.RunDue();
        var run = rig.Db.AiTaskRuns.AsNoTracking().Single();
        Assert.Equal(AiTaskRunStatus.Skipped, run.Status);
        Assert.Equal(notesBefore, rig.Db.Messages.Count(m => m.Private));
    }

    [Fact]
    public void The_dry_run_calls_no_write_tools()
    {
        using var rig = new AiTaskRig();
        rig.Google("google_sheets");
        rig.Google("google_drive");
        var task = rig.Task();
        rig.Llm.Script = InvoiceScript;
        var before = (rig.Db.Messages.Count(), rig.Db.AiTaskRuns.Count(), rig.Db.AuditLogs.Count());

        var o = Task.Run(() => rig.Tasks.TestAsync(task, "invoice attached",
            new AiTaskService.Attachment("inv.pdf", "application/pdf", AiTaskRig.Pdf("INV-123", "Total 99.00")), null))
            .GetAwaiter().GetResult();

        Assert.False(o.ModelFailed);
        Assert.Equal(2, o.Steps.Count);
        Assert.All(o.Steps, s => Assert.True(s.Preview));
        Assert.Contains("INV-123", o.ExtractedJson);
        Assert.Empty(rig.Http.Calls); // nothing reached Google
        Assert.Equal(before, (rig.Db.Messages.Count(), rig.Db.AiTaskRuns.Count(), rig.Db.AuditLogs.Count()));
        Assert.Contains(rig.Db.LlmUsage.AsNoTracking(), u => u.Feature == AiTaskService.Feature);
    }

    [Fact]
    public void An_image_goes_to_a_vision_model_as_a_data_url_else_it_is_noted()
    {
        using var rig = new AiTaskRig();
        var task = rig.Task(tools: new[] { AiTaskTools.Note });
        var jpeg = new AiTaskService.Attachment("slip.jpg", "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 });
        // ToolScriptLlm doesn't read images (the interface default)
        Task.Run(() => rig.Tasks.TestAsync(task, "payment slip", jpeg, null)).GetAwaiter().GetResult();
        var user = rig.Llm.Seen[0].Messages[0];
        Assert.Null(user.Images);
        Assert.Contains("not readable without a vision model", user.Content);

        var vision = new VisionLlm();
        var svc = new AiTaskService(rig.Db, rig.Sc, vision,
            new IntegrationActions(rig.Db, new IntegrationResolver(rig.Db, rig.Sc), new GoogleOAuth(rig.Db, rig.Sc, rig.Http), rig.Http),
            rig.Http, rig.Media);
        Task.Run(() => svc.TestAsync(task, "payment slip", jpeg, null)).GetAwaiter().GetResult();
        var img = Assert.Single(vision.Last!.Images!);
        Assert.StartsWith("data:image/jpeg;base64,", img);
    }

    private sealed class VisionLlm : ILlmClient
    {
        public LlmMessage? Last;
        public bool IsConfigured => true;
        public bool SupportsVision => true;
        public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
            CancellationToken ct = default) => Task.FromResult(new LlmResult("", 0, 0, "test", "v"));
        public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5, CancellationToken ct = default)
        {
            Last = messages[0];
            return Task.FromResult(ToolScriptLlm.Says("{\"relevant\":false}"));
        }
    }

    [Fact]
    public void The_webhook_is_signed_https_only_and_refused_without_a_secret()
    {
        Assert.NotNull(AiTaskService.WebhookUrlProblem("http://example.com/hook"));
        Assert.NotNull(AiTaskService.WebhookUrlProblem("https://localhost/hook"));
        Assert.NotNull(AiTaskService.WebhookUrlProblem("https://192.168.1.10/hook"));
        Assert.Null(AiTaskService.WebhookUrlProblem("https://hooks.example.com/in"));

        using var rig = new AiTaskRig();
        var task = rig.Task(tools: new[] { AiTaskTools.Webhook });
        task.Config["webhook_url"] = "https://hooks.example.com/in";
        rig.Db.SaveChanges();
        rig.Llm.Script = (msgs, _) => msgs.Any(m => m.Role == "tool")
            ? ToolScriptLlm.Says("{\"relevant\":true,\"summary\":\"posted\"}")
            : ToolScriptLlm.Calls((AiTaskTools.Webhook, "{\"data\":{\"invoice\":\"INV-9\"}}"));
        rig.Inbound_("invoice INV-9");
        rig.RunDue();
        var run = rig.Db.AiTaskRuns.AsNoTracking().Single();
        Assert.Equal(AiTaskRunStatus.Failed, run.Status);
        Assert.Contains("secret", run.Error);
        Assert.Empty(rig.Http.Calls);

        var t = rig.Db.AiTasks.Single();
        t.Config = new Dictionary<string, object?>(t.Config) { ["webhook_secret"] = "s3cret-s3cret-s3cret" };
        rig.Db.SaveChanges();
        rig.Inbound_("invoice INV-10", key: "chat-9");
        rig.RunDue();
        var post = Assert.Single(rig.Http.Calls);
        Assert.Equal("hooks.example.com", post.Url.Host);
        var sig = post.Headers[AiTaskService.SignatureHeader];
        var ts = long.Parse(sig.Split(',')[0][2..]);
        Assert.Equal(AiTaskService.Sign("s3cret-s3cret-s3cret", ts, post.Body), sig);
        Assert.Contains("INV-9", post.Body);
    }

    [Fact]
    public void Final_answer_parsing_is_lenient()
    {
        var (rel, ext, sum) = AiTaskService.ParseFinal("Done!\n```json\n{\"relevant\":true,\"extracted\":{\"a\":1},\"summary\":\"ok\"}\n```");
        Assert.True(rel);
        Assert.Contains("\"a\"", ext);
        Assert.Equal("ok", sum);
        Assert.Null(AiTaskService.ParseFinal("no json here").Relevant);
        Assert.DoesNotContain("abcdef123456", AiTaskTools.Redact("Authorization: Bearer abcdef123456789"));
    }
}

/// <summary>The HTTP surface: gates, CRUD + "Available to", the dry run, the simulate hook.</summary>
public class AiTaskApiTests : IClassFixture<PlaygroundFactory>
{
    private readonly PlaygroundFactory _f;

    public AiTaskApiTests(PlaygroundFactory f)
    {
        _f = f;
        _ = _f.CreateClient();
    }

    private Task<HttpClient> Admin() => Platform.As(_f, "admin", "admin123");
    private Task<HttpClient> Agent() => Platform.As(_f, "agent", "agent123");
    private T With<T>(Func<AppDbContext, T> act) => SettingsTestKit.WithDb(_f, act);
    private void Set(string key, string value) => SettingsTestKit.WithDb(_f, db => new ScopingService(db).SetSetting(key, value));

    private static object NewTask(string name, object? extra = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["instructions"] = "Extract the invoice and save a row.",
            ["tools"] = new[] { AiTaskTools.SheetsRow, AiTaskTools.Note },
            ["config"] = new { spreadsheet_id = "sheet-1" },
        };
        if (extra != null)
            foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject()) d[p.Name] = p.Value;
        return d;
    }

    [Fact]
    public async Task Gates_crud_and_validation()
    {
        var admin = await Admin();
        var agent = await Agent();

        // agents (ai.monitor) read; only settings.ai.manage writes
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/ai/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/ai/tasks", NewTask("Nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync("/api/ai/tasks/tick", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/ai/tasks",
            NewTask("Bad", new { tools = new[] { "send_message" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/ai/tasks",
            NewTask("Hook", new { tools = new[] { AiTaskTools.Webhook }, config = new { webhook_url = "https://x.example.com/h" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/ai/tasks",
            NewTask("Http", new { config = new { webhook_url = "http://x.example.com/h" } }))).StatusCode);

        var res = await admin.PostAsJsonAsync("/api/ai/tasks", NewTask("Invoices API", new
        {
            tools = new[] { AiTaskTools.Webhook, AiTaskTools.Note },
            config = new { webhook_url = "https://hooks.example.com/in", webhook_secret = "0123456789abcdef-secret" },
        }));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        Assert.True(created.GetProperty("global").GetBoolean());
        var masked = created.GetProperty("config").GetProperty("webhook_secret").GetString()!;
        Assert.DoesNotContain("0123456789", masked);

        // a masked secret coming back keeps the stored one
        var put = await admin.PutAsJsonAsync($"/api/ai/tasks/{id}", new { enabled = false, config = new { webhook_secret = masked } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("0123456789abcdef-secret", With(db => AiTaskService.Conf(db.AiTasks.Single(t => t.Id == id), "webhook_secret")));

        var list = await agent.GetFromJsonAsync<JsonElement>("/api/ai/tasks");
        Assert.Contains(list.GetProperty("tasks").EnumerateArray(), t => t.GetProperty("id").GetInt32() == id);
        Assert.True(list.GetProperty("catalog").GetProperty("builtins").GetArrayLength() >= 8);
        Assert.False(list.GetProperty("can_manage").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"/api/ai/tasks/{id}/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/ai/tasks/999999/runs")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/ai/tasks/{id}")).StatusCode);
        Assert.Contains(With(db => db.AuditLogs.Where(a => a.Action.StartsWith("ai_task.")).Select(a => a.Action).ToList()),
            a => a == "ai_task.delete");
    }

    [Fact]
    public async Task Dry_run_previews_tools_and_stores_nothing()
    {
        foreach (var p in new[] { "openrouter", "google", "ilmu" }) Set($"{p}_api_key", "");
        Set("openai_api_key", "sk-test-openai-secret");
        try
        {
            var admin = await Admin();
            var created = await (await admin.PostAsJsonAsync("/api/ai/tasks", NewTask("Dry run task"))).Content
                .ReadFromJsonAsync<JsonElement>();
            var id = created.GetProperty("id").GetInt32();
            _f.Chat.Script = call => call.Messages.Any(m => m.GetProperty("role").GetString() == "tool")
                ? ("{\"relevant\":true,\"extracted\":{\"number\":\"INV-7\"},\"summary\":\"would save INV-7\"}", Array.Empty<(string, string)>())
                : ("", new[] { (AiTaskTools.SheetsRow, "{\"values\":[\"INV-7\"]}"), (AiTaskTools.Note, "{\"text\":\"hi\"}") });
            var before = With(db => (db.Messages.Count(), db.AiTaskRuns.Count()));

            var res = await admin.PostAsJsonAsync($"/api/ai/tasks/{id}/test", new
            {
                text = "invoice INV-7",
                attachment = new { name = "inv.pdf", base64 = Convert.ToBase64String(AiTaskRig.Pdf("INV-7")) },
            });
            var body = await res.Content.ReadAsStringAsync();
            Assert.True(res.IsSuccessStatusCode, body);
            var r = JsonDocument.Parse(body).RootElement;
            Assert.True(r.GetProperty("dry_run").GetBoolean());
            Assert.Equal("INV-7", r.GetProperty("extracted").GetProperty("number").GetString());
            Assert.All(r.GetProperty("steps").EnumerateArray(), s => Assert.True(s.GetProperty("preview").GetBoolean()));
            Assert.Equal(before, With(db => (db.Messages.Count(), db.AiTaskRuns.Count())));
            // the PDF's text reached the model
            Assert.Contains(_f.Chat.Calls, c => c.Messages.Any(m => m.ToString().Contains("INV-7")));
        }
        finally
        {
            _f.Chat.Script = null;
            Set("openai_api_key", "");
        }
    }

    [Fact]
    public async Task A_simulated_inbound_queues_the_task_and_tick_runs_it()
    {
        var admin = await Admin();
        var created = await (await admin.PostAsJsonAsync("/api/ai/tasks", NewTask("Sim task", new { keywords = new[] { "zebra-invoice" } })))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        var sim = await admin.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "whatsapp", key = "60100009999", text = "zebra-invoice attached", name = "Zed" });
        Assert.True(sim.IsSuccessStatusCode, await sim.Content.ReadAsStringAsync());
        var run = With(db => db.AiTaskRuns.AsNoTracking().Single(r => r.TaskId == id));
        Assert.Equal(AiTaskRunStatus.Pending, run.Status);
        // due later (the attachment may still be downloading); move it to now and tick
        With(db => db.AiTaskRuns.Where(r => r.Id == run.Id).ExecuteUpdate(s => s.SetProperty(r => r.DueAt, DateTime.UtcNow.AddMinutes(-1))));
        var tick = await (await admin.PostAsJsonAsync("/api/ai/tasks/tick", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(tick.GetProperty("ran").GetInt32() >= 1);
        var after = With(db => db.AiTaskRuns.AsNoTracking().Single(r => r.Id == run.Id));
        // the test host has no AI model: skipped, and it says why
        Assert.Equal(AiTaskRunStatus.Skipped, after.Status);
        var runs = await admin.GetFromJsonAsync<JsonElement>($"/api/ai/tasks/{id}/runs");
        Assert.Equal(1, runs.GetProperty("runs").GetArrayLength());
        await admin.DeleteAsync($"/api/ai/tasks/{id}");
    }
}
