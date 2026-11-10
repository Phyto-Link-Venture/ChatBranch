namespace ChatBranch.Domain;

// ---- social media through Zernio (ChatBranch.Data/Social) ---------------------
//
// One Zernio API key serves the whole deployment; each ChatBranch entity gets its
// own Zernio PROFILE (externalId "cb-entity-<id>"), the organization level uses
// the key's default profile. A connected social account is mirrored as a
// ChannelAccount (channel "social", provider "zernio", ExternalId = Zernio's
// account id), so the inbox, routing, health and scoping work like any inbox.

/// <summary>
/// Which Zernio profile an entity's social accounts live in (<c>crm_socialprofile</c>).
/// <see cref="EntityId"/> null = the organization level (the key's default profile).
/// </summary>
public class SocialProfile
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    /// <summary>Zernio's profile <c>_id</c>.</summary>
    public string ProfileId { get; set; } = "";
    /// <summary>The <c>externalId</c> we set on it (<c>cb-entity-&lt;id&gt;</c>; "" for the default profile).</summary>
    public string ExternalId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One Facebook Page a <c>facebook</c> account can publish to (<c>crm_socialpage</c>),
/// mirrored from Zernio's <c>GET /v1/accounts/{id}/facebook-page</c>. One Facebook login
/// often manages several Pages: each can be bound to its own outlet
/// (<see cref="EntityId"/>; null = it follows the account's level), and a post targets
/// it as <c>"&lt;accountId&gt;:&lt;pageId&gt;"</c>. <see cref="IsSelected"/> = Zernio's
/// <c>selectedPageId</c>, the Page whose comments and messages reach the inbox.
/// </summary>
public class SocialPage
{
    public int Id { get; set; }
    /// <summary>The <see cref="ChannelAccount"/> (channel social) it belongs to.</summary>
    public int AccountId { get; set; }
    public string ZernioAccountId { get; set; } = "";
    public string PageId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string Category { get; set; } = "";
    public string Avatar { get; set; } = "";
    public int? EntityId { get; set; }
    public bool IsSelected { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// What a social conversation is about, beside its <see cref="Thread"/>
/// (<c>crm_socialthread</c>, one row per thread): the kind (comment / review / dm),
/// the platform, the post a comment sits under, a review's rating, whether the
/// comment is hidden, who wrote it — and the Zernio ids a reply needs (a comment
/// reply needs the POST id, which the thread key does not carry).
/// </summary>
public class SocialThread
{
    public int Id { get; set; }
    public int ThreadId { get; set; }
    /// <summary>comment | review | dm.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Zernio's platform id (instagram, facebook, googlebusiness, …).</summary>
    public string Platform { get; set; } = "";
    /// <summary>The Zernio account id (= <see cref="ChannelAccount.ExternalId"/>).</summary>
    public string ZernioAccountId { get; set; } = "";
    public string AccountUsername { get; set; } = "";

    /// <summary>Comment: the platform's post id (what a reply / hide / private reply addresses).</summary>
    public string PostId { get; set; } = "";
    /// <summary>Comment: Zernio's own post id when the post was published through Zernio.</summary>
    public string ZernioPostId { get; set; } = "";
    public string PostText { get; set; } = "";
    public string PostPermalink { get; set; } = "";
    public string PostMediaUrl { get; set; } = "";
    /// <summary>Comment: the top-level comment the thread is about (replies to it join it).</summary>
    public string RootCommentId { get; set; } = "";
    /// <summary>Review: the platform's FULL review id (Google: accounts/…/locations/…/reviews/…).</summary>
    public string ReviewId { get; set; } = "";
    /// <summary>DM: Zernio's conversation id (what a send addresses).</summary>
    public string ConversationId { get; set; } = "";

    /// <summary>Review: 1..5 (0 = not a review).</summary>
    public int Rating { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Comment: a private reply (DM to the commenter) was sent — platforms allow one.</summary>
    public bool PrivateReplied { get; set; }

    public string AuthorId { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string AuthorHandle { get; set; } = "";
    public string AuthorAvatar { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A social post composed in ChatBranch and published / scheduled through Zernio
/// (<c>crm_socialpost</c>). <see cref="ZernioPostId"/> "" = a local draft that has
/// not reached Zernio yet.
/// </summary>
public class SocialPost
{
    public int Id { get; set; }
    public int? EntityId { get; set; }
    public string ZernioPostId { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>[{url, type}] — type image | video | gif | document.</summary>
    public List<Dictionary<string, object?>> Media { get; set; } = new();
    /// <summary>Zernio account ids the post goes to.</summary>
    public List<string> AccountIds { get; set; } = new();
    /// <summary>The platforms of those accounts (for the list's logos).</summary>
    public List<string> Platforms { get; set; } = new();
    /// <summary>UTC; null = publish now (or a draft with no time yet).</summary>
    public DateTime? ScheduledAt { get; set; }
    public string Timezone { get; set; } = "";
    /// <summary>draft | scheduled | publishing | published | partial | failed | deleted.</summary>
    public string Status { get; set; } = "draft";
    public string Error { get; set; } = "";
    /// <summary>[{account_id, platform, status, url, error}] per target.</summary>
    public List<Dictionary<string, object?>> Results { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A Zernio webhook event already handled (<c>crm_zernioevent</c>) — Zernio delivers
/// at least once, so the event id is claimed in a unique index before processing.
/// </summary>
public class ZernioEvent
{
    public int Id { get; set; }
    public string EventId { get; set; } = "";
    public string Event { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
}
