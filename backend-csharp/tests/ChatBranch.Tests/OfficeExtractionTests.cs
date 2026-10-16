using System.IO.Compression;
using System.Text;
using ChatBranch.Data.Ai.Learning;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>What a background AI task reads out of Word, Excel and PowerPoint files.</summary>
public class OfficeExtractionTests
{
    private static byte[] Zip(params (string Name, string Xml)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, xml) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }
        return ms.ToArray();
    }

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    [Fact]
    public void A_word_document_gives_its_paragraphs_and_table_rows()
    {
        var docx = Zip(("word/document.xml",
            $"<w:document xmlns:w=\"{W}\"><w:body>" +
            "<w:p><w:r><w:t>Proof of Delivery</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t xml:space=\"preserve\">MAWB </w:t></w:r><w:r><w:t>843-46241381</w:t></w:r></w:p>" +
            "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Packages</w:t></w:r></w:p></w:tc>" +
            "<w:tc><w:p><w:r><w:t>100</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
            "</w:body></w:document>"));
        var doc = DocumentExtractor.Extract("pod.docx", docx);
        Assert.True(doc.Ok, doc.Error);
        Assert.Contains("Proof of Delivery", doc.Text);
        Assert.Contains("MAWB 843-46241381", doc.Text);
        Assert.Contains("Packages | 100", doc.Text);
    }

    [Fact]
    public void A_spreadsheet_gives_every_row_and_keeps_long_numbers_whole()
    {
        var xlsx = ContactImportTests.Xlsx(new[] { "MAWB", "Packages", "Consignee" },
            new object[] { "843-46241381", 100, "Carsem" }, new object[] { "843-11112222", 4L, "Trivo" });
        var doc = DocumentExtractor.Extract("manifest.xlsx", xlsx);
        Assert.True(doc.Ok, doc.Error);
        Assert.Contains("MAWB | Packages | Consignee", doc.Text);
        Assert.Contains("843-46241381 | 100 | Carsem", doc.Text);
        Assert.Contains("843-11112222 | 4 | Trivo", doc.Text);
        Assert.True(DocumentExtractor.ReadableByTasks("Manifest.XLS"));
    }

    [Fact]
    public void A_presentation_gives_its_slides_in_order()
    {
        string Slide(string text) => $"<p:sld xmlns:p=\"p\" xmlns:a=\"{A}\"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>";
        var pptx = Zip(("ppt/slides/slide10.xml", Slide("Tenth")), ("ppt/slides/slide2.xml", Slide("Second")),
            ("ppt/slides/slide1.xml", Slide("First")));
        var doc = DocumentExtractor.Extract("deck.pptx", pptx);
        Assert.True(doc.Ok, doc.Error);
        Assert.True(doc.Text.IndexOf("First") < doc.Text.IndexOf("Second")
                    && doc.Text.IndexOf("Second") < doc.Text.IndexOf("Tenth"));
        Assert.Equal(3, doc.Pages);
    }

    [Fact]
    public void A_broken_office_file_says_so_and_knowledge_uploads_are_unchanged()
    {
        var doc = DocumentExtractor.Extract("bad.docx", Encoding.ASCII.GetBytes("not a zip"));
        Assert.False(doc.Ok);
        Assert.Contains("Word document", doc.Error);
        Assert.False(DocumentExtractor.Supported("pod.docx")); // the knowledge upload keeps its own list
    }
}
