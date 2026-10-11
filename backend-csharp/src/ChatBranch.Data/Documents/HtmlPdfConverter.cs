using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Documents;

/// <summary>The page a custom layout prints on when its CSS has no <c>@page</c> of its own.</summary>
public sealed record HtmlPdfPage(string Size = "A4", double Top = 16, double Right = 18, double Bottom = 20, double Left = 18)
{
    public static HtmlPdfPage Of(DocumentHtmlConfig? h) => h == null
        ? new HtmlPdfPage()
        : new HtmlPdfPage(h.PageSize, h.MarginTop, h.MarginRight, h.MarginBottom, h.MarginLeft);
}

/// <summary>The custom layout can't print here: no HTML renderer (headless Chromium) on this server.</summary>
public sealed class HtmlRendererUnavailableException() : DocumentHtmlException(Text)
{
    public const string Text =
        "The custom layout is unavailable on this server (no HTML renderer — headless Chromium — is installed). "
        + "Switch the skill back to the designer layout, or install Chromium (set DOCUMENT_HTML_CHROMIUM).";
}

/// <summary>The process-wide converter for code built without DI (and its status word).</summary>
public static class HtmlPdf
{
    private static readonly Lazy<IHtmlPdfConverter> Lazy = new(() => ChromiumHtmlPdfConverter.FromEnvironment());

    /// <summary>Headless Chromium as the environment names it (DOCUMENT_HTML_CHROMIUM → the PATH).</summary>
    public static IHtmlPdfConverter Default => Lazy.Value;

    /// <summary>available | missing — the /skills/apps documents entry's <c>html_renderer</c>.</summary>
    public static string Status(IHtmlPdfConverter c) => c.Available ? "available" : "missing";
}

/// <summary>HTML → PDF. One implementation drives headless Chromium; tests use a fake.</summary>
public interface IHtmlPdfConverter
{
    /// <summary>False when no renderer is installed (HTML-mode documents are then refused, never faked).</summary>
    bool Available { get; }

    /// <summary>Print a page. The converter hardens it first (<see cref="HtmlPdfHardening.Harden"/>).</summary>
    Task<byte[]> ConvertAsync(string html, HtmlPdfPage page, CancellationToken ct = default);
}

/// <summary>
/// What every page gets before it is printed, whatever the template says: UTF-8, a
/// Content-Security-Policy that blocks EVERY script and EVERY fetch (only inline
/// styles and data: images / fonts load), the default <c>@page</c> (the template's
/// own later <c>@page</c> wins) and background colours kept in print.
/// </summary>
public static class HtmlPdfHardening
{
    public const string Csp =
        "default-src 'none'; script-src 'none'; object-src 'none'; frame-src 'none'; connect-src 'none'; "
        + "img-src data:; font-src data:; style-src 'unsafe-inline'; media-src 'none'; base-uri 'none'; form-action 'none'";

    private static readonly Regex Doctype = new(@"^\s*(<!--.*?-->\s*)*<!doctype[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static string Harden(string html, HtmlPdfPage page)
    {
        string Mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + "mm";
        var head = "<meta charset=\"utf-8\">"
                   + $"<meta http-equiv=\"Content-Security-Policy\" content=\"{Csp}\">"
                   + $"<style>@page{{size:{(page.Size == "Letter" ? "Letter" : "A4")};margin:{Mm(page.Top)} {Mm(page.Right)} {Mm(page.Bottom)} {Mm(page.Left)}}}"
                   + "html{-webkit-print-color-adjust:exact;print-color-adjust:exact}</style>";
        var m = Doctype.Match(html);
        return m.Success
            ? html[..m.Length] + head + html[m.Length..]
            : "<!doctype html>" + head + html;
    }
}

/// <summary>
/// HTML → PDF with headless Chromium's command line (no NuGet, no DevTools socket):
/// a throw-away directory with the page, its own profile and the output;
/// <c>--print-to-pdf</c> with every host name unresolvable
/// (<c>--host-resolver-rules="MAP * ~NOTFOUND"</c>, no proxy — the child gets no proxy
/// variables) and the page hardened by <see cref="HtmlPdfHardening"/> (JavaScript and
/// every fetch blocked by its CSP). Verified against Chromium 141:
/// <c>--blink-settings=scriptEnabled=false</c> makes <c>--print-to-pdf</c> silently write
/// nothing, so scripts are switched off by the CSP (and the sanitizer removes them
/// first). At most <see cref="MaxConcurrent"/> renders at once, <see cref="Timeout"/> each
/// (the process tree is killed past it), and the directory is always removed.
/// Binary: env <c>DOCUMENT_HTML_CHROMIUM</c> (a path, or <c>off</c>) → <c>chromium</c>,
/// <c>chromium-browser</c>, <c>google-chrome</c>, <c>google-chrome-stable</c> on the PATH.
/// </summary>
public sealed class ChromiumHtmlPdfConverter : IHtmlPdfConverter
{
    public const int MaxConcurrent = 2;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);
    public static readonly string[] Names = { "chromium", "chromium-browser", "google-chrome", "google-chrome-stable" };

