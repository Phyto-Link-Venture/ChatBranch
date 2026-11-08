using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Making a WhatsApp sticker out of an ordinary picture (<see cref="StickerImage"/>).
/// The container work — reading the webp's canvas, telling an animated one apart, and
/// writing the EXIF block WhatsApp reads the pack name from — is pure and always
/// checked; the ffmpeg renderings only run where the machine has ffmpeg (the API image
/// installs it), because the point of the fallback is that a server without it refuses
/// honestly instead of sending something WhatsApp drops.
/// </summary>
public class StickerTests
{
    // ---- the webp container ------------------------------------------------

    [Fact]
    public void Reads_the_canvas_and_spots_an_animated_webp()
    {
        var still = Webp(512, 512, animated: false);
        var moving = Webp(512, 512, animated: true);

        Assert.True(StickerImage.IsWebp(still));
        Assert.Equal((512, 512), StickerImage.CanvasOf(still));
        Assert.False(StickerImage.IsAnimatedWebp(still));
        Assert.True(StickerImage.IsAnimatedWebp(moving));

        Assert.False(StickerImage.IsWebp(Encoding.ASCII.GetBytes("not a webp at all")));
        Assert.Null(StickerImage.CanvasOf(Encoding.ASCII.GetBytes("nope")));
    }

    [Fact]
    public void A_truncated_webp_is_read_without_throwing()
    {
        var full = Webp(512, 512, animated: false);
        for (var cut = 12; cut < full.Length; cut += 3)
        {
            var part = full[..cut];
            StickerImage.IsAnimatedWebp(part);
            StickerImage.CanvasOf(part);
            StickerImage.WithExif(part, "Pack", "Me");
        }
    }

    [Fact]
    public void Exif_carries_the_pack_json_at_the_offset_whatsapp_reads()
    {
        var exif = StickerImage.ExifPayload("Demo Pack", "Demo Co", new[] { "⭐", "🎉" });

        Assert.Equal(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, exif[..4]);          // "II", 42
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(exif.AsSpan(4, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(exif.AsSpan(8, 2)));
        Assert.Equal(0x5741, BinaryPrimitives.ReadUInt16LittleEndian(exif.AsSpan(10, 2)));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(exif.AsSpan(12, 2)));
        // the count is the json length and the value sits at offset 22, right after the header
        var len = BinaryPrimitives.ReadUInt32LittleEndian(exif.AsSpan(14, 4));
        Assert.Equal(22u, BinaryPrimitives.ReadUInt32LittleEndian(exif.AsSpan(18, 4)));
        Assert.Equal(exif.Length - 22, (int)len);

        var json = JsonDocument.Parse(exif.AsSpan(22).ToArray()).RootElement;
        Assert.Equal("Demo Pack", json.GetProperty("sticker-pack-name").GetString());
        Assert.Equal("Demo Co", json.GetProperty("sticker-pack-publisher").GetString());
        Assert.Equal(2, json.GetProperty("emojis").GetArrayLength());
    }

    [Fact]
    public void With_exif_sets_the_flag_replaces_an_old_block_and_keeps_chunk_order()
    {
        var stamped = StickerImage.WithExif(Webp(512, 512, animated: true), "Pack One", "Me");
        var chunks = Chunks(stamped);

        // VP8X first (WhatsApp needs the extended header), ANIM before the frames, EXIF last
        Assert.Equal("VP8X", chunks[0].Tag);
        Assert.Equal("ANIM", chunks[1].Tag);
        Assert.Equal("EXIF", chunks[^1].Tag);
        Assert.Equal(0x08, chunks[0].Payload[0] & 0x08);   // "this file carries EXIF"
        Assert.Equal(0x02, chunks[0].Payload[0] & 0x02);   // the ANIM flag it already had
        Assert.Equal(4 + stamped.Length - 12, (int)BinaryPrimitives.ReadUInt32LittleEndian(stamped.AsSpan(4, 4)));

        // stamping twice replaces the block rather than stacking a second one
        var again = StickerImage.WithExif(stamped, "Pack Two", "Me");
        Assert.Single(Chunks(again).Where(c => c.Tag == "EXIF"));
        var json = Chunks(again).First(c => c.Tag == "EXIF").Payload.AsSpan(22).ToArray();
        Assert.Equal("Pack Two", JsonDocument.Parse(json).RootElement
            .GetProperty("sticker-pack-name").GetString());
    }

