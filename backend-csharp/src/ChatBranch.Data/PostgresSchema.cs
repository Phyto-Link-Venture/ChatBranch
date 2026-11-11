using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ChatBranch.Data;

/// <summary>
/// Keeps an EXISTING Postgres database in step with the EF model — the Postgres
/// side of <c>Seed.UpgradeSchema</c>, whose hand-written SQLite DDL (TEXT dates,
/// INTEGER booleans) can't run here. Nothing is hand-written: a table the model
/// has and the database doesn't is created from EF's own Postgres script (with
/// its indexes); a missing column is added with the model's store type, existing
/// rows getting the value a NEW object of that entity carries (its C# initializer,
/// through any value converter); a missing index is created from the script.
/// Additive only — it never drops or alters anything. A model change therefore
/// reaches Postgres by itself; the SQLite list in Seed stays the place to add it
/// for SQLite.
/// </summary>
public static partial class PostgresSchema
{
    // Npgsql quotes an identifier only when it must: crm_message, but "Default"
    [GeneratedRegex("^CREATE TABLE (?:\"(?<table>[^\"]+)\"|(?<table>[^\\s(\"]+))")]
    private static partial Regex CreateTable();

    [GeneratedRegex("^CREATE (?:UNIQUE )?INDEX (?:\"(?<index>[^\"]+)\"|(?<index>[^\\s(\"]+)) ON (?:\"(?<table>[^\"]+)\"|(?<table>[^\\s(\"]+))")]
    private static partial Regex CreateIndex();

    /// <summary>The table a CREATE TABLE statement makes, else null.</summary>
    public static string? CreatedTable(string sql)
    {
        var m = CreateTable().Match(sql);
        return m.Success ? m.Groups["table"].Value : null;
    }

    /// <summary>The index and table a CREATE [UNIQUE] INDEX statement names, else null.</summary>
    public static (string Index, string Table)? CreatedIndex(string sql)
    {
        var m = CreateIndex().Match(sql);
        return m.Success ? (m.Groups["index"].Value, m.Groups["table"].Value) : null;
    }

    public static void Sync(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            var tables = Strings(conn, "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema()");
            var columns = Strings(conn,
                "SELECT table_name || '.' || column_name FROM information_schema.columns WHERE table_schema = current_schema()");
            var indexes = Strings(conn, "SELECT indexname FROM pg_indexes WHERE schemaname = current_schema()");
            var script = Statements(db.Database.GenerateCreateScript());

            // 1. whole tables (and their indexes), in the script's dependency order
            var created = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sql in script)
            {
                if (CreatedTable(sql) is { } table && !tables.Contains(table))
                {
                    Execute(conn, sql);
                    created.Add(table);
                    continue;
                }
                if (CreatedIndex(sql) is { } ix && created.Contains(ix.Table)) Execute(conn, sql);
            }

            // 2. columns of existing tables
            foreach (var table in db.Model.GetRelationalModel().Tables)
            {
                if (created.Contains(table.Name) || !tables.Contains(table.Name)) continue;
                foreach (var column in table.Columns)
                    if (!columns.Contains(table.Name + "." + column.Name))
                        Execute(conn, AddColumn(table, column));
            }

            // 3. indexes the model has and the database doesn't. A UNIQUE index the
            // live data already violates must NOT take the boot down with it (Sync
            // runs on every container start): log the clash and leave the database
            // as it is — the same tolerance Seed's SQLite helper is called with.
            foreach (var sql in script)
                if (CreatedIndex(sql) is { } ix && !created.Contains(ix.Table) && tables.Contains(ix.Table)
                    && !indexes.Contains(ix.Index))
                    try { Execute(conn, sql); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[schema] index {ix.Index} on {ix.Table} not created: {ex.Message}");
                    }
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>True when the table exists (Seed's portable index helpers skip legacy SQLite-only tables).</summary>
    public static bool TableExists(DbConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @t";
        var p = cmd.CreateParameter();
        p.ParameterName = "t";
        p.Value = table;
        cmd.Parameters.Add(p);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>ALTER TABLE … ADD COLUMN with the model's store type; NOT NULL columns get the new-object value as default.</summary>
    public static string AddColumn(ITable table, IColumn column)
    {
        var sql = $"ALTER TABLE \"{table.Name}\" ADD COLUMN IF NOT EXISTS \"{column.Name}\" {column.StoreType}";
        if (column.IsNullable) return sql + " NULL";
        var literal = DefaultLiteral(column);
        return literal == null ? sql + " NULL" : $"{sql} NOT NULL DEFAULT {literal}";
    }

    private static string? DefaultLiteral(IColumn column)
    {
        var property = column.PropertyMappings.FirstOrDefault()?.Property;
        if (property == null) return null;
        object? value = null;
        try
        {
            var instance = Activator.CreateInstance(property.DeclaringType.ClrType);
            value = property.PropertyInfo?.GetValue(instance) ?? property.FieldInfo?.GetValue(instance);
        }
        catch { /* no parameterless constructor: fall back to the type's zero */ }
        value ??= property.ClrType == typeof(string) ? ""
            : property.ClrType.IsValueType ? Activator.CreateInstance(property.ClrType) : null;
        if (value == null) return null;
        // the PROPERTY's mapping carries its value converter (JSON lists, enums…)
        try { return property.GetRelationalTypeMapping().GenerateSqlLiteral(value); }
        catch { return null; }
    }

    /// <summary>EF's create script, one statement per entry (trailing ';' removed).</summary>
    public static List<string> Statements(string script) =>
        Regex.Split(script, @";\s*(?:\r?\n|$)")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    private static HashSet<string> Strings(DbConnection conn, string sql)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (!r.IsDBNull(0)) set.Add(r.GetString(0));
        return set;
    }

    private static void Execute(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
