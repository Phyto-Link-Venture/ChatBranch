namespace ChatBranch.Data.Ai;

/// <summary>
/// One piece of what a reply was based on: a knowledge item (or one chunk of a
/// long one) or a learned lesson. Carried on <see cref="AiReplyResult.Grounding"/>,
/// copied into <c>AiDraft.Context["grounding"]</c> and shown in the AI monitor.
/// </summary>
/// <param name="Type">knowledge | lesson.</param>
/// <param name="Score">Relevance 0–1 (1 for a standing rule).</param>
public sealed record AiGrounding(string Type, int Id, string Title, string Chunk, double Score)
{
    public Dictionary<string, object?> ToMeta() => new()
    {
        ["type"] = Type,
        ["id"] = Id,
        ["title"] = Title,
        ["chunk"] = Chunk.Length <= 300 ? Chunk : Chunk[..300] + "…",
        ["score"] = Math.Round(Score, 3),
    };
}
