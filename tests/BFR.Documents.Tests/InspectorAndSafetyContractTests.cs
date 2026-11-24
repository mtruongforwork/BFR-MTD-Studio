namespace BFR.Documents.Tests;

using System.Text;
using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using PdfSharpCore.Pdf;
using Xunit;

/// <summary>
/// Hợp đồng của <see cref="DocumentInspector"/> (phân loại 15 ngoại lệ trong plan) và <see cref="DocumentSafety"/> (ghi nguyên tử,
/// không đè tệp, vùng cấm, dung lượng ổ đĩa). Mọi tệp xấu phải ra <see cref="DocumentErrorCode"/>, không bao giờ ném lỗi.
/// </summary>
public sealed class InspectorAndSafetyContractTests : IDisposable
{
    private readonly string _dir;
    private readonly DocumentInspector _inspector = new();

    public InspectorAndSafetyContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_insp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string BlankPdf(string name, int pages)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        doc.Save(P(name));
        return P(name);
    }

    // ------------------------------------------------------------------ KindFromExtension

    [Theory]
    [InlineData(".pdf", DocumentKind.Pdf)]
    [InlineData(".PDF", DocumentKind.Pdf)]
    [InlineData(".docx", DocumentKind.Word)]
    [InlineData(".doc", DocumentKind.Word)]
    [InlineData(".xlsx", DocumentKind.Excel)]
    [InlineData(".xls", DocumentKind.Excel)]
    [InlineData(".pptx", DocumentKind.PowerPoint)]
    [InlineData(".md", DocumentKind.Markdown)]
    [InlineData(".markdown", DocumentKind.Markdown)]
    [InlineData(".txt", DocumentKind.Text)]
    [InlineData(".csv", DocumentKind.Csv)]
    [InlineData(".tsv", DocumentKind.Csv)]
    [InlineData(".png", DocumentKind.Image)]
    [InlineData(".JPG", DocumentKind.Image)]
    [InlineData(".heic", DocumentKind.Image)]
    [InlineData(".webp", DocumentKind.Image)]
    [InlineData(".zip", DocumentKind.Archive)]
    [InlineData(".exe", DocumentKind.Executable)]
    [InlineData(".html", DocumentKind.Html)]
    [InlineData(".xyz", DocumentKind.Unknown)]
    [InlineData("", DocumentKind.Unknown)]
    public void KindFromExtension_RoutesEveryFamilyOfFiles(string ext, DocumentKind expected) =>
        Assert.Equal(expected, DocumentInspector.KindFromExtension(ext));

    // ------------------------------------------------------------------ Inspect

    [Fact]
    public void Inspect_ValidPdf_ReportsPagesAndNoProblems()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(P("ok.pdf"), 3);

        var d = _inspector.Inspect(pdf);

        Assert.Equal(DocumentErrorCode.None, d.ErrorCode);
        Assert.Equal(DocumentKind.Pdf, d.Kind);
        Assert.Equal(3, d.PageCount);
        Assert.Equal("pdf", d.DetectedFormat);
        Assert.False(d.IsEncrypted || d.IsCorrupted || d.IsScannedPdf || d.IsExtensionSpoofed || d.IsLargeDocument);
    }

    [Fact]
    public void Inspect_MissingFile_IsFileNotFound()
    {
        var d = _inspector.Inspect(P("ghost.pdf"));

        Assert.Equal(DocumentErrorCode.FileNotFound, d.ErrorCode);
    }

    [Theory]
    [InlineData(499, DocumentErrorCode.None, false)]
    [InlineData(500, DocumentErrorCode.LargeDocumentWarning, true)]
    public void Inspect_LargeDocumentThreshold_IsFiveHundredPages(int pages, DocumentErrorCode expectedCode, bool expectedLarge)
    {
        var pdf = BlankPdf("big.pdf", pages);

        var d = _inspector.Inspect(pdf);

        Assert.Equal(expectedCode, d.ErrorCode);
        Assert.Equal(expectedLarge, d.IsLargeDocument);
        Assert.Equal(pages, d.PageCount);
    }

    [Fact]
    public void Inspect_TruncatedPdf_IsCorruptedNotACrash()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(P("full.pdf"), 3);
        var bytes = File.ReadAllBytes(pdf);
        File.WriteAllBytes(P("cut.pdf"), bytes[..(bytes.Length / 2)]);

        var d = _inspector.Inspect(P("cut.pdf"));

        Assert.Equal(DocumentErrorCode.CorruptedPdf, d.ErrorCode);
        Assert.True(d.IsCorrupted);
    }

    [Fact]
    public void Inspect_PasswordPdf_DistinguishesMissingWrongAndRightPassword()
    {
        var locked = P("locked.pdf");
        Assert.True(new PdfSecurityService().Protect(TestDocumentFixtures.CreateSamplePdf(P("src.pdf"), 2), locked,
            new PdfProtectOptions(UserPassword: "secret")).Success);

        Assert.Equal(DocumentErrorCode.PasswordRequired, _inspector.Inspect(locked).ErrorCode);
        Assert.Equal(DocumentErrorCode.PasswordRequired, _inspector.Inspect(locked, "wrong").ErrorCode);

        var ok = _inspector.Inspect(locked, "secret");
        Assert.Equal(DocumentErrorCode.None, ok.ErrorCode);
        Assert.Equal(2, ok.PageCount);
    }

    [Theory]
    [InlineData("jpeg-as.pdf", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F' })]
    [InlineData("png-as.pdf", new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("exe-as.pdf", new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 })]
    [InlineData("zip-as.pdf", new byte[] { (byte)'P', (byte)'K', 3, 4, 0, 0, 0, 0 })]
    public void Inspect_BinaryFilesPretendingToBePdf_AreFlaggedAsSpoofed(string name, byte[] content)
    {
        File.WriteAllBytes(P(name), content.Concat(new byte[64]).ToArray());

        var d = _inspector.Inspect(P(name));

        Assert.Equal(DocumentErrorCode.ExtensionSpoofed, d.ErrorCode);
        Assert.True(d.IsExtensionSpoofed);
    }

    [Fact]
    public void Inspect_PdfRenamedToDocx_IsFlaggedAsSpoofed()
    {
        File.Copy(TestDocumentFixtures.CreateSamplePdf(P("real.pdf"), 1), P("fake.docx"));

        var d = _inspector.Inspect(P("fake.docx"));

        Assert.Equal(DocumentErrorCode.ExtensionSpoofed, d.ErrorCode);
    }

    [Fact]
    public void Inspect_GenuineOfficeFiles_AreNotFlagged()
    {
        var docx = TestDocumentFixtures.CreateSampleDocx(P("a.docx"));
        var xlsx = TestDocumentFixtures.CreateSampleXlsx(P("a.xlsx"));

        foreach (var (path, kind) in new[] { (docx, DocumentKind.Word), (xlsx, DocumentKind.Excel) })
        {
            var d = _inspector.Inspect(path);
            Assert.Equal(DocumentErrorCode.None, d.ErrorCode);
            Assert.Equal(kind, d.Kind);
            Assert.Equal("zip", d.DetectedFormat);
        }
    }

    [Theory]
    [InlineData("n.txt", DocumentKind.Text, "plain words")]
    [InlineData("n.md", DocumentKind.Markdown, "# title")]
    [InlineData("n.csv", DocumentKind.Csv, "a,b\n1,2")]
    public void Inspect_PlainTextFamilies_HaveNoMagicAndNoErrors(string name, DocumentKind kind, string content)
    {
        File.WriteAllText(P(name), content);

        var d = _inspector.Inspect(P(name));

        Assert.Equal(DocumentErrorCode.None, d.ErrorCode);
        Assert.Equal(kind, d.Kind);
        Assert.False(d.IsExtensionSpoofed);
    }

    [Fact]
    public void Inspect_ZeroByteFile_IsFlaggedAsCorrupted_ForPdfAndUnsupportedForOthers()
    {
        File.WriteAllBytes(P("empty.pdf"), Array.Empty<byte>());
        File.WriteAllBytes(P("empty.docx"), Array.Empty<byte>());

        Assert.Equal(DocumentErrorCode.CorruptedPdf, _inspector.Inspect(P("empty.pdf")).ErrorCode);
        Assert.Equal(DocumentErrorCode.UnsupportedFormat, _inspector.Inspect(P("empty.docx")).ErrorCode);
    }

    [Fact]
    public async Task Inspect_AlreadyCancelled_EndsAsCancellation_NeverAsAnotherError()
    {
        var pdf = TestDocumentFixtures.CreateSamplePdf(P("ok.pdf"), 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => _inspector.InspectAsync(pdf, ct: cts.Token));

        Assert.True(ex is null or OperationCanceledException, ex?.ToString());
    }

    // ------------------------------------------------------------------ DocumentSafety

    [Fact]
    public void GetUniqueOutputPath_NumbersThePathUntilItIsFree()
    {
        File.WriteAllText(P("r.pdf"), "1");
        Assert.Equal(P("r (2).pdf"), DocumentSafety.GetUniqueOutputPath(P("r.pdf")));

        File.WriteAllText(P("r (2).pdf"), "2");
        File.WriteAllText(P("r (3).pdf"), "3");
        Assert.Equal(P("r (4).pdf"), DocumentSafety.GetUniqueOutputPath(P("r.pdf")));

        Assert.Equal(P("free.pdf"), DocumentSafety.GetUniqueOutputPath(P("free.pdf")));      // chưa tồn tại: giữ nguyên
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryPrepareOutputPath_BlankPath_IsRejected(string path)
    {
        Assert.False(DocumentSafety.TryPrepareOutputPath(path, false, out _, out var code, out var msg));
        Assert.NotEqual(DocumentErrorCode.None, code);
        Assert.False(string.IsNullOrEmpty(msg));
    }

    [Fact]
    public void TryPrepareOutputPath_ProtectedZone_UsesTheProtectedPathCode()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.False(DocumentSafety.TryPrepareOutputPath(Path.Combine(windows, "x.pdf"), true, out _, out var code, out _));
        Assert.Equal(DocumentErrorCode.ProtectedPath, code);
    }

    [Fact]
    public void TryPrepareOutputPath_AllowOverwrite_KeepsTheExistingPath_ElseNumbersIt()
    {
        File.WriteAllText(P("o.pdf"), "x");

        Assert.True(DocumentSafety.TryPrepareOutputPath(P("o.pdf"), true, out var same, out _, out _));
        Assert.Equal(P("o.pdf"), same);

        Assert.True(DocumentSafety.TryPrepareOutputPath(P("o.pdf"), false, out var numbered, out _, out _));
        Assert.Equal(P("o (2).pdf"), numbered);
    }

    [Fact]
    public void CreateTempPath_IsHiddenSiblingWithBfrtmpExtension_AndCreatesTheDirectory()
    {
        var final = P("sub/deeper/result.pdf");

        var temp = DocumentSafety.CreateTempPath(final);

        Assert.Equal(Path.GetDirectoryName(final), Path.GetDirectoryName(temp));      // cùng ổ đĩa → di chuyển nguyên tử
        Assert.EndsWith(".bfrtmp", temp);
        Assert.StartsWith(".", Path.GetFileName(temp));
        Assert.True(Directory.Exists(Path.GetDirectoryName(final)));
        Assert.NotEqual(temp, DocumentSafety.CreateTempPath(final));                  // mỗi lần một tên mới
    }

    [Fact]
    public void CommitTempFile_MovesIntoPlace_AndRefusesToOverwrite()
    {
        File.WriteAllText(P("t1.tmp"), "new");
        DocumentSafety.CommitTempFile(P("t1.tmp"), P("sub/final.txt"));
        Assert.Equal("new", File.ReadAllText(P("sub/final.txt")));
        Assert.False(File.Exists(P("t1.tmp")));

        File.WriteAllText(P("t2.tmp"), "newer");
        Assert.Throws<IOException>(() => DocumentSafety.CommitTempFile(P("t2.tmp"), P("sub/final.txt")));
        Assert.Equal("new", File.ReadAllText(P("sub/final.txt")));                    // tệp cũ nguyên vẹn
        Assert.True(File.Exists(P("t2.tmp")));                                       // bên gọi còn tệp tạm để tự dọn
    }

    [Fact]
    public void CleanupTempFile_NeverThrows_ForNullMissingOrLockedFiles()
    {
        DocumentSafety.CleanupTempFile(null);
        DocumentSafety.CleanupTempFile("");
        DocumentSafety.CleanupTempFile(P("nope.tmp"));

        File.WriteAllText(P("locked.tmp"), "x");
        using (new FileStream(P("locked.tmp"), FileMode.Open, FileAccess.Read, FileShare.None))
            DocumentSafety.CleanupTempFile(P("locked.tmp"));     // đang bị giữ: bỏ qua, không ném
    }

    [Fact]
    public void SafeFileLength_IsZeroForMissingFiles_AndRealSizeOtherwise()
    {
        File.WriteAllBytes(P("five.bin"), new byte[5]);

        Assert.Equal(5, DocumentSafety.SafeFileLength(P("five.bin")));
        Assert.Equal(0, DocumentSafety.SafeFileLength(P("nope.bin")));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), true)]     // ERROR_DISK_FULL
    [InlineData(unchecked((int)0x80070027), true)]     // ERROR_HANDLE_DISK_FULL
    [InlineData(unchecked((int)0x80070020), false)]
    [InlineData(unchecked((int)0x80004005), false)]
    public void IsDiskFull_RecognisesBothDiskFullHResults(int hresult, bool expected) =>
        Assert.Equal(expected, DocumentSafety.IsDiskFull(new IOException("x", hresult)));

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]     // ERROR_SHARING_VIOLATION
    [InlineData(unchecked((int)0x80070021), true)]     // ERROR_LOCK_VIOLATION
    [InlineData(unchecked((int)0x80070070), false)]
    public void IsSharingViolation_RecognisesBothLockHResults(int hresult, bool expected) =>
        Assert.Equal(expected, DocumentSafety.IsSharingViolation(new IOException("x", hresult)));

    [Fact]
    public void HasEnoughDiskSpace_AcceptsSmallNeeds_RejectsImpossibleOnes()
    {
        Assert.True(DocumentSafety.HasEnoughDiskSpace(P("a.pdf"), 1, out var available));
        Assert.True(available > 0);

        Assert.False(DocumentSafety.HasEnoughDiskSpace(P("a.pdf"), long.MaxValue / 2, out _));
    }

    [Fact]
    public void EnsureReadablePath_ReturnsTheSamePath_ForAnOrdinaryFile()
    {
        File.WriteAllText(P("ok.txt"), "x");

        var readable = DocumentSafety.EnsureReadablePath(P("ok.txt"), out var snapshot, out var usedSnapshot);

        Assert.Equal(P("ok.txt"), readable);
        Assert.Null(snapshot);
        Assert.False(usedSnapshot);
    }

    [Fact]
    public void EnsureReadablePath_ExclusivelyLockedFile_FailsInsteadOfReturningABrokenCopy()
    {
        File.WriteAllText(P("busy.txt"), "x");
        using var hold = new FileStream(P("busy.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => DocumentSafety.EnsureReadablePath(P("busy.txt"), out _, out _));
    }

    [Fact]
    public void ErrorCodes_AreStable_BecauseTheyAreShownToUsersAndPersistedInLogs()
    {
        // Số của mã lỗi là hợp đồng (hiển thị trong thông báo, ghi vào log): không đổi số các mã đã phát hành.
        Assert.Equal(1, (int)DocumentErrorCode.PasswordRequired);
        Assert.Equal(5, (int)DocumentErrorCode.CorruptedPdf);
        Assert.Equal(10, (int)DocumentErrorCode.ExtensionSpoofed);
        Assert.Equal(12, (int)DocumentErrorCode.Cancelled);
        Assert.Equal(15, (int)DocumentErrorCode.FreeTierLimitReached);
        Assert.Equal(99, (int)DocumentErrorCode.Unknown);
    }

    [Fact]
    public void DocumentResult_SavingsPercent_IsComputedFromInputAndOutputBytes()
    {
        Assert.Equal(75.0, DocumentResult.Ok(inputBytes: 1000, outputBytes: 250).SavingsPercent);
        Assert.Equal(-100.0, DocumentResult.Ok(inputBytes: 100, outputBytes: 200).SavingsPercent);   // file to ra: số âm
        Assert.Equal(0.0, DocumentResult.Ok(inputBytes: 0, outputBytes: 50).SavingsPercent);         // không chia cho 0
        Assert.Equal(0.0, DocumentResult.Fail(DocumentErrorCode.Unknown, "x").SavingsPercent);
    }

    [Fact]
    public void DocumentResult_OkWithSingleOutput_ExposesItInOutputPaths()
    {
        var r = DocumentResult.Ok(outputPath: @"C:\x\a.pdf");

        Assert.True(r.Success);
        Assert.Equal(new[] { @"C:\x\a.pdf" }, r.OutputPaths);
        Assert.Equal(@"C:\x\a.pdf", r.OutputPath);
        Assert.Equal(DocumentErrorCode.None, r.ErrorCode);
    }
}
