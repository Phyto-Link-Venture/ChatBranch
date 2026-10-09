using System.Text;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Contacts;

/// <summary>One contact card out of a .vcf file.</summary>
public sealed class VCard
{
    public string FullName { get; set; } = "";
    public string GivenName { get; set; } = "";
    public string FamilyName { get; set; } = "";
    /// <summary>Every number, the preferred one first.</summary>
    public List<string> Phones { get; } = new();
    public List<string> Emails { get; } = new();
    public string Organization { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>yyyy-MM-dd, or --MM-dd when the card has no year.</summary>
    public string Birthday { get; set; } = "";

    public string DisplayName =>
        FullName.Length > 0 ? FullName : string.Join(' ', new[] { GivenName, FamilyName }.Where(s => s.Length > 0));
}

/// <summary>
/// vCard 2.1 / 3.0 / 4.0 (RFC 2426 / 6350 and the 2.1 spec phones still
/// export): several cards per file, line folding, groups (<c>item1.TEL</c>),
/// bare 2.1 parameters (<c>TEL;CELL;PREF:</c>), QUOTED-PRINTABLE with its soft
/// line breaks and a CHARSET (Android's export), backslash escapes, <c>tel:</c>
/// URIs (4.0) and PREF ordering. Photos and everything else are skipped.
/// </summary>
public static class VCardParser
{
    static VCardParser()
    {
        // legacy charsets (Shift_JIS, GBK, windows-125x) in old phone exports
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    /// <summary>Decode the file: a BOM wins, else UTF-8 (QP payloads are re-decoded per CHARSET).</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var text = Encoding.UTF8.GetString(bytes);
        return text.TrimStart('﻿');
    }

    public static List<VCard> Parse(string text)
    {
        var cards = new List<VCard>();
        VCard? cur = null;
        var prefPhones = new List<(string Value, bool Pref)>();
        var prefEmails = new List<(string Value, bool Pref)>();

        foreach (var line in Unfold(text))
        {
            var colon = ValueSeparator(line);
            if (colon <= 0) continue;
            var head = line[..colon];
            var rawValue = line[(colon + 1)..];
            var parts = SplitParams(head);
            var name = parts[0];
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];   // item1.TEL → TEL
            name = name.Trim().ToUpperInvariant();
            var parameters = parts.Skip(1).ToList();

            if (name == "BEGIN" && rawValue.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                cur = new VCard();
                prefPhones.Clear();
                prefEmails.Clear();
                continue;
            }
            if (name == "END" && rawValue.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (cur != null)
                {
                    cur.Phones.AddRange(Ordered(prefPhones));
                    cur.Emails.AddRange(Ordered(prefEmails));
                    if (cur.DisplayName.Length > 0 || cur.Phones.Count > 0 || cur.Emails.Count > 0) cards.Add(cur);
                }
                cur = null;
                continue;
            }
            if (cur == null) continue;

            var value = DecodeValue(rawValue, parameters);
            switch (name)
            {
                case "FN":
                    cur.FullName = Unescape(value).Trim();
                    break;
                case "N":
                {
                    var n = SplitStructured(value);
                    cur.FamilyName = n.ElementAtOrDefault(0)?.Trim() ?? "";
                    cur.GivenName = string.Join(' ', new[] { n.ElementAtOrDefault(1), n.ElementAtOrDefault(2) }
                        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
                    break;
                }
                case "TEL":
                {
                    var v = Unescape(value).Trim();
                    if (v.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) v = v[4..];
                    if (v.Length > 0) prefPhones.Add((v, IsPref(parameters)));
                    break;
                }
                case "EMAIL":
                {
                    var v = Unescape(value).Trim();
                    if (v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) v = v[7..];
                    if (v.Length > 0) prefEmails.Add((v, IsPref(parameters)));
                    break;
                }
                case "ORG":
                    cur.Organization = SplitStructured(value).FirstOrDefault()?.Trim() ?? "";
                    break;
                case "NOTE":
                    cur.Note = Unescape(value).Trim();
                    break;
                case "BDAY":
                    cur.Birthday = Birthday(Unescape(value).Trim());
                    break;
            }
        }
        return cards;
    }

