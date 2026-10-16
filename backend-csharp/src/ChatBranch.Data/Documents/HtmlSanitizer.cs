using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Documents;

/// <summary>
/// Cleans a custom layout's HTML so a document can never run code or reach the
/// network — on the STORED template (when a config is saved) and again on the
/// RENDERED page right before it is printed:
/// <list type="bullet">
/// <item>elements removed with their content: <c>script</c>, <c>iframe</c>,
/// <c>object</c>, <c>applet</c>, <c>frameset</c>, <c>noembed</c>, <c>portal</c>,
/// <c>fencedframe</c>; tags removed: <c>embed</c>, <c>frame</c>, <c>base</c>, every
/// <c>link</c>, every <c>meta</c> but charset / viewport;</item>
/// <item>attributes removed: every <c>on*</c> handler, <c>srcset</c> / <c>srcdoc</c>,
/// and every URL attribute (<c>src</c>, <c>href</c>, <c>poster</c>, <c>action</c>…)
/// whose value is not a <c>data:</c> URL, a <c>#fragment</c> (or <c>mailto:</c> /
/// <c>tel:</c> on a link) — so <c>javascript:</c>, <c>file:</c>, <c>http(s):</c> and
/// relative paths all go;</item>
/// <item>CSS (<c>&lt;style&gt;</c> blocks and <c>style</c> attributes): <c>@import</c>
/// rules dropped, <c>url(…)</c> other than <c>data:</c> emptied, <c>image-set(…)</c>
/// replaced — <c>&lt;style&gt;</c> itself is kept.</item>
/// </list>
/// In TEMPLATE mode a value that holds a Handlebars expression (<c>src="{{{business.logo}}}"</c>)
/// is kept: it is decided on the rendered page, where only the server-built logo
/// (a data: URL) survives. This is one layer of three — the printer also injects a
/// Content-Security-Policy that blocks scripts and every fetch, and runs with every
/// host name unresolvable.
/// <para>EMAIL mode (an inbound mail shown in the inbox) differs in three ways: a
/// link may go to an http(s) page; a remote image's <c>src</c> becomes
/// <c>data-cb-src</c>, so nothing is fetched until a person asks to show images
/// (no tracking pixel fires on open); and form controls go (a mail can't draw a
/// login box inside the dashboard).</para>
/// </summary>
public static class HtmlSanitizer
{
    private static readonly string[] DropWithContent =
        { "script", "iframe", "object", "applet", "frameset", "noembed", "portal", "fencedframe" };
    private static readonly string[] DropTag = { "embed", "frame", "base", "link" };
    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "href", "xlink:href", "action", "formaction", "poster", "background", "data", "cite", "longdesc",
        "ping", "lowsrc", "dynsrc", "manifest", "codebase", "archive", "usemap", "profile", "classid", "icon",
        "itemtype", "content-src",
    };
    private static readonly HashSet<string> AlwaysDrop = new(StringComparer.OrdinalIgnoreCase)
        { "srcset", "imagesrcset", "srcdoc", "http-equiv", "autofocus" };

    private static readonly Regex Blocks = new(
        $@"<({string.Join("|", DropWithContent)})\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StrayTags = new(
        $@"</?({string.Join("|", DropWithContent.Concat(DropTag))})\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FormControls = new(@"</?(form|input|button|select|option|textarea)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Meta = new(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyleBlock = new(@"(<style\b[^>]*>)(.*?)(</style\s*>|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The cleaned HTML (idempotent: cleaning it again changes nothing).</summary>
    public static string Sanitize(string? html, bool template = false, bool email = false)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = html;
        // element removal until nothing changes (a nested "<scr<script>ipt>" can't reassemble)
        for (var i = 0; i < 8; i++)
        {
            // a removed block keeps its line breaks, so error line numbers still match the editor
            var next = StrayTags.Replace(Blocks.Replace(s, m => new string('\n', m.Value.Count(c => c == '\n'))), "");
            if (next == s) break;
            s = next;
        }
        if (email) s = FormControls.Replace(s, "");
        s = Meta.Replace(s, m => Regex.IsMatch(m.Value, @"\b(charset|name\s*=\s*[""']?viewport)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(m.Value, @"http-equiv", RegexOptions.IgnoreCase) ? m.Value : "");
        s = StyleBlock.Replace(s, m => m.Groups[1].Value + Css(m.Groups[2].Value, template) + m.Groups[3].Value);
        return Attributes(s, template, email);
    }

    // =========================================================================
    // attributes: walk every start tag, splice out what isn't allowed
    // =========================================================================

    private static string Attributes(string s, bool template, bool email)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var lt = s.IndexOf('<', i);
            if (lt < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, lt - i);
            // raw-text content (style) is copied as it is — it was cleaned as CSS
            if (StartsWithTag(s, lt, "style"))
            {
                var tagEnd = TagEnd(s, lt);
                if (tagEnd < 0) { sb.Append(Tag(s, lt, s.Length - 1, template, email, out _)); break; }
                sb.Append(Tag(s, lt, tagEnd, template, email, out _));
                var close = s.IndexOf("</style", tagEnd, StringComparison.OrdinalIgnoreCase);
                var contentStart = tagEnd + 1;
                var contentEnd = close < 0 ? s.Length : close;
                if (contentEnd > contentStart) sb.Append(s, contentStart, contentEnd - contentStart);
                i = contentEnd;
                continue;
            }
            if (lt + 1 < s.Length && char.IsAsciiLetter(s[lt + 1]))
            {
                var end = TagEnd(s, lt);
                if (end < 0) { sb.Append(Tag(s, lt, s.Length - 1, template, email, out _)); break; }
                sb.Append(Tag(s, lt, end, template, email, out _));
                i = end + 1;
                continue;
            }
            sb.Append('<');
            i = lt + 1;
        }
        return sb.ToString();
    }

    private static bool StartsWithTag(string s, int lt, string name) =>
        lt + 1 + name.Length <= s.Length
        && string.Compare(s, lt + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (lt + 1 + name.Length == s.Length || !char.IsAsciiLetterOrDigit(s[lt + 1 + name.Length]));

    /// <summary>The index of the '&gt;' closing the tag at <paramref name="lt"/> (quotes and {{…}} respected), -1 = none.</summary>
    private static int TagEnd(string s, int lt)
    {
        char quote = '\0';
        for (var j = lt + 1; j < s.Length; j++)
        {
            var ch = s[j];
            if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
            if (ch == '{' && j + 1 < s.Length && s[j + 1] == '{')
            {
                var close = s.IndexOf("}}", j + 2, StringComparison.Ordinal);
                if (close < 0) return -1;
                j = close + 1;
                if (j + 1 < s.Length && s[j + 1] == '}') j++;
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; continue; }
            if (ch == '>') return j;
        }
        return -1;
    }

    /// <summary>One start tag with its disallowed attributes removed.</summary>
    private static string Tag(string s, int lt, int end, bool template, bool email, out string name)
    {
        var j = lt + 1;
        while (j <= end && (char.IsAsciiLetterOrDigit(s[j]) || s[j] is '-' or ':')) j++;
        name = s[(lt + 1)..j].ToLowerInvariant();
        var sb = new StringBuilder();
        sb.Append(s, lt, j - lt);
        while (j <= end)
        {
            var ch = s[j];
            if (ch == '>' || char.IsWhiteSpace(ch) || ch == '/') { sb.Append(ch); j++; continue; }
            if (ch == '{' && j + 1 <= end && s[j + 1] == '{')
            {
                // a Handlebars expression in the tag ({{#if x}}class="a"{{/if}}) — kept as written
                var close = s.IndexOf("}}", j + 2, StringComparison.Ordinal);
                var stop = close < 0 ? end : Math.Min(end, close + 1 + (close + 2 < s.Length && s[close + 2] == '}' ? 1 : 0));
                sb.Append(s, j, stop - j + 1);
                j = stop + 1;
                continue;
            }
            // an attribute: name [= value]
            var start = j;
            while (j <= end && !char.IsWhiteSpace(s[j]) && s[j] is not ('=' or '>' or '/' or '"' or '\'')
                   && !(s[j] == '{' && j + 1 <= end && s[j + 1] == '{')) j++;
            if (j == start) { sb.Append(s[j]); j++; continue; } // a stray quote
            var attr = s[start..j];
            var k = j;
            while (k <= end && char.IsWhiteSpace(s[k])) k++;
            string? value = null;
            var valueEnd = j;
            if (k <= end && s[k] == '=')
            {
                k++;
                while (k <= end && char.IsWhiteSpace(s[k])) k++;
                if (k <= end && s[k] is '"' or '\'')
                {
                    var q = s[k];
                    var close = s.IndexOf(q, k + 1);
                    if (close < 0 || close > end) close = end;
                    value = s[(k + 1)..close];
                    valueEnd = close + 1;
                }
                else
                {
                    var v = k;
                    while (v <= end && !char.IsWhiteSpace(s[v]) && s[v] != '>') v++;
                    value = s[k..v];
                    valueEnd = v;
                }
            }
            var whole = s[start..Math.Min(valueEnd, end + 1)];
            var keep = Keep(name, attr, value, template, email, out var replacement);
            if (replacement != null) sb.Append(replacement);
            else if (keep) sb.Append(whole);
            j = Math.Max(valueEnd, j);
        }
        return sb.ToString();
    }

    private static bool Keep(string tag, string attr, string? value, bool template, bool email, out string? replacement)
    {
        replacement = null;
        var a = attr.ToLowerInvariant();
        if (a.StartsWith("on")) return false;
        if (AlwaysDrop.Contains(a)) return false;
        if (value == null) return true;
        var hasExpr = template && value.Contains("{{");
        if (a == "style")
        {
            if (hasExpr) return true;
            var decoded = WebUtility.HtmlDecode(value);
            var clean = Css(decoded, template: false);
            if (clean == decoded) return true;
            replacement = $"style=\"{Escape(clean)}\"";
            return true;
        }
        if (!UrlAttributes.Contains(a)) return true;
        if (hasExpr) return true;
        if (SafeUrl(tag, a, value)) return true;
        if (email && IsWeb(value))
        {
            if (tag == "a" && a == "href") return true;
            // held back: the inbox shows it only when a person asks for images
            if (tag == "img" && a == "src") { replacement = $"data-cb-src=\"{Escape(WebUtility.HtmlDecode(value))}\""; return true; }
        }
        return false;
    }

    /// <summary>data: anywhere; #fragment on links / references; mailto: / tel: on a link. Nothing else.</summary>
    public static bool SafeUrl(string tag, string attr, string raw)
    {
        var v = new string(WebUtility.HtmlDecode(raw).Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray())
            .ToLowerInvariant();
        if (v.Length == 0) return true;
        if (v.StartsWith("data:")) return !v.StartsWith("data:text/html") && !v.StartsWith("data:application/xhtml");
        if (v.StartsWith('#')) return attr is "href" or "xlink:href" or "usemap";
        if (tag == "a" && attr == "href" && (v.StartsWith("mailto:") || v.StartsWith("tel:"))) return true;
        return false;
    }

    private static bool IsWeb(string raw)
    {
        var v = WebUtility.HtmlDecode(raw).Trim();
        return Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");

    // =========================================================================
    // CSS: no @import, no remote url(), no image-set()
    // =========================================================================

    private static readonly Regex CssHexEscape = new(@"\\([0-9a-fA-F]{1,6})\s?", RegexOptions.Compiled);
    private static readonly Regex CssCharEscape = new(@"\\([g-zG-Z(])", RegexOptions.Compiled);
    private static readonly Regex Import = new(@"@import\b[^;]*;?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Url = new(@"url\(\s*(?:""([^""]*)""|'([^']*)'|([^)]*?))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ImageSet = new(@"(?:-webkit-)?image-set\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Css(string css, bool template)
    {
        if (string.IsNullOrEmpty(css)) return css;
        // letters written as CSS escapes (u\72l( = url() can't hide a fetch
        var s = CssHexEscape.Replace(css, m =>
        {
            var code = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return code is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '(' or ':' or '@' or '/' ? ((char)code).ToString() : m.Value;
        });
        s = CssCharEscape.Replace(s, m => m.Groups[1].Value);
        s = Import.Replace(s, "");
        s = Url.Replace(s, m =>
        {
            var arg = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (template && arg.Contains("{{")) return m.Value;
            var v = arg.Trim().ToLowerInvariant();
            return v.StartsWith("data:") && !v.StartsWith("data:text/html") ? m.Value : "url()";
        });
        // image-set("x.png" 1x) fetches plain strings: replace the whole function
        var sb = new StringBuilder();
        var i = 0;
        foreach (Match m in ImageSet.Matches(s))
        {
            if (m.Index < i) continue;
            sb.Append(s, i, m.Index - i);
            var depth = 0;
            var j = m.Index + m.Length - 1;
            for (; j < s.Length; j++)
            {
                if (s[j] == '(') depth++;
                else if (s[j] == ')' && --depth == 0) break;
            }
            sb.Append("none");
            i = Math.Min(s.Length, j + 1);
        }
        sb.Append(s, i, s.Length - i);
        return sb.ToString();
    }
}
