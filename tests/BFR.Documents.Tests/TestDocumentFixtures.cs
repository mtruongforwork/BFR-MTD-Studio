namespace BFR.Documents.Tests;

using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BFR.Documents.Pdf;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

internal static class TestDocumentFixtures
{
    public static string CreateSamplePdf(string path, int pageCount, string prefix = "Sample Page")
    {
        PdfFontResolver.EnsureRegistered();
        using var doc = new PdfDocument();
        doc.Info.Title = "Test Document";
        doc.Info.Author = "Test Author";
        doc.Info.Subject = "Test Subject";
        doc.Info.Keywords = "test,pdf";
        doc.Info.Creator = "BFR Test";

        var font = new XFont("Segoe UI", 14, XFontStyle.Regular);
        for (var i = 1; i <= pageCount; i++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"{prefix} {i} - Tiếng Việt có dấu", font, XBrushes.Black, new XPoint(50, 80));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        doc.Save(path);
        return path;
    }

    public static string CreatePdfWithImage(string path, int width = 800, int height = 600, string? overlayText = null)
    {
        PdfFontResolver.EnsureRegistered();
        var imgBytes = CreatePngBytes(width, height, overlayText);

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using var gfx = XGraphics.FromPdfPage(page);
        using var xImg = XImage.FromStream(() => new MemoryStream(imgBytes, writable: false));
        gfx.DrawImage(xImg, 40, 40, 400, 300);

        if (!string.IsNullOrEmpty(overlayText))
        {
            var font = new XFont("Segoe UI", 12, XFontStyle.Regular);
            gfx.DrawString(overlayText, font, XBrushes.Black, new XPoint(40, 380));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        doc.Save(path);
        return path;
    }

    public static string CreatePngImage(string path, int width = 400, int height = 120, string? text = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CreatePngBytes(width, height, text));
        return path;
    }

    public static string CreateJpgImage(string path, int width = 600, int height = 400)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rtb = RenderSampleVisual(width, height, "JPG TEST");
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    public static byte[] CreatePngBytes(int width, int height, string? text = null)
    {
        var rtb = RenderSampleVisual(width, height, text);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static RenderTargetBitmap RenderSampleVisual(int width, int height, string? text)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(Brushes.LightSkyBlue, new Pen(Brushes.Navy, 2), new Rect(10, 10, width - 20, height - 20));
            if (!string.IsNullOrEmpty(text))
            {
                var ft = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    32,
                    Brushes.Black,
                    1.0);
                dc.DrawText(ft, new Point(24, height / 2.0 - 20));
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public static string CreateSampleDocx(string path, string text = "Hello DOCX", bool withMetadataAndComments = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));

        if (withMetadataAndComments)
        {
            doc.PackageProperties.Creator = "Secret Author";
            doc.PackageProperties.LastModifiedBy = "Reviewer";
            doc.PackageProperties.Title = "Confidential Title";
            var extPart = doc.AddExtendedFilePropertiesPart();
            extPart.Properties = new DocumentFormat.OpenXml.ExtendedProperties.Properties
            {
                Company = new DocumentFormat.OpenXml.ExtendedProperties.Company("Secret Corp")
            };

            var commentsPart = main.AddNewPart<WordprocessingCommentsPart>();
            commentsPart.Comments = new Comments(
                new Comment { Id = "1", Author = "Secret Reviewer" });
            commentsPart.Comments.FirstChild!.AppendChild(new Paragraph(new Run(new Text("Hidden comment"))));

            var body = main.Document.Body!;
            body.AppendChild(new Paragraph(new InsertedRun(new Run(new Text("Inserted text")))));
            body.AppendChild(new Paragraph(new DeletedRun(new Run(new Text("Deleted text")))));
        }

        main.Document.Save();
        return path;
    }

    public static string CreateSampleXlsx(string path, bool withMetadata = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Data");
        ws.Cell(1, 1).Value = "Name";
        ws.Cell(1, 2).Value = "Formula";
        ws.Cell(2, 1).Value = "Alice";
        ws.Cell(2, 2).Value = "=CMD|' /C calc'!A0";
        ws.Cell(3, 1).Value = "Bob";
        ws.Cell(3, 2).Value = "42";

        if (withMetadata)
        {
            wb.Properties.Author = "Hidden Excel Author";
            wb.Properties.Company = "Hidden Excel Co";
            wb.Properties.Title = "Secret Sheet";
        }

        wb.SaveAs(path);
        return path;
    }

    public static string CreateZipWithSingleEntry(string path, string entryName, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var sw = new StreamWriter(entry.Open(), Encoding.UTF8);
        sw.Write(content);
        return path;
    }
}
