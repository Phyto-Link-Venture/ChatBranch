namespace ChatBranch.Domain;

/// <summary>
/// An AI app that has asked to connect to this ChatBranch (Claude, an IDE, anything
/// that speaks MCP). Clients register THEMSELVES — that is what OAuth's dynamic client
/// registration is for, and it is why a person never has to paste a token: the app
/// registers, opens a sign-in page, and gets its own credential.
///
/// A registered client is not trusted with anything on its own. It can only ever ask a
/// person to approve it, and what it gets back acts as THAT person, with their
/// capabilities and their entity scope.
/// </summary>
public class OAuthClient
{
    public int Id { get; set; }

    /// <summary>The public identifier the client sends back (generated here, never chosen).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>What the consent screen calls it — the client's own claim, shown as such.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Where an authorization may be redirected. An exact match is required: this is the
    /// one thing standing between a stolen authorization code and someone else's inbox.
    /// </summary>
    public List<string> RedirectUris { get; set; } = new();

    /// <summary>The client's own page, shown on the consent screen when it gives one.</summary>
    public string Uri { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>Last time this client completed a sign-in, for the "connected apps" list.</summary>
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// One sign-in in progress, and then the authorization code it becomes. Both halves live
/// on one row so there is nothing to reconcile: the app's request is stored when it opens
/// the page, the person's approval fills in who they are and the code's hash, and
/// redeeming the code marks it used.
///
/// A code is single-use, short-lived, bound to the client and to the PKCE challenge the
/// app sent. Those three together are what make an intercepted code worthless.
/// </summary>
public class OAuthAuthorization
{
    public int Id { get; set; }

    /// <summary>The id in the consent page's URL — meaningless on its own.</summary>
    public string RequestId { get; set; } = "";

    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";

    /// <summary>What the app asked for, verbatim, so the consent screen can say so.</summary>
    public string Scope { get; set; } = "";

    /// <summary>The app's CSRF value, handed back untouched.</summary>
    public string State { get; set; } = "";

    /// <summary>PKCE (RFC 7636): the S256 challenge the token request must prove.</summary>
    public string CodeChallenge { get; set; } = "";
    public string CodeChallengeMethod { get; set; } = "S256";

    /// <summary>The MCP endpoint the token is for (RFC 8707), when the app named one.</summary>
    public string Resource { get; set; } = "";

    /// <summary>Who approved it. Null = still waiting for a person.</summary>
    public int? UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>What the person agreed to: capability ids, or empty for "everything I can do".</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>SHA-256 of the authorization code. Null until someone approves.</summary>
    public string CodeHash { get; set; } = "";

    /// <summary>Redeemed at — a second attempt with the same code is refused.</summary>
    public DateTime? UsedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A token a connected app holds. It acts as its owner, exactly as a personal API key
/// does (same role, same capabilities, same entity scope, and the same narrowing when
/// the person chose read-only) — the difference is that nobody had to copy a secret out
/// of a settings page, and that revoking it is one click in "Connected apps".
///
/// Only hashes are stored. The access token expires in hours; the refresh token is how
/// the app keeps working without asking the person again, and revoking the row ends both.
/// </summary>
public class OAuthToken
{
    public int Id { get; set; }

    public string ClientId { get; set; } = "";

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Capability ids the app may use; empty = everything its owner can do.</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>What the app asked for, kept for the "connected apps" list.</summary>
    public string Requested { get; set; } = "";

    /// <summary>The lookup half of the access token, and a SHA-256 of its secret.</summary>
    public string AccessPrefix { get; set; } = "";
    public string AccessHash { get; set; } = "";

    public string RefreshPrefix { get; set; } = "";
    public string RefreshHash { get; set; } = "";

    public DateTime ExpiresAt { get; set; }
    public DateTime RefreshExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
