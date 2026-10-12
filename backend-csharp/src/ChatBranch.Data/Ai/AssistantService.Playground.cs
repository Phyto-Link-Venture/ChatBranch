using ChatBranch.Data.Ai.Learning;
using ChatBranch.Data.Bookings;
using ChatBranch.Domain;

namespace ChatBranch.Data.Ai;

/// <summary>One turn of a playground transcript: <c>customer</c> (the operator typing as the customer) or <c>ai</c>.</summary>
public sealed record PlaygroundTurn(string Role, string Text)
{
    public const string Customer = "customer";
    public const string Ai = "ai";
}

/// <summary>A made-up customer for the playground — never stored.</summary>
public sealed record PlaygroundProfile(string Name, string Language, IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// What a playground reply may change for this one call: an unsaved persona that
/// replaces the stored one (the hard rules always stay) and parts of the prompt
/// switched off. Defaults = exactly what a real reply gets.
/// </summary>
/// <param name="EmojiStyle">the emoji style this run uses (off | light | expressive; "" = no style line).</param>
/// <param name="Reactions">offer <c>react_to_message</c> (where the channel has reactions).</param>
/// <param name="Quotes">offer <c>quote_message</c> (where the channel quotes).</param>
public sealed record PlaygroundOptions(string? Persona = null, bool Knowledge = true, bool Lessons = true,
    bool Skills = true, bool Memory = true, string EmojiStyle = "", bool Reactions = false, bool Quotes = false)
{
    public static readonly PlaygroundOptions Defaults = new();
}

/// <summary>A playground request, already validated and scoped by the caller.</summary>
public sealed record PlaygroundInput(int? EntityId, string Channel, int? ContactId, PlaygroundProfile? Profile,
    IReadOnlyList<PlaygroundTurn> Turns, PlaygroundOptions Options, string Actor = "", string Assignee = "");

/// <summary>Everything the playground's inspector shows about one generated reply.</summary>
public sealed record PlaygroundResult(
    AiReplyResult Reply,
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<RetrievalHit> Hits,
    IReadOnlyList<BookingService.BookingPreview> Bookings,
    IReadOnlyList<string> ToolsOffered,
    IReadOnlyList<string> ChecksConfigured,
    Contact? Contact,
    IReadOnlyList<Documents.DocumentService.DocumentPreview>? Documents = null);

/// <summary>
/// The AI playground's seam into the generator: the SAME prompt assembly and
/// generate → check → retry engine as a real reply (<see cref="RunAsync"/>), fed
/// from a transcript the browser sends instead of a stored conversation. It has
/// no outcome side effects — no thread, message, draft, note, notification,
/// handover or status — calendar tools preview instead of reserving, lessons are
/// not counted as used, and usage is metered as feature <c>playground</c>.
/// </summary>
public partial class AssistantService
{
    public const string PlaygroundFeature = "playground";

    public async Task<PlaygroundResult> GeneratePlaygroundAsync(PlaygroundInput input, ILlmClient? llmOverride = null,
        CancellationToken ct = default)
    {
        var contact = input.ContactId is int cid ? await ContactAsync(cid, ct) : null;
        if (contact == null && input.Profile != null) contact = MadeUp(input.Profile);

        var turns = input.Turns.Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();
        // transient rows (never attached) so retrieval reads the transcript exactly as it reads a thread
        var history = turns.Select(t => new Message
        {
            Direction = t.Role == PlaygroundTurn.Customer ? MessageDirection.In : MessageDirection.Out, Text = t.Text,
        }).ToList();
        var lastUser = turns.LastOrDefault(t => t.Role == PlaygroundTurn.Customer)?.Text ?? "";

        var o = input.Options;
        // the reaction / quote tools see transcript POSITIONS as message ids (1 = the first turn sent):
        // the playground is stateless, so the browser maps them back to its own bubbles
        var human = new HumanTurn(input.Channel, o.Reactions, o.Quotes);
        var lastAi = turns.FindLastIndex(t => t.Role != PlaygroundTurn.Customer);
        for (var i = 0; i < turns.Count; i++)
        {
            if (turns[i].Role != PlaygroundTurn.Customer) continue;
            human.Known[i + 1] = turns[i].Text;
            if (i > lastAi) human.Current.Add(i + 1);
        }
        var parts = new PromptParts(string.IsNullOrWhiteSpace(o.Persona) ? null : o.Persona, o.Skills, o.Knowledge,
            o.Lessons, o.Memory, o.EmojiStyle ?? "", human.Guidance(), Reply: true);
        var system = BuildSystemPrompt(input.EntityId, input.Channel, contact, RetrievalQuery.FromHistory(history), parts);
        var hits = _lastComposition.Hits.ToList();
        var messages = turns.Select((t, i) => new LlmMessage(
            t.Role == PlaygroundTurn.Customer ? "user" : "assistant",
            human.TagsMessages && t.Role == PlaygroundTurn.Customer ? HumanTouch.Tag(i + 1) + t.Text : t.Text)).ToList();

        var spec = new Spec(system, messages.ToList(), input.EntityId, input.Channel, contact, lastUser,
            PlaygroundFeature, Trim($"playground:{input.Actor}", 120))
        {
            Assignee = input.Assignee, SkillsOff = !o.Skills, Playground = true, Human = human,
        };
        var (result, ctx, offered, checks) = await RunWithContextAsync(spec, llmOverride ?? _llm, ct);
        // belt and braces: a preview reserves nothing, but nothing may ever stay held
        if (ctx.Apps.Actions.Count > 0) _skills.Bookings.Release(ctx.Apps.Actions, "AI playground — nothing is booked.");
        if (ctx.Docs.DocumentIds.Count > 0) _skills.Documents.Release(ctx.Docs.DocumentIds, "AI playground — nothing is sent.");
        return new PlaygroundResult(result, system, messages, hits, ctx.Apps.Previews.ToList(), offered, checks, contact,
            ctx.Docs.Previews.ToList());
    }

    /// <summary>The made-up customer as a contact the prompt and the skills can read — never attached, never saved.</summary>
    private static Contact MadeUp(PlaygroundProfile p) => new()
    {
        Id = 0,
        Name = string.IsNullOrWhiteSpace(p.Name) ? "Test customer" : p.Name.Trim(),
        Language = string.IsNullOrWhiteSpace(p.Language) ? "en" : p.Language.Trim(),
        Tags = p.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList(),
        Attributes = p.Fields.ToDictionary(kv => kv.Key, kv => kv.Value),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
