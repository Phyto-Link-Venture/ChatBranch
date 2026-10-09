using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Retrieval (RAG): EN + BM tokenising, chunking, BM25 ranking of reworded
/// questions, chunk-level retrieval of long documents, the semantic path with a
/// fake embedding model (hybrid scoring, content-hash re-embedding, provider
/// failure), the LEARNED block (rules first, entity isolation), grounding on the
/// result and the draft, and document text extraction.
/// </summary>
public class RetrievalTests
{
    private static KnowledgeItem Item(AppDbContext db, string kind, string title, string content,
        string question = "", int? entityId = null, string sourceFile = "", int priority = 0)
    {
        var k = new KnowledgeItem
        {
            Kind = kind, Title = title, Question = question, Content = content, EntityId = entityId,
            SourceFile = sourceFile, Priority = priority, Active = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.KnowledgeItems.Add(k);
        db.SaveChanges();
        return k;
    }

    private static AiLesson Lesson(AppDbContext db, string kind, string question, string answer, int? entityId = null,
        string avoid = "", string status = AiLessonStatus.Active)
    {
        var l = new AiLesson
        {
            Kind = kind, Question = question, Answer = answer, Avoid = avoid, EntityId = entityId, Status = status,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.AiLessons.Add(l);
        db.SaveChanges();
        return l;
    }

    private static Entity Entity(AppDbContext db, string slug)
    {
        var tenant = db.Tenants.FirstOrDefault() ?? new Tenant { Name = "T", Slug = "t", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        if (tenant.Id == 0) { db.Tenants.Add(tenant); db.SaveChanges(); }
        var e = new Entity { TenantId = tenant.Id, Name = slug, Slug = slug, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Entities.Add(e);
        db.SaveChanges();
        return e;
    }

    // ---- text analysis -----------------------------------------------------------

    [Fact]
    public void Terms_drop_english_and_malay_stopwords_and_stem_lightly()
    {
        var bm = TextAnalysis.Terms("Berapakah harga penghantaran ke Penang?");
        Assert.Contains("harga", bm);
        Assert.Contains("hantar", bm);       // peng- … -an
        Assert.Contains("penang", bm);
        Assert.DoesNotContain("berapakah", bm);
        Assert.DoesNotContain("ke", bm);
        Assert.Equal(TextAnalysis.Stem("membayar"), TextAnalysis.Stem("pembayaran")); // bayar
        Assert.Equal(TextAnalysis.Stem("beli"), TextAnalysis.Stem("membeli"));

        var en = TextAnalysis.Terms("What are your opening hours?");
        Assert.Equal(new[] { "open", "hour" }, en);
        Assert.Equal(TextAnalysis.Stem("refunds"), TextAnalysis.Stem("refunded"));
        // word order, case and filler don't change the dedup key
        Assert.Equal(TextAnalysis.NormalizeQuestion("Hi, what time do you OPEN on Sunday?"),
            TextAnalysis.NormalizeQuestion("sunday — open what time?"));
    }

    [Fact]
    public void Long_text_is_chunked_with_overlap_and_short_text_stays_whole()
    {
        Assert.Single(TextAnalysis.Chunk("A short fact about opening hours."));
        var sentences = Enumerable.Range(1, 60).Select(i => $"Sentence number {i} talks about topic {i} in some detail.");
        var text = string.Join(" ", sentences);
        var chunks = TextAnalysis.Chunk(text);
        Assert.True(chunks.Count >= 4, $"expected several chunks, got {chunks.Count}");
        Assert.All(chunks, c => Assert.True(c.Length <= TextAnalysis.ChunkMax, $"chunk of {c.Length} chars"));
        // consecutive chunks overlap: the start of the next repeats the end of the previous
        for (var i = 1; i < chunks.Count; i++)
        {
            var head = chunks[i][..30];
            Assert.Contains(head, chunks[i - 1]);
        }
        // nothing lost
        Assert.Contains("Sentence number 60", chunks[^1]);
        // each chunk of an item is title-prefixed for indexing
        var item = new KnowledgeItem { Id = 991, Title = "Manual", Content = text };
        Assert.All(RetrievalService.ChunksFor(item), c => Assert.StartsWith("Manual: ", c.Text));
    }

    // ---- keyword retrieval ----------------------------------------------------------

    [Fact]
    public void Bm25_finds_a_reworded_question_in_english_and_malay()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var hours = Item(t.Db, "faq", "Opening hours", "We are open 9am to 6pm, Monday to Saturday.",
            question: "What are your opening hours?");
        var delivery = Item(t.Db, "faq", "Delivery fee", "Penghantaran ke seluruh Semenanjung: RM10. Sabah & Sarawak: RM20.",
            question: "Berapa kos penghantaran?");
        Item(t.Db, "policy", "Refund policy", "Refunds within 14 days with the receipt.");
        var knowledge = new KnowledgeService(t.Db, sc);

        var en = knowledge.Compose("hi, when do you open on saturday?", null, "whatsapp");
        Assert.Equal(hours.Id, en.Hits.First(h => h.Type == "knowledge").Id);
        Assert.Contains("Opening hours", en.Knowledge);
        Assert.DoesNotContain("Refund policy", en.Knowledge); // zero hits contribute nothing
        Assert.DoesNotContain("Delivery fee", en.Knowledge);

        var ms = knowledge.Compose("berapa harga hantar barang ke Penang?", null, "whatsapp");
        Assert.Equal(delivery.Id, ms.Hits.First(h => h.Type == "knowledge").Id);
        Assert.Contains("Delivery fee", ms.Knowledge);
        Assert.False(ms.Semantic); // no embedding provider → keyword only

        // grounding names what was used
        Assert.Contains(en.Grounding, g => g.Type == "knowledge" && g.Id == hours.Id && g.Score > 0);

        // no query at all (the voice dispatch): the top faq/policy by priority, as before
        Assert.Contains("Opening hours", knowledge.BuildContext(null));
    }

    [Fact]
    public void Chinese_is_split_into_bigrams_at_every_script_change()
    {
        Assert.Equal(new[] { "寄", "5", "公斤", "kl", "吉隆", "隆坡" }, TextAnalysis.Words("寄5公斤 KL吉隆坡"));
        Assert.Equal(new[] { "灯" }, TextAnalysis.Terms("灯"));               // a lone character is a word
        Assert.Contains("槟城", TextAnalysis.Terms("从吉隆坡到槟城多少钱？"));
        var chunks = TextAnalysis.Chunk(new string('字', 500) + "。" + new string('文', 500), 700, 800);
        Assert.Equal(2, chunks.Count);
        Assert.EndsWith("字。", chunks[0]); // 。 ends a sentence with no space after it
    }

    [Fact]
    public void Bm25_finds_a_chinese_question_without_spaces()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var showroom = Item(t.Db, "faq", "Melaka showroom", "Open Monday to Saturday 10am–7pm, closed on Sunday.",
            question: "Melaka showroom hours");
        showroom.Tags = new List<string> { "展厅", "营业时间" };
        var times = Item(t.Db, "faq", "Delivery times", "Peninsular: 1–3 working days. 半岛 1–3 个工作日送达。");
        times.Tags = new List<string> { "几天", "送达" };
        t.Db.SaveChanges();
        var knowledge = new KnowledgeService(t.Db, sc);

        var zh = knowledge.Compose("你好，马六甲的展厅星期天有开吗？", null, "whatsapp");
        Assert.Equal(showroom.Id, zh.Hits.First(h => h.Type == "knowledge").Id);

        var days = knowledge.Compose("寄5公斤的包裹从吉隆坡到槟城几天到？", null, "whatsapp");
        Assert.Equal(times.Id, days.Hits.First(h => h.Type == "knowledge").Id);
    }

    [Fact]
    public void Standing_knowledge_is_always_in_and_long_documents_come_back_by_chunk()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        Item(t.Db, "notice", "Closed Friday", "We are closed this Friday for a public holiday.");
        Item(t.Db, "fact", "Store", "Our store is at 1 Jalan Ampang.");
        var filler = string.Join("\n\n", Enumerable.Range(1, 8).Select(i =>
            $"Section {i}. " + string.Join(" ", Enumerable.Repeat($"General product information paragraph {i} about colours and sizes.", 8))));
        var manual = Item(t.Db, "fact", "Product manual", filler + "\n\nWarranty: claims need the original receipt "
            + "and the serial number; the warranty lasts 24 months from purchase.", sourceFile: "manual.pdf");
        var knowledge = new KnowledgeService(t.Db, sc);

        var c = knowledge.Compose("How long is the warranty?", null, null);
        Assert.Contains("Closed Friday", c.Knowledge);           // notice: standing
        Assert.Contains("Our store is at 1 Jalan Ampang", c.Knowledge); // short fact: standing
        Assert.Contains("the warranty lasts 24 months", c.Knowledge);    // the matching chunk
        Assert.Contains("(part", c.Knowledge);
        Assert.DoesNotContain("Section 1.", c.Knowledge);         // not the whole document
        Assert.True(c.Knowledge.Length <= KnowledgeService.Budget + 20);
        Assert.Contains(c.Grounding, g => g.Id == manual.Id && g.Title.Contains("part"));

        // an unrelated question gets the standing items only
        var other = knowledge.Compose("Do you sell gift cards?", null, null);
        Assert.Contains("Closed Friday", other.Knowledge);
        Assert.DoesNotContain("Product manual", other.Knowledge);

        // with no question at all (the voice dispatch) a long TYPED fact still stands, as before;
        // an uploaded document needs a question to surface
        Item(t.Db, "fact", "About us", string.Join(" ", Enumerable.Repeat("We are a family bakery founded in 1987.", 30)));
        var voice = knowledge.BuildContext(null, null, "voice");
        Assert.Contains("About us", voice);
        Assert.DoesNotContain("Product manual", voice);
        Assert.DoesNotContain("About us", knowledge.Compose("Do you sell gift cards?").Knowledge);
    }

    // ---- semantic retrieval ---------------------------------------------------------

    [Fact]
    public async Task Semantic_path_finds_meaning_without_shared_words_and_reembeds_on_change()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var fake = new FakeEmbeddingClient();
        var fee = Item(t.Db, "faq", "Delivery fee", "We charge RM10 for delivery anywhere in Peninsular Malaysia.");
        Item(t.Db, "faq", "Opening hours", "We are open 9am to 6pm daily.");
        var knowledge = new KnowledgeService(t.Db, sc, new RetrievalService(t.Db, sc, fake));
        var indexer = new EmbeddingIndexer(t.Db, sc, fake);

        // keyword alone can't connect "postage" with "delivery"
        Assert.DoesNotContain(knowledge.Compose("how much is postage?").Hits, h => h.Id == fee.Id);

        Assert.Equal(2, indexer.Status().Pending);
        Assert.Equal(2, await indexer.SyncAsync());
        Assert.Equal(0, indexer.Status().Pending);
        Assert.Equal(2, t.Db.AiEmbeddings.Count());

        var c = knowledge.Compose("how much is postage?");
        Assert.True(c.Semantic);
        var hit = Assert.Single(c.Hits, h => h.Id == fee.Id);
        Assert.True(hit.Semantic > 0.5, $"cosine {hit.Semantic}");
        Assert.Equal(RetrievalService.SemanticWeight * hit.Semantic + RetrievalService.KeywordWeight * hit.Keyword,
            hit.Score, 6);
        Assert.Contains("Delivery fee", c.Knowledge);
        Assert.DoesNotContain(c.Hits, h => h.Title == "Opening hours");
        // the query embedding is metered like a chat call
        Assert.Contains(t.Db.LlmUsage.ToList(), u => u.Feature == "embedding" && u.Ref == "query");

        // nothing changed → nothing re-embedded
        var texts = fake.Texts;
        Assert.Equal(0, await indexer.SyncAsync());
        Assert.Equal(texts, fake.Texts);

        // an edit changes the content hash → exactly that chunk is re-embedded
        var row = t.Db.KnowledgeItems.First(k => k.Id == fee.Id);
        row.Content = "Postage is RM12 anywhere in Peninsular Malaysia.";
        row.UpdatedAt = DateTime.UtcNow;
        t.Db.SaveChanges();
        Assert.Equal(1, indexer.Status().Pending);
        Assert.Equal(1, await indexer.SyncAsync());
        Assert.Equal(texts + 1, fake.Texts);
        Assert.Equal(2, t.Db.AiEmbeddings.Count());

        // a deleted item's vectors go on the next pass
        t.Db.KnowledgeItems.Where(k => k.Id == fee.Id).ExecuteDelete();
        await indexer.SyncAsync();
        Assert.Equal(1, t.Db.AiEmbeddings.Count());
    }

