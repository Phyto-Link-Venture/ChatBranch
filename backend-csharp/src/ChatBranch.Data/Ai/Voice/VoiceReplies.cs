using System.Text;
using System.Text.RegularExpressions;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Data.Ai.Voice;

/// <summary>A synthesized voice note stored as conversation media, ready to send.</summary>
public sealed record VoiceNote(string RelPath, string Mime, string AbsolutePath);

/// <summary>
/// AI voice replies — the AI answers with a VOICE NOTE (ElevenLabs text-to-speech)
/// where the channel can carry one. Setting <c>ai_voice_replies</c> = off (default) |
/// mirror (only when the customer's latest message was a voice note / audio) |
/// always, plus <c>ai_voice_id</c>; both resolved inbox → entity → organization →
/// default like every human-like touch (<see cref="AiBehaviourSettings"/>).
///
/// The rule (<see cref="Wants"/>) is pure and runs at DELIVERY — a held, shadowed,
/// rejected or expired draft never synthesizes; approving one later decides then.
/// A voice note carries the WHOLE reply (never split into bubbles) and the stored
/// message keeps the reply's words (+ the audio as media), so the team and the AI's
/// own history read what was said. Text instead — never silence — when: the channel
/// has no voice notes (email, SMS, LINE, Viber, widget, WeChat, Zalo, TikTok,
/// Douyin, a Twilio WhatsApp number), the reply holds a link or is very long, no
/// ElevenLabs key, the entity's AI quota is reached, or TTS / the send fails.
/// Usage is metered as <c>LlmUsage</c> feature <c>voice_reply</c> (characters as
/// prompt tokens; price them via <c>llm_prices</c>).
/// </summary>
public class VoiceReplyService
{
    public const string Feature = "voice_reply";

    private readonly AppDbContext _db;
    private readonly ScopingService _scoping;
    private readonly MediaStore? _media;
    private readonly SpeechClient _speech;

    public VoiceReplyService(AppDbContext db, ScopingService scoping, MediaStore? media = null,
        SpeechClient? speech = null, IHttpClientFactory? http = null)
    {
        _db = db;
        _scoping = scoping;
        _media = media;
        _speech = speech ?? new SpeechClient(scoping, http);
    }

    /// <summary>Can this conversation carry an outbound voice note?</summary>
    public static bool Supports(string channel, ChannelAccount? account) => channel switch
    {
        ChatBranch.Domain.Channels.WhatsApp => account == null || account.Provider == Providers.Baileys
                                               || WhatsAppProviders.Of(account) is Providers.Cloud or Providers.YCloud,
        ChatBranch.Domain.Channels.Telegram or ChatBranch.Domain.Channels.Facebook
            or ChatBranch.Domain.Channels.Instagram => true,
        _ => false,
    };

    /// <summary>The ElevenLabs format a channel wants: Ogg/Opus where it becomes a voice note, MP3 elsewhere.</summary>
    public static string FormatFor(string channel) =>
        channel is ChatBranch.Domain.Channels.WhatsApp or ChatBranch.Domain.Channels.Telegram
            ? SpeechClient.VoiceNoteFormat : SpeechClient.FileFormat;

