using ChatBranch.Data;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// AI learning over the real AI-ops rig: explicit 👍/👎 on AI messages and
/// drafts (one per user, re-rating replaces), implicit feedback from the
/// monitor's approve / edit / reject, lesson suggestion + merge (normalized
/// text and embedding near-duplicates), and the review / auto / off modes.
/// </summary>
public class LearningTests
{
    private static LearningActor User(AiRig r, string username, string role)
    {
        var u = r.Db.Users.FirstOrDefault(x => x.Username == username);
        if (u == null)
        {
            u = new AppUser { Username = username, Password = "x" };
            r.Db.Users.Add(u);
            r.Db.SaveChanges();
            r.Db.Profiles.Add(new Profile { UserId = u.Id, Role = role });
            r.Db.SaveChanges();
        }
        return new LearningService(r.Db, r.Sc).ActorFor(username);
    }

    private static LearningService Learning(AiRig r) => new(r.Db, r.Sc, r.Clock);

    private static bool All(int? _) => true;

    /// <summary>A customer question answered by the AI (auto mode), returning the AI message.</summary>
    private static Message Answered(AiRig r, string phone, string question, string reply)
    {
        if (!r.Db.Contacts.Any(c => c.Phone == phone)) r.Contact($"C {phone}", phone);
        r.Llm.Replies.Enqueue(reply);
        r.Say(phone, question);
        return r.AiMessages(phone).Last();
    }

    // ---- explicit feedback ------------------------------------------------------------

    [Fact]
    public void Thumbs_up_suggests_an_example_and_rerating_replaces_the_rating()
    {
        using var r = new AiRig();
        var agent = User(r, "agent", Roles.Agent);
        var msg = Answered(r, "60121110001", "Do you deliver on Sundays?", "Yes, Sunday deliveries run 10am–2pm.");
        var learning = Learning(r);

        var up = learning.Rate(agent, msg.Id, null, 1, null, "spot on", null, All);
        Assert.True(up.Ok, up.Error);
        var fb = up.Feedback!;
        Assert.Equal("Do you deliver on Sundays?", fb.Question);  // snapshot of what was asked
        Assert.Equal("Yes, Sunday deliveries run 10am–2pm.", fb.Answer);
        Assert.NotNull(fb.DraftId);                                // linked to the monitor's draft
        var lesson = up.Lesson!;
        Assert.Equal(AiLessonKind.Example, lesson.Kind);
        Assert.Equal(AiLessonStatus.Suggested, lesson.Status);    // review mode: waits for a person
        Assert.Equal(1, lesson.SourceCount);

        // re-rating (here through the monitor's draft id) replaces the same rating
        var down = learning.Rate(agent, null, fb.DraftId, -1, new[] { "wrong_info" }, "",
            "Sunday deliveries run 9am–1pm.", All);
        Assert.True(down.Ok, down.Error);
        Assert.Equal(fb.Id, down.Feedback!.Id);
        Assert.Single(r.Db.AiFeedback.Where(f => f.Kind == AiFeedbackKind.Explicit));
        // the latest correction wins on the same suggestion — no duplicate, no double count
        Assert.Equal(lesson.Id, down.Lesson!.Id);
        Assert.Equal(AiLessonKind.Correction, down.Lesson.Kind);
        Assert.Equal("Sunday deliveries run 9am–1pm.", down.Lesson.Answer);
        Assert.Equal("Yes, Sunday deliveries run 10am–2pm.", down.Lesson.Avoid);
        Assert.Equal(1, down.Lesson.SourceCount);
        Assert.Contains(r.Db.AuditLogs.ToList(), a => a.Action == "ai.feedback" && a.Target == $"message:{msg.Id}");

        // 👎 without a correction is counted, not learned
        var plain = Answered(r, "60121110002", "Where are you?", "We're online only.");
        var noLesson = learning.Rate(agent, plain.Id, null, -1, new[] { "didnt_answer" }, null, null, All);
        Assert.True(noLesson.Ok);
        Assert.Null(noLesson.Lesson);

        // withdrawing the only rating behind a suggestion removes the suggestion
        Assert.True(learning.Withdraw(fb.Id, agent.UserId!.Value));
        Assert.False(r.Db.AiLessons.Any(l => l.Id == lesson.Id));
    }

    [Fact]
    public void Only_ai_replies_can_be_rated_and_out_of_scope_ones_are_hidden()
    {
        using var r = new AiRig();
        var agent = User(r, "agent", Roles.Agent);
        var msg = Answered(r, "60121110003", "Hello?", "Hi! How can I help?");
        var learning = Learning(r);
        var customer = r.Db.Messages.First(m => m.ThreadId == msg.ThreadId && m.Direction == "in");

        Assert.Equal(400, learning.Rate(agent, customer.Id, null, 1, null, null, null, All).Status);
        Assert.Equal(400, learning.Rate(agent, msg.Id, null, 1, new[] { "rude" }, null, null, All).Status);
        Assert.Equal(400, learning.Rate(agent, msg.Id, null, 2, null, null, null, All).Status);
        Assert.Equal(404, learning.Rate(agent, msg.Id, null, 1, null, null, null, _ => false).Status);
        Assert.Equal(404, learning.Rate(agent, 999999, null, 1, null, null, null, All).Status);
    }

