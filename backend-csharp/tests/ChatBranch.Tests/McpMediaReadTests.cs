using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Ai.Voice;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A host whose speech-to-text goes to <see cref="FakeSpeech"/> instead of a provider.</summary>
public class MediaReadFactory : ApiFactory
{
    internal FakeSpeech Speech { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddScoped(sp =>
        {
            var sc = sp.GetRequiredService<ScopingService>();
            return new VoiceTranscriber(sp.GetRequiredService<AppDbContext>(), sc,
                sp.GetRequiredService<MediaStore>(), new SpeechClient(sc, Speech));
        }));
    }
}

/// <summary>
/// An AI reading a chat over MCP SEES its media: pictures as image blocks, voice notes as
/// transcripts (made on demand, stored once), documents as text — not "[image]" / "[audio]".
/// </summary>
public class McpMediaReadTests : IClassFixture<MediaReadFactory>
{
    private readonly MediaReadFactory _f;
    public McpMediaReadTests(MediaReadFactory f) => _f = f;

    // a real 1×1 PNG
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private (int Image, int Audio, int Csv, int Gone) Seed(string key)
    {
        return Platform.WithDb(_f, db =>
        {
            var store = new ScopeStore(_f);
            var t = db.Threads.Single(x => x.Channel == "whatsapp" && x.Key == key);
            Message Add(string text, string type, string rel, byte[]? bytes, string name, string mime)
            {
                if (bytes != null)
                {
                    var full = Path.Combine(store.Media.Root("whatsapp"), rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, bytes);
                }
                var m = new Message
                {
                    ThreadId = t.Id, Direction = MessageDirection.In, Source = MessageSource.Customer,
                    Text = text, Status = "received", MediaType = type, MediaPath = rel, MediaName = name,
                    MediaMime = mime, CreatedAt = DateTime.UtcNow,
                };
                db.Messages.Add(m);
                db.SaveChanges();
                return m;
            }
            var img = Add("[image]", "image", $"{key}/photo.png", Png, "photo.png", "image/png");
            var audio = Add("[audio]", "audio", $"{key}/note.ogg", FakeSpeech.Ogg, "", "audio/ogg");
            var csv = Add("[document]", "document", $"{key}/prices.csv",
                System.Text.Encoding.UTF8.GetBytes("item,price\nnasi lemak,6.50\n"), "prices.csv", "text/csv");
            var gone = Add("[image]", "image", $"{key}/missing.jpg", null, "missing.jpg", "image/jpeg");
            return (img.Id, audio.Id, csv.Id, gone.Id);
        });
    }

    private sealed class ScopeStore
    {
        public MediaStore Media { get; }
        public ScopeStore(MediaReadFactory f) => Media = f.Services.GetRequiredService<MediaStore>();
    }

    private static string Json(JsonElement r) => r.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

    private static List<JsonElement> Blocks(JsonElement r, string type) =>
        r.GetProperty("content").EnumerateArray().Where(b => b.GetProperty("type").GetString() == type).ToList();

