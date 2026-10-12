using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatBranch.Api;

/// <summary>
/// Every <see cref="DateTime"/> the API writes is UTC and says so ("…Z").
/// Timestamps are stored as UTC, but SQLite and Postgres both read them back with
/// <see cref="DateTimeKind.Unspecified"/> (<c>UnspecifiedDateTime</c>), and the
/// default serializer then writes no offset — so a browser read
/// "2026-10-12T05:30:00" as ITS local time and showed a notification sent at
/// 1:30 PM in Kuala Lumpur as 5:30 AM. Unspecified is UTC here by convention;
/// a Local value is converted. Times meant as a wall clock (a booking's local
/// start, an org-local day) are sent as STRINGS, never as a DateTime, so this
/// never shifts them. Reading is unchanged.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(AsUtc(value));

    /// <summary>The value as a UTC DateTime (Unspecified = already UTC).</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
