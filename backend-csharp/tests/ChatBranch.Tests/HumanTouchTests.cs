using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>A live Cloud client that logs every send IN ORDER — reactions, quoted replies, plain text.</summary>
internal sealed class OrderedCloud : IWhatsAppCloudClient
{
    public List<string> Events { get; } = new();
    private int _n;

    public bool IsConfigured(ChannelAccount account) =>
        account.Provider == Providers.Cloud && account.AccessToken.Length > 0 && account.ExternalId.Length > 0;

    public Task<SendOutcome> SendTextAsync(ChannelAccount account, string toWaId, string text, CancellationToken ct = default)
    {
        Events.Add($"text:{text}");
        return Task.FromResult(new SendOutcome(true, $"wamid.out.{++_n}", ""));
    }

    public Task<SendOutcome> SendReplyTextAsync(ChannelAccount account, string toWaId, string text, string contextWamid,
        CancellationToken ct = default)
    {
        Events.Add($"reply[{contextWamid}]:{text}");
        return Task.FromResult(new SendOutcome(true, $"wamid.out.{++_n}", ""));
    }

    public Task<SendOutcome> SendReactionAsync(ChannelAccount account, string toWaId, string wamid, string emoji,
        CancellationToken ct = default)
    {
        Events.Add($"react[{wamid}]:{emoji}");
        return Task.FromResult(new SendOutcome(true, "", ""));
    }
}

/// <summary>The AI-ops stack (AiRig's wiring) with a scripted tool-calling model and an ordered Cloud fake.</summary>
internal sealed class TouchRig : IDisposable
{
    public TestDb T { get; } = new();
    public AppDbContext Db => T.Db;
    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public ToolScriptLlm Llm { get; } = new((_, _) => ToolScriptLlm.Says("Happy to help."));
    public OrderedCloud Cloud { get; } = new();
    public ScopingService Sc { get; }
    public ThreadStore Threads { get; }
    public OutboundSender Sender { get; }
    public AssistantService Assistant { get; }
    public AiGate Gate { get; }
    public AiResponder Responder { get; }
    public InboundProcessor Inbound { get; }

    public TouchRig()
    {
        Sc = new ScopingService(Db);
        Threads = new ThreadStore(Db, Sc);
        Sender = new OutboundSender(Db, Threads, Cloud, new ChatBranch.Data.Sidecars.SandboxBaileysGateway());
        Assistant = new AssistantService(Db, Sc, new PersonaService(Sc), new KnowledgeService(Db, Sc), Llm);
        var composer = new AiComposer(Db, Assistant, Llm);
        Gate = new AiGate(Db, Sc, Threads, Sender, Clock);
        Responder = new AiResponder(Db, Sc, composer, Gate, Clock);
        var queue = new QueueService(Db, Sc, Threads, Assistant, Sender, Responder, Gate, Clock);
        Inbound = new InboundProcessor(Db, Threads, Assistant, Sender, new FlowEngine(Db, Sc, queue), queue, Sc,
            new AutomationService(Db, Sc), Responder);

        foreach (var (k, v) in new[]
                 {
                     ("ai_review_mode", "auto"), ("ai_review_first_n", "0"), ("ai_review_notify", "false"),
                     ("ai_wait_seconds", "0"), ("ai_typing", "false"), ("ai_split", "off"), ("ai_ack_skip", "false"),
                     ("ai_followup", "false"), ("ai_reply_mode", "instant"),
                     ("ai_emoji_style", "light"), ("ai_reactions", "true"), ("ai_quote_replies", "auto"),
                 })
            Sc.SetSetting(k, v);
    }

    public void Set(string key, string value) => Sc.SetSetting(key, value);

