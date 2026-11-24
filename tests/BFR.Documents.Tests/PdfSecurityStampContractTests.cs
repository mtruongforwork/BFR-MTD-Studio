namespace BFR.Documents.Tests;

using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Xunit;

/// <summary>
/// Hợp đồng của <see cref="PdfSecurityService"/> (khoá / mở khoá / xoá metadata) và <see cref="PdfStampService"/> (watermark / số trang).
/// Trọng tâm: mật khẩu sai không bao giờ cho đầu ra, không đè tệp, không ném lỗi, nội dung gốc không đổi.
/// </summary>
public sealed class PdfSecurityStampContractTests : IDisposable
{
    private readonly string _dir;
    private readonly PdfSecurityService _sec = new();
    private readonly PdfStampService _stamp = new();

    public PdfSecurityStampContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_sec_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string Pdf(string name, int pages, string prefix = "Sample Page") =>
        TestDocumentFixtures.CreateSamplePdf(P(name), pages, prefix);

    private string Locked(string name, string user, string? owner = null, int pages = 2)
    {
        var src = Pdf("src_" + name, pages);
        var res = _sec.Protect(src, P(name), new PdfProtectOptions(UserPassword: user, OwnerPassword: owner));
        Assert.True(res.Success, res.Message);
        return P(name);
    }

