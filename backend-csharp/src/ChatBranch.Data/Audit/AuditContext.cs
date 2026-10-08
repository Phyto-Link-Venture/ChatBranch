using ChatBranch.Domain;

namespace ChatBranch.Data.Audit;

/// <summary>
/// The ambient "who/what/where" of the work currently running — one per HTTP
/// request (set by the request-audit middleware) or per background pass. It
/// flows through async calls via <see cref="AsyncLocal{T}"/>, so every
/// <see cref="AuditLog"/> row saved while it is active is stamped with the same
/// source, user, method, path and request id by <see cref="AuditStampInterceptor"/>
/// — without any of the dozen per-endpoint audit helpers having to know.
///
/// It also counts the DOMAIN rows written during a request: the middleware only
/// writes its generic "request" row when the endpoint wrote nothing itself.
/// </summary>
public sealed class AuditContext
{
    private static readonly AsyncLocal<AuditContext?> CurrentSlot = new();

    /// <summary>The context of the work running on this async flow, if any.</summary>
    public static AuditContext? Current => CurrentSlot.Value;

    /// <summary>
    /// Make <paramref name="ctx"/> current until the returned handle is disposed.
    /// Inside an async method the value also reverts on its own when the method
    /// returns (ExecutionContext semantics); the handle is for synchronous blocks
    /// such as a background worker pass.
    /// </summary>
    public static IDisposable Begin(AuditContext ctx)
    {
        var previous = CurrentSlot.Value;
        CurrentSlot.Value = ctx;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly AuditContext? _previous;
        public Restore(AuditContext? previous) => _previous = previous;
        public void Dispose() => CurrentSlot.Value = _previous;
    }

    public string Source { get; set; } = AuditSources.System;
    public string Actor { get; set; } = "system";
    public int? UserId { get; set; }
    public int? EntityId { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string Ip { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string RequestId { get; set; } = "";

    /// <summary>Set by an endpoint (or the MCP/assistant surfaces) that must not get
    /// the generic request row — e.g. a read that happens to be a POST.</summary>
    public bool SkipRequestRow { get; set; }

    private int _domainRows;
    private readonly List<AuditLog> _written = new();

    /// <summary>Rows attributed to THIS context's source that were saved while it
    /// was current (an automation row written during a user request is the
    /// automation's, not the request's, so it does not count).</summary>
    public int DomainRows => Volatile.Read(ref _domainRows);

    /// <summary>Every row stamped under this context (for the status backfill).</summary>
    public IReadOnlyList<AuditLog> Written
    {
        get { lock (_written) return _written.ToList(); }
    }

    internal void Track(AuditLog row)
    {
        lock (_written) _written.Add(row);
        if (string.Equals(row.Source, Source, StringComparison.Ordinal))
            Interlocked.Increment(ref _domainRows);
    }
}
