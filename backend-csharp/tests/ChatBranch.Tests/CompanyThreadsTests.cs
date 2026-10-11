using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Incident 2026-10-11: a customer of company A wrote to company B's new number and
/// A's whole conversation moved into B — B's agent read A's history and B's AI answered
/// from A's invoices. A conversation is now unique per customer PER COMPANY:
/// (channel, key, company). These tests replay the incident and the rules around it.
/// </summary>
public class CompanyThreadsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public CompanyThreadsTests(ApiFactory f) => _f = f;

    private T WithDb<T>(Func<AppDbContext, T> work) => Platform.WithDb(_f, work);

    private static int _n;
    private static string Sfx() => $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];

    /// <summary>Two companies (A with two numbers, B with one), A's contact, a bound agent in each.</summary>
    private sealed record World(int A, int B, int AcctA, int AcctA2, int AcctB, int Liew, string Phone, string Key,
        string AgentA, string AgentB, string Sfx);

    private World Build()
    {
        var sfx = Sfx();
        var digits = "6011" + Random.Shared.Next(10000000, 99999999);
        return WithDb(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            Entity Ent(string name)
            {
                var e = new Entity { TenantId = tenant.Id, Name = name, Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{sfx}",
                    CreatedAt = now, UpdatedAt = now };
                db.Entities.Add(e);
                db.SaveChanges();
                return e;
            }
            var a = Ent("Phyto A");
            var b = Ent("Kong B");
            ChannelAccount Acct(string name, int entity)
            {
                var acct = new ChannelAccount { Channel = "whatsapp", Provider = "baileys", Name = $"{name} {sfx}",
                    EntityId = entity, CreatedAt = now };
                db.ChannelAccounts.Add(acct);
                db.SaveChanges();
                return acct;
            }
            var acctA = Acct("A main", a.Id);
            var acctA2 = Acct("A second", a.Id);
            var acctB = Acct("B number", b.Id);
            var liew = new Contact { Name = $"Liew {sfx}", Phone = "+" + digits, TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now,
                AiProfileSummary = $"A-MEMORY-{sfx}: wants a conduit invoice" };
            db.Contacts.Add(liew);
            db.SaveChanges();
            db.ContactEntities.Add(new ContactEntity { ContactId = liew.Id, EntityId = a.Id, CreatedAt = now });
            db.SaveChanges();
            string Agent(string who, Entity e)
            {
                var username = $"{who}{sfx}";
                var u = new AppUser { Username = username, Password = DjangoPasswordHasher.Hash("password123"), IsActive = true };
                db.Users.Add(u);
                db.SaveChanges();
                var p = new Profile { UserId = u.Id, Role = Roles.Agent, TenantId = tenant.Id };
                p.Entities.Add(e);
                db.Profiles.Add(p);
                db.SaveChanges();
                return username;
            }
            return new World(a.Id, b.Id, acctA.Id, acctA2.Id, acctB.Id, liew.Id, "+" + digits, digits,
                Agent("agenta", a), Agent("agentb", b), sfx);
        });
    }

    private static async Task<JsonElement> Simulate(HttpClient c, string key, string text, int accountId)
    {
        var res = await c.PostAsJsonAsync("/api/whatsapp/simulate",
            new { channel = "whatsapp", key, text, name = "Mr Liew", account_id = accountId });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private List<Thread> ThreadsOf(string key) => WithDb(db =>
        db.Threads.AsNoTracking().Where(t => t.Channel == "whatsapp" && t.Key == key).OrderBy(t => t.Id).ToList());

    private int MessageCount(int threadId) => WithDb(db => db.Messages.Count(m => m.ThreadId == threadId));

    /// <summary>A's long conversation with the customer, then the customer's "Hi" to B's number.</summary>
    private async Task<(Thread A, Thread B)> Incident(World w)
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        await Simulate(su, w.Key, $"Please send INV-0001 {w.Sfx} for the conduit", w.AcctA);
        await Simulate(su, w.Key, $"A-SECRET-{w.Sfx} the PV solar quote", w.AcctA);
        var a = ThreadsOf(w.Key).Single();
        // A's own records about this customer: a private note, a booking, a call, a case summary
        WithDb(db =>
        {
            var now = DateTime.UtcNow;
            db.Messages.Add(new Message { ThreadId = a.Id, Direction = MessageDirection.Out, Source = MessageSource.Agent,
                Text = $"A-NOTE-{w.Sfx} discount approved", Private = true, CreatedAt = now });
            db.Bookings.Add(new Booking { EntityId = w.A, ContactId = w.Liew, ThreadId = a.Id, Title = $"A-BOOKING-{w.Sfx}",
                CalendarKey = $"cal-{w.Sfx}", Status = BookingStatus.Booked, StartUtc = now.AddDays(2),
                EndUtc = now.AddDays(2).AddHours(1), Timezone = "UTC", CreatedAt = now, UpdatedAt = now });
            db.CallRecords.Add(new CallRecord { CallId = $"call-a-{w.Sfx}", ContactId = w.Liew, EntityId = w.A, Phone = w.Phone,
                Name = "Mr Liew", StartedAt = now.ToString("o"), EndedAt = now.ToString("o"),
                Summary = $"A-CALL-{w.Sfx} talked about invoices" });
            db.ConversationSummaries.Add(new ConversationSummary { ContactId = w.Liew, Source = "chat", Channel = "whatsapp",
                Ref = ThreadCompanies.SummaryRef(a.Id), Summary = $"A-SUMMARY-{w.Sfx} invoice INV-0001 pending",
                CreatedAt = now });
            db.SaveChanges();
            return 0;
        });
        var aMessages = MessageCount(a.Id);

        // 16:19 — the customer says "Hi" to company B's brand-new number
        await Simulate(su, w.Key, "Hi", w.AcctB);
        var both = ThreadsOf(w.Key);
        Assert.Equal(2, both.Count);
        var b = both.Single(t => t.Id != a.Id);
        var aAfter = both.Single(t => t.Id == a.Id);
        Assert.Equal(aMessages, MessageCount(a.Id));
        return (aAfter, b);
    }

    [Fact]
    public async Task A_second_companys_number_opens_its_own_conversation_and_the_first_keeps_everything()
    {
        var w = Build();
        var (a, b) = await Incident(w);

        // B's conversation: B's number, entity and company — and NOT A's contact (B can't see him)
        Assert.Equal(w.AcctB, b.AccountId);
        Assert.Equal(w.B, b.EntityId);
        Assert.Equal(w.B, b.CompanyEntityId);
        Assert.Null(b.ContactId);
        Assert.Equal(1, MessageCount(b.Id));
        Assert.Equal("Hi", WithDb(db => db.Messages.Single(m => m.ThreadId == b.Id).Text));

        // A's conversation is untouched: account, entity, company, contact and every message
        Assert.Equal(w.AcctA, a.AccountId);
        Assert.Equal(w.A, a.EntityId);
        Assert.Equal(w.A, a.CompanyEntityId);
        Assert.Equal(w.Liew, a.ContactId);
        Assert.Equal(3, MessageCount(a.Id));   // two inbound + the private note
        Assert.DoesNotContain(WithDb(db => db.Messages.Where(m => m.ThreadId == a.Id).Select(m => m.Text).ToList()),
            t => t == "Hi");

        // the customer writes to each again: each lands in its own company's conversation
        var su = await Platform.As(_f, "superadmin", "super123");
        await Simulate(su, w.Key, "back to A", w.AcctA);
        await Simulate(su, w.Key, "back to B", w.AcctB);
        Assert.Equal(2, ThreadsOf(w.Key).Count);
        Assert.Contains(WithDb(db => db.Messages.Where(m => m.ThreadId == a.Id).Select(m => m.Text).ToList()), t => t == "back to A");
        Assert.Contains(WithDb(db => db.Messages.Where(m => m.ThreadId == b.Id).Select(m => m.Text).ToList()), t => t == "back to B");
        Assert.Equal(w.AcctA, ThreadsOf(w.Key).Single(t => t.Id == a.Id).AccountId);
        Assert.Equal(w.AcctB, ThreadsOf(w.Key).Single(t => t.Id == b.Id).AccountId);

        // a contact link never carries a conversation into another company: linking B's chat to
        // A's contact is refused (the contact is A's), and the thread stays B's
        var admin = await Platform.As(_f, "admin", "admin123");
        await admin.PostAsJsonAsync($"/api/messaging/threads/whatsapp/{w.Key}/link?thread_id={b.Id}", new { contact_id = w.Liew });
        var bAfter = ThreadsOf(w.Key).Single(t => t.Id == b.Id);
        Assert.Equal(w.B, bAfter.CompanyEntityId);
        Assert.Equal(w.B, bAfter.EntityId);
        Assert.NotEqual(w.Liew, bAfter.ContactId);
    }

    [Fact]
    public async Task Two_numbers_of_the_same_company_still_share_one_conversation()
    {
        var w = Build();
        var su = await Platform.As(_f, "superadmin", "super123");
        await Simulate(su, w.Key, "on the main number", w.AcctA);
        await Simulate(su, w.Key, "on the second number", w.AcctA2);
        var t = Assert.Single(ThreadsOf(w.Key));
        Assert.Equal(w.AcctA2, t.AccountId);     // re-attributed to the number they now write to
        Assert.Equal(w.A, t.CompanyEntityId);
        Assert.Equal(w.A, t.EntityId);
        Assert.Equal(w.Liew, t.ContactId);
        Assert.Equal(2, MessageCount(t.Id));
    }

    [Fact]
    public async Task Company_Bs_agent_sees_none_of_company_As_data()
    {
        var w = Build();
        var (a, b) = await Incident(w);
        var agentB = await Platform.As(_f, w.AgentB, "password123");
        var secrets = new[] { $"INV-0001 {w.Sfx}", $"A-SECRET-{w.Sfx}", $"A-NOTE-{w.Sfx}", $"A-BOOKING-{w.Sfx}",
            $"A-CALL-{w.Sfx}", $"A-SUMMARY-{w.Sfx}", $"A-MEMORY-{w.Sfx}" };
        async Task NothingOfA(string url)
        {
            var res = await agentB.GetAsync(url);
            var body = await res.Content.ReadAsStringAsync();
            Assert.True(res.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, $"{url}: {(int)res.StatusCode} {body}");
            foreach (var s in secrets) Assert.DoesNotContain(s, body);
            Assert.DoesNotContain($"\"thread_id\":{a.Id},", body);
        }

        // the chat: B's own conversation, with B's message only
        var mine = await agentB.GetFromJsonAsync<JsonElement>($"/api/whatsapp/threads/{w.Key}");
        Assert.Equal(b.Id, mine.GetProperty("thread_id").GetInt32());
        Assert.Equal("Hi", Assert.Single(mine.GetProperty("messages").EnumerateArray()).GetProperty("text").GetString());
        // …and A's conversation by id is not found for B
        Assert.Equal(HttpStatusCode.NotFound, (await agentB.GetAsync($"/api/whatsapp/threads/{w.Key}?thread_id={a.Id}")).StatusCode);
        var list = await agentB.GetFromJsonAsync<JsonElement>($"/api/inbox/threads?q={w.Key}");
        Assert.Equal(b.Id, Assert.Single(list.GetProperty("threads").EnumerateArray()).GetProperty("thread_id").GetInt32());

        // the contact views: A's contact is not B's — and nothing of A's comes back anyway
        await NothingOfA($"/api/messaging/contacts/{w.Liew}/conversation");
        await NothingOfA($"/api/contacts/{w.Liew}/calls");
        await NothingOfA($"/api/contacts/{w.Liew}");
        await NothingOfA($"/api/contacts/{w.Liew}/channels");
        await NothingOfA($"/api/bookings?contact_id={w.Liew}");
        await NothingOfA("/api/bookings");
        await NothingOfA($"/api/messaging/timeline?phone={Uri.EscapeDataString(w.Phone)}");
        await NothingOfA($"/api/search?q={w.Key}");
        await NothingOfA($"/api/search?q=Liew%20{w.Sfx}");
        await NothingOfA("/api/documents");
        var summary = await agentB.PostAsJsonAsync("/api/messaging/timeline/summary", new { name = "Liew", phone = w.Phone });
        var summaryText = await summary.Content.ReadAsStringAsync();
        foreach (var s in secrets) Assert.DoesNotContain(s, summaryText);

        // A's agent still has the whole of A's conversation and the contact
        var agentA = await Platform.As(_f, w.AgentA, "password123");
        var theirs = await agentA.GetFromJsonAsync<JsonElement>($"/api/whatsapp/threads/{w.Key}");
        Assert.Equal(a.Id, theirs.GetProperty("thread_id").GetInt32());
        Assert.Contains($"A-SECRET-{w.Sfx}", theirs.GetRawText());
        Assert.DoesNotContain("\"Hi\"", theirs.GetRawText());
        var aConvo = await agentA.GetFromJsonAsync<JsonElement>($"/api/messaging/contacts/{w.Liew}/conversation");
        Assert.Contains($"A-SECRET-{w.Sfx}", aConvo.GetRawText());
        Assert.Contains($"A-CALL-{w.Sfx}", await agentA.GetStringAsync($"/api/contacts/{w.Liew}/calls"));
        Assert.Contains($"A-BOOKING-{w.Sfx}", await agentA.GetStringAsync($"/api/bookings?contact_id={w.Liew}"));
    }

    [Fact]
    public async Task A_contact_both_companies_share_shows_each_company_only_its_own()
    {
        // an organization-wide contact (no entity links) may be linked from both companies'
        // conversations: each company's agent sees only its own conversations, calls and bookings
        var w = Build();
        var su = await Platform.As(_f, "superadmin", "super123");
        var shared = WithDb(db =>
        {
            var c = new Contact { Name = $"Shared {w.Sfx}", Phone = "+60122" + Random.Shared.Next(1000000, 9999999),
                TenantId = db.Tenants.First(t => t.Slug == "demo").Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Contacts.Add(c);
            db.SaveChanges();
            return c;
        });
        var key = new string(shared.Phone.Where(char.IsDigit).ToArray());
        await Simulate(su, key, $"A-ONLY-{w.Sfx}", w.AcctA);
        await Simulate(su, key, $"B-ONLY-{w.Sfx}", w.AcctB);
        var threads = ThreadsOf(key);
        Assert.Equal(2, threads.Count);
        Assert.All(threads, t => Assert.Equal(shared.Id, t.ContactId));
        var aT = threads.Single(t => t.CompanyEntityId == w.A);
        var bT = threads.Single(t => t.CompanyEntityId == w.B);
        WithDb(db =>
        {
            var now = DateTime.UtcNow;
            db.CallRecords.AddRange(
                new CallRecord { CallId = $"ca-{w.Sfx}", ContactId = shared.Id, EntityId = w.A, Phone = shared.Phone,
                    StartedAt = now.ToString("o"), Summary = $"A-CALL-{w.Sfx}" },
                new CallRecord { CallId = $"cb-{w.Sfx}", ContactId = shared.Id, EntityId = w.B, Phone = shared.Phone,
                    StartedAt = now.ToString("o"), Summary = $"B-CALL-{w.Sfx}" });
            db.Bookings.AddRange(
                new Booking { EntityId = w.A, ContactId = shared.Id, ThreadId = aT.Id, Title = $"A-BOOK-{w.Sfx}",
                    CalendarKey = "a", Status = BookingStatus.Booked, StartUtc = now.AddDays(1), EndUtc = now.AddDays(1).AddHours(1),
                    Timezone = "UTC", CreatedAt = now, UpdatedAt = now },
                new Booking { EntityId = w.B, ContactId = shared.Id, ThreadId = bT.Id, Title = $"B-BOOK-{w.Sfx}",
                    CalendarKey = "b", Status = BookingStatus.Booked, StartUtc = now.AddDays(1), EndUtc = now.AddDays(1).AddHours(1),
                    Timezone = "UTC", CreatedAt = now, UpdatedAt = now });
            db.SaveChanges();
            return 0;
        });

        foreach (var (agent, own, other) in new[] { (w.AgentA, "A", "B"), (w.AgentB, "B", "A") })
        {
            var c = await Platform.As(_f, agent, "password123");
            var convo = await c.GetStringAsync($"/api/messaging/contacts/{shared.Id}/conversation");
            Assert.Contains($"{own}-ONLY-{w.Sfx}", convo);
            Assert.DoesNotContain($"{other}-ONLY-{w.Sfx}", convo);
            var calls = await c.GetStringAsync($"/api/contacts/{shared.Id}/calls");
            Assert.Contains($"{own}-CALL-{w.Sfx}", calls);
            Assert.DoesNotContain($"{other}-CALL-{w.Sfx}", calls);
            var bookings = await c.GetStringAsync($"/api/bookings?contact_id={shared.Id}");
            Assert.Contains($"{own}-BOOK-{w.Sfx}", bookings);
            Assert.DoesNotContain($"{other}-BOOK-{w.Sfx}", bookings);
            var timeline = await c.GetStringAsync($"/api/messaging/timeline?phone={Uri.EscapeDataString(shared.Phone)}");
            Assert.Contains($"{own}-ONLY-{w.Sfx}", timeline);
            Assert.DoesNotContain($"{other}-ONLY-{w.Sfx}", timeline);
            Assert.DoesNotContain($"{other}-CALL-{w.Sfx}", timeline);
        }

        // the org-wide admin sees both, and the chat's merged view keeps to ONE company
        var admin = await Platform.As(_f, "admin", "admin123");
        var all = await admin.GetStringAsync($"/api/messaging/contacts/{shared.Id}/conversation");
        Assert.Contains($"A-ONLY-{w.Sfx}", all);
        Assert.Contains($"B-ONLY-{w.Sfx}", all);
        var onlyB = await admin.GetStringAsync($"/api/messaging/contacts/{shared.Id}/conversation?company={w.B}");
        Assert.Contains($"B-ONLY-{w.Sfx}", onlyB);
        Assert.DoesNotContain($"A-ONLY-{w.Sfx}", onlyB);
        var callsB = await admin.GetStringAsync($"/api/contacts/{shared.Id}/calls?company={w.B}");
        Assert.Contains($"B-CALL-{w.Sfx}", callsB);
        Assert.DoesNotContain($"A-CALL-{w.Sfx}", callsB);
    }

    [Fact]
    public async Task An_unbound_admin_sees_both_chats_and_must_say_which()
    {
        var w = Build();
        var (a, b) = await Incident(w);
        var admin = await Platform.As(_f, "admin", "admin123");

        // the list: two chats, each naming its company
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/inbox/threads?q={w.Key}");
        var rows = list.GetProperty("threads").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.True(list.GetProperty("multi_company").GetBoolean());
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), rows.Select(r => r.GetProperty("thread_id").GetInt32()).OrderBy(x => x));
        Assert.Contains(rows, r => r.GetProperty("company").GetProperty("id").GetInt32() == w.A);
        Assert.Contains(rows, r => r.GetProperty("company").GetProperty("id").GetInt32() == w.B);

        // a per-chat route without a disambiguator: 409 listing both — never a silent pick
        foreach (var url in new[] { $"/api/whatsapp/threads/{w.Key}", $"/api/messaging/threads/whatsapp/{w.Key}",
                     $"/api/messaging/threads/whatsapp/{w.Key}/summary" })
        {
            var res = await admin.GetAsync(url);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ambiguous_thread", body.GetProperty("code").GetString());
            Assert.Equal(2, body.GetProperty("candidates").GetArrayLength());
        }
        var sent = await admin.PostAsJsonAsync($"/api/whatsapp/threads/{w.Key}/send", new { text = "which one?" });
        Assert.Equal(HttpStatusCode.Conflict, sent.StatusCode);
        Assert.Equal(0, WithDb(db => db.Messages.Count(m => m.Text == "which one?")));

        // thread_id (or company) says which
        var aView = await admin.GetFromJsonAsync<JsonElement>($"/api/whatsapp/threads/{w.Key}?thread_id={a.Id}");
        Assert.Contains($"A-SECRET-{w.Sfx}", aView.GetRawText());
        Assert.Equal(w.A, aView.GetProperty("company").GetProperty("id").GetInt32());
        var bView = await admin.GetFromJsonAsync<JsonElement>($"/api/messaging/threads/whatsapp/{w.Key}?company={w.B}");
        Assert.DoesNotContain($"A-SECRET-{w.Sfx}", bView.GetRawText());
        Assert.Equal(b.Id, bView.GetProperty("thread_id").GetInt32());
        var note = await admin.PostAsJsonAsync($"/api/messaging/threads/whatsapp/{w.Key}/note?thread_id={b.Id}", new { text = "for B" });
        Assert.True(note.IsSuccessStatusCode, await note.Content.ReadAsStringAsync());
        Assert.Equal(b.Id, WithDb(db => db.Messages.Single(m => m.Text == "for B").ThreadId));
        // a thread_id that is not this channel:key is not found
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/whatsapp/threads/60100000000?thread_id={a.Id}")).StatusCode);

        // bulk acts on ids, so picking one company's chat never touches the other's
        var bulk = await admin.PostAsJsonAsync("/api/inbox/threads/bulk", new { thread_ids = new[] { b.Id }, action = "resolve" });
        Assert.True(bulk.IsSuccessStatusCode, await bulk.Content.ReadAsStringAsync());
        Assert.Equal(ThreadStatus.Resolved, ThreadsOf(w.Key).Single(t => t.Id == b.Id).Status);
        Assert.NotEqual(ThreadStatus.Resolved, ThreadsOf(w.Key).Single(t => t.Id == a.Id).Status);

        // a deep link that names ONE conversation keeps working without the id
        var other = "6012" + Random.Shared.Next(10000000, 99999999);
        var su = await Platform.As(_f, "superadmin", "super123");
        await Simulate(su, other, "only A", w.AcctA);
        var unique = await admin.GetAsync($"/api/whatsapp/threads/{other}");
        Assert.Equal(HttpStatusCode.OK, unique.StatusCode);

        // MCP: the same rule — a clear error naming the candidates, then thread_id picks one
        var key = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        var err = await Platform.CallTool(key, "get_conversation", new { channel = "whatsapp", key = w.Key });
        Assert.True(err.GetProperty("isError").GetBoolean());
        var errText = err.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains($"thread_id {a.Id}", errText);
        Assert.Contains($"thread_id {b.Id}", errText);
        var ok = await Platform.CallTool(key, "get_conversation", new { channel = "whatsapp", key = w.Key, thread_id = a.Id });
        Assert.False(ok.GetProperty("isError").GetBoolean(), ok.ToString());
        Assert.Contains($"A-SECRET-{w.Sfx}", ok.GetRawText());
        var state = await Platform.CallTool(key, "conversations_get_state", new { channel = "whatsapp", key = w.Key, thread_id = b.Id });
        Assert.False(state.GetProperty("isError").GetBoolean(), state.ToString());
        Assert.DoesNotContain($"A-SECRET-{w.Sfx}", state.GetRawText());
    }

    [Fact]
    public async Task Unattributed_legacy_conversations_keep_working()
    {
        var su = await Platform.As(_f, "superadmin", "super123");
        var key = "6017" + Random.Shared.Next(10000000, 99999999);
        // no account, no entity: today's org-wide behaviour
        var r1 = await su.PostAsJsonAsync("/api/whatsapp/simulate", new { channel = "whatsapp", key, text = "legacy one" });
        Assert.True(r1.IsSuccessStatusCode);
        var r2 = await su.PostAsJsonAsync("/api/whatsapp/simulate", new { channel = "whatsapp", key, text = "legacy two" });
        Assert.True(r2.IsSuccessStatusCode);
        var t = Assert.Single(ThreadsOf(key));
        Assert.Null(t.CompanyEntityId);
        Assert.Equal(2, MessageCount(t.Id));

        var admin = await Platform.As(_f, "admin", "admin123");
        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/whatsapp/threads/{key}");
        Assert.Equal(t.Id, view.GetProperty("thread_id").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("company").ValueKind);
        var sent = await admin.PostAsJsonAsync($"/api/whatsapp/threads/{key}/send", new { text = "reply to legacy" });
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        Assert.Equal(t.Id, WithDb(db => db.Messages.Single(m => m.Text == "reply to legacy").ThreadId));
        // a bound agent still sees an unassigned conversation (the inbox rule)
        var w = Build();
        var agent = await Platform.As(_f, w.AgentA, "password123");
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"/api/whatsapp/threads/{key}")).StatusCode);
    }

    // ---- the AI: only what the answering company may know -----------------------------------

    private sealed class CapturingLlm : ILlmClient
    {
        public readonly List<string> Seen = new();
        public bool IsConfigured => true;
        public bool SupportsVision => false;
        public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
            CancellationToken ct = default)
        {
            Seen.Add(system);
            Seen.AddRange(messages.Select(m => m.content));
            return Task.FromResult(new LlmResult("ok", 0, 0, "test", "m"));
        }
        public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5, CancellationToken ct = default)
        {
            Seen.Add(system);
            Seen.AddRange(messages.Select(m => m.Content ?? ""));
            return Task.FromResult(ToolScriptLlm.Says("Hello! How can I help?"));
        }
        public string All => string.Join("\n", Seen);
    }

    [Fact]
    public void Company_Bs_AI_prompt_carries_nothing_of_company_As()
    {
        using var t = new TestDb();
        var db = t.Db;
        var now = DateTime.UtcNow;
        var tenant = new Tenant { Name = "T", Slug = "t", Status = "active", CreatedAt = now, UpdatedAt = now };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        var a = new Entity { TenantId = tenant.Id, Name = "Phyto", Slug = "phyto", CreatedAt = now, UpdatedAt = now };
        var b = new Entity { TenantId = tenant.Id, Name = "Kong", Slug = "kong", CreatedAt = now, UpdatedAt = now };
        db.Entities.AddRange(a, b);
        db.SaveChanges();
        // a contact both companies' conversations are linked to (organization-wide: no entity links)
        var c = new Contact { Name = "Mr Liew", Phone = "+60113319000", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now,
            AiProfileSummary = "A-MEMORY wants a conduit invoice", PreferredContactTime = "A-PREF mornings" };
        db.Contacts.Add(c);
        db.SaveChanges();
        Thread T(Entity e) => new() { Channel = "whatsapp", Key = "60113319000", Name = "Mr Liew", ContactId = c.Id,
            EntityId = e.Id, CompanyEntityId = e.Id, CreatedAt = now, UpdatedAt = now };
        var ta = T(a);
        var tb = T(b);
        db.Threads.AddRange(ta, tb);
        db.SaveChanges();
        db.Messages.AddRange(
            new Message { ThreadId = ta.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                Text = "A-HISTORY please flag INV-0001", CreatedAt = now.AddHours(-3) },
            new Message { ThreadId = ta.Id, Direction = MessageDirection.Out, Source = MessageSource.Agent,
                Text = "A-NOTE internal discount", Private = true, CreatedAt = now.AddHours(-2) },
            new Message { ThreadId = tb.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                Text = "Hi", CreatedAt = now });
        db.ConversationSummaries.AddRange(
            new ConversationSummary { ContactId = c.Id, Source = "chat", Channel = "whatsapp",
                Ref = ThreadCompanies.SummaryRef(ta.Id), Summary = "A-SUMMARY invoice INV-0001 pending", CreatedAt = now.AddHours(-1) },
            // contact-level (written from the timeline across conversations): no company can be told
            new ConversationSummary { ContactId = c.Id, Source = "timeline", Channel = "", Ref = "+60113319000",
                Summary = "A-TIMELINE summary of everything", CreatedAt = now.AddMinutes(-30) });
        db.SaveChanges();

        string Prompt(Thread thread)
        {
            using var ctx = t.Fresh();
            var sc = new ScopingService(ctx);
            var llm = new CapturingLlm();
            var assistant = new AssistantService(ctx, sc, new PersonaService(sc), new KnowledgeService(ctx, sc), llm);
            var fresh = ctx.Threads.AsNoTracking().Include(x => x.Contact).Single(x => x.Id == thread.Id);
            Task.Run(() => assistant.GenerateReplyAsync(fresh, applyOutcome: false)).GetAwaiter().GetResult();
            Assert.NotEmpty(llm.Seen);
            return llm.All;
        }

        var forB = Prompt(tb);
        Assert.Contains("Hi", forB);
        foreach (var secret in new[] { "A-HISTORY", "INV-0001", "A-NOTE", "A-SUMMARY", "A-TIMELINE", "A-MEMORY", "A-PREF" })
            Assert.DoesNotContain(secret, forB);

        // A's own reply still has A's history and A's conversation summary (the capture works)
        var forA = Prompt(ta);
        Assert.Contains("A-HISTORY", forA);
        Assert.Contains("A-SUMMARY", forA);
        Assert.DoesNotContain("A-NOTE", forA);   // private notes never reach the model
    }

    // ---- the schema upgrade on an existing SQLite database ------------------------------------

    private static List<string> SqliteIndexes(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'crm_thread'";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    [Fact]
    public void Upgrading_an_existing_sqlite_database_keeps_every_thread_and_swaps_the_unique_key()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cb-company-upgrade-{Guid.NewGuid():N}.sqlite3");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={file}").Options;
        try
        {
            var w = CompanyUpgradeFixture.Arrange(() => new AppDbContext(options));
            using (var db = new AppDbContext(options))
            {
                // make it the OLD schema: no company column, (channel, key) unique
                db.Database.ExecuteSqlRaw($"DROP INDEX \"{AppDbContext.ThreadCompanyIndex}\"");
                db.Database.ExecuteSqlRaw($"DROP INDEX \"{AppDbContext.ThreadUnattributedIndex}\"");
                db.Database.ExecuteSqlRaw("ALTER TABLE \"crm_thread\" DROP COLUMN \"company_entity_id\"");
                db.Database.ExecuteSqlRaw("ALTER TABLE \"crm_callrecord\" DROP COLUMN \"entity_id\"");
                db.Database.ExecuteSqlRaw(
                    $"CREATE UNIQUE INDEX \"{AppDbContext.LegacyThreadIndex}\" ON \"crm_thread\" (\"channel\", \"key\")");
                Assert.Contains(AppDbContext.LegacyThreadIndex, SqliteIndexes(db));
            }

            using (var db = new AppDbContext(options)) Seed.Run(db);   // the boot after the deploy
            using (var db = new AppDbContext(options))
            {
                var idx = SqliteIndexes(db);
                Assert.Contains(AppDbContext.ThreadCompanyIndex, idx);
                Assert.Contains(AppDbContext.ThreadUnattributedIndex, idx);
                Assert.DoesNotContain(AppDbContext.LegacyThreadIndex, idx);
            }
            CompanyUpgradeFixture.AssertUpgraded(() => new AppDbContext(options), w);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// The data both upgrade tests (SQLite here, PostgreSQL in <see cref="PostgresTests"/>) start
/// from and what they check afterwards: every thread kept, the company backfilled (an
/// account's company, else the entity's; none = unattributed), the case-summary cache moved
/// to its thread, and the new key — the same customer in two companies, never twice in one.
/// </summary>
internal static class CompanyUpgradeFixture
{
    public sealed record Rows(int A, int Branch, int B, int OnAccount, int OnEntity, int Unattributed);

    public static Rows Arrange(Func<AppDbContext> open)
    {
        using var db = open();
        Seed.Run(db);
        var tenant = db.Tenants.First();
        var now = DateTime.UtcNow;
        var a = new Entity { TenantId = tenant.Id, Name = "Phyto", Slug = "up-phyto", CreatedAt = now, UpdatedAt = now };
        var b = new Entity { TenantId = tenant.Id, Name = "Kong", Slug = "up-kong", CreatedAt = now, UpdatedAt = now };
        db.Entities.AddRange(a, b);
        db.SaveChanges();
        var branch = new Entity { TenantId = tenant.Id, ParentId = a.Id, Name = "Phyto KL", Slug = "up-phyto-kl",
            CreatedAt = now, UpdatedAt = now };
        db.Entities.Add(branch);
        db.SaveChanges();
        var acct = new ChannelAccount { Channel = "whatsapp", Provider = "baileys", Name = "A KL number",
            EntityId = branch.Id, CreatedAt = now };
        db.ChannelAccounts.Add(acct);
        db.SaveChanges();
        Thread T(string key, int? account, int? entity) => new()
        {
            Channel = "whatsapp", Key = key, Address = key, Name = key, AccountId = account, EntityId = entity,
            CreatedAt = now, UpdatedAt = now,
        };
        // one on an account of A's branch, one with only B's entity, one with neither
        var onAccount = T("601111", acct.Id, null);
        var onEntity = T("602222", null, b.Id);
        var unattributed = T("603333", null, null);
        db.Threads.AddRange(onAccount, onEntity, unattributed);
        db.SaveChanges();
        db.Messages.Add(new Message { ThreadId = onAccount.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
            Text = "kept", CreatedAt = now });
        // the case-summary cache as it was keyed before: "channel:key"
        var contact = new Contact { Name = "Upgrade", Phone = "+601111", TenantId = tenant.Id, CreatedAt = now, UpdatedAt = now };
        db.Contacts.Add(contact);
        db.SaveChanges();
        db.ConversationSummaries.Add(new ConversationSummary { ContactId = contact.Id, Source = "chat", Channel = "whatsapp",
            Ref = "whatsapp:601111", Summary = "old cache", CreatedAt = now });
        db.SaveChanges();
        return new Rows(a.Id, branch.Id, b.Id, onAccount.Id, onEntity.Id, unattributed.Id);
    }

    public static void AssertUpgraded(Func<AppDbContext> open, Rows w)
    {
        using (var db = open())
        {
            Assert.Equal(3, db.Threads.Count());
            Assert.Equal(w.A, db.Threads.Single(t => t.Id == w.OnAccount).CompanyEntityId);   // the account's company
            Assert.Equal(w.Branch, db.Threads.Single(t => t.Id == w.OnAccount).EntityId);
            Assert.Equal(w.B, db.Threads.Single(t => t.Id == w.OnEntity).CompanyEntityId);    // the entity's company
            Assert.Null(db.Threads.Single(t => t.Id == w.Unattributed).CompanyEntityId);      // unattributed stays so
            Assert.Equal("kept", db.Messages.Single(m => m.ThreadId == w.OnAccount).Text);
            Assert.Equal(ThreadCompanies.SummaryRef(w.OnAccount), db.ConversationSummaries.Single().Ref);

            // the new key: another company may have the same customer…
            db.Threads.Add(new Thread { Channel = "whatsapp", Key = "601111", CompanyEntityId = w.B, EntityId = w.B,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        using (var db = open())
        {
            // …the same company not twice
            db.Threads.Add(new Thread { Channel = "whatsapp", Key = "601111", CompanyEntityId = w.A, EntityId = w.A,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
        using (var db = open())
        {
            // unattributed: still once per (channel, key)
            db.Threads.Add(new Thread { Channel = "whatsapp", Key = "603333", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
        using (var db = open())
        {
            Seed.Run(db);   // a second boot changes nothing
            Assert.Equal(4, db.Threads.Count());
            Assert.Equal(w.A, db.Threads.Single(t => t.Id == w.OnAccount).CompanyEntityId);
        }
    }
}
