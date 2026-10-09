using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace ChatBranch.Data.Ai.Learning;

/// <summary>Plain text pulled out of an uploaded document, or why there is none.</summary>
public sealed record ExtractedDocument(string Text, string? Error, int Pages = 0, bool Truncated = false)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Turns an uploaded knowledge document into plain text: PDF (UglyToad.PdfPig,
/// text layer only — no OCR), .txt, .md and .csv (rows become "header: value"
/// lines so retrieval can match them). The binary is never kept: only the text
/// becomes a knowledge item.
/// </summary>
public static class DocumentExtractor
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const int MaxChars = 500_000;
    public static readonly string[] Extensions = { ".pdf", ".txt", ".md", ".csv" };

    public static bool Supported(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName ?? "").ToLowerInvariant());

    public static ExtractedDocument Extract(string fileName, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        try
        {
            var doc = ext switch
            {
                ".pdf" => Pdf(bytes),
                ".csv" => new ExtractedDocument(Csv(Decode(bytes)), null),
                ".txt" or ".md" => new ExtractedDocument(Decode(bytes), null),
                _ => new ExtractedDocument("", $"Unsupported file type '{ext}'. Upload a PDF, .txt, .md or .csv file."),
            };
            if (!doc.Ok) return doc;
            var text = Clean(doc.Text);
            if (text.Length == 0)
                return doc with
                {
                    Text = "",
                    Error = ext == ".pdf"
                        ? "No text found — it may be a scanned image. Upload a PDF with selectable text, or paste the text instead."
                        : "The file is empty.",
                };
            var truncated = text.Length > MaxChars;
            return doc with { Text = truncated ? text[..MaxChars] : text, Truncated = truncated };
        }
        catch (Exception)
        {
            return new ExtractedDocument("", "The file could not be read. Check that it is a valid "
                + (ext == ".pdf" ? "PDF." : "text file."));
        }
    }

    private static ExtractedDocument Pdf(byte[] bytes)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            var pages = 0;
            foreach (var page in pdf.GetPages())
            {
                pages++;
                string text;
                try { text = ContentOrderTextExtractor.GetText(page); }
                catch { text = page.Text; }
                if (!string.IsNullOrWhiteSpace(text)) sb.Append(text.Trim()).Append("\n\n");
                if (sb.Length > MaxChars + 10_000) break;
            }
            return new ExtractedDocument(sb.ToString(), null, pages);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new ExtractedDocument("", "This PDF is password-protected — remove the password and upload it again.");
        }
        catch (Exception)
        {
            return new ExtractedDocument("", "The PDF could not be read — it may be damaged.");
        }
    }

    /// <summary>UTF-8 (BOM-aware), falling back to Latin-1 for bytes that aren't valid UTF-8.</summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// A CSV with a header row becomes one "header: value · header: value" line
    /// per row; anything else is kept as is.
    /// </summary>
    public static string Csv(string text)
    {
        var rows = ParseCsv(text).Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (rows.Count < 2 || rows[0].Count > 40) return text;
        var header = rows[0].Select(h => h.Trim()).ToList();
        var sb = new StringBuilder();
        foreach (var row in rows.Skip(1))
        {
            var cells = new List<string>();
            for (var i = 0; i < row.Count; i++)
            {
                var v = row[i].Trim();
                if (v.Length == 0) continue;
                var h = i < header.Count && header[i].Length > 0 ? header[i] : $"column {i + 1}";
                cells.Add($"{h}: {v}");
            }
            if (cells.Count > 0) sb.AppendLine(string.Join(" · ", cells));
        }
        return sb.ToString();
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    private static string Clean(string text)
    {
        var s = (text ?? "").Replace("\0", "").Replace("\r\n", "\n").Replace('\r', '\n');
        // collapse runs of 3+ newlines and trailing spaces
        var lines = s.Split('\n').Select(l => l.TrimEnd());
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var l in lines)
        {
            if (l.Length == 0) { if (++blank > 1) continue; }
            else blank = 0;
            sb.Append(l).Append('\n');
        }
        return sb.ToString().Trim();
    }
}