    public ChannelAccount Live()
    {
        var a = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Cloud, Name = "Live", ExternalId = "PN-TOUCH", AccessToken = "tok",
            CreatedAt = DateTime.UtcNow,
            Config = new Dictionary<string, object?> { ["role"] = AccountRoles.Conversation },
        };
        Db.ChannelAccounts.Add(a);
        Db.SaveChanges();
        return a;
    }

    public Contact Contact(string name, string phone = "", string email = "")
    {
        var c = new Contact { Name = name, Phone = phone, Email = email, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        Db.Contacts.Add(c);
        Db.SaveChanges();
        return c;
    }

    public Message Say(string phone, string text, int accountId, string wamid) =>
        Inbound.ProcessInbound("whatsapp", phone, text, accountId: accountId, wamid: wamid);

    public Thread ThreadOf(string channel, string key) =>
        T.Fresh().Threads.Include(t => t.Account).First(t => t.Channel == channel && t.Key == key);

    public List<Message> Ai(string channel, string key)
    {
        var t = ThreadOf(channel, key);
        return T.Fresh().Messages.Where(m => m.ThreadId == t.Id && m.Source == MessageSource.Ai).OrderBy(m => m.Id).ToList();
    }

    public List<AiDraft> Drafts(string channel, string key)
    {
        var t = ThreadOf(channel, key);
        return T.Fresh().AiDrafts.Where(d => d.ThreadId == t.Id).OrderBy(d => d.Id).ToList();
    }

    public void Dispose() => T.Dispose();

    /// <summary>The ids the model was shown in front of the customer's messages, oldest first.</summary>
    public static List<int> Ids(IReadOnlyList<LlmMessage> msgs) => msgs.Where(m => m.Role == "user")
        .Select(m => Regex.Match(m.Content ?? "", @"^\[msg (\d+)\]")).Where(x => x.Success)
        .Select(x => int.Parse(x.Groups[1].Value)).ToList();

    /// <summary>A model that, on its first round, calls one tool; then answers <paramref name="text"/>.</summary>
    public static Func<IReadOnlyList<LlmMessage>, IReadOnlyList<LlmTool>?, LlmChatResult> ThenSays(
        Func<IReadOnlyList<LlmMessage>, (string Name, string Args)> call, string text) =>
        (msgs, _) => msgs.LastOrDefault()?.Role == "tool" ? ToolScriptLlm.Says(text) : ToolScriptLlm.Calls(call(msgs));
}

/// <summary>
/// The human-like touches (Ai/HumanTouch.cs): the emoji STYLE line in the prompt,
/// react_to_message (made on delivery, before the text, through the inbox's
/// reaction path; reaction-only replies; held drafts never react) and quoted
/// replies (quote_message → Cloud's context.message_id), the playground's
/// preview of both, and the settings that drive them.
/// </summary>
public class HumanTouchTests
{
    // ---- the emoji STYLE line ---------------------------------------------------------------

    [Fact]
    public void The_emoji_style_line_follows_the_setting_and_the_channel_and_sits_below_the_hard_rules()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Contact("Nadia", "60123000001");
        r.Contact("Mail", email: "mail@example.test");
        r.Set("aicore_persona", "You are Bella from the bakery.");

        string SystemFor(Action say)
        {
            r.Llm.Seen.Clear();
            say();
            return r.Llm.Seen[0].System;
        }

        var light = SystemFor(() => r.Say("60123000001", "hi", acct.Id, "wamid.s1"));
        Assert.Contains("STYLE — emoji: you may use an occasional emoji", light);
        Assert.Contains("If the persona above says otherwise about emoji, follow the persona.", light);
        Assert.StartsWith(PersonaService.HardRules, light);
        Assert.True(light.IndexOf("STYLE — emoji", StringComparison.Ordinal)
                    > light.IndexOf("You are Bella from the bakery.", StringComparison.Ordinal));

        r.Set("ai_emoji_style", "expressive");
        var expressive = SystemFor(() => r.Say("60123000001", "hello again", acct.Id, "wamid.s2"));
        Assert.Contains("one or two emoji per message", expressive);
        Assert.DoesNotContain("occasional emoji", expressive);

        r.Set("ai_emoji_style", "off");
        var off = SystemFor(() => r.Say("60123000001", "and again", acct.Id, "wamid.s3"));
        Assert.Contains("don't use emoji in your messages", off);
        Assert.DoesNotContain("occasional emoji", off);

        // email never gets emoji, whatever the style
        r.Set("ai_emoji_style", "expressive");
        var email = SystemFor(() => r.Inbound.ProcessInbound("email", "mail@example.test", "Do you deliver?"));
        Assert.Contains("this is email, so don't use emoji", email);
        Assert.DoesNotContain("one or two emoji", email);

