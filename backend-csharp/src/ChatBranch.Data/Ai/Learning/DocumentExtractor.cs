using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExcelDataReader;
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
/// becomes a knowledge item. Background AI tasks also read office files —
/// .docx, .xlsx / .xls (every sheet, row by row) and .pptx (<see cref="ReadableByTasks"/>).
/// </summary>
public static class DocumentExtractor
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const int MaxChars = 500_000;
    public static readonly string[] Extensions = { ".pdf", ".txt", ".md", ".csv" };

    /// <summary>What an AI task can read as text: the knowledge types plus office files.</summary>
    public static readonly string[] TaskExtensions = { ".pdf", ".txt", ".md", ".csv", ".docx", ".xlsx", ".xls", ".pptx" };

    public static bool Supported(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName ?? "").ToLowerInvariant());

    public static bool ReadableByTasks(string fileName) =>
        TaskExtensions.Contains(Path.GetExtension(fileName ?? "").ToLowerInvariant());

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
                ".docx" => new ExtractedDocument(Docx(bytes), null),
                ".xlsx" or ".xls" => Sheets(bytes, ext == ".xls"),
                ".pptx" => Pptx(bytes),
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
            return new ExtractedDocument("", "The file could not be read. Check that it is a valid " + ext switch
            {
                ".pdf" => "PDF.",
                ".docx" => "Word document.",
                ".xlsx" or ".xls" => "spreadsheet.",
                ".pptx" => "PowerPoint file.",
                _ => "text file.",
            });
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

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>A Word document's body: paragraphs as lines, table rows as "cell | cell".</summary>
    private static string Docx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("no document.xml");
        using var stream = entry.Open();
        var body = XDocument.Load(stream).Root?.Element(W + "body");
        var sb = new StringBuilder();
        if (body == null) return "";
        foreach (var block in body.Elements())
        {
            if (block.Name == W + "p") sb.AppendLine(Paragraph(block));
            else if (block.Name == W + "tbl")
                foreach (var row in block.Descendants(W + "tr"))
                    sb.AppendLine(string.Join(" | ", row.Elements(W + "tc")
                        .Select(c => string.Join(" ", c.Elements(W + "p").Select(Paragraph)).Trim())));
            if (sb.Length > MaxChars + 10_000) break;
        }
        return sb.ToString();
    }

    private static string Paragraph(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var el in p.Descendants())
        {
            if (el.Name == W + "t") sb.Append(el.Value);
            else if (el.Name == W + "tab") sb.Append('\t');
            else if (el.Name == W + "br" || el.Name == W + "cr") sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Every worksheet: its name, then each non-empty row as "cell | cell".</summary>
    private static ExtractedDocument Sheets(byte[] bytes, bool legacy)
    {
        // .xls stores text in old Windows code pages
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        using var stream = new MemoryStream(bytes);
        using var reader = legacy ? ExcelReaderFactory.CreateBinaryReader(stream) : ExcelReaderFactory.CreateOpenXmlReader(stream);
        var sb = new StringBuilder();
        var sheets = 0;
        do
        {
            sheets++;
            sb.Append("Sheet: ").AppendLine(reader.Name);
            while (reader.Read() && sb.Length <= MaxChars + 10_000)
            {
                var cells = new List<string>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++) cells.Add(Cell(reader.GetValue(i)));
                while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
                if (cells.Count > 0) sb.AppendLine(string.Join(" | ", cells));
            }
            sb.AppendLine();
        } while (reader.NextResult() && sb.Length <= MaxChars + 10_000);
        return new ExtractedDocument(sb.ToString(), null, sheets);
    }

    private static string Cell(object? v) => v switch
    {
        null => "",
        string s => s.Trim(),
        double d when Math.Abs(d) < 1e15 && d == Math.Floor(d) => d.ToString("0", CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm"),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };

    /// <summary>A PowerPoint file's slides in order, each paragraph a line.</summary>
    private static ExtractedDocument Pptx(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml"))
            .OrderBy(e => int.TryParse(e.FullName[16..^4], out var n) ? n : int.MaxValue)
            .ToList();
        var sb = new StringBuilder();
        var number = 0;
        foreach (var slide in slides)
        {
            number++;
            using var stream = slide.Open();
            sb.Append("Slide ").Append(number).AppendLine(":");
            foreach (var p in XDocument.Load(stream).Descendants(A + "p"))
            {
                var line = string.Concat(p.Descendants(A + "t").Select(t => t.Value)).Trim();
                if (line.Length > 0) sb.AppendLine(line);
            }
            sb.AppendLine();
            if (sb.Length > MaxChars + 10_000) break;
        }
        return new ExtractedDocument(sb.ToString(), null, number);
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
