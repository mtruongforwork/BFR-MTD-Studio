# BFR — File & Document Hub — Release Notes v3.0.0

Tài liệu ghi chú phát hành cho **Microsoft Partner Center** & **GitHub Releases**, bản **v3.0.0** (Package Version: `3.0.0.0`, Store Identity: `MTDStudio.BFR-BatchFileRenamer`).

> **Giá và bản quyền Pro trọn đời:** Giữ nguyên Store Identity (`MTDStudio.BFR-BatchFileRenamer`). Toàn bộ người dùng hiện tại nhận bản cập nhật tự động; khách hàng đã mua **Pro trọn đời** từ v1.x/v2.x được mở khóa **100% toàn bộ tính năng PDF, Office Docs, OCR và Tự động hóa của v3.0** không tốn thêm bất kỳ chi phí nào.

---

## What's New in v3.0.0 (English - en-US)

BFR v3.0 transforms Batch File Renamer into **BFR — File & Document Hub**: a 100% offline, all-in-one Windows suite for **PDF, Office Documents (`DOCX`, `XLSX`, `PPTX`, `MD`, `CSV`, `TXT`), Offline OCR, Images, Archives, and Batch Renaming**, centered around a brand-new **Context-Aware 2-Ring Radial Menu**.

### Context-Aware 2-Ring Radial Menu & Actionable Glass Toast
- **4-Tier Active File Detection (`<= 120ms` budget).** Invoke the Radial Menu from anywhere on Windows via global hotkey or **Middle-Mouse Hold (`220ms`, non-blocking)** and BFR automatically detects your active file(s) from File Explorer/Desktop selection (`IShellWindows`), open Office documents (`IRunningObjectTable` with safe snapshot copies), local PDFs in Edge/Chrome/Reader, or BFR Shelf/Clipboard.
- **7 Dynamic Context Profiles.** The 8 primary wedges adapt automatically to what you selected — Single PDF, Multiple PDFs, Office Docs (`DOCX`/`XLSX`/`PPTX`), Images, Text/Markdown/CSV, Mixed Files, or Quick Pick — with smooth **Outer Sub-Arc** presets (`Right-Click`, `Space`, or hover) and `Shift+Click` to open the full Studio.
- **Safe Non-Blocking Middle-Mouse Trigger.** Short clicks (< 220ms), drags (> 8px), and fullscreen apps/games are never interrupted. Configurable in Settings.
- **Actionable Progress & Result Glass Toast.** One-click background execution shows live progress, `[Cancel]`, inline PDF password unlock when needed, Squoosh-style size savings (e.g., `↓ 68% · 14.2 MB → 4.5 MB`), and instant `[Open file]`, `[Folder]`, `[Undo]`, and `[Continue with ▸]` actions.
- **Explorer Context Menu Upgrade.** Added `"Open BFR Radial Menu here"` and quick PDF/Document actions directly in Windows File Explorer.

### PDF Studio (100% Offline)
- **Visual Page Workbench.** Virtualized thumbnail grid (`<= 64 MB` LRU cache) for 1,000+ page PDFs with drag-and-drop page reordering across multiple files, hover micro-actions (`Rotate`, `Preview`, `Delete`), and Smallpdf-style **Scissor Split (`✂️`)** dividers.
- **Organize, Optimize & Convert.** Merge PDFs (including mixed PDF + Images + Markdown), split/extract/delete/rotate pages, compress PDFs (3 quality levels), repair/flatten PDFs, convert **Images ↔ PDF** (`JPG`, `PNG`, `WEBP`, `TIFF`, `HEIC`), and render **Markdown / TXT / CSV → PDF** with full Unicode/Vietnamese/CJK typography.
- **Security & Stamping (Pro).** AES password protection & unlocking, metadata/XMP/unsafe-action sanitization, and customizable diagonal text/image watermarks & page numbering with live preview.

