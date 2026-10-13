using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;

namespace ChatBranch.Data.Ai;

/// <summary>
/// One emoji reaction an AI reply carries — made when the reply is DELIVERED
/// (before its text), never while it is held, shadowed, rejected or expired.
/// Travels on <see cref="AiReplyResult.Reactions"/> and, through the draft
/// context, as <c>reactions: [{message_id, emoji}]</c>.
/// </summary>
public sealed record AiReaction(int MessageId, string Emoji)
{
    public static List<Dictionary<string, object?>> ToMeta(IEnumerable<AiReaction> list) =>
        list.Select(r => new Dictionary<string, object?> { ["message_id"] = r.MessageId, ["emoji"] = r.Emoji })
            .ToList();

    /// <summary>The reactions recorded in a draft / job context (CLR objects or JSON).</summary>
    public static List<AiReaction> From(Dictionary<string, object?>? ctx)
    {
        var list = new List<AiReaction>();
        if (ctx == null) return list;
        try
        {
            foreach (var item in AiGate.Items(ctx, "reactions"))
            {
                var emoji = AiGate.Str(item, "emoji", "");
                if (emoji.Length == 0 || AiGate.Num(item, "message_id") is not double id) continue;
                list.Add(new AiReaction((int)id, emoji));
            }
        }
        catch { /* a malformed context reacts with nothing */ }
        return list.Distinct().Take(HumanTouch.MaxReactions).ToList();
    }
}

/// <summary>
/// One part of a reply that answers several customer messages about different
/// things: its own bubble, quoting the message it answers (<see cref="ReplyTo"/>;
/// null = an unquoted part). The model writes a part by starting it on a new line
/// with the <c>[msg &lt;id&gt;]</c> it was shown (<see cref="HumanTurn.SplitParts"/>).
/// Travels on <see cref="AiReplyResult.Parts"/> and, through the draft context, as
/// <c>parts: [{text, reply_to}]</c> — used at delivery only while the draft's text is
/// still exactly those parts (a person's edit sends it as one message).
/// </summary>
public sealed record AiReplyPart(string Text, int? ReplyTo)
{
    /// <summary>A reply is at most this many parts (more are folded into the last).</summary>
    public const int Max = 4;

    /// <summary>The draft text of a reply in parts (what the customer reads, top to bottom).</summary>
    public static string Join(IEnumerable<AiReplyPart> parts) =>
        string.Join("\n\n", parts.Select(p => p.Text.Trim()).Where(t => t.Length > 0));

    public static List<Dictionary<string, object?>> ToMeta(IEnumerable<AiReplyPart> parts) =>
        parts.Select(p => new Dictionary<string, object?> { ["text"] = p.Text, ["reply_to"] = p.ReplyTo }).ToList();

    /// <summary>
    /// The parts a draft / job context carries, when <paramref name="content"/> is still
    /// exactly them (else none: the reply goes as before, one message).
    /// </summary>
    public static List<AiReplyPart> From(Dictionary<string, object?>? ctx, string? content)
    {
        var list = new List<AiReplyPart>();
        if (ctx == null) return list;
        try
        {
            foreach (var item in AiGate.Items(ctx, "parts"))
            {
                var text = AiGate.Str(item, "text", "").Trim();
                if (text.Length == 0) continue;
                list.Add(new AiReplyPart(text, AiGate.Num(item, "reply_to") is double d && d > 0 ? (int)d : null));
            }
        }
        catch { return new List<AiReplyPart>(); }
        return list.Count >= 2 && Join(list) == (content ?? "").Trim() ? list : new List<AiReplyPart>();
    }
}

