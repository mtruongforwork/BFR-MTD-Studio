namespace BFR.Documents.Tests;

using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using BFR.Documents.Office;
using PdfSharpCore.Pdf.IO;
using Xunit;

/// <summary>
/// Hợp đồng của <see cref="PdfOrganizer"/> (v3.0): mọi đầu vào xấu trả <see cref="DocumentResult"/> có mã lỗi đúng (không ném lỗi ra UI),
/// không bao giờ để lại tệp tạm <c>.bfrtmp</c> hay tệp đầu ra dở dang, và không đè tệp có sẵn.
/// </summary>
public sealed class PdfOrganizerContractTests : IDisposable
{
    private readonly string _dir;
    private readonly PdfOrganizer _org = new();

    public PdfOrganizerContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_org_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string Pdf(string name, int pages, string prefix = "Sample Page") =>
        TestDocumentFixtures.CreateSamplePdf(P(name), pages, prefix);

    private static int PageCount(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    private static int Rotation(string pdf, int pageIndex)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return doc.Pages[pageIndex].Rotate;
    }

    /// <summary>Số trang gốc (từ chữ "Sample Page N") theo thứ tự xuất hiện trong tệp.</summary>
    private static int[] PageNumbersOf(string pdf, string prefix = "Sample Page")
    {
        var text = new PdfExtractor().ExtractText(pdf);
        Assert.True(text.Success, text.Message);
        return System.Text.RegularExpressions.Regex.Matches(text.Data!, prefix + @" (\d+)")
            .Select(m => int.Parse(m.Groups[1].Value)).ToArray();
    }

    private void AssertNoTempFilesLeft() =>
        Assert.Empty(Directory.GetFiles(_dir, "*.bfrtmp", SearchOption.AllDirectories));

    // ------------------------------------------------------------------ ParsePageRanges

    [Theory]
    [InlineData("1-3", 10, new[] { 1, 2, 3 })]
    [InlineData("5", 10, new[] { 5 })]
    [InlineData("8-end", 10, new[] { 8, 9, 10 })]
    [InlineData("end", 10, new[] { 10 })]
    [InlineData("1-end", 3, new[] { 1, 2, 3 })]
    [InlineData("3-1", 5, new[] { 3, 2, 1 })]            // khoảng ngược = đảo thứ tự
    [InlineData("1,1,2", 5, new[] { 1, 1, 2 })]          // cho phép lặp (nhân bản trang)
    [InlineData("  2 , 4-5 ", 6, new[] { 2, 4, 5 })]     // khoảng trắng
    [InlineData("2,,4", 6, new[] { 2, 4 })]              // phần tử rỗng bị bỏ
    [InlineData("END", 4, new[] { 4 })]                  // không phân biệt hoa/thường
    public void ParsePageRanges_ValidSpecs(string spec, int total, int[] expected) =>
        Assert.Equal(expected, PdfOrganizer.ParsePageRanges(spec, total));

    [Theory]
    [InlineData("", 4)]
    [InlineData("   ", 4)]
    public void ParsePageRanges_BlankSpec_MeansAllPages(string spec, int total) =>
        Assert.Equal(Enumerable.Range(1, total), PdfOrganizer.ParsePageRanges(spec, total));

    [Theory]
    [InlineData("0", 5)]
    [InlineData("6", 5)]
    [InlineData("-1", 5)]
    [InlineData("abc", 5)]
    [InlineData("1-9", 5)]
    [InlineData("0-2", 5)]
    [InlineData("x-3", 5)]
    [InlineData("2-y", 5)]
    [InlineData("1.5", 5)]
    public void ParsePageRanges_InvalidSpecs_ThrowArgumentException(string spec, int total) =>
        Assert.Throws<ArgumentException>(() => PdfOrganizer.ParsePageRanges(spec, total));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ParsePageRanges_NoPages_IsEmpty(int total) =>
        Assert.Empty(PdfOrganizer.ParsePageRanges("1-3", total));

    // ------------------------------------------------------------------ Merge

