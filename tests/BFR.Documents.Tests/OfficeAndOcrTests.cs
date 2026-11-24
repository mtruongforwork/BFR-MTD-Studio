namespace BFR.Documents.Tests;

using System.Diagnostics;
using BFR.Documents.Abstractions;
using BFR.Documents.Ocr;
using BFR.Documents.Office;
using Xunit;

public sealed class OfficeAndOcrTests : IDisposable
{
    private readonly string _dir;

    public OfficeAndOcrTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_office_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void OpenXmlCleaner_StripsDocxMetadataCommentsAndRevisions()
    {
        var docx = TestDocumentFixtures.CreateSampleDocx(Path.Combine(_dir, "dirty.docx"), "Body text", withMetadataAndComments: true);
        var cleaner = new OpenXmlCleaner();

        var before = cleaner.InspectMetadata(docx);
        Assert.True(before.Success);
        Assert.Equal("Secret Author", before.Data!.Author);
        Assert.Equal("Secret Corp", before.Data.Company);
        Assert.Equal(1, before.Data.CommentsCount);
        Assert.Equal(2, before.Data.RevisionsCount);

        var cleaned = Path.Combine(_dir, "clean.docx");
        var res = cleaner.Clean(docx, cleaned);
        Assert.True(res.Success, res.Message);
        Assert.True(res.Data!.TotalItemsRemoved >= 5);
        Assert.Null(res.Data.After.Author);
        Assert.Null(res.Data.After.Company);
        Assert.Equal(0, res.Data.After.CommentsCount);
        Assert.Equal(0, res.Data.After.RevisionsCount);
    }

    [Fact]
    public void OpenXmlCleaner_StripsXlsxMetadata()
    {
        var xlsx = TestDocumentFixtures.CreateSampleXlsx(Path.Combine(_dir, "dirty.xlsx"), withMetadata: true);
        var cleaner = new OpenXmlCleaner();
        var cleaned = Path.Combine(_dir, "clean.xlsx");

        var res = cleaner.Clean(xlsx, cleaned);
        Assert.True(res.Success, res.Message);
        Assert.Null(res.Data!.After.Author);
        Assert.Null(res.Data.After.Title);
    }

    [Fact]
    public void OpenXmlExtractor_ExtractsText_And_MergesDocxAndCsv()
    {
        var d1 = TestDocumentFixtures.CreateSampleDocx(Path.Combine(_dir, "d1.docx"), "First Doc");
        var d2 = TestDocumentFixtures.CreateSampleDocx(Path.Combine(_dir, "d2.docx"), "Second Doc");
        var extractor = new OpenXmlExtractor();

        var mergedDocx = Path.Combine(_dir, "merged.docx");
        Assert.True(extractor.MergeDocx(new[] { d1, d2 }, mergedDocx).Success);
        var txt = extractor.ExtractText(mergedDocx);
        Assert.True(txt.Success);
        Assert.Contains("First Doc", txt.Data);
        Assert.Contains("Second Doc", txt.Data);

        var c1 = Path.Combine(_dir, "c1.csv");
        var c2 = Path.Combine(_dir, "c2.csv");
        File.WriteAllText(c1, "Name,Score\nAlice,10\n");
        File.WriteAllText(c2, "Name,Score\nBob,20\n");
        var mergedCsv = Path.Combine(_dir, "merged.csv");
        Assert.True(extractor.MergeCsv(new[] { c1, c2 }, mergedCsv).Success);
        var lines = File.ReadAllLines(mergedCsv);
        Assert.Equal(3, lines.Length); // header deduplicated
    }