/// <summary>
/// The human-like touches of an AI reply (settings <c>ai_emoji_style</c>,
/// <c>ai_reactions</c>, <c>ai_quote_replies</c> — <see cref="AiBehaviourSettings"/>):
///  * an emoji STYLE line in the system prompt, below the hard rules, house rules
///    and persona — a persona that says otherwise wins, and email gets none;
///  * <c>react_to_message(message_id, emoji)</c> on channels with reactions
///    (<see cref="MessageReactions.Supports"/>, not a Twilio WhatsApp number): one
///    reaction on the customer's messages of THIS turn, validated against the
///    channel's set (<see cref="MessageReactions.ForAi"/>); a reaction can stand
///    alone, with no text;
///  * <c>quote_message(message_id)</c>: the reply's first bubble quotes that
///    customer message (WhatsApp / Telegram), when the turn held several messages
///    or the answer refers to an older one;
///  * a reply in PARTS (<see cref="AiReplyPart"/>): when the customer's messages are
///    about different things, each part starts with the <c>[msg id]</c> it answers
///    and goes out as its own bubble quoting that message.
/// The model sees ids next to the customer's messages (<see cref="Tag"/>). Chat
/// replies only — follow-ups and flow messages get the style line, voice calls nothing.
/// </summary>
public static class HumanTouch
{
    public const string ReactTool = "react_to_message";
    public const string QuoteTool = "quote_message";
    public const int MaxReactions = 1;

    /// <summary>The emoji STYLE line for a channel ("" never — every style says something).</summary>
    public static string EmojiLine(string style, string? channel)
    {
        const string persona = " If the persona above says otherwise about emoji, follow the persona.";
        if (channel == ChatBranch.Domain.Channels.Email)
            return "STYLE — emoji: this is email, so don't use emoji." + persona;
        return (style ?? "").ToLowerInvariant() switch
        {
            "expressive" =>
                "STYLE — emoji: write warmly, the way a friendly person texts — one or two emoji per message where "
                + "they fit naturally; never in serious, complaint or apology contexts and never in place of words. "
                + "Mirror the customer's tone." + persona,
            "off" => "STYLE — emoji: don't use emoji in your messages." + persona,
            _ =>
                "STYLE — emoji: you may use an occasional emoji where a person would — at most one per message, never "
                + "in serious, complaint or apology contexts, never in place of words. Mirror the customer: if they "
                + "use none, use few." + persona,
        };
    }

    /// <summary>Can the AI react on this conversation? (Twilio's WhatsApp API sends no reactions.)</summary>
    public static bool ReactionsPossible(string channel, ChannelAccount? account) =>
        MessageReactions.Supports(channel)
        && !(channel == ChatBranch.Domain.Channels.WhatsApp && account is { } a
             && a.Provider != Providers.Baileys && !WhatsAppProviders.SupportsReactions(a));

    /// <summary>Can a reply quote a message on this conversation? (Twilio sends it unquoted.)</summary>
    public static bool QuotesPossible(string channel, ChannelAccount? account) =>
        MessageReactions.Supports(channel)
        && !(channel == ChatBranch.Domain.Channels.WhatsApp && account is { } a
             && a.Provider != Providers.Baileys && !WhatsAppProviders.SupportsQuotes(a));

    /// <summary>The id the model sees in front of a customer's message.</summary>
    public static string Tag(int id) => $"[msg {id}] ";

    private static readonly Regex Tags = new(@"\[msg\s*#?\d+\]\s?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A reply never carries the ids the model was shown.</summary>
    public static string StripTags(string text) => string.IsNullOrEmpty(text) ? text : Tags.Replace(text, "").Trim();

    /// <summary>The quoted message a draft / job context asks for (null = none).</summary>
    public static int? ReplyTo(Dictionary<string, object?>? ctx) =>
        ctx != null && AiGate.Num(ctx, "reply_to") is double d && d > 0 ? (int)d : null;

    internal static readonly LlmTool ReactDef = new(ReactTool,
        "React to one of the customer's messages from this turn with ONE emoji, the way a person would "
        + "(👍 on \"ok\", 🙏 or ❤️ on \"thanks\", ❤️ on a photo, 👍 on a confirmation). At most one reaction "
        + "per reply. The reaction is added when your reply is sent; if it says everything, reply with no text.",
        JsonDocument.Parse("""
            {"type":"object","properties":{
              "message_id":{"type":"integer","description":"The id shown as [msg <id>] in front of the customer's message."},
              "emoji":{"type":"string","description":"One emoji, e.g. 👍 ❤️ 🙏 😊 🎉 👌"}},
             "required":["message_id","emoji"]}
            """).RootElement.Clone());

    internal static readonly LlmTool QuoteDef = new(QuoteTool,
        "Quote the customer's message that your reply answers (its first message carries the quote) — use it when "
        + "they sent several messages since your last reply, or your answer refers to an older message.",
        JsonDocument.Parse("""
            {"type":"object","properties":{
              "message_id":{"type":"integer","description":"The id shown as [msg <id>] in front of the customer's message."}},
             "required":["message_id"]}
            """).RootElement.Clone());
}

/// <summary>
/// The touches one generation may use and what the model decided — built per
/// reply (a chat reply, or a playground turn whose ids are transcript positions),
/// read back into <see cref="AiReplyResult"/>.
/// </summary>
public sealed class HumanTurn
{
    public HumanTurn(string channel, bool reactions, bool quotes)
    {
        Channel = channel;
        ReactionsOn = reactions && MessageReactions.Supports(channel);
        QuotesOn = quotes && MessageReactions.Supports(channel);
    }

