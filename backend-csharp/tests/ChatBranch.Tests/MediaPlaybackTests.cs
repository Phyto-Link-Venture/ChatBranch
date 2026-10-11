using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Api.Endpoints;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Tests;

/// <summary>
/// Media in the inbox: the payload's mime / name / size (stored, or derived for
/// older rows), the signed stream links (Range, expiry, tampering, a deactivated or
/// out-of-scope person, API keys get none), the M4A rendering of Ogg voice notes
/// when ffmpeg is present, and the Baileys /media push answering non-2xx when it
/// stored nothing.
/// </summary>
public class MediaPlaybackTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public MediaPlaybackTests(ApiFactory f) => _f = f;

    private async Task<HttpClient> Authed(string user, string pass)
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private T InScope<T>(Func<AppDbContext, T> work)
    {
        using var scope = _f.Services.CreateScope();
        return work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>A WhatsApp conversation with one inbound attachment.</summary>
    private Message Seed(string key, byte[] bytes, string ext, string type, int? entityId = null,
        string name = "", string mime = "", long size = 0)
    {
        return InScope(db =>
        {
            var now = DateTime.UtcNow;
            var t = new Thread { Channel = "whatsapp", Key = key, Name = key, EntityId = entityId, CreatedAt = now, UpdatedAt = now };
            db.Threads.Add(t);
            db.SaveChanges();
            var store = _f.Services.GetRequiredService<MediaStore>();
            var m = new Message
            {
                ThreadId = t.Id, Direction = "in", Source = "customer", Text = $"[{type}]", Status = "received",
                MediaType = type, MediaName = name, MediaMime = mime, MediaSize = size, CreatedAt = now,
                MediaPath = Task.Run(() => store.SaveAsync("whatsapp", key, bytes, ext)).GetAwaiter().GetResult(),
            };
            db.Messages.Add(m);
            db.SaveChanges();
            return m;
        });
    }

    private static async Task<JsonElement> MediaOf(HttpClient c, string key, int pk)
    {
        var thread = await c.GetFromJsonAsync<JsonElement>($"/api/whatsapp/threads/{key}");
        return thread.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("pk").GetInt32() == pk).GetProperty("media");
    }

    private int ProfileId(string username) =>
        InScope(db => db.Profiles.First(p => p.User!.Username == username).Id);

    [Fact]
    public async Task Payload_carries_mime_name_size_and_a_signed_stream_that_honours_ranges()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 hello world");
        var msg = Seed("60150000001", bytes, ".pdf", "document", name: "Statement March.pdf",
            mime: "application/pdf", size: bytes.Length);
        var admin = await Authed("admin", "admin123");
        var media = await MediaOf(admin, "60150000001", msg.Id);
        Assert.Equal("document", media.GetProperty("type").GetString());
        Assert.Equal($"/api/whatsapp/media/{msg.Id}", media.GetProperty("url").GetString());
        Assert.Equal("application/pdf", media.GetProperty("mime").GetString());
        Assert.Equal("Statement March.pdf", media.GetProperty("name").GetString());
        Assert.Equal(bytes.Length, media.GetProperty("size").GetInt64());
        var stream = media.GetProperty("stream_url").GetString()!;
        Assert.StartsWith("/api/media/s/", stream);
        Assert.Equal(stream + "?download=1", media.GetProperty("download_url").GetString());
        Assert.False(media.TryGetProperty("alt_url", out _)); // not audio

        // anonymous at the door: the token is the credential; Range works
        var anon = _f.CreateClient();
        var ranged = new HttpRequestMessage(HttpMethod.Get, stream);
        ranged.Headers.Range = new RangeHeaderValue(0, 3);
        var part = await anon.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("%PDF", await part.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", part.Headers.GetValues("X-Content-Type-Options").Single());
        var whole = await anon.GetAsync(stream);
        whole.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", whole.Content.Headers.ContentType!.MediaType);
        // ?download=1 → an attachment under the customer's own file name
        var dl = await anon.GetAsync(stream + "?download=1");
        dl.EnsureSuccessStatusCode();
        Assert.Equal("attachment", dl.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("Statement March.pdf", dl.Content.Headers.ContentDisposition.ToString());

        // the same link for half an hour (a re-rendered thread keeps a playing src)
        var again = await MediaOf(admin, "60150000001", msg.Id);
        Assert.Equal(stream, again.GetProperty("stream_url").GetString());
    }

    [Fact]
    public async Task Tampered_and_expired_links_are_refused()
    {
        var msg = Seed("60150000002", new byte[] { 1, 2, 3, 4 }, ".png", "image");
        var admin = await Authed("admin", "admin123");
        var stream = (await MediaOf(admin, "60150000002", msg.Id)).GetProperty("stream_url").GetString()!;
        var anon = _f.CreateClient();
        (await anon.GetAsync(stream)).EnsureSuccessStatusCode();

        var token = stream["/api/media/s/".Length..];
        var parts = token.Split('.');
        // another message id under the same signature
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/media/s/{msg.Id + 1}.{parts[1]}.{parts[2]}.{parts[3]}")).StatusCode);
        // a later expiry
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/media/s/{parts[0]}.{parts[1]}.{long.Parse(parts[2]) + 60}.{parts[3]}")).StatusCode);
        // a forged signature, garbage, and a link "valid" for a year
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/media/s/{parts[0]}.{parts[1]}.{parts[2]}.AAAA{parts[3][4..]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/media/s/not-a-token")).StatusCode);
        var pid = ProfileId("admin");
        var yearLong = MediaStreamLinks.Sign(msg.Id, pid, "admin", DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/media/s/{yearLong}")).StatusCode);
        // a genuine link past its expiry → 410
        var expired = MediaStreamLinks.Sign(msg.Id, pid, "admin", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.Gone, (await anon.GetAsync($"/api/media/s/{expired}")).StatusCode);
        // signed for a different username (renamed user) → refused
        var renamed = MediaStreamLinks.Sign(msg.Id, pid, "someone-else", MediaStreamLinks.ExpiryFor(DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/media/s/{renamed}")).StatusCode);
    }

    [Fact]
    public async Task Links_are_rechecked_as_the_person_scope_and_active_flag()
    {
        var (north, south) = InScope(db =>
        {
            var tenant = db.Tenants.First(t => t.Slug == "demo");
            var now = DateTime.UtcNow;
            var n = new Entity { TenantId = tenant.Id, Name = "Play North", Slug = "play-north", CreatedAt = now, UpdatedAt = now };
            var s = new Entity { TenantId = tenant.Id, Name = "Play South", Slug = "play-south", CreatedAt = now, UpdatedAt = now };
            db.Entities.AddRange(n, s);
            db.SaveChanges();
            var agent = db.Profiles.Include(p => p.Entities).First(p => p.User!.Username == "agent");
            agent.Entities.Add(n);
            db.SaveChanges();
            return (n.Id, s.Id);
        });
        var mine = Seed("60150000003", new byte[] { 9, 9 }, ".jpg", "image", north);
        var theirs = Seed("60150000004", new byte[] { 8, 8 }, ".jpg", "image", south);
        var pid = ProfileId("agent");
        var exp = MediaStreamLinks.ExpiryFor(DateTimeOffset.UtcNow);
        var anon = _f.CreateClient();
        (await anon.GetAsync($"/api/media/s/{MediaStreamLinks.Sign(mine.Id, pid, "agent", exp)}")).EnsureSuccessStatusCode();
        // a link for a conversation outside the person's entities opens nothing
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/media/s/{MediaStreamLinks.Sign(theirs.Id, pid, "agent", exp)}")).StatusCode);

        // deactivated after the link was made → refused
        InScope(db =>
        {
            db.Users.First(u => u.Username == "agent").IsActive = false;
            return db.SaveChanges();
        });
        try
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await anon.GetAsync($"/api/media/s/{MediaStreamLinks.Sign(mine.Id, pid, "agent", exp)}")).StatusCode);
        }
        finally
        {
            InScope(db =>
            {
                db.Users.First(u => u.Username == "agent").IsActive = true;
                return db.SaveChanges();
            });
        }
    }

    [Fact]
    public async Task Old_rows_fall_back_to_the_extension_and_the_file_and_api_keys_get_no_link()
    {
        var msg = Seed("60150000005", new byte[] { 1, 2, 3, 4, 5, 6, 7 }, ".png", "image");
        var admin = await Authed("admin", "admin123");
        var media = await MediaOf(admin, "60150000005", msg.Id);
        Assert.Equal("image/png", media.GetProperty("mime").GetString());
        Assert.Equal($"image-{msg.Id}.png", media.GetProperty("name").GetString());
        Assert.Equal(7, media.GetProperty("size").GetInt64());

        var created = await admin.PostAsJsonAsync("/api/developers/keys", new { name = "media key" });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var keyClient = _f.CreateClient();
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", key);
        var viaKey = await MediaOf(keyClient, "60150000005", msg.Id);
        Assert.False(viaKey.TryGetProperty("stream_url", out _));
        Assert.Equal("image/png", viaKey.GetProperty("mime").GetString());
    }

    [Fact]
    public void Mime_table_names_heic_and_keeps_amr_out_of_inline()
    {
        Assert.Equal("image/heic", MediaStore.MimeFor("x.heic"));
        Assert.Equal("image/heif", MediaStore.MimeFor("x.HEIF"));
        Assert.False(MediaStore.IsInlineSafe("image/heic"));
        Assert.Equal("audio/amr", MediaStore.MimeFor("note.amr"));
        Assert.False(MediaStore.IsInlineSafe("audio/amr"));
        Assert.True(MediaStore.IsInlineSafe("audio/ogg"));
        Assert.Equal("audio/ogg", MediaStore.MimeFor("v.opus"));
        Assert.True(AudioTranscoder.CanTranscode("audio/ogg; codecs=opus"));
        Assert.True(AudioTranscoder.CanTranscode("audio/amr"));
        Assert.False(AudioTranscoder.CanTranscode("audio/mpeg"));
        Assert.Equal("Invoice.pdf", MediaStore.CleanName("../../etc/Invoice.pdf"));
        Assert.Equal("", MediaStore.CleanName(null));
    }

    [Fact]
    public void Telegram_video_stickers_play_and_animated_ones_show_their_thumbnail()
    {
        TelegramMedia? Of(string json) => TelegramInbound.Media(JsonDocument.Parse(json).RootElement);
        var video = Of("""{"sticker":{"file_id":"VID","is_video":true,"is_animated":false}}""");
        Assert.Equal(("VID", "video", "sticker.webm"), (video!.FileId, video.MediaType, video.FallbackName));
        var tgs = Of("""{"sticker":{"file_id":"TGS","is_animated":true,"thumbnail":{"file_id":"THUMB"}}}""");
        Assert.Equal(("THUMB", "image"), (tgs!.FileId, tgs.MediaType));
        var legacy = Of("""{"sticker":{"file_id":"TGS","is_animated":true,"thumb":{"file_id":"OLDTHUMB"}}}""");
        Assert.Equal("OLDTHUMB", legacy!.FileId);
        Assert.Null(Of("""{"sticker":{"file_id":"TGS","is_animated":true}}"""));
        Assert.Equal("image", Of("""{"sticker":{"file_id":"ST"}}""")!.MediaType);
    }

    [Fact]
    public async Task Ogg_voice_notes_get_an_m4a_rendering_when_ffmpeg_is_installed()
    {
        if (!AudioTranscoder.Available) return; // nothing to convert with on this machine
        var dir = Path.Combine(Path.GetTempPath(), $"cb-ogg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var ogg = Path.Combine(dir, "v.ogg");
        var psi = new ProcessStartInfo(AudioTranscoder.FfmpegPath()!)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=d=0.3",
                     "-c:a", "libopus", "-y", ogg })
            psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!)
        {
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 || !File.Exists(ogg)) return; // an ffmpeg without libopus
        }
        var msg = Seed("60150000006", await File.ReadAllBytesAsync(ogg), ".ogg", "audio", mime: "audio/ogg");
        var admin = await Authed("admin", "admin123");
        var media = await MediaOf(admin, "60150000006", msg.Id);
        var alt = media.GetProperty("alt_url").GetString()!;
        Assert.EndsWith("?format=m4a", alt);
        var anon = _f.CreateClient();
        var res = await anon.GetAsync(alt);
        res.EnsureSuccessStatusCode();
        Assert.Equal("audio/mp4", res.Content.Headers.ContentType!.MediaType);
        var body = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("ftyp", Encoding.ASCII.GetString(body, 4, 4)); // an MP4 container
        // cached next to the original
        var full = _f.Services.GetRequiredService<MediaStore>().Resolve("whatsapp", msg.MediaPath)!;
        Assert.True(File.Exists(full + ".m4a"));
        // an image has no audio rendering
        var img = Seed("60150000007", new byte[] { 1 }, ".png", "image");
        var imgStream = (await MediaOf(admin, "60150000007", img.Id)).GetProperty("stream_url").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync(imgStream + "?format=m4a")).StatusCode);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task Baileys_media_push_answers_non_2xx_when_nothing_was_stored_and_records_the_file()
    {
        var anon = _f.CreateClient();
        HttpRequestMessage Post(HttpContent content)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/whatsapp/baileys/media") { Content = content };
            req.Headers.Add("X-Baileys-Secret", "baileys-local-dev");
            return req;
        }
        // not multipart, and a form without a file → 400 (the gateway logs, never a silent 200)
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anon.SendAsync(Post(new StringContent("{}", Encoding.UTF8, "application/json")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.SendAsync(Post(new MultipartFormDataContent
        {
            { new StringContent("60150000008"), "wa_id" }, { new StringContent("BAE5NOFILE"), "msg_id" },
        }))).StatusCode);

        // the file arrives before the /inbound text: the row is created, the name / mime / size kept
        var form = new MultipartFormDataContent
        {
            { new StringContent("60150000008"), "wa_id" },
            { new StringContent("BAE5DOC1"), "msg_id" },
            { new StringContent("document"), "media_type" },
            { new StringContent("Quotation Q-12.pdf"), "file_name" },
        };
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7 q"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "BAE5DOC1.pdf");
        var ok = await anon.SendAsync(Post(form));
        ok.EnsureSuccessStatusCode();
        var msg = InScope(db => db.Messages.Single(m => m.ExternalId == "BAE5DOC1"));
        Assert.Equal(("document", "Quotation Q-12.pdf", "application/pdf", 10L),
            (msg.MediaType, msg.MediaName, msg.MediaMime, msg.MediaSize));

        // a voice note carries no file name: the payload names it by type
        var voice = new MultipartFormDataContent
        {
            { new StringContent("60150000008"), "wa_id" },
            { new StringContent("BAE5PTT1"), "msg_id" },
            { new StringContent("audio"), "media_type" },
            { new StringContent(""), "file_name" },
        };
        var ogg = new ByteArrayContent(new byte[] { 0x4f, 0x67, 0x67, 0x53 });
        ogg.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        voice.Add(ogg, "file", "BAE5PTT1.ogg");
        (await anon.SendAsync(Post(voice))).EnsureSuccessStatusCode();
        var ptt = InScope(db => db.Messages.Single(m => m.ExternalId == "BAE5PTT1"));
        Assert.Equal(("", "audio/ogg"), (ptt.MediaName, ptt.MediaMime));
        var admin = await Authed("admin", "admin123");
        var media = await MediaOf(admin, "60150000008", ptt.Id);
        Assert.Equal($"audio-{ptt.Id}.ogg", media.GetProperty("name").GetString());
        Assert.Equal(AudioTranscoder.Available, media.TryGetProperty("alt_url", out _));
    }
}
