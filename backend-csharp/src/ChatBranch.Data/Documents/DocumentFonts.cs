using System.Reflection;
using System.Text.RegularExpressions;
using PdfSharp.Fonts;

namespace ChatBranch.Data.Documents;

/// <summary>
/// The fonts generated documents are written in. PDFsharp's cross-platform
/// build reads no system fonts, so the text face — Liberation Sans Regular +
/// Bold (SIL OFL 1.1, <c>Documents/Fonts/LICENSE-OFL.txt</c>) — is EMBEDDED in
/// this assembly: a bare container renders the same PDF as a laptop. It covers
/// English, Bahasa Melayu and other Latin / Greek / Cyrillic text.
///
/// Chinese, Japanese and Korean need a CJK font, far too large to ship (10 MB+).
/// When one is available as a TrueType file — env <c>DOCUMENT_CJK_FONT</c>
/// (a path to a .ttf), else a few common system locations — a document whose
/// text contains CJK characters is written entirely in it (CJK fonts carry
/// Latin glyphs too). With none, <see cref="CjkAvailable"/> is false and the
/// document tools refuse CJK text with a clear message rather than printing
/// empty boxes.
/// </summary>
public static class DocumentFonts
{
    public const string Sans = "DocSans";
    public const string Cjk = "DocCJK";

    private static readonly object Gate = new();
    private static bool _installed;
    private static string? _cjkPath;
    private static byte[]? _cjkBytes;

    private static readonly string[] CjkCandidates =
    {
        "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttf",
        "/usr/share/fonts/truetype/arphic/uming.ttf",
        "/usr/share/fonts/truetype/noto/NotoSansSC-Regular.ttf",
        "/usr/share/fonts/opentype/ipafont-gothic/ipag.ttf",
        "/usr/share/fonts/truetype/fonts-japanese-gothic.ttf",
    };

    private static readonly Regex CjkChars = new(
        @"[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsHiragana}\p{IsKatakana}"
        + @"\p{IsHangulSyllables}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}\p{IsCJKCompatibilityIdeographs}]",
        RegexOptions.Compiled);

    public static bool HasCjk(string? text) => !string.IsNullOrEmpty(text) && CjkChars.IsMatch(text);

    /// <summary>A CJK TrueType font this process can embed (env first, then common paths).</summary>
    public static bool CjkAvailable
    {
        get
        {
            EnsureInstalled();
            return _cjkPath != null;
        }
    }

    /// <summary>Install the resolver once per process (PDFsharp's resolver is global).</summary>
    public static void EnsureInstalled()
    {
        if (_installed) return;
        lock (Gate)
        {
            if (_installed) return;
            _cjkPath = FindCjk();
            if (GlobalFontSettings.FontResolver is not Resolver)
            {
                try { GlobalFontSettings.FontResolver = new Resolver(); }
                catch (InvalidOperationException) { /* already set by another renderer in this process */ }
            }
            _installed = true;
        }
    }

    private static string? FindCjk()
    {
        var env = (Environment.GetEnvironmentVariable("DOCUMENT_CJK_FONT") ?? "").Trim();
        if (env.Length > 0) return File.Exists(env) && IsTrueType(env) ? env : null;
        return CjkCandidates.FirstOrDefault(p => File.Exists(p) && IsTrueType(p));
    }

    /// <summary>PDFsharp embeds TrueType outlines only (not CFF .otf, not .ttc collections).</summary>
    private static bool IsTrueType(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[4];
            if (f.Read(head, 0, 4) != 4) return false;
            return head[0] == 0x00 && head[1] == 0x01 && head[2] == 0x00 && head[3] == 0x00;
        }
        catch { return false; }
    }

    private static byte[] Embedded(string file)
    {
        var asm = typeof(DocumentFonts).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(file, StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private sealed class Resolver : IFontResolver
    {
        private readonly Lazy<byte[]> _regular = new(() => Embedded("LiberationSans-Regular.ttf"));
        private readonly Lazy<byte[]> _bold = new(() => Embedded("LiberationSans-Bold.ttf"));

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            if (string.Equals(familyName, Cjk, StringComparison.OrdinalIgnoreCase) && _cjkPath != null)
                return new FontResolverInfo("cjk", bold, italic);
            // everything else (incl. MigraDoc's default "Arial"/"Courier New") → the embedded sans;
            // italics are simulated (no italic face is shipped)
            return bold ? new FontResolverInfo("sans-bold", false, italic) : new FontResolverInfo("sans", false, italic);
        }

        public byte[]? GetFont(string faceName) => faceName switch
        {
            "sans-bold" => _bold.Value,
            "cjk" => _cjkBytes ??= File.ReadAllBytes(_cjkPath!),
            _ => _regular.Value,
        };
    }
}