    [Fact]
    public void A_simple_webp_with_no_vp8x_is_left_alone_rather_than_rebuilt()
    {
        // no extended header to set the EXIF flag on: the sticker still works, it just
        // carries no pack — better than a container assembled by guesswork
        var simple = SimpleWebp();
        Assert.Same(simple, StickerImage.WithExif(simple, "Pack", "Me"));
    }

    [Fact]
    public void Refusal_names_the_size_the_shape_and_the_format()
    {
        Assert.Null(StickerImage.Refusal(Webp(512, 512, animated: false)));

        Assert.Contains("WebP", StickerImage.Refusal(Encoding.ASCII.GetBytes("a png, really"))!);
        Assert.Contains("512×512", StickerImage.Refusal(Webp(320, 240, animated: false))!);
        Assert.Contains("320×240", StickerImage.Refusal(Webp(320, 240, animated: false))!);

        // a still over 100 KB is refused; the same bytes as an ANIMATED sticker are fine,
        // because WhatsApp's ceiling is five times higher for those
        var big = Webp(512, 512, animated: false, padding: 120 * 1024);
        Assert.Contains("100 KB", StickerImage.Refusal(big)!);
        Assert.Null(StickerImage.Refusal(Webp(512, 512, animated: true, padding: 120 * 1024)));
        Assert.Contains("500 KB", StickerImage.Refusal(Webp(512, 512, animated: true, padding: 600 * 1024))!);
    }

    [Fact]
    public void Only_picture_and_short_clip_types_are_offered()
    {
        Assert.True(StickerImage.CanMake("image/png"));
        Assert.True(StickerImage.CanMake("image/webp"));
        Assert.True(StickerImage.CanMake("image/gif"));
        Assert.True(StickerImage.CanMake("video/mp4"));
        Assert.True(StickerImage.CanMake("image/jpeg; charset=binary"));
        Assert.False(StickerImage.CanMake("application/pdf"));
        Assert.False(StickerImage.CanMake("audio/ogg"));
        Assert.False(StickerImage.CanMake(""));
    }

    // ---- the real renderings ----------------------------------------------

