using System.Collections;
using ChatBranch.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ChatBranch.Data;

/// <summary>
/// The one-time move from SQLite to PostgreSQL. On a Postgres boot with
/// <c>DB_IMPORT_SQLITE</c> pointing at the old <c>chatbranch.sqlite3</c>, and only
/// while the Postgres database is still EMPTY (no tenant, no user), every row of
/// every table is copied over with its id, in ONE transaction:
/// <list type="number">
/// <item>a snapshot of the SQLite file is taken with SQLite's backup API (the
///   original is never written) and brought up to the current schema
///   (Seed's SQLite upgrade) so the model can read it;</item>
/// <item>tables are copied principal-first, foreign-key checks deferred for the
///   transaction when the Postgres user may (session_replication_role);</item>
/// <item>row counts are compared table by table — any difference rolls it all
///   back and boot fails loudly, so the API never runs on a half-copied database;</item>
/// <item>identity sequences continue after the copied ids; a Setting
///   <c>db_imported_from_sqlite</c> records when and how much.</item>
/// </list>
/// A Postgres database that already has data is left alone, so the variable can
/// stay set; the SQLite file stays as it was (the way back).
/// </summary>
public static class SqliteImport
{
    public const string MarkerKey = "db_imported_from_sqlite";
    private const int Batch = 500;

    public sealed record Result(bool Imported, string Reason, int Rows = 0, int Tables = 0);

