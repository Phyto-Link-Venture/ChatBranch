namespace ChatBranch.Domain;

/// <summary>
/// Marks a row that carries a nullable Entity binding (null = global). The
/// entity-scoping helpers (ScopingService) filter generically on this so the
/// "entity override wins, global is fallback" rule lives in exactly one place.
/// </summary>
public interface IEntityScoped
{
    int? EntityId { get; }
}
