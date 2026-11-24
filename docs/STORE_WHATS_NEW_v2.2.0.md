# What's new v2.2.0 (<= 1500 ký tự mỗi ngôn ngữ)

## en-US

BFR 2.2: power features, sharper UI, and major security & stability upgrades.

NEW FEATURES
- Inline manual rename (F2 or double-click New name) + export/import filenames via Clipboard or CSV
- Clickable Token Picker bar in Template & Organize, plus new tokens: {width}, {height}, {camera}, {year}, {parent:1..4}, {md5:n}, {sha256:n}
- Command Palette v2 (Ctrl+K): search Pages, all 10 Tools, Presets, Add Rule actions, with Vietnamese/Chinese IME and mouse click support
- History Undo Preview: local & relative timestamps, preview restored filenames before confirming Undo
- Interactive Treemap in Analyze a folder: click blocks to filter and right-click to reveal or send to Rename
- Paste files from Explorer (Ctrl+V) and frosted-glass drag-and-drop overlay

UI & PERFORMANCE
- Click KPI tiles (Will change / Unchanged / Problems) to filter; search bar moved above table; fine-grained LCS diff and accurate status icons
- Animated collapsible sidebar, smooth page transitions, non-blocking Apply spinner, reactive Tem mascot, live language switching without restart, and live Home widgets

STABILITY & SECURITY
- Fixed UI deadlocks, preview race conditions, WIC/handle leaks, and .NET 8 Store interop
- DPAPI-protected local license storage (auto-migrated), symlink/junction protection, CSV formula safety, and hardened ZIP/update checks

Pro price and features are unchanged.

## vi-VN

BFR 2.2: nhiều tính năng mới mạnh mẽ, giao diện sắc nét hơn, nâng cấp lớn về ổn định và bảo mật.

TÍNH NĂNG MỚI
- Sửa tên thủ công từng dòng (F2 hoặc nhấp đúp ô Tên mới) + xuất/nhập danh sách tên qua Clipboard/CSV
- Thanh chọn Token bấm trực tiếp trong Mẫu tên & Sắp xếp, bổ sung {width}, {height}, {camera}, {year}, {parent:1..4}, {md5:n}, {sha256:n}
- Command Palette v2 (Ctrl+K): tìm nhanh Trang, 10 Công cụ, Preset, Thêm quy tắc; hỗ trợ gõ tiếng Việt/Trung và bấm chuột
- Xem trước khi Hoàn tác trong Lịch sử: giờ địa phương & mốc tương đối, xem trước tên khôi phục và xác nhận trước khi hoàn tác
- Treemap tương tác trong Phân tích thư mục: bấm vào ô để lọc, chuột phải để mở Explorer hoặc gửi sang Đổi tên
- Dán tệp từ Explorer (Ctrl+V) và lớp phủ kéo-thả kính mờ toàn cửa sổ

GIAO DIỆN & HIỆU NĂNG
- Bấm trực tiếp 3 ô KPI (Sẽ đổi / Không đổi / Có vấn đề) để lọc; thanh tìm kiếm đưa lên trên bảng; Diff LCS chi tiết và icon trạng thái chuẩn
- Thanh bên thu gọn có hoạt ảnh, chuyển trang mượt, nút Áp dụng xoay tiến trình không chặn màn hình, linh vật Tem phản ứng trực tiếp, đổi ngôn ngữ không cần khởi động lại

ỔN ĐỊNH & BẢO MẬT
- Khắc phục deadlock UI, lỗi đồng thời xem trước, rò rỉ WIC/handle và tương thích Store .NET 8
- Bảo vệ license cục bộ bằng DPAPI (tự động chuyển đổi), chặn vượt rào symlink/junction, chống CSV Formula Injection và gia cố giải nén ZIP/cập nhật

Giá và tính năng Pro không đổi.
