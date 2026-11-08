using System.Text.Json;
using Xunit;

namespace ChatBranch.Tests;

/// <summary><c>conversations_send_media</c> with <c>voice_note: true</c>: the audio goes out through
/// the microphone's route (<c>/send-voice</c>) as a real voice note (ptt + Ogg/Opus — the bubble with
/// the waveform), never as an audio attachment. Its own fixture: a database with no live WhatsApp
/// account, so the note lands as a sandbox row instead of reaching a provider.</summary>
public class McpVoiceNoteTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public McpVoiceNoteTests(ApiFactory f) => _f = f;

    private static async Task<JsonElement> Call(HttpClient c, object args) =>
        await Platform.CallTool(c, "conversations_send_media", args);

    private static string Text(JsonElement r) => r.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    [Fact]
    public async Task A_voice_note_goes_out_as_a_voice_note_not_a_file()
    {
        var admin = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        var session = await Platform.As(_f, "admin", "admin123");
        await Platform.Simulate(session, "whatsapp", "60133300077", "send me a voice note");
        var ogg = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("OggS-fake-opus"));

        var ok = await Call(admin, new { channel = "whatsapp", key = "60133300077", file_base64 = ogg,
            file_name = "reply.ogg", voice_note = true });
        Assert.False(ok.GetProperty("isError").GetBoolean(), Text(ok));
        var msg = Platform.WithDb(_f, db => db.Messages
            .Where(m => m.Thread!.Key == "60133300077" && m.Direction == "out").OrderByDescending(m => m.Id).First());
        // the voice-note route's own row: its words and the audio attached
        Assert.Equal("[voice note]", msg.Text);
        Assert.Equal("audio", msg.MediaType);
        Assert.Equal("voice.ogg", msg.MediaName);

        // a voice note has no caption, needs audio, and is WhatsApp only — each refused with why
        var withCaption = await Call(admin, new { channel = "whatsapp", key = "60133300077", file_base64 = ogg,
            file_name = "reply.ogg", voice_note = true, caption = "hi" });
        Assert.True(withCaption.GetProperty("isError").GetBoolean());
        Assert.Contains("caption", Text(withCaption));
        var png = await Call(admin, new { channel = "whatsapp", key = "60133300077",
            file_base64 = "iVBORw0KGgo=", file_name = "dot.png", voice_note = true });
        Assert.True(png.GetProperty("isError").GetBoolean());
        Assert.Contains("recording", Text(png));
        var noFile = await Call(admin, new { channel = "whatsapp", key = "60133300077", voice_note = true });
        Assert.True(noFile.GetProperty("isError").GetBoolean());
        Assert.Contains("file_base64", Text(noFile));
    }
}
