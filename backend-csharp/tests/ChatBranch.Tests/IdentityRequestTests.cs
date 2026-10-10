using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Part C — "ask who they are": the deterministic parser (EN + BM) and the
/// per-conversation state machine in the ONE inbound path — match → link (the
/// AI then answers), ambiguous → handover, none → create / pending, max two
/// asks, refusals, opt-outs never asked, BM greeting, queued asks on live
/// numbers, the ask_identity automation + contact_linked condition, the
/// per-inbox setting and the chat's "Create contact" quick action.
/// </summary>
public class IdentityRequestTests : IdentityApiBase, IClassFixture<IdentityFactory>
{
    public IdentityRequestTests(IdentityFactory factory) : base(factory) { }

    // ---- parser ------------------------------------------------------------------

    [Theory]
    [InlineData("my name is Ali bin Abu, 012-345 6789", "Ali bin Abu", "+60123456789", "")]
    [InlineData("Saya Aminah, aminah@example.com", "Aminah", "", "aminah@example.com")]
    [InlineData("nama saya siti aishah 0198765432", "Siti Aishah", "+60198765432", "")]
    [InlineData("I'm Mei Ling, mei@x.co", "Mei Ling", "", "mei@x.co")]
    [InlineData("John Tan", "John Tan", "", "")]
    [InlineData("+60 12 999 8888 — Raj", "Raj", "+60129998888", "")]
    [InlineData("what are your opening hours?", "", "", "")]
    [InlineData("how much is the delivery", "", "", "")]
    public void The_parser_reads_names_phones_and_emails(string text, string name, string phone, string email)
    {
        var d = IdentityParser.Parse(text, "60");
        Assert.Equal(name, d.Name);
        Assert.Equal(phone, d.Phone);
        Assert.Equal(email, d.Email);
        Assert.False(d.Refused);
    }

    [Theory]
    [InlineData("no thanks")]
    [InlineData("I'd rather not")]
    [InlineData("tak nak")]
    [InlineData("No")]
    public void Refusals_are_recognised(string text) => Assert.True(IdentityParser.Parse(text, "60").Refused);

    [Fact]
    public void The_language_follows_Malay_keywords()
    {
        Assert.Equal("ms", IdentityParser.Language("Hai, boleh saya tahu harga?"));
        Assert.Equal("en", IdentityParser.Language("Hello, what's the price?"));
    }

    // ---- helpers -------------------------------------------------------------------

    private int Inbox(string mode, string channel = "telegram", int? entityId = null, string provider = "cloud",
        string token = "", string externalId = "", string message = "") =>
        AddAccount(channel, $"{channel} {mode} {Guid.NewGuid():N}"[..24], provider: provider, token: token,
            externalId: externalId, entityId: entityId,
            config: new Dictionary<string, object?>
            {
                [IdentifySettings.ModeKey] = mode,
                [IdentifySettings.MessageKey] = message,
            }.Where(kv => kv.Value is string s && s.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value));

    private void Say(string channel, string key, string text, int accountId) =>
        InScope((sp, _) => sp.GetRequiredService<InboundProcessor>()
            .ProcessInbound(channel, key, text, name: "Visitor", accountId: accountId));

    private ChatBranch.Domain.Thread ThreadOf(string channel, string key) =>
        InScope((_, db) => db.Threads.AsNoTracking().Single(t => t.Channel == channel && t.Key == key));

    private List<Message> Messages(string channel, string key) =>
        InScope((_, db) => db.Messages.AsNoTracking().Where(m => m.Thread!.Channel == channel && m.Thread.Key == key)
            .OrderBy(m => m.Id).ToList());

    private static List<Message> Asks(List<Message> msgs) =>
        msgs.Where(m => m.Source == MessageSource.System && !m.Private && m.Direction == "out").ToList();

    private IdentityRequest Request(string channel, string key) =>
        InScope((_, db) => db.IdentityRequests.AsNoTracking()
            .Single(r => r.Thread!.Channel == channel && r.Thread.Key == key));

    private static string Key() => "TG-id-" + Guid.NewGuid().ToString("N")[..10];

    // ---- the state machine ---------------------------------------------------------