        // an inbox (account) override wins over the organization setting
        acct.Config["ai_emoji_style"] = "off";
        r.Db.ChannelAccounts.Update(acct);
        r.Db.SaveChanges();
        var perInbox = SystemFor(() => r.Say("60123000001", "one more", acct.Id, "wamid.s4"));
        Assert.Contains("don't use emoji in your messages", perInbox);

        // voice calls are untouched: no STYLE in the preamble or the voice SKILLS block
        Assert.DoesNotContain("STYLE", new PersonaService(r.Sc).SystemPreamble());
        Assert.DoesNotContain("STYLE", r.Assistant.SkillsBlock(null));
    }

    // ---- reactions ----------------------------------------------------------------------------

    [Fact]
    public void A_react_call_on_WhatsApp_reacts_through_the_reaction_path_before_the_text()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Contact("Omar", "60123000002");
        r.Llm.Script = TouchRig.ThenSays(
            msgs => (HumanTouch.ReactTool, $"{{\"message_id\":{TouchRig.Ids(msgs).Last()},\"emoji\":\"👍\"}}"),
            "Great — see you at 3pm!");

        var inbound = r.Say("60123000002", "Confirmed for 3pm tomorrow", acct.Id, "wamid.in.1");

        // the model was shown the id and offered both touches
        Assert.Contains($"[msg {inbound.Id}] Confirmed for 3pm tomorrow",
            r.Llm.Seen[0].Messages.Last(m => m.Role == "user").Content);
        Assert.Contains(HumanTouch.ReactTool, r.Llm.Seen[0].Tools);
        Assert.Contains(HumanTouch.QuoteTool, r.Llm.Seen[0].Tools);
        Assert.Contains("REACTIONS:", r.Llm.Seen[0].System);

        // the reaction went to Meta first, then the words (never the id tag)
        Assert.Equal(new[] { "react[wamid.in.1]:👍", "text:Great — see you at 3pm!" }, r.Cloud.Events);
        var row = Assert.Single(r.T.Fresh().MessageReactions.Where(x => x.MessageId == inbound.Id));
        Assert.Equal((MessageReactions.AiActor, "👍"), (row.Actor, row.Emoji));
        var draft = Assert.Single(r.Drafts("whatsapp", "60123000002"));
        Assert.Equal(AiDraftStatus.AutoSent, draft.Status);
        Assert.Single(AiReaction.From(draft.Context));
        Assert.True(AiGate.Bool(draft.Context, "reacted"));
        Assert.Contains(r.T.Fresh().AuditLogs.ToList(), a => a.Action == "message.react" && a.Detail.Contains("👍"));

        // a person's reaction replaces the AI's (the business is one sender)
        MessageReactions.Set(r.Db, r.ThreadOf("whatsapp", "60123000002"), r.Db.Messages.Find(inbound.Id)!,
            MessageReactions.UserActor("admin"), "❤️");
        Assert.Equal("user:admin", Assert.Single(r.T.Fresh().MessageReactions.Where(x => x.MessageId == inbound.Id)).Actor);
    }

    [Fact]
    public void On_email_no_reaction_tool_is_offered_and_a_forced_call_is_refused()
    {
        using var r = new TouchRig();
        r.Contact("Mail", email: "react@example.test");
        r.Llm.Script = TouchRig.ThenSays(_ => (HumanTouch.ReactTool, "{\"message_id\":1,\"emoji\":\"👍\"}"), "Yes, we deliver.");

        r.Inbound.ProcessInbound("email", "react@example.test", "Do you deliver?");

        Assert.DoesNotContain(HumanTouch.ReactTool, r.Llm.Seen[0].Tools);
        Assert.DoesNotContain(HumanTouch.QuoteTool, r.Llm.Seen[0].Tools);
        Assert.DoesNotContain("[msg ", r.Llm.Seen[0].Messages.Last(m => m.Role == "user").Content);
        Assert.Equal("Yes, we deliver.", Assert.Single(r.Ai("email", "react@example.test")).Text);
        Assert.Empty(r.T.Fresh().MessageReactions.ToList());
        var draft = Assert.Single(r.Drafts("email", "react@example.test"));
        Assert.Empty(AiReaction.From(draft.Context));
        // and the gate itself never reacts on a channel without reactions
        var thread = r.Db.Threads.First(t => t.Channel == "email" && t.Key == "react@example.test");
        var inbound = r.T.Fresh().Messages.First(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In);
        r.Gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, "",
            new Dictionary<string, object?> { ["reactions"] = AiReaction.ToMeta(new[] { new AiReaction(inbound.Id, "👍") }) }));
        Assert.Empty(r.T.Fresh().MessageReactions.ToList());
    }

    [Fact]
    public void A_reaction_alone_answers_thanks_with_no_text()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Contact("Mei", "60123000003");
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("We close at 6pm.");
        r.Say("60123000003", "What time do you close?", acct.Id, "wamid.in.10");
        r.Cloud.Events.Clear();

        // ack-skip off: the model decides — a reaction, and no words
        r.Llm.Script = TouchRig.ThenSays(
            msgs => (HumanTouch.ReactTool, $"{{\"message_id\":{TouchRig.Ids(msgs).Last()},\"emoji\":\"🙏\"}}"), "");
        var thanks = r.Say("60123000003", "ok thanks!", acct.Id, "wamid.in.11");

        Assert.Equal(new[] { "react[wamid.in.11]:🙏" }, r.Cloud.Events);
        Assert.Single(r.Ai("whatsapp", "60123000003")); // only the earlier answer — no new bubble
        var draft = r.Drafts("whatsapp", "60123000003").Last();
        Assert.Equal(AiDraftStatus.AutoSent, draft.Status);
        Assert.Equal("", draft.Content);
        Assert.NotNull(draft.SentAt);
        Assert.Equal("🙏", r.T.Fresh().MessageReactions.Single(x => x.MessageId == thanks.Id).Emoji);
    }

    [Fact]
    public void The_acknowledgement_skip_reacts_instead_of_staying_silent()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Set("ai_ack_skip", "true");
        r.Contact("Ack", "60123000004");
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("We close at 6pm today.");
        r.Say("60123000004", "What time do you close?", acct.Id, "wamid.in.20");
        var calls = r.Llm.Seen.Count;
        r.Cloud.Events.Clear();

        var ack = r.Say("60123000004", "terima kasih", acct.Id, "wamid.in.21");

        Assert.Equal(calls, r.Llm.Seen.Count);                     // no generation spent on it
        Assert.Equal(new[] { "react[wamid.in.21]:🙏" }, r.Cloud.Events);
        Assert.Equal("🙏", r.T.Fresh().MessageReactions.Single(x => x.MessageId == ack.Id).Emoji);
        Assert.Single(r.Ai("whatsapp", "60123000004"));

        // reactions off → silence, as before
        r.Set("ai_reactions", "false");
        r.Cloud.Events.Clear();
        r.Say("60123000004", "ok", acct.Id, "wamid.in.22");
        Assert.Empty(r.Cloud.Events);
    }

    [Fact]
    public void An_invalid_emoji_is_refused()
    {
        Assert.Null(MessageReactions.ForAi("whatsapp", "abc"));
        Assert.Null(MessageReactions.ForAi("whatsapp", "👍👍"));      // one emoji, not two
        Assert.Null(MessageReactions.ForAi("whatsapp", "🖕"));        // never the AI's
        Assert.Null(MessageReactions.ForAi("email", "👍"));           // no reactions on email
        Assert.Null(MessageReactions.ForAi("telegram", "😂"));        // not in Telegram's set
        Assert.Equal("❤", MessageReactions.ForAi("telegram", "❤️"));  // Telegram's own spelling
        Assert.Equal("❤️", MessageReactions.ForAi("whatsapp", "❤️"));
        Assert.Equal("👍🏽", MessageReactions.ForAi("whatsapp", "👍🏽")); // a skin tone is one emoji

        using var r = new TouchRig();
        var acct = r.Live();
        r.Contact("Bad", "60123000005");
        r.Llm.Script = TouchRig.ThenSays(
            msgs => (HumanTouch.ReactTool, $"{{\"message_id\":{TouchRig.Ids(msgs).Last()},\"emoji\":\"lol\"}}"),
            "Sure thing.");
        r.Say("60123000005", "Can you hold it for me?", acct.Id, "wamid.in.30");

        var tool = r.Llm.Seen[1].Messages.Last(m => m.Role == "tool").Content!;
        Assert.Contains("\"ok\":false", tool);
        Assert.Equal(new[] { "text:Sure thing." }, r.Cloud.Events);
        Assert.Empty(r.T.Fresh().MessageReactions.ToList());
        // a refused touch is cosmetic: it never flags the reply for review
        var draft = Assert.Single(r.Drafts("whatsapp", "60123000005"));
        Assert.DoesNotContain(AiGate.Items(draft.Context, "flags").Select(f => f?.ToString() ?? ""),
            f => f.Contains("Tool failed"));

        // reacting to something that is not this turn's customer message is refused too
        var turn = new HumanTurn("whatsapp", reactions: true, quotes: true);
        turn.Current.Add(7);
        turn.Known[7] = "hi";
        Assert.False(turn.Execute(new LlmToolCall("c", HumanTouch.ReactTool, "{\"message_id\":8,\"emoji\":\"👍\"}")).Ok);
        Assert.False(turn.Execute(new LlmToolCall("c", HumanTouch.QuoteTool, "{\"message_id\":99}")).Ok);
        Assert.True(turn.Execute(new LlmToolCall("c", HumanTouch.ReactTool, "{\"message_id\":\"msg 7\",\"emoji\":\"🎉\"}")).Ok);
        Assert.Equal(new AiReaction(7, "🎉"), turn.Reaction);
    }

    [Fact]
    public void A_held_draft_does_not_react_until_it_is_approved()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Set("ai_review_mode", "review_all");
        r.Contact("Held", "60123000006");
        r.Llm.Script = TouchRig.ThenSays(
            msgs => (HumanTouch.ReactTool, $"{{\"message_id\":{TouchRig.Ids(msgs).Last()},\"emoji\":\"❤️\"}}"),
            "Lovely photo — that one is in stock!");

        var photo = r.Say("60123000006", "[image] this one please", acct.Id, "wamid.in.40");

        Assert.Empty(r.Cloud.Events);
        Assert.Empty(r.T.Fresh().MessageReactions.ToList());
        var draft = Assert.Single(r.Drafts("whatsapp", "60123000006"));
        Assert.Equal(AiDraftStatus.Pending, draft.Status);
        Assert.Equal(new AiReaction(photo.Id, "❤️"), Assert.Single(AiReaction.From(draft.Context)));

        // a rejected one never reacts
        var reject = r.Gate.Reject(draft.Id, "admin", "no");
        Assert.Equal("rejected", reject.Status);
        Assert.Empty(r.Cloud.Events);

        // the next one is approved: reaction first, then the words
        r.Say("60123000006", "[image] or this one", acct.Id, "wamid.in.41");
        var next = r.Drafts("whatsapp", "60123000006").Last();
        Assert.Empty(r.Cloud.Events);
        var outcome = r.Gate.Approve(next.Id, "admin");
        Assert.True(outcome.Delivered);
        Assert.Equal(new[] { "react[wamid.in.41]:❤️", "text:Lovely photo — that one is in stock!" }, r.Cloud.Events);

        // shadow mode records, never reacts
        r.Set("ai_review_mode", "shadow");
        r.Cloud.Events.Clear();
        r.Say("60123000006", "[image] and this", acct.Id, "wamid.in.42");
        Assert.Equal(AiDraftStatus.Shadow, r.Drafts("whatsapp", "60123000006").Last().Status);
        Assert.Empty(r.Cloud.Events);
    }

    // ---- quoted replies ------------------------------------------------------------------------

    [Fact]
    public void Reply_to_on_a_multi_message_turn_sends_a_quoted_reply()
    {
        using var r = new TouchRig();
        var acct = r.Live();
        r.Set("ai_wait_seconds", "30"); // a burst: both messages answered by ONE reply
        r.Contact("Quote", "60123000007");
        r.Llm.Script = TouchRig.ThenSays(
            msgs => (HumanTouch.QuoteTool, $"{{\"message_id\":{TouchRig.Ids(msgs).First()}}}"),
            "The red one is RM 45.");

        var first = r.Say("60123000007", "How much is the red one?", acct.Id, "wamid.in.50");
        r.Say("60123000007", "Also, do you open Sunday?", acct.Id, "wamid.in.51");
        Assert.Empty(r.Llm.Seen);
        r.Clock.Advance(TimeSpan.FromSeconds(40));
        var queue = new QueueService(r.Db, r.Sc, r.Threads, r.Assistant, r.Sender, r.Responder, r.Gate, r.Clock);
        queue.Drain();

        Assert.Contains("QUOTING:", r.Llm.Seen[0].System);
        Assert.Equal(2, TouchRig.Ids(r.Llm.Seen[0].Messages).Count);
        Assert.Equal(new[] { "reply[wamid.in.50]:The red one is RM 45." }, r.Cloud.Events);
        var sent = Assert.Single(r.Ai("whatsapp", "60123000007"));
        Assert.Equal(first.Id, sent.ReplyToId);
        Assert.Equal(first.Id, HumanTouch.ReplyTo(r.Drafts("whatsapp", "60123000007").Single().Context));
    }

    [Fact]
    public void Reply_to_is_ignored_on_channels_without_quotes_and_when_quoting_is_off()
    {
        using var r = new TouchRig();
        r.Contact("Mail", email: "quote@example.test");
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Noted.");
        r.Inbound.ProcessInbound("email", "quote@example.test", "Two questions");
        var thread = r.Db.Threads.First(t => t.Channel == "email" && t.Key == "quote@example.test");
        var inbound = r.T.Fresh().Messages.First(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In);

        var o = r.Gate.Submit(new AiSubmission(thread, AiDraftKind.Reply, "Answering your first question.",
            new Dictionary<string, object?> { ["reply_to"] = inbound.Id }));
        Assert.True(o.Delivered);
        Assert.Null(r.T.Fresh().Messages.Single(m => m.Id == o.MessageIds[0]).ReplyToId);

        // WhatsApp with quoting off: the tool isn't offered
        var acct = r.Live();
        r.Set("ai_quote_replies", "off");
        r.Contact("NoQuote", "60123000008");
        r.Llm.Seen.Clear();
        r.Say("60123000008", "hi", acct.Id, "wamid.in.60");
        Assert.DoesNotContain(HumanTouch.QuoteTool, r.Llm.Seen[0].Tools);
        Assert.Contains(HumanTouch.ReactTool, r.Llm.Seen[0].Tools);
    }

    // ---- settings ---------------------------------------------------------------------------------

    [Fact]
    public void The_settings_validate_and_are_entity_scoped()
    {
        Assert.Null(AiBehaviourSettings.Normalize("ai_emoji_style", "Expressive", out var style));
        Assert.Equal("expressive", style);
        Assert.NotNull(AiBehaviourSettings.Normalize("ai_emoji_style", "loud", out _));
        Assert.Null(AiBehaviourSettings.Normalize("ai_reactions", "yes", out var on));
        Assert.Equal("true", on);
        Assert.Null(AiBehaviourSettings.Normalize("ai_quote_replies", "OFF", out var quotes));
        Assert.Equal("off", quotes);
        Assert.NotNull(AiBehaviourSettings.Normalize("ai_quote_replies", "always", out _));
        foreach (var key in new[] { "ai_emoji_style", "ai_reactions", "ai_quote_replies" })
        {
            Assert.True(ScopingService.IsEntityScoped(key));
            Assert.Equal("human", AiBehaviourSettings.ByKey[key].Group);
        }

        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        var tenant = new Tenant { Name = "Org", Slug = "org-touch", Status = "active" };
        t.Db.Tenants.Add(tenant);
        t.Db.SaveChanges();
        var e = new Entity { Name = "Outlet", Slug = "outlet-touch", TenantId = tenant.Id };
        t.Db.Entities.Add(e);
        t.Db.SaveChanges();
        sc.SetSetting("ai_emoji_style", "expressive");
        sc.SetSetting("ai_emoji_style", "off", e.Id);
        sc.SetSetting("ai_reactions", "false", e.Id);
        Assert.Equal("expressive", AiBehaviourSettings.Resolve(sc, null).EmojiStyle);
        var outlet = AiBehaviourSettings.Resolve(sc, e.Id);
        Assert.Equal("off", outlet.EmojiStyle);
        Assert.False(outlet.Reactions);
        // the inbox (account) beats the entity
        var inbox = new ChannelAccount { Config = new Dictionary<string, object?> { ["ai_emoji_style"] = "light" } };
        Assert.Equal("light", AiBehaviourSettings.Resolve(sc, e.Id, inbox).EmojiStyle);
    }
}

