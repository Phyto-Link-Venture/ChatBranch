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
