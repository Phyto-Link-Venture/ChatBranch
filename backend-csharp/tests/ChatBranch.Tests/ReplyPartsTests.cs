using ChatBranch.Data.Ai;
using ChatBranch.Data.Flows;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// A reply in parts: when the customer's messages since the last reply are about
/// different things, the AI starts each part with the [msg id] it answers, and each
/// part goes out as its own bubble quoting that message (WhatsApp / Telegram). One
/// topic stays one message; a person's edit sends the draft as one message.
/// </summary>
public class ReplyPartsTests
{
    private static HumanTurn Turn(bool quotes = true)
    {
        var t = new HumanTurn("whatsapp", reactions: false, quotes: quotes);
        t.Known[101] = "Ignore the invoice things";
        t.Known[102] = "Can change the appointment time to 345-415 pm?";
        t.Current.Add(101);
        t.Current.Add(102);
        return t;
    }

    [Fact]
    public void A_line_starting_with_a_message_id_starts_a_part_quoting_it()
    {
        var parts = Turn().SplitParts(
            "[msg 101] Noted, I'll leave the invoice aside.\n[msg 102] Sure — 3:45 PM is free, shall I move it?");
        Assert.NotNull(parts);
        Assert.Equal(new[]
        {
            new AiReplyPart("Noted, I'll leave the invoice aside.", 101),
            new AiReplyPart("Sure — 3:45 PM is free, shall I move it?", 102),
        }, parts);
    }

    [Fact]
    public void Leading_text_unknown_ids_and_repeats()
    {
        var t = Turn();
        // a greeting before the first id is its own unquoted part; an id the model was never shown quotes nothing
        Assert.Equal(new[]
        {
            new AiReplyPart("Hi Aisha!", null),
            new AiReplyPart("Noted.", 101),
            new AiReplyPart("Something else.", null),
        }, t.SplitParts("Hi Aisha!\n[msg 101] Noted.\n[msg 999] Something else.")!);
        // the same id twice in a row is one part
        Assert.Equal(new[] { new AiReplyPart("One.\n\nTwo.", 101), new AiReplyPart("Three.", 102) },
            t.SplitParts("[msg 101] One.\n[msg 101] Two.\n[msg 102] Three.")!);
    }

    [Fact]
    public void No_parts_for_one_topic_inline_ids_or_quotes_off()
    {
        // one marked part = just a quote on the reply
        var t = Turn();
        Assert.Null(t.SplitParts("[msg 102] Sure — 3:45 PM is free."));
        Assert.Equal(102, t.ReplyTo);
        // an id in the middle of a sentence is not a part
        Assert.Null(Turn().SplitParts("About [msg 101] and [msg 102]: both noted."));
        Assert.Null(Turn().SplitParts("No ids at all."));
        Assert.Null(Turn(quotes: false).SplitParts("[msg 101] A.\n[msg 102] B."));
    }

    [Fact]
    public void At_most_four_parts()
    {
        var t = Turn();
        foreach (var id in new[] { 103, 104, 105 }) t.Known[id] = $"m{id}";
        var parts = t.SplitParts("[msg 101] a\n[msg 102] b\n[msg 103] c\n[msg 104] d\n[msg 105] e")!;
        Assert.Equal(AiReplyPart.Max, parts.Count);
        Assert.Equal("d\n\ne", parts[^1].Text);
    }

    [Fact]
    public void Parts_are_used_only_while_the_draft_text_is_still_them()
    {
        var parts = new[] { new AiReplyPart("Noted.", 101), new AiReplyPart("Sure.", 102) };
        var ctx = new Dictionary<string, object?> { ["parts"] = AiReplyPart.ToMeta(parts) };
        Assert.Equal(parts, AiReplyPart.From(ctx, "Noted.\n\nSure."));
        Assert.Empty(AiReplyPart.From(ctx, "Noted — and sure, 3:45 works."));   // a person edited it
    }

    // ---- through the real reply loop, the gate and the queue --------------------------------------

