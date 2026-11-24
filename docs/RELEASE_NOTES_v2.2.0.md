# BFR - Batch File Renamer — Release Notes v2.2.0

Tài liệu ghi chú phát hành cho **Microsoft Partner Center** & **GitHub Releases**, bản **v2.2.0** (Package Version: `2.2.0.0`).

> **Giá và Pro:** Không đổi so với v2.1. Người đã mua Pro giữ nguyên toàn bộ quyền lợi, không phải trả thêm.

---

## What's New in this Version (English - en-US)

### New Features
- **Inline Manual Rename (`F2` / Double-Click) + CSV/Clipboard Round-Trip.** Edit any individual file's new name right inside the preview table with a live `"Manual"` badge and one-click reset. Export the current filename list to Clipboard or CSV for bulk editing in Excel, then paste or import the updated names back with full conflict and validation checks.
- **Interactive Token Picker & Extended Metadata Tokens.** Click grouped token chips (`Basic`, `Date`, `Media`, `Hash`) directly inside the `Template` rule and `Organize` tool to insert tokens at the cursor. Added `{width}`, `{height}`, `{camera}` (EXIF), `{year}` (audio tags), `{parent:1..4}` (ancestor folders), and lazy-cached `{md5:n}` / `{sha256:n}` hash tokens.
- **Command Palette v2 (`Ctrl+K`).** Now dynamically indexes all Pages, all 10 Tools, all Presets, all Add-Rule actions, and core commands with category badges, keyboard shortcut keycaps, full Vietnamese/Chinese IME support, mouse click execution, and click-outside dismissal.
- **History Undo Preview & Confirmation.** Timestamps now show local time with relative labels (e.g., `5 min ago · 14:32`). Inspect the exact `Current → Restored` filename list before confirming an undo.
- **Interactive Treemap Drill-Down & Context Menu.** Click any block in `Analyze a folder` to filter the file list by category or folder (with a clear-filter chip), or right-click to reveal in Explorer, copy the path, or send directly to Rename.
- **Paste Files from Explorer (`Ctrl+V`) & Window Drag Overlay.** Copy files in Windows Explorer and press `Ctrl+V` to add them straight into the Rename list, or drag files over the window for a frosted-glass drop overlay.

### UI Polish & Micro-interactions
- **Interactive KPI Filter Tiles & Cleaner Preview Layout.** Click `Will change`, `Unchanged`, or `Problems` KPI tiles to filter the preview table directly. Search and filter controls now sit above the table, `RULE DETAILS` auto-collapses when no row is selected, and counts use tabular numerals.
- **Fine-Grained LCS Diff & Accurate Status Icons.** Highlight exact inserted/removed character runs in the preview table and show distinct icons for warnings, errors, locks, and unchanged files.
- **Rule Card Upgrades.** Click a rule header to expand/collapse, use `Clone rule` or `Reset rule` from the `⋯` menu, and enjoy visual drag-and-drop insertion indicators when reordering rules.
- **Smooth Motion & Live Reload.** Animated collapsible sidebar (`72px ↔ 220px`), slide+fade page transitions, non-blocking Apply button spinner morph, reactive `Tem` mascot moods, live language switching without restart, and instant live-updated `Home` Bento cards for Watch Folders and History. All animations respect **Reduce visual effects** and **High Contrast**.

