namespace BFR.Documents.Tests;

using System.Text;
using BFR.Documents.Abstractions;
using BFR.Documents.Pdf;
using Xunit;
using NewIO = PdfSharp.Pdf.IO;

/// <summary>
/// Store/Release notes/Menu Tròn quảng cáo "AES". Các test này đọc BYTE THẬT của tệp đầu ra để chứng minh lời hứa đó
/// (trước đây Protect ghi RC4-128: /V 2 /R 3) và kiểm tra mọi dịch vụ khác vẫn dùng được tệp đã khoá bằng AES.
/// Chọn AES-128 (/V 4, /AESV2): Windows.Data.Pdf (thumbnail/OCR/PDF → ảnh) không mở được AES-256, đo trên Windows 11.
/// </summary>
public sealed class PdfAesProtectionTests : IDisposable
{
    private readonly string _dir;
    private readonly PdfSecurityService _sec = new();

    public PdfAesProtectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bfr_aes_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string Pdf(string name, int pages = 3) => TestDocumentFixtures.CreateSamplePdf(P(name), pages);

    private string Protected(string name, PdfProtectOptions options, int pages = 3)
    {
        var res = _sec.Protect(Pdf("src_" + name, pages), P(name), options);
        Assert.True(res.Success, res.Message);
        return P(name);
    }

    private static string RawText(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    private static string TextOf(string path, string? password = null)
    {
        var r = new PdfExtractor().ExtractText(path, password);
        Assert.True(r.Success, r.Message);
        return r.Data!;
    }

    /// <summary>Tạo tệp AES-256 như Acrobat/Word (/V 5, /AESV3) — bằng chính PDFsharp 6, KHÔNG qua Protect của dự án.</summary>
    private string Aes256(string name, string user, string owner, int pages = 3)
    {
        var src = Pdf("src256_" + name, pages);
        using (var doc = NewIO.PdfReader.Open(src, NewIO.PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.UserPassword = user;
            doc.SecuritySettings.OwnerPassword = owner;
            doc.SecurityHandler.SetEncryptionToV5(true);
            doc.Save(P(name));
        }

        return P(name);
    }

    // ------------------------------------------------------------------ bằng chứng: byte thật là AES

    [Fact]
    public void Protect_WritesAes128_NotRc4()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: "pw"));

