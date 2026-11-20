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
    // Django's auth_user name + email columns. Edited by the person themselves
    // (Settings → My account, /api/auth/me/account); "" = not set, and the
    // display name then falls back to the username.
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";

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

    // Customer login: when set, this user IS that Contact — a registered
    // customer who signs in to use the integrations bound to their own record
    // (their calendar, their documents), rather than an operator seat.
    // Null = not a customer login. Nullable FK to crm.Contact.
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }

    // Agent entity allow-list (empty for superadmin/admin, whose access is derived).
    public ICollection<Entity> Entities { get; set; } = new List<Entity>();
    public ICollection<AuthToken> AuthTokens { get; set; } = new List<AuthToken>();

    // Channel allow-list (crm_profile.channel_account_ids, JSON ids of ChannelAccount).
    // Empty = every channel inside the person's entity scope (the default). Set =
    // they see only conversations on those numbers / mailboxes / pages (plus the
    // unattributed ones everybody sees). Never applies to a superadmin.
    public List<int> ChannelAccountIds { get; set; } = new();

    // Per-user preferences as one JSON document ({appearance, notifications}).
    // Null = never chose anything: the organization defaults apply.
    public string? Preferences { get; set; }
}

public class AuthToken
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>
    /// "" = a normal session. <see cref="SessionRestrictions.ChangePassword"/> = signed in
    /// with a seeded default password on a real install: the API answers nothing but the
    /// account-setup routes until the password is changed (Api/Security/SessionGate).
    /// </summary>
    public string Restriction { get; set; } = "";
}

/// <summary>What a restricted session must do before it may use the product.</summary>
public static class SessionRestrictions
{
    /// <summary>Change the seeded default password first.</summary>
    public const string ChangePassword = "change_password";
    /// <summary>The organization requires two-step sign-in and this person has not set it up.</summary>
    public const string EnrollTwoFactor = "enroll_2fa";
}

/// <summary>
/// One person's two-step sign-in (TOTP, RFC 6238 — any authenticator app). The
/// secrets are encrypted at rest with ASP.NET Core Data Protection (keys in the data
/// dir) and never leave the server after enrollment; recovery codes are stored only
/// as salted SHA-256 hashes, each usable once.
/// </summary>
public class UserTwoFactor
{
    public int Id { get; set; }
    public int UserId { get; set; }
    /// <summary>The confirmed secret, protected ("" = not enrolled).</summary>
    public string Secret { get; set; } = "";
    /// <summary>A secret being set up (shown once as a QR code), protected; confirmed by a code.</summary>
    public string PendingSecret { get; set; } = "";
    public DateTime? PendingAt { get; set; }
    public bool Enabled { get; set; }
    /// <summary>The last 30-second time step a code was accepted for — a code is never accepted twice.</summary>
    public long LastStep { get; set; }
    public string RecoverySalt { get; set; } = "";
    /// <summary>Hashes of the unused recovery codes.</summary>
    public List<string> RecoveryCodes { get; set; } = new();
    public DateTime? ConfirmedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A one-time link that lets a person choose their own password: the welcome email's
/// "set your password" link (<see cref="PasswordTokenPurposes.Invite"/>) or an admin's
/// "password reset link" (<see cref="PasswordTokenPurposes.Reset"/>). Only the SHA-256 of
/// the 256-bit token is stored; a link works once, for a limited time, and issuing a new
/// one (or deactivating the user, or changing their email) revokes the older ones.
/// </summary>
public class PasswordToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    /// <summary>Lowercase hex SHA-256 of the token in the link (never the token itself).</summary>
    public string TokenHash { get; set; } = "";
    /// <summary><see cref="PasswordTokenPurposes"/>: invite | reset.</summary>
    public string Purpose { get; set; } = PasswordTokenPurposes.Invite;
    /// <summary>Username of the person who sent the link.</summary>
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>When the password was chosen with it (null = not used).</summary>
    public DateTime? UsedAt { get; set; }
    /// <summary>When a newer link, a deactivation or an email change revoked it (null = not revoked).</summary>
    public DateTime? RevokedAt { get; set; }
}

public static class PasswordTokenPurposes
{
    /// <summary>The welcome email of a new login (no password yet).</summary>
    public const string Invite = "invite";
    /// <summary>An admin's "send password reset link" for someone who has a password.</summary>
    public const string Reset = "reset";
}

public class Role
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Label { get; set; } = "";
    public bool IsSystem { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>
    /// Which channels — and which connection methods of each — people holding this role
    /// may connect and use (crm_role.channel_access, JSON {channel: [method…]}).
    /// "" = every channel and method (the default). Parsed by
    /// ChatBranch.Data.Channels.RoleChannelPolicy; never applies to a superadmin.
    /// </summary>
    public string ChannelAccess { get; set; } = "";
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
/// A group of agents conversations can be routed to — a "team".
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

/// <summary>
/// An event-driven automation: when something happens to a conversation, check
/// conditions and apply actions — an "automation rule".
///
/// Distinct from the flow builder, which is CONTACT-centric and time-triggered
/// (a reminder sequence over days). A rule is CONVERSATION-centric and fires on
/// an event that just happened — label an angry message, route billing to the
/// billing team, reopen a resolved thread when the customer replies.
/// </summary>
public class AutomationRule : IMultiEntityItem
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public Entity? Entity { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>conversation_created | message_created | status_changed | assigned</summary>
    public string Event { get; set; } = "message_created";
    public bool Active { get; set; } = true;
    /// <summary>Lower runs first; rules are applied in order, all matching ones.</summary>
    public int Position { get; set; }
    /// <summary>[{field, op, value}] — ALL must match (AND).</summary>
    public List<object?> Conditions { get; set; } = new();
    /// <summary>[{type, value}] — applied in order.</summary>
    public List<object?> Actions { get; set; } = new();
    /// <summary>Bookkeeping so an operator can see a rule is actually firing.</summary>
    public int TimesFired { get; set; }
    public DateTime? LastFiredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