    // ---- implicit feedback ------------------------------------------------------------

    [Fact]
    public void Approve_edit_and_reject_write_implicit_feedback()
    {
        using var r = new AiRig();
        r.Set("ai_review_mode", "review_all");
        User(r, "admin", Roles.Admin);

        r.Contact("A", "60122220001");
        r.Llm.Replies.Enqueue("We open at 9am.");
        r.Say("60122220001", "What time do you open?");
        var approved = Assert.Single(r.Drafts("60122220001"));
        Assert.True(r.Gate.Approve(approved.Id, "admin").Delivered);

        r.Contact("B", "60122220002");
        r.Llm.Replies.Enqueue("Parking is RM5.");
        r.Say("60122220002", "Is parking free?");
        var edited = Assert.Single(r.Drafts("60122220002"));
        r.Gate.Approve(edited.Id, "admin", "Parking is free for customers.");

        r.Contact("C", "60122220003");
        r.Llm.Replies.Enqueue("Sure, we will refund you twice.");
        r.Say("60122220003", "Can I get a refund?");
        var rejected = Assert.Single(r.Drafts("60122220003"));
        r.Gate.Reject(rejected.Id, "admin", "Never promise refunds");

        // an expired draft produces nothing
        r.Contact("D", "60122220004");
        r.Llm.Replies.Enqueue("First answer");
        r.Say("60122220004", "Question one");
        r.Llm.Replies.Enqueue("Second answer");
        r.Say("60122220004", "Question two");
        Assert.Contains(r.Drafts("60122220004"), d => d.Status == AiDraftStatus.Expired);

        using var db = r.T.Fresh();
        var rows = db.AiFeedback.OrderBy(f => f.Id).ToList();
        Assert.Equal(3, rows.Count);

        var a = rows.Single(f => f.DraftId == approved.Id);
        Assert.Equal(AiFeedbackKind.Approved, a.Kind);
        Assert.Equal(1, a.Rating);
        Assert.Equal(0.5, a.Weight);
        Assert.Equal("What time do you open?", a.Question);
        Assert.NotNull(a.UserId);

        var e = rows.Single(f => f.DraftId == edited.Id);
        Assert.Equal(AiFeedbackKind.Edited, e.Kind);
        Assert.Equal("Parking is RM5.", e.Answer);                  // the AI's text
        Assert.Equal("Parking is free for customers.", e.Correction); // what was sent
        var correction = db.AiLessons.Single(l => l.Id == e.LessonId);
        Assert.Equal(AiLessonKind.Correction, correction.Kind);
        Assert.Equal("Parking is RM5.", correction.Avoid);

        var x = rows.Single(f => f.DraftId == rejected.Id);
        Assert.Equal(AiFeedbackKind.Rejected, x.Kind);
        Assert.Equal(-1, x.Rating);
        Assert.Equal("Never promise refunds", x.Note);
        Assert.Null(x.LessonId);                                     // nothing to learn the answer from

        Assert.Equal(AiLessonKind.Example, db.AiLessons.Single(l => l.Id == a.LessonId).Kind);
        Assert.Equal(3, db.AuditLogs.Count(l => l.Action == "ai.feedback"));
    }

    // ---- modes ---------------------------------------------------------------------------

    [Fact]
    public void Review_auto_and_off_modes_decide_what_becomes_a_lesson()
    {
        using var r = new AiRig();
        var admin = User(r, "admin", Roles.Admin);
        var agent = User(r, "agent", Roles.Agent);
        var learning = Learning(r);

        // review (the default): even an admin's suggestion waits
        var m1 = Answered(r, "60123330001", "Do you have gift cards?", "Yes, from RM50.");
        Assert.Equal(AiLessonStatus.Suggested, learning.Rate(admin, m1.Id, null, 1, null, null, null, All).Lesson!.Status);

        // auto: an admin's goes live (audited), an agent's still waits
        r.Set("ai_learning_mode", "auto");
        var m2 = Answered(r, "60123330002", "Can I bring my dog?", "Small dogs are welcome.");
        var live = learning.Rate(admin, m2.Id, null, 1, null, null, null, All).Lesson!;
        Assert.Equal(AiLessonStatus.Active, live.Status);
        Assert.Equal("admin", live.ApprovedBy);
        Assert.Contains(r.Db.AuditLogs.ToList(), a => a.Action == "ai.lesson.approved" && a.Target == $"lesson:{live.Id}");
        var m3 = Answered(r, "60123330003", "Is there wifi?", "Yes, free wifi.");
        Assert.Equal(AiLessonStatus.Suggested, learning.Rate(agent, m3.Id, null, 1, null, null, null, All).Lesson!.Status);

        // off: feedback is counted, nothing is learned
        r.Set("ai_learning_mode", "off");
        var m4 = Answered(r, "60123330004", "Do you sell vouchers?", "Yes.");
        var off = learning.Rate(admin, m4.Id, null, 1, null, null, null, All);
        Assert.True(off.Ok);
        Assert.Null(off.Lesson);
        Assert.Equal(3, r.Db.AiLessons.Count());
        Assert.Equal(4, r.Db.AiFeedback.Count());
    }

