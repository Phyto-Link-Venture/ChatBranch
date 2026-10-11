using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatBranch.Data.Ai.Voice;

/// <summary>One text-to-speech answer: the audio bytes, or why there are none.</summary>
public sealed record TtsResult(bool Ok, byte[] Audio, string Mime, string Detail)
{
    public static TtsResult Fail(string why) => new(false, Array.Empty<byte>(), "", why);
}

/// <summary>One speech-to-text answer.</summary>
public sealed record SttResult(bool Ok, string Text, string Provider, string Model, string Detail,
    int PromptTokens = 0, int CompletionTokens = 0)
{
    public static SttResult Fail(string provider, string why) => new(false, "", provider, "", why);
}

/// <summary>
/// The speech HTTP calls the AI's voice replies need — written for this API
/// (there was no ElevenLabs client in the C# port before):
///  * ElevenLabs text-to-speech: <c>POST {base}/v1/text-to-speech/{voice_id}?output_format=…</c>
///    (header <c>xi-api-key</c>, body <c>{text, model_id}</c>) — <c>opus_48000_64</c> for a
///    voice note (Ogg/Opus, what WhatsApp and Telegram show as a voice note),
///    <c>mp3_44100_128</c> elsewhere;
///  * OpenAI speech-to-text: <c>POST https://api.openai.com/v1/audio/transcriptions</c>
///    (multipart <c>file</c> + <c>model</c>, default <c>gpt-4o-mini-transcribe</c>);
///  * ElevenLabs speech-to-text: <c>POST {base}/v1/speech-to-text</c> (multipart
///    <c>file</c> + <c>model_id=scribe_v1</c>).
/// Keys: Setting <c>elevenlabs_api_key</c> → env <c>ELEVENLABS_API_KEY</c>; Setting
/// <c>openai_api_key</c> → env <c>OPENAI_API_KEY</c>. Never throws — a failure comes
/// back as data so a reply falls back to text and a voice note stays "[audio]".
/// ⚠ Built to the documented request shapes; the tests run against fakes.
/// </summary>
public class SpeechClient
{
    public const string HttpClientName = "speech";
    public const string DefaultVoiceId = "21m00Tcm4TlvDq8ikWAM"; // ElevenLabs' stock "Rachel"
    public const string VoiceNoteFormat = "opus_48000_64";
    public const string FileFormat = "mp3_44100_128";
    public const string ElevenSttModel = "scribe_v1";
    public const string OpenAiSttModel = "gpt-4o-mini-transcribe";
    /// <summary>A voice note longer than this is not worth synthesizing — the reply goes as text.</summary>
    public const int MaxTtsChars = 1500;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly IHttpClientFactory? _http;
    private readonly ScopingService _scoping;

    public SpeechClient(ScopingService scoping, IHttpClientFactory? http = null)
    {
        _scoping = scoping;
        _http = http;
    }

    private HttpClient Client()
    {
        if (_http == null) return Shared;
        var c = _http.CreateClient(HttpClientName);
        c.Timeout = TimeSpan.FromSeconds(60);
        return c;
    }