    [Fact]
    public async Task A_failing_embedding_provider_falls_back_to_keywords_and_reports_the_error()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var fake = new FakeEmbeddingClient { Fail = true };
        Item(t.Db, "faq", "Refund policy", "Refunds within 14 days with the receipt.", question: "Can I get a refund?");
        var knowledge = new KnowledgeService(t.Db, sc, new RetrievalService(t.Db, sc, fake));
        var indexer = new EmbeddingIndexer(t.Db, sc, fake);

        Assert.Equal(0, await indexer.SyncAsync());
        Assert.Contains("503", indexer.Status().LastError);

        var c = knowledge.Compose("I want a refund please");
        Assert.False(c.Semantic);
        Assert.Contains("Refund policy", c.Knowledge);

        fake.Fail = false;
        Assert.Equal(1, await indexer.SyncAsync());
        Assert.Equal("", indexer.Status().LastError);
    }

    // ---- the LEARNED block ------------------------------------------------------------

    [Fact]
    public void Prompt_carries_the_learned_block_with_rules_first_and_counts_uses()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var rule = Lesson(t.Db, AiLessonKind.Rule, "", "Never promise same-day delivery.");
        var example = Lesson(t.Db, AiLessonKind.Example, "Do you deliver on Sundays?",
            "Yes — Sunday deliveries go out between 10am and 2pm.");
        var correction = Lesson(t.Db, AiLessonKind.Correction, "Can I pay by cheque?",
            "We only take card or bank transfer.", avoid: "Yes, cheques are fine.");
        Lesson(t.Db, AiLessonKind.Example, "Where do you park?", "Basement B2.", status: AiLessonStatus.Suggested);
        var assistant = new AssistantService(t.Db, sc, new PersonaService(sc), new KnowledgeService(t.Db, sc),
            new SandboxLlmClient());

        var prompt = assistant.BuildSystemPrompt(null, "whatsapp", null,
            new RetrievalQuery("do you deliver on a sunday?", "Thanks for your order!"));
        Assert.Contains("LEARNED FROM PAST CONVERSATIONS", prompt);
        var ruleAt = prompt.IndexOf("• Never promise same-day delivery.", StringComparison.Ordinal);
        var exampleAt = prompt.IndexOf("• When asked something like \"Do you deliver on Sundays?\"", StringComparison.Ordinal);
        Assert.True(ruleAt > 0 && exampleAt > ruleAt, "rules come before examples");
        Assert.DoesNotContain("cheque", prompt);   // not relevant to this question
        Assert.DoesNotContain("Basement", prompt); // suggestions never reach the prompt
        // HardRules still lead the prompt
        Assert.True(prompt.IndexOf(PersonaService.HardRules[..40], StringComparison.Ordinal) < ruleAt);

        var cheque = assistant.BuildSystemPrompt(null, "whatsapp", null, "Can I pay with a cheque?");
        Assert.Contains("• For \"Can I pay by cheque?\": avoid \"Yes, cheques are fine.\"; say instead \"We only take card or bank transfer.\"", cheque);
        _ = correction;

        // a generated reply counts the lessons it carried
        t.Db.Threads.Add(new ChatBranch.Domain.Thread { Channel = "whatsapp", Key = "601", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var thread = t.Db.Threads.First();
        t.Db.Messages.Add(new Message { ThreadId = thread.Id, Direction = "in", Source = "customer",
            Text = "Do you deliver on Sundays?", CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var r = assistant.GenerateReplyAsync(thread, applyOutcome: false).GetAwaiter().GetResult();
        Assert.Contains(r.Grounding, g => g.Type == "lesson" && g.Id == example.Id);
        Assert.Contains(r.Grounding, g => g.Type == "lesson" && g.Id == rule.Id);
        Assert.NotNull(r.ToMeta()["grounding"]);
        using var fresh = t.Fresh();
        Assert.Equal(1, fresh.AiLessons.First(l => l.Id == example.Id).Uses);
        Assert.NotNull(fresh.AiLessons.First(l => l.Id == example.Id).LastUsedAt);
    }

    [Fact]
    public void Lessons_never_cross_entities()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var a = Entity(t.Db, "outlet-a");
        var b = Entity(t.Db, "outlet-b");
        Lesson(t.Db, AiLessonKind.Rule, "", "Outlet A rule: mention the A loyalty card.", entityId: a.Id);
        Lesson(t.Db, AiLessonKind.Example, "Is parking free?", "Yes, at outlet A parking is free.", entityId: a.Id);
        Lesson(t.Db, AiLessonKind.Rule, "", "Global rule: be brief.");
        var assistant = new AssistantService(t.Db, sc, new PersonaService(sc), new KnowledgeService(t.Db, sc),
            new SandboxLlmClient());

        var forA = assistant.BuildSystemPrompt(a.Id, "whatsapp", null, "is parking free?");
        Assert.Contains("Outlet A rule", forA);
        Assert.Contains("parking is free", forA);
        Assert.Contains("Global rule", forA);

        var forB = assistant.BuildSystemPrompt(b.Id, "whatsapp", null, "is parking free?");
        Assert.DoesNotContain("Outlet A", forB);
        Assert.DoesNotContain("parking is free", forB);
        Assert.Contains("Global rule", forB);

        // an unassigned conversation gets the organization-wide lessons only
        var unassigned = assistant.BuildSystemPrompt(null, "whatsapp", null, "is parking free?");
        Assert.DoesNotContain("Outlet A", unassigned);
        Assert.Contains("Global rule", unassigned);
    }

    [Fact]
    public void Grounding_reaches_the_monitor_draft()
    {
        using var r = new AiRig();
        r.Contact("Siti", "60120009001");
        var faq = Item(r.Db, "faq", "Opening hours", "Open 9am–6pm daily.", question: "What are your opening hours?");
        r.Llm.Replies.Enqueue("We're open 9am to 6pm every day.");

        r.Say("60120009001", "What time do you open?");

        var draft = Assert.Single(r.Drafts("60120009001"));
        var grounding = AiGate.Items(draft.Context, "grounding").ToList();
        Assert.Contains(grounding, g => AiGate.Str(g, "type", "") == "knowledge" && (int)(AiGate.Num(g, "id") ?? 0) == faq.Id);
    }

    // ---- document extraction -----------------------------------------------------------

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

    [Fact]
    public void Documents_extract_to_text_and_image_only_pdfs_are_refused()
    {
        var pdf = DocumentExtractor.Extract("price list.pdf", Pdf("Price list 2026", "Haircut RM35", "Colour RM120"));
        Assert.True(pdf.Ok, pdf.Error);
        Assert.Contains("Haircut RM35", pdf.Text);
        Assert.Equal(1, pdf.Pages);

        var empty = DocumentExtractor.Extract("scan.pdf", Pdf());
        Assert.False(empty.Ok);
        Assert.Contains("No text found", empty.Error);

        var broken = DocumentExtractor.Extract("broken.pdf", "not a pdf at all"u8.ToArray());
        Assert.False(broken.Ok);

        var txt = DocumentExtractor.Extract("notes.txt", "﻿Line one\r\nLine two\n\n\n\nLine three"u8.ToArray());
        Assert.True(txt.Ok);
        Assert.Equal("Line one\nLine two\n\nLine three", txt.Text);

        var csv = DocumentExtractor.Extract("prices.csv", "Service,Price\nHaircut,RM35\n\"Wash, cut & dry\",RM60\n"u8.ToArray());
        Assert.Contains("Service: Haircut · Price: RM35", csv.Text);
        Assert.Contains("Service: Wash, cut & dry · Price: RM60", csv.Text);

        Assert.False(DocumentExtractor.Supported("contract.docx"));
        Assert.True(DocumentExtractor.Supported("Guide.MD"));
    }
}
