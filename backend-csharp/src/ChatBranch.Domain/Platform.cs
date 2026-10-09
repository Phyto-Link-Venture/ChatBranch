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

/// <summary>
/// One "Ask ChatBranch" chat (crm_assistantconversation), owned by exactly one
/// login. The history is per user: every route reads the CALLER's rows only —
/// another login's id is a 404, superadmin included. At most
/// <c>AssistantHistory.MaxConversationsPerUser</c> are kept; the least recently
/// used ones are pruned when a new one starts.
/// </summary>
public class AssistantConversation
{
    public int Id { get; set; }
    /// <summary>auth_user id of the owner (resolved server-side, never posted).</summary>
    public int UserId { get; set; }
    /// <summary>The first question, shortened; the owner may rename it.</summary>
    public string Title { get; set; } = "";
    /// <summary>The outlet in view when the chat started (null = all entities).</summary>
    public int? EntityId { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Bumped on every turn and every confirm/cancel — the history list's order.</summary>
    public DateTime UpdatedAt { get; set; }
    public List<AssistantMessage> Messages { get; set; } = new();
}

/// <summary>
/// One turn of an assistant chat (crm_assistantmessage). The model's context is
/// rebuilt from these rows — never from history a client sends.
/// </summary>
public class AssistantMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public AssistantConversation? Conversation { get; set; }
    /// <summary>user | assistant</summary>
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    /// <summary>
    /// JSON text the dock re-renders the turn from: for an assistant turn
    /// <c>{steps, pending_actions:[{id, …, state, message}], navigation, mode}</c>
    /// (an action card's state follows confirm/cancel); for a user turn the page
    /// context it was asked from.
    /// </summary>
    public string Payload { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
