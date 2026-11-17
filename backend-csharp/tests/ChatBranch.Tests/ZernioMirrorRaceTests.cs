using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Zernio;
using ChatBranch.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// One Zernio account = one mirrored inbox. The WhatsApp onboarding callback
/// (<c>ZernioEndpoints.WhatsApp</c>) and the <c>account.connected</c> webhook
/// (<c>ZernioWebhook</c>) can both mirror the same account at the same moment; both
/// looked the account up, found nothing and inserted — two inboxes for one number.
/// Partial unique indexes now refuse the second row, and the mirror
/// (<see cref="ZernioAccounts.Upsert"/>, the ONE code path every Zernio channel goes
/// through) treats that refusal as "already mirrored": it refreshes the winner instead
/// of failing the request with a 500.
/// </summary>
public class ZernioMirrorRaceTests
{
    private static DbContextOptions<AppDbContext> Options(string file, params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={file}").AddInterceptors(interceptors).Options;

    private static void InTempDb(Action<string> body)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cb-zernio-race-{Guid.NewGuid():N}.sqlite3");
        try
        {
            using (var db = new AppDbContext(Options(file))) Seed.Run(db);
            body(file);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    private static List<string> AccountIndexes(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'crm_channelaccount'";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    /// <summary>Runs <paramref name="winner"/> just before the FIRST save that inserts a Zernio row.</summary>
    private sealed class RaceInterceptor : SaveChangesInterceptor
    {
        private readonly Action _winner;
        private bool _fired;
        public RaceInterceptor(Action winner) => _winner = winner;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (!_fired && eventData.Context?.ChangeTracker.Entries<ChannelAccount>()
                    .Any(e => e.State == EntityState.Added && e.Entity.Provider == Providers.Zernio) == true)
            {
                _fired = true;
                _winner();
            }
            return result;
        }
    }

    private static JsonElement Account(string zid, string platform) => JsonSerializer.SerializeToElement(new
    {
        _id = zid, platform, username = platform == "whatsapp" || platform == "sms" ? "60123450000" : "racebot",
        displayName = "Race", isActive = true,
    });

    [Theory]
    [InlineData("whatsapp", "whatsapp")]
    [InlineData("telegram", "telegram")]
    [InlineData("sms", "sms")]
    public void Two_mirrors_of_one_account_racing_leave_one_inbox_and_no_error(string platform, string channel)
    {
        InTempDb(file =>
        {
            var zid = $"zr-{Guid.NewGuid():N}"[..16];
            var acct = Account(zid, platform);
            // the webhook wins the race: it mirrors the account between the callback's lookup and its insert
            var race = new RaceInterceptor(() =>
            {
                using var other = new AppDbContext(Options(file));
                new ZernioAccounts(other, new NullZernioClient()).Upsert(acct, null, "", intent: "inbox");
            });
            using var db = new AppDbContext(Options(file, race));
            var accounts = new ZernioAccounts(db, new NullZernioClient());

            var row = accounts.Upsert(acct, null, "", intent: "inbox");      // must not throw
            Assert.NotNull(row);

            // the request carries on: a later save in the same context does not re-fail
            AuditTrail.Write(db, "zernio.connect", channel, "after the race", actor: "test");
            db.SaveChanges();

            using var check = new AppDbContext(Options(file));
            var rows = check.ChannelAccounts.AsNoTracking()
                .Where(a => a.Provider == Providers.Zernio && a.ExternalId == zid).ToList();
            var only = Assert.Single(rows);
            Assert.Equal(channel, only.Channel);
            Assert.Equal(only.Id, row!.Id);
        });
    }

    [Fact]
    public void Fresh_and_upgraded_databases_carry_the_zernio_indexes_and_a_duplicate_still_boots()
    {
        InTempDb(file =>
        {
            using (var db = new AppDbContext(Options(file)))
            {
                var names = AccountIndexes(db);
                Assert.Contains(AppDbContext.WhatsAppNumberIndex, names);
                Assert.Contains(AppDbContext.ZernioWhatsAppIndex, names);
                Assert.Contains(AppDbContext.ZernioAccountIndex, names);
                // the EF model keeps BOTH WhatsApp indexes (one must not overwrite the other)
                var modelNames = db.Model.FindEntityType(typeof(ChannelAccount))!.GetIndexes()
                    .Select(i => i.GetDatabaseName()).ToList();
                Assert.Contains(AppDbContext.WhatsAppNumberIndex, modelNames);
                Assert.Contains(AppDbContext.ZernioWhatsAppIndex, modelNames);
                Assert.Contains(AppDbContext.ZernioAccountIndex, modelNames);

                db.ChannelAccounts.Add(Row("whatsapp", "Z-ONE"));
                db.SaveChanges();
                db.ChannelAccounts.Add(Row("whatsapp", "Z-ONE"));
                Assert.Throws<DbUpdateException>(() => db.SaveChanges());
            }
            using (var db = new AppDbContext(Options(file)))
            {
                // rows with no id yet, other providers and other channels are not duplicates
                db.ChannelAccounts.Add(Row("whatsapp", ""));
                db.ChannelAccounts.Add(Row("whatsapp", ""));
                db.ChannelAccounts.Add(Row("whatsapp", "Z-ONE", provider: "baileys"));
                db.ChannelAccounts.Add(Row("telegram", "Z-ONE"));   // the same id on another channel
                db.SaveChanges();

                // upgrade path: an old database without the indexes, already holding a duplicate
                db.Database.ExecuteSqlRaw($"DROP INDEX \"{AppDbContext.ZernioWhatsAppIndex}\"");
                db.Database.ExecuteSqlRaw($"DROP INDEX \"{AppDbContext.ZernioAccountIndex}\"");
                db.ChannelAccounts.Add(Row("whatsapp", "Z-ONE"));
                db.ChannelAccounts.Add(Row("telegram", "Z-TG"));
                db.SaveChanges();
            }
            using (var db = new AppDbContext(Options(file))) Seed.Run(db);   // the boot after deploy: must NOT throw
            using (var db = new AppDbContext(Options(file)))
            {
                var names = AccountIndexes(db);
                Assert.DoesNotContain(AppDbContext.ZernioWhatsAppIndex, names);        // the clash is left for an operator
                Assert.Contains(AppDbContext.WhatsAppNumberIndex, names);
                Assert.Contains(AppDbContext.ZernioAccountIndex, names);                // its own clash-free index still lands
                Assert.Equal(2, db.ChannelAccounts.Count(a => a.Channel == "whatsapp" && a.Provider == Providers.Zernio
                                                              && a.ExternalId == "Z-ONE"));
            }
        });
    }

    private static ChannelAccount Row(string channel, string zid, string provider = Providers.Zernio) => new()
    {
        Channel = channel, Provider = provider, Name = $"{channel}-{zid}", ExternalId = zid, CreatedAt = DateTime.UtcNow,
    };
}
