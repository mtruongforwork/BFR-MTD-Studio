namespace BFR.Documents.Pdf;

using System.Text;
using NewIO = PdfSharp.Pdf.IO;
using NewPdf = PdfSharp.Pdf;

/// <summary>
/// Mã hoá AES cho PDF bằng PDFsharp 6.2.x (MIT). PdfSharpCore (thư viện chính của dự án) chỉ GHI được RC4 nên không dùng để khoá tệp.
///
/// Chọn AES-128 (PDF 1.6, /V 4 /R 4, bộ lọc /AESV2) thay vì AES-256 (/V 5, /AESV3):
/// mọi trình đọc đều mở được và, quan trọng hơn, <c>Windows.Data.Pdf</c> (thumbnail, OCR, PDF → ảnh của PDF Studio) KHÔNG mở được AES-256
/// (COMException) — đo thực tế trên Windows 11. PdfSharpCore đọc đúng AES-128 nên các dịch vụ gộp/tách/watermark... giữ nguyên.
///
/// Cảnh báo giấy phép: PDFsharp từ 6.5.0 đổi sang AGPL-3.0. Giữ phiên bản 6.2.x (MIT) cho tới khi có quyết định khác.
/// </summary>
internal static class PdfAesEncryption
{
    /// <summary>Khoá PDF bằng AES-128 và ghi ra <paramref name="outputPath"/>. Trả về số trang.</summary>
    public static int Protect(string readablePath, string outputPath, PdfProtectOptions options)
    {
        using var doc = string.IsNullOrEmpty(options.ExistingPassword)
            ? NewIO.PdfReader.Open(readablePath, NewIO.PdfDocumentOpenMode.Modify)
            : NewIO.PdfReader.Open(readablePath, options.ExistingPassword, NewIO.PdfDocumentOpenMode.Modify);

        var sec = doc.SecuritySettings;

        // Luôn gán tường minh cả hai mật khẩu: nếu nguồn đã được khoá, cài đặt cũ không được "rò" sang kết quả
        // (ví dụ chỉ đặt mật khẩu chủ thì mật khẩu mở của tệp nguồn phải biến mất).
        sec.UserPassword = options.UserPassword ?? string.Empty;
        sec.OwnerPassword = string.IsNullOrEmpty(options.OwnerPassword) ? options.UserPassword ?? string.Empty : options.OwnerPassword;

        sec.PermitPrint = options.PermitPrint;
        sec.PermitFullQualityPrint = options.PermitPrint;
        sec.PermitExtractContent = options.PermitExtractContent;
        sec.PermitModifyDocument = options.PermitModifyDocument;
        sec.PermitAnnotations = options.PermitAnnotations;
        sec.PermitAssembleDocument = options.PermitModifyDocument;
        sec.PermitFormsFill = options.PermitAnnotations;

        doc.SecurityHandler.SetEncryptionToV4UsingAES(encryptMetadata: true);
        var pages = doc.PageCount;
        doc.Save(outputPath);
        return pages;
    }

    /// <summary>
    /// Mở tệp (RC4, AES-128 hoặc AES-256) bằng PDFsharp 6 và chép các trang sang tệp mới KHÔNG mã hoá. Dùng cho tệp AES-256 do trình khác tạo
    /// (Acrobat...) mà PdfSharpCore không đọc được. Trả về số trang.
    /// </summary>
    public static int UnlockToFile(string readablePath, string password, string outputPath, CancellationToken ct)
    {
        using var src = NewIO.PdfReader.Open(readablePath, password, NewIO.PdfDocumentOpenMode.Import);
        using var dst = new NewPdf.PdfDocument();
        for (var i = 0; i < src.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            dst.AddPage(src.Pages[i]);
        }

        var pages = dst.PageCount;
        dst.Save(outputPath);
        return pages;
    }

    /// <summary>
    /// True nếu tệp khai báo bộ lọc AES-256 (<c>/AESV3</c>). Từ khoá nằm trong từ điển /Encrypt (luôn là văn bản thường, không nằm trong luồng nén).
    /// Chỉ gọi khi việc mở bằng PdfSharpCore đã thất bại, nên chi phí đọc cả tệp không ảnh hưởng đường thường.
    /// </summary>
    public static bool ContainsAes256Marker(string path)
    {
        var needle = Encoding.ASCII.GetBytes("/AESV3");
        var buffer = new byte[256 * 1024 + needle.Length];
        var carry = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
            int read;
            while ((read = fs.Read(buffer, carry, buffer.Length - carry)) > 0)
            {
                var total = carry + read;
                if (buffer.AsSpan(0, total).IndexOf(needle) >= 0) return true;

                // Giữ lại phần cuối để khớp từ khoá bị cắt đôi giữa hai lần đọc.
                carry = Math.Min(needle.Length - 1, total);
                Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
            }
        }
        catch (IOException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Lỗi sai/thiếu mật khẩu của PDFsharp 6, theo cùng quy ước chữ như phần còn lại của dự án.</summary>
    public static bool IsPasswordError(Exception ex) => PdfOrganizer.IsPasswordError(ex);
}
