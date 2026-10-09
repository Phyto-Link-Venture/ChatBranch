using ChatBranch.Data.Ai.Learning;

namespace ChatBranch.Data.Ai;

/// <summary>
/// The retrieval seam of the assistant (kept in its own file so the prompt
/// assembly in AssistantService.cs only gained one call): every prompt the
/// assistant builds gets the retrieved COMPANY KNOWLEDGE plus the LEARNED FROM
/// PAST CONVERSATIONS block, and what was used is handed to the generation that
/// follows as <see cref="AiReplyResult.Grounding"/>.
/// </summary>
public partial class AssistantService
{
    // The grounding of the prompt built last, consumed by the RunAsync that uses
    // it. An AssistantService lives in one DI scope and generates one reply at a
    // time, and every entry point builds its prompt immediately before running it.
    private KnowledgeComposition _lastComposition = KnowledgeComposition.Empty;

    /// <summary>The knowledge block followed by the learned-lessons block ("" when neither applies).</summary>
    private string KnowledgeAndLessons(RetrievalQuery? query, int? entityId, string? channel)
    {
        var c = _knowledge.Compose(query, entityId, channel, withLessons: true);
        _lastComposition = c;
        if (c.Knowledge.Length == 0) return c.Learned;
        return c.Learned.Length == 0 ? c.Knowledge : c.Knowledge + "\n\n" + c.Learned;
    }

    /// <summary>What the last-built prompt was based on — once. Counts the
    /// lessons it carried as used.</summary>
    private IReadOnlyList<AiGrounding> TakeGrounding()
    {
        var c = _lastComposition;
        _lastComposition = KnowledgeComposition.Empty;
        _knowledge.MarkLessonsUsed(c.LessonIds);
        return c.Grounding;
    }
}