### Docs & OCR Studio (100% Offline)
- **Offline Multi-Language OCR.** Built on Windows built-in `Windows.Media.Ocr` (`en`, `vi`, `fr`, `zh-Hans`) with side-by-side **Split-View** (original scan ↔ editable text), exporting to `.txt`, `.md`, `.docx`, or **Searchable PDF**, with automatic missing language pack detection.
- **Hybrid Office → PDF Conversion.** Converts `DOCX`, `XLSX`, `PPTX` to PDF locally using a sandboxed Windows Job Object worker for LibreOffice/Microsoft Office (zero zombie processes) with a pure-.NET OpenXML fallback.
- **Office Privacy Cleaner & Data Tools.** Inspect and strip hidden authors, comments, tracked revisions, and metadata from `.docx`, `.xlsx`, `.pptx`; extract embedded images; convert `XLSX ↔ CSV/JSON`; and extract structured text/Markdown/DOCX from PDFs.

### Smart Hub, Tool Chaining, Workflow, Watch & CLI
- **Redesigned Main Window & Smart Home Dropzone.** Collapsible sidebar adds **PDF Studio** and **Docs & OCR**; dropping any mix of files on Home suggests tailored one-click actions; **All Tools** adds category filter pills and **★ Favorites**.
- **Continue With (`Tool Chaining`).** Chain outputs seamlessly across tools (e.g., Merge PDF → Compress → Add Page Numbers → Protect → Batch Rename).
- **Workflow, Watch Folder & CLI Extensions.** Automate `CompressPdf`, `ImagesToPdf`, `DocToPdf`, `StripDocMetadata`, and `WatermarkPdf` inside Workflows and Watch Folders, plus new `bfr pdf ...` and `bfr doc ...` CLI commands.
- **Zero Data Loss & Untrusted Document Hardening.** Every generated document is recorded in the Write-Ahead Journal with safe 1-click Undo to Quarantine, atomic `.bfrtmp` commits, magic-byte validation, XXE/Zip-Bomb protection, and 15 clear, actionable edge-case notifications.

---

## Những điểm mới trong v3.0.0 (Tiếng Việt - vi-VN)

BFR v3.0 đánh dấu bước chuyển mình thành **BFR — File & Document Hub**: bộ công cụ **All-in-One 100% Offline** trên Windows cho **PDF, Tài liệu Văn phòng (`DOCX`, `XLSX`, `PPTX`, `MD`, `CSV`, `TXT`), OCR ngoại tuyến, Hình ảnh, Nén/Giải nén và Đổi tên hàng loạt**, xoay quanh vũ khí UX trung tâm là **Menu Tròn Ngữ Cảnh 2 Vòng**.

### Menu Tròn Ngữ Cảnh 2 Vòng & Actionable Glass Toast
- **Bắt ngữ cảnh 4 tầng siêu tốc (`<= 120ms`).** Gọi Menu Tròn từ bất kỳ đâu trên Windows bằng phím tắt toàn cục hoặc **Giữ Chuột Giữa `220ms` (không chặn thao tác)** — BFR tự động nhận diện tệp đang chọn trên File Explorer/Desktop (`IShellWindows`), tài liệu đang mở trong Word/Excel/PowerPoint (`IRunningObjectTable` kèm bản chụp an toàn), PDF cục bộ đang đọc trên Edge/Chrome/Reader, hoặc Shelf/Clipboard.
- **7 Ma trận tự biến hình theo loại tệp.** 8 ô vòng trong tự động thay đổi theo ngữ cảnh (1 PDF, Nhiều PDF, Office, Ảnh, Text/MD/CSV, Hỗn hợp, hoặc Quick Pick) kèm **Vòng cung phụ (Outer Sub-Arc)** trượt mượt và `Shift+Click` để mở thẳng bàn làm việc chi tiết.
- **Giữ chuột giữa an toàn tuyệt đối.** Không làm mất click ngắn đóng tab/mở link (< 220ms), tự hủy khi rê chuột (> 8px) hoặc khi chạy ứng dụng/game toàn màn hình; bật/tắt linh hoạt trong Cài đặt.
- **Glass Toast Tiến trình & Hành động trực tiếp.** Chạy ngầm không làm gián đoạn công việc, hiển thị tiến trình, nút `[Hủy]`, nhập mật khẩu PDF inline khi cần, chỉ số giảm dung lượng trực quan (`↓ 68% · 14.2 MB → 4.5 MB`) và dải nút `[Mở file]`, `[Thư mục]`, `[Hoàn tác]`, `[Làm tiếp ▸]`.
- **Nâng cấp Menu chuột phải Explorer.** Bổ sung mục `"Mở Menu Tròn BFR tại đây"` và các lệnh nhanh cho PDF/Tài liệu.