    public string Channel { get; }
    public bool ReactionsOn { get; }
    public bool QuotesOn { get; }
    /// <summary>The customer's messages of THIS turn (since our last reply): what may get a reaction.</summary>
    public HashSet<int> Current { get; } = new();
    /// <summary>Every customer message the model saw, id → text: what may be quoted.</summary>
    public Dictionary<int, string> Known { get; } = new();

    public AiReaction? Reaction { get; private set; }
    public int? ReplyTo { get; private set; }

    /// <summary>Show the model the ids at all? (Only when a tool can use them.)</summary>
    public bool TagsMessages => (ReactionsOn && Current.Count > 0) || (QuotesOn && Known.Count > 0);

    public IEnumerable<LlmTool> Tools()
    {
        if (ReactionsOn && Current.Count > 0) yield return HumanTouch.ReactDef;
        if (QuotesOn && Known.Count > 0) yield return HumanTouch.QuoteDef;
    }

    public bool Handles(string tool) =>
        (tool == HumanTouch.ReactTool && ReactionsOn) || (tool == HumanTouch.QuoteTool && QuotesOn);

    /// <summary>The prompt lines that explain the ids and the two tools ("" when neither is offered).</summary>
    public string Guidance()
    {
        if (!TagsMessages) return "";
        var lines = new List<string>
        {
            "MESSAGE IDS: each customer message below starts with its id, like [msg 123] — the id is not part of "
            + "what they wrote. Never write ids in your reply"
            + (QuotesOn && Current.Count > 1 ? ", except to start a part as described under ANSWERING IN PARTS." : "."),
        };
        if (ReactionsOn && Current.Count > 0)
            lines.Add($"REACTIONS: you may react to the customer's latest message(s) with one emoji by calling "
                      + $"{HumanTouch.ReactTool}, like a person would — 👍 on \"ok\" or a confirmation, 🙏 or ❤️ on "
                      + "\"thanks\", ❤️ on a photo. At most one reaction per reply, never on a complaint or bad news. "
                      + "When a reaction is all their message needs (\"ok thanks!\"), react and reply with no text at all.");
        if (QuotesOn && Known.Count > 0)
            lines.Add($"QUOTING: when the customer sent several messages since your last reply, or your answer refers "
                      + $"to an older message of theirs, call {HumanTouch.QuoteTool} with the id of the message your "
                      + "reply answers. Don't quote in a simple one-message exchange.");
        if (QuotesOn && Current.Count > 1)
        {
            // name the messages: a small model skipped the rule (and a question) when it only read "since your last reply"
            var waiting = string.Join(", ", Current.Select(id => $"[msg {id}]"));
            lines.Add($"ANSWERING IN PARTS: the customer sent {Current.Count} messages since your last reply ({waiting}). "
                      + "Unless they are all about the same thing, you MUST answer in parts, in order: start each part "
                      + "on a new line with the [msg <id>] of the message it answers, e.g.\n"
                      + "[msg 101] Noted, I'll leave the invoice aside.\n[msg 102] Sure — 3:45 PM is free, shall I move it?\n"
                      + $"Each part is sent as its own message quoting that message (at most {AiReplyPart.Max}). Every "
                      + "question gets an answer; messages about the same thing share ONE part (use the id of the one "
                      + "it answers best). Only when everything is one topic, reply without [msg].");
        }
        return string.Join("\n", lines);
    }