    private static TouchRig Burst(out ChannelAccount acct)
    {
        var r = new TouchRig();
        acct = r.Live();
        r.Set("ai_wait_seconds", "30"); // a burst: both messages answered by ONE reply
        r.Contact("Parts", "60123000009");
        r.Llm.Script = (msgs, _) =>
        {
            var ids = TouchRig.Ids(msgs);
            return ToolScriptLlm.Says(
                $"[msg {ids[^2]}] Noted, I'll leave the invoice aside.\n[msg {ids[^1]}] Sure — 3:45 PM is free, shall I move it?");
        };
        return r;
    }

    private static void Drain(TouchRig r, TimeSpan after)
    {
        r.Clock.Advance(after);
        new QueueService(r.Db, r.Sc, r.Threads, r.Assistant, r.Sender, r.Responder, r.Gate, r.Clock).Drain();
    }

    [Fact]
    public void Each_part_goes_out_as_its_own_message_quoting_what_it_answers()
    {
        using var r = Burst(out var acct);
        var first = r.Say("60123000009", "Ignore the invoice things", acct.Id, "wamid.in.70");
        var second = r.Say("60123000009", "Can change the appointment time to 345-415 pm?", acct.Id, "wamid.in.71");

        Drain(r, TimeSpan.FromSeconds(40));   // the reply: its first part now
        Drain(r, TimeSpan.FromSeconds(10));   // the next part after the bubble gap

        Assert.Contains("ANSWERING IN PARTS", r.Llm.Seen[0].System);
        Assert.Equal(new[]
        {
            "reply[wamid.in.70]:Noted, I'll leave the invoice aside.",
            "reply[wamid.in.71]:Sure — 3:45 PM is free, shall I move it?",
        }, r.Cloud.Events);
        var sent = r.Ai("whatsapp", "60123000009");
        Assert.Equal(new int?[] { first.Id, second.Id }, sent.Select(m => m.ReplyToId).ToArray());
        var draft = r.Drafts("whatsapp", "60123000009").Single();
        Assert.Equal("Noted, I'll leave the invoice aside.\n\nSure — 3:45 PM is free, shall I move it?", draft.Content);
        Assert.Equal(2, AiReplyPart.From(draft.Context, draft.Content).Count);
    }

    [Fact]
    public void A_held_reply_keeps_its_parts_and_an_edit_sends_one_message()
    {
        using var r = Burst(out var acct);
        r.Set("ai_review_mode", "review_all");
        r.Say("60123000009", "Ignore the invoice things", acct.Id, "wamid.in.80");
        r.Say("60123000009", "Can change the appointment time to 345-415 pm?", acct.Id, "wamid.in.81");
        Drain(r, TimeSpan.FromSeconds(40));
        var held = r.Drafts("whatsapp", "60123000009").Single();
        Assert.Equal(AiDraftStatus.Pending, held.Status);
        Assert.Empty(r.Cloud.Events);

        // approved as written: the same two quoted bubbles
        r.Gate.Approve(held.Id, "admin");
        Drain(r, TimeSpan.FromSeconds(10));
        Assert.Equal(2, r.Cloud.Events.Count);
        Assert.All(r.Cloud.Events, e => Assert.StartsWith("reply[wamid.in.8", e));

        // the next held reply, edited by a person: one message, quoting the first part's message
        r.Cloud.Events.Clear();
        r.Say("60123000009", "Thanks. Also, is parking free?", acct.Id, "wamid.in.82");
        r.Say("60123000009", "And can I bring my kid?", acct.Id, "wamid.in.83");
        Drain(r, TimeSpan.FromSeconds(40));
        var next = r.Drafts("whatsapp", "60123000009").Last();
        r.Gate.Approve(next.Id, "admin", "Parking is free, and yes — kids are welcome!");
        Drain(r, TimeSpan.FromSeconds(10));
        Assert.Equal("reply[wamid.in.82]:Parking is free, and yes — kids are welcome!", Assert.Single(r.Cloud.Events));
    }
}