### PDF Studio (100% Offline)
- **Bàn làm việc trang PDF trực quan.** Lưới thumbnail ảo hóa (giới hạn bộ nhớ LRU `<= 64 MB`) mở mượt tài liệu 1.000+ trang, kéo-thả sắp xếp trang xuyên nhiều tệp, nút nhanh khi rê chuột (`Xoay`, `Phóng to`, `Xóa`) và công cụ **Kéo cắt `✂️`** tách tệp trực quan giữa các trang.
- **Tổ chức, Tối ưu & Chuyển đổi.** Gộp PDF (hỗ trợ trộn cả PDF + Ảnh + Markdown), tách/trích/xóa/xoay trang, nén PDF 3 cấp độ, sửa/làm phẳng PDF, chuyển đổi **Ảnh ↔ PDF** (`JPG`, `PNG`, `WEBP`, `TIFF`, `HEIC`), xuất **Markdown / TXT / CSV → PDF** đẹp chuẩn font Tiếng Việt/CJK.
- **Bảo mật & Đóng dấu (Pro).** Đặt/gỡ mật khẩu AES, xóa sạch Metadata/XMP/script nguy hiểm, chèn Watermark chữ/ảnh mờ và đánh số trang với bản xem trước trực tiếp.

### Docs & OCR Studio (100% Offline)
- **OCR ngoại tuyến đa ngôn ngữ.** Dùng `Windows.Media.Ocr` tích hợp sẵn (`vi`, `en`, `fr`, `zh-Hans`), bàn soi song song **Ảnh gốc ↔ Văn bản nhận diện** cho phép sửa nhanh và xuất `.txt`, `.md`, `.docx` hoặc **PDF tra cứu được**, tự phát hiện thiếu gói ngôn ngữ Windows.
- **Chuyển đổi Hybrid Office → PDF.** Chuyển `DOCX`, `XLSX`, `PPTX` sang PDF cục bộ qua worker cô lập trong Windows Job Object (hỗ trợ LibreOffice/Microsoft Office, không để lại tiến trình treo) kèm bộ dựng thuần .NET dự phòng.
- **Dọn sạch dữ liệu ẩn Office & Công cụ dữ liệu.** Quét và xóa tên tác giả, bình luận ẩn, vết sửa đổi (Tracked Revisions) trong `.docx/.xlsx/.pptx`; trích xuất toàn bộ ảnh nhúng; chuyển đổi `XLSX ↔ CSV/JSON`; trích xuất văn bản/Markdown/DOCX từ PDF.

### Smart Hub, Nối tiếp Công cụ, Workflow, Watch & CLI
- **Cửa sổ chính nâng cấp & Vùng thả thông minh.** Thanh bên bổ sung **PDF Studio** và **Docs & OCR**; thả bất kỳ lô tệp nào vào Trang chủ để nhận gợi ý hành động 1 chạm; **All Tools** thêm thẻ lọc nhóm và **★ Yêu thích**.
- **Thanh Làm tiếp (`Continue With`).** Nối kết quả liên hoàn qua nhiều công cụ (VD: Gộp PDF → Nén → Đánh số trang → Đặt mật khẩu → Đổi tên hàng loạt).
- **Mở rộng Workflow, Watch Folder & CLI.** Thêm bước `CompressPdf`, `ImagesToPdf`, `DocToPdf`, `StripDocMetadata`, `WatermarkPdf` vào Workflow/Watch Folder và bổ sung nhóm lệnh CLI `bfr pdf ...`, `bfr doc ...`.
- **An toàn dữ liệu tuyệt đối & Chống tệp độc hại.** Mọi tệp mới tạo đều ghi vào Nhật ký (Journal) hỗ trợ Hoàn tác 1 chạm về Khu cách ly, ghi nguyên tử qua `.bfrtmp`, kiểm tra Magic Bytes, chặn XXE/Zip Bomb và hệ thống 15 thông báo ngoại lệ kèm nút xử lý tại chỗ.
