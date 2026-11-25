namespace BFR.Presentation.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BFR.Core;
using BFR.Core.Licensing;
using BFR.Presentation.Studios;
using PdfSharpCore.Pdf;
using Xunit;

public sealed class PdfStudioOrganizeUxTests : IDisposable
{
    private readonly string _tempDir;

    public PdfStudioOrganizeUxTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BFR_PdfOrgUxTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static LicenseService CreateLicense(LicenseTier tier = LicenseTier.Pro) =>
        new(new LicenseStore(new RealFileSystem(), Path.Combine(Path.GetTempPath(), "bfr_lic_" + Guid.NewGuid().ToString("N") + ".json")),
            tierOverride: () => tier);

    private string CreateSamplePdf(string name, int pages)
    {
        var path = Path.Combine(_tempDir, name);
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        doc.Save(path);
        return path;
    }

    // ---------------------------------------------------------------- 1. Smart Output Naming

    [Theory]
    [InlineData("report_extracted", "report")]
    [InlineData("report_organized", "report")]
    [InlineData("report_split", "report")]
    [InlineData("report_stamped", "report")]
    [InlineData("report_compressed", "report")]
    [InlineData("report_protected", "report")]
    [InlineData("report_unlocked", "report")]
    [InlineData("report_clean", "report")]
    [InlineData("report_pages", "report")]
    [InlineData("report_part1", "report")]
    [InlineData("report_part12", "report")]
    [InlineData("report_p1-3-5-7", "report")]
    [InlineData("report_extracted_organized_part2_p1-3", "report")]
    [InlineData("QUARTERLY_REPORT_EXTRACTED_COMPRESSED", "QUARTERLY_REPORT")]
    [InlineData("invoice_2026_final", "invoice_2026_final")]
    [InlineData("", "")]
    public void StripKnownSuffixes_IterativelyStripsWithoutStacking(string input, string expected)
    {
        Assert.Equal(expected, PdfStudioViewModel.StripKnownSuffixes(input));
    }

    [Fact]
    public void BuildExtractOutputFileName_FormatsShortLongEmptyAndAllPages()
    {
        // Short selection + stacked suffix stripping
        Assert.Equal(
            "report_p1-3-5-7.pdf",
            PdfStudioViewModel.BuildExtractOutputFileName(@"C:\docs\report_extracted_part1.pdf", new[] { 7, 1, 5, 3, 3 }));

        // Long suffix (> 20 chars) -> _pages.pdf
        var manyPages = Enumerable.Range(1, 12).ToList();
        Assert.Equal(
            "report_pages.pdf",
            PdfStudioViewModel.BuildExtractOutputFileName(@"C:\docs\report.pdf", manyPages));

        // Empty selection -> _organized.pdf
        Assert.Equal(
            "report_organized.pdf",
            PdfStudioViewModel.BuildExtractOutputFileName(@"C:\docs\report_split.pdf", Array.Empty<int>()));

        // All active pages selected -> _organized.pdf
        Assert.Equal(
            "report_organized.pdf",
            PdfStudioViewModel.BuildExtractOutputFileName(@"C:\docs\report.pdf", new[] { 1, 2, 3, 4 }, totalActivePages: 4));
    }

    [Fact]
    public void BuildSplitPartFileName_StripsKnownSuffixes()
    {
        Assert.Equal("contract_part1.pdf", PdfStudioViewModel.BuildSplitPartFileName(@"C:\docs\contract_part3_organized.pdf", 1));
        Assert.Equal("contract_part4.pdf", PdfStudioViewModel.BuildSplitPartFileName("contract_extracted.pdf", 4));
    }

    // ---------------------------------------------------------------- 2. 2-Way Split Sync & Validation

    [Fact]
    public async Task ValidateAndApplySplitSpec_ValidSpec_UpdatesMarkersAndReturnsGroups()
    {
        var vm = new PdfStudioViewModel(CreateLicense());
        var pdf = CreateSamplePdf("doc8.pdf", 8);
        await vm.AddFilesAsync(new[] { pdf });

        var result = vm.ValidateAndApplySplitSpec("1-2; 3-end");

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessageVi);
        Assert.Null(result.ErrorMessageEn);
        Assert.Equal(2, result.Groups.Count);
        Assert.Equal(new SplitPreviewGroup(1, 1, 2, 2), result.Groups[0]);
        Assert.Equal(new SplitPreviewGroup(2, 3, 8, 6), result.Groups[1]);

