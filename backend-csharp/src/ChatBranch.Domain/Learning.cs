namespace ChatBranch.Domain;

// AI learning: what people think of the AI's replies (AiFeedback), what the AI
// learns from that (AiLesson), and the vectors semantic retrieval searches
// (AiEmbedding). Logic lives in ChatBranch.Data/Ai/Learning/.

public static class AiFeedbackKind
{
    /// <summary>A person pressed thumbs up / down (one per user per message or draft).</summary>
    public const string Explicit = "explicit";
    /// <summary>A reviewer approved a held draft as written (+1, weight 0.5).</summary>
    public const string Approved = "approved";
    /// <summary>A reviewer edited a held draft, then sent it (correction = what was sent).</summary>
    public const string Edited = "edited";
    /// <summary>A reviewer rejected a held draft (−1, the reject reason as the note).</summary>
    public const string Rejected = "rejected";

    public static readonly string[] All = { Explicit, Approved, Edited, Rejected };
}

public static class AiFeedbackReasons
{
    public const string WrongInfo = "wrong_info";
    public const string DidntAnswer = "didnt_answer";
    public const string Tone = "tone";
    public const string TooLong = "too_long";
    public const string ShouldHandover = "should_handover";
    public const string Other = "other";

    public static readonly string[] All = { WrongInfo, DidntAnswer, Tone, TooLong, ShouldHandover, Other };
}

/// <summary>
/// One judgement of one AI-written reply. Snapshots the customer's question and
/// the AI's answer so the lessons built from it survive message edits/deletes.
/// </summary>
public class AiFeedback : IEntityScoped
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public int? ThreadId { get; set; }
    public int? MessageId { get; set; }
    public int? DraftId { get; set; }
    public int? ContactId { get; set; }
    public string Channel { get; set; } = "";
    /// <summary>+1 or −1.</summary>
    public int Rating { get; set; }
    /// <summary>How much this judgement counts (explicit 1.0, approved 0.5…).</summary>
    public double Weight { get; set; } = 1;
    public string Kind { get; set; } = AiFeedbackKind.Explicit;
    public List<string> Reasons { get; set; } = new();
    public string Note { get; set; } = "";
    /// <summary>The customer message(s) the reply answered (≤ 1000 chars).</summary>
    public string Question { get; set; } = "";
    /// <summary>What the AI wrote.</summary>
    public string Answer { get; set; } = "";
    /// <summary>What it should have said ("" = none given).</summary>
    public string Correction { get; set; } = "";
    /// <summary>null = the system (never for explicit ratings).</summary>
    public int? UserId { get; set; }
    public int? LessonId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class AiLessonKind
{
    /// <summary>A question and a good answer (from 👍 / an approval).</summary>
    public const string Example = "example";
    /// <summary>A question, the answer to avoid and what to say instead (👎 + correction / an edit).</summary>
    public const string Correction = "correction";
    /// <summary>A general instruction a manager wrote ("never promise same-day delivery").</summary>
    public const string Rule = "rule";

    public static readonly string[] All = { Example, Correction, Rule };
}

public static class AiLessonStatus
{
    public const string Suggested = "suggested";
    public const string Active = "active";
    public const string Archived = "archived";
    public const string Dismissed = "dismissed";

    public static readonly string[] All = { Suggested, Active, Archived, Dismissed };
}

/// <summary>
/// What the AI learned. Entity-scoped like knowledge (an outlet uses its own
/// plus the global ones); only <see cref="AiLessonStatus.Active"/> lessons reach
/// the prompt, as data labelled "learned from past conversations".
/// </summary>
public class AiLesson : IEntityScoped
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public string Kind { get; set; } = AiLessonKind.Example;
    public string Status { get; set; } = AiLessonStatus.Suggested;
    public string Question { get; set; } = "";
    /// <summary>The good answer — or, for a rule, the rule text.</summary>
    public string Answer { get; set; } = "";
    /// <summary>The answer to avoid (corrections only).</summary>
    public string Avoid { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>Channels it applies to; empty = every channel.</summary>
    public List<string> Channels { get; set; } = new();
    /// <summary>How many feedback rows were merged into it.</summary>
    public int SourceCount { get; set; } = 1;
    public int Uses { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class EmbeddingOwner
{
    public const string Knowledge = "knowledge";
    public const string Lesson = "lesson";
}

/// <summary>
/// One embedded chunk (crm_embedding). <see cref="Vector"/> is float32
/// little-endian, L2-normalised (cosine = dot product). A row is stale when its
/// <see cref="ContentHash"/> or <see cref="Model"/> no longer matches the chunk
/// the retrieval layer derives from its owner — the embedding worker re-embeds it.
/// </summary>
public class AiEmbedding
{
    public int Id { get; set; }
    public string OwnerType { get; set; } = EmbeddingOwner.Knowledge;
    public int OwnerId { get; set; }
    public int ChunkIndex { get; set; }
    public string Text { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dims { get; set; }
    public byte[] Vector { get; set; } = Array.Empty<byte>();
    public DateTime UpdatedAt { get; set; }
}