### Stability & Security Hardening
- **Zero-Deadlock Async Commands & Thread-Safe Preview.** Eliminated UI-thread deadlocks in `AsyncRelayCommand` and concurrent mutation races in `MainViewModel.RebuildAsync`; moved `Undo` and `WatchFolder` seeding off the UI thread.
- **Memory & Handle Leak Fixes.** Replaced MIL/WIC `RenderTargetBitmap` in `WicImageProcessor` with free-threaded CPU alpha-compositing, fixed shutdown/handle leaks in `App.OnExit`, `MainWindow`, `ShelfWindow`, `MouseHook`, `TrayIcon`, `UiWatchdog`, and `BFR.Shell`, and updated `BFR.Store` to use .NET 8 `WinRT.Interop.InitializeWithWindow`.
- **Security Hardening.** Protected local `license.json` with Windows DPAPI (transparent automatic migration from v2.1), hardened offline key verification with asymmetric ECDSA P-256 signatures, blocked `\\?\` and NTFS junction/symlink bypasses of `ProtectedZones`, neutralized CSV formula injection, hardened ZIP extraction and file list size caps, and validated update feed redirects.

---

## Những điểm mới trong phiên bản này (Tiếng Việt - vi-VN)

### Tính năng mới
- **Sửa tên thủ công từng dòng (`F2` / nhấp đúp) & Xuất/Nhập Clipboard/CSV.** Nhấn `F2` hoặc nhấp đúp vào ô `Tên mới` trong bảng xem trước để chỉnh nhanh từng tệp (kèm huy hiệu `"Thủ công"` và tùy chọn đặt lại). Hỗ trợ xuất danh sách tên ra Clipboard/CSV để sửa hàng loạt trong Excel rồi dán/nhập ngược lại với đầy đủ kiểm tra trùng tên và hợp lệ.
- **Thanh chọn Token bấm trực tiếp & Mở rộng Token.** Nhấp các chip token theo nhóm (`Cơ bản`, `Ngày giờ`, `Đa phương tiện`, `Mã băm`) ngay trong quy tắc `Mẫu tên` và công cụ `Sắp xếp` để chèn đúng vị trí con trỏ. Bổ sung `{width}`, `{height}`, `{camera}` (EXIF), `{year}` (nhạc), `{parent:1..4}` (thư mục cha cấp N), và `{md5:n}` / `{sha256:n}` (tính lười và lưu đệm, giữ nguyên tốc độ 100.000 tệp).
- **Command Palette v2 (`Ctrl+K`).** Nạp động toàn bộ Trang, 10 Công cụ, Preset, hành động Thêm quy tắc và lệnh chính; hỗ trợ gõ tiếng Việt/Trung (IME), bấm chuột, tự đóng khi bấm ra ngoài, huy hiệu nhóm lệnh và phím tắt dạng keycap.
- **Xem trước khi Hoàn tác trong Lịch sử.** Thời gian hiển thị theo giờ địa phương kèm mốc tương đối (`5 phút trước · 14:32`), cho phép mở xem trước danh sách `Tên hiện tại → Tên khôi phục` và xác nhận trước khi hoàn tác.
- **Treemap tương tác & Menu chuột phải.** Bấm trực tiếp vào ô trên biểu đồ `Phân tích thư mục` để lọc danh sách tệp (kèm chip xóa lọc), chuột phải để mở trong Explorer, sao chép đường dẫn hoặc gửi sang Đổi tên.
- **Dán tệp từ Explorer (`Ctrl+V`) & Lớp phủ kéo-thả toàn cửa sổ.** Sao chép tệp trong File Explorer rồi nhấn `Ctrl+V` để thêm ngay vào danh sách Đổi tên, hoặc kéo tệp vào cửa sổ để thấy lớp phủ kính mờ trực quan.

### Tinh chỉnh giao diện & Trải nghiệm
- **Thẻ KPI bấm lọc trực tiếp & Bố cục xem trước gọn gàng.** Bấm thẳng vào 3 ô `Sẽ đổi`, `Không đổi`, `Có vấn đề` để lọc bảng xem trước; thanh tìm kiếm và bộ lọc được đưa lên trên bảng; ngăn `CHI TIẾT QUY TẮC` tự ẩn khi chưa chọn dòng; số đếm dùng chữ số thẳng hàng (tabular).
- **So sánh Diff chi tiết theo ký tự (LCS) & Biểu tượng trạng thái chuẩn xác.** Tô sáng chính xác từng đoạn thêm/xóa và hiển thị đúng biểu tượng cho cảnh báo, lỗi, tệp bị khóa và không đổi.
- **Nâng cấp thẻ quy tắc.** Bấm vào tiêu đề thẻ để mở rộng/thu gọn, thêm `Nhân bản quy tắc` và `Đặt lại quy tắc` trong menu `⋯`, bổ sung hiệu ứng mờ thẻ và vạch chèn sáng khi kéo-thả đổi thứ tự.
- **Chuyển động mượt mà & Cập nhật tức thì.** Thanh bên thu gọn có hoạt ảnh (`72px ↔ 220px`), hiệu ứng trượt+hiện khi chuyển trang, nút `Áp dụng` chuyển sang trạng thái xoay tiến trình không chặn màn hình, linh vật `Tem` phản ứng theo tiến độ đổi tên, đổi ngôn ngữ áp dụng ngay không cần khởi động lại, và trang chủ hiển thị trạng thái sống của `Theo dõi thư mục` & `Lịch sử`. Toàn bộ hiệu ứng tôn trọng `Giảm hiệu ứng` và `Tương phản cao`.

### Ổn định & Tăng cường bảo mật
- **Loại bỏ deadlock & lỗi đồng thời.** Khắc phục triệt để deadlock UI trong `AsyncRelayCommand` và race condition trong `MainViewModel.RebuildAsync`; chuyển `Hoàn tác` và quét khởi tạo `Watch Folder` sang luồng nền.
- **Khắc phục rò rỉ bộ nhớ & handle.** Thay `RenderTargetBitmap` trong `WicImageProcessor` bằng xử lý alpha thuần CPU không giữ tài nguyên MIL, vá rò rỉ khi thoát ở `App.OnExit`, `MainWindow`, `ShelfWindow`, `MouseHook`, `TrayIcon`, `UiWatchdog`, `BFR.Shell`, và chuẩn hóa `WinRT.Interop.InitializeWithWindow` cho .NET 8 trong `BFR.Store`.
- **Gia cố bảo mật toàn diện.** Mã hóa `license.json` cục bộ bằng Windows DPAPI (tự động chuyển đổi mượt từ v2.1), bổ sung xác thực chữ ký bất đối xứng ECDSA P-256, chặn vượt rào `ProtectedZones` qua tiền tố `\\?\` và NTFS junction/symlink, chống CSV Formula Injection, gia cố bộ giải nén ZIP & giới hạn kích thước danh sách tệp, và kiểm tra chuyển hướng URL cập nhật.
