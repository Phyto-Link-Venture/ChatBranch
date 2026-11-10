namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    /// <summary>
    /// Social media through Zernio: connected accounts, posts, and the comment actions.
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
            "The social media accounts connected through Zernio (Instagram, Facebook, TikTok, YouTube, LinkedIn, X, " +
            "Threads, Google Business…) for an entity: id (Zernio account id), platform, username, whether it is " +
            "active, and what it can do (publish, comments, DMs, reviews).", "GET", "/api/social/accounts",
            C("social.view", "channels.view"),
            new[] { A.Entity(), A.Bool("refresh", "Reconcile with Zernio first.") },
            link: (_, _) => Page("Open channels", "/channels"));

        yield return T(g, "social_connect_url", "Get a social account connect link",
            "A Zernio sign-in link that connects one social media account to an entity (or the organization). Give " +
            "the link to the person — they sign in to the platform themselves; the account then appears by itself.",
            "POST", "/api/social/connect", manage,
            new[]
            {
                A.Str("platform", "instagram, facebook, tiktok, youtube, linkedin, twitter (x), threads, pinterest, " +
                                  "googlebusiness, bluesky, reddit, telegram, discord, slack, snapchat, whatsapp.", req: true),
                A.Entity("The entity slug whose Zernio profile gets the account; omit for the organization."),
            }, outside: true);

        yield return T(g, "social_disconnect_account", "Disconnect a social account",
            "Disconnect a social account from Zernio and remove its inbox here (its conversations stay). The " +
            "platform grant is revoked — ask the person first.", "DELETE", "/api/social/accounts/{id}", manage,
            new[] { A.Str("id", "The Zernio account id (social_list_accounts).", req: true) },
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
            A.StrList("account_ids", "Zernio account ids to publish to (social_list_accounts)."),
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