    private static JsonElement Msg(JsonElement result, int pk) =>
        result.GetProperty("structuredContent").GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("pk").GetInt32() == pk);

    [Fact]
    public async Task Reading_a_chat_shows_pictures_transcribes_voice_notes_and_describes_files()
    {
        Platform.WithDb(_f, db =>
        {
            db.Settings.RemoveRange(db.Settings.Where(s => s.Key == "openai_api_key" && s.EntityId == null));
            db.Settings.Add(new Setting { Key = "openai_api_key", Value = "sk-test", UpdatedAt = DateTime.UtcNow });
            return db.SaveChanges();
        });
        var admin = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        var session = await Platform.As(_f, "admin", "admin123");
        await Platform.Simulate(session, "whatsapp", "60133300091", "see attached");
        var (img, audio, csv, gone) = Seed("60133300091");

        var r = await Platform.CallTool(admin, "get_conversation", new { channel = "whatsapp", key = "60133300091" });
        Assert.False(r.GetProperty("isError").GetBoolean(), Json(r));

        // the picture is an MCP image block, labelled with its message
        var images = Blocks(r, "image");
        Assert.Single(images);
        Assert.Equal("image/png", images[0].GetProperty("mimeType").GetString());
        Assert.Equal(Png, Convert.FromBase64String(images[0].GetProperty("data").GetString()!));
        Assert.Contains(Blocks(r, "text"), b => b.GetProperty("text").GetString()!.Contains($"message {img}"));
        Assert.Equal(1, r.GetProperty("structuredContent").GetProperty("images_attached").GetInt32());

        // the voice note was transcribed now — and stored, so nobody pays for it twice
        Assert.Equal(_f.Speech.Transcript, Msg(r, audio).GetProperty("transcript").GetString());
        Assert.Equal(_f.Speech.Transcript, Platform.WithDb(_f, db => db.Messages.Single(m => m.Id == audio).Transcript));
        Assert.Single(Platform.WithDb(_f, db => db.LlmUsage.Where(u => u.Feature == VoiceTranscriber.Feature
                                                                        && u.Ref == $"message:{audio}").ToList()));
        var calls = _f.Speech.Stt.Count;

        // files are described, with the way to open them; a lost file says so
        var doc = Msg(r, csv).GetProperty("media");
        Assert.Equal("document", doc.GetProperty("type").GetString());
        Assert.Equal("prices.csv", doc.GetProperty("name").GetString());
        Assert.Contains("conversations_get_media", doc.GetProperty("note").GetString());
        Assert.Contains("no longer on the server", Msg(r, gone).GetProperty("media").GetProperty("available").GetString());

        // the message list reads the same way, and does not transcribe again
        var page = await Platform.CallTool(admin, "conversations_list_messages", new { channel = "whatsapp", key = "60133300091" });
        Assert.False(page.GetProperty("isError").GetBoolean(), Json(page));
        Assert.Single(Blocks(page, "image"));
        Assert.Equal(_f.Speech.Transcript, Msg(page, audio).GetProperty("transcript").GetString());
        Assert.Equal(calls, _f.Speech.Stt.Count);

        // words only when asked
        var plain = await Platform.CallTool(admin, "get_conversation",
            new { channel = "whatsapp", key = "60133300091", include_media = false });
        Assert.Empty(Blocks(plain, "image"));

        // opening one file: the document as text, the picture as an image
        var opened = await Platform.CallTool(admin, "conversations_get_media", new { channel = "whatsapp", key = "60133300091", pk = csv });
        Assert.False(opened.GetProperty("isError").GetBoolean(), Json(opened));
        Assert.Contains("nasi lemak", opened.GetProperty("structuredContent").GetProperty("content").GetString());
        var pic = await Platform.CallTool(admin, "conversations_get_media", new { channel = "whatsapp", key = "60133300091", pk = img });
        Assert.Single(Blocks(pic, "image"));
        var lost = await Platform.CallTool(admin, "conversations_get_media", new { channel = "whatsapp", key = "60133300091", pk = gone });
        Assert.Contains("not on the server", lost.GetProperty("structuredContent").GetProperty("note").GetString());

        // a message of another conversation is not reachable through this one
        await Platform.Simulate(session, "whatsapp", "60133300092", "other chat");
        var other = Platform.WithDb(_f, db => db.Messages.First(m => m.Thread!.Key == "60133300092").Id);
        var cross = await Platform.CallTool(admin, "conversations_get_media", new { channel = "whatsapp", key = "60133300091", pk = other });
        Assert.True(cross.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Without_a_speech_engine_the_voice_note_says_why()
    {
        Platform.WithDb(_f, db =>
        {
            db.Settings.RemoveRange(db.Settings.Where(s => s.Key == "openai_api_key" || s.Key == "elevenlabs_api_key"));
            return db.SaveChanges();
        });
        var admin = Platform.Client(_f, Platform.MintKey(_f, "admin"));
        var session = await Platform.As(_f, "admin", "admin123");
        await Platform.Simulate(session, "whatsapp", "60133300093", "voice coming");
        var (_, audio, _, _) = Seed("60133300093");

        var r = await Platform.CallTool(admin, "conversations_get_media", new { channel = "whatsapp", key = "60133300093", pk = audio });
        Assert.False(r.GetProperty("isError").GetBoolean(), Json(r));
        Assert.Contains("speech-to-text", r.GetProperty("structuredContent").GetProperty("note").GetString());
    }
}
