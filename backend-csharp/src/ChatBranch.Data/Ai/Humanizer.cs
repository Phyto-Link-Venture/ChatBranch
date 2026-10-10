using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Pure text rules behind "act like a person": splitting a long reply into a few
/// chat-sized bubbles, how long a person would take to type each one, spotting
/// a message that is only an acknowledgement, a reply that leaves the
/// conversation open, and wording that makes a reply worth a human look.
/// Deterministic and side-effect free so every rule is unit-testable.
/// </summary>
public static class Humanizer
{
    public const int MaxBubbles = 3;

    private static readonly Regex Paragraphs = new(@"\n\s*\n", RegexOptions.Compiled);

    // A sentence ends at . ! ? (optionally followed by a closing quote/bracket)
    // when whitespace and an upper-case letter, digit or opening quote follow —
    // so "3.5", "e.g. this" and URLs stay whole.
    private static readonly Regex SentenceEnd =
        new(@"(?<=[.!?…][""')\]]?)\s+(?=[\p{Lu}\d""'(])", RegexOptions.Compiled);

    /// <summary>
    /// Split a reply into 1–<paramref name="maxBubbles"/> messages at paragraph,
    /// then sentence, boundaries. <c>off</c> never splits; <c>auto</c> splits only
    /// text longer than <paramref name="maxChars"/>; <c>always</c> also separates
    /// paragraphs of a short reply (and a single long-ish paragraph at its middle
    /// sentence). Never cuts inside a sentence.
    /// </summary>
    public static List<string> Split(string text, string mode, int maxChars, int maxBubbles = MaxBubbles)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return new List<string>();
        maxBubbles = Math.Max(1, maxBubbles);
        maxChars = Math.Max(40, maxChars);
        if (mode == "off" || maxBubbles == 1) return new List<string> { t };

        var paragraphs = Paragraphs.Split(t).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        if (mode == "always")
        {
            if (paragraphs.Count > 1 && t.Length <= maxChars)
                return Rebalance(paragraphs.Select(p => (p, "\n\n")).ToList(), maxBubbles);
            if (paragraphs.Count == 1 && t.Length <= maxChars)
            {
                var sentences = Sentences(t);
                if (sentences.Count < 2 || t.Length < 60) return new List<string> { t };
                return HalveAtSentence(sentences);
            }
        }
        else if (t.Length <= maxChars)
        {
            return new List<string> { t };
        }

        // Long text: paragraphs as units, over-long paragraphs broken into sentences.
        var units = new List<(string Text, string Joiner)>();
        foreach (var p in paragraphs)
        {
            if (p.Length <= maxChars) { units.Add((p, "\n\n")); continue; }
            var sentences = Sentences(p);
            for (var i = 0; i < sentences.Count; i++)
                units.Add((sentences[i], i == sentences.Count - 1 ? "\n\n" : " "));
        }

