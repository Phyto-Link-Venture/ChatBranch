using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;

namespace ChatBranch.Data;

/// <summary>
/// Which database the platform runs on — ONE place decides it. SQLite (the
/// default: a file, nothing to set up — dev, the tests, a first boot) or
/// PostgreSQL (production under docker compose). <c>DATABASE_PROVIDER</c> =
/// sqlite | postgres; the connection is <c>ConnectionStrings:Default</c>, else
/// <c>DATABASE_URL</c> (postgres://user:password@host:port/database), else the
/// SQLite file. The schema is the same EF model on both; Postgres only gets
/// dates stored like SQLite's (no time zone, read back as written — see
/// <see cref="UnspecifiedDateTime"/>).
/// </summary>
public static class DatabaseSetup
{
    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";
    public const string DefaultSqlite = "Data Source=chatbranch.sqlite3";

    /// <summary>sqlite | postgres from a setting ("postgresql", "pg", "npgsql" are postgres; anything else sqlite).</summary>
    public static string Provider(string? setting) =>
        (setting ?? "").Trim().ToLowerInvariant() switch
        {
            "postgres" or "postgresql" or "pg" or "npgsql" => Postgres,
            _ => Sqlite,
        };

    /// <summary>The connection string: the configured one, else DATABASE_URL for Postgres, else the SQLite file.</summary>
    public static string ConnectionString(string provider, string? configured, string? databaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured!;
        if (provider == Postgres)
        {
            if (string.IsNullOrWhiteSpace(databaseUrl))
                throw new InvalidOperationException(
                    "DATABASE_PROVIDER=postgres needs ConnectionStrings__Default or DATABASE_URL "
                    + "(postgres://user:password@host:5432/database).");
            return FromUrl(databaseUrl!);
        }
        return DefaultSqlite;
    }

    /// <summary>postgres://user:password@host:port/database?sslmode=require → an Npgsql connection string.</summary>
    public static string FromUrl(string url)
    {
        var u = new Uri(url.Trim());
        if (u.Scheme is not ("postgres" or "postgresql"))
            throw new InvalidOperationException("DATABASE_URL must start with postgres://");
        var userInfo = u.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = u.Host,
            Port = u.IsDefaultPort || u.Port <= 0 ? 5432 : u.Port,
            Database = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };
        foreach (var pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<SslMode>(kv[1], true, out var mode))
                b.SslMode = mode;
        }
        return b.ConnectionString;
    }

    /// <summary>Point an options builder at the chosen database.</summary>
    public static DbContextOptionsBuilder Use(this DbContextOptionsBuilder o, string provider, string connectionString)
    {
        if (provider != Postgres) return o.UseSqlite(connectionString);
        o.UseNpgsql(connectionString);
        // DateTime.UtcNow inside a query → timezone('UTC', now()), like SQLite's datetime('now')
        ((IDbContextOptionsBuilderInfrastructure)o).AddOrUpdateExtension(new PostgresUtcNowExtension());
        return o;
    }

    /// <summary>True when this context talks to PostgreSQL.</summary>
    public static bool IsPostgres(this DbContext db) => db.Database.IsNpgsql();
}

/// <summary>
/// Postgres stores <see cref="DateTime"/> like SQLite does: <c>timestamp without
/// time zone</c>, written as given and read back with an unspecified kind. The
/// code writes UTC everywhere; Npgsql would otherwise refuse a UTC value for that
/// column type (and a timestamptz column would hand back UTC-kind values the API
/// then serializes differently from the SQLite build).
/// </summary>
public sealed class UnspecifiedDateTime() : ValueConverter<DateTime, DateTime>(
    v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified),
    v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified));

/// <summary>
/// The model is cached per context TYPE by default; the same process can hold a
/// SQLite and a Postgres context (the one-time import), whose models differ
/// (date columns), so the provider is part of the key.
/// </summary>
public sealed class ProviderModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context.Database.ProviderName, designTime);
}