    public static Result RunIfNeeded(AppDbContext pg, string? sqlitePath, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath) || !pg.IsPostgres()) return new(false, "not requested");
        var path = sqlitePath.Trim();
        if (!File.Exists(path))
        {
            log?.Invoke($"SQLite import skipped: no file at {path}.");
            return new(false, "no SQLite file");
        }
        if (pg.Tenants.Any() || pg.Users.Any())
        {
            log?.Invoke("SQLite import skipped: the Postgres database already has data.");
            return new(false, "Postgres already has data");
        }

        var snapshot = Path.Combine(Path.GetTempPath(), $"chatbranch-import-{Guid.NewGuid():N}.sqlite3");
        var snapshotConn = $"Data Source={snapshot}";
        try
        {
            using (var source = new SqliteConnection($"Data Source={path};Mode=ReadOnly"))
            using (var copy = new SqliteConnection(snapshotConn))
            {
                source.Open();
                copy.Open();
                source.BackupDatabase(copy);
            }
            var options = new DbContextOptionsBuilder<AppDbContext>();
            options.Use(DatabaseSetup.Sqlite, snapshotConn);
            using var lite = new AppDbContext(options.Options);
            Seed.UpgradeSqlite(lite);

            log?.Invoke($"SQLite import: copying {path} into Postgres…");
            var result = Copy(lite, pg, Path.GetFileName(path), log);
            log?.Invoke($"SQLite import done: {result.Rows} row(s) in {result.Tables} table(s).");
            return result;
        }
        finally
        {
            using (var mine = new SqliteConnection(snapshotConn)) SqliteConnection.ClearPool(mine);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                try { File.Delete(snapshot + suffix); } catch { /* temp */ }
        }
    }

    private static Result Copy(AppDbContext lite, AppDbContext pg, string fileName, Action<string>? log)
    {
        var types = PrincipalsFirst(pg.Model.GetEntityTypes().Where(t => t.GetTableName() != null).ToList());
        var conn = pg.Database.GetDbConnection();
        pg.Database.OpenConnection();
        var counts = new List<(string Table, int Rows)>();
        try
        {
            using var tx = pg.Database.BeginTransaction();
            if (pg.Database.SqlQueryRaw<bool>("SELECT rolsuper AS \"Value\" FROM pg_roles WHERE rolname = current_user").Single())
                pg.Database.ExecuteSqlRaw("SET LOCAL session_replication_role = replica");

            foreach (var type in types)
            {
                var table = type.GetTableName()!;
                List<object> rows;
                try { rows = ReadAll(lite, type); }
                catch (SqliteException e) when (e.Message.Contains("no such table"))
                {
                    continue; // a table this SQLite database never had
                }
                for (var i = 0; i < rows.Count; i += Batch)
                {
                    foreach (var row in rows.Skip(i).Take(Batch))
                        if (type.HasSharedClrType) pg.Set<Dictionary<string, object>>(type.Name).Add((Dictionary<string, object>)row);
                        else pg.Entry(row).State = EntityState.Added;
                    pg.SaveChanges();
                    pg.ChangeTracker.Clear();
                }
                counts.Add((table, rows.Count));
            }

            // every table holds exactly what the SQLite one held
            foreach (var (table, expected) in counts)
            {
                var actual = pg.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM \"{table}\"").Single();
                if (actual != expected)
                    throw new InvalidOperationException(
                        $"SQLite import stopped: {table} has {actual} row(s) in Postgres, {expected} in SQLite. Nothing was kept.");
            }

            // ids continue after the copied ones
            foreach (var type in types)
            {
                var key = type.FindPrimaryKey();
                if (key is not { Properties.Count: 1 } || key.Properties[0].ClrType != typeof(int)) continue;
                var table = type.GetTableName()!;
                var column = key.Properties[0].GetColumnName();
                pg.Database.ExecuteSqlRaw(
                    $"SELECT setval(pg_get_serial_sequence('\"{table}\"', '{column}'), "
                    + $"(SELECT COALESCE(MAX(\"{column}\"), 0) + 1 FROM \"{table}\"), false) "
                    + $"WHERE pg_get_serial_sequence('\"{table}\"', '{column}') IS NOT NULL");
            }

            var total = counts.Sum(c => c.Rows);
            pg.Settings.Add(new Setting
            {
                Key = MarkerKey,
                Value = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm}Z — {total} row(s), {counts.Count(c => c.Rows > 0)} table(s) from {fileName}",
                UpdatedAt = DateTime.UtcNow,
            });
            pg.SaveChanges();
            tx.Commit();
            return new(true, "imported", total, counts.Count(c => c.Rows > 0));
        }
        finally
        {
            pg.ChangeTracker.Clear();
            pg.Database.CloseConnection();
        }
    }

    /// <summary>Every row of one entity type, untracked, in key order (parents before children within a table).</summary>
    private static List<object> ReadAll(AppDbContext db, IEntityType type)
    {
        IEnumerable rows;
        if (type.HasSharedClrType)
            rows = db.Set<Dictionary<string, object>>(type.Name).AsNoTracking().ToList();
        else
        {
            var set = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                .MakeGenericMethod(type.ClrType).Invoke(db, null)!;
            rows = (IEnumerable)typeof(EntityFrameworkQueryableExtensions)
                .GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!
                .MakeGenericMethod(type.ClrType).Invoke(null, new[] { set })!;
        }
        var list = rows.Cast<object>().ToList();
        var key = type.FindPrimaryKey()?.Properties.FirstOrDefault();
        if (key?.PropertyInfo != null && !type.HasSharedClrType)
            list = list.OrderBy(r => key.PropertyInfo.GetValue(r)).ToList();
        return list;
    }

    /// <summary>Entity types ordered so a table comes after every table it references.</summary>
    public static List<IEntityType> PrincipalsFirst(IReadOnlyList<IEntityType> types)
    {
        var ordered = new List<IEntityType>();
        var done = new HashSet<IEntityType>();
        var visiting = new HashSet<IEntityType>();
        void Visit(IEntityType t)
        {
            if (done.Contains(t) || !visiting.Add(t)) return; // a cycle: deferred FK checks cover it
            foreach (var fk in t.GetForeignKeys())
                if (fk.PrincipalEntityType != t && types.Contains(fk.PrincipalEntityType)) Visit(fk.PrincipalEntityType);
            visiting.Remove(t);
            done.Add(t);
            ordered.Add(t);
        }
        foreach (var t in types.OrderBy(t => t.GetTableName(), StringComparer.Ordinal)) Visit(t);
        return ordered;
    }
}
