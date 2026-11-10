namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    /// <summary>
    /// Social media through Zernio ("managed connections" to users): connected accounts, posts, the comment
    /// actions, and the connect flows of native inboxes (WhatsApp / Telegram / SMS).
    /// Replying to a comment, a review or a social DM is send_message on the thread
    /// (channel "social") — those tools are the inbox's own.
    /// </summary>
    private static IEnumerable<IChatBranchTool> Social()
    {
        const string g = "social";
        var view = C("social.view");
        var publish = C("social.publish");
        var manage = C("social.manage");
        var postId = A.Id("post");

        yield return T(g, "social_list_accounts", "List social accounts",
            "The social media accounts connected as managed connections (Instagram, Facebook, TikTok, YouTube, LinkedIn, X, " +
            "Threads, Google Business…) for an entity: id (the connection's account id), platform, username, the outlet it is " +
            "bound to, whether it is active, what it can do (publish, comments, DMs, reviews), and for Facebook the " +
            "Pages it can post to (each with its outlet and post target).", "GET", "/api/social/accounts",
            C("social.view", "channels.view"),
            new[] { A.Entity(), A.Bool("refresh", "Reconcile with the connection service first.") },
            link: (_, _) => Page("Open channels", "/channels"));

        yield return T(g, "social_connect_url", "Start connecting an account",
            "Start a managed connection for one account of a platform, for an entity (or the organization). " +
            "purpose \"inbox\" makes WhatsApp, Messenger (facebook), Instagram, Telegram or SMS an inbox of that channel; " +
            "\"social\" connects a social account for comments and posts. The answer says what comes next: flow " +
            "\"hosted\" = a sign-in link (url) to give the person; Telegram = an access code with steps (status_url " +
            "polls it — social_telegram_status); SMS = the outlet's numbers to choose from (social_connect_sms).",
            "POST", "/api/social/connect", C("social.manage", "channels.manage"),
            new[]
            {
                A.Str("platform", "whatsapp, facebook (Messenger / Page), instagram, telegram, sms, tiktok, youtube, " +
                                  "linkedin, twitter (x), threads, pinterest, googlebusiness, bluesky, reddit, discord, " +
                                  "slack, snapchat.", req: true),
                A.Str("purpose", "inbox (DMs into the channel's inbox) or social (comments / posts)."),
                A.Entity("The entity slug that gets the account; omit for the organization."),
            }, outside: true);

        yield return T(g, "social_telegram_status", "Check a Telegram connection code",
            "Whether the access code from social_connect_url (platform telegram) has been sent to the bot yet: " +
            "pending, expired, or connected (the Telegram inbox exists from then on).",
            "GET", "/api/social/connect/telegram/status", C("social.manage", "channels.manage"),
            new[]
            {
                A.Str("code", "The access code social_connect_url returned.", req: true),
                A.Entity("The same entity the connect named."),
            });

        yield return T(g, "social_connect_telegram_chat", "Connect a Telegram group or channel by id",
            "Connect a Telegram group or channel whose admins already include the connection bot, by its chat id " +
            "(a group's negative id, or @channel). It becomes a Telegram inbox.",
            "POST", "/api/social/connect/telegram", C("social.manage", "channels.manage"),
            new[]
            {
                A.Str("chat_id", "The chat id (-100…) or @channelname.", req: true),
                A.Entity("The entity slug that gets the inbox; omit for the organization."),
            }, outside: true);

        yield return T(g, "social_connect_sms", "Turn on SMS for a number",
            "Turn SMS on for one of the outlet's phone numbers (social_connect_url with platform sms lists them) so " +
            "it becomes an SMS inbox. A US number receives at once but sends only after its carrier registration.",
            "POST", "/api/social/connect/sms", C("social.manage", "channels.manage"),
            new[]
            {
                A.Str("phone_number_id", "The number's id from the list.", req: true),
                A.Entity("The entity slug that gets the inbox; omit for the organization."),
            }, outside: true);

        yield return T(g, "social_assign_account", "Assign a social account to an outlet",
            "Bind a connected social account (or a managed WhatsApp / SMS inbox) to an entity (outlet), or back to the " +
            "organization level: it moves to that entity's connection profile, and its existing social conversations and not-yet-published posts follow. " +
            "Organization-level accounts are usable from every outlet; an outlet's own accounts from that outlet only.",
            "PATCH", "/api/social/accounts/{id}", manage,
            new[]
            {
                A.Str("id", "The account id (social_list_accounts, or zernio_account_id on an inbox row).", req: true),
                A.Any("entity", "The entity slug (or id) to bind it to; null or \"\" for the organization level.", req: true),
            }, outside: true, idempotent: true);

        yield return T(g, "social_assign_page", "Assign a Facebook Page to an outlet",
            "One Facebook login often manages several Pages: bind one Page of a facebook account to an entity " +
            "(outlet) so that outlet posts to it (target \"<account id>:<page id>\"), or null / \"\" to make it follow " +
            "the account's level again. The outlet must sit at or under the account's own level.",
            "PATCH", "/api/social/accounts/{id}/pages/{pageId}", manage,
            new[]
            {
                A.Str("id", "The facebook account's id (social_list_accounts).", req: true),
                A.Str("pageId", "The Facebook Page id (the account's pages[].id).", req: true),
                A.Any("entity", "The entity slug (or id); null or \"\" = follow the account's level.", req: true),
            }, idempotent: true);

        yield return T(g, "social_select_page", "Choose the Facebook Page for the inbox",
            "Switch which Page of a facebook account receives comments and messages in the inbox (the selected Page; " +
            "also the Page a post without a page id goes to). The account's inbox conversations take that Page's outlet.",
            "POST", "/api/social/accounts/{id}/pages/{pageId}/select", manage,
            new[]
            {
                A.Str("id", "The facebook account's id (social_list_accounts).", req: true),
                A.Str("pageId", "The Facebook Page id (the account's pages[].id).", req: true),
            }, outside: true, idempotent: true);

        yield return T(g, "social_disconnect_account", "Disconnect a social account",
            "Disconnect a managed connection (a social account or a WhatsApp / Messenger / Instagram / Telegram / SMS " +
            "inbox connected that way) and remove its inbox here (its conversations stay). The " +
            "platform grant is revoked — ask the person first.", "DELETE", "/api/social/accounts/{id}", manage,
            new[] { A.Str("id", "The account id (social_list_accounts, or zernio_account_id on an inbox row).", req: true) },
            destructive: true, outside: true);

        yield return T(g, "social_hide_comment", "Hide or unhide a social comment",
            "Hide (or unhide) the public comment a social conversation is about, on Facebook, Instagram, Threads, X " +
            "or TikTok. A hidden comment stays visible only to its author and the page.",
            "POST", "/api/social/threads/{key}/hide", C("chats.send"),
            new[]
            {
                A.Str("key", "The social conversation's key (comment:<account>:<comment>).", req: true),
                A.Bool("hidden", "true = hide (default), false = unhide."),
                A.Int("thread_id", "Which conversation, when the key is ambiguous.", @in: ArgIn.Query),
            }, outside: true, idempotent: true);

        yield return T(g, "social_private_reply", "Reply privately to a commenter",
            "Send ONE private direct message to the author of an Instagram or Facebook comment (platforms allow one " +
            "per comment, within 7 days). Outward-facing — check the wording with the person first.",
            "POST", "/api/social/threads/{key}/private-reply", C("chats.send"),
            new[]
            {
                A.Str("key", "The social conversation's key (comment:<account>:<comment>).", req: true),
                A.Str("text", "The message.", req: true),
                A.Int("thread_id", "Which conversation, when the key is ambiguous.", @in: ArgIn.Query),
            }, outside: true);

        yield return T(g, "social_list_posts", "List social posts",
            "Social posts composed in ChatBranch: drafts, scheduled, published, partly published and failed, with " +
            "per-account results and links. Filter by entity, status (comma list) and a from / to date window.",
            "GET", "/api/social/posts", view,
            new[]
            {
                A.Entity(), A.Str("status", "draft, scheduled, publishing, published, partial, failed (comma list)."),
                A.Str("from", "ISO date-time — only posts scheduled / created from then."),
                A.Str("to", "ISO date-time — up to then."),
            }, link: (_, _) => Page("Open social posts", "/social"));

        yield return T(g, "social_get_post", "Get a social post",
            "One social post with its status, per-account results (published URL or error) and schedule.",
            "GET", "/api/social/posts/{id:int}", view, new[] { postId });

        var postFields = new[]
        {
            A.Str("content", "The post text."),
            A.StrList("account_ids", "Targets to publish to (social_list_accounts): an account id, or " +
                                     "\"<account id>:<page id>\" for one Facebook Page (pages[].target)."),
            A.Arr("media", "[{url, type}] — public https URLs; type image | video | gif | document."),
            A.Str("scheduled_at", "ISO 8601 time to publish; omit (with publish_now) to publish immediately."),
            A.Str("timezone", "IANA time zone for a scheduled_at without an offset."),
            A.Bool("publish_now", "Publish immediately."),
            A.Bool("draft", "Save as a draft only (nothing is published)."),
        };
        yield return T(g, "social_create_post", "Create, schedule or publish a social post",
            "Compose a social post for one or more connected accounts: save a draft, schedule it, or publish now. " +
            "Publishing is public and outward-facing — always confirm the text, accounts and time with the person first.",
            "POST", "/api/social/posts", publish,
            postFields.Concat(new[] { A.Entity("The entity whose accounts post it; omit for the organization.", ArgIn.Query) }).ToArray(),
            outside: true);

        yield return T(g, "social_update_post", "Edit a social post",
            "Change a draft, scheduled or failed social post (text, accounts, media, time), or promote a draft to " +
            "scheduled / published. Publishing is outward-facing — confirm with the person first.",
            "PATCH", "/api/social/posts/{id:int}", publish, new[] { postId }.Concat(postFields).ToArray(), outside: true);

        yield return T(g, "social_delete_post", "Delete a social post",
            "Delete a social post: a draft or scheduled post is cancelled; a published one is taken down from the " +
            "platforms that allow it (not Instagram or TikTok). Ask the person first.",
            "DELETE", "/api/social/posts/{id:int}", publish, new[] { postId }, destructive: true, outside: true);

        yield return T(g, "social_retry_post", "Retry a failed social post",
            "Publish a failed or partly published social post again on the platforms that failed.",
            "POST", "/api/social/posts/{id:int}/retry", publish, new[] { postId }, outside: true);

        yield return T(g, "social_draft_post", "Draft a social post with AI",
            "Write a ready-to-publish social post from a short brief (nothing is published): fits the platforms' " +
            "length and never invents prices or offers.", "POST", "/api/social/posts/draft", publish,
            new[]
            {
                A.Str("brief", "What the post should say.", req: true),
                A.StrList("platforms", "Target platforms (twitter, instagram…) — sets the length."),
                A.Str("tone", "e.g. friendly, formal, playful."),
            }, write: false);
    }
}
