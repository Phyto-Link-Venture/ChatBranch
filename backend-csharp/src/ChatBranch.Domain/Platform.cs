namespace ChatBranch.Domain;

// Developer platform: API keys (REST + MCP access as a user) and the operator
// assistant's confirm-before-write queue.

/// <summary>
/// A personal API key. The token (<c>cbk_&lt;prefix&gt;_&lt;secret&gt;</c>) is shown
/// ONCE at creation; only the prefix (lookup) and a SHA-256 of the secret are
/// stored. A key acts as its owning user — same role, capabilities and entity
/// scope — optionally narrowed further by <see cref="Scopes"/>.
/// </summary>
public class ApiKey
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    /// <summary>Lowercase hex SHA-256 of the secret part of the token.</summary>
    public string Hash { get; set; } = "";
    /// <summary>Optional capability subset; empty = everything the owner holds.</summary>
    public List<string> Scopes { get; set; } = new();
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A write the operator assistant proposed and is waiting for the signed-in user
/// to confirm. Server-side so the UI can never execute something the model did
/// not propose, and one user can never confirm another's action.
/// </summary>
public class AssistantAction
{
    /// <summary>Opaque public id (act_…), unguessable.</summary>
    public string Id { get; set; } = "";
    public int UserId { get; set; }
    public string Tool { get; set; } = "";
    /// <summary>The arguments as JSON text, exactly as they will be executed.</summary>
    public string Args { get; set; } = "{}";
    public string Summary { get; set; } = "";
    /// <summary>pending | done | failed | cancelled | expired</summary>
    public string Status { get; set; } = "pending";
    public string Result { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}
