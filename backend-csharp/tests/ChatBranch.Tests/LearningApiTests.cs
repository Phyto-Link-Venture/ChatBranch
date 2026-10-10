using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// AI learning over HTTP (with a fake embedding model): feedback endpoints,
/// the lesson lifecycle, capability gates and entity scope on every endpoint,
/// the monitor's my_feedback + grounding, document upload, the retrieval
/// preview, index status / re-index, stats + knowledge gaps, the settings
/// fields, and the additive schema upgrade.
/// </summary>
public class LearningApiTests : IClassFixture<LearningFactory>
{
    private readonly LearningFactory _factory;
    public LearningApiTests(LearningFactory factory) => _factory = factory;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private T WithDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _factory.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private int EntityId(string slug) => WithDb(db =>
    {
        var e = db.Entities.FirstOrDefault(x => x.Slug == slug);
        if (e != null) return e.Id;
        e = new Entity { TenantId = db.Tenants.First().Id, Name = slug.ToUpperInvariant(), Slug = slug,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Entities.Add(e);
        db.SaveChanges();
        return e.Id;
    });

    /// <summary>A conversation where the AI answered: (thread, customer msg, AI msg, draft).</summary>
    private (int Thread, int Customer, int Ai, int Draft) Conversation(string key, string question, string answer,
        int? entityId = null, Dictionary<string, object?>? context = null) => WithDb(db =>
    {
        var contact = new Contact { Name = $"C {key}", Phone = key, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Contacts.Add(contact);
        db.SaveChanges();
        var t = new Thread { Channel = "whatsapp", Key = key, Address = key, Name = contact.Name, ContactId = contact.Id,
            EntityId = entityId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Threads.Add(t);
        db.SaveChanges();
        var q = new Message { ThreadId = t.Id, Direction = "in", Source = "customer", Text = question,
            Status = "received", CreatedAt = DateTime.UtcNow.AddSeconds(-30) };
        var a = new Message { ThreadId = t.Id, Direction = "out", Source = "ai", Text = answer,
            Status = "sent", CreatedAt = DateTime.UtcNow };
        db.Messages.AddRange(q, a);
        db.SaveChanges();
        var d = new AiDraft { ThreadId = t.Id, ThreadKey = key, Channel = "whatsapp", ContactId = contact.Id,
            EntityId = entityId, Kind = AiDraftKind.Reply, Content = answer, Status = AiDraftStatus.AutoSent,
            Context = context ?? new Dictionary<string, object?>(),
            SentMessageId = a.Id, SentAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.AiDrafts.Add(d);
        db.SaveChanges();
        return (t.Id, q.Id, a.Id, d.Id);
    });

    private void AssignAgent(params int[] entityIds) => WithDb(db =>
    {
        var profile = db.Profiles.Include(p => p.Entities).Include(p => p.User).First(p => p.User!.Username == "agent");
        profile.Entities.Clear();
        foreach (var id in entityIds) profile.Entities.Add(db.Entities.First(e => e.Id == id));
        return db.SaveChanges();
    });

    // ---- feedback ---------------------------------------------------------------------

    [Fact]
    public async Task Feedback_endpoints_rate_read_replace_and_withdraw()
    {
        var conv = Conversation("60131000001", "Do you open on public holidays?", "Yes, 10am to 4pm.");
        var agent = await Authed("agent", "agent123");

        var up = await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = conv.Ai, rating = 1 });
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        var fb = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("feedback");
        var id = fb.GetProperty("id").GetInt32();
        Assert.Equal(1, fb.GetProperty("rating").GetInt32());
        Assert.Equal("suggested", fb.GetProperty("lesson_status").GetString());
        Assert.True(fb.GetProperty("lesson_id").GetInt32() > 0);

        var mine = await agent.GetFromJsonAsync<JsonElement>($"/api/ai/feedback?message_ids={conv.Ai},{conv.Customer}");
        var map = mine.GetProperty("feedback");
        Assert.Equal(1, map.GetProperty(conv.Ai.ToString()).GetProperty("rating").GetInt32());
        Assert.False(map.TryGetProperty(conv.Customer.ToString(), out _));

        // re-rating through the monitor's draft replaces the same rating
        var down = await agent.PostAsJsonAsync("/api/ai/feedback", new
        {
            draft_id = conv.Draft, rating = -1, reasons = new[] { "wrong_info", "too_long" },
            note = "holiday hours changed", correction = "We're closed on public holidays.",
        });
        var dfb = (await down.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("feedback");
        Assert.Equal(id, dfb.GetProperty("id").GetInt32());
        Assert.Equal(new[] { "wrong_info", "too_long" },
            dfb.GetProperty("reasons").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("We're closed on public holidays.", dfb.GetProperty("correction").GetString());

        // other people's ratings are their own; the admin sees none of the agent's
        var admin = await Authed("admin", "admin123");
        var adminView = await admin.GetFromJsonAsync<JsonElement>($"/api/ai/feedback?message_ids={conv.Ai}");
        Assert.Empty(adminView.GetProperty("feedback").EnumerateObject());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/ai/feedback/{id}")).StatusCode);

        // only AI replies; only known reasons; rating ±1
        Assert.Equal(HttpStatusCode.BadRequest,
            (await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = conv.Customer, rating = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = conv.Ai, rating = -1, reasons = new[] { "meh" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = conv.Ai, rating = 5 })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await agent.DeleteAsync($"/api/ai/feedback/{id}")).StatusCode);
        Assert.False(WithDb(db => db.AiFeedback.Any(f => f.Id == id)));

        // anonymous callers are refused
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/ai/feedback", new { message_id = conv.Ai, rating = 1 })).StatusCode);
    }

    [Fact]
    public async Task Feedback_follows_the_conversation_scope()
    {
        var a = EntityId("learn-a");
        var b = EntityId("learn-b");
        var inA = Conversation("60131000011", "Is there parking?", "Yes.", a);
        var inB = Conversation("60131000012", "Is there parking?", "No.", b);
        AssignAgent(a);
        try
        {
            var agent = await Authed("agent", "agent123");
            Assert.Equal(HttpStatusCode.OK,
                (await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = inA.Ai, rating = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = inB.Ai, rating = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await agent.PostAsJsonAsync("/api/ai/feedback", new { draft_id = inB.Draft, rating = 1 })).StatusCode);
        }
        finally { AssignAgent(); }
    }

    // ---- lessons -------------------------------------------------------------------------

    [Fact]
    public async Task Lesson_lifecycle_over_http_with_caps_and_audit()
    {
        var admin = await Authed("admin", "admin123");
        var agent = await Authed("agent", "agent123");

        // a suggestion from an agent's 👍
        var conv = Conversation("60131000021", "Can I change my booking?", "Yes, up to 24 hours before.");
        var rated = await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = conv.Ai, rating = 1 });
        var lessonId = (await rated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("feedback")
            .GetProperty("lesson_id").GetInt32();

        var queue = await agent.GetFromJsonAsync<JsonElement>("/api/ai/lessons?status=suggested");
        var item = queue.GetProperty("lessons").EnumerateArray().Single(l => l.GetProperty("id").GetInt32() == lessonId);
        Assert.Equal("Can I change my booking?", item.GetProperty("question").GetString());
        Assert.Equal("example", item.GetProperty("kind").GetString());
        var source = item.GetProperty("feedback").EnumerateArray().Single();
        Assert.Equal("agent", source.GetProperty("user").GetString());
        Assert.Equal("whatsapp:60131000021", source.GetProperty("thread").GetString());
        Assert.True(queue.GetProperty("counts").GetProperty("suggested").GetInt32() >= 1);

        // agents read but can't decide
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PatchAsJsonAsync($"/api/ai/lessons/{lessonId}", new { status = "active" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsJsonAsync("/api/ai/lessons", new { kind = "rule", answer = "x" })).StatusCode);

        // edit then approve in one go
        var approve = await admin.PatchAsJsonAsync($"/api/ai/lessons/{lessonId}",
            new { answer = "Yes — free changes up to 24 hours before.", status = "active" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lesson");
        Assert.Equal("active", approved.GetProperty("status").GetString());
        Assert.Equal("admin", approved.GetProperty("approved_by").GetString());
        Assert.True(WithDb(db => db.AuditLogs.Any(a => a.Action == "ai.lesson.edited" && a.Target == $"lesson:{lessonId}")
                                 && db.AuditLogs.Any(a => a.Action == "ai.lesson.approved" && a.Target == $"lesson:{lessonId}")));

        // illegal move
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PatchAsJsonAsync($"/api/ai/lessons/{lessonId}", new { status = "dismissed" })).StatusCode);

        // a hand-written rule goes live at once and reaches the prompt preview
        var rule = await admin.PostAsJsonAsync("/api/ai/lessons", new { kind = "rule", answer = "Never promise same-day delivery." });
        Assert.Equal(HttpStatusCode.OK, rule.StatusCode);
        var ruleId = (await rule.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lesson").GetProperty("id").GetInt32();
        var preview = await (await admin.PostAsJsonAsync("/api/aicore/knowledge/preview",
            new { query = "can I change my booking?" })).Content.ReadFromJsonAsync<JsonElement>();
        var learned = preview.GetProperty("learned_block").GetString()!;
        Assert.Contains("Never promise same-day delivery.", learned);
        Assert.Contains("free changes up to 24 hours", learned);
        Assert.Contains(preview.GetProperty("hits").EnumerateArray(), h => h.GetProperty("type").GetString() == "lesson");

        // into the knowledge base: an FAQ, and the lesson is archived
        var conv2 = await admin.PostAsync($"/api/ai/lessons/{lessonId}/to-knowledge", null);
        Assert.Equal(HttpStatusCode.OK, conv2.StatusCode);
        var kid = (await conv2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("knowledge_id").GetInt32();
        Assert.True(WithDb(db => db.KnowledgeItems.Any(k => k.Id == kid && k.Kind == "faq")));
        Assert.Equal("archived", WithDb(db => db.AiLessons.First(l => l.Id == lessonId).Status));
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PatchAsJsonAsync($"/api/ai/lessons/{lessonId}", new { status = "active" })).StatusCode);

        // hard delete is admin+ only
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.DeleteAsync($"/api/ai/lessons/{ruleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/ai/lessons/{ruleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PatchAsJsonAsync($"/api/ai/lessons/{ruleId}", new { note = "x" })).StatusCode);
    }

    [Fact]
    public async Task Lessons_list_is_bounded_by_the_callers_outlets()
    {
        var a = EntityId("lesson-a");
        var b = EntityId("lesson-b");
        var ids = WithDb(db =>
        {
            AiLesson L(int? e, string text) => new() { EntityId = e, Kind = AiLessonKind.Rule, Status = AiLessonStatus.Active,
                Answer = text, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var la = L(a, "rule for A");
            var lb = L(b, "rule for B");
            var lg = L(null, "rule for everyone");
            db.AiLessons.AddRange(la, lb, lg);
            db.SaveChanges();
            return (A: la.Id, B: lb.Id, G: lg.Id);
        });
        AssignAgent(a);
        try
        {
            var agent = await Authed("agent", "agent123");
            List<int> Ids(JsonElement j) => j.GetProperty("lessons").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();
            var mine = Ids(await agent.GetFromJsonAsync<JsonElement>("/api/ai/lessons?kind=rule"));
            Assert.Contains(ids.A, mine);
            Assert.Contains(ids.G, mine);
            Assert.DoesNotContain(ids.B, mine);
            Assert.DoesNotContain(ids.B, Ids(await agent.GetFromJsonAsync<JsonElement>("/api/ai/lessons?entity=lesson-b")));

            var admin = await Authed("admin", "admin123");
            var all = Ids(await admin.GetFromJsonAsync<JsonElement>("/api/ai/lessons?kind=rule&q=rule%20for"));
            Assert.Contains(ids.B, all);
            var onlyB = Ids(await admin.GetFromJsonAsync<JsonElement>("/api/ai/lessons?entity=lesson-b&kind=rule"));
            Assert.Contains(ids.B, onlyB);
            Assert.Contains(ids.G, onlyB);  // inherited
            Assert.DoesNotContain(ids.A, onlyB);
        }
        finally { AssignAgent(); }
    }

    // ---- the monitor ------------------------------------------------------------------------

    [Fact]
    public async Task Monitor_drafts_carry_my_feedback_and_grounding()
    {
        var grounding = new List<object?>
        {
            new Dictionary<string, object?> { ["type"] = "knowledge", ["id"] = 7, ["title"] = "Opening hours", ["chunk"] = "…", ["score"] = 0.8 },
            new Dictionary<string, object?> { ["type"] = "knowledge", ["id"] = 7, ["title"] = "Opening hours", ["chunk"] = "…", ["score"] = 0.4 },
            new Dictionary<string, object?> { ["type"] = "lesson", ["id"] = 12, ["title"] = "Lesson #12: open?", ["chunk"] = "…", ["score"] = 0.9 },
        };
        var conv = Conversation("60131000031", "When do you open?", "9am.",
            context: new Dictionary<string, object?> { ["grounding"] = grounding });
        var agent = await Authed("agent", "agent123");
        await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = conv.Ai, rating = -1, reasons = new[] { "tone" } });

        var one = await agent.GetFromJsonAsync<JsonElement>($"/api/ai/monitor/{conv.Draft}");
        var draft = one.GetProperty("draft");
        Assert.Equal(-1, draft.GetProperty("my_feedback").GetProperty("rating").GetInt32());
        var g = draft.GetProperty("grounding").EnumerateArray().ToList();
        Assert.Equal(2, g.Count);                         // one entry per item
        Assert.Equal("lesson", g[0].GetProperty("type").GetString()); // most relevant first
        Assert.Equal(0.8, g[1].GetProperty("score").GetDouble());

        var admin = await Authed("admin", "admin123");
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/ai/monitor?status=auto_sent&page_size=100");
        var row = list.GetProperty("drafts").EnumerateArray().Single(d => d.GetProperty("id").GetInt32() == conv.Draft);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("my_feedback").ValueKind); // the admin hasn't rated it

        // the behaviour settings carry the learning mode
        var behaviour = await admin.GetFromJsonAsync<JsonElement>("/api/ai/behaviour");
        var field = behaviour.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "ai_learning_mode");
        Assert.Equal("review", field.GetProperty("default").GetString());
        Assert.Equal("learning", field.GetProperty("group").GetString());
        Assert.Contains(behaviour.GetProperty("groups").EnumerateArray(), x => x.GetProperty("id").GetString() == "learning");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync("/api/ai/behaviour", new { values = new { ai_learning_mode = "sometimes" } })).StatusCode);
        // entity-scoped: one outlet learns automatically, the organization still reviews
        EntityId("learn-mode");
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync("/api/ai/behaviour?entity=learn-mode", new { values = new { ai_learning_mode = "auto" } })).StatusCode);
        Assert.Equal("auto", (await admin.GetFromJsonAsync<JsonElement>("/api/ai/learning/status?entity=learn-mode"))
            .GetProperty("mode").GetString());
        Assert.Equal("review", (await admin.GetFromJsonAsync<JsonElement>("/api/ai/learning/status"))
            .GetProperty("mode").GetString());
    }

    // ---- upload + preview + index ---------------------------------------------------------

    private static byte[] Pdf(params string[] lines)
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

    private static MultipartFormDataContent Form(string name, byte[] bytes, string? title = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", name);
        if (title != null) form.Add(new StringContent(title), "title");
        return form;
    }

    [Fact]
    public async Task Upload_preview_and_reindex()
    {
        var admin = await Authed("admin", "admin123");
        var agent = await Authed("agent", "agent123");

        // .txt → one fact, chunked by retrieval, source file kept, never published
        var text = "Courier partners and postage\n\n" + string.Join(" ", Enumerable.Range(1, 40)
            .Select(i => $"Line {i} explains how parcels move between our warehouses."));
        var txt = await admin.PostAsync("/api/aicore/knowledge/upload", Form("shipping-guide.txt", System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(HttpStatusCode.OK, txt.StatusCode);
        var tj = await txt.Content.ReadFromJsonAsync<JsonElement>();
        var item = tj.GetProperty("item");
        Assert.Equal("shipping-guide.txt", item.GetProperty("source_file").GetString());
        Assert.Equal("shipping-guide.txt", item.GetProperty("title").GetString());
        Assert.Equal("fact", item.GetProperty("kind").GetString());
        Assert.False(item.GetProperty("published").GetBoolean());
        Assert.True(tj.GetProperty("chunks").GetInt32() >= 2);

        // a generated PDF, titled
        var pdf = await admin.PostAsync("/api/aicore/knowledge/upload",
            Form("prices.pdf", Pdf("Delivery fee list", "We charge RM10 for delivery in the Klang Valley."), "Price list"));
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var pj = await pdf.Content.ReadFromJsonAsync<JsonElement>();
        var pdfId = pj.GetProperty("item").GetProperty("id").GetInt32();
        Assert.Contains("RM10", WithDb(db => db.KnowledgeItems.First(k => k.Id == pdfId).Content));
        Assert.Equal("Price list", pj.GetProperty("item").GetProperty("title").GetString());

        // refused: an image-only PDF, an unsupported type, an agent without settings.ai.manage
        var scan = await admin.PostAsync("/api/aicore/knowledge/upload", Form("scan.pdf", Pdf()));
        Assert.Equal(HttpStatusCode.BadRequest, scan.StatusCode);
        Assert.Contains("No text found", (await scan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            (await admin.PostAsync("/api/aicore/knowledge/upload", Form("contract.docx", new byte[] { 1, 2, 3 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PostAsync("/api/aicore/knowledge/upload", Form("x.txt", "hello"u8.ToArray()))).StatusCode);

        // the semantic index: pending → indexed by the indexer → re-index queues it all again
        var status = await agent.GetFromJsonAsync<JsonElement>("/api/ai/learning/status");
        var emb = status.GetProperty("embedding");
        Assert.True(emb.GetProperty("configured").GetBoolean());
        Assert.Equal("fake", emb.GetProperty("provider").GetString());
        Assert.True(emb.GetProperty("pending").GetInt32() > 0);
        Assert.Equal("review", status.GetProperty("mode").GetString());

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<EmbeddingIndexer>().SyncAsync(10_000);
        var after = (await agent.GetFromJsonAsync<JsonElement>("/api/ai/learning/status")).GetProperty("embedding");
        Assert.Equal(0, after.GetProperty("pending").GetInt32());
        Assert.True(after.GetProperty("indexed").GetInt32() > 0);

        // "postage" shares no word with the PDF's "delivery" — the semantic path finds it
        var preview = await (await agent.PostAsJsonAsync("/api/aicore/knowledge/preview",
            new { query = "how much is postage?" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview.GetProperty("semantic").GetBoolean());
        var hit = preview.GetProperty("hits").EnumerateArray().First(h => h.GetProperty("id").GetInt32() == pdfId);
        Assert.True(hit.GetProperty("semantic").GetDouble() > 0.3);
        Assert.True(hit.TryGetProperty("keyword", out _));
        Assert.Contains("RM10", preview.GetProperty("block").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync("/api/ai/learning/reindex", null)).StatusCode);
        var re = await admin.PostAsync("/api/ai/learning/reindex", null);
        Assert.Equal(HttpStatusCode.OK, re.StatusCode);
        Assert.True((await re.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetInt32() > 0);
        Assert.True((await agent.GetFromJsonAsync<JsonElement>("/api/ai/learning/status"))
            .GetProperty("embedding").GetProperty("pending").GetInt32() > 0);
    }

    // ---- stats ------------------------------------------------------------------------------

    [Fact]
    public async Task Stats_count_ratings_reasons_and_knowledge_gaps()
    {
        var gapEntity = EntityId("stats-gap");
        var c1 = Conversation("60131000041", "Do you deliver to Sabah?", "I'm not sure.", gapEntity);
        var c2 = Conversation("60131000042", "Can you deliver to Sabah next week?", "Maybe.", gapEntity);
        var c3 = Conversation("60131000043", "What colours do you have?", "Red and blue.", gapEntity);
        var held = Conversation("60131000044", "Do you deliver to Sabah island?", "", gapEntity,
            new Dictionary<string, object?>
            {
                ["action"] = "handover",
                ["answering"] = new List<object?> { new Dictionary<string, object?> { ["text"] = "Do you deliver to Sabah island?" } },
            });
        _ = held;
        var agent = await Authed("agent", "agent123");
        var admin = await Authed("admin", "admin123");
        await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = c1.Ai, rating = -1, reasons = new[] { "didnt_answer" } });
        await admin.PostAsJsonAsync("/api/ai/feedback", new { message_id = c2.Ai, rating = -1, reasons = new[] { "wrong_info", "tone" } });
        await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = c3.Ai, rating = 1 });

        var stats = await admin.GetFromJsonAsync<JsonElement>("/api/ai/learning/stats?days=7&entity=stats-gap");
        var totals = stats.GetProperty("totals");
        Assert.Equal(3, totals.GetProperty("rated").GetInt32());
        Assert.Equal(1, totals.GetProperty("up").GetInt32());
        Assert.Equal(2, totals.GetProperty("down").GetInt32());
        Assert.Equal(33.3, totals.GetProperty("up_rate").GetDouble());
        Assert.Equal(7, stats.GetProperty("daily").GetArrayLength());
        Assert.Equal(1, stats.GetProperty("daily")[6].GetProperty("up").GetInt32());
        var reasons = stats.GetProperty("reasons").EnumerateArray()
            .ToDictionary(r => r.GetProperty("reason").GetString()!, r => r.GetProperty("count").GetInt32());
        Assert.Equal(1, reasons["didnt_answer"]);
        Assert.Equal(1, reasons["wrong_info"]);
        Assert.Equal(1, reasons["tone"]);

        var gap = stats.GetProperty("gaps").EnumerateArray().First();
        Assert.Equal(3, gap.GetProperty("count").GetInt32()); // two 👎 + one handover, all about Sabah delivery
        Assert.Contains("sabah", gap.GetProperty("topic").GetString());
        Assert.StartsWith("whatsapp:", gap.GetProperty("thread").GetString());
        Assert.False(string.IsNullOrEmpty(gap.GetProperty("example").GetString()));

        // agents read stats too; an unknown outlet doesn't widen anything
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync("/api/ai/learning/stats")).StatusCode);
    }

    [Fact]
    public async Task Every_channel_payload_carries_the_message_pk_the_thumbs_key_on()
    {
        var ids = WithDb(db =>
        {
            var t = new Thread { Channel = "telegram", Key = "tg-learn-1", Name = "TG", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Threads.Add(t);
            db.SaveChanges();
            var m = new Message { ThreadId = t.Id, Direction = "out", Source = "ai", Text = "Hello from the AI",
                ExternalId = "tg-42", CreatedAt = DateTime.UtcNow };
            db.Messages.Add(m);
            db.SaveChanges();
            return m.Id;
        });
        var agent = await Authed("agent", "agent123");
        var thread = await agent.GetFromJsonAsync<JsonElement>("/api/messaging/threads/telegram/tg-learn-1");
        var msg = thread.GetProperty("messages").EnumerateArray().Single();
        Assert.Equal(ids, msg.GetProperty("pk").GetInt32());
        Assert.Equal("tg-42", msg.GetProperty("id").GetString()); // unchanged
        Assert.Equal(HttpStatusCode.OK,
            (await agent.PostAsJsonAsync("/api/ai/feedback", new { message_id = msg.GetProperty("pk").GetInt32(), rating = 1 })).StatusCode);

        // a hand-written lesson can also be parked for review
        var admin = await Authed("admin", "admin123");
        var parked = await admin.PostAsJsonAsync("/api/ai/lessons",
            new { kind = "example", question = "Do you do gift wrap?", answer = "Yes, RM5.", status = "suggested" });
        var lesson = (await parked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lesson");
        Assert.Equal("suggested", lesson.GetProperty("status").GetString());
        Assert.Equal("", lesson.GetProperty("approved_by").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/ai/lessons", new { kind = "rule", answer = "x", status = "archived" })).StatusCode);
    }

    // ---- settings ------------------------------------------------------------------------------

    [Fact]
    public async Task Ai_providers_expose_and_validate_the_embedding_provider()
    {
        var su = await Authed("superadmin", "super123");
        var get = await su.GetFromJsonAsync<JsonElement>("/api/settings/ai-providers");
        Assert.Contains(get.GetProperty("fields").EnumerateArray(), f => f.GetProperty("key").GetString() == "embedding_provider");
        Assert.Contains(get.GetProperty("embedding_provider_options").EnumerateArray(), o => o.GetString() == "none");
        Assert.True(get.TryGetProperty("embedding_status", out _));

        var bad = await su.PutAsJsonAsync("/api/settings/ai-providers", new { embedding_provider = "cohere" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains(get.GetProperty("embedding_provider_options").EnumerateArray(), o => o.GetString() == "openrouter");
        var ok = await su.PutAsJsonAsync("/api/settings/ai-providers",
            new { embedding_provider = "none", embedding_model = "text-embedding-3-large" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("none", body.GetProperty("embedding_provider").GetString());
        Assert.Equal("text-embedding-3-large", body.GetProperty("embedding_model").GetString());
        Assert.False(body.GetProperty("embedding_status").GetProperty("configured").GetBoolean());
    }

    // ---- schema upgrade -----------------------------------------------------------------------

    [Fact]
    public void Upgrade_schema_adds_the_learning_tables_to_an_existing_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatbranch-upgrade-{Guid.NewGuid():N}.sqlite3");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
            using (var db = new AppDbContext(options))
            {
                db.Database.EnsureCreated();
                // an "old" database: no learning tables, no source_file column
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_aifeedback\"");
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_ailesson\"");
                db.Database.ExecuteSqlRaw("DROP TABLE \"crm_embedding\"");
                db.Database.ExecuteSqlRaw("ALTER TABLE \"crm_knowledgeitem\" DROP COLUMN \"source_file\"");
            }
            using (var db = new AppDbContext(options))
            {
                Seed.Run(db);
                db.KnowledgeItems.Add(new KnowledgeItem { Title = "t", Content = "c", SourceFile = "a.pdf",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                db.AiLessons.Add(new AiLesson { Question = "q", Answer = "a", Channels = new() { "whatsapp" },
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                db.AiFeedback.Add(new AiFeedback { UserId = 1, MessageId = 5, Rating = 1, Reasons = new() { "tone" },
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                db.AiEmbeddings.Add(new AiEmbedding { OwnerId = 1, Vector = VectorMath.ToBlob(new[] { 1f, 0f }),
                    Dims = 2, UpdatedAt = DateTime.UtcNow });
                db.SaveChanges();
                Assert.Equal("a.pdf", db.KnowledgeItems.AsNoTracking().First().SourceFile);
                Assert.Equal(new[] { 1f, 0f }, VectorMath.FromBlob(db.AiEmbeddings.AsNoTracking().First().Vector));
                // the explicit-rating uniqueness survives the upgrade
                db.AiFeedback.Add(new AiFeedback { UserId = 1, MessageId = 5, Rating = -1,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                Assert.Throws<DbUpdateException>(() => db.SaveChanges());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { }
        }
    }
}
