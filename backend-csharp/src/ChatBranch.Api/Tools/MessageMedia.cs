using System.Diagnostics;
using ChatBranch.Data.Ai.Learning;
using ChatBranch.Data.Ai.Voice;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Documents;
using ChatBranch.Domain;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Tools;

/// <summary>
/// What a message's ATTACHMENT says, for an AI reading a conversation over MCP.
///
/// Before this, a reading model saw "[image]" / "[audio]" and nothing else: the payload's
/// media URL needs the dashboard's session (an MCP client cannot open it), the tool result
/// was a single text block, and voice notes were only transcribed when the CHATBOT replied —
/// so a chat with the AI off stayed "[audio]" forever. Now the read tools
/// (<c>get_conversation</c>, <c>conversations_list_messages</c>, <c>conversations_get_media</c>):
///   * transcribe audio on demand (<see cref="VoiceTranscriber.TranscribeMessagesAsync"/> — the
///     same engine, quota, metering and stored transcript as the chatbot's pass);
///   * hand pictures back as MCP image blocks (<see cref="ToolImage"/>) — the newest few in a
///     page, any one in full through get_media — shrunk by ffmpeg when large or in a format a
///     model does not take (HEIC…);
///   * read documents as text (PDF / Word / Excel / PowerPoint / CSV, via
///     <see cref="DocumentExtractor"/>), a scanned PDF as page images, a video as a still frame.
/// Nothing here throws for a bad file: what cannot be read says why, in words.
/// </summary>
public static class MessageMedia
{
    public const int DefaultImages = 4;
    public const int MaxImages = 8;
    /// <summary>One picture as sent to the model (a larger one is re-encoded smaller).</summary>
    public const long MaxImageBytes = 1_500_000;
    /// <summary>All pictures of one tool call together.</summary>
    public const long MaxTotalBytes = 6_000_000;
    /// <summary>Longest side a model gains anything from (Claude's own guidance is ~1568 px).</summary>
    public const int MaxSide = 1568;
    /// <summary>A page's worth of audio transcribed per read before the reply would drag.</summary>
    public const int MaxTranscribe = 8;
    public const int MaxDocumentChars = 20_000;

    private static readonly HashSet<string> NativeImage = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/webp", "image/gif" };

    public const string NoSttEngine =
        "No speech-to-text engine is set up (Settings → AI keys & models → Advanced → Transcription).";

