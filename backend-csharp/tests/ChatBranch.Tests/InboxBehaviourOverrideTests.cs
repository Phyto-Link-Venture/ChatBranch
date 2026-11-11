using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Per-inbox AI behaviour overrides: the level BELOW the entity, stored in
/// <see cref="ChannelAccount.Config"/> and read first by
/// <see cref="AiBehaviourSettings.Resolve"/>. This is what lets a website
/// widget answer in seconds while WhatsApp keeps a long "wait for the customer
/// to finish" debounce, without splitting the organization's settings.
/// </summary>
public class InboxBehaviourOverrideTests
{
    private static ChannelAccount Widget(params (string Key, string Value)[] overrides)
    {
        var cfg = new Dictionary<string, object?>();
        foreach (var (k, v) in overrides) cfg[k] = v;
        return new ChannelAccount
        {
            Id = 13, Channel = "website", Provider = "widget", Name = "Website chat", Config = cfg,
        };
    }

    [Fact]
    public void Inbox_override_wins_over_the_organization()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        sc.SetSetting(AiBehaviourSettings.WaitSeconds, "25");
        sc.SetSetting(AiBehaviourSettings.Typing, "true");

        // no inbox: the organization's 25-second debounce
        Assert.Equal(25, AiBehaviourSettings.Resolve(sc, null).WaitSeconds);

        // this inbox answers fast; the organization is untouched
        var widget = Widget((AiBehaviourSettings.WaitSeconds, "5"));
        var mine = AiBehaviourSettings.Resolve(sc, null, widget);
        Assert.Equal(5, mine.WaitSeconds);
        Assert.True(mine.TypingEnabled);                                  // still inherited
        Assert.Equal(25, AiBehaviourSettings.Resolve(sc, null).WaitSeconds);
    }

    [Fact]
    public void A_blank_override_inherits_again()
    {
        using var t = new TestDb();
        var sc = new ScopingService(t.Db);
        sc.SetSetting(AiBehaviourSettings.WaitSeconds, "25");
        var widget = Widget((AiBehaviourSettings.WaitSeconds, "5"));
        Assert.Equal(5, AiBehaviourSettings.Resolve(sc, null, widget).WaitSeconds);

        Assert.True(InboxSettings.SetBehaviour(widget, AiBehaviourSettings.WaitSeconds, ""));
        Assert.Equal("", InboxSettings.BehaviourOwn(widget, AiBehaviourSettings.WaitSeconds));
        Assert.Equal(25, AiBehaviourSettings.Resolve(sc, null, widget).WaitSeconds);
    }

    [Fact]
    public void SetBehaviour_reports_real_changes_only_and_ignores_unknown_keys()
    {
        var widget = Widget();
        Assert.True(InboxSettings.SetBehaviour(widget, AiBehaviourSettings.WaitSeconds, "5"));
        Assert.False(InboxSettings.SetBehaviour(widget, AiBehaviourSettings.WaitSeconds, "5"));   // no-op
        Assert.False(InboxSettings.SetBehaviour(widget, AiBehaviourSettings.WaitSeconds, " 5 ")); // trimmed
        Assert.True(InboxSettings.SetBehaviour(widget, AiBehaviourSettings.WaitSeconds, "8"));

        Assert.False(InboxSettings.SetBehaviour(widget, "not_a_setting", "1"));
        Assert.False(widget.Config.ContainsKey("not_a_setting"));

        Assert.False(InboxSettings.SetBehaviour(widget, AiBehaviourSettings.Typing, ""));         // nothing to clear
    }

    [Fact]
    public void BehaviourOverrides_lists_only_this_inboxs_own_keys()
    {
        var widget = Widget(
            (AiBehaviourSettings.WaitSeconds, "5"),
            (AiBehaviourSettings.TypingMax, "4"));
        widget.Config["role"] = "support";          // a non-behaviour config key
        widget.Config["team_id"] = "7";

        var own = InboxSettings.BehaviourOverrides(widget);
        Assert.Equal(2, own.Count);
        Assert.Equal("5", own[AiBehaviourSettings.WaitSeconds]);
        Assert.Equal("4", own[AiBehaviourSettings.TypingMax]);
        Assert.DoesNotContain("role", own.Keys);
        Assert.DoesNotContain("team_id", own.Keys);
    }

    [Fact]
    public void An_inbox_overrides_the_entity_not_just_the_organization()
    {
        using var t = new TestDb();
        var tenant = new Tenant { Name = "T", Slug = "t" };
        t.Db.Tenants.Add(tenant);
        t.Db.SaveChanges();
        var entity = new Entity { TenantId = tenant.Id, Name = "Phytolink", Slug = "phytolink" };
        t.Db.Entities.Add(entity);
        t.Db.SaveChanges();

        var sc = new ScopingService(t.Db);
        sc.SetSetting(AiBehaviourSettings.WaitSeconds, "25");                 // organization
        sc.SetSetting(AiBehaviourSettings.WaitSeconds, "15", entity.Id);      // outlet
        Assert.Equal(15, AiBehaviourSettings.Resolve(sc, entity.Id).WaitSeconds);

        var widget = Widget((AiBehaviourSettings.WaitSeconds, "5"));
        Assert.Equal(5, AiBehaviourSettings.Resolve(sc, entity.Id, widget).WaitSeconds);
    }
}
