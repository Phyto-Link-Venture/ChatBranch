using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatBranch.Data.Ai.Voice;

/// <summary>One recording handed to a clone.</summary>
public sealed record VoiceClip(byte[] Audio, string FileName, string Mime);

/// <summary>One voice clone / import answer: the provider's own voice id, or why there is none.</summary>
public sealed record VoiceCloneResult(bool Ok, string RemoteId, string Name, string Detail)
{
    public static VoiceCloneResult Fail(string why) => new(false, "", "", why);
}

/// <summary>
/// The second text-to-speech provider and the voice-library calls.
///
///  * <b>VoiceStudio</b> (github.com/debpalash/VoiceStudio — a self-hosted, open-source ElevenLabs
///    alternative, run as its own container): speech through its OpenAI-compatible
///    <c>POST {base}/v1/audio/speech</c> (<c>{model, input, voice, response_format}</c>, Bearer key),
///    clones through <c>POST {base}/profiles</c> (multipart <c>name</c> + <c>ref_audio</c>,
///    <c>kind=clone</c>) whose <c>id</c> is then the <c>voice</c>; a voice is removed with
///    <c>DELETE {base}/profiles/{id}</c>. Settings <c>voicestudio_base_url</c> (env
///    <c>VOICESTUDIO_URL</c>; it is ON only once an address is set), <c>voicestudio_api_key</c>
///    (<c>VOICESTUDIO_API_KEY</c>, VoiceStudio's <c>OMNIVOICE_API_KEY</c>) and
///    <c>voicestudio_model</c> (<c>VOICESTUDIO_MODEL</c>, default <c>tts-1</c> = VoiceStudio's
///    active engine).
///  * <b>OpenAI</b> speech for the preset voices marin / cedar / alloy (the same OpenAI-compatible
///    call, <c>gpt-4o-mini-tts</c>), with the OpenAI key.
///  * <b>ElevenLabs</b> instant voice cloning <c>POST /v1/voices/add</c>, lookup
///    <c>GET /v1/voices/{id}</c> and delete <c>DELETE /v1/voices/{id}</c>.
/// Never throws — a failure comes back as data.
/// </summary>
public partial class SpeechClient
{
    public const string VoiceStudioDefaultModel = "tts-1";
    public const string OpenAiTtsModel = "gpt-4o-mini-tts";
    /// <summary>VoiceStudio maps OpenAI's voice names onto its active engine's default voice.</summary>
    public const string VoiceStudioDefaultVoice = "alloy";
    /// <summary>A self-hosted model on a CPU can take a while for a long note.</summary>
    private static readonly TimeSpan SelfHostedTimeout = TimeSpan.FromSeconds(180);

    public static readonly string[] TtsProviders = { "elevenlabs", "voicestudio", "openai" };

    /// <summary>VoiceStudio's root URL ("" = not set up). A trailing <c>/v1</c> is dropped — both its
    /// OpenAI routes (<c>/v1/…</c>) and its voice profiles (<c>/profiles</c>) hang off the root.</summary>
    public string VoiceStudioBaseUrl
    {
        get
        {
            var v = Key("voicestudio_base_url", "VOICESTUDIO_URL").TrimEnd('/');
            if (v.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) v = v[..^3];
            return v;
        }
    }

    public string VoiceStudioKey => Key("voicestudio_api_key", "VOICESTUDIO_API_KEY");
    public string VoiceStudioModel =>
        Key("voicestudio_model", "VOICESTUDIO_MODEL") is { Length: > 0 } m ? m : VoiceStudioDefaultModel;
    public bool VoiceStudioConfigured => VoiceStudioBaseUrl.Length > 0;

    /// <summary>Is this text-to-speech provider usable right now?</summary>
    public bool TtsReady(string provider) => provider switch
    {
        "voicestudio" => VoiceStudioConfigured,
        "openai" => OpenAiKey.Length > 0,
        "elevenlabs" => ElevenLabsKey.Length > 0,
        _ => false,
    };

    /// <summary>The providers a voice can be cloned with, in the order they are offered.</summary>
    public string[] CloneProviders() =>
        new[] { "elevenlabs", "voicestudio" }.Where(TtsReady).ToArray();

    /// <summary>The model name the ledger records for a provider.</summary>
    public string TtsModelFor(string provider) => provider switch
    {
        "voicestudio" => VoiceStudioModel,
        "openai" => OpenAiTtsModel,
        _ => TtsModel,
    };

    private HttpClient Client(TimeSpan timeout)
    {
        if (_http == null) return timeout <= TimeSpan.FromSeconds(60) ? Shared : SharedLong;
        var c = _http.CreateClient(HttpClientName);
        c.Timeout = timeout;
        return c;
    }
    private static readonly HttpClient SharedLong = new() { Timeout = SelfHostedTimeout };

