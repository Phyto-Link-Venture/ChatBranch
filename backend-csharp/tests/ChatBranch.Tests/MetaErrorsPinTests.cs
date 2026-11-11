using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Meta error 131031 has two meanings: "the WhatsApp Business Account has been restricted
/// or disabled for violating a platform policy" (a real lock) OR "we were unable to verify
/// data included in the request against data set on the WhatsApp Business Account (e.g.
/// the two-step pin included in the request is incorrect)". error_subcode is deprecated
/// (not returned on v16.0+), so the two are told apart by the error's text. A PIN failure
/// is recoverable — it must never mark the number Banned and pull it out of routing.
/// </summary>
public class MetaErrorsPinTests
{
    // Meta's error-codes reference, Details column for 131031 (one sentence, both meanings)
    private const string DocumentedDetails =
        "The WhatsApp Business Account associated with the app has been restricted or disabled for violating "
        + "a platform policy, or we were unable to verify data included in the request against data set on the "
        + "WhatsApp Business Account (e.g, the two-step pin included in the request is incorrect).";

    [Theory]
    [InlineData("Business account is locked — Unable to verify data included in the request: the two-step pin included in the request is incorrect")]
    [InlineData("(#131031) Two-step verification PIN incorrect")]
    [InlineData("Account has been locked — we were unable to verify data included in the request against data set on the WhatsApp Business Account")]
    [InlineData("Incorrect PIN")]
    public void A_two_step_pin_failure_is_recoverable_not_a_ban(string detail)
    {
        var cls = MetaErrors.Classify(131031, null, detail);
        Assert.NotEqual(Health.Banned, cls.Health);
        Assert.Equal(Health.Degraded, cls.Health);          // like 133005 (two-step PIN mismatch)
        Assert.Equal(MetaErrorScope.Account, cls.Scope);
    }

    [Theory]
    [InlineData("Business Account locked")]
    [InlineData("Account has been locked — The WhatsApp Business Account associated with the app has been restricted or disabled for violating a platform policy.")]
    [InlineData("")]
    [InlineData(null)]
    public void A_policy_lock_or_a_bare_131031_is_still_a_ban(string? detail)
    {
        Assert.Equal(Health.Banned, MetaErrors.Classify(131031, null, detail).Health);
        Assert.Equal(Health.Banned, MetaErrors.Classify(131031).Health);
    }

    [Fact]
    public void Metas_own_two_meaning_sentence_keeps_the_ban()
    {
        // the reference text names BOTH meanings, so it proves nothing about which one this
        // is. A PIN only travels on a registration call; on a send, a status or an account
        // test 131031 is the lock — so only an UNAMBIGUOUS PIN / data-verification text
        // (no policy wording) relaxes the ban. Ambiguity keeps today's verdict.
        Assert.Equal(Health.Banned, MetaErrors.Classify(131031, null, DocumentedDetails).Health);
    }

    [Fact]
    public void Registration_codes_are_classified_by_what_they_mean()
    {
        // 133015: the number was recently deleted, deletion not complete — "wait 5 minutes"
        Assert.Equal(MetaErrorScope.Transient, MetaErrors.Classify(133015).Scope);
        Assert.Null(MetaErrors.Classify(133015).Health);
        // 133016: too many (de)registration attempts — blocked until the limit window passes;
        // a throughput limit on the account, recoverable, never Banned/Disconnected
        Assert.Equal(MetaErrorScope.RateLimit, MetaErrors.Classify(133016).Scope);
        Assert.Equal(Health.Degraded, MetaErrors.Classify(133016).Health);
        Assert.Equal(Health.Degraded, MetaErrors.Classify(133005).Health);
    }

    [Theory]
    [InlineData(131047, "Re-engagement message — account locked pin")]
    [InlineData(131026, "two-step pin incorrect; business account locked")]
    [InlineData(131050, "Business Account locked")]
    public void Per_recipient_codes_never_touch_health_whatever_their_text(int code, string detail)
    {
        var cls = MetaErrors.Classify(code, null, detail);
        Assert.Equal(MetaErrorScope.Recipient, cls.Scope);
        Assert.Null(cls.Health);
    }
}

/// <summary>The detail reaches the classifier on the real health path (<see cref="MetaErrors.ApplyToAccount"/>).</summary>
public class MetaErrorsPinHealthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public MetaErrorsPinHealthTests(ApiFactory f) => _f = f;

    [Fact]
    public void A_pin_failure_degrades_the_number_and_a_lock_bans_it()
    {
        CloudWebhookKit.WithDb(_f, db =>
        {
            ChannelAccount Number()
            {
                var a = new ChannelAccount { Channel = "whatsapp", Provider = Providers.Cloud, Name = "pin test",
                    ExternalId = $"PN-PIN-{Guid.NewGuid():N}"[..20], HealthStatus = Health.Healthy, CreatedAt = DateTime.UtcNow };
                db.ChannelAccounts.Add(a);
                db.SaveChanges();
                return a;
            }
            var pin = Number();
            MetaErrors.ApplyToAccount(db, pin, 131031, null, "Unable to verify data: the two-step pin included in the request is incorrect");
            db.SaveChanges();
            Assert.Equal(Health.Degraded, pin.HealthStatus);

            var locked = Number();
            MetaErrors.ApplyToAccount(db, locked, 131031, null, "Business Account locked");
            db.SaveChanges();
            Assert.Equal(Health.Banned, locked.HealthStatus);
            return 0;
        });
    }
}
