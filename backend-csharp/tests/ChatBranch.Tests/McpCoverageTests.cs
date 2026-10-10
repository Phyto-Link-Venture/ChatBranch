using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Endpoints;
using ChatBranch.Api.Tools;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// "The MCP supports everything the system can do" as a build guarantee: every
/// signed-in /api operation in the route table is reachable through at least
/// one tool (its <see cref="IChatBranchTool.Covers"/>) or listed in
/// <see cref="McpExclusions"/> with a reason — so a new feature without a tool
/// fails here. Plus the catalogue's own hygiene: names, schemas, annotations.
/// </summary>
public class McpCoverageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public McpCoverageTests(ApiFactory f) => _f = f;

    private List<(string Method, string Path, EndpointMetadataCollection Meta)> ApiRoutes()
    {
        _ = _f.CreateClient();
        return _f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith("/api/", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>())
                .Select(m => (m.ToUpperInvariant(), e.RoutePattern.RawText!, e.Metadata)))
            .ToList();
    }

    private static string Norm(string route) => "/" + route.Trim().Trim('/').ToLowerInvariant();

    [Fact]
    public void Every_signed_in_api_operation_has_a_tool_or_a_documented_exclusion()
    {
        var routes = ApiRoutes();
        Assert.True(routes.Count > 250, $"only {routes.Count} API operations found — is the route table loaded?");

        var covered = ToolRegistry.All.SelectMany(t => t.Covers)
            .Select(r => (r.Method.ToUpperInvariant(), Norm(r.Template))).ToHashSet();

        var missing = routes
            .Where(r => r.Meta.GetMetadata<IAllowAnonymous>() == null) // webhooks, widget, help: no signed-in caller
            .Where(r => !covered.Contains((r.Method, Norm(r.Path))))
            .Where(r => McpExclusions.For(r.Method, r.Path) == null)
            .Select(r => $"{r.Method} {r.Path}").Distinct().OrderBy(x => x).ToList();
        Assert.True(missing.Count == 0,
            "These dashboard operations have no MCP tool. Add a tool (Api/Tools/ToolCatalog.*.cs — usually one " +
            "declarative T(...) line) or, if an AI must never do it, an entry in McpExclusions with the reason:\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void Tools_and_exclusions_point_at_real_routes()
    {
        var routes = ApiRoutes().Select(r => (r.Method, Norm(r.Path))).ToHashSet();
        // every route a tool claims to reach exists (a renamed endpoint breaks its tool)
        var stale = ToolRegistry.All.SelectMany(t => t.Covers.Select(c => (t.Name, c)))
            .Where(x => !routes.Contains((x.c.Method, Norm(x.c.Template))))
            .Select(x => $"{x.Name} → {x.c}").ToList();
        Assert.True(stale.Count == 0, "Tools reaching routes that don't exist:\n" + string.Join("\n", stale));

        // every exact exclusion is a real route, not covered by a tool as well
        var coveredByTool = ToolRegistry.All.SelectMany(t => t.Covers)
            .Select(r => (r.Method.ToUpperInvariant(), Norm(r.Template))).ToHashSet();
        foreach (var rule in McpExclusions.All.Where(r => !r.Prefix))
        {
            Assert.True(routes.Contains((rule.Method, Norm(rule.Route))),
                $"McpExclusions lists {rule.Method} {rule.Route}, which is not a route any more");
            Assert.False(coveredByTool.Contains((rule.Method, Norm(rule.Route))),
                $"{rule.Method} {rule.Route} is excluded AND reached by a tool — drop one");
            Assert.True(rule.Reason.Length >= 20, $"{rule.Method} {rule.Route}: give a real reason");
        }
    }

    [Fact]
    public void Catalogue_is_well_formed()
    {
        var names = ToolRegistry.All.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.True(names.Count >= 200, $"only {names.Count} tools");

        // the original sixteen keep their names
        foreach (var core in new[] { "search_contacts", "get_contact", "create_contact", "update_contact",
                     "list_conversations", "get_conversation", "send_message", "add_note", "set_conversation_status",
                     "assign_conversation", "list_templates", "search_knowledge", "list_flows",
                     "get_analytics_summary", "list_activity", "list_tags" })
            Assert.Contains(core, names);

        var catalog = ((string[])typeof(AdminEndpoints)
            .GetField("CapabilityIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!).ToHashSet();
        var vague = ToolRegistry.All.Where(t => t.Description.Length < 40).Select(t => t.Name).ToList();
        Assert.True(vague.Count == 0, "Describe these so a model can pick them: " + string.Join(", ", vague));
        foreach (var t in ToolRegistry.All)
        {
            Assert.Matches("^[a-z][a-z0-9_]{2,63}$", t.Name);
            Assert.False(string.IsNullOrWhiteSpace(t.Title), t.Name);
            Assert.Equal(JsonValueKind.Object, t.InputSchema.ValueKind);
            Assert.Equal("object", t.InputSchema.GetProperty("type").GetString());
            var props = t.InputSchema.TryGetProperty("properties", out var p) ? p : default;
            if (t.InputSchema.TryGetProperty("required", out var req))
                foreach (var r in req.EnumerateArray())
                    Assert.True(props.ValueKind == JsonValueKind.Object && props.TryGetProperty(r.GetString()!, out _),
                        $"{t.Name}: required '{r}' is not a property");
            foreach (var cap in t.AnyCapabilities)
                Assert.True(catalog.Contains(cap), $"{t.Name}: unknown capability {cap}");
            if (t.Destructive)
            {
                Assert.True(t.IsWrite, $"{t.Name}: destructive but read-only?");
                Assert.True(props.ValueKind == JsonValueKind.Object && props.TryGetProperty("confirm", out _),
                    $"{t.Name}: a destructive tool must offer the confirm argument");
            }
            // new tools follow domain_verb_object
            if (t is EndpointTool)
                Assert.Matches("^[a-z]+_[a-z]+(_[a-z0-9]+)*$", t.Name);
        }
    }

    // ---- docs/mcp.md carries the catalogue and the exclusions, generated from the code ----

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !(Directory.Exists(Path.Combine(dir.FullName, "docs"))
                                && Directory.Exists(Path.Combine(dir.FullName, "backend-csharp"))))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    internal static string CatalogMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var group in ToolRegistry.All.GroupBy(t => t.Domain))
        {
            sb.Append($"\n#### {group.Key} ({group.Count()})\n\n| Tool | What it does | Kind | Needs |\n|---|---|---|---|\n");
            foreach (var t in group)
            {
                var kind = !t.IsWrite ? "read" : t.Destructive ? "write · **destructive**" : "write";
                if (t.OpenWorld) kind += " · open-world";
                var needs = t.AnyCapabilities.Count == 0 ? "signed in" : string.Join(" or ", t.AnyCapabilities.Select(c => $"`{c}`"));
                sb.Append($"| `{t.Name}` | {t.Title} | {kind} | {needs} |\n");
            }
        }
        return sb.ToString().Trim();
    }

    internal static string ExclusionsMarkdown()
    {
        var sb = new System.Text.StringBuilder("| Endpoint | Why there is no tool |\n|---|---|\n");
        foreach (var r in McpExclusions.All)
            sb.Append($"| `{r.Method} {r.Route}{(r.Prefix ? "…" : "")}` | {r.Reason.Replace("|", "\\|")} |\n");
        return sb.ToString().Trim();
    }

    private static void CheckBlock(string path, string name, string expected)
    {
        var doc = File.ReadAllText(path);
        string start = $"<!-- {name}:start -->", end = $"<!-- {name}:end -->";
        var i = doc.IndexOf(start, StringComparison.Ordinal);
        var j = doc.IndexOf(end, StringComparison.Ordinal);
        Assert.True(i >= 0 && j > i, $"docs/mcp.md lost its {name} markers");
        var current = doc[(i + start.Length)..j].Trim();
        if (current == expected) return;
        if (Environment.GetEnvironmentVariable("UPDATE_MCP_DOCS") == "1")
        {
            File.WriteAllText(path, doc[..(i + start.Length)] + "\n\n" + expected + "\n\n" + doc[j..]);
            return;
        }
        Assert.Fail($"docs/mcp.md's {name} block is out of date — run `UPDATE_MCP_DOCS=1 dotnet test --filter " +
                    "McpCoverageTests` to regenerate it from the registry.");
    }

    [Fact]
    public void Docs_list_every_tool_and_every_exclusion()
    {
        var path = RepoFile("docs/mcp.md");
        CheckBlock(path, "tools", CatalogMarkdown());
        CheckBlock(path, "exclusions", ExclusionsMarkdown());
    }

    [Fact]
    public void Write_tools_that_need_no_capability_only_touch_the_callers_own_things()
    {
        // A scoped API key is narrowed by capabilities; a write tool with none
        // escapes that narrowing (still refused for a read-only key) — allowed only
        // for the caller's own rows.
        var ownThings = new HashSet<string>
        {
            "settings_set_my_preferences", "notifications_mark", "notifications_mark_all_read", "notifications_delete",
            "notifications_set_my_preferences", "views_create", "views_update", "views_delete", "views_reorder",
            "assistant_rename_conversation", "assistant_delete_conversation", "assistant_delete_all_conversations",
            "developers_revoke_api_key", "integrations_test_connection", "integrations_disconnect",
            // admin+ endpoints with no capability of their own (the role gate applies)
            "voice_set_stt_provider", "voice_set_llm_provider", "voice_set_default_voice", "voice_import_voice", "voice_update_voice",
            "voice_delete_voice",
        };
        var open = ToolRegistry.All.Where(t => t.IsWrite && t.AnyCapabilities.Count == 0).Select(t => t.Name)
            .Where(n => !ownThings.Contains(n)).ToList();
        Assert.True(open.Count == 0, "Write tools without a capability: " + string.Join(", ", open));
    }
}
