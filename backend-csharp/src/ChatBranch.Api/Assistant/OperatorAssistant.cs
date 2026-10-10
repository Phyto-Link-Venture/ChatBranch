using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBranch.Api.Tools;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Mcp;
using ChatBranch.Domain;

namespace ChatBranch.Api.Assistant;

/// <summary>
/// "Ask ChatBranch" — the in-dashboard assistant. It acts AS the signed-in user
/// over the shared tool registry (plus allowlisted external MCP tools): reads
/// run straight away; anything that changes something becomes a pending action
/// the person confirms with a button (<see cref="AssistantAction"/>), so the
/// model can propose but never act on its own.
///
/// With a configured LLM it runs the function-calling loop. Without one (the
/// sandbox) a deterministic interpreter still helps: finding contacts, counting
/// open conversations, recent activity, "go to …", and answering "how do I…"
/// from the product guide and the knowledge base.
/// </summary>
public sealed class OperatorAssistant
{
    public sealed record ChatTurn(string Role, string Content);

    public sealed record Step(string Tool, string Title, bool Ok, string Summary);

    public sealed record Pending(string Id, string Tool, string Title, JsonElement Args, string Summary,
        string ExpiresAt);

    public sealed record Reply(string Text, List<Step> Steps, List<Pending> PendingActions,
        List<ToolLink> Navigation, string Mode);

    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);
    private const int MaxTurns = 20;
    private const int MaxTurnChars = 4000;

    private readonly ToolContext _ctx;
    private readonly ILlmClient _llm;
    private readonly IHttpClientFactory _http;
    private readonly string _path;

    public OperatorAssistant(ToolContext ctx, ILlmClient llm, IHttpClientFactory http, string? path)
    {
        _ctx = ctx;
        _llm = llm;
        _http = http;
        _path = string.IsNullOrWhiteSpace(path) ? "/dashboard" : path!;
    }

    public async Task<Reply> ChatAsync(IReadOnlyList<ChatTurn> history, CancellationToken ct)
    {
        var turns = history
            .Where(t => t.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(t.Content))
            .TakeLast(MaxTurns)
            .Select(t => new ChatTurn(t.Role, t.Content.Length > MaxTurnChars ? t.Content[..MaxTurnChars] : t.Content))
            .ToList();
        var last = turns.LastOrDefault(t => t.Role == "user")?.Content ?? "";

        if (_llm.IsConfigured)
        {
            var reply = await LlmAsync(turns, ct);
            if (reply != null) return reply;
        }
        return await SandboxAsync(last, ct);
    }

    // =====================================================================
    // LLM path
    // =====================================================================

    private async Task<Reply?> LlmAsync(List<ChatTurn> turns, CancellationToken ct)
    {
        var builtins = ToolRegistry.AllowedFor(_ctx).ToList();
        var external = _ctx.Capabilities.Contains("integrations.view")
            ? ExternalMcpTools.Available(_ctx.Services.GetRequiredService<ScopingService>())
            : new List<ExternalMcpTools.ExternalTool>();

        // A bounded list per call: the core + what this request is about + find_tools
        // (which adds more to THIS list mid-loop — ToolLoop re-offers it every round).
        static LlmTool ToLlm(IChatBranchTool t) => new(t.Name, $"{t.Title}. {t.Description}"
                                                              + (t.IsWrite ? " (Needs the user's confirmation.)" : ""),
            t.InputSchema);
        var asked = string.Join(" ", turns.Where(t => t.Role == "user").TakeLast(2).Select(t => t.Content));
        var tools = ToolRouter.Select(builtins, asked).Select(ToLlm)
            .Concat(external.Select(e => new LlmTool(e.QualifiedName, e.Description, e.Parameters)))
            .Append(new LlmTool(ToolRouter.FindToolsName, ToolRouter.FindToolsDescription, ToolRouter.FindToolsSchema))
            .ToList();

        var steps = new List<Step>();
        var pending = new List<Pending>();
        var proposed = new HashSet<string>();
        var messages = turns.Select(t => new LlmMessage(t.Role, t.Content)).ToList();

        async Task<(string, bool)> Execute(LlmToolCall call, CancellationToken token)
        {
            var args = ParseArgs(call.ArgumentsJson);
            if (call.Name == ToolRouter.FindToolsName)
            {
                var query = args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
                    ? q.GetString() ?? "" : "";
                var found = ToolRouter.Find(builtins, query, 10);
                foreach (var t in found)
                    if (tools.All(x => x.Name != t.Name)) tools.Add(ToLlm(t));
                return (JsonSerializer.Serialize(new
                {
                    loaded = found.Select(t => new { name = t.Name, title = t.Title, changes_something = t.IsWrite }),
                    note = found.Count == 0
                        ? "Nothing matched — try other words, or the user may lack the permission."
                        : "These tools are now available: call them by name.",
                }), true);
            }
            if (ToolRegistry.Find(call.Name) is { } tool)
            {
                if (!ToolRegistry.Allowed(_ctx, tool))
                {
                    var denied = await ToolRegistry.ExecuteAsync(_ctx, tool.Name, args, token); // audited refusal
                    steps.Add(new Step(tool.Name, tool.Title, false, denied.Error ?? "not allowed"));
                    return ($"error: {denied.Error}", false);
                }
                if (tool.IsWrite)
                {
                    var key = tool.Name + args.GetRawText();
                    if (!proposed.Add(key))
                        return ("already queued for confirmation", true);
                    var p = await ProposeAsync(tool.Name, tool.Title, args, SafeSummary(tool, args), token);
                    pending.Add(p);
                    steps.Add(new Step(tool.Name, tool.Title, true, "Waiting for your confirmation"));
                    return ($"PENDING: queued for the user's confirmation as \"{p.Summary}\". " +
                            "It has NOT happened yet — tell the user to confirm it.", true);
                }
                var outcome = await ToolRegistry.ExecuteAsync(_ctx, tool.Name, args, token);
                steps.Add(new Step(tool.Name, tool.Title, outcome.Ok,
                    outcome.Ok ? SafeSummary(tool, args) : outcome.Error ?? "failed"));
                return (Cap(outcome.Json(), 12000), outcome.Ok);
            }

            var ext = external.FirstOrDefault(e => e.QualifiedName == call.Name);
            if (ext == null) return ($"error: unknown tool {call.Name}", false);
            var title = ext.Tool.Title.Length > 0 ? ext.Tool.Title : ext.Tool.Name;
            if (!ext.Tool.ReadOnly)
            {
                if (!proposed.Add(call.Name + args.GetRawText())) return ("already queued for confirmation", true);
                var p = await ProposeAsync(ext.QualifiedName, $"{ext.Server.Name}: {title}", args,
                    $"{ext.Server.Name} → {title}", token);
                pending.Add(p);
                steps.Add(new Step(ext.QualifiedName, title, true, "Waiting for your confirmation"));
                return ($"PENDING: queued for the user's confirmation as \"{p.Summary}\".", true);
            }
            var (text, ok) = await RunExternalAsync(ext, args, token);
            steps.Add(new Step(ext.QualifiedName, title, ok, ok ? $"{ext.Server.Name}" : text));
            return (text, ok);
        }

        ToolLoop.Outcome outcomeLoop;
        try
        {
            outcomeLoop = await ToolLoop.RunAsync(_llm, SystemPrompt(), messages, tools, Execute,
                maxRounds: 6, temperature: 0.2, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }

        RecordUsage(outcomeLoop);
        // A configured provider that failed degrades to the sandbox echo — answer
        // deterministically instead of echoing the question back.
        if (outcomeLoop.Provider == "sandbox" && steps.Count == 0) return null;

        var text = outcomeLoop.Text.Trim();
        if (text.Length == 0)
            text = pending.Count > 0
                ? "I've prepared that — please confirm below."
                : "I couldn't finish that in one go. Try asking a narrower question.";
        var nav = _ctx.Links.Concat(LinksIn(text)).ToList();
        return new Reply(text, steps, pending, Dedupe(nav), "llm");
    }

    private string SystemPrompt()
    {
        var entity = _ctx.EntitySlug ?? "All entities";
        return $"""
            You are ChatBranch's built-in assistant, inside the ChatBranch dashboard. You help {_ctx.Username} (role: {_ctx.Role}) get things done in the product.

            Rules:
            - You act AS this user, with exactly their permissions and entity scope. Use tools to look things up; never invent contacts, numbers or messages.
            - Tools that change something (sending, creating, updating, assigning, status, notes) are NOT executed when you call them: each call is queued for the user to confirm with a button. Say what you prepared and that it needs their confirmation. Never claim it is done.
            - If a tool says permission is missing, say so plainly and suggest who can help (an admin).
            - Answer "how do I…" questions from the product guide and name the page by its path (for example /chats) so the user can click it.
            - Be brief: a sentence or two, or a short list. Plain text, no tables.
            - Conversations are identified by channel + key, contacts by id — take them from tool results.
            - You see only the tools picked for this request. If you need one you don't have (AI monitor, knowledge, flows, bookings, tags, teams, channels, calls, settings…), call find_tools with a few words first.

            Context: the user is on {_path}; entity filter: {entity}; today is {DateTime.UtcNow:yyyy-MM-dd} (UTC).

            {AssistantGuide.PromptBlock(_ctx.Capabilities)}
            """;
    }

    private void RecordUsage(ToolLoop.Outcome o)
    {
        try
        {
            var db = _ctx.Db;
            db.LlmUsage.Add(new LlmUsage
            {
                Feature = "assistant", Provider = o.Provider, Model = o.Model, Channel = "dashboard",
                Ref = _ctx.Username, PromptTokens = o.PromptTokens, CompletionTokens = o.CompletionTokens, CostUsd = o.CostUsd,
                Detail = new Dictionary<string, object?> { ["tool_calls"] = o.Steps.Count },
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        catch { /* the ledger never breaks a reply */ }
    }

    // =====================================================================
    // Sandbox path — deterministic, so the widget helps with no model at all
    // =====================================================================

    private static readonly Regex GoTo = new(
        @"^\s*(?:please\s+)?(?:go\s+to|open|take\s+me\s+to|navigate\s+to|show\s+me|show)\s+(?:the\s+|my\s+)?(.+?)\s*(?:page|screen|tab)?\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FindContact = new(
        @"(?:find|search(?:\s+for)?|look\s*up|lookup|who\s+is|show)\s+(?:the\s+|a\s+)?(?:contacts?|customers?|person|people)?\s*(?:named|called|for|with)?\s*[""']?(.+?)[""']?\s*[.?!]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CreateContactRe = new(
        @"(?:create|add|new)\s+(?:a\s+)?contact\s+(?:named\s+|called\s+)?(.+?)\s+(?:with\s+(?:phone|number)\s+|phone\s+|on\s+)?(\+?\d[\d\s\-]{6,}\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task<Reply> SandboxAsync(string text, CancellationToken ct)
    {
        var steps = new List<Step>();
        var pending = new List<Pending>();
        var q = text.Trim();
        var lower = q.ToLowerInvariant();

        if (q.Length == 0 || Regex.IsMatch(lower, @"^(hi|hello|hey|help|yo|hai|what can you do|\?)\W*$"))
            return Done(Intro(), steps, pending);

        // "create contact Jane Tan +60123456789" → a confirm card, like the real thing
        if (CreateContactRe.Match(q) is { Success: true } cm)
        {
            var name = cm.Groups[1].Value.Trim().Trim('"', '\'', ',');
            var phone = Regex.Replace(cm.Groups[2].Value, @"[\s\-]", "");
            var args = JsonSerializer.SerializeToElement(new { name, phone });
            var tool = ToolRegistry.Find("create_contact")!;
            if (!ToolRegistry.Allowed(_ctx, tool))
                return Done("You don't have permission to create contacts — ask an admin.", steps, pending);
            pending.Add(await ProposeAsync(tool.Name, tool.Title, args, tool.Summarize(args), ct));
            steps.Add(new Step(tool.Name, tool.Title, true, "Waiting for your confirmation"));
            return Done($"I've prepared a new contact: {name} ({phone}). Confirm below to create it.", steps, pending);
        }

        // counts: "how many unread / open / waiting"
        if (Regex.IsMatch(lower, @"\b(how many|unread|waiting|awaiting|unanswered|open (chats|conversations))\b"))
        {
            var outcome = await RunAsync("list_conversations", new { status = "open", limit = 5 }, steps, ct);
            if (!outcome.Ok) return Done(outcome.Error ?? "I couldn't read the inbox.", steps, pending);
            var data = JsonSerializer.SerializeToElement(outcome.Data);
            var total = data.GetProperty("total").GetInt32();
            var awaiting = data.GetProperty("awaiting_reply").GetInt32();
            var sb = new StringBuilder($"You have {total} open conversation{(total == 1 ? "" : "s")}");
            sb.Append(awaiting > 0 ? $"; {awaiting} {(awaiting == 1 ? "is" : "are")} waiting for a reply." : ", none waiting for a reply.");
            var waiting = data.GetProperty("conversations").EnumerateArray()
                .Where(c => c.GetProperty("awaiting_reply").GetBoolean()).Take(3).ToList();
            foreach (var c in waiting)
                sb.Append($"\n• {c.GetProperty("name").GetString()} ({c.GetProperty("channel").GetString()}): “{c.GetProperty("last_message").GetString()}”");
            return Done(sb.ToString(), steps, pending);
        }

        // recent activity
        if (Regex.IsMatch(lower, @"\b(activity|audit|what happened|recent changes|who changed|who did)\b"))
        {
            var outcome = await RunAsync("list_activity", new { limit = 5 }, steps, ct);
            if (!outcome.Ok) return Done(outcome.Error ?? "I couldn't read the activity log.", steps, pending);
            var data = JsonSerializer.SerializeToElement(outcome.Data);
            var entries = data.GetProperty("entries").EnumerateArray().ToList();
            if (entries.Count == 0) return Done("Nothing in the activity log yet.", steps, pending);
            var sb = new StringBuilder("Most recent activity:");
            foreach (var e in entries)
                sb.Append($"\n• {e.GetProperty("actor").GetString()} — {e.GetProperty("action").GetString()}"
                          + (e.GetProperty("target").GetString() is { Length: > 0 } t ? $" ({t})" : ""));
            return Done(sb.ToString(), steps, pending);
        }

        // "go to analytics", "open chats"
        if (GoTo.Match(q) is { Success: true } gm && PageFor(gm.Groups[1].Value) is { } page)
        {
            _ctx.Links.Add(new ToolLink($"Open {page.Name}", page.Href));
            return Done($"Here's {page.Name}.", steps, pending);
        }

        // "find contact Jane", "who is +6012…"
        if (Regex.IsMatch(lower, @"\b(find|search|look\s*up|lookup|who is)\b")
            && FindContact.Match(q) is { Success: true } fm)
        {
            var term = fm.Groups[1].Value.Trim();
            if (term.Length > 0)
            {
                var outcome = await RunAsync("search_contacts", new { query = term, limit = 5 }, steps, ct);
                if (!outcome.Ok) return Done(outcome.Error ?? "I couldn't search contacts.", steps, pending);
                var data = JsonSerializer.SerializeToElement(outcome.Data);
                var list = data.GetProperty("contacts").EnumerateArray().ToList();
                if (list.Count == 0) return Done($"No contacts match “{term}”.", steps, pending);
                var sb = new StringBuilder($"Found {list.Count} contact{(list.Count == 1 ? "" : "s")} for “{term}”:");
                foreach (var c in list)
                    sb.Append($"\n• {c.GetProperty("name").GetString()} — {c.GetProperty("phone").GetString()}"
                              + (c.GetProperty("email").GetString() is { Length: > 0 } em ? $", {em}" : ""));
                return Done(sb.ToString(), steps, pending);
            }
        }

        // how-to from the product guide
        var section = AssistantGuide.Match(q);
        var looksHowTo = Regex.IsMatch(lower, @"\b(how|where|what|can i|set ?up|configure|connect|add|create|change)\b");
        if (section != null && (looksHowTo || q.Split(' ').Length <= 4))
        {
            if (section.Capability.Length == 0 || _ctx.Capabilities.Contains(section.Capability))
                _ctx.Links.Add(new ToolLink($"Open {section.Title}", section.Href));
            return Done(section.Body, steps, pending);
        }

        // company knowledge ("what's our refund policy")
        if (_ctx.Capabilities.Contains("chats.view") && q.Length >= 4)
        {
            var outcome = await RunAsync("search_knowledge", new { query = q, limit = 3 }, steps, ct);
            if (outcome.Ok)
            {
                var data = JsonSerializer.SerializeToElement(outcome.Data);
                var hits = data.GetProperty("results").EnumerateArray().ToList();
                if (hits.Count > 0)
                {
                    var top = hits[0];
                    var sb = new StringBuilder($"From the knowledge base — {top.GetProperty("title").GetString()}:\n");
                    sb.Append(top.GetProperty("content").GetString());
                    return Done(sb.ToString(), steps, pending);
                }
            }
            else steps.RemoveAt(steps.Count - 1);
        }

        if (section != null)
        {
            _ctx.Links.Add(new ToolLink($"Open {section.Title}", section.Href));
            return Done(section.Body, steps, pending);
        }
        return Done("I'm running without an AI model here, so I understand simple requests. " + Examples(),
            steps, pending);
    }

    private string Intro() =>
        $"Hi {_ctx.Username}! I can look things up and help you get around ChatBranch. " + Examples();

    private string Examples()
    {
        var ex = new List<string>();
        if (_ctx.Capabilities.Contains("contacts.view")) ex.Add("“find contact Jane”");
        if (_ctx.Capabilities.Contains("chats.view")) ex.Add("“how many chats are waiting?”");
        if (_ctx.Capabilities.Contains("activity.view")) ex.Add("“show recent activity”");
        ex.Add("“how do I connect WhatsApp?”");
        ex.Add("“go to analytics”");
        return "Try " + string.Join(", ", ex) + ".";
    }

    private (string Name, string Href)? PageFor(string target)
    {
        var t = target.Trim().ToLowerInvariant().Trim('.', '!', '?');
        foreach (var p in AssistantGuide.Pages)
            if (p.Aliases.Any(a => t == a || t == a + " page") || t == p.Name.ToLowerInvariant())
                return p.Capability.Length == 0 || _ctx.Capabilities.Contains(p.Capability)
                    ? (p.Name, p.Href) : null;
        return null;
    }

    private async Task<ToolOutcome> RunAsync(string tool, object args, List<Step> steps, CancellationToken ct)
    {
        var el = JsonSerializer.SerializeToElement(args);
        var t = ToolRegistry.Find(tool)!;
        var outcome = await ToolRegistry.ExecuteAsync(_ctx, tool, el, ct);
        steps.Add(new Step(tool, t.Title, outcome.Ok, outcome.Ok ? SafeSummary(t, el) : outcome.Error ?? "failed"));
        return outcome;
    }

    private Reply Done(string text, List<Step> steps, List<Pending> pending) =>
        new(text, steps, pending, Dedupe(_ctx.Links.ToList()), "sandbox");

    // =====================================================================
    // pending actions
    // =====================================================================

    private async Task<Pending> ProposeAsync(string tool, string title, JsonElement args, string summary,
        CancellationToken ct)
    {
        var uid = await Auth.Caller.UserIdAsync(_ctx.Db, _ctx.User) ?? 0;
        var row = new AssistantAction
        {
            Id = "act_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            UserId = uid,
            Tool = tool,
            Args = args.GetRawText(),
            Summary = Cap(summary, 300),
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(PendingLifetime),
        };
        _ctx.Db.AssistantActions.Add(row);
        await _ctx.Db.SaveChangesAsync(ct);
        return new Pending(row.Id, tool, title, args.Clone(), row.Summary, Iso(row.ExpiresAt));
    }

    /// <summary>Run an external MCP tool and record it (the registry only knows built-ins).</summary>
    public async Task<(string Text, bool Ok)> RunExternalAsync(ExternalMcpTools.ExternalTool ext, JsonElement args,
        CancellationToken ct)
    {
        var (text, ok) = await ExternalMcpTools.CallAsync(_http.CreateClient("mcp"), ext, args, ct);
        try
        {
            var row = AuditTrail.Add(_ctx.Db, $"tool.{ext.QualifiedName}", ext.Server.Name,
                ok ? "ok" : Cap(text, 300), source: _ctx.Source);
            row.Status = ok ? 200 : 502;
            await _ctx.Db.SaveChangesAsync(ct);
        }
        catch { }
        return (text, ok);
    }

    // ---- helpers ----------------------------------------------------------------

    private static readonly Regex PathRe = new(@"(?<![\w/])(/(?:[a-z][a-z\-]*)(?:/[a-z0-9\-]+)?)", RegexOptions.Compiled);

    private IEnumerable<ToolLink> LinksIn(string text)
    {
        foreach (Match m in PathRe.Matches(text))
        {
            var href = m.Groups[1].Value;
            var page = AssistantGuide.Pages.FirstOrDefault(p => p.Href == href);
            if (page.Href != null && (page.Capability.Length == 0 || _ctx.Capabilities.Contains(page.Capability)))
                yield return new ToolLink($"Open {page.Name}", page.Href);
        }
    }

    private static List<ToolLink> Dedupe(List<ToolLink> links) =>
        links.GroupBy(l => l.Href).Select(g => g.First()).Take(6).ToList();

    internal static JsonElement ParseArgs(string raw)
    {
        try
        {
            var el = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement;
            return el.ValueKind == JsonValueKind.Object ? el.Clone() : JsonDocument.Parse("{}").RootElement;
        }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }

    private static string SafeSummary(IChatBranchTool tool, JsonElement args)
    {
        try { return tool.Summarize(args); } catch { return tool.Title; }
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string Iso(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ");
}