        var raw = RawText(locked);
        Assert.Contains("/AESV2", raw);                       // bộ lọc mã hoá AES-128
        Assert.Contains("/CFM", raw);
        Assert.Contains("/V 4", raw);                          // phiên bản 4 (Crypt Filters), không phải V 2 của RC4
        Assert.DoesNotContain("/V 2", raw);
        Assert.DoesNotContain("/R 3", raw);                    // R3 = RC4-128
    }

    [Fact]
    public void Protect_EncryptsTheContent_SoPlainTextIsNotRecoverableWithoutThePassword()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: "pw"));

        var raw = RawText(locked);
        Assert.DoesNotContain("Sample Page", raw);             // nội dung gốc không còn ở dạng đọc được
        Assert.DoesNotContain("Test Author", raw);             // metadata cũng được mã hoá
    }

    [Fact]
    public void Protect_OutputIsUnreadableWithoutThePassword_ByEveryReaderInTheProduct()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: "pw"));

        Assert.Equal(DocumentErrorCode.PasswordRequired, new DocumentInspector().Inspect(locked).ErrorCode);       // PdfPig
        Assert.False(new PdfExtractor().ExtractText(locked).Success);                                                // PdfPig
        Assert.ThrowsAny<Exception>(() => PdfSharpCore.Pdf.IO.PdfReader.Open(locked, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import));
        Assert.ThrowsAny<NewIO.PdfReaderException>(() => NewIO.PdfReader.Open(locked, NewIO.PdfDocumentOpenMode.Import));
    }

    [Fact]
    public void Protect_RoundTrip_UnlockReturnsTheOriginalText()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: "s3cret!"), pages: 4);

        var res = _sec.Unlock(locked, P("u.pdf"), "s3cret!");

        Assert.True(res.Success, res.Message);
        Assert.Equal(4, res.PagesProcessed);
        var text = TextOf(P("u.pdf"));
        for (var i = 1; i <= 4; i++) Assert.Contains($"Sample Page {i}", text);
    }

    // ------------------------------------------------------------------ mật khẩu & quyền

    [Theory]
    [InlineData("mậtkhẩu123")]            // tiếng Việt: người dùng của sản phẩm này
    [InlineData("pässwörd")]
    [InlineData("密码")]
    [InlineData("a b c")]
    [InlineData("quote\"inside")]
    [InlineData("0123456789012345678901234567890123456789")]   // dài hơn 32 byte
    public void Protect_UnusualPasswords_StillUnlockWithTheSameText(string password)
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: password), pages: 2);

        var res = _sec.Unlock(locked, P("u.pdf"), password);

        Assert.True(res.Success, $"mật khẩu '{password}': {res.Message}");
        Assert.Contains("Sample Page 2", TextOf(P("u.pdf")));
    }

    [Fact]
    public void Protect_WrongPassword_IsRejected_WithPasswordRequired()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: "right"));

        var res = _sec.Unlock(locked, P("u.pdf"), "wrong");

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, res.ErrorCode);
        Assert.False(File.Exists(P("u.pdf")));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    public void Protect_PermissionFlags_AreWrittenIntoTheFile(bool print, bool extract, bool modify, bool annotate)
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(
            UserPassword: "user", OwnerPassword: "owner",
            PermitPrint: print, PermitExtractContent: extract, PermitModifyDocument: modify, PermitAnnotations: annotate));

        using var doc = NewIO.PdfReader.Open(locked, "user", NewIO.PdfDocumentOpenMode.Import);

        Assert.Equal(print, doc.SecuritySettings.PermitPrint);
        Assert.Equal(extract, doc.SecuritySettings.PermitExtractContent);
        Assert.Equal(modify, doc.SecuritySettings.PermitModifyDocument);
        Assert.Equal(annotate, doc.SecuritySettings.PermitAnnotations);
    }

    [Fact]
    public void Protect_OnlyAnOwnerPassword_LeavesTheFileOpenForReading_ButStillAes()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(OwnerPassword: "owner", PermitModifyDocument: false));

        Assert.Contains("/AESV2", RawText(locked));
        Assert.Equal(DocumentErrorCode.None, new DocumentInspector().Inspect(locked).ErrorCode);       // không cần mật khẩu để xem
        Assert.Contains("Sample Page 1", TextOf(locked));
    }

    [Fact]
    public void Protect_OnlyAUserPassword_AlsoUsesItAsTheOwnerPassword()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: "same", PermitModifyDocument: true));

        using var doc = NewIO.PdfReader.Open(locked, "same", NewIO.PdfDocumentOpenMode.Modify);   // mở để SỬA được: mật khẩu mở = mật khẩu chủ

        Assert.Equal(3, doc.PageCount);
    }

    // ------------------------------------------------------------------ nâng cấp từ RC4

    private string Rc4(string name, string password, int pages = 3)
    {
        var src = Pdf("rc4src_" + name, pages);
        using (var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(src, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            doc.SecuritySettings.UserPassword = password;
            doc.SecuritySettings.OwnerPassword = password;
            doc.Save(P(name));
        }

        var raw = RawText(P(name));
        Assert.Contains("/R 3", raw);                           // tiền đề: đúng là RC4
        Assert.DoesNotContain("/AESV2", raw);
        return P(name);
    }

    [Fact]
    public void Protect_OnAnExistingRc4File_UpgradesItToAes_AndTheOldPasswordStopsWorking()
    {
        var old = Rc4("old.pdf", "old-pass");

        var res = _sec.Protect(old, P("new.pdf"), new PdfProtectOptions(UserPassword: "new-pass", ExistingPassword: "old-pass"));

        Assert.True(res.Success, res.Message);
        var raw = RawText(P("new.pdf"));
        Assert.Contains("/AESV2", raw);
        Assert.DoesNotContain("/R 3", raw);
        Assert.True(_sec.Unlock(P("new.pdf"), P("ok.pdf"), "new-pass").Success);
        var oldPassword = _sec.Unlock(P("new.pdf"), P("bad.pdf"), "old-pass");
        Assert.False(oldPassword.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, oldPassword.ErrorCode);
    }

    [Fact]
    public void Protect_OwnerOnlyOnAnAlreadyLockedFile_DropsTheOldOpenPassword()
    {
        var old = Rc4("old.pdf", "old-pass");

        var res = _sec.Protect(old, P("new.pdf"), new PdfProtectOptions(OwnerPassword: "owner", ExistingPassword: "old-pass"));

        Assert.True(res.Success, res.Message);
        Assert.Equal(DocumentErrorCode.None, new DocumentInspector().Inspect(P("new.pdf")).ErrorCode);   // không còn đòi mật khẩu để mở
        Assert.Contains("Sample Page 1", TextOf(P("new.pdf")));
    }

    [Fact]
    public void Protect_OnAnAlreadyLockedFile_NeedsTheExistingPassword()
    {
        var old = Rc4("old.pdf", "old-pass");

        var without = _sec.Protect(old, P("n1.pdf"), new PdfProtectOptions(UserPassword: "x"));
        var wrong = _sec.Protect(old, P("n2.pdf"), new PdfProtectOptions(UserPassword: "x", ExistingPassword: "nope"));

        foreach (var res in new[] { without, wrong })
        {
            Assert.False(res.Success);
            Assert.Equal(DocumentErrorCode.PasswordRequired, res.ErrorCode);
        }

        Assert.False(File.Exists(P("n1.pdf")) || File.Exists(P("n2.pdf")));
    }

    [Fact]
    public void Protect_AnAes128FileAgain_WithItsPassword_StillWorks()
    {
        var first = Protected("a.pdf", new PdfProtectOptions(UserPassword: "one"));

        var res = _sec.Protect(first, P("b.pdf"), new PdfProtectOptions(UserPassword: "two", ExistingPassword: "one"));

        Assert.True(res.Success, res.Message);
        Assert.True(_sec.Unlock(P("b.pdf"), P("c.pdf"), "two").Success);
        Assert.Contains("Sample Page 3", TextOf(P("c.pdf")));
    }

    // ------------------------------------------------------------------ các dịch vụ khác dùng tệp AES

    [Fact]
    public void EveryService_CanUseAnAes128File_GivenThePassword_AndKeepsTheText()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: "pw"), pages: 3);
        var org = new PdfOrganizer();

        var merged = org.Merge(new[] { locked }, P("merged.pdf"), new PdfMergeOptions(Passwords: new Dictionary<string, string> { [locked] = "pw" }));
        var split = org.SplitByRanges(locked, new[] { "1-2" }, new PdfSplitOptions(P("parts"), Password: "pw"));
        var extract = org.ExtractPages(locked, "3", P("extract.pdf"), "pw");
        var rotate = org.RotatePages(locked, 90, P("rotate.pdf"), password: "pw");
        var stamp = new PdfStampService().AddWatermark(locked, P("wm.pdf"), new PdfWatermarkOptions("DRAFT", AngleDegrees: 0, Password: "pw"));
        var numbers = new PdfStampService().AddPageNumbers(locked, P("num.pdf"), new PdfPageNumberOptions(Password: "pw"));
        var compress = new PdfCompressor().Compress(locked, P("small.pdf"), new PdfCompressionOptions(Password: "pw"));
        var strip = _sec.StripMetadata(locked, P("clean.pdf"), password: "pw");

        foreach (var (name, res) in new (string, DocumentResult)[] { ("merge", merged), ("split", split), ("extract", extract), ("rotate", rotate), ("watermark", stamp), ("numbers", numbers), ("compress", compress) })
            Assert.True(res.Success, $"{name}: {res.Message}");
        Assert.True(strip.Success, strip.Message);

        Assert.Contains("Sample Page 3", TextOf(P("merged.pdf")));
        Assert.Contains("Sample Page 1", TextOf(split.OutputPaths[0]));
        Assert.Contains("Sample Page 3", TextOf(P("extract.pdf")));
        Assert.Contains("DRAFT", TextOf(P("wm.pdf")));
        Assert.Contains("Page 3 of 3", TextOf(P("num.pdf")));
    }

    [Fact]
    public async Task WindowsRenderer_CanRenderAnAes128File_SoThumbnailsAndOcrKeepWorking()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: "pw"));
        using var cache = new PdfThumbnailCache();

        var png = await cache.GetThumbnailBytesAsync(locked, 1, 200, "pw");

        Assert.NotNull(png);
        Assert.True(png!.Length > 100);
    }

    [Fact]
    public void Inspector_ReportsTheLockedAes128File_AndOpensItWithThePassword()
    {
        var locked = Protected("l.pdf", new PdfProtectOptions(UserPassword: "pw"), pages: 5);
        var inspector = new DocumentInspector();

        Assert.Equal(DocumentErrorCode.PasswordRequired, inspector.Inspect(locked).ErrorCode);
        Assert.Equal(DocumentErrorCode.PasswordRequired, inspector.Inspect(locked, "wrong").ErrorCode);
        var ok = inspector.Inspect(locked, "pw");
        Assert.Equal(DocumentErrorCode.None, ok.ErrorCode);
        Assert.Equal(5, ok.PageCount);
        Assert.True(ok.IsEncrypted);
    }

    // ------------------------------------------------------------------ AES-256 do trình khác tạo (Acrobat, Word...)

    [Fact]
    public void Unlock_AnAes256File_FromAnotherTool_Works_InsteadOfClaimingTheFileIsCorrupt()
    {
        var locked = Aes256("acrobat.pdf", "user256", "owner256", pages: 3);
        Assert.Contains("/AESV3", RawText(locked));          // tiền đề: đúng là AES-256

        var res = _sec.Unlock(locked, P("u.pdf"), "user256");

        Assert.True(res.Success, res.Message);
        Assert.Equal(3, res.PagesProcessed);
        Assert.Contains("Sample Page 3", TextOf(P("u.pdf")));
    }

    [Fact]
    public void Unlock_AnAes256File_AcceptsTheOwnerPasswordToo()
    {
        var locked = Aes256("acrobat.pdf", "user256", "owner256");

        Assert.True(_sec.Unlock(locked, P("u.pdf"), "owner256").Success);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    public void Unlock_AnAes256File_WithAWrongPassword_IsPasswordRequired_NotCorrupted(string password)
    {
        var locked = Aes256("acrobat.pdf", "user256", "owner256");

        var res = _sec.Unlock(locked, P("u.pdf"), password);

        Assert.False(res.Success);
        Assert.Equal(DocumentErrorCode.PasswordRequired, res.ErrorCode);
        Assert.False(File.Exists(P("u.pdf")));
        Assert.Empty(Directory.GetFiles(_dir, "*.bfrtmp"));
    }

    [Fact]
    public void Inspector_UnderstandsAnAes256File()
    {
        var locked = Aes256("acrobat.pdf", "user256", "owner256", pages: 4);
        var inspector = new DocumentInspector();

        Assert.Equal(DocumentErrorCode.PasswordRequired, inspector.Inspect(locked).ErrorCode);
        var ok = inspector.Inspect(locked, "user256");
        Assert.Equal(DocumentErrorCode.None, ok.ErrorCode);
        Assert.Equal(4, ok.PageCount);
    }

    [Fact]
    public void Unlock_ARegularCorruptFile_IsStillReportedAsCorrupt_NotMistakenForAes256()
    {
        File.WriteAllText(P("junk.pdf"), "%PDF-1.7 definitely not a valid pdf");

        var res = _sec.Unlock(P("junk.pdf"), P("u.pdf"), "x");

        Assert.False(res.Success);
        Assert.False(File.Exists(P("u.pdf")));
    }

    [Fact]
    public void Aes256Marker_IsFoundEvenWhenItIsSplitAcrossTheReadBufferBoundary()
    {
        // Bộ đệm đọc 256 KB: đặt từ khoá ngay trên ranh giới để kiểm tra phần "giữ lại đuôi" giữa hai lần đọc.
        const int buffer = 256 * 1024;
        foreach (var offset in new[] { buffer - 6, buffer - 3, buffer - 1, buffer, buffer + 1 })
        {
            var bytes = new byte[buffer * 2];
            Array.Fill(bytes, (byte)'x');
            Encoding.ASCII.GetBytes("/AESV3").CopyTo(bytes, offset);
            File.WriteAllBytes(P("marker.bin"), bytes);

            Assert.True(PdfAesEncryption.ContainsAes256Marker(P("marker.bin")), $"không thấy từ khoá ở vị trí {offset}");
        }

        File.WriteAllBytes(P("none.bin"), new byte[buffer * 2]);
        Assert.False(PdfAesEncryption.ContainsAes256Marker(P("none.bin")));
        Assert.False(PdfAesEncryption.ContainsAes256Marker(P("missing.bin")));
    }
}
