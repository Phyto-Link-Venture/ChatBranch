namespace ChatBranch.Domain;

// RBAC + auth (Django app: crm). AppUser stands in for Django's auth_user;
// Profile attaches a role + tenant + entity allow-list to it. AuthToken is one
// row per logged-in session (multiple concurrent tokens per profile).

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    // Django-format password hash: pbkdf2_sha256$<iterations>$<salt>$<b64hash>.
    // See DjangoPasswordHasher for the interop verifier.
    public string Password { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool IsSuperuser { get; set; }
    public DateTime? LastLogin { get; set; }

    public Profile? Profile { get; set; }
}

public class Profile
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public string Role { get; set; } = "admin";
    public string Token { get; set; } = "";
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    // Provider login: when set, this user IS that Service Provider (teacher /
    // supplier) — every data query is scoped to the provider's own world (the
    // customers bound to them via ResourceLink, their conversations, their
    // bookings). Null = a normal operator login. Nullable FK to crm.Resource.
    public int? ResourceId { get; set; }
    public Resource? Resource { get; set; }

    // Customer login: when set, this user IS that Contact — a registered
    // customer who signs in to use the integrations bound to their own record
    // (their calendar, their documents), rather than an operator seat.
    // Null = not a customer login. Nullable FK to crm.Contact.
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }

    // Agent entity allow-list (empty for superadmin/admin, whose access is derived).
    public ICollection<Entity> Entities { get; set; } = new List<Entity>();
    public ICollection<AuthToken> AuthTokens { get; set; } = new List<AuthToken>();
}

public class AuthToken
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class Role
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsSystem { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RolePermission
{
    public int Id { get; set; }
    public string Role { get; set; } = "";
    public string Capability { get; set; } = "";
    public bool Allowed { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A group of agents conversations can be routed to — Chatwoot's "team".
/// Assignment is two-level: a thread belongs to a TEAM (who owns it) and
/// optionally to one agent within it (who is working it right now).
/// </summary>
public class Team : IEntityScoped
{
    public int Id { get; set; }
    /// <summary>Which outlet owns this team. null = organization-wide.</summary>
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>
    /// Round-robin new conversations across this team's members. Off = the team
    /// owns the thread but a human picks up.
    /// </summary>
    public bool AutoAssign { get; set; } = true;
    /// <summary>
    /// The member who received the LAST auto-assignment. Round-robin resumes
    /// after them, so restarts do not always hand work to the same person.
    /// </summary>
    public string LastAssignee { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<TeamMember> Members { get; set; } = new List<TeamMember>();
}

/// <summary>One agent's membership of a team.</summary>
public class TeamMember
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public Team? Team { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public DateTime CreatedAt { get; set; }
}
