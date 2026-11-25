namespace BFR.Documents.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using BFR.Documents.Markdown;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

public sealed class UniversalMarkdownConverterTests : IDisposable
{
    private readonly string _dir;

    public UniversalMarkdownConverterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr-markitdown-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ConvertHtmlToMarkdown_PreservesHeadingsLinksBoldAndTables()
    {
        var html = """
            <html>
              <head><style>body { color: red; }</style><script>alert(1);</script></head>
              <body>
                <h1>Quarterly Report</h1>
                <p>See <a href="https://example.com">portal</a> for <strong>details</strong>.</p>
                <table>
                  <tr><th>Item</th><th>Amount</th></tr>
                  <tr><td>Cloud</td><td>$120</td></tr>
                </table>
              </body>
            </html>
            """;

        var md = UniversalMarkdownConverter.ConvertHtmlToMarkdown(html);

        Assert.DoesNotContain("alert(1)", md);
        Assert.DoesNotContain("color: red", md);
        Assert.Contains("# Quarterly Report", md);
        Assert.Contains("[portal](https://example.com)", md);
        Assert.Contains("**details**", md);
        Assert.Contains("| Item | Amount |", md);
        Assert.Contains("| Cloud | $120 |", md);
    }

    [Fact]
    public async Task ConvertAsync_Docx_PreservesHeadingAndTableInOrder()
    {
        var docxPath = Path.Combine(_dir, "spec.docx");
        using (var doc = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());
            var body = main.Document.Body!;

            var h1 = new Paragraph(
                new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                new Run(new Text("Overview")));
            body.AppendChild(h1);

            var p = new Paragraph(new Run(new RunProperties(new Bold()), new Text("Important")));
            body.AppendChild(p);

            var tbl = new Table(
                new TableRow(new TableCell(new Paragraph(new Run(new Text("Key")))), new TableCell(new Paragraph(new Run(new Text("Value"))))),
                new TableRow(new TableCell(new Paragraph(new Run(new Text("Env")))), new TableCell(new Paragraph(new Run(new Text("Prod"))))));
            body.AppendChild(tbl);
            main.Document.Save();
        }

        var conv = new UniversalMarkdownConverter();
        var res = await conv.ConvertAsync(docxPath);

        Assert.True(res.Success);
        Assert.Equal("DOCX", res.Data!.DetectedFormat);
        Assert.True(res.Data.EstimatedTokens > 0);
        Assert.Contains("# Overview", res.Data.Markdown);
        Assert.Contains("**Important**", res.Data.Markdown);
        Assert.Contains("| Key | Value |", res.Data.Markdown);
        Assert.Contains("| Env | Prod |", res.Data.Markdown);
    }

    [Fact]
    public async Task ConvertAsync_Xlsx_ConvertsSheetsToGfmTables()
    {
        var xlsxPath = Path.Combine(_dir, "budget.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Q4");
            ws.Cell(1, 1).Value = "Dept";
            ws.Cell(1, 2).Value = "Budget";
            ws.Cell(2, 1).Value = "R&D";
            ws.Cell(2, 2).Value = 50000;
            wb.SaveAs(xlsxPath);
        }

        var conv = new UniversalMarkdownConverter();
        var res = await conv.ConvertAsync(xlsxPath);

        Assert.True(res.Success);
        Assert.Equal("XLSX", res.Data!.DetectedFormat);
        Assert.Contains("## Q4", res.Data.Markdown);
        Assert.Contains("| Dept | Budget |", res.Data.Markdown);
        Assert.Contains("| R&D | 50000 |", res.Data.Markdown);
    }

    [Fact]
    public async Task ConvertBatchBundleAsync_MergesMultipleFilesWithToc()
    {
        var csv = Path.Combine(_dir, "data.csv");
        await File.WriteAllTextAsync(csv, "Name,Score\nAlice,98\nBob,92");
        var txt = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(txt, "Meeting summary line 1.");

        var bundleOut = Path.Combine(_dir, "bundle.md");
        var conv = new UniversalMarkdownConverter();
        var res = await conv.ConvertBatchBundleAsync(new[] { csv, txt }, bundleOut);

        Assert.True(res.Success);
        Assert.True(File.Exists(bundleOut));
        Assert.Equal("BUNDLE", res.Data!.DetectedFormat);
        Assert.Equal(2, res.Data.SectionCount);
        Assert.Contains("## Table of Contents", res.Data.Markdown);
        Assert.Contains("data.csv", res.Data.Markdown);
        Assert.Contains("notes.txt", res.Data.Markdown);
        Assert.Contains("| Alice | 98 |", res.Data.Markdown);
        Assert.Contains("Meeting summary line 1.", res.Data.Markdown);
    }
}
