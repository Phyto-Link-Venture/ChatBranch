namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    private static IEnumerable<IChatBranchTool> Platform()
    {
        // ---- analytics ------------------------------------------------------------
        const string an = "analytics";
        yield return T(an, "analytics_get_reports", "Get reports",
            "Reports by agent, team, label and channel: conversations TOUCHED in the window, resolved, open, " +
            "messages sent (\"(unassigned)\" is reported, not dropped).", "GET", "/api/analytics/reports",
            C("analytics.view"), new[] { A.Days(), A.Entity() }, link: (_, _) => Page("Open analytics", "/analytics"));
        yield return T(an, "analytics_get_flows", "Flow analytics",
            "Every flow's performance over the window: runs started, completed, failed, messages and calls sent.",
            "GET", "/api/analytics/flows", C("flows.view"), new[] { A.Days(), A.Entity() });
        yield return T(an, "analytics_get_usage", "AI usage and cost",
            "LLM metering: tokens and estimated COST (USD, what the platform pays) by feature (replies, assistant, " +
            "voice, embeddings…), provider and model. Billing managers only.", "GET", "/api/analytics/usage",
            C("billing.manage"), new[] { A.Days() });
        yield return T(an, "billing_get_ai_bill", "AI usage bill",
            "A calendar month's AI bill per company (top-level entity; branches roll up): requests, tokens, the " +
            "usage amount after markup, the monthly fee and the total, in the billing currency; with company, the " +
            "line items by feature and the daily amounts. Billing managers also get cost, markup and margin.",
            "GET", "/api/billing/ai", C("billing.view"), new[]
            {
                A.Str("month", "YYYY-MM; defaults to this month."),
                A.Str("company", "Company (top-level entity) slug or id for the line items."),
            });
        yield return T(an, "billing_get_settings", "Read the AI billing terms",
            "Currency, exchange rate (1 USD = n units), the default markup % and monthly fee, and each company's " +
            "overrides.", "GET", "/api/billing/ai/settings", C("billing.manage"));
        yield return T(an, "billing_set_settings", "Set the AI billing terms",
            "Replace the AI billing terms: currency (3 letters), usd_rate, default_markup_pct, default_monthly_fee " +
            "and companies {\"<top-level entity id>\": {markup_pct, monthly_fee}} (omit a field to use the default).",
            "PUT", "/api/billing/ai/settings", C("billing.manage"), new[]
            {
                A.Str("currency", "3-letter code, e.g. MYR.", req: true),
                A.Num("usd_rate", "1 USD = this many units of the currency.", req: true),
                A.Num("default_markup_pct", "Markup on cost, in percent."),
                A.Num("default_monthly_fee", "Flat fee per company per month, in the currency."),
                A.Obj("companies", "Per-company overrides keyed by top-level entity id."),
            });
        yield return T(an, "analytics_get_engagement", "Engagement tiles",
            "Conversations total, awaiting reply, replied, needing a human, follow-ups set, calls.",
            "GET", "/api/messaging/engagement", C("analytics.view"), new[] { A.Entity() });
        yield return T(an, "analytics_get_live", "What's live right now",
            "The Live chip: conversations waiting for a reply, active calls and how many chats need a human.",
            "GET", "/api/live", null, new[] { A.Entity() });

        // ---- activity -------------------------------------------------------------
        const string ac = "activity";
        var activity = C("activity.view");
        yield return T(ac, "activity_search_log", "Search the activity log",
            "The full activity log, paged with totals: who did what (actor, source user | api | mcp | assistant | " +
            "automation | flow | webhook | system), target, detail, entity, method/path/status, request id.",
            "GET", "/api/audit", activity,
            new[]
            {
                A.Str("from", "ISO lower bound."), A.Str("to", "ISO upper bound (a bare date includes the day)."),
                A.Str("actor", "Username."),
                A.Str("source", "Comma list of sources (user, api, mcp, assistant, automation, flow, webhook, system)."),
                A.Str("action", "Exact action or a prefix ending in * (e.g. thread.*)."), A.Entity(), A.Str("q", "Free text."),
                A.Page, A.PageSize(200),
            }, link: (_, _) => Page("Open the activity log", "/activity"));
        yield return T(ac, "activity_get_facets", "Activity log filters",
            "The actors, actions, sources and entities present in the activity log (for filtering).",
            "GET", "/api/audit/facets", activity);

        // ---- search -----------------------------------------------------------------
        yield return T("search", "search_everything", "Search everything",
            "The command palette's global search: contacts, conversations, templates, knowledge, flows, users " +
            "and entities — each type only if you may open its page; each hit carries its dashboard URL.",
            "GET", "/api/search", null,
            new[]
            {
                A.Str("q", "Search text.", req: true),
                A.Str("types", "Comma list: contacts, conversations, templates, knowledge, flows, users, entities."),
                A.Limit(8, 25), A.Entity(),
            });

        // ---- developers -------------------------------------------------------------
        const string dv = "developers";
        yield return T(dv, "developers_list_api_keys", "List API keys",
            "Your API keys (prefix, scopes, created, last used, expiry, status) — never the secret. scope=tenant " +
            "lists everyone's (api_keys.manage).", "GET", "/api/developers/keys", null,
            new[] { A.Str("scope", "tenant = every key in your tenant.", oneOf: new[] { "tenant" }) },
            link: (_, _) => Page("Open developers", "/developers"));
        yield return T(dv, "developers_revoke_api_key", "Revoke an API key",
            "Revoke an API key now (yours, or any with api_keys.manage) — apps using it stop working. Revoking " +
            "the key you are connected with ends this session.", "DELETE", "/api/developers/keys/{id:int}", null,
            new[] { A.Id("API key") }, destructive: true);
        yield return T(dv, "developers_get_mcp_info", "Describe this MCP server",
            "ChatBranch's MCP endpoint, protocol versions and every tool with the capability it needs and whether " +
            "you may use it.", "GET", "/api/developers/mcp", C("api_keys.manage"));

        // ---- Ask ChatBranch history ---------------------------------------------------
        const string asst = "assistant";
        yield return T(asst, "assistant_list_conversations", "List my Ask ChatBranch chats",
            "Your own Ask ChatBranch conversation history (title, last used).", "GET", "/api/assistant/conversations");
        yield return T(asst, "assistant_get_conversation", "Read an Ask ChatBranch chat",
            "One of your Ask ChatBranch conversations with its turns and confirm cards.",
            "GET", "/api/assistant/conversations/{id:int}", null, new[] { A.Id("assistant conversation") });
        yield return T(asst, "assistant_rename_conversation", "Rename an Ask ChatBranch chat",
            "Rename one of your own Ask ChatBranch conversations in the history.", "PATCH", "/api/assistant/conversations/{id:int}", null,
            new[] { A.Id("assistant conversation"), A.Str("title", "New title.", req: true) }, idempotent: true);
        yield return T(asst, "assistant_delete_conversation", "Delete an Ask ChatBranch chat",
            "Delete one of your Ask ChatBranch conversations (pending confirm cards in it are cancelled).",
            "DELETE", "/api/assistant/conversations/{id:int}", null, new[] { A.Id("assistant conversation") },
            destructive: true);
        yield return T(asst, "assistant_delete_all_conversations", "Clear my Ask ChatBranch history",
            "Delete ALL of your own Ask ChatBranch conversations (pending confirm cards are cancelled).", "DELETE", "/api/assistant/conversations", null,
            destructive: true);
    }
}
