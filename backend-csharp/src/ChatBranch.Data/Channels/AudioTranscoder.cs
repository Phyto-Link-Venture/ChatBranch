using System.Collections.Concurrent;
using System.Diagnostics;

namespace ChatBranch.Data.Channels;

/// <summary>
/// Voice notes browsers cannot play: WhatsApp / Telegram send Ogg/Opus (no Safari
/// before 17 / older iOS) and some phones AMR (no browser at all). When the server
/// has ffmpeg, the signed media link answers <c>?format=m4a</c> with an AAC/M4A
/// rendering, made once and cached next to the original (<c>{file}.m4a</c>).
///
/// ffmpeg is found through env <c>MEDIA_FFMPEG</c> (a path or a command name;
/// <c>off</c> / <c>0</c> / <c>false</c> disables it), else <c>ffmpeg</c> on PATH.
/// Without it the player offers the download instead.
/// </summary>
public static class AudioTranscoder
{
    private static readonly HashSet<string> Transcodable = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/ogg", "audio/opus", "audio/amr", "audio/amr-wb", "audio/webm", "audio/3gpp",
    };

    private static readonly ConcurrentDictionary<string, string?> Found = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    /// <summary>True for an audio type that has an M4A rendering on request.</summary>
    public static bool CanTranscode(string? mime) =>
        Transcodable.Contains((mime ?? "").Split(';')[0].Trim());

    public static bool Available => FfmpegPath() != null;

    /// <summary>The ffmpeg executable to run, or null (disabled / not installed).</summary>
    public static string? FfmpegPath()
    {
        var env = (Environment.GetEnvironmentVariable("MEDIA_FFMPEG") ?? "").Trim();
        if (env.ToLowerInvariant() is "off" or "0" or "false" or "no" or "none") return null;
        return Found.GetOrAdd(env, Locate);
    }

    private static string? Locate(string configured)
    {
        var name = configured.Length > 0 ? configured : "ffmpeg";
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains('/'))
            return File.Exists(name) ? name : null;
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            foreach (var ext in exts)
            {
                var full = Path.Combine(dir, name + ext);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    /// <summary>
    /// The M4A rendering of <paramref name="source"/> (made now, or the cached one),
    /// or null when ffmpeg is missing or failed. One conversion per file at a time;
    /// written to a temp name and moved, so a reader never sees half a file.
    /// </summary>
    public static async Task<string?> ToM4aAsync(string source, CancellationToken ct = default)
    {
        var ffmpeg = FfmpegPath();
        if (ffmpeg == null || !File.Exists(source)) return null;
        var target = source + ".m4a";
        if (Fresh(source, target)) return target;
        var gate = Locks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Fresh(source, target)) return target;
            var tmp = $"{target}.{Guid.NewGuid():N}.tmp.m4a";
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source,
                         "-vn", "-c:a", "aac", "-b:a", "64k", "-movflags", "+faststart", "-f", "mp4", tmp })
                psi.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return null;
                var drainErr = p.StandardError.ReadToEndAsync(ct);
                var drainOut = p.StandardOutput.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    throw;
                }
                await Task.WhenAll(drainErr, drainOut);
                if (p.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0) return null;
                File.Move(tmp, target, overwrite: true);
                return target;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException
                                          or OperationCanceledException or InvalidOperationException)
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool Fresh(string source, string target) =>
        File.Exists(target) && new FileInfo(target).Length > 0
        && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source);
}
