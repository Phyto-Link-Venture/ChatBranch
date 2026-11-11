using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// "Link existing conversations to contacts" (<see cref="ThreadContactBackfill"/>,
/// <c>POST /api/contacts/link-conversations</c>): the backfill for conversations
/// that already existed when their contact was created — a phone book imported
/// after a WhatsApp history import leaves 287 chats with no contact, because the
/// importer never touches <c>crm_thread</c> and only an inbound message links one.
///
/// The test that matters most is <see cref="A_contact_of_another_company_is_never_linked"/>:
/// this installation holds several unrelated client companies as top-level
/// entities, and a bare phone-tail match would attach one company's contact to
/// another's conversation (incident 2026-10-11). The backfill therefore runs the
/// inbound path's own resolution — <see cref="ContactCompanies.MatchPhone"/> /
/// <see cref="ContactCompanies.MatchEmail"/> against the RECEIVING entity, then
/// <see cref="ContactCompanies.MayLink"/> — and nothing else.
/// </summary>
public class ThreadContactBackfillTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ThreadContactBackfillTests(ApiFactory f)
    {
        _f = f;
        _ = _f.CreateClient(); // boot + seed
    }

    private T WithDb<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private void WithDb(Action<AppDbContext> work) => WithDb<int>(db => { work(db); return 0; });

    private static int _n;
    private static string Sfx() => $"{Interlocked.Increment(ref _n)}{Guid.NewGuid():N}"[..8];
    /// <summary>A fresh number, as a contact book holds it (with the +).</summary>
    private static string Phone() => "+6019" + Random.Shared.Next(1000000, 9999999);
    private static string Digits(string phone) => new(phone.Where(char.IsDigit).ToArray());

    /// <summary>
    /// The real shape of this installation: unrelated client companies as
    /// top-level entities, each with its own WhatsApp number.
    /// </summary>
    private sealed record World(int Phytolink, int PhytolinkKl, int Canmos, int Desheng,
        string PhytolinkSlug, int PhytoAccount, int DeshengAccount);

    private World Companies()
    {
        var sfx = Sfx();
        return WithDb(db =>
        {
            var tenant = db.Tenants.First();
            var now = DateTime.UtcNow;
            int Ent(string slug, int? parent)
            {
                var e = new Entity
                {
                    TenantId = tenant.Id, Name = slug, Slug = slug, ParentId = parent, CreatedAt = now, UpdatedAt = now,
                };
                db.Entities.Add(e);
                db.SaveChanges();
                return e.Id;
            }
            int Account(string name, int entityId)
            {
                var a = new ChannelAccount
                {
                    Channel = Channels.WhatsApp, Provider = "baileys", Name = name, EntityId = entityId, CreatedAt = now,
                };
                db.ChannelAccounts.Add(a);
                db.SaveChanges();
                return a.Id;
            }
            var phyto = Ent($"phytolink-{sfx}", null);
            var kl = Ent($"phytolink-kl-{sfx}", phyto);
            var canmos = Ent($"canmos-{sfx}", null);
            var desheng = Ent($"desheng-{sfx}", null);
            return new World(phyto, kl, canmos, desheng, $"phytolink-{sfx}",
                Account($"Phytolink desk {sfx}", phyto), Account($"Desheng desk {sfx}", desheng));
        });
    }

    /// <summary>A contact of the given entities (none = organization-wide).</summary>
    private int AddContact(string name, string phone = "", string email = "", params int[] entities) =>
        WithDb(db =>
        {
            var now = DateTime.UtcNow;
            var c = new Contact
            {
                Name = name, Phone = phone, Email = email, TenantId = db.Tenants.First().Id,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Contacts.Add(c);
            db.SaveChanges();
            foreach (var e in entities)
                db.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = e, CreatedAt = now });
            db.SaveChanges();
            return c.Id;
        });

    /// <summary>A conversation as the WhatsApp history import left it: no contact.</summary>
    private int AddChat(string channel, string key, int? accountId, int entityId, int companyId, int? contactId = null) =>
        WithDb(db =>
        {
            var now = DateTime.UtcNow;
            var t = new Thread
            {
                Channel = channel, Key = key, Address = key, Name = key, AccountId = accountId,
                EntityId = entityId, CompanyEntityId = companyId, ContactId = contactId,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Threads.Add(t);
            db.SaveChanges();
            return t.Id;
        });

    private ThreadContactBackfill.Report Run(bool preview, int? entityId, string? channel = null) =>
        WithDb(db => new ThreadContactBackfill(db, new ScopingService(db)).Run(preview, entityId, channel));

    private int? LinkOf(int threadId) => WithDb(db => db.Threads.Find(threadId)!.ContactId);

    // ---- the one that matters -------------------------------------------------

    [Fact]
    public void A_contact_of_another_company_is_never_linked()
    {
        var w = Companies();
        var phone = Phone();
        // Desheng Logistic's customer — the same human, but Desheng's record
        var desheng = AddContact($"Desheng Customer {Sfx()}", phone, "", w.Desheng);
        // their conversation on PHYTOLINK's number, left unlinked by the history import
        var chat = AddChat(Channels.WhatsApp, Digits(phone), w.PhytoAccount, w.Phytolink, w.Phytolink);

        var refused = Run(preview: false, entityId: w.Phytolink);
        Assert.Equal(0, refused.Linked);
        Assert.Equal(1, refused.Refused);   // a contact holds the number — just not this company's
        Assert.Null(LinkOf(chat));          // the leak of incident 2026-10-11 does not happen

        // and the test is not vacuous: Phytolink's OWN contact on the same number links
        var mine = AddContact($"Phytolink Customer {Sfx()}", phone, "", w.Phytolink);
        var linked = Run(preview: false, entityId: w.Phytolink);
        Assert.Equal(1, linked.Linked);
        Assert.Equal(mine, LinkOf(chat));
        Assert.NotEqual(desheng, LinkOf(chat));
    }

    [Fact]
    public void A_branch_of_the_same_company_may_link()
    {
        var w = Companies();
        var phone = Phone();
        // a contact of the KL branch; the conversation arrived on head office's number
        var contact = AddContact($"KL Customer {Sfx()}", phone, "", w.PhytolinkKl);
        var chat = AddChat(Channels.WhatsApp, Digits(phone), w.PhytoAccount, w.Phytolink, w.Phytolink);
        Assert.Equal(1, Run(preview: false, entityId: w.Phytolink).Linked);
        Assert.Equal(contact, LinkOf(chat));
    }

    // ---- what it refuses to touch --------------------------------------------

    [Fact]
    public void Groups_broadcasts_and_opaque_channels_are_skipped()
    {
        var w = Companies();
        var phone = Phone();
        AddContact($"Group Member {Sfx()}", phone, "", w.Phytolink);
        var group = AddChat(Channels.WhatsApp, $"1203630000{Random.Shared.Next(100000, 999999)}@g.us",
            w.PhytoAccount, w.Phytolink, w.Phytolink);
        // a status / broadcast jid carries digits that would tail-match a stranger
        var broadcast = AddChat(Channels.WhatsApp, $"{Digits(phone)}@broadcast", w.PhytoAccount, w.Phytolink, w.Phytolink);
        var status = AddChat(Channels.WhatsApp, "status@broadcast", w.PhytoAccount, w.Phytolink, w.Phytolink);
        // a Messenger PSID is opaque: an 8-digit tail match would link a stranger
        var psid = AddChat(Channels.Facebook, Digits(phone), null, w.Phytolink, w.Phytolink);

        var report = Run(preview: false, entityId: w.Phytolink);
        Assert.Equal(0, report.Linked);
        Assert.Equal(4, report.Skipped);
        foreach (var id in new[] { group, broadcast, status, psid }) Assert.Null(LinkOf(id));

        Assert.False(ThreadContactBackfill.Eligible(Channels.WhatsApp, "60123456789@broadcast"));
        Assert.False(ThreadContactBackfill.Eligible(Channels.Telegram, "-1001234567890"));
        Assert.True(ThreadContactBackfill.Eligible(Channels.WhatsApp, "60123456789"));
        Assert.True(ThreadContactBackfill.Eligible(Channels.Sms, "60123456789"));
    }

    [Fact]
    public void A_conversation_that_already_has_a_contact_is_left_alone()
    {
        var w = Companies();
        var mine = AddContact($"Already Linked {Sfx()}", Phone(), "", w.Phytolink);
        var phone = Phone();
        var other = AddContact($"Same Number {Sfx()}", phone, "", w.Phytolink);
        var chat = AddChat(Channels.WhatsApp, Digits(phone), w.PhytoAccount, w.Phytolink, w.Phytolink, contactId: mine);
        var before = WithDb(db => db.Threads.Find(chat)!.UpdatedAt);

        var report = Run(preview: false, entityId: w.Phytolink);
        Assert.Equal(0, report.Linked);
        Assert.Equal(1, report.AlreadyLinked);
        Assert.Equal(mine, LinkOf(chat));                 // never moved to the matching contact
        Assert.NotEqual(other, LinkOf(chat));
        Assert.Equal(before, WithDb(db => db.Threads.Find(chat)!.UpdatedAt));
    }

    [Fact]
    public void A_number_nobody_holds_is_only_counted()
    {
        var w = Companies();
        var chat = AddChat(Channels.WhatsApp, Digits(Phone()), w.PhytoAccount, w.Phytolink, w.Phytolink);
        var report = Run(preview: false, entityId: w.Phytolink);
        Assert.Equal(0, report.Linked);
        Assert.Equal(1, report.NoMatch);
        Assert.Null(LinkOf(chat));
    }

    // ---- what it does --------------------------------------------------------

    [Fact]
    public void A_plus_prefixed_contact_links_to_a_bare_digits_conversation()
    {
        var w = Companies();
        var phone = Phone();                               // "+6019…", as a phone book holds it
        var contact = AddContact($"Tail Match {Sfx()}", phone, "", w.Phytolink);
        var chat = AddChat(Channels.WhatsApp, Digits(phone), w.PhytoAccount, w.Phytolink, w.Phytolink);
        Assert.StartsWith("+", phone);
        Assert.DoesNotContain("+", WithDb(db => db.Threads.Find(chat)!.Key));

        Assert.Equal(1, Run(preview: false, entityId: w.Phytolink).Linked);
        Assert.Equal(contact, LinkOf(chat));
    }

    [Fact]
    public void An_email_conversation_links_by_address()
    {
        var w = Companies();
        var mail = $"ana-{Sfx()}@example.com";
        var contact = AddContact($"Mail Customer {Sfx()}", "", mail, w.Phytolink);
        // the address as the mailbox stored it, in another case
        var chat = AddChat(Channels.Email, mail.ToUpperInvariant(), null, w.Phytolink, w.Phytolink);
        var report = Run(preview: false, entityId: w.Phytolink, channel: Channels.Email);
        Assert.Equal(1, report.Linked);
        Assert.Equal(contact, LinkOf(chat));
    }

    [Fact]
    public void A_linked_conversation_gets_a_fresh_updated_at()
    {
        var w = Companies();
        var phone = Phone();
        AddContact($"Stream Watcher {Sfx()}", phone, "", w.Phytolink);
        var chat = AddChat(Channels.WhatsApp, Digits(phone), w.PhytoAccount, w.Phytolink, w.Phytolink);
        // the SSE change token is thread.updated_at + the message count: a backfill
        // that leaves it alone freezes /chats until something else writes
        var stale = DateTime.UtcNow.AddDays(-30);
        WithDb(db =>
        {
            db.Threads.Find(chat)!.UpdatedAt = stale;
            db.SaveChanges();
        });

        Assert.Equal(1, Run(preview: false, entityId: w.Phytolink).Linked);
        Assert.True(WithDb(db => db.Threads.Find(chat)!.UpdatedAt) > stale.AddDays(1));
    }

    [Fact]
    public void Running_it_twice_changes_nothing_the_second_time()
    {
        var w = Companies();
        var phone = Phone();
        var contact = AddContact($"Idempotent {Sfx()}", phone, "", w.Phytolink);
        var chat = AddChat(Channels.WhatsApp, Digits(phone), w.PhytoAccount, w.Phytolink, w.Phytolink);

        Assert.Equal(1, Run(preview: false, entityId: w.Phytolink).Linked);
        var after = WithDb(db => db.Threads.Find(chat)!.UpdatedAt);

        var again = Run(preview: false, entityId: w.Phytolink);
        Assert.Equal(0, again.Linked);
        Assert.Equal(0, again.Scanned);              // nothing unlinked is left to examine
        Assert.Equal(1, again.AlreadyLinked);
        Assert.Equal(contact, LinkOf(chat));
        Assert.Equal(after, WithDb(db => db.Threads.Find(chat)!.UpdatedAt));
    }

    [Fact]
    public void A_preview_writes_nothing()
    {
        var w = Companies();
        var phone = Phone();
        var contact = AddContact($"Preview Only {Sfx()}", phone, "", w.Phytolink);
        var chat = AddChat(Channels.WhatsApp, Digits(phone), w.PhytoAccount, w.Phytolink, w.Phytolink);
        var before = WithDb(db => db.Threads.Find(chat)!.UpdatedAt);

        var preview = Run(preview: true, entityId: w.Phytolink);
        Assert.True(preview.Preview);
        Assert.Equal(1, preview.Linked);                       // what it WOULD link
        Assert.Contains(preview.Sample, s => s.ThreadId == chat && s.ContactId == contact);
        Assert.NotEmpty(preview.Sample[0].ContactName);
        Assert.Null(LinkOf(chat));                             // …and nothing was written
        Assert.Equal(before, WithDb(db => db.Threads.Find(chat)!.UpdatedAt));

        // the commit then links exactly what the preview promised
        Assert.Equal(1, Run(preview: false, entityId: w.Phytolink).Linked);
        Assert.Equal(contact, LinkOf(chat));
    }

    [Fact]
    public void One_entity_at_a_time()
    {
        var w = Companies();
        var phytoPhone = Phone();
        var deshengPhone = Phone();
        AddContact($"Phyto {Sfx()}", phytoPhone, "", w.Phytolink);
        AddContact($"Desheng {Sfx()}", deshengPhone, "", w.Desheng);
        var phytoChat = AddChat(Channels.WhatsApp, Digits(phytoPhone), w.PhytoAccount, w.Phytolink, w.Phytolink);
        var deshengChat = AddChat(Channels.WhatsApp, Digits(deshengPhone), w.DeshengAccount, w.Desheng, w.Desheng);

        Assert.Equal(1, Run(preview: false, entityId: w.Phytolink).Linked);
        Assert.NotNull(LinkOf(phytoChat));
        Assert.Null(LinkOf(deshengChat));                      // another company's run did not touch it

        Assert.Equal(1, Run(preview: false, entityId: w.Desheng).Linked);
        Assert.NotNull(LinkOf(deshengChat));
    }

    // ---- the endpoint --------------------------------------------------------

    [Fact]
    public async Task The_endpoint_previews_then_commits_and_is_audited()
    {
        var w = Companies();
        var phone = Phone();
        var contact = AddContact($"Endpoint {Sfx()}", phone, "", w.Phytolink);
        var chat = AddChat(Channels.WhatsApp, Digits(phone), w.PhytoAccount, w.Phytolink, w.Phytolink);
        var admin = await Platform.As(_f, "admin", "admin123");

        var dry = await admin.PostAsJsonAsync("/api/contacts/link-conversations",
            new { preview = true, entity = w.PhytolinkSlug });
        Assert.True(dry.IsSuccessStatusCode, await dry.Content.ReadAsStringAsync());
        var plan = await dry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(plan.GetProperty("preview").GetBoolean());
        Assert.Equal(1, plan.GetProperty("linked").GetInt32());
        Assert.False(plan.GetProperty("more").GetBoolean());
        Assert.Equal(contact, plan.GetProperty("sample")[0].GetProperty("contact_id").GetInt32());
        Assert.Null(LinkOf(chat));

        var commit = await admin.PostAsJsonAsync("/api/contacts/link-conversations",
            new { entity = w.PhytolinkSlug });
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        var done = await commit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(done.GetProperty("preview").GetBoolean());
        Assert.Equal(1, done.GetProperty("linked").GetInt32());
        Assert.Equal(contact, LinkOf(chat));

        Assert.True(WithDb(db => db.AuditLogs.Any(a => a.Action == "contacts.link_conversations"
                                                       && a.Target.Contains(w.PhytolinkSlug))));

        // an unknown entity is a 400, one outside the caller's scope a 403 — never a silent run over everything
        var bad = await admin.PostAsJsonAsync("/api/contacts/link-conversations", new { entity = "no-such-entity" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
