using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Api.Tools;

namespace ChatBranch.Api.Assistant;

/// <summary>
/// Keeps Ask ChatBranch's per-call tool list small. The registry has ~250
/// tools; sending every schema on every model call is slow and expensive, so
/// each request offers a fixed core, the tools a cheap deterministic scorer
/// picks for the person's words (names, titles, descriptions and a domain
/// vocabulary — "appointment" → bookings, "outlet" → entities), and
/// <c>find_tools</c>, a meta-tool the model calls to load more mid-conversation.
/// MCP is unaffected: <c>tools/list</c> still returns the whole catalogue.
/// </summary>
public static class ToolRouter
{
    public const string FindToolsName = "find_tools";

    /// <summary>Always offered (when allowed): find, read, reply.</summary>
    public static readonly string[] Core =
    {
        "search_contacts", "get_contact", "list_conversations", "get_conversation", "send_message", "add_note",
        "search_everything", "search_knowledge",
    };

    public const int MaxPicked = 16;

    /// <summary>The meta-tool's schema: a few keywords → the matching tools are loaded.</summary>
    public static readonly JsonElement FindToolsSchema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"What you need to do, in a few words (e.g. 'approve AI draft', 'cancel booking', 'create team')."}},
         "required":["query"]}
        """).RootElement.Clone();

    public const string FindToolsDescription =
        "Find more ChatBranch tools. You only see a few tools at a time; describe what you need to do and the " +
        "matching tools (with their names and what they do) become available to call next.";

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "what", "how", "can", "you", "please", "show", "list", "get", "all", "any",
        "this", "that", "from", "into", "are", "was", "were", "has", "have", "had", "our", "your", "their",
        "there", "here", "who", "why", "when", "which", "about", "them", "they", "want", "need", "would", "could",
        "should", "will", "just", "now", "today", "let", "know", "tell", "give", "see", "one", "some", "out",
        "yang", "dan", "untuk", "saya", "boleh", "tolong", "ini", "itu",
    };

    /// <summary>Words people use → the catalogue domain they mean.</summary>
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
    {
        ["chat"] = new[] { "conversations" }, ["inbox"] = new[] { "conversations", "channels" },
        ["message"] = new[] { "conversations" }, ["conversation"] = new[] { "conversations" },
        ["reply"] = new[] { "conversations", "ai" }, ["handover"] = new[] { "conversations", "ai" },
        ["human"] = new[] { "conversations" }, ["label"] = new[] { "conversations", "tags" },
        ["resolve"] = new[] { "conversations" }, ["unknown"] = new[] { "conversations", "channels" },
        ["customer"] = new[] { "contacts" }, ["client"] = new[] { "contacts" }, ["contact"] = new[] { "contacts" },
        ["person"] = new[] { "contacts" }, ["people"] = new[] { "contacts" }, ["import"] = new[] { "contacts" },
        ["invite"] = new[] { "contacts", "users" }, ["identity"] = new[] { "contacts" }, ["record"] = new[] { "contacts" },
        ["flow"] = new[] { "flows" }, ["workflow"] = new[] { "flows" }, ["sequence"] = new[] { "flows" },
        ["campaign"] = new[] { "flows" }, ["queue"] = new[] { "flows" },
        ["automation"] = new[] { "automations" }, ["rule"] = new[] { "automations", "ai" },
        ["knowledge"] = new[] { "knowledge" }, ["faq"] = new[] { "knowledge" }, ["policy"] = new[] { "knowledge" },
        ["document"] = new[] { "knowledge" }, ["pdf"] = new[] { "knowledge" },
        ["booking"] = new[] { "bookings" }, ["appointment"] = new[] { "bookings" }, ["calendar"] = new[] { "bookings", "integrations" },
        ["slot"] = new[] { "bookings" }, ["availability"] = new[] { "bookings" },
        ["template"] = new[] { "templates" }, ["canned"] = new[] { "templates" }, ["quick"] = new[] { "templates" },
        ["script"] = new[] { "templates", "calls" },
        ["tag"] = new[] { "tags" }, ["team"] = new[] { "teams" }, ["member"] = new[] { "teams", "bindings" },
        ["user"] = new[] { "users" }, ["agent"] = new[] { "users" }, ["staff"] = new[] { "users" },
        ["login"] = new[] { "users" }, ["role"] = new[] { "users" }, ["permission"] = new[] { "users" },
        ["password"] = new[] { "users" }, ["welcome"] = new[] { "users" },
        ["entity"] = new[] { "entities" }, ["outlet"] = new[] { "entities" }, ["branch"] = new[] { "entities" },
        ["brand"] = new[] { "entities" }, ["binding"] = new[] { "bindings" },
        ["number"] = new[] { "channels" }, ["whatsapp"] = new[] { "channels", "conversations" },
        ["channel"] = new[] { "channels" }, ["widget"] = new[] { "channels" }, ["health"] = new[] { "channels" },
        ["routing"] = new[] { "channels" }, ["assignment"] = new[] { "channels" }, ["email"] = new[] { "channels", "conversations" },
        ["recovery"] = new[] { "channels" }, ["webhook"] = new[] { "channels" },
        ["call"] = new[] { "calls", "conversations" }, ["dial"] = new[] { "calls" }, ["phone"] = new[] { "calls" },
        ["voice"] = new[] { "voice", "calls" }, ["pronunciation"] = new[] { "voice" },
        ["notification"] = new[] { "notifications" }, ["bell"] = new[] { "notifications" },
        ["report"] = new[] { "analytics" }, ["analytic"] = new[] { "analytics" }, ["stat"] = new[] { "analytics", "ai" },
        ["usage"] = new[] { "analytics" }, ["cost"] = new[] { "analytics" }, ["live"] = new[] { "analytics" },
        ["audit"] = new[] { "activity" }, ["activity"] = new[] { "activity" }, ["log"] = new[] { "activity", "calls" },
        ["draft"] = new[] { "ai" }, ["monitor"] = new[] { "ai" }, ["bot"] = new[] { "ai" }, ["chatbot"] = new[] { "ai" },
        ["lesson"] = new[] { "ai" }, ["feedback"] = new[] { "ai" }, ["persona"] = new[] { "ai" },
        ["behaviour"] = new[] { "ai" }, ["behavior"] = new[] { "ai" }, ["approve"] = new[] { "ai" },
        ["skill"] = new[] { "skills" }, ["tool"] = new[] { "skills" },
        ["integration"] = new[] { "integrations" }, ["google"] = new[] { "integrations" },
        ["shopify"] = new[] { "integrations" }, ["stripe"] = new[] { "integrations" }, ["sheet"] = new[] { "integrations" },
        ["key"] = new[] { "developers" }, ["mcp"] = new[] { "developers", "integrations" },
        ["setting"] = new[] { "settings" }, ["theme"] = new[] { "settings" }, ["language"] = new[] { "settings" },
        ["field"] = new[] { "settings" }, ["view"] = new[] { "views" }, ["tab"] = new[] { "views" },
        ["history"] = new[] { "assistant" },
        // social media (social_* tools): posts, scheduling, comments, reviews, the platforms
        ["post"] = new[] { "social" }, ["schedule"] = new[] { "social" }, ["schedul"] = new[] { "social" },
        ["publish"] = new[] { "social" }, ["caption"] = new[] { "social" }, ["social"] = new[] { "social" },
        ["instagram"] = new[] { "social", "channels" }, ["facebook"] = new[] { "social", "channels" },
        ["page"] = new[] { "social" }, ["tiktok"] = new[] { "social" }, ["linkedin"] = new[] { "social" },
        ["youtube"] = new[] { "social" }, ["thread"] = new[] { "social", "conversations" },
        ["pinterest"] = new[] { "social" }, ["twitter"] = new[] { "social" }, ["comment"] = new[] { "social" },
        ["review"] = new[] { "social", "ai" }, ["zernio"] = new[] { "social" }, ["telegram"] = new[] { "channels", "social" },
        // Bahasa Melayu
        ["siaran"] = new[] { "social" }, ["hantaran"] = new[] { "social" }, ["jadual"] = new[] { "social" },
        ["terbit"] = new[] { "social" }, ["terbitkan"] = new[] { "social" }, ["kapsyen"] = new[] { "social" },
        ["komen"] = new[] { "social" }, ["ulasan"] = new[] { "social" }, ["sosial"] = new[] { "social" },
    };

    /// <summary>Chinese has no spaces: these substrings anywhere in the text name a domain.</summary>
    private static readonly (string Text, string Domain)[] CjkCues =
    {
        ("帖子", "social"), ("发帖", "social"), ("发布", "social"), ("排程", "social"), ("定时", "social"),
        ("社交", "social"), ("社媒", "social"), ("评论", "social"), ("评价", "social"), ("脸书", "social"),
        ("文案", "social"), ("小红书", "social"),
    };

    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static string Stem(string w)
    {
        w = w.ToLowerInvariant();
        if (w.Length > 5 && w.EndsWith("ies")) return w[..^3] + "y";
        if (w.Length > 5 && w.EndsWith("ing")) return w[..^3];
        if (w.Length > 4 && w.EndsWith("es") && !w.EndsWith("ses")) return w[..^2];
        if (w.Length > 4 && w.EndsWith("ed")) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss")) return w[..^1];
        return w;
    }

    private static List<string> Terms(string text) =>
        Word.Matches(text).Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length >= 3 && !Stop.Contains(w)).Select(Stem).Distinct().ToList();

    /// <summary>How well a tool matches the words (0 = not at all).</summary>
    public static double Score(IChatBranchTool tool, IReadOnlyCollection<string> terms, ISet<string> domains)
    {
        if (terms.Count == 0) return 0;
        var nameParts = tool.Name.Split('_').Select(Stem).ToHashSet();
        var title = Terms(tool.Title).ToHashSet();
        var desc = Terms(tool.Description).ToHashSet();
        double s = domains.Contains(tool.Domain) ? 3 : 0;
        foreach (var t in terms)
        {
            if (nameParts.Contains(t)) s += 4;
            else if (title.Contains(t)) s += 3;
            else if (desc.Contains(t)) s += 1;
        }
        return s;
    }

    private static HashSet<string> DomainsOf(IEnumerable<string> terms) =>
        terms.SelectMany(t => Synonyms.TryGetValue(t, out var d) ? d : Array.Empty<string>()).ToHashSet();

    /// <summary>The tools to offer for this request: the core plus the best matches.</summary>
    public static List<IChatBranchTool> Select(IReadOnlyList<IChatBranchTool> allowed, string text, int max = MaxPicked)
    {
        var chosen = allowed.Where(t => Core.Contains(t.Name)).ToList();
        foreach (var t in Find(allowed, text, max))
            if (!chosen.Contains(t)) chosen.Add(t);
        return chosen;
    }

    /// <summary>The best matches for a query, most relevant first.</summary>
    public static List<IChatBranchTool> Find(IReadOnlyList<IChatBranchTool> allowed, string query, int max = 10)
    {
        var terms = Terms(query);
        var domains = DomainsOf(terms);
        foreach (var (cue, domain) in CjkCues)
            if (query.Contains(cue, StringComparison.Ordinal))
            {
                domains.Add(domain);
                // a domain hit alone scores 3: enough for the domain's tools to be offered
                terms = terms.Append(domain).Distinct().ToList();
            }
        return allowed.Select(t => (t, score: Score(t, terms, domains)))
            .Where(x => x.score >= 3)
            .OrderByDescending(x => x.score).ThenBy(x => x.t.Name.Length)
            .Take(max).Select(x => x.t).ToList();
    }
}
