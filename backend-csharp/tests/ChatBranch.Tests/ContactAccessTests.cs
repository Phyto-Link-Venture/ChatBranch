using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// "Request access": a KL-bound user adding a number that Lumina Melaka already
/// has gets a 409 with an opaque request_token (no name, no id); the request
/// reaches Lumina's company admin (not another company's), approval links the
/// contact to KL, deny / cancel work, and a bad token is a 400.
/// </summary>
public class ContactAccessTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ContactAccessTests(ApiFactory f)
    {
        _f = f;
        _ = _f.CreateClient();
        // in this host the agent role may add contacts (the Contacts page's
        // Add contact is gated on contacts.manage)
        WithDb(db =>
        {
            if (!db.RolePermissions.Any(r => r.Role == Roles.Agent && r.Capability == "contacts.manage"))
            {
                db.RolePermissions.Add(new RolePermission
                    { Role = Roles.Agent, Capability = "contacts.manage", Allowed = true, UpdatedAt = DateTime.UtcNow });
                db.SaveChanges();
            }
            return 0;
        });
    }

    private T WithDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static string Sfx() => Guid.NewGuid().ToString("N")[..8];
    private static string Phone() => "+6017" + Random.Shared.Next(1000000, 9999999);

    private sealed record World(int Lumina, int Kl, int Melaka, int Swift, string KlSlug, string MelakaSlug,
        HttpClient KlAgent, string KlAgentName, HttpClient LuminaAdmin, string LuminaAdminName,
        HttpClient SwiftAdmin, string SwiftAdminName, HttpClient Admin);

    private async Task<World> Arrange()
    {
        var sfx = Sfx();
        var (l, kl, mk, sw) = WithDb(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            Entity Ent(string slug, string name, int? parent)
            {
                var e = new Entity { TenantId = tenant.Id, Name = name, Slug = slug, ParentId = parent, CreatedAt = now, UpdatedAt = now };
                db.Entities.Add(e);
                db.SaveChanges();
                return e;
            }
            var lumina = Ent($"lumina-{sfx}", "Lumina", null);
            var klE = Ent($"lumina-kl-{sfx}", "Lumina KL", lumina.Id);
            var mkE = Ent($"lumina-mk-{sfx}", "Lumina Melaka", lumina.Id);
            var swift = Ent($"swift-{sfx}", "SwiftHaul", null);
            return (lumina, klE, mkE, swift);
        });
        var su = await Platform.As(_f, "superadmin", "super123");
        async Task<(HttpClient, string)> User(string prefix, string role, int entity)
        {
            var name = $"{prefix}{sfx}";
            var res = await su.PostAsJsonAsync("/api/v1/users",
                new { username = name, password = "password123", role, entity_ids = new[] { entity } });
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            return (await Platform.As(_f, name, "password123"), name);
        }
        var (agent, agentName) = await User("klagent", "agent", kl.Id);
        var (la, laName) = await User("lumadmin", "admin", l.Id);
        var (sa, saName) = await User("swiftadmin", "admin", sw.Id);
        return new World(l.Id, kl.Id, mk.Id, sw.Id, kl.Slug, mk.Slug, agent, agentName, la, laName, sa, saName,
            await Platform.As(_f, "admin", "admin123"));
    }

    private async Task<(int Id, string Phone, string Name)> MelakaContact(World w)
    {
        var phone = Phone();
        var name = $"Mei Melaka {Sfx()}";
        var res = await w.Admin.PostAsJsonAsync("/api/v1/contacts",
            new { name, phone, entity_slugs = new[] { w.MelakaSlug } });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return ((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32(), phone, name);
    }

    /// <summary>The KL agent tries to add the Melaka contact's number: the 409's token.</summary>
    private async Task<string> HiddenClashToken(World w, string phone, string mustNotContain, int contactId)
    {
        var res = await w.KlAgent.PostAsJsonAsync("/api/v1/contacts",
            new { name = "Someone new", phone, entity_slugs = new[] { w.KlSlug } });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(mustNotContain, text);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal(JsonValueKind.Null, body.GetProperty("existing_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("existing_name").ValueKind);
        Assert.Equal("Lumina KL", body.GetProperty("target_entity").GetProperty("name").GetString());
        Assert.Equal("Lumina", body.GetProperty("company_name").GetString());
        var token = body.GetProperty("request_token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        // opaque: the contact id is not readable out of it
        Assert.DoesNotContain($"\"c\":{contactId}", TryDecode(token!));
        return token!;
    }

    private static string TryDecode(string token)
    {
        try
        {
            var t = token.Replace('-', '+').Replace('_', '/');
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=')));
        }
        catch { return ""; }
    }

    private List<(string User, string Title, string Body)> AlertsFor(int requestId) => WithDb(db =>
        db.Notifications.Where(n => n.RefKey == $"contact-access:{requestId}")
            .Select(n => new { n.User!.Username, n.Title, n.Body }).ToList()
            .Select(n => (n.Username, n.Title, n.Body)).ToList());

    [Fact]
    public async Task A_hidden_clash_becomes_a_request_that_a_company_admin_approves()
    {
        var w = await Arrange();
        var (contactId, phone, name) = await MelakaContact(w);

        // 409 with a token — no name, no id
        var token = await HiddenClashToken(w, phone, name, contactId);

        // invisible to KL before approval
        Assert.Equal(HttpStatusCode.NotFound, (await w.KlAgent.GetAsync($"/api/contacts/{contactId}")).StatusCode);
        var list = await (await w.KlAgent.GetAsync($"/api/contacts?q={Uri.EscapeDataString(phone)}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, list.GetProperty("contacts").GetArrayLength());

        // ask
        var ask = await w.KlAgent.PostAsJsonAsync("/api/contacts/access-requests",
            new { request_token = token, note = "She walked into KL" });
        Assert.Equal(HttpStatusCode.Created, ask.StatusCode);
        var askText = await ask.Content.ReadAsStringAsync();
        Assert.DoesNotContain(name, askText);
        Assert.DoesNotContain(phone[^7..], askText);
        var req = JsonDocument.Parse(askText).RootElement.GetProperty("request");
        var reqId = req.GetProperty("id").GetInt32();
        Assert.Equal("pending", req.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, req.GetProperty("contact").ValueKind);

        // a duplicate returns the same request
        var again = await w.KlAgent.PostAsJsonAsync("/api/contacts/access-requests", new { request_token = token });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(againBody.GetProperty("created").GetBoolean());
        Assert.Equal(reqId, againBody.GetProperty("request").GetProperty("id").GetInt32());
        Assert.Equal(1, WithDb(db => db.ContactAccessRequests.Count(r => r.ContactId == contactId)));

        // the requester's own list: their request, nothing about the contact
        var mineText = await (await w.KlAgent.GetAsync("/api/contacts/access-requests")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(name, mineText);
        var mine = JsonDocument.Parse(mineText).RootElement;
        Assert.Equal(1, mine.GetProperty("counts").GetProperty("mine_pending").GetInt32());
        Assert.False(mine.GetProperty("can_approve").GetBoolean());

        // Lumina's company admin is alerted (with the contact's name); SwiftHaul's admin is not
        var alerts = AlertsFor(reqId);
        var luminaAlert = Assert.Single(alerts, a => a.User == w.LuminaAdminName);
        Assert.Equal($"{w.KlAgentName} asked to add a customer to Lumina KL", luminaAlert.Title);
        Assert.Contains(name, luminaAlert.Body);
        Assert.DoesNotContain(alerts, a => a.User == w.SwiftAdminName || a.User == w.KlAgentName);

        // another company's admin can't decide (nor see it)
        Assert.Equal(HttpStatusCode.Forbidden,
            (await w.SwiftAdmin.PostAsJsonAsync($"/api/contacts/access-requests/{reqId}/approve", new { })).StatusCode);
        var swiftList = await (await w.SwiftAdmin.GetAsync("/api/contacts/access-requests"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, swiftList.GetProperty("requests").GetArrayLength());
        // nor can the requester approve their own request
        Assert.Equal(HttpStatusCode.Forbidden,
            (await w.KlAgent.PostAsJsonAsync($"/api/contacts/access-requests/{reqId}/approve", new { })).StatusCode);

        // the approver's view: name, masked phone, current entities
        var approverList = await (await w.LuminaAdmin.GetAsync("/api/contacts/access-requests"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(approverList.GetProperty("can_approve").GetBoolean());
        var row = approverList.GetProperty("requests").EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == reqId);
        Assert.True(row.GetProperty("can_decide").GetBoolean());
        Assert.Equal(name, row.GetProperty("contact").GetProperty("name").GetString());
        Assert.Equal("•••• " + phone[^4..], row.GetProperty("contact").GetProperty("phone").GetString());
        Assert.Contains(row.GetProperty("contact").GetProperty("entities").EnumerateArray(),
            e => e.GetProperty("name").GetString() == "Lumina Melaka");
        Assert.Equal("She walked into KL", row.GetProperty("note").GetString());

        // approve
        var ok = await w.LuminaAdmin.PostAsJsonAsync($"/api/contacts/access-requests/{reqId}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var links = WithDb(db => db.ContactEntities.Where(l => l.ContactId == contactId).Select(l => l.EntityId).ToList());
        Assert.Contains(w.Kl, links);
        Assert.Contains(w.Melaka, links);
        Assert.True(WithDb(db => db.AuditLogs.Any(a => a.Action == "contact.access_approved" && a.Target == $"contact:{contactId}")));
        Assert.Equal("approved", WithDb(db => db.ContactAccessRequests.Single(r => r.Id == reqId).Status));

        // now visible to the KL agent, and they were told
        Assert.Equal(HttpStatusCode.OK, (await w.KlAgent.GetAsync($"/api/contacts/{contactId}")).StatusCode);
        Assert.Contains(AlertsFor(reqId), a => a.User == w.KlAgentName && a.Title.Contains("approved"));
        // deciding twice is refused
        Assert.Equal(HttpStatusCode.Conflict,
            (await w.LuminaAdmin.PostAsJsonAsync($"/api/contacts/access-requests/{reqId}/deny", new { })).StatusCode);
    }

    [Fact]
    public async Task Deny_tells_the_requester_and_cancel_withdraws()
    {
        var w = await Arrange();
        var (contactId, phone, name) = await MelakaContact(w);

        var token = await HiddenClashToken(w, phone, name, contactId);
        var reqId = (await (await w.KlAgent.PostAsJsonAsync("/api/contacts/access-requests", new { request_token = token }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetInt32();

        var deny = await w.LuminaAdmin.PostAsJsonAsync($"/api/contacts/access-requests/{reqId}/deny",
            new { reason = "Melaka keeps this account" });
        Assert.Equal(HttpStatusCode.OK, deny.StatusCode);
        Assert.Equal("denied", WithDb(db => db.ContactAccessRequests.Single(r => r.Id == reqId).Status));
        var told = Assert.Single(AlertsFor(reqId), a => a.User == w.KlAgentName);
        Assert.Contains("Melaka keeps this account", told.Body);
        Assert.DoesNotContain(name, told.Body);
        Assert.Equal(HttpStatusCode.NotFound, (await w.KlAgent.GetAsync($"/api/contacts/{contactId}")).StatusCode);
        Assert.DoesNotContain(w.Kl, WithDb(db => db.ContactEntities.Where(l => l.ContactId == contactId).Select(l => l.EntityId).ToList()));
        // the denied request's details stay hidden from the requester
        var denied = await (await w.KlAgent.GetAsync("/api/contacts/access-requests?status=denied")).Content.ReadAsStringAsync();
        Assert.Contains("Melaka keeps this account", denied);
        Assert.DoesNotContain(name, denied);

        // ask again, then withdraw
        var token2 = await HiddenClashToken(w, phone, name, contactId);
        var second = await w.KlAgent.PostAsJsonAsync("/api/contacts/access-requests", new { request_token = token2 });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var id2 = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetInt32();
        Assert.NotEqual(reqId, id2);
        // only the requester cancels
        Assert.Equal(HttpStatusCode.NotFound,
            (await w.LuminaAdmin.PostAsJsonAsync($"/api/contacts/access-requests/{id2}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await w.KlAgent.PostAsJsonAsync($"/api/contacts/access-requests/{id2}/cancel", new { })).StatusCode);
        Assert.Equal("cancelled", WithDb(db => db.ContactAccessRequests.Single(r => r.Id == id2).Status));
        Assert.Equal(HttpStatusCode.Conflict,
            (await w.LuminaAdmin.PostAsJsonAsync($"/api/contacts/access-requests/{id2}/approve", new { })).StatusCode);
        // the approver's alert about it is closed
        Assert.True(WithDb(db => db.Notifications.Where(n => n.RefKey == $"contact-access:{id2}")
            .All(n => n.Read || n.User!.Username == w.KlAgentName)));
    }

    [Fact]
    public async Task A_tampered_expired_or_foreign_token_is_refused()
    {
        var w = await Arrange();
        var (contactId, phone, name) = await MelakaContact(w);
        var token = await HiddenClashToken(w, phone, name, contactId);

        // one character changed
        var mid = token.Length / 2;
        var tampered = token[..mid] + (token[mid] == 'A' ? 'B' : 'A') + token[(mid + 1)..];
        Assert.Equal(HttpStatusCode.BadRequest,
            (await w.KlAgent.PostAsJsonAsync("/api/contacts/access-requests", new { request_token = tampered })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await w.KlAgent.PostAsJsonAsync("/api/contacts/access-requests", new { request_token = "not-a-token" })).StatusCode);

        // issued an hour ago
        var expired = WithDb(db => ContactAccessToken.Issue(new ScopingService(db), contactId, w.Kl, w.KlAgentName,
            DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await w.KlAgent.PostAsJsonAsync("/api/contacts/access-requests", new { request_token = expired })).StatusCode);

        // someone else's token
        Assert.Equal(HttpStatusCode.BadRequest,
            (await w.LuminaAdmin.PostAsJsonAsync("/api/contacts/access-requests", new { request_token = token })).StatusCode);

        Assert.Equal(0, WithDb(db => db.ContactAccessRequests.Count(r => r.ContactId == contactId)));
        // the genuine one still works
        Assert.Equal(HttpStatusCode.Created,
            (await w.KlAgent.PostAsJsonAsync("/api/contacts/access-requests", new { request_token = token })).StatusCode);
    }
}
