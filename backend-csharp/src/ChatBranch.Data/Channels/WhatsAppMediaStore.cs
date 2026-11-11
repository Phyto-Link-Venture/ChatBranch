namespace ChatBranch.Data.Channels;

/// <summary>
/// Where message attachments live on disk (port of the Django <c>store.MEDIA_DIR</c>
/// layout), shared by every channel so ONE auth-gated route can serve them:
///
///   * WhatsApp (Cloud + Baileys): <c>{data}/whatsapp/media/{wa_id}/{file}</c>,
///     <c>Message.MediaPath = "{wa_id}/{file}"</c>
///   * every other channel: <c>{data}/messaging/media/{channel}/{key}/{file}</c>,
///     <c>Message.MediaPath = "{channel}/{key}/{file}"</c>
///
/// One media per message row (extra attachments become extra rows). File names
/// are server-minted GUIDs; the only caller-controlled part is the extension,
/// which is whitelisted to <c>[a-z0-9]</c>. <see cref="Resolve"/> refuses any path
/// that escapes the channel's root.
/// </summary>
public sealed class MediaStore
{
    /// <summary>Inbound cap (Meta allows bigger documents; we keep 20 MB like Django).</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    public MediaStore(string dataDir) => DataDir = dataDir;

    public string DataDir { get; }

