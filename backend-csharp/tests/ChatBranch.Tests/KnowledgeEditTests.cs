using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The two writes the Knowledge page makes to
/// <c>PATCH /api/aicore/knowledge/{id}</c>, pinned against the production
/// report of "a save that returns 200 but does not persist":
///
/// * the editor's "Save changes" (<c>submit()</c> in KnowledgeSettings.tsx)
///   sends the WHOLE body — every field must land, on an organization-wide
///   item and on an entity-scoped one, and re-sending the item's own
///   `entities` must not be read as a scope change;
/// * the list row's on/off button (<c>toggleActive()</c>) sends
///   <c>{"active": …}</c> ALONE — it must flip exactly that one field and
///   leave the text alone, so two taps cancel out: a legitimate 200 + 200 pair
///   that changes nothing. That pair is what 2026-11-11 01:45:46/01:45:48 was,
///   and the response-size arithmetic below (`active` + the derived `live` flip
///   together, 2 bytes) is how the access log identifies it.
/// </summary>
public class KnowledgeEditTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    private static readonly object Gate = new();

    public KnowledgeEditTests(ApiFactory f)
    {
        _f = f;
        _ = _f.CreateClient(); // boot + seed
        lock (Gate)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.Entities.Any(e => e.Slug == "ke-outlet"))
            {
                var tenant = db.Tenants.First(t => t.Slug == "demo");
                var now = DateTime.UtcNow;
                db.Entities.Add(new Entity
                {
                    TenantId = tenant.Id, Name = "KE Outlet", Slug = "ke-outlet", CreatedAt = now, UpdatedAt = now,
                });
                db.SaveChanges();
            }
        }
    }

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<JsonElement> Row(HttpClient c, int id)
    {
        var list = await c.GetFromJsonAsync<JsonElement>("/api/aicore/knowledge");
        return list.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetInt32() == id);
    }

    private async Task<int> Create(HttpClient c, string title, string content, string[] entities)
    {
        var res = await c.PostAsJsonAsync("/api/aicore/knowledge", new
        {
            kind = "faq", title, question = "Why?", content, priority = 20,
            active = true, published = false, entities,
        });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    /// <summary>
    /// The editor's full-body save lands — on an organization-wide item and on
    /// an entity-scoped one, where the body re-sends the entity the item
    /// already has (what `entities: draft.pick ?? availabilityOf(draft)` does).
    /// </summary>
    [Theory]
    [InlineData("")]            // organization-wide
    [InlineData("ke-outlet")]   // entity-scoped, like the item in the report
    public async Task Editor_save_persists_every_field(string availableTo)
    {
        var entities = availableTo.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var admin = await Authed("superadmin", "super123");
        var id = await Create(admin, "KE before", "BODY BEFORE", entities);

        // byte-for-byte the shape submit() sends (KnowledgeSettings.tsx), the
        // cleared date window included
        var scope = string.Join(",", entities.Select(e => $"\"{e}\""));
        var res = await admin.PatchAsync($"/api/aicore/knowledge/{id}", Json($$"""
            {"kind":"policy","title":"KE after","question":"Why not?","content":"BODY AFTER",
             "tags":["refund","sla"],"channels":["whatsapp"],"priority":7,
             "active":true,"published":true,"category":"Returns",
             "starts_at":"","ends_at":"","entities":[{{scope}}]}
            """));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        foreach (var k in new[] { await res.Content.ReadFromJsonAsync<JsonElement>(), await Row(admin, id) })
        {
            Assert.Equal("policy", k.GetProperty("kind").GetString());
            Assert.Equal("KE after", k.GetProperty("title").GetString());
            Assert.Equal("Why not?", k.GetProperty("question").GetString());
            Assert.Equal("BODY AFTER", k.GetProperty("content").GetString());
            Assert.Equal(new[] { "refund", "sla" },
                k.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal(new[] { "whatsapp" },
                k.GetProperty("channels").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal(7, k.GetProperty("priority").GetInt32());
            Assert.True(k.GetProperty("published").GetBoolean());
            Assert.Equal("Returns", k.GetProperty("category").GetString());
            Assert.Null(k.GetProperty("starts_at").GetString());
            Assert.Null(k.GetProperty("ends_at").GetString());
            // re-sending its own availability is not a scope change
            Assert.Equal(entities,
                k.GetProperty("entities").EnumerateArray().Select(x => x.GetProperty("slug").GetString()).ToArray());
            Assert.Equal(entities.Length == 0, k.GetProperty("global").GetBoolean());
        }
    }

    /// <summary>
    /// The list row's on/off button: <c>{"active": …}</c> alone flips that one
    /// field and keeps every other, and two taps are a legitimate no-op pair —
    /// 200, 200, nothing changed. The response grows by exactly 2 bytes when
    /// the item goes off (`active` and the derived `live` each "true"→"false"),
    /// which is the 1041/1043 signature in the production access log.
    /// </summary>
    [Fact]
    public async Task Toggle_active_touches_only_active_and_two_taps_cancel_out()
    {
        var admin = await Authed("superadmin", "super123");
        var id = await Create(admin, "KE toggle", "TOGGLE BODY", Array.Empty<string>());
        var before = await Row(admin, id);

        var off = await admin.PatchAsync($"/api/aicore/knowledge/{id}", Json("""{"active":false}"""));
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var offBody = await off.Content.ReadAsStringAsync();
        var offRow = await Row(admin, id);
        Assert.False(offRow.GetProperty("active").GetBoolean());
        Assert.False(offRow.GetProperty("live").GetBoolean());
        Assert.Equal("TOGGLE BODY", offRow.GetProperty("content").GetString());
        Assert.Equal("KE toggle", offRow.GetProperty("title").GetString());

        var on = await admin.PatchAsync($"/api/aicore/knowledge/{id}", Json("""{"active":true}"""));
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var onBody = await on.Content.ReadAsStringAsync();
        var after = await Row(admin, id);
        Assert.True(after.GetProperty("active").GetBoolean());

        // net effect of the pair: nothing but updated_at
        foreach (var field in new[] { "kind", "title", "question", "content", "category", "priority", "active", "published" })
            Assert.Equal(before.GetProperty(field).ToString(), after.GetProperty(field).ToString());

        // "off" serializes 2 bytes longer than "on": active + live, "true"→"false"
        Assert.Equal(2,
            offBody.Length - Deadline(offBody) - (onBody.Length - Deadline(onBody)));
    }

    /// <summary>
    /// A client that types its booleans as strings — as an MCP client may, and
    /// as <c>knowledge_update</c> passes straight through (EndpointTool only
    /// coerces arrays) — must not be told 200 OK while the flag is dropped.
    /// `priority: "7"` has always been read; `active: "false"` was not.
    /// </summary>
    [Theory]
    [InlineData("\"false\"", false)]
    [InlineData("\"off\"", false)]
    [InlineData("0", false)]
    public async Task Quoted_boolean_is_read_not_silently_dropped(string json, bool expected)
    {
        var admin = await Authed("superadmin", "super123");
        var id = await Create(admin, "KE quoted bool", "QUOTED BODY", Array.Empty<string>());

        var res = await admin.PatchAsync($"/api/aicore/knowledge/{id}",
            Json($$"""{"active":{{json}},"priority":"7"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var row = await Row(admin, id);
        Assert.Equal(expected, row.GetProperty("active").GetBoolean());
        Assert.Equal(7, row.GetProperty("priority").GetInt32());
    }

    /// <summary>The length of the response's <c>updated_at</c> value, which the
    /// clock makes vary — discounted so the boolean arithmetic is exact.</summary>
    private static int Deadline(string body)
    {
        var at = body.IndexOf("\"updated_at\":\"", StringComparison.Ordinal);
        if (at < 0) return 0;
        var start = at + "\"updated_at\":\"".Length;
        return body.IndexOf('"', start) - start;
    }
}
