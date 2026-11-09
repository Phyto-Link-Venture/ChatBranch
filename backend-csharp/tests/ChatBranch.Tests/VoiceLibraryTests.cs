using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Voice;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// VoiceStudio (the self-hosted TTS provider, Ai/Voice/SpeechVoices.cs) and the voice library's
/// provider calls: AI voice replies through VoiceStudio's OpenAI-compatible speech route, a cloned
/// voice resolved to its provider's own id, ElevenLabs / VoiceStudio clone + delete at the HTTP
/// level, and the clone / preview endpoints answering honestly with nothing set up.
/// </summary>
public class VoiceLibraryTests
{
    private static void UseVoiceStudioOnly(VoiceRig r)
    {
        r.Sc.SetSetting("elevenlabs_api_key", "");
        r.Sc.SetSetting("voicestudio_base_url", "http://vs.test:3900/v1/");   // a trailing /v1 is tolerated
        r.Sc.SetSetting("voicestudio_api_key", "vs-key");
    }

    [Fact]
    public void A_voice_reply_goes_through_VoiceStudio_when_it_is_the_one_set_up()
    {
        using var r = new VoiceRig();
        UseVoiceStudioOnly(r);
        var acct = r.Account("whatsapp", Providers.Baileys, "gw-vs");
        r.Contact("Mei", "60123000777");

        r.Inbound.ProcessInbound("whatsapp", "60123000777", "[audio]", accountId: acct.Id, wamid: "wa.vs1");

        Assert.Single(r.Baileys.Voice);
        Assert.Empty(r.Speech.Tts);   // no ElevenLabs call
        var call = Assert.Single(r.Speech.Speak);
        Assert.Equal("http://vs.test:3900", call.Origin);
        Assert.Equal("/v1/audio/speech", call.Path);
        Assert.Equal("vs-key", call.Key);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("opus", body.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(SpeechClient.VoiceStudioDefaultVoice, body.RootElement.GetProperty("voice").GetString());
        Assert.Equal(SpeechClient.VoiceStudioDefaultModel, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Sure, see you at 8pm!", body.RootElement.GetProperty("input").GetString());

        var usage = r.T.Fresh().LlmUsage.Single(u => u.Feature == VoiceReplyService.Feature);
        Assert.Equal("voicestudio", usage.Provider);
        Assert.Equal(SpeechClient.VoiceStudioDefaultModel, usage.Model);
    }

    [Fact]
    public void A_cloned_voice_speaks_through_its_own_provider_with_the_providers_id()
    {
        using var r = new VoiceRig();
        UseVoiceStudioOnly(r);
        r.Sc.SetSetting("elevenlabs_api_key", "el-key");   // both set up: the picked voice decides
        Directory.CreateDirectory(r.Media.DataDir);
        File.WriteAllText(Path.Combine(r.Media.DataDir, "voices.json"), new JsonObject
        {
            ["voices"] = new JsonArray(new JsonObject
            {
                ["id"] = "custom-mine", ["name"] = "Mine", ["provider"] = "voicestudio",
                ["remoteVoiceId"] = "vs-profile-42", ["custom"] = true,
            }),
        }.ToJsonString());
        r.Sc.SetSetting("ai_voice_id", "custom-mine");
        var acct = r.Account("whatsapp", Providers.Baileys, "gw-c");
        r.Contact("Ah Kow", "60123000888");

        r.Inbound.ProcessInbound("whatsapp", "60123000888", "[audio]", accountId: acct.Id, wamid: "wa.c1");

        Assert.Empty(r.Speech.Tts);
        using var body = JsonDocument.Parse(Assert.Single(r.Speech.Speak).Body);
        Assert.Equal("vs-profile-42", body.RootElement.GetProperty("voice").GetString());
    }

    [Fact]
    public void A_VoiceStudio_failure_still_answers_in_text()
    {
        using var r = new VoiceRig();
        UseVoiceStudioOnly(r);
        r.Speech.FailTts = true;
        var acct = r.Account("whatsapp", Providers.Baileys, "gw-f");
        r.Contact("Siti", "60123000999");

        r.Inbound.ProcessInbound("whatsapp", "60123000999", "[audio]", accountId: acct.Id, wamid: "wa.f1");

        Assert.Empty(r.Baileys.Voice);
        Assert.Single(r.Baileys.Texts);
    }

    [Fact]
    public async Task Cloning_and_deleting_talk_to_each_provider_correctly()
    {
        using var r = new VoiceRig();
        r.Sc.SetSetting("voicestudio_base_url", "http://vs.test:3900");
        r.Sc.SetSetting("voicestudio_api_key", "vs-key");
        var speech = new SpeechClient(r.Sc, r.Speech);
        Assert.Equal(new[] { "elevenlabs", "voicestudio" }, speech.CloneProviders());

        var el = await speech.CloneVoiceAsync("elevenlabs", "Boss", FakeSpeech.Ogg, "me.webm", "audio/webm", "ms", "d");
        Assert.True(el.Ok, el.Detail);
        Assert.Equal("el-clone-1", el.RemoteId);
        var elCall = r.Speech.Calls.Single(c => c.Path == "/v1/voices/add");
        Assert.Equal("el-key", elCall.Key);
        Assert.Contains("name=files", elCall.Body.Replace("\"", ""));

        var vs = await speech.CloneVoiceAsync("voicestudio", "Boss", FakeSpeech.Ogg, "me.webm", "audio/webm", "ms", "d");
        Assert.True(vs.Ok, vs.Detail);
        Assert.Equal("vs-profile-1", vs.RemoteId);
        var vsCall = r.Speech.Calls.Single(c => c.Path == "/profiles");
        Assert.Equal("http://vs.test:3900", vsCall.Origin);
        Assert.Equal("vs-key", vsCall.Key);
        Assert.Contains("name=ref_audio", vsCall.Body.Replace("\"", ""));
        Assert.Contains("clone", vsCall.Body);

        Assert.True((await speech.DeleteRemoteVoiceAsync("voicestudio", "vs-profile-1")).Ok);
        Assert.True((await speech.DeleteRemoteVoiceAsync("elevenlabs", "el-clone-1")).Ok);
        Assert.Contains(r.Speech.Calls, c => c.Path == "/profiles/vs-profile-1");
        Assert.Contains(r.Speech.Calls, c => c.Path == "/v1/voices/el-clone-1");

        var imported = await speech.ElevenVoiceAsync("abc123");
        Assert.True(imported.Ok);
        Assert.Equal("Imported Ella", imported.Name);
    }

    [Fact]
    public async Task Several_clips_all_reach_ElevenLabs_but_VoiceStudio_takes_the_first()
    {
        using var r = new VoiceRig();
        r.Sc.SetSetting("voicestudio_base_url", "http://vs.test:3900");
        var speech = new SpeechClient(r.Sc, r.Speech);
        var clips = Enumerable.Range(1, 3)
            .Select(i => new VoiceClip(FakeSpeech.Ogg, $"take-{i}.ogg", "audio/ogg")).ToList();

        Assert.True((await speech.CloneVoiceAsync("elevenlabs", "Boss", clips, "en", "")).Ok);
        var el = r.Speech.Calls.Single(c => c.Path == "/v1/voices/add").Body.Replace("\"", "");
        Assert.Equal(3, el.Split("name=files").Length - 1);
        foreach (var i in new[] { 1, 2, 3 }) Assert.Contains($"take-{i}.ogg", el);

        Assert.True((await speech.CloneVoiceAsync("voicestudio", "Boss", clips, "en", "")).Ok);
        var vs = r.Speech.Calls.Single(c => c.Path == "/profiles").Body.Replace("\"", "");
        Assert.Equal(1, vs.Split("name=ref_audio").Length - 1);
        Assert.Contains("take-1.ogg", vs);

        var tooMany = Enumerable.Range(0, SpeechClient.MaxCloneClips + 1)
            .Select(_ => new VoiceClip(FakeSpeech.Ogg, "x.ogg", "audio/ogg")).ToList();
        Assert.False((await speech.CloneVoiceAsync("elevenlabs", "Boss", tooMany, "en", "")).Ok);
    }

    [Fact]
    public async Task Nothing_set_up_means_no_clone_providers_and_an_honest_refusal()
    {
        using var r = new VoiceRig();
        r.Sc.SetSetting("elevenlabs_api_key", "");
        var speech = new SpeechClient(r.Sc, r.Speech);
        Assert.Empty(speech.CloneProviders());
        Assert.False(speech.TtsConfigured);
        var res = await speech.CloneVoiceAsync("voicestudio", "x", FakeSpeech.Ogg, "a.webm", "audio/webm", "en", "");
        Assert.False(res.Ok);
        Assert.Contains("VoiceStudio is not set up", res.Detail);
        Assert.Empty(r.Speech.Calls);
    }
}

/// <summary>The voice-library endpoints over HTTP, with no speech provider configured.</summary>
public class VoiceLibraryApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public VoiceLibraryApiTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Clone_and_preview_say_what_to_set_up_instead_of_a_bare_501()
    {
        // a clone stored with no gender / accent (what production had) — the list must still fill them
        var path = Path.Combine(ApiFactory.DataDir, "voices.json");
        Directory.CreateDirectory(ApiFactory.DataDir);
        if (!File.Exists(path))
            File.WriteAllText(path, """{"voices":[{"id":"custom-bare","name":"Kx","language":"en","persona":"advisor","provider":"elevenlabs","status":"ready","custom":true,"elevenVoiceId":"abc"}]}""");
        var admin = await Platform.As(_f, "admin", "admin123");
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/voices");
        Assert.Equal(JsonValueKind.Array, list.GetProperty("clone_providers").ValueKind);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Me"), "name");
        form.Add(new StringContent("en"), "language");
        var audio = new ByteArrayContent(FakeSpeech.Ogg);
        audio.Headers.ContentType = new("audio/ogg");
        form.Add(audio, "audio", "me.ogg");
        var resp = await admin.PostAsync("/api/voice/clone", form);
        if (list.GetProperty("clone_providers").GetArrayLength() == 0)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.Contains("VoiceStudio", await resp.Content.ReadAsStringAsync());
        }

        // every voice carries the display fields the page reads, even one saved without them
        foreach (var v in list.GetProperty("voices").EnumerateArray())
            foreach (var key in new[] { "gender", "accent", "language", "persona", "provider" })
                Assert.Equal(JsonValueKind.String, v.GetProperty(key).ValueKind);

        // the upload takes several recordings in one request (the same field repeated)
        using var multi = new MultipartFormDataContent();
        multi.Add(new StringContent("Me"), "name");
        multi.Add(new StringContent("en"), "language");
        for (var i = 0; i < 3; i++)
        {
            var clip = new ByteArrayContent(FakeSpeech.Ogg);
            clip.Headers.ContentType = new("audio/ogg");
            multi.Add(clip, "audio", $"take-{i}.ogg");
        }
        var multiResp = await admin.PostAsync("/api/voice/clone", multi);
        Assert.NotEqual(HttpStatusCode.BadRequest, multiResp.StatusCode);

        var unknown = await admin.PostAsJsonAsync("/api/voice/preview", new { voiceId = "custom-nope", text = "Hi" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var puck = await admin.PostAsJsonAsync("/api/voice/preview", new { voiceId = "puck", text = "Hi" });
        Assert.Equal(HttpStatusCode.BadRequest, puck.StatusCode);
    }
}

/// <summary>POST /api/voice/clone-from — the by-reference clone the MCP tool voice_clone_voice calls.</summary>
public class VoiceCloneFromApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public VoiceCloneFromApiTests(ApiFactory f) => _f = f;

    private static object Clip(string name = "take.ogg") =>
        new { file_base64 = Convert.ToBase64String(FakeSpeech.Ogg), file_name = name };

    [Fact]
    public async Task It_needs_consent_audio_sources_and_then_a_provider()
    {
        var admin = await Platform.As(_f, "admin", "admin123");

        var noConsent = await admin.PostAsJsonAsync("/api/voice/clone-from",
            new { name = "Ah Kow", language = "ms", sources = new[] { Clip() } });
        Assert.Equal((HttpStatusCode)422, noConsent.StatusCode);
        Assert.Contains("consent_required", await noConsent.Content.ReadAsStringAsync());

        var noSources = await admin.PostAsJsonAsync("/api/voice/clone-from",
            new { name = "Ah Kow", language = "ms", consent = true, sources = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, noSources.StatusCode);

        var notAudio = await admin.PostAsJsonAsync("/api/voice/clone-from", new
        {
            name = "Ah Kow", language = "ms", consent = true,
            sources = new[] { new { file_base64 = Convert.ToBase64String("hello"u8.ToArray()), file_name = "notes.txt" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, notAudio.StatusCode);
        Assert.Contains("not an audio file", await notAudio.Content.ReadAsStringAsync());

        var missingMessage = await admin.PostAsJsonAsync("/api/voice/clone-from", new
        {
            name = "Ah Kow", language = "ms", consent = true, sources = new[] { new { source_message_id = 987654321 } },
        });
        Assert.Equal(HttpStatusCode.NotFound, missingMessage.StatusCode);

        // well-formed: three clips read; with no provider configured on the test host it says what to set up
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/voices");
        var ok = await admin.PostAsJsonAsync("/api/voice/clone-from", new
        {
            name = "Ah Kow", language = "ms", consent = true, sources = new[] { Clip("a.ogg"), Clip("b.ogg"), Clip("c.ogg") },
        });
        if (list.GetProperty("clone_providers").GetArrayLength() == 0)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ok.StatusCode);
            Assert.Contains("VoiceStudio", await ok.Content.ReadAsStringAsync());
        }

        var agent = await Platform.As(_f, "agent", "agent123");
        var denied = await agent.PostAsJsonAsync("/api/voice/clone-from",
            new { name = "x", language = "en", consent = true, sources = new[] { Clip() } });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
