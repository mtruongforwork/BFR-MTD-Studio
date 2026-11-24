# Thông báo bên thứ ba

## Phông chữ nhúng (giao diện mới, v2.0)

| Phông | Tác giả | Giấy phép | Tệp giấy phép |
|---|---|---|---|
| Inter 4.0 (Regular, Medium, SemiBold) | The Inter Project Authors | SIL Open Font License 1.1 | `src/BFR.App/Resources/Fonts/OFL-Inter.txt` |
| Plus Jakarta Sans (SemiBold, Bold, ExtraBold) | Tokotype | SIL Open Font License 1.1 | `src/BFR.App/Resources/Fonts/OFL-PlusJakartaSans.txt` |

Phông được nhúng nguyên vẹn (không sửa đổi) và đóng gói cùng ứng dụng; tệp giấy phép nằm cạnh tệp phông và được chép vào thư mục đầu ra.

## Thư viện .NET cho PDF và tài liệu (v3.0, `BFR.Documents`)

Tất cả dùng giấy phép cho phép phân phối thương mại (MIT / BSD-2-Clause / Apache-2.0). Không dùng thư viện AGPL hoặc giấy phép thương mại.

| Thư viện | Phiên bản | Tác giả / bản quyền | Giấy phép | Dùng để |
|---|---|---|---|---|
| PDFsharp | 6.2.4 | © empira — PDFsharp Team | MIT | Ghi và đọc mã hoá AES của PDF (khoá/mở khoá) |
| PdfSharpCore | 1.3.65 | © 2005-2007 empira Software GmbH; © 2016 David Dunscombe; Stefan Steiger và cộng sự | MIT | Gộp, tách, xoay, nén, đóng dấu PDF |
| PdfPig | 0.1.9 | UglyToad | Apache-2.0 | Trích văn bản, bố cục và ảnh từ PDF |
| DocumentFormat.OpenXml | 3.1.1 | © Microsoft Corporation | MIT | Đọc/ghi DOCX, XLSX, PPTX |
| ClosedXML | 0.104.2 | Jan Havlíček, Francois Botha, Aleksei Pankratev, Manuel de Leon, Amir Ghezelbash | MIT | Chuyển đổi XLSX ↔ CSV/JSON |
| Markdig | 0.38.0 | Alexandre Mutel | BSD-2-Clause | Dựng Markdown thành PDF |

Lưu ý khi nâng cấp: **PDFsharp từ 6.5.0 đổi sang AGPL-3.0** — không nâng lên 6.5 trở đi khi chưa có quyết định về giấy phép. Bản 6.2.x (MIT) được ghim cố định trong `BFR.Documents.csproj`.

Mã hoá PDF của BFR là **AES-128** (PDF 1.6, bộ lọc `/AESV2`). AES-256 chưa được dùng để ghi vì `Windows.Data.Pdf` (bộ dựng thumbnail/OCR/PDF → ảnh của Windows) không mở được tệp AES-256; BFR vẫn mở khoá được tệp AES-256 do trình khác tạo.
