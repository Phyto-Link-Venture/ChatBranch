using System.Text.RegularExpressions;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Files attached to non-WhatsApp messages (email attachments, Telegram photos
/// and documents…) live under <c>{data}/messaging/media/{channel}/{key}/</c>;
/// <see cref="ChatBranch.Domain.Message.MediaPath"/> stores the path RELATIVE to
/// <c>{data}/messaging/media</c> and <c>GET /api/messaging/media/{pk}</c> serves
/// it auth-gated (attachments are PII, like message text). Port of
/// messaging/store.save_media_bytes.
/// </summary>
public static partial class MessagingMedia
{
    /// <summary>Hard cap for any one inbound or outbound attachment.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    public static string Root(string dataDir) => Path.Combine(dataDir, "messaging", "media");

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();

    /// <summary>A filesystem-safe name (no separators, no dot-dot), never empty.</summary>
    public static string SafeName(string? name, string fallback = "file.bin")
    {
        var raw = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last());
        var safe = Unsafe().Replace(raw, "_").Trim('.', '_');
        if (safe.Length == 0) safe = fallback;
        return safe.Length > 120 ? safe[^120..] : safe;
    }

    /// <summary>Write the bytes and return the relative media path to store on the message.</summary>
    public static string Save(string dataDir, string channel, string key, string fileName, byte[] content)
    {
        var dir = Path.Combine(Root(dataDir), SafeName(channel, "channel"), SafeName(key, "thread"));
        Directory.CreateDirectory(dir);
        var name = $"{Guid.NewGuid():N}"[..10] + "-" + SafeName(fileName);
        File.WriteAllBytes(Path.Combine(dir, name), content);
        return $"{SafeName(channel, "channel")}/{SafeName(key, "thread")}/{name}";
    }

    /// <summary>
    /// The absolute path of a stored media file, or null when the relative path
    /// escapes the media root (a tampered row must never serve /etc/passwd).
    /// </summary>
    public static string? Resolve(string dataDir, string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        var root = Path.GetFullPath(Root(dataDir));
        var full = Path.GetFullPath(Path.Combine(root, relPath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        MediaOffload.Current?.Hydrate(full);   // an offloaded file comes back before the caller opens it
        return full;
    }

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic" };
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".ogg", ".oga", ".mp3", ".m4a", ".wav", ".aac", ".opus" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".webm", ".3gp", ".mkv" };

    /// <summary>messaging/views._media_type_for_filename: image | audio | video | document.</summary>
    public static string TypeFor(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        if (ImageExts.Contains(ext)) return "image";
        if (AudioExts.Contains(ext)) return "audio";
        if (VideoExts.Contains(ext)) return "video";
        return "document";
    }

    /// <summary>A Content-Type good enough for serving and for MIME parts.</summary>
    public static string ContentTypeFor(string? fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".heic" => "image/heic",
            ".ogg" or ".oga" or ".opus" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".aac" => "audio/aac",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".3gp" => "video/3gpp",
            ".mkv" => "video/x-matroska",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
}

/// <summary>
/// A file riding on an outbound message. <see cref="RelPath"/> is relative to
/// the messaging media root and is what the stored message carries.
/// </summary>
public sealed record OutboundMedia(string AbsolutePath, string RelPath, string MediaType, string FileName);
