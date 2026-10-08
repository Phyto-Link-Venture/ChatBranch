using ChatBranch.Domain;

namespace ChatBranch.Data.Stores;

/// <summary>
/// The ONE phone-identity rule: two numbers are the same person when their last
/// 8 digits agree (punctuation, spaces, a leading <c>+</c> or a trunk prefix
/// never matter). Inbound contact linking, the record-sync contact matcher and
/// the duplicate-phone guards all use it, so "the same number" means the same
/// thing on every path. Numbers with fewer than 8 digits never match anything.
/// </summary>
public static class PhoneMatch
{
    public const int TailDigits = 8;

    /// <summary>Digits only — "+60 12-345 6789" → "60123456789".</summary>
    public static string Digits(string? raw) =>
        new((raw ?? "").Where(char.IsDigit).ToArray());

    /// <summary>The matching key (last 8 digits), or null when the number is too short to identify anyone.</summary>
    public static string? Tail(string? raw)
    {
        var d = Digits(raw);
        return d.Length >= TailDigits ? d[^TailDigits..] : null;
    }

    public static bool Same(string? a, string? b) =>
        Tail(a) is string ta && ta == Tail(b);

    /// <summary>The first contact (lowest id) whose phone matches, or null.</summary>
    public static Contact? Find(IEnumerable<Contact> contacts, string? phone)
    {
        var tail = Tail(phone);
        if (tail == null) return null;
        return contacts.OrderBy(c => c.Id).FirstOrDefault(c => Tail(c.Phone) == tail);
    }
}