    private static int PageCount(string pdf, string? password = null)
    {
        using var doc = string.IsNullOrEmpty(password)
            ? PdfReader.Open(pdf, PdfDocumentOpenMode.Import)
            : PdfReader.Open(pdf, password, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    private void AssertNoTempFiles() => Assert.Empty(Directory.GetFiles(_dir, "*.bfrtmp", SearchOption.AllDirectories));

    // ------------------------------------------------------------------ Protect

    [Fact]
    public void Protect_WithoutAnyPassword_IsRejected_AndWritesNothing()
    {
        var res = _sec.Protect(Pdf("a.pdf", 1), P("p.pdf"), new PdfProtectOptions());

        Assert.False(res.Success);
        Assert.False(File.Exists(P("p.pdf")));
    }

    [Fact]
    public void Protect_MissingInput_IsFileNotFound()
    {
        var res = _sec.Protect(P("ghost.pdf"), P("p.pdf"), new PdfProtectOptions(UserPassword: "x"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void Protect_UserPassword_IsRequiredToOpen_AndKeepsEveryPage()
    {
        var locked = Locked("l.pdf", "pw", pages: 3);

        Assert.Throws<PdfReaderException>(() => PageCount(locked));
        Assert.Equal(3, PageCount(locked, "pw"));
    }

    [Fact]
    public void Protect_DoesNotTouchTheSourceFile()
    {
        var src = Pdf("a.pdf", 2);
        var before = File.ReadAllBytes(src);

        Assert.True(_sec.Protect(src, P("p.pdf"), new PdfProtectOptions(UserPassword: "x")).Success);

        Assert.Equal(before, File.ReadAllBytes(src));
    }

    [Fact]
    public void Protect_AlreadyProtectedSource_NeedsTheExistingPassword()
    {
        var locked = Locked("l.pdf", "old");

        var without = _sec.Protect(locked, P("p1.pdf"), new PdfProtectOptions(UserPassword: "new"));
        Assert.False(without.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, without.ErrorCode);
        Assert.False(File.Exists(P("p1.pdf")));

        var with = _sec.Protect(locked, P("p2.pdf"), new PdfProtectOptions(UserPassword: "new", ExistingPassword: "old"));
        Assert.True(with.Success, with.Message);
        Assert.Equal(2, PageCount(P("p2.pdf"), "new"));
        Assert.Throws<PdfReaderException>(() => PageCount(P("p2.pdf"), "old"));       // mật khẩu cũ không còn mở được
    }

    [Fact]
    public void Protect_OwnerPasswordOnly_StillOpensWithoutAPassword()
    {
        var src = Pdf("a.pdf", 2);

        var res = _sec.Protect(src, P("p.pdf"), new PdfProtectOptions(OwnerPassword: "owner", PermitPrint: false));

        Assert.True(res.Success, res.Message);
        Assert.Equal(2, PageCount(P("p.pdf")));
        Assert.False(new DocumentInspector().Inspect(P("p.pdf")).RequiresUserPassword);
    }

    [Fact]
    public void Protect_ExistingOutput_IsNeverOverwritten()
    {
        File.WriteAllText(P("p.pdf"), "precious");

        var res = _sec.Protect(Pdf("a.pdf", 1), P("p.pdf"), new PdfProtectOptions(UserPassword: "x"));

        Assert.True(res.Success, res.Message);
        Assert.Equal("precious", File.ReadAllText(P("p.pdf")));
        Assert.Equal("p (2).pdf", Path.GetFileName(res.OutputPath));
    }

    [Fact]
    public void Protect_Cancelled_ReturnsCancelled_AndLeavesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var res = _sec.Protect(Pdf("a.pdf", 1), P("p.pdf"), new PdfProtectOptions(UserPassword: "x"), cts.Token);

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.Cancelled, res.ErrorCode);
        Assert.False(File.Exists(P("p.pdf")));
        AssertNoTempFiles();
    }

    [Fact]
    public void Protect_WithJournal_RecordsTheGeneratedFile()
    {
        var res = _sec.Protect(Pdf("a.pdf", 1), P("p.pdf"),
            new PdfProtectOptions(UserPassword: "x", RecordJournal: true, JournalDir: P("_j")));

        Assert.True(res.Success, res.Message);
        Assert.True(File.Exists(res.JournalPath));
    }

    [Fact]
    public void Protect_NotAPdf_FailsCleanly()
    {
        File.WriteAllText(P("fake.pdf"), "hello");

        var res = _sec.Protect(P("fake.pdf"), P("p.pdf"), new PdfProtectOptions(UserPassword: "x"));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("p.pdf")));
        AssertNoTempFiles();
    }

    // ------------------------------------------------------------------ Unlock

    [Fact]
    public void Unlock_RightPassword_ProducesAnOpenPdfWithTheSameText()
    {
        var locked = Locked("l.pdf", "pw", pages: 3);

        var res = _sec.Unlock(locked, P("u.pdf"), "pw");

        Assert.True(res.Success, res.Message);
        Assert.Equal(3, PageCount(P("u.pdf")));
        var text = new PdfExtractor().ExtractText(P("u.pdf"));
        Assert.True(text.Success, text.Message);
        Assert.Contains("Sample Page 3", text.Data);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData("PW")]            // phân biệt hoa/thường
    public void Unlock_WrongPassword_IsPasswordRequired_AndWritesNothing(string password)
    {
        var locked = Locked("l.pdf", "pw");

        var res = _sec.Unlock(locked, P("u.pdf"), password);

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, res.ErrorCode);
        Assert.False(File.Exists(P("u.pdf")));
        AssertNoTempFiles();
    }

    [Fact]
    public void Unlock_AnUnprotectedPdf_JustProducesACopy()
    {
        var res = _sec.Unlock(Pdf("a.pdf", 2), P("u.pdf"), "irrelevant");

        Assert.True(res.Success, res.Message);
        Assert.Equal(2, PageCount(P("u.pdf")));
    }

