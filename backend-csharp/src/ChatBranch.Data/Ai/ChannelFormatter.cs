using System.Text.RegularExpressions;

namespace ChatBranch.Data.Ai;

/// <summary>
/// Rewrites the Markdown a model writes into what the customer's channel can
/// show. A house rule ("no Markdown") is not enough: the model copies the style
/// of earlier replies in the history, so this runs in code on every AI reply
/// (<see cref="AssistantService"/>) — never on a person's message.
///
/// WhatsApp has its own markup (<c>*bold*</c>, <c>_italic_</c>, <c>~strike~</c>,
/// <c>`code`</c>), so Markdown is translated into it; SMS, Messenger, Instagram,
/// LINE, Viber, WeChat, Zalo, TikTok, Douyin and Telegram (sent without
/// <c>parse_mode</c>) show plain text, so the markers go; email and the website
/// widget are left as written. Only matched pairs AROUND text are touched — never
/// a lone <c>*</c>, <c>2*3</c> or a masked IC like <c>******-**-1234</c> — and
/// running it twice changes nothing.
/// </summary>
public static class ChannelFormatter
{
    private enum Style { Unchanged, WhatsApp, Plain }

    private static readonly Regex DoubleStar = new(@"(?<!\*)\*\*(?=[^\s*])(.+?)(?<=[^\s*])\*\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex DoubleUnderscore = new(@"(?<![\w_])__(?=\S)(.+?)(?<=\S)__(?![\w_])", RegexOptions.Compiled);
    private static readonly Regex Strike = new(@"(?<!~)~~(?=[^\s~])(.+?)(?<=[^\s~])~~(?!~)", RegexOptions.Compiled);
    // a single *x* pair (WhatsApp bold): not part of a run of stars, text hugging both ends
    private static readonly Regex SingleStar = new(@"(?<![\*\w])\*(?=[^\s\*])([^\*\n]*?[^\s\*])\*(?![\*\w])", RegexOptions.Compiled);
    private static readonly Regex SingleTilde = new(@"(?<![~\w])~(?=[^\s~])([^~\n]*?[^\s~])~(?![~\w])", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^[ \t]{0,3}#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Link = new(@"\[([^\]\n]+)\]\((https?://[^\s)]+)\)", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"(?<!`)`([^`\n]+)`(?!`)", RegexOptions.Compiled);

    public static string ForChannel(string text, string? channel)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var style = StyleFor(channel);
        if (style == Style.Unchanged) return text;

        // links first, so a bold link text is still converted afterwards
        text = Link.Replace(text, m => m.Groups[1].Value == m.Groups[2].Value
            ? m.Groups[2].Value
            : $"{m.Groups[1].Value}: {m.Groups[2].Value}");

        if (style == Style.WhatsApp)
        {
            text = DoubleStar.Replace(text, "*$1*");
            text = DoubleUnderscore.Replace(text, "*$1*");
            text = Strike.Replace(text, "~$1~");
            text = Heading.Replace(text, m => $"*{Unwrap(m.Groups[1].Value)}*");
            return text;
        }

        // plain: drop the markers, keep the words
        text = Heading.Replace(text, m => Unwrap(m.Groups[1].Value));
        text = DoubleStar.Replace(text, "$1");
        text = DoubleUnderscore.Replace(text, "$1");
        text = Strike.Replace(text, "$1");
        text = SingleStar.Replace(text, "$1");
        text = SingleTilde.Replace(text, "$1");
        text = InlineCode.Replace(text, "$1");
        return text;
    }

    /// <summary>A heading already wrapped in bold ("## **Details**") isn't wrapped twice.</summary>
    private static string Unwrap(string s)
    {
        s = s.Trim();
        var m = DoubleStar.Match(s);
        if (m.Success && m.Index == 0 && m.Length == s.Length) return m.Groups[1].Value;
        m = SingleStar.Match(s);
        if (m.Success && m.Index == 0 && m.Length == s.Length) return m.Groups[1].Value;
        return s;
    }

    private static Style StyleFor(string? channel) => (channel ?? "").Trim().ToLowerInvariant() switch
    {
        ChatBranch.Domain.Channels.WhatsApp => Style.WhatsApp,
        ChatBranch.Domain.Channels.Sms or ChatBranch.Domain.Channels.Facebook or ChatBranch.Domain.Channels.Instagram
            or ChatBranch.Domain.Channels.Line or ChatBranch.Domain.Channels.Viber
            or ChatBranch.Domain.Channels.WeChat or ChatBranch.Domain.Channels.Telegram or "zalo" or "tiktok" or "douyin" => Style.Plain,
        _ => Style.Unchanged, // email, the website widget, voice, unknown
    };
}