    [Fact]
    public void SpreadsheetConverter_XlsxToCsv_EscapesFormulaInjection_And_ExportsJson()
    {
        var xlsx = TestDocumentFixtures.CreateSampleXlsx(Path.Combine(_dir, "data.xlsx"));
        var conv = new SpreadsheetConverter();

        var csvOut = Path.Combine(_dir, "data.csv");
        var csvRes = conv.XlsxToCsv(xlsx, csvOut);
        Assert.True(csvRes.Success, csvRes.Message);
        var csvText = File.ReadAllText(csvOut);
        Assert.Contains("'=CMD", csvText); // SEC-4 formula injection neutralized

        var jsonOut = Path.Combine(_dir, "data.json");
        var jsonRes = conv.XlsxToJson(xlsx, jsonOut);
        Assert.True(jsonRes.Success, jsonRes.Message);
        var jsonText = File.ReadAllText(jsonOut);
        Assert.Contains("Alice", jsonText);
        Assert.Contains("Bob", jsonText);

        var roundTripXlsx = Path.Combine(_dir, "roundtrip.xlsx");
        var xlsxRes = conv.CsvToXlsx(csvOut, roundTripXlsx);
        Assert.True(xlsxRes.Success, xlsxRes.Message);
        Assert.True(File.Exists(roundTripXlsx));
    }

    [Fact]
    public void TextDocumentPdfRenderer_RendersVietnameseMarkdownToPdf()
    {
        var md = Path.Combine(_dir, "notes.md");
        File.WriteAllText(md, "# Báo cáo Tổng hợp\n\nNội dung tiếng Việt có dấu: ă, â, đ, ê, ô, ơ, ư.\n\n- Mục 1\n- Mục 2\n");

        var pdfOut = Path.Combine(_dir, "notes.pdf");
        var res = new TextDocumentPdfRenderer().RenderToPdf(md, pdfOut);
        Assert.True(res.Success, res.Message);
        Assert.True(File.Exists(pdfOut));
        Assert.True(new DocumentInspector().Inspect(pdfOut).PageCount >= 1);
    }

    [Fact]
    public void HybridOfficePdfBridge_FallsBackCleanlyWhenNoExternalOfficePreferred()
    {
        var docx = TestDocumentFixtures.CreateSampleDocx(Path.Combine(_dir, "hybrid.docx"), "Hybrid Offline Text");
        var outPdf = Path.Combine(_dir, "hybrid.pdf");

        var res = new HybridOfficePdfBridge().ConvertToPdf(docx, outPdf, preferHighFidelity: false);
        Assert.True(res.Success, res.Message);
        Assert.True(res.UsedFallbackEngine);
        Assert.True(File.Exists(outPdf));
    }

    [Fact]
    public void HybridOfficePdfBridge_JobObjectKillsTimedOutChildProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 10 127.0.0.1 >nul",
            CreateNoWindow = true,
            UseShellExecute = false
        };

        Assert.Throws<TimeoutException>(() =>
            HybridOfficePdfBridge.RunInKillOnCloseJob(psi, TimeSpan.FromMilliseconds(250), CancellationToken.None));
    }

    [Fact]
    public async Task WindowsOcrService_ListsLanguages_And_RecognizesImageAndPdf()
    {
        var langs = WindowsOcrService.GetAvailableLanguages();
        if (langs.Count == 0)
        {
            // Environment without Windows OCR language packs installed
            return;
        }

        var tag = langs[0].LanguageTag;
        Assert.True(WindowsOcrService.IsLanguageSupported(tag));

        var img = TestDocumentFixtures.CreatePngImage(Path.Combine(_dir, "ocr.png"), 600, 140, "INVOICE 2026");
        var svc = new WindowsOcrService();
        var imgRes = await svc.RecognizeImageAsync(img, tag);
        Assert.True(imgRes.Success, imgRes.Message);
        Assert.NotNull(imgRes.Data);

        var pdf = TestDocumentFixtures.CreatePdfWithImage(Path.Combine(_dir, "scan.pdf"), 600, 200);
        var searchableOut = Path.Combine(_dir, "searchable.pdf");
        var pdfRes = await svc.RecognizePdfAsync(pdf, tag, searchableOut, OcrOutputFormat.SearchablePdf);
        Assert.True(pdfRes.Success, pdfRes.Message);
        Assert.True(File.Exists(searchableOut));
    }
}