    [Fact]
    public async Task A_match_links_the_chat_and_the_AI_then_answers_once()
    {
        var admin = await Authed();
        var phone = NewNumber();
        var kimId = await CreateContact(admin, "Known Kim", "+" + phone);
        var inbox = Inbox(IdentifyModes.Ask);
        var key = Key();

        Say("telegram", key, "Hi, do you deliver to Penang?", inbox);
        var asked = Messages("telegram", key);
        var ask = Asks(asked).Single();
        Assert.StartsWith("Hi! Thanks for getting in touch", ask.Text);
        Assert.DoesNotContain(asked, m => m.Source == MessageSource.Ai);   // unknown: the AI waits
        Assert.Equal(IdentityStates.Asking, Request("telegram", key).State);

        Say("telegram", key, "I'm Kim, 0" + phone[2..], inbox);
        var thread = ThreadOf("telegram", key);
        Assert.Equal(kimId, thread.ContactId);
        var req = Request("telegram", key);
        Assert.Equal(IdentityStates.Linked, req.State);
        Assert.Equal(kimId, req.ContactId);
        var msgs = Messages("telegram", key);
        Assert.Contains(msgs, m => m.Private && m.Text.Contains("identified as Known Kim"));
        Assert.Single(msgs, m => m.Source == MessageSource.Ai);            // answered once, now that it's linked
        Assert.True(InScope((_, db) => db.AuditLogs.Any(a => a.Action == "contact.self_identified"
                                                             && a.Target == "telegram:" + key)));

        // later messages: no more asks
        Say("telegram", key, "thanks!", inbox);
        Assert.Single(Asks(Messages("telegram", key)));
    }

    [Fact]
    public async Task A_first_message_that_already_carries_a_number_resolves_without_asking()
    {
        var admin = await Authed();
        var phone = NewNumber();
        var id = await CreateContact(admin, "Direct Dee", "+" + phone);
        var inbox = Inbox(IdentifyModes.Ask);
        var key = Key();
        Say("telegram", key, $"Hello, this is Dee ({phone}) — is my order ready?", inbox);
        Assert.Equal(id, ThreadOf("telegram", key).ContactId);
        Assert.Empty(Asks(Messages("telegram", key)));
    }

