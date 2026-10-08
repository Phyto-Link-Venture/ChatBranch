using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Rules settled when the transport and routing branches met: only phone-keyed
/// identities are phone-matched, and a conversation number going down raises
/// ONE admin alert (the recovery one), not the generic health bell as well.
/// </summary>
public class MergeFollowUpTests
{
    [Theory]
    [InlineData(ChatBranch.Domain.Channels.Telegram, "5512345678")]
    [InlineData(ChatBranch.Domain.Channels.Facebook, "7712345678")]
    [InlineData(ChatBranch.Domain.Channels.Instagram, "8812345678")]
    [InlineData(ChatBranch.Domain.Channels.Website, "a1b2c3d4e5f6a7b8c12345678")]
    public void Opaque_ids_are_never_phone_matched(string channel, string key)
    {
        using var r = new AiRig();
        // shares the last 8 digits with every key above
        r.Contact("Stranger", "+60 12-345 678");
        r.Contact("Tail", "6012345678");
        Assert.Null(r.Inbound.MatchContact(channel, key));
    }

    [Fact]
    public void WhatsApp_ids_still_match_on_the_last_eight_digits()
    {
        using var r = new AiRig();
        var c = r.Contact("Real", "+60 12-345 6789");
        Assert.Equal(c.Id, r.Inbound.MatchContact(ChatBranch.Domain.Channels.WhatsApp, "60123456789")?.Id);
    }

    [Fact]
    public void A_conversation_number_going_down_raises_one_alert_not_two()
    {
        using var r = new AiRig();
        var u = new AppUser { Username = "boss", IsActive = true };
        r.Db.Users.Add(u);
        r.Db.SaveChanges();
        r.Db.Profiles.Add(new Profile { UserId = u.Id, Role = Roles.SuperAdmin });
        var personal = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Baileys, Name = "Sales phone", ExternalId = "bl-1",
            HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
        };
        var biz = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Cloud, Name = "Biz", ExternalId = "PN-1",
            AccessToken = "tok", HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow,
        };
        r.Db.ChannelAccounts.AddRange(personal, biz);
        r.Db.SaveChanges();

        // the conversation (Baileys) number is banned: the recovery alert only
        Assert.True(AccountHealth.SetStatus(r.Db, personal, Health.Banned, "logged_out"));
        r.Db.SaveChanges();
        var notes = r.T.Fresh().Notifications.AsNoTracking().ToList();
        Assert.Single(notes);
        Assert.StartsWith("Account Sales phone down", notes[0].Title);

        // a business (reminder) number keeps the generic health bell
        Assert.True(AccountHealth.SetStatus(r.Db, biz, Health.Disconnected, "token expired"));
        r.Db.SaveChanges();
        Assert.Contains(r.T.Fresh().Notifications, n => n.Title == "Channel account disconnected: Biz");
    }
}
