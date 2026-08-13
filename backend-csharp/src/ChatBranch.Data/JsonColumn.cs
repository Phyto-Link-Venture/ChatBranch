using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChatBranch.Data;

/// <summary>
/// Reusable EF Core value converters + comparers for Django JSONField columns.
/// Django stores them as TEXT holding JSON; we round-trip through the same
/// representation so the C# backend can read/write the existing SQLite file.
/// </summary>
public static class JsonColumn
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static ValueConverter<T, string> Converter<T>() => new(
        v => Serialize(v),
        v => Deserialize<T>(v));

    // Compare by serialized value so EF detects mutations to the collection.
    public static ValueComparer<T> Comparer<T>() => new(
        (a, b) => Serialize(a) == Serialize(b),
        v => v == null ? 0 : Serialize(v).GetHashCode(),
        v => Deserialize<T>(Serialize(v)));

    private static string Serialize<T>(T? value) =>
        value == null ? DefaultJson<T>() : JsonSerializer.Serialize(value, Opts);

    private static T Deserialize<T>(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return JsonSerializer.Deserialize<T>(DefaultJson<T>(), Opts)!;
        try { return JsonSerializer.Deserialize<T>(raw, Opts) ?? Fresh<T>(); }
        catch { return Fresh<T>(); }
    }

    private static string DefaultJson<T>() =>
        typeof(T).IsGenericType &&
        typeof(T).GetGenericTypeDefinition() == typeof(Dictionary<,>) ? "{}" : "[]";

    private static T Fresh<T>() => JsonSerializer.Deserialize<T>(DefaultJson<T>(), Opts)!;
}
