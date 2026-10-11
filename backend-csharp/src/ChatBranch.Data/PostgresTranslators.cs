using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Data;

/// <summary>
/// <c>DateTime.UtcNow</c> written INSIDE a query (a filter, an ExecuteUpdate) is
/// translated by Npgsql to <c>now()</c> — a <c>timestamp with time zone</c> that it
/// refuses to compare with or assign to our <c>timestamp without time zone</c>
/// columns (<see cref="UnspecifiedDateTime"/>). This translator runs before the
/// provider's and renders it as <c>timezone('UTC', now())</c>: the UTC wall clock
/// without a zone, exactly what SQLite's <c>datetime('now')</c> is — so every
/// query that says UtcNow behaves the same on both databases, written or not yet.
/// </summary>
public sealed class PostgresUtcNowTranslator(ISqlExpressionFactory sql, IRelationalTypeMappingSource types)
    : IMemberTranslator
{
    private static readonly MemberInfo UtcNow = typeof(DateTime).GetProperty(nameof(DateTime.UtcNow))!;

    public SqlExpression? Translate(SqlExpression? instance, MemberInfo member, Type returnType,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (member != UtcNow) return null;
        var now = sql.Function("now", Array.Empty<SqlExpression>(), nullable: false,
            argumentsPropagateNullability: Array.Empty<bool>(), typeof(DateTimeOffset),
            types.FindMapping("timestamp with time zone"));
        return sql.Function("timezone", new SqlExpression[] { sql.Constant("UTC"), now }, nullable: false,
            argumentsPropagateNullability: new[] { false, false }, typeof(DateTime),
            types.FindMapping("timestamp without time zone"));
    }
}

/// <summary>Registers <see cref="PostgresUtcNowTranslator"/> (plugin translators are tried before the provider's).</summary>
public sealed class PostgresUtcNowPlugin(ISqlExpressionFactory sql, IRelationalTypeMappingSource types)
    : IMemberTranslatorPlugin
{
    public IEnumerable<IMemberTranslator> Translators { get; } = new[] { new PostgresUtcNowTranslator(sql, types) };
}

/// <summary>
/// <c>EF.Functions.Like</c> is case-INSENSITIVE on SQLite (ASCII) and every search in
/// the app was written that way (contacts, conversations, message text,
/// notifications, the command palette). Postgres' LIKE is case-sensitive, so on
/// Postgres it becomes <c>lower(x) LIKE lower(pattern)</c> — one rule for every
/// call, present and future, instead of a per-call ILIKE someone will forget.
/// </summary>
public sealed class PostgresLikeTranslator(ISqlExpressionFactory sql, IRelationalTypeMappingSource types)
    : IMethodCallTranslator
{
    public SqlExpression? Translate(SqlExpression? instance, MethodInfo method, IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType != typeof(DbFunctionsExtensions) || method.Name != nameof(DbFunctionsExtensions.Like)
            || arguments.Count < 3)
            return null;
        var mapping = Microsoft.EntityFrameworkCore.Query.ExpressionExtensions.InferTypeMapping(arguments[1], arguments[2]) ?? types.FindMapping(typeof(string));
        SqlExpression Lower(SqlExpression e) => sql.Function("lower", new[] { sql.ApplyTypeMapping(e, mapping) },
            nullable: true, argumentsPropagateNullability: new[] { true }, typeof(string), mapping);
        var escape = arguments.Count > 3 ? sql.ApplyTypeMapping(arguments[3], mapping) : null;
        return sql.Like(Lower(arguments[1]), Lower(arguments[2]), escape);
    }
}

/// <summary>Registers <see cref="PostgresLikeTranslator"/> ahead of the provider's LIKE.</summary>
public sealed class PostgresLikePlugin(ISqlExpressionFactory sql, IRelationalTypeMappingSource types)
    : IMethodCallTranslatorPlugin
{
    public IEnumerable<IMethodCallTranslator> Translators { get; } = new[] { new PostgresLikeTranslator(sql, types) };
}

/// <summary>The options extension that adds both translators to a Postgres context's services.</summary>
public sealed class PostgresUtcNowExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.AddScoped<IMemberTranslatorPlugin, PostgresUtcNowPlugin>(); // scoped, like EF's own plugins
        services.AddScoped<IMethodCallTranslatorPlugin, PostgresLikePlugin>();
    }

    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["ChatBranch:PostgresUtcNow"] = "1";
    }
}