        var budget = maxChars;
        List<string> packed = Pack(units, budget);
        for (var guard = 0; packed.Count > maxBubbles && guard < 200; guard++)
        {
            budget += Math.Max(20, maxChars / 4);
            packed = Pack(units, budget);
        }
        return packed;
    }

    private static List<string> Rebalance(List<(string Text, string Joiner)> units, int maxBubbles)
    {
        if (units.Count <= maxBubbles) return units.Select(u => u.Text).ToList();
        var total = units.Sum(u => u.Text.Length + 2);
        var budget = (int)Math.Ceiling(total / (double)maxBubbles);
        var packed = Pack(units, budget);
        for (var guard = 0; packed.Count > maxBubbles && guard < 200; guard++)
        {
            budget += 20;
            packed = Pack(units, budget);
        }
        return packed;
    }

    private static List<string> HalveAtSentence(List<string> sentences)
    {
        var total = sentences.Sum(s => s.Length);
        var acc = 0;
        var cut = 1;
        for (var i = 0; i < sentences.Count - 1; i++)
        {
            acc += sentences[i].Length;
            cut = i + 1;
            if (acc >= total / 2) break;
        }
        return new List<string>
        {
            string.Join(" ", sentences.Take(cut)),
            string.Join(" ", sentences.Skip(cut)),
        };
    }

    /// <summary>Greedy packing: join consecutive units while the bubble fits the budget.</summary>
    private static List<string> Pack(List<(string Text, string Joiner)> units, int budget)
    {
        var outv = new List<string>();
        var sb = new StringBuilder();
        var pendingJoiner = "";
        foreach (var (text, joiner) in units)
        {
            if (sb.Length > 0 && sb.Length + pendingJoiner.Length + text.Length > budget)
            {
                outv.Add(sb.ToString().Trim());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(pendingJoiner);
            sb.Append(text);
            pendingJoiner = joiner;
        }
        if (sb.Length > 0) outv.Add(sb.ToString().Trim());
        return outv;
    }

    public static List<string> Sentences(string paragraph) =>
        SentenceEnd.Split(paragraph.Trim()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>How long a person would take to type this bubble, clamped to [min, max].</summary>
    public static TimeSpan TypingDelay(string bubble, double charsPerSecond, int minSeconds, int maxSeconds)
    {
        var cps = Math.Max(0.5, charsPerSecond);
        var seconds = (bubble ?? "").Length / cps;
        var lo = Math.Max(0, minSeconds);
        var hi = Math.Max(lo, maxSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, lo, hi));
    }

    // ---- acknowledgements ----------------------------------------------------

    private static readonly HashSet<string> AckPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "ok", "okay", "okey", "oke", "okie", "k", "kk", "okk", "ok ok", "alright", "all right",
        "sure", "fine", "cool", "great", "nice", "perfect", "awesome", "good", "noted", "got it",
        "gotcha", "understood", "roger", "yup", "yep", "thanks", "thank you", "thank u", "thx",
        "ty", "tq", "tqvm", "tysm", "many thanks", "thanks a lot", "thank you so much",
        "thanks so much", "cheers", "ok thanks", "ok thank you", "okay thanks", "okay thank you",
        "ok tq", "ok noted", "noted thanks", "noted thank you", "great thanks", "perfect thanks",
        "cool thanks", "alright thanks", "terima kasih", "ok terima kasih", "baik", "baik terima kasih",
        "ok baik", "xie xie", "谢谢", "好的", "好", "收到",
    };

    /// <summary>
    /// True when a customer message is only an acknowledgement ("ok", "thanks",
    /// a thumbs-up or other emoji-only message) — nothing in it needs an answer.
    /// </summary>
    public static bool IsAcknowledgement(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 40) return false;
        // emoji / punctuation only (👍, 🙏, "!!", "👌🏻") — no letters or digits at all
        if (!t.Any(char.IsLetterOrDigit)) return true;
        var normalized = new StringBuilder();
        foreach (var ch in t.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) normalized.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch == ',' || ch == '-') normalized.Append(' ');
            // emoji, punctuation, variation selectors are dropped
        }
        var words = Regex.Replace(normalized.ToString(), @"\s+", " ").Trim();
        if (words.Length == 0) return true;
        if (AckPhrases.Contains(words)) return true;
        // stretched forms: "okkkk", "thanksss", "thx!!"
        var squeezed = Regex.Replace(words, @"(.)\1{2,}", "$1");
        return AckPhrases.Contains(squeezed);
    }

    private static readonly Regex ThanksWords = new(
        @"\b(thank|thanks|thx|tq|ty|tqvm|tysm|terima\s*kasih|trima\s*kasih|makasih|tenkiu|thank\s*you)\b|谢谢|多谢|感谢|謝謝",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The reaction a person would put on an acknowledgement instead of a reply:
    /// 🙏 on thanks (EN / BM / ZH), ❤️ on a message that is only emoji, 👍 on "ok", "noted"…
    /// </summary>
    public static string AckReaction(string? text)
    {
        var t = (text ?? "").Trim();
        if (ThanksWords.IsMatch(t)) return "🙏";
        if (t.Length > 0 && !t.Any(char.IsLetterOrDigit)) return "❤️";
        return "👍";
    }

    /// <summary>
    /// True when the message leaves the conversation open — it asks something
    /// (ends with a question mark, ignoring trailing emoji/spaces, or its last
    /// sentence is a question).
    /// </summary>
    public static bool LeavesConversationOpen(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        var trimmed = new string(t.Reverse()
            .SkipWhile(c => !char.IsLetterOrDigit(c) && c != '?' && c != '？' && c != '.' && c != '!')
            .Reverse().ToArray());
        if (trimmed.EndsWith('?') || trimmed.EndsWith('？')) return true;
        var sentences = Sentences(t);
        return sentences.Count > 0 && sentences[^1].Contains('?');
    }

    // ---- what makes a reply worth a human look -----------------------------

    private static readonly Regex MoneyRe = new(
        @"(?:(?:RM|USD|SGD|EUR|GBP|MYR|IDR|INR|AUD|\$|€|£|¥|₹)\s?\d[\d,]*(?:\.\d+)?)"
        + @"|(?:\d[\d,]*(?:\.\d+)?\s?(?:dollars?|ringgit|euros?|pounds?|rupiah|usd|myr|sgd|%))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DateRe = new(
        @"\b\d{1,2}[/.-]\d{1,2}(?:[/.-]\d{2,4})?\b"
        + @"|\b\d{1,2}(?:st|nd|rd|th)?\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\b"
        + @"|\b(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\s+\d{1,2}(?:st|nd|rd|th)?\b"
        + @"|\b(?:mon|tues|wednes|thurs|fri|satur|sun)day\b|\btomorrow\b|\bnext week\b"
        + @"|\b\d{1,2}(?::\d{2})?\s?(?:am|pm)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Why a reply might deserve a person's eye: configured keywords
    /// (whole-word, case-insensitive), money amounts and dates/times — the
    /// places an AI could commit the business to something.
    /// </summary>
    public static List<string> SensitiveHits(string? text, IEnumerable<string> keywords)
    {
        var t = text ?? "";
        var hits = new List<string>();
        foreach (var k in keywords)
        {
            var kw = k.Trim();
            if (kw.Length == 0) continue;
            var pattern = kw.All(c => char.IsLetterOrDigit(c) || c == ' ')
                ? $@"\b{Regex.Escape(kw)}\b" : Regex.Escape(kw);
            if (Regex.IsMatch(t, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                hits.Add($"mentions \"{kw}\"");
        }
        if (MoneyRe.IsMatch(t)) hits.Add("mentions a money amount");
        if (DateRe.IsMatch(t)) hits.Add("mentions a date or time");
        return hits;
    }

    private static readonly string[] HedgePhrases =
    {
        "i'm not sure", "i am not sure", "i don't know", "i do not know", "not certain",
        "i cannot confirm", "i can't confirm", "unable to confirm", "i'm unable to",
    };

    // Wording that commits the conversation to a person. Substring matches on
    // the lower-cased reply, in English, Malay and Chinese.
    private static readonly string[] HandoverPhrases =
    {
        // EN
        "connect you with", "transfer you", "a human agent", "a member of our team will",
        "a colleague will", "someone from our team will", "speak to a human", "human representative",
        "hand you over", "pass this to", "pass your case to", "pass your claim to", "pass your request to",
        "pass your details to", "pass everything to", "passing it to", "passing this to", "passing your",
        "hand this over", "hand over your case", "handing this over", "handing you over", "escalate this to",
        "escalating this to", "our sales team will", "our team will contact",
        "customer care will", "customer service will", "after-sales support will", "support team will",
        "i'll pass", "i will pass", "i’ll pass",
        // MS
        "serahkan kepada", "serahkan pertanyaan", "serahkan kes", "panjangkan kepada", "pasukan kami akan",
        "akan menghubungi anda", "hubungkan anda dengan",
        // ZH
        "转给", "转交", "转接", "客服团队会", "销售团队会", "同事会联系", "同事会跟进", "专人联系", "人工客服",
    };

    // A sentence that only OFFERS a person ("Would you like me to connect you
    // with…?", "I can pass this to…") is not a handover until the customer
    // accepts — the reply that follows then commits to it.
    private static readonly Regex OfferMarker = new(
        @"\b(would you like|do you want|shall i|should i|can i|may i|i can(?!'|’|not|t\b)|i could|if you'd like|if you would like|if you want|if you prefer|let me know if|want me to)\b"
        + @"|\b(adakah anda mahu|anda mahu saya|mahu saya|boleh saya|saya boleh|jika anda mahu|kalau anda mahu)\b"
        + @"|需要我|要不要|要我|我可以|如果您需要|如果你需要|是否需要",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HandoverSentence = new(@"[^.!?。！？\n]+[.!?。！？]?", RegexOptions.Compiled);

    /// <summary>The reply hedges — a low-confidence signal.</summary>
    public static bool Hedges(string? text)
    {
        var t = (text ?? "").ToLower(CultureInfo.InvariantCulture);
        return HedgePhrases.Any(t.Contains);
    }

    /// <summary>
    /// The reply hands the conversation to a person — a sentence that commits
    /// to it, not one that only offers it.
    /// </summary>
    public static bool HandsOver(string? text)
    {
        var t = (text ?? "").ToLower(CultureInfo.InvariantCulture);
        if (!HandoverPhrases.Any(t.Contains)) return false;
        foreach (Match m in HandoverSentence.Matches(t))
        {
            var sentence = m.Value.Trim();
            if (!HandoverPhrases.Any(sentence.Contains)) continue;
            var offer = sentence.EndsWith('?') || sentence.EndsWith('？') || OfferMarker.IsMatch(sentence);
            if (!offer) return true;
        }
        return false;
    }
}
