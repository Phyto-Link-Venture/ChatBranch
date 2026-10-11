using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The database switch (DatabaseSetup) everywhere, and — when the suite runs with
/// TEST_DATABASE=postgres — what only a real Postgres shows: the model-driven
/// schema upgrade of an OLDER database and the date round trip. The rest of the
/// Postgres proof is the whole suite running on it (ApiFactory).
/// </summary>
public class PostgresTests
{
    [Fact]
    public void The_provider_and_connection_follow_the_settings()
    {
        Assert.Equal(DatabaseSetup.Sqlite, DatabaseSetup.Provider(null));
        Assert.Equal(DatabaseSetup.Sqlite, DatabaseSetup.Provider("sqlite"));
        foreach (var pg in new[] { "postgres", "PostgreSQL", " pg ", "npgsql" })
            Assert.Equal(DatabaseSetup.Postgres, DatabaseSetup.Provider(pg));

        Assert.Equal(DatabaseSetup.DefaultSqlite, DatabaseSetup.ConnectionString(DatabaseSetup.Sqlite, null, null));
        Assert.Equal("Data Source=/data/x.sqlite3",
            DatabaseSetup.ConnectionString(DatabaseSetup.Sqlite, "Data Source=/data/x.sqlite3", "postgres://ignored"));
        Assert.Throws<InvalidOperationException>(() => DatabaseSetup.ConnectionString(DatabaseSetup.Postgres, null, null));

        var b = new NpgsqlConnectionStringBuilder(DatabaseSetup.ConnectionString(DatabaseSetup.Postgres, null,
            "postgres://chat%40branch:p%3Ass@db.internal:6543/chatbranch?sslmode=require"));
        Assert.Equal("db.internal", b.Host);
        Assert.Equal(6543, b.Port);
        Assert.Equal("chatbranch", b.Database);
        Assert.Equal("chat@branch", b.Username);
        Assert.Equal("p:ss", b.Password);
        Assert.Equal(SslMode.Require, b.SslMode);
        Assert.Equal(5432, new NpgsqlConnectionStringBuilder(DatabaseSetup.FromUrl("postgresql://u:p@h/d")).Port);
        Assert.Throws<InvalidOperationException>(() => DatabaseSetup.FromUrl("mysql://u:p@h/d"));
    }

    [Fact]
    public void EF_create_script_splits_into_statements()
    {
        var parts = PostgresSchema.Statements("CREATE TABLE \"a\" (\n  \"id\" integer\n);\n\n\nCREATE INDEX \"ix\" ON \"a\" (\"id\");\n");
        Assert.Equal(2, parts.Count);
        Assert.StartsWith("CREATE TABLE \"a\"", parts[0]);
        Assert.Equal("CREATE INDEX \"ix\" ON \"a\" (\"id\")", parts[1]);
    }

    [Fact]
    public void The_schema_sync_reads_quoted_and_bare_identifiers()
    {
        // Npgsql quotes only what needs it, so the script says CREATE TABLE crm_message
        Assert.Equal("crm_x", PostgresSchema.CreatedTable("CREATE TABLE crm_x (\n id integer)"));
        Assert.Equal("Crm X", PostgresSchema.CreatedTable("CREATE TABLE \"Crm X\" (id integer)"));
        Assert.Equal(("ix_x", "crm_x"), PostgresSchema.CreatedIndex("CREATE INDEX ix_x ON crm_x (a)"));
        Assert.Equal(("IX X", "Crm X"),
            PostgresSchema.CreatedIndex("CREATE UNIQUE INDEX \"IX X\" ON \"Crm X\" (a) WHERE \"s\" = 'p'"));
        Assert.Null(PostgresSchema.CreatedTable("ALTER TABLE crm_x ADD COLUMN y text"));
        Assert.Null(PostgresSchema.CreatedIndex("CREATE TABLE crm_x (id integer)"));
    }