    [Fact]
    public async Task A_wide_photo_becomes_a_512_square_still_sticker()
    {
        if (!AudioTranscoder.Available) return; // no converter on this machine
        var dir = Temp();
        var src = Path.Combine(dir, "wide.png");
        Assert.True(Ffmpeg("-f", "lavfi", "-i", "testsrc=size=900x300:duration=1:rate=1",
            "-frames:v", "1", src), "could not make the test picture");

        var made = await StickerImage.MakeAsync(src, "image/png", "ChatBranch", "Demo Co", new[] { "⭐" });

        Assert.True(made.Ok, made.Error);
        Assert.False(made.Animated);
        Assert.Null(StickerImage.Refusal(made.Webp!));
        Assert.Equal((512, 512), StickerImage.CanvasOf(made.Webp!));
        Assert.True(made.Webp!.Length <= StickerImage.MaxStaticBytes,
            $"{made.Webp.Length} bytes is over WhatsApp's still-sticker cap");
        Assert.Contains("EXIF", Chunks(made.Webp).Select(c => c.Tag));

        // the square is reached by padding, not by squashing: ffmpeg reads it back at 512²
        var png = Path.Combine(dir, "back.png");
        Assert.True(Ffmpeg("-i", Write(dir, "s.webp", made.Webp), "-frames:v", "1", png));
        Assert.Equal((512, 512), PngSize(png));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task An_animated_gif_becomes_an_animated_sticker_within_the_cap()
    {
        if (!AudioTranscoder.Available) return;
        var dir = Temp();
        var src = Path.Combine(dir, "anim.gif");
        Assert.True(Ffmpeg("-f", "lavfi", "-i", "testsrc=size=400x300:duration=3:rate=15", src));

        var made = await StickerImage.MakeAsync(src, "image/gif", "ChatBranch", "Demo Co");

        Assert.True(made.Ok, made.Error);
        Assert.True(made.Animated);
        Assert.True(StickerImage.IsAnimatedWebp(made.Webp!));
        Assert.Null(StickerImage.Refusal(made.Webp!));
        Assert.True(made.Webp!.Length <= StickerImage.MaxAnimatedBytes);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task An_animated_webp_is_passed_through_stamped_and_refused_at_the_wrong_size()
    {
        if (!AudioTranscoder.Available) return;
        var dir = Temp();
        var gif = Path.Combine(dir, "a.gif");
        Assert.True(Ffmpeg("-f", "lavfi", "-i", "testsrc=size=400x300:duration=2:rate=12", gif));
        var sticker = await StickerImage.MakeAsync(gif, "image/gif", "P", "A");
        Assert.True(sticker.Ok, sticker.Error);

        // ffmpeg cannot DECODE an animated webp, so this path must not try: it stamps and sends
        var path = Write(dir, "already.webp", sticker.Webp!);
        var again = await StickerImage.MakeAsync(path, "image/webp", "Pack Two", "Author Two");
        Assert.True(again.Ok, again.Error);
        Assert.True(again.Animated);
        var json = Chunks(again.Webp!).First(c => c.Tag == "EXIF").Payload.AsSpan(22).ToArray();
        Assert.Equal("Pack Two", JsonDocument.Parse(json).RootElement
            .GetProperty("sticker-pack-name").GetString());

        // one that is not 512² cannot be resized here, and says so
        var odd = Write(dir, "odd.webp", Webp(256, 256, animated: true));
        var refused = await StickerImage.MakeAsync(odd, "image/webp", "P", "A");
        Assert.False(refused.Ok);
        Assert.Contains("512×512", refused.Error);
        Assert.Contains("cannot be resized", refused.Error);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task A_webp_that_is_already_a_sticker_is_not_re_encoded()
    {
        if (!AudioTranscoder.Available) return;
        var dir = Temp();
        var src = Path.Combine(dir, "wide.png");
        Assert.True(Ffmpeg("-f", "lavfi", "-i", "testsrc=size=900x300:duration=1:rate=1", "-frames:v", "1", src));
        var first = await StickerImage.MakeAsync(src, "image/png", "Pack One", "Me");
        Assert.True(first.Ok, first.Error);

        // feeding its own output back only re-stamps the pack: the image bytes are untouched,
        // so re-sending a sticker somebody already made costs it no quality
        var again = await StickerImage.MakeAsync(Write(dir, "made.webp", first.Webp!), "image/webp",
            "Pack Two", "Someone Else");

        Assert.True(again.Ok, again.Error);
        Assert.False(again.Animated);
        var before = Chunks(first.Webp!).First(c => c.Tag == "VP8 ").Payload;
        var after = Chunks(again.Webp!).First(c => c.Tag == "VP8 ").Payload;
        Assert.Equal(before, after);
        var json = Chunks(again.Webp!).First(c => c.Tag == "EXIF").Payload.AsSpan(22).ToArray();
        Assert.Equal("Pack Two", JsonDocument.Parse(json).RootElement
            .GetProperty("sticker-pack-name").GetString());
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_is_refused_before_any_conversion()
    {
        var dir = Temp();
        var pdf = Write(dir, "doc.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 not a picture"));

        var made = await StickerImage.MakeAsync(pdf, "application/pdf", "P", "A");

        Assert.False(made.Ok);
        Assert.Contains("cannot be made into a sticker", made.Error);

        var gone = await StickerImage.MakeAsync(Path.Combine(dir, "gone.png"), "image/png", "P", "A");
        Assert.False(gone.Ok);
        Assert.Contains("missing", gone.Error);

        var empty = await StickerImage.MakeAsync(Write(dir, "empty.png", Array.Empty<byte>()), "image/png", "P", "A");
        Assert.False(empty.Ok);
        Assert.Contains("empty", empty.Error);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task With_no_ffmpeg_an_already_correct_webp_still_goes_out_and_a_photo_says_why_not()
    {
        var dir = Temp();
        var prev = Environment.GetEnvironmentVariable("MEDIA_FFMPEG");
        Environment.SetEnvironmentVariable("MEDIA_FFMPEG", "off");
        try
        {
            var ready = Write(dir, "ready.webp", Webp(512, 512, animated: false));
            var ok = await StickerImage.MakeAsync(ready, "image/webp", "Pack", "Me");
            Assert.True(ok.Ok, ok.Error);
            Assert.Contains("EXIF", Chunks(ok.Webp!).Select(c => c.Tag));

            var wrongSize = Write(dir, "small.webp", Webp(128, 128, animated: false));
            Assert.Contains("512×512", (await StickerImage.MakeAsync(wrongSize, "image/webp", "P", "A")).Error);

            var photo = Write(dir, "p.png", Encoding.ASCII.GetBytes("\x89PNG\r\n\x1a\n pretend"));
            var refused = await StickerImage.MakeAsync(photo, "image/png", "P", "A");
            Assert.False(refused.Ok);
            Assert.Contains("ffmpeg", refused.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEDIA_FFMPEG", prev);
            Directory.Delete(dir, true);
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-sticker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string dir, string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static bool Ffmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(AudioTranscoder.FfmpegPath()!)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);
        return p.ExitCode == 0;
    }

    private static (int, int) PngSize(string path)
    {
        var b = File.ReadAllBytes(path);
        return (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20, 4)));
    }

    /// <summary>A RIFF/WEBP file with a VP8X header of the given canvas — enough for the
    /// container readers, without needing an encoder.</summary>
    private static byte[] Webp(int width, int height, bool animated, int padding = 0)
    {
        var chunks = new List<(string, byte[])>();
        var vp8x = new byte[10];
        vp8x[0] = (byte)(0x10 | (animated ? 0x02 : 0x00)); // alpha, and ANIM when animated
        vp8x[4] = (byte)((width - 1) & 0xFF);
        vp8x[5] = (byte)((width - 1) >> 8 & 0xFF);
        vp8x[6] = (byte)((width - 1) >> 16 & 0xFF);
        vp8x[7] = (byte)((height - 1) & 0xFF);
        vp8x[8] = (byte)((height - 1) >> 8 & 0xFF);
        vp8x[9] = (byte)((height - 1) >> 16 & 0xFF);
        chunks.Add(("VP8X", vp8x));
        if (animated)
        {
            chunks.Add(("ANIM", new byte[6]));
            chunks.Add(("ANMF", new byte[16 + padding]));
        }
        else
        {
            chunks.Add(("VP8 ", new byte[32 + padding]));
        }
        return Riff(chunks);
    }

    /// <summary>A plain lossy webp: no VP8X, so nowhere to flag EXIF.</summary>
    private static byte[] SimpleWebp() => Riff(new List<(string, byte[])> { ("VP8 ", new byte[40]) });

    private static byte[] Riff(List<(string Tag, byte[] Payload)> chunks)
    {
        var body = chunks.Sum(c => 8 + c.Payload.Length + (c.Payload.Length & 1));
        var all = new byte[12 + body];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(all, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(all.AsSpan(4, 4), (uint)(4 + body));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(all, 8);
        var at = 12;
        foreach (var (tag, payload) in chunks)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(all, at);
            BinaryPrimitives.WriteUInt32LittleEndian(all.AsSpan(at + 4, 4), (uint)payload.Length);
            payload.CopyTo(all, at + 8);
            at += 8 + payload.Length;
            if ((payload.Length & 1) != 0) at++;
        }
        return all;
    }

    private static List<(string Tag, byte[] Payload)> Chunks(byte[] b)
    {
        var list = new List<(string, byte[])>();
        var off = 12;
        while (off + 8 <= b.Length)
        {
            var tag = Encoding.ASCII.GetString(b, off, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off + 4, 4));
            if (size < 0 || off + 8 + size > b.Length) break;
            var payload = new byte[size];
            Array.Copy(b, off + 8, payload, 0, size);
            list.Add((tag, payload));
            off += 8 + size + (size & 1);
        }
        return list;
    }
}

/// <summary>
/// <see cref="OutboundSender.SendSticker"/> on an isolated database — the branches the
/// API-level tests cannot reach once any live number exists: no account at all (the
/// sandbox row), and a conversation on a channel that has no stickers.
/// </summary>
public class StickerTransportTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly MediaBaileys _baileys = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cb-stk-{Guid.NewGuid():N}");
    private readonly ThreadStore _threads;
    private readonly MediaStore _media;
    private readonly OutboundSender _sender;

    public StickerTransportTests()
    {
        var sc = new ScopingService(_t.Db);
        _threads = new ThreadStore(_t.Db, sc);
        _media = new MediaStore(_dir);
        _sender = new OutboundSender(_t.Db, _threads, new WhatsAppCloudClient(new FakeHttpFactory(new FakeGraph())),
            _baileys, null, _media);
    }

    public void Dispose()
    {
        _t.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A sticker-shaped webp, without needing an encoder.</summary>
    private static byte[] AWebp(bool animated = false)
    {
        var chunks = new List<(string, byte[])>();
        var vp8x = new byte[10];
        vp8x[0] = (byte)(0x10 | (animated ? 0x02 : 0x00));
        vp8x[4] = 0xFF; vp8x[5] = 0x01;   // 512
        vp8x[7] = 0xFF; vp8x[8] = 0x01;
        chunks.Add(("VP8X", vp8x));
        chunks.Add((animated ? "ANMF" : "VP8 ", new byte[64]));
        var body = chunks.Sum(c => 8 + c.Item2.Length + (c.Item2.Length & 1));
        var all = new byte[12 + body];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(all, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(all.AsSpan(4, 4), (uint)(4 + body));
        System.Text.Encoding.ASCII.GetBytes("WEBP").CopyTo(all, 8);
        var at = 12;
        foreach (var (tag, payload) in chunks)
        {
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(all, at);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(all.AsSpan(at + 4, 4), (uint)payload.Length);
            payload.CopyTo(all, at + 8);
            at += 8 + payload.Length + (payload.Length & 1);
        }
        return all;
    }

    [Fact]
    public async Task With_no_number_at_all_the_chat_still_shows_what_was_meant()
    {
        var thread = _threads.GetOrCreateThread("whatsapp", "60123000001");
        var webp = AWebp();
        var rel = await _media.SaveAsync("whatsapp", thread.Key, webp, "webp");

        var (msg, error, conflict) = _sender.SendSticker(thread, webp, rel, MessageSource.Agent);

        Assert.Equal("", error);
        Assert.False(conflict);
        Assert.NotNull(msg);
        // the truth: nothing left the building
        Assert.Equal("sandbox", msg!.Status);
        Assert.Equal("[sticker]", msg.Text);
        // stored exactly as an INBOUND sticker is, so the inbox needs no second code path
        Assert.Equal("image", msg.MediaType);
        Assert.Equal("image/webp", msg.MediaMime);
        Assert.Equal(rel, msg.MediaPath);
        Assert.Empty(_baileys.Media);
    }

    [Fact]
    public async Task A_qr_number_sends_it_as_a_sticker_with_no_caption()
    {
        var qr = new ChannelAccount
        {
            Channel = "whatsapp", Provider = Providers.Baileys, Name = "QR", ExternalId = "gw-stk",
            CreatedAt = DateTime.UtcNow,
        };
        _t.Db.ChannelAccounts.Add(qr);
        _t.Db.SaveChanges();
        var thread = _threads.GetOrCreateThread("whatsapp", "60123000002", accountId: qr.Id);
        var webp = AWebp(animated: true);
        var rel = await _media.SaveAsync("whatsapp", thread.Key, webp, "webp");

        var (msg, error, _) = _sender.SendSticker(thread, webp, rel, MessageSource.Agent);

        Assert.Equal("", error);
        Assert.Equal("sent", msg!.Status);
        Assert.Equal("3EB0MEDIA1", msg.ExternalId);
        var call = Assert.Single(_baileys.Media);
        Assert.Equal(("sticker", "image/webp"), (call.Type, call.Mime));
        Assert.Null(call.Caption);      // WhatsApp shows no caption on a sticker
        Assert.Equal(webp, call.Content);
    }

    [Fact]
    public async Task A_conversation_on_another_channel_is_refused_by_name()
    {
        var page = new ChannelAccount
        {
            Channel = "facebook", Provider = "meta", Name = "Page", ExternalId = "page-1",
            CreatedAt = DateTime.UtcNow,
        };
        _t.Db.ChannelAccounts.Add(page);
        _t.Db.SaveChanges();
        var thread = _threads.GetOrCreateThread("facebook", "psid-1", accountId: page.Id);
        var webp = AWebp();
        var rel = await _media.SaveAsync("facebook", thread.Key, webp, "webp");

        var (msg, error, conflict) = _sender.SendSticker(thread, webp, rel, MessageSource.Agent);

        Assert.Null(msg);
        Assert.True(conflict);
        Assert.Contains("facebook", error);
        Assert.Empty(_baileys.Media);
    }

    [Fact]
    public async Task A_file_that_is_not_sticker_shaped_never_reaches_the_gateway()
    {
        var thread = _threads.GetOrCreateThread("whatsapp", "60123000003");
        var notASticker = System.Text.Encoding.ASCII.GetBytes("\x89PNG\r\n\x1a\n this is a png");
        var rel = await _media.SaveAsync("whatsapp", thread.Key, notASticker, "png");

        var (msg, error, _) = _sender.SendSticker(thread, notASticker, rel, MessageSource.Agent);

        Assert.Null(msg);
        Assert.Contains("WebP", error);
        Assert.Empty(_baileys.Media);
        Assert.Empty(_t.Db.Messages.ToList());   // no bubble for a send that never happened
    }
}
