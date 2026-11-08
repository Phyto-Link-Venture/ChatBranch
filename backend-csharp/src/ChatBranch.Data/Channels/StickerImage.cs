using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ChatBranch.Data.Channels;

/// <summary>What <see cref="StickerImage.MakeAsync"/> produced, or why it could not.</summary>
public readonly record struct StickerResult(byte[]? Webp, bool Animated, string Error)
{
    public bool Ok => Webp != null && Webp.Length > 0;
    public static StickerResult Fail(string why) => new(null, false, why);
}

/// <summary>
/// Turning an ordinary picture into a WhatsApp STICKER.
///
/// A sticker is not simply a webp file: WhatsApp wants exactly 512×512, a
/// transparent canvas, under 100 KB still / 500 KB animated, and an EXIF block
/// naming the pack and its author (without it the sticker renders but has no
/// attribution and cannot be added to the viewer's tray). Push an arbitrary
/// image through as a <c>stickerMessage</c> and WhatsApp renders it wrong or
/// drops it — so everything here runs BEFORE the gateway is called.
///
/// Three input paths, because ffmpeg's webp decoder cannot read an ANIMATED webp:
///   * animated webp — passed through untouched (only the EXIF is set); it must
///     already be 512×512, and is refused honestly when it is not;
///   * animated gif / a short clip — ffmpeg → animated webp, trimmed to
///     <see cref="MaxSeconds"/> at <see cref="AnimatedFps"/>;
///   * anything else — ffmpeg → one 512×512 still.
/// Either way the quality is walked down until the file fits WhatsApp's ceiling,
/// and a sticker that still will not fit is refused rather than sent to be dropped.
///
/// ffmpeg is the same seam the audio renderings use (<see cref="AudioTranscoder.FfmpegPath"/>,
/// env <c>MEDIA_FFMPEG</c>); with no ffmpeg an already-correct webp still goes out
/// and anything else is refused naming it, never silently.
/// </summary>
public static class StickerImage
{
    /// <summary>WhatsApp's sticker canvas — not negotiable.</summary>
    public const int Size = 512;
    public const long MaxStaticBytes = 100L * 1024;
    public const long MaxAnimatedBytes = 500L * 1024;
    /// <summary>How much of a clip becomes a sticker (WhatsApp plays a short loop).</summary>
    public const int MaxSeconds = 6;
    public const int AnimatedFps = 12;
    /// <summary>Source files big enough that converting is pointless.</summary>
    public const long MaxSourceBytes = 32L * 1024 * 1024;

    public const string Mime = "image/webp";

    /// <summary>Quality ladder: the first rendering that fits WhatsApp's cap wins.</summary>
    private static readonly int[] StillQuality = { 85, 70, 55, 40, 25 };
    private static readonly int[] AnimatedQuality = { 65, 50, 35, 20 };

