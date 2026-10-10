namespace ChatBranch.Data;

/// <summary>
/// The entities a request's <c>?entity=a,b</c> ticks when it names SEVERAL — the
/// dashboard's multi-select entity filter. Set per request (an async-flow value, like
/// the API key scopes) by the API's <c>UseEntitySelection</c> middleware, already cut
/// down to the entities the caller may see; null for a single entity or "All".
///
/// Endpoints keep resolving <c>?entity=</c> to ONE id (the first pick); the scoping
/// helpers widen that id to the whole selection: <see cref="ScopingService.ScopeOwned{T}"/>
/// (the union of every pick's subtree), <see cref="ScopingService.ScopeInherited{T}"/>
/// (the union of every pick's chain) and <see cref="ScopingService.PickSubtree"/>.
/// Settings stay single-level: <c>SettingValue</c> reads the first pick only.
/// </summary>
public static class EntitySelection
{
    private static readonly AsyncLocal<IReadOnlyList<int>?> Picks = new();

    /// <summary>The ticked entity ids (≥2), in the order asked; null = no multi-selection.</summary>
    public static IReadOnlyList<int>? Current => Picks.Value;

    public static void Set(IReadOnlyList<int>? ids) => Picks.Value = ids is { Count: > 1 } ? ids : null;

    /// <summary>True when <paramref name="entityId"/> is one of this request's ticked entities.</summary>
    public static bool Covers(int entityId) => Picks.Value is { } p && p.Contains(entityId);
}