    private static IEnumerable<string> Ordered(List<(string Value, bool Pref)> items) =>
        items.Where(i => i.Pref).Concat(items.Where(i => !i.Pref)).Select(i => i.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Logical lines: RFC folding (CRLF + space/tab) is joined, and a
    /// QUOTED-PRINTABLE value that ends in a soft break "=" swallows the next
    /// physical line (2.1 exports do not indent those).
    /// </summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var physical = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var have = false;
        for (var i = 0; i < physical.Length; i++)
        {
            var line = physical[i];
            if (have && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                sb.Append(line, 1, line.Length - 1);
                continue;
            }
            if (have && IsQp(sb) && sb.Length > 0 && sb[^1] == '=')
            {
                sb.Length -= 1;
                sb.Append(line.TrimStart());
                continue;
            }
            if (have) yield return sb.ToString();
            sb.Clear();
            sb.Append(line);
            have = true;
        }
        if (have && sb.Length > 0) yield return sb.ToString();
    }

    private static bool IsQp(StringBuilder sb)
    {
        var s = sb.ToString();
        var colon = ValueSeparator(s);
        var head = colon > 0 ? s[..colon] : s;
        return head.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The ':' between parameters and value (a quoted parameter may hold one).</summary>
    private static int ValueSeparator(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }
        return -1;
    }

    private static List<string> SplitParams(string head)
    {
        var outv = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var ch in head)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ';' && !quoted) { outv.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        outv.Add(sb.ToString());
        return outv;
    }

    private static bool IsPref(List<string> parameters) =>
        parameters.Any(p =>
        {
            var u = p.Trim().ToUpperInvariant();
            if (u == "PREF" || u.StartsWith("PREF=")) return true;
            if (!u.StartsWith("TYPE=")) return false;
            return u[5..].Split(',').Any(t => t.Trim() == "PREF");
        });

    private static string DecodeValue(string raw, List<string> parameters)
    {
        string? encoding = null, charset = null;
        foreach (var p in parameters)
        {
            var eq = p.IndexOf('=');
            var k = (eq < 0 ? p : p[..eq]).Trim().ToUpperInvariant();
            var v = eq < 0 ? "" : p[(eq + 1)..].Trim();
            if (eq < 0 && k is "QUOTED-PRINTABLE" or "BASE64" or "B") encoding = k;
            else if (k == "ENCODING") encoding = v.ToUpperInvariant();
            else if (k == "CHARSET") charset = v;
        }
        if (encoding != "QUOTED-PRINTABLE") return raw;
        var bytes = QuotedPrintable(raw);
        Encoding enc;
        try { enc = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch { enc = Encoding.UTF8; }
        return enc.GetString(bytes);
    }

    private static byte[] QuotedPrintable(string s)
    {
        var outv = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                outv.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c == '=' && i == s.Length - 1) { /* trailing soft break */ }
            else foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) outv.Add(b);
        }
        return outv.ToArray();
    }

    /// <summary>Split a structured value (N, ORG) on unescaped ';'.</summary>
    private static List<string> SplitStructured(string value)
    {
        var outv = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length) { sb.Append(value[i]).Append(value[i + 1]); i++; continue; }
            if (value[i] == ';') { outv.Add(Unescape(sb.ToString())); sb.Clear(); continue; }
            sb.Append(value[i]);
        }
        outv.Add(Unescape(sb.ToString()));
        return outv;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[i + 1];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
                i++;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string Birthday(string v)
    {
        if (v.Length == 0) return "";
        var m = Regex.Match(v, @"^(\d{4})-?(\d{2})-?(\d{2})");
        if (m.Success) return $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}";
        m = Regex.Match(v, @"^--(\d{2})-?(\d{2})");
        if (m.Success) return $"--{m.Groups[1].Value}-{m.Groups[2].Value}";
        return v.Length <= 20 ? v : "";
    }
}