    public string? Binary { get; }
    public bool Available => Binary != null;

    public ChromiumHtmlPdfConverter(string? binary) => Binary = binary;

    public static ChromiumHtmlPdfConverter FromEnvironment() =>
        new(FindBinary(Environment.GetEnvironmentVariable("DOCUMENT_HTML_CHROMIUM")));

    /// <summary>The browser to print with: the setting (a path, or "off"), else the first known name on the PATH.</summary>
    public static string? FindBinary(string? setting)
    {
        var s = (setting ?? "").Trim();
        if (s.Equals("off", StringComparison.OrdinalIgnoreCase)) return null;
        if (s.Length > 0)
        {
            if (File.Exists(s)) return s;
            if (!s.Contains(Path.DirectorySeparatorChar)) return OnPath(s);
            return null;
        }
        return Names.Select(OnPath).FirstOrDefault(p => p != null);
    }

    private static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    public async Task<byte[]> ConvertAsync(string html, HtmlPdfPage page, CancellationToken ct = default)
    {
        if (Binary == null) throw new HtmlRendererUnavailableException();
        await Gate.WaitAsync(ct);
        var dir = Path.Combine(Path.GetTempPath(), "cb-htmlpdf-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var input = Path.Combine(dir, "page.html");
            var output = Path.Combine(dir, "out.pdf");
            await File.WriteAllTextAsync(input, HtmlPdfHardening.Harden(html, page), new UTF8Encoding(false), ct);

            var psi = new ProcessStartInfo(Binary)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, WorkingDirectory = dir,
            };
            foreach (var a in new[]
                     {
                         "--headless", "--disable-gpu", "--no-sandbox", "--no-first-run", "--no-default-browser-check",
                         "--disable-extensions", "--disable-background-networking", "--disable-component-update",
                         "--disable-sync", "--disable-default-apps", "--disable-crash-reporter", "--mute-audio",
                         "--no-proxy-server", "--host-resolver-rules=MAP * ~NOTFOUND",
                         $"--user-data-dir={Path.Combine(dir, "profile")}", "--no-pdf-header-footer",
                         $"--print-to-pdf={output}", new Uri(input).AbsoluteUri,
                     })
                psi.ArgumentList.Add(a);
            foreach (var v in new[] { "http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY", "all_proxy", "ALL_PROXY", "no_proxy", "NO_PROXY" })
                psi.Environment.Remove(v);
            // a non-root user's HOME may be read-only (the container's `app`): keep Chromium's files in the temp dir
            psi.Environment["HOME"] = dir;
            psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(dir, "config");
            psi.Environment["XDG_CACHE_HOME"] = Path.Combine(dir, "cache");

            using var p = new Process { StartInfo = psi };
            var err = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data != null && err.Length < 4000) err.AppendLine(e.Data); };
            p.OutputDataReceived += (_, _) => { };
            if (!p.Start()) throw new DocumentHtmlException("The HTML renderer could not be started.");
            p.StandardInput.Close();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                ct.ThrowIfCancellationRequested();
                throw new DocumentHtmlException(
                    $"The custom layout took longer than {Timeout.TotalSeconds:0} seconds to print — simplify the template.");
            }
            var bytes = File.Exists(output) ? await File.ReadAllBytesAsync(output, CancellationToken.None) : Array.Empty<byte>();
            if (!DocumentService.IsPdf(bytes))
                throw new DocumentHtmlException("The HTML renderer printed nothing (exit code " + p.ExitCode + ").");
            return bytes;
        }
        finally
        {
            Gate.Release();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    break;
                }
                catch { await Task.Delay(200, CancellationToken.None); } // Chromium's helpers may still hold a file
            }
        }
    }
}
