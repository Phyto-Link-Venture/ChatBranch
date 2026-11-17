namespace ChatBranch.Domain;

// ---- managed connections through Zernio (ChatBranch.Data/Zernio) --------------
//
// One Zernio API key serves the whole deployment; each ChatBranch entity gets its
// own Zernio PROFILE (externalId "cb-entity-<id>"), the organization level uses
// the key's default profile. A connected messaging account (WhatsApp, Messenger,
// Instagram DM, Telegram, SMS) is mirrored as a ChannelAccount ON ITS NATIVE
// CHANNEL (provider "zernio", ExternalId = Zernio's account id), so the inbox,
// routing, health and scoping work like any inbox. ChatBranch does not carry
// social-media features (posts, public comments, reviews) — out of scope.

/// <summary>
/// Which Zernio profile an entity's managed messaging accounts live in (<c>crm_socialprofile</c>).
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
/// One Facebook Page of a managed Messenger account (<c>crm_socialpage</c>),
/// mirrored from Zernio's <c>GET /v1/accounts/{id}/facebook-page</c>. One Facebook login
/// often manages several Pages; <see cref="IsSelected"/> = Zernio's <c>selectedPageId</c>,
/// the Page whose Messenger conversations reach the inbox.
/// </summary>
public class SocialPage
{
    public int Id { get; set; }
    /// <summary>The Messenger <see cref="ChannelAccount"/> (provider zernio) it belongs to.</summary>
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

/// <summary>
/// The Zernio conversation behind a NATIVE-channel thread carried by a Zernio account
/// (<c>crm_zernioconversation</c>): a WhatsApp / Messenger / Instagram / Telegram / SMS
/// conversation connected "via Zernio" keeps ChatBranch's native thread key (the phone
/// digits, the PSID, the chat id), while a reply addresses Zernio's conversation id.
/// One row per (inbox, thread key), learnt from <c>message.received</c> and from a send
/// that opened the conversation.
/// </summary>
public class ZernioConversation
{
    public int Id { get; set; }
    /// <summary>The native <see cref="ChannelAccount"/> (provider zernio) that carries it.</summary>
    public int AccountId { get; set; }
    public string ZernioAccountId { get; set; } = "";
    public string Channel { get; set; } = "";
    /// <summary>The native thread key (WhatsApp / SMS digits, PSID / IGSID, Telegram chat id).</summary>
    public string Key { get; set; } = "";
    /// <summary>Zernio's conversation id (what POST /v1/inbox/conversations/{id}/messages addresses).</summary>
    public string ConversationId { get; set; } = "";
    /// <summary>The platform's participant id (WhatsApp phone / BSUID, PSID, Telegram user id).</summary>
    public string ParticipantId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
