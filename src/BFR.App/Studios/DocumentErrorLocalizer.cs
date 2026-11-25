namespace BFR.App.Studios;

using BFR.Documents.Abstractions;

/// <summary>
/// Maps <see cref="DocumentErrorCode"/> to clear, actionable Vietnamese/English messages
/// so Studio alert banners never expose raw English-only or cryptic internal errors to Vietnamese users.
/// </summary>
internal static class DocumentErrorLocalizer
{
    public static string Format(DocumentErrorCode code, string? fallbackDetail, bool isVi)
    {
        var friendly = code switch
        {
            DocumentErrorCode.PasswordRequired => isVi
                ? "Tệp PDF này được bảo vệ bằng mật khẩu. Vui lòng nhập mật khẩu để mở khoá trước khi xử lý."
                : "This PDF is password-protected. Please enter the password to unlock it first.",

            DocumentErrorCode.OwnerPasswordRequired => isVi
                ? "Tệp PDF bị giới hạn quyền chỉnh sửa (Owner password). Hãy mở khoá bằng mật khẩu chủ sở hữu."
                : "This PDF restricts modification with an owner password. Please unlock it first.",

            DocumentErrorCode.ScannedPdfNoText => isVi
                ? "PDF này là ảnh scan và không có lớp văn bản. Hãy chuyển sang Bàn soi OCR để nhận diện chữ trước."
                : "This PDF is a scanned image with no selectable text layer. Run Offline OCR first.",

            DocumentErrorCode.FileLockedByExternalApp => isVi
                ? "Tệp đang bị khoá độc quyền bởi ứng dụng khác (Word, Excel, Acrobat…). Hãy đóng tệp đó rồi thử lại."
                : "The file is exclusively locked by another app (Word, Excel, Acrobat…). Close it and try again.",

            DocumentErrorCode.CorruptedPdf => isVi
                ? "Cấu trúc PDF bị lỗi, chưa tải hoàn tất, hoặc dùng mã hoá không tương thích. Bạn có thể thử nút Khôi phục trang hoặc Mở khoá."
                : "The PDF structure is damaged, incomplete, or uses unsupported encryption. Try Repair or Unlock.",

            DocumentErrorCode.LargeDocumentWarning => isVi
                ? "Tài liệu có dung lượng lớn — đang bật chế độ ảo hoá để tiết kiệm RAM."
                : "Large document detected — virtualization mode is active to save RAM.",

            DocumentErrorCode.MissingOcrLanguagePack => isVi
                ? "Gói ngôn ngữ Windows OCR bạn chọn chưa được cài đặt trên máy. Hãy mở Cài đặt Ngôn ngữ Windows để tải về."
                : "The selected Windows OCR language pack is not installed. Open Windows Language Settings to install it.",

            DocumentErrorCode.OfficeBridgeUnavailable => isVi
                ? "Không thể dùng LibreOffice cục bộ; ứng dụng sẽ chuyển sang bộ dựng .NET offline."
                : "Local LibreOffice bridge is unavailable; falling back to the built-in offline .NET renderer.",

            DocumentErrorCode.EncryptedOfficeDocument => isVi
                ? "Tài liệu Office này đang được đặt mật khẩu mã hoá. Hãy gỡ mật khẩu trong Word/Excel/PowerPoint trước khi xử lý."
                : "This Office document is password-encrypted. Remove the password in Office before processing.",

            DocumentErrorCode.ExtensionSpoofed => isVi
                ? "Định dạng thực tế của tệp không khớp với phần mở rộng (đuôi tệp bị đổi sai). Vui lòng kiểm tra lại tệp nguồn."
                : "The file's actual content does not match its file extension. Please verify the source file.",

            DocumentErrorCode.DiskFull => isVi
                ? "Ổ đĩa đích không còn đủ dung lượng trống để lưu tệp đầu ra. Hãy giải phóng dung lượng rồi thử lại."
                : "Insufficient free disk space on the target drive. Free up space and try again.",

            DocumentErrorCode.Cancelled => isVi
                ? "Tác vụ đã được huỷ an toàn. Mọi tệp tạm đã được dọn sạch."
                : "Operation was safely cancelled. All temporary files have been cleaned up.",

            DocumentErrorCode.OutputConflict => isVi
                ? "Tệp đầu ra đã tồn tại và chế độ ghi đè đang tắt. Hãy chọn tên tệp khác."
                : "Output file already exists and overwrite is disabled. Please choose another file name.",

            DocumentErrorCode.ProtectedPath => isVi
                ? "Đường dẫn đích nằm trong thư mục hệ thống được bảo vệ (Windows/Program Files). Hãy chọn thư mục khác như Documents hoặc Desktop."
                : "Target path is inside a protected system directory. Please choose another folder such as Documents or Desktop.",

            DocumentErrorCode.FreeTierLimitReached => isVi
                ? "Đã đạt giới hạn của bản miễn phí. Nâng cấp lên Pro để xử lý không giới hạn."
                : "Free tier limit reached. Upgrade to Pro for unlimited processing.",

            DocumentErrorCode.FileNotFound => isVi
                ? "Không tìm thấy tệp nguồn. Có thể tệp đã bị di chuyển hoặc xoá."
                : "Source file was not found. It may have been moved or deleted.",

            DocumentErrorCode.UnsupportedFormat => isVi
                ? "Định dạng tệp này không được hỗ trợ cho tác vụ hiện tại."
                : "This file format is not supported for the selected operation.",

            DocumentErrorCode.SecurityViolation => isVi
                ? "Tệp bị chặn bởi lớp bảo vệ an toàn (nghi ngờ Zip Bomb, XML không an toàn hoặc đường dẫn độc hại)."
                : "File blocked by safety guard (suspected Zip Bomb, unsafe XML/DTD, or invalid archive path).",

            _ => null
        };

        if (!string.IsNullOrWhiteSpace(friendly))
            return friendly;

        if (!string.IsNullOrWhiteSpace(fallbackDetail))
            return fallbackDetail;

        return isVi ? "Đã xảy ra lỗi không mong muốn trong quá trình xử lý." : "An unexpected error occurred during processing.";
    }
}