        Assert.True(vm.Pages[1].SplitAfter);
        Assert.False(vm.Pages[0].SplitAfter);
        Assert.False(vm.Pages[7].SplitAfter);
        Assert.Equal(1, vm.Pages[1].PartIndex);
        Assert.Equal(2, vm.Pages[2].PartIndex);
        Assert.Equal("1-2; 3-8", vm.BuildSplitSpecFromMarkers());
    }

    [Fact]
    public async Task ValidateAndApplySplitSpec_OutOfRangeAndInvalidSyntax_ReturnsSpecificErrors()
    {
        var vm = new PdfStudioViewModel(CreateLicense());
        var pdf = CreateSamplePdf("doc8.pdf", 8);
        await vm.AddFilesAsync(new[] { pdf });

        var outOfRange = vm.ValidateAndApplySplitSpec("1-2; 3-12");
        Assert.False(outOfRange.IsValid);
        Assert.Contains("12", outOfRange.ErrorMessageVi);
        Assert.Contains("8", outOfRange.ErrorMessageVi);
        Assert.Contains("12", outOfRange.ErrorMessageEn);
        Assert.Contains("8", outOfRange.ErrorMessageEn);
        Assert.Empty(outOfRange.Groups);
        Assert.False(vm.HasSplitMarkers);

        var badSyntax = vm.ValidateAndApplySplitSpec("1-2; abc");
        Assert.False(badSyntax.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(badSyntax.ErrorMessageVi));
        Assert.False(string.IsNullOrWhiteSpace(badSyntax.ErrorMessageEn));

        var reversed = vm.ValidateAndApplySplitSpec("5-2");
        Assert.False(reversed.IsValid);
    }

    [Fact]
    public async Task BuildSplitSpecFromMarkers_SingleAndMultiPart()
    {
        var vm = new PdfStudioViewModel(CreateLicense());
        var pdf = CreateSamplePdf("doc5.pdf", 5);
        await vm.AddFilesAsync(new[] { pdf });

        Assert.Equal("1-5", vm.BuildSplitSpecFromMarkers());

        vm.ToggleSplitAfter(vm.Pages[0]); // after p1
        vm.ToggleSplitAfter(vm.Pages[2]); // after p3
        Assert.Equal("1; 2-3; 4-5", vm.BuildSplitSpecFromMarkers());
    }

    [Fact]
    public async Task SplitEveryNPages_And_SplitEachPage_MarkExpectedBoundaries()
    {
        var vm = new PdfStudioViewModel(CreateLicense());
        var pdf = CreateSamplePdf("doc7.pdf", 7);
        await vm.AddFilesAsync(new[] { pdf });

        vm.SplitEveryNPages(3);
        Assert.Equal("1-3; 4-6; 7", vm.BuildSplitSpecFromMarkers());
        Assert.Equal(3, vm.GetCurrentSplitGroups().Count);

        vm.SplitEachPage();
        Assert.Equal("1; 2; 3; 4; 5; 6; 7", vm.BuildSplitSpecFromMarkers());
        Assert.False(vm.Pages[6].SplitAfter);
        Assert.Equal(7, vm.GetCurrentSplitGroups().Count);
    }

    // ---------------------------------------------------------------- 3. Dirty Tracking, MovePages, RestorePages, Reset, ReorderLoadedFiles

    [Fact]
    public async Task MovePages_PreservesRelativeOrderAndTracksUnsavedChanges()
    {
        var vm = new PdfStudioViewModel(CreateLicense());
        var pdf = CreateSamplePdf("doc5.pdf", 5);
        await vm.AddFilesAsync(new[] { pdf });
        Assert.Equal(0, vm.UnsavedChangeCount);

        // Move pages 1 & 2 (indices 0, 1) down to index 3 (after page 4)
        vm.MovePages(new[] { 0, 1 }, 3);
        Assert.Equal(new[] { 3, 4, 1, 2, 5 }, vm.Pages.Select(p => p.PageNumber).ToArray());
        Assert.Equal(1, vm.UnsavedChangeCount);

        // Move non-contiguous indices [2, 4] (pages 1 and 5) to index 0
        vm.MovePages(new[] { 4, 2 }, 0);
        Assert.Equal(new[] { 1, 5, 3, 4, 2 }, vm.Pages.Select(p => p.PageNumber).ToArray());
        Assert.Equal(2, vm.UnsavedChangeCount);

        // No-op move should not increment UnsavedChangeCount
        vm.MovePages(new[] { 0, 1 }, 0);
        Assert.Equal(2, vm.UnsavedChangeCount);
    }

    [Fact]
    public async Task RestorePages_RestoresDeletedPagesAndRecalculatesActiveCount()
    {
        var vm = new PdfStudioViewModel(CreateLicense());
        var pdf = CreateSamplePdf("doc4.pdf", 4);
        await vm.AddFilesAsync(new[] { pdf });

        var p2 = vm.Pages[1];
        var p3 = vm.Pages[2];
        vm.DeletePage(p2);
        vm.DeletePage(p3);
        Assert.Equal(2, vm.ActivePageCount);
        Assert.Equal(2, vm.UnsavedChangeCount);

        vm.RestorePages(new[] { p2, p3 });
        Assert.Equal(4, vm.ActivePageCount);
        Assert.False(p2.IsDeleted);
        Assert.False(p3.IsDeleted);
        Assert.Equal(0, vm.UnsavedChangeCount);
    }

    [Fact]
    public async Task ReorderLoadedFiles_And_ResetOrganizeChanges_WorkEndToEnd()
    {
        var vm = new PdfStudioViewModel(CreateLicense());
        var pdfA = CreateSamplePdf("a.pdf", 2);
        var pdfB = CreateSamplePdf("b.pdf", 3);
        var pdfC = CreateSamplePdf("c.pdf", 1);
        await vm.AddFilesAsync(new[] { pdfA, pdfB, pdfC });

        // Move C (index 2) to front (index 0)
        vm.ReorderLoadedFiles(2, 0);
        Assert.Equal(new[] { "c.pdf", "a.pdf", "b.pdf" }, vm.LoadedFiles.Select(f => f.FileName).ToArray());
        Assert.Equal(
            new[] { "c.pdf:1", "a.pdf:1", "a.pdf:2", "b.pdf:1", "b.pdf:2", "b.pdf:3" },
            vm.Pages.Select(p => $"{p.FileName}:{p.PageNumber}").ToArray());
        Assert.Equal(1, vm.UnsavedChangeCount);

        // Mutate pages further
        vm.RotatePage(vm.Pages[0], 90);
        vm.DeletePage(vm.Pages[1]);
        vm.ToggleSplitAfter(vm.Pages[2]);
        Assert.True(vm.UnsavedChangeCount >= 3);

        // Reset restores everything back to original file order + page order + clean state
        vm.ResetOrganizeChanges();
        Assert.Equal(0, vm.UnsavedChangeCount);
        Assert.Equal(6, vm.ActivePageCount);
        Assert.False(vm.HasSplitMarkers);
        Assert.All(vm.Pages, p =>
        {
            Assert.False(p.IsDeleted);
            Assert.Equal(0, p.RotationDegrees);
            Assert.False(p.SplitAfter);
        });
        Assert.Equal(new[] { "a.pdf", "b.pdf", "c.pdf" }, vm.LoadedFiles.Select(f => f.FileName).ToArray());
        Assert.Equal(
            new[] { "a.pdf:1", "a.pdf:2", "b.pdf:1", "b.pdf:2", "b.pdf:3", "c.pdf:1" },
            vm.Pages.Select(p => $"{p.FileName}:{p.PageNumber}").ToArray());
    }

    [Fact]
    public async Task ExecuteOrganizeToPathAsync_And_SplitAsync_UseCleanNamingAndResetDirtyCount()
    {
        var vm = new PdfStudioViewModel(CreateLicense());
        var pdf = CreateSamplePdf("report_extracted.pdf", 4);
        await vm.AddFilesAsync(new[] { pdf });

        vm.RotatePage(vm.Pages[0], 90);
        vm.SplitEveryNPages(2);
        Assert.Equal(1, vm.UnsavedChangeCount);

        // Direct save to single file ignores split markers and resets UnsavedChangeCount
        var directOut = Path.Combine(_tempDir, "saved_single.pdf");
        var saveRes = await vm.ExecuteOrganizeToPathAsync(directOut);
        Assert.True(saveRes.Success);
        Assert.True(File.Exists(directOut));
        Assert.Equal(0, vm.UnsavedChangeCount);

        // Split mode outputs clean report_part1.pdf, report_part2.pdf (not report_extracted_part1.pdf!)
        var splitDir = Path.Combine(_tempDir, "split_out");
        var splitRes = await vm.ExecuteOrganizeAsync(splitDir);
        Assert.True(splitRes.Success);
        Assert.Equal(2, splitRes.OutputPaths.Count);
        Assert.Equal("report_part1.pdf", Path.GetFileName(splitRes.OutputPaths[0]));
        Assert.Equal("report_part2.pdf", Path.GetFileName(splitRes.OutputPaths[1]));
    }
}