    [Fact]
    public void Unlock_MissingInput_IsFileNotFound()
    {
        var res = _sec.Unlock(P("ghost.pdf"), P("u.pdf"), "x");

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void Unlock_GarbageFile_IsAFailureNotACrash()
    {
        File.WriteAllText(P("junk.pdf"), "%PDF-1.4 definitely not a pdf");

        var res = _sec.Unlock(P("junk.pdf"), P("u.pdf"), "x");

        Assert.False(res.Success);
        Assert.False(File.Exists(P("u.pdf")));
        AssertNoTempFiles();
    }

    // ------------------------------------------------------------------ StripMetadata

    [Fact]
    public void StripMetadata_ClearsEveryDescriptiveField_AndKeepsTheContent()
    {
        var src = Pdf("a.pdf", 2);

        var res = _sec.StripMetadata(src, P("clean.pdf"));

        Assert.True(res.Success, res.Message);
        using var check = PdfReader.Open(P("clean.pdf"), PdfDocumentOpenMode.InformationOnly);
        Assert.True(string.IsNullOrEmpty(check.Info.Author));
        Assert.True(string.IsNullOrEmpty(check.Info.Title));
        Assert.True(string.IsNullOrEmpty(check.Info.Subject));
        Assert.True(string.IsNullOrEmpty(check.Info.Keywords));
        Assert.Equal(2, check.PageCount);
        Assert.Contains("Sample Page 2", new PdfExtractor().ExtractText(P("clean.pdf")).Data);
    }

    [Fact]
    public void StripMetadata_LeavesTheSourceMetadataUntouched()
    {
        var src = Pdf("a.pdf", 1);

        Assert.True(_sec.StripMetadata(src, P("clean.pdf")).Success);

        using var original = PdfReader.Open(src, PdfDocumentOpenMode.InformationOnly);
        Assert.Equal("Test Author", original.Info.Author);
    }

    [Fact]
    public void StripMetadata_WithUnsafeActionsKept_DoesNotRemoveThem()
    {
        var src = Pdf("a.pdf", 1);
        using (var doc = PdfReader.Open(src, PdfDocumentOpenMode.Modify))
        {
            var js = new PdfDictionary(doc);
            js.Elements.SetName("/S", "/JavaScript");
            js.Elements.SetString("/JS", "app.alert('x');");
            doc.Internals.Catalog.Elements["/OpenAction"] = js;
            doc.Save(src);
        }

        var res = _sec.StripMetadata(src, P("keep.pdf"), stripUnsafeActions: false);

        Assert.True(res.Success, res.Message);
        Assert.Equal(0, res.Data!.UnsafeActionsRemoved);
        using var check = PdfReader.Open(P("keep.pdf"), PdfDocumentOpenMode.Modify);
        Assert.True(check.Internals.Catalog.Elements.ContainsKey("/OpenAction"));
    }

    [Fact]
    public void StripMetadata_PasswordPdf_NeedsThePassword()
    {
        var locked = Locked("l.pdf", "pw");

        var without = _sec.StripMetadata(locked, P("c1.pdf"));
        Assert.False(without.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, without.ErrorCode);

        var with = _sec.StripMetadata(locked, P("c2.pdf"), password: "pw");
        Assert.True(with.Success, with.Message);
        Assert.Equal(2, PageCount(P("c2.pdf")));
    }

    [Fact]
    public void StripMetadata_MissingInput_IsFileNotFound()
    {
        var res = _sec.StripMetadata(P("ghost.pdf"), P("c.pdf"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void StripMetadata_IsIdempotent_RunningItOnItsOwnOutputStillWorks()
    {
        Assert.True(_sec.StripMetadata(Pdf("a.pdf", 1), P("c1.pdf")).Success);

        var again = _sec.StripMetadata(P("c1.pdf"), P("c2.pdf"));

        Assert.True(again.Success, again.Message);
        Assert.Equal(0, again.Data!.UnsafeActionsRemoved);
    }

    // ------------------------------------------------------------------ Watermark

    [Fact]
    public void Watermark_WithNeitherTextNorImage_IsRejected()
    {
        var res = _stamp.AddWatermark(Pdf("a.pdf", 1), P("w.pdf"), new PdfWatermarkOptions(Text: null));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("w.pdf")));
    }

    [Fact]
    public void Watermark_ImageThatDoesNotExist_AndNoText_IsAnError_NotASilentUnchangedCopy()
    {
        var res = _stamp.AddWatermark(Pdf("a.pdf", 1), P("w.pdf"),
            new PdfWatermarkOptions(Text: null, ImagePath: P("ghost.png")));

        Assert.False(res.Success);
        Assert.False(File.Exists(P("w.pdf")));
    }

    [Fact]
    public void Watermark_MissingInput_IsFileNotFound()
    {
        var res = _stamp.AddWatermark(P("ghost.pdf"), P("w.pdf"), new PdfWatermarkOptions("X"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Theory]
    [InlineData(WatermarkLayout.Center)]
    [InlineData(WatermarkLayout.Tile)]
    public void Watermark_BothLayouts_StampEveryPage_AndKeepTheOriginalText(WatermarkLayout layout)
    {
        var src = Pdf("a.pdf", 4);

        var res = _stamp.AddWatermark(src, P("w.pdf"), new PdfWatermarkOptions("DRAFT", Layout: layout, AngleDegrees: 0));

        Assert.True(res.Success, res.Message);
        Assert.Equal(4, res.PagesProcessed);
        Assert.Equal(4, PageCount(P("w.pdf")));
        var text = new PdfExtractor().ExtractText(P("w.pdf")).Data!;
        Assert.Contains("DRAFT", text);
        Assert.Contains("Sample Page 4", text);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(0.01)]
    [InlineData(1.0)]
    [InlineData(5.0)]
    public void Watermark_OpacityOutsideTheUsefulRange_IsClamped_NotRejected(double opacity)
    {
        var res = _stamp.AddWatermark(Pdf("a.pdf", 1), P("w.pdf"), new PdfWatermarkOptions("X", Opacity: opacity));

        Assert.True(res.Success, res.Message);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(500.0)]
    public void Watermark_FontSizeOutsideTheUsefulRange_IsClamped_NotRejected(double size)
    {
        var res = _stamp.AddWatermark(Pdf("a.pdf", 1), P("w.pdf"), new PdfWatermarkOptions("X", FontSize: size));

        Assert.True(res.Success, res.Message);
    }

    [Theory]
    [InlineData("#FF0000")]
    [InlineData("FF0000")]
    [InlineData("not-a-colour")]
    [InlineData("")]
    [InlineData("#12")]
    public void Watermark_ColourText_NeverBreaksTheOperation(string colour)
    {
        var res = _stamp.AddWatermark(Pdf("a.pdf", 1), P("w.pdf"), new PdfWatermarkOptions("X", ColorHex: colour));

        Assert.True(res.Success, res.Message);
    }

    [Fact]
    public void Watermark_ImageWatermark_IsApplied_AndGrowsThePdf()
    {
        var src = Pdf("a.pdf", 2);
        var logo = TestDocumentFixtures.CreatePngImage(P("logo.png"), 200, 80, "LOGO");

        var res = _stamp.AddWatermark(src, P("w.pdf"), new PdfWatermarkOptions(Text: null, ImagePath: logo));

        Assert.True(res.Success, res.Message);
        Assert.Equal(2, PageCount(P("w.pdf")));
        Assert.True(new FileInfo(P("w.pdf")).Length > new FileInfo(src).Length);
    }

    [Fact]
    public void Watermark_DoesNotModifyTheSource()
    {
        var src = Pdf("a.pdf", 2);
        var before = File.ReadAllBytes(src);

        Assert.True(_stamp.AddWatermark(src, P("w.pdf"), new PdfWatermarkOptions("X")).Success);

        Assert.Equal(before, File.ReadAllBytes(src));
    }

    [Fact]
    public void Watermark_PasswordPdf_NeedsThePassword()
    {
        var locked = Locked("l.pdf", "pw");

        var without = _stamp.AddWatermark(locked, P("w1.pdf"), new PdfWatermarkOptions("X"));
        Assert.False(without.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, without.ErrorCode);
        Assert.False(File.Exists(P("w1.pdf")));

        var with = _stamp.AddWatermark(locked, P("w2.pdf"), new PdfWatermarkOptions("X", Password: "pw"));
        Assert.True(with.Success, with.Message);
    }

    [Fact]
    public void Watermark_Cancelled_ReturnsCancelled_AndLeavesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var res = _stamp.AddWatermark(Pdf("a.pdf", 3), P("w.pdf"), new PdfWatermarkOptions("X"), ct: cts.Token);

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.Cancelled, res.ErrorCode);
        Assert.False(File.Exists(P("w.pdf")));
        AssertNoTempFiles();
    }

    // ------------------------------------------------------------------ Page numbers

    [Theory]
    [InlineData(PageNumberPosition.BottomCenter)]
    [InlineData(PageNumberPosition.BottomRight)]
    [InlineData(PageNumberPosition.BottomLeft)]
    [InlineData(PageNumberPosition.TopCenter)]
    [InlineData(PageNumberPosition.TopRight)]
    [InlineData(PageNumberPosition.TopLeft)]
    public void PageNumbers_AllSixPositions_LabelEveryPage(PageNumberPosition position)
    {
        var res = _stamp.AddPageNumbers(Pdf("a.pdf", 3), P("n.pdf"), new PdfPageNumberOptions("{page}/{total}", 1, position));

        Assert.True(res.Success, res.Message);
        var text = new PdfExtractor().ExtractText(P("n.pdf")).Data!;
        Assert.Contains("1/3", text);
        Assert.Contains("2/3", text);
        Assert.Contains("3/3", text);
    }

    [Fact]
    public void PageNumbers_StartNumber_ShiftsEveryLabel()
    {
        var res = _stamp.AddPageNumbers(Pdf("a.pdf", 3), P("n.pdf"), new PdfPageNumberOptions("p.{page}", 10));

        Assert.True(res.Success, res.Message);
        var text = new PdfExtractor().ExtractText(P("n.pdf")).Data!;
        Assert.Contains("p.10", text);
        Assert.Contains("p.12", text);
        Assert.DoesNotContain("p.1 ", text);
    }

    [Fact]
    public void PageNumbers_FormatWithoutPlaceholders_RepeatsTheLiteralTextOnEveryPage()
    {
        var res = _stamp.AddPageNumbers(Pdf("a.pdf", 3), P("n.pdf"), new PdfPageNumberOptions("INTERNAL"));

        Assert.True(res.Success, res.Message);
        var text = new PdfExtractor().ExtractText(P("n.pdf")).Data!;
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, "INTERNAL").Count);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 500.0)]
    [InlineData(500.0, 9999.0)]
    public void PageNumbers_ExtremeFontSizeAndMargin_AreClamped_NotRejected(double fontSize, double margin)
    {
        var res = _stamp.AddPageNumbers(Pdf("a.pdf", 1), P("n.pdf"), new PdfPageNumberOptions(FontSize: fontSize, MarginPoints: margin));

        Assert.True(res.Success, res.Message);
    }

    [Fact]
    public void PageNumbers_DoNotChangeThePageCount_OrTheOriginalText()
    {
        var res = _stamp.AddPageNumbers(Pdf("a.pdf", 5), P("n.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(5, PageCount(P("n.pdf")));
        Assert.Contains("Sample Page 5", new PdfExtractor().ExtractText(P("n.pdf")).Data);
    }

    [Fact]
    public void PageNumbers_PasswordPdf_NeedsThePassword()
    {
        var locked = Locked("l.pdf", "pw");

        var without = _stamp.AddPageNumbers(locked, P("n1.pdf"));
        Assert.False(without.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, without.ErrorCode);

        Assert.True(_stamp.AddPageNumbers(locked, P("n2.pdf"), new PdfPageNumberOptions(Password: "pw")).Success);
    }

    [Fact]
    public void PageNumbers_MissingInput_IsFileNotFound()
    {
        var res = _stamp.AddPageNumbers(P("ghost.pdf"), P("n.pdf"));

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.FileNotFound, res.ErrorCode);
    }

    [Fact]
    public void PageNumbers_ExistingOutput_IsNeverOverwritten()
    {
        File.WriteAllText(P("n.pdf"), "precious");

        var res = _stamp.AddPageNumbers(Pdf("a.pdf", 1), P("n.pdf"));

        Assert.True(res.Success, res.Message);
        Assert.Equal("precious", File.ReadAllText(P("n.pdf")));
    }
}
