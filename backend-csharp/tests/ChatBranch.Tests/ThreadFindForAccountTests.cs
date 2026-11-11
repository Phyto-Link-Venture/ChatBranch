using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// <see cref="ThreadStore.FindForAccount"/> with NO account (the env-default Cloud number,
/// the sandbox, an account-less inbound) used to fall back to "the customer's only
/// conversation" — whichever company owned it. That was the primitive behind the
/// cross-company injection fixed at the Cloud webhook (commit 132d689): an account-less
/// inbound landed in company A's conversation, was answered from A's history and
/// paged A's team. With no account it now returns only the unattributed conversation.
/// </summary>
public class ThreadFindForAccountTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ThreadFindForAccountTests(ApiFactory f) => _f = f;

    private static int _n;
    private static string Key() => "60177" + (1000000 + Interlocked.Increment(ref _n)) + Random.Shared.Next(10, 99);

    private T Scoped<T>(Func<IServiceProvider, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider);
    }

    /// <summary>A company with one QR number, and that company's conversation with <paramref name="key"/>.</summary>
    private (int Company, int Account, int Thread) CompanyConversation(string key) => Scoped(sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var tenant = db.Tenants.First(t => t.Slug == "demo");
        var now = DateTime.UtcNow;
        var company = new Entity { TenantId = tenant.Id, Name = "Find Co", Slug = $"find-co-{Guid.NewGuid():N}"[..20],
            CreatedAt = now, UpdatedAt = now };
        db.Entities.Add(company);
        db.SaveChanges();
        var acct = new ChannelAccount { Channel = "whatsapp", Provider = Providers.Baileys, Name = "Find Co number",
            ExternalId = $"find-{Guid.NewGuid():N}"[..16], EntityId = company.Id, CreatedAt = now };
        db.ChannelAccounts.Add(acct);
        db.SaveChanges();
        var store = sp.GetRequiredService<ThreadStore>();
        var t = store.GetOrCreateThread("whatsapp", key, "Customer", acct.Id);
        store.AppendMessage(t, MessageDirection.In, MessageSource.Customer, "COMPANY-SECRET order 42",
            status: "received", accountId: acct.Id);
        Assert.Equal(company.Id, t.CompanyEntityId);
        return (company.Id, acct.Id, t.Id);
    });

    [Fact]
    public void No_account_never_finds_a_companys_only_conversation()
    {
        var key = Key();
        var (_, _, companyThread) = CompanyConversation(key);
        var found = Scoped(sp => sp.GetRequiredService<ThreadStore>().FindForAccount("whatsapp", key, null));
        Assert.Null(found);
        // the company's own number still finds it
        Assert.Equal(companyThread, Scoped(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var acct = db.Threads.AsNoTracking().Single(t => t.Id == companyThread).AccountId;
            return sp.GetRequiredService<ThreadStore>().FindForAccount("whatsapp", key, acct)?.Id;
        }));
    }

    [Fact]
    public void An_account_less_inbound_opens_an_unattributed_conversation_and_leaves_the_company_alone()
    {
        var key = Key();
        var (_, _, companyThread) = CompanyConversation(key);
        var msg = Scoped(sp => sp.GetRequiredService<InboundProcessor>()
            .ProcessInbound("whatsapp", key, "hello from the default number", "Walk-in", accountId: null));
        Assert.NotEqual(companyThread, msg.ThreadId);
        Scoped(sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var landed = db.Threads.AsNoTracking().Single(t => t.Id == msg.ThreadId);
            Assert.Null(landed.CompanyEntityId);
            // the company's conversation got nothing
            Assert.Equal(1, db.Messages.Count(m => m.ThreadId == companyThread));
            return 0;
        });
        // and the next account-less message keeps to that same unattributed conversation
        var again = Scoped(sp => sp.GetRequiredService<InboundProcessor>()
            .ProcessInbound("whatsapp", key, "second", "Walk-in", accountId: null));
        Assert.Equal(msg.ThreadId, again.ThreadId);
    }

    [Fact]
    public void An_unattributed_conversation_is_still_found_with_no_account()
    {
        var key = Key();
        var id = Scoped(sp => sp.GetRequiredService<ThreadStore>().GetOrCreateThread("whatsapp", key, "Walk-in").Id);
        Assert.Equal(id, Scoped(sp => sp.GetRequiredService<ThreadStore>().FindForAccount("whatsapp", key, null)?.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_conversation_is_never_created_with_an_empty_key(string key)
    {
        Assert.Throws<ArgumentException>(() =>
            Scoped(sp => sp.GetRequiredService<ThreadStore>().GetOrCreateThread("whatsapp", key, "Nobody")));
        Assert.Throws<ArgumentException>(() =>
            Scoped(sp => sp.GetRequiredService<ThreadStore>().GetOrCreateInCompany("whatsapp", key, null, "Nobody")));
        Assert.False(Scoped(sp => sp.GetRequiredService<AppDbContext>().Threads
            .Any(t => t.Channel == "whatsapp" && t.Key.Trim() == "")));
    }
}