/// <summary>The playground shows the would-be reaction and quote — and performs neither.</summary>
public class HumanTouchPlaygroundTests : IClassFixture<PlaygroundFactory>
{
    private readonly PlaygroundFactory _f;
    public HumanTouchPlaygroundTests(PlaygroundFactory f)
    {
        _f = f;
        _ = _f.CreateClient();
        SettingsTestKit.WithDb(_f, db => new ScopingService(db).SetSetting("embedding_provider", "none"));
    }

    private void Set(string key, string value) => SettingsTestKit.WithDb(_f, db => new ScopingService(db).SetSetting(key, value));

    [Fact]
    public async Task The_playground_previews_the_reaction_and_the_quote_with_no_side_effects()
    {
        Set("openai_api_key", "sk-test-openai-secret");
        try
        {
            var admin = await Platform.As(_f, "admin", "admin123");
            _f.Chat.Script = call =>
            {
                if (call.Messages.Any(m => m.GetProperty("role").GetString() == "tool"))
                    return call.Messages.Count(m => m.GetProperty("role").GetString() == "tool") == 1
                        ? ("", new[] { (HumanTouch.QuoteTool, "{\"message_id\":1}") })
                        : ("The red one is RM 45, and yes — we open on Sunday.", Array.Empty<(string, string)>());
                return ("", new[] { (HumanTouch.ReactTool, "{\"message_id\":2,\"emoji\":\"👍\"}") });
            };
            int Count() => SettingsTestKit.WithDb(_f, db => db.Messages.Count() + db.MessageReactions.Count()
                + db.AiDrafts.Count() + db.OutboundJobs.Count() + db.Threads.Count());
            var before = Count();

            var res = await admin.PostAsJsonAsync("/api/ai/playground/reply", new
            {
                channel = "whatsapp",
                profile = new { name = "Test", language = "en" },
                messages = new object[]
                {
                    new { role = "customer", text = "How much is the red one?" },
                    new { role = "customer", text = "ok and Sunday?" },
                },
                options = new { reactions = true, quote_replies = "auto", emoji_style = "expressive", show_prompt = true },
            });
            var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            Assert.True(res.IsSuccessStatusCode, body.ToString());

            Assert.Equal(1, body.GetProperty("reaction").GetProperty("message_index").GetInt32());
            Assert.Equal("👍", body.GetProperty("reaction").GetProperty("emoji").GetString());
            Assert.Equal(0, body.GetProperty("quote").GetProperty("message_index").GetInt32());
            Assert.Equal("How much is the red one?", body.GetProperty("quote").GetProperty("text").GetString());
            Assert.True(body.GetProperty("touches").GetProperty("reactions").GetBoolean());
            Assert.Contains("one or two emoji", body.GetProperty("prompt").GetProperty("system").GetString());
            // the model saw transcript positions as ids
            Assert.StartsWith("[msg 1] ", _f.Chat.Calls.First(c => c.Tools.Contains(HumanTouch.ReactTool))
                .Messages[0].GetProperty("content").GetString());
            Assert.Equal(before, Count());

            // agents may not try the touches: an override needs ai.behaviour.manage
            var agent = await Platform.As(_f, "agent", "agent123");
            var refused = await agent.PostAsJsonAsync("/api/ai/playground/reply", new
            {
                channel = "whatsapp", profile = new { name = "T", language = "en" },
                messages = new object[] { new { role = "customer", text = "hi" } },
                options = new { reactions = true },
            });
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);

            // and an unknown emoji style is a 400
            var bad = await admin.PostAsJsonAsync("/api/ai/playground/reply", new
            {
                channel = "whatsapp", profile = new { name = "T", language = "en" },
                messages = new object[] { new { role = "customer", text = "hi" } },
                options = new { emoji_style = "loud" },
            });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally
        {
            Set("openai_api_key", "");
            _f.Chat.Script = null;
        }
    }
}