    private static readonly Dictionary<string, string> ExtByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/webp"] = ".webp", ["image/gif"] = ".gif",
        ["video/mp4"] = ".mp4", ["video/3gpp"] = ".3gp", ["video/quicktime"] = ".mov",
        ["audio/ogg"] = ".ogg", ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a", ["audio/aac"] = ".aac",
        ["audio/amr"] = ".amr", ["audio/wav"] = ".wav", ["audio/x-wav"] = ".wav",
        ["audio/opus"] = ".opus", ["audio/webm"] = ".weba", ["video/webm"] = ".webm",
        ["image/heic"] = ".heic", ["image/heif"] = ".heif",
        ["application/pdf"] = ".pdf", ["text/plain"] = ".txt", ["text/csv"] = ".csv",
        ["application/msword"] = ".doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["application/zip"] = ".zip",
    };

    /// <summary>Content types a browser may render INLINE from our origin. Anything
    /// else (HTML, SVG, scripts…) is served as a download so a customer's
    /// attachment can never run in the dashboard's origin.</summary>
    private static readonly HashSet<string> InlineSafe = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif",
        // (no audio/amr: no browser plays it — it is a download, or transcoded on request)
        "audio/ogg", "audio/mpeg", "audio/mp4", "audio/aac", "audio/wav", "audio/x-wav", "audio/webm",
        "video/mp4", "video/3gpp", "video/quicktime", "video/webm",
        "application/pdf", "text/plain",
    };

    public string Root(string channel) => channel == ChatBranch.Domain.Channels.WhatsApp
        ? Path.Combine(DataDir, "whatsapp", "media")
        : Path.Combine(DataDir, "messaging", "media");

    /// <summary>Our coarse bubble type for a MIME type: image | video | audio | document.</summary>
    public static string TypeForMime(string? mime)
    {
        var m = (mime ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (m.StartsWith("image/")) return "image";
        if (m.StartsWith("video/")) return "video";
        if (m.StartsWith("audio/")) return "audio";
        return "document";
    }

    /// <summary>A safe extension: the original file name's (whitelisted chars), else the MIME's, else .bin.</summary>
    public static string ExtFor(string? mime, string? filename)
    {
        var fromName = Path.GetExtension(filename ?? "").ToLowerInvariant();
        if (fromName.Length is > 1 and <= 9 && fromName.Skip(1).All(char.IsAsciiLetterOrDigit)) return fromName;
        var m = (mime ?? "").Split(';')[0].Trim();
        return ExtByMime.TryGetValue(m, out var ext) ? ext : ".bin";
    }

    private static readonly Dictionary<string, string> MimeByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp",
        [".gif"] = "image/gif", [".mp4"] = "video/mp4", [".3gp"] = "video/3gpp", [".mov"] = "video/quicktime",
        [".webm"] = "video/webm", [".ogg"] = "audio/ogg", [".oga"] = "audio/ogg", [".opus"] = "audio/ogg",
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".aac"] = "audio/aac", [".amr"] = "audio/amr",
        [".wav"] = "audio/wav", [".weba"] = "audio/webm", [".heic"] = "image/heic", [".heif"] = "image/heif",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".csv"] = "text/csv",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".zip"] = "application/zip",
    };

    /// <summary>MIME for a stored file, from its extension.</summary>
    public static string MimeFor(string path) =>
        MimeByExt.TryGetValue(Path.GetExtension(path ?? ""), out var ct) ? ct : "application/octet-stream";

    /// <summary>
    /// Record a saved attachment on its message: the bubble type, the relative path,
    /// and what the payload shows — the original file name (path parts dropped,
    /// capped), the provider's MIME type (parameters dropped, else the extension's)
    /// and the size in bytes.
    /// </summary>
    public static void Stamp(ChatBranch.Domain.Message msg, string type, string relPath, string? mime,
        string? fileName, long size)
    {
        msg.MediaType = type;
        msg.MediaPath = relPath;
        msg.MediaName = CleanName(fileName);
        var m = (mime ?? "").Split(';')[0].Trim().ToLowerInvariant();
        msg.MediaMime = m.Length is > 2 and <= 100 && m.Contains('/') && m != "application/octet-stream"
            ? m : MimeFor(relPath);
        msg.MediaSize = Math.Max(0, size);
    }

    /// <summary>A file's size in bytes, 0 when it is missing or unreadable.</summary>
    public static long SizeOf(string? fullPath)
    {
        try { return fullPath != null && File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0; }
        catch { return 0; }
    }

    /// <summary>A file name fit to show and to put in a Content-Disposition: no path,
    /// no control characters, at most 120 characters ("" when nothing is left).</summary>
    public static string CleanName(string? fileName)
    {
        var n = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        n = new string(n.Where(c => !char.IsControl(c) && c is not '"' and not '/').ToArray()).Trim();
        if (n.Length > 120)
        {
            var ext = Path.GetExtension(n);
            n = ext.Length is > 0 and <= 10 ? n[..(120 - ext.Length)] + ext : n[..120];
        }
        return n;
    }

    /// <summary>True when the type may be rendered inline (else serve as an attachment).</summary>
    public static bool IsInlineSafe(string mime) => InlineSafe.Contains(mime);

    private static string SafeSegment(string raw)
    {
        var s = new string((raw ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (s.Length > 64) s = s[..64];
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>The MediaPath prefix for a conversation (relative to <see cref="Root"/>).</summary>
    private static string Prefix(string channel, string key) => channel == ChatBranch.Domain.Channels.WhatsApp
        ? SafeSegment(key)
        : Path.Combine(SafeSegment(channel), SafeSegment(key));

    /// <summary>Persist bytes and return the relative MediaPath to store on the Message.</summary>
    public async Task<string> SaveAsync(string channel, string key, byte[] bytes, string ext,
        CancellationToken ct = default)
    {
        var rel = Path.Combine(Prefix(channel, key), $"{Guid.NewGuid():N}{NormalizeExt(ext)}");
        var full = Path.Combine(Root(channel), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, ct).ConfigureAwait(false);
        return rel.Replace('\\', '/');
    }

    /// <summary>Persist a stream (copied with the 20 MB cap). null when it was too big.</summary>
    public async Task<string?> SaveAsync(string channel, string key, Stream source, string ext,
        long maxBytes = MaxBytes, CancellationToken ct = default)
    {
        var rel = Path.Combine(Prefix(channel, key), $"{Guid.NewGuid():N}{NormalizeExt(ext)}");
        var full = Path.Combine(Root(channel), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        long total = 0;
        var buffer = new byte[81920];
        var dest = File.Create(full);
        await using (dest.ConfigureAwait(false))
        {
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > maxBytes) break;
                await dest.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        if (total > maxBytes)
        {
            try { File.Delete(full); } catch { }
            return null;
        }
        return rel.Replace('\\', '/');
    }

    // ---- the media library (files uploaded once, sent by id) -----------------

    /// <summary>
    /// Where uploads kept for reuse live — not under a channel, because a library file
    /// belongs to nobody's conversation until it is sent.
    /// </summary>
    public string LibraryRoot() => Path.Combine(DataDir, "library");

    /// <summary>Persist a library upload; returns the relative path to store on the row.</summary>
    public async Task<string?> SaveLibraryAsync(Stream source, string ext, long maxBytes = MaxBytes,
        CancellationToken ct = default)
    {
        // a month per folder, so a long-lived install does not grow one huge directory
        var rel = Path.Combine(DateTime.UtcNow.ToString("yyyy-MM"), $"{Guid.NewGuid():N}{NormalizeExt(ext)}");
        var full = Path.Combine(LibraryRoot(), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        long total = 0;
        var buffer = new byte[81920];
        var dest = File.Create(full);
        await using (dest.ConfigureAwait(false))
        {
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > maxBytes) break;
                await dest.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        if (total > maxBytes)
        {
            try { File.Delete(full); } catch { }
            return null;
        }
        return rel.Replace('\\', '/');
    }

    /// <summary>The absolute path of a library upload, or null when it is missing or escapes the root.</summary>
    public string? ResolveLibrary(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        var root = Path.GetFullPath(LibraryRoot());
        var full = Path.GetFullPath(Path.Combine(root, relPath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return Exists(full) ? full : null;
    }

    /// <summary>Delete a library upload (and its offloaded copy). Never throws — the row going is what matters.</summary>
    public void DeleteLibrary(string relPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relPath)) return;
            var root = Path.GetFullPath(LibraryRoot());
            var full = Path.GetFullPath(Path.Combine(root, relPath));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
            MediaOffload.Current?.Delete(full);
            if (File.Exists(full)) File.Delete(full);
        }
        catch { /* a file already gone is the outcome we wanted */ }
    }

    /// <summary>On disk — after bringing it back from object storage when it was offloaded.</summary>
    private static bool Exists(string full) => MediaOffload.Current?.Hydrate(full) ?? File.Exists(full);

    private static string NormalizeExt(string ext)
    {
        var e = (ext ?? "").Trim().ToLowerInvariant();
        if (!e.StartsWith('.')) e = "." + e;
        return e.Length is > 1 and <= 10 && e.Skip(1).All(char.IsAsciiLetterOrDigit) ? e : ".bin";
    }

    /// <summary>
    /// The absolute path of a stored attachment, or null when it is missing or the
    /// relative path tries to leave the channel's media root.
    /// </summary>
    public string? Resolve(string channel, string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        var root = Path.GetFullPath(Root(channel));
        var full = Path.GetFullPath(Path.Combine(root, relPath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return Exists(full) ? full : null;
    }
}
