using ChatBranch.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Tests;

/// <summary>
/// A throwaway SQLite database (in-memory, kept alive by an open connection) with
/// the real schema — including the partial unique indexes — created via
/// EnsureCreated. Disposing closes the connection and drops the database.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public AppDbContext Db { get; }

    /// <param name="name">a NAMED in-memory database: its connection string is unique, so
    /// per-database caches keyed by it (AiQuota) never mix with other tests'</param>
    public TestDb(string? name = null)
    {
        _conn = new SqliteConnection(name == null ? "DataSource=:memory:" : $"DataSource=file:{name}?mode=memory&cache=shared");
        _conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn)
            .Options;
        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();
    }

    public AppDbContext Fresh()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn)
            .Options;
        return new AppDbContext(options);
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}