    [Fact]
    public async Task Several_matches_hand_the_chat_over_with_the_candidates_listed()
    {
        var admin = await Authed();
        var n = NewNumber();
        await CreateContact(admin, "Twin One", "+" + n);
        // same last 8 digits — a twin the API now refuses (one per company), so it
        // is planted directly, the way older data still holds such pairs
        InScope((_, db) =>
        {
            var tid = db.Contacts.Single(c => c.Name == "Twin One").TenantId;
            db.Contacts.Add(new Contact { Name = "Twin Two", Phone = "+65" + n[3..], TenantId = tid,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        var inbox = Inbox(IdentifyModes.AskAndCreate);
        var key = Key();
        Say("telegram", key, "hello", inbox);
        Say("telegram", key, "it's me, +" + n, inbox);

        var thread = ThreadOf("telegram", key);
        Assert.Null(thread.ContactId);
        Assert.Equal(ThreadStatus.Pending, thread.Status);
        Assert.Equal(IdentityStates.Ambiguous, Request("telegram", key).State);
        // a tracked handover: it shows in the inbox's "Needs human" view
        Assert.Equal(ChatBranch.Data.Ai.HandoverKinds.UnknownCustomer, thread.HandoverKind);
        Assert.NotNull(thread.HandoverAt);
        Assert.Null(thread.HandoverResolvedAt);
        var note = Messages("telegram", key).Last(m => m.Private).Text;
        Assert.Contains("Twin One", note);
        Assert.Contains("Twin Two", note);
        Assert.True(InScope((_, db) => db.Notifications.Any(x => x.Title.Contains("several contacts"))));

        // a person picks the right one with the chat's ordinary link picker: the
        // request closes and the "unknown customer" handover clears
        var twinOne = InScope((_, db) => db.Contacts.Single(c => c.Name == "Twin One").Id);
        (await admin.PostAsJsonAsync($"/api/messaging/threads/telegram/{key}/link", new { contact_id = twinOne }))
            .EnsureSuccessStatusCode();
        var linked = ThreadOf("telegram", key);
        Assert.Equal(twinOne, linked.ContactId);
        Assert.NotNull(linked.HandoverResolvedAt);
        Assert.Equal(IdentityStates.Linked, Request("telegram", key).State);
    }

    [Fact]
    public void No_match_with_ask_and_create_creates_the_contact_in_the_inbox_entity()
    {
        var mainId = InScope((_, db) => db.Entities.Single(e => e.Slug == "main").Id);
        var inbox = Inbox(IdentifyModes.AskAndCreate, entityId: mainId);
        var key = Key();
        var phone = NewNumber();
        Say("telegram", key, "Selamat pagi, ada stok?", inbox);
        Assert.StartsWith("Hai! Terima kasih", Asks(Messages("telegram", key)).Single().Text);   // BM greeting

        Say("telegram", key, $"nama saya nur hidayah, 0{phone[2..]}", inbox);
        var thread = ThreadOf("telegram", key);
        Assert.NotNull(thread.ContactId);
        Assert.Equal(mainId, thread.EntityId);
        var contact = InScope((_, db) => db.Contacts.AsNoTracking().Single(c => c.Id == thread.ContactId));
        Assert.Equal("Nur Hidayah", contact.Name);
        Assert.Equal("+" + phone, contact.Phone);
        Assert.Equal("ms", contact.Language);
        Assert.Contains(IdentityRequestService.SelfIdentifiedTag, contact.Tags);
        Assert.True(InScope((_, db) => db.ContactEntities.Any(l => l.ContactId == contact.Id && l.EntityId == mainId)));
        Assert.Equal(IdentityStates.Created, Request("telegram", key).State);
        // the new contact's own identities were registered too
        Assert.True(InScope((_, db) => db.ContactIdentities.Any(i => i.ContactId == contact.Id && i.Channel == "whatsapp")));
    }

    [Fact]
    public async Task No_match_with_ask_leaves_a_note_and_the_quick_action_creates_and_links()
    {
        var admin = await Authed();
        var inbox = Inbox(IdentifyModes.Ask);
        var key = Key();
        Say("telegram", key, "Hi! What time do you open tomorrow?", inbox);
        Say("telegram", key, "Farah, farah@example.org", inbox);

        var thread = ThreadOf("telegram", key);
        Assert.Null(thread.ContactId);
        Assert.Equal(ThreadStatus.Pending, thread.Status);
        Assert.Equal(IdentityStates.NeedsContact, Request("telegram", key).State);
        Assert.Contains(Messages("telegram", key), m => m.Private && m.Text.Contains("farah@example.org"));
        Assert.Equal(ChatBranch.Data.Ai.HandoverKinds.UnknownCustomer, thread.HandoverKind);
        Assert.Null(thread.HandoverResolvedAt);
        var needsHuman = await admin.GetFromJsonAsync<JsonElement>(
            "/api/inbox/threads?handover=open&handover_kind=unknown_customer&count_only=1");
        Assert.True(needsHuman.GetProperty("count").GetInt32() >= 1);

        // the chat banner's state + prefill
        var state = await admin.GetFromJsonAsync<JsonElement>($"/api/contacts/identify/telegram/{key}");
        Assert.Equal("needs_contact", state.GetProperty("request").GetProperty("state").GetString());
        Assert.Equal("Farah", state.GetProperty("suggested").GetProperty("name").GetString());
        Assert.Equal("farah@example.org", state.GetProperty("suggested").GetProperty("email").GetString());
        Assert.True(state.GetProperty("can_create").GetBoolean());

        Assert.DoesNotContain(Messages("telegram", key), m => m.Source == MessageSource.Ai);
        var res = await admin.PostAsJsonAsync($"/api/contacts/identify/telegram/{key}/contact",
            new { name = "Farah Aziz", email = "farah@example.org", phone = "012 777 8888" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var newId = body.GetProperty("contact_id").GetInt32();
        Assert.Equal("+60127778888", body.GetProperty("contact").GetProperty("phone").GetString());
        Assert.Equal(newId, ThreadOf("telegram", key).ContactId);
        Assert.Equal(IdentityStates.Created, Request("telegram", key).State);
        Assert.Single(Messages("telegram", key), m => m.Source == MessageSource.Ai);   // the waiting customer is answered
        Assert.NotNull(ThreadOf("telegram", key).HandoverResolvedAt);                  // the handover is closed

        // already linked now → 409; agents can read the state but not create
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(
            $"/api/contacts/identify/telegram/{key}/contact", new { name = "Again" })).StatusCode);
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsJsonAsync(
            $"/api/contacts/identify/telegram/{key}/contact", new { name = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task Two_asks_at_most_then_the_chat_is_left_for_a_person()
    {
        await Authed();
        var inbox = Inbox(IdentifyModes.AskAndCreate);
        var key = Key();
        Say("telegram", key, "hello", inbox);
        Say("telegram", key, "what?", inbox);
        var asks = Asks(Messages("telegram", key));
        Assert.Equal(2, asks.Count);
        Assert.StartsWith("Sorry, I didn't quite catch that", asks[1].Text);

        Say("telegram", key, "how much is it?", inbox);
        Assert.Equal(IdentityStates.GaveUp, Request("telegram", key).State);
        Assert.Equal(ThreadStatus.Pending, ThreadOf("telegram", key).Status);
        Say("telegram", key, "hello??", inbox);
        Assert.Equal(2, Asks(Messages("telegram", key)).Count);
        Assert.Null(ThreadOf("telegram", key).ContactId);
    }

    [Fact]
    public async Task A_refusal_stops_asking_and_a_name_alone_asks_for_a_number()
    {
        await Authed();
        var inbox = Inbox(IdentifyModes.Ask);
        var key = Key();
        Say("telegram", key, "hey", inbox);
        Say("telegram", key, "no thanks", inbox);
        Assert.Equal(IdentityStates.GaveUp, Request("telegram", key).State);
        Assert.Single(Asks(Messages("telegram", key)));

        var key2 = Key();
        Say("telegram", key2, "hey", inbox);
        Say("telegram", key2, "Daniel", inbox);
        var asks = Asks(Messages("telegram", key2));
        Assert.Equal("Thanks, Daniel! Could you also share your phone number or email address?", asks[1].Text);
    }

    [Fact]
    public void A_customer_who_never_answers_is_left_for_a_person_after_a_day()
    {
        using var t = new TestDb();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var sc = new ChatBranch.Data.ScopingService(t.Db);
        var threads = new ChatBranch.Data.Stores.ThreadStore(t.Db, sc);
        var thread = threads.GetOrCreateThread("telegram", "TG-silent");
        var msg = threads.AppendMessage(thread, MessageDirection.In, MessageSource.Customer, "hello", status: "received");
        var svc = new IdentityRequestService(t.Db, sc, threads, sender: null, clock);
        Assert.True(IdentityRequestService.Arm(t.Db, thread, "ask"));
        t.Db.SaveChanges();
        Assert.Equal(IdentityStates.Asking, svc.OnInbound(thread, msg)!.State);   // queued ask (no sender)

        clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, svc.SweepStale());
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, svc.SweepStale());
        var req = t.Db.IdentityRequests.Single();
        Assert.Equal(IdentityStates.GaveUp, req.State);
        Assert.Contains("within 24 hours", req.Note);
        Assert.Equal(ThreadStatus.Pending, t.Db.Threads.Single().Status);
        // the queued ask that never went out is cancelled
        Assert.Equal(JobStatus.Cancelled, t.Db.OutboundJobs.Single().Status);
    }

    [Fact]
    public async Task On_WhatsApp_a_name_is_enough_because_the_number_is_known()
    {
        await Authed();
        var inbox = Inbox(IdentifyModes.AskAndCreate, channel: "whatsapp");
        var key = NewNumber();
        Say("whatsapp", key, "hi there", inbox);
        Assert.Contains("registered with us", Asks(Messages("whatsapp", key)).Single().Text);
        Say("whatsapp", key, "Wong Wei", inbox);
        var thread = ThreadOf("whatsapp", key);
        var contact = InScope((_, db) => db.Contacts.AsNoTracking().Single(c => c.Id == thread.ContactId));
        Assert.Equal("Wong Wei", contact.Name);
        Assert.Equal("+" + key, contact.Phone);
        // an org-wide inbox in a single-tenant install: the contact still belongs to the tenant
        Assert.NotNull(contact.TenantId);
    }

    [Fact]
    public async Task Opted_out_chats_are_never_asked_and_existing_inboxes_stay_off()
    {
        await Authed();
        var inbox = Inbox(IdentifyModes.Ask);
        var key = Key();
        Say("telegram", key, "STOP", inbox);
        Assert.Empty(Asks(Messages("telegram", key)));
        Assert.False(InScope((_, db) => db.IdentityRequests.Any(r => r.Thread!.Key == key)));

        // an inbox without the setting (every existing install) never asks
        var plain = AddAccount("telegram", "Plain bot");
        var k2 = Key();
        Say("telegram", k2, "hello", plain);
        Assert.Empty(Asks(Messages("telegram", k2)));
    }

    [Fact]
    public async Task On_a_live_number_the_ask_is_queued_and_cancelled_if_they_answer_first()
    {
        var admin = await Authed();
        var phone = NewNumber();
        var id = await CreateContact(admin, "Queued Quinn", "+" + phone);
        var inbox = Inbox(IdentifyModes.Ask, channel: "whatsapp", token: "live-token", externalId: "PNID-" + phone,
            message: "Hello from the shop! Who are we chatting with?");
        var key = NewNumber();
        Say("whatsapp", key, "hello", inbox);
        Assert.Empty(Asks(Messages("whatsapp", key)));          // nothing inline on a live number
        var job = InScope((_, db) => db.OutboundJobs.AsNoTracking().Single(j => j.ToAddress == key));
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal("system", job.Payload["source"]?.ToString());
        Assert.Equal("Hello from the shop! Who are we chatting with?", job.Payload["text"]?.ToString());

        Say("whatsapp", key, $"Quinn here, my number is +{phone}", inbox);
        Assert.Equal(id, ThreadOf("whatsapp", key).ContactId);
        Assert.Equal(JobStatus.Cancelled,
            InScope((_, db) => db.OutboundJobs.AsNoTracking().Single(j => j.Id == job.Id).Status));
    }

    [Fact]
    public async Task The_ask_identity_automation_arms_the_exchange_for_unlinked_chats()
    {
        var admin = await Authed();
        var plain = AddAccount("line", "LINE OA");                    // inbox setting off
        var rule = await admin.PostAsJsonAsync("/api/automations", new
        {
            name = "Ask unknown LINE customers",
            @event = "conversation_created",
            conditions = new object[]
            {
                new { field = "channel", op = "equals", value = "line" },
                new { field = "contact_linked", op = "equals", value = "no" },
            },
            actions = new object[] { new { type = "ask_identity", value = "ask" } },
        });
        Assert.Equal(HttpStatusCode.Created, rule.StatusCode);
        var ruleId = (await rule.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        try
        {
            var key = "U" + Guid.NewGuid().ToString("N")[..12];
            Say("line", key, "hello", plain);
            Assert.Single(Asks(Messages("line", key)));
            Assert.Equal(IdentityStates.Asking, Request("line", key).State);

            // a linked conversation does not match contact_linked = no
            var phone = NewNumber();
            var linkedId = await CreateContact(admin, "Linked Lee", "+" + phone);
            var code = (await (await admin.PostAsJsonAsync($"/api/contacts/{linkedId}/invites", new { channel = "line" }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
            var k2 = "U" + Guid.NewGuid().ToString("N")[..12];
            Say("line", k2, $"[ref:{code}] hi", plain);
            Assert.Equal(linkedId, ThreadOf("line", k2).ContactId);
            Assert.Empty(Asks(Messages("line", k2)));
        }
        finally
        {
            await admin.DeleteAsync($"/api/automations/{ruleId}");
        }

        var catalog = await admin.GetFromJsonAsync<JsonElement>("/api/automations/catalog");
        Assert.Contains(catalog.GetProperty("actions").EnumerateArray(), a => a.GetProperty("id").GetString() == "ask_identity");
        Assert.Contains(catalog.GetProperty("fields").EnumerateArray(), f => f.GetProperty("id").GetString() == "contact_linked");
    }

    [Fact]
    public async Task The_per_inbox_setting_reads_and_writes_through_its_own_endpoint()
    {
        var admin = await Authed();
        var id = AddAccount("telegram", "Setting bot");
        var got = await admin.GetFromJsonAsync<JsonElement>($"/api/inboxes/{id}/identify");
        Assert.Equal("", got.GetProperty("identify_unknown").GetString());
        Assert.Equal("off", got.GetProperty("effective").GetString());
        Assert.StartsWith("Hai!", got.GetProperty("default_messages").GetProperty("ms").GetString());

        var put = await admin.PutAsJsonAsync($"/api/inboxes/{id}/identify",
            new { identify_unknown = "ask_and_create", identify_message = "Who's this?" });
        put.EnsureSuccessStatusCode();
        var after = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ask_and_create", after.GetProperty("effective").GetString());
        Assert.Equal("Who's this?", after.GetProperty("identify_message").GetString());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PutAsJsonAsync($"/api/inboxes/{id}/identify", new { identify_unknown = "sometimes" })).StatusCode);
        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await agent.PutAsJsonAsync($"/api/inboxes/{id}/identify", new { identify_unknown = "off" })).StatusCode);
    }
}
