using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The contact page's Communications log + AI summary. "The same person" is a separate
/// contact in each company (one phone, two contacts): each contact's page shows ITS
/// conversations and calls — the log was read by phone, so the second company's contact
/// showed the first one's chats and the summary written on the first one's page.
/// </summary>
public class ContactTimelineTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ContactTimelineTests(ApiFactory f) => _f = f;

    private T WithDb<T>(Func<AppDbContext, T> work) => Platform.WithDb(_f, work);

    private static int _n;

    /// <summary>Companies A, B and C, a number each; one phone saved as a contact in A AND in B.</summary>
    private sealed record World(int A, int B, int C, int AcctA, int AcctB, int AcctC, int BenA, int BenB,
        string Phone, string Key, string AgentA, string Sfx);

    private World Build()
    {
        var sfx = $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];
        var digits = "6019" + Random.Shared.Next(10000000, 99999999);
        return WithDb(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            Entity Ent(string name)
            {
                var e = new Entity { TenantId = tenant.Id, Name = $"{name} {sfx}", Slug = $"{name.ToLowerInvariant()}-{sfx}",
                    CreatedAt = now, UpdatedAt = now };
                db.Entities.Add(e);
                db.SaveChanges();
                return e;
            }
            var (a, b, c) = (Ent("Alpha"), Ent("Bravo"), Ent("Charlie"));
            int Acct(Entity e)
            {
                var acct = new ChannelAccount { Channel = "whatsapp", Provider = "baileys", Name = $"{e.Name} number",
                    EntityId = e.Id, CreatedAt = now };
                db.ChannelAccounts.Add(acct);
                db.SaveChanges();
                return acct.Id;
            }
            int Person(string name, Entity e)
            {
                var p = new Contact { Name = $"{name} {sfx}", Phone = "+" + digits, TenantId = tenant.Id,
                    CreatedAt = now, UpdatedAt = now };
                db.Contacts.Add(p);
                db.SaveChanges();
                db.ContactEntities.Add(new ContactEntity { ContactId = p.Id, EntityId = e.Id, CreatedAt = now });
                db.SaveChanges();
                return p.Id;
            }
            var user = new AppUser { Username = $"agenta{sfx}", Password = DjangoPasswordHasher.Hash("password123"), IsActive = true };
            db.Users.Add(user);
            db.SaveChanges();
            var profile = new Profile { UserId = user.Id, Role = Roles.Agent, TenantId = tenant.Id };
            profile.Entities.Add(a);
            db.Profiles.Add(profile);
            db.SaveChanges();
            return new World(a.Id, b.Id, c.Id, Acct(a), Acct(b), Acct(c), Person("Ben A", a), Person("Ben B", b),
                "+" + digits, digits, user.Username, sfx);
        });
    }

    private static async Task Simulate(HttpClient c, string key, string text, int accountId)
    {
        var res = await c.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "whatsapp", key, text, name = "Ben", account_id = accountId });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>Each company's chat, an unlinked chat at C, and a call per company.</summary>
    private async Task<World> Chats()
    {
        var w = Build();
        var admin = await Platform.As(_f, "admin", "admin123");
        await Simulate(admin, w.Key, $"A-CHAT-{w.Sfx}", w.AcctA);
        await Simulate(admin, w.Key, $"B-CHAT-{w.Sfx}", w.AcctB);
        await Simulate(admin, w.Key, $"C-CHAT-{w.Sfx}", w.AcctC);
        WithDb(db =>
        {
            // each company's conversation went to its own contact; C has none
            var threads = db.Threads.AsNoTracking().Where(t => t.Channel == "whatsapp" && t.Key == w.Key).ToList();
            Assert.Equal(w.BenA, threads.Single(t => t.AccountId == w.AcctA).ContactId);
            Assert.Equal(w.BenB, threads.Single(t => t.AccountId == w.AcctB).ContactId);
            Assert.Null(threads.Single(t => t.AccountId == w.AcctC).ContactId);
            var now = DateTime.UtcNow.ToString("o");
            db.CallRecords.AddRange(
                // dialled from Ben A's page
                new CallRecord { CallId = $"call-a-{w.Sfx}", ContactId = w.BenA, EntityId = w.A, Phone = w.Phone,
                    Name = "Ben", StartedAt = now, EndedAt = now, Summary = $"A-CALL-{w.Sfx}" },
                // mirrored from the agent before any link existed — placed for B
                new CallRecord { CallId = $"call-b-{w.Sfx}", ContactId = null, EntityId = w.B, Phone = w.Phone,
                    Name = "Ben", StartedAt = now, EndedAt = now, Summary = $"B-CALL-{w.Sfx}" });
            db.SaveChanges();
            return 0;
        });
        return w;
    }

    [Fact]
    public async Task Each_companys_contact_shows_only_its_own_chats_and_calls()
    {
        var w = await Chats();
        var admin = await Platform.As(_f, "admin", "admin123");

        var a = await admin.GetStringAsync($"/api/messaging/timeline?contact_id={w.BenA}");
        Assert.Contains($"A-CHAT-{w.Sfx}", a);
        Assert.Contains($"A-CALL-{w.Sfx}", a);
        Assert.DoesNotContain($"B-CHAT-{w.Sfx}", a);
        Assert.DoesNotContain($"B-CALL-{w.Sfx}", a);
        Assert.DoesNotContain($"C-CHAT-{w.Sfx}", a);   // C's unlinked chat is not A's company

        var b = await admin.GetStringAsync($"/api/messaging/timeline?contact_id={w.BenB}");
        Assert.Contains($"B-CHAT-{w.Sfx}", b);
        Assert.Contains($"B-CALL-{w.Sfx}", b);
        Assert.DoesNotContain($"A-CHAT-{w.Sfx}", b);
        Assert.DoesNotContain($"A-CALL-{w.Sfx}", b);
        Assert.DoesNotContain($"C-CHAT-{w.Sfx}", b);

        // the contact's call list (the Channels card's AI Voice row) follows the same rule
        var callsA = await admin.GetStringAsync($"/api/contacts/{w.BenA}/calls");
        Assert.Contains($"A-CALL-{w.Sfx}", callsA);
        Assert.DoesNotContain($"B-CALL-{w.Sfx}", callsA);
        var callsB = await admin.GetStringAsync($"/api/contacts/{w.BenB}/calls");
        Assert.Contains($"B-CALL-{w.Sfx}", callsB);
        Assert.DoesNotContain($"A-CALL-{w.Sfx}", callsB);

        // the phone number on its own still lists every chat on it — but names no one contact
        var byPhone = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/messaging/timeline?phone={Uri.EscapeDataString(w.Phone)}");
        var all = byPhone.GetProperty("events").ToString();
        Assert.Contains($"A-CHAT-{w.Sfx}", all);
        Assert.Contains($"B-CHAT-{w.Sfx}", all);
        Assert.Contains($"C-CHAT-{w.Sfx}", all);
    }

    [Fact]
    public async Task A_summary_belongs_to_the_contact_it_was_written_for()
    {
        var w = await Chats();
        var admin = await Platform.As(_f, "admin", "admin123");

        var made = await admin.PostAsJsonAsync("/api/messaging/timeline/summary", new { contact_id = w.BenA });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var brief = await made.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, brief.GetProperty("event_count").GetInt32());   // A's chat + A's call

        var a = await admin.GetFromJsonAsync<JsonElement>($"/api/messaging/timeline?contact_id={w.BenA}");
        Assert.Equal(JsonValueKind.Object, a.GetProperty("summary").ValueKind);
        var b = await admin.GetFromJsonAsync<JsonElement>($"/api/messaging/timeline?contact_id={w.BenB}");
        Assert.Equal(JsonValueKind.Null, b.GetProperty("summary").ValueKind);
        // nor is it handed out by the phone shared with another contact
        var byPhone = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/messaging/timeline?phone={Uri.EscapeDataString(w.Phone)}");
        Assert.Equal(JsonValueKind.Null, byPhone.GetProperty("summary").ValueKind);
    }

    [Fact]
    public async Task A_bound_agent_reads_their_companys_contact_and_never_the_other()
    {
        var w = await Chats();
        var agent = await Platform.As(_f, w.AgentA, "password123");

        var a = await agent.GetStringAsync($"/api/messaging/timeline?contact_id={w.BenA}");
        Assert.Contains($"A-CHAT-{w.Sfx}", a);
        Assert.DoesNotContain($"B-CHAT-{w.Sfx}", a);

        var b = await agent.GetAsync($"/api/messaging/timeline?contact_id={w.BenB}");
        Assert.Equal(HttpStatusCode.NotFound, b.StatusCode);
        var sb = await agent.PostAsJsonAsync("/api/messaging/timeline/summary", new { contact_id = w.BenB });
        Assert.Equal(HttpStatusCode.NotFound, sb.StatusCode);
    }

    [Fact]
    public async Task One_contact_in_two_companies_shows_both_to_the_organization_and_one_to_each_company()
    {
        var w = await Chats();
        // Ben A is ALSO a contact of B now (one contact record, two companies)
        WithDb(db =>
        {
            db.ContactEntities.Add(new ContactEntity { ContactId = w.BenA, EntityId = w.B, CreatedAt = DateTime.UtcNow });
            var bThread = db.Threads.Single(t => t.Channel == "whatsapp" && t.Key == w.Key && t.AccountId == w.AcctB);
            bThread.ContactId = w.BenA;
            db.SaveChanges();
            return 0;
        });
        var admin = await Platform.As(_f, "admin", "admin123");
        var both = await admin.GetStringAsync($"/api/messaging/timeline?contact_id={w.BenA}");
        Assert.Contains($"A-CHAT-{w.Sfx}", both);
        Assert.Contains($"B-CHAT-{w.Sfx}", both);

        var agent = await Platform.As(_f, w.AgentA, "password123");
        var own = await agent.GetStringAsync($"/api/messaging/timeline?contact_id={w.BenA}");
        Assert.Contains($"A-CHAT-{w.Sfx}", own);
        Assert.DoesNotContain($"B-CHAT-{w.Sfx}", own);
        Assert.DoesNotContain($"B-CALL-{w.Sfx}", own);
    }
}
