using ChatBranch.Data.Stores;

namespace ChatBranch.Data.Contacts;

/// <summary>
/// Phone numbers → E.164 with the organization's default country code
/// (Setting <c>default_country_code</c>, default <c>60</c> — Malaysia). The ONE
/// normaliser behind imports, identity discovery and the unknown-customer
/// parser, so "012-345 6789", "60123456789" and "+60 12 345 6789" all become
/// <c>+60123456789</c>. Matching between numbers still goes through
/// <see cref="PhoneMatch"/> (last 8 digits), which this never changes.
/// </summary>
public static class PhoneNormalizer
{
    public const string DefaultCountryCode = "60";

    /// <summary>The configured default country code (digits only), else 60.</summary>
    public static string CountryCode(ScopingService sc, int? entityId = null)
    {
        string raw;
        try { raw = sc.SettingValue("default_country_code", entityId); }
        catch { raw = ""; }
        var d = PhoneMatch.Digits(raw);
        return d.Length is >= 1 and <= 4 ? d : DefaultCountryCode;
    }

    /// <summary>
    /// E.164 (<c>+60123456789</c>), or "" when the input is not a usable number
    /// (fewer than 7 digits, more than 15, or letters mixed in).
    /// <list type="bullet">
    /// <item><c>+…</c> / <c>00…</c> — already international;</item>
    /// <item><c>0…</c> — a national number: the trunk 0 becomes the country code;</item>
    /// <item>starts with the country code and is long enough — international without the +;</item>
    /// <item>up to 10 digits otherwise — national without the trunk 0 (MY mobiles are 9–10);</item>
    /// <item>longer — some other country's number written without the +.</item>
    /// </list>
    /// </summary>
    public static string ToE164(string? raw, string? countryCode = null)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        // drop an extension ("…x12", "… ext 12") — a dialable line is enough
        var cut = s.IndexOfAny(new[] { 'x', 'X', ';', ',' });
        if (cut > 0) s = s[..cut];
        var lower = s.ToLowerInvariant();
        var ext = lower.IndexOf("ext", StringComparison.Ordinal);
        if (ext > 0) s = s[..ext];
        // anything but digits, spaces and the usual punctuation = not a phone number
        if (s.Any(ch => char.IsLetter(ch))) return "";
        var cc = PhoneMatch.Digits(countryCode);
        if (cc.Length == 0) cc = DefaultCountryCode;

        var plus = s.TrimStart().StartsWith('+');
        var d = PhoneMatch.Digits(s);
        if (d.Length < 7) return "";
        string full;
        if (plus) full = d;
        else if (d.StartsWith("00")) full = d[2..];
        else if (d.StartsWith('0')) full = cc + d[1..];
        else if (d.StartsWith(cc) && d.Length >= cc.Length + 8) full = d;
        else if (d.Length <= 10) full = cc + d;
        else full = d;
        return full.Length is >= 8 and <= 15 ? "+" + full : "";
    }

    /// <summary>Digits of the E.164 form (a WhatsApp wa_id), or "".</summary>
    public static string WaId(string? raw, string? countryCode = null) =>
        PhoneMatch.Digits(ToE164(raw, countryCode));
}