    private static readonly Regex Placeholder = new(@"^\s*\[(audio|voice|voice note|ptt)\b[^\]]*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A customer audio message — stored as audio media, or still its "[audio]" placeholder.</summary>
    public static bool IsAudio(Message m) =>
        m.MediaType == "audio" || (m.MediaPath.Length == 0 && Placeholder.IsMatch(m.Text ?? ""));

    /// <summary>Was the customer's latest (non-private) message a voice note / audio?</summary>
    public bool LatestCustomerIsAudio(Thread thread)
    {
        try
        {
            var last = _db.Messages.AsNoTracking()
                .Where(m => m.ThreadId == thread.Id && m.Direction == MessageDirection.In && !m.Private && m.RecalledAt == null)
                .OrderByDescending(m => m.Id).FirstOrDefault();
            return last != null && IsAudio(last);
        }
        catch { return false; }
    }

    /// <summary>
    /// The settings + channel say "voice note" for this reply (the reply's words and the
    /// key are checked when it is made). Pure apart from reading the latest message.
    /// </summary>
    public bool Wants(Thread thread, ChannelAccount? account, AiBehaviour policy, string text)
    {
        if (policy.VoiceReplies is not ("mirror" or "always")) return false;
        if (!Supports(thread.Channel, account)) return false;
        if (Speakable(text) == null) return false;
        return policy.VoiceReplies == "always" || LatestCustomerIsAudio(thread);
    }

    private static readonly Regex Url = new(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Markup = new(@"(\*\*|__|~~|`+|^\s{0,3}#{1,6}\s+|^\s*[-*•]\s+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The words to speak: markdown, message ids and emoji removed. Null = this reply
    /// should stay TEXT (it holds a link the customer must tap, it is empty once
    /// cleaned, or it is too long for a voice note).
    /// </summary>
    public static string? Speakable(string? text)
    {
        var s = HumanTouch.StripTags(text ?? "");
        if (s.Length == 0 || Url.IsMatch(s)) return null;
        s = Markup.Replace(s, "");
        s = s.Replace("*", "").Replace("_", " ");
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            var v = r.Value;
            if (v >= 0x1F000 || v is >= 0x2600 and <= 0x27BF || v is >= 0x2B00 and <= 0x2BFF
                || v is 0xFE0F or 0x200D or 0x20E3) continue;
            sb.Append(r.ToString());
        }
        s = Regex.Replace(sb.ToString(), @"[ \t]+", " ");
        s = Regex.Replace(s, @"\s*\n\s*", "\n").Trim();
        if (s.Length == 0 || !s.Any(char.IsLetterOrDigit) || s.Length > SpeechClient.MaxTtsChars) return null;
        return s;
    }

    /// <summary>The voice: <c>ai_voice_id</c> → the organization's call voice when it is ElevenLabs → ElevenLabs' default.</summary>
    public string VoiceFor(AiBehaviour policy)
    {
        if (policy.VoiceId.Trim().Length > 0) return policy.VoiceId.Trim();
        try
        {
            var saved = _scoping.SettingValue("default_voice");
            var provider = _scoping.SettingValue("default_voice_provider").Trim().ToLowerInvariant();
            if (saved.Length > 0 && provider == "elevenlabs")
                return Sidecars.VoiceDispatch.ResolveVoice(_media?.DataDir ?? "", "elevenlabs", saved) ?? saved;
        }
        catch { /* the stock voice */ }
        return SpeechClient.DefaultVoiceId;
    }

    /// <summary>
    /// Make the voice note for a reply: quota + key + TTS, stored as conversation media,
    /// metered. Null + the reason when it can't be made (the caller sends text).
    /// </summary>
    public (VoiceNote? Note, string Why) Synthesize(Thread thread, string text, AiBehaviour policy)
    {
        var words = Speakable(text);
        if (words == null) return (null, "the reply has a link or is too long for a voice note");
        if (_media == null) return (null, "no media store");
        if (!_speech.TtsConfigured) return (null, "no ElevenLabs API key");
        try
        {
            if (AiQuota.EntityBlock(_db, _scoping, thread.EntityId) is { } cap)
                return (null, AiQuota.Reason(cap));
        }
        catch { /* a quota read failure fails open */ }

        var format = FormatFor(thread.Channel);
        var voice = VoiceFor(policy);
        TtsResult tts;
        try { tts = Task.Run(() => _speech.SynthesizeAsync(words, voice, format)).GetAwaiter().GetResult(); }
        catch (Exception ex) { tts = TtsResult.Fail(ex.GetType().Name); }
        if (tts.Ok) Meter(thread, words, voice, format, tts.Ok);
        if (!tts.Ok) return (null, tts.Detail);
        try
        {
            var ext = format.StartsWith("opus", StringComparison.Ordinal) ? ".ogg" : ".mp3";
            var rel = Task.Run(() => _media.SaveAsync(thread.Channel, thread.Key, tts.Audio, ext)).GetAwaiter().GetResult();
            var full = _media.Resolve(thread.Channel, rel);
            return full == null ? (null, "the voice note could not be stored") : (new VoiceNote(rel, tts.Mime, full), "");
        }
        catch (Exception ex) { return (null, $"the voice note could not be stored ({ex.GetType().Name})"); }
    }

    private void Meter(Thread thread, string words, string voice, string format, bool ok)
    {
        try
        {
            _db.LlmUsage.Add(new LlmUsage
            {
                Feature = Feature, Provider = "elevenlabs", Model = SpeechClient.TtsModel, Channel = thread.Channel,
                ContactId = thread.ContactId, EntityId = thread.EntityId, Ref = thread.Key,
                PromptTokens = words.Length, CompletionTokens = 0,
                Detail = new Dictionary<string, object?>
                {
                    ["characters"] = words.Length, ["voice_id"] = voice, ["format"] = format, ["ok"] = ok,
                },
                CreatedAt = DateTime.UtcNow,
            });
            _db.SaveChanges();
        }
        catch { /* the ledger never breaks a reply */ }
    }
}