    [Fact]
    public void An_older_postgres_database_gets_the_missing_tables_columns_and_indexes()
    {
        if (ApiFactory.PostgresServer == null) return; // SQLite run — the CI Postgres job covers it
        using var db = new PgDb();
        using (var ctx = db.Context())
        {
            Seed.Run(ctx);
            ctx.Tenants.Add(new Tenant { Name = "T", Slug = "older", Status = "active",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            ctx.SaveChanges();
            var t = ctx.Threads.Add(new Domain.Thread { Channel = "whatsapp", Key = "601", CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow }).Entity;
            ctx.SaveChanges();
            ctx.Messages.Add(new Message { ThreadId = t.Id, Direction = "in", Text = "hi", CreatedAt = DateTime.UtcNow });
            ctx.SaveChanges();
        }
        // make it "older": a column, a whole table and an index the model has are gone
        db.Sql("ALTER TABLE \"crm_message\" DROP COLUMN \"transcript\"");
        db.Sql("DROP TABLE \"crm_aitaskrun\"");
        db.Sql("DROP INDEX \"uniq_contactaccess_pending\"");

        using (var ctx = db.Context()) Seed.Run(ctx);

        Assert.Equal("text", db.Scalar("SELECT data_type FROM information_schema.columns "
                                       + "WHERE table_name = 'crm_message' AND column_name = 'transcript'"));
        Assert.Equal("NO", db.Scalar("SELECT is_nullable FROM information_schema.columns "
                                     + "WHERE table_name = 'crm_message' AND column_name = 'transcript'"));
        Assert.Equal("1", db.Scalar("SELECT count(*) FROM information_schema.tables WHERE table_name = 'crm_aitaskrun'"));
        Assert.Equal("1", db.Scalar("SELECT count(*) FROM pg_indexes WHERE indexname = 'uniq_contactaccess_pending'"));
        using (var ctx = db.Context())
        {
            // the existing row took a new object's value, and the recreated table works
            Assert.Equal("", ctx.Messages.Single().Transcript);
            Assert.Empty(ctx.AiTaskRuns.ToList());
        }
        // and a second boot changes nothing
        using (var ctx = db.Context()) Seed.Run(ctx);
    }

    [Fact]
    public void Dates_round_trip_as_written_and_UtcNow_and_Like_translate_like_sqlite()
    {
        if (ApiFactory.PostgresServer == null) return;
        using var db = new PgDb();
        var at = new DateTime(2026, 10, 11, 9, 30, 15, DateTimeKind.Utc);
        using (var ctx = db.Context())
        {
            Seed.Run(ctx);
            ctx.Threads.Add(new Domain.Thread { Channel = "email", Key = "Mixed@Case.Test", Name = "Ahmad BIN Ali",
                CreatedAt = at, UpdatedAt = at.AddYears(-1) });
            ctx.SaveChanges();
        }
        Assert.Equal("timestamp without time zone", db.Scalar("SELECT data_type FROM information_schema.columns "
                                                              + "WHERE table_name = 'crm_thread' AND column_name = 'created_at'"));
        using (var ctx = db.Context())
        {
            var t = ctx.Threads.Single(x => x.Key == "Mixed@Case.Test");
            Assert.Equal(at, t.CreatedAt);
            Assert.Equal(DateTimeKind.Unspecified, t.CreatedAt.Kind); // as SQLite reads it back
            // DateTime.UtcNow inside the query is the database's UTC clock, comparable with the column
            Assert.True(ctx.Threads.Any(x => x.Id == t.Id && x.UpdatedAt < DateTime.UtcNow));
            ctx.Threads.Where(x => x.Id == t.Id).ExecuteUpdate(s => s.SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
            Assert.InRange(ctx.Threads.AsNoTracking().Single(x => x.Id == t.Id).UpdatedAt,
                DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
            // LIKE ignores case, as on SQLite
            Assert.True(ctx.Threads.Any(x => EF.Functions.Like(x.Name, "%ahmad bin%")));
            Assert.True(ctx.Threads.Any(x => EF.Functions.Like(x.Key, "MIXED@%", "\\")));
        }
    }

    [Fact]
    public void The_sqlite_file_is_copied_into_an_empty_postgres_once_with_ids_and_sequences()
    {
        if (ApiFactory.PostgresServer == null) return;
        var file = Path.Combine(Path.GetTempPath(), $"chatbranch-import-test-{Guid.NewGuid():N}.sqlite3");
        try
        {
            var lite = new DbContextOptionsBuilder<AppDbContext>();
            lite.Use(DatabaseSetup.Sqlite, $"Data Source={file}");
            int contactId, entityId;
            using (var ctx = new AppDbContext(lite.Options))
            {
                Seed.Run(ctx);
                var parent = ctx.Entities.First();
                var branch = ctx.Entities.Add(new Entity { TenantId = parent.TenantId, ParentId = parent.Id, Name = "Branch",
                    Slug = "branch", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }).Entity;
                ctx.SaveChanges();
                var c = ctx.Contacts.Add(new Contact { Name = "Siti", Phone = "+60123456789", Tags = new() { "vip" },
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }).Entity;
                ctx.SaveChanges();
                ctx.ContactEntities.Add(new ContactEntity { ContactId = c.Id, EntityId = branch.Id, CreatedAt = DateTime.UtcNow });
                ctx.Settings.Add(new Setting { Key = "org_timezone", Value = "Asia/Kuala_Lumpur", UpdatedAt = DateTime.UtcNow });
                ctx.Settings.Add(new Setting { Key = "aicore_persona", Value = "Branch persona", EntityId = branch.Id,
                    UpdatedAt = DateTime.UtcNow });
                ctx.SaveChanges();
                // a gap in the ids: the copy keeps them, the sequence continues after the highest
                ctx.Contacts.Add(new Contact { Id = 40, Name = "Late", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                ctx.SaveChanges();
                (contactId, entityId) = (c.Id, branch.Id);
            }
            var expected = new Dictionary<string, int>();
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}"))
            {
                conn.Open();
                foreach (var table in new[] { "auth_user", "ingest_entity", "crm_contact", "crm_contactentity", "crm_setting", "crm_role" })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT count(*) FROM \"{table}\"";
                    expected[table] = Convert.ToInt32(cmd.ExecuteScalar());
                }
            }

            using var db = new PgDb();
            using (var ctx = db.Context()) Seed.Run(ctx, file);
            foreach (var (table, rows) in expected)
                Assert.Equal(rows + (table == "crm_setting" ? 1 : 0), int.Parse(db.Scalar($"SELECT count(*) FROM \"{table}\"")!)); // + the marker
            using (var ctx = db.Context())
            {
                var siti = ctx.Contacts.Single(c => c.Id == contactId);
                Assert.Equal("Siti", siti.Name);
                Assert.Equal(new List<string> { "vip" }, siti.Tags);
                Assert.Equal(entityId, ctx.ContactEntities.Single(l => l.ContactId == contactId).EntityId);
                Assert.Equal("Branch persona", ctx.Settings.Single(x => x.Key == "aicore_persona" && x.EntityId == entityId).Value);
                Assert.Contains("row(s)", ctx.Settings.Single(x => x.Key == SqliteImport.MarkerKey).Value);
                var fresh = ctx.Contacts.Add(new Contact { Name = "After", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }).Entity;
                ctx.SaveChanges();
                Assert.Equal(41, fresh.Id);
            }
            // a database that has data is never imported into again
            using (var ctx = db.Context())
                Assert.False(SqliteImport.RunIfNeeded(ctx, file).Imported);
            using (var ctx = db.Context()) Seed.Run(ctx, file);
            Assert.Equal(expected["crm_contact"] + 1, int.Parse(db.Scalar("SELECT count(*) FROM crm_contact")!));
        }
        finally
        {
            using (var mine = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}"))
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(mine);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(file + suffix); } catch { /* temp */ }
        }
    }

    /// <summary>A throwaway Postgres database on the suite's server.</summary>
    private sealed class PgDb : IDisposable
    {
        private readonly string _name = $"cb_unit_{Guid.NewGuid():N}";
        private readonly string _conn;

        public PgDb()
        {
            Admin($"CREATE DATABASE \"{_name}\"");
            _conn = new NpgsqlConnectionStringBuilder(ApiFactory.PostgresServer!) { Database = _name }.ConnectionString;
        }

        public AppDbContext Context()
        {
            var o = new DbContextOptionsBuilder<AppDbContext>();
            o.Use(DatabaseSetup.Postgres, _conn);
            return new AppDbContext(o.Options);
        }

        public void Sql(string sql)
        {
            using var c = new NpgsqlConnection(_conn);
            c.Open();
            using var cmd = new NpgsqlCommand(sql, c);
            cmd.ExecuteNonQuery();
        }

        public string? Scalar(string sql)
        {
            using var c = new NpgsqlConnection(_conn);
            c.Open();
            using var cmd = new NpgsqlCommand(sql, c);
            return cmd.ExecuteScalar()?.ToString();
        }

        private static void Admin(string sql)
        {
            using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ApiFactory.PostgresServer!)
                { Database = "postgres" }.ConnectionString);
            c.Open();
            using var cmd = new NpgsqlCommand(sql, c);
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try
            {
                using (var mine = new NpgsqlConnection(_conn)) NpgsqlConnection.ClearPool(mine);
                Admin($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
            }
            catch { /* best effort */ }
        }
    }
}
