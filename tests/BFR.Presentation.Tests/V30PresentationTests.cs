namespace BFR.Presentation.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BFR.Core.Licensing;
using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using BFR.Presentation.Studios;
using BFR.Presentation.Tools;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharpCore.Pdf;
using Xunit;

public sealed class V30PresentationTests : IDisposable
{
    private readonly string _tempDir;

    public V30PresentationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BFR_V30PresTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static LicenseService CreateLicense(LicenseTier tier) =>
        new(new LicenseStore(new BFR.Core.RealFileSystem(), Path.Combine(Path.GetTempPath(), "bfr_lic_" + Guid.NewGuid().ToString("N") + ".json")),
            tierOverride: () => tier);

    private string CreateSamplePdf(string name, int pages = 4, string? userPassword = null)
    {
        var path = Path.Combine(_tempDir, name);
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        if (!string.IsNullOrEmpty(userPassword))
        {
            doc.SecuritySettings.UserPassword = userPassword;
            doc.SecuritySettings.OwnerPassword = userPassword;
        }
        doc.Save(path);
        return path;
    }

    private string CreateSampleDocxWithMetadata(string name)
    {
        var path = Path.Combine(_tempDir, name);
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("Confidential draft")))));
            doc.PackageProperties.Creator = "Alice";
            doc.PackageProperties.LastModifiedBy = "Bob";
            var comments = main.AddNewPart<WordprocessingCommentsPart>();
            comments.Comments = new Comments(new Comment(new Paragraph(new Run(new Text("Hidden note")))) { Id = "0", Author = "Alice" });
        }
        return path;
    }

    // ---------------------------------------------------------------- RadialMenuModel 7 Profiles

    [Theory]
    [InlineData(RadialContextKind.SinglePdf, "pdf-compress")]
    [InlineData(RadialContextKind.MultiplePdf, "pdf-merge")]
    [InlineData(RadialContextKind.OfficeDoc, "doc-to-pdf")]
    [InlineData(RadialContextKind.Images, "img-to-pdf")]
    [InlineData(RadialContextKind.TextData, "text-to-pdf")]
    [InlineData(RadialContextKind.Mixed, "mixed-merge-pdf")]
    [InlineData(RadialContextKind.Empty, "rename")]
    public void RadialMenuModel_BuildForContext_ReturnsEightDistinctWedgesForAllProfiles(
        RadialContextKind kind, string expectedFirstKey)
    {
        var tools = ToolCatalog.Create();
        var ctx = kind switch
        {
            RadialContextKind.Empty => RadialContextInfo.Empty,
            RadialContextKind.OfficeDoc => new RadialContextInfo(new[] { "report.docx" }, kind, "report.docx", "10 KB"),
            _ => new RadialContextInfo(new[] { "a.pdf", "b.pdf" }, kind, "Test", "10 KB")
        };

        var items = RadialMenuModel.BuildForContext(ctx, tools, vietnamese: false);

        Assert.Equal(RadialMenuModel.Count, items.Count);
        Assert.Equal(items.Count, items.Select(i => i.Key).Distinct().Count());
        Assert.Equal(expectedFirstKey, items[0].Key);
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Icon)));
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.TitleKey)));
    }

    [Fact]
    public void RadialMenuModel_OfficeDoc_AdaptsWedge4ForXlsx()
    {
        var tools = ToolCatalog.Create();
        var docxCtx = RadialContextInfo.Classify(new[] { Path.Combine(_tempDir, "doc.docx") });
        var xlsxCtx = RadialContextInfo.Classify(new[] { Path.Combine(_tempDir, "book.xlsx") });

        var docxItems = RadialMenuModel.BuildForContext(docxCtx, tools, vietnamese: false);
        var xlsxItems = RadialMenuModel.BuildForContext(xlsxCtx, tools, vietnamese: false);

        Assert.Equal("doc-to-text", docxItems[3].Key);
        Assert.Equal("doc-xlsx-convert", xlsxItems[3].Key);
        Assert.Contains(xlsxItems[3].Actions, a => a.Value == "csv");
        Assert.Contains(xlsxItems[3].Actions, a => a.Value == "json");
    }

    [Fact]
    public void RadialContextInfo_Classify_DetectsAllKinds()
    {
        Assert.Equal(RadialContextKind.Empty, RadialContextInfo.Classify(Array.Empty<string>()).Kind);
        Assert.Equal(RadialContextKind.SinglePdf, RadialContextInfo.Classify(new[] { "a.pdf" }).Kind);
        Assert.Equal(RadialContextKind.MultiplePdf, RadialContextInfo.Classify(new[] { "a.pdf", "b.pdf" }).Kind);
        Assert.Equal(RadialContextKind.OfficeDoc, RadialContextInfo.Classify(new[] { "a.docx" }).Kind);
        Assert.Equal(RadialContextKind.Images, RadialContextInfo.Classify(new[] { "a.png", "b.jpg" }).Kind);
        Assert.Equal(RadialContextKind.TextData, RadialContextInfo.Classify(new[] { "a.md", "b.csv" }).Kind);
        Assert.Equal(RadialContextKind.Mixed, RadialContextInfo.Classify(new[] { "a.pdf", "b.png" }).Kind);
    }

    // ---------------------------------------------------------------- PdfStudioViewModel

    [Fact]
    public async Task PdfStudio_ReorderRotateDeleteAndScissorSplit_WorksEndToEnd()
    {
        var pdf = CreateSamplePdf("sample.pdf", pages: 4);
        var vm = new PdfStudioViewModel(CreateLicense(LicenseTier.Pro));

        await vm.AddFilesAsync(new[] { pdf });
        Assert.Single(vm.LoadedFiles);
        Assert.Equal(4, vm.Pages.Count);
        Assert.Equal(4, vm.ActivePageCount);

        // Rotate page 1 by +90 and -180 -> 270
        vm.RotatePage(vm.Pages[0], 90);
        Assert.Equal(90, vm.Pages[0].RotationDegrees);
        vm.RotatePage(vm.Pages[0], -180);
        Assert.Equal(270, vm.Pages[0].RotationDegrees);

        // Move page 4 to index 0
        vm.MovePage(3, 0);
        Assert.Equal(4, vm.Pages[0].PageNumber);

        // Delete page at index 1
        vm.DeletePage(vm.Pages[1]);
        Assert.Equal(3, vm.ActivePageCount);

        // Scissor split after first active page
        vm.ToggleSplitAfter(vm.Pages[0]);
        Assert.True(vm.HasSplitMarkers);
        Assert.Equal(1, vm.Pages[0].PartIndex);
        Assert.Equal(2, vm.Pages[2].PartIndex);

        var splitOutDir = Path.Combine(_tempDir, "split_out");
        var res = await vm.ExecuteOrganizeAsync(splitOutDir);

        Assert.True(res.Success, res.Message);
        Assert.Equal(2, res.OutputPaths.Count);
        Assert.All(res.OutputPaths, p => Assert.True(File.Exists(p)));
        Assert.Equal(2, vm.LastOutputPaths.Count);
        Assert.Contains(vm.ContinueWithSuggestions, s => s.Key == "pdf-compress");
    }

    [Fact]
    public void PdfStudio_ParseRangeAndMarkSplits_MarksExpectedCutPoints()
    {
        var vm = new PdfStudioViewModel(CreateLicense(LicenseTier.Pro));
        for (var i = 1; i <= 6; i++)
            vm.Pages.Add(new PdfPageItemViewModel("doc.pdf", i));

        var ok = vm.ParseRangeAndMarkSplits("1-2, 3-4, 5-end");

        Assert.True(ok);
        Assert.False(vm.Pages[0].SplitAfter);
        Assert.True(vm.Pages[1].SplitAfter);
        Assert.False(vm.Pages[2].SplitAfter);
        Assert.True(vm.Pages[3].SplitAfter);
        Assert.False(vm.Pages[5].SplitAfter);
        Assert.Equal(1, vm.Pages[0].PartIndex);
        Assert.Equal(2, vm.Pages[2].PartIndex);
        Assert.Equal(3, vm.Pages[4].PartIndex);
    }

    [Fact]
    public async Task PdfStudio_EnforcesFreeVsProLimitsWithStructuredUpgradePrompt()
    {
        var freeVm = new PdfStudioViewModel(CreateLicense(LicenseTier.Free));
        var pdfs = new[]
        {
            CreateSamplePdf("f1.pdf", 2),
            CreateSamplePdf("f2.pdf", 2),
            CreateSamplePdf("f3.pdf", 2),
            CreateSamplePdf("f4.pdf", 2)
        };

        await freeVm.AddFilesAsync(pdfs);

        // Free tier allows max 3 PDFs for merge
        var mergeRes = await freeVm.ExecuteOrganizeAsync(Path.Combine(_tempDir, "merged.pdf"));
        Assert.False(mergeRes.Success);
        Assert.Equal(DocumentErrorCode.FreeTierLimitReached, mergeRes.ErrorCode);
        Assert.True(freeVm.ShowUpgradePrompt);
        Assert.Equal(ProFeature.PdfUnlimitedPages, freeVm.UpgradeRequiredFeature);

        // Free tier blocks Watermark/Stamp
        var stampRes = await freeVm.ExecuteStampAsync(
            Path.Combine(_tempDir, "stamped.pdf"),
            new PdfWatermarkOptions("CONFIDENTIAL"));
        Assert.False(stampRes.Success);
        Assert.Equal(ProFeature.PdfSecurityAndStamp, freeVm.UpgradeRequiredFeature);

        // Pro tier succeeds for 4 PDFs
        var proVm = new PdfStudioViewModel(CreateLicense(LicenseTier.Pro));
        await proVm.AddFilesAsync(pdfs);
        var proMerge = await proVm.ExecuteOrganizeAsync(Path.Combine(_tempDir, "pro_merged.pdf"));
        Assert.True(proMerge.Success, proMerge.Message);
        Assert.False(proVm.ShowUpgradePrompt);
    }

    [Fact]
    public async Task PdfStudio_UnlockFileAsync_UnlocksPasswordProtectedPdf()
    {
        var lockedPdf = CreateSamplePdf("locked.pdf", pages: 2, userPassword: "secret");
        var vm = new PdfStudioViewModel(CreateLicense(LicenseTier.Pro));

        await vm.AddFilesAsync(new[] { lockedPdf });
        Assert.True(vm.LoadedFiles[0].IsLocked);
        Assert.Empty(vm.Pages);

        var wrong = await vm.UnlockFileAsync(lockedPdf, "wrong");
        Assert.False(wrong);

        var right = await vm.UnlockFileAsync(lockedPdf, "secret");
        Assert.True(right);
        Assert.False(vm.LoadedFiles[0].IsLocked);
        Assert.Equal(2, vm.Pages.Count);
    }

    // ---------------------------------------------------------------- DocsOcrStudioViewModel

    [Fact]
    public async Task DocsOcrStudio_ConvertMarkdown_CleanPrivacy_AndSpreadsheet_Work()
    {
        var vm = new DocsOcrStudioViewModel(CreateLicense(LicenseTier.Pro));

        // 1. Markdown -> PDF
        var mdPath = Path.Combine(_tempDir, "notes.md");
        await File.WriteAllTextAsync(mdPath, "# Hello BFR v3.0\n\nOffline document hub.");
        vm.AddFiles(new[] { mdPath });
        var pdfOut = Path.Combine(_tempDir, "notes.pdf");
        var pdfRes = await vm.ExecuteConvertToPdfAsync(pdfOut);
        Assert.True(pdfRes.Success, pdfRes.Message);
        Assert.True(File.Exists(pdfOut));
        Assert.Contains(vm.ContinueWithSuggestions, s => s.Key == "pdf-compress");

        // 2. Office Privacy Cleaner
        vm.Clear();
        var docxPath = CreateSampleDocxWithMetadata("secret.docx");
        vm.AddFiles(new[] { docxPath });
        var inspectRes = await vm.InspectPrivacyAsync();
        Assert.True(inspectRes.Success, inspectRes.Message);
        Assert.Equal("Alice", inspectRes.Data?.Author);
        Assert.Equal(1, inspectRes.Data?.CommentsCount);

        var cleanOut = Path.Combine(_tempDir, "secret_clean.docx");
        var cleanRes = await vm.CleanPrivacyAsync(cleanOut);
        Assert.True(cleanRes.Success, cleanRes.Message);
        Assert.True((cleanRes.Data?.TotalItemsRemoved ?? 0) > 0);
        Assert.Null(cleanRes.Data?.After.Author);
        Assert.Equal(0, cleanRes.Data?.After.CommentsCount);

        // 3. Spreadsheet CSV -> XLSX -> CSV
        vm.Clear();
        var csvPath = Path.Combine(_tempDir, "data.csv");
        await File.WriteAllTextAsync(csvPath, "Name,Score\nAlice,95\nBob,88\n");
        vm.AddFiles(new[] { csvPath });
        var xlsxOut = Path.Combine(_tempDir, "data.xlsx");
        var toXlsx = await vm.ExecuteSpreadsheetConvertAsync(xlsxOut, SpreadsheetTargetFormat.Xlsx);
        Assert.True(toXlsx.Success, toXlsx.Message);
        Assert.True(File.Exists(xlsxOut));

        vm.Clear();
        vm.AddFiles(new[] { xlsxOut });
        var csvOut = Path.Combine(_tempDir, "roundtrip.csv");
        var toCsv = await vm.ExecuteSpreadsheetConvertAsync(csvOut, SpreadsheetTargetFormat.Csv);
        Assert.True(toCsv.Success, toCsv.Message);
        Assert.True(File.Exists(csvOut));
    }

    [Fact]
    public async Task DocsOcrStudio_MissingOcrLanguagePack_ReturnsTypedError()
    {
        var vm = new DocsOcrStudioViewModel(CreateLicense(LicenseTier.Free));
        var imgPath = Path.Combine(_tempDir, "dummy.png");
        await File.WriteAllBytesAsync(imgPath, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        vm.AddFiles(new[] { imgPath });
        vm.SelectedLanguageTag = "zz-ZZ-NonExistent";

        Assert.False(vm.IsSelectedLanguageSupported);
        var res = await vm.RunOcrAsync();
        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.MissingOcrLanguagePack, res.ErrorCode);
    }

    // ---------------------------------------------------------------- ContinueWithCatalog & ToolCatalog

    [Fact]
    public void ContinueWithCatalog_ReturnsContextualNextSteps()
    {
        var pdfNext = ContinueWithCatalog.GetSuggestions(new[] { "out.pdf" });
        Assert.Contains(pdfNext, a => a.Key == "pdf-compress");
        Assert.Contains(pdfNext, a => a.Key == "rename");

        var imgNext = ContinueWithCatalog.GetSuggestions(new[] { "photo.jpg" });
        Assert.Contains(imgNext, a => a.Key == "img-to-pdf");
        Assert.Contains(imgNext, a => a.Key == "img-ocr");

        var xlsxNext = ContinueWithCatalog.GetSuggestions(new[] { "sheet.xlsx" });
        Assert.Contains(xlsxNext, a => a.Key == "xlsx-csv");
        Assert.Contains(xlsxNext, a => a.Key == "doc-clean");

        Assert.Empty(ContinueWithCatalog.GetSuggestions(Array.Empty<string>()));
    }

    [Fact]
    public void ToolCatalog_IncludesV30DocumentToolsWithCategories()
    {
        var tools = ToolCatalog.Create();
        Assert.Contains(tools, t => t.Key == "pdf-compress" && t.Category == ToolCategory.Pdf);
        Assert.Contains(tools, t => t.Key == "pdf-merge" && t.Category == ToolCategory.Pdf);
        Assert.Contains(tools, t => t.Key == "doc-to-pdf" && t.Category == ToolCategory.Docs);
        Assert.Contains(tools, t => t.Key == "doc-clean-meta" && t.Category == ToolCategory.Security);
        Assert.Contains(tools, t => t.Key == "ocr-offline" && t.Category == ToolCategory.Docs);
    }
}
