namespace ChatBranch.Api.Tools;

public static partial class ToolCatalog
{
    /// <summary>The AI price book modes (ChatBranch.Domain.AiPriceModes).</summary>
    private static readonly string[] AiPriceModeIds = ChatBranch.Domain.AiPriceModes.All;

    private static IEnumerable<IChatBranchTool> Platform()
    {
        // ---- analytics ------------------------------------------------------------
        const string an = "analytics";
        yield return T(an, "analytics_get_reports", "Get reports",
            "Reports by agent, team, label and channel: conversations TOUCHED in the window, resolved, open, " +
            "messages sent (\"(unassigned)\" is reported, not dropped).", "GET", "/api/analytics/reports",
            C("analytics.view"), new[] { A.Days(), A.Entity() }, link: (_, _) => Page("Open the dashboard", "/dashboard"));
        yield return T(an, "analytics_get_flows", "Journey statistics",
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
        yield return T(an, "billing_get_price_books", "AI price books (plans)",
            "The named AI pricing plans and who they price: each plan's mode (markup = percent over provider "
            + "cost, flat = a fixed amount per company per month, unit = a price per 1,000 tokens and/or per AI "
            + "reply), its monthly fee and the per-login AI allowance it grants, plus a searchable, paginated "
            + "list of companies (or every entity) showing the plan that actually governs each one, whether it "
            + "is inherited from a parent or set on the row, any per-company override, and this month's real "
            + "requests, tokens, provider cost and resulting bill.",
            "GET", "/api/billing/ai/price-books", C("billing.manage"), new[]
            {
                A.Str("q", "Search companies by name or slug."),
                A.Str("scope", "companies (default) or entities (companies and their branches).",
                    oneOf: new[] { "companies", "entities" }),
                A.Str("month", "YYYY-MM; defaults to this month (a past month answers with the plans that billed it)."),
                A.Bool("overrides_only", "Only rows with a plan pinned on them or a per-company override."),
                A.Page, A.PageSize(200),
            }, link: (_, _) => Page("Open AI billing settings", "/settings/billing"));
        yield return T(an, "billing_save_price_book", "Create or change an AI price book",
            "Create a pricing plan, or change one by slug (fields you leave out keep their value). mode markup "
            + "needs markup_pct, flat needs flat_amount, unit needs unit_per_1k_tokens and/or unit_per_reply "
            + "(quoted in the billing currency). user_allowance is the monthly AI allowance every login the plan "
            + "covers gets, instead of a cap typed per person. is_default makes it the organisation default. "
            + "Assign it to entities with bindings_apply, kind pricebook-entities. The change prices the current "
            + "month and later; months already billed keep the plan that billed them.",
            "POST", "/api/billing/ai/price-books", C("billing.manage"), new[]
            {
                A.Str("label", "What to call the plan, e.g. \"Reseller 20%\".", req: true),
                A.Str("slug", "Stable key (default: from the name) — pass it to change an existing plan."),
                A.Str("mode", "markup | flat | unit.", oneOf: AiPriceModeIds),
                A.Num("markup_pct", "markup mode: percent over the provider's cost (decimals allowed)."),
                A.Num("flat_amount", "flat mode: the amount per company per month."),
                A.Num("unit_per_1k_tokens", "unit mode: price per 1,000 tokens, in the billing currency."),
                A.Num("unit_per_reply", "unit mode: price per AI reply (customer replies, follow-ups, journey messages)."),
                A.Num("monthly_fee", "A monthly platform fee on top, in the billing currency."),
                A.Num("user_allowance", "Monthly AI allowance per login this plan covers (0 = none)."),
                A.Bool("active", "False keeps the plan for history but stops it being assigned."),
                A.Bool("is_default", "Make this the organisation default plan."),
                A.Str("note", "Why this plan exists / who agreed it."),
            }, idempotent: true);
        yield return T(an, "billing_delete_price_book", "Delete an AI price book",
            "Delete a pricing plan that no entity is assigned to (move them with bindings_apply first, or just "
            + "deactivate the plan). Months that were already billed keep their own copy of it.",
            "DELETE", "/api/billing/ai/price-books/{slug}", C("billing.manage"),
            new[] { A.Str("slug", "The plan's key.", req: true) }, destructive: true);
        yield return T(an, "billing_get_ai_quotas", "AI usage quotas",
            "Monthly AI spending caps (billing currency, priced like the bill) for companies, branches and users in " +
            "your scope, each with this month's spend, % used and status ok | warn (80%) | paused (100% — the AI " +
            "stops answering those chats, or that person's AI features, until next month or a higher cap).",
            "GET", "/api/billing/ai/quotas", C("billing.view", "billing.manage"));
        yield return T(an, "billing_set_ai_quotas", "Set AI usage quotas",
            "Set or clear monthly AI caps (merge: only the keys you send change; 0 or null clears). entities " +
            "{\"<entity id>\": amount} and/or users {\"<username>\": amount}. Company (top-level) caps need " +
            "billing.manage; admins (billing.quotas.manage) may cap branches and other users in their own scope, never " +
            "their own cap or one a billing manager set.",
            "PUT", "/api/billing/ai/quotas", C("billing.quotas.manage", "billing.manage"), new[]
            {
                A.Obj("entities", "Caps keyed by entity id (amount in the billing currency; 0/null = no cap)."),
                A.Obj("users", "Caps keyed by username (amount in the billing currency; 0/null = no cap)."),
            });
        yield return T(an, "billing_get_ai_credit", "Prepaid AI credit balances",
            "Prepaid AI credit: each company's balance (a top-level entity; a branch draws on its company's "
            + "balance), any per-login balances, and the append-only ledger of grants and adjustments — newest "
            + "first, with the amount, the balance after it, who wrote it and why. A company with an enabled "
            + "account is on credit: its AI usage is debited as consumption lines and its AI pauses at zero "
            + "(minus its overdraft limit). Pass kind (comma-separated) to keep only some kinds of line.",
            "GET", "/api/billing/ai/credit", C("billing.view", "billing.manage"), new[]
            {
                A.Str("company", "Company (top-level entity) slug or id — its ledger only."),
                A.Str("entity", "Any entity slug or id: answers which company's balance it draws on."),
                A.Str("user", "Username — that login's own balance and ledger."),
                A.Str("kind", "Only these kinds, comma-separated: grant, topup, promo_bonus, consumption, adjustment, refund, chargeback, expiry, conversion."),
                A.Page, A.PageSize(200),
            }, link: (_, _) => Page("Open AI billing", "/billing"));
        yield return T(an, "billing_grant_ai_credit", "Grant AI credit",
            "Hand prepaid AI credit to a company (a top-level entity) or to one login: a positive amount in the "
            + "billing currency, written as an immutable ledger line plus an activity-log row. Pass "
            + "idempotency_key to make a retry safe — the same key credits exactly once.",
            "POST", "/api/billing/ai/credit/grant", C("billing.manage"), new[]
            {
                A.Str("entity", "Company (top-level entity) slug or id — credit is held per company."),
                A.Str("user", "Username, for the optional per-login balance (instead of entity)."),
                A.Num("amount", "How much credit to add, in the billing currency (positive).", req: true),
                A.Str("note", "Why — it is kept on the ledger line and in the activity log."),
                A.Str("idempotency_key", "Your own key: a repeated call with the same key credits once."),
            }, idempotent: false);
        yield return T(an, "billing_adjust_ai_credit", "Correct an AI credit balance",
            "Correct a prepaid AI credit balance with a SIGNED amount (negative takes credit away). The ledger "
            + "is append-only, so this writes a new line rather than editing one; balances are allowed to go "
            + "negative and are never silently clamped to zero.",
            "POST", "/api/billing/ai/credit/adjust", C("billing.manage"), new[]
            {
                A.Str("entity", "Company (top-level entity) slug or id."),
                A.Str("user", "Username, for a per-login balance (instead of entity)."),
                A.Num("amount", "Signed: positive adds credit, negative takes it away.", req: true),
                A.Str("note", "Why the correction was made."),
                A.Str("idempotency_key", "Your own key: a repeated call applies once."),
            }, idempotent: false);
        yield return T(an, "billing_get_ai_credit_consumption", "AI credit consumption",
            "What AI usage has drawn from each company's prepaid credit: the recent consumption lines, 24 h / 7 d / "
            + "30 d totals, the balance and its warning level, and whether the company's AI is PAUSED for lack of "
            + "credit (its chats are handed to a person). Billing managers also get the metering health: the "
            + "consumption sweep's cursor, how often a credit read failed open, and AI usage rows that failed to save.",
            "GET", "/api/billing/ai/credit/consumption", C("billing.view", "billing.manage"), new[]
            {
                A.Str("company", "Company (top-level entity) slug or id — that company only."),
                A.Int("limit", "Recent consumption lines per company (1–100, default 10)."),
            }, link: (_, _) => Page("Open AI billing", "/billing"));
        yield return T(an, "billing_set_ai_credit_account", "Set AI credit enforcement",
            "Switch prepaid AI credit on or off for one company (a top-level entity) or one login, and set its "
            + "overdraft limit (how far below zero the balance may go before the AI pauses; 0 = at zero) and its "
            + "low-balance warning level (0 = 10% of the last credit added). enabled=false means the balance is "
            + "never debited and never pauses the AI. Only the fields you send change; recorded in the activity log.",
            "PUT", "/api/billing/ai/credit/account", C("billing.manage"), new[]
            {
                A.Str("entity", "Company (top-level entity) slug or id."),
                A.Str("user", "Username, for a per-login balance (instead of entity)."),
                A.Bool("enabled", "true = credit is enforced (debited, pauses the AI at the limit); false = off."),
                A.Num("overdraft_limit", "How far below zero the balance may go before the AI pauses (≥ 0)."),
                A.Num("low_balance_threshold", "Warn the admins at or below this balance (0 = derive one)."),
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