    [Fact]
    public void Near_identical_questions_merge_and_a_correction_sends_a_live_lesson_back_to_review()
    {
        using var r = new AiRig();
        var agent = User(r, "agent", Roles.Agent);
        var agent2 = User(r, "agent2", Roles.Agent);
        var learning = Learning(r);

        var m1 = Answered(r, "60124440001", "What time do you open on Sunday?", "10am on Sundays.");
        var m2 = Answered(r, "60124440002", "sunday - what time you open??", "We open 10am on Sunday.");
        var first = learning.Rate(agent, m1.Id, null, 1, null, null, null, All).Lesson!;
        var second = learning.Rate(agent2, m2.Id, null, 1, null, null, null, All).Lesson!;
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.SourceCount);
        Assert.Single(r.Db.AiLessons);

        // approve it, then a teammate corrects the same question: back to the queue
        var row = r.Db.AiLessons.First();
        Assert.Null(learning.Move(row, AiLessonStatus.Active, agent));
        r.Db.SaveChanges();
        var m3 = Answered(r, "60124440003", "What time do you open on sunday", "8am on Sundays.");
        var corrected = learning.Rate(agent2, m3.Id, null, -1, new[] { "wrong_info" }, null, "We open at 10am on Sundays.", All).Lesson!;
        Assert.Equal(first.Id, corrected.Id);
        Assert.Equal(AiLessonStatus.Suggested, corrected.Status);
        Assert.Equal(AiLessonKind.Correction, corrected.Kind);
        Assert.Equal("We open at 10am on Sundays.", corrected.Answer);
        Assert.Contains("Was live with", corrected.Note);
        Assert.Equal(3, corrected.SourceCount);
    }

    [Fact]
    public async Task Embedding_near_duplicates_are_merged_by_the_indexer()
    {
        using var r = new AiRig();
        var agent = User(r, "agent", Roles.Agent);
        var agent2 = User(r, "agent2", Roles.Agent);
        var learning = Learning(r);
        var m1 = Answered(r, "60125550001", "When do you open?", "9am daily.");
        var m2 = Answered(r, "60125550002", "What are your opening times?", "We open at 9am.");
        var a = learning.Rate(agent, m1.Id, null, 1, null, null, null, All).Lesson!;
        var b = learning.Rate(agent2, m2.Id, null, 1, null, null, null, All).Lesson!;
        Assert.NotEqual(a.Id, b.Id); // different words: no text-level merge

        var fake = new FakeEmbeddingClient();
        var indexer = new EmbeddingIndexer(r.Db, r.Sc, fake);
        await indexer.SyncAsync();

        using var db = r.T.Fresh();
        var left = Assert.Single(db.AiLessons.ToList());
        Assert.Equal(a.Id, left.Id);               // merged into the older one
        Assert.Equal(2, left.SourceCount);
        Assert.All(db.AiFeedback.ToList(), f => Assert.Equal(a.Id, f.LessonId));
        Assert.DoesNotContain(db.AiEmbeddings.ToList(), e => e.OwnerType == EmbeddingOwner.Lesson && e.OwnerId == b.Id);
    }

    [Fact]
    public void Status_moves_follow_the_lifecycle_and_to_knowledge_archives()
    {
        using var r = new AiRig();
        var admin = User(r, "admin", Roles.Admin);
        var learning = Learning(r);
        var l = new AiLesson
        {
            Kind = AiLessonKind.Example, Status = AiLessonStatus.Suggested, Question = "Do you ship abroad?",
            Answer = "Only to Singapore.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        r.Db.AiLessons.Add(l);
        r.Db.SaveChanges();

        Assert.NotNull(learning.Move(l, AiLessonStatus.Archived, admin));   // suggested → archived: no
        Assert.Null(learning.Move(l, AiLessonStatus.Active, admin));
        Assert.NotNull(learning.Move(l, AiLessonStatus.Dismissed, admin));  // active → dismissed: no
        Assert.Null(learning.Move(l, AiLessonStatus.Archived, admin));
        Assert.Null(learning.Move(l, AiLessonStatus.Active, admin));        // archived → active
        r.Db.SaveChanges();
        Assert.Equal(2, r.Db.AuditLogs.Count(a => a.Action == "ai.lesson.approved"));
        Assert.Equal(1, r.Db.AuditLogs.Count(a => a.Action == "ai.lesson.archived"));

        var item = learning.ToKnowledge(l, admin);
        Assert.Equal("faq", item.Kind);
        Assert.Equal("Do you ship abroad?", item.Question);
        Assert.Equal("Only to Singapore.", item.Content);
        Assert.Equal(AiLessonStatus.Archived, r.Db.AiLessons.First(x => x.Id == l.Id).Status);
    }
}