    [Fact]
    public void Merge_KeepsPageOrderAcrossFiles_AndSumsPageCounts()
    {
        var a = Pdf("a.pdf", 2, "Alpha");
        var b = Pdf("b.pdf", 3, "Beta");
        var c = Pdf("c.pdf", 1, "Gamma");

        var res = _org.Merge(new[] { a, b, c }, P("merged.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(6, res.PagesProcessed);
        Assert.Equal(6, PageCount(P("merged.pdf")));
        var text = new PdfExtractor().ExtractText(P("merged.pdf")).Data!;
        Assert.True(text.IndexOf("Alpha 1", StringComparison.Ordinal) < text.IndexOf("Beta 1", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Beta 3", StringComparison.Ordinal) < text.IndexOf("Gamma 1", StringComparison.Ordinal));
        AssertNoTempFilesLeft();
    }

    [Fact]
    public void Merge_SameFileTwice_DuplicatesItsPages()
    {
        var a = Pdf("a.pdf", 2);

        var res = _org.Merge(new[] { a, a }, P("twice.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(4, PageCount(P("twice.pdf")));
    }

    [Fact]
    public void Merge_NoInputs_FailsWithFileNotFound()
    {
        var res = _org.Merge(Array.Empty<string>(), P("none.pdf"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
        Assert.False(File.Exists(P("none.pdf")));
    }

    [Fact]
    public void Merge_MissingInput_FailsAndLeavesNoOutputOrTempFiles()
    {
        var a = Pdf("a.pdf", 1);

        var res = _org.Merge(new[] { a, P("ghost.pdf") }, P("merged.pdf"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
        Assert.False(File.Exists(P("merged.pdf")));
        AssertNoTempFilesLeft();
    }

    [Fact]
    public void Merge_UnsupportedFileType_IsRejectedWithUnsupportedFormat()
    {
        var a = Pdf("a.pdf", 1);
        var bin = P("data.bin");
        File.WriteAllBytes(bin, new byte[] { 1, 2, 3, 4 });

        var res = _org.Merge(new[] { a, bin }, P("merged.pdf"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.UnsupportedFormat, res.ErrorCode);
        Assert.False(File.Exists(P("merged.pdf")));
    }

    [Fact]
    public void Merge_ExecutableRenamedToPdf_IsNeverImported()
    {
        var a = Pdf("a.pdf", 1);
        var fake = P("fake.pdf");
        File.WriteAllBytes(fake, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });   // "MZ" header

        var res = _org.Merge(new[] { a, fake }, P("merged.pdf"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("merged.pdf")));
        AssertNoTempFilesLeft();
    }

    [Fact]
    public void Merge_PasswordProtectedSource_NeedsItsPassword()
    {
        var plain = Pdf("plain.pdf", 1);
        var locked = P("locked.pdf");
        Assert.True(new PdfSecurityService().Protect(Pdf("src.pdf", 2), locked, new PdfProtectOptions(UserPassword: "pw1")).Success);

        var without = _org.Merge(new[] { plain, locked }, P("m1.pdf"));
        Assert.False(without.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, without.ErrorCode);
        Assert.False(File.Exists(P("m1.pdf")));

        var with = _org.Merge(new[] { plain, locked }, P("m2.pdf"),
            new PdfMergeOptions(Passwords: new Dictionary<string, string> { [locked] = "pw1" }));
        Assert.True(with.Success, with.Message);
        Assert.Equal(3, PageCount(P("m2.pdf")));
        AssertNoTempFilesLeft();
    }

    [Fact]
    public void Merge_WrongPassword_FailsWithPasswordRequired()
    {
        var locked = P("locked.pdf");
        Assert.True(new PdfSecurityService().Protect(Pdf("src.pdf", 1), locked, new PdfProtectOptions(UserPassword: "right")).Success);

        var res = _org.Merge(new[] { locked }, P("m.pdf"),
            new PdfMergeOptions(Passwords: new Dictionary<string, string> { [locked] = "wrong" }));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, res.ErrorCode);
    }

    [Fact]
    public void Merge_CancelledBeforeStart_ReturnsCancelled_AndWritesNothing()
    {
        var a = Pdf("a.pdf", 2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var res = _org.Merge(new[] { a, a }, P("m.pdf"), ct: cts.Token);

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.Cancelled, res.ErrorCode);
        Assert.False(File.Exists(P("m.pdf")));
        AssertNoTempFilesLeft();
    }

    [Fact]
    public void Merge_ExistingOutput_IsPreserved_NewResultGetsNumberedName()
    {
        var a = Pdf("a.pdf", 1);
        File.WriteAllText(P("out.pdf"), "precious");

        var res = _org.Merge(new[] { a }, P("out.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal("precious", File.ReadAllText(P("out.pdf")));
        Assert.Equal("out (2).pdf", Path.GetFileName(res.OutputPath));
    }

    [Fact]
    public void Merge_ReportsProgressUpToOne()
    {
        var a = Pdf("a.pdf", 1);
        var b = Pdf("b.pdf", 1);
        var seen = new List<double>();

        var res = _org.Merge(new[] { a, b }, P("m.pdf"), progress: new SyncProgress(seen.Add));

        Assert.True(res.Success, res.Message);
        Assert.Equal(new[] { 0.5, 1.0 }, seen);
    }

    [Fact]
    public void Merge_WithJournal_RecordsAUndoableCreateFile()
    {
        var a = Pdf("a.pdf", 1);

        var res = _org.Merge(new[] { a }, P("m.pdf"), new PdfMergeOptions(RecordJournal: true, JournalDir: P("_journal")));

        Assert.True(res.Success, res.Message);
        Assert.False(string.IsNullOrEmpty(res.JournalPath));
        Assert.True(File.Exists(res.JournalPath));
    }

    [Fact]
    public void Merge_ProtectedOutputZone_IsRejected()
    {
        var a = Pdf("a.pdf", 1);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var res = _org.Merge(new[] { a }, Path.Combine(windows, "bfr_should_never_exist.pdf"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.ProtectedPath, res.ErrorCode);
        Assert.False(File.Exists(Path.Combine(windows, "bfr_should_never_exist.pdf")));
    }

    [Fact]
    public void Merge_SanitizeOption_RemovesJavaScriptFromTheResult()
    {
        var src = Pdf("unsafe.pdf", 1);
        using (var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify))
        {
            var js = new PdfSharpCore.Pdf.PdfDictionary(doc);
            js.Elements.SetName("/S", "/JavaScript");
            js.Elements.SetString("/JS", "app.alert('x');");
            doc.Internals.Catalog.Elements["/OpenAction"] = js;
            doc.Save(src);
        }

        var res = _org.Merge(new[] { src }, P("safe.pdf"), new PdfMergeOptions(SanitizeUnsafeActions: true));

        Assert.True(res.Success, res.Message);
        using var check = PdfReader.Open(P("safe.pdf"), PdfDocumentOpenMode.Modify);
        Assert.False(check.Internals.Catalog.Elements.ContainsKey("/OpenAction"));
    }

    // ------------------------------------------------------------------ Split

    [Fact]
    public void SplitByRanges_CreatesOnePartPerGroup_WithExpectedNamesAndPages()
    {
        var src = Pdf("book.pdf", 10);
        var outDir = P("parts");

        var res = _org.SplitByRanges(src, new[] { "1-3", "4-6", "7-end" }, new PdfSplitOptions(outDir));

        Assert.True(res.Success, res.Message);
        Assert.Equal(new[] { "book_part1.pdf", "book_part2.pdf", "book_part3.pdf" }, res.OutputPaths.Select(Path.GetFileName).ToArray());
        Assert.Equal(new[] { 3, 3, 4 }, res.OutputPaths.Select(PageCount).ToArray());
        Assert.Equal(new[] { 4, 5, 6 }, PageNumbersOf(res.OutputPaths[1]));
        AssertNoTempFilesLeft();
    }

    [Fact]
    public void SplitByRanges_CustomBaseName_IsUsed()
    {
        var src = Pdf("book.pdf", 4);

        var res = _org.SplitByRanges(src, new[] { "1-2" }, new PdfSplitOptions(P("out"), BaseFileName: "chapter"));

        Assert.True(res.Success, res.Message);
        Assert.Equal("chapter_part1.pdf", Path.GetFileName(res.OutputPath));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("1-99")]
    [InlineData("junk")]
    public void SplitByRanges_InvalidRange_FailsWithoutThrowing_AndLeavesNoTempFiles(string range)
    {
        var src = Pdf("book.pdf", 4);

        var res = _org.SplitByRanges(src, new[] { range }, new PdfSplitOptions(P("out")));

        Assert.False(res.Success);
        AssertNoTempFilesLeft();
    }

    [Fact]
    public void SplitByRanges_MissingInput_FailsWithFileNotFound()
    {
        var res = _org.SplitByRanges(P("ghost.pdf"), new[] { "1" }, new PdfSplitOptions(P("out")));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void SplitByRanges_ProtectedOutputDirectory_IsRejected()
    {
        var src = Pdf("book.pdf", 2);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var res = _org.SplitByRanges(src, new[] { "1" }, new PdfSplitOptions(Path.Combine(windows, "bfr_never")));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.ProtectedPath, res.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(windows, "bfr_never")));
    }

    [Fact]
    public void SplitByRanges_BaseNameWithTraversal_CannotWriteOutsideTheOutputDirectory()
    {
        var src = Pdf("book.pdf", 2);
        var outDir = P("parts");

        var res = _org.SplitByRanges(src, new[] { "1" }, new PdfSplitOptions(outDir, BaseFileName: @"..\..\escaped"));

        Assert.True(res.Success, res.Message);
        foreach (var path in res.OutputPaths)
            Assert.Equal(Path.GetFullPath(outDir), Path.GetDirectoryName(Path.GetFullPath(path)));
        Assert.Empty(Directory.GetFiles(_dir, "escaped*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void SplitByRanges_ExistingPart_IsNotOverwritten()
    {
        var src = Pdf("book.pdf", 2);
        Directory.CreateDirectory(P("out"));
        File.WriteAllText(P("out/book_part1.pdf"), "precious");

        var res = _org.SplitByRanges(src, new[] { "1" }, new PdfSplitOptions(P("out")));

        Assert.True(res.Success, res.Message);
        Assert.Equal("precious", File.ReadAllText(P("out/book_part1.pdf")));
        Assert.Equal("book_part1 (2).pdf", Path.GetFileName(res.OutputPath));
    }

    [Fact]
    public void SplitByRanges_PasswordProtected_NeedsPassword()
    {
        var locked = P("locked.pdf");
        Assert.True(new PdfSecurityService().Protect(Pdf("src.pdf", 3), locked, new PdfProtectOptions(UserPassword: "pw")).Success);

        var without = _org.SplitByRanges(locked, new[] { "1-2" }, new PdfSplitOptions(P("o1")));
        Assert.False(without.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, without.ErrorCode);

        var with = _org.SplitByRanges(locked, new[] { "1-2" }, new PdfSplitOptions(P("o2"), Password: "pw"));
        Assert.True(with.Success, with.Message);
        Assert.Equal(2, PageCount(with.OutputPath!));
    }

    [Fact]
    public void SplitByRanges_Cancelled_ReturnsCancelled()
    {
        var src = Pdf("book.pdf", 4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var res = _org.SplitByRanges(src, new[] { "1-2", "3-4" }, new PdfSplitOptions(P("out")), ct: cts.Token);

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.Cancelled, res.ErrorCode);
        AssertNoTempFilesLeft();
    }

    [Theory]
    [InlineData(10, 3, new[] { 3, 3, 3, 1 })]
    [InlineData(10, 5, new[] { 5, 5 })]
    [InlineData(10, 10, new[] { 10 })]
    [InlineData(10, 25, new[] { 10 })]
    [InlineData(7, 1, new[] { 1, 1, 1, 1, 1, 1, 1 })]
    [InlineData(1, 2, new[] { 1 })]
    public void SplitIntoFixedChunks_ProducesExpectedChunkSizes(int pages, int perChunk, int[] expected)
    {
        var src = Pdf("doc.pdf", pages);

        var res = _org.SplitIntoFixedChunks(src, perChunk, new PdfSplitOptions(P("chunks")));

        Assert.True(res.Success, res.Message);
        Assert.Equal(expected, res.OutputPaths.Select(PageCount).ToArray());
        Assert.Equal(pages, res.PagesProcessed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SplitIntoFixedChunks_NonPositiveSize_IsRejected(int perChunk)
    {
        var src = Pdf("doc.pdf", 4);

        var res = _org.SplitIntoFixedChunks(src, perChunk, new PdfSplitOptions(P("chunks")));

        Assert.False(res.Success);
        Assert.False(Directory.Exists(P("chunks")));
    }

    [Theory]
    [InlineData(new[] { 3, 7 }, new[] { 3, 4, 3 })]
    [InlineData(new int[0], new[] { 10 })]
    [InlineData(new[] { 0, 10, 99, -1 }, new[] { 10 })]          // mọi điểm cắt ngoài (1..N-1) bị bỏ
    [InlineData(new[] { 7, 3, 3 }, new[] { 3, 4, 3 })]           // không sắp xếp + trùng lặp
    [InlineData(new[] { 1 }, new[] { 1, 9 })]
    [InlineData(new[] { 9 }, new[] { 9, 1 })]
    public void SplitByCutPoints_ScissorCuts_ProduceExpectedParts(int[] cuts, int[] expected)
    {
        var src = Pdf("doc.pdf", 10);

        var res = _org.SplitByCutPoints(src, cuts, new PdfSplitOptions(P("cut")));

        Assert.True(res.Success, res.Message);
        Assert.Equal(expected, res.OutputPaths.Select(PageCount).ToArray());
    }

    // ------------------------------------------------------------------ Extract / Delete / Rotate

    [Fact]
    public void ExtractPages_KeepsRequestedOrder_IncludingReversedRanges()
    {
        var src = Pdf("doc.pdf", 5);

        var res = _org.ExtractPages(src, "5-3,1", P("x.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(new[] { 5, 4, 3, 1 }, PageNumbersOf(P("x.pdf")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("abc")]
    [InlineData("1-99")]
    public void ExtractPages_InvalidSpec_ReturnsFailure_InsteadOfThrowingIntoTheUi(string spec)
    {
        var src = Pdf("doc.pdf", 3);

        var res = _org.ExtractPages(src, spec, P("x.pdf"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("x.pdf")));
    }

    [Fact]
    public void ExtractPages_MissingInput_ReturnsFileNotFound()
    {
        var res = _org.ExtractPages(P("ghost.pdf"), "1", P("x.pdf"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void DeletePages_RemovesTheRequestedPages_KeepingTheRest()
    {
        var src = Pdf("doc.pdf", 5);

        var res = _org.DeletePages(src, new[] { 2, 4 }, P("x.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(new[] { 1, 3, 5 }, PageNumbersOf(P("x.pdf")));
    }

    [Theory]
    [InlineData(new int[0], 4)]                  // không xoá gì
    [InlineData(new[] { 99, 100 }, 4)]           // ngoài phạm vi: bỏ qua
    [InlineData(new[] { 2, 2, 2 }, 3)]           // trùng lặp chỉ xoá một lần
    [InlineData(new[] { 0, -1 }, 4)]
    public void DeletePages_IgnoresMeaninglessNumbers(int[] delete, int expectedRemaining)
    {
        var src = Pdf("doc.pdf", 4);

        var res = _org.DeletePages(src, delete, P("x.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(expectedRemaining, PageCount(P("x.pdf")));
    }

    [Fact]
    public void DeletePages_AllPages_IsRefused_NoEmptyPdfIsWritten()
    {
        var src = Pdf("doc.pdf", 2);

        var res = _org.DeletePages(src, new[] { 1, 2 }, P("x.pdf"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("x.pdf")));
    }

    [Theory]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    [InlineData(-90, 270)]
    [InlineData(360, 0)]
    [InlineData(450, 90)]
    [InlineData(0, 0)]
    public void RotatePages_NormalisesAnyMultipleOfNinety(int angle, int expected)
    {
        var src = Pdf("doc.pdf", 2);

        var res = _org.RotatePages(src, angle, P("x.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(expected, Rotation(P("x.pdf"), 0));
        Assert.Equal(expected, Rotation(P("x.pdf"), 1));
    }

    [Fact]
    public void RotatePages_OnlyTheTargetPagesTurn()
    {
        var src = Pdf("doc.pdf", 4);

        var res = _org.RotatePages(src, 90, P("x.pdf"), targetPages: new[] { 2, 4 });

        Assert.True(res.Success, res.Message);
        Assert.Equal(new[] { 0, 90, 0, 90 }, Enumerable.Range(0, 4).Select(i => Rotation(P("x.pdf"), i)).ToArray());
    }

    [Theory]
    [InlineData(45)]
    [InlineData(1)]
    [InlineData(91)]
    public void RotatePages_AngleNotMultipleOfNinety_IsRejectedAsInvalidInput_NotReportedAsCorruption(int angle)
    {
        var src = Pdf("doc.pdf", 1);

        var res = _org.RotatePages(src, angle, P("x.pdf"));

        Assert.False(res.Success);
        Assert.NotEqual(DocumentErrorCode.CorruptedPdf, res.ErrorCode);   // tệp nguồn không hỏng; lỗi là do góc
        Assert.False(File.Exists(P("x.pdf")));
    }

    // ------------------------------------------------------------------ BuildFromInstructions

    [Fact]
    public void Build_ReordersAndInterleavesPagesFromSeveralFiles()
    {
        var a = Pdf("a.pdf", 3, "Alpha");
        var b = Pdf("b.pdf", 3, "Beta");

        var res = _org.BuildFromInstructions(new[]
        {
            new PdfPageInstruction(b, 3), new PdfPageInstruction(a, 1), new PdfPageInstruction(b, 1), new PdfPageInstruction(a, 3)
        }, P("x.pdf"));

        Assert.True(res.Success, res.Message);
        var text = new PdfExtractor().ExtractText(P("x.pdf")).Data!;
        var order = new[] { "Beta 3", "Alpha 1", "Beta 1", "Alpha 3" }.Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void Build_RotationAccumulatesOnTopOfTheExistingRotation()
    {
        var src = Pdf("doc.pdf", 1);
        var once = P("once.pdf");
        Assert.True(_org.RotatePages(src, 90, once).Success);

        var res = _org.BuildFromInstructions(new[] { new PdfPageInstruction(once, 1, 90) }, P("twice.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(180, Rotation(P("twice.pdf"), 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-2)]
    public void Build_PageOutOfRange_FailsCleanly_NothingWritten(int page)
    {
        var src = Pdf("doc.pdf", 3);

        var res = _org.BuildFromInstructions(new[] { new PdfPageInstruction(src, 1), new PdfPageInstruction(src, page) }, P("x.pdf"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("x.pdf")));
        AssertNoTempFilesLeft();
    }

    [Fact]
    public void Build_NoInstructions_IsRejected()
    {
        var res = _org.BuildFromInstructions(Array.Empty<PdfPageInstruction>(), P("x.pdf"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("x.pdf")));
    }

    [Fact]
    public void Build_MissingSource_FailsWithoutOutput()
    {
        var res = _org.BuildFromInstructions(new[] { new PdfPageInstruction(P("ghost.pdf"), 1) }, P("x.pdf"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("x.pdf")));
        AssertNoTempFilesLeft();
    }

    [Fact]
    public void Build_PasswordProtectedSource_UsesThePerInstructionPassword()
    {
        var locked = P("locked.pdf");
        Assert.True(new PdfSecurityService().Protect(Pdf("src.pdf", 2), locked, new PdfProtectOptions(UserPassword: "pw")).Success);

        var bad = _org.BuildFromInstructions(new[] { new PdfPageInstruction(locked, 1) }, P("x1.pdf"));
        Assert.False(bad.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, bad.ErrorCode);

        var good = _org.BuildFromInstructions(new[] { new PdfPageInstruction(locked, 2, Password: "pw") }, P("x2.pdf"));
        Assert.True(good.Success, good.Message);
        Assert.Equal(new[] { 2 }, PageNumbersOf(P("x2.pdf")));
    }

    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