    private string Key(string setting, string env)
    {
        try
        {
            var v = _scoping.SettingValue(setting);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        catch { /* settings unreadable → env */ }
        return (Environment.GetEnvironmentVariable(env) ?? "").Trim();
    }

    public string ElevenLabsKey => Key("elevenlabs_api_key", "ELEVENLABS_API_KEY");
    public string OpenAiKey => Key("openai_api_key", "OPENAI_API_KEY");
    public bool TtsConfigured => ElevenLabsKey.Length > 0;

    /// <summary>Speech-to-text choices for Setting <c>transcription_provider</c> ("" = automatic).</summary>
    public static readonly string[] SttChoices = { "", "openai", "elevenlabs", "local", "off" };

    /// <summary>Where a local / self-hosted OpenAI-compatible Whisper server listens when nothing says otherwise
    /// (the compose file's internal <c>whisper</c> service).</summary>
    public const string LocalSttBaseDefault = "http://whisper:8000/v1";
    public const string LocalSttModelDefault = "Systran/faster-whisper-small";

    /// <summary>Setting <c>transcription_provider</c> → env <c>STT_PROVIDER</c>: "" automatic | openai | elevenlabs | local | off.</summary>
    public string SttChoice
    {
        get
        {
            var v = Key("transcription_provider", "STT_PROVIDER").ToLowerInvariant();
            return v is "whisper" ? "local" : SttChoices.Contains(v) ? v : "";
        }
    }

    /// <summary>The local Whisper server's base URL (Setting <c>transcription_base_url</c> → env <c>STT_BASE_URL</c> → the compose service).</summary>
    public string SttBaseUrl
    {
        get
        {
            var v = Key("transcription_base_url", "STT_BASE_URL");
            return (v.Length > 0 ? v : LocalSttBaseDefault).TrimEnd('/');
        }
    }

    /// <summary>
    /// The speech-to-text provider in use: openai, elevenlabs, local or "" (none — audio
    /// stays "[audio]"). A pin (<c>transcription_provider</c>) is used alone — local Whisper needs no
    /// OpenAI key, so switching it on never adds OpenAI to the chat providers; "off"
    /// turns transcription off. Automatic = OpenAI with a key, else ElevenLabs.
    /// </summary>
    public string SttProvider => SttChoice switch
    {
        "off" => "",
        "local" => "local",
        "openai" => OpenAiKey.Length > 0 ? "openai" : "",
        "elevenlabs" => ElevenLabsKey.Length > 0 ? "elevenlabs" : "",
        _ => OpenAiKey.Length > 0 ? "openai" : ElevenLabsKey.Length > 0 ? "elevenlabs" : "",
    };

    /// <summary>The model sent with a transcription (Setting <c>transcription_model</c> → env <c>STT_MODEL</c> →
    /// <c>OPENAI_STT_MODEL</c> → the provider's default).</summary>
    public string SttModelFor(string provider)
    {
        var v = Key("transcription_model", "STT_MODEL");
        if (v.Length == 0 && provider is "openai" or "local")
            v = (Environment.GetEnvironmentVariable("OPENAI_STT_MODEL") ?? "").Trim();
        if (v.Length > 0 && provider != "elevenlabs") return v;
        return provider switch { "local" => LocalSttModelDefault, "elevenlabs" => ElevenSttModel, _ => OpenAiSttModel };
    }

    public static string ElevenBase =>
        (Environment.GetEnvironmentVariable("ELEVENLABS_BASE_URL") is { Length: > 0 } b ? b : "https://api.elevenlabs.io")
        .TrimEnd('/');

    public static string TtsModel =>
        Environment.GetEnvironmentVariable("ELEVENLABS_TTS_MODEL") is { Length: > 0 } m ? m.Trim() : "eleven_multilingual_v2";

    /// <summary>The MIME of an ElevenLabs output format.</summary>
    public static string MimeFor(string outputFormat) =>
        outputFormat.StartsWith("opus", StringComparison.Ordinal) ? "audio/ogg" : "audio/mpeg";

    public async Task<TtsResult> SynthesizeAsync(string text, string voiceId, string outputFormat,
        CancellationToken ct = default)
    {
        var key = ElevenLabsKey;
        if (key.Length == 0) return TtsResult.Fail("no ElevenLabs API key (Settings → AI keys & models)");
        if (string.IsNullOrWhiteSpace(text)) return TtsResult.Fail("nothing to say");
        try
        {
            var voice = string.IsNullOrWhiteSpace(voiceId) ? DefaultVoiceId : voiceId.Trim();
            var url = $"{ElevenBase}/v1/text-to-speech/{Uri.EscapeDataString(voice)}"
                      + $"?output_format={Uri.EscapeDataString(outputFormat)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { text, model_id = TtsModel }),
                    Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("xi-api-key", key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MimeFor(outputFormat)));
            using var resp = await Client().SendAsync(req, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return TtsResult.Fail($"ElevenLabs {(int)resp.StatusCode}: {Detail(bytes)}");
            if (bytes.Length == 0) return TtsResult.Fail("ElevenLabs returned no audio");
            // a voice note must be Ogg/Opus or WhatsApp shows a plain audio file — check the container
            if (outputFormat.StartsWith("opus", StringComparison.Ordinal) && !IsOgg(bytes))
                return TtsResult.Fail("ElevenLabs did not return Ogg/Opus audio");
            return new TtsResult(true, bytes, MimeFor(outputFormat), "");
        }
        catch (Exception ex)
        {
            return TtsResult.Fail($"ElevenLabs unreachable ({ex.GetType().Name})");
        }
    }

    public static bool IsOgg(byte[] b) => b.Length >= 4 && b[0] == (byte)'O' && b[1] == (byte)'g' && b[2] == (byte)'g' && b[3] == (byte)'S';

    /// <summary>Transcribe an audio file with the first configured provider (OpenAI, else ElevenLabs).</summary>
    public async Task<SttResult> TranscribeAsync(byte[] audio, string fileName, string mime,
        CancellationToken ct = default)
    {
        var provider = SttProvider;
        if (provider.Length == 0) return SttResult.Fail("", "no speech-to-text provider configured");
        if (audio.Length == 0) return SttResult.Fail(provider, "empty audio");
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mime) ? "audio/ogg" : mime.Split(';')[0].Trim());
            form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "voice.ogg" : fileName);
            HttpRequestMessage req;
            string model;
            if (provider is "openai" or "local")
            {
                // both speak OpenAI's /audio/transcriptions; local = a self-hosted Whisper
                // (faster-whisper / speaches) on the internal network, key optional
                model = SttModelFor(provider);
                form.Add(new StringContent(model), "model");
                var url = provider == "local"
                    ? $"{SttBaseUrl}/audio/transcriptions"
                    : "https://api.openai.com/v1/audio/transcriptions";
                req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
                var bearer = provider == "local" ? Key("transcription_api_key", "STT_API_KEY") : OpenAiKey;
                if (bearer.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            else
            {
                model = ElevenSttModel;
                form.Add(new StringContent(model), "model_id");
                req = new HttpRequestMessage(HttpMethod.Post, $"{ElevenBase}/v1/speech-to-text") { Content = form };
                req.Headers.Add("xi-api-key", ElevenLabsKey);
            }
            using (req)
            using (var resp = await Client().SendAsync(req, ct))
            {
                var body = await resp.Content.ReadAsByteArrayAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    return SttResult.Fail(provider, $"{provider} {(int)resp.StatusCode}: {Detail(body)}");
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? "").Trim() : "";
                int pt = 0, cp = 0;
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    if (u.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv)) pt = iv;
                    if (u.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov)) cp = ov;
                }
                return new SttResult(true, text, provider, model, "", pt, cp);
            }
        }
        catch (Exception ex)
        {
            return SttResult.Fail(provider, $"{provider} unreachable ({ex.GetType().Name})");
        }
    }

    private static string Detail(byte[] body)
    {
        try
        {
            var s = Encoding.UTF8.GetString(body);
            return s.Length > 200 ? s[..200] : s;
        }
        catch { return ""; }
    }
}
