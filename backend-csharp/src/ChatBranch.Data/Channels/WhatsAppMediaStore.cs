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
        "audio/ogg", "audio/mpeg", "audio/mp4", "audio/aac", "audio/wav", "audio/x-wav", "audio/amr",
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
        [".wav"] = "audio/wav", [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".csv"] = "text/csv",
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
        await File.WriteAllBytesAsync(full, bytes, ct);
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
        await using (var dest = File.Create(full))
        {
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > maxBytes) break;
                await dest.WriteAsync(buffer.AsMemory(0, n), ct);
            }
        }
        if (total > maxBytes)
        {
            try { File.Delete(full); } catch { }
            return null;
        }
        return rel.Replace('\\', '/');
    }

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
        return File.Exists(full) ? full : null;
    }
}
