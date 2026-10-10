using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Ai;

/// <summary>
/// The guard behind the built-in check <c>builtin:no_false_booking_claim</c>: a
/// reply may only say an appointment HAS BEEN booked / cancelled when this turn's
/// calendar tool actually did it (a <c>*_book</c> result with status
/// <c>reserved</c>, a <c>*_cancel</c> result with <c>cancel_pending</c>). A model
/// once told a customer "your appointment has been cancelled" without calling the
/// tool, and the booking stayed active. Quoting an existing booking ("your booking
/// is on Monday 1:30 PM") has no claim verb and passes.
/// </summary>
public static class BookingClaims
{
    private static readonly Regex CancelClaim = new(
        @"\b(has been|have been|is|was|successfully)\s+(cancel+ed)\b|\bcancel+ed (your|the)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BookClaim = new(
        @"\b(has been|is|successfully)\s+(booked|confirmed|reserved)\b|\bbooking is confirmed\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"made" / "cancelled" when the draft claims something no tool did; null when it's honest.</summary>
    public static string? Unbacked(string draft, bool booked, bool cancelled)
    {
        if (string.IsNullOrWhiteSpace(draft)) return null;
        if (!cancelled && CancelClaim.IsMatch(draft)) return "cancelled";
        if (!booked && BookClaim.IsMatch(draft)) return "made";
        return null;
    }

    /// <summary>Does a tool's JSON result carry <c>"status": want</c>?</summary>
    public static bool HasStatus(string? resultJson, string want)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("status", out var st)
                   && st.ValueKind == JsonValueKind.String
                   && string.Equals(st.GetString(), want, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
