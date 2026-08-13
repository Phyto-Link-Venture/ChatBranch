using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Parity port of backend/tests/test_entity_scoping.py — the same cases that
/// guard the "entity override wins, global is the fallback" rule, now asserted
/// against the C# <see cref="ScopingService"/>.
/// </summary>
public class ScopingServiceTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly int _tenantId;
    private readonly int _rimbayu;
    private readonly int _botanic;
    private readonly int _sibling;

    public ScopingServiceTests()
    {
        var tenant = new Tenant { Name = "Org", Slug = "org", Status = "active" };
        _t.Db.Tenants.Add(tenant);
        _t.Db.SaveChanges();
        _tenantId = tenant.Id;

        _rimbayu = AddEntity("rimbayu");
        _botanic = AddEntity("botanic");
        _sibling = AddEntity("sibling");
    }

    private int AddEntity(string slug)
    {
        var e = new Entity { TenantId = _tenantId, Name = slug, Slug = slug };
        _t.Db.Entities.Add(e);
        _t.Db.SaveChanges();
        return e.Id;
    }

    private ScopingService Svc() => new(_t.Db);

    [Fact]
    public void Entity_override_wins_and_global_is_the_fallback()
    {
        var s = Svc();
        s.SetSetting("booking_hours", "{\"mon\": [9, 18]}");            // global
        s.SetSetting("booking_hours", "{\"mon\": [11, 22]}", _rimbayu); // rimbayu override

        Assert.Equal("{\"mon\": [9, 18]}", s.SettingValue("booking_hours"));
        Assert.Equal("{\"mon\": [11, 22]}", s.SettingValue("booking_hours", _rimbayu));
        // botanic has no override → inherits global
        Assert.Equal("{\"mon\": [9, 18]}", s.SettingValue("booking_hours", _botanic));
    }

    [Fact]
    public void Clearing_an_override_reverts_to_inherited()
    {
        var s = Svc();
        s.SetSetting("booking_hours", "{\"mon\": [9, 18]}");
        s.SetSetting("booking_hours", "{\"mon\": [11, 22]}", _rimbayu);
        Assert.Equal("entity", s.SettingScope("booking_hours", _rimbayu));

        Assert.True(s.ClearSetting("booking_hours", _rimbayu));
        Assert.Equal("{\"mon\": [9, 18]}", s.SettingValue("booking_hours", _rimbayu));
        Assert.Equal("global", s.SettingScope("booking_hours", _rimbayu));
    }

    [Fact]
    public void Blank_override_falls_through_rather_than_blanking()
    {
        var s = Svc();
        s.SetSetting("quiet_start", "22:00");        // global
        s.SetSetting("quiet_start", "", _rimbayu);   // blank override = "no opinion"
        Assert.Equal("22:00", s.SettingValue("quiet_start", _rimbayu));
    }

    [Fact]
    public void Organization_wide_keys_refuse_a_per_entity_override()
    {
        var s = Svc();
        Assert.False(ScopingService.IsEntityScoped("openai_api_key"));
        Assert.Throws<InvalidOperationException>(() =>
            s.SetSetting("openai_api_key", "sk-x", _rimbayu));
    }

    [Fact]
    public void Global_row_stays_unique_per_key()
    {
        var s = Svc();
        s.SetSetting("booking_hours", "{\"mon\": [9, 18]}");
        // A second GLOBAL row for the same key must be rejected by the partial
        // unique index (uniq_setting_key_global).
        using var raw = _t.Fresh();
        raw.Settings.Add(new Setting { Key = "booking_hours", EntityId = null, Value = "dup" });
        Assert.Throws<DbUpdateException>(() => raw.SaveChanges());
    }

    [Fact]
    public void Bulk_read_matches_single_read()
    {
        var s = Svc();
        s.SetSetting("quiet_start", "22:00");
        s.SetSetting("quiet_end", "08:00");
        s.SetSetting("quiet_start", "23:00", _rimbayu);

        var bulk = s.SettingValues(new[] { "quiet_start", "quiet_end" }, _rimbayu);
        Assert.Equal("23:00", bulk["quiet_start"]);
        Assert.Equal("08:00", bulk["quiet_end"]);
        Assert.Equal(s.SettingValue("quiet_start", _rimbayu), bulk["quiet_start"]);
        Assert.Equal(s.SettingValue("quiet_end", _rimbayu), bulk["quiet_end"]);
    }

    [Fact]
    public void Inherited_returns_global_plus_own_but_not_a_sibling()
    {
        _t.Db.CannedResponses.Add(new CannedResponse { ShortCode = "hello", Content = "hi", EntityId = null });
        _t.Db.CannedResponses.Add(new CannedResponse { ShortCode = "rim", Content = "r", EntityId = _rimbayu });
        _t.Db.CannedResponses.Add(new CannedResponse { ShortCode = "sib", Content = "s", EntityId = _sibling });
        _t.Db.SaveChanges();

        var codes = Svc().ScopeInherited(_t.Db.CannedResponses, _rimbayu)
            .Select(c => c.ShortCode).ToHashSet();
        Assert.Equal(new HashSet<string> { "hello", "rim" }, codes); // sibling must not leak
    }

    [Fact]
    public void Owned_returns_only_that_entity_and_unassigned_is_opt_in()
    {
        _t.Db.Threads.Add(new Thread { Channel = "whatsapp", Key = "a", EntityId = _rimbayu });
        _t.Db.Threads.Add(new Thread { Channel = "whatsapp", Key = "b", EntityId = _sibling });
        _t.Db.Threads.Add(new Thread { Channel = "whatsapp", Key = "c", EntityId = null });
        _t.Db.SaveChanges();

        var owned = Svc().ScopeOwned(_t.Db.Threads, _rimbayu).Select(x => x.Key).ToHashSet();
        Assert.Equal(new HashSet<string> { "a" }, owned);

        var withUnassigned = Svc().ScopeOwned(_t.Db.Threads, _rimbayu, includeUnassigned: true)
            .Select(x => x.Key).ToHashSet();
        Assert.Equal(new HashSet<string> { "a", "c" }, withUnassigned);
    }

    [Fact]
    public void None_entity_means_all_entities_and_does_not_narrow()
    {
        _t.Db.Threads.Add(new Thread { Channel = "whatsapp", Key = "a", EntityId = _rimbayu });
        _t.Db.Threads.Add(new Thread { Channel = "whatsapp", Key = "b", EntityId = _sibling });
        _t.Db.SaveChanges();
        Assert.Equal(2, Svc().ScopeOwned(_t.Db.Threads, null).Count());
    }

    [Fact]
    public void Receiving_account_decides_thread_entity()
    {
        var acct = new ChannelAccount { Channel = "whatsapp", Name = "rim number", EntityId = _rimbayu };
        var contact = new Contact { Name = "X", Phone = "+60123" };
        Assert.Equal(_rimbayu, Svc().ThreadEntity(acct, contact));
    }

    [Fact]
    public void Falls_back_to_the_contact_on_a_shared_number()
    {
        var shared = new ChannelAccount { Channel = "whatsapp", Name = "shared", EntityId = null };
        var contact = new Contact { Name = "X", Phone = "+60123" };
        _t.Db.Contacts.Add(contact);
        _t.Db.SaveChanges();
        _t.Db.ContactEntities.Add(new ContactEntity { ContactId = contact.Id, EntityId = _rimbayu });
        _t.Db.SaveChanges();

        Assert.Equal(_rimbayu, Svc().ThreadEntity(shared, contact));
    }

    [Fact]
    public void Multi_entity_contact_on_a_shared_number_stays_unassigned()
    {
        var shared = new ChannelAccount { Channel = "whatsapp", Name = "shared", EntityId = null };
        var contact = new Contact { Name = "X", Phone = "+60123" };
        _t.Db.Contacts.Add(contact);
        _t.Db.SaveChanges();
        _t.Db.ContactEntities.Add(new ContactEntity { ContactId = contact.Id, EntityId = _rimbayu });
        _t.Db.ContactEntities.Add(new ContactEntity { ContactId = contact.Id, EntityId = _sibling });
        _t.Db.SaveChanges();

        Assert.Null(Svc().ThreadEntity(shared, contact));
    }

    [Fact]
    public void Unknown_contact_on_a_shared_number_is_unassigned()
    {
        var shared = new ChannelAccount { Channel = "whatsapp", Name = "shared", EntityId = null };
        Assert.Null(Svc().ThreadEntity(shared, null));
    }

    public void Dispose() => _t.Dispose();
}
