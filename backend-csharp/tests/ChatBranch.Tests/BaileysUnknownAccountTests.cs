using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The Baileys gateway pushes (<c>/api/whatsapp/baileys/inbound</c>, <c>/media</c>) carry the
/// gateway key of the paired number in <c>account_id</c>. A key that matches no account — a
/// number deleted in ChatBranch whose session the gateway still restores from disk, or a
/// forged push holding the shared secret — used to be filed with NO account: the same shape
/// as the Cloud-webhook leak (an organization-wide conversation, adoptable by whichever
/// company touches it next). It is now a recorded drop with no message text and no sender.
/// </summary>
public class BaileysUnknownAccountTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public BaileysUnknownAccountTests(ApiFactory f) => _f = f;

    private static int _n;
    private static string WaId() => "60188" + (1000000 + Interlocked.Increment(ref _n)) + Random.Shared.Next(10, 99);

    private HttpClient Gateway()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Baileys-Secret", "baileys-local-dev");
        return c;
    }

    private T Db<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _f.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>A company's paired number and its conversation (with one message) with <paramref name="waId"/>.</summary>
    private (string Key, int Thread) Paired(string waId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenant = db.Tenants.First(t => t.Slug == "demo");
        var now = DateTime.UtcNow;
        var company = new Entity { TenantId = tenant.Id, Name = "QR Co", Slug = $"qr-co-{Guid.NewGuid():N}"[..20],
            CreatedAt = now, UpdatedAt = now };
        db.Entities.Add(company);
        db.SaveChanges();
        var key = $"gw-known-{Guid.NewGuid():N}"[..20];
        var acct = new ChannelAccount { Channel = "whatsapp", Provider = Providers.Baileys, Name = key, ExternalId = key,
            EntityId = company.Id, HealthStatus = Health.Healthy, CreatedAt = now };
        db.ChannelAccounts.Add(acct);
        db.SaveChanges();
        var store = scope.ServiceProvider.GetRequiredService<ThreadStore>();
        var t = store.GetOrCreateThread("whatsapp", waId, "Customer", acct.Id);
        store.AppendMessage(t, MessageDirection.In, MessageSource.Customer, "real message", status: "received",
            externalId: $"REAL-{waId}", accountId: acct.Id);
        return (key, t.Id);
    }

    private List<AuditLog> Drops(string ghost) => Db(db => db.AuditLogs.AsNoTracking()
        .Where(a => a.Action == BaileysInboxRouting.UnknownAccountAudit && a.Target.Contains(ghost)).ToList());

    [Fact]
    public async Task An_unknown_account_id_is_a_recorded_drop_never_a_conversation()
    {
        var waId = WaId();
        var (_, companyThread) = Paired(waId);
        var ghost = $"gw-ghost-{Guid.NewGuid():N}"[..20];
        var res = await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound", new
        {
            account_id = ghost, wa_id = waId, name = "Spoofer", text = "INJECTED-TEXT pay to account 999",
            msg_id = $"GHOST-{waId}",
        });
        // 2xx: the gateway must not retry something we will never accept
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Db(db =>
        {
            Assert.Equal(1, db.Messages.Count(m => m.ThreadId == companyThread));
            Assert.False(db.Messages.Any(m => m.Text.Contains("INJECTED-TEXT")));
            Assert.Single(db.Threads.Where(t => t.Channel == "whatsapp" && t.Key == waId));
            return 0;
        });
        var audit = Assert.Single(Drops(ghost));
        Assert.DoesNotContain("INJECTED-TEXT", audit.Detail);
        Assert.DoesNotContain(waId, audit.Detail + audit.Target);
        Assert.DoesNotContain("Spoofer", audit.Detail + audit.Target);
    }

    [Fact]
    public async Task An_unknown_account_cannot_react_or_push_media_into_a_conversation()
    {
        var waId = WaId();
        var (_, companyThread) = Paired(waId);
        var ghost = $"gw-ghost-{Guid.NewGuid():N}"[..20];

        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound", new
        {
            account_id = ghost, wa_id = waId, reaction = new { msg_id = $"REAL-{waId}", emoji = "💩" },
        })).EnsureSuccessStatusCode();

        // a media push whose text never landed — and one naming a REAL message's id
        foreach (var msgId in new[] { $"GHOST-MEDIA-{waId}", $"REAL-{waId}" })
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(ghost), "account_id" },
                { new StringContent(waId), "wa_id" },
                { new StringContent(msgId), "msg_id" },
                { new StringContent("image"), "media_type" },
            };
            var file = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 });
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "file", "media.jpg");
            var res = await Gateway().PostAsync("/api/whatsapp/baileys/media", form);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("stored").GetBoolean());
        }

        Db(db =>
        {
            var rows = db.Messages.AsNoTracking().Where(m => m.ThreadId == companyThread).ToList();
            var only = Assert.Single(rows);
            Assert.Equal("", only.MediaPath);
            Assert.False(db.MessageReactions.Any(r => r.MessageId == only.Id));
            Assert.Single(db.Threads.Where(t => t.Channel == "whatsapp" && t.Key == waId));
            return 0;
        });
        Assert.NotEmpty(Drops(ghost));
    }

    [Fact]
    public async Task An_unknown_sender_on_a_paired_number_lands_as_an_unlinked_conversation()
    {
        var (key, _) = Paired(WaId());
        var stranger = WaId();
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound", new
        {
            account_id = key, wa_id = stranger, name = "Stranger", text = "hello from WhatsApp Web", msg_id = $"S-{stranger}",
        })).EnsureSuccessStatusCode();
        var admin = await Platform.As(_f, "admin", "admin123");
        await UnknownSenderAsserts.AssertListedAsNotInContactsAsync(_f, admin, "whatsapp", stranger, "hello from WhatsApp Web");
    }

    [Fact]
    public async Task A_known_account_still_files_its_message()
    {
        var waId = WaId();
        var (key, companyThread) = Paired(waId);
        (await Gateway().PostAsJsonAsync("/api/whatsapp/baileys/inbound", new
        {
            account_id = key, wa_id = waId, name = "Customer", text = "second real message", msg_id = $"REAL2-{waId}",
        })).EnsureSuccessStatusCode();
        Assert.Equal(2, Db(db => db.Messages.Count(m => m.ThreadId == companyThread)));
    }
}