    // a part starts at the beginning of a line with the id the model was shown
    private static readonly Regex PartStart = new(@"^[ \t]*\[msg\s*#?(\d+)\][ \t]*:?[ \t]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// The model's reply cut into parts at the <c>[msg id]</c> that starts a line, each
    /// quoting that customer message (an id it was never shown quotes nothing; a part
    /// with the same id as the one before joins it; more than <see cref="AiReplyPart.Max"/>
    /// fold into the last). Null when quotes are off or there are fewer than two parts —
    /// one marked part then just quotes, like <c>quote_message</c>. The texts are RAW:
    /// the caller renders and formats each.
    /// </summary>
    public List<AiReplyPart>? SplitParts(string raw)
    {
        if (!QuotesOn || string.IsNullOrWhiteSpace(raw)) return null;
        var marks = PartStart.Matches(raw);
        if (marks.Count == 0) return null;
        var parts = new List<AiReplyPart>();
        void Add(string text, int? id)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            if (parts.Count > 0 && parts[^1].ReplyTo == id && id != null)
                parts[^1] = parts[^1] with { Text = parts[^1].Text + "\n\n" + text };
            else parts.Add(new AiReplyPart(text, id));
        }
        Add(raw[..marks[0].Index], null);
        for (var i = 0; i < marks.Count; i++)
        {
            var from = marks[i].Index + marks[i].Length;
            var to = i + 1 < marks.Count ? marks[i + 1].Index : raw.Length;
            var id = int.TryParse(marks[i].Groups[1].Value, out var n) && Known.ContainsKey(n) ? n : (int?)null;
            Add(raw[from..to], id);
        }
        if (parts.Count > AiReplyPart.Max)
            parts = parts.Take(AiReplyPart.Max - 1)
                .Append(parts[AiReplyPart.Max - 1] with
                {
                    Text = string.Join("\n\n", parts.Skip(AiReplyPart.Max - 1).Select(p => p.Text)),
                }).ToList();
        if (parts.Count(p => p.ReplyTo != null) == 0) return null;
        if (parts.Count < 2)
        {
            ReplyTo ??= parts[0].ReplyTo;
            return null;
        }
        return parts;
    }

    public (string Result, bool Ok) Execute(LlmToolCall call)
    {
        JsonElement args;
        try
        {
            args = string.IsNullOrWhiteSpace(call.ArgumentsJson)
                ? default : JsonDocument.Parse(call.ArgumentsJson).RootElement.Clone();
        }
        catch { return Refuse("The arguments were not valid JSON."); }
        var id = Id(args);
        if (call.Name == HumanTouch.ReactTool)
        {
            if (!ReactionsOn) return Refuse("Reactions are off for this conversation.");
            if (id is not int mid || !Current.Contains(mid))
                return Refuse("You can only react to the customer's messages since your last reply: "
                              + string.Join(", ", Current.OrderBy(x => x).Select(x => $"[msg {x}]")) + ".");
            var raw = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("emoji", out var e)
                      && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var emoji = MessageReactions.ForAi(Channel, raw);
            if (emoji == null)
                return Refuse($"\"{raw}\" can't be used as a reaction here — use ONE common emoji such as 👍 ❤️ 🙏 😊 🎉 👌.");
            var replaced = Reaction != null;
            Reaction = new AiReaction(mid, emoji);
            return (JsonSerializer.Serialize(new
            {
                ok = true, message_id = mid, emoji,
                note = (replaced ? "This replaces your earlier reaction (one per reply). " : "")
                       + "The reaction is added when your reply is sent. If it says everything, reply with no text.",
            }), true);
        }
        if (call.Name == HumanTouch.QuoteTool)
        {
            if (!QuotesOn) return Refuse("Quoted replies are off for this conversation.");
            if (id is not int qid || !Known.ContainsKey(qid))
                return Refuse("Quote one of the customer's messages shown with a [msg <id>].");
            ReplyTo = qid;
            return (JsonSerializer.Serialize(new { ok = true, message_id = qid, note = "Your reply will quote that message." }), true);
        }
        return Refuse("Unknown tool.");
    }

    private static int? Id(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("message_id", out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = (v.GetString() ?? "").Trim().TrimStart('#');
            if (s.StartsWith("msg", StringComparison.OrdinalIgnoreCase)) s = s[3..].Trim();
            if (int.TryParse(s, out var p)) return p;
        }
        return null;
    }

    private static (string, bool) Refuse(string why) =>
        (JsonSerializer.Serialize(new { ok = false, error = why }), false);
}