    /// <summary>
    /// Text-to-speech through <paramref name="provider"/> (elevenlabs | voicestudio | openai).
    /// <paramref name="outputFormat"/> is the ElevenLabs-style name the callers already use —
    /// <c>opus_…</c> = an Ogg/Opus voice note, anything else MP3.
    /// </summary>
    public Task<TtsResult> SynthesizeWithAsync(string provider, string text, string voiceId, string outputFormat,
        CancellationToken ct = default) => provider switch
    {
        "voicestudio" => OpenAiCompatSpeechAsync("VoiceStudio", text, VoiceStudioBaseUrl.Length > 0 ? VoiceStudioBaseUrl + "/v1" : "",
            VoiceStudioKey, VoiceStudioModel, Blank(voiceId) ? VoiceStudioDefaultVoice : voiceId.Trim(), outputFormat,
            SelfHostedTimeout, ct),
        "openai" => OpenAiKey.Length == 0
            ? Task.FromResult(TtsResult.Fail("no OpenAI API key (Settings → AI keys & models)"))
            : OpenAiCompatSpeechAsync("OpenAI", text, "https://api.openai.com/v1", OpenAiKey, OpenAiTtsModel,
                Blank(voiceId) ? "alloy" : voiceId.Trim(), outputFormat, TimeSpan.FromSeconds(60), ct),
        _ => SynthesizeAsync(text, voiceId, outputFormat, ct),
    };

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private async Task<TtsResult> OpenAiCompatSpeechAsync(string label, string text, string baseUrl, string key, string model,
        string voice, string outputFormat, TimeSpan timeout, CancellationToken ct)
    {
        if (baseUrl.Length == 0) return TtsResult.Fail($"{label} is not set up (Settings → AI keys & models)");
        if (Blank(text)) return TtsResult.Fail("nothing to say");
        var opus = outputFormat.StartsWith("opus", StringComparison.Ordinal);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/speech")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model, input = text, voice, response_format = opus ? "opus" : "mp3",
                }), Encoding.UTF8, "application/json"),
            };
            if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var resp = await Client(timeout).SendAsync(req, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!resp.IsSuccessStatusCode) return TtsResult.Fail($"{label} {(int)resp.StatusCode}: {Detail(bytes)}");
            if (bytes.Length == 0) return TtsResult.Fail($"{label} returned no audio");
            if (opus && !IsOgg(bytes)) return TtsResult.Fail($"{label} did not return Ogg/Opus audio");
            return new TtsResult(true, bytes, opus ? "audio/ogg" : "audio/mpeg", "");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return TtsResult.Fail($"{label} took longer than {(int)timeout.TotalSeconds}s");
        }
        catch (Exception ex)
        {
            return TtsResult.Fail($"{label} unreachable ({ex.GetType().Name})");
        }
    }

    /// <summary>ElevenLabs takes up to 25 samples for one instant clone.</summary>
    public const int MaxCloneClips = 25;

    /// <summary>One recording — kept for callers that have a single clip.</summary>
    public Task<VoiceCloneResult> CloneVoiceAsync(string provider, string name, byte[] audio, string fileName,
        string mime, string language, string description, CancellationToken ct = default) =>
        CloneVoiceAsync(provider, name, new[] { new VoiceClip(audio, fileName, mime) }, language, description, ct);

    /// <summary>
    /// Clone a voice from one or more recordings. ElevenLabs: instant voice cloning with every clip
    /// as a sample (≤ 25; counts against the plan's voice slots). VoiceStudio: a <c>clone</c> voice
    /// profile — it takes ONE reference clip, so the first is used.
    /// </summary>
    public async Task<VoiceCloneResult> CloneVoiceAsync(string provider, string name, IReadOnlyList<VoiceClip> clips,
        string language, string description, CancellationToken ct = default)
    {
        clips = clips.Where(c => c.Audio.Length > 0).ToList();
        if (clips.Count == 0) return VoiceCloneResult.Fail("empty recording");
        if (clips.Count > MaxCloneClips) return VoiceCloneResult.Fail($"at most {MaxCloneClips} recordings per voice");
        static ByteArrayContent Part(VoiceClip c)
        {
            var part = new ByteArrayContent(c.Audio);
            part.Headers.ContentType = new MediaTypeHeaderValue(Blank(c.Mime) ? "audio/webm" : c.Mime.Split(';')[0].Trim());
            return part;
        }
        static string NameOf(VoiceClip c, int i) => Blank(c.FileName) ? $"sample-{i + 1}.webm" : c.FileName;
        using var form = new MultipartFormDataContent();
        HttpRequestMessage req;
        string label;
        if (provider == "voicestudio")
        {
            label = "VoiceStudio";
            if (!VoiceStudioConfigured) return VoiceCloneResult.Fail("VoiceStudio is not set up (Settings → AI keys & models)");
            form.Add(new StringContent(name), "name");
            form.Add(new StringContent("clone"), "kind");
            // VoiceStudio detects the language itself; its own names are "English", "Malay"…
            form.Add(new StringContent("Auto"), "language");
            form.Add(Part(clips[0]), "ref_audio", NameOf(clips[0], 0));
            req = new HttpRequestMessage(HttpMethod.Post, $"{VoiceStudioBaseUrl}/profiles") { Content = form };
            if (VoiceStudioKey.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", VoiceStudioKey);
        }
        else
        {
            label = "ElevenLabs";
            if (ElevenLabsKey.Length == 0) return VoiceCloneResult.Fail("no ElevenLabs API key (Settings → AI keys & models)");
            form.Add(new StringContent(name), "name");
            if (!Blank(description)) form.Add(new StringContent(description), "description");
            form.Add(new StringContent(JsonSerializer.Serialize(new { language })), "labels");
            for (var i = 0; i < clips.Count; i++) form.Add(Part(clips[i]), "files", NameOf(clips[i], i));
            req = new HttpRequestMessage(HttpMethod.Post, $"{ElevenBase}/v1/voices/add") { Content = form };
            req.Headers.Add("xi-api-key", ElevenLabsKey);
        }
        try
        {
            using (req)
            using (var resp = await Client(SelfHostedTimeout).SendAsync(req, ct))
            {
                var body = await resp.Content.ReadAsByteArrayAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    return VoiceCloneResult.Fail($"{label} {(int)resp.StatusCode}: {ErrorText(body)}");
                using var doc = JsonDocument.Parse(body);
                var idKey = provider == "voicestudio" ? "id" : "voice_id";
                var id = doc.RootElement.TryGetProperty(idKey, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
                return id.Length == 0
                    ? VoiceCloneResult.Fail($"{label} answered without a voice id")
                    : new VoiceCloneResult(true, id, name, "");
            }
        }
        catch (Exception ex)
        {
            return VoiceCloneResult.Fail($"{label} unreachable ({ex.GetType().Name})");
        }
    }

    /// <summary>Look an existing ElevenLabs voice up (the import flow).</summary>
    public async Task<VoiceCloneResult> ElevenVoiceAsync(string voiceId, CancellationToken ct = default)
    {
        if (ElevenLabsKey.Length == 0) return VoiceCloneResult.Fail("no ElevenLabs API key (Settings → AI keys & models)");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ElevenBase}/v1/voices/{Uri.EscapeDataString(voiceId)}");
            req.Headers.Add("xi-api-key", ElevenLabsKey);
            using var resp = await Client().SendAsync(req, ct);
            var body = await resp.Content.ReadAsByteArrayAsync(ct);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return VoiceCloneResult.Fail("ElevenLabs has no voice with that id in this account");
            if (!resp.IsSuccessStatusCode) return VoiceCloneResult.Fail($"ElevenLabs {(int)resp.StatusCode}: {ErrorText(body)}");
            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? "" : "";
            return new VoiceCloneResult(true, voiceId, name, "");
        }
        catch (Exception ex)
        {
            return VoiceCloneResult.Fail($"ElevenLabs unreachable ({ex.GetType().Name})");
        }
    }

    /// <summary>Delete a cloned voice at its provider. Already gone (404) counts as done.</summary>
    public async Task<(bool Ok, string Detail)> DeleteRemoteVoiceAsync(string provider, string remoteId,
        CancellationToken ct = default)
    {
        HttpRequestMessage req;
        string label;
        if (provider == "voicestudio")
        {
            label = "VoiceStudio";
            if (!VoiceStudioConfigured) return (false, "VoiceStudio is not set up, so its copy of the voice can't be removed");
            req = new HttpRequestMessage(HttpMethod.Delete, $"{VoiceStudioBaseUrl}/profiles/{Uri.EscapeDataString(remoteId)}");
            if (VoiceStudioKey.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", VoiceStudioKey);
        }
        else
        {
            label = "ElevenLabs";
            if (ElevenLabsKey.Length == 0) return (false, "no ElevenLabs API key, so the clone can't be removed there");
            req = new HttpRequestMessage(HttpMethod.Delete, $"{ElevenBase}/v1/voices/{Uri.EscapeDataString(remoteId)}");
            req.Headers.Add("xi-api-key", ElevenLabsKey);
        }
        try
        {
            using (req)
            using (var resp = await Client().SendAsync(req, ct))
            {
                if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound) return (true, "");
                var body = await resp.Content.ReadAsByteArrayAsync(ct);
                return (false, $"{label} {(int)resp.StatusCode}: {ErrorText(body)}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"{label} unreachable ({ex.GetType().Name})");
        }
    }

    /// <summary>The provider's own error words when it sent JSON ({detail} / {detail:{message}} / {error:{message}}).</summary>
    private static string ErrorText(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            foreach (var k in new[] { "detail", "error" })
            {
                if (!r.TryGetProperty(k, out var d)) continue;
                if (d.ValueKind == JsonValueKind.String) return Cap(d.GetString() ?? "");
                if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("message", out var m)
                    && m.ValueKind == JsonValueKind.String) return Cap(m.GetString() ?? "");
            }
        }
        catch { /* not JSON */ }
        return Detail(body);
    }

    private static string Cap(string s) => s.Length > 300 ? s[..300] : s;
}