    /// <summary>
    /// A page of messages as the read tools return them — id/pk, who, words, when — with each
    /// attachment described and audio transcripts filled in. With <paramref name="includeMedia"/>
    /// it transcribes what has no transcript yet and attaches the newest
    /// <paramref name="maxImages"/> pictures to <c>ctx.Images</c>.
    /// </summary>
    public static async Task<(List<Dictionary<string, object?>> Messages, int ImagesShown)> ShapeAsync(
        ToolContext ctx, Thread t, IReadOnlyList<Message> msgs, bool includeMedia, int maxImages, CancellationToken ct)
    {
        var store = ctx.Services.GetService<MediaStore>();
        string? FullPath(Message m) => store != null && m.MediaPath.Length > 0 ? store.Resolve(t.Channel, m.MediaPath) : null;

        var transcripts = new Dictionary<int, string>();
        var sttMissing = false;
        var shown = new HashSet<int>();
        if (includeMedia)
        {
            var audio = msgs.Where(m => m.MediaType == "audio" && m.MediaPath.Length > 0
                                        && string.IsNullOrEmpty(m.Transcript) && m.RecalledAt == null)
                .OrderByDescending(m => m.Id).Take(MaxTranscribe).Select(m => m.Id).ToList();
            if (audio.Count > 0)
            {
                var stt = ctx.Services.GetService<VoiceTranscriber>();
                if (stt is { Available: true })
                    transcripts = await stt.TranscribeMessagesAsync(t, audio, TimeSpan.FromSeconds(25), ct);
                else sttMissing = true;
            }

            long budget = MaxTotalBytes;
            var pictures = msgs.Where(m => m.MediaType == "image" && m.RecalledAt == null)
                .OrderByDescending(m => m.Id).Take(Math.Clamp(maxImages, 0, MaxImages)).Reverse().ToList();
            foreach (var m in pictures)
            {
                if (FullPath(m) is not { } full) continue;
                var img = await PrepareImageAsync(full, Mime(m, full), MaxSide, ct);
                if (img == null || img.Value.Data.Length > budget) continue;
                budget -= img.Value.Data.Length;
                ctx.Images.Add(new ToolImage(Label(m, "Image"), img.Value.Mime, img.Value.Data));
                shown.Add(m.Id);
            }
        }

        var shaped = new List<Dictionary<string, object?>>();
        foreach (var m in msgs)
        {
            var row = new Dictionary<string, object?>
            {
                ["id"] = m.Id,
                ["pk"] = m.Id,
                ["from"] = From(m),
                ["text"] = ChatBranchTool.Clip(m.Text, 1200),
                ["status"] = m.Status,
                ["at"] = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc).ToString("o"),
            };
            var said = transcripts.TryGetValue(m.Id, out var fresh) ? fresh : m.Transcript;
            if (!string.IsNullOrEmpty(said))
                row["transcript"] = said == "…" ? "(no speech heard)" : ChatBranchTool.Clip(said, 4000);
            if (m.MediaType.Length > 0 || m.MediaPath.Length > 0)
            {
                var full = FullPath(m);
                var media = Describe(m, full);
                if (shown.Contains(m.Id)) media["shown"] = "attached below as an image";
                else if (m.MediaType == "audio" && string.IsNullOrEmpty(said) && full != null)
                    media["note"] = sttMissing ? NoSttEngine
                        : includeMedia ? "Not transcribed yet — conversations_get_media with this pk tries again."
                        : "conversations_get_media with this pk transcribes it.";
                else if (full != null && m.MediaType != "audio")
                    media["note"] = "conversations_get_media with this pk opens it"
                        + (m.MediaType switch { "document" => " (read as text)", "video" => " (a still frame)", _ => "" }) + ".";
                row["media"] = media;
            }
            shaped.Add(row);
        }
        return (shaped, shown.Count);
    }

    /// <summary>type / name / mime / size of an attachment, and whether the file is on the server.</summary>
    public static Dictionary<string, object?> Describe(Message m, string? fullPath)
    {
        var d = new Dictionary<string, object?> { ["type"] = m.MediaType.Length > 0 ? m.MediaType : "file" };
        if (m.MediaName.Length > 0) d["name"] = m.MediaName;
        if (fullPath != null) d["mime"] = Mime(m, fullPath);
        var size = m.MediaSize > 0 ? m.MediaSize : fullPath != null ? MediaStore.SizeOf(fullPath) : 0;
        if (size > 0) d["size_kb"] = Math.Max(1, (int)(size / 1024));
        if (fullPath == null)
            d["available"] = m.MediaPath.Length == 0
                ? "never received — only the placeholder arrived"
                : "the file is no longer on the server";
        return d;
    }

    public static string Mime(Message m, string fullPath) =>
        m.MediaMime.Length > 0 ? m.MediaMime : MediaStore.MimeFor(fullPath);

    private static string From(Message m) =>
        m.Private ? "note" : m.Direction == MessageDirection.In ? "customer" : m.Source;

    public static string Label(Message m, string what) =>
        $"{what} — message {m.Id}, from {(m.Direction == MessageDirection.In ? "the customer" : m.Private ? "a private note" : "us (" + m.Source + ")")}, "
        + $"{DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc):yyyy-MM-dd HH:mm} UTC"
        + (m.MediaName.Length > 0 ? $", file {m.MediaName}" : "");

    // ---- pictures --------------------------------------------------------------

    /// <summary>
    /// The picture as a model takes it: a JPEG / PNG / WebP / GIF under
    /// <see cref="MaxImageBytes"/> as it is; anything larger or in another format (HEIC,
    /// BMP, TIFF…) re-encoded by ffmpeg as a JPEG of at most <paramref name="maxSide"/> px.
    /// Null when it cannot be done (no ffmpeg, a format ffmpeg does not decode).
    /// </summary>
    public static async Task<(byte[] Data, string Mime)?> PrepareImageAsync(string full, string mime, int maxSide,
        CancellationToken ct)
    {
        try
        {
            var bare = mime.Split(';')[0].Trim().ToLowerInvariant();
            var len = new FileInfo(full).Length;
            if (len == 0) return null;
            if (NativeImage.Contains(bare) && len <= MaxImageBytes)
                return (await File.ReadAllBytesAsync(full, ct), bare);
            return await FrameAsync(full, null, maxSide, ct) is { } jpeg ? (jpeg, "image/jpeg") : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>One still of a video (about a second in, else its first frame) as a JPEG.</summary>
    public static async Task<byte[]?> VideoFrameAsync(string full, CancellationToken ct) =>
        await FrameAsync(full, "1", MaxSide, ct) ?? await FrameAsync(full, null, MaxSide, ct);

    /// <summary>ffmpeg → one JPEG frame, scaled down to fit; stepped smaller until it fits the cap.</summary>
    private static async Task<byte[]?> FrameAsync(string full, string? seek, int maxSide, CancellationToken ct)
    {
        foreach (var (side, q) in new[] { (maxSide, "4"), (1024, "7"), (768, "10") })
        {
            var bytes = await RunFfmpegAsync(full, seek, side, q, ct);
            if (bytes == null) return null;
            if (bytes.Length <= MaxImageBytes) return bytes;
        }
        return null;
    }

    private static async Task<byte[]?> RunFfmpegAsync(string full, string? seek, int side, string quality,
        CancellationToken ct)
    {
        var ffmpeg = AudioTranscoder.FfmpegPath();
        if (ffmpeg == null) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"cb-frame-{Guid.NewGuid():N}.jpg");
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" };
        if (seek != null) { args.Add("-ss"); args.Add(seek); }
        args.AddRange(new[]
        {
            "-i", full, "-frames:v", "1", "-an",
            "-vf", $"scale='min({side},iw)':'min({side},ih)':force_original_aspect_ratio=decrease",
            "-q:v", quality, "-f", "image2", tmp,
        });
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            var err = p.StandardError.ReadToEndAsync(ct);
            var outp = p.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return null;
            }
            await Task.WhenAll(err, outp);
            if (p.ExitCode != 0 || !File.Exists(tmp)) return null;
            var bytes = await File.ReadAllBytesAsync(tmp, ct);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    // ---- one attachment, opened ------------------------------------------------

    /// <summary>
    /// <c>conversations_get_media</c>: open one message's attachment for the model — a picture
    /// as an image block, audio as its transcript (made now when missing), a document as its
    /// text (a scanned PDF as its first pages), a video as a still frame.
    /// </summary>
    public static async Task<object> OpenAsync(ToolContext ctx, Thread t, Message m, CancellationToken ct)
    {
        var store = ctx.Services.GetService<MediaStore>();
        var full = store != null && m.MediaPath.Length > 0 ? store.Resolve(t.Channel, m.MediaPath) : null;
        if (m.MediaType.Length == 0 && m.MediaPath.Length == 0)
            throw new ToolException($"Message {m.Id} has no attachment — it reads: {ChatBranchTool.Clip(m.Text, 200)}");
        var result = new Dictionary<string, object?>
        {
            ["pk"] = m.Id,
            ["from"] = From(m),
            ["text"] = ChatBranchTool.Clip(m.Text, 1200),
            ["media"] = Describe(m, full),
        };
        if (full == null)
        {
            result["note"] = "The file itself is not on the server, so it cannot be opened.";
            return result;
        }
        var mime = Mime(m, full);
        var name = m.MediaName.Length > 0 ? m.MediaName : Path.GetFileName(full);
        switch (m.MediaType)
        {
            case "image":
            {
                var img = await PrepareImageAsync(full, mime, MaxSide, ct);
                if (img == null) { result["note"] = $"This picture ({mime}) could not be converted for viewing."; break; }
                ctx.Images.Add(new ToolImage(Label(m, "Image"), img.Value.Mime, img.Value.Data));
                result["shown"] = "attached below as an image";
                break;
            }
            case "video":
            {
                var frame = await VideoFrameAsync(full, ct);
                if (frame == null) { result["note"] = "No frame could be taken from this video."; break; }
                ctx.Images.Add(new ToolImage(Label(m, "Video (one still frame)"), "image/jpeg", frame));
                result["shown"] = "one still frame attached below — its sound is not transcribed";
                break;
            }
            case "audio":
            {
                var said = m.Transcript;
                if (string.IsNullOrEmpty(said))
                {
                    var stt = ctx.Services.GetService<VoiceTranscriber>();
                    if (stt is not { Available: true }) { result["note"] = NoSttEngine; break; }
                    var done = await stt.TranscribeMessagesAsync(t, new[] { m.Id }, TimeSpan.FromSeconds(45), ct);
                    said = done.TryGetValue(m.Id, out var s) ? s : "";
                }
                if (string.IsNullOrEmpty(said))
                    result["note"] = "It could not be transcribed (the engine refused it, it is too large, or the AI quota is reached).";
                else result["transcript"] = said == "…" ? "(no speech heard)" : said;
                break;
            }
            default:
                await ReadDocumentAsync(ctx, m, full, name, mime, result, ct);
                break;
        }
        return result;
    }

    private static async Task ReadDocumentAsync(ToolContext ctx, Message m, string full, string name, string mime,
        Dictionary<string, object?> result, CancellationToken ct)
    {
        // a file whose name lost its extension: borrow one from the type
        if (!DocumentExtractor.ReadableByTasks(name) && mime == "application/pdf") name += ".pdf";
        if (!DocumentExtractor.ReadableByTasks(name))
        {
            result["note"] = $"A {Path.GetExtension(name).TrimStart('.').ToUpperInvariant()} file can't be read here "
                             + "— PDF, Word, Excel, PowerPoint, CSV and text files can.";
            return;
        }
        if (new FileInfo(full).Length > DocumentExtractor.MaxBytes * 2)
        {
            result["note"] = "The file is too large to read here.";
            return;
        }
        var bytes = await File.ReadAllBytesAsync(full, ct);
        var doc = DocumentExtractor.Extract(name, bytes);
        if (doc.Text.Trim().Length > 0)
        {
            var text = doc.Text.Trim();
            result["content"] = text.Length > MaxDocumentChars ? text[..MaxDocumentChars] : text;
            if (doc.Pages > 0) result["pages"] = doc.Pages;
            if (doc.Truncated || text.Length > MaxDocumentChars) result["truncated"] = true;
            return;
        }
        // no text layer: a scanned PDF — show its first pages as pictures instead
        if (Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var pages = DocumentTemplateAi.RasterizePages(bytes, 3, 110, jpeg: true);
            long budget = MaxTotalBytes;
            var i = 0;
            foreach (var (page, pageMime) in pages)
            {
                i++;
                if (page.Length > budget) break;
                budget -= page.Length;
                ctx.Images.Add(new ToolImage(Label(m, $"Scanned PDF, page {i}"), pageMime, page));
            }
            if (i > 0) { result["shown"] = $"no text layer (a scan) — its first {i} page(s) attached below as images"; return; }
        }
        result["note"] = doc.Error ?? "No text could be read from this file.";
    }
}
