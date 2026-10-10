namespace ChatBranch.Domain;

/// <summary>
/// A library row that may be made available to SEVERAL entities (quick replies,
/// call scripts, knowledge, AI skills, automation rules). Its own
/// <see cref="IEntityScoped.EntityId"/> stays the PRIMARY entity (back-compat:
/// every reader that only knows one entity keeps working); every further
/// entity it was ticked for is a <see cref="LibraryEntity"/> row.
///
/// The rule (ScopingService.LibraryVisible): no entity at all = GLOBAL
/// (everyone); otherwise the item is available to each ticked entity AND its
/// branches — never to a sibling outside the ticks. Invariant kept by
/// <c>LibraryEntities.Set</c>: a global row (EntityId null) has no extra ticks.
/// </summary>
public interface IMultiEntityItem : IEntityScoped
{
    int Id { get; }
}

/// <summary>One extra "available to" tick: (kind, item id) → entity. Table
/// <c>crm_libraryentity</c>, one table for every library kind (like
/// <c>crm_tagassignment</c>'s target_type), so the visibility rule is ONE query.</summary>
public class LibraryEntity
{
    public int Id { get; set; }
    /// <summary>See <see cref="LibraryKinds"/>.</summary>
    public string Kind { get; set; } = "";
    public int ItemId { get; set; }
    public int EntityId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Wire/storage values of <see cref="LibraryEntity.Kind"/>.</summary>
public static class LibraryKinds
{
    public const string Canned = "canned";
    public const string Script = "script";
    public const string Knowledge = "knowledge";
    public const string Skill = "skill";
    public const string Automation = "automation";
    public const string AiTask = "ai_task";

    /// <summary>The kind for a library CLR type, or null when the type is not multi-entity.</summary>
    public static string? For(Type t) =>
        t == typeof(CannedResponse) ? Canned
        : t == typeof(ScriptTemplate) ? Script
        : t == typeof(KnowledgeItem) ? Knowledge
        : t == typeof(AgentSkill) ? Skill
        : t == typeof(AutomationRule) ? Automation
        : t == typeof(ChatBranch.Domain.AiTask) ? AiTask
        : null;
}
