using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChatBranch.Data.Ai.Learning;

/// <summary>
/// The text plumbing retrieval runs on: a tokenizer with English + Malay
/// stopwords and LIGHT stemming (both sides of a comparison go through the same
/// function, so a crude stem still matches its own family), the normalized form
/// lessons are deduplicated on, and the chunker long knowledge items are split
/// with. Pure functions, no I/O.
/// </summary>
public static class TextAnalysis
{
    /// <summary>Target chunk size (chars) — a chunk never exceeds <see cref="ChunkMax"/>.</summary>
    public const int ChunkTarget = 700;
    public const int ChunkMax = 800;
    public const int ChunkOverlap = 120;

    // English function words + chat filler. Question words are noise for
    // matching ("how", "what") — the content words carry the topic.
    private static readonly HashSet<string> EnglishStop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "if", "then", "else", "so", "of", "to", "in", "on", "at",
        "by", "for", "with", "about", "from", "into", "over", "after", "before", "up", "down", "out",
        "off", "again", "is", "am", "are", "was", "were", "be", "been", "being", "do", "does", "did",
        "doing", "have", "has", "had", "having", "i", "me", "my", "mine", "myself", "you", "your",
        "yours", "yourself", "u", "ur", "we", "us", "our", "ours", "he", "him", "his", "she", "her",
        "hers", "it", "its", "they", "them", "their", "theirs", "this", "that", "these", "those",
        "what", "which", "who", "whom", "whose", "when", "where", "why", "how", "can", "could",
        "will", "would", "shall", "should", "may", "might", "must", "there", "here", "any", "some",
        "all", "both", "each", "few", "more", "most", "other", "such", "no", "nor", "not", "only",
        "own", "same", "than", "too", "very", "just", "also", "please", "pls", "plz", "hi", "hello",
        "hey", "thanks", "thank", "thx", "ok", "okay", "yes", "yeah", "yep", "im", "dont", "doesnt",
        "cant", "wont", "isnt", "arent", "ill", "ive", "id", "youre", "whats", "hows", "lets", "get",
        "got", "want", "wanna", "like", "know", "tell", "need", "much", "many",
    };

    // Malay (Bahasa Malaysia) function words + chat filler / abbreviations.
    private static readonly HashSet<string> MalayStop = new(StringComparer.Ordinal)
    {
        "yang", "dan", "di", "ke", "dari", "daripada", "untuk", "bagi", "kepada", "pada", "dengan",
        "ini", "itu", "ni", "tu", "saya", "aku", "sy", "awak", "anda", "kamu", "kau", "encik", "cik",
        "tuan", "puan", "kami", "kita", "mereka", "dia", "beliau", "ada", "tak", "tidak", "x", "bukan",
        "nak", "hendak", "mahu", "mau", "apa", "apakah", "bila", "bilakah", "mana", "manakah",
        "bagaimana", "macam", "camne", "camana", "berapa", "berapakah", "siapa", "kenapa", "mengapa",
        "ialah", "adalah", "akan", "sudah", "dah", "telah", "sedang", "masih", "juga", "jugak", "lagi",
        "atau", "tetapi", "tapi", "kalau", "jika", "sebab", "kerana", "karena", "ya", "ye", "yer",
        "pun", "lah", "kah", "je", "jer", "sahaja", "saja", "boleh", "bole", "blh", "ok", "okey",
        "terima", "kasih", "tq", "salam", "hai", "helo", "sila", "tolong", "mohon", "minta", "nk",
        "dgn", "utk", "dlm", "dalam", "sini", "situ", "sana", "semua", "setiap", "banyak", "sikit",
        "sedikit", "sangat", "amat", "lebih", "kurang", "oleh", "seperti", "bahawa", "agar", "supaya",
    };

    private static readonly string[] MalayParticles = { "lah", "kah", "nya", "tah", "pun" };
    // "-i" is left alone on purpose: far more Malay words simply END in i (beli,
    // hari, cari) than carry the suffix, and stripping it splits families.
    private static readonly string[] MalaySuffixes = { "kan", "an" };
    private static readonly string[] MalayPrefixes =
        { "memper", "menge", "meng", "meny", "men", "mem", "me", "diper", "di", "ber", "be", "ter", "peng", "peny",
          "pen", "pem", "per", "pe", "ke", "se" };

    public static bool IsStopword(string token) => EnglishStop.Contains(token) || MalayStop.Contains(token);

    /// <summary>
    /// Lower-case word tokens (letters/digits), accents stripped, no stemming.
    /// Chinese / Japanese script has no spaces, so a run of CJK characters is
    /// emitted as overlapping BIGRAMS (吉隆坡 → 吉隆, 隆坡) — the standard cheap
    /// way to keyword-match those languages — and a lone CJK character as
    /// itself. A token also breaks wherever the script changes (5公斤 → 5, 公斤).
    /// </summary>
    public static List<string> Words(string? text)
    {
        var outv = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return outv;
        var sb = new StringBuilder();
        var cjk = new StringBuilder();
        void FlushWord() { if (sb.Length > 0) { outv.Add(sb.ToString()); sb.Clear(); } }
        void FlushCjk()
        {
            if (cjk.Length == 1) outv.Add(cjk.ToString());
            else for (var i = 0; i + 1 < cjk.Length; i++) outv.Add(cjk.ToString(i, 2));
            cjk.Clear();
        }
        foreach (var raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
            if (IsCjk(raw)) { FlushWord(); cjk.Append(raw); continue; }
            if (cjk.Length > 0 && raw != '\'' && raw != '’') FlushCjk();
            if (char.IsLetterOrDigit(raw)) sb.Append(char.ToLowerInvariant(raw));
            else if (raw == '\'' || raw == '’') continue; // "don't" → "dont"
            else FlushWord();
        }
        FlushWord();
        if (cjk.Length > 0) FlushCjk();
        return outv;
    }

    /// <summary>A Han ideograph or Japanese kana (scripts written without spaces).</summary>
    public static bool IsCjk(char c) =>
        c is >= '\u4E00' and <= '\u9FFF'     // CJK unified ideographs
          or >= '\u3400' and <= '\u4DBF'     // extension A
          or >= '\uF900' and <= '\uFAFF'     // compatibility ideographs
          or >= '\u3040' and <= '\u30FF';    // hiragana + katakana

    /// <summary>
    /// Content terms: words minus stopwords, lightly stemmed. One-letter tokens
    /// are dropped; numbers are kept (prices, model numbers, postcodes).
    /// </summary>
    public static List<string> Terms(string? text)
    {
        var outv = new List<string>();
        foreach (var w in Words(text))
        {
            if (w.Length == 1 && IsCjk(w[0])) { outv.Add(w); continue; } // 灯, 店: a word on its own
            if (w.Length < 2 || IsStopword(w)) continue;
            var stem = Stem(w);
            if (stem.Length < 2 || IsStopword(stem)) continue;
            outv.Add(stem);
        }
        return outv;
    }

    /// <summary>
    /// Light stemming for English + Malay. Deliberately crude — it only has to
    /// map a word family onto one key consistently: Malay particles (-lah, -kah,
    /// -nya), suffixes (-kan, -an) and up to two prefixes (meN-, ber-, di-,
    /// peN-, ter-…), then English inflections (-ing, -ed, -ies, -es, -s, -ly).
    /// Every strip keeps at least 3–4 characters, so short words survive intact.
    /// </summary>
    public static string Stem(string word)
    {
        var w = word;
        if (w.Length <= 3 || w.All(char.IsDigit)) return w;

        foreach (var p in MalayParticles)
            if (w.Length - p.Length >= 4 && w.EndsWith(p, StringComparison.Ordinal)) { w = w[..^p.Length]; break; }
        foreach (var s in MalaySuffixes)
            if (w.Length - s.Length >= 4 && w.EndsWith(s, StringComparison.Ordinal)) { w = w[..^s.Length]; break; }
        for (var round = 0; round < 2; round++)
        {
            var stripped = false;
            foreach (var p in MalayPrefixes)
                // a bare two-letter prefix (pe-, me-, di-…) needs a longer remainder,
                // so names like "Penang" survive
                if (w.Length - p.Length >= (p.Length <= 2 ? 5 : 4) && w.StartsWith(p, StringComparison.Ordinal))
                {
                    w = w[p.Length..];
                    stripped = true;
                    break;
                }
            if (!stripped) break;
        }

        if (w.Length >= 7 && w.EndsWith("ing", StringComparison.Ordinal)) w = w[..^3];
        else if (w.Length >= 6 && w.EndsWith("ed", StringComparison.Ordinal)) w = w[..^2];
        else if (w.Length >= 5 && w.EndsWith("ies", StringComparison.Ordinal)) w = w[..^3] + "y";
        else if (w.Length >= 6 && w.EndsWith("ly", StringComparison.Ordinal)) w = w[..^2];
        if (w.Length >= 5 && w.EndsWith("es", StringComparison.Ordinal) && "sxz".Contains(w[^3])) w = w[..^2];
        else if (w.Length >= 4 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)
                 && !w.EndsWith("us", StringComparison.Ordinal)) w = w[..^1];
        return w;
    }

    /// <summary>
    /// The form lessons are deduplicated on: content terms, sorted and
    /// de-duplicated, so word order, punctuation, casing and filler do not make
    /// two askings of the same question look different.
    /// </summary>
    public static string NormalizeQuestion(string? text)
    {
        var terms = Terms(text).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (terms.Count > 0) return string.Join(' ', terms);
        // all stopwords ("ok thanks") — fall back to the plain words
        return string.Join(' ', Words(text));
    }

    public static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    /// <summary>
    /// Split long text into ~<see cref="ChunkTarget"/>-char chunks on paragraph,
    /// then sentence, then word boundaries, each carrying the tail of the one
    /// before (~<see cref="ChunkOverlap"/> chars) so a fact straddling a boundary
    /// is still whole in one of them. Text up to <see cref="ChunkMax"/> is one chunk.
    /// </summary>
    public static List<string> Chunk(string? text, int target = ChunkTarget, int max = ChunkMax,
        int overlap = ChunkOverlap)
    {
        var clean = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (clean.Length == 0) return new List<string>();
        if (clean.Length <= max) return new List<string> { clean };

        var pieces = new List<string>();
        foreach (var para in clean.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (para.Length <= target) { pieces.Add(para); continue; }
            foreach (var sentence in Sentences(para))
            {
                if (sentence.Length <= target) { pieces.Add(sentence); continue; }
                pieces.AddRange(HardSplit(sentence, target));
            }
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (current.Length > 0 && current.Length + 1 + piece.Length > target)
            {
                chunks.Add(current.ToString().Trim());
                var tail = Tail(current.ToString(), overlap);
                current.Clear();
                if (tail.Length > 0 && tail.Length + 1 + piece.Length <= max) current.Append(tail).Append(' ');
            }
            if (current.Length > 0 && !char.IsWhiteSpace(current[^1])) current.Append('\n');
            current.Append(piece);
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        // a chunk can only exceed max through a tail + piece combination we refused above
        return chunks.Where(c => c.Length > 0).Select(c => c.Length <= max ? c : c[..max]).ToList();
    }

    private static IEnumerable<string> Sentences(string para)
    {
        var start = 0;
        for (var i = 0; i < para.Length; i++)
        {
            var c = para[i];
            var end = c is '。' or '！' or '？'
                || c is '.' or '!' or '?' or '\n' && (i + 1 == para.Length || char.IsWhiteSpace(para[i + 1]));
            if (!end) continue;
            var s = para[start..(i + 1)].Trim();
            if (s.Length > 0) yield return s;
            start = i + 1;
        }
        if (start < para.Length)
        {
            var rest = para[start..].Trim();
            if (rest.Length > 0) yield return rest;
        }
    }

    private static IEnumerable<string> HardSplit(string text, int size)
    {
        var i = 0;
        while (i < text.Length)
        {
            var len = Math.Min(size, text.Length - i);
            if (i + len < text.Length)
            {
                var space = text.LastIndexOf(' ', i + len - 1, len);
                if (space > i + size / 2) len = space - i;
            }
            yield return text.Substring(i, len).Trim();
            i += len;
        }
    }

    /// <summary>The last ~n chars of a chunk, starting at a word boundary.</summary>
    private static string Tail(string text, int n)
    {
        if (text.Length <= n) return text.Trim();
        var cut = text.Length - n;
        var space = text.IndexOf(' ', cut);
        return (space > 0 && space < text.Length - 1 ? text[(space + 1)..] : text[cut..]).Trim();
    }

    public static string Clip(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n].TrimEnd() + "…";
    }
}
