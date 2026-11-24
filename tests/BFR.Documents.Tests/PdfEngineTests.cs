namespace BFR.Documents.Tests;

using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Xunit;

public sealed class PdfEngineTests : IDisposable
{
    private readonly string _dir;

    public PdfEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_pdf_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void ParsePageRanges_SupportsRangesAndEndKeyword()
    {
        var pages = PdfOrganizer.ParsePageRanges("1-3, 5, 8-end", 10);
        Assert.Equal(new[] { 1, 2, 3, 5, 8, 9, 10 }, pages);
    }

    [Fact]
    public void Merge_MixedPdfImageMarkdownTxtDocx_ProducesValidCombinedPdf()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "a.pdf"), 2, "PartA");
        var img = TestDocumentFixtures.CreatePngImage(Path.Combine(_dir, "b.png"), 300, 200, "IMG");
        var md = Path.Combine(_dir, "c.md");
        File.WriteAllText(md, "# Heading\n\n- Item 1\n- Item 2\n");
        var docx = TestDocumentFixtures.CreateSampleDocx(Path.Combine(_dir, "d.docx"), "Docx Content");

        var outPdf = Path.Combine(_dir, "merged.pdf");
        var res = new PdfOrganizer().Merge(new[] { pdf, img, md, docx }, outPdf);

        Assert.True(res.Success, res.Message);
        Assert.True(File.Exists(outPdf));
        Assert.Equal(5, res.PagesProcessed);

        var desc = new DocumentInspector().Inspect(outPdf);
        Assert.Equal(5, desc.PageCount);
    }

    [Fact]
    public void Split_ByRanges_Chunks_And_ScissorCutPoints_WorkAccurately()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "ten.pdf"), 10);
        var org = new PdfOrganizer();

        var rangeDir = Path.Combine(_dir, "split_ranges");
        var r1 = org.SplitByRanges(pdf, new[] { "1-3", "4-7", "8-end" }, new PdfSplitOptions(rangeDir));
        Assert.True(r1.Success);
        Assert.Equal(3, r1.OutputPaths.Count);
        Assert.Equal(3, new DocumentInspector().Inspect(r1.OutputPaths[0]).PageCount);
        Assert.Equal(4, new DocumentInspector().Inspect(r1.OutputPaths[1]).PageCount);
        Assert.Equal(3, new DocumentInspector().Inspect(r1.OutputPaths[2]).PageCount);

        var cutDir = Path.Combine(_dir, "split_cuts");
        var r2 = org.SplitByCutPoints(pdf, new[] { 2, 6 }, new PdfSplitOptions(cutDir));
        Assert.True(r2.Success);
        Assert.Equal(3, r2.OutputPaths.Count);
        Assert.Equal(2, new DocumentInspector().Inspect(r2.OutputPaths[0]).PageCount);
        Assert.Equal(4, new DocumentInspector().Inspect(r2.OutputPaths[1]).PageCount);
        Assert.Equal(4, new DocumentInspector().Inspect(r2.OutputPaths[2]).PageCount);

        var chunkDir = Path.Combine(_dir, "split_chunks");
        var r3 = org.SplitIntoFixedChunks(pdf, 4, new PdfSplitOptions(chunkDir));
        Assert.True(r3.Success);
        Assert.Equal(3, r3.OutputPaths.Count);
        Assert.Equal(2, new DocumentInspector().Inspect(r3.OutputPaths[2]).PageCount);
    }

    [Fact]
    public void Extract_Delete_Rotate_Reorder_PreserveCorrectPages()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "five.pdf"), 5);
        var org = new PdfOrganizer();

        var extracted = Path.Combine(_dir, "extracted.pdf");
        Assert.True(org.ExtractPages(pdf, "2, 4-5", extracted).Success);
        Assert.Equal(3, new DocumentInspector().Inspect(extracted).PageCount);

        var deleted = Path.Combine(_dir, "deleted.pdf");
        Assert.True(org.DeletePages(pdf, new[] { 1, 3 }, deleted).Success);
        Assert.Equal(3, new DocumentInspector().Inspect(deleted).PageCount);

        var rotated = Path.Combine(_dir, "rotated.pdf");
        Assert.True(org.RotatePages(pdf, 90, rotated, new[] { 2 }).Success);
        using (var doc = PdfReader.Open(rotated, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(0, doc.Pages[0].Rotate);
            Assert.Equal(90, doc.Pages[1].Rotate);
        }

        var reordered = Path.Combine(_dir, "reordered.pdf");
        var instructions = new[]
        {
            new PdfPageInstruction(pdf, 5, 180),
            new PdfPageInstruction(pdf, 1, 0)
        };
        Assert.True(org.BuildFromInstructions(instructions, reordered).Success);
        Assert.Equal(2, new DocumentInspector().Inspect(reordered).PageCount);
    }

    [Fact]
    public void Compress_WithMaxPreset_ReducesImageHeavyPdfSize()
    {
        var pdf = Path.Combine(_dir, "heavy.pdf");
        var jpg = TestDocumentFixtures.CreateJpgImage(Path.Combine(_dir, "photo.jpg"), 2000, 1500);
        new PdfImageConverter().ImagesToPdf(new[] { jpg }, pdf, new ImagesToPdfOptions(PdfPageSizeMode.FitImage));

        var compressed = Path.Combine(_dir, "compressed.pdf");
        var res = new PdfCompressor().Compress(pdf, compressed, new PdfCompressionOptions(PdfCompressionPreset.Max));

        Assert.True(res.Success, res.Message);
        Assert.True(res.OutputBytes < res.InputBytes, $"Expected smaller PDF: {res.OutputBytes} < {res.InputBytes}");
        Assert.True(res.SavingsPercent > 0);
    }

    [Fact]
    public async Task ImagesToPdf_And_PdfToImages_RoundTripSuccessfully()
    {
        var img1 = TestDocumentFixtures.CreatePngImage(Path.Combine(_dir, "i1.png"), 300, 200, "Page 1");
        var img2 = TestDocumentFixtures.CreateJpgImage(Path.Combine(_dir, "i2.jpg"), 400, 300);

        var pdfOut = Path.Combine(_dir, "album.pdf");
        var toPdf = new PdfImageConverter().ImagesToPdf(new[] { img1, img2 }, pdfOut, new ImagesToPdfOptions(PdfPageSizeMode.A4));
        Assert.True(toPdf.Success, toPdf.Message);
        Assert.Equal(2, toPdf.PagesProcessed);

        var imgDir = Path.Combine(_dir, "exported_imgs");
        var toImg = await new PdfImageConverter().PdfToImagesAsync(pdfOut, imgDir, new PdfToImagesOptions(PdfExportImageFormat.Png, Dpi: 100));
        Assert.True(toImg.Success, toImg.Message);
        Assert.Equal(2, toImg.OutputPaths.Count);
        Assert.All(toImg.OutputPaths, p => Assert.True(File.Exists(p) && new FileInfo(p).Length > 0));
    }

    [Fact]
    public void Protect_Unlock_And_StripMetadata_WorkEndToEnd()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "plain.pdf"), 2);
        var sec = new PdfSecurityService();

        var protectedPdf = Path.Combine(_dir, "protected.pdf");
        var pRes = sec.Protect(pdf, protectedPdf, new PdfProtectOptions(UserPassword: "user123", OwnerPassword: "owner456", PermitPrint: false));
        Assert.True(pRes.Success, pRes.Message);

        var lockedDesc = new DocumentInspector().Inspect(protectedPdf);
        Assert.Equal(DocumentErrorCode.PasswordRequired, lockedDesc.ErrorCode);
        Assert.True(lockedDesc.RequiresUserPassword);

        var unlockedPdf = Path.Combine(_dir, "unlocked.pdf");
        var uRes = sec.Unlock(protectedPdf, unlockedPdf, "user123");
        Assert.True(uRes.Success, uRes.Message);
        Assert.Equal(DocumentErrorCode.None, new DocumentInspector().Inspect(unlockedPdf).ErrorCode);

        var strippedPdf = Path.Combine(_dir, "stripped.pdf");
        var sRes = sec.StripMetadata(pdf, strippedPdf, stripUnsafeActions: true);
        Assert.True(sRes.Success, sRes.Message);
        Assert.True(sRes.Data!.FieldsCleared >= 4);

        using var check = PdfReader.Open(strippedPdf, PdfDocumentOpenMode.InformationOnly);
        Assert.True(string.IsNullOrEmpty(check.Info.Author));
        Assert.True(string.IsNullOrEmpty(check.Info.Title));
    }

    [Fact]
    public void StripUnsafeActions_RemovesLaunchAndJavaScript()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "unsafe.pdf"), 1);
        using (var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            var jsAction = new PdfDictionary(doc);
            jsAction.Elements.SetName("/S", "/JavaScript");
            jsAction.Elements.SetString("/JS", "app.alert('xss');");
            doc.Internals.Catalog.Elements["/OpenAction"] = jsAction;
            doc.Save(pdf);
        }

        var cleanPdf = Path.Combine(_dir, "safe.pdf");
        var res = new PdfSecurityService().StripMetadata(pdf, cleanPdf, stripUnsafeActions: true);
        Assert.True(res.Success);
        Assert.True(res.Data!.UnsafeActionsRemoved >= 1);

        using var verify = PdfReader.Open(cleanPdf, PdfDocumentOpenMode.Modify);
        Assert.False(verify.Internals.Catalog.Elements.ContainsKey("/OpenAction"));
    }

    [Fact]
    public void Watermark_And_PageNumbers_ApplyToAllPages()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "doc.pdf"), 3);
        var stamp = new PdfStampService();

        var wmPdf = Path.Combine(_dir, "wm.pdf");
        var wRes = stamp.AddWatermark(pdf, wmPdf, new PdfWatermarkOptions("CONFIDENTIAL", Layout: WatermarkLayout.Tile));
        Assert.True(wRes.Success, wRes.Message);
        Assert.Equal(3, wRes.PagesProcessed);

        var numPdf = Path.Combine(_dir, "numbered.pdf");
        var nRes = stamp.AddPageNumbers(wmPdf, numPdf, new PdfPageNumberOptions("Page {page} of {total}", 1, PageNumberPosition.BottomCenter));
        Assert.True(nRes.Success, nRes.Message);

        var text = new PdfExtractor().ExtractText(numPdf);
        Assert.True(text.Success);
        Assert.Contains("Page 1 of 3", text.Data);
        Assert.Contains("Page 3 of 3", text.Data);
    }

    [Fact]
    public void PdfExtractor_ExportsText_Markdown_Docx_And_Images()
    {
        var pdf = TestDocumentFixtures.CreatePdfWithImage(Path.Combine(_dir, "rich.pdf"), 400, 300, "Extracted Heading");
        var ext = new PdfExtractor();

        var txtRes = ext.ExtractText(pdf);
        Assert.True(txtRes.Success);
        Assert.Contains("Extracted Heading", txtRes.Data);

        var mdOut = Path.Combine(_dir, "rich.md");
        Assert.True(ext.ExportToMarkdown(pdf, mdOut).Success);
        Assert.Contains("Extracted Heading", File.ReadAllText(mdOut));

        var docxOut = Path.Combine(_dir, "rich.docx");
        Assert.True(ext.ExportToDocx(pdf, docxOut).Success);
        Assert.True(File.Exists(docxOut));

        var imgDir = Path.Combine(_dir, "rich_imgs");
        var imgRes = ext.ExtractEmbeddedImages(pdf, imgDir);
        Assert.True(imgRes.Success);
        Assert.NotEmpty(imgRes.OutputPaths);
    }

    [Fact]
    public void PdfRepairService_SalvagesPdfWithLeadingGarbage()
    {
        var good = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "good.pdf"), 2);
        var damaged = Path.Combine(_dir, "damaged.pdf");
        var bytes = File.ReadAllBytes(good);
        var prefixed = new byte[bytes.Length + 32];
        Array.Fill(prefixed, (byte)'X', 0, 32);
        Array.Copy(bytes, 0, prefixed, 32, bytes.Length);
        File.WriteAllBytes(damaged, prefixed);

        var repaired = Path.Combine(_dir, "repaired.pdf");
        var res = new PdfRepairService().Repair(damaged, repaired);
        Assert.True(res.Success, res.Message);
        Assert.Equal(2, res.Data!.SalvagedPages);
        Assert.Equal(0, res.Data.SkippedPages);
    }

    [Fact]
    public async Task PdfThumbnailCache_EnforcesMemoryCapAndLruEviction()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(Path.Combine(_dir, "many.pdf"), 8);
        using var cache = new PdfThumbnailCache(maxMemoryBytes: 1024 * 1024); // 1 MB cap

        for (var p = 1; p <= 8; p++)
        {
            var bytes = await cache.GetThumbnailBytesAsync(pdf, p, maxWidth: 240);
            Assert.NotNull(bytes);
            Assert.True(bytes!.Length > 100);
            Assert.True(cache.CurrentMemoryBytes <= cache.MaxMemoryBytes);
        }

        Assert.True(cache.CachedCount < 8);
    }
}
