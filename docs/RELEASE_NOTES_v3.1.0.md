# FileFlow Studio (BFR) — Release Notes v3.1.0

Phiên bản gói MSIX: `3.1.0.0`

## Nổi bật trong v3.1.0

### Giao diện & Trải nghiệm mới
- **Đổi thương hiệu hiển thị sang FileFlow Studio** và thiết kế lại **Trang chủ Command-First Hub** (tìm nhanh công cụ theo từ khoá, Dropzone thông minh gọn hơn, lưới thẻ công cụ phân nhóm rõ).
- **Nút tròn nổi Desktop (Floating Orb)**: kiểu AssistiveTouch luôn hiện trên Desktop và mọi ứng dụng, kéo thả di chuyển & tự bám mép màn hình, bấm hoặc kéo-thả tệp vào để bung **Menu tròn 8 chức năng**; bật/tắt nhanh trong Tray Icon và Cài đặt.
- **Refactor toàn diện PDF Studio & Docs & OCR Studio**:
  - **Tổ chức & Cắt ghép PDF**: tách rõ chế độ **Trích xuất trang** vs **Tách nhiều file**, chọn nhiều trang với **Floating Bulk Action Bar** (Xoay / Xoá / Trang lẻ-chẵn).
  - **Nén PDF thông minh**: đo DPI thực tế ảnh nhúng trước khi gợi ý các mức nén tối ưu kèm ước tính dung lượng; bảo toàn ảnh CMYK & SMask.
  - **Đóng dấu & Số trang**: 2 thẻ bật/tắt độc lập (chữ/logo ảnh, màu, cỡ chữ, lặp lưới Tile, 6 vị trí số trang) + **Live Preview** đồng bộ thật trên thumbnail.
  - **Bảo mật PDF**: ẩn/hiện động theo 3 chế độ (Đặt mật khẩu AES-128 + phân quyền In/Copy/Sửa, Mở khoá, Xoá Metadata & Script) + nút hiện/ẩn mật khẩu.
  - **Chuyển đổi PDF**: 2 nhóm thẻ trực quan (Ảnh PNG/JPG + DPI động, Tài liệu Word/Markdown/TXT).
  - **Docs & OCR**: tự động quét metadata khi thả tệp Office, tự chọn hướng chuyển bảng tính XLSX ↔ CSV/JSON, OCR có đếm từ/ký tự và xuất `.docx` / Searchable PDF chuẩn cho ảnh.
  - **Processing Card** (% thực + nút Huỷ), **Result Summary Card** (so sánh Trước → Sau, Nạp lên bàn), **Alert Banner 3 cấp độ** tiếng Việt và **Zoom Lightbox** có thanh công cụ.

### Sửa lỗi & Ổn định quan trọng
- Sửa lỗi kẹt màn phủ `DropOverlay` khi kéo-thả tệp ở các trang ngoài Đổi tên.
- Sửa lỗi đổi theme Sáng ↔ Tối / đổi màu nhấn làm lệch màu các trang; giữ nguyên bo tròn của Nút tròn nổi / Menu tròn / Toast khi bật Giảm hiệu ứng.
- Sửa lỗi Menu tròn "Xoá GPS" (`metadata.strip-gps`) nay xoá EXIF GPS lossless thật kèm Hoàn tác.
- Sửa lỗi `RenamePlanner` vòng hoán đổi (`a ↔ b`), `WatchFolder` vượt `MaxFilesPerBatch`, `TrayIcon` tự phục hồi sau khi Explorer khởi động lại, và `LicenseStore` dự phòng `.bak`.
- Cập nhật bảng so sánh Free vs Pro đầy đủ các tính năng PDF/Docs/OCR v3 và gắn badge `PRO` rõ ràng.