    private static readonly HashSet<string> Still = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/webp", "image/bmp", "image/gif",
        "image/tiff", "image/x-ms-bmp", "image/heic", "image/heif", "image/avif",
    };

    private static readonly HashSet<string> Moving = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/gif", "video/mp4", "video/webm", "video/quicktime", "video/3gpp", "video/x-matroska",
    };

    /// <summary>True for a file ChatBranch will try to turn into a sticker.</summary>
    public static bool CanMake(string? mime)
    {
        var m = Bare(mime);
        return Still.Contains(m) || Moving.Contains(m);
    }

    private static string Bare(string? mime) => (mime ?? "").Split(';')[0].Trim();

    // ---- the webp container -------------------------------------------------

    /// <summary>A RIFF/WEBP file (by its own magic, not its name).</summary>
    public static bool IsWebp(byte[] b) =>
        b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
        && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

    /// <summary>
    /// True for an animated webp — the one kind ffmpeg cannot decode, so it has to
    /// be recognised before any conversion is attempted. Read from the VP8X ANIM
    /// flag, falling back to the presence of a frame chunk.
    /// </summary>
    public static bool IsAnimatedWebp(byte[] b)
    {
        if (!IsWebp(b)) return false;
        foreach (var (tag, start, length) in Chunks(b))
        {
            if (tag == "VP8X" && length >= 1 && (b[start] & 0x02) != 0) return true;
            if (tag == "ANMF" || tag == "ANIM") return true;
        }
        return false;
    }

    /// <summary>The webp canvas, or null when it cannot be read (no VP8X header).</summary>
    public static (int Width, int Height)? CanvasOf(byte[] b)
    {
        if (!IsWebp(b)) return null;
        foreach (var (tag, start, length) in Chunks(b))
        {
            if (tag != "VP8X" || length < 10) continue;
            var w = b[start + 4] | b[start + 5] << 8 | b[start + 6] << 16;
            var h = b[start + 7] | b[start + 8] << 8 | b[start + 9] << 16;
            return (w + 1, h + 1);
        }
        return null;
    }

    /// <summary>Every top-level RIFF chunk as (tag, payload start, payload length).</summary>
    private static IEnumerable<(string Tag, int Start, int Length)> Chunks(byte[] b)
    {
        var off = 12;
        while (off + 8 <= b.Length)
        {
            var tag = Encoding.ASCII.GetString(b, off, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off + 4, 4)), int.MaxValue);
            if (size < 0 || off + 8 + size > b.Length) yield break; // truncated — stop, never throw
            yield return (tag, off + 8, size);
            off += 8 + size + (size & 1);
        }
    }

    /// <summary>
    /// The EXIF payload WhatsApp reads a sticker's pack from: a minimal little-endian
    /// TIFF header with ONE undefined-type entry whose value is the pack JSON. The
    /// 22-byte header carries the JSON's length at offset 14 and its offset (22) at 18.
    /// </summary>
    public static byte[] ExifPayload(string packName, string author, IReadOnlyList<string>? emojis = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["sticker-pack-id"] = "com.chatbranch.stickers",
            ["sticker-pack-name"] = Trim(packName, 128),
            ["sticker-pack-publisher"] = Trim(author, 128),
            ["emojis"] = (emojis ?? Array.Empty<string>()).Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim()).Take(3).ToArray(),
        });
        var head = new byte[]
        {
            0x49, 0x49, 0x2A, 0x00,         // "II", 42 — little-endian TIFF
            0x08, 0x00, 0x00, 0x00,         // first IFD at offset 8
            0x01, 0x00,                     // one entry
            0x41, 0x57,                     // the tag WhatsApp looks for
            0x07, 0x00,                     // type 7 (UNDEFINED)
            0x00, 0x00, 0x00, 0x00,         // count — the JSON length, written below
            0x16, 0x00, 0x00, 0x00,         // value offset: 22, straight after this header
        };
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(14, 4), (uint)json.Length);
        var all = new byte[head.Length + json.Length];
        head.CopyTo(all, 0);
        json.CopyTo(all, head.Length);
        return all;
    }

    private static string Trim(string? s, int max)
    {
        var v = (s ?? "").Trim();
        return v.Length > max ? v[..max] : v;
    }

    /// <summary>
    /// The same webp with the sticker pack's EXIF chunk (any existing one replaced)
    /// and the VP8X EXIF flag set. A webp with no VP8X header — a plain still with no
    /// alpha — is returned UNCHANGED: the sticker works without attribution, and
    /// rebuilding the container by hand to add one is not worth a malformed file.
    /// </summary>
    public static byte[] WithExif(byte[] webp, string packName, string author,
        IReadOnlyList<string>? emojis = null)
    {
        if (!IsWebp(webp)) return webp;
        var kept = new List<(string Tag, byte[] Payload)>();
        var extended = false;
        foreach (var (tag, start, length) in Chunks(webp))
        {
            if (tag == "EXIF") continue; // ours replaces it
            var payload = new byte[length];
            Array.Copy(webp, start, payload, 0, length);
            if (tag == "VP8X" && length >= 1)
            {
                payload[0] |= 0x08; // this file now carries EXIF
                extended = true;
            }
            kept.Add((tag, payload));
        }
        if (!extended || kept.Count == 0) return webp;
        kept.Add(("EXIF", ExifPayload(packName, author, emojis)));

        var body = kept.Sum(c => 8 + c.Payload.Length + (c.Payload.Length & 1));
        var outBytes = new byte[12 + body];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(outBytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(4, 4), (uint)(4 + body));
        Encoding.ASCII.GetBytes("WEBP").CopyTo(outBytes, 8);
        var at = 12;
        foreach (var (tag, payload) in kept)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(outBytes, at);
            BinaryPrimitives.WriteUInt32LittleEndian(outBytes.AsSpan(at + 4, 4), (uint)payload.Length);
            payload.CopyTo(outBytes, at + 8);
            at += 8 + payload.Length;
            if ((payload.Length & 1) != 0) outBytes[at++] = 0; // chunks are even-aligned
        }
        return outBytes;
    }

    /// <summary>Why these bytes cannot go out as a sticker (null = they can).</summary>
    public static string? Refusal(byte[] webp)
    {
        if (!IsWebp(webp)) return "a sticker has to be a WebP image";
        var animated = IsAnimatedWebp(webp);
        var cap = animated ? MaxAnimatedBytes : MaxStaticBytes;
        if (webp.Length > cap)
            return $"the sticker is {webp.Length / 1024} KB — WhatsApp's limit is {cap / 1024} KB "
                   + $"for {(animated ? "an animated" : "a still")} sticker";
        var canvas = CanvasOf(webp);
        if (canvas != null && (canvas.Value.Width != Size || canvas.Value.Height != Size))
            return $"a sticker has to be {Size}×{Size} — this one is "
                   + $"{canvas.Value.Width}×{canvas.Value.Height}";
        return null;
    }

    // ---- making one --------------------------------------------------------

    /// <summary>
    /// Render <paramref name="sourcePath"/> into a WhatsApp sticker. Never throws:
    /// anything it will not send comes back as <see cref="StickerResult.Error"/>,
    /// worded for the operator who clicked send.
    /// </summary>
    public static async Task<StickerResult> MakeAsync(string sourcePath, string? mime, string packName,
        string author, IReadOnlyList<string>? emojis = null, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(sourcePath)) return StickerResult.Fail("the file is missing");
            var info = new FileInfo(sourcePath);
            if (info.Length == 0) return StickerResult.Fail("the file is empty");
            if (info.Length > MaxSourceBytes)
                return StickerResult.Fail($"the picture is too big to make a sticker from (max {MaxSourceBytes / 1024 / 1024} MB)");

            var source = await File.ReadAllBytesAsync(sourcePath, ct);

            // An animated webp is already in the one format ffmpeg cannot read back, so it
            // goes out as it is — stamped with the pack, and refused when it is the wrong
            // shape rather than resized into something WhatsApp drops.
            if (IsAnimatedWebp(source))
            {
                var stamped = WithExif(source, packName, author, emojis);
                var why = Refusal(stamped);
                if (why != null)
                    return StickerResult.Fail(why + " — an animated sticker cannot be resized here, "
                                                  + "so it has to be exported at that size");
                return new StickerResult(stamped, true, "");
            }

            // A still webp that is ALREADY a sticker is passed through: re-encoding one
            // somebody made (or that ChatBranch made earlier) only loses quality.
            if (IsWebp(source))
            {
                var ready = WithExif(source, packName, author, emojis);
                if (Refusal(ready) == null) return new StickerResult(ready, false, "");
            }

            var bare = Bare(mime);
            if (bare.Length > 0 && !CanMake(bare))
                return StickerResult.Fail($"that file type ({bare}) cannot be made into a sticker");

            if (AudioTranscoder.FfmpegPath() == null)
            {
                // No converter: a webp that is ALREADY a sticker still goes out.
                if (IsWebp(source))
                {
                    var stamped = WithExif(source, packName, author, emojis);
                    var why = Refusal(stamped);
                    if (why != null) return StickerResult.Fail(why);
                    return new StickerResult(stamped, false, "");
                }
                return StickerResult.Fail(
                    "this server has no ffmpeg, so it cannot turn a picture into a sticker — "
                    + "install ffmpeg, or upload a 512×512 WebP");
            }

            var moving = Moving.Contains(bare) || IsLikelyAnimatedGif(source);
            var made = await RenderAsync(sourcePath, moving, ct);
            if (!made.Ok) return made;

            var final = WithExif(made.Webp!, packName, author, emojis);
            var refused = Refusal(final);
            return refused != null ? StickerResult.Fail(refused) : new StickerResult(final, made.Animated, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return StickerResult.Fail("the picture could not be read");
        }
    }

    /// <summary>A GIF with more than one image descriptor — worth animating.</summary>
    private static bool IsLikelyAnimatedGif(byte[] b)
    {
        if (b.Length < 6 || b[0] != 'G' || b[1] != 'I' || b[2] != 'F') return false;
        var frames = 0;
        for (var i = 13; i < b.Length - 1 && frames < 2; i++)
            if (b[i] == 0x2C) frames++;    // image separator
        return frames > 1;
    }

    /// <summary>
    /// ffmpeg, once per quality step, until the webp fits WhatsApp's ceiling. An
    /// animated rendering that will not fit at the lowest quality drops to half the
    /// frame rate before giving up, because a slower sticker beats no sticker.
    /// </summary>
    private static async Task<StickerResult> RenderAsync(string sourcePath, bool animated, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-sticker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cap = animated ? MaxAnimatedBytes : MaxStaticBytes;
            var ladder = animated ? AnimatedQuality : StillQuality;
            byte[]? best = null;
            foreach (var fps in animated ? new[] { AnimatedFps, AnimatedFps / 2 } : new[] { 0 })
            {
                foreach (var q in ladder)
                {
                    if (ct.IsCancellationRequested) break;
                    var target = Path.Combine(dir, $"s-{fps}-{q}.webp");
                    if (!await RunAsync(sourcePath, target, animated, q, fps, ct)) continue;
                    var bytes = await File.ReadAllBytesAsync(target, ct);
                    if (bytes.Length == 0) continue;
                    best = bytes;
                    // the EXIF block is added after this, so leave room for it
                    if (bytes.Length + 512 <= cap) return new StickerResult(bytes, animated, "");
                }
            }
            if (best == null)
                return StickerResult.Fail("the picture could not be converted into a sticker");
            return StickerResult.Fail(
                $"even at the lowest quality this makes a {best.Length / 1024} KB sticker, over WhatsApp's "
                + $"{cap / 1024} KB limit — try a simpler picture"
                + (animated ? " or a shorter clip" : ""));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    private static async Task<bool> RunAsync(string source, string target, bool animated, int quality, int fps,
        CancellationToken ct)
    {
        var ffmpeg = AudioTranscoder.FfmpegPath();
        if (ffmpeg == null) return false;
        // scale inside the square, then pad the rest TRANSPARENT: a sticker has no
        // letterbox bars, and squashing the aspect ratio looks broken.
        var filter = $"scale={Size}:{Size}:force_original_aspect_ratio=decrease,"
                     + $"pad={Size}:{Size}:(ow-iw)/2:(oh-ih)/2:color=#00000000";
        if (animated && fps > 0) filter += $",fps={fps}";

        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source,
            "-vf", filter, "-pix_fmt", "yuva420p", "-q:v", quality.ToString(),
        };
        if (animated)
        {
            args.AddRange(new[] { "-t", MaxSeconds.ToString(), "-loop", "0", "-c:v", "libwebp_anim" });
        }
        else
        {
            args.AddRange(new[] { "-frames:v", "1", "-c:v", "libwebp" });
        }
        args.AddRange(new[] { "-f", "webp", target });

        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            var err = p.StandardError.ReadToEndAsync(ct);
            var outp = p.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return false;
            }
            await Task.WhenAll(err, outp);
            return p.ExitCode == 0 && File.Exists(target) && new FileInfo(target).Length > 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException
                                      or InvalidOperationException)
        {
            return false;
        }
    }
}
